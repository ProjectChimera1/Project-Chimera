#!/usr/bin/env python3
"""gen_scatter_scripts.py - writes the scatter director scripts of task S5 (plan C scatter 3.8 "Scripts") into Scripts/.

Usage: python gen_scatter_scripts.py            write every script
       python gen_scatter_scripts.py --check    exit 1 when a checked-in script differs from what this generator writes, or an op is unknown

S1X is S1.json's ops verbatim (so heights and splat equal s1_a) with scatter ops injected; S1XF is S1X frozen in one window (SX17's frozen variant); S1XL, SHADX, THINX, LATX, MOUSEX, C1S, C1S_ATTR, C1US,
SOAKS, SOAKS_SMOKE, LOOKX, VIDEOX and SXPROBE are written here. The scripts that were derived from a base script (S1, MOUSE, C1, SOAK, LOOK) read it from
Scripts/, so a change to the base shows up here. The meadow coordinates of THINX are constants below, chosen from the field map of the default seed
by thinx_pick.py (the minimum-sample rule of the SX6 bar guards the choice).
"""
import copy
import json
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
SCRIPTS = os.path.join(T, "Scripts")

# Every op StepOp knows (TerrainScriptDirector.cpp, TerrainScatterOps.cpp); the check refuses anything else.
KNOWN_OPS = {
    "camera", "look", "visible", "settle", "idle", "stroke", "paint", "hash", "shot", "g1_regions", "undo", "redo", "hitch", "save", "load",
    "random_walk", "soak", "gc", "residue", "csv", "movie", "depthcheck", "project_footprint", "await_mouse", "wait_collision", "verify_collision",
    "scatter", "scatter_wait", "scatter_visible", "scatter_verify", "scatter_fresh", "scatter_dump", "scatter_check", "scatter_counts",
    "scatter_view_counts", "scatter_target", "scatter_mask", "movie_start", "movie_wait", "fail", "exit", "temporal_freeze", "exposure",
}

# The fresh-rebuild image pairs (SX10, SX18) are shot with the renderer's temporal sequences frozen (temporal_freeze 1: r.Test.FreezeTemporalSequences 1 and
# r.TemporalAA.Debug.OverrideTemporalIndex 0), so a static scene renders the same frame every frame and an A/A pair agrees to a fraction of 1/255. In S5's
# round 2 the live A/A floor was TSR noise of the order of the P7 bar and depended on the frame rate (s1x_b at 20 fps); frozen, x_before vs x_before_aa
# measured 0.00000 changed, worst block 0.0001 (round 3 probe s1x_fz).
# COMPARE FROZEN FRAMES ONLY WITHIN ONE FREEZE WINDOW. Each temporal_freeze 1 pins ViewState->FrameIndex at whatever value it had (SceneVisibility.cpp), so two
# windows render different frame-indexed noise: in s1x_a, xf_redo vs xf_after (two windows) differs by about 0.2 % of the frame, spread evenly. Every judged
# pair and its A/A floor therefore sits inside one window (S1X/S1XL: xf_<cp> and its scatter_fresh twin; SHADX: frozen from its first op).
# SX17 (undo vs pre_last2, redo vs after) needs frames from different checkpoints. In S1X it reads the unfrozen x_ shots against the unfrozen floor; the
# frozen single-window variant is the separate script S1XF (frozen from its first op through redo, with scatter_visible 0 twins xo_<cp>), reported by
# parse_terrain.py --sx17f. Measured in S5 round 4 (EV/c/c-S5-sx17f-bars.txt): frozen, with TSR on, undo vs pre_last2 differs by about 12 % of the last2
# footprint (tree and shadow edges over the whole frame), the same state one scatter hide/restore apart differs by about 5 %, ScatterApply=clear (canonical
# instance order) changes nothing, and with anti-aliasing off the difference is gone (terrain alone: exactly 0). Heights, splat and scatter are restored
# exactly (SX2, hashes); the difference is TSR history that depends on what was on screen before. Render state only: heights, splat and records are untouched.
FREEZE = {"op": "temporal_freeze", "value": 1}
UNFREEZE = {"op": "temporal_freeze", "value": 0}

