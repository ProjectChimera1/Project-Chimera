"""Self-tests of the HUD comparator (plan B T1). Run: python -m pytest tools/unreal-hud/tests -q

The tests build their own region spec from the development reference (T0's ref_hudonly.png when present, else the r4
board PNG) and perturb copies of that same reference, so they need no Unreal capture.
"""
import json
import sys
from pathlib import Path

import numpy as np
import pytest
from PIL import Image
from scipy.ndimage import gaussian_filter

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import hud_common as hc  # noqa: E402
import hud_compare as cmp_  # noqa: E402
import make_regions as mr  # noqa: E402
import ssim as ssim_mod  # noqa: E402
import text_drift as td  # noqa: E402

REF_PATH, ALPHA_PATH = hc.find_ref("A"), hc.find_alpha()
pytestmark = pytest.mark.skipif(REF_PATH is None or ALPHA_PATH is None, reason="no development reference PNG found")

# Provisional soft thresholds (T2 replaces them with calibrated ones); a clean shift must pass even these
PROV = {"A": {"default": {"G4.text.mad": 4.0, "G4.text.ssim": 0.6, "G4.vector.mad": 4.0, "G4.vector.ssim": 0.6,
                          "G5.mass": 0.35, "G1.vector": 12, "G7.mad": 0.5, "G7.ssim": 0.98}}}

CHIP_IRON = (111, 6, 194, 34)          # snapped border box of the iron chip (r4 5.1)
CARD_S = (1703, 943, 1767, 1003)       # command-card slot S (r4 5.7)
STRIP_FILL = (28, 31, 37)


@pytest.fixture(scope="module")
def ref():
    return hc.load_rgb(REF_PATH)


@pytest.fixture(scope="module")
def built(ref):
    spec, masks, cnt = mr.build_spec(ref, hc.load_alpha(ALPHA_PATH))
    return spec, masks, cnt


@pytest.fixture(scope="module")
def spec(built):
    return cmp_.Spec(json.loads(json.dumps(built[0])))


def run(ref, test, spec, only=None, gates=None):
    return cmp_.compare(ref, test, spec, cmp_.Thr(PROV, "A"), "A", only, gates)


def shift_block(ref, box, dx, dy, fill):
    """Move the pixels of box by (dx, dy), refilling the vacated area with fill."""
    out = ref.copy()
    x0, y0, x1, y1 = box
    block = ref[y0:y1, x0:x1].copy()
    out[y0:y1, x0:x1] = fill
    out[y0 + dy:y1 + dy, x0 + dx:x1 + dx] = block
    return out


def recolour_chip_fill(ref, delta):
    out = ref.copy()
    x0, y0, x1, y1 = CHIP_IRON
    box = out[y0:y1, x0:x1]
    box[(box == np.array([20, 22, 26])).all(axis=2)] = (20 + delta, 22 + delta, 26 + delta)
    return out


def failing_gates(rep, region):
    return rep["regions"][region]["failing_gates"]


# ------------------------------------------------------------------ regions
def test_spec_counts_and_partition(built, ref):
    spec, masks, cnt = built
    ok, lines = mr.check(spec, masks, cnt, ref, hc.load_alpha(ALPHA_PATH))
    assert ok, "\n".join(lines)
    assert len(spec["regions"]) == 38 and len(spec["runs"]) == 36 and len(spec["lines"]) > 100
    hard = [p for p in spec["probes"] if p["class"] == "hard"]
    assert len(hard) == 36 and len([p for p in spec["probes"] if p["class"] == "vector"]) == 5
    assert (cnt == 1).all()


def test_anchors_unique(ref, spec):
    for a in mr.ANCHORS:
        prof = mr.anchor_samples(ref, a[1], a[2], a[3], 0)
        use = mr.anchor_use(spec.cls, a[1], a[2], a[3])
        assert mr.anchor_matches(prof, ref, a, use=use) == [0], a[0]


# ------------------------------------------------------------------ comparator
def test_identity_passes(ref, spec):
    rep = run(ref, ref.copy(), spec)
    assert rep["result"] == "PASS" and rep["failing"] == [] and rep["n_regions"] == 38
    assert rep["g7"]["mad"] == 0.0
    assert f"{rep['g7']['ssim']:.4f}" == "1.0000"
    assert all(r["badness"] == 0 for r in rep["regions"].values())
    assert "RESULT PASS 0/38" in cmp_.pair_line(rep)


