using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
public sealed partial class ExitPolicy : IDisposable
{
    /// <summary>本机公网 IPv6 周围按这个长度一起拦（运营商一般给家里一个 /56 或 /48，路由器和别的 VLAN 都在里面）。</summary>
    public int IPv6SitePrefix { get; }
    /// <summary>
    /// 别处（直连房间的 STUN、路由器端口映射）查到的本机公网地址，所有出口规则共用。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IPAddress, byte> Reported = new();

    public static void ReportPublicAddress(IPAddress address) => Reported.TryAdd(Normalize(address), 0);

    private volatile IReadOnlyList<(IPAddress Network, int PrefixLength)> _localNetworks = [];
    private volatile IReadOnlySet<IPAddress> _localAddresses = new HashSet<IPAddress>();
    private readonly HashSet<IPAddress> _publicAddresses = [];
    private readonly Lock _lock = new();

    private volatile bool _ready;
    private volatile string? _failure;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<string>? _stunServers;

    /// <summary>已经查到本机公网地址，可以开始转发。没查到之前所有连接都拒绝（宁可不转，也不放过自己的公网地址）。</summary>
    public bool IsReady => _ready;

    /// <summary>查不到公网地址时的说明（这时不当出口）；正常时为 null。</summary>
    public string? Failure => _failure;

    private readonly SemaphoreSlim _wake = new(0);

    public ExitPolicy(int ipv6SitePrefix = 56)
    {
        IPv6SitePrefix = ipv6SitePrefix;
        RefreshLocal();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    /// <summary>开始当出口：先查公网地址，之后每 3 分钟、网络变化时再查（路由器重新拨号会换公网 IP）。</summary>
    public void Start(IReadOnlyList<string>? stunServers = null, Action<string>? log = null)
    {
        _stunServers = stunServers;
        _ = LoopAsync(log, _cts.Token);
    }

    private async Task LoopAsync(Action<string>? log, CancellationToken ct)
    {
        try
        {
            string? lastLogged = null;
            while (!ct.IsCancellationRequested)
            {
                await RefreshPublicAsync(ct);
                if (_failure != lastLogged)
                {
                    log?.Invoke(_failure is { } f ? "[转发] " + f : "[转发] 已查到公网 IP，开始转发");
                    lastLogged = _failure;
                }
                // 正常时 3 分钟重查一次；查不到时 15 秒后重试；网络变化时等 3 秒（地址变化常常一下来好几个）再查。
                var delay = _ready ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(15);
                await Task.WhenAny(Task.Delay(delay, ct), _wake.WaitAsync(ct));
                ct.ThrowIfCancellationRequested();
                if (!_ready)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    while (_wake.Wait(0)) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshPublicAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (await DiscoverPublicAddressAsync(_stunServers, ct))
            {
                _failure = null;
                _ready = true;
            }
            else
            {
                _ready = false;
                _failure = "查不到你的公网 IP（STUN 服务器连不上，可能是 UDP 被拦了），为了不让别人经你连到你的路由器，暂时不转发。网络恢复后会自动重试。";
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>等到可以转发（最多等 timeout）。</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_ready && _failure is null && DateTime.UtcNow < deadline) await Task.Delay(100, ct);
        return _ready;
    }

    /// <summary>测试用：指定本机地址和网段，不读真实网卡。</summary>
    internal ExitPolicy(IEnumerable<IPAddress> localAddresses, IEnumerable<(IPAddress, int)> localNetworks)
    {
        _localAddresses = localAddresses.Select(Normalize).ToHashSet();
        _localNetworks = localNetworks.Select(n => (Normalize(n.Item1), n.Item2)).ToList();
        _ready = true;
    }

    /// <summary>测试用：模拟还没查到公网地址的状态。</summary>
    internal void SetReady(bool ready) => _ready = ready;

    /// <summary>网卡地址变了：重新收集本机地址，公网地址也重新查，查到之前不转发。</summary>
    private void OnAddressChanged(object? sender, EventArgs e)
    {
        RefreshLocal();
        _ready = false;
        _failure = null;
        _wake.Release();
    }

    /// <summary>本机所有网卡（包括停用的、虚拟的）的单播地址和所在网段。</summary>
    public void RefreshLocal()
    {
        var addresses = new HashSet<IPAddress>();
        var networks = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                foreach (var u in props.UnicastAddresses)
                {
                    var ip = Normalize(u.Address);
                    addresses.Add(ip);
                    var prefix = u.PrefixLength;
                    if (prefix is > 0 and < 128) networks.Add((ip, prefix));
                    // 公网 IPv6：同一个 /56 里的别的 /64（路由器、别的网段）也是家里的。
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6 && IsGloballyRoutable(ip)) networks.Add((ip, IPv6SitePrefix));
                }
                // 默认网关（路由器）的地址。
                foreach (var g in props.GatewayAddresses) addresses.Add(Normalize(g.Address));
            }
        }
        catch (NetworkInformationException) { }
        // 路由表里除默认路由以外的每条路由的目标网段（包括 VPN 推下来的路由）：这些地址不走普通的上网出口。
        if (OperatingSystem.IsWindows()) networks.AddRange(NonDefaultRoutes());
        _localAddresses = addresses;
        _localNetworks = networks;
    }

