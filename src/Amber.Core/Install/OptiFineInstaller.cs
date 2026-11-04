using Amber.Core.Localization;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Net;

namespace Amber.Core.Install;

/// <summary>
/// OptiFine：官网没有下载接口，版本列表和文件来自 BMCLAPI。
/// 安装包自带补丁工具 optifine.Patcher，把它和原版 jar 合成一个库文件，再写一份继承原版的版本 JSON。
/// </summary>
public sealed class OptiFineInstaller(LoaderContext ctx) : ILoaderInstaller
{
    private const string Api = "https://bmclapi2.bangbang93.com/optifine/";

    public LoaderKind Kind => LoaderKind.OptiFine;

    public async Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await ctx.Downloader.GetStringAsync(Api + mcVersion, ct));
        return doc.RootElement.EnumerateArray().Select(v =>
        {
            var type = v.GetProperty("type").GetString()!;
            var patch = v.GetProperty("patch").GetString()!;
            var forge = v.TryGetProperty("forge", out var f) ? f.GetString() : null;
            return new LoaderVersion(Kind, mcVersion, $"{type}_{patch}", !patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase), Note: forge);
        }).OrderByDescending(v => v.Stable).ThenByDescending(v => v.Version, StringComparer.Ordinal).ToList();
    }

    private static string DownloadUrl(LoaderVersion v)
    {
        var split = v.Version.LastIndexOf('_');
        return $"{Api}{v.McVersion}/{v.Version[..split]}/{v.Version[(split + 1)..]}";
    }

    /// <summary>下载 OptiFine 到指定文件，用于“作为模组装进 mods 文件夹”。</summary>
    public Task DownloadJarAsync(LoaderVersion version, string path, CancellationToken ct = default) =>
        ctx.Downloader.DownloadOneAsync(new DownloadItem(DownloadUrl(version), path), ct);

    public async Task<string> InstallAsync(LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(customId) ? version.DefaultId : customId.Trim();
        var dir = ctx.Dir;
        progress?.Report(new(L.F("core.install.loader.installvanilla", version.McVersion)));
        await ctx.Vanilla.InstallAsync(version.McVersion, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, L.T("core.install.loader.downloadvanilla"), p)), ct);
        var vanilla = dir.ResolveVersion(version.McVersion);

        var work = Path.Combine(Path.GetTempPath(), "amber-optifine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new(L.T("core.install.optifine.download")));
            var installer = Path.Combine(work, "OptiFine.jar");
            await DownloadJarAsync(version, installer, ct);

            var libVersion = $"{version.McVersion}_{version.Version}";
            var library = dir.LibraryPath($"optifine/OptiFine/{libVersion}/OptiFine-{libVersion}.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(library)!);

            var libraries = new JsonArray { (JsonNode)new JsonObject { ["name"] = $"optifine:OptiFine:{libVersion}" } };
            using (var zip = ZipFile.OpenRead(installer))
            {
                if (zip.GetEntry("optifine/Patcher.class") is not null)
                {
                    progress?.Report(new(L.T("core.install.optifine.mergelibrary")));
                    var java = await ctx.JavaFor(Math.Max(vanilla.JavaVersion?.MajorVersion ?? 8, 8), ct);
                    await RunPatcherAsync(java, installer, dir.VersionJarPath(vanilla.Jar ?? version.McVersion), library, ct);
                }
                else
                {
                    File.Copy(installer, library, true);
                }

                // 新版自带修改过的 launchwrapper，旧版用 Mojang 的。
                if (zip.GetEntry("launchwrapper-of.txt") is { } marker)
                {
                    using var reader = new StreamReader(marker.Open());
                    var ofVersion = reader.ReadToEnd().Trim();
                    var target = dir.LibraryPath($"optifine/launchwrapper-of/{ofVersion}/launchwrapper-of-{ofVersion}.jar");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    zip.GetEntry($"launchwrapper-of-{ofVersion}.jar")!.ExtractToFile(target, true);
                    libraries.Add((JsonNode)new JsonObject { ["name"] = $"optifine:launchwrapper-of:{ofVersion}" });
                }
                else if (zip.GetEntry("launchwrapper-2.0.jar") is { } lw2)
                {
                    var target = dir.LibraryPath("optifine/launchwrapper/2.0/launchwrapper-2.0.jar");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    lw2.ExtractToFile(target, true);
                    libraries.Add((JsonNode)new JsonObject { ["name"] = "optifine:launchwrapper:2.0" });
                }
                else
                {
                    libraries.Add((JsonNode)new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" });
                }
            }

            var json = new JsonObject
            {
                ["inheritsFrom"] = version.McVersion,
                ["type"] = vanilla.Type ?? "release",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["libraries"] = libraries,
            };
            const string tweak = "--tweakClass optifine.OptiFineTweaker";
            if (vanilla.MinecraftArguments is { } legacy)
                json["minecraftArguments"] = legacy + " " + tweak;
            else
                json["arguments"] = new JsonObject { ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker") };
            LoaderInstallers.WriteVersionJson(dir, id, json);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* 清理失败不影响结果 */ }
        }

        await ctx.Vanilla.CompleteAsync(id, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, L.T("core.install.optifine.downloadlibraries"), p)), ct);
        return id;
    }

    private static async Task RunPatcherAsync(string java, string installer, string vanillaJar, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(java)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-cp", installer, "optifine.Patcher", vanillaJar, installer, output }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.install.optifine.patchstartfailed"));
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(L.F("core.install.optifine.patchfailed", process.ExitCode, await stderr, await stdout));
    }
}
