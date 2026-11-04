using Amber.Core.Localization;
using Amber.Core;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Multiplayer;

public enum ChatKind { Message, Join, Leave }

/// <summary>一条聊天消息（解密后）。Time 是发送方的时间。</summary>
public sealed record ChatMessage(ChatKind Kind, string SenderId, string Name, string Text, DateTimeOffset Time);

/// <summary>
/// 房间聊天的加密：密钥由房间密钥经 HKDF-SHA256 派生（盐是网络名），每条消息 AES-256-GCM、随机 12 字节 nonce，
/// 发送者 id 放进附加认证数据。即使 EasyTier 自己的加密没开，节点和其他不知道房间码的人也看不到内容、改不了消息；
/// 认证不过的消息直接丢弃。
/// </summary>
public sealed class ChatCipher
{
    private const string Info = "amber-room-chat-v1";
    private readonly byte[] _key;

    public ChatCipher(RoomCode room)
    {
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(room.Secret), 32,
            Encoding.UTF8.GetBytes(room.NetworkName), Encoding.UTF8.GetBytes(Info));
    }

    internal sealed record Envelope(
        [property: JsonPropertyName("s")] string Sender,
        [property: JsonPropertyName("n")] string Nonce,
        [property: JsonPropertyName("c")] string Cipher);

    internal sealed record Body(
        [property: JsonPropertyName("k")] ChatKind Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("ts")] long Time);

    public byte[] Seal(ChatMessage message)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Body(message.Kind, message.Name, message.Text, message.Time.ToUnixTimeMilliseconds()), AmberJsonContext.Default.Body);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(message.SenderId));
        var envelope = new Envelope(message.SenderId, Convert.ToBase64String(nonce), Convert.ToBase64String([.. cipher, .. tag]));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, AmberJsonContext.Default.Envelope);
    }

    /// <summary>解开并校验。格式不对、密钥不对、被改过都返回 null。</summary>
    public ChatMessage? Open(ReadOnlySpan<byte> frame)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(frame, AmberJsonContext.Default.Envelope);
            if (envelope is null || envelope.Sender.Length is 0 or > 64) return null;
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var data = Convert.FromBase64String(envelope.Cipher);
            if (nonce.Length != 12 || data.Length < 16) return null;
            var plain = new byte[data.Length - 16];
            using (var aes = new AesGcm(_key, 16)) aes.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(envelope.Sender));
            var body = JsonSerializer.Deserialize(plain, AmberJsonContext.Default.Body);
            if (body is null) return null;
            return new ChatMessage(body.Kind, envelope.Sender, RoomChat.Clean(body.Name, RoomChat.MaxNameLength), RoomChat.Clean(body.Text, RoomChat.MaxTextLength),
                DateTimeOffset.FromUnixTimeMilliseconds(body.Time));
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

public static class RoomChat
{
    /// <summary>房主电脑上聊天服务的端口（在房间的虚拟网络里访问）。</summary>
    public const int Port = 47652;
    public const int MaxFrameBytes = 8 * 1024;
    public const int MaxTextLength = 500;
    public const int MaxNameLength = 32;

    /// <summary>纯文本：去掉控制字符（保留换行），截断长度。界面只当文字显示，不解析链接或标记。</summary>
    public static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (builder.Length >= max) break;
            if (char.IsControl(c) && c != '\n') continue;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException(L.T("core.mp.chat.toolong"));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读一帧。对方关闭返回 null；长度超限抛异常（连接随后断开）。</summary>
    internal static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException(L.F("core.mp.chat.invalidlength", length));
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
}