# THINX (flat all-grass map, default seed 1853059): chosen by thinx_pick.py from a scatter_dump of the unpainted map (SXPROBE). Each disc's core (inner half
# radius) must hold the pure-grass yield of SX6's minimum-sample rule: grass 200, tussock 10, flower 20 (trees 5 on the path).
THINX = {
    "dirt": [[84.0, 39.0], [-107.0, -94.0]],     # d20 discs, set dirt
    "rock": [[55.0, -45.0]],                     # d48, set rock (a wide disc: the rock band is the brush's soft outer quarter)
    "rock_d": 48,
    "rock_strength": 10,
    "rock_ticks": 14,
    "snow": [[-68.0, 23.0]],                     # d24, set snow
    "path": [[34.0, -128.0], [134.0, -128.0]],   # d10 path through the border woodland, set path
    "flower_near": [[84.0, 39.0]],
    "tree_near": [84.0, -128.0],
}


def load(name):
    with open(os.path.join(SCRIPTS, name + ".json"), encoding="utf-8") as f:
        return json.load(f)


def op(opname, /, **kw):
    d = {"op": opname}
    d.update(kw)
    return d


def settle(n=150, **kw):
    return op("settle", frames=n, **kw)


def waitfor(timeout=600, **kw):
    return op("scatter_wait", timeout_s=timeout, **kw)


def script(name, about, ops):
    return {"name": name, "about": about, "ops": ops}


# ------------------------------------------------------------------------------------------------------------------------------ S1X
def s1x():
    base = load("S1")
    ops = []
    fp_extra = {1: "dirt", 2: "rock", 3: "snow"}
    first_settle = True
    for o in base["ops"]:
        o = copy.deepcopy(o)
        nm = o["op"]
        if nm == "paint":
            fp = o.get("fp")
            fp = [fp] if isinstance(fp, str) else list(fp or [])
            o["fp"] = fp + [fp_extra[o["layer"]]]
        if nm == "settle" and o.get("frames") == 30:
            # S1's settle 30 before a shot: the fill (first one) or the scatter catching up (the others), then settle 150 for TSR, Nanite and VSM
            if first_settle:
                ops.append(waitfor(600, phase="fill"))
                ops.append(settle(150, phase="default"))
                first_settle = False
            else:
                ops.append(waitfor(300))
                ops.append(settle(150))
            continue
        if nm == "hash" and o["name"] in ("pre_last2", "after", "undo", "redo"):
            # the scatter hash fields mean something only when nothing is pending
            ops.append(waitfor(300))
            ops.append(settle(150))
        ops.append(o)
        if nm == "shot" and o["name"] == "before_aa":
            # x_before / x_before_aa: the unfrozen A/A floor (SX17); xf_before / xf_before_aa: the frozen A/A floor of SX18's pairs
            ops += [op("hash", name="before"), op("scatter_verify", name="before"), op("scatter_dump", name="before"), op("scatter_counts", name="before"),
                    op("shot", name="x_before"), op("idle", seconds=1), op("shot", name="x_before_aa"),
                    dict(FREEZE), settle(150), op("shot", name="xf_before"), op("idle", seconds=1), op("shot", name="xf_before_aa"), dict(UNFREEZE)]
        if nm == "shot" and o["name"] in ("pre_last2", "after", "undo", "redo"):
            cp = o["name"]
            blk = [op("scatter_verify", name=cp), op("camera", pose="rts80"), settle(150), op("shot", name="x_" + cp)]
            if cp != "pre_last2":
                # SX18: frozen frames, the live xf_<cp> and its scatter_fresh twin xf_<cp>_fresh; then the sequences run again (SX17's x_ shots stay unfrozen)
                blk += [op("scatter_mask", pose="rts80", name="m_" + cp, layers="all"),
                        dict(FREEZE), settle(150), op("shot", name="xf_" + cp), op("scatter_fresh", name="xf_" + cp), dict(UNFREEZE)]
            if cp == "redo":
                blk.append(op("scatter_dump", name="redo"))
                for st in ("dirt", "rock", "snow"):
                    blk.append(op("scatter_check", name=st, fp=st))
            ops += blk
        if nm == "settle" and o.get("frames") == 120:
            # look full is on: scatter on and off at the three poses (x_<pose>_full and its scatter_visible 0 twin) and the analytic view counts
            for pose in ("rts80", "oblique", "closeup"):
                ops += [op("camera", pose=pose), settle(150), op("shot", name="x_%s_full" % pose),
                        op("scatter_view_counts", pose=pose, name=pose),
                        op("scatter_visible", value=0), settle(150), op("shot", name="x_%s_full_off" % pose),
                        op("scatter_visible", value=1), settle(150)]
    return script("S1X", "Plan C scatter 3.8 script S1X: S1's ops verbatim (heights and splat equal s1_a) plus scatter ops. S1's settle 30 before a shot becomes "
                  "scatter_wait + settle 150; before/before_aa, then hash+verify+dump+counts before, x_before/x_before_aa (SX17's A/A floor with scatter) "
                  "and, with the temporal sequences frozen, xf_before/xf_before_aa (SX18's A/A floor); the scatter is waited for before the hash at "
                  "pre_last2, after, undo and redo, then verify and an rts80 shot x_<name> (SX17); at after/undo/redo the scatter mask m_<name>, then frozen "
                  "frames: xf_<name> and its scatter_fresh twin xf_<name>_fresh (SX18's oracle); dump redo and the three scatter_check sets (dirt, rock, "
                  "snow: S1's paint strokes' footprints); look full shots x_<pose>_full with their scatter_visible 0 twins and the analytic view counts; "
                  "S1's own shots and save follow.", ops)



