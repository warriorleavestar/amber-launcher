using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>云端复审（b3c3f00）提出的必修项和建议项的回归测试。</summary>
public class ReverificationFixTests
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

    // R1：定时器赢了很多次之后，地址变化的唤醒仍然马上生效（不会被之前留下的等待者吞掉）。
    [Fact]
    public async Task R1_WakeAfterManyTimerRounds()
    {
        var calls = 0;
        using var policy = new ExitPolicy([], [])
        {
            Discover = _ => { Interlocked.Increment(ref calls); return Task.FromResult(true); },
            ReadyInterval = TimeSpan.FromMilliseconds(20),
            Debounce = TimeSpan.FromMilliseconds(10),
        };
        policy.Start();
        await Task.Delay(400);   // 定时器赢了十几轮
        var before = Volatile.Read(ref calls);
        policy.ReadyInterval = TimeSpan.FromMinutes(10);
        await Task.Delay(100);
        before = Volatile.Read(ref calls);
        policy.SimulateAddressChange();
        Assert.False(policy.IsReady);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!policy.IsReady && watch.ElapsedMilliseconds < 2000) await Task.Delay(10);
        Assert.True(policy.IsReady, "地址变化后应该很快重查并恢复");
        Assert.True(Volatile.Read(ref calls) > before);
    }

    // R3：地址变化之前开始的查询，结果不能让出口变成可用。
    [Fact]
    public async Task R3_StaleDiscoveryDoesNotReenable()
    {
        var gate = new TaskCompletionSource<bool>();
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
        policy.Start();
        await Task.Delay(50);
        policy.SimulateAddressChange();   // 查询进行中，网络变了
        gate.SetResult(true);            // 旧的查询成功返回
        await Task.Delay(100);
        Assert.False(policy.IsReady);
    }

    // #1(a)：公网 IP 查到太久时，新连接先重查（路由器重新拨号会换 IP）；重查失败就拒绝。
    [Fact]
    public async Task PppoeRedial_RefreshesBeforeAllowing()
    {
        var calls = 0;
        var ok = true;
        using var policy = new ExitPolicy([], [])
        {
            Discover = _ => { Interlocked.Increment(ref calls); return Task.FromResult(ok); },
            MaxPublicIpAge = TimeSpan.FromMilliseconds(50),
        };
        policy.SetReady(true);
        await Task.Delay(100);
        Assert.True(await policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default));
        Assert.Equal(1, calls);
        // 刚查过：不再查。
        Assert.True(await policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default));
        Assert.Equal(1, calls);
        await Task.Delay(100);
        ok = false;
        Assert.False(await policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default));
        Assert.False(policy.Allows(IPAddress.Parse("8.8.8.8")));
    }

    // R7：出口没准备好时回专门的答复，加入方看到“出口还没准备好”。
    [Fact]
    public async Task R7_NotReadyExitSaysSo()
    {
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var port = ((IPEndPoint)exit.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default, allow: _ => true, ready: false);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client.GetStream(), "example.org", 443, default));
        Assert.Contains("没准备好", e.Message);
        exit.Stop();
    }

    // R2：DisposeAsync 等结束记录发出去之后才关连接，对方一定看到“正常结束”。
    [Fact]
    public async Task R2_DisposeAsyncSendsCloseBeforeSocketCloses()
    {
        var profile = TransportProfile.For(TransportMode.High);
        var (a, b, ca, cb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        var server = new SecureStream(b, rSend, rReceive, profile);
        await using var client = new SecureStream(a, iSend, iReceive, profile);
        await server.WriteAsync("bye"u8.ToArray());
        await server.DisposeAsync();
        cb.Dispose();   // 服务端的 using (client) 紧接着关 TCP
        var buf = new byte[3];
        Assert.True(await SecureStream.ReadExactAsync(client, buf, default));
        Assert.Equal(0, await client.ReadAsync(new byte[4]));
        Assert.True(client.ClosedCleanly);
        ca.Dispose();
    }

    // R8/R9：本机那头出错断开时，封装通道不发“正常结束”，对方会当成截断。
    [Fact]
    public async Task R9_LocalErrorAbortsTunnelWithoutClose()
    {
        var profile = TransportProfile.For(TransportMode.High);
        var (a, b, ca, cb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        var tunnel = new SecureStream(a, iSend, iReceive, profile);
        await using var peer = new SecureStream(b, rSend, rReceive, profile);
        var pump = Pump.RunAsync(tunnel, new FailingStream());
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await peer.ReadAsync(new byte[4]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(peer.ClosedCleanly);
        ca.Dispose();
        cb.Dispose();
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("本机连接出错");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException<int>(new IOException("本机连接出错"));
        public override void Write(byte[] buffer, int offset, int count) { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
    }

    // R10：关闭后的加解密报错，不会退回不加密。
    [Fact]
    public void R10_DisposedCipherNeverFailsOpen()
    {
        var (iSend, _, _, _) = Keys();
        iSend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => iSend.EncryptWithAd([], "x"u8));
        Assert.Throws<ObjectDisposedException>(() => iSend.Rekey());
    }

    // R4：本机代理软件的假 IP（198.18.0.0/15）当成没解析，交给出口；R5：名字太长不打断启动。
    [Fact]
    public async Task R4_FakeIpAndLongNames()
    {
        static Task<IPAddress[]> Fake(string _, CancellationToken __) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") });
        static (string, int)? NoSrv(string _) => null;
        Assert.True(JoinTarget.IsFakeIp(IPAddress.Parse("198.19.255.1")));
        Assert.Equal(new RelayPlan("play.example.org", 25565, "play.example.org", 25565), await JoinTarget.ResolveForRelayAsync("play.example.org", default, Fake, NoSrv));
        var skipped = "";
        Assert.Null(await JoinTarget.ResolveForRelayAsync(new string('a', 300) + ".org", default, Fake, NoSrv, r => skipped = r));
        Assert.NotEmpty(skipped);
    }

    // 小项：IPv6 里嵌着的 IPv4 地址换成 IPv4 后，网段判断不会越界。
    [Fact]
    public void MappedPrefixDoesNotThrow()
    {
        Assert.False(ExitPolicy.InNetwork(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:10.0.0.0"), 104));
        using var real = new ExitPolicy();
        real.RefreshLocal();
    }
}
