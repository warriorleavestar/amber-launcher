using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private async void ExportCrash_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("home.crash.export_title"),
            SuggestedFileName = $"amber-error-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportCrashBundle(path);
    }

    private void CloseForceKillFlyout(object? sender, RoutedEventArgs e) => ForceKillButton.Flyout?.Hide();
}
