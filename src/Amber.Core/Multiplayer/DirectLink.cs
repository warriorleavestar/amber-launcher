using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>直连的几种路径，按优先顺序排列。</summary>
public enum PathKind
{
    /// <summary>IPv6 公网地址直连。</summary>
    IPv6,
    /// <summary>路由器映射的端口（UPnP / NAT-PMP）。</summary>
    Mapped,
    /// <summary>同一个局域网里的地址。</summary>
    Lan,
    /// <summary>IPv4 打洞（STUN 查到的外部地址，含端口预测）。</summary>
    Punch,
    /// <summary>IPv4 打洞加“生日”喷洒：对方是对称 NAT 时，往一批端口同时发。</summary>
    PunchSpray,
}

public sealed record Candidate(PathKind Kind, IPEndPoint EndPoint)
{
    public override string ToString() => $"{DirectText.Path(Kind)} {EndPoint}";
}

public static class DirectText
{
    public static string Path(PathKind kind) => kind switch
    {
        PathKind.IPv6 => L.T("core.mp.path.ipv6"),
        PathKind.Mapped => L.T("core.mp.path.mapped"),
        PathKind.Lan => L.T("core.mp.path.lan"),
        PathKind.Punch => L.T("core.mp.path.punch"),
        PathKind.PunchSpray => L.T("core.mp.path.spray"),
        _ => kind.ToString(),
    };

    public static string Nat(NatKind kind) => kind switch
    {
        NatKind.Open => L.T("core.mp.nat.open"),
        NatKind.Cone => L.T("core.mp.nat.cone"),
        NatKind.Symmetric => L.T("core.mp.nat.symmetric"),
        _ => L.T("core.mp.nat.unknown"),
    };
}

/// <summary>
/// 直连邀请码 / 回复码：AMBDIRECT1-… / AMBREPLY1-…（base64url 编码的 JSON）。
/// 里面有房间码（含密钥）、本次会话 id、NAT 类型和能连到自己的地址。只发给要一起玩的人。
/// </summary>
public sealed record DirectCode(bool IsReply, RoomCode Room, string Session, NatKind Nat, IReadOnlyList<Candidate> Candidates)
{
    public const int Version = 1;
    private const string InvitePrefix = "AMBDIRECT";
    private const string ReplyPrefix = "AMBREPLY";

    /// <summary>对方地址里有 IPv6、映射端口或者对方有公网地址：加入方直接连得上，不用再发回复码。</summary>
    public bool DirectlyReachable => Nat == NatKind.Open || Candidates.Any(c => c.Kind is PathKind.IPv6 or PathKind.Mapped);

    public string Encode()
    {
        var json = Addresses();
        json["r"] = Room.ToString();
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{(IsReply ? ReplyPrefix : InvitePrefix)}{Version}-{body}";
    }

    /// <summary>
    /// 只有会话 id、NAT 类型和地址，没有房间码：经直连通道发的 hello 用它。
    /// 直连通道的控制消息只有 HMAC 认证、没有加密，路上的人看得到内容，所以绝不能带房间码（所有密钥都由它派生）。
    /// </summary>
    public JsonObject Addresses() => new()
    {
        ["s"] = Session,
        ["n"] = Nat.ToString(),
        ["c"] = new JsonArray(Candidates.Select(c => (JsonNode?)new JsonArray(c.Kind.ToString(), c.EndPoint.ToString())).ToArray()),
    };

