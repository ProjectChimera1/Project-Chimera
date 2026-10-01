# Builds NAT/harness/bin/chimera_harness.exe with cl.exe (vcvars via cmd /c inside pwsh). Light work: no lock.
# VS 2026's vcvarsall calls vswhere by bare name, so the VS Installer folder goes first on PATH (memory: nativeaot-vswhere-path-fix).
# Copies the DLL under test into bin/ once (harness/bin/ChimeraSim.dll); an existing copy is kept so a run is reproducible
# (pass -RefreshDll to take the current NAT/bin/publish/ChimeraSim.dll). Prints cl_exit=N.
param([switch]$RefreshDll)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$nat = Split-Path -Parent $here
$bin = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
$installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
$src = Join-Path $here 'chimera_harness.cpp'
$log = Join-Path $bin 'build.log'
$cmd = "set `"PATH=$installer;%PATH%`" && call `"$vcvars`" >nul && cd /d `"$bin`" && cl /nologo /EHsc /O2 /W3 /std:c++17 `"$src`" /Fe:chimera_harness.exe /Fo:chimera_harness.obj > `"$log`" 2>&1"
cmd /c $cmd
$clExit = $LASTEXITCODE
if ($RefreshDll -or -not (Test-Path (Join-Path $bin 'ChimeraSim.dll'))) {
    $pub = Join-Path $nat 'bin\publish\ChimeraSim.dll'
    if (Test-Path $pub) { Copy-Item $pub (Join-Path $bin 'ChimeraSim.dll') -Force }
}
Get-Content $log | Select-Object -Last 15
"cl_exit=$clExit"
exit $clExit
