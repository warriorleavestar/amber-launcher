using Amber.Core.Localization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

public sealed record DirectOptions(
    IReadOnlyList<string> StunServers,
    bool TryPortMapping,
    IReadOnlyList<string> FallbackNodes,
    bool FullLan,
    bool AllowExit,
    bool Verbose,
    bool Simulated = false);

/// <summary>
/// 一个直连房间：收集本机地址、生成邀请码 / 回复码、打洞、在直连桥上跑 EasyTier；打不通又配置了联机节点时退回节点。
/// 也负责全条件测试：房主经直连通道告诉加入方每一步怎么配，双方一起切换，加入方测完把结果发回来。
/// </summary>
public sealed class DirectSession : IAsyncDisposable
{
    private readonly EasyTierInstaller _installer;
    private readonly string _workDir;
    private readonly DiagnosticLog _log;
    private readonly string _machineId = Guid.NewGuid().ToString();
    private DirectGatherer.Gathered? _gathered;
    private DirectCode? _remote;
    private IReadOnlyList<Candidate> _guestCandidates = [];
    private NatKind _guestNat = NatKind.Unknown;
    private int _easyTierUdpPort;
    private EasyTierOptions? _currentOptions;
    private SpeedTestServer? _speed;
    private bool _elevated;
    private bool _speedOnVirtualIp;
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private TaskCompletionSource<bool>? _elevateReply;
    private TaskCompletionSource<JsonObject>? _testAnswer;
    private string? _hostRun;
    private Timer? _consentWatch;
    private CancellationTokenSource? _hostAbort;

    // 启动、换参数重启 EasyTier 一次只做一个（测试步骤、提权、结束恢复可能同时触发）。
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // 加入方：每次同意的测试一个取消源。步骤和提权都用它；测试结束（房主说结束、超时、用户点停止）时取消，
    // 还在路上的步骤不会在恢复之后又把 EasyTier 切回测试模式。
    private readonly object _runGate = new();
    private CancellationTokenSource? _runCts;
    private string? _runCtsRun;

    /// <summary>测试用：代替真的启动 EasyTier。</summary>
    internal Func<EasyTierOptions, CancellationToken, Task<IEasyTierSession>>? StartForTest { get; init; }

    /// <summary>房主：加入方是 0.5.9 以前的版本（hello 里带完整房间码、没有会话 id）。全条件测试需要对方升级。</summary>
    public bool GuestIsLegacy { get; private set; }

    /// <summary>加入方：开始执行测试的一步（界面显示“测试中”和完整局域网/出口提示）。</summary>
    public event Action<MatrixStep>? GuestStepStarted;

    /// <summary>加入方：全条件测试要本机用户同意（见 <see cref="GuestTestConsent"/>）。</summary>
    public GuestTestConsent TestConsent { get; } = new();

    /// <summary>加入方：房主请求全条件测试，界面要问用户。</summary>
    public event Action<TestRequest>? TestRequested;

    /// <summary>加入方：测试结束（或请求被撤回），界面收起确认。</summary>
    public event Action? TestEnded;

    public RoomCode Room { get; }
    public bool IsHost { get; }
    public DirectOptions Options { get; private set; }
    public DirectCode? LocalCode { get; private set; }
    public DirectBridge? Bridge { get; private set; }
    public IEasyTierSession? EasyTier { get; private set; }
    public int LocalGamePort { get; } = EasyTierArgs.FreeTcpPort();
    public int LocalChatPort { get; } = EasyTierArgs.FreeTcpPort();
    public int LocalSpeedPort { get; } = EasyTierArgs.FreeTcpPort();
    public bool UsingFallbackNode { get; private set; }
    public NatKind LocalNat => _gathered?.Nat ?? NatKind.Unknown;
    public string LocalNatDetail => _gathered?.NatDetail ?? "";
    public IPAddress? PublicIPv4 => _gathered?.Candidates.FirstOrDefault(c => c.Kind is PathKind.Punch or PathKind.Mapped)?.EndPoint.Address;

    /// <summary>加入方：测试矩阵的一步交给界面执行（聊天探测要用界面那边的连接）。为空时用内置的探测。</summary>
    public event Action? StateChanged;

    public DirectSession(EasyTierInstaller installer, string workDir, RoomCode room, bool isHost, DirectOptions options, DiagnosticLog log)
    {
        _installer = installer;
        _workDir = workDir;
        Room = room;
        IsHost = isHost;
        Options = options;
        _log = log;
    }

    private void Log(string text) => _log.Add(IsHost ? L.T("core.mp.session.host") : L.T("core.mp.session.guest"), text);

