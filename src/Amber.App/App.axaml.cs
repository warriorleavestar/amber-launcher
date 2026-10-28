using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Amber.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var service = new LauncherService(AppSettings.Load());
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(service) };
            desktop.Exit += (_, _) => service.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
