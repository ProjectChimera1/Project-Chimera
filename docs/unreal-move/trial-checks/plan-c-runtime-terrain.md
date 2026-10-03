# Plan C: runtime terrain editing in Unreal (trial check c)

Date 2026-10-01; revised the same day after the API and proof critiques (Review log at the end). Inputs: R6
(`research/r6-runtime-terrain-options.md`), R7 (`research/r7-godot-terrain-as-built.md`), R3 (`research/r3-ue-host-and-instancing.md`),
the Unreal Spec export (Non-negotiables, "How it is built"), `HANDOFF.md`. Every load-bearing claim was checked against the engine
source (`E` = `D:/Epic Games/UE_5.8/Engine`, 5.8.3), the RMC clone at `b8669a0` (scratchpad `rmc/`, `RMC` below; paths under
`Source/RealtimeMeshComponent/`) or the repo (`G` = `D:/Projects/Project_Chimera/godot`). UNVERIFIED marks what only a build or run settles.

**Shell rule.** Every command here runs from **Git Bash** (the Bash tool). From PowerShell, `bash` is the WSL launcher on this PC
(`Get-Command bash -All` → `WindowsApps\bash.exe`, Ubuntu-24.04 installed; checked 2026-10-01), so a PowerShell caller must use
`"C:/Program Files/Git/bin/bash.exe"`. Shorthand: `LOCK` = `bash D:/Projects/Chimera-Unreal/ue_lock.sh`; `PS` = `powershell -NoProfile
-ExecutionPolicy Bypass -File` (Windows PowerShell 5.1: scripts must be 5.1-compatible and write text and JSON as UTF-8 explicitly);
`BG` = start with the Bash tool's `run_in_background` (a foreground call is killed at 600 s); `T` = `D:/Projects/Chimera-Unreal/ChimeraTerrain`,
`RT` = `D:/Projects/Project_Chimera/tools/unreal-terrain`, `PROOF` = `D:/Projects/Project_Chimera/docs/unreal-move/trial-checks/c-terrain`.
Log greps and count checks live in `parse_terrain.py`, never in `grep -c` (it exits 1 on the passing value 0).

## 1. Decision

**Terrain technology for the trial: RealtimeMeshComponent (RMC) Core, MIT, GitHub master pinned at `b8669a0`** (2026-08-23; the
`v5.3.2` tag has no 5.8 guards, R6 §3.1), as a grid of one chunk component (a `URealtimeMeshComponent` subclass, §3.3) per chunk, with
**Dynamic** buffer sets. Heights and paint are plain CPU arrays the game owns. Nothing is bought.

- Why: the closest shipped example is a Dynamic height grid edited in place (`RMC/../RealtimeMeshExamples/Private/Simple/RealtimeMeshExample_Simple_FastUpdate.cpp:96-140`),
  the ranged GPU upload fits sculpting (`Public/RealtimeMeshSimple.h:164-170`), and it has an explicit collision API. RVT is **not**
  available on the Dynamic path (§2.8). The data model stays engine-neutral behind `ITerrainChunkRenderer`, so a swap costs one class.
- Fallback 1 (task CF): `UDynamicMeshComponent`, in-engine and runtime (`E/Source/Runtime/GeometryFramework/Public/Components/DynamicMeshComponent.h:40-54,220-223,287`).
- Fallback 2: Errant Landscape's runtime module (free 30-day trial; price undisclosed, UNVERIFIED; no stated 5.8 support; no Nanite;
  R6 §4). Only if RMC and CF both fail. The trial form and any purchase are Alec's.
- Not candidates: stock Landscape and 5.8 Mesh Terrain (editor-only authoring, R6 §5.3-5.4), VHM (no collision), ProceduralMeshComponent (no partial upload).

## 2. Corrections and facts found while verifying

1. **Map size.** The Frontier is ±160 m visual (`map_bounds` 128 + `border_extent` 32; `G/src/Core/Bootstrap/Phases/TerrainPhase.cs:32-36`),
   not R6's ±320. The sim grid stops at ±128 (`G/src/Core/Definitions/MapSize.cs:56`). Trial terrain ±160 m; ±320 m is headroom evidence only.
2. **Collision.** RMC re-cooks on any Position/Triangles/TexCoords edit of a collision-enabled section (`Private/RealtimeMeshManaged.cpp:57-80`),
   which would hit #290 (a new `UBodySetup` per update, `Private/RealtimeMesh.cpp:377`) every tick. Auto-created sections have no collision (`:24-27`).
   **Custom complex geometry alone is thrown away in `b8669a0`:** `FRealtimeMeshManaged::GenerateComplexCollision` copies it (`:464`) but returns
   LOD0's result (`:469`), which is false when no section has collision (`:225-239`, `:245-258`); the caller keeps the geometry only on true
   (`:719-722`); `ApplyCollisionUpdate` then builds a body with no trimesh and still returns `Updated` (`RealtimeMesh.cpp:373-421`). Fix in §3.6.
3. A ranged in-place edit copies the **whole** stream on the game thread before the sub-range upload (`Private/RealtimeMeshSimple.cpp:79-88`): small chunks; size measured in C12.
4. **Bounds.** `FastUpdateStream` flags Position bounds dirty (`Private/Data/RealtimeMeshBufferSet.cpp:226-230`), but nothing reaches the
   component: the in-place path broadcasts only on `FellBackPublished` (`Private/RenderProxy/RealtimeMeshProxyCommandBatch.cpp:139-154`),
   `OnBoundsChanged` is never broadcast by the core path (`Private/RealtimeMeshComponent.cpp:369-378` comment; grep finds no broadcaster),
   and `CalcBounds` is cached (`:149-165`). Scene bounds move only via `SendRenderTransform_Concurrent` → `UpdatePrimitiveTransform`
   (`E/Source/Runtime/Engine/Private/Components/PrimitiveComponent.cpp:655-664`). So the chunk component owns its bounds (§3.3).
5. R7's ElevationGrid line numbers are stale: class `:20`, `Sample` `:59-74` (clamped nearest-cell, floor via `ToInt()` `:66-67`).
6. Capture without screen recording: `-DUMPMOVIE` sets `GIsDumpingMovie` (`LaunchEngineLoop.cpp:4303-4307`, writable `CoreGlobals.h:417`,
   frame count `UnrealClient.cpp:281-289`). Movie frames exclude UI (`GameViewportClient.cpp:2281-2286`), so the brush ring lives in the material.
7. No photographic PBR ground set ships with the engine. Poly Haven's API serves CC0 sets (`api.polyhaven.com/files/<id>`: `Diffuse`, `nor_dx`,
   `arm`, `Displacement`, md5s; licence CC0, polyhaven.com/license; both fetched 2026-10-01).
8. **RVT needs the Static path.** RMC emits RVT batches only in `DrawStaticElements` (`Private/RenderProxy/RealtimeMeshComponentProxy.cpp:226-290`);
   dynamic batches set `bRenderToVirtualTexture = false` (`RealtimeMeshBufferSetProxy.cpp:301`) and a Dynamic set raises only `DrawDynamic` (`:360`).
9. **VSM.** A Movable primitive is cached as dynamic (`E/Source/Runtime/Engine/Public/PrimitiveSceneProxy.h:771-774`;
   `E/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapCacheManager.cpp:578-586`), so its shadow is re-rendered rather than
   kept stale; that is a cost (risk 3). Chunks are Movable. That this fully covers edited shapes is UNVERIFIED; G1's hill-shadow check proves it.

## 3. Design
Scatter (grass, flowers, shrubs, trees, rocks) is designed in `plan-c-scatter.md` (Alec approved 2026-10-01); it is presentation only and builds on §3.2-3.8.


### 3.1 Project `ChimeraTerrain` (sibling, same Blank C++ shape as ProjectChimera)
- `ChimeraTerrain.uproject`: `EngineAssociation` GUID copied exactly (R3 risk R13). Module `ChimeraTerrain` (Runtime, Default). Plugins:
  `RealtimeMeshComponent`; `PythonScriptPlugin` and `EditorScriptingUtilities` with `TargetAllowList ["Editor"]` (material script only).
- `Plugins/RealtimeMeshComponent/` = git clone at `b8669a0` with two recorded patches in `VENDOR.md`: (1) `"TargetDenyList": ["Game"]` on
  `RealtimeMeshExamples` (`ModuleDescriptor.cs:204,370`); (2) the collision return fix (§3.6).
- Build.cs deps: `Core CoreUObject Engine InputCore RenderCore RHI Slate SlateCore Json JsonUtilities RealtimeMeshComponent GeometryFramework GeometryCore`
  (the last two are for CF, cheap to carry from the start).
