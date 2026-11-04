using Amber.Core.Localization;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Amber.Core.Game;

/// <summary>够读写 servers.dat 和 level.dat 的最小 NBT 实现。复合标签用字典，列表用 NbtList。</summary>
public static class Nbt
{
    private const byte End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6, ByteArray = 7, String = 8, List = 9, Compound = 10, IntArray = 11, LongArray = 12;

    public sealed class NbtList(byte elementType, List<object> items)
    {
        public byte ElementType { get; } = elementType;
        public List<object> Items { get; } = items;
    }

    /// <summary>读文件，自动识别 gzip。根标签的名字被忽略。</summary>
    public static Dictionary<string, object> ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gz.CopyTo(plain);
            bytes = plain.ToArray();
        }
        var reader = new Reader(bytes);
        var type = reader.U8();
        if (type != Compound) throw new InvalidDataException(L.T("core.game.nbt.invalidroot"));
        reader.Str();
        return reader.ReadCompound();
    }

    /// <summary>写 NBT。servers.dat 不压缩，level.dat 要 gzip。</summary>
    public static void WriteFile(string path, Dictionary<string, object> root, bool gzip = false)
    {
        using var memory = new MemoryStream();
        var writer = new Writer(memory);
        writer.U8(Compound);
        writer.Str("");
        writer.WriteCompound(root);
        var bytes = memory.ToArray();
        if (gzip)
        {
            using var packed = new MemoryStream();
            using (var gz = new GZipStream(packed, CompressionLevel.Optimal, true)) gz.Write(bytes);
            bytes = packed.ToArray();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }

    /// <summary>Java 的“改良 UTF-8”：U+0000 写成两字节，增补字符拆成两个代理各写三字节。</summary>
    internal static byte[] EncodeModifiedUtf8(string s)
    {
        var result = new List<byte>(s.Length);
        foreach (var c in s)
        {
            if (c is >= (char)0x01 and <= (char)0x7f)
            {
                result.Add((byte)c);
            }
            else if (c <= (char)0x7ff)
            {
                result.Add((byte)(0xc0 | (c >> 6)));
                result.Add((byte)(0x80 | (c & 0x3f)));
            }
            else
            {
                result.Add((byte)(0xe0 | (c >> 12)));
                result.Add((byte)(0x80 | ((c >> 6) & 0x3f)));
                result.Add((byte)(0x80 | (c & 0x3f)));
            }
        }
        return result.ToArray();
    }

    internal static string DecodeModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                builder.Append((char)b);
                i++;
            }
            else if ((b & 0xe0) == 0xc0 && i + 1 < bytes.Length)
            {
                builder.Append((char)(((b & 0x1f) << 6) | (bytes[i + 1] & 0x3f)));
                i += 2;
            }
            else if ((b & 0xf0) == 0xe0 && i + 2 < bytes.Length)
            {
                builder.Append((char)(((b & 0x0f) << 12) | ((bytes[i + 1] & 0x3f) << 6) | (bytes[i + 2] & 0x3f)));
                i += 3;
            }
            else if ((b & 0xf8) == 0xf0 && i + 3 < bytes.Length)
            {
                // 不是 Java 写的，而是标准 UTF-8 的四字节字符，也认。
                var cp = ((b & 0x07) << 18) | ((bytes[i + 1] & 0x3f) << 12) | ((bytes[i + 2] & 0x3f) << 6) | (bytes[i + 3] & 0x3f);
                builder.Append(cp is <= 0x10ffff and not (>= 0xd800 and <= 0xdfff) ? char.ConvertFromUtf32(cp) : ((char)0xfffd).ToString());
                i += 4;
            }
            else
            {
                builder.Append((char)0xfffd);
                i++;
            }
        }
        return builder.ToString();
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public byte U8() => data[_pos++];

        public short I16()
        {
            var v = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        public int I32()
        {
            var v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        public long I64()
        {
            var v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(_pos));
            _pos += 8;
            return v;
        }

        public string Str()
        {
            var length = (ushort)I16();
            var s = DecodeModifiedUtf8(data.AsSpan(_pos, length));
            _pos += length;
            return s;
        }

        public Dictionary<string, object> ReadCompound()
        {
            var result = new Dictionary<string, object>();
            while (true)
            {
                var type = U8();
                if (type == End) return result;
                var name = Str();
                result[name] = ReadValue(type);
            }
        }

        private object ReadValue(byte type)
        {
            switch (type)
            {
                case Byte: return U8();
                case Short: return I16();
                case Int: return I32();
                case Long: return I64();
                case Float:
                {
                    var v = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(_pos));
                    _pos += 4;
                    return v;
                }
                case Double:
                {
                    var v = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(_pos));
                    _pos += 8;
                    return v;
                }
                case ByteArray:
                {
                    var length = I32();
                    var v = data.AsSpan(_pos, length).ToArray();
                    _pos += length;
                    return v;
                }
                case String: return Str();
                case List:
                {
                    var element = U8();
                    var length = I32();
                    var items = new List<object>(Math.Max(length, 0));
                    for (var i = 0; i < length; i++) items.Add(ReadValue(element));
                    return new NbtList(element, items);
                }
                case Compound: return ReadCompound();
                case IntArray:
                {
                    var length = I32();
                    var v = new int[length];
                    for (var i = 0; i < length; i++) v[i] = I32();
                    return v;
                }
                case LongArray:
                {
                    var length = I32();
                    var v = new long[length];
                    for (var i = 0; i < length; i++) v[i] = I64();
                    return v;
                }
                default:
                    throw new InvalidDataException(L.F("core.game.nbt.unknowntype", type));
            }
        }
    }

    private sealed class Writer(Stream stream)
    {
        public void U8(byte v) => stream.WriteByte(v);

        private void I16(short v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, v);
            stream.Write(b);
        }

        private void I32(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, v);
            stream.Write(b);
        }

        private void I64(long v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, v);
            stream.Write(b);
        }

        public void Str(string s)
        {
            var bytes = EncodeModifiedUtf8(s);
            if (bytes.Length > ushort.MaxValue) throw new InvalidDataException(L.T("core.game.nbt.stringtoolong"));
            I16((short)bytes.Length);
            stream.Write(bytes);
        }

        public void WriteCompound(Dictionary<string, object> compound)
        {
            foreach (var (name, value) in compound)
            {
                U8(TypeOf(value));
                Str(name);
                WriteValue(value);
            }
            U8(End);
        }

        private static byte TypeOf(object value) => value switch
        {
            byte or bool => Byte,
            short => Short,
            int => Int,
            long => Long,
            float => Float,
            double => Double,
            byte[] => ByteArray,
            string => String,
            NbtList => List,
            Dictionary<string, object> => Compound,
            int[] => IntArray,
            long[] => LongArray,
            _ => throw new InvalidDataException(L.F("core.game.nbt.unsupportedtype", value.GetType())),
        };

        private void WriteValue(object value)
        {
            switch (value)
            {
                case byte b: U8(b); break;
                case bool b: U8(b ? (byte)1 : (byte)0); break;
                case short s: I16(s); break;
                case int i: I32(i); break;
                case long l: I64(l); break;
                case float f:
                {
                    Span<byte> buffer = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(buffer, f);
                    stream.Write(buffer);
                    break;
                }
                case double d:
                {
                    Span<byte> buffer = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(buffer, d);
                    stream.Write(buffer);
                    break;
                }
                case byte[] array:
                    I32(array.Length);
                    stream.Write(array);
                    break;
                case string s: Str(s); break;
                case NbtList list:
                    U8(list.ElementType);
                    I32(list.Items.Count);
                    foreach (var item in list.Items) WriteValue(item);
                    break;
                case Dictionary<string, object> compound: WriteCompound(compound); break;
                case int[] array:
                    I32(array.Length);
                    foreach (var v in array) I32(v);
                    break;
                case long[] array:
                    I32(array.Length);
                    foreach (var v in array) I64(v);
                    break;
            }
        }
    }
}
