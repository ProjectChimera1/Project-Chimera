#!/usr/bin/env python3
"""text_drift.py: how far Slate's text layout can drift from Chromium's, run by run (plan B 2.3, F7-F9, T1).

Models, for the 36 text runs of board 3.1a and the three static fonts (no engine needed):
  chromium  exact advance sum from hmtx + GPOS 'kern' (what Chromium lays out, unhinted, fractional)
  block     Slate's plain STextBlock route: every glyph advance (kerning included) rounded to whole px
            (SlateTextShaper.cpp:797 Convert26Dot6ToRoundedPixel on the HarfBuzz x_advance), glyphs at integer origins
  frac      SChimeraText: glyph i placed at round(origin_i) where origin_i comes from FSlateFontMeasure at FontScale 16
            (advances rounded at 16x, i.e. quantised to 1/16 px) and the run reports that exact sum as its desired width
Writes results/text_drift.csv (exact width, whole-px width, worst glyph-origin error for both routes) and prints the chip
left x for both routes. The 'frac' origin of glyph i is  Measure(prefix of i+1 glyphs) - Measure(glyph i alone), which keeps
the kerning pair (i-1, i); the naive Measure(prefix of i glyphs) loses it, and its worst error is reported as
frac_naive_err so T4a knows to avoid it.

    python text_drift.py [--csv path]
"""
from __future__ import annotations

import argparse
import csv
import math
import sys
from functools import lru_cache
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from fontTools.ttLib import TTFont

from hud_common import FONT_DIRS, T, load_elements

FONT_FILES = {
    ("Cinzel", 500): "Cinzel-Medium.ttf", ("Cinzel", 600): "Cinzel-SemiBold.ttf", ("Cinzel", 700): "Cinzel-Bold.ttf",
    ("Inter", 400): "Inter-Regular.ttf", ("Inter", 500): "Inter-Medium.ttf", ("Inter", 600): "Inter-SemiBold.ttf",
    ("Inter", 700): "Inter-Bold.ttf", ("JetBrains Mono", 400): "JetBrainsMono-Regular.ttf",
    ("JetBrains Mono", 500): "JetBrainsMono-Medium.ttf", ("JetBrains Mono", 600): "JetBrainsMono-SemiBold.ttf"}
FRAC_SCALE = 16   # FSlateFontMeasure::Measure(..., FontScale = 16)


def rhu(v: float) -> int:
    """Round half up, like (v + 32) >> 6 on 26.6 values."""
    return int(math.floor(v + 0.5))


class Font:
    """Advances and GPOS pair kerning of one static TTF."""

    def __init__(self, path: Path):
        self.tt = TTFont(str(path))
        self.upm = self.tt["head"].unitsPerEm
        self.cmap = self.tt.getBestCmap()
        self.hmtx = self.tt["hmtx"]
        self._lookups = []
        gpos = self.tt["GPOS"].table if "GPOS" in self.tt else None
        if gpos is not None:
            wanted = set()
            for fr in gpos.FeatureList.FeatureRecord:
                if fr.FeatureTag == "kern":
                    wanted.update(fr.Feature.LookupListIndex)
            for i in sorted(wanted):
                lk = gpos.LookupList.Lookup[i]
                for st in lk.SubTable:
                    if lk.LookupType == 9:
                        st = st.ExtSubTable
                        ltype = st.LookupType
                    else:
                        ltype = lk.LookupType
                    if ltype == 2:
                        self._lookups.append(st)
        self._pair_cache = {}

    def glyph(self, ch: str) -> str:
        return self.cmap[ord(ch)]

    def adv(self, ch: str) -> int:
        return self.hmtx[self.glyph(ch)][0]

    def _pair(self, g1: str, g2: str) -> int:
        for st in self._lookups:
            if g1 not in st.Coverage.glyphs:
                continue
            if st.Format == 1:
                ps = st.PairSet[st.Coverage.glyphs.index(g1)]
                for rec in ps.PairValueRecord:
                    if rec.SecondGlyph == g2:
                        return int(getattr(rec.Value1, "XAdvance", 0) or 0) if rec.Value1 else 0
            elif st.Format == 2:
                c1 = st.ClassDef1.classDefs.get(g1, 0)
                c2 = st.ClassDef2.classDefs.get(g2, 0)
                if c1 < len(st.Class1Record) and c2 < len(st.Class1Record[c1].Class2Record):
                    v = st.Class1Record[c1].Class2Record[c2].Value1
                    return int(getattr(v, "XAdvance", 0) or 0) if v else 0
        return 0

    def kern(self, c1: str, c2: str) -> int:
        k = (c1, c2)
        if k not in self._pair_cache:
            self._pair_cache[k] = self._pair(self.glyph(c1), self.glyph(c2))
        return self._pair_cache[k]


@lru_cache(maxsize=None)
def load_font(family: str, weight: int) -> Font:
    name = FONT_FILES[(family, weight)]
    for d in FONT_DIRS:
        if (d / name).is_file():
            return Font(d / name)
    raise FileNotFoundError(f"{name} not found in {FONT_DIRS}")