def s1xf():
    """SX17's frozen single-window variant (reported): S1X's ops with one freeze window from the first op through the redo checkpoint (the inner
    freeze/unfreeze pairs removed, so x_before/x_before_aa, x_pre_last2, x_after, x_undo and x_redo are all frames of one window), and at each of
    pre_last2, after, undo and redo a scatter_visible 0 twin xo_<cp> (settle 150 after each toggle), so terrain can be told apart from scatter."""
    ops = [dict(FREEZE)]
    for o in s1x()["ops"]:
        if o["op"] == "temporal_freeze":
            continue
        ops.append(o)
        if o["op"] == "shot" and o["name"] in ("x_pre_last2", "x_after", "x_undo", "x_redo"):
            cp = o["name"][2:]
            ops += [op("scatter_visible", value=0), settle(150), op("shot", name="xo_" + cp), op("scatter_visible", value=1), settle(150)]
        if o["op"] == "scatter_check" and o.get("name") == "snow":
            # the redo checkpoint block ends with the last scatter_check: the window closes here (look full and S1's own shots follow unfrozen)
            ops.append(dict(UNFREEZE))
    return script("S1XF", "SX17's frozen single-window variant (task S5 round 4, reported): S1X's ops with the temporal sequences frozen from the first op "
                  "through the redo checkpoint in ONE window (S1X's per-pair freeze/unfreeze removed), so x_before/x_before_aa (the A/A floor), "
                  "x_pre_last2, x_after, x_undo and x_redo compare within one window; at each of the four checkpoints a scatter_visible 0 twin xo_<cp> "
                  "(settle 150 after each toggle) separates terrain from scatter. Run it with -ChimeraTerrainScatter=1, and once more with "
                  "-ChimeraTerrainScatterApply=clear (canonical instance order). parse_terrain.py --sx17f reads it.", ops)

