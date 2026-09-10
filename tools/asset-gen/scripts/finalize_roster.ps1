# Land a finished batch and make Godot actually show it.
#
#   .\finalize_roster.ps1            gate + land + reimport
#   .\finalize_roster.ps1 -SkipLand  reimport only
#
# WHY A SCRIPT. Two of these steps are easy to skip and both fail SILENTLY.
#
# 1. Godot does NOT reimport a changed .glb on an editor restart. The imported .scn in
#    .godot/imported/ stayed three months stale while the source was new, every asset kept its old
#    untextured material, and the symptom was indistinguishable from "the textures did not work".
#    The import cache has to be invalidated for the changed assets and a headless import run.
# 2. Landing re-gates. `--land-existing` does not skip verification -- it skips the expensive mesh
#    stage -- so nothing unverified reaches godot/assets/ either way.

param([switch]$SkipLand)

$ErrorActionPreference = "Stop"
$Repo   = "D:\Projects\Project_Chimera"
$Venv   = "D:\tools\asset-gen-venv\Scripts\python.exe"
$Godot  = "C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe"

if (-not $SkipLand) {
    Write-Output "=== gate + land ==="
    & $Venv "$Repo\tools\asset-gen\scripts\run_manifest.py" `
        --from-raw --mesh-profile paint --land-existing --land
    if ($LASTEXITCODE -ne 0) { Write-Warning "land reported failures - check the summary before trusting the roster" }
}

Write-Output "=== invalidating the stale Godot import cache ==="
$py = @'
import json, os, glob
man = json.load(open(r"D:\Projects\Project_Chimera\tools\asset-gen\config\chimera_assets.json", encoding="utf-8"))
names = {os.path.basename(a["dest"]) for a in man["assets"]}
n = 0
for f in glob.glob(r"D:\Projects\Project_Chimera\godot\.godot\imported\*"):
    b = os.path.basename(f)
    if any(b.startswith(nm + "-") for nm in names):
        os.remove(f); n += 1
print("invalidated", n)
'@
$py | Set-Content "$env:TEMP\invalidate.py" -Encoding utf8
& $Venv "$env:TEMP\invalidate.py"

Write-Output "=== headless reimport (Godot must NOT be running) ==="
$running = Get-Process Godot* -ErrorAction SilentlyContinue
if ($running) { Write-Warning "Godot is running - close it first or the import will contend for .godot/" }
& $Godot --headless --path "$Repo\godot" --import 2>&1 | Select-String -Pattern "ERROR|error" | Select-Object -First 5

$ctex = (Get-ChildItem "$Repo\godot\.godot\imported\*.ctex" -ErrorAction SilentlyContinue).Count
$scn  = (Get-ChildItem "$Repo\godot\.godot\imported\*.scn"  -ErrorAction SilentlyContinue).Count
Write-Output "imported: $scn scenes, $ctex textures"
if ($ctex -lt 24) { Write-Warning "fewer than 24 textures imported - the roster may not be fully textured" }
