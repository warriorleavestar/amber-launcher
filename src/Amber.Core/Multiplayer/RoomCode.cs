using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 联机房间码：AMB-网络名8位-密钥12位-房主端口，例如 AMB-K7Q2M9XD-4HZP8RW2N6TC-25565。
/// 字母数字用 Crockford Base32（不含 I L O U），手抄时把 I/L 看成 1、O 看成 0 也能认。
/// 网络名和密钥都只在房主电脑上随机生成，不经过任何 Amber 的服务器。
/// 房主选了封装模式时，AMB- 后面先写模式段：T模式号（1–3），伪装成 MC 再加 D，例如 AMB-T2D-K7Q2M9XD-4HZP8RW2N6TC-25565。
/// 放在这个位置是故意的：不认识封装模式的旧版本（v0.5.6 及以前）会直接说房间码格式不对，而不是悄悄按通用模式去连。
/// </summary>
public sealed record RoomCode(string Network, string Secret, int Port)
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int NetworkLength = 8;
    public const int SecretLength = 12;

    /// <summary>
    /// 邀请加入的房间：网络名（jay- + 16 位十六进制）和 256 位密钥都是房主随机生成、批准后在加密通道里发来的，直接就是 EasyTier 用的值，
    /// 只用来进 EasyTier 网络。游戏通道和聊天用每个成员自己的钥匙（Member / Members），不从房间密钥派生。
    /// 这种房间没有可以分享的码，ToString 不含任何值。
    /// </summary>
    public bool FromInvite { get; private init; }

    /// <summary>邀请房间里自己的成员钥匙（加入方是房主发来的；房主自己也有一把，给本机的聊天客户端用）。</summary>
    public MemberKey? Member { get; init; }

    /// <summary>邀请房间的网络是第几版：房主踢人后换网络名和密钥，版本加一。只接受比当前大的。</summary>
    public int Epoch { get; init; } = 1;

    /// <summary>换到房主发来的新网络（成员钥匙、成员表、封装方式都不变）。</summary>
    public RoomCode Rekeyed(RoomRekey next) => FromInvite && next.Epoch > Epoch
        ? this with { Network = next.Network, Secret = next.Secret, Epoch = next.Epoch }
        : throw new InvalidOperationException("stale or non-invite rekey");

    /// <summary>邀请房间的房主：成员表（编号 → psk），游戏通道和聊天按它认人。加入方是 null。</summary>
    public MemberTable? Members { get; init; }

    /// <summary>按房主发来（或自己生成）的凭据建房间对象。</summary>
    public static RoomCode FromCredentials(string network, string secret, int port, Transport.TunnelSettings tunnel, MemberKey? member = null, MemberTable? members = null, int epoch = 1)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new RoomCode(network, secret, port) { Tunnel = tunnel, FromInvite = true, Member = member, Members = members, Epoch = epoch };
    }

    /// <summary>EasyTier 的网络名。加前缀避免和别人在公共节点上手动起的网络撞名。</summary>
    public string NetworkName => FromInvite ? Network : "amber-" + Network.ToLowerInvariant();

    /// <summary>
    /// 给 EasyTier 用的网络名和密钥。封装模式的房间用按模式派生的另一套（HKDF），
    /// 这样房间码末尾的模式被删掉或改掉的人连 EasyTier 网络都进不来，碰不到房主没封装的端口。通用模式和以前一样。
    /// </summary>
    public string EasyTierNetworkName => FromInvite ? Network : Tunnel.Wraps
        ? "amber-" + Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(Secret), 8, Encoding.UTF8.GetBytes(NetworkName),
            Encoding.UTF8.GetBytes($"amber-et-name-v1|{(int)Tunnel.Mode}|{(Tunnel.Disguise ? 1 : 0)}")))
        : NetworkName;

    public string EasyTierSecret => FromInvite ? Secret : Tunnel.Wraps
        ? Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(Secret), 32, Encoding.UTF8.GetBytes(NetworkName),
            Encoding.UTF8.GetBytes($"amber-et-v1|{(int)Tunnel.Mode}|{(Tunnel.Disguise ? 1 : 0)}")))
        : Secret;

    /// <summary>要在日志里遮掉的密钥（房间密钥和派生出来的 EasyTier 密钥）。</summary>
    public IReadOnlyList<string> Secrets => FromInvite ? [Secret, Network] : Tunnel.Wraps ? [Secret, EasyTierSecret] : [Secret];

    /// <summary>联机和聊天的封装方式，房间里所有人一样。</summary>
    public Transport.TunnelSettings Tunnel { get; init; } = Transport.TunnelSettings.Default;

    public override string ToString() => FromInvite ? "JAY-ROOM" :
        Tunnel.Wraps ? $"AMB-T{(int)Tunnel.Mode}{(Tunnel.Disguise ? "D" : "")}-{Network}-{Secret}-{Port}" : $"AMB-{Network}-{Secret}-{Port}";

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
        var body = upper[(start + 4)..];
        var tunnel = Transport.TunnelSettings.Default;
        // 模式段：AMB- 后面第一段如果不是 8 位网络名，就必须是 T1–T3（可选 D），只认半角字符；别的写法一律算格式不对。
        var firstDash = body.IndexOf('-');
        if (firstDash is > 0 and < NetworkLength)
        {
            var marker = body[..firstDash].Trim();
            if (marker.Length is < 2 or > 3 || marker[0] != 'T' || marker[1] is < '1' or > '3' || marker.Length == 3 && marker[2] != 'D') return false;
            tunnel = new Transport.TunnelSettings((Transport.TransportMode)(marker[1] - '0'), marker.Length == 3);
            body = body[(firstDash + 1)..];
        }
        var parts = body.Split('-', 3);
        if (parts.Length != 3) return false;

        var network = Normalize(parts[0]);
        var secret = Normalize(parts[1]);
        var digits = new string(parts[2].TakeWhile(char.IsAsciiDigit).ToArray());
        if (network is not { Length: NetworkLength } || secret is not { Length: SecretLength }) return false;
        if (!int.TryParse(digits, out var port) || port is < 1 or > 65535) return false;
        // 端口后面紧跟字母数字（被改坏的码）、或者“-T数字”（旧的把模式写在末尾的写法）算格式不对；
        // 空格、中文、“-thanks”这类粘贴时带上的普通文字可以。
        var rest = parts[2][digits.Length..];
        if (rest.Length > 0 && char.IsAsciiLetterOrDigit(rest[0])) return false;
        if (rest.Length > 2 && rest[0] == '-' && rest[1] == 'T' && char.IsAsciiDigit(rest[2])) return false;
        room = new RoomCode(network, secret, port) { Tunnel = tunnel };
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
