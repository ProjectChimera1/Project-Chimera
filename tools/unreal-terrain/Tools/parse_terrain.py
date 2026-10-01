#!/usr/bin/env python3
"""parse_terrain.py - log scan, gates and tables for the terrain trial runs (plan C 3.8, 5). C3 + C4 + C5.

Every run dir (T/Out/<tag>) holds game.log, results.json, ticks.csv, cmdline.txt and the director's PNGs/JSON. Rules (plan C 3.8;
EXECUTION 3 C4):
  * the log scan is the shared KIT rule set (tools/unreal-trial/logscan.py): RealtimeMesh errors/warnings, ensures, Fatal,
    `Failed to load` other than the 4 profiler DLLs, `UpdateTextureRegions called for`, LogChimera*: Error ... any match fails;
  * results.json must say completed=true with ops_done == ops_total;
  * a gate whose op or file is missing FAILS (never skipped); an op the director lists in results.json skipped[] fails every gate
    that needs it.

Usage:
  parse_terrain.py --g1 RUN_DIR                  gate G1 (plan C 5): one BAR line per bar, `G1 PASS` or `G1 FAIL`; writes RUN_DIR/g1.json
  parse_terrain.py --scan RUN_DIR...             log scan + completion check only
  parse_terrain.py --s1 RUN_DIR                  S1 bars: hashes (undo = pre_last2, redo = after), P7 images (+ the gated A/A floor and
                                                 positive control), P11 depth (full frame + last-two-strokes footprint), probes, splat
                                                 counters, P2 in-place use, P5 collision at pre_last2/after/undo/redo (wait: every body
                                                 has a trimesh and is the one in use, probe rays hit; verify: 0 disagreements either way,
                                                 max |dz| <= 1 cm over frustum and vertex rays), cook validity; cook p95 and GT apply p95
                                                 are REPORTED (gated timings come from the measured campaign, EXECUTION 3 C9).
  parse_terrain.py --s1l RUN_DIR --ref S1_RUN    S1L: hashes equal the reference run's final, `redo` image vs the reference `redo` inside
                                                 the reference's stroke footprints and over its terrain mask, with the reference A/A floor
  parse_terrain.py --same-hash A_DIR B_DIR       P8: equal height/splat FNV at every shared hash, ticks_applied == ticks, B had the hitch
  parse_terrain.py --simgrid RUN_DIR             independent re-computation of sim_grid_fnv and the 16 probes from height.r32 (python
                                                 mirror of ScenarioLoadPhase.cs:252-270 / ElevationGrid.Sample; C10 does the C# check)
  parse_terrain.py --summary RUN_DIR... [--json OUT.json] [--gate-config KEY]
                                                 tables (by diameter, latency, fps, GPU/GT/RT per phase, memory, hashes), the plan C 5
                                                 metric rows with PASS/FAIL, rep-spread flags, then `GATES: ...`. Runs are keyed by
                                                 configuration (half, chunk, draw type, non-default cvars and -ChimeraTerrain* options;
                                                 units are a workload, not a configuration). P1-P4 gate only the gate configuration
                                                 (default: half=160 chunk=64 draw=Dynamic, no extras; --gate-config picks another key as
                                                 printed in the runs table); other configurations are tabled per configuration, and reps
                                                 are grouped within one configuration and script.
Exit 0 = pass, 1 = fail, 2 = usage/io.
"""
import argparse
import contextlib
import csv
import glob
import io
import json
import os
import re
import struct
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

# Plan C 5 bars.
P1_P50_MS = {20: 1.0, 100: 2.0}
P1_P99_MS = 4.0
P2_MEDIAN_FRAMES = 2.0
P2_IN_PLACE_FRACTION = 0.95    # proxy_recreates_during_strokes <= 5 % of ranged edits
P3_MEDIAN_FPS = 60.0
P3_LOW1_FPS = 45.0
P3_OVER33_FRAC = 0.005
P3_MAX_FRAME_MS = 100.0
P3_SCULPT_MINUS_IDLE_MS = 2.0
P4_TERRAIN_GPU_MS = 3.0
P5_COOK_P95_MS = 50.0
P5_GT_APPLY_P95_MS = 4.0
P5_MAX_DZ_CM = 1.0
P5_CHECKPOINTS = ("pre_last2", "after", "undo", "redo")
P5_MIN_FRUSTUM_BOTH_HIT = 1500   # of the 2,000 rts80 frustum rays ~90 % meet the terrain (C4 depthcheck frame_gpu_hit_fraction 0.903)
P5_MIN_VERTEX_RAYS = 100         # every checkpoint follows a collision rewrite, so its dirty rects hold vertices
P6_PEAK_GROWTH_MB = 300.0
P6_FINAL_GROWTH_MB = 100.0
P11_P99_CM = 2.0
P11_MIN_PAIRS = 1000
P7_LOCAL_FRAC = imgdiff.LOCAL_FRAC
P7_LOCAL_BLOCK = imgdiff.LOCAL_BLOCK
P7_CONTROL_MIN = 0.10          # after vs pre_last2 must change >= 10 % of the footprint: the shots really show the strokes
P11_FP_MIN_PAIRS = 500         # of the 2,000 footprint draws, both GPU and analytic must hit at least this many
DEPTH_FP_CHECKS = ("after_rts80", "after_oblique", "undo_rts80", "undo_oblique")   # S1 depthchecks that carry fp=last2
DEFAULT_CONFIG = "half=160 chunk=64 draw=Dynamic"
STANDARD_EXEC = {"t.MaxFPS 0", "r.VSync 0", "r.ScreenPercentage 100", "r.HighResScreenshotDelay 64"}
NON_CONFIG_OPTS = {"Script", "Out", "Load", "Units"}   # where to read/write and the unit workload; not a terrain configuration
# -ChimeraTerrain<K>=<v> spelled at its default value is the default configuration (ChimeraTerrainGameMode.cpp: FastCook defaults to 1,
# CollisionDuringStroke to the cvar's 0 in TerrainActor.cpp), so "run at CollisionDuringStroke=0" stays in the gate configuration.
DEFAULT_OPTS = {"FastCook": "1", "CollisionDuringStroke": "0"}
DEPTH_POSES = ("rts80", "oblique")
COLLISION_OPS = ("wait_collision", "verify_collision")
S1_SHOTS = ("before", "before_aa", "bg", "sculpt", "paint", "pre_last2", "after", "undo", "redo", "closeup", "oblique")
PROBE_MIN_NEG_NONEXACT = 4
PROBE_MIN_BOUNDARY = 2


class Gates:
    def __init__(self):
        self.rows = []

    def bar(self, name, ok, value, rule, informational=False):
        """ok True/False; informational rows print REPORT and never fail the run."""
        status = "REPORT" if informational else ("PASS" if ok else "FAIL")
        self.rows.append({"bar": name, "pass": bool(ok) or informational, "status": status, "value": value, "rule": rule})
        print("BAR %-30s %-6s value=%s rule=%s" % (name, status, _fmt(value), rule))

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


# ---------------------------------------------------------------------------------------------------------------- G1
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


# ---------------------------------------------------------------------------------------------------------------- helpers
def read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def png(run, name):
    return os.path.join(run, name + ".png")


def pctl(values, p):
    """Nearest-rank percentile (the director's FTerrainSeries::Percentile)."""
    if len(values) == 0:
        return 0.0
    s = sorted(values)
    k = min(len(s), max(1, int(np.ceil(p / 100.0 * len(s)))))
    return float(s[k - 1])


def hash_of(res, name):
    h = (res.get("hashes") or {}).get(name)
    return None if not h else (h.get("height_fnv"), h.get("splat_fnv"))


def skipped_collision(res):
    return [s for s in (res.get("skipped") or []) if s.get("op") in COLLISION_OPS]


def read_ticks(run):
    """Rows of ticks.csv as dicts with numeric fields."""
    p = os.path.join(run, "ticks.csv")
    if not os.path.isfile(p):
        return None
    rows = []
    with open(p, encoding="utf-8", newline="") as f:
        for r in csv.DictReader(f):
            for k in ("d", "s", "apply_ms", "normals_ms", "upload_ms", "splat_ms", "total_ms"):
                r[k] = float(r[k])
            rows.append(r)
    return rows


def depth_rows(res):
    return {d.get("name"): d for d in (res.get("depthchecks") or [])}


