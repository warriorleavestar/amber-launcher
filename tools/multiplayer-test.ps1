<#
联机自测：用 Amber.Cli 在本机起房主和加入方两个 EasyTier（不建虚拟网卡、不需要管理员），
先只经 127.0.0.1 互连，再经 -PublicNode 指定的节点互连（随机的一次性房间），
每次都通过加入方的转发端口连一个假的“游戏”TCP 服务，确认能收到回应。
结果写到 <仓库>\测试日志\联机-<时间>.txt，结束时（包括 Ctrl+C）关掉本次启动的进程。
用法：powershell -ExecutionPolicy Bypass -File tools\multiplayer-test.ps1 [-PublicNode tcp://节点:11010] [-SkipPublic]
#>
param(
    [string]$Cli = "",
    [switch]$SkipPublic,
    [string]$PublicNode = "",
    [switch]$SkipDirect,
    [int]$LocalTimeout = 60,
    [int]$PublicTimeout = 120
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Cli) { $Cli = Join-Path $repo "src\Amber.Cli\bin\Debug\net10.0\Amber.Cli.exe" }
$logDir = Join-Path $repo "测试日志"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("联机-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")
$work = Join-Path $env:TEMP ("amber-mp-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
$started = New-Object System.Collections.ArrayList
$results = New-Object System.Collections.ArrayList

function Write-Log([string]$text) {
    $line = "[" + (Get-Date -Format "HH:mm:ss") + "] " + $text
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $word = if ($ok) { "PASS" } else { "FAIL" }
    [void]$results.Add("$word $name $detail")
    Write-Log "$word $name $detail"
}

function Get-FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

function Start-Cli([string]$name, [string[]]$arguments) {
    $out = Join-Path $work "$name.out.txt"
    $err = Join-Path $work "$name.err.txt"
    $env:AMBER_ET_VERBOSE = "1"
    $p = Start-Process -FilePath $Cli -ArgumentList $arguments -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
    [void]$started.Add($p)
    return @{ Process = $p; Out = $out; Err = $err; Name = $name }
}

function Wait-Line($proc, [string]$prefix, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $proc.Out) {
            $hit = Get-Content $proc.Out -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_.StartsWith($prefix) } | Select-Object -First 1
            if ($hit) { return $hit.Substring($prefix.Length).Trim() }
        }
        if ($proc.Process.HasExited) { return $null }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# 假的游戏服务器：在后台作业里接受连接，读一行，回 "pong:" + 那一行。
$gamePort = Get-FreePort
$serverJob = Start-Job -ArgumentList $gamePort -ScriptBlock {
    param($port)
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
    $listener.Start()
    try {
        while ($true) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $line = $reader.ReadLine()
                $bytes = [System.Text.Encoding]::UTF8.GetBytes("pong:" + $line + "`n")
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            } finally { $client.Close() }
        }
    } finally { $listener.Stop() }
}

