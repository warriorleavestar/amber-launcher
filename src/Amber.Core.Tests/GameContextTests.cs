using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Launch;

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
}