def bar_depth(g, res, names, label, fp_names=()):
    """P11 bars for the named depthchecks. A missing check FAILS. Names in fp_names must also carry a passing footprint subset."""
    dc = depth_rows(res)
    for nm in names:
        d = dc.get(nm)
        if d is None:
            g.bar("P11 %s" % nm, False, "no samples (depthcheck op missing)", "p99 <= %.1f cm, 0 hit/miss disagreements outside a 2 px band" % P11_P99_CM)
            continue
        pairs = d.get("both_hit", 0)
        p99 = d.get("abs_delta_cm", {}).get("p99", 1e9)
        out = d.get("hit_miss_disagree_outside_band", 1e9)
        ok = pairs >= P11_MIN_PAIRS and p99 <= P11_P99_CM and out == 0
        g.bar("P11 %s" % nm, ok, "pairs=%d p50=%.3f p99=%.3f max=%.3f cm; disagree in/out band %s/%s; frame hit %.2f" % (
            pairs, d.get("abs_delta_cm", {}).get("p50", 0), p99, d.get("abs_delta_cm", {}).get("max", 0),
            d.get("hit_miss_disagree_in_band"), out, d.get("frame_gpu_hit_fraction", 0)),
            "pairs >= %d, p99 <= %.1f cm, outside-band disagreements 0 (%s)" % (P11_MIN_PAIRS, P11_P99_CM, label))
        if nm in fp_names:
            f = d.get("footprint")
            rule = "same bar on the fp draws from the last strokes' projected footprint: pairs >= %d, p99 <= %.1f cm, outside-band 0" % (P11_FP_MIN_PAIRS, P11_P99_CM)
            if not f:
                g.bar("P11 %s footprint" % nm, False, "no samples (depthcheck without fp)", rule)
                continue
            fpairs = f.get("both_hit", 0)
            fp99 = f.get("abs_delta_cm", {}).get("p99", 1e9)
            fout = f.get("hit_miss_disagree_outside_band", 1e9)
            fok = fpairs >= P11_FP_MIN_PAIRS and fp99 <= P11_P99_CM and fout == 0
            g.bar("P11 %s footprint" % nm, fok, "set=%s pixels=%s draws=%s pairs=%d p50=%.3f p99=%.3f max=%.3f cm; disagree in/out band %s/%s" % (
                f.get("set"), f.get("pixels"), f.get("n"), fpairs, f.get("abs_delta_cm", {}).get("p50", 0), fp99, f.get("abs_delta_cm", {}).get("max", 0),
                f.get("hit_miss_disagree_in_band"), fout), rule)


def footprint_union(fp, keys, shape):
    m = np.zeros(shape, dtype=bool)
    for k in keys:
        m |= imgdiff.footprint_mask(fp, k, shape)
    return m


def local_bar(g, name, a_path, b_path, mask, mask_desc):
    if not (os.path.isfile(a_path) and os.path.isfile(b_path)):
        g.bar(name, False, "missing image(s) %s %s" % (os.path.basename(a_path), os.path.basename(b_path)), "local statistic")
        return None
    A, B = imgdiff.load_luma(a_path), imgdiff.load_luma(b_path)
    if A.shape != B.shape:
        g.bar(name, False, "size mismatch", "local statistic")
        return None
    if int(mask.sum()) < MIN_MASK_PX:
        g.bar(name, False, "no samples (mask %d px)" % int(mask.sum()), "local statistic")
        return None
    st = imgdiff.local_stat(A, B, mask)
    g.bar(name, st["pass"], "changed_frac=%.5f worst16=%.5f mean_abs=%.5f mask_px=%d (%s)" % (
        st["changed_frac"], st["worst_block_mean_abs"], st["mean_abs"], int(mask.sum()), mask_desc),
        "changed <= %.1f %% and worst 16x16 block mean |d| <= %.0f/255" % (P7_LOCAL_FRAC * 100, P7_LOCAL_BLOCK * 255))
    return st


# ---------------------------------------------------------------------------------------------------------------- S1
def check_probes(g, run):
    p = os.path.join(run, "terrain.json")
    if not os.path.isfile(p):
        g.bar("probes", False, "terrain.json missing (save op)", "16 probes")
        return
    t = read_json(p)
    probes = t.get("probes") or []
    neg = [q for q in probes if q["value_raw"] < 0 and q["value_raw"] % 65536 != 0]
    bnd = [q for q in probes if (q["sim_x_raw"] & 0xFFFF) in (0, 0xFFFF) or (q["sim_z_raw"] & 0xFFFF) in (0, 0xFFFF)]
    ok = len(probes) == 16 and len(neg) >= PROBE_MIN_NEG_NONEXACT and len(bnd) >= PROBE_MIN_BOUNDARY and bool(t.get("sim_grid_fnv"))
    g.bar("probes", ok, "n=%d negative_non_exact=%d cell_boundary=%d sim_grid_fnv=%s axes=%s" % (
        len(probes), len(neg), len(bnd), t.get("sim_grid_fnv"), t.get("axes")),
        "16 probes, >= %d negative non-exact heights, >= %d at cell boundaries" % (PROBE_MIN_NEG_NONEXACT, PROBE_MIN_BOUNDARY))


def collision_bars(g, res, checkpoints, label):
    """P5 correctness bars (plan C 5): one per checkpoint, each needing its wait_collision AND verify_collision record."""
    col = res.get("collision") or {}
    waits = {w.get("name"): w for w in (col.get("waits") or [])}
    vers = {v.get("name"): v for v in (col.get("verifies") or [])}
    rule = ("wait: ok, every chunk's body has a trimesh, is in use and its probe ray hits; verify: 0 disagreements either way, "
            "max |dz| <= %.1f cm, frustum both-hit >= %d, vertex rays >= %d (%s)" % (P5_MAX_DZ_CM, P5_MIN_FRUSTUM_BOTH_HIT, P5_MIN_VERTEX_RAYS, label))
    for cp in checkpoints:
        w, v = waits.get(cp), vers.get(cp)
        if not w or not v:
            g.bar("P5 collision %s" % cp, False, "no samples (wait %s, verify %s)" % ("ok" if w else "missing", "ok" if v else "missing"), rule)
            continue
        fr, vx = v.get("frustum") or {}, v.get("vertex") or {}
        wait_ok = bool(w.get("ok")) and w.get("chunks", 0) > 0 and w.get("min_trimeshes", 0) > 0 and w.get("probes_hit") == w.get("chunks")
        ok = (wait_ok and v.get("disagreements", 1) == 0 and v.get("max_dz_cm", 1e9) <= P5_MAX_DZ_CM
              and fr.get("both_hit", 0) >= P5_MIN_FRUSTUM_BOTH_HIT and vx.get("n", 0) >= P5_MIN_VERTEX_RAYS)
        g.bar("P5 collision %s" % cp, ok,
              "wait %s %.0f ms touched=%s min_tri=%s probes %s/%s; frustum n=%s hit=%s phys_only=%s pick_only=%s foreign=%s max_dz=%.4f; "
              "vertex n=%s (rects %s) hit=%s phys_only=%s pick_only=%s foreign=%s max_dz=%.4f cm" % (
                  "ok" if w.get("ok") else "FAIL", w.get("ms", 0), w.get("touched"), w.get("min_trimeshes"), w.get("probes_hit"), w.get("chunks"),
                  fr.get("n"), fr.get("both_hit"), fr.get("physics_only_hit"), fr.get("pick_only_hit"), fr.get("foreign_hit"), fr.get("max_dz_cm", 0),
                  vx.get("n"), vx.get("rects"), vx.get("both_hit"), vx.get("physics_only_hit"), vx.get("pick_only_hit"), vx.get("foreign_hit"),
                  vx.get("max_dz_cm", 0)), rule)


def cook_bars(g, res):
    """Collision cook validity (gated) and the P5 timings (reported here; EXECUTION 3 C9)."""
    col = res.get("collision") or {}
    if not col.get("cooks"):
        g.bar("P5_cooks_valid", False, "no samples (results.json has no collision cooks)", "every collision update resolves Updated with a trimesh")
        return
    ok = col.get("errors", 1) == 0 and col.get("unknown", 1) == 0 and col.get("updated_without_trimesh", 1) == 0
    g.bar("P5_cooks_valid", ok, "cooks=%s updated=%s ignored=%s errors=%s unknown=%s updated_without_trimesh=%s batches=%s incomplete=%s" % (
        col.get("cooks"), col.get("updated"), col.get("ignored"), col.get("errors"), col.get("unknown"), col.get("updated_without_trimesh"),
        col.get("batches"), col.get("batches_incomplete")), "0 errors, 0 unknown, 0 Updated bodies without a trimesh")
    ck, gt = col.get("cook_ms") or {}, col.get("gt_apply_ms") or {}
    fast = (col.get("options") or {}).get("fast_cook")
    g.bar("P5_cook_p95", ck.get("n", 0) > 0 and ck.get("p95", 1e9) <= P5_COOK_P95_MS,
          "n=%s p50=%.2f p95=%.2f max=%.2f ms (edit cooks; fast_cook=%s; init cooks p95 %.2f ms)" % (
              ck.get("n"), ck.get("p50", 0), ck.get("p95", 0), ck.get("max", 0), fast, (col.get("init_cook_ms") or {}).get("p95", 0)),
          "p95 <= %.0f ms (reported; gated from the measured campaign)" % P5_COOK_P95_MS, informational=True)
    g.bar("P5_gt_apply_p95", gt.get("n", 0) > 0 and gt.get("p95", 1e9) <= P5_GT_APPLY_P95_MS,
          "n=%s p50=%.3f p95=%.3f max=%.3f ms (submit p95 %.3f, physics state p95 %.3f)" % (
              gt.get("n"), gt.get("p50", 0), gt.get("p95", 0), gt.get("max", 0), (col.get("submit_ms") or {}).get("p95", 0),
              (col.get("physics_state_ms") or {}).get("p95", 0)),
          "per stroke end / undo / redo p95 <= %.0f ms (reported; gated from the measured campaign)" % P5_GT_APPLY_P95_MS, informational=True)


