"""Shared helpers and data for the look-test job modules (PLAN.md §4.1, PLAN_DELTA.md D2-D5).

Pure-Python parts (roster loader, tint maths, layout, scatter, clip choice,
cameras, recipes, manifest hashing) never touch `unreal` at call time, so they
run on the host against a stub module. Editor helpers further down call
`unreal` only when invoked from a job inside the editor.
"""
import datetime
import hashlib
import json
import math
import os
import random
import re
import struct
import time

import unreal

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
RUN = LT + '/run'
OUT = LT + '/out'
ROSTER_JSON = RUN + '/roster.json'
ROSTER_REPORT = RUN + '/roster_report.json'
FACING_JSON = RUN + '/facing.json'
PROBE_JSON = RUN + '/probe.json'

SEED = 20260930
LOOKS = ('A', 'A_noLumen', 'B')

# Content paths (UE). Every one of these is checked by lt_probe.
ROSTER_ROOT = '/Game/LookTest/Roster'
GROUND_DIR = '/Game/LookTest/Ground'
MAT_DIR = '/Game/LookTest/Materials'
MAP_DIR = '/Game/LookTest/Maps'
ENTRY_MAP = '/Engine/Maps/Entry'
PLANE = '/Engine/BasicShapes/Plane'
CONE = '/Engine/BasicShapes/Cone'
NOISE_TEX = '/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT'
CLOUD_MAT = '/Engine/EngineSky/VolumetricClouds/m_SimpleVolumetricCloud_Inst'
M_GLTF = '/InterchangeAssets/gltf/Substrate/M_GLTF'
TREES = ['/PCGBiomeSample/Meshes/PCG_Tree_01', '/PCGBiomeSample/Meshes/PCG_Tree_02',
         '/PCGBiomeSample/Meshes/PCG_Spruce_01', '/PCGBiomeSample/Meshes/PCG_Pine_01']
SAPLINGS = ['/PCGBiomeSample/Meshes/PCG_Sapling_01', '/PCGBiomeSample/Meshes/PCG_Sapling_02']
SEEDLINGS = ['/PCGBiomeSample/Meshes/PCG_Seedling_01', '/PCGBiomeSample/Meshes/PCG_Seedling_02']
BOULDERS = ['/PCGBiomeSample/Meshes/PCG_Boulder_01']
SCATTER_MESHES = TREES + SAPLINGS + SEEDLINGS + BOULDERS
ENGINE_CONTENT = [ENTRY_MAP, PLANE, CONE, NOISE_TEX, CLOUD_MAT, M_GLTF] + SCATTER_MESHES

# Host files.
GRASS_PNG = 'D:/Projects/Project_Chimera/godot/assets/textures/terrain/grass.png'
DIRT_PNG = 'D:/Projects/Project_Chimera/godot/assets/textures/terrain/dirt.png'


# ---------------------------------------------------------------- waiting / files

def wait_frames(n):
    """Generator: yield n times (one editor tick each under the bridge)."""
    for _ in range(int(n)):
        yield


def wait_seconds(s):
    """Generator: yield until s wall-clock seconds have passed."""
    end = time.time() + float(s)
    while time.time() < end:
        yield


def png_size(path):
    """Return (width, height) from a PNG's IHDR chunk; raise if the file is not a PNG."""
    with open(path, 'rb') as f:
        head = f.read(24)
    if len(head) < 24 or head[:8] != b'\x89PNG\r\n\x1a\n' or head[12:16] != b'IHDR':
        raise ValueError(f'not a PNG: {path}')
    return struct.unpack('>II', head[16:24])


def read_json(path, what=None):
    """Load a JSON file, raising a clear error naming the step that should have written it."""
    if not os.path.isfile(path):
        raise FileNotFoundError(f'{path} missing' + (f' (written by {what})' if what else ''))
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def write_json(path, obj):
    """Write JSON atomically (temp file + rename)."""
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(obj, f, indent=1, sort_keys=False)
    os.replace(tmp, path)


def now_iso():
    """Local time, seconds precision, for report headers."""
    return datetime.datetime.now().isoformat(timespec='seconds')


# ---------------------------------------------------------------- roster (PLAN_DELTA D2)

ROSTER_KEYS = ('faction', 'kind', 'id', 'role', 'name', 'source', 'glb', 'asset_key', 'rigged',
               'anims', 'mesh_scale', 'godot_height_m', 'src_height_m', 'scale')
SOURCES = ('godot_hunyuan', 'tripo_rigged', 'tripo_static')


