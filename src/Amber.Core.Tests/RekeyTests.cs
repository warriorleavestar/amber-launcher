using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 踢人和换网络（只有邀请房间）：踢出的人马上断开、钥匙作废、本房间不许再敲门；
/// 新网络经留下来的成员各自的通道发过去，成员伪造不了，旧版本号不认；AMB- 房间不受影响。
/// </summary>
public sealed class RekeyTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();

    private sealed record Room(RoomCredentials Credentials, MemberTable Table, RoomCode Host);

    private Room HostRoom(TunnelSettings tunnel)
    {
        var credentials = RoomCredentials.Create(_host.PublicKey.ToArray(), 25565, tunnel);
        var table = new MemberTable();
        return new Room(credentials, table, credentials.ToHostRoom(table, "Host"));
    }

    private static RoomCode GuestRoom(Room room, MemberEntry entry, int? epoch = null)
    {
        var guest = RoomCredentials.FromJson((room.Credentials with { Member = entry.Key }).ToJson()).ToRoom();
        return epoch is { } e ? guest with { Epoch = e } : guest;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task KickedMemberIsCutOffOnTunnelAndChat()
    {
        var settings = new TunnelSettings(TransportMode.High, false);
        var room = HostRoom(settings);
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var aliceRoom = GuestRoom(room, alice);

        // 封装通道：房主那边回声。
        await using var server = new TunnelServer(room.Host, settings, 0, _ => async (stream, ct) =>
        {
            var buffer = new byte[64];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n), ct);
                await stream.FlushAsync(ct);
            }
        });
        var open = TunnelClient.Wrapped("127.0.0.1", server.Port, aliceRoom, settings, TunnelService.Game);
        await using var tunnel = await open(default);
        await tunnel.WriteAsync("hi"u8.ToArray());
        await tunnel.FlushAsync();
        var echo = new byte[2];
        await tunnel.ReadExactlyAsync(echo);

        // 聊天。
        await using var hub = new ChatHub(room.Host, 0);
        await using var chat = new ChatClient(aliceRoom, "Alice");
        var dropped = false;
        chat.Disconnected += () => dropped = true;
        await chat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));

        Assert.True(room.Table.Remove(alice));

        // 现有的连接马上断开。
        Assert.True(await WaitAsync(() => dropped));
        Assert.True(await WaitAsync(() => hub.MemberCount == 0));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var cut = false;
            try { cut = await tunnel.ReadAsync(new byte[16], timeout.Token) == 0; }
            catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException) { cut = true; }
            Assert.True(cut);
        }
        // 再连：编号不认识了。
        await Assert.ThrowsAnyAsync<Exception>(() => open(default));
        await using var again = new ChatClient(aliceRoom, "Alice");
        await Assert.ThrowsAnyAsync<Exception>(() => again.ConnectAsync("127.0.0.1", hub.Port));
        Assert.Equal(0, hub.MemberCount);
        // 钥匙清零作废了：再也取不出 psk。
        Assert.True(alice.Key.IsZeroed);
        Assert.Throws<CryptographicException>(() => alice.Key.CopyPsk());
    }

    /// <summary>同一个人重新被批准、换了新钥匙：旧钥匙作废，旧的聊天和封装连接马上断开（不会同时出现两个他）。</summary>
    [Fact]
    public async Task ReissuingClosesTheOldSessions()
    {
        var settings = new TunnelSettings(TransportMode.High, false);
        var room = HostRoom(settings);
        var first = room.Table.Issue(_alice.PublicKey, "Alice");
        var firstRoom = GuestRoom(room, first);
        await using var server = new TunnelServer(room.Host, settings, 0, _ => async (stream, ct) => await stream.CopyToAsync(stream, ct));
        await using var tunnel = await TunnelClient.Wrapped("127.0.0.1", server.Port, firstRoom, settings, TunnelService.Game)(default);
        await using var hub = new ChatHub(room.Host, 0);
        await using var chat = new ChatClient(firstRoom, "Alice");
        var dropped = false;
        chat.Disconnected += () => dropped = true;
        await chat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        Assert.False(first.Revoked);

        var second = room.Table.Issue(_alice.PublicKey, "Alice");
        Assert.True(first.Revoked);
        Assert.True(first.Revocation.IsCancellationRequested);
        Assert.False(second.Revoked);
        Assert.True(await WaitAsync(() => dropped));
        Assert.True(await WaitAsync(() => hub.MemberCount == 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cut = false;
        try { cut = await tunnel.ReadAsync(new byte[16], timeout.Token) == 0; }
        catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException) { cut = true; }
        Assert.True(cut);

        // 新钥匙照常能连。
        await using var fresh = new ChatClient(GuestRoom(room, second), "Alice");
        await fresh.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        // 房间结束：全部作废。
        room.Table.Clear();
        Assert.True(second.Revoked);
        Assert.True(await WaitAsync(() => hub.MemberCount == 0));
    }

    [Fact]
    public void HostRoomKeepsTheHostsOwnKey()
    {
        var room = HostRoom(TunnelSettings.Default);
        var self = room.Table.Host!;
        var again = room.Credentials.ToHostRoom(room.Table, "Host");
        Assert.Same(self.Key, again.Member);
        Assert.False(self.Revoked);
        Assert.Single(room.Table.All, e => e.IsHost);
        // 别的房主的凭据不能拿这张表。
        using var other = X25519KeyPair.Generate();
        Assert.Throws<InvalidOperationException>(() => (room.Credentials with { HostPublicKey = other.PublicKey.ToArray() }).ToHostRoom(room.Table, "X"));
    }

    /// <summary>留给房主的本机聊天端口只认房主自己的钥匙：成员拿自己的钥匙连这个端口被断开。</summary>
    [Fact]
    public async Task HostPortOnlyTakesTheHostsKey()
    {
        var room = HostRoom(TunnelSettings.Default);
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        await using var hub = new ChatHub(room.Host, 0) { FirstFrameTimeout = TimeSpan.FromSeconds(2) };
        await using var chat = new ChatClient(GuestRoom(room, alice), "Alice");
        var dropped = false;
        chat.Disconnected += () => dropped = true;
        try { await chat.ConnectAsync("127.0.0.1", hub.HostPort); }
        catch (Exception e) when (e is IOException or SocketException or CryptographicException) { dropped = true; }
        Assert.True(await WaitAsync(() => dropped));
        Assert.Equal(0, hub.MemberCount);
    }

    [Fact]
    public async Task RemainingMembersGetTheNewNetworkAndTheKickedOneDoesNot()
    {
        var room = HostRoom(TunnelSettings.Default);
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        await using var hub = new ChatHub(room.Host, 0);
        await using var aliceChat = new ChatClient(GuestRoom(room, alice), "Alice");
        await using var bobChat = new ChatClient(GuestRoom(room, bob), "Bob");
        var aliceGot = new List<RoomRekey>();
        var bobGot = new List<RoomRekey>();
        aliceChat.Rekeyed += r => { lock (aliceGot) aliceGot.Add(r); };
        bobChat.Rekeyed += r => { lock (bobGot) bobGot.Add(r); };
        await aliceChat.ConnectAsync("127.0.0.1", hub.Port);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        room.Table.Ban(_alice.PublicKey, "Alice");
        room.Table.Remove(alice);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        var next = room.Credentials.Rotate();
        Assert.Equal(2, next.Epoch);
        Assert.NotEqual(room.Credentials.Network, next.Network);
        Assert.NotEqual(room.Credentials.Secret, next.Secret);
        var (sent, acked) = await hub.SendRekeyAsync(next.ToRekey(), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { bob }, sent);
        Assert.Equal(new[] { bob }, acked);
        Assert.True(await WaitAsync(() => { lock (bobGot) return bobGot.Count == 1; }));
        Assert.Equal(next.Network, bobGot[0].Network);
        Assert.Equal(next.Secret, bobGot[0].Secret);
        Assert.Equal(2, bobGot[0].Epoch);
        Assert.Empty(aliceGot);
        Assert.DoesNotContain(next.Secret, bobGot[0].ToString());

        // 成员按新网络建出的房间：钥匙不变，版本加一。
        var bobRoom = GuestRoom(room, bob).Rekeyed(bobGot[0]);
        Assert.Equal(next.Network, bobRoom.EasyTierNetworkName);
        Assert.Equal(next.Secret, bobRoom.EasyTierSecret);
        Assert.Equal(bob.Key.Id, bobRoom.Member!.Id);
        Assert.Equal(2, bobRoom.Epoch);
    }

    [Fact]
    public async Task MemberCannotForgeARekey()
    {
        var room = HostRoom(TunnelSettings.Default);
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        await using var hub = new ChatHub(room.Host, 0);
        await using var aliceChat = new ChatClient(GuestRoom(room, alice), "Alice");
        await using var bobChat = new ChatClient(GuestRoom(room, bob), "Bob");
        var bobGot = 0;
        var bobSaw = new List<ChatMessage>();
        bobChat.Rekeyed += _ => Interlocked.Increment(ref bobGot);
        bobChat.Received += m => { lock (bobSaw) bobSaw.Add(m); };
        await aliceChat.ConnectAsync("127.0.0.1", hub.Port);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        // Alice 自己拼一个换网络的控制帧发给房主，想让 Bob 换到她的网络。
        var forged = new RoomRekey("jay-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 9);
        await aliceChat.SendFrameAsync(KnockProtocol.Json(forged.ToJson()));
        // 正文里塞一个也没用：房主转发的是重新编码的消息，不带控制字段。
        var inText = forged.ToJson();
        inText["k"] = 0;
        inText["text"] = "smuggled";
        await aliceChat.SendFrameAsync(KnockProtocol.Json(inText));
        Assert.True(await aliceChat.SendAsync("after"));
        Assert.True(await WaitAsync(() => { lock (bobSaw) return bobSaw.Any(m => m.Text == "after"); }));
        Assert.Equal(0, bobGot);
        lock (bobSaw) Assert.DoesNotContain(bobSaw, m => m.Text == "smuggled");
        // 成员回的 ack 也不会被当成房主的消息转发。
        await aliceChat.SendFrameAsync(ChatWire.RekeyAck(2));
        Assert.True(await aliceChat.SendAsync("after2"));
        Assert.True(await WaitAsync(() => { lock (bobSaw) return bobSaw.Any(m => m.Text == "after2"); }));
        Assert.Equal(0, bobGot);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public async Task StaleOrEqualEpochIsIgnored(int current, int offered)
    {
        var room = HostRoom(TunnelSettings.Default);
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        await using var hub = new ChatHub(room.Host, 0);
        await using var bobChat = new ChatClient(GuestRoom(room, bob, current), "Bob");
        var got = 0;
        bobChat.Rekeyed += _ => Interlocked.Increment(ref got);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        var stale = new RoomRekey("jay-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), offered);
        var (sent, acked) = await hub.SendRekeyAsync(stale, TimeSpan.FromMilliseconds(800));
        Assert.Single(sent);
        Assert.Empty(acked);
        Assert.Equal(0, got);
        Assert.Throws<InvalidOperationException>(() => GuestRoom(room, bob, current).Rekeyed(stale));
        // 版本号大一的照常认。
        var fresh = stale with { Epoch = current + 1 };
        var (_, ok) = await hub.SendRekeyAsync(fresh, TimeSpan.FromSeconds(5));
        Assert.Single(ok);
        // 成员先回 ack 再触发 Rekeyed，房主收到 ack 时事件可能还没跑完，所以等一下。
        Assert.True(await WaitAsync(() => Volatile.Read(ref got) == 1));
    }

    [Fact]
    public void RekeyJsonIsValidated()
    {
        var good = new RoomRekey("jay-0123456789abcdef", Convert.ToBase64String(new byte[32]), 2).ToJson();
        Assert.NotNull(RoomRekey.FromJson(good));
        foreach (var (field, value) in new (string, JsonNode)[] { ("net", "amber-x"), ("secret", "abc"), ("epoch", 1), ("epoch", "2") })
        {
            var bad = new RoomRekey("jay-0123456789abcdef", Convert.ToBase64String(new byte[32]), 2).ToJson();
            bad[field] = value;
            Assert.Null(RoomRekey.FromJson(bad));
        }
    }

    [Fact]
    public async Task AmbRoomsAreUnaffected()
    {
        var old = RoomCode.Create(25565);
        Assert.Throws<InvalidOperationException>(() => old.Rekeyed(new RoomRekey("jay-0123456789abcdef", Convert.ToBase64String(new byte[32]), 2)));
        await using var hub = new ChatHub(old, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SendRekeyAsync(new RoomRekey("jay-0123456789abcdef", Convert.ToBase64String(new byte[32]), 2), TimeSpan.FromSeconds(1)));
        // 老房间的聊天照常（共享密钥、成员自己报 id）。
        await using var a = new ChatClient(old, "A");
        await using var b = new ChatClient(old, "B");
        var seen = new List<ChatMessage>();
        var rekeyed = 0;
        b.Received += m => { lock (seen) seen.Add(m); };
        b.Rekeyed += _ => rekeyed++;
        await a.ConnectAsync("127.0.0.1", hub.Port);
        await b.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));
        Assert.True(await a.SendAsync("hello"));
        Assert.True(await WaitAsync(() => { lock (seen) return seen.Any(m => m.Text == "hello" && m.SenderId == a.SenderId); }));
        Assert.Equal(0, rekeyed);
    }

    public void Dispose()
    {
        _host.Dispose();
        _alice.Dispose();
        _bob.Dispose();
    }
}
