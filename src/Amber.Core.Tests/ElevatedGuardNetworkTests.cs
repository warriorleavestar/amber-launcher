using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ElevatedGuardNetworkTests
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task FailedCoreStartupIsAcknowledgedToCaller()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), [], new Dictionary<string, string>(), elevate: false);
        // 启动结果一定会有：守护脚本回“failed”、连不上管道（ConnectTimeout 后）或者守护进程退出，三者之一。
        // 以前这里自己掐 20 秒，比产品自己的 30 秒连接期限还短：机器忙、PowerShell 启动慢时测试先超时，
        // 抛的是 TimeoutException 而不是要测的 IOException。现在等产品自己给出结果，外面这个期限只防挂死。
        var settle = ElevatedGuard.ConnectTimeout + TimeSpan.FromMinutes(2);
        var error = await Assert.ThrowsAsync<IOException>(() => guard.WaitForReadyAsync().WaitAsync(settle));
        Assert.NotEmpty(error.Message);
        Assert.True(guard.WaitForExit(settle));
        Assert.False(guard.StartPending);
        Assert.NotEmpty(guard.RecentOutput);
    }

    [Fact]
    public async Task KillingGuardAlsoKillsCoreWithoutFinally()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Write-Output ('pid=' + $PID); Start-Sleep 60"], new Dictionary<string, string>(), elevate: false);
        await guard.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        while ((line = guard.RecentOutput.FirstOrDefault(x => x.StartsWith("pid=", StringComparison.Ordinal))) is null) await Task.Delay(50, deadline.Token);
        var pid = int.Parse(line[4..]);
        using var core = Process.GetProcessById(pid);
        using var owner = Process.GetProcessById(guard.ProcessId);
        owner.Kill(); // 故意不用 entireProcessTree，子进程只能由 Job 回收。
        await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(core.HasExited);
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void BootCommandStaysBelowWindowsLimitAndOnlyLoadsPinnedSetup()
    {
        var bootstrap = ElevatedGuard.GuardScript
            .Replace("__ID__", new string('a', 32)).Replace("__AMBERPID__", "999999")
            .Replace("__SID__", "S-1-5-21-1234567890-1234567890-1234567890-1234")
            .Replace("__TIMEOUT__", "20").Replace("__SETUPHASH__", new string('b', 64))
            .Replace("__CORE64__", Convert.ToBase64String(Encoding.Unicode.GetBytes("C:\\" + new string('x', 250) + "\\core.exe")));
        Assert.True(Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)).Length + 100 < 32767);
        Assert.True(ElevatedGuard.SetupScript.Length > 10000);
        Assert.True(ElevatedGuard.GuardScript.IndexOf("$hash -cne '__SETUPHASH__'", StringComparison.Ordinal) < ElevatedGuard.GuardScript.IndexOf("ScriptBlock]::Create", StringComparison.Ordinal));
    }
}
