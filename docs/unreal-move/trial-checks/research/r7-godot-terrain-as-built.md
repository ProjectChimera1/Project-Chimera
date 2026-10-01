# R7 - Godot terrain as built (data model, editing tools, sim coupling)

Purpose: give the Unreal runtime-terrain trial (check c) the exact as-built Godot contract to match.
Date: 2026-10-01. Read-only research; no project file edited. Paths are under `D:/Projects/Project_Chimera/godot/` unless
noted. Line numbers are 1-based in the files as read on master (clean tree).

Evidence marks: **[code]** read in this repo; **[live]** measured by a throwaway Godot 4.6.3 headless probe (scratch project
built from the repo's own `terrain_3d` DLL; scripts in the session scratchpad `t3dproj/probe*.gd`, not in the repo);
**[upstream]** Terrain3D v1.0.1-stable C++ source from GitHub raw, fetched 2026-10-01; **UNVERIFIED** = not confirmed.

## 0. Ten facts that matter most

1. Terrain is **Terrain3D 1.0.1** (GDExtension, MIT) at `addons/terrain_3d` (`addons/terrain_3d/plugin.cfg:6`). Chimera's wrapper is
   three presentation files: `TerrainPhase` (create), `TerrainBrush` (edit), `ScenarioLoadPhase` (restore + feed the sim). [code]
2. **Every shipped map is flat.** All 10 scenario JSONs in `resources/data/scenarios/` have `"terrain_ref": ""`, no
   `pathability_blocked`, no `water`, no `props`, no slope config (section 6). Sculpted terrain exists only as code paths and unit
   tests; there is no legacy terrain content to migrate.
3. The sim never touches the terrain engine. It consumes **two load-time grids**: `ElevationGrid` (256x256 cells, 1 m, +-128,
   `Fixed` heights) and `PathabilityGrid` (128x128 cells, 2 m, +-128, bool). Both are Godot-free C#. They are the whole interface
   Unreal must satisfy. `ElevationGrid` feeds the per-entity `Elevation[]` array, which **is in `SimChecksum`** (v15).
4. Terrain content is **not** in the lobby hash: `TerrainRef` is neutralised to `""` in `CanonicalModelHash`
   (`src/Core/Definitions/CanonicalModelHash.cs:226-237`). Painted pathability, slope config and water/prop footprints **are**
   folded (`:239-258`).
5. Terrain data is stored as Godot binary resources `terrain3d[_-]XX[_-]YY.res` (magic `RSCC`), one per 256x256 region, beside the
   scenario JSON and zipped under `map/terrain/` in `.chimera.zip`. **Unreal cannot read them**; a converter or a new on-disk
   format is needed.
6. **The default flat region is NOT centred on the origin.** Live probe: after `TerrainPhase`'s import, `get_region_locations()` is
   `[(-1,-1)]`, i.e. world X,Z in [-256,0). World (+100,+100) has no region (`get_pixel` returns NaN). The code comment says
   "centred on the world origin, +-128" (`TerrainPhase.cs:49-50`); that is wrong for the data. Terrain3D's `world_background`
   defaults to FLAT, so outside regions still renders as a flat plane. [live][upstream]
7. **Two of Chimera's five brush modes are silently broken and the cause is found.** `TerrainBrush` passes operation ints 4
   (REPLACE) and 5 (AVERAGE) from a pre-1.0 enum; in 1.0.1 the enum is ADD=0, SUBTRACT=1, REPLACE=2, AVERAGE=3, GRADIENT=4.
   So Paint sends GRADIENT (a no-op for the texture tool) and Smooth sends 5 (out of range, no-op). Raise/Lower/Flatten work.
   This is the real root cause of backlog story 10-16 ("texture paint write broken"), previously blamed on the GDExtension. [live][upstream]
8. Brush maths (1.0.1): `size` is a **diameter in metres** (Chimera's tooltip says radius, wrong); `strength` is a percent
   (`x0.01`) applied **per `operate()` call**, so Godot sculpt speed depends on mouse-event rate, not on time. At size 20,
   strength 10, one raise call adds +0.10 m at brush centre, falling off linearly to the edge. [live][upstream]
9. In Godot, **units and buildings do not follow the terrain visually**: sim `Position.Y` is always `Fixed.Zero` and no bridge adds
   `Elevation` (`src/UI/MultiMeshBridge.cs:203-206`; `src/Core/Sim/ScenarioApplier.cs:159-163`). Elevation drives only the fog
   vision bonus, the checksum and slope blocking. Unreal terrain-following is new behaviour, not a port.
10. The sim's elevation/slope grids are built **once at load**. Sculpting in Edit mode then pressing F5 does not rebuild them
    (`src/Core/MainScene.cs:3017-3037`, comment "terrain re-bake is out of scope"). Check (c) "edit while the game runs" has no
    Godot precedent for feeding edits back into the sim.

## 1. The terrain plugin and Chimera's wrapper

| Piece | Where | Notes |
|---|---|---|
| Terrain3D 1.0.1, GDExtension | `addons/terrain_3d/` (`plugin.cfg:6`, `terrain.gdextension:1-3`) | Needs Godot >= 4.4. Windows DLLs in `bin/`. Addon GUI scripts in `src/`, `menu/`, `tools/` are the editor plugin; Chimera does not run them at runtime. |
| Create | `src/Core/Bootstrap/Phases/TerrainPhase.cs:20-46, 52-92` (phase position 5) | `ClassDB.Instantiate("Terrain3D")`, `region_size=256` (`:67`), flat 256x256 `Rf` image imported at `(-128,0,-128)` (`:70-82`). Falls back to a `PlaneMesh` + grid shader if the extension is missing (`:26-43`). |
| Edit | `src/CreationSuite/TerrainBrush.cs` (876 lines; phase 11, `TerrainBrushPhase.cs:133-146`) | Drives `Terrain3DEditor` by dynamic dispatch (no C# bindings). Edit mode only (`:162, :212`). |
| Restore | `src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:146-192` | Sets `data_directory` (clear to "" first, then path) when the `TerrainRef` folder holds `.res` files. |
| Sim feed | `ScenarioLoadPhase.cs:216-285` (elevation), `:301-336` (pathability) | Section 7. |
| Save/export | `src/Core/Bootstrap/Phases/WinConditionPhase.cs:501-550` | `data.save_directory(<scenario stem>_terrain/)`. |
| Package | `src/Core/Definitions/ContentPackager.cs:185-206, 874-890`; `ContentPackageManifest.cs:127-140` | `terrain_files[]`, `terrain_hash` (FNV-1a over name+bytes, 0 = none). |
| Nav bake | `NavigationPhase.cs:43-49, 168-191`; `src/UI/NavObstacleManager.cs:35-45, 161-203` | Bake is OFF by default (section 7.3). |

Terrain3D node settings Chimera changes: only `region_size = 256` (`TerrainPhase.cs:67`). Everything else is the 1.0.1 default
[upstream `terrain_3d.h:72-80`]: `vertex_spacing=1.0`, `mesh_size=48`, `mesh_lods=7`, collision `DYNAMIC_GAME`,
`world_background=FLAT` (`terrain_3d_material.h:44`). Region map limit is 32x32 regions (`terrain_3d_data.h:22`), +-4096 m at 256 m
regions, far above the +-128 sim extent. Region size options 64..2048 (`terrain_3d.h:40-46`).

## 2. Editing tools in the Godot map editor

Hotkey `T` toggles the brush in Edit mode (`TerrainBrush.cs:187-190`; `Ctrl+T` is the Tech Tree editor). While active: `1`-`5`
select mode, `[` / `]` change size by 5 (`:222-234`), LMB drags a stroke (`:239-257`). Keys are ignored while typing (`:215`) and for
Ctrl+letter (`:216-217`). The panel sits at (10,155), 350 px wide (`:771-776`). Bare letters are tools, Ctrl+letter are editors
(`src/Core/Definitions/EditorHotkeys.cs:34-61`).

### 2.1 Chimera's five modes (`TerrainBrush.cs:37-47, 580-616`)

| Key | Mode | Terrain3D tool + op sent | Intended effect | Works in 1.0.1? |
|---|---|---|---|---|
| 1 | Raise | SCULPT(1) + ADD(0) | `h += alpha*strength` | yes [live] |
| 2 | Lower | SCULPT(1) + SUBTRACT(1) | `h -= alpha*strength` | yes (same code path as Raise; not separately probed) |
| 3 | Smooth | SCULPT(1) + op **5** | average with 4 neighbours | **no**: 1.0.1 AVERAGE is 3, 5 is out of range. Probe: op 5 left centre and neighbour heights unchanged; op 3 moved them. [live] |
| 4 | Flatten | HEIGHT(2) + ADD(0), `height=0` | lerp toward Y=0 by `alpha*strength` | yes, but target is hard-coded 0 (`:605`); on raised ground at strength 10 it closes 1% of the gap per call [live] |
| 5 | Paint | TEXTURE(3) + op **4** | hard-set layer id | **no**: 1.0.1 op 4 is GRADIENT, ignored by the texture tool. Probe: control map unchanged (raw 1); with op **2** (REPLACE) base id became 2, control raw 276824064. [live] |

The constants block says "MULTIPLY=2, DIVIDE=3 occupy slots before REPLACE; OP_REPLACE = 4; OP_AVERAGE = 5"
(`TerrainBrush.cs:45-47`), which matches pre-1.0 Terrain3D. The 1.0.1 enum is `ADD, SUBTRACT, REPLACE, AVERAGE, GRADIENT`
[upstream `terrain_3d_editor.h`]. The tool enum matches Chimera's: `REGION=0, SCULPT=1, HEIGHT=2, TEXTURE=3, COLOR, ROUGHNESS,
AUTOSHADER, HOLES, NAVIGATION, INSTANCER, ANGLE, SCALE`.

Exist in Terrain3D (addon UI `addons/terrain_3d/src/toolbar.gd:42-92`) but **not exposed by Chimera**: Slope/ramp (SCULPT+GRADIENT
between two picked points), Height picker, Spray texture, Autoshader, Color paint, Wetness/roughness paint, Holes, Navigable-area
paint, Instancer meshes, Add/Remove Region. **Chimera has no ramp, noise or plateau tool.** Procedural noise terrain does not
exist either: `ProceduralMapGenerator` always writes `TerrainRef = ""` (`src/Core/MapGen/ProceduralMapGenerator.cs:109`) and
generates entity layouts only.

### 2.2 Brush parameters and units

Chimera state (`TerrainBrush.cs:96-100`): `_brushSize = 20` (slider 5..100 step 1, `:841-843`; keys clamp 5..100, `:229, :233`),
`_brushStrength = 10` (slider 1..100, `:857-859`), `_activeLayer` 0..3, `mouse_pressure = 1`, `height = 0`, `asset_id = layer`,
`align_to_view = false`, `gradient_points = []` (`:599-613`). A **fresh dictionary is built per call** and `ApplyBrushSettings`
runs on every mouse-motion event (`:285-295`), so deposition scales with input-event rate. (Reusing one dictionary across calls
corrupts strength because `set_brush_data` rescales in place; I hit this in my probe: 0.1, then 0.01, then clamped.)

Terrain3D 1.0.1 semantics [upstream `terrain_3d_editor.cpp` `set_brush_data` / `_operate_map`]:

| Parameter | Chimera value | Meaning in 1.0.1 | Verified |
|---|---|---|---|
| `size` | 5..100 | **Diameter in metres**, clamped 0.1..4096; the brush loop covers `size x size` texels at `vertex_spacing` step | [live]: size 20 touched 309 texels (pi*10^2 = 314) |
| `strength` | 1..100 | Percent, stored `x0.01` (min 0.01, max 1000); height ops add `alpha * strength` metres **per call** | [live]: strength 10, alpha 1 -> +0.100 m; 5 m from centre -> +0.050 m; 12 m -> 0 |
| brush alpha | `circle0.exr` | 256x256 `Rf` image sampled by `(x,y)/size`, `pow(alpha, gamma=1)`; **linear radial falloff** | [live]: centre 1.0, corner 0.0 |
| `jitter` | absent, so 0 | random rotation of the brush image | [upstream] |
| paint threshold | n/a | REPLACE writes only where `alpha > 0.5` (the inner half of the falloff), sets base = overlay = id, blend 0, clears autoshader | [upstream] |
| Smooth | (broken) | `lerp(h, avg5, clamp(alpha*strength*2, 0.02, 1))` | [upstream] |
| Flatten | `height 0` | `lerp(h, height, clamp(alpha*strength, 0, 1))` | [upstream][live] |
| `auto_regions` | absent, so true | sculpt/height strokes create missing regions (blank, height 0) | [live]: a stroke at (50,50) created region (0,0) |

The brush **cursor** is Terrain3D's own decal (`terrain.set_camera(cam)` each frame, `TerrainBrush.cs:177-178`); the hit point
comes from `terrain.get_intersection(origin, dir, true)` with NaN or Z>3.4e38 as miss (`:561-575`).

### 2.3 Paint layers and textures

Four fixed layers, index = texture id: 0 Grass, 1 Dirt, 2 Rock, 3 Snow (`TerrainBrush.cs:50-57`). They are **solid-colour 64x64
placeholder albedos** built at runtime into a `Terrain3DAssets` (`:655-713`; colours (0.30,0.55,0.20), (0.48,0.35,0.20),
(0.50,0.50,0.50), (0.90,0.92,0.95)). Four real 1024x1024 RGB PNGs exist in `assets/textures/terrain/{grass,dirt,rock,snow}.png` but
**no code references them** (grep of `src/` and `scenes/`: no hit). Epic 16 slice 4 (terrain textures) is outstanding
(`docs/history/Snapshot-through-2026-09-21.md:27, 549-556`). Terrain3D allows 32 texture slots (DW-1019,
`_bmad-output/implementation-artifacts/deferred-work.md:8344`).

Control-map bit layout, one `uint32` stored in an `Rf` pixel as a float bit pattern [upstream `terrain_3d_util.h:204-247`]:

| Bits | Field |
|---|---|
| 31..27 | base texture id (0-31) |
| 26..22 | overlay texture id (0-31) |
| 21..14 | blend (0-255) |
| 13..10 | uv rotation (0-15, x22.5 degrees) |
| 9..7 | uv scale (0-7) |
| 2 | hole |
| 1 | navigation flag |
| 0 | autoshader |

New regions are filled with control value 1 (autoshader on, texture 0), verified by probe. The color map (`Rgba8`) holds per-texel
tint (RGB) + roughness (A); Chimera never writes it but it is saved and restored.

### 2.4 Water

Water is **not terrain**. `WaterTool` (key `N`, Edit mode only; `Ctrl+N` is the Replay Browser) drags an axis-aligned rectangle;
persisted as `water[]` of `{x, z, w, h, y}` (`ScenarioData.cs:495-520`, field `:1113`). The level spinner is -50..50 step 0.25,
default -0.5 (`src/CreationSuite/WaterTool.cs`, `_waterLevel` near line 31); minimum extent 0.5. Rendered as a translucent
`PlaneMesh` (`:271-287`). The rectangle's flow cells are stamped into the impassable grid (section 7.2) and folded into the lobby
hash. No fluid sim, no depth, no shoreline; Y is presentation-only.

### 2.5 Impassable paint

`PathabilityTool` (key `K`, overlay toggle `P`, Edit only). Brush radius in **cells** 0..16 (`PathabilityTool.cs:44, 156-157`),
diameter `2r+1`, default r=1; circular stamp `dc^2+dr^2 <= r^2` (`:197-211`); Paint/Erase toggle; slope-auto-block checkbox and
threshold spinner 0..100 step 0.1 (`:374-378`). Cells are 2 m (the flow-field grid). One stroke = one undo entry (`:234-238`).
The overlay is one `MultiMeshInstance3D` of red quads, Edit-only.

## 3. Undo for terrain edits

- One **shared `EditorHistory`** for entities, terrain, water, pathability: `EntityPlacer.History` is injected into
  `TerrainBrush.Initialize` (`TerrainBrushPhase.cs:141`). Ctrl+Z/Y is handled in `EntityPlacer` and swallowed while a stroke is live
  (`TerrainBrushPhase.cs:144`, DW-144).
- Cap: **512 MiB** and 1000 entries, oldest dropped (`src/CreationSuite/EditorHistory.cs:25-28`; DW-140 closed 2026-07-27).
- Mechanism is **whole-region image snapshots**, not deltas. `BeginPaint` snapshots height + control (not color) of up to nine probed
  regions (`TerrainBrush.cs:329-373`, a 9-point +-r box); `EndPaint` snapshots again and pushes redo/undo closures (`:379-427`).
  Restore is `data.import_images([h, c, null], regionOrigin, 0, 1)` + `calc_height_range(true)` + nav `MarkDirty` (`:469-502`).
  Regions created by a stroke are recorded `WasAbsent` and removed on undo (DW-141). A stroke that changed no byte pushes nothing
  (DW-143, `ImageBytesEqual` `:433-435`). Cost estimate = width*height*bytes per Image (`:439-462`).
- Per-region raw size: 256x256x4 B = 256 KiB per float map. Before+after of a stroke touching 1 region is about 1 MiB; 4 regions about 4 MiB.
- `stop_operation` is deliberately **never called** (`:296-315`) to avoid a `_store_undo` push_error at runtime; Terrain3D's own undo
  is bypassed and `is_operating` stays true (accepted trade-off).
- Water add/delete and impassable strokes push closures on the same stack. The whole history is wiped on F5 re-apply (DW-138).

## 4. What is saved: formats and sizes

### 4.1 Scenario JSON fields that touch terrain (System.Text.Json, snake_case)

| JSON key | C# (`ScenarioData.cs`) | Type / units | Omit rule | In lobby hash? |
|---|---|---|---|---|
| `terrain_ref` | `TerrainRef` `:668` | string, `res://.../<stem>_terrain/` folder or `""` | always written | **No** (neutralised, `CanonicalModelHash.cs:226-237`) |
| `map_bounds` | `MapBounds` `:674` | float, playable half-extent in m, default 120, max 128 | always | yes |
| `border_extent` | `BorderExtent` `:692` | float m, cosmetic border beyond bounds | omit when 0 | **No** (also not in checksums) |
| `pathability_blocked` | `PathabilityBlocked` `:1052` | base64 of a 2048-byte bitset, bit `i = row*128+col`, LSB-first within each byte (`PathabilityGrid.cs:210-230`); all-clear omitted | omit when null | yes (FNV digest, `CanonicalModelHash.cs:248`) |
| `slope_auto_block`, `slope_block_threshold` | `:1066`, `:1078` | bool; float rise per metre | omit when default | yes (config only, `:249-250`) |
| `height_advantage_vision`, `height_vision_bonus_per_step` | `:1002`, `:1014` | bool; float m of vision per whole metre of elevation | omit when default | **No** (fog grid is not in the checksum) |
| `water[]` | `:1113`, class `:495` | `{x,z,w,h,y}` m | omit when null | footprint yes (`:258`), Y no |

There is **no heightmap, splat or paint data in the JSON**. Height, texture and color live only in the `.res` files.

### 4.2 Terrain3D region files

- Written by `Terrain3DData.save_directory(abs)` into `<scenario stem>_terrain/` beside the JSON (`WinConditionPhase.cs:527-541`);
  the folder is wiped first, and an empty result resets `TerrainRef` to `""`.
- File name `terrain3d_XX_YY.res`, with a **hyphen** for negative coordinates (location (-1,-1) is `terrain3d-01-01.res`);
  `IsTerrainRegionFile` matches the `terrain3d` prefix, not `terrain3d_` (`ContentPackager.cs:874-890`). [live: both names produced]
- Contents per region [upstream `terrain_3d_region.h`]: `version`, `region_size` (64..2048), `height_range` (min,max), `height_map`
  `Rf`, `control_map` `Rf` (packed uint32), `color_map` `Rgba8`, `instances`, `vertex_spacing`. An optional 16-bit height save exists
  (`save(path, p_16_bit)`); Chimera uses the default 32-bit float.
- Godot compressed binary resource, magic `RSCC`. Probe sizes after one sculpt stroke on an otherwise flat map: 5,935 B and 7,200 B.
  A fully sculpted or painted region will be larger (UNVERIFIED, no shipped sample).
- **Heights are float32 metres, unbounded** (Chimera applies no clamp; only `Fixed` 16.16 range, +-32768 m, applies when the sim
  grid is built). Height range is tracked per region and recomputed with `calc_height_range`.
- World size: 256 texels per region at 1 m per texel; no scale, no offset (`import_images(..., offset 0, scale 1)`).

### 4.3 Package

`.chimera.zip` stores `map/terrain/<leaf>.res`; the manifest lists `terrain_files[]` and `terrain_hash` (FNV-1a over leaf+bytes,
ordinal-sorted; 0 = none) (`ContentPackager.cs:185-206`; `ContentPackageManifest.cs:127-140`). Import copies them to
`res://resources/data/scenarios/<id>_terrain/` and rewrites `terrain_ref` (`WinConditionPhase.cs:600-625`). Open defect DW-145:
import with zero terrain files leaves a stale ref.

## 5. Resolution, extents and map sizes

- **Authoring resolution**: 1 m per texel, 256x256 per region (`region_size=256`, `vertex_spacing=1`). One region = 256 m.
- **Sim extents (fixed at compile time, never derived from `map_bounds`)**: flow/fog/pathability 128x128 cells, 2 m, +-128 m;
  elevation grid 256x256, 1 m, +-128 m; spatial hash +-160 m (`src/Navigation/FlowField.cs:28-37`; `src/Core/Definitions/MapSize.cs:7-21,
  43-56`; test `ProjectChimera.Sim.Tests/Navigation/GridDimensionConsistencyTests.cs`). `MapSizes.MaxHalfExtent = 128` is a permanent,
  deliberate ceiling (`MapSize.cs:56`).
- **Authored sizes** (`MapSize.cs:63-69`): Small 80 (160x160 m), Medium 120 (240x240), Large 128 (256x256) half-extents.
- **Shipped maps** (read from the JSON; `map_bounds`, `border_extent`): alpha_map_01 120; map_02_iron_crossing 120; map_03_the_narrows 90;
  map_04_scorched_plains 120; map_05_crossroads 120; map_06_contested_peaks 120; map_10_mirror_lake 128 (+2 border); map_11_blitz 80;
  map_12_the_frontier 128 (+32 border); quad_map_01 120. All `terrain_ref ""`. The border only sizes the `PlaneMesh` fallback
  (`TerrainPhase.cs:32-42`); a bordered map on real Terrain3D would show flat background beyond +-128 (DW-876).
- **Quadrant layout** (fact 6): a sculpted playable area at +-128 spans **four** region files, (-1,-1), (0,-1), (-1,0), (0,0), each
  holding one 128x128 quarter of the playable area inside its own 256x256 map. Live probe: sculpting at (50,50) created region (0,0)
  alongside the default (-1,-1).
- Heights outside any region read NaN, and the elevation builder maps NaN to 0 (`ScenarioLoadPhase.cs:263`). A map saved with only the
  default region therefore has sim elevation 0 for every X>0 or Z>0.

## 6. Shipped-content check (reproducible)

A short Python loop over `resources/data/scenarios/*.json` printing `terrain_ref`, `map_bounds`, `border_extent`, presence of
`pathability_blocked`, slope fields, and counts of `water` and `props`: every file has `terrain_ref=''`, no pathability, no slope, 0 water,
0 props. `dev-scratch/scenarios/{123,face,my-new-map}.json` also have `terrain_ref ""`. No `terrain3d*.res` exists in the repo outside
`.godot`; the only terrain bytes are test fakes (`ProjectChimera.Sim.Tests/Definitions/ContentPackagerTerrainTests.cs:42-43`).

## 7. How terrain feeds the simulation

### 7.1 ElevationGrid (checksum-relevant)

- Type `ProjectChimera.Core.ElevationGrid` (`src/Core/ElevationGrid.cs:168-222`): `Fixed[] Heights` row-major `[row*Width+col]`,
  `Width`, `Height`, `WorldMinX/Z`, `CellSize` (all `Fixed`). `Sample(x,z)` is a clamped **integer cell lookup**, no interpolation; a
  degenerate grid returns 0.
- Built **once per load** in `ScenarioLoadPhase.BuildAndInjectElevationGrid` (`:216-285`; runs after terrain restore, before `Apply`):
  N=256, half=128, 1 m cells; each cell reads the **raw nearest texel** `data.get_pixel(TYPE_HEIGHT=0, Vector3(wx,0,wz)).R` at the
  texel centre (`:262`), non-finite becomes 0 (`:263`), then `Fixed.FromFloat(h)` = `(int)(h*65536)`, truncation toward zero
  (`src/Core/FixedPoint.cs:27`). `HeightmapCellMapping.CellToTexel` picks the texel with integer maths
  (`src/Core/HeightmapCellMapping.cs:264-273`; identity for the 256-cell/256-texel case). Known residual DW-875: the world coordinate
  is rebuilt as a float, so the texel choice is not purely integer.
- Injected via `ScenarioApplier.SetElevationGrid` (`ScenarioApplier.cs:88`) and threaded into `EntityWorld.SetElevationGrid` at apply
  (`:166`; `EntityWorld.cs:1422`). The headless server takes the same grid as a `ServerBootstrap` argument (`ServerBootstrap.cs:33-41, 113`).
- Consumers: (a) `EntityWorld.Create` stamps `Elevation[id] = grid.Sample(pos)` at **spawn only** (`EntityWorld.cs:1301-1306`; teleport
  re-samples, `src/Effects/TeleportEffect.cs:62`); (b) `VisionWithElevation` = base + `floor(Elevation / 1 m) * bonus` when
  `HeightAdvantageVision` is on (`EntityWorld.cs:1486-1502`, used at `FogOfWarSystem.cs:119`); (c) slope-derived blocking (7.2);
  (d) editor hit-testing via `SampleElevation` (`src/UI/EntityPlacer.cs:2528-2549, 3088`).
- **In `SimChecksum`**: `hash = Mix(hash, world.Elevation[i].Raw)` per alive entity (`SimChecksum.cs:402-408`, added in v15). The grid
  itself is not folded, so two peers with different terrain are caught only once an entity spawns at a differing height.
  `SaveGameState` stores per-entity `Elevation` raw (`src/Core/Persistence/SaveGameState.cs:421, 925`) but not the grid; load re-reads terrain.
- `Position.Y` is always `Fixed.Zero` (`ScenarioApplier.cs:159-163`): the sim is 2D plus a stamped elevation scalar.

### 7.2 PathabilityGrid (movement- and lobby-hash-relevant)

- `PathabilityGrid` is 128x128 bool (`src/Navigation/PathabilityGrid.cs:33-45`); cell identity is `FlowField.WorldToCell`, 2 m.
- Resolved by the shared Godot-free `ScenarioApplier.BuildPathabilityGrid(scenario, elevationGrid)` (`ScenarioApplier.cs:104-130`): the union
  of the painted bitset, **slope-derived cells** (when `slope_auto_block` is on and threshold > 0, a cell blocks when the maximum
  `|rise|/2 m` over its four neighbours reaches the threshold; `PathabilityGrid.DeriveSlopeBlockedInto`, about `:268-306`, pure `Fixed`),
  and blocking-prop and water footprints. It is null when nothing is blocked, so flat maps stay byte-identical.
- Sinks: `EntityWorld.SetPathabilityGrid` (movement uses `CheckedStep.Resolve`, a swept-cell DDA, `src/Navigation/MovementSystem.cs:188`),
  `FlowFieldSystem.SetStaticBlocked` (OR-ed into the BFS obstacle map on the next `RebuildObstacles`, `FlowFieldSystem.cs:97-164`), and
  `SceneContext.Pathability` for the editor overlay (`ScenarioLoadPhase.cs:320-322`).
- Rebuilt on every F5 Edit->Play from the **edited scenario** but with the **old** elevation grid (`MainScene.cs:3017-3037`), so painted,
  prop and water edits take effect and sculpting does not.
- Slope-derived cells are not persisted and not in the handshake (only the slope config is); they ride the heightmap.
- **No discrete high-ground levels exist.** There is only continuous `Elevation` and the per-metre vision bonus (default off). No
  height-based combat modifiers and no terrain line-of-sight blocking.

### 7.3 Navmesh (not sim)

The Godot `NavigationMesh` is baked from `terrain.generate_nav_mesh_source_geometry(aabb, false)` over a **+-120 m** AABB, Y -5..5
(`NavigationPhase.cs:27, 170-189`; `NavObstacleManager.cs:67, 188-197`; agent radius 0.4, height 1.8, max climb 0.25, cell 1 m,
`NavigationPhase.cs:31-36`). Since DW-918 the rebake is **disabled** (`BakeEnabled=false`, `NavObstacleManager.cs:35-41`) because nothing
queries the navmesh: units steer by the flow field. The sim never reads it, so Unreal need not reproduce it. The terrain-change signal
(`MarkDirty`, 0.5 s debounce after a stroke, `TerrainBrush.cs:314`; after undo, `:501`) now only re-arms the disabled bake.

### 7.4 What is NOT derived from terrain

Fog grid (fed by unit positions and building vision), flow field, spatial hash, building placement legality (uses `PathabilityGrid` and
footprints, not height), start positions, regions, camera paths. Water Y, border extent, layer paint and color are presentation-only.

## 8. Tests and goldens that pin this

- `ProjectChimera.Sim.Tests/Core/ElevationGridTests.cs` (hand-built grids), `HeightmapCellMappingTests`, `CreationSuite/PathabilityCellMappingTests`,
  `Navigation/FlowFieldBlockingTests`, `Navigation/CheckedStepTests`, `Navigation/GridDimensionConsistencyTests`,
  `Definitions/ContentPackagerTerrainTests` (fake `.res` pack/unpack/tamper), `Definitions/ScenarioDataPathabilityTests`,
  `Golden/PathabilityBlockScenario`.
- **No golden uses a non-zero `ElevationGrid`** (grep: only `ElevationGridTests` references it among core/golden tests). So trial check (a)
  on any shipped map exercises `Elevation == 0` for every entity; a checksum match there says nothing about the elevation path.
- `src/Core/Bootstrap/Phases/**` (all Godot terrain glue) is `Compile Remove`d from the sim assembly (`SimSources.props:156-161`), so the
  AOT/analyzer set contains `ElevationGrid`, `PathabilityGrid`, `ScenarioApplier`, `ScenarioData` and none of the glue.

## 9. Perf numbers on record

| Number | Source |
|---|---|
| Navmesh bake over the 240x240 terrain AABB: **136-180 ms** synchronous on the main thread (flat map); caused the 2026-08-10 LAN hang; now disabled | `NavObstacleManager.cs:13-18`; `deferred-work.md` DW-918 (~line 7503-7508) |
| Edit-mode FPS ~510-540 in Godot with the Terrain3D map editor live | `_bmad-output/implementation-artifacts/epic-6-retro-2026-07-15.md` verification table |
| Undo cap 512 MiB / 1000 entries; snapshot 512 KiB (height+control) per region, twice per stroke | `EditorHistory.cs:25-28`; `TerrainBrush.cs:439-462` |
| Elevation grid build: 65,536 `get_pixel` calls per load (cost not measured) | `ScenarioLoadPhase.cs:253-267` |
| Sim grid memory: elevation 256 KiB (65,536 x int32); pathability 16 KiB; one cached flow field ~192 KiB (12 B x 16,384) | `ElevationGrid.cs`; `PathabilityGrid.cs:37`; `FlowFieldSystem.cs:72` |
| No sculpted-terrain render benchmark exists (all shipped maps are flat) | UNVERIFIED gap |

## 10. Known defects and gaps relevant to an Unreal port

| Id | Issue | Status |
|---|---|---|
| (new) | Operation-enum drift: Smooth and Paint are no-ops on 1.0.1 (fact 7). Backlog 10-16 (`sprint-status.yaml:220`) names the wrong cause | found 2026-10-01, not filed |
| (new) | Default region at (-1,-1), not centred; `TerrainPhase.cs:49-50` comment is wrong (fact 6) | found 2026-10-01, not filed |
| (new) | Tooltip says brush size is a radius; Terrain3D uses a diameter (`TerrainBrush.cs:848`) | found 2026-10-01 |
| DW-875 | texel choice not purely integer; hard-coded +-128 and 256 assumptions; no sculpted determinism test | open |
| DW-876 | border not rendered on real Terrain3D | open |
| DW-877 | fallback plane shrinks below +-128 on small maps | open |
| DW-145 | import with zero terrain files leaves a stale `terrain_ref` | open |
| DW-1019 | 32-slot palette, packed-channel migration | open |
| 10-16 | paint write | backlog |
| DW-153 | editor hit-tests assumed flat | closed 2026-08-01 (samples elevation) |
| - | Buildings and units ignore elevation visually | untracked; `Snapshot-through-2026-09-21.md` item 6 says units respect it, **the code says otherwise** (trust the code) |

## 11. Implications for the Unreal runtime terrain (for the design phase)

1. **Data model to match**: height field in metres (float32 on disk, unbounded), 1 m texel; per-texel control word (base id, overlay id,
   blend, uv rotation/scale, hole, nav, auto) or a simplified 4-layer splat; optional color + roughness; water as rects `{x,z,w,h,y}`;
   impassable as a 128x128 bitset in the JSON. All shipped content is flat, so there is **no legacy terrain to migrate**.
2. **Do not copy Godot's region addressing.** Keep the world-coordinate semantics (cell `col = floor((x+128)/1)`) because the sim grid uses
   them. One global height image centred on the origin is simpler and removes the four-way quadrant split.
3. **The sim interface is two arrays.** Unreal must produce `ElevationGrid` (256x256 `Fixed`, from the raw texel via `(int)(h*65536)`
   truncation; do not substitute floor or round) and the `PathabilityGrid` inputs, then call the same injection points. For cross-engine
   checksum parity, test with exactly representable heights.
4. **Check (c) has a design question Godot never answered**: edits are Edit-mode only and are not fed back to the sim until reload.
   Decide whether runtime edits (a) stay presentation-only until the next match start (Godot parity), or (b) rebuild `ElevationGrid` and
   slope cells and re-push them to `FlowFieldSystem` (new behaviour; slope cells change pathing and so the checksum; needs a
   deterministic rebuild point).
5. **Brush semantics worth keeping**: diameter-based size 5..100 m, linear-falloff alpha, flatten target, 4 layers, undo by whole-region
   snapshots with a 512 MiB cap. **Fix on the way**: make strength per-second (or per-tick) instead of per-input-event; implement real
   Smooth and Paint; consider a ramp tool (Terrain3D has one, Chimera never did).
6. **Format converter**: reading `.res` outside Godot needs Godot's `RSCC` decompression and resource parser. Cheaper is a one-way exporter
   run inside Godot (`get_height_map()` / `get_control_map()` to raw `.r32` / `.u32`). No shipped data needs it today.
7. **Units on slopes**: Godot never lifted them, so Unreal terrain-following is a visual-only addition; the sim keeps `Position.Y = 0`.

## 12. Open questions

- Is a runtime-sculpted map expected in 1.0? Nothing in content depends on terrain today; Alec's spec says "terrain working for a map
  editor/game" (`unreal_spec.txt:384`).
- Should the Unreal editor expose the extra Terrain3D tools (slope/ramp, color, wetness, holes, navigable) or only Chimera's five?
- Texture palette: 4 fixed layers (today) or the 32-slot creator palette (DW-1019)?
- Height quantisation: truncate like `Fixed.FromFloat`, or define a quantisation step in the new file format (for example int16 millimetres)?
- Hole/cave support (Terrain3D hole bit)? Chimera never used it.
- Strength unit for Unreal brushes (per second vs per event) and whether to keep Godot's feel for parity.
- Fact 6 was proven on a headless node; the on-screen look of the default quadrant and background is UNVERIFIED.

## 13. Method and reproducibility

- Probe project: scratchpad `t3dproj/` (copies of `terrain.gdextension`, `libterrain.windows.{debug,release}.x86_64.dll`, `circle0.exr`),
  run with `Godot_v4.6.3-stable_mono_win64_console.exe --headless --path . --script probeN.gd`. Nothing was written to the repo's `.godot`.
  Results quoted: region list `[(-1,-1)]`; NaN at (+100,+100); raise +0.1 / +0.05 / 0 at 0 / 5 / 12 m from centre; 309 touched texels;
  second operate gives 0.2; auto region (0,0); ops 4 and 5 are no-ops, ops 2 and 3 work; saved files `terrain3d-01-01.res` 5,935 B and
  `terrain3d_00_00.res` 7,200 B, magic `RSCC`.
- Upstream source: `https://raw.githubusercontent.com/TokisanGames/Terrain3D/v1.0.1-stable/src/` files `terrain_3d_editor.cpp`,
  `terrain_3d_editor.h`, `terrain_3d_util.h`, `terrain_3d_region.h`, `terrain_3d_data.h`, `terrain_3d.h`, `terrain_3d_material.h`, fetched
  2026-10-01. The binary-to-source match is inferred (plugin.cfg says 1.0.1 and every probe result agreed with the source), not proven by hash.
