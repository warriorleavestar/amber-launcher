using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amber.Core;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>仅运行未提权的假核心和守护；热点的 COM、网卡和 BFE 边界全部不接触真实系统。</summary>
public sealed class HotspotGuardTests
{
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public void HotspotRequestContainsOnlyTheExplicitSwitch()
    {
        var json = JsonSerializer.Serialize(new GuardCommand("hotspot", 7, "", null, Hotspot: true), AmberJsonContext.Default.GuardCommand);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("hotspot", document.RootElement.GetProperty("cmd").GetString());
        Assert.True(document.RootElement.GetProperty("hotspot").GetBoolean());
        Assert.Equal(7, document.RootElement.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("network").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("env").ValueKind);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task NoExitRejectsSharingWithoutStoppingTheExistingCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var guard = ElevatedGuard.Start(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'core-ready'; Start-Sleep 60"],
            new Dictionary<string, string>(), elevate: false);
        await guard.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<IOException>(() => guard.SetHotspotSharingAsync(true).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("remote exit", error.Message);
        Assert.False(guard.HasExited);
        Assert.False(guard.HotspotSharingActive);
        Assert.False(guard.StartPending);
        await guard.SetHotspotSharingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
        await guard.SetHotspotSharingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(guard.HotspotSharingActive);
        guard.Dispose();
        Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeardownRestoresSharingBeforeDestroyingTunAndAlwaysReleasesTheCore(bool failSharingCleanup)
    {
        if (!OperatingSystem.IsWindows()) return;
        var begin = ElevatedGuard.GuardScript.IndexOf("function Stop-Session {", StringComparison.Ordinal);
        var end = ElevatedGuard.GuardScript.IndexOf("function Start-Core", begin, StringComparison.Ordinal);
        var production = ElevatedGuard.GuardScript[begin..end];
        var body = """
            $ErrorActionPreference = 'Stop'
            $PSModuleAutoLoadingPreference = 'None'
            $script:events = New-Object 'Collections.Generic.List[string]'
            $setupLoaded = $true
            $script:activeExitPlan = 'active'
            $ack = New-Object IO.StringWriter
            function Exit-HotspotSharing { $script:events.Add('hotspot'); if ($failCleanup) { throw 'mock sharing restore failed' } }
            function Start-Sleep { }
            function Write-GuardError($message) { $script:events.Add('error') }
            function Forward-Warning($message) { }
            function Exit-ExitDns { $script:events.Add('dns') }
            function Stop-Core { $script:events.Add('core') }
            function Wait-ExitReleased { $script:events.Add('routes-ready') }
            function Exit-ExitRoutes { $script:events.Add('routes') }
            function Exit-ExitTrafficPolicy { $script:events.Add('policy') }
            """ + $"\n$failCleanup = ${(failSharingCleanup ? "true" : "false")}\n" + production + """

            $failed = $false
            try { Stop-Session } catch { $failed = $true }
            if ($failed -ne $failCleanup) { throw 'Cleanup failure was hidden or fabricated' }
            $actual = @($script:events | Where-Object { $_ -ne 'error' }) -join ','
            $expected = if ($failCleanup) { 'hotspot,hotspot,dns,core,routes-ready,routes,policy' } else { 'hotspot,dns,core,routes-ready,routes,policy' }
            if ($actual -ne $expected) { throw ('Bad teardown order: ' + $actual) }
            if ($script:activeExitPlan) { throw 'Exit plan survived teardown' }
            if (-not $ack.ToString().Contains('hotspot off')) { throw 'Missing inactive acknowledgement' }
            """;
        var info = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        const string bootstrap = "Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management -ErrorAction Stop; try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())) } catch { [Console]::Error.WriteLine($_); exit 1 }";
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)));
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(body);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
    }
}
