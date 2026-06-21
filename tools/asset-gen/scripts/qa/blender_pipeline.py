# -*- coding: utf-8 -*-
"""
Headless Blender normalize/retopo/export for the asset-gen pipeline.
Run:  blender -b -P blender_pipeline.py -- <in.glb|.obj> <out.glb> <tri_target> [kind]

Pass:
  1. import + join to one object
  2. ADAPTIVE budget: triangulate -> DIRECT decimate-collapse to target (preserves AI surface detail
     = crisp). If the mesh is too messy/multi-shell to reach budget directly (>1.5x target), THEN
     voxel-remesh (clean watertight manifold) and re-decimate. Clean single-subject meshes stay crisp;
     junk meshes still get tamed.
  3. single material (clear + one flat material)
  4. origin to base/feet: min-Z = 0, X/Y centered, origin at world 0
  5. export PLAIN GLB (Y-up, no Draco/quantization/meshopt) — the no-compression contract
  6. print PIPELINE_JSON {...}
Facing (+Z) is enforced at generation (prompt) and verified by the QA gate.
"""
import bpy, sys, json

def argv_after_ddash():
    a = sys.argv
    return a[a.index("--") + 1:] if "--" in a else []

def log(obj):
    print("PIPELINE_JSON " + json.dumps(obj))

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

def join_meshes():
    meshes = all_meshes()
    if not meshes:
        raise SystemExit("no mesh objects after import")
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    return bpy.context.view_layer.objects.active

def tri_count(obj):
    me = obj.data
    me.calc_loop_triangles()
    return len(me.loop_triangles)

def triangulate(obj):
    m = obj.modifiers.new("tri", "TRIANGULATE")
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)

def apply_decimate(obj, ratio):
    m = obj.modifiers.new("dec", "DECIMATE")
    m.decimate_type = "COLLAPSE"
    m.ratio = ratio
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)

def voxel_remesh(obj, max_dim, divisions=240):
    bpy.context.view_layer.objects.active = obj
    obj.data.remesh_voxel_size = max(max_dim / float(divisions), 1e-4)
    obj.data.remesh_voxel_adaptivity = 0.0
    obj.data.use_remesh_fix_poles = True
    bpy.ops.object.voxel_remesh()

def process_to_budget(obj, target):
    """Crisp-first: direct collapse; voxel-remesh fallback only if direct can't hit budget."""
    triangulate(obj)
    before = tri_count(obj)
    if before > target:
        apply_decimate(obj, max(0.002, float(target) / float(before)))
    after = tri_count(obj)
    method = "direct-collapse"
    if after > target * 1.5:  # multi-shell/junk mesh resisted collapse -> clean it
        md = max(obj.dimensions.x, obj.dimensions.y, obj.dimensions.z) or 1.0
        voxel_remesh(obj, md, divisions=240)
        triangulate(obj)
        cur = tri_count(obj)
        if cur > target:
            apply_decimate(obj, max(0.002, float(target) / float(cur)))
        after = tri_count(obj)
        method = "voxel-remesh+collapse"
    return before, after, method

def single_material(obj):
    obj.data.materials.clear()
    mat = bpy.data.materials.new("asset_flat")
    mat.use_nodes = True
    obj.data.materials.append(mat)

def origin_to_base(obj):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    corners = [obj.matrix_world @ v.co for v in obj.data.vertices]
    xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
    cx = (min(xs) + max(xs)) / 2.0
    cy = (min(ys) + max(ys)) / 2.0
    minz = min(zs)
    obj.location.x -= cx
    obj.location.y -= cy
    obj.location.z -= minz
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    bpy.context.scene.cursor.location = (0.0, 0.0, 0.0)
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")

def export_plain_glb(path):
    kwargs = dict(filepath=path, export_format="GLB", export_yup=True, export_apply=True)
    kwargs["export_draco_mesh_compression_enable"] = False
    try:
        bpy.ops.export_scene.gltf(**kwargs)
    except TypeError:
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_yup=True)

def main():
    args = argv_after_ddash()
    if len(args) < 3:
        raise SystemExit("usage: -- <in> <out.glb> <tri_target> [kind]")
    src, dst, target = args[0], args[1], int(args[2])
    kind = args[3] if len(args) > 3 else "unit"

    reset_scene()
    import_any(src)
    obj = join_meshes()
    before, after, method = process_to_budget(obj, target)
    single_material(obj)
    origin_to_base(obj)
    export_plain_glb(dst)

    corners = [obj.matrix_world @ v.co for v in obj.data.vertices]
    xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
    log({
        "kind": kind, "src": src, "dst": dst,
        "tris_before": before, "tris_after": after, "method": method,
        "materials": len(obj.data.materials),
        "bbox": {"min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]},
        "min_z": min(zs), "center_xy": [(min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0],
        "ok": True,
    })

if __name__ == "__main__":
    main()
