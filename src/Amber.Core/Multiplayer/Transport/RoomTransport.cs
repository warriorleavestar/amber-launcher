using Amber.Core.Localization;
using System.Net;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>“仅游戏”转发：经房间里另一台电脑的网络进服务器、上网（只影响游戏进程）。</summary>
public sealed record GameProxy(int SocksPort, string ExitIp, Func<CancellationToken, Task<Stream>> Open)
{
    /// <summary>给游戏的 Java 加的参数：游戏里的 HTTP（登录、皮肤、会话校验）走本机的 SOCKS5 转发。</summary>
    /// <remarks>Java 会先在本机解析域名再经代理连（实测 SOCKS5 请求里是 IP），所以域名查询走的是你自己的网络，连接本身才经出口。</remarks>
    public IReadOnlyList<string> JvmArgs => [$"-DsocksProxyHost=127.0.0.1", $"-DsocksProxyPort={SocksPort}"];

    /// <summary>Minecraft 自己的代理参数：登录和会话校验用的 HTTP 请求明确走这个代理。</summary>
    public IReadOnlyList<string> GameArgs => ["--proxyHost", "127.0.0.1", "--proxyPort", SocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>
    /// 为要进的服务器开一个本机转发端口：游戏连 127.0.0.1:端口，Amber 经出口连到真正的服务器（并把握手包里的地址改回去）。
    /// Minecraft 连服务器用的 Netty 不认 Java 的 SOCKS 设置，所以服务器连接要这样单独转。
    /// </summary>
    public TunnelClient RelayTo(string host, int port, Action<string>? log = null) =>
        new(0, Open, log, McHandshakeRewriter.ViaSocks(host, port));

    public TunnelClient RelayTo(RelayPlan plan, Action<string>? log = null) =>
        new(0, Open, log, McHandshakeRewriter.ViaSocks(plan.ConnectHost, plan.ConnectPort, plan.HandshakeHost, plan.HandshakePort));
}

/// <summary>
/// 一次联机会话里封装通道的各个部分，跟着 EasyTier 会话一起开、一起关。
/// 房主：在封装端口上提供游戏和聊天（允许转发时也提供 SOCKS5）。
/// 加入方：本机游戏端口、聊天端口经封装通道连房主；开了“仅游戏”转发时另开一个本机 SOCKS5 端口经出口成员上网。
/// 允许别人经自己转发的成员：在封装端口上提供 SOCKS5。
/// </summary>
public sealed class RoomTransport : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _parts = [];
    private ExitPolicy? _exitPolicy;
    private readonly Action<string>? _log;

    public TunnelServer? Server { get; private set; }
    public TunnelClient? Game { get; private set; }
    public TunnelClient? Chat { get; private set; }
    public TunnelClient? Remote { get; private set; }
    public TunnelClient? Socks { get; private set; }
    public GameProxy? Proxy { get; private set; }

    /// <summary>允许别人经自己转发、却查不到自己公网 IP 时的说明（这时不转发）。</summary>
    public string? ExitProblem => _exitPolicy?.Failure;

    private RoomTransport(Action<string>? log) => _log = log;

    public static RoomTransport Start(EasyTierOptions o, Action<string>? log = null)
    {
        var t = new RoomTransport(log);
        try
        {
            t.StartCore(o);
            return t;
        }
        catch
        {
            t.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private void StartCore(EasyTierOptions o)
    {
        var room = o.Room;
        var settings = room.Tunnel;
        // 不建网卡时 EasyTier 把发往自己虚拟地址的连接交给本机同一端口；完整局域网模式下连接从虚拟网卡进来，听虚拟地址。
        var listen = o.Tun && IPAddress.TryParse(EasyTierArgs.VirtualIp(o), out var vip) ? vip : IPAddress.Loopback;

        // 邀请房间的封装通道用每个成员和房主之间的钥匙：成员之间没有共同的钥匙，所以只有房主能当“仅游戏”转发的出口。
        var memberOnlyHost = room.FromInvite && settings.Wraps;
        var allowProxy = o.AllowProxy && (o.IsHost || !memberOnlyHost);
        if (allowProxy)
        {
            // 别人借我上网：不许连我自己的电脑、路由器和家里的设备。公网 IPv4 用 STUN 在后台查一次。
            _exitPolicy = new ExitPolicy();
            _exitPolicy.Start(log: _log);
        }
        if (o.IsHost && settings.Wraps || allowProxy)
        {
            Server = Add(new TunnelServer(room, settings, settings.Wraps ? TunnelSettings.Port : TunnelSettings.PlainSocksPort, service => service switch
            {
                TunnelService.Game when o.IsHost && settings.Wraps => TunnelServer.ToLocalPort(room.Port),
                TunnelService.Chat when o.IsHost && settings.Wraps => TunnelServer.ToLocalPort(RoomChat.Port),
                TunnelService.Remote when o.IsHost && settings.Wraps && room.FromInvite => TunnelServer.ToLocalPort(RemoteControl.RemoteHub.Port),
                TunnelService.Socks when allowProxy => async (stream, ct) =>
                {
                    // 收到 CONNECT 请求时（不是连接刚开时）确认公网 IP：查到太久了（路由器可能重新拨号）就先重查（最多 8 秒）；
                    // 还是不行就回“出口还没准备好”，不去解析和连接。
                    var policy = _exitPolicy!;
                    await Socks5.ServeAsync(stream, ct, _log, policy.Allows, ensureReady: c => policy.EnsureFreshAsync(TimeSpan.FromSeconds(8), c),
                        stillReady: () => policy.CanForward);
                },
                _ => null,
            }, _log, listen));
        }

        if (!o.IsHost && settings.Wraps)
        {
            var (host, port) = o.Tun ? (EasyTierArgs.HostIp, TunnelSettings.Port) : ("127.0.0.1", o.LocalTunnelPort ?? throw new ArgumentException(L.T("core.mp.transport.join_port_required")));
            if (o.LocalForwardPort is { } game) Game = Add(new TunnelClient(game, TunnelClient.Wrapped(host, port, room, settings, TunnelService.Game), _log));
            if (o.LocalChatPort is { } chat) Chat = Add(new TunnelClient(chat, TunnelClient.Wrapped(host, port, room, settings, TunnelService.Chat), _log));
            if (o.LocalRemotePort is { } remote && room.FromInvite) Remote = Add(new TunnelClient(remote, TunnelClient.Wrapped(host, port, room, settings, TunnelService.Remote), _log));
        }

        if (o.ProxyExit is { } exit && o.LocalSocksPort is { } socks)
        {
            if (memberOnlyHost && exit != EasyTierArgs.HostIp) throw new ArgumentException(L.T("core.mp.transport.exit_host_only"));
            var exitPort = settings.Wraps ? TunnelSettings.Port : TunnelSettings.PlainSocksPort;
            var (host, port) = o.Tun ? (exit, exitPort) : ("127.0.0.1", o.LocalExitPort ?? throw new ArgumentException(L.T("core.mp.transport.exit_port_required")));
            var open = TunnelClient.Wrapped(host, port, room, settings, TunnelService.Socks);
            Socks = Add(new TunnelClient(socks, open, _log));
            Proxy = new GameProxy(Socks.Port, exit, open);
        }
    }

    private T Add<T>(T part) where T : IAsyncDisposable
    {
        _parts.Add(part);
        return part;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var part in Enumerable.Reverse(_parts))
        {
            try { await part.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        }
        _parts.Clear();
        _exitPolicy?.Dispose();
        _exitPolicy = null;
    }
}
