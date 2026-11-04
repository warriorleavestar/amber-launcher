using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>独立安全审查（11 项）每一项的回归测试。</summary>
public class SecurityReviewFixTests
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

    // 1. 没查到公网 IP 之前一律拒绝；别处查到的公网地址也拒绝。
    [Fact]
    public void Exit_FailsClosedUntilPublicIpKnown()
    {
        using var policy = new ExitPolicy([], []);
        policy.SetReady(false);
        Assert.False(policy.Allows(IPAddress.Parse("8.8.8.8")));
        policy.SetReady(true);
        Assert.True(policy.Allows(IPAddress.Parse("8.8.8.8")));
        ExitPolicy.ReportPublicAddress(IPAddress.Parse("198.51.7.9"));
        Assert.False(policy.Allows(IPAddress.Parse("198.51.7.9")));
        Assert.False(policy.Allows(IPAddress.Parse("::ffff:198.51.7.9")));
    }

    // 2. 公网 IPv6 的整个 /56（同一家里的别的 /64），以及路由表里的非默认路由。
    [Fact]
    public void Exit_BlocksSiblingPrefixesAndRoutes()
    {
        using var policy = new ExitPolicy([IPAddress.Parse("2400:1a00:b1:4e::25")], [(IPAddress.Parse("2400:1a00:b1:4e::"), 64), (IPAddress.Parse("2400:1a00:b1:4e::25"), 56)]);
        Assert.False(policy.Allows(IPAddress.Parse("2400:1a00:b1:4f::1")));
        Assert.False(policy.Allows(IPAddress.Parse("2400:1a00:b1:ff::1")));
        Assert.True(policy.Allows(IPAddress.Parse("2400:1a00:b2:0::1")));
        if (OperatingSystem.IsWindows())
        {
            var routes = ExitPolicy.NonDefaultRoutes();
            Assert.Contains(routes, r => r.Item1.Equals(IPAddress.Parse("127.0.0.0")) && r.Item2 == 8);
            Assert.DoesNotContain(routes, r => r.Item2 == 0);
            using var real = new ExitPolicy();
            real.SetReady(true);
            foreach (var (network, _) in routes) Assert.False(real.Allows(network), network.ToString());
        }
    }

    // 3. 本机不支持 ML-KEM 时极端加密报明确的 CryptographicException。
    [Fact]
    public void Extreme_WithoutMlKem_FailsClearly()
    {
        if (NoiseHandshake.HybridSupported) return;
        using var hs = new NoiseHandshake(NoisePattern.NNhfspsk0, true, [], RandomNumberGenerator.GetBytes(32));
        var e = Assert.ThrowsAny<CryptographicException>(() => hs.WriteMessage([]));
        Assert.Contains("ML-KEM", e.Message);
    }

    // 4. 不建网卡时只放行这台电脑真正提供的端口；完整局域网模式不限制。
    [Fact]
    public void EasyTier_WhitelistsOnlyServedPorts()
    {
        var room = RoomCode.Create(25565);
        var host = new EasyTierOptions(room, true, [], 15888);
        var args = EasyTierArgs.Build(host);
        var i = args.IndexOf("--tcp-whitelist");
        Assert.True(i >= 0);
        Assert.Equal(["25565", "47652", "47653"], args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains("--udp-whitelist", args);
        Assert.Contains("--rpc-portal-whitelist", args);
        var wrapped = new EasyTierOptions(room with { Tunnel = new TunnelSettings(TransportMode.High, false) }, true, [], 15888) { AllowProxy = true };
        Assert.Equal([47653, TunnelSettings.Port], EasyTierArgs.ServedPorts(wrapped).Order());
        var joiner = new EasyTierOptions(room, false, [], 15888, 30000);
        Assert.Equal([1], EasyTierArgs.ServedPorts(joiner));
        Assert.DoesNotContain("--tcp-whitelist", EasyTierArgs.Build(host with { Tun = true }));
    }

    // 5. 模式写进 EasyTier 的网络名和密钥；房间码里的模式段坏了直接算格式不对。
    [Theory]
    [InlineData("-T9")]
    [InlineData("-TX")]
    [InlineData("-T2Q")]
    [InlineData("-")]
    [InlineData("-T")]
    [InlineData("X")]
    public void RoomCode_RejectsBadModeSuffix(string suffix) =>
        Assert.False(RoomCode.TryParse("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565" + suffix, out _));

    [Fact]
    public void RoomCode_ModeChangesEasyTierNetwork()
    {
        var plain = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
        var high = plain with { Tunnel = new TunnelSettings(TransportMode.High, false) };
        var highDisguised = plain with { Tunnel = new TunnelSettings(TransportMode.High, true) };
        Assert.Equal(plain.NetworkName, plain.EasyTierNetworkName);
        Assert.Equal(plain.Secret, plain.EasyTierSecret);
        Assert.NotEqual(plain.EasyTierNetworkName, high.EasyTierNetworkName);
        Assert.NotEqual(plain.EasyTierSecret, high.EasyTierSecret);
        Assert.NotEqual(high.EasyTierSecret, highDisguised.EasyTierSecret);
        Assert.Contains(high.EasyTierSecret, high.Secrets);
        Assert.True(RoomCode.TryParse(highDisguised + " 快来", out var parsed));
        Assert.Equal(highDisguised, parsed);
    }

    // 6. 游戏也拿到 Minecraft 自己的代理参数。
    [Fact]
    public void GameProxy_PassesMinecraftProxyArgs()
    {
        var proxy = new GameProxy(1080, "10.233.144.1", _ => Task.FromResult<Stream>(new MemoryStream()));
        Assert.Equal(["--proxyHost", "127.0.0.1", "--proxyPort", "1080"], proxy.GameArgs);
        Assert.Contains("-DsocksProxyPort=1080", proxy.JvmArgs);
    }

    // 7. 正常结束有认证过的结束记录；没有它就断开算截断。
    [Fact]
    public async Task Stream_AuthenticatedCloseVersusTruncation()
    {
        var profile = TransportProfile.For(TransportMode.High);
        var (a, b, ca, cb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        var client = new SecureStream(a, iSend, iReceive, profile);
        await using var server = new SecureStream(b, rSend, rReceive, profile);
        await client.WriteAsync("hi"u8.ToArray());
        await client.DisposeAsync();
        var buf = new byte[16];
        Assert.Equal(2, await server.ReadAsync(buf));
        Assert.Equal(0, await server.ReadAsync(buf));
        Assert.True(server.ClosedCleanly);

        var (a2, b2, ca2, cb2) = await PairAsync();
        var (i2s, i2r, r2s, r2r) = Keys();
        var client2 = new SecureStream(a2, i2s, i2r, profile);
        await using var server2 = new SecureStream(b2, r2s, r2r, profile);
        await client2.WriteAsync("hi"u8.ToArray());
        ca2.Client.Close();
        Assert.Equal(2, await server2.ReadAsync(buf));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await server2.ReadAsync(buf));
        Assert.False(server2.ClosedCleanly);
        ca.Dispose();
        cb.Dispose();
        cb2.Dispose();
    }

    // 8. 空闲时也按时间换钥；对方太久不换钥就断开。
    [Fact]
    public async Task Stream_RekeysWhileIdle_AndReceiverEnforcesKeyAge()
    {
        var fast = TransportProfile.For(TransportMode.High) with { RekeyEvery = TimeSpan.FromMilliseconds(150) };
        var (a, b, ca, cb) = await PairAsync();
        var (iSend, iReceive, rSend, rReceive) = Keys();
        await using (var client = new SecureStream(a, iSend, iReceive, fast))
        await using (var server = new SecureStream(b, rSend, rReceive, fast))
        {
            var reader = Task.Run(async () =>
            {
                var buf = new byte[5];
                return await SecureStream.ReadExactAsync(server, buf, default) ? System.Text.Encoding.ASCII.GetString(buf) : null;
            });
            await Task.Delay(700);
            Assert.True(client.Rekeys >= 2, $"空闲时换钥 {client.Rekeys} 次");
            await client.WriteAsync("after"u8.ToArray());
            Assert.Equal("after", await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var (a2, b2, ca2, cb2) = await PairAsync();
        var (i2s, i2r, r2s, r2r) = Keys();
        await using var lazySender = new SecureStream(a2, i2s, i2r, TransportProfile.For(TransportMode.High));
        await using var strictReceiver = new SecureStream(b2, r2s, r2r, fast);
        await Task.Delay(600);
        await lazySender.WriteAsync("x"u8.ToArray());
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await strictReceiver.ReadAsync(new byte[4]));
        ca.Dispose();
        cb.Dispose();
        ca2.Dispose();
        cb2.Dispose();
    }

    // 9. 关闭后密钥清零。
    [Fact]
    public void Keys_AreErasedOnDispose()
    {
        var (iSend, _, _, _) = Keys();
        Assert.True(iSend.HasKey);
        iSend.Dispose();
        Assert.False(iSend.HasKey);
    }

    // 10. 出口的日志里没有成员访问的目标。
    [Fact]
    public async Task Exit_LogsNoDestinations()
    {
        var lines = new List<string>();
        var exit = new TcpListener(IPAddress.Loopback, 0);
        exit.Start();
        var port = ((IPEndPoint)exit.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await exit.AcceptTcpClientAsync();
            await Socks5.ServeAsync(c.GetStream(), default, l => { lock (lines) lines.Add(l); });
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client.GetStream(), "192.168.77.5", 8080, default));
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        exit.Stop();
        lock (lines)
        {
            Assert.NotEmpty(lines);
            Assert.DoesNotContain(lines, l => l.Contains("192.168.77.5") || l.Contains("8080"));
        }
    }

    // 11. 进服务器的地址：SRV、端口范围、本机和局域网名字不经出口。
    [Theory]
    [InlineData("[2001:db8::1]:25565", "2001:db8::1", 25565, true)]
    [InlineData("[2001:db8::1]:70000", "", 0, false)]
    [InlineData("[2001:db8::1]:0", "", 0, false)]
    [InlineData("mc.example.org:99999", "", 0, false)]
    [InlineData("mc.example.org", "mc.example.org", 25565, true)]
    [InlineData("2001:db8::7", "2001:db8::7", 25565, true)]
    public void JoinTarget_Parse(string input, string host, int port, bool ok)
    {
        Assert.Equal(ok, JoinTarget.TryParse(input, out var h, out var p, out _));
        if (ok) Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("myserver")]
    [InlineData("nas.local")]
    [InlineData("box.lan")]
    [InlineData("pc.home.arpa")]
    public void JoinTarget_LocalNames(string host) => Assert.True(JoinTarget.IsLocalName(host));

    [Fact]
    public async Task JoinTarget_ResolveForRelay()
    {
        static Task<IPAddress[]> Public(string _, CancellationToken __) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        static Task<IPAddress[]> Private(string _, CancellationToken __) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.9") });
        static (string, int)? NoSrv(string _) => null;
        static (string, int)? Srv(string name) => name == "_minecraft._tcp.play.example.org" ? ("mc1.example.org", 25570) : null;

        Assert.Equal(("play.example.org", 25565), await JoinTarget.ResolveForRelayAsync("play.example.org", default, Public, NoSrv));
        Assert.Equal(("mc1.example.org", 25570), await JoinTarget.ResolveForRelayAsync("play.example.org", default, Public, Srv));
        Assert.Equal(("play.example.org", 25566), await JoinTarget.ResolveForRelayAsync("play.example.org:25566", default, Public, Srv));
        Assert.Null(await JoinTarget.ResolveForRelayAsync("intranet.example.org", default, Private, NoSrv));
        Assert.Null(await JoinTarget.ResolveForRelayAsync("localhost:25565", default, Public, NoSrv));
        Assert.Null(await JoinTarget.ResolveForRelayAsync("192.168.1.5:25565", default, Public, NoSrv));
        Assert.Equal(("1.2.3.4", 25565), await JoinTarget.ResolveForRelayAsync("1.2.3.4", default, Public, NoSrv));
    }

    [Fact]
    public void JoinTarget_RealSrvLookupDoesNotCrash()
    {
        // 真的查一次（没有这条记录也行），确认 DnsQuery 的结构读法不会崩。
        _ = JoinTarget.LookupSrv("_minecraft._tcp.example.org");
    }
}