def load_roster(path=ROSTER_JSON, check_files=True):
    """Read run/roster.json (written by prep_roster.py) and validate every entry; return the entry list."""
    data = read_json(path, 'tools/prep_roster.py')
    entries = data.get('entries')
    if not entries:
        raise ValueError(f'{path}: no entries')
    bad = []
    for i, e in enumerate(entries):
        missing = [k for k in ROSTER_KEYS if k not in e]
        if missing:
            bad.append(f'entry {i} ({e.get("faction")}/{e.get("id")}): missing {missing}')
            continue
        if e['faction'] not in ('alpha', 'beta') or e['kind'] not in ('unit', 'building'):
            bad.append(f'{e["faction"]}/{e["id"]}: bad faction/kind')
        if e['source'] not in SOURCES:
            bad.append(f'{e["faction"]}/{e["id"]}: unknown source {e["source"]}')
        if not 0.2 <= float(e['scale']) <= 20:
            bad.append(f'{e["faction"]}/{e["id"]}: scale {e["scale"]} outside 0.2-20')
        if bool(e['rigged']) != (e['source'] == 'tripo_rigged'):
            bad.append(f'{e["faction"]}/{e["id"]}: rigged={e["rigged"]} but source={e["source"]}')
        if e['rigged'] and not e['anims']:
            bad.append(f'{e["faction"]}/{e["id"]}: rigged with no anims')
        if check_files and not os.path.isfile(e['glb']):
            bad.append(f'{e["faction"]}/{e["id"]}: glb missing {e["glb"]}')
    # Entries sharing an asset_key must share the file (import once, place twice).
    by_key = {}
    for e in entries:
        if 'asset_key' in e:
            by_key.setdefault(e['asset_key'], set()).add((e.get('glb'), e.get('rigged'), e.get('source'), e.get('kind')))
    for k, v in by_key.items():
        if len(v) > 1:
            bad.append(f'asset_key {k}: entries disagree on glb/rigged/source/kind {sorted(map(str, v))}')
    if bad:
        raise ValueError(f'{path} invalid:\n  ' + '\n  '.join(bad))
    return entries


def roster_entry(entries, faction, id_):
    """Return the one entry for faction+id, or raise."""
    hits = [e for e in entries if e['faction'] == faction and e['id'] == id_]
    if len(hits) != 1:
        raise KeyError(f'roster has {len(hits)} entries for {faction}/{id_}')
    return hits[0]


def asset_keys(entries):
    """Distinct asset_keys in roster order."""
    seen = []
    for e in entries:
        if e['asset_key'] not in seen:
            seen.append(e['asset_key'])
    return seen


def sanitize(name):
    """Lower-case, non-alphanumerics to '_' (how clip names are compared to imported asset names)."""
    return re.sub(r'[^a-z0-9]+', '_', str(name).lower()).strip('_')


def clip_base(clip):
    """'preset:biped:slash' -> 'slash'."""
    return str(clip).split(':')[-1]


# ---------------------------------------------------------------- team tint (PLAN §6.4)

TEAM_COLORS = {'alpha': (0.2, 0.5, 1.0), 'beta': (1.0, 0.3, 0.2)}   # TeamColorPalette.cs:39-40, sRGB
TINT_STRENGTH = 0.35                                               # TeamTintMaterial.cs:56
UNIT_VALUE = 1.0                                                   # :63
BUILDING_VALUE = 0.88                                              # :66


def srgb_to_linear(c):
    """IEC 61966-2-1 sRGB decode (Godot `source_color`)."""
    c = float(c)
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def team_factor(faction, kind):
    """BaseColorFactor multiplier reproducing Godot's mix(art, art*team, 0.35) * value (PLAN §6.4)."""
    value = BUILDING_VALUE if kind == 'building' else UNIT_VALUE
    return tuple((1.0 - TINT_STRENGTH + TINT_STRENGTH * srgb_to_linear(c)) * value for c in TEAM_COLORS[faction])


# ---------------------------------------------------------------- clip choice (PLAN_DELTA D4)

CLIP_PREFS = {
    'worker': ['chop', 'dig', 'standing_relax', 'idle'],
    'melee': ['slash', 'run', 'walk', 'idle'],
    'heavy': ['slash', 'walk', 'idle'],
    'ranged': ['fire', 'shoot', 'cast_a_spell', 'idle'],
    'mage': ['cast_a_spell', 'fire', 'idle'],
    'scout': ['run', 'walk', 'idle'],
    'air': ['idle', 'standing_relax'],
}


