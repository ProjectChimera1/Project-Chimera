"""Pure-Python data for the scatter assets and materials (plan-c-scatter.md 3.6-3.7, task S3). No `unreal` import, so
Scripts/make_scatter_assets.py (inside the editor) and Tools/test_scatter_assets.py (pytest) read the same facts.

Contents
  * the asset roots and the default-texture names;
  * parse_hlsl(): splits Scripts/scatter/ChimeraScatter.hlsl into its sections (`//#SECTION`, `//#INPUTS`, `//#OUTPUTS`);
  * ground_scalars(): the ground material's SCALARS read with `ast` from make_ground_material.py (which cannot be imported: it imports
    `unreal` and runs main()), so the grass patch field uses the ground's own numbers;
  * MASTERS: the seven master materials (section, blend mode, sidedness, usage flags, every parameter and its default);
  * l0_rule() / l1_rule(): which master and which parameter values a glTF material gets (L0 from meshes.json species, L1 from the glTF
    material's alphaMode, base colour factor and slot);
  * the mesh policy: which slots are Nanite, the distance-field and collision settings the import asserts.
"""
import ast
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(os.path.dirname(HERE))
HLSL_PATH = os.path.join(HERE, "ChimeraScatter.hlsl")
MESHES_JSON = os.path.join(HERE, "meshes.json")
TEXTURE_STATS = os.path.join(HERE, "texture_stats.json")
GROUND_SCRIPT = os.path.join(os.path.dirname(HERE), "make_ground_material.py")
sys.path.insert(0, os.path.dirname(HERE))
import hlsl_include  # noqa: E402  (T/Scripts: the //#INCLUDE expansion shared with make_ground_material.py)

ROOT = "/Game/Terrain/Scatter"
MESH_DIR = {"L0": ROOT + "/Meshes/L0", "L1": ROOT + "/Meshes/L1"}
MAT_DIR = ROOT + "/Materials"
TEX_DIR = ROOT + "/Textures"
GROUND_TEX_DIR = "/Game/Terrain/Textures"
NOISE_TEX = "/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT"  # ground's own patch noise (make_ground_material.py NOISE_TEX)
ALLOWED_DEP_PREFIXES = ("/Game/Terrain/", "/Engine/", "/BaseMaterial/", "/Script/")

DEFAULT_TEXTURES = {"color": "T_ScatterBaseDefault", "masks": "T_ScatterArmDefault", "normal": "T_ScatterNormalDefault"}
# 4x4 RGBA8 defaults (the sampler type of a texture parameter is fixed by its default, so every override must be the same kind).
DEFAULT_PIXELS = {"color": (255, 255, 255, 255), "masks": (255, 204, 0, 255), "normal": (128, 128, 255, 255)}

# Raw CC0 sources of the L0 textures (Poly Haven, fetched by fetch_scatter_assets.py): role -> (file suffix, kind).
L0_TEXTURES = {
    "T_Leaf_C": ("leafy_grass/leafy_grass_diff_1k.jpg", "color"),
    "T_Leaf_N": ("leafy_grass/leafy_grass_nor_gl_1k.jpg", "normal_gl"),
    "T_Bark_C": ("bark_brown_02/bark_brown_02_diff_1k.jpg", "color"),
    "T_Bark_N": ("bark_brown_02/bark_brown_02_nor_gl_1k.jpg", "normal_gl"),
    "T_Bark_ARM": ("bark_brown_02/bark_brown_02_arm_1k.jpg", "masks"),
}

# Rocks reuse the ground's rock textures (already imported by make_ground_material.py, allowed by the dependency rule).
GROUND_ROCK = {"TexC": "T_Rock_C", "TexN": "T_Rock_N"}
ROCK_ALBEDO_SCALE = 0.5  # species rock colours (0.24-0.27) are about 3x the ground rock mean luma (RockLumaMean); halved so rocks sit near it. S6 tunes.

# Ground scalars the grass patch field copies (names as in make_ground_material.py SCALARS).
GROUND_NAMES = ["WarpNoiseM", "PatchM", "PatchWarpM", "PatchStrength", "PatchContrast", "PatchBias", "PatchLo", "PatchHi", "PatchLong",
                "PatchLongM", "DryR", "DryG", "DryB", "DryVal", "DrySat", "LushR", "LushG", "LushB", "LushVal", "LushSat",
                "SunDirX", "SunDirY", "SunDirZ", "TerrainDry", "TieLo", "TieHi", "PatchDither", "ClumpM", "ClumpWarpM"]
