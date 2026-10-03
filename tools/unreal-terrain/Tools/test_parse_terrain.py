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


GT0 = "/Game/Terrain/Scatter/Meshes/L0/GrassT0"


def write_assets(t, licence="project-original", usage_ok="1/1", random_flag="False", manifest_row=True, vc_row=True, vc_expect=None):
    """A minimal S3 asset report and ScatterSrc manifest for one loaded mesh (GrassT0, L0); returns (report, manifest) paths."""
    rp, mp = os.path.join(t, "report.json"), os.path.join(t, "manifest.json")
    json.dump({"checks": {"usage_ok": usage_ok, "deps_ok": True, "flip_ok": True, "vc_ok": True, "errors": 0}, "errors": [], "asset_settings_sha256": "35f87ad8",
               "material_tags": {"/Game/Terrain/Scatter/Materials/M_ScatterBlade": {"HasPerInstanceRandom": random_flag}},
               "meshes": {GT0: {"level": "L0", "slot": "GrassT0", "vertex_colours_needed": True}},
               "usage_rows": [{"mesh": GT0, "slot": "M_Blade", "ok": True}], "vc_rows": [{"path": GT0, "ok": True}] if vc_row else [], "vc_expect": vc_expect}, open(rp, "w", encoding="utf-8"))
    rows = [{"id": "GrassT0", "kind": "mesh", "level": "L0", "licence": licence}] if manifest_row else []
    json.dump({"rows": rows}, open(mp, "w", encoding="utf-8"))
    return rp, mp


class AssetFixture(unittest.TestCase):
    """Points scatter_bars' S3 report and manifest at a valid fixture for the duration of a test (no dependence on this machine's Out folder)."""

    def setUp(self):
        import scatter_bars
        self._sb = scatter_bars
        self._old = (scatter_bars.SCATTER_REPORT, scatter_bars.SCATTER_MANIFEST)
        self._tmp = tempfile.TemporaryDirectory()
        scatter_bars.SCATTER_REPORT, scatter_bars.SCATTER_MANIFEST = write_assets(self._tmp.name)

    def tearDown(self):
        self._sb.SCATTER_REPORT, self._sb.SCATTER_MANIFEST = self._old
        self._tmp.cleanup()


