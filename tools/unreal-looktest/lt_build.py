"""Build one look-test map: /Game/LookTest/Maps/LT_<look> for look in A, A_noLumen, B.

PLAN.md §7 (ground, layout, scatter, cameras, sky/post, manifest) as amended by
PLAN_DELTA.md D4 (rigged units as posed SkeletalMeshActors, roster `scale`, feet on
the ground). Layout, assets and cameras are identical for every look; only the
recipe (lt_common.RECIPES) differs. Needs run/roster_report.json (lt_import) and
run/facing.json (lt_facing.record).
"""
import os
import time

import unreal

import lt_common as C

MEL = unreal.MaterialEditingLibrary
EAL = unreal.EditorAssetLibrary
GROUND_MAT = C.MAT_DIR + '/M_LT_Ground'
PAD_MI = C.MAT_DIR + '/MI_LT_GroundPad'
GROUND_TILING = 240.0   # 1200 m plane / 240 = 5 m texture tiles
PAD_TILING = 8.0        # 40 m pad / 8 = 5 m tiles
BATCH = 20
RETUNE_BUDGET = 3              # PLAN §0.7/§8: at most 3 preview iterations per look
DERIVED = {'A_noLumen': 'A'}   # PLAN §0.5: A_noLumen is A with screen-space GI/reflections, tuned with A


def _manifest_path(look):
    return f'{C.RUN}/manifest_{look}.json'


def _resolve(look, overrides):
    """(overrides, recipe) for a look; a derived look takes its parent's recorded overrides, never its own."""
    parent = DERIVED.get(look)
    if parent:
        if overrides:
            raise ValueError(f'{look} cannot be tuned on its own; tune {parent} and its overrides carry over')
        path = _manifest_path(parent)
        overrides = C.read_json(path).get('overrides', {}) if os.path.isfile(path) else {}
    return dict(overrides or {}), C.resolve_recipe(look, overrides)


def _sampler_for(tex):
    """Sampler type the material compiler expects for a texture (mirrors MaterialExpressionUtils::GetSamplerTypeForTexture)."""
    st, tc = unreal.MaterialSamplerType, unreal.TextureCompressionSettings
    srgb = bool(tex.get_editor_property('srgb'))
    vt = bool(tex.get_editor_property('virtual_texture_streaming'))
    cs = tex.get_editor_property('compression_settings')
    if cs == tc.TC_NORMALMAP:
        kind = 'NORMAL'
    elif cs == tc.TC_GRAYSCALE:
        kind = 'GRAYSCALE' if srgb else 'LINEAR_GRAYSCALE'
    elif cs == tc.TC_ALPHA:
        kind = 'ALPHA'
    elif cs == tc.TC_MASKS:
        kind = 'MASKS'
    else:
        kind = 'COLOR' if srgb else 'LINEAR_COLOR'
    return getattr(st, ('SAMPLERTYPE_VIRTUAL_' if vt else 'SAMPLERTYPE_') + kind)


def _expr(mat, cls, x, y, **props):
    e = MEL.create_material_expression(mat, cls, x, y)
    if e is None:
        raise RuntimeError(f'create_material_expression({cls.__name__}) failed')
    C.set_props(e, props)
    return e


def _link(a, out, b, inp):
    if not MEL.connect_material_expressions(a, out, b, inp):
        raise RuntimeError(f'connect {a.get_name()}.{out or "<0>"} -> {b.get_name()}.{inp or "<0>"} failed')


GRASS_LAYER = '/Engine/StarterContent/Textures/T_ground_Moss_D'


