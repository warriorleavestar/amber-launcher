using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>执行生产 PowerShell 函数，但所有网卡/路由命令都由内存模型替代，不提权也不触碰真实网络。</summary>
public class ExitRoutingScriptTests
{
    [Fact]
    public Task OwnsOnlyNewShortLeasesAndRenewsThem() => RunAsync("""
        $borrowed = Add-Route '198.51.100.2/32' 12 '192.168.1.1' 42
        Enter-ExitRoutes (Plan @('198.51.100.1','198.51.100.2'))
        Check ($script:exitRoutes.Count -eq 2) 'track both routes'
        Check ($script:created -eq 1) 'reuse existing route'
        $ours = @($script:routes | Where-Object DestinationPrefix -eq '198.51.100.1/32')[0]
        Check ($ours.ValidLifetime.TotalSeconds -eq 120 -and $ours.PreferredLifetime.TotalSeconds -eq 120) 'finite equal lease lifetimes'
        Renew-ExitRoutes
        Check ($script:renewed -eq 0) 'renew only when due'
        $script:exitRenewed = [DateTime]::UtcNow.AddSeconds(-31)
        Renew-ExitRoutes
        Check ($script:renewed -eq 1) 'never renew a borrowed route'
        Exit-ExitRoutes
        Check ($script:removed -eq 1 -and $script:routes.Contains($borrowed)) 'remove only owned route'
        Check ($script:exitRoutes.Count -eq 0) 'cleanup is complete'
        Exit-ExitRoutes
        Check ($script:removed -eq 1) 'cleanup is idempotent'
        """);

    [Fact]
    public Task ConcurrentCreationFailureDoesNotDeleteForeignRoute() => RunAsync("""
        $script:failPrefix = '198.51.100.2/32'
        $caught = $false
        try { Enter-ExitRoutes (Plan @('198.51.100.1','198.51.100.2')) } catch { $caught = $true }
        Check $caught 'creation must fail'
        Exit-ExitRoutes
        Check ($script:routes.Count -eq 1 -and $script:routes[0].DestinationPrefix -eq '198.51.100.2/32') 'retain racing third party route'
        """);

    [Fact]
    public Task ReadinessRequiresVirtualAddressAndBothCaptureRoutes() => RunAsync("""
        Enter-ExitRoutes (Plan @())
        Add-Tun
        $script:ips = @([pscustomobject]@{ IPAddress='10.144.144.2'; InterfaceIndex=55 })
        Add-Route '0.0.0.0/1' 55 '0.0.0.0' 9000 | Out-Null
        $caught = $false
        try { Wait-ExitReady (Plan @()) ([pscustomobject]@{HasExited=$false}) 0.05 | Out-Null } catch { $caught = $true }
        Check $caught 'one half route is not ready'
        Check ($script:exitAdapter.InterfaceGuid -eq 'tun-guid') 'record partial capture for rollback'
        Add-Route '128.0.0.0/1' 55 '0.0.0.0' 9000 | Out-Null
        $adapter = Wait-ExitReady (Plan @()) ([pscustomobject]@{HasExited=$false}) 1
        Check ($adapter.InterfaceGuid -eq 'tun-guid') 'exact virtual adapter is ready'
        """);

