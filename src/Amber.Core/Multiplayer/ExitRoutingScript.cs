namespace Amber.Core.Multiplayer;

/// <summary>固定脚本经已认证管道传送，守护核对摘要后加载；这里只管理本次会话创建的短租约 IPv4 旁路。</summary>
internal static class ExitRoutingScript
{
    internal const string Script = """
        $script:exitRoutes = @()
        $script:exitAdapter = $null
        $script:exitAdapterBaseline = @()
        $script:exitAdapterName = $null
        $script:exitRenewed = [DateTime]::UtcNow
        $script:exitLease = [TimeSpan]::FromSeconds(120)

        function Initialize-GuardJob {
            # The guard joins the job first so every child inherits it; killing the guard never leaves a core holding the TUN.
            $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly((New-Object System.Reflection.AssemblyName('AmberGuardJob')), [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
            $t = $assembly.DefineDynamicModule('AmberGuardJob').DefineType('AmberGuardJob', 'Public, Class')
            foreach ($spec in @(
                @('CreateJobObjectW', [IntPtr], [Type[]]@([IntPtr], [string])),
                @('SetInformationJobObject', [bool], [Type[]]@([IntPtr], [int], [IntPtr], [uint32])),
                @('AssignProcessToJobObject', [bool], [Type[]]@([IntPtr], [IntPtr])),
                @('GetCurrentProcess', [IntPtr], [Type[]]@())
            )) {
                $m = $t.DefinePInvokeMethod($spec[0], 'kernel32.dll', [System.Reflection.MethodAttributes]'Public, Static, PinvokeImpl', [System.Reflection.CallingConventions]::Standard, $spec[1], $spec[2], [System.Runtime.InteropServices.CallingConvention]::Winapi, [System.Runtime.InteropServices.CharSet]::Unicode)
                $m.SetImplementationFlags([System.Reflection.MethodImplAttributes]::PreserveSig)
            }
            $jobNative = $t.CreateType()
            $script:guardJob = $jobNative::CreateJobObjectW([IntPtr]::Zero, $null)
            if ($script:guardJob -eq [IntPtr]::Zero) { throw 'Cannot create core lifetime job' }
            $size = if ([IntPtr]::Size -eq 8) { 144 } else { 112 }
            $limits = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
            try {
                for ($i = 0; $i -lt $size; $i++) { [Runtime.InteropServices.Marshal]::WriteByte($limits, $i, 0) }
                [Runtime.InteropServices.Marshal]::WriteInt32($limits, 16, 0x2000)
                if (-not $jobNative::SetInformationJobObject($script:guardJob, 9, $limits, $size)) { throw 'Cannot configure core lifetime job' }
                if (-not $jobNative::AssignProcessToJobObject($script:guardJob, $jobNative::GetCurrentProcess())) { throw 'Cannot protect core lifetime' }
            } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($limits) }
            # Keep this sole, non-inheritable handle open: the OS closes it at exit and ends every core child with it.
        }

        function Get-ExitAdapter([int]$index) {
            $adapter = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { $_.ifIndex -eq $index }) | Select-Object -First 1
            if (-not $adapter.InterfaceGuid) { throw 'Network adapter identity unavailable' }
            return $adapter
        }

        function Get-OwnedExitRoute($entry) {
            $adapter = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { $_.ifIndex -eq $entry.Index }) | Select-Object -First 1
            if (-not $adapter -or [string]$adapter.InterfaceGuid -ne $entry.Guid) { return $null }
            $route = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop |
                Where-Object { $_.DestinationPrefix -eq $entry.Prefix -and $_.InterfaceIndex -eq $entry.Index -and $_.NextHop -eq $entry.Hop -and $_.RouteMetric -eq $entry.Metric }) | Select-Object -First 1
            # A short lease changed externally into a long-lived route no longer belongs to this session; never renew or remove it.
            if ($route -and $entry.Owned -and ($null -eq $route.ValidLifetime -or $route.ValidLifetime.TotalSeconds -gt 125)) { return $null }
            return $route
        }

        function Enter-ExitRoutes($plan) {
            if ($script:exitRoutes.Count -ne 0 -or $script:exitAdapter) { throw 'Previous exit network has not stopped' }
            $script:exitAdapterName = [string]$plan.AdapterName
            if ([string]::IsNullOrWhiteSpace($script:exitAdapterName)) { $script:exitAdapterName = 'Amber LAN' }
            if ($script:exitAdapterName -notmatch '^Amber LAN(?:-[0-9]{1,5})?$') { throw 'Invalid exit adapter name' }
            $before = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
            if (@($before | Where-Object { $_.Name -eq $script:exitAdapterName }).Count -ne 0) { throw 'Exit adapter name is already in use' }
            $script:exitAdapterBaseline = @($before | ForEach-Object { [string]$_.InterfaceGuid })
            $ip = $null
            if (-not [Net.IPAddress]::TryParse([string]$plan.VirtualIp, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Invalid virtual IPv4 address' }
            $addresses = @($plan.UnderlayAddresses | Select-Object -Unique)
            if ($addresses.Count -gt 128) { throw 'Too many underlay addresses' }
            foreach ($address in $addresses) {
                $parsed = $null
                if (-not [Net.IPAddress]::TryParse([string]$address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $parsed.ToString() -ne $address -or [Net.IPAddress]::IsLoopback($parsed)) { throw 'Invalid underlay IPv4 address' }
                $bytes = $parsed.GetAddressBytes()
                if ($bytes[0] -eq 0 -or $bytes[0] -ge 224 -or $address -eq $plan.VirtualIp) { throw 'Unsafe underlay IPv4 address' }
                # The old core and capture routes are gone, so the current best route is the original underlay path to preserve.
                $route = @(Find-NetRoute -RemoteIPAddress $address -ErrorAction Stop | Where-Object { $_.DestinationPrefix }) | Select-Object -First 1
                if (-not $route) { throw 'No route to underlay address' }
                $adapter = Get-ExitAdapter $route.InterfaceIndex
                if ($adapter.Name -like 'Amber LAN*') { throw 'Underlay resolves through Amber LAN' }
                $prefix = $address + '/32'
                $existing = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop |
                    Where-Object { $_.DestinationPrefix -eq $prefix -and $_.InterfaceIndex -eq $route.InterfaceIndex -and $_.NextHop -eq $route.NextHop }) | Select-Object -First 1
                $entry = [pscustomobject]@{ Prefix=$prefix; Index=[int]$route.InterfaceIndex; Hop=[string]$route.NextHop; Guid=[string]$adapter.InterfaceGuid; Metric=1; Owned=$false }
                if ($existing) { $entry.Metric = $existing.RouteMetric }
                else {
                    # Take ownership only after creation succeeds; a concurrent third-party route with the same prefix must not be rolled back.
                    New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $entry.Index -NextHop $entry.Hop -RouteMetric $entry.Metric -PolicyStore ActiveStore -ValidLifetime $script:exitLease -PreferredLifetime $script:exitLease -ErrorAction Stop | Out-Null
                    $entry.Owned = $true
                    $script:exitRoutes += $entry
                    if (-not (Get-OwnedExitRoute $entry)) { throw 'Underlay bypass verification failed' }
                    continue
                }
                $script:exitRoutes += $entry
            }
            $script:exitRenewed = [DateTime]::UtcNow
        }

        function Renew-ExitRoutes {
            if (([DateTime]::UtcNow - $script:exitRenewed).TotalSeconds -lt 30) { return }
            foreach ($entry in $script:exitRoutes) {
                $route = Get-OwnedExitRoute $entry
                if (-not $route) { throw 'Underlay bypass changed while exit was active' }
                if ($entry.Owned) { Set-NetRoute -InputObject $route -ValidLifetime $script:exitLease -PreferredLifetime $script:exitLease -ErrorAction Stop | Out-Null }
            }
            $script:exitRenewed = [DateTime]::UtcNow
        }

        function Wait-ExitReady($plan, $process, [double]$timeoutSeconds = 20) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt $timeoutSeconds) {
                if ($process.HasExited) { throw 'Core exited before exit network was ready' }
                $ips = @(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.IPAddress -eq $plan.VirtualIp })
                if ($ips.Count -gt 1) { throw 'Virtual IPv4 address is not unique' }
                $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { $_.Name -eq $script:exitAdapterName })
                if ($adapters.Count -gt 1) { throw 'Exit adapter name is not unique' }
                if ($adapters.Count -eq 1) {
                    $adapter = $adapters[0]
                    if (-not $adapter.InterfaceGuid -or $adapter.DriverFileName -ne 'wintun.sys' -or $adapter.DriverDescription -ne 'Wintun Userspace Tunnel' -or [string]$adapter.InterfaceGuid -in $script:exitAdapterBaseline) { throw 'Exit adapter is not this session new Wintun device' }
                    # Only a new, strictly verified adapter belongs to this session; keep its identity for rollback even before the IP is ready.
                    $script:exitAdapter = $adapter
                }
                foreach ($ip in $ips) {
                    if ($adapters.Count -ne 1 -or $adapter.ifIndex -ne $ip.InterfaceIndex) { continue }
                    $routes = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.InterfaceIndex -eq $ip.InterfaceIndex })
                    $first = @($routes | Where-Object { $_.DestinationPrefix -eq '0.0.0.0/1' -and $_.NextHop -eq '0.0.0.0' }).Count -gt 0
                    $second = @($routes | Where-Object { $_.DestinationPrefix -eq '128.0.0.0/1' -and $_.NextHop -eq '0.0.0.0' }).Count -gt 0
                    if ($first -and $second) { return $adapter }
                }
                Start-Sleep -Milliseconds 100
            }
            throw 'Exit adapter or capture routes did not become ready'
        }

        function Wait-ExitReleased([double]$timeoutSeconds = 5) {
            # A failure before readiness may already have created capture routes; an unrecorded adapter does not mean nothing was captured.
            if (-not $script:exitAdapter -and $script:exitAdapterName) {
                $candidates = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { $_.Name -eq $script:exitAdapterName })
                if ($candidates.Count -gt 1) { throw 'Cannot identify exit adapter during rollback' }
                if ($candidates.Count -eq 1) {
                    $candidate = $candidates[0]
                    if (-not $candidate.InterfaceGuid -or $candidate.DriverFileName -ne 'wintun.sys' -or $candidate.DriverDescription -ne 'Wintun Userspace Tunnel' -or [string]$candidate.InterfaceGuid -in $script:exitAdapterBaseline) { throw 'Cannot confirm exit adapter ownership during rollback' }
                    $script:exitAdapter = $candidate
                }
            }
            if (-not $script:exitAdapter) { return }
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while ($timer.Elapsed.TotalSeconds -lt $timeoutSeconds) {
                $matching = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { [string]$_.InterfaceGuid -eq [string]$script:exitAdapter.InterfaceGuid })
                if ($matching.Count -gt 1) { throw 'Exit adapter identity is not unique during cleanup' }
                if ($matching.Count -eq 0) { $script:exitAdapter = $null; return }
                $adapter = $matching[0]
                $captured = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop |
                    Where-Object { $_.InterfaceIndex -eq $adapter.ifIndex -and $_.DestinationPrefix -in @('0.0.0.0/1', '128.0.0.0/1') })
                if ($captured.Count -eq 0) { $script:exitAdapter = $null; return }
                Start-Sleep -Milliseconds 100
            }
            throw 'Exit capture routes are still present; bypass leases retained'
        }

        function Exit-ExitRoutes {
            if ($script:exitAdapter) { throw 'Cannot remove bypass before capture routes are released' }
            $failed = @()
            foreach ($entry in $script:exitRoutes) {
                if (-not $entry.Owned) { continue }
                try {
                    $route = Get-OwnedExitRoute $entry
                    if ($route) { Remove-NetRoute -InputObject $route -Confirm:$false -ErrorAction Stop }
                } catch { $failed += $entry }
            }
            $script:exitRoutes = $failed
            if ($failed.Count -ne 0) { throw 'Could not remove all bypass routes; short leases will expire' }
            $script:exitAdapterBaseline = @()
            $script:exitAdapterName = $null
        }
        """;
}
