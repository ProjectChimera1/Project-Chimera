# DW-924 — loopback performance rig: headless server + 2 visible clients on ONE machine,
# stdout/stderr redirected per client so [Catchup]/[FrameProbe]/[FrameHistogram] lines are
# greppable afterwards. Baseline established 2026-08-10: this rig produces ZERO frames >=67 ms
# in ~14,000 ticks — if a run here suddenly shows Catchup bursts, whatever changed is the cause.
# The -ExtraArgs seam exists for the D3D12-vs-Vulkan A/B: -ExtraArgs "--rendering-driver vulkan"
# (the project ships d3d12; that flag is the per-run flip, no project.godot edit needed).
# 2026-08-11 (DW-930): boot mode is now Windowed and settings finally take effect — every run before
# this date (including the KNOWN-CLEAN baseline above) executed under EXCLUSIVE fullscreen despite
# settings.json "windowed". Re-record the baseline before comparing histograms across that boundary.
# Sibling: loopback-suspend-pulse.ps1 forces genuine long frames on a client (verifies the
# DW-924 [phase:] suffix fires and shows the "externally blocked" signature).
param(
    [string]$Tag = "baseline",
    [string]$ExtraArgs = ""   # extra engine-side args for clients, e.g. "--rendering-driver vulkan"
)
$ErrorActionPreference = 'Continue'
$Godot = 'C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe'
if (-not (Test-Path $Godot)) { $Godot = 'C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe' }
$Proj  = 'D:\Projects\Project_Chimera\godot'
$Out   = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) 'lan-logs'  # <repo>\lan-logs, same as the LAN scripts
New-Item -ItemType Directory -Force $Out | Out-Null

# Kill leftover loopback instances (never the editor: matched by loopback flags).
$stale = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Godot*' -and $_.CommandLine -and ($_.CommandLine -match '--autojoin|--server|--headless') }
foreach ($s in $stale) { Stop-Process -Id $s.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

$stamp = Get-Date -Format 'HHmmss'
$extra = @()
if ($ExtraArgs -ne "") { $extra = $ExtraArgs.Split(' ') }

Start-Process $Godot -ArgumentList (@('--headless','--path',$Proj,'--','--server','--port','7777')) `
    -RedirectStandardOutput "$Out\$Tag-$stamp-server.log" -RedirectStandardError "$Out\$Tag-$stamp-server.err.log" -WindowStyle Hidden
Start-Sleep -Seconds 4
Start-Process $Godot -ArgumentList ($extra + @('--path',$Proj,'--','--autojoin','127.0.0.1:7777')) `
    -RedirectStandardOutput "$Out\$Tag-$stamp-client1.log" -RedirectStandardError "$Out\$Tag-$stamp-client1.err.log"
Start-Sleep -Seconds 2
Start-Process $Godot -ArgumentList ($extra + @('--path',$Proj,'--','--autojoin','127.0.0.1:7777')) `
    -RedirectStandardOutput "$Out\$Tag-$stamp-client2.log" -RedirectStandardError "$Out\$Tag-$stamp-client2.err.log"
Write-Host "launched tag=$Tag stamp=$stamp"
