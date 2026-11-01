using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Install;

/// <summary>Fabric 与 Quilt：元数据服务直接给出可用的版本 JSON，补全库即可。</summary>
public sealed class FabricLikeInstaller(LoaderKind kind, string metaRoot, LoaderContext ctx) : ILoaderInstaller
{
    public LoaderKind Kind { get; } = kind;

    public static FabricLikeInstaller Fabric(LoaderContext ctx) => new(LoaderKind.Fabric, "https://meta.fabricmc.net/v2", ctx);
    public static FabricLikeInstaller Quilt(LoaderContext ctx) => new(LoaderKind.Quilt, "https://meta.quiltmc.org/v3", ctx);

    public async Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default)
    {
        using var games = JsonDocument.Parse(await ctx.Downloader.GetStringAsync($"{metaRoot}/versions/game", ct));
        if (!games.RootElement.EnumerateArray().Any(g => g.GetProperty("version").GetString() == mcVersion)) return [];

        using var loaders = JsonDocument.Parse(await ctx.Downloader.GetStringAsync($"{metaRoot}/versions/loader", ct));
        return loaders.RootElement.EnumerateArray().Select(l =>
        {
            var version = l.GetProperty("version").GetString()!;
            // Quilt 没有 stable 字段，预发布版写在版本号里。
            var stable = l.TryGetProperty("stable", out var s) ? s.GetBoolean() : !version.Contains('-');
            return new LoaderVersion(Kind, mcVersion, version, stable);
        }).ToList();
    }

    public async Task<string> InstallAsync(LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(customId) ? version.DefaultId : customId.Trim();
        progress?.Report(new($"安装原版 {version.McVersion}"));
        await ctx.Vanilla.InstallAsync(version.McVersion, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, "下载原版文件", p)), ct);

        progress?.Report(new($"获取 {Kind} 配置"));
        var text = await ctx.Downloader.GetStringAsync($"{metaRoot}/versions/loader/{version.McVersion}/{version.Version}/profile/json", ct);
        var json = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException($"{Kind} 配置解析失败");
        LoaderInstallers.WriteVersionJson(ctx.Dir, id, json);

        await ctx.Vanilla.CompleteAsync(id, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, "下载加载器库", p)), ct);
        return id;
    }
}
