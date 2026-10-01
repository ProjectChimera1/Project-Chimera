#!/usr/bin/env python3
"""mouse_sheet.py - P10's picture (plan C C8): mouse.jpg = before | after at the rts80 pose with the three planned drags marked and the
per-stroke numbers (source, mode, pick error, ticks) burned in. Usage: mouse_sheet.py RUN_DIR [OUT.jpg]  (default RUN_DIR/mouse.jpg)."""
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont


def font(size):
    for p in ("C:/Windows/Fonts/consola.ttf", "C:/Windows/Fonts/arial.ttf"):
        if os.path.isfile(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()


def main():
    run = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(run, "mouse.jpg")
    res = json.load(open(os.path.join(run, "results.json"), encoding="utf-8"))
    tg = json.load(open(os.path.join(run, "mouse_targets.json"), encoding="utf-8"))
    before = Image.open(os.path.join(run, "before.png")).convert("RGB")
    after = Image.open(os.path.join(run, "after.png")).convert("RGB")
    W, H = before.size
    vw, vh = tg["viewport"]
    sx, sy = W / vw, H / vh
    srcs = sorted({m.get("source") for m in res.get("mouse_strokes", [])})
    how = {("os",): "real OS mouse", ("slate",): "Slate fallback, not the OS mouse"}.get(tuple(srcs), "sources %s" % "/".join(map(str, srcs)))
    n = len(res.get("mouse_strokes", []))
    for im, label in ((before, "before"), (after, "after (%d strokes, %s)" % (n, how))):
        d = ImageDraw.Draw(im)
        for i, t in enumerate(tg["strokes"]):
            a = (t["from_px"][0] * sx, t["from_px"][1] * sy)
            b = (t["to_px"][0] * sx, t["to_px"][1] * sy)
            d.line([a, b], fill=(255, 220, 0), width=3)
            for p in (a, b):
                d.ellipse([p[0] - 7, p[1] - 7, p[0] + 7, p[1] + 7], outline=(255, 60, 60), width=3)
            d.text((a[0], a[1] - 34), "S%d" % (i + 1), fill=(255, 255, 255), font=font(22), stroke_width=2, stroke_fill=(0, 0, 0))
        d.text((16, 12), label, fill=(255, 255, 255), font=font(34), stroke_width=3, stroke_fill=(0, 0, 0))
    lines = []
    for s in res.get("mouse_strokes", []):
        lines.append("S%d source=%s %s d=%d s=%d  pick err %.3f m (start), %.2f/%.2f px start/end  ticks=%d dropped=%d%s" % (
            s["index"] + 1, s["source"], s["mode"], s["d"], s["s"], s.get("pick_err_m", -1), s.get("pick_err_start_px", -1),
            s.get("pick_err_end_px", -1), s["ticks_applied"], s["ticks_dropped"],
            "  Ctrl+Z ignored while LMB held" if s.get("keys_ignored_while_lmb") else ""))
    sheet = Image.new("RGB", (W * 2, H + 30 + 34 * len(lines)), (18, 18, 18))
    sheet.paste(before, (0, 0))
    sheet.paste(after, (W, 0))
    d = ImageDraw.Draw(sheet)
    for i, ln in enumerate(lines):
        d.text((16, H + 14 + 34 * i), ln, fill=(235, 235, 235), font=font(26))
    sheet = sheet.resize((sheet.width // 2, sheet.height // 2), Image.LANCZOS)
    sheet.save(out, quality=85)
    print(out, sheet.size, os.path.getsize(out))


if __name__ == "__main__":
    main()
