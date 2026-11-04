using System.Collections.ObjectModel;
using System.Net.Sockets;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Multiplayer;
using Amber.Core.Net;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public sealed record LanGameItem(LanGame Game, string Source)
{
    public string Label => $"{Game.Motd} · 端口 {Game.Port}（{Source}）";
}

public sealed partial class NodeItem(string url) : ObservableObject
{
    public string Url { get; } = url;

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "";
}

public sealed record ChatLine(ChatMessage Message, bool Mine)
{
    /// <summary>恢复的历史和新消息之间的分隔行。</summary>
    public bool IsSeparator { get; init; }
    public string Time => Message.Time.ToLocalTime().ToString("HH:mm");
    public string Name => Mine ? "我" : Message.Name;
    public bool IsNotice => Message.Kind != ChatKind.Message || IsSeparator;
    public string Text => Message.Kind switch
    {
        ChatKind.Join => $"{Message.Name} 进入了房间",
        ChatKind.Leave => $"{Message.Name} 离开了房间",
        _ => Message.Text,
    };
}

public sealed record PeerItem(PeerInfo Peer)
{
    public string Name => Peer.Cost == "Local" ? "我" : Peer.Hostname switch
    {
        "amber-host" => "房主",
        "amber-guest" => "玩家",
        "" => "节点",
        var h => h,
    };
    public string Meta => string.Join(" · ", new[]
    {
        Peer.Ip,
        Peer.LatencyMs is { } ms ? $"{ms:F0} ms" : null,
        Peer.Cost switch { "Local" => null, var c when c.Contains("p2p", StringComparison.OrdinalIgnoreCase) => "直连", "" => null, _ => "经中继" },
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
    private CancellationTokenSource? _downloadCts;
    private DispatcherTimer? _timer;
    private ChatHub? _hub;
    private ChatClient? _chat;
    private bool _chatConnecting;
    private RoomCode? _room;

    public override string Title => "联机";
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

    [ObservableProperty] public partial string MyVirtualIp { get; set; } = "";

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
    public string ExitBanner => UsingExit ? $"你的全部网络流量正经 {ExitNodeIp} 的电脑出去。点“停止”恢复正常上网。" : "";
    public ObservableCollection<PeerItem> ExitCandidates { get; } = [];

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
        ChatLines.Add(new ChatLine(new ChatMessage(ChatKind.Message, "", "", "—— 以上是之前的消息（离开期间的消息看不到）——", DateTimeOffset.Now), false) { IsSeparator = true });
    }
    [ObservableProperty] public partial string? TargetInstance { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsHosting), nameof(IsJoined))]
    public partial string Mode { get; set; } = "";

    public bool IsIdle => Mode == "";
    public bool IsHosting => Mode == "host";
    public bool IsJoined => Mode == "join";

    [ObservableProperty] public partial string RoomCodeText { get; set; } = "";
    [ObservableProperty] public partial string JoinAddress { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";

    public string ComponentText => IsComponentReady
        ? $"EasyTier {EasyTierRelease.Version} 已就绪"
        : $"联机需要先下载 EasyTier {EasyTierRelease.Version}（约 {EasyTierRelease.Size / 1024 / 1024} MB，来自 GitHub 官方发布页，下载后校验 SHA-256）";

    public string LicenseText =>
        $"联机由开源项目 EasyTier 实现（{EasyTierRelease.License}，{EasyTierRelease.ProjectUrl}）。不经过 Amber 的服务器；连不上直连时，数据经你添加的联机节点转发（用房间密钥加密）。默认模式不需要管理员权限，也不安装驱动。";

    public MultiplayerViewModel(LauncherService service)
    {
        _service = service;
        service.GameOutput += OnGameOutput;
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshInstances);
    }

    protected override void OnFirstActivated()
    {
        IsComponentReady = _service.EasyTier.IsInstalled;
        LoadNodes();
        RefreshInstances();
        // 不自动听局域网广播：绑定端口可能让 Windows 弹防火墙询问。用 Amber 启动的游戏从日志就能认出端口。
    }

    private void RefreshInstances()
    {
        var keep = TargetInstance;
        Instances.Clear();
        foreach (var v in _service.InstalledVersions()) Instances.Add(v);
        TargetInstance = keep is not null && Instances.Contains(keep) ? keep
            : _service.Settings.LastVersion is { } last && Instances.Contains(last) ? last
            : Instances.FirstOrDefault();
    }

    private void OnGameOutput(string versionId, string line)
    {
        if (LanGames_PortFromLog(line) is not { } port) return;
        Dispatcher.UIThread.Post(() =>
        {
            var item = new LanGameItem(new LanGame(versionId, port), "Amber 启动的游戏");
            LanGames.Insert(0, item);
            SelectedLanGame = item;
            if (IsIdle) Status = $"{versionId} 已对局域网开放，端口 {port}，可以创建房间了";
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
                LanGames.Add(new LanGameItem(game, "局域网广播"));
            }
            SelectedLanGame ??= LanGames.FirstOrDefault();
            if (IsIdle && LanGames.Count == 0) Status = "没找到开放到局域网的游戏：进入世界后，在暂停菜单点“对局域网开放”，再点“查找”";
        }
        catch (SocketException e)
        {
            Status = "查找局域网游戏失败：" + e.Message + "。也可以手动填端口。";
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
            Status = "正在下载 EasyTier……";
            await _service.EasyTier.EnsureAsync(new Progress<DownloadProgress>(p =>
            {
                DownloadPercent = Math.Min(100, 100.0 * p.BytesDownloaded / EasyTierRelease.Size);
            }), _downloadCts.Token);
            IsComponentReady = true;
            Status = "EasyTier 已下载并校验通过";
        }
        catch (OperationCanceledException) when (_downloadCts.IsCancellationRequested)
        {
            Status = "已取消下载";
        }
        catch (Exception e)
        {
            Status = "下载失败：" + (e is OperationCanceledException ? "网络请求超时，请稍后重试" : e.Message);
        }
        finally
        {
            IsDownloading = false;
            DownloadPercent = 0;
        }
    }

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
            Status = $"节点地址格式不对：{url}（应该像 tcp://example.com:11010）";
            return;
        }
        if (Nodes.Any(n => n.Url == url)) return;
        Nodes.Add(new NodeItem(url));
        SaveNodes();
        NewNodeUrl = "";
        Status = "节点已添加，下次创建或加入房间时生效";
    }

    [RelayCommand]
    private void RemoveNode(NodeItem? item)
    {
        if (item is null) return;
        Nodes.Remove(item);
        SaveNodes();
    }


    [RelayCommand]
    private async Task TestNodesAsync()
    {
        foreach (var node in Nodes) node.LatencyText = "测试中……";
        await Task.WhenAll(Nodes.ToList().Select(async node =>
        {
            var (ms, error) = await RelayNodes.ProbeAsync(node.Url);
            node.LatencyText = ms is { } v ? $"{v:F0} ms" : error ?? "失败";
        }));
    }

    [RelayCommand]
    private async Task ExportNodesAsync()
    {
        ShareCodeText = RelayNodes.Encode(null, Nodes.Select(n => n.Url));
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            await clipboard.SetTextAsync(ShareCodeText);
        Status = "节点分享码已复制。和朋友用同样的节点，联机更容易连上。";
    }

    [RelayCommand]
    private void ImportNodes()
    {
        if (!RelayNodes.TryDecode(ShareCodeText, out var name, out var urls))
        {
            Status = "没认出节点分享码。粘贴 AMBNODE- 开头的分享码，或者直接粘贴节点地址。";
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
        Status = $"导入了 {added} 个节点{(name is null ? "" : "（" + name + "）")}";
    }

    /// <summary>自检用：同一个程序里再开一个联机页（扮演加入方）。</summary>
    internal MultiplayerViewModel CreateTestPeer() => new(_service);

    /// <summary>测试用：额外的监听地址和对端（两个实例在同一台电脑上直接互连，不经过公共节点）。</summary>
    internal IReadOnlyList<string>? TestListeners { get; set; }
    internal IReadOnlyList<string>? TestPeers { get; set; }

    [RelayCommand]
    private async Task CreateRoomAsync()
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
        if (!await CanConnectAsync(port.Value))
        {
            Status = $"本机端口 {port} 上没有游戏在等待连接。确认世界已经“对局域网开放”，端口填对了。";
            return;
        }
        if ((TestPeers ?? _service.MultiplayerPeers).Count == 0 && TestListeners is null)
        {
            Status = "还没有联机节点：在下面“联机节点”里添加一个（自己搭的，或导入朋友的节点分享码），双方要用同一个节点。";
            return;
        }
        var room = RoomCode.Create(port.Value);
        if (!TryStartSession(new EasyTierOptions(room, true, TestPeers ?? _service.MultiplayerPeers, EasyTierArgs.FreeTcpPort(), null, TestListeners, Guid.NewGuid().ToString(), FullLanMode, null, FullLanMode && AllowExitNode))) return;
        Mode = "host";
        RoomCodeText = room.ToString();
        _room = room;
        RestoreChatHistory(room);
        StartHub(room);
        Status = FullLanMode
            ? "房间已创建（完整局域网模式）。把房间码发给朋友。如果朋友连不进来，在 Windows 防火墙里允许 Java（javaw.exe）通过“Amber LAN”网络。"
            : "房间已创建。把房间码发给朋友；关掉 Amber 或点“结束联机”后房间就没了。";
    }

    [RelayCommand]
    private void Join()
    {
        if (!IsIdle) return;
        if (!IsComponentReady)
        {
            Status = "先下载联机组件";
            return;
        }
        if (!RoomCode.TryParse(JoinCode, out var room))
        {
            Status = "房间码格式不对，应该像 AMB-XXXXXXXX-XXXXXXXXXXXX-25565";
            return;
        }
        if ((TestPeers ?? _service.MultiplayerPeers).Count == 0)
        {
            Status = "还没有联机节点：在下面“联机节点”里添加和房主相同的节点（可以让房主导出节点分享码发给你）。";
            return;
        }
        var local = FullLanMode ? (int?)null : EasyTierArgs.FreeTcpPort();
        var chatPort = FullLanMode ? (int?)null : EasyTierArgs.FreeTcpPort();
        if (!TryStartSession(new EasyTierOptions(room, false, TestPeers ?? _service.MultiplayerPeers, EasyTierArgs.FreeTcpPort(), local, TestListeners, Guid.NewGuid().ToString(), FullLanMode, chatPort, FullLanMode && AllowExitNode))) return;
        _room = room;
        RestoreChatHistory(room);
        _localChatPort = chatPort;
        ChatStatus = "连上房主后自动进入聊天";
        Mode = "join";
        // 完整局域网模式下直接连房主的虚拟 IP；普通模式连本机转发端口。
        JoinAddress = local is { } l ? $"127.0.0.1:{l}" : $"{EasyTierArgs.HostIp}:{room.Port}";
        RoomCodeText = room.ToString();
        // 游戏的“多人游戏”列表里也会出现这个房间。
        if (local is { } port) _ = Amber.Core.Multiplayer.LanGames.AnnounceAsync("Amber 联机房间", port, _sessionCts!.Token);
        Status = "正在连接房主……";
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
            Status = "联机没有启动：" + e.Message;
            return false;
        }
        _sessionCts = new CancellationTokenSource();
        var ipIndex = EasyTierArgs.Build(options).IndexOf("--ipv4");
        MyVirtualIp = options.Tun ? EasyTierArgs.Build(options)[ipIndex + 1] : "";
        _service.ActiveMultiplayer = _session;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshPeersAsync());
        _timer.Start();
        OnPropertyChanged(nameof(CanUseExit));
        return true;
    }

    private string ChatName => _service.Accounts.Store.Selected?.Name ?? _service.Settings.PlayerName;

    private void StartHub(RoomCode room)
    {
        try
        {
            _hub = new ChatHub(room, RoomChat.Port);
            _ = ConnectChatAsync("127.0.0.1", RoomChat.Port);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            ChatStatus = $"聊天不可用：本机端口 {RoomChat.Port} 被占用（{e.Message}）";
        }
    }

    private async Task ConnectChatAsync(string host, int port)
    {
        if (_chat is not null || _chatConnecting || _room is not { } room) return;
        _chatConnecting = true;
        var client = new ChatClient(room, ChatName);
        client.Received += m => Dispatcher.UIThread.Post(() => OnChatMessage(client, m));
        client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            if (_chat != client) return;
            _chat = null;
            ChatReady = false;
            ChatStatus = "聊天连接断开了，正在重连……";
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
            ChatStatus = "消息用房间密码加密，只有拿到房间码的人能看到；聊天记录只保存在内存里，关掉 Amber 就清空。";
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            await client.DisposeAsync();
            if (_room == room) ChatStatus = "正在连接房间聊天……";
        }
        finally
        {
            _chatConnecting = false;
        }
    }

    private void OnChatMessage(ChatClient client, ChatMessage message)
    {
        if (_chat is not null && _chat != client) return;
        ChatLines.Add(new ChatLine(message, message.SenderId == client.SenderId));
        while (ChatLines.Count > 500) ChatLines.RemoveAt(0);
        if (!IsCurrent && message.Kind == ChatKind.Message && message.SenderId != client.SenderId) Badge++;
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
                ChatStatus = "发得太快了，稍等几秒再发";
                return;
            }
            ChatInput = "";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            ChatStatus = "发送失败：" + e.Message;
        }
    }

    /// <summary>导出聊天记录（只有用户点了导出才写文件）。</summary>
    public void ExportChat(string path)
    {
        var lines = ChatLines.Select(l => $"[{l.Message.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {(l.IsNotice ? "" : l.Name + "：")}{l.Text}");
        File.WriteAllLines(path, lines);
        Status = "聊天记录已导出：" + path;
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
        if (IsHosting && _chat is null && _hub is not null) _ = ConnectChatAsync("127.0.0.1", RoomChat.Port);
    }

    private bool _hubOnVirtualIp;

    /// <summary>
    /// 换出口要用新参数重启 EasyTier（Windows 会再确认一次权限）。路由由 EasyTier 加在虚拟网卡上，
    /// 它退出、网卡消失时路由跟着消失；Amber 崩溃时守护脚本会结束它，所以不会留下“上不了网”的路由。
    /// </summary>
    private bool RestartWithExit(string? exitIp)
    {
        if (_session is not { } old || !old.Options.Tun) return false;
        var options = old.Options with { ExitNode = exitIp };
        old.Dispose();
        try
        {
            _session = EasyTierElevatedSession.Start(_service.EasyTier, options, Path.Combine(AppSettings.DataDir, "easytier", "run-lan"));
            _service.ActiveMultiplayer = _session;
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 用户拒绝了权限确认：旧会话已经停了，按“结束联机”处理，网络恢复正常。
            StopSession();
            Status = "没有切换出口，联机已结束：" + e.Message;
            return false;
        }
    }

    [RelayCommand]
    private void UseExitNode()
    {
        if (SelectedExitPeer is not { } peer || !CanUseExit) return;
        if (RestartWithExit(peer.Peer.Ip))
        {
            ExitNodeIp = peer.Peer.Ip;
            Status = $"正在切换：之后你的网络流量会经 {peer.Name}（{peer.Peer.Ip}）的电脑出去。对方必须先开启“允许成员通过我的电脑上网”，否则会暂时上不了网，点“停止”即可恢复。";
        }
    }

    [RelayCommand]
    private void StopExitNode()
    {
        if (!UsingExit) return;
        if (RestartWithExit(null)) Status = "已停止经别人上网，网络恢复正常";
        ExitNodeIp = "";
    }

    private bool _refreshing;

    private async Task RefreshPeersAsync()
    {
        if (_session is not { } session || _refreshing) return;
        if (session.HasExited)
        {
            var last = session.RecentOutput.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            StopSession();
            Status = (session.Options.Tun ? "完整局域网模式已结束：" : "联机组件意外退出：") + last;
            return;
        }
        _refreshing = true;
        try
        {
            var peers = await session.PeersAsync();
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
            if (IsJoined)
                Status = host is null ? "正在连接房主……（第一次可能要十几秒）"
                    : $"已连上房主{(host.Peer.LatencyMs is { } ms ? $"，延迟 {ms:F0} ms" : "")}。在游戏里添加服务器 {JoinAddress}，或点下面的按钮直接进入";
            else if (IsHosting)
                Status = Peers.Count(p => p.Peer.Ip != EasyTierArgs.HostIp) is var n and > 0 ? $"房间里有 {n} 位玩家" : "等待朋友加入……";
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

    [RelayCommand]
    private async Task CopyRoomCodeAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(RoomCodeText);
            Status = "房间码已复制";
        }
    }

    /// <summary>把房间加进所选版本的服务器列表。</summary>
    [RelayCommand]
    private void AddToServers()
    {
        if (TargetInstance is not { } id || JoinAddress.Length == 0) return;
        var workDir = _service.WorkDirFor(id);
        Directory.CreateDirectory(workDir);
        ServerList.Add(workDir, new ServerEntry("Amber 联机房间", JoinAddress));
        Status = $"已加到 {id} 的多人游戏列表（地址 {JoinAddress}，每次加入房间端口会变）";
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
        Status = "已结束联机";
    }

    private void StopSession()
    {
        _timer?.Stop();
        _timer = null;
        _sessionCts?.Cancel();
        _sessionCts = null;
        _session?.Dispose();
        _session = null;
        _service.ActiveMultiplayer = null;
        Peers.Clear();
        if (_room is { } leftRoom) SaveChatHistory(leftRoom);
        _room = null;
        _localChatPort = null;
        _hubOnVirtualIp = false;
        var chat = _chat;
        var hub = _hub;
        _chat = null;
        _hub = null;
        ChatReady = false;
        ChatStatus = "";
        ChatLines.Clear();
        Badge = 0;
        _ = Task.Run(async () =>
        {
            if (chat is not null) await chat.DisposeAsync();
            if (hub is not null) await hub.DisposeAsync();
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
