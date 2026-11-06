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
        Assert.Equal(64, c.Secret.Length);
        // 房主看到的是这位的名字和指纹（握手里证明过的长期公钥）。
        var asked = Assert.Single(_asked);
        Assert.Equal("Steve", asked.Name);
        Assert.Equal("0.6.0", asked.Version);
        Assert.Equal(MemberIdentity.FingerprintOf(_guest.PublicKey), asked.Fingerprint);
        Assert.True(await WaitAsync(() => _admitted.Count == 1));
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
        var outcome = await KnockAsync(invite.Invite);
        Assert.Equal("expired", outcome.Reason);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task UsesAreLimited()
    {
        var invite = Invite(uses: 2);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
        Assert.True((await KnockAsync(invite.Invite)).Approved);
        Assert.Equal("expired", (await KnockAsync(invite.Invite)).Reason);
        Assert.Equal(2, _asked.Count);
        Assert.False(_book.HasPending);
    }

    [Fact]
    public async Task RevokedInviteStopsWorking()
    {
        var keep = Invite();
        var revoked = Invite();
        _book.Revoke(revoked);
        Assert.Equal("expired", (await KnockAsync(revoked.Invite)).Reason);
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
        var forged = invite.Invite with { Token = RandomNumberGenerator.GetBytes(16) };
        for (var i = 0; i < 10; i++)
        {
            try { await KnockAsync(forged); }
            catch (Exception e) when (e is IOException or SocketException) { }
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
        var json = _credentials.ToJson();
        Assert.Equal(_credentials.Secret, RoomCredentials.FromJson(json).Secret);
        foreach (var (key, value) in new (string, System.Text.Json.Nodes.JsonNode)[] { ("net", "amber-x"), ("secret", "abc"), ("hostPub", "00"), ("epoch", 0) })
        {
            var bad = _credentials.ToJson();
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
