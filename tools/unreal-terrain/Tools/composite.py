#!/usr/bin/env python3
"""composite.py - captioned grid of unretouched 1920x1080 renders with numbers burned in (plan C 4 C7, C13). Needs Pillow.

Copied from tools/unreal-looktest/composite.py (PLAN section 11.1) and extended: any number of panels (2 per row), a caption bar per panel,
and a footer of measured numbers. The renders are never resized, filtered or retouched in the PNG: after saving, the PNG is re-read and
every panel is compared with its source, and the script fails if a single pixel differs. A JPG copy (for the evidence folder, <= --jpg-max
bytes, EXECUTION 2.3) is written by lowering the quality until it fits. SHA-256 of every source is printed (and written with --hashes).

    python composite.py --out T/Out/c7_composite/composite.png --jpg EV/c/c-C7-composite.jpg \\
        --panel ProjectChimera/LookTest/out/game_A_noLumen_r1.png "LT_A_noLumen (look test) · flat plane · rts80" \\
        --panel T/Out/s1_mat/rts80_full.png "ChimeraTerrain M_ChimeraGround · rts80 · look full" \\
        --panel T/Out/s1_mat/closeup.png "closeup" --panel T/Out/s1_mat/oblique.png "oblique" \\
        --title "C7 ground material" --number "paint changed_frac 0.71 >= 0.30" --number "terrain GPU 1.2 ms"

Exit 1 on any problem (missing file, wrong size, caption too long, pixel mismatch, JPG cannot fit).
"""
import argparse
import hashlib
import io
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

W, H = 1920, 1080
BAR = 56
TITLE = 64
FOOT_LINE = 30
FONT_REG = 'C:/Windows/Fonts/arial.ttf'
FONT_BOLD = 'C:/Windows/Fonts/arialbd.ttf'
BG, FG, FG2, RULE = (22, 22, 26), (245, 245, 245), (170, 175, 185), (90, 94, 104)


def sha256(path):
    """Hex SHA-256 of a file."""
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def load_render(path):
    """Open a render as RGB and assert it is exactly 1920x1080."""
    if not os.path.isfile(path):
        raise ValueError(f'input not found: {path}')
    im = Image.open(path)
    if im.size != (W, H):
        raise ValueError(f'{path}: size {im.size[0]}x{im.size[1]}, expected {W}x{H}')
    return im.convert('RGB')


def fit_font(draw, text, path, size, max_w, min_size):
    """Largest font <= size whose rendering of text fits max_w; raise if even min_size does not fit."""
    while size >= min_size:
        font = ImageFont.truetype(path, size)
        if draw.textlength(text, font=font) <= max_w:
            return font
        size -= 1
    raise ValueError(f'text does not fit in {max_w}px even at {min_size}pt: {text!r}')


def bold():
    return FONT_BOLD if os.path.isfile(FONT_BOLD) else FONT_REG


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--panel', nargs=2, action='append', metavar=('PNG', 'CAPTION'), required=True)
    ap.add_argument('--title', default='')
    ap.add_argument('--number', action='append', default=[], help='one footer line of measured numbers (repeatable)')
    ap.add_argument('--out', required=True, help='PNG output (pixel-exact panels)')
    ap.add_argument('--jpg', help='also write a JPG copy that fits --jpg-max bytes')
    ap.add_argument('--jpg-max', type=int, default=1500000)
    ap.add_argument('--hashes', metavar='PATH', help='write source SHA-256s as JSON')
    a = ap.parse_args(argv)

    try:
        if not os.path.isfile(FONT_REG):
            raise ValueError(f'font not found: {FONT_REG}')
        panels = [(load_render(p), p, c) for p, c in a.panel]
        cols = 1 if len(panels) == 1 else 2
        rows = (len(panels) + cols - 1) // cols
        foot = FOOT_LINE * len(a.number) + (16 if a.number else 0)
        top = TITLE if a.title else 0
        size = (cols * W, top + rows * (BAR + H) + foot)
        canvas = Image.new('RGB', size, BG)
        d = ImageDraw.Draw(canvas)
        if a.title:
            d.text((24, 12), a.title, font=fit_font(d, a.title, bold(), 34, size[0] - 48, 16), fill=FG)
        origins = []
        for i, (im, path, cap) in enumerate(panels):
            x0 = (i % cols) * W
            y0 = top + (i // cols) * (BAR + H)
            d.text((x0 + 24, y0 + 12), cap, font=fit_font(d, cap, bold(), 28, W - 48, 14), fill=FG)
            if i % cols:
                d.line([(x0, y0 + 6), (x0, y0 + BAR - 6)], fill=RULE, width=2)
            canvas.paste(im, (x0, y0 + BAR))
            origins.append((x0, y0 + BAR))
        y = top + rows * (BAR + H) + 8
        for line in a.number:
            d.text((24, y), line, font=fit_font(d, line, FONT_REG, 22, size[0] - 48, 12), fill=FG2)
            y += FOOT_LINE
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        canvas.save(a.out, 'PNG')

        # Honesty check: re-read the file and prove every panel is bit-identical to its source.
        back = Image.open(a.out).convert('RGB')
        if back.size != size:
            raise ValueError(f'output size {back.size}, expected {size}')
        for (im, path, _), (x0, y0) in zip(panels, origins):
            if ImageChops.difference(back.crop((x0, y0, x0 + W, y0 + H)), im).getbbox() is not None:
                back.close()
                os.remove(a.out)
                raise ValueError(f'panel {path} differs from its source render; refusing to keep {a.out}')

        jpg = None
        if a.jpg:
            for q in (90, 85, 80, 75, 70, 65, 60, 55, 50):
                buf = io.BytesIO()
                canvas.save(buf, 'JPEG', quality=q, optimize=True, subsampling=0 if q >= 80 else 2)
                if buf.tell() <= a.jpg_max:
                    os.makedirs(os.path.dirname(os.path.abspath(a.jpg)), exist_ok=True)
                    with open(a.jpg, 'wb') as f:
                        f.write(buf.getvalue())
                    jpg = {'file': a.jpg.replace('\\', '/'), 'quality': q, 'bytes': buf.tell()}
                    break
            if jpg is None:
                raise ValueError(f'JPG does not fit {a.jpg_max} bytes even at quality 50')
    except (ValueError, OSError) as e:
        print(f'composite: {e}', file=sys.stderr)
        return 1

    hashes = {'panels': [{'file': p.replace('\\', '/'), 'caption': c, 'sha256': sha256(p)} for _, p, c in panels],
              'output': {'file': a.out.replace('\\', '/'), 'sha256': sha256(a.out), 'size': list(size)},
              'jpg': jpg, 'numbers': a.number}
    if a.hashes:
        with open(a.hashes, 'w', encoding='utf-8') as f:
            json.dump(hashes, f, indent=1)
    print(json.dumps(hashes, indent=1))
    return 0


if __name__ == '__main__':
    sys.exit(main())
