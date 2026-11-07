using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class StunParseTests
{
    [Fact]
    public void ParsesXorMappedIPv4()
    {
        var id = new byte[12];
        for (var i = 0; i < 12; i++) id[i] = (byte)i;
        var response = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), 0x2112A442);
        id.CopyTo(response, 8);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
        response[25] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(51234 ^ 0x2112));
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(28), 0xCB007105 ^ 0x2112A442);
        Assert.Equal(IPEndPoint.Parse("203.0.113.5:51234"), Stun.ParseResponse(response, id));
        // 事务 id 不对就不认。
        var other = (byte[])id.Clone();
        other[0] = 99;
        Assert.Null(Stun.ParseResponse(response, other));
    }

    [Fact]
    public void ParsesXorMappedIPv6()
    {
        var id = new byte[12];
        var response = new byte[44];
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 24);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), 0x2112A442);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 20);
        response[25] = 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(40000 ^ 0x2112));
        var address = IPAddress.Parse("2408:8000::1").GetAddressBytes();
        var key = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(key, 0x2112A442);
        for (var i = 0; i < 16; i++) response[28 + i] = (byte)(address[i] ^ key[i]);
        Assert.Equal(IPEndPoint.Parse("[2408:8000::1]:40000"), Stun.ParseResponse(response, id));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("192.168.1.2", false)]
    [InlineData("100.70.1.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("2408:8000::1", true)]
    [InlineData("fe80::1", false)]
    public void PublicAddress(string address, bool expected) => Assert.Equal(expected, PortMapping.IsPublic(IPAddress.Parse(address)));
}

/// <summary>本机两个直连桥：打洞、转发、控制消息、禁用路径。</summary>
public class DirectBridgeTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);

    private static List<Candidate> Loopback(DirectBridge bridge) => [new(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, bridge.SocketEndPoint.Port))];

    [Fact]
    public async Task PunchForwardAndControl()
    {
        // 假的“房主 EasyTier”：回显收到的 UDP 包。
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var r = await echo.ReceiveAsync(stop.Token);
                await echo.SendAsync(r.Buffer, r.RemoteEndPoint, stop.Token);
            }
        });

        var logs = new List<string>();
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, echoPort, l => { lock (logs) logs.Add("H " + l); });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, l => { lock (logs) logs.Add("G " + l); });

        var hostWait = host.WaitForPeerAsync(TimeSpan.FromSeconds(10));
        var peer = await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(10));
        Assert.NotNull(peer);
        Assert.NotNull(await hostWait);
        Assert.Equal(PathKind.Lan, peer!.Path);

        // 假的“加入方 EasyTier”：发到加入方桥的本机端口，经直连到房主，再回来。
        using var app = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await app.SendAsync("hello"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, guest.LocalPort));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var back = await app.ReceiveAsync(timeout.Token);
        Assert.Equal("hello"u8.ToArray(), back.Buffer);

        var got = new TaskCompletionSource<JsonObject>();
        host.ControlReceived += (_, json) => got.TrySetResult(json);
        await guest.SendControlAsync(peer, new JsonObject { ["op"] = "hi" });
        Assert.Equal("hi", (await got.Task.WaitAsync(TimeSpan.FromSeconds(5)))["op"]!.GetValue<string>());

        await Task.Delay(2500);
        Assert.NotNull(peer.RttMs);
        stop.Cancel();
    }

    [Fact]
    public async Task DisabledPathIsNotUsed()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        // 模拟“局域网路径被禁用”：两边都只允许 IPv6，本机回环地址连不上。
        host.AllowedPaths.Clear();
        host.AllowedPaths.Add(PathKind.IPv6);
        guest.AllowedPaths.Clear();
        guest.AllowedPaths.Add(PathKind.IPv6);
        Assert.Null(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2)));
        Assert.Empty(host.Peers);
    }

    [Fact]
    public async Task WrongRoomKeyIsIgnored()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var intruder = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.OtherBridge, false, null, _ => { });
        Assert.Null(await intruder.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2)));
        Assert.Empty(host.Peers);
    }
}

public class TestMatrixTests
{
    private static Candidate C(PathKind k, string ep) => new(k, IPEndPoint.Parse(ep));

    [Fact]
    public void PlanUsesWhatBothSidesHave()
    {
        var host = new[] { C(PathKind.IPv6, "[2408::1]:1"), C(PathKind.Punch, "203.0.113.5:1"), C(PathKind.Lan, "192.168.1.5:1") };
        var guest = new[] { C(PathKind.Punch, "198.51.100.7:1"), C(PathKind.Lan, "10.0.0.5:1") };
        var steps = TestMatrix.Plan(new MatrixInputs(host, guest, 2, false, false));
        Assert.Equal([PathKind.Punch, PathKind.PunchSpray], steps.Select(s => s.Path!.Value));
        Assert.All(steps, s => Assert.False(s.FullLan));

        var full = TestMatrix.Plan(new MatrixInputs(host, [.. guest, C(PathKind.IPv6, "[2409::2]:1")], 3, true, true));
        Assert.Equal(3 * 5, full.Count);   // IPv6、打洞、喷洒、中转、节点，各 3 步
        Assert.Contains(full, s => s.Route == RouteKind.Relay && s.FullLan && s.Exit);
        Assert.Equal(Enumerable.Range(1, full.Count), full.Select(s => s.Index));
    }

