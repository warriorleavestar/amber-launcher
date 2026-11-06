using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>用内存中的 WFP 事务替身检查热点策略，不提权、不调用 BFE、不改网络。</summary>
public sealed class HotspotTrafficPolicyScriptTests
{
    private const string Mocks = """
        $script:TunGuid = 'cce8fac0-ff45-4c0b-88af-2b3774e87949'
        $script:PhysicalGuid = 'c6857c9c-8f36-4c37-bc05-85ff2c8f0879'
        $script:HotspotGuid = '2a922260-a39e-40ca-9095-7dbfb5cf88c1'
        $script:Adapters = @(
            [pscustomobject]@{ ifIndex=42; InterfaceGuid=$script:TunGuid },
            [pscustomobject]@{ ifIndex=7; InterfaceGuid=$script:PhysicalGuid },
            [pscustomobject]@{ ifIndex=12; InterfaceGuid=$script:HotspotGuid }
        )
        $script:Plan = [pscustomobject]@{ VirtualIp='10.42.0.2'; UnderlayAddresses=@('203.0.113.10') }
        $script:exitRoutes = @([pscustomobject]@{ Prefix='203.0.113.10/32'; Index=7; Guid=$script:PhysicalGuid; Hop='192.0.2.1'; Metric=1; Owned=$true })
        $script:Filters = New-Object 'Collections.Generic.List[object]'
        $script:Pending = $null
        $script:Events = New-Object 'Collections.Generic.List[string]'
        $script:FailApi = ''
        $script:FailAddAt = 0
        $script:AddCount = 0
        $script:AbortFailures = 0
        function Assert-That([bool]$condition, [string]$message) { if (-not $condition) { throw ('Mock assertion failed: ' + $message) } }
        function Expect-Failure([scriptblock]$action) {
            $failed = $false
            try { & $action } catch { $failed = $true }
            Assert-That $failed 'operation should fail'
        }
        function Get-ExitAdapter([int]$index) { return $script:Adapters | Where-Object { $_.ifIndex -eq $index } | Select-Object -First 1 }
        function Get-OwnedExitRoute($entry) { return $entry }
        function Invoke-ExitWfpApi([string]$name, [object[]]$arguments) {
            $script:Events.Add($name)
            if ($name -eq $script:FailApi) { throw ('mock failure ' + $name) }
            switch ($name) {
                'ConvertInterfaceGuidToLuid' {
                    $guid = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[0], [type][Guid])
                    $adapter = $script:Adapters | Where-Object { [Guid]$_.InterfaceGuid -eq $guid } | Select-Object -First 1
                    if (-not $adapter) { throw 'Unknown adapter' }
                    [Runtime.InteropServices.Marshal]::WriteInt64($arguments[1], ([long]$adapter.ifIndex * 10001))
                }
                'FwpmEngineOpen0' { [Runtime.InteropServices.Marshal]::WriteIntPtr($arguments[4], [IntPtr]1234) }
                'FwpmEngineClose0' { $script:Filters.Clear(); $script:Pending = $null }
                'FwpmTransactionBegin0' {
                    Assert-That ($arguments[0] -eq [IntPtr]1234 -and $arguments[1] -eq 0 -and $null -eq $script:Pending) 'invalid begin'
                    $script:Pending = New-Object 'Collections.Generic.List[object]'
                    foreach ($filter in $script:Filters) { $script:Pending.Add($filter) }
                }
                'FwpmTransactionCommit0' {
                    Assert-That ($null -ne $script:Pending) 'commit without transaction'
                    $script:Filters = $script:Pending
                    $script:Pending = $null
                }
                'FwpmTransactionAbort0' {
                    if ($script:AbortFailures -gt 0) { $script:AbortFailures--; throw 'mock abort failure' }
                    Assert-That ($null -ne $script:Pending) 'abort without transaction'
                    $script:Pending = $null
                }
                'FwpmSubLayerAdd0' {
                    $sub = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[1], [type]$script:ExitWfpTypes.SubLayer)
                    $script:SubLayerKey = $sub.Key
                }
                'FwpmFilterAdd0' {
                    $script:AddCount++
                    if ($script:FailAddAt -eq $script:AddCount) { throw 'mock add failure' }
                    Assert-That ($null -ne $script:Pending) 'filter added outside a transaction'
                    $filter = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[1], [type]$script:ExitWfpTypes.Filter)
                    Assert-That ($filter.SubLayerKey -eq $script:SubLayerKey -and $filter.Flags -eq 0) 'wrong sublayer or hard permit'
                    $conditions = @()
                    $size = [Runtime.InteropServices.Marshal]::SizeOf([type]$script:ExitWfpTypes.Condition)
                    for ($i = 0; $i -lt $filter.ConditionCount; $i++) {
                        $condition = [Runtime.InteropServices.Marshal]::PtrToStructure([IntPtr]::Add($filter.Conditions, $i * $size), [type]$script:ExitWfpTypes.Condition)
                        if ($condition.Value.Type -eq 4) { $value = [Runtime.InteropServices.Marshal]::ReadInt64($condition.Value.Data.Pointer) }
                        elseif ($condition.Value.Type -eq 3) { $value = $condition.Value.Data.UInt32 }
                        else { throw 'Unexpected condition ABI' }
                        $conditions += [pscustomobject]@{ Key=$condition.FieldKey.ToString(); Match=$condition.MatchType; Type=$condition.Value.Type; Value=$value }
                    }
                    $script:Pending.Add([pscustomobject]@{ Key=$filter.Key; Layer=$filter.LayerKey.ToString(); Action=$filter.Action.Type; Weight=[Runtime.InteropServices.Marshal]::ReadInt64($filter.Weight.Data.Pointer); Conditions=$conditions })
                }
                default { throw ('Unmocked native API ' + $name) }
            }
        }
        function Invoke-HotspotWfpApi([string]$name, [object[]]$arguments) {
            $script:Events.Add($name)
            if ($name -eq $script:FailApi) { throw ('mock failure ' + $name) }
            Assert-That ($name -eq 'FwpmFilterDeleteByKey0' -and $arguments[0] -eq [IntPtr]1234 -and $null -ne $script:Pending) 'invalid deletion'
            $key = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[1], [type][Guid])
            $found = @($script:Pending | Where-Object { $_.Key -eq $key })
            Assert-That ($found.Count -eq 1 -and $script:HotspotTrafficLease.Keys.Contains($key)) 'deleting an unowned filter'
            [void]$script:Pending.Remove($found[0])
        }
        function Test-Packet([string]$layer, [long]$luid=0, [uint32]$index=0, [uint32]$address=0, [uint32]$source=0, [uint32]$destination=0, [long]$forwardLuid=0) {
            foreach ($filter in @($script:Filters | Where-Object { $_.Layer -eq $layer } | Sort-Object Weight -Descending)) {
                $matched = $true
                foreach ($condition in $filter.Conditions) {
                    $actual = switch ($condition.Key) {
                        '4cd62a49-59c3-4969-b7f3-bda5d32890a4' { $luid }
                        '667fd755-d695-434a-8af5-d3835a1259bc' { $index }
                        'b235ae9a-1d64-49b8-a44c-5ff3d9095045' { $address }
                        '632ce23b-5167-435c-86d7-e903684aa80c' { 0 }
                        '2311334d-c92d-45bf-9496-edf447820e2d' { $source }
                        '35cf6522-4139-45ee-a0d5-67b80949d879' { $destination }
                        '1076b8a5-6323-4c5e-9810-e8d3fc9e6136' { $forwardLuid }
                        default { throw 'Unexpected packet condition' }
                    }
                    if ($actual -ne $condition.Value) { $matched = $false }
                }
                if ($matched) { return ($filter.Action -eq 0x1002) }
            }
            return $null
        }
        $v4 = '1e5c9fae-8a84-4135-a331-950b54229ecd'
        $v6 = 'a3b3ab6b-3564-488c-9117-f34e82142763'
        $f4 = 'a82acc24-4ee1-4ee1-b465-fd1d25cb10a4'
        $f6 = '7b964818-19c7-493a-b71f-832c3684d28c'
        """;

