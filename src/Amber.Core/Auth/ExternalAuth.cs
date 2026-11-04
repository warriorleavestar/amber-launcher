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

/// <summary>外置登录（authlib-injector / Yggdrasil 协议）。密码只用于这一次请求，不保存。</summary>
public sealed class ExternalAuth(HttpClient http)
{
    /// <summary>按 authlib-injector 约定解析服务器地址：响应头可以把用户填的网址指向真正的 API 地址。</summary>
    public async Task<string> ResolveApiRootAsync(string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var locations) && locations.FirstOrDefault() is { } location)
        {
            var resolved = new Uri(response.RequestMessage?.RequestUri ?? new Uri(url), location).ToString();
            return resolved.TrimEnd('/');
        }
        return url.TrimEnd('/');
    }

    /// <summary>API 根地址返回的元数据原文，启动时要预取给 authlib-injector。</summary>
    public async Task<string> GetMetadataAsync(string apiRoot, CancellationToken ct = default) =>
        await http.GetStringAsync(apiRoot + "/", ct);

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
        using var validate = await http.PostAsync(account.ServerUrl + "/authserver/validate", AmberJson.Body(new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = account.ClientToken,
        }), ct);
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
        var text = await http.GetStringAsync($"{account.ServerUrl}/sessionserver/session/minecraft/profile/{account.Uuid:N}", ct);
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
        using var response = await http.PostAsync(url, AmberJson.Body(body), ct);
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
