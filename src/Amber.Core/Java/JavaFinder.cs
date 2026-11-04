using Amber.Core.Localization;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Amber.Core.Java;

/// <summary>一个本机 Java 安装。</summary>
public sealed record JavaInstall(string JavaPath, string Version, int MajorVersion, bool Is64Bit)
{
    public override string ToString() => L.F("core.java.runtime.description", Version, (Is64Bit ? "64" : "32"), JavaPath);
}

/// <summary>扫描本机已安装的 Java。</summary>
public static partial class JavaFinder
{
    private static string JavaExe => OperatingSystem.IsWindows() ? "java.exe" : "java";

    public static List<JavaInstall> FindAll(string? gameRoot = null)
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) homes.Add(javaHome);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, JavaExe)))
                homes.Add(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir);
        }

        foreach (var parent in CandidateParents(gameRoot))
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var sub in SafeEnumerate(parent))
            {
                homes.Add(sub);
                // Mojang 运行时多套一层：runtime/java-runtime-gamma/windows-x64/java-runtime-gamma
                foreach (var nested in SafeEnumerate(sub))
                foreach (var deeper in SafeEnumerate(nested))
                    homes.Add(deeper);
            }
        }

        var result = new List<JavaInstall>();
        var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes)
        {
            var exe = Path.Combine(home, "bin", JavaExe);
            if (!File.Exists(exe) || !seenExe.Add(Path.GetFullPath(exe))) continue;
            if (Probe(home, exe) is { } install) result.Add(install);
        }
        return result.OrderByDescending(j => j.MajorVersion).ThenBy(j => j.JavaPath).ToList();
    }

    /// <summary>为指定主版本挑一个最合适的 Java：优先完全匹配，其次比要求高的最低版本。</summary>
    public static JavaInstall? Pick(IEnumerable<JavaInstall> installs, int requiredMajor)
    {
        var list = installs.Where(j => j.Is64Bit || !Environment.Is64BitOperatingSystem).ToList();
        if (list.Count == 0) list = installs.ToList();
        return list.FirstOrDefault(j => j.MajorVersion == requiredMajor)
               ?? list.Where(j => j.MajorVersion > requiredMajor).MinBy(j => j.MajorVersion);
    }

    /// <summary>只要主版本完全一致的 64 位 Java（32 位系统除外）。</summary>
    public static JavaInstall? PickExact(IEnumerable<JavaInstall> installs, int major)
    {
        var list = installs.Where(j => j.MajorVersion == major).ToList();
        return list.FirstOrDefault(j => j.Is64Bit || !Environment.Is64BitOperatingSystem) ?? list.FirstOrDefault();
    }

    private static IEnumerable<string> CandidateParents(string? gameRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "Android\\openjdk" })
                    yield return Path.Combine(pf, vendor);
            }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/Library/Java/JavaVirtualMachines";
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".jdks");
        if (gameRoot is not null) yield return Path.Combine(gameRoot, "runtime");
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return []; }
    }

    private static JavaInstall? Probe(string home, string exe)
    {
        // 优先读 release 文件，避免为每个 Java 起进程。
        var release = Path.Combine(home, "release");
        string? version = null;
        bool? is64 = null;
        if (File.Exists(release))
        {
            foreach (var line in File.ReadLines(release))
            {
                if (line.StartsWith("JAVA_VERSION=")) version = line["JAVA_VERSION=".Length..].Trim('"');
                else if (line.StartsWith("OS_ARCH=")) is64 = line.Contains("64");
            }
        }
        if (version is null || is64 is null)
        {
            (version, is64) = ProbeByRunning(exe);
            if (version is null) return null;
        }
        return new JavaInstall(exe, version, ParseMajor(version), is64 ?? true);
    }

    private static (string?, bool?) ProbeByRunning(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-XshowSettings:properties -version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return (null, null);
            var output = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var version = VersionProperty().Match(output) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            var arch = ArchProperty().Match(output) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
            return (version, arch.Contains("64"));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>"1.8.0_401" → 8，"17.0.10" → 17，"21" → 21。</summary>
    public static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], out var first)) return 0;
        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
    }

    [GeneratedRegex(@"java\.version = (.+)")]
    private static partial Regex VersionProperty();

    [GeneratedRegex(@"os\.arch = (.+)")]
    private static partial Regex ArchProperty();
}
