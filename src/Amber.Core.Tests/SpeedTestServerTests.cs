using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>用少量回环连接验证测速服务的配额、期限和关闭，不做资源耗尽测试。</summary>
public sealed class SpeedTestServerTests
{
    [Fact]
    public async Task ExcessConnectionIsClosedBeforeReceivingHeader()
    {
        await using var server = new SpeedTestServer(0);
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < SpeedTestServer.Limits.Default.Sessions; i++) clients.Add(await Connect(server.BoundPort));
            await WaitCount(server, SpeedTestServer.Limits.Default.Sessions);
            clients.Add(await Connect(server.BoundPort));
            await AssertClosed(clients[^1]);
            Assert.Equal(SpeedTestServer.Limits.Default.Sessions, server.ActiveSessions);
            clients[0].Dispose();
            await WaitCount(server, SpeedTestServer.Limits.Default.Sessions - 1);
            Assert.True(await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 4 * 1024 * 1024, default) > 0);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task SilentAndPartialHeadersExpire(int bytes)
    {
        await using var server = new SpeedTestServer(0, SpeedTestServer.Limits.Default with { Handshake = TimeSpan.FromMilliseconds(200) });
        using var client = await Connect(server.BoundPort);
        if (bytes > 0) await client.GetStream().WriteAsync(new byte[bytes]);
        await AssertClosed(client);
        await WaitCount(server, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IdlePayloadExpires(int bytes)
    {
        await using var server = new SpeedTestServer(0, SpeedTestServer.Limits.Default with { Idle = TimeSpan.FromMilliseconds(200) });
        using var client = await Connect(server.BoundPort);
        await client.GetStream().WriteAsync(BitConverter.GetBytes(1024L));
        if (bytes > 0) await client.GetStream().WriteAsync(new byte[bytes]);
        await AssertClosed(client);
        await WaitCount(server, 0);
    }

    [Fact]
    public async Task FragmentedHeaderCannotExtendHandshakeDeadline()
    {
        await using var server = new SpeedTestServer(0, SpeedTestServer.Limits.Default with { Handshake = TimeSpan.FromMilliseconds(350) });
        using var client = await Connect(server.BoundPort);
        using var stop = new CancellationTokenSource();
        var send = Trickle(client, stop.Token, 100);
        try { await AssertClosed(client); }
        finally { stop.Cancel(); await send; }
        await WaitCount(server, 0);
    }

    [Fact]
    public async Task PayloadTrickleCannotExtendTotalDeadline()
    {
        await using var server = new SpeedTestServer(0, SpeedTestServer.Limits.Default with { Idle = TimeSpan.FromMilliseconds(400), Total = TimeSpan.FromMilliseconds(700) });
        using var client = await Connect(server.BoundPort);
        await client.GetStream().WriteAsync(BitConverter.GetBytes(1024L));
        using var stop = new CancellationTokenSource();
        var send = Trickle(client, stop.Token, 50);
        try { await AssertClosed(client); }
        finally { stop.Cancel(); await send; }
        await WaitCount(server, 0);
    }

    [Fact]
    public async Task ProgressRefreshesIdleDeadlineAndReturnsExactAck()
    {
        await using var server = new SpeedTestServer(0, SpeedTestServer.Limits.Default with { Idle = TimeSpan.FromMilliseconds(300) });
        using var client = await Connect(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(BitConverter.GetBytes(8L));
        for (var i = 0; i < 8; i++)
        {
            await stream.WriteAsync(new byte[] { 42 });
            await Task.Delay(80);
        }
        Assert.Equal(8, await ReadAck(stream));
        await WaitCount(server, 0);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(67108865L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task InvalidLengthsCloseAndReleaseCapacity(long length)
    {
        await using var server = new SpeedTestServer(0);
        using var client = await Connect(server.BoundPort);
        await client.GetStream().WriteAsync(BitConverter.GetBytes(length));
        await AssertClosed(client);
        await WaitCount(server, 0);
        Assert.True(await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 1024, default) > 0);
        await WaitCount(server, 0);
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(67108864L)]
    public async Task PayloadEofPreservesPartialAcknowledgment(long expected)
    {
        await using var server = new SpeedTestServer(0);
        using var client = await Connect(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(BitConverter.GetBytes(expected));
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        client.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(3, await ReadAck(stream));
        await WaitCount(server, 0);
    }

    [Fact]
    public async Task TruncatedHeaderAndDisconnectedPayloadReleaseCapacity()
    {
        await using var server = new SpeedTestServer(0);
        using (var header = await Connect(server.BoundPort))
        {
            var stream = header.GetStream();
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            header.Client.Shutdown(SocketShutdown.Send);
            await AssertClosed(stream);
        }
        await WaitCount(server, 0);
        using (var payload = await Connect(server.BoundPort))
        {
            await payload.GetStream().WriteAsync(BitConverter.GetBytes(1024L));
            await WaitCount(server, 1);
        }
        await WaitCount(server, 0);
    }

    [Fact]
    public async Task BothListenersShareOneAtomicQuota()
    {
        await using var server = new SpeedTestServer(0);
        var second = IPAddress.IPv6Loopback;
        Assert.True(server.TryAddListener(second));
        Assert.False(server.TryAddListener(second));
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < SpeedTestServer.Limits.Default.Sessions; i++)
                clients.Add(await Connect(server.BoundPort, i % 2 == 0 ? IPAddress.Loopback : second));
            await WaitCount(server, SpeedTestServer.Limits.Default.Sessions);
            using var firstExtra = await Connect(server.BoundPort);
            using var secondExtra = await Connect(server.BoundPort, second);
            await Task.WhenAll(AssertClosed(firstExtra), AssertClosed(secondExtra));
            Assert.Equal(SpeedTestServer.Limits.Default.Sessions, server.ActiveSessions);
        }
        finally { foreach (var client in clients) client.Dispose(); }
        await WaitCount(server, 0);
        Assert.True(await SpeedTestServer.MeasureAsync(second.ToString(), server.BoundPort, 4 * 1024 * 1024, default) > 0);
    }

    [Fact]
    public async Task DisposalWaitsForActiveClientsAndIsRepeatable()
    {
        await using var server = new SpeedTestServer(0);
        using var header = await Connect(server.BoundPort);
        using var payload = await Connect(server.BoundPort);
        await payload.GetStream().WriteAsync(BitConverter.GetBytes(1024L));
        await WaitCount(server, 2);
        var first = server.DisposeAsync().AsTask();
        var second = server.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, server.ActiveSessions);
        await Task.WhenAll(AssertClosed(header), AssertClosed(payload));
        Assert.False(server.TryAddListener(IPAddress.IPv6Loopback));
        await Assert.ThrowsAsync<SocketException>(() => Connect(server.BoundPort));
    }

    [Fact]
    public async Task AddingListenerCannotRacePastDisposal()
    {
        await using var server = new SpeedTestServer(0);
        var second = IPAddress.IPv6Loopback;
        await Task.WhenAll(Task.Run(() => server.TryAddListener(second)), Task.Run(async () => await server.DisposeAsync()));
        Assert.False(server.TryAddListener(second));
        await Assert.ThrowsAsync<SocketException>(() => Connect(server.BoundPort, second));
    }

    [Fact]
    public async Task DisposalDrainsOwnedConnectionsWhileOtherClientsArrive()
    {
        for (var i = 0; i < 8; i++)
        {
            await using var server = new SpeedTestServer(0);
            using var owned = await Connect(server.BoundPort);
            await WaitCount(server, 1);
            var arriving = Connect(server.BoundPort);
            var closing = Task.Run(async () => await server.DisposeAsync());
            await closing.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, server.ActiveSessions);
            await AssertClosed(owned);
            try
            {
                // 先等关闭任务完成，避免把任务调度时间混入连接关闭期限。
                using var client = await arriving;
                await AssertClosed(client);
            }
            catch (SocketException) { }
            Assert.False(server.TryAddListener(IPAddress.IPv6Loopback));
            await Assert.ThrowsAsync<SocketException>(() => Connect(server.BoundPort));
        }
    }

    [Fact]
    public async Task CallerCancellationAndLaterMeasurementRemainSupported()
    {
        await using var server = new SpeedTestServer(0);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 1024, canceled.Token));
        Assert.True(await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 4 * 1024 * 1024, default) > 0);
        await WaitCount(server, 0);
    }

    private static async Task Trickle(TcpClient client, CancellationToken ct, int milliseconds)
    {
        try
        {
            while (true)
            {
                await client.GetStream().WriteAsync(new byte[] { 1 }, ct);
                await Task.Delay(milliseconds, ct);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<long> ReadAck(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ack = new byte[8];
        await stream.ReadExactlyAsync(ack, timeout.Token);
        return BitConverter.ToInt64(ack);
    }

    private static async Task WaitCount(SpeedTestServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.ActiveSessions != count) await Task.Delay(10, timeout.Token);
    }

    private static async Task<TcpClient> Connect(int port, IPAddress? address = null)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(address ?? IPAddress.Loopback, port, timeout.Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static Task AssertClosed(TcpClient client) => AssertClosed(client.GetStream());

    private static async Task AssertClosed(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token)); }
        catch (IOException) { }
    }
}
