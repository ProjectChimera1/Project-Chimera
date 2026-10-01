#!/usr/bin/env python3
"""make_p6.py: positive control P6, the vector anti-aliasing model (EXECUTION section 7, T2 ruling R4).

P5 models how Slate draws text; P6 models the allowed rasteriser difference of CSS-drawn vector geometry (icons, ornament
bands, keycap corners, ring, dashes, minimap nodes, bakes): the same geometry rasterised at a different sample density.
Inputs (T0's renderer, `python ref_render.py --only p6`, recorded in ref_meta.json with their sha256):
  H/HudRef/ref/raw/p6_A_4x.png      the unmodified board 3.1a, world hidden, rendered by the same Chromium, page, pinned
                                    fonts and 3000 ms freeze at deviceScaleFactor 4 (7680x4320, CSS viewport unchanged)
  H/HudRef/ref/raw/p6_black_4x.png  the same over #000 and over #fff (world hidden): the 4x HUD matte
  H/HudRef/ref/raw/p6_white_4x.png
Output H/HudRef/ref/controls/{A,B}/P6.png (1920x1080): the pair's reference everywhere, except the vector-class pixels of
regions.json's class map, which come from the 4x render box-filtered to 1920x1080:
  * box filter: the mean of each 4x4 block in sRGB bytes (straight coverage averaging; Chromium and Slate both blend in
    sRGB-encoded space, T3), rounded half up;
  * pair A: the box-filtered p6_A_4x;
  * pair B: the box-filtered matte composited over the 1x world (world_layer.png): out = Kb + (Kw - Kb) / 255 * world, with
    Kb, Kw the box-filtered black and white renders (exact for straight sRGB-byte alpha compositing, linear in the
    backdrop). A 4x render with the world shown would also resample the world photo (world.open MAD 1.25, max 24 against
    ref_backdrop, measured 2026-10-01), a difference Slate never has: it blits the 1x world 1:1 (T3, MAD 0). The same
    composite over #14161A must reproduce pair A's direct render; make_p6 reports that check (max difference on vector px).
Layout check (the ruling's stop condition, "deviceScaleFactor 4 changes layout, not only anti-aliasing"): the box-filtered
pair-A render's flat-class pixels must equal the reference (no box, border or fill moved); make_p6 prints the numbers.
Deterministic: same inputs give the same bytes.

    python make_p6.py [--pair A|B|AB] [--no-write]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hud_common import FLAT, HUDREF, REGIONS_JSON, VECTOR, b64_png, load_rgb, sha256_file

REF = HUDREF / "ref"
RAW = REF / "raw"
DSF = 4
INPUTS = {"A": "p6_A_4x.png", "black": "p6_black_4x.png", "white": "p6_white_4x.png"}
BASE = {"A": "ref_hudonly.png", "B": "ref_backdrop.png"}
BOARD_BG = np.array([0x14, 0x16, 0x1A], float)


def box_mean(img4: np.ndarray) -> np.ndarray:
    """float64 (1080, 1920, 3): mean of every DSF x DSF block in sRGB bytes."""
    h, w = img4.shape[0] // DSF, img4.shape[1] // DSF
    return img4.astype(np.float64).reshape(h, DSF, w, DSF, 3).mean(axis=(1, 3))


def to_u8(a: np.ndarray) -> np.ndarray:
    return np.clip(np.floor(a + 0.5), 0, 255).astype(np.uint8)


def load_input(key: str) -> np.ndarray:
    img = load_rgb(RAW / INPUTS[key])
    if img.shape != (1080 * DSF, 1920 * DSF, 3):
        raise ValueError(f"{INPUTS[key]}: expected {1920 * DSF}x{1080 * DSF}, got {img.shape[1]}x{img.shape[0]}")
    return img


def class_map() -> np.ndarray:
    return b64_png(json.load(open(REGIONS_JSON, encoding="utf-8"))["classes_png_b64"])


def model(pair: str) -> np.ndarray:
    """The full-frame 1920x1080 vector AA model of a pair (before the class map selects its vector pixels), float64."""
    if pair == "A":
        return box_mean(load_input("A"))
    kb, kw = box_mean(load_input("black")), box_mean(load_input("white"))
    world = load_rgb(REF / "world_layer.png").astype(np.float64)
    return kb + (kw - kb) / 255.0 * world


def make_p6(pair: str, out=None, cls=None):
    """Render P6 for one pair; returns (uint8 image, stats)."""
    cls = class_map() if cls is None else cls
    base = load_rgb(REF / BASE[pair])
    m = to_u8(model(pair))
    vec = cls == VECTOR
    img = base.copy()
    img[vec] = m[vec]
    d = np.abs(img.astype(int) - base.astype(int)).max(axis=2)
    stats = {"vector_px": int(vec.sum()), "changed_px": int((d > 0).sum()), "vector_mad": float(np.abs(img.astype(int) - base.astype(int))[vec].mean()),
             "max": int(d.max()), "inputs_sha256": {k: sha256_file(RAW / INPUTS[k]) for k in (["A"] if pair == "A" else ["black", "white"])}}
    if out is not None:
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(img).save(out)
    return img, stats


def layout_check(cls=None) -> dict:
    """Pair A: the box-filtered 4x render against the 1x reference on flat pixels (layout), and the matte composite over
    #14161A against the direct render on vector pixels (the pair-B composite is exact)."""
    cls = class_map() if cls is None else cls
    ref = load_rgb(REF / BASE["A"]).astype(int)
    direct = box_mean(load_input("A"))
    kb, kw = box_mean(load_input("black")), box_mean(load_input("white"))
    comp = to_u8(kb + (kw - kb) / 255.0 * BOARD_BG).astype(int)
    a = to_u8(direct).astype(int)
    flat = cls == FLAT
    dflat = np.abs(a - ref).max(axis=2)[flat]
    vec = cls == VECTOR
    dcomp = np.abs(comp - a).max(axis=2)[vec]
    return {"flat_px": int(flat.sum()), "flat_mad": float(np.abs(a - ref)[flat].mean()), "flat_max": int(dflat.max()),
            "flat_px_over_2": int((dflat > 2).sum()), "flat_px_over_8": int((dflat > 8).sum()),
            "matte_vs_direct_vector_max": int(dcomp.max()), "matte_vs_direct_vector_mad": float(np.abs(comp - a)[vec].mean())}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--pair", default="AB")
    ap.add_argument("--no-write", action="store_true")
    a = ap.parse_args(argv)
    cls = class_map()
    lc = layout_check(cls)
    print(f"P6 layout check (pair A, box-filtered 4x vs 1x reference): flat px {lc['flat_px']} MAD {lc['flat_mad']:.4f} "
          f"max {lc['flat_max']} (> 2: {lc['flat_px_over_2']}, > 8: {lc['flat_px_over_8']}); matte composite vs direct render on "
          f"vector px: max {lc['matte_vs_direct_vector_max']} MAD {lc['matte_vs_direct_vector_mad']:.4f}")
    for pair in a.pair:
        out = None if a.no_write else REF / "controls" / pair / "P6.png"
        img, st = make_p6(pair, out, cls)
        print(f"P6 {pair}: {st['vector_px']} vector-class px from the deviceScaleFactor {DSF} model, {st['changed_px']} px changed vs "
              f"{BASE[pair]} (vector MAD {st['vector_mad']:.3f}, max {st['max']})" + (f" -> {out}, sha256 {sha256_file(out)}" if out else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
