# -*- coding: utf-8 -*-
"""Generate the 4 seamless terrain albedo textures (SDXL + circular-padding tiling).
Run with the venv python AFTER the GLB batch (shares ComfyUI/GPU):
    D:\\tools\\asset-gen-venv\\Scripts\\python.exe terrain_gen.py
Lands 1024 tileable PNGs in godot/assets/textures/terrain/ for assignment in the Terrain3D Inspector.
"""
import sys, os, shutil
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from backends.comfy_client import ComfyClient
from backends import workflows as W

COMFY_ROOT = r"C:\Vid-Pic Gen Dump from C Drive\AI Video Generation\ComfyUI_windows_portable_nvidia\ComfyUI_windows_portable\ComfyUI"
OUT = r"D:\Projects\Project_Chimera\godot\assets\textures\terrain"
os.makedirs(OUT, exist_ok=True)

NEG = ("seams, visible tile edges, repeating motif, objects, shadows, baked lighting, text, watermark, "
       "photoreal high detail, high contrast, vignette, border, frame")
BIOMES = [
    ("grass", "seamless tileable top-down stylized grass terrain texture, flat even green tones, subtle blade variation, low-contrast, game ground albedo, no objects"),
    ("dirt",  "seamless tileable top-down stylized dry dirt terrain texture, flat earthy brown, subtle pebbles and cracks, low-contrast, game ground albedo, no objects"),
    ("rock",  "seamless tileable top-down stylized grey rock terrain texture, flat slate tones, subtle fracture pattern, low-contrast, game ground albedo, no objects"),
    ("snow",  "seamless tileable top-down stylized white snow terrain texture, flat soft off-white, gentle drift undulation, low-contrast, game ground albedo, no objects"),
]

c = ComfyClient(comfy_root=COMFY_ROOT)
if not c.ping():
    print("FATAL: ComfyUI not reachable"); sys.exit(1)

for name, prompt in BIOMES:
    print(f">>> {name}", flush=True)
    wf = W.sdxl_seamless_terrain(prompt, NEG, seed=42, steps=30, cfg=7.0, size=1024, out_prefix=f"terrain/{name}")
    hist = c.wait(c.queue(wf), timeout=300)
    pngs = [f for f in c.output_files(hist) if f["filename"].lower().endswith(".png")]
    if not pngs:
        print(f"    FAIL: no image for {name}"); continue
    src = os.path.join(COMFY_ROOT, pngs[0].get("type", "output"), pngs[0]["subfolder"], pngs[0]["filename"])
    dst = os.path.join(OUT, f"{name}.png")
    shutil.copy(src, dst)
    print(f"    -> {dst}", flush=True)

print(f"DONE: terrain -> {OUT}", flush=True)
