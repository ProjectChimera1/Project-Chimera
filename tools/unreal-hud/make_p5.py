#!/usr/bin/env python3
"""make_p5.py: positive control P5, the Slate text model (plan B section 4.4; EXECUTION section 7).

Input  H/HudRef/ref/text_off_{A,B}.png  (board 3.1a with every glyph transparent: icons, boxes and backdrop kept)
Output H/HudRef/ref/controls/{A,B}/P5.png

Every one of the 36 text runs is re-drawn the way SChimeraText will draw it in Slate:
  * FreeType (freetype-py) rasterises each glyph at the run's pixel size with load flags = the chosen Slate hinting
    exactly as Slate sets them (UE 5.8 SlateCore/Private/Fonts/SlateFontRenderer.cpp:57-80, AppendGlyphFlags): FT_LOAD_NO_BITMAP for
    scalable faces, then None = FT_LOAD_NO_AUTOHINT | FT_LOAD_NO_HINTING (the T2 start value), Default = FT_LOAD_TARGET_NORMAL,
    AutoLight = FT_LOAD_TARGET_LIGHT, Auto = FT_LOAD_FORCE_AUTOHINT (FT_LOAD_COLOR is a no-op for these outline fonts);
    raw 8-bit grayscale coverage (FT_RENDER_MODE_NORMAL), no gamma, no stem darkening;
  * glyph origins at round(exact x): the exact origin of glyph i comes from hmtx advances plus GPOS pair kerning (fontTools,
    text_drift.run_metrics), and each glyph is placed at integer_run_origin + round(exact_origin_i);
  * straight-alpha blend in sRGB bytes: out = round(bg * (1 - a) + fg * a), a = coverage; text under a CSS group opacity (the
    locked command buttons, opacity .55) uses plan B 2.4's pre-composited colour fg = op * c + (1 - op) * #1C1F25, drawn at
    alpha = coverage (Slate draws locked buttons opaque with pre-composited colours, no SetRenderOpacity);
  * each run goes at the integer origin (x and baseline y, searched +-2 / +-3 px around the layout position) that best fits
    the reference ink box: smallest sum of the four ink-edge errors against the reference, then smallest window MAD.
The fonts are the 10 static OFL TTFs (hud-ref/fonts, the files Unreal loads), the same ones P1 and P4 serve to Chromium.

    python make_p5.py [--hinting None|Default|AutoLight|Auto] [--pair A|B|AB]
"""
from __future__ import annotations

import argparse
import importlib.metadata
import json
import sys
from functools import lru_cache
from pathlib import Path

import freetype
import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hud_common import FONT_DIRS, HUDREF, load_elements, load_rgb
from hud_compare import ink_stats, rim_median
from make_regions import effective_opacity
from text_drift import FONT_FILES, load_font, rhu, run_metrics

REF = HUDREF / "ref"
# SlateFontRenderer.cpp:61-75 (AppendGlyphFlags, EnableFontAntiAliasing on): NO_BITMAP for scalable faces plus the hinting flags
FT_LOAD = {"None": freetype.FT_LOAD_NO_BITMAP | freetype.FT_LOAD_NO_AUTOHINT | freetype.FT_LOAD_NO_HINTING,
           "Default": freetype.FT_LOAD_NO_BITMAP | freetype.FT_LOAD_TARGET_NORMAL,
           "AutoLight": freetype.FT_LOAD_NO_BITMAP | freetype.FT_LOAD_TARGET_LIGHT,
           "Auto": freetype.FT_LOAD_NO_BITMAP | freetype.FT_LOAD_FORCE_AUTOHINT}
PANEL = np.array([0x1C, 0x1F, 0x25], float)     # the command-card panel under the locked buttons (plan B 2.4)


def versions() -> dict:
    """freetype-py package version (the pinned dev dependency) and the FreeType library it loads."""
    return {"freetype_py": importlib.metadata.version("freetype-py"), "libfreetype": ".".join(map(str, freetype.version()))}
SEARCH_X, SEARCH_Y = range(-2, 3), range(-3, 4)


@lru_cache(maxsize=None)
def ft_face(family: str, weight: int):
    name = FONT_FILES[(family, weight)]
    for d in FONT_DIRS:
        if (d / name).is_file():
            return freetype.Face(str(d / name))
    raise FileNotFoundError(name)


@lru_cache(maxsize=None)
def glyph_bitmap(family: str, weight: int, size: float, ch: str, hinting: str):
    """(coverage uint8 [rows, width], left, top) of one glyph at an integer origin."""
    face = ft_face(family, weight)
    face.set_char_size(int(round(size * 64)), 0, 72, 72)       # ppem = size px
    face.load_char(ch, FT_LOAD[hinting] | freetype.FT_LOAD_RENDER)
    g = face.glyph
    bm = g.bitmap
    if bm.width == 0 or bm.rows == 0:
        return np.zeros((0, 0), np.uint8), 0, 0
    buf = np.array(bm.buffer, dtype=np.uint8).reshape(bm.rows, bm.pitch)[:, :bm.width]
    return buf, g.bitmap_left, g.bitmap_top


