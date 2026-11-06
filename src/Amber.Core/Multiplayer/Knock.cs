using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 房主批准后发给加入方的房间凭据：EasyTier 网络名、256 位随机密钥（只用于 EasyTier）、端口、封装方式，
/// 以及这个人自己的成员编号和 psk（游戏通道和聊天用）。只在内存里，从不显示、复制或保存；ToString 不含任何值。
/// </summary>
public sealed record RoomCredentials(string Network, string Secret, int Epoch, byte[] HostPublicKey, int GamePort, int ChatPort, TunnelSettings Tunnel)
{
    /// <summary>这个成员的钥匙。房主手里的“房间级”凭据没有，批准某个人时才配上。</summary>
    public MemberKey? Member { get; init; }

    public static RoomCredentials Create(byte[] hostPublicKey, int gamePort, TunnelSettings tunnel) =>
        new("jay-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 1,
            (byte[])hostPublicKey.Clone(), gamePort, RoomChat.Port, tunnel);

    /// <summary>加入方：按凭据建出内部的房间对象。</summary>
    public RoomCode ToRoom() => RoomCode.FromCredentials(Network, Secret, GamePort, Tunnel, Member ?? throw new InvalidOperationException("no member key"));

    /// <summary>房主：建房间对象，带上成员表，并给自己（本机的聊天客户端）发一把钥匙。</summary>
    public RoomCode ToHostRoom(MemberTable members, string hostName)
    {
        var self = members.Issue(HostPublicKey, hostName, isHost: true);
        return RoomCode.FromCredentials(Network, Secret, GamePort, Tunnel, self.Key, members);
    }

    public override string ToString() => "RoomCredentials";

    internal JsonObject ToJson() => new()
    {
        ["ok"] = true,
        ["net"] = Network,
        ["secret"] = Secret,
        ["epoch"] = Epoch,
        ["hostPub"] = Convert.ToHexStringLower(HostPublicKey),
        ["ports"] = new JsonObject { ["game"] = GamePort, ["chat"] = ChatPort },
        ["tunnel"] = new JsonObject { ["mode"] = (int)Tunnel.Mode, ["disguise"] = Tunnel.Disguise },
        ["memberId"] = Member?.IdHex ?? throw new InvalidOperationException("no member key"),
        ["memberPsk"] = Member.PskBase64,
    };

    internal static RoomCredentials FromJson(JsonNode json)
    {
        var net = json["net"]?.GetValue<string>() ?? throw new InvalidDataException();
        var secret = json["secret"]?.GetValue<string>() ?? throw new InvalidDataException();
        var hostPub = json["hostPub"]?.GetValue<string>() ?? throw new InvalidDataException();
        var epoch = json["epoch"]?.GetValue<int>() ?? throw new InvalidDataException();
        var game = json["ports"]?["game"]?.GetValue<int>() ?? throw new InvalidDataException();
        var chat = json["ports"]?["chat"]?.GetValue<int>() ?? throw new InvalidDataException();
        var mode = json["tunnel"]?["mode"]?.GetValue<int>() ?? throw new InvalidDataException();
        var disguise = json["tunnel"]?["disguise"]?.GetValue<bool>() ?? throw new InvalidDataException();
        var memberId = json["memberId"]?.GetValue<string>() ?? throw new InvalidDataException();
        var memberPsk = json["memberPsk"]?.GetValue<string>() ?? throw new InvalidDataException();
        if (!KnockProtocol.MemberIdPattern().IsMatch(memberId) || !KnockProtocol.SecretPattern().IsMatch(memberPsk)) throw new InvalidDataException(L.T("core.mp.knock.badreply"));
        if (!KnockProtocol.NetworkPattern().IsMatch(net) || !KnockProtocol.SecretPattern().IsMatch(secret) || !KnockProtocol.KeyPattern().IsMatch(hostPub)
            || epoch < 1 || game is < 1 or > 65535 || chat is < 1 or > 65535 || mode is < 0 or > 3)
            throw new InvalidDataException(L.T("core.mp.knock.badreply"));
        var tunnel = mode == 0 ? TunnelSettings.Default : new TunnelSettings((TransportMode)mode, disguise);
        var psk = Convert.FromBase64String(memberPsk);
        try
        {
            return new RoomCredentials(net, secret, epoch, Convert.FromHexString(hostPub), game, chat, tunnel) { Member = new MemberKey(Convert.FromHexString(memberId), psk) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }
}

/// <summary>敲门协议的公共部分：2 字节长度前缀的帧（最大 4 KB）、握手的 prologue、名字的清理。</summary>
public static partial class KnockProtocol
{
    public const int Port = 47660;
    public const int MaxFrame = 4096;
    public const int MaxNameLength = 32;
    public const int IdLength = 8;

    /// <summary>"JAY-KNOCK-1" + 大厅名 + 8 字节邀请编号：握手同时确认了双方说的是同一张邀请。</summary>
    public static byte[] Prologue(string lobbyName, ReadOnlySpan<byte> inviteId) => [.. Encoding.ASCII.GetBytes("JAY-KNOCK-1" + lobbyName), .. inviteId];

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new InvalidDataException(L.T("core.mp.knock.toolarge"));
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        payload.Span.CopyTo(frame.AsSpan(2));
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length > MaxFrame) throw new InvalidDataException(L.T("core.mp.knock.toolarge"));
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    /// <summary>名字只留可见字符（去掉控制字符、换行、方向控制等格式字符），去掉首尾空白，最多 32 个字符（不切开代理对）。</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var builder = new StringBuilder(MaxNameLength);
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            if (builder.Length + rune.Utf16SequenceLength > MaxNameLength) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString().Trim();
    }

