# R8a: Unreal PCG at runtime on a non-Landscape surface

Date 2026-10-01. Research only: no Unreal process was started, nothing was built or measured. Every claim is cited to the
5.8.3 engine source on this PC or to a URL. UNVERIFIED marks what only a build or a run settles.

Shorthand. `PCG` = `D:/Epic Games/UE_5.8/Engine/Plugins/PCG/Source/PCG` (paths below are relative to it: `Private/...`, `Public/...`).
`GSI` = `D:/Epic Games/UE_5.8/Engine/Plugins/PCGInterops/PCGGeometryScriptInterop/Source/PCGGeometryScriptInterop`.
`PL` = `D:/Epic Games/UE_5.8/Engine/Plugins`. `CT` = `D:/Projects/Chimera-Unreal/ChimeraTerrain/Source/ChimeraTerrain`.
`LT` = `D:/Projects/Chimera-Unreal/ProjectChimera/LookTest`. Docs: `D1` = https://dev.epicgames.com/documentation/unreal-engine/using-pcg-generation-modes-in-unreal-engine,
`D2` = https://dev.epicgames.com/documentation/unreal-engine/using-pcg-with-gpu-processing-in-unreal-engine,
`D3` = https://dev.epicgames.com/documentation/unreal-engine/runtime-hierarchical-generation (all fetched 2026-10-01; the "en-us" variants
of these pages return only a table of contents to the fetch tool, so the un-localised URLs were used).

## 0. Verdict

**Yes, PCG can drive Chimera's runtime-edited scatter, on the CPU, in a packaged Development and Shipping game. Risk: medium.
It is the more expensive route to build than a bespoke C++ scatter, and three of its load-bearing links have never been run on this
project (listed in §7). Recommendation: do not commit the scatter work to PCG on paper; commit it to a one-day spike with hard gates (§8),
behind a thin `IScatterSource` seam so a bespoke ISM scatter (about one class) takes over if a gate fails.**

Why it is possible:
1. Runtime generation is a runtime-module feature with no editor or shipping guard: `GenerateAtRuntime` plus a scheduler that ticks in
   game worlds (§1). Epic documents it for packaged builds (D1).
2. The sampler that matters (Surface Sampler) takes any `UPCGSurfaceData`; a C++ subclass wrapping Chimera's heightfield is the supported
   extension shape (the Landscape, texture and ray-hit data are all subclasses of the same base) (§2).
3. Placement is deterministic in world position by construction (§4).
4. A partitioned runtime component can be refreshed cell by cell from C++ (§3).

Why it is not free:
- The scheduler does nothing unless an `APCGWorldActor` exists in the world, and the only creator (`CreatePCGWorldActor`) is `WITH_EDITOR`.
  Chimera has no `.umap`, so the game must `SpawnActor<APCGWorldActor>` itself (§1.3). UNVERIFIED that this registers cleanly.
- Change tracking (the thing that makes PCG follow edits in the editor) is editor-only; at runtime the game must push refreshes itself (§3).
- GPU PCG does not apply to a graph built in C++ and is Beta; plan CPU only (§5).
- The CPU path costs game-thread time in the ISM spawner and a worker-thread budget that competes with the sim (§3, §6).

## 1. Can PCG generate at runtime in a packaged Shipping game? (a)

### 1.1 The plugin is a runtime plugin
- `PL/PCG/PCG.uplugin`: modules `PCG` (Runtime, Default), `PCGEditor` (Editor), `PCGCompute` (Runtime, PostConfigInit); `EnabledByDefault: true`;
  `IsBetaVersion: false`; description "populating worlds with content in editor and/or at run-time". Plugin dependencies: ComputeFramework (Beta),
  GeometryProcessing (Beta), MeshModelingToolset (Beta), EditorScriptingUtilities.
- `PCG.Build.cs:12-30` public deps include `Landscape`, `Foliage`, `GeometryAlgorithms`, `GeometryFramework`, `ComputeFramework`; private `PCGCompute`,
  `Renderer`, `Voronoi`, `Json`. Editor-only deps are behind `Target.bBuildEditor` (`:34-46`).
- Precompiled game objects exist for the installed engine: `PL/PCG/Intermediate/Build/Win64/x64/UnrealGame/{Development,Shipping}` (and the same for
  `GeometryProcessing`), so a Game target links PCG rather than compiling its 575 `.cpp` files (counted 2026-10-01). Link time and exe size are not measured.
- No `UE_BUILD_SHIPPING` guard touches generation or scheduling. The shipping guards found are all debug aids (`Private/Compute/DataInterfaces/PCGInstanceDataInterface.cpp:49-57`,
  `PCGElement.cpp:786`, print and visualise nodes); `PCGGraphExecutor.cpp:2530` only gates logging.

