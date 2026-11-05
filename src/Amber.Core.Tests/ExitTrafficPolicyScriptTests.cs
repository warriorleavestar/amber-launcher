using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>生产脚本的原生边界由内存替身接管；不打开真实 BFE 会话、不提权、不修改网络。</summary>
public sealed class ExitTrafficPolicyScriptTests
{
    private const string Mocks = """
        $script:TunGuid = 'cce8fac0-ff45-4c0b-88af-2b3774e87949'
        $script:PhysicalGuid = 'c6857c9c-8f36-4c37-bc05-85ff2c8f0879'
        $script:Adapters = @(
            [pscustomobject]@{ ifIndex=42; InterfaceGuid=$script:TunGuid },
            [pscustomobject]@{ ifIndex=7; InterfaceGuid=$script:PhysicalGuid }
        )
        $script:Plan = [pscustomobject]@{ VirtualIp='10.42.0.2'; UnderlayAddresses=@('203.0.113.10') }
        $script:exitRoutes = @([pscustomobject]@{ Prefix='203.0.113.10/32'; Index=7; Guid=$script:PhysicalGuid; Hop='192.0.2.1'; Metric=1; Owned=$true })
        $script:Events = New-Object 'Collections.Generic.List[string]'
        $script:Filters = New-Object 'Collections.Generic.List[object]'
        $script:FailApi = ''
        $script:CloseFailures = 0
        $script:MissingRoute = $false
        $script:Committed = $false
        function Assert-That([bool]$condition, [string]$message) { if (-not $condition) { throw ('Mock assertion failed: ' + $message) } }
        function Expect-Failure([scriptblock]$action) {
            $failed = $false
            try { & $action } catch { $failed = $true }
            Assert-That $failed 'operation should fail'
        }
        function Get-ExitAdapter([int]$index) {
            return $script:Adapters | Where-Object { $_.ifIndex -eq $index } | Select-Object -First 1
        }
        function Get-OwnedExitRoute($entry) {
            if ($script:MissingRoute) { return $null }
            return $entry
        }
        function Invoke-ExitWfpApi([string]$name, [object[]]$arguments) {
            $script:Events.Add($name)
            if ($name -eq $script:FailApi) { throw ('mock native failure ' + $name) }
            switch ($name) {
                'ConvertInterfaceGuidToLuid' {
                    $guid = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[0], [type][Guid])
                    if ($guid.ToString() -eq $script:TunGuid) { $luid = 420042 }
                    elseif ($guid.ToString() -eq $script:PhysicalGuid) { $luid = 700007 }
                    else { throw 'Unknown adapter GUID' }
                    [Runtime.InteropServices.Marshal]::WriteInt64($arguments[1], $luid)
                }
                'FwpmEngineOpen0' {
                    $session = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[3], [type]$script:ExitWfpTypes.Session)
                    Assert-That ($session.Flags -eq 1) 'engine session must be dynamic'
                    Assert-That ($arguments[0] -eq [IntPtr]::Zero -and $arguments[1] -eq 10 -and $arguments[2] -eq [IntPtr]::Zero) 'engine must use local caller credentials'
                    Assert-That ($session.Timeout -eq 5000 -and $session.Key -ne [Guid]::Empty) 'session options malformed'
                    [Runtime.InteropServices.Marshal]::WriteIntPtr($arguments[4], [IntPtr]1234)
                }
                'FwpmTransactionBegin0' { Assert-That ($arguments[0] -eq [IntPtr]1234 -and $arguments[1] -eq 0) 'write transaction arguments malformed' }
                'FwpmSubLayerAdd0' {
                    $sub = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[1], [type]$script:ExitWfpTypes.SubLayer)
                    Assert-That ($sub.Flags -eq 0 -and $sub.Weight -eq 65535 -and $sub.Key -ne [Guid]::Empty) 'sublayer must be temporary and highest priority'
                    $script:SubLayerKey = $sub.Key
                }
                'FwpmFilterAdd0' {
                    $filter = [Runtime.InteropServices.Marshal]::PtrToStructure($arguments[1], [type]$script:ExitWfpTypes.Filter)
                    Assert-That ($filter.Flags -eq 0 -and $filter.ProviderKey -eq [IntPtr]::Zero -and $filter.SubLayerKey -eq $script:SubLayerKey) 'persistent or hard-permit filter is forbidden'
                    Assert-That ($filter.Weight.Type -eq 4 -and $filter.Weight.Data.Pointer -ne [IntPtr]::Zero) 'filter weight must point to a UINT64'
                    $conditions = @()
                    $size = [Runtime.InteropServices.Marshal]::SizeOf([type]$script:ExitWfpTypes.Condition)
                    for ($i = 0; $i -lt $filter.ConditionCount; $i++) {
                        $condition = [Runtime.InteropServices.Marshal]::PtrToStructure([IntPtr]::Add($filter.Conditions, $i * $size), [type]$script:ExitWfpTypes.Condition)
                        if ($condition.Value.Type -eq 4) { $value = [Runtime.InteropServices.Marshal]::ReadInt64($condition.Value.Data.Pointer) }
                        elseif ($condition.Value.Type -eq 3) { $value = $condition.Value.Data.UInt32 }
                        else { throw 'Unexpected native condition value type' }
                        $conditions += [pscustomobject]@{ Key=$condition.FieldKey.ToString(); Match=$condition.MatchType; Type=$condition.Value.Type; Value=$value }
                    }
                    Assert-That ($filter.ConditionCount -ne 0 -or $filter.Conditions -eq [IntPtr]::Zero) 'empty condition array must be null'
                    $script:Filters.Add([pscustomobject]@{
                        Name=[Runtime.InteropServices.Marshal]::PtrToStringUni($filter.Display.Name)
                        Layer=$filter.LayerKey.ToString(); Action=$filter.Action.Type
                        Weight=[Runtime.InteropServices.Marshal]::ReadInt64($filter.Weight.Data.Pointer)
                        Conditions=$conditions
                    })
                }
                'FwpmTransactionCommit0' { $script:Committed = $true }
                'FwpmTransactionAbort0' { Assert-That (-not $script:Committed) 'committed transaction was aborted' }
                'FwpmEngineClose0' {
                    Assert-That ($arguments[0] -eq [IntPtr]1234) 'closing wrong handle'
                    if ($script:CloseFailures -gt 0) { $script:CloseFailures--; throw 'mock close failure' }
                }
                default { throw ('Unmocked native API ' + $name) }
            }
        }
        function Test-Packet([string]$layer, [long]$luid, [uint32]$address, [uint32]$flags, [int]$index=-1) {
            if ($index -lt 0) { $index = if ($luid -eq 420042) { 42 } elseif ($luid -eq 700007) { 7 } else { 9 } }
            foreach ($filter in @($script:Filters | Where-Object { $_.Layer -eq $layer } | Sort-Object Weight -Descending)) {
                $matched = $true
                foreach ($condition in $filter.Conditions) {
                    switch ($condition.Key) {
                        '4cd62a49-59c3-4969-b7f3-bda5d32890a4' { if ($condition.Value -ne $luid) { $matched = $false } }
                        'b235ae9a-1d64-49b8-a44c-5ff3d9095045' { if ($condition.Value -ne $address) { $matched = $false } }
                        '632ce23b-5167-435c-86d7-e903684aa80c' { if (($flags -band $condition.Value) -ne $condition.Value) { $matched = $false } }
                        '667fd755-d695-434a-8af5-d3835a1259bc' { if ($condition.Value -ne $index) { $matched = $false } }
                        default { throw 'Unexpected packet condition' }
                    }
                }
                if ($matched) { return ($filter.Action -eq 0x1002) }
            }
            throw 'Policy has no catch-all rule'
        }
        """;

