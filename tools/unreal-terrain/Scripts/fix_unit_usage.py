"""Give the unit mesh's material the "Used with Instanced Static Meshes" usage (task C9). Runs inside UnrealEditor-Cmd through the Python
commandlet, started by Tools/run_commandlet.ps1 under the lock:
  UE_LOCK_TAG=c/C9 bash D:/Projects/Chimera-Unreal/ue_lock.sh powershell -NoProfile -ExecutionPolicy Bypass -File <T>/Tools/run_commandlet.ps1 \
      -Script <T>/Scripts/fix_unit_usage.py -Tag c9_usage -Sentinel USAGE_OK

Why: the unit layer (Game/TerrainUnitsActor) draws crucible_mortar through one UInstancedStaticMeshComponent with the mesh's own material, the
Interchange glTF material instance tripo_node_..._material (parent: the engine's Interchange glTF master, which lacks that usage). In -game
GIsEditor is false, so UMaterialInstance cannot add the usage at load and the ISM falls back to the default WorldGrid material
("missing usage flag InstancedStaticMeshes! Default Material will be used in game", MaterialInstance.cpp:1826). UE 5.8 material instances carry
their own usage overrides: MaterialEditingLibrary.SetMaterialUsageOverride (MaterialEditingLibrary.h:223), the fix check (a)'s A11 used
(ProjectChimera/SimTrial/tools/sim_usage.py). Only ChimeraTerrain's COPY under /Game/LookTest/Roster/crucible_mortar is changed and saved
(Content/ is git-ignored, so this script is the reproducible record: copy the folder from ProjectChimera, then run this).

Writes the report JSON named by argv[1]; prints `USAGE_OK <n>/<n>` or `USAGE_FAIL <reason>` and raises (the commandlet then exits non-zero).
"""
import json
import sys

import unreal

EAL = unreal.EditorAssetLibrary
MEL = unreal.MaterialEditingLibrary
MESH = '/Game/LookTest/Roster/crucible_mortar/crucible_mortar/StaticMeshes/crucible_mortar'
ROOT = '/Game/LookTest/Roster/crucible_mortar/'


def main(report_path):
    usage = unreal.MaterialUsage.MATUSAGE_INSTANCED_STATIC_MESHES
    rows, bad = [], []
    mesh = EAL.load_asset(MESH)
    if not isinstance(mesh, unreal.StaticMesh):
        bad.append('%s: not a StaticMesh (%s)' % (MESH, type(mesh).__name__))
        mats = []
    else:
        mats = [m.material_interface for m in mesh.static_materials if m.material_interface]
    if mesh is not None and not mats:
        bad.append('%s: no materials' % MESH)
    for mi in mats:
        p = mi.get_path_name().split('.')[0]
        if not p.startswith(ROOT):
            bad.append('%s: outside %s (refusing to change it)' % (p, ROOT))
            continue
        if not isinstance(mi, unreal.MaterialInstanceConstant):
            bad.append('%s: not a MaterialInstanceConstant (%s)' % (p, type(mi).__name__))
            continue
        before = MEL.has_material_usage(mi, usage)
        MEL.set_material_usage_override(mi, usage, True, True)
        MEL.update_material_instance(mi)
        saved = EAL.save_asset(p, False)
        after = MEL.has_material_usage(mi, usage)
        override = MEL.has_material_usage_override(mi, usage)
        rows.append({'mi': p, 'before': bool(before), 'after': bool(after), 'override': bool(override), 'saved': bool(saved)})
        if not (after and override and saved):
            bad.append('%s: read back after=%s override=%s saved=%s' % (p, after, override, saved))
    ok = sum(1 for r in rows if r['after'] and r['override'] and r['saved'])
    rep = {'mesh': MESH, 'mis': len(mats), 'ok': ok, 'rows': rows, 'errors': bad}
    with open(report_path, 'w', encoding='utf-8') as f:
        json.dump(rep, f, indent=1)
    if bad or ok == 0:
        unreal.log_error('USAGE_FAIL %s' % '; '.join(bad or ['no material changed']))
        raise RuntimeError('; '.join(bad or ['no material changed']))
    unreal.log('USAGE_OK %d/%d %s' % (ok, len(mats), json.dumps(rows)))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'fix_unit_usage_report.json')
