#!/usr/bin/env python3
"""t4_routes.py: score the T4a text and icon route captures and write results/t4_routes.csv (plan B section 6, T4a).

    python t4_routes.py [--chosen ROUTE] [--rescore]

Each route is one pair-A capture H/HudRef/out/t4r_<route>.png (taken by hud_shot.sh with the route's runtime switches). This
script scores it with hud_compare.py over the top strip (--only '^top\\.', every gate) into H/HudRef/out/t4r_<route>/A/ and
writes one CSV row per route that has a capture: top.* failing counts, G5 edge errors, class MADs, the four chip left borders
measured in the capture, and the signed ink mass (capture - reference) / reference of every top.* text run (the real Slate
render's weight against the grayscale reference, T2 ruling R7). Matrix rows are plan B's 8; candidate rows are the
engine-supported coverage routes T4a found (kind=candidate). The chosen route is named in the `chosen` column.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from hud_common import HUDREF  # noqa: E402
import hud_compare  # noqa: E402

OUT = HUDREF / "out"
CSV_PATH = HERE / "results" / "t4_routes.csv"
REF_A = HUDREF / "ref" / "ref_hudonly.png"

# route id -> (kind, hinting, kern, text, icons, coverage, command-line switches)
ROUTES = {
    "frac_none_on": ("matrix", "None", "on", "frac", "svg", "default", "-HudHinting=None -HudKern=on -HudText=frac -HudIcons=svg"),
    "frac_none_off": ("matrix", "None", "off", "frac", "svg", "default", "-HudHinting=None -HudKern=off -HudText=frac -HudIcons=svg"),
    "frac_default_on": ("matrix", "Default", "on", "frac", "svg", "default", "-HudHinting=Default -HudKern=on -HudText=frac -HudIcons=svg"),
    "frac_default_off": ("matrix", "Default", "off", "frac", "svg", "default", "-HudHinting=Default -HudKern=off -HudText=frac -HudIcons=svg"),
    "frac_autolight_on": ("matrix", "AutoLight", "on", "frac", "svg", "default", "-HudHinting=AutoLight -HudKern=on -HudText=frac -HudIcons=svg"),
    "frac_autolight_off": ("matrix", "AutoLight", "off", "frac", "svg", "default", "-HudHinting=AutoLight -HudKern=off -HudText=frac -HudIcons=svg"),
    # the next two take the best hinting of the six rows above (filled in by --best-hinting)
    "block_best_on": ("matrix", "<best>", "on", "block", "svg", "default", "-HudHinting=<best> -HudKern=on -HudText=block -HudIcons=svg"),
    "png_best_on": ("matrix", "<best>", "on", "frac", "png", "default", "-HudHinting=<best> -HudKern=on -HudText=frac -HudIcons=png"),
    # engine-supported, text-only coverage candidates (no material, no binary asset)
    "cov_legacy_default_on": ("candidate", "Default", "on", "frac", "svg", "Slate.EnableLegacyFontHinting=1",
                              "-HudHinting=Default -HudKern=on -HudText=frac -HudIcons=svg -ini:Engine:[ConsoleVariables]:Slate.EnableLegacyFontHinting=1"),
    "cov_auto_on": ("candidate", "Auto", "on", "frac", "svg", "hinting=Auto (FT_LOAD_FORCE_AUTOHINT)", "-HudHinting=Auto -HudKern=on -HudText=frac -HudIcons=svg"),
}
CHIP_REF_X = [8, 111, 198, 285]
BORDER = np.array([58, 63, 72])
FILL = np.array([20, 22, 26])


def chip_left_x(img: np.ndarray, y: int = 20) -> list:
    """Left border column of each chip in the capture: a #3A3F48 pixel followed by the #14161A fill, within +-6 px of the reference."""
    out = []
    row = img[y].astype(int)
    for x0 in CHIP_REF_X:
        found = None
        for x in sorted(range(x0 - 6, x0 + 7), key=lambda v: abs(v - x0)):
            if np.abs(row[x] - BORDER).max() <= 2 and np.abs(row[x + 1] - FILL).max() <= 2:
                found = x
                break
        out.append(found)
    return out


