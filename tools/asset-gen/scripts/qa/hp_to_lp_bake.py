# -*- coding: utf-8 -*-
"""
High-poly -> low-poly retopo + TEXTURE BAKE for the asset-gen pipeline.

Run:
  blender -b -P hp_to_lp_bake.py -- --in <hp.glb> --out <lp.glb> --profile <godot_chimera.json>
                                    [--kind unit|building|prop] [--tex 1024] [--ao]
                                    [--no-normal] [--hp-test-material]

This is the stage `blender_pipeline.py` does NOT do: it carries the high-poly's SURFACE
(base colour, and optionally a tangent normal map) onto the decimated low-poly via a
selected-to-active Cycles bake, so the shipped GLB is textured instead of flat grey.

Adapted from Building Aeon's `hp_to_lp_bake_v2.py` (Unity-targeted) and retuned to Project
Chimera's engine profile. Deliberate departures from that script:

  * Budgets come from the ENGINE PROFILE, not constants — 6k unit / 10k building, not 39k.
    Chimera renders 500-2000 entities through MultiMesh at 30 ticks/sec; a 39k hero prop
    budget is ~6x over.
  * Textures default to 1024 (profile `texture.min_dim`), not 8192-baked-to-4096. Units read
    at ~40px on an RTS camera, and 24 assets x 4K normal maps is ~500MB of git history.
  * NO `_LOD0.._LOD3` chain. That naming exists to feed Unity's LODGroup; Godot builds mesh
    LODs at import time, so the chain is dead weight here.
  * Exactly ONE material (profile `max_materials: 1`), and PLAIN GLB — no Draco/meshopt/
    quantization, which Godot 4.6.2 rejects at runtime with err 43.

Emits one JSON line: BAKE_JSON {...}. Exit 0 on success.

NOTE ON THE HIGH-POLY SOURCE: a bake copies the high-poly's own materials. Baking from an
untextured mesh (e.g. Hunyuan3D shape-only output) yields flat grey — correctly, not as a
bug. Use --hp-test-material to prove the plumbing with a known-visible procedural source.
"""
import bpy, bmesh, sys, os, json, math, argparse

# ── Tunables not worth a CLI flag ────────────────────────────────────────────
MIN_SHELL_FACES   = 500     # loose shells smaller than this are AI debris; drop them
DETACHED_PROP_FRAC = 0.15   # a loose shell under this fraction of the biggest one is a held prop
HIDDEN_PREPASS    = 120000  # decimate to this before the expensive work, if denser
BAKE_SAMPLES      = 4       # bake is a surface transfer, not a render — 4 is plenty
UV_ANGLE_LIMIT    = 66.0    # degrees, smart-project seam angle
UV_ISLAND_MARGIN  = 0.003
AO_STRENGTH       = 0.5
VOXEL_DIVISIONS   = 240     # matches blender_pipeline.py's remesh density
DUMP_PROJECTION   = None    # set by --dump-projection; writes the edge-padded concept for inspection


def argv_after_ddash():
    a = sys.argv
    return a[a.index("--") + 1:] if "--" in a else []


def log(obj):
    print("BAKE_JSON " + json.dumps(obj))


# ── Scene / import ───────────────────────────────────────────────────────────

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_any(path):
    p = path.lower()
    if p.endswith(".glb") or p.endswith(".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif p.endswith(".obj"):
        bpy.ops.wm.obj_import(filepath=path)
    elif p.endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path)
    elif p.endswith(".stl"):
        bpy.ops.wm.stl_import(filepath=path)
    else:
        raise SystemExit("unsupported input: " + path)


def all_meshes():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def select_only(objs, active=None):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or (objs[0] if objs else None)


def join_meshes():
    meshes = all_meshes()
    if not meshes:
        raise SystemExit("no mesh objects after import")
    select_only(meshes, meshes[0])
    if len(meshes) > 1:
        bpy.ops.object.join()
    return bpy.context.view_layer.objects.active


def tri_count(obj):
    me = obj.data
    me.calc_loop_triangles()
    return len(me.loop_triangles)


def max_dim(obj):
    return max(obj.dimensions.x, obj.dimensions.y, obj.dimensions.z) or 1.0


# ── Topology ─────────────────────────────────────────────────────────────────

def triangulate(obj):
    m = obj.modifiers.new("tri", "TRIANGULATE")
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)


