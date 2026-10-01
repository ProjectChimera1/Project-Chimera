#!/usr/bin/env python3
"""hud_compare.py: score an Unreal capture of the Match HUD against the grayscale mockup reference (plan B section 4).

    python hud_compare.py --ref ref_hudonly.png --shot cap.png --pair A [--only REGEX] [--gates G1,G2,G3,G5] [--out DIR]
    python hud_compare.py --pipeline REF.png SHOT.png [--max 0]            # T3: 1:1 pipeline check, prints PIPELINE OK|FAIL
    python hud_compare.py --blend --backdrop '#14161A'|world_layer.png --shot SHOT.png [--boxes "x,y,w,h,#hex,a;..."]

Gates (hard = fixed physics, soft = calibrated per pair by calibrate.py into thresholds.json):
  G1 colour probes  36 hard + auto probes, each channel +-2; vector probes soft ('G1.vector')
  G2 border lines   every solid border side, >= 95% of its pixels within +-8, scored at the region's shift
  G3 region shift   argmin of flat+vector MAD over shifts [-3,3]^2 (ties within 0.05 -> 0); fixed regions must be (0,0),
                    flow regions <= 1 px per axis. G1 auto probes, G2, G4, G5 of a flow region use that shift
  G4 class error    flat MAD <= 1.0 and bad px (> 8) <= 1%; placeholder MAD <= 2; vector/text MAD and SSIM (soft)
  G5 text runs      ink box (local background = median of the window rim, threshold 24): left/top/bottom +-1 px,
                    right +-max(1 px, 0.5% of width); ink no heavier than the reference: signed ink mass
                    (test - ref) / ref <= +0.05 (hard, T2: catches a weight step that leaves the ink box unchanged);
                    ink mass relative error |test - ref| / ref (soft 'G5.mass')
  G6 pipeline       world.open MAD <= 0.5, max <= 2
  G7 summary        MAD and SSIM (luma, Gaussian sigma 1.5) over the selected HUD pixels; soft 'G7.mad' / 'G7.ssim'
Soft metric names in thresholds.json ({"A": {"default": {metric: value}, "regions": {region: {metric: value}}}, "B": {...}}):
  G4.text.mad G4.vector.mad (max), G4.text.ssim G4.vector.ssim (min), G5.mass (max), G1.vector (max), G7.mad (max), G7.ssim (min).
A soft metric without a threshold is REPORT-ONLY: shown, never failing.
badness = max over gates of value/threshold (<= 1 passes). Exit codes: 0 PASS, 1 FAIL, 2 usage/hash error, 3 alignment fail.
Alignment: an anchor edge found at a non-zero offset prints ALIGNMENT FAIL and stops (exit 3); an edge not found within
+-10 px prints ANCHOR WARN and the regions are scored anyway. Calibration: a matching results/calibration.json prints
CALIBRATION OK with the hashes; a mismatch, or thresholds.json without calibration.json, exits 2 unless --recalibrated
NOTE (logged to convergence.csv) or, before T2 only, --uncalibrated.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy.ndimage import uniform_filter

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hud_common import (CALIBRATION_JSON, CLASS_NAMES, CONVERGENCE_CSV, FLAT, H_PX, HUDREF, PLACEHOLDER, REGIONS_JSON,
                        TEXT, THRESHOLDS_JSON, VECTOR, W, b64_png, check_calibration, find_ref, load_rgb, luma, parse_color,
                        sha256_file)
from make_regions import ANCHOR_RANGE, RID, anchor_matches, anchor_samples, anchor_use, line_pixels
from ssim import ssim_map

PAD = 10          # edge-replicated border so shifted crops never leave the array
SHIFTS = range(-3, 4)
TIE = 0.05
HARD = {"g1_tol": 2, "g2_tol": 8, "g2_ratio": 0.95, "flat_mad": 1.0, "flat_bad_frac": 0.01, "bad_px": 8,
        "placeholder_mad": 2.0, "g6_mad": 0.5, "g6_max": 2, "flow_shift": 1, "edge_tol": 1, "right_frac": 0.005,
        "ink_thr": 24, "mass_heavy": 0.05}
# mass_heavy (hard, added by T2 for negative N3, plan B 4.4 "a new hard metric for that defect class"; ratified by the main
# session as T2 ruling R1, EXECUTION section 7, with these numbers: positives at most +0.028, P5's heaviest run -0.0545, N3
# +0.115; results/calibration.json "rulings" and "heaviest_run" record them per pair): Slate's grayscale font
# path multiplies the vertex colour by the raw A8 glyph coverage with no gamma or contrast step (UE 5.8
# Engine/Shaders/Private/SlateElementPixelShader.usf:387-406, ST_GrayscaleFont), so correctly weighted Slate text is never
# heavier than Chromium's: the Slate model P5 is lighter in all 36 runs at hinting None, Default and AutoLight (heaviest run
# -0.055; calibrate.py re-measures this and records it in results/calibration.json "p5_hinting_probe"). Sub-pixel placement moves a run's ink mass by at most +0.028 (P3/P4, tab.3 '8'; P1 +0.017), while one weight step
# adds +0.115 (N3, Cinzel 600 -> 700, whose ink box is unchanged because the advances move only 0.36 px). +0.05 sits between.
ALL_GATES = ["G1", "G2", "G3", "G4", "G5", "G6", "G7"]
EPS = 1e-9


# ---------------------------------------------------------------- thresholds
class Thr:
    """Soft thresholds for one pair; missing = report-only."""

    def __init__(self, data=None, pair="A"):
        d = (data or {}).get(pair, {}) or {}
        self.default = d.get("default", {})
        self.regions = d.get("regions", {})

    def get(self, region, metric):
        v = self.regions.get(region, {}).get(metric)
        return self.default.get(metric) if v is None else v


def load_thresholds(path=THRESHOLDS_JSON):
    p = Path(path)
    return json.load(open(p, encoding="utf-8")) if p.is_file() else {}


# ---------------------------------------------------------------- spec wrapper
class Spec:
    def __init__(self, d):
        self.d = d
        self.lab = b64_png(d["labels_png_b64"])
        self.cls = b64_png(d["classes_png_b64"])
        self.regions = {r["name"]: r for r in d["regions"]}
        self.by_id = {r["id"]: r for r in d["regions"]}


def load_spec(path=REGIONS_JSON):
    return Spec(json.load(open(path, encoding="utf-8")))


def badness(value, thr):
    """value/threshold with 0/0 = 0; a violated zero threshold is infinite."""
    if value <= 0:
        return 0.0
    return float("inf") if thr <= 0 else value / thr


# ---------------------------------------------------------------- scoring core
class Ctx:
    def __init__(self, ref, test, spec, thr, gates):
        self.ref, self.test, self.spec, self.thr, self.gates = ref, test, spec, thr, gates
        pad = ((PAD, PAD), (PAD, PAD), (0, 0))
        self.refp = np.pad(ref, pad, mode="edge").astype(np.int16)
        self.testp = np.pad(test, pad, mode="edge").astype(np.int16)
        self._lref = self._ltest = None

    @property
    def lref(self):
        if self._lref is None:
            self._lref = luma(self.refp)
        return self._lref

    @property
    def ltest(self):
        if self._ltest is None:
            self._ltest = luma(self.testp)
        return self._ltest

    def tpix(self, ys, xs, dx, dy):
        return self.testp[ys + PAD + dy, xs + PAD + dx]

    def rpix(self, ys, xs):
        return self.refp[ys + PAD, xs + PAD]


def best_shift(ctx, ys, xs):
    """argmin over [-3,3]^2 of the MAD of the given pixels; (0,0) when within TIE of the best."""
    if len(ys) == 0:
        return (0, 0), {}
    rv = ctx.rpix(ys, xs)
    costs = {}
    for dy in SHIFTS:
        for dx in SHIFTS:
            costs[(dx, dy)] = float(np.abs(ctx.tpix(ys, xs, dx, dy) - rv).mean())
    best = min(costs.values())
    cand = [s for s, c in costs.items() if c <= best + TIE]
    if (0, 0) in cand:
        return (0, 0), costs
    cand.sort(key=lambda s: (abs(s[0]) + abs(s[1]), max(abs(s[0]), abs(s[1])), s))
    return cand[0], costs


def rim_median(win):
    """Per-channel median of the window's outer ring (the local background)."""
    ring = np.concatenate([win[0], win[-1], win[1:-1, 0], win[1:-1, -1]])
    return np.median(ring, axis=0)


