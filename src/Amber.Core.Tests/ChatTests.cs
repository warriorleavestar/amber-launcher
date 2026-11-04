using System.Collections.Concurrent;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class ChatTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);

    [Fact]
    public void Cipher_RoundTripAndOpaque()
    {
        var cipher = new ChatCipher(Room);
        var message = new ChatMessage(ChatKind.Message, "abc", "小明", "你好 <b>world</b>", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var frame = cipher.Seal(message);
        Assert.DoesNotContain("你好", Encoding.UTF8.GetString(frame));
        Assert.DoesNotContain("world", Encoding.UTF8.GetString(frame));
        Assert.Equal(message, cipher.Open(frame));
        // 同一条消息两次加密结果不同（随机 nonce）。
        Assert.NotEqual(frame, cipher.Seal(message));
    }

    [Fact]
    public void Cipher_RejectsWrongKeyTamperingAndSenderSwap()
    {
        var cipher = new ChatCipher(Room);
        var frame = cipher.Seal(new ChatMessage(ChatKind.Message, "abc", "a", "secret", DateTimeOffset.UtcNow));
        Assert.Null(new ChatCipher(Room with { Secret = "4HZP8RW2N6TD" }).Open(frame));
        Assert.Null(new ChatCipher(Room with { Network = "K7Q2M9XE" }).Open(frame));

        var text = Encoding.UTF8.GetString(frame);
        // 改发送者（附加认证数据）就认证失败。
        Assert.Null(cipher.Open(Encoding.UTF8.GetBytes(text.Replace("\"s\":\"abc\"", "\"s\":\"abd\""))));
        // 改密文的一个字节也失败。
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(frame)!;
        var data = Convert.FromBase64String(envelope["c"]!.GetValue<string>());
        data[0] ^= 1;
        envelope["c"] = Convert.ToBase64String(data);
        Assert.Null(cipher.Open(Encoding.UTF8.GetBytes(envelope.ToJsonString())));
        Assert.Null(cipher.Open("garbage"u8));
    }

    [Fact]
    public void Clean_PlainTextLimits()
    {
        Assert.Equal("ab\ncd", RoomChat.Clean("a\u0007b\ncd\u0000", 100));
        Assert.Equal(500, RoomChat.Clean(new string('x', 2000), RoomChat.MaxTextLength).Length);
    }

    [Fact]
    public void RateLimiter_Window()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new RateLimiter(3, TimeSpan.FromSeconds(5), () => now);
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        now = now.AddSeconds(5);
        Assert.True(limiter.TryAcquire());
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++) await Task.Delay(20);
        return condition();
    }

    private static (ChatClient Client, ConcurrentQueue<ChatMessage> Inbox) NewClient(string name, RoomCode? room = null)
    {
        var client = new ChatClient(room ?? Room, name);
        var inbox = new ConcurrentQueue<ChatMessage>();
        client.Received += inbox.Enqueue;
        return (client, inbox);
    }

    [Fact]
    public async Task Hub_RelaysToAllMembersWithJoinAndLeave()
    {
        await using var hub = new ChatHub(Room, 0);
        var (a, aInbox) = NewClient("房主");
        var (b, bInbox) = NewClient("朋友");
        var (c, cInbox) = NewClient("另一个朋友");
        await a.ConnectAsync("127.0.0.1", hub.Port);
        await b.ConnectAsync("127.0.0.1", hub.Port);
        await c.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => aInbox.Count(m => m.Kind == ChatKind.Join) == 3));

        Assert.True(await b.SendAsync("大家好"));
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Text == "大家好") && cInbox.Any(m => m.Text == "大家好") && bInbox.Any(m => m.Text == "大家好")));
        var received = aInbox.First(m => m.Text == "大家好");
        Assert.Equal(("朋友", b.SenderId), (received.Name, received.SenderId));

        await c.DisposeAsync();
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Kind == ChatKind.Leave && m.Name == "另一个朋友")));
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Hub_DropsFloodAndDisconnectsBadClients()
    {
        await using var hub = new ChatHub(Room, 0);
        var (a, aInbox) = NewClient("a");
        var (spammer, _) = NewClient("spam");
        await a.ConnectAsync("127.0.0.1", hub.Port);
        await spammer.ConnectAsync("127.0.0.1", hub.Port);
        await WaitAsync(() => aInbox.Count(m => m.Kind == ChatKind.Join) == 2);

        // 绕过客户端自己的限速，直接发 20 条，房主只转发前 5 条。
        for (var i = 0; i < 20; i++) await spammer.SendRawAsync(new ChatMessage(ChatKind.Message, spammer.SenderId, "spam", "m" + i, DateTimeOffset.UtcNow), default);
        await Task.Delay(500);
        Assert.Equal(5, aInbox.Count(m => m.Kind == ChatKind.Message));

        // 用错误的房间密钥加密的连接被断开。
        var (intruder, _) = NewClient("x", Room with { Secret = "000000000000" });
        await intruder.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        // 超长帧：断开。
        var (big, _) = NewClient("big");
        await big.ConnectAsync("127.0.0.1", hub.Port);
        await WaitAsync(() => hub.MemberCount == 3);
        await big.SendFrameAsync(new byte[RoomChat.MaxFrameBytes + 1]);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Kind == ChatKind.Leave && m.Name == "big")));
        Assert.DoesNotContain(aInbox, m => m.Name == "x");

        // 客户端自己也限速。
        var sent = 0;
        for (var i = 0; i < 10; i++) if (await a.SendAsync("hi" + i)) sent++;
        Assert.Equal(5, sent);
        foreach (var c in new[] { a, spammer, intruder, big }) await c.DisposeAsync();
    }
}
