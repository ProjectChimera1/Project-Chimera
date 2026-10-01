#!/usr/bin/env python3
"""mouse_gate.py - plan C P10 (OS mouse in -game and packaged), C8. Kept in its own file: parse_terrain.py is shared with C7/C12.

Usage: mouse_gate.py RUN_DIR [--json OUT] [--allow-slate]
       mouse_gate.py RUN_DIR --hitch          (a MOUSE_HITCH run: the 15-tick catch-up cap and ticks_dropped, plan C 3.4)
  RUN_DIR is a MOUSE run (run_terrain.ps1 -Script MOUSE -Tag <tag> -Windowed -ResX 1600 -ResY 900 -Inject).
P10 bars (>= 3 strokes source=os; pick error <= 1 m against the logged targets, ~9 px at 80 m; footprint changed_frac >= 0.30 above
the A/A floor):
  log_scan, completed        the shared KIT log scan and results.json completed=true, ops_done == ops_total
  strokes_os                 >= 3 mouse strokes and every one source=os (source=slate FAILS unless --allow-slate: Alec's D9 waiver)
  pick_error                 each stroke's start pick within 1 m of the logged target (3-D distance, metres)
  pick_error_end_px          each stroke's end cursor within 9 px of the logged end target (the plan's pixel equivalent of 1 m; the
                             end pick in metres follows the surface the stroke has just edited, so it is reported, not gated)
  footprint_changed          imgdiff changed_frac(before, after) inside the planned-drag footprint minus the same for before vs
                             before_aa (the A/A floor) >= 0.30
C8 input bars (plan C 3.4 keys, in the same run):
  brush_keys                 the strokes carry d 30 / s 30 and raise, lower, raise: the keys ] ] = = = = 2 1 reached the controller
  keys_ignored_while_lmb     Ctrl+Z pressed mid-drag on stroke 3 was ignored (recorded ignored_lmb_held), no undo applied during strokes
  undo_redo                  Ctrl+Z after the strokes restores stroke 3's start height hash, Ctrl+Y its end hash, `after` equals it
  tail_keys                  [ - ] = 3 4 5 Shift+2 after the strokes: d 25 / s 25 / d 30 / s 30 / smooth / flatten / paint / layer 1
  hud                        the HUD drew the final brush (log line "hud: Terrain  mode Paint  size 30 m  strength 30  layer Dirt")
Reported, not gated: ticks per stroke (plan: 60) and ticks_dropped, platform-cursor error, end-pick drift on the edited surface,
input while disarmed, the injector's pre-checks (LogonUI, parsecd).
Prints "C8 INPUT PASS|FAIL" (the C8 input group) and "P10 PASS|FAIL" (every other bar). Exit 0 = both pass, 1 = a bar failed, 2 = io.
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import parse_terrain as pt  # noqa: E402
import imgdiff  # noqa: E402

P10_MIN_STROKES = 3
P10_PICK_ERR_M = 1.0
P10_PICK_ERR_PX = 9.0
P10_CHANGED_ABOVE_FLOOR = 0.30
PLAN_TICKS = 60
HITCH_MIN_DROPPED = 15   # a 1000 ms hitch owes >= 30 ticks; the cap applies 15
EXPECTED_TAIL = [  # (key, action, mode, d, s, layer) after the three strokes
    ("ctrl+z", "undo", None, None, None, None),
    ("ctrl+y", "redo", None, None, None, None),
    ("[", "size", None, 25, 30, None),
    ("-", "strength", None, 25, 25, None),
    ("]", "size", None, 30, 25, None),
    ("=", "strength", None, 30, 30, None),
    ("3", "mode", "smooth", 30, 30, None),
    ("4", "mode", "flatten", 30, 30, None),
    ("5", "mode", "paint", 30, 30, None),
    ("shift+2", "paint_layer", "paint", 30, 30, 1),
]
C8_INPUT_BARS = ("brush_keys", "keys_ignored_while_lmb", "undo_redo", "tail_keys", "hud")
HUD_FINAL ="hud: Terrain  mode Paint  size 30 m  strength 30  layer Dirt"


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def read_text(path):
    if not os.path.isfile(path):
        return ""
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def hitch_gate(run, g, res):
    strokes = res.get("mouse_strokes") or []
    hitches = [h for h in res.get("hitches") or [] if h.get("source") == "await_mouse"]
    s = strokes[0] if strokes else {}
    slept = hitches[0].get("ms_slept") if hitches else None
    g.bar("hitch_slept", bool(hitches) and num(slept) and slept >= 1000, "hitches=%s" % hitches, "one await_mouse hitch of >= 1000 ms inside the stroke")
    dropped, applied, gap = s.get("ticks_dropped"), s.get("ticks_applied"), s.get("max_frame_gap_ms")
    g.bar("ticks_dropped", num(dropped) and dropped >= HITCH_MIN_DROPPED and num(gap) and gap >= 1000,
          "ticks_applied=%s ticks_dropped=%s max_frame_gap_ms=%s" % (applied, dropped, gap),
          "the >= 1 s frame applies 15 ticks and counts the rest: ticks_dropped >= %d" % HITCH_MIN_DROPPED)
    print("HITCH PASS" if g.ok else "HITCH FAIL")
    return 0 if g.ok else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("run")
    ap.add_argument("--json")
    ap.add_argument("--allow-slate", action="store_true", help="Alec's D9 waiver: source=slate counts for P10")
    ap.add_argument("--hitch", action="store_true", help="gate a MOUSE_HITCH run instead (ticks_dropped)")
    a = ap.parse_args(argv)
    run = a.run
    g = pt.Gates()
    res = pt.scan_run(run, g)
    if res is None:
        return 2
    if a.hitch:
        return hitch_gate(run, g, res)
    strokes = res.get("mouse_strokes") or []
    keys = res.get("mouse_keys") or []
    sources = [s.get("source") for s in strokes]
    allowed = ("os", "slate") if a.allow_slate else ("os",)
    os_ok = len(strokes) >= P10_MIN_STROKES and all(x in allowed for x in sources)
    g.bar("strokes_os", os_ok, "n=%d sources=%s" % (len(strokes), sources),
          ">= %d strokes, every source=os%s" % (P10_MIN_STROKES, " (slate waived, D9)" if a.allow_slate else ""))

    errs = [s.get("pick_err_m") for s in strokes]
    ok = len(strokes) >= P10_MIN_STROKES and all(num(e) and e <= P10_PICK_ERR_M for e in errs)
    g.bar("pick_error", ok, "start pick err m=%s start px=%s" % ([round(e, 3) if num(e) else e for e in errs],
                                                                   [round(s.get("pick_err_start_px", -1), 2) for s in strokes]),
          "<= %.1f m against the logged start targets, every stroke" % P10_PICK_ERR_M)
    end_px = [s.get("pick_err_end_px") for s in strokes]
    ok = len(strokes) >= P10_MIN_STROKES and all(num(e) and e <= P10_PICK_ERR_PX for e in end_px)
    g.bar("pick_error_end_px", ok, "end px err=%s" % [round(e, 2) if num(e) else e for e in end_px],
          "<= %.0f px against the logged end targets (1 m at 80 m), every stroke" % P10_PICK_ERR_PX)
    g.bar("end_pick_drift", True, {"end_pick_drift_xy_m": [round(s.get("end_pick_drift_xy_m", -1), 2) for s in strokes],
                                   "centre_m": [s.get("centre_m") for s in strokes]},
          "reported: the pick follows the surface the stroke edits (raise climbs toward the camera, lower runs away)", informational=True)

    # image bar
    before, before_aa, after = (os.path.join(run, n + ".png") for n in ("before", "before_aa", "after"))
    fp_path = os.path.join(run, "footprints.json")
    img_ok, img_val = False, "missing shot or footprints.json"
    if all(os.path.isfile(p) for p in (before, before_aa, after, fp_path)):
        A, AA, B = imgdiff.load_luma(before), imgdiff.load_luma(before_aa), imgdiff.load_luma(after)
        with open(fp_path, encoding="utf-8") as f:
            mask = imgdiff.footprint_mask(json.load(f), "rts80/mouse", A.shape)
        n = int(mask.sum())
        if n < pt.MIN_MASK_PX:
            img_val = "footprint mask has %d px (< %d): no samples" % (n, pt.MIN_MASK_PX)
        else:
            changed = imgdiff.changed_frac(A, B, mask)
            floor = imgdiff.changed_frac(A, AA, mask)
            img_ok = changed - floor >= P10_CHANGED_ABOVE_FLOOR
            img_val = "changed=%.4f floor(A/A)=%.4f above=%.4f mask_px=%d" % (changed, floor, changed - floor, n)
    g.bar("footprint_changed", img_ok, img_val, "before vs after inside the three planned drags: changed_frac above the A/A floor >= %.2f" % P10_CHANGED_ABOVE_FLOOR)

    modes = [s.get("mode") for s in strokes]
    ds = [s.get("d") for s in strokes]
    ss = [s.get("s") for s in strokes]
    keys_ok = (modes[:3] == ["raise", "lower", "raise"] and len(ds) >= 3
               and all(num(d) and abs(d - 30) < 1e-6 for d in ds[:3]) and all(num(s) and abs(s - 30) < 1e-6 for s in ss[:3]))
    g.bar("brush_keys", keys_ok, "modes=%s d=%s s=%s" % (modes, ds, ss), "raise, lower, raise at d 30 / s 30 (keys ] ] = = = = 2 1 arrived)")

    ignored = [s.get("keys_ignored_while_lmb", 0) for s in strokes]
    ignored_z = [k for k in keys if k.get("key") == "ctrl+z" and k.get("action") == "ignored_lmb_held" and k.get("after_strokes") == 2]
    undo_during = [k for k in keys if k.get("key") == "ctrl+z" and k.get("applied") and k.get("after_strokes", 0) < P10_MIN_STROKES]
    g.bar("keys_ignored_while_lmb", len(ignored) >= 3 and ignored[2] >= 1 and bool(ignored_z) and not undo_during,
          "ignored per stroke=%s ctrl+z ignored records=%d undo applied during strokes=%d" % (ignored, len(ignored_z), len(undo_during)),
          "Ctrl+Z mid-drag on stroke 3 ignored (DW-144), no undo applied before the strokes ended")

    tail = [k for k in keys if k.get("applied") and k.get("after_strokes") == P10_MIN_STROKES]
    s3 = strokes[2] if len(strokes) >= 3 else {}
    after_h = (res.get("hashes") or {}).get("after", {}).get("height_fnv")
    undo = tail[0] if len(tail) >= 1 else {}
    redo = tail[1] if len(tail) >= 2 else {}
    ur_ok = (undo.get("key") == "ctrl+z" and undo.get("action") == "undo" and undo.get("height_fnv") == s3.get("start_height_fnv")
             and redo.get("key") == "ctrl+y" and redo.get("action") == "redo" and redo.get("height_fnv") == s3.get("end_height_fnv")
             and after_h == s3.get("end_height_fnv") and s3.get("start_height_fnv") != s3.get("end_height_fnv"))
    g.bar("undo_redo", ur_ok, "stroke3 start=%s end=%s undo=%s redo=%s after=%s" % (
        s3.get("start_height_fnv"), s3.get("end_height_fnv"), undo.get("height_fnv"), redo.get("height_fnv"), after_h),
        "Ctrl+Z restores stroke 3's start hash, Ctrl+Y its end hash, the `after` hash equals it")

    got = [(k.get("key"), k.get("action"), k.get("mode"), k.get("d"), k.get("s"), k.get("layer")) for k in tail]
    tail_ok = len(got) == len(EXPECTED_TAIL)
    for e, r in zip(EXPECTED_TAIL, got):
        for i, ev in enumerate(e):
            if ev is not None and (r[i] != ev if not num(ev) else not (num(r[i]) and abs(r[i] - ev) < 1e-6)):
                tail_ok = False
    g.bar("tail_keys", tail_ok, "got=%s" % [(r[0], r[1], r[2], r[3], r[4], r[5]) for r in got],
          "after the strokes: %s" % " ".join(e[0] for e in EXPECTED_TAIL))

    log_text = read_text(os.path.join(run, "game.log"))
    g.bar("hud", HUD_FINAL in log_text, "final HUD line logged=%s" % (HUD_FINAL in log_text), "the HUD drew the final brush: '%s'" % HUD_FINAL)

    mc = res.get("mouse_controller") or {}
    g.bar("ticks", True, {"ticks_applied": [s.get("ticks_applied") for s in strokes], "plan_ticks": PLAN_TICKS,
                          "ticks_dropped": [s.get("ticks_dropped") for s in strokes],
                          "max_frame_gap_ms": [round(s.get("max_frame_gap_ms", -1), 1) for s in strokes],
                          "platform_cursor_err_px": [round(s.get("platform_cursor_err_px", -1), 2) for s in strokes],
                          "input_while_disarmed": mc.get("input_while_disarmed")}, "reported", informational=True)
    inj = {}
    ip = os.path.join(run, "inject.json")
    if os.path.isfile(ip):
        with open(ip, encoding="utf-8") as f:
            inj = json.load(f)
    g.bar("injector", True, {"exit": inj.get("exit"), "reason": inj.get("reason"), "precheck": inj.get("precheck"),
                             "foreground": inj.get("foreground")}, "inject.json (reported; the strokes bar is the gate)", informational=True)
    p10_ok = all(r["pass"] for r in g.rows if r["bar"] not in C8_INPUT_BARS)
    input_ok = all(r["pass"] for r in g.rows if r["bar"] in C8_INPUT_BARS)
    print("C8 INPUT PASS" if input_ok else "C8 INPUT FAIL")
    print("P10 PASS" if p10_ok else "P10 FAIL")
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"p10": p10_ok, "c8_input": input_ok, "bars": g.rows}, f, indent=1)
    return 0 if (p10_ok and input_ok) else 1


if __name__ == "__main__":
    sys.exit(main())
