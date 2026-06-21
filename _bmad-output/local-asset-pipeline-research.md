---
title: Local AI Asset-Generation Pipeline — Research & Setup Plan
generated: 2026-06-21
rig: Windows 11 / RTX 3060 12GB / Ryzen 5 5600 / 16GB RAM / WSL2
method: 15-agent adversarially-verified research workflow (wc3foq88g)
status: research complete — awaiting Alec go/no-go on setup
---

> Per-asset *prompt seeds* below the research body use the OLD generic theme; the FMA-themed
> prompts live in `asset-generation-manifest.md`. Everything else here is theme-independent.

# Project Chimera — Local AI Asset-Generation Pipeline (Grounded Final Report)

**Rig:** Windows 11 · RTX 3060 12GB (CUDA 8.6) · Ryzen 5 5600 (6c/12t) · **16GB system RAM (the real ceiling)** · WSL2/CUDA available · large D: free space.
**Contract target:** Godot 4.6.2 + .NET 8 RTS. Runtime ingest via `GLTFDocument.AppendFromFile -> GenerateScene`. Outputs: 24 low-poly `.glb` + 7 mono `.ogg` + 4 seamless terrain textures. Self-QA + reusable Claude skill.

---

## 0. Executive summary — what changed vs the early-2026 plan

The adversarial verification overturned the single most load-bearing assumption from prior research:

- **REFUTED:** "Hunyuan3D-2.0 stock full shape+texture fits ~12GB." The upstream Tencent README says **16GB VRAM** for stock full pipeline; the ~12GB figure was the *offloaded* mmgp path, which itself wants **24GB+ system RAM** (the WinPortable docs) — **above this rig's 16GB**. So neither the stock full pipeline nor the aggressive-offload texture path is comfortable here.
- **The resolution that dissolves the whole VRAM/RAM problem:** generate **SHAPE-ONLY** (Hunyuan3D-2 native ComfyUI shape ≈ **5–6GB VRAM**, CONFIRMED at primary source) and **skip texturing entirely**. This is not a compromise — Project Chimera's engine applies a shared `StandardMaterial3D` preset library + **one global cel-shading post-process pass** that *overrides per-asset PBR*. Baked textures would be thrown away at runtime. Spending the exact VRAM/RAM you don't have on PBR you'll discard is the wrong trade.
- **CONFIRMED, empirically, on the exact build (`Godot_v4.6.2-stable_mono`):** the runtime loader rejects any GLB declaring `KHR_mesh_quantization`, `EXT_meshopt_compression`, or `KHR_draco_mesh_compression` in `extensionsRequired` (returns err 43, never reaches `GenerateScene`). **This inverts the prior gltfpack recommendation.** Export must be **plain, uncompressed, un-quantized GLB.** A control plain-float GLB loaded fine.
- **CONFIRMED:** FLUX.1-dev fp8 on *this exact rig* stalls 10–20 min and freezes Windows (16GB RAM swapping, not VRAM) → **SDXL is the 2D backbone**, not FLUX. Bonus: the seamless-tiling node only works on UNet models (SDXL), not DiT (FLUX/SD3.5), so terrain textures *require* SDXL anyway.
- **CONFIRMED:** Stable Audio Open is commercial-OK under the Stability Community License for users under $1M revenue (free registration required) → local-first SFX is legally clean for a solo indie.
- **UNCERTAIN (verify during setup):** TRELLIS.2-4B (clean MIT license) via ComfyUI GGUF nodes. Q4 (~6GB) is very likely fine on 12GB; the "Full ~11GB" figure was measured on a *16GB* card by a third-party blog and has **no primary 12GB-3060 confirmation**. Treat TRELLIS.2 as an **optional quality-upgrade path to validate with nvidia-smi**, not the day-one backbone.

**Net recommended primary 3D path: Hunyuan3D-2 SHAPE-ONLY (native ComfyUI nodes) → headless Blender decimate/normalize/export plain GLB.** It's the only path with a *primary-source-confirmed* VRAM fit on this rig AND an Apache-2.0 base (Hunyuan3D **2.0**, not 2.1).