# S6: the patch block is shared (Scripts/ChimeraPatch.hlsl, `//#INCLUDE`), so the grass copies every ground patch term, the clump dither included
# (a third noise fetch at the 5 m clump scale). Only the rock/dirt mask on the slope term reads pure grass weights (grass grows where W.x is high).
GROUND_PATCH_OMITTED = []
# PixelAngle (the angle one pixel spans, for the depth-based mips) defaults to 0 = taken from the view in the shader:
# 2 / (ResolvedView.ViewToClip[1][1] * ResolvedView.ViewSizeAndInvSize.y), i.e. 2 tan(vfov/2) / height (the rts80, oblique and closeup poses have
# 75, 50 and 60 degree vertical fovs, RtsCameraPawn.cpp, so no one constant fits). A positive value overrides it (a diagnostic).
PIXEL_ANGLE = 0.0

# Sidedness (plan 3.7 table): two-sided for the thin classes (grass, tussock, flower, fern fronds, near card, masked leaves and twigs) and for the
# L1 wood. One-sided (back faces culled in the Nanite main-view and VSM shadow rasters) only where the surface is closed or open only at hidden
# ends: L0 rocks, bark, canopies, needle and shrub leaf masses (every L0 material agrees with its glTF doubleSided flag, test_scatter_assets.py)
# and the L1 rocks (0 boundary edges). The L1 trunks, branches and shrub bark are NOT closed after S1's decimation (boundary-edge fraction
# 0.11-0.55 against 0.001-0.16 in the raw Poly Haven sources, S3 verification 2026-10-02), so they get the two-sided opaque M_ScatterTexOpaque2S:
# one-sided they would show holes and see-through tube ends in the main view and in the VSM shadows.
L1_ONE_SIDED_SLOT_PREFIXES = ("Rock",)

# Slots drawn as Nanite (plan 3.7 table); the near card is the only non-Nanite slot. Grass also gets a non-Nanite LOD variant (_L).
NANITE_OFF_SLOTS = {"NearCard"}
GRASS_LOD_VARIANTS = ["GrassT0", "GrassT1"]  # L0 only: written as <name>_L, Nanite off, 3 LODs, the fading Blade (the A/B of plan 3.7)
LOD_VARIANT_MASTER = "M_ScatterBladeFade"
# The _L chain: LOD0 = the <name> glb, LOD1 and LOD2 = make_scatter_meshes.py's <name>_LOD1/_LOD2 glbs (blade subsets kept in fade order,
# about 50 % and 25 % of the blades; ScatterSrc/L0/report.json "lod_chains"), attached with StaticMeshEditorSubsystem::SetLodFromStaticMesh
# (StaticMeshEditorSubsystem.h:143). QuadricMeshReduction cannot reduce these open blade strips (S3's set_lods gave 40/40/40), so no
# reduction settings are used. Screen sizes per LOD (SetLodScreenSizes, StaticMeshEditorSubsystem.h:177, turns auto-compute off):
GRASS_LOD_SCREEN_SIZES = [1.0, 0.12, 0.04]
TMP_DIR = ROOT + "/Tmp"  # temporary import folder of the LOD glbs; deleted in the same run, asserted empty
# Nanite fallback (the render LOD0 of a Nanite mesh: drawn by any non-Nanite path, r.Nanite 0 or a diagnostic). The engine's Auto target
# left L1 TreeConiferA at 256 of 29,699 triangles (S3 verification), a broken tree; the L1 trees get an explicit percent-of-triangles target
# (FMeshNaniteSettings::FallbackTarget = PercentTriangles, FallbackPercentTriangles, EngineTypes.h:3349-3355).
NANITE_FALLBACK_PERCENT = {"L1": {"TreeBroadA": 0.15, "TreeBroadB": 0.15, "TreeConiferA": 0.15, "TreeConiferB": 0.15}}
MIN_FALLBACK_FRACTION = 0.10  # the report bar on those rows: render LOD0 >= 10 % of the source triangles
MAX_TRI_DRIFT = 0.01  # imported triangles within 1 % of the generator's or Blender's report

