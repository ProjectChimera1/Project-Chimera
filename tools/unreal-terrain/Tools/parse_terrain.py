#!/usr/bin/env python3
"""parse_terrain.py - log scan and gates for the terrain trial runs (plan C 3.8, 5). C3 part: --g1 and --scan.

Every run dir (T/Out/<tag>) holds game.log, results.json and the director's PNGs/JSON. Rules (plan C 3.8; EXECUTION 3 C4):
  * the log scan is the shared KIT rule set (tools/unreal-trial/logscan.py): RealtimeMesh errors/warnings, ensures, Fatal,
    `Failed to load` other than the 4 profiler DLLs, `UpdateTextureRegions called for`, LogChimera*: Error ... any match fails;
  * results.json must say completed=true with ops_done == ops_total;
  * a gate whose op or file is missing FAILS (never skipped).

Usage:
  parse_terrain.py --g1 RUN_DIR       gate G1 (plan C 5): prints one BAR line per bar and `G1 PASS` or `G1 FAIL`; writes RUN_DIR/g1.json
  parse_terrain.py --scan RUN_DIR...  log scan + completion check only
Exit 0 = pass, 1 = fail, 2 = usage/io.
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.environ.get("CHIMERA_KIT", "D:/Projects/Project_Chimera/tools/unreal-trial")
sys.path.insert(0, HERE)
sys.path.insert(0, KIT)

import numpy as np  # noqa: E402

import imgdiff  # noqa: E402
import logscan  # noqa: E402

# Plan C 5, G1.
G1_TERRAIN_CHANGED_MIN = 0.40
G1_LUMA_BAND = (0.05, 0.95)
G1_LUMA_STD_MIN = 0.01
G1_SUN_OVER_SHADOW_MIN = 1.3
G1_CAST_DARKER_MIN = 0.20      # cast shadow >= 20 % darker than lit ground
G1_SPIKE_CHANGED_MIN = 0.50
MIN_MASK_PX = 50               # fewer analytic pixels than this = no samples = FAIL


class Gates:
    def __init__(self):
        self.rows = []

    def bar(self, name, ok, value, rule):
        self.rows.append({"bar": name, "pass": bool(ok), "value": value, "rule": rule})
        print("BAR %-26s %-5s value=%s rule=%s" % (name, "PASS" if ok else "FAIL", _fmt(value), rule))

    @property
    def ok(self):
        return all(r["pass"] for r in self.rows) and bool(self.rows)


def _fmt(v):
    if isinstance(v, float):
        return "%.4f" % v
    return str(v)


def load_results(run):
    p = os.path.join(run, "results.json")
    if not os.path.isfile(p):
        return None
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def scan_run(run, g):
    """Log scan + completion; adds bars to g."""
    log = os.path.join(run, "game.log")
    if not os.path.isfile(log):
        g.bar("log_present", False, "missing", "game.log exists")
    else:
        with open(log, "r", encoding="utf-8", errors="replace") as f:
            found = logscan.scan_lines(f.readlines(), profile="game")
        for n, rule, text in found[:20]:
            print("  log:%d %s: %s" % (n, rule, text))
        g.bar("log_scan", not found, "%d finding(s)" % len(found), "KIT logscan game profile clean")
    res = load_results(run)
    if res is None:
        g.bar("results", False, "missing", "results.json exists")
        return None
    done, total = res.get("ops_done"), res.get("ops_total")
    g.bar("completed", bool(res.get("completed")) and done == total and total,
          "completed=%s ops=%s/%s" % (res.get("completed"), done, total), "completed=true and ops_done == ops_total")
    return res


def g1(run):
    g = Gates()
    res = scan_run(run, g)
    fp_path = os.path.join(run, "footprints.json")
    shots = {k: os.path.join(run, k + ".png") for k in ("visible", "hidden", "spike_visible", "spike_hidden")}
    missing = [k for k, p in shots.items() if not os.path.isfile(p)]
    if missing or not os.path.isfile(fp_path):
        g.bar("artefacts", False, "missing %s" % (missing + ([] if os.path.isfile(fp_path) else ["footprints.json"])), "4 shots + footprints.json")
        print("G1 FAIL")
        return g, res
    with open(fp_path, encoding="utf-8") as f:
        fp = json.load(f)
    vis = imgdiff.load_luma(shots["visible"])
    hid = imgdiff.load_luma(shots["hidden"])

    # Terrain drawn at all: changed fraction visible vs hidden over the whole frame.
    frac = imgdiff.changed_frac(vis, hid)
    g.bar("terrain_changed_frac", frac >= G1_TERRAIN_CHANGED_MIN, frac, ">= %.2f vs hidden" % G1_TERRAIN_CHANGED_MIN)

    # Not black, not flat, not depth-only: luma band and spread inside the terrain mask.
    tm = imgdiff.terrain_mask(vis, hid)
    mu = float(vis[tm].mean()) if tm.any() else 0.0
    sd = float(vis[tm].std()) if tm.any() else 0.0
    g.bar("terrain_luma_mean", G1_LUMA_BAND[0] <= mu <= G1_LUMA_BAND[1], mu, "in [%.2f, %.2f]" % G1_LUMA_BAND)
    g.bar("terrain_luma_std", sd > G1_LUMA_STD_MIN, sd, "> %.2f" % G1_LUMA_STD_MIN)

    # Lighting and VSM on edited shapes.
    shape = vis.shape
    m_sun = imgdiff.footprint_mask(fp, "rts80/hill_sun", shape)
    m_sh = imgdiff.footprint_mask(fp, "rts80/hill_shadow", shape)
    m_cast = imgdiff.footprint_mask(fp, "rts80/cast_shadow", shape)
    m_lit = imgdiff.footprint_mask(fp, "rts80/lit_ground", shape)
    counts = {"hill_sun": int(m_sun.sum()), "hill_shadow": int(m_sh.sum()), "cast_shadow": int(m_cast.sum()), "lit_ground": int(m_lit.sum())}
    print("  mask px: %s" % counts)
    if min(counts["hill_sun"], counts["hill_shadow"]) < MIN_MASK_PX:
        g.bar("hill_sun_over_shadow", False, "no samples %s" % counts, ">= %.1f" % G1_SUN_OVER_SHADOW_MIN)
    else:
        sun, shd = float(vis[m_sun].mean()), float(vis[m_sh].mean())
        ratio = sun / shd if shd > 0 else float("inf")
        print("  hill sun side luma=%.4f shadow side luma=%.4f" % (sun, shd))
        g.bar("hill_sun_over_shadow", ratio >= G1_SUN_OVER_SHADOW_MIN, ratio, ">= %.1f" % G1_SUN_OVER_SHADOW_MIN)
    if min(counts["cast_shadow"], counts["lit_ground"]) < MIN_MASK_PX:
        g.bar("cast_shadow_darker", False, "no samples %s" % counts, ">= %.0f %% darker than lit ground" % (G1_CAST_DARKER_MIN * 100))
    else:
        cast, lit = float(vis[m_cast].mean()), float(vis[m_lit].mean())
        darker = 1.0 - cast / lit if lit > 0 else 0.0
        print("  cast shadow luma=%.4f lit ground luma=%.4f" % (cast, lit))
        g.bar("cast_shadow_darker", darker >= G1_CAST_DARKER_MIN, darker, ">= %.2f" % G1_CAST_DARKER_MIN)

    # Render bounds follow edits: the spike's projected pixels change between visible and hidden at the spike pose.
    sv = imgdiff.load_luma(shots["spike_visible"])
    shd_ = imgdiff.load_luma(shots["spike_hidden"])
    m_sp = imgdiff.footprint_mask(fp, "spike/spike", sv.shape)
    if int(m_sp.sum()) < MIN_MASK_PX:
        g.bar("spike_changed_frac", False, "no samples (%d px)" % int(m_sp.sum()), ">= %.2f" % G1_SPIKE_CHANGED_MIN)
    else:
        sfrac = imgdiff.changed_frac(sv, shd_, m_sp)
        print("  spike mask px=%d" % int(m_sp.sum()))
        g.bar("spike_changed_frac", sfrac >= G1_SPIKE_CHANGED_MIN, sfrac, ">= %.2f of the spike's projected pixels" % G1_SPIKE_CHANGED_MIN)

    if res:
        r = res.get("render", {})
        print("  reported: draw_type=%s chunks=%s triangles=%s ranged_edits=%s proxy_recreates=%s during_strokes=%s bounds_widenings=%s settles=%s" % (
            res.get("options", {}).get("draw_type"), r.get("chunks"), r.get("triangles"), r.get("ranged_edits"), r.get("proxy_recreates"),
            r.get("proxy_recreates_during_strokes"), r.get("bounds_widenings"),
            [round(s.get("settle_ms", 0)) for s in res.get("settles", [])]))
    print("G1 %s" % ("PASS" if g.ok else "FAIL"))
    return g, res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--g1", metavar="RUN_DIR")
    ap.add_argument("--scan", nargs="+", metavar="RUN_DIR")
    a = ap.parse_args(argv)
    if a.g1:
        if not os.path.isdir(a.g1):
            print("no such run dir: %s" % a.g1)
            return 2
        g, res = g1(a.g1)
        with open(os.path.join(a.g1, "g1.json"), "w", encoding="utf-8") as f:
            json.dump({"gate": "G1", "pass": g.ok, "bars": g.rows}, f, indent=1)
        return 0 if g.ok else 1
    if a.scan:
        bad = 0
        for run in a.scan:
            g = Gates()
            scan_run(run, g)
            print("SCAN %s %s" % (run, "OK" if g.ok else "FAIL"))
            bad += 0 if g.ok else 1
        return 1 if bad else 0
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
