using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>v0.5.8 的传输封装后续修正：出口状态在判断地址前变了、嵌着 IPv4 的 IPv6 写法、取消时的收尾。</summary>
public class TransportFollowupTests
{
    private static async Task<(NetworkStream A, NetworkStream B, TcpClient Ca, TcpClient Cb)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        return (a.GetStream(), b.GetStream(), a, b);
    }

    private static (CipherState, CipherState, CipherState, CipherState) Keys()
    {
        var psk = RandomNumberGenerator.GetBytes(32);
        using var i = new NoiseHandshake(NoisePattern.NNpsk0, true, [], psk);
        using var r = new NoiseHandshake(NoisePattern.NNpsk0, false, [], psk);
        r.ReadMessage(i.WriteMessage([]));
        i.ReadMessage(r.WriteMessage([]));
        var (iSend, iReceive) = i.Split();
        var (rSend, rReceive) = r.Split();
        return (iSend, iReceive, rSend, rReceive);
    }

    // 确认过公网 IP 之后、判断地址之前网络变了：所有地址都被拒绝时回“出口没准备好”，不说成“目标是局域网”。
    [Theory]
    [InlineData(false, "没准备好")]
    [InlineData(true, "出口拒绝连接")]
    public async Task RejectedTargetsRecheckReadiness(bool stillReady, string expected)
    {
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: _ => false, ensureReady: _ => Task.FromResult(true), stillReady: () => stillReady);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client, "8.8.8.8", 443, default));
        Assert.Contains(expected, e.Message);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // 6to4 写法 2002:7f00:0001::（里面是 127.0.0.1）：按 127.0.0.1 判断，也按 127.0.0.1 去连（不走 IPv6 路线）。
    [Fact]
    public async Task EmbeddedIPv4IsDialedAsIPv4()
    {
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        var accepted = target.AcceptTcpClientAsync();
        IPAddress? judged = null;
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: ip => { judged = ExitPolicy.Normalize(ip); return true; });
        await Socks5.ConnectAsync(client, "2002:7f00:1::", port, default).WaitAsync(TimeSpan.FromSeconds(5));
        using var remote = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IPAddress.Loopback, judged);
        Assert.Equal(AddressFamily.InterNetwork, ((IPEndPoint)remote.Client.RemoteEndPoint!).AddressFamily);
        a.Dispose();
        b.Dispose();
        target.Stop();
        try { await serve.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
    }

    // 联机结束（外面取消）时停下的转发：封装通道不发“正常结束”，对面不会把半截数据当成完整的。
    [Fact]
    public async Task CancelledPumpDoesNotCloseCleanly()
    {
        var profile = TransportProfile.For(TransportMode.High);
        var (ta, tb, ca, cb) = await PairAsync();
        var (la, lb, lca, lcb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        var tunnel = new SecureStream(ta, iSend, iReceive, profile);
        await using var peer = new SecureStream(tb, rSend, rReceive, profile);
        using var cts = new CancellationTokenSource();
        var pump = Pump.RunAsync(tunnel, la, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var n = await peer.ReadAsync(new byte[4]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (n == 0) throw new EndOfStreamException("正常结束");
        });
        Assert.False(peer.ClosedCleanly);
        foreach (var c in new[] { ca, cb, lca, lcb }) c.Dispose();
        GC.KeepAlive(lb);
    }
}
