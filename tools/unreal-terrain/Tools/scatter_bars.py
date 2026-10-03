#!/usr/bin/env python3
"""scatter_bars.py - the scatter bars of plan-c-scatter.md 3.8 and 5 (task S5): --s1x, --shadx, --thinx, --latx, the dump oracle and the SCATTER
table of --summary. parse_terrain.py imports this module lazily from main(), so this file may import parse_terrain at the top.

Bars (plan-c-scatter.md 5):
  --s1x A [--pair B C ...] [--reload L] --ref S1_RUN [--xref S1X_RUN] [--packaged] [--shipping]
        SX1 (identity), SX2 (undo/redo), SX3 (reload), SX4 (frame rate, threads, mode), SX5 (on the edited surface, dump oracle), SX7 (presentation only),
        SX8/SX9 (accounting, logs: parse_terrain.scatter on every run), SX17 (image, reported), SX18 (GPU draws the CPU state); with --packaged SX16.
  --thinx RUN    SX6 (thinning) + SX5/SX6's dump oracle on the `end` dump.     --shadx RUN   SX10 (shadows follow edits).
  --latx RUN     SX12 rows (latency, reported).     --dump-oracle RUN DUMP    the oracle alone.
  --sx17f RUN [RUN ...]  SX17's frozen single-window variant (reported) on S1XF runs: undo vs pre_last2 and redo vs after with the scene and with
        scatter hidden (xo_ twins), all frames of one freeze window; gated only on the measurement's own validity (one window, read-back frozen).
Exit 0 = pass, 1 = fail, 2 = usage/io.

The dump oracle is numpy and independent of the C++: for every record of a scatter_dump, from the run's saved height.r32 and splat.rgba8 and the
palette in results.json: the surface z on the BL-TR triangulation and the class's own z rule within 1 mm, the cell-triangle gradient (gxq, gyq) exactly
(same HQ16 quantisation), and the class's own splat and slope limits with margins of 2/255 and 0.5 degrees (an instance inside the margin is exempt; one
beyond it fails).
"""
import contextlib
import io
import json
import math
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import imgdiff  # noqa: E402
import parse_terrain as pt  # noqa: E402

CLASSES = ["Grass", "Tussock", "Flower", "NearCard", "Tree", "Sapling", "Shrub", "Fern", "Rock"]
COARSE = ("Tree", "Sapling", "Shrub", "Fern", "Rock")
REC_BYTES = 45
REC_DTYPE = np.dtype([("cls", "u1"), ("mesh", "u1"), ("stream", "<u2"), ("ix", "<i4"), ("iy", "<i4"), ("slot", "u1"), ("xq", "<i4"), ("yq", "<i4"),
                      ("zq", "<i4"), ("gxq", "<i4"), ("gyq", "<i4"), ("yaw", "<u2"), ("scale", "<u2"), ("zscale", "<u2"), ("tilt", "<u2"),
                      ("cd0", "<u2"), ("cd1", "<u2")])
assert REC_DTYPE.itemsize == REC_BYTES

# SX1 positive control at S1X `before` (the flat all-grass map): every enabled class's count >= half its plan 3.4 EST. Shrubs and saplings share
# one EST ("about 2.5k"), so their floor is on the sum.
FLAT_FLOORS = [(("Grass",), 75000), (("Tussock",), 1500), (("Flower",), 4000), (("Tree",), 750), (("Shrub", "Sapling"), 1250), (("Fern",), 1500), (("Rock",), 100)]
NEARCARD_FLOOR = 17500            # level L1 only
SX5_MOVED_Z_M = 0.05
SX5_MIN_MOVED_GRASS = 1000
SX5_MIN_MOVED_COARSE = 20
Z_TOL_M = 0.001
BYTE_MARGIN = 2
SLOPE_MARGIN_DEG = 0.5
SX6_RATIO = 0.10
SX6_MIN_YIELD = {"Grass": 200, "Tussock": 10, "Flower": 20, "Tree": 5}
SX6_MIN_ROCKS = 5
SX10_PRECONDITION_DZ_M = 2.0
SX10_CHANGED_MIN = 0.10
SX12_LAT_S = {5: 0.3, 20: 0.3, 100: 1.0}
SX12_UNDO_REDO_S = 1.2
SX16_SAT_RATIO = 0.5
SX16_HUE_DEG = 8.0
SX16_CHANGED_RATIO = 0.3
SX13_TARGET_MS = 1.8
SX13_KILL_MS = 2.5
SX11_GT_P99 = 1.5
SX11_GT_P999 = 2.0
SX11_GT_OVER2_FRAC = 0.001
SX11_DELTA_MS = 1.5
SX11_OVER33_PP = 0.1
SX15_DRAWN_MAX = 24000
SX15_TRIS_MAX = 1500000
SX15_PRIMS_MAX = 700              # plan 3.7: primitives <= 700 at rts80
SX15_PRIMS_MAX_T0_SPLIT = 744     # ... 744 only when the run records the grass T0 rank split (options.scatter.t0_rank_split)
SX15_SOAK_GROWTH_MB = 300.0


# ------------------------------------------------------------------------------------------------------------------------------ small helpers
def hsh(res, name):
    return ((res or {}).get("hashes") or {}).get(name) or {}


def scatter_block(res):
    return (res or {}).get("scatter") or {}


def sub(g, label, fn, *args, **kw):
    """Run a bar group (its prints captured), add one bar for it to g and echo the failing rows."""
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        out = fn(*args, **kw)
    sg = out[0] if isinstance(out, tuple) else out
    fails = [r["bar"] for r in sg.rows if not r["pass"]]
    if fails:
        for ln in buf.getvalue().splitlines():
            if " FAIL " in ln or ln.startswith("  "):
                print("  [%s] %s" % (label, ln.strip()))
    g.bar(label, not fails and bool(sg.rows), "%d bars%s" % (len(sg.rows), (", failing: %s" % fails) if fails else ""), "every bar of the group passes")
    return sg


def read_dump(path):
    raw = np.fromfile(path, dtype=np.uint8)
    if raw.size % REC_BYTES:
        raise ValueError("%s: %d bytes is not a multiple of %d" % (path, raw.size, REC_BYTES))
    return raw.view(REC_DTYPE)


def keys_of(rec):
    """FScatterRecord::Key: stream << 48 | slot << 40 | ((ix + 524288) & 0xFFFFF) << 20 | ((iy + 524288) & 0xFFFFF)."""
    ix = ((rec["ix"].astype(np.int64) + 524288) & 0xFFFFF).astype(np.uint64)
    iy = ((rec["iy"].astype(np.int64) + 524288) & 0xFFFFF).astype(np.uint64)
    return (rec["stream"].astype(np.uint64) << np.uint64(48)) | (rec["slot"].astype(np.uint64) << np.uint64(40)) | (ix << np.uint64(20)) | iy


def load_terrain(run):
    """(heights HQ16 int64 [Y, X], splat uint8 [TY, TX, 4], E) from the run's saved files."""
    t = pt.read_json(os.path.join(run, "terrain.json"))
    e, w = int(t["half_extent_m"]), int(t["width"])
    h = np.fromfile(os.path.join(run, "height.r32"), dtype="<f4")
    if h.size != w * w:
        raise ValueError("height.r32 has %d floats, expected %d" % (h.size, w * w))
    hq = np.floor(h.astype(np.float64).reshape(w, w) * 65536.0 + 0.5).astype(np.int64)
    sw = int(t["splat"]["width"])
    s = np.fromfile(os.path.join(run, "splat.rgba8"), dtype=np.uint8)
    if s.size != sw * sw * 4:
        raise ValueError("splat.rgba8 has %d bytes, expected %d" % (s.size, sw * sw * 4))
    return hq, s.reshape(sw, sw, 4), e


def surface_q16(hq, e, xq, yq):
    """Integer surface of the BL-TR triangulation (python twin of ScatterLocateCell + ScatterCellSurfaceQ16): (z, gx, gy), int64 arrays."""
    quads = hq.shape[0] - 1
    eq = e << 16
    u = np.clip(xq + eq, 0, quads << 16)
    v = np.clip(yq + eq, 0, quads << 16)
    cx = np.minimum(u >> 16, quads - 1)
    cy = np.minimum(v >> 16, quads - 1)
    fu = u - (cx << 16)
    fv = v - (cy << 16)
    hbl, hbr, htl, htr = hq[cy, cx], hq[cy, cx + 1], hq[cy + 1, cx], hq[cy + 1, cx + 1]
    upper = fv >= fu
    s = np.where(upper, hbl * 65536 + fu * (htr - htl) + fv * (htl - hbl), hbl * 65536 + fu * (hbr - hbl) + fv * (htr - hbr))
    gx = np.where(upper, htr - htl, hbr - hbl)
    gy = np.where(upper, htl - hbl, htr - hbr)
    return (s + 32768) >> 16, gx, gy


def splat_q8(spl, e, xq, yq):
    """Bilinear splat weights (N, 4) at Q16 metres, texel centres at (T + 0.5) * 0.5 - E, edges clamped (python twin of SplatQ8Bilinear4)."""
    s = spl.shape[0]
    eq = e << 16
    sx = (xq + eq) * 2 - 32768
    sy = (yq + eq) * 2 - 32768
    tx0, ty0 = sx >> 16, sy >> 16
    fx, fy = (sx - tx0 * 65536)[:, None], (sy - ty0 * 65536)[:, None]

    def at(tx, ty):
        return spl[np.clip(ty, 0, s - 1), np.clip(tx, 0, s - 1)].astype(np.int64)
    row0 = at(tx0, ty0) * (65536 - fx) + at(tx0 + 1, ty0) * fx
    row1 = at(tx0, ty0 + 1) * (65536 - fx) + at(tx0 + 1, ty0 + 1) * fx
    v = (row0 * (65536 - fy) + row1 * fy) >> 16
    return (v + 32768) >> 16


def slope_thr_margin(thr):
    """tan^2 * 2^32 threshold raised by the 0.5 degree margin."""
    return math.tan(math.atan(math.sqrt(thr / 4294967296.0)) + math.radians(SLOPE_MARGIN_DEG)) ** 2 * 4294967296.0


def mul_q16(a, b):
    return (a * b) >> 16


def oracle(rec, hq, spl, e, pal):
    """The dump oracle for one record array. Returns a dict of counts, maxima and example rows."""
    n = len(rec)
    xq, yq = rec["xq"].astype(np.int64), rec["yq"].astype(np.int64)
    cls = rec["cls"].astype(np.int64)
    z, gx, gy = surface_q16(hq, e, xq, yq)
    P = pal
    sink = np.zeros(n, dtype=np.int64)
    for c, name in ((0, "GrassSinkM"), (1, "TussockSinkM"), (2, "FlowerSinkM"), (3, "NearCardSinkM"), (6, "ShrubSinkM"), (7, "FernSinkM")):
        sink = np.where(cls == c, P[name], sink)
    rock_sink = mul_q16(mul_q16(P["RockSinkFrac"], P["RockNominalHM"]), rec["scale"].astype(np.int64) << 4)
    sink = np.where(cls == 8, rock_sink, sink)
    zrule = z - sink
    tree = (cls == 4) | (cls == 5)
    if tree.any():
        d = P["TreeBaseProbeM"]
        zt = z.copy()
        for dx, dy in ((d, 0), (-d, 0), (0, d), (0, -d)):
            zt = np.minimum(zt, surface_q16(hq, e, xq + dx, yq + dy)[0])
        zrule = np.where(tree, zt - P["TreeSinkM"], zrule)
    dz_m = np.abs(rec["zq"].astype(np.int64) - zrule) / 65536.0
    bad_z = dz_m > Z_TOL_M
    bad_g = (rec["gxq"].astype(np.int64) != gx) | (rec["gyq"].astype(np.int64) != gy)
    g2 = gx * gx + gy * gy
    w = splat_q8(spl, e, xq, yq)
    m = BYTE_MARGIN
    surf = z.astype(np.int64)
    viol = {}

    def flag(name, mask):
        viol[name] = mask

    e3 = lambda cs: ((w[:, 1] > P["E3DirtHi"] + m) | (w[:, 2] > P["E3RockHi"] + m) | (w[:, 3] > P["E3SnowHi"] + m)) & np.isin(cls, cs)
    flag("e3_splat", e3((0, 1, 2, 3, 7)))
    flag("grass_wg", np.isin(cls, (0, 3)) & (w[:, 0] < P["GLo"] - m))
    flag("flower_wg", (cls == 2) & (w[:, 0] < P["FlowerGLo"] - m))
    flag("slope_sg", np.isin(cls, (0, 1, 3, 6)) & (g2 > slope_thr_margin(P["SgHi"])))
    flag("slope_flower", (cls == 2) & (g2 > slope_thr_margin(P["FlowerSlopeHi"])))
    flag("slope_fern", (cls == 7) & (g2 > slope_thr_margin(P["FernSlopeHi"])))
    flag("slope_tree", tree & (g2 > slope_thr_margin(P["StHi"])))
    flag("grass_height", (cls == 0) & (surf > P["HgHi"] + 655))
    flag("tree_height", tree & (surf > P["HtHi"] + 655))
    flag("shrub_height", (cls == 6) & (surf > P["ShrubZHi"] + 655))
    flag("trunk_dirt", tree & (w[:, 1] > P["TrunkDirt"] + m))
    flag("trunk_rock", tree & (w[:, 2] > P["TrunkRock"] + m))
    conifer = np.isin(rec["mesh"], (7, 8)) & (cls == 4)
    flag("tree_snow", tree & (w[:, 3] > np.where(conifer, P["TreeConiferSnow"], P["TreeBroadSnow"]) + m))
    if tree.any():
        dd = P["TreeProbeM"]
        probe = np.zeros(n, dtype=np.int64)
        for dx, dy in ((dd, 0), (-dd, 0), (0, dd), (0, -dd)):
            probe = np.maximum(probe, splat_q8(spl, e, xq + dx, yq + dy)[:, 1])
        flag("tree_probe_dirt", tree & (probe > P["TreeProbeDirt"] + m))
    flag("shrub_splat", (cls == 6) & ((w[:, 2] > P["ShrubRockHi"] + m) | (w[:, 3] > P["ShrubSnowHi"] + m)))
    # Rocks have no hard splat limit: only the paint term of their probability falls with snow (RockSnowLo..Hi); the slope and boulder terms do not, so a rock on
    # snow is legal and is not flagged (plan 3.4 table, EvalRock).
    flag("inside_map", (np.abs(xq) >= (e << 16)) | (np.abs(yq) >= (e << 16)))
    out = {"records": int(n), "max_dz_mm": float(dz_m.max() * 1000.0) if n else 0.0, "bad_z": int(bad_z.sum()), "bad_gradient": int(bad_g.sum()),
           "violations": {k: int(v.sum()) for k, v in viol.items() if v.any()}, "violation_total": int(sum(int(v.sum()) for v in viol.values())), "examples": []}
    anyv = bad_z | bad_g
    for v in viol.values():
        anyv = anyv | v
    for i in np.nonzero(anyv)[0][:6]:
        names = [k for k, v in viol.items() if v[i]]
        if bad_z[i]:
            names.append("z")
        if bad_g[i]:
            names.append("gradient")
        out["examples"].append("%s at (%.3f, %.3f) z %.4f rule %.4f: %s" % (CLASSES[int(cls[i])], xq[i] / 65536.0, yq[i] / 65536.0, rec["zq"][i] / 65536.0, zrule[i] / 65536.0, ",".join(names)))
    return out


