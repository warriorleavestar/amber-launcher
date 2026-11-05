<#
运行 Jay 的界面自检（开发用）：真实窗口里走一遍安装、启动、联机等流程，结果写进报告目录的 report.txt。
用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-selftest.ps1 -Report <报告目录> [-Configuration Release]
#>
param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$Configuration = "Release",
    [int]$TimeoutSeconds = 1500
)

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\Amber.App\bin\$Configuration\net10.0\Jay.exe"
if (-not (Test-Path $exe)) { Write-Host "找不到 $exe，先 dotnet build -c $Configuration"; exit 1 }
New-Item -ItemType Directory -Force $Report | Out-Null
$env:AMBER_SELFTEST = (Resolve-Path $Report).Path
$p = Start-Process -FilePath $exe -PassThru
$env:AMBER_SELFTEST = $null
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "自检超时，结束 Jay"
    Stop-Process -Id $p.Id -Force
}
$reportFile = Join-Path $Report "report.txt"
if (Test-Path $reportFile) { Get-Content $reportFile -Encoding UTF8 } else { Write-Host "没有生成 report.txt" }
