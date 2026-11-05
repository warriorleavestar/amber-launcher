using Amber.Core.Localization;
using Amber.App.Services;
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
    private readonly AppSettings? _settings;

    public MainWindow() : this(null) { }

    public MainWindow(AppSettings? settings)
    {
        _settings = settings;
        InitializeComponent();
        FitToScreen(settings?.LauncherWindow);
        // 窗口打开后才知道标题栏有多高：Width/Height 是客户区，加上标题栏可能比屏幕的可用区域还高（底下被任务栏挡住）。
        Opened += (_, _) => FitFrameToScreen();
        Closing += (_, _) => RememberPlacement();
        // 开发用：设置 AMBER_SNAPSHOT_DIR 后，依次把每个页面渲染成 PNG 然后退出。
        if (Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR") is { Length: > 0 } dir)
            Opened += async (_, _) => await SnapshotPagesAsync(dir);
        // 开发用：AMBER_LAYOUTCHECK=报告目录，按各种窗口尺寸检查每个页面有没有被切掉的东西，截图后退出。
        if (Environment.GetEnvironmentVariable("AMBER_LAYOUTCHECK") is { Length: > 0 } layoutDir)
            Opened += async (_, _) =>
            {
                var problems = await new LayoutCheck(this, (MainViewModel)DataContext!, layoutDir).RunAsync();
                File.AppendAllText(Path.Combine(layoutDir, "layout.txt"), $"total {problems.Count}" + Environment.NewLine);
                Close();
            };
        // 开发用：AMBER_SELFTEST=报告目录，走一遍端到端自检。
        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 } report)
            Opened += async (_, _) => await new SelfTest(this, (MainViewModel)DataContext!, report).RunAsync();
        // 开发用：AMBER_MEASURE=文件，记下屏幕可用区域、缩放、窗口外框和客户区的大小，然后退出。
        if (Environment.GetEnvironmentVariable("AMBER_MEASURE") is { Length: > 0 } measure)
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                var frame = FrameSize;
                File.WriteAllText(measure, string.Join(Environment.NewLine,
                    $"scaling={screen?.Scaling}",
                    $"screen_px={screen?.Bounds.Width}x{screen?.Bounds.Height}",
                    $"work_px={screen?.WorkingArea.Width}x{screen?.WorkingArea.Height}",
                    $"work_logical={screen?.WorkingArea.Width / screen?.Scaling:F1}x{screen?.WorkingArea.Height / screen?.Scaling:F1}",
                    $"frame_logical={frame?.Width:F1}x{frame?.Height:F1}",
                    $"client_logical={ClientSize.Width:F1}x{ClientSize.Height:F1}",
                    $"chrome_logical={frame?.Width - ClientSize.Width:F1}x{frame?.Height - ClientSize.Height:F1}") + Environment.NewLine);
                Close();
            }, DispatcherPriority.Background);
        // 开发用：记录启动各阶段耗时，窗口打开后退出。
        if (StartupTrace.Enabled)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                StartupTrace.Mark("opened");
                StartupTrace.Flush();
                Close();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>最小尺寸：放得下侧边栏和一列内容。小屏幕（比如 1280×720 开 150% 缩放，只有约 853×448）上再往下收。</summary>
    public const double PreferredMinWidth = 760;
    public const double PreferredMinHeight = 440;

    /// <summary>
    /// 按屏幕的可用区域定窗口大小和位置：恢复上次的（还在某块屏幕上时），否则默认大小居中；都不超过可用区域。
    /// 之前固定 1080×700、最小 900×560，在小屏幕或高缩放下窗口比屏幕还大，下面和右边被切掉。
    /// </summary>
    private void FitToScreen(WindowPlacement? saved)
    {
        var screens = Screens;
        var screen = saved is not null
            ? screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(saved.X + 40, saved.Y + 20))) ?? screens.Primary
            : screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var maxWidth = work.Width / scale;
        var maxHeight = work.Height / scale;
        MinWidth = Math.Min(PreferredMinWidth, maxWidth);
        MinHeight = Math.Min(PreferredMinHeight, maxHeight);
        var onScreen = saved is not null && screen.WorkingArea.Contains(new PixelPoint(saved.X + 40, saved.Y + 20));
        Width = Math.Clamp(onScreen ? saved!.Width : Width, MinWidth, maxWidth);
        Height = Math.Clamp(onScreen ? saved!.Height : Height, MinHeight, maxHeight);
        var pixelWidth = (int)(Width * scale);
        var pixelHeight = (int)(Height * scale);
        WindowStartupLocation = WindowStartupLocation.Manual;
        var x = onScreen ? saved!.X : work.X + (work.Width - pixelWidth) / 2;
        var y = onScreen ? saved!.Y : work.Y + (work.Height - pixelHeight) / 2;
        // 整个窗口都要在可用区域里（标题栏不能跑到屏幕外拖不回来）。
        Position = new PixelPoint(Math.Clamp(x, work.X, Math.Max(work.X, work.Right - pixelWidth)), Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - pixelHeight)));
        if (onScreen && saved!.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 按实际的窗口外框收一次：客户区高度 + 标题栏不超过可用区域，窗口上沿在可用区域里。
    /// 外框里还有看不见的缩放边（左右和下边各约 7 像素），按左右的宽度扣掉，不算进标题栏。
    /// </summary>
    private void FitFrameToScreen()
    {
        if (WindowState != WindowState.Normal || FrameSize is not { } frame) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var side = Math.Max(0, (frame.Width - ClientSize.Width) / 2);
        var title = Math.Max(0, frame.Height - ClientSize.Height - side);
        var maxClientHeight = work.Height / scale - title;
        if (maxClientHeight <= 0) return;
        MinHeight = Math.Min(MinHeight, maxClientHeight);
        if (Height > maxClientHeight) Height = maxClientHeight;
        var visibleHeight = (int)Math.Ceiling((title + Height) * scale);
        var y = Math.Clamp(Position.Y, work.Y, Math.Max(work.Y, work.Bottom - visibleHeight));
        if (y != Position.Y) Position = new PixelPoint(Position.X, y);
    }

    private void RememberPlacement()
    {
        if (_settings is null || Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 }) return;
        var maximized = WindowState == WindowState.Maximized;
        var old = _settings.LauncherWindow;
        _settings.LauncherWindow = new WindowPlacement
        {
            // 最大化时记最大化之前的位置和大小（Avalonia 没直接给，就沿用上次记下的）。
            X = maximized && old is not null ? old.X : Position.X,
            Y = maximized && old is not null ? old.Y : Position.Y,
            Width = maximized && old is not null ? old.Width : Width,
            Height = maximized && old is not null ? old.Height : Height,
            Maximized = maximized,
        };
        try { _settings.Save(); } catch (IOException) { }
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
                await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, $"{i + 1}-{vm.Pages[i].Title}-demo.png")));
            }
        }
        // AMBER_SNAPSHOT_LAUNCH=秒数：再用启动页真实启动一次游戏，等待后截日志页并结束游戏。
        if (int.TryParse(Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_LAUNCH"), out var seconds) && vm.Pages[0] is HomeViewModel home)
        {
            vm.CurrentPage = home;
            _ = home.LaunchCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, "5-launching.png")));
            vm.CurrentPage = vm.Pages[^1];
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => Save(Path.Combine(dir, "6-game-log.png")));
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("settings.directory.picker") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.GameDirectory = path;
    }
}