    /// <summary>解析 <see cref="Addresses"/> 的内容（对方的 hello）。会话 id 长度不对时返回 false。</summary>
    public static bool TryParseAddresses(JsonObject? json, out string session, out NatKind nat, out List<Candidate> candidates)
    {
        session = "";
        nat = NatKind.Unknown;
        candidates = [];
        if (json is null) return false;
        try
        {
            session = (json["s"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "";
            if (session.Length is < 8 or > 64) return false;
            nat = (json["n"] as JsonValue)?.TryGetValue<string>(out var n) == true && Enum.TryParse<NatKind>(n, out var kind) && Enum.IsDefined(kind) ? kind : NatKind.Unknown;
            foreach (var item in json["c"] as JsonArray ?? [])
            {
                if (item is not JsonArray pair || pair.Count != 2) continue;
                if ((pair[0] as JsonValue)?.TryGetValue<string>(out var k) == true && Enum.TryParse<PathKind>(k, out var path) && Enum.IsDefined(path)
                    && (pair[1] as JsonValue)?.TryGetValue<string>(out var e) == true && IPEndPoint.TryParse(e, out var ep) && ep.Port > 0)
                    candidates.Add(new Candidate(path, ep));
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>解析粘贴的文字。版本不对时 error 说明原因。</summary>
    public static bool TryParse(string? text, out DirectCode code, out string? error)
    {
        code = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        var start = upper.IndexOf(InvitePrefix, StringComparison.Ordinal);
        var isReply = false;
        if (start < 0)
        {
            start = upper.IndexOf(ReplyPrefix, StringComparison.Ordinal);
            isReply = start >= 0;
        }
        if (start < 0)
        {
            error = L.T("core.mp.code.unrecognized");
            return false;
        }
        var rest = text[(start + (isReply ? ReplyPrefix : InvitePrefix).Length)..];
        var dash = rest.IndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[..dash], out var version))
        {
            error = L.T("core.mp.code.incomplete");
            return false;
        }
        if (version != Version)
        {
            error = L.F("core.mp.code.version", version, Version);
            return false;
        }
        var body = new string(rest[(dash + 1)..].TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        try
        {
            var b64 = body.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!.AsObject();
            if (!RoomCode.TryParse(json["r"]?.GetValue<string>(), out var room)) throw new FormatException();
            var candidates = new List<Candidate>();
            foreach (var item in json["c"] as JsonArray ?? [])
            {
                if (item is not JsonArray pair || pair.Count != 2) continue;
                if (Enum.TryParse<PathKind>(pair[0]?.GetValue<string>(), out var kind) && IPEndPoint.TryParse(pair[1]?.GetValue<string>() ?? "", out var ep) && ep.Port > 0)
                    candidates.Add(new Candidate(kind, ep));
            }
            var nat = Enum.TryParse<NatKind>(json["n"]?.GetValue<string>(), out var n) ? n : NatKind.Unknown;
            var session = json["s"]?.GetValue<string>() ?? "";
            if (session.Length is < 8 or > 64) throw new FormatException();
            code = new DirectCode(isReply, room, session, nat, candidates);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or NullReferenceException)
        {
            error = L.T("core.mp.code.corrupted");
            return false;
        }
    }
}

/// <summary>收集能连到本机的地址：IPv6 公网地址、局域网地址、STUN 查到的外部地址、路由器映射。</summary>
public static class DirectGatherer
{
    public sealed record Gathered(List<Candidate> Candidates, NatKind Nat, string NatDetail, PortMappingResult? Mapping);

    public static async Task<Gathered> GatherAsync(Socket socket, IReadOnlyList<string> stunServers, bool tryPortMapping, Action<string> log, CancellationToken ct = default)
    {
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var list = new List<Candidate>();
        foreach (var address in Stun.LocalAddresses())
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && PortMapping.IsPublic(address) && !address.IsIPv6Teredo && !address.IsIPv4MappedToIPv6)
                list.Add(new Candidate(PathKind.IPv6, new IPEndPoint(address, port)));
            else if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                list.Add(new Candidate(PathKind.Lan, new IPEndPoint(address, port)));
        }
        log(list.Any(c => c.Kind == PathKind.IPv6)
            ? L.T("core.mp.gather.ipv6") + string.Join(L.T("core.mp.format.listseparator"), list.Where(c => c.Kind == PathKind.IPv6).Select(c => c.EndPoint.Address))
            : L.T("core.mp.gather.noipv6"));
        log(L.T("core.mp.gather.lan") + string.Join(L.T("core.mp.format.listseparator"), list.Where(c => c.Kind == PathKind.Lan).Select(c => c.EndPoint.Address)));

        var watch = Stopwatch.StartNew();
        var stun = await Stun.DetectAsync(socket, stunServers, ct);
        log(stun.Mapped is { } m
            ? L.F("core.mp.gather.stun_external", watch.ElapsedMilliseconds, stun.Detail, m)
            : L.F("core.mp.gather.stun", watch.ElapsedMilliseconds, stun.Detail));
        if (stun.Mapped is { } reported) Transport.ExitPolicy.ReportPublicAddress(reported.Address);
        if (stun.Mapped is { } mapped && mapped.AddressFamily == AddressFamily.InterNetwork)
            list.Add(new Candidate(PathKind.Punch, mapped));

        PortMappingResult? mapping = null;
        if (tryPortMapping)
        {
            mapping = await PortMapping.TryMapAsync(port, log, ct);
            if (mapping is not null)
            {
                list.Add(new Candidate(PathKind.Mapped, mapping.External));
                Transport.ExitPolicy.ReportPublicAddress(mapping.External.Address);
            }
        }
        return new Gathered(list, mapping is not null && stun.Kind != NatKind.Open ? NatKind.Open : stun.Kind, stun.Detail, mapping);
    }
}

/// <summary>一条打通的连接。</summary>
public sealed class DirectPeer
{
    public required IPEndPoint Remote { get; init; }
    public required PathKind Path { get; init; }
    public long ConnectedMs { get; init; }
    internal Socket? Local { get; set; }
    internal IPEndPoint? LocalClient { get; set; }
    internal readonly ConcurrentDictionary<uint, long> PendingPings = new();
    internal int PingsSent;
    internal int PongsReceived;
    public double? RttMs { get; internal set; }
    public double LossPercent => PingsSent == 0 ? 0 : Math.Max(0, 100.0 * (PingsSent - PongsReceived - PendingPings.Count) / PingsSent);
    public long BytesIn;
    public long BytesOut;
    public DateTime LastSeen { get; internal set; } = DateTime.UtcNow;
}

/// <summary>
/// 直连桥：Amber 自己用一个 UDP 套接字打洞（所有路径共用这一个套接字），打通后在两边的 EasyTier 之间转发数据包。
/// EasyTier 只在本机回环上收发，数据本身仍是 EasyTier 加密过的；打洞探测包用房间密钥派生的 HMAC 认证，
/// 不认识的地址发来的数据一律丢掉。房主可以同时连多个加入方，加入方之间由 EasyTier 经房主中转或者再自己打洞。
/// </summary>
public sealed class DirectBridge : IAsyncDisposable
{
    private const byte Probe = 1, ProbeAck = 2, Ping = 4, Pong = 5, Data = 0x10, Control = 0x20;
    private const int TagLength = 16;

    private readonly Socket _socket;
    private readonly byte[] _key;
    private readonly bool _isHost;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, DirectPeer> _peers = new();
    private readonly List<Task> _loops = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Socket? _guestLocal;
    private int _probeRound;

    /// <summary>房主：本机 EasyTier 的 UDP 监听端口；加入方：给本机 EasyTier 连的端口。</summary>
    public int LocalPort { get; private set; }
    public IPEndPoint SocketEndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public IReadOnlyCollection<DirectPeer> Peers => _peers.Values.ToList();
    public HashSet<PathKind> AllowedPaths { get; } = [.. Enum.GetValues<PathKind>()];

    /// <summary>喷洒时每轮往多少个端口发（对方对称 NAT 时用）。</summary>
    public int SprayPorts { get; set; } = 256;

    public event Action<DirectPeer>? Connected;
    public event Action<DirectPeer, JsonObject>? ControlReceived;

    public static Socket CreateSocket(int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        // Windows 上对方端口不可达时 UDP 会报 10054，关掉这个行为，免得打洞时接收循环被打断。
        if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        return socket;
    }

    /// <param name="easyTierPort">房主：本机 EasyTier 监听的 UDP 端口。加入方传 null，桥会开一个本机端口给 EasyTier 连。</param>
    public DirectBridge(Socket socket, RoomCode room, bool isHost, int? easyTierPort, Action<string> log)
    {
        _socket = socket;
        _isHost = isHost;
        _log = log;
        _key = DeriveKey(room);
        if (isHost)
        {
            LocalPort = easyTierPort ?? throw new ArgumentNullException(nameof(easyTierPort));
        }
        else
        {
            _guestLocal = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows()) _guestLocal.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            _guestLocal.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            LocalPort = ((IPEndPoint)_guestLocal.LocalEndPoint!).Port;
            _loops.Add(GuestLocalLoopAsync());
        }
        _loops.Add(ReceiveLoopAsync());
        _loops.Add(PingLoopAsync());
    }

    internal static byte[] DeriveKey(RoomCode room) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(room.Secret), 32, Encoding.UTF8.GetBytes(room.NetworkName), "amber-direct-v1"u8.ToArray());

