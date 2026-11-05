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
using System.Threading.Channels;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Ping 是 0.5.9 加的心跳：转发只在新成员加入后先发一个 Ping 表示“我支持心跳”，客户端收到后才开始定时发。
/// 旧客户端解不开 Ping（不认识的类型）会直接忽略；旧转发从不发 Ping，新客户端也就不会对它发。
/// </summary>
public enum ChatKind { Message, Join, Leave, Ping }

/// <summary>一条聊天消息（解密后）。Time 是发送方的时间。Nonce 是这条消息加密时用的随机数（判断是不是自己发的）。</summary>
public sealed record ChatMessage(ChatKind Kind, string SenderId, string Name, string Text, DateTimeOffset Time)
{
    public string Nonce { get; init; } = "";
}

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

    public byte[] Seal(ChatMessage message) => Seal(message, out _);

    /// <summary>加密，同时给出这条消息的 nonce（Base64）。</summary>
    public byte[] Seal(ChatMessage message, out string nonceText)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Body(message.Kind, message.Name, message.Text, message.Time.ToUnixTimeMilliseconds()), AmberJsonContext.Default.Body);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(message.SenderId));
        nonceText = Convert.ToBase64String(nonce);
        var envelope = new Envelope(message.SenderId, nonceText, Convert.ToBase64String([.. cipher, .. tag]));
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
            if (body is null || !Enum.IsDefined(body.Kind)) return null;
            return new ChatMessage(body.Kind, envelope.Sender, RoomChat.Clean(body.Name, RoomChat.MaxNameLength), RoomChat.Clean(body.Text, RoomChat.MaxTextLength),
                DateTimeOffset.FromUnixTimeMilliseconds(body.Time)) { Nonce = envelope.Nonce };
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

    /// <summary>
    /// 开 TCP 保活：长时间不说话的成员不会因为空闲被断开，但对方真的掉线（断网、关机）时几分钟内能发现。
    /// 系统不支持这些选项时就算了，不影响聊天。
    /// </summary>
    internal static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException or ObjectDisposedException) { }
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
/// 一条连接的第一帧必须是认证通过的“加入”，之后才会收到别人的消息；发送者 id 和名字在这条连接里固定，
/// 已经有人在用的 id 不让别的连接用。每个成员有自己的发送队列：一时满了先丢帧，一直满着才断开它（慢的人不拖住别人）。
/// 第一帧有时限；加入之后不说话不会被断开。掉线靠两样发现：TCP 保活（直接连过来的），和心跳（经 EasyTier 端口转发连过来的，
/// 保活只能探到本机这一跳）：支持心跳的成员太久没有任何帧就断开；不支持心跳的旧版本成员只在连接满了时，按最久没动静的先腾位子。
/// 连接数有上限，还没加入的连接也有上限，并给房主自己的客户端留一个位子（单独的本机端口）。加入/离开通知和整个转发的消息量限速。
/// 超长或认证失败的连接直接断开；发太快、太长的帧丢弃。断开时替离开的人发一条“离开”通知（宣布过加入的才发）。
/// </summary>
public sealed class ChatHub : IAsyncDisposable
{
    /// <summary>同时最多这么多条连接（包括还没发第一帧的，也包括房主自己的）。</summary>
    public const int MaxConnections = 32;

    /// <summary>每个成员的发送队列能排多少帧。</summary>
    internal int OutboxFrames { get; init; } = 32;

    /// <summary>发送队列一直是满的超过这么久才断开（中间能发出去就重新计时）。</summary>
    internal TimeSpan BacklogKickAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>连上后多久内要发出第一帧（加入）。</summary>
    internal TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>同时最多这么多条还没加入的连接（房主自己的端口不算）。反复连上不说话的人占不满所有位子。</summary>
    public const int MaxPending = 8;

    /// <summary>整个转发每 10 秒最多宣布多少次加入（超过的不宣布，成员照常加入；宣布过加入的离开总会宣布）。</summary>
    internal int PresencePerWindow { get; init; } = 16;

