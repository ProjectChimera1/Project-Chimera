# -*- coding: utf-8 -*-
"""
Batch asset generation orchestrator for Project Chimera.
Run with the venv python:
    D:\\tools\\asset-gen-venv\\Scripts\\python.exe run_manifest.py [--only <id>] [--faction alpha|beta] [--limit N]

Two routes through this script:

  GENERATE (default)   SDXL concept -> clean_concept -> Hunyuan3D shape -> mesh stage -> QA gate
                       -> (re-roll new seed up to max_rerolls on FAIL) -> thumbnail -> land.
                       Needs ComfyUI running.

  --from-raw           Skip concept and shape entirely. Read the high-poly `*_raw.glb` and the
                       concept plate `cc_*.png` already on disk, and re-run only the mesh stage
                       onward. Needs NO ComfyUI. This is the route that textures the existing
                       roster without regenerating a single mesh.

Mesh stage is selectable with --mesh-profile:

  normalize   blender_pipeline.py -- join/remesh/decimate to budget. Produces NO UVs and NO
              texture; it clears every material unconditionally. This is what produced the
              shipped roster, and it is why all 24 meshes are flat grey.
  bake        hp_to_lp_bake.py -- joins, welds, drops debris, decimates, UNWRAPS, projects the
              concept plate onto the high-poly and bakes it down to the low-poly as a real
              albedo (plus an optional tangent normal map).

ORDERING INVARIANT: the `bake` profile will NOT write into the project tree unless --land is
passed explicitly. `TeamTintPolicy.Resolve` is `hasAlbedoTexture ? requested : Flat`, so the
first textured GLB that reaches godot/assets/ flips ALL assets onto a shader branch with no code
change. Land only after the material contract is in place. The guard is in the tool rather than
in a runbook because a rule you have to remember is a rule that gets forgotten.

Idempotent: an asset whose recorded content hash still matches is skipped. See stage_identity().
One asset failing never aborts the batch. Writes a summary + per-asset .meta.json + thumbnails.
"""
import sys, os, json, shutil, subprocess, hashlib, argparse
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

QA = os.path.join(HERE, "qa")
PIPELINE = os.path.join(QA, "blender_pipeline.py")     # mesh stage: normalize (no UVs, no texture)
BAKE = os.path.join(QA, "hp_to_lp_bake.py")            # mesh stage: retopo + texture bake
CLEAN = os.path.join(QA, "clean_concept.py")           # subject isolation, between concept and shape
GATE = os.path.join(QA, "trimesh_gate.py")             # L1 numeric gate
RENDER = os.path.join(QA, "blender_qa_render.py")      # L2 contact sheet
PROFILE = os.path.join(HERE, "..", "config", "engine_profiles", "godot_chimera.json")
MANIFEST = os.path.join(HERE, "..", "config", "chimera_assets.json")
WORK = r"D:\tools\asset-gen-work"
THUMBS = os.path.join(WORK, "thumbs")
CLEANED = os.path.join(WORK, "clean")

# The Blender the pipeline is pinned to. Override with CHIMERA_BLENDER to re-bake an old roster
# on the exact build that produced it. The version STRING is folded into the stage identity
# below, so changing Blender correctly invalidates every cached asset instead of silently
# reporting 24 cache hits and success.
BLENDER = os.environ.get(
    "CHIMERA_BLENDER",
    r"D:\tools\blender\blender-4.5.10-windows-x64\blender.exe",
)


def sh(cmd, timeout=1200):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


def blender_version(blender=BLENDER):
    """First line of `blender --version`, e.g. 'Blender 4.5.10 LTS'.

    Folded into the stage identity: Blender IS a pipeline stage, and by far the largest one.
    A Blender upgrade changes the output bytes, so it must invalidate the cache exactly the way
    editing a stage script does.
    """
    if not os.path.exists(blender):
        raise SystemExit("FATAL: Blender not found at %s (set CHIMERA_BLENDER)" % blender)
    r = sh([blender, "--version"], timeout=120)
    for line in (r.stdout or "").splitlines():
        if line.strip().startswith("Blender "):
            return line.strip()
    raise SystemExit("FATAL: could not read a version from %s" % blender)


