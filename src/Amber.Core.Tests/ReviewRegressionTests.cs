using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>PR #7 评审里确认的问题：每条一个回归测试。</summary>
public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-review-test-" + Guid.NewGuid().ToString("N")[..8]);

    public ReviewRegressionTests() => Directory.CreateDirectory(_game);

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

    /// <summary>1：实例被删了以后撤销删除世界，不能凭空建出 versions/A/saves（看不见的实例）。</summary>
    [Fact]
    public void RestoreRefusesWhenTheInstanceIsGone()
    {
        Write(@"versions\A\saves\World\level.dat");
        var entry = TrashService.MoveToTrash(Path.Combine(_game, "versions", "A", "saves", "World"), _game);
        Directory.Delete(Path.Combine(_game, "versions", "A"), true);

        var e = Assert.Throws<TrashException>(() => TrashService.Restore(entry));
        Assert.Equal(TrashFailure.ParentMissing, e.Reason);
        Assert.False(Directory.Exists(Path.Combine(_game, "versions", "A")));
        Assert.True(Directory.Exists(entry.StoredPath));
    }

    /// <summary>1（续）：只少了一层（saves 被删空）时照常恢复。</summary>
    [Fact]
    public void RestoreRecreatesOneMissingLevel()
    {
        Write(@"versions\A\saves\World\level.dat");
        var entry = TrashService.MoveToTrash(Path.Combine(_game, "versions", "A", "saves", "World"), _game);
        Directory.Delete(Path.Combine(_game, "versions", "A", "saves"), true);

        var restored = TrashService.Restore(entry);
        Assert.True(File.Exists(Path.Combine(restored, "level.dat")));
    }

    /// <summary>1（续）：恢复成功后刷新界面出错，不算恢复失败，也不让程序崩。</summary>
    [Fact]
    public void RefreshFailureAfterUndoDoesNotCrashOrStayPending() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        var undone = 0;
        Toasts.AddForTest("World", () => { undone++; return "World"; }, () => throw new NullReferenceException("refresh"));
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(1, undone);
        Assert.Equal(0, Toasts.PendingCount);
    });

    /// <summary>2：整个实例文件夹进回收目录，可以恢复。</summary>
    [Fact]
    public void InstanceFolderGoesToTrashAndComesBack()
    {
        Write(@"versions\A\A.json", "{}");
        Write(@"versions\A\saves\W\level.dat");
        var dir = Path.Combine(_game, "versions", "A");
        var entry = TrashService.MoveToTrash(dir, _game);
        Assert.False(Directory.Exists(dir));
        Assert.Equal(Path.Combine(_game, TrashService.TrashFolder), entry.TrashRoot);

        Assert.Equal(dir, TrashService.Restore(entry));
        Assert.True(File.Exists(Path.Combine(dir, "saves", "W", "level.dat")));
    }

    /// <summary>3：撤销失败（文件被占用）时记录留着，再试能成功；成功后才拿掉。</summary>
    [Fact]
    public void FailedUndoStaysInHistory() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        var attempts = 0;
        Toasts.AddForTest("mod.jar", () => ++attempts == 1 ? throw new IOException("in use") : "mod.jar", null);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(1, Toasts.PendingCount);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(2, attempts);
        Assert.Equal(0, Toasts.PendingCount);
    });

    /// <summary>3（续）：回收目录里已经没有这一项了，再试也没用，拿掉。</summary>
    [Fact]
    public void UndoOfMissingEntryIsDropped() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        Toasts.AddForTest("gone", () => throw new TrashException(TrashFailure.EntryMissing, "x"), null);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(0, Toasts.PendingCount);
    });

    /// <summary>4：同地址的两条服务器，删一条只删点的那条，撤销只放回那一条。</summary>
    [Fact]
    public void RemovingOneOfTwoServersWithTheSameAddressKeepsTheOther()
    {
        // 游戏自己写的 servers.dat 可以有同地址的多条（ServerList.Add 会去重，所以直接写文件）。
        Nbt.WriteFile(Path.Combine(_game, "servers.dat"), new Dictionary<string, object>
        {
            ["servers"] = new Nbt.NbtList(10,
            [
                new Dictionary<string, object> { ["name"] = "生存", ["ip"] = "mc.example" },
                new Dictionary<string, object> { ["name"] = "创造", ["ip"] = "mc.example", ["icon"] = "base64" },
                new Dictionary<string, object> { ["name"] = "别的", ["ip"] = "other.example" },
            ]),
        });

        var removed = ServerList.Remove(_game, "mc.example", "创造", 1);
        Assert.NotNull(removed);
        Assert.Equal(["生存", "别的"], ServerList.Read(_game).Select(s => s.Name));

        ServerList.Restore(_game, removed!);
        ServerList.Restore(_game, removed!);
        Assert.Equal(["生存", "创造", "别的"], ServerList.Read(_game).Select(s => s.Name));
    }

    /// <summary>6：一样的两条消息也是两条，界面能看出“变了”（关掉一条错误后同样的错误要能再出现）。</summary>
    [Fact]
    public void IdenticalStatusMessagesAreDistinct()
    {
        var a = StatusMessage.Error("common.install_failed");
        var b = StatusMessage.Error("common.install_failed");
        Assert.NotEqual(a, b);
        Assert.False(Equals(a, b));
        Assert.Equal(a, a);
    }

    /// <summary>9：错误原因在显示时才翻译，换语言后跟着换。</summary>
    [Fact]
    public void FailureReasonFollowsLanguage()
    {
        var before = L.Language;
        try
        {
            var message = StatusMessage.Failure("common.install_failed", new TimeoutException(), ErrorText.Of(new TimeoutException()));
            L.SetLanguage("en");
            var english = message.Format();
            L.SetLanguage("zh-Hans");
            var chinese = message.Format();
            Assert.Contains(L.T("error.timeout"), chinese);
            Assert.NotEqual(english, chinese);
        }
        finally
        {
            L.SetLanguage(before);
        }
    }

    /// <summary>8：拒绝访问的 IOException（0x80070005）给出“被占用或没有权限”。</summary>
    [Fact]
    public void AccessDeniedIoExceptionIsDescribed()
    {
        var e = new IOException("denied", unchecked((int)0x80070005));
        Assert.Equal("error.in_use_or_denied", ErrorText.DescribeKey(e));
    }

    /// <summary>12：Amber 自己的回收目录、校验缓存、未完成标记不出现在整合包可选内容里。</summary>
    [Fact]
    public void ModpackExportSkipsAmberInternals()
    {
        Directory.CreateDirectory(Path.Combine(_game, TrashService.TrashFolder));
        Directory.CreateDirectory(Path.Combine(_game, ".amber-hashes"));
        Write(GameDirectory.IncompleteMarker, "");
        Write(@"config\a.toml");
        var candidates = ModpackService.ExportCandidates(_game);
        Assert.Contains("config", candidates);
        Assert.DoesNotContain(TrashService.TrashFolder, candidates);
        Assert.DoesNotContain(".amber-hashes", candidates);
        Assert.DoesNotContain(GameDirectory.IncompleteMarker, candidates);
    }
}
