using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.RemoteControl;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>远程控制要用的窗口（同意弹窗、查看窗口、红框和小条）。界面层实现；自检里可以换成假的。</summary>
public interface IRemoteUi
{
    /// <summary>弹同意窗。默认“拒绝”，ct 取消（30 秒）时关窗算拒绝。</summary>
    Task<RemoteConsent> AskConsentAsync(RemoteConsentRequest request, CancellationToken ct);

    /// <summary>打开查看窗口（关窗口就结束）。</summary>
    void ShowViewer(RemoteViewer viewer);

    /// <summary>被控期间：屏幕边缘红框、顶部小条（停止 / 收回控制）、全局热键 Ctrl+Shift+F12。会话结束时自己收起。</summary>
    void ShowSharing(RemoteSharer sharer);
}

/// <summary>
/// 联机页的远程控制（只支持邀请房间）。所有连接都由成员发起、连到房主：
/// 成员看/控房主：成员直接连；房主看/控成员：房主经聊天通道发请求，成员同意后连回来。
/// 被控方每次都要点同意（30 秒不点算拒绝），没有“信任此人免确认”。离开房间、被踢、换网络都马上断开。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private RemoteHub? _remoteHub;
    private bool _remoteHubOnVirtualIp;
    private int? _localRemotePort;
    private readonly HashSet<RemoteEndpoint> _remoteSessions = [];
    private int _remotePrompting;

    /// <summary>远程控制用的窗口。测试可以换掉。</summary>
    internal IRemoteUi RemoteUi { get; set; } = new Views.RemoteWindows();

    /// <summary>远程控制的同意策略：每次都问（可以换，比如以后加“信任此人”）。</summary>
    internal IRemoteConsentPolicy RemoteConsentPolicy => new AskEveryTimePolicy((request, ct) => RemoteUi.AskConsentAsync(request, ct));

    /// <summary>当前房间能用远程控制（邀请房间，并且这台电脑支持截屏和注入）。</summary>
    public bool CanRemoteControl => _room is { FromInvite: true } && RemotePlatform.Supported;

    /// <summary>不能用时的说明（AMB- 房间码的房间、不支持的系统）。</summary>
    public string RemoteUnavailableText => !RemotePlatform.Supported ? L.T("multiplayer.remote.unsupported_os")
        : _room is { FromInvite: false } ? L.T("multiplayer.remote.invite_only") : "";

    public bool HasRemoteUnavailableText => _room is not null && !CanRemoteControl;

    /// <summary>正在等对方同意（按钮先禁用）。</summary>
    [ObservableProperty] public partial bool RemoteWaiting { get; set; }

    private void NotifyRemoteAvailability()
    {
        OnPropertyChanged(nameof(CanRemoteControl));
        OnPropertyChanged(nameof(RemoteUnavailableText));
        OnPropertyChanged(nameof(HasRemoteUnavailableText));
    }

    partial void OnModeChanged(string value) => NotifyRemoteAvailability();

    private ITileCodec RemoteCodec => new SkiaTileCodec();

    // ---- 房主 ----

    private void StartRemoteHub(RoomCode room)
    {
        NotifyRemoteAvailability();
        if (_remoteHub is not null || !room.FromInvite) return;
        try
        {
            var hub = new RemoteHub(room, new RemoteHostOptions(RemoteConsentPolicy, RemotePlatform.CreateScreen, RemotePlatform.CreateInput, RemoteCodec));
            hub.SharingStarted += (sharer, _) => Dispatcher.UIThread.Post(() => TrackSharing(sharer));
            _remoteHub = hub;
        }
        catch (System.Net.Sockets.SocketException e)
        {
            _diag.Add(L.T("multiplayer.remote.diagnostics_category"), L.F("multiplayer.remote.port_occupied", RemoteHub.Port, e.Message));
        }
    }

    private bool _lastCanRemote;

    private void TickRemote()
    {
        if (_lastCanRemote != CanRemoteControl || HasRemoteUnavailableText)
        {
            _lastCanRemote = CanRemoteControl;
            NotifyRemoteAvailability();
        }
        if (!IsHosting || _room is not { FromInvite: true } room) return;
        if (_remoteHub is null) StartRemoteHub(room);
        if (_session?.Options.Tun == true && _remoteHub is { } hub && !_remoteHubOnVirtualIp)
            _remoteHubOnVirtualIp = hub.TryAddListener(System.Net.IPAddress.Parse(EasyTierArgs.HostIp));
    }

    /// <summary>房主：请求看/控这个成员的屏幕。他那边弹同意窗，同意后连回来。</summary>
    [RelayCommand]
    private async Task RequestRemoteAsync(MemberItem? item)
    {
        if (item is null || !IsHosting || RemoteWaiting || _remoteHub is not { } remote || _hub is not { } chat || _members is not { } members) return;
        if (!CanRemoteControl)
        {
            SetStatus(new StatusMessage("common.raw", [RemoteUnavailableText], StatusKind.Warning));
            return;
        }
        if (members.FindGuest(item.PublicKey) is not { } entry)
        {
            SetStatus(StatusMessage.Warning("multiplayer.remote.offline", item.Name));
            return;
        }
        RemoteWaiting = true;
        SetStatus(StatusMessage.Info("multiplayer.remote.waiting", item.Name));
        try
        {
            var result = await remote.RequestAsync(entry, sid => chat.SendRemoteRequest(entry, sid));
            if (result.Viewer is { } viewer) OpenViewer(viewer);
            else SetStatus(FailureStatus(result.Failure, item.Name));
        }
        finally
        {
            RemoteWaiting = false;
        }
    }

    // ---- 成员 ----

    /// <summary>成员：请求看/控房主的屏幕。房主那边弹同意窗。</summary>
    [RelayCommand]
    private async Task ControlHostAsync()
    {
        if (!IsJoined || RemoteWaiting || _room is not { } room) return;
        if (!CanRemoteControl)
        {
            SetStatus(new StatusMessage("common.raw", [RemoteUnavailableText], StatusKind.Warning));
            return;
        }
        var name = HostDisplayName;
        RemoteWaiting = true;
        SetStatus(StatusMessage.Info("multiplayer.remote.waiting", name));
        try
        {
            var (host, port) = RemoteEndpointToHost();
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stream = await RemoteConnector.OpenAsync(host, port, room, RemoteRole.Controller, null, connect.Token);
            var viewer = new RemoteViewer(stream, RemoteCodec, name, HostFingerprint);
            if (_sessionCts is { } life) viewer.EndWith(life.Token);
            var accept = await viewer.StartAsync();
            if (_room != room)
            {
                viewer.Abort(RemoteEnd.Local);
                return;
            }
            if (accept is null)
            {
                SetStatus(FailureStatus(viewer.DenyReason switch
                {
                    RemoteDenyReason.Busy => RemoteRequestFailure.Busy,
                    RemoteDenyReason.Unsupported => RemoteRequestFailure.Unsupported,
                    RemoteDenyReason.TimedOut => RemoteRequestFailure.TimedOut,
                    _ => RemoteRequestFailure.Declined,
                }, name));
                return;
            }
            OpenViewer(viewer);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException
            or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.remote.connect_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            RemoteWaiting = false;
        }
    }

    /// <summary>成员连房主远程控制服务的地址：完整局域网且不封装时直连房主虚拟 IP，否则连本机转发端口。</summary>
    private (string Host, int Port) RemoteEndpointToHost() =>
        _localRemotePort is { } port ? ("127.0.0.1", port) : (EasyTierArgs.HostIp, RemoteHub.Port);

    /// <summary>房主的名字：聊天里带房主指纹的那条消息上的名字；没有时写“房主”。</summary>
    private string HostDisplayName =>
        ChatLines.LastOrDefault(l => HostFingerprint.Length > 0 && l.Message.Fingerprint == HostFingerprint)?.Message.Name is { Length: > 0 } name
            ? name : L.T("multiplayer.remote.host_fallback");

    /// <summary>成员：房主请求看/控自己。弹同意窗；同意就连回房主并共享屏幕，不同意就经聊天通道回“拒绝”。</summary>
    private async Task OnRemoteRequestedAsync(ChatClient client, string sessionId)
    {
        if (_chat != client || !IsJoined || _room is not { FromInvite: true } room) return;
        // 已经在被控或者正在问：直接拒绝（不叠弹窗）。
        if (!RemotePlatform.Supported || _remoteSessions.OfType<RemoteSharer>().Any() || Interlocked.Exchange(ref _remotePrompting, 1) != 0)
        {
            await DeclineRemote(client, sessionId);
            return;
        }
        RemoteConsent consent;
        try
        {
            using var life = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts?.Token ?? default);
            consent = await RemoteConsentPolicy.DecideAsync(new RemoteConsentRequest(HostDisplayName, HostFingerprint), life.Token);
        }
        finally
        {
            Volatile.Write(ref _remotePrompting, 0);
        }
        if (_room != room || _chat != client) return;
        if (!consent.Accepted)
        {
            await DeclineRemote(client, sessionId);
            return;
        }
        var screen = RemotePlatform.CreateScreen();
        var input = consent.ViewOnly ? null : RemotePlatform.CreateInput();
        try
        {
            var (host, port) = RemoteEndpointToHost();
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stream = await RemoteConnector.OpenAsync(host, port, room, RemoteRole.Controlled, sessionId, connect.Token);
            var sharer = new RemoteSharer(stream, screen!, input, RemoteCodec, !consent.ViewOnly, HostDisplayName, HostFingerprint);
            if (_sessionCts is { } lifetime) sharer.EndWith(lifetime.Token);
            sharer.Ended += _ =>
            {
                screen?.Dispose();
                input?.Dispose();
            };
            TrackSharing(sharer);
            _ = Task.Run(sharer.RunAsync);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException
            or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            screen?.Dispose();
            input?.Dispose();
            SetStatus(StatusMessage.Failure("multiplayer.remote.connect_failed", e, ErrorText.Of(e)));
        }
    }

    private static async Task DeclineRemote(ChatClient client, string sessionId)
    {
        try { await client.DeclineRemoteAsync(sessionId); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException) { }
    }

    // ---- 两边共用 ----

    private void OpenViewer(RemoteViewer viewer)
    {
        Track(viewer);
        RemoteUi.ShowViewer(viewer);
        SetStatus(StatusMessage.Success(viewer.ControlGranted ? "multiplayer.remote.controlling" : "multiplayer.remote.viewing", viewer.PeerName));
    }

    private void TrackSharing(RemoteSharer sharer)
    {
        Track(sharer);
        RemoteUi.ShowSharing(sharer);
        SetStatus(StatusMessage.Warning("multiplayer.remote.being_controlled", sharer.PeerName));
    }

    private void Track(RemoteEndpoint session)
    {
        _remoteSessions.Add(session);
        session.Ended += end => Dispatcher.UIThread.Post(() =>
        {
            if (!_remoteSessions.Remove(session)) return;
            SetStatus(StatusMessage.Info(end switch
            {
                RemoteEnd.Peer => "multiplayer.remote.ended_peer",
                RemoteEnd.Local => "multiplayer.remote.ended_local",
                _ => "multiplayer.remote.ended_disconnected",
            }, session.PeerName));
        });
        if (session.HasEnded) _remoteSessions.Remove(session);
    }

    private static StatusMessage FailureStatus(RemoteRequestFailure failure, string name) => failure switch
    {
        RemoteRequestFailure.Offline => StatusMessage.Warning("multiplayer.remote.offline", name),
        RemoteRequestFailure.TimedOut => StatusMessage.Warning("multiplayer.remote.timed_out", name),
        RemoteRequestFailure.Busy => StatusMessage.Warning("multiplayer.remote.busy", name),
        RemoteRequestFailure.Unsupported => StatusMessage.Warning("multiplayer.remote.peer_unsupported", name),
        RemoteRequestFailure.NotInviteRoom => StatusMessage.Warning("multiplayer.remote.invite_only"),
        _ => StatusMessage.Warning("multiplayer.remote.declined", name),
    };

    /// <summary>断开所有远程控制会话（换网络、被踢、离开房间时）。</summary>
    private void StopAllRemote()
    {
        foreach (var s in _remoteSessions.ToList()) s.Abort(RemoteEnd.Disconnected);
        _remoteSessions.Clear();
        _remoteHub?.StopAll();
    }

    /// <summary>离开房间：断开会话、关掉房主的远程控制服务。</summary>
    private void StopRemote()
    {
        StopAllRemote();
        var hub = _remoteHub;
        _remoteHub = null;
        _remoteHubOnVirtualIp = false;
        _localRemotePort = null;
        RemoteWaiting = false;
        if (hub is not null) _ = hub.DisposeAsync().AsTask();
        NotifyRemoteAvailability();
    }

    /// <summary>自检用：不开真的房间，只把联机页摆成“房主 + 一个成员”或“加入方”的样子截图。</summary>
    internal void ShowRemoteLayoutForTest(RoomCode? room, string mode, MemberItem? member = null)
    {
        _room = room;
        Mode = mode;
        Members.Clear();
        if (member is not null) Members.Add(member);
        NotifyRemoteAvailability();
    }

    /// <summary>测试用：现在有几个远程控制会话。</summary>
    internal int RemoteSessionCount => _remoteSessions.Count;
}
