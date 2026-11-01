using Amber.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class InstancePage : UserControl
{
    public InstancePage() => InitializeComponent();

    private InstanceViewModel? Vm => DataContext as InstanceViewModel;
    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task PickAndAddAsync(string title, string[] patterns, string? target)
    {
        if (Vm is not { } vm || Storage is not { } storage || target is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) vm.AddFiles(paths, target);
    }

    private async void AddMods_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync("选择模组文件", ["*.jar", "*.zip"], Vm?.ModsDir);

    private async void AddResourcePacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync("选择资源包", ["*.zip"], Vm?.ResourcePacksDir);

    private async void AddShaderPacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync("选择光影包", ["*.zip"], Vm?.ShaderPacksDir);

    private async void ImportWorld_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择存档压缩包",
            FileTypeFilter = [new FilePickerFileType("压缩包") { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.ImportWorld(path);
    }

    private async void ExportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出整合包",
            SuggestedFileName = $"{vm.ExportName}-{vm.ExportVersion}.{vm.ExportExtension}",
            DefaultExtension = vm.ExportExtension,
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportPackAsync(path);
    }

    private async void ExportScript_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var extension = OperatingSystem.IsWindows() ? "bat" : "sh";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出启动脚本",
            SuggestedFileName = $"{vm.SelectedId}.{extension}",
            DefaultExtension = extension,
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportLaunchScriptAsync(path);
    }
}
