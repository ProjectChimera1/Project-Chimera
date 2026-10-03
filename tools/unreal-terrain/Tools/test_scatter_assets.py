#!/usr/bin/env python3
"""Self-tests for the scatter import and materials (plan-c-scatter.md 3.6-3.7, task S3). No Unreal needed:
python -m pytest T/Tools/test_scatter_assets.py -q
Covers the HLSL text rules (no per-instance random, no derivative or implicit-derivative fetch, every output assigned, every declared input
used, every section ends in a return), that every Custom-node input has exactly one source in the spec (builtin, parameter or sample), that the
ground scalars the grass patch copies exist, that the L0 and L1 rules cover every glTF material of every mesh on disk and name only parameters
their master has, the texture statistics, and the shape of make_scatter_assets.py (parses; sentinel line; no PerInstanceRandom expression).
The editor-side behaviour (Interchange import, compile, usage flags, dependencies, flip, vertex colours) is asserted by the commandlet itself:
LOCK PS T/Tools/run_commandlet.ps1 -Script T/Scripts/make_scatter_assets.py -Tag s3_assets -Sentinel SCATTER_OK -TimeoutMin 60."""
import ast
import json
import os
import re
import subprocess
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(T, "Scripts", "scatter"))
sys.path.insert(0, HERE)

import scatter_material_spec as S  # noqa: E402
import scatter_glb as sg  # noqa: E402

SCRIPT = os.path.join(T, "Scripts", "make_scatter_assets.py")
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
PARSED = S.parse_hlsl()
MASTERS = S.masters()


# ------------------------------------------------------------------------------------------------------------------ HLSL text
def hlsl_text():
    with open(S.HLSL_PATH, encoding="utf-8") as f:
        return f.read()


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def test_sections_present():
    assert set(PARSED["sections"]) == {"Blade", "Triplanar", "Tex"}
    assert "CS_LUMA" in PARSED["common"]
    for name, sec in PARSED["sections"].items():
        assert sec["inputs"], name
        assert len(sec["inputs"]) == len(set(sec["inputs"])), f"{name}: duplicate input"
        assert sec["outputs"], name
        for o, typ in sec["outputs"]:
            assert typ in ("float1", "float3"), (name, o, typ)


def test_no_per_instance_random():
    """F25: the engine's per-instance random changes with remove-at-swap history. Banned in the HLSL, the spec and the builder."""
    for path in (S.HLSL_PATH, os.path.join(T, "Scripts", "scatter", "scatter_material_spec.py")):
        assert "PerInstanceRandom" not in open(path, encoding="utf-8").read(), path
    tree = ast.parse(open(SCRIPT, encoding="utf-8").read())
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute):
            assert node.attr != "MaterialExpressionPerInstanceRandom", "the builder must never create a PerInstanceRandom expression"


def test_no_derivatives_anywhere():
    """F24: no ddx, ddy or fwidth, and no implicit-derivative fetch (Sample, SampleBias, Texture2DSample) in a Custom node: Nanite shading and the
    Nanite raster give a Custom node no derivatives. Every fetch is SampleLevel; the opacity mask is a standard TextureSample expression."""
    code = strip_comments(hlsl_text())
    assert not re.search(r"\b(ddx|ddy|ddx_fine|ddy_fine|ddx_coarse|ddy_coarse|fwidth)\b", code)
    assert not re.search(r"\.Sample\s*\(|\.SampleBias\s*\(|\bTexture2DSample\s*\(|\bTexture2DSampleBias\s*\(|\bTexture2DSampleGrad\s*\(", code)
    assert "Texture2DSampleLevel" in code
    # The masked masters take their mask from a TextureSample parameter, never from HLSL: the Tex section has no output named like a mask.
    assert not any("Opacity" in o or "Mask" in o for o, _ in PARSED["sections"]["Tex"]["outputs"])


def test_no_world_position_offset_or_time():
    code = strip_comments(hlsl_text())
    assert not re.search(r"\b(View\.GameTime|Parameters\.Time|GameTime|Wind|WorldPositionOffset)\b", code)


