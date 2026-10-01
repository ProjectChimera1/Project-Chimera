# run_mainscene_replay.ps1 - Unreal trial A14: the full-MainScene replay of trial_1000 (plan A section 4 A14).
# MEASURED AND REPORTED, NOT GATING.
#
# 1. Builds godot.csproj (Debug) and tools/sim-trial/chmr (Release).
# 2. ChmrTool write: trial_1000.orders.csv -> a replay v8 .chmr (frozen MergedTickPacket codec, CSV order; see
#    tools/sim-trial/chmr/Program.cs for why a tick spans several frames). ChmrTool verify: the managed control, the
#    .chmr played through the REAL ReplayPlayer on a SimSession, compared tick by tick with the golden.
# 3. Launches the REAL game WINDOWED (headless takes the dedicated-server branch, plan A F38):
#      godot --path godot -- --sim-trial-replay <chmr> --sim-trial-replay-scenario <json> --sim-trial-replay-out <file>
#    MainScene boots, TryLoadReplay re-gates and plays the .chmr at 1x through its own _Process replay branch, every
#    presentation system live; the DEBUG watcher (godot/src/Trial/MainSceneSimTrialReplay.cs) quits after the replay.
# 4. compare_mainscene_replay.py: Godot's own [Checksum] lines (interval 60 by default) vs the golden.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools/sim-trial/run_mainscene_replay.ps1 [-Variant ai|main]
#        [-Runtime net8|host] [-ChecksumEvery N] [-Tag NAME] [-OutDir DIR] [-SkipBuild]
#
# A Godot run is HEAVY: invoke this script through the lock, from Git Bash:
#   UE_LOCK_TAG=a/A14 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <this> ...
# Exit 0 = every sample equals the golden; 1 = a divergence or short replay (the A14 finding, reported); 2 = build,
# conversion or run failure; 3 = timeout with no summary (the Godot process this script started is killed).
param(
    [ValidateSet('ai', 'main')][string]$Variant = 'ai',
    [ValidateSet('net8', 'host')][string]$Runtime = 'net8',
    [int]$ChecksumEvery = 0,   # 0 = leave the game's own interval (60)
    [string]$Tag = '',
    [string]$OutDir = 'D:/Projects/Chimera-Unreal/TrialOut/a/a14',
    [string]$Godot = 'C:/Godot/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe',
    [int]$TimeoutSec = 420,
    [string]$Seed = '0xC0FFEE1234567890',
    [string[]]$GodotExtra = @(),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path -replace '\\', '/'
$g = "$repo/godot"
$scenario = "$g/ProjectChimera.Sim.Tests/Trial/trial_1000.json"
$orders = "$g/ProjectChimera.Sim.Tests/Trial/trial_1000.orders.csv"
$golden = if ($Variant -eq 'ai') { "$g/ProjectChimera.Sim.Tests/Golden/trial-1000-ai.golden.txt" } else { "$g/ProjectChimera.Sim.Tests/Golden/trial-1000.golden.txt" }
$aiArg = @(if ($Variant -eq 'ai') { '--ai' })  # @(...) keeps a one-element array (a bare if would unroll it to a string)
$leg = if ($Tag) { $Tag } else { "replay_${Variant}_${Runtime}" + $(if ($ChecksumEvery -gt 0) { "_every$ChecksumEvery" } else { '' }) }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$chmr = "$OutDir/trial_1000_$Variant.chmr"
$log = "$OutDir/$leg.log"
$errLog = "$OutDir/$leg.err.log"
$summary = "$OutDir/$leg.summary.txt"
Remove-Item -Force -ErrorAction SilentlyContinue $log, $errLog, $summary

$tool = "$repo/tools/sim-trial/chmr/bin/Release/net8.0/ChmrTool.exe"
if (-not $SkipBuild) {
    Write-Host "== dotnet build godot.csproj (Debug)"
    & dotnet build "$g/godot.csproj" -c Debug --nologo -v q *> "$OutDir/$leg.build.log"
    if ($LASTEXITCODE -ne 0) { Write-Host "godot build failed (see $OutDir/$leg.build.log)"; exit 2 }
    Write-Host "== dotnet build tools/sim-trial/chmr (Release)"
    & dotnet build "$repo/tools/sim-trial/chmr/ChmrTool.csproj" -c Release --nologo -v q *> "$OutDir/$leg.chmr_build.log"
    if ($LASTEXITCODE -ne 0) { Write-Host "chmr tool build failed (see $OutDir/$leg.chmr_build.log)"; exit 2 }
}

Write-Host "== ChmrTool write ($Variant)"
& $tool write --content $g --scenario $scenario --orders $orders --seed $Seed @aiArg --end-tick 1440 --out $chmr | Tee-Object -FilePath "$OutDir/$leg.chmr_write.txt"
if ($LASTEXITCODE -ne 0) { Write-Host "chmr write failed"; exit 2 }
Write-Host "== ChmrTool verify ($Variant): managed control through the real ReplayPlayer"
& $tool verify --content $g --scenario $scenario --chmr $chmr --seed $Seed @aiArg --ticks 1440 --golden $golden --out "$OutDir/${leg}_managed_trace.txt" | Tee-Object -FilePath "$OutDir/$leg.chmr_verify.txt"
$managedRc = $LASTEXITCODE
Write-Host "managed_control_rc=$managedRc"

if ($Runtime -eq 'net8') { $env:DOTNET_ROLL_FORWARD = 'LatestMinor' } else { Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue }
$env:CHIMERA_MATCH_SEED = $Seed  # not read by the replay path (the .chmr header seeds the RNG); pinned for parity with A8b

$userArgs = @('--', '--sim-trial-replay', $chmr, '--sim-trial-replay-scenario', $scenario, '--sim-trial-replay-out', $summary)
if ($ChecksumEvery -gt 0) { $userArgs += @('--sim-trial-checksum-every', "$ChecksumEvery") }
$gargs = @('--path', $g, '--windowed', '--resolution', '1280x720') + $GodotExtra + $userArgs
Write-Host "== godot $($gargs -join ' ')  (DOTNET_ROLL_FORWARD=$($env:DOTNET_ROLL_FORWARD))"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Godot -ArgumentList $gargs -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errLog
$timedOut = -not $p.WaitForExit($TimeoutSec * 1000)
if ($timedOut) {
    Write-Host "TIMEOUT after $TimeoutSec s, killing the Godot process I started (pid $($p.Id))"
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Milliseconds 500
} else { $p.WaitForExit() }
$rc = if ($timedOut) { 'timeout' } else { $p.ExitCode }
Write-Host ("godot_exit={0} elapsed_s={1:F1}" -f $rc, $sw.Elapsed.TotalSeconds)
if (-not (Test-Path $summary)) {
    Write-Host "no watcher summary written"
    Get-Content $errLog -Tail 20 -ErrorAction SilentlyContinue; Get-Content $log -Tail 30 -ErrorAction SilentlyContinue
    if ($timedOut) { exit 3 } else { exit 2 }
}
if ($timedOut) { Write-Host "NOTE: the summary exists, so the replay finished and the process hung after Quit (killed)" }

Write-Host "== compare (Godot's own [Checksum] lines vs the golden)"
& python "$repo/tools/sim-trial/compare_mainscene_replay.py" --log $log --summary $summary --set $Variant --managed "$OutDir/${leg}_managed_trace.txt" --json "$OutDir/$leg.compare.json" | Tee-Object -FilePath "$OutDir/$leg.compare.txt"
$cmpRc = $LASTEXITCODE
Write-Host "managed_control_rc=$managedRc compare_rc=$cmpRc godot_exit=$rc"
if ($managedRc -ne 0) { exit 2 }
exit $cmpRc
