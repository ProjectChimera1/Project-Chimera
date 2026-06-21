# -*- coding: utf-8 -*-
"""
Batch asset generation orchestrator for Project Chimera.
Run with the venv python:
    D:\\tools\\asset-gen-venv\\Scripts\\python.exe run_manifest.py [--only <id>] [--faction alpha|beta] [--limit N]

Per asset: SDXL concept -> Hunyuan3D shape -> Blender normalize/decimate -> numeric QA gate ->
(re-roll new seed up to max_rerolls on FAIL) -> render thumbnail -> land at dest. Idempotent
(skips assets already produced with a matching content hash). One asset failing never aborts the batch.
Writes a summary + per-asset .meta.json + thumbnails for human/agent review.
"""
import sys, os, json, shutil, subprocess, hashlib, time, argparse
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from backends.comfy_client import ComfyClient
from backends import workflows as W

BLENDER = r"D:\tools\blender\blender-4.5.10-windows-x64\blender.exe"
QA = os.path.join(HERE, "qa")
PIPELINE = os.path.join(QA, "blender_pipeline.py")
GATE = os.path.join(QA, "trimesh_gate.py")
RENDER = os.path.join(QA, "blender_qa_render.py")
PROFILE = os.path.join(HERE, "..", "config", "engine_profiles", "godot_chimera.json")
MANIFEST = os.path.join(HERE, "..", "config", "chimera_assets.json")
WORK = r"D:\tools\asset-gen-work"
THUMBS = os.path.join(WORK, "thumbs")
PIPELINE_VERSION = "v1-remesh240-6k"

os.makedirs(WORK, exist_ok=True)
os.makedirs(THUMBS, exist_ok=True)

def sh(cmd, timeout=1200):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)

def content_hash(asset):
    h = hashlib.sha1()
    h.update((asset["id"] + asset["faction"] + asset["prompt"] + PIPELINE_VERSION).encode("utf-8"))
    return h.hexdigest()[:16]