def s1xl():
    return script("S1XL", "Plan C scatter 3.8 script S1XL: idle 2 frames (the enable fill has tasks in flight or unpolled: the director ticks before scatter), "
                  "then load the reference run's saved files (-ChimeraTerrainLoad), scatter_wait, settle 150, hash loaded, verify, rts80 shot x_redo (SX17's "
                  "ghost check), scatter mask; then frozen frames: xf_redo and xf_redo_aa 1 s later (this run's A/A floor) and scatter_fresh xf_redo (SX3, SX18).", [
        op("look", mode="compare"), op("camera", pose="rts80"),
        op("idle", frames=2, phase="fill"),
        op("load", phase="load"),
        waitfor(600), settle(150, phase="idle"),
        op("hash", name="loaded"), op("scatter_verify", name="loaded"),
        op("camera", pose="rts80"), settle(150), op("shot", name="x_redo"),
        op("scatter_mask", pose="rts80", name="m_redo", layers="all"),
        dict(FREEZE), settle(150), op("shot", name="xf_redo"), op("idle", seconds=1), op("shot", name="xf_redo_aa"),
        op("scatter_fresh", name="xf_redo"), dict(UNFREEZE),
    ])


def shadx():
    near = "@t0"
    return script("SHADX", "Plan C scatter 3.8 script SHADX (flat map, compare mode, rts80): grass and groundcover hidden (thin geometry out of the A/A floor), "
                  "tree t0 stored near [0,40] with the caster mask within 45 m and t0's own tree + shadow mask, shots t_a and t_b 1 s apart (the A/A floor); a far raise 80 m from t0 with the t_far shot and "
                  "its scatter_fresh twin; raise d60 s20 on t0 (the tree must rise more than 2 m), t_raised and its fresh twin with the union masks; undo, t_undo "
                  "and its fresh twin. The CSV capture (-csvCategories=VSM) covers the edits. Temporal sequences are frozen first (repeatable frames).", [
        dict(FREEZE), op("look", mode="compare"), op("camera", pose="rts80"),
        waitfor(600, phase="fill"), settle(150, phase="idle"),
        op("hash", name="start"),
        op("scatter_visible", value=0, layers="grass,groundcover"), settle(150),
        op("scatter_target", **{"class": "tree", "near": [0, 40], "as": "t0"}),
        op("scatter_verify", name="start"),
        op("scatter_mask", pose="rts80", name="m_a", layers="trees,shrubs,rocks", near=near, radius=45),
        op("scatter_mask", pose="rts80", name="m_t0_a", layers="trees", only=near),
        settle(150), op("csv", mode="start"),
        op("shot", name="t_a", phase="a"), op("idle", seconds=1), op("shot", name="t_b"),
        op("stroke", mode="raise", d=40, s=40, path=[[-75, 40]], ticks=20, phase="far"),
        waitfor(300), settle(150), op("shot", name="t_far"), op("scatter_fresh", name="t_far"),
        op("stroke", mode="raise", d=60, s=20, path=near, ticks=20, phase="raised"),
        waitfor(300), op("scatter_verify", name="raised"),
        op("scatter_mask", pose="rts80", name="m_raised", layers="trees,shrubs,rocks", near=near, radius=45, union_with="m_a"),
        op("scatter_mask", pose="rts80", name="m_t0_raised", layers="trees", only=near, union_with="m_t0_a"),
        settle(150), op("shot", name="t_raised"), op("scatter_fresh", name="t_raised"),
        op("undo", n=1, phase="undo"),
        waitfor(300), op("scatter_verify", name="undone"), settle(150), op("shot", name="t_undo"), op("scatter_fresh", name="t_undo"),
        op("hash", name="final"), op("csv", mode="stop"),
    ])


