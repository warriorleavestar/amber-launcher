using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>在非提权 PowerShell 中运行生产 DNS 脚本；所有网络、注册表和缓存命令均由内存替身接管。</summary>
public sealed class ExitDnsScriptTests
{
    private const string Mocks = """
        $script:Guid = 'b7074062-27ac-47df-8a18-dc90aa0a50d1'
        $script:Index = 42
        $script:Adapters = @(
            [pscustomobject]@{ InterfaceGuid = $script:Guid; InterfaceIndex = 42; ifIndex = 42; Name = 'Amber LAN' },
            [pscustomobject]@{ InterfaceGuid = '97595f35-0796-47c9-89cb-3916afce2061'; InterfaceIndex = 7; ifIndex = 7; Name = 'Wi-Fi' }
        )
        $script:Servers = @('9.9.9.9', '149.112.112.112')
        $script:NameServer = '9.9.9.9,149.112.112.112'
        $script:DhcpServers = @('192.0.2.53')
        $script:Automatic = 'Disabled'
        $script:Metric = 37
        $script:DnsWrites = 0
        $script:DnsResets = 0
        $script:MetricWrites = 0
        $script:CacheCalls = 0
        $script:DnsFailBefore = 0
        $script:DnsFailAfter = 0
        $script:MetricFailAfter = 0
        $script:CacheFail = 0
        $script:RestoreFail = 0
        $script:IgnoreDnsWrite = $false
        $script:SnapshotFail = $false

        function Assert-That([bool]$condition, [string]$message) {
            if (-not $condition) { throw ('Mock assertion failed: ' + $message) }
        }
        function Assert-Original {
            Assert-That (($script:Servers -join ',') -eq '9.9.9.9,149.112.112.112') 'static DNS was not restored'
            Assert-That ($script:NameServer -eq '9.9.9.9,149.112.112.112') 'static DNS mode was not restored'
            Assert-That ($script:Automatic -eq 'Disabled' -and $script:Metric -eq 37) 'manual metric was not restored'
            Assert-That ($null -eq $script:ExitDnsLease) 'lease was not released'
        }
        function Expect-Failure([scriptblock]$action) {
            $failed = $false
            try { & $action } catch { $failed = $true }
            Assert-That $failed 'operation should fail'
        }
        function Get-NetAdapter {
            [CmdletBinding()] param([switch]$IncludeHidden)
            return $script:Adapters
        }
        function Get-DnsClientServerAddress {
            [CmdletBinding()] param([uint32]$InterfaceIndex, [string]$AddressFamily)
            Assert-That ($InterfaceIndex -eq $script:Index -and $AddressFamily -eq 'IPv4') 'read touched another adapter or address family'
            return [pscustomobject]@{ InterfaceIndex = $InterfaceIndex; AddressFamily = 2; ServerAddresses = @($script:Servers) }
        }
        function Get-ItemProperty {
            [CmdletBinding()] param([string]$LiteralPath)
            Assert-That ($LiteralPath -eq ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{' + $script:Guid + '}')) 'registry read touched another adapter'
            if ($script:SnapshotFail) { throw 'mock snapshot failed' }
            return [pscustomobject]@{ NameServer = $script:NameServer }
        }
        function Set-DnsClientServerAddress {
            [CmdletBinding()] param($InputObject, [string[]]$ServerAddresses, [switch]$ResetServerAddresses)
            Assert-That ($InputObject.InterfaceIndex -eq $script:Index -and $InputObject.AddressFamily -eq 2) 'DNS write touched another adapter or address family'
            $script:DnsWrites++
            if ($script:DnsFailBefore -gt 0) { $script:DnsFailBefore--; throw 'mock DNS write failed before applying' }
            $restoring = $ResetServerAddresses -or ($ServerAddresses -join ',') -eq '9.9.9.9,149.112.112.112'
            if ($restoring -and $script:RestoreFail -gt 0) { $script:RestoreFail--; throw 'mock restore failed' }
            if ($script:IgnoreDnsWrite) { return }
            if ($ResetServerAddresses) {
                $script:DnsResets++
                $script:Servers = @($script:DhcpServers)
                $script:NameServer = ''
            } else {
                $script:Servers = @($ServerAddresses)
                $script:NameServer = $ServerAddresses -join ','
            }
            if ($script:DnsFailAfter -gt 0) { $script:DnsFailAfter--; throw 'mock DNS write failed after applying' }
        }
        function Get-NetIPInterface {
            [CmdletBinding()] param([uint32]$InterfaceIndex, [string]$AddressFamily)
            Assert-That ($InterfaceIndex -eq $script:Index -and $AddressFamily -eq 'IPv4') 'metric read touched another adapter or address family'
            return [pscustomobject]@{ AutomaticMetric = $script:Automatic; InterfaceMetric = $script:Metric }
        }
        function Set-NetIPInterface {
            [CmdletBinding()] param([uint32]$InterfaceIndex, [string]$AddressFamily, [string]$AutomaticMetric, [uint32]$InterfaceMetric)
            Assert-That ($InterfaceIndex -eq $script:Index -and $AddressFamily -eq 'IPv4') 'metric write touched another adapter or address family'
            $script:MetricWrites++
            $script:Automatic = $AutomaticMetric
            if ($AutomaticMetric -eq 'Enabled') { $script:Metric = 95 } else { $script:Metric = $InterfaceMetric }
            if ($script:MetricFailAfter -gt 0) { $script:MetricFailAfter--; throw 'mock metric write failed after applying' }
        }
        function Clear-DnsClientCache {
            [CmdletBinding()] param()
            $script:CacheCalls++
            if ($script:CacheFail -gt 0) { $script:CacheFail--; throw 'mock cache clear failed' }
        }
        """;

