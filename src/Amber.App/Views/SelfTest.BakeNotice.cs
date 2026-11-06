using System.Diagnostics;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Diagnostics;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>隔离数据目录内的烘焙警告布局/交互自检；不启动游戏，不访问下载服务。</summary>
internal sealed class BakeNoticeSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];
    private BakeNoticeHost Host => window.GetVisualDescendants().OfType<BakeNoticeHost>().Single();
    private BakeNoticeState State => Toasts.BakeNotices!;
    private HomeViewModel Home => main.Pages.OfType<HomeViewModel>().Single();
    private HomePage HomeView => window.GetVisualDescendants().OfType<HomePage>().Single();

    private void Check(string name, bool ok)
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        if (!ok) throw new InvalidOperationException(name);
    }

    private void Show(string id, Action? retry = null) => Toasts.BakeFailed(L.F("home.bake.crashed_toast", id), retry ?? (() => { }));

    private static Rect BoundsIn(Control control, Visual target) =>
        new(control.TranslatePoint(default, target) ?? default, control.Bounds.Size);

    private async Task ShotAsync(string name)
    {
        await Task.Delay(300);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
    }

    private async Task<double> EmptyCardHeightAsync()
    {
        State.Clear();
        await Task.Delay(100);
        window.UpdateLayout();
        return HomeView.FindControl<Border>("LaunchStatusCard")!.Bounds.Height;
    }

    private async Task CheckLayoutAsync(string name, double originalCardHeight, bool multiple = false)
    {
        // 新增内容的模板可能晚于通知集合更新；等真实控件出现再检查，不用固定延时猜布局完成。
        for (var i = 0; i < 40; i++)
        {
            window.UpdateLayout();
            if (State.HasNotice && Host.IsEffectivelyVisible && Host.Bounds.Height > 0
                && Host.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == L.T("home.bake.retry_without"))) break;
            await Task.Delay(50);
        }
        Check(name + "：警告已在状态框呈现", Host.IsVisible && State.HasNotice && Host.Bounds.Height > 0);
        var homePage = HomeView;
        var primary = homePage.FindControl<Button>("PrimaryButton")!;
        var primaryBounds = BoundsIn(primary, window);
        var noticeBounds = BoundsIn(Host, window);
        var card = homePage.FindControl<Border>("LaunchStatusCard")!;
        Check(name + "：提示完整位于原状态框", BoundsIn(card, window).Contains(noticeBounds));
        Check(name + "：状态框不因提示增高", card.Bounds.Height <= originalCardHeight + 1);
        Check(name + "：主启动按钮不被遮挡且仍可操作", primaryBounds.Bottom <= window.Bounds.Height
            && noticeBounds.Right <= primaryBounds.Left && primary.IsEffectivelyEnabled);
        var retry = Host.GetVisualDescendants().OfType<Button>().First(b => b.Command is not null && b.Content?.ToString() == L.T("home.bake.retry_without"));
        Check(name + "：紧凑重试按钮完整可见", BoundsIn(card, window).Contains(BoundsIn(retry, window)));
        var description = Host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == State.Current!.Text);
        Check(name + "：长说明最多两行且完整内容保留在提示框", description.Bounds.Height <= 36 && ToolTip.GetTip(description)?.ToString() == State.Current!.Text);
        var homeScroller = homePage.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(name + "：崩溃详情保留可滚动区域", homeScroller.Bounds.Height >= 60);
        homeScroller.Offset = new Vector(0, homeScroller.Extent.Height);
        await Task.Delay(100);
        var log = homePage.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == L.T("home.crash.view_log"));
        var logBounds = BoundsIn(log, homeScroller);
        Check(name + "：滚动后崩溃操作可达", logBounds.Top >= 0 && logBounds.Bottom <= homeScroller.Bounds.Height);
        if (multiple) Check(name + "：三条通知只呈现最新一条", State.Notices.Count == 3 && retry.Command == State.Current!.RetryCommand
            && Host.GetVisualDescendants().OfType<Button>().Count() == 1);
        await ShotAsync(name);
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("AMBER_DATA_DIR");
            Check("自检使用显式独立数据/游戏目录", !string.IsNullOrWhiteSpace(dataDir)
                && Path.GetFullPath(Home.Service.Directory.Root).StartsWith(Path.GetFullPath(dataDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            main.CurrentPage = Home;
            var settingsBefore = File.ReadAllText(AppSettings.FilePath);
            const string instance = "E2E-Bake-Sodium";
            var original = new LaunchRequest(instance, JoinServer: "localhost:25565", JoinWorld: null);
            var request = original with { SkipBake = true };
            var calls = new List<LaunchRequest>();
            foreach (var theme in new[] { "dark", "light" })
            {
                App.ApplyTheme(theme);
                foreach (var size in new[] { (1280, 700), (760, 440) })
                {
                    L.SetLanguage("zh-Hans");
                    window.Width = size.Item1;
                    window.Height = size.Item2;
                    Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
                    var cardHeight = await EmptyCardHeightAsync();
                    Show(instance, () => calls.Add(request));
                    await CheckLayoutAsync($"{theme}-{size.Item1}x{size.Item2}", cardHeight);
                    Check("截图尺寸与文件名一致", window.Bounds.Width == size.Item1 && window.Bounds.Height == size.Item2);
                }
                var threeCardHeight = await EmptyCardHeightAsync();
                Show("Earlier-instance-A");
                Show("Earlier-instance-B");
                Show(instance, () => calls.Add(request));
                await CheckLayoutAsync($"{theme}-minimum-three-notices", threeCardHeight, multiple: true);
            }
            App.ApplyTheme("dark");
            main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
            State.Clear();
            Show(instance, () => calls.Add(request));
            await Task.Delay(100);
            var beforeNavigation = State.Current;
            Check("非启动页发生的失败仍保存通知", beforeNavigation is not null);
            main.CurrentPage = Home;
            await Task.Delay(100);
            window.UpdateLayout();
            Check("返回启动页绑定原通知状态和实例回调", ReferenceEquals(Host.DataContext, State) && State.Current == beforeNavigation);
            foreach (var language in new[] { "zh-Hant", "en", "ja" })
            {
                L.SetLanguage(language);
                Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
                var cardHeight = await EmptyCardHeightAsync();
                var displayedInstance = language == "zh-Hant" ? instance : "Sodium-Fabric-1.21.1-with-extra-long-instance-name-for-layout-validation";
                Show(displayedInstance);
                await CheckLayoutAsync($"{language}-minimum", cardHeight);
            }
            State.Clear();
            Show(instance, () => calls.Add(request));
            await Task.Delay(100);
            window.UpdateLayout();
            var button = Host.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == L.T("home.bake.retry_without"));
            var pendingRetry = State.Current!.RetryCommand;
            Check("重试按钮可通过自动化接口点击", button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IInvokeProvider>()!.Invoke();
            for (var i = 0; i < 40 && calls.Count == 0; i++) await Task.Delay(50);
            Check("实际按钮点击已触发一次回调并移除通知", calls.Count == 1 && State.Notices.Count == 0);
            pendingRetry.Execute(null);
            Check("按钮只回调一次且保留实例/加入目标/SkipBake", calls.Count == 1 && calls[0] == request && request.VersionId == original.VersionId);
            Check("按钮移除通知，烘焙设置未改变", State.Notices.Count == 0 && File.ReadAllText(AppSettings.FilePath) == settingsBefore);
            window.UpdateLayout();
            Check("通知结束后原状态文字恢复", !Host.IsVisible && HomeView.GetVisualDescendants().OfType<StatusBar>().Single().IsVisible);
            using (var simulatedGame = Process.GetCurrentProcess())
            {
                var run = Home.AddRunForTest(instance, simulatedGame);
                try
                {
                    Show("Earlier-instance-A", () => calls.Add(request));
                    Show("Earlier-instance-B", () => calls.Add(request));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    window.UpdateLayout();
                    Check("活动局的状态及进度优先显示，不被旧通知遮住", State.Notices.Count == 2 && !Host.IsEffectivelyVisible
                        && HomeView.GetVisualDescendants().OfType<StatusBar>().Single().IsEffectivelyVisible && Home.ShowProgress);
                    var stale = State.Notices.ToArray();
                    // 已有模拟运行局：真实启动请求进入多开确认后返回，不进入下载或启动流程。
                    Home.Service.RequestLaunch(original);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Check("新的启动请求清除所有旧通知", !State.HasNotice && Home.PendingLaunch == original);
                    foreach (var notice in stale) notice.RetryCommand.Execute(null);
                    Check("旧通知的回调全部失效", calls.Count == 1);
                }
                finally
                {
                    // 先处理可能仍在排队的请求，再撤下模拟局，避免它落入真实下载/启动流程。
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Home.DismissPendingLaunchCommand.Execute(null);
                    Home.RemoveRunForTest(run);
                }
            }
            main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
            Show(instance, () => calls.Add(request));
            await Task.Delay(50);
            var clock = Stopwatch.StartNew();
            await Task.Delay(6500);
            Check("停留在其他页面时八秒前仍保留重试机会", State.Notices.Count == 1);
            main.CurrentPage = Home;
            while (State.Notices.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(11)) await Task.Delay(100);
            Check("回到启动页不重置八秒期限且不调用重试", State.Notices.Count == 0 && clock.Elapsed >= TimeSpan.FromSeconds(7.8) && calls.Count == 1);
            await CheckInstanceBakeEntryAsync(instance);
        }
        catch (Exception ex)
        {
            _report.Add("FAIL " + ex);
            Environment.ExitCode = 1;
            await ShotAsync("failed-state");
        }
        finally
        {
            var closedCalls = 0;
            var state = Toasts.BakeNotices!;
            var closingNotice = state.Show("window-close", () => closedCalls++);
            window.Closed += (_, _) =>
            {
                closingNotice.RetryCommand.Execute(null);
                var closed = state.Notices.Count == 0 && closedCalls == 0;
                _report.Add($"{(closed ? "PASS" : "FAIL")} 关闭窗口会清空警告并使旧按钮失效");
                if (!closed) Environment.ExitCode = 1;
            };
            window.Close();
            if (!_report.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal))) _report.Add("DONE");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        }
    }

    /// <summary>合并后的实例页仍使用现有模组工具栏；只检查入口，不触发补文件或烘焙进程。</summary>
    private async Task CheckInstanceBakeEntryAsync(string instance)
    {
        var page = main.Pages.OfType<InstanceViewModel>().Single();
        main.CurrentPage = page;
        await Task.Delay(200);
        page.SelectedId = instance;
        page.TabIndex = (int)InstanceTab.Mods;
        Check("实例页保留模组页签及实验烘焙入口", page.Current?.Id == instance && page.BakeEnabled);
        foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            L.SetLanguage(language);
            foreach (var (width, height) in new[] { (760, 440), (1280, 700) })
            {
                window.Width = width;
                window.Height = height;
                await Task.Delay(200);
                window.UpdateLayout();
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(b => ReferenceEquals(b.Command, page.BakeNowCommand));
                button.BringIntoView();
                await Task.Delay(100);
                var bounds = BoundsIn(button, window);
                var hit = window.InputHitTest(bounds.Center) as Visual;
                Check($"实例烘焙 {language}-{width}：入口完整可见且中心命中", button.IsEffectivelyVisible
                    && button.IsEffectivelyEnabled && new Rect(window.ClientSize).Contains(bounds)
                    && hit is not null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button)));
                Check($"实例烘焙 {language}-{width}：按钮文案及提示随语言切换", button.Content?.ToString() == L.T("instance.bake.now")
                    && ToolTip.GetTip(button)?.ToString() == L.T("instance.bake.now_hint"));
                await ShotAsync($"instance-bake-{language}-{width}x{height}");
            }
        }
    }
}
