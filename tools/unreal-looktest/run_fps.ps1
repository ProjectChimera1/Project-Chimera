<#
.SYNOPSIS
  One standalone -game frame-rate run of a look-test map with the CSV profiler (PLAN 10.1, 10.2).
.DESCRIPTION
  Launches UnrealEditor.exe -game on /Game/LookTest/Maps/LT_<Look>, captures 4000 frames (1500 for a
  warm-up), takes a HighResShot at capture frame 3700, samples nvidia-smi once a second, then moves the
  CSV it produced to out\csv\<tag>.csv and checks the game log and screenshot exist.
  <tag> = game_<Look>_r<Rep>  (or warm_<Look> with -Warmup; warm-up results are discarded by the caller).
  Throws, and exits non-zero, on any missing artifact. The editor must be closed first.
.EXAMPLE
  powershell -File run_fps.ps1 -Look A -Warmup
  powershell -File run_fps.ps1 -Look A -Rep 1
#>
param(
    [Parameter(Mandatory)][ValidateSet('A', 'A_noLumen', 'B')][string]$Look,
    [int]$Rep = 1,
    [switch]$Warmup
)
$ErrorActionPreference = 'Stop'

$UE   = 'D:\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe'
$Proj = 'D:\Projects\Chimera-Unreal\ProjectChimera'
$LT   = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'   # forward slashes: these go into UE command lines
$LTw  = $LT.Replace('/', '\')
# An installed-engine -game run writes to the per-user Saved dir, not the project's (seen live 2026-10-01:
# "Writing CSV to file : C:/Users/<u>/AppData/Local/UnrealEngine/5.8/Saved/Profiling/CSV/Profile(...).csv").
$CsvDirs = @("$env:LOCALAPPDATA\UnrealEngine\5.8\Saved\Profiling\CSV", "$Proj\Saved\Profiling\CSV")
$CsvDir = $CsvDirs -join ' or '

function Find-NewCsv {
    # Newest .csv / .csv.gz in $Dir created at or after $Since (UTC), allowing 2 s of clock slack.
    param([string]$Dir, [datetime]$Since)
    if (-not (Test-Path $Dir)) { return @() }
    Get-ChildItem -Path $Dir -File |
        Where-Object { $_.Name -match '\.csv(\.gz)?$' -and $_.CreationTimeUtc -ge $Since.AddSeconds(-2) } |
        Sort-Object CreationTimeUtc -Descending
}

function Wait-FileReadable {
    # The profiler finalises the file as the process exits; wait until it can be opened exclusively.
    param([string]$Path, [int]$Seconds = 30)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try { $s = [System.IO.File]::Open($Path, 'Open', 'Read', 'None'); $s.Close(); return }
        catch { if ((Get-Date) -gt $deadline) { throw "CSV $Path is still locked after $Seconds s" }; Start-Sleep -Milliseconds 500 }
    }
}

$tag = if ($Warmup) { "warm_$Look" } else { "game_$($Look)_r$Rep" }
$map = "/Game/LookTest/Maps/LT_$Look"
$umap = "$Proj\Content\LookTest\Maps\LT_$Look.umap"

