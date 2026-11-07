using Amber.Core.Localization;
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
    /// <param name="onSkip">不经出口转时说明原因（显示在游戏日志里）。</param>
    public static async Task<RelayPlan?> ResolveForRelayAsync(string server, CancellationToken ct = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<string, (string Host, int Port)?>? srv = null, Action<string>? onSkip = null)
    {
        RelayPlan? Skip(string reason)
        {
            onSkip?.Invoke(reason);
            return null;
        }
        if (!TryParse(server, out var typedHost, out var typedPort, out _)) return Skip(L.F("core.mp.join.invalid_address", server));
        if (IPAddress.TryParse(typedHost, out var typedIp))
        {
            if (!ExitPolicy.IsGloballyRoutable(typedIp)) return Skip(L.F("core.mp.join.local_address", typedHost));
            if (!IsIPv4(typedIp)) return Skip(IPv6Only);
            return new RelayPlan(typedHost, typedPort, typedHost, typedPort);
        }
        if (IsLocalName(typedHost)) return Skip(L.F("core.mp.join.local_name", typedHost));
        if (typedHost.Length > 253) return Skip(L.T("core.mp.join.name_too_long"));
        var (host, port) = (typedHost, typedPort);
        // 和游戏一样：端口是默认的 25565（写没写都算）时查 SRV 记录。
        if (typedPort == DefaultPort)
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
        // 游戏按 SRV 查到的主机和端口去连，握手包里写的也是它们（和直接进时一样）。
        if (IPAddress.TryParse(host, out var ip))
        {
            if (!ExitPolicy.IsGloballyRoutable(ip)) return Skip(L.F("core.mp.join.points_to_local_address", typedHost));
            if (!IsIPv4(ip)) return Skip(IPv6Only);
            return new RelayPlan(host, port, host, port);
        }
        if (IsLocalName(host)) return Skip(L.F("core.mp.join.points_to_local_name", typedHost));
        try
        {
            var addresses = await (resolve ?? ((h, c) => Dns.GetHostAddressesAsync(h, c)))(host, ct);
            // 本机的 Clash / sing-box 等代理会把名字解析成 198.18.0.0/15 的假地址：当成没解析，交给出口去解析。
            var real = addresses.Where(a => !IsFakeIp(a)).ToArray();
            // 本机解析出私有地址的名字（内网 DNS、hosts 文件）不经出口——出口那边解析出来的多半不是同一台机器。
            if (real.Length > 0 && real.Any(a => !ExitPolicy.IsGloballyRoutable(a))) return Skip(L.F("core.mp.join.resolves_to_lan", host));
            // 只有 IPv6 地址的服务器：出口只转 IPv4，经出口进不去，直接连（和没开转发时一样）。
            // 看全部结果（包括假 IP）：代理软件的假 IP 是 IPv4，那种情况按名字交给出口解析。
            if (addresses.Length > 0 && !addresses.Any(IsIPv4)) return Skip(IPv6Only);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
        {
            // 本机解析不了也没关系，交给出口那边解析。
        }
        return new RelayPlan(host, port, host, port);
    }

    private static string IPv6Only => L.T("core.mp.join.ipv6_only");

    private static bool IsIPv4(IPAddress address) => ExitPolicy.Normalize(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>代理软件“假 IP”模式用的地址段（198.18.0.0/15）。</summary>
    internal static bool IsFakeIp(IPAddress address)
    {
        var a = ExitPolicy.Normalize(address);
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 198 && b[1] is 18 or 19;
    }

    /// <summary>查 SRV 记录：Windows 用 DnsQuery，Mac/Linux 用系统解析库的 res_query（按系统的 DNS 设置）。查不到返回 null。</summary>
    public static (string Host, int Port)? LookupSrv(string name)
    {
        if (!OperatingSystem.IsWindows()) return UnixResolver.QuerySrv(name);
        const ushort DnsTypeSrv = 33;
        var status = DnsQuery(name, DnsTypeSrv, 0, IntPtr.Zero, out var results, IntPtr.Zero);
        if (results == IntPtr.Zero) return null;
        try
        {
            if (status != 0) return null;
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

/// <summary>Mac/Linux 上查 SRV：调系统的 res_query 拿原始应答，自己解析（只认 SRV，处理名字压缩）。</summary>
internal static unsafe class UnixResolver
{
    private const int ClassIn = 1;
    private const int TypeSrv = 33;
    private static readonly Lock Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged<byte*, int, int, byte*, int, int> _query;

    /// <summary>
    /// res_query 在哪：glibc 2.34 起在 libc 里，更早在 libresolv（导出名 __res_query）；macOS 在 libresolv.9（res_9_query / res_query）。
    /// 都找不到时返回 false，查 SRV 就当查不到（和以前一样直接按名字连）。
    /// </summary>
    private static bool Load()
    {
        lock (Gate)
        {
            if (_loaded) return _query != null;
            _loaded = true;
            (string Library, string[] Names)[] candidates = OperatingSystem.IsMacOS()
                ? [("/usr/lib/libresolv.9.dylib", ["res_9_query", "res_query"])]
                : [("libc.so.6", ["res_query"]), ("libresolv.so.2", ["__res_query", "res_query"]), ("libresolv.so", ["__res_query", "res_query"])];
            foreach (var (library, names) in candidates)
            {
                if (!NativeLibrary.TryLoad(library, out var handle)) continue;
                foreach (var export in names)
                    if (NativeLibrary.TryGetExport(handle, export, out var fn))
                    {
                        _query = (delegate* unmanaged<byte*, int, int, byte*, int, int>)fn;
                        return true;
                    }
            }
            return false;
        }
    }

    internal static (string Host, int Port)? QuerySrv(string name)
    {
        if (!Load()) return null;
        var answer = new byte[4096];
        var question = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        int length;
        // 老的 glibc 和 macOS 的 res_query 用进程共享的解析状态：一次只查一个。
        lock (Gate)
        {
            fixed (byte* q = question)
            fixed (byte* a = answer)
                length = _query(q, ClassIn, TypeSrv, a, answer.Length);
        }
        return length > 0 ? ParseSrv(answer.AsSpan(0, Math.Min(length, answer.Length))) : null;
    }

    /// <summary>从 DNS 应答里挑优先级数字最小的 SRV（和 Windows 那边一样）。格式不对返回 null，不抛异常。</summary>
    internal static (string Host, int Port)? ParseSrv(ReadOnlySpan<byte> message)
    {
        try
        {
            if (message.Length < 12) return null;
            var questions = ReadU16(message, 4);
            var answers = ReadU16(message, 6);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                ReadName(message, ref offset);
                offset += 4;
            }
            (string Host, int Port, int Priority)? best = null;
            for (var i = 0; i < answers; i++)
            {
                ReadName(message, ref offset);
                var type = ReadU16(message, offset);
                var rdLength = ReadU16(message, offset + 8);
                var data = offset + 10;
                offset = data + rdLength;
                if (offset > message.Length) return best is { } partial ? (partial.Host, partial.Port) : null;
                if (type != TypeSrv || rdLength < 7) continue;
                var priority = ReadU16(message, data);
                var port = ReadU16(message, data + 4);
                var targetOffset = data + 6;
                var target = ReadName(message, ref targetOffset);
                // 目标是“.”表示这个服务明确不存在。
                if (target.Length == 0 || port == 0) continue;
                if (best is null || priority < best.Value.Priority) best = (target, port, priority);
            }
            return best is { } b ? (b.Host, b.Port) : null;
        }
        catch (IndexOutOfRangeException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (FormatException) { return null; }
    }

    private static int ReadU16(ReadOnlySpan<byte> m, int at) => m[at] << 8 | m[at + 1];

    /// <summary>读一个域名（支持压缩指针，最多跳 32 次防止循环），offset 移到名字后面。</summary>
    private static string ReadName(ReadOnlySpan<byte> m, ref int offset)
    {
        var labels = new List<string>();
        var at = offset;
        var jumped = false;
        for (var jumps = 0; ; )
        {
            var len = m[at];
            if (len == 0)
            {
                if (!jumped) offset = at + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 32) throw new FormatException("dns name loop");
                var pointer = (len & 0x3F) << 8 | m[at + 1];
                if (!jumped) offset = at + 2;
                jumped = true;
                at = pointer;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("dns label");
            labels.Add(System.Text.Encoding.ASCII.GetString(m.Slice(at + 1, len)));
            at += 1 + len;
        }
        var name = string.Join('.', labels);
        if (name.Length > 253) throw new FormatException("dns name too long");
        return name;
    }
}