def gate(glb, tri_kind):
    r = sh([sys.executable, GATE, glb, "--kind", tri_kind, "--profile", PROFILE], timeout=180)
    line = next((l for l in r.stdout.splitlines() if l.startswith("GATE_JSON ")), None)
    if not line:
        return {"verdict": "FAIL", "fails": ["gate produced no output: " + (r.stderr[-300:] or "")], "warns": [], "metrics": {}}
    return json.loads(line[len("GATE_JSON "):])

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only"); ap.add_argument("--faction"); ap.add_argument("--limit", type=int)
    args = ap.parse_args()

    with open(MANIFEST, "r", encoding="utf-8") as f:
        man = json.load(f)
    assets = man["assets"]
    if args.faction: assets = [a for a in assets if a["faction"] == args.faction]
    if args.only:    assets = [a for a in assets if a["id"] == args.only]
    if args.limit:   assets = assets[:args.limit]

    comfy = ComfyClient(comfy_root=man["comfy_root"])
    if not comfy.ping():
        print("FATAL: ComfyUI not reachable at 127.0.0.1:8188"); sys.exit(1)

    proot = man["project_root"]
    tri_target = man["tri_target"]
    summary = []
    print(f"=== BATCH START: {len(assets)} assets ===", flush=True)

    for i, a in enumerate(assets, 1):
        tag = f"{a['faction']}/{a['id']}"
        dest = os.path.join(proot, a["dest"].replace("/", os.sep))
        meta_path = dest + ".meta.json"
        chash = content_hash(a)
        # idempotent skip
        if os.path.exists(dest) and os.path.exists(meta_path):
            try:
                if json.load(open(meta_path)).get("content_hash") == chash:
                    print(f"[{i}/{len(assets)}] SKIP {tag} (cached)", flush=True);
                    summary.append({"asset": tag, "status": "cached"}); continue
            except Exception: pass

        print(f"[{i}/{len(assets)}] GEN {tag} -> {a['mesh_file']}", flush=True)
        tri_kind = a["tri_kind"]
        target = tri_target[tri_kind]
        prefix = a["prefix"]
        w, hgt = a["concept_size"]
        status = "fail"; gate_res = None
        for attempt in range(man["max_rerolls"] + 1):
            seed = man["hunyuan_seed_base"] + attempt * 1000
            try:
                # 1. concept
                cwf = W.sdxl_concept(a["prompt"], a["negative"], seed=seed,
                                     steps=man["concept_steps"], cfg=man["concept_cfg"],
                                     width=w, height=hgt, out_prefix=f"concept/{a['faction']}_{a['id']}")
                ch = comfy.wait(comfy.queue(cwf), timeout=300)
                imgs = [f for f in comfy.output_files(ch) if f["filename"].lower().endswith(".png")]
                if not imgs: raise RuntimeError("no concept image")
                csrc = os.path.join(man["comfy_root"], imgs[0].get("type", "output"), imgs[0]["subfolder"], imgs[0]["filename"])
                in_name = f"cc_{a['faction']}_{a['id']}.png"
                shutil.copy(csrc, os.path.join(man["comfy_root"], "input", in_name))
                # 2. shape
                swf = W.hunyuan3d_shape(in_name, seed=seed, out_prefix=f"mesh/{a['faction']}_{a['id']}")
                shf = comfy.wait(comfy.queue(swf), timeout=900)
                glbs = [f for f in comfy.output_files(shf) if f["filename"].lower().endswith(".glb")]
                if not glbs: raise RuntimeError("no glb from hunyuan")
                gsrc = os.path.join(man["comfy_root"], glbs[0].get("type", "output"), glbs[0]["subfolder"], glbs[0]["filename"])
                raw = os.path.join(WORK, f"{a['faction']}_{a['id']}_raw.glb")
                shutil.copy(gsrc, raw)
                # 3. blender normalize/decimate
                out = os.path.join(WORK, f"{a['faction']}_{a['id']}.glb")
                pr = sh([BLENDER, "-b", "-P", PIPELINE, "--", raw, out, str(target), tri_kind], timeout=600)
                if not os.path.exists(out): raise RuntimeError("pipeline produced no glb: " + pr.stderr[-300:])
                # 4. gate
                gate_res = gate(out, tri_kind)
                if gate_res["verdict"] == "PASS":
                    status = "pass"; break
                print(f"    attempt {attempt+1} gate FAIL: {gate_res['fails']}", flush=True)
            except Exception as e:
                print(f"    attempt {attempt+1} error: {e}", flush=True)
        # land or record failure
        if status == "pass":
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy(out, dest)
            thumb = os.path.join(THUMBS, f"{a['faction']}_{a['id']}")
            try:
                sh([BLENDER, "-b", "-P", RENDER, "--", out, thumb, "384"], timeout=300)
            except Exception: pass
            json.dump({"content_hash": chash, "asset": tag, "mesh_file": a["mesh_file"],
                       "tris": gate_res["metrics"].get("tris"), "warns": gate_res["warns"],
                       "pipeline": PIPELINE_VERSION}, open(meta_path, "w"), indent=2)
            print(f"    PASS {gate_res['metrics'].get('tris')} tris -> landed {a['dest']}", flush=True)
            summary.append({"asset": tag, "status": "pass", "tris": gate_res["metrics"].get("tris"),
                            "warns": gate_res["warns"], "dest": a["dest"]})
        else:
            print(f"    GIVE UP {tag} after {man['max_rerolls']+1} attempts (box placeholder stays)", flush=True)
            summary.append({"asset": tag, "status": "fail", "gate": gate_res})

    sp = os.path.join(WORK, "batch_summary.json")
    json.dump(summary, open(sp, "w"), indent=2)
    npass = sum(1 for s in summary if s["status"] in ("pass", "cached"))
    print(f"=== BATCH DONE: {npass}/{len(assets)} ok. summary -> {sp}; thumbs -> {THUMBS} ===", flush=True)

if __name__ == "__main__":
    main()
