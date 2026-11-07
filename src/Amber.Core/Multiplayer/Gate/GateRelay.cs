using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 节点（中继）：把收到的 TCP 原样转给固定的源站网关。不持有任何密钥，看到的只有端到端加密的字节，
/// 也不能当开放代理用（目标写死）。VPS 和局域网电脑上都能跑。
/// 隐私++：带上节点码时，节点还会和网关保持一条心跳通道（Noise XXpsk0，用节点自己的长期密钥认证），每隔几秒发一条带序号和时间的心跳。
/// </summary>
public sealed class GateRelay : IAsyncDisposable
{
    private readonly string _originHost;
    private readonly int _originPort;
    private readonly int _maxPerIp;
    private readonly Action<string>? _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private Task? _loop;
    private Task? _heartbeatLoop;
    private readonly GateRelayCode? _code;
    private readonly X25519KeyPair? _key;
    private readonly TimeSpan _heartbeatInterval;

    /// <summary>心跳通道现在是不是连着。</summary>
    public bool HeartbeatConnected { get; private set; }
    public long HeartbeatsSent;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Active;
    public long Forwarded;

    /// <param name="code">节点码（隐私++ 的心跳）；不给就只转发。</param>
    /// <param name="key">节点自己的长期密钥（给了节点码就必须给）。</param>
    public GateRelay(string listenAddress, int listenPort, string originHost, int originPort, int maxConnections = 512, int maxPerIp = 32, Action<string>? log = null,
        GateRelayCode? code = null, X25519KeyPair? key = null, TimeSpan? heartbeatInterval = null)
    {
        if (code is not null && key is null) throw new ArgumentNullException(nameof(key));
        _code = code;
        _key = key;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
        _originHost = originHost;
        _originPort = originPort;
        _maxPerIp = maxPerIp;
        _log = log;
        _slots = new SemaphoreSlim(maxConnections);
        var address = IPAddress.Parse(listenAddress);
        try
        {
            _listener = new TcpListener(address, listenPort);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = true;
        }
        catch (SocketException) when (address.Equals(IPAddress.IPv6Any))
        {
            _listener = new TcpListener(IPAddress.Any, listenPort);
        }
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        if (_code is not null) _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
    }

    /// <summary>心跳：连上网关、认证、每隔一会儿发一条。断了就退避重连（1 秒起，最多 30 秒）。</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        ulong counter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await tcp.ConnectAsync(_originHost, _originPort, timeout.Token);
                }
                await using var channel = await GateProtocol.RelayConnectAsync(new OwnedStream(tcp), _code!, _key!, ct);
                HeartbeatConnected = true;
                backoff = TimeSpan.FromSeconds(1);
                _log?.Invoke(L.T("core.gate.relay.heartbeat_up"));
                while (!ct.IsCancellationRequested)
                {
                    await GateProtocol.WriteHeartbeatAsync(channel, ++counter, ct);
                    Interlocked.Increment(ref HeartbeatsSent);
                    await Task.Delay(_heartbeatInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or IOException or CryptographicException or GateRejectedException or OperationCanceledException or ObjectDisposedException)
            {
                if (HeartbeatConnected || backoff == TimeSpan.FromSeconds(1)) _log?.Invoke(L.F("core.gate.relay.heartbeat_down", e.Message));
            }
            HeartbeatConnected = false;
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    /// <summary>节点的长期密钥（relay.key）：没有就生成一个。文件只给当前用户读写。</summary>
    public static X25519KeyPair LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var raw = File.ReadAllBytes(path);
            try { return X25519KeyPair.FromPrivateKey(raw); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var key = X25519KeyPair.Generate();
        var bytes = key.ExportPrivateKey();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            stream.Write(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        return key;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6() ?? IPAddress.IPv6None;
            if (_perIp.AddOrUpdate(remote, 1, (_, n) => n + 1) > _maxPerIp || !_slots.Wait(0))
            {
                Release(remote);
                Pump.Abort(client);
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await ForwardAsync(client, ct); }
                finally
                {
                    _slots.Release();
                    Release(remote);
                }
            }, CancellationToken.None);
        }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var origin = new TcpClient())
        {
            Pump.Tune(client);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await origin.ConnectAsync(_originHost, _originPort, timeout.Token);
                }
                Pump.Tune(origin);
                Interlocked.Increment(ref Active);
                try
                {
                    var (up, down) = await Pump.RunAsync(client.GetStream(), origin.GetStream(), ct);
                    Interlocked.Add(ref Forwarded, up + down);
                }
                finally
                {
                    Interlocked.Decrement(ref Active);
                }
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
            {
                // 源站连不上：直接重置玩家这头，不回任何字节（和网关一样不暴露这里有服务）。
                Pump.Abort(client);
                _log?.Invoke(e.Message);
            }
        }
    }

    private void Release(IPAddress remote)
    {
        if (_perIp.AddOrUpdate(remote, 0, (_, n) => n - 1) <= 0) _perIp.TryRemove(new KeyValuePair<IPAddress, int>(remote, 0));
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
        if (_heartbeatLoop is not null)
            try { await _heartbeatLoop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
