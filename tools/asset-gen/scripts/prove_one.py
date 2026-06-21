# -*- coding: utf-8 -*-
"""Single-asset proof: SDXL concept -> Hunyuan3D shape -> raw .glb. Validates the ComfyUI half
of the pipeline before the full batch. Run with any python (stdlib-only HTTP to ComfyUI)."""
import sys, os, shutil
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from backends.comfy_client import ComfyClient
from backends import workflows as W

COMFY_ROOT = r"C:\Vid-Pic Gen Dump from C Drive\AI Video Generation\ComfyUI_windows_portable_nvidia\ComfyUI_windows_portable\ComfyUI"
WORK = r"D:\tools\asset-gen-work"
os.makedirs(WORK, exist_ok=True)

UNIT_PREFIX = ("clean low-poly RTS unit, flat-shaded single-material albedo (engine applies a global "
    "cel-shade, do not add PBR/specular/roughness/normal-map detail), readable silhouette for a distant "
    "top-down camera, early-20th-century industrial/European-military FMA-inspired alchemy world, neutral "
    "relaxed idle pose facing +Z (A-pose, arms slightly away from the body, NOT a rigid T-pose), plain "
    "white background, origin centered at the feet/base on the +Z axis.")
COURT_PALETTE = ("FACTION PALETTE: oxblood-crimson cloth, flat black-iron and brass blocks, faint crimson "
    "alchemic Core-glow as emissive accent, gothic-uncanny homunculus mood; flat color blocks, no implied metal gloss.")
NEGATIVE = ("high-poly, photorealistic, PBR, specular highlights, glossy or metallic reflections, normal-map "
    "or rivet/crack micro-detail, baked shadows, ground plane, base or pedestal, scenery, multiple characters, "
    "cropped or missing limbs, text, watermark, logo, signature, busy background, motion blur, depth of field, "
    "dramatic cinematic lighting, off-center framing, action or dynamic pose.")
SUBJECT = ("a stooped hollow-eyed laborer in a soot-stained leather apron, one crude brass prosthetic ending "
    "in a digging claw, a dim red Core-ember glowing through a chest vent; hunched, the smallest and simplest "
    "silhouette so it reads as a non-combatant worker.")

ASSET = "cinderhand_thrall"
prompt = f"{UNIT_PREFIX} {COURT_PALETTE} SUBJECT: {SUBJECT}"

c = ComfyClient(comfy_root=COMFY_ROOT)
if not c.ping():
    print("FAIL: ComfyUI not reachable"); sys.exit(1)

print(f">>> [1/2] SDXL concept image for {ASSET}")
wf = W.sdxl_concept(prompt, NEGATIVE, seed=42, steps=30, cfg=7.0, out_prefix=f"concept/{ASSET}")
hist = c.wait(c.queue(wf), timeout=300)
imgs = [f for f in c.output_files(hist) if f["filename"].lower().endswith(".png")]
print("    concept outputs:", imgs)
if not imgs:
    print("FAIL: no concept image produced"); sys.exit(1)
src = os.path.join(COMFY_ROOT, imgs[0].get("type", "output"), imgs[0]["subfolder"], imgs[0]["filename"])
in_name = f"concept_{ASSET}.png"
shutil.copy(src, os.path.join(WORK, in_name))
shutil.copy(src, os.path.join(COMFY_ROOT, "input", in_name))
print(f"    concept saved -> {os.path.join(WORK, in_name)}")

print(f">>> [2/2] Hunyuan3D shape from concept")
wf2 = W.hunyuan3d_shape(in_name, seed=42, out_prefix=f"mesh/{ASSET}")
hist2 = c.wait(c.queue(wf2), timeout=900)
glbs = [f for f in c.output_files(hist2) if f["filename"].lower().endswith(".glb")]
print("    shape outputs:", glbs)
if not glbs:
    print("FAIL: no glb produced"); sys.exit(1)
gsrc = os.path.join(COMFY_ROOT, glbs[0].get("type", "output"), glbs[0]["subfolder"], glbs[0]["filename"])
graw = os.path.join(WORK, f"{ASSET}_raw.glb")
shutil.copy(gsrc, graw)
print(f"RAW_GLB {graw} {os.path.getsize(graw)//1024} KB")
print("DONE")
