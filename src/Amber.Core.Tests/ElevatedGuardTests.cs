using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
        // 先把冒充者连到已创建的管道，再启动真守护进程，避免靠 PowerShell 启动速度抢连接。
        // 反射只用于安排夹具：管道 ACL、PID 认证和守护脚本仍直接使用生产实现。
        var id = Guid.NewGuid().ToString("N");
        var createPipe = typeof(ElevatedGuard).GetMethod("CreatePipe", BindingFlags.Static | BindingFlags.NonPublic)!;
        NamedPipeServerStream Pipe(string kind, PipeDirection direction) =>
            (NamedPipeServerStream)createPipe.Invoke(null, [$"amber-et-{id}-{kind}", direction])!;
        using var cmd = Pipe("cmd", PipeDirection.Out);
        using var output = Pipe("out", PipeDirection.In);
        using var error = Pipe("err", PipeDirection.In);
        using var ack = Pipe("ack", PipeDirection.In);
        using var impostor = new NamedPipeClientStream(".", $"amber-et-{id}-cmd", PipeDirection.In);
        await impostor.ConnectAsync(5000);

        using var identity = WindowsIdentity.GetCurrent();
        var script = ElevatedGuard.GuardScript
            .Replace("__ID__", id)
            .Replace("__AMBERPID__", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SID__", identity.User!.Value)
            .Replace("__TIMEOUT__", ElevatedGuard.GuardConnectSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SETUPHASH__", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ElevatedGuard.SetupScript))))
            .Replace("__CORE64__", Convert.ToBase64String(Encoding.Unicode.GetBytes(Core)));
        var commandLine = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process? process = null;
        ElevatedGuard? guard = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(Core, commandLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            guard = (ElevatedGuard)Activator.CreateInstance(typeof(ElevatedGuard), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [process, commandLine, new[] { cmd, output, error, ack }, ElevatedGuard.ConnectTimeout], null)!;
            guard.Start(FakeCoreArgs("impostor"), Env);

            var buffer = new byte[4096];
            var read = await impostor.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, read);
            Assert.False(guard.ConnectFailed);
            Assert.True(await WaitAsync(() => CorePid(guard, "impostor") is not null), string.Join(" | ", guard.RecentOutput));
            Assert.False(guard.HasExited);
            guard.Dispose();
            Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            guard?.Dispose();
            if (process is not null)
            {
                if (!process.WaitForExit(20_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                process.Dispose();
            }
        }
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

    /// <summary>Amber 等超时了（PowerShell 启动太慢）：报告失败；晚到的守护脚本一连上就被关掉管道，很快退出，不启动 easytier-core。</summary>
    [Fact]
    public async Task LateGuardAfterTimeoutIsReportedAndShutOut()
    {
        if (!OperatingSystem.IsWindows()) return;
        // easytier-core 一旦被启动就会建这个标记文件：超时后晚到的守护脚本不能启动它。
        var marker = Path.Combine(Path.GetTempPath(), "amber-late-core-" + Guid.NewGuid().ToString("N")[..8]);
        string[] args = ["-NoProfile", "-NonInteractive", "-Command", $"New-Item -ItemType File -Path '{marker}' | Out-Null; Start-Sleep 60"];
        var guard = ElevatedGuard.Start(Core, args, Env, elevate: false, connectTimeoutForTest: TimeSpan.FromMilliseconds(1));
        try
        {
            Assert.True(await WaitAsync(() => guard.ConnectFailed, 5));
            Assert.False(guard.StartPending); // 没真正发出去的重启不算“正在重启”
            // 守护脚本自己的期限是 20 秒；被关掉管道的话远早于这个时间退出。
            Assert.True(await WaitAsync(() => guard.HasExited, 12), "守护脚本没有被关在外面");
            await Task.Delay(1000);
            Assert.False(File.Exists(marker), "超时后 easytier-core 还是被启动了");
        }
        finally
        {
            guard.Dispose();
            try { File.Delete(marker); } catch (IOException) { }
        }
    }

    /// <summary>刚启动就 Dispose（比如端口被占用）：排着的启动命令不发，只发停止，easytier-core 一次都不启动。</summary>
    [Fact]
    public async Task EarlyDisposeNeverStartsCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        var guard = ElevatedGuard.Start(Core, FakeCoreArgs("early"), Env, elevate: false);
        guard.Dispose();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(30)));
        await Task.Delay(200);
        Assert.DoesNotContain(guard.RecentOutput, l => l.Contains("tag=early") || l.StartsWith("pid=", StringComparison.Ordinal));
    }

    /// <summary>守护脚本只把 ET_NETWORK_SECRET 放进 easytier-core 的环境变量，别的键不认。</summary>
    [Fact]
    public async Task OnlyAllowListedEnvironmentReachesCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        var env = new Dictionary<string, string>(Env) { ["PATHEXT"] = "evil", ["AMBER_EXTRA"] = "evil" };
        string[] args = ["-NoProfile", "-NonInteractive", "-Command", "Write-Output ('pid=' + $PID); Write-Output ('tag=env extra=' + $env:AMBER_EXTRA + ' pathext=' + $env:PATHEXT + ' secret=' + $env:ET_NETWORK_SECRET); Start-Sleep 60"];
        using var guard = ElevatedGuard.Start(Core, args, env, elevate: false);
        Assert.True(await WaitAsync(() => guard.RecentOutput.Any(l => l.Contains("tag=env"))), string.Join(" | ", guard.RecentOutput));
        var line = guard.RecentOutput.First(l => l.Contains("tag=env"));
        Assert.Contains("secret=" + Secret, line);
        Assert.DoesNotContain("evil", line);
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
        if (!OperatingSystem.IsWindows()) return;
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

    /// <summary>大文件分块覆盖；有别的硬链接的文件只删名字、不覆盖（覆盖会改到另一处的同一份内容）。</summary>
    [Fact]
    public void LegacyFilesChunkedAndSkipHardLinks()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "amber-legacy-link-" + Guid.NewGuid().ToString("N")[..8]);
        var run = Path.Combine(dir, "a1b2c3d4");
        Directory.CreateDirectory(run);
        var big = Path.Combine(run, "easytier.log");
        File.WriteAllBytes(big, Enumerable.Repeat((byte)'x', EasyTierElevatedSession.LegacyFiles.ChunkSize * 3 + 17).ToArray());
        var outside = Path.Combine(dir, "elsewhere.txt");
        File.WriteAllText(outside, Secret);
        var linked = Path.Combine(run, "args.json");
        Assert.True(CreateHardLink(linked, outside, IntPtr.Zero));
        try
        {
            Assert.Equal(2, EasyTierElevatedSession.LegacyFiles.Remove(dir));
            Assert.False(File.Exists(big));
            // 有别的硬链接：run-lan 里的名字删掉，但不覆盖，另一处的内容原样。
            Assert.False(File.Exists(linked));
            Assert.Equal(Secret, File.ReadAllText(outside));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

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
