using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>网关回给客户端的结果（握手完成后的第一条加密记录）。</summary>
public enum GateStatus : byte
{
    Ok = 0,
    /// <summary>这个用户名绑定的设备已经满了，要管理员先撤销一台。</summary>
    DeviceLimit = 2,
    /// <summary>这台设备不在名单里（被撤销了，或者从没申请过）。</summary>
    NotMember = 4,
    /// <summary>用户名被管理员停用了。</summary>
    Disabled = 5,
    /// <summary>服务器开了隐私+：只接受经两个节点的拆分连接（旧版 Jay 会收到这个，要更新）。</summary>
    SplitRequired = 6,
    /// <summary>隐私++ 熔断中：能用的节点不够两个，暂停接受连接，节点恢复后自动可用。</summary>
    CircuitOpen = 7,
}

/// <summary>服务器的隐私等级。</summary>
public enum PrivacyLevel : byte
{
    /// <summary>直连网关。</summary>
    Basic = 0,
    /// <summary>隐私+：经两个不同节点各走一半（2-of-2 拆分），网关只接受节点转来的连接。</summary>
    Plus = 1,
    /// <summary>隐私++：在隐私+ 之上，节点要持续发心跳；健康节点不够就熔断（全部断开、暂停接受连接），节点可以随时吊销。</summary>
    PlusPlus = 2,
}

public static class PrivacyLevels
{
    /// <summary>这个等级要不要经两个节点拆分。</summary>
    public static bool IsSplit(this PrivacyLevel level) => level is PrivacyLevel.Plus or PrivacyLevel.PlusPlus;
}

/// <summary>一个地址（网关或节点）。</summary>
public sealed record GateEndpoint(string Host, int Port)
{
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>解析 host:port 或 [v6]:port。</summary>
    public static GateEndpoint? TryParse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        string host;
        string port;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0) return null;
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon) return null;
            host = text[..colon];
            port = text[(colon + 1)..];
        }
        return GateInvite.IsValidHost(host) && int.TryParse(port, out var p) && p is > 0 and < 65536 ? new GateEndpoint(host, p) : null;
    }
}

/// <summary>网关告诉客户端的连法：隐私等级、直连地址（隐私+ 下不给，免得暴露源站）、节点列表。每次进服都会回最新的。</summary>
public sealed record GateNetwork(PrivacyLevel Level, GateEndpoint? Direct, IReadOnlyList<GateEndpoint> Relays, GateNetworkRotation? Rotation = null)
{
    public const int MaxRelays = 16;

    /// <summary>直连入口的轮换信息（没开轮换返回 null）。</summary>
    public GateEntryRotation? DirectRotation => Rotation is { DirectSeed: { } seed } r ? new GateEntryRotation(r.Rule, seed) : null;

    /// <summary>某个节点的轮换信息（没开轮换返回 null）。</summary>
    public GateEntryRotation? RelayRotation(GateEndpoint relay)
    {
        if (Rotation is not { } r) return null;
        for (var i = 0; i < Relays.Count && i < r.RelaySeeds.Count; i++)
            if (Relays[i] == relay) return new GateEntryRotation(r.Rule, r.RelaySeeds[i]);
        return null;
    }
}

/// <summary>地址簿里的轮换部分：规则、直连入口的种子、每个节点的种子（和节点列表一一对应）。</summary>
public sealed record GateNetworkRotation(GateRotation Rule, byte[]? DirectSeed, IReadOnlyList<byte[]> RelaySeeds);

/// <summary>进服请求的种类（v2 写在握手第三条消息的负载里）。</summary>
public enum JoinKind : byte
{
    Direct = 0,
    /// <summary>两路拆分里的一条腿。</summary>
    SplitLeg = 1,
}

/// <summary>v2 进服请求：拆分腿要带序号（0 / 1）和两条腿共用的配对 ID。</summary>
public sealed record JoinRequest(JoinKind Kind, byte Index, byte[] PairId)
{
    public const int PairIdLength = 16;
    public static readonly JoinRequest Direct = new(JoinKind.Direct, 0, []);
    public static JoinRequest Leg(byte index, byte[] pairId) => new(JoinKind.SplitLeg, index, pairId);
}

