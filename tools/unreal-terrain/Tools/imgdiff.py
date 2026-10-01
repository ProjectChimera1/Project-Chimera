#!/usr/bin/env python3
"""imgdiff.py - image measures for the terrain trial (plan C 3.8 "Image measures").

Definitions (plan C 3.8):
  luma        Rec.709 weights on the 8-bit sRGB values / 255 (0.2126 R + 0.7152 G + 0.0722 B).
  changed     a pixel whose |delta luma| > 4/255 after a 3x3 box blur of both images.
  terrain     mask = pixels changed between a shot and a `visible 0` shot at the same pose.
  footprint   mask = pixel sets the director projected to screen (footprints.json, `<pose>/<key>`).
  local undo statistic: inside the mask, changed fraction <= 0.5 % AND worst 16x16 block mean |delta| <= 2/255 (mean_abs reported too).

Library use: load_luma, blur3, changed, changed_frac, mean_abs, local_stat, footprint_mask, terrain_mask, morph helpers.
CLI:
  imgdiff.py A.png B.png [--mask terrain --hidden H.png | --mask footprint --footprints F.json --key pose/name | --mask paint --hidden H.png]
             [--local] [--band LO HI]
  prints one JSON line: {"changed_frac", "mean_abs", "mask_px", ...}. Exit 0 always on success (bars are applied by parse_terrain.py),
  except --local / --band, which exit 1 when the statistic fails.
"""
import argparse
import json
import sys

import numpy as np
from PIL import Image

THRESH = 4.0 / 255.0
LOCAL_FRAC = 0.005
LOCAL_BLOCK = 2.0 / 255.0


def load_luma(path):
    """Luma in [0, 1] as float64 HxW."""
    im = Image.open(path).convert("RGB")
    a = np.asarray(im, dtype=np.float64) / 255.0
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def blur3(x):
    """3x3 box blur with edge replication."""
    p = np.pad(x, 1, mode="edge")
    h, w = x.shape
    s = np.zeros_like(x)
    for dy in range(3):
        for dx in range(3):
            s += p[dy:dy + h, dx:dx + w]
    return s / 9.0


def changed(a, b, thresh=THRESH):
    """Boolean HxW: |blur(a) - blur(b)| > thresh."""
    return np.abs(blur3(a) - blur3(b)) > thresh


def _mask_or_all(shape, mask):
    return np.ones(shape, dtype=bool) if mask is None else mask


def changed_frac(a, b, mask=None, thresh=THRESH):
    m = _mask_or_all(a.shape, mask)
    n = int(m.sum())
    if n == 0:
        return 0.0
    return float((changed(a, b, thresh) & m).sum()) / n


def mean_abs(a, b, mask=None):
    m = _mask_or_all(a.shape, mask)
    if not m.any():
        return 0.0
    return float(np.abs(a - b)[m].mean())


def local_stat(a, b, mask, block=16):
    """Plan C 3.8 local undo statistic. Returns dict with changed_frac, worst_block_mean_abs, mean_abs and pass."""
    d = np.abs(blur3(a) - blur3(b))
    frac = changed_frac(a, b, mask)
    worst = 0.0
    h, w = a.shape
    for y in range(0, h, block):
        for x in range(0, w, block):
            mm = mask[y:y + block, x:x + block]
            if mm.sum() == 0:
                continue
            worst = max(worst, float(d[y:y + block, x:x + block][mm].mean()))
    ok = frac <= LOCAL_FRAC and worst <= LOCAL_BLOCK
    return {"changed_frac": frac, "worst_block_mean_abs": worst, "mean_abs": mean_abs(a, b, mask), "pass": bool(ok)}


def dilate(m, r=1):
    out = m.copy()
    h, w = m.shape
    p = np.pad(m, r, mode="constant")
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= p[r + dy:r + dy + h, r + dx:r + dx + w]
    return out


def erode(m, r=1):
    return ~dilate(~m, r)


def footprint_mask(fp_json, key, shape, close=1, shrink=1):
    """Mask from footprints.json pixel list `pose/name`, closed (fills sampling gaps) then eroded (silhouette tolerance)."""
    pose, name = key.split("/", 1)
    data = fp_json[pose]
    pts = data.get(name) or []
    vw, vh = data.get("viewport", [shape[1], shape[0]])
    m = np.zeros(shape, dtype=bool)
    if not pts:
        return m
    p = np.asarray(pts, dtype=np.int64)
    if (vw, vh) != (shape[1], shape[0]) and vw > 0 and vh > 0:
        p = np.stack([p[:, 0] * shape[1] // vw, p[:, 1] * shape[0] // vh], axis=1)
    ok = (p[:, 0] >= 0) & (p[:, 0] < shape[1]) & (p[:, 1] >= 0) & (p[:, 1] < shape[0])
    p = p[ok]
    m[p[:, 1], p[:, 0]] = True
    if close:
        m = erode(dilate(m, close), close)
    if shrink:
        m = erode(m, shrink)
    return m


def terrain_mask(shot, hidden):
    """Pixels where the terrain is drawn: changed between a shot and the `visible 0` shot at the same pose."""
    return changed(shot, hidden)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("a")
    ap.add_argument("b")
    ap.add_argument("--mask", choices=("terrain", "paint", "footprint"))
    ap.add_argument("--hidden", help="the `visible 0` shot for --mask terrain|paint")
    ap.add_argument("--footprints")
    ap.add_argument("--key", help="pose/name in footprints.json")
    ap.add_argument("--local", action="store_true", help="apply the local undo statistic inside the mask")
    ap.add_argument("--band", nargs=2, type=float, metavar=("LO", "HI"), help="require the mean luma of A inside the mask in [LO, HI]")
    a = ap.parse_args(argv)
    A = load_luma(a.a)
    B = load_luma(a.b)
    if A.shape != B.shape:
        print(json.dumps({"error": "size mismatch %s vs %s" % (A.shape, B.shape)}))
        return 2
    mask = None
    if a.mask in ("terrain", "paint"):
        if not a.hidden:
            ap.error("--mask %s needs --hidden" % a.mask)
        mask = terrain_mask(A, load_luma(a.hidden))
    elif a.mask == "footprint":
        if not (a.footprints and a.key):
            ap.error("--mask footprint needs --footprints and --key")
        with open(a.footprints, encoding="utf-8") as f:
            mask = footprint_mask(json.load(f), a.key, A.shape)
    out = {"changed_frac": changed_frac(A, B, mask), "mean_abs": mean_abs(A, B, mask),
           "mask_px": int(A.size if mask is None else mask.sum())}
    rc = 0
    if a.local:
        if mask is None:
            mask = np.ones(A.shape, dtype=bool)
        out["local"] = local_stat(A, B, mask)
        rc |= 0 if out["local"]["pass"] else 1
    if a.band:
        m = np.ones(A.shape, dtype=bool) if mask is None else mask
        mu = float(A[m].mean()) if m.any() else 0.0
        out["luma_mean"] = mu
        out["luma_std"] = float(A[m].std()) if m.any() else 0.0
        out["band_pass"] = bool(a.band[0] <= mu <= a.band[1])
        rc |= 0 if out["band_pass"] else 1
    print(json.dumps(out))
    return rc


if __name__ == "__main__":
    sys.exit(main())
