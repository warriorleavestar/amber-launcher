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
}

/// <summary>入服成功后客户端要保存的东西：网关地址、网关公钥（钉住）、访问密钥、绑定的用户名。</summary>
public sealed record GateMembership(string Host, int Port, byte[] ServerPublicKey, byte[] AccessKey, string Username)
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
/// </summary>
public static class GateProtocol
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int MaxHandshakeMessage = 1024;
    internal static readonly byte[] EnrollPrologue = "jay-gate-v1|enroll"u8.ToArray();
    internal static readonly byte[] JoinPrologue = "jay-gate-v1|join"u8.ToArray();

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
            (secure, serverKey) = await InitiateAsync(stream, hs, key => CryptographicOperations.FixedTimeEquals(ServerKeyHash(key), invite.ServerKeyHash), timeout.Token);
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
            var name = Encoding.ASCII.GetString(reply, 34, reply[33]);
            if (34 + reply[33] != reply.Length || !GateInvite.IsValidUsername(name)) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
            await secure.CloseAsync(timeout.Token);
            return new GateMembership(invite.Host, invite.Port, serverKey, accessKey, name);
        }
        finally
        {
            await secure.DisposeAsync();
        }
    }

    /// <summary>进服：返回已认证的加密流，之后直接当成到 MC 服务端的连接用。网关拒绝时抛 <see cref="GateRejectedException"/>。</summary>
    public static async Task<SecureStream> JoinAsync(Stream stream, GateMembership membership, X25519KeyPair device, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        using var hs = new NoiseHandshake(NoisePattern.XXpsk0, initiator: true, JoinPrologue, membership.AccessKey, device);
        var (secure, _) = await InitiateAsync(stream, hs, key => CryptographicOperations.FixedTimeEquals(key, membership.ServerPublicKey), timeout.Token);
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

    private static async Task<(SecureStream, byte[] ServerKey)> InitiateAsync(Stream stream, NoiseHandshake hs, Func<byte[], bool> trustServer, CancellationToken ct)
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
        await WriteFrameAsync(stream, hs.WriteMessage([]), ct);
        var (send, receive) = hs.Split();
        return (new SecureStream(stream, send, receive, Profile), serverKey);
    }

    // ---------------- 网关 ----------------

    /// <summary>网关这边一次握手的结果。</summary>
    public sealed record Accepted(SecureStream Stream, byte[] DevicePublicKey, bool IsEnroll, string? InviteId);

    /// <summary>
    /// 网关：读第一条消息，按顺序试 join（访问密钥）和每个有效邀请的 psk，哪个解得开就是哪种。都不行返回 null（调用方直接断开，不回任何字节）。
    /// </summary>
    public static async Task<Accepted?> AcceptAsync(Stream stream, X25519KeyPair serverKey, byte[] accessKey, IReadOnlyList<(string Id, byte[] Psk)> invites, CancellationToken ct)
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
        var candidates = new List<(byte[] Prologue, byte[] Psk, string? InviteId)> { (JoinPrologue, accessKey, null) };
        candidates.AddRange(invites.Select(i => (EnrollPrologue, i.Psk, (string?)i.Id)));
        foreach (var (prologue, psk, inviteId) in candidates)
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
                byte[] third;
                try
                {
                    third = await ReadFrameAsync(stream, ct);
                    hs.ReadMessage(third);
                }
                catch (Exception e) when (e is EndOfStreamException or CryptographicException)
                {
                    return null;
                }
                var device = hs.RemoteStaticPublicKey!;
                var (send, receive) = hs.Split();
                return new Accepted(new SecureStream(stream, send, receive, Profile), device, inviteId is not null, inviteId);
            }
        }
        return null;
    }

    /// <summary>网关：回入服结果。</summary>
    public static Task SendEnrollResultAsync(SecureStream stream, GateStatus status, byte[]? accessKey, string? username, CancellationToken ct)
    {
        if (status != GateStatus.Ok) return WriteRecordAsync(stream, [(byte)status], ct);
        var name = Encoding.ASCII.GetBytes(username!);
        return WriteRecordAsync(stream, [(byte)GateStatus.Ok, .. accessKey!, (byte)name.Length, .. name], ct);
    }

    public static Task SendJoinResultAsync(SecureStream stream, GateStatus status, CancellationToken ct) =>
        WriteRecordAsync(stream, [(byte)status], ct);

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
        if (length is 0 or > 256) throw new CryptographicException(L.T("core.gate.protocol.malformed"));
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
