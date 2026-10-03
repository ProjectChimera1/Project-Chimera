#requires -Version 7
<#
.SYNOPSIS
  One Unreal -game sim run of check (a) (plan A 3.7 "run_sim.ps1", A10): trial_1000 through ChimeraSimHost, trace to
  TrialOut/a/unreal/<Tag>/.
.DESCRIPTION
  Start it from Git Bash with MSYS_NO_PATHCONV=1 (EXECUTION 2.2; PLAN_DELTA D7) and a lock tag:
    UE_LOCK_TAG=a/A10 MSYS_NO_PATHCONV=1 pwsh -NoProfile -File run_sim.ps1 -Tag main
  The outer call runs KIT/preflight.ps1 (desktop 1920x1080 @96; -Perf adds -Quiet) BEFORE taking the lock, so a wrong desktop
  fails without a hold, then re-invokes itself with -Inner under ue_lock.sh (one -game run per hold; a nested call inside a
  held command runs directly). The inner call launches UnrealEditor.exe -game on
  <Map>?game=/Script/ChimeraSimHost.ChimeraSimGameMode (the run_fps.ps1 launch pattern), tails run.log and aborts within
  seconds on "Failed to load game mode", on "LogChimeraSim: Error" (the director exits itself; killed after 30 s if not),
  or when no "loaded path=" appears within -LoadTimeoutSec; hard timeout = warm-up cap + ticks/30 + 180 s (+ the load
  timeout for engine start-up). After exit it checks: exit 0, one "loaded path=" with a full path, one RESULT line, the echoed
  seed equals -Seed as a 0x16 hex number, the echoed shot list equals shots.json, trace.txt exists. -ExpectFail inverts the
  verdict for an error run: a non-zero exit, a LogChimeraSim Error line, no RESULT, no trace.txt.
  Writes run_sim.json (all checks, exit code, timings) next to the trace. Exit 0 = every check passed; 1 = a check failed;
  10 = preflight failed (nothing launched); 11 = machine not quiet (-Perf).
  -Perf (A12): the quiet preflight, plus CPU seconds of dotnet/ilc/cl/link/MSBuild/godot* over the run into perf.json (quiet=0
  when any used > 5% of a core).
  A11: every run draws the units (AChimeraUnitRenderer) and writes verify.json; shot pairs go to <Tag>/shots/, film frames to
  <Tag>/film/. -Shots none runs without shots (verify still runs at ticks 0/300/900/1440); -DeadUnderGround is plan A 3.7's
  fallback for dead instances; -NoShotFreeze takes shot pairs without the exposure hold and TSR freeze; -VerifyEvery N adds
  verify holds every N ticks (diagnostic runs, e.g. a long ai run with -Ticks above 1440: its trace has no golden).
