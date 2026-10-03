<#
.SYNOPSIS
  HUD frame cost (plan B T8): ChimeraHud on the Entry map, HUD off (-HudOff) against HUD on, with the CSV profiler.
.DESCRIPTION
  Follows tools/unreal-looktest/run_fps.ps1: UnrealEditor.exe -game -fullscreen 1920x1080, no vsync, t.MaxFPS 0,
  -csvCaptureFrames=<Frames> -ExitAfterCsvProfiling, the CSV moved from the per-user Saved dir to H/HudRef/cost/<tag>.csv.
  The HUD runs live (no -HudFreezeTime: the laser heads and toast animate as in play) over -HudBackdrop=none, so the
  "on" runs pay for the widget tree only, not a full-screen backdrop. Order: one warm-up (HUD on, discarded), then
  <Reps> off runs and <Reps> on runs (EXECUTION 1.2 Phase 4 row: warm-up, 3 off, 3 on), or alternating with -Interleave.
  Then hud_cost_parse.py prints the HUD-cost line and writes the JSON.

  EXECUTION 1.1: the measured runs are a Measure-class job: Phase 4 only, run as
      bash D:/Projects/Chimera-Unreal/ue_lock.sh --measure powershell -NoProfile -ExecutionPolicy Bypass -File <this> [-Interleave]
  The JSON records the lock kind read from the lock's owner file; "measured" is true only for a full run under kind=measure.
  -Smoke (1 off + 1 on, 600 frames, no warm-up) is a plumbing check under the normal lock; its numbers are unmeasured.
  -DryRun prints every command line and checks paths without launching anything (no lock needed).
.EXAMPLE
  powershell -File hud_cost.ps1 -DryRun
  bash ue_lock.sh powershell -File hud_cost.ps1 -Smoke
  bash ue_lock.sh --measure powershell -File hud_cost.ps1
#>
param(
    [int]$Reps = 3,
    [int]$Frames = 1500,
    [int]$WarmFrames = 1500,
    [switch]$NoWarmup,
    [switch]$Interleave,
    [switch]$Smoke,
    [switch]$DryRun,
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'

$UE    = 'D:\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe'
$Hw    = 'D:\Projects\Chimera-Unreal\ChimeraHud'
$Hf    = 'D:/Projects/Chimera-Unreal/ChimeraHud'          # forward slashes: these go into UE command lines
$Here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Kit   = Join-Path $Here '..\unreal-trial'
$LockOwner = 'D:\Projects\Chimera-Unreal\.ue-heavy.lock\owner'
# An installed-engine -game run writes the CSV to the per-user Saved dir (run_fps.ps1, seen live 2026-10-01).
$CsvDirs = @("$env:LOCALAPPDATA\UnrealEngine\5.8\Saved\Profiling\CSV", "$Hw\Saved\Profiling\CSV")
$CostDir = "$Hw\HudRef\cost"

if ($Smoke) { $Reps = 1; $Frames = 600; $NoWarmup = $true; if (-not $Out) { $Out = Join-Path $Here 'results\hud_cost_smoke.json' } }
if (-not $Out) { $Out = Join-Path $Here 'results\hud_cost.json' }
$prefix = if ($Smoke) { 'smoke' } else { 'cost' }

function Find-NewCsv {
    param([string]$Dir, [datetime]$Since)
    if (-not (Test-Path $Dir)) { return @() }
    Get-ChildItem -Path $Dir -File |
        Where-Object { $_.Name -match '\.csv(\.gz)?$' -and $_.CreationTimeUtc -ge $Since.AddSeconds(-2) } |
        Sort-Object CreationTimeUtc -Descending
}

function Wait-FileReadable {
    param([string]$Path, [int]$Seconds = 30)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try { $s = [System.IO.File]::Open($Path, 'Open', 'Read', 'None'); $s.Close(); return }
        catch { if ((Get-Date) -gt $deadline) { throw "CSV $Path is still locked after $Seconds s" }; Start-Sleep -Milliseconds 500 }
    }
}

