#!/usr/bin/env python3
"""Self-tests for the shared meadow patch include (plan-c-scatter.md 3.7 and 4 S6). No Unreal needed:
python -m pytest T/Tools/test_hlsl_include.py -q

S6's acceptance for the include: M_ChimeraGround's fully expanded Custom-node code string is byte-identical before and after the patch block
moved into Scripts/ChimeraPatch.hlsl. GROUND_CODE_SHA256 is the sha256 of Scripts/ChimeraGround.hlsl as committed before the move
(U 1c2f459, the ground-look round 4 commit; 21,673 bytes); make_ground_material.py hands the expanded string to the Custom node and writes
its sha256 to the report, which the C7 commandlet run must print as code_sha256=<this value>.
"""
import hashlib
import os
import re
import subprocess
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
SCRIPTS = os.path.join(T, "Scripts")
sys.path.insert(0, SCRIPTS)
sys.path.insert(0, os.path.join(SCRIPTS, "scatter"))

import hlsl_include as H  # noqa: E402
import scatter_material_spec as S  # noqa: E402

GROUND = os.path.join(SCRIPTS, "ChimeraGround.hlsl")
PATCH = os.path.join(SCRIPTS, "ChimeraPatch.hlsl")
GROUND_CODE_SHA256 = "8a9c94b3130fbc3ee47b3103c4d3c4d6c88bfc20279739ec611eab733767e732"
GROUND_CODE_BYTES = 21673
# What the include reads and must find defined by its includer (ChimeraPatch.hlsl's own //! contract lines).
EXPECTS = ["P", "NoiseP", "NoiseB", "Nw", "W", "ClumpR", "PatchLongM", "SunDirX", "SunDirY", "SunDirZ", "TieLo", "TieHi", "TerrainDry",
           "PatchLong", "PatchBias", "PatchDither", "PatchContrast", "PatchLo", "PatchHi"]


def strip_comments(text):
    return re.sub(r"//[^\n]*", "", re.sub(r"/\*.*?\*/", "", text, flags=re.S))


def test_ground_expanded_string_is_byte_identical_to_the_pre_include_ground():
    code, names, sha = H.expand_file(GROUND)
    assert names == ["ChimeraPatch.hlsl"]
    assert len(code.encode("utf-8")) == GROUND_CODE_BYTES
    assert sha == GROUND_CODE_SHA256


def test_ground_matches_its_committed_pre_include_text_when_git_has_it():
    """Independent of the pinned hash: git's copy of ChimeraGround.hlsl at the round-4 commit, if the repository is here."""
    repo = os.path.dirname(T)
    r = subprocess.run(["git", "-C", repo, "show", "1c2f459:ChimeraTerrain/Scripts/ChimeraGround.hlsl"], capture_output=True)
    if r.returncode != 0:
        pytest.skip("git history not available here (the repo mirror has another layout)")
    assert H.expand_file(GROUND)[0].encode("utf-8") == r.stdout


def test_include_file_contract():
    raw = H.read_lf(PATCH)  # LF-normalised: a CRLF checkout must satisfy the same contract
    assert raw.endswith("\n")
    body = H.included_text("ChimeraPatch.hlsl")
    assert not body.startswith("//!") and "//!" not in body
    assert body.startswith("// Patch field in [-1, 1]")
    assert body.rstrip("\n").endswith("Patch = sign(Patch) * smoothstep(PatchLo, PatchHi, abs(Patch));")
    code = strip_comments(body)
    # F24: shared with the Nanite-shaded grass, so no derivative and no implicit-derivative fetch, no texture fetch at all.
    assert not re.search(r"\b(ddx|ddy|fwidth|Texture2DSample\w*|SampleLevel|Sample)\b", code)
    assert "PerInstanceRandom" not in body
    for name in EXPECTS:
        assert re.search(r"\b%s\b" % name, code), name


