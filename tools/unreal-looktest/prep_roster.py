"""Host-side roster prep for the look test (PLAN_DELTA D1-D2). Plain CPython, stdlib only.

Reads alpha/beta_faction.json, picks the best source GLB per entry (Tripo rigged,
Tripo static, then the Godot Hunyuan GLB), measures sizes, stages a sanitised copy
of every chosen GLB into LookTest/staging/ and writes LookTest/run/roster.json.
Units keep Godot's height; buildings get a fixed longest side (PLAN_DELTA D9). A source
that differs from D1's expectation counts as a failure. On any failure roster.json is
removed and the partial result goes to run/roster.failed.json instead.

    python prep_roster.py            # exit 0 only if every check passes

Sanitising rewrites only the glTF JSON chunk (BIN chunk bytes are copied untouched):
  static entries : strip KHR_materials_volume, skins, node.skin, JOINTS_n/WEIGHTS_n, animations
  rigged entries : strip KHR_materials_volume only
"""
import datetime
import json
import math
import os
import shutil
import struct
import sys

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
STAGING = LT + '/staging'
ROSTER_OUT = LT + '/run/roster.json'
ROSTER_FAILED = LT + '/run/roster.failed.json'
TRIPO = 'D:/tripo-out'
GODOT = 'D:/Projects/Project_Chimera/godot'
FACTION_JSON = GODOT + '/resources/data/factions/{}_faction.json'
FACTIONS = ('alpha', 'beta')
# Static Tripo files that are not named <name>.glb (PLAN_DELTA D1).
STATIC_OVERRIDE = {'crucible_hall': 'crucible_hall_3d_model.glb'}
SCALE_RANGE = (0.2, 20.0)
# PLAN_DELTA D1: which source each entry must resolve to; anything else is a missing file and fails the run.
EXPECT_HUNYUAN = {'covenant_transmuter'}
EXPECT_STATIC_UNITS = {'crucible_mortar', 'render_crawler', 'envy_wraithwing'}
# PLAN_DELTA D9: buildings are sized by their longest bounding-box side (metres); units keep Godot's height.
BUILDING_LONGEST_M = {'command_center': 16.0}
BUILDING_LONGEST_DEFAULT_M = 12.0
VOLUME_EXT = 'KHR_materials_volume'


# ---------------------------------------------------------------- GLB container

def read_glb(path):
    """Parse a .glb into (json_dict, tail_bytes). tail_bytes = every chunk after the JSON chunk, verbatim."""
    with open(path, 'rb') as f:
        b = f.read()
    if len(b) < 20:
        raise ValueError(f'{path}: too small to be a GLB')
    magic, version, total = struct.unpack_from('<4sII', b, 0)
    if magic != b'glTF' or version != 2:
        raise ValueError(f'{path}: not a glTF 2 binary (magic={magic!r} version={version})')
    if total != len(b):
        raise ValueError(f'{path}: header length {total} != file size {len(b)}')
    jlen, jtype = struct.unpack_from('<I4s', b, 12)
    if jtype != b'JSON':
        raise ValueError(f'{path}: first chunk is {jtype!r}, not JSON')
    j = json.loads(b[20:20 + jlen].decode('utf-8'))
    tail = b[20 + jlen:]
    # Walk the remaining chunks so a corrupt length is caught here, not at import time.
    off = 0
    while off < len(tail):
        if off + 8 > len(tail):
            raise ValueError(f'{path}: truncated chunk header')
        ln, _ = struct.unpack_from('<I4s', tail, off)
        off += 8 + ln
    if off != len(tail):
        raise ValueError(f'{path}: chunk lengths overrun the file')
    return j, tail


def write_glb(path, j, tail):
    """Write a GLB with a freshly packed JSON chunk (space padded to 4 bytes) followed by tail."""
    jb = json.dumps(j, separators=(',', ':')).encode('utf-8')
    jb += b' ' * (-len(jb) % 4)
    total = 12 + 8 + len(jb) + len(tail)
    with open(path, 'wb') as f:
        f.write(struct.pack('<4sII', b'glTF', 2, total))
        f.write(struct.pack('<I4s', len(jb), b'JSON'))
        f.write(jb)
        f.write(tail)


