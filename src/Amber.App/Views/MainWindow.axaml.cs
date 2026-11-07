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
        Toasts.Attach(this, new Controls.BakeNoticeState());
        DataContextChanged += (_, _) => AttachGameWindowBehaviour();
        // Esc 取消所有“再点一次确认”的按钮；Ctrl+Z 撤销最近一次删除（焦点在输入框里时留给输入框自己的撤销）。
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Controls.ConfirmButton.CancelAll();
            else if (e.Key == Avalonia.Input.Key.Z && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control
                     && FocusManager?.GetFocusedElement() is not TextBox)
                e.Handled = Toasts.UndoLatest();
        }, RoutingStrategies.Tunnel);
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
        // 开发用：AMBER_SPIKE=报告目录，界面改版第 0 批的技术验证。
        if (Environment.GetEnvironmentVariable("AMBER_SPIKE") is { Length: > 0 } spikeDir)
            Opened += async (_, _) => await new Spike(this, spikeDir).RunAsync();
        // 开发用：AMBER_SELFTEST=报告目录，走一遍端到端自检。
        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 } report)
            Opened += async (_, _) =>
            {
                if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE") == "content-layout")
                    await new ContentPageSelfTest(this, (MainViewModel)DataContext!, report).RunAsync();
                else if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE") == "remote")
                    await new RemoteSelfTest(this, (MainViewModel)DataContext!, report).RunAsync();
                else if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE") == "bake-notice")
                    await new BakeNoticeSelfTest(this, (MainViewModel)DataContext!, report).RunAsync();
                else
                    await new SelfTest(this, (MainViewModel)DataContext!, report).RunAsync();
            };
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

    /// <summary>最小尺寸（见 WindowSizing）。屏幕放不下时再往下收到可用区域。</summary>
    public const double PreferredMinWidth = Amber.Core.Layout.WindowSizing.MinWidth;
    public const double PreferredMinHeight = Amber.Core.Layout.WindowSizing.MinHeight;

    /// <summary>
    /// 按屏幕的可用区域定窗口大小和位置：恢复上次的（还在某块屏幕上时），否则按可用区域的比例取默认大小并居中；
    /// 都不超过可用区域。不按哪一种屏幕定死大小。
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
        var (workW, workH) = (work.Width / scale, work.Height / scale);
        (MinWidth, MinHeight) = Amber.Core.Layout.WindowSizing.Minimum(workW, workH);
        var onScreen = saved is not null && screen.WorkingArea.Contains(new PixelPoint(saved.X + 40, saved.Y + 20));
        (Width, Height) = onScreen
            ? Amber.Core.Layout.WindowSizing.Restore(saved!.Width, saved.Height, workW, workH)
            : Amber.Core.Layout.WindowSizing.Default(workW, workH);
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

    private HomeViewModel? _home;
    private bool _minimizedForGame;

    /// <summary>
    /// “启动后启动器”（改版规格 3.6）：默认游戏起来后最小化，正常退出后恢复；崩溃时总是恢复并放到前台，显示崩溃卡。
    /// 托盘还没做，设成 tray 时先当作保持不动。自检时不动窗口（还要截图）。
    /// </summary>
    private void AttachGameWindowBehaviour()
    {
        if (DataContext is not MainViewModel main || main.Pages.OfType<HomeViewModel>().FirstOrDefault() is not { } home || ReferenceEquals(home, _home)) return;
        _home = home;
        home.GameStarted += () => Dispatcher.UIThread.Post(() =>
        {
            if (_settings?.AfterLaunch != "minimize" || Environment.GetEnvironmentVariable("AMBER_SELFTEST") is { Length: > 0 }) return;
            _minimizedForGame = true;
            WindowState = WindowState.Minimized;
        });
        home.GameExited += crashed => Dispatcher.UIThread.Post(() =>
        {
            if (crashed || _minimizedForGame)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
            _minimizedForGame = false;
        });
        // 多开时一局崩了、另一局还在跑：GameExited 不会来，这里单独把窗口拿回来并提示。
        home.GameCrashed += id => Dispatcher.UIThread.Post(() => OnGameCrashed(home, id));
        home.Service.SettingsSaveFailed += e => Dispatcher.UIThread.Post(() => OnSettingsSaveFailed(e));
    }

    private void OnGameCrashed(HomeViewModel home, string id)
    {
        var wasHidden = WindowState == WindowState.Minimized || !IsActive;
        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST") is not { Length: > 0 })
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        // 不在启动页时启动页自己已经弹了 Toast；在启动页但窗口刚才看不见时，这里补一条。
        if (wasHidden && home.IsCurrent) Toasts.WithAction(L.F("home.crash.toast", id), L.T("home.crash.show"), () => home.NavigateHere?.Invoke());
        CrashToastsShown++;
    }

    /// <summary>崩溃时窗口这边处理过几次（自检用）。</summary>
    internal int CrashToastsShown { get; private set; }

    private DateTime _lastSaveWarning;

    /// <summary>设置文件写不进去：提醒一声（30 秒内只提醒一次），不挡操作。</summary>
    private void OnSettingsSaveFailed(Exception e)
    {
        if (DateTime.UtcNow - _lastSaveWarning < TimeSpan.FromSeconds(30)) return;
        _lastSaveWarning = DateTime.UtcNow;
        Toasts.Show(L.F("settings.save_failed", ErrorText.Describe(e)), Avalonia.Controls.Notifications.NotificationType.Warning);
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