def palette_of(res):
    p = (((res.get("options") or {}).get("scatter")) or {}).get("palette")
    return {k: int(v) for k, v in p.items()} if p else None


def dump_bar(g, run, res, name, label):
    """SX5/SX6 dump oracle on <run>/scatter_<name>.bin; returns the record array or None."""
    path = os.path.join(run, "scatter_%s.bin" % name)
    pal = palette_of(res or {})
    if not os.path.isfile(path):
        g.bar(label, False, "no samples (%s missing: scatter_dump %s)" % (os.path.basename(path), name), "dump oracle")
        return None
    dumps = {d.get("name"): d for d in scatter_block(res).get("dumps") or []}
    if (dumps.get(name) or {}).get("pending"):
        g.bar(label + "_settled", False, "scatter_dump %s was taken with scatter work pending" % name, "a dump is taken with nothing pending (scatter_wait first)")
    if pal is None:
        g.bar(label, False, "no samples (results.json has no options.scatter.palette)", "dump oracle")
        return None
    try:
        rec = read_dump(path)
        hq, spl, e = load_terrain(run)
    except (OSError, ValueError, KeyError) as ex:
        g.bar(label, False, "no samples (%s)" % ex, "dump oracle")
        return None
    want = (dumps.get(name) or {}).get("sha256")
    if want:
        import hashlib
        got = hashlib.sha256(open(path, "rb").read()).hexdigest()
        g.bar(label + "_sha256", got == want, "%s vs results.json %s" % (got[:16], want[:16]), "the dump file's sha256 equals the one the run recorded")
    if len(rec) < 100:
        g.bar(label, False, "no samples (%d records)" % len(rec), ">= 100 records")
        return rec
    o = oracle(rec, hq, spl, e, pal)
    ok = o["bad_z"] == 0 and o["bad_gradient"] == 0 and o["violation_total"] == 0
    g.bar(label, ok, "records=%d max|dz|=%.3f mm bad_z=%d bad_gradient=%d limit violations=%s" % (o["records"], o["max_dz_mm"], o["bad_z"], o["bad_gradient"], o["violations"] or 0),
          "z within 1 mm of the class rule on the saved heights, gxq/gyq exact, every instance inside its class's splat and slope limits (margins 2/255, 0.5 deg)")
    for ex in o["examples"]:
        print("  oracle: " + ex)
    return rec


def moved_bar(g, run, res, before, after, label):
    """SX5: instances moved in z by > 5 cm between two dumps (by key): >= 1,000 grass and >= 20 coarse."""
    pa, pb = os.path.join(run, "scatter_%s.bin" % before), os.path.join(run, "scatter_%s.bin" % after)
    if not (os.path.isfile(pa) and os.path.isfile(pb)):
        g.bar(label, False, "no samples (dump %s or %s missing)" % (before, after), ">= %d grass and >= %d coarse instances moved in z by > 5 cm" % (SX5_MIN_MOVED_GRASS, SX5_MIN_MOVED_COARSE))
        return
    a, b = read_dump(pa), read_dump(pb)
    ka, kb = keys_of(a), keys_of(b)
    ia, ib = np.argsort(ka), np.argsort(kb)
    ka, kb = ka[ia], kb[ib]
    common, ca, cb = np.intersect1d(ka, kb, return_indices=True)
    za = a["zq"][ia][ca].astype(np.int64)
    zb = b["zq"][ib][cb].astype(np.int64)
    cls = a["cls"][ia][ca]
    moved = np.abs(zb - za) > SX5_MOVED_Z_M * 65536.0
    grass = int((moved & (cls == 0)).sum())
    coarse = int((moved & np.isin(cls, [CLASSES.index(c) for c in COARSE])).sum())
    g.bar(label, grass >= SX5_MIN_MOVED_GRASS and coarse >= SX5_MIN_MOVED_COARSE,
          "moved by > 5 cm between %s and %s (of %d common keys): grass %d, coarse %d (tree %d, shrub %d, fern %d, rock %d)" % (
              before, after, len(common), grass, coarse, int((moved & (cls == 4)).sum()), int((moved & (cls == 6)).sum()), int((moved & (cls == 7)).sum()),
              int((moved & (cls == 8)).sum())),
          ">= %d grass and >= %d coarse instances moved, so moved instances are what is tested" % (SX5_MIN_MOVED_GRASS, SX5_MIN_MOVED_COARSE))


# ------------------------------------------------------------------------------------------------------------------------------ images
# P7's local statistic (imgdiff.local_stat, plan C 3.8) takes the worst 16x16 block over every block holding at least one masked pixel. That is the gated
# statistic everywhere (SX10, SX17, SX18). Round 2 of S5 found the live A/A floor to be TSR and screen-space noise of the order of the bar itself
# (x_before vs x_before_aa 0.64 % changed, worst block 2.5/255) and frame-rate dependent. The fresh-rebuild pairs of SX10 and SX18 are therefore shot with
# the temporal sequences frozen (temporal_freeze op), which takes their A/A floor to about 0 (round 3 probe: 0.00000 / 0.0001), so each pair is judged
# against the P7 bar itself; local_pair fails a floor above the bar as unusable. Frozen frames compare only within one freeze window (each window pins a
# different frame index; across windows about 0.2 % of the frame differs).
# What is measured about SX17 (S5 round 4, --sx17f on S1XF, one freeze window): with TSR on, undo vs pre_last2 differs by about 12 % of the last2 footprint
# (worst block 0.025) and the difference covers tree and shadow edges across the whole frame; two frames of the SAME state in the same window, one scatter
# hide/restore apart (x_undo vs xf_undo), differ by about 5 % / 0.017; with the terrain alone (scatter hidden) undo vs pre_last2 still differs by about
# 4.5 %. ScatterApply=clear (canonical instance order) leaves it unchanged (about 14 %), and with anti-aliasing off (r.AntiAliasingMethod 0) undo vs
# pre_last2 falls to 0.000-0.014 % changed, worst block <= 0.001 (two runs), and the terrain-alone pair to exactly 0. So the heights, splat and scatter
# are restored exactly (SX2, the hashes) and the frozen-frame difference comes from TSR's history, which depends on what was on screen before; which part
# of TSR's history keeps it is not measured.
P7_MIN_BLOCK_PX = 1
# The shots each image gate judges, all taken with the temporal sequences frozen (gen_scatter_scripts.py): SX18 in S1X and S1XL, SX10 in SHADX. SX17 in S1X
# reads the unfrozen x_ shots against the unfrozen x_before / x_before_aa floor (reported); its frozen single-window variant is --sx17f on S1XF.
SX18_FROZEN_S1X = ["xf_before", "xf_before_aa"] + ["xf_%s%s" % (c, x) for c in ("after", "undo", "redo") for x in ("", "_fresh")]
SX18_FROZEN_S1XL = ["xf_redo", "xf_redo_aa", "xf_redo_fresh"]
SX10_FROZEN = ["t_a", "t_b"] + ["%s%s" % (n, x) for n in ("t_far", "t_raised", "t_undo") for x in ("", "_fresh")]
REPORT_MIN_BLOCK_PX = 64


def local_stat_px(a, b, mask, min_px=P7_MIN_BLOCK_PX, block=16):
    """imgdiff.local_stat (P7's local statistic) with the worst-block term taken over blocks holding at least min_px masked pixels. min_px=1 is P7 exactly
    (the gated reading); min_px=REPORT_MIN_BLOCK_PX is the reported variant. Also returns where the worst block is and how many masked pixels it holds."""
    d = np.abs(imgdiff.blur3(a) - imgdiff.blur3(b))
    frac = imgdiff.changed_frac(a, b, mask)
    worst, blocks, at, at_px = 0.0, 0, None, 0
    h, w = a.shape
    for y in range(0, h, block):
        for x in range(0, w, block):
            mm = mask[y:y + block, x:x + block]
            n = int(mm.sum())
            if n < max(1, min_px):
                continue
            blocks += 1
            v = float(d[y:y + block, x:x + block][mm].mean())
            if v > worst:
                worst, at, at_px = v, (x, y), n
    return {"changed_frac": frac, "worst_block_mean_abs": worst, "mean_abs": imgdiff.mean_abs(a, b, mask), "blocks": blocks, "worst_at": at, "worst_px": at_px,
            "min_px": min_px, "pass": bool(frac <= pt.P7_LOCAL_FRAC and worst <= pt.P7_LOCAL_BLOCK)}


def local_pair(g, name, apath, bpath, mask, desc, informational=False, floor_paths=None):
    """One image pair inside a mask under P7's local statistic as written (imgdiff.local_stat: changed <= 0.5 % and the worst 16x16 block holding >= 1 masked
    pixel <= 2/255). With floor_paths (the run's A/A pair, plan-c-scatter.md 5 SX10/SX17/SX18 'against the run's A/A floor') the floor is measured in the same
    mask under the same statistic and must itself be within the P7 bar: scripts that judge images freeze the renderer's temporal sequences (temporal_freeze),
    so an A/A pair of a static scene agrees to a fraction of 1/255, and a floor above the bar means the A/A procedure is not repeatable (the row then fails
    'floor unusable' instead of letting a transient loosen the limit). The pair is therefore judged against the P7 bar itself, never looser."""
    if not (os.path.isfile(apath) and os.path.isfile(bpath)):
        g.bar(name, False, "no samples (missing %s or %s)" % (os.path.basename(apath), os.path.basename(bpath)), "local statistic", informational=informational)
        return None
    A, B = imgdiff.load_luma(apath), imgdiff.load_luma(bpath)
    if A.shape != B.shape or mask is None or A.shape != mask.shape:
        g.bar(name, False, "no samples (size mismatch)", "local statistic", informational=informational)
        return None
    if int(mask.sum()) < pt.MIN_MASK_PX:
        g.bar(name, False, "no samples (mask %d px < %d)" % (int(mask.sum()), pt.MIN_MASK_PX), "local statistic", informational=informational)
        return None
    st = local_stat_px(A, B, mask)
    rule = "P7 local statistic (every block with >= 1 masked px): changed <= 0.50 % and worst 16x16 block mean |d| <= 2/255"
    fl = None
    note = ""
    if floor_paths is not None:
        fa, fb = floor_paths
        if not (os.path.isfile(fa) and os.path.isfile(fb)):
            g.bar(name, False, "no samples (A/A floor shots %s / %s missing)" % (os.path.basename(fa), os.path.basename(fb)), "local statistic against the A/A floor",
                  informational=informational)
            return None
        fl = local_stat_px(imgdiff.load_luma(fa), imgdiff.load_luma(fb), mask)
        st["floor"] = fl
        rule += "; the run's A/A floor in the same mask must itself be within that bar"
        if not fl["pass"]:
            st["pass"] = False
            within = st["changed_frac"] <= fl["changed_frac"] and st["worst_block_mean_abs"] <= fl["worst_block_mean_abs"]
            g.bar(name, False, "floor unusable: A/A floor (%s vs %s, same mask) %.5f / %.5f (%d masked px at %s) is above the P7 bar; pair %.5f / %.5f (%d masked px "
                  "at %s), %s the floor (%s)" % (os.path.basename(fa), os.path.basename(fb), fl["changed_frac"], fl["worst_block_mean_abs"], fl["worst_px"],
                                                  fl["worst_at"], st["changed_frac"], st["worst_block_mean_abs"], st["worst_px"], st["worst_at"],
                                                  "within" if within else "ABOVE", desc), rule, informational=informational)
            return st
        note = "; A/A floor (%s vs %s, same mask) %.5f / %.5f" % (os.path.basename(fa), os.path.basename(fb), fl["changed_frac"], fl["worst_block_mean_abs"])
    g.bar(name, st["pass"], "changed_frac=%.5f worst16=%.5f (%d masked px at %s) mean_abs=%.5f mask_px=%d (%s)%s" % (
        st["changed_frac"], st["worst_block_mean_abs"], st["worst_px"], st["worst_at"], st["mean_abs"], int(mask.sum()), desc, note), rule, informational=informational)
    return st


def fp_mask(run, key, shape):
    p = os.path.join(run, "footprints.json")
    if not os.path.isfile(p):
        return None
    try:
        return imgdiff.footprint_mask(pt.read_json(p), key, shape)
    except (KeyError, ValueError, OSError):
        return None