def bin_chunk(tail):
    """Return the payload of the first BIN chunk in tail, or None."""
    off = 0
    while off < len(tail):
        ln, ty = struct.unpack_from('<I4s', tail, off)
        if ty == b'BIN\x00':
            return tail[off + 8:off + 8 + ln]
        off += 8 + ln
    return None


# ---------------------------------------------------------------- bounds maths

def _identity():
    return [[1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]


def _mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def node_matrix(n):
    """4x4 row-major local matrix of a glTF node (matrix is column-major in the file; else T*R*S)."""
    if 'matrix' in n:
        m = n['matrix']
        return [[m[c * 4 + r] for c in range(4)] for r in range(4)]
    tx, ty, tz = n.get('translation', (0, 0, 0))
    x, y, z, w = n.get('rotation', (0, 0, 0, 1))
    sx, sy, sz = n.get('scale', (1, 1, 1))
    rot = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
           [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
           [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    s = (sx, sy, sz)
    m = _identity()
    for r in range(3):
        for c in range(3):
            m[r][c] = rot[r][c] * s[c]
    m[0][3], m[1][3], m[2][3] = tx, ty, tz
    return m


def _xform(m, p):
    return [m[r][0] * p[0] + m[r][1] * p[1] + m[r][2] * p[2] + m[r][3] for r in range(3)]


def _axis_aligned(m):
    """True when the 3x3 part maps axes to axes (so transformed accessor-bound corners are exact)."""
    for r in range(3):
        nz = sum(1 for c in range(3) if abs(m[r][c]) > 1e-6)
        if nz > 1:
            return False
    return True


def _read_positions(j, bin_data, acc_idx):
    """Read a float32 VEC3 accessor as a list of (x, y, z). Used only when a node rotation is not axis aligned."""
    a = j['accessors'][acc_idx]
    if a['componentType'] != 5126 or a['type'] != 'VEC3' or 'sparse' in a:
        raise ValueError('vertex read supports plain float32 VEC3 accessors only')
    bv = j['bufferViews'][a['bufferView']]
    stride = bv.get('byteStride', 12)
    base = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    return [struct.unpack_from('<3f', bin_data, base + i * stride) for i in range(a['count'])]


def world_bounds(j, tail):
    """Min/max of every POSITION in the default scene with node transforms applied. Skins are ignored (bind pose)."""
    nodes = j.get('nodes', [])
    scenes = j.get('scenes', [])
    roots = scenes[j.get('scene', 0)]['nodes'] if scenes else [0]
    lo = [math.inf] * 3
    hi = [-math.inf] * 3
    bin_data = None

    def visit(i, parent):
        nonlocal bin_data
        n = nodes[i]
        m = _mul(parent, node_matrix(n))
        if 'mesh' in n:
            for prim in j['meshes'][n['mesh']]['primitives']:
                acc = j['accessors'][prim['attributes']['POSITION']]
                if _axis_aligned(m) and 'min' in acc and 'max' in acc:
                    pts = [(x, y, z) for x in (acc['min'][0], acc['max'][0])
                           for y in (acc['min'][1], acc['max'][1])
                           for z in (acc['min'][2], acc['max'][2])]
                else:
                    if bin_data is None:
                        bin_data = bin_chunk(tail)
                    pts = _read_positions(j, bin_data, prim['attributes']['POSITION'])
                for p in pts:
                    w = _xform(m, p)
                    for k in range(3):
                        lo[k] = min(lo[k], w[k])
                        hi[k] = max(hi[k], w[k])
        for c in n.get('children', []):
            visit(c, m)

    for r in roots:
        visit(r, _identity())
    if lo[0] == math.inf:
        raise ValueError('no mesh geometry found in the default scene')
    return lo, hi


def tri_count(j):
    """Triangle count over every primitive of every mesh (mode 4 / default)."""
    n = 0
    for mesh in j.get('meshes', []):
        for prim in mesh['primitives']:
            if prim.get('mode', 4) != 4:
                continue
            if 'indices' in prim:
                n += j['accessors'][prim['indices']]['count'] // 3
            else:
                n += j['accessors'][prim['attributes']['POSITION']]['count'] // 3
    return n


# ---------------------------------------------------------------- sanitising

def strip_volume(j):
    """Remove KHR_materials_volume everywhere it can appear. Returns True if anything changed."""
    changed = False
    for key in ('extensionsUsed', 'extensionsRequired'):
        if VOLUME_EXT in j.get(key, []):
            j[key] = [e for e in j[key] if e != VOLUME_EXT]
            if not j[key]:
                del j[key]
            changed = True
    for mat in j.get('materials', []):
        ext = mat.get('extensions')
        if ext and VOLUME_EXT in ext:
            del ext[VOLUME_EXT]
            if not ext:
                del mat['extensions']
            changed = True
    return changed


def strip_skin(j):
    """Make a skinned file static: no skins, node.skin, JOINTS_n/WEIGHTS_n or animations. Returns True if changed."""
    changed = False
    if 'skins' in j:
        del j['skins']
        changed = True
    if 'animations' in j:
        del j['animations']
        changed = True
    for n in j.get('nodes', []):
        if 'skin' in n:
            del n['skin']
            changed = True
    for mesh in j.get('meshes', []):
        for prim in mesh['primitives']:
            for k in [k for k in prim['attributes'] if k.startswith(('JOINTS_', 'WEIGHTS_'))]:
                del prim['attributes'][k]
                changed = True
    return changed


def stage(src, dst, rigged):
    """Write the sanitised copy of src to dst (a plain byte copy when nothing needs changing)."""
    j, tail = read_glb(src)
    changed = strip_volume(j)
    if not rigged:
        changed = strip_skin(j) or changed
    if changed:
        write_glb(dst, j, tail)
    else:
        shutil.copyfile(src, dst)


def verify_staged(path, rigged, src_bounds_h, src_tris):
    """Re-parse a staged file and assert the D2 invariants. Returns a list of problems (empty = ok)."""
    bad = []
    try:
        j, tail = read_glb(path)  # also asserts header total length == file size
        if VOLUME_EXT in json.dumps(j):
            bad.append('KHR_materials_volume still present')
        has_skin = 'skins' in j or 'animations' in j or any('skin' in n for n in j.get('nodes', []))
        has_skin = has_skin or any(k.startswith(('JOINTS_', 'WEIGHTS_')) for m in j['meshes']
                                   for p in m['primitives'] for k in p['attributes'])
        if rigged and not (j.get('skins') and j.get('animations')):
            bad.append('rigged entry lost its skin or animations')
        if not rigged and has_skin:
            bad.append('static entry still has skin/animation data')
        lo, hi = world_bounds(j, tail)
        if abs((hi[1] - lo[1]) - src_bounds_h) > 1e-6:
            bad.append('bounds changed by staging')
        if tri_count(j) != src_tris:
            bad.append('triangle count changed by staging')
        if bin_chunk(tail) is None:
            bad.append('BIN chunk missing')
    except Exception as e:  # any parse failure is a failed check
        bad.append(f're-parse failed: {e!r}')
    return bad


# ---------------------------------------------------------------- source choice

def choose_source(name):
    """Return (source_kind, path, rigged) per PLAN_DELTA D1."""
    for cand in (f'{TRIPO}/{name}_rigged.glb', f'{TRIPO}/{name}-rigged.glb'):
        if os.path.isfile(cand):
            return 'tripo_rigged', cand, True
    static = f'{TRIPO}/{STATIC_OVERRIDE.get(name, name + ".glb")}'
    if os.path.isfile(static):
        return 'tripo_static', static, False
    return 'godot_hunyuan', f'{GODOT}/assets/models/factions/{{faction}}/{name}.glb', False


def expected_source(kind, name):
    """The source PLAN_DELTA D1 requires for this entry."""
    if kind == 'building':
        return 'tripo_static'
    if name in EXPECT_HUNYUAN:
        return 'godot_hunyuan'
    return 'tripo_static' if name in EXPECT_STATIC_UNITS else 'tripo_rigged'


def main():
    os.makedirs(STAGING, exist_ok=True)
    os.makedirs(os.path.dirname(ROSTER_OUT), exist_ok=True)
    problems = []
    entries = []
    foot = {}  # (faction, kind, id) -> (staged x, staged z, godot x, godot z) footprint in metres, printed only
    for faction in FACTIONS:
        with open(FACTION_JSON.format(faction), encoding='utf-8') as f:
            data = json.load(f)
        for kind, key in (('unit', 'units'), ('building', 'buildings')):
            for e in data[key]:
                name = os.path.splitext(os.path.basename(e['mesh_path']))[0]
                tag = f'{faction}/{e["id"]}({name})'
                try:
                    godot_glb = GODOT + '/' + e['mesh_path'].replace('res://', '')
                    gj, gtail = read_glb(godot_glb)
                    glo, ghi = world_bounds(gj, gtail)
                    mesh_scale = float(e['mesh_scale'])
                    godot_h = (ghi[1] - glo[1]) * mesh_scale

                    kind_src, src, rigged = choose_source(name)
                    src = src.format(faction=faction)
                    sj, stail = read_glb(src)
                    slo, shi = world_bounds(sj, stail)
                    if kind_src != expected_source(kind, name):
                        problems.append(f'{tag}: resolved to {kind_src}, PLAN_DELTA D1 expects {expected_source(kind, name)} '
                                        f'(a Tripo file is missing?)')
                    src_h = shi[1] - slo[1]
                    if kind == 'building':  # D9: real-building size, not Godot's cube-tile height
                        target = BUILDING_LONGEST_M.get(e['id'], BUILDING_LONGEST_DEFAULT_M)
                        scale = target / max(shi[k] - slo[k] for k in range(3))
                        size_rule = f'building_longest_{target:g}m'
                    else:
                        scale = godot_h / src_h
                        size_rule = 'unit_godot_height'
                    if not SCALE_RANGE[0] <= scale <= SCALE_RANGE[1]:
                        problems.append(f'{tag}: scale {scale:.3f} outside {SCALE_RANGE}')

                    foot[(faction, kind, e['id'])] = ((shi[0] - slo[0]) * scale, (shi[2] - slo[2]) * scale,
                                                      (ghi[0] - glo[0]) * mesh_scale, (ghi[2] - glo[2]) * mesh_scale)
                    dst = f'{STAGING}/{name}{"_rigged" if rigged else ""}.glb'
                    stage(src, dst, rigged)
                    bad = verify_staged(dst, rigged, src_h, tri_count(sj))
                    problems += [f'{tag}: {b}' for b in bad]
                    anims = [a.get('name', f'anim{i}') for i, a in enumerate(sj.get('animations', []))] if rigged else []
                    entries.append({
                        'faction': faction, 'kind': kind, 'id': e['id'], 'role': e.get('category', ''),
                        'name': name, 'source': kind_src, 'glb': dst, 'asset_key': name,
                        'rigged': rigged, 'anims': anims, 'mesh_scale': mesh_scale,
                        'godot_height_m': round(godot_h, 4), 'src_height_m': round(src_h, 4),
                        'scale': round(scale, 6), 'size_rule': size_rule, 'tris': tri_count(sj),
                    })
                except Exception as ex:
                    problems.append(f'{tag}: {ex!r}')

    print(f'{"faction":6} {"kind":8} {"id":15} {"name":22} {"source":13} {"godot_h":>7} {"src_h":>6} {"scale":>7} {"tris":>6} {"foot_x":>6} {"foot_z":>6} {"gdt_x":>6} {"gdt_z":>6} anims')
    for r in entries:
        fx, fz, gx, gz = foot[(r['faction'], r['kind'], r['id'])]
        print(f'{r["faction"]:6} {r["kind"]:8} {r["id"]:15} {r["name"]:22} {r["source"]:13} '
              f'{r["godot_height_m"]:7.2f} {r["src_height_m"]:6.2f} {r["scale"]:7.3f} {r["tris"]:6d} '
              f'{fx:6.1f} {fz:6.1f} {gx:6.1f} {gz:6.1f} {len(r["anims"])}')
    if len(entries) != 26:
        problems.append(f'expected 26 roster entries, got {len(entries)}')

    out = {'generated': datetime.datetime.now().isoformat(timespec='seconds'), 'entries': entries}
    # A failed run must not leave a roster that the editor jobs would accept.
    dest = ROSTER_FAILED if problems else ROSTER_OUT
    stale = ROSTER_OUT if problems else ROSTER_FAILED
    tmp = dest + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1)
    os.replace(tmp, dest)
    if os.path.isfile(stale):
        os.remove(stale)
    print(f'{len(entries)} entries -> {dest}')
    if problems:
        print('FAILED:', file=sys.stderr)
        for p in problems:
            print('  ' + p, file=sys.stderr)
        print(f'roster.json removed; partial result in {ROSTER_FAILED}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