def apply_decimate(obj, ratio):
    m = obj.modifiers.new("dec", "DECIMATE")
    m.decimate_type = "COLLAPSE"
    m.ratio = max(0.002, min(1.0, ratio))
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)


def voxel_remesh(obj):
    bpy.context.view_layer.objects.active = obj
    obj.data.remesh_voxel_size = max(max_dim(obj) / float(VOXEL_DIVISIONS), 1e-4)
    obj.data.remesh_voxel_adaptivity = 0.0
    obj.data.use_remesh_fix_poles = True
    bpy.ops.object.voxel_remesh()


def weld(obj, rel=1e-4):
    """Merge coincident vertices. MUST run before any connectivity-based step.

    glTF stores normals/UVs per CORNER, so Blender's importer splits a vertex wherever those
    differ — on a smooth-shaded AI mesh that is essentially every vertex. Unwelded, the mesh
    has no connectivity at all: `separate(type='LOOSE')` sees each triangle as its own shell
    (measured: 5981 "shells" in a 5982-triangle asset) and debris removal deletes the model.
    """
    select_only([obj], obj)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=max(1e-6, rel * max_dim(obj)))
    bpy.ops.object.mode_set(mode="OBJECT")
    return len(obj.data.vertices)


def drop_debris_shells(obj):
    """Separate loose parts, delete debris shells, rejoin. Returns (obj, stats).

    AI-generated meshes routinely carry stray faces (thin flaps, floating specks). They
    survive decimation, waste UV space, and shadow the bake with garbage.

    The threshold is SCALE-RELATIVE, not the source script's flat 500 faces. Hunyuan3D
    surface-net output is many small shells, so on an already-decimated 6k-tri asset a flat
    500 deletes the entire model. Rule: drop shells under 1% of the mesh, capped at
    MIN_SHELL_FACES so a dense 200k high-poly does not get an absurdly high bar — and the
    largest shell is kept unconditionally, so this can never empty the scene.
    """
    total = len(obj.data.polygons)
    select_only([obj], obj)
    # mesh.separate is an EDIT-mode operator; calling it from object mode fails the poll.
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    parts = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    stats = {"shells": len(parts), "threshold": 0, "dropped": 0, "faces_dropped": 0}
    if len(parts) <= 1:
        return obj, stats

    largest = max(parts, key=lambda o: len(o.data.polygons))
    largest_faces = len(largest.data.polygons)
    # Two rules, whichever is stricter:
    #   * absolute/scale-relative — kills specks and thin flaps.
    #   * a fraction of the LARGEST shell — kills DETACHED PROPS. Image-to-3D reconstructs a held
    #     object (a bucket, a lantern) as its own floating shell; it clears any speck threshold but
    #     is small next to the body, and it must not ship as part of a unit.
    threshold = max(8,
                    min(MIN_SHELL_FACES, int(0.01 * total)),
                    int(DETACHED_PROP_FRAC * largest_faces))
    stats["threshold"] = threshold
    stats["largest_shell_faces"] = largest_faces

    keep = []
    for o in parts:
        if o is largest or len(o.data.polygons) >= threshold:
            keep.append(o)
        else:
            stats["dropped"] += 1
            stats["faces_dropped"] += len(o.data.polygons)
            bpy.data.objects.remove(o, do_unlink=True)

    select_only(keep, keep[0])
    if len(keep) > 1:
        bpy.ops.object.join()
    return bpy.context.view_layer.objects.active, stats


def process_to_budget(obj, target):
    """Crisp-first: direct collapse; voxel-remesh fallback only if direct can't hit budget.

    Mirrors blender_pipeline.py's proven strategy so the two scripts cannot disagree about
    what a 'unit-budget mesh' means. Clean single-subject meshes stay crisp; multi-shell
    surface-net junk (Hunyuan3D's usual output) gets cleaned first.
    """
    triangulate(obj)
    before = tri_count(obj)
    if before > HIDDEN_PREPASS:
        apply_decimate(obj, float(HIDDEN_PREPASS) / float(before))
    cur = tri_count(obj)
    if cur > target:
        apply_decimate(obj, float(target) / float(cur))
    after = tri_count(obj)
    method = "direct-collapse"

    if after > target * 1.5:
        voxel_remesh(obj)
        triangulate(obj)
        cur = tri_count(obj)
        if cur > target:
            apply_decimate(obj, float(target) / float(cur))
        after = tri_count(obj)
        method = "voxel-remesh+collapse"
    return before, after, method