def thinx():
    d = THINX
    ops = [op("look", mode="compare"), op("camera", pose="rts80"),
           waitfor(600, phase="fill"), settle(150, phase="idle"),
           op("hash", name="start"),
           op("scatter_target", **{"class": "flower", "near": d["flower_near"][0], "as": "f0"}),
           op("scatter_target", **{"class": "tree", "near": d["tree_near"], "zone": "core", "as": "w0"}),
           op("scatter_verify", name="start"), op("scatter_dump", name="start")]
    for i, c in enumerate(d["dirt"]):
        # the first dirt disc is painted on the stored flower (plan: "dirt d20 on @f0"), the second on a meadow point
        ops.append(op("paint", layer=1, d=20, s=100, path="@f0" if i == 0 else [c], ticks=30, fp=["dirt"], phase="paint" if i == 0 else None))
    # rock paint: the brush adds weight 255 * K * (1 - r / R) per tick, so after N ticks wR = 255 * min(1, N K (1 - r / R)). A strength-100 stroke saturates to a hard
    # disc (the 48..176 band that makes rocks is then a ring one metre wide); N K = 1.4 (s 10, 14 ticks) puts the band at 0.5R..0.86R and still saturates the core
    ops.append(op("paint", layer=2, d=d["rock_d"], s=d["rock_strength"], path=d["rock"], ticks=d["rock_ticks"], fp=["rock"]))
    ops.append(op("paint", layer=3, d=24, s=100, path=d["snow"], ticks=30, fp=["snow"]))
    # the path runs through the stored core-zone tree w0
    ops.append(op("paint", layer=1, d=10, s=100, path=[d["path"][0], "@w0", d["path"][1]], ticks=120, fp=["path"]))
    ops += [waitfor(300), settle(150), op("scatter_verify", name="end"), op("hash", name="end")]
    for st in ("dirt", "rock", "snow", "path"):
        ops.append(op("scatter_check", name=st, fp=st))
    ops += [op("scatter_dump", name="end"), op("save")]
    for o in ops:
        if o.get("phase") is None and "phase" in o:
            del o["phase"]
    return script("THINX", "Plan C scatter 3.8 script THINX (flat all-grass map, compare mode): flower f0 and core-zone tree w0 stored; dirt d20 discs (set dirt), "
                  "rock d48 (set rock), snow d24 (set snow) on open meadow, a dirt path d10 100 m long through the woodland (set path); settle, verify, "
                  "scatter_check per set (SX6: the core ratio to the pure-grass yield), dump end (the dump oracle), save. Coordinates are the constants of "
                  "gen_scatter_scripts.py THINX, chosen by thinx_pick.py from a dump of the unpainted map.", ops)


def latx():
    rng = random.Random(20261002)
    ops = [op("look", mode="compare"), op("camera", pose="rts80"), waitfor(600, phase="fill"), settle(150, phase="idle")]
    spots = []
    for d in (5, 20, 60, 100):
        for k in range(20):
            lim = 150 - d / 2.0 - 5
            x = round(rng.uniform(-lim, lim), 1)
            y = round(rng.uniform(-lim, lim), 1)
            spots.append((d, k, x, y))
    for d, k, x, y in spots:
        ph = "d%d" % d
        if k % 2 == 0:
            ops.append(op("stroke", mode="raise", d=d, s=10, path=[[x, y]], ticks=20, phase=ph))
        else:
            ops.append(op("paint", layer=1, d=d, s=100, path=[[x, y]], ticks=20, phase=ph))
        ops += [waitfor(120), op("idle", seconds=1.5)]
    ops += [waitfor(300), settle(150), op("scatter_verify", name="end"), op("hash", name="end")]
    return script("LATX", "Plan C scatter 3.8 script LATX (flat map, compare mode): 20 isolated strokes per diameter 5, 20, 60, 100 (raise and dirt paint alternating, "
                  "seeded positions), each followed by scatter_wait and 1.5 s idle, so one stroke's latency (stroke end -> every tile current and flushed) is "
                  "measured without the next one's dirt.", ops)


