---
name: asset-gen
description: Generate game-ready 3D models, textures, and SFX locally with AI (ComfyUI + Hunyuan3D/SDXL/Stable-Audio), normalize/export them to an engine's exact ingest contract, and self-QA each output (render + numeric checks + in-engine load) with bounded re-rolls. Project-agnostic via a swappable engine profile. Triggers on "generate assets", "run the asset pipeline", "/asset-gen".
---

# asset-gen — local AI asset-generation pipeline (reusable)

A config-driven, self-QA'ing local pipeline that turns a **manifest** of needed assets into
engine-ready files. Project-agnostic: all engine specifics live in a **profile**
(`config/engine_profiles/<engine>.json`); swap the profile to serve any project.

> **First built for:** Project Chimera (Godot 4.6.3). Profile: `config/engine_profiles/godot_chimera.json`.
> Themed manifest: `_bmad-output/asset-generation-manifest.md`. Research/rationale:
> `_bmad-output/local-asset-pipeline-research.md`.

> **This file describes what is INSTALLED AND WIRED on this rig, not what the design allows.**
> It previously asserted three things that were not true — an IP-Adapter style-lock that has no
> node and no weights, an Apache-2.0 licence for Hunyuan3D-2, and a "next gated step" status for a
> generator that had already produced the whole roster. Anything aspirational belongs under
> *Evaluated and rejected* or *Not installed*, never in the stage list.

## Pipeline stages

1. **Concept image** — SDXL 1.0 in ComfyUI, text-to-image only (`backends/workflows.py:sdxl_concept`).
   Seven nodes: `CheckpointLoaderSimple → CLIPTextEncode ×2 → EmptyLatentImage → KSampler →
   VAEDecode → SaveImage`. **There is no IP-Adapter and no style-lock** — no IPAdapter node exists
   in the graph and `D:\ai-models\ipadapter`, `clip_vision`, `controlnet` and `loras` are all
   empty. Style consistency comes from the prompt prefixes in `build_manifest.py`, nothing more.
   Not FLUX: FLUX fp8 freezes a 12 GB/16 GB rig.
2. **Subject isolation** — `scripts/qa/clean_concept.py`. rembg (u2net) matte, largest-component
   keep, erode, feather; emits BOTH an RGBA cutout and a white composite, and `--square` pads to a
   square canvas. Required, not optional: ComfyUI's native Hunyuan3D nodes do **not** run rembg the
   way Tencent's reference pipeline does, so an un-matted plate makes the generator reconstruct the
   drawing's cast shadow as a ground slab welded to the model's feet.
3. **3D geometry** — Hunyuan3D-2 **shape-only** (~5-6 GB VRAM) via ComfyUI's native nodes,
   `VoxelToMesh → SaveGLB`. Shape only means **no UVs and no texture** come out of this stage;
   everything visible is added in stage 4. Licence: Tencent Hunyuan Community License, **not**
   Apache-2.0 as this file used to claim. Cleared for this project by the owner (2026-09-09);
   re-check before any commercial redistribution of generated meshes.
4. **Retopo + texture bake** — headless Blender, `scripts/qa/hp_to_lp_bake.py` (the default, via
   `run_manifest.py --mesh-profile bake`): join → weld → strip the fused ground slab → debris-shell
   removal → decimate to tri budget → **smart UV unwrap** → project the concept plate onto the
   high-poly → **selected-to-active Cycles bake** of base colour (+ optional tangent normal) →
   origin to feet → PLAIN uncompressed GLB with embedded textures.
   *Legacy path:* `scripts/qa/blender_pipeline.py` (`--mesh-profile normalize`) — kept reachable so
   the pre-2026-09 roster can be reproduced. It has **no unwrap step** and clears every material
   unconditionally, which is why every asset it produced is flat grey with zero UVs.
5. **Terrain textures** — SDXL + ComfyUI-seamless-tiling (circular padding; SDXL-only) for tileable
   ground. Four 1024px plates are committed under `godot/assets/textures/` and are currently
   **bound to nothing in engine** — see Epic 16 slice 4.
6. **SFX** — `scripts/sfx_gen.py`, Stable Audio Open Small → ffmpeg mono `.ogg`. **External
   dependency:** it imports `stable_audio_3` from a venv belonging to a different project
   (`D:\Projects\TabletopMagic\...\stable-audio-3`) and will not run without it.
7. **Self-QA loop** (per asset, fail-cheapest-first) — see below.

## The self-QA loop (the "checks its own work" step)

- **L1 numeric** (`scripts/qa/trimesh_gate.py`): forbidden-compression extensions (HARD FAIL —
  Godot rejects Draco/meshopt/quantized GLB, err 43), tri budget, material count, watertight/
  winding, origin-at-feet, NaN. With `--require-textured` it additionally hard-fails on missing
  `TEXCOORD_0`, a missing `baseColorTexture`, no embedded image, or a non-white `baseColorFactor`
  (a non-white factor multiplies the albedo and would render every texture dark). These are read
  from the GLB JSON, not through trimesh, which normalises an absent factor to an assumed white
  and would mask exactly that defect. A trimesh/numpy `ImportError` is a HARD FAIL — as a warning
  it skipped every geometry check and still printed PASS.
