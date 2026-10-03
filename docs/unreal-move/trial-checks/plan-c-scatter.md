# Plan C-scatter: 3D grass, flowers, shrubs and trees that follow runtime terrain edits (extension of trial check c)

Date 2026-10-01; revised 2026-10-02 after an engine/API critique and a proof critique (Review log at the end). Plan of record, judged and
synthesised from three designs (PCG-first, bespoke C++, risk-first hybrid) and research r8a-r8d (`research/r8a-pcg-runtime.md`,
`r8b-scatter-assets.md`, `r8c-scatter-perf.md`, `r8d-scatter-integration.md`). Design only: no Unreal process was started and nothing in either
project was edited. Every fact in §2 was read in the 5.8.3 engine source, the ChimeraTerrain tree or the evidence folder (F1-F17 on 2026-10-01,
F18-F27 and the corrections to F1, F3-F6 and F13 on 2026-10-02). **UNVERIFIED** marks what only a build or a run settles. **EST** marks arithmetic.

Approval: Alec, 2026-10-01: "Go with what you think is best. Just make sure it's implemented well!" Standing rule (HANDOFF, memory):
"Prefer Unreal built-ins (e.g. PCG) where they work at runtime; otherwise build our own." §1 says how that rule was applied.

**Shell rule and shorthand** follow plan C: every command runs from Git Bash. `LOCK` = `bash D:/Projects/Chimera-Unreal/ue_lock.sh`
(set `UE_LOCK_TAG=c/S<n>`), `PS` = `powershell -NoProfile -ExecutionPolicy Bypass -File`, `BG` = start with `run_in_background`, then wait in the
foreground with `bash D:/Projects/Chimera-Unreal/wait_for.sh`. `T` = `D:/Projects/Chimera-Unreal/ChimeraTerrain`, `S` = `T/Source/ChimeraTerrain`,
`E` = `D:/Epic Games/UE_5.8/Engine`, `ER` = `E/Source/Runtime/Engine`, `RR` = `E/Source/Runtime/Renderer`, `PCG` = `E/Plugins/PCG/Source/PCG`,
`R` = `D:/Projects/Project_Chimera`, `EV` = `R/docs/unreal-move/trial-checks/evidence`, `KIT` = `R/tools/unreal-trial`, `U` = `D:/Projects/Chimera-Unreal`.

**Task ids.** S0-S9 (and S4a) here mean *scatter* tasks. EXECUTION.md already uses S0-S4 for the shared Phase 0 tasks (done), so EXECUTION and
HANDOFF cite these as "scatter S4" and so on.

## 0. What done means

Scatter is done when all of this holds, from files, on this PC:
1. A packaged Development **and** Shipping build of ChimeraTerrain shows grass, tussocks, wildflowers, ferns, shrubs, trees and rocks on the
   terrain. After a sculpt or paint stroke, an undo, a redo or a load, they sit on the edited surface. They thin out on paths, rock, snow and
   steep slopes. They catch up without a hitch.
2. Placement is a pure function of the height bytes, the splat bytes, the palette and the seed. The live instances equal a synchronous
   full regeneration at every settled checkpoint, and what the GPU draws equals a fresh rebuild from the same CPU state (SX18). Undo, redo,
   reload, frame rate, hitches, worker count and the build flavour do not change them.
3. Scatter is presentation only. Terrain hashes and saved files are unchanged. With scatter off (dormant), the script-driven runs (S1, S1L, G1,
   C7's paint bar) are hash-identical to their references, and the timing and mouse runs (C1's P4, MOUSE's P10, SOAK's P6) pass their own bars
   (SOAK first runs again in C12). Collision (P5) and depth (P11) bars pass with scatter on.
4. The game-thread and render-thread bars hold in the Phase 4 measure window (SX11).
5. GPU cost, caps and the scene with 1,000 units are reported, with the kill rule applied (§3.7). The look is judged against Manor Lords by
   an Opus art director and sent to Alec each round. It is reported, not gated (D4).

Gated bars: SX1-SX11, SX16 and SX18 (§5). The rest are reported. **Scatter does not change (c)'s Done in plan C §5**, and the schedule keeps
it that way: C9 waits only for the small event task S4a, and C11 never waits for scatter and has a no-scatter fallback (§3.10). A scatter FAIL
goes to Alec with its numbers, and scatter then ships off by default (still available as an option). Nothing is rounded up.

## 1. Decision and why

**Build scatter as a presentation-only module inside ChimeraTerrain, on Unreal's own instanced-foliage path:** plain
`UInstancedStaticMeshComponent` (ISM) tiles with GPU LOD, Nanite and virtual shadow maps (VSM), fed by a pure-C++ generator that makes every
decision in integer arithmetic on a world-anchored lattice. Generation runs on `UE::Tasks` from an immutable snapshot. The game thread
applies each result as a small **keyed edit script** (update in place, remove at swap, append) under a per-frame budget, so a tile edit never
recreates the ISM's scene proxy. Grass units cast no VSM shadow and follow a stroke while it runs; caster units (trees, shrubs, rocks) apply at
stroke end, because any instance edit invalidates the whole unit's shadow pages and keeps it in the dynamic shadow layer for 100 frames (F20).
The base is the "bespoke C++" design; the grafts are marked in §3.

### 1.1 Scores (0-10; each score rests on a claim checked 2026-10-01)
| Design | Packaged | Follows edits | Look ceiling | Perf (D2) | Testability | Simplicity | Deciding evidence |
|---|---|---|---|---|---|---|---|
| PCG-first | 6 | 6 | 7.5 | 6 | 8 | 4 | Its on-demand route is real (F8a), but the custom node does all placement, so PCG only spawns. Its spawner is main-thread, cannot slice one ISM, and clears and recreates every ISM proxy on every regeneration (F8b, F1). It needs ~900 primitives and two backends with equal hashes |
| Bespoke C++ (base) | 8.5 | 8.5 | 8 | 7.5 | 8.5 | 6.5 | Engine primitives only. Its in-place update path avoids `ClearInstances` (F1). L0 procedural / L1 scanned asset levels. Terrain-normal grass shading. A separate Z-follow job type adds a second code path |
| Risk-first hybrid | 9 | 7.5 | 7 | 7 | 8.5 | 7 | Best packaged hygiene: material usage flags asserted (F7), dependency check, components made at enable, pending counter. But its apply is `ClearInstances` + `AddInstances` (proxy recreate, F1), its grass is non-Nanite with WPO on every instance, and its trees are procedural first |

### 1.2 How the built-ins rule was applied
The rule is "built-ins where they work at runtime on our terrain; otherwise our own". Every runtime piece here **is** a built-in: ISM,
GPU LOD selection, Nanite, VSM, the task graph, Interchange import. PCG was checked piece by piece against the source:
- The Surface Sampler and stock filters cannot carry the rules. The lattice is closed at both ends (a shared tile edge emits twice), density
  resets to 1 after projection, and float thresholds can flip between the `/fp:precise` editor and the `/fp:fast` game (F8c, F12). So the
  rules are custom C++ in every design, PCG-first included.
- The runtime-generation scheduler needs an `APCGWorldActor`, whose only creator is editor-only. Change tracking is editor-only. GPU graphs
  must be cooked editor assets and are Beta (r8a §1.3, §3.1, §5).
- What is left is the on-demand executor plus the Static Mesh Spawner. The executor duplicates `UE::Tasks`. The spawner is the weak part
  for an edited map: it runs on the main thread, adds one whole mesh list per step, copies custom data per instance, and resets every reused
  ISM with `ClearInstances`, which recreates its scene proxy (F1, F8b).
So PCG does not "work at runtime on our terrain" better than the engine's own ISM path. The records-to-ISM seam (§3.1) keeps a PCG backend
possible after the trial, for creator-authored scatter graphs (§7).

### 1.3 Choices made between the designs
| Topic | Chosen | Why |
|---|---|---|
| Apply | keyed edit script on a Stationary ISM, no `ClearInstances` after the first fill | F1: no proxy recreation. An edit still invalidates the whole unit's VSM pages and keeps the unit dynamic for 100 frames (F20), so non-casting grass units follow mid-stroke and caster units apply at stroke end (§3.5). `-ChimeraTerrainScatterApply=clear` stays as an A/B |
| Job types | one: full tile generation (no separate Z-follow job) | with the edit script a full regeneration costs the game thread the same as a Z-only update; every applied state is then exactly `BuildInstance(EvaluateTile(snapshot))` |
| Mobility | Stationary | not "often moving", so a new proxy is cached as static (F4); Static forbids previous-transform data. Movable differs only for the first 100 frames after an add (F20), so it is a diagnostic option, not an A/B |
| Grass | opaque procedural blade clumps, Nanite, two rank tiers for the distance taper; non-Nanite LOD chain as the A/B | r8c §3.3 (visibility buffer beats quad overdraw on 1-3 px triangles), r8c §4.1 (Nanite cuts hard at the end distance) |
| WPO | none on any scatter in v1 (no wind, no brush mask) | keeps VSM pages cached, the A/A image floor, and Nanite on its fast path (r8c §5) |
| Masked leaves | full masked raster at every distance (`NanitePixelProgrammableDistance` 0) | a distance > 0 draws masked cards solid beyond it, shadows included (F19); at `rts80` all ground is beyond 61 m (F14) |
| Tiles | fine 32 m (grass layer), coarse 80 m (trees, shrubs, ferns, rocks), anchored at the origin | exact 10x10 and 4x4 at E = 160, 20x20 and 8x8 at E = 320; 644 primitives at most (r8c cap about 700). Coarse 40 m (1,076 primitives) is a measured lever for the stroke-end shadow cost, not the default |
| Assets | L0 procedural (numpy, project-original) for bring-up and as the fallback; L1 Poly Haven CC0 trees, rocks and near cards; the art director picks per slot | no licence or download risk on day one; real scanned crowns are the best bet for Manor Lords forest masses (r8b §3) |
| Map edge | woodland bias in the 128-160 m border ring | the G1 round 1 shot shows a hard map edge against the sky at `rts80` (F16) |

## 2. Facts this plan relies on

