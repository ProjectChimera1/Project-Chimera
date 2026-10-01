"""Side-by-side composite of two look-test renders (PLAN section 11.1). Needs Pillow.

    python composite.py --left out/game_A_r2.png --right out/game_B_r2.png --out out/composite_gameplay.png \
        --left-label "A · Manor-Lords-style · Lumen GI+refl, VSM, TSR · median 82.0 fps (1% low 76.1)" \
        --right-label "B · Northgard-style · no GI, SSR, VSM, TSR · median 130.3 fps (1% low 116.7)" \
        --kind gameplay

Both inputs must be exactly 1920x1080. The output is 3840x1144: a 64 px label bar on top (two lines per
half: the label you pass, then the shared sub-line), then the two renders pasted pixel for pixel. The
renders are never resized, filtered or retouched; after saving, the output is re-read and each half is
compared with its source, and the script fails if a single pixel differs. SHA-256 of both sources is printed
(and written with --hashes) so results.json can record them.

--kind gameplay|close picks the default sub-line ("close" says editor render); --sub overrides it.
Exit 1 on any problem (missing file, wrong size, label too long for its half, pixel mismatch).
"""
import argparse
import hashlib
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

W, H = 1920, 1080
BAR = 64
FONT_REG = 'C:/Windows/Fonts/arial.ttf'
FONT_BOLD = 'C:/Windows/Fonts/arialbd.ttf'
BG, FG, FG2, RULE = (22, 22, 26), (245, 245, 245), (170, 175, 185), (90, 94, 104)
SUB = {
    'gameplay': 'Same models, ground, trees, camera · engine output, unretouched · RTX 3060 1080p, UE 5.8.3 -game (uncooked)',
    'close': 'Same models, ground, trees, camera · editor render, unretouched · RTX 3060 1080p, UE 5.8.3 editor (uncooked)',
}


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
    return im.convert('RGB')  # drops an opaque alpha channel; colour values are unchanged


def fit_font(draw, text, path, size, max_w, min_size):
    """Largest font <= size whose rendering of text fits max_w; raise if even min_size does not fit."""
    while size >= min_size:
        font = ImageFont.truetype(path, size)
        if draw.textlength(text, font=font) <= max_w:
            return font
        size -= 1
    raise ValueError(f'text does not fit in {max_w}px even at {min_size}pt: {text!r}')


def draw_label(draw, x0, line1, line2):
    """Draw the two label lines inside the half starting at x0."""
    pad = 24
    max_w = W - 2 * pad
    f1 = fit_font(draw, line1, FONT_BOLD if os.path.isfile(FONT_BOLD) else FONT_REG, 26, max_w, 16)
    f2 = fit_font(draw, line2, FONT_REG, 18, max_w, 12)
    draw.text((x0 + pad, 6), line1, font=f1, fill=FG)
    draw.text((x0 + pad, 38), line2, font=f2, fill=FG2)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--left', required=True)
    ap.add_argument('--right', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--left-label', required=True)
    ap.add_argument('--right-label', required=True)
    ap.add_argument('--kind', choices=sorted(SUB), default='gameplay')
    ap.add_argument('--sub', help='override the shared second line')
    ap.add_argument('--hashes', metavar='PATH', help='write source SHA-256s as JSON')
    a = ap.parse_args()

    try:
        left, right = load_render(a.left), load_render(a.right)
        if not os.path.isfile(FONT_REG):
            raise ValueError(f'font not found: {FONT_REG}')
        sub = a.sub or SUB[a.kind]
        canvas = Image.new('RGB', (2 * W, H + BAR), BG)
        d = ImageDraw.Draw(canvas)
        draw_label(d, 0, a.left_label, sub)
        draw_label(d, W, a.right_label, sub)
        d.line([(W, 6), (W, BAR - 6)], fill=RULE, width=2)  # divider inside the bar only
        canvas.paste(left, (0, BAR))
        canvas.paste(right, (W, BAR))
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        canvas.save(a.out, 'PNG')

        # Honesty check: re-read the file and prove both halves are bit-identical to their sources.
        back = Image.open(a.out).convert('RGB')
        if back.size != (2 * W, H + BAR):
            raise ValueError(f'output size {back.size}, expected {(2 * W, H + BAR)}')
        for name, src, x0 in (('left', left, 0), ('right', right, W)):
            diff = ImageChops.difference(back.crop((x0, BAR, x0 + W, BAR + H)), src)
            if diff.getbbox() is not None:
                back.close()
                os.remove(a.out)
                raise ValueError(f'{name} half differs from its source render; refusing to keep {a.out}')
    except (ValueError, OSError) as e:
        print(f'composite: {e}', file=sys.stderr)
        return 1

    hashes = {'left': {'file': a.left.replace('\\', '/'), 'sha256': sha256(a.left)},
              'right': {'file': a.right.replace('\\', '/'), 'sha256': sha256(a.right)},
              'output': {'file': a.out.replace('\\', '/'), 'sha256': sha256(a.out), 'size': [2 * W, H + BAR]}}
    if a.hashes:
        with open(a.hashes, 'w', encoding='utf-8') as f:
            json.dump(hashes, f, indent=1)
    print(json.dumps(hashes, indent=1))
    return 0


if __name__ == '__main__':
    sys.exit(main())