def test_card_s_shift_fails_g2_and_g3_in_card_s_only(ref, spec):
    rep = run(ref, shift_block(ref, CARD_S, 1, 0, STRIP_FILL), spec)
    assert rep["failing"] == ["card.S"], rep["failing"]
    g = failing_gates(rep, "card.S")
    assert "G2" in g and "G3" in g
    assert rep["regions"]["card.S"]["units"][0]["argmin_shift"] == [1, 0]


def test_chip_iron_shift_1_passes_and_reports_shift(ref, spec):
    rep = run(ref, shift_block(ref, CHIP_IRON, 1, 0, STRIP_FILL), spec)
    assert rep["result"] == "PASS" and rep["failing"] == [], [(n, r["failing_gates"]) for n, r in rep["regions"].items() if not r["pass"]]
    u = rep["regions"]["top.chip.iron"]["units"][0]
    assert u["scored_shift"] == [1, 0] and u["argmin_shift"] == [1, 0]
    assert rep["regions"]["top.chip.iron"]["gate_badness"]["G3"] == pytest.approx(1.0)


def test_chip_iron_shift_2_fails_g3_only(ref, spec):
    rep = run(ref, shift_block(ref, CHIP_IRON, 2, 0, STRIP_FILL), spec)
    assert rep["failing"] == ["top.chip.iron"], rep["failing"]
    assert failing_gates(rep, "top.chip.iron") == ["G3"]
    assert rep["regions"]["top.chip.iron"]["units"][0]["scored_shift"] == [2, 0]


def test_chip_fill_recolour_fails_g1(ref, spec):
    rep = run(ref, recolour_chip_fill(ref, 8), spec)
    assert "G1" in failing_gates(rep, "top.chip.iron")
    assert rep["result"] == "FAIL"


def test_erased_border_fails_g2(ref, spec):
    line = next(l for l in spec.d["lines"] if l["name"].startswith("cmd-button:foundry") and l["side"] == "left")
    ys, xs = mr.line_pixels(spec.lab, spec.cls, line)
    out = ref.copy()
    out[ys, xs] = (20, 22, 26)
    rep = run(ref, out, spec)
    assert "G2" in failing_gates(rep, "card.Q")
    assert failing_gates(rep, "card.W") == []


def test_whole_image_offsets_fail_alignment(ref, spec):
    rep = run(ref, np.roll(ref, 1, axis=1), spec)
    assert rep.get("alignment_fail")
    bad = {a["anchor"]: a for a in rep["alignment"] if not a["ok"]}
    assert bad["top.chip_left"]["dx"] == 1 and bad["top.chip_left"]["dy"] == 0
    rep = run(ref, np.roll(ref, 1, axis=0), spec)
    assert rep.get("alignment_fail")
    assert {a["anchor"]: a for a in rep["alignment"] if not a["ok"]}["top.hairline"]["dy"] == 1
    scaled = np.array(Image.fromarray(ref).resize((round(1920 * 1.01), round(1080 * 1.01)), Image.BILINEAR))[:1080, :1920]
    rep = run(ref, scaled, spec)
    assert rep.get("alignment_fail") and any(not a["ok"] for a in rep["alignment"])


def test_alignment_respects_only(ref, spec):
    """Anchors of panels outside --only are not used."""
    out = np.roll(ref, 1, axis=1)
    names = {a["anchor"] for a in run(ref, out, spec, only=r"^top\.")["alignment"]}
    assert names == {"top.hairline", "top.chip_left", "top.menu_right"}
    names = {a["anchor"] for a in run(ref, ref.copy(), spec, only=r"^card\.")["alignment"]}
    assert names == {"card.left", "card.Q_top"}


