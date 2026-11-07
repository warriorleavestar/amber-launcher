using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 邀请房间的成员钥匙：游戏通道和聊天都先报成员编号、再用这个成员自己的 psk 握手；
/// 房主按成员表认人，聊天消息的发送者由房主盖章；房间级的共享密钥在邀请房间里不再使用。
/// </summary>
public sealed class MemberKeyTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();

    private static RoomCredentials Credentials(byte[] hostPub, TunnelSettings tunnel) => RoomCredentials.Create(hostPub, 25565, tunnel);

    /// <summary>房主的房间和一位成员的房间（成员的钥匙走一遍 JSON，和真的敲门回复一样）。</summary>
    private (RoomCode Host, MemberTable Table, RoomCode Guest, MemberEntry Entry) Rooms(TunnelSettings tunnel, X25519KeyPair guest, string name)
    {
        var credentials = Credentials(_host.PublicKey.ToArray(), tunnel);
        var table = new MemberTable();
        var host = credentials.ToHostRoom(table, "Host");
        var entry = table.Issue(guest.PublicKey, name);
        var sent = RoomCredentials.FromJson((credentials with { Member = entry.Key }).ToJson());
        return (host, table, sent.ToRoom(), entry);
    }

    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        return (a.GetStream(), b.GetStream(), a, b);
    }

    public static TheoryData<TransportMode, bool> Modes()
    {
        var data = new TheoryData<TransportMode, bool>();
        foreach (var mode in new[] { TransportMode.Minecraft, TransportMode.High, TransportMode.Extreme })
            foreach (var disguise in new[] { false, true })
                data.Add(mode, disguise);
        return data;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task TunnelUsesTheMembersOwnKey(TransportMode mode, bool disguise)
    {
        if (mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported) return;
        var (host, _, guest, _) = Rooms(new TunnelSettings(mode, disguise), _alice, "Alice");
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, host, mode, disguise);
            await using var client = await TunnelHandshake.ConnectAsync(c, guest, mode, disguise, TunnelService.Chat);
            var accepted = await accept;
            Assert.NotNull(accepted);
            Assert.Equal(TunnelService.Chat, accepted.Value.Service);
            await using var server = accepted.Value.Stream;
            await client.WriteAsync("ping"u8.ToArray());
            await client.FlushAsync();
            var buffer = new byte[4];
            await server.ReadExactlyAsync(buffer);
            Assert.Equal("ping"u8.ToArray(), buffer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownMemberIdIsRejected(bool disguise)
    {
        var (host, _, guest, _) = Rooms(new TunnelSettings(TransportMode.High, disguise), _alice, "Alice");
        var stranger = guest with { Member = MemberKey.Create() };
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, host, TransportMode.High, disguise);
            var connect = TunnelHandshake.ConnectAsync(c, stranger, TransportMode.High, disguise, TunnelService.Game);
            await Assert.ThrowsAsync<CryptographicException>(async () => await accept);
            b.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(async () => await connect);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownIdWithWrongPskIsRejected(bool disguise)
    {
        var (host, _, guest, entry) = Rooms(new TunnelSettings(TransportMode.High, disguise), _alice, "Alice");
        var forged = guest with { Member = new MemberKey(entry.Key.Id, RandomNumberGenerator.GetBytes(MemberKey.PskLength)) };
        var (c, s, a, b) = await PairAsync();
        using (a)
        using (b)
        {
            var accept = TunnelHandshake.AcceptAsync(s, host, TransportMode.High, disguise);
            var connect = TunnelHandshake.ConnectAsync(c, forged, TransportMode.High, disguise, TunnelService.Game);
            var done = await Task.WhenAny(accept, connect);
            // 伪装时房主先发握手消息 1，是加入方先解不开；不伪装时房主解不开第一条。两种都不能建立通道。
            await Assert.ThrowsAnyAsync<Exception>(async () => await done);
            a.Dispose();
            b.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(async () => await accept);
            await Assert.ThrowsAnyAsync<Exception>(async () => await connect);
        }
    }

    [Fact]
    public void TwoMembersGetDifferentKeys()
    {
        var table = new MemberTable();
        var a = table.Issue(_alice.PublicKey, "Alice");
        var b = table.Issue(_bob.PublicKey, "Bob");
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(a.Key.CopyPsk().AsSpan().SequenceEqual(b.Key.CopyPsk()));
        Assert.Equal(32, a.Key.CopyPsk().Length);
        Assert.True(table.TryGet(a.Key.Id, out var found) && found == a);
        Assert.Equal(2, table.All.Count);
    }

    [Fact]
    public void RejoiningReplacesTheOldKey()
    {
        var table = new MemberTable();
        var first = table.Issue(_alice.PublicKey, "Alice");
        var oldId = first.Key.Id.ToArray();
        var second = table.Issue(_alice.PublicKey, "Alice");
        Assert.False(table.TryGet(oldId, out _));
        Assert.True(table.TryGet(second.Key.Id, out _));
        Assert.Single(table.All);
        // 旧钥匙清零作废了：再也取不出 psk。
        Assert.True(first.Revoked);
        Assert.True(first.Key.IsZeroed);
        Assert.Throws<CryptographicException>(() => first.Key.CopyPsk());
    }

    [Fact]
    public void SharedRoomKeysAreNotUsedForInviteRooms()
    {
        var (host, _, guest, _) = Rooms(new TunnelSettings(TransportMode.High, false), _alice, "Alice");
        Assert.Throws<InvalidOperationException>(() => new ChatCipher(host));
        Assert.Throws<InvalidOperationException>(() => new ChatCipher(guest));
        Assert.Throws<InvalidOperationException>(() => TunnelHandshake.Psk(host));
        Assert.Throws<InvalidOperationException>(() => TunnelHandshake.Psk(guest));
        // AMB- 房间照旧。
        var old = RoomCode.Create(25565);
        _ = new ChatCipher(old);
        Assert.Equal(32, TunnelHandshake.Psk(old).Length);
    }

    [Fact]
    public void CredentialsCarryTheMemberKey()
    {
        var credentials = Credentials(_host.PublicKey.ToArray(), TunnelSettings.Default);
        Assert.Throws<InvalidOperationException>(() => credentials.ToJson());
        var key = MemberKey.Create();
        var json = (credentials with { Member = key }).ToJson();
        var back = RoomCredentials.FromJson(json);
        Assert.Equal(key.Id, back.Member!.Id);
        Assert.Equal(key.CopyPsk(), back.Member.CopyPsk());
        Assert.DoesNotContain(key.PskBase64, back.ToString());
        foreach (var (field, value) in new (string, JsonNode)[] { ("memberId", "xyz"), ("memberId", "0011"), ("memberPsk", "abc") })
        {
            var bad = (credentials with { Member = key }).ToJson();
            bad[field] = value;
            Assert.Throws<InvalidDataException>(() => RoomCredentials.FromJson(bad));
        }
        var missing = (credentials with { Member = key }).ToJson();
        missing.Remove("memberPsk");
        Assert.Throws<InvalidDataException>(() => RoomCredentials.FromJson(missing));
    }

    [Fact]
    public async Task ChatSenderIsStampedByTheHost()
    {
        var credentials = Credentials(_host.PublicKey.ToArray(), new TunnelSettings(TransportMode.High, false));
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        var alice = table.Issue(_alice.PublicKey, "Alice");
        var bob = table.Issue(_bob.PublicKey, "Bob");
        RoomCode RoomOf(MemberEntry e) => RoomCredentials.FromJson((credentials with { Member = e.Key }).ToJson()).ToRoom();

        await using var hub = new ChatHub(hostRoom, 0);
        await using var aliceClient = new ChatClient(RoomOf(alice), "Alice");
        await using var bobClient = new ChatClient(RoomOf(bob), "I am the host");
        var seen = new List<ChatMessage>();
        bobClient.Received += m => { lock (seen) seen.Add(m); };
        await bobClient.ConnectAsync("127.0.0.1", hub.Port);
        await aliceClient.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        // 正常消息：发送者是 Alice 的编号、名字和指纹。
        Assert.True(await aliceClient.SendAsync("hello"));
        // 冒充：Alice 自己写一个 Bob 的发送者、名字和指纹。房主不看这些字段。
        var fake = System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["k"] = 0, ["s"] = bob.Id, ["name"] = "Bob", ["fp"] = bob.Fingerprint, ["text"] = "I am Bob", ["ts"] = 0, ["n"] = "x",
        }.ToJsonString());
        await aliceClient.SendFrameAsync(fake);
        Assert.True(await WaitAsync(() => { lock (seen) return seen.Count(m => m.Kind == ChatKind.Message) == 2; }));
        lock (seen)
        {
            foreach (var m in seen.Where(m => m.Kind == ChatKind.Message))
            {
                Assert.Equal(alice.Id, m.SenderId);
                Assert.Equal("Alice", m.Name);
                Assert.Equal(alice.Fingerprint, m.Fingerprint);
            }
            Assert.Contains(seen, m => m.Text == "I am Bob");
            // Bob 自己说的名字也不算数：加入通知里的是房主表里的名字。
            Assert.DoesNotContain(seen, m => m.Name == "I am the host");
        }
        Assert.Equal(bob.Id, bobClient.SenderId);
    }

    [Fact]
    public async Task ChatRejectsUnknownMembers()
    {
        var credentials = Credentials(_host.PublicKey.ToArray(), TunnelSettings.Default);
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        await using var hub = new ChatHub(hostRoom, 0) { FirstFrameTimeout = TimeSpan.FromSeconds(2) };
        var strangerRoom = RoomCredentials.FromJson((credentials with { Member = MemberKey.Create() }).ToJson()).ToRoom();
        await using var stranger = new ChatClient(strangerRoom, "Mallory");
        // 编号不认识：房主不回握手、直接断开。
        await Assert.ThrowsAnyAsync<Exception>(() => stranger.ConnectAsync("127.0.0.1", hub.Port));
        Assert.Equal(0, hub.MemberCount);

        // 编号对、psk 不对也一样。
        var alice = table.Issue(_alice.PublicKey, "Alice");
        var forgedRoom = RoomCredentials.FromJson((credentials with { Member = new MemberKey(alice.Key.Id, RandomNumberGenerator.GetBytes(32)) }).ToJson()).ToRoom();
        await using var forged = new ChatClient(forgedRoom, "Mallory");
        await Assert.ThrowsAnyAsync<Exception>(() => forged.ConnectAsync("127.0.0.1", hub.Port));
        Assert.Equal(0, hub.MemberCount);
    }

    [Fact]
    public async Task HostsOwnChatClientUsesItsEntry()
    {
        var credentials = Credentials(_host.PublicKey.ToArray(), TunnelSettings.Default);
        var table = new MemberTable();
        var hostRoom = credentials.ToHostRoom(table, "Host");
        await using var hub = new ChatHub(hostRoom, 0);
        await using var self = new ChatClient(hostRoom, "Host");
        var seen = new List<ChatMessage>();
        self.Received += m => { lock (seen) seen.Add(m); };
        await self.ConnectAsync("127.0.0.1", hub.HostPort);
        Assert.True(await self.SendAsync("hi"));
        Assert.True(await WaitAsync(() => { lock (seen) return seen.Any(m => m.Kind == ChatKind.Message); }));
        lock (seen)
        {
            var m = seen.First(m => m.Kind == ChatKind.Message);
            Assert.True(self.IsMine(m));
            Assert.Equal(MemberIdentity.FingerprintOf(_host.PublicKey), m.Fingerprint);
        }
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

    public void Dispose()
    {
        _host.Dispose();
        _alice.Dispose();
        _bob.Dispose();
    }
}