| # | Fact | Source |
|---|---|---|
| F1 | `ClearInstances` calls `PrimitiveInstanceDataManager.Invalidate`, which calls `MarkRenderStateDirty`: the scene proxy is recreated. `BatchUpdateInstancesTransforms`, `AddInstances` and `RemoveInstances` only record changes (`TransformChanged`, `Add`, `RemoveAt(Swap)`), flushed as a partial update at end of frame without a new proxy. The flush is still a primitive update with culling flags, so VSM treats it as a move of the whole primitive (F20) | `ER/Private/InstancedStaticMesh.cpp:4800-4830, 3633, 3749, 4027-4031, 4469-4520, 2544-2575`; `ER/Private/InstancedStaticMesh/ISMInstanceDataManager.cpp:916-927` |
| F2 | `SetCustomData(Start, End, Floats)`: End is inclusive. `SetRemoveSwap()` turns on remove-at-swap. `BatchUpdateInstancesTransforms` without previous transforms leaves `PerInstancePrevTransform` empty | `InstancedStaticMesh.cpp:3947-3965, 4003`; `ER/Classes/Components/InstancedStaticMeshComponent.h:235,465` |
| F3 | An empty ISM has no proxy (`r.InstancedStaticMeshes.AllowCreateEmpty` = 0). Its first instances add it at end of frame. PSO precaching may delay proxy creation (`CreateStaticMeshSceneProxy` returns null, `:2585-2589`). `CreateSceneProxy` checks `InstancingRandomSeed != 0`; the engine picks a random or path-derived seed when it is 0. A hidden component never gets a proxy (F18) | `InstancedStaticMesh.cpp:96-99, 2564-2571, 2585-2597, 2610-2631, 5870-5880` |
| F4 | The ISM constructor sets Movable. A Stationary proxy counts as movable ("can still move") but not as "often moving", so VSM caches a newly added Stationary primitive as static. Any primitive, Movable included, returns to static caching after 100 frames without an invalidation (F20). A Static ISM must not carry previous transforms | `InstancedStaticMesh.cpp:2360, 5287`; `ER/Public/PrimitiveSceneProxy.h:763-774`; `RR/Private/VirtualShadowMaps/VirtualShadowMapCacheManager.cpp:579-586` |
| F5 | `CreateSceneProxy` (`:535`) and `SendRenderInstanceData_Concurrent` (`:531`) are exported virtuals on ISM, so a subclass can count proxy creations and time the flush. Both may run off the game thread (parallel end-of-frame updates, registration contexts), so counters are atomic | `InstancedStaticMeshComponent.h:531, 535`; `InstancedStaticMesh.cpp:2544-2575`; `ER/Private/LevelTick.cpp:1243-1283` (end-of-frame updates as a `ParallelFor` in a task) |
| F6 | API lines used: `AddInstances` `:275` (navigation update defaults to true, so pass false), `PreAllocateInstancesMemory` `:334`, `BatchUpdateInstancesTransforms` `:388,391`, `RemoveInstances(..., bSortedReverse)` `:427`, `SetCullDistances` `:443` (updates a live proxy in place through `UpdateInstanceCullDistance`, no new proxy, `.cpp:4869-4881`), `bDisableCollision` `:229`, `bUseGpuLodSelection` `:221` (ctor sets true, `.cpp:2370`), `SetNumCustomDataFloats` `:327`, `SetUseConservativeBounds` `:477`; `StaticMeshComponent.h` `bDisallowNanite` `:166`, `bEvaluateWorldPositionOffset` `:176` | headers as cited |
| F7 | Materials carry `bUsedWithInstancedStaticMeshes` and `bUsedWithNanite`. A cooked game cannot compile a missing usage, so the mesh draws the default material, in packaged builds only; the warning is a `LogMaterial` Warning, compiled out of Shipping | `ER/Public/Materials/Material.h:795, 863`; `ER/Private/Materials/Material.cpp:1945` |
| F8a | PCG: `UPCGSubsystem::Tick` runs the graph executor with no world actor; only the runtime-generation scheduler needs one. `GenerateLocal(bForce)` on a GenerateOnDemand component is accepted | `PCG/Private/Subsystems/PCGSubsystem.cpp:363-433`; `IPCGBaseSubsystem.cpp:94-105`; `PCG/Private/PCGComponent.cpp:366-410, 501-600` |
| F8b | PCG spawner: PrepareData and Execute are main-thread only; a reused ISM is reset with `ClearInstances`; instances go in with one `AddInstances(..., bWorldSpace=true)` per mesh list, then custom data per instance; a descriptor with no class defaults to HISM | `PCG/Private/Elements/PCGStaticMeshSpawner.cpp:721-727, 887-895`; `PCG/Private/PCGManagedResource.cpp:979-987`; `PCG/Private/Helpers/PCGActorHelpers.cpp:132-137` |
| F8c | Surface Sampler: cell range `[Ceil(Min/C), Floor(Max/C)]`; `pcg.FrameTime` default 5 ms | `PCG/Private/Elements/PCGSurfaceSampler.cpp:116-121, 312-316`; `PCG/Private/Graph/PCGGraphExecutor.cpp:81-84` |
| F8d | PCG is already linked into the ChimeraTerrain game target (plugin enabled by default); this plan adds no plugin | `T/Binaries/Win64/ChimeraTerrain.target` lists `PCG.uplugin` |
| F9 | Every height or splat write goes through `ATerrainActor`: `InitTerrain` `:171-190`, `ApplyTick` `:216-281` (unions `:237-238`), `EndStroke` `:283-300`, `ApplyDelta` `:317-329` (undo/redo, reason passed in), `LoadFrom` `:362-387` (`HF` replaced at `:378`). `HasPendingWork` `:413-416`. The actor does not tick (`:41`). `SampleSurface` is the triangulated BL-TR surface the mesh draws (`Data/TerrainHeightfield.cpp:137-162`). `HF.Splat` is the authority; the texture is write-only. `FTerrainEditDelta` carries the restored chunk ids plus union rects; `HF.ChunkOwnedRect` / `ChunkOwnedSplatRect` give each chunk's owned rects | `S/Game/TerrainActor.cpp`; `S/Data/TerrainUndo.h:10-17`; `S/Data/TerrainHeightfield.h:121,125`; r8d §1.1-§1.5 |
| F10 | The director ticks in `TG_PostUpdateWork` (`Test/TerrainScriptDirector.cpp:261`). `settle`, `shot` and `depthcheck` wait on `Terrain->HasPendingWork()` (`:626, :801, :1517`). `depthcheck` uses `ShowOnlyActors.Add(Terrain)` (`:1510`), so scatter must live in its own actor. Footprint sets are `(x, y, r)` discs (`:764`; `.h:204`). C1's walk starts a new stroke every `cycle_s / N` = 1.25 s (`:1178-1200`) | `S/Test/TerrainScriptDirector.{h,cpp}` |
| F11 | `config_key` makes every `-ChimeraTerrain<K>=` option outside `{Script, Out, Load, Units}` part of the configuration, so any scatter option splits a run from the gate configuration. Plain flags such as `-csvGpuStats` are not in the key. P4 reads only `idle_visible*` and `idle_hidden` | `T/Tools/parse_terrain.py:98, 717-747, 1046-1064` |
| F12 | The editor target compiles `/fp:precise`, a game target `/fp:fast` | `E/Source/Programs/UnrealBuildTool/Platform/Windows/VCToolChain.cs:1313-1335` (r8d §3) |
| F13 | Latest ground (G1 round 2): `idle_visible` GPU p50 11.54 / 11.56 ms, `idle_hidden` 8.59 / 8.64, terrain 2.946 ms (0.054 ms under P4); walk GT p50 1.63 / 1.64 ms; d100 tick p50 0.950 ms. At 60 fps about 5.1 ms of GPU remain for scatter, 1,000 units and margin | `EV/c/c-G1-c1-look-r2-summary.txt` |
| F14 | At `rts80` the nearest ground is 61 m away; grass geometry reads only inside about 100 m slant; a 12 m tree is 40-105 px. Cameras are fixed poses with no pan, so the whole map is relevant and nothing streams | r8c §2; `S/Game/RtsCameraPawn.cpp:7-41` |
| F15 | Poly Haven is CC0 (polyhaven.com/license); its API wants a User-Agent (ToS §2.4). It has no temperate bush and one small broadleaf (`tree_small_02`, 2.8 m natively); scanned trees are 0.5-17 M triangles. Blender 4.5.10 is at `D:/tools/blender`. D: has 592 GB free. `KIT/preflight.ps1:46`'s busy list lacks blender | r8b §0, §2a, §6; `df` |
| F16 | The G1 round 1 `rts80` shot shows the hard map edge against the sky; Manor Lords frames are framed by forest masses (`ml_01`) | `EV/c/c-G1-ground-r1.jpg`; `T/Out/refs/manor_lords/ml_01.jpg` (git-ignored) |
| F17 | UBT compiles every `.cpp` under the module. A half-written scatter file breaks the ground-look workflow's builds, which share the tree. That workflow is editing `GameMode`, `TerrainLighting`, `DefaultGame.ini` and the ground material now | `git -C U status` |
| F18 | A hidden component is never added to the scene: `ShouldComponentAddToScene` requires `ShouldRender()`, and the ISM flush skips it. `SetVisibility` and `SetCastShadow` call `MarkRenderStateDirty`, which destroys and recreates the proxy | `ER/Private/Components/PrimitiveComponent.cpp:2106-2121, 630-650, 2212-2219`; `InstancedStaticMesh.cpp:2549-2575`; `ER/Private/Components/SceneComponent.cpp:3574-3576` |
| F19 | `NanitePixelProgrammableDistance` > 0 sends instances beyond it to the fallback raster, which does not evaluate the opacity mask (VSM rasterisation included) | `E/Shaders/Private/Nanite/NaniteCullingCommon.ush:489-510`; `ER/Classes/Components/StaticMeshComponent.h:410-412` |
| F20 | Every ISM flush sends `FUpdateInstanceCommand` with `CullingBounds`; VSM calls `UpdatedTransform` on such updates, which invalidates **all** of the primitive's instances and marks it dynamic (re-rendered every frame). It returns to static only after `r.Shadow.Virtual.Cache.FramesStaticThreshold` = 100 frames with no invalidation, with one more invalidation at the switch. Primitives that cast no dynamic shadow are skipped. `r.Shadow.Virtual.Cache.ForceInvalidateDirectional` exists. A page-pool overflow logs a `LogRenderer` Warning naming "Virtual Shadow Map Page Pool overflow". The `VSM` CSV category is off by default | `RR/Private/ScenePrimitiveUpdates.h:69-75`; `VirtualShadowMapCacheManager.cpp:142-146, 546-604, 734, 1767-1838, 2055-2067, 2175-2187`; `VirtualShadowMapClipmap.cpp:36`; `VirtualShadowMapArray.cpp:104` |
| F21 | `r.Nanite.PrimaryRaster.TimeBudgetMs` and `ShadowRaster.TimeBudgetMs` are dynamic-render-scaling budgets, disabled by default; the heuristic records frames only with dynamic resolution on, which nothing here enables; the shadow scaling floor defaults to 100 % (no scaling) | `RR/Private/Nanite/NaniteCullRaster.cpp:329-354, 411-445`; `ER/Private/DynamicResolution.cpp:770-776` |
| F22 | `ContactShadowLength` defaults to 0; the world-space flag is `ContactShadowLengthInWS`. `ATerrainLighting`'s sun sets neither, so no primitive gets a contact shadow from it | `ER/Private/Components/LightComponent.cpp:470`; `ER/Classes/Components/LightComponent.h:131-135`; `S/Game/TerrainLighting.cpp` (sun block) |
| F23 | The Interchange glTF translator sets flip-green on every texture used as a glTF `normalTexture` (GL → DirectX) | `E/Plugins/Interchange/Runtime/Source/Import/Private/Gltf/InterchangeGltfTranslator.cpp:670-677` |
| F24 | In the Nanite programmable raster (a compute shader that is not compute-shaded), `ddx`, `ddy` and `fwidth` are defined as 0, so a mask sampled with explicit derivatives in a Custom node gets mip 0 | `E/Shaders/Private/MaterialTemplate.ush:163-171` |
| F25 | The ISM's per-instance random comes from `FRandomStream(InstancingRandomSeed)` walked in instance index order, so it changes with remove-at-swap history | `InstancedStaticMesh.cpp:2693-2729` |
| F26 | `parse_terrain.py --same-hash A B` is P8's check: besides FNV equality it requires B capped below 60 fps, 1.5x slower frames and a mid-stroke hitch. Two uncapped runs fail it | `T/Tools/parse_terrain.py:530-568` |
| F27 | The ground-look workflow has moved the ground constants (working tree, round 3): `RockBand` 0.06 → 0.10, new `RockBreak` 0.05 (the auto-rock edge is slope plus clump noise), `PatchM` 60 → 140 m. With `RockCos` 0.8192 the band edges are 23.2° and 44.0° | `T/Scripts/make_ground_material.py:61-66`; `T/Scripts/ChimeraGround.hlsl` (round 3 auto-rock block) |

## 3. Design

### 3.1 Shape and files
Three layers, like the terrain code. The generator never includes `TerrainSimExport.h` or any `Game/`, `Render/` or UObject header.

- **`S/Data/` (pure C++, namespace `ChimeraTerrain`):**
  - `TerrainScatterTypes.h`: `EScatterGrid {Fine, Coarse}`, `EScatterClass` (§3.4), `FScatterKey` (64-bit candidate id), `FScatterRecord`,
    `FScatterTileKey`, `FScatterSnapshot`, `FScatterEditScript`, `FScatterUnitState` (applied records in ISM order plus a sorted key→index view).
  - `TerrainScatterMath.{h,cpp}`: `Mix32` (lowbias32), `Key()`, `FloorDiv`, `SurfaceQ16` (integer twin of `SampleSurface`), `CellGradientQ16`,
    `SlopeG2Q32`, `SplatQ8Bilinear`, `ValueNoiseQ16`, `RampQ16`, `BandQ16`, `Fnv1a64` with fixed-width LE helpers (`Fnv1a64U8/U16/U32`).
  - `TerrainScatterPalette.{h,cpp}`: the class and field table of §3.4 as data, `ApplyOverrides("Name=Value,...")`, `ConfigFnv()`.
  - `TerrainLookShared.h`: **generated** by `T/Tools/gen_look_shared.py` from `T/Scripts/make_ground_material.py` `SCALARS` after the
    ground-look final commit: `RockCos`, `RockBand` and the integer slope thresholds `(1/c² − 1)·2³²` for c = RockCos and RockCos ± RockBand,
    as literals. A text test regenerates it and diffs, so the thresholds follow the material and never depend on compiler float constants.
  - `TerrainScatter.{h,cpp}`: `MakeSnapshot(HF, Tile)` (game thread), `EvaluateTile(Snapshot, Palette, Out)` (any thread),
    `BuildInstance(Record, Palette)` (the only record → transform and custom-data function), `BuildEditScript(const FScatterUnitState&,
    NewRecords)` (any thread), `HashRecord`, `ReferenceHash(HF, Palette)`, `DirtyTilesFor(...)`, `FScatterScheduler` (tile states, the Busy
    rule and dispatch order, with an injectable executor and clock for tests) and `FFakeIsm` (a pure-C++ model of an ISM's index order under
    the edit script, for tests).
- **`S/Render/TerrainScatterRenderer.{h,cpp}`:** `UChimeraScatterISM : UInstancedStaticMeshComponent` (overrides `CreateSceneProxy` for the
  atomic proxy counters and `SendRenderInstanceData_Concurrent` for the flush timer, F5), the component table keyed `(grid, tile, mesh)`,
  edit-script execution, hiding by cull distance (§3.5), readback for `scatter_verify`, the fresh rebuild for `scatter_fresh`, the settings of §3.7.
- **`S/Game/TerrainScatter.{h,cpp}`:** `ATerrainScatter`, a ticking actor in `TG_PostUpdateWork` with the director as a tick prerequisite.
  It owns `FScatterOptions::FromCommandLine`, the tile table, the scheduler, the task handles, metrics, `HasPendingWork()` and `DescribeJson()`.
- **`S/Tests/TerrainScatterTests.cpp`:** `Chimera.Terrain.Scatter.*`, pure data, runs under `-nullrhi`; plus `Chimera.Terrain.Scatter.EngineIsm`
  (the `FFakeIsm` model against a real ISM's arrays, §4 S2).
- **Edits to existing files (after the ground-look workflow's final commit, F17):**
  - `Game/TerrainActor.{h,cpp}` (task S4a, landed alone so C9 never waits on the renderer): `FOnTerrainChanged` (§3.5), five broadcast sites,
    `AddPendingWorkProvider(TFunction<bool()>)` OR-ed into `HasPendingWork`, a `GetBrushRingCenter()` getter, per-kind event counts for
    `results.json`. Nothing else.
  - `Game/ChimeraTerrainGameMode.cpp`: spawn `ATerrainScatter` after `InitTerrain` and before `Super::StartPlay`; hand it to the director.
    Enabling scatter dirties every tile and bumps the epoch itself, so it does not depend on `InitTerrain`'s broadcast (which fires before the
    scatter actor exists). Options are parsed in scatter's own file, so this is about four lines.
  - `Game/TerrainLighting.{h,cpp}`: only if S6 tries contact shadows (§3.7): one override key `SunContactShadowM` (default 0, world space) and a
    setter that scatter calls at enable and disable.
  - `Test/TerrainScriptDirector.{h,cpp}`: new ops (§3.8), hash fields, the results block, the scatter frame series, a `frames` field for `idle`
    if it has none.
  - `Game/TerrainHud.cpp`: one line (instances, dirty tiles, last apply ms).
  - `ChimeraTerrain.Build.cs` and `.uproject`: no change (ISM is Engine, `UE::Tasks` is Core).

### 3.2 Data flow
```
ATerrainActor (only writer of HF)
  Init / ApplyTick / EndStroke / ApplyDelta(Undo|Redo) / LoadFrom
     └─ OnTerrainChanged.Broadcast({Kind, HeightRect, SplatRect, Chunks, Gen})     game thread, microseconds; outside FTerrainTickTiming
ATerrainScatter::OnTerrainChanged: rects + apron → dirty bits on fine and coarse tiles (no allocation)
ATerrainScatter::Tick (TG_PostUpdateWork, after the director)
  1 poll finished tasks                       → ready queue (stale epoch → discard)
  2 apply under budget (1.0 ms predictive; 8 ms in the load fill): per unit (tile, mesh) run its edit script:
        BatchUpdateInstancesTransforms on index runs → RemoveInstances(reverse-sorted, swap) → AddInstances(..., false, false, false)
        → SetCustomData(start, end) ; swap in the new FScatterUnitState ; last unit of a tile → the tile stops being Busy
  3 dispatch (≤ 2 in flight; never a Busy tile; coarse tiles only at stroke end): MakeSnapshot (memcpy) → UE::Tasks::Launch(BackgroundNormal):
        EvaluateTile → BuildInstance (transforms + custom data) → BuildEditScript against the tile's current unit states
  4 metrics (scatter_gt_ms, queue, busy, in flight)
End of frame (engine): ISM SendRenderInstanceData_Concurrent flushes partial instance updates and bounds (F1); timed as scatter_flush_ms
Director (scripts only): hash → ReferenceHash(HF) synchronous; scatter_verify reads the ISMs back; scatter_fresh rebuilds proxies
```
Nothing flows back into `HF`, `TerrainIO`, `TerrainSimExport`, `terrain.json`, `height.r32`, `splat.rgba8` or the sim. Workers read only the
snapshot, the immutable palette and the immutable unit states they were given (shared pointers, replaced on the game thread after apply).
A tile is Busy from dispatch until its last unit is applied, so a script always applies to the state it was built against.

### 3.3 Grids, lattice and integer inputs
- **Tiles.** Fine 32 m (allowed 16 or 64 by option) and coarse 80 m (allowed 40 or 160), keyed `FloorDiv(x, tile)` from the origin and
  clipped to the map. Each class's lattice cell divides every allowed tile size of its grid, so a candidate belongs to exactly one tile
  whatever the tile size. Chunks are not used anywhere.
- **Candidates.** A candidate is `(class stream, ix, iy, slot)` with `ix = FloorDiv(x, cell)` in Q16 metres from the map centre. Each class
  and each field has a fixed integer stream id, never its enum order, so adding a class reshuffles nothing.
  `h0 = Key(seed, stream, ix, iy, slot)` chains `Mix32`; sub-draws are `Mix32(h0 ^ k)`. Position:
  `x = ix·c + c·(1−J)/2 + (((int64)(h0 & 0xFFFF) · cJ) >> 16)` with `cJ = c·J` in Q16 (J = jitter fraction; trees 0.5, so trunks are at least
  2 m apart), same in y from the high half. **Every Q16 product is `int64`** (65,535 × 4 m in Q16 overflows int32). Candidates with |x| or
  |y| ≥ E are dropped. XY never depends on the terrain, so a sculpt moves Z and tilt only.
- **Heights.** `HQ16 = (int32)FloorToDouble((double)h · 65536 + 0.5)`, once per snapshot vertex (exact scale). `SurfaceQ16` repeats
  `SampleSurface`'s cell and triangle choice in `int64` with arithmetic shifts. It differs from `SampleSurface` by at most 2⁻¹⁵ m (the one
  bound, gated in S2's test).
- **Slope and gradient** come from the candidate's own cell triangle (its vertices, never `GetNormal`'s 8-neighbour stencil):
  `gx, gy` = the triangle's HQ16 height differences per metre (exact integers, stored in the record), `G2 = gx² + gy²` in Q32.
  Thresholds are `tan²θ · 2³²` integers: 21.8°, 25°, 30°, 35° and 40° from the palette, and the material's rock band edges from
  `TerrainLookShared.h` (`RockCos` = 35°; RockCos ± RockBand = 23.2° and 44.0° today, F27). No sqrt in a decision. The material's rock edge
  is also broken by clump noise (F27), which integer slope rules cannot copy: scatter obeys its own palette thresholds, and how well grass
  avoids material rock is measured (M10), not claimed.
