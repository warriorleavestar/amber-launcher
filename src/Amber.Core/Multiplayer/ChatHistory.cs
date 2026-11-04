using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 联机聊天记录的临时保管：只在内存里，按房间分开，结束联机后再加入同一个房间码还能看到，关掉 Amber 就没了。
/// 键是房间码的 SHA-256 摘要，不用房间码本身；最多记最近几个房间（最久没用的先丢），每个房间最多若干条。
/// </summary>
public sealed class ChatHistory<T>(int maxRooms = 5, int maxPerRoom = 500)
{
    private readonly Dictionary<string, List<T>> _rooms = [];
    private readonly LinkedList<string> _order = new();

    public int RoomCount => _rooms.Count;

    internal static string KeyOf(RoomCode room) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(room.NetworkName + "\n" + room.Secret)));

    public void Save(RoomCode room, IEnumerable<T> lines)
    {
        var key = KeyOf(room);
        _order.Remove(key);
        var list = lines.TakeLast(maxPerRoom).ToList();
        if (list.Count == 0)
        {
            _rooms.Remove(key);
            return;
        }
        _rooms[key] = list;
        _order.AddLast(key);
        while (_order.Count > maxRooms)
        {
            _rooms.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
    }

    /// <summary>取出这个房间的记录（没有就是空）。取过的房间算“最近用过”。</summary>
    public IReadOnlyList<T> Restore(RoomCode room)
    {
        var key = KeyOf(room);
        if (!_rooms.TryGetValue(key, out var list)) return [];
        _order.Remove(key);
        _order.AddLast(key);
        return list;
    }

    public void Clear()
    {
        _rooms.Clear();
        _order.Clear();
    }
}
