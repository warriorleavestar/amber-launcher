using System.Net;
using System.Runtime.InteropServices;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// “进入服务器”的地址，经出口转发前的处理：拆出主机和端口（含 [IPv6]:端口），没写端口时像游戏一样查 _minecraft._tcp 的 SRV 记录；
/// 本机、局域网这类地址不经出口转（出口也会拒绝，这里直接不转，游戏照常直连）。
/// </summary>
/// <summary>经出口进服务器：实际连 Connect 这个地址；MC 握手包里写 Handshake 这个（和直接进时一样，是用户写的那个名字）。</summary>
public sealed record RelayPlan(string ConnectHost, int ConnectPort, string HandshakeHost, int HandshakePort);

public static partial class JoinTarget
{
    public const int DefaultPort = 25565;

    /// <summary>拆地址。端口不在 1–65535 或格式不对返回 false。hasPort 表示用户写了端口。</summary>
    public static bool TryParse(string? server, out string host, out int port, out bool hasPort)
    {
        host = "";
        port = DefaultPort;
        hasPort = false;
        if (string.IsNullOrWhiteSpace(server)) return false;
        server = server.Trim();
        if (server.StartsWith('['))
        {
            var close = server.IndexOf(']');
            if (close <= 1) return false;
            host = server[1..close];
            if (!IPAddress.TryParse(host, out _)) return false;
            if (server.Length == close + 1) return true;
            hasPort = true;
            return server[close + 1] == ':' && int.TryParse(server[(close + 2)..], out port) && port is > 0 and < 65536;
        }
        var colons = server.Count(c => c == ':');
        if (colons > 1)
        {
            // 没加方括号的 IPv6 地址：整段都是地址，用默认端口。
            host = server;
            return IPAddress.TryParse(server, out _);
        }
        if (colons == 1)
        {
            var parts = server.Split(':');
            host = parts[0];
            hasPort = true;
            return host.Length > 0 && int.TryParse(parts[1], out port) && port is > 0 and < 65536;
        }
        host = server;
        return true;
    }

    /// <summary>这个主机名一看就是本机或局域网里的（不查 DNS）：localhost、没有点的单段名字、.local / .lan / .home / .internal 等。</summary>
    public static bool IsLocalName(string host)
    {
        if (IPAddress.TryParse(host, out _)) return false;
        var h = host.TrimEnd('.').ToLowerInvariant();
        if (h is "localhost" || !h.Contains('.')) return true;
        string[] local = [".localhost", ".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain", ".intranet", ".corp"];
        return local.Any(s => h.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>
    /// 决定经出口连哪个主机和端口；返回 null 表示这个地址不经出口（本机、局域网、私有地址、在本机解析出私有地址）。
    /// 没写端口时查 SRV：有就用 SRV 给的主机和端口（游戏本身也是这么做的）。
    /// </summary>
    public static async Task<RelayPlan?> ResolveForRelayAsync(string server, CancellationToken ct = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<string, (string Host, int Port)?>? srv = null)
    {
        if (!TryParse(server, out var typedHost, out var typedPort, out var hasPort)) return null;
        if (IPAddress.TryParse(typedHost, out var typedIp)) return ExitPolicy.IsGloballyRoutable(typedIp) ? new RelayPlan(typedHost, typedPort, typedHost, typedPort) : null;
        if (IsLocalName(typedHost)) return null;
        var (host, port) = (typedHost, typedPort);
        if (!hasPort)
        {
            // SRV 查询是同步的系统调用：放到线程池上，最多等 3 秒，免得卡住界面。
            var lookup = srv ?? LookupSrv;
            var name = $"_minecraft._tcp.{typedHost.TrimEnd('.')}";
            try
            {
                if (await Task.Run(() => lookup(name), ct).WaitAsync(TimeSpan.FromSeconds(3), ct) is { } record) (host, port) = record;
            }
            catch (TimeoutException) { }
        }
        if (IPAddress.TryParse(host, out var ip)) return ExitPolicy.IsGloballyRoutable(ip) ? new RelayPlan(host, port, typedHost, typedPort) : null;
        if (IsLocalName(host)) return null;
        try
        {
            var addresses = await (resolve ?? ((h, c) => Dns.GetHostAddressesAsync(h, c)))(host, ct);
            // 本机解析出私有地址的名字（内网 DNS、hosts 文件）不经出口——出口那边解析出来的多半不是同一台机器。
            if (addresses.Length > 0 && addresses.Any(a => !ExitPolicy.IsGloballyRoutable(a))) return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            // 本机解析不了也没关系，交给出口那边解析。
        }
        return new RelayPlan(host, port, typedHost, typedPort);
    }

    /// <summary>查 SRV 记录（Windows 的 DnsQuery）。查不到或不是 Windows 返回 null。</summary>
    public static (string Host, int Port)? LookupSrv(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const ushort DnsTypeSrv = 33;
        if (DnsQuery(name, DnsTypeSrv, 0, IntPtr.Zero, out var results, IntPtr.Zero) != 0 || results == IntPtr.Zero) return null;
        try
        {
            (string, int, int)? best = null;
            for (var p = results; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
            {
                // DNS_RECORD：pNext、pName、wType、wDataLength、Flags、dwTtl、dwReserved，之后是数据（SRV：pNameTarget、wPriority、wWeight、wPort）。
                var type = (ushort)Marshal.ReadInt16(p, 2 * IntPtr.Size);
                if (type != DnsTypeSrv) continue;
                var data = 2 * IntPtr.Size + 16;
                var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, data));
                var priority = (ushort)Marshal.ReadInt16(p, data + IntPtr.Size);
                var srvPort = (ushort)Marshal.ReadInt16(p, data + IntPtr.Size + 4);
                if (string.IsNullOrEmpty(target) || srvPort == 0) continue;
                if (best is null || priority < best.Value.Item3) best = (target.TrimEnd('.'), srvPort, priority);
            }
            return best is { } b ? (b.Item1, b.Item2) : null;
        }
        finally
        {
            DnsRecordListFree(results, 1);
        }
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(IntPtr list, int freeType);
}
