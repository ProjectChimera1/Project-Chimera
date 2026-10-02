#!/usr/bin/env python
"""Fetch the CC0 scatter sources and write the scatter manifest (plan-c-scatter.md 3.6, task S1; the fetch_textures.py pattern).

Sources are listed in Scripts/scatter/meshes.json "sources" (picks from research/r9-free-asset-routes.md section 2 and plan 3.6):
  polyhaven_models    1k glTF + .bin + its textures (diff, arm, nor_gl; rough for rock_moss_set_01) + every *alpha map (the glTF's
                      JPEG base colours carry no alpha, see fetch_ph_model), md5-verified against
                      api.polyhaven.com/files/<id> -> ScatterSrc/raw/polyhaven/<id>/ (the glTF's own relative layout)
  polyhaven_textures  1k diff + nor_gl + arm jpg (L0 canopy and bark), md5-verified -> ScatterSrc/raw/polyhaven/<id>/
  ambientcg_atlases   1K-PNG zip (colour + opacity atlases), size-verified against the ambientCG API (it publishes no md5) and checked
                      against the zip_sha256 pinned in meshes.json (an id with no pin fails and prints the sha256 to pin)
                      -> ScatterSrc/raw/ambientcg/<id>/
Every request carries User-Agent ChimeraScatterFetch/1.0 (Poly Haven API ToS 2.4). Cached files are re-verified, never re-downloaded.
Before downloading, the free space on the ScatterSrc drive must be at least sources.min_free_gb (3 GB).

The manifest (ScatterSrc/manifest.json) has one row per source asset, per L0 mesh (ScatterSrc/L0/report.json) and per prepared L1 mesh
(ScatterSrc/prepared/report.json): id, kind, level, slot, source (procedural:make_scatter_meshes.py@<sha256> | polyhaven:<id> |
ambientcg:<id> | derived:<ids>), url, licence, licence_url, files with sha256, variant mesh names, glTF triangles, imported triangles
(null until S3), date (a source's fetch date = the UTC date of its oldest file on disk; a mesh row's is its file's build date).
A derived (L1) row's licence and licence_url list come from its source rows, and --check asserts they agree.
Engine-plugin content the scatter materials read (ENGINE_ROWS: the /BaseMaterial noise the grass shares with the ground) gets one row with
source `epic:<engine object path>`, licence `epic`, `engine_path` and no files (it ships with the engine, not in ScatterSrc); --check accepts a
file-less row only for that shape (plan 3.6 Manifest, proof-30). Run the fetch again after the Blender steps (cached: no download) or use --manifest to rebuild it offline.

Usage: python fetch_scatter_assets.py            fetch, verify, write the manifest
                                                 -> SCATTER_FETCH OK ids=<n> md5=verified licence=CC0 (<counts of each check>)
       python fetch_scatter_assets.py --manifest rebuild the manifest from what is on disk (no network)
       python fetch_scatter_assets.py --refresh-l0  replace only the L0 rows (after make_scatter_meshes.py), keeping every other row
       python fetch_scatter_assets.py --check    validate the manifest
                                                 -> MANIFEST OK rows=<n> licences=CC0-1.0|project-original|epic present=<in use>
Network failure: exits 1 with the reason; L0 covers every slot (plan 6 risk 14), and the manifest marks missing sources fallback:true.
"""
import argparse
import datetime
import hashlib
import io
import json
import os
import shutil
import sys
import urllib.error
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
CONFIG = os.path.join(T, "Scripts", "scatter", "meshes.json")
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
API = "https://api.polyhaven.com"
ACG_API = "https://ambientcg.com/api/v2/full_json?type=Atlas&id={}&include=downloadData"
UA = "ChimeraScatterFetch/1.0"
RES = "1k"
ACG_ATTR = "1K-PNG"
TEX_MAPS = ("Diffuse", "nor_gl", "arm")
ALLOWED = ("CC0-1.0", "project-original", "epic")
LICENCES = set(ALLOWED)
LIC_URL = {"polyhaven": "https://polyhaven.com/license", "ambientcg": "https://docs.ambientcg.com/license/"}
ENGINE_PREFIXES = ("/BaseMaterial/", "/Engine/")
# Engine-plugin objects the scatter materials depend on (make_scatter_assets.py report.json engine_dependencies must be a subset).
ENGINE_ROWS = [{"id": "T_Variation_1k_RGB_nonVT", "kind": "engine-texture", "level": "engine", "slot": None,
                "use": "grass patch field noise, shared with the ground material (make_ground_material.py NOISE_TEX)",
                "source": "epic:/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT",
                "engine_path": "/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT", "url": None, "licence": "epic",
                "licence_url": None, "date": None, "fallback": False, "files": []}]


