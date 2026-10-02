#!/usr/bin/env python
"""Mean linear luma of the CC0 textures the L0 scatter materials normalise by (plan-c-scatter.md 3.7, task S3).

The triplanar material (ChimeraScatter.hlsl, section Triplanar) divides the sampled luma by a per-texture mean so that its Tint stays the
species colour whatever the texture's own brightness. The editor's Python has no image library, so this tool measures the means once,
outside Unreal, and writes Scripts/scatter/texture_stats.json (read by make_scatter_assets.py and checked by test_scatter_assets.py).
Usage: python scatter_texture_stats.py [--check]   -> TEXSTATS OK textures=<n> | TEXSTATS DRIFT <name> (with --check)
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
OUT = os.path.join(T, "Scripts", "scatter", "texture_stats.json")
TEXTURES = {
    "leafy_grass": "raw/polyhaven/leafy_grass/leafy_grass_diff_1k.jpg",
    "bark_brown_02": "raw/polyhaven/bark_brown_02/bark_brown_02_diff_1k.jpg",
}


def srgb_to_linear(a):
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)


def measure():
    rows = {}
    for name, rel in TEXTURES.items():
        path = os.path.join(SRC, rel)
        with open(path, "rb") as f:
            sha = hashlib.sha256(f.read()).hexdigest()
        a = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0
        lin = srgb_to_linear(a)
        luma = lin @ np.array([0.2126, 0.7152, 0.0722])
        rows[name] = {"file": rel, "sha256": sha, "mean_linear_luma": round(float(luma.mean()), 5),
                      "mean_linear_rgb": [round(float(x), 5) for x in lin.reshape(-1, 3).mean(axis=0)]}
    return {"_doc": "Mean linear luma of the L0 scatter textures (Tools/scatter_texture_stats.py); read by make_scatter_assets.py.",
            "textures": rows}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="fail when the checked-in file no longer matches the textures")
    args = ap.parse_args()
    data = measure()
    if args.check:
        have = json.load(open(OUT, encoding="utf-8"))
        for n, r in data["textures"].items():
            h = have.get("textures", {}).get(n)
            if h != r:
                print(f"TEXSTATS DRIFT {n}")
                return 1
        print(f"TEXSTATS OK textures={len(data['textures'])}")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1)
        f.write("\n")
    print(f"TEXSTATS OK textures={len(data['textures'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