def choose_clip(clips, clip_role):
    """First preferred clip present (matched on the name after the last ':'); return the original clip name or None."""
    for pref in CLIP_PREFS[clip_role]:
        for c in clips:
            if clip_base(c) == pref:
                return c
    return None


# ---------------------------------------------------------------- layout (PLAN §7.2, D4)

# Layout slot per faction unit id; the clip role used for each (D4). Beta's bulwark fills the
# alpha scout's flank slot but is a melee bulwark, so it uses the heavy/flank clip list.
UNIT_SLOTS = {
    'alpha': {'worker': ('worker', 'worker'), 'infantry': ('infantry', 'melee'),
              'heavy_infantry': ('heavy', 'heavy'), 'archer': ('archer', 'ranged'),
              'mage': ('mage', 'mage'), 'siege_engine': ('siege', None),
              'scout': ('flank', 'scout'), 'griffin': ('air', 'air')},
    'beta': {'forgehand': ('worker', 'worker'), 'footsoldier': ('infantry', 'melee'),
             'ironclad': ('heavy', 'heavy'), 'crossbowman': ('archer', 'ranged'),
             'rune_caster': ('mage', 'mage'), 'war_machine': ('siege', None),
             'bulwark': ('flank', 'heavy'), 'wyvern': ('air', 'air')},
}
SLOT_ORDER = ('worker', 'infantry', 'heavy', 'archer', 'mage', 'siege', 'flank', 'air')

# Alpha-side positions in metres (x, y[, z]); beta mirrors x -> -x except the infantry front rank.
GROVE = (-78.0, 46.0)
WORKER_CENTRE = (-70.0, 40.0)
WORKER_OFFSETS = [(-3.0, -2.0), (2.0, -3.0), (-1.5, 3.0), (3.0, 2.0)]
BASE_CENTRE = (-56.0, 14.0)
BUILDINGS_ALPHA = [('command_center', -58.0, 14.0), ('barracks', -46.0, 26.0), ('archery_range', -46.0, 2.0),
                   ('siege_workshop', -62.0, 32.0), ('aviary', -66.0, -4.0)]


def _lin(a, b, n):
    return [a + (b - a) * i / (n - 1) for i in range(n)] if n > 1 else [a]


def _slot_positions(slot, faction):
    """Un-jittered (x, y, z) metres for every unit in a slot, alpha frame then mirrored for beta."""
    sx = -1.0 if faction == 'beta' else 1.0
    if slot == 'worker':
        return [(sx * (WORKER_CENTRE[0] + dx), WORKER_CENTRE[1] + dy, 0.0) for dx, dy in WORKER_OFFSETS]
    if slot == 'infantry':
        front, back = (-1.6, -3.0) if faction == 'alpha' else (1.4, 3.0)
        return [(x, y, 0.0) for x in (front, back) for y in _lin(0, 12, 4)]
    if slot == 'heavy':
        return [(sx * -4.5, y, 0.0) for y in _lin(-2, 14, 4)]
    if slot == 'archer':
        return [(sx * -11.0, y, 0.0) for y in _lin(1, 11, 6)]
    if slot == 'mage':
        return [(sx * -14.0, y, 0.0) for y in (4.0, 8.0)]
    if slot == 'siege':
        return [(sx * -20.0, 6.0, 0.0)]
    if slot == 'flank':
        return [(sx * -8.0, y, 0.0) for y in (-6.0, 18.0)]
    if slot == 'air':
        return [(sx * -2.0, 3.0, 7.0), (sx * -5.0, 10.0, 8.0)]
    raise KeyError(slot)


