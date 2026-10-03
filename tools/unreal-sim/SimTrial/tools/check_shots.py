#!/usr/bin/env python3
"""check_shots.py - plan A 3.7 / 4 A11: does Unreal draw what the sim says?

Reads a run's verify.json (written by AChimeraSimDirector) and checks:

  verify   every verify tick (--ticks, default: the run's own verify_ticks) has pass=true: visible == alive (no dead id
           visible, no alive id hidden, every instance in the group of its faction and fresh definition id) and
           max_err_cm <= 0.5 (ISM instance transform vs 100 * map(pos) from a fresh chimera_read_units); drawn_instances
           (instances with a non-zero scale over every unit ISM, counted without the journal) == expected_visible; and the
           same for buildings from a fresh chimera_read_buildings: buildings_visible == buildings_alive, no dead slot drawn,
           building_drawn_instances == buildings_alive, building_max_err_cm <= 0.5, AI-built slots (added during the run)
           all drawn (ai_created_visible == ai_created_alive). --min-ai-buildings N also needs ai_created_alive >= N at
           one verify tick at or before --ai-by-tick (the ai variant builds 2 -> 7, plan A 3.3).
  shots    per shot pair (shot_tNNNN_<camera>.png vs ..._hidden.png, units hidden in the second):
             size     1920x1080 and the shot PNG > 300 KB
             armies   per army (alpha = faction 1, beta = 2) at least --min-points projected sample points on screen, and
                      >= 90% of them have a changed pixel (max channel delta > 24/255) inside a 5x5 window
             outside  <= 5% of all changed pixels fall outside the union of the army boxes (foot, top and shadow tip of
                      every alive unit, projected in-engine) dilated by --dilate px (plan A 3.7 as written)
             outside_units  the same bar against the union of per-unit footprint boxes (foot, top and shadow tip, each
                      widened by the mesh radius, projected in-engine; 'unit_boxes') dilated by --dilate px. Added in A11
                      round 3 because one box per army can cover most of the frame on close and late shots (box_cover
                      reports how much); it is stricter, never looser, than the army-box bar.
             mirror   yaw-90 cameras put world -X (alpha's side) on screen right. Plan A 3.7's rule "alpha's mean screen x >
                      beta's" holds only while alpha's sampled units stand at lower world X than beta's (true until the armies
                      cross; at tick 1440 the survivors have swapped sides). So, per pair: (a) the army order on screen must be
                      the mirror of the army order in world X (the plan's rule verbatim whenever its premise holds), and (b) the
                      screen x of every on-screen sample must fall as its world X rises (Pearson r <= --mirror-r) - a mirrored
                      or swapped axis fails both. World X comes from the fresh read, not from the renderer.
  log      (--log run.log) no Warning/Error lines after the "tick=900" line (the tick-900 mass deaths: zero-scale check).

Thresholds are plan A 3.7's first guesses; any change is recorded with the evidence. Writes a JSON report (--out) and,
with --diff-dir, one diff mask PNG per pair (changed pixels white, army boxes outlined). Exit 0 iff every check passes.
"""
import argparse
import json
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw


def load_rgb(path):
    with Image.open(path) as im:
        return np.asarray(im.convert("RGB"), dtype=np.int16)


def check_log(path, after_pat=r"LogChimeraSim: Display: tick=900 "):
    text = open(path, encoding="utf-8", errors="replace").read().splitlines()
    start = None
    for i, line in enumerate(text):
        if re.search(after_pat, line):
            start = i
            break
    if start is None:
        return {"pass": False, "reason": "no tick=900 line", "warnings_after": None}
    bad = [l for l in text[start + 1:] if re.search(r"\]\w+: (Warning|Error): ", l) or re.search(r"^\w+: (Warning|Error): ", l)]
    return {"pass": len(bad) == 0, "after_line": start + 1, "warnings_after": len(bad), "examples": bad[:10]}