    /// <summary>收集地址，建好直连桥，生成自己的码（房主是邀请码，加入方是回复码）。房主同时启动 EasyTier。</summary>
    public async Task<DirectCode> PrepareAsync(CancellationToken ct = default)
    {
        var socket = DirectBridge.CreateSocket();
        Log(L.F("core.mp.session.socket", ((IPEndPoint)socket.LocalEndPoint!).Port));
        if (Options.Simulated)
        {
            // 本机模拟：只用回环地址，不查 STUN、不改路由器。结果会标“模拟”。
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _gathered = new DirectGatherer.Gathered([new Candidate(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, port))], NatKind.Cone, L.T("core.mp.session.simulated"), null);
            Log(L.T("core.mp.session.simulation"));
        }
        else
        {
            _gathered = await DirectGatherer.GatherAsync(socket, Options.StunServers, Options.TryPortMapping, Log, ct);
        }
        Log(L.F("core.mp.session.nat", DirectText.Nat(_gathered.Nat)));
        if (IsHost)
        {
            _easyTierUdpPort = FreeUdpPort();
            Bridge = new DirectBridge(socket, Room, true, _easyTierUdpPort, Log);
            Bridge.Connected += OnConnected;
            Bridge.ControlReceived += OnControl;
            _speed = new SpeedTestServer();
            await StartEasyTierAsync(HostOptions(Options.FullLan, Options.AllowExit), ct);
        }
        else
        {
            Bridge = new DirectBridge(socket, Room, false, null, Log);
            Bridge.Connected += OnConnected;
            Bridge.ControlReceived += OnControl;
        }
        var session = IsHost ? Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) : _remote?.Session ?? "";
        LocalCode = new DirectCode(!IsHost, Room, session.Length >= 8 ? session : "0000000000000000", _gathered.Nat, _gathered.Candidates);
        return LocalCode;
    }

    private static int FreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    private EasyTierOptions HostOptions(bool tun, bool allowExit, IReadOnlyList<string>? extra = null) =>
        new(Room, true, Options.FallbackNodes, EasyTierArgs.FreeTcpPort(), null, [$"udp://127.0.0.1:{_easyTierUdpPort}"], _machineId, tun, null, allowExit, null,
            Verbose(extra)) { ServesSpeedTest = true };

    private EasyTierOptions GuestOptions(bool viaBridge, bool tun, string? exitNode, IReadOnlyList<string>? extra = null, bool nodes = true)
    {
        var peers = new List<string>();
        if (viaBridge && Bridge is not null) peers.Add($"udp://127.0.0.1:{Bridge.LocalPort}");
        if (nodes) peers.AddRange(Options.FallbackNodes);
        return new EasyTierOptions(Room, false, peers, EasyTierArgs.FreeTcpPort(), tun ? null : LocalGamePort, null, _machineId, tun,
            tun ? null : LocalChatPort, false, exitNode, Verbose(extra), [(LocalSpeedPort, SpeedTestServer.Port)]);
    }

    private IReadOnlyList<string> Verbose(IReadOnlyList<string>? extra) =>
        [.. extra ?? [], .. Options.Verbose ? new[] { "--console-log-level", "debug" } : []];

    /// <summary>
    /// 启动或换参数重启 EasyTier。完整局域网第一次启动会弹权限确认；已经提权的会话换参数不再弹。
    /// 一次只做一个；拿到锁之后 ct 已经取消（比如测试已经结束）就不再启动。
    /// 和“正在跑的参数”比较（EasyTier.Options），不是上一次要求的参数；force 时总是重启（测试结束恢复）。
    /// </summary>
    internal async Task StartEasyTierAsync(EasyTierOptions options, CancellationToken ct, bool force = false)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!force && EasyTier is { HasExited: false } running && SameConfig(running.Options, options)) return;
            Log(L.T("core.mp.session.start") + string.Join(' ', EasyTierArgs.Build(options).Select(a => Room.Secrets.Contains(a) ? L.T("core.mp.diagnostics.secret") : a)));
            if (StartForTest is { } start)
            {
                var next = await start(options, ct);
                EasyTier?.Dispose();
                EasyTier = next;
            }
            else if (EasyTier is EasyTierElevatedSession elevated && !elevated.HasExited)
            {
                elevated.Restart(options);
                for (var i = 0; i < 40 && elevated.RestartPending; i++) await Task.Delay(250, ct);
            }
            else
            {
                EasyTier?.Dispose();
                EasyTier = options.Tun || _elevated
                    ? EasyTierElevatedSession.Start(_installer, options, Path.Combine(_workDir, "run-lan"))
                    : EasyTierSession.Start(_installer, options, Path.Combine(_workDir, IsHost ? "run-direct-host" : "run-direct-guest"));
                if (EasyTier is EasyTierElevatedSession) _elevated = true;
                if (EasyTier is EasyTierSession plain && Options.Verbose) plain.Output += line => _log.Add("EasyTier", line);
            }
            _currentOptions = options;
            EasyTierStarts++;
        }
        finally
        {
            _startLock.Release();
        }
        StateChanged?.Invoke();
    }

    /// <summary>加入方：同意了一次测试，给它一个取消源（最长 <see cref="GuestTestConsent.MaxDuration"/>）。</summary>
    internal void BeginRun(string run)
    {
        lock (_runGate)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource(GuestTestConsent.MaxDuration);
            _runCtsRun = run;
        }
    }

    /// <summary>测试用：按这次测试的取消令牌切换（和测试步骤一样）。</summary>
    internal Task StartForRunAsync(string run, EasyTierOptions options) => StartEasyTierAsync(options, RunToken(run));

    /// <summary>这次测试的取消令牌；不是正在进行的测试时返回已取消的令牌（什么都不执行）。</summary>
    private CancellationToken RunToken(string? run)
    {
        lock (_runGate) return _runCts is { } cts && run is not null && _runCtsRun == run ? cts.Token : new CancellationToken(true);
    }

    private void CancelRun(string? run)
    {
        lock (_runGate)
        {
            if (_runCts is null || run is not null && _runCtsRun != run) return;
            _runCts.Cancel();
        }
    }

    /// <summary>EasyTier 启动或重启过几次（包括已提权会话的原地重启）：界面按它判断要不要重新计时。</summary>
    public int EasyTierStarts { get; private set; }

    private static bool SameConfig(EasyTierOptions a, EasyTierOptions b) =>
        a.Tun == b.Tun && a.AllowExit == b.AllowExit && a.ExitNode == b.ExitNode && a.Peers.SequenceEqual(b.Peers)
        && (a.ExtraArgs ?? []).SequenceEqual(b.ExtraArgs ?? []);

    /// <summary>加入方：按邀请码打洞。房主能直接连到时很快通；否则要等房主贴回复码，所以等得久一点。</summary>
    public async Task<bool> JoinAsync(DirectCode invite, CancellationToken ct = default)
    {
        _remote = invite;
        if (LocalCode is { } code) LocalCode = code with { Session = invite.Session };
        Bridge!.SetRemoteCandidates(invite.Candidates);
        Log(L.F("core.mp.session.hostnat", DirectText.Nat(invite.Nat), string.Join(L.T("core.mp.format.listseparator"), invite.Candidates)));
        var peer = await Bridge.PunchAsync(invite.Candidates, invite.Nat, TimeSpan.FromSeconds(invite.DirectlyReachable ? 20 : 90), ct);
        if (peer is not null)
        {
            await StartEasyTierAsync(GuestOptions(true, Options.FullLan, null), ct);
            return true;
        }
        var reason = FailureReason(invite.Nat, LocalNat, invite.Candidates, _gathered?.Candidates ?? []);
        Log(L.T("core.mp.session.failed") + reason);
        if (Options.FallbackNodes.Count > 0)
        {
            Log(L.T("core.mp.session.fallback") + string.Join(L.T("core.mp.format.listseparator"), Options.FallbackNodes));
            UsingFallbackNode = true;
            await StartEasyTierAsync(GuestOptions(false, Options.FullLan, null), ct);
            return true;
        }
        throw new DirectFailedException(reason);
    }

    /// <summary>房主：加入方的回复码到了，往它的地址打洞。</summary>
    public async Task<bool> AddReplyAsync(DirectCode reply, CancellationToken ct = default)
    {
        if (reply.Room != Room) throw new DirectFailedException(L.T("core.mp.code.wrongroom"));
        _guestCandidates = reply.Candidates;
        _guestNat = reply.Nat;
        Bridge!.SetRemoteCandidates(reply.Candidates);
        var peer = await Bridge.PunchAsync(reply.Candidates, reply.Nat, TimeSpan.FromSeconds(60), ct);
        if (peer is null) Log(L.T("core.mp.session.punchfailed") + FailureReason(LocalNat, reply.Nat, _gathered?.Candidates ?? [], reply.Candidates));
        return peer is not null;
    }

    /// <summary>说清楚为什么连不上。</summary>
    public static string FailureReason(NatKind a, NatKind b, IReadOnlyList<Candidate> aCands, IReadOnlyList<Candidate> bCands)
    {
        var noV6 = !aCands.Any(c => c.Kind == PathKind.IPv6) || !bCands.Any(c => c.Kind == PathKind.IPv6);
        if (a == NatKind.Symmetric && b == NatKind.Symmetric)
            return L.T(noV6 ? "core.mp.failure.symmetric_noipv6" : "core.mp.failure.symmetric");
        if (a == NatKind.Unknown || b == NatKind.Unknown)
            return L.T(noV6 ? "core.mp.failure.unknown_noipv6" : "core.mp.failure.unknown");
        return L.T(noV6 ? "core.mp.failure.timeout_noipv6" : "core.mp.failure.timeout");
    }

    private void OnConnected(DirectPeer peer)
    {
        if (!IsHost && Bridge is not null && _gathered is not null)
        {
            // 告诉房主自己的地址（房主能被直接连上时，加入方不用贴回复码，房主靠这条消息拿到加入方地址）。
            if (LocalCode is { } code) _ = Bridge.SendControlAsync(peer, Hello(code));
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 加入方连上后发给房主的 hello：只有会话 id、NAT 类型和地址。
    /// 直连通道的控制消息只认证不加密，所以不带房间码；能通过 HMAC 校验本身就说明对方知道房间密钥。
    /// </summary>
    internal static JsonObject Hello(DirectCode code)
    {
        var hello = code.Addresses();
        hello["op"] = "hello";
        return hello;
    }

    /// <summary>房主：解析 hello，会话 id 要和自己的一致（别的房间或旧会话的 hello 不认）。</summary>
    internal static bool TryParseHello(JsonObject message, string expectedSession, out NatKind nat, out List<Candidate> candidates) =>
        DirectCode.TryParseAddresses(message, out var session, out nat, out candidates)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(session), Encoding.UTF8.GetBytes(expectedSession));

    private void OnControl(DirectPeer peer, JsonObject message)
    {
        switch (Str(message, "op"))
        {
            case "hello" when IsHost && IsLegacyHello(message):
                // 0.5.9 以前的加入方：hello 里是完整的邀请码（含房间密钥，明文经过网络）。不解析它，只记下来提醒用户。
                if (!GuestIsLegacy)
                {
                    GuestIsLegacy = true;
                    Log(L.T("core.mp.session.legacyguest"));
                    StateChanged?.Invoke();
                }
                break;
            case "hello" when IsHost:
                if (LocalCode is { } local && TryParseHello(message, local.Session, out var nat, out var candidates))
                {
                    _guestCandidates = candidates;
                    _guestNat = nat;
                    Log(L.T("core.mp.session.guestaddresses") + string.Join(L.T("core.mp.format.listseparator"), candidates));
                }
                break;
            case "test-request" when !IsHost:
                if (Str(message, "run") is { } requestRun && TestConsent.Request(new TestRequest(requestRun, Bool(message, "fullLan") == true)))
                {
                    Log(L.T("core.mp.matrix.requested"));
                    TestRequested?.Invoke(TestConsent.Pending!);
                }
                break;
            case "test-end" when !IsHost:
                TestConsent.Withdraw(Str(message, "run") ?? "");
                _ = EndGuestTestAsync(Str(message, "run"));
                break;
            case "step" when !IsHost:
                _ = GuestStepAsync(peer, message);
                break;
            case "elevate" when !IsHost:
                _ = GuestElevateAsync(peer, Str(message, "run"));
                break;
            case "test-abort" when IsHost:
                // 加入方的用户点了“停止测试”。
                if (Str(message, "run") is { } abortRun && abortRun == _hostRun) _hostAbort?.Cancel();
                break;
            case "test-answer" when IsHost:
                if (Str(message, "run") is { } answerRun && answerRun == _hostRun) _testAnswer?.TrySetResult(message);
                break;
            case "elevated" when IsHost:
                if (Str(message, "run") == _hostRun) _elevateReply?.TrySetResult(Bool(message, "ok") == true);
                break;
            case "result" when IsHost:
                if (Str(message, "run") != _hostRun) break;
                var index = Int(message, "i") ?? -1;
                lock (_pending)
                    if (_pending.Remove(index, out var tcs)) tcs.TrySetResult(message);
                break;
        }
    }

    /// <summary>0.5.9 以前的 hello：带 code（完整邀请码），没有 s（会话 id）。</summary>
    internal static bool IsLegacyHello(JsonObject message) => message.ContainsKey("code") && !message.ContainsKey("s");

    // 对方发来的 JSON 字段类型不对时当作没有，不抛异常。
    private static string? Str(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<string>(out var v) == true ? v : null;
    private static bool? Bool(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<bool>(out var v) == true ? v : null;
    private static int? Int(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<int>(out var v) == true ? v : null;
    private static long? Long(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<long>(out var v) == true ? v : null;
    private static double? Double(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<double>(out var v) == true ? v : null;

    /// <summary>
    /// 加入方：用户回答房主的测试请求。同意时只同意勾上的部分（完整局域网、把房主当出口），记下现在的联机设置，测完恢复。
    /// </summary>
    public async Task AnswerTestRequestAsync(bool accept, bool allowFullLan, bool allowExit)
    {
        var run = TestConsent.Pending?.Run;
        if (run is null) return;
        if (Bridge?.Peers.FirstOrDefault() is not { } peer)
        {
            // 和房主断开了：当作拒绝（不留下一个没人知道的“已同意”）。
            TestConsent.Answer(false, false, false, null);
            Log(L.T("core.mp.matrix.nopeerdeclined"));
            TestEnded?.Invoke();
            return;
        }
        // 记下现在真正在跑的参数（不是上次要求的），测完恢复成它。
        var answer = TestConsent.Answer(accept, allowFullLan, allowExit, EasyTier?.Options ?? _currentOptions);
        if (answer is { } a)
        {
            BeginRun(run);
            Log(L.T("core.mp.matrix.accepted"));
            _consentWatch?.Dispose();
            _consentWatch = new Timer(_ =>
            {
                if (TestConsent.IsIdle(out var idleRun))
                {
                    Log(L.T("core.mp.matrix.consentexpired"));
                    _ = EndGuestTestAsync(idleRun);
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }
        await Bridge.SendControlAsync(peer, new JsonObject
        {
            ["op"] = "test-answer",
            ["run"] = run,
            ["ok"] = answer is not null,
            ["fullLan"] = answer?.FullLan == true,
            ["exit"] = answer?.Exit == true,
        });
    }

    /// <summary>加入方：用户点了“停止测试”。告诉房主（尽力而为），然后自己结束并恢复。</summary>
    public async Task StopGuestTestAsync()
    {
        if (TestConsent.ActiveRun is not { } run) return;
        Log(L.T("core.mp.matrix.gueststopped"));
        if (Bridge?.Peers.FirstOrDefault() is { } peer)
        {
            try { await Bridge.SendControlAsync(peer, new JsonObject { ["op"] = "test-abort", ["run"] = run }); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        await EndGuestTestAsync(run);
    }

    /// <summary>
    /// 加入方：测试结束（房主说结束、太久没动静、超过最长时间、用户点停止）：清掉同意，取消还在路上的步骤，
    /// 在启动锁里无条件恢复所有路径和原来的联机设置。
    /// </summary>
    internal async Task EndGuestTestAsync(string? run)
    {
        var ended = TestConsent.End(run, out var original);
        if (ended)
        {
            CancelRun(run);
            _consentWatch?.Dispose();
            _consentWatch = null;
            if (Bridge is { } bridge)
            {
                bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
                bridge.SprayPorts = 256;
            }
            if (original is not null)
            {
                try
                {
                    await StartEasyTierAsync(original with { RpcPort = EasyTierArgs.FreeTcpPort() }, CancellationToken.None, force: true);
                    Log(L.T("core.mp.matrix.restored"));
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log(L.T("core.mp.matrix.restorefailedsettings") + e.Message);
                }
            }
        }
        TestEnded?.Invoke();
    }

    /// <summary>当前连接走的路径，界面显示用。</summary>
    public string DescribePath(IReadOnlyList<PeerInfo> peers)
    {
        var bridgePeer = Bridge?.Peers.FirstOrDefault();
        if (!IsHost)
        {
            var host = peers.FirstOrDefault(p => p.Ip == EasyTierArgs.HostIp && p.Cost != "Local");
            if (host is null) return UsingFallbackNode ? L.T("core.mp.session.nodeconnecting") : L.T("core.mp.session.connecting");
            if (UsingFallbackNode || bridgePeer is null) return L.T(host.Cost.StartsWith("relay", StringComparison.Ordinal) ? "core.mp.path.vianode_relay" : "core.mp.path.vianode_introduced");
            return L.F("core.mp.session.path", DirectText.Path(bridgePeer.Path), bridgePeer.RttMs is { } r ? $"{r:F0} ms" : "-", bridgePeer.LossPercent);
        }
        var guests = Bridge?.Peers ?? [];
        return guests.Count == 0 ? L.T("core.mp.session.waiting") : string.Join(L.T("core.mp.format.peerseparator"), guests.Select(g => $"{DirectText.Path(g.Path)} {(g.RttMs is { } r ? $"{r:F0} ms" : "")}"));
    }

    // ---------------- 全条件测试 ----------------

    /// <summary>房主：等加入方确认多久。</summary>
    public static readonly TimeSpan TestAnswerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 房主：先请加入方确认（加入方用户点了同意才开始，完整局域网和出口要它分别勾选），
    /// 再问双方能不能提权，然后排出步骤依次执行。结束、出错、取消都通知加入方结束并恢复原来的设置。
    /// </summary>
    public async Task<List<MatrixResult>> RunMatrixAsync(bool wantFullLan, IProgress<MatrixResult>? progress, Func<MatrixStep, bool, CancellationToken, Task<bool?>>? chatProbe, CancellationToken ct)
    {
        if (!IsHost || Bridge?.Peers.FirstOrDefault() is not { } peer) throw new InvalidOperationException(L.T("core.mp.matrix.peerrequired"));
        if (GuestIsLegacy) throw new InvalidOperationException(L.T("core.mp.matrix.legacyguest"));
        var run = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _hostAbort = abort;
        var callerCt = ct;
        ct = abort.Token;
        _hostRun = run;
        _testAnswer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Bridge.SendControlAsync(peer, new JsonObject { ["op"] = "test-request", ["run"] = run, ["fullLan"] = wantFullLan });
            JsonObject answer;
            try
            {
                answer = await _testAnswer.Task.WaitAsync(TestAnswerTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(L.T("core.mp.matrix.requesttimeout"));
            }
            if (Bool(answer, "ok") != true) throw new InvalidOperationException(L.T("core.mp.matrix.declined"));
            var guestFullLan = Bool(answer, "fullLan") == true;
            var guestExit = Bool(answer, "exit") == true;
            var fullLan = false;
            if (wantFullLan && guestFullLan)
            {
                fullLan = await ElevateBothAsync(peer, run, ct);
                Log(fullLan ? L.T("core.mp.matrix.elevated") : L.T("core.mp.matrix.elevationdeclined"));
            }
            var members = (await (EasyTier?.PeersAsync(ct) ?? Task.FromResult(new List<PeerInfo>()))).Count(p => p.Ip.StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal));
            var plan = TestMatrix.Plan(new MatrixInputs(_gathered!.Candidates, _guestCandidates, members, Options.FallbackNodes.Count > 0, fullLan, Options.Simulated));
            // 加入方没同意把房主当出口：出口那几步不测。
            if (!guestExit) plan = [.. plan.Where(s => !s.Exit).Select((s, i) => s with { Index = i + 1 })];
            Log(L.P("core.mp.matrix.plan", plan.Count, plan.Count));
            _chatProbe = chatProbe;
            return await TestMatrix.RunAsync(plan, new HostRunner(this, run), TimeSpan.FromSeconds(120), progress, Options.Simulated, ct);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested && !callerCt.IsCancellationRequested)
        {
            throw new InvalidOperationException(L.T("core.mp.matrix.guestaborted"));
        }
        finally
        {
            _hostRun = null;
            _hostAbort = null;
            // 恢复：所有路径、原来的模式；通知加入方结束（它会恢复自己的设置）。
            Bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
            if (Bridge.Peers.FirstOrDefault() is { } back)
            {
                try { await Bridge.SendControlAsync(back, new JsonObject { ["op"] = "test-end", ["run"] = run }); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
            await StartEasyTierAsync(HostOptions(Options.FullLan, Options.AllowExit), CancellationToken.None);
        }
    }

    private Func<MatrixStep, bool, CancellationToken, Task<bool?>>? _chatProbe;

    /// <summary>加入方的聊天探测（界面设置）：发一条消息、等回显。</summary>
    public Func<MatrixStep, CancellationToken, Task<bool?>>? GuestChatProbe { get; set; }

    private async Task<bool> ElevateBothAsync(DirectPeer peer, string run, CancellationToken ct)
    {
        try
        {
            if (!_elevated) await StartEasyTierAsync(HostOptions(true, Options.AllowExit), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        _elevateReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevate", ["run"] = run });
        try
        {
            return await _elevateReply.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>加入方：房主要提权。只在用户同意了这次测试、并且勾了完整局域网时执行。</summary>
    private async Task GuestElevateAsync(DirectPeer peer, string? run)
    {
        var ok = TestConsent.AllowsElevate(run);
        if (!ok) Log(L.T("core.mp.matrix.notconsented"));
        else
        {
            try
            {
                if (!_elevated) await StartEasyTierAsync(GuestOptions(!UsingFallbackNode, true, null), RunToken(run));
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ok = false;
                Log(L.T("core.mp.matrix.elevationfailed") + e.Message);
            }
        }
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevated", ["run"] = run, ["ok"] = ok });
    }

    private sealed class HostRunner(DirectSession s, string run) : IMatrixStepRunner
    {
        public async Task<MatrixResult> RunAsync(MatrixStep step, CancellationToken ct)
        {
            var peer = s.Bridge!.Peers.FirstOrDefault() ?? throw new InvalidOperationException(L.T("core.mp.matrix.disconnected"));
            var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (s._pending) s._pending[step.Index] = reply;
            s.Log(L.F("core.mp.matrix.step", step.Index, step.Label));
            await s.Bridge.SendControlAsync(peer, new JsonObject
            {
                ["op"] = "step",
                ["run"] = run,
                ["i"] = step.Index,
                ["route"] = step.Route.ToString(),
                ["path"] = step.Path?.ToString(),
                ["fullLan"] = step.FullLan,
                ["exit"] = step.Exit,
                ["hostIp"] = s.PublicIPv4?.ToString(),
            });
            // 房主这边：同样只开这条路径、按这一步的模式重启 EasyTier，然后往加入方打洞。
            await s.ApplyStepAsync(step, ct);
            if (step.Route == RouteKind.Direct)
            {
                _ = s.Bridge.PunchAsync(s._guestCandidates, s._guestNat, TimeSpan.FromSeconds(30), ct);
            }
            var chatTask = s._chatProbe is { } probe ? probe(step, true, ct) : Task.FromResult<bool?>(null);
            // 等加入方的结果；期间如果这一步把连接弄断了，30 秒后恢复全部路径以便收结果。
            var restore = Task.Delay(TimeSpan.FromSeconds(35), ct).ContinueWith(_ =>
            {
                if (s.Bridge.Peers.Count == 0)
                {
                    s.Bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
                    _ = s.Bridge.PunchAsync(s._guestCandidates, s._guestNat, TimeSpan.FromSeconds(60), CancellationToken.None);
                }
            }, TaskScheduler.Default);
            var message = await reply.Task.WaitAsync(ct);
            s.Bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
            return new MatrixResult(step,
                Bool(message, "passed"),
                Long(message, "connectMs"),
                Double(message, "latency"),
                Double(message, "mbps"),
                Bool(message, "chat"),
                Bool(message, "exit"),
                Str(message, "reason") ?? "");
        }
    }

    /// <summary>两边都要做的切换：只允许这一步的路径、断开旧连接、按模式重启 EasyTier。</summary>
    private async Task ApplyStepAsync(MatrixStep step, CancellationToken ct)
    {
        var bridge = Bridge!;
        if (step.Route == RouteKind.Direct)
        {
            bridge.AllowedPaths.Clear();
            bridge.AllowedPaths.Add(step.Path!.Value);
            if (step.Path == PathKind.PunchSpray) bridge.AllowedPaths.Add(PathKind.Punch);
            bridge.SprayPorts = step.Path == PathKind.PunchSpray ? 256 : 0;
            bridge.DropPeers();
        }
        var relay = step.Route == RouteKind.Relay ? new[] { "--disable-p2p" } : null;
        if (IsHost)
        {
            await StartEasyTierAsync(HostOptions(step.FullLan, step.Exit), ct);
            // 完整局域网模式下加入方直接连房主的虚拟 IP 测吞吐，等虚拟网卡起来后再监听上去。
            if (step.FullLan && _speed is not null && !_speedOnVirtualIp)
                for (var i = 0; i < 20 && !(_speedOnVirtualIp = _speed.TryAddListener(IPAddress.Parse(EasyTierArgs.HostIp))); i++) await Task.Delay(500, ct);
        }
        else await StartEasyTierAsync(GuestOptions(step.Route != RouteKind.Node, step.FullLan, step.Exit ? EasyTierArgs.HostIp : null, relay, step.Route != RouteKind.Direct), ct);
    }

    /// <summary>解析房主发来的一步；字段缺失或类型不对时返回 null。</summary>
    internal static MatrixStep? ParseStep(JsonObject message)
    {
        if (Int(message, "i") is not { } index || Str(message, "route") is not { } routeText
            || !Enum.TryParse<RouteKind>(routeText, out var route) || !Enum.IsDefined(route)) return null;
        PathKind? path = null;
        if (Str(message, "path") is { } pathText)
        {
            if (!Enum.TryParse<PathKind>(pathText, out var parsed) || !Enum.IsDefined(parsed)) return null;
            path = parsed;
        }
        if (route == RouteKind.Direct && path is null) return null;
        return new MatrixStep(index, route, path, Bool(message, "fullLan") == true, Bool(message, "exit") == true);
    }

    private async Task GuestStepAsync(DirectPeer controlPeer, JsonObject message)
    {
        var run = Str(message, "run");
        if (ParseStep(message) is not { } step) return;
        // 没同意、旧的、重复的、乱序的、超出同意范围（完整局域网、出口）的一步都不执行，只回一个失败。
        if (TestConsent.BeginStep(run, step.Index, step.FullLan, step.Exit) is { } refused)
        {
            Log(L.F("core.mp.matrix.steprefused", step.Index, refused));
            await Bridge!.SendControlAsync(controlPeer, new JsonObject { ["op"] = "result", ["run"] = run, ["i"] = step.Index, ["passed"] = false, ["reason"] = L.T("core.mp.matrix.notconsented") });
            return;
        }
        try
        {
            GuestStepStarted?.Invoke(step);
            await GuestRunStepAsync(step, Str(message, "hostIp"), run);
        }
        finally
        {
            TestConsent.EndStep(run);
        }
    }

    private async Task GuestRunStepAsync(MatrixStep step, string? hostIp, string? run)
    {
        Log(L.F("core.mp.matrix.step", step.Index, step.Label));
        var result = new JsonObject { ["op"] = "result", ["run"] = run, ["i"] = step.Index };
        // 测试结束（取消这次测试）时这一步跟着停，不会在恢复之后再切换。
        var runToken = RunToken(run);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        cts.CancelAfter(TimeSpan.FromSeconds(100));
        var watch = Stopwatch.StartNew();
        try
        {
            await ApplyStepAsync(step, cts.Token);
            if (step.Route == RouteKind.Direct)
            {
                var punched = await Bridge!.PunchAsync(_remote!.Candidates, _remote.Nat, TimeSpan.FromSeconds(30), cts.Token);
                if (punched is null) throw new DirectFailedException(L.F("core.mp.matrix.pathfailed", DirectText.Path(step.Path!.Value)));
            }
            // 等 EasyTier 看到房主。
            PeerInfo? host = null;
            for (var i = 0; i < 60 && host is null; i++)
            {
                await Task.Delay(500, cts.Token);
                host = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Ip == EasyTierArgs.HostIp && x.Cost != "Local");
            }
            if (host is null) throw new DirectFailedException(L.T("core.mp.matrix.hosttimeout"));
            result["connectMs"] = watch.ElapsedMilliseconds;
            if (step.Route == RouteKind.Relay)
            {
                var other = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Hostname == "amber-guest" && x.Cost.StartsWith("relay", StringComparison.Ordinal));
                if (other is null) throw new DirectFailedException(L.T("core.mp.matrix.nootherpeer"));
                result["latency"] = other.LatencyMs;
            }
            else
            {
                await Task.Delay(1500, cts.Token);
                result["latency"] = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Ip == EasyTierArgs.HostIp && x.Cost != "Local")?.LatencyMs;
                var (speedHost, speedPort) = step.FullLan ? (EasyTierArgs.HostIp, SpeedTestServer.Port) : ("127.0.0.1", LocalSpeedPort);
                using var speedLimit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                speedLimit.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    result["mbps"] = Math.Round(await SpeedTestServer.MeasureAsync(speedHost, speedPort, 4 * 1024 * 1024, speedLimit.Token), 1);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException && !cts.IsCancellationRequested)
                {
                    Log(L.T("core.mp.matrix.speedfailed") + e.Message);
                }
            }
            if (GuestChatProbe is { } chat) result["chat"] = await chat(step, cts.Token);
            if (step.Exit)
            {
                if (Options.Simulated || hostIp is null)
                {
                    result["exit"] = null;
                    result["reason"] = L.T("core.mp.matrix.simulatedexit");
                }
                else
                {
                    using var probe = DirectBridge.CreateSocket();
                    var mapped = await Stun.QueryAsync(probe, Options.StunServers.FirstOrDefault() ?? Stun.DefaultServers[0], TimeSpan.FromSeconds(4), cts.Token);
                    result["exit"] = mapped?.Address.ToString() == hostIp;
                    if (mapped is null) result["reason"] = L.T("core.mp.matrix.exitnostun");
                    else if (mapped.Address.ToString() != hostIp) result["reason"] = L.T("core.mp.matrix.exitwrongip");
                }
            }
            result["passed"] = Bool(result, "exit") != false;
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or SocketException or InvalidOperationException)
        {
            result["passed"] = false;
            result["reason"] = e is OperationCanceledException ? L.T("core.mp.matrix.steptimeout") : e.Message;
            Log(L.F("core.mp.matrix.stepfailed", step.Index, result["reason"]));
        }
        // 测试已经结束：恢复由 EndGuestTestAsync 做，这里不再打洞、不再回结果。
        if (runToken.IsCancellationRequested) return;
        // 恢复全部路径，确保能把结果发回去。
        var bridge = Bridge!;
        bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
        bridge.SprayPorts = 256;
        if (bridge.Peers.Count == 0) await bridge.PunchAsync(_remote!.Candidates, _remote.Nat, TimeSpan.FromSeconds(60));
        if (bridge.Peers.FirstOrDefault() is { } back) await bridge.SendControlAsync(back, result);
        else Log(L.F("core.mp.matrix.restorefailed", step.Index));
    }

    public async ValueTask DisposeAsync()
    {
        // 每一步单独兜住：前面哪一步出错，后面的（关 EasyTier、关桥、撤端口映射）照样做。
        void Try(Action step)
        {
            try { step(); }
            catch (Exception e) { Log(L.F("core.mp.punch.localerror", e.GetType().Name)); }
        }
        async Task TryAsync(Func<Task> step)
        {
            try { await step(); }
            catch (Exception e) { Log(L.F("core.mp.punch.localerror", e.GetType().Name)); }
        }
        Try(() => _consentWatch?.Dispose());
        Try(() =>
        {
            lock (_runGate)
            {
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = null;
            }
        });
        Try(() => EasyTier?.Dispose());
        if (_speed is not null) await TryAsync(() => _speed.DisposeAsync().AsTask());
        if (Bridge is not null) await TryAsync(() => Bridge.DisposeAsync().AsTask());
        if (_gathered?.Mapping?.Remove is { } remove)
        {
            try
            {
                await remove().WaitAsync(TimeSpan.FromSeconds(3));
                Log(L.T("core.mp.mapping.removed"));
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or TimeoutException or OperationCanceledException) { }
        }
    }
}

public sealed class DirectFailedException(string message) : Exception(message);