def test_expand_rules(tmp_path):
    (tmp_path / "a.hlsl").write_text("//! doc\nfloat A = 1.0;\n", encoding="utf-8", newline="\n")
    (tmp_path / "nest.hlsl").write_text("//#INCLUDE a.hlsl\n", encoding="utf-8", newline="\n")
    (tmp_path / "nonl.hlsl").write_text("float B = 2.0;", encoding="utf-8", newline="\n")
    assert H.expand("x;\n//#INCLUDE a.hlsl\ny;\n", str(tmp_path)) == ("x;\nfloat A = 1.0;\ny;\n", ["a.hlsl"])
    assert H.expand("  //#INCLUDE a.hlsl\n", str(tmp_path))[1] == []  # indented: not a directive
    with pytest.raises(ValueError):
        H.expand("//#INCLUDE nest.hlsl\n", str(tmp_path))
    with pytest.raises(ValueError):
        H.expand("//#INCLUDE missing.hlsl\n", str(tmp_path))
    with pytest.raises(ValueError):
        H.expand("//#INCLUDE nonl.hlsl\n", str(tmp_path))


def test_blade_inlines_the_same_block_after_defining_what_it_reads():
    parsed = S.parse_hlsl()
    blade = parsed["sections"]["Blade"]
    assert blade["includes"] == ["ChimeraPatch.hlsl"]
    body = blade["body"]
    block = H.included_text("ChimeraPatch.hlsl")
    assert body.count(block) == 1
    before = strip_comments(body[:body.index(block)])
    for name in EXPECTS:
        if name in blade["inputs"]:
            continue
        assert re.search(r"\bfloat[234]?\s+%s\s*=" % name, before), "Blade defines %s before the include" % name
    # The patch scalars come from the ground material's own SCALARS (copied at build time).
    g = S.ground_scalars()
    for name in ("PatchDither", "ClumpM", "ClumpWarpM", "PatchLong", "PatchBias", "PatchContrast"):
        assert name in blade["inputs"] and S.masters()["M_ScatterBlade"]["params"][name] == ("s", g[name])
    # No section other than the Blade includes anything; the raw scatter file names the include once.
    assert all(not s.get("includes") for n, s in parsed["sections"].items() if n != "Blade")
    raw = open(S.HLSL_PATH, encoding="utf-8").read()
    assert len(re.findall(r"^//#INCLUDE ChimeraPatch.hlsl$", raw, flags=re.M)) == 1


def test_ground_builder_uses_the_expansion():
    text = open(os.path.join(SCRIPTS, "make_ground_material.py"), encoding="utf-8").read()
    assert "hlsl_include.expand_file(HLSL)" in text and "'code_sha256'" in text and "code_sha256=%s" in text
    assert "code = f.read()" not in text


def test_crlf_checkout_expands_to_the_same_bytes(tmp_path):
    """git runs with core.autocrlf=true and no eol rule covers .hlsl, so a fresh checkout may write CRLF: the ground's expansion, the include
    body and the scatter sections must come out exactly as from the LF files (the pre-S6 builders read with universal newlines)."""
    for name in ("ChimeraGround.hlsl", "ChimeraPatch.hlsl"):
        raw = open(os.path.join(SCRIPTS, name), encoding="utf-8", newline="").read()
        (tmp_path / name).write_bytes(raw.replace("\n", "\r\n").encode("utf-8"))
    code, names, sha = H.expand_file(str(tmp_path / "ChimeraGround.hlsl"), str(tmp_path))
    assert names == ["ChimeraPatch.hlsl"]
    assert "\r" not in code and sha == GROUND_CODE_SHA256
    assert H.included_text("ChimeraPatch.hlsl", str(tmp_path)) == H.included_text("ChimeraPatch.hlsl")
    raw = open(S.HLSL_PATH, encoding="utf-8", newline="").read()
    lf = S.parse_hlsl(raw)
    assert len(lf["sections"]) == 3
    assert S.parse_hlsl(raw.replace("\n", "\r\n")) == lf