def layout(seed=SEED, entries=None, clips_by_key=None):
    """Deterministic placement list for units and buildings (metres, degrees).

    `heading_deg` is the world direction the model's front should face (0 = +X);
    lt_build adds the per-source facing correction from run/facing.json.
    `clips_by_key` ({asset_key: [clip names with an imported anim]}) overrides the
    roster's clip lists so the choice only picks clips that really imported.
    """
    entries = entries if entries is not None else load_roster()
    rng = random.Random(seed)
    out = []
    for faction in ('alpha', 'beta'):
        sx = -1.0 if faction == 'beta' else 1.0
        cap = faction.capitalize()
        by_slot = {slot: (uid, role) for uid, (slot, role) in UNIT_SLOTS[faction].items()}
        for slot in SLOT_ORDER:
            uid, clip_role = by_slot[slot]
            e = roster_entry(entries, faction, uid)
            for i, (x, y, z) in enumerate(_slot_positions(slot, faction)):
                # Draw every random number for every unit so the sequence never depends on rig status.
                jx, jy = rng.uniform(-0.35, 0.35), rng.uniform(-0.35, 0.35)
                jyaw = rng.uniform(-15.0, 15.0)
                phase = rng.random()
                if slot == 'worker':
                    heading = math.degrees(math.atan2(GROVE[1] - y, sx * GROVE[0] - x))
                else:
                    heading = 0.0 if faction == 'alpha' else 180.0
                clip = None
                if e['rigged']:
                    pool = clips_by_key.get(e['asset_key'], []) if clips_by_key is not None else e['anims']
                    clip = choose_clip(pool, clip_role)
                    if clip is None:
                        raise ValueError(f'{faction}/{uid}: no clip from {CLIP_PREFS[clip_role]} in {pool}')
                out.append({
                    'label': f'{cap}_{slot}_{i:02d}', 'folder': f'{cap}/Units', 'faction': faction,
                    'kind': 'unit', 'id': uid, 'slot': slot, 'asset_key': e['asset_key'], 'source': e['source'],
                    'rigged': bool(e['rigged']), 'scale': float(e['scale']),
                    'x_m': x + jx, 'y_m': y + jy, 'z_m': z, 'heading_deg': heading, 'yaw_jitter_deg': jyaw,
                    'clip': clip, 'clip_role': clip_role, 'phase': phase,
                })
        for bid, x, y in BUILDINGS_ALPHA:
            e = roster_entry(entries, faction, bid)
            out.append({
                'label': f'{cap}_{bid}', 'folder': f'{cap}/Buildings', 'faction': faction, 'kind': 'building',
                'id': bid, 'slot': 'building', 'asset_key': e['asset_key'], 'source': e['source'],
                'rigged': False, 'scale': float(e['scale']), 'x_m': sx * x, 'y_m': y, 'z_m': 0.0,
                'heading_deg': 0.0 if faction == 'alpha' else 180.0, 'yaw_jitter_deg': 0.0,
                'clip': None, 'clip_role': None, 'phase': 0.0,
            })
    return out


# ---------------------------------------------------------------- scatter (PLAN §7.3)

SCATTER_CAPS = {'tree': 450, 'undergrowth': 400, 'boulder': 40}
# Target heights in metres. Trees are the plan's 8-16 m; the rest are this module's choice.
SCATTER_HEIGHTS = {'tree': (8.0, 16.0), 'sapling': (1.5, 3.0), 'seedling': (0.3, 0.8), 'boulder': (0.8, 2.5)}
GROVE_TREES = 12   # per side


def _excluded(x, y):
    """True inside the battle box, a base, or a worker patch."""
    if -30.0 <= x <= 30.0 and -15.0 <= y <= 30.0:
        return True
    for s in (1.0, -1.0):
        if math.hypot(x - s * BASE_CENTRE[0], y - BASE_CENTRE[1]) < 30.0:
            return True
        if math.hypot(x - s * WORKER_CENTRE[0], y - WORKER_CENTRE[1]) < 6.0:
            return True
    return False


def _region_point(rng, region):
    """One candidate point: forest band (y 40-300) or a side clump (|x| 90-350, y -100..40)."""
    if region == 'band':
        return rng.uniform(-350.0, 350.0), rng.uniform(40.0, 300.0)
    return rng.choice((-1.0, 1.0)) * rng.uniform(90.0, 350.0), rng.uniform(-100.0, 40.0)


