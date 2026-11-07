using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 敲门端到端（只在本机回环上，不经过 EasyTier）：批准、拒绝、超时、令牌不对、过期、超过次数、撤销、失败太多暂停、超长帧、房主公钥固定。
/// </summary>
public sealed class KnockTests : IAsyncDisposable
{
    private DateTimeOffset _now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private readonly InviteBook _book;
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _guest = X25519KeyPair.Generate();
    private readonly List<KnockRequest> _asked = [];
    private readonly List<KnockRequest> _admitted = [];
    private Func<KnockRequest, CancellationToken, Task<bool>> _decide = (_, _) => Task.FromResult(true);
    private Func<byte[], bool> _trusted = _ => false;
    private RoomCredentials _credentials;
    private KnockServer? _server;

    private static readonly KnockLimits Fast = new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(3),
        DecisionTimeout = TimeSpan.FromMilliseconds(400),
    };

    public KnockTests()
    {
        _book = new InviteBook(() => _now);
        _credentials = RoomCredentials.Create(_host.PublicKey, 25565, new TunnelSettings(TransportMode.High, true));
    }

    private KnockServer Server(KnockLimits? limits = null)
    {
        if (_server is not null) return _server;
        _server = new KnockServer(_book, _host, () => _credentials, pub => _trusted(pub), (request, ct) =>
        {
            lock (_asked) _asked.Add(request);
            return _decide(request, ct);
        }, IPAddress.Loopback, 0, limits: limits ?? Fast);
        _server.Admitted += r =>
        {
            lock (_admitted) _admitted.Add(r);
        };
        return _server;
    }

    private async Task<KnockOutcome> KnockAsync(JayInvite invite, string name = "Steve", TimeSpan? decision = null)
    {
        var server = Server();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        return await KnockClient.KnockAsync(client.GetStream(), invite, _guest, name, "0.6.0", decisionTimeout: decision ?? TimeSpan.FromSeconds(5));
    }

    private PendingInvite Invite(int uses = 1, TimeSpan? life = null, bool auto = false) =>
        _book.Create(uses, life ?? TimeSpan.FromMinutes(10), auto, TunnelSettings.Default);

    [Fact]
    public async Task ApprovedGuestGetsCredentials()
    {
        var invite = Invite();
        var outcome = await KnockAsync(invite.Invite);
        Assert.True(outcome.Approved);
        var c = outcome.Credentials!;
        Assert.Equal(_credentials.Network, c.Network);
        Assert.Equal(_credentials.Secret, c.Secret);
        Assert.Equal(_host.PublicKey, c.HostPublicKey);
        Assert.Equal(_host.PublicKey, outcome.HostPublicKey);
        Assert.Equal(25565, c.GamePort);
        Assert.Equal(RoomChat.Port, c.ChatPort);
        Assert.Equal(new TunnelSettings(TransportMode.High, true), c.Tunnel);
        Assert.Matches("^jay-[0-9a-f]{16}$", c.Network);
        Assert.Equal(32, Convert.FromBase64String(c.Secret).Length);
        // 房主看到的是这位的名字和指纹（握手里证明过的长期公钥）。
        var asked = Assert.Single(_asked);
        Assert.Equal("Steve", asked.Name);
        Assert.Equal("0.6.0", asked.Version);
        Assert.Equal(MemberIdentity.FingerprintOf(_guest.PublicKey), asked.Fingerprint);
        Assert.True(await WaitAsync(() => _admitted.Count == 1));
        // 带着这个人自己的成员钥匙；房主的成员表里记着同一把，确认之后标“已确认”。
        Assert.NotNull(c.Member);
        Assert.True(Server().Members.TryGet(c.Member.Id, out var entry));
        Assert.Equal(c.Member.CopyPsk(), entry.Key.CopyPsk());
        Assert.Equal(MemberIdentity.FingerprintOf(_guest.PublicKey), entry.Fingerprint);
        Assert.Equal("Steve", entry.Name);
        Assert.True(await WaitAsync(() => entry.Confirmed));
        // 凭据建出的房间：EasyTier 用的就是这两个值；打印出来不含它们。
        var room = c.ToRoom();
        Assert.Equal(c.Network, room.EasyTierNetworkName);
        Assert.Equal(c.Secret, room.EasyTierSecret);
        Assert.DoesNotContain(c.Secret, room.ToString());
        Assert.DoesNotContain(c.Secret, c.ToString());
        Assert.Contains(c.Secret, room.Secrets);
        Assert.Equal(0, invite.UsesLeft);
    }

    [Fact]
    public async Task DeniedGuestGetsReason()
    {
        _decide = (_, _) => Task.FromResult(false);
        var outcome = await KnockAsync(Invite().Invite);
        Assert.False(outcome.Approved);
        Assert.Equal("denied", outcome.Reason);
        Assert.Empty(_admitted);
    }

    [Fact]
    public async Task UnansweredRequestTimesOutAsDenied()
    {
        var cancelled = false;
        _decide = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; }
            return true;
        };
        var outcome = await KnockAsync(Invite().Invite);
        Assert.Equal("denied", outcome.Reason);
        // 卡片收到了取消（界面据此把它收起）。
        Assert.True(await WaitAsync(() => cancelled));
    }

    [Fact]
    public async Task TrustedAndAutoApproveSkipTheQuestion()
    {
        _decide = (_, _) => Task.FromResult(false);
        _trusted = pub => pub.AsSpan().SequenceEqual(_guest.PublicKey);
        Assert.True((await KnockAsync(Invite().Invite)).Approved);
        _trusted = _ => false;
        Assert.True((await KnockAsync(Invite(auto: true).Invite)).Approved);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task WrongTokenIsDisconnected()
    {
        var real = Invite();
        var forged = real.Invite with { Token = RandomNumberGenerator.GetBytes(16) };
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(forged));
        Assert.Empty(_asked);
        Assert.Equal(1, real.UsesLeft);
    }

    /// <summary>编号对、令牌不对（psk 不对）：XXpsk0 的第一条消息房主就读不过，什么都不回（拿不到房主的长期公钥），也不算用掉。</summary>
    [Fact]
    public async Task WrongTokenFailsAtFirstMessage()
    {
        var real = Invite();
        var server = Server();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var wrongPsk = LobbyKeys.KnockPsk(RandomNumberGenerator.GetBytes(16));
        var id = LobbyKeys.InviteId(real.Invite.Token);
        using var handshake = new NoiseHandshake(NoisePattern.XXpsk0, true, KnockProtocol.Prologue(LobbyKeys.LobbyName(real.Invite.LobbyKey), id), wrongPsk, _guest);
        await KnockProtocol.WriteFrameAsync(stream, LobbyKeys.InviteId(real.Invite.Token), default);
        await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage([]), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await ReadToEndAsync(stream, timeout.Token));
        Assert.Empty(_asked);
        Assert.Equal(1, real.UsesLeft);
    }

    [Fact]
    public async Task WrongLobbyKeyFailsHandshake()
    {
        // 编号对、大厅密钥不对（prologue 不一样）：握手第二条消息就对不上。
        var real = Invite();
        var forged = real.Invite with { LobbyKey = RandomNumberGenerator.GetBytes(16) };
        await Assert.ThrowsAnyAsync<Exception>(() => KnockAsync(forged));
        Assert.Empty(_asked);
        Assert.Equal(1, real.UsesLeft);
    }

    [Fact]
    public async Task ExpiredInviteIsRefused()
    {
        var invite = Invite(life: TimeSpan.FromMinutes(10));
        Invite(life: null);
        _now += TimeSpan.FromMinutes(11);
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(invite.Invite));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task UsesAreLimited()
    {
        var invite = Invite(uses: 2);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(invite.Invite));
        Assert.Equal(2, _asked.Count);
        Assert.False(_book.HasPending);
    }

    [Fact]
    public async Task RevokedInviteStopsWorking()
    {
        var keep = Invite();
        var revoked = Invite();
        _book.Revoke(revoked);
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(revoked.Invite));
        // 最后一张也撤销了：大厅密钥丢掉，再敲直接断开。
        _book.Revoke(keep);
        Assert.Null(_book.Lobby);
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(keep.Invite));
    }

    [Fact]
    public async Task LobbyKeyChangesOnlyWhenNoInviteIsLeft()
    {
        var a = Invite();
        var b = Invite();
        Assert.Equal(a.Invite.LobbyKey, b.Invite.LobbyKey);
        _book.Revoke(a);
        _book.Revoke(b);
        var c = Invite();
        Assert.NotEqual(a.Invite.LobbyKey, c.Invite.LobbyKey);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TooManyFailuresPauseTheLobby()
    {
        var invite = Invite();
        var server = Server();
        var paused = false;
        server.Paused += () => paused = true;
        // 真的握手失败才算：编号对、psk 不对（乱报的编号见 UnknownInviteIdsDoNotPauseTheLobby）。
        for (var i = 0; i < 10; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            var id = LobbyKeys.InviteId(invite.Invite.Token);
            using var handshake = new NoiseHandshake(NoisePattern.XXpsk0, true, KnockProtocol.Prologue(LobbyKeys.LobbyName(invite.Invite.LobbyKey), id),
                LobbyKeys.KnockPsk(RandomNumberGenerator.GetBytes(16)), _guest);
            await KnockProtocol.WriteFrameAsync(stream, id, default);
            await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage([]), default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReadToEndAsync(stream, timeout.Token);
        }
        Assert.True(await WaitAsync(() => paused));
        Assert.True(server.IsPaused);
        // 暂停时连对的邀请也直接断开。
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(invite.Invite));
        Assert.Equal(1, invite.UsesLeft);
        _now += TimeSpan.FromSeconds(61);
        Assert.False(server.IsPaused);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
    }

    /// <summary>批准了、钥匙也发了，回复却写不出去（对方已经断开）：钥匙撤回，成员表里不留一个永远连不上的人。</summary>
    [Fact]
    public async Task FailedReplyWithdrawsTheIssuedKey()
    {
        var invite = Invite(auto: true);
        // 房主那边第二次写（加密的凭据回复）失败：第一次是握手消息 2。
        await using var server = new KnockServer(_book, _host, () => _credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, 0, limits: Fast)
        {
            StreamForTest = s => new FailingWrites(s, okWrites: 1),
        };
        var issued = 0;
        var withdrawn = 0;
        server.Issued += (_, _) => Interlocked.Increment(ref issued);
        server.Withdrawn += (_, _) => Interlocked.Increment(ref withdrawn);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await Assert.ThrowsAnyAsync<Exception>(() => KnockClient.KnockAsync(client.GetStream(), invite.Invite, _guest, "Gone", "0.6.0", decisionTimeout: TimeSpan.FromSeconds(5)));
        Assert.True(await WaitAsync(() => server.Active == 0));
        Assert.Equal(1, issued);
        Assert.Equal(1, withdrawn);
        Assert.Null(server.Members.FindGuest(_guest.PublicKey));
        Assert.Empty(server.Members.All);
    }

    /// <summary>前几次写照常，之后的写抛 IOException。</summary>
    private sealed class FailingWrites(Stream inner, int okWrites) : Stream
    {
        private int _writes;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            Interlocked.Increment(ref _writes) > okWrites ? throw new IOException("write failed (test)") : inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>本房间踢出的人拿着有效的新邀请再来敲门：不问房主、不回任何东西直接断开，也不用掉邀请。</summary>
    [Fact]
    public async Task BannedKeyCannotKnockAgain()
    {
        Assert.True((await KnockAsync(Invite().Invite)).Approved);
        var server = Server();
        var member = server.Members.FindGuest(_guest.PublicKey)!;
        server.Members.Ban(_guest.PublicKey, "Steve");
        server.Members.Remove(member);
        var fresh = Invite(auto: true);
        await Assert.ThrowsAnyAsync<IOException>(() => KnockAsync(fresh.Invite));
        Assert.Equal(1, fresh.UsesLeft);
        Assert.Single(_asked);
        Assert.Null(server.Members.FindGuest(_guest.PublicKey));
        Assert.False(server.IsPaused);
        // 解除之后可以再进。
        server.Members.Unban(_guest.PublicKey);
        Assert.True((await KnockAsync(fresh.Invite)).Approved);
    }

    /// <summary>两个人用同一张两次的邀请进来：各自一把不同的钥匙。</summary>
    [Fact]
    public async Task EachGuestGetsTheirOwnMemberKey()
    {
        var invite = Invite(uses: 2);
        var first = await KnockAsync(invite.Invite, "Steve");
        using var other = X25519KeyPair.Generate();
        var server = Server();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var second = await KnockClient.KnockAsync(client.GetStream(), invite.Invite, other, "Alex", "0.6.0", decisionTimeout: TimeSpan.FromSeconds(5));
        Assert.NotEqual(first.Credentials!.Member!.Id, second.Credentials!.Member!.Id);
        Assert.False(first.Credentials.Member.CopyPsk().AsSpan().SequenceEqual(second.Credentials.Member.CopyPsk()));
        // 房间级的部分（EasyTier 网络）是同一个。
        Assert.Equal(first.Credentials.Secret, second.Credentials.Secret);
        Assert.Equal(2, server.Members.All.Count);
    }

    /// <summary>
    /// 对方拿到凭据却不回确认（或者确认在路上丢了）：照样能用这把钥匙进房间，所以房主那边发出凭据时就要记上这个人。
    /// </summary>
    [Fact]
    public async Task GuestWhoNeverAcksStillShowsUpAsMember()
    {
        var invite = Invite(auto: true);
        var server = Server();
        var issued = new List<(KnockRequest Request, MemberEntry Member)>();
        server.Issued += (r, m) => { lock (issued) issued.Add((r, m)); };
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var id = LobbyKeys.InviteId(invite.Invite.Token);
        var psk = LobbyKeys.KnockPsk(invite.Invite.Token);
        using var handshake = new NoiseHandshake(NoisePattern.XXpsk0, true, KnockProtocol.Prologue(LobbyKeys.LobbyName(invite.Invite.LobbyKey), id), psk, _guest);
        await KnockProtocol.WriteFrameAsync(stream, id, default);
        await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage([]), default);
        handshake.ReadMessage(await KnockProtocol.ReadFrameAsync(stream, default));
        await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage("{\"v\":1,\"name\":\"Ghost\",\"ver\":\"0.6.0\"}"u8.ToArray()), default);
        var (send, receive) = handshake.Split();
        using (send)
        using (receive)
        {
            var sealedReply = await KnockProtocol.ReadFrameAsync(stream, default);
            var reply = KnockProtocol.ParseJson(receive.DecryptWithAd([], sealedReply));
            var credentials = RoomCredentials.FromJson(reply);
            // 不回确认，直接走。
            client.Dispose();
            Assert.True(await WaitAsync(() => { lock (issued) return issued.Count == 1; }));
            var (request, member) = issued[0];
            Assert.Equal("Ghost", request.Name);
            Assert.False(member.Confirmed);
            Assert.True(server.Members.TryGet(credentials.Member!.Id, out var entry));
            Assert.Same(member, entry);
            Assert.True(await WaitAsync(() => server.Active == 0));
            Assert.Empty(_admitted);
        }
    }

    /// <summary>乱报的邀请编号直接断开，不算失败次数：拿着大厅密钥的人或者本机别的程序不能借此让敲门一直暂停。</summary>
    [Fact]
    public async Task UnknownInviteIdsDoNotPauseTheLobby()
    {
        var invite = Invite();
        var server = Server();
        var paused = false;
        server.Paused += () => paused = true;
        for (var i = 0; i < 25; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            await KnockProtocol.WriteFrameAsync(stream, RandomNumberGenerator.GetBytes(KnockProtocol.IdLength), default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await ReadToEndAsync(stream, timeout.Token));
        }
        Assert.False(paused);
        Assert.False(server.IsPaused);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
    }

    /// <summary>prologue 里带着邀请编号：报的是这张邀请的编号、握手却按另一个编号做，第一条消息就失败。</summary>
    [Fact]
    public async Task PrologueBindsTheInviteId()
    {
        var invite = Invite();
        var server = Server();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var psk = LobbyKeys.KnockPsk(invite.Invite.Token);
        using var handshake = new NoiseHandshake(NoisePattern.XXpsk0, true,
            KnockProtocol.Prologue(LobbyKeys.LobbyName(invite.Invite.LobbyKey), RandomNumberGenerator.GetBytes(KnockProtocol.IdLength)), psk, _guest);
        await KnockProtocol.WriteFrameAsync(stream, LobbyKeys.InviteId(invite.Invite.Token), default);
        await KnockProtocol.WriteFrameAsync(stream, handshake.WriteMessage([]), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await ReadToEndAsync(stream, timeout.Token));
        Assert.Empty(_asked);
        Assert.Equal(1, invite.UsesLeft);
        // 两边按同一个编号做的照常通过。
        Assert.Equal((byte[])[.. "JAY-KNOCK-1x"u8, 1, 2], KnockProtocol.Prologue("x", [1, 2]));
    }

    [Fact]
    public async Task OversizedFrameIsRejected()
    {
        Invite();
        var server = Server();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 1, 2, 3 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await ReadToEndAsync(stream, timeout.Token));

        // 读的一方也拒绝超过 4 KB 的帧，不会照着长度去分配。
        using var big = new MemoryStream([0x10, 0x01, .. new byte[4097]]);
        await Assert.ThrowsAsync<InvalidDataException>(() => KnockProtocol.ReadFrameAsync(big, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => KnockProtocol.WriteFrameAsync(new MemoryStream(), new byte[4097], default));
    }

    [Fact]
    public async Task SilentClientIsDroppedAfterHandshakeTimeout()
    {
        Invite();
        await using var server = new KnockServer(_book, _host, () => _credentials, _ => false, (_, _) => Task.FromResult(true), IPAddress.Loopback, 0,
            limits: Fast with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await ReadToEndAsync(client.GetStream(), timeout.Token));
    }

    [Fact]
    public async Task HostPublicKeyIsPinned()
    {
        // 回复里写的房主公钥和握手里证明的不一样（比如被换掉的凭据）：加入方拒绝。
        using var other = X25519KeyPair.Generate();
        _credentials = _credentials with { HostPublicKey = other.PublicKey.ToArray() };
        await Assert.ThrowsAsync<CryptographicException>(() => KnockAsync(Invite().Invite));
        Assert.Empty(_admitted);
    }

    [Fact]
    public async Task NameIsSanitized()
    {
        await KnockAsync(Invite().Invite, "  ‮evil\nname" + new string('x', 60));
        var asked = Assert.Single(_asked);
        Assert.DoesNotContain('‮', asked.Name);
        Assert.DoesNotContain('\n', asked.Name);
        Assert.True(asked.Name.Length <= KnockProtocol.MaxNameLength);
        Assert.StartsWith("evilname", asked.Name);
    }

    [Fact]
    public void CredentialsJsonRejectsBadValues()
    {
        var withKey = _credentials with { Member = MemberKey.Create() };
        var json = withKey.ToJson();
        Assert.Equal(_credentials.Secret, RoomCredentials.FromJson(json).Secret);
        foreach (var (key, value) in new (string, System.Text.Json.Nodes.JsonNode)[] { ("net", "amber-x"), ("secret", "abc"), ("hostPub", "00"), ("epoch", 0) })
        {
            var bad = withKey.ToJson();
            bad[key] = value;
            Assert.Throws<InvalidDataException>(() => RoomCredentials.FromJson(bad));
        }
    }

    [Fact]
    public void TrustedListPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "amber-trusted-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var list = new TrustedMembers(path);
            list.Add(_guest.PublicKey.ToArray(), "Alex");
            Assert.True(new TrustedMembers(path).IsTrusted(_guest.PublicKey));
            Assert.Equal("Alex", Assert.Single(new TrustedMembers(path).All).Name);
            list.Remove(_guest.PublicKey.ToArray());
            Assert.False(new TrustedMembers(path).IsTrusted(_guest.PublicKey));
            File.WriteAllText(path, "{not json");
            Assert.Empty(new TrustedMembers(path).All);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<int> ReadToEndAsync(Stream stream, CancellationToken ct)
    {
        var total = 0;
        var buffer = new byte[256];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0) total += n;
        }
        catch (IOException) { }
        return total;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _host.Dispose();
        _guest.Dispose();
    }
}
