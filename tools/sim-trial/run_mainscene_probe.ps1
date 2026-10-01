# run_mainscene_probe.ps1 - Unreal trial A8b: the gating MainScene start-state probe of check (a) (plan A sections 1, 3.6, 4 A8b).
#
# Builds godot.csproj (Debug), launches the REAL game WINDOWED (headless takes the dedicated-server branch, plan A F38) with
#   godot --path godot -- --sim-trial-probe <abs trial_1000.json> --probe-out <file>
# and CHIMERA_MATCH_SEED pinned to the trial seed. MainScene boots normally, enters Play the way F5 does (the offline match start:
# AI plan OfflineDefault, match seed, re-apply; MainScene.cs:2985-3075), writes PreTickHashes from its own loaded inputs before the
# first StepOnce and quits by itself (godot/src/Trial/MainSceneSimTrialProbe.cs). This script then compares the six H0 + tick0 values
# with Golden/trial-1000-ai.meta.txt and prints probe_match=N/6, plus the non-gating diagnostics (seed, ai, counts, digests vs trailer).
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools/sim-trial/run_mainscene_probe.ps1 [-Runtime net8|host] [-OutDir DIR] [-SkipBuild]
#
# A Godot run is HEAVY: invoke this script through the lock, from Git Bash:
#   UE_LOCK_TAG=a/A8b bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <this>
# The window opens on the desktop and closes itself. Exit 0 = probe_match 6/6 and the pinned seed landed; 1 = a mismatch (a finding:
# plan A says STOP and report); 2 = build or run failure; 3 = timeout (the Godot process this script started is killed).
param(
    [ValidateSet('net8', 'host')][string]$Runtime = 'net8',
    [string]$OutDir = 'D:/Projects/Chimera-Unreal/TrialOut/a/probe',
    [string]$Godot = 'C:/Godot/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe',
    [int]$TimeoutSec = 180,
    [string]$Seed = '0xC0FFEE1234567890',
    [string[]]$GodotExtra = @(),   # extra engine options before `--` (diagnostics, e.g. --verbose)
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path -replace '\\', '/'
$g = "$repo/godot"
$scenario = "$g/ProjectChimera.Sim.Tests/Trial/trial_1000.json"
$meta = "$g/ProjectChimera.Sim.Tests/Golden/trial-1000-ai.meta.txt"
$trailer = "$g/ProjectChimera.Sim.Tests/Golden/trial-1000-ai.trailer.txt"
$leg = if ($Runtime -eq 'host') { 'probe_host' } else { 'probe' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$probeOut = "$OutDir/$leg.txt"
$log = "$OutDir/$leg.log"
$errLog = "$OutDir/$leg.err.log"
Remove-Item -Force -ErrorAction SilentlyContinue $probeOut, $log, $errLog

if (-not $SkipBuild) {
    Write-Host "== dotnet build godot.csproj (Debug)"
    & dotnet build "$g/godot.csproj" -c Debug --nologo -v q *> "$OutDir/$leg.build.log"
    if ($LASTEXITCODE -ne 0) { Write-Host "build failed (see $OutDir/$leg.build.log)"; exit 2 }
}

# Same runtime switch as run_godot_leg.ps1 (A8 finding: Godot's GodotPlugins.runtimeconfig.json rolls forward to the newest major,
# 10.0.12; net8 pins 8.0.25 through the host's env override, host leaves Godot alone).
if ($Runtime -eq 'net8') { $env:DOTNET_ROLL_FORWARD = 'LatestMinor' } else { Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue }
$env:CHIMERA_MATCH_SEED = $Seed  # MatchSeedProducer.PINNED_SEED_ENV (MatchSeedProducer.cs:36): 0x-hex or decimal

# Windowed on purpose: no --headless. The game quits itself after writing the probe file.
$gargs = @('--path', $g, '--windowed', '--resolution', '1280x720') + $GodotExtra + @('--', '--sim-trial-probe', $scenario, '--probe-out', $probeOut)
Write-Host "== godot $($gargs -join ' ')  (CHIMERA_MATCH_SEED=$Seed, DOTNET_ROLL_FORWARD=$($env:DOTNET_ROLL_FORWARD))"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Godot -ArgumentList $gargs -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errLog
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Write-Host "TIMEOUT after $TimeoutSec s, killing the Godot process I started (pid $($p.Id))"
    Stop-Process -Id $p.Id -Force
    exit 3
}
$p.WaitForExit()
$rc = $p.ExitCode
Write-Host ("godot_exit={0} elapsed_s={1:F1}" -f $rc, $sw.Elapsed.TotalSeconds)
if ($rc -ne 0 -or -not (Test-Path $probeOut)) {
    Write-Host "probe failed (exit $rc, out exists: $(Test-Path $probeOut))"
    Get-Content $errLog -Tail 20 -ErrorAction SilentlyContinue; Get-Content $log -Tail 30 -ErrorAction SilentlyContinue
    exit 2
}

function Read-Kv([string]$path) {
    $h = @{}
    foreach ($line in Get-Content $path) {
        if ($line -match '^#?\s*([A-Za-z0-9_.]+):\s*(.*?)\s*$') { $h[$Matches[1]] = $Matches[2] }
    }
    return $h
}
function Same([string]$a, [string]$b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    if ($a -match '^0x[0-9A-Fa-f]+$' -and $b -match '^0x[0-9A-Fa-f]+$') {
        return [System.UInt64]::Parse($a.Substring(2), 'AllowHexSpecifier') -eq [System.UInt64]::Parse($b.Substring(2), 'AllowHexSpecifier')
    }
    return $a -eq $b
}
$pr = Read-Kv $probeOut
$m = Read-Kv $meta
$tr = Read-Kv $trailer

$gated = @('hash.start_state', 'hash.canonical_model', 'hash.content', 'hash.ruleset', 'hash.agreement', 'hash.tick0')
$match = 0
foreach ($k in $gated) {
    $ok = Same $pr[$k] $m[$k]
    if ($ok) { $match++ }
    Write-Host ("{0,-22} probe={1,-20} meta={2,-20} {3}" -f $k, $pr[$k], $m[$k], $(if ($ok) { 'ok' } else { 'DIFF' }))
}
Write-Host "probe_match=$match/$($gated.Count)"

# Non-gating diagnostics: the run is the one claimed (seed, AI plan, scenario, counts) and the tick-0 world digests vs the trailer.
$diagFail = 0
foreach ($k in @('seed', 'ai', 'scenario_sha256', 'algo', 'units_at_start', 'initial_delay', 'item_registry')) {
    $ok = Same $pr[$k] $m[$k]
    if (-not $ok) { $diagFail++ }
    Write-Host ("{0,-22} probe={1,-20} meta={2,-20} {3}" -f $k, $pr[$k], $m[$k], $(if ($ok) { 'ok' } else { 'DIFF' }))
}
foreach ($k in @('digest.0', 'wide.0')) {
    $ok = Same $pr[$k] $tr[$k]
    Write-Host ("{0,-22} probe={1,-20} trailer={2,-17} {3} (reported, not gated)" -f $k, $pr[$k], $tr[$k], $(if ($ok) { 'ok' } else { 'DIFF' }))
}
Write-Host "runtime=$($pr['runtime']) host=$($pr['host']) mode=$($pr['mode']) tick=$($pr['tick']) ai_plan_mask=$($pr['ai_plan_mask']) rng_state=$($pr['rng_state'])"
Write-Host "OK probe=$probeOut"
if ($match -ne $gated.Count) { exit 1 }
if ($diagFail -ne 0) { Write-Host "FAIL: the probe did not run the claimed match (see DIFF rows above)"; exit 1 }
exit 0