def scatter_layout(seed, mesh_bounds):
    """Deterministic scatter list; mesh_bounds = {path: (height_cm, min_z_cm)} read from the assets.

    Returns dicts with label, mesh, loc_cm (x, y, z), yaw, scale; z puts each mesh's base on z=0.
    """
    rng = random.Random(seed + 1)
    out = []

    def add(kind, cat, meshes, x, y):
        mesh = rng.choice(meshes)
        h_cm, minz_cm = mesh_bounds[mesh]
        if h_cm <= 0:
            raise ValueError(f'{mesh}: non-positive bounds height {h_cm}')
        lo, hi = SCATTER_HEIGHTS[cat]
        s = rng.uniform(lo, hi) * 100.0 / h_cm
        n = sum(1 for o in out if o['kind'] == kind)
        out.append({'label': f'{kind.capitalize()}_{n:04d}', 'kind': kind, 'mesh': mesh,
                    'loc_cm': (x * 100.0, y * 100.0, -minz_cm * s), 'yaw': rng.uniform(0.0, 360.0), 'scale': s})

    def fill(kind, cat, meshes, count, regions):
        placed, tries = 0, 0
        while placed < count and tries < count * 50:
            tries += 1
            x, y = _region_point(rng, rng.choice(regions))
            if _excluded(x, y):
                continue
            add(kind, cat, meshes, x, y)
            placed += 1

    # Groves next to the worker patches.
    for s in (1.0, -1.0):
        for _ in range(GROVE_TREES):
            a, r = rng.uniform(0, 2 * math.pi), rng.uniform(2.0, 10.0)
            x, y = s * GROVE[0] + r * math.cos(a), GROVE[1] + r * math.sin(a)
            if math.hypot(x - s * WORKER_CENTRE[0], y - WORKER_CENTRE[1]) < 6.0:
                continue
            add('tree', 'tree', TREES, x, y)
    n_trees = sum(1 for o in out if o['kind'] == 'tree')
    fill('tree', 'tree', TREES, int((SCATTER_CAPS['tree'] - n_trees) * 0.7), ['band'])
    n_trees = sum(1 for o in out if o['kind'] == 'tree')
    fill('tree', 'tree', TREES, SCATTER_CAPS['tree'] - n_trees, ['clump'])
    half = SCATTER_CAPS['undergrowth'] // 2
    fill('undergrowth', 'sapling', SAPLINGS, half, ['band', 'clump'])
    fill('undergrowth', 'seedling', SEEDLINGS, SCATTER_CAPS['undergrowth'] - half, ['band', 'clump'])
    fill('boulder', 'boulder', BOULDERS, SCATTER_CAPS['boulder'], ['band', 'clump'])
    for kind, cap in SCATTER_CAPS.items():
        n = sum(1 for o in out if o['kind'] == kind)
        if n > cap:
            raise AssertionError(f'scatter {kind}: {n} > cap {cap}')
    return out


# ---------------------------------------------------------------- cameras (PLAN §0.6)

ASPECT = 16.0 / 9.0


def hfov_from_vfov(vfov_deg, aspect=ASPECT):
    """Horizontal FOV for a vertical FOV at the given aspect (UE CameraComponent FOV is horizontal)."""
    return math.degrees(2.0 * math.atan(math.tan(math.radians(vfov_deg) / 2.0) * aspect))


def _orbit_cam(pivot_m, dist_m, pitch_deg):
    """Camera location (cm) behind a pivot along -Y, `pitch_deg` above the ground plane."""
    p = math.radians(pitch_deg)
    return (pivot_m[0] * 100.0, (pivot_m[1] - dist_m * math.cos(p)) * 100.0, (pivot_m[2] + dist_m * math.sin(p)) * 100.0)


CAMS = {
    # RtsCameraController.cs:45-46 (pitch 50, distance 80); Godot default vertical FOV 75.
    'CAM_Gameplay': {'loc_cm': _orbit_cam((0.0, 0.0, 0.0), 80.0, 50.0), 'pitch': -50.0, 'yaw': 90.0, 'roll': 0.0,
                     'vfov': 75.0, 'hfov': hfov_from_vfov(75.0), 'auto_activate': True},
    'CAM_Close': {'loc_cm': _orbit_cam((-2.0, 2.0, 0.0), 30.0, 40.0), 'pitch': -40.0, 'yaw': 90.0, 'roll': 0.0,
                  'vfov': 40.0, 'hfov': hfov_from_vfov(40.0), 'auto_activate': False},
}
PLAYER_START_CM = (-30000.0, -30000.0, 100.0)


# ---------------------------------------------------------------- recipes (PLAN §8)

_A = {
    # Yaw 120, not PLAN §8's 60: CAM yaw 90 puts world -X on screen right, and the light sits at -forward
    # (DirectionalLightComponent::GetLightPosition), so 120 gives the PLAN's intent: lit from camera-left-behind,
    # shadows falling up-right in frame. Yaw 60 would light from camera-right-behind.
    'sun_pitch': -32.0, 'sun_yaw': 120.0, 'sun_lux': 10.0, 'sun_source_angle': 2.0, 'sun_temperature': 5600.0,
    'sun_use_temperature': True, 'sun_atmosphere_sun_light': True, 'sun_cast_cloud_shadows': True,
    'skylight_real_time_capture': True, 'skylight_intensity': 1.0,
    'clouds': True,
    'fog_density': 0.02, 'fog_height_falloff': 0.2, 'fog_volumetric': False, 'fog_inscattering': None,
    'gi_method': 'LUMEN', 'reflection_method': 'LUMEN',
    'saturation': 0.95, 'contrast': 1.05, 'bloom': 0.3, 'vignette': 0.4, 'ao': 0.5, 'motion_blur': 0.0,
    'exposure_bias': 0.0,
}
RECIPES = {
    'A': dict(_A),
    'A_noLumen': dict(_A, gi_method='SCREEN_SPACE', reflection_method='SCREEN_SPACE'),
    'B': dict(_A, sun_pitch=-55.0, sun_source_angle=4.5, sun_temperature=6000.0, sun_cast_cloud_shadows=None,
              skylight_intensity=1.6, clouds=False, fog_density=0.004, fog_inscattering=(0.45, 0.6, 0.85),
              gi_method='NONE', reflection_method='SCREEN_SPACE', saturation=1.30, contrast=1.10,
              bloom=0.15, vignette=0.2, ao=0.3),
}