def test_every_input_is_used_and_every_output_assigned():
    for name, sec in PARSED["sections"].items():
        body = strip_comments(PARSED["common"] + sec["body"])
        for nm in sec["inputs"]:
            assert re.search(r"\b" + re.escape(nm) + r"\b", body), f"{name}: input {nm} never used"
        for o, _ in sec["outputs"]:
            assert re.search(r"\b" + re.escape(o) + r"\s*=", body), f"{name}: output {o} never assigned"
        assert re.search(r"\breturn\s+Col\s*;\s*$", body.strip()), f"{name}: must end with return Col;"


def test_texture_object_inputs_use_their_sampler():
    """A texture-object input X arrives as X plus XSampler (HLSLMaterialTranslator CustomExpression)."""
    for name, sec in PARSED["sections"].items():
        body = strip_comments(sec["body"])
        for nm in sec["inputs"]:
            if nm in ("NoiseTex", "TexC", "TexN"):
                assert re.search(r"Texture2DSampleLevel\(\s*" + nm + r"\s*,\s*" + nm + r"Sampler\b", body), f"{name}: {nm} fetched without {nm}Sampler"


def test_normals_leave_as_world_vectors_times_facing_sign():
    for name in ("Blade", "Triplanar"):
        body = strip_comments(PARSED["sections"][name]["body"])
        assert re.search(r"CgNormalWS\s*=.*Parameters\.TwoSidedSign", body), name


# ------------------------------------------------------------------------------------------------------------------ spec
def test_every_custom_input_has_one_source():
    for mname, spec in MASTERS.items():
        sec = PARSED["sections"][spec["section"]]
        sample_inputs = {"BaseRGB", "ARM"} if spec.get("samples") else set()
        sources = set(S.BUILTIN_INPUTS) | set(spec["params"]) | sample_inputs
        for nm in sec["inputs"]:
            assert nm in sources, f"{mname}: input {nm} has no source"
            n = (nm in S.BUILTIN_INPUTS) + (nm in spec["params"]) + (nm in sample_inputs)
            assert n == 1, f"{mname}: input {nm} has {n} sources"
        for p in spec["params"]:
            assert p in sec["inputs"], f"{mname}: parameter {p} is not read by section {spec['section']}"
        if spec.get("samples"):
            assert set(spec["samples"]) == {"BaseColorTex", "NormalTex", "ARMTex"}
            assert spec["normal"] == "texture"
        else:
            assert spec["normal"] == "custom"


def test_masters_flags():
    assert set(MASTERS) == {"M_ScatterBlade", "M_ScatterBladeFade", "M_ScatterTriplanar", "M_ScatterTexOpaque", "M_ScatterTexOpaque2S",
                            "M_ScatterTexMasked", "M_ScatterCard"}
    for n, m in MASTERS.items():
        assert m["blend"] in ("opaque", "masked")
        assert (m["blend"] == "masked") == ("clip" in m)
    # Plan 3.7: two-sided for the thin classes and the L1 wood (open after decimation); one-sided only for closed surfaces (L0 rocks, bark,
    # canopy masses; L1 rocks).
    assert {n for n, m in MASTERS.items() if m["two_sided"]} == {"M_ScatterBlade", "M_ScatterBladeFade", "M_ScatterTexOpaque2S",
                                                                "M_ScatterTexMasked", "M_ScatterCard"}
    assert MASTERS["M_ScatterTexOpaque2S"]["blend"] == "opaque" and MASTERS["M_ScatterTexOpaque2S"]["nanite"] is True
    assert MASTERS["M_ScatterTexOpaque2S"]["params"] == MASTERS["M_ScatterTexOpaque"]["params"]
    assert {n for n, m in MASTERS.items() if not m["two_sided"]} == {"M_ScatterTriplanar", "M_ScatterTexOpaque"}
    assert MASTERS["M_ScatterCard"]["nanite"] is False and MASTERS["M_ScatterCard"]["clip"] == 0.33
    assert MASTERS["M_ScatterTexMasked"]["clip"] == 0.5
    assert {n for n in MASTERS if not MASTERS[n]["nanite"]} == {"M_ScatterCard", "M_ScatterBladeFade"}
    assert MASTERS["M_ScatterCard"].get("fade") == "alpha"
    # The grass _L A/B fades (plan 3.7 "_L, bDisallowNanite, fade"; risk 8): a masked Blade with the same parameters as the Nanite Blade.
    bf = MASTERS[S.LOD_VARIANT_MASTER]
    assert S.LOD_VARIANT_MASTER == "M_ScatterBladeFade" and bf["fade"] == "blade" and bf["blend"] == "masked"
    assert bf["section"] == "Blade" and bf["params"] == MASTERS["M_ScatterBlade"]["params"]