class ParseTests(AssetFixture):
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
    # ---- S4: --equal-hashes and --scatter -------------------------------------------------------------------------------------------
    def test_equal_hashes_pass_fail_and_saved_files(self):
        h = {n: ("0x%08x" % i, "0x%08x" % (i + 100)) for i, n in enumerate(("pre_last2", "after", "undo", "redo"))}
        with tempfile.TemporaryDirectory() as t:
            a, b, c = (os.path.join(t, x) for x in "abc")
            write_run(a, h, ("0x1", "0x2"))
            write_run(b, h, ("0x1", "0x2"))      # no capped fps, no hitch: equal-hashes has no timing bars (F26)
            self.assertTrue(quiet(pt.equal_hashes, a, b).ok)
            h2 = dict(h)
            h2["undo"] = ("0xdead", "0xbeef")
            write_run(c, h2, ("0x1", "0x2"))
            self.assertFalse(quiet(pt.equal_hashes, a, c).ok)
            write_run(c, h, ("0x1", "0x3"))
            self.assertFalse(quiet(pt.equal_hashes, a, c).ok, "a final splat difference fails")
            # Saved files are compared when both runs saved them.
            for d, payload in ((a, b"\x01\x02"), (b, b"\x01\x02")):
                for f in pt.SAVED_FILES:
                    with open(os.path.join(d, f), "wb") as fh:
                        fh.write(payload)
            self.assertTrue(quiet(pt.equal_hashes, a, b).ok)
            with open(os.path.join(b, "height.r32"), "wb") as fh:
                fh.write(b"\x01\x03")
            self.assertFalse(quiet(pt.equal_hashes, a, b).ok, "a saved height.r32 difference fails")

    def test_equal_hashes_scatter_config_mismatch(self):
        h = {"after": ("0x1", "0x2")}
        with tempfile.TemporaryDirectory() as t:
            a, b = os.path.join(t, "a"), os.path.join(t, "b")
            write_run(a, h, ("0x1", "0x2"))
            write_run(b, h, ("0x1", "0x2"))
            for d, cfg in ((a, "0xaaaa"), (b, "0xbbbb")):
                p = os.path.join(d, "results.json")
                res = json.load(open(p, encoding="utf-8"))
                res["hashes"]["after"].update({"scatter_fnv": "0x5", "scatter_live_fnv": "0x5", "scatter_count": 10})
                res["options"] = {"scatter": {"config_fnv": cfg, "level": "L0", "meshes": {"GrassT0": {"path": "/Game/x"}}}}
                json.dump(res, open(p, "w", encoding="utf-8"))
            g = quiet(pt.equal_hashes, a, b)
            self.assertFalse(g.ok)
            self.assertIn("config mismatch", [r for r in g.rows if r["bar"] == "scatter_config"][0]["value"])

    @staticmethod
    def scatter_run(d, verify_pass=True, recreates=0, final_live="0x9", hidden=True, busy_before=3, read_back=5, compile_edits=0, engine_edits=0, governor=0,
                    readback=None):
        os.makedirs(d, exist_ok=True)
        verify = {"name": "start", "pass": verify_pass, "scatter_fnv": "0x9", "scatter_live_fnv": "0x9" if verify_pass else "0x8", "instances_read_back": read_back,
                  "max_dpos_cm": 0.0, "max_drot_rad": 0.0, "max_dz_m": 0.0, "max_dup_rad": 0.0, "fails": [] if verify_pass else ["live fold differs"]}
        res = {"script": "SXSMOKE", "completed": True, "ops_done": 3, "ops_total": 3, "exit_code": 0,
               "options": {"scatter": {"config_fnv": "0x1234", "level": "L0", "params_error": "", "mesh_errors": "", "meshes": {"GrassT0": {"path": GT0 + ".GrassT0"}}, "governor": governor,
                                       "governor_readback": readback or {}, "predict_a_ms": 0.032, "predict_b_ms_per_change": 7e-05, "predict_c_ms_per_instance": 3e-05}},
               "hashes": {"start": {"height_fnv": "0x1", "splat_fnv": "0x2", "scatter_fnv": "0x9", "scatter_live_fnv": "0x9", "scatter_pending": False},
                          "final": {"height_fnv": "0x1", "splat_fnv": "0x2", "scatter_fnv": "0x9", "scatter_live_fnv": final_live, "scatter_pending": False}},
               "shots": {"hidden": {"path": os.path.join(d, "hidden.png")}} if hidden else {},
               "scatter": {"available": True, "enabled": True, "verifies": [verify], "in_flight_now": 0, "busy_now": 0,
                           "counters": {"dispatched": 10, "applied": 7, "skipped_identical": 1, "discarded_epoch": 2, "cancelled": 0},
                           "proxy_recreates": recreates, "proxy_recreates_during_edits": 0, "proxy_first_creates": 5, "proxy_expected_rebuilds": 5, "proxy_engine_recreates": engine_edits,
                           "proxy_engine_recreates_during_edits": engine_edits, "proxy_compile_recreates": 3 + compile_edits, "proxy_compile_recreates_during_edits": compile_edits,
                           "proxy_apply_dirtied_recreates": 0, "proxy_refills": 1, "proxy_refills_during_edits": 1,
                           "apply_rows": [[0.01 + 0.0001 * k, 0.03, k, k, 0, 0, 10 * k, 0, 0] for k in range(1, 9)],
                           "toggles": [{"value": False, "busy_before": busy_before, "in_flight_before": 0}, {"value": True}],
                           "scatter_gt_ms": {"all_but_fill": {"n": 3, "p50": 0.1}}, "scatter_flush_ms": {"all_but_fill": {}}, "apply_unit_ms_edits": {}, "init_ms": 900,
                           "proxy_event_kinds": ["first_create", "expected_rebuild", "refill", "engine_recreate", "pso_recreate", "compile_recreate", "recreate",
                                                 "shader_propagation", "asset_post_compile", "compile_busy_frame", "apply"],
                           "proxy_events": [[2, 10, 1], [3, 0, 1], [40, 7, -1]], "proxy_events_dropped": 0},
               "timeline": [{"index": 1, "op": "scatter_wait", "frame_start": 1, "frame_end": 30}, {"index": 2, "op": "hash", "frame_start": 30, "frame_end": 30,
                                                                                                    "name": "start"}]}
        with open(os.path.join(d, "results.json"), "w", encoding="utf-8") as f:
            json.dump(res, f)
        with open(os.path.join(d, "game.log"), "w", encoding="utf-8") as f:
            f.write("LogChimeraTerrain: Display: ok\n")
        if hidden:
            open(os.path.join(d, "hidden.png"), "wb").write(b"png")

    def test_scatter_pass_and_each_failure(self):
        with tempfile.TemporaryDirectory() as t:
            ok = os.path.join(t, "ok")
            self.scatter_run(ok)
            self.assertTrue(quiet(pt.scatter, ok)[0].ok)
            for i, kw in enumerate(({"verify_pass": False}, {"recreates": 1}, {"final_live": "0x7"}, {"hidden": False}, {"busy_before": 0}, {"read_back": 0},
                                    {"compile_edits": 1}, {"engine_edits": 1}, {"governor": 3, "readback": {"r.Nanite.MaxPixelsPerEdge": "1"}},
                                    {"governor": 6, "readback": {"r.Nanite.MaxPixelsPerEdge": "2"}})):
                d = os.path.join(t, "bad%d" % i)
                self.scatter_run(d, **kw)
                self.assertFalse(quiet(pt.scatter, d)[0].ok, kw)
            # Unbalanced counters and a scatter material warning in the log both fail.
            d = os.path.join(t, "counters")
            self.scatter_run(d)
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            res["scatter"]["counters"]["applied"] = 6
            json.dump(res, open(p, "w", encoding="utf-8"))
            self.assertFalse(quiet(pt.scatter, d)[0].ok)
            d = os.path.join(t, "log")
            self.scatter_run(d)
            with open(os.path.join(d, "game.log"), "a", encoding="utf-8") as f:
                f.write("LogMaterial: Warning: Missing usage flag on /Game/Terrain/Scatter/Materials/L0/MI_GrassT0_M_Blade\n")
            self.assertFalse(quiet(pt.scatter, d)[0].ok)
            # The governor read-back passes when each cvar reads back its value; the apply fit is reported.
            d = os.path.join(t, "gov")
            self.scatter_run(d, governor=6, readback={"r.Nanite.MaxPixelsPerEdge": "2.000000", "r.Shadow.Virtual.ResolutionLodBiasDirectional": "1.0"})
            g = quiet(pt.scatter, d)[0]
            self.assertTrue(g.ok)
            fit = [r for r in g.rows if r["bar"] == "apply_fit"][0]
            self.assertEqual(fit["status"], "REPORT")
            self.assertIn("least squares a 0.01000", fit["value"])

    def test_scatter_asset_and_licence_bars(self):
        """SX9 (plan 3.8 parser rule): every loaded mesh maps through report.json to a manifest row with an allowed licence; the asset report's flags hold."""
        import scatter_bars as sbm
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "run")
            self.scatter_run(d)
            bars = {r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}
            self.assertTrue(bars["SX9 licence"]["pass"], bars["SX9 licence"])
            self.assertIn("GrassT0=project-original", bars["SX9 licence"]["value"])
            self.assertTrue(bars["SX9 asset_report"]["pass"])
            self.assertTrue(bars["SX9 per_instance_random"]["pass"])
            cases = (({"licence": "CC-BY-NC-4.0"}, "SX9 licence"), ({"manifest_row": False}, "SX9 licence"), ({"usage_ok": "57/58"}, "SX9 asset_report"),
                     ({"random_flag": "True"}, "SX9 per_instance_random"), ({"vc_row": False}, "SX9 licence"),
                     ({"vc_row": False, "vc_expect": {"meshes_needing_vc": 1, "meshes_needing_vc_with_vc": 0, "missing": ["GrassT0"]}}, "SX9 licence"))
            for kw, bar in cases:
                sub_t = os.path.join(t, "a%d" % len(kw))
                os.makedirs(sub_t, exist_ok=True)
                sbm.SCATTER_REPORT, sbm.SCATTER_MANIFEST = write_assets(sub_t, **kw)
                g = quiet(pt.scatter, d)[0]
                self.assertFalse({r["bar"]: r for r in g.rows}[bar]["pass"], kw)
                self.assertFalse(g.ok, kw)
            # a mesh that needs vertex colours and has no vc row of its own passes when S3's vc_expect covers it (S3 samples vc_rows)
            sbm.SCATTER_REPORT, sbm.SCATTER_MANIFEST = write_assets(t, vc_row=False, vc_expect={"meshes_needing_vc": 1, "meshes_needing_vc_with_vc": 1, "missing": []})
            self.assertTrue({r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}["SX9 licence"]["pass"])
            # a mesh the report does not know, and a missing report, fail (no samples)
            sbm.SCATTER_REPORT, sbm.SCATTER_MANIFEST = write_assets(t)
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            res["options"]["scatter"]["meshes"]["Rogue"] = {"path": "/Game/Elsewhere/Rogue.Rogue"}
            json.dump(res, open(p, "w", encoding="utf-8"))
            self.assertFalse({r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}["SX9 licence"]["pass"])
            sbm.SCATTER_REPORT = os.path.join(t, "missing.json")
            self.assertIn("no samples", {r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}["SX9 asset_report"]["value"])

    def test_scatter_needs_the_event_log_and_reports_compile_events_per_phase(self):
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "run")
            self.scatter_run(d)
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            del res["scatter"]["proxy_events"]
            json.dump(res, open(p, "w", encoding="utf-8"))
            g = quiet(pt.scatter, d)[0]
            self.assertFalse(g.ok)
            self.assertIn("no samples", {r["bar"]: r for r in g.rows}["proxy_events_logged"]["value"])

    def test_shipping_run_reports_log_and_proxy_rows(self):
        """SX16 (c), (e): a Shipping run writes no log and is held to SX1 and SX5 only."""
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "ship")
            self.scatter_run(d, recreates=2)
            os.remove(os.path.join(d, "game.log"))
            self.assertFalse(quiet(pt.scatter, d)[0].ok)
            g = quiet(pt.scatter, d, shipping=True)[0]
            self.assertTrue(g.ok, [r for r in g.rows if not r["pass"]])
            d2 = os.path.join(t, "ship_bad")
            self.scatter_run(d2, verify_pass=False)
            self.assertFalse(quiet(pt.scatter, d2, shipping=True)[0].ok, "SX1 stays gated in Shipping")
            # plan 3.7: the governor read-back is checked Shipping included
            d3 = os.path.join(t, "ship_gov")
            self.scatter_run(d3, governor=3, readback={"r.Nanite.MaxPixelsPerEdge": "1"})
            os.remove(os.path.join(d3, "game.log"))
            g = quiet(pt.scatter, d3, shipping=True)[0]
            self.assertFalse(g.ok)
            self.assertEqual({r["bar"]: r for r in g.rows}["scatter_governor_readback"]["status"], "FAIL")

    def test_scatter_verify_full_up_axis_and_phase_labels(self):
        """A verify that compared near-vertical rocks by tilt angle only fails; compile-event phases are not labelled from a script changed since the run."""
        import hashlib
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "run")
            self.scatter_run(d)
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            sp = os.path.join(d, "script.json")
            body = json.dumps({"ops": [{"op": "scatter_wait", "phase": "fill"}, {"op": "hash"}]}).encode("utf-8")
            open(sp, "wb").write(body)
            res["script_path"] = sp
            res["script_sha256"] = hashlib.sha256(body).hexdigest()
            json.dump(res, open(p, "w", encoding="utf-8"))
            bars = {r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}
            self.assertTrue(bars["scatter_verify_full_up_axis"]["pass"])
            self.assertNotIn("phases unavailable", str(bars["compile_events_per_phase"]["value"]))
            self.assertIn("fill", str(bars["compile_events_per_phase"]["value"]))
            open(sp, "wb").write(body + b" ")
            bars = {r["bar"]: r for r in quiet(pt.scatter, d)[0].rows}
            self.assertIn("phases unavailable (script changed since the run)", str(bars["compile_events_per_phase"]["value"]))
            res["scatter"]["verifies"][0]["up_degenerate_angle_only"] = 124
            json.dump(res, open(p, "w", encoding="utf-8"))
            g = quiet(pt.scatter, d)[0]
            self.assertFalse({r["bar"]: r for r in g.rows}["scatter_verify_full_up_axis"]["pass"])
            self.assertFalse(g.ok)

    def test_summary_scatter_table_failure_is_a_gate_failure(self):
        import scatter_bars as sbm
        orig = sbm.summary_scatter

        def boom(*a, **k):
            raise RuntimeError("planted")
        sbm.summary_scatter = boom
        try:
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                with tempfile.TemporaryDirectory() as t:
                    d = os.path.join(t, "c1s")
                    write_run(d, {"x": ("0x1", "0x2")}, ("0x1", "0x2"))
                    pt.summary([d], None)
            self.assertRegex(out.getvalue(), r"GATE FAIL\s+SCATTER table\s+no samples \(the SCATTER table raised")
        finally:
            sbm.summary_scatter = orig

    def test_s1l_image_reference(self):
        """--img-ref: hashes stay against --ref; the image reference must have saved byte-identical terrain files."""
        with tempfile.TemporaryDirectory() as t:
            ref, img, run = (os.path.join(t, n) for n in ("ref", "img", "run"))
            for d, body in ((ref, b"A"), (img, b"A")):
                os.makedirs(d)
                for f in pt.SAVED_FILES:
                    open(os.path.join(d, f), "wb").write(body)
            # Only the reference check is exercised here (the image bars need real shots): a differing saved file fails it.
            open(os.path.join(img, pt.SAVED_FILES[0]), "wb").write(b"B")
            os.makedirs(run, exist_ok=True)
            write_run(run, {"loaded": ("0x1", "0x2")}, ("0x1", "0x2"))
            res = json.load(open(os.path.join(run, "results.json"), encoding="utf-8"))
            res["loaded"] = {"dir": ref}
            json.dump(res, open(os.path.join(run, "results.json"), "w", encoding="utf-8"))
            write_run(ref, {"redo": ("0x1", "0x2")}, ("0x1", "0x2"))
            g = quiet(pt.s1l, run, ref, img)
            row = [r for r in g.rows if r["bar"] == "image_reference_same_terrain"][0]
            self.assertFalse(row["pass"])
            self.assertFalse(g.ok)

    def test_teardown(self):
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "tearx")
            write_run(d, {"a": ("0x1", "0x2")}, ("0x1", "0x2"))
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            res["scatter"] = {"in_flight_now": 2, "busy_now": 2}
            json.dump(res, open(p, "w", encoding="utf-8"))
            line = "LogChimeraTerrain: Display: scatter teardown: in_flight_before=2 task_handles_waited=2 in_flight_after=0 busy_after=0 dispatched=10 applied=6 skipped=1 discarded=1 cancelled=2\n"
            with open(os.path.join(d, "game.log"), "a", encoding="utf-8") as f:
                f.write(line)
            self.assertTrue(quiet(pt.teardown, d).ok)
            with open(os.path.join(d, "game.log"), "w", encoding="utf-8") as f:
                f.write(line.replace("in_flight_after=0", "in_flight_after=1"))
            self.assertFalse(quiet(pt.teardown, d).ok)


