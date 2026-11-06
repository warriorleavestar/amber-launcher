using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;
using System.Diagnostics;

namespace Amber.Core.Tests;

/// <summary>用假会话覆盖热点共享的确认、失败与断开竞态，不创建启动器服务或改动网络。</summary>
public sealed class HotspotSharingViewModelTests
{
    [Theory]
    [InlineData(false, true, "10.144.144.1")]
    [InlineData(true, false, "10.144.144.1")]
    [InlineData(true, true, null)]
    public void MissingPrerequisiteNeverInvokesTheSession(bool supported, bool fullLan, string? exitIp) => UiTestThread.Run(() =>
    {
        var session = new FakeSession();
        var vm = Create(supported);
        vm.UpdateSession(session, fullLan, exitIp);
        Assert.False(vm.CanStart);
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.Equal(0, session.Calls);
        Assert.False(vm.IsSharing);
    });

    [Fact]
    public void StartAndStopFollowConfirmedSessionState() => UiTestThread.Run(() =>
    {
        var session = new FakeSession();
        var vm = Ready(session);
        Assert.True(vm.CanStart);

        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(vm.IsSharing);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanStart);
        Assert.False(vm.HasError);

        vm.StopCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.False(vm.IsSharing);
        Assert.True(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.Equal(new[] { true, false }, session.Requests);
    });

    [Fact]
    public void FailureDoesNotClaimSuccessAndCanBeRetried() => UiTestThread.Run(() =>
    {
        var session = new FakeSession { Change = (_, _) => throw new IOException("hotspot unavailable") };
        var vm = Ready(session);

        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.False(vm.IsSharing);
        Assert.True(vm.HasError);
        Assert.True(vm.CanStart);

        session.Change = null;
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(vm.IsSharing);
        Assert.False(vm.HasError);
    });

    [Fact]
    public void SuccessfulTaskWithoutStateConfirmationIsAnError() => UiTestThread.Run(() =>
    {
        var session = new FakeSession { Change = (_, _) => Task.CompletedTask };
        var vm = Ready(session);
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.False(vm.IsSharing);
        Assert.True(vm.HasError);
    });

    [Fact]
    public void FailedStopKeepsTheConfirmedActiveStateAndAllowsRetry() => UiTestThread.Run(() =>
    {
        var session = new FakeSession { HotspotSharingActive = true, Change = (_, _) => throw new IOException("restore failed") };
        var vm = Ready(session);
        vm.StopCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(vm.IsSharing);
        Assert.True(vm.HasError);
        Assert.True(vm.CanStop);
    });

    [Fact]
    public void GuardStoppingSharingUpdatesTheUiWithoutAnotherUserOperation() => UiTestThread.Run(() =>
    {
        var session = new FakeSession { HotspotSharingActive = true };
        var vm = Ready(session);
        Assert.True(vm.IsSharing);

        session.HotspotSharingActive = false;
        vm.UpdateSession(session, true, "10.144.144.1");
        Assert.False(vm.IsSharing);
        Assert.Equal(L.T("multiplayer.hotspot.stopped_automatically"), vm.StatusText);
        Assert.Equal(0, session.Calls);
    });

    [Fact]
    public async Task WaitingForAcknowledgementDoesNotOptimisticallyEnableSharing()
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { Change = (_, _) => reply.Task };
        HotspotSharingViewModel vm = null!;
        Task start = null!;
        UiTestThread.Run(() =>
        {
            vm = Ready(session);
            start = vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            Assert.False(vm.IsSharing);
            Assert.False(vm.CanStart);
            Assert.False(vm.CanStop);
        });
        session.HotspotSharingActive = true;
        reply.SetResult();
        await PumpUiUntilCompleteAsync(start);
        UiTestThread.Run(() =>
        {
            Assert.True(vm.IsSharing);
            Assert.False(vm.IsBusy);
            Assert.Equal(1, session.Calls);
        });
    }

    [Fact]
    public async Task LateSuccessFromDisconnectedSessionCannotEnableTheNewSession()
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSession = new FakeSession { Change = (_, _) => reply.Task };
        var nextSession = new FakeSession();
        HotspotSharingViewModel vm = null!;
        Task start = null!;
        UiTestThread.Run(() =>
        {
            vm = Ready(oldSession);
            start = vm.StartCommand.ExecuteAsync(null);
            vm.UpdateSession(nextSession, true, "10.144.144.2");
            Assert.True(oldSession.LastToken.IsCancellationRequested);
            Assert.False(vm.IsSharing);
        });
        oldSession.HotspotSharingActive = true;
        reply.SetResult();
        await PumpUiUntilCompleteAsync(start);
        UiTestThread.Run(() =>
        {
            Assert.False(vm.IsSharing);
            Assert.True(vm.CanStart);
            Assert.Equal(0, nextSession.Calls);
        });
    }

    [Fact]
    public async Task ExitSwitchLockPreventsAnOverlappingHotspotChange()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var session = new FakeSession();
        HotspotSharingViewModel vm = null!;
        Task start = null!;
        UiTestThread.Run(() =>
        {
            vm = new HotspotSharingViewModel(gate, true, () => { });
            vm.UpdateSession(session, true, "10.144.144.1");
            start = vm.StartCommand.ExecuteAsync(null);
            Assert.Equal(0, session.Calls);
            vm.UpdateSession(null, false, null);
        });
        await PumpUiUntilCompleteAsync(start);
        Assert.Equal(0, session.Calls);
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void SettingsEntryUsesOnlyTheInjectedAction() => UiTestThread.Run(() =>
    {
        var opened = 0;
        var vm = new HotspotSharingViewModel(new SemaphoreSlim(1, 1), true, () => opened++);
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, opened);
        Assert.False(vm.IsSharing);
    });

    private static async Task PumpUiUntilCompleteAsync(Task operation)
    {
        // UiTestThread 只执行测试委托，不运行 Avalonia 消息循环；异步命令的续体需要显式泵回原 UI 线程。
        var deadline = Stopwatch.StartNew();
        while (!operation.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            UiTestThread.Run(() => Dispatcher.UIThread.RunJobs());
            if (!operation.IsCompleted) await Task.Delay(10);
        }
        Assert.True(operation.IsCompleted, "热点界面命令在消息循环运行时仍未完成");
        await operation;
        UiTestThread.Run(() => Dispatcher.UIThread.RunJobs());
    }

    private static HotspotSharingViewModel Create(bool supported = true) => new(new SemaphoreSlim(1, 1), supported, () => { });

    private static HotspotSharingViewModel Ready(FakeSession session)
    {
        var vm = Create();
        vm.UpdateSession(session, true, "10.144.144.1");
        return vm;
    }

    private sealed class FakeSession : IHotspotSharingSession
    {
        public bool HotspotSharingActive { get; set; }
        public int Calls { get; private set; }
        public List<bool> Requests { get; } = [];
        public CancellationToken LastToken { get; private set; }
        public Func<bool, CancellationToken, Task>? Change { get; set; }

        public Task SetHotspotSharingAsync(bool enabled, CancellationToken ct = default)
        {
            Calls++;
            Requests.Add(enabled);
            LastToken = ct;
            if (Change is not null) return Change(enabled, ct);
            HotspotSharingActive = enabled;
            return Task.CompletedTask;
        }
    }
}
