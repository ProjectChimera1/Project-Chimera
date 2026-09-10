# -*- coding: utf-8 -*-
"""Stage matrix: for every asset in the manifest, what each pipeline stage actually produced.

Every cell cites the ARTEFACT ON DISK that its stage wrote, not the fact that the stage was
invoked. "The batch reported success" is exactly the claim this epic found to be worthless — a run
that skipped all 24 assets on a stale cache key printed success too.

Cell states:
    ok        the artefact exists and satisfies the stage's contract
    MISSING   the stage should have produced it and did not
    excluded  deliberately not run for this asset, with a reason  (a decision, not a gap)

Run (in the asset-gen venv):
    python stage_matrix.py [--out matrix.md] [--json matrix.json]
"""

from __future__ import annotations

import argparse
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CFG = os.path.join(HERE, "..", "..", "config")
MANIFEST = os.path.join(CFG, "chimera_assets.json")
WORK = r"D:\tools\asset-gen-work"
CLEANED = os.path.join(WORK, "clean")
THUMBS = os.path.join(WORK, "thumbs")

# Assets deliberately excluded from a stage, with the reason. An excluded cell must read as a
# decision someone made, never as a hole someone missed.
EXCLUSIONS: dict[tuple[str, str], str] = {}


def glb_facts(path):
    """Read UVs / albedo / base-colour factor straight out of the GLB JSON chunk."""
    if not os.path.exists(path):
        return None
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"glTF":
        return None
    off = 12
    gj = None
    while off < len(data):
        clen = struct.unpack_from("<I", data, off)[0]
        if data[off + 4:off + 8] == b"JSON":
            gj = json.loads(data[off + 8:off + 8 + clen].decode("utf-8"))
            break
        off += 8 + clen
    if gj is None:
        return None
    prims = [p for m in gj.get("meshes", []) for p in m.get("primitives", [])]
    with_uv = sum(1 for p in prims if any(k.startswith("TEXCOORD_") for k in p.get("attributes", {})))
    mats = gj.get("materials", [])
    pbr = mats[0].get("pbrMetallicRoughness", {}) if mats else {}
    bcf = pbr.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
    return {
        "bytes": os.path.getsize(path),
        "prims": len(prims),
        "uvs": bool(prims) and with_uv == len(prims),
        "albedo": pbr.get("baseColorTexture") is not None,
        "images": len(gj.get("images", [])),
        "bcf_white": all(abs(float(c) - 1.0) <= 1e-3 for c in bcf[:3]),
        "bcf": [round(float(c), 3) for c in bcf[:3]],
    }


def cell(state, note=""):
    return {"state": state, "note": note}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(WORK, "stage_matrix.md"))
    ap.add_argument("--json", dest="js", default=os.path.join(WORK, "stage_matrix.json"))
    args = ap.parse_args()

    man = json.load(open(MANIFEST, encoding="utf-8"))
    proot = man["project_root"]
    rows = []

    for a in man["assets"]:
        key = f"{a['faction']}/{a['id']}"
        raw = os.path.join(WORK, f"{a['faction']}_{a['id']}_raw.glb")
        plate = os.path.join(man["comfy_root"], "input", f"cc_{a['faction']}_{a['id']}.png")
        rgba = os.path.join(CLEANED, f"{a['faction']}_{a['id']}_rgba.png")
        baked = os.path.join(WORK, f"{a['faction']}_{a['id']}.glb")
        thumb = os.path.join(THUMBS, f"{a['faction']}_{a['id']}_front.png")
        dest = os.path.join(proot, a["dest"].replace("/", os.sep))

        r = {"asset": key, "mesh_file": a["mesh_file"], "kind": a["tri_kind"]}

        r["concept"] = (cell("ok", os.path.basename(plate)) if os.path.exists(plate)
                        else cell("MISSING", "no concept plate"))
        r["clean"] = (cell("ok", os.path.basename(rgba)) if os.path.exists(rgba)
                      else cell("MISSING", "clean_concept did not write an RGBA cutout"))
        r["shape"] = (cell("ok", f"{os.path.getsize(raw)/1e6:.1f} MB raw") if os.path.exists(raw)
                      else cell("MISSING", "no high-poly on disk"))

        f = glb_facts(baked)
        if f is None:
            r["bake"] = cell("MISSING", "no baked glb")
        else:
            bad = []
            if not f["uvs"]:
                bad.append("no UVs")
            if not f["albedo"]:
                bad.append("no albedo")
            if not f["bcf_white"]:
                bad.append(f"baseColorFactor {f['bcf']}")
            r["bake"] = (cell("ok", f"{f['bytes']/1e6:.1f} MB, {f['images']} img, UVs, white factor")
                         if not bad else cell("MISSING", "; ".join(bad)))

        r["l2_sheet"] = (cell("ok", os.path.basename(thumb)) if os.path.exists(thumb)
                         else cell("MISSING", "no contact sheet rendered"))

        d = glb_facts(dest)
        if (key, "land") in EXCLUSIONS:
            r["landed"] = cell("excluded", EXCLUSIONS[(key, "land")])
        elif d is None:
            r["landed"] = cell("MISSING", "not in godot/assets")
        else:
            r["landed"] = (cell("ok", f"{d['bytes']/1e6:.1f} MB, textured") if d["albedo"]
                           else cell("MISSING", "landed but UNTEXTURED"))

        rows.append(r)

    stages = ["concept", "clean", "shape", "bake", "l2_sheet", "landed"]
    counts = {s: {"ok": 0, "MISSING": 0, "excluded": 0} for s in stages}
    for r in rows:
        for s in stages:
            counts[s][r[s]["state"]] += 1

    sym = {"ok": "ok", "MISSING": "**MISSING**", "excluded": "_excluded_"}
    lines = [
        "# Asset pipeline — stage matrix",
        "",
        "Every cell cites the artefact its stage wrote. `_excluded_` is a decision with a reason;",
        "`**MISSING**` is a gap. A stage reporting success is not evidence and is not shown here.",
        "",
        "| asset | mesh file | concept | clean | shape | bake | L2 sheet | landed |",
        "|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        lines.append("| `{}` | `{}` | {} | {} | {} | {} | {} | {} |".format(
            r["asset"], r["mesh_file"], *[sym[r[s]["state"]] for s in stages]))
    lines += ["", "## Tally", "", "| stage | ok | missing | excluded |", "|---|---|---|---|"]
    for s in stages:
        c = counts[s]
        lines.append(f"| {s} | {c['ok']} | {c['MISSING']} | {c['excluded']} |")

    problems = [(r["asset"], s, r[s]["note"]) for r in rows for s in stages if r[s]["state"] == "MISSING"]
    if problems:
        lines += ["", "## Gaps", ""]
        for a, s, n in problems:
            lines.append(f"- `{a}` — **{s}**: {n}")

    open(args.out, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    json.dump({"rows": rows, "counts": counts}, open(args.js, "w", encoding="utf-8"), indent=2)

    print("\n".join(lines[-(len(stages) + 6):]))
    print(f"\nmatrix -> {args.out}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
