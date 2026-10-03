"""pytest for the scatter generator's constants (plan C scatter 3.1, task S2): the generated TerrainLookShared.h, the integer slope thresholds in
the palette, the Python reference of the integer math against its golden file and against the Mix32 golden of the mesh generator.
Run: python -m pytest Tools/test_scatter_constants.py -q
"""
import math
import os
import re
import subprocess
import sys
from fractions import Fraction

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import gen_look_shared  # noqa: E402
import scatter_math_ref as ref  # noqa: E402

DATA = os.path.join(T, "Source", "ChimeraTerrain", "Data")
PALETTE_H = os.path.join(DATA, "TerrainScatterPalette.h")
LOOK_H = os.path.join(DATA, "TerrainLookShared.h")


def read(path):
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def test_look_shared_header_is_current():
    """The checked-in header equals a fresh generation from make_ground_material.py SCALARS (it follows the material)."""
    fresh = gen_look_shared.generate()
    assert read(LOOK_H).replace("\r\n", "\n") == fresh


def test_look_shared_check_cli():
    r = subprocess.run([sys.executable, os.path.join(HERE, "gen_look_shared.py"), "--check"], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr


def test_look_shared_values():
    sc = gen_look_shared.read_scalars()
    cos = Fraction(sc["RockCos"])
    band = Fraction(sc["RockBand"])
    text = read(LOOK_H)
    m = dict((k, int(v)) for k, v in re.findall(r"constexpr int64 (\w+) = (\d+)", text))
    assert m["RockCosQ16"] == int(cos * 65536 + Fraction(1, 2))
    assert m["RockBandQ16"] == int(band * 65536 + Fraction(1, 2))
    assert m["RockSlopeThr"] == gen_look_shared.thr(cos)
    assert m["RockBandLoThr"] == gen_look_shared.thr(cos + band)
    assert m["RockBandHiThr"] == gen_look_shared.thr(cos - band)
    # The band edges the plan quotes after the ground-look final commit: RockCos 0.8192, RockBand 0.10 -> 23.2 and 44.0 degrees.
    assert math.degrees(math.acos(float(cos + band))) == pytest.approx(23.19, abs=0.02)
    assert math.degrees(math.acos(float(cos - band))) == pytest.approx(44.01, abs=0.02)
    assert m["RockBandLoThr"] < m["RockSlopeThr"] < m["RockBandHiThr"]


def test_palette_slope_literals():
    """Every Slope default in the palette is tan^2(theta) * 2^32 of the angle its name says (or the look-shared RockCos threshold)."""
    text = read(PALETTE_H)
    rows = re.findall(r"X\((\w+), Slope, ([^)]+)\)", text)
    angles = {"SgLo": 25.0, "StLo": 21.8, "StHi": 30.0, "FlowerSlopeLo": 22.0, "FlowerSlopeHi": 32.0, "FernSlopeLo": 30.0, "FernSlopeHi": 40.0, "RockSlopeLo": 25.0, "RockSlopeHi": 40.0}
    seen = set()
    for name, lit in rows:
        seen.add(name)
        lit = lit.strip()
        if name == "SgHi":
            assert lit == "LookShared::RockSlopeThr", "the grass limit must follow the material's RockCos"
            continue
        want = round(math.tan(math.radians(angles[name])) ** 2 * 2 ** 32)
        assert int(lit.rstrip("l")) == want, "%s: %s != %d" % (name, lit, want)
    assert seen == set(angles) | {"SgHi"}


def test_palette_names_unique_and_ordered():
    text = read(PALETTE_H)
    names = re.findall(r"^\s*X\((\w+), (?:Frac|Meters|Byte|Slope|Deg), ", text, flags=re.M)
    assert len(names) == len(set(names)), "duplicate palette names"
    assert len(names) > 150
    assert names[0] == "Density"


def test_palette_defaults_sane():
    """Spot-check defaults the plan states (plan C scatter 3.4)."""
    text = read(PALETTE_H)

    def val(name):
        m = re.search(r"X\(%s, \w+, ScQ\(([0-9.]+)\)\)" % name, text)
        assert m, name
        return float(m.group(1))

    assert val("GrassCellM") == 0.5 and val("TussockCellM") == 2.0 and val("TreeCellM") == 4.0 and val("TreeJitter") == 0.5
    # GrassBaseP 0.80 -> 0.95 and KP 0.20 -> 0.25: the S6 bake (EXECUTION.md section 8 "S6 ruling"); the border and coverage values stay the plan's.
    assert val("GrassBaseP") == 0.95 and val("GrassTierSplit") == 0.55 and val("WWeight1") == 0.65
    assert val("BorderLoM") == 118.0 and val("BorderHiM") == 140.0 and val("BorderBias") == 0.35
    assert val("TreeEdgeW") == 0.62 and val("FlowerDriftLo") == 0.70
    assert val("KP") == 0.25 and val("KCellM") == 32.0
    # Every cell size divides every allowed tile size (16/32/64 fine, 40/80/160 coarse).
    for name in ("GrassCellM", "TussockCellM", "FlowerCellM", "NearCardCellM", "TreeCellM", "SaplingCellM", "ShrubCellM", "FernCellM", "RockCellM"):
        assert val(name) in (0.5, 1.0, 2.0, 4.0), name


def test_math_golden_is_current():
    assert read(ref.GOLDEN).replace("\r\n", "\n") == ref.render(ref.golden())


def test_mix32_golden_file_matches_reference():
    """Scripts/scatter/mix32_golden.json (written by make_scatter_meshes.py) holds exactly what the independent Mix32 here computes."""
    import json
    with open(os.path.join(T, "Scripts", "scatter", "mix32_golden.json"), "r", encoding="utf-8") as f:
        g = json.load(f)
    assert len(g["pairs"]) >= 16
    for a, b in g["pairs"]:
        assert ref.mix32(a) == b
    # Known lowbias32 values (Wellons): the hash of 1.
    assert ref.mix32(1) == 1753845952


def test_mix32_twin_in_mesh_generator():
    import make_scatter_meshes as mm
    for x in (0, 1, 2, 3, 127, 128, 255, 65535, 0x12345678, 0xFFFFFFFF):
        assert mm.mix32(x) == ref.mix32(x)


def test_value_noise_properties():
    """Reference sanity: range, continuity across a lattice line, and exact lattice-corner values (t = 0)."""
    seed = 0xABCDEF01
    period = 37 * 65536
    for i in range(200):
        x = (i * 7919 - 100000) * 331
        y = (i * 104729 - 5000) * 17
        v = ref.value_noise_q16(seed, x, y, period)
        assert 0 <= v <= 65535
    # At a lattice corner the noise equals the corner value exactly.
    x = 5 * period
    y = -3 * period
    assert ref.value_noise_q16(seed, x, y, period) == ref.corner(seed, 5, -3)
    # Continuity: one Q16 step moves the value by at most a few units.
    a = ref.value_noise_q16(seed, x + period - 1, y + 12345, period)
    b = ref.value_noise_q16(seed, x + period, y + 12345, period)
    assert abs(a - b) < 64


def test_source_files_are_lf():
    for name in os.listdir(DATA):
        if name.startswith("TerrainScatter") or name == "TerrainLookShared.h":
            with open(os.path.join(DATA, name), "rb") as f:
                assert b"\r\n" not in f.read(), name + " has CRLF line endings"


if __name__ == "__main__":
    sys.exit(pytest.main([__file__, "-q"]))
