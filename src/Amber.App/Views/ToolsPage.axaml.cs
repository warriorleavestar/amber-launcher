using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class ToolsPage : UserControl
{
    public ToolsPage() => InitializeComponent();

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("tools.download.folder_picker") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.DownloadFolder = path;
    }
}
