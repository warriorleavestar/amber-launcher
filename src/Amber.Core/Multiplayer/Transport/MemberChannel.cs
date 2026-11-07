using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// 经邀请加入的房间里，成员连房主的一条连接（游戏通道或聊天）：
/// 先明文发 8 字节成员编号，再用这个成员自己的 psk 跑 NNpsk0（极端加密模式下 NNhfspsk0），得到 SecureStream。
/// 房主按编号在成员表里查 psk，查不到直接断开；psk 不对第一条握手消息就解不开。
/// prologue 里带上用途、模式和编号，一条连接的握手不能挪到另一种用途上用。
/// 格式：[8 字节编号][2 字节长度][握手消息 1（载荷加密）] → [2 字节长度][握手消息 2]。
/// </summary>
public static class MemberChannel
{
    private const int MaxHandshakeMessage = 4096;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    internal static byte[] Prologue(string purpose, TransportMode mode, bool disguise, ReadOnlySpan<byte> id) =>
        [.. Encoding.ASCII.GetBytes($"jay-member-v1|{purpose}|{(int)mode}|{(disguise ? 1 : 0)}|"), .. id];

    /// <summary>聊天用的通道参数：模式是极端加密时用混合握手，其余 NNpsk0；聊天量小，不填充到档位、不发掩护流量。</summary>
    public static TransportProfile ChatProfile(TransportMode mode) =>
        TransportProfile.For(TransportMode.High) with { Pattern = mode == TransportMode.Extreme ? NoisePattern.NNhfspsk0 : NoisePattern.NNpsk0 };

    /// <summary>成员这边：连上之后调用。</summary>
    public static async Task<SecureStream> ConnectAsync(Stream stream, MemberKey key, TransportProfile profile, string purpose, byte[] payload, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var psk = key.CopyPsk();
        try
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: true, Prologue(purpose, profile.Mode, false, key.Id), psk);
            var message = hs.WriteMessage(payload);
            var first = new byte[MemberKey.IdLength + 2 + message.Length];
            key.Id.CopyTo(first, 0);
            BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(MemberKey.IdLength), (ushort)message.Length);
            message.CopyTo(first, MemberKey.IdLength + 2);
            await stream.WriteAsync(first, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            hs.ReadMessage(await ReadFramedAsync(stream, timeout.Token));
            var (send, receive) = hs.Split();
            return new SecureStream(stream, send, receive, profile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    /// <summary>房主这边：接受连接后调用。编号不认识、psk 不对都抛 CryptographicException（调用方断开）。</summary>
    public static async Task<(SecureStream Stream, MemberEntry Member, byte[] Payload)> AcceptAsync(Stream stream, MemberTable members, TransportProfile profile, string purpose,
        Func<byte[], bool>? acceptPayload = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var member = await ReadMemberAsync(stream, members, timeout.Token);
        return await AcceptMemberAsync(stream, member, profile, purpose, acceptPayload, timeout.Token);
    }

    /// <summary>
    /// 查到成员之后的握手。查到和取钥匙之间他可能刚好被踢：那时钥匙已清零，CopyPsk 抛 CryptographicException，
    /// 不会拿一把全零的 psk 去握手。
    /// </summary>
    internal static async Task<(SecureStream Stream, MemberEntry Member, byte[] Payload)> AcceptMemberAsync(Stream stream, MemberEntry member, TransportProfile profile, string purpose,
        Func<byte[], bool>? acceptPayload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        if (member.Revoked) throw new CryptographicException(L.T("core.mp.member.unknown"));
        var psk = member.Key.CopyPsk();
        try
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: false, Prologue(purpose, profile.Mode, false, member.Key.Id), psk);
            var payload = hs.ReadMessage(await ReadFramedAsync(stream, timeout.Token));
            // 载荷先认过（比如服务号），不认识就在分出会话密钥之前失败。
            if (acceptPayload is not null && !acceptPayload(payload)) throw new CryptographicException(L.T("core.mp.transport.tunnel.unknownservice"));
            await WriteFramedAsync(stream, hs.WriteMessage([]), timeout.Token);
            var (send, receive) = hs.Split();
            return (new SecureStream(stream, send, receive, profile), member, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    /// <summary>读 8 字节编号，在成员表里查。查不到抛 CryptographicException。</summary>
    internal static async Task<MemberEntry> ReadMemberAsync(Stream stream, MemberTable members, CancellationToken ct)
    {
        var id = new byte[MemberKey.IdLength];
        if (!await SecureStream.ReadExactAsync(stream, id, ct)) throw new EndOfStreamException(L.T("core.mp.transport.tunnel.missinghandshake"));
        return Lookup(members, id);
    }

    internal static MemberEntry Lookup(MemberTable members, ReadOnlySpan<byte> id) =>
        members.TryGet(id, out var member) && !member.Revoked ? member : throw new CryptographicException(L.T("core.mp.member.unknown"));

    internal static async Task WriteFramedAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var frame = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<byte[]> ReadFramedAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await SecureStream.ReadExactAsync(stream, header, ct)) throw new EndOfStreamException(L.T("core.mp.transport.tunnel.missinghandshake"));
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is 0 or > MaxHandshakeMessage) throw new CryptographicException(L.T("core.mp.transport.tunnel.handshakelength"));
        var message = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, message, ct)) throw new EndOfStreamException(L.T("core.mp.transport.tunnel.handshaketruncated"));
        return message;
    }
}
