# -*- coding: utf-8 -*-
"""
Make backbone-proof test meshes with Blender (no AI models needed).
Run:  blender -b -P make_test_mesh.py -- <out_dir>
Produces:
  <out_dir>/test_dense.glb  — a dense (~60k tri) subdivided Suzanne, PLAIN export (positive case)
  <out_dir>/test_draco.glb  — the SAME mesh, Draco-compressed (negative case: must FAIL the gate
                              and be rejected by Godot 4.6.2 with err 43)
"""
import bpy, sys, json, os

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out_dir = args[0] if args else "."
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.mesh.primitive_monkey_add()
obj = bpy.context.active_object

sub = obj.modifiers.new("sub", "SUBSURF")
sub.levels = 3
bpy.ops.object.modifier_apply(modifier=sub.name)
tri = obj.modifiers.new("tri", "TRIANGULATE")
bpy.ops.object.modifier_apply(modifier=tri.name)
obj.data.calc_loop_triangles()
tris = len(obj.data.loop_triangles)

plain = os.path.join(out_dir, "test_dense.glb")
draco = os.path.join(out_dir, "test_draco.glb")

bpy.ops.export_scene.gltf(filepath=plain, export_format="GLB", export_yup=True,
                          export_draco_mesh_compression_enable=False)
bpy.ops.export_scene.gltf(filepath=draco, export_format="GLB", export_yup=True,
                          export_draco_mesh_compression_enable=True)

print("MAKE_JSON " + json.dumps({"tris": tris, "plain": plain, "draco": draco}))