def mousex():
    base = load("MOUSE")
    ops = []
    first_settle = True
    for o in copy.deepcopy(base["ops"]):
        if o["op"] == "settle" and first_settle:
            first_settle = False
            ops += [waitfor(600, phase="fill"), settle(150, phase="idle")]
            continue
        if o["op"] == "hash" and o.get("name") == "after":
            ops += [waitfor(300), settle(150)]
        ops.append(o)
        if o["op"] == "hash" and o.get("name") == "after":
            ops += [op("scatter_verify", name="after"), op("scatter_dump", name="after")]
    ops += [waitfor(300), op("scatter_verify", name="end"), op("hash", name="end"), op("save")]
    return script("MOUSEX", "Plan C scatter 3.8 script MOUSEX: MOUSE's ops with scatter on (fill wait before the first shot; wait before hash after; verify and dump "
                  "after), ending with scatter_wait, scatter_verify, hash and save (the dump oracle reads the saved files). Run it like MOUSE (-Windowed -ResX 1600 -ResY 900 -Inject, D9 desktop rule).", ops)


def c1s_ops():
    ops = [op("look", mode="full"), op("camera", pose="rts80"), waitfor(900, phase="fill"), settle(150, phase="transition"), op("csv", mode="start"),
           op("idle", seconds=20, phase="idle_scatter_on"),
           op("scatter_visible", value=0, phase="transition"), settle(150), op("idle", seconds=10, phase="idle_scatter_off"),
           op("scatter_visible", value=1, phase="transition"), settle(150), op("idle", seconds=10, phase="idle_scatter_on2")]
    for layer in ("grass", "groundcover", "shrubs", "trees", "rocks"):
        ops += [op("scatter_visible", value=0, layers=layer, phase="transition"), settle(150), op("idle", seconds=8, phase="idle_layer_off_" + layer),
                op("scatter_visible", value=1, layers=layer, phase="transition"), settle(150)]
    for pose in ("oblique", "closeup"):
        ops += [op("camera", pose=pose, phase="transition"), settle(150), op("idle", seconds=8, phase=pose + "_on"),
                op("scatter_visible", value=0, phase="transition"), settle(150), op("idle", seconds=8, phase=pose + "_off"),
                op("scatter_visible", value=1, phase="transition"), settle(150)]
    ops += [op("camera", pose="rts80", phase="transition"), settle(150),
            op("random_walk", seconds=60, seed=7, cycle_s=5, diameters=[5, 20, 60, 100], s=10, phase="walk"),
            op("idle", seconds=5, phase="idle_after"), op("csv", mode="stop"),
            # after the capture: SX1 and SX8 hold for this run too (the verify reads back the whole scene after the random walk's edits), and SX15's
            # analytic view counts at rts80 (the summary reads them; outside the capture, so the measured phases are unchanged)
            waitfor(300, phase="transition"), op("scatter_verify", name="end"), op("hash", name="end"),
            op("scatter_view_counts", pose="rts80", name="rts80")]
    return ops


def c1s(name, about):
    return script(name, about, c1s_ops())


def soaks(name="SOAKS", minutes=None):
    base = load("SOAK")
    ops = []
    for o in copy.deepcopy(base["ops"]):
        if o["op"] == "soak" and minutes is not None:
            o["minutes"] = minutes
        if o["op"] == "settle":
            ops += [waitfor(900, phase="fill"), settle(150, phase="idle")]
            continue
        if o["op"] == "residue":
            ops += [waitfor(300), op("scatter_verify", name="end"), op("hash", name="end")]
        ops.append(o)
    if name != "SOAKS":
        return script(name, "SOAKS with a %d-minute soak: the unmeasured smoke of the SOAKS path (task S5); never a P6 or SX15 figure." % minutes, ops)
    return script("SOAKS", "Plan C scatter 3.8 script SOAKS: SOAK with scatter on (fill wait first; scatter_wait, scatter_verify and hash after the soak, before the "
                  "reported residue sample). P6's after-GC figures read the same soak sample as in SOAK; the scatter's own growth is part of them.", ops)


