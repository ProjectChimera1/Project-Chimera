# -*- coding: utf-8 -*-
"""Stitch the three QA views into one labelled contact sheet, and MEASURE whether it shows colour.

Run (in the asset-gen venv):
    python contact_sheet.py <out_prefix> [--label "alpha/worker"] [--sheet <path.png>]
    python contact_sheet.py --roster <prefix> [<prefix> ...] --sheet roster.png

Why measure instead of eyeball
------------------------------
The acceptance question for the texture pass is "does the sheet show actual colour rather than
grey", and that is exactly the kind of claim that gets asserted and never checked. Saturation over
the SUBJECT pixels turns it into a number that a gate can fail on.

Background is excluded by sampling the render's corner colour and discarding everything close to
it -- the Workbench world backdrop is uniform, so this is reliable and needs no alpha channel.

Emits one JSON line: SHEET_JSON {...}. Exit 0 = colourful (or --no-gate), 1 = grey.
"""
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

VIEWS = ("front", "side", "iso")
GREY_SATURATION_FLOOR = 0.06     # below this a sheet is grey in every practical sense
BG_TOLERANCE = 12                # 0-255 distance from the corner colour that still counts as backdrop


def subject_saturation(img):
    """Mean HSV saturation over non-background pixels. Returns (mean_sat, subject_frac)."""
    rgb = np.asarray(img.convert("RGB"), dtype=np.int16)
    h, w, _ = rgb.shape
    corners = np.stack([rgb[0, 0], rgb[0, w - 1], rgb[h - 1, 0], rgb[h - 1, w - 1]])
    bg = np.median(corners, axis=0)
    subject = np.abs(rgb - bg).max(axis=2) > BG_TOLERANCE
    frac = float(subject.mean())
    if not subject.any():
        return 0.0, 0.0

    px = rgb[subject].astype(np.float32)
    mx = px.max(axis=1)
    mn = px.min(axis=1)
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    return float(sat.mean()), frac


def load_views(prefix):
    found = {}
    for v in VIEWS:
        p = f"{prefix}_{v}.png"
        if os.path.exists(p):
            found[v] = p
    return found


def build_sheet(entries, sheet_path, cell=384):
    """entries: list of (label, {view: path}). One row per asset, one column per view."""
    rows = len(entries)
    cols = len(VIEWS)
    pad, header = 6, 22
    W = cols * cell + pad * (cols + 1)
    H = rows * (cell + header) + pad * (rows + 1)
    sheet = Image.new("RGB", (W, H), (24, 24, 28))
    draw = ImageDraw.Draw(sheet)

    for r, (label, views) in enumerate(entries):
        y = pad + r * (cell + header + pad)
        draw.text((pad + 2, y + 4), label, fill=(235, 235, 240))
        for c, v in enumerate(VIEWS):
            x = pad + c * (cell + pad)
            box_y = y + header
            if v in views:
                im = Image.open(views[v]).convert("RGB").resize((cell, cell), Image.LANCZOS)
                sheet.paste(im, (x, box_y))
            else:
                draw.rectangle([x, box_y, x + cell, box_y + cell], fill=(48, 40, 40))
                draw.text((x + 8, box_y + 8), f"missing {v}", fill=(220, 140, 140))
            draw.text((x + 4, box_y + cell - 14), v, fill=(200, 200, 210))

    os.makedirs(os.path.dirname(os.path.abspath(sheet_path)) or ".", exist_ok=True)
    sheet.save(sheet_path)
    return sheet_path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("prefix", nargs="?", help="single asset out_prefix (as passed to blender_qa_render)")
    ap.add_argument("--roster", nargs="*", default=None, help="many prefixes -> one roster sheet")
    ap.add_argument("--label", default=None)
    ap.add_argument("--sheet", default=None)
    ap.add_argument("--no-gate", action="store_true", help="report saturation but never exit non-zero")
    args = ap.parse_args()

    prefixes = args.roster if args.roster else ([args.prefix] if args.prefix else [])
    if not prefixes:
        ap.error("give a prefix or --roster <prefixes>")

    entries, per_asset = [], []
    for p in prefixes:
        views = load_views(p)
        label = args.label if (args.label and len(prefixes) == 1) else os.path.basename(p)
        sats = []
        for v, path in views.items():
            s, frac = subject_saturation(Image.open(path))
            sats.append({"view": v, "mean_saturation": round(s, 4), "subject_frac": round(frac, 4)})
        mean_sat = round(float(np.mean([s["mean_saturation"] for s in sats])), 4) if sats else 0.0
        per_asset.append({
            "prefix": p, "label": label, "views_found": sorted(views),
            "per_view": sats, "mean_saturation": mean_sat,
            "colorful": mean_sat >= GREY_SATURATION_FLOOR,
        })
        entries.append((label, views))

    sheet_path = args.sheet or (prefixes[0] + "_sheet.png")
    build_sheet(entries, sheet_path)

    grey = [a["label"] for a in per_asset if not a["colorful"]]
    print("SHEET_JSON " + json.dumps({
        "sheet": sheet_path,
        "floor": GREY_SATURATION_FLOOR,
        "assets": per_asset,
        "grey": grey,
        "ok": not grey,
    }))
    if grey and not args.no_gate:
        sys.exit(1)


if __name__ == "__main__":
    main()
