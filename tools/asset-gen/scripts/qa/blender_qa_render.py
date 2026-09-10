# -*- coding: utf-8 -*-
"""
Render ortho QA views of a .glb so a human or agent can eyeball silhouette, proportions, facing
AND SURFACE.
Run:  blender -b -P blender_qa_render.py -- <in.glb> <out_prefix> [res] [color_type]

Writes <out_prefix>_front.png, _side.png, _iso.png.

COLOUR, AND WHY THIS SCRIPT USED TO LIE
---------------------------------------
Workbench renders whatever `scene.display.shading.color_type` selects. This script never set that
property at all, so it fell through to the default -- which is NOT 'TEXTURE'. The consequence is
that every contact sheet this script has ever produced was structurally incapable of showing an
asset's albedo: a fully textured model and a flat grey one render identically. That made the
sheet useless as evidence for the one question the texture pass needs answered, and worse, it
would have been read as "the bake produced grey" rather than "the renderer cannot show colour".

'TEXTURE' is therefore the default here. It degrades gracefully: an untextured mesh falls back to
its material base colour, which is the correct read for an untextured asset.

Studio lighting and the cavity pass are kept -- they carry form, which matters just as much at the
distance an RTS camera sits at.
"""
import bpy, sys, mathutils

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(args) < 2:
    raise SystemExit("usage: -- <in.glb> <out_prefix> [res] [color_type]")
src, out_prefix = args[0], args[1]
res = int(args[2]) if len(args) > 2 else 512
color_type = args[3].upper() if len(args) > 3 else "TEXTURE"

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
if not meshes:
    raise SystemExit("no mesh in glb")
bpy.ops.object.select_all(action="DESELECT")
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
obj = bpy.context.view_layer.objects.active

corners = [obj.matrix_world @ mathutils.Vector(c) for c in obj.bound_box]
xs = [c.x for c in corners]; ys = [c.y for c in corners]; zs = [c.z for c in corners]
center = mathutils.Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, (min(zs) + max(zs)) / 2))
size = max(max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)) or 1.0

cam_data = bpy.data.cameras.new("cam")
cam_data.type = "ORTHO"
cam_data.ortho_scale = size * 1.25
cam = bpy.data.objects.new("cam", cam_data)
bpy.context.scene.collection.objects.link(cam)
scn = bpy.context.scene
scn.camera = cam
scn.render.engine = "BLENDER_WORKBENCH"
scn.render.resolution_x = res
scn.render.resolution_y = res
scn.render.film_transparent = False
sh = scn.display.shading
sh.light = "STUDIO"
# THE FIX: without this the renderer cannot display an albedo at all.
try:
    sh.color_type = color_type
except TypeError:
    print(f"QA_RENDER_WARN unsupported color_type {color_type!r}; falling back to MATERIAL")
    sh.color_type = "MATERIAL"
sh.show_cavity = True
sh.cavity_type = "WORLD"

# Report what was actually used, so a grey sheet can never again be mistaken for a grey asset.
n_img = len(bpy.data.images)
has_tex = any(
    n.type == "TEX_IMAGE" and n.image is not None
    for m in obj.data.materials if m and m.use_nodes
    for n in m.node_tree.nodes
)
print(f"QA_RENDER_INFO color_type={sh.color_type} light={sh.light} "
      f"materials={len(obj.data.materials)} images={n_img} material_has_image_texture={has_tex}")

dist = size * 2.5
# Blender Z-up after glTF import; glb-front(+Z) maps to Blender -Y. Use clear, labelled angles.
views = {
    "front": mathutils.Vector((0, -1, 0)),
    "side":  mathutils.Vector((1, 0, 0)),
    "iso":   mathutils.Vector((1, -1, 0.7)),
}
for name, dirv in views.items():
    d = dirv.normalized()
    cam.location = center + d * dist
    cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()
    scn.render.filepath = f"{out_prefix}_{name}.png"
    bpy.ops.render.render(write_still=True)
    print(f"RENDER {name} -> {scn.render.filepath}")
