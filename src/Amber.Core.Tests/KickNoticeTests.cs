using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// 踢人通知：房主发 "kicked" 后等被踢的人回 "kicked-ack" 再断开，保证他那边看得到“你已被移出房间”；
/// 被踢之后的断开不再报“掉线”；别人回的确认不算数，也不会被转发。
/// 事件都用 WaitAsync 等，不靠固定延迟。
/// </summary>
public sealed class KickNoticeTests : IDisposable
{
    private readonly X25519KeyPair _host = X25519KeyPair.Generate();
    private readonly X25519KeyPair _alice = X25519KeyPair.Generate();
    private readonly X25519KeyPair _bob = X25519KeyPair.Generate();

    private sealed record Room(RoomCredentials Credentials, MemberTable Table, RoomCode Host);

    private Room HostRoom()
    {
        var credentials = RoomCredentials.Create(_host.PublicKey.ToArray(), 25565, TunnelSettings.Default);
        var table = new MemberTable();
        return new Room(credentials, table, credentials.ToHostRoom(table, "Host"));
    }

    private static RoomCode GuestRoom(Room room, MemberEntry entry) =>
        RoomCredentials.FromJson((room.Credentials with { Member = entry.Key }).ToJson()).ToRoom();

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
    public async Task AckedKickReturnsTrueAndShowsKickedNotDisconnected()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        await using var hub = new ChatHub(room.Host, 0);
        await using var chat = new ChatClient(GuestRoom(room, alice), "Alice");
        var kickedCount = 0;
        var kicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.Kicked += () =>
        {
            Interlocked.Increment(ref kickedCount);
            kicked.TrySetResult();
        };
        chat.Disconnected += () => disconnected.TrySetResult();
        await chat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));

        Assert.True(await hub.NotifyKickedAsync(alice, TimeSpan.FromSeconds(10)));
        // 确认是在 Kicked 之前发的，但事件经线程池触发，这里等它到。
        await kicked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 作废钥匙断开：之后不该再报掉线。
        room.Table.Kick(alice, null);
        Assert.True(alice.Revoked);
        Assert.True(await WaitAsync(() => hub.MemberCount == 0));
        var dropped = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(1))) == disconnected.Task;
        Assert.False(dropped);
        Assert.Equal(1, kickedCount);
    }

    [Fact]
    public async Task NoAckTimesOutAndReturnsFalse()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        await using var hub = new ChatHub(room.Host, 0);
        // 模拟旧客户端：收到通知，但不回确认。
        await using var chat = new ChatClient(GuestRoom(room, alice), "Alice") { SkipKickAckForTest = true };
        var kicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.Kicked += () => kicked.TrySetResult();
        await chat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await hub.NotifyKickedAsync(alice));
        watch.Stop();
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        await kicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OfflineMemberReturnsFalseRightAway()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        await using var hub = new ChatHub(room.Host, 0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await hub.NotifyKickedAsync(alice));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    /// <summary>Bob 冒充 Alice 回确认：不算数（房主照样等到超时），也不会被转发给 Alice，Alice 和 Bob 都不会被当成被踢。</summary>
    [Fact]
    public async Task OtherMembersAckDoesNotCountAndIsNotForwarded()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        await using var hub = new ChatHub(room.Host, 0);
        await using var aliceChat = new ChatClient(GuestRoom(room, alice), "Alice") { SkipKickAckForTest = true };
        await using var bobChat = new ChatClient(GuestRoom(room, bob), "Bob");
        var aliceKicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobKicked = 0;
        var aliceSaw = new List<ChatMessage>();
        aliceChat.Kicked += () => aliceKicked.TrySetResult();
        aliceChat.Received += m => { lock (aliceSaw) aliceSaw.Add(m); };
        bobChat.Kicked += () => Interlocked.Increment(ref bobKicked);
        await aliceChat.ConnectAsync("127.0.0.1", hub.Port);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        var notify = hub.NotifyKickedAsync(alice, TimeSpan.FromSeconds(2));
        await aliceKicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bobChat.SendFrameAsync(ChatWire.KickedAck());
        await bobChat.SendFrameAsync(ChatWire.KickedAck());
        Assert.False(await notify);

        // 控制帧不转发：Alice 只收到 Bob 后面那条正常消息。
        Assert.True(await bobChat.SendAsync("after"));
        Assert.True(await WaitAsync(() => { lock (aliceSaw) return aliceSaw.Any(m => m.Text == "after"); }));
        lock (aliceSaw) Assert.All(aliceSaw, m => Assert.True(m.Kind is ChatKind.Join or ChatKind.Message, m.Kind.ToString()));
        Assert.Equal(0, bobKicked);
        Assert.Equal(2, hub.MemberCount);
    }

    /// <summary>成员自己发 "kicked" 给房主：房主不理，也不转发给别人。</summary>
    [Fact]
    public async Task MemberCannotForgeAKickForOthers()
    {
        var room = HostRoom();
        var alice = room.Table.Issue(_alice.PublicKey, "Alice");
        var bob = room.Table.Issue(_bob.PublicKey, "Bob");
        await using var hub = new ChatHub(room.Host, 0);
        await using var aliceChat = new ChatClient(GuestRoom(room, alice), "Alice");
        await using var bobChat = new ChatClient(GuestRoom(room, bob), "Bob");
        var bobKicked = 0;
        var bobSaw = new List<ChatMessage>();
        bobChat.Kicked += () => Interlocked.Increment(ref bobKicked);
        bobChat.Received += m => { lock (bobSaw) bobSaw.Add(m); };
        await aliceChat.ConnectAsync("127.0.0.1", hub.Port);
        await bobChat.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        await aliceChat.SendFrameAsync(ChatWire.Kicked());
        Assert.True(await aliceChat.SendAsync("after"));
        Assert.True(await WaitAsync(() => { lock (bobSaw) return bobSaw.Any(m => m.Text == "after"); }));
        Assert.Equal(0, bobKicked);
    }

    public void Dispose()
    {
        _host.Dispose();
        _alice.Dispose();
        _bob.Dispose();
    }
}
