using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>经封装通道连到房主那边的哪个服务。</summary>
public enum TunnelService : byte
{
    Game = 1,
    Chat = 2,
    /// <summary>“仅游戏”转发：经出口成员的 SOCKS5 代理上网。</summary>
    Socks = 3,
    /// <summary>邀请房间的远程控制（成员连房主的远程控制服务；里面再跑一层成员钥匙的握手）。</summary>
    Remote = 4,
}

/// <summary>
/// 封装通道的握手：在一条已经建立的 TCP 连接（经 EasyTier）上跑 Noise 握手，得到 SecureStream。
/// 不伪装时：客户端发 [2 字节长度][握手消息 1]，服务端回 [2 字节长度][握手消息 2]。
/// 伪装成 Minecraft 时：客户端先发真正格式的 Handshake 和 Login Start 包，服务端回 Encryption Request（里面装握手消息 1），
/// 客户端回 Encryption Response（装握手消息 2）。之后的数据在外面看来就是开了加密的 MC 数据流（一串随机字节）。
/// 伪装只是让流量第一眼像 MC 登录，长度、时间、密钥大小这些都和真的服务器不一样，挡不住认真分析的人。
/// </summary>
public static class TunnelHandshake
{
    private const int MaxHandshakeMessage = 4096;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>AMB- 房间码的房间：所有人共用的、由房间密钥派生的 psk。经邀请加入的房间不用它（每个成员一把自己的）。</summary>
    internal static byte[] Psk(RoomCode room) => room.FromInvite
        ? throw new InvalidOperationException("invite rooms use per-member keys")
        : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(room.Secret), 32, Encoding.UTF8.GetBytes(room.NetworkName), "amber-tunnel-psk-v1"u8.ToArray());

    internal static byte[] Prologue(TransportMode mode, bool disguise) => Encoding.ASCII.GetBytes($"amber-tunnel-v1|{(int)mode}|{(disguise ? 1 : 0)}");

    private static (LengthMask Initiator, LengthMask Responder) Masks(byte[] handshakeHash) =>
        (new LengthMask(HKDF.DeriveKey(HashAlgorithmName.SHA256, handshakeHash, 32, [], "amber-length-i2r"u8.ToArray())),
         new LengthMask(HKDF.DeriveKey(HashAlgorithmName.SHA256, handshakeHash, 32, [], "amber-length-r2i"u8.ToArray())));

    /// <summary>客户端（加入方）：连上之后调用。</summary>
    public static async Task<SecureStream> ConnectAsync(Stream stream, RoomCode room, TransportMode mode, bool disguise, TunnelService service, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var profile = TransportProfile.For(mode);
        if (room.FromInvite) return await ConnectMemberAsync(stream, room.Member ?? throw new InvalidOperationException("no member key"), profile, disguise, service, timeout.Token);
        if (!disguise)
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: true, Prologue(mode, false), Psk(room));
            await WriteFramedAsync(stream, hs.WriteMessage([(byte)service]), timeout.Token);
            hs.ReadMessage(await ReadFramedAsync(stream, timeout.Token));
            var (send, receive) = hs.Split();
            return new SecureStream(stream, send, receive, profile);
        }
        else
        {
            // 伪装时服务端是 Noise 的发起方（MC 里也是服务端先发 Encryption Request）。
            using var hs = new NoiseHandshake(profile.Pattern, initiator: false, Prologue(mode, true), Psk(room));
            await McDisguise.WriteLoginAsync(stream, timeout.Token);
            var request = await McDisguise.ReadEncryptionRequestAsync(stream, timeout.Token);
            hs.ReadMessage(request);
            await McDisguise.WriteEncryptionResponseAsync(stream, hs.WriteMessage([(byte)service]), timeout.Token);
            var (send, receive) = hs.Split();
            var (i2r, r2i) = Masks(hs.HandshakeHash);
            return new SecureStream(stream, send, receive, profile, sendMask: r2i, receiveMask: i2r);
        }
    }

    /// <summary>服务端（房主或出口成员）：接受连接后调用。返回 null 表示这是一次伪装下的状态查询（已经回答了，连接可以关）。</summary>
    public static async Task<(SecureStream Stream, TunnelService Service)?> AcceptAsync(Stream stream, RoomCode room, TransportMode mode, bool disguise, CancellationToken ct = default) =>
        await AcceptWithMemberAsync(stream, room, mode, disguise, ct) is { } a ? (a.Stream, a.Service) : null;

    /// <summary>同 AcceptAsync，邀请房间时另外给出是哪个成员（AMB- 房间是 null）。</summary>
    public static async Task<(SecureStream Stream, TunnelService Service, MemberEntry? Member)?> AcceptWithMemberAsync(Stream stream, RoomCode room, TransportMode mode, bool disguise, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var profile = TransportProfile.For(mode);
        if (room.FromInvite) return await AcceptMemberAsync(stream, room.Members ?? throw new CryptographicException(L.T("core.mp.member.unknown")), profile, disguise, timeout.Token);
        if (!disguise)
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: false, Prologue(mode, false), Psk(room));
            var payload = hs.ReadMessage(await ReadFramedAsync(stream, timeout.Token));
            // 先认服务：不认识就在分出会话密钥之前失败（不留下没人管的密钥和换钥定时器）。
            var service = ParseService(payload);
            await WriteFramedAsync(stream, hs.WriteMessage([]), timeout.Token);
            var (send, receive) = hs.Split();
            return (new SecureStream(stream, send, receive, profile), service, null);
        }
        else
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: true, Prologue(mode, true), Psk(room));
            if (!await McDisguise.ReadLoginAsync(stream, timeout.Token)) return null;
            await McDisguise.WriteEncryptionRequestAsync(stream, hs.WriteMessage([]), timeout.Token);
            var payload = hs.ReadMessage(await McDisguise.ReadEncryptionResponseAsync(stream, timeout.Token));
            var service = ParseService(payload);
            var (send, receive) = hs.Split();
            var (i2r, r2i) = Masks(hs.HandshakeHash);
            return (new SecureStream(stream, send, receive, profile, sendMask: i2r, receiveMask: r2i), service, null);
        }
    }

    /// <summary>
    /// 邀请房间的成员连房主：不伪装时就是 MemberChannel（明文编号 + 成员 psk 的 NN 握手）。
    /// 伪装时编号放在 Login Start 的 UUID 前 8 字节里（后 8 字节随机），房主读到后查表再发 Encryption Request。
    /// </summary>
    private static async Task<SecureStream> ConnectMemberAsync(Stream stream, MemberKey key, TransportProfile profile, bool disguise, TunnelService service, CancellationToken ct)
    {
        if (!disguise) return await MemberChannel.ConnectAsync(stream, key, profile, "tunnel", [(byte)service], ct);
        var psk = key.CopyPsk();
        try
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: false, MemberChannel.Prologue("tunnel", profile.Mode, true, key.Id), psk);
            await McDisguise.WriteLoginAsync(stream, ct, key.Id);
            hs.ReadMessage(await McDisguise.ReadEncryptionRequestAsync(stream, ct));
            await McDisguise.WriteEncryptionResponseAsync(stream, hs.WriteMessage([(byte)service]), ct);
            var (send, receive) = hs.Split();
            var (i2r, r2i) = Masks(hs.HandshakeHash);
            return new SecureStream(stream, send, receive, profile, sendMask: r2i, receiveMask: i2r);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    private static async Task<(SecureStream Stream, TunnelService Service, MemberEntry? Member)?> AcceptMemberAsync(Stream stream, MemberTable members, TransportProfile profile, bool disguise, CancellationToken ct)
    {
        if (!disguise)
        {
            var (secure, entry, payload) = await MemberChannel.AcceptAsync(stream, members, profile, "tunnel",
                p => p is [var b] && Enum.IsDefined((TunnelService)b), ct);
            return (secure, (TunnelService)payload[0], entry);
        }
        var uuid = await McDisguise.ReadLoginUuidAsync(stream, ct);
        if (uuid is null) return null;
        var member = MemberChannel.Lookup(members, uuid.AsSpan(0, MemberKey.IdLength));
        var psk = member.Key.CopyPsk();
        try
        {
            using var hs = new NoiseHandshake(profile.Pattern, initiator: true, MemberChannel.Prologue("tunnel", profile.Mode, true, member.Key.Id), psk);
            await McDisguise.WriteEncryptionRequestAsync(stream, hs.WriteMessage([]), ct);
            var service = ParseService(hs.ReadMessage(await McDisguise.ReadEncryptionResponseAsync(stream, ct)));
            var (send, receive) = hs.Split();
            var (i2r, r2i) = Masks(hs.HandshakeHash);
            return (new SecureStream(stream, send, receive, profile, sendMask: i2r, receiveMask: r2i), service, member);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    private static TunnelService ParseService(byte[] payload) =>
        payload is [var b] && Enum.IsDefined((TunnelService)b) ? (TunnelService)b : throw new CryptographicException(L.T("core.mp.transport.tunnel.unknownservice"));

    private static async Task WriteFramedAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var frame = new byte[2 + message.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadFramedAsync(Stream stream, CancellationToken ct)
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

/// <summary>
/// Minecraft 协议外观（1.21.1，协议号 767）：VarInt 长度 + VarInt 包 id + 字段，未压缩。
/// 只实现伪装要用的四个包，另外能像真服务器一样回答状态查询（服务器列表里的 ping），免得一连就露馅。
/// </summary>
public static class McDisguise
{
    public const int ProtocolVersion = 767;
    private const int MaxPacket = 8192;

    /// <param name="memberId">邀请房间里成员的编号：放在 UUID 的前 8 字节（后 8 字节随机）。</param>
    public static async Task WriteLoginAsync(Stream stream, CancellationToken ct, byte[]? memberId = null)
    {
        var handshake = new PacketWriter();
        handshake.VarInt(0x00).VarInt(ProtocolVersion).String("localhost").UShort(25565).VarInt(2);
        var login = new PacketWriter();
        var name = "Player" + RandomNumberGenerator.GetInt32(100, 10000);
        var uuid = RandomNumberGenerator.GetBytes(16);
        memberId?.AsSpan(0, Math.Min(memberId.Length, 8)).CopyTo(uuid);
        login.VarInt(0x00).String(name).Bytes(uuid);
        await stream.WriteAsync((byte[])[.. handshake.ToPacket(), .. login.ToPacket()], ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读客户端的 Handshake + Login Start。是状态查询时按服务器列表的格式回答并返回 false。</summary>
    public static async Task<bool> ReadLoginAsync(Stream stream, CancellationToken ct) => await ReadLoginUuidAsync(stream, ct) is not null;

    /// <summary>同 ReadLoginAsync，另外给出 Login Start 里的 16 字节 UUID；状态查询时返回 null。</summary>
    public static async Task<byte[]?> ReadLoginUuidAsync(Stream stream, CancellationToken ct)
    {
        var handshake = new PacketReader(await ReadPacketAsync(stream, ct));
        if (handshake.VarInt() != 0x00) throw new CryptographicException(L.T("core.mp.transport.mc.handshakerequired"));
        handshake.VarInt();
        handshake.String(255);
        handshake.UShort();
        var next = handshake.VarInt();
        if (next == 1)
        {
            await AnswerStatusAsync(stream, ct);
            return null;
        }
        if (next is not (2 or 3)) throw new CryptographicException(L.T("core.mp.transport.mc.nextstate"));
        var login = new PacketReader(await ReadPacketAsync(stream, ct));
        if (login.VarInt() != 0x00) throw new CryptographicException(L.T("core.mp.transport.mc.loginstartrequired"));
        login.String(16);
        return login.Bytes(16);
    }

    private static async Task AnswerStatusAsync(Stream stream, CancellationToken ct)
    {
        var request = new PacketReader(await ReadPacketAsync(stream, ct));
        if (request.VarInt() != 0x00) return;
        var json = $$"""{"version":{"name":"1.21.1","protocol":{{ProtocolVersion}}},"players":{"max":20,"online":0},"description":{"text":"A Minecraft Server"},"enforcesSecureChat":false}""";
        await stream.WriteAsync(new PacketWriter().VarInt(0x00).String(json).ToPacket(), ct);
        try
        {
            var ping = new PacketReader(await ReadPacketAsync(stream, ct));
            if (ping.VarInt() == 0x01) await stream.WriteAsync(new PacketWriter().VarInt(0x01).Long(ping.Long()).ToPacket(), ct);
        }
        catch (EndOfStreamException) { }
        await stream.FlushAsync(ct);
    }

    public static async Task WriteEncryptionRequestAsync(Stream stream, byte[] noiseMessage, CancellationToken ct)
    {
        var p = new PacketWriter().VarInt(0x01).String("").PrefixedBytes(noiseMessage).PrefixedBytes(RandomNumberGenerator.GetBytes(4)).Bool(true);
        await stream.WriteAsync(p.ToPacket(), ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadEncryptionRequestAsync(Stream stream, CancellationToken ct)
    {
        var p = new PacketReader(await ReadPacketAsync(stream, ct));
        if (p.VarInt() != 0x01) throw new CryptographicException(L.T("core.mp.transport.mc.encryptionrequestrequired"));
        p.String(20);
        return p.PrefixedBytes();
    }

    public static async Task WriteEncryptionResponseAsync(Stream stream, byte[] noiseMessage, CancellationToken ct)
    {
        var p = new PacketWriter().VarInt(0x01).PrefixedBytes(noiseMessage).PrefixedBytes(RandomNumberGenerator.GetBytes(128));
        await stream.WriteAsync(p.ToPacket(), ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadEncryptionResponseAsync(Stream stream, CancellationToken ct)
    {
        var p = new PacketReader(await ReadPacketAsync(stream, ct));
        if (p.VarInt() != 0x01) throw new CryptographicException(L.T("core.mp.transport.mc.encryptionresponserequired"));
        return p.PrefixedBytes();
    }

    internal static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift > 28) throw new CryptographicException(L.T("core.mp.transport.packet.varinttoolong"));
            var one = new byte[1];
            if (!await SecureStream.ReadExactAsync(stream, one, ct)) throw new EndOfStreamException(L.T("core.mp.transport.tunnel.disconnected"));
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
        }
        if (length is <= 0 or > MaxPacket) throw new CryptographicException(L.T("core.mp.transport.mc.packetlength"));
        var packet = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, packet, ct)) throw new EndOfStreamException(L.T("core.mp.transport.mc.packettruncated"));
        return packet;
    }

    internal sealed class PacketWriter
    {
        private readonly List<byte> _data = [];

        public PacketWriter VarInt(int value)
        {
            var v = (uint)value;
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                _data.Add(v != 0 ? (byte)(b | 0x80) : b);
            } while (v != 0);
            return this;
        }

        public PacketWriter String(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return VarInt(bytes.Length).Bytes(bytes);
        }

        public PacketWriter UShort(ushort value) => Bytes([(byte)(value >> 8), (byte)value]);
        public PacketWriter Long(long value)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            return Bytes(b);
        }
        public PacketWriter Bool(bool value) => Bytes([(byte)(value ? 1 : 0)]);
        public PacketWriter PrefixedBytes(byte[] bytes) => VarInt(bytes.Length).Bytes(bytes);

        public PacketWriter Bytes(byte[] bytes)
        {
            _data.AddRange(bytes);
            return this;
        }

        public byte[] ToPacket()
        {
            var body = _data.ToArray();
            return [.. new PacketWriter().VarInt(body.Length)._data, .. body];
        }
    }

    internal sealed class PacketReader(byte[] data)
    {
        private int _pos;

        public int VarInt()
        {
            var value = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 28 || _pos >= data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.invalidvarint"));
                var b = data[_pos++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
        }

        public string String(int maxChars)
        {
            var length = VarInt();
            if (length < 0 || length > maxChars * 4 || _pos + length > data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.stringlength"));
            var s = Encoding.UTF8.GetString(data, _pos, length);
            _pos += length;
            return s;
        }

        public ushort UShort()
        {
            if (_pos + 2 > data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.tooshort"));
            var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        public long Long()
        {
            if (_pos + 8 > data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.tooshort"));
            var v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(_pos));
            _pos += 8;
            return v;
        }

        public byte[] Bytes(int count)
        {
            if (_pos + count > data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.tooshort"));
            var b = data.AsSpan(_pos, count).ToArray();
            _pos += count;
            return b;
        }

        public byte[] PrefixedBytes()
        {
            var length = VarInt();
            if (length < 0 || _pos + length > data.Length) throw new CryptographicException(L.T("core.mp.transport.packet.arraylength"));
            var b = data.AsSpan(_pos, length).ToArray();
            _pos += length;
            return b;
        }
    }
}