# ── UVs ──────────────────────────────────────────────────────────────────────

def unwrap(obj):
    """Uniform smooth shading + a clean smart-projected UV set.

    Hard edges and leftover seams make the bake cage split, which reads as black seams in
    the baked texture — so both are cleared before projecting.
    """
    select_only([obj], obj)
    bpy.ops.object.shade_smooth()

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.mark_seam(clear=True)
    bpy.ops.mesh.mark_sharp(clear=True)
    # Voxel-remesh + decimate can leave flipped faces, which the QA gate reports as inconsistent
    # winding and Godot renders as dark or one-sided surfaces. Fix before the bake so the cage rays
    # fire outward from every face.
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.uv.smart_project(angle_limit=math.radians(UV_ANGLE_LIMIT),
                             island_margin=UV_ISLAND_MARGIN)
    bpy.ops.object.mode_set(mode="OBJECT")


# ── Cycles / GPU ─────────────────────────────────────────────────────────────

def enable_gpu(scene):
    """Pick the best available compute backend. Headless Blender defaults to CPU."""
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
    except KeyError:
        return "CPU (cycles prefs unavailable)"

    for backend in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
        try:
            prefs.compute_device_type = backend
        except TypeError:
            continue                      # backend not compiled into this build
        try:
            prefs.get_devices()
        except Exception:
            pass
        devs = [d for d in prefs.devices if d.type == backend]
        if devs:
            for d in prefs.devices:
                d.use = (d.type == backend)
            scene.cycles.device = "GPU"
            return "%s:%s" % (backend, devs[0].name)

    scene.cycles.device = "CPU"
    return "CPU"


def setup_cycles(scene):
    scene.render.engine = "CYCLES"
    scene.cycles.samples = BAKE_SAMPLES
    scene.cycles.use_denoising = False
    scene.cycles.use_adaptive_sampling = False
    return enable_gpu(scene)


# ── Bake ─────────────────────────────────────────────────────────────────────

# Pre-fill colour for a bake target. The baker writes RGB+alpha=1 only where a ray hit, so
# any texel still carrying this exact value afterwards is a MISS. A black pre-fill cannot
# serve: a NORMAL bake clears to (0.5,0.5,1.0) and legitimately-black albedo exists, so
# "is it black" identifies neither pass's misses correctly.
MISS_SENTINEL = (1.0, 0.0, 1.0, 0.0)


def new_image(name, size, is_data, fmt, fill=MISS_SENTINEL):
    img = bpy.data.images.new(name, width=size, height=size, alpha=True, float_buffer=False)
    img.file_format = fmt
    if is_data:
        img.colorspace_settings.name = "Non-Color"
    img.generated_color = fill
    return img


def target_material(obj, name="chimera_baked"):
    """The single material the baked images land in — profile requires max_materials == 1."""
    obj.data.materials.clear()
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    obj.data.materials.append(mat)
    return mat


def bake_image_node(mat, img):
    """Create the bake destination node.

    GOTCHA (from the source script, kept): the image node must be both SELECTED and ACTIVE
    in the node tree or Cycles bakes into whichever node happens to be active instead.
    """
    nt = mat.node_tree
    node = nt.nodes.new("ShaderNodeTexImage")
    node.image = img
    node.select = True
    nt.nodes.active = node
    return node