def _ground_material(report):
    """(Re)build M_LT_Ground in place with legacy pins, plus the pad MI (DirtOverride=1). Return (mat, pad_mi)."""
    # Chimera's grass.png is a flat saturated green (mean RGB 56,112,35, sd ~18): in preview 1 it made the ground
    # read as cartoon in every look. The engine's photographic moss is used as the grass layer for ALL looks
    # instead (same asset everywhere, so the comparison stays fair); recorded in results as a caveat.
    grass = C.load(GRASS_LAYER)
    dirt = C.load(report['ground']['T_Dirt']['path'])
    noise = C.load(C.NOISE_TEX)
    at = unreal.AssetToolsHelpers.get_asset_tools()
    if EAL.does_asset_exist(GROUND_MAT):
        mat = C.load(GROUND_MAT)
    else:
        mat = at.create_asset('M_LT_Ground', C.MAT_DIR, unreal.Material, unreal.MaterialFactoryNew())
        if mat is None:
            raise RuntimeError(f'create_asset {GROUND_MAT} failed')
    MEL.delete_all_material_expressions(mat)  # also drops any default Substrate slab
    tc = _expr(mat, unreal.MaterialExpressionTextureCoordinate, -1400, 0)
    til = _expr(mat, unreal.MaterialExpressionScalarParameter, -1400, 150, parameter_name='Tiling',
                default_value=GROUND_TILING)
    uv = _expr(mat, unreal.MaterialExpressionMultiply, -1150, 50)
    _link(tc, '', uv, 'A')
    _link(til, '', uv, 'B')
    ntil = _expr(mat, unreal.MaterialExpressionScalarParameter, -1400, 350, parameter_name='NoiseTiling',
                 default_value=8.0)
    nuv = _expr(mat, unreal.MaterialExpressionMultiply, -1150, 300)
    _link(tc, '', nuv, 'A')
    _link(ntil, '', nuv, 'B')
    g = _expr(mat, unreal.MaterialExpressionTextureSample, -850, -200, texture=grass, sampler_type=_sampler_for(grass))
    d = _expr(mat, unreal.MaterialExpressionTextureSample, -850, 100, texture=dirt, sampler_type=_sampler_for(dirt))
    n = _expr(mat, unreal.MaterialExpressionTextureSample, -850, 400, texture=noise, sampler_type=_sampler_for(noise))
    _link(uv, '', g, 'UVs')
    _link(uv, '', d, 'UVs')
    _link(nuv, '', n, 'UVs')
    dov = _expr(mat, unreal.MaterialExpressionScalarParameter, -850, 650, parameter_name='DirtOverride',
                default_value=0.0)
    # Dirt only where the noise rises above DirtThreshold, ramped over 1/DirtContrast, so the field reads as grass
    # with dirt patches. The first build fed noise.R straight in and ~half the map was cracked dirt (seen live).
    thr = _expr(mat, unreal.MaterialExpressionScalarParameter, -850, 800, parameter_name='DirtThreshold',
                default_value=0.68)
    con = _expr(mat, unreal.MaterialExpressionScalarParameter, -850, 900, parameter_name='DirtContrast',
                default_value=6.0)
    sub = _expr(mat, unreal.MaterialExpressionSubtract, -700, 450)
    _link(n, 'R', sub, 'A')
    _link(thr, '', sub, 'B')
    mul = _expr(mat, unreal.MaterialExpressionMultiply, -620, 450)
    _link(sub, '', mul, 'A')
    _link(con, '', mul, 'B')
    sat = _expr(mat, unreal.MaterialExpressionSaturate, -560, 450)
    _link(mul, '', sat, '')
    mx = _expr(mat, unreal.MaterialExpressionMax, -480, 450)
    _link(sat, '', mx, 'A')
    _link(dov, '', mx, 'B')
    lerp = _expr(mat, unreal.MaterialExpressionLinearInterpolate, -300, 0)
    _link(g, 'RGB', lerp, 'A')
    _link(d, 'RGB', lerp, 'B')
    _link(mx, '', lerp, 'Alpha')
    rough = _expr(mat, unreal.MaterialExpressionConstant, -300, 300, r=0.9)
    if not MEL.connect_material_property(lerp, '', unreal.MaterialProperty.MP_BASE_COLOR):
        raise RuntimeError('connect lerp -> BaseColor failed')
    if not MEL.connect_material_property(rough, '', unreal.MaterialProperty.MP_ROUGHNESS):
        raise RuntimeError('connect 0.9 -> Roughness failed')
    errors = list(MEL.recompile_material(mat))
    if errors:
        raise RuntimeError('M_LT_Ground compile errors:\n  ' + '\n  '.join(errors))
    EAL.save_asset(GROUND_MAT, False)

    if EAL.does_asset_exist(PAD_MI):
        pad = C.load(PAD_MI)
    else:
        pad = at.create_asset('MI_LT_GroundPad', C.MAT_DIR, unreal.MaterialInstanceConstant,
                              unreal.MaterialInstanceConstantFactoryNew())
        if pad is None:
            raise RuntimeError(f'create_asset {PAD_MI} failed')
    MEL.set_material_instance_parent(pad, mat)
    # The MEL setters return False even on success in 5.8.3 (see lt_import._team_mi), so verify by read-back.
    for k, v in (('DirtOverride', 1.0), ('Tiling', PAD_TILING)):
        MEL.set_material_instance_scalar_parameter_value(pad, k, v)
    MEL.update_material_instance(pad)
    for k, v in (('DirtOverride', 1.0), ('Tiling', PAD_TILING)):
        got = MEL.get_material_instance_scalar_parameter_value(pad, k)
        if abs(got - v) > 1e-4:
            raise RuntimeError(f'{PAD_MI}: {k} read back {got} != {v}')
    EAL.save_asset(PAD_MI, False)
    return mat, pad


