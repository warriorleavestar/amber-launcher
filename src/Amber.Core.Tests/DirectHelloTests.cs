using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// R3：直连通道的控制消息只认证不加密。加入方的 hello 以前带着完整房间码，路上的人抓包就能拿到密钥。
/// 这里在两个直连桥中间放一个记录所有 UDP 包的转发器，确认抓到的字节里既没有密钥原文，也没有能还原出它的编码。
/// </summary>
public sealed class DirectHelloTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);

    private static DirectCode GuestCode() => new(true, "abcdef0123456789", NatKind.Cone,
        [new Candidate(PathKind.Punch, IPEndPoint.Parse("198.51.100.7:40001")), new Candidate(PathKind.Lan, IPEndPoint.Parse("192.168.1.9:40001"))]);

    [Fact]
    public void HelloCarriesOnlyAddresses()
    {
        var hello = DirectSession.Hello(GuestCode());
        Assert.Equal(["c", "n", "op", "s"], hello.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Room.Secret, hello.ToJsonString());

        Assert.True(DirectSession.TryParseHello(hello, "abcdef0123456789", out var nat, out var candidates));
        Assert.Equal(NatKind.Cone, nat);
        Assert.Equal(GuestCode().Candidates, candidates);
        // 会话 id 不对（旧会话、别的房间）不认。
        Assert.False(DirectSession.TryParseHello(hello, "0123456789abcdef", out _, out _));
        // 类型不对的字段不抛异常。
        Assert.False(DirectSession.TryParseHello(new JsonObject { ["op"] = "hello", ["s"] = 5 }, "abcdef0123456789", out _, out _));
        Assert.True(DirectSession.TryParseHello(new JsonObject { ["s"] = "abcdef0123456789", ["n"] = 3, ["c"] = new JsonArray(1, new JsonArray(2, 3)) }, "abcdef0123456789", out _, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public async Task CapturedPacketsHoldNoRoomSecret()
    {
        var captured = new List<byte[]>();
        using var proxy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var proxyEnd = (IPEndPoint)proxy.Client.LocalEndPoint!;
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        var hostEnd = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var stop = new CancellationTokenSource();
        IPEndPoint? guestEnd = null;
        var forward = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var r = await proxy.ReceiveAsync(stop.Token);
                    lock (captured) captured.Add(r.Buffer);
                    var from = r.RemoteEndPoint;
                    if (from.Port == hostEnd.Port) { if (guestEnd is not null) await proxy.SendAsync(r.Buffer, guestEnd, stop.Token); }
                    else { guestEnd = from; await proxy.SendAsync(r.Buffer, hostEnd, stop.Token); }
                }
            }
            catch (OperationCanceledException) { }
        });

        var got = new TaskCompletionSource<JsonObject>();
        host.ControlReceived += (_, json) => got.TrySetResult(json);
        var hostWait = host.WaitForPeerAsync(TimeSpan.FromSeconds(10));
        var peer = await guest.PunchAsync([new Candidate(PathKind.Lan, proxyEnd)], NatKind.Cone, TimeSpan.FromSeconds(10));
        Assert.NotNull(peer);
        await hostWait;
        await guest.SendControlAsync(peer!, DirectSession.Hello(GuestCode()));
        var hello = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DirectSession.TryParseHello(hello, "abcdef0123456789", out _, out _));
        stop.Cancel();
        await forward;

        byte[] all;
        lock (captured) all = captured.SelectMany(b => b).ToArray();
        Assert.NotEmpty(all);
        foreach (var secret in new[] { Room.Secret, Room.ToString(), Room.EasyTierSecret })
            Assert.False(ContainsReversible(all, secret), "抓包里能还原出 " + secret);
        // 自检：以前的 hello（带完整码）确实会被认出来。
        Assert.True(ContainsReversible(Encoding.UTF8.GetBytes(new JsonObject { ["op"] = "hello", ["code"] = Room.ToString() }.ToJsonString()), Room.Secret));
    }

    /// <summary>R9：控制消息处理抛异常（op 是数字、坏 JSON、处理程序出错）只丢掉那一个包，接收循环继续收。</summary>
    [Fact]
    public async Task BadControlPacketDoesNotStopReceiveLoop()
    {
        var logs = new List<string>();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, l => { lock (logs) logs.Add(l); });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        var got = new List<string>();
        var second = new TaskCompletionSource();
        host.ControlReceived += (_, json) =>
        {
            // 和 DirectSession 以前的写法一样：op 不是字符串时 GetValue 抛 InvalidOperationException。
            var op = json["op"]!.GetValue<string>();
            lock (got) got.Add(op);
            if (op == "after") second.TrySetResult();
        };
        var hostWait = host.WaitForPeerAsync(TimeSpan.FromSeconds(10));
        var peer = await guest.PunchAsync([new Candidate(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port))], NatKind.Cone, TimeSpan.FromSeconds(10));
        Assert.NotNull(peer);
        await hostWait;
        await guest.SendControlAsync(peer!, new JsonObject { ["op"] = 5 });
        await guest.SendControlAsync(peer!, new JsonObject { ["op"] = "after" });
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["after"], got);
        lock (logs) Assert.Contains(logs, l => l.Contains(nameof(InvalidOperationException)));
    }

    /// <summary>R9：房主同时接的加入方有上限，超过的不登记（不建本机套接字、不开转发循环），已连上的照常收发。</summary>
    [Fact]
    public async Task HostPeerCountIsCapped()
    {
        var logs = new List<string>();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, l => { lock (logs) logs.Add(l); });
        var guests = new List<DirectBridge>();
        try
        {
            var target = new[] { new Candidate(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port)) };
            for (var i = 0; i < DirectBridge.MaxHostPeers + 2; i++)
            {
                var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
                guests.Add(guest);
                await guest.PunchAsync(target, NatKind.Cone, TimeSpan.FromSeconds(5));
            }
            await Task.Delay(300);
            Assert.Equal(DirectBridge.MaxHostPeers, host.Peers.Count);
            Assert.All(host.Peers, p => Assert.NotNull(p.Local));
            lock (logs) Assert.Contains(logs, l => l.Contains(DirectBridge.MaxHostPeers.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            foreach (var g in guests) await g.DisposeAsync();
        }
    }

    /// <summary>房主人满时先清掉不再回应的加入方：走掉的人不会一直占着位子；不接的加入方收不到探测确认。</summary>
    [Fact]
    public async Task StalePeersAreEvictedWhenFull()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { }) { PeerTimeout = TimeSpan.FromSeconds(2) };
        var endpoint = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        var target = new[] { new Candidate(PathKind.Lan, endpoint) };
        var gone = new List<UdpClient>();
        // 只发真实的已认证探测，不启动客户端心跳；建满之前固定存活状态，避免 CI 调度延迟让早来的连接过期。
        Action<DirectPeer> keepFresh = peer => peer.LastSeen = DateTime.MaxValue;
        host.Connected += keepFresh;
        try
        {
            const byte probe = 1, probeAck = 2;
            for (var i = 0; i < DirectBridge.MaxHostPeers; i++)
            {
                var guest = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                gone.Add(guest);
                using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await guest.SendAsync(host.Seal(probe, [0]), endpoint, handshake.Token);
                UdpReceiveResult response;
                do { response = await guest.ReceiveAsync(handshake.Token); }
                while (response.Buffer[0] != probeAck);
                Assert.Equal(endpoint, response.RemoteEndPoint);
                Assert.True(host.Verify(response.Buffer));
            }
            var original = host.Peers.ToArray();
            Assert.Equal(DirectBridge.MaxHostPeers, original.Length);
            Assert.All(original, peer => Assert.NotNull(peer.Local));
            // 满了：再来的人收不到确认（不会以为连上了），原来的 32 个位子保持不变。
            await using (var refused = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { }))
                Assert.Null(await refused.PunchAsync(target, NatKind.Cone, TimeSpan.FromSeconds(1.5)));
            Assert.Equal(DirectBridge.MaxHostPeers, host.Peers.Count);

            // 旧客户端不回心跳，但保留端口到 finally，避免新客户端复用旧端口而被当作原连接。
            // 明确进入超时状态，不睡固定秒数；也没有排队的心跳回复会把 LastSeen 改回去。
            var expiredAt = DateTime.UtcNow - host.PeerTimeout - TimeSpan.FromSeconds(1);
            foreach (var peer in original) peer.LastSeen = expiredAt;
            await using var late = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
            Assert.NotNull(await late.PunchAsync(target, NatKind.Cone, TimeSpan.FromSeconds(5)));
            var replacement = Assert.Single(host.Peers);
            Assert.Equal(late.SocketEndPoint.Port, replacement.Remote.Port);
            Assert.DoesNotContain(replacement, original);
            Assert.All(original, peer => Assert.True(peer.Local!.SafeHandle.IsClosed));
        }
        finally
        {
            host.Connected -= keepFresh;
            foreach (var guest in gone) guest.Dispose();
        }
    }

    /// <summary>原文、十六进制、Base64 / Base64url（任意对齐）里有没有 secret。</summary>
    private static bool ContainsReversible(byte[] data, string secret)
    {
        var text = Encoding.Latin1.GetString(data);
        if (text.Contains(secret, StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(secret)), StringComparison.OrdinalIgnoreCase)) return true;
        foreach (Match m in Regex.Matches(text, "[A-Za-z0-9+/_-]{8,}"))
        {
            for (var offset = 0; offset < 4 && offset < m.Value.Length; offset++)
            {
                var b64 = m.Value[offset..].Replace('-', '+').Replace('_', '/');
                b64 = b64[..(b64.Length / 4 * 4)];
                try
                {
                    if (Encoding.Latin1.GetString(Convert.FromBase64String(b64)).Contains(secret, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (FormatException) { }
            }
        }
        return false;
    }
}
