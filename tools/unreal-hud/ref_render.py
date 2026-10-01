#!/usr/bin/env python
"""Driver for render_reference.sh (plan B section 3, task T0): renders every reference and control image.

Steps: pin the web fonts (fetch_webfonts.py), refresh the scratch site H/HudRef/site, serve it, then run ref_capture.js
through `playwright-cli run-code` in three groups (bases; controls over pair A; controls over pair B), and post-process
the raw renders into the files of section 3:
  ref_hudonly.png (pair A), ref_backdrop.png (pair B), world_layer.png, hud_alpha.png, minimap_photo.png, portrait_figure.png,
  text_off_{A,B}.png, controls/{A,B}/*.png, lcd_vs_gray_text_4x.png, ref_meta.json  (all under H/HudRef/ref/)
Raw renders (the repeat pass, the laser probe, the black/white matte pairs) stay in H/HudRef/ref/raw/.
"""
import hashlib, json, os, shutil, socket, subprocess, sys, time, urllib.request
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
R = Path("D:/Projects/Project_Chimera")
H = Path("D:/Projects/Chimera-Unreal/ChimeraHud")
REF = H / "HudRef" / "ref"
RAW = REF / "raw"
SITE = H / "HudRef" / "site"
WEBFONTS = H / "HudRef" / "webfonts"
R4 = H / "HudRef" / "r4"
TTFS = R / "docs/unreal-move/trial-checks/research/hud-ref/fonts"
ELEMENTS = R / "docs/unreal-move/trial-checks/research/hud-ref/board-3.1-elements.json"
CONFIG = H / "HudRef" / "gray.config.json"
SESSION = "hudref"
CLI = "playwright-cli"
LAUNCH_ARGS = ["--disable-lcd-text", "--font-render-hinting=none"]

STATIC_FACES = [  # family, weight, file  (the 10 static OFL TTFs of hud-ref/fonts)
    ("Cinzel", 500, "Cinzel-Medium.ttf"), ("Cinzel", 600, "Cinzel-SemiBold.ttf"), ("Cinzel", 700, "Cinzel-Bold.ttf"),
    ("Inter", 400, "Inter-Regular.ttf"), ("Inter", 500, "Inter-Medium.ttf"), ("Inter", 600, "Inter-SemiBold.ttf"),
    ("Inter", 700, "Inter-Bold.ttf"),
    ("JetBrains Mono", 400, "JetBrainsMono-Regular.ttf"), ("JetBrains Mono", 500, "JetBrainsMono-Medium.ttf"),
    ("JetBrains Mono", 600, "JetBrainsMono-SemiBold.ttf"),
]
CONTROLS = [  # name, fonts, mods
    ("P1", "static", []), ("P2", "google", ["P2"]), ("P3", "google", ["P3"]), ("P4", "static", ["P2", "P3"]),
] + [(f"N{i}", "google", [f"N{i}"]) for i in range(1, 9)]


def sha_file(p) -> str:
    return hashlib.sha256(Path(p).read_bytes()).hexdigest()


def free_port() -> int:
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


def refresh_site():
    SITE.mkdir(parents=True, exist_ok=True)
    (SITE / "assets").mkdir(exist_ok=True)
    for f in list((R / "docs/ui-redesign").glob("*.html")) + list((R / "docs/ui-redesign").glob("*.js")):
        shutil.copy2(f, SITE / f.name)
    # bf-village.jpg: recovered from the original export zip (r4 section 0.2); same bytes as the saved copy in r4/
    shutil.copy2(R4 / "world-backdrop-bf-village.jpg", SITE / "assets" / "bf-village.jpg")


def cli(*args, check=True, timeout=900):
    r = subprocess.run([CLI, f"-s={SESSION}", *args], capture_output=True, text=True, timeout=timeout, shell=(os.name == "nt"))
    if check and r.returncode != 0:
        sys.exit(f"playwright-cli {' '.join(args)} failed: {r.stdout[-800:]} {r.stderr[-800:]}")
    return r


