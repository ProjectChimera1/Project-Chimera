#!/usr/bin/env python
"""Contact sheet of every L0 and L1 scatter mesh for Alec (plan-c-scatter.md 3.6, task S1). No Unreal; Blender headless renders the tiles.

Run with plain Python: it starts Blender 4.5.10 (--background --factory-startup) on this same file, which imports each .glb of
ScatterSrc/L0 and ScatterSrc/prepared, renders one tile per mesh (3/4 view from 28 deg up, sun + sky, a ground disc, a 1.8 m figure for
scale beside meshes taller than 0.9 m), and then composes T/Out/scatter_preview/contact_sheet.jpg (labels: name, level, triangles, height)
and a phone copy contact_sheet-phone.jpg (<= 1600 px wide, <= 1 MB).
L0 tiles show the glTF base colour x vertex AO (vertex colour R) only: their real materials (triplanar leaf and bark textures, terrain-normal
grass shading) are built in S3. L1 tiles show the imported Poly Haven textures with alpha clip.
Every pixel on the sheet is ours or CC0 (no Manor Lords reference pixels), so it can go to EV as is.
Usage: python preview_scatter.py [--only NAME,...] [--tile 360] [--engine EEVEE|WORKBENCH]
"""
import json
import math
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
T = os.path.dirname(HERE)
SRC = os.environ.get("CHIMERA_SCATTER_SRC", os.path.join(T, "ScatterSrc"))
OUTDIR = os.path.join(T, "Out", "scatter_preview")
BLENDER = os.environ.get("CHIMERA_BLENDER", "D:/tools/blender/blender-4.5.10-windows-x64/blender.exe")


def inside_blender():
    try:
        import bpy  # noqa: F401
        return True
    except ImportError:
        return False


