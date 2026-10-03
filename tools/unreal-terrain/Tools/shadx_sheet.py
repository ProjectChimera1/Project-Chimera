#!/usr/bin/env python3
"""shadx_sheet.py - the SHADX contact sheet (SX10's picture): the scene before the edit, after the tree was raised, after the undo, and each shot beside the
twin the GPU drew from a fresh rebuild of the CPU state (scatter_fresh), with the t0 tree+shadow mask outline. Only our own frames (no third-party pixels).

Usage: python shadx_sheet.py RUN_DIR OUT.jpg [--width 1920]
Row 1: t_a, t_raised, t_undo (live).  Row 2: t_far_fresh, t_raised_fresh, t_undo_fresh (fresh rebuild).  Each tile is cropped to the mask's bounding box plus a
margin so the tree and its shadow are the picture; the mask outline of m_raised (casters within 45 m of t0) is drawn in magenta on every tile, and t0's own
tree + shadow mask (m_t0_raised) in cyan when the run has it.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import imgdiff  # noqa: E402


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    run, out = sys.argv[1], sys.argv[2]
    width = int(sys.argv[sys.argv.index("--width") + 1]) if "--width" in sys.argv else 1920
    fp = json.load(open(os.path.join(run, "footprints.json"), encoding="utf-8"))
    first = Image.open(os.path.join(run, "t_a.png")).convert("RGB")
    mask = imgdiff.footprint_mask(fp, "rts80/m_raised", (first.height, first.width), close=0, shrink=0)
    ys, xs = np.nonzero(mask)
    pad = 60
    box = (max(0, xs.min() - pad), max(0, ys.min() - pad), min(first.width, xs.max() + pad), min(first.height, ys.max() + pad))
    edge = mask & ~imgdiff.erode(mask, 2)
    # t0's own tree + shadow mask (scatter_mask only=@t0, union of t_a and t_raised) in cyan, when the run has it
    edge0 = None
    if "m_t0_raised" in fp.get("rts80", {}):
        m0 = imgdiff.footprint_mask(fp, "rts80/m_t0_raised", (first.height, first.width), close=0, shrink=0)
        edge0 = m0 & ~imgdiff.erode(m0, 2)
    dz = None
    try:
        res = json.load(open(os.path.join(run, "results.json"), encoding="utf-8"))
        ver = {v.get("name"): v for v in (res.get("scatter") or {}).get("verifies") or []}
        dz = next((t.get("dz_m") for t in (ver.get("raised") or {}).get("targets") or [] if t.get("name") == "t0"), None)
    except (OSError, ValueError):
        pass
    raised_cap = "t_raised (tree up %.1f m)" % dz if dz is not None else "t_raised"
    tiles = [("t_a (before)", "t_a"), (raised_cap, "t_raised"), ("t_undo", "t_undo"),
             ("t_far_fresh (fresh rebuild)", "t_far_fresh"), ("t_raised_fresh (fresh rebuild)", "t_raised_fresh"), ("t_undo_fresh (fresh rebuild)", "t_undo_fresh")]
    tw = width // 3
    th = int((box[3] - box[1]) * tw / float(box[2] - box[0]))
    sheet = Image.new("RGB", (width, 2 * (th + 18)), (20, 20, 20))
    for i, (cap, name) in enumerate(tiles):
        p = os.path.join(run, name + ".png")
        if not os.path.isfile(p):
            continue
        im = np.asarray(Image.open(p).convert("RGB")).copy()
        im[edge] = (255, 0, 255)
        if edge0 is not None:
            im[edge0] = (0, 255, 255)
        tile = Image.fromarray(im).crop(box).resize((tw, th), Image.LANCZOS)
        x, y = (i % 3) * tw, (i // 3) * (th + 18)
        sheet.paste(tile, (x, y + 18))
        ImageDraw.Draw(sheet).text((x + 6, y + 3), cap, fill=(230, 230, 230))
    sheet.save(out, "JPEG", quality=88)
    print("wrote %s (%dx%d, %d bytes)" % (out, sheet.width, sheet.height, os.path.getsize(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
