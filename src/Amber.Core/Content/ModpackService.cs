using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.Core.Content;

/// <summary>整合包概况。Overrides 是压缩包里要原样复制到游戏目录的那一层（MultiMC / HMCL / MCBBS 用）。</summary>
public sealed record ModpackInfo(string Format, string Name, string? Version, string McVersion, LoaderKind? Loader, string? LoaderVersion, int FileCount, string? Overrides = null);

/// <summary>导入 mrpack、CurseForge、MultiMC / Prism、HMCL、MCBBS 整合包，导出 mrpack 和 CurseForge 整合包。</summary>
public sealed partial class ModpackService(LoaderContext ctx, ModrinthProvider modrinth, CurseForgeProvider curseForge)
{
    public static ModpackInfo Inspect(string packPath)
    {
        using var zip = ZipFile.OpenRead(packPath);
        if (zip.GetEntry("modrinth.index.json") is { } mr)
        {
            var index = ReadJson(mr);
            var deps = index["dependencies"]?.AsObject() ?? [];
            var (loader, version) = MrpackLoader(deps);
            return new ModpackInfo("Modrinth", index["name"]?.GetValue<string>() ?? "整合包", index["versionId"]?.GetValue<string>(),
                deps["minecraft"]?.GetValue<string>() ?? throw new InvalidDataException("整合包没有指定游戏版本"), loader, version, (index["files"] as JsonArray)?.Count ?? 0);
        }
        // MCBBS 整合包常常同时带一份 CurseForge 格式的 manifest.json，以 packmeta 为准。
        if (zip.GetEntry("mcbbs.packmeta") is not null) return InspectOtherLaunchers(zip)!;
        if (zip.GetEntry("manifest.json") is { } cf)
        {
            var manifest = ReadJson(cf);
            if (manifest["manifestType"]?.GetValue<string>() != "minecraftModpack") throw new InvalidDataException("不是 CurseForge 整合包");
            var mc = manifest["minecraft"]!;
            var (loader, version) = CurseForgeLoader(mc);
            return new ModpackInfo("CurseForge", manifest["name"]?.GetValue<string>() ?? "整合包", manifest["version"]?.GetValue<string>(),
                mc["version"]!.GetValue<string>(), loader, version, (manifest["files"] as JsonArray)?.Count ?? 0);
        }
        return InspectOtherLaunchers(zip)
               ?? throw new InvalidDataException("不认识这个整合包：支持 Modrinth（mrpack）、CurseForge、MultiMC / Prism 实例、HMCL 和 MCBBS 整合包");
    }

    /// <summary>安装整合包：先装加载器（版本名字用 name），再下载文件、释放 overrides 到版本隔离目录。</summary>
    public async Task<string> ImportAsync(string packPath, string name, Func<string, string> workDirForVersion, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default)
    {
        var info = Inspect(packPath);
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"不能用这个名字：{name}");

        var id = await InstallBaseAsync(info, name, progress, ct);
        var workDir = workDirForVersion(id);
        Directory.CreateDirectory(workDir);

