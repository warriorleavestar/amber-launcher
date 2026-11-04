using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Amber.Core.Multiplayer;

public sealed record DiagnosticEntry(DateTime Time, string Category, string Text)
{
    public override string ToString() => $"[{Time:HH:mm:ss.fff}] [{Category}] {Text}";
}

/// <summary>联机调试日志：只在内存里，导出诊断报告时才写文件。</summary>
public sealed class DiagnosticLog(int capacity = 5000)
{
    private readonly LinkedList<DiagnosticEntry> _entries = new();

    public event Action<DiagnosticEntry>? Added;

    public void Add(string category, string text)
    {
        var entry = new DiagnosticEntry(DateTime.Now, category, text);
        lock (_entries)
        {
            _entries.AddLast(entry);
            while (_entries.Count > capacity) _entries.RemoveFirst();
        }
        Added?.Invoke(entry);
    }

    public List<DiagnosticEntry> Snapshot()
    {
        lock (_entries) return [.. _entries];
    }

    public void Clear()
    {
        lock (_entries) _entries.Clear();
    }
}

/// <summary>诊断报告：去掉房间密钥和各种码，默认遮住公网 IP 的后半段。</summary>
public static partial class NetDiagnostics
{
    public static string Redact(string text, IEnumerable<string> secrets, bool includePublicIps)
    {
        // 先换整个码，再换单独出现的密钥。
        text = RoomCodePattern().Replace(text, m => L.F("core.mp.diagnostics.redactedroom", m.Groups[1].Value, m.Groups[2].Value));
        text = DirectCodePattern().Replace(text, m => L.F("core.mp.diagnostics.redacteddirect", m.Groups[1].Value));
        text = SecretArgPattern().Replace(text, L.T("core.mp.diagnostics.secretarg"));
        foreach (var secret in secrets.Where(s => s.Length >= 6)) text = text.Replace(secret, L.T("core.mp.diagnostics.secret"), StringComparison.OrdinalIgnoreCase);
        if (!includePublicIps)
        {
            text = IPv4Pattern().Replace(text, m => IPAddress.TryParse(m.Value, out var a) && PortMapping.IsPublic(a) ? MaskAddress(m.Value) : m.Value);
            text = IPv6Pattern().Replace(text, m => IPAddress.TryParse(m.Value, out var a) && a.AddressFamily == AddressFamily.InterNetworkV6 && PortMapping.IsPublic(a) && !IPAddress.IsLoopback(a) ? MaskAddress(m.Value) : m.Value);
        }
        return text;
    }

    /// <summary>IPv4 留前两段，IPv6 留前两组。</summary>
    public static string MaskAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var a)) return address;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = address.Split('.');
            return $"{parts[0]}.{parts[1]}.*.*";
        }
        var groups = a.ToString().Split(':');
        return $"{groups[0]}:{groups[1]}:*";
    }

    [GeneratedRegex(@"AMB-([0-9A-Z]{8})-[0-9A-Z]{12}-(\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex RoomCodePattern();

    [GeneratedRegex(@"(AMBDIRECT\d+|AMBREPLY\d+)-[A-Za-z0-9_-]+")]
    private static partial Regex DirectCodePattern();

    [GeneratedRegex(@"--network-secret\s+\S+")]
    private static partial Regex SecretArgPattern();

    [GeneratedRegex(@"(?<![\d.])(\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex IPv4Pattern();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{1,4})*")]
    private static partial Regex IPv6Pattern();

    public static string BuildReport(string version, IEnumerable<DiagnosticEntry> log, IEnumerable<string>? resultTable, IEnumerable<string> secrets, bool includePublicIps)
    {
        var builder = new StringBuilder();
        builder.AppendLine(L.T("core.mp.diagnostics.title"));
        builder.AppendLine(L.F("core.mp.diagnostics.version", version));
        builder.AppendLine(L.F("core.mp.diagnostics.system", Environment.OSVersion, Environment.Is64BitOperatingSystem ? L.T("core.mp.diagnostics.os64") : L.T("core.mp.diagnostics.os32")));
        builder.AppendLine(L.F("core.mp.diagnostics.time", DateTime.Now));
        builder.AppendLine(L.F("core.mp.diagnostics.privacy", includePublicIps ? L.T("core.mp.diagnostics.visibleips") : L.T("core.mp.diagnostics.maskedips")));
        builder.AppendLine();
        if (resultTable is not null)
        {
            builder.AppendLine(L.T("core.mp.diagnostics.results"));
            foreach (var line in resultTable) builder.AppendLine(line);
            builder.AppendLine();
        }
        builder.AppendLine(L.T("core.mp.diagnostics.log"));
        foreach (var entry in log) builder.AppendLine(entry.ToString());
        return Redact(builder.ToString(), secrets, includePublicIps);
    }
}