def refuse_mirror():
    """The R mirror (Project_Chimera/tools/unreal-terrain) is for reading: outputs default next to the script, so never run there."""
    if os.path.basename(T) == "unreal-terrain" and "CHIMERA_SCATTER_SRC" not in os.environ:
        print(f"REFUSED: {T} is the repo mirror; run the ChimeraTerrain copy (or set CHIMERA_SCATTER_SRC)")
        sys.exit(2)


def http(url, timeout=120):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def md5_of(b):
    return hashlib.md5(b).hexdigest()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def rel(path):
    return os.path.relpath(path, SRC).replace("\\", "/")


MD5_FILES = [0]


def fetch_verified(url, want_md5, path):
    """Return the bytes of url at path, md5-verified; a cached file that verifies is reused."""
    MD5_FILES[0] += 1
    if os.path.exists(path):
        data = open(path, "rb").read()
        if md5_of(data) == want_md5:
            return data
    data = http(url)
    got = md5_of(data)
    if got != want_md5:
        raise RuntimeError(f"md5 mismatch {url}: got {got} want {want_md5}")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    return data


def fetch_ph_model(aid):
    files = json.loads(http(f"{API}/files/{aid}"))
    g = files["gltf"][RES]["gltf"]
    d = os.path.join(SRC, "raw", "polyhaven", aid)
    out = []
    gpath = os.path.join(d, f"{aid}_{RES}.gltf")
    fetch_verified(g["url"], g["md5"], gpath)
    out.append({"path": rel(gpath), "url": g["url"], "md5": g["md5"]})
    for name, e in sorted(g.get("include", {}).items()):
        p = os.path.join(d, *name.split("/"))
        fetch_verified(e["url"], e["md5"], p)
        out.append({"path": rel(p), "url": e["url"], "md5": e["md5"]})
    # The 1k glTF's leaf materials say alphaMode BLEND/MASK but their base colour is a JPEG with no alpha channel: the cut-out lives in
    # separate *alpha maps (Alpha, leaves_alpha, twig_alpha) that only the .blend lists. Fetch them too; prep packs them into RGBA.
    for mk in sorted(k for k in files if "alpha" in k.lower()):
        fmts = files[mk].get(RES, {})
        fmt = "png" if "png" in fmts else sorted(fmts)[0]
        e = fmts[fmt]
        p = os.path.join(d, "textures", os.path.basename(e["url"]))
        fetch_verified(e["url"], e["md5"], p)
        out.append({"path": rel(p), "url": e["url"], "md5": e["md5"], "alpha_map": mk})
    return out


def fetch_ph_texture(aid):
    files = json.loads(http(f"{API}/files/{aid}"))
    d = os.path.join(SRC, "raw", "polyhaven", aid)
    out = []
    for m in TEX_MAPS:
        e = files[m][RES]["jpg"]
        p = os.path.join(d, os.path.basename(e["url"]))
        fetch_verified(e["url"], e["md5"], p)
        out.append({"path": rel(p), "url": e["url"], "md5": e["md5"]})
    return out