def stage_identity(scripts, blender_id):
    """sha1 over the BYTES of every stage script this run shells out to, plus the Blender build.

    This REPLACES a hand-typed PIPELINE_VERSION constant, and the constant was the bug. It was
    the only stage-identity term in content_hash(), so swapping the mesh stage -- the entire point
    of this epic -- left every asset hash byte-identical. The batch would then print
    `SKIP (cached)` for all 24 assets and exit reporting success, having done nothing.

    Pure function of its arguments, so it can be exercised in a test without a live batch.
    Each script contributes its basename as well as its bytes, so swapping which scripts are in
    the set changes the identity even in the pathological case of two identical files.
    """
    h = hashlib.sha1()
    h.update(blender_id.encode("utf-8"))
    for path in sorted(scripts, key=os.path.basename):
        h.update(b"\x00")
        h.update(os.path.basename(path).encode("utf-8"))
        with open(path, "rb") as f:
            h.update(f.read())
    return h.hexdigest()[:16]


def content_hash(asset, stage_id):
    """Identity of a produced asset: what it is, plus what produced it.

    `stage_id` is passed in rather than read from a global so this stays a pure function of its
    arguments -- testable without a batch, and impossible to accidentally evaluate against a
    stale module-level constant.
    """
    h = hashlib.sha1()
    h.update((asset["id"] + asset["faction"] + asset["prompt"] + stage_id).encode("utf-8"))
    return h.hexdigest()[:16]


def gate(glb, tri_kind, require_textured=False):
    cmd = [sys.executable, GATE, glb, "--kind", tri_kind, "--profile", PROFILE]
    if require_textured:
        cmd.append("--require-textured")
    r = sh(cmd, timeout=180)
    line = next((l for l in r.stdout.splitlines() if l.startswith("GATE_JSON ")), None)
    if not line:
        return {"verdict": "FAIL", "fails": ["gate produced no output: " + (r.stderr[-300:] or "")], "warns": [], "metrics": {}}
    return json.loads(line[len("GATE_JSON "):])


def clean_concept(src_png, faction, aid):
    """Isolate the subject in a concept plate. Returns (rgba_path, white_path, info).

    Emits BOTH outputs deliberately:
      * the RGBA cutout conditions the shape pass and is what the bake projects, because it
        carries the matte the projection needs to tell subject from backdrop;
      * the white composite exists because ComfyUI's LoadImage hands CLIPVisionEncode the RGB
        and DROPS the alpha -- so an RGBA file still carrying the old backdrop in its colour
        channels would silently condition the shape pass on the very background this stage
        removes.
    """
    os.makedirs(CLEANED, exist_ok=True)
    rgba = os.path.join(CLEANED, f"{faction}_{aid}_rgba.png")
    white = os.path.join(CLEANED, f"{faction}_{aid}_white.png")
    r = sh([sys.executable, CLEAN, src_png, rgba, "--white", white, "--square"], timeout=600)
    line = next((l for l in r.stdout.splitlines() if l.startswith("CLEAN_JSON ")), None)
    if not line or not os.path.exists(rgba):
        raise RuntimeError("clean_concept failed: " + ((r.stderr or "")[-300:] or "no output"))
    return rgba, white, json.loads(line[len("CLEAN_JSON "):])


