"""Shared helpers for the Match HUD (board 3.1a) comparison toolkit.

Used by make_regions.py, hud_compare.py, text_drift.py and the tests. Pure Python + numpy + Pillow + scipy.
Paths: T = this folder (R/tools/unreal-hud), R = repo root, H = D:/Projects/Chimera-Unreal/ChimeraHud.
"""
from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import re
from pathlib import Path

import numpy as np
from PIL import Image

W, H_PX = 1920, 1080

T = Path(__file__).resolve().parent
R = T.parent.parent
U = Path("D:/Projects/Chimera-Unreal")
H = U / "ChimeraHud"
HUDREF = H / "HudRef"
TRIAL = R / "docs" / "unreal-move" / "trial-checks"
ELEMENTS_JSON = TRIAL / "research" / "hud-ref" / "board-3.1-elements.json"
FONT_DIRS = [H / "HudData" / "Fonts", TRIAL / "research" / "hud-ref" / "fonts"]
REGIONS_JSON = T / "regions.json"
THRESHOLDS_JSON = T / "thresholds.json"
CALIBRATION_JSON = T / "results" / "calibration.json"
CONVERGENCE_CSV = T / "results" / "convergence.csv"

# Pixel classes (one per pixel, see make_regions.py)
FLAT, TEXT, VECTOR, PLACEHOLDER = 0, 1, 2, 3
CLASS_NAMES = ["flat", "text", "vector", "placeholder"]

R4_REF_NAME = "board-3.1a-hud-only-on-14161A-1920x1080.png"
R4_ALPHA_NAME = "board-3.1a-hud-only-alpha-1920x1080.png"


def _first_existing(cands):
    for c in cands:
        if c and Path(c).is_file():
            return Path(c)
    return None


def find_ref(pair: str = "A") -> Path | None:
    """Development reference for pair A (flat #14161A) or B (with the world backdrop). T0's refs win over r4's."""
    if pair == "A":
        return _first_existing([os.environ.get("HUD_REF"), HUDREF / "ref" / "ref_hudonly.png",
                                HUDREF / "r4" / R4_REF_NAME, TRIAL / "research" / "hud-ref" / R4_REF_NAME])
    return _first_existing([os.environ.get("HUD_REF_B"), HUDREF / "ref" / "ref_backdrop.png",
                            HUDREF / "r4" / "board-3.1a-with-backdrop-1920x1080.png"])


def find_alpha() -> Path | None:
    return _first_existing([os.environ.get("HUD_ALPHA"), HUDREF / "ref" / "hud_alpha.png",
                            HUDREF / "r4" / R4_ALPHA_NAME, TRIAL / "research" / "hud-ref" / R4_ALPHA_NAME])


def sha256_file(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def load_rgb(path) -> np.ndarray:
    """Load a PNG as uint8 (H, W, 3); alpha is dropped (captures are opaque)."""
    im = Image.open(path)
    return np.array(im.convert("RGB"))


def load_alpha(path) -> np.ndarray:
    im = Image.open(path)
    if im.mode != "RGBA":
        raise ValueError(f"{path}: expected an RGBA image, got {im.mode}")
    return np.array(im)[..., 3]


def png_b64(arr: np.ndarray) -> str:
    buf = io.BytesIO()
    Image.fromarray(arr).save(buf, format="PNG", compress_level=9)
    return base64.b64encode(buf.getvalue()).decode("ascii")


def b64_png(s: str) -> np.ndarray:
    return np.array(Image.open(io.BytesIO(base64.b64decode(s))))


def parse_color(s: str):
    """'rgb(1, 2, 3)' or 'rgba(1, 2, 3, 0.5)' -> (r, g, b, a). None when s is falsy."""
    if not s:
        return None
    m = re.match(r"rgba?\(([^)]*)\)", s)
    if not m:
        return None
    p = [x.strip() for x in m.group(1).split(",")]
    r, g, b = int(float(p[0])), int(float(p[1])), int(float(p[2]))
    a = float(p[3]) if len(p) > 3 else 1.0
    return (r, g, b, a)


def snap(v: float) -> int:
    """Chromium pixel snapping: half pixels round up."""
    return int(np.floor(v + 0.5))


def snapped_box(b) -> tuple[int, int, int, int]:
    """Layout box {x,y,w,h} -> (x0, y0, x1, y1) exclusive, snapped like Chromium (round each edge)."""
    return (snap(b["x"]), snap(b["y"]), snap(b["x"] + b["w"]), snap(b["y"] + b["h"]))


def load_elements() -> dict:
    return json.load(open(ELEMENTS_JSON, encoding="utf-8"))


def luma(rgb: np.ndarray) -> np.ndarray:
    """Rec.601 luma, float64."""
    a = rgb.astype(np.float64)
    return 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]


def file_sha_or_none(p):
    try:
        return sha256_file(p)
    except OSError:
        return None


# Code the frozen thresholds depend on (the hard gates, the soft metrics and the Slate text model). calibrate.py records
# their hashes in calibration.json "code_sha256"; check_calibration reports MISMATCH when one has changed since.
CALIBRATED_CODE = ("hud_common.py", "hud_compare.py", "ssim.py", "make_p5.py", "make_p6.py")


def code_sha256(p) -> str:
    """sha256 of a source file with CRLF normalised to LF, so a git checkout under core.autocrlf does not change it."""
    return hashlib.sha256(Path(p).read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def calibrated_code_hashes(root=T) -> dict:
    return {n: code_sha256(Path(root) / n) for n in CALIBRATED_CODE}


def check_calibration(thresholds_path=THRESHOLDS_JSON, regions_path=REGIONS_JSON, refs=None,
                      calibration_path=CALIBRATION_JSON):
    """Compare the hashes frozen by T2's calibration (results/calibration.json) with the files in use.

    calibration.json schema (written by calibrate.py): {"thresholds_sha256": hex, "regions_sha256": hex,
    "refs_sha256": {"A": hex, "B": hex}, "code_sha256": {file: hex} (optional), ...}. Returns (status, problems): status is "UNCALIBRATED" (no
    calibration.json), "OK" or "MISMATCH". refs maps pair -> reference PNG path (only the pairs in use).
    """
    if not Path(calibration_path).is_file():
        return "UNCALIBRATED", []
    cal = json.load(open(calibration_path, encoding="utf-8"))
    problems = []
    for key, p in (("thresholds_sha256", thresholds_path), ("regions_sha256", regions_path)):
        want, got = cal.get(key), file_sha_or_none(p)
        if want != got:
            problems.append(f"{key}: calibration {str(want)[:12]} vs file {str(got)[:12]}")
    for pair, p in (refs or {}).items():
        want, got = (cal.get("refs_sha256") or {}).get(pair), file_sha_or_none(p)
        if want != got:
            problems.append(f"ref {pair}: calibration {str(want)[:12]} vs file {str(got)[:12]}")
    for name, want in (cal.get("code_sha256") or {}).items():
        try:
            got = code_sha256(T / name)
        except OSError:
            got = None
        if want != got:
            problems.append(f"code {name}: calibration {str(want)[:12]} vs file {str(got)[:12]}")
    return ("MISMATCH" if problems else "OK"), problems
