# -*- coding: utf-8 -*-
"""
Headless Blender normalize/retopo/export for the asset-gen pipeline.
Run:  blender -b -P blender_pipeline.py -- <in.glb|.obj> <out.glb> <tri_target> [kind]

Pass (all bpy APIs present in Blender 4.x):
  1. import mesh
  2. join all meshes into one object
  3. VOXEL REMESH -> clean watertight manifold (AI voxel meshes are high-genus; collapse-decimate
     alone can't hit budget on them, and they're rarely watertight)
  4. triangulate (exact tri counts)
  5. decimate (collapse) to the tri_target
  6. single material (clear + one flat material)
  7. origin to base/feet: translate so min-Z = 0 and X/Y centered, origin at world 0
  8. export PLAIN GLB (Y-up, no Draco/quantization/meshopt) — the no-compression contract
  9. print one JSON metrics line: PIPELINE_JSON {...}
Facing (+Z) is enforced at generation (prompt) and verified by the QA gate, not auto-rotated here.
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

def voxel_remesh(obj, max_dim, divisions=160):
    """Rebuild as a clean watertight manifold ~`divisions` voxels across the largest axis."""
    bpy.context.view_layer.objects.active = obj
    obj.data.remesh_voxel_size = max(max_dim / float(divisions), 1e-4)
    obj.data.remesh_voxel_adaptivity = 0.0
    obj.data.use_remesh_fix_poles = True
    bpy.ops.object.voxel_remesh()

def triangulate(obj):
    m = obj.modifiers.new("tri", "TRIANGULATE")
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)

def decimate_to(obj, target):
    cur = tri_count(obj)
    if cur <= target:
        return cur, cur, 1.0
    ratio = max(0.002, float(target) / float(cur))
    m = obj.modifiers.new("dec", "DECIMATE")
    m.decimate_type = "COLLAPSE"
    m.ratio = ratio
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=m.name)
    return cur, tri_count(obj), ratio

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
    for k in ("export_draco_mesh_compression_enable",):
        kwargs[k] = False
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
    md = max(obj.dimensions.x, obj.dimensions.y, obj.dimensions.z) or 1.0
    voxel_remesh(obj, md)
    triangulate(obj)
    before, after, ratio = decimate_to(obj, target)
    single_material(obj)
    origin_to_base(obj)
    export_plain_glb(dst)

    corners = [obj.matrix_world @ v.co for v in obj.data.vertices]
    xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
    log({
        "kind": kind, "src": src, "dst": dst,
        "tris_before": before, "tris_after": after, "decimate_ratio": round(ratio, 4),
        "materials": len(obj.data.materials),
        "bbox": {"min": [min(xs), min(ys), min(zs)], "max": [max(xs), max(ys), max(zs)]},
        "min_z": min(zs), "center_xy": [(min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0],
        "ok": True,
    })

if __name__ == "__main__":
    main()