/// <summary>入服成功后客户端要保存的东西：网关地址、网关公钥（钉住）、访问密钥、绑定的用户名。</summary>
public sealed record GateMembership(string Host, int Port, byte[] ServerPublicKey, byte[] AccessKey, string Username, GateNetwork? Network = null)
{
    public override string ToString() => $"{Username}@{Host}:{Port}";
}

public sealed class GateRejectedException(GateStatus status) : Exception(Describe(status))
{
    public GateStatus Status { get; } = status;

    public static string Describe(GateStatus status) => status switch
    {
        GateStatus.DeviceLimit => L.T("core.gate.status.devicelimit"),
        GateStatus.NotMember => L.T("core.gate.status.notmember"),
        GateStatus.Disabled => L.T("core.gate.status.disabled"),
        GateStatus.SplitRequired => L.T("core.gate.status.splitrequired"),
        GateStatus.CircuitOpen => L.T("core.gate.status.circuitopen"),
        _ => L.T("core.gate.status.unknown"),
    };
}

/// <summary>
/// Jay 网关协议 v1。全部用现有原语：Noise_XXpsk0_25519_ChaChaPoly_SHA256 + <see cref="SecureStream"/>。
/// <list type="bullet">
/// <item>入服申请（enroll）：psk 从邀请令牌派生。客户端读到网关的长期公钥后先核对邀请里钉住的哈希，对上了才在第三条消息里亮出自己的设备公钥。
/// 网关登记（用户名, 设备公钥），在加密通道里回传访问密钥。</item>
/// <item>进服（join）：psk 是访问密钥。网关核对设备公钥在名单里，再交给 <see cref="McLogin"/> 核对用户名。</item>
/// </list>
/// 两种都是 psk 在第一条消息就生效：不知道令牌或访问密钥的连接第一条就解不开，网关一个字节都不回，扫描器看不出这里有服务。
/// 握手帧：[2 字节长度][Noise 消息]。
/// <para>v2（隐私+）：客户端在第三条消息的负载里写 [2][种类][腿序号][16 字节配对 ID]（入服只写 [2]）；网关看到 v2 才在结果后面附上 <see cref="GateNetwork"/>。
/// 负载为空的是 v1 客户端，网关照旧只回 1 字节结果。</para>
/// </summary>
public static class GateProtocol
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int MaxHandshakeMessage = 1024;
    private const int MaxRecord = 8192;
    internal const byte Version2 = 2;
    internal static readonly byte[] EnrollPrologue = "jay-gate-v1|enroll"u8.ToArray();
    internal static readonly byte[] JoinPrologue = "jay-gate-v1|join"u8.ToArray();
    internal static readonly byte[] RelayPrologue = "jay-gate-v1|relay"u8.ToArray();
    /// <summary>节点心跳记录：[8 字节序号][8 字节 Unix 毫秒]。</summary>
    internal const int HeartbeatLength = 16;

    /// <summary>游戏流量用 MC 专用档：不填充，一小时换一次钥（和房间联机的 MC 专用一致）。</summary>
    internal static TransportProfile Profile => TransportProfile.For(TransportMode.Minecraft);

    /// <summary>网关公钥钉在邀请里的哈希（16 字节）。</summary>
    public static byte[] ServerKeyHash(ReadOnlySpan<byte> serverPublicKey) =>
        SHA256.HashData([.. "jay-gate-server-key-v1"u8, .. serverPublicKey])[..GateInvite.KeyHashLength];

    /// <summary>邀请令牌 → 入服握手的 psk。网关只存这个值，不存令牌本身。</summary>
    public static byte[] EnrollPsk(ReadOnlySpan<byte> token, ReadOnlySpan<byte> serverKeyHash) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, token.ToArray(), 32, serverKeyHash.ToArray(), "jay-gate-enroll-psk-v1"u8.ToArray());

    // ---------------- 客户端 ----------------

    /// <summary>用邀请码入服：成功返回要保存的入服信息；网关拒绝时抛 <see cref="GateRejectedException"/>；邀请无效、用过或过期时网关不回话，抛 IOException。</summary>
    public static async Task<GateMembership> EnrollAsync(Stream stream, GateInvite invite, X25519KeyPair device, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var psk = EnrollPsk(invite.Token, invite.ServerKeyHash);
        byte[] serverKey;
        SecureStream secure;
        try
        {
            using var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: true, EnrollPrologue, psk, device);
            (secure, serverKey) = await InitiateAsync(stream, hs, key => CryptographicOperations.FixedTimeEquals(ServerKeyHash(key), invite.ServerKeyHash), [Version2], timeout.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
        try
        {
            var reply = await ReadRecordAsync(secure, timeout.Token);
            var status = (GateStatus)reply[0];
            if (status != GateStatus.Ok) throw new GateRejectedException(status);
            if (reply.Length < 1 + 32 + 1) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            var accessKey = reply[1..33];
            if (34 + reply[33] > reply.Length) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            var name = Encoding.ASCII.GetString(reply, 34, reply[33]);
            if (!GateInvite.IsValidUsername(name)) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            var network = DecodeNetwork(reply.AsSpan(34 + reply[33]));
            await secure.CloseAsync(timeout.Token);
            return new GateMembership(invite.Host, invite.Port, serverKey, accessKey, name, network);
        }
        finally
        {
            await secure.DisposeAsync();
        }
    }

    /// <summary>进服（直连）：返回已认证的加密流，之后直接当成到 MC 服务端的连接用。网关拒绝时抛 <see cref="GateRejectedException"/>。</summary>
    public static Task<SecureStream> JoinAsync(Stream stream, GateMembership membership, X25519KeyPair device, CancellationToken ct = default) =>
        JoinAsync(stream, membership, device, JoinRequest.Direct, null, ct);

    /// <param name="request">null 表示按 v1 进服（第三条消息负载为空，只收 1 字节结果）：只给兼容测试用。</param>
    /// <param name="onNetwork">网关回了最新的连法（隐私等级、节点列表）时调用，拒绝时（比如要求拆分）也会先调用。</param>
    public static async Task<SecureStream> JoinAsync(Stream stream, GateMembership membership, X25519KeyPair device, JoinRequest? request, Action<GateNetwork>? onNetwork, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        using var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: true, JoinPrologue, membership.AccessKey, device);
        var (secure, _) = await InitiateAsync(stream, hs, key => CryptographicOperations.FixedTimeEquals(key, membership.ServerPublicKey), EncodeJoinRequest(request), timeout.Token);
        try
        {
            var reply = await ReadRecordAsync(secure, timeout.Token);
            if (request is null)
            {
                if (reply.Length != 1) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            }
            else if (reply.Length > 1)
            {
                var network = DecodeNetwork(reply.AsSpan(1)) ?? throw new CryptographicException(L.T("core.gate.protocol.malformed"));
                onNetwork?.Invoke(network);
            }
            if ((GateStatus)reply[0] != GateStatus.Ok) throw new GateRejectedException((GateStatus)reply[0]);
            return secure;
        }
        catch
        {
            secure.Abort();
            await secure.DisposeAsync();
            throw;
        }
    }

    private static async Task<(SecureStream, byte[] ServerKey)> InitiateAsync(Stream stream, NoiseHandshake hs, Func<byte[], bool> trustServer, byte[] payload, CancellationToken ct)
    {
        await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
        byte[] second;
        try
        {
            second = await ReadFrameAsync(stream, ct);
        }
        catch (EndOfStreamException e)
        {
            // 网关对认不出的 psk 一声不吭就断开：邀请用过了、过期了，或者访问密钥不对。
            throw new IOException(L.T("core.gate.protocol.silent"), e);
        }
        hs.ReadMessage(second);
        var serverKey = hs.RemoteStaticPublicKey ?? throw new CryptographicException(L.T("core.gate.protocol.malformed"));
        // 先核对服务器身份，对上了才发第三条消息（里面才有本机的设备公钥）。
        if (!trustServer(serverKey)) throw new CryptographicException(L.T("core.gate.protocol.wrongserver"));
        await WriteFrameAsync(stream, hs.WriteMessage(payload), ct);
        var (send, receive) = hs.Split();
        return (new SecureStream(stream, send, receive, Profile), serverKey);
    }

    // ---------------- 网关 ----------------

    /// <summary>网关这边一次握手的结果。</summary>
    /// <param name="Version">1 = 旧客户端（第三条消息负载为空），2 = 带 v2 负载。</param>
    /// <param name="Request">v2 进服请求；入服和 v1 时为 null。</param>
    /// <param name="RelayId">节点心跳连接：哪个节点（它的地址）。</param>
    public sealed record Accepted(SecureStream Stream, byte[] DevicePublicKey, bool IsEnroll, string? InviteId, int Version = 1, JoinRequest? Request = null, string? RelayId = null);

    /// <summary>
    /// 网关：读第一条消息，按顺序试 join（访问密钥）和每个有效邀请的 psk，哪个解得开就是哪种。都不行返回 null（调用方直接断开，不回任何字节）。
    /// </summary>
    public static async Task<Accepted?> AcceptAsync(Stream stream, X25519KeyPair serverKey, byte[] accessKey, IReadOnlyList<(string Id, byte[] Psk)> invites, CancellationToken ct,
        IReadOnlyList<(string Id, byte[] Psk)>? relays = null)
    {
        byte[] first;
        try
        {
            first = await ReadFrameAsync(stream, ct);
        }
        catch (Exception e) when (e is EndOfStreamException or CryptographicException)
        {
            return null;
        }
        var candidates = new List<(byte[] Prologue, byte[] Psk, string? Id)> { (JoinPrologue, accessKey, null) };
        candidates.AddRange(invites.Select(i => (EnrollPrologue, i.Psk, (string?)i.Id)));
        if (relays is not null) candidates.AddRange(relays.Select(r => (RelayPrologue, r.Psk, (string?)r.Id)));
        foreach (var (prologue, psk, id) in candidates)
        {
            var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: false, prologue, psk, serverKey);
            try
            {
                hs.ReadMessage(first);
            }
            catch (CryptographicException)
            {
                hs.Dispose();
                continue;
            }
            using (hs)
            {
                await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
                byte[] payload;
                try
                {
                    var third = await ReadFrameAsync(stream, ct);
                    payload = hs.ReadMessage(third);
                }
                catch (Exception e) when (e is EndOfStreamException or CryptographicException)
                {
                    return null;
                }
                var device = hs.RemoteStaticPublicKey!;
                if (ReferenceEquals(prologue, RelayPrologue))
                {
                    if (payload.Length != 1 || payload[0] != Version2) return null;
                    var (rs, rr) = hs.Split();
                    return new Accepted(new SecureStream(stream, rs, rr, Profile), device, false, null, 2, null, id);
                }
                var enroll = id is not null;
                if (!TryParsePayload(payload, enroll, out var version, out var request)) return null;
                var (send, receive) = hs.Split();
                return new Accepted(new SecureStream(stream, send, receive, Profile), device, enroll, id, version, request);
            }
        }
        return null;
    }

    /// <summary>网关：回入服结果。<paramref name="network"/> 只发给 v2 客户端（调用方按 <see cref="Accepted.Version"/> 决定传不传）。</summary>
    public static Task SendEnrollResultAsync(SecureStream stream, GateStatus status, byte[]? accessKey, string? username, CancellationToken ct, GateNetwork? network = null)
    {
        if (status != GateStatus.Ok) return WriteRecordAsync(stream, [(byte)status], ct);
        var name = Encoding.ASCII.GetBytes(username!);
        return WriteRecordAsync(stream, [(byte)GateStatus.Ok, .. accessKey!, (byte)name.Length, .. name, .. network is null ? [] : EncodeNetwork(network)], ct);
    }

    public static Task SendJoinResultAsync(SecureStream stream, GateStatus status, CancellationToken ct, GateNetwork? network = null) =>
        WriteRecordAsync(stream, [(byte)status, .. network is null ? [] : EncodeNetwork(network)], ct);

    // ---------------- 节点心跳 ----------------

    /// <summary>节点心跳的 psk：从节点码里的密钥派生，网关只存这个值。</summary>
    public static byte[] RelayPsk(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> serverKeyHash) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), 32, serverKeyHash.ToArray(), "jay-gate-relay-psk-v1"u8.ToArray());

    /// <summary>节点：连上网关的心跳通道。先核对网关公钥（节点码里钉住的哈希），网关认了这个节点才回 Ok。</summary>
    public static async Task<SecureStream> RelayConnectAsync(Stream stream, GateRelayCode code, X25519KeyPair relayKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var psk = RelayPsk(code.Secret, code.ServerKeyHash);
        SecureStream secure;
        try
        {
            using var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: true, RelayPrologue, psk, relayKey);
            (secure, _) = await InitiateAsync(stream, hs, key => CryptographicOperations.FixedTimeEquals(ServerKeyHash(key), code.ServerKeyHash), [Version2], timeout.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
        try
        {
            var reply = await ReadRecordAsync(secure, timeout.Token);
            if (reply.Length != 1) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            if ((GateStatus)reply[0] != GateStatus.Ok) throw new GateRejectedException((GateStatus)reply[0]);
            return secure;
        }
        catch
        {
            secure.Abort();
            await secure.DisposeAsync();
            throw;
        }
    }

    internal static Task WriteHeartbeatAsync(SecureStream stream, ulong counter, CancellationToken ct)
    {
        var record = new byte[HeartbeatLength];
        BinaryPrimitives.WriteUInt64BigEndian(record, counter);
        BinaryPrimitives.WriteInt64BigEndian(record.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return WriteRecordAsync(stream, record, ct);
    }

    internal static (ulong Counter, DateTimeOffset Time) ParseHeartbeat(byte[] record)
    {
        if (record.Length != HeartbeatLength) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
        return (BinaryPrimitives.ReadUInt64BigEndian(record), DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(record.AsSpan(8))));
    }

    // ---------------- v2 负载 ----------------

    internal static byte[] EncodeJoinRequest(JoinRequest? request) => request switch
    {
        null => [],
        { Kind: JoinKind.SplitLeg } => [Version2, (byte)JoinKind.SplitLeg, request.Index, .. request.PairId],
        _ => [Version2, (byte)JoinKind.Direct],
    };

    private static bool TryParsePayload(byte[] payload, bool enroll, out int version, out JoinRequest? request)
    {
        version = 1;
        request = null;
        if (payload.Length == 0) return true;
        if (payload[0] != Version2) return false;
        version = 2;
        if (enroll) return payload.Length == 1;
        if (payload.Length == 2 && payload[1] == (byte)JoinKind.Direct)
        {
            request = JoinRequest.Direct;
            return true;
        }
        if (payload.Length == 3 + JoinRequest.PairIdLength && payload[1] == (byte)JoinKind.SplitLeg && payload[2] <= 1)
        {
            request = JoinRequest.Leg(payload[2], payload[3..]);
            return true;
        }
        return false;
    }

    internal static byte[] EncodeNetwork(GateNetwork network)
    {
        var output = new List<byte> { (byte)network.Level };
        if (network.Direct is { } direct) WriteEndpoint(output, direct);
        else output.Add(0);
        var relays = network.Relays.Take(GateNetwork.MaxRelays).ToList();
        output.Add((byte)relays.Count);
        foreach (var relay in relays) WriteEndpoint(output, relay);
        // 轮换（可选，接在最后）：[1][规则 7 字节][有直连种子 0/1][直连种子][每个节点的种子]。
        if (network.Rotation is { } rotation && rotation.RelaySeeds.Count >= relays.Count)
        {
            output.Add(1);
            rotation.Rule.Write(output);
            output.Add(network.Direct is not null && rotation.DirectSeed is not null ? (byte)1 : (byte)0);
            if (network.Direct is not null && rotation.DirectSeed is not null) output.AddRange(rotation.DirectSeed);
            foreach (var seed in rotation.RelaySeeds.Take(relays.Count)) output.AddRange(seed);
        }
        return [.. output];
    }

    /// <summary>空的返回 null（v1 网关）；格式不对也返回 null。</summary>
    internal static GateNetwork? DecodeNetwork(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        var level = (PrivacyLevel)data[0];
        if (level is not (PrivacyLevel.Basic or PrivacyLevel.Plus or PrivacyLevel.PlusPlus)) return null;
        var offset = 1;
        GateEndpoint? direct = null;
        if (offset >= data.Length) return null;
        if (data[offset] == 0) offset++;
        else if (!TryReadEndpoint(data, ref offset, out direct)) return null;
        if (offset >= data.Length) return null;
        int count = data[offset++];
        if (count > GateNetwork.MaxRelays) return null;
        var relays = new List<GateEndpoint>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadEndpoint(data, ref offset, out var relay)) return null;
            relays.Add(relay!);
        }
        if (offset == data.Length) return new GateNetwork(level, direct, relays);
        if (data[offset++] != 1) return null;
        var rule = GateRotation.Read(data[offset..]);
        if (rule is null) return null;
        offset += GateRotation.EncodedLength;
        if (offset >= data.Length) return null;
        var hasDirect = data[offset++];
        if (hasDirect > 1 || hasDirect == 1 && direct is null) return null;
        var expected = (hasDirect + relays.Count) * GateRotation.SeedLength;
        if (data.Length - offset != expected) return null;
        byte[]? directSeed = null;
        if (hasDirect == 1)
        {
            directSeed = data.Slice(offset, GateRotation.SeedLength).ToArray();
            offset += GateRotation.SeedLength;
        }
        var seeds = new List<byte[]>(relays.Count);
        for (var i = 0; i < relays.Count; i++, offset += GateRotation.SeedLength) seeds.Add(data.Slice(offset, GateRotation.SeedLength).ToArray());
        return new GateNetwork(level, direct, relays, new GateNetworkRotation(rule, directSeed, seeds));
    }

    private static void WriteEndpoint(List<byte> output, GateEndpoint endpoint)
    {
        var host = Encoding.ASCII.GetBytes(endpoint.Host);
        output.Add((byte)host.Length);
        output.AddRange(host);
        output.Add((byte)(endpoint.Port >> 8));
        output.Add((byte)endpoint.Port);
    }

    private static bool TryReadEndpoint(ReadOnlySpan<byte> data, ref int offset, out GateEndpoint? endpoint)
    {
        endpoint = null;
        if (offset >= data.Length) return false;
        int length = data[offset++];
        if (length == 0 || offset + length + 2 > data.Length) return false;
        var host = Encoding.ASCII.GetString(data.Slice(offset, length));
        offset += length;
        var port = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (!GateInvite.IsValidHost(host) || port == 0) return false;
        endpoint = new GateEndpoint(host, port);
        return true;
    }

    // ---------------- 帧 ----------------

    /// <summary>加密流上的一条控制消息：[2 字节长度][内容]。</summary>
    internal static async Task WriteRecordAsync(SecureStream stream, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<byte[]> ReadRecordAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, header, ct)) throw new EndOfStreamException(L.T("core.gate.protocol.closed"));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is 0 or > MaxRecord) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
        var body = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, body, ct)) throw new EndOfStreamException(L.T("core.gate.protocol.closed"));
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var frame = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, header, ct)) throw new EndOfStreamException(L.T("core.gate.protocol.closed"));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is 0 or > MaxHandshakeMessage) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
        var message = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, message, ct)) throw new EndOfStreamException(L.T("core.gate.protocol.closed"));
        return message;
    }
}