def mesh_stage(profile, raw, out, target, tri_kind, project_front=None, tex=0, timeout=1800):
    """Run the selected mesh stage. Returns (info_dict, stderr_tail).

    Both stages are kept reachable: `normalize` is the path that produced the shipped roster and
    must stay runnable to reproduce it, `bake` is the path that produces a textured asset.
    """
    if profile == "normalize":
        r = sh([BLENDER, "-b", "-P", PIPELINE, "--", raw, out, str(target), tri_kind], timeout=timeout)
        return {"stage": "normalize"}, (r.stderr or "")[-600:]

    cmd = [BLENDER, "-b", "-P", BAKE, "--",
           "--in", raw, "--out", out, "--profile", PROFILE, "--kind", tri_kind]
    if tex:
        cmd += ["--tex", str(tex)]
    if project_front:
        cmd += ["--project-front", project_front]
    r = sh(cmd, timeout=timeout)
    line = next((l for l in (r.stdout or "").splitlines() if l.startswith("BAKE_JSON ")), None)
    info = json.loads(line[len("BAKE_JSON "):]) if line else {"stage": "bake", "ok": False}
    return info, (r.stderr or "")[-600:]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", help="asset id (add --faction when the id exists in both factions)")
    ap.add_argument("--faction")
    ap.add_argument("--limit", type=int)
    ap.add_argument("--mesh-profile", choices=["normalize", "bake"], default="normalize",
                    help="normalize = blender_pipeline.py (no UVs, no texture); bake = hp_to_lp_bake.py")
    ap.add_argument("--from-raw", action="store_true",
                    help="skip concept+shape; re-run the mesh stage from the *_raw.glb already on disk (no ComfyUI)")
    ap.add_argument("--land", action="store_true",
                    help="copy results into the project tree. REQUIRED for --mesh-profile bake (ordering invariant)")
    ap.add_argument("--tex", type=int, default=0, help="bake texture size; default = profile texture.min_dim")
    ap.add_argument("--force", action="store_true", help="ignore the cached content hash")
    ap.add_argument("--print-stage-id", action="store_true", help="print the stage identity and exit")
    args = ap.parse_args()

    os.makedirs(WORK, exist_ok=True)
    os.makedirs(THUMBS, exist_ok=True)

    # The stage set is exactly the scripts THIS run shells out to -- so selecting a different
    # mesh profile is itself a change of identity, which is the whole point.
    stages = [CLEAN, GATE, RENDER, BAKE if args.mesh_profile == "bake" else PIPELINE]
    bver = blender_version()
    stage_id = stage_identity(stages, bver)

    if args.print_stage_id:
        print(json.dumps({"stage_id": stage_id, "blender": bver,
                          "stages": [os.path.basename(s) for s in stages],
                          "mesh_profile": args.mesh_profile}, indent=2))
        return

    with open(MANIFEST, "r", encoding="utf-8") as f:
        man = json.load(f)
    assets = man["assets"]
    if args.faction: assets = [a for a in assets if a["faction"] == args.faction]
    if args.only:    assets = [a for a in assets if a["id"] == args.only]
    if args.limit:   assets = assets[:args.limit]
    if not assets:
        print("FATAL: no assets selected"); sys.exit(1)

    if args.mesh_profile == "bake" and not args.land:
        print("NOTE: --mesh-profile bake without --land: results stay in "
              f"{WORK} and nothing is written into the project tree (ordering invariant).", flush=True)

    comfy = None
    if not args.from_raw:
        from backends.comfy_client import ComfyClient
        from backends import workflows as W
        comfy = ComfyClient(comfy_root=man["comfy_root"])
        if not comfy.ping():
            print("FATAL: ComfyUI not reachable at 127.0.0.1:8188 (use --from-raw to skip generation)")
            sys.exit(1)

    proot = man["project_root"]
    tri_target = man["tri_target"]
    summary = []
    print(f"=== BATCH START: {len(assets)} assets | profile={args.mesh_profile} "
          f"| from_raw={args.from_raw} | land={args.land} ===", flush=True)
    print(f"    stage identity {stage_id}  ({bver}; {', '.join(os.path.basename(s) for s in stages)})", flush=True)

    for i, a in enumerate(assets, 1):
        tag = f"{a['faction']}/{a['id']}"
        dest = os.path.join(proot, a["dest"].replace("/", os.sep))
        meta_path = dest + ".meta.json"
        chash = content_hash(a, stage_id)
        if args.land and not args.force and os.path.exists(dest) and os.path.exists(meta_path):
            try:
                if json.load(open(meta_path)).get("content_hash") == chash:
                    print(f"[{i}/{len(assets)}] SKIP {tag} (cached)", flush=True)
                    summary.append({"asset": tag, "status": "cached"}); continue
            except Exception: pass

        print(f"[{i}/{len(assets)}] {'BAKE' if args.from_raw else 'GEN'} {tag} -> {a['mesh_file']}", flush=True)
        tri_kind = a["tri_kind"]
        target = tri_target[tri_kind]
        status = "fail"; gate_res = None; stage_info = None; clean_info = None
        out = os.path.join(WORK, f"{a['faction']}_{a['id']}.glb")
        attempts = 1 if args.from_raw else man["max_rerolls"] + 1

        for attempt in range(attempts):
            seed = man["hunyuan_seed_base"] + attempt * 1000
            try:
                raw = os.path.join(WORK, f"{a['faction']}_{a['id']}_raw.glb")
                plate = os.path.join(man["comfy_root"], "input", f"cc_{a['faction']}_{a['id']}.png")

                if args.from_raw:
                    if not os.path.exists(raw):
                        raise RuntimeError("no raw high-poly on disk: " + raw)
                    if not os.path.exists(plate):
                        raise RuntimeError("no concept plate on disk: " + plate)
                else:
                    w, hgt = a["concept_size"]
                    # 1. concept
                    cwf = W.sdxl_concept(a["prompt"], a["negative"], seed=seed,
                                         steps=man["concept_steps"], cfg=man["concept_cfg"],
                                         width=w, height=hgt, out_prefix=f"concept/{a['faction']}_{a['id']}")
                    ch = comfy.wait(comfy.queue(cwf), timeout=300)
                    imgs = [f for f in comfy.output_files(ch) if f["filename"].lower().endswith(".png")]
                    if not imgs: raise RuntimeError("no concept image")
                    csrc = os.path.join(man["comfy_root"], imgs[0].get("type", "output"), imgs[0]["subfolder"], imgs[0]["filename"])
                    shutil.copy(csrc, plate)

                # 2. isolate the subject. Hunyuan3D's reference pipeline runs rembg before
                #    conditioning; ComfyUI's native nodes do not, which is why every shipped unit
                #    carries the plate's cast shadow fused on as a ground slab.
                rgba, white, clean_info = clean_concept(plate, a["faction"], a["id"])

                if not args.from_raw:
                    # 3. shape, conditioned on the WHITE composite (LoadImage drops alpha)
                    in_name = f"cc_{a['faction']}_{a['id']}_clean.png"
                    shutil.copy(white, os.path.join(man["comfy_root"], "input", in_name))
                    swf = W.hunyuan3d_shape(in_name, seed=seed, out_prefix=f"mesh/{a['faction']}_{a['id']}")
                    shf = comfy.wait(comfy.queue(swf), timeout=900)
                    glbs = [f for f in comfy.output_files(shf) if f["filename"].lower().endswith(".glb")]
                    if not glbs: raise RuntimeError("no glb from hunyuan")
                    gsrc = os.path.join(man["comfy_root"], glbs[0].get("type", "output"), glbs[0]["subfolder"], glbs[0]["filename"])
                    shutil.copy(gsrc, raw)

                # 4. mesh stage
                if os.path.exists(out):
                    os.remove(out)
                stage_info, err = mesh_stage(
                    args.mesh_profile, raw, out, target, tri_kind,
                    project_front=(rgba if args.mesh_profile == "bake" else None), tex=args.tex)
                if not os.path.exists(out):
                    raise RuntimeError(f"{args.mesh_profile} stage produced no glb: {err}")

                # 5. gate
                gate_res = gate(out, tri_kind, require_textured=(args.mesh_profile == "bake"))
                if gate_res["verdict"] == "PASS":
                    status = "pass"; break
                print(f"    attempt {attempt+1} gate FAIL: {gate_res['fails']}", flush=True)
            except Exception as e:
                print(f"    attempt {attempt+1} error: {e}", flush=True)

        thumb = os.path.join(THUMBS, f"{a['faction']}_{a['id']}")
        if status == "pass":
            try:
                sh([BLENDER, "-b", "-P", RENDER, "--", out, thumb, "384"], timeout=300)
            except Exception: pass

        if status == "pass" and args.land:
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy(out, dest)
            json.dump({"content_hash": chash, "asset": tag, "mesh_file": a["mesh_file"],
                       "tris": gate_res["metrics"].get("tris"), "warns": gate_res["warns"],
                       "stage_id": stage_id, "blender": bver, "mesh_profile": args.mesh_profile,
                       "stage": stage_info, "clean": clean_info},
                      open(meta_path, "w"), indent=2)
            print(f"    PASS {gate_res['metrics'].get('tris')} tris -> landed {a['dest']}", flush=True)
            summary.append({"asset": tag, "status": "pass", "tris": gate_res["metrics"].get("tris"),
                            "warns": gate_res["warns"], "dest": a["dest"], "stage": stage_info})
        elif status == "pass":
            print(f"    PASS {gate_res['metrics'].get('tris')} tris -> HELD at {out} (no --land)", flush=True)
            summary.append({"asset": tag, "status": "pass-held", "tris": gate_res["metrics"].get("tris"),
                            "warns": gate_res["warns"], "held_at": out, "stage": stage_info,
                            "thumb": thumb + ".png"})
        else:
            print(f"    GIVE UP {tag} after {attempts} attempt(s)", flush=True)
            summary.append({"asset": tag, "status": "fail", "gate": gate_res})

    sp = os.path.join(WORK, "batch_summary.json")
    json.dump({"stage_id": stage_id, "blender": bver, "mesh_profile": args.mesh_profile,
               "from_raw": args.from_raw, "landed": args.land, "results": summary},
              open(sp, "w"), indent=2)
    npass = sum(1 for s in summary if s["status"] in ("pass", "pass-held", "cached"))
    print(f"=== BATCH DONE: {npass}/{len(assets)} ok. summary -> {sp}; thumbs -> {THUMBS} ===", flush=True)
    sys.exit(0 if npass == len(assets) else 1)


if __name__ == "__main__":
    main()