#>
param(
    [Parameter(Mandatory)][string]$Tag,
    [ValidateSet('main', 'ai')][string]$Variant = 'main',
    [string]$Leg = '',
    [int]$MaxFps = 0,
    [int]$HitchMs = 0,
    [int]$HitchEvery = 0,
    [int]$Ticks = 1440,
    [string]$Content = 'D:/Projects/Project_Chimera/godot',
    [string]$Scenario = '',
    [string]$Orders = '',
    [string]$Seed = '0xC0FFEE1234567890',
    [string]$Shots = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial/shots.json',
    [int]$WarmupMaxSec = 900,
    [string]$Map = '/Engine/Maps/Entry',
    [switch]$NoArena,
    [switch]$HideUnits,
    [int]$FilmEvery = 0,
    [switch]$Windowed,
    [int]$CsvFrames = 0,
    [switch]$ExpectFail,
    [switch]$Perf,
    [int]$LoadTimeoutSec = 300,
    [switch]$DeadUnderGround,
    [switch]$NoShotFreeze,
    [int]$VerifyEvery = 0,
    [switch]$Inner
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$U        = 'D:/Projects/Chimera-Unreal'
$Proj     = "$U/ProjectChimera"
$UE       = 'D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe'
$KIT      = 'D:/Projects/Project_Chimera/tools/unreal-trial'
$GitBash  = 'C:/Program Files/Git/bin/bash.exe'
$OutRoot  = "$U/TrialOut/a/unreal"
$Out      = "$OutRoot/$Tag"
if (-not $Scenario) { $Scenario = "$Content/ProjectChimera.Sim.Tests/Trial/trial_1000.json" }
if (-not $Orders)   { $Orders = "$Content/ProjectChimera.Sim.Tests/Trial/trial_1000.orders.csv" }
if (-not $Leg)      { $Leg = "unreal_$Tag" }

# ---------------------------------------------------------------- outer: preflight, then the lock
if (-not $Inner) {
    $pfArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$KIT/preflight.ps1")
    if ($Perf) { $pfArgs += '-Quiet' }
    $pf = & pwsh @pfArgs 2>&1 | Out-String
    $pfRc = $LASTEXITCODE
    Write-Host ("run_sim: preflight rc={0}`n{1}" -f $pfRc, $pf.Trim())
    if ($pfRc -eq 2) { Write-Host 'run_sim: machine not quiet; not launched'; exit 11 }
    if ($pfRc -ne 0) { Write-Host 'run_sim: preflight failed; not launched (no lock taken)'; exit 10 }

    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Inner')
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Key -eq 'Inner') { continue }
        if ($kv.Value -is [System.Management.Automation.SwitchParameter]) { if ($kv.Value.IsPresent) { $pass += "-$($kv.Key)" } }
        else { $pass += "-$($kv.Key)"; $pass += "$($kv.Value)" }
    }
    if (-not $env:UE_LOCK_TAG) { $env:UE_LOCK_TAG = "a/A10-$Tag" }
    $env:MSYS_NO_PATHCONV = '1'
    & $GitBash "$U/ue_lock.sh" pwsh @pass
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------- inner: one -game run under the lock
function Read-New([string]$Path, [ref]$Pos) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $fs = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $Pos.Value) { return '' }
        $null = $fs.Seek($Pos.Value, 'Begin')
        $sr = New-Object System.IO.StreamReader($fs)
        $t = $sr.ReadToEnd()
        $Pos.Value = $fs.Length
        return $t
    } finally { $fs.Dispose() }
}

function Get-CpuSeconds {
    $names = 'dotnet', 'ilc', 'cl', 'link', 'MSBuild'
    $m = @{}
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        if ($names -contains $p.ProcessName -or $p.ProcessName -like 'godot*') {
            try { $m["$($p.ProcessName)#$($p.Id)"] = $p.TotalProcessorTime.TotalSeconds } catch { }
        }
    }
    return $m
}

$running = @(Get-Process -Name 'UnrealEditor*' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { Write-Host "run_sim: UnrealEditor already running (pid $($running.Id -join ',')); refusing"; exit 1 }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
foreach ($f in 'run.log', 'trace.txt', 'trace.partial.txt', 'frames.csv', 'run_sim.json', 'perf.json', 'profile.csv', 'verify.json', 'shots', 'film') {
    $p = Join-Path $Out $f
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -Recurse }   # stale files never pass as this run's
}
$log = "$Out/run.log"

$pCommit = (& git -C $U rev-parse HEAD 2>$null | Out-String).Trim()
$pStatus = (& git -C $U status --porcelain -- ProjectChimera/Source ProjectChimera/SimTrial ProjectChimera/ProjectChimera.uproject 2>$null | Out-String).Trim()
$pDirty  = if ($pStatus.Length -eq 0) { '0' } else { '1' }

