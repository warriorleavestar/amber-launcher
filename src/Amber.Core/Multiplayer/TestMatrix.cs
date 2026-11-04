using Amber.Core.Localization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

public enum RouteKind { Direct, Relay, Node }

/// <summary>全条件测试里的一步：走哪条路、用不用完整局域网模式、开不开出口。</summary>
public sealed record MatrixStep(int Index, RouteKind Route, PathKind? Path, bool FullLan, bool Exit)
{
    public string Label => L.F(Exit ? "core.mp.matrix.label_exit" : "core.mp.matrix.label", Route switch
    {
        RouteKind.Direct => DirectText.Path(Path!.Value),
        RouteKind.Relay => L.T("core.mp.matrix.relay"),
        _ => L.T("core.mp.matrix.node"),
    }, FullLan ? L.T("core.mp.matrix.fulllan") : L.T("core.mp.matrix.defaultmode"));
}

public sealed record MatrixResult(
    MatrixStep Step,
    bool? Passed,
    long? ConnectMs = null,
    double? LatencyMs = null,
    double? ThroughputMbps = null,
    bool? ChatOk = null,
    bool? ExitOk = null,
    string Reason = "",
    bool Simulated = false);

public sealed record MatrixInputs(
    IReadOnlyList<Candidate> Host,
    IReadOnlyList<Candidate> Guest,
    int MemberCount,
    bool HasNode,
    bool FullLanAllowed,
    bool Simulated = false);

public interface IMatrixStepRunner
{
    Task<MatrixResult> RunAsync(MatrixStep step, CancellationToken ct);
}

public static class TestMatrix
{
    /// <summary>
    /// 根据双方实际有的地址排出要测的组合：IPv6（双方都有）、映射端口（任一方有）、局域网（同网段或模拟）、
    /// IPv4 打洞（普通）和打洞加喷洒（双方都有外部地址）、第三人中转（3 人以上）、联机节点（配置了的话）。
    /// 每条路先测默认模式；允许完整局域网时再测完整局域网，以及开出口。
    /// </summary>
    public static List<MatrixStep> Plan(MatrixInputs input)
    {
        bool Both(PathKind k) => input.Host.Any(c => c.Kind == k) && input.Guest.Any(c => c.Kind == k);
        var routes = new List<(RouteKind, PathKind?)>();
        if (Both(PathKind.IPv6)) routes.Add((RouteKind.Direct, PathKind.IPv6));
        if (input.Host.Concat(input.Guest).Any(c => c.Kind == PathKind.Mapped)) routes.Add((RouteKind.Direct, PathKind.Mapped));
        if (input.Simulated ? Both(PathKind.Lan) : SameSubnet(input.Host, input.Guest)) routes.Add((RouteKind.Direct, PathKind.Lan));
        if (Both(PathKind.Punch))
        {
            routes.Add((RouteKind.Direct, PathKind.Punch));
            routes.Add((RouteKind.Direct, PathKind.PunchSpray));
        }
        if (input.MemberCount >= 3) routes.Add((RouteKind.Relay, null));
        if (input.HasNode) routes.Add((RouteKind.Node, null));

        var steps = new List<MatrixStep>();
        foreach (var (route, path) in routes)
        {
            steps.Add(new MatrixStep(steps.Count + 1, route, path, false, false));
            if (!input.FullLanAllowed) continue;
            steps.Add(new MatrixStep(steps.Count + 1, route, path, true, false));
            steps.Add(new MatrixStep(steps.Count + 1, route, path, true, true));
        }
        return steps;
    }

    private static bool SameSubnet(IReadOnlyList<Candidate> a, IReadOnlyList<Candidate> b) =>
        a.Where(c => c.Kind == PathKind.Lan).Any(x => b.Where(c => c.Kind == PathKind.Lan).Any(y =>
            x.EndPoint.Address.AddressFamily == AddressFamily.InterNetwork && y.EndPoint.Address.AddressFamily == AddressFamily.InterNetwork
            && x.EndPoint.Address.GetAddressBytes().Take(3).SequenceEqual(y.EndPoint.Address.GetAddressBytes().Take(3))));