    /// <summary>整个转发每 5 秒最多转发多少条消息（每人另有 5 秒 5 条）。</summary>
    internal int MessagesPerWindow { get; init; } = 40;

    /// <summary>转发的消息帧最大字节数（500 字的消息加密后约 2 KB；更长的只可能是自己拼的）。</summary>
    public const int MaxForwardFrame = 4096;

    /// <summary>支持心跳的成员这么久没有任何帧就断开。</summary>
    internal TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>连接满了时，不支持心跳的成员这么久没动静就可以被腾掉位子。</summary>
    internal TimeSpan IdleEvictAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>多久检查一次心跳超时。</summary>
    internal TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>转发自己发的心跳用的发送者 id（不会和成员的 16 位十六进制 id 冲突）。</summary>
    internal const string HubSender = "amber-hub";

    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>测试用：给接受的连接设发送缓冲区大小，模拟网络慢。</summary>
    internal int? SendBufferForTest { get; init; }

    private readonly ChatCipher _cipher;
    private readonly List<TcpListener> _listeners = [];
    private readonly ConcurrentDictionary<Guid, Member> _members = new();
    private readonly object _identityGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly TcpListener _hostListener = new(IPAddress.Loopback, 0);
    private RateLimiter? _presenceLimiter;
    private RateLimiter? _messageLimiter;
    private RateLimiter PresenceLimiter => _presenceLimiter ??= new(PresencePerWindow, TimeSpan.FromSeconds(10), Clock);
    private RateLimiter MessageLimiter => _messageLimiter ??= new(MessagesPerWindow, TimeSpan.FromSeconds(5), Clock);

    private sealed class Member(TcpClient client, int outboxFrames, bool isHost, CancellationToken hub)
    {
        public TcpClient Client { get; } = client;
        public bool IsHost { get; } = isHost;
        /// <summary>发送队列从什么时候开始一直是满的（UTC ticks，0 表示没满）。</summary>
        public long FullSince;
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(outboxFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(hub);
        public RateLimiter Limiter { get; } = new(5, TimeSpan.FromSeconds(5));
        public string? SenderId { get; set; }
        public string? Name { get; set; }
        public volatile bool Authenticated;
        /// <summary>最后一次收到任何帧（UTC ticks）。</summary>
        public long LastActivity;
        /// <summary>发过心跳（0.5.9 及以后的客户端）。</summary>
        public volatile bool PingCapable;
        /// <summary>加入被宣布过（离开时要宣布）。</summary>
        public bool JoinAnnounced;

        public void Kick()
        {
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            Client.Dispose();
        }
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
        _hostListener.Start();
        foreach (var listener in _listeners) _loops.Add(AcceptLoopAsync(listener, false));
        _loops.Add(AcceptLoopAsync(_hostListener, true));
    }

    private int _sweepStarted;

    /// <summary>第一个成员加入时才开始检查心跳（这时 init 设置的时间参数已经生效）。</summary>
    private void StartSweep()
    {
        if (Interlocked.Exchange(ref _sweepStarted, 1) != 0) return;
        var loop = SweepLoopAsync();
        lock (_loops) _loops.Add(loop);
    }

    /// <summary>定时断开心跳超时的成员（支持心跳、但太久没有任何帧：多半是经端口转发连着、人已经不在了）。</summary>
    private async Task SweepLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(SweepInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
            var now = Clock().UtcTicks;
            foreach (var member in _members.Values)
                if (member.PingCapable && now - Interlocked.Read(ref member.LastActivity) > PingTimeout.Ticks) member.Kick();
        }
    }

    /// <summary>连接满了：腾掉一个最久没动静、不支持心跳的成员（超过 IdleEvictAfter 才算）。腾了返回 true。</summary>
    private bool EvictIdle()
    {
        var now = Clock().UtcTicks;
        var victim = _members.Values
            .Where(m => !m.IsHost && m.Authenticated && !m.PingCapable && now - Interlocked.Read(ref m.LastActivity) > IdleEvictAfter.Ticks)
            .OrderBy(m => Interlocked.Read(ref m.LastActivity)).FirstOrDefault();
        victim?.Kick();
        return victim is not null;
    }

