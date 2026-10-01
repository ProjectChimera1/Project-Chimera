#!/usr/bin/env python3
"""make_regions.py: the 38 scoring regions, pixel classes, text runs, border lines, probes and anchors (plan B 4.2).

    python make_regions.py --ref <ref_hudonly.png> [--alpha <hud_alpha.png>] [--out regions.json]   # build + write + check
    python make_regions.py --ref <png> --check          # build in memory, verify, and compare with the existing regions.json

Input: board-3.1-elements.json (r4), the reference PNG (pair A, HUD over flat #14161A) and the HUD alpha mask.
Output: one JSON (regions.json) holding the region table, a label image and a class image (both PNG, base64, so
the partition is stored rather than re-derived), the 36 text runs, every solid border side, the colour probes and the
alignment anchors. The comparator (hud_compare.py) reads only this file plus the two images of a run.

Region partition: world.open = complement of the HUD alpha mask dilated by 3 px (a 7x7 square) and outranks everything.
All other pixels of that dilated mask belong to exactly one of the 37 HUD regions: ornament bands first, then the small
named regions, then the panels. Pixels of the mask that no rectangle claims (shadow halo, sigil tips) go to the nearest
claimed pixel's region.

Pixel classes (one per pixel, painter's order, later wins): flat (default) < vector bakes (plate drop shadow, vat glow)
< placeholder (map photo, figure box) < text (glyph rect +1) < vector (icons +1, keycap corners, nodes, ring, dashes,
minimap dots, camera rect) < ornament bands (all vector). Decision: shadow and radial-gradient bakes are classed vector,
not flat, because flat pixels carry the exact gates (MAD 1.0, 1% over 8) which no blurred bake can promise.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
from scipy.ndimage import binary_dilation, distance_transform_edt

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hud_common import (CLASS_NAMES, ELEMENTS_JSON, FLAT, H_PX, PLACEHOLDER, REGIONS_JSON, TEXT, VECTOR, W, b64_png, find_alpha,
                        find_ref, load_alpha, load_elements, load_rgb, parse_color, png_b64, sha256_file,
                        snapped_box)

VERSION = 1
PANEL_FILL = (28, 31, 37)   # #1C1F25, what locked (opacity .55) buttons composite over

REGION_NAMES = (["world.open", "top.strip", "top.chip.gold", "top.chip.iron", "top.chip.aether", "top.chip.supply",
                 "top.clock", "top.alertlog", "top.menu", "toast", "world.ring", "mm.panel", "mm.buttons", "mm.plate",
                 "mm.map", "tab.3", "sel.panel", "sel.portrait", "sel.name", "sel.role", "sel.hp", "sel.stats",
                 "card.panel"] + [f"card.{k}" for k in "QWERASDFZXCV"] + ["orn.minimap", "orn.selection", "orn.card"])
RID = {n: i for i, n in enumerate(REGION_NAMES)}
# position depends on text width (plan 4.2); the toast keycap is a flow part of the fixed toast region
FLOW = {"top.chip.iron", "top.chip.aether", "top.chip.supply", "top.clock", "top.alertlog", "top.menu", "tab.3"}

# Highest precedence first (world.open is applied before all of these)
PRIORITY = (["orn.minimap", "orn.selection", "orn.card"] + [f"card.{k}" for k in "QWERASDFZXCV"] +
            ["sel.portrait", "sel.name", "sel.role", "sel.hp", "sel.stats", "tab.3", "mm.buttons", "mm.map", "mm.plate",
             "world.ring", "toast", "top.chip.gold", "top.chip.iron", "top.chip.aether", "top.chip.supply", "top.clock",
             "top.alertlog", "top.menu", "card.panel", "sel.panel", "mm.panel", "top.strip"])

# Hard colour probes: r4 section 13's 37 single-pixel probes minus the anti-aliased ring left edge, the vat-bottom
# probe (336,1040) moved off the figure placeholder to a vat-glow pixel (300,1040). id, x, y, doc colour (r4 table).
HARD_PROBES = [
    ("strip.fill", 4, 20, 0x1C1F25), ("strip.border", 500, 39, 0x3A3F48), ("strip.below", 500, 40, 0x14161A),
    ("chip.fill", 12, 20, 0x14161A), ("chip.left", 8, 20, 0x3A3F48), ("chip.top", 60, 6, 0x3A3F48),
    ("chip.bottom", 60, 33, 0x3A3F48), ("mm.fill", 4, 1000, 0x1C1F25), ("mm.top", 20, 864, 0x8A6E3C),
    ("mm.right", 255, 1000, 0x3A3F48), ("plate.fill", 46, 1000, 0x262A31), ("plate.border", 44, 1000, 0x8A6E3C),
    ("plate.inner", 48, 1000, 0x4A4234), ("map.border", 52, 1000, 0x0B0C0E), ("mmbtn.fill", 12, 885, 0x14161A),
    ("mmbtn.border", 8, 895, 0x3A3F48), ("tab.fill", 272, 890, 0x2A2F38), ("tab.top", 290, 874, 0xE3C887),
    ("tab.left", 268, 890, 0xE3C887), ("sel.fill", 262, 1000, 0x1C1F25), ("sel.top", 900, 904, 0x8A6E3C),
    ("vat.border", 274, 1000, 0x8A6E3C), ("vat.top", 336, 925, 0x191C21), ("vat.glow", 300, 1040, None),
    ("hp.frame", 418, 1015, 0x3A3F48), ("hp.fill", 600, 1015, 0x4FB39A), ("card.fill", 1625, 1000, 0x1C1F25),
    ("btn.fill", 1640, 930, 0x14161A), ("btn.border", 1633, 900, 0x4A4234), ("btn.active.fill", 1640, 1060, 0x173129),
    ("btn.active.border", 1633, 1040, 0x4FB39A), ("btn.locked.fill", 1780, 930, 0x181A1F),
    ("btn.locked.border", 1773, 900, 0x2D3138), ("key.fill", 1639, 890, 0x0B0C0E), ("key.bottom", 1645, 896, 0x5C5446),
    ("toast.timer", 100, 850, 0xC9A86A)]
# probes only meaningful as exact colours when the backdrop is the flat #14161A (pair A)
A_ONLY = {"strip.below"}
# vector-class probes: anti-aliased ring edge and the four rope rows at x=40 (r4 section 13). Values are anti-aliased: documented, not asserted exactly
VECTOR_PROBES = [("ring.left", 992, 560), ("rope.863", 40, 863), ("rope.864", 40, 864), ("rope.865", 40, 865),
                 ("rope.866", 40, 866)]
ROPE_DOC = {863: 0x58482D, 864: 0x83683A, 865: 0x3D3526, 866: 0x454035}   # r4 section 13 lists rows 863..866 (anti-aliased)

# Alignment anchors (plan 4.1): single-axis edges, all outside the rope bands. id, orientation, edge coordinate,
# coordinate along the edge, guarded region patterns (a panel's ornament band moves with its panel).
# 'row' = a horizontal edge at y=edge sampled down a column x=along.
ANCHORS = [
    ("top.hairline", "row", 39, 500, [r"^top\."]), ("top.chip_left", "col", 8, 20, [r"^top\."]),
    ("top.menu_right", "col", 1911, 20, [r"^top\."]), ("toast.left", "col", 12, 830, [r"^toast$"]),
    ("mm.right", "col", 255, 1000, [r"^mm\.", r"^tab\.", r"^orn\.minimap$"]), ("mm.plate_top", "row", 873, 100, [r"^mm\.plate", r"^mm\.map"]),
    ("sel.vat_top", "row", 919, 336, [r"^sel\.", r"^orn\.selection$"]), ("card.left", "col", 1620, 1000, [r"^card\.", r"^orn\.card$"]),
    ("card.Q_top", "row", 877, 1660, [r"^card\.", r"^orn\.card$"])]
ANCHOR_HALF = 3       # profile = edge-3 .. edge+3
ANCHOR_RANGE = 10     # search +-10 px
ANCHOR_TOL = 8        # floor of the per-channel tolerance on the profile's first differences
ANCHOR_STEP_FRAC = 1 / 3  # tolerance = max(ANCHOR_TOL, this x the reference's largest step): a fill that is off by a few
                          # levels still matches; a 1 px move does not (it puts a full step where the reference has none,
                          # ratio 1.0). The tightest lookalike is mm.plate_top's rope row 867 at offset -6 (ratio 0.45).


def _rect_mask(rects):
    m = np.zeros((H_PX, W), bool)
    for x0, y0, x1, y1 in rects:
        m[max(0, y0):min(H_PX, y1), max(0, x0):min(W, x1)] = True
    return m


def _pad(r, l=0, t=0, rr=0, b=0):
    return (r[0] - l, r[1] - t, r[2] + rr, r[3] + b)


def _union(rects):
    return (min(r[0] for r in rects), min(r[1] for r in rects), max(r[2] for r in rects), max(r[3] for r in rects))


def _px(v):
    return float(str(v).replace("px", "")) if v is not None and str(v).endswith("px") else None


def element_sides(e):
    """{'top': (width_px, style, 'rgb(..)')...} for the sides that exist, from style.border or style.borders."""
    s = e.get("style", {})
    out = {}
    if "border" in s:
        b = s["border"]
        for k in ("top", "right", "bottom", "left"):
            out[k] = (_px(b["w"]), b["style"], b["color"])
    for k, b in (s.get("borders") or {}).items():
        out[k] = (_px(b["w"]), b["style"], b["color"])
    return {k: v for k, v in out.items() if v[0] and v[0] > 0}


def effective_opacity(E, e):
    op = 1.0
    while e is not None:
        v = e.get("style", {}).get("opacity")
        if v is not None:
            op *= float(v)
        e = E.get(e["parent"])
    return op


def region_rects(E, d):
    """Rectangle lists per region (exclusive ends), built from the element dump."""
    sb = lambda i: snapped_box(E[i]["box"])
    r = {}
    for k, i, nm in (("gold", 9, "chip:gold"), ("iron", 18, "chip:iron"), ("aether", 27, "chip:aether"), ("supply", 38, "chip:supply")):
        assert E[i]["name"].startswith(nm), (i, E[i]["name"])
        r[f"top.chip.{k}"] = [_pad(sb(i), 2, 2, 2, 2)]
    assert E[50]["name"] == "match-clock" and E[52]["name"] == "game-speed-pill"
    r["top.clock"] = [_pad(_union([sb(50), sb(52)]), 2, 2, 2, 2)]
    assert E[55]["name"] == "alert-log-button" and E[61]["name"] == "menu-button"
    r["top.alertlog"] = [_pad(sb(55), 2, 2, 2, 2)]
    r["top.menu"] = [_pad(sb(61), 2, 2, 2, 2)]
    r["top.strip"] = [sb(8)]
    assert E[70]["name"].startswith("toast-idle") and E[78]["name"] == "toast-keycap-Space"
    r["toast"] = [sb(70)]
    flow_rects = {"toast": [_pad(sb(78), 2, 2, 2, 2)]}
    assert E[5]["name"].startswith("selection-ring") and E[6]["name"].startswith("selection-health-bar-frame")
    r["world.ring"] = [_pad(sb(5), 2, 2, 2, 2), _pad(sb(6), 2, 2, 2, 2)]
    assert E[81]["name"] == "minimap-panel" and E[197]["name"] == "minimap-frame-plate" and E[203]["name"].startswith("minimap-map-surface")
    r["mm.panel"] = [sb(81)]
    r["mm.buttons"] = [_pad(sb(i), 1, 1, 1, 1) for i in (182, 186, 190)]
    r["mm.plate"] = [_pad(sb(197), 6, 6, 6, 7)]
    r["mm.map"] = [sb(203)]
    assert E[220]["name"].startswith("control-group-tab")
    r["tab.3"] = [_pad(sb(220), 2, 2, 2, 0)]
    assert E[231]["name"] == "selection-panel" and E[265]["name"] == "portrait-vat" and E[267]["name"] == "selection-info-column"
    r["sel.panel"] = [sb(231)]
    r["sel.portrait"] = [sb(265)]
    cx0, _, cx1, _ = sb(267)
    # text rows of the info column: cuts sit in the gaps between the glyph rects (name 941-968, role 972-988, hp 994-1019, stats 1024-1041)
    for name, (ya, yb) in {"sel.name": (938, 969), "sel.role": (969, 990), "sel.hp": (990, 1021), "sel.stats": (1021, 1044)}.items():
        r[name] = [(cx0, ya, cx1, yb)]
    assert E[283]["name"] == "command-card"
    r["card.panel"] = [snapped_box(E[283]["box"])]
    slots = {}
    for e in d["elements"]:
        if e["name"].startswith("cmd-button:"):
            x0, y0, x1, y1 = snapped_box(e["box"])
            c, rr = (x0 - 1633) // 70, (y0 - 877) // 66
            assert (x0 - 1633) % 70 == 0 and (y0 - 877) % 66 == 0 and (x1 - x0, y1 - y0) == (64, 60), e["name"]
            slots["QWERASDFZXCV"[rr * 4 + c]] = e["id"]
    assert len(slots) == 12
    for k, i in slots.items():
        r[f"card.{k}"] = [snapped_box(E[i]["box"])]
    frames = d["ornaments"]["frames"]
    for fname, rname in (("minimap-panel", "orn.minimap"), ("selection-panel", "orn.selection"), ("command-card", "orn.card")):
        fr = frames[fname]
        rects = []
        for i in fr["ropeIds"]:
            b = E[i]["box"]
            if b["h"] > 0 and b["w"] > 0:
                rects.append(snapped_box(b))
        for i in fr["sigilWrapperIds"]:
            b = E[i]["box"]
            rects.append((int(np.floor(b["x"])), snapped_box(b)[1], int(np.ceil(b["x"] + b["w"])), snapped_box(b)[3]))
        r[rname] = rects
    return r, flow_rects, slots


def build_masks(rects, alpha):
    """Independent boolean mask per region (precedence applied) + nearest-claim fill. Returns dict name -> mask."""
    hud = alpha > 0
    dil = binary_dilation(hud, structure=np.ones((3, 3), bool), iterations=3)
    claimed = ~dil                                   # world.open claims everything outside the dilated HUD
    masks = {"world.open": ~dil}
    for name in PRIORITY:
        m = _rect_mask(r_ for r_ in rects[name]) & ~claimed
        masks[name] = m
        claimed |= m
    leftover = ~claimed
    if leftover.any():
        owned = claimed & dil
        lab = np.full((H_PX, W), -1, np.int16)
        for name in PRIORITY:
            lab[masks[name]] = RID[name]
        idx = distance_transform_edt(~owned, return_indices=True)[1]
        ys, xs = np.nonzero(leftover)
        src = lab[idx[0][ys, xs], idx[1][ys, xs]]
        assert (src >= 0).all()
        for name in PRIORITY:
            sel = src == RID[name]
            if sel.any():
                masks[name][ys[sel], xs[sel]] = True
    return masks


def labels_from_masks(masks):
    lab = np.full((H_PX, W), 255, np.uint8)
    cnt = np.zeros((H_PX, W), np.uint8)
    for name, m in masks.items():
        lab[m] = RID[name]
        cnt += m
    return lab, cnt


def collect_runs(E, d):
    runs = []
    for e in d["elements"]:
        for tr in e.get("textRuns", []):
            t = e["text"]
            gr = tr["glyphRects"]
            gx0 = min(g["x"] for g in gr)
            gy0 = min(g["y"] for g in gr)
            gx1 = max(g["x"] + g["w"] for g in gr)
            gy1 = max(g["y"] + g["h"] for g in gr)
            # clip: interior of the nearest ancestor that paints a fill or a border
            a, clip = e, None
            while a is not None:
                s = a.get("style", {})
                fill = parse_color(s.get("backgroundColor"))
                sides = element_sides(a)
                if (fill and fill[3] >= 0.5) or sides:
                    x0, y0, x1, y1 = snapped_box(a["box"])
                    clip = (x0 + int(sides.get("left", (0,))[0]), y0 + int(sides.get("top", (0,))[0]),
                            x1 - int(sides.get("right", (0,))[0]), y1 - int(sides.get("bottom", (0,))[0]))
                    break
                a = E.get(a["parent"])
            assert clip is not None, e["id"]
            win = (int(np.floor(gx0)) - 2, int(np.floor(gy0)) - 2, int(np.ceil(gx1)) + 2, int(np.ceil(gy1)) + 2)
            win = (max(win[0], clip[0]), max(win[1], clip[1]), min(win[2], clip[2]), min(win[3], clip[3]))
            col = parse_color(t["color"])
            runs.append({"id": e["id"], "text": tr["rendered"], "family": t["fontFamily"].strip('"'),
                         "weight": int(t["fontWeight"]), "size": float(t["fontSize"].replace("px", "")),
                         "color": list(col[:3]), "align": t["textAlign"],
                         "glyph": [round(gx0, 2), round(gy0, 2), round(gx1, 2), round(gy1, 2)], "window": list(win),
                         "clip": list(clip)})
    return runs


def paint_classes(E, d, masks, runs, slots):
    cls = np.zeros((H_PX, W), np.uint8)

    def fill(rect, c, only=None):
        x0, y0, x1, y1 = rect
        sl = (slice(max(0, y0), min(H_PX, y1)), slice(max(0, x0), min(W, x1)))
        if only is None:
            cls[sl] = c
        else:
            sub = cls[sl]
            sub[only[sl]] = c

    # 1. bakes: plate drop shadow band (plate box expanded 9 px, minus the plate) and the vat glow
    px0, py0, px1, py1 = snapped_box(E[197]["box"])
    # CSS "0 3px 6px": blur 6 reaches 6 px sideways, 3 px up (6 - 3) and 9 px down (6 + 3)
    band = _rect_mask([_pad((px0, py0, px1, py1), 6, 3, 6, 9)]) & ~_rect_mask([(px0, py0, px1, py1)])
    cls[band] = VECTOR
    vx0, vy0, vx1, vy1 = snapped_box(E[265]["box"])
    fill((vx0 + 1, vy0 + 1, vx1 - 1, vy1 - 1), VECTOR)
    # 2. placeholders
    mx0, my0, mx1, my1 = snapped_box(E[203]["box"])
    fill((mx0 + 1, my0 + 1, mx1 - 1, my1 - 1), PLACEHOLDER)
    fill(snapped_box(E[266]["box"]), PLACEHOLDER)
    # 3. text: glyph rect +1 within the run's clip
    for r in runs:
        gx0, gy0, gx1, gy1 = r["glyph"]
        x0, y0, x1, y1 = int(np.floor(gx0)) - 1, int(np.floor(gy0)) - 1, int(np.ceil(gx1)) + 1, int(np.ceil(gy1)) + 1
        cx0, cy0, cx1, cy1 = r["clip"]
        fill((max(x0, cx0), max(y0, cy0), min(x1, cx1), min(y1, cy1)), TEXT)
    # 4. vector: icons +1, keycap corners, nodes, ring, dashes, minimap dots, camera rect
    for ic in d["iconsUsed"]:
        fill(_pad(snapped_box(ic["box"]), 1, 1, 1, 1), VECTOR)
    for e in d["elements"]:
        s = e.get("style", {})
        rad = s.get("borderRadius")
        sides = element_sides(e)
        if rad and rad.endswith("px") and "bottom" in sides and sides["bottom"][0] == 2:      # keycap
            x0, y0, x1, y1 = snapped_box(e["box"])
            n = int(float(rad[:-2])) + 1
            for cx, cy in ((x0, y0), (x1 - n, y0), (x0, y1 - n), (x1 - n, y1 - n)):
                fill((cx, cy, cx + n, cy + n), VECTOR)
        elif e["name"].startswith("minimap-node"):
            fill(snapped_box(e["box"]), VECTOR)
        elif e["name"].startswith("minimap-unit-dot"):
            fill(_pad(snapped_box(e["box"]), 1, 1, 1, 1), VECTOR)
        elif e["name"].startswith("selection-ring"):
            fill(_pad(snapped_box(e["box"]), 3, 3, 3, 3), VECTOR)
        elif e["name"].startswith("cmd-button:empty-slot"):
            x0, y0, x1, y1 = snapped_box(e["box"])
            for rc in ((x0, y0, x1, y0 + 1), (x0, y1 - 1, x1, y1), (x0, y0, x0 + 1, y1), (x1 - 1, y0, x1, y1)):
                fill(rc, VECTOR)
        elif e["name"].startswith("minimap-camera-rect") or e["id"] == 218:
            x0, y0, x1, y1 = snapped_box(e["box"])
            for rc in ((x0, y0, x1, y0 + 1), (x0, y1 - 1, x1, y1), (x0, y0, x0 + 1, y1), (x1 - 1, y0, x1, y1)):
                fill(rc, VECTOR)
    # 5. ornament bands are vector entirely; the world (outside the dilated HUD) is flat by definition
    for n in ("orn.minimap", "orn.selection", "orn.card"):
        cls[masks[n]] = VECTOR
    cls[masks["world.open"]] = FLAT
    return cls


def line_pixels(lab, cls, line):
    """Absolute (ys, xs) of a border line's pixels: its rectangle, owned by its region, flat class only."""
    x0, y0, x1, y1 = line["rect"]
    m = (lab[y0:y1, x0:x1] == RID[line["region"]]) & (cls[y0:y1, x0:x1] == FLAT)
    ys, xs = np.nonzero(m)
    return ys + y0, xs + x0