function Test-Through([string]$address, [int]$seconds) {
    $parts = $address.Split(":")
    $deadline = (Get-Date).AddSeconds($seconds)
    $attempt = 0
    while ((Get-Date) -lt $deadline) {
        $attempt++
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $client.ReceiveTimeout = 5000
            $client.Connect($parts[0], [int]$parts[1])
            $stream = $client.GetStream()
            $bytes = [System.Text.Encoding]::UTF8.GetBytes("hello$attempt`n")
            $stream.Write($bytes, 0, $bytes.Length)
            $reply = (New-Object System.IO.StreamReader($stream)).ReadLine()
            $client.Close()
            if ($reply -eq "pong:hello$attempt") { return @{ Ok = $true; Detail = "第 $attempt 次尝试收到 $reply" } }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return @{ Ok = $false; Detail = "$seconds 秒内没有收到回应（尝试 $attempt 次）" }
}

function Save-Output($proc) {
    Write-Log "----- $($proc.Name) 输出 -----"
    foreach ($f in @($proc.Out, $proc.Err)) {
        if (Test-Path $f) { Get-Content $f -Encoding UTF8 | ForEach-Object { Add-Content -Path $log -Value ("  " + $_) -Encoding UTF8 } }
    }
}

function Save-PeerTable($proc) {
    $rpc = Wait-Line $proc "RPC " 1
    $cliExe = Wait-Line $proc "CLI " 1
    if ($rpc -and $cliExe -and (Test-Path $cliExe)) {
        Write-Log "----- $($proc.Name) easytier-cli peer（表格和 JSON）-----"
        foreach ($extra in @(@("peer"), @("-o", "json", "peer"), @("route"))) {
            try {
                $text = & $cliExe -p "127.0.0.1:$rpc" @extra 2>&1 | Out-String
                Add-Content -Path $log -Value $text -Encoding UTF8
            } catch { Write-Log "easytier-cli 失败：$_" }
        }
    }
}

function Stop-Pair($pair) {
    foreach ($p in $pair) {
        if ($p -and -not $p.Process.HasExited) { Stop-Process -Id $p.Process.Id -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 2
}

function Run-Case([string]$label, [string[]]$hostPeers, [string[]]$guestPeers, [string[]]$hostListen, [int]$timeout) {
    Write-Log "===== $label ====="
    $hostArgs = @("lan-host", "$gamePort", "--seconds", "600") + $hostPeers + $hostListen
    $h = Start-Cli "$label-房主" $hostArgs
    $room = Wait-Line $h "ROOM " 30
    if (-not $room) { Add-Result "$label 房主启动" $false "没有输出房间码"; Save-Output $h; Stop-Pair @($h); return }
    Add-Result "$label 房主启动" $true "房间码 $($room.Substring(0, 13))…（密钥不写进日志）"
    $guestArgs = @("lan-join", $room, "--seconds", "600") + $guestPeers
    $g = Start-Cli "$label-加入方" $guestArgs
    $local = Wait-Line $g "LOCAL " 30
    if (-not $local) { Add-Result "$label 加入方启动" $false "没有输出本机地址"; Save-Output $h; Save-Output $g; Stop-Pair @($h, $g); return }
    Add-Result "$label 加入方启动" $true "转发地址 $local"
    $r = Test-Through $local $timeout
    Add-Result "$label 经虚拟网络连到房主的游戏端口" $r.Ok $r.Detail
    Save-PeerTable $g
    Save-PeerTable $h
    Stop-Pair @($h, $g)
    Save-Output $h
    Save-Output $g
}

function Run-DirectCase([string]$label, [string[]]$extra, [int]$timeout) {
    Write-Log "===== $label ====="
    $h = Start-Cli "$label-房主" (@("lan-direct-host", "$gamePort", "--seconds", "600") + $extra)
    $invite = Wait-Line $h "INVITE " 60
    if (-not $invite) { Add-Result "$label 生成邀请码" $false "没有输出邀请码"; Save-Output $h; Stop-Pair @($h); return }
    Add-Result "$label 生成邀请码" $true ($invite.Substring(0, 12) + "…")
    $g = Start-Cli "$label-加入方" (@("lan-direct-join", $invite, "--seconds", "600") + $extra)
    $local = Wait-Line $g "LOCAL " 120
    if (-not $local) { Add-Result "$label 打洞并启动" $false "没有输出本机地址"; Save-Output $h; Save-Output $g; Stop-Pair @($h, $g); return }
    Add-Result "$label 打洞并启动" $true "转发地址 $local"
    $r = Test-Through $local $timeout
    Add-Result "$label 经直连桥连到房主的游戏端口" $r.Ok $r.Detail
    Start-Sleep -Seconds 3
    $path = Get-Content $g.Out -Encoding UTF8 | Where-Object { $_.StartsWith("PATH ") } | Select-Object -Last 1
    Write-Log "加入方路径：$path"
    Stop-Pair @($h, $g)
    Save-Output $h
    Save-Output $g
}

try {
    Write-Log "Amber 联机自测，日志：$log"
    Write-Log "Amber.Cli：$Cli"
    if (-not (Test-Path $Cli)) { throw "找不到 Amber.Cli.exe，先在 src 下运行 dotnet build" }
    & $Cli lan-setup 2>&1 | ForEach-Object { Write-Log $_ }
    Write-Log "假游戏服务器端口：$gamePort"

    $relay = Get-FreePort
    Run-Case "本机" @("--peer", "none") @("--peer", "tcp://127.0.0.1:$relay") @("--listen", "tcp://127.0.0.1:$relay") $LocalTimeout

    if (-not $SkipDirect) {
        Run-DirectCase "直连（本机模拟）" @("--sim") $LocalTimeout
    }

    if (-not $SkipPublic -and $PublicNode) {
        Run-Case "公共节点" @("--peer", $PublicNode) @("--peer", $PublicNode) @() $PublicTimeout
    } elseif (-not $SkipPublic) {
        Write-Log "跳过公共节点测试：没有给 -PublicNode（Amber 不内置节点）"
    }

    $etDir = Join-Path $env:APPDATA "Amber\easytier"
    $left = @(Get-Process easytier-core -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($etDir, [StringComparison]::OrdinalIgnoreCase) })
    Add-Result "结束 Amber.Cli 后 easytier-core 跟着退出" ($left.Count -eq 0) "剩 $($left.Count) 个"
}
catch {
    Write-Log "出错：$_"
}
finally {
    foreach ($p in $started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    $etDir = Join-Path $env:APPDATA "Amber\easytier"
    Get-Process easytier-core -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($etDir, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
    # 后台作业卡在 AcceptTcpClient 上，Stop-Job 会等很久；直接强制移除。
    Remove-Job $serverJob -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Write-Log "===== 汇总 ====="
    foreach ($r in $results) { Write-Log $r }
    Write-Log "日志已保存：$log"
}