# Tuning knobs allowed by PLAN §8: (kind, limit) relative to the base recipe.
TUNING_KNOBS = {'sun_pitch': ('add', 10.0), 'exposure_bias': ('add', 1.0),
                'fog_density': ('mul', 2.0), 'saturation': ('add', 0.1)}


def resolve_recipe(look, overrides=None):
    """Recipe for a look with PLAN §8 tuning overrides applied; raise on any knob outside its budget."""
    if look not in RECIPES:
        raise KeyError(f'unknown look {look!r}; expected one of {LOOKS}')
    r = dict(RECIPES[look])
    for k, v in (overrides or {}).items():
        if k not in TUNING_KNOBS:
            raise ValueError(f'{k} is not a tuning knob (allowed: {sorted(TUNING_KNOBS)})')
        kind, lim = TUNING_KNOBS[k]
        base = RECIPES[look][k]
        ok = abs(v - base) <= lim + 1e-9 if kind == 'add' else base / lim - 1e-12 <= v <= base * lim + 1e-12
        if not ok:
            raise ValueError(f'{look}.{k}={v} outside budget ({kind} {lim} around {base})')
        r[k] = float(v)
    return r


# ---------------------------------------------------------------- manifest (PLAN §7.6)

def manifest_record(label, mesh, loc_cm, rot_pyr, scale, materials, clip=None, saved_position=None):
    """Canonical, rounded record for one placed unit/building (what the manifest hash covers)."""
    return {
        'label': label, 'mesh': mesh,
        'loc': [round(float(v), 1) for v in loc_cm],
        'rot': [round(float(v), 2) for v in rot_pyr],
        'scale': [round(float(v), 4) for v in scale],
        'materials': list(materials), 'clip': clip,
        'saved_position': None if saved_position is None else round(float(saved_position), 3),
    }


def camera_record(label, loc_cm, rot_pyr, hfov):
    """Canonical camera record for the manifest."""
    return {'label': label, 'loc': [round(float(v), 1) for v in loc_cm],
            'rot': [round(float(v), 2) for v in rot_pyr], 'hfov': round(float(hfov), 3)}


def manifest_hash(records, cameras):
    """sha256 over the label-sorted records plus the label-sorted cameras (canonical JSON)."""
    payload = {'records': sorted(records, key=lambda r: r['label']),
               'cameras': sorted(cameras, key=lambda r: r['label'])}
    blob = json.dumps(payload, sort_keys=True, separators=(',', ':'))
    return hashlib.sha256(blob.encode('utf-8')).hexdigest()


# ---------------------------------------------------------------- editor helpers (call only inside UE)

def eas():
    return unreal.get_editor_subsystem(unreal.EditorActorSubsystem)


def les():
    return unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)


def editor_world():
    return unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world()


def load(path):
    """Load an asset or raise naming the path."""
    obj = unreal.EditorAssetLibrary.load_asset(path)
    if obj is None:
        raise RuntimeError(f'could not load {path}')
    return obj


def obj_path(obj):
    """Package path without the '.Name' object suffix."""
    return obj.get_path_name().split('.')[0]


def enum_name(v):
    """Readable name for an unreal enum value."""
    return getattr(v, 'name', str(v))


def set_props(obj, props):
    """set_editor_property for each item; raise naming the object and property on failure."""
    for k, v in props.items():
        try:
            obj.set_editor_property(k, v)
        except Exception as e:
            who = obj.get_name() if hasattr(obj, 'get_name') else type(obj).__name__
            raise RuntimeError(f'{who}.{k} = {v!r}: {e}') from e


def find_actor(label):
    """The one level actor with this label, or raise."""
    hits = [a for a in eas().get_all_level_actors() if a.get_actor_label() == label]
    if len(hits) != 1:
        raise RuntimeError(f'{len(hits)} actors labelled {label!r} in the current level')
    return hits[0]