- **Splat** is read bilinearly at texel centres `(T + 0.5)·0.5 − E` in Q8, from `HF.Splat` bytes, edges clamped: `wG, wD, wR, wS` in 0..255.
  A ring read `wD_ring(r)` is the max bilinear dirt weight at 8 points on radius r (offsets rounded to Q16).
- **Prohibited in generator code** (text test): `float`/`double` in any accept, select, position or Z decision; `FMath::Rand`,
  `FRandomStream`, `HashCombine`, `GFrameCounter`, `FPlatformTime`, `DeltaTime`, camera state, mesh bounds, and iteration over `TMap`/`TSet`.
  Doubles appear only in `BuildInstance` (record → `FTransform` and custom-data floats), and in materials.

### 3.4 Placement rules: Manor Lords meadow structure
Notation (all Q16): `R(v,a,b)` ramps 0→1 from a to b, `R̄ = 1 − R`, `B(v,a,p,b)` is a triangle. Common factors:
`G = R(wG,96,224)`; `E3 = R̄(wD,32,128)·R̄(wR,32,112)·R̄(wS,16,64)`; `Sg = R̄(slope,25°,35°)`; `St = R̄(slope,21.8°,30°)`;
`Hg = R̄(z,80,110 m)`; `Ht = R̄(z,40,60 m)`. Grass, tussock, flower, near-card, fern and tree factors fall as dirt, rock, snow or slope rise,
and acceptance compares a fixed per-candidate roll with the product, so painting removes those classes and never moves them. **Two deliberate
exceptions gain from painting:** rocks (rock paint, and a tree painted away frees its cell) and shrubs (the hedge term adds them along new path
verges). S2's monotone test exempts exactly these two and tests the hedge gain positively.