/// <summary>
/// 简单的限速：每个发送者在时间窗口内最多 N 条。客户端和房主的转发都用。
/// </summary>
public sealed class RateLimiter(int count, TimeSpan window, Func<DateTimeOffset>? clock = null)
{
    private readonly Queue<DateTimeOffset> _times = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool TryAcquire()
    {
        var now = _clock();
        lock (_times)
        {
            while (_times.Count > 0 && now - _times.Peek() >= window) _times.Dequeue();
            if (_times.Count >= count) return false;
            _times.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// 房主电脑上的聊天转发：接受房间成员的连接，把认证通过的消息转给其他人。
/// 超长或认证失败的连接直接断开；发太快的消息丢弃。断开时替离开的人发一条“离开”通知。
/// </summary>
public sealed class ChatHub : IAsyncDisposable
{
    private readonly ChatCipher _cipher;
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentDictionary<Guid, Member> _members = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    private sealed class Member(TcpClient client)
    {
        public TcpClient Client { get; } = client;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public RateLimiter Limiter { get; } = new(5, TimeSpan.FromSeconds(5));
        public string? SenderId { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>监听在本机回环（房主自己和普通模式下 EasyTier 转发进来的连接）；完整局域网模式再加上虚拟 IP。</summary>
    public ChatHub(RoomCode room, int port, IEnumerable<IPAddress>? extraAddresses = null)
    {
        _cipher = new ChatCipher(room);
        foreach (var address in new[] { IPAddress.Loopback }.Concat(extraAddresses ?? []))
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            _listeners.Add(listener);
        }
        foreach (var listener in _listeners) _loops.Add(AcceptLoopAsync(listener));
    }

    /// <summary>再监听一个地址（完整局域网模式下虚拟网卡就绪后加上虚拟 IP）。地址还不存在时返回 false，稍后再试。</summary>
    public bool TryAddListener(IPAddress address)
    {
        try
        {
            var listener = new TcpListener(address, Port);
            listener.Start();
            lock (_listeners) _listeners.Add(listener);
            _loops.Add(AcceptLoopAsync(listener));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public int Port => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;
    public int MemberCount => _members.Count;

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            var id = Guid.NewGuid();
            var member = new Member(client);
            _members[id] = member;
            _ = ServeAsync(id, member);
        }
    }

    private async Task ServeAsync(Guid id, Member member)
    {
        try
        {
            var stream = member.Client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                var frame = await RoomChat.ReadFrameAsync(stream, _cts.Token);
                if (frame is null) break;
                var message = _cipher.Open(frame);
                if (message is null) break;
                // 一条连接只能代表一个人，换 id 就断开。
                if (member.SenderId is { } known && known != message.SenderId) break;
                member.SenderId = message.SenderId;
                if (message.Kind == ChatKind.Join) member.Name = message.Name;
                if (message.Kind == ChatKind.Leave) break;
                if (message.Kind == ChatKind.Message && !member.Limiter.TryAcquire()) continue;
                await BroadcastAsync(frame, except: null);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 连接断了或者发了不合规的数据，按离开处理。
        }
        finally
        {
            _members.TryRemove(id, out _);
            member.Client.Dispose();
            if (member.SenderId is { } sender && !_cts.IsCancellationRequested)
            {
                var leave = _cipher.Seal(new ChatMessage(ChatKind.Leave, sender, member.Name ?? "", "", DateTimeOffset.UtcNow));
                await BroadcastAsync(leave, except: null);
            }
        }
    }

    private async Task BroadcastAsync(byte[] frame, Guid? except)
    {
        foreach (var (id, member) in _members)
        {
            if (id == except) continue;
            await member.WriteLock.WaitAsync();
            try
            {
                await RoomChat.WriteFrameAsync(member.Client.GetStream(), frame, _cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // 写不进去的连接会在它自己的读循环里被清理。
            }
            finally
            {
                member.WriteLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        foreach (var m in _members.Values) m.Client.Dispose();
        try
        {
            await Task.WhenAll(_loops);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
        _cts.Dispose();
    }
}

/// <summary>房间成员一侧：连上房主的聊天转发，发消息、收消息。</summary>
public sealed class ChatClient : IAsyncDisposable
{
    private readonly ChatCipher _cipher;
    private readonly TcpClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly RateLimiter _limiter = new(5, TimeSpan.FromSeconds(5));
    private Task? _readLoop;

    public string SenderId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string Name { get; }
    public event Action<ChatMessage>? Received;
    public event Action? Disconnected;

    public ChatClient(RoomCode room, string name)
    {
        _cipher = new ChatCipher(room);
        Name = RoomChat.Clean(name, RoomChat.MaxNameLength) is { Length: > 0 } n ? n : L.T("core.mp.chat.player");
    }

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        await _client.ConnectAsync(host, port, ct);
        _readLoop = ReadLoopAsync();
        await SendRawAsync(new ChatMessage(ChatKind.Join, SenderId, Name, "", DateTimeOffset.UtcNow), ct);
    }

    /// <summary>发一条消息。太长会截断；发太快返回 false。</summary>
    public async Task<bool> SendAsync(string text, CancellationToken ct = default)
    {
        var clean = RoomChat.Clean(text, RoomChat.MaxTextLength);
        if (clean.Length == 0) return true;
        if (!_limiter.TryAcquire()) return false;
        await SendRawAsync(new ChatMessage(ChatKind.Message, SenderId, Name, clean, DateTimeOffset.UtcNow), ct);
        return true;
    }

    internal async Task SendRawAsync(ChatMessage message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await RoomChat.WriteFrameAsync(_client.GetStream(), _cipher.Seal(message), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>测试用：直接写原始帧。</summary>
    internal async Task SendFrameAsync(byte[] frame)
    {
        var stream = _client.GetStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(frame);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var stream = _client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                var frame = await RoomChat.ReadFrameAsync(stream, _cts.Token);
                if (frame is null) break;
                if (_cipher.Open(frame) is { } message) Received?.Invoke(message);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.Connected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendRawAsync(new ChatMessage(ChatKind.Leave, SenderId, Name, "", DateTimeOffset.UtcNow), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        }
        _cts.Cancel();
        _client.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
        _cts.Dispose();
    }
}