def score(route: str, rescore: bool) -> dict | None:
    shot = OUT / f"t4r_{route}.png"
    if not shot.is_file():
        return None
    rep_p = OUT / f"t4r_{route}" / "A" / "report.json"
    if rescore or not rep_p.is_file() or rep_p.stat().st_mtime < shot.stat().st_mtime:
        rc = hud_compare.main(["--pair", "A", "--ref", str(REF_A), "--shot", str(shot), "--only", r"^top\.",
                               "--out", str(rep_p.parent), "--tag", f"t4r_{route}"])
        if rc not in (0, 1):
            print(f"{route}: hud_compare exit {rc}")
    rep = json.loads(rep_p.read_text(encoding="utf-8"))
    img = np.asarray(Image.open(shot).convert("RGB"))
    regions = rep.get("regions", {})
    failing = [n for n, r in regions.items() if not r.get("pass", True)]
    text_failing, edge_max, edge_sum, masses = [], 0.0, 0.0, {}
    text_mads, vec_mads, txt_ssims = [], [], []
    for n, r in regions.items():
        fg = r.get("failing_gates", [])
        tf = "G5" in fg
        for u in r.get("units", []):
            cl = u.get("classes", {})
            if "text" in cl:
                text_mads.append(cl["text"]["mad"])
                txt_ssims.append(cl["text"].get("ssim", 1.0))
                if "G4" in fg and cl["text"].get("badness", 0) > 1:
                    tf = True
            if "vector" in cl:
                vec_mads.append(cl["vector"]["mad"])
            for run in u.get("runs", []):
                masses[run["id"]] = run.get("mass_signed")
                errs = [abs(run.get(k, 0) or 0) for k in ("dl", "dt", "dr", "db")]
                edge_max = max(edge_max, max(errs))
                edge_sum += sum(errs)
        if tf:
            text_failing.append(n)
    g7 = rep.get("g7", {})
    w = rep.get("worst", {})
    return {
        "shot_sha256": hashlib.sha256(shot.read_bytes()).hexdigest()[:12],
        "top_failing": len(failing), "top_failing_regions": " ".join(sorted(failing)),
        "top_text_failing": len(text_failing), "top_text_failing_regions": " ".join(sorted(text_failing)),
        "g5_edge_max_px": round(edge_max, 2), "g5_edge_sum_px": round(edge_sum, 2),
        "text_mad_mean": round(float(np.mean(text_mads)), 3) if text_mads else "",
        "text_ssim_mean": round(float(np.mean(txt_ssims)), 4) if txt_ssims else "",
        "vector_mad_mean": round(float(np.mean(vec_mads)), 3) if vec_mads else "",
        "hud_mad": round(g7.get("mad", 0.0), 3), "hud_ssim": round(g7.get("ssim", 1.0), 4),
        "worst": f"{w.get('region', '-')} {w.get('badness', 0):.3f} ({w.get('gate', '-')})" if w else "",
        "chip_left_x": "/".join("?" if v is None else str(v) for v in chip_left_x(img)),
        "masses": masses,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--chosen", help="route id compiled in as the default")
    ap.add_argument("--best-hinting", help="hinting of the block/png rows (the best frac hinting)")
    ap.add_argument("--rescore", action="store_true")
    a = ap.parse_args()
    rows, run_ids = [], []
    for rid, (kind, hint, kern, text, icons, cov, sw) in ROUTES.items():
        if "<best>" in hint:
            if not a.best_hinting:
                continue
            hint, sw = a.best_hinting, sw.replace("<best>", a.best_hinting)
        s = score(rid, a.rescore)
        if s is None:
            continue
        for k in s["masses"]:
            if k not in run_ids:
                run_ids.append(k)
        rows.append((rid, kind, hint, kern, text, icons, cov, sw, s))
    run_ids.sort()
    spec = hud_compare.load_spec()
    names = {r["id"]: r["text"] for r in spec.d["runs"]}
    head = ["route", "kind", "hinting", "kern", "text", "icons", "coverage", "switches", "shot_sha256", "top_failing", "top_failing_regions",
            "top_text_failing", "top_text_failing_regions", "g5_edge_max_px", "g5_edge_sum_px", "text_mad_mean", "text_ssim_mean",
            "vector_mad_mean", "hud_mad", "hud_ssim", "worst", "chip_left_x"] + [f"mass_r{i}" for i in run_ids] + \
           ["mass_min", "mass_max", "mass_mean", "chosen"]
    CSV_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(CSV_PATH, "w", newline="", encoding="utf-8") as f:
        wr = csv.writer(f)
        wr.writerow(head)
        for rid, kind, hint, kern, text, icons, cov, sw, s in rows:
            ms = [s["masses"].get(i) for i in run_ids]
            mv = [m for m in ms if m is not None]
            wr.writerow([rid, kind, hint, kern, text, icons, cov, sw] + [s[k] for k in head[8:22]] +
                        ["" if m is None else round(m, 4) for m in ms] +
                        [round(min(mv), 4) if mv else "", round(max(mv), 4) if mv else "", round(float(np.mean(mv)), 4) if mv else "",
                         "CHOSEN" if rid == a.chosen else ""])
    print(f"t4_routes: {len(rows)} rows -> {CSV_PATH}")
    print("runs: " + "; ".join(f"r{i}={names.get(i, '?')!r}" for i in run_ids))
    for rid, kind, hint, kern, text, icons, cov, sw, s in rows:
        mv = [m for m in s["masses"].values() if m is not None]
        print(f"{rid:24s} {kind:9s} top_fail {s['top_failing']} text_fail {s['top_text_failing']} edge_max {s['g5_edge_max_px']} "
              f"text_mad {s['text_mad_mean']} vec_mad {s['vector_mad_mean']} hud_mad {s['hud_mad']} chips {s['chip_left_x']} "
              f"mass min {min(mv):+.3f} max {max(mv):+.3f} mean {np.mean(mv):+.3f}  worst {s['worst']}"
              + ("  CHOSEN" if rid == a.chosen else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
