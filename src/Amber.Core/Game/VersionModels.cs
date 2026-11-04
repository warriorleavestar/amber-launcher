using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Game;

/// <summary>版本 JSON（versions/&lt;id&gt;/&lt;id&gt;.json）的数据模型，只包含启动器需要的字段。</summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mainClass")] public string? MainClass { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }

    /// <summary>1.12.2 及更早版本使用的单行游戏参数。</summary>
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }

    /// <summary>1.13+ 的结构化参数，元素可能是字符串或带 rules 的对象，所以保留原始 JSON。</summary>
    [JsonPropertyName("arguments")] public VersionArguments? Arguments { get; set; }

    [JsonPropertyName("libraries")] public List<Library> Libraries { get; set; } = [];
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string? Assets { get; set; }
    [JsonPropertyName("downloads")] public Dictionary<string, DownloadInfo>? Downloads { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRequirement? JavaVersion { get; set; }
    [JsonPropertyName("logging")] public Dictionary<string, LoggingInfo>? Logging { get; set; }
    [JsonPropertyName("releaseTime")] public string? ReleaseTime { get; set; }

    /// <summary>Amber 装的 1.5.2 及更早的 Forge（改 jar 的方式），记下版本号，用来识别加载器。</summary>
    [JsonPropertyName("amberForge")] public string? AmberForge { get; set; }

    /// <summary>这些老 Forge 启动前要放进游戏目录 lib/ 的 FML 库。</summary>
    [JsonPropertyName("amberLegacyLibs")] public List<Amber.Core.Install.FmlLibrary>? AmberLegacyLibs { get; set; }
}

public sealed class VersionArguments
{
    [JsonPropertyName("game")] public List<JsonElement> Game { get; set; } = [];
    [JsonPropertyName("jvm")] public List<JsonElement> Jvm { get; set; } = [];
}

public sealed class Library
{
    /// <summary>Maven 坐标，形如 group:artifact:version[:classifier][@ext]。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<Rule>? Rules { get; set; }

    /// <summary>旧格式的 natives：系统名 → classifier（可含 ${arch}）。</summary>
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("extract")] public ExtractRule? Extract { get; set; }

    /// <summary>Fabric/Forge 等第三方库常用：没有 downloads 时用 url + 坐标拼地址。</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")] public DownloadInfo? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class DownloadInfo
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class ExtractRule
{
    [JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
}

public sealed class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class JavaVersionRequirement
{
    [JsonPropertyName("component")] public string? Component { get; set; }
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public sealed class LoggingInfo
{
    [JsonPropertyName("argument")] public string? Argument { get; set; }
    [JsonPropertyName("file")] public LoggingFile? File { get; set; }
}

public sealed class LoggingFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}

public sealed class Rule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "allow";
    [JsonPropertyName("os")] public OsCondition? Os { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsCondition
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

/// <summary>assets/indexes/&lt;id&gt;.json</summary>
public sealed class AssetIndex
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = [];
    [JsonPropertyName("virtual")] public bool Virtual { get; set; }
    [JsonPropertyName("map_to_resources")] public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>version_manifest_v2.json</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")] public Dictionary<string, string> Latest { get; set; } = [];
    [JsonPropertyName("versions")] public List<ManifestEntry> Versions { get; set; } = [];
}

public sealed class ManifestEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    [JsonPropertyName("releaseTime")] public DateTimeOffset ReleaseTime { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