def do_bake(hp, lp, bake_type, extrusion, ray_dist, tex_size, use_clear=False, **kw):
    select_only([hp], hp)
    lp.select_set(True)
    bpy.context.view_layer.objects.active = lp     # active == bake TARGET
    bpy.ops.object.bake(
        type=bake_type,
        use_selected_to_active=True,
        cage_extrusion=extrusion,
        max_ray_distance=ray_dist,
        margin=max(2, tex_size // 256),
        margin_type="ADJACENT_FACES",
        use_clear=use_clear,           # False: preserve the sentinel so misses stay findable
        **kw
    )


def pixels_of(img):
    import numpy as np
    buf = np.empty(len(img.pixels), dtype=np.float32)
    img.pixels.foreach_get(buf)
    return buf


def set_pixels(img, buf):
    img.pixels.foreach_set(buf)
    img.update()


def miss_mask(buf4):
    """Texels the baker never wrote: alpha still 0 AND RGB still the magenta sentinel."""
    import numpy as np
    return ((buf4[:, 3] < 0.5)
            & (np.abs(buf4[:, 0] - MISS_SENTINEL[0]) < 1e-3)
            & (np.abs(buf4[:, 1] - MISS_SENTINEL[1]) < 1e-3)
            & (np.abs(buf4[:, 2] - MISS_SENTINEL[2]) < 1e-3))


def composite_fill(tight, fallback, neutral):
    """Fill texels the tight pass could not reach using the wide-ray fallback pass.

    A short cage hugs the surface (accurate) but misses deep recesses. A long cage reaches
    them but smears elsewhere. Baking both and taking the fallback ONLY where tight missed
    gets accuracy plus coverage. Anything still unfilled becomes `neutral` — a black normal
    texel is a catastrophic normal, whereas a mid-grey albedo texel is just a dull patch.
    """
    t = pixels_of(tight).reshape(-1, 4)
    f = pixels_of(fallback).reshape(-1, 4)

    t_miss = miss_mask(t)
    f_miss = miss_mask(f)

    recover = t_miss & ~f_miss
    filled_from_fallback = int(recover.sum())
    t[recover] = f[recover]

    still_missing = t_miss & f_miss
    unfilled = int(still_missing.sum())
    t[still_missing] = neutral

    t[:, 3] = 1.0            # alpha was only ever the hit mask; ship it opaque
    set_pixels(tight, t.reshape(-1))
    return filled_from_fallback, unfilled


def bake_pass(hp, lp, mat, bake_type, img_name, tex_size, md, is_data, neutral, **kw):
    """Two-cage bake (tight + fallback) composited into one image. Returns the image."""
    tight    = new_image(img_name, tex_size, is_data, "PNG" if is_data else "JPEG")
    fallback = new_image(img_name + "_fb", tex_size, is_data, "PNG")

    node = bake_image_node(mat, tight)
    do_bake(hp, lp, bake_type, 0.013 * md, 0.029 * md, tex_size, **kw)

    node.image = fallback
    node.select = True
    mat.node_tree.nodes.active = node
    do_bake(hp, lp, bake_type, 0.020 * md, 0.092 * md, tex_size, **kw)

    filled, unfilled = composite_fill(tight, fallback, neutral)
    bpy.data.images.remove(fallback)
    mat.node_tree.nodes.remove(node)
    return tight, filled, unfilled


def apply_ao(color_img, ao_img):
    """Multiply the AO bake into base colour, lerped by AO_STRENGTH."""
    import numpy as np
    c = pixels_of(color_img).reshape(-1, 4)
    a = pixels_of(ao_img).reshape(-1, 4)
    occ = a[:, 0:1]
    c[:, 0:3] *= (1.0 - AO_STRENGTH) + AO_STRENGTH * occ
    set_pixels(color_img, c.reshape(-1))


def wire_material(mat, color_img, normal_img):
    """Rebuild the target material as one Principled BSDF fed by the baked images."""
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); out.location = (600, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (250, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = color_img; tex.location = (-250, 150)
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

    if normal_img is not None:
        ntex = nt.nodes.new("ShaderNodeTexImage")
        ntex.image = normal_img; ntex.location = (-250, -200)
        nmap = nt.nodes.new("ShaderNodeNormalMap"); nmap.location = (0, -200)
        nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])

    # Flat-ish response: the RTS camera is distant, specular highlights just read as noise.
    if "Roughness" in bsdf.inputs:
        bsdf.inputs["Roughness"].default_value = 0.75
    if "Metallic" in bsdf.inputs:
        bsdf.inputs["Metallic"].default_value = 0.0


