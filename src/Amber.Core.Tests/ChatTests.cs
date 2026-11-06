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
        Assert.Equal(message, cipher.Open(frame)! with { Nonce = "" });
        // 同一条消息两次加密结果不同（随机 nonce）。
        Assert.NotEqual(frame, cipher.Seal(message));
    }

    /// <summary>按最坏的每单元 6 字节 JSON 转义算上标签、Base64、发送者和外层字段，合法消息仍小于旧协议的 8 KiB。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cipher_MaximumUnicodeFieldsFitLegacyFrameLimit(bool latestTime)
    {
        const int jsonAllowance = 64;
        var maxPlainBytes = 6 * (RoomChat.MaxTextLength + RoomChat.MaxNameLength) + jsonAllowance;
        var maxCipherBase64Bytes = 4 * ((maxPlainBytes + 16 + 2) / 3);
        var maxEnvelopeBytes = maxCipherBase64Bytes + 6 * RoomChat.MaxSenderLength + jsonAllowance;
        Assert.True(maxEnvelopeBytes < RoomChat.MaxFrameBytes);
        Assert.Equal(RoomChat.MaxFrameBytes, ChatHub.MaxForwardFrame);

        var cipher = new ChatCipher(Room);
        var message = new ChatMessage(ChatKind.Message, new string('\uffff', RoomChat.MaxSenderLength),
            new string('\uffff', RoomChat.MaxNameLength), new string('\uffff', RoomChat.MaxTextLength),
            latestTime ? DateTimeOffset.MaxValue.AddTicks(-9999) : DateTimeOffset.MinValue);
        var frame = cipher.Seal(message);
        Assert.InRange(frame.Length, 1, maxEnvelopeBytes);
        // Base64 是 TCP JSON 的普通字符串，不需要 HTML 转义；否则上述 Base64 上界不成立。
        Assert.DoesNotContain("\\u002B", Encoding.UTF8.GetString(frame));
        Assert.Equal(message, cipher.Open(frame)! with { Nonce = "" });
    }

    /// <summary>解密后先查原始长度，再做显示清理，不能将恶意超长内容截断后当成合法帧。</summary>
    [Theory]
    [InlineData(65, 32, 500)]
    [InlineData(64, 33, 500)]
    [InlineData(64, 32, 501)]
    public void Cipher_RejectsFieldsBeyondProtocolLimits(int senderLength, int nameLength, int textLength)
    {
        var cipher = new ChatCipher(Room);
        var frame = cipher.Seal(new ChatMessage(ChatKind.Message, new string('s', senderLength),
            new string('n', nameLength), new string('t', textLength), DateTimeOffset.UnixEpoch));
        Assert.Null(cipher.Open(frame));
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
        var (a, aInbox) = NewClient("a");
        // 故意暂缓观察者的转发，完成条件必须来自协议顺序，不能依赖固定 500 ms。
        var holdForwarding = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new ChatHub(Room, 0)
        {
            BeforeSendForTest = (id, ct) => id == a.SenderId && Volatile.Read(ref holdForwarding) != 0
                ? gate.Task.WaitAsync(ct) : Task.CompletedTask,
        };
        await using var aLifetime = a;
        var (spammer, _) = NewClient("spam");
        await using var spammerLifetime = spammer;
        await a.ConnectAsync("127.0.0.1", hub.Port);
        await spammer.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => aInbox.Count(m => m.Kind == ChatKind.Join) == 2));
        Volatile.Write(ref holdForwarding, 1);

        // 绕过客户端自己的限速，直接发 20 条，房主只转发前 5 条。
        for (var i = 0; i < 20; i++) await spammer.SendRawAsync(new ChatMessage(ChatKind.Message, spammer.SenderId, "spam", "m" + i, DateTimeOffset.UtcNow), default);
        // 同一 TCP 连接的离开帧排在 20 条消息之后；观察者收到离开时，前面的转发也已读完。
        await spammer.SendRawAsync(new ChatMessage(ChatKind.Leave, spammer.SenderId, "spam", "", DateTimeOffset.UtcNow), default);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        Assert.DoesNotContain(aInbox, m => m.Kind == ChatKind.Message);
        gate.TrySetResult();
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Kind == ChatKind.Leave && m.SenderId == spammer.SenderId)));
        Assert.Equal(5, aInbox.Count(m => m.Kind == ChatKind.Message));

        // 上面主动离开仅用作完成屏障；补回一个正常成员，保留下面拒绝连接的原始人数前置。
        var (member, _) = NewClient("member");
        await using var memberLifetime = member;
        await member.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2
            && aInbox.Any(m => m.Kind == ChatKind.Join && m.SenderId == member.SenderId)));

        // 用错误的房间密钥加密的连接被断开。
        var (intruder, _) = NewClient("x", Room with { Secret = "000000000000" });
        await using var intruderLifetime = intruder;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        intruder.Disconnected += () => disconnected.TrySetResult();
        await intruder.ConnectAsync("127.0.0.1", hub.Port);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        // 超长帧：断开。
        var (big, _) = NewClient("big");
        await using var bigLifetime = big;
        await big.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 3));
        await big.SendFrameAsync(new byte[RoomChat.MaxFrameBytes + 1]);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Kind == ChatKind.Leave && m.Name == "big")));
        Assert.DoesNotContain(aInbox, m => m.Name == "x");

        // 客户端自己也限速。
        var sent = 0;
        for (var i = 0; i < 10; i++) if (await a.SendAsync("hi" + i)) sent++;
        Assert.Equal(5, sent);
    }
}

public class ChatHistoryTests
{
    private static RoomCode Room(int i) => new("K7Q2M9X" + i, "4HZP8RW2N6TC", 25565);

    [Fact]
    public void SaveAndRestorePerRoom()
    {
        var history = new ChatHistory<string>();
        history.Save(Room(1), ["a", "b"]);
        history.Save(Room(2), ["c"]);
        Assert.Equal(["a", "b"], history.Restore(Room(1)));
        Assert.Equal(["c"], history.Restore(Room(2)));
        Assert.Empty(history.Restore(Room(3)));
        // 同一个网络名、不同密钥是另一个房间。
        Assert.Empty(history.Restore(Room(1) with { Secret = "000000000000" }));
    }

    [Fact]
    public void CapsPerRoomAndRoomCountLru()
    {
        var history = new ChatHistory<int>(maxRooms: 5, maxPerRoom: 500);
        history.Save(Room(0), Enumerable.Range(0, 800));
        Assert.Equal(500, history.Restore(Room(0)).Count);
        Assert.Equal(300, history.Restore(Room(0))[0]);
        for (var i = 1; i <= 5; i++)
        {
            history.Save(Room(i), [i]);
            if (i == 3) history.Restore(Room(0));   // 房间 0 被用过，比房间 1、2 新
        }
        Assert.Equal(5, history.RoomCount);
        Assert.Empty(history.Restore(Room(1)));
        Assert.NotEmpty(history.Restore(Room(0)));
        Assert.Equal([5], history.Restore(Room(5)));
    }

    [Fact]
    public void KeyIsHashNotRoomCode()
    {
        var key = ChatHistory<string>.KeyOf(Room(1));
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.DoesNotContain("4HZP8RW2N6TC", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptySaveForgetsRoom()
    {
        var history = new ChatHistory<string>();
        history.Save(Room(1), ["x"]);
        history.Save(Room(1), []);
        Assert.Equal(0, history.RoomCount);
    }
}