# ------------------------------------------------------------------ Blender side ------------------------------------------------
def blender_main(argv):
    import bpy
    from mathutils import Vector

    args = dict(zip(argv[0::2], argv[1::2]))
    tile = int(args.get("--tile", "360"))
    jobs = json.load(open(args["--jobs"], encoding="utf-8"))
    engine = args.get("--engine", "EEVEE")

    def reset():
        bpy.ops.wm.read_factory_settings(use_empty=True)
        sc = bpy.context.scene
        try:
            sc.render.engine = "BLENDER_EEVEE_NEXT" if engine == "EEVEE" else "BLENDER_WORKBENCH"
        except TypeError:
            sc.render.engine = "BLENDER_EEVEE" if engine == "EEVEE" else "BLENDER_WORKBENCH"
        sc.render.resolution_x = tile
        sc.render.resolution_y = tile
        sc.render.film_transparent = False
        sc.view_settings.view_transform = "Standard"
        if sc.render.engine.startswith("BLENDER_EEVEE"):
            try:
                sc.eevee.taa_render_samples = 32
                sc.eevee.use_shadows = True
            except AttributeError:
                pass
        world = bpy.data.worlds.new("W")
        sc.world = world
        world.use_nodes = True
        bg = world.node_tree.nodes["Background"]
        bg.inputs[0].default_value = (0.62, 0.70, 0.80, 1.0)
        bg.inputs[1].default_value = 0.9
        sun = bpy.data.lights.new("Sun", "SUN")
        sun.energy = 3.2
        sun.angle = math.radians(2.0)
        so = bpy.data.objects.new("Sun", sun)
        so.rotation_euler = (math.radians(50), 0, math.radians(-35))
        sc.collection.objects.link(so)
        return sc

    def mat_simple(name, rgb):
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        nt = m.node_tree
        bsdf = nt.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1)
        bsdf.inputs["Roughness"].default_value = 0.9
        return m

    def l0_material(m, colours):
        """base colour (the glTF baseColorFactor, read by the driver) x vertex AO (COLOR_0.R)."""
        nt = m.node_tree
        bsdf = [n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"][0]
        name = m.name.split(".")[0]
        base = tuple(colours.get(name, (0.5, 0.5, 0.5, 1.0)))
        for link in list(bsdf.inputs["Base Color"].links):
            nt.links.remove(link)
        ca = nt.nodes.new("ShaderNodeVertexColor")
        sep = nt.nodes.new("ShaderNodeSeparateColor")
        mul = nt.nodes.new("ShaderNodeMix")
        mul.data_type = "RGBA"
        mul.blend_type = "MULTIPLY"
        mul.inputs[0].default_value = 1.0
        mul.inputs[6].default_value = base
        nt.links.new(ca.outputs["Color"], sep.inputs[0])
        comb = nt.nodes.new("ShaderNodeCombineColor")
        for i in range(3):
            nt.links.new(sep.outputs[0], comb.inputs[i])
        nt.links.new(comb.outputs[0], mul.inputs[7])
        nt.links.new(mul.outputs[2], bsdf.inputs["Base Color"])

    def alpha_clip(m):
        # the glTF importer multiplies COLOR_0 into the base colour; our vertex colour is data (AO, variation, mask), not a tint
        if m.node_tree:
            for n in list(m.node_tree.nodes):
                if n.type in ("VERTEX_COLOR", "ATTRIBUTE"):
                    for out in n.outputs:
                        for link in list(out.links):
                            tgt = link.to_node
                            m.node_tree.links.remove(link)
                            if tgt.type == "MIX" or tgt.type == "MIX_RGB":
                                for inp in tgt.inputs:
                                    if inp.type == "RGBA" and not inp.is_linked:
                                        inp.default_value = (1, 1, 1, 1)
        try:
            m.surface_render_method = "DITHERED"
        except AttributeError:
            pass
        try:
            m.blend_method = "CLIP"
        except (AttributeError, TypeError):
            pass

    results = []
    for job in jobs:
        sc = reset()
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=job["file"])
        objs = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]
        for o in objs:
            for slot in o.material_slots:
                if slot.material is None:
                    continue
                if job["level"] == "L0":
                    try:
                        l0_material(slot.material, job.get("colours", {}))
                    except Exception as e:  # preview only: fall back to the plain base colour
                        print("PREVIEW l0_material failed", job["name"], e)
                else:
                    alpha_clip(slot.material)
        bpy.context.view_layer.update()
        pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
        mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
        mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        size = mx - mn
        h = size.z
        span = max(size.x, size.y, h, 0.05)
        centre = (mn + mx) / 2
        # ground disc and a 1.8 m figure for scale
        bpy.ops.mesh.primitive_circle_add(vertices=48, radius=span * 3.0, fill_type="NGON", location=(centre.x, centre.y, 0))
        g = bpy.context.active_object
        g.data.materials.append(mat_simple("Ground", (0.20, 0.22, 0.12)))
        if h > 0.9:
            bpy.ops.mesh.primitive_cylinder_add(vertices=12, radius=0.22, depth=1.8, location=(mn.x - 0.6 - span * 0.05, centre.y, 0.9))
            fig = bpy.context.active_object
            fig.data.materials.append(mat_simple("Figure", (0.75, 0.25, 0.20)))
            pts2 = pts + [Vector((mn.x - 0.8 - span * 0.05, centre.y, 0)), Vector((mn.x - 0.6, centre.y, 1.8))]
            mn = Vector((min(p.x for p in pts2), min(p.y for p in pts2), min(p.z for p in pts2)))
            mx = Vector((max(p.x for p in pts2), max(p.y for p in pts2), max(p.z for p in pts2)))
            centre = (mn + mx) / 2
            span = max((mx - mn).x, (mx - mn).y, (mx - mn).z)
        cam = bpy.data.cameras.new("Cam")
        cam.lens = 50
        co = bpy.data.objects.new("Cam", cam)
        sc.collection.objects.link(co)
        sc.camera = co
        el, az = math.radians(28), math.radians(-60)
        radius = math.sqrt(sum(((mx - mn)[i] / 2) ** 2 for i in range(3)))
        dist = radius / math.tan(math.radians(36) / 2) * 1.05
        target = Vector((centre.x, centre.y, (mn.z + mx.z) / 2))
        co.location = target + Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az), math.sin(el))) * dist
        direction = target - co.location
        co.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        cam.clip_start = dist * 0.01
        cam.clip_end = dist * 10
        sc.render.filepath = job["png"]
        bpy.ops.render.render(write_still=True)
        results.append({"name": job["name"], "png": job["png"], "h": h, "w": max(size.x, size.y)})
        print("PREVIEW_TILE", job["name"], round(h, 3))
    json.dump(results, open(args["--result"], "w"), indent=1)


# ------------------------------------------------------------------ driver side -------------------------------------------------
def glb_colours(path):
    """material name -> baseColorFactor from a .glb's JSON chunk."""
    import struct
    with open(path, "rb") as f:
        f.read(12)
        n, _ = struct.unpack("<II", f.read(8))
        g = json.loads(f.read(n))
    return {m.get("name", ""): m.get("pbrMetallicRoughness", {}).get("baseColorFactor", [0.5, 0.5, 0.5, 1]) for m in g.get("materials", [])}


def collect():
    jobs = []
    for level, sub in (("L0", "L0"), ("L1", "prepared")):
        rp = os.path.join(SRC, sub, "report.json")
        if not os.path.exists(rp):
            continue
        rep = json.load(open(rp, encoding="utf-8"))
        for m in rep["meshes"]:
            jobs.append({"name": m["name"], "level": level, "slot": m["slot"], "file": os.path.join(SRC, sub, m["file"]).replace("\\", "/"),
                         "tris": m["triangles"], "role": m.get("role", "primary"), "note": m.get("note", ""),
                         "png": os.path.join(OUTDIR, "tiles", f"{level}_{m['name']}.png").replace("\\", "/"),
                         "colours": glb_colours(os.path.join(SRC, sub, m["file"]))})
    return jobs