def shape_of(run, name):
    p = os.path.join(run, name + ".png")
    return imgdiff.load_luma(p).shape if os.path.isfile(p) else None


def fresh_bars(g, run, cps, label_prefix="SX18"):
    """SX18: xf_<cp> vs xf_<cp>_fresh (both shot with the temporal sequences frozen) inside the scatter mask m_<cp> of the same run under P7's statistic, with
    the run's A/A floor (xf_before vs xf_before_aa in S1X; xf_<cp> vs xf_<cp>_aa in S1XL, which has no `before`) in the same mask required to be within the
    P7 bar (local_pair)."""
    for cp in cps:
        live = "xf_" + cp
        shp = shape_of(run, live)
        if shp is None:
            g.bar("%s %s" % (label_prefix, cp), False, "no samples (%s.png missing)" % live, "local statistic inside the scatter mask")
            continue
        mask = fp_mask(run, "rts80/m_" + cp, shp)
        if mask is None:
            g.bar("%s %s" % (label_prefix, cp), False, "no samples (no mask rts80/m_%s in footprints.json)" % cp, "local statistic inside the scatter mask")
            continue
        fa, fb = os.path.join(run, "xf_before.png"), os.path.join(run, "xf_before_aa.png")
        if not (os.path.isfile(fa) and os.path.isfile(fb)):
            fa, fb = os.path.join(run, "%s.png" % live), os.path.join(run, "%s_aa.png" % live)
        local_pair(g, "%s %s" % (label_prefix, cp), os.path.join(run, "%s.png" % live), os.path.join(run, "%s_fresh.png" % live), mask,
                   "scatter mask m_%s, frozen frames" % cp, floor_paths=(fa, fb))


def row_frozen(r):
    """A temporal_freeze row left the renderer frozen only when the cvars exist and their read-back after the set says so
    (r.Test.FreezeTemporalSequences "1", r.TemporalAA.Debug.OverrideTemporalIndex "0"): a set at ECVF_SetByCode is refused when an ini or ExecCmds set the
    cvar at a higher priority, so the request alone proves nothing. A row without a read-back (an older build) is not frozen."""
    rb = r.get("readback") or {}
    return bool(r.get("value") == 1 and r.get("available") and str(rb.get("r.Test.FreezeTemporalSequences")) == "1"
                and str(rb.get("r.TemporalAA.Debug.OverrideTemporalIndex")) == "0")


def freeze_state(res):
    """at(op index) -> frozen, from the run's temporal_freeze rows: an op runs frozen when the last temporal_freeze row before it left the cvars frozen
    (row_frozen: the read-back, not the request)."""
    rows = sorted(res.get("temporal_freeze") or [], key=lambda r: int(r.get("op_index") or 0))

    def at(index):
        st = False
        for r in rows:
            if int(r.get("op_index") or 0) < index:
                st = row_frozen(r)
        return st
    return at


def freeze_window(res):
    """window(op index) -> the op_index of the temporal_freeze row that opened the frozen window the op runs in, or None when it runs unfrozen. Frozen frames
    compare only within one window (each window pins a different frame index)."""
    rows = sorted(res.get("temporal_freeze") or [], key=lambda r: int(r.get("op_index") or 0))

    def window(index):
        w = None
        for r in rows:
            if int(r.get("op_index") or 0) < index:
                if not row_frozen(r):
                    w = None
                elif w is None:
                    w = int(r.get("op_index") or 0)
        return w
    return window


def freeze_bar(g, run, res, label, names):
    """Every image an image bar judges was shot with the renderer's temporal sequences frozen (temporal_freeze op, available in this build), so its A/A floor and
    fresh pairs compare repeatable frames (task S5 round 3). `names` are the shot names (a scatter_fresh op is named by its live shot and shoots <name>_fresh).
    Not available in Shipping (the cvars are compiled out), where SX18 is reported (SX16 (e))."""
    at = freeze_state(res)
    tl = res.get("timeline") or []
    seen, bad = [], []
    for t in tl:
        nm = t.get("name")
        shot = nm if t.get("op") == "shot" else (nm + "_fresh" if t.get("op") == "scatter_fresh" and nm else None)
        if shot in names:
            seen.append(shot)
            if not at(int(t["index"])):
                bad.append(shot)
    missing = sorted(set(names) - set(seen))
    rows = [(r.get("op_index"), r.get("value"), r.get("available"), "frozen" if row_frozen(r) else "not frozen") for r in res.get("temporal_freeze") or []]
    g.bar(label, not bad and not missing, "%d shots frozen; not frozen %s; not in the timeline %s; temporal_freeze rows (op, value, available, read-back) %s" % (
        len(seen) - len(bad), bad, missing, rows or "none"),
          "every judged shot (and its fresh twin) was taken with the temporal sequences frozen (cvar read-back, not the request), so the pair compares repeatable frames")


class _ReportGates:
    """Wraps a Gates so every bar is REPORT (Shipping's SX18, plan 5 SX16 (e); the pair runs' SX18 rows)."""

    def __init__(self, g, why="Shipping: reported, SX16 (e)"):
        self.g = g
        self.why = why

    def bar(self, name, ok, value, rule, informational=False):
        self.g.bar(name, ok, value, rule + " [%s]" % self.why, informational=True)


# ------------------------------------------------------------------------------------------------------------------------------ proxy and compile events
EDIT_OPS = ("stroke", "paint", "undo", "redo", "load", "scatter")
WINDOW_FAIL_KINDS = ("compile_recreate", "engine_recreate", "pso_recreate", "recreate", "expected_rebuild")
WINDOW_REPORT_KINDS = ("refill", "first_create", "shader_propagation", "asset_post_compile", "compile_busy_frame")


def proxy_events(sc):
    """[(frame, kind name, unit)] from the scatter block, or None when the run has no event log (a build before task S5's second round)."""
    if "proxy_events" not in sc:
        return None
    kinds = sc.get("proxy_event_kinds") or []
    out = []
    for row in sc.get("proxy_events") or []:
        f, k, u = int(row[0]), int(row[1]), int(row[2])
        out.append((f, kinds[k] if 0 <= k < len(kinds) else str(k), u))
    return out


def timeline_rows(res):
    return [t for t in res.get("timeline") or [] if "frame_start" in t and "frame_end" in t]


def window_of(res, shot):
    """The frames (lo, hi) a shot's GPU state was built in: from the end of the last scatter_fresh op before `shot` (a fresh rebuild resets every proxy to the
    CPU arrays) or the start of the run, to the end of the shot op."""
    tl = timeline_rows(res)
    idx = next((i for i, t in enumerate(tl) if t.get("op") == "shot" and t.get("name") == shot), None)
    if idx is None:
        return None
    hi = int(tl[idx]["frame_end"])
    lo = next((int(t["frame_end"]) for t in reversed(tl[:idx]) if t.get("op") == "scatter_fresh"), 0)
    return lo, hi


def window_bar(g, res, sc, shot, label):
    """A shot tests partial instance updates only if no scatter component's proxy was rebuilt from the CPU arrays after its partial updates (plan 6 risk 4: an
    editor-compile, engine, PSO or unexplained recreate there would make the shot vs its fresh twin compare fresh with fresh). Per unit, inside the shot's
    window: a recreate FAILs when the unit had a partial update (an apply after its first proxy) before it and none after it up to the shot. Recreates that a
    later apply follows, or on units with no partial update in the window, are harmless and reported, as are compile propagations and busy frames."""
    ev = proxy_events(sc)
    if ev is None:
        g.bar(label, False, "no samples (results.json has no scatter.proxy_events)", "no unit's partial updates hidden by a recreate before the shot")
        return
    if not any(k == "apply" for _f, k, _u in ev):
        g.bar(label, False, "no samples (no apply events in scatter.proxy_events)", "no unit's partial updates hidden by a recreate before the shot")
        return
    w = window_of(res, shot)
    if w is None:
        g.bar(label, False, "no samples (no timeline frames for shot %s)" % shot, "no unit's partial updates hidden by a recreate before the shot")
        return
    lo, hi = w
    first = {}
    applies = {}
    for f, k, u in ev:
        if k == "first_create" and u not in first:
            first[u] = f
    for f, k, u in ev:
        if k == "apply" and u in first and f > first[u] and lo <= f <= hi:
            applies.setdefault(u, []).append(f)
    hidden, harmless, rep = [], 0, {}
    for f, k, u in ev:
        if not (lo <= f <= hi):
            continue
        if k in WINDOW_FAIL_KINDS:
            ap = applies.get(u, [])
            if any(p <= f for p in ap) and not any(p > f for p in ap):
                hidden.append((f, k, u))
            else:
                harmless += 1
        elif k in WINDOW_REPORT_KINDS:
            rep[k] = rep.get(k, 0) + 1
    g.bar(label, not hidden, "frames %d..%d; units with partial updates %d; recreates hiding a unit's partial updates %d%s; harmless recreates %d (a later apply "
          "followed, or no partial update in the window); reported %s" % (lo, hi, len(applies), len(hidden), (" e.g. %s" % hidden[:4]) if hidden else "", harmless, rep or "none"),
          "no compile, engine, PSO, unexplained or expected recreate after a unit's last partial update and before the shot (per unit; refills and first creates reported)")


def compile_events_report(g, res, sc):
    """Shader propagations, asset post-compiles, compile-busy frames and the proxy categories per phase (task note: report shader_propagations per phase)."""
    ev = proxy_events(sc)
    if ev is None:
        g.bar("proxy_events_logged", False, "no samples (results.json has no scatter.proxy_events)", "frame-stamped proxy and compile events are logged")
        return
    dropped = int(sc.get("proxy_events_dropped") or 0)
    g.bar("proxy_events_logged", dropped == 0, "%d events, %d dropped" % (len(ev), dropped), "every proxy and compile event is in the log (none dropped past the bound)")
    tl = timeline_rows(res)
    phases = {}
    sp = res.get("script_path")
    phase_note = ""
    try:
        import hashlib
        with open(sp, "rb") as f:
            raw = f.read()
        if res.get("script_sha256") and hashlib.sha256(raw).hexdigest().lower() != str(res.get("script_sha256")).lower():
            # the script file changed since the run: its op list would label the phases wrongly
            phase_note = "phases unavailable (script changed since the run); "
        else:
            ops = json.loads(raw.decode("utf-8")).get("ops") or []
            cur = "default"
            for i, o in enumerate(ops):
                if o.get("phase"):
                    cur = o["phase"]
                phases[i + 1] = cur
    except (OSError, TypeError, ValueError):
        phase_note = "phases unavailable (script %s unreadable); " % sp
    starts = [int(t["frame_start"]) for t in tl]
    import bisect
    per = {}
    for f, k, _u in ev:
        if k in ("first_create", "expected_rebuild", "apply"):
            continue
        i = bisect.bisect_right(starts, f) - 1
        t = tl[i] if 0 <= i < len(tl) else None
        ph = phases.get(int(t["index"]), "?") if t else "before_ops"
        key = "%s" % ph
        d = per.setdefault(key, {})
        d[k] = d.get(k, 0) + 1
    g.bar("compile_events_per_phase", True, "%s%s" % (phase_note, per or "none"), "shader propagations, asset post-compiles, compile-busy frames and later proxies per script phase "
          "(refills, PSO, engine and editor-compile recreates; reported)", informational=True)


# ------------------------------------------------------------------------------------------------------------------------------ SX9 assets and licences
SCATTER_REPORT = os.path.join(os.path.dirname(HERE), "Out", "scatter_assets", "report.json")
SCATTER_MANIFEST = os.path.join(os.path.dirname(HERE), "ScatterSrc", "manifest.json")
LICENCES_OK = ("CC0-1.0", "project-original", "epic")


