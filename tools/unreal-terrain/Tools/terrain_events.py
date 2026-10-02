#!/usr/bin/env python3
"""Check results.json "terrain_events" of a run (plan C scatter S4a): python T/Tools/terrain_events.py RUN_DIR [--loads N]

The expected counts follow from the run's own record: Init = 1, Tick = the sum of every stroke's ticks_applied, StrokeEnd = the number of
strokes, Undo / Redo = --undos / --redos (the scripted undo and redo counts; S1: 2 and 2),
Load = --loads (default 0). Exit 0 PASS, 1 FAIL, 2 no results.json."""
import argparse
import json
import os
import sys

KINDS = ("init", "tick", "stroke_end", "undo", "redo", "load")


def expected(res, undos, redos, loads):
    strokes = res.get("strokes") or []
    return {
        "init": 1,
        "tick": sum(int(s.get("ticks_applied", 0)) for s in strokes),
        "stroke_end": len(strokes),
        "undo": undos,
        "redo": redos,
        "load": loads,
    }


def check(res, undos=2, redos=2, loads=0):
    """Returns (ok, rows) with rows = [(kind, got, want, ok)]. A missing terrain_events block fails every kind (never passes vacuously)."""
    ev = res.get("terrain_events")
    want = expected(res, undos, redos, loads)
    rows = []
    for k in KINDS:
        got = ev.get(k) if isinstance(ev, dict) else None
        rows.append((k, got, want[k], got is not None and int(got) == want[k]))
    return all(r[3] for r in rows), rows


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("run")
    ap.add_argument("--undos", type=int, default=2)
    ap.add_argument("--redos", type=int, default=2)
    ap.add_argument("--loads", type=int, default=0)
    a = ap.parse_args(argv)
    p = os.path.join(a.run, "results.json")
    if not os.path.isfile(p):
        print("no results.json in %s" % a.run)
        return 2
    with open(p, encoding="utf-8") as f:
        res = json.load(f)
    ok, rows = check(res, a.undos, a.redos, a.loads)
    for k, got, want, good in rows:
        print("  %-10s got=%s want=%s %s" % (k, got, want, "PASS" if good else "FAIL"))
    print("TERRAIN-EVENTS %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