        using var zip = ZipFile.OpenRead(packPath);
        switch (info.Format)
        {
            case "Modrinth": await ImportMrpackFilesAsync(zip, workDir, progress, ct); break;
            case "CurseForge": await ImportCurseForgeFilesAsync(zip, workDir, progress, ct); break;
            default: await ImportOtherLauncherFilesAsync(zip, info, id, workDir, progress, ct); break;
        }
        return id;
    }

    private async Task<string> InstallBaseAsync(ModpackInfo info, string name, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        if (info.Loader is not { } kind)
        {
            await ctx.Vanilla.InstallAsync(info.McVersion, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, "下载游戏文件", p)), ct);
            // 没有加载器的整合包也要一个独立版本，避免和原版共用目录。
            var json = new JsonObject { ["inheritsFrom"] = info.McVersion, ["type"] = "release" };
            LoaderInstallers.WriteVersionJson(ctx.Dir, name, json);
            return name;
        }
        var version = PackLoaderVersion(kind, info);
        return await LoaderInstallers.Create(kind, ctx).InstallAsync(new LoaderVersion(kind, info.McVersion, version), name, progress, ct);
    }

    private static string PackLoaderVersion(LoaderKind kind, ModpackInfo info)
    {
        var version = info.LoaderVersion ?? throw new InvalidDataException("整合包没有指定加载器版本");
        // 整合包里的 Forge 版本是 47.2.0，安装包路径要 1.20.1-47.2.0。
        if (kind == LoaderKind.Forge && !version.StartsWith(info.McVersion + "-", StringComparison.Ordinal))
            return $"{info.McVersion}-{version}";
        return version;
    }

    private async Task ImportMrpackFilesAsync(ZipArchive zip, string workDir, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var index = ReadJson(zip.GetEntry("modrinth.index.json")!);
        var items = new List<DownloadItem>();
        foreach (var file in (index["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (file["env"]?["client"]?.GetValue<string>() == "unsupported") continue;
            var target = SafeJoin(workDir, file["path"]!.GetValue<string>());
            var sha1 = file["hashes"]?["sha1"]?.GetValue<string>() ?? throw new InvalidDataException($"{file["path"]} 没有 SHA1，mrpack 规范要求必须有");
            var url = (file["downloads"] as JsonArray)?.Select(u => u?.GetValue<string>()).FirstOrDefault(u => u is not null && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException($"{file["path"]} 没有可用的 https 下载地址");
            items.Add(new DownloadItem(url, target, sha1, file["fileSize"]?.GetValue<long>()));
        }
        progress?.Report(new("下载整合包文件"));
        await ctx.Downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new($"下载整合包文件 {p.CompletedFiles}/{p.TotalFiles}", 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);

        progress?.Report(new("释放配置文件"));
        ExtractPrefix(zip, "overrides/", workDir);
        ExtractPrefix(zip, "client-overrides/", workDir);
    }

    private async Task ImportCurseForgeFilesAsync(ZipArchive zip, string workDir, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var manifest = ReadJson(zip.GetEntry("manifest.json")!);
        var ids = (manifest["files"] as JsonArray ?? []).OfType<JsonObject>().Select(f => f["fileID"]!.GetValue<long>()).ToList();
        progress?.Report(new("查询模组信息"));
        var files = await curseForge.GetFilesByIdAsync(ids, ct);
        var missing = ids.Count - files.Count;

        var folders = await curseForge.GetProjectFoldersAsync(files.Select(f => long.Parse(f.ProjectId)).Distinct().ToList(), ct);
        var items = files.Select(f => ContentInstaller.ToItem(f, Path.Combine(workDir, folders.GetValueOrDefault(f.ProjectId, "mods")))).ToList();
        await ctx.Downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new($"下载整合包文件 {p.CompletedFiles}/{p.TotalFiles}", 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);

        var overrides = manifest["overrides"]?.GetValue<string>() ?? "overrides";
        ExtractPrefix(zip, overrides.TrimEnd('/') + "/", workDir);
        if (missing > 0) throw new InvalidDataException($"整合包有 {missing} 个文件在 CurseForge 上查不到，其余已安装");
    }

    /// <summary>导出 mrpack：能在 Modrinth 上按哈希识别的模组写成下载项，其余放进 overrides。</summary>
    public async Task ExportMrpackAsync(GameInstance instance, string outputPath, string name, string version, IReadOnlyCollection<string> includeRelative, CancellationToken ct = default)
    {
        var modFiles = EnumerateIncluded(instance.WorkDir, includeRelative).ToList();
        var candidates = modFiles.Where(f => f.Relative.StartsWith("mods/", StringComparison.Ordinal) || f.Relative.StartsWith("resourcepacks/", StringComparison.Ordinal) || f.Relative.StartsWith("shaderpacks/", StringComparison.Ordinal)).ToList();
        var hashes = new Dictionary<string, string>();
        foreach (var f in candidates) hashes[f.Relative] = await Downloader.Sha1Async(f.FullPath, ct);
        var identified = await modrinth.IdentifyBySha1Async(hashes.Values.Distinct().ToList(), ct);

        var files = new JsonArray();
        var linked = new HashSet<string>();
        foreach (var f in candidates)
        {
            if (!identified.TryGetValue(hashes[f.Relative], out var remote)) continue;
            linked.Add(f.Relative);
            var sha512 = await Sha512Async(f.FullPath, ct);
            files.Add(new JsonObject
            {
                ["path"] = f.Relative,
                ["hashes"] = new JsonObject { ["sha1"] = hashes[f.Relative], ["sha512"] = sha512 },
                ["downloads"] = new JsonArray(remote.Url),
                ["fileSize"] = new FileInfo(f.FullPath).Length,
            });
        }

        var dependencies = new JsonObject { ["minecraft"] = instance.McVersion };
        switch (instance.Loader)
        {
            case "fabric": dependencies["fabric-loader"] = instance.LoaderVersion; break;
            case "quilt": dependencies["quilt-loader"] = instance.LoaderVersion; break;
            case "forge": dependencies["forge"] = StripMcPrefix(instance.LoaderVersion, instance.McVersion); break;
            case "neoforge": dependencies["neoforge"] = instance.LoaderVersion; break;
        }
        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = version,
            ["name"] = name,
            ["files"] = files,
            ["dependencies"] = dependencies,
        };

        await WritePackAsync(outputPath, "modrinth.index.json", index, modFiles.Where(f => !linked.Contains(f.Relative)), ct);
    }

    /// <summary>导出 CurseForge 整合包：有 Key 且能按指纹识别的写进 manifest，其余放进 overrides。</summary>
    public async Task ExportCurseForgeAsync(GameInstance instance, string outputPath, string name, string version, string author, IReadOnlyCollection<string> includeRelative, CancellationToken ct = default)
    {
        var all = EnumerateIncluded(instance.WorkDir, includeRelative).ToList();
        var mods = all.Where(f => f.Relative.StartsWith("mods/", StringComparison.Ordinal)).ToList();

        var fingerprints = new Dictionary<uint, string>();
        foreach (var f in mods) fingerprints[CurseForgeProvider.Fingerprint(await File.ReadAllBytesAsync(f.FullPath, ct))] = f.Relative;
        Dictionary<uint, ContentFile> matches;
        try
        {
            matches = await curseForge.IdentifyByFingerprintAsync(fingerprints.Keys, ct);
        }
        catch (MissingApiKeyException)
        {
            matches = [];
        }

        var linked = new HashSet<string>();
        var files = new JsonArray();
        foreach (var (fp, remote) in matches)
        {
            linked.Add(fingerprints[fp]);
            files.Add(new JsonObject { ["projectID"] = long.Parse(remote.ProjectId), ["fileID"] = long.Parse(remote.FileId), ["required"] = true });
        }

        var loaders = new JsonArray();
        if (instance.Loader is "forge" or "neoforge" or "fabric" or "quilt")
            loaders.Add(new JsonObject { ["id"] = $"{instance.Loader}-{StripMcPrefix(instance.LoaderVersion, instance.McVersion)}", ["primary"] = true });
        var manifest = new JsonObject
        {
            ["minecraft"] = new JsonObject { ["version"] = instance.McVersion, ["modLoaders"] = loaders },
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = name,
            ["version"] = version,
            ["author"] = author,
            ["files"] = files,
            ["overrides"] = "overrides",
        };
        await WritePackAsync(outputPath, "manifest.json", manifest, all.Where(f => !linked.Contains(f.Relative)), ct);
    }

    private static async Task WritePackAsync(string outputPath, string indexName, JsonObject index, IEnumerable<(string Relative, string FullPath)> overrides, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temp = outputPath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(indexName);
            await using (var writer = entry.Open())
                await JsonSerializer.SerializeAsync(writer, index, new JsonSerializerOptions { WriteIndented = true }, ct);
            foreach (var (relative, full) in overrides)
            {
                ct.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(full, "overrides/" + relative, CompressionLevel.Optimal);
            }
        }
        File.Move(temp, outputPath, true);
    }

    private static IEnumerable<(string Relative, string FullPath)> EnumerateIncluded(string workDir, IReadOnlyCollection<string> include)
    {
        foreach (var rel in include)
        {
            var full = Path.Combine(workDir, rel);
            if (File.Exists(full))
            {
                yield return (rel.Replace('\\', '/'), full);
            }
            else if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    // 禁用的模组不算进整合包。
                    if (file.EndsWith(ModManager.DisabledSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    yield return (Path.GetRelativePath(workDir, file).Replace('\\', '/'), file);
                }
            }
        }
    }

    /// <summary>实例目录下可以选择导出的一级内容。</summary>
    public static List<string> ExportCandidates(string workDir)
    {
        if (!Directory.Exists(workDir)) return [];
        string[] skip = ["logs", "crash-reports", "natives", "saves", "screenshots", "versions", ".fabric", ".mixin.out", "downloads", ".cache", "debug", "backups", "local", InstanceSettings.FileName];
        var result = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(workDir))
        {
            var name = Path.GetFileName(path);
            if (skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(path) && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.StartsWith(Path.GetFileName(workDir), StringComparison.Ordinal)
                                      || name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(name);
        }
        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? StripMcPrefix(string? version, string mc)
    {
        if (version is null) return null;
        var v = version.StartsWith(mc + "-", StringComparison.Ordinal) ? version[(mc.Length + 1)..] : version;
        return v.EndsWith("-" + mc, StringComparison.Ordinal) ? v[..^(mc.Length + 1)] : v;
    }

    private static async Task<string> Sha512Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA512.HashDataAsync(stream, ct));
    }

    private static (LoaderKind?, string?) MrpackLoader(JsonObject deps)
    {
        if (deps["fabric-loader"] is { } fabric) return (LoaderKind.Fabric, fabric.GetValue<string>());
        if (deps["quilt-loader"] is { } quilt) return (LoaderKind.Quilt, quilt.GetValue<string>());
        if (deps["neoforge"] is { } neo) return (LoaderKind.NeoForge, neo.GetValue<string>());
        if (deps["forge"] is { } forge) return (LoaderKind.Forge, forge.GetValue<string>());
        return (null, null);
    }

    private static (LoaderKind?, string?) CurseForgeLoader(JsonNode mc)
    {
        var loaders = mc["modLoaders"] as JsonArray ?? [];
        var primary = loaders.FirstOrDefault(l => l?["primary"]?.GetValue<bool>() == true) ?? loaders.FirstOrDefault();
        var id = primary?["id"]?.GetValue<string>();
        if (id is null) return (null, null);
        var dash = id.IndexOf('-');
        if (dash < 0) return (null, null);
        var version = id[(dash + 1)..];
        return id[..dash] switch
        {
            "forge" => (LoaderKind.Forge, version),
            "neoforge" => (LoaderKind.NeoForge, version),
            "fabric" => (LoaderKind.Fabric, version),
            "quilt" => (LoaderKind.Quilt, version),
            var other => throw new InvalidDataException($"不支持的加载器：{other}"),
        };
    }

    private static void ExtractPrefix(ZipArchive zip, string prefix, string target, IReadOnlyCollection<string>? skip = null)
    {
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[prefix.Length..];
            if (skip?.Contains(relative) == true) continue;
            var path = SafeJoin(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    /// <summary>整合包里的路径不可信：拒绝任何能跳出目标目录的路径。</summary>
    public static string SafeJoin(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"整合包里有不安全的路径：{relative}");
        return full;
    }

    private static JsonObject ReadJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException($"{entry.FullName} 解析失败");
    }
}