# OP = the instance origin in the PIXEL shader: TransformPosition(Instance -> Absolute World) of (0,0,0). ObjectPositionWS is NOT it: in a
# pixel shader GetObjectWorldPosition reads the primitive's (the ISM component's) bounds origin (MaterialTemplate.ush MakeMaterialLWCData
# for FMaterialPixelParameters); only the vertex overload has the instancing branch. The instance transform reads GetInstanceToWorld, and the
# translator sets bUsesInstanceLocalToWorldPS (HLSLMaterialTranslator.cpp MCB_Instance), so HAS_INSTANCE_LOCAL_TO_WORLD_PS covers ISM
# (USE_INSTANCE_CULLING) and Nanite (IS_NANITE_PASS) draws (MaterialTemplate.ush:159, 885-891).
BUILTIN_INPUTS = {"WP": "world_position", "OP": "instance_origin", "VN": "vertex_normal", "PD": "pixel_depth", "VC": "vertex_color",
                  "CD0": ("custom_data", 0), "CD1": ("custom_data", 1), "CD2": ("custom_data", 2), "CD3": ("custom_data", 3)}


# ---------------------------------------------------------------------------------------------------------------- HLSL sections
def parse_hlsl(text=None):
    """{'common': str, 'sections': {name: {'inputs': [..], 'outputs': [(name, type)], 'body': str, 'includes': [..]}}} from ChimeraScatter.hlsl.
    `//#INCLUDE <file>` lines are expanded first (Scripts/hlsl_include.py, the expansion the ground material uses), so every body is the exact
    Custom-node text."""
    if text is None:
        with open(HLSL_PATH, encoding="utf-8", newline="") as f:
            text = f.read()
    # LF only: a checkout under core.autocrlf=true may write CRLF, and the //#SECTION / //#INPUTS patterns end at `$` (hlsl_include.to_lf).
    text = hlsl_include.to_lf(text)
    parts = re.split(r"^//#SECTION[ \t]+(\w+)[ \t]*$", text, flags=re.M)
    sections, common = {}, ""
    for i in range(1, len(parts), 2):
        name, body = parts[i], parts[i + 1]
        inputs, outputs, lines = [], [], []
        for ln in body.split("\n"):
            m = re.match(r"^//#INPUTS[ \t]+(.*)$", ln)
            if m:
                inputs = m.group(1).split()
                continue
            m = re.match(r"^//#OUTPUTS[ \t]+(.*)$", ln)
            if m:
                outputs = [tuple(x.split(":")) for x in m.group(1).split()]
                continue
            lines.append(ln)
        body = "\n".join(lines).strip("\n") + "\n"
        body, includes = hlsl_include.expand(body)
        if name == "Common":
            common = body
        else:
            sections[name] = {"inputs": inputs, "outputs": outputs, "body": body, "includes": includes}
    return {"common": common, "sections": sections}


def custom_code(section, parsed=None):
    """The Custom node code of a section: COMMON in front of the body."""
    parsed = parsed or parse_hlsl()
    return parsed["common"] + "\n" + parsed["sections"][section]["body"]


def ground_scalars():
    """SCALARS of make_ground_material.py as {name: float}, read with ast (the script cannot be imported)."""
    with open(GROUND_SCRIPT, encoding="utf-8") as f:
        tree = ast.parse(f.read())
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "SCALARS" for t in node.targets):
            return {k: float(v) for k, v in ast.literal_eval(node.value)}
    raise RuntimeError("SCALARS not found in " + GROUND_SCRIPT)


def texture_stats():
    with open(TEXTURE_STATS, encoding="utf-8") as f:
        return json.load(f)["textures"]


def meshes_config():
    with open(MESHES_JSON, encoding="utf-8") as f:
        return json.load(f)


# ---------------------------------------------------------------------------------------------------------------- masters
def _ground_params():
    g = ground_scalars()
    return {n: ("s", g[n]) for n in GROUND_NAMES}


