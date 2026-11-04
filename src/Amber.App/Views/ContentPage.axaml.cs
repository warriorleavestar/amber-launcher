using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class ContentPage : UserControl
{
    public ContentPage() => InitializeComponent();

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ContentViewModel vm) vm.SearchCommand.Execute(null);
    }

    private async void ImportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContentViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("content.modpack.picker"),
            FileTypeFilter = [new FilePickerFileType(L.T("content.type.modpack")) { Patterns = ["*.mrpack", "*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ImportLocalPackAsync(path);
    }
}
