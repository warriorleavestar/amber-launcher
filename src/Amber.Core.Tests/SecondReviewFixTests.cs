using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>第二次云端复审（dce9967）提出的修改项的回归测试。</summary>
public class SecondReviewFixTests
{
    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
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

    // #1：连接开着等到路由器重新拨号、换了公网 IP 之后才发 CONNECT，出口在收到请求时重查，新的公网 IP 也拒绝。
    [Fact]
    public async Task DelayedConnect_RechecksAtRequestTime()
    {
        var newIp = IPAddress.Parse("203.0.114.9");
        using var policy = new ExitPolicy([], []) { MaxPublicIpAge = TimeSpan.FromMilliseconds(100), StaleGrace = TimeSpan.FromMilliseconds(50) };
        var calls = 0;
        policy.Discover = _ =>
        {
            Interlocked.Increment(ref calls);
            policy.AddPublicAddress(newIp);   // 重新拨号后的新公网 IP
            return Task.FromResult(true);
        };
        policy.SetReady(true);
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: policy.Allows, ensureReady: c => policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), c));
        await Task.Delay(300);   // 连接开着，公网 IP 变旧了
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client, newIp.ToString(), 25565, default));
        Assert.Contains("拒绝", e.Message);
        Assert.Equal(1, calls);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // #1：收到请求时重查失败，回“出口还没准备好”。
    [Fact]
    public async Task DelayedConnect_RefreshFailsSaysNotReady()
    {
        using var policy = new ExitPolicy([], []) { MaxPublicIpAge = TimeSpan.FromMilliseconds(50), Discover = _ => Task.FromResult(false) };
        policy.SetReady(true);
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: policy.Allows, ensureReady: c => policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), c));
        await Task.Delay(150);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client, "8.8.8.8", 443, default));
        Assert.Contains("没准备好", e.Message);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // #1：公网 IP 太久没确认时，Allows 本身也拒绝（不只看“准备好了”这个标记）。
    [Fact]
    public async Task Allows_RefusesWhenPublicIpTooOld()
    {
        using var policy = new ExitPolicy([], []) { MaxPublicIpAge = TimeSpan.FromMilliseconds(50), StaleGrace = TimeSpan.FromMilliseconds(50) };
        policy.SetReady(true);
        Assert.True(policy.Allows(IPAddress.Parse("8.8.8.8")));
        await Task.Delay(250);
        Assert.True(policy.IsReady);
        Assert.False(policy.Allows(IPAddress.Parse("8.8.8.8")));
    }

    // #1：握手有期限，空闲连接到时间就断开。
    [Fact]
    public async Task Handshake_HasDeadline()
    {
        var (_, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: _ => true, handshakeTimeout: TimeSpan.FromMilliseconds(200));
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // #1：多条连接同时要重查时只查一次。
    [Fact]
    public async Task EnsureFresh_CoalescesConcurrentCallers()
    {
        var calls = 0;
        using var policy = new ExitPolicy([], [])
        {
            MaxPublicIpAge = TimeSpan.FromMilliseconds(50),
            Discover = async c =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(200, c);
                return true;
            },
        };
        policy.SetReady(true);
        await Task.Delay(100);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => policy.EnsureFreshAsync(TimeSpan.FromSeconds(2), default)));
        Assert.All(results, Assert.True);
        Assert.Equal(1, calls);
    }

    // #2：IPv6 地址和只有 IPv6 的名字不经出口（出口只转 IPv4），游戏照常直连。
    [Fact]
    public async Task IPv6Targets_AreNotRelayed()
    {
        static (string, int)? NoSrv(string _) => null;
        var skipped = "";
        Assert.Null(await JoinTarget.ResolveForRelayAsync("[2606:4700::1]:25566", default, null, NoSrv, r => skipped = r));
        Assert.Contains("IPv4", skipped);
        skipped = "";
        static Task<IPAddress[]> V6Only(string _, CancellationToken __) => Task.FromResult(new[] { IPAddress.Parse("2606:4700::1") });
        Assert.Null(await JoinTarget.ResolveForRelayAsync("v6.example.org", default, V6Only, NoSrv, r => skipped = r));
        Assert.Contains("IPv4", skipped);
        static Task<IPAddress[]> Both(string _, CancellationToken __) => Task.FromResult(new[] { IPAddress.Parse("2606:4700::1"), IPAddress.Parse("1.1.1.1") });
        Assert.NotNull(await JoinTarget.ResolveForRelayAsync("both.example.org", default, Both, NoSrv));
        // SRV 指向 IPv6 地址也一样。
        static (string, int)? SrvV6(string _) => ("2606:4700::1", 25570);
        Assert.Null(await JoinTarget.ResolveForRelayAsync("srv.example.org", default, Both, SrvV6));
    }

    // #2：出口拒绝 IPv6 目标时说清楚是“只转 IPv4”，不说成局域网地址。
    [Fact]
    public async Task Exit_SaysIPv6IsNotSupported()
    {
        using var policy = new ExitPolicy([], []);
        var (client, server, a, b) = await PairAsync();
        var serve = Socks5.ServeAsync(server, default, allow: policy.Allows);
        var e = await Assert.ThrowsAsync<IOException>(() => Socks5.ConnectAsync(client, "2606:4700::1", 443, default));
        Assert.Contains("只转 IPv4", e.Message);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        b.Dispose();
    }

    // #3：诊断报告里带模式段的房间码也隐藏密钥；EasyTier 输出里打印的密钥也去掉。
    [Fact]
    public void Redact_WrappedCodesAndEasyTierConfig()
    {
        var text = "房间码 AMB-T2D-K7Q2M9XD-4HZP8RW2N6TC-25565\nnetwork_secret: \"0123abcd4567ef\"\nnetwork_secret = Some(\"89abcdef0123\")";
        var redacted = NetDiagnostics.Redact(text, [], includePublicIps: false);
        Assert.DoesNotContain("4HZP8RW2N6TC", redacted);
        Assert.Contains("AMB-T2D-K7Q2M9XD-<密钥>-25565", redacted);
        Assert.DoesNotContain("0123abcd4567ef", redacted);
        Assert.DoesNotContain("89abcdef0123", redacted);
    }

    // #5：端口后面只拒绝“-T数字”，粘贴时带上的“-thanks”之类照常认；模式段两边的空格去掉。
    [Fact]
    public void RoomCode_SuffixRules()
    {
        Assert.True(RoomCode.TryParse("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565-thanks", out var a));
        Assert.Equal(25565, a.Port);
        Assert.False(RoomCode.TryParse("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565-T2", out _));
        Assert.True(RoomCode.TryParse("AMB- T2D -K7Q2M9XD-4HZP8RW2N6TC-25565", out var b));
        Assert.True(b.Tunnel.Disguise);
        Assert.Equal(TransportMode.High, b.Tunnel.Mode);
    }

    // 小项：64:ff9b:1::/48 不再当成嵌着 IPv4；前缀长度超出范围不会越界。
    [Fact]
    public void NormalizeAndPrefixEdgeCases()
    {
        Assert.Equal(AddressFamily.InterNetworkV6, ExitPolicy.Normalize(IPAddress.Parse("64:ff9b:1::a00:1")).AddressFamily);
        Assert.Equal(AddressFamily.InterNetwork, ExitPolicy.Normalize(IPAddress.Parse("64:ff9b::a00:1")).AddressFamily);
        Assert.True(ExitPolicy.InNetwork(IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.8.8"), 200));
    }
}
