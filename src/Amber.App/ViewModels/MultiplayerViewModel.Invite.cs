using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>邀请的有效期选项。</summary>
public sealed record InviteExpiryChoice(TimeSpan? Lifetime, string Key) : LocalizedRecord
{
    public string Label => L.T(Key);
    public override string ToString() => Label;
}

/// <summary>还能用的一张邀请（房主的“待用邀请”列表）。</summary>
public sealed record PendingInviteItem(PendingInvite Invite) : LocalizedRecord
{
    public string Label => L.F("multiplayer.invite.pending_item", Invite.Invite.Masked, Invite.UsesLeft, Invite.Uses,
        Invite.ExpiresAt is { } at ? L.F("multiplayer.invite.expires_at", at.ToLocalTime().ToString("HH:mm")) : L.T("multiplayer.invite.expiry.room"),
        Invite.AutoApprove ? L.T("multiplayer.invite.auto_suffix") : "");
}

/// <summary>有人敲门、等房主批准的卡片。120 秒没点算拒绝（卡片自己消失）。</summary>
public sealed record ApprovalCard(KnockRequest Request, string? Warning) : LocalizedRecord
{
    internal TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Title => L.F("multiplayer.invite.knocking", Request.Name.Length > 0 ? Request.Name : L.T("multiplayer.peers.player"));
    public string Detail => L.F("multiplayer.invite.fingerprint", Request.Fingerprint) + (Request.Version.Length > 0 ? " · " + Request.Version : "");
    public bool HasWarning => Warning is not null;
}

/// <summary>经邀请进来的成员（房主看）。发出凭据时就列上；对方回了确认再标“已确认”。换网络时不在线的标“需要重新邀请”。</summary>
public sealed record MemberItem(string Name, byte[] PublicKey, bool Trusted, bool Confirmed = false, bool NeedsInvite = false) : LocalizedRecord
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
    public string Label => (Name.Length > 0 ? Name : L.T("multiplayer.peers.player")) + " · " + Fingerprint
        + " · " + L.T(NeedsInvite ? "multiplayer.kick.needs_invite" : Confirmed ? "multiplayer.invite.member_confirmed" : "multiplayer.invite.member_unconfirmed");
    public bool CanTrust => !Trusted;
}

/// <summary>这个房间里踢出的人（房主看，可以解除）。</summary>
public sealed record BannedItem(BannedMember Member) : LocalizedRecord
{
    public string Label => (Member.Name.Length > 0 ? Member.Name : L.T("multiplayer.peers.player")) + " · " + Member.Fingerprint;
}

/// <summary>信任列表里的一个人（联机设置里管理）。</summary>
public sealed record TrustedItem(TrustedMember Member) : LocalizedRecord
{
    public string Label => (Member.Name.Length > 0 ? Member.Name : L.T("multiplayer.peers.player")) + " · " + Member.Fingerprint;
}

