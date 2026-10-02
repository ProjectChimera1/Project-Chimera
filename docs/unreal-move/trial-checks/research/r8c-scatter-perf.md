# R8c: Scatter rendering and GPU budget on an RTX 3060 (UE 5.8.3, Lumen off)

Date 2026-10-01. Research and design only: no Unreal process was started (lock rule), nothing was edited except this file. Inputs read: plan C
(`plan-c-runtime-terrain.md` §1, §3.1-3.9, §4 C9/C11/C12, §5, §7), `EXECUTION.md` §2, §3, §7, §8, the ChimeraTerrain source, the look-test README and
`LookTest/out`, the C1/C5/C7 evidence summaries under `evidence/c/`, the 5.8.3 engine source, and the web pages listed in §13.

Tags. **MEASURED** = read from a file in this repo or this PC. **SOURCE** = read in the 5.8.3 engine source. **WEB** = fetched page (reliability noted).
**ESTIMATE** = my arithmetic from stated assumptions. **UNVERIFIED** = only a build or a run settles it; §10 names the run.
Paths: `E` = `D:/Epic Games/UE_5.8/Engine`, `ES` = `E/Source/Runtime`, `T` = `D:/Projects/Chimera-Unreal/ChimeraTerrain`, `LT` =
`D:/Projects/Chimera-Unreal/ProjectChimera/LookTest`. Sibling notes: r8a (PCG), r8b (assets), r8d (integration with `ATerrainActor`).

## 0. Verdict

**Scatter fits D2's bars on this PC if it is built as a budgeted layer, not as free decoration. Target 1.8 ms of GPU at `rts80` (ceiling 2.5 ms before the
governor acts), 1.0 ms average game thread, and a hard cap of about 24k drawn instances / 1.5 M triangles / 1.0 M covered pixels in the `rts80` view.**
The ESTIMATE for the proposed palette is 0.9-2.6 ms (mid 1.8) at `rts80` and 1.2-3.4 ms (mid 2.3) at the worst look shot (the low `closeup` pose, §7). It is a band
because no scatter-only measurement exists yet; §10 is the run that collapses it.

1. **Instancing: one `UInstancedStaticMeshComponent` (ISM, never HISM) per scatter tile per mesh, Mobility Stationary, no collision, no navigation.**
   ISM has static relevance, cached draw commands and GPU instance culling; HISM is forced to dynamic relevance (a CPU tree traversal and mesh-batch gather
   every frame and every shadow view) and rebuilds its cluster tree on edits (§3). The ISM constructor defaults to Movable, which makes VSM re-render the
   primitive every frame; setting Stationary before registration is mandatory for shadow casters (§5).
2. **Rendering: Nanite-enabled opaque meshes for trees, shrubs, rocks and grass clumps, A/B-tested against non-Nanite LOD'd clumps for grass only** (§3.3).
   Reason: the only scatter number measured on this PC (about 900 Nanite meshes inside a 12.7 ms scene) is Nanite; Nanite's visibility buffer does not pay
   quad overdraw for 3-10 px clumps; 5.8 Nanite honours per-component end-cull distance and has a built-in raster time budget (§4).
3. **Do not enable Nanite Foliage (assemblies, voxels, skinning wind) for the trial.** Experimental in 5.8, read-only project setting that changes shader
   permutations and TSR behaviour, aimed at multi-million-triangle hero trees, incompatible with WPO wind and alpha masking, needs authored assembly and
   skeletal assets (§3.4). At `rts80` a 12 m tree is only about 40-70 px tall (foreshortened by the 50 degree pitch): ordinary Nanite handles it.
