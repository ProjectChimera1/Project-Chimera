# -*- coding: utf-8 -*-
"""Prove the three QA gates can FAIL on the thing this epic changes — in both directions.

Why this exists
---------------
A gate that has never been observed failing is not a gate; it is a line of code that returns PASS.
Every defect this epic found in the QA layer was of exactly that shape: L1's trimesh ImportError
branch warned and passed, L2's renderer could not display an albedo at all, and L3 never looked at
a material. All three would have reported a clean run on a completely untextured roster.

So each gate is exercised against a KNOWN-GOOD artifact (must pass) and a KNOWN-BAD one (must fail,
by name, with a non-zero exit). A gate that passes both is broken, and this script says so.

Run (in the asset-gen venv):
    python prove_gates.py --good <textured.glb> --bad <untextured.glb> [--godot <godot.exe>]

Exit 0 = every gate discriminated correctly.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROFILE = os.path.join(HERE, "..", "..", "config", "engine_profiles", "godot_chimera.json")
GATE = os.path.join(HERE, "trimesh_gate.py")
RENDER = os.path.join(HERE, "blender_qa_render.py")
SHEET = os.path.join(HERE, "contact_sheet.py")
INGEST = os.path.join(HERE, "godot_ingest_check.gd")
GODOT_PROJECT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", "godot"))
BLENDER = os.environ.get("CHIMERA_BLENDER", r"D:\tools\blender\blender-4.5.10-windows-x64\blender.exe")

results: list[tuple[str, bool, str]] = []


def record(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (("   " + detail) if detail else ""), flush=True)
    return ok


def run(cmd, timeout=600):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


# ── L1 ───────────────────────────────────────────────────────────────────────

def l1(glb, kind, require_textured):
    cmd = [sys.executable, GATE, glb, "--kind", kind, "--profile", PROFILE]
    if require_textured:
        cmd.append("--require-textured")
    r = run(cmd, timeout=300)
    line = next((l for l in r.stdout.splitlines() if l.startswith("GATE_JSON ")), None)
    data = json.loads(line[len("GATE_JSON "):]) if line else {"verdict": "FAIL", "fails": ["no output"]}
    return r.returncode, data


def make_grey_factor_copy(src, dst):
    """Rewrite a GLB's baseColorFactor to 0.8 grey — the exact defect the white-factor check exists
    to catch, and the value every shipped asset carries."""
    data = open(src, "rb").read()
    off, chunks = 12, []
    while off < len(data):
        clen = struct.unpack_from("<I", data, off)[0]
        ctype = data[off + 4:off + 8]
        chunks.append((ctype, data[off + 8:off + 8 + clen]))
        off += 8 + clen
    out = bytearray()
    for ctype, cdata in chunks:
        if ctype == b"JSON":
            gj = json.loads(cdata.decode("utf-8"))
            for m in gj.get("materials", []):
                m.setdefault("pbrMetallicRoughness", {})["baseColorFactor"] = [0.8, 0.8, 0.8, 1.0]
            cdata = json.dumps(gj, separators=(",", ":")).encode("utf-8")
            cdata += b" " * ((4 - len(cdata) % 4) % 4)
        else:
            cdata = cdata + b"\x00" * ((4 - len(cdata) % 4) % 4)
        out += struct.pack("<I", len(cdata)) + ctype + cdata
    header = b"glTF" + struct.pack("<II", 2, 12 + len(out))
    open(dst, "wb").write(header + bytes(out))
    return dst


# ── L2 ───────────────────────────────────────────────────────────────────────

def l2(glb, prefix):
    run([BLENDER, "-b", "-P", RENDER, "--", glb, prefix, "320"], timeout=900)
    r = run([sys.executable, SHEET, prefix, "--no-gate"], timeout=300)
    line = next((l for l in r.stdout.splitlines() if l.startswith("SHEET_JSON ")), None)
    if not line:
        return None
    return json.loads(line[len("SHEET_JSON "):])["assets"][0]


# ── L3 ───────────────────────────────────────────────────────────────────────

def l3(godot, glb, require_textured):
    if not godot or not os.path.exists(godot):
        return None, "godot binary not available"
    rel = os.path.relpath(INGEST, GODOT_PROJECT).replace("\\", "/")
    cmd = [godot, "--headless", "--path", GODOT_PROJECT, "-s", "res://" + rel, "--", glb]
    if require_textured:
        cmd.append("--require-textured")
    r = run(cmd, timeout=600)
    blob = (r.stdout or "") + (r.stderr or "")
    return r.returncode, blob


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--good", required=True, help="a TEXTURED glb that must pass every gate")
    ap.add_argument("--bad", required=True, help="an UNTEXTURED glb that must fail every gate")
    ap.add_argument("--kind", default="unit")
    ap.add_argument("--godot", default=r"C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64.exe")
    ap.add_argument("--work", default=r"D:\tools\asset-gen-work\gateproof")
    args = ap.parse_args()
    os.makedirs(args.work, exist_ok=True)

    print("L1 — numeric gate")
    rc, d = l1(args.good, args.kind, True)
    record("L1 GREEN: textured asset passes --require-textured", rc == 0 and d["verdict"] == "PASS",
           f"exit={rc} fails={d['fails']}")
    rc, d = l1(args.bad, args.kind, True)
    named = any("UV" in f or "texture" in f or "image" in f for f in d["fails"])
    record("L1 RED: untextured asset fails --require-textured, by name",
           rc != 0 and d["verdict"] == "FAIL" and named, f"exit={rc} fails={d['fails']}")
    rc, d = l1(args.bad, args.kind, False)
    record("L1 CONTROL: same untextured asset PASSES without the flag (flag is what discriminates)",
           rc == 0 and d["verdict"] == "PASS", f"exit={rc}")

    grey = make_grey_factor_copy(args.good, os.path.join(args.work, "grey_factor.glb"))
    rc, d = l1(grey, args.kind, True)
    named = any("baseColorFactor" in f for f in d["fails"])
    record("L1 RED: non-white baseColorFactor fails by name", rc != 0 and named, f"fails={d['fails']}")

    print("\nL2 — contact sheet")
    g = l2(args.good, os.path.join(args.work, "good"))
    b = l2(args.bad, os.path.join(args.work, "bad"))
    if g and b:
        record("L2 GREEN: textured asset reads as colour", bool(g["colorful"]),
               f"saturation={g['mean_saturation']} floor=0.06")
        record("L2 RED: untextured asset reads as grey", not b["colorful"],
               f"saturation={b['mean_saturation']}")
        record("L2 DISCRIMINATES: textured saturation clearly exceeds untextured",
               g["mean_saturation"] > b["mean_saturation"] * 2.5,
               f"{b['mean_saturation']} -> {g['mean_saturation']}")
    else:
        record("L2 ran", False, "no sheet json")

    print("\nL3 — in-engine ingest")
    rc, blob = l3(args.godot, args.good, True)
    if rc is None:
        record("L3 skipped", False, blob)
    else:
        record("L3 GREEN: textured asset passes --require-textured",
               rc == 0 and "INGEST_OK" in blob, f"exit={rc}")
        rc2, blob2 = l3(args.godot, args.bad, True)
        record("L3 RED: untextured asset fails, by name",
               rc2 != 0 and ("no_albedo_texture" in blob2 or "no_uvs" in blob2),
               f"exit={rc2} " + next((l for l in blob2.splitlines() if "INGEST_FAIL" in l), "")[:150])
        rc3, blob3 = l3(args.godot, args.bad, False)
        record("L3 CONTROL: same untextured asset PASSES without the flag",
               rc3 == 0 and "INGEST_OK" in blob3, f"exit={rc3}")

    failed = [n for n, ok, _ in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} gate checks discriminated correctly")
    if failed:
        print("NOT DISCRIMINATING: " + "; ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