def run_group(site_url, jobs, tag):
    lock = json.loads((HERE / "webfonts.lock.json").read_text())
    font_map = {e["url"]: str(WEBFONTS / e["file"]) for e in lock["files"]}
    static_css = "\n".join(
        f"@font-face{{font-family:'{fam}';font-style:normal;font-weight:{w};src:url(https://fonts.gstatic.com/_static/{f}) format('truetype');}}"
        for fam, w, f in STATIC_FACES)
    static_map = {f"https://fonts.gstatic.com/_static/{f}": str(TTFS / f) for _, _, f in STATIC_FACES}
    job = {"site": site_url, "googleCss": str(WEBFONTS / "google.css"), "fontMap": font_map,
           "staticCss": static_css, "staticMap": static_map, "jobs": jobs}
    src = (HERE / "ref_capture.js").read_text(encoding="utf-8")
    a, b = src.index("/*JOB*/"), src.index("/*END*/")
    code = src[:a] + json.dumps(job) + src[b + len("/*END*/"):]
    tmp = H / "HudRef" / "tmp"; tmp.mkdir(exist_ok=True)
    f = tmp / f"capture_{tag}.js"
    f.write_text(code, encoding="utf-8")
    r = cli("--raw", "run-code", "--filename", str(f))
    out = r.stdout.strip()
    try:
        data = json.loads(out)
        if isinstance(data, str):
            data = json.loads(data)
    except Exception:
        sys.exit(f"run-code returned non-JSON for group {tag}: {out[-1500:]}")
    for d in data:
        if "error" in d:
            sys.exit(f"capture job {d['name']} failed: {d['error'][:1500]}")
    return data


def base_jobs():
    j = lambda name, **k: {"name": name, "out": str(RAW / f"{name}.png"), "fonts": "google", "world": "show", "bg": None,
                           "textOff": False, "mods": [], **k}
    return [
        j("hudonly", world="hide"), j("backdrop"),
        j("worldonly", world="only"),
        j("alpha_black", world="hide", bg="#000"), j("alpha_white", world="hide", bg="#fff"),
        j("minimap", world="hide", mods=["minimap"]),
        j("figure_black", world="hide", bg="#000", mods=["figure"]), j("figure_white", world="hide", bg="#fff", mods=["figure"]),
        j("text_off_A", world="hide", textOff=True), j("text_off_B", textOff=True),
        j("lasers_off_A", world="hide", mods=["lasers_off"]),
        j("repeat_hudonly", world="hide"), j("repeat_backdrop"),
    ]


def control_jobs(pair):
    world = "hide" if pair == "A" else "show"
    return [{"name": f"ctl_{pair}_{n}", "out": str(REF / "controls" / pair / f"{n}.png"), "fonts": fonts, "world": world,
             "bg": None, "textOff": False, "mods": mods} for n, fonts, mods in CONTROLS]


def alpha_from_pair(black: Path, white: Path):
    """Straight RGBA from a render on #000 and on #fff: alpha = 1 - (W-B)/255, colour = B / alpha."""
    B = np.asarray(Image.open(black).convert("RGB"), dtype=np.float64)
    W = np.asarray(Image.open(white).convert("RGB"), dtype=np.float64)
    a_c = 1.0 - (W - B) / 255.0
    disagree = float((a_c.max(axis=2) - a_c.min(axis=2)).max() * 255.0)
    a = np.clip(a_c.mean(axis=2), 0, 1)
    with np.errstate(divide="ignore", invalid="ignore"):
        rgb = np.where(a[..., None] > 0, B / np.maximum(a[..., None], 1e-6), 0.0)
    rgba = np.dstack([np.clip(np.rint(rgb), 0, 255), np.rint(a * 255)]).astype(np.uint8)
    return Image.fromarray(rgba, "RGBA"), disagree


