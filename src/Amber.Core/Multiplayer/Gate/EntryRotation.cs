using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 入口端口轮换（TOTP 原理）：端口 = 范围下限 + HMAC-SHA256(入口种子, "jay-entry-port-v1" ‖ 时间窗编号) mod 范围大小。
/// 只有拿到种子（邀请码或进服时网关下发的地址簿）的人才知道现在该连哪个端口，扫描器扫不到固定端口，泄露的旧地址过一个窗口就失效。
/// 这只是隐藏入口，不是加密：连接本身的保密仍然靠 Noise。服务端同时听上一个、当前、下一个时间窗的端口，容忍两边时钟差一个窗口。
/// </summary>
public sealed record GateRotation(int WindowSeconds, int MinPort, int MaxPort)
{
    public const int SeedLength = 32;
    /// <summary>默认范围停在 49152 以下：Windows 的动态端口段（49152–65535）里常有被 Hyper-V / WinNAT 保留、绑不上的端口。</summary>
    public static readonly GateRotation Default = new(600, 20000, 45000);

    public bool IsValid => WindowSeconds is >= 30 and <= 86400 && MinPort is >= 1024 and <= 65535 && MaxPort is >= 1024 and <= 65535 && MaxPort - MinPort >= 100;

    private long Window(DateTimeOffset time) => time.ToUnixTimeSeconds() / WindowSeconds;

    /// <summary>某个时间窗的端口。</summary>
    public int PortAt(ReadOnlySpan<byte> seed, long window)
    {
        Span<byte> message = stackalloc byte[17 + 8];
        "jay-entry-port-v1"u8.CopyTo(message);
        BinaryPrimitives.WriteInt64BigEndian(message[17..], window);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(seed, message, mac);
        var value = BinaryPrimitives.ReadUInt32BigEndian(mac);
        return MinPort + (int)(value % (uint)(MaxPort - MinPort + 1));
    }

    public int PortAt(ReadOnlySpan<byte> seed, DateTimeOffset time) => PortAt(seed, Window(time));

    /// <summary>客户端按这个顺序试：当前窗、上一个窗、下一个窗（去重）。服务端把它们全听着。</summary>
    public IReadOnlyList<int> CandidatePorts(ReadOnlySpan<byte> seed, DateTimeOffset time)
    {
        var w = Window(time);
        var ports = new List<int>(3);
        foreach (var window in new[] { w, w - 1, w + 1 })
        {
            var port = PortAt(seed, window);
            if (!ports.Contains(port)) ports.Add(port);
        }
        return ports;
    }

    /// <summary>每个入口（网关直连、各个节点）用自己的种子：从网关的主种子派生，知道一个入口的种子推不出别的。</summary>
    public static byte[] EntrySeed(ReadOnlySpan<byte> masterSeed, string entry) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSeed.ToArray(), SeedLength, [], Encoding.UTF8.GetBytes("jay-entry-seed-v1|" + entry));

    /// <summary>网关直连入口在派生种子时用的名字。</summary>
    public const string DirectEntry = "direct";

    internal void Write(List<byte> output)
    {
        output.Add((byte)(WindowSeconds >> 16));
        output.Add((byte)(WindowSeconds >> 8));
        output.Add((byte)WindowSeconds);
        output.Add((byte)(MinPort >> 8));
        output.Add((byte)MinPort);
        output.Add((byte)(MaxPort >> 8));
        output.Add((byte)MaxPort);
    }

    internal const int EncodedLength = 7;

    internal static GateRotation? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < EncodedLength) return null;
        var rotation = new GateRotation((data[0] << 16) | (data[1] << 8) | data[2], BinaryPrimitives.ReadUInt16BigEndian(data[3..]), BinaryPrimitives.ReadUInt16BigEndian(data[5..]));
        return rotation.IsValid ? rotation : null;
    }
}

/// <summary>一个入口的轮换信息：规则 + 这个入口的种子。</summary>
public sealed record GateEntryRotation(GateRotation Rotation, byte[] Seed)
{
    public IReadOnlyList<int> CandidatePorts(DateTimeOffset time) => Rotation.CandidatePorts(Seed, time);
}

/// <summary>
/// 一组监听端口：一个固定端口（可以关掉再开回同一个端口号）加若干轮换端口。每接到一条连接就交给回调。
/// 网关和节点共用：开轮换时关掉固定端口，只听当前前后三个时间窗的端口。
/// </summary>
internal sealed class EntryListeners : IAsyncDisposable
{
    private sealed record Slot(TcpListener Listener, CancellationTokenSource Cts, Task Loop);

    private readonly IPAddress _address;
    private readonly Action<TcpClient> _accepted;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Slot> _rotating = [];
    private Slot? _fixed;
    private bool _disposed;

    public int FixedPort { get; private set; }

    public EntryListeners(string listenAddress, int fixedPort, Action<TcpClient> accepted, Action<string>? log = null)
    {
        _address = IPAddress.Parse(listenAddress);
        _accepted = accepted;
        _log = log;
        _fixed = Open(fixedPort) ?? throw new SocketException((int)SocketError.AddressAlreadyInUse);
        FixedPort = ((IPEndPoint)_fixed.Listener.LocalEndpoint).Port;
    }

    public bool FixedOpen
    {
        get { lock (_gate) return _fixed is not null; }
    }

    public IReadOnlyList<int> RotatingPorts
    {
        get { lock (_gate) return [.. _rotating.Keys.Order()]; }
    }

    /// <summary>开关固定端口（关了再开还是同一个端口号）。</summary>
    public void SetFixedOpen(bool open)
    {
        Slot? closing = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (open && _fixed is null) _fixed = Open(FixedPort);
            else if (!open && _fixed is not null)
            {
                closing = _fixed;
                _fixed = null;
            }
        }
        if (closing is not null) Close(closing);
    }

    /// <summary>把轮换端口调成这一组：多的关掉，少的开上（开不了就跳过并记日志，比如端口被别的程序占了）。已经接进来的连接不受影响。</summary>
    public void SetRotating(IReadOnlyCollection<int> ports)
    {
        var closing = new List<Slot>();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var port in _rotating.Keys.Where(p => !ports.Contains(p)).ToList())
            {
                closing.Add(_rotating[port]);
                _rotating.Remove(port);
            }
            foreach (var port in ports)
                if (!_rotating.ContainsKey(port) && port != FixedPort && Open(port) is { } slot)
                    _rotating[port] = slot;
        }
        foreach (var slot in closing) Close(slot);
    }

    private Slot? Open(int port)
    {
        TcpListener listener;
        try
        {
            try
            {
                listener = new TcpListener(_address, port);
                if (_address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = true;
                listener.Start();
            }
            catch (SocketException) when (_address.Equals(IPAddress.IPv6Any))
            {
                // 有的 VPS / 容器关掉了 IPv6：退回只听 IPv4。
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
        }
        catch (SocketException e)
        {
            _log?.Invoke($"{port}: {e.Message}");
            return null;
        }
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        return new Slot(listener, cts, Task.Run(() => LoopAsync(listener, token)));
    }

    private async Task LoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _accepted(client);
        }
    }

    private static void Close(Slot slot)
    {
        slot.Cts.Cancel();
        slot.Listener.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        List<Slot> slots;
        lock (_gate)
        {
            _disposed = true;
            slots = [.. _rotating.Values];
            if (_fixed is not null) slots.Add(_fixed);
            _rotating.Clear();
            _fixed = null;
        }
        foreach (var slot in slots)
        {
            Close(slot);
            try { await slot.Loop; } catch (OperationCanceledException) { }
        }
    }
}
