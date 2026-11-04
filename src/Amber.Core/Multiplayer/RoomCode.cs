using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 联机房间码：AMB-网络名8位-密钥12位-房主端口，例如 AMB-K7Q2M9XD-4HZP8RW2N6TC-25565。
/// 字母数字用 Crockford Base32（不含 I L O U），手抄时把 I/L 看成 1、O 看成 0 也能认。
/// 网络名和密钥都只在房主电脑上随机生成，不经过任何 Amber 的服务器。
/// </summary>
public sealed record RoomCode(string Network, string Secret, int Port)
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int NetworkLength = 8;
    public const int SecretLength = 12;

    /// <summary>EasyTier 的网络名。加前缀避免和别人在公共节点上手动起的网络撞名。</summary>
    public string NetworkName => "amber-" + Network.ToLowerInvariant();

    public override string ToString() => $"AMB-{Network}-{Secret}-{Port}";

    public static RoomCode Create(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new RoomCode(Random(NetworkLength), Random(SecretLength), port);
    }

    private static string Random(int length)
    {
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++) builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return builder.ToString();
    }

    /// <summary>容忍大小写、空格、前后多余的文字（比如聊天软件里复制来的整句话）。</summary>
    public static bool TryParse(string? text, out RoomCode room)
    {
        room = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var upper = text.ToUpperInvariant();
        var start = upper.IndexOf("AMB-", StringComparison.Ordinal);
        if (start < 0) return false;
        var parts = upper[(start + 4)..].Split('-', 3);
        if (parts.Length != 3) return false;

        var network = Normalize(parts[0]);
        var secret = Normalize(parts[1]);
        var digits = new string(parts[2].TakeWhile(char.IsAsciiDigit).ToArray());
        if (network is not { Length: NetworkLength } || secret is not { Length: SecretLength }) return false;
        if (!int.TryParse(digits, out var port) || port is < 1 or > 65535) return false;
        room = new RoomCode(network, secret, port);
        return true;
    }

    private static string? Normalize(string part)
    {
        var builder = new StringBuilder(part.Length);
        foreach (var raw in part.Trim())
        {
            var c = raw switch { 'I' or 'L' => '1', 'O' => '0', _ => raw };
            if (Alphabet.IndexOf(c) < 0) return null;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
