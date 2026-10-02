@echo off
rem Project Chimera terrain trial (plan C scatter, task S2). The standalone scatter harness: compiles the pure Data/ layer and
rem Tests/TerrainScatterTests.cpp with plain cl.exe against shim/ (a tiny stand-in for the engine headers, CHIMERA_SCATTER_STANDALONE)
rem and runs every pure test in seconds. Light work (no Unreal process, no lock). The engine-only tests (options, real ISM) are skipped.
rem Usage: Tools\scatter_harness\build_and_run.bat [test-name filter]
rem Output: Saved\ScatterHarness\ (git-ignored). Override the toolchain with VCVARS=<path to vcvars64.bat>.
rem It compiles with cl's default /fp:precise, the same float mode as the editor build; the /fp:fast game target is SX16's job (S5).
setlocal
rem VS 2026 vcvarsall calls vswhere by bare name: put the VS Installer folder on PATH first.
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
if "%VCVARS%"=="" set "VCVARS=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
call "%VCVARS%" >nul
set "H=%~dp0"
set "S=%~dp0..\..\Source\ChimeraTerrain"
set "O=%~dp0..\..\Saved\ScatterHarness"
if not exist "%O%" mkdir "%O%"
cl /nologo /std:c++20 /O2 /EHsc /W3 /wd4244 /wd4267 /wd4996 /I "%H%shim" /I "%S%" /Fo"%O%\\" /Fe"%O%\scatter_tests.exe" "%H%main_tests.cpp" "%S%\Tests\TerrainScatterTests.cpp" "%S%\Data\TerrainHeightfield.cpp" "%S%\Data\TerrainBrush.cpp" "%S%\Data\TerrainUndo.cpp" "%S%\Data\TerrainScatterMath.cpp" "%S%\Data\TerrainScatterPalette.cpp" "%S%\Data\TerrainScatter.cpp" "%S%\Data\TerrainScatterScheduler.cpp" >"%O%\build.log" 2>&1
if errorlevel 1 (
  type "%O%\build.log"
  echo HARNESS BUILD FAILED
  exit /b 2
)
"%O%\scatter_tests.exe" %1
exit /b %errorlevel%
