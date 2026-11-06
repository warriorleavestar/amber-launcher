using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Bake;
using Amber.Core.Game;
using Amber.Core.Launch;
using Avalonia.Threading;
using System.Diagnostics;

namespace Amber.Core.Tests;

/// <summary>
/// H12：安装、启动、补全文件在开始时取一份游戏目录和安装器（GameContext），全程用它。
/// 中途换了游戏目录，标记、设置、工作目录都还落在原来的目录里，也不去通知新目录的界面。
/// </summary>
public sealed class GameContextTests : IDisposable
{
    private readonly string _a = Path.Combine(Path.GetTempPath(), "amber-ctx-a-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _b = Path.Combine(Path.GetTempPath(), "amber-ctx-b-" + Guid.NewGuid().ToString("N")[..8]);

    public GameContextTests()
    {
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
    }

    public void Dispose()
    {
        try { Directory.Delete(_a, true); } catch (IOException) { }
        try { Directory.Delete(_b, true); } catch (IOException) { }
    }

    private static void Instance(string root, string id) => File.WriteAllText(
        Path.Combine(Directory.CreateDirectory(Path.Combine(root, "versions", id)).FullName, id + ".json"),
        $$"""{"id":"{{id}}","type":"release","mainClass":"x","libraries":[]}""");

    private LauncherService Service() => new(new AppSettings
    {
        GameDirectory = _a,
        PlayerName = "",
        SavePathForTest = Path.Combine(_a, "settings.json"),
    });

    [Fact]
    public void CapturedContextKeepsOldDirectoryAfterSwitch()
    {
        Instance(_a, "x");
        Instance(_b, "x");
        var service = Service();
        var ctx = service.Context;
        ctx.Directory.MarkInstallStarted("x");
        new GameDirectory(_b).MarkInstallStarted("x");
        new InstanceSettings { Isolate = true }.Save(ctx.Directory, "x");
        new InstanceSettings { Isolate = false }.Save(new GameDirectory(_b), "x");

        // 操作进行到一半，在设置里换了游戏目录。
        service.Settings.GameDirectory = _b;
        service.SaveAndReload();
        Assert.NotSame(ctx, service.Context);
        Assert.Equal(Path.GetFullPath(_b), Path.GetFullPath(service.Directory.Root));
        Assert.Equal(Path.GetFullPath(_a), Path.GetFullPath(ctx.Directory.Root));
        Assert.False(service.IsCurrentDirectory(ctx));

        // 设置、工作目录按开始时的目录算。
        Assert.True(service.EffectiveSettings(ctx, "x").Isolate);
        Assert.False(service.EffectiveSettings("x").Isolate);
        Assert.Equal(ctx.Directory.VersionDir("x"), service.WorkDirFor(ctx, "x"));
        Assert.Equal(service.Directory.Root, service.WorkDirFor("x"));

        // 装完去掉的是原目录里的标记；新目录里同名实例的标记不动，也不通知界面（界面显示的是新目录）。
        var notified = new List<string>();
        service.InstanceStateChanged += notified.Add;
        service.MarkInstallFinished(ctx, "x");
        Assert.Equal(InstanceState.Ready, ctx.Directory.StateOf("x"));
        Assert.Equal(InstanceState.Incomplete, service.Directory.StateOf("x"));
        Assert.Empty(notified);
        service.MarkInstallFinished(service.Context, "x");
        Assert.Equal(["x"], notified);
    }

    /// <summary>区分大小写的系统上，只差大小写的是另一个游戏目录。</summary>
    [Fact]
    public void DirectoryThatDiffersOnlyByCaseIsAnotherDirectoryOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var upper = Path.Combine(Path.GetDirectoryName(_a)!, Path.GetFileName(_a).ToUpperInvariant());
        Directory.CreateDirectory(upper);
        try
        {
            var service = Service();
            var ctx = service.Context;
            service.Settings.GameDirectory = upper;
            service.SaveAndReload();
            Assert.False(service.IsCurrentDirectory(ctx));
        }
        finally { Directory.Delete(upper, true); }
    }

    /// <summary>只换了下载源（目录没变）：还算同一个目录，装完照常设成当前实例、通知界面。</summary>
    [Fact]
    public void SameDirectoryReloadStillCounts()
    {
        var service = Service();
        var ctx = service.Context;
        service.Settings.DownloadSource = "mirror";
        service.SaveAndReload();
        Assert.NotSame(ctx, service.Context);
        Assert.True(service.IsCurrentDirectory(ctx));
        Assert.Same(ctx.Directory, ctx.Loaders.Dir);
        Assert.Same(ctx.Installer, ctx.Loaders.Vanilla);
    }

