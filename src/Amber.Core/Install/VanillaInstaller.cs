using Amber.Core;
using System.Text.Json;
using Amber.Core.Game;
using Amber.Core.Net;

namespace Amber.Core.Install;

public enum InstallStage { Manifest, VersionJson, AssetIndex, Files, Done }

public readonly record struct InstallProgress(InstallStage Stage, DownloadProgress Files);

/// <summary>原版安装与补全：写入版本 JSON，下载客户端、库、资源索引与资源文件。</summary>
public sealed class VanillaInstaller(Downloader downloader, GameDirectory dir)
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetBaseUrl = "https://resources.download.minecraft.net/";

    public RuleEvaluator Rules { get; init; } = RuleEvaluator.Current;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        var text = await downloader.GetStringAsync(ManifestUrl, ct);
        return JsonSerializer.Deserialize(text, Json.Options.Info<VersionManifest>())
               ?? throw new InvalidDataException("版本清单解析失败");
    }

    /// <summary>安装一个原版版本（已存在则只补全缺失文件）。</summary>
    public async Task InstallAsync(string versionId, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var jsonPath = dir.VersionJsonPath(versionId);
        if (!File.Exists(jsonPath))
        {
            progress?.Report(new(InstallStage.Manifest, default));
            var manifest = await GetManifestAsync(ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                        ?? throw new ArgumentException($"版本清单里没有 {versionId}");

            progress?.Report(new(InstallStage.VersionJson, default));
            await downloader.DownloadOneAsync(new DownloadItem(entry.Url, jsonPath, entry.Sha1), ct);
        }
        await CompleteAsync(versionId, progress, ct);
    }

    /// <summary>补全一个已有版本（含加载器版本）运行所需的所有文件。</summary>
    public async Task CompleteAsync(string versionId, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var version = dir.ResolveVersion(versionId);
        var items = new List<DownloadItem>();

        // 客户端 jar：加载器版本会通过 jar / inheritsFrom 指向原版的 jar。
        var jarId = version.Jar ?? versionId;
        if (version.Downloads?.GetValueOrDefault("client") is { Url: { } clientUrl } client)
            items.Add(new DownloadItem(clientUrl, dir.VersionJarPath(jarId), client.Sha1, client.Size));

        foreach (var lib in LibraryResolver.Resolve(version, dir, Rules))
        {
            if (!string.IsNullOrEmpty(lib.Url))
                items.Add(new DownloadItem(lib.Url, lib.LocalPath, lib.Sha1, lib.Size));
        }

        if (version.Logging?.GetValueOrDefault("client")?.File is { Url: { } logUrl } logFile)
            items.Add(new DownloadItem(logUrl, dir.LoggingConfigPath(logFile.Id), logFile.Sha1, logFile.Size));

        if (version.AssetIndex is { Url: { } indexUrl } indexRef)
        {
            progress?.Report(new(InstallStage.AssetIndex, default));
            var indexPath = dir.AssetIndexPath(indexRef.Id);
            await downloader.DownloadOneAsync(new DownloadItem(indexUrl, indexPath, indexRef.Sha1, indexRef.Size), ct);
            var index = await ReadAssetIndexAsync(indexPath, ct);
            foreach (var obj in index.Objects.Values.DistinctBy(o => o.Hash))
            {
                var prefix = obj.Hash[..2];
                items.Add(new DownloadItem($"{AssetBaseUrl}{prefix}/{obj.Hash}", dir.AssetObjectPath(obj.Hash), obj.Hash, obj.Size));
            }
        }

        await downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new(InstallStage.Files, p))), ct);
        progress?.Report(new(InstallStage.Done, default));
    }

    public static async Task<AssetIndex> ReadAssetIndexAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, Json.Options.Info<AssetIndex>(), ct)
               ?? throw new InvalidDataException($"资源索引解析失败：{path}");
    }
}
