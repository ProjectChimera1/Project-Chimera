#!/usr/bin/env python
"""Fetch Poly Haven CC0 2K layer textures and pack them for the ChimeraTerrain ground material (plan C §3.5, C6).

Per layer (Grass, Dirt, Rock, Snow) writes T/Textures/<Layer>/:
  T_<L>_C.png    sRGB albedo      (Diffuse jpg)
  T_<L>_N.png    tangent normal   (nor_dx jpg, DirectX convention, linear)
  T_<L>_ARH.png  R=AO G=roughness B=height (arm jpg R,G + Displacement png), linear
plus T/Textures/manifest.json. Raw downloads are cached in T/Textures/_raw and md5-verified against the API.
Network failure: falls back to the repo PNGs G/assets/textures/terrain/*.png with flat normals, fallback:true.
Usage: python fetch_textures.py [--out DIR] [--force-fallback] [--alternates]
"""
import argparse, datetime, hashlib, json, os, sys, urllib.request
import numpy as np
from PIL import Image

API = "https://api.polyhaven.com"
LAYERS = {"Grass": "grass_ground", "Dirt": "brown_mud_02", "Rock": "rocks_ground_05", "Snow": "snow_02"}
ALTERNATES = {"Grass": "forest_ground_04", "Dirt": "dry_ground_01", "Rock": "aerial_rocks_02", "Snow": "snow_01"}
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
    manifest = {"date": datetime.date.today().isoformat(), "license": "CC0 (Poly Haven)", "resolution": RES,
                "fallback": False, "layers": {}}
    try:
        if a.force_fallback:
            raise OSError("forced fallback")
        raw = os.path.join(out, "_raw")
        os.makedirs(raw, exist_ok=True)
        for layer, aid in ids.items():
            srcs = fetch_asset(aid, raw)
            size = pack_layer(layer, srcs, os.path.join(out, layer))
            manifest["layers"][layer] = {"id": aid, "license": "CC0", "packed_size": list(size),
                                         "files": [f"{layer}/T_{layer}_{s}.png" for s in ("C", "N", "ARH")],
                                         "sources": {k: {kk: vv for kk, vv in v.items() if kk != "path"} for k, v in srcs.items()}}
        md5s = "verified"
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
