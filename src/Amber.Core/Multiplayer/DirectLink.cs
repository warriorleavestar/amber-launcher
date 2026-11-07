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
/// 直连邀请码 / 回复码：JAY2D-… / JAY2R-…（base64url 编码的 JSON）。
/// 邀请码里是本次会话 id、NAT 类型、能连到房主的地址，和一张普通的 JAY2 邀请（大厅密钥 L + 一次性令牌 K）：
/// 拿着它只能经直连桥进大厅敲门，房主批准后才在加密通道里拿到房间的凭据。里面没有房间的网络名、密钥和成员钥匙。
/// 回复码只有会话 id、NAT 类型和地址，没有任何密钥。两种码都含公网 IP，只发给要一起玩的人。
/// </summary>
public sealed record DirectCode(bool IsReply, string Session, NatKind Nat, IReadOnlyList<Candidate> Candidates, JayInvite? Invite = null)
{
    public const int Version = 1;
    public const string InvitePrefix = "JAY2D-";
    public const string ReplyPrefix = "JAY2R-";
    private const string OldInvitePrefix = "AMBDIRECT";
    private const string OldReplyPrefix = "AMBREPLY";

    /// <summary>对方地址里有 IPv6、映射端口或者对方有公网地址：加入方直接连得上，不用再发回复码。</summary>
    public bool DirectlyReachable => Nat == NatKind.Open || Candidates.Any(c => c.Kind is PathKind.IPv6 or PathKind.Mapped);

    public string Encode()
    {
        if (!IsReply && Invite is null) throw new InvalidOperationException("direct invite needs a JAY2 invite");
        if (IsReply && Invite is not null) throw new InvalidOperationException("reply codes carry no invite");
        var json = Addresses();
        json["v"] = Version;
        if (Invite is { } invite) json["i"] = invite.ToString();
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (IsReply ? ReplyPrefix : InvitePrefix) + body;
    }

    /// <summary>打码后的样子（界面默认显示这个）：只露出前缀和最后 4 位。</summary>
    public string Masked
    {
        get
        {
            var text = Encode();
            return text[..InvitePrefix.Length] + new string('•', 8) + text[^4..];
        }
    }

    public override string ToString() => IsReply ? "DirectCode(reply)" : "DirectCode(invite)";

    /// <summary>
    /// 只有会话 id、NAT 类型和地址：回复码和经直连通道发的 hello 用它。
    /// 直连通道的控制消息只有 HMAC 认证、没有加密，路上的人看得到内容，所以绝不能带任何密钥。
    /// </summary>
    public JsonObject Addresses() => new()
    {
        ["s"] = Session,
        ["n"] = Nat.ToString(),
        ["c"] = new JsonArray(Candidates.Select(c => (JsonNode?)new JsonArray(c.Kind.ToString(), c.EndPoint.ToString())).ToArray()),
    };

