using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Amber.Core.RemoteControl;

/// <summary>远程控制连接里的一帧是什么。</summary>
public enum RemoteFrameType : byte
{
    /// <summary>控制方 → 被控方：连接开始时报协议版本。</summary>
    Hello = 1,
    /// <summary>被控方 → 控制方：同意了（是否允许控制、画面尺寸、编码）。</summary>
    Accept = 2,
    /// <summary>被控方 → 控制方：不同意（原因）。</summary>
    Deny = 3,
    /// <summary>被控方 → 控制方：一批变化的画面块。</summary>
    Tiles = 4,
    /// <summary>被控方 → 控制方：鼠标指针位置。</summary>
    Cursor = 5,
    /// <summary>控制方 → 被控方：鼠标。</summary>
    MouseInput = 6,
    /// <summary>控制方 → 被控方：键盘（扫描码）。</summary>
    KeyInput = 7,
    /// <summary>被控方 → 控制方：允许控制了。</summary>
    GrantControl = 8,
    /// <summary>被控方 → 控制方：收回控制（画面不断）。</summary>
    RevokeControl = 9,
    /// <summary>任一方：结束。</summary>
    Stop = 10,
    Ping = 11,
    Pong = 12,
    /// <summary>控制方 → 被控方：这一批画面收到了（被控方据此控制发送节奏）。</summary>
    FrameAck = 13,
}

/// <summary>连接建立时（握手载荷里）说明自己是哪一方。连接总是成员发起的。</summary>
public enum RemoteRole : byte
{
    /// <summary>发起连接的人要看/控对方（成员看房主）。</summary>
    Controller = 1,
    /// <summary>发起连接的人是被控方（房主请求看成员，成员同意后连回去）。</summary>
    Controlled = 2,
}

public enum RemoteDenyReason : byte
{
    Declined = 0,
    TimedOut = 1,
    Busy = 2,
    Unsupported = 3,
}

public enum RemoteStopReason : byte
{
    Normal = 0,
    /// <summary>被控方按了停止或全局热键。</summary>
    StoppedByUser = 1,
    ProtocolError = 2,
}

public enum RemoteMouseAction : byte
{
    Move = 0,
    Down = 1,
    Up = 2,
    Wheel = 3,
    HWheel = 4,
}

public enum RemoteMouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

/// <summary>收到的一帧。</summary>
public readonly record struct RemoteFrame(RemoteFrameType Type, byte[] Payload);

/// <summary>一个画面块（坐标和尺寸是画面像素，Data 是编码后的数据）。</summary>
public sealed record RemoteTile(int X, int Y, int Width, int Height, byte[] Data);

/// <summary>一批画面块。</summary>
public sealed record RemoteTileBatch(uint Sequence, int FrameWidth, int FrameHeight, IReadOnlyList<RemoteTile> Tiles);

public readonly record struct RemoteMouse(RemoteMouseAction Action, RemoteMouseButton Button, int X, int Y, int Delta);

public readonly record struct RemoteKey(ushort ScanCode, bool Up, bool Extended);

public readonly record struct RemoteAccept(bool Control, int Width, int Height, byte Codec);

/// <summary>
/// 远程控制的线上格式。连接本身是成员钥匙的 Noise 通道（MemberChannel，用途 remote-v1），这里只管通道里面的帧：
/// [4 字节长度（类型 + 载荷）][1 字节类型][载荷]，整数都是大端。格式不对的帧抛 InvalidDataException，连接随后断开。
/// </summary>
public static class RemoteWire
{
    public const string Purpose = "remote-v1";
    public const byte Version = 1;
    public const int SessionIdLength = 16;

    /// <summary>一帧最大 4 MiB（一批画面块）。</summary>
    public const int MaxFrame = 4 * 1024 * 1024;

    /// <summary>画面最大边长（超过的按比例缩小后再发）。</summary>
    public const int MaxDimension = 4096;

    /// <summary>画面块边长。</summary>
    public const int TileSize = 64;

    /// <summary>一个编码后的块最多这么大（64×64 的原始像素也才 16 KiB）。</summary>
    public const int MaxTileData = 64 * 1024;

    // ---- 握手载荷 ----