def s1(run):
    g = Gates()
    res = scan_run(run, g)
    if res is None:
        print("S1 FAIL")
        return g, res, False

    # hashes
    names = ("pre_last2", "after", "undo", "redo")
    hs = {n: hash_of(res, n) for n in names}
    missing = [n for n, h in hs.items() if h is None]
    if missing:
        g.bar("hashes_present", False, "missing %s" % missing, "hash ops pre_last2, after, undo, redo")
    else:
        g.bar("hash_undo_eq_pre_last2", hs["undo"] == hs["pre_last2"], "%s vs %s" % (hs["undo"], hs["pre_last2"]), "undo = pre_last2 (height and splat FNV)")
        g.bar("hash_redo_eq_after", hs["redo"] == hs["after"], "%s vs %s" % (hs["redo"], hs["after"]), "redo = after (height and splat FNV)")
        g.bar("hash_after_ne_pre_last2", hs["after"] != hs["pre_last2"], "%s vs %s" % (hs["after"], hs["pre_last2"]),
              "the last two strokes changed height and splat", )
        saved = res.get("saved") or {}
        g.bar("saved_eq_after", (saved.get("height_fnv"), saved.get("splat_fnv")) == hs["after"], "saved %s/%s" % (saved.get("height_fnv"), saved.get("splat_fnv")),
              "save wrote the redo state")
    # ticks
    bad = [s for s in (res.get("strokes") or []) if s.get("ticks_applied") != s.get("ticks")]
    g.bar("ticks_applied_eq_ticks", not bad and bool(res.get("strokes")), "%d strokes, %d mismatched" % (len(res.get("strokes") or []), len(bad)), "ticks_applied == ticks for every stroke")
    # shots
    absent = [n for n in S1_SHOTS if not os.path.isfile(png(run, n))]
    g.bar("shots_present", not absent, "missing %s" % absent if absent else "%d shots" % len(S1_SHOTS), "11 S1 shots")
    # splat counters (plan C 3.5)
    sp = res.get("splat") or {}
    g.bar("splat_submits_eq_cleanups", sp.get("submits", 0) > 0 and sp.get("submits") == sp.get("cleanups"), "submits=%s cleanups=%s" % (sp.get("submits"), sp.get("cleanups")), "equal and > 0")
    # P2 in-place use
    r = res.get("render") or {}
    edits = r.get("ranged_edits", 0)
    during = r.get("proxy_recreates_during_strokes", 0)
    g.bar("P2_in_place", edits > 0 and during <= (1 - P2_IN_PLACE_FRACTION) * edits, "ranged_edits=%s proxy_recreates_during_strokes=%s" % (edits, during),
          "<= 5 %% of ranged edits (reported here; gated from the measured campaign)", informational=True)
    lat = (r.get("edit_latency") or {})
    print("  edit latency: n=%s frames p50=%s p99=%s max=%s hist(0..4+)=%s ms p50=%.2f" % (
        lat.get("n"), lat.get("frames", {}).get("p50"), lat.get("frames", {}).get("p99"), lat.get("frames", {}).get("max"),
        lat.get("frames_hist_0_to_4plus"), lat.get("ms", {}).get("p50", 0)))

    # P7 images
    fpj = os.path.join(run, "footprints.json")
    if not os.path.isfile(fpj):
        g.bar("P7_footprints", False, "footprints.json missing", "project_footprint ops")
    else:
        fp = read_json(fpj)
        try:
            shape = imgdiff.load_luma(png(run, "after")).shape
            mask = footprint_union(fp, ["rts80/last2_after", "rts80/last2_flat"], shape)
        except (KeyError, OSError) as e:
            g.bar("P7_footprints", False, "no footprint masks (%s)" % e, "rts80/last2_after and last2_flat")
            mask = None
        if mask is not None:
            desc = "footprint of the last two strokes, after + flat states"
            # A/A noise floor over the same mask (plan C 3.8: the floor every image bar must clear): the two identical-state shots 1 s
            # apart must themselves pass the local statistic, or the image bars below mean nothing.
            aa = local_bar(g, "P7_aa_floor", png(run, "before"), png(run, "before_aa"), mask, desc + "; before vs before_aa")
            local_bar(g, "P7_undo_vs_pre_last2", png(run, "undo"), png(run, "pre_last2"), mask, desc)
            local_bar(g, "P7_redo_vs_after", png(run, "redo"), png(run, "after"), mask, desc)
            # Positive control: the last two strokes are visible in these shots, so a stale or blank shot cannot pass the bars above.
            if os.path.isfile(png(run, "after")) and os.path.isfile(png(run, "pre_last2")) and int(mask.sum()) >= MIN_MASK_PX:
                A, B = imgdiff.load_luma(png(run, "after")), imgdiff.load_luma(png(run, "pre_last2"))
                ctl = imgdiff.changed_frac(A, B, mask)
                floor = aa["changed_frac"] if aa else 1.0
                g.bar("P7_control", ctl >= P7_CONTROL_MIN and ctl >= 10.0 * floor, "after vs pre_last2 changed_frac=%.4f (A/A %.5f)" % (ctl, floor),
                      ">= %.2f of the footprint and >= 10x the A/A changed_frac" % P7_CONTROL_MIN)
            else:
                g.bar("P7_control", False, "no samples (after/pre_last2 missing or empty mask)", ">= %.2f of the footprint" % P7_CONTROL_MIN)

    # P11 depth
    names = ["%s_%s" % (cp, pose) for cp in ("after", "undo") for pose in DEPTH_POSES]
    bar_depth(g, res, names, "after and undo at rts80 and oblique", fp_names=DEPTH_FP_CHECKS)
    bar_depth(g, res, ["pre_last2_%s" % pose for pose in DEPTH_POSES], "pre_last2 (reported with the same bar)")

    # probes
    check_probes(g, run)

    # P5: collision (C5). A skipped collision op fails P5 outright; otherwise the four checkpoints and the cook validity gate it.
    sk = skipped_collision(res)
    if sk:
        g.bar("P5_collision_skipped", False, "skipped x%d (%s)" % (len(sk), ", ".join(sorted({s["op"] for s in sk}))), "no collision op skipped")
    collision_bars(g, res, P5_CHECKPOINTS, "S1 checkpoints")
    cook_bars(g, res)
    ok = g.ok
    print("S1 %s" % ("PASS" if ok else "FAIL"))
    return g, res, ok


def s1l(run, ref):
    g = Gates()
    res = scan_run(run, g)
    rres = load_results(ref)
    if res is None or rres is None:
        g.bar("reference", False, "results.json missing in %s or %s" % (run, ref), "both runs")
        print("S1L FAIL")
        return g
    loaded = res.get("loaded") or {}
    h = hash_of(res, "loaded")
    want = hash_of(rres, "redo")
    saved = rres.get("saved") or {}
    g.bar("loaded_dir", os.path.normcase(os.path.abspath(loaded.get("dir", ""))) == os.path.normcase(os.path.abspath(ref)), loaded.get("dir"), "load read the reference run's folder")
    g.bar("hash_eq_reference_redo", h is not None and h == want, "%s vs %s" % (h, want), "loaded hashes = reference redo hashes")
    g.bar("hash_eq_reference_saved", h is not None and h == (saved.get("height_fnv"), saved.get("splat_fnv")), "%s vs %s/%s" % (h, saved.get("height_fnv"), saved.get("splat_fnv")),
          "loaded hashes = reference saved hashes")
    try:
        bg = imgdiff.load_luma(png(ref, "bg"))
        ref_redo = imgdiff.load_luma(png(ref, "redo"))
    except OSError as e:
        g.bar("image_redo_vs_reference", False, "reference images missing: %s" % e, "local statistic")
        ref_redo = None
    if ref_redo is not None:
        # Plan C 3.8's local statistic is defined inside the footprint: every stroke of the reference run, projected at rts80 in its
        # `after` state (footprints.json rts80/all_after). The whole-terrain mask stays as a second bar.
        fpj = os.path.join(ref, "footprints.json")
        fmask = None
        try:
            fmask = imgdiff.footprint_mask(read_json(fpj), "rts80/all_after", ref_redo.shape)
        except (KeyError, OSError, ValueError) as e:
            g.bar("image_redo_vs_reference_footprint", False, "no footprint mask rts80/all_after in the reference (%s)" % e, "local statistic")
        if fmask is not None:
            desc = "every stroke footprint of the reference (rts80/all_after)"
            local_bar(g, "aa_floor_reference", png(ref, "before"), png(ref, "before_aa"), fmask, desc + "; reference before vs before_aa")
            local_bar(g, "image_redo_vs_reference_footprint", png(run, "redo"), png(ref, "redo"), fmask, desc)
        tmask = imgdiff.terrain_mask(ref_redo, bg)
        local_bar(g, "image_redo_vs_reference", png(run, "redo"), png(ref, "redo"), tmask, "terrain mask of the reference redo vs bg")
    bar_depth(g, res, ["loaded_rts80"], "GPU mesh equals the loaded heightfield")
    print("S1L %s" % ("PASS" if g.ok else "FAIL"))
    return g