def ink_stats(win, bg, d=None):
    """Ink box (l, t, r, b exclusive; None when empty), coverage mass and peak distance of a window against bg."""
    dist = np.abs(win - bg).max(axis=2)
    ink = dist > HARD["ink_thr"]
    if not ink.any():
        return None, 0.0, float(dist.max()), d
    cols, rows = np.nonzero(ink.any(axis=0))[0], np.nonzero(ink.any(axis=1))[0]
    box = (int(cols[0]), int(rows[0]), int(cols[-1]) + 1, int(rows[-1]) + 1)
    if d is None:   # direction bg -> brightest ink pixel of the reference window
        iy, ix = np.unravel_index(int(np.argmax(dist)), dist.shape)
        d = win[iy, ix] - bg
    dd = float(d @ d)
    cov = np.clip(((win - bg) @ d) / dd, 0, 1.2) if dd > 0 else np.zeros(dist.shape)
    return box, float(cov.sum()), float(dist.max()), d


def score_text_run(ctx, run, shift):
    x0, y0, x1, y1 = run["window"]
    dx, dy = shift
    ys, xs = np.mgrid[y0:y1, x0:x1]
    rwin = ctx.rpix(ys, xs).astype(float)
    twin = ctx.tpix(ys, xs, dx, dy).astype(float)
    rbox, rmass, rpeak, d = ink_stats(rwin, rim_median(rwin))
    tbox, tmass, tpeak, _ = ink_stats(twin, rim_median(twin), d)
    out = {"id": run["id"], "text": run["text"], "ref_box": rbox, "test_box": tbox, "mass_ref": rmass, "mass_test": tmass,
           "peak_ratio": (tpeak / rpeak) if rpeak else None}
    if rbox is None:
        out.update(note="no ink in the reference window", bad=0.0, mass_rel=0.0, mass_signed=0.0)
        return out
    if tbox is None:
        out.update(note="no ink in the capture", bad=float("inf"), mass_rel=1.0, mass_signed=-1.0)
        return out
    wref = rbox[2] - rbox[0]
    dl, dt, db, dr = tbox[0] - rbox[0], tbox[1] - rbox[1], tbox[3] - rbox[3], tbox[2] - rbox[2]
    right_tol = max(1.0, HARD["right_frac"] * wref)
    signed = (tmass - rmass) / rmass if rmass else 0.0
    out.update(dl=dl, dt=dt, db=db, dr=dr, right_tol=right_tol, mass_rel=abs(signed), mass_signed=signed)
    out["bad"] = max(abs(dl) / HARD["edge_tol"], abs(dt) / HARD["edge_tol"], abs(db) / HARD["edge_tol"], abs(dr) / right_tol,
                     max(signed, 0.0) / HARD["mass_heavy"])
    return out


