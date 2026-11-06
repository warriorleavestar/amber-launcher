using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Amber.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        StartupTrace.Mark("platform");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Mark("theme");
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DevCrashGuard.HookDispatcher();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            ApplyTheme(settings.Theme);
            Amber.Core.Localization.L.SetLanguage(settings.Language);
            var service = new LauncherService(settings);
            var viewModel = new MainViewModel(service);
            StartupTrace.Mark("viewmodels");
            desktop.MainWindow = new MainWindow(settings) { DataContext = viewModel };
            StartupTrace.Mark("window");
            desktop.Exit += (_, _) => service.Dispose();
            // 开发截图只查看界面，不清理游戏回收目录或真实用户的无痕配置。
            if (Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is not { Length: > 0 }
                && Environment.GetEnvironmentVariable("AMBER_SELFTEST") is not { Length: > 0 })
            {
                // 回收目录里过期的条目放到后台清，不拖慢启动。
                _ = Task.Run(() =>
                {
                    try { service.PurgeTrash(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                });
                // 清掉以前留下的无痕模式临时配置（Amber 没等到游戏退出就关了、崩溃了之类）。
                _ = Task.Run(() => Amber.Core.Launch.GameLauncher.SweepNoTrace());
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