def _locate(ref, cls, side, rect, rgb):
    """Find the border strip within +-1 px of the predicted rectangle: the offset whose flat-class pixels are closest to rgb
    (pixels under ornaments, icons or text are ignored)."""
    best = None
    for dd in (0, -1, 1):
        x0, y0, x1, y1 = rect
        if side in ("top", "bottom"):
            y0, y1 = y0 + dd, y1 + dd
        else:
            x0, x1 = x0 + dd, x1 + dd
        if x0 < 0 or y0 < 0 or x1 > W or y1 > H_PX or x1 <= x0 or y1 <= y0:
            continue
        flat = cls[y0:y1, x0:x1] == FLAT
        if not flat.any():
            continue
        err = np.abs(ref[y0:y1, x0:x1].astype(int) - np.array(rgb)).max(axis=2)
        ratio = float((err[flat] <= 8).mean())
        if best is None or ratio > best[0] + 1e-9:
            best = (ratio, (x0, y0, x1, y1))
    return best


def collect_lines(E, d, lab, cls, ref):
    lines, dropped = [], []
    for e in d["elements"]:
        nm = e["name"]
        if nm.startswith(("minimap-unit-dot", "selection-ring", "minimap-node", "world-")) or e["id"] in (0, 216):
            continue
        sides = element_sides(e)
        if not sides:
            continue
        rad = e["style"].get("borderRadius", "0px")
        if rad.endswith("%"):
            continue
        rad = int(float(rad[:-2])) if rad.endswith("px") else 0
        x0, y0, x1, y1 = snapped_box(e["box"])
        wd = {k: (max(1, int(sides[k][0])) if k in sides else 0) for k in ("top", "right", "bottom", "left")}
        op = effective_opacity(E, e)
        for side, (w, style, col) in sides.items():
            if style != "solid":
                continue
            c = parse_color(col)
            rgb = [int(round(op * c[i] + (1 - op) * PANEL_FILL[i])) for i in range(3)]
            wi = wd[side]
            if side in ("top", "bottom"):
                xa, xb = x0 + max(rad, wd["left"]), x1 - max(rad, wd["right"])
                rect = (xa, y0, xb, y0 + wi) if side == "top" else (xa, y1 - wi, xb, y1)
            else:
                ya, yb = y0 + max(rad, wd["top"]), y1 - max(rad, wd["bottom"])
                rect = (x0, ya, x0 + wi, yb) if side == "left" else (x1 - wi, ya, x1, yb)
            if (rect[2] - rect[0]) * (rect[3] - rect[1]) < 3:
                continue
            loc = _locate(ref, cls, side, rect, rgb)
            if loc is None or loc[0] < 0.95:
                dropped.append({"element": e["id"], "name": nm, "side": side, "reason": "not located in the reference"})
                continue
            rect = loc[1]
            sub = lab[rect[1]:rect[3], rect[0]:rect[2]]
            vals, counts = np.unique(sub, return_counts=True)
            owner = int(vals[np.argmax(counts)])
            rec = {"element": e["id"], "name": nm, "side": side, "rect": list(rect), "region": REGION_NAMES[owner]}
            ys, xs = line_pixels(lab, cls, rec)
            if len(ys) < 3:
                dropped.append({"element": e["id"], "name": nm, "side": side, "reason": "no flat pixels left (ornament or text overlap)"})
                continue
            rec["npx"] = int(len(ys))
            rec["rgb"] = [int(v) for v in np.median(ref[ys, xs], axis=0)]
            lines.append(rec)
    for i, l in enumerate(lines):
        l["id"] = i
    return lines, dropped


