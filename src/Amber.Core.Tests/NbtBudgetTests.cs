using System.IO.Compression;
using System.Buffers.Binary;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>只构造小型 NBT，在分配和递归边界检查预算，不运行耗尽内存或栈的样本。</summary>
public sealed class NbtBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-nbt-budget-" + Guid.NewGuid().ToString("N"));

    public NbtBudgetTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void ExcessiveNestingIsRejected()
    {
        var path = Save(NestedCompounds(Nbt.MaxDepth + 2));
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(path));
    }

    private static readonly Nbt.ReadLimits Small = new(8192, 8192, 64, 8);

    [Theory]
    [InlineData("compounds")]
    [InlineData("lists")]
    [InlineData("mixed")]
    public void ContainerDepthRetainsTheExistingRootZeroBoundary(string shape)
    {
        Assert.NotNull(Parse(NestedContainers(Small.Depth + 1, shape), Small));
        Assert.Throws<InvalidDataException>(() => Parse(NestedContainers(Small.Depth + 2, shape), Small));
        Assert.NotNull(Nbt.ReadFile(Save(NestedContainers(Nbt.MaxDepth + 1, shape))));
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(Save(NestedContainers(Nbt.MaxDepth + 2, shape))));
    }

    public static IEnumerable<object[]> ArrayLengths()
    {
        foreach (var type in new byte[] { 7, 11, 12 })
            foreach (var length in new[] { -1, int.MinValue, int.MaxValue, 1 })
                yield return [type, length];
    }

    [Theory]
    [MemberData(nameof(ArrayLengths))]
    public void ArrayCountsRequireActualPayloadBeforeAllocation(byte type, int length)
    {
        // 不附根 End，确保声明为 1 的 byte 数组也确实没有载荷。
        byte[] bytes = [10, 0, 0, .. Tag(type, "x", I32(length))];
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(Save(bytes)));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    [InlineData((byte)7)]
    [InlineData((byte)8)]
    [InlineData((byte)9)]
    [InlineData((byte)10)]
    [InlineData((byte)11)]
    [InlineData((byte)12)]
    [InlineData((byte)255)]
    public void HugeOrUnknownPositiveListsFailBeforeCapacityAllocation(byte type)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Root(Tag(9, "x", [type, .. I32(int.MaxValue)])), Small));
    }

    public static IEnumerable<object[]> EmptyLists()
    {
        foreach (var type in new byte[] { 0, 8, 10 }) yield return [type, 0];
    }

    [Theory]
    [MemberData(nameof(EmptyLists))]
    public void EmptyKnownListsKeepTheirTypeAndWriteBackAsEmpty(byte type, int length)
    {
        var root = Nbt.ReadFile(Save(Root(Tag(9, "empty", [type, .. I32(length)]))));
        var list = Assert.IsType<Nbt.NbtList>(root["empty"]);
        Assert.Empty(list.Items);
        Assert.Equal(type, list.ElementType);
        var path = Path.Combine(_root, "normalized.dat");
        Nbt.WriteFile(path, root);
        var reread = Assert.IsType<Nbt.NbtList>(Nbt.ReadFile(path)["empty"]);
        Assert.Equal(type, reread.ElementType);
        Assert.Empty(reread.Items);
    }

    [Theory]
    [InlineData((byte)0, -1)]
    [InlineData((byte)8, -1)]
    [InlineData((byte)10, int.MinValue)]
    [InlineData((byte)255, 0)]
    public void NegativeAndUnknownEmptyListsRemainRejected(byte type, int length)
    {
        Assert.Throws<InvalidDataException>(() => Nbt.ReadFile(Save(Root(Tag(9, "x", [type, .. I32(length)])))));
    }

    [Fact]
    public void ScalarDepthStillCountsAfterTheRoot()
    {
        static byte[] WithScalar(int compounds)
        {
            using var bytes = new MemoryStream();
            for (var i = 0; i < compounds; i++) bytes.Write([10, 0, 0]);
            bytes.Write([1, 0, 0, 42]);
            bytes.Write(new byte[compounds]);
            return bytes.ToArray();
        }
        Assert.NotNull(Parse(WithScalar(Small.Depth), Small));
        Assert.Throws<InvalidDataException>(() => Parse(WithScalar(Small.Depth + 1), Small));
    }

    [Fact]
    public void AggregateValuesCoverSiblingListsAndDuplicateKeys()
    {
        var siblings = Root(Tag(9, "a", [1, .. I32(2), 1, 2]), Tag(9, "b", [1, .. I32(2), 3, 4]));
        Assert.Equal(2, Parse(siblings, Small with { Values = 7 }).Count);
        Assert.Throws<InvalidDataException>(() => Parse(siblings, Small with { Values = 6 }));
        var duplicate = Root(Tag(1, "x", [1]), Tag(1, "x", [2]), Tag(1, "x", [3]));
        Assert.Equal((byte)3, Parse(duplicate, Small with { Values = 4 })["x"]);
        Assert.Throws<InvalidDataException>(() => Parse(duplicate, Small with { Values = 3 }));
        var nested = Root(Tag(9, "lists", [9, .. I32(2), 1, .. I32(2), 1, 2, 1, .. I32(2), 3, 4]));
        Assert.Single(Parse(nested, Small with { Values = 8 }));
        Assert.Throws<InvalidDataException>(() => Parse(nested, Small with { Values = 7 }));
    }

    [Fact]
    public void PendingNestedListsCannotMultiplyReservedCapacity()
    {
        var limits = new Nbt.ReadLimits(65536, 65536, 8192, 32);
        Assert.Throws<InvalidDataException>(() => Parse(Root(Tag(9, "x", [0, .. I32(1)])), limits));
        using var payload = new MemoryStream();
        for (var i = 0; i < 16; i++) payload.Write([9, .. I32(4000)]);
        payload.Write([0, .. I32(1)]);
        payload.Write(new byte[25000]);
        var bytes = Root(Tag(9, "nested", payload.ToArray()));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => Parse(bytes, limits));
        // 样本不足 26 KiB；错误声明不应为每一层提前保留 4000 个对象引用。
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
    }

    [Fact]
    public void PrimitiveArraysUseBoundedBytesWithoutBoxingEveryElement()
    {
        var bytes = Root(Tag(7, "blob", [.. I32(1024), .. new byte[1024]]));
        Assert.Equal(1024, Assert.IsType<byte[]>(Parse(bytes, Small with { Values = 2 })["blob"]).Length);
        Assert.Throws<InvalidDataException>(() => Parse(bytes, Small with { DecodedBytes = 1024 }));
    }

    public static IEnumerable<object[]> TruncatedPayloads()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 10 }];
        yield return [new byte[] { 10, 0, 3, (byte)'a' }];
        yield return [new byte[] { 10, 0, 0 }];
        foreach (var type in new byte[] { 1, 2, 3, 4, 5, 6 })
            yield return [new byte[] { 10, 0, 0, type, 0, 0 }];
        yield return [Root(Tag(8, "s", [0, 4, (byte)'a']))];
        yield return [Root(Tag(9, "s", [8, .. I32(1), 0, 4, (byte)'a']))];
        yield return [Root(Tag(9, "s", [9, .. I32(2), 0]))];
        yield return [Root(Tag(13, "unknown", []))];
        yield return [new byte[] { 1, 0, 0, 1 }];
    }

    [Theory]
    [MemberData(nameof(TruncatedPayloads))]
    public void TruncationIsAControlledFormatError(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => Parse(bytes, Small));
    }

    [Fact]
    public void EncodedAndDecodedLimitsAreIndependentAndInclusive()
    {
        var plain = Root();
        Assert.Empty(Parse(plain, Small with { FileBytes = plain.Length, DecodedBytes = plain.Length }));
        Assert.Throws<InvalidDataException>(() => Parse([.. plain, 42], Small with { FileBytes = plain.Length }));
        Assert.Throws<InvalidDataException>(() => Parse([.. plain, 42], Small with { DecodedBytes = plain.Length }));
        var packed = Compress(plain);
        Assert.Empty(Parse(packed, Small with { FileBytes = packed.Length, DecodedBytes = plain.Length }));
        Assert.Throws<InvalidDataException>(() => Parse(packed, Small with { FileBytes = packed.Length - 1 }));
        Assert.Throws<InvalidDataException>(() => Parse(Compress([.. plain, 42]), Small with { DecodedBytes = plain.Length }));
    }

    [Fact]
    public void SmallCompressedExpansionStopsAtTheDecodedBudget()
    {
        var plain = Root(Tag(7, "blob", [.. I32(4096), .. new byte[4096]]));
        var compressed = Compress(plain);
        Assert.True(compressed.Length < 256);
        Assert.Throws<InvalidDataException>(() => Parse(compressed, Small with { DecodedBytes = 256 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingBudgetDoesNotDependOnLengthSnapshot(bool claimsSeekable)
    {
        using var stream = new ChunkedInput([.. Root(), .. new byte[64]], claimsSeekable);
        Assert.Throws<InvalidDataException>(() => Nbt.Read(stream, Small with { FileBytes = 16 }));
        Assert.Equal(17, stream.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllSupportedTypesAndUnknownFieldsRoundTrip(bool gzip)
    {
        var root = new Dictionary<string, object>
        {
            ["Byte"] = (byte)255, ["Short"] = (short)-7, ["Int"] = -123456, ["Long"] = long.MaxValue,
            ["Float"] = 1.25f, ["Double"] = -2.5d, ["Text"] = "中文\0😀𝄞",
            ["Bytes"] = new byte[] { 0, 255 }, ["Ints"] = new[] { int.MinValue, int.MaxValue },
            ["Longs"] = new[] { long.MinValue, long.MaxValue },
            ["unknown"] = new Dictionary<string, object> { ["preserved"] = "yes" },
            ["list"] = new Nbt.NbtList(8, ["vanilla", "file/世界.zip"])
        };
        var path = Path.Combine(_root, "roundtrip.dat");
        Nbt.WriteFile(path, root, gzip);
        var actual = Nbt.ReadFile(path);
        foreach (var key in new[] { "Byte", "Short", "Int", "Long", "Float", "Double", "Text" }) Assert.Equal(root[key], actual[key]);
        Assert.Equal((byte[])root["Bytes"], Assert.IsType<byte[]>(actual["Bytes"]));
        Assert.Equal((int[])root["Ints"], Assert.IsType<int[]>(actual["Ints"]));
        Assert.Equal((long[])root["Longs"], Assert.IsType<long[]>(actual["Longs"]));
        Assert.Equal("yes", Assert.IsType<Dictionary<string, object>>(actual["unknown"])["preserved"]);
        Assert.Equal(new object[] { "vanilla", "file/世界.zip" }, Assert.IsType<Nbt.NbtList>(actual["list"]).Items);
    }

    [Fact]
    public void RootNameAndTrailingBytesRemainCompatible()
    {
        byte[] bytes = [10, 0, 4, (byte)'r', (byte)'o', (byte)'o', (byte)'t', 0, 255, 42];
        Assert.Empty(Parse(bytes, Small));
    }

    [Fact]
    public void ImportedHostileWorldIsListedAsCorruptWithoutRunningIt()
    {
        var zipPath = Path.Combine(_root, "world.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("world/level.dat").Open()) output.Write(Compress(NestedCompounds(Nbt.MaxDepth + 2)));
        var saves = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saves);
        var folder = WorldManager.Import(zipPath, saves);
        var world = Assert.Single(WorldManager.List(saves));
        Assert.Equal(L.F("core.game.world.corruptlevel", folder), world.Name);
    }

    [Fact]
    public void RejectedServerAndDatapackEditsLeaveOriginalBytesUntouched()
    {
        var bytes = NestedCompounds(Nbt.MaxDepth + 2);
        var serverPath = Save(bytes, name: "servers.dat");
        Assert.Empty(ServerList.Read(_root));
        Assert.Throws<InvalidDataException>(() => ServerList.Add(_root, new ServerEntry("test", "example.invalid")));
        Assert.Throws<InvalidDataException>(() => ServerList.Remove(_root, "example.invalid"));
        Assert.Equal(bytes, File.ReadAllBytes(serverPath));
        Assert.False(File.Exists(serverPath + ".tmp"));
        var levelPath = Save(bytes, name: "level.dat");
        Assert.Throws<InvalidDataException>(() => DatapackManager.SetEnabled(_root, "pack.zip", true));
        Assert.Equal(bytes, File.ReadAllBytes(levelPath));
        Assert.False(File.Exists(levelPath + "_old"));
        Assert.False(File.Exists(levelPath + ".tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DatapackEditAtValueBudgetPreservesExistingRecoveryBackup(bool enabled)
    {
        var root = new Dictionary<string, object>
        {
            ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.DefaultReadLimits.Values - 6).ToList()),
            ["Data"] = new Dictionary<string, object>
            {
                ["DataPacks"] = new Dictionary<string, object>
                {
                    ["Enabled"] = new Nbt.NbtList(8, []),
                    ["Disabled"] = new Nbt.NbtList(8, [])
                }
            }
        };
        var path = Path.Combine(_root, "level.dat");
        Nbt.WriteFile(path, root, gzip: true);
        Assert.NotNull(Nbt.ReadFile(path));
        var original = File.ReadAllBytes(path);
        byte[] recovery = [7, 8, 9];
        File.WriteAllBytes(path + "_old", recovery);

        Assert.Throws<InvalidDataException>(() => DatapackManager.SetEnabled(_root, "pack.zip", enabled));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(recovery, File.ReadAllBytes(path + "_old"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SuccessfulDatapackEditBacksUpExactPreviousFile()
    {
        var path = Path.Combine(_root, "level.dat");
        Nbt.WriteFile(path, new Dictionary<string, object> { ["Data"] = new Dictionary<string, object>() }, gzip: true);
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path + "_old", [7, 8, 9]);

        DatapackManager.SetEnabled(_root, "pack.zip", false);

        Assert.Equal(original, File.ReadAllBytes(path + "_old"));
        var data = (Dictionary<string, object>)Nbt.ReadFile(path)["Data"];
        var packs = (Dictionary<string, object>)data["DataPacks"];
        Assert.Equal("file/pack.zip", Assert.Single(((Nbt.NbtList)packs["Disabled"]).Items));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AddingServerAtValueBudgetDoesNotReplaceAcceptedFile()
    {
        var root = new Dictionary<string, object>
        {
            ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.DefaultReadLimits.Values - 7).ToList()),
            ["servers"] = new Nbt.NbtList(10, [new Dictionary<string, object>
            {
                ["name"] = "old", ["ip"] = "old.invalid", ["hidden"] = (byte)0
            }])
        };
        var path = Path.Combine(_root, "servers.dat");
        Nbt.WriteFile(path, root);
        var original = File.ReadAllBytes(path);
        Assert.Single(ServerList.Read(_root));
        Assert.Throws<InvalidDataException>(() => ServerList.Add(_root, new ServerEntry("new", "new.invalid")));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(ServerList.Read(_root));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RejectedServerRestoreRetainsUndoStateAndCanBeRetried()
    {
        var path = Path.Combine(_root, "servers.dat");
        var removed = new RemovedServer(0, new Dictionary<string, object>
        {
            ["name"] = "old", ["ip"] = "old.invalid", ["icon"] = "kept"
        });
        Nbt.WriteFile(path, new Dictionary<string, object>
        {
            ["padding"] = new Nbt.NbtList(1, Enumerable.Repeat((object)(byte)0, Nbt.MaxTags - 2).ToList())
        });
        var original = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => ServerList.Restore(_root, removed));
        Assert.False(removed.Restored);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));

        Nbt.WriteFile(path, new Dictionary<string, object>());
        ServerList.Restore(_root, removed);
        ServerList.Restore(_root, removed);
        Assert.True(removed.Restored);
        var entry = Assert.Single(((Nbt.NbtList)Nbt.ReadFile(path)["servers"]).Items);
        Assert.Equal("kept", Assert.IsType<Dictionary<string, object>>(entry)["icon"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputByteBudgetsFailBeforeReplacingOriginal(bool gzip)
    {
        var path = Path.Combine(_root, "write.dat");
        var root = new Dictionary<string, object> { ["name"] = "old" };
        Nbt.WriteFile(path, root, gzip);
        var original = File.ReadAllBytes(path);
        var rawPath = Path.Combine(_root, "raw.dat");
        Nbt.WriteFile(rawPath, root);
        var rawLength = File.ReadAllBytes(rawPath).Length;
        root["name"] = "longer";
        Assert.Throws<InvalidDataException>(() => Nbt.WriteFile(path, root, gzip, Small with { DecodedBytes = rawLength }));
        Assert.Equal(original, File.ReadAllBytes(path));
        root["name"] = "old";
        Assert.Throws<InvalidDataException>(() => Nbt.WriteFile(path, root, gzip, Small with { FileBytes = original.Length - 1 }));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static Dictionary<string, object> Parse(byte[] bytes, Nbt.ReadLimits limits)
    {
        using var input = new MemoryStream(bytes);
        return Nbt.Read(input, limits);
    }

    private static byte[] I32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Tag(byte type, string name, byte[] payload)
    {
        var encoded = Nbt.EncodeModifiedUtf8(name);
        return [type, (byte)(encoded.Length >> 8), (byte)encoded.Length, .. encoded, .. payload];
    }

    private static byte[] Root(params byte[][] tags) => [10, 0, 0, .. tags.SelectMany(t => t), 0];

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true)) zip.Write(bytes);
        return output.ToArray();
    }

    private static byte[] NestedContainers(int depth, string shape)
    {
        byte type = depth > 1 && shape == "lists" ? (byte)9 : (byte)10;
        byte[] payload = type == 9 ? [0, 0, 0, 0, 0] : [0];
        for (var level = depth - 1; level >= 1; level--)
        {
            var list = level > 1 && (shape == "lists" || shape == "mixed" && level % 2 == 0);
            payload = list ? [type, .. I32(1), .. payload] : [type, 0, 0, .. payload, 0];
            type = list ? (byte)9 : (byte)10;
        }
        return [type, 0, 0, .. payload];
    }

    private sealed class ChunkedInput(byte[] bytes, bool claimsSeekable) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => claimsSeekable;
        public override bool CanWrite => false;
        // 模拟读取期间增长的文件：初始长度快照不能代替逐次读取预算。
        public override long Length => claimsSeekable ? 0 : throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, 3), bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private string Save(byte[] bytes, bool gzip = false, string name = "input.dat")
    {
        var path = Path.Combine(_root, name);
        if (gzip)
        {
            using var file = File.Create(path);
            using var compressed = new GZipStream(file, CompressionLevel.Optimal);
            compressed.Write(bytes);
        }
        else File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] NestedCompounds(int depth)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < depth; i++) bytes.Write(new byte[] { 10, 0, 0 });
        bytes.Write(new byte[depth]);
        return bytes.ToArray();
    }
}
