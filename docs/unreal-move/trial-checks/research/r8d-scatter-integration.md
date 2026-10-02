# R8d: where a scatter system plugs into ChimeraTerrain

Date 2026-10-01. Design only: nothing was edited, no Unreal process was started (lock rule). Read in full: plan C (`plan-c-runtime-terrain.md`, all of it; §1, §3.1-3.9, §4 C9/C11/C12, §5, §7), `EXECUTION.md` §1.1, §2.2, §3, §7, §8, every file under `D:/Projects/Chimera-Unreal/ChimeraTerrain/Source/ChimeraTerrain` that this note cites (Data, Render, Game, Test, Tests), `Tools/{run_terrain.ps1, run_commandlet.ps1, parse_terrain.py, imgdiff.py, fetch_textures.py, sync_to_repo.sh}`, `Scripts/{S1,S1L,C1,SOAK}.json`, `Scripts/make_ground_material.py`, `Config/*.ini`, the C7 evidence summary, and the UE 5.8.3 headers cited below.

Conventions: `T` = `D:/Projects/Chimera-Unreal/ChimeraTerrain`, `S` = `T/Source/ChimeraTerrain`, `E` = `D:/Epic Games/UE_5.8/Engine`, `PC` = plan C, `EX` = EXECUTION.md. `file:line` citations are against the working tree as read; the ground-look workflow is editing `Game/ChimeraTerrainGameMode.{h,cpp}`, `Game/TerrainLighting.{h,cpp}` and `Config/DefaultGame.ini` right now (git status), so lines in those three drift: use the function names there. UNVERIFIED marks what only a build or run settles.

## 0. Verdict

**Scatter fits ChimeraTerrain with one new hook seam and no change to any existing gate.** Every height or splat write already funnels through five methods of `ATerrainActor` (`ApplyTick`, `EndStroke`, `ApplyDelta` for undo/redo, `LoadFrom`, `InitTerrain`), the CPU arrays `HF.Heights` and `HF.Splat` are the authority (the GPU splat texture is write-only), and `HF.SampleSurface` already is the exact triangulated surface the mesh draws. Recommended shape:

1. A pure-C++ **generator** in `Data/` (no UObjects, `const` heightfield in, instance lists out): candidate lattice keyed by world cell + species + slot through an integer hash, accepted by integer-quantised rules (splat weight, slope, height). Terrain only ever *accepts or rejects* candidates and supplies Z, so thinning on paths, rock and snow is monotone and edits are local.
2. A **world-aligned 32 m tile grid owned by scatter**, independent of `ChunkQuads` (C12 will pick 32 or 64 later; the instance set must not depend on it, nor on `-ChimeraTerrainHalf`).
3. A separate **`ATerrainScatter` actor** (not components of `ATerrainActor`: the P11 depth capture uses `ShowOnlyActors.Add(Terrain)`, which pulls in every primitive component of that actor), one `UInstancedStaticMeshComponent` per (non-empty tile, species), no collision, no navigation.
4. One new **`FOnTerrainChanged` event on `ATerrainActor`** fired from the six hook points in §2 with the height and splat rects of each event. Scatter subscribes; C9's unit layer should subscribe to the same event instead of adding its own hooks.
5. **Threading: snapshot on the game thread, generate on the task graph, apply on the game thread under a per-frame time budget.** No locks, no shared reads of mutable arrays.
6. **Dirty tracking per tick as a tile set**, not the stroke's union rect (the union over-covers a diagonal stroke by about 2x, §4.3). Mid-stroke refresh is throttled like the collision cvar; stroke end, undo, redo and load flush at priority.
7. **Scatter is off by default in every scripted run and on by default in interactive and packaged play** (the precedent is the mouse controller's "armed only when no script is loaded", `Game/ChimeraTerrainPlayerController.h` header comment). That keeps S1, S1L, C1, SOAK, G1, MOUSE bit-identical to what passed; new scripts S1X, S1XL, C1S, C1US exercise scatter and are reported configurations (`parse_terrain.py:717-747` makes `-ChimeraTerrainScatter=` a separate configuration automatically, so P1-P4 gating of the default configuration is untouched).
8. Test hooks: a **reference hash** (full regeneration from the heightfield, synchronous, independent of any incremental state) and a **live hash** (hash of what the ISMs hold); the gate is live == reference at every settled checkpoint plus undo == pre_last2, redo == after, S1L == s1_a final. Section 6 lists every op, field and parser row.

Cost/budget (from the measured C7 run, §8): idle GPU 11.63 ms at `rts80`, 60 fps needs 16.67 ms, so scatter plus 1,000 units must fit in about 5 ms of GPU; a design target of **scatter <= 2.5 ms GPU, <= 1.0 ms average game thread, apply slice <= 1.5 ms per frame** is proposed, reported not gated (EX D2).

## 1. The plumbing as it exists today

### 1.1 One funnel for every mutation
All writers go through `ATerrainActor` (`S/Game/TerrainActor.cpp`). A search for `GetHeightfield()`, `ApplyTick(`, `BeginStroke(`, `EndStroke(`, `UndoLast`, `RedoLast`, `LoadFrom`, `SetHeight(` and `.Heights[` over `S` outside `Tests/`, `Data/TerrainBrush`, `Data/TerrainUndo` finds the controller (`Game/ChimeraTerrainPlayerController.cpp:311,318,356-358,389,398`), the director (`Test/TerrainScriptDirector.cpp:688,711,718,1051,1121,1187-1238`) and nothing else; the only direct array writes are `FTerrainBrush::ApplyTick` (`Data/TerrainBrush.cpp:219`), `FTerrainUndo::Restore*` (`Data/TerrainUndo.cpp:47-75`) and `TerrainIO::Load` (`Data/TerrainIO.cpp:258-260`). Everything else only reads `GetHeightfield()` (const ref, `Game/TerrainActor.h:119`). A scatter system hooked in `ATerrainActor` therefore cannot miss an edit.

`ATerrainActor` does not tick (`TerrainActor.cpp:41`); the renderer's `PollCompletions()` is called only by the director (`Test/TerrainScriptDirector.cpp:459`, and `:2057` in wait_collision), and `UpdateHeights`/`UpdateCollision` prune opportunistically (`RmcTerrainRenderer.cpp:280,440`). **Scatter needs its own tick** (the director's tick group is `TG_PostUpdateWork`, `TerrainScriptDirector.cpp:259-261`; use the same) because interactive and packaged play has no director.

