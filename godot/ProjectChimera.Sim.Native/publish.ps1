#requires -Version 7
<#
.SYNOPSIS
  Unreal trial A5 (plan A section 3.4): publish ChimeraSim.dll (NativeAOT, C ABI) or stage it into the Unreal project.

.DESCRIPTION
  Default: ILC publish of ProjectChimera.Sim.Native.csproj to NAT/bin/publish (win-x64, Release), stamped with the git
  HEAD (SourceRevisionId) and a dirty flag (1 when `git status --porcelain -- godot tools/sim-trial` is non-empty),
  the whole output teed to NAT/publish.log. Every run is a REAL native compile: the ILC step is incremental, so the
  script deletes its native intermediates first and fails (exit 4) unless the log shows "Generating native code"; the
  IL-warning count therefore always describes an ILC run that happened. The publish goes to bin/publish.new and is
  swapped into bin/publish only when it passed, together with a copy of include/chimera_sim.h (the header that was
  published with this DLL; check_exports.py validates that pair). An ILC publish is a heavy job: run it under the UE
  lock, from Git Bash:
    UE_LOCK_TAG=a/A5 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <this>

  -StageOnly: no publish. Creates the target folders and copies the ALREADY published ChimeraSim.dll (+ pdb) to
  <StageProject>/Binaries/ThirdParty/ChimeraSim/Win64/ and the PUBLISHED chimera_sim.h (bin/publish/chimera_sim.h, not
  the working copy in include/) to
  <StageProject>/Source/ChimeraSimHost/Private/ThirdParty/, verifying each copy by SHA-256. Staging writes into the
  Unreal project, so run it under the lock too (first used by A10). -StageProject overrides the project folder (tests).

  VS 2026's vcvarsall calls vswhere by bare name, so the VS Installer folder is prepended to PATH (plan A F9).
  Exit codes: 0 ok, 2 nothing published to stage, 3 publish failed, 4 ILC warnings present, no native compile in the
  log, or DLL missing, 5 the published DLL is in use by another process (wait for it to unload, then retry; the old
  bin/publish is left as it was).
