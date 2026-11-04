using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// “仅游戏”转发出口的目标地址规则：别人借你上网时，只能连公网上的别的机器，碰不到你自己的电脑、路由器和家里的设备。
/// 拒绝：
///   保留和私有地址（本机回环、私有网段、链路本地、CGNAT、组播 / 广播、ULA、文档和基准测试网段、EasyTier 虚拟网段）；
///   IPv6 里嵌着 IPv4 的各种写法（::ffff:a.b.c.d、::a.b.c.d、64:ff9b::/96、64:ff9b:1::/48、6to4 2002::/16、Teredo 2001::/32）——按里面的 IPv4 再判断一次；
///   本机所有网卡的地址（包括公网 IPv6 和临时地址）以及这些网卡所在的网段（家里其他设备的公网 IPv6 也在里面）；
///   路由器的公网 IPv4（经 STUN 查到的，以及端口映射报告的外部地址）。
/// 主机名在出口这边解析，按解析出的每个地址判断，连接也只连判断过的地址（不会被 DNS 重绑定绕过）。
/// 网卡地址变化时自动重新收集。
/// </summary>
public sealed class ExitPolicy : IDisposable
{
    private volatile IReadOnlyList<(IPAddress Network, int PrefixLength)> _localNetworks = [];
    private volatile IReadOnlySet<IPAddress> _localAddresses = new HashSet<IPAddress>();
    private readonly HashSet<IPAddress> _publicAddresses = [];
    private readonly Lock _lock = new();

    public ExitPolicy()
    {
        RefreshLocal();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    /// <summary>测试用：指定本机地址和网段，不读真实网卡。</summary>
    internal ExitPolicy(IEnumerable<IPAddress> localAddresses, IEnumerable<(IPAddress, int)> localNetworks)
    {
        _localAddresses = localAddresses.Select(Normalize).ToHashSet();
        _localNetworks = localNetworks.Select(n => (Normalize(n.Item1), n.Item2)).ToList();
    }

    private void OnAddressChanged(object? sender, EventArgs e) => RefreshLocal();

    /// <summary>本机所有网卡（包括停用的、虚拟的）的单播地址和所在网段。</summary>
    public void RefreshLocal()
    {
        var addresses = new HashSet<IPAddress>();
        var networks = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = Normalize(u.Address);
                    addresses.Add(ip);
                    var prefix = u.PrefixLength;
                    if (prefix is > 0 and < 128) networks.Add((ip, prefix));
                }
            }
        }
        catch (NetworkInformationException) { }
        _localAddresses = addresses;
        _localNetworks = networks;
    }

    /// <summary>加上从外面看到的本机公网地址（STUN、路由器端口映射报告的外部地址）。</summary>
    public void AddPublicAddress(IPAddress address)
    {
        lock (_lock) _publicAddresses.Add(Normalize(address));
    }

    /// <summary>用 STUN 查一次自己的公网 IPv4（查不到就算了，不影响其他规则）。</summary>
    public async Task DiscoverPublicAddressAsync(IReadOnlyList<string>? stunServers = null, CancellationToken ct = default)
    {
        foreach (var server in stunServers ?? Stun.DefaultServers)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                if (await Stun.QueryAsync(socket, server, TimeSpan.FromSeconds(3), ct) is { } mapped)
                {
                    AddPublicAddress(mapped.Address);
                    return;
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException) { }
        }
    }

    /// <summary>发邮件的端口（SMTP 25、465、587）：不许借别人的 IP 发垃圾邮件。其他端口都放行（游戏服务器端口各不相同）。</summary>
    public static readonly IReadOnlySet<int> DeniedPorts = new HashSet<int> { 25, 465, 587 };

    public static bool AllowsPort(int port) => port is > 0 and < 65536 && !DeniedPorts.Contains(port);

    /// <summary>这个目标能不能经出口连。</summary>
    public bool Allows(IPAddress address)
    {
        var ip = Normalize(address);
        if (!IsGloballyRoutable(ip)) return false;
        if (_localAddresses.Contains(ip)) return false;
        lock (_lock)
            if (_publicAddresses.Contains(ip)) return false;
        foreach (var (network, prefix) in _localNetworks)
            if (InNetwork(ip, network, prefix)) return false;
        return true;
    }

    /// <summary>
    /// 去掉作用域编号；IPv6 里嵌着 IPv4 的写法换成那个 IPv4（这样按 IPv4 的规则再判断一次）。
    /// </summary>
    internal static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        var b = address.GetAddressBytes();
        // ::a.b.c.d（已废弃的 IPv4 兼容地址）；:: 和 ::1 不算。
        if (b.Take(12).All(x => x == 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1)) return new IPAddress(b[12..16]);
        // NAT64：64:ff9b::/96 和 64:ff9b:1::/48（嵌在最后 4 字节）。
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && (b.Skip(4).Take(8).All(x => x == 0) || b[4] == 0 && b[5] == 1))
            return new IPAddress(b[12..16]);
        // 6to4：2002:AABB:CCDD::/48，嵌的是 AA.BB.CC.DD。
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);
        // Teredo：2001:0000::/32，最后 4 字节是客户端 IPv4 取反。
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return new IPAddress(b[12..16].Select(x => (byte)~x).ToArray());
        return address.ScopeId != 0 ? new IPAddress(b) : address;
    }

    /// <summary>只按地址本身判断：是不是公网上可以去的地址（不看本机网卡）。</summary>
    public static bool IsGloballyRoutable(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                     || b[0] == 100 && b[1] >= 64 && b[1] < 128          // CGNAT
                     || b[0] == 169 && b[1] == 254                          // 链路本地
                     || b[0] == 172 && b[1] >= 16 && b[1] < 32
                     || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2          // IETF 协议、文档
                     || b[0] == 192 && b[1] == 88 && b[2] == 99             // 6to4 中继
                     || b[0] == 192 && b[1] == 168
                     || b[0] == 198 && b[1] is 18 or 19                     // 基准测试
                     || b[0] == 198 && b[1] == 51 && b[2] == 100            // 文档
                     || b[0] == 203 && b[1] == 0 && b[2] == 113             // 文档
                     || b[0] >= 224);                                        // 组播、保留、广播
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            if ((b[0] & 0xFE) == 0xFC) return false;                        // ULA fc00::/7
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;   // 文档 2001:db8::/32
            if (b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0)) return false;   // 丢弃前缀 100::/64
            // 只有全球单播 2000::/3 才算公网。
            return (b[0] & 0xE0) == 0x20;
        }
        return false;
    }

    internal static bool InNetwork(IPAddress ip, IPAddress network, int prefixLength)
    {
        if (ip.AddressFamily != network.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        var full = prefixLength / 8;
        for (var i = 0; i < full; i++)
            if (a[i] != n[i]) return false;
        var rest = prefixLength % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (n[full] & mask);
    }

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;
}
