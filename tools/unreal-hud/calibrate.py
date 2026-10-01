#!/usr/bin/env python3
"""calibrate.py: freeze the soft thresholds of the HUD comparator from the calibration controls (plan B 4.4; EXECUTION section 7).

For each pair (A over flat #14161A, B over the mockup world) the controls are scored with hud_compare.compare against the pair's
reference with every soft metric report-only (pass 1, so a pass or fail then means the hard gates alone):
  positives P1 (static TTFs), P3 (.4/.3 px offset), P4 (P1+P3), P5 (Slate text model, make_p5.py at the chosen hinting, start
  None) and P6 (vector AA model, make_p6.py: the board at deviceScaleFactor 4, box-filtered, vector-class pixels only; T2
  ruling R4) must pass every hard gate in every region; P2 (font-kerning:none) is dropped, EXECUTION section 7;
  negatives N1..N8 each change one thing in the regions listed in ref_controls.json.
Soft metrics (hud_compare.soft_metrics, unrounded): G4.text.mad G4.vector.mad (max), G4.text.ssim G4.vector.ssim (min),
G5.mass (max), G1.vector (max), per region; G7.mad (max) and G7.ssim (min) for the HUD summary.
Rule (plan 4.4): thr = 1.5 x the worst positive, and it must lie below best negative / 1.2 (for SSIM both sides are deficits,
1 - ssim); otherwise the metric is REPORT-ONLY (no threshold, named). Two T2 refinements, ratified by the main session
(EXECUTION section 7, T2 ruling R3 and R4) and recorded in calibration.json:
  * "best negative" is taken over the negatives intended in that region that pass every HARD gate there in pass 1 and move
    that metric at all (value != perfect). A negative already caught by a hard gate in a region needs no soft threshold
    there, and one that leaves a metric untouched (N6, an icon swap, has text MAD 0 in card.S) is no evidence about it, so
    neither can veto a threshold; this only adds gates, and pass 2 still requires every negative to fail. A metric with no
    such negative gets its 1.5 x threshold and is listed as unvalidated_by_a_negative. (G7: every negative that passes
    every hard gate and moves the metric.)
  * a metric where EVERY positive measures exactly 0 (MAD 0, SSIM 1, probe error 0: no positive control touches those pixels)
    is report-only, listed as report_only_no_positive: the controls carry no information about the anti-aliasing allowed
    there, and a 0 threshold would fail any real render. Since ruling R4 this is a last resort only: P6 moves the vector
    pixels of every region, so the vector metrics of the ornament, ring, plate, map, portrait, card.V and menu regions get
    real thresholds; whatever is still listed rests on its hard gates.
Then every control is re-scored with the thresholds (pass 2): positives must pass everything, every negative must fail in at
least one of its intended regions and in none outside them, with changed pixels outside its intended boxes = 0 (pair B: up to
16 one-level world-photo pixels in world.open are photo noise, exactly ref_check.py's rule).
A positive failing a hard gate, or a negative passing every gate, stops calibration (exit 1). Nothing is written unless every
verdict of both pairs is OK. If every text soft metric of a region is report-only, calibration still finishes and ESCALATE is
printed (T4a waits for the main session); the escalation list is printed on the STOP path too.

Outputs (only when CALIBRATION OK on both pairs): thresholds.json, results/calibration.json (sha256 of thresholds, regions,
refs, controls, P6 inputs, calibrated code, freetype-py version, rulings R1-R6) and, when missing, results/calibration_log.csv
with its header (the main session appends the commit hash). controls/{A,B}/P5.png and P6.png are written by the OK path when
they differ from the in-memory render.

    python calibrate.py [--hinting None|Default|AutoLight|Auto] [--no-write] [--diagnose]
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import binary_dilation

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hud_common import CALIBRATION_JSON, HUDREF, calibrated_code_hashes, REGIONS_JSON, THRESHOLDS_JSON, load_alpha, load_rgb, sha256_file
from hud_compare import HARD, Thr, compare, load_spec, soft_metrics
from make_p5 import FT_LOAD, make_p5, versions
from make_p6 import INPUTS as P6_INPUTS, RAW as P6_RAW, layout_check as p6_layout_check, make_p6

T = Path(__file__).resolve().parent
REF = HUDREF / "ref"
LOG_CSV = T / "results" / "calibration_log.csv"
LOG_HEADER = ["date", "commit", "thresholds_sha256", "regions_sha256", "ref_A_sha256", "ref_B_sha256", "note"]
PAIR_REF = {"A": "ref_hudonly.png", "B": "ref_backdrop.png"}
POS_CHROMIUM = ["P1", "P3", "P4"]          # P2 dropped (EXECUTION section 7)
POSITIVES = POS_CHROMIUM + ["P5", "P6"]     # P5 Slate text model, P6 vector AA model (ruling R4)
MODELLED = ("P5", "P6")                      # rendered in memory by make_p5 / make_p6, never read back from disk
NEGATIVES = [f"N{i}" for i in range(1, 9)]
MAX_METRICS = ("G4.text.mad", "G4.vector.mad", "G5.mass", "G1.vector")
MIN_METRICS = ("G4.text.ssim", "G4.vector.ssim")
TEXT_METRICS = ("G4.text.mad", "G4.text.ssim", "G5.mass")
MARGIN_POS, MARGIN_NEG = 1.5, 1.2
PHOTO_NOISE_MAX = 16      # = ref_check.PHOTO_NOISE_MAX (pair B only)
ZERO = 1e-12


def ceil6(v):
    return math.ceil(v * 1e6 - 1e-9) / 1e6


def floor6(v):
    return math.floor(v * 1e6 + 1e-9) / 1e6


def deficit(metric, v):
    """Distance from perfect: SSIM metrics are deficits 1 - ssim, the others are already errors."""
    return (1.0 - v) if metric in MIN_METRICS or metric == "G7.ssim" else v


def load_controls_spec():
    return json.load(open(T / "ref_controls.json", encoding="utf-8"))["controls"]


def control_image(pair, name, hinting):
    """Control image; P5 and P6 are rendered in memory (make_p5 and make_p6 are deterministic), never written here."""
    if name == "P5":
        img, _ = make_p5(pair, hinting, None)
        return img
    if name == "P6":
        img, _ = make_p6(pair, None)
        return img
    return load_rgb(REF / "controls" / pair / f"{name}.png")


def inside_mask(boxes, shape):
    m = np.zeros(shape, bool)
    for x0, y0, x1, y1 in boxes:
        m[max(y0, 0):y1, max(x0, 0):x1] = True
    return m


def changed_counts(pair, base, img, spec_c, open_mask):
    """Changed pixels against the pair's reference, split by the control's intended boxes. Mirrors ref_check.check_controls:
    pair B only, 1-level differences in world.open outside the intended boxes are photo noise (allowed up to 16 px)."""
    d = np.abs(img.astype(int) - base.astype(int)).max(axis=2)
    ch = d > 0
    out = {"changed": int(ch.sum()), "changed_in_hud_mask": int((ch & ~open_mask).sum())}
    if spec_c.get("intended_boxes"):
        inside = inside_mask([tuple(b) for b in spec_c["intended_boxes"]], ch.shape)
        noise = (ch & ~inside & open_mask & (d == 1)) if pair == "B" else np.zeros_like(ch)
        out.update(inside=int((ch & inside).sum()), outside=int((ch & ~inside & ~noise).sum()), photo_noise=int(noise.sum()),
                   ok=bool((ch & inside).any()) and int((ch & ~inside & ~noise).sum()) == 0 and int(noise.sum()) <= PHOTO_NOISE_MAX)
    else:
        out["ok"] = out["changed"] > 0
    return out


def score(ref, img, spec, thr, pair):
    """hud_compare.compare with score_misaligned: a control that trips an anchor (N1: the command card moved by 1 px, anchor
    card.Q_top) still gets its region metrics, and the anchor failure counts as a failure of every region it guards."""
    rep = compare(ref, img, spec, thr, pair, score_misaligned=True)
    rep["align_failed_regions"] = sorted({g for a in rep["alignment"] if not a["ok"] for g in a["guarded_regions"]})
    return rep


def hard_failures(rep):
    out = {n: {"gates": list(r["failing_gates"]), "badness": r["badness"]} for n, r in rep["regions"].items() if not r["pass"]}
    for n in rep.get("align_failed_regions", []):
        out.setdefault(n, {"gates": [], "badness": 0.0})["gates"].append("alignment")
    return out


def jsonable(o):
    """inf/nan -> strings, so calibration.json stays strict JSON."""
    if isinstance(o, float) and not math.isfinite(o):
        return str(o)
    if isinstance(o, dict):
        return {k: jsonable(v) for k, v in o.items()}
    if isinstance(o, (list, tuple)):
        return [jsonable(v) for v in o]
    return o


def fmt_bad(v):
    return "inf" if v == float("inf") else f"{v:.2f}"


def heaviest_run(rep, regions=None):
    """Largest signed ink-mass error (test heavier than the reference) over the runs of the given regions (all when None)."""
    v = [t.get("mass_signed", 0.0) for n, r in rep["regions"].items() if regions is None or n in regions
         for u in r["units"] for t in u.get("runs", [])]
    return max(v) if v else float("nan")


def derive(metric, pos, negs):
    """(threshold or None, reason). pos: the positives' values; negs: the eligible negatives' values."""
    worst = max(pos) if metric in MAX_METRICS or metric == "G7.mad" else min(pos)
    if deficit(metric, worst) <= ZERO:
        return None, "no_positive"
    if metric in MAX_METRICS or metric == "G7.mad":
        thr = ceil6(MARGIN_POS * worst)
        thr_def = thr
    else:
        thr = floor6(1.0 - MARGIN_POS * (1.0 - worst))
        thr_def = 1.0 - thr
    if not negs:
        return thr, "unvalidated"
    best = min(deficit(metric, v) for v in negs)
    if best > 0 and best >= MARGIN_NEG * thr_def:
        return thr, "validated"
    return None, "report_only"


def calibrate_pair(pair, hinting, spec, ctl):
    ref = load_rgb(REF / PAIR_REF[pair])
    alpha = load_alpha(REF / "hud_alpha.png")
    open_mask = ~binary_dilation(alpha > 0, structure=np.ones((3, 3), bool), iterations=3)
    names = POSITIVES + NEGATIVES
    imgs = {n: control_image(pair, n, hinting) for n in names}
    # ---- pass 1: hard gates only (empty thresholds => every soft metric is report-only)
    rep1, soft, counts, hard1 = {}, {}, {}, {}
    for n in names:
        rep1[n] = score(ref, imgs[n], spec, Thr({}, pair), pair)
        soft[n] = soft_metrics(rep1[n])
        counts[n] = changed_counts(pair, ref, imgs[n], ctl[n], open_mask)
        hard1[n] = hard_failures(rep1[n])
    pos_hard_fail = {n: hard1[n] for n in POSITIVES if hard1[n] or rep1[n].get("alignment_fail")}
    # ---- thresholds
    regions, report_only, no_positive, unvalidated = {}, [], [], []
    all_regions = sorted({r for n in POSITIVES for r in soft[n] if r != "*"})
    for r in all_regions:
        metrics = sorted({m for n in POSITIVES for m in soft[n].get(r, {})})
        # negatives intended here that the hard gates do not already catch here
        eligible = [n for n in NEGATIVES if r in ctl[n]["intended_regions"] and r not in hard1[n]]
        for m in metrics:
            pos = [soft[n][r][m] for n in POSITIVES if m in soft[n].get(r, {})]
            negs = [soft[n][r][m] for n in eligible if m in soft[n].get(r, {}) and deficit(m, soft[n][r][m]) > ZERO]
            thr, why = derive(m, pos, negs)
            key = f"{r}:{m}"
            if thr is not None:
                regions.setdefault(r, {})[m] = thr
                if why == "unvalidated":
                    unvalidated.append(key)
            elif why == "no_positive":
                no_positive.append(key)
            else:
                report_only.append(key)
    default = {}
    for m in ("G7.mad", "G7.ssim"):
        pos = [soft[n]["*"][m] for n in POSITIVES]
        negs = [soft[n]["*"][m] for n in NEGATIVES if not hard1[n] and deficit(m, soft[n]["*"][m]) > ZERO]
        thr, why = derive(m, pos, negs)
        if thr is not None:
            default[m] = thr
            if why == "unvalidated":
                unvalidated.append(f"*:{m}")
        else:
            (no_positive if why == "no_positive" else report_only).append(f"*:{m}")
    thresholds = {"regions": regions, "default": default}
    # ---- pass 2: re-score with the thresholds
    thr_obj = Thr({pair: thresholds}, pair)
    rep2, verdict = {}, {}
    for n in names:
        rep2[n] = score(ref, imgs[n], spec, thr_obj, pair)
        fail = {r for r, row in rep2[n]["regions"].items() if not row["pass"]} | set(rep2[n]["align_failed_regions"])
        g7_fail = bool(rep2[n].get("g7_fail"))
        if n in POSITIVES:
            verdict[n] = {"ok": not fail and not g7_fail and counts[n]["ok"], "failing_regions": sorted(fail), "g7_fail": g7_fail}
        else:
            intended = set(ctl[n]["intended_regions"])
            inside, outside = sorted(fail & intended), sorted(fail - intended)
            verdict[n] = {"ok": bool(inside) and not outside and counts[n]["ok"],
                          "failing_regions": sorted(fail), "failing_intended": inside, "failing_outside": outside,
                          "g7_fail": g7_fail, "passes_every_gate": not fail and not g7_fail}
    # text soft metrics that ended up without a threshold in a region
    escalate = []
    for r in all_regions:
        have = [m for m in TEXT_METRICS if any(m in soft[n].get(r, {}) for n in POSITIVES)]
        if have and not any(m in regions.get(r, {}) for m in have):
            escalate.append({"region": r, "metrics": have,
                             "why": sorted({("no positive" if f"{r}:{m}" in no_positive else "report-only") for m in have})})
    gaps = vector_gaps(pair, thresholds, no_positive, report_only)
    return {"vector_gaps": gaps, "imgs": imgs, "rep1": rep1, "rep2": rep2, "soft": soft, "counts": counts, "pos_hard_fail": pos_hard_fail,
            "hard1": hard1, "thresholds": thresholds, "report_only": sorted(report_only), "no_positive": sorted(no_positive),
            "unvalidated": sorted(unvalidated), "verdict": verdict, "escalate": escalate}


def print_table(pair, res):
    print(f"\n=== pair {pair}: control table (changed px vs {PAIR_REF[pair]}; worst soft values over regions in pass 1; "
          f"heavy = largest signed ink-mass error of any run, hard limit +{HARD['mass_heavy']}) ===")
    print(f"{'ctl':3s} {'kind':8s} {'changed':>8s} {'inside':>7s} {'outside':>7s} {'hardfail':>8s} {'textMAD':>8s} {'textSSIM':>8s} "
          f"{'vecMAD':>7s} {'vecSSIM':>8s} {'massRel':>8s} {'heavy':>7s} {'HUDmad':>7s} {'HUDssim':>8s}  after thresholds (pass 2)")
    for n in POSITIVES + NEGATIVES:
        s, c = res["soft"][n], res["counts"][n]

        def agg(m, f):
            v = [x[m] for k, x in s.items() if k != "*" and m in x]
            return f(v) if v else float("nan")
        v = res["verdict"][n]
        hf = len(res["hard1"][n])
        kind = "positive" if n in POSITIVES else "negative"
        tail = ("PASS every gate" if v["ok"] else f"FAIL in {v['failing_regions']}{' + G7' if v['g7_fail'] else ''}") if kind == "positive" else \
            f"fails {len(v['failing_regions'])} regions: intended {len(v['failing_intended'])}, outside {v['failing_outside']}{' + G7' if v['g7_fail'] else ''}"
        print(f"{n:3s} {kind:8s} {c['changed']:8d} {c.get('inside', 0):7d} {c.get('outside', 0):7d} {hf:8d} "
              f"{agg('G4.text.mad', max):8.3f} {agg('G4.text.ssim', min):8.4f} {agg('G4.vector.mad', max):7.3f} "
              f"{agg('G4.vector.ssim', min):8.4f} {agg('G5.mass', max):8.3f} {heaviest_run(res['rep1'][n]):+7.3f} "
              f"{s['*']['G7.mad']:7.3f} {s['*']['G7.ssim']:8.5f}  {tail}"
              + (f" (+{c['photo_noise']} px photo noise)" if c.get("photo_noise") else ""))
    for n in NEGATIVES:
        print(f"    {n} hard failures in pass 1: " + ("; ".join(f"{r} {f['gates']}" for r, f in sorted(res["hard1"][n].items())) or "none"))


def print_escalations(escalations):
    for pair, e in escalations:
        print(f"ESCALATE: pair {pair} region {e['region']}: every text soft metric is report-only "
              f"({', '.join(e['metrics'])}; {', '.join(e['why'])})")


def modelled_status(results):
    """[(pair, name, path, the in-memory P5/P6 equals the file on disk)]."""
    out = []
    for pair, res in results.items():
        for n in MODELLED:
            p = REF / "controls" / pair / f"{n}.png"
            out.append((pair, n, p, p.is_file() and np.array_equal(load_rgb(p), res["imgs"][n])))
    return out


def g7_derivation(res):
    """Why G7.mad / G7.ssim got (or did not get) a threshold (ruling R6): the positives' worst, the 1.5x candidate, and the
    negatives that pass every hard gate (the only ones a summary threshold could be validated by) with their values."""
    out = {}
    for m in ("G7.mad", "G7.ssim"):
        pos = {n: res["soft"][n]["*"][m] for n in POSITIVES}
        worst = max(pos.values()) if m == "G7.mad" else min(pos.values())
        cand = ceil6(MARGIN_POS * worst) if m == "G7.mad" else floor6(1.0 - MARGIN_POS * (1.0 - worst))
        negs = {n: res["soft"][n]["*"][m] for n in NEGATIVES if not res["hard1"][n]}
        out[m] = {"positives": pos, "worst_positive": worst, "candidate_threshold": cand,
                  "negatives_passing_every_hard_gate": negs,
                  "required_below": {n: (v / MARGIN_NEG if m == "G7.mad" else 1.0 - (1.0 - v) / MARGIN_NEG) for n, v in negs.items()},
                  "threshold": res["thresholds"]["default"].get(m)}
    return out


def p5_hinting_probe(spec):
    """Backs the mass_heavy comment in hud_compare: P5 at every non-Auto Slate hinting, scored on hard gates only."""
    out = {}
    for h in ("None", "Default", "AutoLight"):
        out[h] = {}
        for pair in "AB":
            img, _ = make_p5(pair, h, None)
            rep = score(load_rgb(REF / PAIR_REF[pair]), img, spec, Thr({}, pair), pair)
            out[h][pair] = {"heaviest_run": heaviest_run(rep), "hard_failures": len(hard_failures(rep))}
    return out


def vector_gaps(pair, thresholds, no_positive, report_only):
    """Regions with vector pixels where no soft vector metric has a threshold, with what still gates them (hard probes,
    border lines, flat pixels). G7 is report-only too, so these regions rest on hard gates and human review (T2 review)."""
    raw = json.load(open(REGIONS_JSON, encoding="utf-8"))
    vm = ("G4.vector.mad", "G4.vector.ssim", "G1.vector")
    gaps = []
    for reg in raw["regions"]:
        r, px = reg["name"], reg["class_px"]
        if px.get("vector", 0) == 0 or any(m in thresholds["regions"].get(r, {}) for m in vm):
            continue
        gaps.append({"region": r, "vector_px": px["vector"], "flat_px": px.get("flat", 0),
                     "hard_probes": sum(1 for q in raw["probes"] if q["region"] == r and q["class"] == "hard" and pair in q["pair"]),
                     "lines": sum(1 for q in raw["lines"] if q["region"] == r),
                     "report_only": [m for m in vm if f"{r}:{m}" in no_positive + report_only]})
    return gaps


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--hinting", default="None", choices=list(FT_LOAD))
    ap.add_argument("--no-write", action="store_true", help="run everything, write nothing")
    ap.add_argument("--diagnose", action="store_true", help="also print the threshold lists and per-negative verdicts; writes nothing")
    a = ap.parse_args(argv)
    write = not (a.no_write or a.diagnose)
    ctl = load_controls_spec()
    spec = load_spec(REGIONS_JSON)
    out_thr, results, stop, escalations = {}, {}, False, []
    for pair in "AB":
        res = calibrate_pair(pair, a.hinting, spec, ctl)
        results[pair] = res
        out_thr[pair] = res["thresholds"]
        print_table(pair, res)
        if res["pos_hard_fail"]:
            print(f"\nPAIR {pair}: STOP, a positive fails a hard gate (a mask is wrong; fix the mask, never the gate):")
            for n, f in res["pos_hard_fail"].items():
                print(f"  {n}: " + "; ".join(f"{r} {v['gates']} badness {fmt_bad(v['badness'])}" for r, v in f.items()))
            stop = True
        for n in NEGATIVES:
            if res["verdict"][n]["passes_every_gate"]:
                print(f"\nPAIR {pair}: STOP, negative {n} passes every gate. Per-metric values (worst over its intended regions): " +
                      json.dumps({r: res["soft"][n].get(r) for r in ctl[n]["intended_regions"] if r in res["soft"][n]}))
                stop = True
        escalations += [(pair, e) for e in res["escalate"]]
    if a.diagnose:
        for pair, res in results.items():
            print(f"\nDIAGNOSE pair {pair}: thresholds {sum(len(d) for d in res['thresholds']['regions'].values())} "
                  f"(+{len(res['thresholds']['default'])} G7), unvalidated (no eligible negative) {len(res['unvalidated'])}")
            print(f"  report-only (rule fails) {len(res['report_only'])}: {res['report_only']}")
            print(f"  report-only (every positive measured 0) {len(res['no_positive'])}: {res['no_positive']}")
            print(f"  escalate {[e['region'] for e in res['escalate']]}")
            for n in NEGATIVES:
                v = res["verdict"][n]
                print(f"  {n}: passes_every_gate={v['passes_every_gate']} failing intended={v['failing_intended']} outside={v['failing_outside']} "
                      f"changed-px ok={res['counts'][n]['ok']}")
    bad = stop or any(not res["verdict"][n]["ok"] for res in results.values() for n in POSITIVES + NEGATIVES)
    thr_json = json.dumps(out_thr, indent=1, sort_keys=True) + "\n"
    thr_sha = hashlib.sha256(thr_json.encode("utf-8")).hexdigest()
    print()
    for pair, res in results.items():
        pos_ok = sum(res["verdict"][n]["ok"] for n in POSITIVES)
        neg_ok = sum(res["verdict"][n]["ok"] for n in NEGATIVES)
        ro = res["report_only"] + [f"{k} (no positive)" for k in res["no_positive"]]
        print(f"PAIR {pair}: CALIBRATION {'OK' if pos_ok == len(POSITIVES) and neg_ok == len(NEGATIVES) and not stop else 'FAIL'} "
              f"positives {pos_ok}/{len(POSITIVES)} pass, negatives {neg_ok}/{len(NEGATIVES)} fail only in intended regions; "
              f"report-only: [{', '.join(ro)}]; thresholds sha256 {thr_sha}")
    print_escalations(escalations)
    for pair, res in results.items():
        for g in res["vector_gaps"]:
            print(f"VECTOR GAP: pair {pair} region {g['region']}: {g['vector_px']} vector px, no soft vector threshold "
                  f"(report-only {g['report_only']}); still gated by {g['hard_probes']} hard probes, {g['lines']} lines, "
                  f"{g['flat_px']} flat px")
    if bad:
        print("CALIBRATION " + ("STOPPED" if stop else "FAILED") + " (see above); nothing written")
        return 1
    if not write:
        print("(--no-write/--diagnose: nothing written)")
        return 0
    probe = p5_hinting_probe(spec)
    for h, d in probe.items():
        print(f"P5 hinting probe {h}: heaviest run " + ", ".join(f"{p} {v['heaviest_run']:+.4f} (hard failures {v['hard_failures']})" for p, v in d.items()))
    # ---- every verdict is OK: write P5 and P6 (if changed), thresholds, calibration.json, log header
    for pair, n, p, same in modelled_status(results):
        if not same:
            p.parent.mkdir(parents=True, exist_ok=True)
            Image.fromarray(results[pair]["imgs"][n]).save(p)
            assert np.array_equal(load_rgb(p), results[pair]["imgs"][n]), p
            print(f"wrote {p}")
    assert all(same for *_, same in modelled_status(results)), "a modelled control on disk differs from the calibrated one"
    THRESHOLDS_JSON.write_text(thr_json, encoding="utf-8", newline="\n")
    assert sha256_file(THRESHOLDS_JSON) == thr_sha
    cal = {"version": 2, "status": "OK", "date": time.strftime("%Y-%m-%d"), "hinting": a.hinting, **versions(),
           "thresholds_sha256": thr_sha, "regions_sha256": sha256_file(REGIONS_JSON),
           "refs_sha256": {p: sha256_file(REF / PAIR_REF[p]) for p in "AB"},
           "controls_sha256": {f"{p}/{n}": sha256_file(REF / "controls" / p / f"{n}.png") for p in "AB" for n in POSITIVES + NEGATIVES},
           "text_off_sha256": {p: sha256_file(REF / f"text_off_{p}.png") for p in "AB"},
           "hud_alpha_sha256": sha256_file(REF / "hud_alpha.png"),
           "controls_spec_sha256": sha256_file(T / "ref_controls.json"), "dropped_controls": sorted(n for n, c in ctl.items() if c.get("dropped")),
           "code_sha256": calibrated_code_hashes(T),
           "p5_hinting_probe": probe,
           "p6": {"inputs_sha256": {k: sha256_file(P6_RAW / v) for k, v in P6_INPUTS.items()},
                  "layout_check_pair_A": p6_layout_check(),
                  "model": "the board at deviceScaleFactor 4 (same Chromium, page, fonts, 3000 ms freeze), 4x4 box filter in sRGB bytes; "
                           "pair A from p6_A_4x, pair B from the black/white matte composited over the 1x world_layer.png; "
                           "vector-class pixels only, the base everywhere else (make_p6.py)"},
           "g7_derivation": {p: g7_derivation(results[p]) for p in results},
           "rulings": {
               "R1": "hard G5 heavy-ink gate: signed ink mass (test - ref) / ref <= +%s (hud_compare.HARD['mass_heavy']) is plan 4.4's "
                     "'new hard metric for that defect class' for N3: positives reach at most +0.028 (P3/P4 tab.3; P1 +0.017), P5's "
                     "heaviest run -0.0545 (None/Default; AutoLight -0.0758), N3 +0.115 (heaviest_run below)" % HARD["mass_heavy"],
               "R2": "unit_ssim is masked to its unit (capture pixels outside the unit take the reference's), so N5 no longer bleeds into mm.plate",
               "R3": "only eligible negatives bound a region's thresholds (rules.eligible_negatives); negatives fail only in their listed regions",
               "R4": "P6 vector AA model added; positives are P1, P3, P4, P5, P6; no_positive report-only is a last resort (pairs.*.report_only_no_positive)",
               "R5": "sel.panel's 87 text-class pixels at x=417, y 939-1041 (panel fill, no run) are flat: make_regions paints text only inside the run's own region",
               "R6": "G7.mad / G7.ssim are report-only when no negative that passes every hard gate moves the HUD summary by 1.2 x the "
                     "1.5 x worst-positive candidate (g7_derivation): a localised defect moves a 476k-px average less than the positives' "
                     "anti-aliasing does, so the summary cannot be validated; it is reported, and the region gates decide",
               "R7": "P5 stays raw coverage at hinting None; its lighter ink is T4a's to measure on the real Slate render; no light-side hard gate",
               "R8": "B/N6's photo-noise rebyte after the P4 re-render is accepted as disclosed (equals an earlier T0 render, ref_check 0 noise)"},
           "vector_gaps": {p: results[p]["vector_gaps"] for p in results},
           "rules": {"thr": "1.5 x worst positive, below best negative / 1.2 (plan B 4.4)",
                     "eligible_negatives": "intended in the region, passing every hard gate there in pass 1, and moving the metric (T2)",
                     "no_positive": "a metric every positive measures exactly 0 is report-only (T2)",
                     "hard_mass_heavy": HARD["mass_heavy"],
                     "n1": "N1 trips anchor card.Q_top by design; scored with score_misaligned, the anchor failure counts in every guarded region"},
           "pairs": {}}
    for pair, res in results.items():
        cal["pairs"][pair] = {"report_only": res["report_only"], "report_only_no_positive": res["no_positive"],
                              "unvalidated_by_a_negative": res["unvalidated"], "escalate": res["escalate"],
                              "verdict": res["verdict"], "changed": res["counts"],
                              "hard_failures_pass1": {n: res["hard1"][n] for n in NEGATIVES},
                              "heaviest_run": {n: heaviest_run(res["rep1"][n]) for n in POSITIVES + NEGATIVES},
                              "soft_values": {n: res["soft"][n] for n in POSITIVES + NEGATIVES}}
    CALIBRATION_JSON.parent.mkdir(parents=True, exist_ok=True)
    CALIBRATION_JSON.write_text(json.dumps(jsonable(cal), indent=1, sort_keys=True, allow_nan=False) + "\n", encoding="utf-8", newline="\n")
    if not LOG_CSV.is_file():
        with open(LOG_CSV, "w", newline="", encoding="utf-8") as f:
            csv.writer(f, lineterminator="\n").writerow(LOG_HEADER)
    print(f"wrote {THRESHOLDS_JSON.name} (sha256 {thr_sha}) and {CALIBRATION_JSON.relative_to(T)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