- `DefaultEngine.ini`: ProjectChimera's renderer block (Substrate, VSM, DX12 SM6, RT off) **but** `r.DynamicGlobalIlluminationMethod=2` and
  `r.ReflectionMethod=2` (ScreenSpace, as LT_A_noLumen measured; ProjectChimera's ini has Lumen = 1, `Config/DefaultEngine.ini:6-14`).
  `r.GenerateMeshDistanceFields=True` stays, matching the A_noLumen measurement. `[/Script/EngineSettings.GameMapsSettings]`:
  `EditorStartupMap=/Engine/Maps/Entry` (the default is the OpenWorld template, `E/Config/BaseEngine.ini:14`), `GameDefaultMap=/Engine/Maps/Entry`,
  `GlobalDefaultGameMode=/Script/ChimeraTerrain.ChimeraTerrainGameMode`.
- `DefaultGame.ini`: new ProjectID; `[/Script/UnrealEd.ProjectPackagingSettings]` (section name per `E/Config/BaseGame.ini:87`) with
  `+DirectoriesToAlwaysCook=(Path="/Game/Terrain")` and `+DirectoriesToAlwaysCook=(Path="/Engine/EngineSky/VolumetricClouds")`
  (`ProjectPackagingSettings.h:564-568`; the cloud material is referenced only by a C++ string path, `tools/unreal-looktest/lt_common.py:40`). No `.umap` of ours.
- Module `Source/ChimeraTerrain/`: `Data/` (pure C++, no UObjects: heightfield, brush, undo, pick, IO, sim export), `Render/`
  (`ITerrainChunkRenderer`, `UTerrainChunkComponent`, `FRmcTerrainRenderer`, splat texture), `Game/` (game mode, terrain actor, player controller,
  RTS camera pawn, HUD, unit layer), `Test/` (script director, metrics), `Tests/` (automation). All gameplay and UI is C++; no Blueprints.
- `Tools/`: `build.ps1`, `run_tests.ps1`, `run_terrain.ps1`, `run_commandlet.ps1`, `package.ps1`, `parse_terrain.py`, `imgdiff.py`,
  `composite.py`, `fetch_textures.py`, `inject_mouse.py`, `make_video.sh`, `sync_to_repo.sh`. `Scripts/`: brush scripts (`*.json`),
  `make_ground_material.py`, `ChimeraGround.hlsl`. All text is mirrored to `RT`.

### 3.2 Heightfield data model, matched to the Godot format
| Item | Godot as built | Unreal trial |
|---|---|---|
| Spacing | 1 m texel, Terrain3D `vertex_spacing 1` (R7 §1, §4.2) | 1 m vertex grid |
| Extent | 256 m regions; playable ±128, visual ±(128+border) | one global grid centred on the origin, half-extent `E` = 128 + border; trial `E` = 160 → 321x321 vertices |
| Heights | float32 m, unbounded | float32 m, clamped to ±128 m (trial choice) |
| Paint | 4 fixed layers 0 Grass, 1 Dirt, 2 Rock, 3 Snow | RGBA8 weights (sum exactly 255), same ids, 0.5 m texels (640² for E=160) |
| Sim cell (c,r) | texel `CellToTexel(c,256,256)` at the texel centre, `Fixed.FromFloat` = `(int)(h*65536)` (`ScenarioLoadPhase.cs:252-270`, `HeightmapCellMapping.cs:40-48`, `FixedPoint.cs:27`) | vertex `(CellToTexel(c)+B, CellToTexel(r)+B)`, `B = E-128`, `(int32)(h*65536.0f)` (exact ×2^16, truncation toward zero) |
| Files | `terrain3d_XX_YY.res` | `terrain.json` (dims, origin, spacing, layer ids, `axes`, fnvs, `sim_grid_fnv`, probes) + `height.r32` (LE float32 row-major) + `splat.rgba8` |

No legacy content exists (every shipped map is flat, R7 §6). Axes: one function `SimToUe(x,z,h)` = `(100x, 100z, 100h)` cm, recorded
as `terrain.json` `axes`; it **must** match check (a)'s host mapping (R3 §3.3 says that host applies the Godot-to-UE mapping itself).

### 3.3 Chunked mesh, chunk component, normals, updates
- Chunks are `C` quads square (C = 64 default, 32 measured): 25 or 100 components for E = 160; each owns (C+1)² vertices, borders duplicated
  from the global array (bit-equal seams). Triangles 320²·2 = **204,800**. Builder `TRealtimeMeshBuilderLocal<uint32, FPackedNormal, FVector2f, 1>`
  (the FastUpdate example's `FVector2DHalf`, `:104`, would put the splat 0.3 texel off near u = 1). Streams: Position (FVector3f), Tangents,
  TexCoord0 (global XY, written once, never edited), Triangles. Winding and BL-TR diagonal copy the FastUpdate example (`:115-129`).
- Normals: global central differences `n = normalize(-(h[x+1]-h[x-1])/2, -(h[y+1]-h[y-1])/2, 1)`, clamped one-sided differences on the map border;
  tangent `normalize(1, 0, dh/dx)`.
- Per stroke tick: dirty rect +1 vertex for normals; per touched chunk `EditMeshInPlaceRanged` on Position + Tangents over rows
  `[r0*(C+1), (r1+1)*(C+1))`. Buffer sets use `FRealtimeMeshBufferSetConfig(ERealtimeMeshSectionDrawType::Dynamic)` (default is Static,
  `RealtimeMeshBufferSetConfig.h:14-18,35`; Static is invisible in standalone on 5.8.2, #303; branch `RealtimeMeshComponentProxy.cpp:184-187`).
- **`UTerrainChunkComponent : URealtimeMeshComponent`** (`RealtimeMeshComponent.h:15`, `CalcBounds`/`CreateSceneProxy` virtual at `:127,137`):
  Mobility Movable (§2.9). `CalcBounds` returns the chunk's XY footprint × [zmin − 2 m, zmax + 2 m] from the CPU array. When a tick moves a
  vertex outside that range, the range widens with 16 m headroom and `MarkRenderTransformDirty()` (`ActorComponent.h:1134`) pushes it to
  the scene; stroke end, undo and redo recompute it exactly and push again. `CreateSceneProxy` is wrapped to count proxy recreations
  (each after creation is an RMC fallback publish); `OnCreatePhysicsState` (`ActorComponent.h:871`) is wrapped to time the game-thread collision apply.

### 3.4 Brushes (Terrain3D 1.0.1 semantics kept, known Godot flaws fixed)
Diameter 5..100 m, linear falloff `a = max(0, 1 - r/R)`, strength `s` 1..100 applied as `k = s*0.01` per **stroke tick** (30 Hz).
- **Script strokes apply exactly `ticks` ApplyTick calls in order, independent of DeltaTime** (one per frame, or `"per_frame":K`); each
  stroke records `ticks_applied`, gated equal to `ticks`. Script results therefore do not depend on fps (P8 tests this).
- **Mouse strokes** use a 30 Hz accumulator that catches up at most 15 ticks per frame (0.5 s); a longer hitch drops ticks, counted as
  `ticks_dropped` (a deliberate fps dependence for live input only). Tick positions interpolate along the segment between frame picks.

| Mode | Per-vertex rule (inside the footprint) | Godot parity value |
|---|---|---|
| Raise / Lower | `h ± a·k` | d=20, s=10: +0.100 at centre, +0.050 at 5 m, 0 at 10 m (R7 §2.2 live probe) |
| Smooth | `lerp(h, avg5(h₀), clamp(2·a·k, 0.02, 1))`, reading the tick-start snapshot `h₀` | Terrain3D `_operate_map` (R7 §2.2) |
| Flatten | `lerp(h, target, clamp(a·k, 0, 1))`, target = height under the cursor at stroke start (Godot hard-coded 0, R7 §2.1) | target fixed per stroke |
| Paint(L) | `w[L] += round(a·k·255)` capped at 255; the same total is taken from the other layers in proportion, floored; the leftover units go by largest fractional remainder, ties to the lower layer id; sum stays exactly 255 | Godot paint was a no-op (R7 fact 7) |

Inputs: (1) **Mouse** in `-game` and packaged: cursor shown, `DeprojectMousePositionToWorld` (`PlayerController.h:731`) feeds the pick.
LMB = stroke, `1`-`5` = mode, `Shift+1..4` = paint layer, `[`/`]` size ±5, `-`/`=` strength ±5, Ctrl+Z / Ctrl+Y = undo / redo,
**ignored while LMB is held** (Godot swallowed them mid-stroke, DW-144). (2) **Script**: `-ChimeraTerrainScript=<json>` drives the same
`FTerrainBrush::ApplyTick` with world-space paths. An AHUD Canvas (C++) shows mode, size, strength, layer, last tick ms.

### 3.5 Paint and the ground material (Manor-Lords-style, Lumen off)
- Splat: `UTexture2D::CreateTransient(640, 640, PF_R8G8B8A8, Name, InitialBytes)` (`Texture2D.h:342`), then `SRGB=false`, `Filter=TF_Bilinear`,
  `AddressX/Y=TA_Clamp`, then `UpdateResource()` again (CreateTransient calls it only when data is passed, and these properties change after;
  `Texture2D.cpp:1349-1353`), then `check(GetResource())`: with no resource `UpdateTextureRegions` silently does nothing and never calls the
  cleanup (`Texture2D.cpp:1438-1452`). Dirty rects go through `UpdateTextureRegions` (`Texture2D.h:298`) with heap copies freed in the cleanup
  callback; `splat.submits` and `splat.cleanups` are counted (gate: equal and > 0 after S1); the log must have 0 `UpdateTextureRegions called for`.
- Textures: Poly Haven CC0 2K, Grass `grass_ground`, Dirt `brown_mud_02`, Rock `rocks_ground_05`, Snow `snow_02` (alternates `forest_ground_04`,
  `dry_ground_01`, `aerial_rocks_02`, `snow_01`). Per layer `T_<L>_C` (sRGB albedo), `T_<L>_N` (`nor_dx`), `T_<L>_ARH` (AO, rough, height from
  `Displacement`; linear), packed by `fetch_textures.py`. Macro noise `/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT` (`lt_common.py:39`).
- **Samplers.** All textures enter the single Custom node as Texture Objects; each brings its own `<Name>Sampler` with the asset's own state
  (`HLSLMaterialTranslator.cpp:14196-14201`): the splat clamp/bilinear (above), layer textures and noise Wrap from their assets, or sampled in HLSL
  with `View.MaterialTextureBilinearWrapedSampler` (`SceneView.h:1162`). `SamplerSource` exists only on TextureSample (`MaterialExpressionTextureSample.h:70`),
  so the script sets no "Shared: Wrap". Sampler count is no limit here (SM6 `MaxSamplers=32`, `E/Config/Windows/DataDrivenPlatformInfo.ini:136`).
- `M_ChimeraGround` is built by `make_ground_material.py` through the Python commandlet (`PythonScriptCommandlet.cpp:15-24`), wired like
  `tools/unreal-looktest/lt_build.py:77-140`. The maths is one Custom node whose `Code` is read from `Scripts/ChimeraGround.hlsl`
  (`MaterialExpressionCustom.h:79-97`): **splat UV from absolute world position**, `u = (X/100 + E)/(2E)`, `v` likewise; world-aligned UVs at two
  scales per layer (tile 3-6 m, second ×0.21) mixed by the macro noise; height blend `w'ᵢ = max(0, wᵢ + hᵢ·depth − max_j(w_j + h_j·depth) + depth)`,
  normalised; auto-rock where `N.z < cos 35°`; brush ring from `BrushCenter`/`BrushRadius` on the MID (XY distance, so it follows the surface).
- Lighting spawned in C++ from LT_A_noLumen (R3 §1.4): sun pitch -32, yaw 120, 10 lux, 5600 K, source angle 2; SkyAtmosphere; SkyLight real-time 1.0;
  ExponentialHeightFog 0.02/0.2; VolumetricCloud; unbound PostProcessVolume with GI/reflections ScreenSpace (deprecated, `EngineTypes.h:452-466`;
  kept for parity), saturation 0.95, contrast 1.05, bloom 0.3, vignette 0.4, AO 0.5, motion blur 0.
- **Compare mode** (`-ChimeraTerrainCompare`, op `look compare|full`): clouds off, fixed exposure, `BrushRadius=0`, `ShowHUD false`.
  Numeric shots are always taken in compare mode; look shots (`closeup`, `oblique`, video) use `look full`.

### 3.6 Picking and collision
- **The cursor and editor queries use an analytic ray march over the CPU height array** (DDA over cells, then the quad's two triangles with
  the mesh diagonal): zero lag, no physics cost. It shares the source array with the sim, **not its semantics**: the sim reads
  `TerrainSimExport` values through `ElevationGrid.Sample`, a clamped nearest-cell lookup (`ElevationGrid.cs:59-74`), which differs from the
  triangulated surface by up to slope × 1 m. Any gameplay-facing height query must use a sim-semantics sampler, never the pick.
- **Physics collision** (for engine traces: props and buildings later). Render sections stay collision-free; at stroke end, undo and redo
  each touched chunk gets `SetCustomComplexMeshGeometry(...)` (`Public/RealtimeMeshManaged.h:192-199`) from the same vertices and triangles.
  **Vendored patch** at `Private/RealtimeMeshManaged.cpp:467-471` so custom geometry is kept (§2.2):
  `if (LODs.IsValidIndex(0)) { const bool bLod = ...->GenerateComplexCollision(LockContext, OutComplexGeometry); return bLod || OutComplexGeometry.NumMeshes() > 0; } return OutComplexGeometry.NumMeshes() > 0;`
  Recorded in `VENDOR.md`, proved by the C5 automation test. Patch-free fallback (risk 2): a hidden collision-only buffer set per chunk
  (`bIsVisible=false`, `Public/Core/RealtimeMeshSectionConfig.h:18`) with collision on, rewritten only at stroke end.
- Config on the chunk's `URealtimeMeshSimple` (`SetCollisionConfig` is a mesh method, `RealtimeMeshManaged.h:388-393`; the struct has a
  constructor, `RealtimeMeshCollision.h:26-57`): `FRealtimeMeshCollisionConfiguration Cfg; Cfg.bUseComplexAsSimpleCollision = true;
  Cfg.bUseAsyncCook = true; Cfg.bShouldFastCookMeshes = true; Mesh->SetCollisionConfig(Cfg);` (fast cook is measured both ways in C5).
  On the component: `bUpdateNavigationOnCollisionUpdate = false` (`RealtimeMeshComponent.h:59`), `SetCollisionEnabled(QueryOnly)`,
  responses Ignore except `ECC_Visibility` = Block, `SetCanEverAffectNavigation(false)`.
- Cook time = submit → `Updated`, valid only when the new body has `TriMeshGeometries.Num() > 0` (`BodySetup.h:278`). The cvar
  `chimera.terrain.CollisionDuringStroke=<ms>` (0 = stroke end only, the default) allows throttled mid-stroke updates (soak worst case).

### 3.7 Undo and redo
Copy-on-write per chunk per stroke: the first touch snapshots the chunk's heights (and splat tile); stroke end pushes `{chunk ids, before, after}`.
Cap 512 MiB or 1000 entries, oldest dropped (`EditorHistory.cs:25-28`). A stroke that changed no byte pushes nothing (DW-143). Undo restores
the arrays, re-uploads the chunks (rect +1 for normals), the splat rect, the bounds and the collision. Proof by FNV-1a hash (float bits, splat bytes).

### 3.8 Unattended harness
`run_terrain.ps1 -Script <name> -Tag <tag> [-Chunk 64] [-Half 160] [-Packaged] [-Shipping] [-FixedFps 30] [-MaxFps n] [-Windowed]
[-ResX 1600 -ResY 900] [-Inject] [-Measure] [-TimeoutMin 15] [-Extra "<args>"]` launches `UnrealEditor.exe T/ChimeraTerrain.uproject -game`
(or the packaged exe) at 1920x1080 fullscreen (windowed: the given size) with `-novsync -nosound -unattended -nosplash -ABSLOG=<out>/game.log`,
`-ExecCmds="t.MaxFPS <n|0>,r.VSync 0,r.ScreenPercentage 100,r.HighResScreenshotDelay 64"`, `-ChimeraTerrainOut=<absolute T/Out/tag>` and
the `-ChimeraTerrain*` arguments; no leading-slash arguments (D7). Guards copied from `tools/unreal-looktest/run_fps.ps1`: refuse if
`UnrealEditor*` is running (`:56`), delete stale outputs (`:61`), record parsecd (`:75`), `WaitForExit` with the timeout then
`taskkill /T /F /PID` (`:88-89` pattern), require the last `Scene viewport resized to` line to match the size (`:122`). `-Measure` adds an
idle precheck: no `dotnet`, `ilc`, `cl`, `link`, `ShaderCompileWorker`, `ffmpeg` or ComfyUI process and < 10 % CPU over 5 s, otherwise exit 4
`infra: busy` (it never kills anything); it records a process snapshot, parsecd and an `nvidia-smi` baseline. Wrapper timeouts: run_terrain
15 min (60 for warm-ups), run_tests 20, run_commandlet 30, each package step 60; each kills its own process tree and exits non-zero.

The director writes everything to `-ChimeraTerrainOut` (`results.json`, `height.r32`, `splat.rgba8`, `terrain.json`, PNGs, `footprints.json`,
`mouse_targets.json`); at `csv stop` it moves the newest CSV there from the per-user `%LOCALAPPDATA%/UnrealEngine/5.8/Saved/Profiling/CSV`,
`T/Saved` or the packaged `Saved` (the `Find-NewCsv` pattern, `run_fps.ps1:27-30`). It exits through `FPlatformMisc::RequestExitWithStatus(false, code)`
(`GenericPlatformMisc.h:1096`; plain `RequestExit` always exits 0): 0 all ops done, 2 op failed, 3 op timeout; `results.json` carries
`completed`, `ops_done`, `ops_total`, `fail_reason`. Whether the editor binary in `-game` propagates the code is UNVERIFIED, so C3 checks it.
Ops (timeout in brackets, every timeout fails the run):
- `camera <pose>`, `look compare|full`, `visible 0|1`, `stroke`, `paint`, `random_walk`, `idle`, `undo`, `redo`, `hash <name>`, `save`, `load <dir>`,
  `hitch <ms>` (sleeps the game thread), `csv start|stop` (`CsvProfiler.cpp:1059-1063,1118`), `movie <frames>`, `gc`, `fail`, `exit`.
- `settle [frames=30]` (600 s): wait until `GShaderCompilingManager` is null or `GetNumRemainingJobs()==0` (`ShaderCompiler.h:1214,1371`) and
  `FAssetCompilingManager::Get().GetNumRemainingAssets()==0` (`AssetCompilingManager.h:64`), then N frames; logs `settle_ms`.
- `shot <name>` (60 s): compare mode; wait until every outstanding mesh and collision future and splat cleanup has completed, plus 2 frames;
  `HighResShot 1920x1080 filename=<abs>` (Shipping: `FScreenshotRequest::RequestScreenshot`, `UnrealClient.h:219`); wait for the PNG before the next op.
- `wait_collision` (30 s): all collision futures resolved **and** every touched chunk's body has `TriMeshGeometries.Num() > 0` **and** a vertical
  probe ray at the chunk centre hits. `verify_collision`: 2,000 seeded rays from the `rts80` frustum plus a vertical ray at every vertex inside
  the last dirty rects (cap 4,000); `LineTraceSingleByChannel(ECC_Visibility)` (`World.h:2161`) vs the pick, disagreements counted both ways.
- `depthcheck <pose>`: `USceneCaptureComponent2D` with `SCS_SceneDepth` (`EngineTypes.h:540`) into an `RTF_R32f` target (`TextureRenderTarget2D.h:37`),
  `CaptureScene()` (`SceneCaptureComponent2D.h:299`), `ReadLinearColorPixels` (`UnrealClient.h:162`); 10k seeded pixels vs the analytic ray's view depth.
- `await_mouse <n>` (120 s): writes `mouse_targets.json` (world targets, `ProjectWorldLocationToScreen` `PlayerController.h:742`, viewport size).
- `soak <min>`: samples `UsedPhysical` (`GenericPlatformMemory.h:141`) and the live `UBodySetup` count every 2 s, both total and RMC-owned
  (`GetOuter()->IsA<URealtimeMesh>()`, owner per `RealtimeMesh.cpp:377`), with no forced GC; one `gc` and a final sample at the end.

Metrics: frame, GPU (`DynamicRHI.h:1250`), GT/RT (`RenderTimer.h:8-17`) ms per frame; per tick apply, normals, upload-submit, splat ms and
`ticks_applied`/`ticks_dropped`; per ranged edit the render-thread acceptance latency; `proxy_recreates_during_strokes`; per chunk cook ms and
GT apply ms; splat submits/cleanups; triangles plus CSV `RHI/PrimitivesDrawn` (`GPUProfiler.cpp:2108`); VRAM via `nvidia-smi`.
`parse_terrain.py` counts log lines matching `(LogRealtimeMesh[A-Za-z]*|RealtimeMesh): (Error|Warning)` (RMC raises most errors via
`FMessageLog("RealtimeMesh")`, logged under that name, `MessageLog.cpp:111-121`, 16 sites), `Ensure condition failed`, `Fatal`,
`Failed to load` and `UpdateTextureRegions called for`; any match fails the run and is listed. It fails any gate whose op was skipped.

**Image measures** (`imgdiff.py`): a pixel is *changed* when |Δluma| > 4/255 after a 3x3 box blur. Terrain mask = pixels that differ between
the shot and a `visible 0` shot at the same pose. Footprint mask = the stroke footprints the director projects to screen (`footprints.json`).
*Local undo statistic*: inside the footprint, changed fraction ≤ 0.5 % and worst 16x16 block mean |Δ| ≤ 2/255 (mean_abs reported too).
Every S1-type run starts with `before` and `before_aa` 1 s apart: the A/A noise floor every image bar must clear.

Scripts:
- **S1** (compare mode, pose `rts80` = look-test CAM_Gameplay (0,-5142,6128) cm, rot (-50,90,0), vFOV 75; and `oblique`): `before`, `before_aa`,
  `visible 0`, shot `bg`, `visible 1`; raise a hill, raise a ridge, lower a valley, smooth, flatten a plateau → shot `sculpt`; paint a dirt path, paint rock →
  shot `paint`; `wait_collision`, `verify_collision`, `depthcheck rts80 oblique`, shot `pre_last2`, hash; **raise a stroke across a chunk corner, then paint
  snow** → hash `after`, shot `after`, wait/verify/depthcheck; `undo 2` → hash (= `pre_last2`), wait/verify/depthcheck, shot `undo`; `redo 2` →
  hash (= `after`), wait/verify, shot `redo`; `look full`, settle 120, shots `closeup`, `oblique`; `save`. `-ChimeraTerrainHitchMs=300` inserts a
  `hitch` mid-stroke (P8).
- **S1L**: `load` s1_a's files, hash, shot `redo` (P-gate: hashes equal s1_a's final, image vs s1_a `redo` under the local statistic).
- **C1** (terrain only): idle 30 s, then a 60 s seeded random walk cycling the five modes every 5 s at d 5/20/60/100, stroke-end collision on,
  CSV on; with `visible 0|1` idle phases for P4. **C1U**: C1 with `-ChimeraTerrainUnits=1000` under `look full` (P3).
- **SOAK**: 5 min random walk, `soak` sampling; run at `CollisionDuringStroke=0` (gated) and `=250` (reported worst case).
- **VIDEO** (own sequence, `look full`, `oblique`): 3 sculpt strokes of 60 ticks, 1 paint stroke of 60 ticks, 1 undo, about 11 s at `-FixedFps 30`, `movie 330`.
- **G1**: flat terrain, `settle`, then script strokes raise a 30 m hill near the top edge of the `rts80` frustum behind a nearer 15 m hill, and a
  60 m spike whose base chunk lies outside the frustum with only its top inside; shots `visible`/`hidden`; the director logs the hill's sun and
  shadow sides, its analytic cast-shadow region and the spike's projected area to `footprints.json`. **MOUSE**: `await_mouse 3`. **FAIL**: one `fail` op.

### 3.9 Design note: how an edited heightfield reaches the sim later (not built in the trial)
1. Terrain is content: the scenario carries `height.r32` and `terrain.json` (`splat.rgba8` is presentation only). Godot-free C# in `SimSources.props`
   builds the 256x256 `ElevationGrid` with §3.2's mapping, so the NativeAOT client and the .NET server derive it from the same bytes and code.
   Unreal never writes sim state ("One way in"); it passes a scenario path.
2. Edit-to-play (F5) rebuilds `ElevationGrid` and slope pathability from the edited heights (Godot never did: `G/src/Core/MainScene.cs:3017-3037`).
   Deformation during a match would be a sim command with Fixed maths and the grid in the checksum: a separate feature.
3. Gaps to file then: terrain bytes are not in the lobby hash (`CanonicalModelHash.cs:226-237`); `SimChecksum` folds per-entity `Elevation` only
   (`SimChecksum.cs:408`). Float brush maths need not be cross-machine deterministic: the saved bytes are the authority (C10). Units follow a
   **bilinear** presentation sample; the sim stays nearest-cell (R6 §7, R7 fact 9).

## 4. Tasks (ordered)

All implementers: cite headers you rely on, do not guess API names, keep each LOCK step one command. Code is written without the lock;
builds, editor, commandlet, `-game` and packaging runs take it. Durations: build 2-6 min (first RMC build longest, UNVERIFIED), `-game` run
1-6 min, first `-game` and first commandlet run up to ~15 min (shaders; HANDOFF records ~12 min), each package step 10-40 min.
Steps marked BG run in the background. A timeout while the log shows shader compilation is **infra: retry**, never a gate or CF trigger.

**C0 Scaffold and vendor RMC** · sonnet · no lock · deps: none
`T/` per §3.1 (uproject, Target.cs files in V7/Unreal5_8 form from `ProjectChimera/Source/*.Target.cs`, Build.cs, stub module, `Config/*.ini`,
`VENDOR.md`). Clone RMC to `T/Plugins/RealtimeMeshComponent`, check out `b8669a0`, apply both patches. `RT/README.md`, `Tools/sync_to_repo.sh`
(text only: Source, Config, Tools, Scripts, uproject, VENDOR.md; never Binaries, Intermediate, Saved, Content, Plugins, Out).
Accept: `git -C T/Plugins/RealtimeMeshComponent rev-parse HEAD` → `b8669a0891b728d8fa8a31f43f76d3853c10f743`; `git -C ... diff --stat` lists
exactly `RealtimeMeshComponent.uplugin` and `Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp`; the uproject check prints
`{B44F274D-4E33-1231-4E78-41A8FFF3BD74} ['ChimeraTerrain']`.

**C1 Gate G1a: RMC compiles on 5.8.3, Editor and Game** · sonnet · LOCK · deps: C0
`Tools/build.ps1 -Target Editor|Game` (wraps `Build.bat ChimeraTerrain[Editor] Win64 Development -Project=... -WaitMutex`, 30 min watchdog,
prints wall seconds, exits with UBT's code). Accept: both targets exit 0 with `Result: Succeeded`; record seconds and warnings. Compile failure:
patch locally ≤ 1 h, record in `VENDOR.md`; else CF.

**C2 Data core and tests** · sonnet (high) · LOCK for build and test run · deps: C0 (code), C1
`Data/{TerrainHeightfield,TerrainBrush,TerrainUndo,TerrainPick,TerrainIO,TerrainSimExport}.{h,cpp}`, `Tests/TerrainDataTests.cpp`
(`Chimera.Terrain.Data`), `Tools/run_tests.ps1` (`UnrealEditor-Cmd.exe T/ChimeraTerrain.uproject /Engine/Maps/Entry -ExecCmds="Automation RunTests
<filter>;Quit" -nullrhi -unattended -nosplash -nosound -stdout`; prints `STALE` and exits 5 if `Binaries/Win64/UnrealEditor-ChimeraTerrain.dll`
is older than any `Source/` file; counts `Test Completed. Result={Success|Fail}` (`AutomationControllerManager.cpp:43`); prints `TESTS pass=<n> fail=<m>`,
non-zero if m > 0 or n = 0). Tests (≥ 17): §3.4 raise parity; lower symmetric; smooth reduces a spike; flatten converges to the stroke-start target;
height clamp; paint sum exactly 255, never wraps, remainder rule (ties to the lower id); border normals finite and exact for a tilted plane;
undo/redo exact hashes; no-change stroke pushes nothing; cap evicts oldest; pick within 1e-3 m on a slope and misses above the map; both diagonals
agree with the triangulation; truncation `0.99999→65535`, `-0.5→-32768`, `1.0→65536`, **`-0.00001→0`, `-1.00001→-65536`, `-0.3→-19660`**
(floor would give -1, -65537, -19661); cell→vertex mapping; IO byte-identical round trip; same tick list twice → same hash.
Accept: `LOCK PS T/Tools/build.ps1 -Target Editor`, then `LOCK PS T/Tools/run_tests.ps1 -Filter Chimera.Terrain.Data` → `TESTS pass>=17 fail=0`.

**C3 Chunk renderer, game shell, gate G1b** · opus (medium) · LOCK for runs · deps: C1, C2
`Render/{TerrainChunkRenderer.h, TerrainChunkComponent, RmcTerrainRenderer, TerrainSplatTexture}`, `Game/{ChimeraTerrainGameMode, TerrainActor,
RtsCameraPawn, TerrainHud}` (lighting per §3.5), `Test/{TerrainScriptDirector, TerrainMetrics}` (ops `camera settle shot visible look hash fail exit`
plus the exit-code contract), `Scripts/{G1,FAIL}.json`, `Tools/run_terrain.ps1` (§3.8, complete), `Tools/imgdiff.py` (changed_frac, mean_abs,
local statistic, `--mask terrain|paint|footprint`, luma band), first `Tools/parse_terrain.py` (log scan, `--g1`), `Tests/TerrainRenderTests.cpp`
(seams bit-equal after a corner stroke; CPU bounds contract: after a ranged raise above the old range the component `Bounds` contain the max
vertex; under `-nullrhi` this checks the CPU side only, G1's spike checks the render side). `-ChimeraTerrainDrawType=Dynamic|Static` (Static
records #303 only). Default lit grey material until C7.
Accept: `LOCK PS T/Tools/build.ps1 -Target Editor`; BG `LOCK PS T/Tools/run_terrain.ps1 -Script G1 -Tag warmup -TimeoutMin 60`;
`LOCK PS ... -Script G1 -Tag g1_dyn` and `-Tag g1_static -Extra "-ChimeraTerrainDrawType=Static"` exit 0; `python T/Tools/parse_terrain.py --g1 T/Out/g1_dyn`
→ `G1 PASS` (bars in §5; Static recorded only); `LOCK PS ... -Script FAIL -Tag failtest` exits **2**; `LOCK PS T/Tools/run_tests.ps1 -Filter
Chimera.Terrain.Render` → `fail=0`.

**C4 Director, metrics, S1/S1L/C1, determinism, GPU-vs-CPU** · sonnet (high) · LOCK for runs · deps: C3
Every §3.8 op except the collision ones, which log `skipped:C5` until C5 (the parser fails any gate needing them); `depthcheck`; `save` writes
`sim_grid_fnv` (FNV-1a 32 over the little-endian 4 bytes of each Raw, row-major) and the 16 C10 probes. `Scripts/{S1,S1L,C1}.json`,
complete `parse_terrain.py` (tables by diameter, latency, fps, GPU/GT/RT, memory, hashes, `GATES: ...`, rep spread > 5 % flag like `parse_csv.py`,
`completed` and `ops_done == ops_total` checks).
Accept: `LOCK PS T/Tools/run_terrain.ps1 -Script S1 -Tag s1_a` and `-Tag s1_b -MaxFps 20 -Extra "-ChimeraTerrainHitchMs=300"` exit 0;
`python T/Tools/parse_terrain.py --same-hash T/Out/s1_a T/Out/s1_b` → `height_fnv equal, splat_fnv equal, ticks_applied==ticks`; S1 hashes:
undo = `pre_last2`, redo = `after`; P7 image and P11 depth bars pass on s1_a; `-Script S1L -Tag s1l -Extra "-ChimeraTerrainLoad=<abs T/Out/s1_a>"` passes.

**C5 Collision path and #290 soak** · opus (medium) · LOCK for runs · deps: C4
§3.6 complete; `wait_collision`, `verify_collision`, the cvar throttle, GT-apply timing, RMC-owned body counts, SOAK. Latent automation test
`Chimera.Terrain.Collision.CustomOnly`: a mesh with collision-free sections plus `SetCustomComplexMeshGeometry` resolves `Updated` with
`GetBodySetup()->TriMeshGeometries.Num() > 0` (fails on unpatched RMC). Measure `bShouldFastCookMeshes` on and off in S1; keep the faster if P5 holds.
Accept: build; `LOCK PS T/Tools/run_tests.ps1 -Filter Chimera.Terrain.Collision` → `fail=0`; `-Script S1 -Tag s1_col` meets P5 at all four
checkpoints; BG `-Script SOAK -Tag soak0` meets P6, BG `-Tag soak250 -Extra "-ChimeraTerrainCollisionDuringStroke=250"` reported.
If the patch misbehaves: risk 2's hidden collision set; if that fails too: CF.

**C6 Fetch and pack CC0 layer textures** · sonnet · no lock · deps: C0
`Tools/fetch_textures.py`: Poly Haven 2K `Diffuse` jpg, `nor_dx`, `arm`, `Displacement` png for §3.5's ids; verify API md5s; pack ARH; write
`T/Textures/<layer>/*` and `manifest.json` (ids, urls, md5, CC0, date). Accept: `OK layers=4 files=12 md5=verified`. Network failure: repo PNGs
`G/assets/textures/terrain/*.png` with flat normals and `T_ground_Moss_D`, `fallback:true` in manifest and caption.

**C7 Ground material, look, GPU cost** · opus (medium; judges the look) · LOCK · deps: C4, C6
`Scripts/make_ground_material.py` (imports 12 textures to `/Game/Terrain/Textures`, builds `/Game/Terrain/M_ChimeraGround` per §3.5,
`recompile_material` errors empty), `Scripts/ChimeraGround.hlsl`, `Tools/run_commandlet.ps1`, MID wiring, compare mode, `Tools/composite.py`
(copied from `tools/unreal-looktest/composite.py`, extended to burn in numbers).
Accept: BG `LOCK PS T/Tools/run_commandlet.ps1 -Script T/Scripts/make_ground_material.py` → `MATERIAL_OK /Game/Terrain/M_ChimeraGround errors=0`;
one warm-up S1 after each material change, then `-Script S1 -Tag s1_mat`: `imgdiff --mask paint` `paint` vs `sculpt` → changed_frac ≥ 0.30.
`python T/Tools/composite.py` puts `closeup` and `rts80` beside `ProjectChimera/LookTest/out/game_A_noLumen_r1.png`; the implementer writes
V1-V6 notes with reasons; **the main session sends this composite to Alec as soon as it exists**. GPU: C1 `visible` phases → `terrain_gpu_ms`.

**C8 Mouse sculpting, HUD, mouse proof** · sonnet · LOCK for the run · deps: C4
`Game/ChimeraTerrainPlayerController` (§3.4 keys, mouse ticks, `source=os|slate` per stroke with centre and pick error), `Scripts/MOUSE.json`,
`Tools/inject_mouse.py` (ctypes only: `SetProcessDpiAwareness(2)`, find the window, `GetClientRect`/`ClientToScreen`, wait for
`mouse_targets.json`, `SetForegroundWindow`, `SendInput` drags at the logged viewport targets; pre-check: `LogonUI.exe` absent (screen not
locked), parsecd state recorded). Strokes fixed: d 30, s 30, 60 ticks over a 30 m drag. Fallback `-ChimeraTerrainSynthMouse` via
`FSlateApplication::ProcessMouse*Event` (`SlateApplication.h:1292-1310`), logged `source=slate`.
Accept: `LOCK PS T/Tools/run_terrain.ps1 -Script MOUSE -Tag mouse -Windowed -ResX 1600 -ResY 900 -Inject` exits 0; P10 bars.
`source=slate` is a P10 FAIL unless Alec waives it.

**C9 Unit layer and sculpt video** · sonnet · LOCK for runs · deps: C7
`Game/TerrainUnits` (`-ChimeraTerrainUnits=N`: an ISM of the `crucible_mortar` static mesh copied from
`ProjectChimera/Content/LookTest/Roster/crucible_mortar` with the same path, instances riding a bilinear height sample, updated only where
terrain changed), `Scripts/{VIDEO,C1U}.json`, `Tools/make_video.sh` (collects `MovieFrame*.png` from the screenshot dirs searched like CSVs,
ffmpeg `C:/Users/MD_Ki/AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe` → `sculpt.mp4` h264 1280x720 30 fps, `sculpt.gif` 640 px 15 fps).
Accept: `LOCK PS ... -Script VIDEO -Tag video -FixedFps 30 -Extra "-ChimeraTerrainUnits=300"`, then `bash T/Tools/make_video.sh T/Out/video`;
`ffprobe` duration 10-15 s; GIF ≤ 8 MB. One `-Script C1U -Tag c1u_smoke` exits 0.
As built (2026-10-03, EXECUTION §8 'C9 record'): units ride `HF.SampleSurface` via `FOnTerrainChanged`; the git-ignored unit content is
rebuilt by the `cp -r` plus `Scripts/fix_unit_usage.py` (ISM usage on the material instance), both under the lock.

**C10 Sim-grid parity** · sonnet · no lock · deps: C4
`RT/elevhash/elevhash.csproj` (net8.0 console; `<Compile Include>` `G/src/Core/FixedPoint.cs`, `ElevationGrid.cs`, `HeightmapCellMapping.cs`,
all Godot-free). It reads `height.r32`, picks texels with `CellToTexel(c,256,256)` and vertices `(texel+B)`, converts with `Fixed.FromFloat`,
constructs `ElevationGrid` exactly as `ScenarioLoadPhase.cs:269-270` (min `Fixed.FromFloat(-128)`, cell `Fixed.FromFloat(1)`), prints
`sim_grid_fnv` (same FNV definition as C4) and 16 probe `Sample()` values at sim-world points defined here, including negative non-exact
heights and cell-boundary points; the C++ side pushes the same points through `SimToUe` and writes its values. It does not touch
`ProjectChimera.Sim.Tests` (baseline 6392 / 0 / 1). Accept: `dotnet run --project RT/elevhash -- T/Out/s1_a/height.r32 321 160` equals
s1_a's `sim_grid_fnv` and probes, and the `axes` field matches. Hand-off, not gated here: when check (a)'s host can build a `SimulationHost`,
it runs `ServerBootstrap.Build(..., elevationGrid)` (`ServerBootstrap.cs:38-41`) on a shipped scenario with this grid and records a
checksum that differs from flat (R7 §8: no golden uses a non-zero grid).

**C11 Gate G2: packaged builds** · sonnet (high) · LOCK, three BG steps · deps: C5, C7, C8
`Tools/package.ps1 -Config Development|Shipping -Step build|cook|stage` (`RunUAT.bat BuildCookRun -project=... -platform=Win64 -clientconfig=<cfg>`
with `-build` | `-cook -skipbuild` | `-stage -pak -archive -skipcook -archivedirectory=T/Packaged/<cfg>`, `-map=/Engine/Maps/Entry -unattended
-utf8output -noP4`). In Shipping the director sets cvars through `IConsoleManager` and shoots via `FScreenshotRequest` (console paths may be
compiled out, UNVERIFIED). Accept (Development): three steps exit 0, exe exists; `-Script S1 -Tag pkg_s1 -Packaged` exits 0, hashes equal
s1_a (or `max_abs_dh<=1e-4` with the compiler cause), `after` vs `before` changed_frac ≥ 0.10, C7's paint bar, parser log scan clean
(incl. `Failed to load`); `-Script S1L -Tag pkg_s1l -Packaged` passes; `-Script MOUSE -Tag pkg_mouse -Packaged -Windowed -ResX 1600 -ResY 900
-Inject` meets P10. Shipping: package, then `-Script S1 -Tag ship_s1 -Packaged -Shipping`: `results.json` hashes equal s1_a and `after.png` exists.

**C12 Measurement campaign and chunk size** · sonnet · LOCK per run, BG · deps: C5, C7, C9
Every run uses `-Measure`; if a foreign GPU or CPU user (ComfyUI, a `dotnet test`) is found the run aborts and the main session reschedules;
nothing is killed. Warm up once, then 3 reps each of C1 at `-Chunk 64` and `-Chunk 32`, 3 reps of C1U at the winner, one C1 at `-Half 320`
(819,200 triangles, headroom), one C1 with `RealtimeMesh.InPlaceUpdate.Enabled=0` (`RealtimeMeshBufferSet.cpp:17-22`, cost of the fallback
path), SOAK again if C5 changed. Chunk size by lower median GPU+GT idle cost, then p99 tick ms; set as default.
Accept: `python T/Tools/parse_terrain.py --summary T/Out/c1_* T/Out/c1u_* T/Out/soak* > PROOF/summary.txt` lists every §5 metric PASS/FAIL,
FAILs with `no samples` for any gated diameter without data, and writes `PROOF/summary.json`.

**C13 Proof pack and visual verdict** · opus (xhigh, review) · no lock · deps: C9, C10, C11, C12
Committed to `PROOF/`: JPG composites (`composite.jpg`: before | after | undo | redo | closeup with numbers burned in; `g1.jpg`; `mouse.jpg`;
`pkg.jpg`), `results/*.json`, `summary.*`, `README.md` (gate table, numbers, V verdicts, deviations, RMC hash and patches). Full PNGs,
`sculpt.mp4` and `sculpt.gif` stay in `T/Out` (the look-test precedent is 2.6 MB of JPG+JSON). Accept: `python T/Tools/parse_terrain.py
--check-proof PROOF` → `PROOF OK files=<n> gates_failed=0` (or the explicit failure list). The main session sends `composite.jpg`,
`sculpt.gif` and the README summary to Alec.

**C14 Checkpoint** · sonnet · no lock · deps: C13
`bash T/Tools/sync_to_repo.sh`; `git -C D:/Projects/Project_Chimera status --short` shows only `tools/unreal-terrain/` and
`docs/unreal-move/trial-checks/`. The main session commits, pushes and rewrites HANDOFF.

**CF (conditional) UDynamicMeshComponent renderer** · opus (medium) · LOCK for runs · trigger: C1 cannot compile in the time box, G1 fails
with Dynamic, C5 cannot get working collision with the patch or the hidden set, or C11 shows a blank terrain.
`ITerrainChunkRenderer` with one `UDynamicMeshComponent` per chunk: `EditMesh(Func, EDynamicMeshComponentRenderUpdateMode::NoUpdate)`
(the default is FullUpdate, `DynamicMeshComponent.h:220-223`), write the recomputed normals into the normal overlay, then
`FastNotifyPositionsUpdated(true)` (`:287`); `bUseAsyncCooking=true`, `SetTransientDeferCollisionUpdates(true)` during strokes, `UpdateCollision`
at stroke end. Rerun C3-C5, C11, C12 unchanged. If that also fails, stop and ask Alec about the Errant trial (§8).

## 5. Proof of done (proposed bars with their reason; Alec may adjust, §8)
| # | What | Threshold (reason) | Artefact |
|---|---|---|---|
| G1 | RMC builds (Editor, Game); Dynamic chunks render correctly in `-game` | `Result: Succeeded` ×2; terrain changed_frac ≥ 0.40 vs hidden; terrain-mask mean luma in [0.05, 0.95] with std > 0.01 (not black, not flat, not depth-only); hill sun side ≥ 1.3× its shadow side and its cast shadow ≥ 20 % darker than lit ground (lighting and VSM on edited shapes); ≥ 50 % of the spike's projected pixels changed (render bounds follow edits) | `g1.jpg`, results |
| G2 | Packaged Development sculpts by script and mouse; Shipping by script | S1 and S1L as in C11; mouse = P10; Shipping S1 hashes equal and `after.png` exists | `pkg.jpg`, `pkg_*/results.json` |
| P1 | Game-thread cost per stroke tick (apply + normals + upload + splat) | p50 ≤ 1.0 ms at d 20, ≤ 2.0 ms at d 100; p99 ≤ 4 ms (2 ms = 12 % of a 16.7 ms frame); every gated diameter must have samples | `summary.txt` |
| P2 | Render-thread acceptance latency of ranged edits, and in-place use | median ≤ 2 frames; `proxy_recreates_during_strokes` ≤ 5 % of ranged edits (≥ 95 % in place; fallbacks reallocate, RMC `RealtimeMeshData.cpp:500-543`) | `summary.txt` |
| P3 | fps while sculpting with 1,000 units (C1U, 1080p, `rts80`, `look full`) | median ≥ 60, 1% low ≥ 45, ≤ 0.5 % frames > 33.3 ms, none > 100 ms; sculpt − idle ≤ 2 ms (spec: 60 fps with 500-2,000 units) | CSV, `summary.txt` |
| P4 | Terrain GPU cost (visible − hidden, idle, C1) | ≤ 3.0 ms median (A_noLumen scene ran 12.7 ms GPU, look-test README; +3 ms = 15.7 ms ≈ 64 fps) | `summary.txt` |
| P5 | Collision current for traces | at `pre_last2`, `after`, `undo`, `redo`: 0 disagreements either way and max \|Δz\| ≤ 1 cm over frustum and vertex rays; every touched body has a trimesh; cook p95 ≤ 50 ms (samples from S1, C1, SOAK; ≈ 3 frames); GT apply per stroke end p95 ≤ 4 ms | `results.json` |
| P6 | #290 memory (SOAK, `CollisionDuringStroke=0`, no forced GC) | peak `UsedPhysical` growth ≤ 300 MB (16 GB machine; editor idles at 4.2 GB, HANDOFF); after the final GC growth ≤ 100 MB and RMC-owned bodies ≤ 2 × chunks; peak bodies and creation rate reported, `soak250` reported | `soak*/results.json` |
| P7 | Undo and redo exact | hashes undo = `pre_last2`, redo = `after`; `undo` vs `pre_last2` and `redo` vs `after` pass the local statistic | `composite.jpg`, imgdiff |
| P8 | Determinism independent of fps | s1_a (uncapped) vs s1_b (`t.MaxFPS 20` + 300 ms hitch mid-stroke): equal height and splat FNV; `ticks_applied == ticks` everywhere | `s1_a`, `s1_b` |
| P9 | Sim-grid parity | C++ `sim_grid_fnv` = C# FNV via the shared mapping code; 16 probes equal; `axes` recorded | `elevhash` output |
| P10 | OS mouse in `-game` and packaged | ≥ 3 strokes `source=os`; pick error ≤ 1 m against the logged targets (≈ 9 px at 80 m); footprint changed_frac ≥ 0.30 above the A/A floor | `mouse.jpg` |
| P11 | GPU mesh equals the CPU heightfield | `depthcheck` at `rts80` and `oblique` after `after` and `undo`: p99 \|Δdepth\| ≤ 2 cm on 10k pixels; 0 hit/miss disagreements outside a 2 px silhouette band | `results.json` |
| R | Reported, not gated | triangles 204,800 / 819,200; PrimitivesDrawn; process and VRAM memory; cook ms fast/normal; chunk choice; Static #303; `InPlaceUpdate=0` cost; `ticks_dropped`; `settle_ms`; A/A noise | `summary.json` |
| V | Look | **gated (technical, Opus with the numbers):** V3 no chunk seams (seam test, P11, no luma step on chunk borders); V6 brush ring follows the surface and shadows stay stable in the video. **Reported to Opus and Alec, not gated:** V1 soil and grass read real at `rts80` beside LT_A_noLumen; V2 no obvious tiling; V4 rock on steep faces, no stretching at `oblique`; V5 path, rock and snow read with soft height-blended edges | `composite.jpg`, `sculpt.gif` |

**Done** = G1, G2, P1-P11 PASS, V3 and V6 pass review, R and V1/V2/V4/V5 reported and sent to Alec. A FAIL is delivered with its numbers and
the fallback taken; never rounded up.

## 6. Risks and fallbacks
1. **RMC on 5.8.3 is untagged; #303 open (Static invisible in standalone, 5.8.2, AMD).** Always Dynamic; G1/G2 test standalone and packaged on the 3060; CF ready.
2. **Collision.** The custom-geometry patch (§3.6) is new code in a vendored plugin; the C5 test fails without it. If it misbehaves: hidden
   collision-only buffer set per chunk updated at stroke end. #290 (new `UBodySetup` per update) is bounded by stroke-end-only cooks and gated
   by peak memory (P6). Last resort: collision only on leaving edit mode, or none (the pick covers the cursor and editor queries).
3. **VSM cost of Movable/Dynamic chunks** (re-rendered each frame, §2.9). Measured in P4 and P3. Fallback: idle chunks (> 2 s) rebuilt as Static,
   only if #303 does not reproduce on NVIDIA (G1 `g1_static`).
4. **Whole-stream copy per ranged edit** (`RealtimeMeshSimple.cpp:79-88`) scales with chunk size; C12 picks 32 or 64; a d=100 brush touches ≤ 9 chunks of 64.
5. **Ground material too heavy** (≈ 2 × 4 layers × 3 samples + splat + macro). Fallbacks in order: (1) drop the second tiling scale far from the camera;
   (2) cut samples (height into albedo alpha, AO/rough from normal B/A); (3) RVT with `URuntimeVirtualTextureComponent::Invalidate(bounds)`
   (`RuntimeVirtualTextureComponent.h:165`) only if Static renders on the 3060 (via risk 3's idle-Static chunks) or with a vendored patch that
   emits RVT batches from `GetDynamicMeshElements` (§2.8).
6. **Poly Haven unreachable or a layer looks wrong**: alternates (§3.5), then the flagged repo-PNG fallback (C6).
7. **Commandlet import or material quirks** (Substrate legacy pins; `MaterialEditingLibrary` setters return False on success, HANDOFF): mirror
   `lt_build.py`. Fallback: rerun the commandlet once with `-unattended -nosplash`; then a ChimeraTerrain copy of `start_editor.sh`/`ue_bridge.py`
   with the uproject path and queue directory parameterised (today they launch ProjectChimera from its `LookTest/tools`), launched with `MSYS_NO_PATHCONV=1` (D7).
8. **Packaging** (installed-engine Game target, cook of `/Engine/Maps/Entry`, C++-referenced engine assets) is UNVERIFIED end to end; 2 h time box;
   without G2 the "shipped game" claim fails and is reported as such. Shipping console and log paths may be compiled out (C11 avoids them).
9. **Mouse injection blocked by focus rules, a locked screen or Parsec**: pre-checks record the state; Slate fallback is flagged and fails P10 unless waived.
10. **Axis mapping drift from check (a)**: one `SimToUe`, recorded in `terrain.json` `axes`; the merge aligns it to (a).
11. **Lock robustness on 16 GB.** Our wrappers kill their own process tree on timeout and long steps run BG. Two items belong to the main session:
    (a) a killed `ue_lock.sh` lets a waiter take over while UnrealEditor still runs (`ue_lock.sh:24-29` checks only the bash pid); recording the
    Unreal child pid in the owner file and treating the lock as stale only when both are gone would close it; (b) the lock does not cover
    heavy non-UE jobs from checks (a)/(b) (`dotnet test`, ILC publish, ffmpeg), so C12 should run while nothing else executes (or under a `measure` lock).
12. **One-maintainer plugin**: `ITerrainChunkRenderer` keeps data, brushes, undo and pick renderer-free; CF proves the swap costs one class.
13. **Tracked bounds** (§3.3) could miss a height change that bypasses the bounds update (a new code path such as `load`). The G1 spike and
    S1L catch stale bounds; fallback: fixed conservative bounds (footprint × ±128 m), at the cost of weaker culling and wider VSM coverage.

## 7. Out of scope
Ramp, noise and plateau tools. Water, impassable paint, regions, props. Terrain LOD, Nanite, RMC Pro. RVT unless risk 5 triggers. (Foliage
and grass scatter moved into scope on 2026-10-02: `plan-c-scatter.md`.) The Round 2 Map Editor UI (debug HUD only). Feeding edits to the running sim, in-match deformation, terrain in the
lobby hash (§3.9 is a note); running the sim on the edited grid is handed to check (a) (C10). The 32-layer palette (DW-1019). A Godot `.res`
importer. Merging into ProjectChimera. The Errant evaluation unless CF also fails. Multiplayer sync of edits.

## 8. Decisions for Alec
1. **Scope reading of "terrain editing while the game runs"**: sculpt and paint inside the running packaged game (the in-game map editor), with
   the sim picking the heights up when play starts (F5). Mid-match deformation is not in the trial. Confirm, or ask for deformation during play.
2. **Pass bars** in §5 are proposals with their reasons (60 fps with 1,000 units while sculpting, ≤ 3 ms terrain GPU, ≤ 1 ms per brush tick,
   ≤ 300 MB peak memory growth in the soak, ...). Confirm or change before C12.
3. **What "done" means for (c)**: the ground's look (V1, V2, V4, V5) is reported to you, not a gate; (c) passes on the technical bars. Say if the look must pass too.
4. **Contingent, nothing to buy now**: if RMC and the in-engine fallback both fail, the next step is Errant Landscape's free 30-day trial
   (a request form in your name; price unpublished; 5.8 support unconfirmed).

## 9. Evidence
Citations are inline, checked 2026-10-01 against RMC `b8669a0`, the 5.8.3 engine source, the repo and this machine (Git Bash vs WSL `bash`; ffmpeg,
PIL, numpy present; pyautogui, pywin32 absent). #303, #290 and the Errant facts are as R6 cites them (not re-fetched).

## Review log (2026-10-01 critique; A = accepted, A* = accepted with a different or partial fix, R = rejected)
- api-1 blocker, custom collision dropped: A. Verified `RealtimeMeshManaged.cpp:225-258,462-471,719-722`, `RealtimeMesh.cpp:373-421`. Option (a) patch, test, trimesh check in `wait_collision`; option (b) kept as fallback.
- api-2 bounds never refresh, VSM: A*. Verified the event chain. Chunk component owns bounds, but **tracked** (exact + 16 m headroom, pushed via `MarkRenderTransformDirty`) instead of fixed ±128 m, which would cripple culling and widen VSM coverage; fixed bounds are risk 13's fallback. VSM: Movable chunks are cached as dynamic (§2.9), so no per-tick dirtying; G1 spike and shadow checks added.
- api-3 RVT not on the Dynamic path: A. Verified `RealtimeMeshComponentProxy.cpp:226-290`, `RealtimeMeshBufferSetProxy.cpp:301,360`. §1, §2.8, risk 5 reordered.
- api-4 `settle` ignores shader compiles: A. `settle` defined (shader and asset compile queues, N frames, 600 s timeout); warm-up per material change.
- api-5 accumulator makes P8 flaky: A. Script strokes apply exactly `ticks`; `ticks_applied` gated; mouse accumulator documented.
- api-6 no timeouts, exit codes always 0: A. Op timeouts, `RequestExitWithStatus` (`GenericPlatformMisc.h:1096`), wrapper watchdogs that kill their tree; C3 checks the exit code.
- api-7 mouse targets ignore window offset and DPI: A. `mouse_targets.json` from `ProjectWorldLocationToScreen`, DPI-aware `ClientToScreen`, 1600x900 window.
- api-8 log grep misses `RealtimeMesh:` lines: A. Verified `MessageLog.cpp:111-121`, 16 `FMessageLog` sites. Regex in `parse_terrain.py`.
- api-9 S1 needs C5 ops; C7 deps: A. C4 stubs collision ops (`skipped:C5`, gates fail on skipped); C7 deps C4, C6.
- api-10 imgdiff/composite unassigned, wrong path: A. `imgdiff.py` in C3, `composite.py` copied in C7 at `T/Tools/`.
- api-11 CreateTransient without data has no resource: A. Verified `Texture2D.cpp:1349-1353,1438-1452`. Create with data, set props, `UpdateResource`, `check`; submit/cleanup counters gated.
- api-12 sampler claims contradict: A. Verified `MaterialExpressionTextureSample.h:70`, `HLSLMaterialTranslator.cpp:14196-14201`. Per-texture samplers stated.
- api-13 half-float UV: A. `FVector2f` builder; splat UV from world position in the material.
- api-14 run_tests loads OpenWorld, no build: A. Verified `BaseEngine.ini:14`. `EditorStartupMap` + map argument; build step and `STALE` check.
- api-15 packaged output locations: A. `-ChimeraTerrainOut`; CSV moved into Out from the three possible dirs.
- api-16 cloud material not cooked: A. Both `DirectoriesToAlwaysCook` lines with the section name (`BaseGame.ini:87`); `Failed to load` scanned.
- api-17 truncation tests cannot tell floor: A. Added `-0.00001`, `-1.00001`, `-0.3`.
- api-18 C10 re-implements the mapping, FNV undefined: A. `HeightmapCellMapping.cs` compiled in; ctor as `ScenarioLoadPhase.cs:269-270`; LE row-major FNV; C4 `save` writes them.
- api-19 UBodySetup count includes all bodies: A. RMC-owned count gated, total reported.
- api-20 `SetCollisionConfig` is a mesh method, brace init: A. Verified `RealtimeMeshManaged.h:388-393`, `RealtimeMeshCollision.h:26-57`. Code spelled out; fast cook measured.
- api-21 CF EditMesh default FullUpdate, deps missing: A. Verified `DynamicMeshComponent.h:220-223`. `NoUpdate` + overlay normals; deps in C0.
- api-22 numeric shots show the ring, catch half-applied updates: A. Compare mode hides ring and HUD; `shot` waits for futures, cleanups, 2 frames, the PNG.
- api-23 copied renderer block keeps Lumen: A*. Verified ProjectChimera `DefaultEngine.ini:6-14`. GI and reflections set to ScreenSpace; distance fields kept because LT_A_noLumen was measured with them.
- api-24 risk 7 bridge tied to ProjectChimera: A. Risk 7 names the parameterised copy and the commandlet retry.
- api-25 brush and input gaps: A. Remainder rule, Shift+1..4, one-sided border normals, Ctrl+Z/Y ignored while LMB held; C2 tests.
- api-26 pick vs sim overstated: A. Verified `ElevationGrid.cs:59-74`. §3.6 reworded.
- api-27 VIDEO cannot hold S1 in 12 s: A. VIDEO is its own ~11 s sequence.
- api-28 P2 cannot see the fallback path: A. Verified `RealtimeMeshProxyCommandBatch.cpp:139-152`. Proxy-recreate counter (no patch needed), in-place gate, `InPlaceUpdate=0` rep.
- proof-1 blocker, collision bodies empty: A (same as api-1).
- proof-2 undo collision never checked: A. S1 ends with a sculpt stroke across a chunk corner then paint; verify after undo and after redo; vertex rays added.
- proof-3 P7 compares with the wrong shot, mean too blunt: A. `pre_last2` shot; local statistic; mean_abs secondary.
- proof-4 GPU mesh vs CPU never checked; G1 accepts black: A. `depthcheck` (P11); G1 luma band, sun/shadow and spike checks.
- proof-5 P6 gates residue not peak: A. 2 s sampling without forced GC, peak gated, after-GC kept as leak check; gated at the default stroke-end setting, 250 ms reported.
- proof-6 P8 does not vary fps: A. s1_b at `t.MaxFPS 20` with a 300 ms hitch.
- proof-7 no d=100 data: A. C1 cycles d 5/20/60/100; missing samples FAIL.
- proof-8 perf contamination outside the lock: A*. `-Measure` precheck aborts and records state; a `measure` lock is the main session's call (risk 11b).
- proof-9 long steps exceed the tool timeout; stale-lock takeover: A*. BG marking, tree-killing watchdogs, BuildCookRun in three steps; the `ue_lock.sh` child-pid change is the main session's (risk 11a).
- proof-10 commands only work in Git Bash: A. Verified (`Get-Command bash -All` → WindowsApps). Shell rule, 5.1/UTF-8 scripts, no `grep -c`.
- proof-11 P9 compares trial-only code: A*. Shared mapping code, negative non-exact and boundary probes, `axes` field. Running `ServerBootstrap` on the edited grid needs check (a)'s host and scenario loading, so it is handed to (a), not gated here.
- proof-12 P3 without units: A. C1U with 1,000 ISM units gates P3; P4's 3 ms derived from the look-test 12.7 ms.
- proof-13 RMC error grep: A (same as api-8).
- proof-14 P2 naming, in-place unknown: A (same as api-28); renamed.
- proof-15 bounds test under `-nullrhi`: A. Test documented as CPU contract; G1 spike is the render-side check; §2.4 cites the source.
- proof-16 C4 needs C5 ops: A (same as api-9).
- proof-17 run_terrain under-specified: A. Full parameter list, run_fps guards, `completed`/`ops_done` checks.
- proof-18 compare shots show ring and HUD: A (same as api-22).
- proof-19 splat sampler and UV precision: A (same as api-12/13).
- proof-20 no save-restart-load proof: A. `load` op, S1L in `-game` and packaged.
- proof-21 mouse proof false failures, unjustified bar: A. Logged targets, DPI, lock-screen pre-check, slate = FAIL unless waived, fixed stroke, footprint bar.
- proof-22 collision sample count, one-sided misses, GT cost: A. Samples from S1, C1, SOAK; both-way disagreements; vertex rays; GT apply gated ≤ 4 ms.
- proof-23 subjective look decides Done; late delivery: A. Only V3/V6 gate; V1/V2/V4/V5 reported; C7 composite sent at once; decision 3 for Alec.
- proof-24 packaged painting unchecked, Shipping ungated: A. Paint bar and S1L on the package; Shipping gated on hashes and `after.png`.
- proof-25 first run may time out on shaders: A. BG warm-up with a 60 min timeout; shader-compile timeouts are infra-retry.
- proof-26 thresholds and masks undefined: A. Changed-pixel rule, masks, A/A floor, one reason per bar in §5.
- proof-27 C12 closes ComfyUI: A. Detect and abort; nothing is killed.
- proof-28 PROOF binaries in git: A. JPG composites, JSON and README committed; PNG/MP4/GIF stay in `T/Out` and go to Alec.
- proof-29 API wording, collision channel: A (same as api-20); QueryOnly, `ECC_Visibility` Block, no navigation.
