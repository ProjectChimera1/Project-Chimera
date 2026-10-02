#!/usr/bin/env python3
"""Self-tests for the scatter asset tools (plan-c-scatter.md 3.6, task S1). No Unreal and no Blender needed:
python -m pytest T/Tools/test_scatter_tools.py -q
Covers: the Mix32 golden shared with the C++ generator (and a C compile of lowbias32 when cl.exe or gcc is on PATH), byte-identical L0
re-runs, L0 budgets, pivots, heights and vertex colours, the Z-up -> glTF Y-up mapping read back from a written file, no zero-area
triangle anywhere, the prepared L1 report against its files (S1 class budgets for every row, measured pivots, heights, current
script/recipe hashes, clean mesh names, baked live UVs with no texture transform, alpha MASK with an RGBA base colour, nor_gl normal
maps), wood-island filtering measured on every wood-filtered card set's exported cards, bush part coverage, the manifest check and licence derivation, and the meshes.json
cross-references. The Blender second-run check is Tools/blender/prep_rerun_check.py (it needs Blender, so it is not a pytest)."""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np
import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
sys.path.insert(0, HERE)

sys.path.insert(0, os.path.join(HERE, "blender"))
import fetch_scatter_assets as fsa  # noqa: E402
import prep_polyhaven as pp  # noqa: E402
import make_scatter_meshes as msm  # noqa: E402
import scatter_glb as sg  # noqa: E402

CFG = json.load(open(msm.CONFIG, encoding="utf-8"))
SRC = fsa.SRC
L0 = os.path.join(SRC, "L0")
PREP = os.path.join(SRC, "prepared")


def lowbias32_reference(x):
    """Independent spelling of lowbias32 (Wellons, 'Prospecting for hash functions'), for the golden check."""
    x = x & 0xFFFFFFFF
    x = (x ^ (x >> 16)) & 0xFFFFFFFF
    x = (x * 2146121005) & 0xFFFFFFFF
    x = (x ^ (x >> 15)) & 0xFFFFFFFF
    x = (x * 2221713035) & 0xFFFFFFFF
    return (x ^ (x >> 16)) & 0xFFFFFFFF


# ------------------------------------------------------------------ Mix32 golden -------------------------------------------------
def test_mix32_golden_matches_python_numpy_and_reference():
    gold = json.load(open(msm.GOLDEN, encoding="utf-8"))["pairs"]
    assert len(gold) >= 30
    ins = np.array([a for a, _ in gold], dtype=np.uint32)
    outs = [b for _, b in gold]
    assert [msm.mix32(a) for a, _ in gold] == outs
    assert [lowbias32_reference(a) for a, _ in gold] == outs
    assert msm.mix32v(ins).tolist() == outs
    assert msm.mix32(0) == 0  # lowbias32 fixes 0: callers never feed a raw 0 seed (Rng mixes the stream in first)


def test_mix32_golden_in_c():
    """Compile lowbias32 in C (the C++ twin's arithmetic) and compare with the golden; skipped when no compiler is on PATH."""
    cc = shutil.which("gcc") or shutil.which("cl")
    vcvars = None
    if not cc:
        import glob
        hits = sorted(glob.glob("C:/Program Files/Microsoft Visual Studio/*/*/VC/Auxiliary/Build/vcvars64.bat"))
        vcvars = hits[-1] if hits else None
        if not vcvars:
            pytest.skip("no C compiler (the S2 C++ test reads the same golden)")
        cc = "cl"
    gold = json.load(open(msm.GOLDEN, encoding="utf-8"))["pairs"]
    d = tempfile.mkdtemp()
    src = os.path.join(d, "m.c")
    with open(src, "w") as f:
        f.write("#include <stdio.h>\n#include <stdint.h>\n#include <stdlib.h>\n"
                "static uint32_t mix32(uint32_t x){x^=x>>16;x*=0x7feb352dU;x^=x>>15;x*=0x846ca68bU;x^=x>>16;return x;}\n"
                "int main(int c,char**v){for(int i=1;i<c;i++)printf(\"%u\\n\",mix32((uint32_t)strtoul(v[i],0,10)));return 0;}\n")
    exe = os.path.join(d, "m.exe")
    if os.path.basename(cc).lower().startswith("gcc"):
        r = subprocess.run([cc, "-O2", "-o", exe, src], capture_output=True)
    elif vcvars:
        env = dict(os.environ)
        env["PATH"] = "C:/Program Files (x86)/Microsoft Visual Studio/Installer;" + env.get("PATH", "")  # vcvarsall calls vswhere
        bat = os.path.join(d, "b.bat")
        obj = os.path.join(d, "m.obj")
        with open(bat, "w") as f:
            f.write(f'@call "{vcvars}" >nul\r\ncl /nologo /O2 /Fe"{exe}" /Fo"{obj}" "{src}"\r\n')
        r = subprocess.run(["cmd", "/c", bat], capture_output=True, env=env)
    else:
        r = subprocess.run([cc, "/nologo", "/O2", f"/Fe{exe}", f"/Fo{d}\\", src], capture_output=True)
    if r.returncode != 0:
        pytest.skip(f"compiler failed: {r.stderr[:200]!r}")
    out = subprocess.run([exe] + [str(a) for a, _ in gold], capture_output=True, text=True).stdout.split()
    assert [int(x) for x in out] == [b for _, b in gold]


