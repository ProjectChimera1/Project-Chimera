# Epic 6 Context: Map & Terrain Editor

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

A map creator sculpts and texture-paints terrain with textures/heights that actually persist across save/load, and places entities, start positions, resource nodes, regions, doodads, impassable terrain, cameras, and water to a ship-quality bar. The headline defect is that today's terrain regenerates flat on every load and painted textures/sculpted height never survive a save — this epic fixes that persistence gap, hardens the existing brush/placement UX, and rounds the editor out to WC3-level parity (regions, pathability, props, multi-select/copy/rotate, 2–4 start positions, minimap preview, custom building placement) while keeping every sim-touching change deterministic and checksummed.

## Stories

- Story 6.1: Verify & harden the creation-suite editor — terrain sculpt/paint + entity/start/resource/win placement to ship bar
- Story 6.2: Persist sculpted terrain height + painted textures across save/load + stroke undo/redo (headline defect fix)
- Story 6.3: Sim-side deterministic terrain elevation + height-advantage vision (and fog-of-war verify)
- Story 6.4: Regions — named areas as a first-class map/trigger primitive
- Story 6.5: Impassable terrain — pathability paint, deterministic blocking, and the pathability overlay
- Story 6.6: Doodads/props placement + editor multi-select/copy-paste/rotation + named cameras + water floor
- Story 6.7: Map properties, New-Map flow, 2–4 start positions, and minimap preview
- Story 6.8: Custom building placement — thread an authored building id through BuildingSystem/ScenarioApplier + retire the enum gate

## Requirements & Constraints

