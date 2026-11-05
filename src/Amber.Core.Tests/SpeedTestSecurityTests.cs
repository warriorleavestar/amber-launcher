using Amber.Core.Multiplayer;
using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Tests;

public class SpeedTestSecurityTests
{
    private static SpeedTestServer.Limits ShortLimits(int sessions = 2) => new(sessions,
        TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(180), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentOrPartialHandshakeExpiresAndReleasesSlot(bool partial)
    {
        await using var server = new SpeedTestServer(0, ShortLimits(1));
        using var client = await ConnectAsync(server.BoundPort);
        await WaitForAsync(() => server.ActiveSessions == 1);
        if (partial) await client.GetStream().WriteAsync(new byte[] { 1 });
        await AssertClosedAsync(client);
        await WaitForAsync(() => server.ActiveSessions == 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 8192, timeout.Token) > 0);
    }

    [Fact]
    public async Task StalledBodyExpiresAndReleasesSlot()
    {
        await using var server = new SpeedTestServer(0, ShortLimits(1));
        using var client = await ConnectAsync(server.BoundPort);
        await client.GetStream().WriteAsync(BitConverter.GetBytes(64L * 1024 * 1024));
        await AssertClosedAsync(client);
        await WaitForAsync(() => server.ActiveSessions == 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 8192, timeout.Token) > 0);
    }

    [Fact]
    public async Task TotalDeadlineStopsAContinuouslyTricklingBody()
    {
        var limits = new SpeedTestServer.Limits(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300));
        await using var server = new SpeedTestServer(0, limits);
        using var client = await ConnectAsync(server.BoundPort);
        await client.GetStream().WriteAsync(BitConverter.GetBytes(64L * 1024 * 1024));
        using var stop = new CancellationTokenSource();
        var trickle = TrickleAsync(client, stop.Token);
        try { await AssertClosedAsync(client, TimeSpan.FromSeconds(1.5)); }
        finally { stop.Cancel(); await trickle; }
        await WaitForAsync(() => server.ActiveSessions == 0);
    }

    [Fact]
    public async Task ConcurrentLimitIsSharedByListenersAndRecovers()
    {
        var limits = new SpeedTestServer.Limits(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        await using var server = new SpeedTestServer(0, limits);
        var secondAddress = IPAddress.Parse("127.0.0.2");
        Assert.True(server.TryAddListener(secondAddress));
        using var first = await ConnectAsync(server.BoundPort);
        using var second = await ConnectAsync(server.BoundPort, secondAddress);
        await WaitForAsync(() => server.ActiveSessions == 2);
        using var excess = await ConnectAsync(server.BoundPort, secondAddress);
        await AssertClosedAsync(excess);
        Assert.Equal(2, server.ActiveSessions);
        first.Dispose();
        await WaitForAsync(() => server.ActiveSessions == 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await SpeedTestServer.MeasureAsync(secondAddress.ToString(), server.BoundPort, 8192, timeout.Token) > 0);
    }

    [Fact]
    public async Task DisposeCancelsAndAwaitsSessionsAndIsRepeatable()
    {
        var limits = new SpeedTestServer.Limits(2, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var server = new SpeedTestServer(0, limits);
        using var first = await ConnectAsync(server.BoundPort);
        using var second = await ConnectAsync(server.BoundPort);
        await second.GetStream().WriteAsync(BitConverter.GetBytes(1024L));
        await WaitForAsync(() => server.ActiveSessions == 2);
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, server.ActiveSessions);
        await AssertClosedAsync(first);
        await AssertClosedAsync(second);
        Assert.False(server.TryAddListener(IPAddress.Parse("127.0.0.2")));
        await server.DisposeAsync();
    }

    private static async Task<TcpClient> ConnectAsync(int port, IPAddress? address = null)
    {
        var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(address ?? IPAddress.Loopback, port, timeout.Token);
        return client;
    }

    private static async Task AssertClosedAsync(TcpClient client, TimeSpan? duration = null)
    {
        using var timeout = new CancellationTokenSource(duration ?? TimeSpan.FromSeconds(3));
        try { Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token)); }
        catch (IOException) { }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task TrickleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await client.GetStream().WriteAsync(new byte[] { 1 }, ct);
                await Task.Delay(25, ct);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }
}
