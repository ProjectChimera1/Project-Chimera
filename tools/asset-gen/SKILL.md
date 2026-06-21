---
name: asset-gen
description: Generate game-ready 3D models, textures, and SFX locally with AI (ComfyUI + Hunyuan3D/SDXL/Stable-Audio), normalize/export them to an engine's exact ingest contract, and self-QA each output (render + numeric checks + in-engine load) with bounded re-rolls. Project-agnostic via a swappable engine profile. Triggers on "generate assets", "run the asset pipeline", "/asset-gen".
---

# asset-gen — local AI asset-generation pipeline (reusable)

A config-driven, self-QA'ing local pipeline that turns a **manifest** of needed assets into
engine-ready files. Project-agnostic: all engine specifics live in a **profile**
(`config/engine_profiles/<engine>.json`); swap the profile to serve any project.

> **First built for:** Project Chimera (Godot 4.6.2). Profile: `config/engine_profiles/godot_chimera.json`.
> Themed manifest: `_bmad-output/asset-generation-manifest.md`. Research/rationale:
> `_bmad-output/local-asset-pipeline-research.md`.

## Pipeline stages
1. **Concept image** (optional) — SDXL in ComfyUI + IP-Adapter style-lock (NOT FLUX: FLUX fp8 freezes a 12GB/16GB rig).
2. **3D geometry** — Hunyuan3D-2 **shape-only** (~5-6GB VRAM, Apache-2.0) via ComfyUI; TripoSG (MIT) failover; TripoSR for instant drafts.
3. **Normalize/retopo/export** — headless Blender (`scripts/qa/blender_pipeline.py`): join → triangulate → decimate to tri-budget → single material → origin to feet → **PLAIN uncompressed GLB**.
4. **Textures** — SDXL + ComfyUI-seamless-tiling (circular padding; SDXL-only) for tileable terrain.
5. **SFX** — Stable Audio Open Small → ffmpeg mono `.ogg`.
6. **Self-QA loop** (per asset, fail-cheapest-first) — see below.

## The self-QA loop (the "checks its own work" step)
- **L1 numeric** (`scripts/qa/trimesh_gate.py`): forbidden-compression extensions (HARD FAIL — Godot rejects Draco/meshopt/quantized GLB, err 43), tri budget, material count, watertight/winding, origin-at-feet, NaN.
- **L2 render** (`scripts/qa/blender_qa_render.py`): 3 ortho PNGs (top/front/¾) + metrics → an agent reads them and judges silhouette/proportions/facing.
- **L3 in-engine** (`scripts/qa/godot_ingest_check.gd`): load through the REAL runtime path (`GLTFDocument.AppendFromFile → GenerateScene`) headless — catches silent corruption that renders fine.
- **Decision:** PASS → land at `dest`; FIXABLE (winding/over-budget/off-center) → auto-repair, retest once; FAIL → re-roll new seed up to **N=4**, then failover generator, then flag for human. The engine box-placeholder guarantees no crash.

## Manifest schema (per asset)
`id, type(unit|building|texture|sfx), prompt|concept_ref, faction/tags, backend, gen_params{seed,...},
constraints{tri_target,tri_cap,max_materials,max_tex_dim,must_be_watertight,must_be_mono,target_format},
transform{up_axis,forward_axis,origin}, dest_path, status, content_hash, qa_report`

Idempotent: hash `(id + prompt + params + tool_version)`; skip if a passing artifact + sidecar `.meta.json` exists, so re-runs only regenerate changed entries and a crash resumes.

## Local tool paths (this machine — see config/tools.local.json)
- Blender 4.5.10: `D:\tools\blender\blender-4.5.10-windows-x64\blender.exe`
- venv (validators): `D:\tools\asset-gen-venv\Scripts\python.exe`
- ffmpeg: under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg...\bin\ffmpeg.exe`
- Python 3.12: `%LOCALAPPDATA%\Programs\Python\Python312\python.exe`

## Status
Backbone (Blender export + numeric gate + in-engine ingest) built and being proven on a test mesh
before any multi-GB model download. Generator wiring (ComfyUI/Hunyuan3D/SDXL/Stable-Audio) is the next
gated step.