def _flat_patches(ref, r):
    """Boolean image: every pixel in the (2r+1)^2 neighbourhood equals the centre."""
    ok = np.ones((H_PX, W), bool)
    p = np.pad(ref, ((r, r), (r, r), (0, 0)), mode="edge")
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            ok &= (p[r + dy:r + dy + H_PX, r + dx:r + dx + W] == ref).all(axis=2)
    return ok


def collect_probes(E, d, lab, cls, ref):
    probes = []
    for pid, x, y, doc in HARD_PROBES:
        probes.append({"id": pid, "x": x, "y": y, "rgb": [int(v) for v in ref[y, x]], "class": "hard",
                       "pair": "A" if pid in A_ONLY else "AB", "region": REGION_NAMES[int(lab[y, x])], "doc": doc})
    for pid, x, y in VECTOR_PROBES:
        probes.append({"id": pid, "x": x, "y": y, "rgb": [int(v) for v in ref[y, x]], "class": "vector", "pair": "AB",
                       "region": REGION_NAMES[int(lab[y, x])]})
    flat2, flat1 = _flat_patches(ref, 2), _flat_patches(ref, 1)
    seen = set()
    for e in d["elements"]:
        if e["id"] <= 3 or e["id"] == 216 or e["name"].startswith(("minimap-map-dim", "minimap-map-fog", "selection-health-bar-frame")):
            continue
        s = e.get("style", {})
        fillc = parse_color(s.get("backgroundColor"))
        if not fillc or fillc[3] <= 0:
            continue
        x0, y0, x1, y1 = snapped_box(e["box"])
        if min(x1 - x0, y1 - y0) < 5 or "clipPath" in s:
            continue
        sides = element_sides(e)
        ix0, iy0 = x0 + 1 + int(sides.get("left", (0,))[0]), y0 + 1 + int(sides.get("top", (0,))[0])
        ix1, iy1 = x1 - 1 - int(sides.get("right", (0,))[0]), y1 - 1 - int(sides.get("bottom", (0,))[0])
        if ix1 <= ix0 or iy1 <= iy0:
            continue
        box_lab = lab[y0:y1, x0:x1]
        vals, counts = np.unique(box_lab, return_counts=True)
        owner = int(vals[np.argmax(counts)])
        op = effective_opacity(E, e)
        chosen = None
        for flat in (flat2, flat1):
            sl = (slice(iy0, iy1), slice(ix0, ix1))
            cand = flat[sl] & (lab[sl] == owner) & (cls[sl] == FLAT)
            if fillc[3] >= 1 and op >= 1:
                cand &= (np.abs(ref[sl].astype(int) - np.array(fillc[:3])).max(axis=2) <= 14)
            ys, xs = np.nonzero(cand)
            if len(ys):
                ys, xs = ys + iy0, xs + ix0
                cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
                k = int(np.argmin((xs - cx) ** 2 + (ys - cy) ** 2))
                chosen = (int(xs[k]), int(ys[k]))
                break
        if chosen is None or chosen in seen:
            continue
        seen.add(chosen)
        x, y = chosen
        probes.append({"id": f"auto.{e['id']}.{e['name'][:24]}", "x": x, "y": y, "rgb": [int(v) for v in ref[y, x]],
                       "class": "auto", "pair": "AB", "region": REGION_NAMES[int(lab[y, x])]})
    return probes


