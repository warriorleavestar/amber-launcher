using Amber.Core.Localization;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 联机节点：EasyTier 节点地址（tcp://主机:端口 等）。全部由用户自己添加，Amber 不从网上拉节点列表。
/// 节点分享码把一个或几个地址打包成一串文字，方便搭节点的人发给朋友：AMBNODE-（base64url 编码的 JSON）。
/// </summary>
public static class RelayNodes
{
    private const string Prefix = "AMBNODE-";
    private static readonly string[] Schemes = ["tcp", "udp", "ws", "wss", "quic", "wg"];

    /// <summary>检查节点地址：协议是 EasyTier 支持的，带主机和端口。</summary>
    public static bool IsValid(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && Schemes.Contains(u.Scheme) && u.Host.Length > 0 && u.Port > 0;

    public static string Encode(string? name, IEnumerable<string> urls)
    {
        var list = urls.Select(u => u.Trim()).Where(IsValid).Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException(L.T("core.mp.nodes.empty"));
        var json = new JsonObject { ["v"] = 1, ["peers"] = new JsonArray(list.Select(u => (JsonNode?)u).ToArray()) };
        if (!string.IsNullOrWhiteSpace(name)) json["name"] = name.Trim();
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>从粘贴的文字里找分享码并解出节点。也接受直接粘贴的节点地址（每行一个）。</summary>
    public static bool TryDecode(string? text, out string? name, out List<string> urls)
    {
        name = null;
        urls = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            urls = text.Split(['\n', '\r', ' ', ',', '\uFF0C'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsValid).Distinct().ToList();
            return urls.Count > 0;
        }
        var body = new string(text[(start + Prefix.Length)..].TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        try
        {
            var b64 = body.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))?.AsObject();
            if (json is null) return false;
            name = json["name"]?.GetValue<string>();
            urls = (json["peers"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(IsValid).Distinct().ToList();
            return urls.Count > 0;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>测节点延迟：TCP 类（tcp/ws/wss）测建立连接的时间；UDP 类测不了，返回 null 和说明。</summary>
    public static async Task<(double? Ms, string? Error)> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return (null, L.T("core.mp.nodes.invalidaddress"));
        if (u.Scheme is not ("tcp" or "ws" or "wss")) return (null, L.F("core.mp.nodes.unsupportedlatency", u.Scheme.ToUpperInvariant()));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // 先解析域名，免得把 DNS 时间算进延迟。
            var addresses = await System.Net.Dns.GetHostAddressesAsync(u.Host, timeout.Token);
            if (addresses.Length == 0) return (null, L.T("core.mp.nodes.noaddresses"));
            using var client = new TcpClient(addresses[0].AddressFamily);
            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(addresses[0], u.Port, timeout.Token);
            return (watch.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, L.T("core.mp.nodes.timeout"));
        }
        catch (SocketException e)
        {
            return (null, e.SocketErrorCode == SocketError.HostNotFound ? L.T("core.mp.nodes.dnsfailed") : L.T("core.mp.nodes.connectfailed") + e.Message);
        }
    }
}
