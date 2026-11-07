using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// Jay 服务器的邀请码：<c>JAYS1-</c> + Crockford Base32。管理员在 VPS 上用 <c>jay-gate invite</c> 生成，发给一个具体的玩家。
/// 内容：网关地址和端口、网关公钥的哈希（钉住，防冒充服务器）、一次性令牌（128 位）、这份邀请绑定的用户名、4 字节校验。
/// 任何一位被改都会校验失败，直接报错，不会被当成别的码。邀请里没有长期密钥：令牌只用来做第一次握手，用掉或过期就作废。
/// <para>版本 2：入口开了端口轮换时，后面再接轮换规则和这个入口的种子（Port 只是固定端口，可能已经关了）。</para>
/// </summary>
public sealed record GateInvite(string Host, int Port, byte[] ServerKeyHash, byte[] Token, string Username, GateEntryRotation? Rotation = null)
{
    public const string Prefix = "JAYS1-";
    public const int KeyHashLength = 16;
    public const int TokenLength = 16;
    private const byte Version = 1;
    private const byte VersionRotating = 2;
    private const int ChecksumLength = 4;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public string Encode()
    {
        Validate(Host, Port, ServerKeyHash, Token, Username);
        var host = Encoding.ASCII.GetBytes(Host);
        var name = Encoding.ASCII.GetBytes(Username);
        var body = new List<byte> { Rotation is null ? Version : VersionRotating, (byte)host.Length };
        body.AddRange(host);
        Span<byte> port = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)Port);
        body.AddRange(port.ToArray());
        body.AddRange(ServerKeyHash);
        body.AddRange(Token);
        body.Add((byte)name.Length);
        body.AddRange(name);
        if (Rotation is not null)
        {
            Rotation.Rotation.Write(body);
            body.AddRange(Rotation.Seed);
        }
        var bytes = body.ToArray();
        return Prefix + Base32Encode([.. bytes, .. Checksum(bytes)]);
    }

    /// <summary>从一段文字里找出邀请码并解析（前后有别的字、中间有空格或换行都行）。没有或者不对返回 null，error 说明原因。</summary>
    public static GateInvite? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = L.T("core.gate.invite.missing"); return null; }
        var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) { error = L.T("core.gate.invite.missing"); return null; }
        var chars = new StringBuilder();
        foreach (var c in text.AsSpan(start + Prefix.Length))
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            if (!char.IsAsciiLetterOrDigit(c)) break;
            chars.Append(c);
        }
        var raw = Base32Decode(chars.ToString());
        if (raw is null || raw.Length < 1 + 1 + 2 + KeyHashLength + TokenLength + 1 + ChecksumLength)
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
        var bytes = raw.AsSpan(0, raw.Length - ChecksumLength);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(bytes), raw.AsSpan(raw.Length - ChecksumLength)))
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
        if (bytes[0] is not (Version or VersionRotating))
        {
            error = L.T("core.gate.invite.version");
            return null;
        }
        try
        {
            var p = 1;
            int hostLength = bytes[p++];
            var host = Encoding.ASCII.GetString(bytes.Slice(p, hostLength)); p += hostLength;
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(p, 2)); p += 2;
            var keyHash = bytes.Slice(p, KeyHashLength).ToArray(); p += KeyHashLength;
            var token = bytes.Slice(p, TokenLength).ToArray(); p += TokenLength;
            int nameLength = bytes[p++];
            var name = Encoding.ASCII.GetString(bytes.Slice(p, nameLength)); p += nameLength;
            GateEntryRotation? rotation = null;
            if (bytes[0] == VersionRotating)
            {
                var rule = GateRotation.Read(bytes[p..]) ?? throw new FormatException();
                p += GateRotation.EncodedLength;
                rotation = new GateEntryRotation(rule, bytes.Slice(p, GateRotation.SeedLength).ToArray());
                p += GateRotation.SeedLength;
            }
            if (p != bytes.Length) throw new FormatException();
            Validate(host, port, keyHash, token, name);
            return new GateInvite(host, port, keyHash, token, name, rotation);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
    }

    /// <summary>Minecraft 用户名：3–16 个字母、数字或下划线。离线账号也按这个规则。</summary>
    public static bool IsValidUsername(string? name) =>
        name is { Length: >= 3 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>主机名或 IP：只允许字母、数字、点、横杠和冒号（IPv6），最长 253。</summary>
    public static bool IsValidHost(string? host) =>
        host is { Length: >= 1 and <= 253 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')
        && !host.StartsWith('-') && !host.StartsWith('.');

    private static void Validate(string host, int port, byte[] keyHash, byte[] token, string username)
    {
        if (!IsValidHost(host)) throw new FormatException(L.T("core.gate.invite.badhost"));
        if (port is < 1 or > 65535) throw new FormatException(L.T("core.gate.invite.badport"));
        if (keyHash.Length != KeyHashLength || token.Length != TokenLength) throw new FormatException(L.T("core.gate.invite.corrupt"));
        if (!IsValidUsername(username)) throw new FormatException(L.T("core.gate.invite.badname"));
    }

    private static byte[] Checksum(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData([.. "jay-invite-v1"u8, .. bytes], digest);
        return digest[..ChecksumLength].ToArray();
    }

    internal static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Crockford[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) text.Append(Crockford[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>手抄容错：I、L 当 1，O 当 0，不分大小写。多出来的位必须是 0，否则当作被改过。</summary>
    internal static byte[]? Base32Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            var c = char.ToUpperInvariant(raw) switch { 'I' or 'L' => '1', 'O' => '0', var x => x };
            var v = Crockford.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        if (bits >= 5 || buffer != 0) return null;
        return [.. output];
    }

    /// <summary>日志和界面上显示：不带令牌。</summary>
    public override string ToString() => $"{Prefix}... ({Username}@{Host}:{Port})";
}
