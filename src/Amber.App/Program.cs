using Avalonia;

namespace Amber.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Mark("main");
        // 开发用：伪本地化，检查长译文和漏翻的文字。
        if (args.Contains("--pseudo-loc") || Environment.GetEnvironmentVariable("AMBER_PSEUDO_LOC") == "1")
            Amber.Core.Localization.L.Pseudo = true;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

/// <summary>开发用：AMBER_STARTUP_LOG=文件，记录启动各阶段距进程启动的毫秒数。没设置时什么都不做。</summary>
internal static class StartupTrace
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AMBER_STARTUP_LOG") is { Length: > 0 } path ? path : null;
    private static readonly System.Text.StringBuilder Marks = new();

    public static bool Enabled => LogPath is not null;

    public static void Mark(string name)
    {
        if (LogPath is null) return;
        var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        Marks.Append($" {name}={elapsed.TotalMilliseconds:F0}");
    }

    public static void Flush()
    {
        if (LogPath is not null) File.AppendAllText(LogPath, Marks.ToString().Trim() + "\n");
    }
}