    private static async Task RunAsync(string body, bool mock = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string bootstrap = "Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management -ErrorAction Stop; "
            + "$PSModuleAutoLoadingPreference = 'None'; $ErrorActionPreference = 'Stop'; "
            + "try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())) } catch { [Console]::Error.WriteLine($_.ToString() + [Environment]::NewLine + $_.ScriptStackTrace); exit 1 }";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(ExitTrafficPolicyScript.Script + "\n" + HotspotTrafficPolicyScript.Script + "\n" + (mock ? Mocks : "") + "\n" + body);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + "\n" + await errors);
    }

    [Fact]
    public Task HotspotRepliesAndOnlyTunForwardingAreAllowed() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        $original = @($script:Filters | ForEach-Object { $_.Key })
        Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2]
        Assert-That ($script:Filters.Count -eq 14) 'hotspot filters not installed'
        Assert-That (Test-Packet $v4 120012 12 ([uint32]4294967295)) 'DHCP broadcast blocked'
        Assert-That (Test-Packet $v4 120012 12 ([uint32]3232270594)) 'hotspot DNS reply blocked'
        Assert-That (-not (Test-Packet $v4 120012 7 ([uint32]3232270594))) 'hotspot LUID allowed another interface'
        Assert-That (-not (Test-Packet $v4 70007 12 ([uint32]3232270594))) 'hotspot index allowed another LUID'
        Assert-That (-not (Test-Packet $v4 70007 7 0x09090909)) 'physical DNS leaked'
        Assert-That (Test-Packet $v4 70007 7 ([uint32]3405803786)) 'existing underlay lost'
        Assert-That (-not (Test-Packet $v6 120012 12)) 'hotspot IPv6 egress leaked'
        Assert-That (Test-Packet $f4 -source 12 -destination 42 -forwardLuid 420042) 'phone to exit blocked'
        Assert-That (Test-Packet $f4 -source 42 -destination 12 -forwardLuid 120012) 'phone return blocked'
        Assert-That (-not (Test-Packet $f4 -source 12 -destination 42 -forwardLuid 70007)) 'reused destination index allowed wrong adapter'
        Assert-That (-not (Test-Packet $f4 -source 12 -destination 7 -forwardLuid 70007)) 'phone fell back to physical uplink'
        Assert-That (-not (Test-Packet $f4 -source 7 -destination 12 -forwardLuid 120012)) 'physical return bypassed TUN'
        Assert-That (-not (Test-Packet $f6 -source 12 -destination 42 -forwardLuid 420042)) 'phone IPv6 forwarded'
        Assert-That (-not (Test-Packet $f6 -source 42 -destination 12 -forwardLuid 120012)) 'IPv6 return forwarded'
        Assert-That ($null -eq (Test-Packet $f4 -source 7 -destination 8 -forwardLuid 80008)) 'unrelated forwarding changed'
        Exit-HotspotTrafficPolicy
        Exit-HotspotTrafficPolicy
        Assert-That ($script:Filters.Count -eq 7 -and @($script:Filters | Where-Object { $original -notcontains $_.Key }).Count -eq 0) 'base filters changed'
        Assert-That (-not (Test-Packet $v4 120012 12 ([uint32]4294967295))) 'hotspot exception remained'
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]1234 -and -not $script:Events.Contains('FwpmEngineClose0')) 'base gate was closed'
        """);

    [Theory]
    [InlineData("begin")]
    [InlineData("add")]
    [InlineData("commit")]
    public Task SetupFailureKeepsTheOriginalGate(string stage) => RunAsync($$"""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        if ('{{stage}}' -eq 'begin') { $script:FailApi = 'FwpmTransactionBegin0' }
        if ('{{stage}}' -eq 'commit') { $script:FailApi = 'FwpmTransactionCommit0' }
        if ('{{stage}}' -eq 'add') { $script:FailAddAt = $script:AddCount + 3 }
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2] }
        Assert-That ($script:Filters.Count -eq 7 -and $null -eq $script:HotspotTrafficLease -and $null -eq $script:Pending) 'partial hotspot policy remained'
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]1234 -and -not $script:Events.Contains('FwpmEngineClose0')) 'original engine closed'
        """);

    [Theory]
    [InlineData("FwpmTransactionBegin0")]
    [InlineData("FwpmFilterDeleteByKey0")]
    [InlineData("FwpmTransactionCommit0")]
    public Task CleanupFailureRetainsAllFiltersForRetry(string api) => RunAsync($$"""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2]
        $script:FailApi = '{{api}}'
        Expect-Failure { Exit-HotspotTrafficPolicy }
        Assert-That ($script:Filters.Count -eq 14 -and $null -ne $script:HotspotTrafficLease) 'failed cleanup lost ownership or partially opened gate'
        $script:FailApi = ''
        Exit-HotspotTrafficPolicy
        Assert-That ($script:Filters.Count -eq 7 -and $null -eq $script:HotspotTrafficLease) 'cleanup retry failed'
        Assert-That (-not $script:Events.Contains('FwpmEngineClose0')) 'cleanup closed base engine'
        """);

    [Fact]
    public Task FailedAbortDuringInstallCanBeRetriedWithoutClosingBaseEngine() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        $script:FailAddAt = $script:AddCount + 3
        $script:AbortFailures = 1
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2] }
        Assert-That ($script:HotspotTrafficLease.PendingAbort -and $script:Filters.Count -eq 7) 'pending abort ownership lost'
        Exit-HotspotTrafficPolicy
        Assert-That ($null -eq $script:Pending -and $null -eq $script:HotspotTrafficLease -and $script:Filters.Count -eq 7) 'install abort retry failed'
        """);

    [Fact]
    public Task FailedAbortDuringCleanupPreservesInstalledLease() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2]
        $script:FailApi = 'FwpmFilterDeleteByKey0'
        $script:AbortFailures = 1
        Expect-Failure { Exit-HotspotTrafficPolicy }
        Assert-That ($script:HotspotTrafficLease.PendingAbort -and $script:HotspotTrafficLease.Installed) 'installed lease lost'
        $script:FailApi = ''
        Exit-HotspotTrafficPolicy
        Assert-That ($script:Filters.Count -eq 7 -and $null -eq $script:HotspotTrafficLease) 'delete abort retry failed'
        """);

    [Fact]
    public Task AdapterIdentityAndExitOwnershipAreCheckedBeforeMutation() => RunAsync("""
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2] }
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        $events = $script:Events.Count
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[0] }
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[1] $script:Adapters[2] }
        $stale = [pscustomobject]@{ ifIndex=12; InterfaceGuid=[Guid]::NewGuid() }
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $stale }
        Assert-That ($script:Events.Count -eq $events -and $script:Filters.Count -eq 7) 'invalid identity reached native mutation'
        Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2]
        Expect-Failure { Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2] }
        $owner = $script:ExitTrafficSubLayer
        $script:ExitTrafficSubLayer = [Guid]::NewGuid()
        Expect-Failure { Exit-HotspotTrafficPolicy }
        Assert-That ($script:Filters.Count -eq 14) 'changed ownership deleted filters'
        $script:ExitTrafficSubLayer = $owner
        Exit-HotspotTrafficPolicy
        """);

    [Fact]
    public Task BaseEngineCloseAlreadyRemovesHotspotFilters() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        Enter-HotspotTrafficPolicy $script:Adapters[0] $script:Adapters[2]
        Exit-ExitTrafficPolicy
        Exit-HotspotTrafficPolicy
        Assert-That ($script:Filters.Count -eq 0 -and $null -eq $script:HotspotTrafficLease) 'closed dynamic engine left a lease'
        Assert-That (-not $script:Events.Contains('FwpmFilterDeleteByKey0')) 'deleted using a closed engine'
        """);

    [Fact]
    public Task DeleteNativeMetadataMatchesSdkWithoutInvokingIt() => RunAsync("""
        Initialize-HotspotWfpNative
        $methods = @($script:HotspotWfpNative.GetMethods([Reflection.BindingFlags]'Public, Static, DeclaredOnly'))
        if ($methods.Count -ne 1) { throw 'Unexpected hotspot native surface' }
        $method = $methods[0]
        $attribute = @($method.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false))[0]
        $parameters = @($method.GetParameters())
        if ($method.Name -ne 'FwpmFilterDeleteByKey0' -or $method.ReturnType -ne [uint32] -or $attribute.CallingConvention -ne [Runtime.InteropServices.CallingConvention]::Winapi -or $parameters.Count -ne 2 -or $parameters[0].ParameterType -ne [IntPtr] -or $parameters[1].ParameterType -ne [IntPtr]) { throw 'Wrong hotspot deletion ABI' }
        """, mock: false);
}
