# GUI smoke test: drives the real exe through UI Automation and screenshots the result.
param(
    [string]$Template = $(throw "pass -Template <file.serps>"),
    [string]$Donor = "Ferrari",
    [string]$EntryMatch = "FOM chassis",
    [string]$Out = (Join-Path $PSScriptRoot "out\gui_test.serps"),
    [string]$Shot = (Join-Path $PSScriptRoot "out\gui_test.png")
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
public struct R { public int L,T,Rt,B; } }
"@
[Win]::SetProcessDPIAware() | Out-Null
$exe = Join-Path $PSScriptRoot "..\dist\Steering Wheel Swapper.exe"
Remove-Item $Out -ErrorAction SilentlyContinue
$Report = Join-Path (Split-Path $Out) ("{0} - report.html" -f [IO.Path]::GetFileNameWithoutExtension($Out))
Remove-Item $Report -ErrorAction SilentlyContinue
# the app saves its settings (game folder, donor, report option); keep the user's copy and put it back at the end
$Settings = Join-Path $env:APPDATA "SteeringWheelSwapper\settings.json"
$SettingsBackup = if (Test-Path $Settings) { Get-Content $Settings -Raw } else { $null }
$p = Start-Process $exe -ArgumentList "`"$Template`"" -PassThru
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $win = $A::FromHandle($p.MainWindowHandle) } }
function Find($name, $type) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type)),
        (New-Object System.Windows.Automation.PropertyCondition($A::IsEnabledProperty, $true)))
    $win.FindAll($T::Descendants, $c) | Where-Object { $_.Current.Name -like "*$name*" } | Select-Object -First 1
}
$ct = [System.Windows.Automation.ControlType]
# wait for the template to load (its cars appear as checkboxes), then tick the entry
$cb = $null
for ($i = 0; $i -lt 120 -and -not $cb; $i++) { Start-Sleep -Milliseconds 500; $cb = Find $EntryMatch $ct::CheckBox }
$cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
# ask for the report (off by default)
$rb = Find "Save a report" $ct::CheckBox
$tp = $rb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
# pick the donor chip
$chip = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) |
        Where-Object { $_.Current.Name -eq $Donor } | Select-Object -First 1
$chip.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
# output path (last Edit box)
$edits = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Edit)))
$outBox = $edits[$edits.Count - 1]; $outBox.SetFocus()
$outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Out)
# build
(Find "Build" $ct::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$done = $false
for ($i = 0; $i -lt 240 -and -not $done; $i++) { Start-Sleep -Milliseconds 500
    $texts = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name }
    $done = [bool]($texts | Where-Object { $_ -like "Done.*" -or $_ -like "Failed:*" }) }
Start-Sleep -Milliseconds 500
$r = New-Object Win+R; [Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot)
$texts | Where-Object { $_ -match "^(Done|Failed|Warning|Verified|Ferrari|.*->)" } | Select-Object -Last 8
"output exists: $(Test-Path $Out)  size: $([math]::Round((Get-Item $Out -ErrorAction SilentlyContinue).Length/1MB,1)) MB"
$verified = [bool]($texts | Where-Object { $_ -like "Verified: *checks passed*" })
for ($i = 0; $i -lt 60 -and -not (Test-Path $Report); $i++) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Milliseconds 500
$reportBtn = Find "Open report" $ct::Button
$btnVisible = [bool]($reportBtn -and -not $reportBtn.Current.IsOffscreen)
"report exists: $(Test-Path $Report)  button visible: $btnVisible"
# preview: same build in memory, a 3D page in %TEMP%\SteeringWheelSwapper (opens in the default browser)
$pvDir = Join-Path ([IO.Path]::GetTempPath()) "SteeringWheelSwapper"
$pvStart = Get-Date
(Find "Preview" $ct::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$pvDone = $false
for ($i = 0; $i -lt 240 -and -not $pvDone; $i++) { Start-Sleep -Milliseconds 500
    $pvTexts = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name }
    $pvDone = [bool]($pvTexts | Where-Object { $_ -like "Preview opened*" -or $_ -like "Preview failed*" }) }
$pvFile = Get-ChildItem $pvDir -Filter "Preview - *.html" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $pvStart } | Select-Object -First 1
$pvOk = [bool]$pvFile -and -not ($pvTexts | Where-Object { $_ -like "Preview failed*" }) -and ((Get-Content $pvFile.FullName -Raw) -like "*pv-canvas*")
"preview: $(if ($pvFile) { "$($pvFile.Name)  $([math]::Round($pvFile.Length/1MB,1)) MB" } else { "none" })  ok: $pvOk"
$ok = $verified -and (Test-Path $Report) -and $btnVisible -and (Test-Path $Out) -and $pvOk -and -not ($texts | Where-Object { $_ -like "Failed:*" })
if ($ok) { "GUI SMOKE: PASS" } else { "GUI SMOKE: FAIL (verified=$verified)" }
$p.CloseMainWindow() | Out-Null
$p.WaitForExit(10000) | Out-Null
if ($null -ne $SettingsBackup) { Set-Content $Settings $SettingsBackup -NoNewline -Encoding UTF8 } else { Remove-Item $Settings -ErrorAction SilentlyContinue }
