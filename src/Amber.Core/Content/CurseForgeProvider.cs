using Amber.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Content;

/// <summary>CurseForge API。Key 只来自用户配置；没有 Key 时所有请求都抛 <see cref="MissingApiKeyException"/>。</summary>
public sealed class CurseForgeProvider(HttpClient http, Func<string?> apiKey, string apiRoot = CurseForgeProvider.DefaultApiRoot) : IContentProvider
{
    public const string DefaultApiRoot = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;

    public ContentSource Source => ContentSource.CurseForge;

    /// <summary>Key 为空但地址不是官方（用户自己配的代理）时也放行。</summary>
    private bool CanRequest => !string.IsNullOrWhiteSpace(apiKey()) || apiRoot != DefaultApiRoot;

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = AmberJson.Body(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        if (!CanRequest) throw new MissingApiKeyException();
        var request = new HttpRequestMessage(method, apiRoot + path);
        if (apiKey() is { Length: > 0 } key) request.Headers.Add("x-api-key", key.Trim());
        return request;
    }

    public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        var path = $"/mods/search?gameId={MinecraftGameId}&classId={ClassId(q.Type)}&sortField=2&sortOrder=desc" +
                   $"&searchFilter={Uri.EscapeDataString(q.Text)}&index={q.Offset}&pageSize={Math.Min(q.Limit, 50)}";
        if (!string.IsNullOrEmpty(q.GameVersion)) path += "&gameVersion=" + Uri.EscapeDataString(q.GameVersion);
        if (q.Type == ContentType.Mod && LoaderCode(q.Loader) is { } loader) path += $"&modLoaderType={loader}";

