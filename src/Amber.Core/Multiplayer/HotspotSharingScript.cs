namespace Amber.Core.Multiplayer;

/// <summary>将现有 Windows 热点的共享上游临时切换到出口网卡；按接口 GUID 和共享状态核对所有修改及恢复。</summary>
internal static class HotspotSharingScript
{
    internal const string Script = """
        $script:HotspotSharingActive = $false
        $script:HotspotSharingLease = $null

        function New-HotspotSharingManager {
            return New-Object -ComObject HNetCfg.HNetShare -ErrorAction Stop
        }

        function Get-HotspotPortMappingCount($configuration) {
            # ICSSC_DEFAULT includes enabled and disabled mappings; either may belong to another application.
            return @($configuration.EnumPortMappings(0) | Where-Object { $null -ne $_ }).Count
        }

        function Get-HotspotSharingState($manager) {
            if (-not $manager.SharingInstalled) { throw 'Windows Internet Connection Sharing is unavailable' }
            $entries = @()
            foreach ($connection in $manager.EnumEveryConnection) {
                $properties = $manager.NetConnectionProps($connection)
                $guid = ([Guid]$properties.Guid).ToString('D')
                if ($guid -eq [Guid]::Empty.ToString('D') -or @($entries | Where-Object { $_.Guid -eq $guid }).Count -ne 0) {
                    throw 'Internet sharing connection identity is missing or duplicated'
                }
                $configuration = $manager.INetSharingConfigurationForINetConnection($connection)
                $enabled = [bool]$configuration.SharingEnabled
                $kind = -1
                if ($enabled) {
                    $kind = [int]$configuration.SharingConnectionType
                    if ($kind -notin @(0, 1)) { throw 'Unknown Internet sharing connection type' }
                }
                $entries += [pscustomobject]@{ Guid=$guid; Enabled=$enabled; Kind=$kind; Configuration=$configuration }
            }
            return $entries
        }

        function Get-HotspotSharingKey($entries) {
            return (@($entries | Where-Object { $_.Enabled } | ForEach-Object { $_.Guid + ':' + [string]$_.Kind } | Sort-Object) -join ',')
        }

        function Find-HotspotConnection($entries, [string]$guid) {
            $matches = @($entries | Where-Object { $_.Guid -eq $guid })
            if ($matches.Count -ne 1) { throw 'Internet sharing connection disappeared or is ambiguous' }
            return $matches[0]
        }

        function Find-HotspotAdapter([string]$guid) {
            $matches = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object { ([Guid]$_.InterfaceGuid).ToString('D') -eq $guid })
            if ($matches.Count -ne 1) { throw 'Hotspot network adapter disappeared or is ambiguous' }
            return $matches[0]
        }

        function Assert-HotspotPrivateAdapter($adapter) {
            $driver = [IO.Path]::GetFileName([string]$adapter.DriverFileName)
            $description = [string]$adapter.InterfaceDescription
            if ([string]::IsNullOrWhiteSpace($description)) { $description = [string]$adapter.DriverDescription }
            if ($driver -ine 'vwifimp.sys' -or $description -notmatch '^Microsoft Wi-Fi Direct Virtual Adapter(?: #\d+)?$' -or [string]$adapter.Status -ne 'Up') {
                throw 'The shared private connection is not an active Windows Wi-Fi mobile hotspot'
            }
            $usable = $false
            foreach ($address in @(Get-NetIPAddress -InterfaceIndex ([int]$adapter.ifIndex) -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop)) {
                $ip = $null
                if (-not [Net.IPAddress]::TryParse([string]$address.IPAddress, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { continue }
                $bytes = $ip.GetAddressBytes()
                if ($bytes[0] -eq 0 -or $bytes[0] -eq 127 -or $bytes[0] -ge 224 -or ($bytes[0] -eq 169 -and $bytes[1] -eq 254)) { continue }
                if ([int]$address.PrefixLength -lt 1 -or [int]$address.PrefixLength -gt 30 -or [string]$address.AddressState -ne 'Preferred') { continue }
                $usable = $true
            }
            if (-not $usable) { throw 'The Windows mobile hotspot does not have a usable IPv4 address yet' }
        }

        function Assert-HotspotNoMappings($entries, [string[]]$guids) {
            foreach ($guid in $guids) {
                $entry = Find-HotspotConnection $entries $guid
                if ((Get-HotspotPortMappingCount $entry.Configuration) -ne 0) {
                    throw 'Internet sharing has custom port mappings; leave this configuration unchanged'
                }
            }
        }

        function Assert-HotspotSharing {
            if (-not $script:HotspotSharingActive) { return }
            $lease = $script:HotspotSharingLease
            try {
                if ($null -eq $lease) { throw 'Hotspot sharing ownership is missing' }
                $entries = @(Get-HotspotSharingState $lease.Manager)
                if ((Get-HotspotSharingKey $entries) -ne $lease.AppliedKey) { throw 'Hotspot sharing was changed outside this session' }
                Assert-HotspotNoMappings $entries @($lease.PublicGuid, $lease.PrivateGuid, $lease.OriginalPublicGuid)
                $private = Find-HotspotAdapter $lease.PrivateGuid
                Assert-HotspotPrivateAdapter $private
                $public = Find-HotspotAdapter $lease.PublicGuid
                if ([IO.Path]::GetFileName([string]$public.DriverFileName) -ine 'wintun.sys' -or [string]$public.Status -ne 'Up') {
                    throw 'Hotspot exit adapter is no longer available'
                }
                if ([int]$public.ifIndex -ne $lease.PublicIndex -or [int]$private.ifIndex -ne $lease.PrivateIndex) {
                    throw 'Hotspot network interface index changed; restart sharing to rebuild its traffic policy'
                }
            } catch {
                $script:HotspotSharingActive = $false
                throw
            }
        }

        function Exit-HotspotSharing {
            $script:HotspotSharingActive = $false
            $lease = $script:HotspotSharingLease
            if ($null -eq $lease) { return }
            $entries = @(Get-HotspotSharingState $lease.Manager)
            $key = Get-HotspotSharingKey $entries
            if ($key -ne $lease.OriginalKey) {
                # These are the only observable states our single PUBLIC switch can own. Never replace a foreign pair.
                if (-not $lease.MutationAttempted -or $key -notin @($lease.AppliedKey, $lease.PrivateOnlyKey)) {
                    throw 'Hotspot sharing changed externally; automatic restoration was not applied'
                }
                $ownedPublic = @($entries | Where-Object { $_.Guid -eq $lease.PublicGuid })
                $mappingGuids = @($lease.PrivateGuid, $lease.OriginalPublicGuid)
                if ($ownedPublic.Count -eq 1) { $mappingGuids += $lease.PublicGuid }
                elseif ($key -ne $lease.PrivateOnlyKey) { throw 'Hotspot exit sharing connection disappeared unexpectedly' }
                Assert-HotspotNoMappings $entries $mappingGuids
                $original = Find-HotspotConnection $entries $lease.OriginalPublicGuid
                # Enabling the original PUBLIC automatically disables our PUBLIC; do not disable the private hotspot.
                $original.Configuration.EnableSharing(0) | Out-Null
                $entries = @(Get-HotspotSharingState $lease.Manager)
                if ((Get-HotspotSharingKey $entries) -ne $lease.OriginalKey) { throw 'Original hotspot sharing could not be restored' }
            }
            # A failed policy cleanup keeps the lease, allowing Stop-Session/finally to retry without changing ICS twice.
            if ($lease.PolicyPending) { Exit-HotspotTrafficPolicy; $lease.PolicyPending = $false }
            $script:HotspotSharingLease = $null
        }

        function Enter-HotspotSharing($adapter) {
            $publicGuid = ([Guid]$adapter.InterfaceGuid).ToString('D')
            if ($script:HotspotSharingActive) {
                if ($script:HotspotSharingLease.PublicGuid -ne $publicGuid) { throw 'A different exit already owns hotspot sharing' }
                Assert-HotspotSharing
                return
            }
            if ($null -ne $script:HotspotSharingLease) { throw 'Previous hotspot sharing has not been restored' }
            $publicAdapter = Find-HotspotAdapter $publicGuid
            if ([IO.Path]::GetFileName([string]$publicAdapter.DriverFileName) -ine 'wintun.sys' -or [string]$publicAdapter.Status -ne 'Up') {
                throw 'Hotspot sharing requires the active exit Wintun adapter'
            }
            $manager = New-HotspotSharingManager
            $entries = @(Get-HotspotSharingState $manager)
            $privateEntries = @($entries | Where-Object { $_.Enabled -and $_.Kind -eq 1 })
            $publicEntries = @($entries | Where-Object { $_.Enabled -and $_.Kind -eq 0 })
            if ($privateEntries.Count -ne 1 -or $publicEntries.Count -ne 1) {
                throw 'Turn on one Windows mobile hotspot first; other or ambiguous Internet sharing is not supported'
            }
            $privateGuid = $privateEntries[0].Guid
            $originalPublicGuid = $publicEntries[0].Guid
            $privateAdapter = Find-HotspotAdapter $privateGuid
            Assert-HotspotPrivateAdapter $privateAdapter
            if ([int]$publicAdapter.ifIndex -le 0 -or [int]$privateAdapter.ifIndex -le 0) { throw 'Hotspot network interface index is unavailable' }
            $target = Find-HotspotConnection $entries $publicGuid
            if ($target.Enabled -or $publicGuid -in @($privateGuid, $originalPublicGuid)) { throw 'The exit adapter is already used by Internet sharing' }
            Assert-HotspotNoMappings $entries @($publicGuid, $privateGuid, $originalPublicGuid)
            $lease = [pscustomobject]@{
                Manager=$manager; PublicGuid=$publicGuid; PrivateGuid=$privateGuid; OriginalPublicGuid=$originalPublicGuid
                PublicIndex=[int]$publicAdapter.ifIndex; PrivateIndex=[int]$privateAdapter.ifIndex
                OriginalKey=(Get-HotspotSharingKey $entries)
                AppliedKey=((@(($publicGuid + ':0'), ($privateGuid + ':1')) | Sort-Object) -join ',')
                PrivateOnlyKey=($privateGuid + ':1'); MutationAttempted=$false; PolicyPending=$false
            }
            $script:HotspotSharingLease = $lease
            try {
                # Install the forwarding boundary before ICS can move any hotspot client to the tunnel.
                $lease.PolicyPending = $true
                Enter-HotspotTrafficPolicy $publicAdapter $privateAdapter
                $entries = @(Get-HotspotSharingState $manager)
                if ((Get-HotspotSharingKey $entries) -ne $lease.OriginalKey) { throw 'Hotspot sharing changed while preparing the exit' }
                Assert-HotspotNoMappings $entries @($publicGuid, $privateGuid, $originalPublicGuid)
                $target = Find-HotspotConnection $entries $publicGuid
                $lease.MutationAttempted = $true
                $target.Configuration.EnableSharing(0) | Out-Null
                $script:HotspotSharingActive = $true
                Assert-HotspotSharing
            } catch {
                $failure = $_.Exception.Message
                try { Exit-HotspotSharing }
                catch { throw ('Hotspot sharing failed: ' + $failure + '; restoration also failed: ' + $_.Exception.Message) }
                throw $failure
            }
        }
        """;
}
