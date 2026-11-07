using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 邀请码：JAY2-[T模式[D]-]A-B[-校验位]，例如 JAY2-T2D-0K7Q…（26 位）-3HZP…（26 位）-7。
/// A 是房主这次的“大厅密钥”L（16 字节），B 是这一张邀请的令牌 K（16 字节），都用 Crockford Base32 写成 26 位（最高 2 位恒为 0）。
/// 邀请码里没有房间本身的网络名和密钥：拿着它只能进大厅敲门，房主批准后才在加密通道里拿到房间的凭据。
/// 模式段只用来提前判断“这台电脑支不支持这种加密”；真正用的封装方式以房主批准时发来的为准。
/// 校验位按 Crockford 的 mod-37 写法（多出来的 5 个符号是 *~$=U），抄错一位多半能当场发现。解析和 <see cref="RoomCode"/> 一样宽松。
/// </summary>
public sealed record JayInvite(byte[] LobbyKey, byte[] Token, TunnelSettings Tunnel)
{
    public const string Prefix = "JAY2-";
    public const int KeyLength = 16;
    public const int PartLength = 26;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string CheckAlphabet = Alphabet + "*~$=U";

    public override string ToString()
    {
        var builder = new StringBuilder(Prefix);
        if (Tunnel.Wraps) builder.Append('T').Append((int)Tunnel.Mode).Append(Tunnel.Disguise ? "D" : "").Append('-');
        builder.Append(Encode(LobbyKey)).Append('-').Append(Encode(Token)).Append('-').Append(CheckSymbol(LobbyKey, Token, Tunnel));
        return builder.ToString();
    }

    /// <summary>打码后的样子（界面默认显示这个）：只露出前缀和最后 4 位。</summary>
    public string Masked
    {
        get
        {
            var text = ToString();
            return text[..(text.IndexOf('-') + 1)] + new string('•', 8) + text[^4..];
        }
    }

    public static JayInvite Create(byte[] lobbyKey, TunnelSettings tunnel)
    {
        if (lobbyKey.Length != KeyLength) throw new ArgumentException(null, nameof(lobbyKey));
        return new JayInvite(lobbyKey, RandomNumberGenerator.GetBytes(KeyLength), tunnel);
    }

    /// <summary>16 字节按大端整数写成 26 位 Crockford Base32。</summary>
    internal static string Encode(ReadOnlySpan<byte> bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[PartLength];
        for (var i = PartLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }

    private static byte[]? Decode(string part)
    {
        if (part.Length != PartLength) return null;
        var value = BigInteger.Zero;
        foreach (var c in part)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) return null;
            value = (value << 5) | digit;
        }
        // 26 × 5 = 130 位，最高 2 位必须是 0（第一位只能是 0–7）。
        if (value >> (KeyLength * 8) != 0) return null;
        var bytes = new byte[KeyLength];
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.CopyTo(bytes.AsSpan(KeyLength - raw.Length));
        return bytes;
    }

    /// <summary>校验位：把模式字节、A、B 连起来当一个大端整数，模 37。</summary>
    internal static char CheckSymbol(ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> token, TunnelSettings tunnel)
    {
        var data = new byte[1 + KeyLength * 2];
        data[0] = (byte)(tunnel.Wraps ? (int)tunnel.Mode << 1 | (tunnel.Disguise ? 1 : 0) : 0);
        lobbyKey.CopyTo(data.AsSpan(1));
        token.CopyTo(data.AsSpan(1 + KeyLength));
        return CheckAlphabet[(int)(new BigInteger(data, isUnsigned: true, isBigEndian: true) % 37)];
    }

    /// <summary>容忍大小写、空格、I/L 写成 1、O 写成 0、前后多余的文字；校验位可以省略，写了就必须对。</summary>
    public static bool TryParse(string? text, out JayInvite invite)
    {
        invite = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        var start = upper.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var body = upper[(start + Prefix.Length)..];
        var tunnel = TunnelSettings.Default;
        var firstDash = body.IndexOf('-');
        if (firstDash is > 0 and < 4)
        {
            var marker = body[..firstDash].Trim();
            if (marker.Length is < 2 or > 3 || marker[0] != 'T' || marker[1] is < '1' or > '3' || marker.Length == 3 && marker[2] != 'D') return false;
            tunnel = new TunnelSettings((TransportMode)(marker[1] - '0'), marker.Length == 3);
            body = body[(firstDash + 1)..];
        }
        var parts = body.Split('-', 3);
        if (parts.Length < 2) return false;
        var a = Normalize(parts[0]);
        // B 后面可能紧跟着粘贴时带上的文字（没有校验位时），只取前 26 位有效字符。
        var bText = parts[1].Trim();
        var bEnd = 0;
        while (bEnd < bText.Length && char.IsAsciiLetterOrDigit(bText[bEnd])) bEnd++;
        if (bEnd != PartLength) return false;
        var rest = parts.Length == 3 ? "-" + parts[2] : bText[bEnd..];
        var b = Normalize(bText[..bEnd]);
        if (a is null || b is null || Decode(a) is not { } lobbyKey || Decode(b) is not { } token) return false;

        if (rest.StartsWith('-') && rest.Length >= 2 && CheckAlphabet.Contains(NormalizeCheck(rest[1])))
        {
            // 校验位后面只能是普通文字（空格、标点、中文），不能紧跟字母数字（那多半是码被改坏了）。
            if (rest.Length > 2 && char.IsAsciiLetterOrDigit(rest[2])) return false;
            if (NormalizeCheck(rest[1]) != CheckSymbol(lobbyKey, token, tunnel)) return false;
        }
        else if (rest.Length > 0 && char.IsAsciiLetterOrDigit(rest[0])) return false;
        invite = new JayInvite(lobbyKey, token, tunnel);
        return true;
    }

    private static char NormalizeCheck(char c) => c switch { 'I' or 'L' => '1', 'O' => '0', _ => c };

    private static string? Normalize(string part)
    {
        var builder = new StringBuilder(part.Length);
        foreach (var raw in part.Trim())
        {
            var c = NormalizeCheck(raw);
            if (Alphabet.IndexOf(c) < 0) return null;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>旧版本的房间码（AMB-）和直连邀请码（AMBDIRECT）：加入框里看到它们时提示对方更新。</summary>
    public static bool LooksLikeOldCode(string? text) =>
        text is not null && (text.Contains("AMB-", StringComparison.OrdinalIgnoreCase) || text.Contains("AMBDIRECT", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 从邀请码里的两把密钥派生出大厅和敲门要用的值（HKDF-SHA256，salt = "jay-lobby-v1"）。
/// 大厅密钥 L：大厅的 EasyTier 网络名和密钥；邀请令牌 K：敲门时报的编号（明文发，用来找到是哪张邀请）和握手的 psk。
/// </summary>
public static class LobbyKeys
{
    private static readonly byte[] Salt = "jay-lobby-v1"u8.ToArray();

    private static byte[] Derive(ReadOnlySpan<byte> key, string info, int length)
    {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, Salt, Encoding.ASCII.GetBytes(info));
        return output;
    }

    public static string LobbyName(ReadOnlySpan<byte> lobbyKey) => "jay-l-" + Convert.ToHexStringLower(Derive(lobbyKey, "name", 8));

    public static string LobbySecret(ReadOnlySpan<byte> lobbyKey) => Convert.ToHexStringLower(Derive(lobbyKey, "secret", 32));

    public static byte[] InviteId(ReadOnlySpan<byte> token) => Derive(token, "id", 8);

    public static byte[] KnockPsk(ReadOnlySpan<byte> token) => Derive(token, "psk", 32);
}