    /// <summary>
    /// 依次跑每一步。每步有超时，失败、超时、出错都记下原因接着跑下一步；
    /// 同一条路的完整局域网模式不通时，后面开出口的那步直接跳过。
    /// </summary>
    public static async Task<List<MatrixResult>> RunAsync(IReadOnlyList<MatrixStep> steps, IMatrixStepRunner runner, TimeSpan stepTimeout,
        IProgress<MatrixResult>? progress = null, bool simulated = false, CancellationToken ct = default)
    {
        var results = new List<MatrixResult>();
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            MatrixResult result;
            var prerequisite = step.Exit ? results.LastOrDefault(r => r.Step.Route == step.Route && r.Step.Path == step.Path && r.Step.FullLan && !r.Step.Exit) : null;
            if (prerequisite is { Passed: not true })
            {
                result = new MatrixResult(step, null, Reason: L.T("core.mp.matrix.prerequisitefailed"), Simulated: simulated);
            }
            else
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(stepTimeout);
                var watch = Stopwatch.StartNew();
                try
                {
                    result = await runner.RunAsync(step, limit.Token).WaitAsync(stepTimeout + TimeSpan.FromSeconds(5), ct);
                    result = result with { Simulated = simulated };
                }
                catch (Exception e) when (e is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
                {
                    result = new MatrixResult(step, false, Reason: L.P("core.mp.matrix.timeout", (long)Math.Round(stepTimeout.TotalSeconds), stepTimeout.TotalSeconds), Simulated: simulated);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
                {
                    result = new MatrixResult(step, false, Reason: L.T("core.mp.matrix.error") + e.Message, Simulated: simulated);
                }
            }
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    public static List<string> FormatTable(IEnumerable<MatrixResult> results)
    {
        var lines = new List<string> { L.T("core.mp.matrix.header") };
        foreach (var r in results)
        {
            static string Flag(bool? v) => v switch { true => L.T("core.mp.matrix.connected"), false => L.T("core.mp.matrix.unreachable"), _ => "-" };
            lines.Add(string.Join(" | ",
                r.Step.Index,
                r.Step.Label + (r.Simulated ? L.T("core.mp.matrix.simulated") : ""),
                r.Passed switch { true => L.T("core.mp.matrix.passed"), false => L.T("core.mp.matrix.failed"), _ => L.T("core.mp.matrix.skipped") },
                r.ConnectMs is { } c ? $"{c} ms" : "-",
                r.LatencyMs is { } l ? $"{l:F1} ms" : "-",
                r.ThroughputMbps is { } t ? $"{t:F1} Mbps" : "-",
                Flag(r.ChatOk),
                r.Step.Exit ? Flag(r.ExitOk) : "-",
                r.Reason));
        }
        return lines;
    }
}

/// <summary>简单的吞吐测试：加入方经隧道往房主发一段数据，房主回报收到的字节数。</summary>
public sealed class SpeedTestServer : IAsyncDisposable
{
    public const int Port = RoomChat.Port + 1;
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _cts = new();

    public SpeedTestServer(int port = Port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listeners.Add(listener);
        _ = AcceptLoopAsync(listener);
    }

    public int BoundPort => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;

    public bool TryAddListener(IPAddress address)
    {
        try
        {
            var listener = new TcpListener(address, BoundPort);
            listener.Start();
            _listeners.Add(listener);
            _ = AcceptLoopAsync(listener);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var header = new byte[8];
                        await stream.ReadExactlyAsync(header, _cts.Token);
                        var expected = BitConverter.ToInt64(header);
                        if (expected is <= 0 or > 64 * 1024 * 1024) return;
                        var buffer = new byte[64 * 1024];
                        long received = 0;
                        while (received < expected)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - received)), _cts.Token);
                            if (n == 0) break;
                            received += n;
                        }
                        await stream.WriteAsync(BitConverter.GetBytes(received), _cts.Token);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or EndOfStreamException) { }
                }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>发 bytes 字节，返回 Mbps。连不上或数据不全时抛异常。</summary>
    public static async Task<double> MeasureAsync(string host, int port, long bytes, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        var stream = client.GetStream();
        var watch = Stopwatch.StartNew();
        await stream.WriteAsync(BitConverter.GetBytes(bytes), ct);
        var chunk = new byte[64 * 1024];
        Random.Shared.NextBytes(chunk);
        for (long sent = 0; sent < bytes; sent += chunk.Length)
            await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, bytes - sent)), ct);
        var ack = new byte[8];
        await stream.ReadExactlyAsync(ack, ct);
        var received = BitConverter.ToInt64(ack);
        if (received != bytes) throw new IOException(L.F("core.mp.speed.incomplete", received, bytes));
        return bytes * 8 / 1_000_000.0 / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
    }
}
