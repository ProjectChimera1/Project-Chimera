"""A9 host-side prep: stage the 12 unrigged unit GLBs and write run/units_prep.json. Plain CPython, no editor, no lock.

The sim trial renders units as StaticMesh instances (ISMs), so the 12 units that the look test imported rigged are
imported again from their unrigged Tripo GLB (D:/tripo-out/<name>.glb). Sanitising and bounds come from the look
test's prep_roster.py (strip KHR_materials_volume and any skin data; PLAN_DELTA D2). Units keep Godot's in-game
height (godot_height_m from LookTest/run/roster.json), exactly the look test's rule, so scale = godot_h / src_h of
THIS GLB. The 4 already-static units and the 2 command centres are reused from the look-test import: their GLB is
not touched, only their roster scale and report entry are carried over.

    python prep_units.py            # exit 0 only if all 18 entries resolve
"""
import json
import os
import sys

SIM = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial'
LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
STAGING = SIM + '/staging'
OUT = SIM + '/run/units_prep.json'
sys.path.insert(0, LT + '/tools')
import prep_roster as PR  # noqa: E402  (look-test rules: read_glb, world_bounds, strip_*, stage, verify_staged)


def main():
    os.makedirs(STAGING, exist_ok=True)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    roster = json.load(open(LT + '/run/roster.json', encoding='utf-8'))['entries']
    # 16 sim units + the two command centres = 18 entries.
    wanted = [e for e in roster if e['kind'] == 'unit' or (e['kind'] == 'building' and e['id'] == 'command_center')]
    problems, entries = [], []
    for e in wanted:
        tag = f'{e["faction"]}/{e["id"]}({e["name"]})'
        row = {'faction': e['faction'], 'def_id': e['id'], 'kind': e['kind'], 'role': e['role'], 'key': e['asset_key'],
               'source_roster': e['source'], 'size_rule': e['size_rule'], 'godot_height_m': e['godot_height_m']}
        try:
            if e['rigged']:
                src = f'{PR.TRIPO}/{e["name"]}.glb'
                if not os.path.isfile(src):
                    raise FileNotFoundError(src)
                dst = f'{STAGING}/{e["name"]}.glb'
                sj, stail = PR.read_glb(src)
                lo, hi = PR.world_bounds(sj, stail)
                src_h = hi[1] - lo[1]
                PR.stage(src, dst, False)   # static rules: no volume ext, no skins, no animations
                bad = PR.verify_staged(dst, False, src_h, PR.tri_count(sj))
                if bad:
                    raise ValueError('; '.join(bad))
                scale = e['godot_height_m'] / src_h
                if not PR.SCALE_RANGE[0] <= scale <= PR.SCALE_RANGE[1]:
                    raise ValueError(f'scale {scale:.3f} outside {PR.SCALE_RANGE}')
                row.update(action='import', source='tripo_unrigged', glb=dst, src_height_m=round(src_h, 4),
                           scale=round(scale, 6), tris=PR.tri_count(sj), front_source='tripo_rigged')
                # facing: the unrigged GLB is the same Tripo character geometry as the rigged one (pierce_marksman is
                # vertex-identical to lt_facing's measured subject), so it takes tripo_rigged (-90), not tripo_static
                # (0, measured on the crucible_mortar cart).
            else:
                row.update(action='reuse', source=e['source'], glb=e['glb'], src_height_m=e['src_height_m'],
                           scale=e['scale'], tris=e['tris'], front_source=e['source'])
            if e['kind'] == 'unit':
                row['expected_cm'] = round(e['godot_height_m'] * 100.0, 3)
                row['expected_dim'] = 'height'
            else:   # PLAN_DELTA D9: buildings are sized by their longest side
                row['expected_cm'] = round(PR.BUILDING_LONGEST_M[e['id']] * 100.0, 3)
                row['expected_dim'] = 'longest'
            entries.append(row)
        except Exception as ex:
            problems.append(f'{tag}: {ex!r}')
    n_imp = sum(1 for r in entries if r['action'] == 'import')
    n_reuse = sum(1 for r in entries if r['action'] == 'reuse')
    if (len(entries), n_imp, n_reuse) != (18, 12, 6):
        problems.append(f'expected 18 entries (12 import + 6 reuse), got {len(entries)} ({n_imp} + {n_reuse})')
    for r in entries:
        print(f'{r["faction"]:5} {r["def_id"]:15} {r["key"]:22} {r["action"]:6} scale={r["scale"]:.4f} '
              f'src_h={r["src_height_m"]:.3f} expected_{r["expected_dim"]}={r["expected_cm"]:.1f}cm')
    if problems:
        print('FAILED:', file=sys.stderr)
        for p in problems:
            print('  ' + p, file=sys.stderr)
        return 1
    tmp = OUT + '.tmp'
    json.dump({'entries': entries}, open(tmp, 'w', encoding='utf-8'), indent=1)
    os.replace(tmp, OUT)
    print(f'prep_units: {len(entries)} entries ({n_imp} to import, {n_reuse} reused) -> {OUT}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