    /// <summary>解析 <see cref="Addresses"/> 的内容（对方的 hello、码里的地址）。会话 id 长度不对时返回 false。</summary>
    public static bool TryParseAddresses(JsonObject? json, out string session, out NatKind nat, out List<Candidate> candidates)
    {
        session = "";
        nat = NatKind.Unknown;
        candidates = [];
        if (json is null) return false;
        try
        {
            session = (json["s"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "";
            if (session.Length is < 8 or > 64 || !session.All(char.IsAsciiLetterOrDigit)) return false;
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

    /// <summary>旧版本的直连码（AMBDIRECT1- / AMBREPLY1-，里面是完整的房间码）：不再接受，提示对方更新。</summary>
    public static bool LooksLikeOldCode(string? text) =>
        text is not null && (text.Contains(OldInvitePrefix, StringComparison.OrdinalIgnoreCase) || text.Contains(OldReplyPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>解析粘贴的文字（容忍前后多余的文字）。认不出、旧版本、内容坏了时 error 说明原因。</summary>
    public static bool TryParse(string? text, out DirectCode code, out string? error)
    {
        code = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        var start = upper.IndexOf(InvitePrefix, StringComparison.Ordinal);
        var isReply = false;
        var reply = upper.IndexOf(ReplyPrefix, StringComparison.Ordinal);
        if (reply >= 0 && (start < 0 || reply < start))
        {
            start = reply;
            isReply = true;
        }
        if (start < 0)
        {
            error = L.T(LooksLikeOldCode(text) ? "core.mp.code.old_direct" : "core.mp.code.unrecognized");
            return false;
        }
        var body = new string(text[(start + InvitePrefix.Length)..].TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        try
        {
            var b64 = body.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            if (JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))) is not JsonObject json) throw new FormatException();
            var version = (json["v"] as JsonValue)?.TryGetValue<int>(out var v) == true ? v : 0;
            if (version != Version)
            {
                error = L.F("core.mp.code.version", version, Version);
                return false;
            }
            if (!TryParseAddresses(json, out var session, out var nat, out var candidates)) throw new FormatException();
            JayInvite? invite = null;
            if (json["i"] is JsonValue iv && iv.TryGetValue<string>(out var inviteText))
            {
                if (isReply || !JayInvite.TryParse(inviteText, out var parsed)) throw new FormatException();
                invite = parsed;
            }
            if (!isReply && invite is null) throw new FormatException();
            // 只认这几个字段：码里多出来的东西（比如塞进去的密钥）不当成合法的码。
            if (json.Any(p => p.Key is not ("v" or "s" or "n" or "c" or "i"))) throw new FormatException();
            code = new DirectCode(isReply, session, nat, candidates, invite);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            error = L.T("core.mp.code.corrupted");
            return false;
        }
    }
}

/// <summary>
/// 直连用的两种钥匙，都用 HKDF-SHA256（salt = "jay-direct-v1"）派生，和房间密钥无关：
/// 桥的探测 / 控制包 HMAC 钥匙来自邀请里的大厅密钥 L；批准后的“这条桥是我的”证明来自这个成员自己的 psk。
/// </summary>
public static class DirectKeys
{
    private static readonly byte[] Salt = "jay-direct-v1"u8.ToArray();

    private static byte[] Derive(ReadOnlySpan<byte> key, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, Salt, Encoding.ASCII.GetBytes(info));
        return output;
    }

    /// <summary>直连桥探测、心跳、控制包的 HMAC 钥匙。</summary>
    public static byte[] Bridge(ReadOnlySpan<byte> lobbyKey) => Derive(lobbyKey, "bridge");

    /// <summary>
    /// 批准后的证明：加入方用自己成员钥匙的 psk 对房主这条桥上发来的随机挑战做 HMAC（带上会话 id 和成员编号），
    /// 房主据此把这条桥接到房间的 EasyTier。挑战每条桥一个，路上的人抄下来也用不到别的桥上。
    /// </summary>
    public static byte[] AdmitProof(MemberKey member, string session, ReadOnlySpan<byte> challenge)
    {
        var psk = member.CopyPsk();
        var key = Derive(psk, "admit");
        try
        {
            var data = new List<byte>();
            data.AddRange("jay-direct-admit-v1|"u8.ToArray());
            data.AddRange(Encoding.ASCII.GetBytes(session + "|" + member.IdHex + "|"));
            data.AddRange(challenge.ToArray());
            return HMACSHA256.HashData(key, data.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
            CryptographicOperations.ZeroMemory(key);
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
    public long ConnectedMs { get; internal set; }
    internal Socket? Local { get; set; }
    internal IPEndPoint? LocalClient { get; set; }
    internal readonly ConcurrentDictionary<uint, long> PendingPings = new();
    /// <summary>房主：这条桥的数据转发到本机哪个 EasyTier（批准前是大厅，批准后是房间）。</summary>
    internal int TargetPort;
    /// <summary>房主：已经证明了自己是批准过的成员，数据转发到房间的 EasyTier。</summary>
    public bool Admitted { get; internal set; }
    /// <summary>房主：这条桥上是哪个成员（批准后才有）。踢出时断开这条桥。</summary>
    public MemberEntry? Member { get; internal set; }
    /// <summary>房主：发给这条桥的随机挑战（批准后的证明要对它做 HMAC）。</summary>
    internal byte[]? Challenge;
    internal CancellationTokenRegistration Revocation;
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
/// EasyTier 只在本机回环上收发，数据本身仍是 EasyTier 加密过的；打洞探测包用邀请里大厅密钥派生的 HMAC 认证（见 <see cref="DirectKeys"/>），
/// 不认识的地址发来的数据一律丢掉。房主可以同时连多个加入方，加入方之间由 EasyTier 经房主中转或者再自己打洞。
/// 房主开了大厅端口时（邀请房间），新连上的桥只转发到大厅的 EasyTier（只能敲门）；加入方证明自己是批准过的成员后才改接房间的 EasyTier。
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
    // 测试重新验证同一条路径时保留 EasyTier 绑定的本机端点；暂停期间不接受数据转发。
    private readonly Dictionary<IPEndPoint, DirectPeer> _pausedPeers = new();
    private readonly object _peerGate = new();
    private readonly ConcurrentBag<Task> _loops = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DirectPeer>> _punches = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>房主：踢出的人的地址，之后它的探测包不再接。</summary>
    private readonly HashSet<IPEndPoint> _blocked = [];
    /// <summary>房主：证明过成员身份的地址（桥断了从同一地址回来时不算新人）。</summary>
    private readonly ConcurrentDictionary<IPEndPoint, MemberEntry> _admittedRemotes = new();
    private Socket? _guestLocal;
    private int _probeRound;

    /// <summary>房主：本机房间 EasyTier 的 UDP 监听端口；加入方：给本机 EasyTier 连的端口。</summary>
    public int LocalPort { get; private set; }

    /// <summary>房主：本机大厅 EasyTier 的 UDP 监听端口。设了它，新连上的桥先只能到大厅，批准后才接到房间。</summary>
    public int? LobbyPort { get; }
    public IPEndPoint SocketEndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public IReadOnlyCollection<DirectPeer> Peers => _peers.Values.ToList();
    public HashSet<PathKind> AllowedPaths { get; } = [.. Enum.GetValues<PathKind>()];

    /// <summary>
    /// 房主：现在收不收新的加入方。直连房间的大厅密钥 L（也就是桥钥匙）整个房间都不换，
    /// 所以没有待用邀请时不再接新的桥——拿着旧码的人连大厅都到不了；已经连着的、证明过身份的照常。
    /// </summary>
    public Func<bool>? AcceptingNewPeers { get; set; }

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

    /// <param name="key">探测和控制包的 HMAC 钥匙（<see cref="DirectKeys.Bridge"/>）。</param>
    /// <param name="easyTierPort">房主：本机房间 EasyTier 监听的 UDP 端口。加入方传 null，桥会开一个本机端口给 EasyTier 连。</param>
    /// <param name="lobbyPort">房主：本机大厅 EasyTier 监听的 UDP 端口（邀请房间）。批准前的桥只转发到这里。</param>
    public DirectBridge(Socket socket, byte[] key, bool isHost, int? easyTierPort, Action<string> log, int? lobbyPort = null)
    {
        if (key.Length != 32) throw new ArgumentException(null, nameof(key));
        _socket = socket;
        _isHost = isHost;
        _log = log;
        _key = (byte[])key.Clone();
        LobbyPort = isHost ? lobbyPort : null;
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
        var acknowledged = new TaskCompletionSource<DirectPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        _punches[round] = acknowledged;
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
                var confirmed = acknowledged.Task.IsCompletedSuccessfully ? acknowledged.Task.Result : null;
                if (confirmed is not null && (!_peers.TryGetValue(confirmed.Remote, out var active) || !ReferenceEquals(active, confirmed)))
                    confirmed = null;
                var newest = confirmed ?? _peers.Values.Where(p => p.ConnectedMs >= start).OrderBy(p => p.ConnectedMs).FirstOrDefault();
                if (newest is not null && (confirmed is not null || !_isHost || _peers.Count > known))
                {
                    _log(L.P("core.mp.punch.success", sent, DirectText.Path(newest.Path), newest.Remote, _clock.ElapsedMilliseconds - start, sent));
                    return newest;
                }
                await Task.Delay(tick < 20 ? 100 : 300, limit.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 超时。
        }
        finally
        {
            _punches.TryRemove(round, out _);
        }
        _log(L.P("core.mp.punch.timeout", (long)Math.Round(timeout.TotalSeconds), timeout.TotalSeconds, sent));
        return null;
    }

    /// <summary>NAT 保活的间隔。很多运营商（尤其是国内的 CGNAT）几十秒没有包就回收 UDP 映射。</summary>
    internal TimeSpan NatKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    private byte[]? _keepAliveId;
    private IPEndPoint? _keepAliveExpected;
    private int _keepAliveStarted;

    /// <summary>
    /// 打洞套接字的 NAT 保活：每隔 <see cref="NatKeepAliveInterval"/> 往 STUN 服务器发一个 Binding 请求。
    /// 码里写的外部地址（STUN 查到的端口）只有在映射一直活着时才有效：房主发出邀请码后要等对方贴回复码，
    /// 这段时间套接字上没有任何包，路由器/运营商把映射回收后，再发包会换一个新的外部端口，
    /// 对方往码里的旧端口发的探测全被丢掉，端口受限锥形 NAT 的对方又不收新端口发来的包，两边都打不通。
    /// <paramref name="expected"/> 不为空时（锥形 NAT），回复里的外部地址变了就记一笔，方便排查。
    /// </summary>
    public void StartNatKeepAlive(IPEndPoint stunServer, IPEndPoint? expected)
    {
        if (Interlocked.Exchange(ref _keepAliveStarted, 1) != 0) return;
        _keepAliveExpected = expected is null ? null : Stun.Normalize(expected);
        _loops.Add(KeepAliveLoopAsync(stunServer));
    }

    private async Task KeepAliveLoopAsync(IPEndPoint stunServer)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NatKeepAliveInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var id = RandomNumberGenerator.GetBytes(12);
            Volatile.Write(ref _keepAliveId, id);
            await SendRawAsync(Stun.BuildRequest(id), stunServer, _cts.Token);
        }
    }

    /// <summary>接收循环收到的包是不是保活请求的 STUN 回复（是的话吃掉，不再往下处理）。</summary>
    private bool HandleKeepAliveResponse(ReadOnlySpan<byte> packet)
    {
        if (Volatile.Read(ref _keepAliveId) is not { } id || Stun.ParseResponse(packet, id) is not { } mapped) return false;
        mapped = Stun.Normalize(mapped);
        if (_keepAliveExpected is { } expected && !expected.Equals(mapped))
        {
            _log(L.F("core.mp.punch.mapping_changed", expected.Port, mapped.Port));
            _keepAliveExpected = mapped;
        }
        return true;
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
            if (HandleKeepAliveResponse(packet)) continue;

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
                // 先登记再回确认：不接（人满、路径没开）就不回，对方不会以为连上了。
                if (!Accept(from)) break;
                await SendRawAsync(Seal(ProbeAck, payload), from, _cts.Token);
                break;
            case ProbeAck:
                if (Accept(from) && payload.Length >= 5 && payload[0] == (_isHost ? 1 : 0)
                    && _punches.TryGetValue(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)), out var round)
                    && _peers.TryGetValue(from, out var acknowledged)) round.TrySetResult(acknowledged);
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
                p3.LastSeen = DateTime.UtcNow;
                if (JsonNode.Parse(payload) is JsonObject json) ControlReceived?.Invoke(p3, json);
                break;
        }
    }

    /// <summary>房主最多同时接这么多个加入方（每个一个本机套接字和一个转发循环）。</summary>
    public const int MaxHostPeers = 32;

    /// <summary>房主：加入方这么久没有任何回应就移除（腾出位子，也关掉它的本机套接字和转发循环）。</summary>
    internal TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>登记一个对方。已经登记过返回 true；不接（加入方已经有房主、人满、路径没开、建不了套接字）返回 false。</summary>
    private bool Accept(IPEndPoint from)
    {
        DirectPeer? connected;
        bool accepted;
        lock (_peerGate) accepted = AcceptLocked(from, out connected);
        if (connected is not null)
        {
            _log(L.F("core.mp.punch.connected", from, DirectText.Path(connected.Path)));
            Connected?.Invoke(connected);
        }
        return accepted;
    }

    private bool AcceptLocked(IPEndPoint from, out DirectPeer? connected)
    {
        connected = null;
        if (_cts.IsCancellationRequested) return false;
        if (_peers.TryGetValue(from, out var known))
        {
            // 已经连着的对方回了探测（心跳断了之后补发的探测）：算它还活着。
            known.LastSeen = DateTime.UtcNow;
            return true;
        }
        if (!_isHost && !_peers.IsEmpty) return false;
        if (_blocked.Contains(from)) return false;
        var path = Classify(from);
        if (!AllowedPaths.Contains(path) && !(path == PathKind.Punch && AllowedPaths.Contains(PathKind.PunchSpray)))
        {
            _log(L.F("core.mp.punch.disabled", from, DirectText.Path(path)));
            return false;
        }
        EvictStale();
        if (_pausedPeers.Remove(from, out var resumed))
        {
            resumed.ConnectedMs = _clock.ElapsedMilliseconds;
            resumed.LastSeen = DateTime.UtcNow;
            _peers[from] = resumed;
            connected = resumed;
            return true;
        }
        if (_isHost && AcceptingNewPeers?.Invoke() == false
            && !(_admittedRemotes.TryGetValue(from, out var member) && !member.Revoked))
        {
            _log(L.F("core.mp.direct.closed_to_new", from));
            return false;
        }
        if (_isHost && _peers.Count + _pausedPeers.Count >= MaxHostPeers)
        {
            _log(L.F("core.mp.punch.toomanypeers", from, MaxHostPeers));
            return false;
        }
        if (!_isHost)
        {
            // 加入方只保留一个房主；真正换地址后不再保留旧路径状态。
            _pausedPeers.Clear();
        }
        var peer = new DirectPeer { Remote = from, Path = path, ConnectedMs = _clock.ElapsedMilliseconds, TargetPort = LobbyPort ?? LocalPort, Admitted = LobbyPort is null };
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
                return false;
            }
            peer.Local = local;
            if (!_peers.TryAdd(from, peer))
            {
                local.Dispose();
                return true;
            }
            _loops.Add(HostLocalLoopAsync(peer, local));
        }
        else if (!_peers.TryAdd(from, peer))
        {
            return true;
        }
        connected = peer;
        return true;
    }

    /// <summary>房主：移除超过 PeerTimeout 没有任何回应的加入方（离开了、换了地址、掉线了）。</summary>
    private void EvictStale()
    {
        lock (_peerGate)
        {
            var now = DateTime.UtcNow;
            foreach (var (endpoint, peer) in _pausedPeers.ToArray())
            {
                if (now - peer.LastSeen <= PeerTimeout) continue;
                _pausedPeers.Remove(endpoint);
                peer.Local?.Dispose();
            }
            if (!_isHost) return;
            foreach (var (endpoint, peer) in _peers)
            {
                if (now - peer.LastSeen <= PeerTimeout || !_peers.TryRemove(endpoint, out var removed)) continue;
                removed.Local?.Dispose();
                _log(L.F("core.mp.punch.peerevicted", endpoint));
            }
        }
    }

    private async Task ForwardToLocalAsync(DirectPeer peer, byte[] data)
    {
        peer.LastSeen = DateTime.UtcNow;
        Interlocked.Add(ref peer.BytesIn, data.Length);
        try
        {
            if (_isHost) { if (peer.Local is { } local) await local.SendToAsync(data, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, peer.TargetPort)); }
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

    private async Task HostLocalLoopAsync(DirectPeer peer, Socket local)
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await local.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                // 批准后换了本机套接字：旧套接字（大厅那边）上收到的不再往外发。
                if (!ReferenceEquals(peer.Local, local)) return;
                if (!_peers.TryGetValue(peer.Remote, out var active) || !ReferenceEquals(active, peer)) continue;
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
            EvictStale();
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

    /// <summary>
    /// 房主：这条桥上的人证明了自己是批准过的成员：换一个新的本机套接字，改接房间的 EasyTier（大厅那边的连接随旧套接字断掉）。
    /// 成员钥匙作废（踢出、重新批准换了钥匙、房间结束）时这条桥马上断开，之后这个地址的探测不再接。
    /// </summary>
    public bool Admit(DirectPeer peer, MemberEntry member)
    {
        if (!_isHost) throw new InvalidOperationException();
        Socket? old;
        lock (_peerGate)
        {
            if (!_peers.TryGetValue(peer.Remote, out var active) || !ReferenceEquals(active, peer) || member.Revoked) return false;
            if (peer.Admitted && ReferenceEquals(peer.Member, member)) return true;
            Socket local;
            try
            {
                local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) local.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            catch (SocketException e)
            {
                _log(L.F("core.mp.punch.localerror", e.GetType().Name));
                return false;
            }
            old = peer.Local;
            peer.Revocation.Dispose();
            peer.Local = local;
            peer.TargetPort = LocalPort;
            peer.Member = member;
            peer.Admitted = true;
            _admittedRemotes[peer.Remote] = member;
            _loops.Add(HostLocalLoopAsync(peer, local));
        }
        old?.Dispose();
        // 已经作废的，Register 会立刻调用。
        peer.Revocation = member.Revocation.Register(() => Kick(peer));
        _log(L.F("core.mp.direct.admitted", peer.Remote));
        return true;
    }

    /// <summary>房主：断开这条桥（踢出的人），之后这个地址发来的探测不再接。</summary>
    public void Kick(DirectPeer peer)
    {
        lock (_peerGate)
        {
            _blocked.Add(peer.Remote);
            _admittedRemotes.TryRemove(peer.Remote, out _);
            if (_peers.TryGetValue(peer.Remote, out var active) && ReferenceEquals(active, peer)) _peers.TryRemove(peer.Remote, out _);
            if (_pausedPeers.TryGetValue(peer.Remote, out var paused) && ReferenceEquals(paused, peer)) _pausedPeers.Remove(peer.Remote);
            peer.Local?.Dispose();
        }
        _log(L.F("core.mp.direct.kicked", peer.Remote));
    }

    /// <summary>房主：这条桥的随机挑战（批准后的证明对它做 HMAC）。每条桥一个，第一次要时生成。</summary>
    internal byte[] ChallengeFor(DirectPeer peer)
    {
        lock (_peerGate) return peer.Challenge ??= RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>经直连通道发控制消息（测试矩阵双方同步用）。</summary>
    public async Task SendControlAsync(DirectPeer peer, JsonObject message) =>
        await SendRawAsync(Seal(Control, Encoding.UTF8.GetBytes(message.ToJsonString())), peer.Remote, _cts.Token);

    /// <summary>重新验证远端路径；矩阵可保留本地传输端点，避免破坏仍在运行的 EasyTier 会话。</summary>
    public void DropPeers(bool preserveLocalTransport = false)
    {
        lock (_peerGate)
        {
            if (!preserveLocalTransport)
            {
                foreach (var peer in _pausedPeers.Values)
                {
                    peer.Local?.Dispose();
                    peer.Revocation.Dispose();
                }
                _pausedPeers.Clear();
            }
            foreach (var endpoint in _peers.Keys)
            {
                if (!_peers.TryRemove(endpoint, out var peer)) continue;
                if (preserveLocalTransport)
                {
                    peer.LastSeen = DateTime.UtcNow;
                    _pausedPeers[endpoint] = peer;
                }
                else
                {
                    peer.Local?.Dispose();
                    peer.Revocation.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socket.Dispose();
        _guestLocal?.Dispose();
        DropPeers();
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
        CryptographicOperations.ZeroMemory(_key);
    }
}
