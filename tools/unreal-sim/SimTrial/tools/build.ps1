#requires -Version 7
<#
.SYNOPSIS
  Plan A A10: build ProjectChimeraEditor (Win64 Development), which carries the ChimeraSimHost module that -game runs use.
.DESCRIPTION
  A heavy job: run it under the UE lock, from Git Bash, with no editor in between the .uproject/Target.cs edits and it:
    UE_LOCK_TAG=a/A10 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -File <this> [-Log <path>]
  Refuses to start while an UnrealEditor process runs (the module DLL would be locked). Tees UBT's output to -Log and
  exits 0 only when the log has "Result: Succeeded"; also checks that the staged ChimeraSim.dll and header exist.
  Exit: 0 ok, 1 build failed, 2 precondition failed.
#>
param(
    [string]$Log = 'D:/Projects/Chimera-Unreal/TrialOut/a/a10/build.log'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$Proj = 'D:/Projects/Chimera-Unreal/ProjectChimera'
$Uproject = "$Proj/ProjectChimera.uproject"
$BuildBat = 'D:/Epic Games/UE_5.8/Engine/Build/BatchFiles/Build.bat'

$running = @(Get-Process -Name 'UnrealEditor*' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { [Console]::Error.WriteLine("UnrealEditor is running (pid $($running.Id -join ',')): close it first"); exit 2 }
foreach ($f in "$Proj/Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll", "$Proj/Source/ChimeraSimHost/Private/ThirdParty/chimera_sim.h") {
    if (-not (Test-Path -LiteralPath $f)) { [Console]::Error.WriteLine("missing $f (run NAT/publish.ps1 -StageOnly first)"); exit 2 }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log) | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "build: ProjectChimeraEditor Win64 Development -> $Log"
& cmd /c "`"$BuildBat`" ProjectChimeraEditor Win64 Development -Project=`"$Uproject`" -WaitMutex" 2>&1 | Tee-Object -FilePath $Log
$rc = $LASTEXITCODE
$ok = @(Select-String -LiteralPath $Log -SimpleMatch -Pattern 'Result: Succeeded').Count -gt 0
Write-Host ("build: exit {0} in {1:n0} s; Result: Succeeded present = {2}" -f $rc, $sw.Elapsed.TotalSeconds, $ok)
if ($rc -ne 0 -or -not $ok) { exit 1 }
$mod = "$Proj/Binaries/Win64/UnrealEditor-ChimeraSimHost.dll"
if (Test-Path -LiteralPath $mod) {
    Write-Host ("build: {0} sha256={1}" -f $mod, (Get-FileHash -Algorithm SHA256 -LiteralPath $mod).Hash.ToLowerInvariant())
}
exit 0
