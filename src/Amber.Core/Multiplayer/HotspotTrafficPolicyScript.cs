namespace Amber.Core.Multiplayer;

/// <summary>热点共享期间附加到已有动态 WFP 会话的接口隔离规则。</summary>
internal static class HotspotTrafficPolicyScript
{
    internal const string Script = """
        $script:HotspotTrafficLease = $null
        $script:HotspotWfpNative = $null

        function Initialize-HotspotWfpNative {
            if ($script:HotspotWfpNative) { return }
            $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly((New-Object Reflection.AssemblyName('AmberHotspotWfp')), [Reflection.Emit.AssemblyBuilderAccess]::Run)
            $builder = $assembly.DefineDynamicModule('AmberHotspotWfp').DefineType('AmberHotspotWfp.Native', 'Public, Class')
            $method = $builder.DefinePInvokeMethod('FwpmFilterDeleteByKey0', 'fwpuclnt.dll', [Reflection.MethodAttributes]'Public, Static, PinvokeImpl', [Reflection.CallingConventions]::Standard, [uint32], [Type[]]@([IntPtr], [IntPtr]), [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
            $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            $script:HotspotWfpNative = $builder.CreateType()
        }

        function Invoke-HotspotWfpApi([string]$name, [object[]]$arguments) {
            $code = [uint32]$script:HotspotWfpNative.GetMethod($name).Invoke($null, $arguments)
            if ($code -ne 0) { throw ('Windows hotspot traffic policy ' + $name + ' failed (0x' + $code.ToString('X8') + ')') }
        }

        function Remove-HotspotWfpFilter([IntPtr]$engine, [Guid]$key) {
            $allocations = New-Object 'Collections.Generic.List[IntPtr]'
            try {
                $pointer = Write-ExitWfpStructure $key $allocations
                Invoke-HotspotWfpApi 'FwpmFilterDeleteByKey0' @($engine, $pointer)
            } finally { foreach ($pointer in $allocations) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) } }
        }

        function Enter-HotspotTrafficPolicy($tunAdapter, $hotspotAdapter) {
            if ($script:HotspotTrafficLease) { throw 'Previous hotspot traffic policy has not stopped' }
            if ($script:ExitTrafficEngine -eq [IntPtr]::Zero -or $script:ExitTrafficSubLayer -eq [Guid]::Empty) { throw 'Exit traffic policy is not active' }
            $tun = Get-ExitAdapter ([int]$tunAdapter.ifIndex)
            $hotspot = Get-ExitAdapter ([int]$hotspotAdapter.ifIndex)
            if (-not $tun -or -not $hotspot -or [uint32]$tun.ifIndex -eq 0 -or [uint32]$hotspot.ifIndex -eq 0) { throw 'Hotspot traffic adapter is missing' }
            $tunGuid = [Guid]$tun.InterfaceGuid
            $hotspotGuid = [Guid]$hotspot.InterfaceGuid
            if ($tunGuid -eq [Guid]::Empty -or $hotspotGuid -eq [Guid]::Empty -or $tunGuid -eq $hotspotGuid -or $tun.ifIndex -eq $hotspot.ifIndex) { throw 'Hotspot traffic adapters must be distinct' }
            if ($tunGuid -ne [Guid]$tunAdapter.InterfaceGuid -or $hotspotGuid -ne [Guid]$hotspotAdapter.InterfaceGuid) { throw 'Hotspot traffic adapter identity changed' }
            if ($tunGuid -ne $script:ExitTrafficAdapterGuid -or [uint32]$tun.ifIndex -ne $script:ExitTrafficAdapterIndex) { throw 'Hotspot traffic TUN is not the active exit adapter' }
            $tunLuid = Get-ExitPolicyLuid $tun
            $hotspotLuid = Get-ExitPolicyLuid $hotspot
            Initialize-HotspotWfpNative
            # Microsoft WinSDK fwpmu.h: source/destination indices and destination LUID at IPFORWARD.
            $sourceIndex = '2311334d-c92d-45bf-9496-edf447820e2d'
            $destinationIndex = '35cf6522-4139-45ee-a0d5-67b80949d879'
            $forwardInterface = '1076b8a5-6323-4c5e-9810-e8d3fc9e6136'
            $v4Forward = 'a82acc24-4ee1-4ee1-b465-fd1d25cb10a4'
            $v6Forward = '7b964818-19c7-493a-b71f-832c3684d28c'
            $rules = @()
            # ICS assigns its private subnet only after this gate is installed. Permit IPv4 replies
            # and DHCP broadcasts only on the exact private interface; never widen physical uplinks.
            $rules += [pscustomobject]@{ Name='hotspot local IPv4'; Layer='1e5c9fae-8a84-4135-a331-950b54229ecd'; Permit=$true; Weight=200; Conditions=@(
                [pscustomobject]@{ Key='4cd62a49-59c3-4969-b7f3-bda5d32890a4'; Match=0; Type=4; Value=$hotspotLuid },
                [pscustomobject]@{ Key='667fd755-d695-434a-8af5-d3835a1259bc'; Match=0; Type=3; Value=[uint32]$hotspot.ifIndex }
            ) }
            foreach ($pair in @(
                [pscustomobject]@{ Name='hotspot to exit'; Source=[uint32]$hotspot.ifIndex; Destination=[uint32]$tun.ifIndex; Luid=$tunLuid },
                [pscustomobject]@{ Name='exit to hotspot'; Source=[uint32]$tun.ifIndex; Destination=[uint32]$hotspot.ifIndex; Luid=$hotspotLuid }
            )) {
                $rules += [pscustomobject]@{ Name=$pair.Name; Layer=$v4Forward; Permit=$true; Weight=200; Conditions=@(
                    [pscustomobject]@{ Key=$sourceIndex; Match=0; Type=3; Value=$pair.Source },
                    [pscustomobject]@{ Key=$destinationIndex; Match=0; Type=3; Value=$pair.Destination },
                    [pscustomobject]@{ Key=$forwardInterface; Match=0; Type=4; Value=$pair.Luid }
                ) }
            }
            foreach ($layer in @($v4Forward, $v6Forward)) {
                foreach ($indexKey in @($sourceIndex, $destinationIndex)) {
                    $rules += [pscustomobject]@{ Name='block other hotspot forwarding'; Layer=$layer; Permit=$false; Weight=100; Conditions=@(
                        [pscustomobject]@{ Key=$indexKey; Match=0; Type=3; Value=[uint32]$hotspot.ifIndex }
                    ) }
                }
            }
            $keys = @($rules | ForEach-Object { [Guid]::NewGuid() })
            $lease = [pscustomobject]@{ Engine=$script:ExitTrafficEngine; SubLayer=$script:ExitTrafficSubLayer; Keys=$keys; Installed=$false; PendingAbort=$false }
            $transaction = $false
            try {
                Invoke-ExitWfpApi 'FwpmTransactionBegin0' @($lease.Engine, [uint32]0)
                $transaction = $true
                for ($i = 0; $i -lt $rules.Count; $i++) { Add-ExitWfpFilter $lease.Engine $lease.SubLayer $rules[$i] $keys[$i] }
                Invoke-ExitWfpApi 'FwpmTransactionCommit0' @($lease.Engine)
                $transaction = $false
                $lease.Installed = $true
                $script:HotspotTrafficLease = $lease
            } catch {
                $failure = $_
                if ($transaction) {
                    try { Invoke-ExitWfpApi 'FwpmTransactionAbort0' @($lease.Engine) }
                    catch {
                        $lease.PendingAbort = $true
                        $script:HotspotTrafficLease = $lease
                        throw ($failure.ToString() + '; hotspot transaction abort failed: ' + $_.ToString())
                    }
                }
                throw $failure
            }
        }

        function Exit-HotspotTrafficPolicy {
            $lease = $script:HotspotTrafficLease
            if (-not $lease) { return }
            # Closing the owning dynamic engine already removes all of its filters.
            if ($script:ExitTrafficEngine -eq [IntPtr]::Zero) { $script:HotspotTrafficLease = $null; return }
            if ($script:ExitTrafficEngine -ne $lease.Engine -or $script:ExitTrafficSubLayer -ne $lease.SubLayer) { throw 'Hotspot traffic engine ownership changed' }
            if ($lease.PendingAbort) {
                Invoke-ExitWfpApi 'FwpmTransactionAbort0' @($lease.Engine)
                $lease.PendingAbort = $false
                if (-not $lease.Installed) { $script:HotspotTrafficLease = $null; return }
            }
            $transaction = $false
            try {
                Invoke-ExitWfpApi 'FwpmTransactionBegin0' @($lease.Engine, [uint32]0)
                $transaction = $true
                foreach ($key in $lease.Keys) { Remove-HotspotWfpFilter $lease.Engine $key }
                Invoke-ExitWfpApi 'FwpmTransactionCommit0' @($lease.Engine)
                $transaction = $false
                $script:HotspotTrafficLease = $null
            } catch {
                $failure = $_
                if ($transaction) {
                    try { Invoke-ExitWfpApi 'FwpmTransactionAbort0' @($lease.Engine) }
                    catch { $lease.PendingAbort = $true; throw ($failure.ToString() + '; hotspot transaction abort failed: ' + $_.ToString()) }
                }
                throw $failure
            }
        }
        """;
}