def anchor_profile(img, a):
    """7-sample profile (7 x 3) of anchor a in img, with the search offsets applied by the caller."""
    _id, orient, edge, along, _g = a
    return anchor_samples(img, orient, edge, along, 0)


def anchor_samples(img, orient, edge, along, off):
    lo = edge + off - ANCHOR_HALF
    idx = np.arange(lo, lo + 2 * ANCHOR_HALF + 1)
    if orient == "row":
        if idx.min() < 0 or idx.max() >= H_PX:
            return None
        return img[idx, along].astype(int)
    if idx.min() < 0 or idx.max() >= W:
        return None
    return img[along, idx].astype(int)


def anchor_use(cls, orient, edge, along):
    """Which of the profile's 6 first differences are scored: both samples flat-class in the reference. Differences
    touching a vector bake (plate shadow, vat glow) or text are left out, so a bake that is off cannot block alignment."""
    c = anchor_samples(cls[..., None], orient, edge, along, 0)[:, 0]
    return (c[:-1] == FLAT) & (c[1:] == FLAT)


def anchor_step(ref_profile, use):
    """Largest scored step (max channel |first difference|) of a reference profile."""
    d = np.diff(np.asarray(ref_profile, int), axis=0)[use]
    return int(np.abs(d).max()) if d.size else 0


