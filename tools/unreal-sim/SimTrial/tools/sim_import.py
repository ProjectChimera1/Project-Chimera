"""A9 editor-side jobs (run through the look-test job bridge, see sim_bridge.py).

run()  : for each of the 18 entries in run/units_prep.json
         - import the staged unrigged GLB as a StaticMesh to /Game/SimUnits/<key>/ (action 'import'), or reuse the
           look-test StaticMesh from LookTest/run/roster_report.json (action 'reuse');
         - check it is a StaticMesh, has Nanite on, opaque materials with a BaseColorFactor parameter;
         - create MI_SimTeam_<key>_<faction> (slot 0; later slots get _s<n>) in /Game/SimUnits/<key>/ with
           lt_common.team_factor maths (Godot: mix(art, art*team, 0.35) * value), the same as lt_import._team_mi;
         - measure the mesh (bounds in cm at import scale) and compute the height/longest-side error after scale.
         Writes P/SimTrial/unit_meshes.json (the data A10's director reads) and run/unit_meshes_report.json.
quit() : ask the editor to exit cleanly after this job's result is written.
"""
import unreal

import lt_common as C
import lt_import as LI

SIM = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial'
PREP = SIM + '/run/units_prep.json'
REPORT = SIM + '/run/unit_meshes_report.json'
OUT = SIM + '/unit_meshes.json'
ROOT = '/Game/SimUnits'
EAL = unreal.EditorAssetLibrary
MEL = unreal.MaterialEditingLibrary


def _team_mis(key, faction, kind, mats, folder):
    """One MI per material slot, parent = the mesh's slot material; return [{'path', 'factor', ...}]."""
    out = []
    for i, m in enumerate(mats):
        name = f'MI_SimTeam_{key}_{faction}' + ('' if i == 0 else f'_s{i}')
        path = f'{folder}/{name}'
        if EAL.does_asset_exist(path):
            mi = C.load(path)
        else:
            mi = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
                name, folder, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())
            if mi is None:
                raise RuntimeError(f'create_asset {path} failed')
        MEL.set_material_instance_parent(mi, m)
        base = (1.0, 1.0, 1.0, 1.0)
        if isinstance(m, unreal.MaterialInstanceConstant):
            f = MEL.get_material_instance_vector_parameter_value(m, 'BaseColorFactor')
            base = (f.r, f.g, f.b, f.a)
        t = C.team_factor(faction, kind)
        val = (base[0] * t[0], base[1] * t[1], base[2] * t[2], base[3])
        # 5.8.3: the setter returns False even on success (see lt_import._team_mi); judge by the read-back.
        MEL.set_material_instance_vector_parameter_value(mi, 'BaseColorFactor', unreal.LinearColor(*val))
        MEL.update_material_instance(mi)
        got = MEL.get_material_instance_vector_parameter_value(mi, 'BaseColorFactor')
        if max(abs(got.r - val[0]), abs(got.g - val[1]), abs(got.b - val[2])) > 1e-4:
            raise RuntimeError(f'{path}: BaseColorFactor read back {got} != {val}')
        EAL.save_asset(path, False)
        out.append({'path': path, 'parent': C.obj_path(m), 'parent_factor': list(base), 'team_factor': list(t),
                    'factor': list(val)})
    return out


