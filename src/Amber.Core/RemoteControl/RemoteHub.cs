using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.RemoteControl;

/// <summary>房主请求看/控一个成员的结果。</summary>
public enum RemoteRequestFailure
{
    None,
    /// <summary>对方不在线（聊天通道没连着，请求发不出去）。</summary>
    Offline,
    Declined,
    TimedOut,
    /// <summary>已经有一次在进行（同一个人的请求还没结束，或对方正在被别人控制）。</summary>
    Busy,
    /// <summary>对方这台电脑不支持（截不了屏）。</summary>
    Unsupported,
    /// <summary>这个房间不是邀请房间。</summary>
    NotInviteRoom,
}

public sealed record RemoteRequestResult(RemoteViewer? Viewer, RemoteRequestFailure Failure);

/// <summary>房主这边被控时用到的东西：同意策略、截屏和注入（按需新建，结束时释放）、编码。</summary>
public sealed record RemoteHostOptions(IRemoteConsentPolicy Consent, Func<IScreenSource?> Screen, Func<IInputSink?> Input, ITileCodec Codec);

/// <summary>
/// 房主电脑上的远程控制服务（端口 47662，只在房间网络里能连到）。所有远程控制连接都由成员发起：
/// 先明文报成员编号，再用这个成员自己的 psk 握手（MemberChannel，用途 remote-v1，参数跟聊天一样随房间模式）。
/// 握手载荷说明对方是哪一方：
/// · 控制方：成员要看/控房主。房主这边弹同意窗（名字和指纹来自成员表），同意后共享屏幕。
/// · 被控方：房主之前经这个成员的聊天通道发过请求（带随机会话号），成员同意后连回来；会话号对不上、不是同一个成员都断开。
/// 每条连接挂在成员钥匙的作废上：踢出、换钥匙、房间结束都立刻断开。只支持邀请房间。
/// </summary>
public sealed class RemoteHub : IAsyncDisposable
{
    public const int Port = 47662;

    /// <summary>同时最多这么多条还在握手、等同意的连接。</summary>
    public const int MaxPending = 8;

    /// <summary>房主请求后等成员同意并连回来的时间（成员那边 30 秒自动拒绝，再留一点连接时间）。</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>被拒绝后这个成员多久内再请求直接回“忙”（防止反复弹窗）。</summary>
    internal TimeSpan DeclineCooldown { get; init; } = TimeSpan.FromSeconds(10);

    private readonly MemberTable _table;
    private readonly TransportProfile _profile;
    private readonly RemoteHostOptions _options;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RemoteEndpoint, byte> _sessions = new();
    private int _pending;
    private int _prompting;
    private RemoteSharer? _sharing;

    private sealed record PendingRequest(MemberEntry Member, TaskCompletionSource<Stream?> Connection);

    /// <summary>房主开始被某个成员看/控（在接受连接的线程上触发）。</summary>
    public event Action<RemoteSharer, MemberEntry>? SharingStarted;

