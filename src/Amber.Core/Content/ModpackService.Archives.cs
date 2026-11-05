using Amber.Core.Localization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Install;

namespace Amber.Core.Content;

/// <summary>
/// 不是标准整合包、但大家常这样分发的压缩包：
/// 带启动器的压缩包（PCL 导出时可以把启动器和 modpack.mrpack 一起打包），
/// 直接压缩的 .minecraft 文件夹（里面有 versions/X/X.json）。
/// </summary>
public sealed partial class ModpackService
{
    /// <summary>直接压缩的游戏文件夹。</summary>
    internal const string GameFolder = "GameFolder";

    /// <summary>
    /// 打开整合包。带启动器的压缩包里真正的整合包是 modpack.mrpack 或 modpack.zip，先把它解到临时目录再打开；
    /// 压缩包里的启动器（exe）不会运行，也不会解出来。
    /// </summary>
    internal sealed class PackSource : IDisposable
    {
        public ZipArchive Zip { get; }
        private readonly string? _tempDir;

        private PackSource(ZipArchive zip, string? tempDir) => (Zip, _tempDir) = (zip, tempDir);

        public static PackSource Open(string path)
        {
            RejectOtherArchives(path);
            var zip = ZipFile.OpenRead(path);
            try
            {
                if (InnerPack(zip) is not { } inner) return new PackSource(zip, null);
                var tempDir = Path.Combine(Path.GetTempPath(), "amber-pack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var innerPath = Path.Combine(tempDir, inner.Name);
                inner.ExtractToFile(innerPath);
                zip.Dispose();
                try
                {
                    return new PackSource(ZipFile.OpenRead(innerPath), tempDir);
                }
                catch
                {
                    TryDelete(tempDir);
                    throw;
                }
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Zip.Dispose();
            if (_tempDir is not null) TryDelete(_tempDir);
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 带启动器的压缩包：根上或唯一一层文件夹里有 modpack.mrpack / modpack.zip，而外层自己不是整合包。
    /// </summary>
    internal static ZipArchiveEntry? InnerPack(ZipArchive zip)
    {
        if (PackRoot(zip) is var root && IndexFiles.Any(f => zip.GetEntry(root + f) is not null)) return null;
        if (FindEntry(zip, "mmc-pack.json") is not null || FindEntry(zip, "instance.cfg") is not null) return null;
        return zip.Entries
            .Where(e => e.Name is "modpack.mrpack" or "modpack.zip" && e.FullName.Count(c => c == '/') <= 1
                        && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();
    }

    /// <summary>RAR、7z 打不开，给一句能照着做的提示，而不是“文件损坏”。</summary>
    private static void RejectOtherArchives(string path)
    {
        Span<byte> head = stackalloc byte[6];
        int read;
        using (var stream = File.OpenRead(path)) read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var kind = read >= 4 && head[..4].SequenceEqual("Rar!"u8) ? "RAR"
            : read >= 6 && head.SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) ? "7z"
            : null;
        if (kind is not null) throw new InvalidDataException(L.F("core.content.modpack.notzip", kind));
    }

    /// <summary>压缩包里形如“前缀/versions/X/X.json”的版本文件，前缀最多两层（“整合包/.minecraft/”）。</summary>
    [GeneratedRegex(@"^(?<root>(?:[^/]+/){0,2})versions/(?<id>[^/]+)/\k<id>\.json$")]
    private static partial Regex VersionJsonEntry();

    /// <summary>游戏文件夹里能说明“这个版本开了版本隔离”的内容。</summary>
    private static readonly string[] IsolatedContent = ["mods/", "config/", "saves/", "resourcepacks/", "shaderpacks/", "options.txt"];

    /// <summary>
    /// 直接压缩的游戏文件夹。版本文件只用来认出游戏版本和加载器，加载器由 Amber 重新安装，
    /// 压缩包里的版本文件、库和 JVM 参数都不用（和其他格式一样，不让压缩包决定 Java 怎么启动）。
    /// 有多个版本时，优先选版本目录里带模组的那个。
    /// </summary>
    internal static ModpackInfo? InspectGameFolder(ZipArchive zip)
    {
        var candidates = new List<(string Root, string Id, ZipArchiveEntry Entry)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
            if (VersionJsonEntry().Match(entry.FullName) is { Success: true } m) candidates.Add((m.Groups["root"].Value, m.Groups["id"].Value, entry));
        }
        if (candidates.Count == 0) return null;

        bool Has(string prefix) => zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal));
        var picked = candidates
            .OrderByDescending(c => Has($"{c.Root}versions/{c.Id}/mods/"))
            .ThenBy(c => c.Root.Length)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();

        JsonObject json;
        try { json = ReadJson(picked.Entry); }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException(L.F("core.content.modpack.parsefailed", picked.Entry.FullName)); }
        var (loader, version) = FromLibraries(json);
        var mc = McVersionOf(json, picked.Id) ?? throw new InvalidDataException(L.F("core.content.modpack.foldernoversion", picked.Id));

