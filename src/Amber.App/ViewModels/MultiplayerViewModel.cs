using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Multiplayer;
using Amber.Core.Net;
using Amber.Core;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed record LanGameItem(LanGame Game, string Source) : LocalizedRecord
{
    public string Label => L.F("multiplayer.lan.game_label", Game.Motd, Game.Port, Source);
}

public sealed partial class NodeItem(string url) : ObservableObject
{
    public string Url { get; } = url;

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "";
}

public sealed record ChatLine(ChatMessage Message, bool Mine) : LocalizedRecord
{
    /// <summary>恢复的历史和新消息之间的分隔行。</summary>
    public bool IsSeparator { get; init; }
    public string Time => Message.Time.ToLocalTime().ToString("HH:mm");
    public string Name => Mine ? L.T("common.me") : Message.Name;
    public bool IsNotice => Message.Kind != ChatKind.Message || IsSeparator;
    public string Text => Message.Kind switch
    {
        ChatKind.Join => L.F("multiplayer.chat.member_joined", Message.Name),
        ChatKind.Leave => L.F("multiplayer.chat.member_left", Message.Name),
        _ => Message.Text,
    };
}

/// <summary>联机页的数据封装选项。</summary>
public sealed record TransportChoice(TransportMode Mode) : LocalizedRecord
{
    public string Label => L.T(Mode switch
    {
        TransportMode.Minecraft => "multiplayer.transport.mode.minecraft",
        TransportMode.High => "multiplayer.transport.mode.high",
        TransportMode.Extreme => "multiplayer.transport.mode.extreme",
        _ => "multiplayer.transport.mode.general",
    });
    public string Detail => TransportProfile.Describe(Mode);

    /// <summary>一句话说清这种模式下谁看得到游戏流量。</summary>
    public string Privacy => L.T(Mode switch
    {
        TransportMode.General => "multiplayer.transport.privacy.general",
        _ => "multiplayer.transport.privacy.member_channel",
    });
    public override string ToString() => Label;
}