def test_ground_scalars_exist_and_are_copied():
    g = S.ground_scalars()
    assert not set(S.GROUND_PATCH_OMITTED) & set(S.GROUND_NAMES)
    for n in S.GROUND_NAMES:
        assert n in g, n
        assert MASTERS["M_ScatterBlade"]["params"][n] == ("s", g[n])
    # Sanity against the ground's own round-4 numbers (a change in make_ground_material.py must be a conscious one here too).
    assert g["PatchM"] > 0 and g["WarpNoiseM"] > 0 and 0 < g["PatchStrength"] <= 1.5


def test_default_textures_cover_every_role():
    assert set(S.DEFAULT_TEXTURES) == set(S.DEFAULT_PIXELS) == {"color", "masks", "normal"}
    for spec in MASTERS.values():
        for _, (kind, default) in spec["params"].items():
            if kind == "t":
                assert default in S.DEFAULT_TEXTURES
        for role in spec.get("samples", {}).values():
            assert role in S.DEFAULT_TEXTURES


def test_texture_stats_match_textures():
    if not os.path.isdir(os.path.join(SRC, "raw")):
        pytest.skip("ScatterSrc/raw not fetched")
    r = subprocess.run([sys.executable, os.path.join(HERE, "scatter_texture_stats.py"), "--check"], capture_output=True, text=True)
    assert r.returncode == 0, r.stdout + r.stderr
    st = S.texture_stats()
    assert 0.05 < st["leafy_grass"]["mean_linear_luma"] < 0.6
    assert MASTERS["M_ScatterTriplanar"]["params"]["TexLumaMean"][1] == pytest.approx(st["leafy_grass"]["mean_linear_luma"], rel=0.5)


# ------------------------------------------------------------------------------------------------------------------ rules over the real meshes
def master_param_names(master):
    spec = MASTERS[master]
    return set(spec["params"]) | set(spec.get("samples", {}))


def glb_materials(path):
    js, _ = sg.read_glb(path)
    out = []
    for m in js.get("materials", []):
        pbr = m.get("pbrMetallicRoughness", {})
        mr = pbr.get("metallicRoughnessTexture")
        arm = js["images"][js["textures"][mr["index"]]["source"]].get("name") if mr else None
        out.append({"name": m.get("name"), "alphaMode": m.get("alphaMode", "OPAQUE"), "doubleSided": bool(m.get("doubleSided")),
                    "baseColorFactor": pbr.get("baseColorFactor"), "arm": arm})
    return out