#>
[CmdletBinding()]
param(
    [switch]$StageOnly,
    [string]$StageProject = 'D:/Projects/Chimera-Unreal/ProjectChimera',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$nat      = $PSScriptRoot
$proj     = Join-Path $nat 'ProjectChimera.Sim.Native.csproj'
$outDir   = Join-Path $nat 'bin/publish'
$log      = Join-Path $nat 'publish.log'
$header   = Join-Path $nat 'include/chimera_sim.h'
$dllName  = 'ChimeraSim.dll'

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

if ($StageOnly) {
    $dll = Join-Path $outDir $dllName
    if (-not (Test-Path -LiteralPath $dll)) {
        [Console]::Error.WriteLine("nothing to stage: $dll does not exist (run publish.ps1 without -StageOnly first)")
        exit 2
    }
    $binDir = Join-Path $StageProject 'Binaries/ThirdParty/ChimeraSim/Win64'
    $incDir = Join-Path $StageProject 'Source/ChimeraSimHost/Private/ThirdParty'
    New-Item -ItemType Directory -Force -Path $binDir, $incDir | Out-Null
    $pubHeader = Join-Path $outDir 'chimera_sim.h'
    if (-not (Test-Path -LiteralPath $pubHeader)) {
        [Console]::Error.WriteLine("nothing to stage: $pubHeader does not exist (republish: older publishes carried no header)")
        exit 2
    }
    $pairs = @(
        @{ Src = $dll; Dst = (Join-Path $binDir $dllName) },
        @{ Src = $pubHeader; Dst = (Join-Path $incDir 'chimera_sim.h') }
    )
    $pdb = Join-Path $outDir 'ChimeraSim.pdb'
    if (Test-Path -LiteralPath $pdb) { $pairs += @{ Src = $pdb; Dst = (Join-Path $binDir 'ChimeraSim.pdb') } }
    foreach ($p in $pairs) {
        Copy-Item -LiteralPath $p.Src -Destination $p.Dst -Force
        $a = Get-Sha256 $p.Src; $b = Get-Sha256 $p.Dst
        if ($a -ne $b) { [Console]::Error.WriteLine("stage copy mismatch for $($p.Dst)"); exit 4 }
        Write-Host ("staged {0} sha256={1}" -f $p.Dst, $b)
    }
    exit 0
}

# --- publish ---------------------------------------------------------------------------------------------------
$vsInstaller = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root   = (& git -C $nat rev-parse --show-toplevel).Trim()
$commit = (& git -C $root rev-parse HEAD).Trim()
$status = (& git -C $root status --porcelain -- godot tools/sim-trial) | Out-String
$dirty  = if ($status.Trim().Length -eq 0) { '0' } else { '1' }
Write-Host "publish: commit=$commit dirty=$dirty config=$Configuration -> $outDir"

# A DLL loaded by a harness or host cannot be replaced: say so with a documented code before spending a publish.
function Test-InUse([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try { $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None'); $fs.Dispose(); return $false }
    catch { return $true }
}
$oldDll = Join-Path $outDir $dllName
if (Test-InUse $oldDll) { [Console]::Error.WriteLine("output in use: $oldDll (wait for it to unload, then retry)"); exit 5 }

# Force a real ILC run: IlcCompile is incremental (its Outputs are obj/.../native/ChimeraSim.obj), so a re-run with no
# source change would otherwise skip it and log 0 IL warnings without compiling anything.
$tfmRid = 'net8.0/win-x64'
foreach ($d in @((Join-Path $nat "obj/$Configuration/$tfmRid/native"), (Join-Path $nat "bin/$Configuration/$tfmRid/native"))) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
}
$newDir = Join-Path $nat 'bin/publish.new'
if (Test-Path -LiteralPath $newDir) { Remove-Item -LiteralPath $newDir -Recurse -Force }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet publish $proj -c $Configuration -r win-x64 -o $newDir "-p:SourceRevisionId=$commit" "-p:ChimeraDirty=$dirty" 2>&1 |
    Tee-Object -FilePath $log
$rc = $LASTEXITCODE
Write-Host ("publish: dotnet exit {0} in {1:n0} s; log {2}" -f $rc, $sw.Elapsed.TotalSeconds, $log)
if ($rc -ne 0) { exit 3 }

$newDll = Join-Path $newDir $dllName
if (-not (Test-Path -LiteralPath $newDll)) { [Console]::Error.WriteLine("publish produced no $dllName"); exit 4 }
$nativeRuns = @(Select-String -LiteralPath $log -SimpleMatch -Pattern 'Generating native code').Count
$ilWarnings = @(Select-String -LiteralPath $log -SimpleMatch -Pattern 'warning IL').Count
Write-Host ("publish: {0} bytes sha256={1} il_warnings={2} native_compile={3}" -f (Get-Item -LiteralPath $newDll).Length, (Get-Sha256 $newDll), $ilWarnings, $nativeRuns)
if ($nativeRuns -lt 1) { [Console]::Error.WriteLine("no 'Generating native code' in $log : the IL count would not describe an ILC run"); exit 4 }
if ($ilWarnings -ne 0) { exit 4 }

# The header published WITH this DLL (what check_exports.py and -StageOnly use).
Copy-Item -LiteralPath $header -Destination (Join-Path $newDir 'chimera_sim.h') -Force
Write-Host ("publish: header sha256={0}" -f (Get-Sha256 (Join-Path $newDir 'chimera_sim.h')))

# Swap bin/publish.new into bin/publish.
if (Test-Path -LiteralPath $outDir) {
    try { Remove-Item -LiteralPath $outDir -Recurse -Force }
    catch {
        [Console]::Error.WriteLine("output in use: $outDir ($($_.Exception.Message)); the new publish is kept in $newDir")
        exit 5
    }
}
Move-Item -LiteralPath $newDir -Destination $outDir
Write-Host "publish: swapped into $outDir"
exit 0