    /// <summary>握手第一条消息的载荷：[版本][角色][16 字节会话号（控制方发起时全零）]。</summary>
    public static byte[] HelloPayload(RemoteRole role, ReadOnlySpan<byte> sessionId)
    {
        if (role == RemoteRole.Controlled && sessionId.Length != SessionIdLength) throw new ArgumentException(null, nameof(sessionId));
        var payload = new byte[2 + SessionIdLength];
        payload[0] = Version;
        payload[1] = (byte)role;
        if (role == RemoteRole.Controlled) sessionId.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static bool TryParseHelloPayload(ReadOnlySpan<byte> payload, out RemoteRole role, out byte[] sessionId)
    {
        role = default;
        sessionId = [];
        if (payload.Length != 2 + SessionIdLength || payload[0] != Version) return false;
        role = (RemoteRole)payload[1];
        if (role is not (RemoteRole.Controller or RemoteRole.Controlled)) return false;
        sessionId = payload[2..].ToArray();
        // 被控方连回来时必须带房主发出的会话号；控制方发起时是全零。
        return role == RemoteRole.Controller ? !sessionId.AsSpan().ContainsAnyExcept((byte)0) : sessionId.AsSpan().ContainsAnyExcept((byte)0);
    }

    public static byte[] NewSessionId() => RandomNumberGenerator.GetBytes(SessionIdLength);

    // ---- 帧 ----

    public static byte[] Encode(RemoteFrameType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length + 1 > MaxFrame) throw new InvalidDataException("remote frame too large");
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = (byte)type;
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>读一帧。对方正常关闭返回 null。</summary>
    public static async Task<RemoteFrame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxFrame) throw new InvalidDataException("remote frame length");
        var type = (RemoteFrameType)header[4];
        if (!Enum.IsDefined(type)) throw new InvalidDataException("remote frame type");
        var payload = new byte[length - 1];
        if (payload.Length > 0 && !await ReadExactAsync(stream, payload, ct)) throw new EndOfStreamException();
        return new RemoteFrame(type, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }

    // ---- 各种帧的载荷 ----

    public static byte[] Hello() => Encode(RemoteFrameType.Hello, [Version]);

    public static byte[] Accept(RemoteAccept a)
    {
        Span<byte> p = stackalloc byte[6];
        p[0] = a.Control ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(p[1..], (ushort)a.Width);
        BinaryPrimitives.WriteUInt16BigEndian(p[3..], (ushort)a.Height);
        p[5] = a.Codec;
        return Encode(RemoteFrameType.Accept, p);
    }

