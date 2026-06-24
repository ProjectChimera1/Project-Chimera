@echo off
REM ============================================================================
REM  Story 1.9a - one-click loopback desync smoke.
REM  Launches a headless dedicated server + TWO auto-joining client windows.
REM  Once both clients are in the match: click EITHER client window and press F9
REM  to induce a one-peer desync. Expected result: the server detects no strict
REM  majority (N=2) and BOTH clients show a red "MATCH HALTED" overlay.
REM
REM  Requires the C# build to be current (run `dotnet build godot/godot.csproj`
REM  once if you changed code). Edit GODOT / PROJ below if your paths differ.
REM ============================================================================

setlocal
REM GUI exe (windowed clients). The folder also has a *_console.exe variant if you want server stdout.
set "GODOT=C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe"
set "PROJ=D:\Projects\Project_Chimera\godot"
set "PORT=7777"

if not exist "%GODOT%" (
  echo [ERROR] Godot not found at "%GODOT%".
  echo         Edit the GODOT path at the top of this file.
  pause & exit /b 1
)
if not exist "%PROJ%\project.godot" (
  echo [ERROR] No project.godot under "%PROJ%".
  echo         Edit the PROJ path at the top of this file.
  pause & exit /b 1
)

echo Starting headless dedicated server on port %PORT% ...
start "Chimera Server (port %PORT%)" "%GODOT%" --path "%PROJ%" --headless -- --port %PORT%

echo Waiting 3s for the server to bind the port ...
timeout /t 3 /nobreak >nul

echo Launching client 1 (auto-join 127.0.0.1:%PORT%) ...
start "Chimera Client 1" "%GODOT%" --path "%PROJ%" -- --autojoin 127.0.0.1:%PORT%

timeout /t 1 /nobreak >nul

echo Launching client 2 (auto-join 127.0.0.1:%PORT%) ...
start "Chimera Client 2" "%GODOT%" --path "%PROJ%" -- --autojoin 127.0.0.1:%PORT%

echo.
echo ============================================================================
echo  Two client windows will AUTO-JOIN and start the match (a few seconds).
echo.
echo   ==^> Click EITHER client window and press  F9  to induce a desync.
echo.
echo  Expected: BOTH clients show a red "MATCH HALTED" overlay, and the match
echo  stops advancing. Close the three windows when done.
echo ============================================================================
echo.
pause
endlocal
