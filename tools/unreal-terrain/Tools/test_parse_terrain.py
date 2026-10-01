#!/usr/bin/env python3
"""Self-tests for parse_terrain.py (C4): python T/Tools/test_parse_terrain.py  (no Unreal needed; synthetic run folders)."""
import contextlib
import io
import json
import os
import struct
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np  # noqa: E402

import parse_terrain as pt  # noqa: E402


def fnv1a(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


def quiet(fn, *a, **k):
    with contextlib.redirect_stdout(io.StringIO()):
        return fn(*a, **k)


def write_run(d, hashes, final, strokes_ok=True, cmd="t.MaxFPS 0", hitch=False, frame_p50=8.0):
    os.makedirs(d, exist_ok=True)
    res = {"completed": True, "ops_done": 3, "ops_total": 3, "exit_code": 0, "final_height_fnv": final[0], "final_splat_fnv": final[1],
           "hashes": {n: {"height_fnv": h[0], "splat_fnv": h[1]} for n, h in hashes.items()},
           "strokes": [{"ticks": 10, "ticks_applied": 10 if strokes_ok else 9}],
           "hitches": [{"source": "stroke", "ms_requested": 300, "ms_slept": 301}] if hitch else [],
           "metrics": {"frame_ms": {"p50": frame_p50}}}
    with open(os.path.join(d, "results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f)
    with open(os.path.join(d, "game.log"), "w", encoding="utf-8") as f:
        f.write("LogChimeraTerrain: Display: ok\n")
    with open(os.path.join(d, "cmdline.txt"), "w", encoding="utf-8") as f:
        f.write(cmd)


class ParseTests(unittest.TestCase):
    def test_fnv_matches_byte_fnv(self):
        for v in (0, 1, 65536, 0xFFFFFFFF, 0x80000000, 12345678):
            self.assertEqual(pt.fnv_u32_le(pt.FNV_OFFSET, v), fnv1a(struct.pack("<I", v)))

    def test_percentile_nearest_rank(self):
        self.assertEqual(pt.pctl([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 5.0)
        self.assertEqual(pt.pctl([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 99), 10.0)
        self.assertEqual(pt.pctl([], 50), 0.0)

    def test_same_hash_pass_and_fail(self):
        h = {n: ("0x%08x" % i, "0x%08x" % (i + 100)) for i, n in enumerate(("pre_last2", "after", "undo", "redo"))}
        with tempfile.TemporaryDirectory() as t:
            a, b, c = (os.path.join(t, x) for x in "abc")
            write_run(a, h, ("0x1", "0x2"))
            write_run(b, h, ("0x1", "0x2"), cmd="t.MaxFPS 20", hitch=True, frame_p50=50.0)
            self.assertTrue(quiet(pt.same_hash, a, b).ok)
            h2 = dict(h)
            h2["after"] = ("0xdead", "0xbeef")
            write_run(c, h2, ("0x1", "0x2"), cmd="t.MaxFPS 20", hitch=True, frame_p50=50.0)
            self.assertFalse(quiet(pt.same_hash, a, c).ok)
            write_run(c, h, ("0x1", "0x2"), strokes_ok=False, cmd="t.MaxFPS 20", hitch=True, frame_p50=50.0)
            self.assertFalse(quiet(pt.same_hash, a, c).ok)
            write_run(c, h, ("0x1", "0x2"), cmd="t.MaxFPS 20", hitch=False, frame_p50=50.0)
            self.assertFalse(quiet(pt.same_hash, a, c).ok, "no hitch in b must fail")

    def test_simgrid_mirror_negative_truncation(self):
        # A 257x257 field (E = 128, B = 0): heights -0.3 and -1.00001 must truncate toward zero, never floor.
        e, w = 128, 257
        hts = np.zeros((w, w), dtype="<f4")
        hts[5, 3] = np.float32(-0.3)         # row 5, col 3
        hts[0, 0] = np.float32(-1.00001)
        with tempfile.TemporaryDirectory() as t:
            with open(os.path.join(t, "height.r32"), "wb") as f:
                f.write(hts.tobytes())
            raw = (hts[:256, :256].astype(np.float32) * np.float32(65536.0)).astype(np.int32)
            self.assertEqual(int(raw[5, 3]), -19660)       # floor would give -19661
            self.assertEqual(int(raw[0, 0]), -65536)       # floor would give -65537
            h = pt.FNV_OFFSET
            for v in raw.reshape(-1).tolist():
                h = pt.fnv_u32_le(h, v & 0xFFFFFFFF)
            probes = [{"sim_x_raw": -128 * 65536 + 3 * 65536 + 100, "sim_z_raw": -128 * 65536 + 5 * 65536, "value_raw": -19660}]
            probes += [{"sim_x_raw": 0, "sim_z_raw": 0, "value_raw": int(raw[128, 128])}] * 15
            with open(os.path.join(t, "terrain.json"), "w", encoding="utf-8") as f:
                json.dump({"half_extent_m": e, "width": w, "sim_grid_fnv": "0x%08x" % h, "probes": probes, "axes": "sim(x,z,h)->ue_cm(100x,100z,100h)"}, f)
            self.assertTrue(quiet(pt.simgrid, t).ok)

    @staticmethod
    def collision_record(disagree=0, max_dz=0.01, wait_ok=True, both_hit=1800, vertex_n=400, errors=0, no_tri=0, drop=None):
        def wait(n):
            return {"name": n, "ok": wait_ok, "chunks": 25, "min_trimeshes": 1 if wait_ok else 0, "probes_hit": 25 if wait_ok else 20, "ms": 40, "touched": 4}

        def verify(n):
            return {"name": n, "disagreements": disagree, "max_dz_cm": max_dz, "rays": 2000 + vertex_n,
                    "frustum": {"n": 2000, "both_hit": both_hit, "physics_only_hit": disagree, "pick_only_hit": 0, "foreign_hit": 0, "max_dz_cm": max_dz},
                    "vertex": {"n": vertex_n, "rects": 2, "both_hit": vertex_n, "physics_only_hit": 0, "pick_only_hit": 0, "foreign_hit": 0, "max_dz_cm": 0.0}}
        names = [n for n in pt.P5_CHECKPOINTS if n != drop]
        return {"options": {"fast_cook": True, "during_stroke_ms": 0}, "cooks": 40, "updated": 40, "ignored": 0, "errors": errors, "unknown": 0,
                "updated_without_trimesh": no_tri, "batches": 12, "batches_incomplete": 0,
                "cook_ms": {"n": 15, "p50": 8.0, "p95": 12.0, "max": 14.0}, "init_cook_ms": {"n": 25, "p95": 30.0},
                "gt_apply_ms": {"n": 10, "p50": 0.5, "p95": 0.9, "max": 1.0}, "submit_ms": {"p95": 0.4}, "physics_state_ms": {"p95": 0.5},
                "cook_ms_values": [8.0] * 15, "gt_apply_ms_values": [0.5] * 10,
                "waits": [wait(n) for n in names], "verifies": [verify(n) for n in names]}

    def p5_rows(self, t, name, collision, skipped=None):
        h = {n: ("0x%08x" % i, "0x%08x" % (i + 100)) for i, n in enumerate(("pre_last2", "after", "undo", "redo"))}
        d = os.path.join(t, name)
        write_run(d, h, ("0x1", "0x2"))
        p = os.path.join(d, "results.json")
        res = json.load(open(p, encoding="utf-8"))
        if collision is not None:
            res["collision"] = collision
        if skipped:
            res["skipped"] = skipped
        json.dump(res, open(p, "w", encoding="utf-8"))
        g, _res, _ok = quiet(pt.s1, d)
        return {r["bar"]: r["status"] for r in g.rows if r["bar"].startswith("P5")}

    def test_p5_collision_bars(self):
        with tempfile.TemporaryDirectory() as t:
            rows = self.p5_rows(t, "good", self.collision_record())
            for cp in pt.P5_CHECKPOINTS:
                self.assertEqual(rows["P5 collision %s" % cp], "PASS", rows)
            self.assertEqual(rows["P5_cooks_valid"], "PASS")
            self.assertEqual(rows["P5_cook_p95"], "REPORT", "cook p95 is reported, never gated, outside the measured campaign")
            self.assertEqual(rows["P5_gt_apply_p95"], "REPORT")
            # No collision record at all: every checkpoint and the cook bar fail (never pass vacuously).
            rows = self.p5_rows(t, "none", None)
            self.assertTrue(all(rows["P5 collision %s" % cp] == "FAIL" for cp in pt.P5_CHECKPOINTS), rows)
            self.assertEqual(rows["P5_cooks_valid"], "FAIL")
            # One disagreement, |dz| over 1 cm, a failed wait, too few rays, a missing checkpoint: each fails.
            self.assertEqual(self.p5_rows(t, "dis", self.collision_record(disagree=1))["P5 collision after"], "FAIL")
            self.assertEqual(self.p5_rows(t, "dz", self.collision_record(max_dz=1.01))["P5 collision undo"], "FAIL")
            self.assertEqual(self.p5_rows(t, "wait", self.collision_record(wait_ok=False))["P5 collision redo"], "FAIL")
            self.assertEqual(self.p5_rows(t, "few", self.collision_record(both_hit=100))["P5 collision pre_last2"], "FAIL")
            self.assertEqual(self.p5_rows(t, "novert", self.collision_record(vertex_n=0))["P5 collision pre_last2"], "FAIL")
            rows = self.p5_rows(t, "drop", self.collision_record(drop="undo"))
            self.assertEqual((rows["P5 collision undo"], rows["P5 collision redo"]), ("FAIL", "PASS"))
            # Updated bodies without a trimesh (the unpatched-RMC symptom) or errors fail the cook bar.
            self.assertEqual(self.p5_rows(t, "notri", self.collision_record(no_tri=1))["P5_cooks_valid"], "FAIL")
            self.assertEqual(self.p5_rows(t, "err", self.collision_record(errors=1))["P5_cooks_valid"], "FAIL")
            # A skipped collision op fails P5 even when records exist.
            rows = self.p5_rows(t, "sk", self.collision_record(), skipped=[{"op": "wait_collision", "reason": "skipped"}])
            self.assertEqual(rows["P5_collision_skipped"], "FAIL")

    def test_config_key(self):
        base = ('"UnrealEditor.exe" x.uproject -game -ResX=1920 -ResY=1080 -ExecCmds="t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100,'
                'r.HighResScreenshotDelay 64" -ChimeraTerrainScript="D:/S/C1.json" -ChimeraTerrainOut="D:/O/c1" -ChimeraTerrainChunk=64 -ChimeraTerrainHalf=160')
        opts = {"options": {"half": 160, "chunk": 64, "draw_type": "Dynamic"}}
        self.assertEqual(pt.config_key(opts, base), pt.DEFAULT_CONFIG)
        self.assertEqual(pt.config_key(opts, base + " -ChimeraTerrainUnits=1000"), pt.DEFAULT_CONFIG, "units are a workload")
        self.assertNotEqual(pt.config_key({"options": {"half": 160, "chunk": 32, "draw_type": "Dynamic"}}, base), pt.DEFAULT_CONFIG)
        ipu = base.replace("r.HighResScreenshotDelay 64", "r.HighResScreenshotDelay 64,RealtimeMesh.InPlaceUpdate.Enabled 0")
        self.assertIn("RealtimeMesh.InPlaceUpdate.Enabled 0", pt.config_key(opts, ipu))
        self.assertIn("t.MaxFPS 20", pt.config_key(opts, base.replace("t.MaxFPS 0", "t.MaxFPS 20")))
        # Options spelled at their defaults stay in the default configuration; other values do not.
        self.assertEqual(pt.config_key(opts, base + " -ChimeraTerrainCollisionDuringStroke=0 -ChimeraTerrainFastCook=1"), pt.DEFAULT_CONFIG)
        self.assertIn("CollisionDuringStroke=250", pt.config_key(opts, base + " -ChimeraTerrainCollisionDuringStroke=250"))
        self.assertIn("FastCook=0", pt.config_key(opts, base + " -ChimeraTerrainFastCook=0"))

    @staticmethod
    def soak_res(peak=50.0, after=40.0, bodies=25, chunks=25):
        mem = {"growth_peak_mb": peak, "after_gc_index": 3}
        if after is not None:
            mem["growth_after_gc_mb"] = after
        if bodies is not None:
            mem["bodies_rmc_after_gc"] = bodies
        return {"script": "SOAK", "completed": True, "ops_done": 1, "ops_total": 1, "options": {"half": 160, "chunk": 64, "draw_type": "Dynamic"},
                "render": {"chunks": chunks}, "memory": mem, "walks": [{"op": "soak", "seconds": 300.0}],
                "collision": {"cook_ms_by_reason": {"init": {"n": 25}, "stroke_end": {"n": 600}, "mid_stroke": {"n": 0}}}}

    def test_p6_check_bars_and_missing_data(self):
        ok, _d, rec = pt.p6_check(self.soak_res())
        self.assertTrue(ok)
        self.assertAlmostEqual(rec["bodies_created_per_s"], 2.0)
        self.assertFalse(pt.p6_check(self.soak_res(after=101.0))[0], "after-GC growth over 100 MB fails")
        self.assertFalse(pt.p6_check(self.soak_res(peak=301.0))[0], "peak growth over 300 MB fails")
        self.assertFalse(pt.p6_check(self.soak_res(bodies=51))[0], "more than 2 x chunks RMC bodies fails")
        ok, d, _r = pt.p6_check(self.soak_res(bodies=None))
        self.assertFalse(ok, "a missing body count fails, never passes without data")
        self.assertIn("MISSING", d)
        self.assertFalse(pt.p6_check(self.soak_res(bodies=-1))[0], "an uncounted (-1) sample fails")
        self.assertFalse(pt.p6_check(self.soak_res(after=None))[0], "no after-GC sample fails")

    def test_p6_summary_gates_the_right_soaks(self):
        base = ('"UnrealEditor.exe" x.uproject -game -ResX=1920 -ResY=1080 -ExecCmds="t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100,'
                'r.HighResScreenshotDelay 64" -ChimeraTerrainScript="D:/S/SOAK.json" -ChimeraTerrainOut="D:/O/x"')

        def mk(t, tag, res, cmd, measured=False):
            d = os.path.join(t, tag)
            os.makedirs(d)
            with open(os.path.join(d, "results.json"), "w", encoding="utf-8") as f:
                json.dump(res, f)
            with open(os.path.join(d, "cmdline.txt"), "w", encoding="utf-8") as f:
                f.write(cmd)
            if measured:
                with open(os.path.join(d, "measure.txt"), "w", encoding="utf-8") as f:
                    f.write("measured")
            return d

        def p6_rows(paths, t):
            out = os.path.join(t, "summ.json")
            quiet(pt.summary, paths, out)
            with open(out, encoding="utf-8") as f:
                return {g["metric"]: g["status"] for g in json.load(f)["gates"] if g["metric"].startswith("P6")}

        with tempfile.TemporaryDirectory() as t:
            bad = mk(t, "soak_old", self.soak_res(after=160.0), base)
            r250 = mk(t, "soak250", self.soak_res(after=400.0, peak=400.0), base + " -ChimeraTerrainCollisionDuringStroke=250")
            rows = p6_rows([bad, r250], t)
            self.assertEqual(rows["P6 soak_old"], "FAIL", "with no measured soak the gate configuration's soak gates")
            self.assertEqual(rows["P6 soak250"], "REPORTED-fail", "another configuration is reported")
            good = mk(t, "soak_meas", self.soak_res(after=40.0), base, measured=True)
            rows = p6_rows([bad, good, r250], t)
            self.assertEqual(rows["P6 soak_meas"], "PASS", "the measured soak gates")
            self.assertEqual(rows["P6 soak_old"], "FAIL", "every gate-configuration soak gates: a measured pass never demotes an unmeasured fail")
            self.assertEqual(rows["P6 soak250"], "REPORTED-fail")
            spelled = mk(t, "soak_spelled", self.soak_res(after=160.0), base + " -ChimeraTerrainCollisionDuringStroke=0")
            rows = p6_rows([spelled], t)
            self.assertEqual(rows["P6 soak_spelled"], "FAIL", "CollisionDuringStroke=0 spelled out is still the gated configuration")

    def test_p6_excl_undo_window_ends_at_the_gc_sample(self):
        # baseline 0; gc sample 2; a residue sample 3 with the history cleared must not leak into the undo-excluded figures.
        samples = [{"mb": 1000.0, "undo_mb": 0.0}, {"mb": 1150.0, "undo_mb": 120.0}, {"mb": 1140.0, "undo_mb": 120.0},
                   {"mb": 1150.0, "undo_mb": 0.0}]
        m = {"baseline_index": 0, "after_gc_index": 2, "samples": samples}
        at_gc, peak = pt.gc_window_excl_undo(m)
        self.assertAlmostEqual(at_gc, 20.0)
        self.assertAlmostEqual(peak, 30.0)
        self.assertEqual(pt.gc_window_excl_undo({"baseline_index": 0, "after_gc_index": -1, "samples": samples}), (None, None))

    def test_csv_phase_last_event_wins(self):
        with tempfile.TemporaryDirectory() as t:
            p = os.path.join(t, "terrain.csv")
            with open(p, "w", encoding="utf-8", newline="") as f:
                f.write("EVENTS,FrameTime,GPUTime\n")
                f.write("phase_idle_visible,10,5\n")
                f.write(",11,5\n")
                f.write("phase_transition;phase_idle_hidden,12,4\n")
                f.write(",13,4\n")
                f.write("EVENTS,FrameTime,GPUTime\n")
            cap = pt.read_capture(p)
            self.assertEqual(cap["phase"], ["idle_visible", "idle_visible", "idle_hidden", "idle_hidden"])
            self.assertTrue(cap["complete"])


    def test_c7_paint_bar(self):
        """C7: the paint bar counts changed pixels inside rts80/paint only; a grey-like unchanged paint shot fails it."""
        from PIL import Image
        with tempfile.TemporaryDirectory() as t:
            write_run(t, {}, ("0x0", "0x0"))
            r = json.load(open(os.path.join(t, "results.json")))
            r["material"] = {"path": pt.C7_GROUND_PATH, "ground": True, "error": ""}
            json.dump(r, open(os.path.join(t, "results.json"), "w"))
            base = np.full((90, 160, 3), 120, dtype=np.uint8)
            painted = base.copy()
            painted[20:60, 40:100] = 60          # the painted disc changed
            pts = [[x, y] for y in range(20, 60) for x in range(40, 100)]
            json.dump({"rts80": {"viewport": [160, 90], "paint": pts}}, open(os.path.join(t, "footprints.json"), "w"))
            for name, im in (("sculpt", base), ("paint", painted), ("before", base), ("before_aa", base)):
                Image.fromarray(im).save(os.path.join(t, name + ".png"))
            g, vals = quiet(pt.c7, t)
            self.assertTrue(g.ok, g.rows)
            self.assertGreater(vals["paint_changed_frac"], 0.9)
            Image.fromarray(base).save(os.path.join(t, "paint.png"))
            g, vals = quiet(pt.c7, t)
            self.assertFalse(g.ok)
            self.assertEqual(vals["paint_changed_frac"], 0.0)

if __name__ == "__main__":
    unittest.main(verbosity=2)
