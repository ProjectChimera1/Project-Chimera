#!/usr/bin/env python
"""L1 scatter meshes from the CC0 Poly Haven / ambientCG sources (plan-c-scatter.md 3.6, task S1). Blender 4.5.10 headless.

Run with plain Python: the driver packs the RGBA leaf textures (PIL), starts
  D:/tools/blender/blender-4.5.10-windows-x64/blender.exe --background --factory-startup --python <this file> -- <jobs.json>
which builds each recipe of Scripts/scatter/meshes.json "prepared" and exports ScatterSrc/prepared/<Name>.glb, and then writes
ScatterSrc/prepared/report.json from the exported files themselves (Tools/scatter_glb.py: triangles, bounds, vertex-position sha256,
measured pivot). make_bush.py adds its composite bushes to the same report (it imports the Blender helpers below).

UVs: every source object first goes through bake_uvs: for each material, the UV layer its image textures sample (the glTF importer's
UV Map node, e.g. tree_small_02_branches samples TEXCOORD_1 = 'UVMap.001') and its KHR_texture_transform (the importer's Mapping node)
are baked into ONE layer 'UVMap', and the Mapping / UV Map nodes are removed, so every exported primitive samples TEXCOORD_0 with no
texture transform (S3 replaces the materials with ChimeraScatter.hlsl ones, which would ignore a transform). A material whose layer is
degenerate (fir_tree_01_trunk_c has no TEXCOORD_0 in the source) gets cylindrical UVs instead. Each row's "uv" says what was done.

Recipe ops (all deterministic; every random choice is Mix32 of the recipe seed and an index, never Python's random):
  objects        source object names to keep (the glTF node names; the rest is deleted)
  cards          {"materials": [...], "scale_cap": c, "coverage": k, "margin": m, "wood_filter": {...}}: each leaf island (faces
                 connected by vertices) is replaced by ONE quad fitted to it: the least-squares affine map uv -> position over the
                 island's loops, evaluated at the island's UV bounding-box corners, so the quad shows exactly the island's texture region
                 (the cut-out comes from the alpha map). wood_filter {"max_coverage": a, "min_green": g} first drops islands whose UV box
                 is nearly opaque (alpha coverage > a: solid bark strips of the atlas) or brown (mean G - R of the opaque texels < g),
                 which as grown cards read as planks. Islands are then thinned to the triangle budget (exactly n_keep = budget / 2
                 islands, the lowest Mix32 ranks; keep fraction k = n_keep / islands) and the kept quads grow by
                 min(scale_cap, sqrt(coverage / k)) about their centre to hold the crown's coverage. A tube (a twig or branch)
                 unwrapped onto a solid strip is exactly what the coverage test catches: as a flat card it would be a plank.
                 Card normals are bent 70 % toward the crown-centre radial (soft foliage shading).
  decimate       {material: target_triangles}: collapse decimation of the non-card faces of that material (Blender's COLLAPSE
                 decimate; planar decimation is not used; seams are not explicitly protected, so decimated card plants need S6's look check)
  drop           materials whose faces are left out (fir_tree_01's dead branches read as sticks once the twigs are thinned)
  prescale       {"crown": [sx, sy, sz]}: the crown above the crown base (the lowest card centre) is stretched: z' = zb + (z - zb) sz,
                 xy about the trunk axis by s(xy), eased in over the first 15 % of the crown; cards are MOVED with their centres, not
                 stretched, so leaf textures keep their aspect (TreeBroadB: crown z x1.3, xy x0.85, plan 3.6)
  height         nominal height (m) of the slot: uniform scale so the mesh's height equals it; null keeps the native size
  pivot          "base_centre" (bbox centre in xy, min z = 0) or "trunk" (xy of the given material's vertices in the lowest 2 % of the height);
                 the seed-card clump is "root_disc" (rooted on a disc round the origin; the report measures the root band's mean xy)
  alpha          materials whose base colour gets the source's alpha map (RGBA PNG) and alphaMode MASK, cutoff 0.5, double sided
Normal maps stay nor_gl (Interchange flips the green of every glTF normal texture once, F23).
Usage: python prep_polyhaven.py [--only NAME,...]   -> SCATTER_PREP OK meshes=<n> ...
Env: CHIMERA_SCATTER_SRC (raw sources), CHIMERA_SCATTER_PREP / CHIMERA_SCATTER_WORK (output and work folders; prep_rerun_check.py
points them at a temp folder to rebuild and compare).
"""
import hashlib
import json
import math
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
T = os.path.dirname(TOOLS)
CONFIG = os.path.join(T, "Scripts", "scatter", "meshes.json")
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
PREP = os.environ.get("CHIMERA_SCATTER_PREP", os.path.join(SRC, "prepared"))
WORK = os.environ.get("CHIMERA_SCATTER_WORK", os.path.join(SRC, "work"))
BLENDER = os.environ.get("CHIMERA_BLENDER", "D:/tools/blender/blender-4.5.10-windows-x64/blender.exe")
M32 = 0xFFFFFFFF
NEAR_CARD_TRIS = 310


def mix32(x):
    x &= M32
    x ^= x >> 16
    x = (x * 0x7FEB352D) & M32
    x ^= x >> 15
    x = (x * 0x846CA68B) & M32
    x ^= x >> 16
    return x


def inside_blender():
    try:
        import bpy  # noqa: F401
        return True
    except ImportError:
        return False


def refuse_mirror():
    """The R mirror (Project_Chimera/tools/unreal-terrain) is for reading: outputs default next to the script, so never run there."""
    if os.path.basename(T) == "unreal-terrain" and "CHIMERA_SCATTER_SRC" not in os.environ:
        print(f"REFUSED: {T} is the repo mirror; run the ChimeraTerrain copy (or set CHIMERA_SCATTER_SRC)")
        sys.exit(2)