def test_l0_rule_covers_every_l0_material():
    d = os.path.join(SRC, "L0")
    if not os.path.isdir(d):
        pytest.skip("ScatterSrc/L0 not generated")
    cfg = S.meshes_config()
    species = {s["name"]: s for s in cfg["species"]}
    rep = json.load(open(os.path.join(d, "report.json"), encoding="utf-8"))
    assert len(rep["meshes"]) == 13
    for m in rep["meshes"]:
        sp = species[m["name"]]
        for gm in glb_materials(os.path.join(d, m["file"])):
            master, scal, vec, tex = S.l0_rule(sp, gm["name"])
            names = master_param_names(master)
            for k in list(scal) + list(vec) + list(tex):
                assert k in names, f"{m['name']}/{gm['name']}: {k} is not a parameter of {master}"
            for k in vec:
                assert MASTERS[master]["params"][k][0] == "v", (m["name"], k)
            for k in scal:
                assert MASTERS[master]["params"][k][0] == "s", (m["name"], k)
            if master == "M_ScatterTriplanar":
                assert set(tex) == {"TexC", "TexN"}
            if master == "M_ScatterTexOpaque":
                assert set(tex) == {"BaseColorTex", "NormalTex", "ARMTex"}
            assert all(len(v) == 4 for v in vec.values())
            # The L0 generator marks thin parts doubleSided and closed meshes not: the master's sidedness must agree.
            assert MASTERS[master]["two_sided"] is gm["doubleSided"], (m["name"], gm["name"], master)


def test_l1_rule_covers_every_prepared_material():
    d = os.path.join(SRC, "prepared")
    if not os.path.isdir(d):
        pytest.skip("ScatterSrc/prepared not generated")
    rep = json.load(open(os.path.join(d, "report.json"), encoding="utf-8"))
    n = 0
    for m in rep["meshes"]:
        for gm in glb_materials(os.path.join(d, m["file"])):
            master, scal, vec = S.l1_rule(m["name"], m["slot"], gm)
            names = master_param_names(master)
            assert all(k in names for k in list(scal) + list(vec)), (m["name"], master)
            if m["slot"] == "NearCard":
                assert master == "M_ScatterCard"
            else:
                if gm["alphaMode"] == "MASK":
                    assert master == "M_ScatterTexMasked"
                elif m["slot"].startswith("Rock"):
                    assert master == "M_ScatterTexOpaque"  # closed (0 boundary edges): one-sided
                else:
                    assert master == "M_ScatterTexOpaque2S", (m["name"], gm)  # trunks, branches, shrub bark: open after decimation
            # AO comes from R only when the packed map is an ARM map (RockA__moss carries a plain roughness map).
            assert scal["AOFromR"] == (1.0 if (gm["arm"] is None or "_arm" in gm["arm"]) else 0.0), (m["name"], gm["arm"])
            if m["name"] == "RockA__moss":
                assert scal["AOFromR"] == 0.0
            n += 1
    assert n >= len(rep["meshes"])


def test_is_arm_image():
    assert S.is_arm_image("rock_09_arm_1k") and S.is_arm_image("tree_small_02_branch_arm_1k") and S.is_arm_image("x_arm")
    assert not S.is_arm_image("rock_moss_set_01_rough_1k") and not S.is_arm_image(None) and not S.is_arm_image("armadillo_diff_1k")


def test_fern_rule_ignores_grass_custom_data():
    """Ferns are a coarse-grid class (CD0 variation, no CD2/CD3): no terrain-normal mix and no dry tint."""
    sp = {"name": "Fern", "kind": "fern", "colour": [0.1, 0.2, 0.05], "roughness": 0.8}
    master, scal, vec, _ = S.l0_rule(sp, "M_Frond")
    assert master == "M_ScatterBlade" and scal["TerrainNormalMix"] == 0.0 and vec["DryMul"] == [1.0, 1.0, 1.0, 1.0]


def test_blade_samples_the_patch_at_the_instance_origin_with_a_mip():
    body = strip_comments(PARSED["sections"]["Blade"]["body"])
    assert re.search(r"float2 P = OP\.xy", body) and "WP" not in PARSED["sections"]["Blade"]["inputs"]
    # The instance origin in the pixel shader is the instance-space transform, not ObjectPositionWS (the component's bounds origin there).
    assert S.BUILTIN_INPUTS["OP"] == "instance_origin"
    text = open(SCRIPT, encoding="utf-8").read()
    assert "unreal.MaterialExpressionTransformPosition" in text and "unreal.MaterialExpressionObjectPositionWS" not in text
    assert re.search(r"enum_exact\(unreal\.MaterialPositionTransformSource, 'INSTANCE'", text)
    assert re.search(r"enum_exact\(unreal\.MaterialPositionTransformSource, 'WORLD'", text)
    for call in re.findall(r"Texture2DSampleLevel\(([^;]*)\)", body):
        assert not re.search(r",\s*0\.0\s*\)?$", call.strip()), call  # no fixed mip-0 fetch


