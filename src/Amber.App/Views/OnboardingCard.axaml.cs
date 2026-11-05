using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

public partial class OnboardingCard : UserControl
{
    private OnboardingViewModel? _vm;

    public OnboardingCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusRequested -= FocusStep;
            _vm = DataContext as OnboardingViewModel;
            if (_vm is not null) _vm.FocusRequested += FocusStep;
        };
    }

    /// <summary>启动按钮在缺东西时点了：滚到卡上，焦点放到这一步的输入框或安装按钮。</summary>
    private void FocusStep(OnboardingStep step) => Dispatcher.UIThread.Post(() =>
    {
        Control target = step == OnboardingStep.Account ? PlayerNameBox : InstallLatestButton;
        target.BringIntoView();
        target.Focus();
    });

    private void PlayerName_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm?.AddOfflineCommand.CanExecute(null) == true) _vm.AddOfflineCommand.Execute(null);
    }

    private async void PickDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("onboarding.detect.picker") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) _vm.UseDirectory(path);
    }

    private async void ImportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ImportPack is not { } import || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("content.modpack.picker"),
            FileTypeFilter = [new FilePickerFileType(L.T("content.type.modpack")) { Patterns = ["*.mrpack", "*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await import(path);
    }
}
