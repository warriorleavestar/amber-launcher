using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// 完整局域网模式的守护脚本：不提权运行，用一个 PowerShell 小程序代替 easytier-core。
/// 检查参数（含假的房间密钥）只经管道传、不出现在命令行里，输出经管道回来，重启、停止、Amber 崩溃三种情况，
/// 以及别的程序抢先连上管道时什么都不发。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ElevatedGuardTests
{
    private const string Secret = "SECRET-7f3a9c";

    private static string Core => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>假的 easytier-core：打印收到的参数、环境变量里的密钥和自己的进程号，然后一直等着。</summary>
    private static string[] FakeCoreArgs(string tag) =>
        ["-NoProfile", "-NonInteractive", "-Command", $"Write-Output ('pid=' + $PID); Write-Output ('tag={tag} secret=' + $env:{EasyTierArgs.SecretVariable}); [Console]::Error.WriteLine('err-line'); Start-Sleep 60"];

    private static readonly Dictionary<string, string> Env = new() { [EasyTierArgs.SecretVariable] = Secret };

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 20)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static int? CorePid(ElevatedGuard guard, string tag)
    {
        var lines = guard.RecentOutput;
        var index = lines.ToList().FindIndex(l => l.Contains("tag=" + tag));
        if (index <= 0) return null;
        var pidLine = lines.Take(index).LastOrDefault(l => l.StartsWith("pid=", StringComparison.Ordinal));
        return pidLine is not null && int.TryParse(pidLine[4..], out var pid) ? pid : null;
    }

    private static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task ArgumentsGoThroughThePipeAndOutputComesBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var args = FakeCoreArgs("one");
        Assert.DoesNotContain(args, a => a.Contains(Secret));
        using var guard = ElevatedGuard.Start(Core, args, Env, elevate: false);
        Assert.DoesNotContain(Secret, guard.CommandLine);
        Assert.True(await WaitAsync(() => guard.RecentOutput.Any(l => l.Contains("tag=one secret=" + Secret))), string.Join(" | ", guard.RecentOutput));
        Assert.True(await WaitAsync(() => guard.RecentOutput.Contains("err-line")));
        Assert.True(await WaitAsync(() => !guard.StartPending));
        Assert.False(guard.HasExited);
    }

    [Fact]
    public async Task RestartSwitchesArgumentsAndStopEndsEverything()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guard = ElevatedGuard.Start(Core, FakeCoreArgs("first"), Env, elevate: false);
        Assert.True(await WaitAsync(() => CorePid(guard, "first") is not null), string.Join(" | ", guard.RecentOutput));
        var first = CorePid(guard, "first")!.Value;

        guard.Start(FakeCoreArgs("second"), Env);
        Assert.True(await WaitAsync(() => !guard.StartPending && CorePid(guard, "second") is not null), string.Join(" | ", guard.RecentOutput));
        var second = CorePid(guard, "second")!.Value;
        Assert.True(await WaitAsync(() => !IsRunning(first)));
        Assert.True(IsRunning(second));
        Assert.False(guard.HasExited);

        guard.Dispose();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.True(await WaitAsync(() => !IsRunning(second)));
    }

    /// <summary>Amber 崩溃（管道断开、没有停止命令）：守护脚本也要结束 easytier-core。</summary>
    [Fact]
    public async Task BrokenPipeEndsCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guard = ElevatedGuard.Start(Core, FakeCoreArgs("crash"), Env, elevate: false);
        Assert.True(await WaitAsync(() => CorePid(guard, "crash") is not null), string.Join(" | ", guard.RecentOutput));
        var core = CorePid(guard, "crash")!.Value;
        guard.AbandonForTest();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(20)));
        Assert.True(await WaitAsync(() => !IsRunning(core)));
    }

    /// <summary>别的程序抢先连上命令管道：认出进程号不对，断开它、什么都不发，接着等真正的守护脚本，之后照常启动。</summary>
    [Fact]
    public async Task ImpostorClientGetsNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guard = ElevatedGuard.Start(Core, FakeCoreArgs("impostor"), Env, elevate: false);
        using var impostor = new NamedPipeClientStream(".", $"amber-et-{guard.PipeId}-cmd", PipeDirection.In);
        await impostor.ConnectAsync(5000);
        var buffer = new byte[4096];
        var read = await impostor.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, read);
        Assert.True(await WaitAsync(() => CorePid(guard, "impostor") is not null), string.Join(" | ", guard.RecentOutput));
        Assert.False(guard.HasExited);
        guard.Dispose();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(20)));
    }

    /// <summary>管道不是 Amber 建的（服务端进程号不对）：守护脚本什么都不读就退出，不启动 easytier-core。</summary>
    [Fact]
    public async Task GuardRefusesForeignPipeServer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guard = ElevatedGuard.Start(Core, FakeCoreArgs("foreign"), Env, elevate: false, serverPidForTest: Environment.ProcessId + 4);
        Assert.True(await WaitAsync(() => guard.HasExited, 30));
        Assert.DoesNotContain(guard.RecentOutput, l => l.Contains("tag=foreign"));
        guard.Dispose();
    }

    /// <summary>守护脚本连管道的期限比 Amber 等的短：Amber 先等到它放弃退出。</summary>
    [Fact]
    public void GuardGivesUpBeforeAmber() =>
        Assert.True(TimeSpan.FromSeconds(ElevatedGuard.GuardConnectSeconds) < ElevatedGuard.ConnectTimeout);

    /// <summary>程序路径里有 PowerShell 会当成引号的字符（‘’‚‛ 和 '）也能原样传过去。</summary>
    [Fact]
    public async Task CorePathWithQuoteCharacters()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "amber-q ‘x’ ‚y‛ 'z' __ID__ " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var core = Path.Combine(dir, "core.exe");
        File.Copy(Core, core);
        try
        {
            var guard = ElevatedGuard.Start(core, FakeCoreArgs("quoted"), Env, elevate: false);
            Assert.True(await WaitAsync(() => CorePid(guard, "quoted") is not null), string.Join(" | ", guard.RecentOutput));
            guard.Dispose();
            Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            for (var i = 0; i < 20; i++)
            {
                try { Directory.Delete(dir, true); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData("trailing\\")]
    [InlineData("trailing space\\")]
    [InlineData("back\\\\\"slash")]
    [InlineData("tcp://1.2.3.4:11010")]
    [InlineData("a\tb")]
    public void CommandLineQuotingRoundTrips(string arg)
    {
        if (!OperatingSystem.IsWindows()) return;
        var parsed = Parse("prog " + WindowsCommandLine.Join(["x", arg, "y"]));
        Assert.Equal(["prog", "x", arg, "y"], parsed);
    }

    private static string[] Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>旧版本写在 run-lan 里的文件（参数、重启文件、日志都可能有密钥）启动时清掉；别的文件不碰。</summary>
    [Fact]
    public void LegacyFilesAreRemoved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amber-legacy-test-" + Guid.NewGuid().ToString("N")[..8]);
        var run = Path.Combine(dir, "a1b2c3d4");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "args.json"), $"[\"--network-secret\",\"{Secret}\"]");
        File.WriteAllText(Path.Combine(run, "guard.ps1"), "x");
        File.WriteAllText(Path.Combine(run, "easytier.log"), "log");
        File.WriteAllText(Path.Combine(run, "stop.restart"), Secret);
        File.WriteAllText(Path.Combine(run, "easytier.log.err"), Secret);
        var other = Path.Combine(dir, "keep.txt");
        File.WriteAllText(other, "keep");
        try
        {
            Assert.Equal(5, EasyTierElevatedSession.LegacyFiles.Remove(dir));
            Assert.False(Directory.Exists(run));
            Assert.True(File.Exists(other));
            Assert.Equal(0, EasyTierElevatedSession.LegacyFiles.Remove(Path.Combine(dir, "missing")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>run-lan 里的联接（junction）指到别处：不跟过去，那边的同名文件留着。</summary>
    [Fact]
    public void LegacyFilesSkipJunctions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "amber-legacy-junction-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, "run-lan");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "args.json");
        File.WriteAllText(target, "keep");
        var link = Path.Combine(dir, "a1b2c3d4");
        try
        {
            using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
                mk.WaitForExit(10_000);
            Assert.True(Directory.Exists(link));
            Assert.Equal(0, EasyTierElevatedSession.LegacyFiles.Remove(dir));
            Assert.Equal("keep", File.ReadAllText(target));

            // 整个 run-lan 是联接也不碰。
            var linkedRoot = Path.Combine(root, "linked-run-lan");
            using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkedRoot}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
                mk.WaitForExit(10_000);
            Assert.Equal(0, EasyTierElevatedSession.LegacyFiles.Remove(linkedRoot));
            Assert.Equal("keep", File.ReadAllText(target));
            Directory.Delete(linkedRoot);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