def test_pixel_angle_comes_from_the_view():
    """rts80, oblique and closeup have 75, 50 and 60 degree vertical fovs: the mip footprint reads the view, a positive parameter overrides."""
    assert S.PIXEL_ANGLE == 0.0
    common = strip_comments(PARSED["common"])
    assert "ResolvedView.ViewToClip[1][1]" in common and "ResolvedView.ViewSizeAndInvSize.y" in common
    for name in ("Blade", "Triplanar"):
        body = strip_comments(PARSED["sections"][name]["body"])
        assert "CS_PIXEL_ANGLE(PixelAngle)" in body, name
        assert not re.search(r"\*\s*PixelAngle\b", body), name  # the raw parameter is never used as the angle


def test_l1_tree_fallback_targets():
    assert set(S.NANITE_FALLBACK_PERCENT["L1"]) == {"TreeBroadA", "TreeBroadB", "TreeConiferA", "TreeConiferB"}
    assert all(S.MIN_FALLBACK_FRACTION <= v <= 1.0 for v in S.NANITE_FALLBACK_PERCENT["L1"].values())


def test_nanite_policy():
    assert S.is_nanite_slot("GrassT0") and S.is_nanite_slot("TreeBroadA") and S.is_nanite_slot("RockB") and S.is_nanite_slot("Flower")
    assert not S.is_nanite_slot("NearCard")


# ------------------------------------------------------------------------------------------------------------------ the builder script
def test_builder_parses_and_prints_the_sentinel_contract():
    text = open(SCRIPT, encoding="utf-8").read()
    ast.parse(text)
    assert "SCATTER_OK meshes=%d materials=%d errors=0 usage_ok=%s deps_ok=%d flip_ok=%d vc_ok=%d" in text
    assert "SCATTER_FAIL errors=%d" in text
    # It must never touch the config files (run_commandlet's guard) or the project's ground assets.
    assert "DefaultEngine" not in text.replace("DefaultEngine.ini unchanged", "")
    assert "M_ChimeraGround" not in text


def test_builder_references_only_scatter_paths():
    assert S.ROOT == "/Game/Terrain/Scatter"
    for d in list(S.MESH_DIR.values()) + [S.MAT_DIR, S.TEX_DIR]:
        assert d.startswith(S.ROOT)
    assert S.ALLOWED_DEP_PREFIXES == ("/Game/Terrain/", "/Engine/", "/BaseMaterial/", "/Script/")


def test_grass_lod_variants_are_three_lods():
    assert S.GRASS_LOD_SCREEN_SIZES == [1.0, 0.12, 0.04]
    assert S.GRASS_LOD_VARIANTS == ["GrassT0", "GrassT1"]
    assert S.TMP_DIR.startswith(S.ROOT + "/")


def lod_chain_strict(tris):
    """The report bar on a _L variant: at least two LODs and strictly fewer triangles LOD by LOD."""
    return len(tris) >= 2 and all(a > b > 0 for a, b in zip(tris, tris[1:]))


def test_lod_chain_bar_rejects_a_chain_that_does_not_reduce():
    """S3's first pass shipped GrassT0_L 40/40/40 and the old >= bar let it pass; the strict bar must not."""
    assert lod_chain_strict([40, 20, 10]) and lod_chain_strict([42, 21, 14])
    assert not lod_chain_strict([40, 40, 40]) and not lod_chain_strict([40, 20, 20]) and not lod_chain_strict([40])