def same_hash(a, b):
    g = Gates()
    ra, rb = scan_run(a, g), scan_run(b, g)
    if ra is None or rb is None:
        print("SAME-HASH FAIL")
        return g
    names = sorted(set((ra.get("hashes") or {}).keys()) & set((rb.get("hashes") or {}).keys()))
    g.bar("shared_hashes", len(names) >= 4, names, ">= 4 shared hash ops")
    hneq = [n for n in names if hash_of(ra, n)[0] != hash_of(rb, n)[0]]
    sneq = [n for n in names if hash_of(ra, n)[1] != hash_of(rb, n)[1]]
    g.bar("height_fnv_equal", not hneq and bool(names), "differ at %s" % hneq if hneq else "equal at %d hashes + final" % len(names), "equal at every shared hash")
    g.bar("splat_fnv_equal", not sneq and bool(names), "differ at %s" % sneq if sneq else "equal at %d hashes + final" % len(names), "equal at every shared hash")
    g.bar("final_equal", (ra.get("final_height_fnv"), ra.get("final_splat_fnv")) == (rb.get("final_height_fnv"), rb.get("final_splat_fnv")),
          "%s/%s vs %s/%s" % (ra.get("final_height_fnv"), ra.get("final_splat_fnv"), rb.get("final_height_fnv"), rb.get("final_splat_fnv")), "final hashes equal")
    for tag, r in (("a", ra), ("b", rb)):
        bad = [s for s in (r.get("strokes") or []) if s.get("ticks_applied") != s.get("ticks")]
        g.bar("ticks_applied_eq_ticks_%s" % tag, not bad and bool(r.get("strokes")), "%d strokes, %d mismatched" % (len(r.get("strokes") or []), len(bad)), "ticks_applied == ticks")
    # B must really have varied the frame rate and hitched: the proof is that fps did not matter.
    cmd_a = open(os.path.join(a, "cmdline.txt"), encoding="utf-8").read() if os.path.isfile(os.path.join(a, "cmdline.txt")) else ""
    cmd_b = open(os.path.join(b, "cmdline.txt"), encoding="utf-8").read() if os.path.isfile(os.path.join(b, "cmdline.txt")) else ""
    fps_b = re.search(r"t\.MaxFPS (\d+)", cmd_b)
    fps_a = re.search(r"t\.MaxFPS (\d+)", cmd_a)
    g.bar("b_capped_fps", bool(fps_b) and int(fps_b.group(1)) in range(1, 60) and (not fps_a or int(fps_a.group(1)) == 0),
          "a MaxFPS=%s, b MaxFPS=%s" % (fps_a.group(1) if fps_a else "?", fps_b.group(1) if fps_b else "?"), "a uncapped (0), b capped below 60")
    fa = (ra.get("metrics") or {}).get("frame_ms", {}).get("p50", 0)
    fb = (rb.get("metrics") or {}).get("frame_ms", {}).get("p50", 0)
    g.bar("b_frame_time_differs", fb > 0 and fa > 0 and fb > 1.5 * fa, "median frame a=%.2f ms b=%.2f ms" % (fa, fb), "b's frames are clearly slower than a's")
    hit = [h for h in (rb.get("hitches") or []) if h.get("source") == "stroke"]
    g.bar("b_hitched_mid_stroke", len(hit) >= 1 and all(h.get("ms_slept", 0) >= 0.9 * h.get("ms_requested", 1) for h in hit), "%d hitch(es): %s" % (
        len(hit), [(h.get("ms_requested"), round(h.get("ms_slept", 0))) for h in hit]), ">= 1 mid-stroke hitch of the requested length")
    if g.ok:
        print("height_fnv equal, splat_fnv equal, ticks_applied==ticks")
    print("SAME-HASH %s" % ("PASS" if g.ok else "FAIL"))
    return g


# ---------------------------------------------------------------------------------------------------------------- simgrid mirror
FNV_OFFSET = 2166136261
FNV_PRIME = 16777619


def fnv_u32_le(h, v):
    for sh in (0, 8, 16, 24):
        h ^= (v >> sh) & 0xFF
        h = (h * FNV_PRIME) & 0xFFFFFFFF
    return h


def simgrid(run):
    """Python mirror of the sim mapping; independent of the C++ (a third implementation for C10 to agree with)."""
    g = Gates()
    t = read_json(os.path.join(run, "terrain.json"))
    e = int(t["half_extent_m"])
    w = int(t["width"])
    heights = np.frombuffer(open(os.path.join(run, "height.r32"), "rb").read(), dtype="<f4")
    if heights.size != w * w:
        g.bar("height_r32_size", False, "%d floats, expected %d" % (heights.size, w * w), "width^2")
        return g
    b = e - 128
    cells = np.arange(256)
    tex = ((2 * cells + 1) * 256) // 512          # HeightmapCellMapping.CellToTexel(c, 256, 256), integer arithmetic
    vert = tex + b
    grid = heights.reshape(w, w)[np.ix_(vert, vert)]   # [row, col]
    raw = (grid.astype(np.float32) * np.float32(65536.0)).astype(np.int32)  # (int)(h * ONE): float multiply then truncation toward zero
    h = FNV_OFFSET
    for v in raw.reshape(-1).tolist():
        h = fnv_u32_le(h, v & 0xFFFFFFFF)
    want = t["sim_grid_fnv"]
    g.bar("sim_grid_fnv", "0x%08x" % h == want, "python 0x%08x vs terrain.json %s" % (h, want), "equal")
    bad = []
    for q in t["probes"]:
        dx = (q["sim_x_raw"] + 128 * 65536) & 0xFFFFFFFF                # Fixed - Fixed (wrapping int), / cell(1.0) then >> 16
        dz = (q["sim_z_raw"] + 128 * 65536) & 0xFFFFFFFF
        dx = dx - (1 << 32) if dx >= (1 << 31) else dx
        dz = dz - (1 << 32) if dz >= (1 << 31) else dz
        col = min(255, max(0, dx >> 16))
        row = min(255, max(0, dz >> 16))
        if int(raw[row, col]) != q["value_raw"]:
            bad.append((q["sim_x_raw"], q["sim_z_raw"], int(raw[row, col]), q["value_raw"]))
    g.bar("probes_16", len(t["probes"]) == 16 and not bad, "16 probes, mismatches %s" % bad, "all 16 equal")
    g.bar("axes", t.get("axes") == "sim(x,z,h)->ue_cm(100x,100z,100h)", t.get("axes"), "axes recorded")
    print("SIMGRID %s" % ("PASS" if g.ok else "FAIL"))
    return g


# ---------------------------------------------------------------------------------------------------------------- summary
def load_run(path):
    res = load_results(path)
    run = {"dir": path.replace("\\", "/"), "tag": os.path.basename(os.path.normpath(path)), "res": res}
    if res is None:
        return run
    run["ticks"] = read_ticks(path) or []
    cmd = os.path.join(path, "cmdline.txt")
    run["cmd"] = open(cmd, encoding="utf-8").read() if os.path.isfile(cmd) else ""
    run["measured"] = os.path.isfile(os.path.join(path, "measure.txt"))
    m = re.search(r"-ChimeraTerrainUnits=(\d+)", run["cmd"])
    run["units"] = int(m.group(1)) if m else 0
    run["script"] = res.get("script")
    run["config"] = config_key(res, run["cmd"])
    run["vram_peak_mib"] = peak_vram(os.path.join(path, "smi.csv"))
    run["csv"] = None
    cp = os.path.join(path, "terrain.csv")
    if os.path.isfile(cp):
        try:
            run["csv"] = read_capture(cp)
        except (OSError, ValueError) as e:
            print("RUN %s: terrain.csv unreadable: %s" % (run["dir"], e))
    run["log_findings"] = None
    log = os.path.join(path, "game.log")
    if os.path.isfile(log):
        with open(log, encoding="utf-8", errors="replace") as f:
            run["log_findings"] = len(logscan.scan_lines(f.readlines(), profile="game"))
    return run


def peak_vram(path):
    if not os.path.isfile(path):
        return None
    peak = None
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = [x.strip() for x in f if x.strip()]
    if len(lines) < 2:
        return None
    heads = [h.strip() for h in lines[0].split(",")]
    idx = next((i for i, h in enumerate(heads) if h.startswith("memory.used")), None)
    if idx is None:
        return None
    for ln in lines[1:]:
        parts = ln.split(",")
        if len(parts) > idx:
            m = re.search(r"\d+", parts[idx])
            if m:
                peak = max(peak or 0, int(m.group()))
    return peak


csv.field_size_limit(2 ** 31 - 1)
CSV_COLS = ("FrameTime", "GPUTime", "GameThreadTime", "RenderThreadTime", "RHI/PrimitivesDrawn", "RHI/DrawCalls")