def fetch_acg_atlas(aid, pin=None):
    meta = json.loads(http(ACG_API.format(aid)))["foundAssets"][0]
    dl = [x for x in meta["downloadFolders"]["default"]["downloadFiletypeCategories"]["zip"]["downloads"] if x["attribute"] == ACG_ATTR][0]
    d = os.path.join(SRC, "raw", "ambientcg", aid)
    os.makedirs(d, exist_ok=True)
    zpath = os.path.join(d, dl["fileName"])
    data = open(zpath, "rb").read() if os.path.exists(zpath) else None
    if data is None or len(data) != dl["size"] or (pin and hashlib.sha256(data).hexdigest() != pin):
        data = http(dl["downloadLink"])
        if len(data) != dl["size"]:
            raise RuntimeError(f"size mismatch {dl['downloadLink']}: got {len(data)} want {dl['size']}")
        with open(zpath, "wb") as f:
            f.write(data)
    got = hashlib.sha256(data).hexdigest()
    if not pin:
        raise RuntimeError(f"no zip_sha256 pin in meshes.json; the downloaded zip (size verified) has sha256 {got}: review and pin it")
    if got != pin:
        raise RuntimeError(f"sha256 mismatch {dl['downloadLink']}: got {got} want pinned {pin}")
    out = [{"path": rel(zpath), "url": dl["downloadLink"], "size_verified": dl["size"], "sha256_pinned": pin}]
    z = zipfile.ZipFile(io.BytesIO(data))
    for n in sorted(z.namelist()):
        if n.lower().endswith(".png") and any(s in n for s in ("_Color", "_Opacity", "_NormalGL", "_Roughness")):
            p = os.path.join(d, os.path.basename(n))
            b = z.read(n)
            if not os.path.exists(p) or open(p, "rb").read() != b:
                with open(p, "wb") as f:
                    f.write(b)
            out.append({"path": rel(p), "url": dl["downloadLink"] + "#" + n, "zip_member": n})
    return out


def load_json(path):
    if not os.path.exists(path):
        return None
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def file_rows(entries):
    rows = []
    for e in entries:
        p = os.path.join(SRC, e["path"])
        r = dict(e)
        r["sha256"] = sha256_file(p)
        r["bytes"] = os.path.getsize(p)
        rows.append(r)
    return rows


def gltf_triangles(gltf_path):
    """Triangle count per mesh of a .gltf (sum of index accessor counts / 3), and the mesh names."""
    g = load_json(gltf_path)
    per = {}
    for m in g.get("meshes", []):
        n = 0
        for pr in m["primitives"]:
            if "indices" in pr:
                n += g["accessors"][pr["indices"]]["count"] // 3
            else:
                n += g["accessors"][pr["attributes"]["POSITION"]]["count"] // 3
        per[m.get("name", "?")] = n
    return per


def build_manifest(cfg, fetched, today):
    """Rows for the source assets, the L0 report and the prepared report."""
    rows = []
    srcs = cfg["sources"]
    for kind_key, kind, site in (("polyhaven_models", "source-model", "polyhaven"), ("polyhaven_textures", "source-texture", "polyhaven"),
                                 ("ambientcg_atlases", "source-atlas", "ambientcg")):
        for s in srcs.get(kind_key, []):
            aid = s["id"]
            ent = fetched.get(aid)
            row = {"id": aid, "kind": kind, "level": "src", "slot": None, "use": s.get("use"), "source": f"{site}:{aid}",
                   "url": f"https://polyhaven.com/a/{aid}" if site == "polyhaven" else f"https://ambientcg.com/a/{aid}",
                   "licence": "CC0-1.0", "licence_url": LIC_URL[site],
                   "date": min(file_date(os.path.join(SRC, e["path"])) for e in ent) if ent else today, "fallback": ent is None,
                   "files": file_rows(ent) if ent else []}
            if ent and kind == "source-model":
                gl = [e for e in ent if e["path"].endswith(".gltf")][0]
                tris = gltf_triangles(os.path.join(SRC, gl["path"]))
                row["variant_mesh_names"] = sorted(tris)
                row["gltf_triangles"] = tris
            rows.append(row)
    l0 = load_json(os.path.join(SRC, "L0", "report.json"))
    if l0:
        rows.extend(l0_rows(l0))
    prep = load_json(os.path.join(SRC, "prepared", "report.json"))
    if prep:
        src_rows = {r["source"]: r for r in rows if r["level"] == "src"}
        for m in prep["meshes"]:
            ids = m["sources"]
            lic, lic_url = derived_licence(ids, src_rows)
            rows.append({"id": m["name"], "kind": "mesh", "level": "L1", "slot": m["slot"], "role": m.get("role", "primary"),
                         "source": "derived:" + ",".join(ids), "derived_by": m.get("script"),
                         "url": [f"https://polyhaven.com/a/{s.split(':', 1)[1]}" if s.startswith("polyhaven:") else
                                 f"https://ambientcg.com/a/{s.split(':', 1)[1]}" for s in ids],
                         "licence": lic, "licence_url": lic_url,
                         "date": file_date(os.path.join(SRC, "prepared", m["file"])), "fallback": False,
                         "files": [{"path": "prepared/" + m["file"], "sha256": m["sha256"], "bytes": m["bytes"]}],
                         "variant_mesh_names": m.get("source_meshes", []), "gltf_triangles": {m["name"]: m["triangles"]},
                         "source_triangles": m.get("source_triangles"), "slot_tris_max": m.get("slot_tris_max"),
                         "slot_budget_ok": m.get("slot_budget_ok"), "imported_triangles": None})
    rows.extend(json.loads(json.dumps(r)) for r in ENGINE_ROWS)
    return rows


