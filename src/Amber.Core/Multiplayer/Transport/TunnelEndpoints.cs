using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>房间的封装设置：模式、是否伪装成 MC。写在房间码里，两边一样。</summary>
public sealed record TunnelSettings(TransportMode Mode, bool Disguise)
{
    public static readonly TunnelSettings Default = new(TransportMode.General, false);
    public bool Wraps => Mode != TransportMode.General;

    /// <summary>封装通道在每个成员虚拟地址上用的端口（房主：游戏和聊天；出口成员：转发）。</summary>
    /// <remarks>47652 是聊天，47653 是直连房间的测速，47654 是通用模式的 SOCKS5。</remarks>
    public const int Port = 47656;
    /// <summary>通用模式下出口成员的 SOCKS5 端口（不封装，只靠 EasyTier 加密）。</summary>
    public const int PlainSocksPort = 47654;
}

/// <summary>两个流互相转发，任何一边结束就都关掉。返回两个方向各转了多少字节。</summary>
public static class Pump
{
    /// <param name="abortLocal">封装通道没有正常结束（被截断、认证失败）时调用：把本机那头的连接直接重置，而不是像正常结束那样关闭，
    /// 免得游戏把半截数据当成完整的。</param>
    public static async Task<(long Up, long Down)> RunAsync(Stream a, Stream b, CancellationToken ct = default, Action? abortLocal = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = CopyAsync(a, b, linked.Token);
        var down = CopyAsync(b, a, linked.Token);
        var first = await Task.WhenAny(up, down);
        var secure = a as SecureStream ?? b as SecureStream;
        // 任何一个方向出错结束（本机连接读写出错、封装通道被截断）：封装通道按出错处理，不发“正常结束”。
        // 只有从本机那头正常读到结尾时才算正常结束。
        if (first.Result.Error) secure?.Abort();
        // 先记下是不是外面取消的：之后 2 秒的收尾期间外面才取消（正常结束之后），不算。
        var cancelledFromOutside = ct.IsCancellationRequested;
        linked.Cancel();
        try { await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        // 另一个方向收尾时才出错（比如本机连接在取消前一刻被重置）也一样。只看从本机读的那个方向：
        // 从封装通道读的那个方向被我们取消时也可能报出错，那不代表连接坏了。
        var fromLocal = secure is null ? null : ReferenceEquals(secure, a) ? down : up;
        if (fromLocal is { IsCompletedSuccessfully: true, Result.Error: true }) secure!.Abort();
        // 是外面取消（联机结束、程序退出）让转发停下的：数据流并没有正常结束，对面不能当成“正常关闭”。
        if (cancelledFromOutside) secure?.Abort();
        // 不管哪边先结束，只要封装通道没收到认证过的结束记录，就把本机那头重置，而不是正常关闭。
        if (secure is { ClosedCleanly: false }) abortLocal?.Invoke();
        await a.DisposeAsync();
        await b.DisposeAsync();
        long Bytes(Task<(long Total, bool Error)> t) => t.IsCompletedSuccessfully ? t.Result.Total : 0;
        return (Bytes(up), Bytes(down));
    }

    /// <summary>把 from 的数据转给 to，返回转了多少字节、是不是出错结束的（正常读到结尾不算出错）。</summary>
    private static async Task<(long Total, bool Error)> CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        long total = 0;
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                await to.FlushAsync(ct);
                total += n;
            }
            return (total, false);
        }
        catch (OperationCanceledException)
        {
            return (total, false);
        }
        catch (Exception)
        {
            // 不认识的异常也算出错结束：宁可当成截断，也不发“正常结束”。
            return (total, true);
        }
    }

    /// <summary>关闭时直接发 RST（不经过正常的四次挥手），对面马上知道连接是出错断的。</summary>
    public static void Abort(TcpClient client)
    {
        try { client.Client.LingerState = new LingerOption(true, 0); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    /// <summary>游戏用的 TCP：关掉 Nagle（MC 的包小而频繁，等着合并只会加延迟）。</summary>
    public static void Tune(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = Math.Max(client.ReceiveBufferSize, 256 * 1024);
        client.SendBufferSize = Math.Max(client.SendBufferSize, 256 * 1024);
    }
}

/// <summary>
/// 房主 / 出口成员这边：在本机端口上接受封装通道，按握手里请求的服务把数据交给本机的游戏、聊天或者 SOCKS5 转发。
/// EasyTier 不建网卡时会把发往自己虚拟地址的连接交给本机同一端口，所以只听 127.0.0.1。
/// </summary>
public sealed class TunnelServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly RoomCode _room;
    private readonly TunnelSettings _settings;
    private readonly Func<TunnelService, Func<Stream, CancellationToken, Task>?> _route;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Action<string>? _log;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Accepted;
    public int Rejected;

    /// <param name="route">每种服务怎么处理；返回 null 表示不提供这个服务（连接会被关掉）。</param>
    public TunnelServer(RoomCode room, TunnelSettings settings, int port, Func<TunnelService, Func<Stream, CancellationToken, Task>?> route, Action<string>? log = null, IPAddress? address = null)
    {
        _room = room;
        _settings = settings;
        _route = route;
        _log = log;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _listener.Start();
        // 在线程池上跑：不要带上界面线程的同步上下文（否则每条连接的收尾都要排队等界面线程）。
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>把服务交给本机的一个 TCP 端口（游戏、聊天）。</summary>
    public static Func<Stream, CancellationToken, Task> ToLocalPort(int port) => async (stream, ct) =>
    {
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, port, ct);
        Pump.Tune(target);
        await Pump.RunAsync(stream, target.GetStream(), ct, () => Pump.Abort(target));
    };

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            Pump.Tune(client);
            Stream stream = client.GetStream();
            try
            {
                TunnelService service;
                if (_settings.Wraps)
                {
                    var accepted = await TunnelHandshake.AcceptAsync(stream, _room, _settings.Mode, _settings.Disguise, ct);
                    if (accepted is null) return;
                    (stream, service) = accepted.Value;
                }
                else service = TunnelService.Socks;
                var handler = _route(service);
                if (handler is null)
                {
                    _log?.Invoke(L.F("core.mp.transport.endpoint_service_unavailable", service));
                    Interlocked.Increment(ref Rejected);
                    return;
                }
                Interlocked.Increment(ref Accepted);
                await handler(stream, ct);
            }
            catch (Exception e) when (e is CryptographicException or IOException or SocketException or EndOfStreamException or OperationCanceledException or PlatformNotSupportedException or InvalidDataException or ArgumentException)
            {
                // 出错断开：不给对面发“正常结束”。
                (stream as SecureStream)?.Abort();
                Interlocked.Increment(ref Rejected);
                if (e is CryptographicException or PlatformNotSupportedException) _log?.Invoke(L.T("core.mp.transport.endpoint_handshake_failed") + e.Message);
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

/// <summary>
/// 加入方这边：在本机端口上等游戏（或聊天）连进来，每条连接经封装通道连到房主 / 出口成员。
/// 通用模式下不封装，直接连过去（只靠 EasyTier 的加密）。
/// </summary>
public sealed class TunnelClient : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly Func<Stream, Stream, CancellationToken, Task<Stream>>? _prepare;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Action<string>? _log;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public long BytesUp;
    public long BytesDown;
    public int Connections;
    public int Failures;

    /// <param name="open">打开一条到对面的已封装的流。</param>
    /// <param name="prepare">连上之后、开始转发之前的处理（比如 SOCKS5 CONNECT、改写 MC 握手包），返回之后用来转发的“游戏这边”的流。</param>
    public TunnelClient(int localPort, Func<CancellationToken, Task<Stream>> open, Action<string>? log = null,
        Func<Stream, Stream, CancellationToken, Task<Stream>>? prepare = null)
    {
        _open = open;
        _prepare = prepare;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, localPort);
        _listener.Start();
        // 在线程池上跑：不要带上界面线程的同步上下文（否则每条连接的收尾都要排队等界面线程）。
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>经 EasyTier 连到 host:port，再按房间设置做封装握手。</summary>
    public static Func<CancellationToken, Task<Stream>> Wrapped(string host, int port, RoomCode room, TunnelSettings settings, TunnelService service) => async ct =>
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            Pump.Tune(tcp);
            Stream stream = new OwnedStream(tcp);
            return settings.Wraps ? await TunnelHandshake.ConnectAsync(stream, room, settings.Mode, settings.Disguise, service, ct) : stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    };

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient game;
            try
            {
                game = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(game, ct);
        }
    }

    private async Task HandleAsync(TcpClient game, CancellationToken ct)
    {
        using (game)
        {
            Pump.Tune(game);
            Interlocked.Increment(ref Connections);
            Stream? remote = null;
            try
            {
                remote = await _open(ct);
                Stream local = game.GetStream();
                if (_prepare is not null) local = await _prepare(local, remote, ct);
                var (up, down) = await Pump.RunAsync(remote, local, ct, () => Pump.Abort(game));
                Interlocked.Add(ref BytesUp, down);
                Interlocked.Add(ref BytesDown, up);
            }
            catch (Exception e) when (e is CryptographicException or IOException or SocketException or EndOfStreamException or OperationCanceledException or InvalidDataException or PlatformNotSupportedException or ArgumentException)
            {
                // 出错断开：封装通道不发“正常结束”，本机的游戏连接也直接重置。
                (remote as SecureStream)?.Abort();
                Pump.Abort(game);
                Interlocked.Increment(ref Failures);
                _log?.Invoke(L.T("core.mp.transport.endpoint_connect_failed") + e.Message);
            }
            finally
            {
                if (remote is not null) await remote.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

/// <summary>流关掉时把 TcpClient 也关掉。</summary>
internal sealed class OwnedStream(TcpClient client) : Stream
{
    private readonly NetworkStream _inner = client.GetStream();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            client.Dispose();
        }
        base.Dispose(disposing);
    }
}
