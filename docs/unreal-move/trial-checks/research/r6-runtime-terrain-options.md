# R6. Runtime-editable terrain for Unreal 5.8 (trial check c)

Date of research: 2026-10-01. Engine checked: D:/Epic Games/UE_5.8, `Engine/Build/Build.version` says 5.8.3,
branch `++UE5+Release-5.8` (Build.version:2-3,7). Every claim carries a file:line or a URL. "UNVERIFIED" marks anything I
could not confirm from a header, a source file or a fetched page. Nothing in either project was edited and no editor or
UE build was run. I cloned RealtimeMeshComponent into the session scratchpad (outside both projects) to read its source.

## 1. Bottom line

1. Recommended for the trial: **RealtimeMeshComponent (RMC) Core, master @ `b8669a0`, as a grid of per-chunk components**,
   with heights and paint layers held as plain arrays that the game owns. The cursor is picked by marching a ray through
   that same height array, not by physics. RMC Core is MIT, claims UE 5.5 to 5.8, and ships a ready-made "Dynamic grid
   edited in place" example that is almost exactly a heightfield.
2. Fallback if RMC fails the first gate: **`UDynamicMeshComponent`** (in the engine, GeometryFramework, runtime module,
   not experimental). Heavier per edit, but zero third-party risk and it has partial render-buffer updates.
3. Errant Landscape is the only option here that edits the **stock** Landscape at runtime. It has a free 30-day trial, so
   it can be evaluated without buying. It is a stamp-brush system (not direct vertex sculpting), is documented as
   incompatible with Nanite, and I found no statement of UE 5.8 support (latest release 1.7, 2026-06-15). Keep it as the
   second candidate, trial it only if RMC fails.
4. The spec line "Unreal's own Landscape can't be sculpted in a shipped game, and 5.8's Mesh Terrain doesn't change that"
   is **true for stock Landscape and Mesh Terrain**, verified in engine source (section 5). It needs one softening: Errant
   claims to do it as a plugin (section 4).
5. RMC has two live 5.8 hazards that the first trial step must test (section 3.5): open issue #303 (static-draw meshes
   do not render in standalone/packaged on 5.8.2) and open issue #290 (native collision memory retained until GC).
   The first is avoided by always creating Dynamic buffer sets, which sculpting needs anyway.
6. No purchase is needed for the trial. Possible later purchases (all Alec's call): Errant Landscape (price UNVERIFIED,
   per-seat perpetual with 1 year of updates), RMC Pro $49.99 (only if runtime Nanite terrain is wanted).

## 2. What Chimera's terrain has to do (from the repo and the spec)

- Spec: "Sculpt and paint ... regions, impassable paint, water" is a Godot-built editor feature that must work in the
  shipped game. The creation suite lives in the game, never in Unreal's editor (unreal_spec.txt:171, :336-345 non-negotiables).
  Terrain row: "RealtimeMeshComponent (free, MIT) or Errant Landscape's runtime module" (unreal_spec.txt:309-311).
- Today's tool: raise, lower, smooth, flatten, hard-paint one of 4 layers (Grass, Dirt, Rock, Snow), brush size and
  strength, undo through `EditorHistory` (godot/src/CreationSuite/TerrainBrush.cs:15-26, :68-72). New request: ramp.
- The sim only reads terrain through `ElevationGrid`: a flat `Fixed[]` row-major array sampled by clamped integer cell
  lookup, built once at load (godot/src/Core/ElevationGrid.cs:23, :59). So the editable terrain is a plain array first and a
  render mesh second. This favours any option whose source of truth is a CPU array we own.
- Map sizes: default map is 256x256 cells over +-128 world units (TerrainPhase.cs:49-67); "The Frontier" is +-320, so
  about 640x640 cells (TerrainPhase.cs:35). Impassable paint is a separate 128x128 bitset (ScenarioData.cs:1039).
  Worst case about 410k vertices and 820k triangles at one vertex per cell. That is small by Unreal standards; the
  difficulty is edit latency and collision, not triangle count.
- Look: Manor-Lords-style near-photoreal, Lumen off (HANDOFF.md, "World look decided").
- Trial project today: Substrate on, DX12 SM6, VSM on, no plugins for terrain (ProjectChimera/Config/DefaultEngine.ini,
  ProjectChimera.uproject). Module deps are only Core, CoreUObject, Engine, InputCore, EnhancedInput
  (ProjectChimera.Build.cs:11).

## 3. RealtimeMeshComponent (TriAxis Games)

