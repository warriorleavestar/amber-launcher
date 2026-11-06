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
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>隔离数据目录内的烘焙警告布局/交互自检；不启动游戏，不访问下载服务。</summary>
internal sealed class BakeNoticeSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];
    private BakeNoticeHost Host => window.FindControl<BakeNoticeHost>("BakeWarnings")!;
    private HomeViewModel Home => main.Pages.OfType<HomeViewModel>().Single();

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

    private async Task CheckLayoutAsync(string name, bool multiple = false)
    {
        // 新增内容的模板可能晚于通知集合更新；等真实控件出现再检查，不用固定延时猜布局完成。
        for (var i = 0; i < 40; i++)
        {
            window.UpdateLayout();
            if (Host.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == L.T("home.bake.retry_without"))) break;
            await Task.Delay(50);
        }
        Check(name + "：警告已在内容区呈现", Host.IsVisible && Host.Notices.Count > 0 && Host.Bounds.Height > 0);
        var homePage = window.GetVisualDescendants().OfType<HomePage>().Single();
        var primary = homePage.FindControl<Button>("PrimaryButton")!;
        var primaryBounds = BoundsIn(primary, window);
        var noticeBounds = BoundsIn(Host, window);
        Check(name + "：启动按钮在窗口内且不被警告覆盖", primaryBounds.Bottom <= window.Bounds.Height && primaryBounds.Top >= noticeBounds.Bottom);
        var warningScroller = Host.GetVisualDescendants().OfType<ScrollViewer>().First();
        var retry = Host.GetVisualDescendants().OfType<Button>().First(b => b.Command is not null && b.Content?.ToString() == L.T("home.bake.retry_without"));
        Check(name + "：首张警告的重试按钮完整可见", BoundsIn(retry, warningScroller).Bottom <= warningScroller.Bounds.Height);
        var homeScroller = homePage.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(name + "：崩溃详情保留可滚动区域", homeScroller.Bounds.Height >= 60);
        homeScroller.Offset = new Vector(0, homeScroller.Extent.Height);
        await Task.Delay(100);
        var log = homePage.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == L.T("home.crash.view_log"));
        var logBounds = BoundsIn(log, homeScroller);
        Check(name + "：滚动后崩溃操作可达", logBounds.Top >= 0 && logBounds.Bottom <= homeScroller.Bounds.Height);
        if (multiple) Check(name + "：三条警告限高且可滚动", Host.Notices.Count == 3 && warningScroller.Bounds.Height <= 172 && warningScroller.Extent.Height > warningScroller.Viewport.Height);
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
                    Host.Clear();
                    Show(instance, () => calls.Add(request));
                    await CheckLayoutAsync($"{theme}-{size.Item1}x{size.Item2}");
                    Check("截图尺寸与文件名一致", window.Bounds.Width == size.Item1 && window.Bounds.Height == size.Item2);
                }
                Host.Clear();
                Show("Earlier-instance-A");
                Show("Earlier-instance-B");
                Show(instance, () => calls.Add(request));
                await CheckLayoutAsync($"{theme}-minimum-three-notices", multiple: true);
            }
            App.ApplyTheme("dark");
            var beforeNavigation = Host.Notices.ToArray();
            main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
            await Task.Delay(100);
            Check("切换页面仍保留警告及原实例回调", Host.Notices.SequenceEqual(beforeNavigation));
            main.CurrentPage = Home;
            foreach (var language in new[] { "zh-Hant", "en", "ja" })
            {
                L.SetLanguage(language);
                Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
                Host.Clear();
                Show(instance, () => calls.Add(request));
                await CheckLayoutAsync($"{language}-minimum");
            }
            var button = Host.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == L.T("home.bake.retry_without"));
            var pendingRetry = Host.Notices[0].RetryCommand;
            Check("重试按钮可通过自动化接口点击", button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IInvokeProvider>()!.Invoke();
            for (var i = 0; i < 40 && calls.Count == 0; i++) await Task.Delay(50);
            Check("实际按钮点击已触发一次回调并移除通知", calls.Count == 1 && Host.Notices.Count == 0);
            pendingRetry.Execute(null);
            Check("按钮只回调一次且保留实例/加入目标/SkipBake", calls.Count == 1 && calls[0] == request && request.VersionId == original.VersionId);
            Check("按钮移除通知，烘焙设置未改变", Host.Notices.Count == 0 && File.ReadAllText(AppSettings.FilePath) == settingsBefore);
            Show(instance, () => calls.Add(request));
            await Task.Delay(50);
            var clock = Stopwatch.StartNew();
            await Task.Delay(6500);
            Check("八秒前仍保留重试机会", Host.Notices.Count == 1);
            while (Host.Notices.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(11)) await Task.Delay(100);
            Check("八秒后自动消失且不调用重试", Host.Notices.Count == 0 && clock.Elapsed >= TimeSpan.FromSeconds(7.8) && calls.Count == 1);
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
            var closingNotice = Host.Show("window-close", () => closedCalls++);
            window.Closed += (_, _) =>
            {
                closingNotice.RetryCommand.Execute(null);
                var closed = Host.Notices.Count == 0 && closedCalls == 0;
                _report.Add($"{(closed ? "PASS" : "FAIL")} 关闭窗口会清空警告并使旧按钮失效");
                if (!closed) Environment.ExitCode = 1;
            };
            window.Close();
            if (!_report.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal))) _report.Add("DONE");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        }
    }
}