def sheet(jobs, results, tile):
    from PIL import Image, ImageDraw, ImageFont
    byname = {(r["png"]): r for r in results}
    cols = 7
    lab = 46
    rows_l = {}
    for j in jobs:
        rows_l.setdefault(j["level"], []).append(j)
    blocks = []
    for level in ("L0", "L1"):
        if level in rows_l:
            blocks.append((level, rows_l[level]))
    head = 70
    sec = 40
    total_rows = sum((len(js) + cols - 1) // cols for _, js in blocks)
    W = cols * tile
    H = head + len(blocks) * sec + total_rows * (tile + lab)
    img = Image.new("RGB", (W, H), (24, 26, 28))
    d = ImageDraw.Draw(img)
    try:
        f1 = ImageFont.truetype("arial.ttf", 30)
        f2 = ImageFont.truetype("arial.ttf", 19)
        f3 = ImageFont.truetype("arial.ttf", 16)
    except OSError:
        f1 = f2 = f3 = ImageFont.load_default()
    d.text((14, 12), "Chimera scatter meshes (S1): L0 procedural (project-original) and L1 CC0 Poly Haven / ambientCG", fill=(235, 235, 235), font=f1)
    d.text((14, 46), "L0 tiles: base colour x vertex AO only (real materials come in S3). Red post = 1.8 m figure. Labels: triangles, height, widest footprint.",
           fill=(170, 170, 170), font=f3)
    y = head
    for level, js in blocks:
        d.text((14, y + 8), "L0 procedural" if level == "L0" else "L1 prepared from CC0 (primary per slot; '__' = candidate for the S6 bake-off)",
               fill=(240, 210, 120), font=f2)
        y += sec
        for i, j in enumerate(js):
            r, c = divmod(i, cols)
            x0, y0 = c * tile, y + r * (tile + lab)
            res = byname.get(j["png"])
            if res and os.path.exists(j["png"]):
                im = Image.open(j["png"]).convert("RGB").resize((tile, tile))
                img.paste(im, (x0, y0))
                hh = f"h {res['h']:.2f} m, w {res['w']:.2f} m"
            else:
                hh = "render failed"
            d.text((x0 + 6, y0 + tile + 3), j["name"], fill=(235, 235, 235), font=f2)
            d.text((x0 + 6, y0 + tile + 25), f"{j['tris']:,} tris  {hh}", fill=(160, 200, 160), font=f3)
        y += ((len(js) + cols - 1) // cols) * (tile + lab)
    return img


def main():
    import argparse
    if os.path.basename(T) == "unreal-terrain" and "CHIMERA_SCATTER_SRC" not in os.environ:
        print(f"REFUSED: {T} is the repo mirror; run the ChimeraTerrain copy (or set CHIMERA_SCATTER_SRC)")
        return 2
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", default="")
    ap.add_argument("--tile", type=int, default=360)
    ap.add_argument("--engine", default="EEVEE")
    a = ap.parse_args()
    jobs = collect()
    if a.only:
        keep = set(a.only.split(","))
        jobs = [j for j in jobs if j["name"] in keep]
    os.makedirs(os.path.join(OUTDIR, "tiles"), exist_ok=True)
    jp = os.path.join(OUTDIR, "jobs.json")
    rp = os.path.join(OUTDIR, "results.json")
    json.dump(jobs, open(jp, "w"), indent=1)
    # no stale output can pass as this run's: the previous results and this run's tiles go first, and a raising script exits 1
    for f in [rp] + [j["png"] for j in jobs]:
        if os.path.exists(f):
            os.remove(f)
    cmd = [BLENDER, "--background", "--factory-startup", "--python-exit-code", "1", "--python", os.path.abspath(__file__), "--",
           "--jobs", jp, "--result", rp, "--tile", str(a.tile), "--engine", a.engine]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log = os.path.join(OUTDIR, "blender.log")
    open(log, "w", encoding="utf-8").write(p.stdout + "\n" + p.stderr)
    if p.returncode != 0 or not os.path.exists(rp):
        print(f"PREVIEW FAIL blender exit {p.returncode}; see {log}")
        return 1
    results = json.load(open(rp))
    img = sheet(jobs, results, a.tile)
    out = os.path.join(OUTDIR, "contact_sheet.jpg" if not a.only else "contact_partial.jpg")
    img.save(out, quality=90)
    phone = out.replace(".jpg", "-phone.jpg")
    w = min(1600, img.width)
    ph = img.resize((w, int(img.height * w / img.width)))
    q = 85
    while True:
        ph.save(phone, quality=q)
        if os.path.getsize(phone) <= 1_000_000 or q <= 40:
            break
        q -= 5
    phone_bytes = os.path.getsize(phone)
    rendered = sum(1 for r in results if os.path.exists(r["png"]))
    ok = len(results) == len(jobs) == rendered and phone_bytes <= 1_000_000 and ph.width <= 1600
    print(f"PREVIEW {'OK' if ok else 'FAIL'} tiles={rendered}/{len(jobs)} sheet={out} ({img.width}x{img.height}) phone={phone} "
          f"({ph.width}x{ph.height}, {phone_bytes // 1024} KB, limit 1600 px / 1000 KB)")
    return 0 if ok else 1


if __name__ == "__main__":
    if inside_blender():
        argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
        blender_main(argv)
    else:
        sys.exit(main())