/// <summary>
/// 联机页的邀请加入：房主发 JAY2- 邀请码，朋友贴上后先进“大厅”敲门，房主批准才在加密通道里拿到房间的凭据。
/// 房间本身的网络名和密钥是随机的，从不显示、复制或保存。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private MemberIdentity? _identity;
    private TrustedMembers? _trusted;
    private HostLobby? _lobby;
    private RoomCredentials? _credentials;
    private MemberTable? _members;
    private CancellationTokenSource? _knockCts;
    private JayInvite? _lastInvite;

    /// <summary>这台电脑的联机身份（第一次用到时读取或新建）。</summary>
    internal MemberIdentity Identity
    {
        get
        {
            if (_identity is not null) return _identity;
            _identity = MemberIdentity.LoadOrCreate(AppSettings.DataDir);
            if (_identity.Warning is { } warning) SetStatus(new StatusMessage("common.raw", [warning], StatusKind.Warning));
            return _identity;
        }
    }

    private TrustedMembers Trusted => _trusted ??= CreateTrusted();

    private TrustedMembers CreateTrusted()
    {
        var trusted = new TrustedMembers(Path.Combine(AppSettings.DataDir, "trusted.json"));
        trusted.Changed += () => Dispatcher.UIThread.Post(RefreshTrusted);
        return trusted;
    }

    public string MyFingerprint => L.F("multiplayer.identity.mine", Identity.Fingerprint);

    public ObservableCollection<PendingInviteItem> PendingInvites { get; } = [];
    public ObservableCollection<ApprovalCard> ApprovalCards { get; } = [];
    public ObservableCollection<MemberItem> Members { get; } = [];
    public ObservableCollection<TrustedItem> TrustedPeople { get; } = [];
    public ObservableCollection<BannedItem> BannedPeople { get; } = [];

    /// <summary>房主正在踢人、换网络（这期间不能再踢）。</summary>
    [ObservableProperty] public partial bool IsRekeying { get; set; }

    /// <summary>测试用：当前房间网络的版本号。</summary>
    internal int RoomEpoch => _room?.Epoch ?? 0;

    /// <summary>测试用：换一个联机身份（自检里同一个进程开好几个成员，要各自不同的身份）。</summary>
    internal void UseTestIdentity(string directory) => _identity = MemberIdentity.LoadOrCreate(directory);

    public IReadOnlyList<InviteExpiryChoice> InviteExpiryChoices { get; } =
    [
        new(TimeSpan.FromMinutes(10), "multiplayer.invite.expiry.ten_minutes"),
        new(TimeSpan.FromHours(1), "multiplayer.invite.expiry.hour"),
        new(null, "multiplayer.invite.expiry.room"),
    ];

    [ObservableProperty] public partial bool InviteDialogOpen { get; set; }
    [ObservableProperty] public partial decimal? InviteUses { get; set; } = 1;
    [ObservableProperty] public partial InviteExpiryChoice? SelectedInviteExpiry { get; set; }
    [ObservableProperty] public partial bool InviteAutoApprove { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InviteDisplay), nameof(HasInvite))]
    public partial bool InviteShown { get; set; }

    public bool HasInvite => _lastInvite is not null;
    /// <summary>直连房间的房主发的是直连邀请码（JAY2D-，带着这张邀请和本机地址）；其他时候是普通邀请码。默认打码。</summary>
    public string InviteDisplay => _lastInvite is not { } invite ? ""
        : IsDirect && MyDirectCode.Length > 0 ? (InviteShown ? MyDirectCode : MyDirectCode[..6] + new string('•', 8) + MyDirectCode[^4..])
        : InviteShown ? invite.ToString() : invite.Masked;

    /// <summary>加入方：正在进大厅 / 敲门 / 等房主批准。</summary>
    [ObservableProperty] public partial bool IsKnocking { get; set; }

    /// <summary>加入方：房主的指纹（敲门握手里证明过的身份）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHostFingerprint), nameof(HostFingerprintText))] public partial string HostFingerprint { get; set; } = "";
    public bool HasHostFingerprint => HostFingerprint.Length > 0;
    public string HostFingerprintText => L.F("multiplayer.invite.host_fingerprint", HostFingerprint);

    /// <summary>测试用：最近一次发出的完整邀请码（界面上默认打码）。</summary>
    internal string? TestInviteText => _lastInvite?.ToString();

    private void RefreshTrusted()
    {
        TrustedPeople.Clear();
        foreach (var m in Trusted.All) TrustedPeople.Add(new TrustedItem(m));
    }

    private void RefreshInvites()
    {
        PendingInvites.Clear();
        if (_lobby is null) return;
        foreach (var invite in _lobby.Book.Pending) PendingInvites.Add(new PendingInviteItem(invite));
    }

    // ---- 房主 ----

    /// <summary>开房间时调用：生成这个房间的随机凭据。</summary>
    private RoomCredentials NewCredentials(int port, TunnelSettings tunnel) => RoomCredentials.Create(Identity.PublicKey, port, tunnel);

    private IReadOnlyList<string> LobbyPeers(bool host) =>
        [.. (host ? TestListeners ?? [] : []), .. TestPeers ?? _service.MultiplayerPeers];

    /// <summary>房间开好之后：准备大厅（有邀请时才真的启动）。</summary>
    /// <param name="book">直连房间：大厅密钥不换的邀请簿。</param>
    /// <param name="listenUdpPort">直连房间：大厅 EasyTier 在本机回环上监听的 UDP 端口（直连桥把没批准的加入方转发到这里）。</param>
    /// <param name="peers">大厅连哪些节点；直连房间只用直连的备用节点（可以没有），默认是房间的节点。</param>
    private void StartHostLobby(RoomCredentials credentials, MemberTable members, InviteBook? book = null, int? listenUdpPort = null, Func<IReadOnlyList<string>>? peers = null)
    {
        _credentials = credentials;
        _members = members;
        var lobby = HostLobby.Create(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier", "lobby"), peers ?? (() => LobbyPeers(host: true)),
            Identity.KeyPair, () => _credentials ?? throw new InvalidOperationException(), pub => Trusted.IsTrusted(pub), AskApprovalAsync, members,
            line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line), book, listenUdpPort);
        lobby.Book.Changed += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.StateChanged += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.Paused += () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Warning("multiplayer.invite.paused")));
        // 发出凭据时就列上（对方不回确认也已经能进房间）；确认后改成“已确认”。同一个人重新敲门进来时替换旧的那行。
        lobby.Issued += (request, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = Members.Count - 1; i >= 0; i--)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey)) Members.RemoveAt(i);
            Members.Insert(0, new MemberItem(request.Name, request.PublicKey, request.Trusted));
            SetStatus(StatusMessage.Success("multiplayer.invite.admitted", request.Name, request.Fingerprint));
        });
        // 凭据没发出去（连接断了）：钥匙已经撤回，列表里也去掉。
        lobby.Withdrawn += (request, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = Members.Count - 1; i >= 0; i--)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey) && !Members[i].Confirmed) Members.RemoveAt(i);
        });
        lobby.Admitted += request => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = 0; i < Members.Count; i++)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey)) Members[i] = Members[i] with { Confirmed = true };
        });
        _lobby = lobby;
    }

    private void StopHostLobby()
    {
        var lobby = _lobby;
        _lobby = null;
        _credentials = null;
        _members?.Clear();
        _members = null;
        _lastInvite = null;
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        InviteDialogOpen = false;
        foreach (var card in ApprovalCards) card.Decision.TrySetResult(false);
        ApprovalCards.Clear();
        PendingInvites.Clear();
        Members.Clear();
        BannedPeople.Clear();
        if (lobby is not null) _ = lobby.DisposeAsync().AsTask();
    }

    private void RefreshBanned()
    {
        BannedPeople.Clear();
        if (_members is null) return;
        foreach (var b in _members.Banned) BannedPeople.Add(new BannedItem(b));
    }

    /// <summary>
    /// 踢人：移出成员表（钥匙清零，他的游戏通道和聊天马上断开）、记进这个房间的禁入名单，然后换网络：
    /// 新的随机网络名和密钥（版本加一）经每个留下来的成员自己的通道发过去，等确认最多 5 秒，房主再切过去。旧网络不留。
    /// 当时不在线的成员收不到新网络，钥匙作废，界面上标“需要重新邀请”。
    /// </summary>
    [RelayCommand]
    private async Task KickMemberAsync(MemberItem? item)
    {
        if (item is null || IsRekeying || !IsHosting || _members is not { } members || _credentials is not { } credentials || _hub is not { } hub
            || _room is not { FromInvite: true } room) return;
        IsRekeying = true;
        try
        {
            // 连他进来用的那张邀请一起作废（禁入名单按公钥记，换个身份就能拿同一张再进来）。
            var inviteRevoked = false;
            if (members.FindGuest(item.PublicKey) is { } entry)
            {
                // 先告诉他“你已被移出房间”（他那边停止联机），再作废钥匙断开。
                await hub.NotifyKickedAsync(entry);
                inviteRevoked = members.Kick(entry, _lobby?.Book);
            }
            else members.Ban(item.PublicKey, item.Name);
            Members.Remove(item);
            RefreshInvites();
            RefreshBanned();
            var next = credentials.Rotate();
            _credentials = next;
            SetStatus(StatusMessage.Info("multiplayer.rekey.switching", item.Name));
            var (sent, _) = await hub.SendRekeyAsync(next.ToRekey(), TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(_room, room)) return;
            var missed = members.All.Where(e => !e.IsHost && !sent.Contains(e)).ToList();
            foreach (var e in missed) members.Remove(e);
            for (var i = 0; i < Members.Count; i++)
                if (missed.Any(e => e.PublicKey.AsSpan().SequenceEqual(Members[i].PublicKey))) Members[i] = Members[i] with { NeedsInvite = true };
            if (!SwitchNetwork(room.Rekeyed(next.ToRekey()))) return;
            SetStatus(missed.Count > 0 && inviteRevoked ? StatusMessage.Warning("multiplayer.kick.invite_revoked_missed", item.Name, missed.Count)
                : missed.Count > 0 ? StatusMessage.Warning("multiplayer.rekey.done_missed", item.Name, missed.Count)
                : inviteRevoked ? StatusMessage.Warning("multiplayer.kick.invite_revoked", item.Name)
                : StatusMessage.Success("multiplayer.rekey.done", item.Name));
        }
        finally
        {
            IsRekeying = false;
        }
    }

    [RelayCommand]
    private void Unban(BannedItem? item)
    {
        if (item is null || _members is not { } members) return;
        members.Unban(item.Member.PublicKey);
        RefreshBanned();
        SetStatus(StatusMessage.Info("multiplayer.kick.unbanned", item.Member.Name));
    }

    /// <summary>
    /// 换到新的房间网络：关掉旧的 EasyTier 和封装通道，按同样的设置用新网络名和密钥重开（旧网络不留）。
    /// 房主的聊天服务和本机聊天连接不动；加入方的聊天连接经旧网络，丢掉重连。
    /// </summary>
    private bool SwitchNetwork(RoomCode next)
    {
        if (_direct is { } direct)
        {
            // 直连房间：直连桥不动（钥匙和房间无关），只让直连会话换上新房间重启 EasyTier。
            _room = next;
            _ = SwitchDirectRoomAsync(direct, next);
            AfterNetworkSwitch();
            return true;
        }
        if (_session is not { } old) return false;
        var options = old.Options with { Room = next };
        StopTransport();
        old.Dispose();
        _session = null;
        _service.ActiveMultiplayer = null;
        try
        {
            var workDir = Path.Combine(AppSettings.DataDir, "easytier", options.Tun ? "run-lan" : "run");
            _session = options.Tun
                ? EasyTierElevatedSession.Start(_service.EasyTier, options, workDir)
                : EasyTierSession.Start(_service.EasyTier, options, workDir);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            StopSession();
            SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
            return false;
        }
        _service.ActiveMultiplayer = _session;
        if (!TryStartTransport(options))
        {
            StopSession();
            return false;
        }
        _room = next;
        AfterNetworkSwitch();
        return true;
    }

    private async Task SwitchDirectRoomAsync(DirectSession direct, RoomCode next)
    {
        try
        {
            await direct.SwitchRoomAsync(next);
            if (_direct == direct) _session = direct.EasyTier;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            if (_direct == direct) SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>换了网络之后：加入方的聊天连接经旧网络，丢掉重连。</summary>
    private void AfterNetworkSwitch()
    {
        if (IsJoined)
        {
            var chat = _chat;
            _chat = null;
            ChatReady = false;
            ChatStatus = L.T("multiplayer.chat.reconnecting");
            if (chat is not null) _ = chat.DisposeAsync().AsTask();
            MarkJoinStarted(newRoom: true);
        }
    }

    /// <summary>加入方：被房主踢出：停止联机（关掉 EasyTier 和聊天），告诉用户。</summary>
    private void OnKicked(ChatClient client)
    {
        if (_chat != client || !IsJoined || _room is not { FromInvite: true }) return;
        StopSession();
        SetStatus(StatusMessage.Warning("multiplayer.kick.you_were_kicked"));
    }

    /// <summary>加入方：房主换了网络（有人被踢）。经自己的成员通道收到、版本比当前大才换。</summary>
    private void OnRekey(ChatClient client, RoomRekey next)
    {
        if (_chat != client || !IsJoined || _room is not { FromInvite: true } room || next.Epoch <= room.Epoch) return;
        SetStatus(StatusMessage.Info("multiplayer.rekey.reconnecting"));
        SwitchNetwork(room.Rekeyed(next));
    }

    /// <summary>敲门的人通过了握手：在界面上放一张卡片，等房主点；超时或者房间关了算拒绝。</summary>
    private Task<bool> AskApprovalAsync(KnockRequest request, CancellationToken ct)
    {
        var sameName = Trusted.All.Any(m => m.Name.Length > 0 && m.Name == request.Name && !m.PublicKey.AsSpan().SequenceEqual(request.PublicKey));
        var card = new ApprovalCard(request, sameName ? L.T("multiplayer.invite.name_mismatch") : null);
        ct.Register(() => card.Decision.TrySetResult(false));
        Dispatcher.UIThread.Post(() =>
        {
            if (card.Decision.Task.IsCompleted) return;
            ApprovalCards.Add(card);
            if (!IsCurrent) Badge++;
            _ = card.Decision.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() => ApprovalCards.Remove(card)), TaskScheduler.Default);
        });
        return card.Decision.Task;
    }

    [RelayCommand]
    private void ApproveKnock(ApprovalCard? card) => card?.Decision.TrySetResult(true);

    [RelayCommand]
    private void DenyKnock(ApprovalCard? card) => card?.Decision.TrySetResult(false);

    [RelayCommand]
    private void OpenInviteDialog()
    {
        if (!IsHosting || _lobby is null) return;
        SelectedInviteExpiry ??= InviteExpiryChoices[0];
        InviteDialogOpen = true;
    }

    [RelayCommand]
    private void CloseInviteDialog() => InviteDialogOpen = false;

    [RelayCommand]
    private async Task CreateInviteAsync()
    {
        var uses = (int)Math.Clamp(InviteUses ?? 1, 1, 20);
        if (await IssueInviteAsync(uses, (SelectedInviteExpiry ?? InviteExpiryChoices[0]).Lifetime, InviteAutoApprove) is not null)
            SetStatus(StatusMessage.Success(IsDirect ? "multiplayer.direct.invite_created" : "multiplayer.invite.created"));
    }

    /// <summary>
    /// 发一张邀请、确保大厅开着。直连房间的房主同时生成直连邀请码（这张邀请 + 本机地址）。
    /// 大厅开不起来时撤掉这张邀请、显示原因，返回 null。
    /// </summary>
    private async Task<PendingInvite?> IssueInviteAsync(int uses, TimeSpan? lifetime, bool autoApprove)
    {
        if (_lobby is not { } lobby || _credentials is not { } credentials) return null;
        var invite = lobby.Book.Create(uses, lifetime, autoApprove, credentials.Tunnel);
        _lastInvite = invite.Invite;
        if (_direct is { IsHost: true } direct) MyDirectCode = direct.InviteCode(invite.Invite).Encode();
        InviteShown = false;
        DirectCodeShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        RefreshInvites();
        try
        {
            await lobby.UpdateAsync();
            return invite;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 敲门端口被占（比如另一个 Amber 正在当房主）：这张邀请没法用，撤掉。
            lobby.Book.Revoke(invite);
            _lastInvite = null;
            if (_direct is { IsHost: true }) MyDirectCode = "";
            OnPropertyChanged(nameof(InviteDisplay));
            OnPropertyChanged(nameof(HasInvite));
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
            return null;
        }
    }

    [RelayCommand]
    private void ToggleInviteShown() => InviteShown = !InviteShown;

    [RelayCommand]
    private async Task CopyInviteAsync()
    {
        if (_lastInvite is not { } invite) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(IsDirect && MyDirectCode.Length > 0 ? MyDirectCode : invite.ToString());
            SetStatus(StatusMessage.Success("multiplayer.invite.copied"));
        }
    }

    [RelayCommand]
    private void RevokeInvite(PendingInviteItem? item)
    {
        if (item is null || _lobby is not { } lobby) return;
        lobby.Book.Revoke(item.Invite);
        RefreshInvites();
        if (_lastInvite == item.Invite.Invite)
        {
            _lastInvite = null;
            if (_direct is { IsHost: true }) MyDirectCode = "";
            OnPropertyChanged(nameof(InviteDisplay));
            OnPropertyChanged(nameof(HasInvite));
        }
        SetStatus(StatusMessage.Info("multiplayer.invite.revoked"));
    }

    [RelayCommand]
    private void TrustMember(MemberItem? member)
    {
        if (member is null || member.Trusted) return;
        Trusted.Add(member.PublicKey, member.Name);
        RefreshTrusted();
        var index = Members.IndexOf(member);
        if (index >= 0) Members[index] = member with { Trusted = true };
        SetStatus(StatusMessage.Success("multiplayer.invite.trusted", member.Name, member.Fingerprint));
    }

    [RelayCommand]
    private void RemoveTrusted(TrustedItem? item)
    {
        if (item is null) return;
        Trusted.Remove(item.Member.PublicKey);
        RefreshTrusted();
    }

    [RelayCommand]
    private void ResetIdentity()
    {
        // 开着房间或在别人房间里时不许换（正在进行的握手和房主记住的指纹都基于当前身份）。
        if (!IsIdle || IsKnocking) return;
        Identity.Reset();
        OnPropertyChanged(nameof(MyFingerprint));
        SetStatus(StatusMessage.Success("multiplayer.identity.reset_done", Identity.Fingerprint));
    }

    // ---- 加入方 ----

    /// <summary>贴了邀请码：进大厅、敲门、等批准；批准后离开大厅，用拿到的凭据加入房间。</summary>
    private async Task KnockAndJoinAsync(JayInvite invite)
    {
        IsKnocking = true;
        var cts = new CancellationTokenSource();
        _knockCts = cts;
        SetStatus(StatusMessage.Info("multiplayer.invite.entering_lobby"));
        try
        {
            var me = Identity.KeyPair;
            var peers = LobbyPeers(host: false);
            var name = ChatName;
            var outcome = await Task.Run(() => LobbyJoin.KnockAsync(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier", "lobby-join"), peers, invite,
                me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line),
                () => Dispatcher.UIThread.Post(() =>
                {
                    if (_knockCts == cts) SetStatus(StatusMessage.Info("multiplayer.invite.waiting_approval"));
                }), cts.Token));
            if (_knockCts != cts) return;
            if (outcome.Credentials is not { } credentials)
            {
                SetStatus(StatusMessage.Warning(outcome.Reason switch
                {
                    "expired" => "multiplayer.invite.reason.expired",
                    "full" => "multiplayer.invite.reason.full",
                    _ => "multiplayer.invite.reason.denied",
                }));
                return;
            }
            if (credentials.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
            {
                SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
                return;
            }
            IsKnocking = false;
            if (StartJoin(credentials.ToRoom())) HostFingerprint = outcome.HostFingerprint;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus(StatusMessage.Info("multiplayer.invite.cancelled"));
        }
        catch (TimeoutException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException
            or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.invite.knock_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            if (_knockCts == cts)
            {
                _knockCts = null;
                IsKnocking = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelKnock() => _knockCts?.Cancel();
}
