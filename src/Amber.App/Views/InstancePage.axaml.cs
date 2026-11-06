using Amber.Core.Localization;
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

    private async Task PickAndAddAsync(string title, string filterName, string[] patterns, string? target)
    {
        if (Vm is not { } vm || Storage is not { } storage || target is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) vm.AddFiles(paths, target);
    }

    private async void AddMods_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.mods.picker"), L.T("instance.picker.mods_filter"), ["*.jar", "*.zip"], Vm?.ModsDir);

    private async void AddResourcePacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.resourcepacks.picker"), L.T("instance.picker.resourcepacks_filter"), ["*.zip"], Vm?.ResourcePacksDir);

    private async void AddShaderPacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.shaderpacks.picker"), L.T("instance.picker.shaderpacks_filter"), ["*.zip"], Vm?.ShaderPacksDir);

    private async void AddDatapacks_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("instance.datapacks.picker"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(L.T("instance.picker.datapacks_filter")) { Patterns = ["*.zip"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) vm.AddDatapacks(paths);
    }

    private async void ImportWorld_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("instance.worlds.picker"),
            FileTypeFilter = [new FilePickerFileType(L.T("common.archive")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.ImportWorld(path);
    }

    /// <summary>【⋯ 更多】→“导出整合包…”：按当前实例填好选项，打开对话框。</summary>
    private async void ExportPackDialog_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { HasInstance: true } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        vm.PrepareExport();
        await new ExportPackDialog { DataContext = vm }.ShowDialog(owner);
    }

    private async void ExportScript_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var extension = OperatingSystem.IsWindows() ? "bat" : "sh";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("instance.export.script_picker"),
            SuggestedFileName = $"{vm.SelectedId}.{extension}",
            DefaultExtension = extension,
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportLaunchScriptAsync(path);
    }
}