    internal static byte[] Json(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    internal static JsonNode ParseJson(byte[] bytes) =>
        JsonNode.Parse(bytes) ?? throw new InvalidDataException(L.T("core.mp.knock.badreply"));

    [GeneratedRegex("^jay-[0-9a-f]{16}$")]
    internal static partial Regex NetworkPattern();

    /// <summary>32 字节的标准 Base64（44 个字符，末尾一个 =）。</summary>
    [GeneratedRegex("^[A-Za-z0-9+/]{43}=$")]
    internal static partial Regex SecretPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    internal static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9a-f]{16}$")]
    internal static partial Regex MemberIdPattern();
}

/// <summary>敲门的结果：批准时带凭据；拒绝时带原因（denied / expired / full）。</summary>
public sealed record KnockOutcome(RoomCredentials? Credentials, string? Reason, byte[] HostPublicKey)
{
    public bool Approved => Credentials is not null;
    public string HostFingerprint => MemberIdentity.FingerprintOf(HostPublicKey);
}

/// <summary>加入方：经大厅连到房主的敲门端口，报上邀请编号，用 XXpsk0 握手证明自己拿着邀请，等房主批准。</summary>
public static class KnockClient
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    /// <summary>等房主点批准：房主那边 120 秒没点算拒绝，这边多等一会儿。</summary>
    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(135);

    public static async Task<KnockOutcome> KnockAsync(Stream stream, JayInvite invite, X25519KeyPair me, string name, string version,
        CancellationToken ct = default, TimeSpan? handshakeTimeout = null, TimeSpan? decisionTimeout = null)
    {
        var lobbyName = LobbyKeys.LobbyName(invite.LobbyKey);
        var psk = LobbyKeys.KnockPsk(invite.Token);
        var inviteId = LobbyKeys.InviteId(invite.Token);
        using var handshake = new NoiseHandshake(NoisePattern.XXpsk0, true, KnockProtocol.Prologue(lobbyName, inviteId), psk, me);
        CryptographicOperations.ZeroMemory(psk);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(handshakeTimeout ?? HandshakeTimeout);
            await KnockProtocol.WriteFrameAsync(stream, inviteId, timeout.Token);
            await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage([]), timeout.Token);
            var hello = KnockProtocol.ParseJson(handshake.ReadMessage(await KnockProtocol.ReadFrameAsync(stream, timeout.Token)));
            if (hello["v"]?.GetValue<int>() != 1) throw new InvalidDataException(L.T("core.mp.knock.badreply"));
            var about = new JsonObject { ["v"] = 1, ["name"] = KnockProtocol.SanitizeName(name), ["ver"] = version };
            await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage(KnockProtocol.Json(about)), timeout.Token);
        }
        var hostPub = handshake.RemoteStaticPublicKey ?? throw new CryptographicException();
        var (send, receive) = handshake.Split();
        using (send)
        using (receive)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(decisionTimeout ?? DecisionTimeout);
            var sealedReply = await KnockProtocol.ReadFrameAsync(stream, wait.Token);
            var reply = KnockProtocol.ParseJson(receive.DecryptWithAd([], sealedReply));
            if (reply["ok"]?.GetValue<bool>() != true)
            {
                var reason = reply["reason"]?.GetValue<string>() is var r && r is "denied" or "expired" or "full" ? r : "denied";
                return new KnockOutcome(null, reason, hostPub);
            }
            var credentials = RoomCredentials.FromJson(reply);
            // 只信握手里证明过的那把房主公钥：回复里写的必须就是它。
            if (!CryptographicOperations.FixedTimeEquals(credentials.HostPublicKey, hostPub)) throw new CryptographicException(L.T("core.mp.knock.hostmismatch"));
            using var ack = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ack.CancelAfter(handshakeTimeout ?? HandshakeTimeout);
            await KnockProtocol.WriteFrameAsync(stream, send.EncryptWithAd([], KnockProtocol.Json(new JsonObject { ["ack"] = true })), ack.Token);
            return new KnockOutcome(credentials, null, hostPub);
        }
    }
}

