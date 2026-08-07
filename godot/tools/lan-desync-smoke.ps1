# ============================================================================
#  Story 1.9b — two-machine LAN determinism launcher (FR-39, the #1 ship gate).
#
#  ⚠ THE F9 DESYNC DRILL REQUIRES A SOURCE / DEBUG BUILD. The F9 desync-injection hotkey and the
#    client's `--autojoin` flag are both compiled under `#if DEBUG` (src/Core/MainScene.cs) and are
#    ABSENT from an exported release build. On a release/export build F9 is a SILENT no-op — no
#    error, no log — so the match never desyncs and a clean run there means the drill was NOT
#    exercised, NOT that determinism held (DW-238). Run this against the editor / `dotnet build`
#    (Debug) game. The clean-pass [Determinism] window + MATCH SUMMARY readout
#    (src/Multiplayer/Server/ServerHost.cs) is NOT DEBUG-gated and works in any build.
#
#  Each invocation launches exactly ONE process, so you compose the pinned two-machine topology
#  (see godot/tools/lan-determinism-runbook.md):
#
#    Machine A (the server's machine):
#        powershell -File lan-desync-smoke.ps1 -Role server            # blocks: this IS the server
#        powershell -File lan-desync-smoke.ps1 -Role client -ServerIp 127.0.0.1
#    Machine B:
#        powershell -File lan-desync-smoke.ps1 -Role client -ServerIp <machine-A-LAN-IP>
#
#  Find machine A's LAN IP with `ipconfig` (the IPv4 of the active adapter). Allow inbound UDP 7777
#  through Windows Firewall on A.
#
#  Once both clients are in the match: click a CLIENT window and press F9 to induce a one-peer
#  desync. Expected: the server console prints "GLOBAL DESYNC … Broadcasting terminal HALT" and BOTH
#  clients show the red "MATCH HALTED" overlay. For a clean PASS run, play 300+ ticks and read the
#  server console's "[Determinism] … window #N" lines + the MATCH SUMMARY.
#
#  ── 2026-08-07 rewrite (DW-906), after the first-ever live two-machine run ──────────────────────
#  Three faults this launcher shipped with, all found in one session:
#    (1) The server was launched via `Start-Process`, which DETACHES it with no attached console.
#        Its stdout went nowhere, so the [Determinism] verdict — the entire point of the runbook —
#        was UNREADABLE. The server role now runs in the FOREGROUND of this window and tees to a log.
#    (2) That same detach leaked an orphan server holding UDP 7777 after every run. The next launch
#        then failed with "Couldn't create an ENet host" / CantCreate, and a client launched instead
#        of a fresh server would silently rejoin the STALE one — carrying frozen-slot and tick state
#        across matches (DW-598/599/600), which produces a garbage result that looks like a finding.
#        Cleanup is now ON by default and ROLE-AWARE: the server role kills only stale servers, the
#        client role kills only stale clients, so launching a client can never kill your server.
#        (That hazard is exactly why the old -CleanFirst was off by default; role-awareness fixes the
#        cause instead of the symptom.) Use -NoClean to opt out.
#    (3) $Godot / $Proj were hardcoded to one machine's paths, so machine B had to hand-edit the
#        script. $Proj is now derived from this script's own location; $Godot is probed, with a
#        -GodotExe override.
#  Also: the server now runs --headless rather than the DEBUG-only `--server`. Headless server mode
#  is selected by DisplayServer.GetName()=="headless" (MainScene.cs:324), which is NOT DEBUG-gated
#  and is the same path the real dedicated server in docs/server-deploy uses.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('server', 'client')]
    [string]$Role,

    # The dedicated server's address (client role only). On machine A use 127.0.0.1;
    # on machine B use machine A's LAN IP (e.g. 192.168.1.100).
    [string]$ServerIp = '127.0.0.1',

    [int]$Port = 7777,

    # Skip the role-aware stale-instance cleanup. Cleanup is ON by default because a leaked server
    # holding the port is the single most common way this run fails (see note 2 above).
    [switch]$NoClean,

    # Override the Godot binary if the probe below does not find yours.
    [string]$GodotExe = ''
)

$ErrorActionPreference = 'Continue'

# ── Paths ───────────────────────────────────────────────────────────────────────────────────────
# This script lives in <repo>/godot/tools, so the Godot project dir is its parent.
$Proj     = Split-Path $PSScriptRoot -Parent
$RepoRoot = Split-Path $Proj -Parent
$LogDir   = Join-Path $RepoRoot 'lan-logs'

if (-not (Test-Path (Join-Path $Proj 'project.godot'))) {
    Write-Host "[ERROR] No project.godot under $Proj — is this script still in <repo>/godot/tools?" -ForegroundColor Red
    exit 1
}

if ($GodotExe) {
    $Godot = $GodotExe
} else {
    $candidates = @(
        'C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe',
        'C:\Godot\Godot_v4.6.3-stable_mono_win64.exe',
        "$env:LOCALAPPDATA\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe"
    )
    $Godot = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Godot) {
        $onPath = Get-Command godot -ErrorAction SilentlyContinue
        if ($onPath) { $Godot = $onPath.Source }
    }
}

