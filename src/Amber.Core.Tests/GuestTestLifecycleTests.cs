using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// R2：加入方的全条件测试。EasyTier 的启动一次只做一个；测试结束时取消还在路上的步骤，并在启动锁里无条件恢复原来的设置；
/// 用户可以停止测试；测试有最长时间和最多步数；和房主断开时的回答算拒绝；认得出旧版本的加入方；心跳停了会补发探测。
/// </summary>
public sealed class GuestTestLifecycleTests : IDisposable
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly DirectOptions Options = new([], false, [], false, false, false, true);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-guest-test-" + Guid.NewGuid().ToString("N"));

    private static readonly EasyTierOptions Original = new(Room, false, ["udp://127.0.0.1:1"], 1, 2);
    private static readonly EasyTierOptions Step = Original with { Peers = ["udp://127.0.0.1:9"] };

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private DirectSession NewSession(Func<EasyTierOptions, CancellationToken, Task<IEasyTierSession>> start) =>
        new(null!, _dir, Room, false, Options, new DiagnosticLog()) { StartForTest = start };

    private static void Accept(DirectSession session, string run)
    {
        Assert.True(session.TestConsent.Request(new TestRequest(run, false)));
        Assert.NotNull(session.TestConsent.Answer(true, false, false, session.EasyTier!.Options));
        session.BeginRun(run);
    }

    private static bool IsStep(EasyTierOptions o) => o.Peers.SequenceEqual(Step.Peers);

    /// <summary>步骤正在切换 EasyTier 时测试结束：这一步被取消，恢复的设置留下来；之后迟到的同一次测试的步骤不再执行。</summary>
    [Fact]
    public async Task EndCancelsInFlightStepAndRestoreWins()
    {
        var gate = new TaskCompletionSource();
        var stepStarted = new TaskCompletionSource();
        await using var session = NewSession(async (o, ct) =>
        {
            if (IsStep(o))
            {
                stepStarted.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0001");

        var step = session.StartForRunAsync("run-0001", Step);
        await stepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ended = 0;
        session.TestEnded += _ => ended++;
        await session.EndGuestTestAsync("run-0001");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step);
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        Assert.Equal(1, ended);
        Assert.Null(session.TestConsent.ActiveRun);

        // 迟到的步骤（比如房主那边已经发出的下一步）：不执行。
        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartForRunAsync("run-0001", Step));
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
    }

    /// <summary>步骤已经切过去了再结束：恢复照样执行（不因为“上次要求的参数”相同就跳过）。</summary>
    [Fact]
    public async Task EndRestoresAfterCompletedStep()
    {
        var starts = new List<EasyTierOptions>();
        await using var session = NewSession((o, _) =>
        {
            lock (starts) starts.Add(o);
            return Task.FromResult<IEasyTierSession>(new FakeEasyTier(o));
        });
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0002");
        await session.StartForRunAsync("run-0002", Step);
        Assert.True(IsStep(session.EasyTier!.Options));
        await session.EndGuestTestAsync("run-0002");
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        Assert.Equal(3, starts.Count);
    }

    /// <summary>恢复前、恢复途中、结束通知期间都不能接受新测试；重复结束不会提前放开，也不会重复通知。</summary>
    [Fact]
    public async Task NewRunWaitsUntilRestoreAndNotificationComplete()
    {
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = NewSession(async (o, ct) =>
        {
            if (o.Peers.SequenceEqual(Original.Peers) && o.RpcPort != Original.RpcPort)
            {
                restoring.TrySetResult();
                await allowRestore.Task.WaitAsync(ct);
            }
            return new FakeEasyTier(o);
        });
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "old-0001");
        await session.StartForRunAsync("old-0001", Step with { Tun = true, ExitNode = EasyTierArgs.HostIp });
        var endedRuns = new List<string?>();
        session.TestEnded += run =>
        {
            endedRuns.Add(run);
            Assert.False(session.TestConsent.Request(new TestRequest("new-0002", false)));
        };
        var blockedBeforeRestore = false;
        var end = session.EndGuestTestAsync("old-0001", beforeRestore: () =>
        {
            blockedBeforeRestore = !session.TestConsent.Request(new TestRequest("new-0002", false));
            return Task.CompletedTask;
        });
        try
        {
            await restoring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(blockedBeforeRestore);
            Assert.Null(session.TestConsent.ActiveRun);
            Assert.Equal("old-0001", session.TestConsent.RestoringRun);
            Assert.False(session.TestConsent.Request(new TestRequest("new-0002", false)));
            await session.EndGuestTestAsync("old-0001");
            await session.EndGuestTestAsync("another-run");
            Assert.Empty(endedRuns);
            Assert.Equal("old-0001", session.TestConsent.RestoringRun);
        }
        finally
        {
            allowRestore.TrySetResult();
            await end;
        }
        Assert.Equal(new[] { "old-0001" }, endedRuns);
        Assert.Null(session.TestConsent.RestoringRun);
        Assert.False(session.EasyTier!.Options.Tun);
        Assert.Null(session.EasyTier.Options.ExitNode);

        // 之后的测试保存的是已恢复的设置，旧结束通知带着自己的编号，不会被误认成这一次。
        Accept(session, "new-0002");
        await session.StartForRunAsync("new-0002", Step);
        await session.EndGuestTestAsync("new-0002");
        Assert.Equal(new[] { "old-0001", "new-0002" }, endedRuns);
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        Assert.False(session.EasyTier.Options.Tun);
        Assert.Null(session.EasyTier.Options.ExitNode);
    }

    /// <summary>通知处理器出错也不留下永远恢复中的状态，恢复完成的配置仍然可用于下一次测试。</summary>
    [Fact]
    public async Task NotificationFailureStillCompletesRestore()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-notify");
        await session.StartForRunAsync("run-notify", Step);
        session.TestEnded += _ => throw new InvalidOperationException("notification failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EndGuestTestAsync("run-notify"));
        Assert.Null(session.TestConsent.RestoringRun);
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        Assert.True(session.TestConsent.Request(new TestRequest("run-next", false)));
    }

    /// <summary>恢复连接或通知房主失败，仍然恢复本机设置并解除恢复状态。</summary>
    [Fact]
    public async Task BeforeRestoreFailureStillRestoresSettings()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-restore");
        await session.StartForRunAsync("run-restore", Step);
        await session.EndGuestTestAsync("run-restore", beforeRestore: () => throw new IOException("connection failed"));
        Assert.Null(session.TestConsent.RestoringRun);
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        Assert.True(session.TestConsent.Request(new TestRequest("run-next", false)));
    }

    /// <summary>同时来的几个切换一次只做一个。</summary>
    [Fact]
    public async Task StartsAreSerialized()
    {
        var running = 0;
        var maxRunning = 0;
        await using var session = NewSession(async (o, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref running);
            return new FakeEasyTier(o);
        });
        var tasks = Enumerable.Range(0, 6).Select(i => session.StartEasyTierAsync(Original with { Peers = [$"udp://127.0.0.1:{100 + i}"] }, default)).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(1, maxRunning);
    }

    /// <summary>用户点“停止测试”：同意清掉、恢复原来的设置。</summary>
    [Fact]
    public async Task GuestCanStopTest()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0003");
        await session.StartForRunAsync("run-0003", Step);
        await session.StopGuestTestAsync();
        Assert.Null(session.TestConsent.ActiveRun);
        Assert.Equal(Original.Peers, session.EasyTier!.Options.Peers);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartForRunAsync("run-0003", Step));
    }

    /// <summary>回答时已经和房主断开：算拒绝，不留下“已同意”。</summary>
    [Fact]
    public async Task AnswerWithoutPeerIsDecline()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Assert.True(session.TestConsent.Request(new TestRequest("run-0004", true)));
        var ended = false;
        session.TestEnded += _ => ended = true;
        await session.AnswerTestRequestAsync(true, true, true);
        Assert.Null(session.TestConsent.Pending);
        Assert.Null(session.TestConsent.ActiveRun);
        Assert.True(ended);
    }

    /// <summary>测试有最长时间和最多步数。</summary>
    [Fact]
    public void ConsentHasDurationAndStepCaps()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var consent = new GuestTestConsent(() => now);
        Assert.True(consent.Request(new TestRequest("run-0005", false)));
        consent.Answer(true, false, false, null);
        for (var i = 1; i <= GuestTestConsent.MaxSteps; i++)
        {
            Assert.Null(consent.BeginStep("run-0005", i, false, false));
            consent.EndStep("run-0005");
        }
        Assert.Equal("too-many-steps", consent.BeginStep("run-0005", GuestTestConsent.MaxSteps + 1, false, false));

        var consent2 = new GuestTestConsent(() => now);
        consent2.Request(new TestRequest("run-0006", false));
        consent2.Answer(true, false, false, null);
        // 一直有步骤（不算空闲），但总时间到了：结束。
        for (var i = 1; i <= 12; i++)
        {
            now += TimeSpan.FromMinutes(1);
            if (consent2.BeginStep("run-0006", i, false, false) is not null) break;
            consent2.EndStep("run-0006");
        }
        Assert.True(consent2.IsIdle(out var run));
        Assert.Equal("run-0006", run);
        Assert.NotNull(consent2.BeginStep("run-0006", 99, false, false));
    }

    /// <summary>恢复时出了没预料到的错（比如没权限写运行目录）：同意照样清掉，界面照样收到“测试结束”。</summary>
    [Fact]
    public async Task RestoreFailureStillEndsTest()
    {
        await using var session = NewSession((o, _) => IsStep(o) || o.RpcPort == 1
            ? Task.FromResult<IEasyTierSession>(new FakeEasyTier(o))
            : throw new UnauthorizedAccessException("denied"));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0007");
        await session.StartForRunAsync("run-0007", Step);
        var temporary = session.EasyTier!;
        var ended = 0;
        session.TestEnded += _ => ended++;
        await session.EndGuestTestAsync("run-0007");
        Assert.Equal(1, ended);
        Assert.Null(session.TestConsent.ActiveRun);
        Assert.Null(session.TestConsent.RestoringRun);
        Assert.True(temporary.HasExited);
        Assert.Null(session.EasyTier);
        Assert.True(session.TestConsent.Request(new TestRequest("run-after-failure", false)));
    }

    /// <summary>提权守护已退出：恢复普通模式必须创建普通会话，不能沿用“曾经提权”的状态。</summary>
    [Fact]
    public async Task ExitedElevatedSessionRestoresWithPlainProcess()
    {
        var modes = new List<bool>();
        await using var session = new DirectSession(null!, _dir, Room, false, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            CreateSessionForTest = (elevated, o) =>
            {
                modes.Add(elevated);
                if (elevated && !o.Tun) throw new ArgumentException("elevation requires tun");
                return new FakeEasyTier(o);
            },
        };
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-recreate");
        await session.StartForRunAsync("run-recreate", Step with { Tun = true, ExitNode = EasyTierArgs.HostIp });
        session.EasyTier!.Dispose();

        await session.EndGuestTestAsync("run-recreate");

        Assert.Equal(new[] { false, true, false }, modes);
        Assert.NotNull(session.EasyTier);
        Assert.False(session.EasyTier.HasExited);
        Assert.False(session.EasyTier.Options.Tun);
        Assert.Null(session.EasyTier.Options.ExitNode);
    }

    /// <summary>恢复失败清空会话后，仍能重新创建普通会话；测试在模式选择之后替代进程创建。</summary>
    [Fact]
    public async Task FailedElevatedRestoreCanRetryWithoutElevation()
    {
        var modes = new List<bool>();
        var failNext = false;
        await using var session = new DirectSession(null!, _dir, Room, false, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            CreateSessionForTest = (elevated, o) =>
            {
                modes.Add(elevated);
                if (elevated && !o.Tun) throw new ArgumentException("elevation requires tun");
                if (failNext) { failNext = false; throw new IOException("start failed"); }
                return new FakeEasyTier(o);
            },
        };
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-retry");
        await session.StartForRunAsync("run-retry", Step with { Tun = true });
        session.EasyTier!.Dispose();
        failNext = true;
        await session.EndGuestTestAsync("run-retry");
        Assert.Null(session.EasyTier);

        await session.StartEasyTierAsync(Original, default);

        Assert.Equal(new[] { false, true, false, false }, modes);
        Assert.False(session.EasyTier!.HasExited);
        Assert.False(session.EasyTier.Options.Tun);
    }

    /// <summary>房主和加入方都以当前存活会话判断提权：还活着时复用，退出后必须重新创建。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureElevationRecreatesExitedGuard(bool isHost)
    {
        var modes = new List<bool>();
        await using var session = new DirectSession(null!, _dir, Room, isHost, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            CreateSessionForTest = (elevated, o) =>
            {
                modes.Add(elevated);
                Assert.True(o.Tun);
                return new FakeEasyTier(o);
            },
        };
        await session.EnsureElevatedAsync(default);
        var first = session.EasyTier!;
        await session.EnsureElevatedAsync(default);
        Assert.Same(first, session.EasyTier);
        Assert.Single(modes);
        first.Dispose();

        await session.EnsureElevatedAsync(default);

        Assert.Equal(new[] { true, true }, modes);
        Assert.NotSame(first, session.EasyTier);
        Assert.False(session.EasyTier!.HasExited);
    }

    /// <summary>加入方平台不支持提权：经真实控制消息立即回拒绝，结束测试后仍恢复普通会话。</summary>
    [Fact]
    public async Task UnsupportedGuestElevationRepliesFalseAndCanRestore()
    {
        var elevationAttempts = 0;
        await using var session = new DirectSession(null!, _dir, Room, false, Options, new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            CreateSessionForTest = (elevated, o) =>
            {
                if (elevated)
                {
                    Interlocked.Increment(ref elevationAttempts);
                    throw new PlatformNotSupportedException("完整局域网只支持 Windows");
                }
                return new FakeEasyTier(o);
            },
        };
        await session.StartEasyTierAsync(Original, default);
        await session.PrepareAsync();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ControlReceived += (_, message) =>
        {
            if (message["op"]?.GetValue<string>() == "elevated") reply.TrySetResult(message);
        };
        var hostWait = host.WaitForPeerAsync(TimeSpan.FromSeconds(5));
        var peer = await session.Bridge!.PunchAsync(
            [new Candidate(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port))],
            NatKind.Cone, TimeSpan.FromSeconds(5));
        Assert.NotNull(peer);
        var guest = await hostWait;
        Assert.NotNull(guest);
        const string run = "run-unsupported";
        Assert.True(session.TestConsent.Request(new TestRequest(run, true)));
        Assert.True(await session.AnswerTestRequestAsync(true, true, false));

        await host.SendControlAsync(guest!, new JsonObject { ["op"] = "elevate", ["run"] = run });
        var answer = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, elevationAttempts);
        Assert.Equal(run, answer["run"]!.GetValue<string>());
        Assert.False(answer["ok"]!.GetValue<bool>());
        await session.StopGuestTestAsync();
        Assert.Null(session.TestConsent.ActiveRun);
        Assert.NotNull(session.EasyTier);
        Assert.False(session.EasyTier.HasExited);
        Assert.False(session.EasyTier.Options.Tun);
    }

    /// <summary>别的测试编号的“结束”：不动正在进行的测试，也不让界面收起。</summary>
    [Fact]
    public async Task WrongRunEndIsIgnored()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0008");
        var ended = 0;
        session.TestEnded += _ => ended++;
        await session.EndGuestTestAsync("run-other");
        Assert.Equal(0, ended);
        Assert.Equal("run-0008", session.TestConsent.ActiveRun);
    }

    /// <summary>等确认的请求放久了作废：不能再同意，也不挡住房主下一次请求。</summary>
    [Fact]
    public void PendingRequestExpires()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var consent = new GuestTestConsent(() => now);
        Assert.True(consent.Request(new TestRequest("run-0009", false)));
        Assert.False(consent.ExpirePending("run-0009"));
        now += GuestTestConsent.PendingLimit + TimeSpan.FromSeconds(1);
        Assert.True(consent.ExpirePending("run-0009"));
        Assert.Null(consent.Pending);
        Assert.Null(consent.Answer(true, false, false, null));
        Assert.True(consent.Request(new TestRequest("run-0010", false)));
        Assert.True(consent.Withdraw("run-0010"));
        Assert.False(consent.Withdraw("run-0010"));
    }

    /// <summary>还没同意时点“停止测试”：当作拒绝，请求清掉。</summary>
    [Fact]
    public async Task StopBeforeAnswerDeclines()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Assert.True(session.TestConsent.Request(new TestRequest("run-0011", true)));
        await session.StopGuestTestAsync();
        Assert.Null(session.TestConsent.Pending);
        Assert.Null(session.TestConsent.ActiveRun);
    }

    /// <summary>启动途中（比如在等权限确认）会话已经关了：刚起来的 EasyTier 马上关掉。</summary>
    [Fact]
    public async Task StartFinishingAfterDisposeIsStopped()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        FakeEasyTier? late = null;
        var session = NewSession(async (o, _) =>
        {
            started.TrySetResult();
            await gate.Task;
            return late = new FakeEasyTier(o);
        });
        var start = session.StartEasyTierAsync(Original, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        gate.SetResult();
        await start;
        Assert.True(late!.HasExited);
    }

    /// <summary>认得出 0.5.9 以前的 hello（带完整码、没有会话 id）；新版本的不算。</summary>
    [Fact]
    public void LegacyHelloIsRecognized()
    {
        Assert.True(DirectSession.IsLegacyHello(new JsonObject { ["op"] = "hello", ["code"] = "AMBER-D1-xxxx" }));
        var code = new DirectCode(true, "abcdef0123456789", NatKind.Cone, [new Candidate(PathKind.Lan, IPEndPoint.Parse("192.168.1.9:40001"))]);
        Assert.False(DirectSession.IsLegacyHello(DirectSession.Hello(code)));
    }

    /// <summary>对方不再回心跳：桥会补发探测包（任何版本都会回探测）。</summary>
    [Fact]
    public async Task SilentPeerIsReprobed()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var sealer = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        using var raw = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hostEnd = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        await raw.SendAsync(sealer.Seal(1, new byte[13]), hostEnd);

        var types = new List<byte>();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!types.Contains(1))
            {
                var r = await raw.ReceiveAsync(limit.Token);
                types.Add(r.Buffer[0]);
            }
        }
        catch (OperationCanceledException) { }
        Assert.Contains((byte)2, types);
        Assert.Contains((byte)4, types);
        Assert.Contains((byte)1, types);
        // 回一个探测确认：算重新连上了，桥里还是这一个对方。
        Assert.Single(host.Peers);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