/// <summary>房主这次发出去的一张邀请。</summary>
public sealed class PendingInvite
{
    internal PendingInvite(JayInvite invite, int uses, DateTimeOffset? expiresAt, bool autoApprove)
    {
        Invite = invite;
        Id = LobbyKeys.InviteId(invite.Token);
        UsesLeft = uses;
        Uses = uses;
        ExpiresAt = expiresAt;
        AutoApprove = autoApprove;
    }

    public JayInvite Invite { get; }
    internal byte[] Id { get; }
    public int Uses { get; }
    public int UsesLeft { get; internal set; }
    public DateTimeOffset? ExpiresAt { get; }
    public bool AutoApprove { get; }
    public bool Revoked { get; internal set; }

    public bool IsUsable(DateTimeOffset now) => !Revoked && UsesLeft > 0 && (ExpiresAt is not { } at || now < at);
}

/// <summary>
/// 房主的邀请簿：所有还能用的邀请共用一把大厅密钥 L；一张能用的都没有了（用完、过期、撤销）就丢掉 L，
/// 下次发邀请时换一把新的——旧邀请码里的大厅从此不存在。
/// </summary>
public sealed class InviteBook(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private readonly List<PendingInvite> _invites = [];
    private byte[]? _lobbyKey;

    /// <summary>邀请增减、用掉、过期或大厅密钥换了的时候。</summary>
    public event Action? Changed;

    public PendingInvite Create(int uses, TimeSpan? lifetime, bool autoApprove, TunnelSettings tunnel)
    {
        if (uses is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(uses));
        PendingInvite invite;
        lock (_gate)
        {
            PruneLocked();
            _lobbyKey ??= RandomNumberGenerator.GetBytes(JayInvite.KeyLength);
            invite = new PendingInvite(JayInvite.Create(_lobbyKey, tunnel), uses, lifetime is { } l ? _now() + l : null, autoApprove);
            _invites.Add(invite);
        }
        Changed?.Invoke();
        return invite;
    }

    public void Revoke(PendingInvite invite)
    {
        lock (_gate)
        {
            invite.Revoked = true;
            PruneLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>还能用的邀请。</summary>
    public IReadOnlyList<PendingInvite> Pending
    {
        get
        {
            var now = _now();
            lock (_gate) return [.. _invites.Where(i => i.IsUsable(now))];
        }
    }

    public bool HasPending => Pending.Count > 0;

    /// <summary>当前大厅的网络名和密钥；没有能用的邀请时是 null。</summary>
    public (string Name, string Secret)? Lobby
    {
        get
        {
            lock (_gate) return _lobbyKey is { } key ? (LobbyKeys.LobbyName(key), LobbyKeys.LobbySecret(key)) : null;
        }
    }

    /// <summary>定时调用：过期的去掉；一张能用的都没有了就丢掉大厅密钥。有变化时返回 true（并触发 Changed）。</summary>
    public bool Tick()
    {
        bool changed;
        lock (_gate) changed = PruneLocked();
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>所有邀请作废（房间关了）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _invites.Clear();
            DropKeyLocked();
        }
        Changed?.Invoke();
    }

    private bool PruneLocked()
    {
        var now = _now();
        if (_invites.Any(i => i.IsUsable(now))) return false;
        var had = _lobbyKey is not null || _invites.Count > 0;
        _invites.Clear();
        DropKeyLocked();
        return had;
    }

    private void DropKeyLocked()
    {
        if (_lobbyKey is { } key) CryptographicOperations.ZeroMemory(key);
        _lobbyKey = null;
    }

    /// <summary>按敲门报上来的编号找邀请（包括已过期、已撤销但大厅还在的，这样能告诉对方“过期了”）。</summary>
    internal PendingInvite? Find(ReadOnlySpan<byte> id)
    {
        lock (_gate)
        {
            foreach (var invite in _invites)
                if (CryptographicOperations.FixedTimeEquals(invite.Id, id)) return invite;
            return null;
        }
    }

    /// <summary>握手第三条消息验证通过：用掉一次。用不了（过期、撤销、用完）时返回 false。</summary>
    internal bool TryConsume(PendingInvite invite)
    {
        bool ok;
        lock (_gate)
        {
            ok = invite.IsUsable(_now()) && _invites.Contains(invite);
            if (ok) invite.UsesLeft--;
        }
        if (ok) Changed?.Invoke();
        return ok;
    }

    internal DateTimeOffset Now => _now();
}

/// <summary>敲门的人：通过了握手、等房主批准。</summary>
public sealed record KnockRequest(string Name, byte[] PublicKey, string Version, bool Trusted, PendingInvite Invite)
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
}