import scatter_bars as sb  # noqa: E402


PAL = {"GrassSinkM": 1311, "TussockSinkM": 1966, "FlowerSinkM": 655, "NearCardSinkM": 1311, "ShrubSinkM": 5243, "FernSinkM": 1966, "RockSinkFrac": 16384,
       "RockNominalHM": 78643, "TreeBaseProbeM": 52429, "TreeSinkM": 3277, "E3DirtHi": 128, "E3RockHi": 112, "E3SnowHi": 64, "GLo": 96, "FlowerGLo": 200,
       "SgHi": 2992283648, "FlowerSlopeHi": 1677020262, "FernSlopeHi": 3024035754, "StHi": 1431655765, "HgHi": 7208960, "HtHi": 3932160, "ShrubZHi": 5570560,
       "TrunkDirt": 32, "TrunkRock": 64, "TreeConiferSnow": 150, "TreeBroadSnow": 24, "TreeProbeM": 262144, "TreeProbeDirt": 64, "ShrubRockHi": 128,
       "ShrubSnowHi": 48, "RockSnowHi": 160}


def make_rec(cls, mesh, xq, yq, zq, gx, gy, ix=0, iy=0, scale=4096):
    r = np.zeros(1, dtype=sb.REC_DTYPE)
    r["cls"], r["mesh"], r["stream"], r["ix"], r["iy"] = cls, mesh, 100 + cls, ix, iy
    r["xq"], r["yq"], r["zq"], r["gxq"], r["gyq"] = xq, yq, zq, gx, gy
    r["scale"] = scale
    return r