def asset_bars(g, res, report_path=None, manifest_path=None):
    """plan-c-scatter.md 3.8 parser rule and 5 SX9: usage_ok, deps_ok, flip_ok, vc_ok and errors 0 in S3's asset report, per-instance random false on every
    master material, and the licence bar: every mesh path the run loaded maps through report.json to a ScatterSrc/manifest.json row with licence CC0-1.0,
    project-original or epic. A missing report or manifest is 'no samples' (FAIL)."""
    report_path = report_path or SCATTER_REPORT
    manifest_path = manifest_path or SCATTER_MANIFEST
    opt = ((res.get("options") or {}).get("scatter")) or {}
    meshes = opt.get("meshes") or {}
    try:
        rep = pt.read_json(report_path)
    except (OSError, ValueError) as ex:
        g.bar("SX9 asset_report", False, "no samples (%s: %s)" % (report_path, ex), "S3's report.json")
        g.bar("SX9 licence", False, "no samples (no report.json)", "every loaded mesh maps to a manifest row with an allowed licence")
        return
    ck = rep.get("checks") or {}
    uo = str(ck.get("usage_ok") or "")
    um = uo.split("/")
    usage = len(um) == 2 and um[0] == um[1] and um[0].isdigit() and int(um[0]) > 0
    errs = len(rep.get("errors") or [])
    ok = usage and ck.get("deps_ok") is True and ck.get("flip_ok") is True and ck.get("vc_ok") is True and int(ck.get("errors", 1)) == 0 and errs == 0
    g.bar("SX9 asset_report", ok, "usage_ok=%s deps_ok=%s flip_ok=%s vc_ok=%s errors=%s (%d listed); asset_settings_sha256 %s" % (
        uo, ck.get("deps_ok"), ck.get("flip_ok"), ck.get("vc_ok"), ck.get("errors"), errs, (rep.get("asset_settings_sha256") or "")[:16]),
          "S3's asset report: usage_ok k/k, deps_ok, flip_ok, vc_ok, errors 0")
    tags = rep.get("material_tags") or {}
    rnd = [m for m, t in tags.items() if str((t or {}).get("HasPerInstanceRandom")).lower() != "false"]
    g.bar("SX9 per_instance_random", bool(tags) and not rnd, "%d materials, reading PerInstanceRandom: %s" % (len(tags), rnd or "none"),
          "no scatter material reads PerInstanceRandom (F25)")
    try:
        man = pt.read_json(manifest_path)
    except (OSError, ValueError) as ex:
        g.bar("SX9 licence", False, "no samples (%s: %s)" % (manifest_path, ex), "every loaded mesh maps to a manifest row with an allowed licence")
        return
    rows = man.get("rows") if isinstance(man, dict) else man
    by_id = {}
    for r in rows or []:
        if r.get("kind") == "mesh":
            by_id[(r.get("id"), r.get("level"))] = r
    rmeshes = rep.get("meshes") or {}
    usage_rows = rep.get("usage_rows") or []
    vc_rows = {}
    for v in rep.get("vc_rows") or []:
        # a mesh may have several rows (source and render LOD): it is ok only when every row is
        prev = vc_rows.get(v.get("path"))
        vc_rows[v.get("path")] = dict(v, ok=bool(v.get("ok")) and (prev is None or bool(prev.get("ok"))))
    vc_expect = rep.get("vc_expect") or {}
    bad, rows_out = [], []
    if not meshes:
        g.bar("SX9 licence", False, "no samples (options.scatter.meshes is empty)", "every loaded mesh maps to a manifest row with an allowed licence")
        return
    for slot, m in sorted(meshes.items()):
        path = (m or {}).get("path") or ""
        pkg = path.split(".")[0]
        rr = rmeshes.get(pkg)
        if not path:
            continue   # a slot this level does not load (the actor records no path)
        if rr is None:
            bad.append("%s: %s not in report.json" % (slot, pkg))
            continue
        mid = pkg.rsplit("/", 1)[-1]
        mrow = by_id.get((mid, rr.get("level")))
        if mrow is None:
            bad.append("%s: no manifest row id=%s level=%s" % (slot, mid, rr.get("level")))
            continue
        lic = mrow.get("licence")
        if lic not in LICENCES_OK:
            bad.append("%s: licence %s" % (slot, lic))
        ur = [u for u in usage_rows if u.get("mesh") == pkg]
        if not ur or not all(u.get("ok") for u in ur):
            bad.append("%s: usage rows %s" % (slot, [(u.get("slot"), u.get("ok")) for u in ur] or "none"))
        vc = vc_rows.get(pkg)
        if rr.get("vertex_colours_needed"):
            # S3's report proves vertex colours two ways: byte-exact vc_rows for sampled meshes, and vc_expect's coverage of every mesh that needs them
            # (meshes_needing_vc == meshes_needing_vc_with_vc, missing []). A mesh that needs them passes with an ok vc row, or with no row when
            # vc_expect covers it; with neither it fails (a missing row is never silently accepted).
            if vc is not None:
                if not vc.get("ok"):
                    bad.append("%s: vertex colours not ok" % slot)
            elif not (vc_expect and int(vc_expect.get("meshes_needing_vc", -1)) == int(vc_expect.get("meshes_needing_vc_with_vc", -2))
                      and not any(m in (pkg, mid, slot) for m in (vc_expect.get("missing") or []))):
                bad.append("%s: needs vertex colours but has no vc row and report.json vc_expect does not cover it" % slot)
        rows_out.append("%s=%s" % (slot, lic))
    g.bar("SX9 licence", not bad and bool(rows_out), "; ".join(bad) if bad else "%d meshes: %s" % (len(rows_out), ", ".join(rows_out)),
          "every loaded mesh maps through report.json to a manifest row with licence in %s, with its usage rows ok" % (LICENCES_OK,))


# ------------------------------------------------------------------------------------------------------------------------------ extra --scatter rows
def saved_state_dumps(res, sc):
    """Dumps taken at the state the run saved: no stroke, paint, undo, redo, load or scatter toggle between the dump op and the `save` op."""
    tl = res.get("timeline") or []
    save_idx = [t["index"] for t in tl if t.get("op") == "save"]
    if not save_idx:
        return []
    last_save = max(save_idx)
    out = []
    for d in sc.get("dumps") or []:
        i = int(d.get("op_index") or 0)
        if i <= 0 or i > last_save:
            continue
        between = [t for t in tl if i < t["index"] < last_save and t.get("op") in EDIT_OPS]
        if not between:
            out.append(d.get("name"))
    return out


def scatter_extra(g, run, res, sc):
    """Rows pt.scatter adds for task S5: SX9's asset and licence bars, the dump oracle (SX5) on every dump taken at the saved state, the full up-axis
    check per verify, and the compile events per phase."""
    if res is None:
        return
    asset_bars(g, res)
    vs = sc.get("verifies") or []
    # Plan 3.8: every instance's full up axis is compared with HF's (rocks included). A build whose verify compared near-vertical rocks by tilt angle only
    # (S5 round 2, `up_degenerate_angle_only`) fails here; the fixed build has no such count.
    relaxed = [(v.get("name"), v.get("up_degenerate_angle_only")) for v in vs if v.get("up_degenerate_angle_only")]
    g.bar("scatter_verify_full_up_axis", bool(vs) and not relaxed, ("angle-only verifies %s" % relaxed) if relaxed else
          "; ".join("%s max_dup %.2e rad" % (v.get("name"), v.get("max_dup_rad") or 0.0) for v in vs) or "no samples (no verifies)",
          "every verify compares each instance's full up axis with HF's cell-triangle rule (no angle-only exemption)")
    have_saved = all(os.path.isfile(os.path.join(run, f)) for f in pt.SAVED_FILES)
    names = saved_state_dumps(res, sc) if have_saved else []
    for nm in names:
        dump_bar(g, run, res, nm, "SX5 oracle %s" % nm)
    others = [d.get("name") for d in sc.get("dumps") or [] if d.get("name") not in names]
    if others or not names:
        g.bar("SX5 oracle (other dumps)", True, "dumps not at the saved state or no saved files: %s" % (others or "no dumps"), "the oracle needs the dump's own heights", informational=True)
    compile_events_report(g, res, sc)


# ------------------------------------------------------------------------------------------------------------------------------ --dump-diff
def dump_diff(run_a, run_b, name, name_b=None):
    """Diff two scatter dumps by key (plan 3.8: 'dumps diff by key for any SX4 or SX16 failure'): keys only in A, only in B, and per-field differences."""
    name_b = name_b or name
    pa, pb = os.path.join(run_a, "scatter_%s.bin" % name), os.path.join(run_b, "scatter_%s.bin" % name_b)
    if not (os.path.isfile(pa) and os.path.isfile(pb)):
        print("DUMP-DIFF no samples (missing %s or %s)" % (pa, pb))
        return 2
    a, b = read_dump(pa), read_dump(pb)
    ka, kb = keys_of(a), keys_of(b)
    ia, ib = np.argsort(ka, kind="stable"), np.argsort(kb, kind="stable")
    a, b, ka, kb = a[ia], b[ib], ka[ia], kb[ib]
    common, ca, cb = np.intersect1d(ka, kb, return_indices=True)
    only_a = np.setdiff1d(ka, kb)
    only_b = np.setdiff1d(kb, ka)
    fields = [f for f in REC_DTYPE.names]
    diff_any = np.zeros(len(common), dtype=bool)
    per = {}
    for f in fields:
        d = a[f][ca] != b[f][cb]
        if d.any():
            per[f] = int(d.sum())
        diff_any |= d
    print("DUMP-DIFF %s/%s vs %s/%s: records %d vs %d, common keys %d, only in A %d, only in B %d, common with any field different %d, per field %s" % (
        os.path.basename(run_a), name, os.path.basename(run_b), name_b, len(a), len(b), len(common), len(only_a), len(only_b), int(diff_any.sum()), per))
    for k in only_a[:5]:
        r = a[np.searchsorted(ka, k)]
        print("  only A: key 0x%016x %s (%.3f, %.3f)" % (int(k), CLASSES[int(r["cls"])], r["xq"] / 65536.0, r["yq"] / 65536.0))
    for k in only_b[:5]:
        r = b[np.searchsorted(kb, k)]
        print("  only B: key 0x%016x %s (%.3f, %.3f)" % (int(k), CLASSES[int(r["cls"])], r["xq"] / 65536.0, r["yq"] / 65536.0))
    for j in np.nonzero(diff_any)[0][:5]:
        ra, rb = a[ca[j]], b[cb[j]]
        print("  differs: key 0x%016x %s: %s" % (int(common[j]), CLASSES[int(ra["cls"])], {f: (int(ra[f]), int(rb[f])) for f in fields if ra[f] != rb[f]}))
    same = len(only_a) == 0 and len(only_b) == 0 and not diff_any.any()
    print("DUMP-DIFF %s" % ("EQUAL" if same else "DIFFER"))
    return 0 if same else 1


# ------------------------------------------------------------------------------------------------------------------------------ S1X
def class_counts(res, name):
    c = hsh(res, name).get("scatter_classes")
    return c


def floors_bar(g, res, level):
    c = class_counts(res, "before")
    if not c:
        g.bar("SX1 floors", False, "no samples (hash `before` has no scatter_classes)", "every enabled class's count >= half its plan 3.4 flat-map EST")
        return
    rows, bad = [], []
    fl = list(FLAT_FLOORS) + ([(("NearCard",), NEARCARD_FLOOR)] if level == "L1" else [])
    for names, floor in fl:
        v = sum(int(c.get(x, 0)) for x in names)
        rows.append("%s %d>=%d" % ("+".join(names), v, floor))
        if v < floor:
            bad.append("+".join(names))
    g.bar("SX1 floors", not bad, "; ".join(rows) + ("; BELOW: %s" % bad if bad else ""), "every enabled class's flat-map count >= half its EST, so the identity bars cannot pass empty")


def scatter_cfg_equal(g, name, runs):
    cfgs = [(r, pt.scatter_config(res)) for r, res in runs]
    ok = all(c == cfgs[0][1] and c is not None for _r, c in cfgs)
    g.bar(name, ok, "equal" if ok else "config mismatch: %s" % [(os.path.basename(r), (c or (None,))[0]) for r, c in cfgs],
          "config_fnv, level and mesh list equal, else FAIL config mismatch (fix the run, never the bar)")
    return ok


def sg_equal(g, name, runs):
    sgs = [(r, sg_of(res)) for r, res in runs]
    ok = all(s == sgs[0][1] and s is not None for _r, s in sgs)
    g.bar(name, ok, "equal" if ok else "sg mismatch: %s" % [(os.path.basename(r), s) for r, s in sgs],
          "scalability groups (sg.*) equal across every compared pair, else FAIL sg mismatch")
    return ok


def compared_equal(g, compared, xref=None):
    """Config and sg.* equality across every run S1X compares (plan 3.8: 'Every pair of runs compared (C1 vs C1S, S1X vs pkg/ship S1X) must have equal sg.*';
    a scatter-hash comparison with a different config_fnv, level or mesh list FAILs config mismatch). With --xref (the packaged path) s1x_final joins them."""
    compared = list(compared)
    if xref:
        rx = pt.load_results(xref)
        if rx is None:
            g.bar("xref", False, "results.json missing in %s" % xref, "the --xref S1X run (s1x_final)")
        else:
            compared.append((xref, rx))
    scatter_cfg_equal(g, "scatter_config", compared)
    sg_equal(g, "sg_equal", compared)


def pair_roles_bar(g, pair_results):
    """Plan 5 SX4: beside s1x_a, exactly one ScatterThreads=0 run (s1x_b) and one ScatterDuringStroke=0 run (s1x_c), so each timing path really ran."""
    roles = []
    for res in pair_results:
        o = ((res.get("options") or {}).get("scatter")) or {}
        roles.append("threads=0" if o.get("threads") == 0 else "during_stroke=0" if o.get("during_stroke_ms") == 0 else "other")
    g.bar("SX4 pair_roles", sorted(roles) == ["during_stroke=0", "threads=0"], "pairs %s" % roles,
          "exactly one ScatterThreads=0 pair and one ScatterDuringStroke=0 pair beside s1x_a")


