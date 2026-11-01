using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Game;
using Amber.Core.Net;

namespace Amber.Core.Install;

/// <summary>
/// Forge 系安装器的公共部分：解析官方安装包里的 install_profile.json。
/// 旧格式（1.12.2 及更早）直接解出通用包；新格式（1.13+ 与 NeoForge）要运行处理器生成客户端。
/// </summary>
public abstract partial class ForgeLikeInstaller(LoaderContext ctx) : ILoaderInstaller
{
    protected LoaderContext Ctx { get; } = ctx;

    public abstract LoaderKind Kind { get; }
    public abstract Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default);
    protected abstract string InstallerUrl(LoaderVersion version);

    public async Task<string> InstallAsync(LoaderVersion version, string? customId = null, IProgress<LoaderProgress>? progress = null, CancellationToken ct = default)
    {
        var id = string.IsNullOrWhiteSpace(customId) ? version.DefaultId : customId.Trim();
        var dir = Ctx.Dir;

        progress?.Report(new($"安装原版 {version.McVersion}"));
        await Ctx.Vanilla.InstallAsync(version.McVersion, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, "下载原版文件", p)), ct);

        var work = Path.Combine(Path.GetTempPath(), "amber-forge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new($"下载 {Kind} 安装包"));
            var installerPath = Path.Combine(work, "installer.jar");
            await Ctx.Downloader.DownloadOneAsync(new DownloadItem(InstallerUrl(version), installerPath), ct);

            using var zip = ZipFile.OpenRead(installerPath);
            var profile = ReadJson(zip, "install_profile.json")
                          ?? throw new InvalidDataException("安装包里没有 install_profile.json（这个版本太旧，官方没有提供安装器）");

            if (profile["install"] is JsonObject install && profile["versionInfo"] is JsonObject info)
            {
                InstallLegacy(zip, id, version.McVersion, install, info);
            }
            else
            {
                var versionPath = (profile["json"]?.GetValue<string>() ?? "/version.json").TrimStart('/');
                var versionJson = ReadJson(zip, versionPath) ?? throw new InvalidDataException($"安装包里没有 {versionPath}");
                versionJson["inheritsFrom"] ??= version.McVersion;
                LoaderInstallers.WriteVersionJson(dir, id, versionJson);
                ExtractMaven(zip);
                await RunProfileAsync(zip, profile, version.McVersion, work, installerPath, progress, ct);
            }
        }
        catch
        {
            // 半装好的版本不留在列表里。
            TryDeleteDir(dir.VersionDir(id));
            throw;
        }
        finally
        {
            TryDeleteDir(work);
        }

        await Ctx.Vanilla.CompleteAsync(id, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, "下载加载器库", p)), ct);
        return id;
    }

    /// <summary>旧格式：versionInfo 就是版本 JSON，filePath 是要放进 libraries 的通用包。</summary>
    private void InstallLegacy(ZipArchive zip, string id, string mcVersion, JsonObject install, JsonObject info)
    {
        var entryName = install["filePath"]!.GetValue<string>();
        var coordinate = install["path"]!.GetValue<string>();
        var target = Ctx.Dir.LibraryPath(MavenCoordinate.Parse(coordinate).RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        (zip.GetEntry(entryName) ?? throw new InvalidDataException($"安装包里没有 {entryName}")).ExtractToFile(target, true);

        // 只给服务端用的库不下载；clientreq 明确为 false 的就是这类。
        if (info["libraries"] is JsonArray libs)
        {
            for (var i = libs.Count - 1; i >= 0; i--)
            {
                if (libs[i]?["clientreq"] is JsonValue v && v.TryGetValue<bool>(out var required) && !required) libs.RemoveAt(i);
            }
        }
        info["inheritsFrom"] ??= mcVersion;
        info["jar"] ??= mcVersion;
        LoaderInstallers.WriteVersionJson(Ctx.Dir, id, info);
    }

    private void ExtractMaven(ZipArchive zip)
    {
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("maven/", StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            var target = Ctx.Dir.LibraryPath(entry.FullName["maven/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private async Task RunProfileAsync(ZipArchive zip, JsonObject profile, string mcVersion, string work, string installerPath, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var dir = Ctx.Dir;
        var vanilla = dir.ResolveVersion(mcVersion);

        // 处理器依赖的库。
        var profileVersion = new VersionJson { Libraries = profile["libraries"]?.Deserialize<List<Library>>(Json.Options) ?? [] };
        var items = LibraryResolver.Resolve(profileVersion, dir, RuleEvaluator.Current)
            .Where(l => !string.IsNullOrEmpty(l.Url))
            .Select(l => new DownloadItem(l.Url!, l.LocalPath, l.Sha1, l.Size)).ToList();
        progress?.Report(new("下载处理器"));
        await Ctx.Downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(p => progress?.Report(new($"下载处理器 {p.CompletedFiles}/{p.TotalFiles}", 100.0 * p.CompletedFiles / Math.Max(1, p.TotalFiles)))), ct);

        var data = new Dictionary<string, string>
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = dir.VersionJarPath(vanilla.Jar ?? mcVersion),
            ["MINECRAFT_VERSION"] = mcVersion,
            ["ROOT"] = dir.Root,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = dir.LibrariesDir,
        };
        if (profile["data"] is JsonObject dataNode)
        {
            foreach (var (key, value) in dataNode)
            {
                if (value?["client"]?.GetValue<string>() is { } client)
                    data[key] = ResolveValue(client, zip, work);
            }
        }

        var processors = (profile["processors"] as JsonArray ?? []).OfType<JsonObject>().Where(p =>
            p["sides"] is not JsonArray sides || sides.Any(s => s?.GetValue<string>() == "client")).ToList();
        if (processors.Count == 0) return;

        var required = Math.Max(vanilla.JavaVersion?.MajorVersion ?? 8, 8);
        progress?.Report(new($"准备 Java {required}"));
        var java = await Ctx.JavaFor(required, ct);

        for (var i = 0; i < processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = processors[i]["jar"]!.GetValue<string>();
            progress?.Report(new($"运行处理器 {i + 1}/{processors.Count}：{MavenCoordinate.Parse(name).Artifact}", 100.0 * i / processors.Count));
            await RunProcessorAsync(processors[i], data, java, vanilla, ct);
        }
    }

    private async Task RunProcessorAsync(JsonObject processor, Dictionary<string, string> data, string java, VersionJson vanilla, CancellationToken ct)
    {
        var dir = Ctx.Dir;
        var outputs = new List<(string Path, string? Sha1)>();
        if (processor["outputs"] is JsonObject outs)
        {
            foreach (var (key, value) in outs)
                outputs.Add((Substitute(key, data), value is null ? null : Substitute(value.GetValue<string>(), data).Trim('\'')));
        }
        if (outputs.Count > 0 && await AllValidAsync(outputs, ct)) return;

        var args = (processor["args"] as JsonArray ?? []).Select(a => Substitute(a!.GetValue<string>(), data)).ToList();

        // 映射表自己下载，这样能走镜像和校验，不依赖处理器内部的单一地址。
        var taskIndex = args.IndexOf("--task");
        var outputIndex = args.IndexOf("--output");
        if (taskIndex >= 0 && taskIndex + 1 < args.Count && args[taskIndex + 1] == "DOWNLOAD_MOJMAPS"
            && outputIndex >= 0 && outputIndex + 1 < args.Count
            && vanilla.Downloads?.GetValueOrDefault("client_mappings") is { Url: { } mappingsUrl } mappings)
        {
            await Ctx.Downloader.DownloadOneAsync(new DownloadItem(mappingsUrl, args[outputIndex + 1], mappings.Sha1, mappings.Size), ct);
            return;
        }

        var jar = dir.LibraryPath(MavenCoordinate.Parse(processor["jar"]!.GetValue<string>()).RelativePath);
        var classpath = (processor["classpath"] as JsonArray ?? [])
            .Select(c => dir.LibraryPath(MavenCoordinate.Parse(c!.GetValue<string>()).RelativePath)).Append(jar).ToList();

        var psi = new ProcessStartInfo(java)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir.Root,
        };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
        psi.ArgumentList.Add(ReadMainClass(jar));
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动处理器");
        await using var cancel = ct.Register(() => { try { process.Kill(true); } catch { /* 已退出 */ } });
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var output = await stdout + await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"处理器 {Path.GetFileName(jar)} 失败（退出代码 {process.ExitCode}）：{Tail(output)}");

        if (outputs.Count > 0 && !await AllValidAsync(outputs, ct))
            throw new InvalidDataException($"处理器 {Path.GetFileName(jar)} 的输出校验不通过");
    }

    private static async Task<bool> AllValidAsync(List<(string Path, string? Sha1)> outputs, CancellationToken ct)
    {
        foreach (var (path, sha1) in outputs)
        {
            if (!File.Exists(path)) return false;
            if (sha1 is not null && !(await Downloader.Sha1Async(path, ct)).Equals(sha1, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string ReadMainClass(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        var manifest = zip.GetEntry("META-INF/MANIFEST.MF") ?? throw new InvalidDataException($"{jar} 没有 MANIFEST");
        using var reader = new StreamReader(manifest.Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase)) return line["Main-Class:".Length..].Trim();
        }
        throw new InvalidDataException($"{jar} 没有 Main-Class");
    }

    /// <summary>data 的值：[坐标] 是库路径，'x' 是字面量，/x 是安装包里的文件（解到临时目录）。</summary>
    private string ResolveValue(string value, ZipArchive zip, string work)
    {
        if (value.Length > 1 && value[0] == '[' && value[^1] == ']')
            return Ctx.Dir.LibraryPath(MavenCoordinate.Parse(value[1..^1]).RelativePath);
        if (value.Length > 1 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value.StartsWith('/'))
        {
            var entry = zip.GetEntry(value.TrimStart('/')) ?? throw new InvalidDataException($"安装包里没有 {value}");
            var target = Path.Combine(work, value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            return target;
        }
        return value;
    }

    private string Substitute(string value, Dictionary<string, string> data)
    {
        if (value.Length > 1 && value[0] == '[' && value[^1] == ']')
            return Ctx.Dir.LibraryPath(MavenCoordinate.Parse(value[1..^1]).RelativePath);
        return Placeholder().Replace(value, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static JsonObject? ReadJson(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry is null) return null;
        using var stream = entry.Open();
        return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject();
    }

    private static string Tail(string text) => text.Length <= 800 ? text.Trim() : "…" + text[^800..].Trim();

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { /* 清理失败不影响结果 */ }
    }

    [GeneratedRegex(@"\{([A-Z_0-9]+)\}")]
    private static partial Regex Placeholder();
}

public sealed class ForgeInstaller(LoaderContext ctx) : ForgeLikeInstaller(ctx)
{
    private const string MetadataUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    private const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    public override LoaderKind Kind => LoaderKind.Forge;

    protected override string InstallerUrl(LoaderVersion v) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{v.Version}/forge-{v.Version}-installer.jar";

    public override async Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default)
    {
        using var meta = JsonDocument.Parse(await Ctx.Downloader.GetStringAsync(MetadataUrl, ct));
        if (!meta.RootElement.TryGetProperty(mcVersion, out var list)) return [];

        // 推荐版 / 最新版标记只是展示用，取不到也不影响安装。
        var notes = new Dictionary<string, string>();
        try
        {
            using var promos = JsonDocument.Parse(await Ctx.Downloader.GetStringAsync(PromotionsUrl, ct));
            foreach (var p in promos.RootElement.GetProperty("promos").EnumerateObject())
            {
                if (!p.Name.StartsWith(mcVersion + "-", StringComparison.Ordinal)) continue;
                notes[p.Value.GetString()!] = p.Name.EndsWith("recommended", StringComparison.Ordinal) ? "推荐" : "最新";
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException) { }

        return list.EnumerateArray().Select(v => v.GetString()!).Reverse().Select(full =>
        {
            var parts = full.Split('-');
            var build = parts.Length > 1 ? parts[1] : full;
            return new LoaderVersion(Kind, mcVersion, full, Stable: true, Note: notes.GetValueOrDefault(build));
        }).ToList();
    }
}

public sealed class NeoForgeInstaller(LoaderContext ctx) : ForgeLikeInstaller(ctx)
{
    private const string ApiRoot = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/";
    private const string MavenRoot = "https://maven.neoforged.net/releases/net/neoforged/";

    public override LoaderKind Kind => LoaderKind.NeoForge;

    /// <summary>1.20.1 用的是 net.neoforged:forge，之后才是 net.neoforged:neoforge。</summary>
    private static string Artifact(string mcVersion) => mcVersion == "1.20.1" ? "forge" : "neoforge";

    protected override string InstallerUrl(LoaderVersion v)
    {
        var artifact = Artifact(v.McVersion);
        return $"{MavenRoot}{artifact}/{v.Version}/{artifact}-{v.Version}-installer.jar";
    }

    public override async Task<IReadOnlyList<LoaderVersion>> ListAsync(string mcVersion, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Ctx.Downloader.GetStringAsync(ApiRoot + Artifact(mcVersion), ct));
        var prefix = VersionPrefix(mcVersion);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString()!)
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Reverse()
            .Select(v => new LoaderVersion(Kind, mcVersion, v, !v.Contains("-beta") && !v.Contains("-alpha")))
            .ToList();
    }

    /// <summary>1.21.1 → 21.1.；1.21 → 21.0.；26.3 → 26.3.0.；26.1.2 → 26.1.2.；1.20.1 → 1.20.1-</summary>
    public static string VersionPrefix(string mcVersion)
    {
        if (mcVersion == "1.20.1") return "1.20.1-";
        var parts = mcVersion.Split('.');
        if (parts[0] == "1" && parts.Length >= 2)
            return $"{parts[1]}.{(parts.Length > 2 ? parts[2] : "0")}.";
        return parts.Length == 2 ? $"{mcVersion}.0." : mcVersion + ".";
    }
}
