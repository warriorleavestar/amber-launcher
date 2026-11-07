using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>真实本机直连控制通道配合假的 EasyTier：矩阵切换、取消、超时后不能留下网络任务或重启未改变的会话。</summary>
public sealed class DirectMatrixNetworkTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly DirectOptions Options = new([], false, [], false, false, false, true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>
        {
            new("amber-host", EasyTierArgs.HostIp, "1", 1, "udp"),
        });
        public void Dispose() => HasExited = true;
    }

    private static DirectSession NewSession(bool isHost, TimeSpan? stepTimeout = null, TimeSpan? recoveryDelay = null) =>
        new(null!, Path.Combine(Path.GetTempPath(), "amber-matrix-" + Guid.NewGuid().ToString("N")), Room,
            isHost, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            SpeedTestPortForTest = 0,
            MatrixStepTimeout = stepTimeout ?? TimeSpan.FromSeconds(120),
            MatrixRecoveryDelay = recoveryDelay ?? TimeSpan.FromSeconds(35),
        };

    private static Candidate Address(DirectBridge bridge) =>
        new(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, bridge.SocketEndPoint.Port));

    private static async Task<DirectPeer> ConnectHostAsync(DirectSession host, DirectBridge guest)
    {
        var invite = await host.PrepareAsync();
        var hostWait = host.Bridge!.WaitForPeerAsync(Deadline);
        var hostPeer = await guest.PunchAsync(invite.Candidates, invite.Nat, Deadline);
        Assert.NotNull(hostPeer);
        Assert.NotNull(await hostWait);
        await guest.SendControlAsync(hostPeer, DirectSession.Hello(new DirectCode(true, invite.Session, NatKind.Cone, [Address(guest)])));
        return hostPeer;
    }

    private static Channel<JsonObject> Controls(DirectBridge bridge)
    {
        var channel = Channel.CreateUnbounded<JsonObject>();
        bridge.ControlReceived += (_, message) => channel.Writer.TryWrite(message);
        return channel;
    }

    private static async Task<JsonObject> NextAsync(Channel<JsonObject> messages, string operation)
    {
        using var limit = new CancellationTokenSource(Deadline);
        while (true)
        {
            var message = await messages.Reader.ReadAsync(limit.Token);
            if (message["op"]?.GetValue<string>() == operation) return message;
        }
    }

    private static async Task AnswerAsync(DirectBridge guest, DirectPeer host, JsonObject request) =>
        await guest.SendControlAsync(host, new JsonObject
        {
            ["op"] = "test-answer", ["run"] = request["run"]!.GetValue<string>(), ["ok"] = true,
        });

    [Fact]
    public async Task TimedOutHostStepCancelsBackgroundWorkAndRestoresAllPaths()
    {
        await using var host = NewSession(true, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400));
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        var messages = Controls(guest);
        var peer = await ConnectHostAsync(host, guest);
        var chatStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = host.RunMatrixAsync(false, null, async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { chatStopped.TrySetResult(); }
            return true;
        }, default);
        await AnswerAsync(guest, peer, await NextAsync(messages, "test-request"));
        await NextAsync(messages, "step");

        var result = Assert.Single(await run.WaitAsync(Deadline));
        Assert.False(result.Passed);
        Assert.True(chatStopped.Task.IsCompleted);
        Assert.Equal(Enum.GetValues<PathKind>().Length, host.Bridge!.AllowedPaths.Count);
        Assert.Equal(256, host.Bridge.SprayPorts);
        Assert.Equal(1, host.EasyTierStarts);
        await NextAsync(messages, "test-end");

        // 若本步留下未取消的延迟恢复，它会在返回之后重新放开这些路径。
        host.Bridge.DropPeers();
        host.Bridge.AllowedPaths.Clear();
        host.Bridge.AllowedPaths.Add(PathKind.IPv6);
        await Task.Delay(600);
        Assert.Equal([PathKind.IPv6], host.Bridge.AllowedPaths);
    }

    [Fact]
    public async Task CancelledHostStepWaitsForProbeCleanupBeforeReturning()
    {
        await using var host = NewSession(true);
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        var messages = Controls(guest);
        var peer = await ConnectHostAsync(host, guest);
        using var stop = new CancellationTokenSource();
        var chatStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = host.RunMatrixAsync(false, null, async (_, _, ct) =>
        {
            chatStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { chatStopped.TrySetResult(); }
            return true;
        }, stop.Token);
        await AnswerAsync(guest, peer, await NextAsync(messages, "test-request"));
        await chatStarted.Task.WaitAsync(Deadline);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Deadline));
        Assert.True(chatStopped.Task.IsCompleted);
        Assert.Equal(Enum.GetValues<PathKind>().Length, host.Bridge!.AllowedPaths.Count);
        Assert.Equal(256, host.Bridge.SprayPorts);
        await NextAsync(messages, "test-end");
    }

    [Fact]
    public async Task GuestAcceptsNextStepAfterResultAndKeepsUnchangedEasyTier()
    {
        await using var guest = NewSession(false);
        await guest.PrepareAsync();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        var messages = Controls(host);
        var waiting = host.WaitForPeerAsync(Deadline);
        Assert.True(await guest.JoinAsync(new DirectCode(false, "0123456789abcdef", NatKind.Cone, [Address(host)], DirectTestKeys.Invite), DirectTestKeys.Knock(Room)));
        Assert.NotNull(await waiting);
        await using var speed = new SpeedTestServer(guest.LocalSpeedPort);
        var original = guest.EasyTier;
        const string run = "matrix-two-steps";
        Assert.True(guest.TestConsent.Request(new TestRequest(run, false)));
        Assert.True(await guest.AnswerTestRequestAsync(true, false, false));
        guest.GuestChatProbe = (_, _) => Task.FromResult<bool?>(true);

        for (var index = 1; index <= 2; index++)
        {
            await host.SendControlAsync(Assert.Single(host.Peers), new JsonObject
            {
                ["op"] = "step", ["run"] = run, ["i"] = index,
                ["route"] = nameof(RouteKind.Direct), ["path"] = nameof(PathKind.Lan),
            });
            var result = await NextAsync(messages, "result");
            Assert.Equal(index, result["i"]!.GetValue<int>());
            Assert.True(result["passed"]!.GetValue<bool>());
            Assert.True(result["chat"]!.GetValue<bool>());
            Assert.True(result["mbps"]!.GetValue<double>() > 0);
        }

        await guest.EndGuestTestAsync(run);
        Assert.Null(guest.TestConsent.ActiveRun);
        Assert.Null(guest.TestConsent.RestoringRun);
        Assert.Same(original, guest.EasyTier);
        Assert.False(original!.HasExited);
        Assert.Equal(1, guest.EasyTierStarts);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GuestCannotPassWhenThroughputOrChatFails(bool throughputAvailable, bool chatWorks)
    {
        await using var guest = NewSession(false);
        await guest.PrepareAsync();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        var messages = Controls(host);
        var waiting = host.WaitForPeerAsync(Deadline);
        Assert.True(await guest.JoinAsync(new DirectCode(false, "0123456789abcdef", NatKind.Cone, [Address(host)], DirectTestKeys.Invite), DirectTestKeys.Knock(Room)));
        Assert.NotNull(await waiting);
        await using var speed = throughputAvailable ? new SpeedTestServer(guest.LocalSpeedPort) : null;
        const string run = "matrix-data-failure";
        Assert.True(guest.TestConsent.Request(new TestRequest(run, false)));
        Assert.True(await guest.AnswerTestRequestAsync(true, false, false));
        guest.GuestChatProbe = (_, _) => Task.FromResult<bool?>(chatWorks);

        await host.SendControlAsync(Assert.Single(host.Peers), new JsonObject
        {
            ["op"] = "step", ["run"] = run, ["i"] = 1,
            ["route"] = nameof(RouteKind.Direct), ["path"] = nameof(PathKind.Lan),
        });
        var result = await NextAsync(messages, "result");

        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(result["reason"]?.GetValue<string>()));
        if (throughputAvailable)
        {
            Assert.True(result["mbps"]!.GetValue<double>() > 0);
            Assert.False(result["chat"]!.GetValue<bool>());
        }
        else Assert.Null(result["mbps"]);
        await guest.EndGuestTestAsync(run);
    }

    [Fact]
    public async Task GuestStepReleasedAfterTestEndedCannotDropRestoredPeer()
    {
        await using var guest = NewSession(false);
        await guest.PrepareAsync();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        var waiting = host.WaitForPeerAsync(Deadline);
        Assert.True(await guest.JoinAsync(new DirectCode(false, "0123456789abcdef", NatKind.Cone, [Address(host)], DirectTestKeys.Invite), DirectTestKeys.Knock(Room)));
        Assert.NotNull(await waiting);
        var originalPeer = Assert.Single(guest.Bridge!.Peers);
        const string run = "matrix-delayed-step";
        Assert.True(guest.TestConsent.Request(new TestRequest(run, false)));
        Assert.True(await guest.AnswerTestRequestAsync(true, false, false));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        guest.GuestStepStarted += (_, _) =>
        {
            started.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("等待测试释放步骤超时");
        };
        var step = Task.Run(() => guest.GuestStepAsync(originalPeer, new JsonObject
        {
            ["op"] = "step", ["run"] = run, ["i"] = 1,
            ["route"] = nameof(RouteKind.Direct), ["path"] = nameof(PathKind.Lan),
        }));
        try
        {
            await started.Task.WaitAsync(Deadline);
            await guest.EndGuestTestAsync(run).WaitAsync(Deadline);
        }
        finally { release.Set(); }
        await step.WaitAsync(Deadline);

        Assert.Same(originalPeer, Assert.Single(guest.Bridge.Peers));
        Assert.Equal(Enum.GetValues<PathKind>().Length, guest.Bridge.AllowedPaths.Count);
        Assert.Equal(256, guest.Bridge.SprayPorts);
        Assert.Equal(1, guest.EasyTierStarts);
        Assert.Null(guest.TestConsent.ActiveRun);
    }

    [Fact]
    public async Task EndRestoresAfterUncooperativeOldStartCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var guest = new DirectSession(null!, "unused", Room, false, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            StartForTest = async (o, _) =>
            {
                if (o.Tun)
                {
                    started.TrySetResult();
                    await release.Task;
                }
                return new FakeEasyTier(o);
            },
        };
        var original = new EasyTierOptions(Room, false, [], 1, 2);
        await guest.StartEasyTierAsync(original, default);
        const string run = "matrix-late-start";
        Assert.True(guest.TestConsent.Request(new TestRequest(run, true)));
        Assert.NotNull(guest.TestConsent.Answer(true, true, false, original));
        guest.BeginRun(run);
        var switching = guest.StartForRunAsync(run, original with { Tun = true });
        await started.Task.WaitAsync(Deadline);
        var ending = guest.EndGuestTestAsync(run);
        Assert.False(ending.IsCompleted);
        release.TrySetResult();
        await switching.WaitAsync(Deadline);
        await ending.WaitAsync(Deadline);

        Assert.False(guest.EasyTier!.Options.Tun);
        Assert.Null(guest.TestConsent.ActiveRun);
        Assert.Null(guest.TestConsent.RestoringRun);
        Assert.Equal(3, guest.EasyTierStarts);
    }
}
