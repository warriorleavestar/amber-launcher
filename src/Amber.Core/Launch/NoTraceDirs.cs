using System.Diagnostics;
using System.Globalization;

namespace Amber.Core.Launch;

/// <summary>无痕模式临时配置目录的记录和清理（见 <see cref="GameLauncher.SweepNoTrace"/>）。</summary>
internal static class NoTraceDirs
{
    /// <summary>目录里记着用它的游戏进程：进程号和启动时间（防止进程号被别的进程重用）。</summary>
    public const string OwnerFile = "owner";

    /// <summary>没有 owner 的目录多久以内不动：可能是另一个 Amber 正在准备启动。</summary>
    public static readonly TimeSpan FreshLimit = TimeSpan.FromMinutes(10);

    public static bool IsUnder(string root, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void WriteOwner(string dir, Process process)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, OwnerFile),
                process.Id.ToString(CultureInfo.InvariantCulture) + "\n" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>owner 记着的进程还在运行。</summary>
    public static bool OwnerAlive(string dir)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(dir, OwnerFile));
            if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static int Sweep(string root, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // 不跟着联接走
                    if (File.Exists(Path.Combine(dir, OwnerFile)))
                    {
                        if (OwnerAlive(dir)) continue;
                    }
                    else if (nowUtc - info.CreationTimeUtc < FreshLimit)
                    {
                        continue;
                    }
                    if (TryDelete(dir)) removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
