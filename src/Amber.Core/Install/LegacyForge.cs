using Amber.Core.Localization;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Net;

namespace Amber.Core.Install;

/// <summary>FML 要放进游戏目录 lib/ 的一个文件。</summary>
public sealed record FmlLibrary(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("sha1")] string Sha1);

/// <summary>
/// 1.5.2 及更早的 Forge：官方没有能用的安装器，Forge 是“改 jar”的模组。
/// 做法：原版客户端 jar 去掉 META-INF 签名，再把 Forge 的 universal（1.3.2 起）或 client（更早）包盖上去，作为新版本自己的 jar。
/// 1.3.2–1.5.2 的 FML 第一次启动会往游戏目录的 lib/ 下载几个库，原来的地址早就没了：
/// 这里从 Forge 自己的 CoreFMLLibraries 读出文件名和 SHA-1，从现在还能下到的地方找内容完全一致的文件，启动前放进 lib/。
/// </summary>
public static class LegacyForge
{
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    private const string Archive = "https://web.archive.org/web/2013id_/http://files.minecraftforge.net/fmllibs/";

    /// <summary>1.5.2 及更早（含 1.1、1.2.x、1.3.2、1.4.x、1.5.x）。</summary>
    public static bool IsJarModEra(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out var minor)) return false;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return minor < 5 || (minor == 5 && patch <= 2);
    }

    /// <summary>FML 要的库：先 Forge 的 maven，再 Maven 中央仓库，最后互联网档案馆里 2013 年的原始文件。下载后按 FML 自己的 SHA-1 校验。</summary>
    internal static IEnumerable<string> Sources(string name)
    {
        const string fm = "https://maven.minecraftforge.net/";
        const string central = "https://repo1.maven.org/maven2/";
        var known = name switch
        {
            "argo-small-3.2.jar" => [fm + "net/sourceforge/argo/argo/3.2-small/argo-3.2-small.jar"],
            "argo-2.25.jar" => [central + "net/sourceforge/argo/argo/2.25/argo-2.25.jar"],
            "guava-14.0-rc3.jar" => [fm + "com/google/guava/guava/14.0-rc3/guava-14.0-rc3.jar"],
            "guava-12.0.1.jar" => [central + "com/google/guava/guava/12.0.1/guava-12.0.1.jar"],
            "asm-all-4.1.jar" => [fm + "org/ow2/asm/asm-all/4.1/asm-all-4.1.jar"],
            "asm-all-4.0.jar" => [central + "org/ow2/asm/asm-all/4.0/asm-all-4.0.jar"],
            "bcprov-jdk15on-148.jar" => [fm + "org/bouncycastle/bcprov-jdk15on/148/bcprov-jdk15on-148.jar"],
            "bcprov-jdk15on-147.jar" => [central + "org/bouncycastle/bcprov-jdk15on/1.47/bcprov-jdk15on-1.47.jar"],
            "scala-library.jar" => [fm + "org/scala-lang/scala-library/2.10.0-custom/scala-library-2.10.0-custom.jar"],
            _ => Array.Empty<string>(),
        };
        return [.. known, Archive + Uri.EscapeDataString(name)];
    }

    public static async Task<string> InstallAsync(LoaderContext ctx, LoaderVersion version, string id, IProgress<LoaderProgress>? progress, CancellationToken ct)
    {
        var dir = ctx.Dir;
        var mc = version.McVersion;
        progress?.Report(new(L.F("core.install.loader.installvanilla", mc)));
        await ctx.Vanilla.InstallAsync(mc, new Progress<InstallProgress>(p => LoaderInstallers.ReportFiles(progress, L.T("core.install.loader.downloadvanilla"), p)), ct);
        var vanilla = dir.ResolveVersion(mc);
        var vanillaJar = dir.VersionJarPath(vanilla.Jar ?? mc);
        if (!File.Exists(vanillaJar)) throw new FileNotFoundException(L.T("core.install.legacyforge.missingclient"), vanillaJar);

        var work = Path.Combine(Path.GetTempPath(), "amber-oldforge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new(L.T("core.install.legacyforge.download")));
            var forgeZip = Path.Combine(work, "forge.zip");
            await DownloadForgeAsync(ctx.Downloader, version.Version, forgeZip, ct);

            progress?.Report(new(L.T("core.install.legacyforge.mergeclient")));
            var target = dir.VersionJarPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MergeJar(vanillaJar, forgeZip, target);

            var libs = ReadFmlLibraries(forgeZip, mc);
            if (libs.Count > 0)
            {
                progress?.Report(new(L.P("core.install.legacyforge.downloadlibraries", libs.Count, libs.Count)));
                var cache = FmlCacheDir(dir.Root);
                Directory.CreateDirectory(cache);
                var done = 0;
                foreach (var lib in libs)
                {
                    await FetchVerifiedAsync(ctx.Downloader, lib, Path.Combine(cache, lib.Name), ct);
                    progress?.Report(new(L.F("core.install.legacyforge.libraryprogress", ++done, libs.Count), 100.0 * done / libs.Count));
                }
            }

            var json = new JsonObject
            {
                ["inheritsFrom"] = mc,
                ["jar"] = id,
                // 空的 downloads 盖掉原版的客户端下载信息，免得“补全文件”把改过的 jar 换回原版。
                ["downloads"] = new JsonObject(),
                ["type"] = "release",
                ["amberForge"] = version.Version,
                ["amberLegacyLibs"] = new JsonArray(libs.Select(l => (JsonNode?)new JsonObject { ["name"] = l.Name, ["sha1"] = l.Sha1 }).ToArray()),
            };
            LoaderInstallers.WriteVersionJson(dir, id, json);
        }
        catch
        {
            try { if (Directory.Exists(dir.VersionDir(id))) Directory.Delete(dir.VersionDir(id), true); } catch (IOException) { }
            throw;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        return id;
    }

    public static string FmlCacheDir(string root) => Path.Combine(root, "libraries", "net", "minecraftforge", "fmllibs");

    /// <summary>1.3.2 起是 universal 包，更早的是 client 包。</summary>
    private static async Task DownloadForgeAsync(Downloader downloader, string fullVersion, string path, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var classifier in new[] { "universal", "client" })
        {
            try
            {
                await downloader.DownloadOneAsync(new DownloadItem($"{ForgeMaven}{fullVersion}/forge-{fullVersion}-{classifier}.zip", path), ct);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or DownloadException or IOException)
            {
                last = e;
            }
        }
        throw new InvalidDataException(L.F("core.install.legacyforge.packageunavailable", fullVersion, last?.Message));
    }

    /// <summary>新 jar = Forge 包里的全部文件 + 原版 jar 里 Forge 没覆盖的文件；两边的 META-INF 都不要（签名会让改过的类加载失败）。</summary>
    internal static void MergeJar(string vanillaJar, string forgeZip, string target)
    {
        var temp = target + ".tmp";
        using (var output = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in new[] { forgeZip, vanillaJar })
            {
                using var input = ZipFile.OpenRead(source);
                foreach (var entry in input.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!written.Add(entry.FullName)) continue;
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                    using var from = entry.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
            }
        }
        File.Move(temp, target, true);
    }

    /// <summary>
    /// 从 Forge 包里的 cpw/mods/fml/relauncher/CoreFMLLibraries.class 读出库文件名和 SHA-1（类文件常量池里按顺序排着），
    /// 1.5.x 另外要 deobfuscation_data_&lt;版本&gt;.zip，哈希在 fmlversion.properties 里。1.2.5 及更早没有这个类，返回空。
    /// </summary>
    internal static List<FmlLibrary> ReadFmlLibraries(string forgeZip, string mcVersion)
    {
        using var zip = ZipFile.OpenRead(forgeZip);
        var result = new List<FmlLibrary>();
        if (zip.GetEntry("cpw/mods/fml/relauncher/CoreFMLLibraries.class") is { } entry)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var strings = Utf8Constants(memory.ToArray());
            var names = strings.Where(s => s.EndsWith(".jar", StringComparison.Ordinal) && !s.Contains('/') && s.Length > 4).ToList();
            var hashes = strings.Where(IsSha1).ToList();
            if (names.Count != hashes.Count) throw new InvalidDataException(L.F("core.install.legacyforge.invalidlibrarylist", names.Count, hashes.Count));
            result.AddRange(names.Zip(hashes, (n, h) => new FmlLibrary(n, h)));
        }
        foreach (var name in new[] { "fmlversion.properties", "forgeversion.properties" })
        {
            if (zip.GetEntry(name) is not { } props) continue;
            using var reader = new StreamReader(props.Open());
            var hash = reader.ReadToEnd().Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("fmlbuild.deobfuscation.hash=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim();
            if (hash is not null && IsSha1(hash)) result.Add(new FmlLibrary($"deobfuscation_data_{mcVersion}.zip", hash));
        }
        return result;
    }

    private static bool IsSha1(string s) => s.Length == 40 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>读 Java 类文件常量池里的 UTF-8 字符串，按出现顺序。</summary>
    internal static List<string> Utf8Constants(byte[] data)
    {
        if (data.Length < 10 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0xCAFEBABE) throw new InvalidDataException(L.T("core.install.legacyforge.invalidclass"));
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        var result = new List<string>();
        var i = 10;
        for (var index = 1; index < count; index++)
        {
            var tag = data[i];
            switch (tag)
            {
                case 1:
                    var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 1));
                    result.Add(Encoding.UTF8.GetString(data, i + 3, length));
                    i += 3 + length;
                    break;
                case 3 or 4: i += 5; break;
                case 5 or 6: i += 9; index++; break;
                case 7 or 8 or 16 or 19 or 20: i += 3; break;
                case 9 or 10 or 11 or 12 or 17 or 18: i += 5; break;
                case 15: i += 4; break;
                default: throw new InvalidDataException(L.F("core.install.legacyforge.unknownconstant", tag));
            }
        }
        return result;
    }

    /// <summary>按顺序试每个来源，内容的 SHA-1 必须和 FML 要的一样。已经有且正确就不下载。</summary>
    private static async Task FetchVerifiedAsync(Downloader downloader, FmlLibrary lib, string path, CancellationToken ct)
    {
        if (File.Exists(path) && await Sha1Async(path, ct) == lib.Sha1) return;
        var errors = new List<string>();
        foreach (var url in Sources(lib.Name))
        {
            try
            {
                await downloader.DownloadOneAsync(new DownloadItem(url, path, lib.Sha1), ct);
                if (await Sha1Async(path, ct) == lib.Sha1) return;
                errors.Add(L.F("core.install.legacyforge.invalidcontent", url));
            }
            catch (Exception e) when (e is HttpRequestException or DownloadException or IOException or InvalidDataException)
            {
                errors.Add(L.F("core.install.legacyforge.mirror_error", new Uri(url).Host, e.Message));
            }
        }
        try { File.Delete(path); } catch (IOException) { }
        throw new InvalidDataException(L.F("core.install.legacyforge.librarydownloadfailed", lib.Name, string.Join(L.T("common.error_separator"), errors)));
    }

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct));
    }

    /// <summary>启动前：把这个版本要的 FML 库放进游戏目录的 lib/（版本隔离时每个实例都有自己的 lib/）。</summary>
    public static void EnsureLibraries(string root, string workDir, IReadOnlyList<FmlLibrary> libs, Action<string>? log)
    {
        if (libs.Count == 0) return;
        var libDir = Path.Combine(workDir, "lib");
        Directory.CreateDirectory(libDir);
        foreach (var lib in libs)
        {
            // amberLegacyLibs 写在版本文件里（任何版本文件都能写，继承时也会被合并过来）：名字只能是单个文件名，拼出来的路径必须在各自目录里。
            var name = Game.GameDirectory.CheckId(lib.Name);
            var target = Game.GameDirectory.Contained(libDir, name);
            if (File.Exists(target) && Sha1Sync(target) == lib.Sha1) continue;
            var source = Game.GameDirectory.Contained(FmlCacheDir(root), name);
            if (!File.Exists(source))
            {
                log?.Invoke(L.F("core.install.legacyforge.missinglibrary", lib.Name));
                continue;
            }
            File.Copy(source, target, true);
        }
    }

    private static string Sha1Sync(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }
}