def masters():
    """The seven master materials. params: name -> (kind, default); kinds: s scalar, v vector (RGBA), t texture object parameter
    (default = a role in DEFAULT_TEXTURES), tx fixed texture object (an engine path). `samples` are standard TextureSample parameters
    (the Tex family: albedo, packed AO/roughness/metal, normal) that feed the Custom node, the opacity mask and the normal pin."""
    blade = {"Root": ("v", (0.2, 0.3, 0.08, 1.0)), "Tip": ("v", (0.55, 0.7, 0.28, 1.0)),
             "LushMul": ("v", (1.0, 1.0, 1.0, 1.0)), "DryMul": ("v", (1.15, 1.0, 0.75, 1.0)),
             "HeadYellow": ("v", (1.0, 0.85, 0.15, 1.0)), "HeadWhite": ("v", (0.85, 0.85, 0.8, 1.0)),
             "HeadViolet": ("v", (0.4, 0.25, 0.7, 1.0)),
             "FlowerMode": ("s", 0.0), "TerrainNormalMix": ("s", 0.75), "Rough": ("s", 0.85), "AOStrength": ("s", 1.0),
             "VarAmt": ("s", 0.10), "PatchAmount": ("s", 0.6), "PixelAngle": ("s", PIXEL_ANGLE), "NoiseTex": ("tx", NOISE_TEX)}
    blade.update(_ground_params())
    tri = {"Tint": ("v", (0.1, 0.19, 0.06, 1.0)), "TexLumaMean": ("s", 0.244), "TexAmt": ("s", 0.7), "TileM": ("s", 1.5),
           "MipShift": ("s", 0.0), "PixelAngle": ("s", PIXEL_ANGLE), "NormalStrength": ("s", 0.5), "Rough": ("s", 0.85),
           "AOStrength": ("s", 1.0), "VarAmt": ("s", 0.10), "StandAmt": ("s", 0.08), "PartVar": ("s", 0.15), "HeightGain": ("s", 0.10),
           "TexC": ("t", "color"), "TexN": ("t", "normal")}
    tex = {"Tint": ("v", (1.0, 1.0, 1.0, 1.0)), "VarAmt": ("s", 0.10), "RoughMul": ("s", 1.0), "AOFromR": ("s", 1.0)}
    samples = {"BaseColorTex": "color", "NormalTex": "normal", "ARMTex": "masks"}
    return {
        "M_ScatterBlade": {"section": "Blade", "blend": "opaque", "two_sided": True, "nanite": True, "normal": "custom", "params": blade},
        # The grass _L A/B (Nanite off, 3 LODs): the Blade with the ISM fade. Opacity mask = PerInstanceFadeAmount + 0.5 - vertex colour G
        # (one value per blade), clip 0.5: as an instance fades its blades drop out one by one in G order instead of popping together.
        "M_ScatterBladeFade": {"section": "Blade", "blend": "masked", "clip": 0.5, "two_sided": True, "nanite": False, "normal": "custom",
                               "fade": "blade", "params": blade},
        "M_ScatterTriplanar": {"section": "Triplanar", "blend": "opaque", "two_sided": False, "nanite": True, "normal": "custom", "params": tri},
        "M_ScatterTexOpaque": {"section": "Tex", "blend": "opaque", "two_sided": False, "nanite": True, "normal": "texture",
                               "params": tex, "samples": samples},
        # L1 trunks, branches and shrub bark: the same opaque Tex master, two-sided (open after decimation, see above).
        "M_ScatterTexOpaque2S": {"section": "Tex", "blend": "opaque", "two_sided": True, "nanite": True, "normal": "texture",
                                 "params": tex, "samples": samples},
        "M_ScatterTexMasked": {"section": "Tex", "blend": "masked", "clip": 0.5, "two_sided": True, "nanite": True, "normal": "texture",
                               "params": tex, "samples": samples},
        "M_ScatterCard": {"section": "Tex", "blend": "masked", "clip": 0.33, "two_sided": True, "nanite": False, "normal": "texture",
                          "fade": "alpha", "params": tex, "samples": samples},
    }


def master_reads_vertex_colour(name, parsed=None):
    """True when master `name` reads the mesh's vertex colour: its HLSL section takes the VC input (Blade: AO, part variation, head mask and
    height; Triplanar: AO, part variation and height gain) or its opacity mask is the blade fade (vertex colour G). A mesh whose material
    reads it must carry COLOR_0 in its final saved asset (make_scatter_assets.py vc gate); the Tex family does not read it."""
    spec = masters()[name]
    parsed = parsed or parse_hlsl()
    return "VC" in parsed["sections"][spec["section"]]["inputs"] or spec.get("fade") == "blade"


# ---------------------------------------------------------------------------------------------------------------- L0 / L1 rules
def _scale(c, k):
    return [round(float(x) * k, 4) for x in c]


