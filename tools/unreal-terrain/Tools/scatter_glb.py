"""Small glTF-binary helpers for the scatter tools (plan-c-scatter.md 3.6, S1): read and rewrite a .glb's JSON chunk, count triangles,
hash vertex positions and read bounds from the accessors. Pure Python + numpy (no Blender), so reports never depend on the exporter."""
import hashlib
import json
import struct

import numpy as np

COMP = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def read_glb(path):
    """Return (gltf_json, bin_bytes)."""
    with open(path, "rb") as f:
        data = f.read()
    magic, ver, total = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67 or ver != 2:
        raise ValueError(f"{path}: not a glTF 2 binary")
    off = 12
    js, binb = None, b""
    while off < total:
        n, kind = struct.unpack_from("<II", data, off)
        chunk = data[off + 8: off + 8 + n]
        if kind == 0x4E4F534A:
            js = json.loads(chunk.decode("utf-8"))
        elif kind == 0x004E4942:
            binb = chunk
        off += 8 + n
    return js, binb


def write_glb(path, js, binb):
    jb = json.dumps(js, separators=(",", ":"), sort_keys=True).encode("utf-8")
    jb += b" " * ((4 - len(jb) % 4) % 4)
    binb = binb + b"\x00" * ((4 - len(binb) % 4) % 4)
    total = 12 + 8 + len(jb) + (8 + len(binb) if binb else 0)
    out = struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(jb), 0x4E4F534A) + jb
    if binb:
        out += struct.pack("<II", len(binb), 0x004E4942) + binb
    with open(path, "wb") as f:
        f.write(out)
    return out


def accessor(js, binb, i):
    a = js["accessors"][i]
    v = js["bufferViews"][a["bufferView"]]
    dt = np.dtype(COMP[a["componentType"]])
    n = NCOMP[a["type"]]
    off = v.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = v.get("byteStride", 0)
    if stride and stride != dt.itemsize * n:
        rows = [np.frombuffer(binb, dtype=dt, count=n, offset=off + k * stride) for k in range(a["count"])]
        return np.array(rows)
    return np.frombuffer(binb, dtype=dt, count=a["count"] * n, offset=off).reshape(a["count"], n) if n > 1 else \
        np.frombuffer(binb, dtype=dt, count=a["count"], offset=off)


def stats(path):
    """Triangles, vertices, Z-up bounds (metres), vertex-position sha256 (float32 positions of every primitive in order), materials."""
    js, binb = read_glb(path)
    tris, verts, pos_all = 0, 0, []
    for m in js.get("meshes", []):
        for p in m["primitives"]:
            pos = accessor(js, binb, p["attributes"]["POSITION"]).astype(np.float32)
            pos_all.append(pos)
            verts += len(pos)
            tris += (js["accessors"][p["indices"]]["count"] if "indices" in p else len(pos)) // 3
    pos = np.concatenate(pos_all) if pos_all else np.zeros((0, 3), np.float32)
    zup = np.stack([pos[:, 0], -pos[:, 2], pos[:, 1]], axis=1) if len(pos) else pos  # glTF (x, y, z) -> Z-up (x, -z, y)
    return {"triangles": int(tris), "vertices": int(verts),
            "bounds_min": [round(float(x), 4) for x in zup.min(axis=0)] if len(pos) else None,
            "bounds_max": [round(float(x), 4) for x in zup.max(axis=0)] if len(pos) else None,
            "vertex_position_sha256": hashlib.sha256(np.ascontiguousarray(pos).tobytes()).hexdigest(),
            "materials": [mm.get("name") for mm in js.get("materials", [])],
            "alpha_modes": {mm.get("name"): mm.get("alphaMode", "OPAQUE") for mm in js.get("materials", [])},
            "images": len(js.get("images", []))}


def node_transforms_identity(js):
    """True when no node carries a translation/rotation/scale/matrix (the mesh data is already in its final space)."""
    for n in js.get("nodes", []):
        if any(k in n for k in ("matrix", "translation", "rotation", "scale")):
            return False
    return True


def primitives(path):
    """Per primitive of every mesh: material name, Z-up positions (metres), TEXCOORD_0 (or None), triangle indices (n, 3), the
    attribute names and the material's base-colour texCoord / KHR_texture_transform (None when absent)."""
    js, binb = read_glb(path)
    mats = js.get("materials", [])
    out = []
    for m in js.get("meshes", []):
        for p in m["primitives"]:
            pos = accessor(js, binb, p["attributes"]["POSITION"]).astype(np.float64)
            zup = np.stack([pos[:, 0], -pos[:, 2], pos[:, 1]], axis=1)
            uv = accessor(js, binb, p["attributes"]["TEXCOORD_0"]).astype(np.float64) if "TEXCOORD_0" in p["attributes"] else None
            idx = accessor(js, binb, p["indices"]).astype(np.int64).reshape(-1, 3) if "indices" in p else \
                np.arange(len(pos), dtype=np.int64).reshape(-1, 3)
            mat = mats[p["material"]] if "material" in p else {}
            texrefs = [mat.get("pbrMetallicRoughness", {}).get("baseColorTexture"), mat.get("normalTexture"),
                       mat.get("pbrMetallicRoughness", {}).get("metallicRoughnessTexture")]
            texrefs = [t for t in texrefs if t]
            out.append({"mesh": m.get("name"), "material": mat.get("name"), "pos": zup, "uv": uv, "tri": idx,
                        "attributes": sorted(p["attributes"]), "textured": bool(texrefs),
                        "texcoords": sorted({t.get("texCoord", 0) for t in texrefs}),
                        "texture_transform": any("KHR_texture_transform" in t.get("extensions", {}) for t in texrefs)})
    return out


def triangle_areas(prim):
    """Area (m^2) of each triangle of a primitive from primitives()."""
    P = prim["pos"][prim["tri"]]
    return 0.5 * np.linalg.norm(np.cross(P[:, 1] - P[:, 0], P[:, 2] - P[:, 0]), axis=1)
