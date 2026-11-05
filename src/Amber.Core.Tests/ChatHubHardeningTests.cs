using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>R5–R7：聊天转发不让人冒充别的成员、不转发没认证的连接、慢的成员不拖住别人，并有超时和连接上限。</summary>
public sealed class ChatHubHardeningTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static readonly ChatCipher Cipher = new(Room);

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++) await Task.Delay(20);
        return condition();
    }

    private static (ChatClient Client, ConcurrentQueue<ChatMessage> Inbox) NewClient(string name)
    {
        var client = new ChatClient(Room, name);
        var inbox = new ConcurrentQueue<ChatMessage>();
        client.Received += inbox.Enqueue;
        return (client, inbox);
    }

    private static ChatMessage Msg(ChatKind kind, string sender, string name, string text = "") => new(kind, sender, name, text, DateTimeOffset.UtcNow);

    private static async Task WriteAsync(NetworkStream stream, byte[] frame)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(frame);
    }

    /// <summary>R5：别的连接拿已经在用的 id 加入会被拒；冒充别人发的消息不会被当成“自己的”。</summary>
    [Fact]
    public async Task DuplicateSenderIdIsRejectedAndMineNeedsOwnNonce()
    {
        await using var hub = new ChatHub(Room, 0);
        var (a, aInbox) = NewClient("A");
        await a.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));

        // 冒充者用 A 的 id 加入：连接被断开，A 收不到它的任何东西。
        var (impostor, _) = NewClient("A");
        await impostor.ConnectAsync("127.0.0.1", hub.Port);
        await impostor.SendRawAsync(Msg(ChatKind.Message, a.SenderId, "A", "我是 A"), default);
        Assert.True(await WaitAsync(() => hub.ConnectionCount == 1));
        Assert.DoesNotContain(aInbox, m => m.Text == "我是 A");

        // A 自己的消息是“自己的”；有人用 A 的 id 发（这里绕过转发直接造一条）不是。
        Assert.True(await a.SendAsync("真的 A"));
        Assert.True(await WaitAsync(() => aInbox.Any(m => m.Text == "真的 A")));
        Assert.True(a.IsMine(aInbox.First(m => m.Text == "真的 A")));
        var forged = Cipher.Open(Cipher.Seal(Msg(ChatKind.Message, a.SenderId, "A", "伪造")))!;
        Assert.False(a.IsMine(forged));
        await a.DisposeAsync();
        await impostor.DisposeAsync();
    }

    /// <summary>R5/R6：一条连接只能加入一次，名字和 id 固定；换了就断开。第一帧不是加入也断开。</summary>
    [Fact]
    public async Task IdentityIsFixedPerConnection()
    {
        await using var hub = new ChatHub(Room, 0);
        var (watcher, inbox) = NewClient("W");
        await watcher.ConnectAsync("127.0.0.1", hub.Port);

        async Task<bool> Disconnects(params ChatMessage[] frames)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", hub.Port);
            var stream = tcp.GetStream();
            foreach (var f in frames) await WriteAsync(stream, Cipher.Seal(f));
            var buffer = new byte[4096];
            try
            {
                using var t = new CancellationTokenSource(5000);
                while (await stream.ReadAsync(buffer, t.Token) > 0) { }
                return true;
            }
            catch (IOException) { return true; }
            catch (OperationCanceledException) { return false; }
        }

        Assert.True(await Disconnects(Msg(ChatKind.Join, "x1", "X"), Msg(ChatKind.Join, "x1", "X")));
        Assert.True(await Disconnects(Msg(ChatKind.Join, "x2", "X"), Msg(ChatKind.Message, "x2", "Y", "改名")));
        Assert.True(await Disconnects(Msg(ChatKind.Join, "x3", "X"), Msg(ChatKind.Message, "x4", "X", "换 id")));
        Assert.True(await Disconnects(Msg(ChatKind.Message, "x5", "X", "没加入")));
        Assert.DoesNotContain(inbox, m => m.Text is "改名" or "换 id" or "没加入");
        await watcher.DisposeAsync();
    }

    /// <summary>R6：不认识的消息类型解不开。</summary>
    [Fact]
    public void UnknownKindIsRejected()
    {
        var frame = Cipher.Seal(Msg((ChatKind)7, "abc", "a", "x"));
        Assert.Null(Cipher.Open(frame));
    }

    /// <summary>R7：没发第一帧的连接收不到别人的消息，到时间就断开；连接数有上限。</summary>
    [Fact]
    public async Task UnauthenticatedGetsNothingAndTimesOut()
    {
        await using var hub = new ChatHub(Room, 0) { FirstFrameTimeout = TimeSpan.FromSeconds(1) };
        using var silent = new TcpClient();
        await silent.ConnectAsync("127.0.0.1", hub.Port);
        var (a, _) = NewClient("A");
        await a.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await a.SendAsync("别人看不到"));
        var buffer = new byte[4096];
        using var t = new CancellationTokenSource(5000);
        var read = await silent.GetStream().ReadAsync(buffer, t.Token);
        Assert.Equal(0, read);
        Assert.True(await WaitAsync(() => hub.ConnectionCount == 1));
        await a.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionCountIsCapped()
    {
        await using var hub = new ChatHub(Room, 0);
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < ChatHub.MaxConnections + 5; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", hub.Port);
                clients.Add(c);
            }
            await Task.Delay(500);
            Assert.True(hub.ConnectionCount <= ChatHub.MaxConnections);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    /// <summary>R7：一个不读数据的成员发送队列一直满着会被断开（这里把“一直”设成 0），其他人照常收消息。</summary>
    [Fact]
    public async Task SlowMemberIsDroppedOthersKeepReceiving()
    {
        await using var hub = new ChatHub(Room, 0) { OutboxFrames = 4, SendBufferForTest = 1024, BacklogKickAfter = TimeSpan.Zero };
        using var slow = new TcpClient { ReceiveBufferSize = 1024 };
        await slow.ConnectAsync("127.0.0.1", hub.Port);
        await WriteAsync(slow.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "slow", "slow")));
        var (fast, inbox) = NewClient("fast");
        await fast.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        // 几个发送者各发 5 条长消息（每个人限速内），慢的那个一直不读。
        var senders = new List<ChatClient>();
        for (var s = 0; s < 6; s++)
        {
            var (c, _) = NewClient("s" + s);
            await c.ConnectAsync("127.0.0.1", hub.Port);
            senders.Add(c);
        }
        var text = new string('x', RoomChat.MaxTextLength - 1);
        // 一条一条发，等快的那个收到再发下一条：快的人队列不会满，慢的人越积越多。
        var sent = 0;
        for (var i = 0; i < 5; i++)
            foreach (var c in senders)
            {
                await c.SendRawAsync(Msg(ChatKind.Message, c.SenderId, c.Name, text + i), default);
                sent++;
                Assert.True(await WaitAsync(() => inbox.Count(m => m.Kind == ChatKind.Message) == sent), $"fast got {inbox.Count(m => m.Kind == ChatKind.Message)} of {sent}");
            }
        Assert.True(await WaitAsync(() => hub.MemberCount == senders.Count + 1, 10_000), $"members {hub.MemberCount}");
        foreach (var c in senders) await c.DisposeAsync();
        await fast.DisposeAsync();
    }

    /// <summary>R7：队列一时满了只丢帧，不断开；等他读完了，后面的消息照常收到。</summary>
    [Fact]
    public async Task BriefBacklogDropsFramesButKeepsMember()
    {
        await using var hub = new ChatHub(Room, 0) { OutboxFrames = 2, SendBufferForTest = 1024, BacklogKickAfter = TimeSpan.FromMinutes(5) };
        using var slow = new TcpClient { ReceiveBufferSize = 1024 };
        await slow.ConnectAsync("127.0.0.1", hub.Port);
        await WriteAsync(slow.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "slow", "slow")));
        var senders = new List<ChatClient>();
        for (var s = 0; s < 6; s++)
        {
            var (c, _) = NewClient("s" + s);
            await c.ConnectAsync("127.0.0.1", hub.Port);
            senders.Add(c);
        }
        Assert.True(await WaitAsync(() => hub.MemberCount == 7));
        var text = new string('x', RoomChat.MaxTextLength - 1);
        for (var i = 0; i < 4; i++)
            foreach (var c in senders) await c.SendRawAsync(Msg(ChatKind.Message, c.SenderId, c.Name, text + i), default);
        await Task.Delay(300);
        Assert.Equal(7, hub.MemberCount);

        // 慢的人开始读：读空积压之后，新消息还能收到。
        var stream = slow.GetStream();
        var got = new List<ChatMessage>();
        var reader = Task.Run(async () =>
        {
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                await stream.ReadExactlyAsync(frame);
                if (Cipher.Open(frame) is { } m) lock (got) got.Add(m);
            }
        });
        await Task.Delay(500);
        await senders[0].SendRawAsync(Msg(ChatKind.Message, senders[0].SenderId, senders[0].Name, "after"), default);
        Assert.True(await WaitAsync(() => { lock (got) return got.Any(m => m.Text == "after"); }));
        lock (got) Assert.True(got.Count(m => m.Kind == ChatKind.Message) < 25, "积压时应该丢掉了一些帧");
        foreach (var c in senders) await c.DisposeAsync();
    }

    /// <summary>R6：加入之后长时间不说话也不会被断开（不再有空闲时限；第一帧时限照旧）。</summary>
    [Fact]
    public async Task QuietMemberStaysConnected()
    {
        await using var hub = new ChatHub(Room, 0) { FirstFrameTimeout = TimeSpan.FromMilliseconds(300) };
        var (quiet, inbox) = NewClient("Q");
        await quiet.ConnectAsync("127.0.0.1", hub.Port);
        await Task.Delay(1200);
        Assert.Equal(1, hub.MemberCount);
        var (other, _) = NewClient("O");
        await other.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await other.SendAsync("还在吗"));
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Text == "还在吗")));
        await other.DisposeAsync();
        await quiet.DisposeAsync();
    }

    /// <summary>R7：房间连接满了、新连接被限速时，房主自己的客户端从留给它的端口照样能连上；那个端口同时只接一条。</summary>
    [Fact]
    public async Task HostSlotIsReserved()
    {
        await using var hub = new ChatHub(Room, 0);
        var clients = new List<ChatClient>();
        try
        {
            for (var i = 0; i < ChatHub.MaxConnections + 3; i++)
            {
                var (c, _) = NewClient("m" + i);
                try
                {
                    await c.ConnectAsync("127.0.0.1", hub.Port);
                    clients.Add(c);
                }
                catch (Exception e) when (e is IOException or SocketException) { await c.DisposeAsync(); }
                await WaitAsync(() => hub.ConnectionCount == hub.MemberCount, 1000);
            }
            Assert.True(await WaitAsync(() => hub.ConnectionCount == ChatHub.MaxConnections - 1), $"{hub.ConnectionCount}");
            Assert.True(await WaitAsync(() => hub.MemberCount == ChatHub.MaxConnections - 1), $"{hub.MemberCount}");
            var (host, _) = NewClient("房主");
            await host.ConnectAsync("127.0.0.1", hub.HostPort);
            Assert.True(await WaitAsync(() => hub.MemberCount == ChatHub.MaxConnections));
            Assert.Equal(ChatHub.MaxConnections, hub.ConnectionCount);

            // 第二条连房主端口的被拒。
            using var second = new TcpClient();
            await second.ConnectAsync("127.0.0.1", hub.HostPort);
            using var t = new CancellationTokenSource(5000);
            Assert.Equal(0, await second.GetStream().ReadAsync(new byte[16], t.Token));
            await host.DisposeAsync();
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }

    /// <summary>R7：加入通知整体限速：有人反复连上断开时，转发的加入/离开通知有上限（宣布过加入的离开才宣布）；还没加入的连接有上限。</summary>
    [Fact]
    public async Task ChurnIsRateLimitedHubWide()
    {
        await using var hub = new ChatHub(Room, 0) { PresencePerWindow = 4 };
        var (watcher, inbox) = NewClient("W");
        await watcher.ConnectAsync("127.0.0.1", hub.Port);
        for (var i = 0; i < 10; i++)
        {
            var (c, _) = NewClient("c" + i);
            await c.ConnectAsync("127.0.0.1", hub.Port);
            await c.DisposeAsync();
        }
        await Task.Delay(500);
        // 观察者自己的加入用了一个；剩下 3 个名额：最多 3 次加入、对应 3 次离开。
        var joins = inbox.Count(m => m.Kind == ChatKind.Join && m.SenderId != watcher.SenderId);
        var leaves = inbox.Count(m => m.Kind == ChatKind.Leave);
        Assert.True(joins <= 3 && leaves == joins, $"joins {joins} leaves {leaves}");
        await watcher.DisposeAsync();

        // 还没加入的连接最多 MaxPending 条，多的直接断开；已经加入的人照样能连。
        var silent = new List<TcpClient>();
        try
        {
            for (var i = 0; i < ChatHub.MaxPending + 4; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", hub.Port);
                silent.Add(c);
            }
            await Task.Delay(300);
            Assert.True(hub.ConnectionCount <= ChatHub.MaxPending, $"{hub.ConnectionCount}");
        }
        finally
        {
            foreach (var c in silent) c.Dispose();
        }
    }

    /// <summary>心跳：新转发在加入后发一个心跳，新客户端收到后定时发；支持心跳却太久没帧的成员被断开。心跳不交给界面。</summary>
    [Fact]
    public async Task PingCapableMembersAreReclaimed()
    {
        var old = ChatClient.PingInterval;
        ChatClient.PingInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            await using var hub = new ChatHub(Room, 0) { PingTimeout = TimeSpan.FromMilliseconds(800), SweepInterval = TimeSpan.FromMilliseconds(100) };
            var (live, inbox) = NewClient("L");
            await live.ConnectAsync("127.0.0.1", hub.Port);

            // 一个“死掉”的新客户端：先发过心跳，然后再也不发（模拟端口转发还连着、人已经不在）。
            using var dead = new TcpClient();
            await dead.ConnectAsync("127.0.0.1", hub.Port);
            await WriteAsync(dead.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "dead", "D")));
            await WriteAsync(dead.GetStream(), Cipher.Seal(Msg(ChatKind.Ping, "dead", "D")));
            Assert.True(await WaitAsync(() => hub.MemberCount == 2));
            Assert.True(await WaitAsync(() => hub.MemberCount == 1, 5000), $"{hub.MemberCount}");
            await Task.Delay(1200);
            // 一直在发心跳的那个还在，界面也收不到心跳。
            Assert.Equal(1, hub.MemberCount);
            Assert.DoesNotContain(inbox, m => m.Kind == ChatKind.Ping);
            await live.DisposeAsync();
        }
        finally
        {
            ChatClient.PingInterval = old;
        }
    }

    /// <summary>旧版本（不发心跳）的成员不会因为不说话被断开；只有连接满了时，最久没动静的才被腾掉位子。</summary>
    [Fact]
    public async Task IdleLegacyMemberIsEvictedOnlyWhenFull()
    {
        var now = DateTimeOffset.UtcNow;
        await using var hub = new ChatHub(Room, 0) { Clock = () => now, IdleEvictAfter = TimeSpan.FromMinutes(10) };
        using var legacy = new TcpClient();
        await legacy.ConnectAsync("127.0.0.1", hub.Port);
        await WriteAsync(legacy.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "legacy", "Old")));
        Assert.True(await WaitAsync(() => hub.MemberCount == 1));
        now += TimeSpan.FromHours(1);
        var others = new List<TcpClient>();
        try
        {
            for (var i = 0; i < ChatHub.MaxConnections - 2; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", hub.Port);
                await WriteAsync(c.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "o" + i, "O")));
                others.Add(c);
                await WaitAsync(() => hub.MemberCount == i + 2, 2000);
            }
            Assert.Equal(ChatHub.MaxConnections - 1, hub.MemberCount);
            // 满了：再来一个时，一小时没动静的旧成员被腾掉（其他人刚连上，不动）。
            using var late = new TcpClient();
            await late.ConnectAsync("127.0.0.1", hub.Port);
            await WriteAsync(late.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "late", "Late")));
            using var t = new CancellationTokenSource(5000);
            var buffer = new byte[4096];
            var stream = legacy.GetStream();
            try { while (await stream.ReadAsync(buffer, t.Token) > 0) { } }
            catch (IOException) { }
            Assert.False(t.IsCancellationRequested, "旧成员应该被腾掉");
        }
        finally
        {
            foreach (var c in others) c.Dispose();
        }
    }

    /// <summary>500 个 UTF-16 单元的中文、补充平面字符和需转义字符，连同最长名字都能通过真实 TCP 转发。</summary>
    [Theory]
    [InlineData("中")]
    [InlineData("\U0001F600")]
    [InlineData("<>&+\"\\")]
    public async Task MaximumUnicodeMessageReachesAllMembers(string unit)
    {
        static string Repeat(string value, int length) => string.Concat(Enumerable.Repeat(value, length / value.Length + 1))[..length];
        var name = Repeat(unit, RoomChat.MaxNameLength);
        var text = Repeat(unit, RoomChat.MaxTextLength);
        await using var hub = new ChatHub(Room, 0);
        var (watcher, inbox) = NewClient("W");
        await using var watcherLifetime = watcher;
        var (sender, echoes) = NewClient(name);
        await using var senderLifetime = sender;
        await watcher.ConnectAsync("127.0.0.1", hub.Port);
        await sender.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await WaitAsync(() => hub.MemberCount == 2));

        Assert.True(await sender.SendAsync(text));
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Kind == ChatKind.Message && m.Text == text)
                                      && echoes.Any(m => m.Kind == ChatKind.Message && m.Text == text)));
        Assert.Equal(name, inbox.Single(m => m.Kind == ChatKind.Message).Name);
    }

    /// <summary>旧的 JSON 转义形式及最大 64 单元的发送者 id 仍能通过转发。</summary>
    [Fact]
    public async Task LegacyEscapedFrameWithMaximumFieldsIsForwarded()
    {
        await using var hub = new ChatHub(Room, 0);
        var (watcher, inbox) = NewClient("W");
        await using var watcherLifetime = watcher;
        await watcher.ConnectAsync("127.0.0.1", hub.Port);
        using var legacy = new TcpClient();
        await legacy.ConnectAsync("127.0.0.1", hub.Port);
        var sender = new string('源', RoomChat.MaxSenderLength);
        var name = new string('名', RoomChat.MaxNameLength);
        var text = new string('中', RoomChat.MaxTextLength);
        byte[] LegacyFrame(ChatMessage message) => Encoding.UTF8.GetBytes(JsonNode.Parse(Cipher.Seal(message))!.ToJsonString());
        await WriteAsync(legacy.GetStream(), LegacyFrame(Msg(ChatKind.Join, sender, name)));
        var frame = LegacyFrame(Msg(ChatKind.Message, sender, name, text));
        Assert.True(frame.Length > 4096, $"旧编码帧只有 {frame.Length} 字节，没有覆盖原来的限制");
        Assert.True(frame.Length <= RoomChat.MaxFrameBytes);
        await WriteAsync(legacy.GetStream(), frame);
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Kind == ChatKind.Message && m.Text == text)));
        Assert.Equal(sender, inbox.Single(m => m.Kind == ChatKind.Message).SenderId);
        Assert.Equal(name, inbox.Single(m => m.Kind == ChatKind.Message).Name);
    }

    /// <summary>原始文字超限仍丢弃（连接不断，正常消息照常），不能靠 Clean 截断绕过。</summary>
    [Theory]
    [InlineData(501, false)]
    [InlineData(3500, false)]
    [InlineData(1, true)]
    public async Task OversizedMessageFrameIsDropped(int textLength, bool oversizedName)
    {
        await using var hub = new ChatHub(Room, 0);
        var (watcher, inbox) = NewClient("W");
        await watcher.ConnectAsync("127.0.0.1", hub.Port);
        using var raw = new TcpClient();
        await raw.ConnectAsync("127.0.0.1", hub.Port);
        await WriteAsync(raw.GetStream(), Cipher.Seal(Msg(ChatKind.Join, "big", "B")));
        // 正常客户端在发送前截断；手工拼帧即使没到 8 KiB，原始字段超长也不能转发。
        var oversized = Cipher.Seal(Msg(ChatKind.Message, "big", oversizedName ? new string('B', RoomChat.MaxNameLength + 1) : "B", new string('a', textLength)));
        Assert.True(oversized.Length <= RoomChat.MaxFrameBytes);
        await WriteAsync(raw.GetStream(), oversized);
        await WriteAsync(raw.GetStream(), Cipher.Seal(Msg(ChatKind.Message, "big", "B", "短的")));
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Text == "短的")));
        Assert.Single(inbox, m => m.Kind == ChatKind.Message);
        await watcher.DisposeAsync();
    }

    /// <summary>R5：别人把我发过的帧原样重放（或者同一条被转发两次），只有第一次算“自己的”；id 不是我的也不算。</summary>
    [Fact]
    public async Task MineIsMatchedOnceAndNeedsOwnId()
    {
        await using var hub = new ChatHub(Room, 0);
        var (a, inbox) = NewClient("A");
        await a.ConnectAsync("127.0.0.1", hub.Port);
        Assert.True(await a.SendAsync("一次"));
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Text == "一次")));
        var echo = inbox.First(m => m.Text == "一次");
        Assert.True(a.IsMine(echo));
        Assert.False(a.IsMine(echo));
        Assert.False(a.IsMine(echo with { SenderId = "someone-else" }));

        Assert.True(await a.SendAsync("二次"));
        Assert.True(await WaitAsync(() => inbox.Any(m => m.Text == "二次")));
        var second = inbox.First(m => m.Text == "二次");
        // 同一个 nonce 换成别人的 id（重放成别人发的）：不算，也不会把这个 nonce 用掉。
        Assert.False(a.IsMine(second with { SenderId = "someone-else" }));
        Assert.True(a.IsMine(second));
        await a.DisposeAsync();
    }
}