def read_capture(path):
    """CSV profiler capture -> {"phase": [phase per frame], <col>: [value per frame]} for CSV_COLS (CsvProfiler.cpp FCsvStreamWriter layout:
    header EVENTS,<series>...; one row per frame "<event text>,<values>"; the final repeated header holds every series name).
    The director records an event `phase_<name>` at each phase change, so frames are tagged with the phase they ran in."""
    import gzip
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8", newline="") as f:
        reader = csv.reader(f, quoting=csv.QUOTE_NONE)
        first = next(reader, None)
        if not first or first[0] != "EVENTS":
            raise ValueError("%s is not a UE CSV profiler file" % path)
        data, final = [], None
        for row in reader:
            if not row:
                continue
            if row[0] == "EVENTS":
                final = row
                break
            data.append(row)
    header = final or first
    names = header[1:]
    idx = {c: names.index(c) + 1 for c in CSV_COLS if c in names}
    out = {"phase": [], "complete": final is not None}
    for c in idx:
        out[c] = []
    phase = None
    for row in data:
        ms = re.findall(r"phase_([A-Za-z0-9_]+)", row[0])
        if ms:
            phase = ms[-1]   # several phase changes in one frame: the frame ends in the last one
        out["phase"].append(phase)
        for c, i in idx.items():
            try:
                out[c].append(float(row[i]) if i < len(row) else None)
            except ValueError:
                out[c].append(None)
    return out


def csv_phase_values(run, phase, col):
    """Values of one CSV column for the frames of one phase (None entries dropped); None when the run has no CSV."""
    cap = run.get("csv")
    if not cap or col not in cap:
        return None
    return [v for ph, v in zip(cap["phase"], cap[col]) if ph == phase and v is not None]


def config_key(res, cmd):
    """Configuration of a run: half, chunk, draw type, plus every non-default ExecCmds cvar and -ChimeraTerrain* option except where the
    run reads/writes and the unit workload (C1U is C1's configuration with units). `t.MaxFPS 20` or a hitch makes a different configuration."""
    o = res.get("options") or {}
    parts = ["half=%s" % o.get("half"), "chunk=%s" % o.get("chunk"), "draw=%s" % o.get("draw_type")]
    extra = []
    m = re.search(r'-ExecCmds="([^"]*)"', cmd)
    if m:
        for c in m.group(1).split(","):
            c = " ".join(c.split())
            if c and c not in STANDARD_EXEC:
                extra.append(c)
    for k, v in re.findall(r"-ChimeraTerrain([A-Za-z]+)=(\"[^\"]*\"|\S+)", cmd):
        if k in NON_CONFIG_OPTS or k in ("Chunk", "Half"):
            continue
        v = v.strip('"')
        if DEFAULT_OPTS.get(k) == v:
            continue
        extra.append("%s=%s" % (k, v))
    for flag in ("-windowed", "-Windowed"):
        if flag in cmd.split():
            extra.append("windowed")
    if "UnrealEditor.exe" not in cmd and cmd:
        extra.append("packaged")
    rx = re.search(r"-ResX=(\d+)", cmd)
    ry = re.search(r"-ResY=(\d+)", cmd)
    if rx and ry and (rx.group(1), ry.group(1)) != ("1920", "1080"):
        extra.append("res=%sx%s" % (rx.group(1), ry.group(1)))
    return " ".join(parts + sorted(set(extra)))


def group_key(run):
    """Reps of one configuration and script (tags may differ only in a trailing _<n> / _r<n>)."""
    return "%s | %s units=%d" % (run["config"], run["script"], run["units"])


def phase_stat(run, phase, series, stat="p50"):
    ph = ((run["res"].get("metrics") or {}).get("phases") or {}).get(phase)
    if not ph:
        return None
    return ph.get(series, {}).get(stat)


def fps_from_ms(ms):
    return 1000.0 / ms if ms and ms > 0 else 0.0


def p6_check(res):
    """P6 bars for one SOAK results.json: (ok, detail, record). After-GC figures come from the sample the soak marked right after its
    blocking GC (memory.after_gc_index); a missing or negative figure fails the bar instead of passing it without data. Peak bodies, the
    body creation rate and the residue sample (undo cleared + gc + FMemory::Trim) are reported beside it, never gated."""
    m = res.get("memory") or {}
    chunks = (res.get("render") or {}).get("chunks", 0)
    col = res.get("collision") or {}
    peak = m.get("growth_peak_mb")
    after = m.get("growth_after_gc_mb")
    bodies = m.get("bodies_rmc_after_gc")
    missing = [k for k, v in (("growth_peak_mb", peak), ("growth_after_gc_mb", after), ("bodies_rmc_after_gc", bodies)) if v is None]
    if bodies is not None and bodies < 0:
        missing.append("bodies_rmc_after_gc<0")
    if not chunks:
        missing.append("render.chunks")
    ok = not missing and peak <= P6_PEAK_GROWTH_MB and after <= P6_FINAL_GROWTH_MB and bodies <= 2 * chunks
    soak_s = sum(w.get("seconds", 0) for w in (res.get("walks") or []) if w.get("op") == "soak")
    by = col.get("cook_ms_by_reason") or {}
    edit_cooks = sum((by.get(k) or {}).get("n", 0) for k in ("stroke_end", "mid_stroke", "undo", "redo"))
    mid = (by.get("mid_stroke") or {}).get("n", 0)
    rate = edit_cooks / soak_s if soak_s > 0 else None
    win = gc_window_excl_undo(m)
    rec = {"chunks": chunks, "growth_peak_mb": peak, "growth_after_gc_mb": after, "bodies_rmc_after_gc": bodies,
           "after_gc_excl_undo_mb": win[0], "peak_excl_undo_mb": win[1],
           "growth_delayed_mb": m.get("growth_delayed_mb"), "delayed_after_gc_s": m.get("delayed_after_gc_s"),
           "undo_mb_delayed": m.get("undo_mb_delayed"), "residue_after_gc_s": m.get("residue_after_gc_s"),
           "peak_bodies_rmc": m.get("peak_bodies_rmc"), "peak_bodies_total": m.get("peak_bodies_total"), "soak_s": soak_s,
           "bodies_created": edit_cooks, "bodies_created_per_s": rate, "mid_stroke_bodies_created": mid,
           "undo_mb_after_gc": m.get("undo_mb_after_gc"), "growth_residue_mb": m.get("growth_residue_mb"),
           "bodies_rmc_residue": m.get("bodies_rmc_residue"), "undo_mb_residue": m.get("undo_mb_residue"), "missing": missing}
    f = lambda v, fmt="%.0f": "missing" if v is None else fmt % v
    detail = "peak growth %s MB <= %.0f, after gc %s MB <= %.0f, RMC bodies after gc %s <= %d" % (
        f(peak), P6_PEAK_GROWTH_MB, f(after), P6_FINAL_GROWTH_MB, f(bodies, "%d"), 2 * chunks)
    if missing:
        detail += " (MISSING: %s)" % ", ".join(missing)
    detail += "; reported: peak bodies RMC %s / total %s, bodies created %d in %.0f s = %s/s (mid-stroke %d)" % (
        m.get("peak_bodies_rmc"), m.get("peak_bodies_total"), edit_cooks, soak_s, f(rate, "%.2f"), mid)
    if m.get("undo_mb_after_gc") is not None:
        detail += "; undo history at the gc sample %.0f MB" % m["undo_mb_after_gc"]
    if win[0] is not None:
        detail += " (growth excl. undo, by subtraction: %.0f MB at the gc sample, %.0f MB peak)" % win
    if m.get("growth_delayed_mb") is not None:
        detail += "; delayed sample %.0f s after gc growth %.0f MB (undo %.0f MB)" % (
            m.get("delayed_after_gc_s", -1), m["growth_delayed_mb"], m.get("undo_mb_delayed", -1))
    if m.get("growth_residue_mb") is not None:
        later = ", %.0f s after gc" % m["residue_after_gc_s"] if m.get("residue_after_gc_s") is not None else ""
        detail += "; residue (undo cleared, gc, trim%s) growth %.0f MB, RMC bodies %s" % (later, m["growth_residue_mb"], m.get("bodies_rmc_residue"))
    return ok, detail, rec


def gc_window_excl_undo(m):
    """Growth minus the undo history's own growth, recomputed from memory.samples over the gated window [baseline_index, after_gc_index]
    only (reported, never gated; a subtraction, not a measurement). Returns (at the gc sample, peak) or (None, None) without the data.
    Older results.json wrote growth_*_excl_undo_mb over every sample, the residue sample included; this does not read them."""
    samples = m.get("samples") or []
    b, g = m.get("baseline_index"), m.get("after_gc_index")
    if b is None or g is None or g < 0 or not (0 <= b <= g < len(samples)):
        return (None, None)
    s0 = samples[b]
    if s0.get("undo_mb", -1) < 0 or samples[g].get("undo_mb", -1) < 0:
        return (None, None)
    ex = lambda x: (x["mb"] - s0["mb"]) - (x["undo_mb"] - s0["undo_mb"])
    return (ex(samples[g]), max(ex(x) for x in samples[b:g + 1] if x.get("undo_mb", -1) >= 0))


