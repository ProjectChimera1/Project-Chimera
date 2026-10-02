#!/usr/bin/env python
"""Fill the manifest's imported_triangles column from the S3 asset report (plan-c-scatter.md 3.6 Manifest, task S3).

ScatterSrc/manifest.json (written by fetch_scatter_assets.py) has one mesh row per glb with `imported_triangles: null`. make_scatter_assets.py
imports every glb and records, per mesh, the triangles of the imported source mesh (LOD0 mesh description, the full-resolution source also for a
Nanite mesh) in Out/scatter_assets/report.json. This tool copies those numbers into the rows (keyed by level and mesh name), adds
`imported_nanite` and the imported asset path, adds the file-less engine rows (fetch_scatter_assets.ENGINE_ROWS) if a manifest written before
they existed lacks them, asserts every engine dependency the report lists has its 'epic' row, and re-checks the manifest with
fetch_scatter_assets.py --check.
Usage: python scatter_manifest_fill.py [--report Out/scatter_assets/report.json] [--manifest ScatterSrc/manifest.json] [--check]
   -> MANIFEST FILLED rows=<n> of <m> mesh rows   |   with --check: MANIFEST FILL OK (every mesh row has its imported count, within 1 % of its glTF count)
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import fetch_scatter_assets as fsa  # noqa: E402

T = os.path.dirname(HERE)
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
MANIFEST = os.path.join(SRC, "manifest.json")
REPORT = os.path.join(T, "Out", "scatter_assets", "report.json")


def key_of(mesh_path):
    """('L1', 'TreeBroadA') from /Game/Terrain/Scatter/Meshes/L1/TreeBroadA; None for the _L variants (their LOD0 is the <Name> glb row;
    their LOD1 and LOD2 fill the <Name>_LOD<i> rows, see main)."""
    parts = mesh_path.split("/")
    name = parts[-1]
    if name.endswith("_L"):
        return None
    return parts[-2], name


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--report", default=REPORT)
    ap.add_argument("--manifest", default=MANIFEST)
    ap.add_argument("--check", action="store_true", help="verify only, write nothing")
    args = ap.parse_args()
    rep = json.load(open(args.report, encoding="utf-8"))
    man = json.load(open(args.manifest, encoding="utf-8"))
    imported = {}
    for path, row in rep["meshes"].items():
        k = key_of(path)
        if k is not None and row.get("triangles_source") is not None:
            imported[k] = (row["triangles_source"], row.get("nanite"), path)
        if k is None:
            # a grass _L variant: its LOD i (i >= 1) is the glb row <Name>_LOD<i> (make_scatter_meshes.py LOD_CHAINS); the count is that
            # LOD's render triangles (a non-Nanite mesh, so render = source)
            parts = path.split("/")
            base = parts[-1][:-2]
            for l in row.get("lods") or []:
                if l["lod"] >= 1:
                    imported[(parts[-2], f"{base}_LOD{l['lod']}")] = (l["triangles"], row.get("nanite"), f"{path} LOD{l['lod']}")
    rows = [r for r in man["rows"] if r["kind"] == "mesh"]
    bad, filled = [], 0
    # Engine-plugin dependencies: one 'epic' row each (plan 3.6). Added here when missing (not with --check, which only verifies).
    have = {r.get("engine_path") for r in man["rows"] if fsa.is_engine_row(r)}
    for er in fsa.ENGINE_ROWS:
        if er["engine_path"] not in have and not args.check:
            man["rows"].append(json.loads(json.dumps(er)))
            have.add(er["engine_path"])
    for dep in rep.get("engine_dependencies", []):
        if dep["path"] not in have:
            bad.append(f"engine dependency {dep['path']}: no epic row in the manifest")
    for r in rows:
        k = (r["level"], r["id"])
        if k not in imported:
            bad.append(f"{k}: not in the asset report")
            continue
        tris, nanite, path = imported[k]
        want = next(iter(r["gltf_triangles"].values()))
        if abs(tris - want) > 0.01 * want:
            bad.append(f"{k}: imported {tris} vs glTF {want}")
        if args.check:
            if r.get("imported_triangles") != tris:
                bad.append(f"{k}: manifest has {r.get('imported_triangles')} (report {tris})")
        else:
            r["imported_triangles"] = tris
            r["imported_nanite"] = nanite
            r["imported_asset"] = path
            filled += 1
    if bad:
        print("MANIFEST FILL FAIL " + "; ".join(bad))
        return 1
    if args.check:
        print(f"MANIFEST FILL OK rows={len(rows)}")
        return 0
    with open(args.manifest, "w", encoding="utf-8", newline="\n") as f:
        json.dump(man, f, indent=1)
    r = subprocess.run([sys.executable, os.path.join(HERE, "fetch_scatter_assets.py"), "--check"], capture_output=True, text=True)
    print(r.stdout.strip())
    if r.returncode != 0:
        print("MANIFEST FILL FAIL fetch_scatter_assets.py --check: " + r.stdout + r.stderr)
        return 1
    print(f"MANIFEST FILLED rows={filled} of {len(rows)} mesh rows")
    return 0


if __name__ == "__main__":
    sys.exit(main())
