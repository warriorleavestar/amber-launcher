using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>PR #7 第二轮评审（a–g）：每条一个回归测试。</summary>
public sealed class FollowUpTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-followup-test-" + Guid.NewGuid().ToString("N")[..8]);

    public FollowUpTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_game, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- a：撤销记录 ----

    /// <summary>a：原来的实例不在了（ParentMissing）时记录留着，实例装回来以后还能撤销。</summary>
    [Fact]
    public void ParentMissingStaysInHistory() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        Toasts.AddForTest("mod.jar", () => throw new TrashException(TrashFailure.ParentMissing, "x"), null);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(1, Toasts.PendingCount);
        Toasts.ClearHistory();
    });

    /// <summary>a：最近一条撤销失败了，下一次 Ctrl+Z 撤销更早的，不被它挡住；都试过后再回来试最近的。</summary>
    [Fact]
    public void FailingNewestDoesNotBlockOlder() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        var older = 0;
        var newer = 0;
        Toasts.AddForTest("older", () => { older++; return "older"; }, null);
        Toasts.AddForTest("newer", () => { newer++; throw new IOException("in use"); }, null);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal((0, 1), (older, newer));
        Assert.True(Toasts.UndoLatest());
        Assert.Equal((1, 1), (older, newer));
        Assert.Equal(1, Toasts.PendingCount);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(2, newer);
        Toasts.ClearHistory();
    });

    /// <summary>a：撤销时抛了没料到的异常，Running 也要放开，之后还能再试。</summary>
    [Fact]
    public void UnexpectedExceptionResetsRunning() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        var attempts = 0;
        Toasts.AddForTest("x", () => ++attempts == 1 ? throw new InvalidOperationException("boom") : "x", null);
        Assert.Throws<InvalidOperationException>(() => Toasts.UndoLatest());
        Assert.Equal(1, Toasts.PendingCount);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(2, attempts);
        Assert.Equal(0, Toasts.PendingCount);
    });

    // ---- b：撤销删除实例时已有同名实例 ----

    /// <summary>b：keepName 时有同名的就不恢复（不会多出打不开的“A (1)”），回收目录里的还在，挪开同名的以后能恢复。</summary>
    [Fact]
    public void InstanceUndeleteRefusesWhenNameTaken()
    {
        Write(@"versions\A\A.json", "{\"id\":\"A\"}");
        var dir = Path.Combine(_game, "versions", "A");
        var entry = TrashService.MoveToTrash(dir, _game);
        Write(@"versions\A\A.json", "{\"id\":\"A\",\"new\":true}");

        var e = Assert.Throws<TrashException>(() => TrashService.Restore(entry, keepName: true));
        Assert.Equal(TrashFailure.TargetExists, e.Reason);
        Assert.False(Directory.Exists(Path.Combine(_game, "versions", "A (1)")));
        Assert.Contains("\"new\"", File.ReadAllText(Path.Combine(dir, "A.json")));

        Directory.Delete(dir, true);
        Assert.Equal(dir, TrashService.Restore(entry, keepName: true));
        Assert.DoesNotContain("\"new\"", File.ReadAllText(Path.Combine(dir, "A.json")));
    }

    /// <summary>b：撤销失败（同名）时记录留着，可以再试。</summary>
    [Fact]
    public void TargetExistsStaysInHistory() => UiTestThread.Run(() =>
    {
        Toasts.ClearHistory();
        Toasts.AddForTest("A", () => throw new TrashException(TrashFailure.TargetExists, @"C:\x\A"), null);
        Assert.True(Toasts.UndoLatest());
        Assert.Equal(1, Toasts.PendingCount);
        Toasts.ClearHistory();
    });

    // ---- c：没名字的服务器 ----

    /// <summary>c：没名字的条目显示成“Minecraft Server”，按显示的名字也能删掉；跳过的坏条目不影响位置。</summary>
    [Fact]
    public void NamelessServerCanBeRemoved()
    {
        Nbt.WriteFile(Path.Combine(_game, "servers.dat"), new Dictionary<string, object>
        {
            ["servers"] = new Nbt.NbtList(10,
            [
                new Dictionary<string, object> { ["name"] = "没地址" },
                new Dictionary<string, object> { ["ip"] = "a.example" },
                new Dictionary<string, object> { ["name"] = "有名字", ["ip"] = "a.example" },
            ]),
        });
        var shown = ServerList.Read(_game);
        Assert.Equal(["Minecraft Server", "有名字"], shown.Select(s => s.Name));

        var removed = ServerList.Remove(_game, shown[0].Address, shown[0].Name, 0);
        Assert.NotNull(removed);
        Assert.Equal(1, removed!.Index);
        Assert.Equal(["有名字"], ServerList.Read(_game).Select(s => s.Name));
    }

    /// <summary>c：界面上的第 2 条（文件里前面有一条被跳过）按位置删，删的是点的那条。</summary>
    [Fact]
    public void ServerIndexIsMappedPastSkippedEntries()
    {
        Nbt.WriteFile(Path.Combine(_game, "servers.dat"), new Dictionary<string, object>
        {
            ["servers"] = new Nbt.NbtList(10,
            [
                new Dictionary<string, object> { ["name"] = "坏的" },
                new Dictionary<string, object> { ["name"] = "同名", ["ip"] = "a.example" },
                new Dictionary<string, object> { ["name"] = "同名", ["ip"] = "a.example", ["icon"] = "第二条" },
            ]),
        });
        var removed = ServerList.Remove(_game, "a.example", "同名", 1);
        Assert.Equal(2, removed!.Index);
        Assert.True(removed.Raw.ContainsKey("icon"));
    }

    // ---- d：错误原因跟着语言 ----

    /// <summary>d：回收目录的错误原因放进消息参数时不是现成的字符串，显示时才翻译。</summary>
    [Fact]
    public void TrashReasonIsDeferred()
    {
        var e = new TrashException(TrashFailure.ParentMissing, "x");
        var reason = Toasts.Reason(e);
        Assert.IsNotType<string>(reason);
        Assert.Equal(Toasts.Describe(e), reason.ToString());
    }

    /// <summary>界面控件里的 StaticResource（圆角、字号这些）来自 Jay 的 Tokens.axaml：测试的 Application 是空的，先装上一次。</summary>
    private static void EnsureAppResources()
    {
        var resources = Avalonia.Application.Current!.Resources;
        if (resources.ContainsKey("RadiusMd")) return;
        resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Jay/"))
        {
            Source = new Uri("avares://Jay/Themes/Tokens.axaml"),
        });
    }

    /// <summary>改产物名后仍能从新程序集找到并解码窗口图标，不能只检查磁盘上有图标文件。</summary>
    [Fact]
    public void JayWindowIconLoadsFromRenamedAssembly() => UiTestThread.Run(() =>
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Jay/Assets/jay.ico"));
        var icon = new Avalonia.Controls.WindowIcon(stream);
        Assert.NotNull(icon);
    });

    // ---- e：复制按钮 ----

    /// <summary>e：复制按钮的文字由自己的状态决定，换语言时按状态重写（不靠被覆盖掉的绑定）。</summary>
    [Fact]
    public void CopyButtonFollowsItsOwnState() => UiTestThread.Run(() =>
    {
        EnsureAppResources();
        var bar = new StatusBar();
        Assert.Equal("common.copy", bar.CopyStateKey);
        var button = bar.CopyButton;
        Assert.Equal(L.T("common.copy"), button.Content);
    });

    // ---- f：dotnet 的位置 ----

    [Fact]
    public void DotnetIsFoundUnderTestHost()
    {
        var dotnet = BuildSettingsTests.DotnetPath();
        if (dotnet is null) return;
        Assert.True(File.Exists(dotnet));
    }

    // ---- g：补的测试 ----

    /// <summary>g：没选中实例（被删了或读不了）时刷新世界列表，清空而不是崩。走真实的 InstanceViewModel。</summary>
    [Fact]
    public void LoadWorldsWithoutCurrentClearsList() => UiTestThread.Run(() =>
    {
        var service = new LauncherService(new AppSettings { GameDirectory = _game });
        var vm = new InstanceViewModel(service);
        Assert.Null(vm.Current);
        vm.Worlds.Add(new WorldItem(new WorldInfo("w", Path.Combine(_game, "w"), "w", "", null, null)));
        typeof(InstanceViewModel).GetMethod("LoadWorlds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
        Assert.Empty(vm.Worlds);
        Assert.Null(vm.DatapackWorld);
    });

    /// <summary>g：资源页导入失败时用 Failure：错误色、带详情（完整异常），原因跟着语言。</summary>
    [Fact]
    public void ContentImportFailureUsesFailure()
    {
        LauncherService? service = null;
        ContentViewModel? vm = null;
        UiTestThread.Run(() =>
        {
            service = new LauncherService(new AppSettings { GameDirectory = _game });
            vm = new ContentViewModel(service);
        });
        vm!.ImportLocalPackAsync(Path.Combine(_game, "missing.mrpack")).GetAwaiter().GetResult();
        var status = vm.StatusInfo;
        Assert.NotNull(status);
        Assert.Equal(StatusKind.Error, status!.Kind);
        Assert.Equal("instance.worlds.import_failed", status.Key);
        Assert.False(string.IsNullOrEmpty(status.Detail));
        Assert.DoesNotContain(status.Args, a => a is string);
    }

    /// <summary>g：只装着状态条的卡片：没消息或被 ✕ 关掉时 IsShowing 为 false（卡片跟着收起）。</summary>
    [Fact]
    public void StatusBarHidesWhenEmptyOrDismissed() => UiTestThread.Run(() =>
    {
        EnsureAppResources();
        var bar = new StatusBar();
        var window = new Avalonia.Controls.Window { Content = bar };
        window.Show();
        try
        {
            Assert.False(bar.IsShowing);
            bar.Message = StatusMessage.Error("common.install_failed", "x");
            Assert.True(bar.IsShowing);
            bar.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.False(bar.IsShowing);
            bar.Message = StatusMessage.Error("common.install_failed", "x");
            Assert.True(bar.IsShowing);
            bar.Message = null;
            Assert.False(bar.IsShowing);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>状态条右边都有 ✕；成功、警告到时间自动消失，错误和进行中的信息留着。</summary>
    [Fact]
    public void StatusBarAutoDismissesSuccessAndWarning() => UiTestThread.Run(() =>
    {
        EnsureAppResources();
        var bar = new StatusBar();
        var window = new Avalonia.Controls.Window { Content = bar };
        window.Show();
        try
        {
            foreach (var kind in Enum.GetValues<StatusKind>())
            {
                bar.Message = new StatusMessage("common.raw", ["x"], kind);
                Assert.True(bar.IsShowing);
                Assert.True(bar.CloseButton.IsVisible);
                var auto = kind is StatusKind.Success or StatusKind.Warning;
                Assert.Equal(auto, bar.AutoDismissPending);
                bar.ElapseAutoDismissForTest();
                Assert.Equal(!auto, bar.IsShowing);
            }

            // 自动消失后来了新消息照常显示，并重新计时。
            bar.Message = StatusMessage.Success("multiplayer.component.downloaded");
            bar.ElapseAutoDismissForTest();
            Assert.False(bar.IsShowing);
            Assert.False(bar.AutoDismissPending);
            bar.Message = StatusMessage.Success("multiplayer.component.downloaded");
            Assert.True(bar.IsShowing);
            Assert.True(bar.AutoDismissPending);

            // 手动 ✕ 也能关成功消息，关了就不再计时。
            bar.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.False(bar.IsShowing);
            Assert.False(bar.AutoDismissPending);
        }
        finally
        {
            window.Close();
        }
    });
}
