using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 网关读客户端发来的头一两个 Minecraft 包（还没压缩、没加密）：Handshake，以及登录时的 Login Start。
/// 用来核对“登录用的用户名 = 这台设备绑定的用户名”。读过的原始字节原样保留，核对通过后先转给服务端，再接着转发后面的数据。
/// 只认新版协议（1.7 以后的 VarInt 帧）；1.6 及更早的旧版状态查询（首字节 0xFE）直接拒绝。
/// </summary>
public static class McLogin
{
    /// <summary>Handshake 的 next state。</summary>
    public enum Intent { Status = 1, Login = 2, Transfer = 3 }

    public sealed record Result(Intent Intent, string? Username, byte[] Raw);

    // Handshake：服务器地址最长 255 个字符（UTF-8 最多 ×3），Forge 会在后面加 “\0FML3\0” 之类的标记，留够余量。
    private const int MaxHandshake = 2048;
    // Login Start：名字 16 字符 + 1.19 起的签名数据或 1.20.2 起的 UUID，留够余量。
    private const int MaxLoginStart = 1024;

    public static async Task<Result> ReadAsync(Stream stream, CancellationToken ct)
    {
        var raw = new MemoryStream();
        var handshake = await ReadPacketAsync(stream, raw, MaxHandshake, ct);
        var p = 0;
        if (ReadVarInt(handshake, ref p) != 0x00) throw Bad();
        ReadVarInt(handshake, ref p); // 协议号：不限制，服务端自己会判断版本
        ReadString(handshake, ref p, 1024);
        if (p + 2 > handshake.Length) throw Bad();
        p += 2; // 端口
        var next = ReadVarInt(handshake, ref p);
        if (p != handshake.Length || next is < 1 or > 3) throw Bad();
        var intent = (Intent)next;
        if (intent == Intent.Status) return new Result(intent, null, raw.ToArray());

        var login = await ReadPacketAsync(stream, raw, MaxLoginStart, ct);
        p = 0;
        if (ReadVarInt(login, ref p) != 0x00) throw Bad();
        var name = ReadString(login, ref p, 16);
        if (!GateInvite.IsValidUsername(name)) throw Bad();
        return new Result(intent, name, raw.ToArray());
    }

    private static InvalidDataException Bad() => new(L.T("core.gate.mc.malformed"));

    private static async Task<byte[]> ReadPacketAsync(Stream stream, MemoryStream raw, int max, CancellationToken ct)
    {
        var length = 0;
        var one = new byte[1];
        for (var i = 0; ; i++)
        {
            if (i == 3) throw Bad();
            if (!await SecureStream.ReadExactAsync(stream, one, ct)) throw new EndOfStreamException(L.T("core.gate.mc.closed"));
            raw.WriteByte(one[0]);
            // 旧版（1.6 及更早）的状态查询以 0xFE 开头，不是 VarInt 帧。
            if (i == 0 && one[0] == 0xFE) throw Bad();
            length |= (one[0] & 0x7F) << (7 * i);
            if ((one[0] & 0x80) == 0) break;
        }
        if (length is <= 0 || length > max) throw Bad();
        var body = new byte[length];
        if (!await SecureStream.ReadExactAsync(stream, body, ct)) throw new EndOfStreamException(L.T("core.gate.mc.closed"));
        raw.Write(body);
        return body;
    }

    private static int ReadVarInt(byte[] data, ref int p)
    {
        var value = 0;
        for (var i = 0; i < 5; i++)
        {
            if (p >= data.Length) throw Bad();
            var b = data[p++];
            value |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return value;
        }
        throw Bad();
    }

    private static string ReadString(byte[] data, ref int p, int maxChars)
    {
        var length = ReadVarInt(data, ref p);
        if (length < 0 || length > maxChars * 3 || p + length > data.Length) throw Bad();
        var text = Encoding.UTF8.GetString(data, p, length);
        p += length;
        if (text.Length > maxChars) throw Bad();
        return text;
    }
}