def test_l0_lod_chain_files_match_the_spec():
    """ScatterSrc/L0/report.json has a three-LOD, strictly reducing chain for every grass _L variant (make_scatter_meshes.py LOD_CHAINS)."""
    rep_path = os.path.join(T, "ScatterSrc", "L0", "report.json")
    if not os.path.isfile(rep_path):
        pytest.skip("ScatterSrc/L0 not built")
    chains = {c["name"]: c for c in json.load(open(rep_path, encoding="utf-8")).get("lod_chains", [])}
    for name in S.GRASS_LOD_VARIANTS:
        lods = chains[name]["lods"]
        assert len(lods) == len(S.GRASS_LOD_SCREEN_SIZES), name
        assert lod_chain_strict([l["triangles"] for l in lods]), (name, [l["triangles"] for l in lods])
        assert lods[0]["file"] == name + ".glb" and all(os.path.isfile(os.path.join(T, "ScatterSrc", "L0", l["file"])) for l in lods)


def test_vertex_colour_readers():
    """Blade and Triplanar read VC, BladeFade's mask reads VC G; the Tex family (every L1 material) does not, so L1 meshes without COLOR_0
    are fine and every L0 mesh (Blade or Triplanar on at least one slot) must carry it."""
    reads = {n: S.master_reads_vertex_colour(n) for n in MASTERS}
    assert reads == {"M_ScatterBlade": True, "M_ScatterBladeFade": True, "M_ScatterTriplanar": True, "M_ScatterTexOpaque": False,
                     "M_ScatterTexOpaque2S": False, "M_ScatterTexMasked": False, "M_ScatterCard": False}


# ------------------------------------------------------------------------------------------------------------------ the commandlet's report
REPORT = os.path.join(T, "Out", "scatter_assets", "report.json")


def test_manifest_fill_key_of():
    import scatter_manifest_fill as mf  # noqa: F401
    assert mf.key_of("/Game/Terrain/Scatter/Meshes/L1/TreeBroadA") == ("L1", "TreeBroadA")
    assert mf.key_of("/Game/Terrain/Scatter/Meshes/L0/GrassT0") == ("L0", "GrassT0")
    assert mf.key_of("/Game/Terrain/Scatter/Meshes/L0/GrassT0_L") is None