def class_stats(ctx, ys, xs, shift):
    rv = ctx.rpix(ys, xs)
    tv = ctx.tpix(ys, xs, *shift)
    diff = np.abs(tv - rv)
    return float(diff.mean()), float((diff.max(axis=1) > HARD["bad_px"]).mean())


def unit_ssim(ctx, box, mask_c, shift):
    """SSIM map over a unit's bbox (+6 px so the 11-tap window is exact inside) of ref vs the shifted capture.
    Capture pixels outside the unit's mask are replaced by the reference's before the map is computed, so a change in a
    neighbouring region (or unit) cannot reach this unit through the 11-tap window: each change is scored only where it is
    (T2: negative N5's 108 mm.buttons pixels otherwise failed mm.plate's vector SSIM across the region border)."""
    x0, y0, x1, y1 = box
    e = 6
    sl_r = (slice(y0 - e + PAD, y1 + e + PAD), slice(x0 - e + PAD, x1 + e + PAD))
    sl_t = (slice(y0 - e + PAD + shift[1], y1 + e + PAD + shift[1]), slice(x0 - e + PAD + shift[0], x1 + e + PAD + shift[0]))
    lr = ctx.lref[sl_r]
    lt = ctx.ltest[sl_t].copy()
    keep = np.zeros(lr.shape, bool)
    keep[e:-e, e:-e] = mask_c
    lt[~keep] = lr[~keep]
    m = ssim_map(lr, lt)
    return m[e:-e, e:-e]


def score_region(ctx, reg, ctxd):
    """All region gates; returns the report row."""
    spec, thr, gates = ctx.spec, ctx.thr, ctx.gates
    name = reg["name"]
    x0, y0, x1, y1 = reg["bbox"]
    lab_c = spec.lab[y0:y1, x0:x1]
    cls_c = spec.cls[y0:y1, x0:x1]
    m = lab_c == reg["id"]
    units = []
    if reg["flow_rects"]:
        flow_any = np.zeros_like(m)
        for k, (a, b, c, dd) in enumerate(reg["flow_rects"]):
            fm = np.zeros_like(m)
            fm[max(0, b - y0):max(0, dd - y0), max(0, a - x0):max(0, c - x0)] = True
            units.append({"name": f"flow{k}", "kind": "flow", "mask": m & fm, "rect": (a, b, c, dd)})
            flow_any |= fm
        units.insert(0, {"name": "main", "kind": "fixed", "mask": m & ~flow_any, "rect": None})
    else:
        units.append({"name": "all", "kind": reg["kind"], "mask": m, "rect": None})

    def unit_of(px, py):
        for u in units[1:] if reg["flow_rects"] else []:
            a, b, c, dd = u["rect"]
            if a <= px < c and b <= py < dd:
                return u
        return units[0]

    runs = [r for r in spec.d["runs"] if r["region"] == name]
    lines = [l for l in spec.d["lines"] if l["region"] == name]
    probes = [p for p in spec.d["probes"] if p["region"] == name]
    row = {"region": name, "kind": reg["kind"], "units": [], "gates": {}, "bbox": reg["bbox"]}
    gate_bad = {g: 0.0 for g in ALL_GATES}
    gate_val = {}
    report_only = set()

    for u in units:
        uy, ux = np.nonzero(u["mask"])
        ys, xs = uy + y0, ux + x0
        ucls = cls_c[uy, ux]
        fvsel = (ucls == FLAT) | (ucls == VECTOR)
        arg, costs = best_shift(ctx, ys[fvsel], xs[fvsel])
        scored = arg if u["kind"] == "flow" else (0, 0)
        mx = max(abs(arg[0]), abs(arg[1]))
        if u["kind"] == "flow":
            g3 = badness(mx, HARD["flow_shift"])
        else:
            g3 = badness(mx, 0.5)
        ur = {"unit": u["name"], "kind": u["kind"], "argmin_shift": list(arg), "scored_shift": list(scored),
              "px": int(len(ys)), "g3_badness": g3}
        gate_bad["G3"] = max(gate_bad["G3"], g3)
        # G1 probes
        pr = []
        for p in probes:
            if unit_of(p["x"], p["y"]) is not u:
                continue
            sh = (0, 0) if p["class"] == "hard" else scored
            err = int(np.abs(ctx.tpix(np.array([p["y"]]), np.array([p["x"]]), *sh) - ctx.rpix(np.array([p["y"]]), np.array([p["x"]]))).max())
            pr.append({"id": p["id"], "class": p["class"], "err": err})
            if p["class"] == "vector":
                t = thr.get(name, "G1.vector")
                if t is None:
                    report_only.add("G1.vector")
                else:
                    gate_bad["G1"] = max(gate_bad["G1"], badness(err, t))
            else:
                gate_bad["G1"] = max(gate_bad["G1"], badness(err, HARD["g1_tol"]))
        ur["probes"] = pr
        # G2 border lines
        lr = []
        for l in lines:
            cx, cy = (l["rect"][0] + l["rect"][2]) // 2, (l["rect"][1] + l["rect"][3]) // 2
            if unit_of(cx, cy) is not u:
                continue
            lys, lxs = line_pixels(spec.lab, spec.cls, l)
            diff = np.abs(ctx.tpix(lys, lxs, *scored) - ctx.rpix(lys, lxs)).max(axis=1)
            frac_bad = float((diff > HARD["g2_tol"]).mean())
            lr.append({"id": l["id"], "name": l["name"], "side": l["side"], "px": int(len(lys)), "bad_frac": round(frac_bad, 4)})
            gate_bad["G2"] = max(gate_bad["G2"], badness(frac_bad, 1 - HARD["g2_ratio"]))
        ur["lines"] = lr
        # G4 classes
        cr = {}
        smap_cache = []
        for cname, cid in (("flat", FLAT), ("placeholder", PLACEHOLDER), ("vector", VECTOR), ("text", TEXT)):
            sel = ucls == cid
            if not sel.any():
                continue
            mad, bad = class_stats(ctx, ys[sel], xs[sel], scored)
            e = {"px": int(sel.sum()), "mad": round(mad, 4), "bad_frac": round(bad, 5), "mad_raw": mad}
            if cid == FLAT:
                e["badness"] = max(badness(mad, HARD["flat_mad"]), badness(bad, HARD["flat_bad_frac"]))
            elif cid == PLACEHOLDER:
                e["badness"] = badness(mad, HARD["placeholder_mad"])
            else:
                if not smap_cache:
                    smap_cache.append(unit_ssim(ctx, reg["bbox"], u["mask"], scored))
                smap = smap_cache[0]
                ssim_v = float(smap[uy[sel], ux[sel]].mean())
                e["ssim"] = round(ssim_v, 5)
                e["ssim_raw"] = ssim_v
                b = []
                tm, ts = thr.get(name, f"G4.{cname}.mad"), thr.get(name, f"G4.{cname}.ssim")
                if tm is None:
                    report_only.add(f"G4.{cname}.mad")
                else:
                    b.append(badness(mad, tm))
                if ts is None:
                    report_only.add(f"G4.{cname}.ssim")
                else:
                    b.append(badness(1 - ssim_v, 1 - ts))
                e["badness"] = max(b) if b else 0.0
            cr[cname] = e
            gate_bad["G4"] = max(gate_bad["G4"], e["badness"])
        ur["classes"] = cr
        # G5 text runs
        tr = []
        for r in runs:
            cx, cy = (r["window"][0] + r["window"][2]) // 2, (r["window"][1] + r["window"][3]) // 2
            if unit_of(cx, cy) is not u:
                continue
            s5 = score_text_run(ctx, r, scored)
            b = s5["bad"]
            t = thr.get(name, "G5.mass")
            if t is None:
                report_only.add("G5.mass")
            else:
                b = max(b, badness(s5["mass_rel"], t))
            s5["badness"] = b
            tr.append(s5)
            gate_bad["G5"] = max(gate_bad["G5"], b)
        ur["runs"] = tr
        row["units"].append(ur)
    row["gate_badness"] = {g: gate_bad[g] for g in ALL_GATES if g in ("G1", "G2", "G3", "G4", "G5")}
    row["report_only"] = sorted(report_only)
    return row


