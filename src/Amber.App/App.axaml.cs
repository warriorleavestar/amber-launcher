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
        }
        base.OnFrameworkInitializationCompleted();
    }
}