        var versionDir = $"{picked.Root}versions/{picked.Id}/";
        var isolated = IsolatedContent.Any(c => Has(versionDir + c));
        return new ModpackInfo(GameFolder, picked.Id, null, mc, loader, version, 0, isolated ? versionDir : picked.Root, picked.Root);
    }

    /// <summary>从版本文件认出游戏版本：inheritsFrom、HMCL 的 patches、intermediary / forge 库、--fml.mcVersion、jar，最后才看 id 本身。</summary>
    internal static string? McVersionOf(JsonObject json, string id)
    {
        if (Str(json, "inheritsFrom") is { } inherits) return inherits;
        foreach (var patch in (json["patches"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(patch, "id") == "game" && Str(patch, "version") is { } v) return v;
        foreach (var lib in (json["libraries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var parts = (Str(lib, "name") ?? "").Split(':');
            if (parts.Length < 3) continue;
            switch ($"{parts[0]}:{parts[1]}")
            {
                case "net.fabricmc:intermediary" or "org.quiltmc:hashed": return parts[2];
                case "net.minecraftforge:forge" when parts[2].IndexOf('-') is var dash and > 0: return parts[2][..dash];
            }
        }
        if (json["arguments"]?["game"] is JsonArray args)
        {
            var list = args.Select(a => a is JsonValue value && value.TryGetValue<string>(out var s) ? s : null).ToList();
            var at = list.IndexOf("--fml.mcVersion");
            if (at >= 0 && at + 1 < list.Count && list[at + 1] is { Length: > 0 } fml) return fml;
        }
        if (Str(json, "jar") is { } jar && jar != id) return jar;
        if (Str(json, "clientVersion") is { } client) return client;
        return ReleaseId().IsMatch(id) ? id : null;
    }

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$|^\d{2}w\d{2}[a-z]$")]
    private static partial Regex ReleaseId();

    /// <summary>
    /// 游戏文件夹里不该带进新实例的东西：其他启动器的设置和账号文件（可能有登录令牌）、日志、
    /// 共享目录里的 versions / libraries / assets（Amber 重新下载），版本目录里原来的版本文件和 natives。
    /// </summary>
    internal static bool SkipGameFolderEntry(ModpackInfo info, string relative)
    {
        var first = relative.Split('/')[0];
        string[] always = ["logs", "crash-reports", "PCL", ".hmcl", "hmclversion.cfg", "instance-game-settings.json", "launcher_profiles.json",
            "launcher_accounts.json", "launcher_accounts_microsoft_store.json", "usercache.json", "hmcl.json", "natives"];
        if (always.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
        if (info.Overrides == info.Root)
        {
            string[] shared = ["versions", "libraries", "assets", "runtime", "webcache", "webcache2"];
            return relative.Contains('/') && shared.Contains(first, StringComparer.OrdinalIgnoreCase);
        }
        // 版本目录里原来的 X.json、X.jar、X-natives/。
        var id = info.Overrides![(info.Root.Length + "versions/".Length)..].TrimEnd('/');
        return relative.Equals(id + ".json", StringComparison.OrdinalIgnoreCase)
               || relative.Equals(id + ".jar", StringComparison.OrdinalIgnoreCase)
               || first.StartsWith(id + "-natives", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ExtractGameFolder(ZipArchive zip, ModpackInfo info, PackTarget pack) =>
        ExtractPrefix(zip, info.Overrides!, pack, skipWhere: relative => SkipGameFolderEntry(info, relative));

    private async Task ImportGameFolderFilesAsync(ZipArchive zip, ModpackInfo info, PackTarget pack, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new(L.T("core.content.modpack.extractgamefiles")));
        ExtractGameFolder(zip, info, pack);

        // Forge 版本里装了 OptiFine 时，OptiFine 是版本文件里的一个库：作为模组补进 mods。
        var versionJson = zip.GetEntry($"{info.Root}versions/{info.Name}/{info.Name}.json");
        if (versionJson is not null) await AddOptiFineModAsync(info, OptiFineLibrary(ReadJson(versionJson)), pack.WorkDir, progress, ct);
    }
}
