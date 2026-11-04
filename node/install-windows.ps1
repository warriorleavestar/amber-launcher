<#
Amber 联机节点（Windows，适合一直开着的电脑或 Windows 服务器）
Amber relay node for Windows (an always-on PC or Windows Server)

下载 EasyTier 官方发布的固定版本并校验 SHA-256，注册一个开机运行的计划任务（只做中继，不建虚拟网卡），
在 Windows 防火墙放行 TCP/UDP 端口，最后打印节点地址和 Amber 节点分享码。
用管理员 PowerShell 运行 / run in an elevated PowerShell:
  powershell -ExecutionPolicy Bypass -File install-windows.ps1 [-Port 11010] [-Relay amber|all] [-NodeName 名字] [-PublicHost 公网IP]
卸载 / uninstall:
  powershell -ExecutionPolicy Bypass -File install-windows.ps1 -Uninstall
#>
param(
    [int]$Port = 11010,
    [ValidateSet("amber", "all")][string]$Relay = "amber",
    [string]$NodeName = "",
    [string]$PublicHost = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$Version = "2.6.4"
$Sha256 = "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84"
$Dir = Join-Path $env:ProgramData "AmberNode"
$Task = "Amber relay node"
$Rule = "Amber relay node"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "请用管理员身份运行 PowerShell / run as administrator" }

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
    Get-Process easytier-core -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
    Get-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
    Write-Host "已卸载 / uninstalled"
    return
}

if ($Port -lt 1024 -or $Port -gt 65535) { throw "Port 要在 1024-65535 之间" }
$asset = "easytier-windows-x86_64-v$Version.zip"
$url = "https://github.com/EasyTier/EasyTier/releases/download/v$Version/$asset"
$tmp = Join-Path $env:TEMP ("amber-node-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Write-Host "下载 / downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $tmp $asset)
    $hash = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) { throw "SHA-256 不对，已停止 / checksum mismatch: $hash" }
    Expand-Archive -Force (Join-Path $tmp $asset) (Join-Path $tmp "x")
    New-Item -ItemType Directory -Force $Dir | Out-Null
    Get-ChildItem -Recurse (Join-Path $tmp "x") -Include "easytier-core.exe", "easytier-cli.exe", "*.dll" | Copy-Item -Destination $Dir -Force
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$bytes = New-Object byte[] 18
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$secret = -join ($bytes | ForEach-Object { $_.ToString("x2") })
$net = "amber-node-" + $secret.Substring(0, 12)
$whitelist = if ($Relay -eq "amber") { "amber-*" } else { "*" }
$arguments = "--network-name $net --network-secret $secret --no-tun --listeners tcp://0.0.0.0:$Port udp://0.0.0.0:$Port " +
             "--relay-network-whitelist `"$whitelist`" --hostname amber-node --rpc-portal 127.0.0.1:15888"

# 用权限最低的内置账户 LOCAL SERVICE 运行，开机启动，挂了自动重启。
$action = New-ScheduledTaskAction -Execute (Join-Path $Dir "easytier-core.exe") -Argument $arguments -WorkingDirectory $Dir
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\LOCAL SERVICE" -LogonType ServiceAccount
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $Task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Get-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $Rule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program (Join-Path $Dir "easytier-core.exe") | Out-Null
New-NetFirewallRule -DisplayName $Rule -Direction Inbound -Action Allow -Protocol UDP -LocalPort $Port -Program (Join-Path $Dir "easytier-core.exe") | Out-Null
Start-ScheduledTask -TaskName $Task

if (-not $PublicHost) {
    $PublicHost = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -ne "WellKnown" -and $_.IPAddress -notlike "169.254.*" } | Select-Object -First 1).IPAddress
    Write-Host "注意：猜到的地址是 $PublicHost；在家里的电脑上一般要用路由器的公网 IP，并在路由器上做端口转发。可以用 -PublicHost 指定。"
}
$json = '{"v":1'
if ($NodeName) { $json += ',"name":' + (ConvertTo-Json $NodeName) }
$json += ',"peers":["tcp://' + $PublicHost + ':' + $Port + '","udp://' + $PublicHost + ':' + $Port + '"]}'
$code = "AMBNODE-" + ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd("=").Replace("+", "-").Replace("/", "_"))

Write-Host ""
Write-Host "节点已运行 / node is running（计划任务 / scheduled task: $Task）"
Write-Host "节点地址 / node address:  tcp://${PublicHost}:$Port   udp://${PublicHost}:$Port"
Write-Host "Amber 节点分享码 / share code:"
Write-Host $code