def run_metrics(text: str, family: str, weight: int, size_px: float):
    """Per-glyph exact origins plus both Slate routes' origins and widths, all in CSS px."""
    f = load_font(family, weight)
    n = len(text)
    s = size_px / f.upm
    adv = [f.adv(c) for c in text]
    kern = [f.kern(text[i], text[i + 1]) if i + 1 < n else 0 for i in range(n)]
    # Chromium: fractional, unhinted
    exact_orig, x = [], 0.0
    for i in range(n):
        exact_orig.append(x)
        x += (adv[i] + kern[i]) * s
    exact_w = x
    # block route: whole px per glyph (kerning folded into the advance, last glyph has no partner)
    block_adv = [rhu((adv[i] + kern[i]) * s) for i in range(n)]
    block_orig = [sum(block_adv[:i]) for i in range(n)]
    block_w = sum(block_adv)
    # frac route: advances rounded at FontScale 16 (1/16 px); Measure(prefix) has no kerning with the next glyph
    sc = size_px * FRAC_SCALE / f.upm
    a16 = [rhu((adv[i] + kern[i]) * sc) for i in range(n)]
    solo16 = [rhu(adv[i] * sc) for i in range(n)]

    def measure16(k):   # Measure(text[:k]) at scale 16, in 1/16 px
        return sum(a16[:k - 1]) + solo16[k - 1] if k > 0 else 0

    frac_w = measure16(n) / FRAC_SCALE
    frac_orig = [(measure16(i + 1) - solo16[i]) / FRAC_SCALE for i in range(n)]
    naive_orig = [measure16(i) / FRAC_SCALE for i in range(n)]
    return {"n": n, "exact_w": exact_w, "block_w": float(block_w), "frac_w": frac_w, "exact_orig": exact_orig,
            "block_orig": [float(v) for v in block_orig], "frac_orig": frac_orig, "naive_orig": naive_orig,
            "kern_sum_px": sum(kern) * s, "kern_pairs": sum(1 for k in kern if k)}


def worst_err(exact, placed, snap=False):
    return max((abs((rhu(p) if snap else p) - e) for e, p in zip(exact, placed)), default=0.0)


def chip_x(routes_w: dict):
    """Left x of the four resource chips under a width function (text -> px). Chip = 1+10 + icon16 + 7 + value + 7 + rate + 10+1."""
    chips = [("340", "+18"), ("60", "+6"), ("20", "+2"), ("9 / 20", "")]
    out, x = [], 8.0
    for value, rate in chips:
        out.append(x)
        w = 2 + 20 + 16 + 7 + routes_w(value, 600, 15) + 7 + (routes_w(rate, 500, 11) if rate else 0)
        x += w + 4
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--csv", default=str(T / "results" / "text_drift.csv"))
    a = ap.parse_args(argv)
    d = load_elements()
    rows = []
    for e in d["elements"]:
        for tr in e.get("textRuns", []):
            t = e["text"]
            fam, wt, px = t["fontFamily"].strip('"'), int(t["fontWeight"]), float(t["fontSize"].replace("px", ""))
            m = run_metrics(tr["rendered"], fam, wt, px)
            chrom_w = sum(g["w"] for g in tr["glyphRects"])
            rows.append({"element": e["id"], "text": tr["rendered"], "family": fam, "weight": wt, "size_px": px,
                         "glyphs": m["n"], "chromium_w": round(chrom_w, 3), "exact_w": round(m["exact_w"], 3),
                         "block_w": m["block_w"], "frac_w": round(m["frac_w"], 4),
                         "block_err": round(worst_err(m["exact_orig"], m["block_orig"]), 3),
                         "frac_err": round(worst_err(m["exact_orig"], m["frac_orig"], snap=True), 3),
                         "frac_naive_err": round(worst_err(m["exact_orig"], m["naive_orig"], snap=True), 3),
                         "kern_px": round(m["kern_sum_px"], 3), "kern_pairs": m["kern_pairs"]})
    Path(a.csv).parent.mkdir(parents=True, exist_ok=True)
    with open(a.csv, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()), lineterminator="\n")
        w.writeheader()
        w.writerows(rows)
    print(f"{len(rows)} text runs -> {a.csv}")
    worst_model = max(rows, key=lambda r: abs(r["exact_w"] - r["chromium_w"]))
    print(f"model vs Chromium width: max |exact - chromium| = {abs(worst_model['exact_w'] - worst_model['chromium_w']):.3f} px "
          f"(run {worst_model['element']} {repr(worst_model['text'])})")
    big = max(rows, key=lambda r: r["block_err"])
    print(f"block route worst glyph-origin error {big['block_err']:.2f} px on {repr(big['text'])} ({big['glyphs']} glyphs);"
          f" whole-px width {big['block_w']:.0f} vs exact {big['exact_w']:.2f}")
    print(f"frac route worst glyph-origin error {max(r['frac_err'] for r in rows):.2f} px (placed at round(origin));"
          f" naive-prefix variant {max(r['frac_naive_err'] for r in rows):.2f} px")
    cov = next(r for r in rows if r["text"] == "Covenant Acolyte")
    print(f'kern check "Covenant Acolyte" Cinzel600 20px: kerning sum {cov["kern_px"]:.2f} px over {cov["kern_pairs"]} pairs '
          f"(exact {cov['exact_w']:.2f} px, Chromium {cov['chromium_w']:.2f} px)")
    # chip left x per route
    blk = chip_x(lambda s, wt, px: run_metrics(s, "JetBrains Mono", wt, px)["block_w"])
    frc = chip_x(lambda s, wt, px: run_metrics(s, "JetBrains Mono", wt, px)["frac_w"])
    ex = chip_x(lambda s, wt, px: run_metrics(s, "JetBrains Mono", wt, px)["exact_w"])
    fmt = lambda xs: "/".join(str(rhu(v)) for v in xs)
    print(f"chip left x, Chromium layout (snapped): {fmt(ex)}  (r4: 8/111/198/285)")
    print(f"chip left x: block {fmt(blk)}")
    print(f"chip left x: frac {fmt(frc)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