4. **Shadows: grass and wildflowers do not cast; trees, shrubs and rocks cast through VSM, static-cached, with no WPO on casters** (or `Rigid` invalidation
   if wind is wanted, UNVERIFIED). Contact shadows stand in for grass shadows (Epic's own VSM advice, §5).
5. **Materials: opaque for everything solid; masked only for sparse leaf cards on near LODs; never translucent** (§6). Wind only on non-casting grass,
   disabled beyond about 45 m.
6. **No impostors, no HLOD** in the trial: at the proposed caps (about 400 visible trees) a mesh LOD chain or Nanite costs about 0.3 ms, below what a Blueprint
   driven ImpostorBaker pipeline would save, and r8b already rules the plugin out on the text-first rule (§4.4).
7. **Budget split at `rts80`, 1080p, GPU p50, target 15.2 ms (about 66 fps GPU-bound, leaving margin to 60):** fixed world 8.7 (MEASURED) + terrain 2.9
   (MEASURED, bar 3.0) + scatter 1.8 + 1,000 units 1.4 (ESTIMATE, C9 measures) + spare 0.4 (§8). The fixed world is the real squeeze: 8.7 ms before any geometry.
8. **Governor ladder** (all presentation-only, none touches placement): rank-tier visibility, cull distances, `r.Nanite.PrimaryRaster.TimeBudgetMs`, LOD bias,
   caster flags (§8.3). **Kill rule:** scatter is not merged into the packaged look if `scatter_gpu_ms` (visible minus hidden, same method as P4) exceeds 2.5 ms at
   `rts80` after the ladder, or C1US median falls under 60 / 1%-low under 45.

## 1. Evidence base

| # | Fact | Value | Source |
|---|---|---|---|
| E1 | `idle_hidden` GPU p50 (terrain hidden, `look full`, 0 units): sky, clouds, atmosphere, sky-light capture, SSGI, post, TSR, VSM base | 8.63 / 8.85 / 8.86 ms (c1_mat, c1_look_r0, c1_look_r0b) | MEASURED `evidence/c/c-C7-c1-mat-summary.txt`, `c-G1-c1-look-r0-summary.txt` |
| E2 | `idle_visible` GPU p50 at `rts80` with the reworked ground | 11.63 (C7) / 11.74 / 11.80 ms (ground-look r0); terrain = 2.93-2.96 ms | same files; P4 line "2.926 ms <= 3.0" (not a `-Measure` run, so reported) |
| E3 | Frame at `idle_visible`: GT 2.1 ms, RT 12.2 ms, frame 12.0-12.3 ms (81-84 fps), 1%-low 66-75 fps; 295,579 triangles drawn (`RHI/PrimitivesDrawn`, 204,800 of them the terrain) | the frame is GPU-bound | same files |
| E4 | Look-test A_noLumen: 76.2 fps, GPU 12.7 ms, GT 4.0, RT 13.1, VRAM 3.1 GB, with 58 units, 10 buildings and about 900 Nanite scatter meshes (450 trees, 400 undergrowth, 40 boulders) spawned as 900 separate static-mesh actors, flat ground | 2 runs, 1,650-frame window, spread 0.7 % | MEASURED `LT/out/fps_summary.json`, `docs/unreal-move/look-test-2026-10-01/README.md`, `results.json` `manifests.A_noLumen.counts`, `tools/unreal-looktest/lt_build.py:262-275` |
| E5 | Same scene with B (no GI, no clouds, softer sun): GPU 9.0 ms, 107.9 fps. A_noLumen minus B = 3.7 ms: an upper bound for what SSGI + clouds + cloud shadows + the A sun cost | MEASURED same files |
| E6 | Subtracting E1 (8.7) from E4 (12.7) leaves about 4.0 ms for 58 skinned units, 10 buildings, 900 scatter actors, their VSM work and a flat ground plane. Looking only at scatter that is roughly 2.0-3.0 ms. Different projects, different ground, so a sanity bound, not a measurement | ESTIMATE |
| E7 | The terrain trial's own cost per brush tick: p50 0.12 ms (d20), 1.07 ms (d100); GT total 2.1 ms | MEASURED `c-G1-c1-look-r0-summary.txt` |
| E8 | RTX 3060 12 GB: 3,584 CUDA cores, 1.78 GHz boost, 192-bit GDDR6 (WEB nvidia.com), so 12.8 FP32 TFLOPS and 360 GB/s at 15 Gbps (arithmetic); a GA106 has 3 GPCs (general knowledge, not fetched) | WEB / ESTIMATE |

Consequences. 60 fps median means 16.67 ms per frame. After E1 + E2 (8.7 + 2.9 = 11.6 ms) **5.0 ms of GPU remain for scatter, 1,000 units, sculpting
transients and margin.** The look-test was not run with scatter hidden, so there is no scatter-only figure; E6 says to expect 2-3 ms for 900 *individual* actors, which
ISM tiles with distance culling should beat, not match. Both r8d (scatter <= 2.5 ms) and this note agree on 2.5 ms as the ceiling; this note adds a 1.8 ms target
because 2.5 + 1.4 (units) + 8.7 + 2.9 = 15.5 ms leaves under 1 ms against 16.67 ms once frame overhead (about 0.3-0.5 ms between GPU time and frame time in E2/E3) is added.

## 2. The views: how much ground, how many pixels

ESTIMATE from camera geometry on a flat ±160 m map (script in Appendix A; 1920x1080, UE yaw 90 looks +Y, vFOV 75 gives hFOV 107.5).

`rts80` = `CAM_Gameplay` (0, -51.42, 61.28) m, pitch -50: **the nearest ground is 61 m away** (bottom-edge ray), the top edge overshoots the map.

| slant distance (m) | ground m² | pixels | px per m² | m per pixel |
|---|---|---|---|---|
| 60-80 | 3,879 | 551,186 | 142 | 0.084 |
| 80-100 | 5,343 | 458,228 | 86 | 0.108 |
| 100-130 | 8,566 | 359,212 | 42 | 0.154 |
| 130-160 | 9,532 | 209,882 | 22 | 0.213 |
| 160-200 | 14,521 | 183,746 | 12.7 | 0.281 |
| 200-300 | 14,703 | 110,034 | 7.5 | 0.366 |

90.3 % of the pixels see map ground; the visible ground is 56,545 m² (55 % of the 102,400 m² map); mean 33 px/m²; 49 % of all pixels lie inside 100 m slant.
Zoomed in to 40 m distance the ground is 25,515 m² at 81 px/m². Other poses (same method):

| pose | on-map pixels | ground m² | note |
|---|---|---|---|
| `CAM_Close` (look-test: target (-2,2), 30 m, pitch -40, vFOV 40) | 100 % | 2,001 (2 % of the map) | 1,036 px/m², 0.023 m/px at 20-40 m |
| trial `closeup` (`RtsCameraPawn.cpp:30-37`: 3.5 m up, pitch -7, vFOV 60) | 59 % | 37,644 | near-horizontal view of the meadow: worst case for scatter |
| trial `oblique` (`RtsCameraPawn.cpp:21-27`: 25 m up, pitch -13, vFOV 50) | 63 % | 50,010 | |

What this means for design. At `rts80` a 0.6 m grass clump is 1.6 px wide at 200-300 m and 7 px at 60-80 m; a 12 m tree is about 70 px tall at 100 m slant and 40 px at 200 m (foreshortened by the 50 degree pitch: apparent height 12 m x cos(elevation)). **Grass geometry only
reads in the bottom half of the screen (inside about 100 m slant, 49 % of pixels); beyond that it is colour and the ground material's job** (this agrees with r8b §0 item 4).
Trees, shrubs and their shadows are what make the silhouette at `rts80`.

## 3. Instancing choice

### 3.1 HISM vs ISM (SOURCE)
- ISM is static-relevance; HISM is not. `FInstancedStaticMeshSceneProxy::GetViewRelevance` keeps the static path (`ES/Engine/Classes/Engine/InstancedStaticMesh.h:453-467`, dynamic only for
  editor-selected instances); `FHierarchicalStaticMeshSceneProxy` forces `bDynamicRelevance = true; bStaticRelevance = false` (`ES/Engine/Private/HierarchicalInstancedStaticMesh.cpp:899-901`) and builds
  its mesh batches per view in `GetDynamicMeshElements` from a CPU tree traversal (`:1509-1567`, counters `STAT_FoliageTraversalTime` at `:156`). It sets `bVisibleInLumenScene = false` ("Dynamic draw path without Nanite
  isn't supported by Lumen", `:806`; irrelevant here, Lumen is off) and returns false from `GetInstanceDrawDistanceMinMax` because it culls on the CPU (`:2022-2031`).
- HISM is for static, rarely edited foliage: it keeps a cluster tree (`HierarchicalInstancedStaticMeshComponent.h:134-195`: `ClusterTreePtr`, `NumBuiltInstances`, `UnbuiltInstanceBounds`,
  `bAutoRebuildTreeOnInstanceChanges`) and unbuilt instances "will always be rendered" until the async rebuild applies. For scatter that follows sculpting, a rebuild per edit is the wrong cost.
- 5.8 gives plain ISM a spatial-hash CPU culling pass: `r.SceneCulling.HierarchicalCPUCulling` (default true, `ES/Renderer/Private/SceneCulling/SceneCulling.cpp:172-181`; also `Engine 5.8 performance highlights`, WEB, tomlooman.com), which
  uses `MaxInstanceDrawDistance` from the proxy (`SceneCulling.cpp:2819-2824`). That removes the one thing HISM used to offer ISM users. `r.SceneCulling.CullChunkViewDistance` (default false,
  `SceneCullingRenderer.cpp:23-27`) additionally rejects far cells on the CPU; worth one A/B in §10 (UNVERIFIED).
- Counts: Epic's Nanite page says the scene's hard limit is 16 million instances (WEB, dev.epicgames.com Nanite page); our candidates are about 220k for the whole map (§7), so the limit is irrelevant.
- `foliage.DensityScale` and `grass.DensityScale` only scale HISM components that opted in (`HierarchicalInstancedStaticMesh.cpp:136-200`, `bEnableDensityScaling`); ISM tiles ignore them, so the density
  governor must be ours (§8.3). `BaseScalability.ini:964-996` sets them per FoliageQuality group (0 / 0.4 / 0.8 / 1.0), another reason not to rely on them.

### 3.2 Per-component settings that decide cost (SOURCE)
| Setting | Value to use | Why | Where |
|---|---|---|---|
| Mobility | **Stationary** (or Static), set before `RegisterComponent` | ISM ctor sets Movable (`InstancedStaticMesh.cpp:2360`); a Movable primitive is cached as dynamic in VSM (`PrimitiveSceneProxy.h:771-774`, `VirtualShadowMapCacheManager.cpp:578-586`) | see §5 |
| Collision | `SetCollisionEnabled(NoCollision)` and `bDisableCollision = true` (public member, `InstancedStaticMeshComponent.h:229`) before registration | otherwise one physics body per instance (`InstancePhysicsBodies`, `InstancedStaticMesh.cpp:2358`) | |
| Navigation | `bUpdateNavigation = false` on `AddInstances(...)` (`InstancedStaticMeshComponent.h:275`), `SetCanEverAffectNavigation(false)` | | |
| GPU LOD selection | leave `bUseGpuLodSelection = true` (default, `InstancedStaticMesh.cpp:2370`; cvar `r.InstancedStaticMeshes.GpuLod` = 1, `:90-95`) | this is what Manor Lords' patch note "GPU based LOD for Instanced Components" is (§11) | |
| Casting | grass/flowers: `CastShadow = false` (or `bCastDynamicShadow = false`), `bCastContactShadow` per taste; casters per §5 | `PrimitiveComponent.h:568,587` | |
| Distance field | `bAffectDistanceFieldLighting = false`, `bAffectDynamicIndirectLighting = false` | `r.GenerateMeshDistanceFields=True` stays in `T/Config/DefaultEngine.ini:10` for parity; Lumen is off, so nothing needs scatter in DF scenes; avoids cook-time DF builds mattering at runtime | `PrimitiveComponent.h:556,564` |
| Occluder | grass `bUseAsOccluder = false` | do not feed grass to the depth prepass as an occluder | `PrimitiveComponent.h:513` |
| Decals | `bReceivesDecals = false` | | `PrimitiveComponent.h:489` |
| End cull distance | per component (`SetCullDistances(Start, End)`), see §4 | GPU per-instance distance cull | `InstancedStaticMeshComponent.h:204-217,443` |
| Custom data | 0-1 floats per instance only if the material needs it (`SetNumCustomDataFloats`) | per-instance cost in GPUScene | `:181` |

### 3.3 Nanite ISM vs non-Nanite ISM (the grass A/B)
- Engine facts. Imported static meshes are Nanite when the asset says so; a component can opt out per component with `bDisallowNanite` (`ES/Engine/Classes/Components/StaticMeshComponent.h:166`). ISM creates a Nanite
  proxy when the mesh is Nanite (`InstancedStaticMesh.cpp:2588-2605` per R3 §4.1). Nanite supports ISM and HISM (WEB, Nanite page). Masked materials are allowed (`r.Nanite.AllowMaskedMaterials` = 1,
  `ES/Engine/Private/Rendering/NaniteResources.cpp:142-144`) but Epic states the cost: "negative space in alpha maps adds additional overdraw cost" and masked Nanite shades by recomputing barycentrics per pixel (WEB, search summary of the
  Nanite docs; Epic's Nanite Foliage page: "Alpha Masking: ... Nanite doesn't work well with it since it introduces a lot of overdraw").
- Why Nanite first for grass clumps at this camera: a 40-60 triangle clump covers about 6-12 px at 60-100 m, so triangles are 1-3 px. Classic raster shades 2x2 quads per triangle: ESTIMATE of lane waste 4-7x for such triangles
  (48 triangles over 36 px). Nanite's software rasteriser and visibility buffer avoid that. Opaque meshes also stay on the cheap Nanite path (no programmable raster).
- Why a non-Nanite option still earns its A/B: it supports gentle WPO wind (Nanite WPO falls back to programmable raster and splits clusters, WEB Nanite page: "Nanite meshes using WPO displacement are split into smaller
  clusters"), has the end-cull *fade* (`PerInstanceFadeAmount`, `InstancedStaticMesh.cpp:1781-1790` only for the ISM proxy, not overridden by Nanite's `FSceneProxy`, `NaniteResources.cpp:1846-1870`), and costs nothing in Nanite
  budget. Decision rule: build both clump variants (the second is `bDisallowNanite = true` with a generated 3-LOD chain from the same source), compare `grass_gpu_ms` at `rts80` and trial `closeup`; lower wins; ties go to Nanite (no LOD authoring).
- Do not let the A/B hold the schedule: the default (Nanite) is what the look-test already ran.

### 3.4 Nanite Foliage in 5.8: status and why not now
- It is a project setting "Nanite Foliage (Experimental)" (`ES/Engine/Classes/Engine/RendererSettings.h:1549-1553`, `ConfigRestartRequired`), cvar `r.Nanite.Foliage`, default 0, read-only (`NaniteResources.cpp:118-122`);
  assemblies (`r.Nanite.AllowAssemblies`), voxels (`r.Nanite.AllowVoxels`) and curves default 0 and read-only too (`:124-140`). Any of them changes which Nanite and TSR shader permutations build
  (`ES/RenderCore/Private/RenderUtils.cpp:1372-1385`) and turns on TSR thin-geometry detection by default (`ES/Renderer/Private/PostProcess/TemporalSuperResolution.cpp:331-340,499-501`), an extra TSR pass on every frame.
- Epic's page (WEB, dev.epicgames.com/documentation/unreal-engine/nanite-foliage, fetched 2026-10-01): "Learn to use this Experimental feature, but use caution when shipping with it"; assemblies up to 65k part instances;
  skinning wind "one-hundred thousand bones updating, which takes about 0.1 milliseconds on the GPU"; **WPO "isn't a good match"**; **alpha masking "introduces a lot of overdraw"**; no collision (Dynamic Wind plugin); VSM needs animation
  disabled at distance. The tree example is "41 million triangles, 12 unique assembly parts, 2160 assembly part instances, 850 skeletal bones".
- 5.8 release notes (WEB, search summary of the 5.8 notes, tomlooman.com 5.8 page): "Nanite Foliage rendering paths have moved to an experimental state"; new `Nanite Pixel Programmable Distance` on foliage types
  (the property is `StaticMeshComponent.h:411-412`); WPO disable distance and `Evaluate World Position Offset` now exist for skinned meshes.
- Measured claims on the web are about hero trees: 62 to 119 fps with 77,376 trees of 20 M triangles each (80.lv, no GPU or resolution given); 500k instances of dozens of tree variants in the Witcher 4 demo (search
  summary). None is a 3060 number, none concerns 6-px grass clumps. A third-party benchmark post (strayspark.studio) refuses to publish numbers; treat as opinion.
- Needs authored assets we cannot make text-first (Procedural Vegetation Editor and Dynamic Wind are editor plugins, r8b §0 item 3). **Defer to after the trial; record as the upgrade path for hero trees at close zoom.**

## 4. Culling, LOD and impostors

### 4.1 Per-instance distance culling (SOURCE)
- ISM: `InstanceMinDrawDistance`, `InstanceStartCullDistance`, `InstanceEndCullDistance` are component properties (`InstancedStaticMeshComponent.h:204-217`); the proxy reports
  `OutDistanceMinMax = (MinDraw, EndCull)` when `EndCullDistance > 0` (`InstancedStaticMesh.cpp:1767-1778`) and `PrimitiveSceneProxy.cpp:870-874` writes it into the GPU-scene primitive data, so the culling kernel rejects instances beyond it.
  `StartCullDistance > 0` adds a fade factor for the material (`GetInstancingFadeOutParams`, `:1781-1790`; `PerInstanceFadeAmount` node, Epic Foliage Mode page WEB).
- **Doc vs source.** Epic's Foliage Mode page says "Nanite-enabled meshes are not affected by culling distance and instance fading". In 5.8 source the Nanite proxy does return a draw-distance range (`NaniteResources.cpp:1846-1856`) and the Nanite culling shaders test it
  (`E/Shaders/Private/Nanite/NaniteInstanceHierarchyCulling.usf:139-180`, `NaniteCullingCommon.ush:441-442`), so the end distance cuts
  Nanite instances; the fade is the part that is missing (only the ISM proxy overrides `GetInstancingFadeOutParams`, `InstancedStaticMesh.h:512`, `.cpp:1781`). Expect a hard cut at the end distance for Nanite grass (UNVERIFIED; the A/B in §10 looks at it).
- Taper without a fade: split a species' instances into rank bands, each its own ISM with its own end distance (**rank tiers**). Rank = a hash of the candidate's world cell, species and slot (r8d's deterministic lattice), so the instance
  set stays camera-independent and identical after reload. Proposed grass: T0 = 45 % of instances end 130 m, T1 = 30 % end 95 m, T2 = 25 % end 65 m, giving 100 % density inside 65 m, 75 % to 95 m, 45 % to 130 m, 0 beyond. At `rts80` that is
  280 + 4,239 + 14,408 = 18.9k clumps against 19.7k for the linear taper used in §7. At `rts80` T2 is nearly idle (nearest ground 61 m), at closer zooms it carries the near field. It costs 3x primitives for that one species.
- Frustum culling per tile: each tile ISM is a primitive with box bounds, so off-screen tiles never reach the instance kernel. At `rts80` about 55 % of the map's ground is visible.

### 4.2 LOD (SOURCE)
- Non-Nanite: GPU LOD selection per instance by screen size (`bUseGpuLodSelection`, `InstancedStaticMesh.cpp:1547-1548`, `GetGpuLodInstanceRadius` `:1797-1800`); `InstanceLODDistanceScale` (`InstancedStaticMeshComponent.h:198-202`) and
  the global `foliage.LODDistanceScale` (`HierarchicalInstancedStaticMesh.cpp:95-103`, HISM only for the cvar) bias it. VSM and Epic's doc require "full Mesh LOD hierarchies" on non-Nanite meshes or they become "extremely expensive" in shadow pages (WEB, VSM page).
- Nanite: automatic. The relevant dials are `r.Nanite.MaxPixelsPerEdge` (default 1.0, `ES/Renderer/Private/Nanite/NaniteCullRaster.cpp:133-138`) and **a 5.8 time-budget governor**: `r.Nanite.PrimaryRaster.TimeBudgetMs` and `r.Nanite.ShadowRaster.TimeBudgetMs`
  (default disabled, `:344-354`) scale the pixel error when over budget, down to `r.Nanite.PrimaryRaster.PixelsPerEdgeScaling` = 30 % (primary, i.e. up to 3.3x coarser) and 100 % (shadow, i.e. none) (`:329-342`). It is the cheapest safety net: set the
  primary budget to the Nanite raster share of the plan (about 1.5 ms) and the engine degrades triangle density instead of frame rate. Presentation-only; sim untouched. UNVERIFIED in a packaged Shipping build (cvar `Set` from C++ is expected to work, ini works).
- Nanite WPO has its own disable distance (`r.Nanite.Culling.WPODisableDistance`, `NaniteCullRaster.cpp:294-299`).

### 4.3 Scalability groups change these numbers
`BaseScalability.ini` sets `r.Shadow.RadiusThreshold` 0.06 / 0.05 / 0.04 / 0.01 / 0 and VSM `MaxPhysicalPages` 512 / 512 / 2048 / 4096 / 8192 for ShadowQuality 0 / 1 / 2 / 3 / cine (`:138,173,208,246,284`, `:146,181,219,257,295`). A packaged
game that auto-detects group 3 (Epic) draws smaller shadow casters than group 2 (High). The engine default 2048 pages is `VirtualShadowMapArray.cpp:173-182`. **Pin the group in `DefaultScalability.ini` or `GameUserSettings` for measured runs**, otherwise the editor, `-game` and the package can differ (UNVERIFIED which group this PC auto-selects).

### 4.4 Impostors and HLOD
- `ImpostorBaker` (`E/Plugins/Experimental/ImpostorBaker`): editor content is Blueprints and Editor Utility Widgets (`BP_Generate_ImpostorSprites`, `EUW_Impostor_Editor`), material functions `Impostor_*`, `HemiOctahedronToUnitVector`; depends on GeometryScripting.
  It breaks constraint 4 (no Blueprints) unless wrapped in a script, and r8b already excludes it. HLOD in 5.8 is World-Partition-centric; the trial map is a runtime-built actor, not a partitioned level.
- Do the arithmetic before authoring anything: at `rts80`, 424 trees at an LOD1 of 800 triangles = 0.34 M triangles = about 0.15-0.3 ms (§7 coefficients). An octahedral impostor pays off at thousands of visible trees. The cap here is about 300-400.
- If distant trees ever need cheaper silhouettes, generate a single cross-card LOD with Python (`StaticMeshEditorSubsystem` LOD reduction) rather than baking sprites.

## 5. Shadows, WPO and VSM

### 5.1 What invalidates a VSM page (SOURCE + WEB)
- Epic (WEB, VSM page): "Geometry that can be deformed using Skeletal animation, or materials using World Position Offset or Pixel Depth Offset always invalidates cached pages every frame." and for grass "using only Contact Shadows is a sufficient
  substitute for high resolution shadow maps"; options: LOD materials without WPO at distance, no dynamic shadow for distant objects, WPO Disable Distance, and `Shadow Cache Invalidation Behavior` Auto / Always / Rigid / Static
  (`ES/Engine/Public/SceneTypes.h:220-235`: Rigid = "Suppresses invalidations that would otherwise be generated by e.g. WPO"; Static additionally suppresses transform-change invalidations).
- Code path. A primitive is a "good candidate for cached shadow maps" only if `r.Shadow.CacheWPOPrimitives` = 1 or its materials use no WPO and no displacement (`StaticMeshSceneProxy.cpp:424`; cvar default 0, `PrimitiveSceneProxy.cpp:55-61`). Otherwise
  `IsMeshShapeOftenMoving()` is true (`PrimitiveSceneProxy.h:771-774`) and `Added` marks it `CachePrimitiveAsDynamic` (`VirtualShadowMapCacheManager.cpp:578-586`): it is re-rendered into the dynamic layer instead of the cache. `Rigid`/`Static` set
  `DisableMaterialInvalidations` in the GPU-scene primitive flags (`PrimitiveSceneProxy.cpp:823`). `r.Shadow.Virtual.Cache.FramesStaticThreshold` = 100 frames before an object counts as static (`VirtualShadowMapCacheManager.cpp:142-146`).
- Non-Nanite primitives that are CPU-culled and then "revealed" invalidate their pages when they come back (`VirtualShadowMapCacheManager.cpp:1281-1301`, `GVSMCacheDebugSkipRevealedPrimitivesInvalidate = 0`). Nanite casters avoid this CPU path: a second reason to prefer Nanite for trees on a panning RTS camera (UNVERIFIED how often it triggers for tile primitives).
- Clipmap WPO distance: `r.Shadow.Virtual.Clipmap.WPODisableDistance` = 1 and `.LodBias` = 3 (`VirtualShadowMapClipmap.cpp:99-112`) disable WPO in coarse clipmap levels based on a primitive's WPO disable distance. `r.Shadow.Virtual.DeferredInvalidationBudget` (5.8, default -1 = unbounded,
  `VirtualShadowMapArray.cpp:119-127`) spreads Nanite LOD-delta invalidation over frames.
- Non-Nanite shadow cost: instances are culled per clip level; `r.Shadow.Virtual.NonNanite.IncludeInCoarsePages` = 1 ("rendering non-Nanite geometry into large coarse pages can be expensive; disabling this can be a significant performance win",
  `VirtualShadowMapArray.cpp:437-443`); `r.Shadow.Virtual.NonNanite.UseRadiusThreshold` = 1 applies `r.Shadow.RadiusThreshold` to uncached maps (`:497-503`; the threshold itself `ShadowSetup.cpp:117-123`, default 0.01 in the
  engine, overridden by scalability). A third-party note (ibbles/LearningUnreal, WEB) recommends `IncludeInCoarsePages 0` for non-Nanite foliage; we have none if casters are Nanite.

### 5.2 Choices for scatter
| Layer | Casts VSM shadow | WPO | Mobility | Why |
|---|---|---|---|---|
| grass clumps, wildflowers | no (contact shadows only) | optional sway, **disabled beyond about 45 m** (`WorldPositionOffsetDisableDistance`, `StaticMeshComponent.h:158`), `bWorldPositionOffsetWritesVelocity = false` (`:184`, saves the velocity pass at the cost of slightly wrong motion vectors) | Stationary | thousands of instances; the shadow of a 0.35 m clump is below a VSM texel at `rts80`; with WPO it would invalidate pages every frame |
| shrubs, rocks | yes | none | Stationary | static silhouettes; cached after 100 frames |
| trees | yes | none on the casters; wind only through `Rigid` invalidation (UNVERIFIED that shadows then stay cached while the main view sways) | Stationary | "shadow pools" are what the art director asked for; cached static pages make them nearly free in steady state, an uncached frame costs 1-3 ms (ESTIMATE) |
| terrain | already Movable (plan C §2.9) | | | unchanged |

**Mobility must be set explicitly.** Runtime-created ISMs are Movable (`InstancedStaticMesh.cpp:2360`); the look-test spawned `AStaticMeshActor`s, whose component is Static (`ES/Engine/Private/StaticMeshActor.cpp:34`), so its VSM behaviour is not what a naive runtime ISM gets.
Whether runtime instance edits on a Stationary or Static ISM stay legal and invalidate only the touched instance ranges is UNVERIFIED but supported by the 5.8 instance-update tracking (`ISMInstanceDataManager.cpp`,
`ISMInstanceUpdateChangeSet.cpp`; the only Static-mobility restriction in `InstancedStaticMesh.cpp` is previous-transform data, `:5287`). Test: edit one tile, count invalidated pages (VSM stats, `r.Shadow.Virtual.Stats`).

### 5.3 What the look-test already paid (UNVERIFIED)
The PCG sample foliage material `PCG_Foliage_01` carries WorldPositionOffset and PixelDepthOffset inputs (strings of `PCGBiomeSample/Content/Materials/PCG_Foliage_01.uasset`) and its Broadleaf instance is masked
(`OpacityMaskClipValue`). If the wind input is wired, E4's 900 meshes include per-frame VSM invalidation for the trees. Another reason to read E6 as an upper bound.

## 6. Materials, overdraw and the TSR

- **Blend mode.** Translucent grass is out: per-primitive sorting (a whole ISM tile is one sort key), no VSM casting, post-DOF ordering, large overdraw. Masked is the classic cheap card, but with `r.EarlyZPass` = 3 ("use built in heuristic",
  `ES/Renderer/Private/RendererScene.cpp:145-154`) masked primitives are not reliable prepass occluders, so each overlapping card runs the full material until a nearer opaque pixel wins. Opaque blade geometry is the cheapest per covered pixel
  and early-Z friendly. Use opaque tapered-blade clumps; keep masked for flower petals and near-LOD leaf cards only (a few thousand instances).
- **Overdraw numbers.** The model counts covered pixels including overlap: 1.04 Mpx at `rts80` (50 % of the screen, trees 0.53 Mpx and grass 0.41 Mpx), 2.9 Mpx for `CAM_Close`, 3.8 Mpx for the low trial `closeup`. Pixel cost is where grazing views hurt (§7).
- **Substrate.** The project uses `r.Substrate.ProjectGBufferFormat=0`, the blendable GBuffer ("faster", closures forced to 1, `ES/RenderCore/Private/RenderUtils.cpp:2085-2090`, `T/Config/DefaultEngine.ini:22-24`): a single-slab, two-sided, no-SSS grass material
  fits; do not use thin-translucent or multi-closure foliage BSDFs.
- **Sub-pixel blades and TSR.** Grass at 1-3 px flickers. `r.TSR.ThinGeometryDetection` (default 0, `TemporalSuperResolution.cpp:331-340`) relaxes history rejection for foliage-like pixels at the price of an extra pass; try it only if a flicker shows in the video.
  Prefer bigger, fewer clumps (r8b: author at 2-3x natural size) over more, thinner ones.
- **Pixel programmable distance.** For Nanite masked or WPO materials, `NanitePixelProgrammableDistance` (`StaticMeshComponent.h:411-412`) disables programmable raster beyond a distance: set about 40 m for any masked leaf cards.

## 7. Cost model and estimates

### 7.1 Palette (design caps, per eligible ground; Appendix A)
| layer | density near / at cull (per m²) | eligible ground | tris LOD0 / LOD1 | cull end (slant) | size (m) |
|---|---|---|---|---|---|
| grass clump | 3.0 / 0 (linear from 60 m; tiers in §4.1 approximate it) | 60 % (meadow; paths, rock, snow excluded) | 48 / 12 | 150 m | 0.6 x 0.35 |
| wildflower | 0.5 / 0 | 60 % | 16 / 6 | 130 m | 0.4 x 0.4 |
| shrub | 0.04 / 0.02 | 50 % | 600 / 150 | 300 m | 2.0 x 1.8 |
| rock | 0.01 / 0.01 | 20 % | 400 / 100 | 300 m | 1.5 x 1.0 |
| tree | 0.03 / 0.03 | 25 % (woodland) | 3,000 / 800 | 600 m | 6 x 12 |

The whole map at these densities holds about 218k candidate instances (102,400 m² x 2.13/m²). Memory is a non-issue (tens of MB, r8d §8).

### 7.2 What is drawn (ESTIMATE; geometry from Appendix A)
| view | drawn instances | candidates in visible tiles | triangles | covered px (incl. overlap) | casters |
|---|---|---|---|---|---|
| `rts80` | 23,550 (grass 19,686, flowers 2,430, shrubs 896, rocks 113, trees 424) | 150,500 | 1.46 M | 1.04 M | 1,434 |
| `CAM_Close` (30 m) | 4,258 | 5,300 | 0.25 M | 2.91 M | 59 |
| trial `oblique` | 15,683 | 133,100 | 1.08 M | 2.24 M | 1,201 |
| trial `closeup` (low) | 20,022 | 100,200 | 1.23 M | 3.82 M | 970 |

### 7.3 Cost model (assumptions, not measurements)
`ms = geo x Mtri + pix x Mpx + surv x k_drawn + rej x k_rejected + vsm x (casters / 1,433) + fixed`, low / high:

| term | low | high | basis |
|---|---|---|---|
| geo (ms per M triangles, cull + raster + prepass/base for Nanite, setup for classic) | 0.35 | 0.90 | RTX 3060 about 5 Gtri/s peak setup, 3-4x lower for 1-3 px triangles; Nanite software raster for micro-triangles |
| pix (ms per M covered px, opaque, simple lit, 1.6x overlap included in the px count) | 0.15 | 0.45 | the reworked ground measures about 1.5 ns per pixel all-in (E2: 2.9 ms over 1.87 M ground px, including its VSM and prepass share); a grass material is 3-8x cheaper |
| surv (ms per k drawn instances: cull, cluster traversal, GPU-scene fetch) | 0.002 | 0.005 | 2-5 ns |
| rej (ms per k instances rejected by frustum/distance in the cull kernel) | 0.0005 | 0.002 | 0.5-2 ns; 150k candidates at `rts80` = 0.08-0.3 ms |
| vsm (cached steady state, casters only) | 0.10 | 0.30 | cached pages are read, not drawn; the first frame or an invalidation costs 1-3 ms (transient) |
| fixed (primitives, material bins, GPU-scene upload) | 0.05 | 0.15 | 100-400 primitives |

| view | GPU ms low - high | mid | note |
|---|---|---|---|
| `rts80` | **0.93 - 2.61** | 1.77 | geo 0.5-1.3, pix 0.16-0.47, cull 0.13-0.5, VSM 0.1-0.3 |
| `CAM_Close` | 0.59 - 1.72 | 1.16 | pixel-bound: 2.9 Mpx |
| trial `oblique` | 0.94 - 2.70 | 1.82 | |
| trial `closeup` (low, grazing) | 1.20 - 3.44 | 2.32 | pixel and overdraw bound; reported, not gated (D2) |

Cross-checks. (a) E6: 900 individual actors plus 58 skinned units and 10 buildings cost at most about 4 ms in the look-test; our high end for scatter (2.6) is consistent with "scatter was 2-3 ms there". (b) The ground alone, with 90 % pixel coverage, costs 2.9 ms; a
grass layer covering 21 % of the screen at one-fifth the per-pixel cost is about 0.12 ms of pixel work, so triangles and culling, not shading, set the grass cost at `rts80`: reason to cap triangles and instances rather than pixels. (c) Epic's Nanite Foliage page quotes
0.1 ms for 100k bones; irrelevant to non-skinned clumps but shows the order of magnitude of instance-parallel work on current GPUs.

**Instance counts to hold as caps at `rts80`:** drawn <= 24k (grass+flowers <= 22.5k, shrubs <= 900, rocks <= 120, trees <= 400); triangles (LOD-weighted) <= 1.5 M; covered pixels <= 1.1 M; candidates in visible tiles <= 160k; scatter primitives in the scene <= 700 (the look-test ran 900 individual
Nanite actors with RT 13.1 ms against GPU 12.7 ms, E4, so primitive count was not the bound).

## 8. Budget split that keeps D2's bars

### 8.1 Split at `rts80`, 1080p, GPU p50 (target 15.2 ms; 60 fps needs 16.67)
| item | ms | status | owner of the number |
|---|---|---|---|
| fixed world (sky, clouds, atmosphere, sky-light capture, SSGI, post, TSR, VSM base) | 8.7 | MEASURED (E1) | ground-look and lighting recipe |
| terrain (RMC chunks, ground material, VSM re-render of Movable chunks) | 2.9 | MEASURED (P4 bar 3.0) | C7 / ground-look |
| **scatter** | **1.8** | ESTIMATE (band 0.9-2.6) | this note; grass 0.6, trees + their shadows 0.55, shrubs and rocks 0.3, flowers 0.1, culling and transients 0.25 |
| 1,000 units (ISM `crucible_mortar`, shadows, tint) | 1.4 | ESTIMATE; unmeasured until C9/C12 | C9 |
| spare (hitches, sculpt transients, driver variance) | 0.4 | | |
| total | 15.2 | 66 fps GPU-bound; about 64 fps after 0.3-0.5 ms of frame overhead | |

CPU: GT 2.1 ms measured at idle (E3); scatter adds <= 1.0 ms average (tick, apply slices; r8d §8 holds <= 1.5 ms per apply slice) and its edits never run on the render thread's critical path. RT tracks GPU in E3, so RT is not the bound.

### 8.2 How this meets D2 and what D2 gates
D2 (EXECUTION §5): terrain GPU <= 3.0 ms, C1U median >= 60 / 1%-low >= 45, trees-and-scatter scene reported not gated. This split keeps the median bar with scatter ON if units cost <= 1.4 ms; the 1%-low bar (22.2 ms) has about 6.5 ms of slack. **If units cost 2.5 ms, scatter ON breaks the median bar** (8.7 + 2.9 + 1.8 + 2.5 = 15.9 + overhead): then the ladder is mandatory.
Recommendation for C12: run C1U twice, scatter OFF (the gated configuration, unchanged) and scatter ON (reported, with the same bars as the target). That keeps D2 as written and shows Alec the number that matters to the look.

### 8.3 Governor ladder (all presentation-only; placement and sim untouched; each step is O(1) at runtime)
1. **Rank-tier visibility** (`chimera.scatter.Density` 0-1 toggles T1/T2 grass tiers and the equivalent for flowers): about -0.2 to -0.4 ms.
2. **Cull end distances** x0.8 (grass 150 -> 120 m): -0.2 to -0.4 ms; `SetCullDistances` per component.
3. `r.Nanite.PrimaryRaster.TimeBudgetMs` = about 1.5 and `r.Nanite.ShadowRaster.TimeBudgetMs` (5.8, `NaniteCullRaster.cpp:344-354`): automatic degrade, no code.
4. `r.Nanite.MaxPixelsPerEdge` 1.0 -> 2.0 for all Nanite meshes (units included): about -30 % Nanite triangle work (UNVERIFIED size).
5. Tree and shrub `CastShadow = false` beyond the nearest tile ring (not possible per instance; use a second ISM per tile for far trees) or `r.Shadow.RadiusThreshold` up (a global scalability value).
6. Wildflowers off, grass LOD1 distance 90 -> 60 m.
7. Fixed-world recovery, a look decision for Alec, not for scatter: E5 says SSGI + clouds + cloud shadows + sun cost up to 3.7 ms. Measure each with `ProfileGPU` before touching the recipe.

### 8.4 Kill and merge criteria (name the metric, name when to stop)
- Metric: `scatter_gpu_ms` = GPU p50 `idle_visible` with scatter built and visible minus the same with scatter built and hidden, at `rts80` and `CAM_Close`, from a `-Measure` run (C12 method, same as P4).
- Merge scatter into the packaged default look only if: `scatter_gpu_ms` <= 2.5 at `rts80`; C1US median >= 60 and 1%-low >= 45; `proxy`/hitch counters in the C1 family unchanged (no new frames > 33.3 ms from scatter updates).
- Kill or cut scope (drop wildflowers, then grass T0, then casters' far tiles) if after step 6 `scatter_gpu_ms` is still > 2.5. Re-decide when the units' real cost (C9) is known.

## 9. What the render side needs from placement and update (interface to r8a / r8d)
- Tiles: r8d's world-aligned 32 m scatter tiles are compatible; one ISM per (tile, mesh) and, for grass, per rank tier. Empty tiles get no component. At 100 tiles x about 6 meshes = 600 primitives, inside the 700 cap above (the look-test ran 900).
- **Stroke behaviour.** Do not touch ISMs per tick. A d=100 brush overlaps up to 16 tiles; rebuild affected tile-meshes at stroke end, undo, redo and load, on worker threads (pure function of heights, splat and hash), applying `ClearInstances` + one `AddInstances(Transforms, false, false, bUpdateNavigation=false)` (`InstancedStaticMeshComponent.h:275`) per tile-mesh within a per-frame slice. Reserve with `PreAllocateInstancesMemory` (`:334`). 5.8 tracks instance changes and uploads partial updates (`ISMInstanceDataManager.cpp`, R3 §4.1), so a tile rebuild is one partial upload, not a primitive recreation; ESTIMATE 0.3-0.6 ms game thread and about 0.5 MB upload per 2-4k instances (UNVERIFIED).
- **Mid-stroke popping.** Grass floating over a lowered brush for 2 s looks wrong. Cheapest honest fix: at stroke start hide only the instances inside the brush footprint (transform-only `BatchUpdateInstancesTransforms` to zero scale for the grass tiers, no WPO, no VSM impact because grass does not cast); restore by the tile rebuild at stroke end. Trees stay put until stroke end.
- **Thinning on path, rock, snow** is placement-side (accept or reject candidates by splat weight and slope). It also lowers cost: the 60 % eligible fraction in §7 is the assumption; a map painted with 30 % path and rock lowers `rts80` grass by half.
- Nothing here reads or writes sim state; the instance set is a pure function of heightfield, splat and seed, so S1L (reload) reproduces it, and frame rate does not enter.

## 10. Measurement plan (to collapse the band; none of it runs now)
1. **Scatter-only A/B inside C1S / C1US** (r8d's scripts): add ops `scatter visible 0|1` (set component visibility, instances stay built) and `chimera.scatter.Layers=<bitmask>` to isolate grass, flowers, shrubs, rocks, trees. Phases per layer at `rts80`, `CAM_Close` and trial `closeup`; 3 reps; `-Measure`.
2. **Per-pass GPU attribution.** Add `-csvGpuStats` (`ES/Core/Private/ProfilingDebugging/CsvProfiler.cpp:5098-5105` sets `r.GPUCsvStatsEnabled` = 1, `ES/RHI/Private/GPUProfiler.cpp:119-122`) and `-csvCategories=...,VSM,GPUScene` (categories defined `VirtualShadowMapArray.cpp:104`, `GPUScene.cpp:48`) to the `run_terrain.ps1` extra args for these runs; the same CSV already carries `GPUTime` (E2). Column names are UNVERIFIED until the first CSV is opened. One `ProfileGPU` dump at `idle_visible` per variant for pass-level ms (BasePass, Nanite, ShadowDepths, Prepass, Velocity) and one on `idle_hidden` to split E1's 8.7 ms.
3. **Grass A/B** (§3.3): Nanite opaque clump vs non-Nanite 3-LOD clump, same placement hash; metric `grass_gpu_ms` at `rts80` and trial `closeup`; read `r.Shadow.Virtual.NonNanite` counters (`VSM/NonNanitePreCullInstanceCount`, `VirtualShadowMapArray.cpp:4511`) to confirm the casters-only design.
4. **VSM behaviour check** (the UNVERIFIED items of §5): Stationary ISM + sculpt stroke: invalidated pages local to the tile; `Rigid` + WPO sway: shadow pages stay cached; Movable (default) ISM for contrast. Use the "Cached Pages" view and `r.Shadow.Virtual.Stats`.
5. **Cull switches**: `r.SceneCulling.CullChunkViewDistance` 0/1, `r.SceneCulling.HierarchicalCPUCulling` 1/0, Nanite hard-cut visibility at the end distance (video at `rts80` panning).
6. **Packaged parity**: C11's package with scatter ON, S1X hash equal; check that console-variable `Set` calls for the governor take effect in Shipping (C11 already doubts console paths).
7. Pin the scalability group for all of the above (§4.3).
Decision rule after 1-3: set the budget split's scatter line to the measured `rts80` number plus 15 %; replace this note's ESTIMATE column with MEASURED.

## 11. How Manor Lords is reported to do it
Public technical detail is thin; no talk or devlog describing its grass or foliage rendering was found (searches 2026-10-01).
- UE4 to UE5 port (0.7.987, Sept 2024), official patch notes as relayed by Gameranx (WEB, fetched): "A new 'virtual shadow map' system that produces very sharp, high resolution shadows", "New volumetric clouds and atmosphere rendering", **"GPU based LOD for Instanced Components"**.
  In engine terms that is `bUseGpuLodSelection` on plain instanced meshes (`InstancedStaticMesh.cpp:90-95,2370`), not Nanite.
- Lumen: Greg Styczen is reported (GamesRadar, search summary; the page itself did not load) as saying "Lumen wasn't worth the extra performance cost for this particular game therefore I left it off". Nanite: reported as not used (Steam discussion post, second-hand;
  GamesRadar summary). Both are consistent with our recipe: A_noLumen is the project's choice, and Nanite is a per-mesh choice for us.
- A search summary mentioned dynamic foliage LODs that depend on camera angle as well as distance; I could not find the patch-note text, so treat it as unconfirmed.
- Reading: a classic LOD'd-ISM pipeline plus VSM, no GI, painted ground; the Manor Lords look at an RTS camera is carried by density, colour variation, shadows and silhouettes, which is what this budget buys. UE4-era assumptions (LOD chains, GPU LOD) are valid in 5.8 and are our non-Nanite fallback.

## 12. Open questions and UNVERIFIED items
1. Units' real GPU cost with 1,000 ISM instances and shadows (C9/C12). The 1.4 ms is a guess; it decides whether scatter ON passes D2's median bar.
2. The scatter band itself (0.9-2.6 ms at `rts80`); §10 steps 1-3.
3. Nanite vs non-Nanite for grass clumps: ranking predicted, not measured (§3.3). Includes whether the Nanite hard cut at the end distance pops visibly.
4. Stationary/Static ISM with runtime instance edits: legality and VSM invalidation scope (§5.2).
5. `Rigid` invalidation with WPO sway on trees: do shadows stay cached (§5.1)? Needs a G-style test; until then casters have no WPO.
6. Which scalability group this PC auto-selects, and whether the package differs from `-game` (§4.3).
7. Fixed-world 8.7 ms breakdown. Who owns reducing it (ground-look/lighting, Alec's recipe decision)? E5 suggests up to 3.7 ms is available there; nothing in scatter can recover it.
8. Whether the trial's `closeup` pose should gate anything. At the low pose scatter is pixel-bound (3.8 Mpx) and may reach 3.4 ms; D2 reports it, but the look video will show it. Option for Alec: keep the pose as a look shot only.
9. Whether `r.Nanite.PrimaryRaster.TimeBudgetMs` and the other cvars can be set at runtime in Shipping (C11).
10. Asset triangle counts: r8b measured PCGBiomeSample meshes at 6 to 2,588 Nanite-input triangles; Poly Haven trees need decimation. Caps in §7 assume 3,000 / 800 triangle trees and 48 / 12 triangle clumps.

## 13. Sources
**Engine (5.8.3, `ES` = `D:/Epic Games/UE_5.8/Engine/Source/Runtime`):** `Engine/Classes/Components/{InstancedStaticMeshComponent.h:181,198-229,275,334,391,421,443; HierarchicalInstancedStaticMeshComponent.h:134-195; StaticMeshComponent.h:158-191,411-412; PrimitiveComponent.h:489,513,556,564,568,587}`,
`Engine/Classes/Engine/{InstancedStaticMesh.h:453-467; RendererSettings.h:1549-1553}`, `Engine/Public/{PrimitiveSceneProxy.h:771-774; SceneTypes.h:220-235}`,
`Engine/Private/{HierarchicalInstancedStaticMesh.cpp:37-142,156,806,899-901,912-919,1235-1306,2022-2031; InstancedStaticMesh.cpp:90-95,1547-1548,1767-1800,2354-2370,5287; PrimitiveSceneProxy.cpp:55-61,823,870-874; StaticMeshSceneProxy.cpp:424; Rendering/NaniteResources.cpp:118-144,1846-1870}`,
`Renderer/Private/{Nanite/NaniteCullRaster.cpp:133-138,294-299,329-354; SceneCulling/SceneCulling.cpp:147-152,172-190,2819-2824; SceneCulling/SceneCullingRenderer.cpp:23-27; ShadowSetup.cpp:117-123; RendererScene.cpp:138-154;
VirtualShadowMaps/VirtualShadowMapArray.cpp:104,119-127,173-182,437-443,497-503,4511; VirtualShadowMaps/VirtualShadowMapCacheManager.cpp:98-110,142-146,189-195,578-586,1281-1301; VirtualShadowMaps/VirtualShadowMapClipmap.cpp:99-112; PostProcess/TemporalSuperResolution.cpp:331-340,499-501}`,
`RenderCore/Private/RenderUtils.cpp:1372-1385,2085-2090`, `RHI/Private/GPUProfiler.cpp:119-122`, `Core/Private/ProfilingDebugging/CsvProfiler.cpp:5098-5105`, `E/Config/BaseScalability.ini:138-304,964-996`, `E/Shaders/Private/Nanite/{NaniteInstanceHierarchyCulling.usf:139-180,NaniteCullingCommon.ush:441-442}`,
`E/Plugins/Experimental/{PCGBiomeSample,ImpostorBaker,DynamicWind}`.
**Repo and machine:** `docs/unreal-move/look-test-2026-10-01/{README.md,results.json,fps_summary.json}`; `LT/out/fps_summary.json`; `tools/unreal-looktest/{lt_build.py:262-275,lt_common.py:317-392,412-418}`;
`docs/unreal-move/trial-checks/evidence/c/{c-C7-c1-mat-summary.txt,c-G1-c1-look-r0-summary.txt}`; `T/Source/ChimeraTerrain/Game/{RtsCameraPawn.cpp:7-45,TerrainActor.cpp:317-329}`; `T/Config/DefaultEngine.ini`; r3 §4.1, r8a, r8b, r8d.
**Web (fetched 2026-10-01; reliability in brackets):**
- Epic, Nanite Foliage (experimental, WPO and alpha masking not recommended, 0.1 ms per 100k bones, 41 M triangle tree): https://dev.epicgames.com/documentation/unreal-engine/nanite-foliage [primary]
- Epic, Virtual Shadow Maps (WPO/PDO invalidate every frame, contact shadows for grass, Rigid/Static, non-Nanite LOD advice): https://dev.epicgames.com/documentation/en-us/unreal-engine/virtual-shadow-maps-in-unreal-engine [primary]
- Epic, Nanite (instance limit 16 M, WPO cluster splitting, masked supported): https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine [primary]
- Epic, Foliage Mode (cull and fade distances, Nanite note, density scale): https://dev.epicgames.com/documentation/unreal-engine/foliage-mode-in-unreal-engine [primary, partly stale vs 5.8 source]
- Tom Looman, UE 5.8 performance highlights (Nanite Pixel Programmable Distance, hierarchical CPU culling for non-Nanite ISM, VSM deferred invalidation budget): https://tomlooman.com/unreal-engine-5-8-performance-highlights/ [secondary, matches source]
- Tom Looman, UE 5.7 performance highlights (Nanite Foliage uses skinning not WPO; no ms numbers): https://tomlooman.com/unreal-engine-5-7-performance-highlights/ [secondary]
- 80.lv, Nanite Foliage voxel test (62 to 119 fps, 77,376 trees; no GPU stated): https://80.lv/articles/get-a-glimpse-of-nanite-foliage-with-voxel-representation-in-ue5-7 [secondary]
- Gameranx, Manor Lords UE5 patch (virtual shadow map, volumetric clouds, GPU based LOD for Instanced Components): https://gameranx.com/updates/id/506187/article/manor-lords-update-ports-title-to-unreal-engine-5-adds-fishing/ [secondary, quotes patch notes]
- GamesRadar, Manor Lords UE5 articles (Lumen "wasn't worth the extra performance cost"; page body not retrievable, quote via search summary) [secondary, unconfirmed]
- ibbles/LearningUnreal VSM notes (IncludeInCoarsePages 0 for animated non-Nanite foliage): https://github.com/ibbles/LearningUnreal/blob/main/Virtual%20Shadow%20Maps.md [community]
- NVIDIA RTX 3060 product page (3,584 cores, 1.78 GHz, 12 GB, 192-bit): https://www.nvidia.com/en-us/geforce/graphics-cards/30-series/rtx-3060-3060ti/ [primary]
- Epic forum, foliage overdraw testing (qualitative only: billboards beat meshes at distance, no numbers): https://forums.unrealengine.com/t/foliage-overdraw-testing/80905 [community]
- strayspark.studio Nanite foliage guide (no numbers; "VSM become the dominant line item"): https://www.strayspark.studio/blog/ue5-nanite-foliage-procedural-placement-performance [low reliability; a second post there claims 8.2 to 3.1 ms from disabling WPO shadows on PS5 with no method, not used]
Pages that failed to load (403): unrealengine.com Manor Lords interview, hoodedhorse wiki, steamdb patch notes, techpowerup GPU specs, Epic Fortnite Nanite tech blog.

## Appendix A: the model (reproduces §2 and §7; numpy only)
```python
"""r8c scatter model: camera ground footprint, palette counts, cost bands. Pure geometry + stated assumptions; no engine."""
import math
import numpy as np

W, H, HALF = 1920, 1080, 160.0   # 1080p, map half extent (m)


def footprint(pos, pitch, yaw, vfov):
    """Per-pixel flat-ground hit: slant distance, ground area per pixel (m2), inside-map mask. UE yaw 90 looks +Y."""
    p, y = math.radians(pitch), math.radians(yaw)
    fwd = np.array([math.cos(p) * math.cos(y), math.cos(p) * math.sin(y), math.sin(p)])
    right = np.array([math.sin(y), -math.cos(y), 0.0])
    up = np.cross(right, fwd)
    ty = math.tan(math.radians(vfov) / 2); tx = ty * W / H
    X, Y = np.meshgrid((np.arange(W) + .5) / W * 2 - 1, 1 - (np.arange(H) + .5) / H * 2)
    d = fwd + X[..., None] * tx * right + Y[..., None] * ty * up
    hit = d[..., 2] < -1e-6
    t = np.where(hit, -pos[2] / np.where(hit, d[..., 2], -1), np.inf)
    gx, gy = pos[0] + t * d[..., 0], pos[1] + t * d[..., 1]
    inside = hit & (abs(gx) <= HALF) & (abs(gy) <= HALF)
    gxp, gyp = np.where(inside, gx, np.nan), np.where(inside, gy, np.nan)
    area = abs(np.gradient(gxp, axis=1) * np.gradient(gyp, axis=0) - np.gradient(gyp, axis=1) * np.gradient(gxp, axis=0))
    dist = np.sqrt((gx - pos[0]) ** 2 + (gy - pos[1]) ** 2 + pos[2] ** 2)
    return dict(inside=inside, area=area, dist=dist, A=float(np.nansum(area[inside])), vfov=vfov, z=pos[2])


# name, density near (/m2), density at cull, eligible ground fraction, tris LOD0, tris LOD1, LOD1 at slant m, cull end slant m, width m, height m
PALETTE = [("grass clump", 3.0, 0.0, 0.60, 48, 12, 90, 150, 0.6, 0.35), ("wildflower", 0.5, 0.0, 0.60, 16, 6, 80, 130, 0.4, 0.40),
           ("shrub", 0.04, 0.02, 0.50, 600, 150, 120, 300, 2.0, 1.8), ("rock", 0.01, 0.01, 0.20, 400, 100, 120, 300, 1.5, 1.0),
           ("tree", 0.03, 0.03, 0.25, 3000, 800, 120, 600, 6.0, 12.0)]
LOW = dict(geo=0.35, pix=0.15, surv=0.002, rej=0.0005, vsm=0.10, fixed=0.05)    # ms per Mtri, per Mpx, per k drawn, per k rejected
HIGH = dict(geo=0.90, pix=0.45, surv=0.005, rej=0.002, vsm=0.30, fixed=0.15)   # ASSUMPTIONS


def scatter(r):
    I, Ar, D = r['inside'], r['area'], r['dist']
    el = np.arcsin(np.clip(r['z'] / np.where(D > 0, D, np.nan), 0, 1))
    mpp = 2 * D * math.tan(math.radians(r['vfov']) / 2) / H
    inst = tri = px = casters = 0.0
    for name, dn, df, frac, t0, t1, l1, cull, w, h in PALETTE:
        m = I & (D <= cull)
        dens = np.where(D < 60, dn, dn + (df - dn) * np.clip((D - 60) / max(cull - 60, 1), 0, 1))
        n = float(np.nansum(np.where(m, dens * Ar * frac, 0)))
        n0 = float(np.nansum(np.where(m & (D < l1), dens * Ar * frac, 0)))
        proj = w * h * 0.6 if h > 1 else w * h * np.cos(el) + 0.7 * w * w * np.sin(el)   # projected m2 per instance
        px += float(np.nansum(np.where(m, dens * frac * Ar * proj / mpp ** 2, 0)))
        inst += n; tri += n0 * t0 + (n - n0) * t1; casters += n if name in ("shrub", "rock", "tree") else 0
    cand = r['A'] * sum(p[1] * p[3] for p in PALETTE) * 1.25     # instances of the visible 32 m tiles, whatever their distance
    ms = [c['geo'] * tri / 1e6 + c['pix'] * px / 1e6 + c['surv'] * inst / 1e3 + c['rej'] * max(cand - inst, 0) / 1e3
          + c['vsm'] * min(casters / 1433, 1.5) + c['fixed'] for c in (LOW, HIGH)]
    return inst, cand, tri / 1e6, px / 1e6, ms


VIEWS = {"rts80": ((0, -51.42, 61.28), -50, 90, 75),
         "CAM_Close (30 m, 40 vFOV)": ((-2, 2 - 30 * math.cos(math.radians(40)), 30 * math.sin(math.radians(40))), -40, 90, 40),
         "trial oblique": ((60, -90, 25), -13, 123.7, 50), "trial closeup (low)": ((-66, -30, 3.5), -7, 50.7, 60)}
if __name__ == "__main__":
    for name, cam in VIEWS.items():
        r = footprint(*cam)
        inst, cand, tri, px, ms = scatter(r)
        print(f"{name:26s} ground {r['A']:7,.0f} m2  drawn {inst:7,.0f}  tile candidates {cand:8,.0f}  {tri:4.2f} Mtri  {px:4.2f} Mpx  GPU ms {ms[0]:.2f}-{ms[1]:.2f}")
```
Output when run: `rts80` 56,545 m², 23,550 drawn, 150,515 candidates, 1.46 Mtri, 1.04 Mpx, 0.93-2.61 ms; `CAM_Close` 2,001 m², 4,258, 5,327, 0.25, 2.91, 0.59-1.72; trial `oblique` 50,010 m², 15,683, 133,120, 1.08, 2.24, 0.94-2.70; trial `closeup` 37,644 m², 20,022, 100,204, 1.23, 3.82, 1.20-3.44.
