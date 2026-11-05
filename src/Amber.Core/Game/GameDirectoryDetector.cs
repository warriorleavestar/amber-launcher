namespace Amber.Core.Game;

/// <summary>找到的已有游戏目录和里面装好的实例数。</summary>
public sealed record DetectedGameDirectory(string Path, int InstanceCount);

/// <summary>
/// 首次使用时找电脑上已有的游戏目录（改版规格 3.1）：默认的 .minecraft，
/// 以及别的启动器常放的位置（启动器旁边的 .minecraft，放在桌面、下载、文档或各盘根目录下的文件夹里）。
/// 只读：列目录、数 versions 里的版本，不写任何东西。
/// </summary>
public static class GameDirectoryDetector
{
    /// <summary>常见位置。顺序就是优先顺序（实例数一样时靠前的排前面）。</summary>
    public static IEnumerable<string> CommonCandidates(string? launcherDir = null)
    {
        yield return GameDirectory.DefaultRoot();
        if (launcherDir is { Length: > 0 }) yield return Path.Combine(launcherDir, ".minecraft");
        var roots = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments })
            if (Environment.GetFolderPath(folder) is { Length: > 0 } path) roots.Add(path);
        if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home) roots.Add(Path.Combine(home, "Downloads"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in SafeDrives()) roots.Add(drive);
        }
        foreach (var root in roots)
        {
            yield return Path.Combine(root, ".minecraft");
            // 再往下一层：“桌面\HMCL\.minecraft”“D:\我的世界\.minecraft”这种。
            foreach (var child in SafeDirectories(root))
                yield return Path.Combine(child, ".minecraft");
        }
    }

    /// <summary>在候选目录里找有已装实例的，去重、跳过 exclude（当前用的目录），实例多的排前面。</summary>
    public static List<DetectedGameDirectory> Find(IEnumerable<string> candidates, string? exclude = null, int limit = 5)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (exclude is { Length: > 0 }) seen.Add(Normalize(exclude));
        var found = new List<(DetectedGameDirectory Dir, int Order)>();
        var order = 0;
        foreach (var candidate in candidates)
        {
            order++;
            string full;
            try { full = Normalize(candidate); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!seen.Add(full)) continue;
            var count = CountInstances(full);
            if (count > 0) found.Add((new DetectedGameDirectory(full, count), order));
        }
        return [.. found.OrderByDescending(f => f.Dir.InstanceCount).ThenBy(f => f.Order).Take(limit).Select(f => f.Dir)];
    }

    /// <summary>目录里装好的实例数（有版本 JSON 的版本文件夹）；不是游戏目录或读不了时是 0。</summary>
    public static int CountInstances(string root)
    {
        try
        {
            return Directory.Exists(Path.Combine(root, "versions")) ? new GameDirectory(root).ListInstalled().Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).StartsWith('$'))
                .Take(200).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