### 1.2 The chunk grid and what each event delivers
Grid: E = 160 gives 321 x 321 vertices (`Data/TerrainHeightfield.h:60,69-85`), 640 x 640 splat texels at 0.5 m (`SplatTexelsPerMeter = 2`, `:14`), chunks of C = 64 (25 chunks) or 32 (100) quads that RENDER (C+1)^2 vertices and OWN a disjoint block (`TerrainHeightfield.h:62-64`, `.cpp:164-182`). Options clamp `-ChimeraTerrainHalf` 16..512 and `-ChimeraTerrainChunk` 8..256 (`Game/ChimeraTerrainGameMode.cpp:22-23`).

| Event | Rect it carries | Semantics that matter to scatter |
|---|---|---|
| per tick | `FTerrainTickResult.HeightRect` (vertices whose bytes changed, tight bounding rect) and `.SplatRect` (texels whose bytes changed) (`Data/TerrainBrush.h:44-54`, filled `TerrainBrush.cpp:220,136,219-221`) | one footprint: at d100 a 103-104 vertex square / 203-204 texel square (`TerrainBrush.cpp:144-145,110-112`); empty for a tick that changed nothing; paint ticks have an empty HeightRect, height ticks an empty SplatRect |
| stroke end | `StrokeHeightRect`/`StrokeSplatRect` = union of every tick rect (`TerrainActor.cpp:237-238`) | bounding box of the whole path: over-covers diagonals (S1's dirt path: 24 of 100 32 m tiles under the box vs 11 really touched, `scratchpad/tiles.py`, my computation) |
| undo / redo | `FTerrainEditDelta`: union of the OWNED chunk rects restored (`Data/TerrainUndo.cpp:219-236`); `Undo.Undo/Redo` already wrote the arrays | chunk-aligned, whole-chunk granularity; only the arrays the entry kept (height and/or splat) |
| load | none (whole field replaced: `HF = MoveTemp(Loaded)`, `TerrainActor.cpp:378`) | any raw pointer or `TArrayView` into `HF.Heights`/`HF.Splat` taken before is dangling afterwards |
| init | none | `HF.Init` -> all zero height, pure grass (`TerrainHeightfield.cpp:74-100`) |

Dirty-rect arithmetic that already exists and scatter should reuse: `ChunksRenderOverlappingRect` (rect grown by 1 vertex for normals, `RmcTerrainRenderer.cpp:282-284`), `VertexRectToSplatRect` (`TerrainHeightfield.cpp:184-193`), `ChunksOwningSplatRect` (texel T belongs to vertex T/2, `:228-239`).

### 1.3 Height samplers
| Function | Where | Use for scatter |
|---|---|---|
| `FTerrainHeightfield::SampleSurface(double X, double Y)` | `Data/TerrainHeightfield.h:112`, `.cpp:137-162` | **Z of every instance.** Triangulated surface, BL-TR diagonal, clamped to the map: exactly what the mesh draws (matching vertex order `RmcTerrainRenderer.cpp:162-172`) and what the pick and `depthcheck` agree with (P11). Never the sim's nearest-cell lookup (`TerrainHeightfield.h:107-111`, PC §3.6) |
| `GetHeight(X,Y)`, `GetHeightClamped`, `Heights` (public `TArray<float>`, row-major `Y*Width+X`) | `.h:88-90,145` | slope from the cell's two triangles; no smoothing kernel needed |
| `GetNormal`, `GetTangent` | `.h:103-105` | central differences, 8-neighbour dependence: avoid for acceptance (it widens the dirty apron by one more vertex); the face normal of the cell triangle depends on the 4 cell vertices only |
| `GetChunkZRange`, `ChunkOwnedRect`, `ChunkRenderRect` | `.h:121-136` | not needed (scatter owns its tile grid) |
| `TerrainPick::RayCast` | `Data/TerrainPick.h:30` | not needed |

### 1.4 The splat texture and its CPU copy
`HF.Splat` (public `TArray<uint8>`, RGBA, channel = layer id 0 Grass 1 Dirt 2 Rock 3 Snow, every texel sums to exactly 255, `TerrainHeightfield.h:11-12,146-147`; invariant enforced by `PaintTexel` `TerrainBrush.cpp:20-86` and by `TerrainIO::Load` `TerrainIO.cpp:262-270`) is the authority. `FTerrainSplatTexture` (`Render/TerrainSplatTexture.h:33-47`) holds the `UTexture2D` (`TStrongObjectPtr`, `:44`) and counters only; it is **never read back**, so scatter must read `HF.Splat`. The texture object survives `LoadFrom` (`Splat.UpdateAll`, `TerrainActor.cpp:385`, no re-`Create`), so a scatter material's `Splat` parameter set once stays valid. Texel centre formula: `TexelCenterToWorld` (`TerrainHeightfield.h:97`); a bilinear splat read at world (x,y) touches 2x2 texels, so a splat apron of 1 texel (0.5 m) per kernel radius.

Under `-nullrhi` the texture has no resource and uploads are skipped (`TerrainSplatTexture.cpp:29-36`, `bNoRender`). Scatter should copy that pattern: with `!FApp::CanEverRender()` run the generator, the hashes and the tests, create no components.

### 1.5 Undo, redo, load
`UndoLast`/`RedoLast` (`TerrainActor.cpp:331-351`) call `ApplyDelta` (`:317-329`): `Renderer->UpdateHeights` (:321), `RecomputeBounds` (:322), `SubmitCollision` (:324), `Splat.UpdateRect` (:327). `LoadFrom` (`:362-387`) calls `RebuildAll` (:382), a full-map `SubmitCollision` (:384) and `Splat.UpdateAll` (:385) and clears the undo stack (:379). `SaveTo` (`:353-360`) writes only `terrain.json`, `height.r32`, `splat.rgba8`; scatter is **derived data and must not be written into them** (`TerrainIO::Load` validates `axes` and three FNVs, `TerrainIO.cpp:207-290`); its hash lives in `results.json`.

### 1.6 Director, metrics, parser, scripts
Director ops and their relevance (`Test/TerrainScriptDirector.cpp`; op list `StepOp` `:530-570`, header doc `.h:6-62`):
- `visible 0|1` (`:612-619`) hides terrain chunk components only (`FRmcTerrainRenderer::SetVisible`, `RmcTerrainRenderer.cpp:361-370`); P4's terrain GPU is visible minus hidden at idle (`parse_terrain.py:1046-1064`; C1 phases `idle_visible`, `idle_hidden`, `idle_visible2`).
- `settle` waits `CompileQueuesIdle() && !Terrain->HasPendingWork()` (`:626`), `shot` waits `Terrain->HasPendingWork()` then 2 frames (`:801-809`), `depthcheck` waits both (`:1517`). **Adding scatter work to `ATerrainActor::HasPendingWork` (`TerrainActor.cpp:413-416`) makes all three wait for it with no director change.**
- `hash` (`:773-789`) computes height, splat and sim-grid FNV synchronously on the op's first frame; `load` (`:1108-1132`) and `undo`/`redo` (`:1045-1066`) are synchronous too; S1L runs `load` then `hash` (`Scripts/S1L.json`). A scatter hash must therefore be computable *synchronously from the heightfield* (the reference hash), not read from asynchronous state.
- `depthcheck` (`:1481-`) captures scene depth with `PRM_UseShowOnlyList` and `ShowOnlyActors.Add(Terrain.Get())` (`:1509-1510`); the renderer adds every primitive component of an actor in that list (`E/Source/Runtime/Renderer/Private/SceneCaptureRendering.cpp:658-668`). Scatter components under `ATerrainActor` would enter the depth image and break P11; a separate actor does not.
- `verify_collision`/`wait_collision` trace `ECC_Visibility` (`:2007-2045`, `:2162-`): scatter must be `NoCollision` or every ray count changes.
- `look compare|full` (`:587-610`) toggles clouds, exposure, ring and HUD; `csv` (`:1339-1424`) and `movie` (`:1426-1479`) are independent of scatter. `csv` is `#if CSV_PROFILER` (`:1342`): scatter metrics must also go to `results.json`, not only to CSV categories (Shipping may compile them out, PC §4 C11).
- Frame metrics: `FTerrainMetrics::SampleFrame` (`Test/TerrainMetrics.cpp:83-99`, called `Director.cpp:454`) records frame, GT, RT, GPU ms per phase; tick timings `FTerrainTickTiming`/`FTickSeriesSet` (`Game/TerrainActor.h:20-30`, `TerrainMetrics.cpp:167-195`); `results.json` assembled in `Finish` (`:2477-2605`: options :2493-2501, render :2509-2523, hashes :2532).
- The log scan (`tools/unreal-trial/logscan.py`) fails any `LogChimera*: Error`, `Failed to load '/`, `LogStreaming/LogLinker: Warning`, `LogUObjectGlobals: Warning: Failed to find`, ensures: scatter must log under `LogChimeraTerrain`, load assets with quiet flags, and treat a missing species mesh as one explicit `Error` line only when scatter was explicitly requested.

Parser: `parse_terrain.py` `s1` (`:399-486`), `s1l` (`:488`), `same_hash` (`:530`, P8), `config_key` (`:717-747`), `summary` (`:829-`), P3 (`:1022-1044`), P4 (`:1046-1064`), `c7` (`:1163`), `DEFAULT_CONFIG` (`:96`), `NON_CONFIG_OPTS = {Script, Out, Load, Units}` (`:98`).

Scripts as they stand: S1 is all compare mode with image bars that tolerate no extra noise (`Scripts/S1.json`; A/A floor `worst16` 0.0011-0.0014 against the 2/255 = 0.0078 bar, `evidence/c/c-C5-p7-noise.txt`); C1 phases `idle_visible`, `transition`, `idle_hidden`, `idle_visible2`, `walk`, `idle_after` (`Scripts/C1.json`); SOAK is a 5-minute walk (`Scripts/SOAK.json`). **No `Units` code, `C1U.json` or `VIDEO.json` exists yet** (grep of `S` and `Scripts/`): C9 is unwritten, which is the cheapest moment to share the change event with it.

### 1.7 Packaging and C9's plan for units
C11 (`PC` lines 352-359): `Tools/package.ps1` does not exist yet. Cook of `/Game/Terrain` is forced (`Config/DefaultGame.ini:5-6`, `+DirectoriesToAlwaysCook`), so **anything under `/Game/Terrain/Scatter/` is cooked with no ini change**; the ground material is already referenced by a C++ string path and cooked this way (`TerrainActor.cpp:24`). Options come from `FCommandLine::Get()` (`GameMode.cpp:80-82`), which the packaged exe also has (C11 passes `-ChimeraTerrain*` to it); `chimera.terrain.*` cvars exist but Shipping console paths may be compiled out (PC C11), so behaviour switches must be command-line options, with cvars only as extra dev knobs.

C9 (`PC` lines 333-339) plans `Game/TerrainUnits`: an ISM of `crucible_mortar` "riding a bilinear height sample, updated only where terrain changed". That needs the same dirty-rect event. `HF.SampleSurface` (triangulated) already exists and is what the mesh draws, so units should use it rather than a new bilinear sampler (a bilinear surface differs from the drawn triangles by up to slope x 0.5 m).

## 2. Hook list (what scatter must hook, in order of a stroke's life)

| # | Event | Hook point | What scatter does |
|---|---|---|---|
| H1 | init | `Game/TerrainActor.cpp:171-190` (after `Renderer->Initialize` :183); game mode creates the scatter actor after `InitTerrain` (`Game/ChimeraTerrainGameMode.cpp:108`) and before `Super::StartPlay()` (:132) and the director spawn (:136-137) | allocate tile table, create the actor and material instances, mark all tiles dirty (initial build) |
| H2 | stroke open | `TerrainActor.cpp:192-214` (`SetStrokeOpen(true)` :212) | `bStrokeOpen = true` (selects throttled follow-up) |
| H3 | each tick | `TerrainActor.cpp:216-281`: brush `:220`, height upload `:225`, splat upload `:234`, union `:237-238` (insert the call at `:237`, after both CPU arrays are final for this tick) | convert `R.HeightRect.Expanded(1)` and `R.SplatRect` (via `VertexRect` of the texel rect, grown by the splat apron) to tile ids, set dirty bits, record frame; O(tiles per tick) <= 25; no allocation |
| H4 | stroke end | `TerrainActor.cpp:283-300` (after `SubmitCollision` :297, before `Undo.EndStroke` :299) | clear `bStrokeOpen`, dispatch every dirty tile now (no throttle), start the latency stopwatch |
| H5 | undo / redo | `TerrainActor.cpp:317-329` `ApplyDelta` (end of function) | dirty tiles for `Delta.HeightRect.Expanded(1)` and `Delta.SplatRect`; priority dispatch |
| H6 | load | `TerrainActor.cpp:362-387` (after `Splat.UpdateAll` :385) | bump global epoch (drops every in-flight result), dirty all tiles, priority dispatch |
| H7 | visibility/ring | `TerrainActor.cpp:404-411` and `PushBrushRing` `:128-137` | optional: push `BrushX/Y/Radius` into scatter MIDs for a footprint mask (see §4.5); terrain `visible` itself does **not** toggle scatter (separate op) |
| H8 | pending work | `TerrainActor.cpp:413-416` | `|| Scatter->HasPendingWork()` (dirty tiles, running tasks, completed-unapplied results, unflushed render updates) |
| H9 | teardown | `TerrainActor.cpp:418-422` `EndPlay` | cancel/wait tasks, destroy components |
| H10 | frame tick | scatter actor's own `Tick` (`TG_PostUpdateWork`) | poll finished tasks, apply under budget, dispatch next tiles, update metrics |
| H11 | director frame | `TerrainScriptDirector.cpp:454-461` | sample scatter series next to `Metrics.SampleFrame` |
| H12 | director ops | `StepOp` `:530-570`, `StepHash` `:773`, `Finish` `:2477` | new ops and result blocks (§6) |

Implementation form: a native multicast `FOnTerrainChanged(const FTerrainChange&)` on `ATerrainActor` with `struct FTerrainChange { ETerrainChangeKind Kind; FTerrainRect HeightRect; FTerrainRect SplatRect; uint64 Generation; }`, `Kind` in {Init, Tick, StrokeEnd, Undo, Redo, Load}. Broadcast from H1, H3 (Tick), H4 (StrokeEnd, informational union), H5, H6; scatter and C9 units subscribe. H2/H7/H8/H9 are direct calls. No other code path changes.

## 3. Data scatter can read without copies, and the hazards

Readable in place on the game thread, no copy: `ATerrainActor::GetHeightfield()` -> `HF.Heights`, `HF.Splat`, `HalfExtentM()`, `Width()`, `SplatSize()`, `SampleSurface`, `GetHeight`, `SplatTexel`; `GetSplat().GetTexture()` for the material parameter only.

Hazards:
- `HF` is mutated in place on the game thread at tick time with no versioning (`TerrainBrush.cpp:219`); a worker reading `HF.Heights` races with a stroke. Any off-thread work must read a **snapshot taken on the game thread**: per 32 m tile, heights `35 x 35` floats (tile + 1 vertex apron low side, 2 high side) = 4.9 KB and splat `68 x 68 x 4` bytes (64 texels + 2-texel apron each side) = 18.5 KB, about 23 KB per tile; 25 tiles (a d100 tick) = 0.6 MB, about 0.1 ms of memcpy (my estimate, UNVERIFIED but memcpy-bound).
- `LoadFrom` replaces the arrays (`:378`): never cache `GetData()` pointers across frames.
- Float semantics: the Editor target compiles with `/fp:precise`, a Game target with MSVC defaults to `/fp:fast` (`E/Source/Programs/UnrealBuildTool/Platform/Windows/VCToolChain.cs:1313-1335`; the UBT log shows the Visual Studio 14.50 MSVC toolchain, not clang, `Log.txt` line 65). So `-game` (Editor-built module) and a packaged exe can disagree in the last bits of brush float maths, which PC C11 already anticipates (`max_abs_dh<=1e-4`). The scatter generator must therefore decide with **integers** (splat bytes, quantised slope, integer hash) so equal input bytes give equal instance lists in every build (§4.2).

## 4. Design that follows

### 4.1 Layout
- `Data/TerrainScatter.{h,cpp}` (pure C++, namespace `ChimeraTerrain`, beside the other Data files; includes only `TerrainHeightfield.h`; **must not include `TerrainSimExport.h`**): `FScatterConfig`/`FScatterSpecies`, `FScatterTileInput` (the snapshot), `FScatterInstance`, `FScatterTileResult`, `GenerateTile(const FScatterTileInput&, const FScatterConfig&, FScatterTileResult&)`, `HashTile`, `HashReference(const FTerrainHeightfield&, const FScatterConfig&)`, tile/rect helpers.
- `Render/TerrainScatterRenderer.{h,cpp}`: component pool, apply, visibility, MIDs.
- `Game/TerrainScatter.{h,cpp}`: `ATerrainScatter` (ticking actor), owns config, tile table, task state, metrics.
- Build.cs needs nothing new (ISM is Engine; `UE::Tasks` is Core, `ChimeraTerrain.Build.cs`).

### 4.2 Deterministic placement (constraint 3)
- Candidate identity `(species, ix, iy, slot)` where `ix = floor(worldX / CellM_species)`, world-anchored with the map origin at 0, so a `-Half 320` run produces the same instances over the overlap with `-Half 160`, and `-Chunk 32` equals `-Chunk 64`.
- All randomness is a 32-bit integer mixer of `(seed, species, ix, iy, slot)` (own `Mix32`; not `FMath::Rand`, `FRandomStream` or `HashCombine`, whose stability across engine versions is not ours to depend on). Jitter, yaw, scale, accept roll are bit fields of that hash.
- Acceptance uses integer inputs: bilinear grass weight from the four splat bytes in Q8, slope as the cell triangle's `N.z` quantised (Q10), height band in mm; thresholds are integers. Cluster fields (tree clumps, meadow patches) are integer value-noise on the world lattice, not float fbm.
- Density is applied by comparing the candidate's roll to the accepted-density value, so lowering density only removes candidates (monotone, stable under small edits, natural thinning without moving anything). Mirrors the material's rock rule: reuse `cos 35 deg = 0.8192` (`Scripts/make_ground_material.py`, `RockCos`; one shared constant plus a text test that the two agree, because the material script is a separate file).
- Z from the same arithmetic as `SampleSurface`: extract the body of `SampleSurface` (`.cpp:146-160`) into one inline helper that both call, and test equality on 10,000 random points against `HF.SampleSurface` (exact, the arithmetic is shared).
- Canonical hash record per instance: `species u8, ix i32, iy i32, slot u8, x_mm i32, y_mm i32, z_mm i32, yaw u16, scale u16`, folded with the existing `Fnv1aU32LE` (`TerrainHeightfield.h:24`) in sorted `(species, iy, ix, slot)` order, plus a per-tile hash and an order-independent sum so the global hash is independent of tile size. Z is included at 1 mm: it proves instances sit on the surface, and double-precision `SampleSurface` followed by an mm quantiser flips only for last-bit differences (negligible), so equal height bytes give equal scatter hashes across Editor and packaged builds.

### 4.3 Tiles, dirty tracking, rect arithmetic
- Tile = 32 m (100 tiles at E = 160; 400 at `-Half 320`). Independent of chunks. Tile key `(floor(x/32), floor(y/32))` clipped to the map. Rationale for 32 over 16 or 64: a d100 tick touches up to 25 tiles at 32 m (16 at 64 m is the best case, 25 the worst; computed in `scratchpad/tiles.py` for E = 160) but each rebuild is 4x cheaper than a 64 m tile and the ISM component count stays at about 100 x species rather than 400 x species. Measure before fixing (§11 Q3).
- Tile dependence on the heightfield: an instance in cell (cx,cy) depends on the 4 cell vertices, so a changed vertex v dirties cells `[v-1, v]`; a changed splat texel dirties the texels within the bilinear footprint. Apron: heights `Expanded(1)`, splat texel rect grown by 1 texel, then mapped to tiles. `ChunksOwningSplatRect`'s texel-to-vertex rule (`TerrainHeightfield.cpp:236-237`) is the model.
- **Accumulate dirty tiles per tick** (a 3-bit-per-tile flag array), not from `StrokeHeightRect`. Measured example (my computation, S1's four-point dirt path, d6, 60 ticks): 11 tiles touched vs 24 under the union box; the other S1 strokes are 4 to 8 tiles either way. Collision's over-submission on diagonals is existing behaviour and untouched.
- Skip-if-identical: the worker returns the tile's apply-hash (instance records incl. z in mm); the game thread compares with the last applied hash and leaves the ISM untouched if equal. A smooth or flatten tick that changes heights by 1e-6 therefore costs a generation but no render-state work.

### 4.4 Rendering side
- One `UInstancedStaticMeshComponent` per (non-empty tile, species), created lazily, kept (cleared, not destroyed) when a tile empties. Component at identity, transforms in world space passed with `bWorldSpace=false` (`R3` §4.2 line 269: the world-space path computes a relative transform per instance).
- Per-tile update: `ClearInstances()` (`E/.../InstancedStaticMeshComponent.h:431`), `PreAllocateInstancesMemory(n)` (:334), `AddInstances(Transforms, false, false, /*bUpdateNavigation*/ false)` (:275). In 5.8 the call tracks changes in `PrimitiveInstanceDataManager` and the proxy update runs at end of frame in `SendRenderInstanceData_Concurrent` (`E/.../Private/InstancedStaticMesh.cpp:2544-2566`), which also refreshes bounds (`UpdateBounds()`), so unlike RMC chunks no bounds management is needed. Keep `bMarkRenderStateDirty = false` anywhere it is offered (R3 line 262-263). Cost per instance is UNVERIFIED: the `apply_ms` series (§6) measures it.
- `SetCollisionEnabled(NoCollision)`, `SetCanEverAffectNavigation(false)`, `bUpdateNavigation=false`: scatter is presentation only and never an input to pathing (constraint 1). If trees must ever block movement they are scenario content owned by the sim, not scatter output.
- Culling/LOD: `SetCullDistances(Start, End)` (`:443`, properties :213-217) per species; `bUseGpuLodSelection` (:221) UNVERIFIED benefit.
- Mobility: terrain chunks are Movable so VSM re-renders their shadows (PC §2.9). Scatter ISMs change only at edits; **Static mobility lets VSM cache unchanged tiles** (ISM defaults to Movable, `InstancedStaticMesh.cpp:2360`; the `Optimized` tracking state applies only to cooked precomputed data, `ISMInstanceDataManager.cpp:704`, so runtime-built ISMs use the tracked path either way). Whether VSM invalidates edited-tile pages correctly for Static ISMs is UNVERIFIED: a `scatter_shadow` check (a tree-pool region before and after an edit) belongs in S1X; `-ChimeraTerrainScatterMobility=movable` is the fallback.
- Materials: scatter MIDs created by the scatter actor, `Splat` texture parameter set once from `Terrain->GetSplat().GetTexture()` (same pattern as `TerrainActor.cpp:82`) and `HalfExtentM`, so grass tint can follow the splat (colour-match to the ground) with the same world-position UV (`u = (X/100 + E)/(2E)`, PC §3.5). Wind, if any, is driven by a scalar parameter that compare mode zeroes (otherwise the A/A floor and P7 break).

### 4.5 Behaviour during a stroke
- Tick: mark only (H3). The p50 tick budget has little room at the big brush (C7 run: d100 total p50 0.967 ms against the 2.0 ms bar, d20 0.110 ms against 1.0, `evidence/c/c-C7-c1-mat-summary.txt`), so H3 must be a few microseconds: it is, since it only sets flags.
- Follow-up while the brush moves: the generator is off-thread and the apply is budgeted, so tiles can be refreshed during the stroke at most once per `ScatterDuringStrokeMs` per tile (default 150, `-ChimeraTerrainScatterDuringStroke=`; 0 = stroke end only, the same switch pattern as `chimera.terrain.CollisionDuringStroke`, `TerrainActor.cpp:31-36,240-251`). Recommendation: implement both, default to follow-up on (instances that float over a lowered brush are the first thing a player sees), and let C12 measure both with `walk` phase `frames_over_33ms`. Fallback if the p99 frame misses: stroke-end only.
- Optional cheap mask: scatter materials read `BrushX/BrushY/BrushRadius` and collapse instances inside the brush circle (WPO scale to 0). `ATerrainActor::PushBrushRing` (`TerrainActor.cpp:128-137`) is the existing place to set them; costs a WPO permutation, UNVERIFIED value.
- Stroke end, undo, redo, load: all dirty tiles dispatch immediately, nearest to the last brush centre first.
- Final state depends only on the final heightfield: intermediate lag is time-based and may differ run to run, so hashes are gated only at settled checkpoints (independence from frame rate holds at every checkpoint, P8-style).

## 5. Threading options

| Option | What | Verdict |
|---|---|---|
| A. All on the game thread, synchronous | generate and apply in `ApplyTick`/`EndStroke` | no: one d100 tick can dirty 25 tiles, about 1 ms each (my estimate, UNVERIFIED) = 25 ms in the tick, 25x P1's p50 bar; hitches |
| B. All on the game thread, time-sliced | queue tiles, generate and apply a few per frame | simple and race-free but burns the 1.0 ms game-thread budget on pure maths; viable as the fallback if the task graph misbehaves |
| **C. Snapshot on GT, generate on the task graph, apply on GT under a budget (recommended)** | `UE::Tasks::Launch` (`E/Source/Runtime/Core/Public/Tasks/Task.h:299`), priority `BackgroundNormal` (`.../Async/Fundamental/Task.h:28`), concurrency capped at 2-3 with `FTaskConcurrencyLimiter` (`.../Tasks/TaskConcurrencyLimiter.h`) so the 6-core 5600 keeps the render and RHI threads fed (and ShaderCompileWorker at warm-up); one in-flight task per tile, a per-tile generation counter so a result is applied only when no newer dirt arrived (otherwise re-dispatch), a global epoch for load | the generator touches only the snapshot and the config (both immutable), writes only its own result; the game thread owns every UObject and every ISM call, as ISM requires |
| D. Apply off the game thread | | impossible: `AddInstances` and component creation are game-thread UObject operations |
| E. GPU generation (PCG GPU, compute) | | PCG graphs are binary assets (violates text-first, constraint 4); R3 §4.4 lists a GPU-only instance mode for instanced skinned meshes, and the static ISM header has none (only `bUseGpuLodSelection`, `InstancedStaticMeshComponent.h:221`) |

Details of C: results are plain structs behind a `TSharedPtr` captured by value, so `EndPlay` can drop the actor without a dangling reference; on teardown the actor waits the outstanding handles. Application order cannot change the final state (tile results are independent), so the reference hash is order-free. Apply budget: `FPlatformTime`-checked, default 1.5 ms per frame with a guarantee of at least one tile per frame so the queue always drains; the in-frame cost is the measured `scatter_apply_ms` series. In `-UseFixedTimeStep` runs (VIDEO, FixedFps) the budget is wall time, so intermediate frames can differ run to run; only checkpoints are compared.

## 6. Test hooks and the changes to the harness

### 6.1 Hashes, counts, latency (all in `results.json`, none in the saved terrain files)
| Hook | Definition | Gate or report |
|---|---|---|
| `scatter_fnv` (**reference**) | `HashReference(HF, config)`: synchronous full regeneration of every tile from the current heightfield in the calling thread, canonical order (§4.2). Independent of tiles, streaming, async state | the identity hash |
| `scatter_live_fnv` | same fold over the instance lists currently applied to the ISMs (applied-hash per tile) | gate: equal to the reference at every settled checkpoint (proves edits are followed) |
| `scatter_count`, `scatter_counts` | per species, and per tile `{tx, ty, counts[]}` plus per terrain chunk id (tiles aggregated by centre) in `scatter_counts.json` | report; parser asserts thinning below |
| `scatter_verify` op | reads back the ISMs (`GetInstanceTransform`): count and every transform equal the live lists; `|z - HF.SampleSurface| <= 0.5 cm` for every instance; none inside a tile that is still dirty | gate (surface contact on the edited terrain) |
| `scatter_check {fp:<set>}` op | counts instances inside the director's footprint discs (`FootprintSets`, `TerrainScriptDirector.h:204`) of the painted-path, rock and snow strokes versus an equal-area control disc on grass; instances on cells with N.z < 0.8192 | gate: density in path/rock/snow discs <= 0.15 x control; zero instances above the slope limit; snow cell density 0 |
| `scatter_latency[]` | per flush: `reason` (stroke_end, undo, redo, load), tiles, ms and frames from H4/H5/H6 to "all tiles current and render-flushed", worker generation ms total/max, GT apply ms total/max | report; proposal: p95 <= 30 frames for a d100 stroke end, no single apply frame > 4 ms |
| `scatter_apply_ms`, `scatter_gen_ms`, `scatter_queue_max`, `scatter_tiles_rebuilt`, `scatter_skipped_identical` series | `FTerrainSeries` (`TerrainMetrics.h:12-24`), also per phase like `FramesToJson` | report |
| tick mark cost | `ScatterMarkMs` in `FTerrainTickTiming`, excluded from `TotalMs()` (P1) and reported beside it so the P1 bar is unchanged | report |

Hash gates: `undo == pre_last2` and `redo == after` using the **reference** hash; `S1XL` (load of `s1x_a`'s files) equals `s1x_a` final; P8: `s1x_a` vs `s1x_b` (`-MaxFps 20` + 300 ms hitch) equal at every shared hash name (`same_hash` `:530` extended to scatter keys when both runs have them); packaged: **if `height_fnv` equals the reference run's, `scatter_fnv` must equal too** (same bytes in, same instances out, build-independent by §4.2); if the heights differ by the known compiler cause, report.

### 6.2 New ops (director, `StepOp` `:530-570`)
- `scatter {value: 0|1}`: enable/disable; off hides every ISM, suspends generation (dirt accumulates), costs nothing; on flushes. `visible` stays terrain-only so every existing mask and P4 computation is unchanged.
- `scatter_verify {name}`, `scatter_check {name, fp}`, `scatter_counts {name}`: as above, results into `results.json` `scatter.{verifies,checks}` and `scatter_counts.json`.
- `hash` (`:773`) gains `scatter_fnv`, `scatter_live_fnv`, `scatter_count` when scatter is enabled (reference computed synchronously; if scatter is off the fields are absent, so old scripts write the same JSON).
- `settle`, `shot`, `depthcheck`: unchanged, wait through `HasPendingWork` (H8).
- `look compare|full`: unchanged, except `Lighting`/`Post` stay as is; compare mode runs with wind scalar 0.
- `results.json`: `options.scatter` (mode, seed, tile_m, during_stroke_ms, mobility, species list with mesh paths and asset hashes), `scatter` block (tiles, counts, hashes, latency, series, apply/gen totals, ISM component count, instance count, proxy status).
- Command line: `-ChimeraTerrainScatter=0|1|<preset>`, `-ChimeraTerrainScatterSeed=`, `-ChimeraTerrainScatterDuringStroke=`, `-ChimeraTerrainScatterMobility=static|movable`, `-ChimeraTerrainScatterDensity=` (a scalar on every species, for the budget knob). Parsed in `FChimeraTerrainOptions::FromCommandLine` (`GameMode.cpp:17-55`) with `FParse::Value(..., false)` (EX §2.2); defaults: off when `-ChimeraTerrainScript` is present, on otherwise.

### 6.3 Scripts (new, existing ones untouched)
- **S1X** = S1's edit sequence (so the heights and splat equal `s1_a`; height and splat hashes must equal C4's, proving scatter touched no terrain data) with `scatter 1` after the first `look full`, compare-mode shots with scatter off for the existing image bars where they still apply, and scatter-on shots of the same states for the new checks: at `pre_last2`, `after`, `undo`, `redo`: `settle`, `scatter_verify`, `hash` (reference and live), `scatter_check` (fp sets `paint` and `last2`), plus `scatter_counts`. The Alec-facing look shots (`rts80_full`, `closeup`, `oblique`) come from here.
- **S1XL**: `load` of S1X's saved files, `hash`, `scatter_verify`, shot `redo`.
- **C1S**: C1 with `scatter 1`; phases `idle_scatter_on`, `idle_scatter_off` (same camera, terrain visible both times), then `walk`. Reports scatter GPU/GT/RT = on minus off (new `P4S` row), the `walk` frame statistics with scatter on (`frames_over_33ms`, `max`), apply/gen series, latency.
- **C1US**: C1S with `-ChimeraTerrainUnits=1000` once C9 exists: the P3 numbers with scatter on, printed as REPORT rows with the same thresholds so the pass/fail is visible without being a gate (EX D2 reports the trees-and-scatter scene; Alec may gate it).
- **SOAKS**: SOAK with scatter on, reported (memory growth with ISM churn, peak RAM).
- **G1**-style shadow check inside S1X: region of a tree shadow pool before/after an edit (`scatter_shadow`), because VSM invalidation for edited ISM tiles is UNVERIFIED.
- Phase names are new (`idle_scatter_*`), so `parse_terrain.py:1046-1064` (P4) keeps reading only `idle_visible*` and `idle_hidden` and the gate configuration is unchanged.

### 6.4 Parser changes (`Tools/parse_terrain.py`)
`--scatter RUN` (like `--c7` `:1163`): log scan, completion, then the bars above; `--s1x` aggregates S1's hash bars (heights equal `s1_a`), P11 depth bars (they still pass because scatter is a different actor), and the scatter bars; `same_hash` extension; `summary` adds a scatter table (instances, tiles, ISM count, apply p50/p95/max, gen p50/p95, latency, queue max) and the `P4S`/C1US rows. `config_key` already splits runs by `-ChimeraTerrainScatter...` (`:730-735`); add `ScatterSeed`/`ScatterDuringStroke`/`ScatterMobility` defaults to `DEFAULT_OPTS` (`:101`) only if they should keep a default run in the gate configuration. `STANDARD_EXEC` (`:97`) unchanged.

### 6.5 Automation tests (pure data, run under `-nullrhi` like `Tests/TerrainDataTests.cpp`, `TT_TEST` macro `:28-31`) `Chimera.Terrain.Scatter.*`
Same heightfield twice -> same hash; hash independent of tile size (16/32/64) and of `ChunkQuads` (32/64); same instances over the overlap of E = 160 and E = 320; incremental edit (random stroke list) equals full regeneration, at every step; undo restores the earlier reference hash; zero instances where the grass weight is below the threshold, on slopes above the limit, in the snow layer; every instance Z equals `HF.SampleSurface` (shared arithmetic, exact); generator leaves the heightfield bytes unchanged (HF hash before/after) and the module includes no sim header; ISM-side apply under `FTestWorld` (`Tests/TerrainRenderTests.cpp:86-123`) with `CanEverRender` false creates no components but keeps the counts; text test that scatter's slope constant equals `RockCos` in `make_ground_material.py`.

## 7. Packaging (C11) and Content reproducibility
- Cook: place meshes, materials and textures under `/Game/Terrain/Scatter/...`: covered by `DirectoriesToAlwaysCook=/Game/Terrain` (`Config/DefaultGame.ini:6`). Meshes are loaded by C++ string path like the ground material (`TerrainActor.cpp:24,72`), which is exactly why the always-cook line exists (nothing references them from a cooked map). Use quiet loads and a single `LogChimeraTerrain` Error naming the missing species, never `Failed to load '/`-style log lines unless the load truly failed.
- Config: species table in C++ with optional overrides from `Config/DefaultGame.ini` `[/Script/ChimeraTerrain.TerrainScatter]` read like `ATerrainLighting::ReadConfigLookOverrides` (`TerrainLighting.cpp:187-195`). Whether the packaged build reads the staged `DefaultGame.ini` that way is **UNVERIFIED until C11's first run** (the LookOverrides precedent has never been packaged); the command-line switches do not depend on it.
- Assets (constraint 4 and 5): source files in `T/ScatterSrc/<id>/` fetched by `Tools/fetch_scatter_assets.py` (the `fetch_textures.py` pattern: md5/sha256 verification, `manifest.json` with id, URL, licence name and URL, date, hashes), imported and materials built by `Scripts/make_scatter_assets.py` through `Tools/run_commandlet.ps1` (`-AllowCommandletRendering`, config-rewrite guard, sentinel `SCATTER_OK`), the same way as `make_ground_material.py`. `T/ScatterSrc/` needs one `U/.gitignore` line (the S2 edit is the main session's, EX §2.1 S2); `sync_to_repo.sh` already mirrors `Tools/` and `Scripts/` but not `ScatterSrc/` (`ITEMS`, `sync_to_repo.sh:8`), so the manifest is copied into `EV/c` with `evidence.py add`, as the texture manifest was.
- Warm-up: a new material means shader compilation on first run (`settle` already waits the compile queues, `Director.cpp:371-376`; C7 ran one warm-up per material change, PC C7). Budget a 60-minute warm-up hold.
- Packaged runs: `S1X` and `S1XL` with `-Packaged` (Development) and `-Shipping`; the Shipping rule is the existing one (hashes in `results.json`, `after.png`); scatter metrics must not use CSV-only paths (`#if CSV_PROFILER`). The three counters `splat.submits/cleanups` pattern (`results.json splat`) has an equivalent here: `scatter.tasks_dispatched == tasks_applied + tasks_discarded`, gated equal after settle.

## 8. Budget numbers on this machine
From the only measured idle run with the current ground material (`evidence/c/c-C7-c1-mat-summary.txt`, `c1_mat`, not a `-Measure` run, so reported): `idle_visible` 83.6 fps, frame 11.96 ms, GPU p50 11.63 ms, GT 2.15 ms, RT 11.94 ms, 1% low 74.6 fps, `idle_hidden` GPU 8.63 ms, so terrain 3.0 ms (EX §8 records 2.96 ms). 60 fps median needs 16.67 ms: about **5.0 ms of GPU headroom** shared by scatter, 1,000 units, sculpting and everything the ground-look pass adds. Proposed scatter allocation, reported not gated: GPU <= 2.5 ms at `rts80`, average GT <= 1.0 ms, apply slice <= 1.5 ms per frame (all UNVERIFIED targets). The `rts80` camera sees nearly the whole 320 m map (hFOV 107.5 deg, centre ray hits y = 0, bottom ray y = -48.7, top ray reaches y = 225 m, past the map edge), but ground within 100 m of the camera only reaches y = +27.6 m (120 m: +51.8, 150 m: +85.5; camera at (0,-51.4,61.3) m, my computation), so per-species cull distances of about 100-150 m cut the visible instance count roughly in half to two thirds; density scale is the second knob. For scale, the look-test A_noLumen scene already drew about 900 Nanite scatter meshes at 76.2 fps (R3 line 111).
Instance volume estimates: the whole map at 1 grass clump/m^2 would be 100k instances (about 4 k per 32 m tile), trees 0.02/m^2 about 2 k; ISM memory is tens of MB (UNVERIFIED), irrelevant against 16 GB; the cost that matters is draw/GPU work and per-primitive overhead (about 100 x species primitives).

## 9. Risks
1. **Image bars.** Thin geometry plus TSR may raise the A/A floor and make P7 fail in S1X. Mitigation: scatter is off in S1/S1L; S1X uses the same statistic, measures its own A/A floor and wind is zero in compare mode; if the floor itself fails with scatter on, the bar is not loosened silently: the numbers go to Alec as a reported result and a static (non-TSR-sensitive) species set is tried.
2. **VSM and Static ISM edits** (UNVERIFIED), see §4.4 test and fallback.
3. **ISM edit cost per instance and per component** (UNVERIFIED): measured by `scatter_apply_ms`; levers are tile size, density, adaptive budget, skip-if-identical.
4. **Task graph pressure on a 6-core machine while the render thread is near 12 ms**: concurrency limiter 2-3, background priority, measured by RT/GPU phase stats; fallback option B.
5. **Terrain GPU bar** (2.96 of 3.0 ms): unaffected by design (scatter off in the gate configuration; scatter in its own actor and phases), but the full-game budget is tight (§8).
6. **Hash divergence between Editor-built and packaged builds** from `/fp:fast` vs `/fp:precise`: addressed by integer decisions; residual: a terrain-byte difference (already in C11's rule).
7. **Material script drift**: slope and rock constants live in two languages; text test.
8. **Concurrent edits to `GameMode` and `Config/DefaultGame.ini` by the ground-look workflow**: land the scatter hooks after it commits, in new files where possible (the only edits in existing files are the six hook lines in `TerrainActor.cpp`, the options fields and parse lines in the game mode, the three result fields in `Finish`, the new ops in `StepOp`).

## 10. Suggested task slicing (for the plan owner)
Each is its own implementer task with the lock only for builds and runs:
- **SC0** asset manifest and import script (depends on the asset research; no lock for code).
- **SC1** `Data/TerrainScatter` generator, hashes, `Chimera.Terrain.Scatter.*` tests (sonnet high; lock for build and `run_tests.ps1`).
- **SC2** `ATerrainScatter`, renderer, change event, hooks H1-H10, options, `HasPendingWork` (opus medium: concurrency; lock).
- **SC3** director ops, metrics, `results.json`, parser rows, S1X/S1XL/C1S/SOAKS scripts (sonnet high; lock for runs).
- **SC4** C11 and C12 amendments: `pkg_s1x`, `pkg_s1xl`, C12 gains 3 reps C1S at the chosen chunk size, 1 C1S at `-Half 320`, C1US reps, SOAKS (Phase 4 measure window, EX §1.2).
Plan C §7 lists "Foliage and grass scatter" as out of scope; the plan needs one line amended to record Alec's approval ("Go with what you think is best") and the reported-not-gated status (EX D2).

## 11. Open questions
1. **Mid-stroke follow-up default** (my recommendation: on, 150 ms, 1.5 ms apply budget; fallback stroke-end only after C12 numbers).
2. **Static vs Movable ISM** for VSM caching: needs the `scatter_shadow` check; no way to decide from source alone.
3. **Tile size** 16/32/64: ISM component count versus rebuild granularity; measure with C1S.
4. **Nanite vs plain ISM** for foliage cards and WPO wind (Nanite foliage and WPO cost are the assets researchers' call); integration works for either, but a masked non-Nanite species adds overdraw to the GPU budget.
5. **Should S1's own look-full shots include scatter?** I recommend no (S1 stays the bars script; S1X makes the look composite); say if the C7 composite must always show scatter.
6. **Does Alec want C1US gated?** EX D2 says reported; the shipped game has scatter on, so the gated 60 fps / 1%-low 45 figures describe a scene without it.
7. **Brush-footprint WPO mask** (cheap, but another shader permutation): worth it or not.
8. **Packaged `DefaultGame.ini` read** for species overrides (UNVERIFIED until C11).
9. **Units sampler**: plan C §4 C9 says "bilinear height sample", while `SampleSurface` is the drawn triangulated surface; recommend C9 uses `SampleSurface` and subscribes to the same change event. Needs the plan owner's nod.
10. **Where scatter-affecting constants live** (slope limit, grass-weight thresholds) so the material and the generator cannot drift: a shared C++ header read by a text test, or generate the material scalar from the header at commandlet time.
11. **Material colour match** between ground and grass (scatter material sampling the splat, or a baked ground-colour texture): depends on the ground-look result.