### 3.1 Facts
| Item | Finding | Evidence |
|---|---|---|
| Repo | github.com/TriAxis-Games/RealtimeMeshComponent, 1,760 stars, not archived, 15 open issues | GitHub API, fetched 2026-10-01 |
| Licence | MIT for the Core edition (LICENSE.txt) | README.md "License" section; API `license: MIT` |
| Latest tag/release | `v5.3.2`, published 2026-07-27, pointing at commit `dbf43b7` dated 2025-12-02 ("Minor fixes for UE 5.7"). The tag has **no** UE 5.8 guards | `git grep 5_8 v5.3.2 -- Source` returned nothing; releases API |
| Branch for 5.8 | **master**. Head `b8669a0891b728d8fa8a31f43f76d3853c10f743`, 2026-08-23. `Release 5.4` (commit `dc8b9b2`, 2026-08-21) added the 5.8 guards; never tagged | `git log`; RealtimeMeshComponent.uplugin:4 (`VersionName 5.4`) |
| Supported engines | README says "UE 5.5 - 5.8" | README.md "Installation" |
| 5.8 code in master | `RMC_ENGINE_BELOW_5_8` guards, for example "5.8 removed FMeshBatchElement::bIsSplineProxy" | Source/RealtimeMeshComponent/Private/RenderProxy/RealtimeMeshSectionProxy.cpp:77; Public/Core/RealtimeMeshCoreFwd.h:24-25 |
| Engine header cross-check | `Templates/ChooseClass.h` is deprecated in 5.8 (`UE_DEPRECATED_HEADER`), a warning source | UE_5.8/Engine/Source/Runtime/Core/Public/Templates/ChooseClass.h:5; open PR #300 |
| Core vs Pro | Core = streams, LODs, sections, collision, C++/BP APIs, examples. Pro adds mesh providers, Nanite runtime builder, UDynamicMesh integration, compute meshes, spatial streaming | README.md "What's included", "Pro edition adds" |
| Pro price | $49.99 on Fab (search snippet; the Fab page itself returned 403) | WebSearch 2026-10-01; fab.com/listings/c04e6977-a3bd-4e24-b0a2-0b696bee90da. UNVERIFIED on the page |
| Docs | triaxis.games/realtime-mesh; the same docs are in the repo `Docs/` folder | README.md |

### 3.2 Install as a project plugin (does not need the Launcher or Fab)
1. `git clone https://github.com/TriAxis-Games/RealtimeMeshComponent` into
   `D:/Projects/Chimera-Unreal/ProjectChimera/Plugins/RealtimeMeshComponent/`, then check out `b8669a0` (pin the hash in the
   notes, because the project folder is not a git repo). README.md "Installation" steps 1-3.
2. Add `{ "Name": "RealtimeMeshComponent", "Enabled": true }` to ProjectChimera.uproject `Plugins`; add
   `"RealtimeMeshComponent"` to `PublicDependencyModuleNames` in ProjectChimera.Build.cs. Requires a C++ project, which the
   trial already is.
3. The plugin declares three modules; `RealtimeMeshExamples` is `Runtime` and loads by default
   (RealtimeMeshComponent.uplugin modules block). Drop or deny-list it for shipping builds. A "shipping/mac compilation" fix
   exists in history (commit `1d95171`, 2025-11-11), so packaged Windows builds are an expected path.

### 3.3 API for chunked sections and partial updates (verified in the cloned source)
- Component holds a `URealtimeMesh`; the mesh holds LODs, each LOD holds "buffer sets" (formerly section groups), each buffer
  set holds sections (Docs/component-core/structure/index.md). `URealtimeMeshSimple` is the concrete type used for
  hand-built meshes (Public/RealtimeMeshSimple.h:95-99, :130-148).
- Full rebuild: `CreateBufferSet` / `UpdateBufferSet(Key, StreamSet)` (RealtimeMeshSimple.h:130-136). Changing vertex
  count needs this.
- In-place edit: `EditMeshInPlace(Key, EditFunc)` returns the changed stream keys, and `EditMeshInPlaceRanged` returns a
  half-open element range per stream "uploads only that sub-region of each eligible vertex stream's GPU buffer (no
  realloc, no new proxy version)" for a **Dynamic** buffer set (RealtimeMeshSimple.h:164-170;
  Docs/component-core/updating-mesh-data/index.md). This is the sculpting path: heights only change `Z` and normals.
- Dynamic vs Static is set per buffer set at creation: `FRealtimeMeshBufferSetConfig(ERealtimeMeshSectionDrawType::Dynamic)`;
  default is `Static` (Public/Core/RealtimeMeshBufferSetConfig.h:14-18, :35). Docs: Static is cheaper to render but a change
  "means rebuilding the render proxy for every component using this mesh"; Dynamic "costs slightly more per frame"
  (Docs/component-core/sections/index.md:80-81).
- Worker-thread building: `DoOnAsyncThread` / `ContinueOnGameThread` in `Core/RealtimeMeshFuture.h`; a `FRealtimeMeshStreamSet`
  is pure CPU data (updating-mesh-data doc, "Building off the game thread").
- LOD: `AddLOD(FRealtimeMeshLODConfig(ScreenSize))`; LODs are fully independent buffer sets, so an edited chunk must be
  rebuilt at every LOD it has (Docs/component-core/lods/index.md). No built-in crack stitching: skirts or matched borders
  are our job.
- Shipped example that is nearly our case: `RealtimeMeshExample_Simple_FastUpdate` builds a Dynamic height grid and animates it with
  `EditMeshInPlace` and `EditMeshInPlaceRanged` (Source/RealtimeMeshExamples/Private/Simple/RealtimeMeshExample_Simple_FastUpdate.cpp:34-35,
  :109-140).
- Rendering into runtime virtual textures is supported by the RMC proxy
  (Private/RenderProxy/RealtimeMeshComponentProxy.cpp:198, :277-281).

### 3.4 Collision (verified)
- Config struct `FRealtimeMeshCollisionConfiguration`: `bUseComplexAsSimpleCollision` (default true), `bUseAsyncCook` (default true),
  `bShouldFastCookMeshes`, `bDeformableMesh`, and others (Docs/component-core/collision/index.md "Collision settings").