def score_g6(ctx):
    m = ctx.spec.lab == 0
    ys, xs = np.nonzero(m)
    diff = np.abs(ctx.tpix(ys, xs, 0, 0) - ctx.rpix(ys, xs)).max(axis=1) if len(ys) else np.zeros(1)
    full = np.abs(ctx.tpix(ys, xs, 0, 0) - ctx.rpix(ys, xs)).mean() if len(ys) else 0.0
    mx = float(diff.max())
    sm = ssim_map(ctx.lref[PAD:-PAD, PAD:-PAD], ctx.ltest[PAD:-PAD, PAD:-PAD])    # reported separately from the HUD (G7)
    return {"region": "world.open", "kind": "fixed",
            "units": [{"unit": "all", "mad": round(float(full), 5), "max": mx, "px": int(len(ys)), "ssim": round(float(sm[m].mean()), 5)}],
            "gate_badness": {"G6": max(badness(float(full), HARD["g6_mad"]), badness(mx, HARD["g6_max"]))}, "report_only": [], "bbox": [0, 0, W, H_PX]}


def check_alignment(ctx, selected_names):
    """Anchor edges (plan 4.1): a +-10 px search for the reference's edge (position and step, see
    make_regions.anchor_matches). Each result has status 'ok' (found at 0), 'shifted' (found only at a non-zero offset:
    ALIGNMENT FAIL, the run stops) or 'not_found' (no edge of that shape within +-10: ANCHOR WARN, regions are still
    scored, so a colour fault next to an edge is reported by G1/G2/G4 of its region, not as a capture fault)."""
    out = []
    all_names = list(ctx.spec.regions)
    for a in ctx.spec.d["anchors"]:
        if not any(re.search(g, n) for g in a["guards"] for n in selected_names):
            continue
        prof = anchor_samples(ctx.ref, a["orient"], a["edge"], a["along"], 0).astype(int)
        use = anchor_use(ctx.spec.cls, a["orient"], a["edge"], a["along"])
        key = (a["id"], a["orient"], a["edge"], a["along"], a["guards"])
        ref_unique = anchor_matches(prof, ctx.ref, key, use=use) == [0]
        m = anchor_matches(prof, ctx.test, key, use=use)
        if 0 in m:
            off, status = 0, "ok"
        elif m:
            off, status = min(m, key=abs), "shifted"
        else:
            off, status = None, "not_found"
        dx = (off if a["orient"] == "col" else 0) if off is not None else None
        dy = (off if a["orient"] == "row" else 0) if off is not None else None
        guarded = [n for n in all_names if any(re.search(g, n) for g in a["guards"])]
        out.append({"anchor": a["id"], "status": status, "ok": status != "shifted", "warn": status == "not_found",
                    "dx": dx, "dy": dy, "found": off is not None, "ref_unique": ref_unique, "guarded_regions": guarded})
    return out