def s1x(a, pairs, reload_run, ref, xref=None, packaged=False, shipping=False):
    g = pt.Gates()
    runs = [("a", a)] + [("pair%d" % i, p) for i, p in enumerate(pairs)] + ([("reload", reload_run)] if reload_run else [])
    results = {}
    for tag, r in runs:
        results[tag] = pt.load_results(r)
        if results[tag] is None:
            g.bar("results_" + tag, False, "results.json missing in %s" % r, "results.json exists")
    if any(v is None for v in results.values()):
        print("S1X FAIL")
        return g
    ra = results["a"]
    rref = pt.load_results(ref)
    if rref is None:
        g.bar("reference", False, "results.json missing in %s" % ref, "the S1 reference run")
        print("S1X FAIL")
        return g
    opt = ((ra.get("options") or {}).get("scatter")) or {}
    # SX1, SX8, SX9 on every run: the shared scatter bars (verify, hashes, counters, proxies, log scan).
    for tag, r in runs:
        sub(g, "SX1/SX8/SX9 %s" % tag, pt.scatter, r, shipping=shipping)
    floors_bar(g, ra, opt.get("level"))
    compared_equal(g, [(r, results[tag]) for tag, r in runs], xref)
    # SX2
    for tag, r in runs[:1] + runs[1:-1 if reload_run else None]:
        res = results[tag]
        u, p, rd, af = hsh(res, "undo"), hsh(res, "pre_last2"), hsh(res, "redo"), hsh(res, "after")
        ok = bool(u.get("scatter_live_fnv")) and u.get("scatter_live_fnv") == p.get("scatter_fnv") and rd.get("scatter_live_fnv") == af.get("scatter_fnv") \
            and p.get("scatter_fnv") != af.get("scatter_fnv")
        g.bar("SX2 undo/redo %s" % tag, ok, "undo live %s vs pre_last2 %s; redo live %s vs after %s; after != pre_last2" % (u.get("scatter_live_fnv"), p.get("scatter_fnv"),
              rd.get("scatter_live_fnv"), af.get("scatter_fnv")), "live at undo = reference at pre_last2, at redo = at after (a function of the terrain, not of history)")
    # SX3
    if reload_run:
        rl = results["reload"]
        l, want = hsh(rl, "loaded"), hsh(ra, "redo")
        k = scatter_block(rl).get("counters") or {}
        g.bar("SX3 reload", bool(l.get("scatter_fnv")) and l.get("scatter_fnv") == want.get("scatter_fnv") and l.get("scatter_live_fnv") == want.get("scatter_fnv")
              and l.get("height_fnv") == want.get("height_fnv") and l.get("splat_fnv") == want.get("splat_fnv") and (k.get("discarded_epoch") or 0) >= 1,
              "loaded ref %s live %s vs the reference run's redo %s; terrain hashes %s; discarded_epoch=%s" % (l.get("scatter_fnv"), l.get("scatter_live_fnv"), want.get("scatter_fnv"),
              "equal" if (l.get("height_fnv"), l.get("splat_fnv")) == (want.get("height_fnv"), want.get("splat_fnv")) else "DIFFER", k.get("discarded_epoch")),
              "live and reference at `loaded` = the reference run's final; discarded_epoch >= 1 (the load really cut a fill)")
        loaded = (rl.get("loaded") or {}).get("dir", "")
        g.bar("SX3 loaded_dir", os.path.normcase(os.path.abspath(loaded)) == os.path.normcase(os.path.abspath(xref if (packaged and xref) else a)), loaded,
              "the load read the reference S1X run's saved files")
    # SX4
    b_runs = [(tag, r) for tag, r in runs if tag.startswith("pair")]
    if not packaged:
        pair_roles_bar(g, [results[t] for t, _r in b_runs])
    shared = None
    for tag, r in [("a", a)] + b_runs:
        names = {n for n, h in ((results[tag].get("hashes") or {}).items()) if "scatter_live_fnv" in h}
        shared = names if shared is None else shared & names
    shared = sorted(shared or [])
    neq = [n for n in shared if len({hsh(results[t], n).get("scatter_live_fnv") for t, _r in [("a", a)] + b_runs}) != 1]
    g.bar("SX4 live_fnv_equal", len(shared) >= 4 and not neq, "differ at %s" % neq if neq else "equal at %d shared names %s across %d runs" % (len(shared), shared, 1 + len(b_runs)),
          "scatter_live_fnv equal at every shared hash name across the three S1X runs")
    pa = scatter_block(ra).get("path_proofs") or {}
    g.bar("SX4 paths_a", (pa.get("mid_stroke_dispatches") or 0) > 0 and (pa.get("stale_result_redispatches") or 0) > 0 and pa.get("in_flight_max") == 2,
          "mid_stroke_dispatches=%s stale_result_redispatches=%s in_flight_max=%s" % (pa.get("mid_stroke_dispatches"), pa.get("stale_result_redispatches"), pa.get("in_flight_max")),
          "s1x_a: mid_stroke_dispatches > 0, stale_result_redispatches > 0, in_flight_max = 2")
    for tag, r in b_runs:
        res = results[tag]
        pp = scatter_block(res).get("path_proofs") or {}
        o = ((res.get("options") or {}).get("scatter")) or {}
        if o.get("threads") == 0:
            g.bar("SX4 paths_%s" % tag, (pp.get("gt_generations") or 0) > 0 and pp.get("in_flight_max") == 0,
                  "threads=0 gt_generations=%s in_flight_max=%s" % (pp.get("gt_generations"), pp.get("in_flight_max")), "ScatterThreads=0 run: gt_generations > 0, in_flight_max = 0")
            sub(g, "SX4 same_hash a/%s" % tag, pt.same_hash, a, r)
        else:
            g.bar("SX4 paths_%s" % tag, (pp.get("mid_stroke_dispatches") or 0) == 0 and o.get("during_stroke_ms") == 0,
                  "during_stroke_ms=%s mid_stroke_dispatches=%s" % (o.get("during_stroke_ms"), pp.get("mid_stroke_dispatches")), "ScatterDuringStroke=0 run: mid_stroke_dispatches = 0")
    # SX5 (S1X: before -> redo moved instances, the oracle on the redo dump)
    dump_bar(g, a, ra, "redo", "SX5 oracle redo")
    moved_bar(g, a, ra, "before", "redo", "SX5 moved")
    # SX7
    for tag, r in runs:
        if tag == "reload":
            continue
        if packaged:
            # plan 5 SX16 (b): a packaged run's heights may differ from the editor build's; SX7 is the -game bar, so it is reported here.
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                eg = pt.equal_hashes(ref, r)
            g.bar("SX7 equal_hashes ref/%s" % tag, eg.ok, "%s (packaged: reported; SX16 (a)/(b) gate)" % ("PASS" if eg.ok else "FAIL"), "--equal-hashes against s1_a", informational=True)
            continue
        sub(g, "SX7 equal_hashes ref/%s" % tag, pt.equal_hashes, ref, r)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        s1g, _rr, _ok2 = pt.s1(a)
    sx7 = [r for r in s1g.rows if r["bar"].startswith("P5") or r["bar"].startswith("P11")]
    gated = [r for r in sx7 if r["status"] != "REPORT"]
    g.bar("SX7 P5/P11 on S1X", bool(gated) and all(r["pass"] for r in gated), "%d bars: %s" % (len(gated), ", ".join("%s %s" % (r["bar"], r["status"]) for r in gated if not r["pass"]) or "all pass"),
          "collision (P5) and depth (P11) bars pass with scatter on", informational=packaged)
    # SX17 (reported): the image statistics against S1X's own floor, and the ghost check
    shp = shape_of(a, "x_after")
    if shp is not None:
        fpm = None
        p = os.path.join(a, "footprints.json")
        if os.path.isfile(p):
            fp = pt.read_json(p)
            try:
                fpm = pt.footprint_union(fp, ["rts80/last2_after", "rts80/last2_flat"], shp)
            except (KeyError, ValueError):
                fpm = None
        if fpm is not None:
            # Reported (plan 5 SX17: "a floor that fails goes to Alec as is, never loosened"): P7's statistic as written; the A/A floor row against the P7
            # bar, the three pairs against S1X's own floor (x_before / x_before_aa in the same footprint).
            flp = (os.path.join(a, "x_before.png"), os.path.join(a, "x_before_aa.png"))
            local_pair(g, "SX17 aa_floor", flp[0], flp[1], fpm, "last2 footprint; x_before vs x_before_aa against the P7 bar", informational=True)
            local_pair(g, "SX17 undo_vs_pre_last2", os.path.join(a, "x_undo.png"), os.path.join(a, "x_pre_last2.png"), fpm, "last2 footprint", informational=True, floor_paths=flp)
            local_pair(g, "SX17 redo_vs_after", os.path.join(a, "x_redo.png"), os.path.join(a, "x_after.png"), fpm, "last2 footprint", informational=True, floor_paths=flp)
            if reload_run:
                local_pair(g, "SX17 ghost_a_vs_reload", os.path.join(a, "x_redo.png"), os.path.join(reload_run, "x_redo.png"), fpm, "last2 footprint; s1x_a x_redo vs s1xl x_redo",
                           informational=True, floor_paths=flp)
        else:
            g.bar("SX17", False, "no footprint masks last2_after/last2_flat", "reported", informational=True)
    # scatter_check per stroke set at `redo` (reported; THINX gates the thinning)
    for c in scatter_block(ra).get("checks") or []:
        rows = []
        for cl in ("Grass", "Tussock", "Flower", "Tree", "Rock"):
            r = (c.get("classes") or {}).get(cl) or {}
            if r.get("core_pure") or r.get("core_live") or r.get("band_live"):
                rows.append("%s core %d/%d band %d" % (cl, r.get("core_live", 0), r.get("core_pure", 0), r.get("band_live", 0)))
        g.bar("S1X scatter_check %s" % c.get("name"), True, "; ".join(rows) or "no instances", "live / pure-grass yield in the set's core (reported)", informational=True)
    # SX15 (reported): the analytic view counts of the three look poses against the plan's caps
    for v in scatter_block(ra).get("view_counts") or []:
        g.bar("SX15 caps %s" % v.get("name"), True, "analytic: drawn %s (cap %d), primitives in view %s of %s with instances (cap %d)" % (
            v.get("drawn_total"), SX15_DRAWN_MAX, v.get("primitives_in_view"), v.get("primitives_with_instances"), SX15_PRIMS_MAX),
            "caps of plan 3.7 (stated for rts80; %s reported against the same caps): instance origins in the frustum and inside their cull end, no occlusion, no LOD" % (
                v.get("pose") or v.get("name")), informational=True)
        g.bar("SX15 triangles %s" % v.get("name"), True, tris_verdict(v, opt.get("level") or "L0", opt.get("meshes"))[1],
              "plan 3.7 cap: LOD-weighted triangles <= %d (reported)" % SX15_TRIS_MAX, informational=True)
    # SX12 rows read here too: undo/redo latency
    lat = [l for l in scatter_block(ra).get("latency") or [] if l.get("reason") in ("undo", "redo")]
    if lat:
        mx = max(l.get("ms", 0) for l in lat) / 1000.0
        g.bar("SX12 undo/redo latency", mx <= SX12_UNDO_REDO_S, "n=%d max %.3f s: %s" % (len(lat), mx, [(l.get("reason"), round(l.get("ms", 0))) for l in lat]),
              "undo and redo latency <= %.1f s (max, n = 2)" % SX12_UNDO_REDO_S, informational=True)
    # SX18 (Shipping: reported, SX16 (e))
    gs = g if not shipping else _ReportGates(g)
    s1x_frozen = SX18_FROZEN_S1X
    freeze_bar(gs, a, ra, "SX18 temporal_freeze a", s1x_frozen)
    fresh_bars(gs, a, ("after", "undo", "redo"))
    for cp in ("after", "undo", "redo"):
        window_bar(gs, ra, scatter_block(ra), "xf_" + cp, "SX18 %s compile window" % cp)
    # Reported: the same SX18 rows on the pair runs (s1x_b capped at 20 fps, s1x_c), so the oracle is shown not to depend on frame rate or timing path.
    for tag, r in b_runs:
        rg = _ReportGates(g, "reported: a pair run")
        freeze_bar(rg, r, results[tag], "SX18 temporal_freeze %s (reported)" % tag, s1x_frozen)
        fresh_bars(rg, r, ("after", "undo", "redo"), "SX18[%s] (reported)" % os.path.basename(os.path.normpath(r)))
    if reload_run:
        freeze_bar(gs, reload_run, results["reload"], "SX18 temporal_freeze reload", SX18_FROZEN_S1XL)
        fresh_bars(gs, reload_run, ("redo",), "SX18 loaded")
        window_bar(gs, results["reload"], scatter_block(results["reload"]), "xf_redo", "SX18 loaded redo compile window")
    if packaged:
        sx16(g, a, ra, reload_run, results.get("reload"), xref, shipping, ref)
    ok = g.ok
    print("S1X %s" % ("PASS" if ok else "FAIL"))
    return g


def sx16(g, a, ra, reload_run, rl, xref, shipping, ref):
    """SX16 (packaged, plan 5): (a) the load path equals s1x_final's final unconditionally, (b) the generation path equals it when the heights do, (c) SX1/SX5 at
    every verify (the groups above), (d) the render proof, (e) Development also meets SX8, SX9 and SX18 (the groups above)."""
    rx = pt.load_results(xref) if xref else None
    if rx is None:
        g.bar("SX16 xref", False, "--xref run missing or without results.json", "s1x_final's results.json")
        return
    want = hsh(rx, "redo")
    final = want if want else hsh(rx, "after")
    if rl is not None:
        l = hsh(rl, "loaded")
        g.bar("SX16a load", bool(l.get("scatter_fnv")) and l.get("scatter_fnv") == final.get("scatter_fnv") and l.get("scatter_live_fnv") == final.get("scatter_fnv"),
              "loaded ref %s live %s vs s1x_final redo %s" % (l.get("scatter_fnv"), l.get("scatter_live_fnv"), final.get("scatter_fnv")),
              "scatter_fnv and scatter_live_fnv at `loaded` equal s1x_final's final, unconditionally")
    mine = hsh(ra, "redo")
    if mine.get("height_fnv") == final.get("height_fnv"):
        g.bar("SX16b generation", mine.get("scatter_fnv") == final.get("scatter_fnv") and mine.get("scatter_live_fnv") == final.get("scatter_fnv"),
              "heights equal; scatter ref %s live %s vs %s" % (mine.get("scatter_fnv"), mine.get("scatter_live_fnv"), final.get("scatter_fnv")),
              "the heights equal s1x_final's, so scatter_fnv must too")
    else:
        g.bar("SX16b generation", True, "heights differ (%s vs %s): scatter_fnv reported only, with max_abs_dh per C11" % (mine.get("height_fnv"), final.get("height_fnv")),
              "heights differ from the editor build's: reported", informational=True)
    # (d) the render proof: the scatter changes the frame, and what it draws is vegetation, not the grey default material
    def proof(run):
        on, off = os.path.join(run, "x_rts80_full.png"), os.path.join(run, "x_rts80_full_off.png")
        if not (os.path.isfile(on) and os.path.isfile(off)):
            return None
        from PIL import Image
        A, B = imgdiff.load_luma(on), imgdiff.load_luma(off)
        m = fp_mask(run, "rts80/m_redo", A.shape)
        if m is None or int(m.sum()) < pt.MIN_MASK_PX:
            return None
        ch = imgdiff.changed_frac(A, B, m)
        veg = imgdiff.changed(A, B) & m
        im = np.asarray(Image.open(on).convert("RGB"), dtype=np.float64) / 255.0
        mx, mn = im.max(axis=2), im.min(axis=2)
        sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-9), 0.0)
        hue = np.zeros_like(mx)
        d = np.maximum(mx - mn, 1e-9)
        r, gg, b = im[..., 0], im[..., 1], im[..., 2]
        hue = np.where(mx == r, ((gg - b) / d) % 6, np.where(mx == gg, (b - r) / d + 2, (r - gg) / d + 4)) * 60.0
        n = int(veg.sum())
        if n < pt.MIN_MASK_PX:
            return {"changed": ch, "n": n, "sat": 0.0, "hue": 0.0}
        ang = np.deg2rad(hue[veg])
        return {"changed": ch, "n": n, "sat": float(sat[veg].mean()), "hue": float(np.rad2deg(np.arctan2(np.sin(ang).mean(), np.cos(ang).mean())) % 360.0)}
    mine_p, ref_p = proof(a), proof(xref)
    if mine_p is None or ref_p is None:
        g.bar("SX16d render proof", False, "no samples (x_rts80_full / x_rts80_full_off / mask m_redo missing in %s or %s)" % (a, xref), "render proof")
    else:
        dh = abs((mine_p["hue"] - ref_p["hue"] + 180.0) % 360.0 - 180.0)
        ok = mine_p["changed"] >= SX16_CHANGED_RATIO * ref_p["changed"] and mine_p["sat"] >= SX16_SAT_RATIO * ref_p["sat"] and dh <= SX16_HUE_DEG and mine_p["n"] >= pt.MIN_MASK_PX
        g.bar("SX16d render proof", ok, "changed in the scatter mask %.4f vs %.4f (>= %.1fx); vegetation px %d, mean saturation %.3f vs %.3f (>= %.1fx), mean hue %.1f vs %.1f deg (within %.0f)" % (
            mine_p["changed"], ref_p["changed"], SX16_CHANGED_RATIO, mine_p["n"], mine_p["sat"], ref_p["sat"], SX16_SAT_RATIO, mine_p["hue"], ref_p["hue"], SX16_HUE_DEG),
            "on vs off changes the scatter mask >= 0.3x the editor build's, and the vegetation is as green (saturation >= 0.5x, hue within 8 deg): the default material is grey")