    private byte[] Tag(ReadOnlySpan<byte> data) => HMACSHA256.HashData(_key, data)[..TagLength];

    /// <summary>认证过的包：类型 + 载荷 + 16 字节 HMAC。</summary>
    internal byte[] Seal(byte type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[1 + payload.Length + TagLength];
        packet[0] = type;
        payload.CopyTo(packet.AsSpan(1));
        Tag(packet.AsSpan(0, 1 + payload.Length)).CopyTo(packet.AsSpan(1 + payload.Length));
        return packet;
    }

    internal bool Verify(ReadOnlySpan<byte> packet) =>
        packet.Length > TagLength && CryptographicOperations.FixedTimeEquals(Tag(packet[..^TagLength]), packet[^TagLength..]);

    /// <summary>往对方的所有允许的地址同时发探测包，持续到连上或超时。</summary>
    public async Task<DirectPeer?> PunchAsync(IReadOnlyList<Candidate> remote, NatKind remoteNat, TimeSpan timeout, CancellationToken ct = default)
    {
        var round = Interlocked.Increment(ref _probeRound);
        var targets = remote.Where(c => AllowedPaths.Contains(c.Kind)).ToList();
        var spray = AllowedPaths.Contains(PathKind.PunchSpray) && remoteNat == NatKind.Symmetric
                    && remote.FirstOrDefault(c => c.Kind == PathKind.Punch) is { } punch ? punch.EndPoint : null;
        var startMessage = L.P("core.mp.punch.start", targets.Count, targets.Count);
        _log(spray is null ? startMessage : L.F("core.mp.punch.start_spray", startMessage,
            L.P("core.mp.punch.spray", SprayPorts, spray.Address, SprayPorts)));
        foreach (var t in targets) _log(L.T("core.mp.punch.try") + t);
        var known = _peers.Count;
        var start = _clock.ElapsedMilliseconds;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        limit.CancelAfter(timeout);
        var sent = 0;
        var random = new Random();
        try
        {
            for (var tick = 0; !limit.IsCancellationRequested; tick++)
            {
                var probe = Seal(Probe, BuildProbePayload(round));
                foreach (var t in targets)
                {
                    await SendRawAsync(probe, t.EndPoint, limit.Token);
                    sent++;
                }
                if (spray is not null && tick % 2 == 0)
                {
                    // 先往映射端口附近（端口预测），再往随机端口（生日悖论）。
                    for (var i = 0; i < SprayPorts; i++)
                    {
                        var port = i < 32 ? spray.Port + i - 16 : random.Next(1024, 65536);
                        if (port is < 1 or > 65535) continue;
                        await SendRawAsync(probe, new IPEndPoint(spray.Address, port), limit.Token);
                        sent++;
                    }
                }
                var newest = _peers.Values.Where(p => p.ConnectedMs >= start).OrderBy(p => p.ConnectedMs).FirstOrDefault();
                if (newest is not null && (!_isHost || _peers.Count > known))
                {
                    _log(L.P("core.mp.punch.success", sent, DirectText.Path(newest.Path), newest.Remote, newest.ConnectedMs - start, sent));
                    return newest;
                }
                await Task.Delay(tick < 20 ? 100 : 300, limit.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 超时。
        }
        _log(L.P("core.mp.punch.timeout", (long)Math.Round(timeout.TotalSeconds), timeout.TotalSeconds, sent));
        return null;
    }

    /// <summary>房主没有对方地址时也能被连：只要有人发来认证通过的探测包就接受。</summary>
    public Task<DirectPeer?> WaitForPeerAsync(TimeSpan timeout, CancellationToken ct = default) => PunchAsync([], NatKind.Unknown, timeout, ct);

    private byte[] BuildProbePayload(int round)
    {
        var payload = new byte[13];
        payload[0] = _isHost ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), round);
        RandomNumberGenerator.Fill(payload.AsSpan(5));
        return payload;
    }