def test_only_and_gates_selection(ref, spec):
    rep = run(ref, ref.copy(), spec, only=r"^top\.")
    assert rep["n_regions"] == 8 and "RESULT PASS 0/8" in cmp_.pair_line(rep)
    rep = run(ref, shift_block(ref, CHIP_IRON, 2, 0, STRIP_FILL), spec, only=r"^top\.", gates=["G1", "G2", "G5"])
    assert rep["result"] == "PASS"            # G3 is reported but not decisive
    assert rep["regions"]["top.chip.iron"]["gate_badness"]["G3"] > 1


def test_world_open_g6(ref, spec):
    out = ref.copy()
    out[500:520, 800:900] = (30, 32, 36)      # 'world' pixels differing by 10 levels
    rep = run(ref, out, spec, only=r"^world\.open$")
    assert rep["regions"]["world.open"]["failing_gates"] == ["G6"]


# ------------------------------------------------------------------ badness and SSIM
def test_badness_ordering(ref, spec):
    g1 = []
    for d in (3, 6, 12):
        rep = run(ref, recolour_chip_fill(ref, d), spec)
        g1.append(rep["regions"]["top.chip.iron"]["gate_badness"]["G1"])
        assert rep["worst"]["region"] == "top.chip.iron"
        assert "WORST top.chip.iron" in cmp_.pair_line(rep)
    assert g1[0] < g1[1] < g1[2] and g1[0] == pytest.approx(1.5)
    assert cmp_.badness(0, 1) == 0 and cmp_.badness(1, 0) == float("inf") and cmp_.badness(3, 2) > cmp_.badness(1, 2)
    # a 2 px chip shift (G3 badness 2.0) ranks above a 1-level colour error elsewhere (still passing)
    two = shift_block(ref, CHIP_IRON, 2, 0, STRIP_FILL)
    gold = two[6:34, 8:107]
    gold[(gold == np.array([20, 22, 26])).all(axis=2)] = (21, 23, 27)
    rep = run(ref, two, spec)
    assert rep["worst"]["region"] == "top.chip.iron" and rep["worst"]["gate"] == "G3"
    assert 0 < rep["regions"]["top.chip.gold"]["badness"] < 1


def test_ssim_closed_form_constant_images():
    for a, b in ((100.0, 140.0), (30.0, 31.0), (200.0, 10.0)):
        x, y = np.full((40, 50), a), np.full((40, 50), b)
        assert ssim_mod.ssim(x, y) == pytest.approx(ssim_mod.closed_form_constant(a, b), abs=1e-12)
    x = np.full((40, 50), 77.0)
    assert ssim_mod.ssim(x, x) == pytest.approx(1.0, abs=1e-12)


def test_ssim_constant_offset_textured():
    rng = np.random.default_rng(5)
    x = rng.uniform(40, 200, (60, 70))
    y = x + 9.0                                     # same contrast and structure, shifted mean
    m = ssim_mod.ssim_map(x, y)
    mx = gaussian_filter(x, 1.5, mode="nearest", truncate=3.5)
    my = mx + 9.0
    l_term = (2 * mx * my + ssim_mod.C1) / (mx * mx + my * my + ssim_mod.C1)
    assert np.allclose(m, l_term, atol=1e-10)
    assert ssim_mod.ssim(x, x) == pytest.approx(1.0, abs=1e-12)


# ------------------------------------------------------------------ helper modes and hashes
def test_pipeline_and_blend_modes(ref, tmp_path):
    a = tmp_path / "a.png"
    Image.fromarray(ref).save(a)
    assert cmp_.main(["--pipeline", str(a), str(a)]) == 0
    b = ref.copy()
    b[10, 10] = (0, 0, 0)
    bp = tmp_path / "b.png"
    Image.fromarray(b).save(bp)
    assert cmp_.main(["--pipeline", str(a), str(bp)]) == 1
    # blend: a shot built with gamma-space blending passes, a wrong colour fails
    shot = np.broadcast_to(np.array([20, 22, 26], np.uint8), (1080, 1920, 3)).copy()
    for x, y, w, h, col, al in cmp_.BLEND_BOXES:
        c = cmp_._hex(col)
        shot[y:y + h, x:x + w] = np.floor(np.array([20, 22, 26]) * (1 - al) + c * al + 0.5)
    sp = tmp_path / "blend.png"
    Image.fromarray(shot).save(sp)
    assert cmp_.main(["--blend", "--backdrop", "#14161A", "--shot", str(sp)]) == 0
    shot[100:300, 100:300] = 120
    Image.fromarray(shot).save(sp)
    assert cmp_.main(["--blend", "--backdrop", "#14161A", "--shot", str(sp)]) == 1