def mirror_check(res, means, world_means, pts, args):
    """See the module docstring, 'mirror'. Appends failures to res['fail']; returns True when both parts hold."""
    m = {"alpha_mean_x": means.get("1"), "beta_mean_x": means.get("2"),
         "alpha_mean_world_x_cm": world_means.get("1"), "beta_mean_world_x_cm": world_means.get("2")}
    res["mirror"] = m
    if means.get("1") is None or means.get("2") is None:
        res["fail"].append("mirror: an army has no on-screen samples")
        return False
    wa, wb = world_means.get("1"), world_means.get("2")
    if wa is None or wb is None:
        premise = True  # no world data: the plan's rule as written
    else:
        premise = wa < wb
    m["premise_alpha_left_in_world"] = premise
    order_ok = (means["1"] > means["2"]) if premise else (means["1"] < means["2"])
    m["order_ok"] = order_ok
    m["rule"] = "alpha mean screen x > beta mean screen x" if premise else "armies crossed in world X: alpha mean screen x < beta mean screen x"
    if not order_ok:
        res["fail"].append(f"mirror: {m['rule']} fails (alpha {means['1']}, beta {means['2']}; world alpha {wa}, beta {wb})")
    corr_ok = True
    if pts and all(len(p) >= 5 for p in pts) and len(pts) >= 3:
        sx = np.array([p[0] for p in pts], dtype=float)
        wx = np.array([p[4] for p in pts], dtype=float)
        if sx.std() > 0 and wx.std() > 0:
            r = float(np.corrcoef(sx, wx)[0, 1])
            m["r_screen_x_world_x"] = round(r, 4)
            corr_ok = r <= args.mirror_r
            if not corr_ok:
                res["fail"].append(f"mirror: screen x vs world X correlation {r:.3f} > {args.mirror_r}")
    m["corr_ok"] = corr_ok
    return order_ok and corr_ok


