using Amber.Core.Localization;
using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>联机页的直连房间（不用联机节点）、网络检测、调试模式和全条件测试。</summary>
public sealed partial class MultiplayerViewModel
{
    private DirectSession? _direct;
    private readonly DiagnosticLog _diag = new();
    private CancellationTokenSource? _matrixCts;
    private List<MatrixResult> _lastMatrix = [];

    public ObservableCollection<string> DebugLines { get; } = [];
    public ObservableCollection<string> MatrixLines { get; } = [];

    [ObservableProperty] public partial bool DebugMode { get; set; }
    [ObservableProperty] public partial bool IncludePublicIps { get; set; }
    [ObservableProperty] public partial bool DirectUseNodes { get; set; } = true;
    [ObservableProperty] public partial bool DirectSimulate { get; set; }
    [ObservableProperty] public partial string StunText { get; set; } = string.Join(", ", Stun.DefaultServers);
    [ObservableProperty] public partial string DirectInviteInput { get; set; } = "";
    [ObservableProperty] public partial string DirectReplyInput { get; set; } = "";
    /// <summary>自己的直连码（完整的，复制用）。界面默认显示打码后的 <see cref="MyDirectCodeDisplay"/>。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MyDirectCodeDisplay), nameof(InviteDisplay), nameof(HasInvite))] public partial string MyDirectCode { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MyDirectCodeDisplay))] public partial bool DirectCodeShown { get; set; }
    public string MyDirectCodeDisplay => DirectCodeShown || MyDirectCode.Length <= 12 ? MyDirectCode : MyDirectCode[..6] + new string('•', 8) + MyDirectCode[^4..];

