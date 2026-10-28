using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Auth;

/// <summary>启动游戏需要的身份信息。</summary>
public sealed record GameSession(string PlayerName, Guid Uuid, string AccessToken, string UserType, string? Xuid = null, string ClientId = "");

public static class OfflineAccount
{
    /// <summary>
    /// 离线账号：UUID 按 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + 名字) 生成，
    /// 与服务器离线模式下计算的 UUID 一致，存档和权限不会错乱。
    /// </summary>
    public static GameSession Create(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) throw new ArgumentException("玩家名不能为空", nameof(playerName));
        return new GameSession(playerName, OfflineUuid(playerName), Guid.NewGuid().ToString("N"), "legacy");
    }

    public static Guid OfflineUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // 版本 3
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // IETF 变体
        // Guid(byte[]) 按小端解释前三段，这里直接按大端拼字符串避免字节序问题。
        return Guid.Parse(Convert.ToHexString(hash));
    }
}
