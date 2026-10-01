#!/usr/bin/env python3
"""make_svgs.py: the HUD's icon and ornament files, generated from the mockup's own markup (plan B 2.5, task T4a).

    python make_svgs.py            # SVGs + index + manifest (no browser)
    python make_svgs.py --png      # also the PNG route: Chromium rasterises the same SVGs at the exact on-screen sizes
    python make_svgs.py --check    # regenerate in memory and compare with the files on disk (exit 1 on any difference)

Inputs: research/hud-ref/board-3.1-elements.json  iconsUsed[].markup (18 icons of icons.js), ornaments.ropeTileSvg_16x8_repeat-x,
ornaments.sigils[].markup (the sigil wrappers; their distinct markups are the centre-mark variants).
Outputs under H/HudData/:
  Icons/<name>.svg           24x24 viewBox, white strokes (currentColor and the root's own stroke colour -> #FFFFFF; tinted at
                             draw time), presentation attributes moved from <svg> onto a <g> because nanosvg reads them only on
                             shapes and groups, not on the root (UE 5.8 ThirdParty/nanosvg, nsvg__parseSVG)
  Icons/<name>_<size>.png    --png: Chromium's raster of the same markup at every on-screen size the board uses (12/14/16/24),
                             white on transparent (straight alpha), placed at integer offsets as Blink snaps an <svg> root
  Ornaments/rope_tile.svg (16x8), Ornaments/sigil_<k>.svg (14 px, own colours) and their --png rasters
  Icons/index.json, Ornaments/index.json   name -> sizes, source element ids
  manifest.json              sha256 + bytes of every generated file (the shot script's allow-list, hud_shot_check.py)
Nothing here is cropped from a reference image: every pixel of the PNG route is Chromium rendering our generated SVG.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from hud_common import ELEMENTS_JSON, H  # noqa: E402

DATA = H / "HudData"
ICONS = DATA / "Icons"
ORN = DATA / "Ornaments"
MANIFEST = DATA / "manifest.json"
SVG_NS = "http://www.w3.org/2000/svg"
PRESENTATION = ("fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin")
CLI = "playwright-cli"
SESSION = "hudsvg"
CONFIG = H / "HudRef" / "gray.config.json"


def _strip_ns(tag: str) -> str:
    return tag.split("}", 1)[-1]


def _shape_xml(el: ET.Element) -> str:
    """Re-serialise a primitive (and its children) without namespaces, attributes in source order."""
    attrs = "".join(f' {k}="{v}"' for k, v in el.attrib.items())
    kids = "".join(_shape_xml(c) for c in el)
    tag = _strip_ns(el.tag)
    return f"<{tag}{attrs}>{kids}</{tag}>" if kids else f"<{tag}{attrs}/>"


def icon_svg(markup: str) -> str:
    """White 24x24 icon SVG from one iconsUsed markup."""
    root = ET.fromstring(markup)
    g = {k: root.attrib[k] for k in PRESENTATION if k in root.attrib}
    g["stroke"] = "#FFFFFF"                     # tinted at draw time (currentColor, or the root's own colour)
    gattrs = "".join(f' {k}="{v}"' for k, v in g.items())
    body = "".join(_shape_xml(c) for c in root)
    vb = root.attrib.get("viewBox", "0 0 24 24")
    return (f'<svg xmlns="{SVG_NS}" width="24" height="24" viewBox="{vb}"><g{gattrs}>{body}</g></svg>\n')


def ornament_svg(markup: str, w: int, h: int) -> str:
    """An ornament SVG keeps its own colours; only the root is normalised (namespace, size, no CSS style)."""
    root = ET.fromstring(markup.replace("'", '"'))
    vb = root.attrib.get("viewBox", f"0 0 {w} {h}")
    g = {k: root.attrib[k] for k in PRESENTATION if k in root.attrib}
    gattrs = "".join(f' {k}="{v}"' for k, v in g.items())
    body = "".join(_shape_xml(c) for c in root)
    inner = f"<g{gattrs}>{body}</g>" if gattrs else body
    return f'<svg xmlns="{SVG_NS}" width="{w}" height="{h}" viewBox="{vb}">{inner}</svg>\n'


def build(elements: dict) -> tuple[dict[str, str], dict, dict, list[dict]]:
    """Returns ({relpath: svg text}, icon index, ornament index, png jobs)."""
    files: dict[str, str] = {}
    icon_index: dict[str, dict] = {}
    jobs: list[dict] = []
    for use in elements["iconsUsed"]:
        name, size = use["icon"], int(use["sizePx"])
        svg = icon_svg(use["markup"])
        rel = f"Icons/{name}.svg"
        if rel in files and files[rel] != svg:
            sys.exit(f"icon {name}: two different markups in iconsUsed")
        files[rel] = svg
        ent = icon_index.setdefault(name, {"svg": rel, "sizes": [], "elements": []})
        if size not in ent["sizes"]:
            ent["sizes"].append(size)
        ent["elements"].append(use["elementId"])
    for name, ent in sorted(icon_index.items()):
        ent["sizes"].sort()
        for s in ent["sizes"]:
            jobs.append({"name": f"{name}_{s}", "out": str(ICONS / f"{name}_{s}.png"), "svg": files[ent["svg"]], "w": s, "h": s})
    orn = elements["ornaments"]
    files["Ornaments/rope_tile.svg"] = ornament_svg(orn["ropeTileSvg_16x8_repeat-x"], 16, 8)
    orn_index: dict = {"rope_tile": {"svg": "Ornaments/rope_tile.svg", "size": [16, 8]}, "sigils": []}
    jobs.append({"name": "rope_tile", "out": str(ORN / "rope_tile.png"), "svg": files["Ornaments/rope_tile.svg"], "w": 16, "h": 8})
    variants: list[str] = []
    for s in orn["sigils"]:
        m = s["markup"]
        if m not in variants:
            variants.append(m)
        k = variants.index(m)
        orn_index["sigils"].append({"element": s["elementId"], "frame": s["frame"], "box": s["box"], "variant": k})
    for k, m in enumerate(variants):
        w = int(round(float(ET.fromstring(m).attrib.get("width", "14"))))
        rel = f"Ornaments/sigil_{k}.svg"
        files[rel] = ornament_svg(m, w, w)
        jobs.append({"name": f"sigil_{k}", "out": str(ORN / f"sigil_{k}.png"), "svg": files[rel], "w": w, "h": w})
    orn_index["variants"] = len(variants)
    return files, icon_index, orn_index, jobs


def sha(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def render_pngs(jobs: list[dict]) -> None:
    """Chromium (playwright-cli, the T0 grayscale launch args) rasterises each SVG inline at its size, white on transparent."""
    html = ["<!doctype html><html><head><style>html,body{margin:0;background:transparent;color:#fff}"
            ".c{position:absolute}svg{display:block}</style></head><body>"]
    y = 0
    for i, j in enumerate(jobs):
        svg = j["svg"].replace('width="24" height="24"', f'width="{j["w"]}" height="{j["h"]}"', 1)
        html.append(f'<div class="c" id="j{i}" style="left:0px;top:{y}px;width:{j["w"]}px;height:{j["h"]}px">{svg}</div>')
        y += j["h"] + 4
    html.append("</body></html>")
    page = "".join(html)
    shots = [{"sel": f"#j{i}", "out": j["out"]} for i, j in enumerate(jobs)]
    code = ("async (page) => { await page.setViewportSize({width: 64, height: %d}); await page.setContent(%s);"
            " const out = []; for (const s of %s) { await page.locator(s.sel).screenshot({path: s.out, omitBackground: true});"
            " out.push(s.out); } return JSON.stringify(out); }") % (y + 8, json.dumps(page), json.dumps(shots))
    tmp = H / "HudRef" / "tmp"
    tmp.mkdir(parents=True, exist_ok=True)
    f = tmp / "make_svgs_png.js"
    f.write_text(code, encoding="utf-8")
    run = lambda *a, check=True: subprocess.run([CLI, f"-s={SESSION}", *a], capture_output=True, text=True, timeout=600,
                                                shell=(os.name == "nt"), check=False)
    run("close")
    r = run("open", "about:blank", f"--config={CONFIG}")
    if r.returncode != 0:
        sys.exit(f"playwright-cli open failed: {r.stdout[-600:]} {r.stderr[-600:]}")
    try:
        r = run("--raw", "run-code", "--filename", str(f))
        if r.returncode != 0:
            sys.exit(f"playwright-cli run-code failed: {r.stdout[-800:]} {r.stderr[-800:]}")
    finally:
        run("close")
    for j in jobs:
        if not Path(j["out"]).is_file():
            sys.exit(f"PNG not written: {j['out']}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--png", action="store_true", help="also rasterise the PNG route with Chromium")
    ap.add_argument("--check", action="store_true", help="compare a fresh generation with the files on disk")
    a = ap.parse_args()
    elements = json.loads(Path(ELEMENTS_JSON).read_text(encoding="utf-8"))
    files, icon_index, orn_index, jobs = build(elements)
    files["Icons/index.json"] = json.dumps(icon_index, indent=1, sort_keys=True) + "\n"
    files["Ornaments/index.json"] = json.dumps(orn_index, indent=1, sort_keys=True) + "\n"
    if a.check:
        bad = [rel for rel, txt in files.items() if not (DATA / rel).is_file() or (DATA / rel).read_bytes() != txt.encode("utf-8")]
        mf = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.is_file() else {"files": {}}
        for rel, ent in mf.get("files", {}).items():
            p = DATA / rel
            if not p.is_file() or sha(p.read_bytes()) != ent["sha256"]:
                bad.append(f"manifest:{rel}")
        print(f"MAKE_SVGS {'OK' if not bad else 'STALE'}: {len(files)} generated files, manifest {len(mf.get('files', {}))} entries"
              + (f"; differ: {bad}" if bad else ""))
        return 0 if not bad else 1
    for rel, txt in files.items():
        p = DATA / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(txt.encode("utf-8"))
    if a.png:
        render_pngs(jobs)
    # manifest: every generated file present on disk (PNGs only once the --png route has been rendered)
    entries = {}
    for rel in sorted(files):
        b = (DATA / rel).read_bytes()
        entries[rel] = {"sha256": sha(b), "bytes": len(b)}
    for j in jobs:
        p = Path(j["out"])
        if p.is_file():
            rel = p.relative_to(DATA).as_posix()
            b = p.read_bytes()
            entries[rel] = {"sha256": sha(b), "bytes": len(b)}
    MANIFEST.write_text(json.dumps({"generator": "tools/unreal-hud/make_svgs.py", "source": "research/hud-ref/board-3.1-elements.json",
                                    "source_sha256": sha(Path(ELEMENTS_JSON).read_bytes()), "files": entries},
                                   indent=1, sort_keys=True) + "\n", encoding="utf-8")
    n_png = sum(1 for k in entries if k.endswith(".png"))
    print(f"MAKE_SVGS wrote {len(icon_index)} icons, {orn_index['variants']} sigil variants + rope tile, {n_png} PNGs; "
          f"manifest {len(entries)} files ({MANIFEST})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
