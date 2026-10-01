# run_godot_leg.ps1 - Unreal trial A8: the Godot headless leg of check (a) (plan A section 3.6, 4 A8).
#
# Builds godot.csproj (Debug, the assembly Godot loads), runs res://scenes/sim_trial.tscn headless under Godot 4.6.3 for
# one variant, kills it after -TimeoutSec, then checks the exit code, the trace file and the [Checksum] lines.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools/sim-trial/run_godot_leg.ps1 -Variant main|ai [-OutDir DIR] [-SkipBuild]
#
# A Godot run is HEAVY: invoke this script through the lock, from Git Bash:
#   UE_LOCK_TAG=a/A8 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <this> -Variant main
# Outputs in -OutDir (default D:/Projects/Chimera-Unreal/TrialOut/a/godot): godot[_ai].trace.txt, godot[_ai].log,
# godot[_ai].build.log. Exit 0 = ran, trace written, 24 (ticks/60) native [Checksum] lines equal the golden's samples.
# Exit 1 = a check failed, 2 = build or run failure, 3 = timeout.
param(
    [Parameter(Mandatory = $true)][ValidateSet('main', 'ai')][string]$Variant,
    [string]$OutDir = 'D:/Projects/Chimera-Unreal/TrialOut/a/godot',
    [string]$Godot = 'C:/Godot/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe',
    [int]$TimeoutSec = 300,
    [int]$Ticks = 1440,
    [ValidateSet('net8', 'host')][string]$Runtime = 'net8',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path -replace '\\', '/'
$g = "$repo/godot"
$trial = "$g/ProjectChimera.Sim.Tests/Trial"
$goldenName = if ($Variant -eq 'ai') { 'trial-1000-ai' } else { 'trial-1000' }
$golden = "$g/ProjectChimera.Sim.Tests/Golden/$goldenName.golden.txt"
$leg = if ($Variant -eq 'ai') { 'godot_ai' } else { 'godot' }
if ($Runtime -eq 'host') { $leg = $leg -replace '^godot', 'godot_host' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$trace = "$OutDir/$leg.trace.txt"
$log = "$OutDir/$leg.log"
$errLog = "$OutDir/$leg.err.log"
Remove-Item -Force -ErrorAction SilentlyContinue $trace, $log, $errLog

if (-not $SkipBuild) {
    Write-Host "== dotnet build godot.csproj (Debug)"
    & dotnet build "$g/godot.csproj" -c Debug --nologo -v q *> "$OutDir/$leg.build.log"
    if ($LASTEXITCODE -ne 0) { Write-Host "build failed (see $OutDir/$leg.build.log)"; exit 2 }
}

$commit = (& git -C $repo rev-parse HEAD).Trim()
$dirtyOut = (& git -C $repo status --porcelain -- godot tools/sim-trial) -join ''
$dirty = if ($dirtyOut.Trim().Length -eq 0) { '0' } else { '1' }

$gargs = @('--headless', '--path', $g, 'res://scenes/sim_trial.tscn', '--',
    '--scenario', "$trial/trial_1000.json", '--orders', "$trial/trial_1000.orders.csv",
    '--content', $g, '--ticks', "$Ticks", '--out', $trace, '--commit', $commit, '--dirty', $dirty)
$gargs += @('--leg', $leg)
if ($Variant -eq 'ai') { $gargs += '--ai' }
# Godot's own GodotPlugins.runtimeconfig.json says rollForward LatestMajor, so the hosted runtime is the newest installed (10.0.12),
# not the project's net8.0 (A8 finding, F7 corrected). -Runtime net8 pins the 8.0.x line (8.0.25) through the host's env override;
# -Runtime host leaves Godot alone (what the game really runs on).
if ($Runtime -eq 'net8') { $env:DOTNET_ROLL_FORWARD = 'LatestMinor' } else { Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue }

Write-Host "== godot $($gargs -join ' ')"
$p = Start-Process -FilePath $Godot -ArgumentList $gargs -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errLog
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Write-Host "TIMEOUT after $TimeoutSec s, killing the Godot process I started (pid $($p.Id))"
    Stop-Process -Id $p.Id -Force
    exit 3
}
$p.WaitForExit()
$rc = $p.ExitCode
Write-Host "godot_exit=$rc"
if ($rc -ne 0) { Get-Content $errLog -Tail 20; Get-Content $log -Tail 20; exit 2 }
if (-not (Test-Path $trace)) { Write-Host "no trace written"; exit 1 }

$rt = ((Get-Content $trace -TotalCount 12) | Where-Object { $_ -match '^# runtime: (.*)$' } | ForEach-Object { $Matches[1] })
Write-Host "runtime=$rt"
if ($Runtime -eq 'net8' -and $rt -notmatch '^\.NET 8\.0\.') { Write-Host "FAIL: -Runtime net8 but Godot hosted '$rt'"; exit 1 }

# the native [Checksum] lines vs the golden
$want = @{}
foreach ($line in Get-Content $golden) { if ($line -match '^(\d+)\s+([0-9A-F]{8})\s*$') { $want[[int]$Matches[1]] = $Matches[2] } }
$seen = @()
foreach ($line in Get-Content $log) { if ($line -match '^\[Checksum\] tick=(\d+) hash=0x([0-9A-F]{8})') { $seen += , @([int]$Matches[1], $Matches[2]) } }
$expectedLines = [math]::Floor($Ticks / 60)
$equal = 0
foreach ($s in $seen) { if ($want.ContainsKey($s[0]) -and $want[$s[0]] -eq $s[1]) { $equal++ } }
Write-Host "checksum_lines=$($seen.Count) equal_golden=$equal expected=$expectedLines"
if ($seen.Count -ne $expectedLines -or $equal -ne $expectedLines) { Write-Host "FAIL: [Checksum] lines"; exit 1 }
Write-Host "OK trace=$trace"
exit 0
