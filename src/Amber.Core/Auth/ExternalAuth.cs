using Amber.Core.Localization;
using Amber.Core;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Auth;

public sealed record ExternalProfile(string Id, string Name);

public sealed record ExternalLoginResult(Account Account, IReadOnlyList<ExternalProfile> Profiles);

/// <summary>
/// 外置登录（authlib-injector / Yggdrasil 协议）。密码只用于这一次请求，不保存。
/// http 要用不自动跟随跳转的（<see cref="CreateHttpClient"/>）：自动跟随 307/308 时，密码和令牌在检查之前就已经发到别的服务器了。
/// </summary>
public sealed class ExternalAuth(HttpClient http)
{
    /// <summary>发现 API 地址时最多跟几次跳转。</summary>
    private const int MaxDiscoveryRedirects = 5;

    /// <summary>外置登录专用的 HttpClient：不自动跟随跳转。</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Amber/{typeof(ExternalAuth).Assembly.GetName().Version?.ToString(3) ?? "0.1"}");
        return client;
    }

    /// <summary>
    /// 外置登录服务器地址只能是 https（密码和令牌要经它传）；本机回环地址例外（本地测试用）。
    /// 返回规范化的地址；不合格时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static Uri CheckSecure(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri))
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(L.F("core.auth.external.insecure", url));
        return uri;
    }

    /// <summary>
    /// 只认用户写的 localhost、127.0.0.0/8 和 ::1。Uri 会把主机名“loopback”悄悄改写成 localhost（Uri.IsLoopback 也认它），
    /// 所以这里看原文里写的主机名，不看规范化之后的。
    /// </summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        var host = WrittenHost(uri.OriginalString);
        if (host is null) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase);
        return System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip)
            && System.Net.IPAddress.TryParse(uri.DnsSafeHost, out var parsed) && System.Net.IPAddress.IsLoopback(parsed);
    }

    /// <summary>地址原文里的主机名（去掉协议、用户信息、端口和路径）。</summary>
    private static string? WrittenHost(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return null;
        var rest = url[(start + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#', '\\']);
        var authority = end < 0 ? rest : rest[..end];
        authority = authority[(authority.LastIndexOf('@') + 1)..];
        if (authority.StartsWith('[')) return authority.IndexOf(']') is var close and > 0 ? authority[..(close + 1)] : null;
        var colon = authority.IndexOf(':');
        return colon < 0 ? authority : authority[..colon];
    }

    /// <summary>返回了跳转（3xx）：不跟着走，当作错误。</summary>
    private static void CheckNotRedirected(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException(L.F("core.auth.external.redirected",
                response.Headers.Location is { } location && location.IsAbsoluteUri ? location.GetLeftPart(UriPartial.Authority) : (int)response.StatusCode));
    }

    /// <summary>
    /// 按 authlib-injector 约定解析服务器地址：响应头可以把用户填的网址指向真正的 API 地址。
    /// 用户填的地址、跳转后的地址和响应头指过去的地址都要是 https，从 https 换到 http 一律拒绝。
    /// </summary>
    public async Task<string> ResolveApiRootAsync(string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        var final = CheckSecure(url);
        // 跳转自己一步步跟：每一步都不能降到 http，也不能换到别的服务器。
        for (var hop = 0; ; hop++)
        {
            using var response = await http.GetAsync(final, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                if (hop >= MaxDiscoveryRedirects) throw new InvalidOperationException(L.F("core.auth.external.redirected", final.GetLeftPart(UriPartial.Authority)));
                var target = new Uri(final, next);
                CheckNoDowngrade(final, target);
                CheckSameOrigin(final, target);
                final = target;
                continue;
            }
            // 传进来的 HttpClient 自己跟随了跳转（不是 CreateHttpClient 建的）：最终地址也得过同样的检查。
            if (response.RequestMessage?.RequestUri is { } followed && followed != final)
            {
                CheckNoDowngrade(final, followed);
                CheckSameOrigin(final, followed);
            }
            if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var locations) && locations.FirstOrDefault() is { } location)
            {
                var resolved = new Uri(final, location);
                CheckNoDowngrade(final, resolved);
                return resolved.ToString().TrimEnd('/');
            }
            return final.ToString().TrimEnd('/');
        }
    }

    private static void CheckNoDowngrade(Uri from, Uri to)
    {
        CheckSecure(to.ToString());
        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(L.F("core.auth.external.insecure", to));
    }

    /// <summary>API 根地址返回的元数据原文，启动时要预取给 authlib-injector。</summary>
    public async Task<string> GetMetadataAsync(string apiRoot, CancellationToken ct = default)
    {
        var uri = CheckSecure(apiRoot + "/");
        using var response = await http.GetAsync(uri, ct);
        CheckSameOrigin(uri, response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>请求被跳转到了别的地方（3xx，或者传进来的 HttpClient 自己跟随了跳转）：不认，令牌和密码只发给用户填的那个服务器。</summary>
    private static void CheckSameOrigin(Uri requested, HttpResponseMessage response)
    {
        CheckNotRedirected(response);
        if (response.RequestMessage?.RequestUri is { } final) CheckSameOrigin(requested, final);
    }

    private static void CheckSameOrigin(Uri requested, Uri final)
    {
        if (final.Scheme != requested.Scheme || !string.Equals(final.Authority, requested.Authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.F("core.auth.external.redirected", final.GetLeftPart(UriPartial.Authority)));
    }

    /// <summary>这个外置账号存的服务器地址不安全（v0.5.8 及以前允许 http）：要用 https 重新添加。</summary>
    public static bool HasInsecureServer(Account account)
    {
        if (account.Kind != AccountKind.External || string.IsNullOrEmpty(account.ServerUrl)) return false;
        try { CheckSecure(account.ServerUrl); return false; }
        catch (InvalidOperationException) { return true; }
    }

    public async Task<string> GetServerNameAsync(string apiRoot, CancellationToken ct = default)
    {
        var meta = JsonNode.Parse(await GetMetadataAsync(apiRoot, ct));
        return meta?["meta"]?["serverName"]?.GetValue<string>() ?? new Uri(apiRoot).Host;
    }

    public async Task<ExternalLoginResult> LoginAsync(string apiRoot, string username, string password, CancellationToken ct = default)
    {
        var clientToken = Guid.NewGuid().ToString("N");
        var json = await PostAsync(apiRoot + "/authserver/authenticate", new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = false,
        }, ct);

        var profiles = (json["availableProfiles"] as JsonArray ?? [])
            .Select(p => new ExternalProfile(p!["id"]!.GetValue<string>(), p["name"]!.GetValue<string>())).ToList();
        if (profiles.Count == 0) throw new InvalidOperationException(L.T("core.auth.external.noprofiles"));

        var account = new Account
        {
            Kind = AccountKind.External,
            ServerUrl = apiRoot,
            ServerName = await GetServerNameAsync(apiRoot, ct),
            ClientToken = json["clientToken"]?.GetValue<string>() ?? clientToken,
            AccessToken = json["accessToken"]!.GetValue<string>(),
        };
        if (json["selectedProfile"] is JsonObject selected)
            Apply(account, selected["id"]!.GetValue<string>(), selected["name"]!.GetValue<string>());
        else if (profiles.Count == 1)
            await SelectProfileAsync(account, profiles[0], ct);
        return new ExternalLoginResult(account, profiles);
    }

    /// <summary>一个账号多个角色时，用刷新接口绑定选中的角色。</summary>
    public async Task SelectProfileAsync(Account account, ExternalProfile profile, CancellationToken ct = default)
    {
        var json = await PostAsync(account.ServerUrl + "/authserver/refresh", new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = account.ClientToken,
            ["selectedProfile"] = new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name },
        }, ct);
        account.AccessToken = json["accessToken"]!.GetValue<string>();
        Apply(account, profile.Id, profile.Name);
    }

    /// <summary>启动前确认令牌有效，失效就刷新；刷新也失败时要求重新输入密码。</summary>
    public async Task EnsureValidAsync(Account account, CancellationToken ct = default)
    {
        if (account.AccessToken is null) throw new LoginRequiredException(L.F("core.auth.account.expired", account.Name));
        var validateUri = CheckSecure(account.ServerUrl + "/authserver/validate");
        using var validate = await http.PostAsync(validateUri, AmberJson.Body(new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = account.ClientToken,
        }), ct);
        CheckSameOrigin(validateUri, validate);
        if (validate.IsSuccessStatusCode) return;

        try
        {
            var json = await PostAsync(account.ServerUrl + "/authserver/refresh", new JsonObject
            {
                ["accessToken"] = account.AccessToken,
                ["clientToken"] = account.ClientToken,
            }, ct);
            account.AccessToken = json["accessToken"]!.GetValue<string>();
        }
        catch (InvalidOperationException e)
        {
            throw new LoginRequiredException(L.F("core.auth.account.expireddetail", account.Name, e.Message));
        }
    }

    /// <summary>从 sessionserver 读皮肤地址。没有皮肤时 SkinUrl 保持为空。</summary>
    public async Task LoadSkinAsync(Account account, CancellationToken ct = default)
    {
        var profileUri = CheckSecure($"{account.ServerUrl}/sessionserver/session/minecraft/profile/{account.Uuid:N}");
        using var profileResponse = await http.GetAsync(profileUri, ct);
        CheckSameOrigin(profileUri, profileResponse);
        profileResponse.EnsureSuccessStatusCode();
        var text = await profileResponse.Content.ReadAsStringAsync(ct);
        var profile = JsonNode.Parse(text);
        var property = (profile?["properties"] as JsonArray)?.FirstOrDefault(p => p?["name"]?.GetValue<string>() == "textures");
        if (property?["value"]?.GetValue<string>() is not { } encoded) return;
        var textures = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))?["textures"];
        account.SkinUrl = textures?["SKIN"]?["url"]?.GetValue<string>();
        account.SlimSkin = textures?["SKIN"]?["metadata"]?["model"]?.GetValue<string>() == "slim";
    }

    private static void Apply(Account account, string id, string name)
    {
        account.Uuid = Guid.Parse(id);
        account.Name = name;
    }

    private async Task<JsonObject> PostAsync(string url, JsonObject body, CancellationToken ct)
    {
        var uri = CheckSecure(url);
        using var response = await http.PostAsync(uri, AmberJson.Body(body), ct);
        CheckSameOrigin(uri, response);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonObject json;
        try { json = string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? []; }
        catch (JsonException) { json = []; }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(json["errorMessage"]?.GetValue<string>() ?? L.F("core.auth.external.serverstatus", (int)response.StatusCode));
        return json;
    }
}

