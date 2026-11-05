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

    public string VersionDir(string id) => Path.Combine(VersionsDir, id);
    public string VersionJsonPath(string id) => Path.Combine(VersionDir(id), id + ".json");
    public string VersionJarPath(string id) => Path.Combine(VersionDir(id), id + ".jar");
    public string NativesDir(string id) => Path.Combine(VersionDir(id), "natives");
    public string AssetIndexPath(string id) => Path.Combine(AssetsDir, "indexes", id + ".json");
    public string LoggingConfigPath(string fileId) => Path.Combine(AssetsDir, "log_configs", fileId);
    public string AssetObjectPath(string hash) => Path.Combine(AssetsDir, "objects", hash[..2], hash);
    public string LibraryPath(string relative) => Path.Combine(LibrariesDir, relative.Replace('/', Path.DirectorySeparatorChar));

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
            if (File.Exists(VersionJsonPath(id))) yield return id;
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