def test_asset_report_bars_if_present():
    """The S3 acceptance bars read from Out/scatter_assets/report.json (skipped before the commandlet has run)."""
    if not os.path.isfile(REPORT):
        pytest.skip("no Out/scatter_assets/report.json")
    r = json.load(open(REPORT, encoding="utf-8"))
    if not r.get("full_run"):
        pytest.skip("a partial debug run")
    c = r["checks"]
    assert c["errors"] == 0 and c["deps_ok"] is True and c["flip_ok"] is True and c["vc_ok"] is True
    k, n = c["usage_ok"].split("/")
    assert k == n and int(n) > 0
    assert not r["errors"] and not r["unexpected_assets"]
    # Imported source triangles equal the generator's or Blender's report within 1 % (r8b question 7: in fact exactly).
    for p, m in r["meshes"].items():
        if m.get("triangles_expected"):
            assert abs(m["triangles_source"] - m["triangles_expected"]) <= S.MAX_TRI_DRIFT * m["triangles_expected"], p
        assert m["distance_field_resolution_scale"] == 0.0 and m["simple_collision_shapes"] == 0, p
        assert m["nanite"] is (m["slot"] not in S.NANITE_OFF_SLOTS and not p.endswith("_L")), p
    assert all(t["never_stream"] for t in r["textures"].values())
    assert all(t["flip_green_channel"] for t in r["textures"].values() if t["normal"] and not t["path"].endswith(S.DEFAULT_TEXTURES["normal"]))
    assert all(not m["per_instance_random"] and m["translation_errors"] == 0 and m["stats"]["num_pixel_shader_instructions"] > 0
               for m in r["masters"].values())
    assert set(r["masters"]) == set(MASTERS)
    assert all(m["two_sided_read_back"] is MASTERS[n]["two_sided"] for n, m in r["masters"].items())
    # The engine's own statistic on every master and instance (asset-registry tag), beside the expression scan.
    assert r["material_tags"] and all(t["HasPerInstanceRandom"] == "False" for t in r["material_tags"].values())
    assert len(r["material_tags"]) == len(r["masters"]) + len(r["instances"])
    assert r["checks"]["shader_log"]["scanned"] and r["checks"]["shader_log"]["error_lines"] == 0
    fc = r["checks"]["flip_counts"]
    assert fc["flipped"] == fc["normal_maps"] - fc["flat_default_exempt"] and fc["flipped_by_interchange"] == fc["gltf_normal_maps"] > 0
    # The _L copies draw through the fading Blade master.
    for p, m in r["meshes"].items():
        if p.endswith("_L"):
            assert all(r["instances"][x["instance"]]["parent"] == S.LOD_VARIANT_MASTER for x in m["materials"]), p
    for d in r["deps"].values():
        assert all(x.startswith(S.ALLOWED_DEP_PREFIXES) for x in d)
    assert r["outside_scatter"]["new"] == [] and r["outside_scatter"]["gone"] == []
    # Vertex colours read back on the FINAL saved assets: three L0 source descriptions (plus their render data carrying colour) and every
    # LOD of both _L variants; and every mesh whose material reads VC has it.
    src_rows = [v for v in r["vc_rows"] if v["mode"] == "final_source_mesh_description"]
    lod_rows = [v for v in r["vc_rows"] if v["mode"] == "final_render_lod"]
    assert sorted(v["mesh"] for v in src_rows) == ["GrassT0", "RockA", "TreeBroadA"]
    assert sorted((v["mesh"], v["lod"]) for v in lod_rows) == [(n + "_L", i) for n in S.GRASS_LOD_VARIANTS for i in range(3)]
    assert len(r["vc_rows"]) == len(src_rows) + len(lod_rows)
    assert all(v["ok"] and max(v["max_abs_diff_bytes"].values()) <= 2 and v["tuple_multiset_equal"] for v in r["vc_rows"])
    assert all(v["render_lod0"]["non_white_corners"] > 0 for v in src_rows)
    for p, m in r["meshes"].items():
        if m.get("vertex_colours_needed"):
            assert m["has_vertex_colors"] is True, p
        if "/L0/" in p:
            assert m["vertex_colours_needed"] is True, p
    ve = r["vc_expect"]
    assert ve["missing"] == [] and ve["meshes_needing_vc"] == ve["meshes_needing_vc_with_vc"] == sum(1 for p in r["meshes"] if "/L0/" in p)
    # The Blade masters read the instance origin through the instance-space transform (never ObjectPositionWS).
    for n, m in r["masters"].items():
        if MASTERS[n]["section"] == "Blade":
            assert "MaterialExpressionTransformPosition" in m["expression_classes"], n
            assert "MaterialExpressionObjectPositionWS" not in m["expression_classes"], n
            t = m["instance_origin_transform"]
            assert "INSTANCE" in t["source"].upper() and t["destination"].upper().endswith("WORLD"), (n, t)
            assert "PERIODIC" not in t["destination"].upper() and "TRANSLATED" not in t["destination"].upper(), (n, t)
    # L1 trees: an explicit Nanite fallback, so the render LOD0 is a usable tree.
    for nm in S.NANITE_FALLBACK_PERCENT["L1"]:
        m = r["meshes"]["/Game/Terrain/Scatter/Meshes/L1/" + nm]
        assert "PERCENT" in m["nanite_fallback"]["target"].upper(), (nm, m["nanite_fallback"])
        assert m["triangles_render_lod0"] >= S.MIN_FALLBACK_FRACTION * m["triangles_source"], (nm, m["triangles_render_lod0"])
    # The _L copies are real LOD chains: three LODs, strictly fewer triangles each, equal to their glbs, at the spec's screen sizes.
    n_l = 0
    for p, m in r["meshes"].items():
        if p.endswith("_L"):
            n_l += 1
            assert [x["lod"] for x in m["lods"]] == [0, 1, 2], p
            assert [x["screen_size"] for x in m["lods"]] == [pytest.approx(sc, abs=1e-3) for sc in S.GRASS_LOD_SCREEN_SIZES], p
            tris = [x["triangles"] for x in m["lods"]]
            assert lod_chain_strict(tris), (p, tris)
            assert tris == m["lod_triangles_expected"] and m["lod_chain_reduces"] is True, p
            assert len(m["materials"]) == 1, p
    assert n_l == len(S.GRASS_LOD_VARIANTS)
    # Nothing new anywhere under /Game outside the scatter root (and the temporary LOD import folder is gone).
    assert not any(x.startswith(S.TMP_DIR + "/") for x in r["deps"]), "temporary LOD imports left"
    assert all(set(i.get("texture_samplers", {})) == set(i["textures"]) for i in r["instances"].values())
    # Engine dependencies are derived from the registry rows (not a fixed list), and S6's settings hash is present.
    eng = sorted(set(x for d in r["deps"].values() for x in d if x.startswith(("/BaseMaterial/", "/Engine/"))))
    assert [e["path"] for e in r["engine_dependencies"]] == eng and eng
    assert re.fullmatch(r"[0-9a-f]{64}", r["asset_settings_sha256"])