    /// <summary>房主自己的聊天客户端连这个本机端口：给它留着位子，房间连接满了或者新连接被限速时也能连上（同时只接一条）。</summary>
    public int HostPort => ((IPEndPoint)_hostListener.LocalEndpoint).Port;

    /// <summary>再监听一个地址（完整局域网模式下虚拟网卡就绪后加上虚拟 IP）。地址还不存在时返回 false，稍后再试。</summary>
    public bool TryAddListener(IPAddress address)
    {
        try
        {
            var listener = new TcpListener(address, Port);
            listener.Start();
            lock (_listeners) _listeners.Add(listener);
            var loop = AcceptLoopAsync(listener, false);
            lock (_loops) _loops.Add(loop);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public int Port => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;

    /// <summary>已经加入（发过认证通过的第一帧）的成员数。</summary>
    public int MemberCount => _members.Values.Count(m => m.Authenticated);

    /// <summary>所有连接数（包括还没加入的）。</summary>
    public int ConnectionCount => _members.Count;

    private async Task AcceptLoopAsync(TcpListener listener, bool host)
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
            // 房主端口同时只接一条；其他连接给房主留一个位子，还没加入的连接另有上限。满了时先试着腾掉一个久没动静的旧版本成员。
            var accept = host
                ? !_members.Values.Any(m => m.IsHost)
                : _members.Values.Count(m => !m.IsHost && !m.Authenticated) < MaxPending
                  && (_members.Values.Count(m => !m.IsHost) < MaxConnections - 1 || EvictIdle());
            if (!accept)
            {
                client.Dispose();
                continue;
            }
            if (SendBufferForTest is { } size) client.Client.SendBufferSize = size;
            RoomChat.EnableKeepAlive(client.Client);
            var id = Guid.NewGuid();
            var member = new Member(client, OutboxFrames, host, _cts.Token) { LastActivity = Clock().UtcTicks };
            _members[id] = member;
            _ = ServeAsync(id, member);
        }
    }

    private async Task ServeAsync(Guid id, Member member)
    {
        var writer = WriteLoopAsync(member);
        try
        {
            var stream = member.Client.GetStream();
            while (!member.Cts.IsCancellationRequested)
            {
                byte[]? frame;
                if (member.Authenticated)
                {
                    // 加入之后不按空闲断开：有人挂着不说话很正常。真掉线由 TCP 保活发现。
                    frame = await RoomChat.ReadFrameAsync(stream, member.Cts.Token);
                }
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(member.Cts.Token);
                    deadline.CancelAfter(FirstFrameTimeout);
                    frame = await RoomChat.ReadFrameAsync(stream, deadline.Token);
                }
                if (frame is null) break;
                var message = _cipher.Open(frame);
                if (message is null) break;
                Interlocked.Exchange(ref member.LastActivity, Clock().UtcTicks);
                if (!member.Authenticated)
                {
                    // 第一帧必须是加入；别的连接正在用这个 id 就不让进。
                    if (message.Kind != ChatKind.Join) break;
                    lock (_identityGate)
                    {
                        if (_members.Values.Any(m => m != member && m.Authenticated && m.SenderId == message.SenderId)) break;
                        member.SenderId = message.SenderId;
                        member.Name = message.Name;
                        member.Authenticated = true;
                    }
                    if (member.IsHost || PresenceLimiter.TryAcquire())
                    {
                        member.JoinAnnounced = true;
                        Broadcast(frame);
                    }
                    StartSweep();
                    // 告诉这个客户端：这个转发支持心跳（旧客户端解不开，直接忽略）。
                    member.Outbox.Writer.TryWrite(_cipher.Seal(new ChatMessage(ChatKind.Ping, HubSender, "", "", Clock())));
                    continue;
                }
                // 一条连接只能代表一个人：换 id、换名字、再发一次加入都断开。
                if (message.SenderId != member.SenderId || message.Name != member.Name || message.Kind == ChatKind.Join) break;
                if (message.Kind == ChatKind.Leave) break;
                if (message.Kind == ChatKind.Ping)
                {
                    member.PingCapable = true;
                    continue;
                }
                if (frame.Length > MaxForwardFrame || !member.Limiter.TryAcquire() || !MessageLimiter.TryAcquire()) continue;
                Broadcast(frame);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 连接断了、超时了或者发了不合规的数据，按离开处理。
        }
        finally
        {
            _members.TryRemove(id, out _);
            member.Outbox.Writer.TryComplete();
            member.Kick();
            try { await writer; } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            member.Cts.Dispose();
            if (member.Authenticated && member.JoinAnnounced && member.SenderId is { } sender && !_cts.IsCancellationRequested)
            {
                var leave = _cipher.Seal(new ChatMessage(ChatKind.Leave, sender, member.Name ?? "", "", DateTimeOffset.UtcNow));
                Broadcast(leave);
            }
        }
    }

