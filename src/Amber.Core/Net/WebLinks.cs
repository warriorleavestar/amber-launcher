namespace Amber.Core.Net;

/// <summary>来自远端元数据的网页地址只能交给 HTTP(S) 浏览器处理。</summary>
public static class WebLinks
{
    public static bool TryGetHttpUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
            parsed.UserInfo.Length > 0 || parsed.Host.Length == 0 || parsed.IsFile || parsed.IsUnc ||
            Uri.UnescapeDataString(value).Any(char.IsControl)) return false;
        uri = parsed;
        return true;
    }
}