def fresh_level(map_path):
    """Generator: switch to Entry, delete map_path if it exists, create it empty (non-partitioned)."""
    # Deleting the map then calling new_level races the asset registry ("An asset already exists at this
    # location", seen live 2026-10-01), so an existing map is reopened and emptied instead.
    if unreal.EditorAssetLibrary.does_asset_exist(map_path):
        if not les().load_level(map_path):
            raise RuntimeError(f'load_level({map_path}) failed')
        yield from wait_frames(5)
        actors = [a for a in eas().get_all_level_actors() if not isinstance(a, unreal.WorldSettings)]
        eas().destroy_actors(actors)
        yield from wait_frames(5)
        left = [a.get_name() for a in eas().get_all_level_actors()
                if not isinstance(a, (unreal.WorldSettings, unreal.Brush))]
        if left:
            raise RuntimeError(f'{map_path}: could not clear actors {left[:10]}')
    else:
        if not les().load_level(ENTRY_MAP):
            raise RuntimeError(f'load_level({ENTRY_MAP}) failed')
        yield from wait_frames(5)
        if not les().new_level(map_path, False):
            raise RuntimeError(f'new_level({map_path}) failed')
        yield from wait_frames(5)


def spawn_mesh(mesh, loc_cm, yaw=0.0, scale=1.0, label=None, folder=None, pitch=0.0, roll=0.0):
    """Spawn a Static/SkeletalMeshActor from a mesh asset with uniform or (x, y, z) scale; return the actor."""
    rot = unreal.Rotator(roll=roll, pitch=pitch, yaw=yaw)
    actor = eas().spawn_actor_from_object(mesh, unreal.Vector(*loc_cm), rot)
    if actor is None:
        raise RuntimeError(f'spawn_actor_from_object({mesh.get_name()}) returned None')
    s = scale if isinstance(scale, (tuple, list)) else (scale, scale, scale)
    actor.set_actor_scale3d(unreal.Vector(*s))
    if label:
        actor.set_actor_label(label)
    if folder:
        actor.set_folder_path(folder)
    return actor


def spawn_class(cls, loc_cm, pitch=0.0, yaw=0.0, roll=0.0, label=None, folder=None):
    """Spawn an actor of a class; return it."""
    actor = eas().spawn_actor_from_class(cls, unreal.Vector(*loc_cm), unreal.Rotator(roll=roll, pitch=pitch, yaw=yaw))
    if actor is None:
        raise RuntimeError(f'spawn_actor_from_class({cls}) returned None')
    if label:
        actor.set_actor_label(label)
    if folder:
        actor.set_folder_path(folder)
    return actor


def component(actor, cls):
    """The actor's first component of a class, or raise."""
    c = actor.get_component_by_class(cls)
    if c is None:
        raise RuntimeError(f'{actor.get_actor_label()} has no {cls.__name__}')
    return c


SKY_LABELS = {'sun': 'LT_Sun', 'atmosphere': 'LT_SkyAtmosphere', 'skylight': 'LT_SkyLight',
              'fog': 'LT_HeightFog', 'clouds': 'LT_VolumetricCloud', 'post': 'LT_PostProcess'}


def spawn_sky(recipe):
    """Spawn the outdoor-lighting actors for a recipe (clouds only if the recipe has them); return {role: actor}."""
    classes = {'sun': unreal.DirectionalLight, 'atmosphere': unreal.SkyAtmosphere, 'skylight': unreal.SkyLight,
               'fog': unreal.ExponentialHeightFog, 'post': unreal.PostProcessVolume}
    if recipe['clouds']:
        classes['clouds'] = unreal.VolumetricCloud
    actors = {role: spawn_class(cls, (0.0, 0.0, 0.0), label=SKY_LABELS[role], folder='Lighting')
              for role, cls in classes.items()}
    apply_recipe(actors, recipe)
    return actors


def find_sky():
    """Sky actors of the current level by label ({role: actor}); clouds may be absent."""
    found = {}
    for a in eas().get_all_level_actors():
        for role, label in SKY_LABELS.items():
            if a.get_actor_label() == label:
                found[role] = a
    for role in ('sun', 'atmosphere', 'skylight', 'fog', 'post'):
        if role not in found:
            raise RuntimeError(f'no {SKY_LABELS[role]} in the current level')
    return found