def l0_rule(species, glb_material):
    """(master, {scalar: value}, {vector: [r,g,b,a]}, {texture parameter: asset name}) for one glTF material of an L0 mesh.
    `species` is the meshes.json species row. Raises KeyError for a material the table does not know."""
    kind = species["kind"]
    stats = texture_stats()
    if glb_material in ("M_Blade", "M_Frond", "M_FlowerStem", "M_FlowerHead"):
        scal = {"Rough": species["roughness"], "FlowerMode": 0.0}
        vec = {}
        if kind in ("grass", "tussock"):
            vec["Root"], vec["Tip"] = species["root"] + [1.0], species["tip"] + [1.0]
        elif kind == "flower":
            vec["Root"], vec["Tip"] = species["stem"] + [1.0], _scale(species["stem"], 1.25) + [1.0]
            vec["HeadYellow"] = species["head"] + [1.0]
            scal["FlowerMode"] = 1.0
        elif kind == "fern":
            vec["Root"], vec["Tip"] = _scale(species["colour"], 0.8) + [1.0], _scale(species["colour"], 1.25) + [1.0]
            # Ferns are a coarse-grid class: CD0 is per-instance variation (not dryness) and CD2/CD3 are absent (read 0), so there is no
            # terrain normal to mix and no dryness: TerrainNormalMix 0 and DryMul = LushMul (CD1, the stand, still varies the tint by VarAmt).
            scal["TerrainNormalMix"] = 0.0
            vec["DryMul"] = [1.0, 1.0, 1.0, 1.0]
        else:
            raise KeyError(f"{species['name']}: {glb_material} on a {kind}")
        return "M_ScatterBlade", scal, vec, {}
    if glb_material == "M_Rock":
        return ("M_ScatterTriplanar", {"TexLumaMean": ground_scalars()["RockLumaMean"], "TexAmt": 0.8, "TileM": 1.5, "NormalStrength": 0.6, "Rough": species["roughness"],
                                       "VarAmt": 0.15, "PartVar": 0.0, "HeightGain": 0.0},
                {"Tint": _scale(species["colour"], ROCK_ALBEDO_SCALE) + [1.0]}, {"TexC": GROUND_ROCK["TexC"], "TexN": GROUND_ROCK["TexN"]})
    if glb_material in ("M_ShrubLeaf", "M_Canopy", "M_Needles"):
        tile = {"shrub": 0.6, "broadleaf": 1.2, "conifer": 1.0}[kind]
        return ("M_ScatterTriplanar", {"TexLumaMean": stats["leafy_grass"]["mean_linear_luma"], "TileM": tile, "Rough": species["roughness"]},
                {"Tint": species["leaf"] + [1.0]}, {"TexC": "T_Leaf_C", "TexN": "T_Leaf_N"})
    if glb_material in ("M_Bark", "M_ShrubBark"):
        return ("M_ScatterTexOpaque", {"RoughMul": 1.0, "AOFromR": 1.0}, {"Tint": [1.0, 1.0, 1.0, 1.0]},
                {"BaseColorTex": "T_Bark_C", "NormalTex": "T_Bark_N", "ARMTex": "T_Bark_ARM"})
    raise KeyError(f"unknown L0 material {glb_material} on {species['name']}")


def is_arm_image(name):
    """True when a glTF metallicRoughness image is a packed AO/roughness/metal map (Poly Haven `<id>_arm_<res>`), so its R channel is ambient
    occlusion. A plain roughness map (rock_moss_set_01_rough_1k) is not: its R is roughness."""
    return bool(name) and re.search(r"_arm(_\d+k)?$", str(name)) is not None


def l1_rule(mesh_name, slot, glb_material):
    """(master, {scalar: value}, {vector: [r,g,b,a]}) for one glTF material of an L1 mesh. `glb_material` carries 'alphaMode',
    'baseColorFactor' (or None) and 'arm' (the metallicRoughness image name, or None). The textures come from the Interchange import
    (make_scatter_assets.py). Opaque L1 rocks are closed: the one-sided TexOpaque. Opaque L1 trunks, branches and shrub bark are open after
    S1's decimation: the two-sided TexOpaque2S. Masked ones (leaves, twigs, cards) are two-sided. The prepared glbs all say doubleSided=true
    (the Blender export), so that flag is not used for L1.
    AOFromR = 0 when the packed map is not an ARM map (its R is not ambient occlusion)."""
    mode = glb_material.get("alphaMode", "OPAQUE")
    f = glb_material.get("baseColorFactor") or [1.0, 1.0, 1.0, 1.0]
    if slot == "NearCard":
        master = "M_ScatterCard"
    elif mode == "MASK":
        master = "M_ScatterTexMasked"
    else:
        master = "M_ScatterTexOpaque" if slot.startswith(L1_ONE_SIDED_SLOT_PREFIXES) else "M_ScatterTexOpaque2S"
    arm = glb_material.get("arm")
    ao = 1.0 if (arm is None or is_arm_image(arm)) else 0.0  # no image: the default ARM (R = 1) is bound, so AO is 1 either way
    return master, {"RoughMul": 1.0, "AOFromR": ao}, {"Tint": [float(f[0]), float(f[1]), float(f[2]), 1.0]}


