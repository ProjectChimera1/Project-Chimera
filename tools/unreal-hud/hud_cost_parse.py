#!/usr/bin/env python3
"""hud_cost_parse.py - summarise hud_cost.ps1's CSV profiler captures into the HUD-cost line and JSON (plan B T8).

    python hud_cost_parse.py --out results/hud_cost.json --lock-kind measure --measured off=<csv> ... on=<csv> ...

Reads each capture with tools/unreal-looktest/parse_csv.py's read_capture, drops the first frames (settle: 300, or a
quarter of a short capture), and takes per run the median FrameTime, GameThreadTime, RenderThreadTime and GPUTime (ms).
Across the reps of one mode it reports min and median of those per-run medians. Prints
    HUD cost: frame ms off/on min a/b median c/d; game-thread ms off/on min .. median ..; render-thread ms off/on ...; gpu ms ...;
    median frame delta +x ms [FLAG: above 0.5 ms] [UNMEASURED]
No gate (plan B T8): a median frame delta above 0.5 ms is flagged. Exit 1 on an unreadable or incomplete capture.
"""
import argparse
import json
import os
import statistics
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "unreal-looktest"))
from parse_csv import read_capture  # noqa: E402

SERIES = (("frame", "FrameTime"), ("game_thread", "GameThreadTime"), ("render_thread", "RenderThreadTime"), ("gpu", "GPUTime"))
FLAG_MS = 0.5


def run_summary(path):
    names, cols, meta, complete = read_capture(path)
    n = len(cols["FrameTime"])
    skip = min(300, n // 4)
    out = {"csv": path.replace("\\", "/"), "frames": n, "skipped": skip, "complete": bool(complete)}
    for key, col in SERIES:
        vals = [v for v in cols[col][skip:] if v is not None]
        out[key + "_median_ms"] = round(statistics.median(vals), 4) if vals else None
    ft = sorted(cols["FrameTime"][skip:])
    out["frame_p99_ms"] = round(ft[max(0, int(0.99 * len(ft)) - 1)], 4)
    out["frame_max_ms"] = round(ft[-1], 4)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--lock-kind", default="none")
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--measured", action="store_true")
    g.add_argument("--unmeasured", action="store_true")
    ap.add_argument("runs", nargs="+", help="off=<csv> or on=<csv>")
    a = ap.parse_args()

    per = {"off": [], "on": []}
    for item in a.runs:
        mode, _, path = item.partition("=")
        if mode not in per or not path:
            print(f"bad run argument {item!r}")
            return 2
        try:
            s = run_summary(path)
        except (OSError, ValueError) as e:
            print(f"HUD COST FAIL {e}")
            return 1
        if not s["complete"]:
            print(f"HUD COST FAIL {path}: capture has no final summary header (run did not finish)")
            return 1
        per[mode].append(s)
    if not per["off"] or not per["on"]:
        print("HUD COST FAIL need at least one off and one on run")
        return 2

    agg = {}
    for mode, rows in per.items():
        agg[mode] = {}
        for key, _ in SERIES:
            vals = [r[key + "_median_ms"] for r in rows if r[key + "_median_ms"] is not None]
            agg[mode][key] = {"min": round(min(vals), 4), "median": round(statistics.median(vals), 4)} if vals else None
    delta = round(agg["on"]["frame"]["median"] - agg["off"]["frame"]["median"], 4)
    flagged = delta > FLAG_MS

    def pair(key):
        o, n = agg["off"][key], agg["on"][key]
        if not o or not n:
            return f"{key} n/a"
        return f"min {o['min']:.3f}/{n['min']:.3f} median {o['median']:.3f}/{n['median']:.3f}"

    line = (f"HUD cost: frame ms off/on {pair('frame')}; game-thread ms off/on {pair('game_thread')}; "
            f"render-thread ms off/on {pair('render_thread')}; gpu ms off/on {pair('gpu')}; median frame delta {delta:+.3f} ms"
            + (f" FLAG: above {FLAG_MS} ms" if flagged else "")
            + ("" if a.measured else " [UNMEASURED: not a Phase-4 --measure run]"))
    print(line)
    res = {
        "measured": bool(a.measured),
        "lock_kind": a.lock_kind,
        "reps": {"off": len(per["off"]), "on": len(per["on"])},
        "aggregate_ms": agg,
        "median_frame_delta_ms": delta,
        "flag_threshold_ms": FLAG_MS,
        "flagged": flagged,
        "runs": per,
        "line": line,
    }
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(res, f, indent=1)
    print(f"wrote {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
