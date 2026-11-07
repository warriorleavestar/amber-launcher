using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// 节点码：<c>JAYR1-</c> + Crockford Base32。管理员在网关上 <c>jay-server relays add</c> 时生成，拿到节点电脑上运行 <c>jay-server relay --code</c>。
/// 内容：网关公钥的哈希（节点核对网关身份）、32 字节节点密钥（派生心跳通道的 psk）、4 字节校验。
/// 节点第一次连上心跳通道时，网关记下节点的公钥；之后只认这把公钥。节点码泄露后用 <c>relays code</c> 重发一个，旧的立即作废。
/// </summary>
public sealed record GateRelayCode(byte[] ServerKeyHash, byte[] Secret)
{
    public const string Prefix = "JAYR1-";
    public const int SecretLength = 32;
    private const byte Version = 1;
    private const int ChecksumLength = 4;

    public string Encode()
    {
        byte[] body = [Version, .. ServerKeyHash, .. Secret];
        return Prefix + GateInvite.Base32Encode([.. body, .. Checksum(body)]);
    }

    public static GateRelayCode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var chars = new StringBuilder();
        foreach (var c in text.AsSpan(start + Prefix.Length))
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            if (!char.IsAsciiLetterOrDigit(c)) break;
            chars.Append(c);
        }
        var raw = GateInvite.Base32Decode(chars.ToString());
        if (raw is null || raw.Length != 1 + GateInvite.KeyHashLength + SecretLength + ChecksumLength) return null;
        var body = raw.AsSpan(0, raw.Length - ChecksumLength);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(body), raw.AsSpan(raw.Length - ChecksumLength)) || body[0] != Version) return null;
        return new GateRelayCode(body.Slice(1, GateInvite.KeyHashLength).ToArray(), body[(1 + GateInvite.KeyHashLength)..].ToArray());
    }

    private static byte[] Checksum(ReadOnlySpan<byte> bytes) => SHA256.HashData([.. "jay-relay-code-v1"u8, .. bytes])[..ChecksumLength];

    public override string ToString() => L.T("core.gate.relay.code_hidden");
}