def lcd_vs_gray():
    """4x nearest-neighbour: old LCD reference vs new grayscale render for the unit name, the stat line and a chip."""
    E = {e["id"]: e for e in json.loads(ELEMENTS.read_text())["elements"]}
    old = Image.open(R4 / "board-3.1a-hud-only-on-14161A-1920x1080.png").convert("RGB")
    new = Image.open(REF / "ref_hudonly.png").convert("RGB")
    samples = [("Unit name (Cinzel 600 20px)", 270, None), ("Stat line, first 24 glyphs (JetBrains Mono 500 12px)", 282, 176),
               ("Gold chip (JetBrains Mono 600 15px + 500 11px)", 9, None)]
    font = ImageFont.truetype(str(TTFS / "Inter-Medium.ttf"), 20)
    small = ImageFont.truetype(str(TTFS / "Inter-Regular.ttf"), 16)
    S, pad = 4, 6
    rows = []
    for title, nid, wcap in samples:
        b = E[nid]["box"]
        x0, y0 = int(b["x"]) - pad, int(b["y"]) - pad
        w = int(b["w"] + 2 * pad) if wcap is None else wcap
        h = int(b["h"] + 2 * pad)
        box = (x0, y0, x0 + w, y0 + h)
        rows.append((title, old.crop(box).resize((w * S, h * S), Image.NEAREST), new.crop(box).resize((w * S, h * S), Image.NEAREST)))
    W = max(r[1].width for r in rows) + 40
    H_ = 24 + sum(34 + r[1].height * 2 + 30 + 22 for r in rows) + 40
    img = Image.new("RGB", (W, H_), (11, 12, 14))
    d = ImageDraw.Draw(img)
    d.text((20, 10), "Reference text: old LCD (colour fringes) vs new grayscale (what Slate draws). 4x, nearest.", fill=(236, 230, 216), font=font)
    y = 48
    for title, o, n in rows:
        d.text((20, y), title, fill=(227, 200, 135), font=font); y += 30
        d.text((20, y), "old: LCD subpixel text (r4 reference)", fill=(154, 149, 138), font=small); y += 20
        img.paste(o, (20, y)); y += o.height + 8
        d.text((20, y), "new: grayscale text (--disable-lcd-text)", fill=(154, 149, 138), font=small); y += 20
        img.paste(n, (20, y)); y += n.height + 14
    img.save(REF / "lcd_vs_gray_text_4x.png")