> **License note that prior research got wrong:** Hunyuan3D **2.0** weights = Apache 2.0 (clean). Hunyuan3D **2.1** = Tencent Community License (commercial OK under 1M MAU, but **void in EU/UK/South Korea**). Use the **2.0** shape models. TripoSG (MIT) and TRELLIS.2 (MIT) are the cleanest-license backups.

---

## 1. Recommended end-to-end LOCAL pipeline

One **ComfyUI** server (Windows portable, NVIDIA) is the single headless API surface for 3D shape, 2D textures, and audio — driven over HTTP `/prompt` + WebSocket `/ws` + `/history` + `/view`. Heavy CPU-only stages (Blender, ffmpeg, validators) run as subprocesses. Load models **per-stage sequentially** — never co-resident a 3D model and an SDXL model (16GB RAM is the binding constraint).

### Stage A — 3D geometry (units + buildings)
- **Generator:** Hunyuan3D-2 **shape-only** via ComfyUI native nodes (`Hunyuan3Dv2Conditioning` / `...MultiView` + Image-Only Checkpoint Loader). Uses the **0.6B mini (~5GB)** or **standard shape (~6GB)** model. Input = a concept image (Stage B). Output = dense `.glb` to `ComfyUI/output/mesh`.
  - **Backup geometry generator (auto-failover on OOM / bad topology):** **TripoSG** (MIT, ~6GB) driven from a small Python/diffusers script.
  - **Optional quality upgrade (verify VRAM first):** TRELLIS.2-4B GGUF **Q4** via `ComfyUI-Trellis2-GGUF`.
  - **Draft/placeholder (instant):** TripoSR (MIT).
- **Why shape-only:** primary-source-confirmed 5–6GB fit; sidesteps the refuted 12–16GB texture path and the 24GB-RAM offload path; aligns exactly with the engine's material override.

### Stage B — concept/reference images (input to image-to-3D) + terrain textures
- **Model:** **SDXL** (a commercial-OK community fine-tune, e.g. Juggernaut/RealVisXL — verify each model card) in ComfyUI. ~8GB, comfortable on 12GB, no RAM thrash. Add SDXL-Lightning/DMD2 LoRA for 4–8 step drafting so the QA agent can over-generate cheaply.
- **Style lock (zero training, day one):** **IP-Adapter Plus** (Apache-2.0), `weight_type = "Style Transfer (SDXL)"`, fed one canonical "style key" image; add **ControlNet** (depth/lineart) when faction variants must share a silhouette. **Phase 2 (optional, after the look is final):** train an SDXL **style LoRA** in Kohya_ss/OneTrainer (batch 1, gradient checkpointing, Adafactor, bf16, rank 32–64; 12GB is the hard floor, ~3–5h, close all other GPU apps).
- **Terrain (4 seamless biomes):** SDXL + **ComfyUI-seamless-tiling** (spinagon, GPL-3.0) — circular-padding `Seamless Tile` + `Make Circular VAE` / `Circular VAE Decode`, asymmetric X/Y. Generate 1024, tile-upscale to 1k–2k keeping circular padding. **DiT models cannot do this** — SDXL is mandatory for terrain.

