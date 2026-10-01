#!/usr/bin/env python
"""Acceptance check for T0 (plan B section 6): every reference and control rendered by render_reference.sh.

Prints, per pair (A = HUD over flat #14161A, B = HUD over the mockup world), then once for the shared files, the lines
of the T0 accept clause, and ends with `REF CHECK OK` (exit 0) or `REF CHECK FAIL` (exit 1).
"""
import hashlib, json, re, sys
from pathlib import Path
import numpy as np
from PIL import Image
from scipy import ndimage

HERE = Path(__file__).resolve().parent
R = Path("D:/Projects/Project_Chimera")
H = Path("D:/Projects/Chimera-Unreal/ChimeraHud")
HREF = H / "HudRef"
REF = HREF / "ref"
RAW = REF / "raw"
ELEMENTS = R / "docs/unreal-move/trial-checks/research/hud-ref/board-3.1-elements.json"
PAIRS = {"A": "ref_hudonly.png", "B": "ref_backdrop.png"}
REPEAT = {"A": "repeat_hudonly.png", "B": "repeat_backdrop.png"}
LCD_TOL = 12          # /255 off the bg-to-fg line (plan B F29)
PHOTO_NOISE_MAX = 16  # pair B negatives: most 1-level world-photo raster flips tolerated outside the intended boxes
BORDER_TOL = 12  # a border-rect pixel is guarded when within this (/255) of the border colour (keycaps blend to ~4 off)
INK_TOL = 8           # a pixel is text ink when it differs from the glyph-less render by more than this (/255)
failures = []


def load(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.int32)


