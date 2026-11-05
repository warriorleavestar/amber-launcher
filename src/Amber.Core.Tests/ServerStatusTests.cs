using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amber.Core.Game;

namespace Amber.Core.Tests;

/// <summary>只用内存和回环套接字检查不可信状态响应，不分配攻击者声明的大数组。</summary>
[Collection("服务器状态并发预算")]
public sealed class ServerStatusTests
{
    [Fact]
    public async Task StatusCannotReadPastDeclaredFrame()
    {
        var response = StatusPacket("{\"description\":\"hello\"}");
        response[0] = 1;
        Assert.False((await PingResponse(response)).Online);
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        yield return [Array.Empty<byte>(), typeof(EndOfStreamException)];
        yield return [new byte[] { 0x80 }, typeof(EndOfStreamException)];
        foreach (var length in new[] { 0, -1, int.MinValue, int.MaxValue, ServerList.MaxStatusPacketBytes + 1 })
            yield return [VarInt(length), typeof(InvalidDataException)];
        yield return [new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 }, typeof(InvalidDataException)];
        yield return [new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0 }, typeof(InvalidDataException)];
        yield return [new byte[] { 2, 0 }, typeof(EndOfStreamException)];
        yield return [Frame([0x80]), typeof(EndOfStreamException)];
        yield return [Frame([1, 2, (byte)'{', (byte)'}']), typeof(InvalidDataException)];
        yield return [Frame([0x80, 0x80, 0x80, 0x80, 0x10, 2, (byte)'{', (byte)'}']), typeof(InvalidDataException)];
        foreach (var length in new[] { -1, int.MinValue, int.MaxValue, ServerList.MaxStatusPacketBytes + 1 })
            yield return [Frame([0, .. VarInt(length)]), typeof(InvalidDataException)];
        yield return [Frame([0, 0x80]), typeof(EndOfStreamException)];
        yield return [Frame([0, 0x80, 0x80, 0x80, 0x80, 0x10]), typeof(InvalidDataException)];
        yield return [Frame([0, 1, (byte)'{', (byte)'}']), typeof(InvalidDataException)];
        yield return [Frame([0, 3, (byte)'{', (byte)'}']), typeof(InvalidDataException)];
        yield return [Frame([0, 2, (byte)'{', (byte)'}', 0]), typeof(InvalidDataException)];
        yield return [Frame([0, 1, (byte)'{']), typeof(JsonException)];
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public async Task MalformedFrameIsRejected(byte[] bytes, Type exceptionType)
    {
        using var stream = new MemoryStream(bytes);
        var error = await Record.ExceptionAsync(() => ServerList.ReadStatusPacketAsync(stream, default));
        Assert.NotNull(error);
        Assert.IsAssignableFrom(exceptionType, error);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAnyBodyRead()
    {
        var prefix = VarInt(int.MaxValue);
        using var stream = new MemoryStream([.. prefix, 42]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(stream, default));
        Assert.Equal(prefix.Length, stream.Position);
    }

    [Fact]
    public async Task NextPacketCannotCompleteTruncatedStatus()
    {
        var first = Frame([0, 5, (byte)'{', (byte)'}']);
        using var stream = new MemoryStream([.. first, .. StatusPacket("{}")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(stream, default));
        Assert.Equal(first.Length, stream.Position);
    }

    [Fact]
    public async Task NonMinimalVarIntsAndFollowingPacketArePreserved()
    {
        byte[] body = [0x80, 0, 0x82, 0x80, 0x80, 0x80, 0, (byte)'{', (byte)'}'];
        byte[] packet = [(byte)(body.Length | 0x80), 0x80, 0, .. body];
        using var stream = new MemoryStream([.. packet, 42]);
        Assert.NotNull(await ServerList.ReadStatusPacketAsync(stream, default));
        Assert.Equal(packet.Length, stream.Position);
        Assert.Equal(42, stream.ReadByte());
    }

    [Fact]
    public async Task FullFrameBudgetAllowsMultibyteAndBoundaryResponses()
    {
        var motd = new string('中', 20000);
        using (var stream = new MemoryStream(StatusPacket("{\"description\":\"" + motd + "\"}")))
            Assert.Equal(motd, (await ServerList.ReadStatusPacketAsync(stream, default))!["description"]!.GetValue<string>());
        // 为 ID 和三字节 JSON 长度前缀留四字节，尾部空白恰好填满既有 1 MiB 整帧预算。
        var json = "{}" + new string(' ', ServerList.MaxStatusPacketBytes - 6);
        using var boundary = new MemoryStream(StatusPacket(json));
        Assert.NotNull(await ServerList.ReadStatusPacketAsync(boundary, default));
        using var tooLarge = new MemoryStream(StatusPacket(json + " "));
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(tooLarge, default));
    }

    [Fact]
    public async Task PlainMotdExtensionsAndMissingPongRemainSupported()
    {
        var status = await PingResponse(StatusPacket("""{"description":"你好 😀","version":{"name":"custom"},"players":{"online":3,"max":20},"favicon":"data:image/png;base64,AAAA","modinfo":{"type":"FML","modList":[]}}"""));
        Assert.True(status.Online, status.Error);
        Assert.Equal(("你好 😀", "custom", 3, 20), (status.Motd, status.VersionName, status.Players, status.MaxPlayers));
        Assert.Null(status.PingMs);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    public async Task NonObjectStatusKeepsMainValidation(string json)
    {
        using var stream = new MemoryStream(StatusPacket(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(stream));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public async Task PongRetainsExactPayloadAndLengthValidation(int kind, bool online)
    {
        var body = new byte[9];
        body[0] = 1;
        if (kind == 1) body[0] = 2;
        if (kind == 2) body[8] = 1;
        var pong = kind switch
        {
            3 => Frame(body[..^1]),
            4 => VarInt(int.MaxValue),
            _ => Frame(body)
        };
        var status = await PingResponse(StatusPacket("{}"), pong);
        Assert.Equal(online, status.Online);
        if (online) Assert.NotNull(status.PingMs);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(ServerList.MaxStatusPacketBytes + 1)]
    public async Task MaliciousLengthReturnsOfflineThroughPublicApi(int length)
    {
        var status = await PingResponse(VarInt(length));
        Assert.False(status.Online);
        Assert.NotEmpty(status.Error!);
    }

    [Fact]
    public async Task SilentPeerTimesOutAndNextQuerySucceeds()
    {
        Assert.False((await PingResponse([])).Online);
        Assert.True((await PingResponse(StatusPacket("{}"))).Online);
    }

    [Fact]
    public async Task QueuedQueriesKeepTheirNetworkTimeoutAndReleaseSlots()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var accepted = new List<TcpClient>();
        try
        {
            for (var wave = 0; wave < 3; wave++)
            {
                using var active = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var queries = Enumerable.Range(0, 4).Select(_ => ServerList.PingAsync(address, TimeSpan.FromSeconds(5), active.Token)).ToArray();
                for (var i = 0; i < 4; i++) accepted.Add(await listener.AcceptTcpClientAsync(stop.Token));
                if (wave == 0)
                {
                    using var queued = new CancellationTokenSource();
                    var canceled = ServerList.PingAsync(address, ct: queued.Token);
                    queued.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
                    var healthy = ServerList.PingAsync(address, TimeSpan.FromMilliseconds(500), stop.Token);
                    await Task.Delay(700, stop.Token);
                    Assert.False(healthy.IsCompleted);
                    Assert.False(listener.Pending());
                    // 读状态时调用者取消必须归还名额，下一轮仍能建立四个连接。
                    active.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(queries));
                    using var next = await listener.AcceptTcpClientAsync(stop.Token);
                    await Reply(next, StatusPacket("{}"), stop.Token);
                    Assert.True((await healthy).Online);
                }
                else if (wave == 1)
                {
                    var replies = accepted.Select(async client =>
                    {
                        using var stream = client.GetStream();
                        await ReadClientPacket(stream, stop.Token);
                        await ReadClientPacket(stream, stop.Token);
                        await stream.WriteAsync(new byte[] { 0 }, stop.Token);
                    });
                    await Task.WhenAll(replies);
                    Assert.All(await Task.WhenAll(queries), status => Assert.False(status.Online));
                }
                else
                {
                    var replies = accepted.Select(client => Reply(client, StatusPacket("{\"description\":\"ok\"}"), stop.Token));
                    await Task.WhenAll(replies);
                    Assert.All(await Task.WhenAll(queries), status => Assert.True(status.Online, status.Error));
                }
                foreach (var client in accepted) client.Dispose();
                accepted.Clear();
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var client in accepted) client.Dispose();
        }
    }

    private static byte[] VarInt(int value)
    {
        using var output = new MemoryStream();
        var v = (uint)value;
        while (v >= 0x80)
        {
            output.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        output.WriteByte((byte)v);
        return output.ToArray();
    }

    private static byte[] StatusPacket(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        byte[] body = [0, .. VarInt(bytes.Length), .. bytes];
        return Frame(body);
    }

    private static byte[] Frame(byte[] body) => [.. VarInt(body.Length), .. body];

    private static async Task<ServerStatus> PingResponse(byte[] response, byte[]? pong = null)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Serve();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await ServerList.PingAsync($"127.0.0.1:{port}", TimeSpan.FromSeconds(2));
        await server;
        return result;

        async Task Serve()
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await Reply(client, response, stop.Token, pong);
        }
    }

    private static async Task Reply(TcpClient client, byte[] response, CancellationToken ct, byte[]? pong = null)
    {
        using var stream = client.GetStream();
        await ReadClientPacket(stream, ct);
        await ReadClientPacket(stream, ct);
        await stream.WriteAsync(response, ct);
        try
        {
            await ReadClientPacket(stream, ct);
            if (pong is not null) await stream.WriteAsync(pong, ct);
        }
        catch (IOException) { }
    }

    private static async Task ReadClientPacket(Stream stream, CancellationToken ct)
    {
        var length = 0;
        var next = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(next, ct);
            length |= (next[0] & 0x7f) << shift;
            if ((next[0] & 0x80) != 0) continue;
            Assert.InRange(length, 1, 1024);
            await stream.ReadExactlyAsync(new byte[length], ct);
            return;
        }
        throw new InvalidDataException();
    }
}

// 这些用例独占共享 Core 查询预算，不与其他回环服务器测试争抢名额。
[CollectionDefinition("服务器状态并发预算", DisableParallelization = true)]
public sealed class ServerStatusCollection;
