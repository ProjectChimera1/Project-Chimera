#!/usr/bin/env python
"""Fetch CC0 2K layer textures (Poly Haven or ambientCG) and pack them for the ChimeraTerrain ground material (plan C §3.5, C6, G1).

Per layer (Grass, Dirt, Rock, Snow) writes T/Textures/<Layer>/:
  T_<L>_C.png    sRGB albedo      (Diffuse jpg); for the layers in HEIGHT_IN_ALPHA (G1 round 1: Grass) RGBA with A = height, so the
                 material reads the grass grain and its blend height from the albedo fetch alone (no ARH fetch for grass)
  T_<L>_N.png    tangent normal   (nor_dx jpg, DirectX convention, linear)
  T_<L>_ARH.png  R=AO G=roughness B=height (arm jpg R,G + Displacement png), linear
plus T/Textures/manifest.json. Raw downloads are cached in T/Textures/_raw. Poly Haven maps are md5-verified against its API;
ambientCG publishes no md5, so its 2K-JPG zip is verified by the byte size its API reports and recorded with its sha256 (G1).
Layer ids live in LAYERS only ("polyhaven:<id>" or "ambientcg:<id>"); G1 (2026-10-01) took Grass004 and Rock030 from G0's candidates.
Network failure: falls back to the repo PNGs G/assets/textures/terrain/*.png with flat normals, fallback:true.
Usage: python fetch_textures.py [--out DIR] [--force-fallback] [--alternates]
"""
import argparse, datetime, hashlib, io, json, os, sys, urllib.request, zipfile
import numpy as np
from PIL import Image

API = "https://api.polyhaven.com"
ACG_API = "https://ambientcg.com/api/v2/full_json?type=Material&id={}&include=downloadData"
# The one place the layer ids live (G1: Grass004 and Rock030 per G0's candidates.json; C6's grass_ground and rocks_ground_05 are now
# alternates). Every source is CC0 1.0 (polyhaven.com/license, ambientcg.com/license).
LAYERS = {"Grass": "ambientcg:Grass004", "Dirt": "polyhaven:brown_mud_02", "Rock": "ambientcg:Rock030", "Snow": "polyhaven:snow_02"}
ALTERNATES = {"Grass": "polyhaven:grass_ground", "Dirt": "polyhaven:dry_ground_01", "Rock": "polyhaven:rocks_ground_05", "Snow": "polyhaven:snow_01"}
# G1 round 1: layers whose albedo PNG carries the height map in alpha (BC3 on import; the ARH file is still written for the record).
HEIGHT_IN_ALPHA = ("Grass",)
RES = "2k"
# (map key, API format, local suffix)
MAPS = [("Diffuse", "jpg", "diff"), ("nor_dx", "jpg", "nor_dx"), ("arm", "jpg", "arm"), ("Displacement", "png", "disp")]
HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
REPO_TEX = os.environ.get("CHIMERA_TERRAIN_PNGS", "D:/Projects/Project_Chimera/godot/assets/textures/terrain")