# ------------------------------------------------------------------------------------------------------------------------------ SX17 frozen (S1XF)
SX17F_WINDOW_SHOTS = ["x_before", "x_before_aa", "x_pre_last2", "x_after", "x_undo", "x_redo", "xo_pre_last2", "xo_after", "xo_undo", "xo_redo",
                      "xf_undo", "xf_undo_fresh"]


def exec_cvar(run, name):
    """The value a run's command line set for a console variable through -ExecCmds (None when it set none)."""
    p = os.path.join(run, "cmdline.txt")
    try:
        txt = open(p, encoding="utf-8", errors="replace").read()
    except OSError:
        return None
    m = re.search(r'-ExecCmds="([^"]*)"', txt)
    for item in (m.group(1).split(",") if m else []):
        k, _sp, v = item.strip().partition(" ")
        if k == name:
            return v.strip()
    return None


def sx17f_run(g, run):
    """One S1XF run: the measurement's validity (gated: completed, every compared shot in ONE frozen window by the cvar read-back, SX2 and the terrain hashes
    restored at undo and redo), then SX17's pairs under P7's statistic in the last2 footprint (reported), with scatter (x_) and with scatter hidden (xo_),
    the same-state control x_undo vs xf_undo (same window, a scatter hide/restore apart) and the in-window SX18 pair xf_undo vs xf_undo_fresh."""
    tag = os.path.basename(os.path.normpath(run))
    res = pt.load_results(run)
    if res is None:
        g.bar("SX17F %s results" % tag, False, "results.json missing in %s" % run, "results.json")
        return
    g.bar("SX17F %s completed" % tag, bool(res.get("completed")) and res.get("script") == "S1XF", "script %s completed %s ops %s/%s" % (
        res.get("script"), res.get("completed"), res.get("ops_done"), res.get("ops_total")), "an S1XF run that completed")
    o = ((res.get("options") or {}).get("scatter")) or {}
    aa = exec_cvar(run, "r.AntiAliasingMethod")
    cfg = "apply=%s, anti-aliasing %s" % (o.get("apply"), "r.AntiAliasingMethod %s (diagnosis)" % aa if aa is not None else "project default (TSR)")
    window = freeze_window(res)
    tl = res.get("timeline") or []
    wins, missing = {}, []
    for nm in SX17F_WINDOW_SHOTS:
        idx = None
        for t in tl:
            shot = t.get("name") if t.get("op") == "shot" else ((t.get("name") or "") + "_fresh" if t.get("op") == "scatter_fresh" else None)
            if shot == nm:
                idx = int(t["index"])
        if idx is None:
            missing.append(nm)
        else:
            wins[nm] = window(idx)
    ws = set(wins.values())
    g.bar("SX17F %s one_window" % tag, not missing and len(ws) == 1 and None not in ws, "windows (opening temporal_freeze op) %s; missing %s" % (
        sorted(ws, key=str), missing or "none"), "every compared frame was shot frozen (cvar read-back) inside ONE freeze window (frozen frames compare only within one window)")
    u, p, rd, af = hsh(res, "undo"), hsh(res, "pre_last2"), hsh(res, "redo"), hsh(res, "after")
    ok = bool(u.get("scatter_live_fnv")) and u.get("scatter_live_fnv") == p.get("scatter_fnv") and rd.get("scatter_live_fnv") == af.get("scatter_fnv") \
        and (u.get("height_fnv"), u.get("splat_fnv")) == (p.get("height_fnv"), p.get("splat_fnv")) and (rd.get("height_fnv"), rd.get("splat_fnv")) == (af.get("height_fnv"), af.get("splat_fnv")) \
        and u.get("height_fnv") is not None
    g.bar("SX17F %s state_restored" % tag, ok, "undo: scatter live %s vs pre_last2 %s, height %s vs %s, splat %s vs %s; redo: scatter live %s vs after %s" % (
        u.get("scatter_live_fnv"), p.get("scatter_fnv"), u.get("height_fnv"), p.get("height_fnv"), u.get("splat_fnv"), p.get("splat_fnv"), rd.get("scatter_live_fnv"),
        af.get("scatter_fnv")), "undo restores pre_last2's heights, splat and scatter, redo restores after's (so any image difference is render state)")
    shp = shape_of(run, "x_after")
    fp = os.path.join(run, "footprints.json")
    if shp is None or not os.path.isfile(fp):
        g.bar("SX17F %s images" % tag, False, "no samples (x_after.png or footprints.json missing)", "reported")
        return
    try:
        m = pt.footprint_union(pt.read_json(fp), ["rts80/last2_after", "rts80/last2_flat"], shp)
    except (KeyError, ValueError):
        g.bar("SX17F %s images" % tag, False, "no samples (no last2_after / last2_flat footprints)", "reported")
        return
    j = lambda n: os.path.join(run, n + ".png")
    fl = (j("x_before"), j("x_before_aa"))
    rows = (("aa_floor", "x_before", "x_before_aa", None), ("undo_vs_pre_last2", "x_undo", "x_pre_last2", fl), ("redo_vs_after", "x_redo", "x_after", fl),
            ("terrain_only undo_vs_pre_last2", "xo_undo", "xo_pre_last2", fl), ("terrain_only redo_vs_after", "xo_redo", "xo_after", fl),
            ("same_state x_undo_vs_xf_undo", "x_undo", "xf_undo", fl), ("sx18 xf_undo_vs_fresh", "xf_undo", "xf_undo_fresh", fl))
    for label, a, b, flp in rows:
        st = local_pair(g, "SX17F %s %s" % (tag, label), j(a), j(b), m, "last2 footprint; %s vs %s; one freeze window; %s" % (a, b, cfg), informational=True,
                        floor_paths=flp)
        if st is not None and os.path.isfile(j(a)) and os.path.isfile(j(b)):
            ch = imgdiff.changed(imgdiff.load_luma(j(a)), imgdiff.load_luma(j(b)))
            ys, xs = np.nonzero(ch)
            print("  [%s %s] whole frame: %d px changed (%.3f %%)%s" % (tag, label, int(ch.sum()), 100.0 * ch.mean(),
                                                                       (", bbox x %d..%d y %d..%d" % (xs.min(), xs.max(), ys.min(), ys.max())) if len(xs) else ""))


def sx17f(runs):
    """SX17's frozen single-window variant (plan 5 SX17 as amended 2026-10-03, reported) on S1XF runs."""
    g = pt.Gates()
    for r in runs:
        sx17f_run(g, r)
    print("SX17F %s (images reported; gated rows are the measurement's validity only)" % ("PASS" if g.ok else "FAIL"))
    return g


# ------------------------------------------------------------------------------------------------------------------------------ THINX (SX6)
def thinx(run):
    g = pt.Gates()
    res = pt.load_results(run)
    if res is None:
        g.bar("results", False, "results.json missing in %s" % run, "results.json")
        print("THINX FAIL")
        return g
    sub(g, "SX1/SX8/SX9", pt.scatter, run)
    checks = {c.get("name"): c for c in scatter_block(res).get("checks") or []}
    for st in ("dirt", "rock", "snow"):
        c = checks.get(st)
        for cl in ("Grass", "Tussock", "Flower"):
            name = "SX6 %s %s" % (st, cl)
            if not c:
                g.bar(name, False, "no samples (scatter_check %s missing)" % st, "core ratio to the pure-grass yield <= %.2f" % SX6_RATIO)
                continue
            row = (c.get("classes") or {}).get(cl) or {}
            live, pure = row.get("core_live", 0), row.get("core_pure", 0)
            if pure < SX6_MIN_YIELD[cl]:
                g.bar(name, False, "no samples (pure-grass core yield %d < %d: fix the coordinates, never the bar)" % (pure, SX6_MIN_YIELD[cl]), "core ratio to the pure-grass yield <= %.2f" % SX6_RATIO)
                continue
            g.bar(name, live / float(pure) <= SX6_RATIO, "core live %d / pure-grass %d = %.3f" % (live, pure, live / float(pure)), "<= %.2f of the pure-grass yield (min yield %d)" % (SX6_RATIO, SX6_MIN_YIELD[cl]))
    c = checks.get("path")
    name = "SX6 path Tree"
    if not c:
        g.bar(name, False, "no samples (scatter_check path missing)", "0 trees in the path core")
    else:
        row = (c.get("classes") or {}).get("Tree") or {}
        live, pure = row.get("core_live", 0), row.get("core_pure", 0)
        if pure < SX6_MIN_YIELD["Tree"]:
            g.bar(name, False, "no samples (pure-grass core yield %d < %d)" % (pure, SX6_MIN_YIELD["Tree"]), "0 trees in the path core")
        else:
            g.bar(name, live == 0, "core live %d / pure-grass %d" % (live, pure), "0 trees on the path (min yield %d)" % SX6_MIN_YIELD["Tree"])
    # positive control: rocks gain in the rock disc's edge band
    c = checks.get("rock")
    rocks = ((c or {}).get("classes") or {}).get("Rock") or {}
    g.bar("SX6 rock positive control", bool(c) and rocks.get("band_live", 0) >= SX6_MIN_ROCKS,
          "rocks in the rock disc's edge band: %s (core %s)" % (rocks.get("band_live"), rocks.get("core_live")), ">= %d rocks in the edge band (rock paint makes rocks there)" % SX6_MIN_ROCKS)
    dump_bar(g, run, res, "end", "SX5/SX6 oracle end")
    ok = g.ok
    print("THINX %s" % ("PASS" if ok else "FAIL"))
    return g