    [Fact]
    public void SimulatedPlanIncludesLan()
    {
        var a = new[] { C(PathKind.Lan, "127.0.0.1:1") };
        Assert.Single(TestMatrix.Plan(new MatrixInputs(a, a, 2, false, false, Simulated: true)));
        Assert.Empty(TestMatrix.Plan(new MatrixInputs([C(PathKind.Lan, "192.168.1.5:1")], [C(PathKind.Lan, "10.0.0.5:1")], 2, false, false)));
    }

    private static async Task<MatrixResult> Stuck(MatrixStep step, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new MatrixResult(step, true);
    }

    private sealed class FakeRunner(Func<MatrixStep, CancellationToken, Task<MatrixResult>> run) : IMatrixStepRunner
    {
        public Task<MatrixResult> RunAsync(MatrixStep step, CancellationToken ct) => run(step, ct);
    }

    [Fact]
    public async Task CallerCancellationWaitsForStepCleanup()
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var runner = new FakeRunner(async (step, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                cleaning.SetResult();
                await release.Task;
                cleaned = true;
            }
            return new MatrixResult(step, true);
        });
        var run = TestMatrix.RunAsync([new(1, RouteKind.Direct, PathKind.Lan, false, false)],
            runner, TimeSpan.FromSeconds(30), ct: stop.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task RunnerTimeoutsErrorsAndSkips()
    {
        var steps = new List<MatrixStep>
        {
            new(1, RouteKind.Direct, PathKind.Punch, false, false),
            new(2, RouteKind.Direct, PathKind.Punch, true, false),
            new(3, RouteKind.Direct, PathKind.Punch, true, true),
            new(4, RouteKind.Node, null, false, false),
        };
        var runner = new FakeRunner(async (step, ct) => step.Index switch
        {
            1 => new MatrixResult(step, true, 120, 30, 50, true),
            2 => await Stuck(step, ct),   // 卡住，靠超时
            4 => throw new InvalidOperationException("没有节点"),
            _ => new MatrixResult(step, true),
        });
        var results = await TestMatrix.RunAsync(steps, runner, TimeSpan.FromSeconds(1), simulated: true);
        Assert.Equal([true, false, null, false], results.Select(r => r.Passed));
        Assert.Contains("超时", results[1].Reason);
        Assert.Contains("跳过", results[2].Reason);
        Assert.Contains("没有节点", results[3].Reason);
        var table = TestMatrix.FormatTable(results);
        Assert.Equal(5, table.Count);
        Assert.Contains("（模拟）", table[1]);
        Assert.Contains("50.0 Mbps", table[1]);
    }

    [Fact]
    public async Task SpeedTestOverLoopback()
    {
        await using var server = new SpeedTestServer(0);
        var mbps = await SpeedTestServer.MeasureAsync("127.0.0.1", server.BoundPort, 2 * 1024 * 1024, default);
        Assert.True(mbps > 0);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void RedactsSecretsCodesAndPublicIps()
    {
        var text = "secret 4HZP8RW2N6TC room AMB-K7Q2M9XD-4HZP8RW2N6TC-25565 code AMBDIRECT1-eyJhIjoxfQ args --network-secret abc " +
                   "stun 203.0.113.77:53115 lan 192.168.1.5 v6 2408:8207:1234:5678::1 time 12:00:01";
        var redacted = NetDiagnostics.Redact(text, ["4HZP8RW2N6TC"], includePublicIps: false);
        Assert.DoesNotContain("4HZP8RW2N6TC", redacted);
        Assert.Contains("AMB-K7Q2M9XD-<密钥>-25565", redacted);
        Assert.Contains("AMBDIRECT1-<直连码已隐藏>", redacted);
        Assert.Contains("--network-secret <房间密钥>", redacted);
        Assert.Contains("203.0.*.*:53115", redacted);
        Assert.Contains("192.168.1.5", redacted);
        Assert.Contains("2408:8207:*", redacted);
        Assert.Contains("12:00:01", redacted);

        var withIps = NetDiagnostics.Redact(text, ["4HZP8RW2N6TC"], includePublicIps: true);
        Assert.Contains("203.0.113.77", withIps);
        Assert.DoesNotContain("4HZP8RW2N6TC", withIps);
    }
}
