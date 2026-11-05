using System.Runtime.InteropServices;

namespace Amber.App;

/// <summary>
/// 开发用的自检、截图、布局检查、技术验证这些无人值守的运行方式：出现未处理的异常时把异常写进报告目录的 crash.txt，
/// 然后以退出码 3 结束，不弹 Windows 的“应用程序错误”对话框（那个对话框会一直挂在桌面上等人点）。
/// 只对这个进程生效（SetErrorMode 不改系统设置），正常给用户用的时候不启用。
/// </summary>
internal static class DevCrashGuard
{
    private static readonly string[] Switches = ["AMBER_SELFTEST", "AMBER_LAYOUTCHECK", "AMBER_SPIKE", "AMBER_SNAPSHOT_DIR", "AMBER_MEASURE", "AMBER_STARTUP_LOG"];

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    public static bool Enabled { get; private set; }

    private static string? _crashFile;

    public static void InstallIfUnattended()
    {
        var active = Switches.Select(s => (Name: s, Value: Environment.GetEnvironmentVariable(s))).FirstOrDefault(s => !string.IsNullOrEmpty(s.Value));
        if (active.Value is null) return;
        Enabled = true;
        // 报告目录类的开关写到目录里；文件类的（AMBER_MEASURE、AMBER_STARTUP_LOG）写到同一个文件夹。
        var dir = active.Name is "AMBER_MEASURE" or "AMBER_STARTUP_LOG" ? Path.GetDirectoryName(Path.GetFullPath(active.Value)) : active.Value;
        _crashFile = dir is null ? null : Path.Combine(dir, "crash.txt");
        if (OperatingSystem.IsWindows()) SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Avalonia 界面线程上的未处理异常（async void 事件处理里抛出的）也走这里。</summary>
    public static void HookDispatcher()
    {
        if (!Enabled) return;
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Fail("Dispatcher.UnhandledException", e.Exception);
        };
    }

    private static void Fail(string source, Exception? e)
    {
        Write(source, e);
        Environment.Exit(3);
    }

    private static void Write(string source, Exception? e)
    {
        var text = $"{DateTime.Now:O} {source}{Environment.NewLine}{e}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            Console.Error.Write(text);
            if (_crashFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_crashFile)!);
                File.AppendAllText(_crashFile, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