# ------------------------------------------------------------------------------------------------------------------------------ SHADX (SX10)
def shadx(run):
    g = pt.Gates()
    res = pt.load_results(run)
    if res is None:
        g.bar("results", False, "results.json missing in %s" % run, "results.json")
        print("SHADX FAIL")
        return g
    sub(g, "SX1/SX8/SX9", pt.scatter, run)
    sc = scatter_block(res)
    ver = {v.get("name"): v for v in sc.get("verifies") or []}
    t0 = next((t for t in (ver.get("raised") or {}).get("targets") or [] if t.get("name") == "t0"), None)
    ok = bool(t0) and bool(t0.get("exists")) and (t0.get("dz_m") or 0) > SX10_PRECONDITION_DZ_M
    g.bar("SX10 precondition", ok, "stored key t0: %s" % (t0 if t0 else "missing"), "after the raise the stored tree key exists with z up by > %.1f m (so the shadow of a moved caster is what is tested)" % SX10_PRECONDITION_DZ_M)
    freeze_bar(g, run, res, "SX10 temporal_freeze", SX10_FROZEN)
    shp = shape_of(run, "t_a")
    if shp is None:
        g.bar("SX10 shots", False, "no samples (t_a.png missing)", "shots")
    else:
        mask = fp_mask(run, "rts80/m_raised", shp)
        if mask is None:
            g.bar("SX10 mask", False, "no samples (rts80/m_raised missing in footprints.json)", "the union mask of t0's tree and shadows at t_a and t_raised")
        else:
            desc = "union of the caster + shadow masks within 45 m of t0 at t_a and t_raised"
            flp = (os.path.join(run, "t_a.png"), os.path.join(run, "t_b.png"))
            # plan 5 SX10: "t_b vs t_a gives the A/A floor" (reported); each edit's shot vs its fresh twin passes P7's statistic against that floor.
            aa = local_pair(g, "SX10 aa_floor", flp[0], flp[1], mask, desc + "; t_a vs t_b (1 s apart), the A/A floor (reported)", informational=True)
            for nm in ("t_far", "t_raised", "t_undo"):
                local_pair(g, "SX10 %s_vs_fresh" % nm, os.path.join(run, nm + ".png"), os.path.join(run, nm + "_fresh.png"), mask, desc, floor_paths=flp)
            # The plan's own mask: t0's tree and its shadow alone (scatter_mask only=@t0) at t_a and t_raised, beside the wider caster union, so a small
            # stale shadow cannot hide in a large mask's changed fraction.
            m0 = fp_mask(run, "rts80/m_t0_raised", shp)
            if m0 is None or int(m0.sum()) < pt.MIN_MASK_PX:
                g.bar("SX10 t0 mask", False, "no samples (rts80/m_t0_raised missing or < %d px)" % pt.MIN_MASK_PX, "t0's tree + shadow mask at t_a and t_raised")
            else:
                d0 = "t0's tree + shadow alone at t_a and t_raised"
                local_pair(g, "SX10 t0 aa_floor", flp[0], flp[1], m0, d0 + "; t_a vs t_b (reported)", informational=True)
                for nm in ("t_far", "t_raised", "t_undo"):
                    local_pair(g, "SX10 t0 %s_vs_fresh" % nm, os.path.join(run, nm + ".png"), os.path.join(run, nm + "_fresh.png"), m0, d0, floor_paths=flp)
            for nm in ("t_far", "t_raised", "t_undo"):
                window_bar(g, res, sc, nm, "SX10 %s compile window" % nm)
            if os.path.isfile(os.path.join(run, "t_raised.png")):
                A, B = imgdiff.load_luma(os.path.join(run, "t_raised.png")), imgdiff.load_luma(os.path.join(run, "t_a.png"))
                ch = imgdiff.changed_frac(A, B, mask)
                floor = aa["changed_frac"] if aa else 1.0
                g.bar("SX10 scene_changed", ch >= SX10_CHANGED_MIN and ch >= 10.0 * floor, "t_raised vs t_a changed_frac=%.4f (A/A %.5f)" % (ch, floor),
                      ">= %.2f inside the union mask and >= 10x the A/A floor: the scene really changed" % SX10_CHANGED_MIN)
    # VSM pages per edit: reported when the CSV has VSM columns
    cp = os.path.join(run, "terrain.csv")
    if os.path.isfile(cp):
        try:
            cols = vsm_columns(cp)
            g.bar("SX10 vsm_pages", True, cols if cols else "no VSM columns in terrain.csv (UNVERIFIED: page stats may need r.Shadow.Virtual.ShowStats)", "VSM pages per edit (reported)", informational=True)
        except (OSError, ValueError) as ex:
            g.bar("SX10 vsm_pages", True, "terrain.csv unreadable: %s" % ex, "reported", informational=True)
    else:
        g.bar("SX10 vsm_pages", True, "no terrain.csv", "reported", informational=True)
    ok = g.ok
    print("SHADX %s" % ("PASS" if ok else "FAIL"))
    return g


def vsm_columns(path):
    """p50/max per phase of every CSV series whose name mentions the virtual shadow maps."""
    import csv
    import gzip
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8", newline="") as f:
        reader = csv.reader(f, quoting=csv.QUOTE_NONE)
        first = next(reader, None)
        data, final = [], None
        for row in reader:
            if row and row[0] == "EVENTS":
                final = row
                break
            if row:
                data.append(row)
    names = (final or first)[1:]
    idx = [(i + 1, n) for i, n in enumerate(names) if "VSM" in n or "VirtualShadow" in n]
    out = {}
    phase = None
    per = {}
    for row in data:
        ms = re.findall(r"phase_([A-Za-z0-9_]+)", row[0])
        if ms:
            phase = ms[-1]
        for i, n in idx:
            try:
                per.setdefault((phase, n), []).append(float(row[i]))
            except (ValueError, IndexError):
                pass
    for (ph, n), v in per.items():
        out["%s/%s" % (ph, n)] = {"p50": float(np.percentile(v, 50)), "max": float(max(v))}
    return out


# ------------------------------------------------------------------------------------------------------------------------------ LATX (SX12)
def latx(run):
    g = pt.Gates()
    res = pt.load_results(run)
    if res is None:
        g.bar("results", False, "results.json missing in %s" % run, "results.json")
        print("LATX FAIL")
        return g, {}
    sub(g, "SX1/SX8/SX9", pt.scatter, run)
    sc = scatter_block(res)
    strokes = res.get("strokes") or []
    lat = [l for l in sc.get("latency") or [] if l.get("reason") == "stroke_end"]
    g.bar("SX12 pairing", len(lat) == len(strokes) and len(strokes) > 0, "%d stroke_end latency rows, %d strokes" % (len(lat), len(strokes)),
          "one stroke_end latency row per stroke, in order")
    rows = {}
    for s, l in zip(strokes, lat):
        d = int(round(s.get("d", 0)))
        rows.setdefault(d, []).append(l.get("ms", -1.0) / 1000.0)
    summary = {}
    for d in sorted(rows):
        v = rows[d]
        n = len(v)
        # an unfinished stroke (ms = -1: its tiles never became current) is not a fast stroke: it is left out of the statistics and forces the OVER verdict
        unfinished = sum(1 for x in v if x < 0)
        done = [x for x in v if x >= 0]
        n_done = len(done)
        bar = SX12_LAT_S.get(d)
        if not done:
            summary[d] = {"n": n, "n_finished": 0, "p95_or_max_s": None, "median_s": None, "max_s": None, "unfinished": unfinished, "bar_s": bar}
            g.bar("SX12 latency d%d" % d, True, "n=%d, all %d unfinished: %s" % (n, unfinished, "n/a (no bar at this diameter)" if bar is None else "OVER %.1f s" % bar),
                  "end -> every tile current and flushed: <= 0.3 s at d <= 20, <= 1.0 s at d100 (reported)", informational=True)
            continue
        stat = float(np.percentile(done, 95)) if n_done >= 20 else max(done)
        summary[d] = {"n": n, "n_finished": n_done, "p95_or_max_s": stat, "median_s": float(np.median(done)), "max_s": max(done), "unfinished": unfinished, "bar_s": bar}
        verdict = "n/a (no bar at this diameter)" if bar is None else ("within %.1f s" % bar if stat <= bar and not unfinished else "OVER %.1f s" % bar)
        g.bar("SX12 latency d%d" % d, True, "n=%d (finished %d) %s %.3f s (median %.3f, max %.3f), unfinished %d: %s" % (
            n, n_done, "p95" if n_done >= 20 else "max", stat, float(np.median(done)), max(done), unfinished, verdict),
              "end -> every tile current and flushed: <= 0.3 s at d <= 20, <= 1.0 s at d100; statistics over finished strokes (reported)", informational=True)
    inits = [l for l in sc.get("latency") or [] if l.get("reason") in ("enable", "init")]
    g.bar("SX12 init_ms", True, "init_ms=%.0f; fill rows %s" % (sc.get("init_ms", -1), [(l.get("reason"), round(l.get("ms", 0))) for l in inits]), "initial fill (reported)", informational=True)
    ok = g.ok
    print("LATX %s" % ("PASS" if ok else "FAIL"))
    return g, summary


# ------------------------------------------------------------------------------------------------------------------------------ --summary SCATTER table
def pct(values, p):
    return pt.pctl(values, p)


def gpu_series(run, phase):
    v = pt.csv_phase_values(run, phase, "GPUTime")
    if v:
        return v, "csv"
    ph = ((run["res"].get("metrics") or {}).get("phases") or {}).get(phase)
    if ph and ph.get("gpu_ms"):
        return None, "director"
    return None, None


def gpu_p50(run, phase):
    v, src = gpu_series(run, phase)
    if v:
        return pct(v, 50)
    return pt.phase_stat(run, phase, "gpu_ms")