def lookx():
    base = load("LOOK")
    ops = []
    for o in copy.deepcopy(base["ops"]):
        if o["op"] == "settle" and o.get("frames") == 120:
            ops += [waitfor(900), settle(150)]
            break
        ops.append(o)
    # S6 round 2 (art director, round 1): auto exposure brightened every scatter-on frame (pixels scatter never touches 1.10-1.30x brighter), so
    # each on/off pair is shot at ONE exposure: the off shot's adapted one. Per pose: <name>_auto (scatter on, auto exposure, the in-game look),
    # then hide, adapt, hold (op exposure, TerrainLighting::SetExposureHold), <name>_off, show, settle, <name> (on, same exposure), release.
    for pose, nm in (("rts80", "rts80_full"), ("oblique", "oblique"), ("closeup", "closeup")):
        ops += [op("camera", pose=pose), settle(150), op("shot", name=nm + "_auto"),
                op("scatter_visible", value=0), settle(150), op("exposure", mode="hold"), op("shot", name=nm + "_off"),
                op("scatter_visible", value=1), settle(150), op("shot", name=nm), op("exposure", mode="auto"), settle(150)]
    ops += [op("scatter_mask", pose="rts80", name="mask_trees", layers="trees"), op("scatter_mask", pose="rts80", name="mask_shrubs", layers="shrubs"),
            # S6 look measures (look_measure.py --scatter): canopy bounds (M3, M5), the casters' shadow hull (M4), grass at oblique (M10).
            op("scatter_mask", pose="rts80", name="mask_canopy", layers="trees", parts="body"),
            op("scatter_mask", pose="rts80", name="mask_casters_body", layers="trees,shrubs,rocks", parts="body"),
            op("scatter_mask", pose="rts80", name="mask_shadow", layers="trees,shrubs", parts="shadow"),
            op("scatter_mask", pose="oblique", name="mask_grass", layers="grass", parts="body"),
            op("scatter_view_counts", pose="rts80", name="rts80"), op("scatter_view_counts", pose="oblique", name="oblique"),
            op("scatter_verify", name="look"), op("hash", name="look"),
            op("look", mode="compare"), op("camera", pose="rts80"), settle(150),
            op("movie", frames=30), op("scatter_visible", value=0), settle(150), op("movie", frames=30), op("scatter_visible", value=1), settle(150),
            op("look", mode="full"), op("save")]
    return script("LOOKX", "Plan C scatter 3.8 script LOOKX: LOOK's map with scatter. look full shots at rts80, oblique, closeup, each with its scatter_visible 0 twin "
                  "(<name>_off) shot at the off shot's held exposure (op exposure; <name>_auto is the on shot under auto exposure), the tree and shrub layer masks (S6: canopy bounds, caster shadows, grass at oblique) and analytic view counts, then movie 30 at rts80 in compare mode with scatter on and off "
                  "(M9 shimmer); the shots keep LOOK's names so look_measure.py reads them.", ops)


def videox():
    ops = [op("look", mode="full"), op("camera", pose="oblique"), waitfor(600, phase="fill"), settle(150, phase="idle"),
           op("movie_start", frames=330, phase="movie"), op("idle", frames=20),
           op("stroke", mode="raise", d=40, s=60, path=[[-30, 40]], ticks=60, phase="sculpt"), op("idle", frames=10),
           op("stroke", mode="raise", d=30, s=50, path=[[20, 60]], ticks=60), op("idle", frames=10),
           op("stroke", mode="lower", d=36, s=25, path=[[-15, -5], [15, -5]], ticks=60), op("idle", frames=10),
           op("paint", layer=1, d=6, s=100, path=[[-40, 0], [0, 30], [40, 60]], ticks=60, phase="paint"), op("idle", frames=10),
           op("undo", n=1, phase="undo"), op("idle", frames=60),
           op("movie_wait"), waitfor(300), op("scatter_verify", name="end"), op("hash", name="end")]
    return script("VIDEOX", "Plan C scatter 3.8 script VIDEOX: the sculpt video with scatter on (look full, oblique): 3 sculpt strokes of 60 ticks, 1 paint stroke of "
                  "60 ticks, 1 undo, movie 330 frames captured while they run (movie_start / movie_wait, since `movie` blocks), for Alec. Run it with "
                  "-FixedFps 30 -Extra \"-ChimeraTerrainScatter=1\".", ops)


