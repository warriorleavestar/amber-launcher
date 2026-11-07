using System.Runtime.InteropServices;

namespace Amber.Core.Launch;

/// <summary>Linux 上请求 JVM 正常结束：SIGTERM 会运行 JVM 关闭钩子，比强制结束更有机会保存世界。</summary>
internal static partial class UnixGameProcess
{
    private const int SigTerm = 15;

    public static bool RequestExit(int processId)
    {
        if (!OperatingSystem.IsLinux()) return false;
        return Kill(processId, SigTerm) == 0;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