    private static async Task RunAsync(string body)
    {
        if (!OperatingSystem.IsWindows()) return;
        // 禁用自动加载网络模块，遗漏的替身不能悄悄调用真实网络命令。只加载常用的内置命令。
        const string bootstrap = "Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management -ErrorAction Stop; "
            + "$PSModuleAutoLoadingPreference = 'None'; $ErrorActionPreference = 'Stop'; "
            + "try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())) } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(Mocks + "\n" + ExitDnsScript.Script + "\n" + body);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + "\n" + await errors);
    }

    [Fact]
    public Task StaticDnsAndManualMetricRoundTrip() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        Assert-That (($script:Servers -join ',') -eq '1.1.1.1,8.8.8.8') 'exit DNS was not applied'
        Assert-That ($script:Automatic -eq 'Disabled' -and $script:Metric -eq 1) 'exit metric was not applied'
        Exit-ExitDns
        Assert-Original
        Assert-That ($script:DnsWrites -eq 2 -and $script:MetricWrites -eq 2 -and $script:CacheCalls -eq 2) 'unexpected mutation count'
        """);

    [Fact]
    public Task DhcpAndAutomaticMetricAreRestoredWithoutFreezingOldValues() => RunAsync("""
        $script:NameServer = ''
        $script:Servers = @('192.0.2.1')
        $script:Automatic = 'Enabled'
        Enter-ExitDns $script:Adapters[0]
        Exit-ExitDns
        Assert-That ($script:NameServer -eq '' -and ($script:Servers -join ',') -eq '192.0.2.53') 'DHCP DNS was replaced with a static snapshot'
        Assert-That ($script:DnsResets -eq 1) 'DHCP reset was not used'
        Assert-That ($script:Automatic -eq 'Enabled' -and $script:Metric -eq 95) 'automatic metric was frozen'
        Assert-That ($null -eq $script:ExitDnsLease) 'lease was not released'
        """);

    [Fact]
    public Task NewAdapterWithNoDnsServersReturnsToAnEmptyDynamicConfiguration() => RunAsync("""
        $script:NameServer = $null
        $script:Servers = @()
        $script:DhcpServers = @()
        Enter-ExitDns $script:Adapters[0]
        Exit-ExitDns
        Assert-That ($script:Servers.Count -eq 0 -and [string]::IsNullOrWhiteSpace($script:NameServer)) 'empty DNS configuration was not restored'
        Assert-That ($script:DnsResets -eq 1 -and $null -eq $script:ExitDnsLease) 'empty dynamic DNS did not use reset'
        """);

    [Fact]
    public Task DuplicateCallsAreIdempotentAndRestorationUsesGuidAfterIndexChanges() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        Enter-ExitDns $script:Adapters[0]
        Assert-That ($script:DnsWrites -eq 1 -and $script:MetricWrites -eq 1) 'duplicate entry applied settings again'
        $script:Adapters[0].InterfaceIndex = 99
        $script:Adapters[0].ifIndex = 99
        $script:Index = 99
        Exit-ExitDns
        Exit-ExitDns
        Assert-Original
        Assert-That ($script:DnsWrites -eq 2 -and $script:MetricWrites -eq 2) 'duplicate cleanup changed settings again'
        """);

    [Fact]
    public Task UserDnsChangesArePreservedWhileOwnedMetricIsRestored() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        $script:Servers = @('4.2.2.2')
        $script:NameServer = '4.2.2.2'
        Exit-ExitDns
        Assert-That (($script:Servers -join ',') -eq '4.2.2.2' -and $script:DnsWrites -eq 1) 'user DNS was overwritten'
        Assert-That ($script:Metric -eq 37 -and $script:MetricWrites -eq 2) 'owned metric was not restored'
        Assert-That ($null -eq $script:ExitDnsLease) 'lease was not released'
        """);

    [Fact]
    public Task UserDhcpAndMetricChangesArePreservedEvenWhenEffectiveDnsMatches() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        $script:NameServer = ''
        $script:Automatic = 'Disabled'
        $script:Metric = 73
        Exit-ExitDns
        Assert-That ($script:NameServer -eq '' -and $script:DnsWrites -eq 1) 'user DHCP mode was overwritten'
        Assert-That ($script:Metric -eq 73 -and $script:MetricWrites -eq 1) 'user metric was overwritten'
        Assert-That ($null -eq $script:ExitDnsLease) 'lease was not released'
        """);

    [Fact]
    public Task DnsFailureAfterMutationRollsBack() => RunAsync("""
        $script:DnsFailAfter = 1
        Expect-Failure { Enter-ExitDns $script:Adapters[0] }
        Assert-Original
        Assert-That ($script:DnsWrites -eq 2 -and $script:MetricWrites -eq 0) 'partial DNS setup was not rolled back'
        """);

    [Fact]
    public Task MetricFailureAfterMutationRollsBackBothSettings() => RunAsync("""
        $script:MetricFailAfter = 1
        Expect-Failure { Enter-ExitDns $script:Adapters[0] }
        Assert-Original
        Assert-That ($script:DnsWrites -eq 2 -and $script:MetricWrites -eq 2) 'partial metric setup was not rolled back'
        """);

    [Fact]
    public Task CacheFailureRollsBackBothSettings() => RunAsync("""
        $script:CacheFail = 1
        Expect-Failure { Enter-ExitDns $script:Adapters[0] }
        Assert-Original
        Assert-That ($script:CacheCalls -eq 2) 'cleanup did not retry cache invalidation'
        """);

    [Fact]
    public Task FailedDnsCleanupStillRestoresMetricAndCanRetry() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        $script:RestoreFail = 1
        Expect-Failure { Exit-ExitDns }
        Assert-That ($script:Metric -eq 37) 'one cleanup failure prevented metric restoration'
        Assert-That ($null -ne $script:ExitDnsLease) 'failed cleanup lease was lost'
        Exit-ExitDns
        Assert-Original
        Assert-That ($script:MetricWrites -eq 2) 'retry applied metric twice'
        """);

    [Fact]
    public Task MissingGuidNeverRestoresIntoAReplacementAtTheSameIndex() => RunAsync("""
        Enter-ExitDns $script:Adapters[0]
        $script:Adapters[0].InterfaceGuid = 'eebd3b0a-cc18-4939-b649-d8c9045b0a9f'
        Exit-ExitDns
        Assert-That ($script:DnsWrites -eq 1 -and $script:MetricWrites -eq 1) 'replacement adapter was modified'
        Assert-That ($null -eq $script:ExitDnsLease) 'missing adapter lease was not released'
        """);

    [Fact]
    public Task ReadbackMismatchFailsInsteadOfReportingConfigured() => RunAsync("""
        $script:IgnoreDnsWrite = $true
        Expect-Failure { Enter-ExitDns $script:Adapters[0] }
        Assert-Original
        Assert-That ($script:MetricWrites -eq 0) 'readback failure continued applying settings'
        """);

    [Fact]
    public Task SnapshotFailureDoesNotModifyAnySetting() => RunAsync("""
        $script:SnapshotFail = $true
        Expect-Failure { Enter-ExitDns $script:Adapters[0] }
        Assert-That ($script:DnsWrites -eq 0 -and $script:MetricWrites -eq 0 -and $script:CacheCalls -eq 0) 'snapshot failure modified settings'
        Assert-That ($null -eq $script:ExitDnsLease) 'snapshot failure created a lease'
        """);
}