def is_nanite_slot(slot):
    return slot not in NANITE_OFF_SLOTS


# ---------------------------------------------------------------------------------------------------------------- S6 look-round material scalars
# Task S6 (plan 3.9: "a round changes only palette constants, the asset level per slot, material scalars, or SunContactShadowM"): material
# parameter values that replace what l0_rule / l1_rule give, keyed by the material instance name make_scatter_assets.py builds
# ('MI_<mesh>_<glTF material>'; the grass _L copies inherit their original's values). Scalars and vectors only: no master, blend, texture or
# usage changes, so asset_settings_sha256 (S6's from-clean comparison) does not move. meshes.json stays the S1 mesh source (its colours are
# also written into the glbs, whose sha256 the manifest pins), so the round's colours live here.
# Round 2 (art director's round-1 verdict, 2026-10-03), linear colours:
#  * grass blades: tips were straw-cream (0.55,0.70,0.28), closeup V 0.43 against Manor Lords' 0.17-0.20 -> Tip (0.18,0.26,0.06), Root
#    (0.05,0.09,0.02); PatchAmount 0.6 -> 0.9 so the blades carry the ground's dry/lush field. Tussocks and flower stems darkened alike so the
#    tussock islands stay darker than the meadow and the stems match it; HeadWhite and HeadViolet brighter so drifts read.
#  * L0 broadleaf canopy: Tint (0.10,0.19,0.06) read as a bright toy (V 0.48, S 0.59) -> about 0.3x and warmer (0.035,0.055,0.015), aiming
#    at Manor Lords' V ~0.26 and hue 75-80; NormalStrength 0.5 -> 1.0, TileM 1.2 -> 0.8 m and TexAmt 0.7 -> 0.85 so the leaf texture breaks
#    up the smooth lobes.
#  * L0 conifers warmed from hue ~128 toward ~95; L0 shrub leaf masses about 0.4x (V ~0.22, hue ~75 on screen).
#  * L1 tree_small_02 leaves (the art director's one check run of L1, judged at oblique): Tint (1.0,0.9,0.55) moves the blue-grey leaves
#    from screen hue ~96 toward ~75, RoughMul 1.5 kills the sheen.
#  * L1 grass cards (the grass slot's comparison route): Tint maps each texture's mean linear colour (grass_medium_02 (0.311,0.300,0.142),
#    seed-card atlas (0.251,0.228,0.129), measured on the prepared images) onto the blade target's mean (0.115,0.175,0.04) and a drier
#    seed-head target (0.13,0.16,0.05).
# Round 3 (art director's round-2 verdict, 2026-10-03):
#  * tussocks are the rts80 mid-frequency carrier: Root (0.045,0.06,0.02) -> (0.035,0.045,0.015), Tip (0.17,0.19,0.07) -> (0.12,0.14,0.05),
#    so the (now larger, more frequent) islands read darker than the meadow at RTS height.
#  * rocks read near-black: Tint x1.5 (RockA (0.12,0.115,0.105) -> (0.18,0.1725,0.1575), RockB (0.135,0.125,0.11) -> (0.2025,0.1875,0.165)).
#  * shrubs read as smooth boulders: leaf-texture scalars TexAmt 1.0, TileM 0.3 m, NormalStrength 2.0 (the art director's numbers), VarAmt 0.2.
#  * canopy tint spread through custom data: VarAmt 0.10 -> 0.20 (cd0, about +-20 % value per tree) on the L0 canopies and the L1 leaves;
#    HeightGain 0.10 -> 0.20 on the L0 canopies (brighter crown tops from the vertex height).
_BLADE_ROOT, _BLADE_TIP = [0.05, 0.09, 0.02, 1.0], [0.18, 0.26, 0.06, 1.0]
_CANOPY_L0 = {"NormalStrength": 1.0, "TileM": 0.8, "TexAmt": 0.85, "VarAmt": 0.20, "HeightGain": 0.20}
_SHRUB_L0 = {"TexAmt": 1.0, "TileM": 0.3, "NormalStrength": 2.0, "VarAmt": 0.20}
_L1_LEAVES = {"RoughMul": 1.5, "VarAmt": 0.20}
LOOK_OVERRIDES = {
    "MI_GrassT0_M_Blade": {"vectors": {"Root": _BLADE_ROOT, "Tip": _BLADE_TIP}, "scalars": {"PatchAmount": 0.9}},
    "MI_GrassT1_M_Blade": {"vectors": {"Root": _BLADE_ROOT, "Tip": [0.20, 0.25, 0.07, 1.0]}, "scalars": {"PatchAmount": 0.9}},
    "MI_Tussock_M_Blade": {"vectors": {"Root": [0.035, 0.045, 0.015, 1.0], "Tip": [0.12, 0.14, 0.05, 1.0]}, "scalars": {"PatchAmount": 0.9}},
    "MI_Flower_M_FlowerStem": {"vectors": {"Root": [0.06, 0.11, 0.025, 1.0], "Tip": [0.075, 0.135, 0.03, 1.0]}},
    "MI_Flower_M_FlowerHead": {"vectors": {"HeadWhite": [1.0, 1.0, 0.95, 1.0], "HeadViolet": [0.55, 0.35, 0.9, 1.0]}},
    "MI_TreeBroadA_M_Canopy": {"vectors": {"Tint": [0.035, 0.055, 0.015, 1.0]}, "scalars": dict(_CANOPY_L0)},
    "MI_TreeBroadB_M_Canopy": {"vectors": {"Tint": [0.04, 0.06, 0.016, 1.0]}, "scalars": dict(_CANOPY_L0)},
    "MI_TreeConiferA_M_Needles": {"vectors": {"Tint": [0.075, 0.12, 0.04, 1.0]}},
    "MI_TreeConiferB_M_Needles": {"vectors": {"Tint": [0.08, 0.13, 0.045, 1.0]}},
    "MI_ShrubA_M_ShrubLeaf": {"vectors": {"Tint": [0.045, 0.07, 0.02, 1.0]}, "scalars": dict(_SHRUB_L0)},
    "MI_ShrubB_M_ShrubLeaf": {"vectors": {"Tint": [0.05, 0.078, 0.022, 1.0]}, "scalars": dict(_SHRUB_L0)},
    "MI_RockA_M_Rock": {"vectors": {"Tint": [0.18, 0.1725, 0.1575, 1.0]}},
    "MI_RockB_M_Rock": {"vectors": {"Tint": [0.2025, 0.1875, 0.165, 1.0]}},
    "MI_TreeBroadA_tree_small_02_leaves": {"vectors": {"Tint": [1.0, 0.9, 0.55, 1.0]}, "scalars": dict(_L1_LEAVES)},
    "MI_TreeBroadB_tree_small_02_leaves": {"vectors": {"Tint": [1.0, 0.9, 0.55, 1.0]}, "scalars": dict(_L1_LEAVES)},
    "MI_GrassT0__medium02_grass_medium_02": {"vectors": {"Tint": [0.37, 0.58, 0.28, 1.0]}},
    "MI_GrassT1__seedcard_M_SeedCard": {"vectors": {"Tint": [0.52, 0.70, 0.38, 1.0]}},
}


def apply_look(mic_name, scalars, vectors):
    """(scalars, vectors) with the round's LOOK_OVERRIDES for instance `mic_name` merged over the rule's values (new dicts; the inputs are
    not changed). Raises KeyError when an override names a parameter the rule did not set and the instance's master does not have (checked
    by the caller through master_params)."""
    o = LOOK_OVERRIDES.get(mic_name)
    if not o:
        return dict(scalars), dict(vectors)
    s, v = dict(scalars), dict(vectors)
    s.update(o.get("scalars", {}))
    v.update(o.get("vectors", {}))
    return s, v


def master_params(master_name):
    """{name: kind} of a master's parameters (kinds as in masters())."""
    return {k: kind for k, (kind, _d) in masters()[master_name]["params"].items()}