    [Fact]
    public Task BypassIsRetainedUntilCaptureRoutesDisappear() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        Add-Tun
        $script:exitAdapter = $script:adapters[1]
        $capture = Add-Route '0.0.0.0/1' 55 '0.0.0.0' 9000
        $caught = $false
        try { Wait-ExitReleased 0.05 } catch { $caught = $true }
        Check $caught 'capture still present'
        $caught = $false
        try { Exit-ExitRoutes } catch { $caught = $true }
        Check ($caught -and $script:removed -eq 0) 'must not release transport under active capture'
        $script:routes.Remove($capture) | Out-Null
        Wait-ExitReleased
        Exit-ExitRoutes
        Check ($script:removed -eq 1) 'remove bypass after capture release'
        """);

    [Fact]
    public Task AdapterReplacementAndExternalEditsAreNotOverwritten() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1','198.51.100.2'))
        $script:routes[0].RouteMetric = 77
        $script:adapters[0].InterfaceGuid = 'replacement-guid'
        Exit-ExitRoutes
        Check ($script:removed -eq 0 -and $script:routes.Count -eq 2) 'adapter identity and route identity must match'
        """);

    [Fact]
    public Task LostBypassIsReportedInsteadOfQuietlyContinuing() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        $script:routes.Clear()
        $script:exitRenewed = [DateTime]::UtcNow.AddSeconds(-31)
        $caught = $false
        try { Renew-ExitRoutes } catch { $caught = $true }
        Check $caught 'renewal detects missing bypass'
        """);

    [Fact]
    public Task RouteReplacedByLongLivedEntryIsNotDeleted() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        $script:routes[0].ValidLifetime = [TimeSpan]::MaxValue
        Exit-ExitRoutes
        Check ($script:removed -eq 0 -and $script:routes.Count -eq 1) 'do not delete an externally replaced permanent route'
        """);

    [Fact]
    public Task RouteReadFailureCannotBeMistakenForCaptureRemoval() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        Add-Tun
        $script:exitAdapter = $script:adapters[1]
        $script:failRead = $true
        $caught = $false
        try { Wait-ExitReleased 0.05 } catch { $caught = $true }
        Check ($caught -and $null -ne $script:exitAdapter) 'read errors must retain capture identity'
        Check ($script:removed -eq 0) 'read failure must not drop bypass'
        """);

    [Fact]
    public Task RejectsVirtualUnderlayAndDeadCore() => RunAsync("""
        Add-Tun
        $script:adapters[1].Name = 'Amber LAN-12345'
        $script:underlayIndex = 55
        $caught = $false
        try { Enter-ExitRoutes (Plan @('198.51.100.1')) } catch { $caught = $true }
        Check ($caught -and $script:created -eq 0) 'never bootstrap underlay through own tunnel'
        $caught = $false
        try { Wait-ExitReady (Plan @()) ([pscustomobject]@{HasExited=$true}) 1 } catch { $caught = $true }
        Check $caught 'dead core cannot be ready'
        """);

    [Theory]
    [InlineData("$script:adapters[1].DriverFileName='netfake.sys'")]
    [InlineData("$script:adapters[1].DriverDescription='Other virtual adapter'")]
    [InlineData("$script:adapters[1].InterfaceGuid='wifi-guid'")]
    [InlineData("$script:ips += [pscustomobject]@{IPAddress='10.144.144.2'; InterfaceIndex=12}")]
    public Task RefusesWrongOrAmbiguousTunnelIdentity(string mutation) => RunAsync("""
        Enter-ExitRoutes (Plan @())
        Add-Tun
        $script:ips = @([pscustomobject]@{IPAddress='10.144.144.2'; InterfaceIndex=55})
        Add-Route '0.0.0.0/1' 55 '0.0.0.0' 9000 | Out-Null
        Add-Route '128.0.0.0/1' 55 '0.0.0.0' 9000 | Out-Null
        """ + "\n" + mutation + "\n" + """
        $caught = $false
        try { Wait-ExitReady (Plan @()) ([pscustomobject]@{HasExited=$false}) 0.1 | Out-Null } catch { $caught=$true }
        Check $caught 'must reject foreign or ambiguous tunnel'
        """);

    [Fact]
    public Task ExistingNamedAdapterIsRejectedBeforeRouteChanges() => RunAsync("""
        Add-Tun
        $caught = $false
        try { Enter-ExitRoutes (Plan @('198.51.100.1')) } catch { $caught=$true }
        Check ($caught -and $script:created -eq 0) 'never adopt an existing named tunnel'
        """);

    [Fact]
    public Task CaptureCleanupTracksGuidWhenInterfaceIndexChanges() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        Add-Tun
        $script:exitAdapter = $script:adapters[1].PSObject.Copy()
        $script:adapters[1].ifIndex = 56
        $script:adapters[1].InterfaceIndex = 56
        Add-Route '0.0.0.0/1' 56 '0.0.0.0' 9000 | Out-Null
        $caught=$false
        try { Wait-ExitReleased 0.05 } catch { $caught=$true }
        Check ($caught -and $null -ne $script:exitAdapter -and $script:removed -eq 0) 'same GUID at new index is still our active capture'
        """);

    [Fact]
    public Task EarlyReadinessFailureStillWaitsForNewCaptureToRelease() => RunAsync("""
        Enter-ExitRoutes (Plan @('198.51.100.1'))
        Add-Tun
        Add-Route '0.0.0.0/1' 55 '0.0.0.0' 9000 | Out-Null
        Check ($null -eq $script:exitAdapter) 'readiness has not recorded adapter yet'
        $caught=$false
        try { Wait-ExitReleased 0.05 } catch { $caught=$true }
        Check ($caught -and $null -ne $script:exitAdapter -and $script:removed -eq 0) 'partial initialization must retain bypass until capture disappears'
        """);

    private const string Mocks = """
        $script:routes = New-Object 'System.Collections.Generic.List[object]'
        $script:adapters = @(
            [pscustomobject]@{ InterfaceIndex=12; ifIndex=12; InterfaceGuid='wifi-guid'; Name='WLAN' }
        )
        $script:ips = @()
        $script:created = 0; $script:renewed = 0; $script:removed = 0
        $script:failPrefix = ''; $script:underlayIndex = 12; $script:failRead = $false
        function Check($condition, [string]$message) { if (-not $condition) { throw $message } }
        function Plan($addresses) { return [pscustomobject]@{VirtualIp='10.144.144.2'; UnderlayAddresses=@($addresses)} }
        function Add-Tun { $script:adapters += [pscustomobject]@{ InterfaceIndex=55; ifIndex=55; InterfaceGuid='tun-guid'; Name='Amber LAN'; DriverFileName='wintun.sys'; DriverDescription='Wintun Userspace Tunnel' } }
        function Add-Route($prefix, $index, $hop, $metric) {
            $r = [pscustomobject]@{ DestinationPrefix=$prefix; InterfaceIndex=$index; NextHop=$hop; RouteMetric=$metric; ValidLifetime=[TimeSpan]::MaxValue; PreferredLifetime=[TimeSpan]::MaxValue }
            $script:routes.Add($r)
            return $r
        }
        function Get-NetAdapter { param($InterfaceIndex, [switch]$IncludeHidden, $ErrorAction)
            $script:adapters | Where-Object { -not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex }
        }
        function Find-NetRoute { param($RemoteIPAddress, $ErrorAction)
            [pscustomobject]@{ IPAddress='192.168.1.20'; InterfaceIndex=$script:underlayIndex }
            [pscustomobject]@{ DestinationPrefix='0.0.0.0/0'; InterfaceIndex=$script:underlayIndex; NextHop='192.168.1.1'; RouteMetric=0 }
        }
        function Get-NetRoute { param($AddressFamily, $DestinationPrefix, $InterfaceIndex, $PolicyStore, $ErrorAction)
            if ($script:failRead) { throw 'simulated CIM failure' }
            Check ($PolicyStore -eq 'ActiveStore' -and $AddressFamily -eq 'IPv4') 'only active IPv4 routes'
            $script:routes | Where-Object { (-not $DestinationPrefix -or $_.DestinationPrefix -eq $DestinationPrefix) -and (-not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex) }
        }
        function New-NetRoute { param($DestinationPrefix, $InterfaceIndex, $NextHop, $RouteMetric, $PolicyStore, $ValidLifetime, $PreferredLifetime, $ErrorAction)
            Check ($PolicyStore -eq 'ActiveStore') 'route must not be persistent'
            $r = Add-Route $DestinationPrefix $InterfaceIndex $NextHop $RouteMetric
            if ($DestinationPrefix -eq $script:failPrefix) { throw 'external route won the creation race' }
            $script:created++
            $r.ValidLifetime = $ValidLifetime; $r.PreferredLifetime = $PreferredLifetime
            return $r
        }
        function Set-NetRoute { param($InputObject, $ValidLifetime, $PreferredLifetime, $ErrorAction)
            $script:renewed++; $InputObject.ValidLifetime=$ValidLifetime; $InputObject.PreferredLifetime=$PreferredLifetime
        }
        function Remove-NetRoute { param($InputObject, $Confirm, $ErrorAction)
            $script:removed++; $script:routes.Remove($InputObject) | Out-Null
        }
        function Get-NetIPAddress { param($AddressFamily, $IPAddress, $PolicyStore, $ErrorAction)
            $script:ips | Where-Object { -not $IPAddress -or $_.IPAddress -eq $IPAddress }
        }
        function Start-Sleep { param($Milliseconds) }
        """;

    private static async Task RunAsync(string assertions)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = "$ErrorActionPreference='Stop'\n" + ExitRoutingScript.Script + "\n" + Mocks + "\n" + assertions + "\nWrite-Output 'PASS'";
        var path = Path.Combine(Path.GetTempPath(), "amber-route-test-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
        try
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.True(process.ExitCode == 0, output + "\n" + await stderr);
            Assert.Contains("PASS", output);
        }
        finally { File.Delete(path); }
    }
}
