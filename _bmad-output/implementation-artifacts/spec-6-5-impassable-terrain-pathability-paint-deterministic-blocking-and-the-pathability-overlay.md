---
title: 'Story 6.5: Impassable terrain — pathability paint, deterministic blocking, and the pathability overlay'
type: 'feature'
created: '2026-07-14'
status: 'done'
baseline_revision: '30093303340acadcd0198c2a2d188a23383d59f7'
final_revision: '64f50d527c4cef8598c050f4ed5032ec8131e462'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** Maps are open fields — terrain can never block movement, so there are no chokepoints, walls, or cliffs. Creators cannot author unwalkable terrain, and the sim has no deterministic notion of a painted "blocked" cell.

**Approach:** Add an authored per-cell **pathability layer** (painted blocked/erased cells, plus an optional per-map slope-derived auto-block), persisted in the map and resolved once at load into a Godot-free `PathabilityGrid` that (a) the deterministic sim honors so units cannot cross into blocked cells, (b) the flow field routes around, and (c) a toggleable red editor overlay (the WC3 'P' view) visualizes. Because blocking is **lockstep-critical** (it changes unit paths → positions), the authored layer folds into the MP start-state handshake (`CanonicalModelHash`) so mismatched peers are rejected at the handshake rather than desyncing in-sim — a one-time, explicitly-stated golden re-baseline.

## Boundaries & Constraints

**Always:**
- Sim/Presentation boundary is sacred. `PathabilityGrid` is pure Godot-free C# (`Fixed`/`bool`, ascending-id iteration, clamped **integer-cell** lookup — never Godot `Image` interpolation), living in `src/Core`/`src/Navigation`. The paint tool + overlay are presentation (`src/CreationSuite`).
- The pathability grid is **128×128 @ 2 world-units/cell over ±128**, byte-identical to `FlowField.WorldToCell` (`FlowField.cs:28-69`). Painted cells map through that exact mapping so validator, sim, and flow field agree on cell identity.
- The authored blocked layer folds into `CanonicalModelHash` (bump `AlgoVersion` 5→6) so it flows into `StartStateHash` via the content seed and gets handshake rejection — the StartCrystal/Supply lockstep-critical posture, the intentional **divergence** from 6.3's vision-only exclusion (epic context line 55).
- Deterministic blocking is enforced in the fixed sim tick (`MovementSystem`): a live unit may not integrate its position **into** a blocked cell it is not already in. Null/empty grid ⇒ exact no-op.
- A flat/legacy map with no painted cells and slope-auto-block OFF is **byte-identical** to pre-feature behavior: `PathabilityGrid` is null, the `ScenarioData` field is omitted (`WhenWritingNull`), all 23 per-tick SimChecksum goldens and `SimChecksum.AlgoVersion` (15) are **unchanged**.
- Every editor stroke (paint/erase batch) pushes exactly one `EditorHistory.Push(redo, undo)` pair onto the shared history (`EntityPlacer.History`) and interleaves safely with entity/region/terrain undo.
- Slope-auto-block is **per-map, default OFF**; when ON, steep cells are derived deterministically from `ElevationGrid` neighbor differences at load and unioned into the runtime grid.
- `ScenarioValidator` fails closed (clear message, pre-tick) if any start/spawn position resolves to a **painted** blocked cell.

**Block If:**
- The intended pathability grid resolution/extent cannot be reconciled with `FlowField.WorldToCell` (e.g. a required alignment other than 128²/2-unit/±128) — do not silently pick one.
- Making blocking handshake-critical would require folding into per-tick `SimChecksum` **and** re-baselining all 23 tick goldens AND you find blocking that is NOT transitively captured by `Position` — surface it rather than expanding the re-baseline surface unattended.