    private static async Task RunAsync(string body, bool mock = true, string? powershell = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        // 即使替身遗漏，也不能自动加载网络管理模块；唯一原生调用入口由 Mocks 整体替换。
        const string bootstrap = "Import-Module Microsoft.PowerShell.Utility,Microsoft.PowerShell.Management -ErrorAction Stop; "
            + "$PSModuleAutoLoadingPreference = 'None'; $ErrorActionPreference = 'Stop'; "
            + "try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())) } catch { [Console]::Error.WriteLine($_.ToString() + [Environment]::NewLine + $_.ScriptStackTrace); exit 1 }";
        var info = new ProcessStartInfo(powershell ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
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
        await process.StandardInput.WriteAsync(ExitTrafficPolicyScript.Script + "\n" + (mock ? Mocks : "") + "\n" + body);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + "\n" + await errors);
    }

    [Fact]
    public Task OnlyTunLoopbackAndExactProtectedUnderlayCanEgress() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        Assert-That ($script:Committed -and $script:ExitTrafficEngine -eq [IntPtr]1234) 'policy was not committed'
        Assert-That ($script:Filters.Count -eq 7) 'unexpected filter count'
        $v4 = '1e5c9fae-8a84-4135-a331-950b54229ecd'
        $v6 = 'a3b3ab6b-3564-488c-9117-f34e82142763'
        Assert-That (Test-Packet $v4 420042 0x09090909 0) 'TUN DNS was blocked'
        Assert-That (-not (Test-Packet $v4 700007 0x09090909 0)) 'physical DNS leaked'
        Assert-That (-not (Test-Packet $v4 700007 ([uint32]3221226037) 0)) 'old LAN DNS leaked'
        Assert-That (Test-Packet $v4 700007 ([uint32]3405803786) 0) 'protected underlay was blocked or wrong address byte order'
        Assert-That (-not (Test-Packet $v4 900009 ([uint32]3405803786) 0)) 'underlay escaped through another adapter'
        Assert-That (-not (Test-Packet $v4 700007 ([uint32]3405803787) 0)) 'underlay exception widened beyond one address'
        Assert-That (-not (Test-Packet $v4 420042 0x09090909 0 7)) 'virtual source identity allowed physical egress'
        Assert-That (-not (Test-Packet $v6 700007 0 0)) 'physical IPv6 leaked'
        Assert-That (Test-Packet $v4 0 0 1) 'IPv4 loopback blocked'
        Assert-That (Test-Packet $v6 0 0 1) 'IPv6 loopback blocked'
        Assert-That (Test-Packet $v6 420042 0 0) 'virtual adapter IPv6 blocked'
        $rules = @($script:Filters | Where-Object { $_.Name -eq 'Amber exit protected underlay' })
        Assert-That ($rules.Count -eq 1 -and $rules[0].Conditions.Count -eq 3) 'underlay permit must require LUID, interface index and address'
        Exit-ExitTrafficPolicy
        Exit-ExitTrafficPolicy
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]::Zero) 'engine lease not released'
        Assert-That (@($script:Events | Where-Object { $_ -eq 'FwpmEngineClose0' }).Count -eq 1) 'duplicate exit closed twice'
        """);

    [Theory]
    [InlineData("FwpmTransactionBegin0", false)]
    [InlineData("FwpmSubLayerAdd0", true)]
    [InlineData("FwpmFilterAdd0", true)]
    [InlineData("FwpmTransactionCommit0", true)]
    public Task SetupFailureClosesDynamicSessionWithoutAcknowledgement(string api, bool abort) => RunAsync($$"""
        $script:FailApi = '{{api}}'
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Assert-That (-not $script:Committed -and $script:ExitTrafficEngine -eq [IntPtr]::Zero) 'failed setup left a published policy'
        Assert-That ($script:Events.Contains('FwpmEngineClose0')) 'failed setup leaked an engine handle'
        Assert-That ($script:Events.Contains('FwpmTransactionAbort0') -eq ${{abort.ToString().ToLowerInvariant()}}) 'transaction abort state incorrect'
        """);

    [Fact]
    public Task EngineOpenFailureDoesNotCloseAnUnownedHandle() => RunAsync("""
        $script:FailApi = 'FwpmEngineOpen0'
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]::Zero -and -not $script:Events.Contains('FwpmEngineClose0')) 'open failure invented a handle'
        """);

    [Fact]
    public Task MissingOrChangedUnderlayFailsBeforeOpeningEngine() => RunAsync("""
        $script:MissingRoute = $true
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        $script:MissingRoute = $false
        $script:exitRoutes[0].Guid = [Guid]::NewGuid().ToString()
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Assert-That (-not $script:Events.Contains('FwpmEngineOpen0')) 'invalid underlay started policy mutation'
        """);

    [Fact]
    public Task NativeIpv6UnderlayIsRejectedInsteadOfSilentlyBlocked() => RunAsync("""
        $script:Plan.UnderlayAddresses = @('2001:db8::5')
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Assert-That (-not $script:Events.Contains('FwpmEngineOpen0')) 'IPv6 underlay started policy mutation'
        """);

    [Fact]
    public Task BorrowedRoutesAreValidButOnlyOneRuleIsAddedForEachAddress() => RunAsync("""
        $script:Plan.UnderlayAddresses = @('203.0.113.10', '203.0.113.10')
        $script:exitRoutes[0].Owned = $false
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        Assert-That ($script:Filters.Count -eq 7) 'duplicate or borrowed route mishandled'
        Exit-ExitTrafficPolicy
        """);

    [Fact]
    public Task CloseFailureKeepsHandleForCleanupRetry() => RunAsync("""
        Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan
        $script:CloseFailures = 1
        Expect-Failure { Exit-ExitTrafficPolicy }
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]1234) 'failed close lost owned handle'
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Exit-ExitTrafficPolicy
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]::Zero) 'cleanup retry did not release handle'
        """);

    [Fact]
    public Task CleanupFailureAfterSetupFailureCanStillBeRetried() => RunAsync("""
        $script:FailApi = 'FwpmFilterAdd0'
        $script:CloseFailures = 1
        Expect-Failure { Enter-ExitTrafficPolicy $script:Adapters[0] $script:Plan }
        Assert-That (-not $script:Committed -and $script:ExitTrafficEngine -eq [IntPtr]1234) 'failed setup cleanup lost handle'
        Exit-ExitTrafficPolicy
        Assert-That ($script:ExitTrafficEngine -eq [IntPtr]::Zero) 'failed setup cleanup could not retry'
        """);

    private const string LayoutChecks = """
        Initialize-ExitWfpNative
        function Check-Size([string]$name, [int]$x64, [int]$x86) {
            $expected = if ([IntPtr]::Size -eq 8) { $x64 } else { $x86 }
            $actual = [Runtime.InteropServices.Marshal]::SizeOf([type]$script:ExitWfpTypes[$name])
            if ($actual -ne $expected) { throw ('SDK layout size mismatch ' + $name + ': ' + $actual + ' != ' + $expected) }
        }
        function Check-Offset([string]$name, [string]$field, [int]$x64, [int]$x86) {
            $expected = if ([IntPtr]::Size -eq 8) { $x64 } else { $x86 }
            $actual = [Runtime.InteropServices.Marshal]::OffsetOf([type]$script:ExitWfpTypes[$name], $field).ToInt32()
            if ($actual -ne $expected) { throw ('SDK field offset mismatch ' + $name + '.' + $field + ': ' + $actual + ' != ' + $expected) }
        }
        # 按 Microsoft WinSDK fwpmtypes.h / fwptypes.h 的默认 pack=8 ABI 校验。
        Check-Size 'Value' 16 8
        Check-Size 'Condition' 40 28
        Check-Size 'Session' 72 48
        Check-Size 'SubLayer' 72 44
        Check-Size 'Action' 20 20
        Check-Size 'Context' 16 16
        Check-Size 'Filter' 200 152
        Check-Offset 'Value' 'Data' 8 4
        Check-Offset 'Condition' 'Value' 24 20
        Check-Offset 'Session' 'Flags' 32 24
        Check-Offset 'Session' 'Sid' 48 36
        Check-Offset 'SubLayer' 'Weight' 64 40
        Check-Offset 'Filter' 'LayerKey' 64 40
        Check-Offset 'Filter' 'Weight' 96 72
        Check-Offset 'Filter' 'ConditionCount' 112 80
        Check-Offset 'Filter' 'Conditions' 120 84
        Check-Offset 'Filter' 'Action' 128 88
        Check-Offset 'Filter' 'Context' 152 112
        Check-Offset 'Filter' 'Reserved' 168 128
        Check-Offset 'Filter' 'FilterId' 176 136
        Check-Offset 'Filter' 'EffectiveWeight' 184 144
        $methods = $script:ExitWfpNative.GetMethods([Reflection.BindingFlags]'Public, Static, DeclaredOnly')
        if ($methods.Count -ne 8) { throw 'Unexpected native API surface' }
        foreach ($method in $methods) {
            $attribute = @($method.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false))[0]
            if ($method.ReturnType -ne [uint32] -or $attribute.CallingConvention -ne [Runtime.InteropServices.CallingConvention]::Winapi) { throw 'Wrong native method ABI' }
        }
        # 只生成元数据、检查布局；此测试绝不调用任何原生函数。
        """;

    [Fact]
    public Task NativeMetadataMatchesWindowsSdkWithoutOpeningBfe() => RunAsync(LayoutChecks, mock: false);

    [Fact]
    public Task X86NativeMetadataMatchesWindowsSdkWithoutOpeningBfe()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem) return Task.CompletedTask;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(executable) ? RunAsync(LayoutChecks, mock: false, powershell: executable) : Task.CompletedTask;
    }
}
