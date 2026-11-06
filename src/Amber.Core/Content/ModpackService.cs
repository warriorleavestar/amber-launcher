using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.Core.Content;

/// <summary>
/// 整合包概况。Overrides 是压缩包里要原样复制到游戏目录的那一层（MultiMC / HMCL / MCBBS / 游戏文件夹压缩包用）。
/// Root 是整合包套着的那一层文件夹（直接压缩整合包文件夹时会有），没有时为空。
/// </summary>
public sealed record ModpackInfo(string Format, string Name, string? Version, string McVersion, LoaderKind? Loader, string? LoaderVersion, int FileCount, string? Overrides = null, string Root = "");

/// <summary>
/// 导入 mrpack、CurseForge、MultiMC / Prism、HMCL、MCBBS 整合包，PCL 那种带启动器的压缩包和直接打包的 .minecraft 文件夹；
/// 导出 mrpack 和 CurseForge 整合包。
/// </summary>
public sealed partial class ModpackService(LoaderContext ctx, ModrinthProvider modrinth, CurseForgeProvider curseForge)
{
    public static ModpackInfo Inspect(string packPath)
    {
        using var source = PackSource.Open(packPath);
        return Inspect(source.Zip);
    }

    internal static ModpackInfo Inspect(ZipArchive zip)
    {
        var root = PackRoot(zip);
        if (zip.GetEntry(root + "modrinth.index.json") is { } mr)
        {
            var index = ReadJson(mr);
            var deps = index["dependencies"]?.AsObject() ?? [];
            var (loader, version) = MrpackLoader(deps);
            return new ModpackInfo("Modrinth", index["name"]?.GetValue<string>() ?? L.T("core.content.modpack.defaultname"), index["versionId"]?.GetValue<string>(),
                deps["minecraft"]?.GetValue<string>() ?? throw new InvalidDataException(L.T("core.content.modpack.nogameversion")), loader, version, (index["files"] as JsonArray)?.Count ?? 0, Root: root);
        }
        // MCBBS 整合包常常同时带一份 CurseForge 格式的 manifest.json，以 packmeta 为准；只有 manifest.json 但里面有 addons 的也是 MCBBS 格式。
        if (McbbsMeta(zip, root) is not null) return InspectOtherLaunchers(zip, root)!;
        if (zip.GetEntry(root + "manifest.json") is { } cf)
        {
            var manifest = ReadJson(cf);
            if (manifest["manifestType"]?.GetValue<string>() != "minecraftModpack") throw new InvalidDataException(L.T("core.content.modpack.notcurseforge"));
            var mc = manifest["minecraft"]!;
            var (loader, version) = CurseForgeLoader(mc);
            return new ModpackInfo("CurseForge", manifest["name"]?.GetValue<string>() ?? L.T("core.content.modpack.defaultname"), manifest["version"]?.GetValue<string>(),
                mc["version"]!.GetValue<string>(), loader, version, (manifest["files"] as JsonArray)?.Count ?? 0, Root: root);
        }
        return InspectOtherLaunchers(zip, root)
               ?? InspectGameFolder(zip)
               ?? throw new InvalidDataException(L.T("core.content.modpack.unrecognized"));
    }

    /// <summary>整合包的索引文件。</summary>
    private static readonly string[] IndexFiles = ["modrinth.index.json", "mcbbs.packmeta", "manifest.json", "modpack.json"];

