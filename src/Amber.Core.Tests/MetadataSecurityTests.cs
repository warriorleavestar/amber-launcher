using System.IO.Compression;
using System.Text;
using Amber.Core.Content;
using Amber.Core.Game;

namespace Amber.Core.Tests;

public sealed class MetadataSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-metadata-security-" + Guid.NewGuid().ToString("N"));

    public MetadataSecurityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Zip(string name, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName, CompressionLevel.SmallestSize).Open());
            writer.Write(text);
        }
        return path;
    }

    [Theory]
    [InlineData("fabric.mod.json")]
    [InlineData("quilt.mod.json")]
    [InlineData("META-INF/mods.toml")]
    [InlineData("META-INF/neoforge.mods.toml")]
    [InlineData("mcmod.info")]
    public void OversizedModMetadata_RemainsVisibleWithoutExpandedFields(string entry)
    {
        var path = Zip("mods/oversized.jar.disabled", (entry, new string(' ', ArchiveMetadata.MaxMetadataBytes + 1)));
        Assert.True(new FileInfo(path).Length < 16_384);
        var mod = Assert.Single(new ModManager(Path.GetDirectoryName(path)!).List());
        Assert.Equal("oversized.jar", mod.Name);
        Assert.False(mod.Enabled);
        Assert.Null(mod.ModId);
        Assert.Null(mod.Description);
        Assert.Null(mod.Icon);
    }

    [Fact]
    public void OversizedManifest_DoesNotMaterializeAnUnboundedLine()
    {
        var path = Zip("manifest.jar",
            ("fabric.mod.json", """{"id":"sample","name":"Sample"}"""),
            ("META-INF/MANIFEST.MF", "Implementation-Version: " + new string('x', ArchiveMetadata.MaxMetadataBytes)));
        var mod = ModManager.Read(path);
        Assert.Equal("manifest.jar", mod.Name);
        Assert.Null(mod.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedDatapackMetadata_FallsBackForZipAndFolder(bool folder)
    {
        var world = Path.Combine(_root, "world");
        var datapacks = DatapackManager.DirectoryOf(world);
        Directory.CreateDirectory(datapacks);
        var text = "{\"pack\":{\"description\":\"" + new string('x', ArchiveMetadata.MaxMetadataBytes) + "\"}}";
        if (folder)
        {
            var path = Path.Combine(datapacks, "folder");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "pack.mcmeta"), text);
        }
        else Zip("world/datapacks/pack.zip", ("pack.mcmeta", text), ("data/example.txt", ""));
        var pack = Assert.Single(DatapackManager.List(world));
        Assert.Null(pack.Description);
        Assert.Equal(folder, pack.IsFolder);
    }

    [Fact]
    public void OversizedPackIndex_IsRejectedBeforeImport()
    {
        var path = Zip("large.mrpack", ("modrinth.index.json", new string(' ', ArchiveMetadata.MaxPackMetadataBytes + 1)));
        Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(path));
    }

    [Fact]
    public void OversizedInstanceCfgLine_IsRejectedBeforeImport()
    {
        var path = Zip("large-cfg.zip", ("instance.cfg", "name=" + new string('a', ArchiveMetadata.MaxMetadataBytes) + "\nIntendedVersion=1.20.1\n"));
        Assert.Throws<InvalidDataException>(() => ModpackService.Inspect(path));
    }

    [Fact]
    public void ActualReadLimit_IsEnforcedWithoutTrustingStreamLength()
    {
        using var stream = new CountedStream(new byte[4096]);
        Assert.Throws<InvalidDataException>(() => ArchiveMetadata.ReadBytes(stream, 64));
        Assert.Equal(65, stream.BytesRead);
    }

    [Fact]
    public void AggregateBudget_IsSharedAndExactLimitIsAccepted()
    {
        var budget = new ArchiveMetadata.Budget(10);
        using var first = new MemoryStream(new byte[6]);
        Assert.Equal(6, ArchiveMetadata.ReadBytes(first, 64, budget).Length);
        using var second = new MemoryStream(new byte[4]);
        Assert.Equal(4, ArchiveMetadata.ReadBytes(second, 64, budget).Length);
        using var extra = new CountedStream(new byte[200]);
        Assert.Throws<InvalidDataException>(() => ArchiveMetadata.ReadBytes(extra, 64, budget));
        Assert.Equal(1, extra.BytesRead);
        Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void DeclaredEntryLimit_IsRejectedWithoutOpeningItsContent()
    {
        var path = Zip("declared.zip", ("metadata", "12345"));
        using var zip = ZipFile.OpenRead(path);
        Assert.Throws<InvalidDataException>(() => ArchiveMetadata.ReadBytes(zip.GetEntry("metadata")!, 4));
        var budget = new ArchiveMetadata.Budget(4);
        Assert.Throws<InvalidDataException>(() => ArchiveMetadata.ReadBytes(zip.GetEntry("metadata")!, 8, budget));
        Assert.Equal(4, budget.Remaining);
    }

    [Fact]
    public void BoundedText_PreservesBomUnicodeCommentsAndTrailingCommas()
    {
        var path = Path.Combine(_root, "unicode.jar");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(true)))
            writer.Write("""{/* 展示文字 */ "id":"unicode","name":"模组 日本語","version":"1",} """);
        var mod = ModManager.Read(path);
        Assert.Equal("模组 日本語", mod.Name);
        Assert.Contains("unicode", mod.Requirements!.Ids);

        var world = Path.Combine(_root, "normal-world");
        var pack = Path.Combine(DatapackManager.DirectoryOf(world), "folder");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.mcmeta"), """{"pack":{"description":[{"text":"你好","extra":[" §a世界"]}],},} """, Encoding.Unicode);
        Assert.Equal("你好 世界", Assert.Single(DatapackManager.List(world)).Description);
    }

    [Fact]
    public void NestedJarMetadata_UsesTheSameExpandedLimit()
    {
        var inner = Zip("inner.jar", ("fabric.mod.json", new string(' ', ArchiveMetadata.MaxMetadataBytes + 1)));
        var outer = Path.Combine(_root, "outer.jar");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(inner, "META-INF/jarjar/inner.jar");
        Assert.Null(ModCompat.Read(outer));
    }

    [Fact]
    public void TooManyNestedJars_RejectsTheCompatibilityResult()
    {
        var inner = Zip("valid-inner.jar", ("fabric.mod.json", """{"id":"inner","version":"1"}"""));
        var outer = Path.Combine(_root, "many.jar");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            for (var i = 0; i <= ArchiveMetadata.MaxNestedJars; i++)
                zip.CreateEntryFromFile(inner, $"META-INF/jarjar/{i}.jar");
        Assert.Null(ModCompat.Read(outer));
    }

    [Fact]
    public void OversizedIcon_IsIgnoredAndNormalModMetadataRemains()
    {
        var path = Zip("icon.jar",
            ("fabric.mod.json", """{"id":"sample","name":"Sample","version":"1","icon":"icon.png"}"""),
            ("icon.png", new string('x', 2_000_001)));
        var mod = ModManager.Read(path);
        Assert.Equal("Sample", mod.Name);
        Assert.Null(mod.Icon);
    }

    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
    }
}