    public RemoteHub(RoomCode room, RemoteHostOptions options, int port = Port, IEnumerable<IPAddress>? extraAddresses = null)
    {
        if (!room.FromInvite || room.Members is not { } table) throw new InvalidOperationException("remote control is only for invite rooms");
        _table = table;
        _profile = MemberChannel.ChatProfile(room.Tunnel.Mode);
        _options = options;
        foreach (var address in new[] { IPAddress.Loopback }.Concat(extraAddresses ?? []))
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            _listeners.Add(listener);
            _loops.Add(AcceptLoopAsync(listener));
        }
    }

    public int ListenPort => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;

    /// <summary>正在共享房主屏幕的会话（没有时 null）。</summary>
    public RemoteSharer? Sharing => Volatile.Read(ref _sharing);

    /// <summary>完整局域网模式下虚拟网卡就绪后加上虚拟 IP。</summary>
    public bool TryAddListener(IPAddress address)
    {
        try
        {
            var listener = new TcpListener(address, ListenPort);
            listener.Start();
            lock (_listeners) _listeners.Add(listener);
            var loop = AcceptLoopAsync(listener);
            lock (_loops) _loops.Add(loop);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            if (Interlocked.Increment(ref _pending) > MaxPending)
            {
                Interlocked.Decrement(ref _pending);
                client.Dispose();
                continue;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var pendingReleased = false;
        void ReleasePending()
        {
            if (!pendingReleased) Interlocked.Decrement(ref _pending);
            pendingReleased = true;
        }
        SecureStream? secure = null;
        var handedOff = false;
        try
        {
            client.NoDelay = true;
            var role = RemoteRole.Controller;
            byte[] sid = [];
            var (stream, member, _) = await MemberChannel.AcceptAsync(client.GetStream(), _table, _profile, RemoteWire.Purpose,
                p => RemoteWire.TryParseHelloPayload(p, out role, out sid), _cts.Token);
            secure = stream;
            // 房主自己的那把钥匙不能用来连自己。
            if (member.IsHost || member.Revoked) throw new CryptographicException("not a guest");
            using var revoked = member.Revocation.Register(stream.Abort);
            if (role == RemoteRole.Controlled)
            {
                ReleasePending();
                var key = Convert.ToHexStringLower(sid);
                if (!_requests.TryRemove(key, out var request) || !ReferenceEquals(request.Member, member) || !request.Connection.TrySetResult(secure))
                    throw new CryptographicException("unknown remote session");
                handedOff = true; // 交给请求方了
                return;
            }
            await ServeControllerAsync(secure, member, ReleasePending);
        }
        catch (Exception e) when (RemoteEndpoint.IsConnectionError(e) || e is EndOfStreamException or InvalidDataException)
        {
        }
        finally
        {
            ReleasePending();
            if (!handedOff)
            {
                secure?.Abort();
                client.Dispose();
            }
        }
    }

    /// <summary>成员要看/控房主：问房主同意，同意后共享屏幕直到结束。</summary>
    private async Task ServeControllerAsync(SecureStream stream, MemberEntry member, Action releasePending)
    {
        var name = ChatWire.NameOf(member);
        // 先读完控制方的 Hello（版本不对就不弹窗）；不读的话拒绝后关连接时未读的数据会让对方收到重置、看不到“拒绝”。
        if (!await RemoteSharer.ReadHelloAsync(stream, _cts.Token)) return;
        if (Sharing is not null || Interlocked.Exchange(ref _prompting, 1) != 0)
        {
            await DenyAsync(stream, RemoteDenyReason.Busy);
            return;
        }
        RemoteConsent consent;
        try
        {
            if (_cooldown.TryGetValue(member.Id, out var until) && DateTimeOffset.UtcNow < until)
            {
                await DenyAsync(stream, RemoteDenyReason.Busy);
                return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, member.Revocation);
            consent = await _options.Consent.DecideAsync(new RemoteConsentRequest(name, member.Fingerprint), linked.Token);
        }
        finally
        {
            Volatile.Write(ref _prompting, 0);
        }
        releasePending();
        if (!consent.Accepted || member.Revoked || _cts.IsCancellationRequested)
        {
            _cooldown[member.Id] = DateTimeOffset.UtcNow + DeclineCooldown;
            await DenyAsync(stream, RemoteDenyReason.Declined);
            return;
        }
        var screen = _options.Screen();
        if (screen is null)
        {
            await DenyAsync(stream, RemoteDenyReason.Unsupported);
            return;
        }
        var input = consent.ViewOnly ? null : _options.Input();
        var sharer = new RemoteSharer(stream, screen, input, _options.Codec, !consent.ViewOnly, name, member.Fingerprint) { HelloAlreadyRead = true };
        if (Interlocked.CompareExchange(ref _sharing, sharer, null) is not null)
        {
            screen.Dispose();
            input?.Dispose();
            await DenyAsync(stream, RemoteDenyReason.Busy);
            return;
        }
        _sessions[sharer] = 0;
        sharer.EndWith(member.Revocation);
        sharer.EndWith(_cts.Token);
        try
        {
            SharingStarted?.Invoke(sharer, member);
            await sharer.RunAsync();
        }
        finally
        {
            sharer.Abort(RemoteEnd.Disconnected);
            _sessions.TryRemove(sharer, out _);
            Interlocked.CompareExchange(ref _sharing, null, sharer);
            screen.Dispose();
            input?.Dispose();
        }
    }

    private static async Task DenyAsync(SecureStream stream, RemoteDenyReason reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await stream.WriteAsync(RemoteWire.Deny(reason), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            await stream.DisposeAsync();
        }
        catch (Exception e) when (RemoteEndpoint.IsConnectionError(e))
        {
            stream.Abort();
        }
    }

    /// <summary>
    /// 房主要看/控一个成员：生成随机会话号，经 send（这个成员的聊天通道）发请求，等他同意并连回来，然后开始查看。
    /// 同一个成员同时只有一个请求。
    /// </summary>
    public async Task<RemoteRequestResult> RequestAsync(MemberEntry member, Func<string, bool> send, CancellationToken ct = default)
    {
        if (member.IsHost || member.Revoked) return new(null, RemoteRequestFailure.Offline);
        if (_requests.Values.Any(r => ReferenceEquals(r.Member, member))) return new(null, RemoteRequestFailure.Busy);
        var sid = Convert.ToHexStringLower(RemoteWire.NewSessionId());
        var request = new PendingRequest(member, new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _requests[sid] = request;
        try
        {
            if (!send(sid)) return new(null, RemoteRequestFailure.Offline);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, member.Revocation);
            linked.CancelAfter(RequestTimeout);
            Stream? stream;
            try { stream = await request.Connection.Task.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { return new(null, member.Revoked ? RemoteRequestFailure.Offline : RemoteRequestFailure.TimedOut); }
            if (stream is null) return new(null, RemoteRequestFailure.Declined);
            var viewer = new RemoteViewer(stream, _options.Codec, ChatWire.NameOf(member), member.Fingerprint);
            viewer.EndWith(member.Revocation);
            viewer.EndWith(_cts.Token);
            _sessions[viewer] = 0;
            viewer.Ended += _ => _sessions.TryRemove(viewer, out byte _);
            var accept = await viewer.StartAsync(ct);
            return accept is null
                ? new(null, viewer.DenyReason switch
                {
                    RemoteDenyReason.Busy => RemoteRequestFailure.Busy,
                    RemoteDenyReason.Unsupported => RemoteRequestFailure.Unsupported,
                    RemoteDenyReason.TimedOut => RemoteRequestFailure.TimedOut,
                    _ => RemoteRequestFailure.Declined,
                })
                : new(viewer, RemoteRequestFailure.None);
        }
        finally
        {
            _requests.TryRemove(sid, out _);
        }
    }

    /// <summary>成员经聊天通道回了“拒绝”（只认这个成员自己的请求）。</summary>
    public void Decline(MemberEntry member, string sessionId)
    {
        if (_requests.TryGetValue(sessionId, out var request) && ReferenceEquals(request.Member, member)) request.Connection.TrySetResult(null);
    }

    /// <summary>停掉所有会话（换网络时）。</summary>
    public void StopAll()
    {
        foreach (var s in _sessions.Keys) s.Abort(RemoteEnd.Disconnected);
        foreach (var r in _requests.Values) r.Connection.TrySetCanceled();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        lock (_listeners) foreach (var l in _listeners) l.Stop();
        StopAll();
        Task[] loops;
        lock (_loops) loops = [.. _loops];
        try { await Task.WhenAll(loops); }
        catch (Exception e) when (e is OperationCanceledException or SocketException) { }
        _cts.Dispose();
    }
}

/// <summary>成员这边：连房主的远程控制服务。</summary>
public static class RemoteConnector
{
    /// <summary>
    /// 连上并握手（成员钥匙、用途 remote-v1、聊天同样的参数）。role 是控制方时 sessionId 为空；被控方时是房主发来的会话号。
    /// </summary>
    public static async Task<SecureStream> OpenAsync(string host, int port, RoomCode room, RemoteRole role, string? sessionId, CancellationToken ct = default)
    {
        if (!room.FromInvite || room.Member is not { } key) throw new InvalidOperationException("remote control is only for invite rooms");
        byte[] sid = role == RemoteRole.Controlled
            ? (sessionId is { Length: RemoteWire.SessionIdLength * 2 } ? Convert.FromHexString(sessionId) : throw new ArgumentException(null, nameof(sessionId)))
            : [];
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct);
            return await MemberChannel.ConnectAsync(client.GetStream(), key, MemberChannel.ChatProfile(room.Tunnel.Mode), RemoteWire.Purpose,
                RemoteWire.HelloPayload(role, sid), ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>会话号格式对不对（32 位十六进制小写）。</summary>
    public static bool IsSessionId(string? text) =>
        text is { Length: RemoteWire.SessionIdLength * 2 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
