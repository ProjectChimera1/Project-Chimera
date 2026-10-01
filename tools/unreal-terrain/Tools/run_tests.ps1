# Run ChimeraTerrain automation tests under -nullrhi and count the results.
#   LOCK:  UE_LOCK_TAG=c/C2 bash D:/Projects/Chimera-Unreal/ue_lock.sh powershell -NoProfile -ExecutionPolicy Bypass -File <this> -Filter Chimera.Terrain.Data
# Windows PowerShell 5.1 compatible. Exit codes: 0 all passed, 1 a test failed, none ran, fewer finished than the engine found
# (INCOMPLETE) or the editor exited non-zero (CRASH), 5 STALE (editor DLL older than a Source file), 6 CONFIG CHANGED (the run
# rewrote a Config/*.ini, e.g. the engine's AndroidFileServer SecurityToken; EXECUTION S3/C13), 124 timeout (own process tree killed),
# 3 launch problem.
# Prints "TESTS pass=<n> fail=<m>" from the engine's "Test Completed. Result={Success|Fail}" lines (AutomationControllerManager.cpp:43).
param(
  [string]$Filter = 'Chimera.Terrain',
  [int]$TimeoutMin = 20,
  [string]$Tag = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$uproject = (Join-Path $root 'ChimeraTerrain.uproject') -replace '\\', '/'
$dll = Join-Path $root 'Binaries/Win64/UnrealEditor-ChimeraTerrain.dll'
$exe = 'D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
if (-not (Test-Path $exe)) { Write-Host "ERROR: $exe not found"; exit 3 }

# STALE: the editor module must be newer than every Source file, otherwise the tests would run old code.
if (-not (Test-Path $dll)) { Write-Host "STALE: $dll does not exist (build the Editor target first)"; exit 5 }
$dllTime = (Get-Item $dll).LastWriteTimeUtc
$newer = Get-ChildItem -Path (Join-Path $root 'Source') -Recurse -File | Where-Object { $_.LastWriteTimeUtc -gt $dllTime } | Select-Object -First 1
if ($newer) { Write-Host "STALE: $($newer.FullName) is newer than UnrealEditor-ChimeraTerrain.dll (rebuild with Tools/build.ps1 -Target Editor)"; exit 5 }

# Snapshot Config/*.ini so an engine plugin writing into the project config (secrets included) fails the run instead of slipping through.
function Get-ConfigState {
  $state = @{}
  Get-ChildItem -Path (Join-Path $root 'Config') -Filter '*.ini' -File | ForEach-Object { $state[$_.Name] = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash }
  return $state
}
$configBefore = Get-ConfigState

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
if ($Tag -eq '') { $Tag = ($Filter -replace '[^A-Za-z0-9]+', '_') }
$logDir = Join-Path $root 'Saved/TestRuns'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stdoutFile = Join-Path $logDir "${Tag}_${stamp}.stdout.txt"
$stderrFile = Join-Path $logDir "${Tag}_${stamp}.stderr.txt"
$absLog = (Join-Path $logDir "${Tag}_${stamp}.log") -replace '\\', '/'

$argString = "`"$uproject`" /Engine/Maps/Entry -ExecCmds=`"Automation RunTests $Filter;Quit`" -nullrhi -unattended -nosplash -nosound -stdout -ABSLOG=`"$absLog`""
Write-Host "RUN: UnrealEditor-Cmd.exe $argString"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $exe -ArgumentList $argString -NoNewWindow -PassThru -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutMin * 60 * 1000)) {
  Write-Host "TIMEOUT: tests exceeded $TimeoutMin min, killing own process tree (pid $($p.Id))"
  & taskkill /PID $p.Id /T /F | Out-Null
  exit 124
}
$p.WaitForExit()
$sw.Stop()

$text = ''
if (Test-Path $stdoutFile) { $text = Get-Content -Raw -Path $stdoutFile }
if ((-not $text) -or ($text -notmatch 'Test Completed')) {
  # Fall back to the engine's own log file.
  if (Test-Path $absLog) { $text = (Get-Content -Raw -Path $absLog) }
}
$pass = 0
$fail = 0
$failLines = @()
foreach ($m in [regex]::Matches($text, 'Test Completed\. Result=\{([^}]*)\} Name=\{([^}]*)\} Path=\{([^}]*)\}')) {
  $res = $m.Groups[1].Value
  if ($res -eq 'Success') { $pass++ }
  elseif ($res -eq 'Fail') { $fail++; $failLines += $m.Groups[3].Value }
}
Write-Host ("wall_seconds={0:N0} editor_exit={1} log={2}" -f $sw.Elapsed.TotalSeconds, $p.ExitCode, $absLog)
foreach ($f in $failLines) { Write-Host "FAILED: $f" }
if ($fail -gt 0) {
  # Show the error lines the engine printed for the failing tests.
  foreach ($line in ($text -split "`r?`n")) { if ($line -match 'Error:|LogAutomationController: Error') { Write-Host $line } }
}
Write-Host "TESTS pass=$pass fail=$fail"

$configAfter = Get-ConfigState
$configChanged = @()
foreach ($k in $configAfter.Keys) { if ((-not $configBefore.ContainsKey($k)) -or ($configBefore[$k] -ne $configAfter[$k])) { $configChanged += $k } }
foreach ($k in $configBefore.Keys) { if (-not $configAfter.ContainsKey($k)) { $configChanged += $k } }
if ($configChanged.Count -gt 0) {
  Write-Host ("CONFIG CHANGED: the run rewrote Config/{0}; restore it and disable the plugin that wrote it" -f ($configChanged -join ', Config/'))
  exit 6
}

# The engine prints "Found N automation tests based on '<filter>'" (AutomationCommandline.cpp) before it starts; every one must report.
$found = -1
$fm = [regex]::Match($text, 'Found (\d+) automation tests based on')
if ($fm.Success) { $found = [int]$fm.Groups[1].Value }
Write-Host "found=$found"
if ($fail -gt 0 -or $pass -eq 0) { exit 1 }
if ($found -lt 0) { Write-Host "INCOMPLETE: no 'Found N automation tests' line in the log"; exit 1 }
if (($pass + $fail) -ne $found) { Write-Host "INCOMPLETE: engine found $found tests but only $($pass + $fail) reported a result (crash mid-run?)"; exit 1 }
if ($p.ExitCode -ne 0) { Write-Host "CRASH: editor exited $($p.ExitCode) although every test passed"; exit 1 }
exit 0