def test_calibration_hash_mismatch_refuses(tmp_path, ref):
    thr, reg = tmp_path / "thresholds.json", tmp_path / "regions.json"
    thr.write_text("{}")
    reg.write_text("{}")
    refp = tmp_path / "ref.png"
    Image.fromarray(ref).save(refp)
    cal = tmp_path / "calibration.json"
    cal.write_text(json.dumps({"thresholds_sha256": hc.sha256_file(thr), "regions_sha256": hc.sha256_file(reg),
                               "refs_sha256": {"A": hc.sha256_file(refp)}}))
    assert hc.check_calibration(thr, reg, {"A": refp}, cal)[0] == "OK"
    thr.write_text('{"A": {}}')
    status, problems = hc.check_calibration(thr, reg, {"A": refp}, cal)
    assert status == "MISMATCH" and "thresholds" in problems[0]
    assert hc.check_calibration(thr, reg, {"A": refp}, tmp_path / "none.json")[0] == "UNCALIBRATED"


# ------------------------------------------------------------------ text drift model
def test_text_drift_chip_x_and_kerning(tmp_path):
    csv_path = tmp_path / "drift.csv"
    assert td.main(["--csv", str(csv_path)]) == 0
    blk = td.chip_x(lambda s, wt, px: td.run_metrics(s, "JetBrains Mono", wt, px)["block_w"])
    frc = td.chip_x(lambda s, wt, px: td.run_metrics(s, "JetBrains Mono", wt, px)["frac_w"])
    assert [td.rhu(v) for v in blk] == [8, 112, 200, 288]
    assert [td.rhu(v) for v in frc] == [8, 111, 198, 285]
    m = td.run_metrics("Covenant Acolyte", "Cinzel", 600, 20)
    assert m["kern_sum_px"] == pytest.approx(-1.9, abs=0.05) and m["exact_w"] == pytest.approx(204.42, abs=0.02)
    stat = td.run_metrics("Armour 1 · Damage 6–8 · Speed 2.8 · Carry 10 g", "JetBrains Mono", 500, 12)
    assert stat["block_w"] == 322 and stat["exact_w"] == pytest.approx(331.2, abs=0.01)
    assert len(csv_path.read_text().strip().splitlines()) == 37


def test_cli_hash_mismatch_exit_2_and_recalibrated_override(tmp_path, ref, built, monkeypatch, capsys):
    reg = tmp_path / "regions.json"
    reg.write_text(json.dumps(built[0]))
    thr = tmp_path / "thresholds.json"
    thr.write_text("{}")
    refp = tmp_path / "ref.png"
    Image.fromarray(ref).save(refp)
    cal = tmp_path / "calibration.json"
    cal.write_text(json.dumps({"thresholds_sha256": "0" * 64, "regions_sha256": hc.sha256_file(reg),
                               "refs_sha256": {"A": hc.sha256_file(refp)}}))
    monkeypatch.setattr(cmp_, "CONVERGENCE_CSV", tmp_path / "convergence.csv")
    args = ["--ref", str(refp), "--shot", str(refp), "--regions", str(reg), "--thresholds", str(thr), "--calibration", str(cal),
            "--no-images", "--out", str(tmp_path / "out"), "--only", r"^top\.strip$"]
    assert cmp_.main(args) == 2
    assert "CALIBRATION HASH MISMATCH" in capsys.readouterr().err
    assert cmp_.main(args + ["--recalibrated", "thresholds edited for the test"]) == 0
    rows = (tmp_path / "convergence.csv").read_text().strip().splitlines()
    assert len(rows) == 2 and "RECALIBRATED" in rows[1] and "thresholds edited" in rows[1]


