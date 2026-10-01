"""A9 gate: P/SimTrial/unit_meshes.json against the editor's report and the files on disk. Plain CPython.

    python check_unit_meshes.py     # prints "entries=18 resolved=18 static=18 nanite=18 height_err_max=X%" ; exit 0 only if ok

resolved = mesh and every team MI exist as .uasset under P/Content and the editor report resolved the mesh;
static   = the editor report says the mesh class is StaticMesh;
nanite   = Nanite enabled in the report;
height_err_max = largest |measured*scale/expected - 1| (unit height, or longest side for command centres), must be <= 3%.
"""
import json
import os
import sys

P = 'D:/Projects/Chimera-Unreal/ProjectChimera'
DATA = P + '/SimTrial/unit_meshes.json'
REPORT = P + '/SimTrial/run/unit_meshes_report.json'
LIMIT = 0.03


def uasset(game_path):
    """/Game/a/b -> P/Content/a/b.uasset"""
    return f'{P}/Content/{game_path[len("/Game/"):]}.uasset'


def main():
    bad = []
    data = json.load(open(DATA, encoding='utf-8'))['entries']
    rep = json.load(open(REPORT, encoding='utf-8'))['entries']
    ids = [e['id'] for e in data]
    if len(set(ids)) != len(ids):
        bad.append('duplicate ids')
    resolved = static = nanite = 0
    err_max = 0.0
    for e in data:
        r = rep.get(e['id'])
        if r is None:
            bad.append(f'{e["id"]}: not in the editor report')
            continue
        mesh_ok = os.path.isfile(uasset(e['mesh'])) and r.get('mesh') == e['mesh']
        mi_ok = bool(e['team_mi']) and all(os.path.isfile(uasset(m)) for m in e['team_mi'])
        if mesh_ok and mi_ok:
            resolved += 1
        else:
            bad.append(f'{e["id"]}: unresolved (mesh file {mesh_ok}, team MI files {mi_ok})')
        if r.get('class_name') == 'StaticMesh' and e.get('static'):
            static += 1
        else:
            bad.append(f'{e["id"]}: class {r.get("class_name")}')
        if r.get('nanite') and e.get('nanite'):
            nanite += 1
        else:
            bad.append(f'{e["id"]}: nanite off')
        err = r['measured_cm'] * e['scale'] / e['expected_cm'] - 1.0
        err_max = max(err_max, abs(err))
        if abs(err) > LIMIT:
            bad.append(f'{e["id"]}: size error {err:+.2%}')
        if r.get('failures'):
            bad.append(f'{e["id"]}: editor failures {r["failures"]}')
    print(f'entries={len(data)} resolved={resolved} static={static} nanite={nanite} height_err_max={err_max:.2%}')
    ok = (len(data), resolved, static, nanite) == (18, 18, 18, 18) and err_max <= LIMIT and not bad
    for b in bad:
        print('  ' + b, file=sys.stderr)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
