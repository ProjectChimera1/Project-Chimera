#!/usr/bin/env python3
"""hud_shot_check.py - post-run checks for hud_shot.sh (plan B 2.9, 2.7).

Prints one line: `SHOT OK <png> mode=<m> compile_s=<n> scale=1.000 local=1920x1080 ...` (or `SHOT OK <log> WARMUP DONE compile_s=<n>`)
or `SHOT FAIL <reason> exit=<code>`; exit 0 only on OK.

Checks: process exit 0 and the success sentinel (`shot written` or `WARMUP DONE`); the PNG is newer than the start and 1920x1080;
the last `Scene viewport resized to 1920x1080, mode (Fullscreen|WindowedFullscreen)`; `geometry scale=1.000 local=1920x1080` (with --ui-scale s: scale=s, local=1920/s x 1080/s,
viewport still 1920x1080);
the image allow-list over `loaded <path> <sha256>` lines; zero `LogChimeraHud: Error`.
"""
import argparse
import hashlib
import json
import os
import re
import struct
import sys




def png_size(path):
    with open(path, "rb") as f:
        head = f.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    return struct.unpack(">II", head[16:24])


def allowed(path, sha, backdrop, h):
    p = path.replace("\\", "/")
    hn = h.replace("\\", "/").rstrip("/") + "/"
    if backdrop and p.lower() == backdrop.replace("\\", "/").lower():
        return True
    if p.startswith(hn + "HudData/Fonts/") and p.lower().endswith(".ttf"):
        return True
    if p in (hn + "HudData/Placeholders/minimap_photo.png", hn + "HudData/Placeholders/portrait_figure.png"):
        return True
    for sub in ("Icons", "Ornaments"):
        if p.startswith(hn + "HudData/" + sub + "/"):
            mf = os.path.join(h, "HudData", "manifest.json")
            if os.path.isfile(mf):
                files = json.load(open(mf, encoding="utf-8")).get("files", {})
                rel = p[len(hn):]
                ent = files.get(rel) or files.get(rel[len("HudData/"):])
                if isinstance(ent, dict):
                    ent = ent.get("sha256")
                return ent == sha
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", required=True)
    ap.add_argument("--png")
    ap.add_argument("--start", type=float, default=0)
    ap.add_argument("--exit", default="NONE")
    ap.add_argument("--backdrop", default="")
    ap.add_argument("--h", required=True)
    ap.add_argument("--warmup", action="store_true")
    ap.add_argument("--ui-scale", type=float, default=1.0,
                    help="expected -HudUiScale (T8's ungated UI-scale shot): geometry scale=<s>, local=round(1920/s)x round(1080/s)")
    a = ap.parse_args()

    def fail(reason):
        print(f"SHOT FAIL {reason} exit={a.exit}")
        return 1

    if not os.path.isfile(a.log):
        return fail("NO LOG")
    text = open(a.log, encoding="utf-8", errors="replace").read()
    errs = re.findall(r"LogChimeraHud: Error: (.*)", text)
    shot_err = next((e for e in errs if e.startswith("SHOT ")), None)
    if shot_err:
        # the forced exit code must match the reason
        return fail(shot_err.strip())
    if a.exit == "KILLED":
        return fail("KILLED PAST DEADLINE")
    if a.exit != "0":
        # a bare exit code with no SHOT line in the log is a crash or an engine exit, never a capture failure
        crit = re.search(r"LogWindows: Error: (Assertion failed.*|=== Critical error)", text)
        return fail("CRASH " + crit.group(1).strip()[:120] if crit else "NONZERO EXIT without a SHOT error line")
    if errs:
        return fail("LogChimeraHud Error: " + errs[0].strip())

    comp = re.search(r"LogChimeraHud: (?:Display: )?compile idle after ([0-9.]+) s", text)
    compile_s = comp.group(1) if comp else "?"

    # allow-list
    for m in re.finditer(r"LogChimeraHud: (?:Display: )?loaded (.+?) ([0-9a-f]{64})\s*$", text, re.M):
        if not allowed(m.group(1).strip(), m.group(2), a.backdrop, a.h):
            return fail(f"ALLOW-LIST VIOLATION {m.group(1).strip()}")

    if a.warmup:
        m = re.search(r"LogChimeraHud: (?:Display: )?WARMUP DONE compile_s=(\d+)", text)
        if not m:
            return fail("NO WARMUP SENTINEL")
        print(f"SHOT OK {a.log} WARMUP DONE compile_s={m.group(1)}")
        return 0

    m = re.search(r"LogChimeraHud: (?:Display: )?shot written (\d+)", text)
    if not m:
        return fail("NO SHOT WRITTEN SENTINEL")
    if not a.png or not os.path.isfile(a.png):
        return fail("PNG MISSING")
    if os.path.getmtime(a.png) < a.start - 2:
        return fail("PNG STALE")
    size = png_size(a.png)
    if size != (1920, 1080):
        return fail(f"PNG SIZE {size}")
    modes = re.findall(r"Scene viewport resized to 1920x1080, mode (\w+)", text)
    if not modes:
        return fail("NO 1920x1080 VIEWPORT LINE")
    g = re.search(r"geometry scale=([0-9.]+) local=(\d+)x(\d+) viewport=(\d+)x(\d+)", text)
    if not g:
        return fail("NO GEOMETRY LINE")
    want_scale = f"{a.ui_scale:.3f}"
    want_local = (str(round(1920 / a.ui_scale)), str(round(1080 / a.ui_scale)))
    if g.group(1) != want_scale or (g.group(2), g.group(3)) != want_local or (g.group(4), g.group(5)) != ("1920", "1080"):
        return fail(f"GEOMETRY scale={g.group(1)} local={g.group(2)}x{g.group(3)} viewport={g.group(4)}x{g.group(5)} "
                    f"(want scale={want_scale} local={want_local[0]}x{want_local[1]} viewport=1920x1080)")
    sha = hashlib.sha256(open(a.png, "rb").read()).hexdigest()[:12]
    print(f"SHOT OK {a.png} mode={modes[-1]} compile_s={compile_s} scale={g.group(1)} local={g.group(2)}x{g.group(3)} "
          f"viewport={g.group(4)}x{g.group(5)} bytes={m.group(1)} sha={sha}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