        using var doc = await GetAsync(path, ct);
        var root = doc.RootElement;
        var total = root.TryGetProperty("pagination", out var page) && page.TryGetProperty("totalCount", out var t) ? t.GetInt32() : 0;
        return new SearchResult(root.GetProperty("data").EnumerateArray().Select(ParseProject).ToList(), total);
    }

    public async Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/mods/{projectId}", ct);
        return ParseProject(doc.RootElement.GetProperty("data"));
    }

    public async Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var path = $"/mods/{projectId}/files?pageSize=50";
        if (!string.IsNullOrEmpty(gameVersion)) path += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
        if (LoaderCode(loader) is { } code) path += $"&modLoaderType={code}";
        using var doc = await GetAsync(path, ct);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(ParseFile).OrderByDescending(f => f.Date).ToList();
    }

    public async Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/mods/{projectId}/files/{fileId}", ct);
        return ParseFile(doc.RootElement.GetProperty("data"));
    }

    /// <summary>批量取文件信息（CurseForge 整合包里只有 projectID + fileID）。</summary>
    public async Task<IReadOnlyList<ContentFile>> GetFilesByIdAsync(IReadOnlyCollection<long> fileIds, CancellationToken ct = default)
    {
        if (fileIds.Count == 0) return [];
        using var doc = await PostAsync("/mods/files", new JsonObject { ["fileIds"] = new JsonArray(fileIds.Select(i => (JsonNode?)i).ToArray()) }, ct);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(ParseFile).ToList();
    }

    /// <summary>批量查项目分类，返回 项目 id → 这类文件该放的文件夹（CurseForge 整合包里混着模组、资源包和光影）。</summary>
    public async Task<Dictionary<string, string>> GetProjectFoldersAsync(IReadOnlyCollection<long> modIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        if (modIds.Count == 0) return result;
        using var doc = await PostAsync("/mods", new JsonObject { ["modIds"] = new JsonArray(modIds.Select(i => (JsonNode?)i).ToArray()) }, ct);
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var classId = m.TryGetProperty("classId", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 6;
            result[m.GetProperty("id").GetInt64().ToString()] = classId switch
            {
                12 => "resourcepacks",
                6552 => "shaderpacks",
                _ => "mods",
            };
        }
        return result;
    }

    /// <summary>按指纹识别本地文件。返回指纹 → 文件。</summary>
    public async Task<Dictionary<uint, ContentFile>> IdentifyByFingerprintAsync(IReadOnlyCollection<uint> fingerprints, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, ContentFile>();
        if (fingerprints.Count == 0) return result;
        using var doc = await PostAsync("/fingerprints", new JsonObject { ["fingerprints"] = new JsonArray(fingerprints.Select(i => (JsonNode?)i).ToArray()) }, ct);
        foreach (var match in doc.RootElement.GetProperty("data").GetProperty("exactMatches").EnumerateArray())
        {
            var file = ParseFile(match.GetProperty("file"));
            if (match.GetProperty("file").TryGetProperty("fileFingerprint", out var fp)) result[fp.GetUInt32()] = file;
        }
        return result;
    }

    private ContentProject ParseProject(JsonElement m)
    {
        var authors = m.TryGetProperty("authors", out var a) && a.GetArrayLength() > 0 ? a[0].GetProperty("name").GetString() : null;
        var icon = m.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object && logo.TryGetProperty("thumbnailUrl", out var thumb) ? thumb.GetString() : null;
        var url = m.TryGetProperty("links", out var links) && links.TryGetProperty("websiteUrl", out var w) ? w.GetString() : null;
        return new ContentProject(
            Source,
            m.GetProperty("id").GetInt64().ToString(),
            m.GetProperty("name").GetString() ?? "",
            m.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            authors,
            icon,
            m.TryGetProperty("downloadCount", out var d) ? (long)d.GetDouble() : 0,
            url);
    }

    private ContentFile ParseFile(JsonElement f)
    {
        var id = f.GetProperty("id").GetInt64();
        var fileName = f.GetProperty("fileName").GetString()!;
        var url = f.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String ? du.GetString()! : ForgeCdnUrl(id, fileName);

        string? sha1 = null;
        if (f.TryGetProperty("hashes", out var hashes))
        {
            foreach (var h in hashes.EnumerateArray())
            {
                if (h.GetProperty("algo").GetInt32() == 1) sha1 = h.GetProperty("value").GetString();
            }
        }

        var gameVersions = new List<string>();
        var loaders = new List<string>();
        foreach (var g in f.TryGetProperty("gameVersions", out var gv) ? gv.EnumerateArray() : default)
        {
            var text = g.GetString() ?? "";
            if (text.Length > 0 && (char.IsDigit(text[0]))) gameVersions.Add(text);
            else if (text is "Forge" or "Fabric" or "Quilt" or "NeoForge") loaders.Add(text.ToLowerInvariant());
        }

        var deps = new List<ContentDependency>();
        foreach (var d in f.TryGetProperty("dependencies", out var dn) ? dn.EnumerateArray() : default)
        {
            var kind = d.GetProperty("relationType").GetInt32() switch
            {
                3 => DependencyKind.Required,
                5 => DependencyKind.Incompatible,
                1 => DependencyKind.Embedded,
                _ => DependencyKind.Optional,
            };
            deps.Add(new ContentDependency(Source, d.GetProperty("modId").GetInt64().ToString(), null, kind));
        }

        return new ContentFile(
            Source,
            f.GetProperty("modId").GetInt64().ToString(),
            id.ToString(),
            f.TryGetProperty("displayName", out var name) ? name.GetString() ?? fileName : fileName,
            fileName,
            url,
            sha1,
            f.TryGetProperty("fileLength", out var len) ? len.GetInt64() : 0,
            gameVersions,
            loaders,
            f.TryGetProperty("releaseType", out var rt) ? rt.GetInt32() switch { 1 => "release", 2 => "beta", _ => "alpha" } : "release",
            f.TryGetProperty("fileDate", out var date) && date.TryGetDateTimeOffset(out var parsed) ? parsed : default,
            deps);
    }

    /// <summary>作者关闭第三方下载时 downloadUrl 为空，这个公开 CDN 路径通常仍然可用。</summary>
    private static string ForgeCdnUrl(long id, string fileName) =>
        $"https://edge.forgecdn.net/files/{id / 1000}/{id % 1000:D3}/{Uri.EscapeDataString(fileName)}";

    private static int ClassId(ContentType type) => type switch
    {
        ContentType.Mod => 6,
        ContentType.ResourcePack => 12,
        ContentType.Shader => 6552,
        ContentType.Modpack => 4471,
        ContentType.Datapack => 6945,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static int? LoaderCode(string? loader) => loader switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null,
    };

    /// <summary>CurseForge 指纹：去掉空白字节后的 Murmur2（种子 1）。</summary>
    public static uint Fingerprint(ReadOnlySpan<byte> data)
    {
        var filtered = new byte[data.Length];
        var length = 0;
        foreach (var b in data)
        {
            if (b is not (9 or 10 or 13 or 32)) filtered[length++] = b;
        }

        const uint m = 0x5bd1e995;
        const int r = 24;
        var h = 1u ^ (uint)length;
        var i = 0;
        while (length - i >= 4)
        {
            var k = (uint)(filtered[i] | filtered[i + 1] << 8 | filtered[i + 2] << 16 | filtered[i + 3] << 24);
            k *= m;
            k ^= k >> r;
            k *= m;
            h *= m;
            h ^= k;
            i += 4;
        }
        switch (length - i)
        {
            case 3:
                h ^= (uint)filtered[i + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)filtered[i + 1] << 8;
                goto case 1;
            case 1:
                h ^= filtered[i];
                h *= m;
                break;
        }
        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }
}