/// <summary>下载 authlib-injector，并生成启动外置账号需要的 JVM 参数。</summary>
public sealed class AuthlibInjector(HttpClient http, string cacheDir)
{
    private const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const string MirrorLatestUrl = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";

    public async Task<string> EnsureJarAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var existing = Directory.EnumerateFiles(cacheDir, "authlib-injector-*.jar").OrderDescending().FirstOrDefault();
        JsonNode? latest = null;
        foreach (var url in new[] { LatestUrl, MirrorLatestUrl })
        {
            try
            {
                latest = JsonNode.Parse(await http.GetStringAsync(url, ct));
                break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        }
        if (latest is null)
        {
            // 联不上更新地址时，手里有哪个版本就用哪个。
            return existing ?? throw new InvalidOperationException(L.T("core.auth.injector.downloadfailed"));
        }

        var version = latest["version"]!.GetValue<string>();
        var target = Path.Combine(cacheDir, $"authlib-injector-{version}.jar");
        if (File.Exists(target)) return target;

        var bytes = await http.GetByteArrayAsync(latest["download_url"]!.GetValue<string>(), ct);
        var expected = latest["checksums"]?["sha256"]?.GetValue<string>();
        if (expected is not null && !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("core.auth.injector.checksumfailed"));
        await File.WriteAllBytesAsync(target, bytes, ct);
        return target;
    }

    public static IReadOnlyList<string> JvmArgs(string jarPath, string apiRoot, string metadataJson) =>
    [
        $"-javaagent:{jarPath}={apiRoot}",
        "-Dauthlibinjector.side=client",
        "-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(metadataJson)),
    ];
}