### Stage C — retopo / normalize / export (CPU-only, the backbone)
**Headless Blender (`blender -b -P pipeline.py -- in.glb out.glb tri_budget`)** does everything in one pass (all APIs CONFIRMED present in Blender 4.x):
1. Import the dense AI mesh.
2. **DECIMATE (Collapse)** modifier → ratio = `target_tris / current_tris`; apply.
3. **Origin to base/feet:** move origin so `min-Z ≈ 0` and X/Y centered (not geometric median).
4. **Orient +Z facing** *before* the Y-up remap; `transform_apply(rotation=True, scale=True)`.
5. **Single material:** `obj.data.materials.clear()` then append one flat material.
6. **Convex collision hull:** `bmesh.ops.convex_hull` → emit hull verts as sidecar JSON for runtime `ConvexPolygonShape3D` (Chimera ingests via `AppendFromFile`, not editor import, so do NOT rely on `-col`/`-convcol` suffixes).
7. **Export plain GLB:** `export_format='GLB', export_yup=True, export_apply=True, export_draco_mesh_compression_enable=False`, no cameras/lights/animations. **Never enable Draco/quantization** (CONFIRMED runtime-incompatible).
- **LODs:** re-decimate to ~40% (LOD1) and ~15% (LOD2), or use `gltf-transform weld && gltf-transform simplify --ratio` (discrete commands only — never `optimize`, which adds Draco+webp).
- **DEMOTED:** gltfpack (its default quantization breaks Godot runtime load). If ever used: `-noq` and no `-c`.

