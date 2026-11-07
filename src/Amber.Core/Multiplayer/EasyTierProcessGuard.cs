using System.Diagnostics;
using System.Globalization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 上保证 Jay 没了 easytier-core 也跟着结束（Windows 用作业对象 <see cref="ProcessJob"/>，这里不管）：
/// <list type="bullet">
/// <item>启动时套一层 /bin/sh 看门：每秒看一次 Jay 还在不在，不在了就结束 easytier-core。Jay 被 kill -9 也一样。</item>
/// <item>每次启动记一个 PID 文件（看门进程的 PID 和启动时间、Jay 的 PID 和启动时间）。看门进程自己也被结束时，
/// 下次打开 Jay 或开房间前按文件清理：只清“进程还在、启动时间对得上、而且记录的 Jay 已经不在了”的，别的 Jay 正在用的不碰。</item>
/// </list>
/// </summary>
internal static class EasyTierProcessGuard
{
    /// <summary>看门脚本：$1 是 Jay 的 PID，后面是 easytier-core 和它的参数。收到 TERM/INT/HUP 时也转给 core。</summary>
    internal const string WatchScript =
        "p=$1; shift; \"$@\" & c=$!; " +
        "trap 'kill -TERM $c 2>/dev/null' TERM INT HUP; " +
        "while kill -0 \"$p\" 2>/dev/null && kill -0 \"$c\" 2>/dev/null; do sleep 1; done; " +
        "kill -TERM \"$c\" 2>/dev/null; wait \"$c\"";

    /// <summary>非 Windows：把要启动的 easytier-core 改成经看门脚本启动。参数原样传，不经过 shell 拼接。</summary>
    internal static void Wrap(ProcessStartInfo info)
    {
        if (OperatingSystem.IsWindows()) return;
        var core = info.FileName;
        var args = info.ArgumentList.ToList();
        info.FileName = "/bin/sh";
        info.ArgumentList.Clear();
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(WatchScript);
        info.ArgumentList.Add("jay-easytier");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(core);
        foreach (var a in args) info.ArgumentList.Add(a);
    }

    private static readonly long OwnerStart = StartTicks(Process.GetCurrentProcess()) ?? 0;

    private static long? StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    private static string PidFile(string pidDir, int pid) => Path.Combine(pidDir, pid.ToString(CultureInfo.InvariantCulture) + ".pid");

    /// <summary>记下刚启动的进程。返回 PID 文件路径（记不下时返回 null，不影响联机）。</summary>
    internal static string? Record(string pidDir, Process process)
    {
        if (OperatingSystem.IsWindows() || StartTicks(process) is not { } start) return null;
        try
        {
            Directory.CreateDirectory(pidDir);
            var path = PidFile(pidDir, process.Id);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
            using var writer = new StreamWriter(new FileStream(path, options));
            writer.Write(string.Join(' ', process.Id, start, Environment.ProcessId, OwnerStart));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Forget(string? pidFile)
    {
        if (pidFile is null) return;
        try { File.Delete(pidFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>清理上次没收尾的 easytier（Jay 崩溃、被强制结束、看门进程也被结束）。返回结束了几个。</summary>
    internal static int Sweep(string pidDir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(pidDir)) return 0;
        var killed = 0;
        string[] files;
        try { files = Directory.GetFiles(pidDir, "*.pid"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        foreach (var file in files)
        {
            try
            {
                var parts = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var start)
                    || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var owner) || !long.TryParse(parts[3], CultureInfo.InvariantCulture, out var ownerStart))
                {
                    File.Delete(file);
                    continue;
                }
                // 记录它的 Jay 还活着（另一个 Jay 窗口，或者就是现在这个）：别动。
                if (IsSame(owner, ownerStart)) continue;
                if (IsSame(pid, start))
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(true);
                    p.WaitForExit(3000);
                    killed++;
                }
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        }
        return killed;
    }

    /// <summary>PID 还在，而且启动时间对得上（PID 可能被别的程序重用了）。</summary>
    private static bool IsSame(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && StartTicks(p) is { } now && Math.Abs(now - startTicks) < TimeSpan.TicksPerSecond;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
