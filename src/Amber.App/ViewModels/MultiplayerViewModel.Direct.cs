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
    [ObservableProperty] public partial string PathText { get; set; } = "";
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
        StunText.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

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
        NetworkCheckText = "正在检测……";
        try
        {
            using var socket = DirectBridge.CreateSocket();
            var lines = new List<string>();
            var gathered = await DirectGatherer.GatherAsync(socket, StunServers(), true, l => { lines.Add(l); _diag.Add("检测", l); });
            if (gathered.Mapping?.Remove is { } remove)
            {
                try { await remove(); } catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException) { }
            }
            var v6 = gathered.Candidates.Any(c => c.Kind == PathKind.IPv6);
            var verdict = gathered.Nat switch
            {
                NatKind.Open => "很好连：你能被直接连上，朋友不用发回复码",
                NatKind.Cone => "容易打洞：和大多数网络都能直连",
                NatKind.Symmetric => v6 ? "IPv4 是严格 NAT，但你有 IPv6，对方也有 IPv6 时能直连" : "不容易打洞：严格 NAT，也没有 IPv6；对方是普通家庭宽带时还有机会，双方都严格时需要联机节点",
                _ => "查不出来：STUN 没回应，可能 UDP 被拦了",
            };
            NetworkCheckText = $"{verdict}\n{string.Join("\n", lines)}";
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            NetworkCheckText = "检测失败：" + e.Message;
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
            Status = "先下载联机组件";
            return;
        }
        var port = int.TryParse(ManualPort.Trim(), out var manual) && manual is > 0 and < 65536 ? manual : SelectedLanGame?.Game.Port;
        if (port is null)
        {
            Status = "先在游戏里“对局域网开放”，或手动填写端口";
            return;
        }
        var room = RoomCode.Create(port.Value);
        Status = "正在收集能连到你的地址（IPv6、STUN、路由器端口映射）……";
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), room, true, MakeDirectOptions(), _diag);
        try
        {
            var code = await direct.PrepareAsync();
            AttachDirect(direct, room);
            Mode = "host";
            RoomCodeText = room.ToString();
            MyDirectCode = code.Encode();
            DirectHint = code.DirectlyReachable
                ? "你能被直接连上：把邀请码发给朋友就行，不用等回复码。"
                : $"你的网络：{DirectText.Nat(code.Nat)}。把邀请码发给朋友，等他发回回复码，贴到下面点“连接”。";
            StartHub(room);
            Status = "直连房间已创建";
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await direct.DisposeAsync();
            Status = "直连房间没有创建：" + e.Message;
        }
    }

    /// <summary>房主：贴上朋友的回复码，往他那边打洞。</summary>
    [RelayCommand]
    private async Task AddDirectReplyAsync()
    {
        if (_direct is not { IsHost: true } direct) return;
        if (!DirectCode.TryParse(DirectReplyInput, out var reply, out var error) || !reply.IsReply)
        {
            Status = error ?? "这不是回复码（回复码以 AMBREPLY 开头）";
            return;
        }
        Status = "正在打洞……";
        try
        {
            Status = await direct.AddReplyAsync(reply) ? "打通了" : "没打通，详细原因看调试日志";
            DirectReplyInput = "";
        }
        catch (DirectFailedException e)
        {
            Status = e.Message;
        }
    }

    /// <summary>加入方：贴上邀请码，生成回复码并开始打洞。</summary>
    [RelayCommand]
    private async Task JoinDirectAsync()
    {
        if (!IsIdle) return;
        if (!IsComponentReady)
        {
            Status = "先下载联机组件";
            return;
        }
        if (!DirectCode.TryParse(DirectInviteInput, out var invite, out var error) || invite.IsReply)
        {
            Status = error ?? "这不是邀请码（邀请码以 AMBDIRECT 开头）";
            return;
        }
        var direct = new DirectSession(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier"), invite.Room, false, MakeDirectOptions(), _diag);
        try
        {
            Status = "正在收集能连到你的地址……";
            var code = await direct.PrepareAsync();
            Mode = "join";
            IsDirect = true;
            _direct = direct;
            _room = invite.Room;
            RoomCodeText = invite.Room.ToString();
            MyDirectCode = (code with { Session = invite.Session }).Encode();
            DirectHint = invite.DirectlyReachable
                ? "房主能被直接连上，正在连接，不用发回复码。"
                : "把上面的回复码发给房主，房主贴上后双方同时打洞。最多等 90 秒。";
            Status = "正在打洞……";
            await direct.JoinAsync(invite);
            AttachDirect(direct, invite.Room);
            JoinAddress = FullLanMode ? $"{EasyTierArgs.HostIp}:{invite.Room.Port}" : $"127.0.0.1:{direct.LocalGamePort}";
            _localChatPort = FullLanMode ? null : direct.LocalChatPort;
            RestoreChatHistory(invite.Room);
            ChatStatus = "连上房主后自动进入聊天";
            Status = direct.UsingFallbackNode ? "直连没打通，已改用联机节点" : "已直连";
            direct.GuestChatProbe = ChatProbeAsync;
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await direct.DisposeAsync();
            _direct = null;
            IsDirect = false;
            Mode = "";
            Status = "直连失败：" + e.Message;
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
        var text = $"[自动测试 #{step.Index}]";
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
            Status = "要先有朋友通过直连加入，才能做全条件测试";
            return;
        }
        MatrixRunning = true;
        MatrixLines.Clear();
        _matrixCts = new CancellationTokenSource();
        var results = new List<MatrixResult>();
        try
        {
            Status = MatrixFullLan ? "全条件测试：先请双方确认管理员权限（只问一次）……" : "全条件测试进行中……";
            var progress = new Progress<MatrixResult>(r =>
            {
                results.Add(r);
                MatrixLines.Clear();
                foreach (var line in TestMatrix.FormatTable(results)) MatrixLines.Add(line);
                Status = $"全条件测试：第 {r.Step.Index} 步 {(r.Passed switch { true => "通过", false => "失败", _ => "跳过" })}";
            });
            _lastMatrix = await direct.RunMatrixAsync(MatrixFullLan, progress, null, _matrixCts.Token);
            MatrixLines.Clear();
            foreach (var line in TestMatrix.FormatTable(_lastMatrix)) MatrixLines.Add(line);
            Status = $"全条件测试完成：{_lastMatrix.Count(r => r.Passed == true)}/{_lastMatrix.Count} 通过。可以点“导出诊断报告”发给别人看。";
        }
        catch (OperationCanceledException)
        {
            Status = "全条件测试已取消";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            Status = "全条件测试中断：" + e.Message;
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
            Status = "已复制";
        }
    }

    /// <summary>导出诊断报告：去掉房间密钥和各种码；默认遮住公网 IP。</summary>
    public void ExportDiagnostics(string path)
    {
        var table = _lastMatrix.Count > 0 ? TestMatrix.FormatTable(_lastMatrix) : MatrixLines.Count > 0 ? MatrixLines.ToList() : null;
        var secrets = _room is { } r ? r.Secrets : [];
        var extra = new List<DiagnosticEntry>();
        if (_session is { } s) extra.AddRange(s.RecentOutput.Select(l => new DiagnosticEntry(DateTime.Now, "EasyTier 最近输出", l)));
        var report = NetDiagnostics.BuildReport(Amber.Core.Launch.UpdateChecker.CurrentVersion, _diag.Snapshot().Concat(extra), table, secrets, IncludePublicIps);
        File.WriteAllText(path, report);
        Status = "诊断报告已导出：" + path;
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