- Complex (per-triangle) collision from rendered triangles needs `bShouldCreateCollision` on each section. The doc itself
  names "arbitrary terrain" as the case for it.
- Async: cook runs on a worker thread, and "a newer update can overtake one still cooking", reported by
  `ERealtimeMeshCollisionUpdateResult` (Updated / Ignored / Error). Code: Private/RealtimeMeshManaged.cpp:676-741.
- **Granularity is the whole mesh, not the edited section.** `FRealtimeMeshLODManaged::GenerateComplexCollision` walks every
  section group and re-extracts all collision triangles (RealtimeMeshManaged.cpp:245-262). So one `URealtimeMesh` per
  terrain chunk is the only way a brush stroke re-cooks a few thousand triangles instead of 800k.
- Navigation: `bUpdateNavigationOnCollisionUpdate` defaults true; the doc says to turn it off for frequently changing
  meshes (collision doc, "Component level settings"). Chimera has no Unreal navmesh need, so turn it off.

### 3.5 Known problems on exactly our engine (open, with dates)
- **#303, open, 2026-09-03: "Meshes are not rendered in the base pass on UE 5.8".** UE 5.8.2, RMC master `b8669a0`, AMD
  Radeon 890M, D3D12 SM5 and SM6. The bundled examples draw nothing in `-game`; wireframe, selection and the editor/PIE
  viewport hide it because they force the dynamic path. Follow-up measurement by the reporter: `Static` not rendered,
  `Dynamic` renders correctly. Substrate, ray tracing, Lumen and VSM toggles made no difference.
  Source of the branch: `bForceDynamicPath = IsRichView || IsSelected || Wireframe` in
  RealtimeMeshComponentProxy.cpp:184-187. Not yet checked on NVIDIA or on 5.8.3 (UNVERIFIED). Consequence: use
  `Dynamic` for every terrain buffer set and test in a standalone `-game` run, not only PIE.
- **#290, open since 2024-11-22: "Memory leak ... on async rebuild of collisions".** Root-cause comment (2026-08-19): each
  accepted collision update creates a new `UBodySetup` and the retired one's native Chaos geometry stays until GC (default
  `gc.TimeBetweenPurgingPendingKillObjects` 60 s). A workload that rebuilds collision several times a second grows memory
  between GC passes. The commenter's confirmation recipe: `obj gc`, `obj list class=UBodySetup`. This is the main reason
  to avoid physics collision as the cursor path (section 7).
