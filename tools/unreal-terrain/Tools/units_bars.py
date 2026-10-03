#!/usr/bin/env python3
"""units_bars.py - the unit-layer bars of plan C 4 C9 (task C9): parse_terrain.py --units RUN_DIR [--expect-units N] [--min-verifies K].

Bars (one run of VIDEO, UNITX, C1U or C1US with -ChimeraTerrainUnits=N):
  scan / completed        the shared log scan and results.json completed=true with ops_done == ops_total;
  units_active            results.json units.active, units.count == N (N from --expect-units, else from cmdline.txt);
  verify_present/pass     at least K units_verify rows (default 1; --min-verifies 0 for a script with none) and every one passed: pushed state == a full recompute from the heightfield, the ISM's
                          instance transforms read back equal it (position <= 0.01 cm, rotation <= 1e-4), z == HF.SampleSurface at the unit's x, y
                          (<= 0.01 cm), nothing pending;
  hash_fields             every hash row carries units_fnv and units_count == N;
  locality                in the edit phases (sculpt, walk, movie, undo, redo) the p99 of units re-posed per flush stays below 60 % of N: a stroke moves
                          the units under its footprint, never the whole army (the units_examined vs units_updated split is reported);
  paint_moves_nothing     a paint stroke changes no height: the `paint` hash's units_fnv equals the hash before it when both exist (UNITX);
  edits_move_units        the first edit hash's units_fnv differs from `start` when both exist (UNITX);
  flatten_moves_units     the `flatten` hash's height_fnv and units_fnv differ from `smooth`'s when both exist (UNITX: the flatten stroke must land on relief);
  unit_material           game.log has no "missing usage flag" / "Default Material will be used in game" line: the units draw with their own material, not
                          the engine's WorldGrid fallback (a material without the InstancedStaticMeshes usage, fixed by Scripts/fix_unit_usage.py);
  z on the surface        units_verify's z == SampleSurface check uses the same SampleSurface the layer places with, so it only catches readback error; that
                          SampleSurface equals the DRAWN surface is S1's P11 depth bars (and the visual check of the video);
  proxy creates           REPORT: proxy creates after the first edit flush (PSO or compile replacements are not unit edits; the in-place update never marks the
                          render state dirty).
Exit 0 = pass, 1 = fail, 2 = usage/io.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import parse_terrain as pt  # noqa: E402

EDIT_PHASES = ("sculpt", "walk", "movie", "undo", "redo")
LOCAL_P99_FRAC = 0.60


MATERIAL_FALLBACK = re.compile(r"missing usage flag|Default Material will be used in game")


def material_fallbacks(run):
    """The game.log lines saying a material fell back to the engine default (empty when there is no game.log)."""
    path = os.path.join(run, "game.log")
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8", errors="replace") as f:
        return [ln.strip() for ln in f if MATERIAL_FALLBACK.search(ln)]


def units(run, expect=None, min_verifies=1):
    g = pt.Gates()
    res = pt.scan_run(run, g)
    if res is None:
        return g
    cmd_path = os.path.join(run, "cmdline.txt")
    cmd = open(cmd_path, encoding="utf-8").read() if os.path.isfile(cmd_path) else ""
    m = re.search(r"-ChimeraTerrainUnits=(\d+)", cmd)
    n = expect if expect is not None else (int(m.group(1)) if m else 0)
    u = res.get("units") or {}
    g.bar("units_active", bool(u.get("active")) and n > 0 and u.get("count") == n,
          "active=%s count=%s expected=%s error='%s'" % (u.get("active"), u.get("count"), n, u.get("error", "")),
          "the unit layer is active with exactly the requested count")
    ver = u.get("verifies") or []
    if min_verifies > 0:
        g.bar("verify_present", len(ver) >= min_verifies, "%d units_verify row(s)" % len(ver), ">= %d" % min_verifies)
    bad = [v for v in ver if not v.get("pass")]
    worst_pos = max([v.get("max_pos_err_cm", 0.0) for v in ver] or [0.0])
    worst_z = max([v.get("max_z_err_cm", 0.0) for v in ver] or [0.0])
    worst_rot = max([v.get("max_rot_err", 0.0) for v in ver] or [0.0])
    mism = sum(v.get("pushed_vs_full_mismatch", 0) + v.get("missing", 0) for v in ver)
    g.bar("verify_pass", (bool(ver) or min_verifies == 0) and not bad,
          "%d of %d passed; mismatches %d; max pos err %.5f cm, max z err %.5f cm, max rot err %.2e" % (len(ver) - len(bad), len(ver), mism, worst_pos, worst_z, worst_rot),
          "every units_verify passed (pushed == full recompute, ISM readback within float, z == SampleSurface)")
    for v in bad:
        print("  failed verify: %s" % json.dumps(v))
    hashes = res.get("hashes") or {}
    miss = [k for k, h in hashes.items() if "units_fnv" not in h or h.get("units_count") != n]
    g.bar("hash_fields", (bool(hashes) or min_verifies == 0) and not miss, "%d hash row(s), %d without units_fnv/units_count == %d" % (len(hashes), len(miss), n),
          "every hash row names the units' fnv and count")
    phases = u.get("phases") or {}
    rows = []
    over = []
    for ph in EDIT_PHASES:
        p = phases.get(ph)
        if not p:
            continue
        up = p.get("updated_per_flush") or {}
        rows.append("%s: flushes %s, updated/flush p50 %s p99 %s max %s, step ms p50 %.3f p99 %.3f max %.3f" % (
            ph, up.get("n"), up.get("p50"), up.get("p99"), up.get("max"), p["step_ms"]["p50"], p["step_ms"]["p99"], p["step_ms"]["max"]))
        if up.get("p99", 0) >= LOCAL_P99_FRAC * max(1, n):
            over.append(ph)
    for r in rows:
        print("  phase " + r)
    g.bar("locality", bool(rows) and not over,
          "%d edit phase(s); p99 re-posed per flush >= %.0f %% of %d in: %s" % (len(rows), 100 * LOCAL_P99_FRAC, n, over or "none"),
          "p99 units re-posed per flush < %.0f %% of N in every edit phase" % (100 * LOCAL_P99_FRAC))
    print("  totals: events seen %s marked %s, flushes %s (whole grid %s), examined %s, updated %s, skipped identical %s, max per flush %s" % (
        u.get("events_seen"), u.get("events_marked"), u.get("flushes"), u.get("flushes_whole_grid"), u.get("units_examined"), u.get("units_updated"),
        u.get("units_skipped_identical"), u.get("max_updated_per_flush")))
    if "paint" in hashes and "flatten" in hashes:
        a, b = hashes["flatten"].get("units_fnv"), hashes["paint"].get("units_fnv")
        g.bar("paint_moves_nothing", a is not None and a == b, "flatten %s paint %s" % (a, b), "a paint stroke changes no height, so units_fnv stays")
    if "start" in hashes and "raise" in hashes:
        a, b = hashes["start"].get("units_fnv"), hashes["raise"].get("units_fnv")
        g.bar("edits_move_units", a is not None and a != b, "start %s raise %s" % (a, b), "a raise under the army changes units_fnv")
    if "smooth" in hashes and "flatten" in hashes:
        hs, hf = hashes["smooth"], hashes["flatten"]
        moved = hs.get("height_fnv") != hf.get("height_fnv") and hs.get("units_fnv") is not None and hs.get("units_fnv") != hf.get("units_fnv")
        g.bar("flatten_moves_units", moved, "smooth %s/%s flatten %s/%s" % (hs.get("height_fnv"), hs.get("units_fnv"), hf.get("height_fnv"), hf.get("units_fnv")),
              "the flatten stroke changes heights and moves units")
    fb = material_fallbacks(run)
    g.bar("unit_material", fb is not None and not fb, "no game.log" if fb is None else ("%d fallback line(s)%s" % (len(fb), (": " + fb[0][-200:]) if fb else "")),
          "no material falls back to the default (missing usage flag)")
    g.bar("proxy_creates_after_first_edit", True,
          "%s (total %s, at first edit %s)" % (u.get("proxy_creates_after_first_edit"), u.get("proxy_creates"), u.get("proxy_creates_at_first_edit")),
          "reported", informational=True)
    return g


def main_hook(a):
    g = units(a.units, a.expect_units, a.min_verifies)
    with open(os.path.join(a.units, "units.json"), "w", encoding="utf-8") as f:
        json.dump({"gate": "UNITS", "pass": g.ok, "bars": g.rows}, f, indent=1)
    print("UNITS %s" % ("PASS" if g.ok else "FAIL"))
    return 0 if g.ok else 1