### Stage D — SFX (7 mono .ogg)
- **Generator:** **Stable Audio Open SMALL** (341M, Stability Community License, commercial-OK <$1M) via `diffusers StableAudioPipeline` (fp16, `audio_end_in_s` for 1–2s, `num_waveforms_per_prompt=3` for best-of CLAP ranking, fixed `torch.Generator` seed). Trivially under 12GB/16GB. Escalate to **SAO 1.0 (1.1B)** only for explosion/death — and only with `enable_vae_slicing()` to avoid the documented ~12GB VAE-decode OOM.
- **Convert:** `ffmpeg -i in.wav -af silenceremove=... -c:a libvorbis -q:a 4 -ac 1 out.ogg` (mono downmix + trailing-silence trim).
- **REJECTED:** Meta AudioGen — weights CC-BY-NC (can't ship) and needs ~16GB VRAM.

### Stage E — orchestration
`comfy launch --background` (comfy-cli) + snapshot lockfiles for reproducibility. One Python layer submits API-format workflow JSON with manifest-substituted params (prompt/image/seed), waits on the `executing`/`node==null` WS event **and** polls `/history` for `execution_error` with a hard timeout + re-queue, then harvests outputs.

---

## 2. The self-QA loop (render-to-thumbnail + programmatic checks + re-roll/fix)

A bounded, four-layer gate run **per asset**, fail-fast cheapest-first:

**Layer 1 — trimesh (ms, CPU, no display):** `m = trimesh.load(glb, force='mesh')`
- tri count `len(m.faces)`; `m.is_watertight`; `m.is_winding_consistent`; `m.bounds`/`m.extents` (verify `min-Z ≈ 0`, X/Y centered, sane scale); submesh/material count via Scene geometry dict.
- Minor faults auto-fix: `m.process(validate=True)`, `m.fix_normals()`, re-test once.

**Layer 2 — Blender headless render + repair (seconds):** `blender -b -P qa_render.py -- in.glb out_*.png`
- Render **3 ortho views (top / front / 3-4)** under a key light + a dim shadowless opposing fill (EEVEE). Also print JSON metrics (bbox, poly count, material-slot count) to stdout.
- Blender is the chosen renderer because **pyrender's offscreen backends (EGL/OSMesa) are not Windows-native** (OSMesa needs Mesa compiled from source).
- Doubles as the geometry-repair stage (decimate to budget, recenter, +Z, merge materials, re-export).

**Layer 3 — agent vision judgment:** Claude reads the 3 PNGs and judges **silhouette legibility from top-down, proportions, facing, and gross deformities** against the prompt intent ("reads as an Iron Pact juggernaut from the RTS camera").

**Layer 4 — in-engine authoritative gate:** load through the **real runtime path** — `GLTFDocument.AppendFromFile -> GenerateScene` headless, or the project's `godot-mcp godot_validate_meshes` tool — catching winding/dropped-tri/degenerate-UV/NaN-normal corruption that renders silently. Anything that would hit the box-placeholder fallback is caught *before* shipping.

**Decision controller:**
- PASS → promote to `dest_path`, log `seed + workflow-hash + tool versions` to the manifest.
- FIXABLE (winding/normals/over-budget/off-center) → auto-repair, goto Layer 1 (once).
- FAIL (bad geometry/silhouette) → **re-roll new seed** (and optionally tweak prompt), up to **N=4**. Then fall back **TripoSG → (optional) Tripo-cloud paid**, else flag for human. The engine's box placeholder guarantees the pipeline never crashes/blocks.
- **Determinism:** pinned model snapshot, fixed seeds, fixed decimate ratios, recorded workflow-JSON hash. Re-rolls are the *intended* stochastic escape hatch, not a determinism failure.

---

## 3. Reusable, project-agnostic Claude skill

```
skills/asset-gen/
  SKILL.md                       # when-to-use + orchestration prose
  scripts/
    run_manifest.py              # queue + content-hash cache + bounded retry orchestrator
    backends/
      comfy_client.py            # /prompt + /ws + /history + /view
      triposg_client.py          # diffusers failover (shape)
      audio_client.py            # StableAudioPipeline + ffmpeg
    qa/
      blender_pipeline.py        # decimate/origin/+Z/material/hull/export
      blender_qa_render.py       # 3 ortho PNGs + JSON metrics
      trimesh_gate.py
      godot_ingest_check.gd      # AppendFromFile->GenerateScene
      texture_seam_check.py      # np.roll half-tile + seam delta
      audio_check.py             # ffprobe mono/duration/peak
  workflows/*.json               # parameterized ComfyUI graph templates
  config/engine_profiles/
    godot_chimera.json           # THE ENGINE CONTRACT AS DATA
```

**Key design:** the engine contract (tri budgets, `.glb`/`.ogg`, Y-up/+Z/feet, single-material, texture caps, mono audio, no-compression rule) lives in `engine_profiles/*.json`. The **identical skill serves any future project by swapping the profile.** Idempotent + content-hash-cached: hash `(asset_id + prompt + params + tool_version)`; skip if a passing artifact + sidecar `.meta.json` already exists, so re-runs only regenerate changed entries and a mid-batch crash resumes.

**Manifest schema (per asset):** `id, type(unit|building|texture|sfx), prompt|concept_ref, faction/tags, backend, gen_params{seed, octree_resolution|steps|cfg|audio_len}, constraints{tri_target, tri_cap, max_materials, max_tex_dim, must_be_watertight, must_be_mono, target_format}, transform{up_axis, forward_axis, origin}, dest_path, status, content_hash, qa_report`.

---

## 4. How outputs satisfy the engine ingest contract

| Contract requirement | How the pipeline guarantees it |
|---|---|
| `.glb`, Y-up | Blender `export_format='GLB', export_yup=True` |
| Origin at base/feet | Blender origin moved to `min-Z=0`, X/Y centered; verified post-export via `trimesh.bounds` |
| Facing +Z | Rotate before Y-up remap, then `transform_apply` |
| Single material | `materials.clear()` + one flat material; gltf-transform `prune`/`join` |
| Tri caps | DECIMATE to target; QA hard-fails above cap |
| **No quantization/meshopt/Draco** | Plain GLB export; QA fails the build if any of those appear in `extensionsUsed` (CONFIRMED-required) |
| Loads via `AppendFromFile->GenerateScene` | Layer-4 in-engine gate exercises the exact runtime path |
| Modest texture dims, mono audio | gltf-transform `inspect` dim check; ffprobe mono/duration check |
| Invalid → box placeholder, never crash | Pipeline mirrors the engine's own allow-list, so passing assets are guaranteed ingestible |

---

## 5. ONE reconciled tri-budget recommendation

Resolve the contract's looser figures against the GDD's tighter LOD0 figures by treating **tighter = target, looser = hard ceiling**:

| Asset | LOD0 PASS (target) | WARN + auto-decimate | FAIL (re-roll) | LOD1 | LOD2 | Collision |
|---|---|---|---|---|---|---|
| **Units** | ≤ 3,000 tris (1k–3k) | 3k–15k | > 15,000 | ~40% LOD0 | ~15% LOD0 | convex hull, <1% visual tris |
| **Buildings** | ≤ 10,000 tris (3k–10k) | 10k–30k | > 30,000 | ~40% | ~15% | convex hull, <1% |
| **Props (future)** | 200–1,000 | 1k–3k | > 3,000 | — | — | hull |

Generators overshoot massively (Hunyuan/TRELLIS emit 100k+ via marching cubes — CONFIRMED), so the fix loop's first move is always decimate-to-target.

---

## 6. Setup cost summary (see structured setup plan)

Rough disk footprint: ComfyUI portable + Python/CUDA ~6GB; SDXL fine-tune + VAE ~7GB; Hunyuan3D-2 shape models (mini+standard) ~4GB; Stable Audio Open (small + 1.0) ~5GB; IP-Adapter + ControlNet + CLIP-Vision ~4GB; Blender ~1GB; Node/gltf-transform/trimesh/ffmpeg ~0.5GB; optional TRELLIS.2 GGUF Q4 ~4GB. **Total ~30–36GB on D:.** Set a **large Windows pagefile on D:** to survive any offload spikes.


---

## Appendix A — Recommended stack (per stage)

| Stage | Tool | Why |
|---|---|---|
| Orchestration backbone | ComfyUI (Windows portable, NVIDIA) + comfy-cli, driven headless via /prompt + /ws + /history + /view | Single headless API surface for 3D shape, 2D, and audio; snapshot lockfiles give reproducibility; load models per-stage to respect the 16GB-RAM ceiling. GPL-3.0 tool, does not encumber outputs. |
| 3D geometry (PRIMARY) | Hunyuan3D-2 SHAPE-ONLY via native ComfyUI nodes (0.6B mini ~5GB / standard ~6GB), texturing SKIPPED | ONLY 3D path with primary-source-confirmed VRAM fit on a 12GB 3060 (5-6GB shape). The refuted full-pipeline (16GB VRAM / 24GB RAM offload) is avoided. Engine overrides per-asset PBR with shared material + global cel-shade, so baked texture is wasted anyway. Hunyuan3D 2.0 weights = Apache 2.0 (clean commercial). |
| 3D geometry (BACKUP / auto-failover) | TripoSG (MIT, ~6GB) via diffusers script; TripoSR (MIT) for instant drafts/placeholders | Cleanest license of the strong options, light VRAM, easy on 16GB RAM; the re-roll loop fails over to it when Hunyuan OOMs or produces bad topology. |
| 3D geometry (OPTIONAL upgrade - VERIFY VRAM) | TRELLIS.2-4B (MIT) via ComfyUI-Trellis2-GGUF, Q4 quant | Cleanest license (MIT, no MAU/geo strings) and SOTA quality. Q4 ~6GB is very likely fine; the 'Full ~11GB' figure is UNCONFIRMED on a 12GB 3060 (measured on a 16GB card by a third-party blog). Install and watch nvidia-smi before relying on it; ~7-10 min/model on consumer GPU. |
| Retopo / normalize / export (BACKBONE) | Blender 4.x headless (bpy): decimate, origin-to-feet, +Z, single-material, convex hull, PLAIN GLB export | CPU-only, trivial on this rig, does everything in one scriptable pass. Plain uncompressed GLB is MANDATORY: Godot 4.6.2 runtime rejects KHR_mesh_quantization/EXT_meshopt_compression/Draco (empirically CONFIRMED on the exact build). GPL tool; exported assets are yours. |
| GLB cleanup / validation / LODs | gltf-transform (donmccurdy, MIT): discrete weld/simplify/join/dedup/prune + inspect/validate; NEVER 'optimize' | Discrete commands keep the GLB extension-free and Godot-loadable; 'optimize' adds Draco+webp which fail runtime load. inspect/validate gives machine-readable tri/material/texture-dim metrics for the QA gate. gltfpack DEMOTED (default quantization breaks Godot runtime). |
| 2D concept images + style lock | SDXL (commercial-OK fine-tune) in ComfyUI + IP-Adapter Plus (Style Transfer) + ControlNet; optional Kohya/OneTrainer style LoRA later | FLUX fp8 stalls 10-20min and freezes Windows on this exact 12GB/16GB rig (CONFIRMED) - SDXL runs in <12GB with no RAM thrash and has the deep LoRA/ControlNet/IP-Adapter ecosystem for consistency. IP-Adapter gives zero-training day-one style lock. |
| Seamless terrain textures (4 biomes) | SDXL + ComfyUI-seamless-tiling (spinagon, circular padding) | Circular-padding tiling works ONLY on UNet (SDXL), NOT on DiT models (FLUX/SD3.5/Qwen) - so terrain REQUIRES SDXL. Generate 1024 -> tile-upscale to 1k-2k keeping circular padding; keep albedo flat (global cel-shade overrides PBR). |
| SFX (7 mono .ogg) | Stable Audio Open SMALL (341M, diffusers) -> ffmpeg mono .ogg; escalate to SAO 1.0 with enable_vae_slicing() for explosion/death | Trivially under 12GB/16GB (no VAE-decode OOM like the 1.1B model), 44.1kHz, Stability markets it as better at SFX than music. Stability Community License = free commercial use under $1M revenue (CONFIRMED), free registration required. AudioGen REJECTED (CC-BY-NC + ~16GB VRAM). |
| Audio post / format | ffmpeg (libvorbis): -ac 1 mono downmix, silenceremove trim, -q:a 4; ffprobe for QA | Single deterministic CLI per file; converts WAV to the mono .ogg Vorbis contract and trims Stable Audio's trailing silence; ffprobe/astats validate mono/duration/peak for the re-roll gate. |
| Self-QA programmatic gate | trimesh (MIT) + manifold3d (Apache-2.0) for tri/watertight/winding/bounds/material; Blender for ortho render + repair | CPU-only, fast, fail-cheapest-first; trimesh auto-fixes minor faults; Blender renders the 3 ortho thumbnails the agent inspects with vision. pyrender avoided (EGL/OSMesa not Windows-native). |
| Self-QA authoritative in-engine gate | Godot headless GLTFDocument.AppendFromFile->GenerateScene / godot-mcp godot_validate_meshes | Tests the asset through the EXACT runtime ingest path, catching silent winding/dropped-tri/degenerate-UV/NaN-normal corruption that the numeric gates miss; anything that would hit the box-placeholder fallback is caught before shipping. Godot MIT, MCP already present in the project. |

## Appendix B — Costed setup plan (ordered)

| # | Action | Download | Notes |
|---|---|---|---|
| 1 | Install ComfyUI Windows portable to a no-spaces path (D:\ComfyUI). Pin the bundled Python (3.12) + CUDA (cu124/cu126) to match the prebuilt wheels you'll need later. Install comfy-cli. Confirm run_nvidia_gpu.bat launches and /prompt responds. | ~6 GB | Foundation for all three media types. One-time GUI step: enable Dev mode + 'Save (API Format)' so workflows export as API JSON. Keep everything on D:. |
| 2 | Set a large Windows pagefile on D: (e.g. 32-48GB). Close Godot editor + browser during heavy gen runs. | 0 GB | 16GB system RAM is the binding constraint. The pagefile is the safety net for any offload spike; do not skip. |
| 3 | Install Blender 4.x + verify headless bpy: blender -b -P test.py; confirm export_scene.gltf kwargs (export_yup, export_apply, export_draco_mesh_compression_enable), origin_set, and bmesh.ops.convex_hull exist via bpy.ops.export_scene.gltf.__doc__. | ~1 GB | CPU-only backbone. All APIs CONFIRMED present in 4.x. modifier_apply dropped apply_as in 2.90 - use the 4.x signature. |
| 4 | Install Node.js + 'npm i -g @gltf-transform/cli'; install Python deps (trimesh, manifold3d, numpy, soundfile, diffusers, torch+cu, Pillow); install ffmpeg (static Windows binary on PATH). | ~2 GB | The CPU validation/conversion layer. Verify gltf-transform inspect/validate emit --format md. |
| 5 | Download Hunyuan3D-2 shape models (0.6B mini + standard shape) into ComfyUI; load the native Hunyuan3D nodes. Run one throwaway concept-image -> shape -> .glb to confirm ~5-6GB peak on nvidia-smi. | ~4 GB | PRIMARY 3D path. SHAPE-ONLY - do NOT install the texture stage (refuted to fit). Hunyuan3D 2.0 = Apache 2.0. |
| 6 | Download an SDXL commercial-OK fine-tune (Juggernaut/RealVisXL) + SDXL VAE + IP-Adapter Plus (SDXL) + CLIP-Vision ViT-H + 1-2 ControlNets (depth/lineart). Install ComfyUI-seamless-tiling. | ~9 GB | 2D concept-image + terrain-texture engine. Verify each model card permits commercial use. Add SDXL-Lightning/DMD2 LoRA for fast drafting. |
| 7 | Download Stable Audio Open SMALL (and optionally 1.0). Run a 7-prompt diffusers batch -> WAV -> ffmpeg mono .ogg; confirm sub-12GB VRAM and clean mono output. | ~5 GB | REGISTER with Stability AI for commercial use (free, mandatory). Use enable_vae_slicing() if you ever run the 1.0 model. |
| 8 | Install TripoSG (MIT) diffusers script as the geometry failover; optionally TripoSR for instant drafts. | ~5 GB | Auto-failover when Hunyuan OOMs / yields bad topology. Cleanest license backup. |
| 9 | OPTIONAL: install ComfyUI-Trellis2-GGUF (Q4 first) and benchmark peak VRAM on nvidia-smi with a real image-to-3D run on the 3060 BEFORE trusting it. | ~4 GB | UNCERTAIN claim: Full ~11GB unconfirmed on 12GB. If Q4 holds under ~8GB peak with headroom, promote to quality-upgrade path; else stay on Hunyuan shape-only. MIT license. |
| 10 | Empirically confirm the Godot runtime rejects compressed/quantized GLB: run a gltfpack-default (quantized) GLB through GLTFDocument.AppendFromFile on the actual 4.6.2 build; confirm it errors and a plain GLB loads. Then wire the no-compression assertion into the QA gate. | 0 GB | Already CONFIRMED empirically in verification (err 43), but re-run on Alec's exact build to lock the export strategy. This is the load-bearing export rule. |
| 11 | Author skills/asset-gen/SKILL.md + scripts + config/engine_profiles/godot_chimera.json. Convert _bmad-output/asset-generation-manifest.md into the structured manifest schema. Prove the full loop end-to-end on ONE throwaway unit (gen -> Blender QA -> Godot ingest -> pass). | 0 GB | Validate the whole chain on one asset before scaling. Then run the 24/7/4 manifest as a resumable overnight batch. |

**Approx total download: ~36 GB** (the optional TRELLIS step is included).

## Appendix C — Open decisions for Alec

1. TRELLIS.2 GGUF as 3D primary vs Hunyuan3D shape-only as primary: TRELLIS.2 has the cleaner MIT license (no MAU cap, no EU/UK/SK carve-out) and better quality, but its sub-12GB VRAM fit on a 3060 is UNCONFIRMED (Full ~11GB was measured on a 16GB card). Decide after Step 9's nvidia-smi benchmark: if Q4 holds comfortably, make TRELLIS.2 primary for license cleanliness; otherwise keep Hunyuan3D-2 shape-only (Apache 2.0, confirmed 5-6GB) as primary.
2. Shape-only (flat albedo, zero baked color) vs occasional baked texture: the engine overrides PBR, so shape-only is the rig-safe default - but confirm with one real in-engine render that an untextured mesh under the global cel-shade + shared material reads acceptably for the RTS look before committing all 24 assets to it. If color/AO is genuinely needed, that's the one case to pay the VRAM cost (or use TRELLIS.2 native PBR).
3. Local-only vs paid-cloud fallback for the 2 abstract UI SFX (ui_click, training_complete): these are the SFX most likely to need many re-rolls from text-to-audio. Decide whether to budget a cheap ElevenLabs PAID plan (free tier is non-commercial - cannot ship) or accept CC0 sourced clicks for those two, vs grinding local re-rolls.
4. Hunyuan3D region/distribution exposure: Hunyuan3D 2.0 is Apache 2.0 (clean), but if any 2.1 path is ever used, its license is void in EU/UK/South Korea and capped at 1M MAU. Confirm Alec stays on 2.0 weights (or TripoSG/TRELLIS MIT) for anything that could ship to those regions.
5. Quality-vs-speed for the 24-asset batch: a full 3D run is an overnight job on the 3060 (especially if TRELLIS.2 at ~7-10 min/model is used). Decide whether to run TripoSR fast drafts first for silhouette approval, then only promote approved concepts to the slow high-quality generator - vs generating high-quality directly and eating more re-roll time.
6. Style-lock effort: ship the MVP on IP-Adapter (zero training, immediate) vs invest the ~3-5h to train an SDXL style LoRA up front. Recommend IP-Adapter first; only train the LoRA once the canonical look is locked and IP-Adapter drift across 24 assets proves unacceptable.
7. Optional RAM upgrade to 32GB: 16GB is the genuine bottleneck. Staying on shape-only/SDXL/SAO-small keeps you within it, but if Alec later wants FLUX-dev concept art, Hunyuan texturing, or TRELLIS.2 Full, a 32GB upgrade removes the swap-thrash ceiling cheaply - decide whether that's worth it now or deferred.

## Appendix D — Adversarial verification verdicts

| Verdict | Claim |
|---|---|
| **REFUTED** | Hunyuan3D-2.0 standard runs the full shape+texture pipeline in ~12GB VRAM (mini ~5GB / shape ~6GB), fitting an RTX 3060 12GB, while 2.1 needs 21GB texture / 29GB combined and its low-VRAM mode requires >=24GB system RAM (exceeding 16GB). |
| **CONFIRMED** | Godot 4.6.2's runtime GLTFDocument.AppendFromFile cannot decode meshes using KHR_mesh_quantization, EXT_meshopt_compression, or KHR_draco_mesh_compression, so gltfpack default output and gltf-transform optimize output fail to load and hit the box-placeholder fallback. |
| **CONFIRMED** | FLUX.1-dev fp8 on RTX 3060 12GB + 16GB RAM stalls 10-20 minutes and freezes Windows (system RAM, not VRAM, is the bottleneck), making SDXL the better primary model. |
| **CONFIRMED** | Stable Audio Open (both Small and 1.0) outputs are licensed for COMMERCIAL use under the Stability AI Community License for users under $1M annual revenue. |
| **CONFIRMED** | Native ComfyUI runs Hunyuan3D-2 SHAPE generation at ~5-6GB VRAM (mini 5GB, standard 6GB), Windows-native, exporting .glb - and the rig only needs the shape stage because Project Chimera's shared-material + global cel-shade pass overrides per-asset PBR. |
| **UNCERTAIN** | TRELLIS.2 (MIT) runs on a 12GB RTX 3060 on Windows via the community ComfyUI-TRELLIS2 GGUF nodes at ~6GB (Q4) to ~11GB (Full) peak VRAM, despite Microsoft's official repo stating 24GB+Linux. |
| **CONFIRMED** | All recommended local generators output dense, non-low-poly meshes, so a headless decimation/remesh step is mandatory to meet the reconciled RTS tri budget (units 1k-3k target/15k cap, buildings 3k-10k/30k cap). |
| **CONFIRMED** | Headless Blender bpy can do the listed mesh ops and GLB export with the given kwargs, and the export, origin_set, and convex_hull APIs exist in 4.x |