def prepare_concept(img, tol=0.10, pad_iterations=160, shadow_cut=0.06, edge_trim=3):
    """Return (padded_image, subject_bounds) for a concept plate.

    Two things must happen before a concept image can be projected onto a mesh:

    1. **Locate the subject.** Plates are a centred character on a flat background with wide margins,
       so mapping the mesh's bounding box to the whole IMAGE slides the art off the model. Background
       colour is sampled from an INSET ring, not the outermost pixels — SDXL plates carry a subtle
       frame/vignette at the very edge which reads as "subject" and blows the box out to the full
       image (measured: u0=0, v0=0 on a character that starts ~10% in).

    2. **Erase the background.** The mesh silhouette never matches the drawing exactly, so wherever
       the model is wider than the character the projection paints plate-grey onto it — a grey halo
       around shoulders and arms — and the drawing's contact shadow lands on the feet. Both are fixed
       by discarding background texels and flooding the subject's own colours outward (ordinary edge
       padding), so an over-wide mesh samples character colour instead of background.

    The bottom `shadow_cut` band is forced to background: a cast shadow is dark enough to pass the
    tolerance test as "subject" but must never be painted onto the model.
    """
    import numpy as np
    w, h = img.size
    buf = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(buf)
    px = buf.reshape(h, w, 4)                      # row 0 = BOTTOM (Blender images are bottom-up)
    rgb = px[:, :, :3].copy()
    alpha = px[:, :, 3]

    # Prefer the matte clean_concept.py already computed. Re-deriving the subject from colour is
    # strictly worse: the matte's anti-aliased edge blends toward white, those near-white pixels fail
    # a colour test against a white plate, and the flood below then propagates WHITE outward instead
    # of character colour — which is exactly the halo this function exists to prevent.
    if alpha.min() < 0.5:
        subject = alpha > 0.5
        used = "alpha"
    else:
        inset = max(2, min(w, h) // 50)
        ring = np.concatenate([rgb[inset, inset:w - inset], rgb[h - 1 - inset, inset:w - inset],
                               rgb[inset:h - inset, inset], rgb[inset:h - inset, w - 1 - inset]])
        bg = np.median(ring, axis=0)
        subject = np.abs(rgb - bg).max(axis=2) > tol
        subject[:max(1, int(h * shadow_cut)), :] = False   # bottom band = contact shadow
        used = "colour"

    rows = np.where(subject.any(axis=1))[0]
    cols = np.where(subject.any(axis=0))[0]
    if rows.size == 0 or cols.size == 0 or (cols[-1] - cols[0]) < w * 0.05:
        bounds = (0.0, 0.0, 1.0, 1.0)
    else:
        bounds = (float(cols[0]) / w, float(rows[0]) / h,
                  float(cols[-1] + 1) / w, float(rows[-1] + 1) / h)

    # Trim the matte inward before using it as the flood SOURCE. The outermost ring of a matte is
    # anti-aliased toward the plate colour; seeding from it would smear that fringe outward.
    known = subject.copy()
    for _ in range(edge_trim):
        known &= (np.roll(known, 1, 0) & np.roll(known, -1, 0)
                  & np.roll(known, 1, 1) & np.roll(known, -1, 1))
    if not known.any():                       # subject thinner than the trim — keep it as-is
        known = subject.copy()

    # Flood subject colour outward over the background (edge padding). All four directions are
    # sampled from the SAME snapshot each pass, so growth is isotropic; updating `known` inside the
    # direction loop instead lets one pass race ahead diagonally and leaves visible streaks.
    for _ in range(pad_iterations):
        if known.all():
            break
        snap_known, snap_rgb = known.copy(), rgb.copy()
        grew = False
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            src_known = np.roll(snap_known, (dy, dx), axis=(0, 1))
            src_rgb   = np.roll(snap_rgb,   (dy, dx), axis=(0, 1))
            fill = (~known) & src_known
            if not fill.any():
                continue
            rgb[fill] = src_rgb[fill]
            known |= fill
            grew = True
        if not grew:
            break

    padded = bpy.data.images.new(img.name + "_padded", width=w, height=h, alpha=False)
    out = np.concatenate([rgb, np.ones((h, w, 1), dtype=np.float32)], axis=2)
    padded.pixels.foreach_set(out.reshape(-1))
    padded.update()
    if DUMP_PROJECTION:
        padded.filepath_raw = DUMP_PROJECTION
        padded.file_format = "PNG"
        padded.save()
    return padded, bounds


def projection_material(obj, front_path, back_path=None, box_blend=None):
    # box_blend=None (FLAT) is the default on MEASURED evidence, not preference. Box/triplanar was
    # tried at blend 0.30 to kill the grazing-angle stretch and made every angle worse: a
    # voxel-remeshed mesh has noisy per-face normals, so box projection's plane choice flickers
    # face to face and fragments the clean front read into mush. Flat keeps a crisp front and
    # accepts the smear; the real fix is a properly textured high-poly, not a better projection.
    """Wrap the high-poly in its own concept art, projected orthographically front and back.

    This is the LOCAL substitute for a textured high-poly. Hunyuan3D is shape-only, so a bake from
    its raw output transfers nothing but grey; but the mesh was generated FROM the concept image and
    is aligned to it, so projecting that image back on is a real albedo source that costs one SDXL
    render we already produce per asset.

    Axes: glTF is Y-up and imports Z-up, so object X = across, Z = up, and the pipeline's enforced
    +Z glTF facing becomes -Y in Blender. Surfaces whose normal points at -Y take the front image;
    the rest take the back image (or a mirrored front when no back art exists).

    Known limits, in order of how much they show: the concept's own line art and contact shadow bake
    in as dark bands; surfaces near-perpendicular to both cameras smear; a mirrored front makes an
    asymmetric character symmetric from behind.
    """
    bpy.context.view_layer.objects.active = obj
    select_only([obj], obj)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    front, fb = prepare_concept(bpy.data.images.load(front_path, check_existing=True))
    # keep the alpha out of the sampled result; the padded copy is fully opaque by construction
    if back_path:
        back, bb = prepare_concept(bpy.data.images.load(back_path, check_existing=True))
    else:
        back, bb = front, fb

    co = [v.co for v in obj.data.vertices]
    minx = min(c.x for c in co); maxx = max(c.x for c in co)
    miny = min(c.y for c in co); maxy = max(c.y for c in co)
    minz = min(c.z for c in co); maxz = max(c.z for c in co)
    sx = (maxx - minx) or 1.0
    sy = (maxy - miny) or 1.0
    sz = (maxz - minz) or 1.0

    obj.data.materials.clear()
    mat = bpy.data.materials.new("hp_concept_projection")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()

    out = nt.nodes.new("ShaderNodeOutputMaterial"); out.location = (900, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (650, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    coord = nt.nodes.new("ShaderNodeTexCoord"); coord.location = (-800, 0)
    sep = nt.nodes.new("ShaderNodeSeparateXYZ"); sep.location = (-620, 0)
    nt.links.new(coord.outputs["Object"], sep.inputs["Vector"])

    def axis_to_uv(value_socket, lo, size, dst0, dst1, y):
        """object coord -> 0..1 across the mesh -> the subject's span in the image."""
        m = nt.nodes.new("ShaderNodeMath"); m.location = (-440, y)
        m.operation = "MULTIPLY_ADD"
        m.inputs[1].default_value = (dst1 - dst0) / size
        m.inputs[2].default_value = dst0 - lo * (dst1 - dst0) / size
        nt.links.new(value_socket, m.inputs[0])
        return m.outputs[0]

    def sample(image, bounds, mirror, y):
        """One projected sample of `image`.

        BOX (triplanar) projection is the default because a single flat projection STRETCHES every
        surface that runs parallel to the projection axis — and on an RTS camera that grazing band is
        most of what the player actually sees at 3/4 view. Box projection picks the best-aligned plane
        per face instead, so nothing smears: front/back faces get the true front mapping, side faces
        get the art mapped along depth (plausible rather than correct), top faces get it from above.
        """
        u = axis_to_uv(sep.outputs["X"], minx, sx, bounds[0], bounds[2], y)
        v = axis_to_uv(sep.outputs["Z"], minz, sz, bounds[1], bounds[3], y - 90)
        d = axis_to_uv(sep.outputs["Y"], miny, sy, bounds[0], bounds[2], y - 180)
        if mirror:
            inv = nt.nodes.new("ShaderNodeMath"); inv.location = (-260, y)
            inv.operation = "SUBTRACT"
            inv.inputs[0].default_value = bounds[0] + bounds[2]
            nt.links.new(u, inv.inputs[1])
            u = inv.outputs[0]
        comb = nt.nodes.new("ShaderNodeCombineXYZ"); comb.location = (-100, y)
        nt.links.new(u, comb.inputs["X"])
        nt.links.new(d, comb.inputs["Y"])
        nt.links.new(v, comb.inputs["Z"])
        tex = nt.nodes.new("ShaderNodeTexImage"); tex.location = (80, y)
        tex.image = image
        tex.extension = "EXTEND"
        if box_blend is not None:
            tex.projection = "BOX"
            tex.projection_blend = box_blend
        else:
            # Flat: only X/Y of the vector are read, so feed height in Y as a plain front projection.
            comb.inputs["Y"].default_value = 0.0
            nt.links.new(v, comb.inputs["Y"])
        nt.links.new(comb.outputs["Vector"], tex.inputs["Vector"])
        return tex.outputs["Color"]

    front_col = sample(front, fb, mirror=False, y=260)
    back_col = sample(back, bb, mirror=(back_path is None), y=-260)

    # Facing split: object-space normal Y < 0 selects the front image.
    geo = nt.nodes.new("ShaderNodeNewGeometry"); geo.location = (-800, -520)
    nsep = nt.nodes.new("ShaderNodeSeparateXYZ"); nsep.location = (-620, -520)
    nt.links.new(geo.outputs["Normal"], nsep.inputs["Vector"])
    facing = nt.nodes.new("ShaderNodeMath"); facing.location = (-440, -520)
    facing.operation = "LESS_THAN"
    facing.inputs[1].default_value = 0.0
    nt.links.new(nsep.outputs["Y"], facing.inputs[0])

    mix = nt.nodes.new("ShaderNodeMixRGB"); mix.location = (380, 0)
    nt.links.new(facing.outputs[0], mix.inputs["Fac"])
    nt.links.new(back_col, mix.inputs["Color1"])     # Fac 0 -> back
    nt.links.new(front_col, mix.inputs["Color2"])    # Fac 1 -> front
    nt.links.new(mix.outputs["Color"], bsdf.inputs["Base Color"])

    obj.data.materials.append(mat)
    return {"front_subject_uv": fb, "back_subject_uv": bb, "mirrored_back": back_path is None,
            "projection": "box" if box_blend is not None else "flat", "box_blend": box_blend}


def make_test_material(obj):
    """A loud procedural material for proving the bake transfers surface, not geometry."""
    obj.data.materials.clear()
    mat = bpy.data.materials.new("hp_test_source")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial"); out.location = (600, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (300, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    checker = nt.nodes.new("ShaderNodeTexChecker"); checker.location = (0, 0)
    checker.inputs["Scale"].default_value = 14.0
    checker.inputs["Color1"].default_value = (0.85, 0.10, 0.06, 1.0)   # oxblood
    checker.inputs["Color2"].default_value = (0.12, 0.35, 0.80, 1.0)   # slate-blue
    coord = nt.nodes.new("ShaderNodeTexCoord"); coord.location = (-250, 0)
    nt.links.new(coord.outputs["Object"], checker.inputs["Vector"])
    nt.links.new(checker.outputs["Color"], bsdf.inputs["Base Color"])
    obj.data.materials.append(mat)


# ── Placement + export (must match blender_pipeline.py's contract) ───────────

def origin_to_base(obj):
    """min-Z = 0, X/Y centred, origin at world zero — the gate checks all three."""
    bpy.context.view_layer.objects.active = obj
    select_only([obj], obj)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    corners = [obj.matrix_world @ v.co for v in obj.data.vertices]
    xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
    obj.location.x -= (min(xs) + max(xs)) / 2.0
    obj.location.y -= (min(ys) + max(ys)) / 2.0
    obj.location.z -= min(zs)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    bpy.context.scene.cursor.location = (0.0, 0.0, 0.0)
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")


def export_plain_glb(path, obj, jpeg_quality=90):
    """PLAIN GLB with embedded textures. No Draco/meshopt/quantization — Godot err 43."""
    select_only([obj], obj)
    kwargs = dict(
        filepath=path,
        export_format="GLB",
        export_yup=True,
        export_apply=True,
        use_selection=True,
        export_image_format="AUTO",     # honours each image datablock's file_format
    )
    try:
        bpy.ops.export_scene.gltf(export_draco_mesh_compression_enable=False,
                                  export_jpeg_quality=jpeg_quality, **kwargs)
    except TypeError:
        bpy.ops.export_scene.gltf(**kwargs)


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(prog="hp_to_lp_bake")
    ap.add_argument("--in", dest="src", required=True)
    ap.add_argument("--out", dest="dst", required=True)
    ap.add_argument("--profile", required=True, help="engine profile json (tri budgets, tex dims)")
    ap.add_argument("--kind", default="unit", choices=["unit", "building", "prop"])
    ap.add_argument("--tex", type=int, default=0, help="texture size; default = profile min_dim")
    ap.add_argument("--ao", action="store_true", help="bake AO and multiply into base colour")
    ap.add_argument("--no-normal", action="store_true", help="skip the normal map (smaller GLB)")
    ap.add_argument("--hp-test-material", action="store_true",
                    help="apply a procedural checker to the high-poly first (plumbing proof)")
    ap.add_argument("--project-front", help="concept image to project onto the high-poly as its albedo")
    ap.add_argument("--project-back", help="rear concept image; omitted = mirror the front")
    ap.add_argument("--dump-projection", help="write the edge-padded concept here (debug)")
    args = ap.parse_args(argv_after_ddash())

    with open(args.profile, "r", encoding="utf-8") as f:
        prof = json.load(f)
    mesh_prof = prof["mesh"]
    target = mesh_prof["tri_budget"].get(args.kind, mesh_prof["tri_budget"]["unit"])["target"]
    tex_size = args.tex or int(prof["texture"]["min_dim"])
    tex_max = int(prof["texture"]["max_dim"])
    if tex_size > tex_max:
        raise SystemExit("--tex %d exceeds profile max_dim %d" % (tex_size, tex_max))

    global DUMP_PROJECTION
    DUMP_PROJECTION = args.dump_projection

    reset_scene()
    scene = bpy.context.scene
    device = setup_cycles(scene)

    import_any(args.src)
    hp = join_meshes()
    hp.name = "HP_source"
    hp_verts = weld(hp)
    projection = None
    if args.hp_test_material:
        make_test_material(hp)
    elif args.project_front:
        projection = projection_material(hp, args.project_front, args.project_back)
    hp_tris = tri_count(hp)
    md = max_dim(hp)

    # Low-poly working copy.
    select_only([hp], hp)
    bpy.ops.object.duplicate()
    lp = bpy.context.view_layer.objects.active
    lp.name = "LP_baked"

    lp, shell_stats = drop_debris_shells(lp)
    lp.name = "LP_baked"
    before, after, method = process_to_budget(lp, target)
    unwrap(lp)
    mat = target_material(lp)

    # Base colour. Neutral fill = mid grey (a hole, not a void).
    color_img, c_filled, c_unfilled = bake_pass(
        hp, lp, mat, "DIFFUSE", "chimera_basecolor", tex_size, md,
        is_data=False, neutral=(0.5, 0.5, 0.5, 1.0), pass_filter={"COLOR"})

    normal_img = n_filled = n_unfilled = None
    if not args.no_normal:
        # Neutral fill = flat tangent normal (0.5, 0.5, 1.0).
        normal_img, n_filled, n_unfilled = bake_pass(
            hp, lp, mat, "NORMAL", "chimera_normal", tex_size, md,
            is_data=True, neutral=(0.5, 0.5, 1.0, 1.0), normal_space="TANGENT")

    ao_applied = False
    if args.ao:
        ao_img = new_image("chimera_ao", tex_size, True, "PNG", fill=(1.0, 1.0, 1.0, 1.0))
        node = bake_image_node(mat, ao_img)
        do_bake(hp, lp, "AO", 0.020 * md, 0.100 * md, tex_size, use_clear=True)
        mat.node_tree.nodes.remove(node)
        apply_ao(color_img, ao_img)
        bpy.data.images.remove(ao_img)
        ao_applied = True

    wire_material(mat, color_img, normal_img)
    bpy.data.objects.remove(hp, do_unlink=True)      # HP must not reach the GLB
    origin_to_base(lp)
    export_plain_glb(args.dst, lp)

    corners = [lp.matrix_world @ v.co for v in lp.data.vertices]
    xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
    log({
        "src": args.src, "dst": args.dst, "kind": args.kind,
        "device": device,
        "tex_size": tex_size,
        "tris_hp": hp_tris, "verts_hp_welded": hp_verts,
        "tris_before": before, "tris_after": after,
        "tri_target": target, "method": method, "shells": shell_stats,
        "materials": len(lp.data.materials),
        "basecolor": {"filled_from_fallback": c_filled, "unfilled": c_unfilled},
        "normal": None if normal_img is None else {"filled_from_fallback": n_filled,
                                                   "unfilled": n_unfilled},
        "ao_applied": ao_applied,
        "projection": projection,
        "min_z": min(zs),
        "center_xy": [(min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0],
        "out_bytes": os.path.getsize(args.dst) if os.path.exists(args.dst) else 0,
        "ok": True,
    })


if __name__ == "__main__":
    main()
