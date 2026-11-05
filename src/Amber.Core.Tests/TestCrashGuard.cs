using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Amber.Core.Tests;

/// <summary>
/// 测试进程里（后台线程、没等待的 Task）出现未处理的异常时：写到标准错误、以退出码 3 结束，
/// 不弹 Windows 的“应用程序错误”对话框（无人值守跑测试时那个对话框会一直挂着）。只影响测试进程本身。
/// </summary>
internal static class TestCrashGuard
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [ModuleInitializer]
    internal static void Install()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Console.Error.WriteLine("Unhandled exception in test host: " + e.ExceptionObject); } catch (IOException) { }
            Environment.Exit(3);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Console.Error.WriteLine("Unobserved task exception in test host: " + e.Exception); } catch (IOException) { }
            e.SetObserved();
        };
    }
}