def test_soft_metrics_and_report_only(ref, spec):
    rep = cmp_.compare(ref, ref.copy(), spec, None, "A")           # no thresholds at all: everything soft is report-only
    assert rep["result"] == "PASS"
    assert any("G4.text.mad" in r["report_only"] for r in rep["regions"].values())
    sm = cmp_.soft_metrics(rep)
    assert sm["top.chip.gold"]["G4.text.ssim"] == pytest.approx(1.0) and sm["top.chip.gold"]["G4.text.mad"] == 0
    assert sm["*"]["G7.ssim"] == pytest.approx(1.0) and "G5.mass" in sm["sel.name"]


def test_cli_prints_alignment_fail_and_exits_3(tmp_path, ref, built, capsys):
    reg = tmp_path / "regions.json"
    reg.write_text(json.dumps(built[0]))
    refp, shotp = tmp_path / "ref.png", tmp_path / "shot.png"
    Image.fromarray(ref).save(refp)
    Image.fromarray(np.roll(ref, 1, axis=1)).save(shotp)
    rc = cmp_.main(["--ref", str(refp), "--shot", str(shotp), "--regions", str(reg), "--calibration", str(tmp_path / "none.json"),
                    "--thresholds", str(tmp_path / "none2.json"), "--out", str(tmp_path / "o"), "--no-images"])
    out = capsys.readouterr().out
    assert rc == 3 and "ALIGNMENT FAIL top.chip_left dx=1 dy=0" in out and "RESULT FAIL alignment" in out


# ------------------------------------------------------------------ anchors locate edges, not colours (verifier fix)
CHIPS = ["top.chip.gold", "top.chip.iron", "top.chip.aether", "top.chip.supply"]


def recolour_fills(ref, spec, regions, rgb_from=(20, 22, 26), delta=(8, 9, 11)):
    out = ref.copy()
    ids = [spec.regions[n]["id"] for n in regions]
    m = np.isin(spec.lab, ids) & (out == np.array(rgb_from)).all(axis=2)
    out[m] = np.array(rgb_from) + np.array(delta)
    return out


def test_all_chip_fills_recoloured_fail_g1_in_chips_not_alignment(ref, spec):
    """T0's N2 shape: chip fills off by (+8,+9,+11) next to the top.chip_left edge. Must be scored, not stopped."""
    rep = run(ref, recolour_fills(ref, spec, CHIPS), spec)
    assert not rep.get("alignment_fail"), rep["alignment"]
    assert all(a["status"] == "ok" for a in rep["alignment"]), rep["alignment"]
    assert sorted(rep["failing"]) == sorted(CHIPS), rep["failing"]
    for n in CHIPS:
        assert "G1" in failing_gates(rep, n), (n, failing_gates(rep, n))


def test_anchor_not_found_warns_and_still_scores(ref, spec):
    """An anchor whose edge is gone is a WARN; the run scores the regions and the fault shows in the region."""
    out = ref.copy()
    out[6:34, 8] = (28, 31, 37)          # erase the gold chip's left border column (the top.chip_left anchor edge)
    rep = run(ref, out, spec)
    al = {a["anchor"]: a for a in rep["alignment"]}
    assert al["top.chip_left"]["status"] == "not_found" and al["top.chip_left"]["warn"]
    assert not rep.get("alignment_fail") and rep["result"] == "FAIL"
    assert "G2" in failing_gates(rep, "top.chip.gold")


def test_vector_bake_off_next_to_anchor_does_not_block(ref, spec):
    """The plate shadow (rows 870-872) and the vat glow (rows 920-922) are vector bakes beside anchors; they are left
    out of the anchor's scored differences, so a bake that is 20 levels off cannot stop the run."""
    out = ref.copy()
    out[870:873, 60:140] = np.clip(out[870:873, 60:140].astype(int) + 20, 0, 255)
    out[920:923, 300:380] = np.clip(out[920:923, 300:380].astype(int) + 20, 0, 255)
    rep = run(ref, out, spec)
    al = {a["anchor"]: a for a in rep["alignment"]}
    assert al["mm.plate_top"]["status"] == "ok" and al["sel.vat_top"]["status"] == "ok"
    assert not rep.get("alignment_fail")