def _check_inputs(entries, report, facing):
    """Every roster asset imported cleanly with team MIs; facing covers every source."""
    bad = []
    for e in entries:
        a = report['assets'].get(e['asset_key'])
        if not a:
            bad.append(f'{e["asset_key"]}: not in roster_report')
        elif a.get('failures'):
            bad.append(f'{e["asset_key"]}: import failures {a["failures"]}')
        elif e['faction'] not in a.get('team_mi', {}):
            bad.append(f'{e["asset_key"]}: no team MI for {e["faction"]}')
        elif (a.get('glb') != e['glb'] or a.get('source') != e['source'] or a.get('rigged') != bool(e['rigged'])
              or abs(float(a.get('expected_height_cm', -1.0)) - float(e['src_height_m']) * 100.0) >= 0.01):
            bad.append(f'{e["asset_key"]}: roster changed since lt_import; re-import {e["asset_key"]}')
        if e['source'] not in facing['front_yaw'] and e['asset_key'] not in facing.get('overrides', {}):
            bad.append(f'{e["asset_key"]}: no facing for source {e["source"]}')
    for name in ('T_Grass', 'T_Dirt'):
        if name not in report.get('ground', {}):
            bad.append(f'ground texture {name} not imported')
    if bad:
        raise RuntimeError('lt_build inputs incomplete:\n  ' + '\n  '.join(bad))


def _pose(comp, anim, position):
    """Single-node animation at a start position (D4); return whether update_animation_in_editor was set."""
    comp.set_animation_mode(unreal.AnimationMode.ANIMATION_SINGLE_NODE)
    comp.override_animation_data(anim, True, True, position, 1.0)
    # bUpdateAnimationInEditor is transient and not settable as a property; the BlueprintCallable setter works.
    # lt_capture re-applies it after every map load (C.animate_in_editor).
    live = C.animate_in_editor(comp)
    d = comp.get_editor_property('animation_data')
    got = d.get_editor_property('anim_to_play')
    if got is None or got.get_path_name() != anim.get_path_name() \
            or abs(d.get_editor_property('saved_position') - position) > 1e-3:
        raise RuntimeError(f'{comp.get_owner().get_actor_label()}: animation_data did not take {anim.get_name()}@{position}')
    return live


def _place(p, report, facing):
    """Spawn one unit/building from a layout entry; return (actor, manifest record, live_anim flag)."""
    a = report['assets'][p['asset_key']]
    yaw0 = facing.get('overrides', {}).get(p['asset_key'], facing['front_yaw'].get(p['source']))
    s = p['scale']
    loc = (p['x_m'] * 100.0, p['y_m'] * 100.0, p['z_m'] * 100.0 - float(a['min_z_cm']) * s)
    actor = C.spawn_mesh(C.load(a['mesh']), loc, yaw=yaw0 + p['heading_deg'] + p['yaw_jitter_deg'], scale=s,
                         label=p['label'], folder=p['folder'])
    comp_cls = unreal.SkeletalMeshComponent if p['rigged'] else unreal.StaticMeshComponent
    comp = C.component(actor, comp_cls)
    mis = [C.load(m['path']) for m in a['team_mi'][p['faction']]]
    for i, mi in enumerate(mis):
        comp.set_material(i, mi)
    clip, pos, live = None, None, None
    if p['rigged']:
        clip = p['clip']
        length = float(a['anim_lengths'][clip])
        pos = p['phase'] * length
        live = _pose(comp, C.load(a['anims'][clip]), pos)
    rot = actor.get_actor_rotation()
    sc = actor.get_actor_scale3d()
    lo = actor.get_actor_location()
    rec = C.manifest_record(p['label'], a['mesh'], (lo.x, lo.y, lo.z), (rot.pitch, rot.yaw, rot.roll),
                            (sc.x, sc.y, sc.z), [C.obj_path(m) for m in comp.get_materials() if m is not None],
                            clip, pos)
    return actor, rec, live