### 1.2 Generation trigger and scheduler
- `Public/PCGComponent.h:77-82`: `EPCGComponentGenerationTrigger { GenerateOnLoad, GenerateOnDemand, GenerateAtRuntime }`; `:327` the property; `:526`
  `IsManagedByRuntimeGenSystem()`. A runtime-managed component refuses every other trigger (`Private/PCGComponent.cpp:373-384`).
- `Private/Subsystems/PCGSubsystem.cpp:273` creates `FPCGRuntimeGenScheduler` for every world; `:425-428` ticks it **only if `PCGWorldActor` is non-null**.
- Generation sources (`Private/RuntimeGen/PCGGenSourceManager.cpp:203-233`): every local `APlayerController` becomes a `UPCGGenSourcePlayer` when
  `APCGWorldActor::bTreatPlayerControllersAsGenerationSources` is true (default, `Public/PCGWorldActor.h:82`). That source reports
  `GetPlayerViewPoint` (`Private/RuntimeGen/GenSources/PCGGenSourcePlayer.cpp:41`), i.e. the **camera** position, which for the RTS camera is about 61 m above
  the ground and 51 m behind the look-at point (rts80 pose in plan C §3.8). A `UPCGGenSourceComponent` (`Public/RuntimeGen/GenSources/PCGGenSourceComponent.h:18-45`) placed at the camera's
  ground focus point is the better source. The Distance-and-Direction policy can cull by view frustum (`Public/RuntimeGen/SchedulingPolicies/PCGSchedulingPolicyDistanceAndDirection.h:35,60`).
- Radii: default generation radius is 2x the grid size, cleanup 1.1x (`Public/PCGCommon.h:620-621`); at most 16 components generate at once
  (`Private/RuntimeGen/PCGRuntimeGenScheduler.cpp:66-70`, `pcg.RuntimeGeneration.NumGeneratingComponents`).
- Hierarchical generation (HiGen) with a 2D grid is graph state: `bUseHierarchicalGeneration`, `HiGenGridSize`, `bUse2DGrid` (`Public/PCGGraph.h:718-728`), grid sizes 400 cm to 204,800 cm in
  powers of two in the UI (larger ones hidden) (`Public/PCGCommon.h:520-553`). Docs: runtime generation "works across editor, PIE and packaged builds", "most effective when paired with Hierarchical Generation" (D1, D3).
- Cell actors: `FindOrCreatePCGPartitionActor` spawns `APCGPartitionActor`s for runtime grids in a game world (`Private/Subsystems/PCGSubsystem.cpp:1385-1460`, `SetToRuntimeGenerated` `:1460`);
  for non-runtime grids in a game world it returns null because "PAs need to be Pre-Existing" (`:1355-1360`). **A partitioned `GenerateOnLoad` component therefore cannot work in Chimera (no baked map); `GenerateAtRuntime` can.**
- Actor-less mode exists but is off and experimental: `bUseActorComponentlessGeneration` "[EXPERIMENTAL] ... Any functionality which depends on a target actor/component are not supported" (`Public/PCGGraph.h:805-807`), gated by cvar default false (`Private/PCGGraph.cpp:70-73`). Not used here: the Static Mesh Spawner needs a target actor.

### 1.3 The missing world actor (the one hard blocker)
- `FindOrCreatePCGPartitionActor` returns null with "PCGWorldActor is null" (`Private/Subsystems/PCGSubsystem.cpp:1344`), and the scheduler tick is skipped without it (`:425`).
- The subsystem finds one only by scanning the persistent level at `PostInitialize` (`:251-262`) and a game world cannot create one: `APCGWorldActor::CreatePCGWorldActor` is `#if WITH_EDITOR` (`Public/PCGWorldActor.h:55-57`, `Private/PCGWorldActor.cpp:207-227`),
  and `GetPCGWorldActor()` only auto-creates under `WITH_EDITOR` (`Private/Subsystems/PCGSubsystem.cpp:436-446`).
