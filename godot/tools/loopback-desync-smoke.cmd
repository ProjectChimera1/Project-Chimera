@echo off
REM Story 1.9a — one-click loopback desync smoke (double-click this).
REM Cleans up leftover loopback processes, then opens a VISIBLE dedicated server window
REM + two auto-joining client windows. Click a CLIENT window and press F9 to induce a
REM desync -> both clients should show a red "MATCH HALTED" overlay.
REM
REM REQUIRES A SOURCE / DEBUG BUILD (DW-324): the F9 drill is #if DEBUG only, so in a
REM RELEASE/exported build F9 silently does nothing -> a FALSE PASS. Editor/Debug only.
REM
REM All the work (incl. killing leftover servers) is in the .ps1 next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0loopback-desync-smoke.ps1"
echo.
pause