class ScatterBarsTests(AssetFixture):
    E = 8

    def terrain(self, d, dirt_patch=True):
        """E = 8: 17 x 17 vertices, a gentle ramp in x; splat 32 x 32 pure grass with a dirt patch on the east side. Saved as the run's files."""
        w, sw = 17, 32
        h = np.zeros((w, w), dtype="<f4")
        h += (np.arange(w, dtype="<f4") * 0.1)[None, :]
        spl = np.zeros((sw, sw, 4), dtype=np.uint8)
        spl[..., 0] = 255
        if dirt_patch:
            spl[:, 24:, 0] = 0
            spl[:, 24:, 1] = 255
        os.makedirs(d, exist_ok=True)
        h.tofile(os.path.join(d, "height.r32"))
        spl.tofile(os.path.join(d, "splat.rgba8"))
        json.dump({"half_extent_m": self.E, "width": w, "splat": {"width": sw}}, open(os.path.join(d, "terrain.json"), "w"))
        return sb.load_terrain(d)

    def valid_records(self, hq, spl):
        recs = []
        for i, (x, y) in enumerate([(-4.2, -3.1), (-1.3, 2.7), (0.4, 0.2), (2.1, -2.2), (3.3, 3.9)]):
            xq, yq = int(x * 65536), int(y * 65536)
            z, gx, gy = [int(v[0]) for v in sb.surface_q16(hq, self.E, np.array([xq]), np.array([yq]))]
            recs.append(make_rec(0, 0, xq, yq, z - PAL["GrassSinkM"], gx, gy, ix=i, iy=i))
        return recs

    def test_oracle_passes_valid_and_flags_each_planted_violation(self):
        with tempfile.TemporaryDirectory() as t:
            hq, spl, e = self.terrain(t)
            good = np.concatenate(self.valid_records(hq, spl))
            o = sb.oracle(good, hq, spl, e, PAL)
            self.assertEqual((o["bad_z"], o["bad_gradient"], o["violation_total"]), (0, 0, 0), o)
            self.assertLess(o["max_dz_mm"], 0.1)
            xq, yq = int(-1.3 * 65536), int(2.7 * 65536)
            z, gx, gy = [int(v[0]) for v in sb.surface_q16(hq, e, np.array([xq]), np.array([yq]))]
            cases = {
                "z": make_rec(0, 0, xq, yq, z - PAL["GrassSinkM"] + 131, gx, gy),
                "gradient": make_rec(0, 0, xq, yq, z - PAL["GrassSinkM"], gx + 1, gy),
            }
            xd, yd = int(6.0 * 65536), int(1.0 * 65536)
            zd, gxd, gyd = [int(v[0]) for v in sb.surface_q16(hq, e, np.array([xd]), np.array([yd]))]
            cases["grass on dirt"] = make_rec(0, 0, xd, yd, zd - PAL["GrassSinkM"], gxd, gyd)
            cases["trunk on dirt"] = make_rec(4, 5, xd, yd, zd - PAL["TreeSinkM"], gxd, gyd)
            for name, bad in cases.items():
                o = sb.oracle(np.concatenate([good, bad]), hq, spl, e, PAL)
                self.assertGreaterEqual(o["bad_z"] + o["bad_gradient"] + o["violation_total"], 1, name)
            # exactly one: the clean records stay clean beside the planted one
            o = sb.oracle(np.concatenate([good, cases["z"]]), hq, spl, e, PAL)
            self.assertEqual((o["bad_z"], o["bad_gradient"], o["violation_total"]), (1, 0, 0))
            o = sb.oracle(np.concatenate([good, cases["gradient"]]), hq, spl, e, PAL)
            self.assertEqual((o["bad_z"], o["bad_gradient"]), (0, 1))
            o = sb.oracle(np.concatenate([good, cases["grass on dirt"]]), hq, spl, e, PAL)
            self.assertGreaterEqual(o["violations"].get("e3_splat", 0) + o["violations"].get("grass_wg", 0), 1)

    def test_dump_bar_on_a_hand_made_dump(self):
        with tempfile.TemporaryDirectory() as t:
            hq, spl, e = self.terrain(t)
            good = np.concatenate(self.valid_records(hq, spl) * 30)   # 150 records (the oracle wants >= 100)
            with open(os.path.join(t, "scatter_end.bin"), "wb") as f:
                f.write(good.tobytes())
            res = {"options": {"scatter": {"palette": PAL}}, "scatter": {"dumps": []}}
            g = pt.Gates()
            quiet(sb.dump_bar, g, t, res, "end", "oracle")
            self.assertTrue(g.ok, g.rows)
            xd, yd = int(6.0 * 65536), int(1.0 * 65536)
            zd, gxd, gyd = [int(v[0]) for v in sb.surface_q16(hq, e, np.array([xd]), np.array([yd]))]
            planted = np.concatenate([good, make_rec(4, 5, xd, yd, zd - PAL["TreeSinkM"], gxd, gyd)])
            with open(os.path.join(t, "scatter_end.bin"), "wb") as f:
                f.write(planted.tobytes())
            g = pt.Gates()
            quiet(sb.dump_bar, g, t, res, "end", "oracle")
            self.assertFalse(g.ok)
            self.assertIn("trunk_dirt", g.rows[-1]["value"])
            # a missing dump, or a missing palette, is no samples (FAIL), never a pass
            g = pt.Gates()
            quiet(sb.dump_bar, g, t, res, "nope", "oracle")
            self.assertFalse(g.ok)
            self.assertIn("no samples", g.rows[0]["value"])
            g = pt.Gates()
            quiet(sb.dump_bar, g, t, {"options": {"scatter": {}}}, "end", "oracle")
            self.assertIn("no samples", g.rows[0]["value"])

    def test_moved_instances_by_key(self):
        with tempfile.TemporaryDirectory() as t:
            n = 1500
            base = np.zeros(n + 30, dtype=sb.REC_DTYPE)
            base["cls"][:n] = 0
            base["cls"][n:] = 4
            base["ix"] = np.arange(n + 30)
            base["stream"] = 100 + base["cls"]
            after = base.copy()
            after["zq"][:1200] += 6 * 65536 // 100 + 1      # 6 cm: moved
            after["zq"][n:n + 25] += 65536                   # 1 m: moved (coarse)
            open(os.path.join(t, "scatter_before.bin"), "wb").write(base.tobytes())
            open(os.path.join(t, "scatter_redo.bin"), "wb").write(after.tobytes())
            g = pt.Gates()
            quiet(sb.moved_bar, g, t, {}, "before", "redo", "SX5 moved")
            self.assertTrue(g.ok, g.rows)
            after["zq"][:1200] -= 6 * 65536 // 100 + 1
            open(os.path.join(t, "scatter_redo.bin"), "wb").write(after.tobytes())
            g = pt.Gates()
            quiet(sb.moved_bar, g, t, {}, "before", "redo", "SX5 moved")
            self.assertFalse(g.ok)

    def test_flat_floors_and_the_empty_class(self):
        cls = {"Grass": 221854, "Tussock": 4561, "Flower": 6001, "NearCard": 0, "Tree": 1003, "Sapling": 348, "Shrub": 1474, "Fern": 7646, "Rock": 124}
        g = pt.Gates()
        quiet(sb.floors_bar, g, {"hashes": {"before": {"scatter_classes": cls}}}, "L0")
        self.assertTrue(g.ok, g.rows)
        for empty in ("Flower", "Rock", "Grass"):
            g = pt.Gates()
            quiet(sb.floors_bar, g, {"hashes": {"before": {"scatter_classes": dict(cls, **{empty: 3})}}}, "L0")
            self.assertFalse(g.ok, empty)
        g = pt.Gates()
        quiet(sb.floors_bar, g, {"hashes": {"before": {"scatter_classes": cls}}}, "L1")
        self.assertFalse(g.ok, "near cards are required at L1")

    def test_config_and_sg_mismatch_fail(self):
        def opt(cfg, sg):
            return {"options": {"scatter": {"config_fnv": cfg, "level": "L0", "meshes": {"GrassT0": {"path": "/a"}}, "sg": {"sg.ShadowQuality": sg}}}}
        base, poisoned, sg_bad = opt("0x1", "3"), opt("0x2", "3"), opt("0x1", "2")
        g = pt.Gates()
        quiet(sb.scatter_cfg_equal, g, "cfg", [("a", base), ("b", base)])
        self.assertTrue(g.ok)
        g = pt.Gates()
        quiet(sb.scatter_cfg_equal, g, "cfg", [("a", base), ("b", poisoned)])
        self.assertFalse(g.ok)
        self.assertIn("config mismatch", g.rows[0]["value"])
        g = pt.Gates()
        quiet(sb.sg_equal, g, "sg", [("a", base), ("b", sg_bad)])
        self.assertFalse(g.ok)
        self.assertIn("sg mismatch", g.rows[0]["value"])
        g = pt.Gates()
        quiet(sb.sg_equal, g, "sg", [("a", base), ("b", poisoned)])
        self.assertTrue(g.ok)

    def thinx_run(self, d, checks):
        write_run(d, {"start": ("0x1", "0x2")}, ("0x1", "0x2"))
        p = os.path.join(d, "results.json")
        res = json.load(open(p, encoding="utf-8"))
        res["scatter"] = {"available": True, "enabled": True, "checks": checks, "verifies": [], "counters": {"dispatched": 1, "applied": 1}, "in_flight_now": 0, "busy_now": 0}
        json.dump(res, open(p, "w", encoding="utf-8"))

    def test_thinx_no_samples_and_ratio(self):
        def row(cg, cgp, tu=1, tup=40, fl=2, flp=60, tr=0, trp=10, rock_band=9):
            return {"Grass": {"core_live": cg, "core_pure": cgp, "band_live": 5, "band_pure": 10},
                    "Tussock": {"core_live": tu, "core_pure": tup, "band_live": 0, "band_pure": 0},
                    "Flower": {"core_live": fl, "core_pure": flp, "band_live": 0, "band_pure": 0},
                    "Tree": {"core_live": tr, "core_pure": trp, "band_live": 0, "band_pure": 0},
                    "Rock": {"core_live": 0, "core_pure": 0, "band_live": rock_band, "band_pure": 0}}

        def mk():
            return {st: {"name": st, "set": st, "classes": row(5, 400)} for st in ("dirt", "rock", "snow", "path")}

        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "ok")
            self.thinx_run(d, list(mk().values()))
            rows = {r["bar"]: r for r in quiet(sb.thinx, d).rows}
            self.assertTrue(rows["SX6 dirt Grass"]["pass"], rows["SX6 dirt Grass"])
            self.assertTrue(rows["SX6 path Tree"]["pass"])
            self.assertTrue(rows["SX6 rock positive control"]["pass"])
            # a 0/0 row is "no samples" and fails, however good the ratio looks
            d = os.path.join(t, "zero")
            c = mk()
            c["dirt"] = {"name": "dirt", "set": "dirt", "classes": row(0, 0, 0, 0, 0, 0)}
            self.thinx_run(d, list(c.values()))
            g = quiet(sb.thinx, d)
            rows = {r["bar"]: r for r in g.rows}
            self.assertFalse(g.ok)
            for cl in ("Grass", "Tussock", "Flower"):
                self.assertFalse(rows["SX6 dirt %s" % cl]["pass"])
                self.assertIn("no samples", rows["SX6 dirt %s" % cl]["value"])
            # a set above the ratio fails; trees in the path core fail
            d = os.path.join(t, "ratio")
            c = mk()
            c["snow"] = {"name": "snow", "set": "snow", "classes": row(100, 400)}
            self.thinx_run(d, list(c.values()))
            self.assertFalse(quiet(sb.thinx, d).ok)
            d = os.path.join(t, "tree")
            c = mk()
            c["path"] = {"name": "path", "set": "path", "classes": row(5, 400, tr=2, trp=10)}
            self.thinx_run(d, list(c.values()))
            rows = {r["bar"]: r for r in quiet(sb.thinx, d).rows}
            self.assertFalse(rows["SX6 path Tree"]["pass"])
            # no rocks in the edge band: the positive control fails
            d = os.path.join(t, "norock")
            c = mk()
            c["rock"] = {"name": "rock", "set": "rock", "classes": row(5, 400, rock_band=1)}
            self.thinx_run(d, list(c.values()))
            rows = {r["bar"]: r for r in quiet(sb.thinx, d).rows}
            self.assertFalse(rows["SX6 rock positive control"]["pass"])

    def test_latx_rows_by_diameter(self):
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "latx")
            write_run(d, {"end": ("0x1", "0x2")}, ("0x1", "0x2"))
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            strokes, lat = [], []
            for dia, ms in ((5, 40.0), (20, 120.0), (60, 400.0), (100, 800.0)):
                for k in range(20):
                    strokes.append({"d": dia, "ticks": 20, "ticks_applied": 20})
                    lat.append({"reason": "stroke_end", "id": len(lat), "frames": 3, "ms": ms + k})
            res["scatter"] = {"available": True, "enabled": True, "latency": [{"reason": "enable", "ms": 1600.0}] + lat, "verifies": [], "init_ms": 1600.0,
                              "counters": {"dispatched": 1, "applied": 1}, "in_flight_now": 0, "busy_now": 0}
            res["strokes"] = strokes
            json.dump(res, open(p, "w", encoding="utf-8"))
            g, summ = quiet(sb.latx, d)
            self.assertEqual(sorted(summ), [5, 20, 60, 100])
            self.assertEqual(summ[100]["n"], 20)
            self.assertAlmostEqual(summ[100]["p95_or_max_s"], 0.818, places=3)
            self.assertTrue([r for r in g.rows if r["bar"] == "SX12 pairing"][0]["pass"])
            res["strokes"] = strokes[:-1]
            json.dump(res, open(p, "w", encoding="utf-8"))
            g, _s = quiet(sb.latx, d)
            self.assertFalse([r for r in g.rows if r["bar"] == "SX12 pairing"][0]["pass"])

    def test_scatter_mask_runs_expand_in_imgdiff(self):
        import imgdiff
        fp = {"rts80": {"viewport": [64, 48], "m": {"runs": [[y, 5, 30] for y in range(10, 16)]}}}
        m = imgdiff.footprint_mask(fp, "rts80/m", (48, 64), close=0, shrink=0)
        self.assertEqual(int(m.sum()), 6 * 26)
        self.assertTrue(m[10, 5] and m[15, 30] and not m[9, 5] and not m[10, 31])
        # a mask from a larger director frame scales onto a smaller image
        fp = {"rts80": {"viewport": [128, 96], "m": {"runs": [[20, 10, 59]]}}}
        m2 = imgdiff.footprint_mask(fp, "rts80/m", (48, 64), close=0, shrink=0)
        self.assertTrue(m2.sum() > 0 and m2[10, 5] and m2[10, 29])

    def test_generated_scripts_match_the_generator(self):
        import gen_scatter_scripts as gs
        for name, s in gs.all_scripts().items():
            self.assertFalse(sorted({o["op"] for o in s["ops"]} - gs.KNOWN_OPS), name)
            path = os.path.join(gs.SCRIPTS, name + ".json")
            self.assertTrue(os.path.isfile(path), name)
            self.assertEqual(open(path, encoding="utf-8", newline="").read().replace("\r\n", "\n"), gs.render(s), name)
        s = gs.all_scripts()["S1X"]
        base = gs.load("S1")
        names = [o["op"] for o in s["ops"]]

        def keep(ops):
            return [(o["op"], o.get("mode"), o.get("layer"), o.get("d"), o.get("s"), json.dumps(o.get("path")), o.get("ticks")) for o in ops if o["op"] in ("stroke", "paint")]
        # S1's strokes and paints survive verbatim (same ticks, same paths): the terrain hashes equal s1_a's
        self.assertEqual(keep(base["ops"]), keep(s["ops"]))
        self.assertEqual([o["name"] for o in s["ops"] if o["op"] == "hash"][:3], ["before", "pre_last2", "after"])
        for need in ("scatter_dump", "scatter_check", "scatter_mask", "scatter_fresh", "scatter_view_counts", "scatter_verify", "scatter_counts"):
            self.assertIn(need, names)


    def sx16_run(self, d, on_rgb, off_rgb, scatter_fnv="0xabc", height="0x1"):
        """A synthetic S1X-shaped run: results with the redo and loaded hashes, x_rts80_full(_off) shots and the m_redo mask."""
        from PIL import Image
        os.makedirs(d, exist_ok=True)
        h = {"height_fnv": height, "splat_fnv": "0x2", "scatter_fnv": scatter_fnv, "scatter_live_fnv": scatter_fnv}
        res = {"completed": True, "ops_done": 1, "ops_total": 1, "exit_code": 0, "hashes": {"redo": h, "after": h, "loaded": h}, "scatter": {}}
        json.dump(res, open(os.path.join(d, "results.json"), "w"))
        open(os.path.join(d, "game.log"), "w").write("ok\n")
        w, hh = 128, 96
        on = np.zeros((hh, w, 3), dtype=np.uint8)
        off = np.zeros((hh, w, 3), dtype=np.uint8)
        on[:] = off_rgb
        off[:] = off_rgb
        on[20:70, 20:100] = on_rgb
        Image.fromarray(on).save(os.path.join(d, "x_rts80_full.png"))
        Image.fromarray(off).save(os.path.join(d, "x_rts80_full_off.png"))
        runs = [[y, 15, 105] for y in range(15, 75)]
        json.dump({"rts80": {"viewport": [w, hh], "m_redo": {"runs": runs}}}, open(os.path.join(d, "footprints.json"), "w"))
        return res

    def test_sx16_render_proof_green_vs_grey_default_material(self):
        with tempfile.TemporaryDirectory() as t:
            xref, good, grey = (os.path.join(t, n) for n in ("xref", "good", "grey"))
            self.sx16_run(xref, (30, 100, 25), (110, 120, 90))
            self.sx16_run(good, (32, 98, 27), (110, 120, 90))
            self.sx16_run(grey, (80, 80, 80), (110, 120, 90))
            for d, want in ((good, True), (grey, False)):
                g = pt.Gates()
                quiet(sb.sx16, g, d, pt.load_results(d), None, None, xref, False, xref)
                rows = {r["bar"]: r for r in g.rows}
                self.assertEqual(rows["SX16d render proof"]["pass"], want, rows["SX16d render proof"])
            # the generation path: equal heights need equal scatter hashes; differing heights are reported, not failed
            diff = os.path.join(t, "diff")
            self.sx16_run(diff, (32, 98, 27), (110, 120, 90), scatter_fnv="0xdef")
            g = pt.Gates()
            quiet(sb.sx16, g, diff, pt.load_results(diff), None, None, xref, False, xref)
            self.assertFalse({r["bar"]: r for r in g.rows}["SX16b generation"]["pass"])
            other = os.path.join(t, "other")
            self.sx16_run(other, (32, 98, 27), (110, 120, 90), scatter_fnv="0xdef", height="0x9")
            g = pt.Gates()
            quiet(sb.sx16, g, other, pt.load_results(other), None, None, xref, False, xref)
            self.assertTrue({r["bar"]: r for r in g.rows}["SX16b generation"]["pass"])
            # the load path is unconditional
            rl = pt.load_results(diff)
            g = pt.Gates()
            quiet(sb.sx16, g, other, pt.load_results(other), "x", rl, xref, False, xref)
            self.assertFalse({r["bar"]: r for r in g.rows}["SX16a load"]["pass"])

    def test_fresh_pair_held_to_the_bar_and_an_unusable_floor_fails(self):
        """SX18 (local_pair): the pair is held to P7's bar; the run's A/A floor must itself be within the bar, else the row fails 'floor unusable' (a transient
        caught in the A/A pair must never loosen the limit)."""
        from PIL import Image
        with tempfile.TemporaryDirectory() as t:
            rng = np.random.RandomState(3)
            w, hh = 160, 96
            base = rng.randint(60, 180, size=(hh, w, 3)).astype(np.uint8)

            def noisy(frac):
                im = base.copy()
                n = int(frac * w * hh)
                ys, xs = rng.randint(0, hh, n), rng.randint(0, w, n)
                im[ys, xs] = np.clip(im[ys, xs].astype(int) + 90, 0, 255)   # isolated bright pixels; the 3x3 blur spreads each over 9
                return Image.fromarray(im)
            os.makedirs(t, exist_ok=True)
            runs = [[y, 10, 150] for y in range(10, 86)]
            json.dump({"rts80": {"viewport": [w, hh], "m_after": {"runs": runs}}}, open(os.path.join(t, "footprints.json"), "w"))
            noisy(0.0).save(os.path.join(t, "xf_before.png"))
            noisy(0.0).save(os.path.join(t, "xf_after.png"))

            def row():
                g = pt.Gates()
                quiet(sb.fresh_bars, g, t, ("after",))
                return [r for r in g.rows if r["bar"] == "SX18 after"][0]
            # a repeatable floor (frozen temporal sequences) and an equal fresh twin pass
            noisy(0.0).save(os.path.join(t, "xf_before_aa.png"))
            noisy(0.0).save(os.path.join(t, "xf_after_fresh.png"))
            r = row()
            self.assertTrue(r["pass"], r)
            self.assertIn("A/A floor (xf_before.png vs xf_before_aa.png", r["value"])
            # the same quiet floor, a fresh twin over the bar: fails
            noisy(0.012).save(os.path.join(t, "xf_after_fresh.png"))
            self.assertFalse(row()["pass"])
            # a noisy floor above the bar never loosens the limit: the row fails 'floor unusable'
            noisy(0.02).save(os.path.join(t, "xf_before_aa.png"))
            r = row()
            self.assertFalse(r["pass"], r)
            self.assertIn("floor unusable", r["value"])

    def test_freeze_bar(self):
        """Every judged shot (and its fresh twin) was taken with the temporal sequences frozen; an unfrozen one, a missing one, or a build without the cvars
        fails."""
        tl = [{"index": 1, "op": "temporal_freeze"}, {"index": 2, "op": "shot", "name": "x_after"}, {"index": 3, "op": "temporal_freeze"},
              {"index": 4, "op": "shot", "name": "xf_after"}, {"index": 5, "op": "scatter_fresh", "name": "xf_after"}, {"index": 6, "op": "temporal_freeze"}]
        names = ["xf_after", "xf_after_fresh"]
        frozen_rb = {"r.Test.FreezeTemporalSequences": "1", "r.TemporalAA.Debug.OverrideTemporalIndex": "0"}
        on = {"op_index": 3, "value": 1, "available": True, "readback": frozen_rb}
        off = {"op_index": 6, "value": 0, "available": True, "readback": {"r.Test.FreezeTemporalSequences": "0", "r.TemporalAA.Debug.OverrideTemporalIndex": "-1"}}
        # a refused set (an ini or ExecCmds holds the cvar at a higher priority): the request says 1, the read-back says 0, so the shots were not frozen
        refused = dict(on, readback={"r.Test.FreezeTemporalSequences": "0", "r.TemporalAA.Debug.OverrideTemporalIndex": "0"})
        refused_index = dict(on, readback={"r.Test.FreezeTemporalSequences": "1", "r.TemporalAA.Debug.OverrideTemporalIndex": "-1"})
        no_readback = {k: v for k, v in on.items() if k != "readback"}
        cases = (([on, off], True), ([], False), ([dict(on, available=False), off], False), ([dict(on, op_index=5), off], False),
                 ([on, dict(off, op_index=4.5)], False), ([dict(on, op_index=0), off], True), ([refused, off], False), ([refused_index, off], False),
                 ([no_readback, off], False))
        for rows, ok in cases:
            g = pt.Gates()
            quiet(sb.freeze_bar, g, "run", {"timeline": tl, "temporal_freeze": rows}, "freeze", names)
            self.assertEqual(g.ok, ok, rows)
        g = pt.Gates()
        quiet(sb.freeze_bar, g, "run", {"timeline": tl, "temporal_freeze": [on, off]}, "freeze", names + ["xf_before"])
        self.assertFalse(g.ok, "a judged shot missing from the timeline fails")
        # freeze windows: two frozen windows are different windows; a second freeze request inside a window keeps it; a refused set is no window
        w = sb.freeze_window({"temporal_freeze": [on, off, dict(on, op_index=7), dict(on, op_index=9)]})
        self.assertEqual((w(2), w(4), w(6.5), w(8), w(10)), (None, 3, None, 7, 7))
        self.assertIsNone(sb.freeze_window({"temporal_freeze": [refused]})(4))

    def test_sx17f_one_window_and_state_restored(self):
        """--sx17f gates only the measurement's validity: every compared frame inside ONE frozen window (by read-back) and the state restored at undo/redo."""
        rb = {"r.Test.FreezeTemporalSequences": "1", "r.TemporalAA.Debug.OverrideTemporalIndex": "0"}
        names = sb.SX17F_WINDOW_SHOTS
        tl, i = [], 2
        for nm in names:
            if nm.endswith("_fresh"):
                tl.append({"index": i, "op": "scatter_fresh", "name": nm[:-len("_fresh")]})
            else:
                tl.append({"index": i, "op": "shot", "name": nm})
            i += 1
        h = {"height_fnv": "0x1", "splat_fnv": "0x2", "scatter_fnv": "0xa", "scatter_live_fnv": "0xa"}
        h2 = dict(h, height_fnv="0x3", scatter_fnv="0xb", scatter_live_fnv="0xb")
        base = {"script": "S1XF", "completed": True, "timeline": tl, "hashes": {"pre_last2": h, "undo": h, "after": h2, "redo": h2},
                "temporal_freeze": [{"op_index": 1, "value": 1, "available": True, "readback": rb}]}

        def rows(res):
            with tempfile.TemporaryDirectory() as t:
                d = os.path.join(t, "s1xf")
                os.makedirs(d)
                json.dump(res, open(os.path.join(d, "results.json"), "w"))
                g = pt.Gates()
                quiet(sb.sx17f_run, g, d)
                return {r["bar"].split(" ", 2)[2]: r for r in g.rows}
        r = rows(base)
        self.assertTrue(r["one_window"]["pass"] and r["state_restored"]["pass"] and r["completed"]["pass"], r)
        self.assertFalse(r["images"]["pass"], "no images: no samples")
        # an unfreeze/refreeze between the compared frames splits them over two windows
        split = dict(base, temporal_freeze=base["temporal_freeze"] + [{"op_index": 6, "value": 0, "available": True, "readback": {}},
                                                                      {"op_index": 7, "value": 1, "available": True, "readback": rb}])
        self.assertFalse(rows(split)["one_window"]["pass"])
        refused = dict(base, temporal_freeze=[{"op_index": 1, "value": 1, "available": True, "readback": dict(rb, **{"r.Test.FreezeTemporalSequences": "0"})}])
        self.assertFalse(rows(refused)["one_window"]["pass"])
        bad = dict(base, hashes=dict(base["hashes"], undo=dict(h, height_fnv="0x9")))
        self.assertFalse(rows(bad)["state_restored"]["pass"])

    def test_s5_round4_minors(self):
        """SX15's triangle row never passes above the cap; sg_equal reads sg the way --summary does; LATX leaves unfinished strokes out of the statistic."""
        sb.MESH_TRIS_CACHE["rows"] = {("L0", "GrassT0"): 30}
        try:
            ok, detail = sb.tris_verdict({"meshes_drawn": {"GrassT0": 100000}})
            self.assertFalse(ok)
            self.assertIn("above the cap", detail)
            self.assertIn("unknown", detail)
            ok, detail = sb.tris_verdict({"meshes_drawn": {"GrassT0": 1000}})
            self.assertTrue(ok, detail)
            ok, detail = sb.tris_verdict({"meshes_drawn": {"GrassT0": 1000, "Rogue": 5}})
            self.assertFalse(ok, "an incomplete triangle table is not a pass")
        finally:
            sb.MESH_TRIS_CACHE.clear()
        g = pt.Gates()
        quiet(sb.sg_equal, g, "sg", [("a", {"sg": {"sg.ShadowQuality": "3"}}), ("b", {"options": {"scatter": {"sg": {"sg.ShadowQuality": "3"}}}})])
        self.assertTrue(g.ok, "top-level sg and options.scatter.sg are the same reading")
        g = pt.Gates()
        quiet(sb.sg_equal, g, "sg", [("a", {"sg": {"sg.ShadowQuality": "3"}}), ("b", {"sg": {"sg.ShadowQuality": "2"}})])
        self.assertFalse(g.ok)
        with tempfile.TemporaryDirectory() as t:
            d = os.path.join(t, "latx")
            write_run(d, {"end": ("0x1", "0x2")}, ("0x1", "0x2"))
            p = os.path.join(d, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            strokes = [{"d": 5, "ticks": 20, "ticks_applied": 20} for _k in range(20)]
            lat = [{"reason": "stroke_end", "id": k, "frames": 3, "ms": 100.0 + k} for k in range(19)] + [{"reason": "stroke_end", "id": 19, "frames": 0, "ms": -1.0}]
            res["scatter"] = {"available": True, "enabled": True, "latency": lat, "verifies": [], "init_ms": 1.0, "counters": {"dispatched": 1, "applied": 1},
                              "in_flight_now": 0, "busy_now": 0}
            res["strokes"] = strokes
            json.dump(res, open(p, "w", encoding="utf-8"))
            g, summ = quiet(sb.latx, d)
            self.assertEqual((summ[5]["n"], summ[5]["n_finished"], summ[5]["unfinished"]), (20, 19, 1))
            self.assertAlmostEqual(summ[5]["max_s"], 0.118, places=3)
            self.assertIn("OVER", [r for r in g.rows if r["bar"] == "SX12 latency d5"][0]["value"])

    def test_pair_roles_and_xref_compare(self):
        """SX4 needs exactly one ScatterThreads=0 pair and one ScatterDuringStroke=0 pair; the packaged path compares config and sg.* with --xref too."""
        def o(**kw):
            d = {"config_fnv": "0x1", "level": "L0", "meshes": {"GrassT0": {"path": "/a"}}, "sg": {"sg.ShadowQuality": "3"}, "threads": 2, "during_stroke_ms": 150}
            d.update(kw)
            return {"options": {"scatter": d}}
        for pairs, ok in (([o(threads=0), o(during_stroke_ms=0)], True), ([o(during_stroke_ms=0)], False), ([o(threads=0), o(threads=0)], False),
                          ([o(threads=0), o()], False)):
            g = pt.Gates()
            quiet(sb.pair_roles_bar, g, pairs)
            self.assertEqual(g.ok, ok, pairs)
        with tempfile.TemporaryDirectory() as t:
            x = os.path.join(t, "s1x_final")
            os.makedirs(x)
            json.dump(o(), open(os.path.join(x, "results.json"), "w"))
            g = pt.Gates()
            quiet(sb.compared_equal, g, [("pkg", o())], x)
            self.assertTrue(g.ok, g.rows)
            json.dump(o(sg={"sg.ShadowQuality": "2"}), open(os.path.join(x, "results.json"), "w"))
            g = pt.Gates()
            quiet(sb.compared_equal, g, [("pkg", o())], x)
            self.assertFalse(g.ok)
            self.assertIn("sg mismatch", {r["bar"]: r for r in g.rows}["sg_equal"]["value"])
            json.dump(o(config_fnv="0x2"), open(os.path.join(x, "results.json"), "w"))
            g = pt.Gates()
            quiet(sb.compared_equal, g, [("pkg", o())], x)
            self.assertIn("config mismatch", {r["bar"]: r for r in g.rows}["scatter_config"]["value"])
            g = pt.Gates()
            quiet(sb.compared_equal, g, [("pkg", o())], os.path.join(t, "missing"))
            self.assertFalse(g.ok)

    def test_p7_statistic_counts_sparse_boundary_blocks(self):
        """The gate is P7's statistic as written: a noisy block holding 8 masked pixels counts (it fails here), while the reported 64-px variant skips it."""
        from PIL import Image
        with tempfile.TemporaryDirectory() as t:
            w, hh = 160, 96
            base = np.full((hh, w, 3), 120, dtype=np.uint8)
            Image.fromarray(base).save(os.path.join(t, "xf_before.png"))
            Image.fromarray(base).save(os.path.join(t, "xf_before_aa.png"))
            Image.fromarray(base).save(os.path.join(t, "xf_after.png"))
            bad = base.copy()
            bad[38:46, 148:154] = 160          # after the mask's close + 1-px erosion the block at x=144 holds 24 masked pixels, all of them changed
            Image.fromarray(bad).save(os.path.join(t, "xf_after_fresh.png"))
            runs = [[y, 10, 140] for y in range(10, 86)] + [[y, 148, 153] for y in range(38, 46)]
            json.dump({"rts80": {"viewport": [w, hh], "m_after": {"runs": runs}}}, open(os.path.join(t, "footprints.json"), "w"))
            mask = sb.fp_mask(t, "rts80/m_after", (hh, w))
            A, B = pt.imgdiff.load_luma(os.path.join(t, "xf_after.png")), pt.imgdiff.load_luma(os.path.join(t, "xf_after_fresh.png"))
            p7 = pt.imgdiff.local_stat(A, B, mask)
            mine = sb.local_stat_px(A, B, mask)
            self.assertAlmostEqual(p7["worst_block_mean_abs"], mine["worst_block_mean_abs"], places=12)
            self.assertAlmostEqual(p7["changed_frac"], mine["changed_frac"], places=12)
            self.assertLess(sb.local_stat_px(A, B, mask, sb.REPORT_MIN_BLOCK_PX)["worst_block_mean_abs"], mine["worst_block_mean_abs"])
            g = pt.Gates()
            quiet(sb.fresh_bars, g, t, ("after",))
            self.assertFalse([r for r in g.rows if r["bar"] == "SX18 after"][0]["pass"], [r for r in g.rows if r["bar"] == "SX18 after"][0])

    def test_compile_window_bar(self):
        """Per unit: a recreate after a unit's last partial update (an apply after its first proxy) and before the shot fails; one that a later apply follows,
        or on a unit with no partial update in the window, is harmless; a scatter_fresh resets the window."""
        kinds = ["first_create", "expected_rebuild", "refill", "engine_recreate", "pso_recreate", "compile_recreate", "recreate", "shader_propagation",
                 "asset_post_compile", "compile_busy_frame", "apply"]
        A, FC, CR, EX, SP = 10, 0, 5, 1, 7
        tl = [{"index": 1, "op": "shot", "name": "x_pre_last2", "frame_start": 10, "frame_end": 20},
              {"index": 2, "op": "stroke", "frame_start": 21, "frame_end": 60},
              {"index": 4, "op": "shot", "name": "x_after", "frame_start": 210, "frame_end": 280},
              {"index": 5, "op": "scatter_fresh", "name": "x_after", "frame_start": 280, "frame_end": 500},
              {"index": 6, "op": "undo", "frame_start": 501, "frame_end": 502},
              {"index": 7, "op": "shot", "name": "x_undo", "frame_start": 600, "frame_end": 660}]
        res = {"timeline": tl}
        base = [[2, A, 7], [3, FC, 7], [2, A, 8], [3, FC, 8]]       # units 7 and 8 filled, first proxies at frame 3
        cases = (
            ([[30, A, 7], [100, SP, -1]], True),                     # a partial update, no recreate
            ([[30, A, 7], [150, CR, 7]], False),                     # compile recreate after unit 7's last partial update: hidden
            ([[30, A, 7], [40, CR, 7], [50, A, 7]], True),           # a later partial update follows the recreate
            ([[30, A, 7], [150, CR, 8]], True),                      # unit 8 had no partial update: harmless
            ([[30, A, 7], [150, CR, 7], [520, A, 7]], True),         # x_after's window: hidden (checked below); x_undo's window starts after the fresh
        )
        for extra, ok in cases[:4]:
            sc = {"proxy_event_kinds": kinds, "proxy_events": base + extra}
            g = pt.Gates()
            quiet(sb.window_bar, g, res, sc, "x_after", "w after")
            self.assertEqual(g.ok, ok, (extra, g.rows))
        sc = {"proxy_event_kinds": kinds, "proxy_events": base + cases[4][0]}
        g = pt.Gates()
        quiet(sb.window_bar, g, res, sc, "x_undo", "w undo")
        self.assertTrue(g.ok, g.rows)
        # an expected rebuild after the undo's partial update, before the shot, fails
        sc = {"proxy_event_kinds": kinds, "proxy_events": base + [[520, A, 7], [550, EX, 7]]}
        g = pt.Gates()
        quiet(sb.window_bar, g, res, sc, "x_undo", "w undo")
        self.assertFalse(g.ok)
        g = pt.Gates()
        quiet(sb.window_bar, g, {"timeline": []}, sc, "x_undo", "w undo")
        self.assertIn("no samples", g.rows[-1]["value"])

    def test_dump_pending_fails_and_dump_diff(self):
        with tempfile.TemporaryDirectory() as t:
            a, b = os.path.join(t, "a"), os.path.join(t, "b")
            os.makedirs(a)
            os.makedirs(b)
            r1 = make_rec(0, 0, 65536, 65536, 0, 0, 0, ix=1, iy=1)
            r2 = make_rec(0, 0, 2 * 65536, 65536, 0, 0, 0, ix=2, iy=1)
            r2b = make_rec(0, 0, 2 * 65536, 65536, 655, 0, 0, ix=2, iy=1)
            np.array([r1, r2], dtype=sb.REC_DTYPE).tofile(os.path.join(a, "scatter_end.bin"))
            np.array([r2, r1], dtype=sb.REC_DTYPE).tofile(os.path.join(b, "scatter_end.bin"))
            self.assertEqual(quiet(sb.dump_diff, a, b, "end"), 0, "order does not matter: diffed by key")
            np.array([r1, r2b], dtype=sb.REC_DTYPE).tofile(os.path.join(b, "scatter_end.bin"))
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(sb.dump_diff(a, b, "end"), 1)
            self.assertIn("'zq': 1", out.getvalue())
            # a dump taken with work pending fails its bar
            g = pt.Gates()
            res = {"options": {"scatter": {"palette": PAL}}, "scatter": {"dumps": [{"name": "end", "pending": True}]}}
            quiet(sb.dump_bar, g, a, res, "end", "oracle end")
            self.assertTrue(any(r["bar"] == "oracle end_settled" and not r["pass"] for r in g.rows))

    def test_summary_report_rows_sx11_scopes_and_sg_pairs(self):
        added = []

        def add(metric, ok, detail, measured_only=False, any_measured=True, report=False):
            added.append((metric, ok, detail, measured_only, report))
        fr = [[i, 1, 0, 0.1, 0.0, 1 if i % 2 else 0, 0, 0, 0, 0] for i in range(1000)] + [[2000, 1, 0, 2.5, 0.0, 1, 0, 0, 0, 0]]
        sc = {"frames": fr, "phase_names": ["default", "walk"]}
        scopes = sb.sx11_scopes(sc)
        self.assertEqual(len(scopes["walk"]), 1001)
        self.assertEqual(len(scopes["flush_windows"]), 501)
        self.assertIsNone(sb.sx11_scopes({"frames": fr}))
        a = {"tag": "c1s", "res": {"sg": {"sg.ShadowQuality": "3"}}}
        b = {"tag": "c1", "res": {"sg": {"sg.ShadowQuality": "2"}}}
        self.assertFalse(sb.sg_pair(add, a, b, "SX11"))
        self.assertIn("sg mismatch", added[-1][2])
        b["res"]["sg"]["sg.ShadowQuality"] = "3"
        self.assertTrue(sb.sg_pair(add, a, b, "SX11"))
        self.assertFalse(sb.sg_pair(add, a, {"tag": "old", "res": {}}, "SX11"))
        self.assertIn("no samples", added[-1][2])
        # report-only rows print REPORTED and never count as a gate failure
        g_out = io.StringIO()
        with contextlib.redirect_stdout(g_out):
            with tempfile.TemporaryDirectory() as t:
                d = os.path.join(t, "lat")
                write_run(d, {"x": ("0x1", "0x2")}, ("0x1", "0x2"))
                pt.summary([d], None)
        self.assertNotIn("FAIL     SX15", g_out.getvalue())


if __name__ == "__main__":
    unittest.main(verbosity=2)
