using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 开发用：设置 AMBER_SNAPSHOT_DIR 后，依次把每个页面渲染成 PNG 然后退出。
        if (Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is { Length: > 0 } dir)
            Opened += async (_, _) => await SnapshotPagesAsync(dir);
    }

    private async Task SnapshotPagesAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        if (DataContext is not MainViewModel vm) return;
        var delay = int.TryParse(Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DELAY"), out var d) ? d : 3000;
        for (var i = 0; i < vm.Pages.Count; i++)
        {
            vm.CurrentPage = vm.Pages[i];
            await Task.Delay(delay);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var bitmap = new RenderTargetBitmap(size);
                bitmap.Render(this);
                bitmap.Save(Path.Combine(dir, $"{i + 1}-{vm.Pages[i].Title}.png"));
            });
            // AMBER_SNAPSHOT_DEMO=1：页面带演示流程的，走一遍真实操作后再截一张。
            if (Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DEMO") == "1" && vm.Pages[i] is ISnapshotDemo demo)
            {
                await demo.RunSnapshotDemoAsync();
                await Task.Delay(500);
                await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, $"{i + 1}-{vm.Pages[i].Title}-演示.png")));
            }
        }
        // AMBER_SNAPSHOT_LAUNCH=秒数：再用启动页真实启动一次游戏，等待后截日志页并结束游戏。
        if (int.TryParse(Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_LAUNCH"), out var seconds) && vm.Pages[0] is HomeViewModel home)
        {
            vm.CurrentPage = home;
            _ = home.LaunchCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, "5-启动中.png")));
            vm.CurrentPage = vm.Pages[^1];
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, "6-游戏日志.png")));
            home.CancelCommand.Execute(null);
            await Task.Delay(1500);
        }
        Close();
    }

    private void Save(string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private async void BrowseGameDir_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SettingsViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择游戏目录（.minecraft）" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.GameDirectory = path;
    }
}
