# Start the Blender MCP host: a headless Blender 5.2.1 serving the Blender Lab MCP add-on
# on 127.0.0.1:9876. The stdio side (`blender-mcp` in .mcp.json) connects to this.
#
#   .\blender_mcp_host.ps1            start it (no-op if already listening)
#   .\blender_mcp_host.ps1 -Stop      stop it
#   .\blender_mcp_host.ps1 -Status    report
#
# WHY THIS BLENDER AND NOT THE PIPELINE'S. The add-on declares blender_version_min = "5.1.0",
# so it cannot run on the 4.5.10 LTS that the asset pipeline is pinned to. The two coexist
# deliberately: 5.2.1 hosts the MCP, 4.5.10 bakes the roster (run_manifest.py resolves it from
# CHIMERA_BLENDER), and the Blender version string is folded into the pipeline's stage identity
# so the two can never be silently confused for one another.
#
# --online-mode is REQUIRED, not optional: the add-on declares a `network` permission, and
# without it Blender refuses to enable the add-on and exits with
# "Online access must be enabled in the system preferences".

param(
    [switch]$Stop,
    [switch]$Status
)

$Blender = $env:CHIMERA_BLENDER_MCP
if (-not $Blender) { $Blender = "D:\tools\blender\blender-5.2.1-windows-x64\blender.exe" }
$Port = 9876
$LogDir = "D:\tools\blender-mcp"

function Get-Listener {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

if ($Status) {
    $c = Get-Listener
    if ($c) { Write-Output "LISTENING on $Port (pid $($c.OwningProcess))" }
    else { Write-Output "not running" }
    exit 0
}

if ($Stop) {
    $c = Get-Listener
    if ($c) { Stop-Process -Id $c.OwningProcess -Force; Write-Output "stopped pid $($c.OwningProcess)" }
    else { Write-Output "not running" }
    exit 0
}

if (Get-Listener) { Write-Output "already listening on $Port"; exit 0 }

if (-not (Test-Path $Blender)) {
    Write-Error "Blender not found at $Blender (set CHIMERA_BLENDER_MCP)"
    exit 2
}

$p = Start-Process -FilePath $Blender `
    -ArgumentList "--background", "--online-mode", "--command", "blender_mcp" `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$LogDir\bl_out.log" -RedirectStandardError "$LogDir\bl_err.log"

for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 700
    if (Get-Listener) { Write-Output "Blender MCP host up on $Port (pid $($p.Id))"; exit 0 }
}

Write-Error "host did not start listening on $Port within ~14s"
Get-Content "$LogDir\bl_out.log" -Tail 15
Get-Content "$LogDir\bl_err.log" -Tail 15
exit 1
