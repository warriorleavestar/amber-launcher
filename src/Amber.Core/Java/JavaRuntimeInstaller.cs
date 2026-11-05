using Amber.Core.Localization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Amber.Core.Net;

namespace Amber.Core.Java;

/// <summary>从 Mojang 的 Java 运行时清单下载 JRE 到 &lt;游戏目录&gt;/runtime。</summary>
public sealed class JavaRuntimeInstaller(Downloader downloader, string gameRoot)
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public static string Platform
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsWindows())
                return arch switch { Architecture.Arm64 => "windows-arm64", Architecture.X86 => "windows-x86", _ => "windows-x64" };
            if (OperatingSystem.IsMacOS())
                return arch == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";
            // Mojang 目前只发布 Linux x86/x64 运行时；ARM Linux 要使用系统安装的 Java，
            // 不能把 x64 包误装到 ARM 机器上。
            return arch switch
            {
                Architecture.X64 => "linux",
                Architecture.X86 => "linux-i386",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-" + arch.ToString().ToLowerInvariant(),
            };
        }
    }

    /// <summary>安装运行时并返回 java 可执行文件路径。component 为空或当前平台没有时按主版本号找。</summary>
    public async Task<string> InstallAsync(string? component, int majorVersion, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var all = JsonDocument.Parse(await downloader.GetStringAsync(ManifestUrl, ct));
        if (!all.RootElement.TryGetProperty(Platform, out var platform))
            throw new PlatformNotSupportedException(L.F("core.java.install.unsupportedplatform", Platform));

        var (name, manifestUrl) = FindComponent(platform, component, majorVersion)
                                  ?? throw new InvalidOperationException(L.F("core.java.install.unavailableversion", majorVersion));

        using var manifest = JsonDocument.Parse(await downloader.GetStringAsync(manifestUrl, ct));
        var home = Path.Combine(gameRoot, "runtime", name, Platform, name);
        var items = new List<DownloadItem>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var local = Path.Combine(home, file.Name.Replace('/', Path.DirectorySeparatorChar));
            switch (file.Value.GetProperty("type").GetString())
            {
                case "directory":
                    Directory.CreateDirectory(local);
                    break;
                case "file":
                    var raw = file.Value.GetProperty("downloads").GetProperty("raw");
                    items.Add(new DownloadItem(raw.GetProperty("url").GetString()!, local, raw.GetProperty("sha1").GetString(), raw.GetProperty("size").GetInt64()));
                    break;
            }
        }
        await downloader.DownloadAllAsync(items, progress, ct);

        var exe = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(exe)) throw new FileNotFoundException(L.T("core.java.install.missingexecutable"), exe);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return exe;
    }

    private static (string Name, string Url)? FindComponent(JsonElement platform, string? component, int major)
    {
        if (component is not null && Url(platform, component) is { } direct) return (component, direct);
        foreach (var entry in platform.EnumerateObject())
        {
            if (entry.Name == "minecraft-java-exe" || entry.Value.GetArrayLength() == 0) continue;
            var versionName = entry.Value[0].GetProperty("version").GetProperty("name").GetString() ?? "";
            var parsed = versionName.StartsWith("8u", StringComparison.Ordinal) ? 8 : JavaFinder.ParseMajor(versionName);
            if (parsed == major && Url(platform, entry.Name) is { } url) return (entry.Name, url);
        }
        return null;

        static string? Url(JsonElement p, string key) =>
            p.TryGetProperty(key, out var arr) && arr.GetArrayLength() > 0 ? arr[0].GetProperty("manifest").GetProperty("url").GetString() : null;
    }
}