def check_pair(shot, args, diff_dir):
    res = {"name": shot.get("name"), "tick": shot.get("tick"), "camera": shot.get("camera"), "pass": False, "fail": []}
    png, hid = shot.get("png"), shot.get("hidden_png")
    if not png or not hid or not os.path.isfile(png) or not os.path.isfile(hid):
        res["fail"].append("missing png or hidden png")
        return res
    a, b = load_rgb(png), load_rgb(hid)
    h, w = a.shape[:2]
    res["size"] = [w, h]
    res["png_bytes"] = os.path.getsize(png)
    if (w, h) != (args.width, args.height):
        res["fail"].append(f"size {w}x{h} != {args.width}x{args.height}")
    if res["png_bytes"] <= args.min_bytes:
        res["fail"].append(f"png {res['png_bytes']} bytes <= {args.min_bytes}")
    if b.shape != a.shape:
        res["fail"].append("shot and hidden differ in size")
        return res
    changed = np.abs(a - b).max(axis=2) > args.delta
    n_changed = int(changed.sum())
    res["changed_px"] = n_changed

    proj = shot.get("projection") or {}
    armies = proj.get("armies") or {}
    mask = np.zeros_like(changed)
    r = args.window // 2
    means = {}
    world_means = {}
    all_pts = []
    res["armies"] = {}
    for key in ("1", "2"):
        arm = armies.get(key)
        info = {"pass": False}
        res["armies"][key] = info
        if not arm:
            res["fail"].append(f"army {key}: no projection")
            continue
        info["name"] = arm.get("name")
        pts = [p for p in arm.get("points", []) if len(p) >= 4 and p[3] == 1]
        hits = 0
        for pt in pts:
            x, y = pt[0], pt[1]
            xi, yi = int(round(x)), int(round(y))
            win = changed[max(0, yi - r):min(h, yi + r + 1), max(0, xi - r):min(w, xi + r + 1)]
            hits += 1 if win.any() else 0
        info["on_screen"] = len(pts)
        info["hits"] = hits
        info["hit_frac"] = round(hits / len(pts), 4) if pts else 0.0
        info["mean_x"] = round(float(np.mean([p[0] for p in pts])), 1) if pts else None
        wx = [p[4] for p in pts if len(p) >= 5]
        info["mean_world_x_cm"] = round(float(np.mean(wx)), 1) if pts and len(wx) == len(pts) else None
        means[key] = info["mean_x"]
        world_means[key] = info["mean_world_x_cm"]
        all_pts.extend(pts)
        box = arm.get("box") or []
        if len(box) == 4:
            x0, y0, x1, y1 = box
            x0, y0 = max(0, int(x0) - args.dilate), max(0, int(y0) - args.dilate)
            x1, y1 = min(w, int(x1) + args.dilate + 1), min(h, int(y1) + args.dilate + 1)
            mask[y0:y1, x0:x1] = True
            info["box_dilated"] = [x0, y0, x1, y1]
        ok = len(pts) >= args.min_points and info["hit_frac"] >= args.min_frac
        info["pass"] = ok
        if not ok:
            res["fail"].append(f"army {key} ({info.get('name')}): {hits}/{len(pts)} points changed (need >= {args.min_points} on screen and >= {args.min_frac:.0%})")
    outside = int((changed & ~mask).sum())
    res["outside_px"] = outside
    res["outside_frac"] = round(outside / n_changed, 5) if n_changed else 1.0
    res["box_cover"] = round(float(mask.mean()), 4)
    if not n_changed or res["outside_frac"] > args.max_out:
        res["fail"].append(f"outside fraction {res['outside_frac']:.4f} > {args.max_out}")
    # Per-unit footprints (stricter than the army boxes).
    umask = np.zeros_like(changed)
    n_boxes = 0
    for key in ("1", "2"):
        for bx in (armies.get(key) or {}).get("unit_boxes", []) or []:
            if len(bx) != 4:
                continue
            x0, y0 = max(0, int(bx[0]) - args.dilate), max(0, int(bx[1]) - args.dilate)
            x1, y1 = min(w, int(bx[2]) + args.dilate + 1), min(h, int(bx[3]) + args.dilate + 1)
            if x1 > x0 and y1 > y0:
                umask[y0:y1, x0:x1] = True
                n_boxes += 1
    res["unit_boxes"] = n_boxes
    if n_boxes == 0:
        res["fail"].append("outside_units: no unit_boxes in the projection")
        res["outside_units_frac"] = None
    else:
        out_u = int((changed & ~umask).sum())
        res["outside_units_px"] = out_u
        res["outside_units_frac"] = round(out_u / n_changed, 5) if n_changed else 1.0
        res["unit_cover"] = round(float(umask.mean()), 4)
        if not n_changed or res["outside_units_frac"] > args.max_out:
            res["fail"].append(f"outside_units fraction {res['outside_units_frac']:.4f} > {args.max_out}")
    res["mirror_ok"] = mirror_check(res, means, world_means, all_pts, args)
    if diff_dir:
        os.makedirs(diff_dir, exist_ok=True)
        rgb = np.zeros((h, w, 3), dtype=np.uint8)
        rgb[umask] = (40, 40, 70)  # per-unit footprints (dilated), dark blue
        rgb[changed] = (255, 255, 255)
        rgb[changed & ~umask] = (255, 0, 255)  # changed pixels outside every unit footprint, magenta
        img = Image.fromarray(rgb)
        d = ImageDraw.Draw(img)
        for key, col in (("1", (80, 160, 255)), ("2", (255, 110, 60))):
            bx = res["armies"].get(key, {}).get("box_dilated")
            if bx:
                d.rectangle(bx, outline=col, width=3)
        out = os.path.join(diff_dir, f"{shot.get('name')}_diff.png")
        img.save(out)
        res["diff_png"] = out
    res["pass"] = not res["fail"]
    return res


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("verify", help="verify.json of one run")
    ap.add_argument("--ticks", default="", help="comma list of ticks that must have a passing verify (default: the run's verify_ticks)")
    ap.add_argument("--expect-shots", type=int, default=-1, help="exact number of shot pairs expected (-1: any)")
    ap.add_argument("--log", default="", help="run.log: no warnings after the tick=900 line")
    ap.add_argument("--out", default="", help="JSON report path")
    ap.add_argument("--diff-dir", default="", help="write one diff mask PNG per pair here")
    ap.add_argument("--delta", type=int, default=24, help="changed pixel: max channel delta > this (of 255)")
    ap.add_argument("--window", type=int, default=5)
    ap.add_argument("--min-frac", type=float, default=0.90)
    ap.add_argument("--min-points", type=int, default=10)
    ap.add_argument("--max-out", type=float, default=0.05)
    ap.add_argument("--dilate", type=int, default=24)
    ap.add_argument("--mirror-r", type=float, default=-0.8, help="max Pearson r of screen x vs world X over the samples")
    ap.add_argument("--width", type=int, default=1920)
    ap.add_argument("--height", type=int, default=1080)
    ap.add_argument("--min-bytes", type=int, default=300 * 1024)
    ap.add_argument("--min-ai-buildings", type=int, default=0, help="need ai_created_alive >= N at a verify tick <= --ai-by-tick (ai run)")
    ap.add_argument("--ai-by-tick", type=int, default=900)
    args = ap.parse_args()

    doc = json.load(open(args.verify, encoding="utf-8"))
    report = {"verify_json": os.path.abspath(args.verify), "leg": doc.get("leg"), "thresholds": {
        "delta": args.delta, "window": args.window, "min_frac": args.min_frac, "min_points": args.min_points,
        "max_out": args.max_out, "dilate": args.dilate, "mirror_r": args.mirror_r, "size": [args.width, args.height], "min_bytes": args.min_bytes,
        "max_err_cm": 0.5}, "shot_freeze": doc.get("shot_freeze")}
    ok = True

    want = [int(t) for t in args.ticks.split(",") if t.strip()] if args.ticks else list(doc.get("verify_ticks", []))
    by_tick = {v["tick"]: v for v in doc.get("verifies", [])}
    vrows = []
    for t in want:
        v = by_tick.get(t)
        if v is None:
            vrows.append({"tick": t, "pass": False, "reason": "missing"})
            ok = False
            print(f"verify tick={t}: MISSING")
            continue
        units_ok = bool(v.get("pass")) and v.get("max_err_cm", 99) <= 0.5 and v.get("visible") == v.get("alive") \
            and v.get("dead_visible") == 0 and v.get("alive_hidden") == 0 and v.get("group_mismatch") == 0 \
            and v.get("drawn_instances") is not None and v.get("drawn_instances") == v.get("expected_visible")
        b_ok = v.get("buildings_alive") is not None and v.get("buildings_visible") == v.get("buildings_alive") \
            and v.get("building_dead_visible") == 0 and v.get("building_alive_hidden") == 0 and v.get("building_group_mismatch") == 0 \
            and v.get("building_drawn_instances") == v.get("buildings_alive") and v.get("building_max_err_cm", 99) <= 0.5 \
            and v.get("ai_created_visible") == v.get("ai_created_alive")
        p = units_ok and b_ok
        vrows.append({k: v.get(k) for k in ("tick", "rows", "alive", "phased", "expected_visible", "visible", "drawn_instances", "dead_visible",
                                             "alive_hidden", "no_instance", "group_mismatch", "max_err_cm", "stats_alive", "new_ids_alive",
                                             "new_ids_visible", "units_hidden_by_option", "building_rows", "buildings_alive", "buildings_visible",
                                             "building_drawn_instances", "building_dead_visible", "building_alive_hidden", "building_group_mismatch",
                                             "building_max_err_cm", "ai_created_alive", "ai_created_visible", "ai_created")}
                     | {"units_pass": units_ok, "buildings_pass": b_ok, "pass": p})
        ok &= p
        print(f"verify tick={t}: alive={v.get('alive')} visible={v.get('visible')} drawn_instances={v.get('drawn_instances')} "
              f"dead_visible={v.get('dead_visible')} alive_hidden={v.get('alive_hidden')} group_mismatch={v.get('group_mismatch')} "
              f"max_err_cm={v.get('max_err_cm', 99):.4f} new_ids_alive={v.get('new_ids_alive')} new_ids_visible={v.get('new_ids_visible')} | "
              f"buildings alive={v.get('buildings_alive')} visible={v.get('buildings_visible')} drawn_instances={v.get('building_drawn_instances')} "
              f"dead_visible={v.get('building_dead_visible')} max_err_cm={v.get('building_max_err_cm', 99):.4f} "
              f"ai_created={v.get('ai_created_visible')}/{v.get('ai_created_alive')} -> {'PASS' if p else 'FAIL'}")
    report["verify"] = vrows
    if args.min_ai_buildings > 0:
        best = max([r.get("ai_created_alive") or 0 for r in vrows if r.get("tick") is not None and r["tick"] <= args.ai_by_tick] or [0])
        ai_ok = best >= args.min_ai_buildings
        report["ai_buildings"] = {"min": args.min_ai_buildings, "by_tick": args.ai_by_tick, "max_ai_created_alive": best, "pass": ai_ok}
        ok &= ai_ok
        print(f"ai buildings: max ai_created_alive at ticks <= {args.ai_by_tick} = {best} (need >= {args.min_ai_buildings}) -> {'PASS' if ai_ok else 'FAIL'}")

    shots = [s for v in doc.get("verifies", []) for s in v.get("shots", [])]
    srows = []
    for s in shots:
        r = check_pair(s, args, args.diff_dir)
        srows.append(r)
        ok &= r["pass"]
        arm = r.get("armies", {})
        a1, a2 = arm.get("1", {}), arm.get("2", {})
        print(f"shot {r['name']}: size={r.get('size')} bytes={r.get('png_bytes')} alpha {a1.get('hits')}/{a1.get('on_screen')} "
              f"beta {a2.get('hits')}/{a2.get('on_screen')} outside={r.get('outside_frac')} (box_cover {r.get('box_cover')}) "
              f"outside_units={r.get('outside_units_frac')} (unit_cover {r.get('unit_cover')}) mirror={r.get('mirror_ok')} -> "
              f"{'PASS' if r['pass'] else 'FAIL ' + '; '.join(r['fail'])}")
    report["shots"] = srows
    if args.expect_shots >= 0 and len(shots) != args.expect_shots:
        ok = False
        print(f"shots: {len(shots)} pairs, expected {args.expect_shots}")
    report["shot_pairs"] = len(shots)

    if args.log:
        lg = check_log(args.log)
        report["log"] = lg
        ok &= lg["pass"]
        print(f"log: warnings/errors after tick=900: {lg.get('warnings_after')} -> {'PASS' if lg['pass'] else 'FAIL'}")
        for e in lg.get("examples", []):
            print(f"  {e}")

    report["pass"] = bool(ok)
    print(f"CHECK_SHOTS {'PASS' if ok else 'FAIL'} verify={sum(1 for v in vrows if v['pass'])}/{len(vrows)} "
          f"shots={sum(1 for s in srows if s['pass'])}/{len(srows)}")
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=1)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