$res = if ($Windowed) { @('-windowed', '-ResX=1280', '-ResY=720') } else { @('-fullscreen', '-ResX=1920', '-ResY=1080') }
$a = @("`"$Proj/ProjectChimera.uproject`"", "$Map`?game=/Script/ChimeraSimHost.ChimeraSimGameMode", '-game') + $res + @(
    '-novsync', '-nosound', '-unattended', '-nosplash', "-ABSLOG=$log",
    "-ExecCmds=`"t.MaxFPS $MaxFps,r.VSync 0`"",
    "-ChimeraSimContent=$Content", "-ChimeraSimScenario=$Scenario", "-ChimeraSimOrders=$Orders", "-ChimeraSimSeed=$Seed",
    "-ChimeraSimTicks=$Ticks", "-ChimeraSimOut=$Out", "-ChimeraSimShots=$Shots", "-ChimeraSimWarmupMaxSec=$WarmupMaxSec",
    "-ChimeraSimLeg=$Leg", "-ChimeraSimPCommit=$pCommit", "-ChimeraSimPDirty=$pDirty", '-ChimeraSimExitWhenDone')
if ($Variant -eq 'ai') { $a += '-ChimeraSimAi' }
if ($HitchMs -gt 0 -and $HitchEvery -gt 0) { $a += "-ChimeraSimHitchMs=$HitchMs"; $a += "-ChimeraSimHitchEvery=$HitchEvery" }
if ($NoArena) { $a += '-ChimeraSimNoArena' }
if ($HideUnits) { $a += '-ChimeraSimHideUnits' }
if ($FilmEvery -gt 0) { $a += "-ChimeraSimFilmEvery=$FilmEvery" }
if ($DeadUnderGround) { $a += '-ChimeraSimDeadUnderGround' }
if ($NoShotFreeze) { $a += '-ChimeraSimNoShotFreeze' }
if ($VerifyEvery -gt 0) { $a += "-ChimeraSimVerifyEvery=$VerifyEvery" }
if ($CsvFrames -gt 0) { $a += "-csvCaptureFrames=$CsvFrames"; $a += '-csvCompression=0' }

$hardS = $WarmupMaxSec + [math]::Ceiling($Ticks / 30.0) + 180 + $LoadTimeoutSec
Write-Host "run_sim: tag=$Tag variant=$Variant leg=$Leg maxfps=$MaxFps hitch=$HitchMs/$HitchEvery hard_timeout=${hardS}s"
Write-Host "run_sim: $UE $($a -join ' ')"

$cpu0 = if ($Perf) { Get-CpuSeconds } else { @{} }
$t0 = Get-Date
$t0Utc = $t0.ToUniversalTime()
$p = Start-Process $UE -ArgumentList $a -PassThru
$null = $p.Handle   # caches the handle so ExitCode is readable after exit
$pos = 0L
$seenLoaded = $false; $seenError = $null; $abort = $null; $errorAt = $null
while (-not $p.HasExited) {
    Start-Sleep -Milliseconds 500
    $text = Read-New $log ([ref]$pos)
    if ($text) {
        if (-not $seenLoaded -and $text -match 'LogChimeraSim: Display: loaded path=') { $seenLoaded = $true }
        if (-not $seenError) {
            $m = [regex]::Match($text, 'LogChimeraSim: Error: [^\r\n]*')
            if ($m.Success) { $seenError = $m.Value; $errorAt = Get-Date; Write-Host "run_sim: $seenError" }
        }
        if ($text -match 'Failed to load game mode') { $abort = 'Failed to load game mode'; break }
    }
    $el = ((Get-Date) - $t0).TotalSeconds
    if ($seenError -and ((Get-Date) - $errorAt).TotalSeconds -gt 30) { $abort = 'error logged but the process did not exit within 30 s'; break }
    if (-not $seenLoaded -and $el -gt $LoadTimeoutSec) { $abort = "no 'loaded path=' within $LoadTimeoutSec s"; break }
    if ($el -gt $hardS) { $abort = "hard timeout $hardS s"; break }
}
if (-not $p.HasExited) {
    Write-Host "run_sim: ABORT ($abort); stopping our child pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
    $null = $p.WaitForExit(30000)
}
$p.WaitForExit()
$elapsed = ((Get-Date) - $t0).TotalSeconds
$exit = $p.ExitCode
Write-Host ("run_sim: exit={0} after {1:n1} s" -f $exit, $elapsed)

$logText = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
$lines = $logText -split "`r?`n"
$loaded = @($lines | Where-Object { $_ -match 'LogChimeraSim: Display: loaded path=' })
$results = @($lines | Where-Object { $_ -match 'LogChimeraSim: Display: RESULT ' })
$errors = @($lines | Where-Object { $_ -match 'LogChimeraSim: Error: ' })
$seedEcho = @($lines | Where-Object { $_ -match 'LogChimeraSim: Display: seed=(\S+)' } | ForEach-Object { ([regex]::Match($_, 'seed=(\S+)')).Groups[1].Value })
$shotsEcho = @($lines | Where-Object { $_ -match 'LogChimeraSim: Display: shots=(\S+)' } | ForEach-Object { ([regex]::Match($_, 'shots=(\S+)')).Groups[1].Value })
$loadedPath = if ($loaded.Count -ge 1) { ([regex]::Match($loaded[0], 'loaded path=(\S+)')).Groups[1].Value } else { '' }

# Expected echoes: the seed as a 0x + 16 hex number, the shot list as "tick:camera,..." from shots.json.
$seedNum = if ($Seed -match '^0[xX]') { [Convert]::ToUInt64($Seed.Substring(2), 16) } else { [UInt64]::Parse($Seed) }
$seedWant = '0x{0:X16}' -f $seedNum
$shotWant = 'none'
if ($Shots -and $Shots -ne 'none' -and (Test-Path -LiteralPath $Shots)) {
    $j = Get-Content -LiteralPath $Shots -Raw | ConvertFrom-Json
    $shotWant = (@($j) | ForEach-Object { "$($_.tick):$($_.camera)" }) -join ','
}
$trace = "$Out/trace.txt"
$checks = [ordered]@{}
if ($ExpectFail) {
    $checks['exit_nonzero'] = ($exit -ne 0)
    $checks['error_logged'] = ($errors.Count -ge 1)
    $checks['no_result'] = ($results.Count -eq 0)
    $checks['no_trace'] = -not (Test-Path -LiteralPath $trace)
    $checks['self_exit'] = ($null -eq $abort)
    $checks['within_120s'] = ($elapsed -le 120)
} else {
    $checks['exit_0'] = ($exit -eq 0)
    $checks['no_abort'] = ($null -eq $abort)
    $checks['loaded_once'] = ($loaded.Count -eq 1)
    $checks['loaded_full_path'] = ($loadedPath -match '^[A-Za-z]:/')
    $checks['result_once'] = ($results.Count -eq 1)
    $checks['no_error'] = ($errors.Count -eq 0)
    $checks['seed_echo'] = ($seedEcho.Count -eq 1 -and $seedEcho[0] -eq $seedWant)
    $checks['shots_echo'] = ($shotsEcho.Count -eq 1 -and $shotsEcho[0] -eq $shotWant)
    $checks['trace_exists'] = (Test-Path -LiteralPath $trace)
    $checks['verify_exists'] = (Test-Path -LiteralPath "$Out/verify.json")
}

# The CSV profiler writes to the per-user Saved dir for an installed-engine -game run (run_fps.ps1).
$csvOut = $null
if ($CsvFrames -gt 0) {
    $dirs = @("$env:LOCALAPPDATA\UnrealEngine\5.8\Saved\Profiling\CSV", "$Proj\Saved\Profiling\CSV")
    $found = @($dirs | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem -Path $_ -File } |
        Where-Object { $_.Name -match '\.csv$' -and $_.CreationTimeUtc -ge $t0Utc.AddSeconds(-2) } | Sort-Object CreationTimeUtc -Descending)
    if ($found.Count -gt 0) { $csvOut = "$Out/profile.csv"; Move-Item -LiteralPath $found[0].FullName -Destination $csvOut -Force }
    $checks['csv_written'] = ($null -ne $csvOut)
}

$perfDoc = $null
if ($Perf) {
    $cpu1 = Get-CpuSeconds
    $busy = @()
    foreach ($k in $cpu1.Keys) {
        $d = $cpu1[$k] - ($(if ($cpu0.ContainsKey($k)) { $cpu0[$k] } else { 0 }))
        $pct = 100.0 * $d / [math]::Max(1.0, $elapsed)
        if ($pct -gt 5.0) { $busy += [ordered]@{ proc = $k; cpu_s = [math]::Round($d, 2); pct_core = [math]::Round($pct, 1) } }
    }
    $perfDoc = [ordered]@{ quiet = $(if ($busy.Count -eq 0) { 1 } else { 0 }); busy = $busy; elapsed_s = [math]::Round($elapsed, 1) }
    $perfDoc | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Out/perf.json" -Encoding utf8NoBOM
}

$pass = -not ($checks.Values -contains $false)
$doc = [ordered]@{
    tag = $Tag; variant = $Variant; leg = $Leg; expect_fail = $ExpectFail.IsPresent; exit = $exit; elapsed_s = [math]::Round($elapsed, 1)
    abort = $abort; first_error = $(if ($errors.Count) { $errors[0] } else { $null }); loaded = $loaded; result = $(if ($results.Count) { $results[0] } else { $null })
    seed_echo = $seedEcho; seed_want = $seedWant; shots_echo = $shotsEcho; shots_want = $shotWant; trace = $trace; csv = $csvOut
    p_commit = $pCommit; p_dirty = $pDirty; checks = $checks; pass = $pass
}
$doc | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Out/run_sim.json" -Encoding utf8NoBOM
foreach ($k in $checks.Keys) { Write-Host ("run_sim: check {0} = {1}" -f $k, $checks[$k]) }
if ($results.Count) { Write-Host "run_sim: $($results[0])" }
Write-Host ("run_sim: {0} {1} exit={2} elapsed={3:n1}s" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $Tag, $exit, $elapsed)
exit $(if ($pass) { 0 } else { 1 })
