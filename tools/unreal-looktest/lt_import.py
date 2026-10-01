"""Import the ground textures and the staged roster GLBs, check them, and make team-tint MIs.

PLAN.md §6.1 (ground), §6.4 (tint), PLAN_DELTA.md D3. Reads run/roster.json; writes
run/roster_report.json, which lt_build reads for mesh, anim and MI paths. Every
check failure is recorded per asset; the job raises after writing the report.
"""
import os

import unreal

import lt_common as C

MEL = unreal.MaterialEditingLibrary
EAL = unreal.EditorAssetLibrary


def _import_file(path, dest_dir, dest_name):
    """Automated AssetImportTask with no factory (Interchange); return the imported objects."""
    if not os.path.isfile(path):
        raise FileNotFoundError(path)
    t = unreal.AssetImportTask()
    C.set_props(t, {'filename': path, 'destination_path': dest_dir, 'destination_name': dest_name,
                    'automated': True, 'replace_existing': True, 'save': True})
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([t])
    paths = list(t.get_editor_property('imported_object_paths'))
    if not paths:
        raise RuntimeError(f'import of {path} produced no objects')
    objs = [EAL.load_asset(p) for p in paths]
    return [o for o in objs if o is not None]


def _folder_objects(folder):
    """Every asset under a content folder, loaded."""
    out = []
    for p in EAL.list_assets(folder, True, False):
        o = EAL.load_asset(p)
        if o is not None:
            out.append(o)
    return out


def _unique(objs):
    seen, out = set(), []
    for o in objs:
        k = o.get_path_name()
        if k not in seen:
            seen.add(k)
            out.append(o)
    return out


def _material_info(m):
    """Path, class, blend mode, BaseColorFactor presence/value for one slot material."""
    bm = m.get_blend_mode()
    names = [str(n) for n in MEL.get_vector_parameter_names(m)]
    info = {'path': C.obj_path(m), 'class': m.get_class().get_name(), 'blend_mode': C.enum_name(bm),
            'opaque': bm in (unreal.BlendMode.BLEND_OPAQUE, unreal.BlendMode.BLEND_MASKED),
            'has_base_color_factor': 'BaseColorFactor' in names, 'base_color_factor': None}
    if info['has_base_color_factor'] and isinstance(m, unreal.MaterialInstanceConstant):
        f = MEL.get_material_instance_vector_parameter_value(m, 'BaseColorFactor')
        info['base_color_factor'] = [f.r, f.g, f.b, f.a]
    return info


def _static_materials(mesh):
    n = len(mesh.get_editor_property('static_materials'))
    return [mesh.get_material(i) for i in range(n)]


def _skeletal_materials(mesh):
    return [sm.get_editor_property('material_interface') for sm in mesh.get_editor_property('materials')]


def _match_anims(clips, anims, prefix=''):
    """Map glTF clip names to imported AnimSequences by sanitised name; return (map, unmatched clips, unused anims).

    Interchange names each AnimSequence <asset name><sanitised clip> with no separator (verified live 2026-10-01:
    'cinder_cantorcast_a_spell', 'pierce_marksmanpreset_biped_fire'), so the asset-name prefix is stripped first.
    """
    pre = C.sanitize(prefix) if prefix else ''
    by_name = {}
    for a in anims:
        n = C.sanitize(a.get_name())
        by_name[a] = n[len(pre):] if pre and n.startswith(pre) else n
    mapping, unmatched = {}, []
    for clip in clips:
        full, base = C.sanitize(clip), C.sanitize(C.clip_base(clip))
        hits = [a for a, n in by_name.items() if n == full]
        hits = hits or [a for a, n in by_name.items() if n == base]
        hits = hits or [a for a, n in by_name.items() if n.endswith('_' + full)]
        hits = hits or [a for a, n in by_name.items() if n == base or n.endswith('_' + base)]
        if hits:
            mapping[clip] = sorted(hits, key=lambda a: len(a.get_name()))[0]
        else:
            unmatched.append(clip)
    used = {a.get_path_name() for a in mapping.values()}
    unused = [C.obj_path(a) for a in anims if a.get_path_name() not in used]
    return mapping, unmatched, unused


