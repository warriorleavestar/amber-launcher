using System.Diagnostics;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Diagnostics;
using Amber.Core.Game;
using Amber.Core.Localization;
using Amber.Core.Launch;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>1b（ccd1340）评审的 8 条：多开、强制结束、状态、日志、崩溃分析、实例切换、实例状态、设置保存。</summary>
public sealed class RunLifecycleTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "amber-run-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _processes = [];

    public RunLifecycleTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private void Instance(string id) => File.WriteAllText(
        Path.Combine(Directory.CreateDirectory(Path.Combine(_game, "versions", id)).FullName, id + ".json"),
        $$"""{"id":"{{id}}","type":"release","mainClass":"x","libraries":[]}""");

    private LauncherService Service() => new(new AppSettings
    {
        GameDirectory = _game,
        SavePathForTest = Path.Combine(_game, "settings.json"),
    });

    /// <summary>不开窗口、一直等着的进程：CloseMainWindow 对它返回 false。</summary>
    private Process FakeGame()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command Start-Sleep 60") { UseShellExecute = false, CreateNoWindow = true };
        var p = Process.Start(info)!;
        _processes.Add(p);
        return p;
    }

    private static void Drain() => Dispatcher.UIThread.RunJobs();

    [Fact]
    public async Task PrimaryButtonCanCancelWhileItIsPreparing()
    {
        using var server = new TestServer();
        var url = server.Add("client", [1]);
        server.Hang.Add("client");
        Instance("A");
        File.WriteAllText(Path.Combine(_game, "versions", "A", "A.jar"), "old");
        File.WriteAllText(Path.Combine(_game, "versions", "A", "A.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "A",
                type = "release",
                mainClass = "x",
                libraries = Array.Empty<object>(),
                downloads = new { client = new { url, size = 1 } },
            }));
        using var service = Service();
        HomeViewModel? home = null;
        Task? launching = null;
        try
        {
            UiTestThread.Run(() =>
            {
                service.Accounts.AddOffline("TestPlayer");
                home = new HomeViewModel(service, new LogViewModel());
                home.SelectedVersion = "A";
                home.SelectedAccount = home.Accounts.First();
                launching = home.PrimaryCommand.ExecuteAsync(null);
                Assert.False(launching.IsCompleted);
                Assert.True(home.PrimaryCommand.CanExecute(null));
                home.PrimaryCommand.Execute(null);
            });
        }
        finally
        {
            UiTestThread.Run(() => home?.CancelCommand.Execute(null));
            if (launching is not null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!launching.IsCompleted && DateTime.UtcNow < deadline)
                {
                    UiTestThread.Run(Drain);
                    await Task.Delay(10);
                }
                await launching.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public void MacExitRequestDoesNotForceTerminateHeadlessProcesses()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var process = Process.Start(new ProcessStartInfo("/bin/sleep")
        {
            ArgumentList =
            {
                "60",
            },
            UseShellExecute = false,
        })!;
        try
        {
            Assert.False(GameLauncher.RequestExit(process));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    [Fact]
    public void LinuxWithoutX11WindowFallsBackToSigterm()
    {
        // 没有图形会话（CI）时找不到窗口，只能发 SIGTERM；有 X11 的情况用 Xvfb 手动验证过（见 PR 说明）。
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        using var process = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" }, UseShellExecute = false })!;
        try
        {
            Assert.Equal(ExitRequest.Signal, GameLauncher.RequestExitDetailed(process));
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(ExitRequest.NotSent, GameLauncher.RequestExitDetailed(process));
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
        }
    }

    // ---- 1：强制结束绑定到那一局 ----

    /// <summary>1：关不掉主窗口时“正在退出”撤回，强制结束只给这一局；又开了一局后收回，不会杀到新的那局。</summary>
    [Fact]
    public void ForceKillIsTiedToItsRun()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = FakeGame();
        var b = FakeGame();
        UiTestThread.Run(() =>
        {
            var home = new HomeViewModel(Service(), new LogViewModel());
            var runA = home.AddRunForTest("A", a);
            home.ExitGameForTest(runA);
            Assert.False(HomeViewModel.IsExitingForTest(runA));
            Assert.False(HomeViewModel.StoppedByUserForTest(runA));
            Assert.True(home.ShowForceKill);

            // 又开了一局：给 A 的强制结束收回，点了也不会杀到 B。
            var runB = home.AddRunForTest("B", b);
            Assert.False(home.ShowForceKill);
            home.ForceKillCommand.Execute(null);
            Assert.False(a.HasExited);
            Assert.False(b.HasExited);

            // B 结束后 A 又是最近一局：重新给出，强制结束只杀 A。
            home.RemoveRunForTest(runB);
            home.OfferForceKillForTest(runA);
            Assert.True(home.ShowForceKill);
            home.ForceKillCommand.Execute(null);
            Assert.False(home.ShowForceKill);
            home.RemoveRunForTest(runA);
        });
        Assert.True(a.WaitForExit(10_000));
        Assert.False(b.HasExited);
    }

    // ---- 2：Tick 只覆盖自己写的状态 ----

    [Fact]
    public void TickKeepsOtherStatuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var game = FakeGame();
        UiTestThread.Run(() =>
        {
            var home = new HomeViewModel(Service(), new LogViewModel());
            var run = home.AddRunForTest("A", game);
            home.SetOwnedStatusForTest(StatusMessage.Info("home.status.launching"));
            home.TickForTest();
            Assert.Equal("home.status.loading_game", home.StatusInfo!.Key);

            var failure = StatusMessage.Error("home.status.launch_failed", "x");
            home.SetStatus(failure);
            home.TickForTest();
            Assert.Same(failure, home.StatusInfo);
            home.RemoveRunForTest(run);
        });
    }

    // ---- 4：每局一份日志 ----

    [Fact]
    public void EachRunKeepsItsOwnLog() => UiTestThread.Run(() =>
    {
        var log = new LogViewModel();
        var first = log.BeginRun("A", DateTimeOffset.Now);
        log.AddRaw(first, "a1");
        log.Flush();
        var second = log.BeginRun("B", DateTimeOffset.Now);
        Assert.Same(second, log.SelectedRun);
        Assert.True(log.HasSeveralRuns);
        Assert.Empty(log.Lines);

        // 第一局的输出继续进第一局，不混进正在看的第二局。
        log.AddRaw(first, "a2");
        log.AddRaw(second, "b1");
        log.Flush();
        Assert.Equal(["b1"], log.Lines.Select(l => l.Message));

        log.Show(first);
        Assert.Equal(["a1", "a2"], log.Lines.Select(l => l.Message));
        Assert.Equal(L.F("log.run_summary", "A", Fmt.Time(first.StartedAt)), log.RunSummary);

        // 清空只清正在看的那局。
        log.Clear();
        log.Flush();
        Assert.Empty(log.Lines);
        log.Show(second);
        Assert.Equal(["b1"], log.Lines.Select(l => l.Message));
    });

    [Fact]
    public void OldRunLogsAreDropped() => UiTestThread.Run(() =>
    {
        var log = new LogViewModel();
        var first = log.BeginRun("A", DateTimeOffset.Now);
        for (var i = 0; i < LogViewModel.KeptRuns; i++) log.BeginRun("B" + i, DateTimeOffset.Now);
        Assert.Equal(LogViewModel.KeptRuns, log.Runs.Count);
        Assert.DoesNotContain(first, log.Runs);
        var shown = log.SelectedRun;
        log.Show(first);
        Assert.Same(shown, log.SelectedRun);
    });

    // ---- 5：同一游戏目录的两局 ----

    /// <summary>5：两局共用一个目录时，崩溃报告和 latest.log 分不清是谁的，只用自己的 hs_err_pid 文件。</summary>
    [Fact]
    public void SharedWorkDirUsesOnlyOwnFiles()
    {
        var started = DateTime.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(Path.Combine(_game, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(_game, "logs"));
        File.WriteAllText(Path.Combine(_game, "crash-reports", "crash-other.txt"), "x");
        File.WriteAllText(Path.Combine(_game, "logs", "latest.log"), "x");
        File.WriteAllText(Path.Combine(_game, "hs_err_pid111.log"), "x");
        File.WriteAllText(Path.Combine(_game, "hs_err_pid222.log"), "x");

        var alone = CrashAnalyzer.FindRunFiles(_game, started);
        Assert.Equal(3, alone.Count);

        var shared = CrashAnalyzer.FindRunFiles(_game, started, pid: 222, sharedWorkDir: true);
        Assert.Equal([Path.Combine(_game, "hs_err_pid222.log")], shared);

        var sharedNoPid = CrashAnalyzer.FindRunFiles(_game, started, sharedWorkDir: true);
        Assert.Empty(sharedNoPid);

        var own = CrashAnalyzer.FindRunFiles(_game, started, pid: 111);
        Assert.Contains(Path.Combine(_game, "hs_err_pid111.log"), own);
        Assert.DoesNotContain(Path.Combine(_game, "hs_err_pid222.log"), own);
    }

    // ---- 6：实例页不被后台安装抢走选择 ----

    [Fact]
    public void InstancePageKeepsSelectionWhileInUse()
    {
        Instance("A");
        Instance("B");
        UiTestThread.Run(() =>
        {
            var service = Service();
            var vm = new InstanceViewModel(service);
            vm.Activate();
            vm.IsCurrent = true;
            var selected = vm.SelectedId;
            Assert.NotNull(selected);
            var other = selected == "A" ? "B" : "A";

            // 后台装完一个新实例、别处把它设成当前实例：正在看的这一页不跟着换。
            Instance("C");
            service.NotifyInstalledChanged();
            service.SetCurrentInstance("C");
            Drain();
            Assert.Equal(selected, vm.SelectedId);
            Assert.Contains("C", vm.Instances);

            // 离开这一页再回来：跟上当前实例。
            vm.IsCurrent = false;
            service.SetCurrentInstance(other);
            Drain();
            vm.IsCurrent = true;
            Drain();
            Assert.Equal(other, vm.SelectedId);
        });
    }

    // ---- 7：实例状态变了要通知 ----

    [Fact]
    public void InstallMarksRaiseInstanceStateChanged()
    {
        Instance("A");
        UiTestThread.Run(() =>
        {
            var service = Service();
            var changed = new List<string>();
            service.InstanceStateChanged += changed.Add;
            var home = new HomeViewModel(service, new LogViewModel());
            home.SelectedVersion = "A";
            var before = home.VersionInfo;

            service.MarkInstallStarted("A");
            Drain();
            Assert.Equal(["A"], changed);
            Assert.Equal(InstanceState.Incomplete, service.Directory.StateOf("A"));
            Assert.NotEqual(before, home.VersionInfo);

            service.MarkInstallFinished("A");
            Drain();
            Assert.Equal(["A", "A"], changed);
            Assert.Equal(before, home.VersionInfo);
        });
    }

    // ---- 8：当前实例存不进设置文件 ----

    [Fact]
    public void CurrentInstanceSurvivesSaveFailure()
    {
        var settingsPath = Path.Combine(_game, "locked", "settings.json");
        // 临时文件的位置被一个文件夹占着：写设置时抛 UnauthorizedAccessException。
        Directory.CreateDirectory(settingsPath + ".tmp");
        // 此用例只验证实例设置保存，不应触发共享测试账号库的旧玩家名迁移。
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _game,
            SavePathForTest = settingsPath,
            PlayerName = "",
        });
        var failures = new List<Exception>();
        var changed = 0;
        service.SettingsSaveFailed += failures.Add;
        service.CurrentInstanceChanged += () => changed++;

        service.SetCurrentInstance("A");
        Assert.Equal("A", service.CurrentInstance);
        Assert.Equal(1, changed);
        Assert.Single(failures);
        Assert.True(failures[0] is UnauthorizedAccessException or IOException);
    }
}