def summary(paths, out_json, gate_config=None):
    runs = [load_run(p) for p in paths]
    bad_runs = [r for r in runs if r["res"] is None]
    for r in bad_runs:
        print("RUN %s: results.json missing" % r["dir"])
    runs = [r for r in runs if r["res"] is not None]
    gates = []   # (metric, status, detail)
    summ = {"runs": {}, "gates": []}

    def add(metric, ok, detail, measured_only=False, any_measured=True):
        """measured_only: a gated timing; unmeasured evidence is REPORTED, not PASS/FAIL (EXECUTION 3 C9)."""
        if detail.startswith("no samples"):
            status = "FAIL"   # a gate without data fails, measured or not
        elif measured_only and not any_measured:
            status = "REPORTED-" + ("pass" if ok else "fail")
        else:
            status = "PASS" if ok else "FAIL"
        gates.append((metric, status, detail))
        print("GATE %-12s %-26s %s" % (status, metric, detail))

    # ---- per-run overview
    print("== runs")
    print("%-20s %-6s %-9s %-8s %-5s %-9s %-5s %s" % ("tag", "script", "completed", "ops", "unit", "measured", "log", "configuration"))
    for r in runs:
        res = r["res"]
        o = res.get("options") or {}
        print("%-20s %-6s %-9s %-8s %-5d %-9s %-5s %s" % (
            r["tag"], r["script"], res.get("completed"), "%s/%s" % (res.get("ops_done"), res.get("ops_total")), r["units"],
            "yes" if r["measured"] else "no", r["log_findings"] if r["log_findings"] is not None else "n/a", r["config"]))
        summ["runs"][r["tag"]] = {"script": r["script"], "completed": res.get("completed"), "ops": [res.get("ops_done"), res.get("ops_total")],
                                  "measured": r["measured"], "units": r["units"], "options": o, "config": r["config"],
                                  "final_hashes": [res.get("final_height_fnv"), res.get("final_splat_fnv")]}
    all_ok = all(bool(r["res"].get("completed")) and r["res"].get("ops_done") == r["res"].get("ops_total") for r in runs) and bool(runs) and not bad_runs
    add("runs_completed", all_ok, "%d run(s); completed=true and ops_done == ops_total for each" % len(runs))
    add("log_scan", all(r["log_findings"] == 0 for r in runs) and bool(runs), "findings per run: %s" % {r["tag"]: r["log_findings"] for r in runs})
    gate_cfg = gate_config or DEFAULT_CONFIG
    print("gate configuration: %s%s" % (gate_cfg, "" if gate_config else " (default)"))
    cfg_runs = [r for r in runs if r["config"] == gate_cfg]
    meas = [r for r in cfg_runs if r["measured"]]
    any_measured = bool(meas)
    # Gated timings (P1-P4) come from the gate configuration's measured runs (EXECUTION 3 C9); without any they are REPORTED.
    gated_runs = meas if meas else cfg_runs
    other_cfgs = sorted({r["config"] for r in runs if r["config"] != gate_cfg})
    summ["gate_config"] = gate_cfg
    summ["gated_runs"] = [r["tag"] for r in gated_runs]

    # ---- frames: fps, GPU/GT/RT per phase
    print("\n== frame metrics per phase (director samples, ms; fps = 1000 / frame ms)")
    print("%-16s %-14s %6s %8s %8s %8s %8s %8s %8s %8s %6s" % ("tag", "phase", "n", "fps p50", "fps 1%lo", "frame", "GPU p50", "GT p50", "RT p50", "GPU p99", ">33ms"))
    for r in runs:
        phases = (r["res"].get("metrics") or {}).get("phases") or {}
        for name, ph in phases.items():
            fm = ph.get("frame_ms", {})
            print("%-16s %-14s %6d %8.1f %8.1f %8.2f %8.2f %8.2f %8.2f %8.2f %6d" % (
                r["tag"], name, fm.get("n", 0), fps_from_ms(fm.get("p50")), fps_from_ms(fm.get("p99")), fm.get("p50", 0),
                ph.get("gpu_ms", {}).get("p50", 0), ph.get("gt_ms", {}).get("p50", 0), ph.get("rt_ms", {}).get("p50", 0), ph.get("gpu_ms", {}).get("p99", 0),
                ph.get("frames_over_33ms", 0)))

    print("\n== CSV profiler per phase (frames of terrain.csv tagged by the director's phase events; ms)")
    print("%-16s %-14s %6s %8s %8s %8s %8s %8s %8s %10s" % ("tag", "phase", "n", "fps p50", "fps 1%lo", "GPU p50", "GT p50", "RT p50", "max ms", "prims p50"))
    for r in runs:
        cap = r.get("csv")
        if not cap:
            continue
        for ph in sorted({p_ for p_ in cap["phase"] if p_}, key=lambda x: cap["phase"].index(x)):
            ft = csv_phase_values(r, ph, "FrameTime") or []
            if not ft:
                continue
            g_ = csv_phase_values(r, ph, "GPUTime") or [0]
            t_ = csv_phase_values(r, ph, "GameThreadTime") or [0]
            rt_ = csv_phase_values(r, ph, "RenderThreadTime") or [0]
            pr = csv_phase_values(r, ph, "RHI/PrimitivesDrawn") or [0]
            print("%-16s %-14s %6d %8.1f %8.1f %8.2f %8.2f %8.2f %8.1f %10.0f" % (r["tag"], ph, len(ft), fps_from_ms(pctl(ft, 50)), fps_from_ms(pctl(ft, 99)),
                  pctl(g_, 50), pctl(t_, 50), pctl(rt_, 50), max(ft), pctl(pr, 50)))

    # ---- ticks by diameter (pooled over the gated runs' ticks.csv; other configurations tabled on their own, never pooled in)
    def tick_table(label, pool_):
        print("\n== brush ticks by diameter (ms; %s)" % label)
        bd = {}
        for t in pool_:
            bd.setdefault(int(round(t["d"])), []).append(t)
        print("%5s %6s %9s %9s %9s %9s %9s %9s %9s" % ("d", "n", "tot p50", "tot p99", "tot max", "apply50", "norm50", "upl50", "splat50"))
        for d in sorted(bd):
            rows = bd[d]
            col = lambda k: [x[k] for x in rows]  # noqa: E731
            print("%5d %6d %9.3f %9.3f %9.3f %9.3f %9.3f %9.3f %9.3f" % (d, len(rows), pctl(col("total_ms"), 50), pctl(col("total_ms"), 99), max(col("total_ms")),
                  pctl(col("apply_ms"), 50), pctl(col("normals_ms"), 50), pctl(col("upload_ms"), 50), pctl(col("splat_ms"), 50)))
        bm = {}
        for t in pool_:
            bm.setdefault(t["mode"], []).append(t["total_ms"])
        print("by mode: " + ", ".join("%s n=%d p50=%.3f p99=%.3f" % (m, len(v), pctl(v, 50), pctl(v, 99)) for m, v in sorted(bm.items())))
        return bd

    pool = [t for r in gated_runs for t in r["ticks"]]
    by_d = tick_table("gate configuration, pooled over %s: %s" % ("measured runs" if meas else "its runs, none measured", [r["tag"] for r in gated_runs]), pool)
    summ["ticks_by_diameter"] = {str(d): {"n": len(v), "total_p50": pctl([x["total_ms"] for x in v], 50), "total_p99": pctl([x["total_ms"] for x in v], 99)} for d, v in by_d.items()}
    summ["ticks_by_config"] = {}
    for cfg in other_cfgs:
        rs = [r for r in runs if r["config"] == cfg]
        bd = tick_table("REPORTED, configuration %s: %s" % (cfg, [r["tag"] for r in rs]), [t for r in rs for t in r["ticks"]])
        summ["ticks_by_config"][cfg] = {str(d): {"n": len(v), "total_p50": pctl([x["total_ms"] for x in v], 50), "total_p99": pctl([x["total_ms"] for x in v], 99)} for d, v in bd.items()}

    # ---- latency, memory, hashes
    print("\n== render-thread acceptance latency of ranged edits")
    lat_frames, lat_n, edits, during = [], 0, 0, 0
    for r in runs:
        rr = r["res"].get("render") or {}
        el = rr.get("edit_latency") or {}
        fr = el.get("frames") or {}
        print("%-16s edits=%s finished=%s frames p50=%s p95=%s p99=%s max=%s hist(0..4+)=%s ms p50=%.2f proxy_recreates_during_strokes=%s proxy_recreates=%s" % (
            r["tag"], rr.get("ranged_edits"), el.get("n"), fr.get("p50"), fr.get("p95"), fr.get("p99"), fr.get("max"), el.get("frames_hist_0_to_4plus"),
            (el.get("ms") or {}).get("p50", 0), rr.get("proxy_recreates_during_strokes"), rr.get("proxy_recreates")))
    for r in gated_runs:
        rr = r["res"].get("render") or {}
        el = rr.get("edit_latency") or {}
        if el.get("n"):
            lat_frames.append((el["frames"]["p50"], el["n"]))
            lat_n += el["n"]
        edits += rr.get("ranged_edits", 0)
        during += rr.get("proxy_recreates_during_strokes", 0)
    print("\n== memory (UsedPhysical MB) and bodies")
    for r in runs:
        m = r["res"].get("memory") or {}
        print("%-16s n=%s start=%.0f end=%.0f peak=%.0f growth_peak=%.0f growth_end=%.0f bodies total/rmc peak=%s/%s end=%s/%s vram_peak_mib=%s" % (
            r["tag"], m.get("n"), m.get("start_mb", 0), m.get("end_mb", 0), m.get("peak_mb", 0), m.get("growth_peak_mb", 0), m.get("growth_end_mb", 0),
            m.get("peak_bodies_total"), m.get("peak_bodies_rmc"), m.get("end_bodies_total"), m.get("end_bodies_rmc"), r["vram_peak_mib"]))
        if m.get("growth_after_gc_mb") is not None or m.get("growth_residue_mb") is not None:
            print("%-16s after gc (sample %s): growth %s MB, bodies total/rmc %s/%s, undo %s MB, excl. undo %s MB; delayed (%s s after gc, reported): growth %s MB, undo %s MB; residue (undo cleared + gc + trim, reported): growth %s MB, bodies rmc %s" % (
                "", m.get("after_gc_index"), _fmt(m.get("growth_after_gc_mb")), m.get("bodies_total_after_gc"), m.get("bodies_rmc_after_gc"),
                _fmt(m.get("undo_mb_after_gc")), _fmt(gc_window_excl_undo(m)[0]), _fmt(m.get("delayed_after_gc_s")), _fmt(m.get("growth_delayed_mb")),
                _fmt(m.get("undo_mb_delayed")), _fmt(m.get("growth_residue_mb")), m.get("bodies_rmc_residue")))
    print("\n== hashes")
    for r in runs:
        for n, h in sorted((r["res"].get("hashes") or {}).items()):
            print("%-16s %-12s height=%s splat=%s sim_grid=%s ticks=%s" % (r["tag"], n, h.get("height_fnv"), h.get("splat_fnv"), h.get("sim_grid_fnv"), h.get("ticks_applied_total")))
        print("%-16s %-12s height=%s splat=%s" % (r["tag"], "final", r["res"].get("final_height_fnv"), r["res"].get("final_splat_fnv")))
    print("\n== reported (not gated): triangles / chunks / hitches / dropped ticks / walks / settle")
    for r in runs:
        rr = r["res"].get("render") or {}
        print("%-16s triangles=%s chunks=%s walks=%s hitches=%s settle_ms=%s" % (
            r["tag"], rr.get("triangles"), rr.get("chunks"), [(w.get("ticks_applied"), w.get("ticks_dropped")) for w in r["res"].get("walks") or []],
            len(r["res"].get("hitches") or []), [round(s.get("settle_ms", 0)) for s in r["res"].get("settles") or []]))

    # ---- rep spread (like parse_csv.py: > 5 % between reps flags the result)
    print("\n== rep spread (reps of one configuration; > 5 % is flagged)")
    groups = {}
    for r in runs:
        groups.setdefault(group_key(r), []).append(r)
    flags = []
    for key, rs in sorted(groups.items()):
        if len(rs) < 2:
            continue
        for label, getter in (("idle frame p50 ms", lambda x: phase_stat(x, "idle_visible", "frame_ms")),
                              ("idle GPU p50 ms", lambda x: phase_stat(x, "idle_visible", "gpu_ms")),
                              ("walk frame p50 ms", lambda x: phase_stat(x, "walk", "frame_ms")),
                              ("tick total p50 ms", lambda x: pctl([t["total_ms"] for t in x["ticks"]], 50) if x["ticks"] else None)):
            vals = [getter(x) for x in rs]
            vals = [v for v in vals if v]
            if len(vals) < 2:
                continue
            spread = (max(vals) - min(vals)) / (sum(vals) / len(vals)) * 100.0
            tag = "FLAG" if spread > 5.0 else "ok"
            print("%s  %-18s reps=%d spread=%.1f %% %s" % (key, label, len(vals), spread, tag))
            if spread > 5.0:
                flags.append("%s %s %.1f %%" % (key, label, spread))
    if not flags:
        print("no flags")
    summ["rep_spread_flags"] = flags

    # ---- gates
    print("\n== plan C 5 metrics (%s)" % ("measured runs only for timings" if any_measured else "NO measured run: timings are REPORTED, not gated (EXECUTION 3 C9)"))
    mt = True  # timings are measured_only
    # P1
    for d, lim in sorted(P1_P50_MS.items()):
        rows = by_d.get(d) or []
        if not rows:
            add("P1 tick p50 d%d" % d, False, "no samples (no tick at d=%d)" % d, mt, any_measured)
        else:
            v = pctl([x["total_ms"] for x in rows], 50)
            add("P1 tick p50 d%d" % d, v <= lim, "%.3f ms <= %.1f (n=%d)" % (v, lim, len(rows)), mt, any_measured)
    alltick = [x["total_ms"] for x in pool]
    if alltick:
        add("P1 tick p99", pctl(alltick, 99) <= P1_P99_MS, "%.3f ms <= %.1f (n=%d)" % (pctl(alltick, 99), P1_P99_MS, len(alltick)), mt, any_measured)
    else:
        add("P1 tick p99", False, "no samples", mt, any_measured)
    # P2
    if lat_n:
        med = float(np.median([f for f, _ in lat_frames])) if lat_frames else 0.0
        add("P2 latency median", med <= P2_MEDIAN_FRAMES, "%.1f frames <= %.0f (%d edits)" % (med, P2_MEDIAN_FRAMES, lat_n), mt, any_measured)
        add("P2 in-place use", edits > 0 and during <= (1 - P2_IN_PLACE_FRACTION) * edits, "proxy_recreates_during_strokes %d of %d ranged edits (<= 5 %%)" % (during, edits), mt, any_measured)
    else:
        add("P2 latency median", False, "no samples", mt, any_measured)
        add("P2 in-place use", False, "no samples", mt, any_measured)
    # P3 (C1U): walk phase of runs with units
    p3 = [r for r in gated_runs if r["units"] >= 1000 and phase_stat(r, "walk", "frame_ms") is not None]
    if not p3:
        add("P3 fps with 1000 units", False, "no samples (no C1U run with -ChimeraTerrainUnits=1000)", mt, any_measured)
    else:
        for r in p3:
            # The CSV profiler frames of the walk phase are the artefact (plan C 5); the director's own per-phase samples are the fallback.
            walk = csv_phase_values(r, "walk", "FrameTime")
            idle_ms = csv_phase_values(r, "idle_visible", "FrameTime")
            if walk:
                src = "csv"
                med_ms, p99_ms, max_ms, n = pctl(walk, 50), pctl(walk, 99), max(walk), len(walk)
                over33 = sum(1 for v in walk if v > 33.3) / max(1, n)
                idle = pctl(idle_ms, 50) if idle_ms else (phase_stat(r, "idle_visible", "frame_ms") or 0)
            else:
                src = "director"
                ph = r["res"]["metrics"]["phases"]["walk"]
                fm = ph["frame_ms"]
                med_ms, p99_ms, max_ms, n = fm["p50"], fm["p99"], fm["max"], fm["n"]
                over33 = ph.get("frames_over_33ms", 0) / max(1, n)
                idle = phase_stat(r, "idle_visible", "frame_ms") or 0
            med, low1 = fps_from_ms(med_ms), fps_from_ms(p99_ms)
            add("P3 %s" % r["tag"], med >= P3_MEDIAN_FPS and low1 >= P3_LOW1_FPS and over33 <= P3_OVER33_FRAC and max_ms <= P3_MAX_FRAME_MS and (med_ms - idle) <= P3_SCULPT_MINUS_IDLE_MS,
                "median %.1f fps, 1%% low %.1f, >33 ms %.2f %%, max %.1f ms, sculpt-idle %.2f ms (%d frames, %s)" % (med, low1, over33 * 100, max_ms, med_ms - idle, n, src), mt, any_measured)
    # P4: terrain GPU = visible - hidden at idle (C1)
    gpus = []
    for r in gated_runs:
        vis_c = [csv_phase_values(r, p_, "GPUTime") for p_ in ("idle_visible", "idle_visible2")]
        hid_c = csv_phase_values(r, "idle_hidden", "GPUTime")
        vis_c = [pctl(v, 50) for v in vis_c if v]
        if vis_c and hid_c:
            gpus.append(float(np.median(vis_c)) - pctl(hid_c, 50))
            continue
        vis = [phase_stat(r, p_, "gpu_ms") for p_ in ("idle_visible", "idle_visible2")]
        hid = phase_stat(r, "idle_hidden", "gpu_ms")
        vis = [v for v in vis if v is not None]
        if vis and hid is not None:
            gpus.append(float(np.median(vis)) - hid)
    if not gpus:
        add("P4 terrain GPU ms", False, "no samples (no C1 run with idle_visible and idle_hidden phases)", mt, any_measured)
    else:
        v = float(np.median(gpus))
        add("P4 terrain GPU ms", v <= P4_TERRAIN_GPU_MS, "%.3f ms <= %.1f (median of %d run(s))" % (v, P4_TERRAIN_GPU_MS, len(gpus)), mt, any_measured)
    # P5: correctness from every S1 run (the four checkpoints) and every SOAK run (collision still current at the end); the timings pool
    # the gate configuration's S1, C1 and SOAK runs (plan C 5: cook samples from S1, C1, SOAK) and are measured_only (EXECUTION 3 C9).
    s1r = [r for r in runs if r["script"] == "S1"]
    if not s1r:
        add("P5 collision", False, "no samples (no S1 run)")
    for r in s1r + [r for r in runs if r["script"] == "SOAK"]:
        buf = io.StringIO()
        cg = Gates()
        with contextlib.redirect_stdout(buf):
            sk = skipped_collision(r["res"])
            if sk:
                cg.bar("P5_collision_skipped", False, "skipped x%d" % len(sk), "no collision op skipped")
            collision_bars(cg, r["res"], P5_CHECKPOINTS if r["script"] == "S1" else ("end",), r["tag"])
            cook_bars(cg, r["res"])
        gated = [x for x in cg.rows if x["status"] != "REPORT"]
        add("P5 %s correctness" % r["tag"], bool(gated) and all(x["pass"] for x in gated),
            "; ".join("%s %s" % (x["bar"], x["status"]) for x in gated))
    print("\n== collision cooks and GT apply (ms; per run)")
    print("%-16s %-9s %-7s %6s %8s %8s %8s %6s %8s %8s" % ("tag", "fast_cook", "during", "cooks", "cook p50", "cook p95", "cook max", "gt n", "gt p95", "gt max"))
    pool_cook, pool_gt = [], []
    for r in runs:
        col = r["res"].get("collision") or {}
        if not col.get("cooks"):
            continue
        ck, gt = col.get("cook_ms") or {}, col.get("gt_apply_ms") or {}
        opt = col.get("options") or {}
        print("%-16s %-9s %-7s %6s %8.2f %8.2f %8.2f %6s %8.3f %8.3f" % (r["tag"], opt.get("fast_cook"), opt.get("during_stroke_ms"), ck.get("n", 0),
              ck.get("p50", 0), ck.get("p95", 0), ck.get("max", 0), gt.get("n", 0), gt.get("p95", 0), gt.get("max", 0)))
        if r in gated_runs and r["script"] in ("S1", "C1", "SOAK"):
            pool_cook += col.get("cook_ms_values") or []
            pool_gt += col.get("gt_apply_ms_values") or []
    if not pool_cook:
        add("P5 cook p95", False, "no samples (no cook in the gate configuration's S1/C1/SOAK runs)", mt, any_measured)
    else:
        v = pctl(pool_cook, 95)
        add("P5 cook p95", v <= P5_COOK_P95_MS, "%.2f ms <= %.0f (n=%d pooled)" % (v, P5_COOK_P95_MS, len(pool_cook)), mt, any_measured)
    if not pool_gt:
        add("P5 GT apply p95", False, "no samples (no stroke-end/undo/redo batch in the gate configuration's S1/C1/SOAK runs)", mt, any_measured)
    else:
        v = pctl(pool_gt, 95)
        add("P5 GT apply p95", v <= P5_GT_APPLY_P95_MS, "%.3f ms <= %.0f (n=%d pooled)" % (v, P5_GT_APPLY_P95_MS, len(pool_gt)), mt, any_measured)
    # P6: memory, not a timing, so it gates on EVERY SOAK run of the gate configuration, measured or not (C5 task note; EXECUTION 3 C9
    # moves only timings to Phase 4). A stale soak is kept out by the folders passed, never demoted here. A SOAK in another configuration
    # (soak250, mi.MemoryResetDelay 0) is always REPORTED.
    soaks = [r for r in cfg_runs if r["script"] == "SOAK"]
    other_soaks = [r for r in runs if r["script"] == "SOAK" and r not in soaks]
    if not soaks:
        add("P6 soak growth", False, "no samples (no SOAK run)")
    summ["p6"] = {}
    for r in soaks + other_soaks:
        gated_soak = r in soaks
        ok, detail, rec = p6_check(r["res"])
        summ["p6"][r["tag"]] = dict(rec, gated=gated_soak)
        add("P6 %s" % r["tag"], ok, detail, not gated_soak, gated_soak)
    # P7, P11 from S1 runs (their own bars via --s1)
    if s1r:
        for r in s1r:
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                sg, _r, _ok = s1(r["dir"])
            # A run that did not complete, lost hash ops or mis-applied ticks cannot pass P7/P11 on the bars it did produce.
            base = ("log_scan", "completed", "results", "log_present", "ticks_applied_eq_ticks", "shots_present")
            ok_base = all(x["pass"] for x in sg.rows if x["bar"] in base) and any(x["bar"] == "completed" for x in sg.rows)
            r7 = [x for x in sg.rows if x["bar"].startswith("P7") or x["bar"].startswith("hash")]
            r11 = [x for x in sg.rows if x["bar"].startswith("P11")]
            ok7 = ok_base and bool(r7) and all(x["pass"] for x in r7)
            ok11 = ok_base and bool(r11) and all(x["pass"] for x in r11)
            add("P7 %s" % r["tag"], ok7, "hashes undo=pre_last2, redo=after, local image statistic, A/A floor and control (%d bars)" % len(r7))
            add("P11 %s" % r["tag"], ok11, "depth bars at after/undo/pre_last2 x rts80/oblique, full frame and footprint (%d bars)" % len(r11))
    else:
        add("P7 undo/redo exact", False, "no samples (no S1 run)")
        add("P11 GPU mesh = CPU", False, "no samples (no S1 run)")
    # P8 / P9 / P10: their own commands
    if len(s1r) >= 2:
        eq = len({(r["res"].get("final_height_fnv"), r["res"].get("final_splat_fnv")) for r in s1r}) == 1
        add("P8 determinism", eq, "final hashes of %s %s (full check: --same-hash A B)" % ([r["tag"] for r in s1r], "equal" if eq else "DIFFER"))
    else:
        add("P8 determinism", False, "no samples (needs two S1 runs; run `--same-hash s1_a s1_b`)")
    add("P9 sim grid", False, "no samples here: `--simgrid RUN` (python mirror) and C10's elevhash (C#) decide it")
    mouse = [r for r in runs if r["script"] == "MOUSE"]
    if mouse:
        add("P10 mouse", False, "%d MOUSE run(s) found; the mouse bars are C8's (strokes source=os, pick error, footprint)" % len(mouse))
    else:
        add("P10 mouse", False, "no samples (no MOUSE run)")

    npass = sum(1 for _, s, _ in gates if s == "PASS")
    nfail = sum(1 for _, s, _ in gates if s == "FAIL")
    nrep = sum(1 for _, s, _ in gates if s.startswith("REPORTED"))
    print("\nGATES: pass=%d fail=%d reported=%d flags=%d" % (npass, nfail, nrep, len(flags)))
    summ["gates"] = [{"metric": m, "status": s, "detail": d} for m, s, d in gates]
    summ["counts"] = {"pass": npass, "fail": nfail, "reported": nrep, "flags": len(flags)}
    if out_json:
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump(summ, f, indent=1)
    return nfail == 0 and not flags


