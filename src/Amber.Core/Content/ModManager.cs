using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Net;

namespace Amber.Core.Content;

/// <summary>mods 文件夹里的一个模组。</summary>
public sealed record LocalMod(string Path, string FileName, bool Enabled, string? ModId, string Name, string? Version, string? Description, string? Authors, byte[]? Icon,
    ModRequirements? Requirements = null)
{
    public string DisplayName => Name;
}

/// <summary>管理实例的 mods 文件夹：列表、启用/禁用、删除、检查更新。</summary>
public sealed partial class ModManager(string modsDir)
{
    public const string DisabledSuffix = ".disabled";

    public string ModsDir { get; } = modsDir;

    public List<LocalMod> List()
    {
        if (!Directory.Exists(ModsDir)) return [];
        // 几百个模组的大整合包：并行读，读过的按大小和修改时间缓存（启用 / 禁用只改文件名，不用重读）。
        var mods = Directory.EnumerateFiles(ModsDir)
            .Where(IsModFile)
            .AsParallel()
            .Select(ReadCached)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // 这个文件夹里已经不在的文件（更新换了文件名、删了）从缓存里去掉，免得越积越多。
        var present = mods.Select(m => CacheKey(m.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = Path.GetFullPath(ModsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var key in Cache.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !present.Contains(key)) Cache.TryRemove(key, out _);
        return mods;
    }

    private sealed record CacheEntry(long Size, DateTime Modified, LocalMod Mod);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存的键：去掉 .disabled 的完整路径。</summary>
    private static string CacheKey(string path) =>
        path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? path[..^DisabledSuffix.Length] : path;

    private static LocalMod ReadCached(string path)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        var key = CacheKey(path);
        // 列文件和读文件之间文件被删掉 / 改名了（比如正在更新模组）：不缓存，按读不到处理。
        if (!info.Exists) return Read(path);
        if (Cache.TryGetValue(key, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc)
        {
            var fileName = System.IO.Path.GetFileName(path);
            return hit.Mod with { Path = path, FileName = fileName, Enabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) };
        }
        var mod = Read(path, out var cacheable);
        if (cacheable) Cache[key] = new CacheEntry(info.Length, info.LastWriteTimeUtc, mod);
        return mod;
    }

    /// <summary>只数文件不解压，启动页和自动内存用。</summary>
    public int CountEnabled() =>
        Directory.Exists(ModsDir) ? Directory.EnumerateFiles(ModsDir).Count(p => IsModFile(p) && !p.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)) : 0;