def run(only=None):
    """Import / reuse / tint all entries; write unit_meshes.json and the report; raise if any check failed."""
    prep = C.read_json(PREP, 'prep_units.py')['entries']
    facing = C.read_json(C.FACING_JSON, 'lt_facing.record')
    lt_report = C.read_json(C.ROSTER_REPORT, 'lt_import.run')['assets']
    if only:
        prep = [p for p in prep if f'{p["faction"]}/{p["def_id"]}' in only]
    entries, rep, fails = [], {}, []
    for p in prep:
        eid = f'{p["faction"]}/{p["def_id"]}'
        key, folder = p['key'], f'{ROOT}/{p["key"]}'
        f = []
        mesh = None
        if p['action'] == 'import':
            objs = LI._import_file(p['glb'], folder, key)
            yield
            meshes = [o for o in objs if isinstance(o, unreal.StaticMesh)]
            others = [o for o in objs if isinstance(o, (unreal.SkeletalMesh, unreal.AnimSequence, unreal.Skeleton))]
            if len(meshes) != 1 or others:
                f.append(f'expected exactly 1 StaticMesh, got {len(meshes)} (+{len(others)} skeletal/anim/skeleton objects)')
            mesh = meshes[0] if meshes else None
        else:
            path = lt_report[key]['mesh']
            mesh = EAL.load_asset(path)
            if not isinstance(mesh, unreal.StaticMesh):
                f.append(f'{path} is not a StaticMesh')
                mesh = None
        info = {'id': eid, 'key': key, 'action': p['action'], 'failures': f}
        if mesh is not None:
            info['mesh'] = C.obj_path(mesh)
            ns = mesh.get_editor_property('nanite_settings')
            nanite = bool(ns.get_editor_property('enabled'))
            if not nanite:
                # Nanite is wanted for the 2,000-unit ISM runs (A12): record that it was off and fail the entry.
                f.append('Nanite is off on the imported mesh')
            bb = mesh.get_bounding_box()
            size = (bb.max.x - bb.min.x, bb.max.y - bb.min.y, bb.max.z - bb.min.z)
            measured = size[2] if p['expected_dim'] == 'height' else max(size)
            err = measured * p['scale'] / p['expected_cm'] - 1.0
            n = len(mesh.get_editor_property('static_materials'))
            mats = [mesh.get_material(i) for i in range(n)]
            if not mats or any(m is None for m in mats):
                f.append(f'{n} material slots, some empty')
            mats = [m for m in mats if m is not None]
            minfo = [LI._material_info(m) for m in mats]
            for mi in minfo:
                if not mi['opaque']:
                    f.append(f'translucent material {mi["path"]} ({mi["blend_mode"]})')
                if not mi['has_base_color_factor']:
                    f.append(f'{mi["path"]} has no BaseColorFactor parameter (team tint impossible)')
            info.update(class_name=mesh.get_class().get_name(), nanite=nanite, size_cm=list(size), min_z_cm=bb.min.z,
                        measured_cm=measured, expected_cm=p['expected_cm'], expected_dim=p['expected_dim'],
                        height_err=err, materials=minfo)
            if abs(err) > 0.03:
                f.append(f'{p["expected_dim"]} error {err:+.2%} after scale (>3%)')
            if not f:
                tm = _team_mis(key, p['faction'], p['kind'], mats, folder)
                info['team_mi'] = tm
                entries.append({
                    'id': eid, 'faction': p['faction'], 'def_id': p['def_id'], 'kind': p['kind'], 'role': p['role'],
                    'key': key, 'source': p['source'], 'action': p['action'],
                    'mesh': info['mesh'], 'team_mi': [t['path'] for t in tm], 'scale': p['scale'],
                    'front_yaw_deg': float(facing['overrides'].get(key, facing['front_yaw'][p['front_source']])),
                    'foot_z_cm': -bb.min.z * p['scale'], 'size_cm': [s * p['scale'] for s in size],
                    'expected_cm': p['expected_cm'], 'expected_dim': p['expected_dim'],
                    'nanite': nanite, 'static': True})
        rep[eid] = info
        fails += [f'{eid}: {x}' for x in f]
        yield
    doc = {'version': 1, 'generated': C.now_iso(),
           'note': 'front_yaw_deg = actor yaw that turns the mesh front to +X (LookTest/run/facing.json, by source); '
                   'foot_z_cm lifts the pivot so the mesh rests on z=0; size_cm is after scale.',
           'entries': entries}
    C.write_json(REPORT, {'generated': C.now_iso(), 'entries': rep, 'failures': fails})
    if not fails and not only:
        C.write_json(OUT, doc)
    if fails:
        raise RuntimeError(f'{len(fails)} failures (see {REPORT}):\n  ' + '\n  '.join(fails))
    return {'entries': len(entries), 'out': OUT}


def quit():
    """Close the editor cleanly (console QUIT_EDITOR is processed after this job's result is written)."""
    unreal.SystemLibrary.execute_console_command(C.editor_world(), 'QUIT_EDITOR')
    return 'quit requested'
