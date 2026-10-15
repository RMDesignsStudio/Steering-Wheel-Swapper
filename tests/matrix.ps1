# Dry-run test matrix: every donor onto the Aston FOM + MyTeam entries, and one donor onto every
# team template's own entries. Templates are only read. Summarises result + warnings per run.
param(
    [string]$Game = "D:\SteamLibrary\steamapps\common\F1 25",
    [string]$Templates = "E:\F1 25 - 2026 Season Pack Mods\SERPs Files\.SERPs Files"
)
$cli = Join-Path $PSScriptRoot "..\src\WheelSwap.Cli\bin\Release\net10.0\WheelSwap.Cli.exe"
$donors = "fom_car","force_india","ferrari","gm_cadillac","haas","lotus","mclaren","mercedes","redbull","sauber","toro_rosso","williams"
$results = @()

function Run($tpl, $donor) {
    $out = & $cli build $tpl --donor $donor --no-write --game $Game 2>&1 | Out-String
    $ok = $out -match "Dry run: nothing written"
    $warn = ([regex]::Matches($out, "WARNING: (.+)") | ForEach-Object { $_.Groups[1].Value.Trim() } | Sort-Object -Unique) -join " | "
    $err = if ($ok) { "" } else { ([regex]::Match($out, "(Unhandled exception\.[^\r\n]+|Exception: [^\r\n]+)")).Value }
    [pscustomobject]@{ Template = [IO.Path]::GetFileNameWithoutExtension($tpl).Replace(" - SERPs Template v1.1",""); Donor = $donor; OK = $ok; Error = $err; Warnings = $warn }
}

$aston = Join-Path $Templates "2026 Aston Martin - SERPs Template v1.1.serps"
$myteam = Join-Path $Templates "2026 My Team - SERPs Template v1.1.serps"
foreach ($d in $donors) {
    if ($d -ne "force_india" -and $d -ne "fom_car") { $results += Run $aston $d }
    if ($d -ne "fom_car") { $results += Run $myteam $d }
}
foreach ($t in Get-ChildItem $Templates -Filter "2026 *.serps") {
    if ($t.Name -match "Aston|My Team") { continue }
    $donor = if ($t.Name -match "Racing Bulls") { "williams" } else { "toro_rosso" }
    $results += Run $t.FullName $donor
}
$results | ForEach-Object { "{0,-22} {1,-12} {2,-5} {3}" -f $_.Template, $_.Donor, $_.OK, ($(if ($_.OK) { $_.Warnings } else { "ERROR " + $_.Error })) }
"{0} of {1} builds OK" -f ($results | Where-Object OK).Count, $results.Count
