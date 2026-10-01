# One unattended ChimeraTerrain run (plan C 3.8): launches UnrealEditor.exe <uproject> -game (or the packaged exe) with a director script,
# waits with a watchdog, and checks the EXECUTION 2.2 contract (exit code + results.json completed=true + ops_done == ops_total).
#   LOCK: UE_LOCK_TAG=c/C3 bash D:/Projects/Chimera-Unreal/ue_lock.sh powershell -NoProfile -ExecutionPolicy Bypass -File <this> -Script G1 -Tag g1_dyn
# Windows PowerShell 5.1 compatible; text and JSON are written as UTF-8 without BOM.
# Exit codes: the game's own code when non-zero (2 op failed, 3 op timeout, other = crash); 0 = success with the sentinel;
#   4 infra (desktop wrong, Unreal already running, -Measure machine busy); 5 script or exe missing; 7 game exited 0 without the success
#   sentinel; 8 viewport size wrong; 124 watchdog timeout (own process tree killed; "infra: retry" when the log shows shader compilation).
param(
  [Parameter(Mandatory = $true)][string]$Script,
  [Parameter(Mandatory = $true)][string]$Tag,
  [int]$Chunk = 64,
  [int]$Half = 160,
  [switch]$Packaged,
  [switch]$Shipping,
  [int]$FixedFps = 0,
  [int]$MaxFps = 0,
  [switch]$Windowed,
  [int]$ResX = 1920,
  [int]$ResY = 1080,
  [switch]$Inject,
  [switch]$Measure,
  [int]$TimeoutMin = 15,
  [string]$Extra = '',
  [string]$ExecExtra = ''   # more console commands appended to -ExecCmds (comma-separated; a non-standard cvar is its own configuration)
)
$ErrorActionPreference = 'Stop'
$T = (Split-Path $PSScriptRoot -Parent) -replace '\\', '/'
$UE = 'D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe'
$Uproject = "$T/ChimeraTerrain.uproject"
$Kit = 'D:/Projects/Project_Chimera/tools/unreal-trial'
$Utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Utf8([string]$Path, [string]$Text) { [System.IO.File]::WriteAllText($Path, $Text, $Utf8) }
function Say([string]$Msg) { Write-Host "run_terrain: $Msg" }

# ---- resolve the script, the exe and the output folder
if (Test-Path -LiteralPath $Script -PathType Leaf) { $ScriptPath = (Resolve-Path -LiteralPath $Script).Path -replace '\\', '/' }
else { $ScriptPath = "$T/Scripts/$Script.json" }
if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { Say "script not found: $ScriptPath"; exit 5 }

if ($Packaged) {
  $cfg = 'Development'; $bin = 'ChimeraTerrain.exe'
  if ($Shipping) { $cfg = 'Shipping'; $bin = 'ChimeraTerrain-Win64-Shipping.exe' }
  $Exe = "$T/Packaged/$cfg/Windows/ChimeraTerrain/Binaries/Win64/$bin"
} else {
  $Exe = $UE
}
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { Say "exe not found: $Exe"; exit 5 }

$Out = "$T/Out/$Tag"
if (Test-Path -LiteralPath $Out) { Get-ChildItem -LiteralPath $Out -Force | Remove-Item -Recurse -Force }  # stale outputs must never pass as this run's
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Log = "$Out/game.log"

# ---- guards (copied from tools/unreal-looktest/run_fps.ps1:56-61, 75)
$pf = & powershell -NoProfile -ExecutionPolicy Bypass -File "$Kit/preflight.ps1"
$pfRc = $LASTEXITCODE
$pf | ForEach-Object { Say "preflight: $_" }
if ($pfRc -ne 0 -and -not $Windowed) { Say 'infra: desktop is not 1920x1080 @96 or the screen is locked'; exit 4 }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'UnrealEditor*' -or $_.ProcessName -like 'ChimeraTerrain*' })
if ($running.Count -gt 0) { Say ("infra: Unreal already running (pid {0}); not starting a second one" -f (($running | ForEach-Object { $_.Id }) -join ',')); exit 4 }
$parsec = @(Get-Process -Name parsecd -ErrorAction SilentlyContinue).Count -gt 0
Say "parsecd running = $parsec"