def _team_mi(key, faction, slot, parent, kind, folder):
    """Create or update MI_LTteam_<key>_<faction>_s<slot>: parent's BaseColorFactor x Godot team factor."""
    name = f'MI_LTteam_{key}_{faction}_s{slot}'
    path = f'{folder}/{name}'
    if EAL.does_asset_exist(path):
        mi = C.load(path)
    else:
        mi = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
            name, folder, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())
        if mi is None:
            raise RuntimeError(f'create_asset {path} failed')
    MEL.set_material_instance_parent(mi, parent)
    base = (1.0, 1.0, 1.0, 1.0)
    if isinstance(parent, unreal.MaterialInstanceConstant):
        f = MEL.get_material_instance_vector_parameter_value(parent, 'BaseColorFactor')
        base = (f.r, f.g, f.b, f.a)
    t = C.team_factor(faction, kind)
    val = (base[0] * t[0], base[1] * t[1], base[2] * t[2], base[3])
    # In 5.8.3 this setter returns False even when it succeeds (verified live 2026-10-01: the value lands in
    # vector_parameter_values and reads back), so judge it by the read-back, not the return value.
    MEL.set_material_instance_vector_parameter_value(mi, 'BaseColorFactor', unreal.LinearColor(*val))
    MEL.update_material_instance(mi)
    got = MEL.get_material_instance_vector_parameter_value(mi, 'BaseColorFactor')
    if max(abs(got.r - val[0]), abs(got.g - val[1]), abs(got.b - val[2])) > 1e-4:
        raise RuntimeError(f'{path}: BaseColorFactor read back {got} != {val}')
    EAL.save_asset(path, False)
    return {'path': path, 'parent_factor': list(base), 'team_factor': list(t), 'factor': list(val)}


def _ground(report):
    """Import grass/dirt to /Game/LookTest/Ground; sRGB must be on (forced on and recorded if not)."""
    for name, png in (('T_Grass', C.GRASS_PNG), ('T_Dirt', C.DIRT_PNG)):
        objs = _import_file(png, C.GROUND_DIR, name)
        tex = [o for o in objs if isinstance(o, unreal.Texture2D)]
        if len(tex) != 1:
            report['ground_failures'].append(f'{name}: {len(tex)} Texture2D from {png}')
            continue
        tex = tex[0]
        srgb_was = bool(tex.get_editor_property('srgb'))
        vt_was = bool(tex.get_editor_property('virtual_texture_streaming'))
        if not srgb_was or vt_was:
            # Albedo must be sRGB; VT off keeps the ground material on plain Color samplers.
            C.set_props(tex, {'srgb': True, 'virtual_texture_streaming': False})
            EAL.save_asset(C.obj_path(tex), False)
        report['ground'][name] = {'path': C.obj_path(tex), 'srgb_was': srgb_was, 'vt_was': vt_was}
        yield


def _collect_failures(report):
    """Report-wide failure list: ground plus every asset's own list (including assets from earlier runs)."""
    report['failures'] = list(report['ground_failures']) + [
        f'{k}: {f}' for k, a in report['assets'].items() for f in a.get('failures', [])]