def l0_rows(l0):
    """Manifest rows of the L0 procedural meshes (ScatterSrc/L0/report.json "meshes") and of the grass LOD-chain files ("lod_chains",
    LOD1 and up: <Name>_LOD<i>.glb, imported by S3 only as LOD i of <Name>_L, so the row carries lod_of and lod)."""
    src = f"procedural:make_scatter_meshes.py@{l0['generator_sha256']}"
    rows = []
    for m in l0["meshes"]:
        rows.append({"id": m["name"], "kind": "mesh", "level": "L0", "slot": m["slot"], "source": src,
                     "url": None, "licence": "project-original", "licence_url": None,
                     "date": file_date(os.path.join(SRC, "L0", m["file"])), "fallback": False,
                     "files": [{"path": "L0/" + m["file"], "sha256": m["sha256"], "bytes": m["bytes"]}],
                     "variant_mesh_names": [m["name"]], "gltf_triangles": {m["name"]: m["triangles"]}, "imported_triangles": None})
    for c in l0.get("lod_chains", []):
        for l in c["lods"][1:]:
            rid = l["file"][:-4]
            rows.append({"id": rid, "kind": "mesh", "level": "L0", "slot": c["slot"], "source": src, "lod_of": c["name"], "lod": l["lod"],
                         "url": None, "licence": "project-original", "licence_url": None,
                         "date": file_date(os.path.join(SRC, "L0", l["file"])), "fallback": False,
                         "files": [{"path": "L0/" + l["file"], "sha256": l["sha256"], "bytes": l["bytes"]}],
                         "variant_mesh_names": [rid], "gltf_triangles": {rid: l["triangles"]}, "imported_triangles": None})
    return rows


def refresh_l0(path):
    """Replace only the L0 rows of an existing manifest with l0_rows(ScatterSrc/L0/report.json), in place (the other rows keep their
    md5, url and fetch data, which --manifest cannot rebuild offline). Used after make_scatter_meshes.py changes."""
    man = load_json(path)
    l0 = load_json(os.path.join(SRC, "L0", "report.json"))
    if man is None or l0 is None:
        print("MANIFEST FAIL refresh-l0: manifest or L0 report missing")
        return 1
    old = [i for i, r in enumerate(man["rows"]) if r["level"] == "L0"]
    at = old[0] if old else len(man["rows"])
    rest = [r for r in man["rows"] if r["level"] != "L0"]
    new = l0_rows(l0)
    man["rows"] = rest[:at] + new + rest[at:]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(man, f, indent=1)
    print(f"MANIFEST L0 REFRESHED rows={len(new)} (was {len(old)})")
    return 0


def is_engine_row(r):
    """A file-less engine-plugin row: licence epic, source epic:<engine_path>, engine_path under /BaseMaterial or /Engine."""
    ep = r.get("engine_path") or ""
    return r.get("licence") == "epic" and r.get("level") == "engine" and ep.startswith(ENGINE_PREFIXES) and r.get("source") == "epic:" + ep