    public static bool IsModFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)) name = name[..^DisabledSuffix.Length];
        return name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>启用或禁用（改文件名加 .disabled），返回新路径。</summary>
    public static string SetEnabled(LocalMod mod, bool enabled)
    {
        if (mod.Enabled == enabled) return mod.Path;
        var target = enabled ? mod.Path[..^DisabledSuffix.Length] : mod.Path + DisabledSuffix;
        File.Move(mod.Path, target, true);
        return target;
    }

    /// <summary>读一个模组时可能遇到的问题：文件损坏或正被别的程序改动、元数据格式不对。都只影响这一个文件。</summary>
    private static bool IsBadMetadata(Exception e) =>
        e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException;

    public static LocalMod Read(string path) => Read(path, out _);

    /// <param name="cacheable">读的时候文件正被占用（比如还在下载、写入）时为 false：这次读不到不代表文件坏了，不能缓存。</param>
    private static LocalMod Read(string path, out bool cacheable)
    {
        cacheable = true;
        var fileName = System.IO.Path.GetFileName(path);
        var enabled = !fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        var fallbackName = enabled ? fileName : fileName[..^DisabledSuffix.Length];
        ModRequirements? requirements = null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            // 同一次打开里把兼容检查要的元数据也读了。
            try
            {
                requirements = ModCompat.ReadZip(zip, nested: true);
            }
            catch (Exception e) when (IsBadMetadata(e))
            {
                cacheable &= e is not (IOException or UnauthorizedAccessException);
            }
            var meta = ReadFabric(zip) ?? ReadQuilt(zip) ?? ReadToml(zip, "META-INF/neoforge.mods.toml") ?? ReadToml(zip, "META-INF/mods.toml") ?? ReadMcmodInfo(zip);
            if (meta is not null)
            {
                var version = meta.Version is null or "${file.jarVersion}" ? ManifestVersion(zip) : meta.Version;
                return new LocalMod(path, fileName, enabled, meta.Id, string.IsNullOrWhiteSpace(meta.Name) ? fallbackName : meta.Name, version, meta.Description, meta.Authors, ReadIcon(zip, meta.IconPath), requirements);
            }
        }
        catch (Exception e) when (IsBadMetadata(e))
        {
            cacheable &= e is not (IOException or UnauthorizedAccessException);
            // 损坏的压缩包、写得不规范的元数据也照样显示出来，方便用户发现并删除。
        }
        return new LocalMod(path, fileName, enabled, null, fallbackName, null, null, null, null, requirements);
    }

    private sealed record ModMeta(string? Id, string? Name, string? Version, string? Description, string? Authors, string? IconPath);

    private static ModMeta? ReadFabric(ZipArchive zip)
    {
        if (ReadJson(zip, "fabric.mod.json") is not JsonObject json) return null;
        return new ModMeta(Str(json["id"]), Str(json["name"]) ?? Str(json["id"]), Str(json["version"]), Str(json["description"]), People(json["authors"]), IconFrom(json["icon"]));
    }

    private static ModMeta? ReadQuilt(ZipArchive zip)
    {
        if (ReadJson(zip, "quilt.mod.json")?["quilt_loader"] is not JsonObject loader) return null;
        var meta = loader["metadata"];
        return new ModMeta(Str(loader["id"]), Str(meta?["name"]) ?? Str(loader["id"]), Str(loader["version"]), Str(meta?["description"]),
            meta?["contributors"] is JsonObject people ? string.Join(", ", people.Select(p => p.Key)) : null, IconFrom(meta?["icon"]));
    }

    private static ModMeta? ReadMcmodInfo(ZipArchive zip)
    {
        var json = ReadJson(zip, "mcmod.info");
        var first = json is JsonArray list ? list.FirstOrDefault() : json?["modList"] is JsonArray nested ? nested.FirstOrDefault() : null;
        if (first is null) return null;
        return new ModMeta(Str(first["modid"]), Str(first["name"]), Str(first["version"]), Str(first["description"]), People(first["authorList"]) ?? People(first["authors"]), Str(first["logoFile"]));
    }

    /// <summary>mods.toml 只取第一个 [[mods]] 段的简单键值，够展示用。</summary>
    private static ModMeta? ReadToml(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        var start = text.IndexOf("[[mods]]", StringComparison.Ordinal);
        if (start < 0) return null;
        var section = text[start..];
        var next = section.IndexOf("[[", 8, StringComparison.Ordinal);
        if (next > 0) section = section[..next];
        var top = text[..start];

        return new ModMeta(TomlValue(section, "modId"), TomlValue(section, "displayName"), TomlValue(section, "version"),
            TomlValue(section, "description")?.Trim(), TomlValue(section, "authors") ?? TomlValue(top, "authors"), TomlValue(section, "logoFile") ?? TomlValue(top, "logoFile"));
    }

    private static string? TomlValue(string text, string key)
    {
        var triple = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*('''|"""""")(.*?)\1", RegexOptions.Singleline);
        if (triple.Success) return triple.Groups[2].Value;
        var single = Regex.Match(text, $@"(?m)^\s*{key}\s*=\s*[""'](.*?)[""']");
        return single.Success ? single.Groups[1].Value : null;
    }

    private static string? ManifestVersion(ZipArchive zip)
    {
        var entry = zip.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase)) return line["Implementation-Version:".Length..].Trim();
        }
        return null;
    }

    private static byte[]? ReadIcon(ZipArchive zip, string? path)
    {
        if (string.IsNullOrEmpty(path) || zip.GetEntry(path.TrimStart('/')) is not { } entry || entry.Length > 2_000_000) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static JsonNode? ReadJson(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        try
        {
            return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? People(JsonNode? node) => node switch
    {
        JsonArray list => string.Join(", ", list.Select(p => Str(p) ?? Str(p?["name"])).Where(s => !string.IsNullOrEmpty(s))),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => null,
    };

    /// <summary>fabric.mod.json 的 icon 可以是字符串或 尺寸 → 路径 的对象，取最大的。</summary>
    private static string? IconFrom(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => o.OrderByDescending(p => int.TryParse(p.Key, out var n) ? n : 0).Select(p => Str(p.Value)).FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// 检查更新：先问 Modrinth（按 SHA1），剩下的问 CurseForge（按指纹，有 Key 才查）。
    /// 只有版本真的不同才算更新。
    /// </summary>
    public static async Task<List<ModUpdate>> CheckUpdatesAsync(
        IReadOnlyList<LocalMod> mods, ModrinthProvider modrinth, CurseForgeProvider? curseForge, string mcVersion, string? loader, CancellationToken ct = default)
    {
        var updates = new List<ModUpdate>();
        var bySha1 = new Dictionary<string, LocalMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            bySha1[await Downloader.Sha1Async(mod.Path, ct)] = mod;

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = await modrinth.IdentifyBySha1Async(bySha1.Keys, ct);
        known.UnionWith(current.Keys);
        var latest = await modrinth.LatestBySha1Async(bySha1.Keys, mcVersion, loader, ct);
        foreach (var (hash, file) in latest)
        {
            if (!string.Equals(file.Sha1, hash, StringComparison.OrdinalIgnoreCase))
                updates.Add(new ModUpdate(bySha1[hash].Path, file));
        }

        if (curseForge is null) return updates;
        var unknown = bySha1.Where(p => !known.Contains(p.Key)).Select(p => p.Value).ToList();
        var fingerprints = new Dictionary<uint, LocalMod>();
        foreach (var mod in unknown)
            fingerprints[CurseForgeProvider.Fingerprint(await File.ReadAllBytesAsync(mod.Path, ct))] = mod;
        var matches = await curseForge.IdentifyByFingerprintAsync(fingerprints.Keys, ct);
        foreach (var (fp, file) in matches)
        {
            var files = await curseForge.GetFilesAsync(file.ProjectId, mcVersion, loader, ct);
            var newest = files.PickPreferred();
            if (newest is not null && newest.FileId != file.FileId && newest.Date > file.Date)
                updates.Add(new ModUpdate(fingerprints[fp].Path, newest));
        }
        return updates;
    }

    /// <summary>下载新版本后再删旧文件；保持原来的启用状态。</summary>
    public static async Task ApplyUpdateAsync(ModUpdate update, Downloader downloader, CancellationToken ct = default)
    {
        var dir = System.IO.Path.GetDirectoryName(update.LocalPath)!;
        var disabled = update.LocalPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        var item = ContentInstaller.ToItem(update.Latest, dir);
        await downloader.DownloadOneAsync(item, ct);
        var target = disabled ? item.LocalPath + DisabledSuffix : item.LocalPath;
        if (disabled) File.Move(item.LocalPath, target, true);
        if (!string.Equals(target, update.LocalPath, StringComparison.OrdinalIgnoreCase)) File.Delete(update.LocalPath);
    }
}