# ---------------------------------------------------------------------------------------------------------------- main
def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--g1", metavar="RUN_DIR")
    ap.add_argument("--scan", nargs="+", metavar="RUN_DIR")
    ap.add_argument("--s1", metavar="RUN_DIR")
    ap.add_argument("--s1l", metavar="RUN_DIR")
    ap.add_argument("--ref", metavar="S1_RUN_DIR")
    ap.add_argument("--same-hash", nargs=2, metavar=("A_DIR", "B_DIR"))
    ap.add_argument("--simgrid", metavar="RUN_DIR")
    ap.add_argument("--summary", nargs="+", metavar="RUN_DIR")
    ap.add_argument("--json", metavar="OUT")
    ap.add_argument("--gate-config", metavar="KEY", help="configuration key (as printed in the summary's runs table) that P1-P4 gate")
    a = ap.parse_args(argv)

    def dirs(patterns):
        out = []
        for p in patterns:
            hits = sorted(glob.glob(p)) if any(c in p for c in "*?[") else [p]
            out.extend(h for h in hits if os.path.isdir(h))
        return out

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
    if a.s1:
        g, _res, ok = s1(a.s1)
        with open(os.path.join(a.s1, "s1.json"), "w", encoding="utf-8") as f:
            json.dump({"gate": "S1", "pass": ok, "bars": g.rows}, f, indent=1)
        return 0 if ok else 1
    if a.s1l:
        if not a.ref:
            ap.error("--s1l needs --ref")
        g = s1l(a.s1l, a.ref)
        with open(os.path.join(a.s1l, "s1l.json"), "w", encoding="utf-8") as f:
            json.dump({"gate": "S1L", "pass": g.ok, "bars": g.rows}, f, indent=1)
        return 0 if g.ok else 1
    if a.same_hash:
        g = same_hash(*a.same_hash)
        return 0 if g.ok else 1
    if a.simgrid:
        g = simgrid(a.simgrid)
        return 0 if g.ok else 1
    if a.summary:
        paths = dirs(a.summary)
        if not paths:
            print("no run dirs match %s" % a.summary)
            return 2
        return 0 if summary(paths, a.json, a.gate_config) else 1
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
