using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// 打洞套接字的 NAT 保活：码发出去之后要等对方贴码，这段时间套接字必须一直有包出去，
/// 不然运营商回收映射，码里的外部端口就失效了（两地实测：房主等回复码 107～349 秒，每次都打不通）。
/// </summary>
public class DirectKeepAliveTests
{
    /// <summary>本机假 STUN 服务器：每个 Binding 请求都回 <paramref name="mappedPort"/>（返回值里记收到了几个请求）。</summary>
    private static (UdpClient Server, Func<int> Count) FakeStun(Func<int> mappedPort, CancellationToken ct)
    {
        var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var count = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var r = await server.ReceiveAsync(ct);
                    if (r.Buffer.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(r.Buffer) != 0x0001) continue;
                    Interlocked.Increment(ref count);
                    var response = new byte[32];
                    BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
                    BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), 0x2112A442);
                    r.Buffer.AsSpan(8, 12).CopyTo(response.AsSpan(8));
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
                    response[25] = 0x01;
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(mappedPort() ^ 0x2112));
                    BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(28), 0xCB007105 ^ 0x2112A442);
                    await server.SendAsync(response, r.RemoteEndPoint, ct);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }, ct);
        return (server, () => Volatile.Read(ref count));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(50);
    }

    [Fact]
    public async Task KeepsSendingWhileWaitingForPeer()
    {
        using var stop = new CancellationTokenSource();
        var (server, count) = FakeStun(() => 51234, stop.Token);
        using var _ = server;
        var logs = new List<string>();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, l => { lock (logs) logs.Add(l); })
        {
            NatKeepAliveInterval = TimeSpan.FromMilliseconds(100),
        };
        host.StartNatKeepAlive((IPEndPoint)server.Client.LocalEndPoint!, IPEndPoint.Parse("203.0.113.5:51234"));
        await WaitUntil(() => count() >= 3, TimeSpan.FromSeconds(5));
        Assert.True(count() >= 3);
        // 外部端口没变：不报警；STUN 回复也不会被当成坏的探测包记错误。
        await Task.Delay(200);
        lock (logs) Assert.Empty(logs);
        stop.Cancel();
    }

    [Fact]
    public async Task ReportsChangedMappingOnce()
    {
        using var stop = new CancellationTokenSource();
        var (server, count) = FakeStun(() => 40000, stop.Token);
        using var _ = server;
        var logs = new List<string>();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, l => { lock (logs) logs.Add(l); })
        {
            NatKeepAliveInterval = TimeSpan.FromMilliseconds(100),
        };
        host.StartNatKeepAlive((IPEndPoint)server.Client.LocalEndPoint!, IPEndPoint.Parse("203.0.113.5:51234"));
        await WaitUntil(() => count() >= 4, TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        lock (logs) Assert.Single(logs, l => l.Contains("51234") && l.Contains("40000"));
        stop.Cancel();
    }

    [Fact]
    public async Task KeepAliveDoesNotBreakPunching()
    {
        using var stop = new CancellationTokenSource();
        var (server, count) = FakeStun(() => 51234, stop.Token);
        using var _ = server;
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { }) { NatKeepAliveInterval = TimeSpan.FromMilliseconds(50) };
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { }) { NatKeepAliveInterval = TimeSpan.FromMilliseconds(50) };
        host.StartNatKeepAlive((IPEndPoint)server.Client.LocalEndPoint!, null);
        guest.StartNatKeepAlive((IPEndPoint)server.Client.LocalEndPoint!, null);
        var hostWait = host.WaitForPeerAsync(TimeSpan.FromSeconds(10));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        var peer = await guest.PunchAsync([new Candidate(PathKind.Lan, target)], NatKind.Cone, TimeSpan.FromSeconds(10));
        Assert.NotNull(peer);
        Assert.NotNull(await hostWait);
        Assert.True(count() > 0);
        stop.Cancel();
    }
}
