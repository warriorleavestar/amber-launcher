using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>直连测试共用的钥匙：一张固定的邀请（大厅密钥 L + 令牌 K）和由 L 派生的桥钥匙。</summary>
internal static class DirectTestKeys
{
    public static readonly byte[] LobbyKey = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];
    public static readonly JayInvite Invite = new(LobbyKey, [.. Enumerable.Range(101, 16).Select(i => (byte)i)], TunnelSettings.Default);
    public static readonly byte[] Bridge = DirectKeys.Bridge(LobbyKey);
    public static readonly byte[] OtherBridge = DirectKeys.Bridge([.. Enumerable.Range(201, 16).Select(i => (byte)i)]);

    /// <summary>测试里的“敲门”：直接给出房间（不经大厅）。</summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<RoomCode?>> Knock(RoomCode room) => (_, _) => Task.FromResult<RoomCode?>(room);
}