def anchor_matches(ref_profile, img, a, tol=ANCHOR_TOL, use=None):
    """Offsets in [-10, 10] at which img carries the reference's EDGE (plan 4.1).

    The edge is matched by position and step, not by absolute colour: the profile's first differences (scored pairs only,
    see anchor_use) must agree per channel within max(tol, ANCHOR_STEP_FRAC x the reference's largest step). A fill
    recoloured next to the edge changes one step by a few levels and still matches; a 1 px move puts the step one
    sample over, where the reference's difference is ~0, and fails."""
    _id, orient, edge, along, _g = a
    ref_profile = np.asarray(ref_profile, int)
    use = np.ones(2 * ANCHOR_HALF, bool) if use is None else np.asarray(use, bool)
    d_ref = np.diff(ref_profile, axis=0)[use]
    t = max(tol, ANCHOR_STEP_FRAC * anchor_step(ref_profile, use))
    out = []
    for off in range(-ANCHOR_RANGE, ANCHOR_RANGE + 1):
        s = anchor_samples(img, orient, edge, along, off)
        if s is not None and np.abs(np.diff(s, axis=0)[use] - d_ref).max() <= t:
            out.append(off)
    return out


def anchor_margin(ref_profile, img, a, use):
    """Nearest lookalike of an anchor in img: (max |first-difference error| / step, offset) over the non-zero offsets."""
    _id, orient, edge, along, _g = a
    d_ref = np.diff(np.asarray(ref_profile, int), axis=0)[use]
    h = max(1, anchor_step(ref_profile, use))
    best = (float("inf"), None)
    for off in range(-ANCHOR_RANGE, ANCHOR_RANGE + 1):
        s = anchor_samples(img, orient, edge, along, off)
        if off and s is not None:
            best = min(best, (float(np.abs(np.diff(s, axis=0)[use] - d_ref).max()) / h, off))
    return best