def baseline_guess(e, family, weight, size) -> int:
    """Chromium baseline: top of the text's content area (glyph rect y) plus the rounded hhea ascent."""
    f = load_font(family, weight)
    asc = round(f.tt["hhea"].ascent * size / f.upm)
    return rhu(e["textRuns"][0]["glyphRects"][0]["y"] + asc)


def draw_run(canvas, run, e, opacity, ox, base, hinting, clip=None):
    """Blend one run onto canvas (float64 HxWx3, straight sRGB bytes) with the run origin at integer (ox, base)."""
    fam, wt, size, text = run["family"], run["weight"], run["size"], run["text"]
    m = run_metrics(text, fam, wt, size)
    fg = np.array(run["color"], float)
    if opacity < 1.0:       # plan B 2.4: pre-composite over the panel, draw at alpha = coverage
        fg = opacity * fg + (1.0 - opacity) * PANEL
    H, W = canvas.shape[:2]
    cx0, cy0, cx1, cy1 = clip if clip else (0, 0, W, H)
    for ch, org in zip(text, m["exact_orig"]):
        if ch == " ":
            continue
        cov, left, top = glyph_bitmap(fam, wt, size, ch, hinting)
        if cov.size == 0:
            continue
        x0, y0 = ox + rhu(org) + left, base - top
        h, w = cov.shape
        xa, ya, xb, yb = max(x0, cx0), max(y0, cy0), min(x0 + w, cx1), min(y0 + h, cy1)
        if xa >= xb or ya >= yb:
            continue
        a = (cov[ya - y0:yb - y0, xa - x0:xb - x0].astype(float) / 255.0)[..., None]
        dst = canvas[ya:yb, xa:xb]
        # coverage of overlapping neighbours: composite sequentially, like Slate's glyph quads
        canvas[ya:yb, xa:xb] = dst * (1 - a) + fg * a


def fit_run(base_img, ref, run, e, opacity, hinting):
    """Search the integer origin of one run; returns (ox, base, stats)."""
    x0, y0, x1, y1 = run["window"]
    rwin = ref[y0:y1, x0:x1].astype(float)
    rbox, _, _, d = ink_stats(rwin, rim_median(rwin))
    fam, wt, size = run["family"], run["weight"], run["size"]
    gx = e["textRuns"][0]["glyphRects"][0]["x"]
    ox0, b0 = rhu(gx), baseline_guess(e, fam, wt, size)
    best = None
    for dy in SEARCH_Y:
        for dx in SEARCH_X:
            canvas = base_img[y0:y1, x0:x1].astype(float)
            # draw into the window only: shift the origin so the window starts at (0, 0)
            draw_run(canvas, run, e, opacity, ox0 + dx - x0, b0 + dy - y0, hinting)
            canvas = np.floor(canvas + 0.5)
            tbox, _, _, _ = ink_stats(canvas, rim_median(canvas), d)
            if tbox is None or rbox is None:
                score = (99, 99)
            else:
                dl, dt, db, dr = tbox[0] - rbox[0], tbox[1] - rbox[1], tbox[3] - rbox[3], tbox[2] - rbox[2]
                score = (abs(dl) + abs(dt) + abs(db) + abs(dr), float(np.abs(canvas - rwin).mean()))
            if best is None or score < best[0]:
                best = (score, ox0 + dx, b0 + dy)
    return best[1], best[2], best[0]


def make_p5(pair: str, hinting: str = "None", out=None, verbose=False):
    """Render P5 for one pair; returns (uint8 image, per-run [(id, text, ox, base, box_err, mad)])."""
    spec_runs = json.load(open(Path(__file__).resolve().parent / "regions.json", encoding="utf-8"))["runs"]
    E = {e["id"]: e for e in load_elements()["elements"]}
    base = load_rgb(REF / f"text_off_{pair}.png")
    ref = load_rgb(REF / ("ref_hudonly.png" if pair == "A" else "ref_backdrop.png"))
    canvas = base.astype(float)
    info = []
    for run in spec_runs:
        e = E[run["id"]]
        op = effective_opacity(E, e)
        ox, b, (berr, mad) = fit_run(base, ref, run, e, op, hinting)
        clip = tuple(run["clip"])
        draw_run(canvas, run, e, op, ox, b, hinting, clip=clip)
        info.append((run["id"], run["text"], ox, b, berr, mad))
        if verbose:
            print(f"  P5 {pair} run {run['id']:3d} {run['text'][:24]!r:28s} origin ({ox},{b}) ink-edge err {berr} window MAD {mad:.3f}")
    img = np.clip(np.floor(canvas + 0.5), 0, 255).astype(np.uint8)
    if out is not None:
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(img).save(out)
    return img, info


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--hinting", default="None", choices=list(FT_LOAD))
    ap.add_argument("--pair", default="AB")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args(argv)
    for pair in a.pair:
        out = REF / "controls" / pair / "P5.png"
        img, info = make_p5(pair, a.hinting, out, a.verbose)
        base = load_rgb(REF / f"text_off_{pair}.png")
        changed = int((np.abs(img.astype(int) - base).max(axis=2) > 0).sum())
        v = versions()
        print(f"P5 {pair}: hinting {a.hinting}, freetype-py {v['freetype_py']} (libfreetype {v['libfreetype']}), {len(info)} runs, {changed} px changed vs text_off -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
