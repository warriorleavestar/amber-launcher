using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;
using Amber.Core.Net;

namespace Amber.Core.Install;

public enum LoaderKind { Fabric, Quilt, Forge, NeoForge, OptiFine }

/// <summary>某个游戏版本可安装的一个加载器版本。</summary>
/// <param name="Recommended">官方标成推荐的版本（Forge），自动选中时按它判断；Note 只是显示用的文字。</param>
public sealed record LoaderVersion(LoaderKind Kind, string McVersion, string Version, bool Stable = true, DateTimeOffset? Date = null, string? Note = null, bool Recommended = false)
{
    public string DefaultId => Kind switch
    {
        LoaderKind.Fabric => $"fabric-loader-{Version}-{McVersion}",
        LoaderKind.Quilt => $"quilt-loader-{Version}-{McVersion}",
        LoaderKind.Forge => $"{McVersion}-forge-{ForgeShort}",
        LoaderKind.NeoForge => $"neoforge-{Version}",
        LoaderKind.OptiFine => $"{McVersion}-OptiFine_{Version}",
        _ => Version,
    };

    /// <summary>Forge 版本串去掉前后的游戏版本（1.7.10-10.13.4.1614-1.7.10 → 10.13.4.1614）。</summary>
    private string ForgeShort
    {
        get
        {
            var s = Version;
            if (s.StartsWith(McVersion + "-", StringComparison.Ordinal)) s = s[(McVersion.Length + 1)..];
            if (s.EndsWith("-" + McVersion, StringComparison.Ordinal)) s = s[..^(McVersion.Length + 1)];
            return s;
        }
    }
}

public readonly record struct LoaderProgress(string Stage, double? Percent = null);

public interface ILoaderInstaller
{
    LoaderKind Kind { get; }

    /// <summary>列出适用于该游戏版本的加载器版本，最新的在前。</summary>
    Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default);

    /// <summary>安装并返回版本 id。customId 为空时用默认名字。原版部分和所有库都会一起补全。</summary>
    Task<string> InstallAsync(LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>加载器安装共用的依赖。JavaFor 按主版本号给出一个可用的 java.exe（处理器需要）。</summary>
public sealed record LoaderContext(
    Downloader Downloader,
    GameDirectory Dir,
    VanillaInstaller Vanilla,
    Func<int, CancellationToken, Task<string>> JavaFor);

public static class LoaderInstallers
{
    public static ILoaderInstaller Create(LoaderKind kind, LoaderContext ctx) => kind switch
    {
        LoaderKind.Fabric => FabricLikeInstaller.Fabric(ctx),
        LoaderKind.Quilt => FabricLikeInstaller.Quilt(ctx),
        LoaderKind.Forge => new ForgeInstaller(ctx),
        LoaderKind.NeoForge => new NeoForgeInstaller(ctx),
        LoaderKind.OptiFine => new OptiFineInstaller(ctx),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>把版本 JSON 写到 versions/&lt;id&gt;/，把里面的 id 改成目标名字。</summary>
    internal static void WriteVersionJson(GameDirectory dir, string id, JsonObject json)
    {
        json["id"] = id;
        var path = dir.VersionJsonPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void ReportFiles(IProgress<LoaderProgress>? progress, string stage, InstallProgress p)
    {
        if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
            progress?.Report(new($"{stage} {p.Files.CompletedFiles}/{p.Files.TotalFiles}", 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles));
    }
}