def apply_recipe(actors, r):
    """Set lights, fog, clouds and post-process from a recipe, on components (Epic default_outdoor_lighting rule)."""
    movable = unreal.ComponentMobility.MOVABLE
    sun = actors['sun']
    sun.set_actor_rotation(unreal.Rotator(roll=0.0, pitch=r['sun_pitch'], yaw=r['sun_yaw']), False)
    dl = component(sun, unreal.DirectionalLightComponent)
    dl.set_mobility(movable)
    props = {'intensity': r['sun_lux'], 'light_source_angle': r['sun_source_angle'],
             'use_temperature': r['sun_use_temperature'], 'temperature': r['sun_temperature'],
             'atmosphere_sun_light': r['sun_atmosphere_sun_light']}
    if r['sun_cast_cloud_shadows'] is not None:
        props['cast_cloud_shadows'] = r['sun_cast_cloud_shadows']
    set_props(dl, props)

    sl = component(actors['skylight'], unreal.SkyLightComponent)
    sl.set_mobility(movable)
    set_props(sl, {'real_time_capture': r['skylight_real_time_capture'], 'intensity': r['skylight_intensity']})

    fog = component(actors['fog'], unreal.ExponentialHeightFogComponent)
    fprops = {'fog_density': r['fog_density'], 'fog_height_falloff': r['fog_height_falloff'],
              'enable_volumetric_fog': r['fog_volumetric']}
    if r['fog_inscattering'] is not None:
        fprops['fog_inscattering_luminance'] = unreal.LinearColor(*r['fog_inscattering'], 1.0)
    set_props(fog, fprops)

    if r['clouds']:
        if 'clouds' not in actors:
            raise RuntimeError('recipe wants clouds but the level has no LT_VolumetricCloud')
        # Assigned, never edited (Epic's rule for engine cloud material).
        component(actors['clouds'], unreal.VolumetricCloudComponent).set_material(load(CLOUD_MAT))

    ppv = actors['post']
    ppv.set_editor_property('unbound', True)
    s = ppv.get_editor_property('settings')
    gi = getattr(unreal.DynamicGlobalIlluminationMethod, r['gi_method'])
    refl = getattr(unreal.ReflectionMethod, r['reflection_method'])
    set_props(s, {
        'override_dynamic_global_illumination_method': True, 'dynamic_global_illumination_method': gi,
        'override_reflection_method': True, 'reflection_method': refl,
        'override_color_saturation': True, 'color_saturation': unreal.Vector4(r['saturation'], r['saturation'], r['saturation'], 1.0),
        'override_color_contrast': True, 'color_contrast': unreal.Vector4(r['contrast'], r['contrast'], r['contrast'], 1.0),
        'override_bloom_intensity': True, 'bloom_intensity': r['bloom'],
        'override_vignette_intensity': True, 'vignette_intensity': r['vignette'],
        'override_ambient_occlusion_intensity': True, 'ambient_occlusion_intensity': r['ao'],
        'override_motion_blur_amount': True, 'motion_blur_amount': r['motion_blur'],
        'override_auto_exposure_bias': True, 'auto_exposure_bias': r['exposure_bias'],
    })
    ppv.set_editor_property('settings', s)


def spawn_camera(label, spec):
    """Spawn a CameraActor from a CAMS entry; return it."""
    cam = spawn_class(unreal.CameraActor, spec['loc_cm'], pitch=spec['pitch'], yaw=spec['yaw'], roll=spec['roll'],
                      label=label, folder='Cameras')
    cc = cam.get_editor_property('camera_component')
    set_props(cc, {'field_of_view': spec['hfov'], 'constrain_aspect_ratio': False})
    if spec.get('auto_activate'):
        cam.set_editor_property('auto_activate_for_player', unreal.AutoReceiveInput.PLAYER0)
    return cam


def animate_in_editor(comp):
    """Make a SkeletalMeshComponent tick its animation in the editor viewport (transient: redo after a map load)."""
    try:
        # Off-screen skeletons keep their ref (T) pose unless they always tick and refresh bones; MCP captures and
        # editor screenshots render views the main viewport never saw (seen live 2026-10-01).
        comp.set_editor_property('visibility_based_anim_tick_option',
                                 unreal.VisibilityBasedAnimTickOption.ALWAYS_TICK_POSE_AND_REFRESH_BONES)
        comp.set_update_animation_in_editor(True)
        return True
    except Exception:
        return False


def animate_all_in_editor(world=None):
    """Re-apply animate_in_editor to every SkeletalMeshActor in the loaded level; return (set, total)."""
    actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem).get_all_level_actors()
    comps = [a.skeletal_mesh_component for a in actors if isinstance(a, unreal.SkeletalMeshActor)]
    return sum(1 for c in comps if animate_in_editor(c)), len(comps)