def sha256_file(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def recipe_sha(entry):
    """sha256 of a meshes.json recipe entry as written (canonical JSON), so a stale prepared row can be detected."""
    return hashlib.sha256(json.dumps(entry, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


# =============================================================== Blender helpers (also used by make_bush.py) ======================
def bl_reset():
    import bpy
    bpy.ops.wm.read_factory_settings(use_empty=True)


def bl_import(path):
    import bpy
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def bl_keep(objs, names):
    """Delete every imported object not named in names; apply the kept objects' transforms into their mesh data."""
    import bpy
    keep = []
    for o in objs:
        if o.type == "MESH" and o.name in names:
            keep.append(o)
    for o in objs:
        if o not in keep:
            bpy.data.objects.remove(o, do_unlink=True)
    for o in keep:
        o.parent = None
        mw = o.matrix_world.copy()
        o.data.transform(mw)
        o.matrix_world.identity()
    missing = set(names) - {o.name for o in keep}
    if missing:
        raise RuntimeError(f"objects not found: {sorted(missing)}")
    return sorted(keep, key=lambda o: names.index(o.name))


def loops_of(ls, lt):
    """Loop indices of polygons with loop starts ls and totals lt, concatenated polygon by polygon."""
    import numpy as np
    lt = np.asarray(lt, dtype=np.int64)
    if len(lt) == 0:
        return np.zeros(0, np.int64)
    return np.repeat(ls, lt) + (np.arange(int(lt.sum())) - np.repeat(np.cumsum(lt) - lt, lt))


def mesh_arrays(me):
    import numpy as np
    nv, npl, nl = len(me.vertices), len(me.polygons), len(me.loops)
    co = np.empty(nv * 3)
    me.vertices.foreach_get("co", co)
    ls, lt, mi = np.empty(npl, np.int64), np.empty(npl, np.int64), np.empty(npl, np.int64)
    me.polygons.foreach_get("loop_start", ls)
    me.polygons.foreach_get("loop_total", lt)
    me.polygons.foreach_get("material_index", mi)
    lv = np.empty(nl, np.int64)
    me.loops.foreach_get("vertex_index", lv)
    if len(me.uv_layers) > 1:
        raise RuntimeError(f"{me.name}: {len(me.uv_layers)} UV layers; bake_uvs must run first")
    uv = np.zeros(nl * 2)
    if me.uv_layers.active is not None:
        me.uv_layers.active.data.foreach_get("uv", uv)
    ed = np.empty(len(me.edges) * 2, np.int64)
    me.edges.foreach_get("vertices", ed)
    return {"co": co.reshape(-1, 3), "ls": ls, "lt": lt, "mi": mi, "lv": lv, "uv": uv.reshape(-1, 2), "edges": ed.reshape(-1, 2)}


def material_uv_source(mat):
    """(uv layer name or '' for the default layer, (location, rotation, scale) of a POINT Mapping node or None) shared by every image
    texture of mat. The glTF importer writes texCoord N as a UV Map node and KHR_texture_transform as a Mapping node."""
    found = set()
    if mat is None or not mat.use_nodes or mat.node_tree is None:
        return "", None
    for n in mat.node_tree.nodes:
        if n.type != "TEX_IMAGE":
            continue
        layer, mp = "", None
        sock = n.inputs["Vector"]
        while sock.is_linked:
            src = sock.links[0].from_node
            if src.type == "MAPPING":
                if src.vector_type != "POINT" or any(src.inputs[k].is_linked for k in ("Location", "Rotation", "Scale")):
                    raise RuntimeError(f"{mat.name}: unsupported Mapping node ({src.vector_type})")
                mp = tuple(tuple(round(float(v), 6) for v in src.inputs[k].default_value) for k in ("Location", "Rotation", "Scale"))
                sock = src.inputs["Vector"]
            elif src.type == "UVMAP":
                layer = src.uv_map
                break
            else:
                raise RuntimeError(f"{mat.name}: unsupported texture-coordinate node {src.type}")
        found.add((layer, mp))
    if len(found) > 1:
        raise RuntimeError(f"{mat.name}: image textures use different UV sources {sorted(map(str, found))}")
    return found.pop() if found else ("", None)


def strip_uv_nodes(mat):
    """Unlink every image texture's Vector input and delete the Mapping / UV Map nodes (the UVs are baked)."""
    if mat is None or not mat.use_nodes or mat.node_tree is None:
        return
    nt = mat.node_tree
    for n in nt.nodes:
        if n.type == "TEX_IMAGE":
            for link in list(n.inputs["Vector"].links):
                nt.links.remove(link)
    for n in list(nt.nodes):
        if n.type in ("MAPPING", "UVMAP"):
            nt.nodes.remove(n)


def cylinder_uv(P, poly_sizes, tile):
    """Cylindrical UVs for loop positions P (polygon by polygon, sizes poly_sizes) about the vertical axis through the lowest 5 % of P:
    u = turns x round(circumference / tile), v = height / tile, with the seam fixed per polygon (no polygon spans the wrap)."""
    import numpy as np
    z0, z1 = P[:, 2].min(), P[:, 2].max()
    low = P[P[:, 2] <= z0 + 0.05 * (z1 - z0)]
    cx, cy = low[:, 0].mean(), low[:, 1].mean()
    r = float(np.median(np.hypot(low[:, 0] - cx, low[:, 1] - cy)))
    nu = max(1, int(round(2 * math.pi * r / tile)))
    u = (np.arctan2(P[:, 1] - cy, P[:, 0] - cx) / (2 * math.pi) + 0.5) * nu
    v = (P[:, 2] - z0) / tile
    starts = np.concatenate([[0], np.cumsum(poly_sizes)[:-1]]).astype(np.int64)
    umax = np.maximum.reduceat(u, starts)
    umin = np.minimum.reduceat(u, starts)
    wrap = np.repeat((umax - umin) > nu / 2, poly_sizes)
    u = np.where(wrap & (u < nu / 2), u + nu, u)
    return np.c_[u, v], {"axis_xy": [round(float(cx), 4), round(float(cy), 4)], "radius": round(r, 4), "u_repeats": nu, "tile_m": tile}


def bake_uvs(o, cache, facts, tile=0.8):
    """Bake each material's UV layer and texture transform into one layer 'UVMap' (see the module doc). cache: material name ->
    (layer, mapping), filled on the first object that uses the material (its nodes are stripped then). facts: material -> what was done."""
    import numpy as np
    from mathutils import Euler
    me = o.data
    nl = len(me.loops)
    layers = {}
    for lay in me.uv_layers:
        a = np.zeros(nl * 2)
        lay.data.foreach_get("uv", a)
        layers[lay.name] = a.reshape(-1, 2)
    dflt = next((lay.name for lay in me.uv_layers if lay.active_render), next(iter(layers), None))
    out = layers[dflt].copy() if dflt else np.zeros((nl, 2))
    npl = len(me.polygons)
    ls, lt, mi = np.empty(npl, np.int64), np.empty(npl, np.int64), np.empty(npl, np.int64)
    me.polygons.foreach_get("loop_start", ls)
    me.polygons.foreach_get("loop_total", lt)
    me.polygons.foreach_get("material_index", mi)
    lv = np.empty(nl, np.int64)
    me.loops.foreach_get("vertex_index", lv)
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    for k, mat in enumerate(me.materials):
        if mat is None:
            continue
        if mat.name not in cache:
            cache[mat.name] = material_uv_source(mat)
            strip_uv_nodes(mat)
        layer, mp = cache[mat.name]
        polys = np.nonzero(mi == k)[0]
        if len(polys) == 0:
            continue
        L = loops_of(ls[polys], lt[polys])
        lname = layer or dflt
        if lname not in layers:
            raise RuntimeError(f"{o.name}/{mat.name}: UV layer {lname!r} not on the mesh ({sorted(layers)})")
        uv = layers[lname][L].copy()
        span = uv.max(axis=0) - uv.min(axis=0)
        fact = {"uv_layer": lname, "texture_transform": None, "generated": None}
        if float(span.max()) < 1e-6:
            uv, cyl = cylinder_uv(co[lv[L]], lt[polys], tile)
            fact["generated"] = dict(kind="cylindrical", source_uv_span=[float(x) for x in span], **cyl)
        elif mp is not None:
            loc, rot, sc = mp
            R = np.array(Euler(rot).to_matrix())
            v3 = np.c_[uv, np.zeros(len(uv))] * np.array(sc)
            uv = (v3 @ R.T + np.array(loc))[:, :2]
            fact["texture_transform"] = {"location": list(loc), "rotation": list(rot), "scale": list(sc), "baked": True}
        out[L] = uv
        if mat.name not in facts or fact["generated"]:
            facts[mat.name] = fact
    while len(me.uv_layers):
        me.uv_layers.remove(me.uv_layers[0])
    lay = me.uv_layers.new(name="UVMap")
    lay.data.foreach_set("uv", out.reshape(-1))
    me.update()


def vertex_islands(nv, edges):
    """Connected-component label (= min vertex index of the component) per vertex: min-label propagation with pointer jumping."""
    import numpy as np
    lab = np.arange(nv, dtype=np.int64)
    if len(edges) == 0:
        return lab
    while True:
        m = np.minimum(lab[edges[:, 0]], lab[edges[:, 1]])
        new = lab.copy()
        np.minimum.at(new, edges[:, 0], m)
        np.minimum.at(new, edges[:, 1], m)
        for _ in range(4):
            new = new[new]
        if np.array_equal(new, lab):
            return lab
        lab = new


def build_mesh(name, verts, faces_flat, face_sizes, loop_uv, mat_idx, materials, loop_normals=None, smooth=True):
    """A new mesh object from numpy arrays (faces as a flat vertex-index list plus sizes)."""
    import bpy
    import numpy as np
    me = bpy.data.meshes.new(name)
    me.vertices.add(len(verts))
    me.vertices.foreach_set("co", np.asarray(verts, dtype=np.float64).reshape(-1))
    nl = int(np.sum(face_sizes))
    me.loops.add(nl)
    me.loops.foreach_set("vertex_index", np.asarray(faces_flat, dtype=np.int64))
    me.polygons.add(len(face_sizes))
    starts = np.concatenate([[0], np.cumsum(face_sizes)[:-1]]).astype(np.int64)
    me.polygons.foreach_set("loop_start", starts)
    me.polygons.foreach_set("material_index", np.asarray(mat_idx, dtype=np.int64))
    me.polygons.foreach_set("use_smooth", np.full(len(face_sizes), bool(smooth)))
    uvl = me.uv_layers.new(name="UVMap")
    uvl.data.foreach_set("uv", np.asarray(loop_uv, dtype=np.float64).reshape(-1))
    for m in materials:
        me.materials.append(m)
    me.update(calc_edges=True)
    me.validate(clean_customdata=False)
    if loop_normals is not None:
        me.normals_split_custom_set([tuple(n) for n in np.asarray(loop_normals)])
    o = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(o)
    return o


def subset_mesh(name, A, face_mask, materials):
    """Copy of the faces in face_mask (with their UVs and material indices) as a new object."""
    import numpy as np
    sel = np.nonzero(face_mask)[0]
    if len(sel) == 0:
        return None
    sizes = A["lt"][sel]
    loops = loops_of(A["ls"][sel], sizes)
    vids = A["lv"][loops]
    used, inv = np.unique(vids, return_inverse=True)
    return build_mesh(name, A["co"][used], inv, sizes, A["uv"][loops], A["mi"][sel], materials)


def decimate_to(obj, target):
    """Collapse decimation to about target triangles; returns the triangle count after."""
    import bpy
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if tris <= target:
        return tris
    mod = obj.modifiers.new("dec", "DECIMATE")
    mod.decimate_type = "COLLAPSE"
    mod.ratio = max(1e-4, target / tris)
    mod.use_collapse_triangulate = True
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev)
    old = obj.data
    obj.modifiers.remove(mod)
    obj.data = me
    bpy.data.meshes.remove(old)
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def island_texels(grid, U):
    """(alpha coverage, mean G - R of the opaque texels) of the UV box of U (Blender UVs, v up) in a texel-stats grid
    {"alpha": HxW in 0..1, "gr": HxW G - R in -1..1}; None when the box holds no texel."""
    import numpy as np
    a, gr = grid["alpha"], grid["gr"]
    H, W = a.shape
    u0, v0 = np.clip(U.min(axis=0), 0, 1)
    u1, v1 = np.clip(U.max(axis=0), 0, 1)
    x0, x1 = int(math.floor(u0 * W)), max(int(math.floor(u0 * W)) + 1, int(math.ceil(u1 * W)))
    y0, y1 = int(math.floor((1 - v1) * H)), max(int(math.floor((1 - v1) * H)) + 1, int(math.ceil((1 - v0) * H)))
    box_a = a[y0:min(y1, H), x0:min(x1, W)]
    if box_a.size == 0:
        return None
    opaque = box_a > 0.5
    cov = float(opaque.mean())
    green = float(gr[y0:min(y1, H), x0:min(x1, W)][opaque].mean()) if opaque.any() else 0.0
    return cov, green


def card_islands(A, card_mats, budget_tris, seed, scale_cap, coverage=1.0, wood_filter=None, grids=None):
    """Fit one quad per island of the faces whose material index is in card_mats; drop wood islands (wood_filter, grids by material
    index); thin to budget_tris. Returns quads, uvs, material indices and stats."""
    import numpy as np
    lab = vertex_islands(len(A["co"]), A["edges"])
    fmask = np.isin(A["mi"], list(card_mats))
    faces = np.nonzero(fmask)[0]
    if len(faces) == 0:
        return None
    sizes = A["lt"][faces]
    loops = loops_of(A["ls"][faces], sizes)
    loop_island = lab[A["lv"][loops]]
    loop_mat = np.repeat(A["mi"][faces], sizes)
    order = np.argsort(loop_island, kind="stable")
    li = loop_island[order]
    uniq, start = np.unique(li, return_index=True)
    ends = np.append(start[1:], len(li))
    cand, wood = [], 0
    for j in range(len(uniq)):
        sl = order[start[j]:ends[j]]
        if wood_filter and grids:
            g = grids.get(int(loop_mat[sl[0]]))
            t = island_texels(g, A["uv"][loops[sl]]) if g else None
            if t is not None and (t[0] > wood_filter["max_coverage"] or t[1] < wood_filter["min_green"]):
                wood += 1
                continue
        cand.append(sl)
    n_isl = len(cand)
    n_keep = min(n_isl, max(1, budget_tris // 2))
    k = n_keep / n_isl
    s = min(scale_cap, math.sqrt(coverage / k)) if (k < 1 or coverage != 1.0) else 1.0
    # exactly n_keep islands: the n_keep lowest Mix32 ranks (ties by index), so the kept count, and with it the triangle budget,
    # never depends on the luck of per-island draws
    keep = set(sorted(range(n_isl), key=lambda j: (mix32(seed ^ mix32(j)), j))[:n_keep])
    quads, uvs, mats, skipped, resid = [], [], [], 0, []
    for j, sl in enumerate(cand):
        if j not in keep:
            continue
        P = A["co"][A["lv"][loops[sl]]]
        U = A["uv"][loops[sl]]
        M = np.c_[U, np.ones(len(U))]
        sol, res, rank, _ = np.linalg.lstsq(M, P, rcond=None)
        if rank < 3:
            skipped += 1
            continue
        u0, v0 = U.min(axis=0)
        u1, v1 = U.max(axis=0)
        cuv = np.array([[u0, v0], [u1, v0], [u1, v1], [u0, v1]])
        Q = np.c_[cuv, np.ones(4)] @ sol
        c = Q.mean(axis=0)
        Q = c + (Q - c) * s
        fit = np.c_[U, np.ones(len(U))] @ sol - P
        resid.append(float(np.sqrt((fit ** 2).sum(axis=1).mean())))
        quads.append(Q)
        uvs.append(cuv)
        mats.append(int(loop_mat[sl[0]]))
    stats = {"islands": int(len(uniq)), "wood_dropped": wood, "kept": len(quads), "keep_fraction": round(k, 5), "card_scale": round(s, 4),
             "skipped_rank": skipped, "fit_rms_m_median": round(float(np.median(resid)), 5) if resid else None}
    return np.array(quads), np.array(uvs), np.array(mats), stats


def make_card_object(name, quads, uvs, mats, materials, bend=0.7):
    import numpy as np
    n = len(quads)
    verts = quads.reshape(-1, 3)
    faces = np.arange(n * 4)
    sizes = np.full(n, 4)
    luv = uvs.reshape(-1, 2)
    centre = verts.mean(axis=0)
    centre[2] = verts[:, 2].min() + (verts[:, 2].max() - verts[:, 2].min()) * 0.55
    fn = np.cross(quads[:, 1] - quads[:, 0], quads[:, 3] - quads[:, 0])
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
    radial = verts - centre
    radial /= np.maximum(np.linalg.norm(radial, axis=1, keepdims=True), 1e-12)
    fnl = np.repeat(fn, 4, axis=0)
    flip = np.sign(np.sum(fnl * radial, axis=1))
    flip[flip == 0] = 1
    nrm = (1 - bend) * fnl * flip[:, None] + bend * radial
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-12)
    return build_mesh(name, verts, faces, sizes, luv, mats, materials, loop_normals=nrm)


def join(objs, name):
    """Join objs into one object named name; its mesh datablock is named name too (no orphan of that name may exist)."""
    import bpy
    objs = [o for o in objs if o is not None]
    for o in bpy.context.scene.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
            bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active
    o.name = name
    o.data.name = name
    if o.data.name != name:
        raise RuntimeError(f"mesh name {o.data.name!r} != {name!r} (an orphan mesh holds the name)")
    return o


def remove_object(o):
    """Delete an object and its mesh datablock."""
    import bpy
    me = o.data
    bpy.data.objects.remove(o, do_unlink=True)
    if me is not None and me.users == 0:
        bpy.data.meshes.remove(me)


def crown_map(axis, zb, ztop, s):
    """Vectorised map of positions for prescale "crown": identity below zb; above, z' = zb + (z - zb) sz and xy about axis scaled by
    1 + (s - 1) w, w easing 0 -> 1 over the first 15 % of the crown."""
    import numpy as np

    def f(P):
        P = np.array(P, dtype=np.float64)
        z = P[:, 2]
        w = np.clip((z - zb) / max(1e-9, 0.15 * (ztop - zb)), 0, 1)
        w = w * w * (3 - 2 * w)
        for i in (0, 1):
            P[:, i] = axis[i] + (P[:, i] - axis[i]) * (1 + (s[i] - 1) * w)
        P[:, 2] = np.where(z > zb, zb + (z - zb) * s[2], z)
        return P
    return f


def trunk_axis(co, mi, ls, lt, lv, mat_index):
    """xy of the vertices of material mat_index in the lowest 2 % of the whole mesh's height (the trunk base)."""
    import numpy as np
    fi = np.nonzero(mi == mat_index)[0]
    vids = np.unique(lv[loops_of(ls[fi], lt[fi])])
    zmin, zmax = co[:, 2].min(), co[:, 2].max()
    low = co[vids][co[vids][:, 2] < zmin + 0.02 * (zmax - zmin)]
    return (float(low[:, 0].mean()), float(low[:, 1].mean())) if len(low) else None


def fit_height_and_pivot(o, height, pivot, pivot_material=None):
    """Pivot (base centre or trunk) to the origin with min z = 0, then a uniform scale to height. Returns (scale, pre-fit height m,
    pivot offset removed [x, y, z] in pre-fit metres)."""
    from mathutils import Matrix
    me = o.data
    A = mesh_arrays(me)
    co = A["co"]
    zmin, zmax = co[:, 2].min(), co[:, 2].max()
    cx, cy = (co[:, 0].min() + co[:, 0].max()) / 2, (co[:, 1].min() + co[:, 1].max()) / 2
    if pivot == "trunk":
        names = [m.name for m in me.materials]
        if pivot_material not in names:
            raise RuntimeError(f"{o.name}: pivot material {pivot_material} not on the mesh")
        ax = trunk_axis(co, A["mi"], A["ls"], A["lt"], A["lv"], names.index(pivot_material))
        if ax is None:
            raise RuntimeError(f"{o.name}: no {pivot_material} vertices at the base")
        cx, cy = ax
    me.transform(Matrix.Translation((-cx, -cy, -zmin)))
    s = 1.0 if height is None else height / (zmax - zmin)
    me.transform(Matrix.Diagonal((s, s, s, 1.0)))
    me.update()
    return s, float(zmax - zmin), [round(float(cx), 5), round(float(cy), 5), round(float(zmin), 5)]


def remove_degenerate(o, min_area=1e-7):
    """Delete the faces of area < min_area m^2 of a collapse-decimated object (decimation can leave slivers; Nanite and remove-degenerates
    builds would drop them anyway). Only used on decimated parts, which carry no custom normals. Returns how many were removed."""
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bad = [f for f in bm.faces if f.calc_area() < min_area]
    if bad:
        bmesh.ops.delete(bm, geom=bad, context="FACES_ONLY")
        loose = [v for v in bm.verts if not v.link_faces]
        if loose:
            bmesh.ops.delete(bm, geom=loose, context="VERTS")
    n = len(bad)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()
    return n


def set_alpha_image(mat, rgba_path):
    """Base colour from the packed RGBA image, its alpha into the BSDF alpha (the exporter then writes the PNG with alpha)."""
    import bpy
    nt = mat.node_tree
    bsdf = [n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"][0]
    img = bpy.data.images.load(rgba_path, check_existing=True)
    img.alpha_mode = "STRAIGHT"
    tex = None
    for link in bsdf.inputs["Base Color"].links:
        node = link.from_node
        while node is not None and node.type != "TEX_IMAGE":
            ins = [i for i in node.inputs if i.is_linked]
            node = ins[0].links[0].from_node if ins else None
        tex = node
    if tex is None:
        tex = nt.nodes.new("ShaderNodeTexImage")
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    tex.image = img
    for link in list(bsdf.inputs["Alpha"].links):
        nt.links.remove(link)
    nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])


def export_glb(o, path):
    import bpy
    for x in bpy.context.scene.objects:
        x.select_set(False)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    kw = dict(filepath=path, export_format="GLB", use_selection=True, export_yup=True, export_apply=False, export_image_format="AUTO",
              export_materials="EXPORT", export_animations=False, export_skins=False, export_morph=False, export_cameras=False,
              export_lights=False, export_extras=False, export_texcoords=True, export_normals=True)
    try:
        bpy.ops.export_scene.gltf(export_vertex_color="NONE", **kw)
    except TypeError:
        bpy.ops.export_scene.gltf(**kw)


def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def objects_height(objs):
    """Height (m) of the kept source objects (their transforms are applied by bl_keep)."""
    zs = [v.co.z for o in objs for v in o.data.vertices]
    return float(max(zs) - min(zs))


def objects_size(objs):
    import numpy as np
    co = np.array([tuple(v.co) for o in objs for v in o.data.vertices])
    return [round(float(x), 4) for x in co.max(axis=0) - co.min(axis=0)]


def run_recipe(r, src_root, out_dir):
    """Build one recipe; returns a dict of build facts (the driver adds file facts)."""
    import bpy
    import numpy as np
    bl_reset()
    gl = os.path.join(src_root, "raw", "polyhaven", r["source"], f"{r['source']}_1k.gltf")
    objs = bl_keep(bl_import(gl), r["objects"])
    if r.get("stack_objects"):
        # several source clumps into one: each object's base centre to the origin (the source lays variants out side by side)
        from mathutils import Matrix
        for so in objs:
            c = np.array([tuple(v.co) for v in so.data.vertices])
            so.data.transform(Matrix.Translation((-(c[:, 0].min() + c[:, 0].max()) / 2, -(c[:, 1].min() + c[:, 1].max()) / 2,
                                                  -c[:, 2].min())))
    src_tris = sum(tri_count(o) for o in objs)
    facts = {"source_triangles": src_tris, "source_height_m": round(objects_height(objs), 4), "source_size_m": objects_size(objs)}
    uv_cache, uv_facts = {}, {}
    for o in objs:
        bake_uvs(o, uv_cache, uv_facts)
    facts["uv"] = uv_facts
    for mname, rgba in r.get("_alpha_images", {}).items():
        if mname in bpy.data.materials:
            set_alpha_image(bpy.data.materials[mname], rgba)
    o = join(objs, r["name"] + "_src")
    me = o.data
    names = [m.name for m in me.materials]
    A = mesh_arrays(me)
    parts = []
    cards = r.get("cards")
    card_idx = set()
    if cards:
        card_idx = {names.index(m) for m in cards["materials"] if m in names}
    other = ~np.isin(A["mi"], list(card_idx))
    dec = r.get("decimate", {})
    # non-card faces: one object per material, decimated to its target
    for mi_, mname in enumerate(names):
        if mi_ in card_idx:
            continue
        mask = other & (A["mi"] == mi_)
        if not mask.any() or mname in r.get("drop", []):
            continue
        sub = subset_mesh(f"{r['name']}_{mi_}", A, mask, list(me.materials))
        if mname in dec:
            decimate_to(sub, dec[mname])
            facts.setdefault("degenerate_removed", {})[mname] = remove_degenerate(sub, 1e-9)
        parts.append(sub)
    used = sum(tri_count(p) for p in parts)
    quads = None
    if cards:
        budget = r["budget"] - used - cards.get("margin", 200)
        grids = None
        if cards.get("wood_filter"):
            grids = {}
            for mname, gp in r.get("_texel_grids", {}).items():
                if mname in names:
                    z = np.load(gp)
                    grids[names.index(mname)] = {"alpha": z["alpha"], "gr": z["gr"]}
        res = card_islands(A, card_idx, budget, mix32(r["seed"]), cards.get("scale_cap", 2.5), cards.get("coverage", 1.0),
                           cards.get("wood_filter"), grids)
        if res is not None:
            quads, uvs, mats, st = res
            facts["cards"] = st
    pre = r.get("prescale")
    if pre:
        # crown stretch about the trunk axis above the crown base (the lowest card centre); cards move, wood parts deform
        if quads is None or "crown" not in pre:
            raise RuntimeError(f"{r['name']}: prescale needs cards and a 'crown' entry")
        s3 = pre["crown"]
        ax = trunk_axis(A["co"], A["mi"], A["ls"], A["lt"], A["lv"], names.index(r["pivot_material"]))
        cen = quads.mean(axis=1)
        zb, ztop = float(cen[:, 2].min()), float(A["co"][:, 2].max())
        f = crown_map(ax, zb, ztop, s3)
        quads = quads + (f(cen) - cen)[:, None, :]
        for p in parts:
            co = np.empty(len(p.data.vertices) * 3)
            p.data.vertices.foreach_get("co", co)
            p.data.vertices.foreach_set("co", f(co.reshape(-1, 3)).reshape(-1))
            p.data.update()
        facts["prescale"] = {"mode": "crown", "scale": s3, "crown_base_m": round(zb, 4), "axis_xy": [round(x, 4) for x in ax]}
    if quads is not None:
        parts.append(make_card_object(r["name"] + "_cards", quads, uvs, mats, list(me.materials), bend=cards.get("bend", 0.7)))
    remove_object(o)
    final = join(parts, r["name"])
    s, fit_h, off = fit_height_and_pivot(final, r.get("height"), r.get("pivot", "base_centre"), r.get("pivot_material"))
    facts.update({"scale_factor": round(s, 6), "pre_fit_height_m": round(fit_h, 4), "pivot_offset_removed_m": off,
                  "target_height_m": r.get("height"), "blender_triangles": tri_count(final)})
    export_glb(final, os.path.join(out_dir, r["name"] + ".glb"))
    return facts


def blender_main(argv):
    jobs = json.load(open(argv[0], encoding="utf-8"))
    facts = {}
    for r in jobs["recipes"]:
        facts[r["name"]] = run_recipe(r, jobs["src"], jobs["out"])
        print("PREP_BUILT", r["name"], json.dumps(facts[r["name"]]), flush=True)
    json.dump(facts, open(jobs["facts"], "w"), indent=1)


# =============================================================== driver ==========================================================
def alpha_u8(path, size=None):
    """An alpha / opacity map as an 8-bit Pillow 'L' image, scaled, never clipped. Pillow's convert("L") CLIPS 16-bit (I;16, I;16B,
    I) values at 255 instead of scaling them, which turns a 0..65535 ramp into a near-binary mask (11 of Poly Haven's leaf alpha maps
    are I;16). Integer modes are scaled by 255/65535 (16-bit) and rounded; F is taken as 0..1; 8-bit modes pass through."""
    import numpy as np
    from PIL import Image
    im = Image.open(path)
    if im.mode in ("I;16", "I;16B", "I;16L", "I"):
        v = np.asarray(im).astype(np.float64)
        u = np.clip(np.rint(v * 255.0 / 65535.0), 0, 255).astype(np.uint8)
        out = Image.fromarray(u, "L")
    elif im.mode == "F":
        v = np.asarray(im).astype(np.float64)
        out = Image.fromarray(np.rint(np.clip(v, 0.0, 1.0) * 255.0).astype(np.uint8), "L")
    elif im.mode in ("L", "P", "RGB", "LA", "RGBA"):
        out = im.convert("L") if im.mode != "LA" else im.getchannel("L")
    else:
        raise RuntimeError(f"alpha_u8: unsupported image mode {im.mode} in {path}")
    return out.resize(size) if size is not None and out.size != tuple(size) else out


def pack_alpha(src_id, recipe):
    """RGBA PNG per alpha material: the material's base colour JPEG + the matching *alpha map. Returns {material: png path}."""
    from PIL import Image
    gpath = os.path.join(SRC, "raw", "polyhaven", src_id, f"{src_id}_1k.gltf")
    g = json.load(open(gpath, encoding="utf-8"))
    out = {}
    tex_dir = os.path.join(SRC, "raw", "polyhaven", src_id, "textures")
    alphas = sorted(n for n in os.listdir(tex_dir) if "_alpha_" in n)
    for mname in recipe.get("alpha", []):
        mat = [m for m in g["materials"] if m["name"] == mname][0]
        uri = g["images"][g["textures"][mat["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"]]["uri"]
        diff = os.path.basename(uri)                         # e.g. tree_small_02_leaves_diff_1k.jpg
        prefix = diff.split("_diff_")[0]                      # tree_small_02_leaves
        cand = [a for a in alphas if a.startswith(prefix + "_alpha_")] or [a for a in alphas if a == f"{src_id}_alpha_1k.png"]
        if not cand:
            raise RuntimeError(f"{src_id}/{mname}: no alpha map for {diff} among {alphas}")
        c = Image.open(os.path.join(SRC, "raw", "polyhaven", src_id, *uri.split("/"))).convert("RGB")
        a = alpha_u8(os.path.join(tex_dir, cand[0]), c.size)
        os.makedirs(WORK, exist_ok=True)
        p = os.path.join(WORK, f"{prefix}_diffa_1k.png")
        Image.merge("RGBA", (*c.split(), a)).save(p, optimize=False)
        out[mname] = p.replace("\\", "/")
    return out


def texel_grids(alpha_images, materials, n=256):
    """Per card material, an n x n grid of alpha (0..1) and G - R (-1..1) of its packed RGBA base colour, saved as .npz for Blender."""
    import numpy as np
    from PIL import Image
    out = {}
    for mname in materials:
        if mname not in alpha_images:
            raise RuntimeError(f"wood_filter: card material {mname} has no packed alpha image")
        im = np.asarray(Image.open(alpha_images[mname]).convert("RGBA").resize((n, n), Image.BOX), dtype=np.float64) / 255.0
        p = os.path.join(WORK, f"{mname}_texels_{n}.npz")
        np.savez(p, alpha=im[:, :, 3], gr=im[:, :, 1] - im[:, :, 0])
        out[mname] = p.replace("\\", "/")
    return out


def postprocess(path, recipe):
    """alphaMode MASK (cutoff 0.5) + doubleSided on the alpha materials; returns scatter_glb stats."""
    sys.path.insert(0, TOOLS)
    import scatter_glb as sg
    js, binb = sg.read_glb(path)
    for m in js.get("materials", []):
        if m.get("name") in recipe.get("alpha", []):
            m["alphaMode"] = "MASK"
            m["alphaCutoff"] = 0.5
            m["doubleSided"] = True
        elif "alphaMode" in m and m.get("name") not in recipe.get("alpha", []):
            m.pop("alphaMode", None)
            m.pop("alphaCutoff", None)
    js.get("asset", {}).pop("extras", None)
    data = sg.write_glb(path, js, binb)
    st = sg.stats(path)
    st["identity_nodes"] = sg.node_transforms_identity(js)
    st["mesh_names"] = [m.get("name") for m in js.get("meshes", [])]
    return hashlib.sha256(data).hexdigest(), len(data), st


def measured_pivot(path, mode, material):
    """Pivot as exported: for 'trunk' the mean xy of the pivot material's vertices in the lowest 2 % of the height, for 'root_disc'
    (a clump rooted on a disc round the origin, e.g. the seed-card clump) the mean xy of all vertices in that band, else the bbox
    centre xy; min z of the whole mesh."""
    import numpy as np
    sys.path.insert(0, TOOLS)
    import scatter_glb as sg
    prims = sg.primitives(path)
    allp = np.concatenate([p["pos"] for p in prims])
    zmin, zmax = float(allp[:, 2].min()), float(allp[:, 2].max())
    if mode == "trunk":
        P = np.concatenate([p["pos"] for p in prims if p["material"] == material])
        low = P[P[:, 2] < zmin + 0.02 * (zmax - zmin)]
        xy = low[:, :2].mean(axis=0)
    elif mode == "root_disc":
        xy = allp[allp[:, 2] < zmin + 0.02 * (zmax - zmin)][:, :2].mean(axis=0)
    else:
        xy = (allp[:, :2].min(axis=0) + allp[:, :2].max(axis=0)) / 2
    return {"mode": mode, "material": material if mode == "trunk" else None, "base_xy_m": [round(float(x), 4) for x in xy],
            "min_z_m": round(zmin, 5)}


def slot_tris_max(cfg, slot):
    """The S1 budget for an L1 mesh of this slot: near card 310, else the slot's l1_tris_max when it has one (rocks: null = as
    shipped), else its tris_max (grass, flower, fern: the same budget as L0)."""
    if slot == "NearCard":
        return NEAR_CARD_TRIS
    s = cfg["slots"][slot]
    return s["l1_tris_max"] if "l1_tris_max" in s else s["tris_max"]


def load_report():
    p = os.path.join(PREP, "report.json")
    if os.path.exists(p):
        return json.load(open(p, encoding="utf-8"))
    return {"_doc": "L1 prepared scatter meshes (prep_polyhaven.py, make_bush.py). Triangles, bounds (m, +Z up), the vertex-position "
                    "sha256 and the pivot are read from the exported .glb (Tools/scatter_glb.py). script_sha256 / helpers_sha256 / "
                    "recipe_sha256 identify what built each row.", "date": "deterministic", "meshes": []}


def save_report(rep):
    rep["meshes"].sort(key=lambda m: m["name"])
    with open(os.path.join(PREP, "report.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(rep, f, indent=1)


def run_driver(recipes, script, extra_jobs=None):
    """Shared by make_bush.py: run Blender on script with the recipes, post-process, merge rows into the report."""
    os.makedirs(PREP, exist_ok=True)
    os.makedirs(WORK, exist_ok=True)
    jp = os.path.join(WORK, os.path.basename(script) + ".jobs.json")
    fp = os.path.join(WORK, os.path.basename(script) + ".facts.json")
    if os.path.exists(fp):
        os.remove(fp)
    jobs = {"src": SRC.replace("\\", "/"), "out": PREP.replace("\\", "/"), "facts": fp.replace("\\", "/"), "recipes": recipes}
    if extra_jobs:
        jobs.update(extra_jobs)
    json.dump(jobs, open(jp, "w"), indent=1)
    cmd = [BLENDER, "--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", jp]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log = os.path.join(WORK, os.path.basename(script) + ".log")
    open(log, "w", encoding="utf-8").write(p.stdout + "\n" + p.stderr)
    if p.returncode != 0 or not os.path.exists(fp):
        tail = "\n".join((p.stdout + p.stderr).splitlines()[-25:])
        raise RuntimeError(f"Blender failed (exit {p.returncode}); log {log}\n{tail}")
    facts = json.load(open(fp))
    return add_rows(recipes, script, facts)


def add_rows(recipes, script, facts):
    """Post-process each recipe's exported .glb and merge its row into prepared/report.json."""
    cfg = json.load(open(CONFIG, encoding="utf-8"))
    rep = load_report()
    names = {r["name"] for r in recipes}
    rep["meshes"] = [m for m in rep["meshes"] if m["name"] not in names]
    bad = []
    script_sha = sha256_file(script)
    helpers_sha = sha256_file(os.path.abspath(__file__))
    for r in recipes:
        path = os.path.join(PREP, r["name"] + ".glb")
        sha, nbytes, st = postprocess(path, r)
        f = facts[r["name"]]
        lo, hi = r.get("tris_min", 1), r["budget"]
        ok = (lo <= st["triangles"] <= hi) if hi else True
        smax = slot_tris_max(cfg, r["slot"])
        slot_ok = st["triangles"] <= smax if smax else True
        if not ok:
            bad.append(f"{r['name']} tris={st['triangles']} budget<={hi}")
        if not slot_ok:
            bad.append(f"{r['name']} tris={st['triangles']} over the S1 slot budget {r['slot']} <= {smax}")
        if not st["identity_nodes"]:
            bad.append(f"{r['name']} has node transforms")
        if st["mesh_names"] != [r["name"]]:
            bad.append(f"{r['name']} mesh names {st['mesh_names']}")
        if abs(st["bounds_min"][2]) > 1e-3:
            bad.append(f"{r['name']} min z {st['bounds_min'][2]} (pivot not at base)")
        if r.get("height") and abs((st["bounds_max"][2] - st["bounds_min"][2]) - r["height"]) > 0.01 * r["height"]:
            bad.append(f"{r['name']} height {st['bounds_max'][2]} != {r['height']}")
        piv = measured_pivot(path, r.get("pivot", "base_centre"), r.get("pivot_material"))
        if piv["mode"] in ("trunk", "root_disc") and max(abs(x) for x in piv["base_xy_m"]) > 0.05:
            bad.append(f"{r['name']} trunk base at {piv['base_xy_m']} (more than 5 cm off the pivot)")
        row = {"name": r["name"], "slot": r["slot"], "role": r.get("role", "primary"), "file": r["name"] + ".glb", "sha256": sha,
               "bytes": nbytes, "script": os.path.basename(script), "script_sha256": script_sha, "helpers_sha256": helpers_sha,
               "recipe_sha256": r["_recipe_sha256"], "sources": r["sources"], "source_meshes": r.get("objects", []),
               "budget": hi, "budget_ok": ok, "slot_tris_max": smax, "slot_budget_ok": slot_ok, "note": r.get("note", ""),
               "normal_maps": "nor_gl (GL convention; Interchange flips once, F23)", "alpha_materials": r.get("alpha", []),
               "pivot": piv, **f, **st}
        rep["meshes"].append(row)
        print(f"  {r['name']:<24} tris={st['triangles']:>6} (src {f.get('source_triangles') or 0:>8}) h={st['bounds_max'][2]:.2f} m "
              f"scale={f.get('scale_factor')} {'ok' if ok and slot_ok else 'OVER'}", flush=True)
    save_report(rep)
    return rep, bad


def atlas_regions(opacity, min_w=4, min_h=20):
    """Stem regions of an atlas opacity map: bands of rows with any opacity, then column runs inside each band, each tightened to its
    own rows. Returns [(x0, y0, x1, y1)] in pixels, in scan order."""
    import numpy as np

    def runs(v):
        out, st = [], None
        for k, x in enumerate(v):
            if x and st is None:
                st = k
            if not x and st is not None:
                out.append((st, k))
                st = None
        if st is not None:
            out.append((st, len(v)))
        return out
    regs = []
    for y0, y1 in runs(opacity.any(axis=1)):
        if y1 - y0 < min_h:
            continue
        for x0, x1 in runs(opacity[y0:y1].any(axis=0)):
            if x1 - x0 < min_w:
                continue
            ys = np.nonzero(opacity[y0:y1, x0:x1].any(axis=1))[0]
            regs.append((x0, y0 + int(ys[0]), x1, y0 + int(ys[-1]) + 1))
    return regs


def build_seedcard(r):
    """Seed-head card clump (r9 grass row): ambientCG atlases side by side in one RGBA texture, one alpha-cut quad per card, each
    showing one stem region, rooted on a small disc, yawed and leaning by Mix32 draws. Pure Python (the make_scatter_meshes writer)."""
    import io
    import numpy as np
    from PIL import Image
    sys.path.insert(0, TOOLS)
    import make_scatter_meshes as msm
    cols, nors, regions, xoff = [], [], [], 0
    for aid in r["atlases"]:
        d = os.path.join(SRC, "raw", "ambientcg", aid)
        c = Image.open(os.path.join(d, f"{aid}_1K-PNG_Color.png")).convert("RGB")
        a = alpha_u8(os.path.join(d, f"{aid}_1K-PNG_Opacity.png"))
        nmap = Image.open(os.path.join(d, f"{aid}_1K-PNG_NormalGL.png")).convert("RGB")
        op = np.asarray(a) > 127
        for (x0, y0, x1, y1) in atlas_regions(op):
            regions.append((x0 + xoff, y0, x1 + xoff, y1, aid))
        cols.append(Image.merge("RGBA", (*c.split(), a)))
        nors.append(nmap)
        xoff += c.size[0]
    W, H = xoff, cols[0].size[1]
    atlas = Image.new("RGBA", (W, H))
    natlas = Image.new("RGB", (W, H))
    x = 0
    for c, nm in zip(cols, nors):
        atlas.paste(c, (x, 0))
        natlas.paste(nm, (x, 0))
        x += c.size[0]
    bb, nb = io.BytesIO(), io.BytesIO()
    atlas.save(bb, format="PNG", optimize=False)
    natlas.save(nb, format="PNG", optimize=False)
    mesh = msm.Mesh(r["name"])
    mesh.material("M_SeedCard", [1, 1, 1], 0.85, True)
    rng = msm.Rng(r["seed"], 1)
    hmax = max(y1 - y0 for (_, y0, _, y1, _) in regions)
    for k in range(r["cards"]):
        x0, y0, x1, y1, _ = regions[rng.u32() % len(regions)]
        h = r["height"] * (y1 - y0) / hmax * rng.uni(0.8, 1.1)
        w = h * (x1 - x0) / (y1 - y0)
        ang = 2 * math.pi * rng.unit()
        rr = r["radius"] * math.sqrt(rng.unit())
        base = np.array([rr * math.cos(ang), rr * math.sin(ang), 0.0])
        yaw = 2 * math.pi * rng.unit()
        lean = math.radians(rng.pick(r["lean_deg"]))
        side = np.array([math.cos(yaw), math.sin(yaw), 0.0])
        out = np.array([-math.sin(yaw), math.cos(yaw), 0.0])
        up = np.array([0.0, 0.0, math.cos(lean)]) + out * math.sin(lean)
        pos = np.array([base - side * w / 2, base + side * w / 2, base + side * w / 2 + up * h, base - side * w / 2 + up * h])
        uv = np.array([[x0 / W, y1 / H], [x1 / W, y1 / H], [x1 / W, y0 / H], [x0 / W, y0 / H]])
        mesh.add(msm.Part("M_SeedCard", pos, [(0, 1, 2), (0, 2, 3)], uv, var=rng.unit(), mask=1.0, ao=np.array([0.4, 0.4, 1.0, 1.0])))
    msm.finalise(mesh, r["height"])
    path = os.path.join(PREP, r["name"] + ".glb")
    os.makedirs(PREP, exist_ok=True)
    msm.write_glb(mesh, path, textures={"M_SeedCard": {"base": bb.getvalue(), "normal": nb.getvalue(), "name": "seedcard_atlas"}})
    return {"source_triangles": None, "cards_n": r["cards"], "atlas_regions": len(regions), "scale_factor": 1.0,
            "source_height_m": None, "pre_fit_height_m": r["height"], "target_height_m": r["height"], "blender_triangles": None,
            "uv": {"M_SeedCard": {"uv_layer": "TEXCOORD_0", "texture_transform": None, "generated": "atlas regions"}}}


def main():
    import argparse
    refuse_mirror()
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    a = ap.parse_args()
    cfg = json.load(open(CONFIG, encoding="utf-8"))
    recipes = [dict(r, _recipe_sha256=recipe_sha(r)) for r in cfg["prepared"]]
    if a.only:
        keep = set(a.only.split(","))
        recipes = [r for r in recipes if r["name"] in keep]
    for r in recipes:
        r.setdefault("seed", mix32(cfg["seed"] ^ mix32(len(r["name"]) * 131 + sum(ord(c) for c in r["name"]))))
        r["sources"] = [f"polyhaven:{r['source']}"]
        r["_alpha_images"] = pack_alpha(r["source"], r)
        if r.get("cards", {}).get("wood_filter"):
            r["_texel_grids"] = texel_grids(r["_alpha_images"], r["cards"]["materials"])
    rep, bad = run_driver(recipes, os.path.abspath(__file__)) if recipes else (load_report(), [])
    seeds = [dict(r, _recipe_sha256=recipe_sha(r)) for r in cfg.get("seedcards", []) if not a.only or r["name"] in set(a.only.split(","))]
    if seeds:
        facts = {}
        for r in seeds:
            r["sources"] = [f"ambientcg:{x}" for x in r["atlases"]]
            r["alpha"] = ["M_SeedCard"]
            facts[r["name"]] = build_seedcard(r)
        rep, bad2 = add_rows(seeds, os.path.abspath(__file__), facts)
        bad += bad2
    if bad:
        print("SCATTER_PREP FAIL " + "; ".join(bad))
        return 1
    n = len([m for m in rep["meshes"] if m["script"] == os.path.basename(__file__)])
    print(f"SCATTER_PREP OK meshes={n} (prep_polyhaven; report rows {len(rep['meshes'])})")
    return 0


if __name__ == "__main__":
    if inside_blender():
        blender_main(sys.argv[sys.argv.index("--") + 1:])
    else:
        sys.exit(main())
