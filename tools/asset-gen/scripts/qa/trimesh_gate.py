# -*- coding: utf-8 -*-
"""
Numeric QA gate for a generated .glb against an engine profile.
Run (in the asset-gen venv):  python trimesh_gate.py <asset.glb> --kind unit --profile <godot_chimera.json>

Checks (fail-cheapest-first):
  - NO forbidden GLB compression extensions (Draco/meshopt/quantization)  [HARD FAIL — Godot 4.6.2 err 43]
  - tri count vs profile budget (target/warn/fail)
  - material count <= profile max_materials  (authoritative from the GLB JSON)
  - watertight / winding-consistent
  - origin at feet: min_z within tolerance, X/Y centered within tolerance
  - no NaN/Inf vertices
Emits one JSON verdict line: GATE_JSON {...}.  Exit 0 = PASS (warns ok), 1 = FAIL.
"""
import sys, json, struct, argparse

def read_glb_json(path):
    """Parse the JSON chunk of a binary glTF without external deps."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"glTF":
        raise ValueError("not a binary glTF (.glb)")
    # header: magic(4) version(4) length(4); then chunks: len(4) type(4) data
    off = 12
    while off < len(data):
        clen = struct.unpack_from("<I", data, off)[0]
        ctype = data[off + 4:off + 8]
        cdata = data[off + 8:off + 8 + clen]
        if ctype == b"JSON":
            return json.loads(cdata.decode("utf-8"))
        off += 8 + clen
    raise ValueError("no JSON chunk in glb")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("glb")
    ap.add_argument("--kind", default="unit")
    ap.add_argument("--profile", required=True)
    ap.add_argument("--require-textured", action="store_true",
                    help="hard-fail unless the asset carries UVs, an albedo texture and a white base-colour factor")
    args = ap.parse_args()

    with open(args.profile, "r", encoding="utf-8") as f:
        prof = json.load(f)
    mp = prof["mesh"]
    budget = mp["tri_budget"].get(args.kind, mp["tri_budget"]["unit"])

    fails, warns, metrics = [], [], {}

    # ---- 1. compression extensions (hard) ----
    gj = read_glb_json(args.glb)
    used = set(gj.get("extensionsUsed", [])) | set(gj.get("extensionsRequired", []))
    forbidden = set(mp["compression_forbidden"]) & used
    metrics["extensions"] = sorted(used)
    if forbidden:
        fails.append(f"FORBIDDEN compression extension(s): {sorted(forbidden)} (Godot 4.6.2 rejects -> box placeholder)")

    # ---- material count (authoritative from GLB json) ----
    nmat = len(gj.get("materials", []))
    nprim = sum(len(m.get("primitives", [])) for m in gj.get("meshes", []))
    metrics["materials"] = nmat
    metrics["primitives"] = nprim
    metrics["node_translations"] = [n.get("translation") for n in gj.get("nodes", []) if n.get("translation")]
    if nmat > mp["max_materials"]:
        fails.append(f"materials {nmat} > max {mp['max_materials']}")

    # ---- texture contract (only enforced under --require-textured) ----
    # Read straight out of the GLB JSON rather than through trimesh: trimesh normalises materials
    # into its own PBRMaterial and a missing baseColorFactor comes back as an assumed white, which
    # would mask exactly the 0.8-grey defect this check exists to catch.
    prims = [p for m in gj.get("meshes", []) for p in m.get("primitives", [])]
    with_uv = [p for p in prims if any(k.startswith("TEXCOORD_") for k in p.get("attributes", {}))]
    metrics["primitives_with_uv"] = len(with_uv)
    metrics["has_uvs"] = bool(prims) and len(with_uv) == len(prims)

    mats = gj.get("materials", [])
    pbr0 = (mats[0].get("pbrMetallicRoughness", {}) if mats else {})
    bct = pbr0.get("baseColorTexture")
    bcf = pbr0.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])   # glTF default IS white when absent
    metrics["base_color_factor"] = bcf
    metrics["has_albedo_texture"] = bct is not None
    metrics["images"] = len(gj.get("images", []))
    white = all(abs(float(c) - 1.0) <= 1e-3 for c in bcf[:3])
    metrics["base_color_factor_white"] = white

    if args.require_textured:
        if not prims:
            fails.append("no primitives in glb")
        elif not metrics["has_uvs"]:
            fails.append(f"missing UVs: {len(with_uv)}/{len(prims)} primitives carry TEXCOORD_0")
        if not metrics["has_albedo_texture"]:
            fails.append("no baseColorTexture on material 0 (asset is untextured)")
        if not metrics["images"]:
            fails.append("no images embedded in the glb")
        if not white:
            fails.append(f"baseColorFactor {bcf[:3]} is not white — it multiplies the albedo, "
                         f"so the texture would render dark and read as a lighting bug")

    # ---- geometry metrics via trimesh ----
    try:
        import trimesh, numpy as np
        m = trimesh.load(args.glb, force="mesh")
        tris = int(len(m.faces))
        metrics["tris"] = tris
        metrics["watertight"] = bool(m.is_watertight)
        metrics["winding_consistent"] = bool(m.is_winding_consistent)
        # NOTE: a .glb is Y-UP. Vertical axis = Y (index 1); ground plane = X (0) and Z (2).
        bounds = m.bounds  # [[minx,miny,minz],[maxx,maxy,maxz]]
        min_y = float(bounds[0][1])
        cx = float((bounds[0][0] + bounds[1][0]) / 2.0)
        cz = float((bounds[0][2] + bounds[1][2]) / 2.0)
        ext = m.extents
        metrics["bbox_min"] = [float(x) for x in bounds[0]]
        metrics["bbox_max"] = [float(x) for x in bounds[1]]
        metrics["min_y"] = min_y
        metrics["center_xz"] = [cx, cz]
        metrics["has_nan"] = bool(np.isnan(m.vertices).any() or np.isinf(m.vertices).any())

        # tri budget
        if tris > budget["fail"]:
            fails.append(f"tris {tris} > FAIL cap {budget['fail']}")
        elif tris > budget["target"]:
            warns.append(f"tris {tris} > target {budget['target']} (auto-decimate)")

        # origin at feet (Y-up): feet on the ground plane -> min_y ~ 0, centered on X/Z
        lo, hi = mp["origin_min_z_tolerance"]
        if not (lo <= min_y <= hi):
            warns.append(f"min_y {min_y:.3f} outside feet tolerance {mp['origin_min_z_tolerance']} (auto-recenter)")
        xz_tol = mp["xy_center_tolerance_frac"]
        max_ext_ground = max(float(ext[0]), float(ext[2]), 1e-6)
        if abs(cx) > xz_tol * max_ext_ground or abs(cz) > xz_tol * max_ext_ground:
            warns.append(f"not XZ-centered (cx={cx:.3f}, cz={cz:.3f})")

        if mp.get("require_no_nan") and metrics["has_nan"]:
            fails.append("mesh has NaN/Inf vertices")
        if not metrics["winding_consistent"]:
            warns.append("winding inconsistent (fix_normals + retest)")
    except ImportError as e:
        # HARD failure, not a warning. As a warning this silently skipped every geometry check --
        # tri budget, NaN, winding, origin -- and still printed PASS, so a broken venv read as a
        # clean gate. A gate that cannot run its checks has not passed; it has not run.
        fails.append(f"trimesh/numpy not importable — geometry checks could not run: {e}")
    except Exception as e:
        fails.append(f"trimesh load error: {e}")

    verdict = "PASS" if not fails else "FAIL"
    print("GATE_JSON " + json.dumps({"verdict": verdict, "kind": args.kind, "fails": fails, "warns": warns, "metrics": metrics}))
    sys.exit(0 if not fails else 1)

if __name__ == "__main__":
    main()
