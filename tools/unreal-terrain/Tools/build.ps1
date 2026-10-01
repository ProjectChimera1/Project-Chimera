# Build ChimeraTerrain (Editor or Game, Win64 Development). Prints wall seconds; exits with UBT's code.
# Heavy: run under the lock (UE_LOCK_TAG=c/C1 bash D:/Projects/Chimera-Unreal/ue_lock.sh powershell -File ...).
param([ValidateSet('Editor','Game')][string]$Target = 'Editor', [int]$WatchdogMin = 30)
$ErrorActionPreference = 'Stop'
$proj = Join-Path (Split-Path $PSScriptRoot -Parent) 'ChimeraTerrain.uproject'
$name = if ($Target -eq 'Editor') { 'ChimeraTerrainEditor' } else { 'ChimeraTerrain' }
$bat  = 'D:/Epic Games/UE_5.8/Engine/Build/BatchFiles/Build.bat'
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $bat -ArgumentList @($name, 'Win64', 'Development', "-Project=`"$proj`"", '-WaitMutex') -NoNewWindow -PassThru
$null = $p.Handle  # cache the handle so ExitCode is readable
if (-not $p.WaitForExit($WatchdogMin * 60 * 1000)) {
  Write-Host "WATCHDOG: build exceeded $WatchdogMin min, killing build process tree"
  & taskkill /PID $p.Id /T /F | Out-Null
  exit 124
}
$p.WaitForExit()
$sw.Stop()
Write-Host ("BUILD {0} wall_seconds={1:N0} exit={2}" -f $name, $sw.Elapsed.TotalSeconds, $p.ExitCode)
exit $p.ExitCode