def main():
    os.chdir(HERE)
    subprocess.run([sys.executable, str(HERE / "fetch_webfonts.py")], check=True)
    for d in (REF, RAW, REF / "controls" / "A", REF / "controls" / "B", H / "HudData" / "Placeholders"):
        d.mkdir(parents=True, exist_ok=True)
    CONFIG.write_text(json.dumps({"browser": {"launchOptions": {"args": LAUNCH_ARGS}}}, indent=1))  # the pinned grayscale-text launch args
    refresh_site()
    port = free_port()
    server = subprocess.Popen([sys.executable, "-m", "http.server", str(port), "--bind", "127.0.0.1"], cwd=SITE,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    site_url = f"http://localhost:{port}"
    try:
        for _ in range(50):
            try:
                if urllib.request.urlopen(site_url + "/Match.dc.html", timeout=2).status == 200:
                    break
            except Exception:
                time.sleep(0.2)
        else:
            sys.exit("site server did not answer 200")
        cli("close", check=False)
        cli("open", "about:blank", f"--config={CONFIG}")
        results = []
        for tag, jobs in (("base", base_jobs()), ("ctlA", control_jobs("A")), ("ctlB", control_jobs("B"))):
            t = time.time()
            results += run_group(site_url, jobs, tag)
            print(f"group {tag}: {len(jobs)} renders in {time.time() - t:.0f}s", flush=True)
    finally:
        cli("close", check=False)
        server.terminate()

    # ---- post-process
    cp = lambda s, d: shutil.copy2(RAW / s, REF / d)
    cp("hudonly.png", "ref_hudonly.png"); cp("backdrop.png", "ref_backdrop.png"); cp("worldonly.png", "world_layer.png")
    cp("text_off_A.png", "text_off_A.png"); cp("text_off_B.png", "text_off_B.png")
    alpha, disagree = alpha_from_pair(RAW / "alpha_black.png", RAW / "alpha_white.png")
    alpha.save(REF / "hud_alpha.png")
    Image.open(RAW / "minimap.png").convert("RGB").crop((53, 882, 53 + 182, 882 + 182)).save(H / "HudData" / "Placeholders" / "minimap_photo.png")
    shutil.copy2(H / "HudData" / "Placeholders" / "minimap_photo.png", REF / "minimap_photo.png")
    E = {e["id"]: e for e in json.loads(ELEMENTS.read_text())["elements"]}
    fb = E[266]["box"]
    fx, fy, fw, fh = int(round(fb["x"])), int(round(fb["y"])), int(round(fb["w"])), int(round(fb["h"]))
    fig, fig_dis = alpha_from_pair(RAW / "figure_black.png", RAW / "figure_white.png")
    fig = fig.crop((fx, fy, fx + fw, fy + fh)); fig.save(REF / "portrait_figure.png")
    shutil.copy2(REF / "portrait_figure.png", H / "HudData" / "Placeholders" / "portrait_figure.png")
    # P5 (Slate model) is not a Chromium render: its input is text_off_{A,B}.png and T2's make_p5.py writes
    # controls/{A,B}/P5.png (regenerated once more if T4a picks a hinting other than None), so T0 writes no P5.png and
    # P5.png is outside the frozen output set. A stale copy from an earlier T0 run is removed.
    for pair in "AB":
        (REF / "controls" / pair / "P5.png").unlink(missing_ok=True)
    lcd_vs_gray()

    # ---- meta
    lock = json.loads((HERE / "webfonts.lock.json").read_text())
    first = results[0]
    renders = []
    for r in results:
        plat = [p for p in r["platform"] if "fonts" in p]
        renders.append({
            "name": r["name"], "file": str(Path(r["out"]).relative_to(H / "HudRef")).replace("\\", "/"),
            "sha256": sha_file(r["out"]), "fonts": r["fonts"], "world": r["world"], "bg": r["bg"], "textOff": r["textOff"],
            "mods": r["mods"], "chromium": r["browserVersion"], "userAgent": r["ua"], "webgl": r["webgl"],
            "nodes": r["info"]["nodes"], "textElements": len(r["info"]["textEls"]),
            # fonts as rendered: asserted after the job's mods and the second document.fonts.ready
            "fontsUsed": f'{sum(f["ok"] for f in r["after"]["fontsUsed"])}/{len(r["after"]["fontsUsed"])}',
            "fontsUsedDetail": r["after"]["fontsUsed"], "fontsUsedBeforeMods": r["info"]["fontsUsed"],
            "fontsStatus": r["after"]["fontsStatus"], "modInfo": r["info"]["mod"],
            "platformFontsCustom": f'{sum(all(x["custom"] for x in p["fonts"]) and len(p["fonts"]) > 0 for p in plat)}/{len(plat)}',
            "platformFonts": plat,
            "servedFontFiles": sorted(set(r["served"])), "unmappedRequests": r["unmapped"], "failedRequests": r["failed"],
            "allAnimationsPausedAt3000": r["after"]["allPaused"], "animationCount": r["after"]["nAnim"],
            "lasers": r["info"]["lasers"] if r["name"] in ("hudonly", "backdrop") else None,
        })
    meta = {
        "board": "3.1a", "chromium": first["browserVersion"], "userAgent": first["ua"], "webglRenderer": first["webgl"],
        "playwrightCli": subprocess.run([CLI, "--version"], capture_output=True, text=True, shell=(os.name == "nt")).stdout.strip(),
        "launchArgs": LAUNCH_ARGS, "viewport": [2200, 1300], "deviceScaleFactor": 1, "animationsPausedAtMs": 3000,
        "site": "H/HudRef/site (copy of docs/ui-redesign + assets/bf-village.jpg)",
        "webfontsLock": {"css_sha256": lock["css_sha256"], "files": {e["file"]: e["sha256"] for e in lock["files"]}},
        "staticFonts": {f: sha_file(TTFS / f) for _, _, f in STATIC_FACES},
        "alphaMatte": {"maxChannelDisagreement_of_255": round(disagree, 3)},
        "figureMatte": {"maxChannelDisagreement_of_255": round(fig_dis, 3), "box": [fx, fy, fw, fh]},
        "renders": renders,
        "outputs": {str(p.relative_to(H / "HudRef")).replace("\\", "/"): sha_file(p) for p in sorted(REF.rglob("*.png")) if "raw" not in p.parts and p.name != "P5.png"},
    }
    (REF / "ref_meta.json").write_text(json.dumps(meta, indent=1))
    print(f"rendered {len(results)} images; ref_meta.json written; alpha matte disagreement {disagree:.2f}/255")


if __name__ == "__main__":
    main()
