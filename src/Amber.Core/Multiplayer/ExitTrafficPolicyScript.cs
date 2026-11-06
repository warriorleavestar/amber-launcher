namespace Amber.Core.Multiplayer;

/// <summary>
/// Windows 出口会话的临时 WFP 门禁；仅在守护进程的动态 BFE 会话中存在。
/// IPPACKET 层按接口 LUID 与地址过滤，不能限制应用或端口，因此承载例外是明确的目标地址例外。
/// 原生定义依据 Microsoft win32metadata 的 fwpmu.h、fwpmtypes.h、fwptypes.h；不安装驱动或持久防火墙规则。
/// </summary>
internal static class ExitTrafficPolicyScript
{
    internal const string Script = """
        $script:ExitTrafficEngine = [IntPtr]::Zero
        $script:ExitTrafficSubLayer = [Guid]::Empty
        $script:ExitTrafficAdapterGuid = [Guid]::Empty
        $script:ExitTrafficAdapterIndex = [uint32]0
        $script:ExitWfpNative = $null
        $script:ExitWfpTypes = @{}

        function New-ExitWfpType($module, [string]$name, [object[]]$fields, [switch]$Union) {
            $layout = if ($Union) { 'Public, Sealed, ExplicitLayout' } else { 'Public, Sealed, SequentialLayout' }
            $builder = $module.DefineType(('AmberExitWfp.' + $name), [Reflection.TypeAttributes]$layout, [ValueType])
            foreach ($field in $fields) {
                $member = $builder.DefineField($field[0], $field[1], [Reflection.FieldAttributes]::Public)
                if ($Union) { $member.SetOffset(0) }
            }
            return $builder.CreateType()
        }

        function Initialize-ExitWfpNative {
            if ($script:ExitWfpNative) { return }
            # Declare native structures and calls in memory only; no Add-Type compiler, temporary source or on-disk DLL.
            $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly((New-Object Reflection.AssemblyName('AmberExitWfp')), [Reflection.Emit.AssemblyBuilderAccess]::Run)
            $module = $assembly.DefineDynamicModule('AmberExitWfp')
            $types = @{}
            $types.Display = New-ExitWfpType $module 'Display' @(@('Name', [IntPtr]), @('Description', [IntPtr]))
            $types.Blob = New-ExitWfpType $module 'Blob' @(@('Size', [uint32]), @('Data', [IntPtr]))
            # In FWP_VALUE0 a UINT64 is a pointer; UINT32/UINT8 are inline values in the same union.
            $types.ValueUnion = New-ExitWfpType $module 'ValueUnion' @(@('Pointer', [IntPtr]), @('UInt32', [uint32]), @('UInt8', [byte])) -Union
            $types.Value = New-ExitWfpType $module 'Value' @(@('Type', [uint32]), @('Data', $types.ValueUnion))
            $types.Context = New-ExitWfpType $module 'Context' @(@('Raw', [uint64]), @('Key', [Guid])) -Union
            $types.Action = New-ExitWfpType $module 'Action' @(@('Type', [uint32]), @('Key', [Guid]))
            $types.Condition = New-ExitWfpType $module 'Condition' @(@('FieldKey', [Guid]), @('MatchType', [uint32]), @('Value', $types.Value))
            $types.Session = New-ExitWfpType $module 'Session' @(@('Key', [Guid]), @('Display', $types.Display), @('Flags', [uint32]), @('Timeout', [uint32]), @('ProcessId', [uint32]), @('Sid', [IntPtr]), @('Username', [IntPtr]), @('KernelMode', [int]))
            $types.SubLayer = New-ExitWfpType $module 'SubLayer' @(@('Key', [Guid]), @('Display', $types.Display), @('Flags', [uint32]), @('ProviderKey', [IntPtr]), @('ProviderData', $types.Blob), @('Weight', [uint16]))
            $types.Filter = New-ExitWfpType $module 'Filter' @(@('Key', [Guid]), @('Display', $types.Display), @('Flags', [uint32]), @('ProviderKey', [IntPtr]), @('ProviderData', $types.Blob), @('LayerKey', [Guid]), @('SubLayerKey', [Guid]), @('Weight', $types.Value), @('ConditionCount', [uint32]), @('Conditions', [IntPtr]), @('Action', $types.Action), @('Context', $types.Context), @('Reserved', [IntPtr]), @('FilterId', [uint64]), @('EffectiveWeight', $types.Value))
            $builder = $module.DefineType('AmberExitWfp.Native', 'Public, Class')
            foreach ($spec in @(
                @('FwpmEngineOpen0', 'fwpuclnt.dll', [Type[]]@([IntPtr], [uint32], [IntPtr], [IntPtr], [IntPtr])),
                @('FwpmEngineClose0', 'fwpuclnt.dll', [Type[]]@([IntPtr])),
                @('FwpmTransactionBegin0', 'fwpuclnt.dll', [Type[]]@([IntPtr], [uint32])),
                @('FwpmTransactionCommit0', 'fwpuclnt.dll', [Type[]]@([IntPtr])),
                @('FwpmTransactionAbort0', 'fwpuclnt.dll', [Type[]]@([IntPtr])),
                @('FwpmSubLayerAdd0', 'fwpuclnt.dll', [Type[]]@([IntPtr], [IntPtr], [IntPtr])),
                @('FwpmFilterAdd0', 'fwpuclnt.dll', [Type[]]@([IntPtr], [IntPtr], [IntPtr], [IntPtr])),
                @('ConvertInterfaceGuidToLuid', 'iphlpapi.dll', [Type[]]@([IntPtr], [IntPtr]))
            )) {
                $method = $builder.DefinePInvokeMethod($spec[0], $spec[1], [Reflection.MethodAttributes]'Public, Static, PinvokeImpl', [Reflection.CallingConventions]::Standard, [uint32], $spec[2], [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
                $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            }
            $script:ExitWfpTypes = $types
            $script:ExitWfpNative = $builder.CreateType()
        }

        function Invoke-ExitWfpApi([string]$name, [object[]]$arguments) {
            $code = [uint32]$script:ExitWfpNative.GetMethod($name).Invoke($null, $arguments)
            if ($code -ne 0) { throw ('Windows traffic policy ' + $name + ' failed (0x' + $code.ToString('X8') + ')') }
        }

        function New-ExitWfpMemory([int]$size, $allocations) {
            $pointer = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
            $allocations.Add($pointer)
            for ($i = 0; $i -lt $size; $i++) { [Runtime.InteropServices.Marshal]::WriteByte($pointer, $i, 0) }
            return $pointer
        }

        function Write-ExitWfpStructure($value, $allocations) {
            $pointer = New-ExitWfpMemory ([Runtime.InteropServices.Marshal]::SizeOf($value)) $allocations
            [Runtime.InteropServices.Marshal]::StructureToPtr($value, $pointer, $false)
            return $pointer
        }

        function New-ExitWfpDisplay([string]$name, $allocations) {
            $display = [Activator]::CreateInstance($script:ExitWfpTypes.Display)
            $display.Name = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($name)
            $allocations.Add($display.Name)
            return $display
        }

        function Get-ExitPolicyLuid($adapter) {
            $allocations = New-Object 'Collections.Generic.List[IntPtr]'
            try {
                $guid = [Guid]$adapter.InterfaceGuid
                if ($guid -eq [Guid]::Empty) { throw 'Missing network adapter identity' }
                $guidPtr = Write-ExitWfpStructure $guid $allocations
                $luidPtr = New-ExitWfpMemory 8 $allocations
                Invoke-ExitWfpApi 'ConvertInterfaceGuidToLuid' @($guidPtr, $luidPtr)
                $luid = [Runtime.InteropServices.Marshal]::ReadInt64($luidPtr)
                if ($luid -eq 0) { throw 'Missing network interface LUID' }
                return $luid
            } finally { foreach ($pointer in $allocations) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) } }
        }

        function Open-ExitWfpEngine {
            $allocations = New-Object 'Collections.Generic.List[IntPtr]'
            try {
                $session = [Activator]::CreateInstance($script:ExitWfpTypes.Session)
                $session.Key = [Guid]::NewGuid()
                $session.Display = New-ExitWfpDisplay 'Amber temporary exit traffic policy' $allocations
                $session.Flags = 1 # FWPM_SESSION_FLAG_DYNAMIC: the system deletes every object on exit, crash or handle close.
                $session.Timeout = 5000
                $sessionPtr = Write-ExitWfpStructure $session $allocations
                $handlePtr = New-ExitWfpMemory ([IntPtr]::Size) $allocations
                Invoke-ExitWfpApi 'FwpmEngineOpen0' @([IntPtr]::Zero, [uint32]10, [IntPtr]::Zero, $sessionPtr, $handlePtr)
                $handle = [Runtime.InteropServices.Marshal]::ReadIntPtr($handlePtr)
                if ($handle -eq [IntPtr]::Zero) { throw 'Windows traffic policy returned no engine handle' }
                return $handle
            } finally { foreach ($pointer in $allocations) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) } }
        }

        function Add-ExitWfpSubLayer([IntPtr]$engine, [Guid]$key) {
            $allocations = New-Object 'Collections.Generic.List[IntPtr]'
            try {
                $subLayer = [Activator]::CreateInstance($script:ExitWfpTypes.SubLayer)
                $subLayer.Key = $key
                $subLayer.Display = New-ExitWfpDisplay 'Amber temporary exit isolation' $allocations
                $subLayer.Weight = [uint16]::MaxValue
                $pointer = Write-ExitWfpStructure $subLayer $allocations
                Invoke-ExitWfpApi 'FwpmSubLayerAdd0' @($engine, $pointer, [IntPtr]::Zero)
            } finally { foreach ($pointer in $allocations) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) } }
        }

        function Add-ExitWfpFilter([IntPtr]$engine, [Guid]$subLayer, $rule, [Guid]$filterKey = [Guid]::Empty) {
            $allocations = New-Object 'Collections.Generic.List[IntPtr]'
            try {
                $filter = [Activator]::CreateInstance($script:ExitWfpTypes.Filter)
                $filter.Key = if ($filterKey -eq [Guid]::Empty) { [Guid]::NewGuid() } else { $filterKey }
                $filter.Display = New-ExitWfpDisplay ('Amber exit ' + $rule.Name) $allocations
                $filter.LayerKey = [Guid]$rule.Layer
                $filter.SubLayerKey = $subLayer
                $weight = [Activator]::CreateInstance($script:ExitWfpTypes.Value)
                $weight.Type = 4 # FWP_UINT64; explicit weights make exceptions in this sublayer win over the final block.
                $weightData = [Activator]::CreateInstance($script:ExitWfpTypes.ValueUnion)
                $weightData.Pointer = New-ExitWfpMemory 8 $allocations
                [Runtime.InteropServices.Marshal]::WriteInt64($weightData.Pointer, [long]$rule.Weight)
                $weight.Data = $weightData
                $filter.Weight = $weight
                $action = [Activator]::CreateInstance($script:ExitWfpTypes.Action)
                $action.Type = if ($rule.Permit) { 0x1002 } else { 0x1001 }
                $filter.Action = $action
                # No CLEAR_ACTION_RIGHT: permits stay soft and cannot override existing firewall blocks.
                $conditions = @($rule.Conditions)
                $filter.ConditionCount = $conditions.Count
                if ($conditions.Count -gt 0) {
                    $conditionSize = [Runtime.InteropServices.Marshal]::SizeOf([type]$script:ExitWfpTypes.Condition)
                    $filter.Conditions = New-ExitWfpMemory ($conditions.Count * $conditionSize) $allocations
                    for ($i = 0; $i -lt $conditions.Count; $i++) {
                        $inputCondition = $conditions[$i]
                        $condition = [Activator]::CreateInstance($script:ExitWfpTypes.Condition)
                        $condition.FieldKey = [Guid]$inputCondition.Key
                        $condition.MatchType = $inputCondition.Match
                        $value = [Activator]::CreateInstance($script:ExitWfpTypes.Value)
                        $value.Type = $inputCondition.Type
                        $data = [Activator]::CreateInstance($script:ExitWfpTypes.ValueUnion)
                        if ($value.Type -eq 4) {
                            $data.Pointer = New-ExitWfpMemory 8 $allocations
                            [Runtime.InteropServices.Marshal]::WriteInt64($data.Pointer, [long]$inputCondition.Value)
                        } elseif ($value.Type -eq 3) { $data.UInt32 = [uint32]$inputCondition.Value }
                        else { throw 'Unsupported Windows traffic condition type' }
                        $value.Data = $data
                        $condition.Value = $value
                        [Runtime.InteropServices.Marshal]::StructureToPtr($condition, [IntPtr]::Add($filter.Conditions, $i * $conditionSize), $false)
                    }
                }
                $pointer = Write-ExitWfpStructure $filter $allocations
                Invoke-ExitWfpApi 'FwpmFilterAdd0' @($engine, $pointer, [IntPtr]::Zero, [IntPtr]::Zero)
            } finally { foreach ($pointer in $allocations) { [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer) } }
        }

        function Enter-ExitTrafficPolicy($adapter, $plan) {
            if ($script:ExitTrafficEngine -ne [IntPtr]::Zero) { throw 'Previous exit traffic policy has not stopped' }
            Initialize-ExitWfpNative
            $current = Get-ExitAdapter ([int]$adapter.ifIndex)
            if ([string]$current.InterfaceGuid -ne [string]$adapter.InterfaceGuid) { throw 'Exit adapter identity changed' }
            $tunLuid = Get-ExitPolicyLuid $current
            $underlay = @()
            $addresses = @($plan.UnderlayAddresses | Select-Object -Unique)
            if ($addresses.Count -gt 128) { throw 'Too many exit underlay addresses' }
            foreach ($address in $addresses) {
                $ip = $null
                if (-not [Net.IPAddress]::TryParse([string]$address, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -ne $address -or [Net.IPAddress]::IsLoopback($ip)) { throw 'Unsupported exit underlay address' }
                $bytes = $ip.GetAddressBytes()
                if ($bytes[0] -eq 0 -or $bytes[0] -ge 224 -or $address -eq $plan.VirtualIp) { throw 'Unsafe exit underlay address' }
                $entries = @($script:exitRoutes | Where-Object { $_.Prefix -eq ($address + '/32') })
                if ($entries.Count -ne 1 -or -not (Get-OwnedExitRoute $entries[0])) { throw 'Protected exit underlay route is missing' }
                $physical = Get-ExitAdapter ([int]$entries[0].Index)
                if ([string]$physical.InterfaceGuid -ne [string]$entries[0].Guid -or [string]$physical.InterfaceGuid -eq [string]$adapter.InterfaceGuid) { throw 'Exit underlay adapter identity changed' }
                $luid = Get-ExitPolicyLuid $physical
                # A WFP IPv4 UINT32 is the address value in host order, not the network bytes read as little-endian.
                $numeric = [uint32](([uint64]$bytes[0] * 16777216) + ([uint64]$bytes[1] * 65536) + ([uint64]$bytes[2] * 256) + $bytes[3])
                $underlay += [pscustomobject]@{ Address=$numeric; Luid=$luid; Index=[uint32]$entries[0].Index }
            }
            $interfaceKey = '4cd62a49-59c3-4969-b7f3-bda5d32890a4'
            $indexKey = '667fd755-d695-434a-8af5-d3835a1259bc'
            $addressKey = 'b235ae9a-1d64-49b8-a44c-5ff3d9095045'
            $flagsKey = '632ce23b-5167-435c-86d7-e903684aa80c'
            $layers = @('1e5c9fae-8a84-4135-a331-950b54229ecd', 'a3b3ab6b-3564-488c-9117-f34e82142763')
            $transaction = $false
            try {
                $script:ExitTrafficEngine = Open-ExitWfpEngine
                $engine = $script:ExitTrafficEngine
                Invoke-ExitWfpApi 'FwpmTransactionBegin0' @($engine, [uint32]0)
                $transaction = $true
                $subLayer = [Guid]::NewGuid()
                Add-ExitWfpSubLayer $engine $subLayer
                foreach ($layer in $layers) {
                    Add-ExitWfpFilter $engine $subLayer ([pscustomobject]@{ Name='loopback'; Layer=$layer; Permit=$true; Weight=300; Conditions=@([pscustomobject]@{ Key=$flagsKey; Match=6; Type=3; Value=1 }) })
                    # Also match the live interface index so physical egress bound to the virtual source address is not mistaken for the TUN.
                    Add-ExitWfpFilter $engine $subLayer ([pscustomobject]@{ Name='virtual adapter'; Layer=$layer; Permit=$true; Weight=200; Conditions=@(
                        [pscustomobject]@{ Key=$interfaceKey; Match=0; Type=4; Value=$tunLuid },
                        [pscustomobject]@{ Key=$indexKey; Match=0; Type=3; Value=[uint32]$current.ifIndex }
                    ) })
                    if ($layer -eq $layers[0]) {
                        foreach ($endpoint in $underlay) {
                            Add-ExitWfpFilter $engine $subLayer ([pscustomobject]@{ Name='protected underlay'; Layer=$layer; Permit=$true; Weight=100; Conditions=@(
                                [pscustomobject]@{ Key=$interfaceKey; Match=0; Type=4; Value=$endpoint.Luid },
                                [pscustomobject]@{ Key=$addressKey; Match=0; Type=3; Value=$endpoint.Address },
                                [pscustomobject]@{ Key=$indexKey; Match=0; Type=3; Value=$endpoint.Index }
                            ) })
                        }
                    }
                    # IPv6 has no physical underlay exception: the exit is IPv4-only, so IPv6 is blocked and apps fall back to IPv4.
                    Add-ExitWfpFilter $engine $subLayer ([pscustomobject]@{ Name='block other egress'; Layer=$layer; Permit=$false; Weight=1; Conditions=@() })
                }
                Invoke-ExitWfpApi 'FwpmTransactionCommit0' @($engine)
                $transaction = $false
                $script:ExitTrafficSubLayer = $subLayer
                $script:ExitTrafficAdapterGuid = [Guid]$current.InterfaceGuid
                $script:ExitTrafficAdapterIndex = [uint32]$current.ifIndex
            } catch {
                $failure = $_
                if ($transaction) { try { Invoke-ExitWfpApi 'FwpmTransactionAbort0' @($script:ExitTrafficEngine) } catch { } }
                try { Exit-ExitTrafficPolicy } catch { throw ($failure.ToString() + '; traffic cleanup also failed: ' + $_.ToString()) }
                throw $failure
            }
        }

        function Exit-ExitTrafficPolicy {
            if ($script:ExitTrafficEngine -eq [IntPtr]::Zero) { return }
            Invoke-ExitWfpApi 'FwpmEngineClose0' @($script:ExitTrafficEngine)
            # On failure keep the handle for a retry in finally; BFE still revokes the dynamic session's filters when the process ends.
            $script:ExitTrafficEngine = [IntPtr]::Zero
            $script:ExitTrafficSubLayer = [Guid]::Empty
            $script:ExitTrafficAdapterGuid = [Guid]::Empty
            $script:ExitTrafficAdapterIndex = [uint32]0
        }
        """;
}
