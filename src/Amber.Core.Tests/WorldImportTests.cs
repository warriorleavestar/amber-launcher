using System.IO.Compression;
using Amber.Core.Game;

namespace Amber.Core.Tests;

/// <summary>世界 ZIP 的根和条目都必须留在 saves 的新子目录里。</summary>
public sealed class WorldImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-world-import-" + Guid.NewGuid().ToString("N"));
    private string Saves => Path.Combine(_root, "game", "saves");

    public WorldImportTests()
    {
        Directory.CreateDirectory(Saves);
        File.WriteAllText(Path.Combine(_root, "sentinel.txt"), "keep");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Archive(string filename, params string[] entries)
    {
        var path = Path.Combine(_root, filename);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entries)
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(name);
        return path;
    }

    [Fact]
    public void BackslashTraversalCannotChooseExtractionRoot()
    {
        var zip = Archive("attack.zip", @"..\..\escaped/level.dat", @"..\..\escaped/payload.txt");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.False(Directory.Exists(Path.Combine(_root, "escaped")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "sentinel.txt")));
    }

    [Fact]
    public void NestedWorldAndCollisionRemainSupported()
    {
        var zip = Archive("normal.zip", "wrapper/世界/level.dat", "wrapper/世界/region/r.0.0.mca", "wrapper/readme.txt");
        Assert.Equal("世界", WorldManager.Import(zip, Saves));
        Assert.Equal("世界 (2)", WorldManager.Import(zip, Saves));
        foreach (var folder in new[] { "世界", "世界 (2)" })
        {
            Assert.Equal("wrapper/世界/level.dat", File.ReadAllText(Path.Combine(Saves, folder, "level.dat")));
            Assert.True(File.Exists(Path.Combine(Saves, folder, "region", "r.0.0.mca")));
            Assert.False(File.Exists(Path.Combine(Saves, folder, "readme.txt")));
        }
    }

    [Theory]
    [InlineData("../outside/")]
    [InlineData(@"wrapper\..\outside/")]
    [InlineData("wrapper/../outside/")]
    [InlineData(@"\outside/")]
    [InlineData("/outside/")]
    [InlineData(@"C:\outside/")]
    [InlineData("C:outside/")]
    [InlineData(@"\\server\share\world/")]
    [InlineData(@"\\?\C:\outside/")]
    [InlineData(".. /")]
    [InlineData("world./")]
    [InlineData("world /")]
    [InlineData("world:stream/")]
    [InlineData("NUL/")]
    [InlineData("aux.txt/")]
    [InlineData("COM¹/")]
    [InlineData("bad?name/")]
    [InlineData("bad\0name/")]
    public void UnsafeSelectedPrefixesAreRejectedBeforeWriting(string prefix)
    {
        var zip = Archive("attack.zip", prefix + "level.dat", prefix + "payload.txt");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "sentinel.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData("C:outside.txt")]
    [InlineData("region/.. /outside.txt")]
    [InlineData("region/NUL")]
    [InlineData("region/file:stream")]
    [InlineData("region/file.")]
    [InlineData("region/file ")]
    [InlineData("region/file?.dat")]
    public void UnsafeLaterEntryLeavesNoPartialWorld(string relative)
    {
        var zip = Archive("attack.zip", "world/level.dat", "world/region/ok.mca", "world/" + relative);
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "sentinel.txt")));
    }

    [Fact]
    public void AbsoluteRootWithinSandboxIsStillRejected()
    {
        var outside = Path.Combine(_root, "outside");
        var zip = Archive("absolute.zip", outside + "/level.dat");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.False(Directory.Exists(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
    }

    [Theory]
    [InlineData("world\\level.dat", "world\\region\\r.0.0.mca", "world\\")]
    [InlineData("wrapper\\world/level.dat", "wrapper/world\\region/r.0.0.mca", "wrapper/world/region\\")]
    public void BackslashAndMixedLayoutsRemainSupported(string level, string region, string directory)
    {
        var zip = Archive("mixed.zip", level, region, directory);
        Assert.Equal("world", WorldManager.Import(zip, Saves));
        Assert.Equal(level, File.ReadAllText(Path.Combine(Saves, "world", "level.dat")));
        Assert.Equal(region, File.ReadAllText(Path.Combine(Saves, "world", "region", "r.0.0.mca")));
    }

    [Fact]
    public void RootLevelWorldUsesArchiveNameAndSkipsFileCollision()
    {
        var zip = Archive("我的 世界.zip", "level.dat", "region/r.0.0.mca");
        File.WriteAllText(Path.Combine(Saves, "我的 世界"), "existing file");
        Assert.Equal("我的 世界 (2)", WorldManager.Import(zip, Saves + Path.DirectorySeparatorChar));
        Assert.Equal("existing file", File.ReadAllText(Path.Combine(Saves, "我的 世界")));
        Assert.True(File.Exists(Path.Combine(Saves, "我的 世界 (2)", "region", "r.0.0.mca")));
    }

    [Fact]
    public void InvalidArchiveFallbackNameIsRejected()
    {
        var zip = Archive("...zip", "level.dat");
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
    }

    [Fact]
    public void ShortestWorldAndPrefixBoundaryStayUnchanged()
    {
        var zip = Archive("multiple.zip", "longer/world/level.dat", "w/level.dat", "world/ignored.txt", "w/region/r.mca", "w/region/r.mca");
        Assert.Equal("w", WorldManager.Import(zip, Saves));
        Assert.Equal("w/region/r.mca", File.ReadAllText(Path.Combine(Saves, "w", "region", "r.mca")));
        Assert.False(File.Exists(Path.Combine(Saves, "w", "ignored.txt")));
        Assert.Single(Directory.EnumerateDirectories(Saves));
    }

    [Fact]
    public void SymlinkMetadataCannotRedirectExtraction()
    {
        var zipPath = Archive("link.zip", "world/level.dat");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            var link = zip.CreateEntry("world/link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using (var writer = new StreamWriter(link.Open())) writer.Write(Path.Combine(_root, "outside"));
        }
        Assert.Equal("world", WorldManager.Import(zipPath, Saves));
        var extracted = Path.Combine(Saves, "world", "link");
        Assert.True(File.Exists(extracted));
        Assert.Null(new FileInfo(extracted).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Theory]
    [InlineData("other/NUL")]
    [InlineData("other/region/../escape")]
    [InlineData("other//payload")]
    [InlineData("other/./payload")]
    public void UnselectedUnsafeEntriesStillFailBeforeAnyWorldIsWritten(string name)
    {
        var zip = Archive("unselected.zip", "world/level.dat", name);
        Assert.Throws<InvalidDataException>(() => WorldManager.Import(zip, Saves));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Saves));
    }

    [Fact]
    public void DanglingDirectoryLinkRemainsOccupiedOnUnix()
    {
        // Windows 创建符号链接需要额外权限；Unix CI 在不提升权限的临时目录中覆盖此状态。
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_root, "missing-outside");
        var link = Path.Combine(Saves, "world");
        Directory.CreateSymbolicLink(link, outside);
        var zip = Archive("dangling.zip", "world/level.dat", "world/region/r.mca");

        Assert.Equal("world (2)", WorldManager.Import(zip, Saves));

        Assert.Equal(outside, new DirectoryInfo(link).LinkTarget);
        Assert.False(Directory.Exists(outside));
        Assert.True(File.Exists(Path.Combine(Saves, "world (2)", "level.dat")));
    }
}
