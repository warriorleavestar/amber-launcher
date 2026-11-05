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

    /// <summary>R7：一个不读数据的成员发送队列满了会被断开，其他人照常收消息。</summary>
    [Fact]
    public async Task SlowMemberIsDroppedOthersKeepReceiving()
    {
        await using var hub = new ChatHub(Room, 0) { OutboxFrames = 4, SendBufferForTest = 1024 };
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
        var text = new string('x', RoomChat.MaxTextLength);
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
}
