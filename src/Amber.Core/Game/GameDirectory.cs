using Amber.Core.Localization;
using Amber.Core;
using System.Text.Json;

namespace Amber.Core.Game;

/// <summary>一个 .minecraft 目录的路径约定与版本读取。</summary>
public sealed class GameDirectory(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string VersionsDir => Path.Combine(Root, "versions");
    public string LibrariesDir => Path.Combine(Root, "libraries");
    public string AssetsDir => Path.Combine(Root, "assets");

    // 版本文件里的 id、jar、assetIndex.id、logging.file.id、库路径都可能来自整合包，不可信：
    // 每个路径都经 Contained 拼接，拼出来必须还在对应的目录里，不然抛异常（宁可装不上，也不往别处写）。
    public string VersionDir(string id) => Contained(VersionsDir, CheckId(id));
    public string VersionJsonPath(string id) => Contained(VersionDir(id), id + ".json");
    public string VersionJarPath(string id) => Contained(VersionDir(id), id + ".jar");
    public string NativesDir(string id) => Contained(VersionDir(id), "natives");
    public string AssetIndexPath(string id) => Contained(Path.Combine(AssetsDir, "indexes"), CheckId(id) + ".json");
    public string LoggingConfigPath(string fileId) => Contained(Path.Combine(AssetsDir, "log_configs"), CheckId(fileId));
    public string AssetObjectPath(string hash) =>
        hash.Length == 40 && hash.All(char.IsAsciiHexDigit)
            ? Path.Combine(AssetsDir, "objects", hash[..2], hash)
            : throw new InvalidDataException(L.F("core.game.directory.unsafepath", hash));
    public string LibraryPath(string relative) => Contained(LibrariesDir, relative);

    /// <summary>
    /// 版本 id（以及 assetIndex.id、logging.file.id 这类单个文件名）：不能是空的、“.”“..”，不能带路径分隔符、冒号或别的文件名非法字符。
    /// </summary>
    public static string CheckId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(['/', '\\', ':']) >= 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id != id.TrimEnd('.', ' '))
            throw new InvalidDataException(L.F("core.game.directory.unsafepath", id));
        return id;
    }

    /// <summary>
    /// 在 baseDir 下拼接相对路径，结果必须在 baseDir 里面。拒绝绝对路径、盘符、UNC、带冒号（备用数据流）、“..” 段。
    /// </summary>
    public static string Contained(string baseDir, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\') || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(s => s == ".."))
            throw new InvalidDataException(L.F("core.game.directory.unsafepath", relative));
        var root = Path.GetFullPath(baseDir);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, full))
            throw new InvalidDataException(L.F("core.game.directory.unsafepath", relative));
        return full;
    }

    /// <summary>
    /// full 是不是在 root 里面（不等于 root 本身）。root 可以是盘符根目录（“D:\”）：那时不能再拼一个分隔符，
    /// 否则“D:\\”谁都不匹配。full 和 root 都要先规范化。
    /// </summary>
    public static bool IsWithin(string root, string full)
    {
        root = Path.GetFullPath(root);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前系统的 Minecraft 官方默认目录。</summary>
    public static string DefaultRoot() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "minecraft")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");

    /// <summary>列出已安装（有版本 JSON）的版本 id。</summary>
    public IEnumerable<string> ListInstalled()
    {
        if (!Directory.Exists(VersionsDir)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
        {
            var id = Path.GetFileName(dir);
            string json;
            try { json = VersionJsonPath(id); }
            catch (InvalidDataException) { continue; }
            if (File.Exists(json)) yield return id;
        }
    }

    /// <summary>安装开始时放进版本文件夹、装完才删的标记：还在说明上次安装没走完（取消、失败、断电）。</summary>
    public const string IncompleteMarker = ".amber-install-incomplete";

    public void MarkInstallStarted(string id)
    {
        Directory.CreateDirectory(VersionDir(id));
        File.WriteAllText(Path.Combine(VersionDir(id), IncompleteMarker), "");
    }

    public void MarkInstallFinished(string id)
    {
        var marker = Path.Combine(VersionDir(id), IncompleteMarker);
        if (File.Exists(marker)) File.Delete(marker);
    }

    /// <summary>实例状态：安装没走完、版本文件读不了（含继承的版本），或者正常。</summary>
    public InstanceState StateOf(string id)
    {
        if (File.Exists(Path.Combine(VersionDir(id), IncompleteMarker))) return InstanceState.Incomplete;
        try
        {
            ResolveVersion(id);
            return InstanceState.Ready;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            return InstanceState.Broken;
        }
    }

    public VersionJson ReadVersion(string id)
    {
        var path = VersionJsonPath(id);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, Json.Options.Info<VersionJson>())
               ?? throw new InvalidDataException(L.F("core.game.directory.parsefailed", path));
    }

    /// <summary>读取版本并沿 inheritsFrom 链合并成一个完整版本（加载器版本依赖原版时需要）。</summary>
    public VersionJson ResolveVersion(string id)
    {
        var chain = new List<VersionJson>();
        var seen = new HashSet<string>();
        for (string? current = id; current is not null; current = chain[^1].InheritsFrom)
        {
            if (!seen.Add(current)) throw new InvalidDataException(L.F("core.game.directory.inheritancecycle", current));
            chain.Add(ReadVersion(current));
        }

        // 从最底层的父版本开始，逐层用子版本覆盖。
        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--) merged = Merge(merged, chain[i]);
        merged.Id = id;
        merged.InheritsFrom = null;
        return merged;
    }

    private static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        // 子版本的库放前面，同名（不含版本号）库以子版本为准。
        var libraries = new List<Library>(child.Libraries);
        var childKeys = child.Libraries.Select(l => LibraryKey(l.Name)).ToHashSet();
        libraries.AddRange(parent.Libraries.Where(l => !childKeys.Contains(LibraryKey(l.Name))));

        VersionArguments? arguments = null;
        if (parent.Arguments is not null || child.Arguments is not null)
        {
            arguments = new VersionArguments
            {
                Game = [.. parent.Arguments?.Game ?? [], .. child.Arguments?.Game ?? []],
                Jvm = [.. parent.Arguments?.Jvm ?? [], .. child.Arguments?.Jvm ?? []],
            };
        }

        return new VersionJson
        {
            Id = child.Id,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            Jar = child.Jar ?? parent.Jar ?? parent.Id,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = arguments,
            Libraries = libraries,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Assets = child.Assets ?? parent.Assets,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            AmberForge = child.AmberForge ?? parent.AmberForge,
            AmberLegacyLibs = child.AmberLegacyLibs ?? parent.AmberLegacyLibs,
        };
    }

    /// <summary>group:artifact[:classifier]，用于判断两个库是否是“同一个库的不同版本”。</summary>
    private static string LibraryKey(string name)
    {
        var parts = name.Split('@')[0].Split(':');
        return parts.Length switch
        {
            >= 4 => $"{parts[0]}:{parts[1]}:{parts[3]}",
            >= 2 => $"{parts[0]}:{parts[1]}",
            _ => name,
        };
    }
}

/// <summary>实例在列表里显示的状态。</summary>
public enum InstanceState
{
    Ready,
    /// <summary>上次安装被取消或失败了；补全文件可以接着装。</summary>
    Incomplete,
    /// <summary>版本文件读不了（损坏或继承的原版不在了）。</summary>
    Broken,
}
