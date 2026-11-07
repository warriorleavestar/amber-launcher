using System.Diagnostics;
using System.Globalization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 上保证 Jay 没了 easytier-core 也跟着结束（Windows 用作业对象 <see cref="ProcessJob"/>，这里不管）：
/// 启动时套一层 /bin/sh 看门，每秒看一次 Jay 还在不在，不在了（包括 kill -9）就结束 easytier-core。
/// 看门进程自己也被单独结束时，由 <see cref="EasyTierProcessTracker"/> 在下次开始联机前清理。
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

    /// <summary>看门脚本用的 sh 的真实路径（Linux 上 /bin/sh 多半指向 dash 或 bash，进程信息里看到的是真实路径）。</summary>
    internal static string WatcherExecutable { get; } = ResolveShell();

    private static string ResolveShell()
    {
        if (OperatingSystem.IsWindows()) return "/bin/sh";
        // 要和系统报告的进程路径完全一致：/bin 本身也可能是指向 /usr/bin 的链接，所以用 realpath 解析整条路径。
        try
        {
            var resolved = realpath("/bin/sh", IntPtr.Zero);
            if (resolved == IntPtr.Zero) return "/bin/sh";
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved) ?? "/bin/sh"; }
            finally { free(resolved); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return "/bin/sh"; }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern IntPtr realpath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern void free(IntPtr p);

    /// <summary>给 <see cref="EasyTierProcessTracker"/> 记录的可执行文件：套了看门脚本时是 sh，否则是 easytier-core 本身。</summary>
    internal static string TrackedExecutable(ProcessStartInfo info) =>
        info.FileName == "/bin/sh" ? WatcherExecutable : info.FileName;
}
