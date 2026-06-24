# ============================================================================
#  Story 1.9b — two-machine LAN determinism launcher (FR-39, the #1 ship gate).
#
#  Parameterized sibling of loopback-desync-smoke.ps1: instead of forcing
#  127.0.0.1, you pick a -Role and (for clients) the dedicated server's LAN IP.
#  Each invocation launches exactly ONE process, so you can compose the pinned
#  two-machine topology (see godot/tools/lan-determinism-runbook.md):
#
#    Machine A (the server's machine):
#        powershell -File lan-desync-smoke.ps1 -Role server
#        powershell -File lan-desync-smoke.ps1 -Role client -ServerIp 127.0.0.1
#    Machine B:
#        powershell -File lan-desync-smoke.ps1 -Role client -ServerIp <machine-A-LAN-IP>
#
#  Find machine A's LAN IP with `ipconfig` (the IPv4 of the active adapter,
#  e.g. 192.168.1.100). Allow inbound UDP 7777 through Windows Firewall on A.
#
#  Once both clients are in the match: click a CLIENT window and press F9 to
#  induce a one-peer desync. Expected: the server console prints
#  "GLOBAL DESYNC … Broadcasting terminal HALT" and BOTH clients show the red
#  "MATCH HALTED" overlay. For a clean PASS run, just play 300+ ticks and read
#  the server console's "[Determinism] … window #N" lines + the MATCH SUMMARY.
#
#  Edit $Godot / $Proj below if your paths differ.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('server', 'client')]
    [string]$Role,

    # The dedicated server's address (client role only). On machine A use 127.0.0.1;
    # on machine B use machine A's LAN IP (e.g. 192.168.1.100).
    [string]$ServerIp = '127.0.0.1',

    [int]$Port = 7777,

    # Kill leftover loopback/LAN Godot instances on THIS machine first. OFF by default so that,
    # on machine A, launching the client does not kill the server you just started. Use only to
    # clear a stale process before a fresh server launch.
    [switch]$CleanFirst
)

$ErrorActionPreference = 'Continue'
$Godot = 'C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe'
$Proj  = 'D:\Projects\Project_Chimera\godot'

if (-not (Test-Path $Godot)) { Write-Host "[ERROR] Godot not found at $Godot — edit `$Godot in this script." -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }
if (-not (Test-Path (Join-Path $Proj 'project.godot'))) { Write-Host "[ERROR] No project.godot under $Proj — edit `$Proj." -ForegroundColor Red; Read-Host 'Press Enter to exit'; exit 1 }

if ($CleanFirst) {
    Write-Host 'Cleaning up any leftover loopback/LAN Godot instances on this machine...' -ForegroundColor Cyan
    $stale = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Godot*' -and $_.CommandLine -and ($_.CommandLine -match '--autojoin|--server|--headless') }
    foreach ($s in $stale) { Write-Host "  killing leftover PID $($s.ProcessId)"; Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

if ($Role -eq 'server') {
    Write-Host "Starting dedicated SERVER window on port $Port ..." -ForegroundColor Cyan
    Start-Process $Godot -ArgumentList @('--path', $Proj, '--', '--server', '--port', "$Port")
    Write-Host ''
    Write-Host '============================================================================' -ForegroundColor Green
    Write-Host "  This machine is the dedicated SERVER (port $Port)." -ForegroundColor Green
    Write-Host '  ==> Find this machine''s LAN IP with  ipconfig  (IPv4, e.g. 192.168.1.100)' -ForegroundColor Yellow
    Write-Host '      and allow inbound UDP 7777 through Windows Firewall.' -ForegroundColor Yellow
    Write-Host '  Watch this server window''s Output for the [Determinism] verdict lines.' -ForegroundColor Green
    Write-Host '============================================================================' -ForegroundColor Green
}
else {
    Write-Host "Launching CLIENT (auto-join ${ServerIp}:$Port) ..." -ForegroundColor Cyan
    Start-Process $Godot -ArgumentList @('--path', $Proj, '--', '--autojoin', "${ServerIp}:$Port")
    Write-Host ''
    Write-Host '============================================================================' -ForegroundColor Green
    Write-Host "  CLIENT auto-joining ${ServerIp}:$Port and auto-readying." -ForegroundColor Green
    Write-Host '  When both clients are in the match, click this window and play 300+ ticks.' -ForegroundColor Green
    Write-Host '  Press  F9  to induce a desync drill (both clients should show MATCH HALTED).' -ForegroundColor Yellow
    Write-Host '  The HUD top line shows  Hash 0x........  ONLINE  — both machines must match.' -ForegroundColor Green
    Write-Host '============================================================================' -ForegroundColor Green
}
