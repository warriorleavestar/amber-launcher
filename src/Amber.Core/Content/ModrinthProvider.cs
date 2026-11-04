using Amber.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Content;

/// <summary>Modrinth 公开 API v2。</summary>
public sealed class ModrinthProvider(HttpClient http, string apiRoot = "https://api.modrinth.com/v2") : IContentProvider
{
    public ContentSource Source => ContentSource.Modrinth;

    public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        var facets = new List<string[]> { new[] { $"project_type:{ProjectType(q.Type)}" } };
        if (!string.IsNullOrEmpty(q.GameVersion)) facets.Add([$"versions:{q.GameVersion}"]);
        // 资源包和光影的 loader 分类和模组不是一回事，只对模组和整合包过滤。
        // Quilt 能直接用 Fabric 模组，两种都搜（同一个数组里是“或”）。
        if (!string.IsNullOrEmpty(q.Loader) && q.Type is ContentType.Mod or ContentType.Modpack)
            facets.Add(q.Loader == "quilt" ? ["categories:quilt", "categories:fabric"] : [$"categories:{q.Loader}"]);

        var url = $"{apiRoot}/search?query={Uri.EscapeDataString(q.Text)}&limit={q.Limit}&offset={q.Offset}&index=relevance" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets, AmberJsonContext.Default.ListStringArray))}";
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var root = doc.RootElement;
        var items = root.GetProperty("hits").EnumerateArray().Select(h => new ContentProject(
            Source,
            h.GetProperty("project_id").GetString()!,
            h.GetProperty("title").GetString() ?? "",
            h.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            h.TryGetProperty("author", out var a) ? a.GetString() : null,
            h.TryGetProperty("icon_url", out var i) ? i.GetString() : null,
            h.TryGetProperty("downloads", out var n) ? n.GetInt64() : 0,
            h.TryGetProperty("slug", out var s) ? $"https://modrinth.com/project/{s.GetString()}" : null)).ToList();
        return new SearchResult(items, root.GetProperty("total_hits").GetInt32());
    }

    public async Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apiRoot}/project/{projectId}", ct));
        var p = doc.RootElement;
        return new ContentProject(
            Source,
            p.GetProperty("id").GetString()!,
            p.GetProperty("title").GetString() ?? "",
            p.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            null,
            p.TryGetProperty("icon_url", out var i) ? i.GetString() : null,
            p.TryGetProperty("downloads", out var n) ? n.GetInt64() : 0,
            $"https://modrinth.com/project/{p.GetProperty("slug").GetString()}");
    }

    public async Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var url = $"{apiRoot}/project/{projectId}/version?include_changelog=false";
        if (!string.IsNullOrEmpty(gameVersion)) url += "&game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }, AmberJsonContext.Default.StringArray));
        if (!string.IsNullOrEmpty(loader)) url += "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(loader == "quilt" ? new[] { "quilt", "fabric" } : new[] { loader }, AmberJsonContext.Default.StringArray));
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        return doc.RootElement.EnumerateArray().Select(ParseVersion).OfType<ContentFile>().OrderByDescending(f => f.Date).ToList();
    }

    public async Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{apiRoot}/version/{fileId}", ct));
        return ParseVersion(doc.RootElement) ?? throw new InvalidDataException($"版本 {fileId} 没有可下载文件");
    }

    /// <summary>按 SHA1 批量识别本地文件。没有匹配的文件不在结果里。</summary>
    public async Task<Dictionary<string, ContentFile>> IdentifyBySha1Async(IReadOnlyCollection<string> sha1s, CancellationToken ct = default)
    {
        if (sha1s.Count == 0) return [];
        using var response = await http.PostAsync($"{apiRoot}/version_files", AmberJson.Body(new JsonObject { ["hashes"] = new JsonArray(sha1s.Select(h => (JsonNode?)h).ToArray()), ["algorithm"] = "sha1" }), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseHashMap(doc.RootElement);
    }

    /// <summary>按 SHA1 查最新版本，返回值的键是传入的哈希。</summary>
    public async Task<Dictionary<string, ContentFile>> LatestBySha1Async(IReadOnlyCollection<string> sha1s, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        if (sha1s.Count == 0) return [];
        var body = new JsonObject { ["hashes"] = new JsonArray(sha1s.Select(h => (JsonNode?)h).ToArray()), ["algorithm"] = "sha1" };
        if (!string.IsNullOrEmpty(loader)) body["loaders"] = new JsonArray(loader);
        if (!string.IsNullOrEmpty(gameVersion)) body["game_versions"] = new JsonArray(gameVersion);
        using var response = await http.PostAsync($"{apiRoot}/version_files/update", AmberJson.Body(body), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseHashMap(doc.RootElement);
    }

    private Dictionary<string, ContentFile> ParseHashMap(JsonElement root)
    {
        var result = new Dictionary<string, ContentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.EnumerateObject())
        {
            if (ParseVersion(entry.Value) is { } file) result[entry.Name] = file;
        }
        return result;
    }

    private ContentFile? ParseVersion(JsonElement v)
    {
        var files = v.GetProperty("files");
        if (files.GetArrayLength() == 0) return null;
        var file = files.EnumerateArray().FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined) file = files[0];

        var deps = new List<ContentDependency>();
        if (v.TryGetProperty("dependencies", out var depsNode))
        {
            foreach (var d in depsNode.EnumerateArray())
            {
                var kind = d.GetProperty("dependency_type").GetString() switch
                {
                    "required" => DependencyKind.Required,
                    "incompatible" => DependencyKind.Incompatible,
                    "embedded" => DependencyKind.Embedded,
                    _ => DependencyKind.Optional,
                };
                deps.Add(new ContentDependency(Source, StringOrNull(d, "project_id"), StringOrNull(d, "version_id"), kind));
            }
        }

        return new ContentFile(
            Source,
            v.GetProperty("project_id").GetString()!,
            v.GetProperty("id").GetString()!,
            StringOrNull(v, "name") ?? StringOrNull(v, "version_number") ?? "",
            file.GetProperty("filename").GetString()!,
            file.GetProperty("url").GetString()!,
            file.GetProperty("hashes").TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
            file.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            v.GetProperty("game_versions").EnumerateArray().Select(g => g.GetString()!).ToList(),
            v.GetProperty("loaders").EnumerateArray().Select(l => l.GetString()!).ToList(),
            StringOrNull(v, "version_type") ?? "release",
            v.TryGetProperty("date_published", out var date) && date.TryGetDateTimeOffset(out var parsed) ? parsed : default,
            deps);
    }

    private static string? StringOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string ProjectType(ContentType type) => type switch
    {
        ContentType.Mod => "mod",
        ContentType.ResourcePack => "resourcepack",
        ContentType.Shader => "shader",
        ContentType.Modpack => "modpack",
        ContentType.Datapack => "datapack",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