- #302 (open): convex collision `Center` ignored; irrelevant to terrain.
- #299 (closed 2026-08-26): "When can support for UE 5.8 be provided?" is what the master commits answered.
- A 3rd-party change set (PR #300) is still open, but master already carries its own 5.8 guards, so it is not required
  to compile on 5.8.2 (the #303 reporter built master). On 5.8.3 compile status is UNVERIFIED.

### 3.6 Fit and cost summary
Pros: MIT, in-place ranged GPU updates, async worker-thread build, per-LOD sections, explicit RVT support, 8+ years of use.
Cons: not Nanite (Pro only: Docs/rendering/nanite/index.md:14-18 says stock-engine support is 5.5 to 5.8, but that is the
Pro module), 5.8 support is two months old and has an open render bug, one-person vendor risk, per-mesh collision granularity,
no published performance numbers (Docs grep for "benchmark|fps|ms" found none).

## 4. Errant Landscape (Errant Photon)

- What it is: a non-destructive brush/stamp system for the standard Unreal Landscape; since 1.5 (2024-07-27) it has "a
  dedicated module for modifying the Unreal's standard landscape at runtime ... heightmap, weightmaps, and collision",
  "can be used in multiplayer" (documentation.errantphoton.com/landscape/runtime/introduction, /blog/landscape-1-5).
- How runtime works: runtime brush components. `ElRtTextureStampBrush` for tens to hundreds of add/move/destroy brushes
  that revert when destroyed; `ElRtStaticTextureStampBrush` for hundreds to millions of add-only brushes that render into
  a cached "Static Layer" texture; call `Update Landscape`; optional `UElRtWorldSubsystem::QueueRender` for batches
  (/landscape/runtime/spawning). The Static Layer "is blended additively" and cannot overwrite editor-sculpted terrain, so
  use Dynamic brushes with "Replace" (/landscape/runtime/setup).
- Limits from its own docs (/landscape/runtime/introduction, /setup): **incompatible with Nanite**, dedicated servers not
  supported (needs GPU), mobile unsupported, physical materials not updated by runtime paint, foliage not adjusted,
  only non weight-blended weightmaps, weightmap data must be pre-filled to about 0.005 or the cooker strips it, RVT results
  must be refreshed manually, needs landscape components of 255x255 quads with 2x2 sections per component for speed.
- Needs `Enable Edit Layers` on the Landscape actor and an Errant edit layer (/landscape/setup). A Landscape actor has to
  exist in the level at cook time; I found no Epic or Errant statement that a Landscape can be created at runtime, and the
  Epic answer-hub results say it cannot (WebSearch result, 2026-10-01). So a creator-made map of arbitrary size would sit
  on a pre-made maximum-size Landscape (inference, UNVERIFIED with Errant).
- Fit with the game: editing is stamp-based (textured stamps plus Flat, Blur and Noise brushes), not per-vertex. Raise,
  lower, smooth, flatten map onto stamps and the Flat/Blur brushes; ramp could be a spline brush (runtime spline brushes
  arrived in 1.7: /blog/landscape-1-7). Dynamic-brush undo is "destroy the brush". Heights live in Landscape textures, so
  the sim's `ElevationGrid` would need a readback (UNVERIFIED how Errant exposes one).
- Price and licence: **price not disclosed on any page I could fetch.** The pricing page is a calculator (Indie
  "Revenue 0-100,000 USD and funding 0-300,000 USD and 1-5 seats"; Studio above that; source code is a paid add-on for Indie).
  Perpetual licence, 1 year of updates, year-2 renewal 30% off and year-3 50% off for Indie. 5 runtime seats per editor seat,
  needed only for testing in the Editor. "We do not check the license in a packaged game"
  (errantphoton.com/pricing, /license-explained). The Fab page returned 403, so the Fab price is UNVERIFIED.
- **Evaluation without purchase: yes.** "Free 30-day trial" for Biomes, Landscape or Paths via a form and a Trial EULA
  (errantphoton.com home and pricing); trial gives 5 seats (/trial). On expiry the Editor mode is lost and Errant Landscape
  brushes are removed while sculpted landscapes remain (/trial). Whether runtime works in a packaged build during the trial is
  not stated (UNVERIFIED).
- UE 5.8: **no mention** on the blog index, the 1.7 post (2026-06-15) or the installation page, which only promises "the
  latest Unreal Engine version as well as the previous one". 1.6.9 added 5.7 (/blog/landscape-1-6). Treat 5.8 as UNVERIFIED
  until the trial binary installs. Delivered as precompiled per-engine downloads from a licence page, or as source if bought.

## 5. Built-in 5.8 options, verified in D:/Epic Games/UE_5.8

Paths below are under `Engine/` unless stated.

### 5.1 UDynamicMeshComponent (GeometryFramework)
- Class `UDynamicMeshComponent : UBaseDynamicMeshComponent, IInterface_CollisionDataProvider`
  (Source/Runtime/GeometryFramework/Public/Components/DynamicMeshComponent.h:171). Lives in `Source/Runtime/`, so it ships
  in packaged games; the module has no editor-only guard around the class (editor deps are only for outliner filters:
  GeometryFramework.Build.cs:12-27). Geometry Script, which wraps it for runtime use, is a non-experimental runtime plugin
  (Plugins/Runtime/GeometryScripting/GeometryScripting.uplugin:14-20).
- Edit API: `EditMesh(Func, UpdateMode)` with `NoUpdate / FullUpdate / FastUpdate` (DynamicMeshComponent.h:46-54, :220-221);
  `FastNotifyVerticesUpdated`, `FastNotifyTriangleVerticesUpdated` (:287-329). With a `FMeshRenderDecomposition` set
  (`SetExternalDecomposition`, :555) only the affected render-buffer groups are rebuilt (:540-556;
  MeshRenderDecomposition.h:19-36). That gives chunk-style partial GPU updates inside one component.
- Collision: `CollisionType`, `bEnableComplexCollision`, `bUseAsyncCooking` (default **false**), `bDeferCollisionUpdates`,
  `UpdateCollision(bOnlyIfPending)` (:714-784). `RebuildPhysicsData` creates a new body setup, aborts earlier in-flight async
  cooks, and cooks with `CreatePhysicsMeshesAsync` (Private/Components/DynamicMeshComponent.cpp:1598-1658). It re-cooks the
  whole component, so the same per-chunk-component rule applies. `SetTransientDeferCollisionUpdates` (:800) fits "defer until
  the stroke ends".
- RVT writes are supported (Private/Components/BaseDynamicMeshSceneProxy.cpp:73, :488-498).
- No Nanite references anywhere in the module (grep of Public and Private returned nothing).
- Cost: it is a general `FDynamicMesh3` (double positions, edges, overlays). I estimate 100 MB or more for a 410k-vertex
  grid (estimate, not measured) and per-edit overhead is higher than RMC's raw streams. Strength: no third-party risk.

### 5.2 UProceduralMeshComponent
- Plugin `Plugins/Runtime/ProceduralMeshComponent`, `EnabledByDefault: true`, Runtime module
  (ProceduralMeshComponent.uplugin:12-20). `UpdateMeshSection` replaces whole arrays of `FVector`, "faster than
  CreateMeshSection, but does not let you change topology. Collision info is also updated"
  (Source/ProceduralMeshComponent/Public/ProceduralMeshComponent.h:200-215). `bUseAsyncCooking` and
  `bUseComplexAsSimpleCollision` are the collision switches (:279, :285). No partial GPU upload, no LOD.
  Usable but strictly weaker than RMC for per-stroke updates. Not recommended.

### 5.3 5.8 "Mesh Terrain" (Mesh Partition)
- Experimental: `IsExperimentalVersion: true` for both `MeshPartition` (Plugins/Experimental/MeshPartition/MeshPartition.uplugin:15)
  and `MeshTerrainMode` (MeshTerrainMode.uplugin:16). Epic's doc says "Learn to use this feature, but use caution when shipping with it".
- **Editor-only authoring.** `MeshTerrainMode` is a single `Editor` module (MeshTerrainMode.uplugin:19-22; "a suite of
  interactive tools for creating and editing Mesh Partitions in the Editor", :6). `MeshPartitionEditor`,
  `MeshPartitionModelingToolset` and `MeshPartitionEditorUI` are `Editor` modules; only `MeshPartition` and
  `MeshPartitionCompute` are `Runtime` (MeshPartition.uplugin:18-42).
- The runtime side consumes baked output: transformers produce `UMeshPartitionStaticMeshComponent : UStaticMeshComponent`
  and cooked `FTriMeshCollisionData` (MeshPartitionStaticMeshComponent.h:16-26; MeshPartitionCollisionComponent.h:25-35;
  MeshPartitionEditor/Public/MeshPartitionStaticMeshTransformer.h:171-184). Epic's doc: "Compiled sections are for runtime
  only ... Preview sections are for the editor only".
- Epic forum thread "Does 5.8 Mesh Terrain support creation of a terrain at runtime in a packaged build?" answered "no"
  by community members, 2026-06-21 and 2026-07-21, no Epic staff reply
  (forums.unrealengine.com/t/.../2730244). Verdict: it does not help runtime editing. Also experimental.

### 5.4 Why stock Landscape can't be edited in a shipped game
- The edit interface is compiled out of non-editor builds: `FLandscapeEditDataInterface` (LandscapeEdit.h:125) sits inside
  `#if WITH_EDITOR` that runs from line 34 to line 588. The edit-layer merge contexts are also `WITH_EDITOR`
  (LandscapeEditLayerMergeContext.h:27-244).
- Collision refresh is editor-only too: `UpdateHeightfieldRegion` and `CookCollisionData` are inside `#if WITH_EDITOR`
  (Source/Runtime/Landscape/Classes/LandscapeHeightfieldCollisionComponent.h:240-263). A packaged game loads cooked
  heightfield bytes. The one exported runtime piece is `WriteRuntimeData` (:295); how Errant uses the surrounding runtime
  surface is UNVERIFIED.
- The heightmap texture itself exists at runtime (LandscapeComponent.h:572), which is why a plugin can write to it.
- The new Landscape Patch plugin is also editor-category (Plugins/Editor/LandscapePatch/LandscapePatch.uplugin:6).

### 5.5 Virtual Heightfield Mesh (VHM)
- Plugin `Plugins/Experimental/VirtualHeightfieldMesh`, experimental (uplugin:15). It renders a LOD-adaptive mesh displaced
  by a runtime virtual texture height channel and hides the Collision category in its details
  (VirtualHeightfieldMeshComponent.h:17), so **no collision**. Its min/max build helper `FHeightfieldMinMaxTextureBuildDesc`
  is `WITH_EDITOR` (HeightfieldMinMaxTexture.h:11-20), but the GPU helpers `DownsampleMinMaxAndCopy` and
  `GenerateMinMaxTextureMips` are exported (HeightfieldMinMaxRender.h:17-20), so a runtime rebuild is possible but custom
  work (UNVERIFIED that it is practical).
- The 3rd-party MIT plugin TerraDyne (github.com/gregorik/TerraDyne, v0.4, 2026-09-11, 41 stars) is built on VHM plus
  GeometryScripting and says it targets UE 5.7 and Win64 only (README). Interesting as a reference, not a candidate: it is
  Landscape-import oriented and not stated for 5.8. A Fab "Pro" version exists; price UNVERIFIED.

### 5.6 Other
- `UStaticMesh::BuildFromMeshDescriptions` exists at runtime (Source/Runtime/Engine/Classes/Engine/StaticMesh.h:1891) but a
  full static-mesh rebuild per stroke is the wrong tool; not investigated further.

## 6. Runtime texture painting and a photoreal ground

### 6.1 Splat map update paths
- **A. CPU array to `UTexture2D::UpdateTextureRegions`** (Source/Runtime/Engine/Classes/Engine/Texture2D.h:298). Verified
  constraints in Private/Texture2D.cpp:1438-1449: it returns early for a virtual-textured texture, and it logs and does
  nothing for a streamable texture unless streaming is disabled. So create the splat as a transient, non-streaming,
  non-VT texture; send only the dirty rectangle; keep the source buffer alive until the cleanup callback runs. Authoritative
  data stays a CPU array that serializes straight into the scenario zip.
- **B. GPU: draw a brush material into a `UTextureRenderTarget2D`** with `UKismetRenderingLibrary::DrawMaterialToRenderTarget`
  (Classes/Kismet/KismetRenderingLibrary.h:82) or the canvas variants (:223). Cheaper per stroke, but save needs an async
  readback. Not recommended for the trial; revisit if (A) costs measurable ms.
- Layer count: the existing tool has 4 layers (TerrainBrush.cs:26). One RGBA8 splat texture covers it; two cover 8.

### 6.2 Photoreal blending recipe (material design, no engine claims beyond the cited docs)
- Weighted blend of N layers using the splat weights, plus **height blend**: use each layer's height value to bias the
  weights so dirt collects between rocks. Epic's Landscape doc names the same technique, "LB Height Blend", and the two
  blend modes (weight vs alpha) (dev.epicgames.com/.../landscape-materials-in-unreal-engine).
- Put albedo, normal and ARM(H) of each layer in **Texture2DArrays** so sampler count does not grow with layers (the 16
  sampler hard limit applies to mobile ES3.1 only, same doc). World-aligned UVs; two tiling scales plus a low-frequency
  macro-variation mask to break repetition; triplanar projection only on steep slopes.
- Per-pixel cost for 4 layers at two scales is roughly 24 array samples plus the splat. That is the case for an RVT cache.
- **RVT compatibility:** both candidate proxies write RVT (RMC: RealtimeMeshComponentProxy.cpp:198-281; DynamicMesh:
  BaseDynamicMeshSceneProxy.cpp:73, :488-498). RVT is a shading cache that "isn't fully updated on every frame", invalidates on
  material-parameter or component changes rather than tracking deformation, and cannot sample virtual textures while
  filling itself (Epic RVT doc, 5.8 page). So after each stroke call
  `URuntimeVirtualTextureComponent::Invalidate(Bounds)` (Classes/Components/RuntimeVirtualTextureComponent.h:165) over the
  edited rectangle; do not rely on automatic invalidation. Whether RVT is needed at all for a 640 m map at an RTS camera is
  a measurement (section 9, M6).
- Nanite displacement is not available to RMC Core or `UDynamicMeshComponent`, so close-up relief comes from normal maps, a
  height-based parallax/POM in the material and mesh density, not tessellation. RMC Pro's runtime Nanite builder is the only
  route to Nanite here (Docs/rendering/nanite/index.md:8-20) and is UNVERIFIED for terrain edit rates.

## 7. Picking and placement: do not make physics the primary path

- The sim already defines the ground as a clamped integer-cell height lookup (ElevationGrid.cs:59). Editor cursor picking
  can ray-march the same array (a DDA through the cell grid is microseconds) and unit placement can read the same array.
  That needs no Chaos body, no cook, no GC churn from #290, and it cannot disagree with what the sim will use.
- Keep complex collision as a secondary path (async, `bUseAsyncCook` true, navigation updates off, one component per chunk,
  collision rebuild deferred to stroke end) only for things that need real line traces against props and buildings.
- Visual smoothness caveat for the plan, not for this trial: the sim samples nearest-cell, a rendered mesh is bilinear, so
  a unit's presentation height from `Elevation[]` can differ by up to half a cell on slopes. Presentation should bilinear-sample
  its own copy of the array (presentation only, never feeds the sim).

## 8. Performance numbers and community reports

| Option | What exists | Quality |
|---|---|---|
| RMC | No official benchmarks in the repo docs (grep found none). A web-search summary of the older RuntimeMeshComponent v4 repos says it "can update a 600k+ vertex mesh in real time"; I did not open a source for that figure. The v5 examples include a `HighPoly` stress example and `FastUpdate` (RealtimeMeshExample_Simple_HighPoly.cpp:10-11) | Anecdote only; treat as UNVERIFIED |
| RMC collision | #290 root cause: retired `UBodySetup` memory is held until GC (default 60 s purge) | Credible reading of source, one reporter, open |
| RMC on 5.8 | #303: static draw path not rendering on 5.8.2 / AMD iGPU; `Dynamic` renders | One reporter, reproduces with bundled examples; not seen on NVIDIA |
| UDynamicMeshComponent | Engine ships partial-update and async-cook paths; no published numbers found | Source only |
| Errant | Claims "thousands of brushes on a single map", "real-time landscape editing on even the largest of maps", "GPU accelerated, much faster than the Landmass System". No numbers | Vendor marketing |
| Errant limits | Runtime weightmap shading needs complex shader variants; RVT recommended | Vendor docs |
| 5.8 engine | Tom Looman's 5.8 summary: no terrain numbers; Lumen Lite is "twice as fast as Lumen high" (irrelevant, Lumen is off) | Third-party summary |
| TerraDyne | No numbers in README | none |

No option has a published number for "edit a 640x640 terrain at 60 fps on an RTX 3060". That is exactly what the trial
has to produce.

## 9. Ranked recommendation, what to measure, purchase decisions

### 9.1 Ranking for the trial
1. **RMC Core (master `b8669a0`), chunk grid of `URealtimeMeshComponent`, Dynamic buffer sets, analytic ray pick, splat via
   `UpdateTextureRegions`, materials as Texture2DArray blend.** Fastest route to a pass; closest shipped example; cheapest
   to change if it fails.
2. **`UDynamicMeshComponent` with a `FMeshRenderDecomposition` or one component per chunk.** Run only if RMC fails gate G1
   or G2 below. Zero vendor risk.
3. **Errant Landscape (30-day free trial).** Run only if both fail, or if Alec decides Landscape's World Partition, Nanite
   landscape and tooling matter more than owning the data. Biggest unknowns: 5.8 binary, packaged-build behaviour of the
   trial, readback to the sim, pre-made landscape size limit.
4. Not worth trial time: Mesh Terrain (editor-only, verified), stock Landscape (editor-only, verified), VHM (no collision,
   experimental, custom MinMax rebuild), ProceduralMeshComponent (superseded by 1 and 2).

### 9.2 Gates and measurements (pass bars are proposals for Alec to adjust)
| # | Measure | How | Pass |
|---|---|---|---|
| G1 | RMC compiles on 5.8.3 and a `Dynamic` and a `Static` HelloTriangle both render in standalone `-game` with Substrate on, on the RTX 3060 | Build.bat, run `-game` | Dynamic renders. Record whether Static does (#303) |
| G2 | Same in a packaged build (Development, then Shipping), examples module excluded | Package, run | Terrain visible, no cook or shipping compile errors |
| M1 | Frame cost of a 640x640 terrain, 32x32-cell chunks (400 components) and 64x64 (100), static camera at the RTS pose | `stat unit`, `ProfileGPU`, Unreal Insights | Chunk size picked with terrain alone at or under 4 ms GPU and 2 ms game+render thread (proposal) |
| M2 | Edit latency: input event to changed pixel, for a 32-cell brush, via `EditMeshInPlaceRanged`; vertices and bytes uploaded per stroke | Insights markers | Under 2 frames at 60 fps; no frame over 16.6 ms during strokes |
| M3 | VSM cost with Dynamic-path chunks vs Static: shadow page invalidation per edit, steady-state cost when idle | `stat Unit`, VSM visualisation | Idle cost not worse than 1 ms over Static; if worse, test switching idle chunks to Static |
| M4 | Collision: async cook time per chunk, and memory growth over 5 minutes of continuous sculpting with collision on; run `obj list class=UBodySetup` before and after `obj gc` (the #290 recipe) | Console, task manager, Insights memory | Growth bounded and reclaimed by GC; otherwise keep collision off and use analytic pick only |
| M5 | Full scene: terrain + 1,000 instanced units + 4-layer material at 1080p, Lumen off | `stat unit`, 60 s capture | 60 fps average, 1% low above 45 (proposal) |
| M6 | Splat painting: `UpdateTextureRegions` cost per stroke; material cost direct vs RVT; RVT `Invalidate` cost per stroke | Insights, `ProfileGPU` | Paint stroke under 1 ms CPU; choose direct or RVT by measured GPU ms |
| M7 | Round trip: sculpt, paint, serialize to bytes, reload, compare a hash of the height and splat arrays; feed the height array into the sim's `ElevationGrid` and confirm the grid hash equals the Godot grid hash for the same map | Test in C++ and sim tests | Byte-identical arrays and the same grid hash |
| M8 | Brush set: raise, lower, smooth, flatten, ramp (new) implemented on the array, with undo by stroke snapshot | Manual plus a test on the array code | All five work, undo restores the exact array |
| M9 (only if Errant is trialled) | Plugin installs on 5.8.3, runtime brushes change heightmap, weightmap and collision in a **packaged** build, readback to the sim, edit latency | Trial request form | Same pass bars as M2, M7 |

### 9.3 Decisions that are Alec's
- **Nothing to buy for the trial.** RMC Core is free and MIT; Errant offers a free 30-day trial.
- If RMC passes: optional RMC Pro ($49.99, UNVERIFIED on the page) only if Nanite terrain is wanted later. I do not recommend
  buying it before M5 shows the geometry cost is the problem.
- If Errant becomes the choice: per-seat perpetual licence, 1 year of updates, Indie cap 1-5 seats with revenue under
  100,000 USD and funding under 300,000 USD (above that Studio pricing), source code a separate add-on. The price is
  unknown to me; Alec would have to read it from the calculator or Fab. Ask for 5.8 written support before paying. Also a
  strategic call: maps would depend on a closed third-party plugin for the core creator feature.
- Whether to accept a one-maintainer MIT plugin as a foundation for the map editor, or fall back to the in-engine
  `UDynamicMeshComponent` even at higher per-edit cost. My recommendation: start RMC, keep the terrain data model and
  brush code independent of the renderer (plain arrays plus a thin "chunk renderer" interface), so swapping renderers is a
  small change.

## 10. Risks
1. RMC 5.8 support is new (master, 2026-08-21 to 08-23), untagged, and #303 is open and unreproduced on NVIDIA.
2. #290: retired physics bodies pile up between GCs; any design that cooks collision per stroke will hit it.
3. Dynamic draw path may cost more in VSM than static; unknown until M3.
4. Per-chunk components multiply draw calls in every shadow pass; chunk size is a real trade-off (M1).
5. LOD seams between chunk resolutions are our work in both RMC and DynamicMesh.
6. Errant: Nanite incompatibility, unknown 5.8 build, Landscape needs a pre-existing actor, price unknown, vendor lock.
7. Splat texture updates silently do nothing if the texture is streaming or virtual (Texture2D.cpp:1440-1449).
8. Machine limits: 16 GB RAM with the editor idling at 4.2 GB (HANDOFF.md); a 100 MB class dynamic mesh matters less than
   Unreal DDC and shader compiles, but keep an eye on it.
9. The trial project is not a git repo; a vendored plugin must be pinned by commit hash in the trial notes.
10. Epic could change or add runtime Mesh Terrain; spec phrasing should say "as of 5.8.3".

## 11. Open questions
1. Does `b8669a0` compile cleanly on 5.8.3, and does the #303 static-path bug reproduce on NVIDIA/DX12 with Substrate on?
2. Does Errant Landscape run on 5.8.3 today, and does its free trial work in a packaged build?
3. How does Errant expose heights to the CPU, and can a Landscape of arbitrary player-chosen size be created at runtime?
4. What is the Errant price at Fab or on the calculator for 1 seat with source?
5. What chunk size minimises total cost (M1) and do we need terrain LODs at the RTS camera at all?
6. Is physics collision needed anywhere besides cursor picking and placement (for example prop line traces)? If no, collision
   can be dropped entirely, which removes #290 from the picture.
7. Should presentation height be bilinear while the sim stays nearest-cell (section 7)?
8. How is the 128x128 impassable bitset drawn on the terrain (a decal or a second splat channel)? Not researched here.

## 12. Sources
Local files (read 2026-10-01):
- D:/Epic Games/UE_5.8/Engine/Build/Build.version
- UE_5.8/Engine/Source/Runtime/GeometryFramework/Public/Components/DynamicMeshComponent.h, MeshRenderDecomposition.h; Private/Components/DynamicMeshComponent.cpp; Private/Components/BaseDynamicMeshSceneProxy.cpp; GeometryFramework.Build.cs
- UE_5.8/Engine/Plugins/Runtime/ProceduralMeshComponent/ProceduralMeshComponent.uplugin and Source/ProceduralMeshComponent/Public/ProceduralMeshComponent.h
- UE_5.8/Engine/Plugins/Runtime/GeometryScripting/GeometryScripting.uplugin
- UE_5.8/Engine/Plugins/Experimental/MeshPartition/ (uplugin and Source/MeshPartition/Public/*.h, MeshPartitionEditor/Public/MeshPartitionStaticMeshTransformer.h)
- UE_5.8/Engine/Plugins/Experimental/MeshTerrainMode/MeshTerrainMode.uplugin
- UE_5.8/Engine/Plugins/Experimental/VirtualHeightfieldMesh/ (uplugin and Source/VirtualHeightfieldMesh/Public/*.h)
- UE_5.8/Engine/Plugins/Editor/LandscapePatch/LandscapePatch.uplugin
- UE_5.8/Engine/Source/Runtime/Landscape/Public/LandscapeEdit.h, Classes/LandscapeHeightfieldCollisionComponent.h, Classes/LandscapeComponent.h
- UE_5.8/Engine/Source/Runtime/Engine/Classes/Engine/Texture2D.h, Private/Texture2D.cpp, Classes/Kismet/KismetRenderingLibrary.h, Classes/Components/RuntimeVirtualTextureComponent.h, Classes/Engine/StaticMesh.h
- UE_5.8/Engine/Source/Runtime/Core/Public/Templates/ChooseClass.h
- RMC clone (scratchpad, `git clone https://github.com/TriAxis-Games/RealtimeMeshComponent`, head b8669a0): README.md, RealtimeMeshComponent.uplugin, Docs/component-core/{collision,sections,updating-mesh-data,lods,structure}/index.md, Docs/rendering/nanite/index.md, Source/RealtimeMeshComponent/{Public,Private}/..., Source/RealtimeMeshExamples/Private/Simple/*
- Repo: godot/src/Core/ElevationGrid.cs, Core/Bootstrap/Phases/TerrainPhase.cs, CreationSuite/TerrainBrush.cs, Core/Definitions/ScenarioData.cs; HANDOFF.md; spec export unreal_spec.txt; Chimera-Unreal ProjectChimera.uproject, ProjectChimera.Build.cs, Config/DefaultEngine.ini

Web (fetched 2026-10-01):
- https://github.com/TriAxis-Games/RealtimeMeshComponent (and the GitHub API: /repos, /releases, /tags, /branches, /commits, /issues, /search/issues)
- https://github.com/TriAxis-Games/RealtimeMeshComponent/issues/303, /290, /299, /300, /302
- https://github.com/TriAxis-Games/RealtimeMeshComponent/releases
- https://triaxis.games/realtime-mesh/ (named in README; docs read from the repo copy)
- https://www.fab.com/listings/c04e6977-a3bd-4e24-b0a2-0b696bee90da (Pro price from search snippet only; page not fetchable)
- https://www.errantphoton.com/ and https://www.errantphoton.com/pricing
- https://documentation.errantphoton.com/landscape/runtime/introduction/, /landscape/runtime/setup, /landscape/runtime/spawning, /landscape/runtime/multiplayer, /landscape/runtime/examples, /landscape/setup, /landscape/introduction, /license-explained, /trial, /installation, /blog/, /blog/landscape-1-5, /blog/landscape-1-6, /blog/landscape-1-7
- https://www.fab.com/listings/1cf61011-822d-49a0-a819-62603258b23e (Errant Landscape; returned 403, existence only)
- https://dev.epicgames.com/documentation/unreal-engine/unreal-engine-5-8-release-notes
- https://dev.epicgames.com/documentation/unreal-engine/mesh-terrain-in-unreal-engine
- https://dev.epicgames.com/documentation/unreal-engine/runtime-virtual-texturing-in-unreal-engine
- https://dev.epicgames.com/documentation/unreal-engine/landscape-materials-in-unreal-engine
- https://forums.unrealengine.com/t/does-5-8-mesh-terrain-support-creation-of-a-terrain-at-runtime-in-a-packaged-build/2730244
- https://www.strayspark.studio/blog/mesh-terrain-ue5-8-caves-overhangs-guide
- https://tomlooman.com/unreal-engine-5-8-performance-highlights/
- https://github.com/gregorik/TerraDyne and https://www.fab.com/listings/20f72d71-9737-4ac1-bce5-034eca166529 (TerraDyne; Fab page not fetchable)
- Web search results used for "runtime Landscape creation not supported natively": answers.unrealengine.com/questions/401283/view.html (redirects, not read directly; UNVERIFIED beyond the search summary)