**Never:**
- Never bake blocked-ness into the Terrain3D control map (the texture-paint channels) — pathability is authored sim-consumable data, kept separate.
- Never fold the authored layer into per-tick `SimChecksum` (its effect reaches the checksum transitively via `Position`; the handshake hash gives source-level rejection). Do not bump `SimChecksum.AlgoVersion` or touch the 23 per-tick goldens.
- Never render the overlay with per-cell `MeshInstance3D` nodes (use one `MultiMeshInstance3D`); never leave the overlay visible in Play.
- No mid-match mutation of the grid; no circles/polygons; no per-cell weighted cost (binary passable/blocked only).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Paint blocked cells | Author drags brush over cells in Edit mode | Cells set blocked in the painted bitset; overlay shows red quads; one undo entry on mouse-up | Ignore strokes over the dock panel (`IsOverPanel`); raycast miss = no-op |
| Erase blocked cells | Erase mode, drag over blocked cells | Cells cleared; overlay updates; one undo entry | Erasing an unblocked cell is a no-op |
| Save → reload | Map with painted cells | Layer round-trips: identical blocked set, identical `CanonicalModelHash` | All-clear layer normalizes to null ⇒ key omitted |
| Export → import `.chimera.zip` | Map with painted cells | Layer survives; package integrity holds | — |
| Flat/legacy map | No painted cells, slope-auto OFF | `ScenarioData` omits the field; grid null; behavior + all 23 tick goldens byte-identical | — |
| Unit commanded into a blocked cell | `MoveTarget` across a blocked wall | Unit stops at the blocked-cell boundary; never occupies a blocked cell | Deterministic across two same-seed replays |
| Separation shoves unit toward blocked cell | Crowded units beside a wall | Post-integration rejection keeps the unit out of the blocked cell | No throw / no OOB |
| Flow field around blocked cells | Goal beyond a blocked wall | BFS excludes blocked cells; field steers around; identical field across two loads | Blocked goal cell ⇒ no path seeded |
| Slope-auto-block ON | Sculpted map, threshold set | Steep cells auto-block, unioned with painted layer, shown in overlay | Toggle OFF ⇒ zero derived cells |
| Spawn in blocked cell | Start/unit/`spawn_unit` coord on a painted blocked cell | `ScenarioValidator` fails closed with a clear message before any tick | Clear cell passes |
| Peer layer mismatch | Two clients, different painted layers | `CanonicalModelHash`/`StartStateHash` differ ⇒ rejected at handshake | Not a silent desync |

</intent-contract>

## Code Map

