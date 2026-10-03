#!/usr/bin/env python3
"""test_check_shots.py - synthetic self-test of check_shots.py (A11).

Builds small verify.json files and shot/hidden PNG pairs in a temporary folder (or --out DIR) and asserts each verdict:
  good            a clean pair and a clean verify: pass
  mirrored        alpha drawn on the left: fails 'mirror'
  noisy           changed pixels outside both army boxes: fails 'outside'
  crossed         armies crossed in world X and drawn on the matching sides: pass
  swapped_axis    screen x rising with world X (a mirrored axis): fails 'mirror'
  inbox_noise     changed pixels inside the army box but away from every unit: passes 'outside', fails 'outside_units'
  ghost_instance  drawn_instances > expected_visible (an instance no map reaches): fails verify
  building_hidden an alive building without a drawn instance: fails verify
  ai_buildings    --min-ai-buildings 1 with no AI-built slot: fails; with one drawn AI-built slot: passes
Exit 0 and 'SELFTEST OK n/n' when every case gives its expected verdict.
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

TOOL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check_shots.py")
W, H = 960, 540
rng = np.random.default_rng(1)


def make(d, name, alpha_x, beta_x, noise=0, world=None, inbox_noise=0):
    base = rng.integers(60, 120, size=(H, W, 3)).astype(np.uint8)
    shot = base.copy()
    pts = {"1": [], "2": []}
    boxes = {"1": [], "2": []}
    for key, cx in (("1", alpha_x), ("2", beta_x)):
        for i in range(100):
            x = int(cx + (rng.integers(-60, -20) if (inbox_noise and key == "1") else rng.integers(-60, 60)))
            y = int(H // 2 + rng.integers(-80, 80))
            shot[y - 4:y + 5, x - 3:x + 4] = (200, 30, 30) if key == "1" else (30, 30, 200)
            wx = None if world is None else world(key, x)
            pts[key].append([float(x), float(y), i, 1] + ([] if wx is None else [float(wx)]))
            boxes[key].append([x - 4, y - 5, x + 4, y + 5])
    if noise:  # far left strip, outside both army boxes
        shot[rng.integers(0, H, noise), rng.integers(0, 100, noise)] = 255
    if inbox_noise:  # alpha's units stand in the left part of its box; changed pixels in the right part, away from every unit
        shot[H // 2 - 80:H // 2 + 80:4, int(alpha_x) + 10:int(alpha_x) + 65:4] = 255
    a = os.path.join(d, name + ".png")
    b = os.path.join(d, name + "_hidden.png")
    Image.fromarray(shot).save(a)
    Image.fromarray(base).save(b)
    arm = {}
    for key, cx in (("1", alpha_x), ("2", beta_x)):
        arm[key] = {"name": "alpha" if key == "1" else "beta", "points": pts[key], "unit_boxes": boxes[key],
                    "box": [int(cx) - 70, H // 2 - 90, int(cx) + 70, H // 2 + 90]}
    return {"name": name, "tick": 0, "camera": "wide", "png": a, "hidden_png": b, "projection": {"armies": arm}}


def verify_row(shots=(), **over):
    v = {"tick": 0, "rows": 200, "alive": 200, "expected_visible": 200, "visible": 200, "drawn_instances": 200, "dead_visible": 0,
         "alive_hidden": 0, "group_mismatch": 0, "max_err_cm": 0.0, "buildings_alive": 2, "buildings_visible": 2,
         "building_drawn_instances": 2, "building_dead_visible": 0, "building_alive_hidden": 0, "building_group_mismatch": 0,
         "building_max_err_cm": 0.0, "ai_created_alive": 0, "ai_created_visible": 0, "pass": True, "shots": list(shots)}
    v.update(over)
    return v


def run(d, rows, extra=()):
    doc = {"leg": "selftest", "verify_ticks": [r["tick"] for r in rows], "verifies": rows}
    p = os.path.join(d, "verify.json")
    with open(p, "w", encoding="utf-8") as f:
        json.dump(doc, f)
    r = subprocess.run([sys.executable, TOOL, p, "--min-bytes", "0", "--width", str(W), "--height", str(H), "--dilate", "6", *extra],
                       capture_output=True, text=True)
    return r.returncode, r.stdout + r.stderr


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="", help="keep the synthetic files here (default: a temporary folder)")
    args = ap.parse_args()
    d = args.out or tempfile.mkdtemp(prefix="check_shots_selftest_")
    os.makedirs(d, exist_ok=True)
    cases = []

    def case(name, rc_want, must=None, rows=None, extra=()):
        rc, out = run(d, rows, extra)
        ok = rc == rc_want and (must is None or must in out)
        cases.append((name, ok))
        print(f"{'ok  ' if ok else 'FAIL'} {name}: rc={rc} (want {rc_want}{', with ' + repr(must) if must else ''})")
        if not ok:
            print(out)

    case("good", 0, rows=[verify_row([make(d, "good", 650, 300)])])
    case("mirrored", 1, "mirror", rows=[verify_row([make(d, "mirrored", 300, 650)])])
    case("noisy", 1, "outside fraction", rows=[verify_row([make(d, "noisy", 650, 300, noise=20000)])])
    case("crossed", 0, rows=[verify_row([make(d, "crossed", 300, 650, world=lambda k, x: (480 - x) * 10)])])
    case("swapped_axis", 1, "mirror", rows=[verify_row([make(d, "swapped", 650, 300, world=lambda k, x: (x - 480) * 10)])])
    case("inbox_noise", 1, "outside_units fraction", rows=[verify_row([make(d, "inbox", 650, 300, inbox_noise=1)])])
    case("ghost_instance", 1, "FAIL", rows=[verify_row(drawn_instances=201)])
    case("building_hidden", 1, "FAIL", rows=[verify_row(buildings_visible=1, building_alive_hidden=1, building_drawn_instances=1)])
    case("ai_buildings_none", 1, "ai buildings", rows=[verify_row()], extra=("--min-ai-buildings", "1"))
    case("ai_buildings_one", 0, rows=[verify_row(buildings_alive=3, buildings_visible=3, building_drawn_instances=3, ai_created_alive=1,
                                                ai_created_visible=1)], extra=("--min-ai-buildings", "1"))
    n_ok = sum(1 for _, ok in cases if ok)
    print(f"SELFTEST {'OK' if n_ok == len(cases) else 'FAIL'} {n_ok}/{len(cases)} (files in {d})")
    return 0 if n_ok == len(cases) else 1


if __name__ == "__main__":
    sys.exit(main())