def run(look, overrides=None, seed=C.SEED):
    """Build LT_<look> from scratch and write run/manifest_<look>.json; return counts and the manifest hash."""
    overrides, recipe = _resolve(look, overrides)
    entries = C.load_roster()
    report = C.read_json(C.ROSTER_REPORT, 'lt_import.run')
    facing = C.read_json(C.FACING_JSON, 'lt_facing.record')
    _check_inputs(entries, report, facing)
    clips_by_key = {k: sorted(v.get('anims', {})) for k, v in report['assets'].items()}
    placements = C.layout(seed, entries, clips_by_key)
    map_path = f'{C.MAP_DIR}/LT_{look}'
    t0 = time.time()

    yield from C.fresh_level(map_path)
    mat, pad = _ground_material(report)
    plane = C.load(C.PLANE)
    ground = C.spawn_mesh(plane, (0.0, 20000.0, 0.0), scale=(1200.0, 1200.0, 1.0), label='Ground', folder='Ground')
    C.component(ground, unreal.StaticMeshComponent).set_material(0, mat)
    for side, sx in (('Alpha', 1.0), ('Beta', -1.0)):
        padact = C.spawn_mesh(plane, (sx * C.BASE_CENTRE[0] * 100.0, C.BASE_CENTRE[1] * 100.0, 2.0),
                              scale=(40.0, 40.0, 1.0), label=f'{side}_BasePad', folder='Ground')
        C.component(padact, unreal.StaticMeshComponent).set_material(0, pad)
    yield

    records, live_flags = [], []
    for i, p in enumerate(placements):
        _, rec, live = _place(p, report, facing)
        records.append(rec)
        if live is not None:
            live_flags.append(live)
        if i % BATCH == BATCH - 1:
            yield

    bounds = {}
    for path in C.SCATTER_MESHES:
        bb = C.load(path).get_bounding_box()
        bounds[path] = (bb.max.z - bb.min.z, bb.min.z)
    scatter = C.scatter_layout(seed, bounds)
    meshes = {path: C.load(path) for path in C.SCATTER_MESHES}
    folders = {'tree': 'Scatter/Trees', 'undergrowth': 'Scatter/Undergrowth', 'boulder': 'Scatter/Boulders'}
    for i, s in enumerate(scatter):
        C.spawn_mesh(meshes[s['mesh']], s['loc_cm'], yaw=s['yaw'], scale=s['scale'], label=s['label'],
                     folder=folders[s['kind']])
        if i % BATCH == BATCH - 1:
            yield

    cams = []
    for label, spec in C.CAMS.items():
        cam = C.spawn_camera(label, spec)
        lo, rot = cam.get_actor_location(), cam.get_actor_rotation()
        fov = cam.get_editor_property('camera_component').get_editor_property('field_of_view')
        cams.append(C.camera_record(label, (lo.x, lo.y, lo.z), (rot.pitch, rot.yaw, rot.roll), fov))
    C.spawn_class(unreal.PlayerStart, C.PLAYER_START_CM, label='PlayerStart', folder='Cameras')
    C.spawn_sky(recipe)
    yield
    if not C.les().save_current_level():
        raise RuntimeError(f'save_current_level failed for {map_path}')
    yield

    h = C.manifest_hash(records, cams)
    scatter_hash = C.manifest_hash([{'label': r['label'], 'mesh': r['mesh'], 'loc': [round(v, 1) for v in r['loc_cm']],
                                     'yaw': round(r['yaw'], 2), 'scale': round(r['scale'], 4)} for r in scatter], [])
    others = {}
    for other in C.LOOKS:
        if other != look and os.path.isfile(_manifest_path(other)):
            others[other] = C.read_json(_manifest_path(other)).get('hash') == h
    # A rebuild keeps the retune history so the tuning budget cannot be reset by rebuilding.
    prev = C.read_json(_manifest_path(look)) if os.path.isfile(_manifest_path(look)) else {}
    manifest = {'look': look, 'map': map_path, 'seed': seed, 'hash': h, 'scatter_hash': scatter_hash,
                'recipe': recipe, 'overrides': overrides, 'retunes': prev.get('retunes', []), 'facing': facing,
                'counts': {'units': sum(1 for p in placements if p['kind'] == 'unit'),
                           'buildings': sum(1 for p in placements if p['kind'] == 'building'),
                           **{k: sum(1 for s in scatter if s['kind'] == k) for k in C.SCATTER_CAPS}},
                'live_anim_in_editor': {'set': sum(live_flags), 'of': len(live_flags)},
                'records': records, 'cameras': cams, 'matches_other_looks': others,
                'secs': round(time.time() - t0, 1), 't': time.time()}
    C.write_json(_manifest_path(look), manifest)
    return {k: manifest[k] for k in ('look', 'map', 'hash', 'scatter_hash', 'counts', 'live_anim_in_editor',
                                     'matches_other_looks', 'secs')}


