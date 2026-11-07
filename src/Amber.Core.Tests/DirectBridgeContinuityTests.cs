using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class DirectBridgeContinuityTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static List<Candidate> Loopback(DirectBridge bridge) =>
        [new(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, bridge.SocketEndPoint.Port))];

    [Fact]
    public async Task ReprobePreservesEstablishedLocalTransportInBothDirections()
    {
        // 模拟有连接状态的 EasyTier：一直向第一次握手的 UDP 端点发送，不能用无状态回显掩盖端点变化。
        using var hostApp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guestApp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true,
            ((IPEndPoint)hostApp.Client.LocalEndPoint!).Port, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2), limit.Token));
        var localGuest = new IPEndPoint(IPAddress.Loopback, guest.LocalPort);
        await guestApp.SendAsync("before"u8.ToArray(), localGuest, limit.Token);
        var initial = await hostApp.ReceiveAsync(limit.Token);
        Assert.Equal("before"u8.ToArray(), initial.Buffer);
        await hostApp.SendAsync("ready"u8.ToArray(), initial.RemoteEndPoint, limit.Token);
        Assert.Equal("ready"u8.ToArray(), (await guestApp.ReceiveAsync(limit.Token)).Buffer);

        for (var step = 0; step < 3; step++)
        {
            host.DropPeers(preserveLocalTransport: true);
            guest.DropPeers(preserveLocalTransport: true);
            Assert.Empty(host.Peers);
            Assert.Empty(guest.Peers);
            Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2), limit.Token));

            // 重打洞后房主先发包：加入方不必重新发包登记 LocalClient，已有会话立即可用。
            await hostApp.SendAsync("host-first"u8.ToArray(), initial.RemoteEndPoint, limit.Token);
            Assert.Equal("host-first"u8.ToArray(), (await guestApp.ReceiveAsync(limit.Token)).Buffer);
            await guestApp.SendAsync("after"u8.ToArray(), localGuest, limit.Token);
            var next = await hostApp.ReceiveAsync(limit.Token);
            Assert.Equal(initial.RemoteEndPoint, next.RemoteEndPoint);
            Assert.Equal("after"u8.ToArray(), next.Buffer);
        }
    }

    [Fact]
    public async Task RevalidatingAnAlreadyConnectedPeerCompletesOnItsProbeReply()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2)));
        // 对方可能已先完成握手；不能再要求 Count 增加才返回成功。
        Assert.NotNull(await host.PunchAsync(Loopback(guest), NatKind.Cone, TimeSpan.FromSeconds(2)));
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PausedPeerCannotForwardOrResumeOnDisabledPathAndDefaultDropClosesIt()
    {
        using var hostApp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var guestApp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true,
            ((IPEndPoint)hostApp.Client.LocalEndPoint!).Port, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2), limit.Token));
        await guestApp.SendAsync("initial"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, guest.LocalPort), limit.Token);
        var initial = await hostApp.ReceiveAsync(limit.Token);
        var local = Assert.Single(host.Peers).Local!;
        host.DropPeers(preserveLocalTransport: true);
        guest.DropPeers(preserveLocalTransport: true);
        host.AllowedPaths.Clear();
        await hostApp.SendAsync("blocked"u8.ToArray(), initial.RemoteEndPoint, limit.Token);
        using (var noData = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await guestApp.ReceiveAsync(noData.Token));
        Assert.Null(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromMilliseconds(200), limit.Token));
        Assert.Empty(host.Peers);
        Assert.Empty(guest.Peers);
        host.DropPeers();
        Assert.True(local.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task ExpiredPausedTransportIsReleased()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { })
            { PeerTimeout = TimeSpan.FromMilliseconds(100) };
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2)));
        var local = Assert.Single(host.Peers).Local!;
        host.DropPeers(preserveLocalTransport: true);
        guest.DropPeers(preserveLocalTransport: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!local.SafeHandle.IsClosed) await Task.Delay(20, timeout.Token);
        Assert.Empty(host.Peers);
    }

    [Fact]
    public async Task PausedTransportsCountTowardsHostLimitAndExistingPeerCanResume()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        var clients = new List<UdpClient>();
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            for (var i = 0; i < DirectBridge.MaxHostPeers; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(client);
                await client.SendAsync(host.Seal(1, [0]), target, limit.Token);
                Assert.Equal(2, (await client.ReceiveAsync(limit.Token)).Buffer[0]);
            }
            var original = host.Peers.Single(p => p.Remote.Port == ((IPEndPoint)clients[0].Client.LocalEndPoint!).Port);
            host.DropPeers(preserveLocalTransport: true);
            using var extra = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await extra.SendAsync(host.Seal(1, [0]), target, limit.Token);
            using (var noAck = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await extra.ReceiveAsync(noAck.Token));
            Assert.Empty(host.Peers);

            await clients[0].SendAsync(host.Seal(1, [0]), target, limit.Token);
            while ((await clients[0].ReceiveAsync(limit.Token)).Buffer[0] != 2) { }
            Assert.Same(original, Assert.Single(host.Peers));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }
}
