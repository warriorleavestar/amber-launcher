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

    /// <summary>启动或换参数重启 EasyTier。完整局域网第一次启动会弹权限确认；已经提权的会话换参数不再弹。</summary>
    private async Task StartEasyTierAsync(EasyTierOptions options, CancellationToken ct)
    {
        if (_currentOptions is { } current && SameConfig(current, options) && EasyTier is { HasExited: false }) return;
        Log(L.T("core.mp.session.start") + string.Join(' ', EasyTierArgs.Build(options).Select(a => Room.Secrets.Contains(a) ? L.T("core.mp.diagnostics.secret") : a)));
        if (EasyTier is EasyTierElevatedSession elevated && !elevated.HasExited)
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
        StateChanged?.Invoke();
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
        switch (message["op"]?.GetValue<string>())
        {
            case "hello" when IsHost:
                if (LocalCode is { } local && TryParseHello(message, local.Session, out var nat, out var candidates))
                {
                    _guestCandidates = candidates;
                    _guestNat = nat;
                    Log(L.T("core.mp.session.guestaddresses") + string.Join(L.T("core.mp.format.listseparator"), candidates));
                }
                break;
            case "step" when !IsHost:
                _ = GuestStepAsync(peer, message);
                break;
            case "elevate" when !IsHost:
                _ = GuestElevateAsync(peer);
                break;
            case "elevated" when IsHost:
                _elevateReply?.TrySetResult(message["ok"]?.GetValue<bool>() == true);
                break;
            case "result" when IsHost:
                var index = message["i"]?.GetValue<int>() ?? -1;
                lock (_pending)
                    if (_pending.Remove(index, out var tcs)) tcs.TrySetResult(message);
                break;
        }
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

    /// <summary>房主：先问双方能不能提权（完整局域网和出口要用），然后排出步骤依次执行。</summary>
    public async Task<List<MatrixResult>> RunMatrixAsync(bool wantFullLan, IProgress<MatrixResult>? progress, Func<MatrixStep, bool, CancellationToken, Task<bool?>>? chatProbe, CancellationToken ct)
    {
        if (!IsHost || Bridge?.Peers.FirstOrDefault() is not { } peer) throw new InvalidOperationException(L.T("core.mp.matrix.peerrequired"));
        var fullLan = false;
        if (wantFullLan)
        {
            fullLan = await ElevateBothAsync(peer, ct);
            Log(fullLan ? L.T("core.mp.matrix.elevated") : L.T("core.mp.matrix.elevationdeclined"));
        }
        var members = (await (EasyTier?.PeersAsync(ct) ?? Task.FromResult(new List<PeerInfo>()))).Count(p => p.Ip.StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal));
        var plan = TestMatrix.Plan(new MatrixInputs(_gathered!.Candidates, _guestCandidates, members, Options.FallbackNodes.Count > 0, fullLan, Options.Simulated));
        Log(L.P("core.mp.matrix.plan", plan.Count, plan.Count));
        _chatProbe = chatProbe;
        var results = await TestMatrix.RunAsync(plan, new HostRunner(this), TimeSpan.FromSeconds(120), progress, Options.Simulated, ct);
        // 恢复：所有路径、原来的模式。
        Bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
        await StartEasyTierAsync(HostOptions(Options.FullLan, Options.AllowExit), ct);
        return results;
    }

    private Func<MatrixStep, bool, CancellationToken, Task<bool?>>? _chatProbe;

    /// <summary>加入方的聊天探测（界面设置）：发一条消息、等回显。</summary>
    public Func<MatrixStep, CancellationToken, Task<bool?>>? GuestChatProbe { get; set; }

    private async Task<bool> ElevateBothAsync(DirectPeer peer, CancellationToken ct)
    {
        try
        {
            if (!_elevated) await StartEasyTierAsync(HostOptions(true, Options.AllowExit), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        _elevateReply = new TaskCompletionSource<bool>();
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevate" });
        try
        {
            return await _elevateReply.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task GuestElevateAsync(DirectPeer peer)
    {
        var ok = true;
        try
        {
            if (!_elevated) await StartEasyTierAsync(GuestOptions(!UsingFallbackNode, true, null), CancellationToken.None);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ok = false;
            Log(L.T("core.mp.matrix.elevationfailed") + e.Message);
        }
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevated", ["ok"] = ok });
    }

    private sealed class HostRunner(DirectSession s) : IMatrixStepRunner
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
                message["passed"]?.GetValue<bool?>(),
                message["connectMs"]?.GetValue<long?>(),
                message["latency"]?.GetValue<double?>(),
                message["mbps"]?.GetValue<double?>(),
                message["chat"]?.GetValue<bool?>(),
                message["exit"]?.GetValue<bool?>(),
                message["reason"]?.GetValue<string>() ?? "");
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

    private async Task GuestStepAsync(DirectPeer controlPeer, JsonObject message)
    {
        var step = new MatrixStep(
            message["i"]!.GetValue<int>(),
            Enum.Parse<RouteKind>(message["route"]!.GetValue<string>()),
            message["path"]?.GetValue<string>() is { } p ? Enum.Parse<PathKind>(p) : null,
            message["fullLan"]!.GetValue<bool>(),
            message["exit"]!.GetValue<bool>());
        var hostIp = message["hostIp"]?.GetValue<string>();
        Log(L.F("core.mp.matrix.step", step.Index, step.Label));
        var result = new JsonObject { ["op"] = "result", ["i"] = step.Index };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
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
            result["passed"] = result["exit"]?.GetValue<bool?>() != false;
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or SocketException or InvalidOperationException)
        {
            result["passed"] = false;
            result["reason"] = e is OperationCanceledException ? L.T("core.mp.matrix.steptimeout") : e.Message;
            Log(L.F("core.mp.matrix.stepfailed", step.Index, result["reason"]));
        }
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
        EasyTier?.Dispose();
        if (_speed is not null) await _speed.DisposeAsync();
        if (Bridge is not null) await Bridge.DisposeAsync();
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
