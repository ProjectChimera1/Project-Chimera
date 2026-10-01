# Run one editor Python script through the Python commandlet (plan C 3.1 / C7: Scripts/make_ground_material.py).
#   LOCK (BG, 30-50 min possible on a cold DDC):
#     UE_LOCK_TAG=c/C7 bash D:/Projects/Chimera-Unreal/ue_lock.sh powershell -NoProfile -ExecutionPolicy Bypass -File <this> -Script <T>/Scripts/make_ground_material.py
# Launches UnrealEditor-Cmd.exe <uproject> -run=pythonscript -script="<script> <report.json>" (PythonScriptCommandlet.cpp: the -Script=
# value may be quoted and carry arguments; the commandlet returns -1 when the script raised). Windows PowerShell 5.1 compatible.
# Output: <T>/Out/<Tag>/commandlet.log (ABSLOG), stdout.txt, report.json (written by the script).
# Exit codes: 0 = the editor exited 0, the log has the script's OK sentinel (MATERIAL_OK for the material script) and no shader-compile
#   error lines; 1 = script or compile failure (the matching lines are printed); 5 = script missing or editor DLL stale; 6 = the run
#   rewrote a Config/*.ini; 124 = watchdog timeout (own process tree killed).
param(
  [Parameter(Mandatory = $true)][string]$Script,
  [string]$Tag = 'c7_material',
  [string]$Sentinel = 'MATERIAL_OK',
  [int]$TimeoutMin = 30,
  # A commandlet runs on NullDrv, where no material is compiled for rendering (the first C7 run: get_statistics returned all zeros), so
  # the material script needs the real RHI: -AllowCommandletRendering makes GMaxRHIShaderPlatform PCD3D_SM6 and compiles the shaders the
  # -game runs use. -NoRendering drops it.
  [switch]$NoRendering
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$uproject = (Join-Path $root 'ChimeraTerrain.uproject') -replace '\\', '/'
$exe = 'D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$dll = Join-Path $root 'Binaries/Win64/UnrealEditor-ChimeraTerrain.dll'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
function Say([string]$Msg) { Write-Host "run_commandlet: $Msg" }

if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) { Say "script not found: $Script"; exit 5 }
$ScriptPath = (Resolve-Path -LiteralPath $Script).Path -replace '\\', '/'
if (-not (Test-Path $exe)) { Say "editor not found: $exe"; exit 5 }
if (-not (Test-Path $dll)) { Say "STALE: $dll does not exist (build the Editor target first)"; exit 5 }
$dllTime = (Get-Item $dll).LastWriteTimeUtc
$newer = Get-ChildItem -Path (Join-Path $root 'Source') -Recurse -File | Where-Object { $_.LastWriteTimeUtc -gt $dllTime } | Select-Object -First 1
if ($newer) { Say "STALE: $($newer.FullName) is newer than UnrealEditor-ChimeraTerrain.dll (rebuild with Tools/build.ps1 -Target Editor)"; exit 5 }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'UnrealEditor*' })
if ($running.Count -gt 0) { Say ("infra: Unreal already running (pid {0}); not starting a second one" -f (($running | ForEach-Object { $_.Id }) -join ',')); exit 4 }

function Get-ConfigState {
  $state = @{}
  Get-ChildItem -Path (Join-Path $root 'Config') -Filter '*.ini' -File | ForEach-Object { $state[$_.Name] = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash }
  return $state
}
$configBefore = Get-ConfigState

$Out = (Join-Path $root "Out/$Tag") -replace '\\', '/'
if (Test-Path -LiteralPath $Out) { Get-ChildItem -LiteralPath $Out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$absLog = "$Out/commandlet.log"
$report = "$Out/report.json"
$stdoutFile = "$Out/stdout.txt"
$stderrFile = "$Out/stderr.txt"

$argString = "`"$uproject`" -run=pythonscript -script=`"$ScriptPath $report`" -unattended -nosplash -nosound -stdout -ABSLOG=`"$absLog`""
if (-not $NoRendering) { $argString += ' -AllowCommandletRendering' }
Say "RUN: UnrealEditor-Cmd.exe $argString"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exe -ArgumentList $argString -NoNewWindow -PassThru -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutMin * 60 * 1000)) {
  Say "TIMEOUT: commandlet exceeded $TimeoutMin min, killing own process tree (pid $($p.Id))"
  & taskkill /PID $p.Id /T /F | Out-Null
  exit 124
}
$p.WaitForExit()
$sw.Stop()

$text = ''
if (Test-Path $absLog) { $text = Get-Content -Raw -Path $absLog }
if (-not $text -and (Test-Path $stdoutFile)) { $text = Get-Content -Raw -Path $stdoutFile }
$lines = $text -split "`r?`n"
Say ("wall_seconds={0:N0} editor_exit={1} log={2}" -f $sw.Elapsed.TotalSeconds, $p.ExitCode, $absLog)

# The script's own result lines, then anything that looks like a shader or material compile error.
$okLine = $null
foreach ($l in $lines) {
  if ($l -match "$Sentinel |MATERIAL_FAIL|make_ground_material:") { Write-Host $l }
  if (-not $okLine -and $l -match "(^|\s)$Sentinel\s") { $okLine = $l }
}
$errPattern = 'Failed to compile Material|Shader compile error|error X\d+|\berror\b.*\.usf|LogShaderCompilers: (Error|Warning)|LogMaterial: (Error|Warning)|LogPython: Error|Python script executed with errors'
$errs = @($lines | Where-Object { $_ -match $errPattern })
foreach ($e in ($errs | Select-Object -First 40)) { Say "ERRLINE: $e" }

$configAfter = Get-ConfigState
$configChanged = @()
foreach ($k in $configAfter.Keys) { if ((-not $configBefore.ContainsKey($k)) -or ($configBefore[$k] -ne $configAfter[$k])) { $configChanged += $k } }
foreach ($k in $configBefore.Keys) { if (-not $configAfter.ContainsKey($k)) { $configChanged += $k } }
if ($configChanged.Count -gt 0) { Say ("CONFIG CHANGED: the run rewrote Config/{0}" -f ($configChanged -join ', Config/')); exit 6 }

Say ("errlines={0} sentinel={1}" -f $errs.Count, [bool]$okLine)
if ($p.ExitCode -ne 0 -or -not $okLine -or $errs.Count -gt 0) { Say 'FAIL'; exit 1 }
Say 'OK'
exit 0
