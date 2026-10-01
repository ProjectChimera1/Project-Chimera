#requires -Version 7
<#
.SYNOPSIS
  Unreal trial A6 (plan A section 4 A6): the AOT golden replay gate, in three steps.

.DESCRIPTION
  -Step jit      Build SimAotGoldens WITHOUT PublishAot (JitMode=true: a plain JIT program, dynamic code on, reflection STJ on) into
                 bin/jit and run it. It checks the goldens and writes out/jit_reference.txt (every sequence and value the AOT leg
                 must reproduce). Light: no lock.
  -Step publish  ILC publish (PublishAot) to bin/publish, teed to publish.log. Heavy: run it under the UE lock, from Git Bash:
                   UE_LOCK_TAG=a/A6 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <this> -Step publish
                 Like NAT/publish.ps1 it deletes the native intermediates first, so every run is a real ILC compile, and it
                 fails (exit 4) unless the log shows "Generating native code" and holds 0 "warning IL" lines.
  -Step run      Run bin/publish/SimAotGoldens.exe against the JIT reference; prints the gate line
                   jit_equal=N1/N1 golden_equal=N2/N2 trial=1440/1440 unknown_effect=fail_closed runtime=.NET 8.0.25 aot=1
                 and exits 0 only when every check passed. Output also in out/aot.log and out/report_aot.txt. Light.
  -Step all      jit, publish, run in order (take the lock yourself around `all` only if you accept holding it through the jit leg).

  VS 2026's vcvarsall calls vswhere by bare name, so the VS Installer folder is prepended to PATH for the publish (plan A F9).
  Exit codes: 0 ok, 3 publish failed, 4 IL warnings / no native compile / exe missing, 1 a check failed, 2 bad step.
#>
[CmdletBinding()]
param(
    [ValidateSet('jit', 'publish', 'run', 'all')]
    [string]$Step = 'all',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$here    = $PSScriptRoot
$proj    = Join-Path $here 'SimAotGoldens.csproj'
$outDir  = Join-Path $here 'out'
$jitDir  = Join-Path $here 'bin/jit'
$pubDir  = Join-Path $here 'bin/publish'
$log     = Join-Path $here 'publish.log'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Step-Jit {
    Write-Host "jit: dotnet build (JitMode) -> $jitDir"
    & dotnet build $proj -c $Configuration -p:JitMode=true -p:IntermediateOutputPath=obj/jit/ "-p:OutDir=$jitDir/" 2>&1 |
        Tee-Object -FilePath (Join-Path $outDir 'jit_build.log')
    if ($LASTEXITCODE -ne 0) { exit 3 }
    $exe = Join-Path $jitDir 'SimAotGoldens.exe'
    & $exe 2>&1 | Tee-Object -FilePath (Join-Path $outDir 'jit.log')
    $rc = $LASTEXITCODE
    Write-Host "jit: exit $rc"
    if ($rc -ne 0) { exit 1 }
}

function Step-Publish {
    $vsInstaller = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer'
    if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
    # Force a real ILC run: IlcCompile is incremental, so a re-run with no source change would skip it and log 0 warnings.
    $tfmRid = 'net8.0/win-x64'
    foreach ($d in @((Join-Path $here "obj/$Configuration/$tfmRid/native"), (Join-Path $here "bin/$Configuration/$tfmRid/native"))) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    }
    if (Test-Path -LiteralPath $pubDir) { Remove-Item -LiteralPath $pubDir -Recurse -Force }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet publish $proj -c $Configuration -r win-x64 -o $pubDir 2>&1 | Tee-Object -FilePath $log
    $rc = $LASTEXITCODE
    Write-Host ("publish: dotnet exit {0} in {1:n0} s; log {2}" -f $rc, $sw.Elapsed.TotalSeconds, $log)
    if ($rc -ne 0) { exit 3 }
    $exe = Join-Path $pubDir 'SimAotGoldens.exe'
    if (-not (Test-Path -LiteralPath $exe)) { [Console]::Error.WriteLine("publish produced no SimAotGoldens.exe"); exit 4 }
    $nativeRuns = @(Select-String -LiteralPath $log -SimpleMatch -Pattern 'Generating native code').Count
    $ilWarnings = @(Select-String -LiteralPath $log -SimpleMatch -Pattern 'warning IL').Count
    $sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
    Write-Host ("publish: {0} bytes sha256={1} il_warnings={2} native_compile={3}" -f (Get-Item -LiteralPath $exe).Length, $sha, $ilWarnings, $nativeRuns)
    if ($nativeRuns -lt 1) { [Console]::Error.WriteLine("no 'Generating native code' in $log"); exit 4 }
    if ($ilWarnings -ne 0) { exit 4 }
}

function Step-Run {
    $exe = Join-Path $pubDir 'SimAotGoldens.exe'
    if (-not (Test-Path -LiteralPath $exe)) { [Console]::Error.WriteLine("missing $exe (run -Step publish first)"); exit 4 }
    & $exe 2>&1 | Tee-Object -FilePath (Join-Path $outDir 'aot.log')
    $rc = $LASTEXITCODE
    Write-Host "run: exit $rc"
    if ($rc -ne 0) { exit 1 }
}

switch ($Step) {
    'jit'     { Step-Jit }
    'publish' { Step-Publish }
    'run'     { Step-Run }
    'all'     { Step-Jit; Step-Publish; Step-Run }
}
exit 0
