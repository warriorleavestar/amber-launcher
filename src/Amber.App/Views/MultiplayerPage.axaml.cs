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

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出联机诊断报告",
            SuggestedFileName = $"Amber 联机诊断 {DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportDiagnostics(path);
    }

    private async void ExportChat_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MultiplayerViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出聊天记录",
            SuggestedFileName = $"Amber 联机聊天 {DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExtension = "txt",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportChat(path);
    }
}