    private async Task SendRawAsync(byte[] packet, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, Stun.MapTo(_socket, target), ct);
        }
        catch (SocketException)
        {
            // 某个地址不可达（比如没有 IPv6 路由），换下一个。
        }
    }

    private PathKind Classify(IPEndPoint from, IReadOnlyList<Candidate>? hints = null)
    {
        if (from.AddressFamily == AddressFamily.InterNetworkV6) return PathKind.IPv6;
        if (!PortMapping.IsPublic(from.Address)) return PathKind.Lan;
        return _lastRemote?.FirstOrDefault(c => c.EndPoint.Equals(from))?.Kind ?? PathKind.Punch;
    }

    private IReadOnlyList<Candidate>? _lastRemote;

    /// <summary>记下对方的地址，用来判断连上的是哪条路径。</summary>
    public void SetRemoteCandidates(IReadOnlyList<Candidate> candidates) => _lastRemote = candidates;

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), _cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var from = Stun.Normalize((IPEndPoint)r.RemoteEndPoint);
            var packet = buffer.AsSpan(0, r.ReceivedBytes);
            if (packet.Length == 0) continue;
            var type = packet[0];

            // 对方发来的内容不可信：处理一个包时出任何错（坏 JSON、字段类型不对、事件处理抛异常、转发出错）都只丢掉这个包、记一笔，
            // 不能让共用的接收循环停掉。数据包也在里面。
            try
            {
                if (type == Data)
                {
                    if (_peers.TryGetValue(from, out var peer)) await ForwardToLocalAsync(peer, packet[1..].ToArray());
                    continue;
                }
                if (!Verify(packet)) continue;
                await HandleAuthenticatedAsync(from, type, packet[1..^TagLength].ToArray());
            }
            catch (Exception e) when (e is not OperationCanceledException || !_cts.IsCancellationRequested)
            {
                if (e is ObjectDisposedException && _cts.IsCancellationRequested) return;
                _log(L.F("core.mp.punch.controlerror", e.GetType().Name));
            }
        }
    }

    private async Task HandleAuthenticatedAsync(IPEndPoint from, byte type, byte[] payload)
    {
        switch (type)
        {
            case Probe:
                // 对方是同一侧（房主收到房主的探测）就不理，防止自己连自己。
                if (payload.Length >= 1 && payload[0] == (_isHost ? 1 : 0)) break;
                await SendRawAsync(Seal(ProbeAck, payload), from, _cts.Token);
                Accept(from);
                break;
            case ProbeAck:
                Accept(from);
                break;
            case Ping when _peers.TryGetValue(from, out var p1):
                p1.LastSeen = DateTime.UtcNow;
                await SendRawAsync(Seal(Pong, payload), from, _cts.Token);
                break;
            case Pong when _peers.TryGetValue(from, out var p2) && payload.Length >= 4:
                p2.LastSeen = DateTime.UtcNow;
                var seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
                if (p2.PendingPings.TryRemove(seq, out var sentAt))
                {
                    Interlocked.Increment(ref p2.PongsReceived);
                    var rtt = _clock.Elapsed.TotalMilliseconds - sentAt / 1000.0;
                    p2.RttMs = p2.RttMs is { } old ? old * 0.7 + rtt * 0.3 : rtt;
                }
                break;
            case Control when _peers.TryGetValue(from, out var p3):
                if (JsonNode.Parse(payload) is JsonObject json) ControlReceived?.Invoke(p3, json);
                break;
        }
    }

    /// <summary>房主最多同时接这么多个加入方（每个一个本机套接字和一个转发循环）。</summary>
    public const int MaxHostPeers = 32;

    private void Accept(IPEndPoint from)
    {
        if (_peers.TryGetValue(from, out var known))
        {
            // 已经连着的对方回了探测（心跳断了之后补发的探测）：算它还活着。
            known.LastSeen = DateTime.UtcNow;
            return;
        }
        if (!_isHost && !_peers.IsEmpty) return;
        if (_isHost && _peers.Count >= MaxHostPeers)
        {
            _log(L.F("core.mp.punch.toomanypeers", from, MaxHostPeers));
            return;
        }
        var peer = new DirectPeer { Remote = from, Path = Classify(from), ConnectedMs = _clock.ElapsedMilliseconds };
        if (!AllowedPaths.Contains(peer.Path) && !(peer.Path == PathKind.Punch && AllowedPaths.Contains(PathKind.PunchSpray)))
        {
            _log(L.F("core.mp.punch.disabled", from, DirectText.Path(peer.Path)));
            return;
        }
        if (_isHost)
        {
            // 每个加入方一个本机套接字，去连本机 EasyTier 的监听端口。先建好再登记：建不了就不接这个加入方，
            // 不会留下一个没有本机套接字的对方（之后它的数据包会让转发出错）。
            Socket? local = null;
            try
            {
                local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) local.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                local?.Dispose();
                _log(L.F("core.mp.punch.localerror", e.GetType().Name));
                return;
            }
            peer.Local = local;
            if (!_peers.TryAdd(from, peer))
            {
                local.Dispose();
                return;
            }
            _loops.Add(HostLocalLoopAsync(peer));
        }
        else if (!_peers.TryAdd(from, peer))
        {
            return;
        }
        _log(L.F("core.mp.punch.connected", from, DirectText.Path(peer.Path)));
        Connected?.Invoke(peer);
    }

    private async Task ForwardToLocalAsync(DirectPeer peer, byte[] data)
    {
        peer.LastSeen = DateTime.UtcNow;
        Interlocked.Add(ref peer.BytesIn, data.Length);
        try
        {
            if (_isHost) { if (peer.Local is { } local) await local.SendToAsync(data, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, LocalPort)); }
            else if (peer.LocalClient is { } client) await _guestLocal!.SendToAsync(data, SocketFlags.None, client);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    private async Task SendDataAsync(DirectPeer peer, ReadOnlyMemory<byte> data)
    {
        var packet = new byte[data.Length + 1];
        packet[0] = Data;
        data.Span.CopyTo(packet.AsSpan(1));
        Interlocked.Add(ref peer.BytesOut, data.Length);
        await SendRawAsync(packet, peer.Remote, _cts.Token);
    }

    private async Task HostLocalLoopAsync(DirectPeer peer)
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await peer.Local!.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                await SendDataAsync(peer, buffer.AsMemory(0, r.ReceivedBytes));
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) { }
        }
    }

    private async Task GuestLocalLoopAsync()
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _guestLocal!.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                var peer = _peers.Values.FirstOrDefault();
                if (peer is null) continue;
                peer.LocalClient = (IPEndPoint)r.RemoteEndPoint;
                await SendDataAsync(peer, buffer.AsMemory(0, r.ReceivedBytes));
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) { }
        }
    }

    /// <summary>对方这么久没回任何东西（心跳停了）就同时补发探测包。</summary>
    internal static readonly TimeSpan ReprobeAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 每秒一次心跳：测延迟和丢包，也保持路由器上的映射不过期。对方的回应停了（对方换了版本、路由器映射被回收、
    /// 对方重启了桥）时，每次心跳再补一个探测包：探测包任何版本都会回，回了就重新算连着。
    /// </summary>
    private async Task PingLoopAsync()
    {
        uint seq = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            foreach (var peer in _peers.Values)
            {
                seq++;
                // 超过 5 秒没回的算丢包。
                foreach (var (s, at) in peer.PendingPings)
                    if (_clock.Elapsed.TotalMilliseconds - at / 1000.0 > 5000) peer.PendingPings.TryRemove(s, out _);
                peer.PendingPings[seq] = (long)(_clock.Elapsed.TotalMilliseconds * 1000);
                Interlocked.Increment(ref peer.PingsSent);
                var payload = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(payload, seq);
                await SendRawAsync(Seal(Ping, payload), peer.Remote, _cts.Token);
                if (DateTime.UtcNow - peer.LastSeen > ReprobeAfter)
                    await SendRawAsync(Seal(Probe, BuildProbePayload(_probeRound)), peer.Remote, _cts.Token);
            }
        }
    }

    /// <summary>经直连通道发控制消息（测试矩阵双方同步用）。</summary>
    public async Task SendControlAsync(DirectPeer peer, JsonObject message) =>
        await SendRawAsync(Seal(Control, Encoding.UTF8.GetBytes(message.ToJsonString())), peer.Remote, _cts.Token);

    /// <summary>断开所有对方（测试矩阵换路径时用），套接字保留。</summary>
    public void DropPeers()
    {
        foreach (var peer in _peers.Values) peer.Local?.Dispose();
        _peers.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socket.Dispose();
        _guestLocal?.Dispose();
        foreach (var peer in _peers.Values) peer.Local?.Dispose();
        try
        {
            await Task.WhenAll(_loops.ToArray());
        }
        catch (Exception e)
        {
            // 哪个循环出过错都不影响收尾：调用方接着要撤掉端口映射、重置界面。
            if (e is not (OperationCanceledException or ObjectDisposedException or SocketException)) _log(L.F("core.mp.punch.localerror", e.GetType().Name));
        }
        _cts.Dispose();
    }
}
