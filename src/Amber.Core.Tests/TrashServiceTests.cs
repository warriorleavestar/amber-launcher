using Amber.Core.Content;

namespace Amber.Core.Tests;

public sealed class TrashServiceTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-trash-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TrashServiceTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ModGoesIntoGameDirTrashAndCanBeRestored()
    {
        var mod = Write(@"versions\A\mods\sodium.jar", "jar-bytes");
        var entry = TrashService.MoveToTrash(mod, _game);

        Assert.False(File.Exists(mod));
        Assert.Equal(Path.Combine(_game, TrashService.TrashFolder), entry.TrashRoot);
        Assert.True(File.Exists(entry.StoredPath));
        Assert.Equal(9, entry.SizeBytes);
        Assert.Single(TrashService.List(entry.TrashRoot));

        var restored = TrashService.Restore(entry);
        Assert.Equal(mod, restored);
        Assert.Equal("jar-bytes", File.ReadAllText(mod));
        Assert.Empty(TrashService.List(entry.TrashRoot));
        Assert.False(Directory.Exists(entry.EntryDir));
    }

    [Fact]
    public void WorldFolderMovesAsAWholeAndRestores()
    {
        Write(@"saves\My World\level.dat", "nbt");
        Write(@"saves\My World\region\r.0.0.mca", "region");
        var world = Path.Combine(_game, "saves", "My World");

        var entry = TrashService.MoveToTrash(world, _game);
        Assert.True(entry.IsDirectory);
        Assert.False(Directory.Exists(world));
        Assert.Equal(9, entry.SizeBytes);

        TrashService.Restore(entry);
        Assert.Equal("region", File.ReadAllText(Path.Combine(world, "region", "r.0.0.mca")));
    }

    [Fact]
    public void RestoreDoesNotOverwriteANewFileWithTheSameName()
    {
        var mod = Write(@"mods\a.jar", "old");
        var entry = TrashService.MoveToTrash(mod, _game);
        Write(@"mods\a.jar", "new");

        var restored = TrashService.Restore(entry);
        Assert.Equal(Path.Combine(_game, "mods", "a (1).jar"), restored);
        Assert.Equal("new", File.ReadAllText(mod));
        Assert.Equal("old", File.ReadAllText(restored));
    }

    [Fact]
    public void TrashIsNeverInsideAScannedFolder()
    {
        var mod = Write(@"versions\A\mods\x.jar");
        var root = TrashService.TrashRootFor(mod, _game);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + "mods" + Path.DirectorySeparatorChar, root + Path.DirectorySeparatorChar);
        Assert.DoesNotContain(TrashService.ScannedFolders, s => root.Split(Path.DirectorySeparatorChar).Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingSourceThrowsWithReason()
    {
        var e = Assert.Throws<TrashException>(() => TrashService.MoveToTrash(Path.Combine(_game, "nope.jar"), _game));
        Assert.Equal(TrashFailure.SourceMissing, e.Reason);
    }

    [Fact]
    public void CannotTrashTheTrash()
    {
        var mod = Write(@"mods\a.jar");
        var entry = TrashService.MoveToTrash(mod, _game);
        var e = Assert.Throws<TrashException>(() => TrashService.MoveToTrash(entry.TrashRoot, _game));
        Assert.Equal(TrashFailure.IsTrashItself, e.Reason);
    }

    [Fact]
    public void TamperedEntryCannotRestoreOutsideItsOwner()
    {
        var mod = Write(@"mods\a.jar");
        var entry = TrashService.MoveToTrash(mod, _game);
        var outside = Path.Combine(Path.GetTempPath(), "amber-elsewhere-" + Guid.NewGuid().ToString("N")[..8], "a.jar");
        var forged = entry with { OriginalPath = outside };
        var e = Assert.Throws<TrashException>(() => TrashService.Restore(forged));
        Assert.Equal(TrashFailure.OriginalOutsideOwner, e.Reason);
        Assert.False(File.Exists(outside));
        Assert.True(File.Exists(entry.StoredPath));
    }

    [Fact]
    public void PurgeRemovesOnlyExpiredEntries()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var old = TrashService.MoveToTrash(Write(@"mods\old.jar"), _game, now.AddDays(-8));
        var fresh = TrashService.MoveToTrash(Write(@"mods\fresh.jar"), _game, now.AddDays(-1));
        // 回收目录里别人放的文件夹（没有 entry.json）不动。
        var stranger = Path.Combine(old.TrashRoot, "not-ours");
        Directory.CreateDirectory(stranger);

        Assert.Equal(1, TrashService.Purge(old.TrashRoot, TrashService.DefaultKeepDays, now));
        Assert.False(Directory.Exists(old.EntryDir));
        Assert.True(File.Exists(fresh.StoredPath));
        Assert.True(Directory.Exists(stranger));
    }

    [Fact]
    public void ListIsNewestFirstAndTotalSizeAddsUp()
    {
        var now = DateTimeOffset.Now;
        TrashService.MoveToTrash(Write(@"mods\a.jar", "12345"), _game, now.AddMinutes(-2));
        var b = TrashService.MoveToTrash(Write(@"mods\b.jar", "123"), _game, now);
        var list = TrashService.List(b.TrashRoot);
        Assert.Equal(["b.jar", "a.jar"], list.Select(e => e.Name));
        Assert.Equal(8, TrashService.TotalSize(b.TrashRoot));
    }

    [Fact]
    public void LockedFileStaysWhereItWas()
    {
        if (!OperatingSystem.IsWindows()) return;
        var mod = Write(@"mods\locked.jar");
        using (File.Open(mod, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => TrashService.MoveToTrash(mod, _game));
        }
        Assert.True(File.Exists(mod));
        // 失败时不留下空的条目。
        Assert.Empty(TrashService.List(Path.Combine(_game, TrashService.TrashFolder)));
        var root = Path.Combine(_game, TrashService.TrashFolder);
        Assert.True(!Directory.Exists(root) || !Directory.EnumerateDirectories(root).Any());
    }

    [Fact]
    public void IsUnderComparesWholeSegments()
    {
        Assert.True(TrashService.IsUnder(@"C:\Game", @"C:\Game\mods\a.jar"));
        Assert.False(TrashService.IsUnder(@"C:\Game", @"C:\Game2\a.jar"));
        Assert.False(TrashService.IsUnder(@"C:\Game", @"C:\Game"));
    }
}