def test_rng_streams_differ_and_repeat():
    a = [msm.Rng(CFG["seed"], 101).u32() for _ in range(3)]
    r1, r2 = msm.Rng(CFG["seed"], 101), msm.Rng(CFG["seed"], 102)
    s1 = [r1.u32() for _ in range(64)]
    s2 = [r2.u32() for _ in range(64)]
    assert s1 != s2 and len(set(s1)) == 64
    assert a[0] == s1[0]


# ------------------------------------------------------------------ L0 meshes ----------------------------------------------------
@pytest.fixture(scope="module")
def l0_twice():
    d1, d2 = tempfile.mkdtemp(), tempfile.mkdtemp()
    r1, bad1 = msm.build_all(CFG, d1)
    r2, bad2 = msm.build_all(CFG, d2)
    yield d1, d2, r1, bad1, bad2
    shutil.rmtree(d1, ignore_errors=True)
    shutil.rmtree(d2, ignore_errors=True)


def test_l0_rerun_byte_identical(l0_twice):
    d1, d2, r1, bad1, bad2 = l0_twice
    assert not bad1 and not bad2
    names = sorted(os.listdir(d1))
    assert names == sorted(os.listdir(d2))
    for n in names:
        assert open(os.path.join(d1, n), "rb").read() == open(os.path.join(d2, n), "rb").read(), n


def test_l0_matches_files_on_disk(l0_twice):
    """The checked L0 folder is what the current generator and meshes.json produce."""
    d1 = l0_twice[0]
    if not os.path.isdir(L0):
        pytest.skip("ScatterSrc/L0 not built")
    for n in sorted(os.listdir(d1)):
        assert os.path.exists(os.path.join(L0, n)), n
        assert open(os.path.join(d1, n), "rb").read() == open(os.path.join(L0, n), "rb").read(), f"{n} is stale: rerun make_scatter_meshes.py"


def test_every_slot_has_an_l0_mesh_and_streams_are_unique():
    slots_l0 = {s["slot"] for s in CFG["species"]}
    assert slots_l0 == set(CFG["slots"]) - {"NearCard"}
    streams = [s["stream"] for s in CFG["species"]]
    assert len(streams) == len(set(streams))