- Terrain sculpt (raise/lower/smooth/flatten) and texture-paint (Grass/Dirt/Rock/Snow) must work in-app with a responsive brush, and painted textures/sculpted height must persist through save/load and through .chimera.zip export/import — this is the epic's headline fix, not new functionality.
- Entity/start-position/resource-node/win-condition placement (already largely built) must be verified and closed against a ghost-preview-follows-cursor, grid-snap, left-click-place / right-click-or-Esc-cancel interaction contract, with full undo/redo.
- World-editor parity floor for 1.0: named regions (rect-only; circles/polys post-1.0), impassable-terrain paint + overlay, doodads/props (MultiMesh, `blocks_pathing` flag), map properties + New-Map flow + minimap preview, multi-select/copy-paste/rotation, named cameras, and cheap (visual + blocking, no fluid sim) water.
- Match-scale honesty: the editor authors exactly 2–4 start positions (not the GDD's original 8), matching the verified multiplayer player count.
- Win-condition preset templates (King of the Hill, etc., built in Epic 7) depend on regions existing as a drawable primitive here.
- Building authoring: the editor must support placing any authored (custom) building id, not just the fixed `BuildingType` enum — every `(int)BuildingType`-indexed touch-site needs auditing so a custom id can't crash past the Tier-1 gate.
- Any sim-state change (elevation, pathability blocking, region containment) must stay in Fixed-point, deterministic, and fold into `SimChecksum` — each such change re-baselines the golden checksum as an explicit, named step, never a silent regression. Flat/legacy maps and the feature-toggle-off path must remain byte-identical to pre-feature behavior.

## Technical Decisions

- Sim/Presentation boundary is sacred: sim lives in `src/Core`, `Combat`, `Economy`, `Navigation` (pure C#, `Fixed` 16.16 math, no Godot types, ascending-entity-id iteration); presentation lives in `src/UI`, `src/CreationSuite` (Godot Nodes: `TerrainBrush.cs`, `EntityPlacer.cs`, `EditorToolsController.cs`, `MapIoController.cs`). Most of this epic is presentation-only; only Story 6.3 (elevation/vision) and 6.5 (pathability blocking) touch sim.
- New per-entity sim state (e.g. `Elevation`) = a new `Fixed[MAX_ENTITIES]` parallel array on `EntityWorld`, reset (not `Fixed.FromFloat`) in `Create()`, sampled via clamped integer cell lookup (never Godot `Image` interpolation) in the sim layer, and folded into `SimChecksum.Compute` via `Mix()` — a `SimChecksumCoverageGuardTest` fails builds that add a store without folding it in.
- A `SimChecksum` widening from a new store is its own named re-baseline step, stated explicitly in the story/PR — not bundled silently into an unrelated change.
- NavMesh bakes from Terrain3D geometry; flow-field pathfinding (`FlowFieldComputer`/`FlowFieldSystem`) is the live deterministic path system via `NavigationServer3D`'s direct API (never `NavigationAgent3D` nodes); `SpatialHash` handles deterministic neighbor queries.
- Map package (`scenario.chimera.zip`) layout: `manifest.json`; `map/terrain.bin` + `terrain_meta.json` + `props.json`; `scenario/setup.json` (start positions/resources), `triggers.json`, `objectives.json`; `assets/`. `ScenarioData.TerrainRef` should be a `res://` path into this package's saved Terrain3D data (region/control-map storage), not a regenerated procedural heightmap.
- Props/doodads render via `MultiMeshInstance3D` only (never per-prop nodes); a prop's `blocks_pathing` flag stamps/un-stamps the 6.5 pathability layer and never otherwise touches sim state or the checksum.
- `FactionRegistry`/`FactionSlots` centralize all player-count knowledge (`PLAYER_COUNT`, `FACTION_ARRAY_SIZE`) — new start-position/slot code must read through this registry, never hardcode a player-count loop bound.
- Threading an authored building id through placement requires auditing every closed `(int)BuildingType`-indexed array (`NavObstacleManager`, `EntityPlacer` costs, `BuildingBridge` type count, etc.) — a switch-statement grep alone misses these array classes.

## UX & Interaction Patterns

- All editor work reuses the existing Creation Suite shell (no redesign): top toolbar (Edit/Play toggle, tool tabs including Terrain, undo/redo, Save, Publish); left tool palette with hotkey tooltips; right dock showing the active panel with a Simple/Advanced disclosure toggle. Editor chrome is fully hidden during Play — a Commander who only plays never sees an authoring surface.
- Placement contract: a semi-transparent ghost mesh follows the cursor in the correct shape/color for the selected type; `G` toggles grid-snap; left-click places; right-click or Esc cancels the active placement mode and hides the ghost without placing anything.
- Undo/redo (`Ctrl+Z`/`Ctrl+Y`) is a blanket expectation across the whole editor — every new tool (terrain strokes, regions, props, multi-select groups) must push onto the same shared editor history and interleave safely with existing entity undo/redo, with no cross-corruption.
- Terrain brush hotkeys: `T` toggles the brush; `1`–`4` select raise/lower/smooth/flatten, `5` selects paint mode with a layer picker; `[`/`]` resize the brush; clicking inside the brush panel must never paint terrain underneath it.
- New surfaces this epic adds to the design system (no per-story UX-DR spec pre-authored yet; author using existing components + the tooltip-on-every-control mandate): region draw tool, pathability overlay, prop palette, New-Map dialog, multi-select/copy-paste, camera tool.

## Cross-Story Dependencies

- Epic-level: depends on Epic 3 (the Creation Suite shell must exist first).
- 6.2 depends on 6.1; 6.3 depends on 6.2; 6.4 (Regions) depends on 6.1; 6.5 (Impassable terrain) depends on 6.2 and 6.3; 6.7 (Map properties/New-Map/start positions/minimap) depends on 6.2; 6.8 (Custom building placement) depends on 6.1 and on Epic 4's Story 4.5 (building editor).
- 6.6 (doodads/props, multi-select, cameras, water) depends on 6.1, 6.2, and 6.5 (props' `blocks_pathing` rides the pathability layer); its merged multi-select/rotation sub-part depends on 6.4 and 6.8, and its merged cameras/water sub-part depends on 6.5 and pairs with Epic 7 Story 7.13 (the `MoveCamera` trigger action consumes 6.6's named cameras).
- 6.4's regions are a prerequisite for Epic 7's win-condition preset templates (e.g. King of the Hill) to bind to an author-drawn zone instead of a hardcoded one.
- 6.5 formally lifts the vision-only scope limit that 6.3 explicitly imposed on elevation/pathing coupling — 6.5 is where elevation-driven blocking becomes an intentional sim-behavior change requiring its own golden re-baseline.