def test_shifted_anchor_reports_guarded_regions(ref, spec):
    """T0's N1 shape: the command card moved down 1 px stops the run at card.Q_top with its guarded regions listed."""
    out = shift_block(ref, (1610, 860, 1920, 1079), 0, 1, (20, 22, 26))
    rep = run(ref, out, spec)
    assert rep.get("alignment_fail")
    bad = [a for a in rep["alignment"] if a["status"] == "shifted"]
    assert any(a["anchor"].startswith("card.") and a["dy"] == 1 for a in bad), bad
    assert {"card.Q", "card.panel", "orn.card"} <= set(bad[0]["guarded_regions"])


CONTROLS = hc.HUDREF / "ref" / "controls"


@pytest.mark.skipif(not (CONTROLS / "A" / "N2.png").is_file(), reason="T0's controls not rendered")
@pytest.mark.parametrize("pair,refname", [("A", "ref_hudonly.png"), ("B", "ref_backdrop.png")])
def test_t0_control_n2_scored_in_chips_only(pair, refname):
    refimg = hc.load_rgb(hc.HUDREF / "ref" / refname)
    sp = cmp_.load_spec()
    rep = cmp_.compare(refimg, hc.load_rgb(CONTROLS / pair / "N2.png"), sp, None, pair)
    assert not rep.get("alignment_fail"), rep["alignment"]
    assert sorted(rep["failing"]) == sorted(CHIPS), rep["failing"]
    assert all("G1" in rep["regions"][n]["failing_gates"] for n in CHIPS)


# ------------------------------------------------------------------ CLI calibration lines and outputs
def _cli_files(tmp_path, ref, built):
    reg = tmp_path / "regions.json"
    reg.write_text(json.dumps(built[0]))
    thr = tmp_path / "thresholds.json"
    thr.write_text("{}")
    refp = tmp_path / "ref.png"
    Image.fromarray(ref).save(refp)
    return reg, thr, refp


def test_cli_prints_calibration_ok_hashes(tmp_path, ref, built, capsys):
    reg, thr, refp = _cli_files(tmp_path, ref, built)
    cal = tmp_path / "calibration.json"
    cal.write_text(json.dumps({"thresholds_sha256": hc.sha256_file(thr), "regions_sha256": hc.sha256_file(reg),
                               "refs_sha256": {"A": hc.sha256_file(refp)}}))
    rc = cmp_.main(["--ref", str(refp), "--shot", str(refp), "--regions", str(reg), "--thresholds", str(thr),
                    "--calibration", str(cal), "--no-images", "--out", str(tmp_path / "o"), "--only", r"^top\.strip$"])
    out = capsys.readouterr().out
    assert rc == 0
    want = f"CALIBRATION OK thresholds {hc.sha256_file(thr)[:12]} regions {hc.sha256_file(reg)[:12]} refA {hc.sha256_file(refp)[:12]}"
    assert want in out and out.index(want) < out.index("PAIR A:")


def test_cli_refuses_thresholds_without_calibration(tmp_path, ref, built, capsys):
    reg, thr, refp = _cli_files(tmp_path, ref, built)
    args = ["--ref", str(refp), "--shot", str(refp), "--regions", str(reg), "--thresholds", str(thr),
            "--calibration", str(tmp_path / "missing.json"), "--no-images", "--out", str(tmp_path / "o"), "--only", r"^top\.strip$"]
    assert cmp_.main(args) == 2
    assert "CALIBRATION MISSING" in capsys.readouterr().err
    assert cmp_.main(args + ["--uncalibrated"]) == 0


def test_outputs_no_worst_gate_or_crops_when_clean(tmp_path, ref, spec):
    rep = run(ref, ref.copy(), spec)
    assert rep["worst"]["gate"] == "-" and all(r["worst_gate"] is None for r in rep["regions"].values())
    cmp_.write_outputs(rep, ref, ref.copy(), spec, tmp_path, "clean")
    assert list((tmp_path / "crops").iterdir()) == []
    md = (tmp_path / "report.md").read_text(encoding="utf-8")
    assert "| G1 |" not in md and "| - |" in md
    out = recolour_fills(ref, spec, ["top.chip.iron"])
    rep = run(ref, out, spec)
    d2 = tmp_path / "bad"
    cmp_.write_outputs(rep, ref, out, spec, d2, "bad")
    assert [p.name for p in (d2 / "crops").iterdir()] == ["top.chip.iron.png"]