    [Fact]
    public void CapturedBakeInputsAndRecordStayInOriginalDirectory()
    {
        const string id = "1.21.1";
        WriteBakeInstance(_a, id, 21, "from-a", isolate: true);
        WriteBakeInstance(_b, id, 17, "from-b", isolate: false);
        using var service = Service();
        var ctx = service.Context;
        var beforeB = Directory.GetFiles(_b, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        service.Settings.GameDirectory = _b;
        service.SaveAndReload();

        // 两个目录有同名实例；当前目录的隔离设置、Java 要求和库都不同。
        var current = service.GetInstance(id);
        Assert.Equal(_b, current.WorkDir);
        Assert.Equal(17, service.Directory.ResolveVersion(id).JavaVersion!.MajorVersion);
        var captured = service.GetInstance(ctx, id);
        Assert.Equal(ctx.Directory.VersionDir(id), captured.WorkDir);
        Assert.Equal(21, ctx.Directory.ResolveVersion(captured.Id).JavaVersion!.MajorVersion);
        var inputs = BakeService.GameInputs(ctx.Directory, captured);
        Assert.Equal(ctx.Directory.VersionJarPath(id), inputs.GameJar);
        Assert.Equal([Path.Combine(_a, "libraries", "test", "from-a", "1", "from-a-1.jar")], inputs.Libraries);
        Assert.Equal("game-from-a", File.ReadAllText(inputs.GameJar));
        Assert.Equal("library-from-a", File.ReadAllText(Assert.Single(inputs.Libraries)));

        // 只模拟烘焙产物落盘，不启动 Java；状态沿捕获的实例工作目录保存。
        var state = Path.Combine(captured.WorkDir, BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");
        var launch = new BakedLaunch("test.Main", [inputs.GameJar, .. inputs.Libraries], [], [], state, "original");
        BakeRecord.Write(captured.WorkDir, new BakeRecord("captured-directory", launch));
        Assert.Equal("original", BakeRecord.Read(ctx.Directory.VersionDir(id))!.Launch.StateKey);
        Assert.Null(BakeRecord.Read(current.WorkDir));
        Assert.Null(BakeRecord.Read(service.Directory.VersionDir(id)));
        Assert.Equal(beforeB, Directory.GetFiles(_b, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void BakeRetryCannotLaunchSameNamedInstanceInAnotherDirectory()
    {
        Instance(_a, "same-id");
        Instance(_b, "same-id");
        using var service = Service();
        var ctx = service.Context;
        var requests = new List<LaunchRequest>();
        service.LaunchRequested += requests.Add;
        var request = new LaunchRequest("same-id", JoinServer: "example.invalid:25565", JoinWorld: "Original world");
        service.Settings.GameDirectory = _b;
        service.SaveAndReload();

        Assert.False(HomeViewModel.TryRequestBakeRetry(service, ctx, request));
        Assert.Empty(requests);
        Assert.False(request.SkipBake);
    }

    [Fact]
    public void BakeRetryAfterSameDirectoryReloadPreservesTheOriginalRequest()
    {
        Instance(_a, "same-id");
        using var service = Service();
        var ctx = service.Context;
        var requests = new List<LaunchRequest>();
        service.LaunchRequested += requests.Add;
        var request = new LaunchRequest("same-id", JoinServer: "example.invalid:25565", JoinWorld: "Original world");
        service.Settings.DownloadSource = "mirror";
        service.SaveAndReload();
        Assert.NotSame(ctx, service.Context);

        Assert.True(HomeViewModel.TryRequestBakeRetry(service, ctx, request));
        var retry = Assert.Single(requests);
        Assert.Equal(request.VersionId, retry.VersionId);
        Assert.Equal(request.JoinServer, retry.JoinServer);
        Assert.Equal(request.JoinWorld, retry.JoinWorld);
        Assert.True(retry.SkipBake);
        Assert.False(request.SkipBake);
        Assert.Same(ctx, retry.ExpectedContext);
        Assert.True(HomeViewModel.BakeRetryDirectoryMatches(service, retry));

        // 请求已经发出但尚未开始（或仍在等多开确认）时切目录，启动入口仍要再次拒绝。
        service.Settings.GameDirectory = _b;
        service.SaveAndReload();
        Assert.False(HomeViewModel.BakeRetryDirectoryMatches(service, retry));
    }

    [Fact]
    public void QueuedBakeRetryIsRejectedAfterDirectoryChangesBeforeDispatch()
    {
        Instance(_a, "same-id");
        Instance(_b, "same-id");
        UiTestThread.Run(() =>
        {
            using var service = Service();
            var home = new HomeViewModel(service, new LogViewModel());
            var ctx = service.Context;
            using var placeholder = new Process();
            // 有一局占位：有效请求只能进入多开确认，整个测试都不会补文件或启动 Java。
            var running = home.AddRunForTest("existing-run", placeholder);
            try
            {
                Assert.True(HomeViewModel.TryRequestBakeRetry(service, ctx, new LaunchRequest("same-id")));
                Assert.Null(home.PendingLaunch);
                service.Settings.GameDirectory = _b;
                service.SaveAndReload();
                Dispatcher.UIThread.RunJobs();
                Assert.Null(home.PendingLaunch);
                Assert.Equal(_b, service.Directory.Root);

                // 正向对照：同一订阅和消息队列确实会处理请求，回到原目录后可进入确认。
                service.Settings.GameDirectory = _a;
                service.SaveAndReload();
                Assert.True(HomeViewModel.TryRequestBakeRetry(service, ctx, new LaunchRequest("same-id")));
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(home.PendingLaunch);
                Assert.Same(ctx, home.PendingLaunch.ExpectedContext);
                Assert.True(home.PendingLaunch.SkipBake);
            }
            finally
            {
                home.DismissPendingLaunchCommand.Execute(null);
                home.RemoveRunForTest(running);
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    private static void WriteBakeInstance(string root, string id, int javaMajor, string artifact, bool isolate)
    {
        var directory = new GameDirectory(root);
        Directory.CreateDirectory(directory.VersionDir(id));
        File.WriteAllText(directory.VersionJsonPath(id), System.Text.Json.JsonSerializer.Serialize(new
        {
            id,
            type = "release",
            mainClass = "test.Main",
            javaVersion = new { majorVersion = javaMajor },
            libraries = new[]
            {
                new
                {
                    name = $"test:{artifact}:1",
                    downloads = new { artifact = new { path = $"test/{artifact}/1/{artifact}-1.jar", url = "https://example.invalid/library.jar" } },
                },
            },
        }));
        File.WriteAllText(directory.VersionJarPath(id), "game-" + artifact);
        var library = Path.Combine(directory.LibrariesDir, "test", artifact, "1", artifact + "-1.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.WriteAllText(library, "library-" + artifact);
        new InstanceSettings { Isolate = isolate }.Save(directory, id);
    }
}
