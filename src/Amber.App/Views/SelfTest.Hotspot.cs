using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>真实控件配假会话；只验证界面和状态绑定，不启动网络组件或改系统共享。</summary>
    private async Task HotspotAsync()
    {
        var page = Page<MultiplayerViewModel>();
        main.CurrentPage = page;
        window.Width = 1080;
        window.Height = 700;
        await Task.Delay(300);
        var panel = window.GetVisualDescendants().OfType<HotspotSharingPanel>().Single();
        var vm = page.Hotspot;
        var session = new HotspotSelfTestSession();

        Check("热点：真实页面绑定独立热点控件", ReferenceEquals(panel.DataContext, vm));
        Check("热点：未连接时禁止开始", !vm.CanStart && !vm.IsSharing);
        if (!vm.IsSupported)
        {
            Check("热点：非 Windows 平台显示说明", vm.PrerequisiteText == L.T("multiplayer.hotspot.windows_only"));
            Log("SKIP 热点配置状态：此平台不是 Windows；未调用任何网络配置");
            return;
        }

        vm.UpdateSession(session, false, "10.144.144.1");
        Check("热点：普通联机模式禁止开始", !vm.CanStart);
        vm.UpdateSession(session, true, null);
        Check("热点：未选择远端出口时禁止开始", !vm.CanStart);
        vm.UpdateSession(session, true, "10.144.144.1");
        Check("热点：完整局域网及远端出口就绪后可以开始", vm.CanStart);
        panel.BringIntoView();
        await ShotAsync("热点共享-准备");

        session.Reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = vm.StartCommand.ExecuteAsync(null);
        await Task.Delay(100);
        Check("热点：收到守护确认前不显示共享成功", vm.IsBusy && !vm.IsSharing && !vm.CanStart && session.Calls == 1);
        session.HotspotSharingActive = true;
        session.Reply.SetResult();
        await starting;
        session.Reply = null;
        Check("热点：确认后显示已配置及停止按钮", vm.IsSharing && vm.CanStop && !vm.IsBusy);
        Check("热点：说明要求手机核对实际出口", vm.StatusText == L.T("multiplayer.hotspot.configured"));
        panel.BringIntoView();
        await ShotAsync("热点共享-已配置");

        await vm.StopCommand.ExecuteAsync(null);
        Check("热点：停止后恢复可再次开始状态", !vm.IsSharing && vm.CanStart && !vm.CanStop);
        session.Fail = true;
        await vm.StartCommand.ExecuteAsync(null);
        Check("热点：失败显示原因且允许重试", !vm.IsSharing && vm.HasError && vm.CanStart);
        panel.BringIntoView();
        await ShotAsync("热点共享-失败");
        session.Fail = false;

        await vm.StartCommand.ExecuteAsync(null);
        session.HotspotSharingActive = false;
        vm.UpdateSession(session, true, "10.144.144.1");
        Check("热点：守护停止后不保留成功状态", !vm.IsSharing && vm.StatusText == L.T("multiplayer.hotspot.stopped_automatically"));

        session.Reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        starting = vm.StartCommand.ExecuteAsync(null);
        vm.UpdateSession(null, false, null);
        session.HotspotSharingActive = true;
        session.Reply.SetResult();
        await starting;
        session.Reply = null;
        Check("热点：断开后的迟到确认不重新启用共享", !vm.IsSharing && !vm.CanStart && !vm.IsBusy);

        session.HotspotSharingActive = false;
        vm.UpdateSession(session, true, "10.144.144.1");
        await vm.StartCommand.ExecuteAsync(null);
        Log("结构检查：以下为联机整页的四语言 × 四尺寸共 16 组布局检查；截图可能未滚动到热点卡，不作为热点按钮操作或热点卡视觉验收。");
        await LayoutHereAsync("热点共享-全页结构");
        await HotspotButtonsAsync(panel, vm, session);
        vm.UpdateSession(null, false, null);
        Log("热点自检只使用假会话，未验证真实手机、ICS 或远端互联网连通性。");
    }

    /// <summary>独立于全页结构矩阵，逐个窗口取景并通过真实按钮的自动化接口操作假会话。</summary>
    private async Task HotspotButtonsAsync(HotspotSharingPanel panel, HotspotSharingViewModel vm, HotspotSelfTestSession session)
    {
        var originalLanguage = L.Language;
        var originalPseudo = L.Pseudo;
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var originalWindowState = window.WindowState;
        Log("按钮操作检查：四语言 × 760×440 / 1080×700 共 8 组真实窗口；先核对按钮完整处于滚动视口且中心命中，再通过 IInvokeProvider 开始和停止。截图仍须人工查看。");
        try
        {
            L.Pseudo = false;
            window.WindowState = WindowState.Normal;
            foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
            {
                L.SetLanguage(language);
                foreach (var (width, height) in new[] { (760, 440), (1080, 700) })
                {
                    var tag = $"{language}-{width}x{height}";
                    session.Reply = null;
                    session.Fail = false;
                    session.HotspotSharingActive = false;
                    vm.UpdateSession(session, true, "10.144.144.1");
                    window.Width = width;
                    window.Height = height;
                    await Task.Delay(350);
                    window.UpdateLayout();
                    Check($"热点操作 {tag}：真实窗口客户区尺寸", Math.Abs(window.ClientSize.Width - width) <= 1
                        && Math.Abs(window.ClientSize.Height - height) <= 1,
                        $"实际 {window.ClientSize.Width:F0}×{window.ClientSize.Height:F0}");
                    panel.BringIntoView();
                    await Task.Delay(200);

                    var start = panel.GetVisualDescendants().OfType<Button>()
                        .SingleOrDefault(button => ReferenceEquals(button.Command, vm.StartCommand));
                    var stop = panel.GetVisualDescendants().OfType<Button>()
                        .SingleOrDefault(button => ReferenceEquals(button.Command, vm.StopCommand));
                    Check($"热点操作 {tag}：真实按钮使用当前语言文字", L.Language == language
                        && string.Equals(start?.Content?.ToString(), L.T("multiplayer.hotspot.start"), StringComparison.Ordinal)
                        && string.Equals(stop?.Content?.ToString(), L.T("multiplayer.hotspot.stop"), StringComparison.Ordinal));
                    var startReady = await HotspotButtonInViewAsync(panel, start, $"{tag}-开始前");
                    var calls = session.Calls;
                    var started = startReady && Press(start);
                    Check($"热点操作 {tag}：真实开始按钮调用自动化接口", started);
                    Check($"热点操作 {tag}：点击开始只调用假会话一次并显示已配置", started
                        && await WaitAsync(() => session.Calls == calls + 1 && session.HotspotSharingActive
                            && vm.IsSharing && !vm.IsBusy && vm.CanStop, 5));

                    var stopReady = await HotspotButtonInViewAsync(panel, stop, $"{tag}-已配置");
                    calls = session.Calls;
                    var stopped = stopReady && Press(stop);
                    Check($"热点操作 {tag}：真实停止按钮调用自动化接口", stopped);
                    Check($"热点操作 {tag}：点击停止只调用假会话一次并恢复开始", stopped
                        && await WaitAsync(() => session.Calls == calls + 1 && !session.HotspotSharingActive
                            && !vm.IsSharing && !vm.IsBusy && vm.CanStart, 5));
                    await HotspotButtonInViewAsync(panel, start, $"{tag}-已停止");
                }
            }
        }
        finally
        {
            L.Pseudo = originalPseudo;
            L.SetLanguage(originalLanguage);
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.WindowState = originalWindowState;
        }
    }

    private async Task<bool> HotspotButtonInViewAsync(HotspotSharingPanel panel, Button? button, string tag)
    {
        panel.BringIntoView();
        await Task.Delay(150);
        button?.BringIntoView();
        await Task.Delay(200);
        window.UpdateLayout();
        var inView = button is not null && HotspotControlInViewport(button, out _, out _);
        var bounds = default(Rect);
        var viewport = default(Rect);
        if (button is not null) HotspotControlInViewport(button, out bounds, out viewport);
        var hit = inView ? window.InputHitTest(bounds.Center) : null;
        var hitButton = hit is Visual visual && (ReferenceEquals(visual, button)
            || visual.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, button)));
        var ready = button is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true, IsHitTestVisible: true }
            && inView && hitButton;
        Check($"热点取景 {tag}：操作按钮可见、启用、完整位于视口且中心命中", ready,
            $"按钮={bounds}；视口={viewport}；命中={hit?.GetType().Name ?? "无"}");
        var entireCard = HotspotControlInViewport(panel, out var cardBounds, out var cardViewport);
        Log($"截图取景 {tag}：{(entireCard ? "热点卡边界完整位于当前视口" : "热点卡不能同时完整显示，需要滚动；截图优先保留操作按钮")}" +
            $"；卡片={cardBounds}；视口={cardViewport}。此处是几何检查，不代替人工看图。");
        await ShotAsync($"热点按钮-{tag}");
        return ready;
    }

    private bool HotspotControlInViewport(Control control, out Rect bounds, out Rect viewport)
    {
        bounds = default;
        viewport = new Rect(window.Bounds.Size);
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0
            || control.TranslatePoint(default, window) is not { } origin) return false;
        bounds = new Rect(origin, control.Bounds.Size);
        foreach (var ancestor in control.GetVisualAncestors())
        {
            // ScrollContentPresenter represents the actual viewport, excluding headers and scrollbars.
            if (!ancestor.ClipToBounds && ancestor is not ScrollContentPresenter) continue;
            if (ancestor.TranslatePoint(default, window) is not { } point) return false;
            viewport = viewport.Intersect(new Rect(point, ancestor.Bounds.Size));
        }
        return viewport.Width > 0 && viewport.Height > 0
            && bounds.Left >= viewport.Left - 0.5 && bounds.Top >= viewport.Top - 0.5
            && bounds.Right <= viewport.Right + 0.5 && bounds.Bottom <= viewport.Bottom + 0.5;
    }

    private sealed class HotspotSelfTestSession : IHotspotSharingSession
    {
        public bool HotspotSharingActive { get; set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource? Reply { get; set; }

        public Task SetHotspotSharingAsync(bool enabled, CancellationToken ct = default)
        {
            Calls++;
            if (Fail) throw new IOException("Simulated hotspot configuration failure");
            if (Reply is not null) return Reply.Task;
            HotspotSharingActive = enabled;
            return Task.CompletedTask;
        }
    }
}