def build_spec(ref, alpha, ref_sha="", alpha_sha=""):
    d = load_elements()
    E = {e["id"]: e for e in d["elements"]}
    rects, flow_rects, slots = region_rects(E, d)
    masks = build_masks(rects, alpha)
    lab, cnt = labels_from_masks(masks)
    runs = collect_runs(E, d)
    cls = paint_classes(E, d, masks, runs, slots)
    for r in runs:
        cx, cy = (r["window"][0] + r["window"][2]) // 2, (r["window"][1] + r["window"][3]) // 2
        r["region"] = REGION_NAMES[int(lab[cy, cx])]
        r["flow"] = None
        for k, fr in enumerate(flow_rects.get(r["region"], [])):
            if fr[0] <= cx < fr[2] and fr[1] <= cy < fr[3]:
                r["flow"] = k
    lines, dropped = collect_lines(E, d, lab, cls, ref)
    probes = collect_probes(E, d, lab, cls, ref)
    regions = []
    for n in REGION_NAMES:
        m = masks[n]
        ys, xs = np.nonzero(m)
        cc = np.bincount(cls[m].ravel(), minlength=4)
        regions.append({"id": RID[n], "name": n, "kind": "flow" if n in FLOW else "fixed",
                        "flow_rects": [list(x) for x in flow_rects.get(n, [])],
                        "bbox": [int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1], "area": int(m.sum()),
                        "class_px": {CLASS_NAMES[i]: int(cc[i]) for i in range(4)}})
    anchors = [{"id": a[0], "orient": a[1], "edge": a[2], "along": a[3], "guards": a[4]} for a in ANCHORS]
    spec = {"version": VERSION, "size": [W, H_PX], "ref_sha256": ref_sha, "alpha_sha256": alpha_sha,
            "elements_sha256": sha256_file(ELEMENTS_JSON),
            "class_names": CLASS_NAMES, "regions": regions, "labels_png_b64": png_b64(lab), "classes_png_b64": png_b64(cls),
            "runs": runs, "lines": lines, "dropped_lines": dropped, "probes": probes, "anchors": anchors,
            "pair_note": "probe.pair 'A' = the recorded rgb is only meaningful over the flat #14161A; comparisons always read the run's own reference pixel"}
    return spec, masks, cnt


