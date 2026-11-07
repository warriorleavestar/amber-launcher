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
    public void SweepKillsOnlyOrphansWhoseJayIsGone()
    {
        if (OperatingSystem.IsWindows()) return;
        var mine = Start("/bin/sleep", "600");
        Assert.NotNull(EasyTierProcessGuard.Record(_dir, mine));
        // 记录它的就是现在这个（活着的）进程：不清。
        Assert.Equal(0, EasyTierProcessGuard.Sweep(_dir));
        Assert.False(mine.HasExited);

        // 另一个文件：Jay（owner）已经不在了 → 清掉。
        var orphan = Start("/bin/sleep", "600");
        var gone = Start("/bin/true");
        gone.WaitForExit();
        var start = orphan.StartTime.ToUniversalTime().Ticks;
        File.WriteAllText(Path.Combine(_dir, orphan.Id + ".pid"), $"{orphan.Id} {start} {gone.Id} 1");
        // 再来一个：PID 对上了但启动时间不对（PID 被别的程序重用）→ 不清，只删文件。
        var reused = Start("/bin/sleep", "600");
        File.WriteAllText(Path.Combine(_dir, reused.Id + ".pid"), $"{reused.Id} 1 {gone.Id} 1");

        Assert.Equal(1, EasyTierProcessGuard.Sweep(_dir));
        Assert.True(orphan.WaitForExit(5000));
        Assert.False(reused.HasExited);
        Assert.False(mine.HasExited);
        Assert.False(File.Exists(Path.Combine(_dir, orphan.Id + ".pid")));
        Assert.False(File.Exists(Path.Combine(_dir, reused.Id + ".pid")));
        Assert.True(File.Exists(Path.Combine(_dir, mine.Id + ".pid")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, mine.Id + ".pid")));
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
