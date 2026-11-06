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

/// <summary>经邀请进来的成员（房主看）。</summary>
public sealed record MemberItem(string Name, byte[] PublicKey, bool Trusted) : LocalizedRecord
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
    public string Label => (Name.Length > 0 ? Name : L.T("multiplayer.peers.player")) + " · " + Fingerprint;
    public bool CanTrust => !Trusted;
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
    public string InviteDisplay => _lastInvite is not { } invite ? "" : InviteShown ? invite.ToString() : invite.Masked;

    /// <summary>加入方：正在进大厅 / 敲门 / 等房主批准。</summary>
    [ObservableProperty] public partial bool IsKnocking { get; set; }

    /// <summary>加入方：房主的指纹（敲门握手里证明过的身份）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHostFingerprint))] public partial string HostFingerprint { get; set; } = "";
    public bool HasHostFingerprint => HostFingerprint.Length > 0;

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
    private void StartHostLobby(RoomCredentials credentials)
    {
        _credentials = credentials;
        var lobby = HostLobby.Create(_service.EasyTier, Path.Combine(AppSettings.DataDir, "easytier", "lobby"), () => LobbyPeers(host: true),
            Identity.KeyPair, () => _credentials ?? throw new InvalidOperationException(), pub => Trusted.IsTrusted(pub), AskApprovalAsync,
            line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line));
        lobby.Book.Changed += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.StateChanged += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.Paused += () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Warning("multiplayer.invite.paused")));
        lobby.Admitted += request => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            Members.Insert(0, new MemberItem(request.Name, request.PublicKey, request.Trusted));
            SetStatus(StatusMessage.Success("multiplayer.invite.admitted", request.Name, request.Fingerprint));
        });
        _lobby = lobby;
    }

    private void StopHostLobby()
    {
        var lobby = _lobby;
        _lobby = null;
        _credentials = null;
        _lastInvite = null;
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        InviteDialogOpen = false;
        foreach (var card in ApprovalCards) card.Decision.TrySetResult(false);
        ApprovalCards.Clear();
        PendingInvites.Clear();
        Members.Clear();
        if (lobby is not null) _ = lobby.DisposeAsync().AsTask();
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
        if (_lobby is not { } lobby || _credentials is not { } credentials) return;
        var uses = (int)Math.Clamp(InviteUses ?? 1, 1, 20);
        var invite = lobby.Book.Create(uses, (SelectedInviteExpiry ?? InviteExpiryChoices[0]).Lifetime, InviteAutoApprove, credentials.Tunnel);
        _lastInvite = invite.Invite;
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        try
        {
            await lobby.UpdateAsync();
            SetStatus(StatusMessage.Success("multiplayer.invite.created"));
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 敲门端口被占（比如另一个 Amber 正在当房主）：这张邀请没法用，撤掉。
            lobby.Book.Revoke(invite);
            _lastInvite = null;
            OnPropertyChanged(nameof(InviteDisplay));
            OnPropertyChanged(nameof(HasInvite));
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
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
            await clipboard.SetTextAsync(invite.ToString());
            SetStatus(StatusMessage.Success("multiplayer.invite.copied"));
        }
    }

    [RelayCommand]
    private void RevokeInvite(PendingInviteItem? item)
    {
        if (item is null || _lobby is not { } lobby) return;
        lobby.Book.Revoke(item.Invite);
        if (_lastInvite == item.Invite.Invite)
        {
            _lastInvite = null;
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
        var index = Members.IndexOf(member);
        if (index >= 0) Members[index] = member with { Trusted = true };
        SetStatus(StatusMessage.Success("multiplayer.invite.trusted", member.Name, member.Fingerprint));
    }

    [RelayCommand]
    private void RemoveTrusted(TrustedItem? item)
    {
        if (item is null) return;
        Trusted.Remove(item.Member.PublicKey);
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