def test_l0_budgets_pivots_heights_and_vertex_colours(l0_twice):
    d1, _, rep, _, _ = l0_twice
    assert len(rep["meshes"]) == 13
    for m in rep["meshes"]:
        slot = CFG["slots"][m["slot"]]
        st = sg.stats(os.path.join(d1, m["file"]))
        assert st["triangles"] == m["triangles"]
        assert slot.get("tris_min", 1) <= st["triangles"] <= slot["tris_max"], m["name"]
        assert abs(st["bounds_min"][2]) < 1e-5, m["name"]                     # pivot at the base
        assert abs(st["bounds_max"][2] - slot["nominal_height_m"]) < 1e-4, m["name"]   # nominal height
        w = max(st["bounds_max"][0] - st["bounds_min"][0], st["bounds_max"][1] - st["bounds_min"][1])
        cx = (st["bounds_max"][0] + st["bounds_min"][0]) / 2
        cy = (st["bounds_max"][1] + st["bounds_min"][1]) / 2
        assert abs(cx) < 0.3 * w and abs(cy) < 0.3 * w, m["name"]             # pivot near the base centre
        assert st["vertex_position_sha256"] == m["vertex_position_sha256"]
        js, binb = sg.read_glb(os.path.join(d1, m["file"]))
        assert sg.node_transforms_identity(js)
        for p in js["meshes"][0]["primitives"]:
            pos = sg.accessor(js, binb, p["attributes"]["POSITION"])
            nrm = sg.accessor(js, binb, p["attributes"]["NORMAL"])
            col = sg.accessor(js, binb, p["attributes"]["COLOR_0"])
            idx = sg.accessor(js, binb, p["indices"])
            assert js["accessors"][p["attributes"]["COLOR_0"]]["normalized"] is True
            assert idx.max() < len(pos) and idx.min() >= 0
            assert np.allclose(np.linalg.norm(nrm, axis=1), 1.0, atol=1e-3)
            h = slot["nominal_height_m"]
            # A = height fraction (glTF y is up), within one byte step
            assert np.all(np.abs(col[:, 3].astype(float) - np.clip(pos[:, 1] / h, 0, 1) * 255) <= 1.0), m["name"]
            assert col[:, 0].min() >= 0.1 * 255  # AO never black


def test_glb_is_y_up_and_mapping_is_a_rotation(tmp_path):
    """The written glb holds the builder's Z-up positions as glTF (x, z, -y): read POSITION back and compare with the builder's parts.
    The mapping has determinant +1, so the triangle winding survives (checked on the closed rock: positive signed volume both ways)."""
    sp = [s for s in CFG["species"] if s["name"] == "RockA"][0]
    mesh = msm.Mesh(sp["name"])
    msm.BUILDERS[sp["kind"]](sp, msm.Rng(CFG["seed"], sp["stream"]), mesh, msm.mix32(CFG["seed"] ^ sp["stream"] * 7919))
    msm.finalise(mesh, CFG["slots"][sp["slot"]]["nominal_height_m"])
    path = str(tmp_path / "RockA.glb")
    msm.write_glb(mesh, path)
    js, binb = sg.read_glb(path)
    order = []
    for p in mesh.parts:
        if p.material not in order:
            order.append(p.material)
    for prim, mname in zip(js["meshes"][0]["primitives"], order):
        zup = np.concatenate([p.pos for p in mesh.parts if p.material == mname])
        tri = np.concatenate([p.tri + sum(len(q.pos) for q in mesh.parts[:i] if q.material == mname)
                              for i, p in enumerate(mesh.parts) if p.material == mname])
        raw = sg.accessor(js, binb, prim["attributes"]["POSITION"]).astype(np.float64)
        assert np.allclose(raw, np.c_[zup[:, 0], zup[:, 2], -zup[:, 1]], atol=1e-6)
        assert raw[:, 1].min() >= -1e-6 and abs(raw[:, 1].max() - CFG["slots"]["RockA"]["nominal_height_m"]) < 1e-5     # +Y up in the file

        def vol(P, T):
            a, b, c = P[T[:, 0]], P[T[:, 1]], P[T[:, 2]]
            return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6)
        idx = sg.accessor(js, binb, prim["indices"]).astype(np.int64).reshape(-1, 3)
        assert np.array_equal(idx, tri)
        assert vol(zup, tri) > 0 and vol(raw, idx) > 0


def test_no_zero_area_triangles(l0_twice):
    """Every L0 and prepared triangle has area >= 1e-7 m^2 (a Nanite or remove-degenerates build would drop the rest)."""
    files = [os.path.join(l0_twice[0], n) for n in sorted(os.listdir(l0_twice[0])) if n.endswith(".glb")]
    if os.path.isdir(PREP):
        files += [os.path.join(PREP, n) for n in sorted(os.listdir(PREP)) if n.endswith(".glb")]
    bad = {}
    for f in files:
        z = sum(int((sg.triangle_areas(p) < 1e-7).sum()) for p in sg.primitives(f))
        if z:
            bad[os.path.basename(f)] = z
    assert not bad, bad


