<#
界面改版第 0 批技术验证（开发用）：用 AMBER_SPIKE 跑一遍 Spike.cs，同时在外面用 UI 自动化监听“实时播报”的 LiveRegionChanged 事件，
数一数 Avalonia 有没有真的发出来（读屏软件靠这个事件念状态变化）。
用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\spike-live-check.ps1 -Report <报告目录> [-Seconds 8] [-Configuration Release]
#>
param([Parameter(Mandatory = $true)][string]$Report, [int]$Seconds = 8, [string]$Configuration = "Release")
$repo = Split-Path -Parent $PSScriptRoot
$Exe = Join-Path $repo "src\Amber.App\bin\$Configuration\net10.0\Amber.exe"
if (-not (Test-Path $Exe)) { Write-Host "找不到 $Exe，先 dotnet build -c $Configuration"; exit 1 }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Windows.Automation;
public static class LiveCounter {
    public static int Live, NameChanged;
    public static List<string> Names = new List<string>();
    public static void Attach(AutomationElement win) {
        Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, win, TreeScope.Subtree,
            (s, e) => { lock (Names) { Live++; try { Names.Add(((AutomationElement)s).Current.Name); } catch { Names.Add("?"); } } });
        Automation.AddAutomationPropertyChangedEventHandler(win, TreeScope.Subtree,
            (s, e) => { lock (Names) NameChanged++; }, AutomationElement.NameProperty);
    }
    public static string Probe(AutomationElement win) {
        foreach (AutomationElement el in win.FindAll(TreeScope.Descendants, Condition.TrueCondition)) {
            string n; try { n = el.Current.Name; } catch { continue; }
            if (n != null && n.StartsWith("status ")) {
                object v;
                try { v = el.GetCurrentPropertyValue(AutomationElementIdentifiers.LiveSettingProperty); } catch (Exception ex) { v = ex.GetType().Name; }
                return "found '" + n + "' LiveSetting=" + v;
            }
        }
        return null;
    }
}
"@
New-Item -ItemType Directory -Force $Report | Out-Null
$env:AMBER_SPIKE = $Report
$env:AMBER_SPIKE_LIVE = "$Seconds"
$p = Start-Process -FilePath $Exe -PassThru
$env:AMBER_SPIKE = $null; $env:AMBER_SPIKE_LIVE = $null
$win = $null
for ($i = 0; $i -lt 80 -and $null -eq $win; $i++) {
    Start-Sleep -Milliseconds 250
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
if ($null -eq $win) { "window not found"; exit 1 }
[LiveCounter]::Attach($win)
$probe = $null
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not $probe) {
    try { $probe = [LiveCounter]::Probe($win) } catch {}
    Start-Sleep -Milliseconds 300
}
$p.WaitForExit(($Seconds + 60) * 1000) | Out-Null
Start-Sleep -Milliseconds 500
[System.Windows.Automation.Automation]::RemoveAllEventHandlers()
"probe: $probe"
"LiveRegionChanged events: $([LiveCounter]::Live); Name property-changed events: $([LiveCounter]::NameChanged)"
"names: " + ([LiveCounter]::Names -join ", ")
if (Test-Path "$Report\spike.txt") { Get-Content "$Report\spike.txt" -Encoding UTF8 }
