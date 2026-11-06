using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>使用纯内存 COM 与网卡替身执行生产 PowerShell，不提权、不加载网络模块、不读取真实 ICS。</summary>
public sealed class HotspotSharingScriptTests
{
    private const string Mocks = """
        $script:PublicGuid = '04cccb89-5b68-4215-8cea-a60642cb958f'
        $script:PrivateGuid = '29fb5c51-f7b0-4c74-a5ac-fb3638c053d1'
        $script:OriginalGuid = 'cbc727f3-a3c6-4a73-998d-92b6f885fc67'
        $script:OtherGuid = 'a800a1cc-783d-4599-b1cb-44330fc349c7'
        $script:Writes = @()
        $script:Failure = ''
        $script:RestoreFail = 0
        $script:PolicyFail = $false
        $script:PolicyExitFail = 0
        $script:PolicyEnters = 0
        $script:PolicyExits = 0
        $script:PolicyActive = $false
        $script:ChangeDuringPolicy = $false

        function Check([bool]$condition, [string]$message) { if (-not $condition) { throw ('Mock assertion failed: ' + $message) } }
        function Expect-Failure([scriptblock]$action) {
            $failed = $false
            try { & $action } catch { $failed = $true }
            Check $failed 'operation must fail'
        }
        function New-Object { param([string]$ComObject) throw 'Real object creation is forbidden in the hotspot test' }
        function New-MockConnection([string]$guid, [bool]$enabled, [int]$kind) {
            $configuration = [pscustomobject]@{ OwnerGuid=$guid; SharingEnabled=$enabled; SharingConnectionType=$kind; Mappings=0 }
            $configuration | Add-Member ScriptMethod EnumPortMappings { param($flags) Check ($flags -eq 0) 'inspect all port mappings'; if ($this.Mappings -gt 0) { return [pscustomobject]@{Name='existing mapping'} } }
            $configuration | Add-Member ScriptMethod EnableSharing { param($kind) Set-MockPublic $this.OwnerGuid $kind }
            $configuration | Add-Member ScriptMethod DisableSharing { throw 'Must never disable the private hotspot or blindly clear sharing' }
            return [pscustomobject]@{ Properties=[pscustomobject]@{ Guid=$guid }; Configuration=$configuration }
        }
        $script:Connections = @(
            (New-MockConnection $script:PublicGuid $false 0),
            (New-MockConnection $script:PrivateGuid $true 1),
            (New-MockConnection $script:OriginalGuid $true 0),
            (New-MockConnection $script:OtherGuid $false 0)
        )
        $script:Manager = [pscustomobject]@{ SharingInstalled=$true; EnumEveryConnection=$script:Connections }
        $script:Manager | Add-Member ScriptMethod NetConnectionProps { param($connection) return $connection.Properties }
        $script:Manager | Add-Member ScriptMethod INetSharingConfigurationForINetConnection { param($connection) return $connection.Configuration }
        $script:Adapters = @(
            [pscustomobject]@{ InterfaceGuid=$script:PublicGuid; ifIndex=42; Status='Up'; DriverFileName='wintun.sys'; InterfaceDescription='Wintun Userspace Tunnel' },
            [pscustomobject]@{ InterfaceGuid=$script:PrivateGuid; ifIndex=70; Status='Up'; DriverFileName='vwifimp.sys'; InterfaceDescription='Microsoft Wi-Fi Direct Virtual Adapter #2' },
            [pscustomobject]@{ InterfaceGuid=$script:OriginalGuid; ifIndex=7; Status='Up'; DriverFileName='netwifi.sys'; InterfaceDescription='Physical Wi-Fi' }
        )
        $script:HotspotAddresses = @([pscustomobject]@{ IPAddress='192.168.137.1'; PrefixLength=24; AddressState='Preferred' })
        function Get-NetAdapter { [CmdletBinding()] param([switch]$IncludeHidden) Check $IncludeHidden 'must enumerate hidden virtual adapters'; return $script:Adapters }
        function Get-NetIPAddress {
            [CmdletBinding()] param([int]$InterfaceIndex, [string]$AddressFamily, [string]$PolicyStore)
            $private = @($script:Adapters | Where-Object { $_.InterfaceGuid -eq $script:PrivateGuid })[0]
            Check ($InterfaceIndex -eq $private.ifIndex -and $AddressFamily -eq 'IPv4' -and $PolicyStore -eq 'ActiveStore') 'read only hotspot active IPv4 addresses'
            return $script:HotspotAddresses
        }
        function Set-MockPublic([string]$guid, [int]$kind) {
            Check ($kind -eq 0) 'only the PUBLIC upstream may be changed'
            Check $script:PolicyActive 'forwarding policy must precede every shared-upstream mutation'
            $script:Writes += $guid
            if ($guid -eq $script:PublicGuid -and $script:Failure -eq 'before') { throw 'mock switch failed before write' }
            if ($guid -eq $script:OriginalGuid -and $script:RestoreFail -gt 0) { $script:RestoreFail--; throw 'mock restore failed' }
            if ($guid -eq $script:PublicGuid -and $script:Failure -eq 'ignore') { return }
            foreach ($item in $script:Connections) { if ($item.Configuration.SharingConnectionType -eq 0) { $item.Configuration.SharingEnabled = $false } }
            if ($guid -eq $script:PublicGuid -and $script:Failure -eq 'between') { throw 'mock switch failed after disabling old upstream' }
            $target = @($script:Connections | Where-Object { $_.Properties.Guid -eq $guid })[0]
            $target.Configuration.SharingConnectionType = 0
            $target.Configuration.SharingEnabled = $true
            if ($guid -eq $script:PublicGuid -and $script:Failure -eq 'after') { throw 'mock switch failed after write' }
        }
        function Enter-HotspotTrafficPolicy($adapter, $private) {
            Check ($adapter.InterfaceGuid -eq $script:PublicGuid -and $private.InterfaceGuid -eq $script:PrivateGuid) 'policy must use exact tunnel and hotspot'
            $script:PolicyEnters++
            $script:PolicyActive = $true
            if ($script:ChangeDuringPolicy) { $script:Connections[2].Configuration.SharingEnabled=$false; $script:Connections[3].Configuration.SharingEnabled=$true }
            if ($script:PolicyFail) { throw 'mock policy failed after partial setup' }
        }
        function Exit-HotspotTrafficPolicy {
            $script:PolicyExits++
            if ($script:PolicyExitFail -gt 0) { $script:PolicyExitFail--; throw 'mock policy cleanup failed' }
            Check $script:Connections[2].Configuration.SharingEnabled 'restore original upstream before removing forwarding boundary'
            $script:PolicyActive = $false
        }
        function Check-Original {
            Check (-not $script:Connections[0].Configuration.SharingEnabled) 'new PUBLIC must not remain enabled'
            Check $script:Connections[1].Configuration.SharingEnabled 'private hotspot must remain enabled'
            Check $script:Connections[2].Configuration.SharingEnabled 'original PUBLIC must be restored'
            Check (-not $script:HotspotSharingActive -and $null -eq $script:HotspotSharingLease) 'sharing lease must be released'
            Check (-not $script:PolicyActive) 'policy must be released'
        }
        """;