    [RelayCommand]
    private void ToggleDirectCodeShown() => DirectCodeShown = !DirectCodeShown;
    [ObservableProperty] public partial string DirectHint { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentPathText))] public partial string PathText { get; set; } = "";
    public string CurrentPathText => L.F("multiplayer.direct.current_path", PathText);
    [ObservableProperty] public partial string NetworkCheckText { get; set; } = "";
    [ObservableProperty] public partial bool IsCheckingNetwork { get; set; }
    [ObservableProperty] public partial bool MatrixRunning { get; set; }
    [ObservableProperty] public partial bool MatrixFullLan { get; set; }

    partial void OnMatrixFullLanChanged(bool value)
    {
        if (value && !CanUseFullLan) MatrixFullLan = false;
    }

    /// <summary>加入方：房主请求全条件测试，等用户确认。</summary>
    [ObservableProperty] public partial bool TestRequestPending { get; set; }

    /// <summary>加入方：正在跑房主发起的测试（显示“测试中”横幅和“停止测试”）。</summary>
    [ObservableProperty] public partial bool GuestTestRunning { get; set; }

    [ObservableProperty] public partial string GuestTestBanner { get; set; } = "";

    /// <summary>测试把出口设成了房主：测试前的出口设置，结束时还原。</summary>
    private string? _exitBeforeTest;
    private string? _guestTestRun;
    [ObservableProperty] public partial bool TestRequestWantsFullLan { get; set; }
    [ObservableProperty] public partial bool TestAllowFullLan { get; set; }
    [ObservableProperty] public partial bool TestAllowExit { get; set; }

    partial void OnTestAllowFullLanChanged(bool value)
    {
        if (value && !CanUseFullLan)
        {
            TestAllowFullLan = false;
            return;
        }
        if (!value) TestAllowExit = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectHost))]
    public partial bool IsDirect { get; set; }

    public bool IsDirectHost => IsDirect && IsHosting;

    /// <summary>测试用：现在的直连会话。</summary>
    internal DirectSession? DirectForTest => _direct;

    /// <summary>测试用：现在房间的密钥（检查诊断报告里没有它们）。</summary>
    internal IReadOnlyList<string> RoomSecretsForTest => _room is { } room ? [.. room.Secrets] : [];

    private void InitDirect()
    {
        _diag.Added += entry => Dispatcher.UIThread.Post(() =>
        {
            if (!DebugMode && entry.Category == "EasyTier") return;
            DebugLines.Add(NetDiagnostics.Redact(entry.ToString(), _room is { } r ? [.. r.Secrets] : [], includePublicIps: true));
            while (DebugLines.Count > 400) DebugLines.RemoveAt(0);
        });
    }

    private List<string> StunServers() =>
        StunText.Split([',', '\uFF0C', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private DirectOptions MakeDirectOptions() => new(
        StunServers(),
        !DirectSimulate,
        DirectUseNodes ? (TestPeers ?? _service.MultiplayerPeers).ToList() : [],
        FullLanMode,
        FullLanMode && AllowExitNode,
        DebugMode,
        DirectSimulate);

    /// <summary>检测网络：IPv6、STUN 外部地址和 NAT 类型、路由器能不能映射端口（试完马上删掉）。</summary>
    [RelayCommand]
    private async Task CheckNetworkAsync()
    {
        if (IsCheckingNetwork) return;
        IsCheckingNetwork = true;
        NetworkCheckText = L.T("multiplayer.network.checking");
        try
        {
            using var socket = DirectBridge.CreateSocket();
            var lines = new List<string>();
            var gathered = await DirectGatherer.GatherAsync(socket, StunServers(), true, l => { lines.Add(l); _diag.Add(L.T("multiplayer.network.diagnostic_category"), l); });
            if (gathered.Mapping?.Remove is { } remove)
            {
                try { await remove(); } catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException) { }
            }
            var v6 = gathered.Candidates.Any(c => c.Kind == PathKind.IPv6);
            var verdict = gathered.Nat switch
            {
                NatKind.Open => L.T("multiplayer.network.open"),
                NatKind.Cone => L.T("multiplayer.network.cone"),
                NatKind.Symmetric => v6 ? L.T("multiplayer.network.symmetric_ipv6") : L.T("multiplayer.network.symmetric"),
                _ => L.T("multiplayer.network.unknown"),
            };
            NetworkCheckText = $"{verdict}\n{string.Join("\n", lines)}";
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            NetworkCheckText = L.F("multiplayer.network.check_failed", e.Message);
        }
        finally
        {
            IsCheckingNetwork = false;
        }
    }

    /// <summary>
    /// 房主：创建直连房间。和经节点的房间一样是邀请房间：随机的房间凭据、成员表、大厅和敲门；
    /// 大厅密钥 L 在整个房间期间不换（直连桥的 HMAC 钥匙由它派生）。马上发第一张邀请（一次、要房主批准），生成直连邀请码。
    /// </summary>
    [RelayCommand]
    private async Task CreateDirectRoomAsync()
    {
        if (!IsIdle) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        var port = int.TryParse(ManualPort.Trim(), out var manual) && manual is > 0 and < 65536 ? manual : SelectedLanGame?.Game.Port;
        if (port is null)
        {
            SetStatus(StatusMessage.Warning("multiplayer.lan.port_required"));
            return;
        }
        var credentials = NewCredentials(port.Value, TunnelSettings.Default);
        var members = new MemberTable();
        var room = credentials.ToHostRoom(members, ChatName);
        var book = new InviteBook { KeepLobbyKey = true };
        var lobbyKey = book.LobbyKeyCopy();
        var bridgeKey = DirectKeys.Bridge(lobbyKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(lobbyKey);
        SetStatus(StatusMessage.Info("multiplayer.direct.gathering_host"));
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), room, true, MakeDirectOptions(), _diag)
        {
            BridgeKey = bridgeKey,
            // 桥钥匙整个房间都不换：没有待用邀请时不接新的直连加入方。
            AcceptNewPeers = () => book.HasPending,
        };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bridgeKey);
        try
        {
            var code = await direct.PrepareAsync();
            AttachDirect(direct, room);
            Mode = "host";
            RoomCodeText = "";
            RestoreChatHistory(room);
            StartHub(room);
            var fallback = direct.Options.FallbackNodes;
            StartHostLobby(credentials, members, book, direct.LobbyUdpPort, () => fallback);
            DirectHint = code.DirectlyReachable
                ? L.T("multiplayer.direct.host_reachable")
                : L.F("multiplayer.direct.host_reply_hint", DirectText.Nat(code.Nat));
            if (await IssueInviteAsync(1, InviteExpiryChoices[0].Lifetime, false) is null) return;
            SetStatus(StatusMessage.Success("multiplayer.direct.created"));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            if (_direct == direct) StopSession();
            else await direct.DisposeAsync();
            SetStatus(StatusMessage.Failure("multiplayer.direct.create_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>房主：贴上朋友的回复码，往他那边打洞。</summary>
    [RelayCommand]
    private async Task AddDirectReplyAsync()
    {
        if (_direct is not { IsHost: true } direct) return;
        if (!DirectCode.TryParse(DirectReplyInput, out var reply, out var error) || !reply.IsReply)
        {
            Status = error ?? L.T("multiplayer.direct.invalid_reply");
            return;
        }
        SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
        try
        {
            SetStatus(await direct.AddReplyAsync(reply) ? StatusMessage.Success("multiplayer.direct.punch_succeeded") : StatusMessage.Error("multiplayer.direct.punch_failed"));
            DirectReplyInput = "";
        }
        catch (DirectFailedException e)
        {
            SetStatus(StatusMessage.Plain(e.Message, StatusKind.Error));
        }
    }

    /// <summary>
    /// 加入方：贴上直连邀请码，生成回复码并开始打洞；打通后经直连桥进大厅敲门，房主批准后拿到房间凭据，
    /// 再在桥上证明自己是批准过的成员，房主才把这条桥接到房间。
    /// </summary>
    [RelayCommand]
    private async Task JoinDirectAsync()
    {
        if (!IsIdle || IsKnocking) return;
        if (RouteMisplacedCode(DirectInviteInput, PastedCodeKind.DirectInvite, () => DirectInviteInput = "")) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        if (!DirectCode.TryParse(DirectInviteInput, out var invite, out var error) || invite.IsReply || invite.Invite is not { } jay)
        {
            if (DirectCode.LooksLikeOldCode(DirectInviteInput)) SetStatus(StatusMessage.Warning("multiplayer.direct.old_code"));
            else Status = error ?? L.T("multiplayer.direct.invalid_invite");
            return;
        }
        if (jay.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
        {
            SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
            return;
        }
        var bridgeKey = DirectKeys.Bridge(jay.LobbyKey);
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), null, false, MakeDirectOptions(), _diag) { BridgeKey = bridgeKey };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bridgeKey);
        // 先订阅再加入：打通后房主可能马上发来测试请求。只认当前这个会话的事件（结束或换了会话就不管）。
        direct.TestRequested += request => Dispatcher.UIThread.Post(() =>
        {
            // 事件排到界面线程时请求可能已经作废或撤回：只显示还在等确认的那个。
            if (_direct != direct || direct.TestConsent.Pending?.Run != request.Run) return;
            ShowTestRequest(request);
        });
        direct.GuestStepStarted += (run, step) => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct || _guestTestRun != run || direct.TestConsent.ActiveRun != run) return;
            OnGuestStep(step);
        });
        direct.TestEnded += run => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct || _guestTestRun != run) return;
            ResetGuestTest();
        });
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.direct.gathering_guest"));
            var code = await direct.PrepareAsync();
            Mode = "join";
            IsDirect = true;
            _direct = direct;
            RoomCodeText = "";
            MyDirectCode = (code with { Session = invite.Session }).Encode();
            DirectCodeShown = false;
            DirectHint = invite.DirectlyReachable
                ? L.T("multiplayer.direct.guest_reachable")
                : L.T("multiplayer.direct.guest_reply_hint");
            SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
            if (!await direct.JoinAsync(invite, (peers, ct) => DirectKnockAsync(jay, peers, ct)))
            {
                // 被拒、过期：原因已经显示了。
                await LeaveFailedDirectAsync(direct);
                return;
            }
            var room = direct.Room;
            AttachDirect(direct, room);
            JoinAddress = FullLanMode ? $"{EasyTierArgs.HostIp}:{room.Port}" : $"127.0.0.1:{direct.LocalGamePort}";
            _localChatPort = FullLanMode ? null : direct.LocalChatPort;
            _localRemotePort = FullLanMode ? null : direct.LocalRemotePort;
            RestoreChatHistory(room);
            ChatStatus = L.T("multiplayer.chat.awaiting_host");
            Status = direct.UsingFallbackNode ? L.T("multiplayer.direct.fallback") : L.T("multiplayer.direct.connected");
            direct.GuestChatProbe = ChatProbeAsync;
            // 万一请求在界面订阅前就到了（或者事件被错过）：按会话里记着的待确认请求补上。
            if (direct.TestConsent.Pending is { } pending && !TestRequestPending) ShowTestRequest(pending);
        }
        catch (TimeoutException e)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException or InvalidDataException or System.Text.Json.JsonException)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(StatusMessage.Failure("multiplayer.direct.connect_failed", e, ErrorText.Of(e)));
        }
    }

    private async Task LeaveFailedDirectAsync(DirectSession direct)
    {
        await direct.DisposeAsync();
        if (_direct != direct) return;
        _direct = null;
        IsDirect = false;
        Mode = "";
        MyDirectCode = "";
        // 打洞期间可能已经收到测试请求：一起收起。
        ResetGuestTest();
    }

    /// <summary>加入方：经直连桥（或者节点）进大厅敲门、等批准。拿到房间凭据返回房间；被拒、过期返回 null（原因已经显示）。</summary>
    private async Task<RoomCode?> DirectKnockAsync(JayInvite invite, IReadOnlyList<string> peers, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsKnocking = true;
            SetStatus(StatusMessage.Info("multiplayer.invite.entering_lobby"));
        });
        try
        {
            var me = Identity.KeyPair;
            var name = ChatName;
            var outcome = await Task.Run(() => LobbyJoin.KnockAsync(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier", "lobby-join"), peers, invite,
                me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line),
                () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Info("multiplayer.invite.waiting_approval"))), ct));
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (outcome.Credentials is not { } credentials)
                {
                    SetStatus(StatusMessage.Warning(outcome.Reason switch
                    {
                        "expired" => "multiplayer.invite.reason.expired",
                        "full" => "multiplayer.invite.reason.full",
                        _ => "multiplayer.invite.reason.denied",
                    }));
                    return null;
                }
                if (credentials.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
                {
                    SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
                    return null;
                }
                HostFingerprint = outcome.HostFingerprint;
                SetStatus(StatusMessage.Info("multiplayer.direct.admitting"));
                return (RoomCode?)credentials.ToRoom();
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsKnocking = false);
        }
    }

    private void ShowTestRequest(TestRequest request)
    {
        ResetGuestTest();
        _guestTestRun = request.Run;
        TestRequestWantsFullLan = request.WantsFullLan;
        TestAllowFullLan = false;
        TestAllowExit = false;
        TestRequestPending = true;
        SetStatus(StatusMessage.Info("multiplayer.matrix.request_status"));
    }

    /// <summary>加入方：测试的一步开始了。完整局域网/出口时显示提示；出口那几步把出口横幅也显示出来（经房主上网）。</summary>
    private void OnGuestStep(MatrixStep step)
    {
        GuestTestRunning = true;
        GuestTestBanner = step.Exit ? L.T("multiplayer.matrix.banner_exit")
            : step.FullLan ? L.T("multiplayer.matrix.banner_full_lan")
            : L.T("multiplayer.matrix.banner_running");
        if (step.Exit)
        {
            _exitBeforeTest ??= ExitNodeIp;
            ExitNodeIp = EasyTierArgs.HostIp;
        }
        else if (_exitBeforeTest is { } before)
        {
            ExitNodeIp = before;
            _exitBeforeTest = null;
        }
    }

    /// <summary>测试结束、会话结束：收起确认卡片和测试横幅，还原出口显示。</summary>
    private void ResetGuestTest()
    {
        _guestTestRun = null;
        TestRequestPending = false;
        GuestTestRunning = false;
        GuestTestBanner = "";
        if (_exitBeforeTest is { } before)
        {
            ExitNodeIp = before;
            _exitBeforeTest = null;
        }
    }

    /// <summary>加入方：停止房主发起的测试，恢复原来的联机设置。</summary>
    [RelayCommand]
    private async Task StopGuestTestAsync()
    {
        if (_direct is not { IsHost: false } direct) return;
        var run = _guestTestRun;
        await direct.StopGuestTestAsync();
        // 同一次结束事件可能已先收起横幅；只有换会话或显示了另一轮时才忽略这次停止结果。
        if (_direct != direct || _guestTestRun is not null && _guestTestRun != run) return;
        ResetGuestTest();
        SetStatus(StatusMessage.Warning("multiplayer.matrix.cancelled"));
    }

    private void AttachDirect(DirectSession direct, RoomCode room)
    {
        _direct = direct;
        IsDirect = true;
        _room = room;
        _session = direct.EasyTier;
        // 直连从组件真正开起来时开始计时（之前在等回复码、打洞）。
        MarkJoinStarted(newRoom: true);
        _sessionCts = new CancellationTokenSource();
        _service.ActiveMultiplayer = new DirectHandle(direct);
        var starts = direct.EasyTierStarts;
        direct.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct) return;
            // 直连会话自己启动或重启了 EasyTier（打不通改用节点、已提权的原地重启）：从这次开始重新计时。
            if (direct.EasyTierStarts != starts)
            {
                starts = direct.EasyTierStarts;
                MarkJoinStarted(newRoom: false);
            }
            _session = direct.EasyTier;
            OnPropertyChanged(nameof(CanUseExit));
            if (direct.GuestIsLegacy && !_legacyGuestWarned)
            {
                _legacyGuestWarned = true;
                SetStatus(StatusMessage.Warning("multiplayer.direct.legacy_guest"));
            }
        });
        if (FullLanMode && direct.EasyTier?.Options is { } o) MyVirtualIp = EasyTierArgs.Build(o)[EasyTierArgs.Build(o).IndexOf("--ipv4") + 1];
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshPeersAsync());
        _timer.Start();
        OnPropertyChanged(nameof(CanUseExit));
    }

    private bool _legacyGuestWarned;

    /// <summary>退出 Amber 时一起关掉。</summary>
    private sealed class DirectHandle(DirectSession direct) : IDisposable
    {
        public void Dispose() => direct.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>刷新时更新“当前路径”。</summary>
    private void TickDirect(IReadOnlyList<PeerInfo> peers)
    {
        if (_direct is not { } direct) return;
        PathText = direct.DescribePath(peers);
    }

    private async Task<bool?> ChatProbeAsync(MatrixStep step, CancellationToken ct)
    {
        for (var i = 0; i < 30 && !ChatReady; i++) await Task.Delay(500, ct);
        if (_chat is not { } chat) return false;
        var text = L.F("multiplayer.matrix.chat_probe", step.Index);
        await chat.SendAsync(text, ct);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250, ct);
            if (await Dispatcher.UIThread.InvokeAsync(() => ChatLines.Any(l => l.Message.Text == text))) return true;
        }
        return false;
    }

    /// <summary>全条件测试：房主点，双方一起按顺序切换每种组合。</summary>
    [RelayCommand]
    private async Task RunMatrixAsync()
    {
        if (_direct is not { IsHost: true } direct || MatrixRunning) return;
        if (direct.Bridge?.Peers.Count is not > 0)
        {
            SetStatus(StatusMessage.Warning("multiplayer.matrix.peer_required"));
            return;
        }
        MatrixRunning = true;
        MatrixLines.Clear();
        _matrixCts = new CancellationTokenSource();
        var results = new List<MatrixResult>();
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.matrix.waiting_guest"));
            var progress = new Progress<MatrixResult>(r =>
            {
                results.Add(r);
                MatrixLines.Clear();
                foreach (var line in TestMatrix.FormatTable(results)) MatrixLines.Add(line);
                SetStatus(StatusMessage.Info("multiplayer.matrix.progress", r.Step.Index, (r.Passed switch { true => L.T("common.passed"), false => L.T("common.failed"), _ => L.T("common.skipped") })));
            });
            _lastMatrix = await direct.RunMatrixAsync(MatrixFullLan, progress, null, _matrixCts.Token);
            MatrixLines.Clear();
            foreach (var line in TestMatrix.FormatTable(_lastMatrix)) MatrixLines.Add(line);
            SetStatus(StatusMessage.Success("multiplayer.matrix.completed", _lastMatrix.Count(r => r.Passed == true), _lastMatrix.Count));
        }
        catch (OperationCanceledException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.matrix.cancelled"));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.matrix.failed", e, ErrorText.Of(e)));
        }
        finally
        {
            MatrixRunning = false;
        }
    }

    [RelayCommand]
    private void CancelMatrix() => _matrixCts?.Cancel();

    /// <summary>加入方：同意房主的全条件测试（只同意勾上的部分）。</summary>
    [RelayCommand]
    private Task AcceptTestRequestAsync() => AnswerTestRequestAsync(true);

    [RelayCommand]
    private Task DeclineTestRequestAsync() => AnswerTestRequestAsync(false);

    private async Task AnswerTestRequestAsync(bool accept)
    {
        if (_direct is not { IsHost: false } direct || !TestRequestPending) return;
        var run = _guestTestRun;
        TestRequestPending = false;
        var accepted = await direct.AnswerTestRequestAsync(accept, TestRequestWantsFullLan && TestAllowFullLan, TestRequestWantsFullLan && TestAllowFullLan && TestAllowExit);
        if (_direct != direct || _guestTestRun != run) return;
        if (accepted && direct.TestConsent.ActiveRun == run)
        {
            // 同意后马上显示横幅和“停止测试”（不等第一步到）。
            GuestTestRunning = true;
            GuestTestBanner = L.T("multiplayer.matrix.banner_running");
            SetStatus(StatusMessage.Info("multiplayer.matrix.running"));
        }
        else
        {
            ResetGuestTest();
            SetStatus(StatusMessage.Warning(accept ? "multiplayer.matrix.request_gone" : "multiplayer.matrix.cancelled"));
        }
    }

    [RelayCommand]
    private async Task CopyDirectCodeAsync()
    {
        if (await SecretClipboard.CopyAsync(MyDirectCode))
            SetStatus(StatusMessage.Success("multiplayer.clipboard.copied_autoclear"));
    }

    /// <summary>导出诊断报告：去掉房间密钥和各种码；默认遮住公网 IP。</summary>
    public void ExportDiagnostics(string path)
    {
        var table = _lastMatrix.Count > 0 ? TestMatrix.FormatTable(_lastMatrix) : MatrixLines.Count > 0 ? MatrixLines.ToList() : null;
        var secrets = _room is { } r ? r.Secrets : [];
        var extra = new List<DiagnosticEntry>();
        if (_session is { } s) extra.AddRange(s.RecentOutput.Select(l => new DiagnosticEntry(DateTime.Now, L.T("multiplayer.diagnostics.recent_output"), l)));
        var report = NetDiagnostics.BuildReport(Amber.Core.Launch.UpdateChecker.CurrentVersion, _diag.Snapshot().Concat(extra), table, secrets, IncludePublicIps);
        File.WriteAllText(path, report);
        SetStatus(StatusMessage.Success("multiplayer.diagnostics.exported", path));
    }

    private async Task StopDirectAsync()
    {
        _matrixCts?.Cancel();
        // 会话结束：确认卡片、测试横幅跟着收起（之后旧会话的事件不再理会）。
        ResetGuestTest();
        _legacyGuestWarned = false;
        if (_direct is { } direct)
        {
            _direct = null;
            await direct.DisposeAsync();
        }
        IsDirect = false;
        MyDirectCode = "";
        DirectCodeShown = false;
        DirectHint = "";
        PathText = "";
    }
}