public sealed record PeerItem(PeerInfo Peer) : LocalizedRecord
{
    public string Name => Peer.Cost == "Local" ? L.T("common.me") : Peer.Hostname switch
    {
        "amber-host" => L.T("multiplayer.peers.host"),
        "amber-guest" => L.T("multiplayer.peers.player"),
        "" => L.T("multiplayer.peers.node"),
        var h => h,
    };
    public string Meta => string.Join(" · ", new[]
    {
        Peer.Ip,
        Peer.LatencyMs is { } ms ? $"{ms:F0} ms" : null,
        Peer.Cost switch { "Local" => null, var c when c.Contains("p2p", StringComparison.OrdinalIgnoreCase) => L.T("multiplayer.peers.direct"), "" => null, _ => L.T("multiplayer.peers.relay") },
        Peer.Tunnel is "-" ? null : Peer.Tunnel,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// 联机页：用 EasyTier 把“对局域网开放”的游戏分享给不在同一个局域网的朋友。
/// 房主选一局开放到局域网的游戏，生成房间码；加入方输入房间码，本机会出现一个转发端口，游戏里直接连。
/// </summary>
public sealed partial class MultiplayerViewModel : PageViewModel
{
    private readonly LauncherService _service;
    private IEasyTierSession? _session;
    private CancellationTokenSource? _sessionCts;
    private readonly SemaphoreSlim _exitSwitchLock = new(1, 1);
    private CancellationTokenSource? _downloadCts;
    private DispatcherTimer? _timer;
    private ChatHub? _hub;
    private ChatClient? _chat;
    private bool _chatConnecting;
    private RoomCode? _room;

    public override string Title => L.T("multiplayer.title");
    public override string Icon => "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zm0 2c.9 0 2.2 1.900 2.700 5H9.300C9.800 6.900 11.100 5 12 5zM5.100 13h2.900c.1 1.600.4 3 .9 4.200A7 7 0 0 1 5.100 13zm0-2a7 7 0 0 1 3.800-4.200c-.5 1.200-.8 2.600-.9 4.200zm4.200 2h5.400c-.4 3.100-1.800 6-2.700 6s-2.300-2.900-2.700-6zm5.800 4.200c.5-1.200.8-2.600.9-4.200h2.900a7 7 0 0 1-3.800 4.200zM16 11c-.1-1.600-.4-3-.9-4.200A7 7 0 0 1 18.900 11z";

    public ObservableCollection<LanGameItem> LanGames { get; } = [];
    public ObservableCollection<PeerItem> Peers { get; } = [];
    public ObservableCollection<string> Instances { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ComponentText))] public partial bool IsComponentReady { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadPercent { get; set; }

    [ObservableProperty] public partial LanGameItem? SelectedLanGame { get; set; }
    [ObservableProperty] public partial string ManualPort { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }

    [ObservableProperty] public partial string JoinCode { get; set; } = "";

    /// <summary>完整局域网模式：建虚拟网卡，每人一个虚拟 IP，要管理员权限。默认关闭。</summary>
    [ObservableProperty] public partial bool FullLanMode { get; set; }

    public bool CanUseFullLan => PlatformFeatures.FullLan;
    public string FullLanUnavailableText => CanUseFullLan ? "" : L.T("core.mp.component.windowsonly");

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MyVirtualIpText))] public partial string MyVirtualIp { get; set; } = "";

    public string MyVirtualIpText => L.F("multiplayer.room.virtual_ip", MyVirtualIp);

    // ---- 出口节点（只在完整局域网模式下）----

    /// <summary>允许房间成员经我的电脑上网。默认关闭，开房间或加入前勾选。</summary>
    [ObservableProperty] public partial bool AllowExitNode { get; set; }

    /// <summary>当前经哪个成员上网（虚拟 IP），空表示正常上网。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsingExit), nameof(ExitBanner))]
    public partial string ExitNodeIp { get; set; } = "";

    [ObservableProperty] public partial PeerItem? SelectedExitPeer { get; set; }

    public bool UsingExit => ExitNodeIp.Length > 0;
    public bool CanUseExit => _session?.Options.Tun == true;
    public string ExitBanner
    {
        get
        {
            if (!UsingExit) return "";
            var exit = Peers.FirstOrDefault(p => p.Peer.Ip == ExitNodeIp);
            var route = exit is null ? "" : exit.Peer.Cost.Contains("p2p", StringComparison.OrdinalIgnoreCase) ? L.T("multiplayer.exit.direct_route") : L.T("multiplayer.exit.relay_route");
            return L.F("multiplayer.exit.banner", ExitNodeIp, route);
        }
    }
    public ObservableCollection<PeerItem> ExitCandidates { get; } = [];

    // ---- 数据封装和“仅游戏”转发 ----


    /// <summary>极端加密要系统支持 ML-KEM（较新的 Windows 11）；不支持时不列出来。</summary>
    public IReadOnlyList<TransportChoice> TransportChoices { get; } =
    [
        new(TransportMode.General),
        new(TransportMode.Minecraft),
        new(TransportMode.High),
        .. NoiseHandshake.HybridSupported ? [new TransportChoice(TransportMode.Extreme)] : Array.Empty<TransportChoice>(),
    ];

    /// <summary>开房间时用的封装方式；加入时跟着房间码走。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDisguise))]
    public partial TransportChoice SelectedTransport { get; set; }

    [ObservableProperty] public partial bool DisguiseAsMinecraft { get; set; }
    public bool CanDisguise => SelectedTransport.Mode != TransportMode.General;

    /// <summary>允许房间成员经我的电脑转发游戏流量（只开一个转发服务，不建网卡、不需要管理员权限）。</summary>
    [ObservableProperty] public partial bool AllowGameProxy { get; set; }

    /// <summary>当前“仅游戏”转发经过的成员（虚拟 IP），空表示没开。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsingGameProxy), nameof(GameProxyBanner), nameof(CanUseGameProxy))]
    public partial string GameProxyIp { get; set; } = "";

    public bool UsingGameProxy => GameProxyIp.Length > 0;
    public string GameProxyBanner => UsingGameProxy
        ? L.F("multiplayer.proxy.banner", GameProxyIp)
        : "";

    /// <summary>允许别人经自己转发、但查不到自己公网 IP 时的提示（这时不转发）。</summary>
    [ObservableProperty] public partial string ExitWarning { get; set; } = "";

    /// <summary>这个房间实际用的封装方式（显示用）。</summary>
    [ObservableProperty] public partial string TransportText { get; set; } = "";

    private RoomTransport? _transport;
    private DateTime _joinedAt;
    /// <summary>这次加入之后连上过房主没有：连上过就不再提示“还没连上房主，检查房间码”。</summary>
    private bool _hostSeen;

    /// <summary>刚加入或者刚重启了联机组件：从现在开始计时，20 秒还没连上房主才提示。</summary>
    private void MarkJoinStarted(bool newRoom)
    {
        _joinedAt = DateTime.UtcNow;
        if (newRoom) _hostSeen = false;
    }
    /// <summary>当前会话实际用的参数（完整局域网模式下换转发不重启 EasyTier，参数记在这里）。</summary>
    private EasyTierOptions? _currentOptions;

    // ---- 聊天 ----
    public ObservableCollection<ChatLine> ChatLines { get; } = [];
    [ObservableProperty] public partial string ChatInput { get; set; } = "";
    [ObservableProperty] public partial string ChatStatus { get; set; } = "";
    [ObservableProperty] public partial bool ChatReady { get; set; }
    private int? _localChatPort;

    /// <summary>每个房间的聊天记录只放在内存里（最近 5 个房间、每个 500 条），关掉 Amber 就没了，从不写磁盘。</summary>
    private readonly ChatHistory<ChatLine> _chatHistory = new();

    private void SaveChatHistory(RoomCode room) =>
        _chatHistory.Save(room, ChatLines.Where(l => !l.IsSeparator));

    private void RestoreChatHistory(RoomCode room)
    {
        ChatLines.Clear();
        var lines = _chatHistory.Restore(room);
        if (lines.Count == 0) return;
        foreach (var line in lines) ChatLines.Add(line);
        ChatLines.Add(new ChatLine(new ChatMessage(ChatKind.Message, "", "", L.T("multiplayer.chat.history_separator"), DateTimeOffset.Now), false) { IsSeparator = true });
    }
    [ObservableProperty] public partial string? TargetInstance { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsHosting), nameof(IsJoined), nameof(IsDirectHost))]
    public partial string Mode { get; set; } = "";

    public bool IsIdle => Mode == "";
    public bool IsHosting => Mode == "host";
    public bool IsJoined => Mode == "join";

    [ObservableProperty] public partial string RoomCodeText { get; set; } = "";
    [ObservableProperty] public partial string JoinAddress { get; set; } = "";

    public string ComponentText => IsComponentReady
        ? L.F("multiplayer.component.ready", EasyTierRelease.Version)
        : L.F("multiplayer.component.required", EasyTierRelease.Version, EasyTierRelease.Current.Size / 1024 / 1024);

    public string LicenseText =>
        L.F("multiplayer.component.license", EasyTierRelease.License, EasyTierRelease.ProjectUrl);

    public MultiplayerViewModel(LauncherService service)
    {
        _service = service;
        SelectedTransport = TransportChoices[0];
        service.GameOutput += OnGameOutput;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_service.CurrentInstance is { } id && Instances.Contains(id)) TargetInstance = id;
        });
        InitDirect();
        InitGuide();
        JayServers = new JayServersViewModel(service, () => TargetInstance, () => NavigateHome?.Invoke(), () => Identity);
    }

    /// <summary>Jay 服务器（经网关进的专用服务器）。</summary>
    public JayServersViewModel JayServers { get; }

    protected override void OnFirstActivated()
    {
        IsComponentReady = _service.EasyTier.IsInstalled;
        // 发布包自带安装包时直接装好，不用联网，也不用用户点。
        if (!IsComponentReady && File.Exists(_service.EasyTier.BundledPath)) _ = DownloadComponentAsync();
        LoadNodes();
        RefreshInstances();
        RefreshTrusted();
        JayServers.Load();
        // 不自动听局域网广播：绑定端口可能让 Windows 弹防火墙询问。用 Amber 启动的游戏从日志就能认出端口。
    }

    partial void OnTargetInstanceChanged(string? value) => _service.SetCurrentInstance(value);

    private void RefreshInstances()
    {
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        TargetInstance = _service.CurrentOr(Instances, Instances.FirstOrDefault());
    }

    private void OnGameOutput(string versionId, string line)
    {
        if (LanGames_PortFromLog(line) is not { } port) return;
        Dispatcher.UIThread.Post(() =>
        {
            var item = new LanGameItem(new LanGame(versionId, port), L.T("multiplayer.lan.amber_source"));
            LanGames.Insert(0, item);
            SelectedLanGame = item;
            if (IsIdle) SetStatus(StatusMessage.Success("multiplayer.lan.opened", versionId, port));
        });
    }

    private static int? LanGames_PortFromLog(string line) => Amber.Core.Multiplayer.LanGames.PortFromLog(line);

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var found = await Amber.Core.Multiplayer.LanGames.ListenAsync(TimeSpan.FromSeconds(3));
            foreach (var game in found)
            {
                if (LanGames.Any(g => g.Game.Port == game.Port)) continue;
                LanGames.Add(new LanGameItem(game, L.T("multiplayer.lan.broadcast_source")));
            }
            SelectedLanGame ??= LanGames.FirstOrDefault();
            if (IsIdle && LanGames.Count == 0) SetStatus(StatusMessage.Error("multiplayer.lan.not_found"));
        }
        catch (SocketException e)
        {
            SetStatus(StatusMessage.Failure("multiplayer.lan.scan_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task DownloadComponentAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.component.downloading"));
            string? source = null;
            await _service.EasyTier.EnsureAsync(new Progress<DownloadProgress>(p =>
            {
                if (p.CurrentFile is { } name && name != source)
                {
                    source = name;
                    SetStatus(StatusMessage.Info("multiplayer.component.downloading_from", name));
                }
                DownloadPercent = Math.Min(100, 100.0 * p.BytesDownloaded / EasyTierRelease.Current.Size);
            }), _downloadCts.Token);
            IsComponentReady = true;
            SetStatus(StatusMessage.Success("multiplayer.component.downloaded"));
        }
        catch (OperationCanceledException) when (_downloadCts.IsCancellationRequested)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_cancelled"));
        }
        catch (Exception e)
        {
            SetStatus(StatusMessage.Failure("multiplayer.component.download_failed", e, e is EasyTierInstallException ? e.Message : EasyTierInstaller.Describe(e)));
        }
        finally
        {
            IsDownloading = false;
            DownloadPercent = 0;
        }
    }

    /// <summary>手动导入用户自己下载的官方安装包；大小和 SHA-256 不对就拒绝。</summary>
    public async Task ImportComponentAsync(string path)
    {
        if (IsDownloading) return;
        IsDownloading = true;
        try
        {
            await _service.EasyTier.ImportAsync(path);
            IsComponentReady = true;
            SetStatus(StatusMessage.Success("multiplayer.component.imported"));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.component.import_failed", e, e.Message));
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public string ComponentFileName => _service.EasyTier.ExpectedFileName;

    public string ManualHintText => L.F("multiplayer.component.manual_hint", EasyTierRelease.Current.Name, EasyTierRelease.Current.Url);

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    // ---- 联机节点 ----

    public ObservableCollection<NodeItem> Nodes { get; } = [];
    [ObservableProperty] public partial string NewNodeUrl { get; set; } = "";
    [ObservableProperty] public partial string ShareCodeText { get; set; } = "";

    private void LoadNodes()
    {
        Nodes.Clear();
        foreach (var url in _service.MultiplayerPeers) Nodes.Add(new NodeItem(url));
        NodesExpanded = Nodes.Count == 0;
    }

    private void SaveNodes()
    {
        var list = Nodes.Select(n => n.Url).ToList();
        _service.Settings.MultiplayerPeers = list.Count == 0 ? null : list;
        _service.Settings.Save();
    }

    [RelayCommand]
    private void AddNode()
    {
        var url = NewNodeUrl.Trim();
        if (!RelayNodes.IsValid(url))
        {
            SetStatus(StatusMessage.Error("multiplayer.nodes.invalid_address", url));
            return;
        }
        if (Nodes.Any(n => n.Url == url)) return;
        Nodes.Add(new NodeItem(url));
        SaveNodes();
        NewNodeUrl = "";
        SetStatus(StatusMessage.Success("multiplayer.nodes.added"));
    }

    [RelayCommand]
    private void RemoveNode(NodeItem? item)
    {
        if (item is null) return;
        var index = Nodes.IndexOf(item);
        Nodes.Remove(item);
        SaveNodes();
        Toasts.Removed(item.Url, () =>
        {
            if (Nodes.Any(n => n.Url == item.Url)) return;
            Nodes.Insert(Math.Clamp(index, 0, Nodes.Count), item);
            SaveNodes();
        });
    }


    [RelayCommand]
    private async Task TestNodesAsync()
    {
        foreach (var node in Nodes) node.LatencyText = L.T("multiplayer.nodes.testing");
        await Task.WhenAll(Nodes.ToList().Select(async node =>
        {
            var (ms, error) = await RelayNodes.ProbeAsync(node.Url);
            node.LatencyText = ms is { } v ? $"{v:F0} ms" : error ?? L.T("common.failed");
        }));
    }

    [RelayCommand]
    private async Task ExportNodesAsync()
    {
        ShareCodeText = RelayNodes.Encode(null, Nodes.Select(n => n.Url));
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await clipboard.SetTextAsync(ShareCodeText);
        SetStatus(StatusMessage.Success("multiplayer.nodes.exported"));
    }

    [RelayCommand]
    private void ImportNodes()
    {
        if (!RelayNodes.TryDecode(ShareCodeText, out var name, out var urls))
        {
            SetStatus(StatusMessage.Error("multiplayer.nodes.invalid_share_code"));
            return;
        }
        var added = 0;
        foreach (var url in urls.Where(u => Nodes.All(n => n.Url != u)))
        {
            Nodes.Add(new NodeItem(url));
            added++;
        }
        SaveNodes();
        ShareCodeText = "";
        Status = name is null ? L.P("multiplayer.nodes.imported", added, added, "") : L.P("multiplayer.nodes.imported_named", added, added, name);
    }

    /// <summary>自检用：同一个程序里再开一个联机页（扮演加入方）。</summary>
    internal MultiplayerViewModel CreateTestPeer() => new(_service);

    /// <summary>测试用：这个页面自己的“仅游戏”转发（测试里两个页面共用一个 LauncherService）。</summary>
    internal GameProxy? TestGameProxy => _transport?.Proxy;

    /// <summary>测试用：额外的监听地址和对端（两个实例在同一台电脑上直接互连，不经过公共节点）。</summary>
    internal IReadOnlyList<string>? TestListeners { get; set; }
    internal IReadOnlyList<string>? TestPeers { get; set; }

    [RelayCommand]
    private async Task CreateRoomAsync()
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
        if (!await CanConnectAsync(port.Value))
        {
            SetStatus(StatusMessage.Error("multiplayer.lan.port_unavailable", port));
            return;
        }
        if ((TestPeers ?? _service.MultiplayerPeers).Count == 0 && TestListeners is null)
        {
            SetStatus(StatusMessage.Warning("multiplayer.nodes.host_required"));
            return;
        }
        // 房间的网络名和密钥随机生成，只在批准加入时经加密的敲门通道发给对方；没有可以直接分享的房间码，靠“邀请”。
        var credentials = NewCredentials(port.Value, new TunnelSettings(SelectedTransport.Mode, CanDisguise && DisguiseAsMinecraft));
        // 成员表：批准一个人时发给他自己的钥匙（游戏通道和聊天按它认人）；房主自己的聊天客户端也有一把。
        var members = new MemberTable();
        var room = credentials.ToHostRoom(members, ChatName);
        if (!TryStartSession(new EasyTierOptions(room, true, TestPeers ?? _service.MultiplayerPeers, EasyTierArgs.FreeTcpPort(), null, TestListeners, Guid.NewGuid().ToString(), FullLanMode, null, FullLanMode && AllowExitNode)
            {
                AllowProxy = AllowGameProxy,
            })) return;
        Mode = "host";
        RoomCodeText = "";
        _room = room;
        RestoreChatHistory(room);
        StartHub(room);
        StartHostLobby(credentials, members);
        Status = FullLanMode
            ? L.T("multiplayer.room.created_full_lan")
            : L.T("multiplayer.room.created");
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (!IsIdle || IsKnocking) return;
        if (RouteMisplacedCode(JoinCode, PastedCodeKind.Invite, () => JoinCode = "")) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        if (!JayInvite.TryParse(JoinCode, out var invite))
        {
            SetStatus(JayInvite.LooksLikeOldCode(JoinCode) ? StatusMessage.Warning("multiplayer.invite.old_code") : StatusMessage.Error("multiplayer.room.invalid_code"));
            return;
        }
        if (invite.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
        {
            SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
            return;
        }
        if ((TestPeers ?? _service.MultiplayerPeers).Count == 0)
        {
            SetStatus(StatusMessage.Warning("multiplayer.nodes.join_required"));
            return;
        }
        await KnockAndJoinAsync(invite);
    }

    /// <summary>拿到房间凭据之后：启动 EasyTier 加入房间。</summary>
    private bool StartJoin(RoomCode room)
    {
        if (!IsIdle) return false;
        // 封装模式下游戏和聊天都先连本机的 Amber，再封装着发给房主（完整局域网模式也一样）。
        var wraps = room.Tunnel.Wraps;
        var local = FullLanMode && !wraps ? (int?)null : EasyTierArgs.FreeTcpPort();
        var chatPort = FullLanMode && !wraps ? (int?)null : EasyTierArgs.FreeTcpPort();
        var remotePort = room.FromInvite && !(FullLanMode && !wraps) ? EasyTierArgs.FreeTcpPort() : (int?)null;
        if (!TryStartSession(new EasyTierOptions(room, false, TestPeers ?? _service.MultiplayerPeers, EasyTierArgs.FreeTcpPort(), local, TestListeners, Guid.NewGuid().ToString(), FullLanMode, chatPort, FullLanMode && AllowExitNode)
            {
                LocalTunnelPort = wraps && !FullLanMode ? EasyTierArgs.FreeTcpPort() : null,
                LocalRemotePort = remotePort,
                AllowProxy = AllowGameProxy,
            })) return false;
        _room = room;
        MarkJoinStarted(newRoom: true);
        RestoreChatHistory(room);
        _localChatPort = chatPort;
        _localRemotePort = remotePort;
        ChatStatus = L.T("multiplayer.chat.awaiting_host");
        Mode = "join";
        // 完整局域网模式下直接连房主的虚拟 IP；普通模式连本机转发端口。
        JoinAddress = local is { } l ? $"127.0.0.1:{l}" : $"{EasyTierArgs.HostIp}:{room.Port}";
        RoomCodeText = "";
        // 游戏的“多人游戏”列表里也会出现这个房间。
        if (local is { } port) _ = Amber.Core.Multiplayer.LanGames.AnnounceAsync(L.T("multiplayer.room.server_name"), port, _sessionCts!.Token);
        SetStatus(StatusMessage.Info("multiplayer.room.connecting"));
        return true;
    }

    private bool TryStartSession(EasyTierOptions options)
    {
        StopSession();
        try
        {
            var workDir = Path.Combine(AppSettings.DataDir, "easytier", options.Tun ? "run-lan" : "run");
            _session = options.Tun
                ? EasyTierElevatedSession.Start(_service.EasyTier, options, workDir)
                : EasyTierSession.Start(_service.EasyTier, options, workDir);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
            return false;
        }
        if (!TryStartTransport(options)) return false;
        _sessionCts = new CancellationTokenSource();
        var ipIndex = EasyTierArgs.Build(options).IndexOf("--ipv4");
        MyVirtualIp = options.Tun ? EasyTierArgs.Build(options)[ipIndex + 1] : "";
        _service.ActiveMultiplayer = _session;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshPeersAsync());
        _timer.Start();
        OnPropertyChanged(nameof(CanUseExit));
        return true;
    }

    private bool TryStartTransport(EasyTierOptions options)
    {
        try
        {
            _transport = RoomTransport.Start(options, line => _diag.Add(L.T("multiplayer.transport.diagnostics_category"), line));
            _currentOptions = options;
        }
        catch (System.Net.Sockets.SocketException e)
        {
            _session?.Dispose();
            _session = null;
            SetStatus(StatusMessage.Failure("multiplayer.transport.port_in_use", e, TunnelSettings.Port, ErrorText.Of(e)));
            return false;
        }
        _service.ActiveGameProxy = _transport.Proxy;
        GameProxyIp = _transport.Proxy?.ExitIp ?? "";
        var t = options.Room.Tunnel;
        TransportText = t.Wraps
            ? L.F("multiplayer.transport.summary", TransportChoices.FirstOrDefault(c => c.Mode == t.Mode)?.Label ?? t.Mode.ToString(), (t.Disguise ? L.T("multiplayer.transport.disguised_suffix") : ""), TransportProfile.Describe(t.Mode))
            : L.T("multiplayer.transport.general_summary");
        OnPropertyChanged(nameof(CanUseGameProxy));
        return true;
    }

    private void StopTransport()
    {
        var transport = _transport;
        _transport = null;
        _service.ActiveGameProxy = null;
        _currentOptions = null;
        GameProxyIp = "";
        TransportText = "";
        if (transport is not null) _ = transport.DisposeAsync().AsTask();
    }

    public bool CanUseGameProxy => _session is not null && !UsingGameProxy;

    /// <summary>“仅游戏”转发：游戏经选中成员的网络连服务器、登录。要用新的转发参数重启联机组件（几秒，聊天会自动重连）。</summary>
    [RelayCommand]
    private async Task UseGameProxyAsync()
    {
        if (SelectedExitPeer is not { } peer || _session is null || _currentOptions is not { } current) return;
        // 邀请房间的封装通道只在成员和房主之间有钥匙：只能经房主转发。
        if (current.Room.FromInvite && current.Room.Tunnel.Wraps && peer.Peer.Ip != EasyTierArgs.HostIp)
        {
            SetStatus(StatusMessage.Warning("multiplayer.proxy.host_only"));
            return;
        }
        var options = current with
        {
            ProxyExit = peer.Peer.Ip,
            LocalSocksPort = EasyTierArgs.FreeTcpPort(),
            LocalExitPort = current.Tun ? null : EasyTierArgs.FreeTcpPort(),
        };
        if (await RestartForProxyAsync(options))
            SetStatus(StatusMessage.Info("multiplayer.proxy.switching", peer.Name, peer.Peer.Ip));
    }

    [RelayCommand]
    private async Task StopGameProxyAsync()
    {
        if (_currentOptions is not { } current || !UsingGameProxy) return;
        if (await RestartForProxyAsync(current with { ProxyExit = null, LocalSocksPort = null, LocalExitPort = null }))
            SetStatus(StatusMessage.Info("multiplayer.proxy.stopped"));
    }

    /// <summary>换“仅游戏”转发的设置：不建网卡时要重启联机组件（转发规则是启动参数）；完整局域网模式下只换 Amber 自己的部分。</summary>
    private async Task<bool> RestartForProxyAsync(EasyTierOptions options)
    {
        if (_transport is { } t)
        {
            _transport = null;
            await t.DisposeAsync();
        }
        if (!options.Tun)
        {
            _session?.Dispose();
            try
            {
                _session = EasyTierSession.Start(_service.EasyTier, options, Path.Combine(AppSettings.DataDir, "easytier", "run"));
                _service.ActiveMultiplayer = _session;
                MarkJoinStarted(newRoom: false);
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                StopSession();
                SetStatus(new StatusMessage("common.raw", [L.T("multiplayer.proxy.restart_failed") + e.Message], StatusKind.Error, e.ToString()));
                return false;
            }
        }
        if (!TryStartTransport(options))
        {
            StopSession();
            return false;
        }
        return true;
    }

    private string ChatName => _service.Accounts.Store.Selected?.Name ?? _service.Settings.PlayerName;

    private void StartHub(RoomCode room)
    {
        try
        {
            _hub = new ChatHub(room, RoomChat.Port);
            var hub = _hub;
            hub.RemoteDeclined += (who, sid) => _remoteHub?.Decline(who, sid);
            StartRemoteHub(room);
            // 房主自己的客户端走转发留给它的本机端口（房间连接满了也能连上）。
            _ = ConnectChatAsync("127.0.0.1", _hub.HostPort);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            ChatStatus = L.F("multiplayer.chat.port_occupied", RoomChat.Port, e.Message);
        }
    }

    private async Task ConnectChatAsync(string host, int port)
    {
        if (_chat is not null || _chatConnecting || _room is not { } room) return;
        _chatConnecting = true;
        var client = new ChatClient(room, ChatName);
        client.Received += m => Dispatcher.UIThread.Post(() => OnChatMessage(client, m));
        client.Rekeyed += next => Dispatcher.UIThread.Post(() => OnRekey(client, next));
        client.Kicked += () => Dispatcher.UIThread.Post(() => OnKicked(client));
        client.RemoteRequested += sid => Dispatcher.UIThread.Post(() => _ = OnRemoteRequestedAsync(client, sid));
        client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_chat != client) return;
            _chat = null;
            ChatReady = false;
            ChatStatus = L.T("multiplayer.chat.reconnecting");
            _ = client.DisposeAsync();
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, timeout.Token);
            if (_room != room)
            {
                await client.DisposeAsync();
                return;
            }
            _chat = client;
            ChatReady = true;
            ChatStatus = L.T("multiplayer.chat.privacy");
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            await client.DisposeAsync();
            if (_room == room) ChatStatus = L.T("multiplayer.chat.connecting");
        }
        finally
        {
            _chatConnecting = false;
        }
    }

    private void OnChatMessage(ChatClient client, ChatMessage message)
    {
        if (_chat is not null && _chat != client) return;
        var mine = client.IsMine(message);
        ChatLines.Add(new ChatLine(message, mine));
        while (ChatLines.Count > 500) ChatLines.RemoveAt(0);
        if (!IsCurrent && message.Kind == ChatKind.Message && !mine) Badge++;
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        if (_chat is not { } chat || string.IsNullOrWhiteSpace(ChatInput)) return;
        var text = ChatInput;
        try
        {
            if (!await chat.SendAsync(text))
            {
                ChatStatus = L.T("multiplayer.chat.rate_limited");
                return;
            }
            ChatInput = "";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            ChatStatus = L.F("multiplayer.chat.send_failed", e.Message);
        }
    }

    /// <summary>导出聊天记录（只有用户点了导出才写文件）。</summary>
    public void ExportChat(string path)
    {
        var lines = ChatLines.Select(l =>
        {
            var time = l.Message.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return l.IsNotice ? L.F("multiplayer.chat.export_notice", time, l.Text) : L.F("multiplayer.chat.export_line", time, l.Name, l.Text);
        });
        File.WriteAllLines(path, lines);
        SetStatus(StatusMessage.Success("multiplayer.chat.exported", path));
    }

    /// <summary>每次刷新时看看聊天要不要连：房主在完整局域网模式下等虚拟网卡就绪后再监听虚拟 IP；加入方看到房主后连。</summary>
    private void TickChat(bool hostVisible)
    {
        if (_room is null) return;
        if (IsHosting && _session?.Options.Tun == true && _hub is not null && !_hubOnVirtualIp)
            _hubOnVirtualIp = _hub.TryAddListener(System.Net.IPAddress.Parse(EasyTierArgs.HostIp));
        if (IsJoined && hostVisible && _chat is null)
        {
            if (_localChatPort is { } port) _ = ConnectChatAsync("127.0.0.1", port);
            else _ = ConnectChatAsync(EasyTierArgs.HostIp, RoomChat.Port);
        }
        // 端口一时被占（比如刚结束的房间还没放开）时，下次刷新再试。
        if (IsHosting && _hub is null && _room is { } room) StartHub(room);
        if (IsHosting && _chat is null && _hub is not null) _ = ConnectChatAsync("127.0.0.1", _hub.HostPort);
        TickRemote();
    }

    private bool _hubOnVirtualIp;

    /// <summary>
    /// 换出口由会话所有者重启；提权守护确认网卡和出口路由就绪后才更新当前设置。
    /// 停止、核心退出或 Amber 管道断开时，守护撤回它创建的网络设置。
    /// </summary>
    private async Task<bool> RestartWithExitAsync(string? exitIp)
    {
        var lifetime = _sessionCts;
        var direct = _direct;
        if (lifetime is null) return false;
        try { await _exitSwitchLock.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return false; }
        IEasyTierSession? replacement = null;
        var transitionStarted = false;
        try
        {
            if (_sessionCts != lifetime || _direct != direct || lifetime.IsCancellationRequested) return false;
            if (_session is not { } old || !old.Options.Tun) return false;
            var options = old.Options with { ExitNode = exitIp };
            if (direct is not null)
            {
                await direct.SetExitNodeAsync(exitIp, lifetime.Token);
                if (_sessionCts != lifetime || _direct != direct) return false;
                _session = direct.EasyTier;
                // ActiveMultiplayer 保持 DirectHandle，由它一起清理桥、端口映射和子会话。
            }
            else
            {
                // 计划校验失败时保留仍然有效的旧会话，不先结束它。
                _ = EasyTierArgs.Build(options);
                transitionStarted = true;
                if (old is EasyTierElevatedSession { HasExited: false } elevated)
                    await elevated.RestartAsync(options, lifetime.Token);
                else
                {
                    old.Dispose();
                    var next = EasyTierElevatedSession.Start(_service.EasyTier, options, Path.Combine(AppSettings.DataDir, "easytier", "run-lan"));
                    replacement = next;
                    await next.WaitForReadyAsync(lifetime.Token);
                    if (_sessionCts != lifetime || _direct != direct) return false;
                    _session = next;
                    _service.ActiveMultiplayer = next;
                    replacement = null;
                }
            }
            if (_sessionCts != lifetime || _direct != direct || lifetime.IsCancellationRequested) return false;
            if (_session is not { HasExited: false } ready) throw new IOException(L.T("core.mp.component.startfailed"));
            _currentOptions = ready.Options;
            ExitNodeIp = ready.Options.ExitNode ?? "";
            MarkJoinStarted(newRoom: false);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or TimeoutException or UnauthorizedAccessException)
        {
            if (_sessionCts == lifetime && _direct == direct)
            {
                if (direct is not null) _session = direct.EasyTier;
                if (!transitionStarted && _session is { HasExited: false } preserved)
                {
                    _currentOptions = preserved.Options;
                    ExitNodeIp = preserved.Options.ExitNode ?? "";
                }
                else StopSession();
                SetStatus(StatusMessage.Failure("multiplayer.exit.switch_failed", e, ErrorText.Of(e)));
            }
            return false;
        }
        finally
        {
            try { replacement?.Dispose(); }
            finally { _exitSwitchLock.Release(); }
        }
    }

    [RelayCommand]
    private async Task UseExitNodeAsync()
    {
        if (SelectedExitPeer is not { } peer || !CanUseExit) return;
        if (await RestartWithExitAsync(peer.Peer.Ip))
        {
            SetStatus(StatusMessage.Info("multiplayer.exit.switching", peer.Name, peer.Peer.Ip));
        }
    }

    [RelayCommand]
    private async Task StopExitNodeAsync()
    {
        if (!UsingExit) return;
        // 出口是测试设的：停止整个测试（由测试恢复原来的设置）。
        if (GuestTestRunning)
        {
            await StopGuestTestAsync();
            return;
        }
        if (await RestartWithExitAsync(null)) SetStatus(StatusMessage.Info("multiplayer.exit.stopped"));
    }

    private bool _refreshing;

    private async Task RefreshPeersAsync()
    {
        if (_session is not { } session || _refreshing) return;
        if (session.HasExited)
        {
            // EasyTier 最后一行输出要显示在状态栏：先遮掉房间密钥和各种码。
            var last = NetDiagnostics.Redact(session.RecentOutput.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "", _room is { } r ? [.. r.Secrets] : [], includePublicIps: true);
            StopSession();
            Status = session.Options.Tun ? L.F("multiplayer.room.full_lan_ended", last) : L.F("multiplayer.component.exited", last);
            return;
        }
        _refreshing = true;
        try
        {
            ExitWarning = _transport?.ExitProblem ?? "";
            var peers = await session.PeersAsync();
            TickDirect(peers);
            if (UsingExit) OnPropertyChanged(nameof(ExitBanner));
            if (_session != session) return;
            Peers.Clear();
            foreach (var p in peers.Where(p => p.Ip.StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal)).OrderBy(p => p.Ip)) Peers.Add(new PeerItem(p));
            var others = Peers.Where(p => p.Peer.Cost != "Local").ToList();
            if (!others.Select(p => p.Peer.Ip).SequenceEqual(ExitCandidates.Select(p => p.Peer.Ip)))
            {
                var keep = SelectedExitPeer?.Peer.Ip;
                ExitCandidates.Clear();
                foreach (var p in others) ExitCandidates.Add(p);
                SelectedExitPeer = ExitCandidates.FirstOrDefault(p => p.Peer.Ip == keep) ?? ExitCandidates.FirstOrDefault();
            }
            var host = Peers.FirstOrDefault(p => p.Peer.Ip == EasyTierArgs.HostIp && p.Peer.Cost != "Local");
            TickChat(host is not null);
            if (host is not null) _hostSeen = true;
            if (IsJoined && host is null && !_hostSeen && DateTime.UtcNow - _joinedAt > TimeSpan.FromSeconds(20))
                SetStatus(StatusMessage.Info("multiplayer.room.not_connected_hint"));
            else if (IsJoined)
                Status = host is null ? L.T("multiplayer.room.connecting_first_time")
                    : L.F("multiplayer.room.connected", (host.Peer.LatencyMs is { } ms ? L.F("multiplayer.room.latency", ms) : ""), JoinAddress);
            else if (IsHosting)
                Status = Peers.Count(p => p.Peer.Ip != EasyTierArgs.HostIp) is var n and > 0 ? L.P("multiplayer.room.player_count", n, n) : L.T("multiplayer.room.waiting");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>复制房间码、邀请码、直连码用的剪贴板：60 秒后自动清掉（自检里换成短一点的）。</summary>
    internal Amber.App.Services.SensitiveClipboard SecretClipboard { get; set; } = Amber.App.Services.SensitiveClipboard.Shared;

    [RelayCommand]
    private async Task CopyRoomCodeAsync()
    {
        if (await SecretClipboard.CopyAsync(RoomCodeText))
            SetStatus(StatusMessage.Success("multiplayer.room.code_copied"));
    }

    /// <summary>把房间加进所选版本的服务器列表。</summary>
    [RelayCommand]
    private void AddToServers()
    {
        if (TargetInstance is not { } id || JoinAddress.Length == 0) return;
        var workDir = _service.WorkDirFor(id);
        Directory.CreateDirectory(workDir);
        ServerList.Add(workDir, new ServerEntry(L.T("multiplayer.room.server_name"), JoinAddress));
        SetStatus(StatusMessage.Success("multiplayer.room.added_to_servers", id, JoinAddress));
    }

    /// <summary>主窗口设置：启动请求发出后切回启动页。</summary>
    public Action? NavigateHome { get; set; }

    [RelayCommand]
    private void LaunchAndJoin()
    {
        if (TargetInstance is not { } id || JoinAddress.Length == 0) return;
        _service.RequestLaunch(new LaunchRequest(id, JoinServer: JoinAddress));
        NavigateHome?.Invoke();
    }

    [RelayCommand]
    private void Stop()
    {
        StopSession();
        SetStatus(StatusMessage.Info("multiplayer.room.stopped"));
    }

    private void StopSession()
    {
        _timer?.Stop();
        _timer = null;
        _sessionCts?.Cancel();
        _sessionCts = null;
        // 直连房间的 EasyTier 归直连会话管，由它一起关掉。
        if (_direct is not null) _ = StopDirectAsync();
        else _session?.Dispose();
        _session = null;
        _service.ActiveMultiplayer = null;
        StopTransport();
        Peers.Clear();
        if (_room is { } leftRoom) SaveChatHistory(leftRoom);
        _room = null;
        StopHostLobby();
        HostFingerprint = "";
        _localChatPort = null;
        _hubOnVirtualIp = false;
        StopRemote();
        var chat = _chat;
        var hub = _hub;
        _chat = null;
        _hub = null;
        ChatReady = false;
        ChatStatus = "";
        ChatLines.Clear();
        Badge = 0;
        // 先关房主的聊天服务（马上释放端口，紧接着开新房间也能用），再关自己的连接。
        var hubClosing = hub?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            await hubClosing;
            if (chat is not null) await chat.DisposeAsync();
        });
        Mode = "";
        RoomCodeText = "";
        JoinAddress = "";
        MyVirtualIp = "";
        ExitNodeIp = "";
        ExitCandidates.Clear();
        OnPropertyChanged(nameof(CanUseExit));
    }
}