if (-not $Godot -or -not (Test-Path $Godot)) {
    Write-Host '[ERROR] Godot 4.6.3 mono not found. Pass -GodotExe <full path to the .exe>.' -ForegroundColor Red
    Write-Host '        Probed:' -ForegroundColor DarkGray
    Write-Host '          C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe' -ForegroundColor DarkGray
    Write-Host '          %LOCALAPPDATA%\Godot\...  and  godot on PATH' -ForegroundColor DarkGray
    exit 1
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'

# ── Role-aware stale cleanup ────────────────────────────────────────────────────────────────────
# A SERVER is a Godot started with --headless (or the legacy --server); a CLIENT is one started with
# --autojoin. Matching on the role's own pattern means `-Role client` can never kill the server you
# just started on the same machine. The Godot EDITOR matches neither pattern and is never touched.
if (-not $NoClean) {
    $pattern = if ($Role -eq 'server') { '--headless|--server' } else { '--autojoin' }
    $stale = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Godot*' -and $_.CommandLine -and ($_.CommandLine -match $pattern) }
    if ($stale) {
        Write-Host "Cleaning stale $Role instance(s) on this machine..." -ForegroundColor Cyan
        foreach ($s in $stale) {
            Write-Host "  killing leftover PID $($s.ProcessId)" -ForegroundColor DarkGray
            Stop-Process -Id $s.ProcessId -Force -Confirm:$false -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 1
    }
}

if ($Role -eq 'server') {

    # Pre-flight: refuse to start on a busy port rather than dying inside Godot with a bare
    # "Couldn't create an ENet host" and a C# backtrace that names no owner.
    $busy = Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue
    if ($busy) {
        $ownerPid = $busy.OwningProcess | Select-Object -First 1
        $owner = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        Write-Host "[ERROR] UDP $Port is already held by PID $ownerPid ($($owner.ProcessName))." -ForegroundColor Red
        Write-Host '        A leftover server is still running. Re-run without -NoClean, or:' -ForegroundColor Yellow
        Write-Host "          Stop-Process -Id $ownerPid -Force" -ForegroundColor Yellow
        exit 1
    }

    $log = Join-Path $LogDir "$stamp-server.log"
    Write-Host '============================================================================' -ForegroundColor Green
    Write-Host "  DEDICATED SERVER (headless) on port $Port — this window IS the server." -ForegroundColor Green
    Write-Host "  Log: $log" -ForegroundColor Green
    Write-Host '  ==> Allow inbound UDP 7777 through Windows Firewall on this machine.' -ForegroundColor Yellow
    Write-Host '  ==> Find this machine''s LAN IP with  ipconfig  (IPv4) for machine B.' -ForegroundColor Yellow
    Write-Host '  Watch below for [Determinism] window lines and the final MATCH SUMMARY.' -ForegroundColor Green
    Write-Host '  Ctrl+C to stop the server.' -ForegroundColor DarkGray
    Write-Host '============================================================================' -ForegroundColor Green
    Write-Host ''

    # FOREGROUND + tee. Blocking is correct here: this console is the server's console, and its
    # stdout is the verdict. Do NOT Start-Process this — that is fault (1) above.
    & $Godot --headless --path $Proj -- --port $Port 2>&1 | Tee-Object -FilePath $log

} else {

    $out = Join-Path $LogDir "$stamp-client-$($ServerIp -replace '[^0-9a-zA-Z]', '_').out.log"
    $err = Join-Path $LogDir "$stamp-client-$($ServerIp -replace '[^0-9a-zA-Z]', '_').err.log"

    # Clients stay detached (Start-Process) — they are the interactive game windows, so this console
    # must return. Their output is redirected to files instead of scrolling into a console nobody
    # can copy out of on a remote session.
    Start-Process $Godot `
        -ArgumentList @('--path', $Proj, '--', '--autojoin', "${ServerIp}:$Port") `
        -RedirectStandardOutput $out `
        -RedirectStandardError  $err | Out-Null

    Write-Host ''
    Write-Host '============================================================================' -ForegroundColor Green
    Write-Host "  CLIENT auto-joining ${ServerIp}:$Port and auto-readying." -ForegroundColor Green
    Write-Host "  Log: $out" -ForegroundColor Green
    Write-Host '  When both clients are in the match, click this window and play 300+ ticks.' -ForegroundColor Green
    Write-Host '  Press  F9  to induce a desync drill (both clients should show MATCH HALTED).' -ForegroundColor Yellow
    Write-Host '  NOTE: F9 and --autojoin are #if DEBUG only — in a RELEASE build F9 silently does' -ForegroundColor Yellow
    Write-Host '        nothing, which reads as a PASS but is not one (DW-238).' -ForegroundColor Yellow
    Write-Host '  The HUD top line shows  Hash 0x........  ONLINE  — both machines must match.' -ForegroundColor Green
    Write-Host '  ⚠ Do NOT minimise, background, or screenshot-from-phone a remote session during a' -ForegroundColor Yellow
    Write-Host '    scored run: the window stops processing, stops submitting ticks, and the server' -ForegroundColor Yellow
    Write-Host '    drops that peer as a timeout. That is what ended the 2026-08-07 first run.' -ForegroundColor Yellow
    Write-Host '============================================================================' -ForegroundColor Green
}