- `godot/src/Navigation/PathabilityGrid.cs` — **NEW** Godot-free grid: `bool[] Blocked` (16384), fixed 128×128/2-unit/±128 mirroring `FlowField.WorldToCell`; `bool IsBlocked(Fixed x, Fixed z)` (clamped integer cell), `bool AnyBlocked`, shared `Empty`, `uint Digest()` (FNV over the mask for the hash fold). Degenerate/empty-safe.
- `godot/src/Core/EntityWorld.cs` — hold `PathabilityGrid? Pathability` + `SetPathabilityGrid(grid)` (mirror `SetElevationGrid` ~:851-856). Never reassigned per-tick.
- `godot/src/Navigation/MovementSystem.cs` — after `Position[i] = pos + vel*dt` (~:143), if `world.Pathability` non-null and the new cell is blocked while the old cell is not, reject the crossing (retain the pre-step position / clamp the offending axis). Pure `Fixed`, ascending-id.
- `godot/src/Navigation/FlowFieldSystem.cs` — add `SetStaticBlocked(bool[] mask)` and OR the static mask into `_obstacles` inside `RebuildObstacles` (~:50-60) after `Array.Clear`, before/after `MarkBuildingCells`. `FlowFieldComputer` unchanged (already skips `_obstacles`).
- `godot/src/Core/Definitions/ScenarioData.cs` — add `[JsonIgnore(WhenWritingNull)] string? PathabilityBlocked` (base64 of the packed 128² bitset; null ⇒ omit, the `Regions` precedent ~:400-413) + `[JsonIgnore(WhenWritingDefault)] bool SlopeAutoBlock=false` and `float SlopeBlockThreshold=0f` (the `HeightAdvantageVision`/`HeightVisionBonusPerStep` omit-when-default precedent ~:381-394). Doc: threaded to the sim at apply; folded into `CanonicalModelHash` because pathing is lockstep-critical.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — fold `PathabilityBlocked` digest + `SlopeAutoBlock` + quantized `SlopeBlockThreshold` (near `TerrainRef` neutralization ~:74-83); bump `AlgoVersion` 5→6; doc the divergence from Regions (Regions feed only triggers → excluded; pathability feeds movement → included). `StartStateHash` inherits via the seed (~:63); its own `AlgoVersion` stays 2 (algorithm unchanged, seed changes).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — resolve start-slot bases (~:123-127), pre-placed units (~:193-203), and `spawn_unit` trigger coords (~:339-343) through the same 128² cell mapping and fail closed if the (decoded painted) cell is blocked. Mirror the Regions validator↔applier same-domain agreement (~:205-248).
- `godot/src/Core/Sim/ScenarioApplier.cs` — hold `PathabilityGrid? _pathability` + `SetPathabilityGrid` (mirror `_elevationGrid` ~:50/:83); thread into `EntityWorld` before `Apply` spawns (~:105-112).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — **NEW** `BuildAndInjectPathabilityGrid()` (mirror `BuildAndInjectElevationGrid` ~:158-205): decode painted bitset; if `SlopeAutoBlock`, derive steep cells from the just-built `ElevationGrid` (neighbor rise/run ≥ threshold) and union; inject into `Applier`, `EntityWorld`, `FlowFieldSystem` (`SetStaticBlocked`), and the overlay. Null/empty everywhere ⇒ flat.
- `godot/src/CreationSuite/PathabilityTool.cs` — **NEW** editor Node modeled on `RegionTool.cs`: toggle `K` (Edit-gated), LMB paint / Paint-vs-Erase panel toggle, `[`/`]` brush size, per-cell radius stamp into the painted bitset, `IsOverPanel`+`MouseFilter.Stop` guard, `_Input` interception, right-dock Simple/Advanced panel (brush size, Paint/Erase, slope-auto-block toggle+threshold writing `ScenarioData`), snapshot-before/after → single `EditorHistory.Push` per stroke (TerrainBrush pattern), `MultiMeshInstance3D` red-quad overlay for the union (painted ∪ derived), overlay toggle `P` (independent of the tool), `_ExitTree` free.
- `godot/src/Core/Bootstrap/Phases/PathabilityToolPhase.cs` — **NEW** phase wiring the tool (inject `Placer.History`, `Cam`, `GameState`, `Scenario`, `Terrain`); log-skip on null handles.
- `godot/src/Core/MainScene.cs`, `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`, `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — register the phase after `ScenarioLoad` (needs the loaded scenario + `ElevationGrid`) in all three in lockstep.
- `godot/ProjectChimera.Sim.Tests/**` — **NEW** Tier-1 tests (see Tasks). New golden `Golden/pathability-block-scenario.golden.txt` + replay wiring. Re-baseline `CanonicalModelHashTests`, `StartStateHashTests`, `HeroStartStateGoldenTests` (`hero-start-state.golden.txt`), `VersionStampConsistencyTests`.

## Tasks & Acceptance

**Execution:**
- `PathabilityGrid.cs` (NEW) — Godot-free bitset grid + clamped `IsBlocked` + `Digest()`; `Empty`/degenerate-safe.
- `EntityWorld.cs` — `Pathability` + `SetPathabilityGrid`.
- `MovementSystem.cs` — post-integration blocked-cell rejection; null-grid no-op.
- `FlowFieldSystem.cs` — `SetStaticBlocked` + OR-in during `RebuildObstacles`.
- `ScenarioData.cs` — `PathabilityBlocked` (omit-when-null) + `SlopeAutoBlock`/`SlopeBlockThreshold` (omit-when-default); normalize all-clear → null at the serialize chokepoint (the empty-`Regions`→null precedent).
- `CanonicalModelHash.cs` — fold pathability digest + slope config; `AlgoVersion` 5→6; documented divergence.
- `ScenarioValidator.cs` — spawn/start/`spawn_unit`-in-blocked fail-closed, same cell domain as the sim.
- `ScenarioApplier.cs` — `SetPathabilityGrid`; thread into `EntityWorld` before spawn.
- `ScenarioLoadPhase.cs` — `BuildAndInjectPathabilityGrid` (decode + slope-derive + union + inject 4 sinks).
- `PathabilityTool.cs` (NEW) + `PathabilityToolPhase.cs` (NEW) — paint/erase brush, panel (incl. slope toggle+threshold), `MultiMesh` overlay (`P`), shared stroke-undo, phase-registered (4-file order update).
- Tests — `PathabilityGridTests` (clamped lookup, edge cells, empty/degenerate, `WorldToCell` parity); `ScenarioDataPathabilityTests` (bitset round-trip; all-clear→key absent; slope defaults omit); `CanonicalModelHashPathabilityTests` (painted layer + slope config move CMH; empty layer == post-rebaseline baseline; `AlgoVersion` 6; propagates to `StartStateHash`); `MovementSystemBlockingTests` (commanded-into-wall stops; null-grid byte-identical; separation-shove rejected; two-replay determinism); `FlowFieldBlockingTests` (BFS routes around; identical field across two loads — the 6.2 pattern); `SlopeAutoBlockTests` (threshold derivation from a synthetic `ElevationGrid`; OFF ⇒ zero cells); `ScenarioValidatorPathabilityTests` (start/unit/`spawn_unit` in blocked → fail closed; clear passes); new golden `pathability-block-scenario` replay proving a unit is blocked deterministically.

**Acceptance Criteria:**
- Given the pathability brush in Edit mode, when the author paints then erases blocked cells, then the painted layer changes, the overlay reflects it, and each stroke undoes/redoes as a single step interleaved with entity placement without corruption; when the map is saved/reloaded and exported→imported, the blocked set round-trips identically.
- Given a map with painted blocked cells, when the flow field computes and units move, then the flow field excludes blocked cells with identical field results across two loads, and a live unit commanded across a blocked wall stops at the boundary and never occupies a blocked cell, byte-identically across two same-seed replays; the change is captured by the new `pathability-block-scenario` golden.
- Given a flat/legacy map with no painted cells and slope-auto-block OFF, when it is serialized and simulated, then `scenario.json` omits the pathability field, the bytes are byte-identical to pre-feature, `SimChecksum.AlgoVersion` stays 15, and all 23 per-tick goldens are unchanged.
- Given two clients whose painted pathability layers differ, when they compute `CanonicalModelHash`/`StartStateHash`, then the hashes differ and the mismatch is rejected at the handshake (not an in-sim desync); this is a one-time, explicitly-stated re-baseline of `CanonicalModelHash`/`StartStateHash`/`hero-start-state.golden.txt`/`VersionStampConsistencyTests` with `AlgoVersion` 5→6.
- Given slope-auto-block enabled on a sculpted map, when the grid builds, then steep cells auto-block consistently with the painted layer and appear in the overlay; with the toggle OFF a flat map behaves byte-identically to pre-feature.
- Given a start/unit/`spawn_unit` position on a painted blocked cell, when it passes through `ScenarioValidator`, then validation fails closed with a clear message before any tick; a clear cell passes.

## Spec Change Log

## Review Triage Log

### 2026-07-14 — Review pass 1 (post-implementation adversarial review: 4 layers — blind/edge/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 4
- reject: 10
- addressed_findings:
  - `[medium]` `[patch]` **Validator soft-lock gap (Blind #3).** The story added a fail-closed blocked-cell check to start bases, pre-placed units, and `spawn_unit` triggers, but not to resource nodes or pre-placed buildings — a resource node painted onto a blocked cell is unreachable by gatherers (soft-lock) yet passed validation. Added `CheckNotBlocked` to both the resource-node loop (reachability) and the building loop (consistency with the start-base check), positioned early in each `??` chain so a blocked placement fails before the later type/collection gates. (`ScenarioValidator.cs`; tests `ResourceNodeOnBlockedCell_FailsClosed`, `PrePlacedBuildingOnBlockedCell_FailsClosed`.)
  - `[medium]` `[patch]` **Load-composition unverified (VG1).** `BuildAndInjectPathabilityGrid`'s decode→slope-derive→union decision was inline in the Godot phase with no test — dropping a sink or inverting the elevation/derive order would ship green (all helper tests inject grids directly). Extracted the pure decision into `PathabilityGrid.Resolve(paintedBase64, slopeAutoBlock, threshold, elev)` (Godot-free, returns the union grid or null), which `ScenarioLoadPhase` now calls and fans out; unit-tested for painted-only / slope-only / both / flat→null. (`PathabilityGrid.cs`, `ScenarioLoadPhase.cs`; test `Resolve_PaintedOnly_FlatNull_SlopeOnly_And_Both`.)
  - `[low]` `[patch]` **DigestOfBase64 non-canonical fold (Blind #2 / Edge E1).** The handshake digest folded the raw decoded bytes, so two base64 encodings that unpack to the same mask (short/over-long blob) hashed differently and disagreed with the instance `Digest()` — a hand-authored non-canonical map would false-reject at the handshake against a tool-saved (always-2048-byte) one, contradicting the class doc. Now folds `Pack(Unpack(bytes))` (the sim's canonical 2048-byte mask). Behavior-preserving for all canonical/tool-saved maps, so the `CanonicalModelHash` baseline is unmoved. (`PathabilityGrid.cs`; test `DigestOfBase64_CanonicalizesNonCanonicalEncodings`.)
  - `[low]` `[patch]` **Unbounded slope threshold (Edge E6).** `SlopeBlockThreshold` (folded into `CanonicalModelHash` via `Fixed.FromFloat`) had no validation, so a non-finite/huge value would overflow the float→Fixed boundary with a platform-unspecified result → potential false handshake reject. The validator now gates it finite / non-negative / inside the Fixed range, mirroring `map_bounds`; default 0f passes unchanged. (`ScenarioValidator.cs`; tests `SlopeBlockThresholdOutOfRange_FailsClosed`, `SlopeBlockThresholdInRange_Passes`.)
- deferred (4, in `deferred-work.md`): **swept-cell tunneling** (fast unit / 1-cell wall crosses on endpoint-only rejection — unreachable with realistic speeds); **slope-derived spawn/already-blocked-roam** (validator sees painted-only; an in-blocked unit may traverse blocked cells — narrow, slope-auto-block default OFF); **slope forward-only differences** (far-edge cliffs miss, asymmetric wall — default-OFF feature); **PathabilityTool cell-mapping duplication** (re-implements `FlowField.WorldToCell` — presentation, agrees today).
- rejected (10): **NavMesh not carved** (intent R2 — `NavigationServer3D`/`PathRequestSystem` is vestigial for routing; `MatchLifecycleController` wires `OnRequestPath → FlowFieldBridge`, the live deterministic pather this story hooks via `SetStaticBlocked`); **route-around not wired at movement layer** (false alarm — wired via `FlowFieldBridge` in `_Process`; the golden tests the sim clamp by design); **paint doesn't update live sim in Edit** (the established "takes effect next load" authoring pattern from 6.3/6.4); **hash over-folds inert slope config** (over-conservative, safe direction; only independently-authored inert-config maps, and the threshold is now range-bounded); **concave-corner jam** (deterministic gameplay polish; the flow field routes around in the live game); **malformed-base64 data loss on save** (external-corruption-only; dropping unusable garbage to null is acceptable recovery); **slope→consume not e2e** (the union path is now covered by `Resolve` tests); **golden `checksum_algo_version` header is doc-only** (informational; the explicit `AlgoVersion==15` assertions cover it); **`Clear()` reset untested** (mitigated — `Apply` unconditionally re-injects); **handshake-vs-per-tick re-baseline** (the deliberate, precedent-backed design — see Design Notes).

## Design Notes

**The hash decision (the load-bearing call).** This is the deliberate inverse of 6.4's Regions decision. Regions were *excluded* from `CanonicalModelHash` because they feed only the trigger system (Triggers-parity posture). Pathability blocking feeds **movement** — a core sim system whose output (`Position`) is checksummed — so two peers with mismatched blocked layers produce divergent paths and desync from the first move order. The established fix for lockstep-critical start-state (StartCrystal/Supply, `CanonicalModelHash.cs:26-41`) is **handshake rejection**, not in-sim detection. So the authored layer folds into `CanonicalModelHash` (→`StartStateHash` via seed), forcing a one-time re-baseline of the handshake fixtures — the "explicit, named step" the epic mandates (context line 28/55). We deliberately do **not** fold into per-tick `SimChecksum`: the effect is already transitively captured by `Position`, and folding a static per-map grid there would churn all 23 tick goldens for zero behavioral signal, breaking the "flat maps byte-identical" guarantee at the per-tick level. Slope-derived cells depend on the terrain heightmap (which rides in `.res`/`TerrainHash`, not `CanonicalModelHash` — `TerrainRef` is neutralized); they therefore inherit 6.3's accepted terrain-not-in-handshake residual, while the slope *config* (toggle+threshold) IS folded.

**Deterministic teeth live in the tick, not the flow field.** `FlowFieldSystem`/`FlowFieldBridge` run in `_Process` (presentation), so a pure-sim golden harness can't prove blocking through them. The deterministic guarantee ("units never path into blocked cells") therefore lives in `MovementSystem.Tick` as a post-integration cell rejection; the flow-field OR-in is the live-game "route around" nicety. Both consume the one `PathabilityGrid` union built at load.

**One canonical resolution.** Painted cells and slope-derived cells both resolve to the **128²/2-unit** flow grid so paint, validator, sim enforcement, and flow-field BFS share one cell identity (`FlowField.WorldToCell`). The `ElevationGrid` is 256²/1-unit; slope derivation samples elevation at each flow-cell's footprint (2×2 down-sample) at build time. Only the painted bitset + slope config persist; derived cells are recomputed deterministically at load.

## Verification

**Commands:**
- `dotnet build godot.sln` — expected: clean compile, 0 errors, no new banned-API analyzer warnings on the `PathabilityGrid`/`MovementSystem`/`ScenarioValidator` sim path (no `float`/`Mathf`/`System.Random`; the single float→`Fixed` boundary is the load-time decode/slope-derive).
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all new pathability tests green; the **23 per-tick goldens + `KnownWorldState_ProducesPinnedV15Hash` + `SimChecksum.AlgoVersion==15` UNCHANGED** (any movement there = wrongly folded into SimChecksum → STOP and fix); `CanonicalModelHashTests`/`StartStateHashTests`/`hero-start-state.golden.txt`/`VersionStampConsistencyTests` re-baselined **once** with `AlgoVersion` 5→6 (record via `CHIMERA_GOLDEN_RECORD=1` then rebuild). Note the 2 pre-existing `PersistenceManifestTests` failures on baseline (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`); confirm unrelated via `git stash` if seen — any *other* new failure is real.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for the tool, drag input, `MultiMesh` overlay, or `.chimera.zip` UI):**
- Paint and erase blocked cells in Edit mode; toggle the overlay with `P`; confirm red cells render and are hidden in Play.
- Save/reload and export→import `.chimera.zip`; confirm the blocked set persists identically.
- Exercise undo/redo across paint/erase interleaved with entity + region placement; confirm no cross-corruption.
- In Play, command units toward a walled-off area; confirm they route around (flow field) and none enters a blocked cell.
- Enable slope-auto-block on a sculpted map; confirm steep cells appear blocked in the overlay and clear when the toggle is OFF.
- Confirm `ScenarioValidator` rejects a map with a start/spawn position on a painted blocked cell.

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 4 patches applied, 4 deferred, 10 rejected, 0 spec loopbacks, committed)

### Implemented change
Impassable-terrain pathability shipped across sim + persistence + editor: (1) a Godot-free deterministic `PathabilityGrid` (128²/2-unit/±128, mirroring `FlowField.WorldToCell`) carrying a painted blocked bitset ∪ optional slope-derived cells, resolved once at load via the pure `PathabilityGrid.Resolve`; (2) deterministic blocking enforced in `MovementSystem` (post-integration wall-slide/hard-stop, null-grid no-op) and routed-around by the live flow field via `FlowFieldSystem.SetStaticBlocked`; (3) the authored layer persisted on `ScenarioData` (`PathabilityBlocked` omit-when-null base64 bitset + `SlopeAutoBlock`/`SlopeBlockThreshold` omit-when-default) and folded into `CanonicalModelHash` (AlgoVersion 5→6) — the deliberate inverse of 6.4's Regions decision — so mismatched peers are rejected at the MP handshake instead of desyncing in-sim; (4) fail-closed `ScenarioValidator` on start/unit/`spawn_unit`/resource-node/building positions over painted blocked cells, plus a bounded slope threshold; (5) a Creation Suite `PathabilityTool` (K-toggled paint/erase brush, `MultiMesh` red overlay toggled by `P`, shared stroke-undo) via a new `PathabilityToolPhase`. Per-tick `SimChecksum` stays 15 (blocking reaches it transitively via `Position`); flat/legacy maps are byte-identical (grid null).

### Files changed (one line each)
- `godot/src/Navigation/PathabilityGrid.cs` (NEW) — Godot-free 128²/2-unit blocked grid: clamped `IsBlocked`, pack/unpack/base64, canonicalized `Digest`/`DigestOfBase64`, pure-`Fixed` slope derivation, and the pure `Resolve` load-time union (P: VG1, digest canonicalize).
- `godot/src/Core/EntityWorld.cs` — `Pathability` + `SetPathabilityGrid`; reset to null in `Clear()`.
- `godot/src/Navigation/MovementSystem.cs` — post-integration blocked-cell rejection (wall-slide then hard-stop); null/all-clear no-op.
- `godot/src/Navigation/FlowFieldSystem.cs` — `SetStaticBlocked` + OR the static mask into `_obstacles` in `RebuildObstacles`.
- `godot/src/Core/Definitions/ScenarioData.cs` — `PathabilityBlocked` (omit-when-null) + `SlopeAutoBlock`/`SlopeBlockThreshold` (omit-when-default).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — folded pathability digest + slope config; `AlgoVersion` 5→6; documented inverse-of-Regions rationale.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — normalize all-clear painted layer → null at the serialize chokepoint (swap/restore, no caller mutation).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — fail-closed on start-base/unit/`spawn_unit`/resource-node/building on a painted blocked cell; bounded `SlopeBlockThreshold` (P: Blind #3, Edge E6).
- `godot/src/Core/Sim/ScenarioApplier.cs` — `SetPathabilityGrid` + thread into `EntityWorld` before spawns.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — `BuildAndInjectPathabilityGrid` now calls the pure `Resolve` and fans out to applier/FlowFieldSystem/SceneContext (P: VG1).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — `Pathability` handle for the overlay tool.
- `godot/src/CreationSuite/PathabilityTool.cs` (NEW) — K-toggled paint/erase brush, `[`/`]` sizing, Paint/Erase + slope config panel, single-`MultiMesh` red overlay (P toggle, Edit-only), one shared `EditorHistory` push per stroke.
- `godot/src/Core/Bootstrap/Phases/PathabilityToolPhase.cs` (NEW) — phase wiring (log-skips on null handles).
- `godot/src/Core/MainScene.cs`, `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`, `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — registered `PathabilityTool` after `RegionTool` in lockstep.
- Tests (NEW): `PathabilityGridTests` (+`Resolve_*`, +`DigestOfBase64_Canonicalizes*`), `MovementSystemBlockingTests`, `FlowFieldBlockingTests`, `SlopeAutoBlockTests`, `ScenarioDataPathabilityTests`, `CanonicalModelHashPathabilityTests`, `ScenarioValidatorPathabilityTests` (+resource-node/building/threshold), `PathabilityBlockScenario`+`PathabilityBlockGoldenTests`+`pathability-block-scenario.golden.txt`.
- Re-baselined (one-time, `CanonicalModelHash` 5→6): `CanonicalModelHashTests`, `CanonicalModelHashRegionExclusionTests`, `VersionStampConsistencyTests`, `HeroProfilePersistenceTests`, `SimResetTests`, `ScenarioApplierTests` (pinned hash), `hero-start-state.golden.txt`.

### Review findings breakdown (review pass 1, 4 layers)
- Patches applied: 4 — Blind #3 (validator resource-node/building soft-lock), VG1 (extract testable `Resolve`), Blind #2/Edge E1 (digest canonicalize), Edge E6 (bound slope threshold).
- Deferred: 4 — swept-cell tunneling; slope-derived spawn/already-blocked-roam; slope forward-only edge/asymmetry; PathabilityTool cell-mapping duplication (all in `deferred-work.md`).
- Rejected: 10 — see Review Triage Log (NavMesh-vestigial R2, flow-field route-around wired via FlowFieldBridge, live-edit-next-load pattern, inert-config hash, corner-jam, etc.).

### Verification
- `dotnet build godot.sln` → Build succeeded, 0 errors, 0 warnings (production sim path clean; no new banned-API/determinism analyzer warnings on `PathabilityGrid`/`MovementSystem`/`ScenarioValidator`/`ScenarioLoadPhase`).
- `dotnet test ProjectChimera.Sim.Tests` → **1611 passed, 2 failed, 1 skipped**. The 2 failures are the named pre-existing baseline failures (`PersistenceManifestTests.ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`), **git-stash-verified** on the clean baseline (2 failed / 29 passed) — unrelated to pathability.
- Golden discipline ground-truthed: only `hero-start-state.golden.txt` moved among goldens (the sanctioned handshake re-baseline); the **23 per-tick goldens are byte-unchanged**; `SimChecksum.AlgoVersion==15`, `CanonicalModelHash.AlgoVersion==6`. The digest canonicalization patch is behavior-preserving for canonical (all tool-saved/golden) inputs, so the re-baselined hash values did not move again after patching.
- Manual (godot-mcp / godot-verify) checks NOT executed in this unattended run — no xUnit surface for paint/erase drag, `MultiMesh` overlay visibility, `.chimera.zip` UI, or cross-tool undo interleave; see Verification section. Tool + phase compile and follow the `RegionTool`/`TerrainBrush` patterns.

### Follow-up review recommendation: **false**
The 4 patches are localized and fully test-covered (9 new tests, all green), the two determinism-adjacent ones (digest canonicalize, validator) are behavior-preserving for the golden/hash baseline (verified unmoved), and no existing scenario is newly rejected. Not significant enough to warrant an independent follow-up pass.

### Residual risks
- Slope-auto-block (default OFF) inherits 6.3's accepted terrain-not-in-handshake posture: slope-derived cells depend on the terrain heightmap (rides `TerrainHash`, not `CanonicalModelHash` since `TerrainRef` is neutralized), so a peer terrain mismatch under slope-auto-block isn't handshake-caught (the painted layer IS). The slope *config* is folded.
- Deterministic blocking is proven via the sim clamp (non-occupancy) in a real per-tick replay golden; the flow-field route-around is proven only as a field property, not joined end-to-end in a sim replay (needs `FlowFieldBridge`, presentation).
- The 4 deferred edge cases (tunneling, slope-spawn validation, slope edges, tool cell-mapping) — all narrow / default-OFF / presentation, tracked in `deferred-work.md`.
- Manual in-engine verification of the editor tool/overlay/package UI is outstanding (unattended run).
