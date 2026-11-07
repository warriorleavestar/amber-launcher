using System.Net;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>“仅游戏”出口在 Mac/Linux 上也拦路由表里的非默认路由（VPN 内网、直连网段）。解析部分用样例文本。</summary>
public sealed class ExitRoutesUnixTests
{
    private static bool Has(List<(IPAddress, int)> routes, string network, int prefix) =>
        routes.Any(r => r.Item1.Equals(IPAddress.Parse(network)) && r.Item2 == prefix);

    [Fact]
    public void LinuxProcNetRoute()
    {
        // 默认路由经 eth0；本机网段 192.168.1.0/24；OpenVPN 推的 10.8.0.0/16 和 172.20.0.0/16 经 tun0；全局 VPN 的 0.0.0.0/1 经 tun0。
        const string text = """
            Iface	Destination	Gateway 	Flags	RefCnt	Use	Metric	Mask		MTU	Window	IRTT
            eth0	00000000	0101A8C0	0003	0	0	100	00000000	0	0	0
            eth0	0001A8C0	00000000	0001	0	0	100	00FFFFFF	0	0	0
            tun0	0000080A	0100080A	0003	0	0	0	0000FFFF	0	0	0
            tun0	000014AC	0100080A	0003	0	0	0	0000FFFF	0	0	0
            tun0	00000000	0100080A	0003	0	0	0	00000080	0	0	0
            """;
        var rows = ExitPolicy.ParseLinuxRoutes(text);
        Assert.Equal(5, rows.Count);
        Assert.Contains(rows, r => r.Interface == "eth0" && r.Prefix == 0);
        var routes = ExitPolicy.Classify(rows);
        Assert.True(Has(routes, "192.168.1.0", 24));
        Assert.True(Has(routes, "10.8.0.0", 16));
        Assert.True(Has(routes, "172.20.0.0", 16));
        // 太宽的（/1）不拦，否则什么都连不了。
        Assert.DoesNotContain(routes, r => r.Item2 < 8);
    }

    [Fact]
    public void LinuxProcNetIPv6Route()
    {
        const string text = """
            fd000000000000000000000000000000 40 00000000000000000000000000000000 00 00000000000000000000000000000000 00000100 00000001 00000000 00000001     eth0
            fd420000000000000000000000000000 30 00000000000000000000000000000000 00 fd000000000000000000000000000001 00000400 00000001 00000000 00000003     wg0
            00000000000000000000000000000000 00 00000000000000000000000000000000 00 fe800000000000000000000000000001 00000400 00000001 00000000 00000003     eth0
            """;
        var rows = ExitPolicy.ParseLinuxIPv6Routes(text);
        Assert.Equal(3, rows.Count);
        var routes = ExitPolicy.Classify(rows);
        Assert.True(Has(routes, "fd00::", 64));
        Assert.True(Has(routes, "fd42::", 48));
        Assert.DoesNotContain(routes, r => r.Item2 == 0);
    }

    [Fact]
    public void MacNetstat()
    {
        const string inet = """
            Routing tables

            Internet:
            Destination        Gateway            Flags               Netif Expire
            default            192.168.1.1        UGScg                 en0
            10.8/16            10.8.0.1           UGSc                utun3
            127                127.0.0.1          UCS                   lo0
            127.0.0.1          127.0.0.1          UH                    lo0
            169.254            link#6             UCS                   en0      !
            192.168.1          link#6             UCS                   en0      !
            192.168.1.1/32     link#6             UCS                   en0      !
            192.168.1.1        a4:91:b1:2c:3d:4e  UHLWIir               en0   1198
            224.0.0/4          link#6             UmCS                  en0      !
            """;
        const string inet6 = """
            Routing tables

            Internet6:
            Destination                             Gateway                                 Flags               Netif Expire
            default                                 fe80::1%en0                             UGcg                  en0
            ::1                                     ::1                                     UHL                   lo0
            fd7a:115c:a1e0::/48                     link#20                                 UCS                 utun4
            fe80::%lo0/64                           fe80::1%lo0                             UcI                   lo0
            """;
        var rows = ExitPolicy.ParseBsdNetstat(inet).Concat(ExitPolicy.ParseBsdNetstat(inet6)).ToList();
        Assert.Contains(rows, r => r.Interface == "en0" && r.Prefix == 0 && r.Destination.Equals(IPAddress.Any));
        Assert.Contains(rows, r => r.Interface == "en0" && r.Prefix == 0 && r.Destination.Equals(IPAddress.IPv6Any));
        var routes = ExitPolicy.Classify(rows);
        Assert.True(Has(routes, "10.8.0.0", 16));       // VPN 内网
        Assert.True(Has(routes, "127.0.0.0", 8));
        Assert.True(Has(routes, "192.168.1.0", 24));    // 省略写法的直连网段
        Assert.True(Has(routes, "192.168.1.1", 32));    // 路由器（MAC 地址作网关的主机路由）
        Assert.True(Has(routes, "fd7a:115c:a1e0::", 48));
        Assert.True(Has(routes, "::1", 128));
        Assert.DoesNotContain(routes, r => r.Item2 == 0);
    }

    [Fact]
    public void RealRoutingTableIsReadOnEveryPlatform()
    {
        var routes = ExitPolicy.NonDefaultRoutes();
        Assert.DoesNotContain(routes, r => r.Item2 == 0);
        using var real = new ExitPolicy();
        real.SetReady(true);
        foreach (var (network, _) in routes) Assert.False(real.Allows(network), network.ToString());
    }
}
