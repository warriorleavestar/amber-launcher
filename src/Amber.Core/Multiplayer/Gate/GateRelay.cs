using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 节点（中继）：把收到的 TCP 原样转给固定的源站网关。不持有任何密钥，看到的只有端到端加密的字节，
/// 也不能当开放代理用（目标写死）。VPS 和局域网电脑上都能跑。
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

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Active;
    public long Forwarded;

    public GateRelay(string listenAddress, int listenPort, string originHost, int originPort, int maxConnections = 512, int maxPerIp = 32, Action<string>? log = null)
    {
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
        _cts.Dispose();
    }
}
