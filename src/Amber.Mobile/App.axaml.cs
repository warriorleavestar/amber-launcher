using Amber.Core.Localization;
using Amber.Mobile.ViewModels;
using Amber.Mobile.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Amber.Mobile;

public sealed class App : Application
{
    private MobileMainViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        L.SetLanguage(null);
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = CreateMainView;
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = CreateMainView();
        base.OnFrameworkInitializationCompleted();
    }

    private MainView CreateMainView()
    {
        _viewModel ??= new MobileMainViewModel();
        return new MainView { DataContext = _viewModel };
    }
}