    /// <summary>放进每个已加入成员的发送队列。队列满了先丢这一帧；一直满着超过 BacklogKickAfter 才断开他。</summary>
    private void Broadcast(byte[] frame)
    {
        var now = Clock().UtcTicks;
        foreach (var member in _members.Values)
        {
            if (!member.Authenticated) continue;
            if (member.Outbox.Writer.TryWrite(frame))
            {
                Interlocked.Exchange(ref member.FullSince, 0);
                continue;
            }
            var since = Interlocked.CompareExchange(ref member.FullSince, now, 0);
            if (since != 0 && now - since >= BacklogKickAfter.Ticks) member.Kick();
        }
    }

    private static async Task WriteLoopAsync(Member member)
    {
        try
        {
            var stream = member.Client.GetStream();
            await foreach (var frame in member.Outbox.Reader.ReadAllAsync(member.Cts.Token))
                await RoomChat.WriteFrameAsync(stream, frame, member.Cts.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            member.Kick();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        _hostListener.Stop();
        foreach (var m in _members.Values) m.Kick();
        Task[] loops;
        lock (_loops) loops = [.. _loops];
        try
        {
            await Task.WhenAll(loops);
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
    private readonly Queue<string> _sentNonces = new();
    private readonly HashSet<string> _unmatchedNonces = new(StringComparer.Ordinal);
    private Task? _readLoop;
    private Task? _pingLoop;

    /// <summary>转发表示支持心跳后，多久发一次。</summary>
    internal static TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(30);

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
        RoomChat.EnableKeepAlive(_client.Client);
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
            var frame = _cipher.Seal(message, out var nonce);
            lock (_sentNonces)
            {
                _sentNonces.Enqueue(nonce);
                _unmatchedNonces.Add(nonce);
                while (_sentNonces.Count > 256) _unmatchedNonces.Remove(_sentNonces.Dequeue());
            }
            await RoomChat.WriteFrameAsync(_client.GetStream(), frame, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 这条消息是不是自己发的：发送者 id 是自己的，并且 nonce 是自己加密时用的、还没被认领过。
    /// 只看 id 的话知道房间码的人能冒充；只看 nonce 的话别人把自己发过的帧原样重放（换上他的 id 也能过认证）也会被当成自己的。
    /// 每个 nonce 只认第一次。
    /// </summary>
    public bool IsMine(ChatMessage message)
    {
        if (message.SenderId != SenderId || message.Nonce.Length == 0) return false;
        lock (_sentNonces) return _unmatchedNonces.Remove(message.Nonce);
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
                if (_cipher.Open(frame) is not { } message) continue;
                if (message.Kind == ChatKind.Ping)
                {
                    // 转发支持心跳：开始定时发（只开一次）。心跳不交给界面。
                    _pingLoop ??= PingLoopAsync();
                    continue;
                }
                Received?.Invoke(message);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke();
    }

    private async Task PingLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, _cts.Token);
                await SendRawAsync(new ChatMessage(ChatKind.Ping, SenderId, Name, "", DateTimeOffset.UtcNow), _cts.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or SocketException) { }
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
        if (_pingLoop is not null) await _pingLoop;
        _cts.Dispose();
    }
}