def compare(ref, test, spec, thr=None, pair="A", only=None, gates=None, score_misaligned=False):
    """Score one capture. ref/test are uint8 (1080, 1920, 3). Returns the report dict.
    An anchor found at a non-zero offset sets alignment_fail and stops before the regions are scored, unless
    score_misaligned (calibrate.py only: negative N1 moves the command card by 1 px on purpose and still needs its region
    metrics); the result is FAIL either way."""
    thr = thr or Thr({}, pair)
    gates = [g for g in (gates or ALL_GATES)]
    t0 = time.time()
    ctx = Ctx(ref, test, spec, thr, gates)
    sel_re = re.compile(only) if only else None
    names = [n for n in spec.regions if (sel_re is None or sel_re.search(n))]
    if not names:
        raise ValueError(f"--only {only!r} matches no region")
    report = {"pair": pair, "only": only, "gates": gates, "alignment": [], "regions": {}, "uncalibrated": None}
    report["alignment"] = check_alignment(ctx, names)
    if any(not a["ok"] for a in report["alignment"]):
        report["alignment_fail"] = True
        if not score_misaligned:
            return report
    for n in names:
        if n == "world.open":
            report["regions"][n] = score_g6(ctx)
        else:
            report["regions"][n] = score_region(ctx, spec.regions[n], ctx)
    # pass / badness per region over the selected gates
    for n, row in report["regions"].items():
        gb = {g: v for g, v in row["gate_badness"].items() if g in gates}
        worst_g = max(gb, key=lambda g: gb[g]) if gb else None
        row["badness"] = gb[worst_g] if worst_g else 0.0
        row["worst_gate"] = worst_g if row["badness"] > 0 else None     # nothing wrong: no worst gate
        row["failing_gates"] = [g for g, v in gb.items() if v > 1 + EPS]
        row["pass"] = not row["failing_gates"]
    # G7 over the selected HUD pixels (and world.open separately when selected)
    hud_names = [n for n in names if n != "world.open"]
    if hud_names:
        ids = [spec.regions[n]["id"] for n in hud_names]
        mask = np.isin(spec.lab, ids)
        ys, xs = np.nonzero(mask)
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
        mad = float(np.abs(ctx.tpix(ys, xs, 0, 0) - ctx.rpix(ys, xs)).mean())
        e = 6
        sl = (slice(y0 - e + PAD, y1 + e + PAD), slice(x0 - e + PAD, x1 + e + PAD))
        sm = ssim_map(ctx.lref[sl], ctx.ltest[sl])[e:-e, e:-e]
        ssim_v = float(sm[mask[y0:y1, x0:x1]].mean())
        g7 = {"mad": mad, "ssim": ssim_v, "px": int(mask.sum())}
        tm, ts = thr.get(None, "G7.mad"), thr.get(None, "G7.ssim")
        b = []
        if tm is not None:
            b.append(badness(mad, tm))
        if ts is not None:
            b.append(badness(1 - ssim_v, 1 - ts))
        g7["badness"] = max(b) if b else 0.0
        g7["report_only"] = tm is None or ts is None
        report["g7"] = g7
    report["n_regions"] = len(report["regions"])
    failing = [n for n, r in report["regions"].items() if not r["pass"]]
    if "G7" in gates and report.get("g7") and report["g7"]["badness"] > 1 + EPS:
        report["g7_fail"] = True
    report["failing"] = failing
    worst = max(report["regions"].items(), key=lambda kv: kv[1]["badness"]) if report["regions"] else None
    report["worst"] = ({"region": worst[0], "badness": worst[1]["badness"], "gate": worst[1]["worst_gate"] or "-"}
                       if worst and worst[1]["badness"] > 0 else {"region": "-", "badness": 0.0, "gate": "-"})
    report["result"] = "PASS" if not failing and not report.get("g7_fail") and not report.get("alignment_fail") else "FAIL"
    report["seconds"] = round(time.time() - t0, 2)
    return report


def pair_line(report):
    if report.get("alignment_fail"):
        bad = [a for a in report["alignment"] if not a["ok"]]
        return f"PAIR {report['pair']}: RESULT FAIL alignment ({', '.join(a['anchor'] for a in bad)})"
    g7 = report.get("g7") or {}
    w = report["worst"] or {"region": "-", "badness": 0.0, "gate": "-"}
    wb = "inf" if w["badness"] == float("inf") else f"{w['badness']:.3f}"
    return (f"PAIR {report['pair']}: RESULT {report['result']} {len(report['failing'])}/{report['n_regions']} regions failing; "
            f"HUD MAD {g7.get('mad', 0):.3f}; HUD SSIM {g7.get('ssim', 1):.4f}; WORST {w['region']} badness {wb} ({w['gate']})")