# ------------------------------------------------------------------ grass LOD chains (S3 second pass) ---------------------------
def corner_tuples(path):
    """(position xyz rounded to 1e-6, RGBA bytes) at every triangle corner of every primitive of a glb, as a sorted list."""
    js, binb = sg.read_glb(path)
    out = []
    for p in js["meshes"][0]["primitives"]:
        pos = sg.accessor(js, binb, p["attributes"]["POSITION"])
        col = sg.accessor(js, binb, p["attributes"]["COLOR_0"])
        idx = sg.accessor(js, binb, p["indices"]).reshape(-1)
        out += [(tuple(round(float(x), 6) for x in pos[i]), tuple(int(c) for c in col[i])) for i in idx]
    return sorted(out)


def test_lod_chain_reduces_rejects_a_flat_chain():
    """The bar the generator and the S3 report test apply: a chain that does not reduce (S3's first 40/40/40) fails."""
    assert msm.lod_chain_reduces([40, 20, 10])
    assert not msm.lod_chain_reduces([40, 40, 40])
    assert not msm.lod_chain_reduces([40, 20, 20])
    assert not msm.lod_chain_reduces([40])


def test_grass_lod_chains_reduce_and_keep_lod0_blades(l0_twice):
    """Each grass LOD chain (msm.LOD_CHAINS) strictly reduces, every LOD file is in the report with its own hashes, and a lower LOD is
    exactly a subset of LOD0's corners (same positions and vertex colours, A against LOD0's height): the blades with the lowest G byte,
    i.e. the ones M_ScatterBladeFade keeps longest."""
    d1, _, rep, _, _ = l0_twice
    chains = {c["name"]: c for c in rep["lod_chains"]}
    assert set(chains) == set(msm.LOD_CHAINS) == {"GrassT0", "GrassT1"}
    for name, c in chains.items():
        lods = c["lods"]
        assert [l["lod"] for l in lods] == [0, 1, 2]
        tris = [l["triangles"] for l in lods]
        assert msm.lod_chain_reduces(tris), (name, tris)
        for l in lods[1:]:
            assert l["blades"] == max(1, -(-lods[0]["blades"] * int(l["keep_fraction"] * 100) // 100)), (name, l)
        base = corner_tuples(os.path.join(d1, lods[0]["file"]))
        g_all = sorted(set(t[1][1] for t in base))
        for l in lods[1:]:
            path = os.path.join(d1, l["file"])
            assert hashlib.sha256(open(path, "rb").read()).hexdigest() == l["sha256"]
            assert sg.stats(path)["triangles"] == l["triangles"]
            assert sg.stats(path)["vertex_position_sha256"] == l["vertex_position_sha256"]
            sub = corner_tuples(path)
            # multiset inclusion: every corner of the LOD is a corner of LOD0 (no new or moved vertex, no changed colour)
            pool = {}
            for t in base:
                pool[t] = pool.get(t, 0) + 1
            for t in sub:
                assert pool.get(t, 0) > 0, (name, l["file"], t)
                pool[t] -= 1
            # the kept blades are the lowest-G ones: every dropped blade's G is >= every kept blade's G
            kept_g = sorted(set(t[1][1] for t in sub))
            dropped_g = [g for g in g_all if g not in kept_g]
            assert not dropped_g or min(dropped_g) >= max(kept_g), (name, l["file"], kept_g, dropped_g)
            assert set(l["kept_g_bytes"]) == set(kept_g), (name, l["file"])
        # lower LODs are nested (LOD2's blades are LOD1's lowest)
        assert set(lods[2]["kept_parts"]) <= set(lods[1]["kept_parts"]) <= set(lods[0]["kept_parts"])


def test_lod_files_are_not_meshes_rows(l0_twice):
    """The LOD files are not import rows of their own (S3 imports report['meshes'] as meshes and the LODs only into <Name>_L)."""
    rep = l0_twice[2]
    files = {m["file"] for m in rep["meshes"]}
    for c in rep["lod_chains"]:
        for l in c["lods"][1:]:
            assert l["file"] not in files


# ------------------------------------------------------------------ L1 prepared ---------------------------------------------------
def prepared_report():
    p = os.path.join(PREP, "report.json")
    if not os.path.exists(p):
        pytest.skip("ScatterSrc/prepared not built (Blender step)")
    return json.load(open(p, encoding="utf-8"))


def test_prepared_report_matches_files_and_budgets():
    rep = prepared_report()
    names = {m["name"] for m in rep["meshes"]}
    want = {r["name"] for r in CFG["prepared"]} | {b["name"] for b in CFG["bushes"]}
    assert want <= names, sorted(want - names)
    for m in rep["meshes"]:
        path = os.path.join(PREP, m["file"])
        assert hashlib.sha256(open(path, "rb").read()).hexdigest() == m["sha256"], m["name"]
        st = sg.stats(path)
        assert st["triangles"] == m["triangles"]
        assert st["vertex_position_sha256"] == m["vertex_position_sha256"]
        assert m["budget_ok"], m["name"]
        slot = CFG["slots"][m["slot"]]
        # the S1 class budget holds for EVERY row, candidates included (flower <= 64, fern <= 300, grass <= 48, L1 tree <= 30k, ...)
        lim = pp.slot_tris_max(CFG, m["slot"])
        assert m["slot_tris_max"] == lim, m["name"]
        assert m["slot_budget_ok"] and (lim is None or st["triangles"] <= lim), (m["name"], st["triangles"], lim)
        assert m["mesh_names"] == [m["name"]] and [x["name"] for x in sg.read_glb(path)[0]["meshes"]] == [m["name"]], m["name"]
        assert abs(st["bounds_min"][2]) < 1e-3, m["name"]
        if m.get("target_height_m"):
            assert abs(st["bounds_max"][2] - m["target_height_m"]) <= 0.01 * m["target_height_m"], m["name"]
        if m["slot"].startswith("Tree"):
            assert m["target_height_m"] == slot["nominal_height_m"]
            assert m["scale_factor"] > 0 and m["source_height_m"] > 0 and m["pre_fit_height_m"] > 0
            assert abs(m["scale_factor"] * m["pre_fit_height_m"] - m["target_height_m"]) < 1e-3, m["name"]


def test_prepared_rows_are_current():
    """Each row names the script, helpers and recipe that built it; all equal the current files (a stale prepared/ folder fails)."""
    rep = prepared_report()
    entries = {r["name"]: r for r in CFG["prepared"] + CFG["bushes"] + CFG.get("seedcards", [])}
    bl = os.path.join(HERE, "blender")
    for m in rep["meshes"]:
        assert m["script_sha256"] == pp.sha256_file(os.path.join(bl, m["script"])), m["name"]
        assert m["helpers_sha256"] == pp.sha256_file(os.path.join(bl, "prep_polyhaven.py")), m["name"]
        assert m["recipe_sha256"] == pp.recipe_sha(entries[m["name"]]), m["name"]


def test_prepared_uvs_are_baked_and_live():
    """Every textured primitive samples TEXCOORD_0 with no KHR_texture_transform, and its UVs span a real texture region
    (tree_small_02_branches used to collapse to one texel: its material samples the source's TEXCOORD_1)."""
    rep = prepared_report()
    for m in rep["meshes"]:
        for p in sg.primitives(os.path.join(PREP, m["file"])):
            if not p["textured"]:
                continue
            key = (m["name"], p["material"])
            assert p["uv"] is not None, key
            assert p["texcoords"] == [0] and not p["texture_transform"], key
            span = p["uv"].max(axis=0) - p["uv"].min(axis=0)
            assert span.min() > 1e-3, (key, span.tolist())
        for mat, f in m["uv"].items():
            assert f["texture_transform"] is None or f["texture_transform"]["baked"], (m["name"], mat)
    rows = {m["name"]: m for m in rep["meshes"]}
    for n in ("TreeBroadA", "TreeBroadB"):
        f = rows[n]["uv"]["tree_small_02_branches"]
        assert f["uv_layer"] == "UVMap.001" and f["texture_transform"]["scale"][:2] == [3.0, 0.6], f
    assert rows["TreeConiferA"]["uv"]["fir_tree_01_trunk_c"]["generated"]["kind"] == "cylindrical"


def test_prepared_alpha_and_normal_maps():
    rep = prepared_report()
    for m in rep["meshes"]:
        js, binb = sg.read_glb(os.path.join(PREP, m["file"]))
        for mat in js["materials"]:
            pbr = mat.get("pbrMetallicRoughness", {})
            if mat["name"] in m["alpha_materials"]:
                assert mat.get("alphaMode") == "MASK" and mat.get("doubleSided"), (m["name"], mat["name"])
                img = js["images"][js["textures"][pbr["baseColorTexture"]["index"]]["source"]]
                assert img["mimeType"] == "image/png", (m["name"], mat["name"])
                v = js["bufferViews"][img["bufferView"]]
                png = binb[v.get("byteOffset", 0): v.get("byteOffset", 0) + v["byteLength"]]
                assert png[25] == 6, (m["name"], mat["name"], "base colour PNG is not RGBA")  # IHDR colour type 6 = RGBA
            if "normalTexture" in mat:
                img = js["images"][js["textures"][mat["normalTexture"]["index"]]["source"]]
                assert "nor_gl" in img.get("name", "") + img.get("uri", ""), (m["name"], img.get("name"))


def test_prepared_trees_have_trunk_pivot_and_cards():
    rep = prepared_report()
    trees = 0
    for m in rep["meshes"]:
        path = os.path.join(PREP, m["file"])
        piv = pp.measured_pivot(path, m["pivot"]["mode"], m["pivot"]["material"])
        assert piv == m["pivot"], m["name"]                                  # the report's pivot is what the file holds
        assert abs(piv["min_z_m"]) < 1e-3, m["name"]
        if m["slot"].startswith("Tree"):
            trees += 1
            assert m["cards"]["kept"] > 1000 and m["cards"]["fit_rms_m_median"] < 0.02, m["name"]
            assert m["triangles"] <= 30000
            assert piv["mode"] == "trunk" and max(abs(x) for x in piv["base_xy_m"]) < 0.05, (m["name"], piv)
            trunk = [p for p in sg.primitives(path) if p["material"] == m["pivot_material"]] if "pivot_material" in m else \
                [p for p in sg.primitives(path) if p["material"] == piv["material"]]
            assert trunk and abs(min(p["pos"][:, 2].min() for p in trunk)) < 1e-3, m["name"]   # the trunk itself stands on z = 0
    assert trees == 4


def card_components(prim):
    """Connected triangle groups of a primitive (each fitted card is one), as lists of vertex indices."""
    parent = np.arange(len(prim["pos"]))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for a, b, c in prim["tri"]:
        for x, y in ((a, b), (a, c)):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[ry] = rx
    groups = {}
    for v in np.unique(prim["tri"]):
        groups.setdefault(find(v), []).append(v)
    return list(groups.values())


def embedded_alpha(js, binb, mat_name):
    """Alpha channel (H x W, 0..1) of a material's embedded base-colour PNG."""
    import io
    from PIL import Image
    mat = [m for m in js["materials"] if m["name"] == mat_name][0]
    img = js["images"][js["textures"][mat["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"]]
    bv = js["bufferViews"][img["bufferView"]]
    data = binb[bv.get("byteOffset", 0):bv.get("byteOffset", 0) + bv["byteLength"]]
    return np.asarray(Image.open(io.BytesIO(data)).convert("RGBA"), dtype=np.float64)[:, :, 3] / 255.0


def wood_filtered_card_sets():
    """(row name, card material, the card stats of that set) for every wood-filtered card recipe: prepared trees and bush parts."""
    rows = {m["name"]: m for m in prepared_report()["meshes"]}
    out = []
    for r in CFG["prepared"]:
        if r.get("cards", {}).get("wood_filter"):
            out += [(r["name"], mname, rows[r["name"]]["cards"]) for mname in r["cards"]["materials"]]
    for b in CFG["bushes"]:
        for part in b["parts"]:
            if part["op"] == "cards" and part.get("wood_filter"):
                out.append((b["name"], part["source"], rows[b["name"]]["cards"][part["object"]]))
    return rows, out


def test_wood_filtered_cards_carry_no_opaque_strip():
    """No card of a wood-filtered set shows a nearly solid UV box (an opaque bark or twig strip of its atlas: a tube fitted as one
    grown card reads as a flat plank, as it did on ShrubA's shrub_04 sprigs and the conifers), measured on the exported file's own
    cards and embedded alpha; every such set really dropped islands."""
    rows, sets = wood_filtered_card_sets()
    names = {n for n, _, _ in sets}
    assert {"TreeConiferA", "TreeConiferB", "TreeBroadA", "TreeBroadB", "ShrubA", "ShrubB"} <= names, names
    for name, mname, st in sets:
        assert st["wood_dropped"] > 0, (name, mname)
        path = os.path.join(PREP, rows[name]["file"])
        js, binb = sg.read_glb(path)
        alpha = embedded_alpha(js, binb, mname)
        H, W = alpha.shape
        prims = [p for p in sg.primitives(path) if p["material"] == mname]
        assert prims, (name, mname)
        n_cards, worst = 0, 0.0
        for p in prims:
            for comp in card_components(p):
                uv = p["uv"][comp]
                u0, v0 = np.clip(uv.min(axis=0), 0, 1)
                u1, v1 = np.clip(uv.max(axis=0), 0, 1)
                x0, y0 = int(u0 * W), int(v0 * H)                      # glTF v runs down the image
                box = alpha[y0:max(y0 + 1, int(np.ceil(v1 * H))), x0:max(x0 + 1, int(np.ceil(u1 * W)))]
                cov = float((box > 0.5).mean())
                worst = max(worst, cov)
                n_cards += 1
        assert n_cards > 0 and worst <= 0.9, (name, mname, n_cards, round(worst, 3))


def test_bushes_draw_every_listed_part():
    rows = {m["name"]: m for m in prepared_report()["meshes"]}
    for b in CFG["bushes"]:
        m = rows[b["name"]]
        assert all(m["sprigs_per_part"][p["object"]] >= 1 for p in b["parts"]), m["sprigs_per_part"]
        assert m["sources"] == m["drawn_sources"]
        assert sum(m["sprigs_per_part"].values()) == b["sprigs"]


def test_bush_sprig_guarantee_is_deterministic():
    import make_bush as mb
    b = {"seed": 7, "sprigs": 5, "parts": [{"object": "a", "weight": 100}, {"object": "b", "weight": 0.001}, {"object": "c", "weight": 0.001}]}
    names = ["a", "b", "c"]
    c1, c2 = mb.sprig_parts(b, names), mb.sprig_parts(b, names)
    assert c1 == c2 and set(c1) == set(names) and len(c1) == 5


# ------------------------------------------------------------------ manifest and config -------------------------------------------
def test_manifest_check_passes():
    p = os.path.join(SRC, "manifest.json")
    if not os.path.exists(p):
        pytest.skip("manifest not written yet")
    assert fsa.check(p) == 0


def test_manifest_check_rejects_bad_licence(tmp_path):
    p = os.path.join(SRC, "manifest.json")
    if not os.path.exists(p):
        pytest.skip("manifest not written yet")
    man = json.load(open(p, encoding="utf-8"))
    man["rows"][0]["licence"] = "Fab-Standard"
    bad = tmp_path / "manifest.json"
    bad.write_text(json.dumps(man))
    assert fsa.check(str(bad)) == 1


def test_manifest_licence_follows_sources(tmp_path):
    p = os.path.join(SRC, "manifest.json")
    if not os.path.exists(p):
        pytest.skip("manifest not written yet")
    man = json.load(open(p, encoding="utf-8"))
    rows = {(r["level"], r["id"]): r for r in man["rows"]}
    seed = rows[("L1", "GrassT1__seedcard")]
    assert seed["licence"] == "CC0-1.0" and seed["licence_url"] == [fsa.LIC_URL["ambientcg"]]
    assert rows[("L1", "TreeBroadA")]["licence_url"] == [fsa.LIC_URL["polyhaven"]]
    for r in man["rows"]:
        if r["kind"] == "mesh":
            assert r["date"][:2] == "20", r["id"]                            # a real date, not 'deterministic'
    seed["licence_url"] = [fsa.LIC_URL["polyhaven"]]                         # the old wrong URL must now fail the check
    bad = tmp_path / "manifest.json"
    bad.write_text(json.dumps(man))
    assert fsa.check(str(bad)) == 1


def test_config_cross_references():
    src_ids = {s["id"] for s in CFG["sources"]["polyhaven_models"]}
    for r in CFG["prepared"]:
        assert r["source"] in src_ids, r["name"]
        assert r["slot"] in CFG["slots"]
        assert ("__" in r["name"]) == (r.get("role") == "candidate"), r["name"]
    for b in CFG["bushes"]:
        for part in b["parts"]:
            assert part["source"] in src_ids
    for s in CFG["sources"]["polyhaven_models"] + CFG["sources"]["polyhaven_textures"] + CFG["sources"]["ambientcg_atlases"]:
        assert s["id"] and s["use"]
    for s in CFG["sources"]["ambientcg_atlases"]:
        assert len(s["zip_sha256"]) == 64, s["id"]
    for s in CFG["sources"]["skipped"]:
        assert s["id"] and s["reason"]


# ------------------------------------------------------------------ alpha maps and rock height ---------------------------------
def test_alpha_u8_scales_16_bit_ramp(tmp_path):
    """A 16-bit 0..65535 ramp becomes the full 0..255 ramp (Pillow's convert("L") would clip it at 255 instead)."""
    from PIL import Image
    v = np.arange(65536, dtype=np.uint16).reshape(256, 256)
    p = str(tmp_path / "ramp16.png")
    Image.fromarray(v).save(p)                     # uint16 -> a 16-bit PNG
    assert Image.open(p).mode in ("I;16", "I")
    a = np.asarray(pp.alpha_u8(p)).astype(np.int64)
    assert a.dtype.kind in "iu" and a.min() == 0 and a.max() == 255
    assert np.array_equal(a.ravel(), np.rint(np.arange(65536) * 255.0 / 65535.0).astype(np.int64))
    assert len(np.unique(a)) == 256
    p8 = str(tmp_path / "ramp8.png")
    Image.fromarray((np.arange(65536) % 256).astype(np.uint8).reshape(256, 256)).save(p8)
    assert np.array_equal(np.asarray(pp.alpha_u8(p8)), (np.arange(65536) % 256).astype(np.uint8).reshape(256, 256))


def test_near_card_alpha_is_scaled_not_clipped():
    """NearCard's embedded mask (grass_medium_01, a 16-bit source) covers what the correctly scaled source covers at the 0.5 cutoff
    (0.160), not the clipped 0.452."""
    src = os.path.join(SRC, "raw", "polyhaven", "grass_medium_01", "textures", "grass_medium_01_alpha_1k.png")
    want = float((np.asarray(pp.alpha_u8(src)) > 127).mean())
    assert abs(want - 0.160) < 0.01
    row = [m for m in prepared_report()["meshes"] if m["name"] == "NearCard"][0]
    js, binb = sg.read_glb(os.path.join(PREP, row["file"]))
    for mname in row["alpha_materials"]:
        got = float((embedded_alpha(js, binb, mname) > 127.0 / 255.0).mean())
        assert abs(got - want) < 0.01, (mname, got, want)


def test_rock_height_matches_s2_palette():
    """Rock meshes are built at the palette's RockNominalHM, so S2's sink (RockSinkFrac x RockNominalHM x scale) is 25 % of the real
    mesh height (plan 3.4)."""
    import re
    h = open(os.path.join(os.path.dirname(HERE), "Source", "ChimeraTerrain", "Data", "TerrainScatterPalette.h"), encoding="utf-8").read()
    nominal = float(re.search(r"X\(RockNominalHM,\s*Meters,\s*ScQ\(([0-9.]+)\)\)", h).group(1))
    for slot in ("RockA", "RockB"):
        assert CFG["slots"][slot]["nominal_height_m"] == nominal, slot
    for r in CFG["prepared"]:
        if r["slot"] in ("RockA", "RockB"):
            assert r["height"] == nominal, r["name"]
    for m in prepared_report()["meshes"]:
        if m["slot"] in ("RockA", "RockB"):
            assert abs(m["target_height_m"] - nominal) < 1e-9, m["name"]
