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
            // 回收目录里过期的条目放到后台清，不拖慢启动。
            _ = Task.Run(() =>
            {
                try { service.PurgeTrash(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
