using System.Diagnostics;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux：Jay 没了 easytier-core 不能留着。用 sleep 代替 easytier-core 和“另一个 Jay”。</summary>
public sealed class EasyTierProcessGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-etguard-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Process Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        var p = Process.Start(info)!;
        _started.Add(p);
        return p;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50);
        }
        return true;
    }

    [Fact]
    public void WrapKeepsArgumentsIntact()
    {
        var info = new ProcessStartInfo("/opt/et/easytier-core");
        info.ArgumentList.Add("--network-name");
        info.ArgumentList.Add("a b; rm -rf ~");
        EasyTierProcessGuard.Wrap(info);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("/opt/et/easytier-core", info.FileName);
            return;
        }
        Assert.Equal("/bin/sh", info.FileName);
        Assert.Equal(["-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/opt/et/easytier-core", "--network-name", "a b; rm -rf ~"], info.ArgumentList);
    }

    [Fact]
    public async Task CoreEndsSoonAfterJayIsKilled()
    {
        if (OperatingSystem.IsWindows()) return;
        var jay = Start("/bin/sleep", "600");
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", jay.Id.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Children(watch.Id).Count > 0), "core started");
        var core = Children(watch.Id)[0];
        await Task.Delay(1200);
        Assert.True(Alive(core), "Jay 还在时 core 一直跑");
        jay.Kill(); // 相当于 kill -9 掉 Jay
        Assert.True(await Until(() => !Alive(core)), "Jay 没了以后 core 跟着结束");
        Assert.True(await Until(() => watch.HasExited));
    }

    [Fact]
    public async Task StoppingTheWatcherStopsTheCore()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Children(watch.Id).Count > 0));
        var core = Children(watch.Id)[0];
        Process.Start("kill", ["-TERM", watch.Id.ToString()])!.WaitForExit();
        Assert.True(await Until(() => !Alive(core)));
    }

    [Fact]
    public async Task TrackerCleansUpAWrappedCoreWhenTheWatcherWasLeftBehind()
    {
        // 看门进程自己被留下（比如记录它的 Jay 已经没了但看门进程还没轮到检查）：下次开始联机前的清理按 sh 认出它，连 core 一起结束。
        if (OperatingSystem.IsWindows()) return;
        var info = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        info.ArgumentList.Add("600");
        EasyTierProcessGuard.Wrap(info);
        var watch = Process.Start(info)!;
        _started.Add(watch);
        Assert.True(await Until(() => Children(watch.Id).Count > 0));
        var core = Children(watch.Id)[0];
        Assert.NotNull(EasyTierProcessTracker.Track(watch, _dir, EasyTierProcessGuard.TrackedExecutable(info)));
        EasyTierProcessTracker.Cleanup(_dir, "/opt/not-easytier", EasyTierProcessGuard.WatcherExecutable);
        Assert.True(await Until(() => watch.HasExited && !Alive(core)));
    }

    /// <summary>直接子进程（Linux 读 /proc，macOS 用 pgrep）。</summary>
    private static List<int> Children(int pid)
    {
        var info = new ProcessStartInfo("pgrep") { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("-P");
        info.ArgumentList.Add(pid.ToString());
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
    }
}
