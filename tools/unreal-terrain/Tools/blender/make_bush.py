#!/usr/bin/env python
"""L1 composite bushes from CC0 Poly Haven plants (plan-c-scatter.md 3.6 shrubs row, task S1; the r8b section 6 fix (b) for the bush gap).

Each bush (Scripts/scatter/meshes.json "bushes") is a mound of sprigs on a seeded hemisphere: sprig k is a copy of one part prototype
(fern_02, nettle_plant, weed_plant_02, shrub_04 objects, reduced once by card fitting or collapse decimation with the prep_polyhaven.py
helpers, UVs baked per material by bake_uvs; a card part's "wood_filter" drops the islands on the atlas's opaque bark strip first, as
for fir_tree_01, because a branch tube fitted as one grown card is a flat plank), chosen by Mix32 weights, tilted from +Z toward its hemisphere direction, yawed, scaled,
and rooted inside the mound so the leaves face out. Every listed part is drawn at least once: a part the weighted draw missed replaces
the lowest-ring sprig of the most-drawn part (deterministic), so the row's sources are exactly the parts in the mesh. The joined mesh is
pivoted at the base centre, scaled to the slot's nominal height and exported to ScatterSrc/prepared/<Name>.glb (alpha materials MASK;
nor_gl normal maps). The rows join ScatterSrc/prepared/report.json.
Run with plain Python (it starts Blender 4.5.10 headless on this file). Usage: python make_bush.py  -> SCATTER_PREP OK meshes=<n> ...
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prep_polyhaven as pp  # noqa: E402


def sprig_parts(b, names):
    """Part name per sprig: Mix32-weighted draws, then every part guaranteed at least once (see the module doc)."""
    weights = [(p["object"], p["weight"]) for p in b["parts"]]
    wsum = sum(w for _, w in weights)
    out = []
    for k in range(b["sprigs"]):
        h = pp.mix32(b["seed"] ^ pp.mix32(k + 1))
        r = (h & 0xFFFF) / 65536.0 * wsum
        name = weights[-1][0]
        acc = 0.0
        for nm, w in weights:
            acc += w
            if r < acc:
                name = nm
                break
        out.append(name)
    for nm in names:
        if nm in out:
            continue
        most = max(names, key=lambda x: (out.count(x), -names.index(x)))
        idx = max(i for i, x in enumerate(out) if x == most)
        out[idx] = nm
    return out


def blender_main(argv):
    import bpy
    import numpy as np
    from mathutils import Matrix, Vector

    jobs = json.load(open(argv[0], encoding="utf-8"))
    facts = {}
    for b in jobs["recipes"]:
        pp.bl_reset()
        protos, part_src_tris, uv_cache, uv_facts, degenerate, card_stats = {}, {}, {}, {}, {}, {}
        for part in b["parts"]:
            gl = os.path.join(jobs["src"], "raw", "polyhaven", part["source"], f"{part['source']}_1k.gltf")
            objs = pp.bl_keep(pp.bl_import(gl), [part["object"]])
            o = objs[0]
            part_src_tris[part["object"]] = pp.tri_count(o)
            pp.bake_uvs(o, uv_cache, uv_facts)
            for mname, rgba in b["_alpha_images"].items():
                if mname in bpy.data.materials:
                    pp.set_alpha_image(bpy.data.materials[mname], rgba)
            A = pp.mesh_arrays(o.data)
            mats = list(o.data.materials)
            if part["op"] == "cards":
                grids = None
                if part.get("wood_filter"):
                    # shrub_04's atlas carries an opaque bark strip; its branch tubes as grown cards read as planks (as on fir_tree_01)
                    grids = {}
                    for k, m in enumerate(mats):
                        gp = b["_texel_grids"].get(m.name) if m else None
                        if gp:
                            z = np.load(gp)
                            grids[k] = {"alpha": z["alpha"], "gr": z["gr"]}
                    if not grids:
                        raise RuntimeError(f"{b['name']}/{part['object']}: wood_filter set but no texel grid for {[m.name for m in mats]}")
                quads, uvs, mi, st = pp.card_islands(A, set(range(len(mats))), part["target"], pp.mix32(b["seed"] ^ len(protos)),
                                                     part.get("scale_cap", 1.6), 1.0, part.get("wood_filter"), grids)
                card_stats[part["object"]] = st
                proto = pp.make_card_object(part["object"] + "_proto", quads, uvs, mi, mats, bend=0.5)
            else:
                proto = pp.subset_mesh(part["object"] + "_proto", A, np.ones(len(A["mi"]), bool), mats)
                pp.decimate_to(proto, part["target"])
                # slivers the collapse leaves (nettle_plant: 1e-8 m2 after the sprig and height scales) are deleted here, in proto space
                degenerate[part["object"]] = pp.remove_degenerate(proto, 2e-8)
            pp.remove_object(o)
            # prototype pivot: base centre, min z = 0
            co = pp.mesh_arrays(proto.data)["co"]
            proto.data.transform(Matrix.Translation((-(co[:, 0].min() + co[:, 0].max()) / 2, -(co[:, 1].min() + co[:, 1].max()) / 2, -co[:, 2].min())))
            protos[part["object"]] = (proto, float(co[:, 2].max() - co[:, 2].min()), part)
        # sprigs
        names = [p["object"] for p in b["parts"]]
        choice = sprig_parts(b, names)
        copies = []
        n = b["sprigs"]
        golden = math.pi * (3 - 5 ** 0.5)
        R = b["radius"]
        for k in range(n):
            h = pp.mix32(b["seed"] ^ pp.mix32(k + 1))
            proto, ph, part = protos[choice[k]]
            u = (k + 0.5) / n
            zf = 1.0 - u * 0.85                       # cap of the hemisphere first, then rings down toward the ground
            rad = math.sqrt(max(0.0, 1 - zf * zf))
            a = k * golden + ((h >> 16) & 0xFF) / 255.0 * 0.6
            d = Vector((rad * math.cos(a), rad * math.sin(a), zf))
            tilt_dir = (d + Vector((0, 0, b["upright"]))).normalized()
            s = part["scale"][0] + (part["scale"][1] - part["scale"][0]) * ((h >> 24) & 0xFF) / 255.0
            root = Vector((d.x * R * b["root_in"], d.y * R * b["root_in"], max(0.0, d.z * R * b["root_in"] * b["squash"])))
            rot = Vector((0, 0, 1)).rotation_difference(tilt_dir).to_matrix().to_4x4() @ Matrix.Rotation(a * 3.7 + k, 4, "Z")
            m = Matrix.Translation(root) @ rot @ Matrix.Scale(s, 4)
            c = proto.copy()
            c.data = proto.data.copy()
            c.data.transform(m)
            bpy.context.scene.collection.objects.link(c)
            copies.append(c)
        for proto, _, _ in protos.values():
            pp.remove_object(proto)
        final = pp.join(copies, b["name"])
        # sprigs bent below the ground are clamped to it (z >= 0) before the pivot fit
        A = pp.mesh_arrays(final.data)
        co = A["co"]
        co[:, 2] = np.maximum(co[:, 2], 0.0)
        final.data.vertices.foreach_set("co", co.reshape(-1))
        final.data.update()
        s, fit_h, off = pp.fit_height_and_pivot(final, b["height"], "base_centre")
        drawn = sorted(set(choice))
        facts[b["name"]] = {"source_triangles": sum(part_src_tris[x] for x in drawn), "sprigs": n,
                            "sprigs_per_part": {x: choice.count(x) for x in names}, "drawn_sources":
                            sorted({f"polyhaven:{p['source']}" for p in b["parts"] if p["object"] in drawn}),
                            "source_height_m": None, "scale_factor": round(s, 6), "pre_fit_height_m": round(fit_h, 4),
                            "pivot_offset_removed_m": off, "target_height_m": b["height"], "blender_triangles": pp.tri_count(final),
                            "uv": uv_facts, "degenerate_removed": degenerate, "cards": card_stats}
        pp.export_glb(final, os.path.join(jobs["out"], b["name"] + ".glb"))
        print("BUSH_BUILT", b["name"], json.dumps(facts[b["name"]]), flush=True)
    json.dump(facts, open(jobs["facts"], "w"), indent=1)


def main():
    pp.refuse_mirror()
    cfg = json.load(open(pp.CONFIG, encoding="utf-8"))
    recipes = []
    for b in cfg["bushes"]:
        r = dict(b, _recipe_sha256=pp.recipe_sha(b))
        r["sources"] = sorted({f"polyhaven:{p['source']}" for p in b["parts"]})
        r["objects"] = [p["object"] for p in b["parts"]]
        r["_alpha_images"] = {}
        r["alpha"] = []
        for src in sorted({p["source"] for p in b["parts"]}):
            g = json.load(open(os.path.join(pp.SRC, "raw", "polyhaven", src, f"{src}_1k.gltf"), encoding="utf-8"))
            mats = [m["name"] for m in g["materials"]]
            imgs = pp.pack_alpha(src, {"alpha": mats})
            r["_alpha_images"].update(imgs)
            r["alpha"] += mats
        r["_texel_grids"] = {}
        for p in b["parts"]:
            if p.get("wood_filter"):
                g = json.load(open(os.path.join(pp.SRC, "raw", "polyhaven", p["source"], f"{p['source']}_1k.gltf"), encoding="utf-8"))
                r["_texel_grids"].update(pp.texel_grids(r["_alpha_images"], [m["name"] for m in g["materials"]]))
        recipes.append(r)
    rep, bad = pp.run_driver(recipes, os.path.abspath(__file__))
    for m in rep["meshes"]:
        if m["script"] == os.path.basename(__file__) and m["sources"] != m["drawn_sources"]:
            bad.append(f"{m['name']}: sources {m['sources']} != parts drawn {m['drawn_sources']}")
    if bad:
        print("SCATTER_PREP FAIL " + "; ".join(bad))
        return 1
    n = len([m for m in rep["meshes"] if m["script"] == os.path.basename(__file__)])
    print(f"SCATTER_PREP OK meshes={n} (make_bush; report rows {len(rep['meshes'])})")
    return 0


if __name__ == "__main__":
    if pp.inside_blender():
        blender_main(sys.argv[sys.argv.index("--") + 1:])
    else:
        sys.exit(main())