def soft_metrics(report):
    """Every soft metric of a report as {region: {metric: value}} (region '*' = G7), aggregated over a region's units:
    MADs, G5.mass and G1.vector as the worst (max), SSIMs as the worst (min). calibrate.py derives thresholds from these."""
    out = {}
    for n, r in report["regions"].items():
        m = {}
        for u in r["units"]:
            for cname in ("text", "vector"):
                c = u.get("classes", {}).get(cname)
                if c:   # unrounded values: the gates compare these, so thresholds must be derived from them too
                    m[f"G4.{cname}.mad"] = max(m.get(f"G4.{cname}.mad", 0.0), c.get("mad_raw", c["mad"]))
                    m[f"G4.{cname}.ssim"] = min(m.get(f"G4.{cname}.ssim", 1.0), c.get("ssim_raw", c["ssim"]))
            for t in u.get("runs", []):
                m["G5.mass"] = max(m.get("G5.mass", 0.0), t["mass_rel"])
            for p in u.get("probes", []):
                if p["class"] == "vector":
                    m["G1.vector"] = max(m.get("G1.vector", 0), p["err"])
        if m:
            out[n] = m
    if report.get("g7"):
        out["*"] = {"G7.mad": report["g7"]["mad"], "G7.ssim": report["g7"]["ssim"]}
    return out


# ---------------------------------------------------------------- outputs (plan 4.5)
def _font(size):
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def heat_rgb(diff):
    """max-channel |diff| -> colour: 0 black, 8 blue, 32 yellow, >= 64 red."""
    stops = np.array([0, 8, 32, 64], float)
    cols = np.array([[0, 0, 0], [0, 0, 255], [255, 255, 0], [255, 0, 0]], float)
    d = np.clip(diff.astype(float), 0, 64)
    return np.stack([np.interp(d, stops, cols[:, c]) for c in range(3)], axis=-1).astype(np.uint8)


