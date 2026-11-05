using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

/// <summary>出口使用端交给提权守护的网络计划。只传地址，接口和网关由守护在改变路由前现场核对。</summary>
public sealed record ExitNetworkPlan(string VirtualIp, IReadOnlyList<string> UnderlayAddresses, string AdapterName = "Amber LAN")
{
    public const int MaxUnderlayAddresses = 128;

    public static ExitNetworkPlan? Create(EasyTierOptions options)
    {
        if (options.ExitNode is null) return null;
        if (!options.Tun) throw new ArgumentException(L.T("core.mp.component.exitrequireslan"));
        if (options.DirectUnderlayUsesIpv6)
            throw new InvalidOperationException(L.T("core.mp.exit.ipv6underlay"));
        var ownIp = EasyTierArgs.VirtualIp(options);
        if (!IPAddress.TryParse(options.ExitNode, out var exit) || exit.AddressFamily != AddressFamily.InterNetwork
            || !exit.ToString().StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal)
            || exit.GetAddressBytes()[3] is 0 or 255 || exit.ToString() == ownIp)
            throw new ArgumentException(L.T("core.mp.exit.notmember"));

        var loopbackBridge = options.Peers.Concat(options.Listeners ?? []).Any(IsLoopback);
        if (loopbackBridge && options.Peers.Any(p => !IsLoopback(p)))
            throw new InvalidOperationException(L.T("core.mp.exit.mixedpeers"));

        if (options.DirectUnderlayAddresses.Count > MaxUnderlayAddresses)
            throw new ArgumentException(L.T("core.mp.exit.toomany"));
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var nodeAddresses = new List<string>();
        if (options.Peers.Count > MaxUnderlayAddresses) throw new ArgumentException(L.T("core.mp.exit.toomany"));
        // 全局门禁不能只信任进程的网卡绑定；普通节点也须有精确的物理承载地址。
        foreach (var peer in options.Peers.Where(p => !IsLoopback(p)))
        {
            if (!Uri.TryCreate(peer, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException(L.T("core.mp.exit.nodeipv4only"));
            nodeAddresses.Add(address.ToString());
        }
        foreach (var value in options.DirectUnderlayAddresses.Concat(nodeAddresses))
        {
            if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException(L.T("core.mp.exit.underlayipv4"));
            var bytes = ip.GetAddressBytes();
            if (bytes[0] is 0 or >= 224 || ip.Equals(IPAddress.Broadcast)
                || ip.ToString().StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal))
                throw new ArgumentException(L.T("core.mp.exit.underlayunsafe"));
            if (!IPAddress.IsLoopback(ip)) addresses.Add(ip.ToString());
        }
        if (addresses.Count > MaxUnderlayAddresses) throw new ArgumentException(L.T("core.mp.exit.toomany"));
        // 只返回已明确识别的承载；未列入的物理出口由会话门禁阻止，不能猜测网关或放行整个进程。
        return new ExitNetworkPlan(ownIp, addresses.Order(StringComparer.Ordinal).ToArray(), EasyTierArgs.DeviceName(options));
    }

    private static bool IsLoopback(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;
}
