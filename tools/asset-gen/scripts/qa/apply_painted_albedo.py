# -*- coding: utf-8 -*-
"""Swap a mesh's base-colour texture for a painted one, keeping everything else intact.

Run with the PINNED pipeline Blender:
  blender -b -P apply_painted_albedo.py -- --in <lp.glb> --albedo <painted.png> --out <final.glb>

Emits one JSON line: APPLY_JSON {...}. Exit 0 on success.

What it deliberately KEEPS
--------------------------
* The tangent NORMAL MAP baked by hp_to_lp_bake.py. Hunyuan3D-Paint emits no normal map — normals
  are an INPUT to its multi-view diffusion, never an output — so the high-poly-to-low-poly normal
  bake remains the only source of that surface detail and is genuinely worth preserving. Only
  image slot 0 is replaced.
* The mesh, its UVs and its transform, untouched. This script rewires a material; it is not
  another geometry stage.

What it FORCES, because the gate requires it
--------------------------------------------
* Exactly ONE material (profile `max_materials: 1`).
* A WHITE baseColorFactor — a fresh Principled BSDF defaults to 0.8 grey, and the exporter writes
  that socket into the material where it MULTIPLIES the albedo, rendering every texture 20% dark.
* A plain uncompressed GLB with embedded textures (Godot rejects Draco/meshopt/quantized, err 43).
"""

import bpy
import sys
import os
import json


def argv_after_ddash():
    a = sys.argv
    return a[a.index("--") + 1:] if "--" in a else []


def main():
    import argparse
    ap = argparse.ArgumentParser(prog="apply_painted_albedo")
    ap.add_argument("--in", dest="src", required=True)
    ap.add_argument("--albedo", required=True)
    ap.add_argument("--out", dest="dst", required=True)
    args = ap.parse_args(argv_after_ddash())

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=args.src)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        print("APPLY_JSON " + json.dumps({"ok": False, "error": "no mesh in glb"}))
        raise SystemExit(1)

    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active

    if not obj.data.materials or obj.data.materials[0] is None:
        print("APPLY_JSON " + json.dumps({"ok": False, "error": "mesh has no material"}))
        raise SystemExit(1)
    mat = obj.data.materials[0]
    while len(obj.data.materials) > 1:
        obj.data.materials.pop()

    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        print("APPLY_JSON " + json.dumps({"ok": False, "error": "no Principled BSDF"}))
        raise SystemExit(1)

    # Find the image node feeding Base Color and repoint it. Repointing rather than rebuilding
    # keeps the normal-map branch and its Normal Map node wired exactly as the bake left them.
    base_in = bsdf.inputs["Base Color"]
    tex_node = None
    if base_in.is_linked:
        n = base_in.links[0].from_node
        if n.type == "TEX_IMAGE":
            tex_node = n
    if tex_node is None:
        tex_node = nt.nodes.new("ShaderNodeTexImage")
        tex_node.location = (-250, 150)
        nt.links.new(tex_node.outputs["Color"], base_in)

    old_name = tex_node.image.name if tex_node.image else None
    painted = bpy.data.images.load(os.path.abspath(args.albedo), check_existing=False)
    painted.name = os.path.splitext(os.path.basename(args.dst))[0] + "_hy3d_albedo"
    tex_node.image = painted

    # White factor, or the exporter writes 0.8 grey and the gate fails by name.
    base_in.default_value = (1.0, 1.0, 1.0, 1.0)

    has_normal = any(
        n.type == "NORMAL_MAP" and n.inputs["Color"].is_linked for n in nt.nodes
    )

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    kwargs = dict(
        filepath=args.dst,
        export_format="GLB",
        export_yup=True,
        export_apply=True,
        use_selection=True,
        export_image_format="AUTO",
    )
    try:
        bpy.ops.export_scene.gltf(export_draco_mesh_compression_enable=False, **kwargs)
    except TypeError:
        bpy.ops.export_scene.gltf(**kwargs)

    print("APPLY_JSON " + json.dumps({
        "ok": True,
        "src": args.src,
        "albedo": args.albedo,
        "dst": args.dst,
        "replaced_image": old_name,
        "normal_map_kept": bool(has_normal),
        "materials": len(obj.data.materials),
        "out_bytes": os.path.getsize(args.dst) if os.path.exists(args.dst) else 0,
    }))


if __name__ == "__main__":
    main()
