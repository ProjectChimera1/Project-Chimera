"""Facing check map and record (PLAN.md §6.5, PLAN_DELTA.md D5).

run()    builds /Game/LookTest/Maps/LT_Facing: one model per source kind at yaw 0 in a row
         along +Y, each with a Cone pointing +X in front of it, plus top and front cameras.
         Claude captures it with MCP CaptureViewport and reads off each model's front.
record() writes run/facing.json. `front_yaw[source]` is the actor yaw (degrees) at which a
         model of that source faces world +X; `overrides[asset_key]` beats the source value.
         Expected from the glTF axis mapping (+Z -> UE +Y): -90.
"""
import time

import lt_common as C

MAP = C.MAP_DIR + '/LT_Facing'
# (asset_key, y metres, cone x metres): rigged Tripo unit, static Tripo unit, Tripo building, Hunyuan unit.
SUBJECTS = [('covenant_transmuter', 0.0, 3.0), ('pierce_marksman', 6.0, 3.0),
            ('crucible_mortar', 14.0, 4.0), ('covenant_sanctum', 26.0, 7.0)]
CAMS = {
    # Top-down: yaw 0 puts world +X at the top of the image.
    'CAM_FacingTop': {'loc_cm': (0.0, 1300.0, 6000.0), 'pitch': -90.0, 'yaw': 0.0, 'roll': 0.0,
                      'vfov': 40.0, 'hfov': C.hfov_from_vfov(40.0)},
    # From +X looking back at the row: a model facing +X shows its front here.
    'CAM_FacingFront': {'loc_cm': (3500.0, 1300.0, 300.0), 'pitch': -3.0, 'yaw': 180.0, 'roll': 0.0,
                        'vfov': 45.0, 'hfov': C.hfov_from_vfov(45.0)},
}


def run():
    """Build LT_Facing from the imported roster; return subject positions and camera transforms for MCP."""
    report = C.read_json(C.ROSTER_REPORT, 'lt_import.run')
    entries = C.load_roster()
    yield from C.fresh_level(MAP)
    C.spawn_sky(C.resolve_recipe('B'))  # no clouds; clear, high sun
    C.spawn_mesh(C.load(C.PLANE), (0.0, 1300.0, 0.0), scale=(100.0, 100.0, 1.0), label='Floor')
    cone = C.load(C.CONE)
    placed = []
    for key, y, cone_x in SUBJECTS:
        a = report['assets'].get(key)
        if not a or 'mesh' not in a:
            raise RuntimeError(f'{key} not imported (run lt_import first)')
        e = next(e for e in entries if e['asset_key'] == key)
        s = float(e['scale'])
        z = -float(a['min_z_cm']) * s
        C.spawn_mesh(C.load(a['mesh']), (0.0, y * 100.0, z), yaw=0.0, scale=s, label=f'Face_{key}')
        # BasicShapes Cone points +Z; pitch -90 turns its tip to +X.
        C.spawn_mesh(cone, (cone_x * 100.0, y * 100.0, 100.0), pitch=-90.0, scale=0.6, label=f'Arrow_{key}')
        placed.append({'asset_key': key, 'source': a['source'], 'y_m': y, 'scale': s})
        yield
    for label, spec in CAMS.items():
        C.spawn_camera(label, spec)
    if not C.les().save_current_level():
        raise RuntimeError('save_current_level failed')
    yield
    cams = {label: {'location': list(spec['loc_cm']),
                    'rotation': {'pitch': spec['pitch'], 'yaw': spec['yaw'], 'roll': spec['roll']},
                    'hfov': spec['hfov']} for label, spec in CAMS.items()}
    return {'map': MAP, 'subjects': placed, 'cameras': cams,
            'note': 'cones point +X; top view has +X up; record() the yaw that turns each front to +X'}


def record(front_yaw, overrides=None, note=''):
    """Write run/facing.json after checking every roster source (and override key) is covered."""
    entries = C.load_roster()
    sources = sorted({e['source'] for e in entries})
    missing = [s for s in sources if s not in front_yaw]
    if missing:
        raise ValueError(f'front_yaw missing sources {missing} (roster uses {sources})')
    keys = set(C.asset_keys(entries))
    bad = [k for k in (overrides or {}) if k not in keys]
    if bad:
        raise ValueError(f'overrides name unknown asset_keys {bad}')
    data = {'front_yaw': {k: float(v) for k, v in front_yaw.items()},
            'overrides': {k: float(v) for k, v in (overrides or {}).items()},
            'note': note, 't': time.time()}
    C.write_json(C.FACING_JSON, data)
    return data