    private static async Task RunAsync(string body)
    {
        if (!OperatingSystem.IsWindows()) return;
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
        await process.StandardInput.WriteAsync(Mocks + "\n" + HotspotSharingScript.Script
            + "\nfunction New-HotspotSharingManager { return $script:Manager }\n" + body);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + "\n" + await errors);
    }

    [Fact]
    public Task RoundTripKeepsPrivateHotspotAndIsIdempotent() => RunAsync("""
        Assert-HotspotSharing
        Enter-HotspotSharing $script:Adapters[0]
        Enter-HotspotSharing $script:Adapters[0]
        Check $script:HotspotSharingActive 'sharing must be active only after readback'
        Check ($script:Writes.Count -eq 1 -and $script:PolicyEnters -eq 1) 'duplicate entry must not reconfigure'
        Assert-HotspotSharing
        Exit-HotspotSharing
        Exit-HotspotSharing
        Check-Original
        Check ($script:Writes.Count -eq 2 -and $script:PolicyExits -eq 1) 'one switch and one restoration'
        """);

    [Theory]
    [InlineData("before", 1)]
    [InlineData("between", 2)]
    [InlineData("after", 2)]
    [InlineData("ignore", 1)]
    public Task FailedOrIgnoredSwitchRestoresItsPartialState(string failure, int expectedWrites) => RunAsync(
        "$script:Failure = '" + failure + "'\n" + """
        Expect-Failure { Enter-HotspotSharing $script:Adapters[0] }
        Check-Original
        """ + "\nCheck ($script:Writes.Count -eq " + expectedWrites + ") 'unexpected mutation count'");

    [Theory]
    [InlineData("$script:Connections[1].Configuration.SharingEnabled=$false")]
    [InlineData("$script:Connections[2].Configuration.SharingEnabled=$false")]
    [InlineData("$script:Connections[3].Configuration.SharingEnabled=$true; $script:Connections[3].Configuration.SharingConnectionType=1")]
    [InlineData("$script:Connections[3].Configuration.SharingEnabled=$true")]
    [InlineData("$script:Connections[0].Configuration.SharingEnabled=$true")]
    [InlineData("$script:Adapters[1].DriverFileName='netethernet.sys'")]
    [InlineData("$script:Adapters[1].InterfaceDescription='Ethernet adapter'")]
    [InlineData("$script:Adapters[1].Status='Disconnected'")]
    [InlineData("$script:Adapters[0].DriverFileName='foreign.sys'")]
    [InlineData("$script:Manager.SharingInstalled=$false")]
    [InlineData("$script:Manager.EnumEveryConnection=@($script:Connections[1],$script:Connections[2])")]
    [InlineData("$script:Adapters += $script:Adapters[1]")]
    [InlineData("$script:Manager.EnumEveryConnection += $script:Connections[1]")]
    [InlineData("$script:HotspotAddresses=@()")]
    [InlineData("$script:HotspotAddresses[0].IPAddress='169.254.1.2'")]
    [InlineData("$script:HotspotAddresses[0].IPAddress='127.0.0.1'")]
    [InlineData("$script:HotspotAddresses[0].IPAddress='224.0.0.1'")]
    [InlineData("$script:HotspotAddresses[0].AddressState='Tentative'")]
    [InlineData("$script:HotspotAddresses[0].PrefixLength=0")]
    public Task UnsupportedOrAmbiguousConfigurationDoesNotMutate(string mutation) => RunAsync(mutation + "\n" + """
        Expect-Failure { Enter-HotspotSharing $script:Adapters[0] }
        Check ($script:Writes.Count -eq 0 -and $script:PolicyEnters -eq 0) 'preflight failure must not mutate settings'
        Check ($null -eq $script:HotspotSharingLease) 'preflight must not take ownership'
        """);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task ExistingPortMappingsPreventTakingOver(int index) => RunAsync(
        "$script:Connections[" + index + "].Configuration.Mappings=1\n" + """
        Expect-Failure { Enter-HotspotSharing $script:Adapters[0] }
        Check ($script:Writes.Count -eq 0 -and $script:PolicyEnters -eq 0) 'custom mapping must be preserved'
        """);

    [Fact]
    public Task PolicyFailureDoesNotSwitchAndUnwindsPartialPolicy() => RunAsync("""
        $script:PolicyFail=$true
        Expect-Failure { Enter-HotspotSharing $script:Adapters[0] }
        Check-Original
        Check ($script:Writes.Count -eq 0 -and $script:PolicyExits -eq 1) 'policy must be ready before any ICS switch'
        """);

    [Fact]
    public Task ChangeDuringPreparationIsNotOverwritten() => RunAsync("""
        $script:ChangeDuringPolicy=$true
        Expect-Failure { Enter-HotspotSharing $script:Adapters[0] }
        Check ($script:Writes.Count -eq 0 -and $script:Connections[3].Configuration.SharingEnabled) 'foreign sharing must not be overwritten'
        Check (-not $script:HotspotSharingActive) 'failure must not report sharing active'
        """);

    [Fact]
    public Task ExternalReplacementIsReportedAndNeverRestoredOver() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:Connections[0].Configuration.SharingEnabled=$false
        $script:Connections[3].Configuration.SharingEnabled=$true
        Expect-Failure { Assert-HotspotSharing }
        Expect-Failure { Exit-HotspotSharing }
        Check ($script:Writes.Count -eq 1 -and $script:Connections[3].Configuration.SharingEnabled) 'external sharing must not be replaced'
        Check (-not $script:HotspotSharingActive -and $null -ne $script:HotspotSharingLease) 'failed recovery must remain explicit'
        """);

    [Fact]
    public Task ExternalPortMappingIsNotDisabledByCleanup() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:Connections[0].Configuration.Mappings=1
        Expect-Failure { Assert-HotspotSharing }
        Expect-Failure { Exit-HotspotSharing }
        Check ($script:Writes.Count -eq 1 -and $script:Connections[0].Configuration.Mappings -eq 1) 'foreign port mapping must not be disabled'
        """);

    [Fact]
    public Task FailedRestorationRetainsLeaseForRetry() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:RestoreFail=1
        Expect-Failure { Exit-HotspotSharing }
        Check ($null -ne $script:HotspotSharingLease -and $script:PolicyActive) 'failed restoration must retain policy and lease'
        Exit-HotspotSharing
        Check-Original
        """);

    [Fact]
    public Task MissingOwnedTunnelAfterCoreExitStillRestoresOriginalUpstream() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:Connections[0].Configuration.SharingEnabled=$false
        $script:Manager.EnumEveryConnection=@($script:Connections[1], $script:Connections[2], $script:Connections[3])
        $script:Adapters=@($script:Adapters[1], $script:Adapters[2])
        Exit-HotspotSharing
        Check-Original
        """);

    [Fact]
    public Task PolicyCleanupRetryDoesNotSwitchIcsAgain() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:PolicyExitFail=1
        Expect-Failure { Exit-HotspotSharing }
        Check ($null -ne $script:HotspotSharingLease -and $script:Connections[2].Configuration.SharingEnabled) 'ICS restored even if policy cleanup fails'
        Exit-HotspotSharing
        Check-Original
        Check ($script:Writes.Count -eq 2 -and $script:PolicyExits -eq 2) 'cleanup retry must not switch ICS twice'
        """);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public Task ChangedIndexStopsSharingWhileCleanupStillUsesGuid(int index) => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        """ + "\n$script:Adapters[" + index + "].ifIndex=91\n" + """
        Expect-Failure { Assert-HotspotSharing }
        Check (-not $script:HotspotSharingActive) 'stale interface-index policy must not stay active'
        Exit-HotspotSharing
        Check-Original
        """);

    [Fact]
    public Task ReplacementAtTheSameIndexIsDetected() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:Adapters[1].InterfaceGuid=$script:OtherGuid
        Expect-Failure { Assert-HotspotSharing }
        Check (-not $script:HotspotSharingActive) 'replacement interface cannot stay active'
        """);

    [Fact]
    public Task LosingHotspotIpv4StopsSharingAndRestoresUpstream() => RunAsync("""
        Enter-HotspotSharing $script:Adapters[0]
        $script:HotspotAddresses=@()
        Expect-Failure { Assert-HotspotSharing }
        Check (-not $script:HotspotSharingActive) 'hotspot with no address cannot stay active'
        Exit-HotspotSharing
        Check-Original
        """);
}