**Fields** (integer value noise on the world lattice, two octaves where listed; the second octave on exactly rotated integer coordinates
`(⌊(4x−3y)/5⌋, ⌊(3x+4y)/5⌋)` so patterns stay off the axes):
| Field | Scales | Role |
|---|---|---|
| W woodland | 96 m + 37 m (0.65/0.35), warped 30 m | forest masses. **Border bias:** `W' = min(1, W + 0.35·R(max(\|x\|,\|y\|), 118 m, 140 m))`, so the 128-160 m ring outside the playable area becomes forest and hides the map edge (F16) |
| M dryness | 60 m + 23 m (its own periods; the ground's `PatchM` is now 140 m and is not shared, F27) | lush vs dry grass density, tussock colour. Grass *colour* follows the ground through the shared patch include (§3.7) |
| T tussock | 9 m, warped 4 m | dark tussock islands (press_03) |
| F drift, Fc drift colour | 18 m + 7 m; colour 30 m | single-colour flower drifts, not a sprinkle |
| C stand | 120 m | broadleaf vs conifer stands |
| K grove seeds | 32 m lattice, p 0.20, radius 6-14 m from the hash | groves and small clumps in open meadow (ml_01, news_016) |
| Fs, Fh, Fr | 9 m, 40 m, 25 m | shrub clumps, which path stretches get hedges, rare boulders |

**Classes** (fine grid = grass layer; coarse grid = everything that casts, plus ferns). Layers (for `scatter_visible`, `ScatterLayers` and
C1S): `grass` = T0, T1, near card; `groundcover` = tussock, flower, fern; `shrubs` = shrub; `trees` = tree, sapling; `rocks` = rock.
| Class → meshes | Grid, cell | Accept probability | Scale, align, sink | Cull end (start) |
|---|---|---|---|---|
| Grass → `GrassT0` (rank < 0.55), `GrassT1` | fine, 0.5 m | `0.80·G·E3·Sg·Hg·(0.65 + 0.35·(1−M))·(1 − 0.6·R(W',0.62,0.75))` | xy 0.8-1.25, z 0.7-1.3; z × 0.6 on verges (`wD_ring(1.5 m) ≥ 64` and `wD < 32`); align 0.5; sink 2 cm | T0 130 m, T1 90 m |
| Tussock → `Tussock` | fine, 2 m | `(T ≥ 0.64 ? 0.60 : 0.02)·E3·Sg·Hg·(0.5 + 0.5·(1−M)) + 0.30·B(W',0.50,0.58,0.66)·E3·Sg` | 0.9-1.4; align 0.4; sink 3 cm; dry tint when M > 0.6 | 150 m |
| Flower → `Flower` | fine, 1 m | `0.60·R(F,0.70,0.85)·R(wG,200,235)·E3·R̄(slope,22°,32°)·R̄(W',0.55,0.65)` | 1.2-1.8; align 0.3; colour from Fc: yellow 50 %, white 30 %, violet 20 %, 15 % take the next | 110 m |
| Near card → `NearCard` (L1 only) | fine, 1 m | `0.60·G·E3·Sg` | 2.2-2.8; align 0.6 | 32 m (fade from 22 m) |
| Tree → `TreeBroadA/B`, `TreeConiferA/B` | coarse, 4 m, J 0.5 | zone: core `W' ≥ 0.70` 0.35; edge 0.62-0.70 0.10; grove (inside a K disc) 0.25; lone 0.002 (broadleaf, ×1.15). × trunk `wD ≤ 32`, `wR ≤ 64`; 4 probes at 4 m `wD ≤ 64` (no trees on paths); × St·Ht; broadleaf `wS ≤ 24`; conifer `wS ≤ 150`, half p when `wS ≥ 60`. Conifer when `roll < R(C,0.40,0.70) + 0.5·R(z,20,50 m)` | broadleaf 0.9-1.2 × 11 m nominal height, conifer 0.85-1.15; upright; base z = min of the surface at the centre and 4 probes at 0.8 m, minus 5 cm | 600 m (never at this map size) |
| Sapling → `TreeBroadA` at 0.25-0.40 | coarse, 4 m (own stream) | `0.30·(B(W',0.45,0.55,0.62) + grove fringe)`·trunk and ring rules | as trees | 300 m (as built: 600 m, the broadleaf units' end; EXECUTION §8 S5 record) |
| Shrub → `ShrubA/B` | coarse, 2 m | `0.35·B(W',0.50,0.58,0.64) + 0.08·R(Fs,0.75,0.90)·G·E3 + 0.25·B(wD_ring(3 m),48,96,160)·R̄(wD,8,24)·R(Fh,0.50,0.70)`, all × `R̄(wR,32,128)·R̄(wS,16,48)·Sg·R̄(z,55,85 m)` | 0.8-1.6; align 0.3; sink 8 cm | 300 m |
| Fern → `Fern` | coarse, 2 m | `0.80·R(W',0.55,0.70)·E3·R̄(slope,30°,40°)` | 1.2-1.8; align 0.4; sink 3 cm | 100 m |
| Rock → `RockA/B` | coarse, 4 m | `0.60·B(wR,48,112,176)·R̄(wS,64,160) + 0.15·R(slope,25°,40°) + 0.01·R(Fr,0.80,0.95)`; rejected if the tree candidate of the same cell is accepted | 0.6-1.8 (cubic skew); align 0.8 plus up to 8° hashed tilt; sink 25 % of the palette's nominal rock height × scale | 300 m |

Z rules use palette constants only, never mesh bounds, so swapping L0 and L1 meshes never moves a record. Note that rock paint makes rocks in a
footprint's **edge band** (where `wR` passes 48-176), not in a full-strength core (`B` is 0 at `wR` 255); THINX's positive control (§3.8)
measures the band. Coverage held by tests at the default seed: woodland (`W' ≥ 0.62`) 12-20 % of the playable interior and 60-90 % of the
border ring; flower drifts 5-12 % of the meadow. Expected totals on a flat all-grass E = 160 map (EST): grass about 150k, near cards about 35k
(drawn only inside 32 m), flowers about 8k, tussocks about 3k, ferns about 3k, shrubs and saplings about 2.5k, trees 1.5-2k, rocks about 200.
Drawn at `rts80` (EST, r8c §7): about 19k grass, 2k flowers, 1.5k tussocks, 0.9k shrubs, 0.4k trees, 0.1k rocks. Every constant above lives
in the palette table: a look round changes numbers, never code, and every change moves `config_fnv` on purpose.

### 3.5 Records, hashes, the change event and the update path
- **Record** (integers only): `{class u8, mesh u8, stream u16, ix i32, iy i32, slot u8, xq i32, yq i32, zq i32, gxq i32, gyq i32, yaw u16,
  scale u16, zscale u16, tilt u16, cd0 u16, cd1 u16}`. `gxq, gyq` are the cell-triangle gradient (§3.3), stored for every class.
  `BuildInstance(record, palette)` is the only source of what an instance shows: translation, yaw, scale, zscale, hashed tilt, the alignment of
  the up axis to the normal `(−gx, −gy, 1)` by the class's align factor, and the custom data: fine grid 4 floats (cd0 dryness, cd1 tint or
  flower colour, terrain normal x and y from `gxq, gyq`); coarse grid 2 floats (cd0 variation, cd1 stand). So a sculpt that tilts a cell
  without moving `zq` changes the record, and the hash, the edit script and the readback all see it.
- **Hashes** (in `results.json`, never in the terrain files). Per record `h = HashRecord(r)`: FNV-1a 64 over the fields in the order above,
  each little-endian at its fixed width (the `Fnv1aU32LE` pattern of `S/Data/TerrainHeightfield.h:21-24`), **never the struct bytes** (3 padding
  bytes follow `slot`). Per class `{count, Σh mod 2⁶⁴, ⊕h}`. `scatter_fnv` = `Fnv1a64(config_fnv, E, level, the per-class triples in class
  order)`: independent of tile size, tile order, threading and ISM order. It is computed by `ReferenceHash`: a synchronous full regeneration with
  `ParallelFor` over tiles (EST 20-60 ms; test ops only). `scatter_live_fnv` is the same fold over the applied unit states. `scatter_count` and
  per-class counts go beside them.
  **`config_fnv` folds** the palette table after `ScatterParams`, `ScatterDensity`, `ScatterSeed`, the level and its mesh slot list. It does
  **not** fold tile sizes, threads, budgets, the during-stroke intervals, mobility, apply mode, governor, layers or grass Nanite: those change
  timing or drawing, never records.
- **Event.** `struct FTerrainChange { ETerrainChangeKind Kind; FTerrainRect HeightRect, SplatRect; TConstArrayView<int32> Chunks; uint64 Gen; }`,
  `Kind ∈ {Init, Tick, StrokeEnd, Undo, Redo, Load}`, native multicast `FOnTerrainChanged`. Broadcast at: end of `InitTerrain`; `ApplyTick` after
  the unions (`:237-238`, outside the tick timing so P1 is unchanged); `EndStroke` after `SubmitCollision` (`:297`); end of `ApplyDelta` (reason →
  Undo or Redo; `Chunks` = the delta's restored chunk ids, empty for the other kinds); `LoadFrom` after `Splat.UpdateAll` (`:385`). Stroke state
  is read from `IsStrokeOpen()`. C9's units subscribe to the same event.
- **Dirty tiles.** A changed vertex rect `[X0,X1)` dirties world `[X0−E−1−A, X1−E+A]`; a texel rect `[T0,T1)` dirties
  `[T0/2−E−0.5−A, T1/2−E+0.5+A]`. Apron `A` = 2.5 m on the fine grid (1.5 m verge ring plus bilinear reach) and 5 m on the coarse grid (4 m
  path ring plus trunk probes). Marking is per tick, never from the stroke's union box (which over-covers diagonals about 2×, r8d §4.3).
  Undo and redo dirty each restored chunk's `HF.ChunkOwnedRect` and `ChunkOwnedSplatRect` (F9); Init, Load and scatter enable dirty
  everything and bump the epoch. The "incremental equals full" test proves the apron.
- **Scheduler** (`FScatterScheduler`, pure). Per tile `{DirtyGen, DispatchedGen, AppliedGen, Busy}`. **A tile is Busy from dispatch until the
  last unit of its result has been applied and its unit states swapped**; a Busy tile is never dispatched again, and new dirt only raises its
  `DirtyGen`. A tile is current when `!Busy && AppliedGen ≥ DirtyGen`.
  - Stroke open: a dirty **fine** tile is dispatched at most every `ScatterDuringStroke` ms (default 150; 0 = stroke end only), nearest the
    brush first. **Coarse** tiles wait for stroke end (`ScatterCasterDuringStroke`, default 0; a value in ms is the mid-stroke A/B), because each
    caster-unit edit costs a whole-unit shadow re-render for the next 100 frames (F20). During a long raise, trees and rocks therefore sit at
    their old height until the stroke ends; VIDEOX shows it.
  - Stroke end, undo, redo, load, enable: every dirty tile at once, nearest the last brush centre first, ties by tile index.
  - At most `ScatterThreads` tasks in flight (2, own counter). `ScatterThreads=0` generates on the game thread, one whole tile per frame with
    no budget: a determinism path only (s1x_b), never counted by SX11 or SX12.
  - A result whose gen is newer than the applied gen is applied even if newer dirt arrived; the tile then stops being Busy, is still dirty and
    is redispatched (`stale_result_redispatches`). A result from an old epoch is discarded and counted. `scatter 0` and `load` bump the epoch.
- **Apply (keyed edit script).** For each unit (tile, mesh) the worker merges the new records (sorted by key) with the unit's applied state:
  unchanged records produce nothing; changed ones an in-place transform and custom-data update; missing ones a removal; new ones an append.
  Removals run in descending index order with remove-at-swap, so the swapped-in element always has a higher index and was already handled.
  The worker also returns the next unit state in the ISM's new index order. The game thread only executes the engine calls, **one whole unit
  per step** (a unit is never half-applied). Budget `ScatterBudgetMs` = 1.0 ms, **predictive**: a unit starts only if elapsed + its predicted
  cost ≤ the budget, except the frame's first unit, which always runs. Predicted cost = `a + b·changes + c·instances_after`, fitted in S4 from
  the `apply_unit_ms` rows and stored as option defaults (not hashed). Unit size is at most about 2,300 instances (a 32 m tile's grass T0
  tier). If S4 measures any unit above 0.8 ms, the grass T0 tier splits by rank band into two units per tile (744 primitives, reported against
  r8c's ~700 cap); 16 m fine tiles are not the lever (they quadruple fine primitives to 2,144). During the init and load fill the budget is
  8 ms, and those frames carry their own phase, excluded from the hitch bars. `-ChimeraTerrainScatterApply=clear` (ClearInstances +
  AddInstances) stays for the A/B and as a fallback.
- **Components** for every `(tile, mesh)` pair are created once at enable, with every setting of §3.7 including caster flags and governor
  step 5, an explicit fixed non-zero `InstancingRandomSeed` (F3) and `SetRemoveSwap()`, so PSO precaching happens at enable, not mid-stroke.
  **Scatter never calls `SetVisibility` or `SetCastShadow` after enable** (F18): `scatter_visible`, `ScatterLayers` and governor steps 1 and 4
  hide a unit with `SetCullDistances(0, 1)` and restore its class's cull distances, which updates the live proxy in place (F6). A toggle still
  invalidates casters' shadow pages, so every script runs `settle 150` after a toggle before it samples.
- **Proxy counters** (`UChimeraScatterISM::CreateSceneProxy`, `std::atomic<int32>`, non-null returns only): a component's first proxy →
  `proxy_first_creates`; a later one while the component's atomic "expected rebuilds" count is above zero (raised on the game thread before
  scatter's own rebuild calls: enable, `scatter 1`, `ScatterApply=clear`, `scatter_fresh`) → `proxy_expected_rebuilds`; any other later one →
  `proxy_recreates`, gated 0 (SX8). Counters are read on the game thread after `FlushRenderingCommands` when an op needs them.
  **Amended 2026-10-02 (S4 ruling, EXECUTION §8):** later proxies are attributed per component. A recreate whose dirty mark came from
  scatter's own apply always counts in `proxy_recreates`, whatever else applies. Otherwise these are reported, not counted there: refills (a
  unit emptied, then refilled), PSO-precache completions, engine-context recreates, and editor-compile recreates (the component's render state
  dirtied by `PropagateMaterialChangesToPrimitives` / `RedrawAllViewports` or an asset post-compile event). Gated 0: `proxy_recreates`,
  `proxy_compile_recreates_during_edits`, `proxy_engine_recreates_during_edits`. Packaged runs (S7) compile nothing, so they test the strict count.
- **`HasPendingWork`** is true while any tile is dirty or Busy, a unit was applied this frame (its flush is pending), or a scatter component
  that `ShouldComponentAddToScene()` holds instances and has no proxy (first fill or PSO delay, F3). Because scatter never hides a component
  by visibility, the last term cannot wait on a hidden one.
- **Teardown and nullrhi.** `EndPlay` waits on every task handle. `scatter 0` bumps the epoch, clears every unit and resets unit states. Under
  `!FApp::CanEverRender()` the generator, hashes and tests run and no component is created (the splat texture's precedent).

### 3.6 Assets, licences, import (text first)
| Slot | L0 (bring-up, fallback; project-original) | L1 (final candidate) | Licence |
|---|---|---|---|
| grass T0/T1 | procedural opaque tapered-blade clumps, 2 shapes, 36-48 tris | same (procedural is the final grass) | project-original |
| tussock, flower, fern | procedural: blade dome ≤ 200 tris; stems + 6-tri heads ≤ 64; fronds ≤ 300 | flowers and ferns stay procedural unless the art director asks for Poly Haven `celandine_01` / `fern_02` | project-original / CC0 |
| near card | none | Poly Haven `grass_medium_01` mesh Plane.042 (310 tris, masked) | CC0 |
| trees | procedural trunk + 7-14 displaced canopy lobes (≤ 4k), stacked noisy cones (≤ 3k) | Poly Haven `tree_small_02` (2 variants: as is and crown z×1.3, xy×0.85; rescaled from 2.8 m to the 11 m nominal height, about ×4, so its leaves grow too) and `fir_tree_01` variants 0 and 1, Blender-decimated to ≤ 30k tris with UVs and alpha kept, feet pivot | CC0 |
| shrubs | procedural lumpy leaf-mass blobs, 2 variants, 600-1,500 tris | Blender composite of Poly Haven `fern_02`, `nettle_plant`, `weed_plant_02`, `shrub_04` on a seeded hemisphere, about 3-4k tris | CC0-derived |
| rocks | displaced icospheres ≤ 800 | Poly Haven `rock_09`, `rock_07` (Nanite, not decimated) | CC0 |

- **Procedural meshes:** `T/Tools/make_scatter_meshes.py`, numpy only, a small glTF-binary writer, all randomness from the same `Mix32` as C++
  (a golden test on both sides), parameters in `T/Scripts/scatter/meshes.json`. Vertex colour R = AO, A = height fraction. Re-runs are byte-identical.
- **Fetch:** `T/Tools/fetch_scatter_assets.py` (the `fetch_textures.py` pattern): `api.polyhaven.com/files/<id>`, 1k glTF + `.bin` + `diff`,
  `arm`, `nor_gl`, User-Agent `ChimeraScatterFetch/1.0`, API md5 verified, sha256 recorded, a free-disk check ≥ 3 GB, also two CC0 textures for
  the L0 canopy and bark (ids checked against `/info`; missing → macro-noise fallback). About 620 MB. `--check` validates the manifest.
- **Blender prep:** `T/Tools/blender/prep_polyhaven.py` and `T/Tools/blender/make_bush.py` run headless with
  `D:/tools/blender/blender-4.5.10-windows-x64/blender.exe --background --factory-startup --python <script> -- <args>`: pick variants, decimate
  (collapse + planar), pivot at the base, rescale trees to their slot's nominal height, write one `.glb` per mesh and `prepared/report.json`
  (triangles, bounds, pivot, vertex-position hash, target height and scale factor). **Normal maps stay `nor_gl`** in the `.glb`: Interchange
  flips the green of every glTF normal texture once (F23), so converting in Blender would flip them twice.
  `T/Tools/preview_scatter.py` renders a contact sheet of every L0 and L1 mesh for Alec.
- **Manifest:** `T/ScatterSrc/manifest.json`, one row per mesh and texture: id, source (`procedural:make_scatter_meshes.py@<sha256>`,
  `polyhaven:<id>`, or `epic:/BaseMaterial/...` for the engine-plugin noise the grass shares with the ground), URL, licence, licence URL, files
  with sha256, variant mesh names, glTF and imported triangle counts, date. Copied to `EV/c/c-S1-scatter-manifest.json` with `evidence.py add`.
  No Epic sample content, no Fab or Quixel, no login.
- **Import:** `T/Scripts/make_scatter_assets.py` through `T/Tools/run_commandlet.ps1 -Sentinel SCATTER_OK` (C7's pattern; Interchange
  `AssetImportTask` as `tools/unreal-looktest/lt_import.py:17-29`). Meshes to `/Game/Terrain/Scatter/Meshes/<L0|L1>/`, textures to `.../Textures`
  (`NeverStream`, so shots never catch half-streamed mips), materials built from `T/Scripts/scatter/ChimeraScatter.hlsl` (one Custom node per
  material, the `make_ground_material.py` pattern; the opacity mask is a separate standard `TextureSample` expression, F24). It: sets Nanite per
  §3.7; builds the grass `_L` variant (Nanite off, 3 LODs) for the A/B; turns off distance fields and simple collision; sets and then **asserts**
  `bUsedWithInstancedStaticMeshes` and, for Nanite rows, `bUsedWithNanite` (F7); asserts each material's "uses per-instance random" statistic is
  false (F25); replaces Interchange's generated materials and deletes them; asserts through the asset registry that every scatter package's
  **game** hard dependencies (`EDependencyQuery::Game`, `CoreUObject/Public/Misc/AssetRegistryInterface.h:120`; editor-only import data is
  excluded) are under `/Game/Terrain/**`, `/Engine/**`, `/BaseMaterial/**` or `/Script/**`; records each texture's `flip_green_channel` and asserts
  it per row (normal maps true); reads back the vertex colours of three imported meshes and asserts R within ±2/255 of the generator's values
  (a linear-vs-sRGB conversion fails here, not in a shot); writes `T/Out/scatter_assets/report.json` (Nanite rows report the **source**
  triangle count, not the fallback mesh's; the commandlet cites the header it reads it from); prints
  `SCATTER_OK meshes=<n> materials=<m> errors=0 usage_ok=<k>/<k> deps_ok=1 flip_ok=1 vc_ok=1`.
- **Cook and load:** everything sits under `/Game/Terrain`, already in `DirectoriesToAlwaysCook` (`T/Config/DefaultGame.ini`): no ini edit.
  The scatter actor loads meshes and materials by C++ string path with `LOAD_Quiet | LOAD_NoWarn` at enable, before the first dispatch; with
  scatter off nothing is loaded. A missing mesh logs one `LogChimeraTerrain: Error` naming the class when scatter was requested on the command
  line, otherwise a Warning, and its class is skipped.
- **Git:** `T/ScatterSrc/` gets one `U/.gitignore` line; `Content/` and `Out/` are already ignored; scripts live under `Tools/` and `Scripts/`,
  which `sync_to_repo.sh` mirrors.

### 3.7 Rendering, budget, governor
**Every scatter component** (set before `RegisterComponent`): class `UChimeraScatterISM` (never HISM; HISM forces dynamic relevance and
rebuilds its tree on edits, r8c §3.1); Mobility **Stationary** (`-ChimeraTerrainScatterMobility=static|movable` for diagnosis only), the actor
root too; `bDisableCollision = true`, `NoCollision`, `SetCanEverAffectNavigation(false)`, no overlap events; `bAffectDistanceFieldLighting`,
`bAffectDynamicIndirectLighting`, `bReceivesDecals`, `bVisibleInRayTracing` off; `bEvaluateWorldPositionOffset = false`;
`NanitePixelProgrammableDistance = 0` (F19); `ShadowCacheInvalidationBehavior` Auto; `bUseGpuLodSelection` on;
`ComponentTags = {ChimeraScatter.<class>}`.

| Class | Nanite | Blend | VSM shadow | Occluder |
|---|---|---|---|---|
| grass T0/T1 | yes (A/B: `_L`, `bDisallowNanite`, fade) | opaque, two-sided | no | no |
| tussock, flower | yes | opaque, two-sided | no | no |
| near card | no | masked 0.33, two-sided, ISM fade | no | no |
| fern | yes | opaque (L0) / masked (L1) | no | no |
| shrub, tree | yes | opaque (L0); bark opaque + masked leaves (L1) | yes | yes |
| rock | yes | opaque | yes | yes |

**Contact shadows: none by default.** The sun has no contact-shadow length (F22), and at `rts80` a 0.3-0.5 m screen-space shadow covers only a
few pixels. Grass grounding comes from VC AO and the terrain-normal shading below. S6 may try `SunContactShadowM` (0.3-0.5 m, world space)
as one round option with an on/off shot pair; if the art director keeps it, scatter sets it at enable and clears it at disable, so scatter-off
gate runs stay unchanged, and its cost is part of `scatter_gpu_ms`.

Lumen stays off; `T/Config/DefaultEngine.ini` is untouched (run_commandlet's config guard). Materials use Substrate's blendable GBuffer,
single slab, and **never read `PerInstanceRandom`** (F25): all variation comes from the record's custom data. **Grass material:** base colour =
lerp(root, tip, VC.A) × lerp(lush, dry, cd0) × (0.9 + 0.2·cd1) × the ground's patch tint at the instance origin; AO = VC.R; shading normal =
lerp(geometry normal, terrain normal from cd2/cd3, 0.75), so grass lights like the ground under it and does not glitter; roughness 0.85; no
WPO, no time term, so compare mode needs nothing new. Until S6, the patch tint is the ground's macro-noise modulation with scalars copied from
`M_ChimeraGround` at build time. In S6, after the ground-look final commit, the ground's patch block moves into `T/Scripts/ChimeraPatch.hlsl`,
which both material scripts inline into their Custom-node strings; the refactor is accepted only if `M_ChimeraGround`'s fully expanded
Custom-node code string (written by `make_ground_material.py` to its report) is **byte-identical** before and after (§4 S6). Flowers add a
vertex-colour head mask with the colour index in cd1. L0 canopies use a triplanar leaf texture, a detail normal, tint and VC AO. Text tests on
`ChimeraScatter.hlsl`: no `PerInstanceRandom`, and no `ddx`, `ddy` or `fwidth` in the opacity-mask path (F24). No impostors, no HLOD, no Nanite
Foliage.

**GPU split at `rts80`, 1080p, GPU p50** (r8c §8, against 16.67 ms): fixed world 8.6 (measured, F13) + terrain 2.95 (measured, P4 ≤ 3.0) +
**scatter 1.8 target, 2.5 ceiling** (EST band 0.9-2.6: grass 0.6, trees and shadows 0.55, shrubs and rocks 0.3, flowers 0.1, culling 0.25) +
1,000 units 1.4 (EST until C9) + spare 0.4. Caps at `rts80` (reported, analytic): drawn ≤ 24k, LOD-weighted triangles ≤ 1.5 M, primitives ≤ 700
(this layout has at most 100×5 + 16×9 = 644; 744 if the T0 rank split is taken). CPU: scatter's own game-thread time p99 ≤ 1.5 ms, and the
frame's GT and RT p99 each within 1.5 ms of C1's (SX11).

**Governor ladder** (presentation only; `-ChimeraTerrainScatterGovernor=0..6`; fixed levers applied at enable, cvars set through
`IConsoleManager` and read back into `results.json`, where a parser bar checks each read-back, Shipping included): (1) hide grass T1 (cull
hide, rank-based, the hash is unchanged); (2) cull ends × 0.8; (3) `r.Nanite.MaxPixelsPerEdge` 1 → 2; (4) flowers hidden, tussock end 150 →
110 m; (5) no VSM shadow on caster units of coarse tiles whose centre is more than 200 m from the `rts80` ground focus (set before the first
fill, never per pose); (6) `r.Shadow.Virtual.ResolutionLodBiasDirectional` +1 (every shadow, terrain included, so it is the last scatter
step). Nanite raster time budgets are not used: they act only under dynamic resolution (F21), which would also scale the screen percentage and
is a runtime adaptive governor (§7). Step 7 is not scatter's: up to 3.7 ms sits in SSGI, clouds and sun (r8c E5), a lighting decision for
Alec, raised only with `ProfileGPU` numbers.
**Kill rule:** scatter is not on by default in the packaged look if, after step 6, `scatter_gpu_ms` > 2.5 at `rts80`, or C1US median < 60
or 1%-low < 45, or SX11 still fails after its levers (§6 risk 1). It then ships behind `-ChimeraTerrainScatter=1` and Alec gets the numbers.

### 3.8 Harness: options, ops, results, scripts, parser
**Options** (`FScatterOptions::FromCommandLine`, `FParse::Value(..., false)`; every one is its own configuration by F11):
`-ChimeraTerrainScatter=0|1` (default 1 without `-ChimeraTerrainScript`, 0 with it, the mouse controller's precedent), `ScatterSeed=<u32>`,
`ScatterLevel=L0|L1` (default L0 until S6 picks), `ScatterDuringStroke=<ms>` (150, fine tiles), `ScatterCasterDuringStroke=<ms>` (0 = stroke
end, coarse tiles), `ScatterBudgetMs` (1.0), `ScatterLoadBudgetMs` (8), `ScatterThreads=0..4` (2), `ScatterLayers=<csv>` (visibility only:
cull hide, records and hashes unchanged), `ScatterDensity=<0..1>` (grass and flower base p; moves the hash), `ScatterGovernor=0..6`,
`ScatterMobility` (diagnosis), `ScatterGrassNanite=0|1` (1), `ScatterApply=diff|clear`, `ScatterFineTileM=16|32|64`,
`ScatterCoarseTileM=40|80|160`, `ScatterParams=Name=Value,...` (palette overrides; folded into `config_fnv`). Each is spelled
`-ChimeraTerrainScatter<Name>=`. §3.5 lists which ones fold into `config_fnv`.

**Ops** (in `StepOp`; each scatter op fails with "needs -ChimeraTerrainScatter=1" otherwise, so scatter frames can never pool into the gate
configuration):
- `scatter {value}`: enable (load assets, dirty all, bump the epoch, fill) or disable (bump the epoch, cancel, clear, reset unit states).
- `scatter_wait {timeout_s=120}`: until `HasPendingWork()` is false, then 2 frames; records `scatter_wait_ms`. Every script follows a
  `scatter_wait` that precedes a shot with `settle 150` (Nanite pages, TSR, VSM's 100-frame static threshold).
- `scatter_visible {value, layers="all"|"grass,..."}`: cull-distance hide or restore (§3.5); generation continues; no proxy changes.
- `scatter_verify {name}`: live == reference; ISM readback (`GetInstanceCount`, `GetInstanceTransform`, custom data) equals
  `BuildInstance` of the unit states within 0.01 cm, 1e-4 rad and 1/65536; independently of the records, every instance z equals its rule's z
  recomputed with `HF.SampleSurface` within 1 mm, and every instance's up axis equals the axis recomputed from HF's cell triangle, the class
  align factor and the record's tilt within 1e-3 rad; no tile dirty or Busy.
- `scatter_fresh {name}`: test only. Raises every component's expected-rebuild count and calls `MarkRenderStateDirty` (proxies rebuilt from
  the CPU arrays), sets `r.Shadow.Virtual.Cache.ForceInvalidateDirectional 1` for 10 frames then restores it, `settle 150`, shoots
  `<name>_fresh`. The pair `<name>` / `<name>_fresh` is SX18's oracle for what the GPU draws.
- `scatter_dump {name}`: every applied record in canonical LE binary (the `HashRecord` field order, about 45 B per record, about 9 MB) plus
  sha256, to `T/Out/<run>/scatter_<name>.bin`.
- `scatter_check {name, fp}`: per class, instances in the core (inner half radius) of every footprint disc of set `fp`, and what the generator
  yields for the same discs with the splat forced to pure grass (same heights, same seed, computed in the op), plus the outer band
  (0.5R .. R + 2 m) counts; reported per set. Gated thinning reads THINX's rows and the dump oracle, never the generator's own judgement.
- `scatter_counts {name}` → `scatter_counts.json` (per tile and class); `scatter_view_counts {pose, name}` (analytic: per class, instances in
  the frustum inside their cull end; labelled analytic, not what the GPU drew).
- `scatter_target {class, near:[x,y], as, zone?}`: stores the nearest instance's XY **and key**; `zone=core|edge|grove|lone` limits trees to a
  woodland zone. A stroke `path` may then be `"@<as>"`, and `scatter_verify` reports whether each stored key still exists and its z change.
- `scatter_mask {pose, name, layers}`: projected instance bounds of the layers, and the analytic sun-shadow footprint of casters with the sun
  direction read from `ATerrainLighting`, into `footprints.json`; a `union_with` argument merges an earlier mask (SHADX).
- `hash` gains `scatter_fnv`, `scatter_live_fnv`, `scatter_count` only while scatter is enabled (old scripts write identical hash JSON).
  `settle`, `shot` and `depthcheck` wait through the pending-work provider with no change (F10).

**`results.json`:** `options.scatter` (every option, level, mesh paths with asset sha256, `config_fnv`, effective `sg.*` scalability and
governor read-back); `terrain_events` (counts per kind, from S4a); `scatter` block: tiles, units, components, instances per class,
`proxy_first_creates`, `proxy_expected_rebuilds`, `proxy_recreates` (overall and during edits); counters `{dispatched, applied,
skipped_identical, discarded_epoch, cancelled}`; path proofs `{mid_stroke_dispatches, stale_result_redispatches, in_flight_max, busy_max,
gt_generations}`; latency rows `{reason, stroke_id, tiles, frames, ms}`; per-phase series p50/p99/p99.9/max of `scatter_gt_ms` and
`scatter_flush_ms`, `apply_unit_ms` (with instance and change counts, and the predicted cost), `gen_ms`, `mark_us`, `queue_depth`; `init_ms`,
`verifies[]` (with stored-key rows), `checks[]`, `dumps[]`, `view_counts[]`. Everything goes to JSON, never CSV only (Shipping may compile
the CSV profiler out).

**Scripts** (existing ones untouched; all scatter runs pass `-Extra "-ChimeraTerrainScatter=1 ..."`):
- **SXSMOKE:** compare mode; `scatter_wait`; `settle 150`; hash; `scatter_verify`; `scatter_visible 0`, `settle 150`, shot `hidden` (must not
  hang), `scatter_visible 1`, `settle 150`; one raise and one dirt paint; `scatter_wait`; verify; hash; another dirt paint, then `scatter 0`
  while its tiles are Busy and `scatter 1`; `scatter_wait`; verify; `undo 3` (one entry per stroke, plan C §3.7; scatter touches no undo
  entry), `scatter_wait`, `scatter_verify`, hash (reference and live both equal the first hash).
- **S1X:** S1's ops verbatim (so heights and splat equal `s1_a`); S1's two paint ops' footprints also join sets `dirt` and `rock`, the snow
  paint joins `snow` (footprints change no height). Added: `scatter_wait`, `settle 150`, A/A shots `x_before`, `x_before_aa` (its own floor),
  `scatter_verify before` (class floors) and `scatter_dump before`; at `pre_last2`, `after`, `undo`, `redo`: `scatter_wait`, `settle 150`,
  `scatter_verify`, hash, shot `x_<name>`; at `after`, `undo` and `redo` also `scatter_fresh`; `scatter_dump redo`; `scatter_check` per set
  (reported); S1's collision and depth ops unchanged; `look full` shots `x_rts80_full`, `x_oblique`, `x_closeup`, each with a
  `scatter_visible 0` twin (`settle 150` between); `scatter_view_counts` for the three poses; `save` (the `redo` dump's state, read by the dump
  oracle).
- **S1XL:** `idle {frames: 2}` (the enable fill has tasks in flight or unpolled: the director ticks before scatter), then `load` of the
  reference run's saved files (s1x_a in `-game`; s1x_final in the packages, §4 S7), `scatter_wait`, `settle 150`, hash `loaded`,
  `scatter_verify`, shot `x_redo`, `scatter_fresh`.
- **SHADX** (flat map, compare mode, `rts80`): `scatter_visible 0 layers=grass,groundcover` (thin geometry out of the floor),
  `scatter_target tree near [0,40] as t0`; `scatter_mask` of trees; `settle 150`; shots `t_a`, `t_b` 1 s apart; a raise stroke 80 m from
  `@t0`, wait, settle, shot `t_far`, `scatter_fresh`; **raise d60 s20 ×20 on `@t0`** (+4 m at the centre, flanks about 8°, under every tree
  slope limit), wait, verify (stored key `t0` must exist with z up by > 2 m), `scatter_mask ... union_with` the first, settle, shot
  `t_raised`, `scatter_fresh`; undo, wait, settle, shot `t_undo`, `scatter_fresh`. CSV capture with `-csvCategories=VSM` across the edits
  (page counts reported; UNVERIFIED that page stats fill without `r.Shadow.Virtual.ShowStats`).
- **THINX** (new; flat all-grass map, compare mode): `scatter_target flower near [x1,y1] as f0`, `scatter_target tree near [x2,y2] zone=core
  as w0`; paint dirt d20 on `@f0` and on a meadow point (set `dirt`), rock d24 (set `rock`), snow d24 (set `snow`), each on open meadow, and a
  dirt path d10, ≥ 100 m long, through `@w0` (set `path`); `scatter_wait`; verify; `scatter_check` per set; `scatter_dump end`; `save`. The
  meadow coordinates are fixed in S5 from the field map S2 prints; the minimum-sample rule (§5 SX6) guards the choice.
- **LATX** (new; flat map): 20 isolated strokes per diameter 5, 20, 60, 100, raise and dirt paint alternating, 2 s apart (each stroke's
  latency is measured without the next one's dirt).
- **MOUSEX** (new): MOUSE's ops with scatter on, ending with `scatter_wait`, `scatter_verify`, hash; the real-mouse path with scatter, in
  `-game` (S5) and packaged (S7); same preflight and desktop rules as MOUSE (D9).
- **FAILX** (new): scatter on, a d100 stroke, then `fail` while tasks are in flight: must exit 2, write `results.json`, leave no
  `Saved/Crashes` folder.
- **C1S** (`look full`, `rts80`, `-Extra "-ChimeraTerrainScatter=1"`, no `-csvGpuStats`): `scatter_wait`, `settle 150`, csv start;
  `idle_scatter_on` 20 s; `scatter_visible 0`, `settle 150`, `idle_scatter_off` 10 s; on, `settle 150`, `idle_scatter_on2` 10 s; five
  `idle_layer_off_<layer>` phases of 8 s, one per §3.4 layer, each after `settle 150`; `oblique` and `closeup` on/off 8 s each; back to
  `rts80`, `settle 150`; C1's 60 s `random_walk` as `walk`; `idle_after` 5 s; csv stop. **C1S_ATTR** = one C1S rep with `-csvGpuStats
  -csvCategories=VSM` (per-pass GPU and VSM pages; its own configuration, never pooled or compared). **C1US** = C1S with
  `-ChimeraTerrainUnits=1000` (after C9). **SOAKS** = SOAK with scatter on.
- **LOOKX:** LOOK's map with scatter, `look full` shots at `rts80`, `oblique`, `closeup`, each scatter on and off (paired, `settle 150` each),
  tree/shrub layer masks, and `movie 30` at `rts80` in compare mode with scatter on and off (M9).
- **VIDEOX:** VIDEO with scatter on (`movie 330`), for Alec.

**Parser** (`T/Tools/parse_terrain.py`, tests in `test_parse_terrain.py`): `--scatter RUN`; `--s1x RUN [--pair RUN ...] [--reload RUN] --ref S1_RUN
[--xref S1X_RUN] [--packaged] [--shipping]`; `--shadx RUN`; `--thinx RUN`; `--latx RUN`; **`--equal-hashes A B`** (shared-name height and splat
FNV, final hashes and `ticks_applied == ticks`; scatter keys too when both runs have them; no timing bars, unlike `--same-hash`, F26);
`--same-hash` unchanged (P8, and SX4's s1x_b timing proof); `--summary` gains a SCATTER table and the SX11-SX15 rows. Rules:
- A scatter-hash comparison whose `config_fnv`, level or mesh list differ FAILs with reason `config mismatch` (fix the run, never the bar).
- Every pair of runs compared (C1 vs C1S, S1X vs pkg/ship S1X) must have equal `sg.*`; otherwise the comparison FAILs `sg mismatch`.
- Governor read-back equals each requested value.
- Licence: every mesh path a run loaded maps through `report.json` to a manifest row with licence `CC0-1.0`, `project-original` or Epic
  engine content.
- **Dump oracle** (numpy, independent of the C++): for every record of a dump, with the run's saved `height.r32` and `splat.rgba8`: the
  surface z on the BL-TR triangulation and the class's z rule within 1 mm; `gxq, gyq` exactly (from the same HQ16 quantisation); the class's
  own splat and slope limits, read from the palette in `results.json`, with margins 2/255 and 0.5° (an instance inside the margin is exempt;
  one beyond it FAILs). Dumps diff by key for any SX4 or SX16 failure.
- The log scan adds any `LogMaterial` warning or error naming `/Game/Terrain/Scatter`, `LogChimeraTerrain: Error`, and
  `Virtual Shadow Map Page Pool overflow` (F20). `P4` (terrain GPU) never reads scatter phases.

### 3.9 Judging the look against Manor Lords (reported, D4)
- References read in place (git-ignored third-party pixels): RTS `ml_01`, `ml_08`, `press/press_03`, `news/news_080`, `news/news_095`;
  near-RTS `ml_04`, `news/news_016`, `news/news_017`; ground level `ml_07`, `ml_09` (only beside our `closeup`).
- Measures, `T/Tools/look_measure.py --scatter` after the ground-look commit (reusing G0's methods, no copies). Baselines (M1 hue/S/V, M2
  band energy) are re-taken from the ground-look **final** round, not from G1 round 1: M1 meadow hue/S/V with scatter on stays within ±3° /
  ±0.05 of the final ground's values; M2 meadow luma band energy at 8-64 px vs the ML meadow crops (ours 0.078 vs ML 0.12 at 8-16 px in
  `c-G1-look-r1-measure.md`, re-measured on the final); M3 canopy fraction of ground pixels (bounds-based, from `scatter_mask`) vs ML within
  ±30 %; M4 tree-shadow luma on meadow 0.27-0.33 × lit, olive; M5 canopy massing (connected components: share in masses vs groves vs singles);
  M6 path core vs verge contrast at least the scatter-off value; M7 map edge hidden (share of beyond-map pixels at `rts80` showing forest); M8
  no fade ring (luma row step across 90-130 m ≤ A/A floor + 1/255); M9 shimmer (30-frame temporal luma std from LOOKX's `movie 30` ≤ 1.5 ×
  terrain-only); M10 grass on rock (share of material-rock pixels at `oblique` covered by grass-layer instances).
- Verdict: an Opus xhigh art director scores `rts80`, `oblique` and `closeup` 0-10 with reasons, after the numbers and the references, on
  **paired scatter-on and scatter-off shots of the same build**, and reports the delta beside the absolute score, so scatter's contribution is
  separated from judge drift. The brief includes the L1 leaf scale (trees rescaled about ×4, §3.6). Baseline 5/10 (ground only). Target ≥ 7.5 at
  `rts80` and `oblique`. At most 3 rounds; a round changes only palette constants, the asset level per slot, material scalars, or the
  `SunContactShadowM` option. Each round reruns S1X's SX1-SX5, THINX's SX6 and one C1S smoke. **The main session sends each round's composite
  to Alec at once** (SendUserFile, render). Composites with Manor Lords pixels stay in `T/Out`; `EV/c` gets only our shots and the measure tables.

### 3.10 How C9, C11, C12 and C13 change
- **C9** (unit layer): units subscribe to `FOnTerrainChanged` and ride `HF.SampleSurface` (the drawn triangulated surface), not a new bilinear
  sampler (r8d §1.7). C9 needs only **scatter S4a** (the event), never the scatter renderer. `C1U` stays scatter-off; `C1US` is added once C9 lands.
- **C11** (packaging) **never waits for scatter.** When C11's own deps are done: if scatter S5 and S6 have passed, the cook carries scatter and
  S7 runs inside C11's window; otherwise C11 cooks what the tree holds with scatter not yet passed (`Content/Terrain/Scatter` absent or
  untested) and S7 packages again later (+about 1 h of lock). C11's own runs are scripted, so scatter is off in them by default (§3.8). If a
  scatter-carrying cook or packaged run fails for a scatter cause, C11 deletes `Content/Terrain/Scatter` (reproducible by S3), re-cooks, runs
  its gated bars, and SX16 is recorded FAIL with the cause. `DefaultGame.ini` needs no change.
- **C12** (Phase 4): add C1S ×3 **interleaved** with C1's reps (C1, C1S, C1, C1S, C1, C1S), C1US ×3, SOAKS ×1, C1S_ATTR ×1, and one rep each of
  the A/Bs (grass `_L`, `ScatterApply=clear`, `ScatterCasterDuringStroke=150`), about +70-85 min. D2 is unchanged: C1 and C1U (scatter off)
  remain the gated runs; C1US is printed against all five P3 conditions as REPORT.
- **C13** (proof): `EV/c` gains `c-S9-scatter-composite.jpg` (ours only), the SX rows in `summary.txt`, the manifest and lock copies, and a README
  section; `--check-proof` covers SX1-SX11, SX16 and SX18.
- Plan C §7 drops "Foliage and grass scatter" from out of scope and §3 gains a pointer here (S0). The EXECUTION slot is Phase 3: ground-look →
  scatter S1-S3 (S1 now, beside it) → S4a → C9 beside scatter S4-S6 → C11 (+S7 if scatter passed) → Phase 4 C12 (+S8) → C13 (+S9).

## 4. Tasks (ordered)
Rules for every implementer: cite the headers you rely on and never guess an API name; code is written without the lock; every build,
commandlet, test run, `-game` run or package step takes LOCK, one command per hold; steps marked BG run in the background with a foreground
`wait_for.sh`; **no file under `T/Source` is created or edited before the ground-look workflow's final commit** (F17); Blender and fetches are
not Unreal processes but never run inside a Phase 4 measure window; every task ends with `bash T/Tools/sync_to_repo.sh`. Only the main
session commits (`Terrain:` prefix, by pathspec) and pushes R, at checkpoints S1, S2, S4a, S4, S5, S6, S7 (with C11) and S9 (with C13), each
with a HANDOFF rewrite.

**S0 Plan amendments and ignore lines** · main session · no lock · deps: none
Amend plan C §7 and add the §3 pointer; add the EXECUTION §8 line and the Phase 3 slot (§3.10, including S4a and C11's no-wait rule); add
`ChimeraTerrain/ScatterSrc/` to `U/.gitignore`; add `blender` to `KIT/preflight.ps1`'s busy pattern (F15). Accept: `git -C D:/Projects/Chimera-Unreal
check-ignore -v ChimeraTerrain/ScatterSrc/x` prints the rule; `python -m pytest D:/Projects/Project_Chimera/tools/unreal-trial/tests -q` passes.

**S1 Meshes and assets outside Unreal** · opus (medium; mesh quality is judged) · no lock · deps: S0 · may start now
`T/Tools/make_scatter_meshes.py`, `T/Scripts/scatter/meshes.json`, `T/Tools/fetch_scatter_assets.py`, `T/Tools/blender/prep_polyhaven.py`,
`T/Tools/blender/make_bush.py`, `T/Tools/preview_scatter.py`, `T/Tools/test_scatter_tools.py` (§3.6; budgets: grass ≤ 48, tussock ≤ 200,
flower ≤ 64, fern ≤ 300, L0 shrub 600-1,500, L0 tree ≤ 4,000, L1 tree ≤ 30,000, L1 bush ≤ 4,000, near card 310, rocks as shipped; pivots at
the base, +Z up, metres; trees at their slot's nominal height; normal maps left in GL convention).
Accept: `python T/Tools/make_scatter_meshes.py` → `MESHES_OK species=<n> files=<m> tris_ok`; a second run is byte-identical (pytest);
`python T/Tools/fetch_scatter_assets.py` → `SCATTER_FETCH OK ids=<n> md5=verified licence=CC0`; Blender prep and bush →
`SCATTER_PREP OK meshes=<n>` with identical vertex hashes on a second run and each tree's target height and scale factor in `report.json`;
`python T/Tools/fetch_scatter_assets.py --check` → `MANIFEST OK rows=<n> licences=CC0-1.0|project-original|epic`; `python -m pytest
T/Tools/test_scatter_tools.py -q` passes (includes the `Mix32` golden shared with C++); the contact sheet goes to the main session, which sends
it to Alec.

**S2 Generator and pure tests** · sonnet (high) · LOCK for build and tests · deps: S0, the ground-look workflow's final commit
`S/Data/TerrainScatter{Types,Math,Palette}`, `TerrainScatter.{h,cpp}` (including `FScatterScheduler` and `BuildInstance`),
`T/Tools/gen_look_shared.py` → `TerrainLookShared.h`, `S/Tests/TerrainScatterTests.cpp`, `T/Tools/test_scatter_constants.py`. Re-read
`make_ground_material.py` after the ground-look final commit before writing any threshold. Tests (≥ 30):
same input twice → same records; `Mix32`/`Key`/`ValueNoiseQ16` goldens; `SurfaceQ16` vs `SampleSurface` ≤ 2⁻¹⁵ m on 10k seeded points of a
sculpted field, edges included (observed max logged); positions at E = 512 with c = 4 m (no overflow, inside the cell); `SplatQ8Bilinear` exact
at texel centres and inside the 4 neighbours between them; fine tile 16/32/64 and coarse 40/80/160 give the same `scatter_fnv`; `ChunkQuads`
32 vs 64 the same; E = 160 vs 320 identical records over the overlap (cells within 5 m of the smaller edge excluded); **incremental equals
full** over 60 seeded strokes of all modes and layers, d 5-100 (per-tick marking, the edit scripts applied to `FFakeIsm`; the live fold equals
the reference after every stroke); **tilt-only sculpt** (a smooth that changes the cell gradient but not `zq` at an instance changes every
aligned record in the tilted cells); edit-script property test (random old/new record sets: `FFakeIsm` ends with exactly the new set, removals
strictly descending); **scheduler with a fake executor and clock**: tasks complete in a seeded random order with random delays, a 1-unit-per-frame
budget forces partial applies, new dirt arrives while tiles are Busy, the epoch bumps mid-flight, and after quiescence `FFakeIsm` equals the
reference after every step, with no tile dispatched while Busy; record hash with the record's memory filled with 0xCD, then 0x00, before the
fields are assigned → same hash; undo then redo restores both earlier reference hashes; locality (records outside the dirty tiles
byte-identical); monotone thinning over 1,000 random paints (classes other than rock and shrub never gain; positions never move); hedge
positive test (a painted dirt line raises shrubs in its 1.5-3.5 m verge band where `Fh ≥ 0.7`, and leaves 0 in its core); exclusions (no
grass-class instance at `wG < 96`, on G2 > tan²35° or at `wS ≥ 64`; no trunk at `wD > 32`; no tree on G2 > tan²30°); every z equals the
class's rule on `SurfaceQ16` exactly; snapshot generation equals generation from the live HF; the HF FNV is unchanged by generation; field
coverage inside the §3.4 ranges; options parsing (script → off unless explicit; clamps); `config_fnv` moves for every folded option and for no
unfolded one; include and forbidden-API text scans; `TerrainLookShared.h` regenerated from `SCALARS` equals the checked-in file.
**`Chimera.Terrain.Scatter.EngineIsm`** (`-nullrhi`): an unregistered `UInstancedStaticMeshComponent` with `SetRemoveSwap()` and 4 custom floats
takes 1,000 random edit scripts beside `FFakeIsm`; `PerInstanceSMData` and the custom data match index by index after each (UNVERIFIED that an
unregistered component accepts the calls; if not, register it in an `FTestWorld`, which under `-nullrhi` creates no proxy).
Accept: BG `LOCK PS T/Tools/build.ps1 -Target Editor` → `Result: Succeeded`; `LOCK PS T/Tools/run_tests.ps1 -Filter Chimera.Terrain.Scatter`
→ `TESTS pass>=30 fail=0`; `LOCK PS T/Tools/run_tests.ps1 -Filter Chimera.Terrain` → `fail=0`; `python -m pytest T/Tools/test_scatter_constants.py -q`
passes; the test log prints woodland, drift and border fractions, per-class map totals and a field map for THINX's coordinates.

**S3 Import and materials** · sonnet (high) · LOCK, BG · deps: S1, S2's Editor build
`T/Scripts/make_scatter_assets.py`, `T/Scripts/scatter/ChimeraScatter.hlsl` (§3.6, §3.7; the shared patch include waits for S6).
Accept: BG `LOCK PS T/Tools/run_commandlet.ps1 -Script T/Scripts/make_scatter_assets.py -Tag s3_assets -Sentinel SCATTER_OK -TimeoutMin 60`
exits 0 with `errors=0 usage_ok=<k>/<k> deps_ok=1 flip_ok=1 vc_ok=1`; `T/Out/scatter_assets/report.json`: imported triangles = the generator's
or Blender's report ± 1 % (source triangles for Nanite rows; settles r8b question 7), Nanite flags per §3.7, distance fields off, textures
`NeverStream`, per-instance random false on every material, every path under `/Game/Terrain/Scatter`, material compile errors 0; the HLSL text
tests pass; `T/Config/DefaultEngine.ini` unchanged.

**S4a Terrain change event** · sonnet (medium) · LOCK for builds and runs · deps: the ground-look final commit
`Game/TerrainActor.{h,cpp}`: `FOnTerrainChanged` with the five broadcast sites of §3.5, `AddPendingWorkProvider`, `GetBrushRingCenter()`,
`terrain_events` in `results.json` (director). Nothing else; scatter and C9 both build on it.
Accept: BG builds Editor and Game → `Result: Succeeded`; `run_tests.ps1 -Filter Chimera.Terrain` → `fail=0`; `-Script S1 -Tag s1_s4a` then
`parse_terrain.py --equal-hashes T/Out/s1_a T/Out/s1_s4a` → PASS and `--s1 T/Out/s1_s4a` → PASS; its `terrain_events` equal Init 1, Tick = the
sum of stroke ticks, StrokeEnd = the stroke count, Undo 2, Redo 2, Load 0 (a parser test reads them).

**S4 Runtime scatter** · opus (medium; concurrency, render state) · LOCK for builds and runs · deps: S2, S3 (L0 is enough), S4a
`Game/TerrainScatter`, `Render/TerrainScatterRenderer`, the four GameMode lines, the scheduler and apply of §3.5, the settings of §3.7, options,
the HUD line, ops `scatter`, `scatter_wait`, `scatter_visible`, `scatter_verify`, `scatter_fresh` and the hash fields, `T/Scripts/SXSMOKE.json`,
`T/Scripts/FAILX.json`, plus a latent test `Chimera.Terrain.Scatter.Render` (FTestWorld, `CanEverRender` false: no components, counts kept).
Accept:
1. BG `LOCK PS T/Tools/build.ps1 -Target Editor` and BG `LOCK PS T/Tools/build.ps1 -Target Game` → both `Result: Succeeded`.
2. `LOCK PS T/Tools/run_tests.ps1 -Filter Chimera.Terrain` → `fail=0`.
3. Dormant changes nothing: `-Script S1 -Tag s1_sx4`, `--equal-hashes T/Out/s1_a T/Out/s1_sx4` → PASS and `--s1 T/Out/s1_sx4` → PASS;
   `--c7 T/Out/s1_sx4` → PASS; `-Script S1L -Tag s1l_sx4 -Extra "-ChimeraTerrainLoad=<abs T/Out/s1_a>"` and `--s1l T/Out/s1l_sx4 --ref
   T/Out/s1_a --img-ref T/Out/s1_s4a` → PASS (image reference ruling, EXECUTION §8, 2026-10-02); `-Script G1 -Tag
   g1_sx4` and `--g1` → `G1 PASS`; `-Script MOUSE -Tag mouse_sx4` (MOUSE's own flags) → P10 PASS, or, if preflight finds the desktop locked, the
   row reads "deferred to C11 pkg_mouse" (recorded, not waived).
4. BG warm-up `LOCK PS T/Tools/run_terrain.ps1 -Script SXSMOKE -Tag sx_warm -TimeoutMin 60 -Extra "-ChimeraTerrainScatter=1"`, then
   `-Script SXSMOKE -Tag sx_smoke -Extra "-ChimeraTerrainScatter=1"` exits 0 and `python T/Tools/parse_terrain.py --scatter T/Out/sx_smoke` →
   `SCATTER PASS` (live = reference, verify, counters balanced, `proxy_recreates=0`, the hidden shot taken, log scan clean); `-Script FAILX
   -Tag failx -Extra "-ChimeraTerrainScatter=1"` exits 2 with `results.json` written and no `Saved/Crashes`.
5. `apply_unit_ms` against instance and change counts is recorded and the prediction constants are fitted; if any unit exceeds 0.8 ms, the T0
   rank split of §3.5 is taken and the result is recorded; if `scatter_flush_ms` per unit is large, `SetUseConservativeBounds(true)` is tried as
   a recorded A/B (tiles are fixed boxes).

**S5 Harness and determinism runs** · sonnet (high) · LOCK for runs · deps: S4
The remaining ops (`scatter_dump`, `scatter_check`, `scatter_counts`, `scatter_view_counts`, `scatter_target`, `scatter_mask`), the results block,
the parser of §3.8 (including `--equal-hashes` and the dump oracle) and its tests, scripts S1X, S1XL, SHADX, THINX, LATX, MOUSEX, C1S, C1S_ATTR,
C1US, SOAKS, LOOKX, VIDEOX.
Accept:
1. `python -m pytest T/Tools/test_parse_terrain.py -q` passes (including a poisoned `config_fnv` → `config mismatch`, an `sg.*` mismatch, a
   0/0 `scatter_check` row → "no samples", and the dump oracle on a hand-made dump with one planted violation).
2. `LOCK PS T/Tools/run_terrain.ps1 -Script S1X -Tag s1x_a -Extra "-ChimeraTerrainScatter=1"`;
   `-Script S1X -Tag s1x_b -MaxFps 20 -Extra "-ChimeraTerrainScatter=1 -ChimeraTerrainHitchMs=300 -ChimeraTerrainScatterThreads=0"`;
   `-Script S1X -Tag s1x_c -Extra "-ChimeraTerrainScatter=1 -ChimeraTerrainScatterDuringStroke=0"`;
   `-Script S1XL -Tag s1xl -Extra "-ChimeraTerrainScatter=1 -ChimeraTerrainLoad=<abs T/Out/s1x_a>"`: all exit 0.
3. `python T/Tools/parse_terrain.py --s1x T/Out/s1x_a --pair T/Out/s1x_b T/Out/s1x_c --reload T/Out/s1xl --ref T/Out/s1_a` → SX1-SX5, SX7-SX9
   and SX18 PASS, SX17 reported; `--same-hash T/Out/s1x_a T/Out/s1x_b` → PASS (s1x_b really ran capped and hitched).
4. `-Script THINX -Tag thinx` and `--thinx T/Out/thinx` → SX6 PASS.
5. `-Script SHADX -Tag shadx` and `--shadx T/Out/shadx` → SX10 PASS (on FAIL: §6 risk 3).
6. `-Script MOUSEX -Tag mousex` (MOUSE's flags) → SX1, SX5, SX8 PASS, P10 PASS (same desktop rule as S4.3); `-Script LATX -Tag latx` →
   `--latx` prints SX12's rows with n.
7. One unmeasured `-Script C1S -Tag c1s_smoke` exits 0 and `--summary T/Out/c1s_smoke` prints the SCATTER table with `scatter_gpu_ms`,
   `stroke_gpu_ms` and `scatter_flush_ms` rows.

**S6 Look rounds against Manor Lords** · opus (medium) implementer + opus (xhigh) art director · LOCK for runs · deps: S5, ground-look final
First the shared `ChimeraPatch.hlsl` include: `make_ground_material.py` and `make_scatter_assets.py` both inline it; accept only if
`M_ChimeraGround`'s expanded Custom-node code string is byte-identical before and after; then rerun the C7 commandlet and `--c7` on one S1 run.
If the string had to change, the ground is re-judged with the ground-look art director, G1 reruns, and P4 is re-measured in C12 (0.054 ms of
margin, F13). Then the S3 re-run **from clean** (delete `Content/Terrain/Scatter`, rerun, `report.json` triangle counts, flags and
dependencies equal the S3 original); the L1 swap per slot (`-ChimeraTerrainScatterLevel=L1`, then per-slot palette rows); palette tuning via
`-ChimeraTerrainScatterParams` and then baked into the table; the tree and bush bake-off (L0 vs L1 at `oblique` and `rts80`, with the trees
layer GPU row from a C1S smoke; masked leaves at full raster, F19); optionally `SunContactShadowM` (the TerrainLighting edit of §3.1) with an
on/off pair; `look_measure.py --scatter`. At most 3 rounds.
Accept per round: `LOCK PS T/Tools/run_terrain.ps1 -Script LOOKX -Tag lookx_r<n> -Extra "-ChimeraTerrainScatter=1 <round options>"` exits 0;
`python T/Tools/look_measure.py --scatter T/Out/lookx_r<n>` writes M1-M10; the art director's JSON (paired scores, deltas, gaps, next changes)
goes to `EV/c`; the composite reaches Alec at once; S1X and THINX reruns with the round's options pass SX1-SX6. Stop at ≥ 7.5 at `rts80` and
`oblique`, or after round 3 with the gaps reported. The chosen defaults become the palette table and `ScatterLevel`'s default.

**S7 Packaged runs (inside C11, or after it)** · sonnet (high) · LOCK, BG · deps: S5, S6, a package cooked after S6 (§3.10)
First `-Script S1X -Tag s1x_final -Extra "-ChimeraTerrainScatter=1"` in `-game` from the package's commit (the shipped defaults; the reference).
Then: `-Script S1X -Tag pkg_s1x -Packaged -Extra "-ChimeraTerrainScatter=1"`;
`-Script S1XL -Tag pkg_s1xl -Packaged -Extra "-ChimeraTerrainScatter=1 -ChimeraTerrainLoad=<abs T/Out/s1x_final>"`;
`-Script S1X -Tag ship_s1x -Packaged -Shipping -Extra "-ChimeraTerrainScatter=1"`;
`-Script S1XL -Tag ship_s1xl -Packaged -Shipping -Extra "-ChimeraTerrainScatter=1 -ChimeraTerrainLoad=<abs T/Out/s1x_final>"`;
`-Script MOUSEX -Tag pkg_mousex -Packaged` (MOUSE's flags): all exit 0.
Accept: `python T/Tools/parse_terrain.py --s1x T/Out/pkg_s1x --reload T/Out/pkg_s1xl --ref T/Out/s1_a --xref T/Out/s1x_final --packaged` and
`--s1x T/Out/ship_s1x --reload T/Out/ship_s1xl --ref T/Out/s1_a --xref T/Out/s1x_final --packaged --shipping` → SX16 PASS; `--scatter
T/Out/pkg_mousex` → SX1, SX5, SX8 PASS.

**S8 Measurement (inside C12, Phase 4)** · sonnet (high) · LOCK `--measure`, BG · deps: S5, C9 for C1US
Warm-up, then `-Measure` runs: C1S ×3 interleaved with C1's three reps (`c1_r<n>`, `c1s_r<n>` alternating), C1US ×3, SOAKS ×1, C1S_ATTR ×1,
LATX ×1, and one rep each of `-ChimeraTerrainScatterGrassNanite=0`, `-ChimeraTerrainScatterApply=clear`,
`-ChimeraTerrainScatterCasterDuringStroke=150`; governor steps only if SX13 or SX14 fail.
Accept: `python T/Tools/parse_terrain.py --summary T/Out/c1_* T/Out/c1u_* T/Out/c1s_* T/Out/c1us_* T/Out/soak* T/Out/latx* > EV/c/c-S8-summary.txt`
lists P1-P6 unchanged, SX11 gated, SX12-SX15 and the A/B rows with numbers, the kill-rule verdict and the governor step chosen; rep spread > 5 %
flagged; `sg.*` equal across every compared pair.

**S9 Review and proof (inside C13)** · opus (xhigh, reviewer: determinism, presentation-only, budgets, text first) · no lock · deps: S7, S8
`EV/c` additions per §3.10. Accept: `python T/Tools/parse_terrain.py --check-proof EV/c` → `PROOF OK ... gates_failed=0`, or the explicit
failure list; the main session sends the composite, `sculpt_scatter.gif` and the numbers to Alec.

**Lock estimate (EST):** S2 30 min, S3 45 BG + 60 warm-up BG, S4a 25, S4 80, S5 110, S6 3 × 35 + 20 for the patch check, S7 +75 inside or
after C11, S8 +85 inside C12. About 10 h, inside Phases 3-4. Builds run 4-18 min under contention (HANDOFF).

## 5. Proof of done (bars and their reasons)
| # | What | Bar (reason) | Gate | Source |
|---|---|---|---|---|
| SX1 | identity | at every `scatter_verify`: `scatter_live_fnv == scatter_fnv`; readback equals `BuildInstance` of the unit states; counts equal; z and up axis match HF (edits are followed exactly). Positive control at S1X `before`: every enabled class's count ≥ half its §3.4 flat-map EST (floors in the parser), so the identity bars cannot pass with an empty class | gate | S1X, S1XL, SHADX, THINX, MOUSEX |
| SX2 | undo / redo | `scatter_live_fnv` at `undo` = `scatter_fnv` at `pre_last2`, at `redo` = at `after` (scatter is a function of the terrain, not of history) | gate | S1X |
| SX3 | reload | S1XL `loaded`: live and reference = the loaded run's final; `discarded_epoch ≥ 1` (the load really cut a fill) (constraint 3, S1L) | gate | S1XL |
| SX4 | frame rate, threads, mode | SX1 holds at every verify in s1x_a, s1x_b, s1x_c; `scatter_live_fnv` equal at every shared name. Path proofs: s1x_a `mid_stroke_dispatches > 0`, `stale_result_redispatches > 0`, `in_flight_max = 2`; s1x_b `gt_generations > 0`, `in_flight_max = 0`, and `--same-hash s1x_a s1x_b` PASS (capped, hitched); s1x_c `mid_stroke_dispatches = 0` (no residue from timing, and each timing path really ran) | gate | three S1X runs |
| SX5 | on the edited surface | dump oracle on S1X `redo` and THINX `end`: every record's z within 1 mm of its class rule on the saved heights, `gxq, gyq` exact; verify's z and up-axis rows (SX1); ≥ 1,000 grass and ≥ 20 coarse instances moved in z by > 5 cm between S1X's `before` and `redo` dumps (by key), so moved instances are what is tested | gate | S1X, THINX, SHADX, MOUSEX |
| SX6 | thinning | THINX cores: grass, tussock, flower ratio to pure grass ≤ 0.10 on `dirt`, `rock`, `snow`; trees 0 on `path`. Each row needs a pure-grass core yield of at least grass 200, tussock 10, flower 20, tree 5, else it FAILs "no samples" (fix the coordinates, never the bar). Positive control: ≥ 5 rocks in the `rock` disc's edge band. Dump oracle: every instance on S1X and THINX obeys its own class's splat and slope limits from the palette (margins 2/255, 0.5°) | gate | THINX, S1X dumps |
| SX7 | presentation only | S1X height and splat FNV = s1_a at every shared name (`--equal-hashes`); saved `terrain.json`/`height.r32`/`splat.rgba8` sha256 = s1_a's; P5 and P11 pass on S1X; dormant: s1_sx4 `--equal-hashes` and `--s1` PASS, `--c7` PASS, s1l_sx4 `--s1l` PASS, g1_sx4 G1 PASS, mouse_sx4 P10 PASS (or "deferred to C11 pkg_mouse" if the desktop was locked) | gate | S1X, S4's dormant runs |
| SX8 | accounting | `dispatched == applied + skipped_identical + discarded_epoch + cancelled`; 0 busy and 0 in flight at settle; `proxy_recreates = 0` (a proxy rebuilt for no reason of scatter's own; every apply-dirtied recreate counts), `proxy_compile_recreates_during_edits = 0` and `proxy_engine_recreates_during_edits = 0` (§3.5 as amended 2026-10-02); `proxy_first_creates`, `proxy_expected_rebuilds`, refills, PSO and editor-compile recreates reported | gate | every scatter run |
| SX9 | logs and assets | log scan clean, including `LogMaterial` lines on scatter materials and VSM page-pool overflow; `usage_ok`, `deps_ok`, `flip_ok`, `vc_ok` in the asset report; per-instance random false; licence bar (packaged-only failure class, F7) | gate | every scatter run, S3 |
| SX10 | shadows follow edits | precondition: stored key `t0` exists after the raise with z up by > 2 m; `t_b` vs `t_a` gives the A/A floor; `t_far`, `t_raised`, `t_undo` each vs its `_fresh` twin pass the local statistic inside the union of t0's tree and shadow masks at `t_a` and `t_raised` (no stale pages anywhere near the edit); `t_raised` vs `t_a` changed fraction ≥ 0.10 inside that union (the scene really changed); VSM pages per edit reported **Amended 2026-10-03:** pairs and floor shot frozen (`temporal_freeze`), within one freeze window; a floor above P7's bar is "floor unusable" (EXECUTION §8 S5 record). | gate | SHADX |
| SX11 | no hitch (Phase 4) | C1S `walk` plus every flush window, fill excluded: `scatter_gt_ms` p99 ≤ 1.5, p99.9 ≤ 2.0, ≤ 0.1 % of frames > 2.0 (each listed); walk GT p99 and RT p99 each − C1's ≤ 1.5 ms (interleaved reps; catches the end-of-frame flush and GPU-scene upload, which `scatter_gt_ms` cannot see); frames > 33.3 ms not above C1's + 0.1 pp (a single max is noise on this shared desktop); `scatter_flush_ms` printed beside. `ScatterThreads=0` runs never count | gate | S8 |
| SX12 | latency | LATX: per stroke, end → every tile that stroke dirtied (a snapshot of its tile set) current and flushed: ≤ 0.3 s at d ≤ 20, ≤ 1.0 s at d100, p95 when n ≥ 20, else max, n printed; undo/redo from S1X ≤ 1.2 s (max, n = 2); `init_ms` | report | S5, S8 |
| SX13 | GPU | `scatter_gpu_ms` at `rts80` (on − off GPU p50, P4's method): target 1.8, kill above 2.5 after the ladder; per layer; `oblique` and `closeup` pairs; `stroke_gpu_ms` = (C1S walk − idle_scatter_on) − (C1 walk − idle_visible), p50/p95/p99 (caster shadows re-rendered after strokes, F20); per-pass and VSM pages from C1S_ATTR | report, kill rule | S8 |
| SX14 | D2 with scatter | C1US: all five P3 conditions: median ≥ 60, 1%-low ≥ 45, ≤ 0.5 % frames > 33.3 ms, none > 100 ms, sculpt − idle ≤ 2 ms (as D2 reports the trees-and-scatter scene) | report, kill rule | S8 |
| SX15 | caps and memory | analytic drawn ≤ 24k, analytic LOD-weighted triangles ≤ 1.5 M, primitives ≤ 700 (744 with the T0 split) at `rts80`; shadow Nanite triangles from the VSM CSV category; primary-view Nanite triangles UNVERIFIED (`NaniteStats` draws on screen only); SOAKS peak growth against P6's 300 MB | report | S5, S8 |
| SX16 | packaged | Development and Shipping: (a) pkg_s1xl and ship_s1xl load s1x_final's editor-saved files, and `scatter_fnv` and `scatter_live_fnv` at `loaded` equal s1x_final's final, unconditionally (heights identical by construction); (b) pkg_s1x and ship_s1x: if `height_fnv` equals s1x_final's then `scatter_fnv` equals too (otherwise reported with `max_abs_dh`, per C11); (c) SX1 and SX5 at every verify; (d) render proof: `x_rts80_full` on vs off changed fraction in the scatter mask ≥ 0.3 × s1x_final's own, and in the vegetation mask mean saturation ≥ 0.5 × s1x_final's and mean hue within ±8° (the default material is grey); (e) Development also meets SX8, SX9 and SX18; pkg_mousex meets SX1, SX5, SX8 | gate | S7 |
| SX17 | image | S1X A/A floor; `undo` vs `pre_last2` and `redo` vs `after` under P7's local statistic against S1X's own floor; ghost check s1x_a `x_redo` vs s1xl `x_redo` (a floor that fails goes to Alec as is, never loosened) **Amended 2026-10-03:** also a frozen single-window variant, reported (EXECUTION §8 S5 record). | report | S5 |
| SX18 | GPU draws the CPU state | S1X `after`, `undo`, `redo`, S1XL `loaded` and pkg_s1x: `x_<n>` vs `x_<n>_fresh` passes P7's local statistic inside the scatter mask against the run's A/A floor (partial instance updates and the VSM cache draw what a fresh rebuild draws; risks 3 and 4) **Amended 2026-10-03:** pairs `xf_<n>` vs `xf_<n>_fresh`, shot frozen in one window, held to P7's bar itself (EXECUTION §8 S5 record). | gate | S1X, S1XL, S7 |
| L | look | M1-M10 and the art director's paired scores per round | report (D4) | S6 |

**Scatter Done** = SX1-SX11, SX16 and SX18 PASS; SX12-SX15, SX17 and L reported and sent to Alec; the kill rule applied and recorded.

## 6. Risks and fallbacks
1. **Game-thread cost of instance edits is unmeasured.** Levers in order: apply budget 1.0 → 0.7 ms; `ScatterDuringStroke` 150 → 300 → 0;
   the grass T0 rank split (744 primitives); `SetUseConservativeBounds(true)` per unit (drops the per-flush `CalcBounds`); `ScatterThreads`
   2 → 1; grass density. The keyed script already skips unchanged instances. The decision rests on S4's `apply_unit_ms` against instance
   counts, not on one run.
2. **GPU with 1,000 units** (8.6 ms fixed world, unit cost unknown, EST 1.4 ms): the governor, then the kill rule; the lighting question goes to
   Alec only with `ProfileGPU` numbers.
3. **VSM on edited units.** Whole-unit invalidation and 100 frames in the dynamic layer per edit (F20) is the expected cost, not a defect.
   Cost levers: caster units at stroke end (the default); coarse 40 m tiles (1,076 primitives, reported); governor step 5. Stale pages would be
   a defect: SX10 and SX18 compare every edit against a forced fresh render. Fallback for staleness: `ScatterApply=clear`.
4. **Partial instance updates misbehave on Nanite ISMs** (UNVERIFIED in a run; the engine path is generic, F1): SX18's fresh-rebuild compare
   and SX1's readback catch it; fallback `ScatterApply=clear`.
5. **Packaged-only failures**: missing material usages (F7), Interchange materials outside `/Game/Terrain`, Shipping console and log paths.
   Asserted usages and dependencies, the always-cooked folder, metrics in `results.json`, cvar read-back, SX9, SX16's render proof, and C11's
   delete-and-re-cook fallback (§3.10).
6. **Scanned trees look poor after decimation or rescaling, or masked leaves cost too much** (full masked raster at every distance, F19).
   L0 procedural trees ship instead; the art director picks per slot with the trees layer GPU row beside it. The PVE sample trees
   (experimental skeletal Nanite assemblies) stay post-trial.
7. **The bush gap** (no CC0 temperate bush): L0 blob bushes, the L1 composite, scale and cluster tuning.
8. **Nanite grass pops at the end distance or flickers under TSR**: rank tiers; the `_L` A/B with the ISM fade; `r.TSR.ThinGeometryDetection=1`
   only if the video shows flicker; bigger, fewer clumps.
9. **Thin geometry raises S1X's A/A image floor**: S1 stays scatter-off; S1X reports against its own floor; SHADX hides thin layers; hashes,
   dumps and contact checks carry the gates.
10. **Hash divergence between editor and game builds**: integer decisions everywhere, field-wise record hashes, thresholds as generated
    integer literals; SX16 (a) compares the load path unconditionally; dumps diff by key.
11. **Shared source tree with the ground-look workflow** (F17): no `T/Source` edits before its final commit; the colour include after it,
    gated by the byte-identical code string.
12. **Task-graph pressure on the 6-core 5600**: 2 workers at `BackgroundNormal`; RT gated in SX11; `ScatterThreads=0` exists for determinism.
13. **Field thresholds give poor forest fractions at the default seed**: S2 prints them; S6 tunes; every change moves `config_fnv` deliberately.
14. **Poly Haven unreachable**: L0 covers every slot, flagged `fallback:true` in the manifest.
15. **C9 late**: SX14 reads "no samples" until C1US can run; the kill rule then waits for it.
16. **The ground material keeps moving after S2** (F27): S2 waits for the final commit; the generated `TerrainLookShared.h` and its text test
    fail on any later drift.
17. **`FFakeIsm` differs from the engine's index order**: `Chimera.Terrain.Scatter.EngineIsm` (S2) and SX1's readback.

## 7. Out of scope
A PCG backend (the records-to-ISM seam keeps it possible after the trial, for creator-authored scatter graphs); GPU PCG; Nanite Foliage, PVE
and DynamicWind; wind or any WPO; impostors and HLOD; streaming or HiGen for maps beyond ±320 m; creator-painted woodland, clearings and
building-pad exclusion (they become extra acceptance factors in the generator: the layer palette DW-1019 and the building-foundations brief);
trees as sim resources with collision or pathing (scenario content, never scatter); a runtime adaptive governor, dynamic resolution and the
Nanite raster time budgets (F21); `PerInstanceRandom` in scatter materials; JSON species content (at the merge into ProjectChimera, per its
data-driven rule); multiplayer sync.

## 8. Decisions for Alec
None are needed to start, and the 2026-10-02 revision added none. For information:
1. **Your "built-ins first" rule** was applied as §1.2 says: the scatter runs entirely on Unreal's own instancing, Nanite, VSM and task system;
   PCG was checked in the source and not used, because its useful runtime part (the spawner) recreates every instance group on each edit and
   runs on the main thread. Say if you want PCG spiked anyway (about one day of lock time, behind the same seam).
2. **After S8, with numbers:** keep the trees-and-scatter scene (C1US) reported, as D2 says (recommended; the kill rule decides whether scatter is
   on by default), or gate it.
3. **Only if the governor ladder fails:** whether to trim part of the fixed-world lighting recipe (SSGI, clouds, sun), with `ProfileGPU` numbers.
4. **Visible in the video, decided here:** while a sculpt stroke is held, trees, shrubs and rocks keep their old height until the stroke ends
   (grass follows live). That keeps their shadows cheap (F20); the mid-stroke A/B is measured in C12 and can become the default if it is cheap.

## Review log (2026-10-02 critiques; A = accepted, A* = accepted with a different or partial fix, R = rejected)
api = the engine/API critique, proof = the bars critique. Every engine claim marked verified was re-read in the 5.8.3 source today.
- api-1 blocker, hidden components never get a proxy, so `HasPendingWork` hangs every shot after `scatter_visible 0`: A. Verified F18. Hiding is by `SetCullDistances(0, 1)` (in place, F6); the proxy term counts only components that should be in the scene; SXSMOKE shoots while hidden.
- api-2 `NanitePixelProgrammableDistance` 40 m draws L1 leaves as solid cards at `rts80`: A*. Verified F19. Set to 0 everywhere; masked cost is judged in S6's bake-off with the trees GPU row, L0 opaque canopies are the fallback, so no with/without LOOKX shot is needed.
- api-3 an edit invalidates the whole unit's shadow and keeps it dynamic for 100 frames: A*. Verified F20. F1, F4, §1.3 and risk 3 corrected; caster units apply at stroke end by default with the mid-stroke A/B in C12; `stroke_gpu_ms` and VSM pages reported; 40 m coarse tiles stay a measured lever (1,076 primitives), not the default.
- api-4 governor step 3 does nothing without dynamic resolution: A. Verified F21. Dropped; the ladder is fixed levers only, with `ResolutionLodBiasDirectional` +1 last; dynamic resolution added to §7.
- api-5 the sun has no contact-shadow length: A*. Verified F22 (the flag is `ContactShadowLengthInWS`). No contact shadows by default; `SunContactShadowM` is an S6 round option (one TerrainLighting edit, listed in §3.1), set only while scatter is on so scatter-off gates stay unchanged.
- api-6 SX8's counter fails on legitimate first fills: A*. Counters split into first creates, expected rebuilds and recreates; only recreates gate. `AllowCreateEmpty=1` rejected: a global cvar that would also change C9's unit ISM and add up to 644 empty primitives.
- api-7 the record has no terrain normal, so tilt and cd2/cd3 escape the hash and the edit script: A. `gxq, gyq` in the record; `BuildInstance(record)` is the only source of transforms and custom data; tilt-only S2 test; verify checks the up axis.
- api-8 hashing raw record bytes includes 3 padding bytes: A. Field-wise little-endian `HashRecord`; poisoned-memory test.
- api-9 a tile can be redispatched while part of its result is unapplied: A. Busy from dispatch until the last unit is applied; fake-executor scheduler test with partial applies.
- api-10 `SetVisibility`/`SetCastShadow` recreate proxies on every toggle: A. Cull-distance hiding; caster flags once at enable; `settle 150` after every toggle; governor step 5 never per pose.
- api-11 constants already stale (RockBand 0.10, PatchM 140): A. Verified F27. `TerrainLookShared.h` generated from `SCALARS` (23.2°/44.0° today); M keeps its own periods; the noise-broken rock edge is measured (M10), not claimed.
- api-12 normal maps flipped twice (Blender, then Interchange): A. Verified F23. Blender leaves `nor_gl`; the import asserts `flip_green_channel` per texture.
- api-13 masked opacity in a Custom node gets zero derivatives in the Nanite raster: A. Verified F24. Mask through a standard TextureSample; text test.
- api-14 `PerInstanceRandom` depends on index order and edit history: A. Verified F25. Banned; material statistic asserted in S3; text test.
- api-15 the proxy counter must be thread-safe: A. Atomics, non-null returns only, read after `FlushRenderingCommands`.
- api-16 `scatter_gt_ms` misses the end-of-frame flush: A. `SendRenderInstanceData_Concurrent` timed as `scatter_flush_ms`; frame GT and RT deltas gated in SX11; conservative bounds as a recorded lever, not a default.
- api-17 `ScatterThreads=0` cannot meet the budget: A. A determinism-only path, one tile per frame, never counted by SX11 or SX12.
- api-18 scatter spawns after the Init broadcast; undo/redo rects underspecified: A. Enable dirties everything itself; the event carries restored chunk ids mapped through `ChunkOwnedRect`/`ChunkOwnedSplatRect`.
- api-19 `deps_ok` and triangle checks would fail falsely on Interchange meshes: A. Game-only hard dependencies (`EDependencyQuery::Game`, verified at `AssetRegistryInterface.h:120`), `/Script/**` and `/BaseMaterial/**` allowed; Nanite rows compare source triangles.
- api-20 the mobility rationale is wrong for 5.8: A. F4 corrected; the Movable A/B is dropped from C12 and from risk 3; the option stays for diagnosis.
- api-21 shots may catch Nanite and texture streaming: A. `settle 150` after every `scatter_wait` before a shot; scatter textures `NeverStream`.
- api-22 int32 overflow, the ×4 tree rescale, vertex-colour gamma: A. int64 products with an E = 512 test; prep records target height and factor, the art director's brief checks leaf scale; S3 reads vertex colours back.
- proof-1 blocker, S4.3's `--same-hash` gates capped fps and a hitch: A. Verified F26. `--equal-hashes` for S4a, S4 and SX7; `--same-hash` stays for P8 and SX4's s1x_b.
- proof-2 blocker, SX8 contradicts F3 and the visibility toggles: A* (as api-6; toggles no longer create proxies at all).
- proof-3 SHADX's 42° cone can delete the tree, and the bar passes on terrain change alone: A*. Raise d60 s20 ×20 (+4 m, about 8°) with a key-survival precondition; fresh-cache twins decide; union mask; sun read from TerrainLighting; thin layers hidden; VSM pages reported. Took s20 rather than the suggested s5 so the tree moves 4 m, not 1 m.
- proof-4 no gated oracle for what the GPU draws: A. `scatter_fresh` and the new gated SX18.
- proof-5 SX6 is near-vacuous on S1's steep paint: A*. THINX on flat meadow with minimum denominators. The rock positive control measures the edge band, not the core: the rock rule is 0 at full rock paint by design.
- proof-6 the snow bar contradicts the conifer rule: A. Per-class limits read from the palette in `results.json`.
- proof-7 the monotone test contradicts the hedge term: A. Rock and shrub exempt; a positive verge test added.
- proof-8 SX16 skips the scatter check exactly when heights differ: A. Packaged S1XL runs load s1x_final's editor-saved files and are gated unconditionally.
- proof-9 Shipping has no render proof: A. On/off fraction and a saturation/hue band against s1x_final; SX1 and SX5 gated in Shipping.
- proof-10 per-instance outputs outside the record: A (as api-7).
- proof-11 the scheduler's concurrency has no deterministic test, and nothing proves the timing paths ran: A. Fake-executor test; path-proof counters gated in SX4.
- proof-12 SX11 p99 ≤ 1.5 collides with a 1.5 ms budget that always finishes a unit: A*. Predictive admission and a 1.0 ms default taken; the suggested 0.5 ms cap by splitting units is not a default, because unit cost is unmeasured: the T0 rank split is S4's measured lever. RT added to SX11.
- proof-13 the `ChimeraPatch.hlsl` refactor edits the ground with 0.054 ms of P4 margin: A*. Accepted only with a byte-identical expanded code string, then the C7 commandlet and `--c7`; if the string changes, G1, the ground judge and C12's P4 decide.
- proof-14 scatter can break (c)'s gated Done and sits on its critical path: A. S4a for C9; C11's no-wait rule and delete-and-re-cook fallback.
- proof-15 S7 compares against a pre-S6 reference and possibly a stale package: A. `s1x_final` from the package's commit; `config mismatch` FAILs.
- proof-16 stale ground constants: A (as api-11).
- proof-17 the hash bars pass with no scatter at all: A. Class floors at S1X `before`; moved-instance counts from dumps (SX5).
- proof-18 SX2 and SX4 compare reference hashes that follow from equal heights: A. Restated on the live fold, plus path proofs.
- proof-19 no per-instance dump, so no independent oracle: A. `scatter_dump` and the numpy dump oracle; SX5 and SX6 gate there.
- proof-20 the scalability group is neither pinned nor compared: A*. An `sg.*` equality bar on every compared pair; no pin, because a new `DefaultScalability.ini` would change C1's gate configuration mid-trial. Pin only if the bar ever fails.
- proof-21 the shipped default path never runs with scatter on: A. MOUSEX in `-game` and packaged.
- proof-22 SXSMOKE's single undo cannot return to the first hash: A. One undo per stroke, then `scatter_wait`, verify, hash.
- proof-23 `scatter_check` footprint sets mix strokes: A. Per-layer sets in S1X (reported); gated thinning is THINX's.
- proof-24 SX7's dormant C1 row can never fail: A. Dropped; C12's P4 measures the dormant build.
- proof-25 §0.3 claims more than S4 reruns: A. S4 adds S1L, `--c7` and MOUSE; §0.3 reworded.
- proof-26 SX15's triangle cap has no Nanite source: A*. Labelled analytic; shadow Nanite triangles from the VSM CSV category; primary-view Nanite triangles UNVERIFIED (the `NaniteStats` command draws on screen only); M3 called bounds-based.
- proof-27 SX12 latency is undefined under C1's back-to-back strokes: A. LATX with per-stroke tile snapshots; n printed, max when n < 20.
- proof-28 `-csvGpuStats` only on C1S biases comparisons: A. Removed from C1S; C1S_ATTR is a separate, never-pooled rep.
- proof-29 the 16 m lever quadruples primitives: A. Replaced by the T0 rank split (744).
- proof-30 the grass noise lives in `/BaseMaterial`: A. Allowed and recorded in the manifest as Epic engine-plugin content.
- proof-31 struct padding in the hash: A (as api-8).
- proof-32 two SurfaceQ16 bounds: A. 2⁻¹⁵ m in both; observed max logged.
- proof-33 `FFakeIsm` is never checked against the engine: A. `Chimera.Terrain.Scatter.EngineIsm` under `-nullrhi` (unregistered component; `FTestWorld` if it must register, UNVERIFIED).
- proof-34 `PerInstanceRandom`: A (as api-14).
- proof-35 SX14 drops P3's sculpt − idle bar: A. All five P3 conditions printed.
- proof-36 over-wide VSM invalidation and page-pool overflow are unmeasured: A. VSM pages in SHADX and C1S_ATTR; the overflow warning is scanned.
- proof-37 teardown edge cases are untested: A. FAILX; `scatter 0/1` while tiles are Busy in SXSMOKE; disable bumps the epoch.
- proof-38 option and hash semantics unstated: A. `config_fnv` fold set listed in §3.5; layers visibility-only; governor read-back bar.
- proof-39 absolute look scores cannot separate scatter from judge drift: A. Paired on/off scores with the delta; M1/M2 baselines from the ground-look final; `movie 30` for M9.
- proof-40 C1S walk starts after camera moves; reps not interleaved; layers unnamed: A. `settle 150` before the walk; C1/C1S interleaved; the layer map in §3.4.
- proof-41 licence and from-clean reproducibility are not tied to runs: A. Parser licence bar; S6's S3 re-run from a deleted Scatter folder with `report.json` equality.