- **L2 render** (`scripts/qa/blender_qa_render.py`): 3 ortho PNGs (front/side/iso) an agent reads
  to judge silhouette, proportions, facing **and surface**. `display.shading.color_type` is set to
  `TEXTURE`; it used to be left unset, falling through to a default that physically cannot display
  an albedo, so every sheet this script produced before 2026-09-10 was grey regardless of the
  asset. `scripts/qa/contact_sheet.py` stitches the views and MEASURES subject saturation, so
  "shows colour rather than grey" is a number rather than an opinion.
- **L3 in-engine** (`scripts/qa/godot_ingest_check.gd`): load through the REAL runtime path
  (`GLTFDocument.AppendFromFile → GenerateScene`) headless — catches silent corruption that
  renders fine.
- **Decision:** PASS → land at `dest`; FAIL → re-roll new seed up to **N=4** (generate route only;
  `--from-raw` has nothing to re-roll), then flag for human. The engine box-placeholder guarantees
  no crash.

## Idempotency — how the cache key works

`content_hash(asset, stage_id)` = sha1 over `id + faction + prompt + stage_id`, where
`stage_id` = sha1 over **the bytes of every stage script the run actually shells out to**, plus the
`blender --version` string.

This replaced a hand-typed `PIPELINE_VERSION` constant, which was a live defect: nobody retypes a
version string when they swap a stage, so replacing the mesh stage left all 24 hashes byte-identical
and the batch printed `SKIP (cached)` 24 times and exited reporting success. Pinned by
`scripts/test_stage_identity.py` (11 checks, both directions).

`scripts/vault_sources.py` mirrors the raw high-polys and concept plates off-drive with sha256
sidecars — they live outside the repo in a single copy and every later stage re-derives from them.

## Manifest schema (per asset)

`id, faction, prefix, tri_kind, mesh_file, mesh_scale, dest, concept_size, prompt, negative`
— generated by `scripts/build_manifest.py`. Note `prompt` feeds the content hash and `negative`
does not, so editing a negative prompt will not invalidate a cached asset.

## Local tool paths (this machine)

- **Blender 4.5.10** (the pipeline's pinned build): `D:\tools\blender\blender-4.5.10-windows-x64\blender.exe`
  — override with `CHIMERA_BLENDER`. The version string is folded into the stage identity, so
  changing it correctly invalidates the cache.
- **Blender 5.2.1** (MCP host only, NOT the pipeline): `D:\tools\blender\blender-5.2.1-windows-x64\blender.exe`
  — required because the Blender Lab MCP add-on declares `blender_version_min = "5.1.0"`.
  Start it with `scripts/blender_mcp_host.ps1`.
- venv (validators): `D:\tools\asset-gen-venv\Scripts\python.exe` (3.12.10; trimesh, rembg, scipy,
  Pillow, numpy, xatlas, onnxruntime — **no torch**)
- ComfyUI: `D:\tools\ComfyUI_windows_portable\` (`run_nvidia_gpu.bat`); models on `D:\ai-models\`
- ffmpeg: under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg...\bin\ffmpeg.exe`
- Python 3.12: `%LOCALAPPDATA%\Programs\Python\Python312\python.exe`

*(`config/tools.local.json` is referenced by older notes and does not exist. Paths live here.)*

## Not installed on this rig

Named because their absence is load-bearing — several plans have assumed otherwise:
**Hunyuan3D-Paint** (texture generation), **BiRefNet**, **ControlNet**, any **LoRA**, any
**IP-Adapter** or **CLIP-Vision** weights, **TripoSG**, **TripoSR**, **UniRig**. `D:\ai-models`
holds exactly two weights: `sd_xl_base_1.0` and `hunyuan3d-dit-v2_fp16` (shape only).

## Evaluated and rejected, and why

- **Hunyuan3D-Paint for real texture generation** — the correct fix for texture quality, and the
  reason the current bake is a projection instead. Rejected for now on VRAM: its multi-view paint
  stack does not fit alongside the shape model on a 12 GB card without offloading work that makes
  a 24-asset batch impractical. Revisit if texture quality becomes the blocker.
- **Pixal3D / TRELLIS.2 as a generator swap** — off the critical path. The raws already on disk
  are good; the quality loss was in the reduction stage, not generation. Candidate for the *final*
  roster once the narrative work lands, not for the rough draft.
- **`nvdiffrast`-tainted wrapper packs** — licence-incompatible with shipping.
- **Paid ComfyUI workflow packs** — no capability here that the native nodes lack.
- **A quantised cel-shade ramp in engine** — measured a REGRESSION on this roster and rejected;
  see `godot/src/UI/WorldPresentation.cs`. Viable only once assets carry real albedo.
- **VAT / skeletal animation** — the tooling is absent, not merely unbuilt (UniRig has no Python
  3.13 wheels; the asset-gen venv has no torch; Mixamo is a manual web service with no API).

## Status (2026-09-10)

Wired end to end and proven on the real roster. The generate route (ComfyUI concept → shape) and
the `--from-raw` route (re-bake from the high-polys on disk, no ComfyUI) both work. Textured GLBs
carry UVs, an embedded albedo and a white base-colour factor, and pass L1 with `--require-textured`.