def worst_window(diff, bbox, w=160, h=100):
    """Top-left of the w x h window inside bbox with the largest diff sum (bbox smaller than the window: the bbox)."""
    x0, y0, x1, y1 = bbox
    if x1 - x0 <= w and y1 - y0 <= h:
        return x0, y0, x1, y1
    sub = diff[y0:y1, x0:x1].astype(float)
    ww, hh = min(w, x1 - x0), min(h, y1 - y0)
    s = uniform_filter(sub, size=(hh, ww), mode="constant")
    cy, cx = np.unravel_index(int(np.argmax(s)), s.shape)
    ax0 = int(np.clip(x0 + cx - ww // 2, x0, x1 - ww))
    ay0 = int(np.clip(y0 + cy - hh // 2, y0, y1 - hh))
    return ax0, ay0, ax0 + ww, ay0 + hh


def write_outputs(report, ref, test, spec, outdir, tag=""):
    outdir = Path(outdir)
    (outdir / "crops").mkdir(parents=True, exist_ok=True)
    diff = np.abs(ref.astype(np.int16) - test.astype(np.int16)).max(axis=2)
    heat = Image.fromarray(heat_rgb(diff))
    dr = ImageDraw.Draw(heat)
    f = _font(13)
    for n, r in report["regions"].items():
        if n == "world.open":
            continue
        x0, y0, x1, y1 = r["bbox"]
        col = (0, 255, 0) if r["pass"] else (255, 40, 40)
        dr.rectangle([x0, y0, x1 - 1, y1 - 1], outline=col)
        dr.text((x0 + 2, max(0, y0 - 14)), n, fill=col, font=f)
    heat.save(outdir / "diff_heat.png")
    sbs = Image.new("RGB", (2 * W, H_PX))
    sbs.paste(Image.fromarray(ref), (0, 0))
    sbs.paste(Image.fromarray(test), (W, 0))
    sbs.save(outdir / "side_by_side.png")
    sbs.resize((W, H_PX // 2), Image.LANCZOS).save(outdir / "side_by_side_phone.png")
    # 5 worst crops at 4x: ref | capture | diff
    ranked = sorted((r for n, r in report["regions"].items() if n != "world.open" and r["badness"] > 0),
                    key=lambda r: (-r["badness"], r["region"]))[:5]          # only regions with something wrong
    worst_crop = None
    for r in ranked:
        wx0, wy0, wx1, wy1 = worst_window(diff, r["bbox"])
        a, b = ref[wy0:wy1, wx0:wx1], test[wy0:wy1, wx0:wx1]
        c = heat_rgb(diff[wy0:wy1, wx0:wx1])
        trip = np.concatenate([a, b, c], axis=1)
        im = Image.fromarray(trip).resize((trip.shape[1] * 4, trip.shape[0] * 4), Image.NEAREST)
        canvas = Image.new("RGB", (im.width, im.height + 22), (11, 12, 14))
        canvas.paste(im, (0, 22))
        ImageDraw.Draw(canvas).text((4, 3), f"{r['region']}  badness {r['badness']:.2f} ({r['worst_gate']})   ref | capture | diff", fill=(236, 230, 216), font=_font(15))
        canvas.save(outdir / "crops" / f"{r['region']}.png")
        if worst_crop is None:
            worst_crop = (a, b)
    if worst_crop is not None:
        fr = [Image.fromarray(x).resize((x.shape[1] * 3, x.shape[0] * 3), Image.NEAREST) for x in worst_crop]
        fr[0].save(outdir / "flicker_worst.gif", save_all=True, append_images=[fr[1]], duration=600, loop=0)
    # scorecard: heat map + region table (failing first)
    card = Image.new("RGB", (1920, 760), (11, 12, 14))
    card.paste(heat.resize((960, 540), Image.LANCZOS), (0, 60))
    d2 = ImageDraw.Draw(card)
    d2.text((12, 12), f"{tag} {pair_line(report)}", fill=(236, 230, 216), font=_font(18))
    rows = sorted(report["regions"].values(), key=lambda r: (r["pass"], -r["badness"]))
    ft = _font(14)
    y = 44
    d2.text((980, y), "region", fill=(154, 149, 138), font=ft)
    d2.text((1200, y), "status", fill=(154, 149, 138), font=ft)
    d2.text((1290, y), "badness", fill=(154, 149, 138), font=ft)
    d2.text((1390, y), "worst gate", fill=(154, 149, 138), font=ft)
    d2.text((1500, y), "shift", fill=(154, 149, 138), font=ft)
    y += 20
    for r in rows[:38]:
        col = (79, 179, 154) if r["pass"] else (226, 115, 95)
        sh = ",".join(f"{u['scored_shift'][0]}/{u['scored_shift'][1]}" for u in r["units"] if "scored_shift" in u) or "-"
        bd = "inf" if r["badness"] == float("inf") else f"{r['badness']:.2f}"
        d2.text((980, y), r["region"], fill=(236, 230, 216), font=ft)
        d2.text((1200, y), "PASS" if r["pass"] else "FAIL", fill=col, font=ft)
        d2.text((1290, y), bd, fill=col, font=ft)
        d2.text((1390, y), r["worst_gate"] or "-", fill=(236, 230, 216), font=ft)
        d2.text((1500, y), sh, fill=(236, 230, 216), font=ft)
        y += 17
    card.save(outdir / "scorecard.png")
    # report.json / report.md
    with open(outdir / "report.json", "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=1, default=lambda o: None if o != o else str(o))
    lines = [f"# HUD compare {tag}", "", pair_line(report), ""]
    if report.get("alignment"):
        lines += ["## Alignment", ""] + [f"- {a['anchor']}: {a['status']} dx={a['dx']} dy={a['dy']}" for a in report["alignment"]] + [""]
    lines += ["## Regions (failing first)", "", "| region | status | badness | worst gate | failing gates | shift |", "|---|---|---|---|---|---|"]
    for r in rows:
        sh = " ".join(f"{u['unit']}:{u['scored_shift'][0]},{u['scored_shift'][1]}" for u in r["units"] if "scored_shift" in u)
        lines.append(f"| {r['region']} | {'PASS' if r['pass'] else 'FAIL'} | {r['badness']:.3f} | {r['worst_gate'] or '-'} | {' '.join(r['failing_gates'])} | {sh} |")
    ro = sorted({m for r in report["regions"].values() for m in r.get("report_only", [])})
    if ro:
        lines += ["", "Report-only (no calibrated threshold): " + ", ".join(ro)]
    (outdir / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


# ---------------------------------------------------------------- T3 helper modes
BLEND_BOXES = [(100, 100, 200, 200, "#ECE6D8", 0.50), (400, 100, 200, 200, "#0B0C0E", 0.25),
               (700, 100, 200, 200, "#ECE6D8", 0.25), (1000, 100, 200, 200, "#0B0C0E", 0.50)]


def _hex(s):
    s = s.lstrip("#")
    return np.array([int(s[i:i + 2], 16) for i in (0, 2, 4)], float)


def pipeline_check(ref_path, shot_path, max_allowed=0):
    a, b = load_rgb(ref_path), load_rgb(shot_path)
    if a.shape != b.shape or a.shape != (H_PX, W, 3):
        print(f"PIPELINE FAIL size {a.shape} vs {b.shape}")
        return 1
    d = np.abs(a.astype(int) - b.astype(int))
    mad, mx = float(d.mean()), int(d.max())
    ok = mx <= max_allowed
    print(f"PIPELINE {'OK' if ok else 'FAIL'} MAD {mad:.3f} max {mx}")
    return 0 if ok else 1


def blend_check(backdrop, shot_path, boxes=None):
    """Alpha boxes drawn over a backdrop must equal the sRGB-byte blend (gamma space), not a linear-light blend."""
    shot = load_rgb(shot_path).astype(float)
    if backdrop.startswith("#"):
        base = np.broadcast_to(_hex(backdrop), (H_PX, W, 3))
    else:
        base = load_rgb(backdrop).astype(float)
    worst_g, worst_l = 0.0, 0.0
    for x, y, w, h, col, a in (boxes or BLEND_BOXES):
        c = _hex(col)
        under = base[y:y + h, x:x + w]
        gam = np.floor(under * (1 - a) + c * a + 0.5)
        lin = lambda v: np.where(v <= 10.31475, v / 3294.6, ((v / 255 + 0.055) / 1.055) ** 2.4)
        enc = lambda v: np.where(v <= 0.0031308, v * 3294.6, 255 * (1.055 * np.power(np.maximum(v, 1e-12), 1 / 2.4) - 0.055))
        linb = np.floor(enc(lin(under) * (1 - a) + lin(c) * a) + 0.5)
        got = shot[y:y + h, x:x + w]
        worst_g = max(worst_g, float(np.abs(got - gam).max()))
        worst_l = max(worst_l, float(np.abs(got - linb).max()))
    ok = worst_g <= 1
    print(f"BLEND {'OK' if ok else 'FAIL'} gamma-space max err <= 1 (measured {worst_g:.0f}; linear-space prediction would be off by {worst_l:.0f})")
    return 0 if ok else 1


def parse_boxes(s):
    out = []
    for part in s.split(";"):
        x, y, w, h, c, a = part.strip().split(",")
        out.append((int(x), int(y), int(w), int(h), c, float(a)))
    return out


def append_convergence(note, pair=None):
    new = not CONVERGENCE_CSV.is_file()
    CONVERGENCE_CSV.parent.mkdir(parents=True, exist_ok=True)
    with open(CONVERGENCE_CSV, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["iteration", "time", "failing_A", "failing_B", "worst_region", "badness", "hud_mad", "hud_ssim", "tree_hash", "note"])
        w.writerow(["", time.strftime("%Y-%m-%d %H:%M:%S"), "", "", "", "", "", "", "", f"RECALIBRATED{(' pair ' + pair) if pair else ''}: {note}"])


# ---------------------------------------------------------------- CLI
def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--ref")
    ap.add_argument("--shot")
    ap.add_argument("--pair", default="A", choices=["A", "B"])
    ap.add_argument("--regions", default=str(REGIONS_JSON))
    ap.add_argument("--thresholds", default=str(THRESHOLDS_JSON))
    ap.add_argument("--only", help="regex over region names")
    ap.add_argument("--gates", help="comma list of gates that decide PASS/FAIL (all are still reported)")
    ap.add_argument("--out", help="output folder (default H/HudRef/out/<tag>)")
    ap.add_argument("--tag", default="run")
    ap.add_argument("--no-images", action="store_true")
    ap.add_argument("--calibration", default=str(CALIBRATION_JSON), help="frozen hashes (results/calibration.json)")
    ap.add_argument("--recalibrated", metavar="NOTE", help="main session only: score despite a calibration hash mismatch (logged)")
    ap.add_argument("--uncalibrated", action="store_true",
                    help="pre-T2 development only: score although thresholds.json exists without results/calibration.json")
    ap.add_argument("--pipeline", nargs=2, metavar=("REF", "SHOT"))
    ap.add_argument("--max", type=int, default=0, help="--pipeline: allowed max channel difference")
    ap.add_argument("--blend", action="store_true")
    ap.add_argument("--backdrop")
    ap.add_argument("--boxes")
    a = ap.parse_args(argv)
    if a.pipeline:
        return pipeline_check(a.pipeline[0], a.pipeline[1], a.max)
    if a.blend:
        if not (a.backdrop and a.shot):
            print("--blend needs --backdrop and --shot", file=sys.stderr)
            return 2
        return blend_check(a.backdrop, a.shot, parse_boxes(a.boxes) if a.boxes else None)
    refp = Path(a.ref) if a.ref else find_ref(a.pair)
    if not (refp and refp.is_file() and a.shot and Path(a.shot).is_file()):
        print("need --ref and --shot PNGs", file=sys.stderr)
        return 2
    status, problems = check_calibration(a.thresholds, a.regions, {a.pair: refp}, a.calibration)
    if status == "UNCALIBRATED" and Path(a.thresholds).is_file() and not (a.recalibrated or a.uncalibrated):
        # once T2 has frozen thresholds.json, a missing calibration.json must not silently bypass the hash check
        print(f"CALIBRATION MISSING (refusing to score): {a.thresholds} exists but {a.calibration} does not "
              "(pass --recalibrated NOTE, or --uncalibrated before T2)", file=sys.stderr)
        return 2
    if status == "MISMATCH" and not a.recalibrated:
        print("CALIBRATION HASH MISMATCH (refusing to score): " + "; ".join(problems), file=sys.stderr)
        return 2
    if a.recalibrated and status != "OK":
        append_convergence(a.recalibrated, a.pair)
        print(f"RECALIBRATED override logged to {CONVERGENCE_CSV.name}: {a.recalibrated}")
    if status == "OK":
        print(f"CALIBRATION OK thresholds {sha256_file(a.thresholds)[:12]} regions {sha256_file(a.regions)[:12]} "
              f"ref{a.pair} {sha256_file(refp)[:12]}")
    ref, shot = load_rgb(refp), load_rgb(a.shot)
    for nm, im in (("reference", ref), ("capture", shot)):
        if im.shape != (H_PX, W, 3):
            print(f"{nm} must be {W}x{H_PX}, got {im.shape[1]}x{im.shape[0]}", file=sys.stderr)
            return 2
    spec = load_spec(a.regions)
    thr = Thr(load_thresholds(a.thresholds), a.pair)
    gates = a.gates.split(",") if a.gates else None
    rep = compare(ref, shot, spec, thr, a.pair, a.only, gates)
    rep["calibration"] = status
    rep["uncalibrated"] = status == "UNCALIBRATED"
    outdir = Path(a.out) if a.out else HUDREF / "out" / a.tag
    outdir.mkdir(parents=True, exist_ok=True)
    for al in rep["alignment"]:
        if al["warn"]:
            print(f"ANCHOR WARN {al['anchor']} not found within +-{ANCHOR_RANGE} px (regions scored anyway)")
    if rep.get("alignment_fail"):
        for al in rep["alignment"]:
            if not al["ok"]:
                dxs = "?" if al["dx"] is None else al["dx"]
                dys = "?" if al["dy"] is None else al["dy"]
                print(f"ALIGNMENT FAIL {al['anchor']} dx={dxs} dy={dys} (guards {' '.join(al['guarded_regions'])})")
        print(pair_line(rep))
        with open(outdir / "report.json", "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=1)
        return 3
    if not a.no_images:
        write_outputs(rep, ref, shot, spec, outdir, a.tag)
    else:
        with open(outdir / "report.json", "w", encoding="utf-8") as fh:
            json.dump(rep, fh, indent=1, default=str)
    if status == "UNCALIBRATED":
        print("note: UNCALIBRATED (no results/calibration.json): soft gates are report-only unless thresholds.json sets them")
    print(pair_line(rep))
    return 0 if rep["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