- The class is `UCLASS(MinimalAPI, NotBlueprintable, NotPlaceable)` (`Public/PCGWorldActor.h:16`); `BeginPlay` calls `RegisterToSubsystem` (`Private/PCGWorldActor.cpp:187-191`, `:234-240`) and `RegisterPCGWorldActor` adopts it (`PCGSubsystem.cpp:648-659`).
  `NotPlaceable` blocks the editor, not `UWorld::SpawnActor<APCGWorldActor>()` from C++. **Plan: spawn it in the game mode before any PCG component registers. UNVERIFIED** (nobody has done this in this repo; the engine's own cooked path is "the actor is saved in the map").
  Its `PartitionGridSize` (default 25,600 cm, `Public/PCGWorldActor.h:59,64`) matters only for non-HiGen graphs.

### 1.4 What Chimera needs on top
- Create the component in C++: spawn an actor, `NewObject<UPCGComponent>`, set `GenerationTrigger = GenerateAtRuntime`, `bIsComponentPartitioned = true` (`Public/PCGComponent.h:324,327`), `Seed`, `SetGraph(...)`, then `RegisterComponent`.
  The scheduler registers original components on registration (`PCGSubsystem.cpp:669-690`).
- The graph can be built in C++ at runtime: `UPCGGraph::AddNodeOfType`, `AddNodeInstance`, `AddEdge` are not editor-gated (`Public/PCGGraph.h:450-477`), and `Compile` falls back to in-process compilation when there is no cooked data
  (`Private/Graph/PCGGraphCompiler.cpp:416-423`, `:1252-1285`). The same calls are `UFUNCTION(BlueprintCallable)`, so a Python commandlet can also author a `.uasset` graph (C7's pattern); an asset graph is compiled at cook time
  (`Private/PCGGraph.cpp:964-1062`) and runs from cooked tasks (`PCGGraphCompiler.cpp:1252`). Either meets the "text first, no hand-edited binary assets" rule; a C++-built graph needs no asset at all.
- `UPCGGraph::bUseHierarchicalGeneration`, `HiGenGridSize`, `bUse2DGrid` and `GenerationRadii` are plain `UPROPERTY` members (`Public/PCGGraph.h:718-728,802`) and settable from C++. How a graph assigns each node to a grid (HiGen Grid Size node) is a settings class too (`Public/Elements/PCGHiGenGridSize.h`).

## 2. How PCG samples a surface that is not a Landscape (b)

### 2.1 What the Surface Sampler needs
- `UPCGSurfaceSamplerSettings` input pin "Surface" is typed `EPCGDataType::Surface`, required, multiple connections and data allowed (`Private/Elements/PCGSurfaceSampler.cpp:412-421`); the element casts inputs to `UPCGSurfaceData`
  (`:542-546`) and for every grid-cell candidate calls `InSurface->ProjectPoint(...)` (`:312`) with the result carrying position, normal-aligned transform and metadata (layer weights). A surface is "two dimensional" (`Public/Data/PCGSurfaceData.h:25-30`).
- A custom surface therefore needs: `GetBounds`, `SamplePoint`, `ProjectPoint`, `CopyInternal`, `AddToCrc` (and optionally `InitializeTargetMetadata` to declare layer attributes). Pure virtuals are listed in `Public/Data/PCGSpatialData.h:137-189,280-282`.
  `UPCGLandscapeData` is the worked example of the same shape: `ProjectPoint` (`Public/Data/PCGLandscapeData.h:84`), `InitializeTargetMetadata` (`:92`) and layer weights as attributes (`:29,109`).
- The subclass lives in the game module (`UCLASS(MinimalAPI)` base, `PCG_API`-exported `CopyBaseSurfaceData` `Public/Data/PCGSurfaceData.h:34`); the module needs `PCG` in `PublicDependencyModuleNames`. Custom nodes are the documented extension: Epic ships a settings/element template
  (`PL/PCG/Content/Templates/PCGTemplateSettings.h.template`, `.cpp.template`).

### 2.2 Recommended source: a C++ "Chimera Heightfield Surface" node
A `UPCGChimeraTerrainSettings` plus `FPCGChimeraTerrainElement` with no input pins and one `Surface` output. The element reads the execution source's bounds (the cell), snapshots the covering height and splat rectangle, and emits one `UPCGChimeraSurfaceData`.
- `ProjectPoint`: bilinear height from the snapshot (Chimera already has `FTerrainHeightfield::SampleSurface(WorldX, WorldY)`, `CT/Data/TerrainHeightfield.h:112`, matching the triangulated render surface), normal from `GetNormal` (`:103`), layer weights from the 0.5 m splat (`SplatTexel` `:115`) written as `Grass/Dirt/Rock/Snow` float attributes, like the Landscape's layer weights.
  Slope can come from the normal (the Normal To Density node, `Public/Elements/PCGNormalToDensity.h`).
- Downstream nodes are stock PCG: Surface Sampler, Attribute Filter (`Public/Elements/PCGAttributeFilter.h`), Density filter/remap, Self Pruning (`Public/Elements/PCGSelfPruning.h:127`), Static Mesh Spawner. Path, rock and snow thinning is an attribute filter; no custom code.
- **Thread safety is the design constraint.** PCG elements run on worker threads (`Public/PCGElement.h:151` default false for main-thread-only) while strokes mutate the heightfield on the game thread at 30 Hz. Copy the rectangle into an immutable snapshot (a cell of 256 m is 257² floats = 0.26 MB plus a 512² RGBA splat = 1 MB; the 32 m cell is about 30 KB) inside the element
  and let workers read only the snapshot. Marking the "get data" element main-thread-only (`CanExecuteOnlyOnMainThread`) is the simple way to take the snapshot without a lock. The graph cache is off at runtime (`Private/Graph/PCGGraphCache.cpp:19-22`, `pcg.Cache.Runtime.Enabled` default false), so there is no stale-output cache to bust,
  and `AddToCrc` can use a version counter anyway.

### 2.3 Other routes checked and why not
| Route | What the source shows | Verdict |
|---|---|---|
| Landscape data | needs `ALandscapeProxy` (`Public/Data/PCGLandscapeData.h:68-110`) | not applicable (Chimera is RMC) |
| World Ray Hit Query (`UPCGWorldRayHitData : UPCGSurfaceData`) | surface made of physics line traces; works on RMC collision (`Public/Data/PCGWorldData.h:415-470`, `Private/Data/PCGWorldData.cpp:445-451`); runs on workers unless landscape metadata is requested (`Private/Elements/PCGWorldQuery.cpp:161-166`) | **zero C++ fallback** for the spike: set channel to Visibility, `bIgnorePCGHits`. No layer weights (needs a second texture sample), one trace per candidate, and it sees collision only after the async cook that follows a stroke (plan C §3.6, P5 cook p95 ≤ 50 ms) |
| Texture data from the splat (`UPCGTextureData::Initialize(UTexture*,...)`, `Public/Data/PCGTextureData.h:212`) | CPU access needs a CPU-accessible texture or an async GPU readback (`Private/Data/PCGTextureData.cpp:618,887,968-992,1027-1034`) | usable for splat weights only; a readback adds frames of latency; the CPU array is better |
| Primitive data (`UPCGPrimitiveData`, accepts any `UPrimitiveComponent`, so RMC components can be fed in) | a 3D voxel volume via `OverlapComponent` per 100 cm voxel (`Private/Data/PCGPrimitiveData.cpp:32-47,66-76`, registry `Private/Data/Registry/PCGGetDataFunctionRegistry.cpp:333-336`); no `ProjectPoint` ("TODO needs an implementation to support projection", `Public/Data/PCGPrimitiveData.h:33-34`) | rejected: volume, not surface, coarse, physics-bound |
| Dynamic mesh data (`UPCGDynamicMeshData`) | `ToPointData` returns only the **vertices** (`Private/Data/PCGDynamicMeshData.cpp:174-212`); no `ProjectPoint` (`Public/Data/PCGDynamicMeshData.h:38-39`) | rejected as a surface |
| Mesh Sampler (GeometryScript interop, Beta plugin, adds GeometryScripting) | one point per triangle / vertex, or Poisson (not time-sliced, "can be expensive") (`GSI/Public/Elements/PCGMeshSampler.h:34-43,103`, `Private/Elements/PCGMeshSampler.cpp:184-226`) | possible (`UDynamicMesh` built from the heights) but needs a mesh copy per cell and extra plugins; rejected |
| Virtual-texture data (`UPCGVirtualTextureData : UPCGSurfaceData`, `Public/Data/PCGVirtualTextureData.h:21`) | needs an RVT | impossible: RMC Dynamic draws no RVT (plan C §2.8) |
| Custom point set (a C++ element that emits `UPCGPointArrayData` directly) | supported (the template above); gives up the Surface Sampler's world-grid determinism (§4) unless the element reproduces it | only if the surface route is blocked |

RMC components cannot be handed to a PCG surface directly (RMC has no PCG integration; `UPCGPrimitiveData` treats them as opaque physics bodies). The right input is Chimera's own height array, which is the source RMC is built from.

## 3. Incremental regeneration after an edit and its cost (c)

### 3.1 What exists at runtime
- Editor change tracking is compiled out of games: `Public/Subsystems/PCGSubsystem.h:335-417` (`ScheduleRefresh`, `DirtyGraph`, `NotifySelectionKeyChanged`, tracking) is `#if WITH_EDITOR`; `Public/ChangeTracking/PCGChangeTracker.h:5`, `Public/PCGComponent.h:426` `Refresh()` likewise. **PCG will not notice a stroke.**
- Runtime refresh API (not editor-gated): `RefreshRuntimeGenExecutionSource(Source, ChangeType)` (`Public/Subsystems/PCGSubsystem.h:217`), `RefreshAllRuntimeGenExecutionSources` (`:221`), `DirtyRuntimeGenExecutionSources` (`:225`, deferred and coalesced to the next tick),
  `ForAllRegisteredIntersectingLocalComponents(OriginalComponent, Bounds, Func)` (`:256`).
- Behaviour of a refresh (`Private/RuntimeGen/PCGRuntimeGenScheduler.cpp:2041-2300`): for the original source it flushes the cached cell scan, cancels, and removes **all** generated cells for it (`:2132-2160`): a whole-map regeneration. For a **local** (single-cell) source it cleans up that cell only (`:2118-2130`) and queues it for regeneration.
  `ChangeType` must not include `GenerationGrid` (that forces the deep path that returns partition actors to the pool, `PCGSubsystem.cpp:1071-1076`).
- Per-cell flow for Chimera: at stroke end (and optionally every N ms during a stroke), take the dirty rectangle, convert to a world box, `ForAllRegisteredIntersectingLocalComponents` on each HiGen grid's component set, and `RefreshRuntimeGenExecutionSource(Local)`. Cells the camera has not generated are not in the set and cost nothing; they generate fresh from current data when the camera arrives.
- Instance continuity: a cell refresh uses `bReleaseManagedResources=false` (`PCGRuntimeGenScheduler.cpp:2127-2131`), which soft-releases managed ISMs: at runtime the component is **not** hidden (hiding is `bMarkedTransientOnLoad`, editor only, `Private/PCGManagedResource.cpp:608-615`),
  it is marked unused and tagged for cleanup (`:630-640`), then reused with `ClearInstances` when the regenerated spawner runs (`:979-1030`, `pcg.ISM.AllowReuse` default true `Private/Elements/PCGStaticMeshSpawner.cpp:111-115`). Expected visual effect: old instances stay until replaced, so no blank flash, only a stale interval. UNVERIFIED.

### 3.2 Cost (estimates; nothing measured, no Unreal run)
- Budget cvars: game-thread time per frame `pcg.FrameTime` = 5.0 ms default (`Private/Graph/PCGGraphExecutor.cpp:81-84`); worker threads: 90 % of cores usable, 30 % of that executing, hard cap 8 (`:44-62`): on the Ryzen 5 5600 (12 threads) that is about 3 executing threads
  (arithmetic from the defaults; the PC's thread count is from the CPU model, not queried). Async point loops time-slice at 5 ms per task (`Private/Helpers/PCGAsync.cpp:25-29`). The Static Mesh Spawner is main-thread-only in PrepareData and Execute (`Private/Elements/PCGStaticMeshSpawner.cpp:721-727`): ISM instance population is game-thread work.
  Left at defaults, PCG may take 5 ms of a 16.7 ms frame on the game thread while cells regenerate, which would eat the D2 floor (median >= 60 fps) during sculpting. **Set `pcg.FrameTime` to 1-1.5 ms and debounce refreshes** (stroke end plus a quiet period); the scheduler already spreads work across frames (`pcg.RuntimeGeneration.TimeBetweenRuntimeGenSchedulerTicks`, `PCGRuntimeGenScheduler.cpp:136-139`).
- Work per cell, order of magnitude (the Surface Sampler is one hash and one `ProjectPoint` per candidate, `PCGSurfaceSampler.cpp:258-330`): a 32 m grass cell at 2 candidates/m² is about 2,000 candidates; a 128 m tree cell at 0.02/m² is about 330. These are well under a millisecond of worker time each by arithmetic;
  the game-thread ISM population and `PrecachePSOs` for new components (`Private/Components/PCGProceduralISMComponent.cpp:93`, similar for ISM) are the unknowns. A d=100 brush (50 m radius) touches about 4-9 cells of 32 m and 1-4 of 128 m.
- **Required measurement, not estimable here:** the spike in §8 measures per-cell regenerate ms (worker and game thread), instance count, and sculpt-with-scatter fps. Do not rely on the numbers above.
- Hidden costs outside PCG: new/changed instances invalidate cached virtual shadow map pages in their region (the plan C §2.9 VSM concern now applies to scatter too); Static-mobility ISMs are cached, so repeated regeneration in a hot region re-renders those pages.

## 4. Determinism (hard constraint 3)
- The Surface Sampler places candidates on a world-anchored grid: "By using scaled indices in the world, we can easily make this process deterministic" (`Private/Elements/PCGSurfaceSampler.cpp:116`; cell indices from the world bounds `:113-121`), and each candidate's chance and jitter come from `FRandomStream(ComputeSeed(Seed, CellX, CellY))` (`:281`).
  XY positions therefore depend only on world position and seed, not on partition boundaries or frame rate; sculpting changes Z and normal, never XY, so scatter does not swim sideways after an edit.
- Local components copy the original's seed (`Private/PCGComponent.cpp:445`); `Context->GetSeed()` combines it with the node's seed (`Private/PCGContext.cpp:138-146`). Per-point seeds use `PCGHelpers::ComputeSeedFromPosition` (`Public/Helpers/PCGHelpers.h:43`).
- After reload (S1L) the same height and splat bytes give the same filter outcomes and the same instances. Float-order differences between machines are not controlled; same-machine determinism is what S1L needs. UNVERIFIED until a hash of the generated instance transforms is recorded (the spike adds one).
- Rule for the custom node: no `FMath::Rand`, no time, no frame counter, no iteration over a `TMap`/`TSet` of object pointers.

## 5. GPU PCG and compute graphs in 5.8 (d)
- Status: **Beta**. Docs: "Learn to use this Beta feature, but use caution when shipping with it" (D2). Supported GPU nodes: Attribute Partition (strings and paths only), Copy Points, Cull Points Outside Actor Bounds, Custom HLSL, Data Count, Normal To Density, Static Mesh Spawner, Transform Points (D2).
  Notably the Surface Sampler is not on the list; a CPU sampler followed by GPU nodes pays an upload and a possible readback (D2: "minimize how much data is transferred").
- The GPU Static Mesh Spawner path uses Procedural ISM Components: "Instances are not persisted or saved in any way. They exist only at runtime in GPU memory"; static baking, HLODs, collision, navigation, ray tracing and distance-field lighting are unsupported (D2).
  The PISM component clears `bAffectDistanceFieldLighting` and `bAffectDynamicIndirectLighting` and sets `bIsEditorOnly = true` in its constructor (`Private/Components/PCGProceduralISMComponent.cpp:128,140`; why the last flag is set is unclear from the source). Shadow behaviour (VSM) of PISMC is UNVERIFIED.
- Packaged availability: compute graphs are compiled in the editor at cook time and shipped as cooked data (`Private/PCGGraph.cpp:964-1062`, `Private/Graph/PCGGraphCompiler.cpp:94-107` "Cannot compile compute graphs outside of editor, so we always look for cooked compute graphs"). Outside the editor, graphs with no cooked data **cull every GPU node**
  (`PCGGraphCompiler.cpp:1431-1451`, comment "GPU execution is editor only for now. Need to cook kernels for standalone"). So a graph built in C++ at runtime is CPU only; only a cooked graph asset can use GPU nodes. The engine's own samples ship GPU graphs (`PL/Experimental/PCGBiomeSample/Content/BiomeGenerators/Runtime/GroundScatterGeneratorGraphGPU_*.uasset`, `PL/PCG/Content/GraphTemplates/TPL_Showcase_RuntimeGrassGPU.uasset`), but whether they run in a packaged build was not checked.
- Verdict: **do not use GPU PCG for the trial.** It is Beta, the sampler that Chimera needs is CPU, re-running a GPU graph per edited cell is untested, and the cooked-graph prerequisite forces an editor-authored `.uasset`. Revisit only if the CPU path's game-thread cost fails §8 gate G4.

## 6. What it costs to ship (e)
- **Plugins to enable** in `ChimeraTerrain.uproject`: `PCG` is `EnabledByDefault: true` (`PCG.uplugin`) so it is already in the plugin set; its dependencies ComputeFramework, GeometryProcessing and MeshModelingToolset are Beta and come with it. List `PCG` explicitly and add `"PCG"` (plus `"GeometryScriptingCore"` only if the Mesh Sampler is ever used) to the module's dependencies. `PCGBiomeSample`/`PCGBiomeCore` are **not** needed for the pipeline (§9).
- **Modules linked into the game:** `PCG` (575 `.cpp`) and `PCGCompute` (9) and, through `PCG.Build.cs`, `Landscape`, `Foliage`, `Voronoi` and `GeometryAlgorithms`: code for features Chimera does not use. Binary-size and startup-time increase is not measured (UNVERIFIED); no extra content is cooked unless referenced.
- **Cook:** no `.umap`, no world-actor serialization (the world actor is spawned). A C++-built graph needs no cooked graph asset. Meshes and materials used by the spawner must be cooked: load them by path in C++ and add their folder to `DirectoriesToAlwaysCook` (the same mechanism plan C §3.1 uses for the clouds), or they will be missing in the package. Chimera's `Content` stays git-ignored; the scatter mesh import is a script, as in C7.
- **Memory:** the PCG runtime graph cache is off by default and capped at 128 MB when on (`Private/Graph/PCGGraphCache.cpp:19-22,34-37`). ISM component and instance memory scales with instance count: 100k instances of a 4x4 matrix plus custom data is roughly 6-10 MB of CPU and GPU scene data (arithmetic, UNVERIFIED). The look-test scene's scatter (about 890 static-mesh actors) cost no measurable memory outside VRAM 3.1 GB for the whole scene (`LT/out/fps_summary.json`, `docs/unreal-move/look-test-2026-10-01/README.md`).
- **Runtime budget:** CPU worker threads (about 3 on the 5600) overlap the sim and the unit layer; game-thread ISM population (§3.2); GPU: instance draw and shadow cost, not PCG, dominates. The 3.0 ms terrain gate (D2, C12) excludes scatter by definition; the trees-and-scatter scene is reported only (EXECUTION.md §5 D2).
- **Packaging risk:** nothing in this repo has packaged a PCG-enabled game; plan C's own packaging is itself UNVERIFIED end to end (plan C §6 risk 8). PCG adds one more cook-time thing to break (graph compile in `PreSave` only for graph assets).
- **Licence:** PCG and the `/PCGBiomeSample` meshes are Epic engine content under the Unreal Engine EULA (EULA page returned HTTP 403 to the fetch tool, so not quoted; see open questions). Poly Haven or other CC0 meshes are the licence-clean alternative and need no Epic terms.

## 7. Risks, ranked
| # | Risk | Evidence | Mitigation |
|---|---|---|---|
| 1 | Spawning `APCGWorldActor` at runtime in a map-less world does not register or tick the scheduler | `PCGWorldActor.h:55-57`, `PCGSubsystem.cpp:251-262,425,648-659` | spike S1 (gate G1); fallback: add a one-actor `.umap` generated by a script, or bespoke scatter |
| 2 | Per-cell refresh costs more game-thread time than D2 allows while sculpting (5 ms default PCG tick, main-thread spawner) | `PCGGraphExecutor.cpp:81-84`, `PCGStaticMeshSpawner.cpp:721-727` | `pcg.FrameTime` 1-1.5 ms, refresh at stroke end plus debounce, measure with C1U-style run |
| 3 | Custom surface node correctness and thread safety (worker reads while the game thread edits) | §2.2 | immutable snapshot per cell; main-thread-only get-data element; automation test that the snapshot equals the heightfield |
| 4 | Stale-instance interval after an edit is visible (instances sit on the old surface until regenerated) | §3.1 | reduce by refreshing only dirty cells, accept as reported, or hide with the brush ring; gate G3 measures it |
| 5 | Runtime gen in a non-World-Partition world untested | scheduler queries `WorldPartitionSubsystem` only when non-null (`PCGRuntimeGenScheduler.cpp:263,501,568`) | spike S1 |
| 6 | Beta plugin dependencies (ComputeFramework, GeometryProcessing, MeshModelingToolset) and API churn between engine point releases | `PCG.uplugin` | pin 5.8.3; the custom node is the only code touching PCG types |
| 7 | One more large dependency for a feature a bespoke class could deliver | §0 | keep `IScatterSource`; decide after the spike |

## 8. Recommended spike (design only; cannot run now because of the lock rule)
Seam: `IScatterSource { OnTerrainEdited(FTerrainRect), ... }` called from `ATerrainActor::ApplyDelta` (`CT/Game/TerrainActor.h:170`), so PCG or a bespoke ISM scatter sits behind one interface and presentation never touches sim state.
- **S1 (gate G1):** in `-game` and packaged Development: game mode spawns `APCGWorldActor`, a partitioned `GenerateAtRuntime` component with a C++-built graph (Chimera surface -> Surface Sampler -> Static Mesh Spawner with one tree and one grass mesh), a `UPCGGenSourceComponent` at the camera focus; log instance counts per cell. Pass: instances appear in both builds and `LogPCG` has no errors.
- **S2 (G2, correctness):** `ProjectPoint` equals `FTerrainHeightfield::SampleSurface` within 1 cm at 10k seeded points; after a script stroke plus paint, instances sit on the edited surface and thin out where `Dirt/Rock/Snow` weight is high (counts per layer logged); undo returns the previous instance transforms (hash).
- **S3 (G3, determinism):** instance-transform hash identical across two runs, a different frame cap and a different camera path, and after `S1L` reload.
- **S4 (G4, cost):** C1U with scatter: median >= 60 fps, 1%-low >= 45 while sculpting (the D2 floor); `pcg.FrameTime` 1.5 ms; per-cell regenerate game-thread ms and worker ms; time from stroke end to instances updated (target <= 500 ms for a d=20 stroke; reported, not gated).
- **Fallback decision:** G1 or G4 fails -> bespoke C++ scatter: one `UInstancedStaticMeshComponent` (or HISM) per mesh per 32 m cell, positions from the same world-cell hash recipe as §4, `SampleSurface` for Z, splat for density, rebuilt for dirty cells at stroke end. About the same editing seam and none of PCG's runtime dependencies. G2 or G3 fails -> fix the node.

## 9. Look-test project: how PCGBiomeSample is used (`D:/Projects/Chimera-Unreal/ProjectChimera`)
- `ProjectChimera.uproject` enables `PCGBiomeSample` (`"Plugins"` list, last entry), plus `ModelContextProtocol`, `ToolsetRegistry`, `AllToolsets`, `EditorToolset` (Editor only), `PythonScriptPlugin`, `EditorScriptingUtilities`. It does not list `PCG` or `PCGBiomeCore`: they load as dependencies of `PCGBiomeSample` (`PL/Experimental/PCGBiomeSample/PCGBiomeSample.uplugin` depends on `PCGBiomeCore` and `PCG`; `PCGBiomeCore.uplugin` depends on `PCG` and `PCGGeometryScriptInterop`, which depends on `GeometryScripting`).
  `PCGBiomeSample` is `EnabledByDefault: false`, `IsExperimentalVersion: true`, content only (no module); `PCGBiomeCore` has a runtime module needing only `Core/CoreUObject/Engine/Slate/SlateCore` (`PCGBiomeCore.Build.cs`).
- **PCG graphs are not used at all.** `LT/tools/lt_common.py:42-47` names nine meshes (`/PCGBiomeSample/Meshes/PCG_Tree_01, PCG_Tree_02, PCG_Spruce_01, PCG_Pine_01, PCG_Sapling_01/02, PCG_Seedling_01/02, PCG_Boulder_01`) as `SCATTER_MESHES`; `LT/tools/lt_build.py:265-270` places them as individual static-mesh actors from a Python `random.Random(seed + 1)` layout (`lt_common.py:344-393`): at most 450 trees, 400 undergrowth, 40 boulders, on a flat plane. No PCG component, graph or runtime generation ran in the look test.
  Plan notes confirm it: "the trees are PCGBiomeSample stylised meshes" (`LT/PLAN.md:499`); the mesh claim is `LT/PLAN_CHECKS.md:303-305` (default materials load, "Rendering with the default materials is UNCONFIRMED").
- The sample's PCG assets are an editor biome-authoring toolset: `BiomeDefinitions/` (BroadleafForest, ConiferousForest, Desert, Landslide, MountainForest), `BiomeGenerators/` (including `Runtime/GroundScatterGeneratorGraphGPU_*`), `PCGAssemblies/`, `Tiles/`, a 102 MB `__ExternalActors__` set and `BiomeSampleLevel.umap`; 113 MB in all (du, 2026-10-01). Meshes are small (`Meshes/*.uasset` 12-98 KB each) with `GPURuntime/` variants for the GPU path. Chimera needs only the meshes and their materials; the rest is not cooked unless referenced.
- Look-test numbers for context: A_noLumen 76.2 fps median, 66.6 1%-low, 12.7 ms GPU, VRAM 3.1 GB (`docs/unreal-move/look-test-2026-10-01/README.md`); ChimeraTerrain after C7: terrain GPU 2.96 ms against the 3.0 ms bar (EXECUTION.md §8). Scatter has to live in the difference, so its GPU cost matters more than PCG's CPU cost.
- `ChimeraTerrain.uproject` does not mention PCG or PCGBiomeSample (it lists RealtimeMeshComponent, PythonScriptPlugin, AndroidFileServer off, EditorScriptingUtilities). PCG is engine-default-enabled; the scatter meshes should come from a CC0 manifest (hard constraint 5), with PCGBiomeSample meshes as the zero-cost stand-in for the spike only if their EULA terms are accepted.

## 10. Open questions
1. Does `SpawnActor<APCGWorldActor>()` in a game world with no map actor register with `UPCGSubsystem` and start the scheduler in both a `-game` run and a packaged Shipping build? (§1.3, spike G1.)
2. Does runtime generation work in a world that is not World Partition (Chimera uses `/Engine/Maps/Entry`)? The scheduler only skips WP queries when the subsystem is absent (`PCGRuntimeGenScheduler.cpp:263,501,568`); the docs example worlds are WP (D1).
3. What are the real numbers: per-cell regenerate ms (worker, game thread), stroke-end-to-updated latency, instance memory, and the sculpt fps with scatter at d=100? None can be derived from source.
4. Does cell refresh via `RefreshRuntimeGenExecutionSource(Local)` visibly leave stale instances, and for how long (§3.1)?
5. Epic content licence for shipping `/PCGBiomeSample` meshes and materials in a product: the EULA page was not retrievable (HTTP 403). Alec or a licence check should confirm; the CC0 alternative removes the question.
6. Does the Static Mesh Spawner's HISM/ISM choice (Nanite meshes decay to ISM, `Private/Helpers/PCGActorHelpers.cpp:128-157`) and per-cell component count produce acceptable draw-call counts across 100 cells of 32 m on this GPU? Unknown until measured.
7. Is PISMC (GPU instancing) compatible with virtual shadow maps for tree shadow pools? Only matters if CPU fails the cost gate.
8. Should G1 fail only for the actor spawn, would a script-generated one-actor `.umap` be acceptable under the "no `.umap` of ours" rule in plan C §3.1 (it is text-first if generated by script)?