    /// <summary>
    /// 索引文件不在压缩包根上、而是在唯一的一层文件夹里时（把整合包文件夹直接压缩），返回“文件夹/”；否则返回空。
    /// </summary>
    internal static string PackRoot(ZipArchive zip)
    {
        if (IndexFiles.Any(f => zip.GetEntry(f) is not null)) return "";
        var tops = zip.Entries
            .Where(e => !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
            .Select(e => e.FullName.IndexOf('/') is var slash and > 0 ? e.FullName[..(slash + 1)] : null)
            .Distinct().ToList();
        if (tops.Count != 1 || tops[0] is not { } top) return "";
        return IndexFiles.Any(f => zip.GetEntry(top + f) is not null) ? top : "";
    }

    /// <summary>MCBBS 的元数据：mcbbs.packmeta，或者带 addons 的 manifest.json。</summary>
    private static ZipArchiveEntry? McbbsMeta(ZipArchive zip, string root)
    {
        if (zip.GetEntry(root + "mcbbs.packmeta") is { } meta) return meta;
        if (zip.GetEntry(root + "manifest.json") is not { } manifest) return null;
        return ReadJson(manifest)["addons"] is JsonArray ? manifest : null;
    }

    /// <summary>安装整合包：先装加载器（版本名字用 name），再下载文件、释放 overrides 到版本隔离目录。</summary>
    /// <param name="onSkip">跳过了一个文件（整合包里的原路径）。进度消息会被后面的覆盖，界面要在导入完成后汇总时用这个。</param>
    public async Task<string> ImportAsync(string packPath, string name, Func<string, string> workDirForVersion, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default,
        Action<string>? onSkip = null)
    {
        using var source = PackSource.Open(packPath);
        var zip = source.Zip;
        var info = Inspect(zip);
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException(L.F("core.content.modpack.invalidname", name));
        InstanceInstallDirectory.CheckBase(ctx.Dir, name, info.McVersion);
        var versionDir = ctx.Dir.VersionDir(name);
        var workDir = InstanceInstallDirectory.Normalize(workDirForVersion(name));
        var baseDir = ctx.Dir.VersionDir(info.McVersion);
        // 游戏目录不能盖住原版依赖，也不能作为目标版本的祖先而把共享目录纳入回滚。
        if (InstanceInstallDirectory.Same(workDir, baseDir) || GameDirectory.IsWithin(workDir, baseDir) ||
            GameDirectory.IsWithin(baseDir, workDir) || GameDirectory.IsWithin(workDir, versionDir))
            throw new InvalidDataException(L.F("core.install.targetbase", name));
        using var targetDirectory = InstanceInstallDirectory.Reserve(versionDir);
        using var separateWorkDirectory = InstanceInstallDirectory.Same(workDir, versionDir) || GameDirectory.IsWithin(versionDir, workDir)
            ? null : InstanceInstallDirectory.Reserve(workDir);
        Directory.CreateDirectory(workDir);

        var id = await InstallBaseAsync(info, name, targetDirectory, progress, ct);

        // 可疑的文件名（像 8.3 短名、带冒号、设备名）跳过这一个文件并说一声，不让整个导入失败。
        var target = new PackTarget(workDir, ctx.Dir.VersionDir(id), id) { OnSkip = path =>
        {
            progress?.Report(new LoaderProgress(L.F("core.content.modpack.skipped", path)));
            onSkip?.Invoke(path);
        } };
        switch (info.Format)
        {
            case "Modrinth": await ImportMrpackFilesAsync(zip, info.Root, target, progress, ct); break;
            case "CurseForge": await ImportCurseForgeFilesAsync(zip, info.Root, target, progress, ct); break;
            case GameFolder: await ImportGameFolderFilesAsync(zip, info, target, progress, ct); break;
            default: await ImportOtherLauncherFilesAsync(zip, info, id, target, progress, ct); break;
        }
        separateWorkDirectory?.Complete();
        targetDirectory.Complete();
        return id;
    }

    private async Task<string> InstallBaseAsync(ModpackInfo info, string name, InstanceInstallDirectory targetDirectory, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        if (info.Loader is not { } kind)
        {
            await ctx.Vanilla.InstallAsync(info.McVersion, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, L.T("core.content.modpack.downloadgamefiles"), p)), ct);
            // 没有加载器的整合包也要一个独立版本，避免和原版共用目录。
            var json = new JsonObject { ["inheritsFrom"] = info.McVersion, ["type"] = "release" };
            LoaderInstallers.WriteVersionJson(ctx.Dir, name, json);
            return name;
        }
        var version = PackLoaderVersion(kind, info);
        var installer = LoaderInstallers.Create(kind, ctx);
        var loaderVersion = new LoaderVersion(kind, info.McVersion, version);
        return installer is ForgeLikeInstaller forge
            ? await forge.InstallReservedAsync(loaderVersion, name, targetDirectory, progress, ct)
            : await installer.InstallAsync(loaderVersion, name, progress, ct);
    }

    private static string PackLoaderVersion(LoaderKind kind, ModpackInfo info)
    {
        var version = info.LoaderVersion ?? throw new InvalidDataException(L.T("core.content.modpack.noloaderversion"));
        // 整合包里的 Forge 版本是 47.2.0，安装包路径要 1.20.1-47.2.0。
        if (kind == LoaderKind.Forge && !version.StartsWith(info.McVersion + "-", StringComparison.Ordinal))
            return $"{info.McVersion}-{version}";
        return version;
    }

    private async Task ImportMrpackFilesAsync(ZipArchive zip, string root, PackTarget pack, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var index = ReadJson(zip.GetEntry(root + "modrinth.index.json")!);
        var items = new List<DownloadItem>();
        foreach (var file in (index["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (file["env"]?["client"]?.GetValue<string>() == "unsupported") continue;
            if (pack.Resolve(file["path"]!.GetValue<string>()) is not { } target) continue;
            var sha1 = file["hashes"]?["sha1"]?.GetValue<string>() ?? throw new InvalidDataException(L.F("core.content.modpack.missingsha1", file["path"]));
            var url = (file["downloads"] as JsonArray)?.Select(u => u?.GetValue<string>()).FirstOrDefault(u => u is not null && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException(L.F("core.content.modpack.nohttpsurl", file["path"]));
            items.Add(new DownloadItem(url, target, sha1, file["fileSize"]?.GetValue<long>()));
        }
        progress?.Report(new(L.T("core.content.modpack.downloadfiles")));
        await ctx.Downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new(L.F("core.content.modpack.downloadprogress", p.CompletedFiles, p.TotalFiles), 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);

        progress?.Report(new(L.T("core.content.modpack.extractconfig")));
        ExtractPrefix(zip, root + "overrides/", pack);
        ExtractPrefix(zip, root + "client-overrides/", pack);
    }

    private async Task ImportCurseForgeFilesAsync(ZipArchive zip, string root, PackTarget pack, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var workDir = pack.WorkDir;
        var manifest = ReadJson(zip.GetEntry(root + "manifest.json")!);
        var ids = (manifest["files"] as JsonArray ?? []).OfType<JsonObject>().Select(f => f["fileID"]!.GetValue<long>()).ToList();
        progress?.Report(new(L.T("core.content.modpack.querymods")));
        var files = await curseForge.GetFilesByIdAsync(ids, ct);
        var missing = ids.Count - files.Count;

        var folders = await curseForge.GetProjectFoldersAsync(files.Select(f => long.Parse(f.ProjectId)).Distinct().ToList(), ct);
        var targets = files.Select(f => (File: f, TargetDir: Path.Combine(workDir, folders.GetValueOrDefault(f.ProjectId, "mods")))).ToList();
        var overrides = manifest["overrides"]?.GetValue<string>() ?? "overrides";
        var overridePrefix = root + overrides.TrimEnd('/') + "/";
        // 包里可能已经附带受限文件：只提前释放这些目标，让同一套 SHA-1 / 大小预检决定能否复用。
        // 普通文件仍在下载后由 overrides 覆盖；沿用独占实例目录和原有的路径检查、失败回滚。
        var manualPaths = targets.Where(t => t.File.RequiresManualDownload)
            .Select(t => Path.GetRelativePath(workDir, Path.Combine(t.TargetDir, ContentInstaller.SafeFileName(t.File.FileName))).Replace('\\', '/'))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (manualPaths.Count > 0)
            ExtractPrefix(zip, overridePrefix, pack with { OnSkip = null }, skipWhere: relative => !manualPaths.Contains(relative));
        var items = await ContentInstaller.PrepareDownloadItemsAsync(targets, ct);
        await ctx.Downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new(L.F("core.content.modpack.downloadprogress", p.CompletedFiles, p.TotalFiles), 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);

        ExtractPrefix(zip, overridePrefix, pack);
        if (missing > 0) throw new InvalidDataException(L.P("core.content.modpack.missingcursefiles", missing, missing));
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
            files.Add((JsonNode)new JsonObject
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
            files.Add((JsonNode)new JsonObject { ["projectID"] = long.Parse(remote.ProjectId), ["fileID"] = long.Parse(remote.FileId), ["required"] = true });
        }

        var loaders = new JsonArray();
        if (instance.Loader is "forge" or "neoforge" or "fabric" or "quilt")
            loaders.Add((JsonNode)new JsonObject { ["id"] = $"{instance.Loader}-{StripMcPrefix(instance.LoaderVersion, instance.McVersion)}", ["primary"] = true });
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
            {
                await using var json = new Utf8JsonWriter(writer, new JsonWriterOptions { Indented = true });
                index.WriteTo(json);
            }
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
        string[] skip = ["logs", "crash-reports", "natives", "saves", "screenshots", "versions", ".fabric", ".mixin.out", "downloads", ".cache", "debug", "backups", "local", InstanceSettings.FileName,
            // Amber 自己的回收目录、下载校验缓存和“安装未完成”标记不属于整合包。
            TrashService.TrashFolder, ".amber-hashes", Amber.Core.Game.GameDirectory.IncompleteMarker,
            // 烘焙记录（实验）里是本机的绝对路径和 classpath。
            Bake.BakeService.StateFolder];
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
            var other => throw new InvalidDataException(L.F("core.content.modpack.unsupportedloader", other)),
        };
    }

    private static void ExtractPrefix(ZipArchive zip, string prefix, PackTarget target, IReadOnlyCollection<string>? skip = null, Func<string, bool>? skipWhere = null)
    {
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            // 前缀后面多出来的斜杠（“overrides//mods/x.jar”）只是多余的分隔符，不是绝对路径：去掉再判断。
            var relative = entry.FullName[prefix.Length..].TrimStart('/', '\\');
            if (target.Resolve(relative) is not { } path) continue;
            // 过滤必须使用真正落盘的名字；否则“./launcher_accounts.json”“PCL./Setup.ini”等会绕过排除规则。
            var normalized = Path.GetRelativePath(Path.GetFullPath(target.WorkDir), path).Replace('\\', '/');
            if (skip?.Contains(normalized) == true || skipWhere?.Invoke(normalized) == true) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, true);
        }
    }

    /// <summary>
    /// 整合包文件释放到哪里。启动器自己的文件整合包不能提供，直接跳过：
    /// 任何位置的 amber-instance.json（实例设置，里面有启动前后执行的命令、Java 路径和 JVM 参数），
    /// 以及版本目录根上的 &lt;id&gt;.json、&lt;id&gt;.jar 和 natives/。大小写不敏感，路径里的每一段都查（“amber-instance.json/x”也算）。
    /// </summary>
    internal sealed record PackTarget(string WorkDir, string VersionDir, string Id)
    {
        /// <summary>跳过了一个可疑或属于启动器的文件（参数：整合包里的原路径）。</summary>
        public Action<string>? OnSkip { get; init; }

        /// <summary>
        /// 能释放时返回完整路径；启动器自己的文件、可疑的名字返回 null（并通知 OnSkip）；真正跳出目录的路径抛异常。
        /// 每一段先按 Windows 的规则规范化（去掉结尾的点和空格），再做短名、设备名和启动器文件的判断：
        /// 不然“AMBER-~1.JSO.”这种名字检查时不像短名，写文件时却被 Windows 当成 amber-instance.json 的短名。
        /// </summary>
        public string? Resolve(string relative)
        {
            // 以斜杠开头（绝对路径、\\server\share）是真的越界，不是可疑名字。
            if (relative.StartsWith('/') || relative.StartsWith('\\'))
                throw new InvalidDataException(L.F("core.content.modpack.unsafepath", relative));
            if (NormalizeSegments(relative) is not { } segments) return Skip(relative);
            var full = SafeJoin(WorkDir, string.Join('/', segments));
            return IsLauncherOwned(full) ? Skip(relative) : full;
        }

        private string? Skip(string relative)
        {
            OnSkip?.Invoke(relative);
            return null;
        }

        private bool IsLauncherOwned(string full)
        {
            var workDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkDir));
            var inWorkDir = Path.GetRelativePath(workDir, full).Split(Path.DirectorySeparatorChar);
            if (inWorkDir.Any(s => s.Equals(Launch.InstanceSettings.FileName, StringComparison.OrdinalIgnoreCase))) return true;
            // 烘焙记录（实验）决定启动用的主类、classpath 和 JVM 参数，只能由本机烘焙生成。
            if (inWorkDir[0].Equals(Bake.BakeService.StateFolder, StringComparison.OrdinalIgnoreCase)) return true;
            var versionDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(VersionDir));
            if (!full.StartsWith(versionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var first = Path.GetRelativePath(versionDir, full).Split(Path.DirectorySeparatorChar)[0].Normalize(System.Text.NormalizationForm.FormC);
            var id = Id.Normalize(System.Text.NormalizationForm.FormC);
            return first.Equals(id + ".json", StringComparison.OrdinalIgnoreCase) || first.Equals(id + ".jar", StringComparison.OrdinalIgnoreCase)
                   || first.Equals("natives", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 把整合包里的路径按段规范化：去掉空段和“.”，每段去掉结尾的点和空格（Windows 打开文件时也会这么做）。
    /// 有可疑的段时返回 null：带冒号（备用数据流，“x::$DATA”就是 x 本身）、设备名（CON、NUL、COM1…）、
    /// 像 8.3 短文件名（“AMBER-~1.JSO”）、含非法字符、规范化后成了空的。“..”原样保留，交给 SafeJoin 判断有没有跳出目录。
    /// </summary>
    internal static List<string>? NormalizeSegments(string relative)
    {
        var result = new List<string>();
        foreach (var piece in relative.Split('/', '\\'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..") { result.Add(piece); continue; }
            // 统一成 NFC：macOS 的文件系统把 NFC 和 NFD 当成同一个名字，不统一的话启动器文件的检查可能被绕过。
            var raw = piece.IsNormalized(System.Text.NormalizationForm.FormC) ? piece : piece.Normalize(System.Text.NormalizationForm.FormC);
            var segment = raw.TrimEnd('.', ' ');
            if (segment.Length == 0 || segment.Contains(':') || segment.IndexOfAny(InvalidNameChars) >= 0
                || IsDeviceName(segment) || ShortName().IsMatch(segment)) return null;
            result.Add(segment);
        }
        return result.Count == 0 ? null : result;
    }

    // 按 Windows 的规则判断（别的系统上 GetInvalidFileNameChars 只有 \0 和 /）：整合包要在哪都能装、导出后在 Windows 上也能用。
    private static readonly char[] InvalidNameChars =
        [.. Enumerable.Range(0, 32).Select(i => (char)i), '"', '<', '>', '|', '?', '*', ':', '/', '\\'];

    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows 的设备名（带不带扩展名都算，“nul.txt”也是 NUL）。</summary>
    internal static bool IsDeviceName(string segment) => DeviceNames.Contains(segment.Split('.')[0].TrimEnd(' '));

    /// <summary>整合包里的路径不可信：拒绝任何能跳出目标目录的路径（真正的越界，抛异常）。</summary>
    public static string SafeJoin(string root, string relative)
    {
        // ZIP 条目的分隔符不受当前系统影响；Unix 上也必须拒绝 Windows 绝对路径和反斜杠穿越。
        if (string.IsNullOrWhiteSpace(relative) || relative[0] is '/' or '\\' ||
            (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':'))
            throw new InvalidDataException(L.F("core.content.modpack.unsafepath", relative));
        var rootFull = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException(L.F("core.content.modpack.unsafepath", relative));
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        // 区分大小写的文件系统上（Linux），“../INST/x” 会落到和 root 只差大小写的另一个目录：只在 Windows 上忽略大小写。
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, comparison))
            throw new InvalidDataException(L.F("core.content.modpack.unsafepath", relative));
        return full;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^.]{1,6}~\d+(\.[^.]{0,3})?$")]
    private static partial System.Text.RegularExpressions.Regex ShortName();

    private static JsonObject ReadJson(ZipArchiveEntry entry)
    {
        var text = ArchiveMetadata.ReadText(entry, ArchiveMetadata.MaxPackMetadataBytes);
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException(L.F("core.content.modpack.parsefailed", entry.FullName));
    }
}