# --- preconditions: fail before launching anything
if (-not (Test-Path $UE)) { throw "UnrealEditor.exe not found: $UE" }
if (-not (Test-Path $umap)) { throw "map not found: $umap (run lt_build for this look first)" }
$running = Get-Process -Name 'UnrealEditor*' -ErrorAction SilentlyContinue
if ($running) { throw "UnrealEditor is running (pid $($running.Id -join ',')); close it first: python ue_job.py lt_admin quit" }
foreach ($d in "$LTw\out\csv", "$LTw\logs") { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$logPath = "$LTw\logs\$tag.log"; $smiPath = "$LTw\logs\$tag.smi.csv"
$csvOut  = "$LTw\out\csv\$tag.csv"; $pngPath = "$LTw\out\$tag.png"
foreach ($f in $logPath, $smiPath, $csvOut, $pngPath) { if (Test-Path $f) { Remove-Item $f -Force } }  # stale files must never pass as this run's

# --- command line (PLAN 10.1). Elements that contain spaces carry their own quotes.
$frames = if ($Warmup) { 1500 } else { 4000 }
$a = @(
    "`"$Proj\ProjectChimera.uproject`"", $map,
    '-game', '-fullscreen', '-ResX=1920', '-ResY=1080', '-novsync', '-nosound', '-unattended', '-nosplash',
    "-ABSLOG=$LT/logs/$tag.log", '-LogCmds="LogViewport Verbose"',
    '-ExecCmds="t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100,r.HighResScreenshotDelay 64"',
    "-csvCaptureFrames=$frames", '-csvCompression=0', '-ExitAfterCsvProfiling'
)
if (-not $Warmup) { $a += "-csvExecCmds=`"3700:HighResShot 1920x1080 filename=$LT/out/$tag.png`"" }

Write-Host "run_fps: $tag  map=$map  frames=$frames  warmup=$($Warmup.IsPresent)"
$parsec = Get-Process -Name parsecd -ErrorAction SilentlyContinue
Write-Host "run_fps: parsecd running = $([bool]$parsec)"

$smi = $null
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    $smi = Start-Process nvidia-smi -ArgumentList '--query-gpu=timestamp,utilization.gpu,memory.used,clocks.gr,temperature.gpu,power.draw', '--format=csv', '-lms', '1000' `
        -RedirectStandardOutput $smiPath -PassThru -NoNewWindow
} else { Write-Warning 'nvidia-smi not on PATH: no GPU sample for this run' }

$t0 = (Get-Date).ToUniversalTime()
$p = $null
try {
    $p = Start-Process $UE -ArgumentList $a -PassThru
    $null = $p.Handle   # caches the handle so ExitCode is not $null after WaitForExit
    if (-not $p.WaitForExit(900000)) {
        Stop-Process -Id $p.Id -Force   # our own child, hung past 15 min
        throw "game run exceeded 15 min and was stopped"
    }
} finally {
    if ($smi -and -not $smi.HasExited) { Stop-Process -Id $smi.Id -Force }
}
Write-Host "run_fps: game exited with code $($p.ExitCode)"
# -ExitAfterCsvProfiling ends the run with a normal exit; anything else is a crash and the CSV is truncated.
if ($p.ExitCode -ne 0) { throw "game exited with code $($p.ExitCode) (crash?); capture is not valid. Check $logPath and Saved\Logs" }

# --- collect artifacts
$found = @($CsvDirs | ForEach-Object { Find-NewCsv -Dir $_ -Since $t0 } | Sort-Object CreationTimeUtc -Descending)
if ($found.Count -eq 0) {
    $have = ($CsvDirs | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -File | Select-Object -Last 5 | ForEach-Object Name }) -join ', '
    throw "no CSV created under $CsvDir since $($t0.ToString('o')); newest existing: $have. Check $logPath"
}
if ($found.Count -gt 1) { Write-Warning "$($found.Count) CSVs created during this run; taking the newest ($($found[0].Name))" }
$csv = $found[0]
Wait-FileReadable -Path $csv.FullName
if ($csv.Name -match '\.gz$') {
    # -csvCompression=0 should prevent this, but a .gz is still handled so a run is never lost
    $in = [System.IO.File]::OpenRead($csv.FullName); $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
    $out = [System.IO.File]::Create($csvOut); $gz.CopyTo($out); $out.Close(); $gz.Close(); $in.Close(); Remove-Item $csv.FullName
} else {
    Move-Item -LiteralPath $csv.FullName -Destination $csvOut -Force
}
if (-not (Test-Path $csvOut) -or (Get-Item $csvOut).Length -lt 1024) { throw "CSV missing or tiny after move: $csvOut" }
Write-Host "run_fps: csv  -> $csvOut ($([int]((Get-Item $csvOut).Length / 1024)) KB)"

if (-not (Test-Path $logPath)) { throw "game log missing: $logPath (-ABSLOG ignored? it must end in .log)" }
Write-Host "run_fps: log  -> $logPath"
# Every resize is logged; the LAST one is the size the capture ran at.
$resizes = @(Select-String -Path $logPath -Pattern 'Scene viewport resized to (\d+)x(\d+), mode (\w+)')
if ($resizes.Count -eq 0) {
    Write-Warning 'log has no "Scene viewport resized to" line: resolution not proven'
} else {
    $m = $resizes[-1].Matches[0]
    $last = "$($m.Groups[1].Value)x$($m.Groups[2].Value) $($m.Groups[3].Value)"
    Write-Host "run_fps: last viewport resize = $last"
    if ($m.Groups[1].Value -ne '1920' -or $m.Groups[2].Value -ne '1080') { throw "last viewport resize was $last, not 1920x1080: fps not measured at the target resolution" }
}
if ($smi) { Write-Host "run_fps: smi  -> $smiPath" }

if (-not $Warmup) {
    if (-not (Test-Path $pngPath)) { throw "screenshot missing: $pngPath (HighResShot at capture frame 3700 did not fire; see $logPath)" }
    Write-Host "run_fps: png  -> $pngPath"
}
Write-Host "run_fps: OK $tag"