    public static RemoteAccept ParseAccept(byte[] p)
    {
        if (p.Length != 6 || p[0] > 1) throw new InvalidDataException("accept");
        var a = new RemoteAccept(p[0] == 1, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(3)), p[5]);
        CheckDimensions(a.Width, a.Height);
        return a;
    }

    public static byte[] Deny(RemoteDenyReason reason) => Encode(RemoteFrameType.Deny, [(byte)reason]);

    public static RemoteDenyReason ParseDeny(byte[] p) =>
        p.Length == 1 && Enum.IsDefined((RemoteDenyReason)p[0]) ? (RemoteDenyReason)p[0] : RemoteDenyReason.Declined;

    public static byte[] Stop(RemoteStopReason reason) => Encode(RemoteFrameType.Stop, [(byte)reason]);

    public static byte[] Tiles(RemoteTileBatch batch)
    {
        var size = 4 + 2 + 2 + 2 + batch.Tiles.Sum(t => 12 + t.Data.Length);
        var p = new byte[size];
        var s = p.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, batch.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)batch.FrameWidth);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)batch.FrameHeight);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)batch.Tiles.Count);
        var o = 10;
        foreach (var t in batch.Tiles)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[o..], (ushort)t.X);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 2)..], (ushort)t.Y);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 4)..], (ushort)t.Width);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 6)..], (ushort)t.Height);
            BinaryPrimitives.WriteInt32BigEndian(s[(o + 8)..], t.Data.Length);
            t.Data.CopyTo(s[(o + 12)..]);
            o += 12 + t.Data.Length;
        }
        return Encode(RemoteFrameType.Tiles, p);
    }

    /// <summary>解析一批画面块，并检查每块都在画面里、尺寸不超过一块。</summary>
    public static RemoteTileBatch ParseTiles(byte[] p)
    {
        if (p.Length < 10) throw new InvalidDataException("tiles");
        var s = p.AsSpan();
        var seq = BinaryPrimitives.ReadUInt32BigEndian(s);
        int w = BinaryPrimitives.ReadUInt16BigEndian(s[4..]), h = BinaryPrimitives.ReadUInt16BigEndian(s[6..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
        CheckDimensions(w, h);
        var tiles = new List<RemoteTile>(count);
        var o = 10;
        for (var i = 0; i < count; i++)
        {
            if (o + 12 > p.Length) throw new InvalidDataException("tiles");
            int x = BinaryPrimitives.ReadUInt16BigEndian(s[o..]), y = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 2)..]);
            int tw = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 4)..]), th = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 6)..]);
            var len = BinaryPrimitives.ReadInt32BigEndian(s[(o + 8)..]);
            o += 12;
            if (tw is < 1 or > TileSize || th is < 1 or > TileSize || x + tw > w || y + th > h || len is < 0 or > MaxTileData || o + len > p.Length)
                throw new InvalidDataException("tile");
            tiles.Add(new RemoteTile(x, y, tw, th, s.Slice(o, len).ToArray()));
            o += len;
        }
        if (o != p.Length) throw new InvalidDataException("tiles");
        return new RemoteTileBatch(seq, w, h, tiles);
    }

    public static byte[] Cursor(int x, int y, bool visible)
    {
        Span<byte> p = stackalloc byte[9];
        BinaryPrimitives.WriteInt32BigEndian(p, x);
        BinaryPrimitives.WriteInt32BigEndian(p[4..], y);
        p[8] = visible ? (byte)1 : (byte)0;
        return Encode(RemoteFrameType.Cursor, p);
    }

    public static (int X, int Y, bool Visible) ParseCursor(byte[] p) => p.Length == 9
        ? (BinaryPrimitives.ReadInt32BigEndian(p), BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4)), p[8] == 1)
        : throw new InvalidDataException("cursor");

    public static byte[] Mouse(RemoteMouse m)
    {
        Span<byte> p = stackalloc byte[12];
        p[0] = (byte)m.Action;
        p[1] = (byte)m.Button;
        BinaryPrimitives.WriteInt32BigEndian(p[2..], m.X);
        BinaryPrimitives.WriteInt32BigEndian(p[6..], m.Y);
        BinaryPrimitives.WriteInt16BigEndian(p[10..], (short)Math.Clamp(m.Delta, short.MinValue, short.MaxValue));
        return Encode(RemoteFrameType.MouseInput, p);
    }

    public static RemoteMouse ParseMouse(byte[] p)
    {
        if (p.Length != 12 || !Enum.IsDefined((RemoteMouseAction)p[0]) || !Enum.IsDefined((RemoteMouseButton)p[1])) throw new InvalidDataException("mouse");
        return new RemoteMouse((RemoteMouseAction)p[0], (RemoteMouseButton)p[1], BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(2)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(6)), BinaryPrimitives.ReadInt16BigEndian(p.AsSpan(10)));
    }

    public static byte[] Key(RemoteKey k)
    {
        Span<byte> p = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(p, k.ScanCode);
        p[2] = (byte)((k.Up ? 1 : 0) | (k.Extended ? 2 : 0));
        return Encode(RemoteFrameType.KeyInput, p);
    }

    public static RemoteKey ParseKey(byte[] p)
    {
        if (p.Length != 3 || p[2] > 3) throw new InvalidDataException("key");
        var scan = BinaryPrimitives.ReadUInt16BigEndian(p);
        if (scan is 0 or > 0xFF) throw new InvalidDataException("key");
        return new RemoteKey(scan, (p[2] & 1) != 0, (p[2] & 2) != 0);
    }

    public static byte[] Ping(long stamp)
    {
        Span<byte> p = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(p, stamp);
        return Encode(RemoteFrameType.Ping, p);
    }

    public static byte[] Pong(byte[] pingPayload) => Encode(RemoteFrameType.Pong, pingPayload.Length == 8 ? pingPayload : throw new InvalidDataException("ping"));

    public static long ParseStamp(byte[] p) => p.Length == 8 ? BinaryPrimitives.ReadInt64BigEndian(p) : throw new InvalidDataException("ping");

    public static byte[] FrameAck(uint sequence)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(p, sequence);
        return Encode(RemoteFrameType.FrameAck, p);
    }

    public static uint ParseFrameAck(byte[] p) => p.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(p) : throw new InvalidDataException("ack");

    private static void CheckDimensions(int w, int h)
    {
        if (w is < 1 or > MaxDimension || h is < 1 or > MaxDimension) throw new InvalidDataException("dimensions");
    }
}