def spec_arrays(spec):
    """(label image, class image) of a spec."""
    return b64_png(spec["labels_png_b64"]), b64_png(spec["classes_png_b64"])


def check(spec, masks, cnt, ref, alpha, quiet=False):
    """Run the T1 acceptance checks; returns (ok, lines)."""
    out, ok = [], True
    names = [r["name"] for r in spec["regions"]]
    out.append(f"regions {len(names)}, text runs {len(spec['runs'])}, border lines {len(spec['lines'])}"
               f" ({len(spec['dropped_lines'])} dropped)")
    ok &= len(names) == 38 and len(spec["runs"]) == 36 and len(spec["lines"]) > 100
    hard = [p for p in spec["probes"] if p["class"] == "hard"]
    vec = [p for p in spec["probes"] if p["class"] == "vector"]
    auto = [p for p in spec["probes"] if p["class"] == "auto"]
    out.append(f"probes {len(hard)} hard + {len(vec)} vector (+ {len(auto)} auto)")
    ok &= len(hard) == 36 and len(vec) == 5
    # documented colours (r4 section 13) against this reference
    bad = [(p["id"], p["rgb"], p["doc"]) for p in hard if p["doc"] is not None and
           max(abs(p["rgb"][i] - ((p["doc"] >> (16 - 8 * i)) & 255)) for i in range(3)) > 2]
    for p in hard:
        if p["doc"] is not None and (p["id"], p["rgb"], p["doc"]) in bad:
            out.append(f"  probe {p['id']} ({p['x']},{p['y']}) is {p['rgb']} but r4 section 13 says #{p['doc']:06X}")
    ok &= not bad
    for p in vec:
        if p["id"].startswith("rope."):
            doc = ROPE_DOC[int(p["id"][5:])]
            dd = max(abs(p["rgb"][i] - ((doc >> (16 - 8 * i)) & 255)) for i in range(3))
            out.append(f"  vector probe {p['id']} {p['rgb']} (r4 #{doc:06X}, max diff {dd})")
    # anchors unique
    uniq = 0
    lab_, cls_ = spec_arrays(spec)
    for a in ANCHORS:
        prof = anchor_samples(ref, a[1], a[2], a[3], 0)
        use = anchor_use(cls_, a[1], a[2], a[3])
        h = anchor_step(prof, use)
        m = anchor_matches(prof, ref, a, use=use)
        if m == [0] and h >= 2 * ANCHOR_TOL:
            uniq += 1
            r, o = anchor_margin(prof, ref, a, use)
            out.append(f"  anchor {a[0]}: step {h}, {int(use.sum())}/6 differences scored, unique in +-{ANCHOR_RANGE}"
                       f" (nearest lookalike {r:.2f} x step at {o:+d}; tolerance {max(ANCHOR_TOL, ANCHOR_STEP_FRAC * h) / h:.2f})")
        else:
            out.append(f"  anchor {a[0]}: step {h} (needs >= {2 * ANCHOR_TOL}), matches at offsets {m}")
    out.append(f"anchors unique {uniq}/{len(ANCHORS)}")
    ok &= uniq == len(ANCHORS)
    # partition: independent masks sum to exactly one per pixel; world.open is the complement of the dilated mask
    one = bool((cnt == 1).all())
    dil = binary_dilation(alpha > 0, structure=np.ones((3, 3), bool), iterations=3)
    w_ok = bool((masks["world.open"] == ~dil).all())
    empty = [n for n in names if not masks[n].any()]
    lab, cls = spec_arrays(spec)
    rebuilt = np.full((H_PX, W), 255, np.uint8)
    for n, m in masks.items():
        rebuilt[m] = RID[n]
    consistent = bool((rebuilt == lab).all())
    out.append(f"every pixel in exactly one region: {'OK' if one and w_ok and not empty and consistent else 'FAIL'}"
               f" (pixels {W * H_PX}, world.open {int(masks['world.open'].sum())}, HUD {int((~masks['world.open']).sum())}"
               f"{'' if w_ok else ', world.open != complement of dilated alpha'}{'' if not empty else ', empty ' + str(empty)})")
    ok &= one and w_ok and not empty and consistent
    # every pixel in exactly one class (by construction a uint8 image) and text runs inside their regions
    out.append("pixel classes: " + ", ".join(f"{CLASS_NAMES[i]} {int((cls == i).sum())}" for i in range(4)))
    return ok, out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--ref", help="pair A reference PNG (default: ref/ref_hudonly.png, else the r4 copy)")
    ap.add_argument("--alpha", help="HUD alpha PNG (RGBA; default: next to the reference)")
    ap.add_argument("--out", help=f"output JSON (default {REGIONS_JSON})")
    ap.add_argument("--check", action="store_true", help="verify only: build in memory, run the checks, compare with the existing JSON")
    a = ap.parse_args(argv)
    refp = Path(a.ref) if a.ref else find_ref("A")
    if refp is None or not refp.is_file():
        print("no reference PNG found (pass --ref)", file=sys.stderr)
        return 2
    alp = Path(a.alpha) if a.alpha else (refp.parent / "hud_alpha.png" if (refp.parent / "hud_alpha.png").is_file() else find_alpha())
    if alp is None or not alp.is_file():
        print("no HUD alpha PNG found (pass --alpha)", file=sys.stderr)
        return 2
    ref, alpha = load_rgb(refp), load_alpha(alp)
    if ref.shape != (H_PX, W, 3) or alpha.shape != (H_PX, W):
        print(f"reference/alpha must be {W}x{H_PX}", file=sys.stderr)
        return 2
    spec, masks, cnt = build_spec(ref, alpha, sha256_file(refp), sha256_file(alp))
    spec["ref_name"], spec["alpha_name"] = refp.name, alp.name
    ok, lines = check(spec, masks, cnt, ref, alpha)
    print(f"reference {refp}\nalpha     {alp}")
    print("\n".join(lines))
    out = Path(a.out) if a.out else REGIONS_JSON
    if a.check:
        if out.is_file():
            old = json.load(open(out, encoding="utf-8"))
            if old.get("ref_sha256") != spec["ref_sha256"] or old.get("alpha_sha256") != spec["alpha_sha256"]:
                print(f"{out.name} was built from {old.get('ref_name')} / {old.get('alpha_name')} (different files): not compared")
            else:
                same = old == json.loads(json.dumps(spec))
                print(f"{out.name} is {'current' if same else 'STALE (differs from a fresh build)'}")
                ok &= same
    else:
        with open(out, "w", encoding="utf-8") as f:
            json.dump(spec, f, separators=(",", ":"), sort_keys=True)
        print(f"wrote {out} ({out.stat().st_size} bytes, sha256 {sha256_file(out)[:16]})")
    print("MAKE_REGIONS " + ("OK" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