def sxprobe():
    return script("SXPROBE", "Scatter probe (S5): fill wait, settle, hash, dump and counts of the unpainted map, the three analytic view counts. thinx_pick.py reads "
                  "the dump to choose THINX's coordinates.", [
        op("look", mode="compare"), op("camera", pose="rts80"), waitfor(600, phase="fill"), settle(150, phase="idle"),
        op("hash", name="start"), op("scatter_verify", name="start"), op("scatter_dump", name="start"), op("scatter_counts", name="start"),
        op("scatter_view_counts", pose="rts80", name="rts80"), op("scatter_view_counts", pose="oblique", name="oblique"),
        op("scatter_view_counts", pose="closeup", name="closeup"),
        op("scatter_target", **{"class": "tree", "near": [0, 40], "as": "t0"}),
        op("scatter_mask", pose="rts80", name="m_trees", layers="trees", near="@t0", radius=45),
        op("shot", name="probe"), op("save"),
    ])


def all_scripts():
    return {
        "S1X": s1x(), "S1XF": s1xf(), "S1XL": s1xl(), "SHADX": shadx(), "THINX": thinx(), "LATX": latx(), "MOUSEX": mousex(),
        "C1S": c1s("C1S", "Plan C scatter 3.8 script C1S (look full, rts80): scatter_wait, settle 150, csv start; idle_scatter_on 20 s; scatter_visible 0, settle, "
                   "idle_scatter_off 10 s; on, settle, idle_scatter_on2 10 s; idle_layer_off_<layer> 8 s for each of the five layers; oblique and closeup on/off "
                   "8 s each; back to rts80; C1's 60 s random_walk as walk; idle_after 5 s; csv stop; then scatter_wait, scatter_verify, hash and the rts80 analytic view counts after the capture. Run with -Extra \"-ChimeraTerrainScatter=1\"."),
        "C1S_ATTR": c1s("C1S_ATTR", "C1S for the per-pass attribution rep (run with -Extra \"-ChimeraTerrainScatter=1 -csvGpuStats -csvCategories=VSM\"): its own script "
                        "name, so it is never pooled with or compared to C1S reps."),
        "C1US": c1s("C1US", "C1S with the 1,000 units (run with -Extra \"-ChimeraTerrainScatter=1 -ChimeraTerrainUnits=1000\" once C9 has landed): its own script name "
                    "so the D2 rows read it against C1U."),
        "SOAKS": soaks(), "SOAKS_SMOKE": soaks("SOAKS_SMOKE", 1), "LOOKX": lookx(), "VIDEOX": videox(), "SXPROBE": sxprobe(),
    }


def render(s):
    return json.dumps(s, indent=1, ensure_ascii=False) + "\n"


def main():
    check = "--check" in sys.argv
    bad = 0
    for name, s in all_scripts().items():
        unknown = sorted({o["op"] for o in s["ops"]} - KNOWN_OPS)
        if unknown:
            print("%s: unknown ops %s" % (name, unknown))
            bad += 1
        path = os.path.join(SCRIPTS, name + ".json")
        text = render(s)
        if check:
            cur = open(path, encoding="utf-8", newline="").read().replace("\r\n", "\n") if os.path.isfile(path) else None
            if cur != text:
                print("%s: differs from the generator" % name)
                bad += 1
        else:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
            print("wrote %s (%d ops)" % (path, len(s["ops"])))
    if bad:
        return 1
    if check:
        print("SCRIPTS OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
