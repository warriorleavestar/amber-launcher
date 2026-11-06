using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Install;
using Amber.Core.Launch;

namespace Amber.Core.Content;

/// <summary>
/// 其他启动器的整合包和实例压缩包：
/// MultiMC / Prism Launcher 导出的实例（instance.cfg + mmc-pack.json，游戏文件在 .minecraft/ 或 minecraft/），
/// HMCL 整合包（modpack.json + minecraft/pack.json，游戏文件在 minecraft/），
/// MCBBS 整合包（mcbbs.packmeta，游戏文件在 overrides/）。
/// </summary>
public sealed partial class ModpackService
{
    private const string MultiMc = "MultiMC";
    private const string Hmcl = "HMCL";
    private const string Mcbbs = "MCBBS";

    /// <summary>按文件名找条目，允许套一层文件夹（直接压缩实例文件夹时很常见）。取路径最短的一个。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.Entries
            .Where(e => !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)
                        && (e.FullName == name || e.FullName.EndsWith("/" + name, StringComparison.Ordinal))
                        && e.FullName.Count(c => c == '/') <= 1)
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();

    private static string PrefixOf(ZipArchiveEntry entry) => entry.FullName[..^entry.Name.Length];

    private static ModpackInfo? InspectOtherLaunchers(ZipArchive zip, string root)
    {
        if (McbbsMeta(zip, root) is { } mcbbs)
        {
            var meta = ReadJson(mcbbs);
            var (mc, loader, version) = FromAddons(meta["addons"] as JsonArray);
            return new ModpackInfo(Mcbbs, Str(meta, "name") ?? L.T("core.content.modpack.defaultname"), Str(meta, "version"),
                mc ?? throw new InvalidDataException(L.T("core.content.modpack.mcbbsnoversion")), loader, version,
                (meta["files"] as JsonArray)?.Count ?? 0, root + "overrides/", root);
        }

        if (zip.GetEntry(root + "modpack.json") is { } hmcl)
        {
            var meta = ReadJson(hmcl);
            var (mc, loader, version) = FromAddons(meta["addons"] as JsonArray);
            if (loader is null && zip.GetEntry(root + "minecraft/pack.json") is { } pack)
            {
                var json = ReadJson(pack);
                (loader, version) = FromLibraries(json);
                mc ??= Str(json, "inheritsFrom") ?? Str(json, "jar");
            }
            mc = Str(meta, "gameVersion") ?? mc;
            return new ModpackInfo(Hmcl, Str(meta, "name") ?? L.T("core.content.modpack.defaultname"), Str(meta, "version"),
                mc ?? throw new InvalidDataException(L.T("core.content.modpack.hmclnoversion")), loader, version, 0, root + "minecraft/", root);
        }

        var mmcPack = FindEntry(zip, "mmc-pack.json");
        var cfg = FindEntry(zip, "instance.cfg");
        if (mmcPack is null && cfg is null) return null;
        root = PrefixOf(mmcPack ?? cfg!);
        var settings = cfg is null ? [] : ReadInstanceCfg(cfg);

        string? game = null;
        LoaderKind? kind = null;
        string? loaderVersion = null;
        if (mmcPack is not null)
        {
            foreach (var c in (ReadJson(mmcPack)["components"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var uid = Str(c, "uid");
                var v = Str(c, "version") ?? Str(c, "cachedVersion");
                LoaderKind? found = uid switch
                {
                    "net.minecraft" => null,
                    "net.fabricmc.fabric-loader" => LoaderKind.Fabric,
                    "org.quiltmc.quilt-loader" => LoaderKind.Quilt,
                    "net.minecraftforge" => LoaderKind.Forge,
                    "net.neoforged" => LoaderKind.NeoForge,
                    "com.mumfrey.liteloader" => throw new InvalidDataException(L.T("core.content.modpack.liteloaderunsupported")),
                    // LWJGL、intermediary 这类组件安装加载器时会自动带上。
                    _ => null,
                };
                if (uid == "net.minecraft") game = v;
                else if (found is not null) (kind, loaderVersion) = (found, v);
            }
        }
        game ??= settings.GetValueOrDefault("IntendedVersion");
        var gameRoot = zip.Entries.Any(e => e.FullName.StartsWith(root + ".minecraft/", StringComparison.Ordinal)) ? root + ".minecraft/" : root + "minecraft/";
        return new ModpackInfo(MultiMc, settings.GetValueOrDefault("name") ?? (root.Length > 0 ? root.TrimEnd('/') : L.T("core.content.modpack.defaultinstancename")), null,
            game ?? throw new InvalidDataException(L.T("core.content.modpack.multimcnoversion")), kind, loaderVersion, 0, gameRoot);
    }

    /// <summary>HMCL 和 MCBBS 的 addons：[{id: game|forge|neoforge|fabric|quilt|optifine, version}]。</summary>
    private static (string? Mc, LoaderKind? Loader, string? Version) FromAddons(JsonArray? addons)
    {
        string? mc = null;
        LoaderKind? loader = null;
        string? version = null;
        LoaderKind? optifine = null;
        string? optifineVersion = null;
        foreach (var a in (addons ?? []).OfType<JsonObject>())
        {
            var v = Str(a, "version");
            switch (Str(a, "id"))
            {
                case "game": mc = v; break;
                case "forge": (loader, version) = (LoaderKind.Forge, v); break;
                case "neoforge": (loader, version) = (LoaderKind.NeoForge, v); break;
                case "fabric": (loader, version) = (LoaderKind.Fabric, v); break;
                case "quilt": (loader, version) = (LoaderKind.Quilt, v); break;
                case "optifine": (optifine, optifineVersion) = (LoaderKind.OptiFine, v); break;
                case var other when other is not null && other != "game":
                    throw new InvalidDataException(L.F("core.content.modpack.unsupportedloader", other));
            }
        }
        // 只有 OptiFine 时装 OptiFine；和别的加载器同时出现时以那个加载器为准，OptiFine 要作为模组自己放进 mods。
        if (loader is null && optifine is not null) (loader, version) = (optifine, OptiFineVersion(optifineVersion, mc));
        return (mc, loader, version);
    }

    /// <summary>从版本 JSON 的库里认出加载器（HMCL 的 pack.json 没有 addons 时用）。</summary>
    internal static (LoaderKind? Loader, string? Version) FromLibraries(JsonObject versionJson)
    {
        LoaderKind? loader = null;
        string? version = null;
        string? optifine = null;
        foreach (var lib in (versionJson["libraries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var parts = (Str(lib, "name") ?? "").Split(':');
            if (parts.Length < 3) continue;
            switch ($"{parts[0]}:{parts[1]}")
            {
                case "net.fabricmc:fabric-loader": (loader, version) = (LoaderKind.Fabric, parts[2]); break;
                case "org.quiltmc:quilt-loader": (loader, version) = (LoaderKind.Quilt, parts[2]); break;
                case "net.neoforged:neoforge": (loader, version) = (LoaderKind.NeoForge, parts[2]); break;
                case "net.minecraftforge:forge" or "net.minecraftforge:fmlloader":
                    if (loader != LoaderKind.NeoForge) (loader, version) = (LoaderKind.Forge, parts[2]);
                    break;
                case "optifine:OptiFine": optifine = parts[2]; break;
            }
        }
        if (loader is null && optifine is not null)
        {
            // optifine:OptiFine:1.20.1_HD_U_I6 → HD_U_I6
            var underscore = optifine.IndexOf('_');
            return (LoaderKind.OptiFine, underscore > 0 ? optifine[(underscore + 1)..] : optifine);
        }
        return (loader, version);
    }

    private static string? OptiFineVersion(string? version, string? mc)
    {
        if (version is null) return null;
        if (mc is not null && version.StartsWith(mc + "_", StringComparison.Ordinal)) return version[(mc.Length + 1)..];
        return version;
    }

    /// <summary>instance.cfg 是 key=value 的文本，Prism 会加一个 [General] 段。</summary>
    private static Dictionary<string, string> ReadInstanceCfg(ZipArchiveEntry entry)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(ArchiveMetadata.ReadText(entry));
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('[') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\\\"", "\"");
            result[line[..eq].Trim()] = value;
        }
        return result;
    }

    private async Task ImportOtherLauncherFilesAsync(ZipArchive zip, ModpackInfo info, string id, PackTarget pack, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var workDir = pack.WorkDir;
        progress?.Report(new(L.T("core.content.modpack.extractgamefiles")));
        ExtractPrefix(zip, info.Overrides!, pack, skip: info.Format == Hmcl ? ["pack.json"] : []);

        switch (info.Format)
        {
            case MultiMc when FindEntry(zip, "instance.cfg") is { } cfg:
                ApplyMultiMcSettings(ReadInstanceCfg(cfg), id);
                break;
            case Mcbbs:
                var meta = ReadJson(McbbsMeta(zip, info.Root)!);
                await AddOptiFineModAsync(info, OptiFineAddon(meta["addons"] as JsonArray, info.McVersion), workDir, progress, ct);
                await DownloadMcbbsCurseFilesAsync(meta, workDir, progress, ct);
                break;
            case Hmcl:
                var addon = OptiFineAddon(ReadJson(zip.GetEntry(info.Root + "modpack.json")!)["addons"] as JsonArray, info.McVersion);
                if (addon is null && zip.GetEntry(info.Root + "minecraft/pack.json") is { } packJson) addon = OptiFineLibrary(ReadJson(packJson));
                await AddOptiFineModAsync(info, addon, workDir, progress, ct);
                break;
        }
    }

    /// <summary>Forge 整合包带 OptiFine 时，OptiFine 作为 Forge 模组放进 mods（包里已经有就不重复下载）。</summary>
    private async Task AddOptiFineModAsync(ModpackInfo info, string? optifine, string workDir, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        if (optifine is null || info.Loader != LoaderKind.Forge) return;
        var mods = Path.Combine(workDir, "mods");
        if (Directory.Exists(mods) && Directory.EnumerateFiles(mods, "*OptiFine*.jar").Any()) return;
        progress?.Report(new(L.T("core.install.optifine.download")));
        Directory.CreateDirectory(mods);
        // 版本号来自整合包：拼成文件名前先确认是单个文件名，并且在 mods 里。
        var name = Game.GameDirectory.CheckId($"OptiFine_{info.McVersion}_{optifine}.jar");
        await new OptiFineInstaller(ctx).DownloadJarAsync(new LoaderVersion(LoaderKind.OptiFine, info.McVersion, optifine),
            Game.GameDirectory.Contained(mods, name), ct);
    }

    private static string? OptiFineAddon(JsonArray? addons, string mc) =>
        OptiFineVersion((addons ?? []).OfType<JsonObject>().Where(a => Str(a, "id") == "optifine").Select(a => Str(a, "version")).FirstOrDefault(), mc);

    private static string? OptiFineLibrary(JsonObject versionJson)
    {
        var name = (versionJson["libraries"] as JsonArray ?? []).OfType<JsonObject>().Select(l => Str(l, "name"))
            .FirstOrDefault(n => n?.StartsWith("optifine:OptiFine:", StringComparison.Ordinal) == true);
        if (name is null) return null;
        var v = name["optifine:OptiFine:".Length..];
        var underscore = v.IndexOf('_');
        return underscore > 0 ? v[(underscore + 1)..] : v;
    }

    /// <summary>
    /// 把 MultiMC / Prism 实例自己的内存和窗口大小带过来（只在实例里勾了覆盖时）。
    /// JVM 参数不带：它能让 Java 加载任意代理或在出错时执行命令，只能由用户在本机设置里填。
    /// </summary>
    private void ApplyMultiMcSettings(Dictionary<string, string> cfg, string id)
    {
        var settings = InstanceSettings.Load(ctx.Dir, id);
        ApplyMultiMcCfg(cfg, settings);
        settings.Save(ctx.Dir, id);
    }

    internal static void ApplyMultiMcCfg(Dictionary<string, string> cfg, InstanceSettings settings)
    {
        if (cfg.GetValueOrDefault("OverrideMemory") == "true" && int.TryParse(cfg.GetValueOrDefault("MaxMemAlloc"), out var mb) && mb > 0)
        {
            settings.AutoMemory = false;
            settings.MaxMemoryMb = mb;
        }
        if (cfg.GetValueOrDefault("OverrideWindow") == "true")
        {
            if (int.TryParse(cfg.GetValueOrDefault("MinecraftWinWidth"), out var w) && w > 0) settings.WindowWidth = w;
            if (int.TryParse(cfg.GetValueOrDefault("MinecraftWinHeight"), out var h) && h > 0) settings.WindowHeight = h;
        }
    }

    /// <summary>MCBBS 整合包里 type 为 curse 的文件要从 CurseForge 下载；其他文件都在 overrides 里。</summary>
    private async Task DownloadMcbbsCurseFilesAsync(JsonObject meta, string workDir, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var curse = (meta["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => Str(f, "type") == "curse" && f["fileID"] is not null).ToList();
        if (curse.Count == 0) return;
        progress?.Report(new(L.T("core.content.modpack.querymods")));
        var ids = curse.Select(f => f["fileID"]!.GetValue<long>()).ToList();
        var files = await curseForge.GetFilesByIdAsync(ids, ct);
        var folders = await curseForge.GetProjectFoldersAsync(files.Select(f => long.Parse(f.ProjectId)).Distinct().ToList(), ct);
        var items = await ContentInstaller.PrepareDownloadItemsAsync(files.Select(f => (f, Path.Combine(workDir, folders.GetValueOrDefault(f.ProjectId, "mods")))), ct);
        await ctx.Downloader.DownloadAllAsync(items, new Progress<Net.DownloadProgress>(p => progress?.Report(new(L.F("core.content.modpack.downloadprogress", p.CompletedFiles, p.TotalFiles), 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);
        if (ids.Count > files.Count) throw new InvalidDataException(L.P("core.content.modpack.missingcursefiles", ids.Count - files.Count, ids.Count - files.Count));
    }

    private static string? Str(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