if ($Measure) {
  # Idle precheck (plan C 3.8): never kills anything; a busy machine aborts the run with exit 4.
  $q = & powershell -NoProfile -ExecutionPolicy Bypass -File "$Kit/preflight.ps1" -Quiet
  $qRc = $LASTEXITCODE
  $comfy = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine -match 'ComfyUI' })
  $snap = New-Object System.Text.StringBuilder
  [void]$snap.AppendLine("time=$((Get-Date).ToString('o')) parsecd=$parsec preflight_rc=$qRc comfyui=$($comfy.Count)")
  $q | ForEach-Object { [void]$snap.AppendLine("preflight: $_") }
  Get-Process | Sort-Object CPU -Descending | Select-Object -First 25 | ForEach-Object { [void]$snap.AppendLine(("{0,-28} {1,8} cpu_s={2:N1} ws_mb={3:N0}" -f $_.ProcessName, $_.Id, $_.CPU, ($_.WorkingSet64 / 1MB))) }
  if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    $smi0 = & nvidia-smi --query-gpu=utilization.gpu,memory.used,clocks.gr,temperature.gpu,power.draw --format=csv
    $smi0 | ForEach-Object { [void]$snap.AppendLine("nvidia-smi: $_") }
  }
  Write-Utf8 "$Out/measure.txt" $snap.ToString()
  if ($qRc -ne 0 -or $comfy.Count -gt 0) { Say 'infra: busy (machine not quiet; see measure.txt)'; exit 4 }
}

