using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

public partial class MultiplayerPage : UserControl
{
    public MultiplayerPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // 新消息来了滚到底。
            if (DataContext is MultiplayerViewModel vm)
            {
                vm.ChatLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd(), DispatcherPriority.Background);
                vm.DebugLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("DebugScroll")?.ScrollToEnd(), DispatcherPriority.Background);
            }
        };
    }

    private void ChatInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MultiplayerViewModel vm)
        {
            vm.SendChatCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void ImportComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.F("multiplayer.component.import_picker", vm.ComponentFileName),
            FileTypeFilter = [new FilePickerFileType(L.T("multiplayer.component.import_filetype")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ImportComponentAsync(path);
    }

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("multiplayer.diagnostics.export_title"),
            SuggestedFileName = L.F("multiplayer.diagnostics.file_name", DateTime.Now) + ".txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportDiagnostics(path);
    }

    private async void ExportChat_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("multiplayer.chat.export_title"),
            SuggestedFileName = L.F("multiplayer.chat.file_name", DateTime.Now) + ".txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportChat(path);
    }
}