function Get-Args([string]$Tag, [int]$N, [bool]$Off) {
    $a = @(
        "`"$Hw\ChimeraHud.uproject`"", '/Engine/Maps/Entry?game=/Script/ChimeraHud.ChimeraHudGameMode',
        '-game', '-fullscreen', '-ResX=1920', '-ResY=1080', '-novsync', '-nosound', '-unattended', '-nosplash',
        "-ABSLOG=$Hf/HudRef/cost/$Tag.log", '-LogCmds="LogViewport Verbose"',
        '-ExecCmds="DisableAllScreenMessages,t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100"',
        "-csvCaptureFrames=$N", '-csvCompression=0', '-ExitAfterCsvProfiling',
        '-HudBackdrop=none'
    )
    if ($Off) { $a += '-HudOff' }
    return $a
}

# run list
$runs = @()
if (-not $NoWarmup) { $runs += [pscustomobject]@{ Tag = "${prefix}_warm"; Off = $false; N = $WarmFrames; Kind = 'warmup' } }
if ($Interleave) {
    for ($r = 1; $r -le $Reps; $r++) {
        $runs += [pscustomobject]@{ Tag = "${prefix}_off_r$r"; Off = $true;  N = $Frames; Kind = 'off' }
        $runs += [pscustomobject]@{ Tag = "${prefix}_on_r$r";  Off = $false; N = $Frames; Kind = 'on' }
    }
} else {
    for ($r = 1; $r -le $Reps; $r++) { $runs += [pscustomobject]@{ Tag = "${prefix}_off_r$r"; Off = $true;  N = $Frames; Kind = 'off' } }
    for ($r = 1; $r -le $Reps; $r++) { $runs += [pscustomobject]@{ Tag = "${prefix}_on_r$r";  Off = $false; N = $Frames; Kind = 'on' } }
}

# preconditions: fail before launching anything
foreach ($p in $UE, "$Hw\ChimeraHud.uproject", "$Hw\Binaries\Win64\UnrealEditor-ChimeraHud.dll", (Join-Path $Here 'hud_cost_parse.py'),
              (Join-Path $Here '..\unreal-looktest\parse_csv.py'), (Join-Path $Kit 'preflight.ps1')) {
    if (-not (Test-Path $p)) { throw "missing: $p" }
}
$lockKind = 'none'
if (Test-Path $LockOwner) {
    $k = Select-String -Path $LockOwner -Pattern '^kind=(\w+)' | Select-Object -First 1
    if ($k) { $lockKind = $k.Matches[0].Groups[1].Value }
}
Write-Host "hud_cost: runs=$($runs.Count) reps=$Reps frames=$Frames warmup=$(-not $NoWarmup) interleave=$($Interleave.IsPresent) smoke=$($Smoke.IsPresent) lock_kind=$lockKind out=$Out"

if ($DryRun) {
    foreach ($run in $runs) { Write-Host ("hud_cost: DRY {0,-14} `"{1}`" {2}" -f $run.Tag, $UE, ((Get-Args $run.Tag $run.N $run.Off) -join ' ')) }
    $pf = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Kit 'preflight.ps1') 2>&1
    Write-Host "hud_cost: preflight: $(($pf | Select-Object -First 1))"
    Write-Host "hud_cost: DRY RUN OK ($($runs.Count) runs, nothing launched)"
    exit 0
}

$running = Get-Process -Name 'UnrealEditor*' -ErrorAction SilentlyContinue
if ($running) { throw "UnrealEditor is running (pid $($running.Id -join ',')); a cost run needs it closed" }
$pf = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Kit 'preflight.ps1') 2>&1
if (-not ("$pf" -match 'DESKTOP OK 1920x1080 @96')) { throw "preflight: $pf" }
New-Item -ItemType Directory -Force -Path $CostDir | Out-Null

$csvs = @()
foreach ($run in $runs) {
    $log = "$CostDir\$($run.Tag).log"; $csvOut = "$CostDir\$($run.Tag).csv"
    foreach ($f in $log, $csvOut) { if (Test-Path $f) { Remove-Item $f -Force } }   # stale files never pass as this run's
    $t0 = (Get-Date).ToUniversalTime()
    Write-Host "hud_cost: $($run.Tag) off=$($run.Off) frames=$($run.N)"
    $p = Start-Process $UE -ArgumentList (Get-Args $run.Tag $run.N $run.Off) -PassThru
    $null = $p.Handle
    if (-not $p.WaitForExit(900000)) { Stop-Process -Id $p.Id -Force; throw "$($run.Tag): exceeded 15 min and was stopped" }
    if ($p.ExitCode -ne 0) { throw "$($run.Tag): game exited with code $($p.ExitCode); capture not valid (see $log)" }
    $found = @($CsvDirs | ForEach-Object { Find-NewCsv -Dir $_ -Since $t0 } | Sort-Object CreationTimeUtc -Descending)
    if ($found.Count -eq 0) { throw "$($run.Tag): no CSV created since $($t0.ToString('o')) (see $log)" }
    $csv = $found[0]
    Wait-FileReadable -Path $csv.FullName
    Move-Item -LiteralPath $csv.FullName -Destination $csvOut -Force
    if (-not (Test-Path $log)) { throw "$($run.Tag): game log missing: $log" }
    $hudLine = Select-String -Path $log -Pattern 'HUD off: root widget not added|root widget added' | Select-Object -First 1
    if ($run.Off -and -not ($hudLine -and $hudLine.Line -match 'HUD off')) { throw "$($run.Tag): -HudOff not honoured (no 'HUD off' line)" }
    if (-not $run.Off -and -not ($hudLine -and $hudLine.Line -match 'root widget added')) { throw "$($run.Tag): HUD not added (no 'root widget added' line)" }
    Write-Host "hud_cost: $($run.Tag) csv -> $csvOut ($([int]((Get-Item $csvOut).Length / 1024)) KB)"
    if ($run.Kind -ne 'warmup') { $csvs += "$($run.Kind)=$csvOut" }
}

$measuredFlag = if (-not $Smoke -and $lockKind -eq 'measure') { '--measured' } else { '--unmeasured' }
& python (Join-Path $Here 'hud_cost_parse.py') --out $Out --lock-kind $lockKind $measuredFlag @csvs
if ($LASTEXITCODE -ne 0) { throw "hud_cost_parse.py failed ($LASTEXITCODE)" }
