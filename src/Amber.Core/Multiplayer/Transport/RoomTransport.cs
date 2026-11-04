using System.Net;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>“仅游戏”转发：经房间里另一台电脑的网络进服务器、上网（只影响游戏进程）。</summary>
public sealed record GameProxy(int SocksPort, string ExitIp, Func<CancellationToken, Task<Stream>> Open)
{
    /// <summary>给游戏的 Java 加的参数：游戏里的 HTTP（登录、皮肤、会话校验）走本机的 SOCKS5 转发。</summary>
    public IReadOnlyList<string> JvmArgs => [$"-DsocksProxyHost=127.0.0.1", $"-DsocksProxyPort={SocksPort}"];

    /// <summary>
    /// 为要进的服务器开一个本机转发端口：游戏连 127.0.0.1:端口，Amber 经出口连到真正的服务器（并把握手包里的地址改回去）。
    /// Minecraft 连服务器用的 Netty 不认 Java 的 SOCKS 设置，所以服务器连接要这样单独转。
    /// </summary>
    public TunnelClient RelayTo(string host, int port, Action<string>? log = null) =>
        new(0, Open, log, McHandshakeRewriter.ViaSocks(host, port));
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
    public TunnelClient? Socks { get; private set; }
    public GameProxy? Proxy { get; private set; }

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

        if (o.AllowProxy)
        {
            // 别人借我上网：不许连我自己的电脑、路由器和家里的设备。公网 IPv4 用 STUN 在后台查一次。
            _exitPolicy = new ExitPolicy();
            _ = _exitPolicy.DiscoverPublicAddressAsync();
        }
        if (o.IsHost && settings.Wraps || o.AllowProxy)
        {
            Server = Add(new TunnelServer(room, settings, settings.Wraps ? TunnelSettings.Port : TunnelSettings.PlainSocksPort, service => service switch
            {
                TunnelService.Game when o.IsHost && settings.Wraps => TunnelServer.ToLocalPort(room.Port),
                TunnelService.Chat when o.IsHost && settings.Wraps => TunnelServer.ToLocalPort(RoomChat.Port),
                TunnelService.Socks when o.AllowProxy => (stream, ct) => Socks5.ServeAsync(stream, ct, _log, _exitPolicy!.Allows),
                _ => null,
            }, _log, listen));
        }

        if (!o.IsHost && settings.Wraps)
        {
            var (host, port) = o.Tun ? (EasyTierArgs.HostIp, TunnelSettings.Port) : ("127.0.0.1", o.LocalTunnelPort ?? throw new ArgumentException("加入方需要封装通道的本机转发端口"));
            if (o.LocalForwardPort is { } game) Game = Add(new TunnelClient(game, TunnelClient.Wrapped(host, port, room, settings, TunnelService.Game), _log));
            if (o.LocalChatPort is { } chat) Chat = Add(new TunnelClient(chat, TunnelClient.Wrapped(host, port, room, settings, TunnelService.Chat), _log));
        }

        if (o.ProxyExit is { } exit && o.LocalSocksPort is { } socks)
        {
            var exitPort = settings.Wraps ? TunnelSettings.Port : TunnelSettings.PlainSocksPort;
            var (host, port) = o.Tun ? (exit, exitPort) : ("127.0.0.1", o.LocalExitPort ?? throw new ArgumentException("转发需要出口的本机转发端口"));
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
