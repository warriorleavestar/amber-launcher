namespace Amber.Core.Multiplayer;

/// <summary>
/// 出口模式的临时 DNS：只操作调用方已验证的虚拟网卡 IPv4 设置，按 GUID 恢复自己仍然拥有的修改。
/// 较低的接口 metric 只提高 DNS 选择优先级；这不是隔离所有 DNS 查询或防泄漏的 VPN 策略。
/// </summary>
internal static class ExitDnsScript
{
    internal const string Script = """
        $script:ExitDnsLease = $null

        function Get-ExitDnsIndex($adapter) {
            $index = $adapter.InterfaceIndex
            if ($null -eq $index) { $index = $adapter.ifIndex }
            if ($null -eq $index -or [uint32]$index -eq 0) { throw 'Exit DNS adapter has no interface index' }
            return [uint32]$index
        }

        function Find-ExitDnsAdapter([string]$guid) {
            $found = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                try { ([guid]$_.InterfaceGuid).ToString('D') -eq $guid } catch { $false }
            })
            if ($found.Count -gt 1) { throw 'Exit DNS adapter GUID is not unique' }
            if ($found.Count -eq 1) { return $found[0] }
            return $null
        }

        function Get-ExitDnsAddressKey([string[]]$addresses) {
            $values = @()
            foreach ($value in $addresses) {
                $address = [System.Net.IPAddress]::Parse($value)
                if ($address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
                    throw 'Exit DNS only accepts IPv4 server addresses'
                }
                $values += $address.ToString()
            }
            return [string]::Join(',', [string[]]$values)
        }

        function Get-ExitDnsConfiguration($adapter) {
            $index = Get-ExitDnsIndex $adapter
            $rows = @(Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction Stop)
            if ($rows.Count -ne 1) { throw 'Exit DNS IPv4 configuration is not unique' }
            $guid = ([guid]$adapter.InterfaceGuid).ToString('B')
            $registry = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\' + $guid) -ErrorAction Stop
            $servers = @($rows[0].ServerAddresses)
            $key = Get-ExitDnsAddressKey $servers
            $isStatic = -not [string]::IsNullOrWhiteSpace([string]$registry.NameServer)
            if ($isStatic -and $servers.Count -eq 0) { throw 'Exit DNS static server list is empty' }
            return [pscustomobject]@{ DnsObject = $rows[0]; Servers = $servers; Key = $key; IsStatic = $isStatic }
        }

        function Get-ExitDnsMetric($adapter) {
            $rows = @(Get-NetIPInterface -InterfaceIndex (Get-ExitDnsIndex $adapter) -AddressFamily IPv4 -ErrorAction Stop)
            if ($rows.Count -ne 1) { throw 'Exit DNS IPv4 interface is not unique' }
            $automatic = [string]$rows[0].AutomaticMetric
            if ($automatic -ne 'Enabled' -and $automatic -ne 'Disabled') { throw 'Exit DNS automatic metric mode is unknown' }
            return [pscustomobject]@{ Automatic = $automatic; Metric = [uint32]$rows[0].InterfaceMetric }
        }

        function Exit-ExitDns {
            $lease = $script:ExitDnsLease
            if ($null -eq $lease) { return }
            $adapter = Find-ExitDnsAdapter $lease.Guid
            if ($null -eq $adapter) { $script:ExitDnsLease = $null; return }
            $errors = New-Object 'System.Collections.Generic.List[string]'

            if ($lease.DnsPending) {
                try {
                    $current = Get-ExitDnsConfiguration $adapter
                    $original = $lease.OriginalDns
                    $alreadyRestored = $current.IsStatic -eq $original.IsStatic -and (-not $original.IsStatic -or $current.Key -eq $original.Key)
                    if ($alreadyRestored) {
                        $lease.DnsPending = $false
                        $lease.CachePending = $true
                    } elseif ($current.IsStatic -and $current.Key -eq $lease.AppliedDnsKey) {
                        if ($original.IsStatic) {
                            Set-DnsClientServerAddress -InputObject $current.DnsObject -ServerAddresses $original.Servers -ErrorAction Stop | Out-Null
                        } else {
                            Set-DnsClientServerAddress -InputObject $current.DnsObject -ResetServerAddresses -ErrorAction Stop | Out-Null
                        }
                        $restored = Get-ExitDnsConfiguration $adapter
                        if ($restored.IsStatic -ne $original.IsStatic -or ($original.IsStatic -and $restored.Key -ne $original.Key)) {
                            throw 'Exit DNS original configuration was not restored'
                        }
                        $lease.DnsPending = $false
                        $lease.CachePending = $true
                    } else {
                        Write-Warning 'Exit DNS was changed externally; leaving the current DNS configuration unchanged'
                        $lease.DnsPending = $false
                    }
                } catch { $errors.Add($_.Exception.Message) }
            }

            if ($lease.MetricPending) {
                try {
                    $currentMetric = Get-ExitDnsMetric $adapter
                    $originalMetric = $lease.OriginalMetric
                    $alreadyRestored = $currentMetric.Automatic -eq $originalMetric.Automatic -and ($originalMetric.Automatic -eq 'Enabled' -or $currentMetric.Metric -eq $originalMetric.Metric)
                    if ($alreadyRestored) {
                        $lease.MetricPending = $false
                        $lease.CachePending = $true
                    } elseif ($currentMetric.Automatic -eq 'Disabled' -and $currentMetric.Metric -eq 1) {
                        if ($originalMetric.Automatic -eq 'Enabled') {
                            Set-NetIPInterface -InterfaceIndex (Get-ExitDnsIndex $adapter) -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop | Out-Null
                        } else {
                            Set-NetIPInterface -InterfaceIndex (Get-ExitDnsIndex $adapter) -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric $originalMetric.Metric -ErrorAction Stop | Out-Null
                        }
                        $restoredMetric = Get-ExitDnsMetric $adapter
                        if ($restoredMetric.Automatic -ne $originalMetric.Automatic -or ($originalMetric.Automatic -eq 'Disabled' -and $restoredMetric.Metric -ne $originalMetric.Metric)) {
                            throw 'Exit DNS original interface metric was not restored'
                        }
                        $lease.MetricPending = $false
                        $lease.CachePending = $true
                    } else {
                        Write-Warning 'Exit DNS interface metric was changed externally; leaving the current metric unchanged'
                        $lease.MetricPending = $false
                    }
                } catch { $errors.Add($_.Exception.Message) }
            }

            if ($lease.CachePending) {
                try { Clear-DnsClientCache -ErrorAction Stop; $lease.CachePending = $false }
                catch { $errors.Add($_.Exception.Message) }
            }
            if (-not $lease.DnsPending -and -not $lease.MetricPending -and -not $lease.CachePending) { $script:ExitDnsLease = $null }
            if ($errors.Count -gt 0) { throw ('Exit DNS cleanup failed: ' + [string]::Join('; ', $errors)) }
        }

        function Enter-ExitDns($adapter) {
            $guid = ([guid]$adapter.InterfaceGuid).ToString('D')
            if ($null -ne $script:ExitDnsLease) {
                if ($script:ExitDnsLease.Guid -eq $guid) {
                    $currentAdapter = Find-ExitDnsAdapter $guid
                    if ($null -eq $currentAdapter) { throw 'Exit DNS adapter disappeared' }
                    $dns = Get-ExitDnsConfiguration $currentAdapter
                    $metric = Get-ExitDnsMetric $currentAdapter
                    if ($dns.IsStatic -and $dns.Key -eq '1.1.1.1,8.8.8.8' -and $metric.Automatic -eq 'Disabled' -and $metric.Metric -eq 1) { return }
                    throw 'Exit DNS active configuration was changed externally'
                }
                Exit-ExitDns
            }

            $currentAdapter = Find-ExitDnsAdapter $guid
            if ($null -eq $currentAdapter) { throw 'Exit DNS adapter disappeared' }
            $originalDns = Get-ExitDnsConfiguration $currentAdapter
            $originalMetric = Get-ExitDnsMetric $currentAdapter
            $lease = [pscustomobject]@{
                Guid = $guid; OriginalDns = $originalDns; OriginalMetric = $originalMetric
                AppliedDnsKey = '1.1.1.1,8.8.8.8'; DnsPending = $false; MetricPending = $false; CachePending = $false
            }
            $script:ExitDnsLease = $lease
            try {
                if (-not $originalDns.IsStatic -or $originalDns.Key -ne $lease.AppliedDnsKey) {
                    $lease.DnsPending = $true
                    $lease.CachePending = $true
                    Set-DnsClientServerAddress -InputObject $originalDns.DnsObject -ServerAddresses @('1.1.1.1', '8.8.8.8') -ErrorAction Stop | Out-Null
                }
                $appliedDns = Get-ExitDnsConfiguration $currentAdapter
                if (-not $appliedDns.IsStatic -or $appliedDns.Key -ne $lease.AppliedDnsKey) { throw 'Exit DNS server readback failed' }
                if ($originalMetric.Automatic -ne 'Disabled' -or $originalMetric.Metric -ne 1) {
                    $lease.MetricPending = $true
                    Set-NetIPInterface -InterfaceIndex (Get-ExitDnsIndex $currentAdapter) -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction Stop | Out-Null
                }
                $appliedMetric = Get-ExitDnsMetric $currentAdapter
                if ($appliedMetric.Automatic -ne 'Disabled' -or $appliedMetric.Metric -ne 1) { throw 'Exit DNS interface metric readback failed' }
                $lease.CachePending = $true
                Clear-DnsClientCache -ErrorAction Stop
                $lease.CachePending = $false
            } catch {
                $applyError = $_.Exception
                try { Exit-ExitDns }
                catch { throw ('Exit DNS setup failed: ' + $applyError.Message + '; ' + $_.Exception.Message) }
                throw $applyError
            }
        }
        """;
}
