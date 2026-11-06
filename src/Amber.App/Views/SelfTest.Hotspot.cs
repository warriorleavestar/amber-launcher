using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Controls;
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
        await LayoutHereAsync("热点共享");
        window.Width = 760;
        window.Height = 440;
        await Task.Delay(250);
        panel.BringIntoView();
        await ShotAsync("热点共享-最小窗口");
        vm.UpdateSession(null, false, null);
        Log("热点自检只使用假会话，未验证真实手机、ICS 或远端互联网连通性。");
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