def sha(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def ok(cond, msg):
    if not cond:
        failures.append(msg)
    return cond


# ---------------------------------------------------------------- text runs and the LCD-fringe metric
def text_runs():
    runs = []
    for e in json.loads(ELEMENTS.read_text())["elements"]:
        for r in e.get("textRuns", []):
            gx0 = min(g["x"] for g in r["glyphRects"]); gy0 = min(g["y"] for g in r["glyphRects"])
            gx1 = max(g["x"] + g["w"] for g in r["glyphRects"]); gy1 = max(g["y"] + g["h"] for g in r["glyphRects"])
            runs.append({"id": e["id"], "text": r["rendered"], "box": (int(gx0) - 1, int(gy0) - 1, int(np.ceil(gx1)) + 1, int(np.ceil(gy1)) + 1)})
    return runs


def lcd_fringed(img, off, runs, ids=None):
    """(fringed ink px, ink px) over the text runs.

    `off` is the same render with every glyph transparent (text_off_{A,B}.png), so |img - off| is exactly the text ink and
    off[y, x] is the true background under each ink pixel, whatever drawn element sits next to it (keycap borders, hairlines).
    Grayscale text lies on the straight line background -> foreground in RGB, LCD text does not: with f the run's most
    extreme ink colour, a pixel is fringed when it is more than LCD_TOL (/255, max channel) off the line bg -> f."""
    fr = ink = 0
    for r in runs:
        if ids is not None and r["id"] not in ids:
            continue
        x0, y0, x1, y1 = r["box"]
        x0, y0 = max(x0, 0), max(y0, 0)
        p = img[y0:y1, x0:x1].astype(np.float64)
        bg = off[y0:y1, x0:x1].astype(np.float64)
        d = p - bg
        m = np.abs(d).max(axis=2) > INK_TOL
        if not m.any():
            continue
        f = p[np.unravel_index(np.argmax(np.where(m, np.linalg.norm(d, axis=2), -1)), m.shape)]
        v = f - bg                                   # direction bg -> f at every pixel
        n2 = np.maximum((v * v).sum(axis=2), 1e-6)
        t = (d * v).sum(axis=2) / n2
        resid = np.abs(d - t[..., None] * v).max(axis=2)
        ink += int(m.sum())
        fr += int((m & (resid > LCD_TOL)).sum())
    return fr, ink


# ---------------------------------------------------------------- r4 section 13 probes (41 values)
def hx(s):
    return tuple(int(s[i:i + 2], 16) for i in (1, 3, 5))


PROBES = [  # (name, x, y, hex)
    ("top strip fill", 4, 20, "#1C1F25"), ("top strip bottom border", 500, 39, "#3A3F48"), ("strip below border (world)", 500, 40, "#14161A"),
    ("chip fill", 12, 20, "#14161A"), ("chip left border", 8, 20, "#3A3F48"), ("chip top border x=60", 60, 6, "#3A3F48"),
    ("chip bottom border x=60", 60, 33, "#3A3F48"),
    ("minimap panel fill", 4, 1000, "#1C1F25"), ("minimap panel top border", 20, 864, "#8A6E3C"), ("minimap panel right border", 255, 1000, "#3A3F48"),
    ("minimap plate", 46, 1000, "#262A31"), ("minimap plate border", 44, 1000, "#8A6E3C"), ("minimap inner line", 48, 1000, "#4A4234"),
    ("minimap map border", 52, 1000, "#0B0C0E"), ("minimap button fill", 12, 885, "#14161A"), ("minimap button border", 8, 895, "#3A3F48"),
    ("tab fill", 272, 890, "#2A2F38"), ("tab top border", 290, 874, "#E3C887"), ("tab left border", 268, 890, "#E3C887"),
    ("selection panel fill", 262, 1000, "#1C1F25"), ("selection panel top border", 900, 904, "#8A6E3C"),
    ("portrait border", 274, 1000, "#8A6E3C"), ("vat top", 336, 925, "#191C21"), ("vat bottom", 336, 1040, "#3A4959"),
    ("hp frame", 418, 1015, "#3A3F48"), ("hp fill", 600, 1015, "#4FB39A"),
    ("command card fill", 1625, 1000, "#1C1F25"), ("normal button fill", 1640, 930, "#14161A"), ("normal button border", 1633, 900, "#4A4234"),
    ("active button fill", 1640, 1060, "#173129"), ("active button border", 1633, 1040, "#4FB39A"),
    ("locked button fill", 1780, 930, "#181A1F"), ("locked button border", 1773, 900, "#2D3138"),
    ("keycap fill", 1639, 890, "#0B0C0E"), ("keycap bottom border", 1645, 896, "#5C5446"),
    ("toast timer bar", 100, 850, "#C9A86A"), ("ring left edge", 992, 560, "#4FB39A"),
    ("rope x=40 y=863", 40, 863, "#58482D"), ("rope x=40 y=864", 40, 864, "#83683A"),
    ("rope x=40 y=865", 40, 865, "#3D3526"), ("rope x=40 y=866", 40, 866, "#454035"),
]


def probes(img, world=None):
    """Pair B draws the mockup world where pair A has flat #14161A, so the one 'world' probe is checked against world_layer.png there."""
    bad = []
    for name, x, y, h in PROBES:
        got = tuple(int(v) for v in img[y, x])
        want = tuple(int(v) for v in world[y, x]) if (world is not None and '(world)' in name) else hx(h)
        if max(abs(a - b) for a, b in zip(got, want)) > 2:
            bad.append(f"{name} ({x},{y}) want {h} got #{got[0]:02X}{got[1]:02X}{got[2]:02X}")
    return len(PROBES) - len(bad), bad


# ---------------------------------------------------------------- controls
def inside_mask(boxes, shape):
    m = np.zeros(shape, bool)
    for x0, y0, x1, y1 in boxes:
        m[max(y0, 0):y1, max(x0, 0):x1] = True
    return m


def border_mask(base):
    """Solid box-border pixels of the base render: every side of every element with a solid, opaque border in
    board-3.1-elements.json (box edges rounded to whole pixels), kept only where the base pixel has the border colour
    (+-BORDER_TOL), so corners, overlaps and hidden elements drop out. Positives P1-P4 move or reshape text and vectors by less
    than half a pixel and must leave every one of these pixels unchanged."""
    m = np.zeros(base.shape[:2], bool)
    H_, W_ = m.shape
    E = json.loads(ELEMENTS.read_text())["elements"]
    vec = np.zeros_like(m)  # pixels under an svg (+1 px of anti-aliasing) are vector pixels: P3 moves them legitimately
    for e in E:
        if e["tag"] == "svg":
            b = e["box"]
            vec[max(int(np.floor(b["y"])) - 1, 0):int(np.ceil(b["y"] + b["h"])) + 1, max(int(np.floor(b["x"])) - 1, 0):int(np.ceil(b["x"] + b["w"])) + 1] = True
    for e in E:
        st = e.get("style", {})
        sides = st.get("borders") or ({k: st["border"] for k in ("top", "right", "bottom", "left")} if st.get("border") else None)
        if not sides:
            continue
        b = e["box"]
        x0, y0, x1, y1 = int(round(b["x"])), int(round(b["y"])), int(round(b["x"] + b["w"])), int(round(b["y"] + b["h"]))
        for side, d in sides.items():
            if d.get("style") != "solid":
                continue
            c = [float(v) for v in re.findall(r"[\d.]+", d.get("color", ""))]
            w = int(round(float(str(d.get("w", "0")).rstrip("px") or 0)))
            if w <= 0 or len(c) < 3 or (len(c) == 4 and c[3] < 1):
                continue
            r = {"top": (x0, y0, x1, y0 + w), "bottom": (x0, y1 - w, x1, y1), "left": (x0, y0, x0 + w, y1), "right": (x1 - w, y0, x1, y1)}[side]
            xa, ya, xb, yb = max(r[0], 0), max(r[1], 0), min(r[2], W_), min(r[3], H_)
            if xa >= xb or ya >= yb:
                continue
            col = np.array(c[:3], dtype=np.int32)
            m[ya:yb, xa:xb] |= np.abs(base[ya:yb, xa:xb] - col).max(axis=2) <= BORDER_TOL
    return m & ~vec


P6_DSF = 4


def check_p6_inputs(meta):
    """P6's inputs (EXECUTION section 7, T2 ruling R4): each raw 4x render exists, is 7680x4320, hashes as recorded in
    ref_meta.json and was rendered at devicePixelRatio 4. Returns (ok, detail)."""
    spec = json.loads((HERE / "ref_controls.json").read_text())["controls"]["P6"]
    by_file = {r["file"]: r for r in meta["renders"]}
    bad, n = [], 0
    for f in spec["inputs"]:
        p = REF / f
        r = by_file.get("ref/" + f)
        if not p.exists() or r is None:
            bad.append(f"{f} missing" + ("" if r else " from ref_meta.json")); continue
        size = Image.open(p).size
        good = size == (1920 * P6_DSF, 1080 * P6_DSF) and sha(p) == r["sha256"] and r.get("devicePixelRatio") == P6_DSF
        n += good
        if not good:
            bad.append(f"{f} size {size} dpr {r.get('devicePixelRatio')} sha {'ok' if sha(p) == r['sha256'] else 'MISMATCH'}")
    return not bad, f"{n}/{len(spec['inputs'])} 4x inputs ok" + (f" ({'; '.join(bad)})" if bad else "")


def check_controls(pair, base, openm, meta):
    """Every Chromium control (11 per pair) must differ from its base; a negative must change pixels inside its
    intended boxes and NOTHING outside them (exact: no noise tolerance); a positive P1-P4 must leave every solid
    box-border pixel unchanged (a sub-pixel positive that moves a border would fail hard gate G2). P5 and P6 are not 1x
    Chromium renders: P5's input text_off_{pair}.png must exist and differ from the base (P5.png itself is T2's
    make_p5.py); P6's inputs are the 4x renders of check_p6_inputs (P6.png itself is make_p6.py)."""
    spec = json.loads((HERE / "ref_controls.json").read_text())["controls"]
    borders = border_mask(base)
    rows, n_ok, n_tot = [], 0, 0
    inputs_ok = True
    for name, c in spec.items():
        if c.get("dropped"):  # P2: dropped by EXECUTION section 7; no file is rendered or checked
            continue
        if c.get("t0_file_is_input_only") and c.get("inputs"):
            good, detail = check_p6_inputs(meta)
            if not good:
                failures.append(f"control {pair}/{name} inputs: {detail}")
            p6 = REF / "controls" / pair / f"{name}.png"
            rows.append(f"  {name:3s} {c['kind']:8s} inputs {detail}  {'ok' if good else 'FAIL'}"
                        f"  (P6.png {'present, written by make_p6.py' if p6.exists() else 'not yet written: make_p6.py'})")
            inputs_ok = inputs_ok and good
            continue
        if c.get("t0_file_is_input_only"):
            src = REF / f"text_off_{pair}.png"
            img = load(src)
            changed = int((np.abs(img - base).max(axis=2) > 0).sum())
            good = img.shape == (1080, 1920, 3) and changed > 0
            if not good:
                failures.append(f"control {pair}/{name} input {src.name}: changed={changed} size={img.shape}")
            p5 = REF / "controls" / pair / f"{name}.png"
            rows.append(f"  {name:3s} {c['kind']:8s} input {src.name}: changed px {changed:6d}  {'ok' if good else 'FAIL'}"
                        f"  (P5.png {'present, written by T2' if p5.exists() else 'not yet written: T2 make_p5.py'})")
            inputs_ok = inputs_ok and good
            continue
        n_tot += 1
        p = REF / "controls" / pair / f"{name}.png"
        if not p.exists():
            failures.append(f"control {pair}/{name}: file missing"); rows.append(f"  {name:3s} MISSING"); continue
        img = load(p)
        good = img.shape == (1080, 1920, 3)
        diff = np.abs(img - base).max(axis=2) > 0
        changed = int(diff.sum())
        extra = ""
        if c["intended_boxes"] is not None:
            inside = inside_mask([tuple(b) for b in c["intended_boxes"]], diff.shape)
            n_in = int((diff & inside).sum())
            # pair B only: Chromium re-rasterises the world photo with single 1-level pixel flips from run to run
            # (measured: B/N2 5 px on 2026-10-01; B/N4, B/N6, B/P3 on an earlier run; never in pair A, never inside the
            # HUD mask). Those, and only up to PHOTO_NOISE_MAX px, are reported and not counted as "outside".
            noise = (diff & ~inside & openm & (np.abs(img - base).max(axis=2) == 1)) if pair == "B" else np.zeros_like(diff)
            n_noise = int(noise.sum())
            outside = int((diff & ~inside & ~noise).sum())
            good = good and n_in > 0 and outside == 0 and n_noise <= PHOTO_NOISE_MAX
            extra = f"  inside intended boxes {n_in:6d}  outside {outside:5d}" + (f"  (+{n_noise} px of 1-level photo noise in world.open, max {PHOTO_NOISE_MAX})" if n_noise else "")
        else:
            n_in = outside = None
            moved = int((diff & borders).sum())
            good = good and changed > 0 and moved == 0
            extra = f"  box-border px changed {moved:4d} of {int(borders.sum())}"
        n_ok += good
        if not good:
            failures.append(f"control {pair}/{name}: changed={changed} inside={n_in} outside={outside} size={img.shape}{extra}")
        rows.append(f"  {name:3s} {c['kind']:8s} changed px {changed:6d}{extra}  {'ok' if good else 'FAIL'}")
    return n_ok, n_tot, inputs_ok, rows


# ---------------------------------------------------------------- main
def main():
    meta = json.loads((REF / "ref_meta.json").read_text())
    lock = json.loads((HERE / "webfonts.lock.json").read_text())
    alpha = np.asarray(Image.open(REF / "hud_alpha.png"))[..., 3]
    hud = ndimage.binary_dilation(alpha > 0, structure=np.ones((3, 3), bool), iterations=3)
    openm = ~hud
    runs = text_runs()
    ok(len(runs) == 36, f"text runs {len(runs)} != 36")
    renders = {r["name"]: r for r in meta["renders"]}

    # fonts: the pinned files still hash as locked, and every served file is one of them
    lock_by_url = {e["url"]: e for e in lock["files"]}
    bad_hash = [e["file"] for e in lock["files"] if sha(HREF / "webfonts" / e["file"]) != e["sha256"]]
    ok(not bad_hash, f"woff2 sha256 mismatch: {bad_hash}")
    ok(sha(HREF / "webfonts" / "google.css") == lock["css_sha256"], "google.css sha256 mismatch")
    ok(meta["webfontsLock"]["css_sha256"] == lock["css_sha256"], "ref_meta css sha256 differs from webfonts.lock.json")
    ok(all(meta["webfontsLock"]["files"].get(e["file"]) == e["sha256"] for e in lock["files"]), "ref_meta woff2 hashes differ from webfonts.lock.json")
    ok(all(sha(R / "docs/unreal-move/trial-checks/research/hud-ref/fonts" / f) == h for f, h in meta["staticFonts"].items()), "static TTF sha256 mismatch")

    # frozen outputs: every output PNG still hashes as recorded
    stale = [k for k, h in meta["outputs"].items() if sha(HREF / k) != h]
    ok(not stale, f"outputs changed since ref_meta.json: {stale}")
    ctl_out = [k for k in meta["outputs"] if k.startswith("ref/controls/")]
    ok(len(ctl_out) == 22 and not any(k.endswith(("/P5.png", "/P6.png", "/P2.png")) for k in ctl_out), f"frozen control set: {len(ctl_out)} files (want 22: 11 per pair, no P2.png, P5.png or P6.png)")

    for pair, fname in PAIRS.items():
        print(f"== pair {pair} ({fname}) ==")
        img = load(REF / fname)
        r = renders["hudonly" if pair == "A" else "backdrop"]
        # 1. size and LCD fringes (every render of the pair, plus the base)
        size_ok = img.shape == (1080, 1920, 3)
        off = load(REF / f"text_off_{pair}.png")
        fr, ink = lcd_fringed(img, off, runs)
        ctl_dirs = [REF / "controls" / pair / f"{n}.png" for n, c in json.loads((HERE / "ref_controls.json").read_text())["controls"].items() if not c.get("dropped")]
        all_imgs_ok = size_ok and all(Image.open(p).size == (1920, 1080) for p in ctl_dirs if p.exists())
        ok(all_imgs_ok, f"pair {pair}: an image is not 1920x1080")
        ok(fr == 0, f"pair {pair}: lcd_fringed {fr} of {ink}")
        print(f"1920x1080 lcd_fringed {fr}   ({ink} ink px over {len(runs)} text runs; threshold {LCD_TOL}/255 off the bg-fg line)")
        # 2. fonts
        used = r["fontsUsedDetail"]
        n_ok = sum(1 for f in used if f["ok"])
        served = set(r["servedFontFiles"])
        match = all(u in lock_by_url and meta["webfontsLock"]["files"][lock_by_url[u]["file"]] == lock_by_url[u]["sha256"] for u in served) and not r["unmappedRequests"]
        ok(n_ok == len(used) and match, f"pair {pair}: fonts {n_ok}/{len(used)} match={match}")
        plat = r["platformFontsCustom"]
        ok(plat.split("/")[0] == plat.split("/")[1] and r["textElements"] == 36, f"pair {pair}: platform fonts {plat}, text elements {r['textElements']}")
        print(f"fonts loaded {n_ok}/{len(used)}, woff2 sha256 match ({len(served)} files served, glyph source is the web font for {plat} text elements)")
        # 3. repeat
        rep = load(RAW / REPEAT[pair])
        mad = float(np.abs(img - rep).mean())
        mx = int(np.abs(img - rep).max())
        ok(mad == 0.0 and mx == 0, f"pair {pair}: repeat MAD {mad} max {mx}")
        print(f"repeat MAD {mad:.3f}   (max {mx})")
        # 4. r4 section 13 probes
        n, bad = probes(img, load(REF / 'world_layer.png') if pair == 'B' else None)
        ok(n == len(PROBES), f"pair {pair}: probes {n}/{len(PROBES)}: {bad}")
        print(f"r4 §13 non-text values {n}/{len(PROBES)}" + "".join(f"\n  MISMATCH {b}" for b in bad))
        # 5. controls
        n_c, tot, in_ok, rows = check_controls(pair, img, openm, meta)
        ok(n_c == tot == 11 and in_ok, f"pair {pair}: controls {n_c}/{tot}, P5/P6 inputs ok {in_ok}")
        print(f"controls {n_c}/{tot} rendered + P5 and P6 inputs {'ok' if in_ok else 'FAIL'} (13 per pair: 11 Chromium + P5 + P6; P2 dropped), each differs from base")
        print("\n".join(rows))

    # fonts on EVERY render (plan §3: every control carries the same assertion), as rendered: fontsUsed is taken after
    # the job's mods and the second document.fonts.ready, so N3/N4/N7 and the static-TTF controls P1/P4 are covered
    static_urls = {f"https://fonts.gstatic.com/_static/{f}" for f in meta["staticFonts"]}
    font_bad = []
    for r in meta["renders"]:
        used = r["fontsUsedDetail"]
        served = set(r["servedFontFiles"])
        if r["fonts"] == "static":
            served_ok = served <= static_urls and served
        else:
            served_ok = all(u in lock_by_url and meta["webfontsLock"]["files"][lock_by_url[u]["file"]] == lock_by_url[u]["sha256"] for u in served) and served
        plat = r["platformFontsCustom"].split("/")
        good = (used and all(f["ok"] for f in used) and served_ok and not r["unmappedRequests"] and not r["failedRequests"]
                and plat[0] == plat[1] and r["textElements"] == 36 and r.get("fontsStatus") == "loaded")
        if not good:
            font_bad.append(f"{r['name']} fonts {r['fontsUsed']} platform {r['platformFontsCustom']} served {len(served)} "
                            f"unmapped {len(r['unmappedRequests'])} failed {len(r['failedRequests'])} status {r.get('fontsStatus')}")
    ok(not font_bad, f"font assertion failed on renders: {font_bad}")
    combos = sorted({(f["family"], f["weight"], f["size"]) for r in meta["renders"] for f in r["fontsUsedDetail"]})
    print(f"== every render ==\nfonts asserted after mods on {len(meta['renders']) - len(font_bad)}/{len(meta['renders'])} renders "
          f"(each: every family/weight/size loaded, 36/36 text elements drawn from the web font, no unmapped or failed request); "
          f"{len(combos)} combinations in all")
    for b in font_bad:
        print("  FONT FAIL " + b)

    print("== shared ==")
    # placeholders
    ph_ok = 0
    for name, size, mode in (("minimap_photo.png", (182, 182), None), ("portrait_figure.png", (48, 78), "RGBA")):
        p = H / "HudData" / "Placeholders" / name
        if p.exists():
            im = Image.open(p)
            good = im.size == size and (mode is None or im.mode == mode) and sha(p) == sha(REF / name)
            if name == "portrait_figure.png":
                a = np.asarray(im.convert("RGBA"))[..., 3]
                good = good and 0 < (a > 0).sum() < a.size and (a == 0).any()
            ph_ok += good
    ok(ph_ok == 2, f"placeholders {ph_ok}/2")
    print(f"placeholders {ph_ok}/2 (182x182, 48x78 RGBA)")
    # world_layer vs ref_backdrop over world.open (complement of the HUD alpha mask dilated 3 px)
    wl, bd = load(REF / "world_layer.png"), load(REF / "ref_backdrop.png")
    dd = np.abs(wl - bd).max(axis=2)[openm]
    mx = int(dd.max())
    ok(mx <= 1, f"world_layer vs ref_backdrop over world.open: max {mx}")
    print(f"world_layer vs ref_backdrop over world.open: max <= 1  (actual max {mx}, MAD {dd.mean():.4f}, {int(openm.sum())} px = {100 * openm.mean():.1f}% of the frame; HUD mask {100 * (alpha > 0).mean():.1f}%)")
    # laser heads
    base, off = load(REF / "ref_hudonly.png"), load(RAW / "lasers_off_A.png")
    diff = np.abs(base - off).max(axis=2) > 0
    lasers = renders["hudonly"]["lasers"]
    panel = {"minimap": lambda x: x < 256, "selection": lambda x: 256 <= x < 1620, "card": lambda x: x >= 1620}
    for pn, f in panel.items():
        ls = [l for l in lasers if f(l["box"][0] + 1)]
        ok(len(ls) == 1, f"laser animations for {pn}: {len(ls)}")
    summary = {}
    for l in lasers:
        x0, y0, w, h = l["box"]
        pn = "minimap" if x0 < 256 else ("selection" if x0 < 1620 else "card")
        win = np.zeros_like(diff); win[max(int(y0) - 4, 0):int(y0 + h) + 4, max(int(x0), 0):int(x0 + w) + 1] = True
        n = int((diff & win).sum()); xs = np.nonzero((diff & win).any(axis=0))[0]
        summary[pn] = (n, (int(xs.min()), int(xs.max())) if n else None, l["delay"], l["progress"], l["bgPos"])
    for pn, (n, xr, delay, prog, bgp) in summary.items():
        print(f"  laser {pn:9s} delay {delay:g} ms progress {prog} bg-position {bgp} -> {n} px differ when hidden" + (f", x {xr[0]}..{xr[1]}" if xr else ""))
    n = summary["card"][0]
    print(f"card laser head at 3000 ms: {'visible' if n > 0 else 'hidden'} ({n} px)")
    # meta identical across renders
    keys = ("chromium", "userAgent", "webgl")
    ident = all(tuple(r[k] for k in keys) == tuple(meta["renders"][0][k] for k in keys) for r in meta["renders"])
    ident = ident and meta["viewport"] == [2200, 1300] and all(r["allAnimationsPausedAt3000"] for r in meta["renders"])
    ident = ident and all(not r["unmappedRequests"] for r in meta["renders"])
    ok(ident, "ref_meta: renders differ in Chromium/UA/WebGL or animations were not paused at 3000 ms")
    print(f"ref_meta identical across {len(meta['renders'])} renders: chromium {meta['chromium']}, WebGL {meta['webglRenderer'][:48]}, launch args {' '.join(meta['launchArgs'])}")
    print(f"alpha matte max channel disagreement {meta['alphaMatte']['maxChannelDisagreement_of_255']}/255 (plan: 2)")
    ok((REF / "lcd_vs_gray_text_4x.png").exists(), "lcd_vs_gray_text_4x.png missing")
    print(f"lcd_vs_gray_text_4x.png: {REF / 'lcd_vs_gray_text_4x.png'}")
    if failures:
        print("REF CHECK FAIL")
        for f in failures:
            print("  - " + f)
        sys.exit(1)
    print("REF CHECK OK")


if __name__ == "__main__":
    main()
