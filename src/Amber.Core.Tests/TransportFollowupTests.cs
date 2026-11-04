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

    // 握手期限也管“发了问候、迟迟不发请求”的情况。
    [Fact]
    public async Task HandshakeDeadlineCoversStalledRequest()
    {
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: _ => true, handshakeTimeout: TimeSpan.FromMilliseconds(300));
        await client.WriteAsync(new byte[] { 5, 1, 0 });
        var choice = new byte[2];
        Assert.True(await SecureStream.ReadExactAsync(client, choice, default));
        // 不发请求，等期限到。
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // 重查失败后，下一次调用要重新查，不能一直拿那次失败的结果。
    [Fact]
    public async Task FailedRefreshIsNotCached()
    {
        var calls = 0;
        var ok = false;
        using var policy = new ExitPolicy([], [])
        {
            MaxPublicIpAge = TimeSpan.FromMilliseconds(10),
            Discover = _ => { Interlocked.Increment(ref calls); return Task.FromResult(ok); },
        };
        policy.SetReady(true);
        await Task.Delay(50);
        Assert.False(await policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default));
        ok = true;
        Assert.True(await policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default));
        Assert.Equal(2, calls);
    }

    // 往本机那头写出错（游戏连接坏了）先冒出来：封装通道不发“正常结束”。
    [Fact]
    public async Task LocalWriteErrorAbortsTunnel()
    {
        var profile = TransportProfile.For(TransportMode.High);
        var (ta, tb, ca, cb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        var tunnel = new SecureStream(ta, iSend, iReceive, profile);
        await using var peer = new SecureStream(tb, rSend, rReceive, profile);
        var pump = Pump.RunAsync(tunnel, new WriteFailsStream());
        await peer.WriteAsync("data"u8.ToArray());
        await peer.FlushAsync();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var n = await peer.ReadAsync(new byte[4]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (n == 0) throw new EndOfStreamException("正常结束");
        });
        Assert.False(peer.ClosedCleanly);
        ca.Dispose();
        cb.Dispose();
    }

    /// <summary>读的时候一直等（像一个没发数据的游戏连接），写的时候出错。</summary>
    private sealed class WriteFailsStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("游戏连接断了");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException(new IOException("游戏连接断了"));
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
    }

    // 关闭之后换钥：抛异常，也不会把密钥装回去。
    [Fact]
    public void RekeyAfterDisposeDoesNotReviveKey()
    {
        var (iSend, _, _, _) = Keys();
        iSend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => iSend.Rekey());
        Assert.False(iSend.HasKey);
        Assert.Throws<ObjectDisposedException>(() => iSend.EncryptWithAd([], "x"u8));
    }

    // 网络变化之前开始的 STUN 查询：结果回来时不能让出口变成可用（也不记“已查到公网 IP”）。
    [Fact]
    public async Task ResultFromBeforeAddressChangeIsDiscarded()
    {
        var gate = new TaskCompletionSource<bool>();
        var logs = new List<string>();
        var first = true;
        using var policy = new ExitPolicy([], [])
        {
            Discover = _ =>
            {
                if (!first) return Task.FromResult(false);
                first = false;
                return gate.Task;
            },
            ReadyInterval = TimeSpan.FromMinutes(10),
            RetryInterval = TimeSpan.FromMinutes(10),
            Debounce = TimeSpan.FromMinutes(10),
        };
        policy.SetReady(false);
        policy.Start(log: l => { lock (logs) logs.Add(l); });
        await Task.Delay(50);
        policy.SimulateAddressChange();
        gate.SetResult(true);
        await Task.Delay(150);
        Assert.False(policy.IsReady);
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("已查到公网 IP"));
    }

    // 路由表里嵌着 IPv4 的 IPv6 网段：前缀减 96；/96 及更宽的跳过（不当成默认路由）；IPv4、普通 IPv6 原样。
    [Theory]
    [InlineData("::ffff:10.0.0.0", 104, "10.0.0.0", 8)]
    [InlineData("::ffff:192.168.1.0", 120, "192.168.1.0", 24)]
    [InlineData("::ffff:0:0", 96, null, 0)]
    [InlineData("::ffff:0:0", 80, null, 0)]
    [InlineData("10.0.0.0", 8, "10.0.0.0", 8)]
    [InlineData("fd00::", 8, "fd00::", 8)]
    public void RouteDestinationConvertsEmbeddedPrefixes(string destination, int prefix, string? network, int expected)
    {
        var route = ExitPolicy.RouteDestination(IPAddress.Parse(destination), prefix);
        if (network is null) Assert.Null(route);
        else
        {
            Assert.NotNull(route);
            Assert.Equal(IPAddress.Parse(network), route.Value.Network);
            Assert.Equal(expected, route.Value.Prefix);
        }
    }

    [Fact]
    public void InNetworkClampsPrefix()
    {
        Assert.True(ExitPolicy.InNetwork(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("10.1.2.3"), 999));
        Assert.True(ExitPolicy.InNetwork(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("99.0.0.0"), -5));
    }
}