def retune(look, overrides):
    """Re-apply the recipe with PLAN §8 tuning overrides to an existing LT_<look> and every built look derived
    from it (sky/post only) and save; raise once the look has used its RETUNE_BUDGET."""
    m = C.read_json(_manifest_path(look), 'lt_build.run')
    if len(m.get('retunes', [])) >= RETUNE_BUDGET:
        raise RuntimeError(f'{look}: tuning budget spent ({RETUNE_BUDGET} retunes, PLAN §8)')
    _resolve(look, overrides)  # validate before touching any map
    targets = [look] + [d for d, p in DERIVED.items() if p == look and os.path.isfile(_manifest_path(d))]
    recipes = {}
    for t in targets:  # parent first: a derived look reads the parent manifest written below
        ov, recipe = _resolve(t, overrides if t == look else None)
        map_path = f'{C.MAP_DIR}/LT_{t}'
        if not C.les().load_level(map_path):
            raise RuntimeError(f'load_level({map_path}) failed')
        yield from C.wait_frames(10)
        C.apply_recipe(C.find_sky(), recipe)
        if not C.les().save_current_level():
            raise RuntimeError(f'save_current_level failed for {map_path}')
        path = _manifest_path(t)
        mt = C.read_json(path, 'lt_build.run')
        mt.setdefault('retunes', []).append({'overrides': ov, 'from': look, 't': time.time()})
        mt.update(recipe=recipe, overrides=ov)
        C.write_json(path, mt)
        recipes[t] = recipe
    return {'look': look, 'recipes': recipes}


def check_manifests():
    """PLAN §7.6 CHECK: all three manifests exist and share one layout hash (and one scatter hash)."""
    ms = {look: C.read_json(_manifest_path(look), f'lt_build.run({look!r})') for look in C.LOOKS}
    hashes = {look: (m['hash'], m['scatter_hash']) for look, m in ms.items()}
    if len(set(hashes.values())) != 1:
        raise RuntimeError(f'manifest hashes differ: {hashes}')
    for d, p in DERIVED.items():
        want = sorted(k for k in C.RECIPES[d] if C.RECIPES[d][k] != C.RECIPES[p][k])
        rd, rp = ms[d]['recipe'], ms[p]['recipe']
        got = sorted(k for k in set(rd) | set(rp) if rd.get(k) != rp.get(k))
        if got != want:
            raise RuntimeError(f'{d} recipe must differ from {p} only in {want}; differs in {got} (retune {p})')
    return {'hash': ms['A']['hash'], 'scatter_hash': ms['A']['scatter_hash'], 'looks': list(ms)}
