using System.Net;
using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>出口仅在确认后生效；失败、停止和直连所有权都不需要实际 UAC 或修改系统路由。</summary>
public sealed class ExitLifecycleRegressionTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly DirectOptions Direct = new([], false, [], true, false, false, true);
    private static readonly EasyTierOptions Original = new(Room, false, ["udp://127.0.0.1:1"], 1, Tun: true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private sealed class Owner : IDisposable
    {
        public void Dispose() { }
    }

    private static DirectSession NewSession(Func<EasyTierOptions, CancellationToken, Task<IEasyTierSession>> start) =>
        new(null!, Path.Combine(Path.GetTempPath(), "amber-exit-lifecycle-" + Guid.NewGuid().ToString("N")), Room, false, Direct, new DiagnosticLog())
        { StartForTest = start };

    [Fact]
    public async Task ExitIsPublishedOnlyAfterReadinessAcknowledgement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = NewSession(async (o, ct) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        var switching = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        Assert.False(switching.IsCompleted);
        Assert.Same(original, session.EasyTier);
        Assert.Null(session.EasyTier!.Options.ExitNode);
        Assert.False(original.HasExited);
        Assert.Equal(1, session.EasyTierStarts);

        ready.SetResult();
        await switching.WaitAsync(Deadline);
        Assert.True(original.HasExited);
        Assert.Equal(EasyTierArgs.HostIp, session.EasyTier!.Options.ExitNode);
        Assert.Equal(2, session.EasyTierStarts);
    }

    [Fact]
    public async Task FailedReadinessCannotLeaveRequestedExitPublished()
    {
        await using var session = NewSession((o, _) => o.ExitNode is null
            ? Task.FromResult<IEasyTierSession>(new FakeEasyTier(o))
            : Task.FromException<IEasyTierSession>(new IOException("route acknowledgement failed")));
        await session.StartEasyTierAsync(Original, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<IOException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Null(session.EasyTier);
        Assert.True(original.HasExited);
        Assert.Equal(1, session.EasyTierStarts);
    }

    [Fact]
    public async Task RejectedMixedUnderlayPlanPreservesWorkingSession()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original with { Peers = ["udp://127.0.0.1:1", "udp://192.0.2.20:11010"] }, default);
        var original = session.EasyTier!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetExitNodeAsync(EasyTierArgs.HostIp));
        Assert.Same(original, session.EasyTier);
        Assert.False(original.HasExited);
        Assert.Null(original.Options.ExitNode);
        Assert.Equal(1, session.EasyTierStarts);
    }

    [Fact]
    public async Task StopQueuedBehindEnableUsesTheAcknowledgedConfiguration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = NewSession(async (o, ct) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var enable = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        var stop = session.SetExitNodeAsync(null);
        Assert.False(stop.IsCompleted);
        ready.SetResult();
        await Task.WhenAll(enable, stop).WaitAsync(Deadline);
        Assert.Null(session.EasyTier!.Options.ExitNode);
        Assert.False(session.EasyTier.HasExited);
        Assert.Equal(3, session.EasyTierStarts);
    }

    [Fact]
    public async Task DisposedDirectOwnerStopsALateExitAndDoesNotReportSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEasyTier? late = null;
        await using var session = NewSession(async (o, _) =>
        {
            if (o.ExitNode is not null)
            {
                entered.TrySetResult();
                await ready.Task; // 模拟底层无法及时响应取消。
                return late = new FakeEasyTier(o);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        var enable = session.SetExitNodeAsync(EasyTierArgs.HostIp);
        await entered.Task.WaitAsync(Deadline);
        await session.DisposeAsync();
        ready.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => enable.WaitAsync(Deadline));
        Assert.True(late!.HasExited);
    }

    [Fact]
    public void UnderlayIncludesChangedPeerAddressesAndNormalizesMappedIpv4()
    {
        var result = DirectSession.CollectDirectUnderlayAddresses(
            [IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::1")],
            [IPAddress.Parse("::ffff:198.51.100.20"), IPAddress.Parse("203.0.113.30")]);
        Assert.Equal(["198.51.100.20", "203.0.113.30"], result);
    }

    [Fact]
    public void ViewModelKeepsDirectOwnerAndSynchronizesExitOptions() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        using var lifetime = new CancellationTokenSource();
        var owner = new Owner();
        try
        {
            session.StartEasyTierAsync(Original, default).GetAwaiter().GetResult();
            var vm = new MultiplayerViewModel(service) { SelectedExitPeer = new PeerItem(new PeerInfo("amber-host", EasyTierArgs.HostIp, "p2p", 1, "udp")) };
            SetField(vm, "_direct", session);
            SetField(vm, "_session", session.EasyTier);
            SetField(vm, "_sessionCts", lifetime);
            service.ActiveMultiplayer = owner;

            vm.UseExitNodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Same(owner, service.ActiveMultiplayer);
            Assert.Same(session.EasyTier, GetField(vm, "_session"));
            Assert.Same(session.EasyTier!.Options, GetField(vm, "_currentOptions"));
            Assert.Equal(EasyTierArgs.HostIp, vm.ExitNodeIp);

            vm.StopExitNodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Same(owner, service.ActiveMultiplayer);
            Assert.Same(session.EasyTier, GetField(vm, "_session"));
            Assert.Null(session.EasyTier!.Options.ExitNode);
            Assert.Equal("", vm.ExitNodeIp);
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
}
