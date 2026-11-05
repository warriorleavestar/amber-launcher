using Amber.Core.Localization;
using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Multiplayer;
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
    [ObservableProperty] public partial string MyDirectCode { get; set; } = "";
    [ObservableProperty] public partial string DirectHint { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentPathText))] public partial string PathText { get; set; } = "";
    public string CurrentPathText => L.F("multiplayer.direct.current_path", PathText);
    [ObservableProperty] public partial string NetworkCheckText { get; set; } = "";
    [ObservableProperty] public partial bool IsCheckingNetwork { get; set; }
    [ObservableProperty] public partial bool MatrixRunning { get; set; }
    [ObservableProperty] public partial bool MatrixFullLan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectHost))]
    public partial bool IsDirect { get; set; }

    public bool IsDirectHost => IsDirect && IsHosting;

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

    /// <summary>房主：创建直连房间，生成邀请码。</summary>
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
        var room = RoomCode.Create(port.Value);
        SetStatus(StatusMessage.Info("multiplayer.direct.gathering_host"));
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), room, true, MakeDirectOptions(), _diag);
        try
        {
            var code = await direct.PrepareAsync();
            AttachDirect(direct, room);
            Mode = "host";
            RoomCodeText = room.ToString();
            MyDirectCode = code.Encode();
            DirectHint = code.DirectlyReachable
                ? L.T("multiplayer.direct.host_reachable")
                : L.F("multiplayer.direct.host_reply_hint", DirectText.Nat(code.Nat));
            StartHub(room);
            SetStatus(StatusMessage.Success("multiplayer.direct.created"));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await direct.DisposeAsync();
            SetStatus(StatusMessage.Failure("multiplayer.direct.create_failed", e, ErrorText.Describe(e)));
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

    /// <summary>加入方：贴上邀请码，生成回复码并开始打洞。</summary>
    [RelayCommand]
    private async Task JoinDirectAsync()
    {
        if (!IsIdle) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        if (!DirectCode.TryParse(DirectInviteInput, out var invite, out var error) || invite.IsReply)
        {
            Status = error ?? L.T("multiplayer.direct.invalid_invite");
            return;
        }
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), invite.Room, false, MakeDirectOptions(), _diag);
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.direct.gathering_guest"));
            var code = await direct.PrepareAsync();
            Mode = "join";
            IsDirect = true;
            _direct = direct;
            _room = invite.Room;
            RoomCodeText = invite.Room.ToString();
            MyDirectCode = (code with { Session = invite.Session }).Encode();
            DirectHint = invite.DirectlyReachable
                ? L.T("multiplayer.direct.guest_reachable")
                : L.T("multiplayer.direct.guest_reply_hint");
            SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
            await direct.JoinAsync(invite);
            AttachDirect(direct, invite.Room);
            JoinAddress = FullLanMode ? $"{EasyTierArgs.HostIp}:{invite.Room.Port}" : $"127.0.0.1:{direct.LocalGamePort}";
            _localChatPort = FullLanMode ? null : direct.LocalChatPort;
            RestoreChatHistory(invite.Room);
            ChatStatus = L.T("multiplayer.chat.awaiting_host");
            Status = direct.UsingFallbackNode ? L.T("multiplayer.direct.fallback") : L.T("multiplayer.direct.connected");
            direct.GuestChatProbe = ChatProbeAsync;
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await direct.DisposeAsync();
            _direct = null;
            IsDirect = false;
            Mode = "";
            SetStatus(StatusMessage.Failure("multiplayer.direct.connect_failed", e, ErrorText.Describe(e)));
        }
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
        direct.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _session = direct.EasyTier;
            OnPropertyChanged(nameof(CanUseExit));
        });
        if (FullLanMode && direct.EasyTier?.Options is { } o) MyVirtualIp = EasyTierArgs.Build(o)[EasyTierArgs.Build(o).IndexOf("--ipv4") + 1];
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshPeersAsync());
        _timer.Start();
        OnPropertyChanged(nameof(CanUseExit));
    }

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
            Status = MatrixFullLan ? L.T("multiplayer.matrix.requesting_admin") : L.T("multiplayer.matrix.running");
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
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.matrix.failed", e, ErrorText.Describe(e)));
        }
        finally
        {
            MatrixRunning = false;
        }
    }

    [RelayCommand]
    private void CancelMatrix() => _matrixCts?.Cancel();

    [RelayCommand]
    private async Task CopyDirectCodeAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, MyDirectCode);
            SetStatus(StatusMessage.Success("common.copied"));
        }
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
        if (_direct is { } direct)
        {
            _direct = null;
            await direct.DisposeAsync();
        }
        IsDirect = false;
        MyDirectCode = "";
        DirectHint = "";
        PathText = "";
    }
}
