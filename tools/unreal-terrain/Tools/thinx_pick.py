#!/usr/bin/env python3
"""thinx_pick.py - chooses THINX's meadow coordinates (plan-c-scatter.md 3.8: "fixed in S5 from the field map S2 prints") from a scatter_dump of the unpainted
flat map (an SXPROBE run: T/Out/<run>/scatter_start.bin).

Usage: python thinx_pick.py RUN_DIR [--margin 2.0] [--json]
For every candidate disc centre (2 m grid, inside +-130 m) it counts the pure-grass yield of the disc's core (inner half radius) per class from the dump, and
picks, with the SX6 minimum samples times --margin (grass 200, tussock 10, flower 20, tree 5), open-meadow discs far from each other (>= 60 m), and a 100 m
path whose core strip (|dy| <= 2.5 m) holds the most trees. The result is pasted into gen_scatter_scripts.py THINX (the generator does not read this file:
the choice is a recorded constant, and the SX6 minimum-sample rule still guards it on every run).
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import scatter_bars as sb  # noqa: E402

MIN = {"Grass": 200, "Tussock": 10, "Flower": 20, "Tree": 5}


def grids(rec, e):
    """1 m count grids per class over [-e, e)^2 (index [y, x])."""
    n = 2 * e
    out = {}
    for ci, name in enumerate(sb.CLASSES):
        sel = rec["cls"] == ci
        x = rec["xq"][sel] / 65536.0 + e
        y = rec["yq"][sel] / 65536.0 + e
        h, _, _ = np.histogram2d(y, x, bins=[n, n], range=[[0, n], [0, n]])
        out[name] = h
    return out


def disc_counts(grid, radius_m):
    """Count of grid cells within radius of every cell centre, by FFT convolution with the disc kernel."""
    n = grid.shape[0]
    r = int(np.ceil(radius_m))
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    kern = ((xx ** 2 + yy ** 2) <= radius_m ** 2).astype(float)
    big = np.zeros((n + 2 * r, n + 2 * r))
    kb = np.zeros_like(big)
    big[r:r + n, r:r + n] = grid
    kb[:2 * r + 1, :2 * r + 1] = kern
    conv = np.fft.irfft2(np.fft.rfft2(big) * np.fft.rfft2(kb), s=big.shape)
    # conv[i + 2r, j + 2r] is the sum over the window centred at (i, j) of the padded grid
    return np.rint(conv[2 * r:2 * r + n, 2 * r:2 * r + n]).astype(int)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    run = sys.argv[1]
    margin = float(sys.argv[sys.argv.index("--margin") + 1]) if "--margin" in sys.argv else 2.0
    res = json.load(open(os.path.join(run, "results.json"), encoding="utf-8"))
    rec = sb.read_dump(os.path.join(run, "scatter_start.bin"))
    e = int(json.load(open(os.path.join(run, "terrain.json")))["half_extent_m"]) if os.path.isfile(os.path.join(run, "terrain.json")) else 160
    gr = grids(rec, e)
    print("classes in the dump:", {k: int(v.sum()) for k, v in gr.items()})
    # candidate discs: core radius R/2 for d20 (R = 10 -> 5) and d24 (R = 12 -> 6). Each core must hold SX6's minimum yield times a margin (grass 1.1, tussock
    # 1.2, flower 1.5: a d20 core tops out near 264 grass) and must be tree-free. Only the CORE is required to be tree-free. tree_out (trees and saplings within
    # R + 3 m; a tree in a cell vetoes its rock) is computed and kept for inspection but is NOT a selection rule: the recorded rock disc (55, -45) d48 has 18
    # within R + 3 m, and THINX's positive control (>= 5 rocks in the edge band, SX6) is what guards the rock disc on every run (23 rocks in S5 round 3).
    margins = {"Grass": 1.1, "Tussock": 1.2, "Flower": 1.5}
    out = {}
    for label, d in (("d20", 20), ("d24", 24), ("d48", 48)):
        core = d / 4.0
        outer = d / 2.0 + 3.0
        cnt = {c: disc_counts(gr[c], core) for c in ("Grass", "Tussock", "Flower", "Tree")}
        tree_out = disc_counts(gr["Tree"], outer) + disc_counts(gr["Sapling"], outer)
        ok = np.ones_like(cnt["Grass"], dtype=bool)
        for c in ("Grass", "Tussock", "Flower"):
            ok &= cnt[c] >= MIN[c] * margins[c]
        ok &= cnt["Tree"] == 0
        lim = 130
        ys, xs = np.nonzero(ok)
        keep = [(int(x) - e, int(y) - e) for x, y in zip(xs, ys) if abs(int(x) - e) <= lim and abs(int(y) - e) <= lim]
        print("%s: %d candidate centres (core r=%.1f m tree-free, yields >= %s of the minimum)" % (label, len(keep), core, margins))

        def score(p, cnt=cnt):
            xi, yi = p[0] + e, p[1] + e
            return min(cnt[c][yi, xi] / float(MIN[c]) for c in ("Grass", "Tussock", "Flower"))
        keep.sort(key=score, reverse=True)
        out[label] = (keep, cnt, score, tree_out)
    chosen = []

    def far(p, q, d=60.0):
        return (p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 >= d * d
    picks = {}
    k20, c20, s20, _t20 = out["d20"]
    k24, c24, s24, t24 = out["d24"]
    k48, c48, s48, t48 = out["d48"]
    dirt = []
    for p in k20:
        if all(far(p, q) for q in chosen):
            dirt.append(p)
            chosen.append(p)
        if len(dirt) == 2:
            break
    # rock: d48 (core 12 m), because a rock stroke's band (wR passing 48..176) is the soft outer quarter of the brush: a wide disc gives it area for the cells
    rock = next((p for p in k48 if all(far(p, q, 80.0) for q in chosen)), None)
    if rock:
        chosen.append(rock)
    snow = next((p for p in k24 if all(far(p, q, 70.0) for q in chosen)), None)
    if snow:
        chosen.append(snow)
    picks["dirt"] = dirt
    picks["rock"] = rock
    picks["snow"] = snow
    for name, pts, cnts, sc in (("dirt", dirt, c20, s20), ("rock", [rock] if rock else [], c48, s48), ("snow", [snow] if snow else [], c24, s24)):
        for p in pts:
            xi, yi = p[0] + e, p[1] + e
            print("%-5s at (%4d, %4d): core pure-grass yield grass %d tussock %d flower %d tree %d (minimum %s)" % (
                name, p[0], p[1], cnts["Grass"][yi, xi], cnts["Tussock"][yi, xi], cnts["Flower"][yi, xi], cnts["Tree"][yi, xi], MIN))
    # the path: a horizontal 100 m segment whose core strip (|dy| <= 2.5) holds the most trees, kept 40 m from every chosen disc
    tr = gr["Tree"]
    best = None
    for yi in range(10, 2 * e - 10):
        for x0 in range(10, 2 * e - 110, 4):
            xs0, xs1 = x0, x0 + 100
            y_m = yi - e
            if abs(y_m) > 135 or abs(xs0 - e) > 135 or abs(xs1 - e) > 135:
                continue
            n_tr = tr[max(0, yi - 2):yi + 3, xs0:xs1].sum()
            if best is None or n_tr > best[0]:
                if all(((xs0 - e + 50) - q[0]) ** 2 + (y_m - q[1]) ** 2 >= 45.0 ** 2 for q in chosen):
                    best = (int(n_tr), xs0 - e, xs1 - e, y_m)
    if best:
        print("path: y = %d from x = %d to %d (100 m): %d trees in the core strip |dy| <= 2.5 m (minimum %d)" % (best[3], best[1], best[2], best[0], MIN["Tree"]))
        mid = (best[1] + best[2]) / 2.0
        picks["path"] = [[best[1], best[3]], [best[2], best[3]]]
        picks["tree_near"] = [mid, best[3]]
        picks["tree_count"] = best[0]
    print("THINX = " + json.dumps({"dirt": [list(map(float, p)) for p in dirt], "rock": [list(map(float, rock))] if rock else None, "snow": [list(map(float, snow))] if snow else None,
                                  "path": [[float(a), float(b)] for a, b in (picks.get("path") or [])], "flower_near": [list(map(float, dirt[0]))] if dirt else None,
                                  "tree_near": [float(v) for v in (picks.get("tree_near") or [])]}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
