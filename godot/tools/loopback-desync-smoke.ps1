# ============================================================================
#  Story 1.9a — one-click loopback desync smoke.
#  Cleans up any leftover loopback processes, then opens a VISIBLE dedicated
#  server window + two auto-joining client windows. Once both clients are in
#  the match: click a CLIENT window and press F9 to induce a one-peer desync.
#  Expected: the server finds no majority (N=2) and BOTH clients show a red
#  "MATCH HALTED" overlay.
#
#  Safe to run repeatedly — it kills leftover loopback instances first (matched
#  by their command-line flags, so your Godot EDITOR is never touched).
#  Edit $Godot / $Proj below if your paths differ.
# ============================================================================

$ErrorActionPreference = 'Continue'
$Godot = 'C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe'
$Proj  = 'D:\Projects\Project_Chimera\godot'
$Port  = 7777

if (-not (Test-Path $Godot)) { Write-Host "[ERROR] Godot not found at $Godot — edit `$Godot in this script." -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }
if (-not (Test-Path (Join-Path $Proj 'project.godot'))) { Write-Host "[ERROR] No project.godot under $Proj — edit `$Proj." -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }

# 1) Kill leftover loopback instances from a previous run (server + clients). Matched by their loopback
#    command-line flags, so the Godot EDITOR (which has none of these) is never affected.
Write-Host 'Cleaning up any leftover loopback processes...' -ForegroundColor Cyan
$stale = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Godot*' -and $_.CommandLine -and ($_.CommandLine -match '--autojoin|--server|--headless') }
foreach ($s in $stale) { Write-Host "  killing leftover PID $($s.ProcessId)"; Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

# 2) Start a FRESH, VISIBLE dedicated server window.
Write-Host "Starting dedicated server window on port $Port ..." -ForegroundColor Cyan
Start-Process $Godot -ArgumentList @('--path', $Proj, '--', '--server', '--port', "$Port")
Start-Sleep -Seconds 3   # let it bind the port before clients connect

# 3) Two auto-joining client windows.
Write-Host "Launching client 1 (auto-join 127.0.0.1:$Port) ..." -ForegroundColor Cyan
Start-Process $Godot -ArgumentList @('--path', $Proj, '--', '--autojoin', "127.0.0.1:$Port")
Start-Sleep -Seconds 1
Write-Host "Launching client 2 (auto-join 127.0.0.1:$Port) ..." -ForegroundColor Cyan
Start-Process $Godot -ArgumentList @('--path', $Proj, '--', '--autojoin', "127.0.0.1:$Port")

Write-Host ''
Write-Host '============================================================================' -ForegroundColor Green
Write-Host '  3 windows open: 1 dedicated SERVER + 2 CLIENTS (auto-join + start match).' -ForegroundColor Green
Write-Host ''
Write-Host '  ==> Click EITHER client window and press  F9  to induce a desync.' -ForegroundColor Yellow
Write-Host ''
Write-Host '  Expected: BOTH clients show a red "MATCH HALTED" overlay.' -ForegroundColor Green
Write-Host '  Close all 3 windows when done (re-running this auto-cleans leftovers).' -ForegroundColor DarkGray
Write-Host '============================================================================' -ForegroundColor Green
