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

    def test_p5_never_passes_without_collision_bars(self):
        h = {n: ("0x%08x" % i, "0x%08x" % (i + 100)) for i, n in enumerate(("pre_last2", "after", "undo", "redo"))}
        with tempfile.TemporaryDirectory() as t:
            ran = os.path.join(t, "ran")
            write_run(ran, h, ("0x1", "0x2"))
            g, _res, ok = quiet(pt.s1, ran, True)
            p5 = [r for r in g.rows if r["bar"] == "P5_collision"]
            self.assertEqual(len(p5), 1)
            self.assertEqual(p5[0]["status"], "FAIL", "collision ops that ran must not pass P5 vacuously")
            self.assertIn("not evaluated", p5[0]["value"])
            self.assertFalse(ok, "--expect-skipped must not excuse P5 when nothing was skipped")
            sk = os.path.join(t, "sk")
            write_run(sk, h, ("0x1", "0x2"))
            p = os.path.join(sk, "results.json")
            res = json.load(open(p, encoding="utf-8"))
            res["skipped"] = [{"op": "wait_collision", "why": "skipped:C5"}]
            json.dump(res, open(p, "w", encoding="utf-8"))
            g, _res, _ok = quiet(pt.s1, sk, True)
            self.assertEqual([r["status"] for r in g.rows if r["bar"] == "P5_collision"], ["FAIL"])

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


if __name__ == "__main__":
    unittest.main(verbosity=2)