def run(only=None, skip_existing=False):
    """Import everything in run/roster.json (or only the asset_keys listed); write run/roster_report.json."""
    entries = C.load_roster()
    keys = C.asset_keys(entries)
    if only:
        missing = [k for k in only if k not in keys]
        if missing:
            raise KeyError(f'not in roster: {missing}')
        keys = [k for k in keys if k in only]
    old = C.read_json(C.ROSTER_REPORT) if os.path.isfile(C.ROSTER_REPORT) else {}
    report = {'generated': C.now_iso(), 'ground': old.get('ground', {}), 'assets': dict(old.get('assets', {})),
              'ground_failures': old.get('ground_failures', []), 'failures': []}
    if not only:
        report.update(ground={}, ground_failures=[])
        yield from _ground(report)

    for key in keys:
        group = [e for e in entries if e['asset_key'] == key]
        e0 = group[0]
        folder = f'{C.ROSTER_ROOT}/{key}'
        fails = []
        if skip_existing and EAL.does_directory_have_assets(folder, True):
            imported = []
            print(f'[lt_import] {key}: skipped import (folder has assets)')
        else:
            imported = _import_file(e0['glb'], folder, key)
            print(f'[lt_import] {key}: {len(imported)} objects from {os.path.basename(e0["glb"])}')
        yield
        objs = _unique(imported + _folder_objects(folder))
        objs = [o for o in objs if not o.get_name().startswith('MI_LTteam_')]
        info = {'source': e0['source'], 'glb': e0['glb'], 'rigged': bool(e0['rigged']), 'kind': e0['kind'],
                'objects': sorted(f'{o.get_class().get_name()}:{C.obj_path(o)}' for o in objs)}
        mesh_cls = unreal.SkeletalMesh if e0['rigged'] else unreal.StaticMesh
        # Mesh count comes from this import when there was one; stale folder leftovers do not count.
        meshes = [o for o in (imported or objs) if isinstance(o, mesh_cls)]
        other = [o for o in (imported or objs) if isinstance(o, (unreal.StaticMesh, unreal.SkeletalMesh))
                 and not isinstance(o, mesh_cls)]
        if len(meshes) != 1 or other:
            fails.append(f'expected exactly 1 {mesh_cls.__name__}, got {len(meshes)} (+{len(other)} other meshes)')
        if meshes:
            mesh = meshes[0]
            info['mesh'] = C.obj_path(mesh)
            if e0['rigged']:
                b = mesh.get_imported_bounds()
                height, min_z = 2.0 * b.box_extent.z, b.origin.z - b.box_extent.z
                mats = _skeletal_materials(mesh)
                anims = [o for o in objs if isinstance(o, unreal.AnimSequence)]
                info['skeleton'] = [C.obj_path(o) for o in objs if isinstance(o, unreal.Skeleton)]
                if not anims:
                    fails.append('no AnimSequence imported')
                mapping, unmatched, unused = _match_anims(e0['anims'], anims, prefix=key)
                info['anims'] = {c: C.obj_path(a) for c, a in mapping.items()}
                info['anim_lengths'] = {c: float(a.get_play_length()) for c, a in mapping.items()}
                info['unmatched_clips'] = unmatched
                info['unused_anims'] = unused
                if not mapping and anims:
                    fails.append('no glTF clip matched any imported AnimSequence name')
            else:
                bb = mesh.get_bounding_box()
                height, min_z = bb.max.z - bb.min.z, bb.min.z
                mats = _static_materials(mesh)
                info['nanite'] = bool(mesh.get_editor_property('nanite_settings').get_editor_property('enabled'))
            expected = float(e0['src_height_m']) * 100.0
            info.update(height_cm=height, min_z_cm=min_z, expected_height_cm=expected,
                        height_ratio=height / expected if expected else None)
            if not expected or abs(height / expected - 1.0) > 0.05:
                fails.append(f'height {height:.1f} cm vs expected {expected:.1f} cm (>5%)')
            if any(m is None for m in mats) or not mats:
                fails.append(f'{len(mats)} material slots, some empty')
            mats = [m for m in mats if m is not None]
            info['materials'] = [_material_info(m) for m in mats]
            for mi in info['materials']:
                if not mi['opaque']:
                    fails.append(f'translucent material {mi["path"]} ({mi["blend_mode"]})')
                if not mi['has_base_color_factor']:
                    fails.append(f'{mi["path"]} has no BaseColorFactor vector parameter (team tint impossible)')
            yield
            if not fails:
                info['team_mi'] = {}
                for faction in sorted({e['faction'] for e in group}):
                    info['team_mi'][faction] = [_team_mi(key, faction, i, m, e0['kind'], folder)
                                                for i, m in enumerate(mats)]
        info['failures'] = fails
        report['assets'][key] = info
        _collect_failures(report)
        C.write_json(C.ROSTER_REPORT, report)
        yield

    _collect_failures(report)
    C.write_json(C.ROSTER_REPORT, report)
    if report['failures']:
        raise RuntimeError(f'{len(report["failures"])} import check failures (run/roster_report.json):\n  '
                           + '\n  '.join(report['failures']))
    return {'assets': len(keys), 'ground': sorted(report['ground']), 'report': C.ROSTER_REPORT}
