using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Game;

namespace Amber.Core.Tests;

public sealed class InputParsingSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-input-security-" + Guid.NewGuid().ToString("N"));

    public InputParsingSecurityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Archive(params string[] names)
    {
        var path = Path.Combine(_root, "import.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(new byte[] { 10, 0, 0, 0 });
        }
        return path;
    }

    [Theory]
    [InlineData("../outside/level.dat")]
    [InlineData("..\\outside/level.dat")]
    [InlineData("..\\..\\outside/level.dat")]
    [InlineData("/outside/level.dat")]
    [InlineData("C:/outside/level.dat")]
    [InlineData("C:\\outside\\level.dat")]
    [InlineData("\\\\server\\share\\level.dat")]
    [InlineData("world/../outside/level.dat")]
    [InlineData("world./level.dat")]
    [InlineData("world:stream/level.dat")]
    public void WorldImport_RejectsUnsafeRootsBeforeWriting(string name)
    {
        var saves = Path.Combine(_root, "instance", "saves");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(Archive(name), saves));
        Assert.False(Directory.Exists(Path.Combine(_root, "instance")));
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Theory]
    [InlineData("world/../escape.txt")]
    [InlineData("world\\..\\escape.txt")]
    [InlineData("world/data:ads")]
    public void WorldImport_PreflightsLaterEntries(string invalidEntry)
    {
        var saves = Path.Combine(_root, "saves");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(Archive("world/level.dat", invalidEntry), saves));
        Assert.False(Directory.Exists(saves));
    }

    [Theory]
    [InlineData("level.dat", "import")]
    [InlineData("nested/world/level.dat", "world")]
    [InlineData("nested\\world\\level.dat", "world")]
    [InlineData("nested/world\\level.dat", "world")]
    public void WorldImport_PreservesNormalRootsAndCollisionSuffix(string entry, string expected)
    {
        var saves = Path.Combine(_root, "saves");
        var zip = Archive(entry);
        Assert.Equal(expected, WorldManager.Import(zip, saves));
        Assert.Equal(expected + " (2)", WorldManager.Import(zip, saves));
        Assert.Equal(2, WorldManager.List(saves).Count);
    }

    private string NbtFile(byte[] bytes)
    {
        var path = Path.Combine(_root, "level.dat");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    [Theory]
    [InlineData(7, -1)]
    [InlineData(7, int.MaxValue)]
    [InlineData(11, -1)]
    [InlineData(11, int.MaxValue)]
    [InlineData(12, -1)]
    [InlineData(12, int.MaxValue)]
    public void Nbt_RejectsArrayLengthBeforeAllocation(byte type, int length)
    {
        var path = NbtFile([10, 0, 0, type, 0, 1, (byte)'x', .. Int32(length), 0]);
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(path));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Nbt_RejectsListLengthBeforeAllocation(int length)
    {
        var path = NbtFile([10, 0, 0, 9, 0, 1, (byte)'x', 10, .. Int32(length), 0]);
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void Nbt_TruncatedScalarsHaveControlledFormatErrors(byte type)
    {
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(NbtFile([10, 0, 0, type, 0, 0])));
    }

    [Fact]
    public void Nbt_BoundsNestedCompoundsAndLists()
    {
        using var compounds = new MemoryStream();
        compounds.Write(new byte[] { 10, 0, 0 });
        for (var i = 0; i <= Nbt.MaxDepth; i++) compounds.Write(new byte[] { 10, 0, 0 });
        compounds.Write(new byte[Nbt.MaxDepth + 2]);
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(NbtFile(compounds.ToArray())));

        using var lists = new MemoryStream();
        lists.Write(new byte[] { 10, 0, 0, 9, 0, 0 });
        for (var i = 0; i <= Nbt.MaxDepth; i++) lists.Write(new byte[] { 9, 0, 0, 0, 1 });
        lists.Write(new byte[] { 1, 0, 0, 0, 0, 0 });
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(NbtFile(lists.ToArray())));
    }

    [Fact]
    public void Nbt_BoundsAggregateTagsEvenWithShallowDuplicateNames()
    {
        using var bytes = new MemoryStream();
        bytes.Write(new byte[] { 10, 0, 0 });
        for (var i = 0; i <= Nbt.MaxTags; i++) bytes.Write(new byte[] { 10, 0, 0, 0 });
        bytes.WriteByte(0);
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(NbtFile(bytes.ToArray())));
    }

    [Fact]
    public void Nbt_BoundsRawAndDecompressedFiles()
    {
        var path = Path.Combine(_root, "large.dat");
        using (var file = File.Create(path)) file.SetLength((long)Nbt.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(path));
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            var zero = new byte[8192];
            for (var i = 0; i < Nbt.MaxFileBytes / zero.Length; i++) gzip.Write(zero);
            gzip.WriteByte(0);
        }
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(path));
    }

    [Fact]
    public void Nbt_KeepsUnknownFieldsAndModifiedUtf8WithinBudget()
    {
        var path = Path.Combine(_root, "normal.dat");
        Nbt.WriteFile(path, new Dictionary<string, object>
        {
            ["custom"] = new Dictionary<string, object> { ["text"] = "a\0b 😀", ["values"] = new long[] { -1, long.MaxValue } },
            ["empty"] = new Nbt.NbtList(0, [])
        }, gzip: true);
        var result = Nbt.ReadFile(path);
        var custom = Assert.IsType<Dictionary<string, object>>(result["custom"]);
        Assert.Equal("a\0b 😀", custom["text"]);
        Assert.Equal(new long[] { -1, long.MaxValue }, custom["values"]);
        Assert.Empty(Assert.IsType<Nbt.NbtList>(result["empty"]).Items);
    }

    private static byte[] VarInt(int value)
    {
        using var bytes = new MemoryStream();
        var remaining = (uint)value;
        do
        {
            var current = (byte)(remaining & 0x7f);
            remaining >>= 7;
            bytes.WriteByte(remaining == 0 ? current : (byte)(current | 0x80));
        } while (remaining != 0);
        return bytes.ToArray();
    }

    private static byte[] Frame(byte[] body) => [.. VarInt(body.Length), .. body];
    private static byte[] Status(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return Frame([0, .. VarInt(body.Length), .. body]);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(ServerList.MaxStatusPacketBytes + 1)]
    public async Task Status_RejectsOuterLengthWithoutReadingPayload(int length)
    {
        using var stream = new MemoryStream(VarInt(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(stream));
    }

    [Fact]
    public async Task Status_RejectsOverflowInnerLengthWrongTypeAndTrailingBytes()
    {
        byte[][] invalid =
        [
            [0x80, 0x80, 0x80, 0x80, 0x10],
            [0x80, 0x80, 0x80, 0x80, 0x80, 0],
            Frame([0, .. VarInt(int.MaxValue)]),
            Frame([0, .. VarInt(-1)]),
            Frame([1, 2, (byte)'{', (byte)'}']),
            Frame([0, 2, (byte)'{', (byte)'}', 0]),
            Status("[]")
        ];
        foreach (var bytes in invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => ServerList.ReadStatusPacketAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Status_HandlesNormalFrameAndTruncation()
    {
        var bytes = Status("{\"description\":\"hello\"}");
        var json = await ServerList.ReadStatusPacketAsync(new MemoryStream(bytes));
        Assert.Equal("hello", json["description"]!.GetValue<string>());
        await Assert.ThrowsAsync<EndOfStreamException>(() => ServerList.ReadStatusPacketAsync(new MemoryStream(bytes[..^1])));
    }

    [Fact]
    public async Task Status_WrongJsonFieldShapeReturnsOfflineInsteadOfThrowing()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(limit.Token);
            var stream = client.GetStream();
            await ReadClientFrame(stream, limit.Token);
            await ReadClientFrame(stream, limit.Token);
            await stream.WriteAsync(Status("{\"players\":{\"online\":[]}}"), limit.Token);
            var ping = await ReadClientFrame(stream, limit.Token);
            await stream.WriteAsync(Frame(ping), limit.Token);
        });
        var result = await ServerList.PingAsync($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", ct: limit.Token);
        await server;
        Assert.False(result.Online);
    }

    private static async Task<byte[]> ReadClientFrame(Stream stream, CancellationToken ct)
    {
        var length = 0;
        var one = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(one, ct);
            length |= (one[0] & 0x7f) << shift;
            if ((one[0] & 0x80) != 0) continue;
            Assert.InRange(length, 1, 8192);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, ct);
            return body;
        }
        throw new InvalidDataException();
    }
}