def test_look_overrides_name_real_instances_and_parameters():
    """Task S6: every LOOK_OVERRIDES row names a material instance the import builds ('MI_<mesh>_<glTF material>', L0 and L1) and only
    parameters of that instance's master, of the right kind (scalar or RGBA vector); apply_look merges without changing its inputs."""
    import re as _re
    built = {}
    d0, d1 = os.path.join(SRC, "L0"), os.path.join(SRC, "prepared")
    if not (os.path.isdir(d0) and os.path.isdir(d1)):
        pytest.skip("ScatterSrc not generated")
    species = {s["name"]: s for s in S.meshes_config()["species"]}
    for m in json.load(open(os.path.join(d0, "report.json"), encoding="utf-8"))["meshes"]:
        for gm in glb_materials(os.path.join(d0, m["file"])):
            built["MI_%s_%s" % (m["name"], _re.sub(r"[^A-Za-z0-9_]", "_", gm["name"]))] = S.l0_rule(species[m["name"]], gm["name"])[0]
    for m in json.load(open(os.path.join(d1, "report.json"), encoding="utf-8"))["meshes"]:
        for gm in glb_materials(os.path.join(d1, m["file"])):
            built["MI_%s_%s" % (m["name"], _re.sub(r"[^A-Za-z0-9_]", "_", gm["name"]))] = S.l1_rule(m["name"], m["slot"], gm)[0]
    assert S.LOOK_OVERRIDES
    for name, o in S.LOOK_OVERRIDES.items():
        assert name in built, f"{name} is not an instance the import builds"
        params = MASTERS[built[name]]["params"]
        assert set(o) <= {"scalars", "vectors"}, name
        for k, v in o.get("scalars", {}).items():
            assert params[k][0] == "s" and isinstance(v, float), (name, k)
        for k, v in o.get("vectors", {}).items():
            assert params[k][0] == "v" and len(v) == 4 and all(0.0 <= x <= 1.0 for x in v), (name, k)
    s0, v0 = {"Rough": 0.85}, {"Tip": [1.0, 1.0, 1.0, 1.0]}
    s1, v1 = S.apply_look("MI_GrassT0_M_Blade", s0, v0)
    assert s0 == {"Rough": 0.85} and v0 == {"Tip": [1.0, 1.0, 1.0, 1.0]}
    assert s1["Rough"] == 0.85 and s1["PatchAmount"] == 0.9 and v1["Tip"] == S.LOOK_OVERRIDES["MI_GrassT0_M_Blade"]["vectors"]["Tip"]
    assert S.apply_look("MI_not_overridden", s0, v0) == (s0, v0)
