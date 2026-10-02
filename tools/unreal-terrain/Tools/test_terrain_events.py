#!/usr/bin/env python3
"""Self-tests for terrain_events.py (S4a): python T/Tools/test_terrain_events.py (no Unreal needed)."""
import contextlib
import io
import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import terrain_events as te  # noqa: E402


def res(events, ticks=(40, 30, 5)):
    r = {"strokes": [{"ticks": t, "ticks_applied": t} for t in ticks]}
    if events is not None:
        r["terrain_events"] = events
    return r


GOOD = {"init": 1, "tick": 75, "stroke_end": 3, "undo": 2, "redo": 2, "load": 0}


class EventTests(unittest.TestCase):
    def test_expected_counts_pass(self):
        ok, rows = te.check(res(GOOD))
        self.assertTrue(ok, rows)

    def test_each_kind_off_by_one_fails(self):
        for k in te.KINDS:
            ev = dict(GOOD)
            ev[k] += 1
            ok, rows = te.check(res(ev))
            self.assertFalse(ok, k)
            self.assertEqual([r[0] for r in rows if not r[3]], [k])

    def test_missing_block_or_kind_fails(self):
        self.assertFalse(te.check(res(None))[0])
        ev = dict(GOOD)
        del ev["redo"]
        self.assertFalse(te.check(res(ev))[0])

    def test_loads_and_undo_counts_are_parameters(self):
        ev = dict(GOOD, load=1, undo=4)
        self.assertTrue(te.check(res(ev), undos=4, loads=1)[0])

    def test_main_reads_the_run_folder(self):
        with tempfile.TemporaryDirectory() as d:
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(te.main([d]), 2)
            with open(os.path.join(d, "results.json"), "w", encoding="utf-8") as f:
                json.dump(res(GOOD), f)
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(te.main([d]), 0)
            self.assertIn("TERRAIN-EVENTS PASS", out.getvalue())


if __name__ == "__main__":
    unittest.main()