# ---- command line (no leading-slash arguments, D7; -ABSLOG ends in .log, EXECUTION 2.2)
$a = New-Object System.Collections.Generic.List[string]
if (-not $Packaged) { $a.Add("`"$Uproject`""); $a.Add('-game') }
if ($Windowed) { $a.Add('-windowed') } else { $a.Add('-fullscreen') }
$a.Add("-ResX=$ResX"); $a.Add("-ResY=$ResY")
foreach ($x in @('-novsync', '-nosound', '-unattended', '-nosplash')) { $a.Add($x) }
$a.Add("-ABSLOG=`"$Log`"")
$a.Add('-LogCmds="LogViewport Verbose"')
$ExecCmds = "t.MaxFPS $MaxFps,r.VSync 0,r.ScreenPercentage 100,r.HighResScreenshotDelay 64"
if ($ExecExtra -ne '') { $ExecCmds = "$ExecCmds,$ExecExtra" }
$a.Add("-ExecCmds=`"$ExecCmds`"")
if ($FixedFps -gt 0) { $a.Add('-UseFixedTimeStep'); $a.Add("-FPS=$FixedFps") }
$a.Add("-ChimeraTerrainScript=`"$ScriptPath`"")
$a.Add("-ChimeraTerrainOut=`"$Out`"")
$a.Add("-ChimeraTerrainChunk=$Chunk")
$a.Add("-ChimeraTerrainHalf=$Half")
if ($TimeoutMin -gt 15) { $a.Add(("-ChimeraTerrainSettleTimeoutS={0}" -f [Math]::Max(600, $TimeoutMin * 60 - 180))) }
if ($Extra -ne '') { $a.Add($Extra) }
$argString = $a -join ' '
Write-Utf8 "$Out/cmdline.txt" ("`"$Exe`" $argString")
Say "tag=$Tag script=$ScriptPath timeout=${TimeoutMin}min"
Say "cmd: $Exe $argString"

$smi = $null
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
  $smi = Start-Process nvidia-smi -ArgumentList '--query-gpu=timestamp,utilization.gpu,memory.used,clocks.gr,temperature.gpu,power.draw', '--format=csv', '-lms', '1000' `
    -RedirectStandardOutput "$Out/smi.csv" -PassThru -NoNewWindow
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -ArgumentList $argString -PassThru
$null = $p.Handle   # cache the handle so ExitCode is readable after WaitForExit (run_fps.ps1:88)
$inj = $null
if ($Inject) {
  $injScript = "$T/Tools/inject_mouse.py"
  if (-not (Test-Path -LiteralPath $injScript)) { Say "inject_mouse.py missing (C8)"; & taskkill /PID $p.Id /T /F | Out-Null; exit 5 }
  $inj = Start-Process python -ArgumentList "`"$injScript`" --out `"$Out`" --pid $($p.Id)" -PassThru -NoNewWindow -RedirectStandardOutput "$Out/inject.log" -RedirectStandardError "$Out/inject.err"
}
$timedOut = $false
try {
  if (-not $p.WaitForExit($TimeoutMin * 60 * 1000)) {
    $timedOut = $true
    Say "WATCHDOG: run exceeded $TimeoutMin min; killing own process tree (pid $($p.Id))"
    & taskkill /PID $p.Id /T /F | Out-Null
    $p.WaitForExit(30000) | Out-Null
  } else {
    $p.WaitForExit()
  }
} finally {
  if ($smi -and -not $smi.HasExited) { Stop-Process -Id $smi.Id -Force }
  if ($inj -and -not $inj.HasExited) { Stop-Process -Id $inj.Id -Force }
}
$sw.Stop()
$code = $p.ExitCode
Say ("game exited code={0} wall_s={1:N0}" -f $code, $sw.Elapsed.TotalSeconds)

if ($timedOut) {
  $shader = $false
  if (Test-Path -LiteralPath $Log) {
    $tail = Get-Content -LiteralPath $Log -Tail 400
    $shader = @($tail | Where-Object { $_ -match 'ShaderCompil|Compiling shader|shaders left to compile|Shader compil' }).Count -gt 0
  }
  if ($shader) { Say 'TIMEOUT while the log shows shader compilation: infra: retry' } else { Say 'TIMEOUT' }
  exit 124
}

# ---- artefacts and the success sentinel
if (-not (Test-Path -LiteralPath $Log)) { Say "game log missing: $Log"; if ($code -ne 0) { exit $code } ; exit 7 }
$resizes = @(Select-String -LiteralPath $Log -Pattern 'Scene viewport resized to (\d+)x(\d+), mode (\w+)')
$viewOk = $true
if ($resizes.Count -eq 0) { Say 'log has no "Scene viewport resized to" line: resolution not proven'; $viewOk = $false }
else {
  $m = $resizes[-1].Matches[0]
  Say ("last viewport resize = {0}x{1} {2}" -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value)
  if ([int]$m.Groups[1].Value -ne $ResX -or [int]$m.Groups[2].Value -ne $ResY) { $viewOk = $false }
}

$res = $null
$resPath = "$Out/results.json"
if (Test-Path -LiteralPath $resPath) {
  try { $res = [System.IO.File]::ReadAllText($resPath, $Utf8) | ConvertFrom-Json } catch { Say "results.json unreadable: $_" }
}
if ($res) {
  Say ("results: completed={0} ops={1}/{2} exit_code={3} fail_reason='{4}'" -f $res.completed, $res.ops_done, $res.ops_total, $res.exit_code, $res.fail_reason)
} else {
  Say 'results.json missing'
}
if ($code -ne 0) { Say "FAIL exit=$code"; exit $code }
if (-not $res -or -not $res.completed -or ($res.ops_done -ne $res.ops_total)) { Say 'FAIL: game exited 0 without the success sentinel (results.json completed=true, ops_done == ops_total)'; exit 7 }
if (-not $viewOk) { Say "FAIL: viewport is not ${ResX}x${ResY}"; exit 8 }
Say "OK $Tag"
exit 0
