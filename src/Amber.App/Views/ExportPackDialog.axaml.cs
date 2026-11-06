using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

/// <summary>导出整合包对话框：选格式、名字、版本和要带的文件，点“保存”选位置后导出，导出进度显示在实例页底部的状态条。</summary>
public partial class ExportPackDialog : Window
{
    public ExportPackDialog()
    {
        InitializeComponent();
        Opened += (_, _) => CancelButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstanceViewModel vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("instance.export.pack_picker"),
            SuggestedFileName = $"{vm.ExportName}-{vm.ExportVersion}.{vm.ExportExtension}",
            DefaultExtension = vm.ExportExtension,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Close();
        await vm.ExportPackAsync(path);
    }
}