/// <summary>敲门端的限制。默认值见协议说明；测试里改小。</summary>
public sealed record KnockLimits
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxConcurrent { get; init; } = 3;
    public int MaxFailuresPerMinute { get; init; } = 10;
    public TimeSpan PauseAfterFailures { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 房主的敲门服务：只在大厅里听 47660。流程：
/// 收 8 字节邀请编号（明文，不认识就断开）→ XXpsk0 握手（psk 由邀请令牌派生、第一条消息就混入，令牌不对第一条就失败；双方交换长期身份）→ 第三条消息验证通过时用掉一次邀请 →
/// 信任列表里的人或勾了“自动批准”的邀请直接批准，否则问房主（120 秒没点算拒绝）→ 加密发回房间凭据或拒绝原因 → 等对方确认。
/// 同时最多 3 个人在敲；一分钟里失败 10 次就暂停 60 秒。
/// </summary>
public sealed class KnockServer : IAsyncDisposable
{
    private readonly InviteBook _book;
    private readonly MemberTable _members;
    private readonly X25519KeyPair _identity;
    private readonly Func<RoomCredentials> _credentials;
    private readonly Func<byte[], bool> _isTrusted;
    private readonly Func<KnockRequest, CancellationToken, Task<bool>> _approve;
    private readonly Action<string>? _log;
    private readonly KnockLimits _limits;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Lock _gate = new();
    private readonly Task _loop;
    private int _active;
    private DateTimeOffset _pausedUntil;

    /// <summary>批准了，给这个人发了钥匙（发出凭据之前就触发：对方不回确认也已经拿到了，房主必须看得到他）。</summary>
    public event Action<KnockRequest, MemberEntry>? Issued;
    /// <summary>对方确认收到了凭据。</summary>
    public event Action<KnockRequest>? Admitted;

    /// <summary>批准时往这里发钥匙；房间的游戏通道和聊天按它认人。</summary>
    public MemberTable Members => _members;
    /// <summary>失败太多，暂停一会儿。</summary>
    public event Action? Paused;
    /// <summary>一次敲门结束了（不管结果）。</summary>
    public event Action? SessionEnded;

    /// <summary>正在进行的敲门（包括在等房主点批准的）。邀请用完后大厅要等它们结束再关。</summary>
    public int Active
    {
        get { lock (_gate) return _active; }
    }

    public int Port { get; }

    public KnockServer(InviteBook book, X25519KeyPair identity, Func<RoomCredentials> credentials, Func<byte[], bool> isTrusted,
        Func<KnockRequest, CancellationToken, Task<bool>> approve, IPAddress address, int port = KnockProtocol.Port, Action<string>? log = null, KnockLimits? limits = null,
        MemberTable? members = null)
    {
        _book = book;
        _members = members ?? new MemberTable();
        _identity = identity;
        _credentials = credentials;
        _isTrusted = isTrusted;
        _approve = approve;
        _log = log;
        _limits = limits ?? new KnockLimits();
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = AcceptLoopAsync();
    }

    public bool IsPaused
    {
        get { lock (_gate) return _book.Now < _pausedUntil; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            bool admit;
            lock (_gate) admit = _book.Now >= _pausedUntil && _active < _limits.MaxConcurrent && (_active += 1) > 0;
            if (!admit)
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client); }
                finally
                {
                    client.Dispose();
                    lock (_gate) _active--;
                    SessionEnded?.Invoke();
                }
            });
        }
    }

    private void Fail(string why)
    {
        _log?.Invoke(L.F("core.mp.knock.failed", why));
        var paused = false;
        lock (_gate)
        {
            var now = _book.Now;
            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(1)) _failures.Dequeue();
            if (_failures.Count >= _limits.MaxFailuresPerMinute)
            {
                _pausedUntil = now + _limits.PauseAfterFailures;
                _failures.Clear();
                paused = true;
            }
        }
        if (paused)
        {
            _log?.Invoke(L.T("core.mp.knock.paused"));
            Paused?.Invoke();
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var ct = _stop.Token;
        NoiseHandshake? handshake = null;
        PendingInvite? invite;
        JsonNode about;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_limits.HandshakeTimeout);
            try
            {
                var id = await KnockProtocol.ReadFrameAsync(stream, timeout.Token);
                invite = id.Length == KnockProtocol.IdLength ? _book.Find(id) : null;
                // 不认识、已过期、已用完或已撤销：都直接断开，不握手。不算失败次数：拿着大厅密钥乱报编号的人不能借此让敲门一直暂停。
                if (invite is null || !invite.IsUsable(_book.Now) || _book.Lobby is not { } lobby)
                {
                    _log?.Invoke(L.F("core.mp.knock.failed", L.T("core.mp.knock.unknown")));
                    return;
                }
                var psk = LobbyKeys.KnockPsk(invite.Invite.Token);
                handshake = new NoiseHandshake(NoisePattern.XXpsk0, false, KnockProtocol.Prologue(lobby.Name, invite.Id), psk, _identity);
                CryptographicOperations.ZeroMemory(psk);
                handshake.ReadMessage(await KnockProtocol.ReadFrameAsync(stream, timeout.Token));
                await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage(KnockProtocol.Json(new JsonObject { ["v"] = 1 })), timeout.Token);
                about = KnockProtocol.ParseJson(handshake.ReadMessage(await KnockProtocol.ReadFrameAsync(stream, timeout.Token)));
                if (about["v"]?.GetValue<int>() != 1) throw new InvalidDataException(L.T("core.mp.knock.badreply"));
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or CryptographicException or InvalidDataException
                or System.Text.Json.JsonException or InvalidOperationException or FormatException or OperationCanceledException or SocketException)
            {
                if (!ct.IsCancellationRequested) Fail(e is OperationCanceledException ? L.T("core.mp.knock.timeout") : e.Message);
                return;
            }

            var guestPub = handshake.RemoteStaticPublicKey!;
            var (send, receive) = handshake.Split();
            using (send)
            using (receive)
            {
                string? name = null, version = null;
                try
                {
                    name = KnockProtocol.SanitizeName(about["name"]?.GetValue<string>());
                    version = KnockProtocol.SanitizeName(about["ver"]?.GetValue<string>());
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException) { }
                var request = new KnockRequest(name ?? "", (byte[])guestPub.Clone(), version ?? "", _isTrusted(guestPub), invite);
                JsonObject reply;
                MemberEntry? member = null;
                if (!_book.TryConsume(invite)) reply = Reject("expired");
                else if (request.Trusted || invite.AutoApprove || await AskAsync(request, ct))
                {
                    member = _members.Issue(guestPub, request.Name);
                    reply = (_credentials() with { Member = member.Key }).ToJson();
                }
                else reply = Reject("denied");
                _log?.Invoke(L.F(member is not null ? "core.mp.knock.approved" : "core.mp.knock.rejected", request.Name, request.Fingerprint));
                // 发出去之前就记上：对方拿到凭据后不回确认也能进房间，房主得看得到他。
                if (member is not null) Issued?.Invoke(request, member);
                using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                answer.CancelAfter(_limits.HandshakeTimeout);
                await KnockProtocol.WriteFrameAsync(stream, send.EncryptWithAd([], KnockProtocol.Json(reply)), answer.Token);
                if (member is null) return;
                var sealedAck = await KnockProtocol.ReadFrameAsync(stream, answer.Token);
                var ack = KnockProtocol.ParseJson(receive.DecryptWithAd([], sealedAck));
                if (ack["ack"]?.GetValue<bool>() == true)
                {
                    _members.Confirm(member);
                    Admitted?.Invoke(request);
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or CryptographicException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) _log?.Invoke(L.F("core.mp.knock.failed", e.Message));
        }
        finally
        {
            handshake?.Dispose();
        }
    }

    private static JsonObject Reject(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    /// <summary>问房主；超时或者服务关了算拒绝。</summary>
    private async Task<bool> AskAsync(KnockRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_limits.DecisionTimeout);
        try
        {
            var decision = _approve(request, timeout.Token);
            var finished = await Task.WhenAny(decision, Task.Delay(Timeout.Infinite, timeout.Token));
            // 超时之后才给出的答案（包括被取消时回的 true）一律不算。
            if (finished != decision || timeout.IsCancellationRequested) return false;
            return await decision;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        _stop.Dispose();
    }
}