def summary_scatter(runs, add, any_measured, summ):
    """The SCATTER table and the SX11-SX15 rows of --summary. `runs` are pt.load_run dicts, `add` is the summary's gate adder."""
    sruns = [r for r in runs if (r["res"].get("options") or {}).get("scatter")]
    print("\n== SCATTER (plan-c-scatter.md 5; ms unless noted)")
    if not sruns:
        print("no run with scatter in this summary")
        return
    print("%-16s %-8s %9s %8s %20s %20s %16s %6s" % ("tag", "script", "instances", "init ms", "scatter_gt p50/p99/max", "flush p50/p99/max", "apply p50/p99", "recr"))
    out = {}
    for r in sruns:
        sc = scatter_block(r["res"])
        gt = (sc.get("scatter_gt_ms") or {}).get("all_but_fill") or {}
        fl = (sc.get("scatter_flush_ms") or {}).get("all_but_fill") or {}
        ap = sc.get("apply_unit_ms_edits") or {}
        print("%-16s %-8s %9s %8.0f %20s %20s %16s %6s" % (r["tag"], r["script"], sc.get("instances"), sc.get("init_ms", -1),
              "%.3f/%.3f/%.3f" % (gt.get("p50", 0), gt.get("p99", 0), gt.get("max", 0)), "%.3f/%.3f/%.3f" % (fl.get("p50", 0), fl.get("p99", 0), fl.get("max", 0)),
              "%.3f/%.3f" % (ap.get("p50", 0), ap.get("p99", 0)), sc.get("proxy_recreates")))
        out[r["tag"]] = {"gt": gt, "flush": fl, "apply": ap}
        print("%-16s scatter_flush_ms row: p50 %.3f p99 %.3f max %.3f (end-of-frame instance flush, outside the fill)" % (r["tag"], fl.get("p50", 0), fl.get("p99", 0), fl.get("max", 0)))
    summ["scatter"] = out
    # scatter_gpu_ms and the per-layer, pose pairs (C1S phases)
    c1s = [r for r in sruns if r["script"] in ("C1S", "C1US")]
    for r in c1s:
        on = [gpu_p50(r, p) for p in ("idle_scatter_on", "idle_scatter_on2")]
        on = [x for x in on if x is not None]
        off = gpu_p50(r, "idle_scatter_off")
        if not on or off is None:
            print("%-16s scatter_gpu_ms: no samples (needs the idle_scatter_on / idle_scatter_on2 / idle_scatter_off phases)" % r["tag"])
            continue
        onm = float(np.median(on))
        gpu = onm - off
        src = "csv" if pt.csv_phase_values(r, "idle_scatter_on", "GPUTime") else "director"
        print("%-16s scatter_gpu_ms = %.3f (on p50 %.3f [%s], off p50 %.3f; target %.1f, kill above %.1f after the governor ladder)" % (r["tag"], gpu, onm, ", ".join("%.3f" % x for x in on), off, SX13_TARGET_MS, SX13_KILL_MS))
        for layer in ("grass", "groundcover", "shrubs", "trees", "rocks"):
            lo = gpu_p50(r, "idle_layer_off_" + layer)
            print("%-16s   layer %-12s %s" % (r["tag"], layer, "no samples" if lo is None else "%.3f ms (on %.3f - layer off %.3f)" % (onm - lo, onm, lo)))
        for pose in ("oblique", "closeup"):
            a_, b_ = gpu_p50(r, pose + "_on"), gpu_p50(r, pose + "_off")
            print("%-16s   pose %-12s %s" % (r["tag"], pose, "no samples" if a_ is None or b_ is None else "%.3f ms (on %.3f - off %.3f)" % (a_ - b_, a_, b_)))
        add("SX13 scatter_gpu_ms %s" % r["tag"], gpu <= SX13_KILL_MS, "%.3f ms at rts80 (target %.1f, kill above %.1f; reported, kill rule applied by the main session)" % (gpu, SX13_TARGET_MS, SX13_KILL_MS),
            report=True)
        # stroke_gpu_ms: (C1S walk - idle_scatter_on) - (C1 walk - idle_visible), paired with a C1 run
        c1 = [x for x in runs if x["script"] == "C1" and not (x["res"].get("options") or {}).get("scatter")]
        wk = pt.csv_phase_values(r, "walk", "GPUTime")
        if c1 and wk and pt.csv_phase_values(c1[0], "walk", "GPUTime"):
            w1 = pt.csv_phase_values(c1[0], "walk", "GPUTime")
            vis = [gpu_p50(c1[0], p) for p in ("idle_visible", "idle_visible2")]
            vis = [x for x in vis if x is not None]
            sg_ok = sg_pair(add, r, c1[0], "stroke_gpu_ms")
            # a pair without equal sg.* is not one configuration: when the C1 run recorded no sg.* (a build before task S5) it is also a different build,
            # so the difference is printed for the record only, labelled not comparable
            nc = "" if sg_ok else " NOT COMPARABLE (sg mismatch or sg.* not recorded: a different configuration, here a different build)"
            if not vis:
                print("%-16s stroke_gpu_ms: no samples (%s has no idle_visible / idle_visible2 GPU phase to subtract)" % (r["tag"], c1[0]["tag"]))
            else:
                base1 = float(np.median(vis))
                rows = {}
                for p in (50, 95, 99):
                    rows[p] = (pct(wk, p) - onm) - (pct(w1, p) - base1)
                print("%-16s stroke_gpu_ms p50 %.3f p95 %.3f p99 %.3f (= (%s walk - scatter on idle) - (%s walk - idle_visible)); %s" % (
                    r["tag"], rows[50], rows[95], rows[99], r["tag"], c1[0]["tag"], ("reported;" + nc) if nc else "reported"))
        else:
            print("%-16s stroke_gpu_ms: no samples (needs a scatter-off C1 run with a CSV beside it, and this run's walk CSV)" % r["tag"])
    if not c1s:
        print("scatter_gpu_ms / stroke_gpu_ms: no samples (no C1S or C1US run in this summary)")
    # SX11 on C1S: the walk plus every flush window (every non-fill frame with an instance flush), fill excluded; ScatterThreads=0 runs never count
    for r in c1s:
        sc = scatter_block(r["res"])
        if ((((r["res"].get("options") or {}).get("scatter")) or {}).get("threads")) == 0:
            add("SX11 scatter_gt_ms %s" % r["tag"], False, "no samples (ScatterThreads=0 run: never counted by SX11)", True, any_measured)
            continue
        scopes = sx11_scopes(sc)
        if scopes is None:
            add("SX11 scatter_gt_ms %s" % r["tag"], False, "no samples (no per-frame scatter series with phase names in results.json)", True, any_measured)
            continue
        for scope, v in scopes.items():
            if not v:
                add("SX11 scatter_gt_ms %s %s" % (r["tag"], scope), False, "no samples (no %s frames)" % scope, True, any_measured)
                continue
            a = np.asarray(v, dtype=float)
            p99, p999, over2 = float(np.percentile(a, 99)), float(np.percentile(a, 99.9)), float((a > 2.0).mean())
            add("SX11 scatter_gt_ms %s %s" % (r["tag"], scope), p99 <= SX11_GT_P99 and p999 <= SX11_GT_P999 and over2 <= SX11_GT_OVER2_FRAC,
                "%s: n %d, p99 %.3f <= %.1f, p99.9 %.3f <= %.1f, frames > 2.0 ms %.3f %% <= %.1f %%, max %.3f" % (
                    scope, len(a), p99, SX11_GT_P99, p999, SX11_GT_P999, over2 * 100, SX11_GT_OVER2_FRAC * 100, float(a.max())), True, any_measured)
        c1 = [x for x in runs if x["script"] == "C1" and not (x["res"].get("options") or {}).get("scatter")]
        if c1:
            sg_pair(add, r, c1[0], "SX11")
            ps = (r["res"].get("metrics") or {}).get("phases", {}).get("walk", {})
            pc = (c1[0]["res"].get("metrics") or {}).get("phases", {}).get("walk", {})
            need = [(n, k) for n, ph in (("C1S", ps), ("C1", pc)) for k in ("gt_ms", "rt_ms", "frame_ms") if "p99" not in (ph.get(k) or {}) and k != "frame_ms"]
            need += [(n, "frame_ms.n") for n, ph in (("C1S", ps), ("C1", pc)) if not (ph.get("frame_ms") or {}).get("n")]
            if need:
                add("SX11 frame GT/RT %s" % r["tag"], False, "no samples (walk metrics missing: %s)" % need, True, any_measured)
            else:
                dgt = ps["gt_ms"]["p99"] - pc["gt_ms"]["p99"]
                drt = ps["rt_ms"]["p99"] - pc["rt_ms"]["p99"]
                o33 = ps.get("frames_over_33ms", 0) / float(ps["frame_ms"]["n"])
                c33 = pc.get("frames_over_33ms", 0) / float(pc["frame_ms"]["n"])
                add("SX11 frame GT/RT %s" % r["tag"], dgt <= SX11_DELTA_MS and drt <= SX11_DELTA_MS and (o33 - c33) * 100.0 <= SX11_OVER33_PP,
                    "walk GT p99 delta %.3f, RT p99 delta %.3f (<= %.1f vs %s), frames > 33.3 ms %.2f %% vs %.2f %% (+%.2f pp <= %.1f)" % (
                        dgt, drt, SX11_DELTA_MS, c1[0]["tag"], o33 * 100, c33 * 100, (o33 - c33) * 100, SX11_OVER33_PP), True, any_measured)
        else:
            add("SX11 frame GT/RT %s" % r["tag"], False, "no samples (no scatter-off C1 run in this summary to difference against)", True, any_measured)
    # SX12 from LATX runs, SX14 from C1US, SX15
    for r in [x for x in sruns if x["script"] == "LATX"]:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            _g, lat = latx(r["dir"])
        for d in sorted(lat):
            v = lat[d]
            print("%-16s SX12 latency d%-3d n=%d %s s (bar %s, unfinished %d)" % (r["tag"], d, v["n"], "n/a" if v["p95_or_max_s"] is None else "%.3f" % v["p95_or_max_s"],
                                                                               v["bar_s"], v["unfinished"]))
        add("SX12 latency %s" % r["tag"], bool(lat), "%d diameters measured" % len(lat) if lat else "no samples (no stroke_end latency rows)", report=True)
    for r in [x for x in sruns if x["script"] == "C1US"]:
        walk = pt.csv_phase_values(r, "walk", "FrameTime")
        if walk:
            med, low = pt.fps_from_ms(pct(walk, 50)), pt.fps_from_ms(pct(walk, 99))
            over33 = sum(1 for v in walk if v > 33.3) / float(len(walk))
            idle = pct(pt.csv_phase_values(r, "idle_scatter_on", "FrameTime") or [0], 50)
            ok = med >= pt.P3_MEDIAN_FPS and low >= pt.P3_LOW1_FPS and over33 <= pt.P3_OVER33_FRAC and max(walk) <= pt.P3_MAX_FRAME_MS and pct(walk, 50) - idle <= pt.P3_SCULPT_MINUS_IDLE_MS
            add("SX14 C1US %s" % r["tag"], ok, "median %.1f fps, 1%% low %.1f, > 33 ms %.2f %%, max %.1f ms, sculpt-idle %.2f ms" % (med, low, over33 * 100, max(walk), pct(walk, 50) - idle),
                report=True)
        else:
            add("SX14 C1US %s" % r["tag"], False, "no samples (no walk CSV)", report=True)
    views = []
    for r in sruns:
        for v in scatter_block(r["res"]).get("view_counts") or []:
            if v.get("pose") == "rts80":
                views.append((r["tag"], v))
    if views:
        for tag, v in views:
            run_opt = ((next(x for x in sruns if x["tag"] == tag)["res"].get("options") or {}).get("scatter")) or {}
            prims_cap = SX15_PRIMS_MAX_T0_SPLIT if run_opt.get("t0_rank_split") else SX15_PRIMS_MAX
            add("SX15 caps %s" % tag, v.get("drawn_total", 1e9) <= SX15_DRAWN_MAX and v.get("primitives_in_view", 1e9) <= prims_cap,
                "analytic at rts80: drawn %s (<= %d), primitives in view %s of %s with instances (<= %d%s)" % (
                    v.get("drawn_total"), SX15_DRAWN_MAX, v.get("primitives_in_view"), v.get("primitives_with_instances"), prims_cap,
                    "" if run_opt.get("t0_rank_split") else ", no T0 rank split recorded"), report=True)
            tok, tdetail = tris_verdict(v, run_opt.get("level") or "L0", run_opt.get("meshes"))
            add("SX15 triangles %s" % tag, tok, tdetail, report=True)
    else:
        add("SX15 caps", False, "no samples (no scatter_view_counts at rts80 in these runs)", report=True)
    for r in [x for x in sruns if x["script"] == "SOAKS"]:
        ok, detail, rec = pt.p6_check(r["res"])
        g_ = rec.get("growth_peak_mb")
        add("SX15 SOAKS peak growth %s" % r["tag"], g_ is not None and g_ <= SX15_SOAK_GROWTH_MB, "peak growth %s MB against P6's %.0f MB" % (g_, SX15_SOAK_GROWTH_MB)
            if g_ is not None else "no samples (no P6 soak record)", report=True)


def sg_of(res):
    """The effective sg.* of a run: results.json `sg` (every run, task S5), else options.scatter.sg (scatter runs of an earlier build)."""
    return res.get("sg") or ((((res.get("options") or {}).get("scatter")) or {}).get("sg"))


def sg_pair(add, a, b, what):
    """plan 3.8: every pair of runs compared (C1 vs C1S, S1X vs pkg/ship S1X) must have equal sg.*; otherwise the comparison FAILs `sg mismatch`."""
    sa, sb = sg_of(a["res"]), sg_of(b["res"])
    if sa is None or sb is None:
        add("sg %s %s/%s" % (what, a["tag"], b["tag"]), False, "no samples (sg.* not recorded in %s)" % [x["tag"] for x, s_ in ((a, sa), (b, sb)) if s_ is None])
        return False
    ok = sa == sb
    add("sg %s %s/%s" % (what, a["tag"], b["tag"]), ok, "equal" if ok else "sg mismatch: %s" % {k: (sa.get(k), sb.get(k)) for k in sorted(set(sa) | set(sb)) if sa.get(k) != sb.get(k)})
    return ok


def sx11_scopes(sc):
    """scatter_gt_ms per frame for SX11's two scopes: `walk` (the C1S random walk) and `flush_windows` (every non-fill frame whose end-of-frame instance flush
    ran, any phase). None when the per-frame series or its phase names are missing."""
    fr = sc.get("frames") or []
    names = sc.get("phase_names")
    if not fr or not names:
        return None
    walk = [r[3] for r in fr if 0 <= int(r[1]) < len(names) and names[int(r[1])] == "walk" and not r[2]]
    flush = [r[3] for r in fr if not r[2] and (r[5] or 0) > 0]
    return {"walk": walk, "flush_windows": flush}


MESH_TRIS_CACHE = {}


def tris_report(view, level="L0", paths=None):
    """LOD0 source-triangle upper bound of a view-count row (per-mesh drawn counts x the S3 asset report's triangles_source of the run's level; an L1 run uses its
    L1 row where S3 imported one and the L0 row otherwise, as the scatter actor loads meshes). S6: `paths` (results options.scatter.meshes, slot -> {path})
    names the mesh each slot really loaded (per-slot choices, -ChimeraTerrainScatterMeshes); its report row wins over the level rule."""
    rp = os.path.join(os.path.dirname(HERE), "Out", "scatter_assets", "report.json")
    if "rows" not in MESH_TRIS_CACHE:
        MESH_TRIS_CACHE["rows"] = {}
        if os.path.isfile(rp):
            try:
                for _k, m in (pt.read_json(rp).get("meshes") or {}).items():
                    tri = m.get("triangles_source") or m.get("triangles_expected")
                    if m.get("slot") and tri:
                        MESH_TRIS_CACHE["rows"][(m.get("level"), m["slot"])] = int(tri)
                    if tri:
                        MESH_TRIS_CACHE.setdefault("paths", {})[_k] = int(tri)
            except (OSError, ValueError, TypeError, AttributeError):
                pass
    rows = MESH_TRIS_CACHE["rows"]
    if not rows:
        return "n/a (no S3 report.json triangle table)"
    total, miss = 0, []
    by_path = MESH_TRIS_CACHE.get("paths") or {}
    for mesh, n in (view.get("meshes_drawn") or {}).items():
        loaded = ((paths or {}).get(mesh) or {}).get("path") or ""
        tri = by_path.get(loaded.split(".")[0]) if loaded else None
        tri = tri or rows.get((level, mesh)) or rows.get(("L0", mesh))
        if tri is None:
            if n:
                miss.append(mesh)
            continue
        total += n * tri
    return "%d%s" % (total, " (no triangle count for %s)" % miss if miss else "")


def tris_verdict(view, level="L0", paths=None):
    """SX15's triangle row: (ok, detail). Only the LOD0 upper bound can be computed here (no per-instance LOD is recorded), so the row passes only when that
    upper bound is within the 1.5 M cap; above it the LOD-weighted figure is unknown and the row says so (never pass)."""
    rep = tris_report(view, level, paths)
    m = re.match(r"(\d+)", rep)
    if not m:
        return False, "no samples (%s)" % rep
    ub = int(m.group(1))
    if "no triangle count" in rep:
        return False, "LOD0 triangle upper bound %s: incomplete, LOD-weighted figure unknown (cap %d)" % (rep, SX15_TRIS_MAX)
    if ub <= SX15_TRIS_MAX:
        return True, "LOD0 triangle upper bound %d <= %d, so the LOD-weighted figure is within the cap too" % (ub, SX15_TRIS_MAX)
    return False, "LOD0 triangle upper bound %d above the cap %d: LOD-weighted figure unknown (not computed: no per-instance LOD recorded)" % (ub, SX15_TRIS_MAX)


# ------------------------------------------------------------------------------------------------------------------------------ main hooks
def write_json(path, name, g, extra=None):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(dict({"gate": name, "pass": g.ok, "bars": g.rows}, **(extra or {})), f, indent=1)


def main_hook(a):
    """Called by parse_terrain.main for --s1x, --thinx, --shadx, --latx, --dump-oracle. Returns the exit code or None when not ours."""
    if a.s1x:
        if not a.ref:
            print("--s1x needs --ref")
            return 2
        g = s1x(a.s1x, a.pair or [], a.reload, a.ref, a.xref, a.packaged, a.shipping)
        write_json(os.path.join(a.s1x, "s1x.json"), "S1X", g)
        return 0 if g.ok else 1
    if getattr(a, "sx17f", None):
        g = sx17f(a.sx17f)
        write_json(os.path.join(a.sx17f[0], "sx17f.json"), "SX17F", g)
        return 0 if g.ok else 1
    if a.thinx:
        g = thinx(a.thinx)
        write_json(os.path.join(a.thinx, "thinx.json"), "THINX", g)
        return 0 if g.ok else 1
    if a.shadx:
        g = shadx(a.shadx)
        write_json(os.path.join(a.shadx, "shadx.json"), "SHADX", g)
        return 0 if g.ok else 1
    if a.latx:
        g, summary = latx(a.latx)
        write_json(os.path.join(a.latx, "latx.json"), "LATX", g, {"latency": summary})
        return 0 if g.ok else 1
    if getattr(a, "dump_diff", None):
        ra_, rb_, nm = a.dump_diff[:3]
        return dump_diff(ra_, rb_, nm, a.dump_diff[3] if len(a.dump_diff) > 3 else None)
    if a.dump_oracle:
        run, name = a.dump_oracle
        g = pt.Gates()
        res = pt.load_results(run) or {}
        dump_bar(g, run, res, name, "oracle %s" % name)
        return 0 if g.ok else 1
    return None