def file_date(path):
    """UTC build date of a mesh file (the manifest's date column for mesh rows)."""
    return datetime.datetime.fromtimestamp(os.path.getmtime(path), datetime.timezone.utc).date().isoformat()


def derived_licence(ids, src_rows):
    """Licence of a mesh derived from the given source ids. Every source must have a row; all CC0 gives CC0-1.0 with every source
    site's licence URL (sorted, unique); any other mix gives 'mixed:<licences>', which the check rejects (not an allowed licence)."""
    lics, urls = set(), set()
    for s in ids:
        r = src_rows.get(s)
        if r is None:
            return "unknown", None
        lics.add(r["licence"])
        if r.get("licence_url"):
            urls.add(r["licence_url"])
    if lics == {"CC0-1.0"}:
        return "CC0-1.0", sorted(urls)
    return "mixed:" + ",".join(sorted(lics)), sorted(urls)


def write_manifest(rows, today):
    man = {"_doc": "Scatter asset manifest (plan-c-scatter.md 3.6). Written by Tools/fetch_scatter_assets.py; licences CC0-1.0 | "
                   "project-original | epic only (r9 section 5 flag 10: no Fab, Quixel or CC-BY content).",
           "date": today, "root": SRC.replace("\\", "/"), "rows": rows}
    os.makedirs(SRC, exist_ok=True)
    with open(os.path.join(SRC, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(man, f, indent=1)
    return man


def scan_disk_for_fetched(cfg):
    """Rebuild the fetched file lists from disk (for --manifest, no network): md5 is not re-checked here."""
    fetched = {}
    for key, site in (("polyhaven_models", "polyhaven"), ("polyhaven_textures", "polyhaven"), ("ambientcg_atlases", "ambientcg")):
        for s in cfg["sources"].get(key, []):
            d = os.path.join(SRC, "raw", site, s["id"])
            if not os.path.isdir(d):
                continue
            ents = []
            for root, _, names in os.walk(d):
                for n in sorted(names):
                    ents.append({"path": rel(os.path.join(root, n))})
            fetched[s["id"]] = sorted(ents, key=lambda e: e["path"])
    return fetched


def check(path):
    man = load_json(path)
    if man is None:
        print(f"MANIFEST FAIL missing {path}")
        return 1
    errs = []
    lics = set()
    ids = set()
    for r in man["rows"]:
        rid = (r["level"], r["id"])
        if rid in ids:
            errs.append(f"duplicate row {rid}")
        ids.add(rid)
        lics.add(r["licence"])
        if r["licence"] not in LICENCES:
            errs.append(f"{r['id']}: licence {r['licence']} not in {sorted(LICENCES)}")
        if r["licence"] == "CC0-1.0" and not r.get("licence_url"):
            errs.append(f"{r['id']}: CC0 row without licence_url")
        if r["kind"] == "mesh" and r["level"] == "L0" and not r["source"].startswith("procedural:make_scatter_meshes.py@"):
            errs.append(f"{r['id']}: L0 source {r['source']}")
        if r.get("fallback"):
            errs.append(f"{r['id']}: source missing (fallback)")
        if r["licence"] == "epic" and not is_engine_row(r):
            errs.append(f"{r['id']}: an epic row must be a file-less engine row (level engine, engine_path, source epic:<engine_path>)")
        if not r["files"] and not is_engine_row(r):
            errs.append(f"{r['id']}: no files")
        if is_engine_row(r) and r["files"]:
            errs.append(f"{r['id']}: an engine row carries no files")
        for fr in r["files"]:
            p = os.path.join(SRC, fr["path"])
            if not os.path.exists(p):
                errs.append(f"{r['id']}: missing {fr['path']}")
            elif sha256_file(p) != fr["sha256"]:
                errs.append(f"{r['id']}: sha256 changed {fr['path']}")
    # every mesh on disk has a row
    for lvl, sub in (("L0", "L0"), ("L1", "prepared")):
        d = os.path.join(SRC, sub)
        if os.path.isdir(d):
            for n in sorted(os.listdir(d)):
                if n.endswith(".glb") and (lvl, n[:-4]) not in ids:
                    errs.append(f"{sub}/{n} has no manifest row")
    # derived rows must name sources that have rows, and carry exactly the licence and licence URLs those rows give
    src_rows = {r["source"]: r for r in man["rows"] if r["level"] == "src"}
    for r in man["rows"]:
        if r["source"].startswith("derived:"):
            ids = r["source"][len("derived:"):].split(",")
            for s in ids:
                if s not in src_rows:
                    errs.append(f"{r['id']}: derived from {s}, which has no source row")
            lic, urls = derived_licence(ids, src_rows)
            if r["licence"] != lic or r.get("licence_url") != urls:
                errs.append(f"{r['id']}: licence {r['licence']} {r.get('licence_url')} != from its sources {lic} {urls}")
    if errs:
        for e in errs:
            print("MANIFEST ERROR", e)
        print(f"MANIFEST FAIL rows={len(man['rows'])} errors={len(errs)}")
        return 1
    print(f"MANIFEST OK rows={len(man['rows'])} licences={'|'.join(ALLOWED)} present={','.join(x for x in ALLOWED if x in lics)}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--manifest", action="store_true", help="rebuild the manifest from disk, no network")
    ap.add_argument("--refresh-l0", action="store_true", help="replace only the L0 rows from ScatterSrc/L0/report.json, no network")
    a = ap.parse_args()
    refuse_mirror()
    if a.check:
        return check(os.path.join(SRC, "manifest.json"))
    if a.refresh_l0:
        return refresh_l0(os.path.join(SRC, "manifest.json"))
    cfg = load_json(CONFIG)
    today = datetime.date.today().isoformat()
    if a.manifest:
        man = write_manifest(build_manifest(cfg, scan_disk_for_fetched(cfg), today), today)
        print(f"MANIFEST WRITTEN rows={len(man['rows'])}")
        return 0
    os.makedirs(SRC, exist_ok=True)
    free_gb = shutil.disk_usage(SRC).free / 1e9
    if free_gb < cfg["sources"]["min_free_gb"]:
        print(f"SCATTER_FETCH FAIL free={free_gb:.1f} GB < {cfg['sources']['min_free_gb']} GB")
        return 1
    fetched = {}
    failures = []
    acg_pins = {s["id"]: s.get("zip_sha256") for s in cfg["sources"].get("ambientcg_atlases", [])}
    for key, fn in (("polyhaven_models", fetch_ph_model), ("polyhaven_textures", fetch_ph_texture),
                    ("ambientcg_atlases", lambda aid: fetch_acg_atlas(aid, acg_pins.get(aid)))):
        for s in cfg["sources"].get(key, []):
            try:
                fetched[s["id"]] = fn(s["id"])
                print(f"  ok {s['id']} files={len(fetched[s['id']])}", flush=True)
            except (OSError, urllib.error.URLError, RuntimeError, KeyError, IndexError) as e:
                failures.append(f"{s['id']}: {e}")
                print(f"  FAIL {s['id']}: {e}", flush=True)
    man = write_manifest(build_manifest(cfg, fetched, today), today)
    if failures:
        print(f"SCATTER_FETCH FAIL ids={len(fetched)} failed={len(failures)} (L0 covers every slot; manifest rows fallback:true)")
        return 1
    n_ph = sum(len(cfg["sources"].get(k, [])) for k in ("polyhaven_models", "polyhaven_textures"))
    n_acg = len(cfg["sources"].get("ambientcg_atlases", []))
    # md5=verified covers every Poly Haven file (its API publishes md5). ambientCG publishes none, so its zips are size-verified against
    # its API and sha256-checked against the pin in meshes.json. licence=CC0: both sites publish every asset under CC0 (LIC_URL).
    print(f"SCATTER_FETCH OK ids={len(fetched)} md5=verified licence=CC0 (md5=verified: polyhaven ids {n_ph}/{n_ph}, files "
          f"{MD5_FILES[0]}; sha256=pinned: ambientcg ids {n_acg}/{n_acg}, no md5 published, size-verified) rows={len(man['rows'])} "
          f"free_gb={free_gb:.0f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