def http(url):
    req = urllib.request.Request(url, headers={"User-Agent": "chimera-terrain-fetch/1.0"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def md5(b):
    return hashlib.md5(b).hexdigest()


def fetch_asset(asset_id, raw_dir):
    """Download and md5-verify the four maps of one asset. Returns {key: {path,url,md5,size}}."""
    files = json.loads(http(f"{API}/files/{asset_id}"))
    out = {}
    for key, fmt, suf in MAPS:
        e = files[key][RES][fmt]
        path = os.path.join(raw_dir, f"{asset_id}_{suf}_{RES}.{fmt}")
        data = open(path, "rb").read() if os.path.exists(path) else None
        if data is None or md5(data) != e["md5"]:
            data = http(e["url"])
            if md5(data) != e["md5"]:
                raise RuntimeError(f"md5 mismatch {e['url']}: got {md5(data)} want {e['md5']}")
            open(path, "wb").write(data)
        out[key] = {"path": path, "url": e["url"], "md5": e["md5"], "size": len(data), "verified": True}
    return out


def fetch_ambientcg(asset_id, raw_dir):
    """Download the 2K-JPG zip of one ambientCG material (size-verified against its API), extract Color, NormalDX, Roughness,
    AmbientOcclusion (optional) and Displacement. Returns {key: {path,url,sha256,size}} keyed like the Poly Haven maps
    (Diffuse, nor_dx, arm = AO+rough packed here, Displacement)."""
    meta = json.loads(http(ACG_API.format(asset_id)))["foundAssets"][0]
    dl = [d for d in meta["downloadFolders"]["default"]["downloadFiletypeCategories"]["zip"]["downloads"] if d["attribute"] == "2K-JPG"][0]
    zpath = os.path.join(raw_dir, dl["fileName"])
    data = open(zpath, "rb").read() if os.path.exists(zpath) else None
    if data is None or len(data) != dl["size"]:
        data = http(dl["downloadLink"])
        if len(data) != dl["size"]:
            raise RuntimeError(f"size mismatch {dl['downloadLink']}: got {len(data)} want {dl['size']}")
        open(zpath, "wb").write(data)
    sha = hashlib.sha256(data).hexdigest()
    z = zipfile.ZipFile(io.BytesIO(data))
    names = z.namelist()

    def member(suffix, required=True):
        hit = [n for n in names if n.endswith("_" + suffix + ".jpg")]
        if not hit:
            if required:
                raise RuntimeError(f"{asset_id}: no *_{suffix}.jpg in {dl['fileName']} ({names})")
            return None
        out = os.path.join(raw_dir, hit[0])
        with open(out, "wb") as f:
            f.write(z.read(hit[0]))
        return out
    col, nor, rough, disp = member("Color"), member("NormalDX"), member("Roughness"), member("Displacement")
    ao = member("AmbientOcclusion", required=False)
    r = to_u8(Image.open(rough))
    a = to_u8(Image.open(ao)) if ao else np.full_like(r, 255)
    arm_path = os.path.join(raw_dir, f"{asset_id}_arm_2k.png")
    Image.fromarray(np.dstack([a, r, np.zeros_like(r)]), "RGB").save(arm_path)
    src = {"url": dl["downloadLink"], "zip_size": dl["size"], "zip_sha256": sha, "verified": "size", "ao_map": bool(ao)}
    return {"Diffuse": dict(src, path=col, member=os.path.basename(col)), "nor_dx": dict(src, path=nor, member=os.path.basename(nor)),
            "arm": dict(src, path=arm_path, member=os.path.basename(rough) + (" + " + os.path.basename(ao) if ao else " + AO=255")),
            "Displacement": dict(src, path=disp, member=os.path.basename(disp))}


def to_u8(img):
    """Grayscale image (8 or 16 bit) -> uint8 array."""
    a = np.asarray(img)
    if a.ndim == 3:
        a = a[..., 0]
    if a.dtype == np.uint16:
        return (a >> 8).astype(np.uint8)
    return a.astype(np.uint8)


def pack_layer(layer, srcs, outdir):
    os.makedirs(outdir, exist_ok=True)
    c = Image.open(srcs["Diffuse"]["path"]).convert("RGB")
    n = Image.open(srcs["nor_dx"]["path"]).convert("RGB")
    arm = np.asarray(Image.open(srcs["arm"]["path"]).convert("RGB"))
    disp = Image.open(srcs["Displacement"]["path"])
    h = to_u8(disp)
    if h.shape != arm.shape[:2]:
        h = np.asarray(Image.fromarray(h).resize((arm.shape[1], arm.shape[0]), Image.BILINEAR))
    arh = np.dstack([arm[..., 0], arm[..., 1], h])
    if layer in HEIGHT_IN_ALPHA:
        c = Image.fromarray(np.dstack([np.asarray(c), h]), "RGBA")
    c.save(os.path.join(outdir, f"T_{layer}_C.png"))
    n.save(os.path.join(outdir, f"T_{layer}_N.png"))
    Image.fromarray(arh, "RGB").save(os.path.join(outdir, f"T_{layer}_ARH.png"))
    return c.size


def fallback(out):
    """Repo PNGs, flat normals, constant AO/roughness/height. fallback:true."""
    layers = {}
    for layer in LAYERS:
        src = os.path.join(REPO_TEX, layer.lower() + ".png")
        d = os.path.join(out, layer)
        os.makedirs(d, exist_ok=True)
        c = Image.open(src).convert("RGB")
        w, h = c.size
        c.save(os.path.join(d, f"T_{layer}_C.png"))
        Image.new("RGB", (w, h), (128, 128, 255)).save(os.path.join(d, f"T_{layer}_N.png"))
        Image.new("RGB", (w, h), (255, 200, 128)).save(os.path.join(d, f"T_{layer}_ARH.png"))
        layers[layer] = {"source": src, "license": "repo asset", "size": [w, h]}
    return layers


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(T, "Textures"))
    ap.add_argument("--force-fallback", action="store_true")
    ap.add_argument("--alternates", action="store_true", help="use the alternate asset ids")
    a = ap.parse_args()
    out = a.out
    os.makedirs(out, exist_ok=True)
    ids = ALTERNATES if a.alternates else LAYERS
    manifest = {"date": datetime.date.today().isoformat(), "license": "CC0 1.0 (Poly Haven, ambientCG)", "resolution": RES,
                "fallback": False, "layers": {}}
    try:
        if a.force_fallback:
            raise OSError("forced fallback")
        raw = os.path.join(out, "_raw")
        os.makedirs(raw, exist_ok=True)
        for layer, key in ids.items():
            site, aid = key.split(":", 1)
            srcs = fetch_asset(aid, raw) if site == "polyhaven" else fetch_ambientcg(aid, raw)
            size = pack_layer(layer, srcs, os.path.join(out, layer))
            lic = "https://polyhaven.com/license" if site == "polyhaven" else "https://ambientcg.com/license"
            page = f"https://polyhaven.com/a/{aid}" if site == "polyhaven" else f"https://ambientcg.com/a/{aid}"
            manifest["layers"][layer] = {"id": aid, "site": site, "page": page, "license": "CC0 1.0", "license_url": lic, "packed_size": list(size),
                                         "albedo_alpha": "height" if layer in HEIGHT_IN_ALPHA else None,
                                         "files": [f"{layer}/T_{layer}_{s}.png" for s in ("C", "N", "ARH")],
                                         "sources": {k: {kk: vv for kk, vv in v.items() if kk != "path"} for k, v in srcs.items()}}
        md5s = "verified" if all(k.startswith("polyhaven:") for k in ids.values()) else "verified(polyhaven md5, ambientcg size+sha256)"
    except (OSError, urllib.error.URLError) as e:
        print(f"NETWORK FAILURE ({e}); using repo PNG fallback", file=sys.stderr)
        manifest["fallback"] = True
        manifest["layers"] = fallback(out)
        md5s = "fallback"
    json.dump(manifest, open(os.path.join(out, "manifest.json"), "w"), indent=2)
    nfiles = sum(1 for l in LAYERS for s in "C N ARH".split() if os.path.exists(os.path.join(out, l, f"T_{l}_{s}.png")))
    print(f"OK layers={len(manifest['layers'])} files={nfiles} md5={md5s}" + (" fallback:true" if manifest["fallback"] else ""))
    return 0 if nfiles == 12 else 1


if __name__ == "__main__":
    sys.exit(main())