    /// <summary>加上从外面看到的本机公网地址（STUN、路由器端口映射报告的外部地址）。</summary>
    public void AddPublicAddress(IPAddress address)
    {
        lock (_lock) _publicAddresses.Add(Normalize(address));
    }

    /// <summary>用 STUN 查一次自己的公网 IPv4，查到返回 true。</summary>
    public async Task<bool> DiscoverPublicAddressAsync(IReadOnlyList<string>? stunServers = null, CancellationToken ct = default)
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
                    return true;
                }
            }
            catch (Exception e) when (e is SocketException or IOException) { }
        }
        return false;
    }

    /// <summary>发邮件的端口（SMTP 25、465、587）：不许借别人的 IP 发垃圾邮件。其他端口都放行（游戏服务器端口各不相同）。</summary>
    public static readonly IReadOnlySet<int> DeniedPorts = new HashSet<int> { 25, 465, 587 };

    public static bool AllowsPort(int port) => port is > 0 and < 65536 && !DeniedPorts.Contains(port);

    /// <summary>这个目标能不能经出口连。</summary>
    public bool Allows(IPAddress address)
    {
        if (!_ready) return false;
        var ip = Normalize(address);
        if (!IsGloballyRoutable(ip)) return false;
        if (_localAddresses.Contains(ip)) return false;
        lock (_lock)
            if (_publicAddresses.Contains(ip)) return false;
        if (Reported.ContainsKey(ip)) return false;
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

    /// <summary>
    /// 读 Windows 路由表（GetIpForwardTable2），返回所有非默认路由的目标网段。
    /// 太宽的路由（IPv4 短于 /8、IPv6 短于 /16，比如全局 VPN 常用的 0.0.0.0/1）当作默认路由，不拦，否则什么都连不了。
    /// </summary>
    internal static List<(IPAddress, int)> NonDefaultRoutes()
    {
        var result = new List<(IPAddress, int)>();
        if (GetIpForwardTable2(0 /* AF_UNSPEC */, out var table) != 0 || table == IntPtr.Zero) return result;
        try
        {
            var count = Marshal.ReadInt32(table);
            const int rowSize = 104;
            const int firstRow = 8;
            var rows = new List<(uint Interface, IPAddress Destination, int Prefix, bool OnLink)>();
            for (var i = 0; i < count; i++)
            {
                var row = table + firstRow + i * rowSize;
                // MIB_IPFORWARD_ROW2：InterfaceIndex（偏移 8）、DestinationPrefix（SOCKADDR_INET 偏移 12 + PrefixLength 偏移 40）、NextHop（偏移 44）。
                var index = (uint)Marshal.ReadInt32(row, 8);
                var prefixLength = Marshal.ReadByte(row, 40);
                if (ReadSockaddr(row + 12) is not { } destination) continue;
                var nextHop = ReadSockaddr(row + 44);
                var onLink = nextHop is null || nextHop.Equals(IPAddress.Any) || nextHop.Equals(IPAddress.IPv6Any);
                rows.Add((index, Normalize(destination), prefixLength, onLink));
            }
            // 有默认路由的网卡（正常上网走的那块）。
            var defaultInterfaces = rows.Where(r => r.Prefix == 0).Select(r => r.Interface).ToHashSet();
            foreach (var r in rows)
            {
                if (r.Prefix == 0) continue;
                var full = r.Destination.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                // 直连网段（本机所在的网段）和主机路由（本机地址、广播）：一定是本地的。
                if (r.OnLink && r.Prefix >= (full == 32 ? 8 : 16) || r.Prefix == full) result.Add((r.Destination, r.Prefix));
            }
            // 经别的网卡（VPN、虚拟网卡）去的、范围不太大的路由：公司内网、VPN 推下来的网段。
            // 分流 VPN 常常装上成百上千条公网路由，那种情况不拦（不然大半个互联网都连不了）。
            var viaOther = rows.Where(r => r.Prefix > 0 && !r.OnLink && !defaultInterfaces.Contains(r.Interface)
                                           && r.Prefix >= (r.Destination.AddressFamily == AddressFamily.InterNetwork ? 16 : 32)).ToList();
            if (viaOther.Count <= 64) result.AddRange(viaOther.Select(r => (r.Destination, r.Prefix)));
        }
        finally
        {
            FreeMibTable(table);
        }
        return result;
    }

    private static IPAddress? ReadSockaddr(IntPtr p)
    {
        var family = (ushort)Marshal.ReadInt16(p);
        if (family == 2)
        {
            var bytes = new byte[4];
            Marshal.Copy(p + 4, bytes, 0, 4);
            return new IPAddress(bytes);
        }
        if (family == 23)
        {
            var bytes = new byte[16];
            Marshal.Copy(p + 8, bytes, 0, 16);
            return new IPAddress(bytes);
        }
        return null;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpForwardTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

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

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _cts.Cancel();
    }
}
