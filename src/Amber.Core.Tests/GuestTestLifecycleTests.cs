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
        session.TestEnded += () => ended++;
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
        session.TestEnded += () => ended = true;
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
        var ended = 0;
        session.TestEnded += () => ended++;
        await session.EndGuestTestAsync("run-0007");
        Assert.Equal(1, ended);
        Assert.Null(session.TestConsent.ActiveRun);
    }

    /// <summary>别的测试编号的“结束”：不动正在进行的测试，也不让界面收起。</summary>
    [Fact]
    public async Task WrongRunEndIsIgnored()
    {
        await using var session = NewSession((o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)));
        await session.StartEasyTierAsync(Original, default);
        Accept(session, "run-0008");
        var ended = 0;
        session.TestEnded += () => ended++;
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
        var code = new DirectCode(true, Room, "abcdef0123456789", NatKind.Cone, [new Candidate(PathKind.Lan, IPEndPoint.Parse("192.168.1.9:40001"))]);
        Assert.False(DirectSession.IsLegacyHello(DirectSession.Hello(code)));
    }

    /// <summary>对方不再回心跳：桥会补发探测包（任何版本都会回探测）。</summary>
    [Fact]
    public async Task SilentPeerIsReprobed()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), Room, true, 1, _ => { });
        await using var sealer = new DirectBridge(DirectBridge.CreateSocket(), Room, false, null, _ => { });
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
