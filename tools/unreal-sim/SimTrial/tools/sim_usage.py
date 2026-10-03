"""A11 editor-side job (look-test job bridge, see sim_bridge.py): give every team material instance of unit_meshes.json the
"Used with Instanced Static Meshes" usage, so A11's ISM renderer draws them in -game.

Why: the team MIs (A9) inherit their usage from the Interchange glTF master /InterchangeAssets/gltf/Substrate/M_GLTF, which is
engine plugin content without that usage. In -game GIsEditor is false, so UMaterialInstance::SetMaterialUsage cannot add it at
load and the ISMs fall back to the default material ("missing usage flag InstancedStaticMeshes", MaterialInstance.cpp:1762-1840).
UE 5.8 MIs carry their own usage overrides (FMaterialInstanceBasePropertyOverrides::bOverride_UsageFlags), set here through
MaterialEditingLibrary.SetMaterialUsageOverride (MaterialEditingLibrary.h:223). Only the project's /Game/SimUnits MIs are
changed and saved; the engine master and the look test's own materials are not touched.

run()  : set + save + read back the override on every team_mi; returns {'mis': n, 'ok': n, 'rows': [...]}; raises on any miss.
quit() : ask the editor to exit cleanly after this job's result is written.
"""
import json

import unreal

import lt_common as C

TABLE = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial/unit_meshes.json'
REPORT = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial/run/usage_report.json'
EAL = unreal.EditorAssetLibrary
MEL = unreal.MaterialEditingLibrary


def run():
    table = json.load(open(TABLE, encoding='utf-8'))
    usage = unreal.MaterialUsage.MATUSAGE_INSTANCED_STATIC_MESHES
    rows, bad = [], []
    paths = []
    for e in table['entries']:
        for p in e.get('team_mi', []):
            if p not in paths:
                paths.append(p)
    for p in paths:
        if not p.startswith('/Game/SimUnits/'):
            bad.append(f'{p}: not a /Game/SimUnits asset (refusing to change it)')
            continue
        mi = C.load(p)
        if not isinstance(mi, unreal.MaterialInstanceConstant):
            bad.append(f'{p}: not a MaterialInstanceConstant ({type(mi).__name__})')
            continue
        before = MEL.has_material_usage(mi, usage)
        MEL.set_material_usage_override(mi, usage, True, True)
        MEL.update_material_instance(mi)
        saved = EAL.save_asset(p, False)
        after = MEL.has_material_usage(mi, usage)
        override = MEL.has_material_usage_override(mi, usage)
        rows.append({'mi': p, 'before': bool(before), 'after': bool(after), 'override': bool(override), 'saved': bool(saved)})
        if not (after and override):
            bad.append(f'{p}: usage read back after={after} override={override}')
    rep = {'mis': len(paths), 'ok': sum(1 for r in rows if r['after'] and r['override']), 'rows': rows, 'errors': bad}
    with open(REPORT, 'w', encoding='utf-8') as f:
        json.dump(rep, f, indent=1)
    unreal.log(f'sim_usage: {rep["ok"]}/{rep["mis"]} team MIs used with instanced static meshes; errors={len(bad)}')
    if bad:
        raise RuntimeError('; '.join(bad))
    return {'mis': rep['mis'], 'ok': rep['ok']}


def quit():
    """Close the editor cleanly (console QUIT_EDITOR is processed after this job's result is written)."""
    unreal.SystemLibrary.execute_console_command(C.editor_world(), 'QUIT_EDITOR')
    return 'quit requested'
