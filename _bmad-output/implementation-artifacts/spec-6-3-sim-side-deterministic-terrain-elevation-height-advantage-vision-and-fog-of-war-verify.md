---
title: 'Sim-side deterministic terrain elevation + height-advantage vision (and fog-of-war verify)'
type: 'feature'
created: '2026-07-14'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '036eba2306a05c7acb50d471e170a692bf0bbf95'
final_revision: 'a36dc1cee89c59aee085b43c92b41fac853a621d'
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The simulation carries no terrain elevation — every entity spawns at a flat all-zero heightmap (`TerrainPhase.cs:62` imports a zero RF image; `ScenarioApplier` hardcodes `Fixed.Zero` for Y at every spawn), so there is no high-ground tactical reason to seize elevated positions. The as-built `FogOfWarSystem` (3-state 128×128 byte Grid, per-unit `StampCircle` keyed on `VisionRange`, GPU R8 + minimap + spectator RevealAll) is fully built but unverified as a regression baseline.

**Approach:** (1) VERIFY the as-built fog/vision system with zero behavioural change when the new feature is OFF. (2) Add a new Godot-free `Fixed[] Elevation` parallel SoA array on `EntityWorld`, populated at spawn by deterministically sampling the authored heightmap via clamped integer cell lookup (Fixed only). (3) Add a per-scenario `HeightAdvantageVision` creator toggle (default OFF) + a configurable per-step Fixed bonus that, only when enabled, widens the stamped vision radius by an elevation-derived term computed entirely in `Fixed` before the existing `.ToFloat()` boundary. (4) Fold `Elevation` into `SimChecksum.Compute`, bump `AlgoVersion` 14→15, and re-baseline all 23 per-tick goldens as an acknowledged, intentional sim-state expansion.

## Boundaries & Constraints

**Always:**
- Sim layer stays Godot-free and `Fixed`-only, ascending-entity-id iteration. Elevation is sampled via **clamped integer cell lookup over a `Fixed[]` grid**, never Godot `Image` interpolation inside the sim, never an out-of-bounds read / NaN / exception.
- The height bonus term is computed in `Fixed` and added to the base `Fixed VisionRange` **before** the existing per-tick `.ToFloat()` conversion in `FogOfWarSystem.Tick` — introduce no new `float`/`double`/`Mathf` on any sim path.
- Elevation is stored in a **new dedicated `Fixed[] Elevation` SoA array**, defaulted in `EntityWorld.Create()` with a recycle-trap comment. It is **not** written by `ApplyUnitDefinition` (terrain-derived, not def-derived).
- Feature OFF (default) ⇒ the stamped fog `Grid` is **byte-for-byte identical** to the pre-feature Grid for an identical scenario (the bonus term is not applied at all).
- Folding `Elevation` into `SimChecksum` re-baselines every per-tick golden — perform the version bump (`SimChecksum.AlgoVersion` 14→15, `VersionStampConsistencyTests.ExpectedSimChecksumAlgoVersion`, the pinned known-state hash) and the golden re-record together in one commit, stated explicitly as an intentional expansion.

**Block If:**
- Any sim system (combat target acquisition, `AiOpponentSystem`, `FlowFieldComputer`/`FlowFieldSystem`, `NavObstacleManager` bake) is found to consume the fog `Grid`/`IsVisible` **or** the new `Elevation` for a deterministic decision that feeds `SimChecksum` — HALT rather than (a) silently make the toggle lockstep-critical without folding it into the MP handshake, or (b) alter pathfinding/nav determinism in this vision-only story.
- Sampling or storing elevation is found to feed the NavMesh bake or flow-field results in any way (the 6.3 vision-only scope limit; 6.5 is where elevation→blocking becomes intentional).

**Never:**
- Never put elevation in `Position.Y` (it is already folded into `SimChecksum:176` and would move goldens as position, and could leak through `MovementSystem` integration).
- Never fold `HeightAdvantageVision`/the bonus into `CanonicalModelHash` or `StartStateHash`; `CanonicalModelHash.AlgoVersion` stays 5 and `StartStateHash.AlgoVersion` stays 2 — the fog Grid is not in `SimChecksum` and no sim system consumes it, so the toggle is not lockstep-critical (verified: only `MinimapPhase`/`RenderingPhase` read `_ctx.Fog`). `hero-start-state.golden.txt` must NOT move.
- Never rebuild `FogOfWarSystem`/`FogOfWarBridge`/`MinimapBridge` — verify them, do not rewrite.
- Never read the Godot `Terrain3D` node from the sim layer; the heightmap→`Fixed` grid conversion is a Godot-side load-time step.
- Never add a bespoke elevation subsystem when a new `EntityWorld` SoA array + the existing `SimChecksum` fold discipline cover it.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Flat/legacy map | heightmap all-zero (or no `ElevationGrid` injected / null) | every entity `Elevation == Fixed.Zero`; no behavior change vs pre-feature | No error |
| Sculpted hill | entity spawns at world XZ over non-zero authored height | `Elevation[id]` = the deterministic clamped-cell `Fixed` sample at that XZ | No error |
| Sample at/outside heightmap edge | world XZ mapping to an out-of-range cell | clamps to nearest valid cell, returns a finite `Fixed`; sim does not crash/desync | Clamp, no OOB read |
| Vision, toggle ON, elevated unit (≥1 step) | `HeightAdvantageVision=true`, `Elevation[id] ≥ 1 step`, `BonusPerStep>0` | stamps a **strictly larger** Visible radius = base `VisionRange` + Fixed elevation bonus | No error |
| Vision, toggle ON, ground unit | `HeightAdvantageVision=true`, `Elevation[id] == Fixed.Zero` | stamped radius == base `VisionRange`, unchanged | No error |
| Vision, toggle OFF, any elevation | `HeightAdvantageVision=false` | stamped radius == base `VisionRange`; fog `Grid` byte-identical to pre-feature | No error |
| Determinism replay | Elevation folded into `SimChecksum`, same seed twice | byte-identical per-frame checksums both runs; golden re-baselined to new value | No error |

</intent-contract>

## Code Map

- `godot/src/Core/EntityWorld.cs` -- add `public readonly Fixed[] Elevation;` (~decl :220, alloc `new Fixed[MAX_ENTITIES]` ~:610); default `Elevation[id] = Fixed.Zero;` in `Create()` (~:707) with a recycle-trap comment. Add sim-global config fields `public bool HeightAdvantageVision;` + `public Fixed HeightVisionBonusPerStep;` (default false/Zero) and an optional injected `ElevationGrid`; sample `Elevation[id] = _elevationGrid?.Sample(position.X, position.Z) ?? Fixed.Zero;` inside `Create()` (has `position`) so ALL spawn paths get correct elevation uniformly. Add `public Fixed EffectiveVisionRange(int id)` (base + Fixed height bonus when toggle on). Do NOT touch `ApplyUnitDefinition` (:809) or `Position.Y`.
- `godot/src/Core/ElevationGrid.cs` -- **NEW** Godot-free type: `Fixed[] Heights`, `int Width/Height`, world-extent metadata (`Fixed WorldMinX/WorldMinZ/CellSize`), and `Fixed Sample(Fixed worldX, Fixed worldZ)` doing clamped integer cell lookup. Pure `Fixed` — the Tier-1-testable core of AC5.
- `godot/src/Core/FogOfWarSystem.cs` -- in `Tick` (:64-66), replace `float radius = world.VisionRange[id].ToFloat();` with `float radius = world.EffectiveVisionRange(id).ToFloat();`. Nothing else changes (StampCircle float math is the as-built, verified-not-rewritten path; the new term is Fixed and merges before `.ToFloat()`).
- `godot/src/Core/SimChecksum.cs` -- fold `hash = Mix(hash, world.Elevation[i].Raw);` in the per-alive-entity loop (after Health, ~:178). Bump `AlgoVersion` 14→15 (:153) with a v15 doc entry.
- `godot/src/Core/Definitions/ScenarioData.cs` -- add `bool HeightAdvantageVision` (default false, `JsonIgnoreCondition.WhenWritingDefault` so existing files serialize byte-identically) + `float HeightVisionBonusPerStep` (default 0f) after `Supply` (~:335). Do NOT add either to `CanonicalModelHash.Compute`.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- build/inject the toggle+bonus and the `ElevationGrid` into `EntityWorld` before spawning: `world.HeightAdvantageVision = scenario.HeightAdvantageVision; world.HeightVisionBonusPerStep = Fixed.FromFloat(scenario.HeightVisionBonusPerStep); world.SetElevationGrid(grid);`. `SpawnUnitAt` (:309) needs no per-callsite elevation edit once `Create` samples the grid. Keep `Position.Y == Fixed.Zero` everywhere.
- `godot/src/Core/Bootstrap/Phases/TerrainPhase.cs` (or the Godot-side phase that owns `SceneContext.Terrain` and runs **after** 6.2 terrain restore, **before** scenario apply) -- read the finalized Terrain3D heightmap `Image` (RF) → build a `Fixed[]` `ElevationGrid` (one `Fixed.FromFloat` per cell at this load-time boundary), and hand it to `ScenarioApplier`/`EntityWorld`. Confirm ordering via `ScenePhaseOrder`/`PhaseOrderTest`.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- add an Elevation tooth to `EntityCommandFields_AreFoldedIntoTheChecksum` (:139); rename `KnownWorldState_ProducesPinnedV14Hash`→V15, `Assert.Equal(15,...)`, re-pin `ExpectedV14Hash`→`ExpectedV15Hash` new value.
- `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` -- `ExpectedSimChecksumAlgoVersion` 14→15 (:58). Leave `CanonicalModelHash`(5)/`StartStateHash`(2) untouched.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` -- add `RecycledSlot_CarriesNoPriorElevation` (Create→dirty→Destroy→Create→assert `Fixed.Zero`). Do NOT add Elevation to the def-derived mapper tests.
- `godot/ProjectChimera.Sim.Tests/**` -- **NEW** `ElevationGridTests` (clamped lookup incl. edge/OOB → nearest valid cell, flat→Zero) and `HeightAdvantageVisionTests` (Godot-free: construct world, tick `FogOfWarSystem`, assert toggle-ON elevated Grid > ground, toggle-OFF Grid byte-identical to base-`VisionRange`-only Grid).
- Golden `.txt` files (`godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt`, the 23 per-tick SimChecksum goldens) -- re-recorded via `CHIMERA_GOLDEN_RECORD=1`.

## Tasks & Acceptance

**Execution:**
- `ElevationGrid.cs` (NEW) -- Godot-free `Fixed` grid + `Sample(worldX, worldZ)` with clamped integer cell lookup; carries dims + world extent so it is testable with small grids and general over resolution.
- `EntityWorld.cs` -- add `Fixed[] Elevation` (decl+alloc); default in `Create()` (recycle-trap comment); add sim-global `HeightAdvantageVision`/`HeightVisionBonusPerStep` + `SetElevationGrid`; sample `Elevation[id]` in `Create()` from the injected grid; add `EffectiveVisionRange(id)` computing `VisionRange[id] + (HeightAdvantageVision ? steps(Elevation[id]) * HeightVisionBonusPerStep : Fixed.Zero)` with `steps = floor(Elevation)` clamped ≥0 (deterministic Fixed integer math; see Design Notes).
- `FogOfWarSystem.cs` -- use `world.EffectiveVisionRange(id)` in `Tick`; verify (grep + read) nothing else consumes the Grid for a sim decision.
- `SimChecksum.cs` -- fold `Elevation[i].Raw`; bump `AlgoVersion` 14→15 + doc entry.
- `ScenarioData.cs` -- add the toggle + bonus fields (default-omitting); NOT folded into `CanonicalModelHash`.
- `ScenarioApplier.cs` -- thread toggle/bonus/grid into `EntityWorld` before spawn; keep `Position.Y == Fixed.Zero`.
- `TerrainPhase.cs` (Godot side) -- build the `Fixed[]` `ElevationGrid` from the finalized heightmap Image after restore, before scenario apply; confirm phase ordering.
- Tests -- `ElevationGridTests`, `HeightAdvantageVisionTests`, `RecycledSlot_CarriesNoPriorElevation`, the `EntityCommandFields` Elevation tooth, the re-pinned V15 known-state hash, and the `VersionStampConsistencyTests` bump.
- Golden re-record -- `CHIMERA_GOLDEN_RECORD=1 dotnet test ProjectChimera.Sim.Tests` (all 23 SimChecksum goldens), then `dotnet build`, then commit the moved `.txt` files; state in the commit that this is the intentional Elevation-fold re-baseline.

**Acceptance Criteria:**
- Given a running match with P1 units, an out-of-vision enemy, and the RevealAll path, when the match ticks with the feature OFF, then the as-built `FogOfWarSystem` behavior is confirmed unchanged: Visible→Explored demote then per-unit `StampCircle` on `VisionRange`, `FogOfWarBridge` R8 upload + minimap render the 3 states, and spectator `RevealAll` sets every cell Visible — with zero behavioural change to the fog Grid.
- Given a scenario whose authored heightmap has sculpted hills, when it loads and entities spawn, then each entity's `EntityWorld.Elevation[id]` is populated by deterministically sampling the heightmap at its world X/Z, and a flat/legacy heightmap yields `Elevation == Fixed.Zero` for every entity.
- Given `HeightAdvantageVision` ENABLED and an elevated unit (≥1 elevation step) plus a same-type ground unit, when `FogOfWarSystem` stamps both, then the elevated unit stamps a strictly larger Visible radius (base + configured Fixed per-step bonus) while the ground unit's stamped radius equals its base `VisionRange`.
- Given `HeightAdvantageVision` DISABLED (default), when any unit at any elevation stamps vision, then the stamped radius equals exactly its base `VisionRange` and the fog Grid is byte-for-byte identical to the pre-feature Grid for an identical scenario.
- Given an entity sampled at or outside the heightmap edge / an out-of-range XZ, when Elevation is sampled, then it clamps to the nearest valid cell (no OOB read, NaN, or exception) and returns a finite Fixed; the sim does not desync or crash.
- Given the golden-checksum harness, when `Elevation` is folded into `SimChecksum.Compute` (ascending id, alongside Position/Health) and a match is replayed twice from the same seed, then both runs produce byte-identical per-frame checksums AND the goldens are re-baselined to the new values with `AlgoVersion` 14→15 (intentional expansion, not a regression); `CanonicalModelHash`/`StartStateHash` and `hero-start-state.golden.txt` are unchanged.

## Spec Change Log

## Review Triage Log

### 2026-07-14 — Review pass 1 (post-implementation adversarial review: 4 layers)

- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 3, low 3)
- defer: 1: (medium 1)
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` **VG1 — the feature's activation path was untested.** Every 6.3 test configured a raw `EntityWorld` by hand and bypassed `ScenarioApplier.Apply`, so a dropped/ inverted threading of the toggle/bonus/grid would ship a silently-dead feature green. Added `Apply_ThreadsStory6_3_HeightVisionConfig_AndSamplesElevationAtSpawn` (Godot-free) asserting `Apply` threads the toggle/bonus and a spawned unit samples the injected grid. (`ScenarioApplierTests.cs`.)
  - `[medium]` `[patch]` **VG2 — the reset-equivalence guard did not pin the new sim-globals or `Elevation`.** A dropped reset in `ClearForReset` would let a prior map's height-vision config survive into the next match (reset != fresh boot). Populated the toggle/bonus/grid + a dirtied `Elevation` slot before reset and asserted all reset to fresh, incl. a post-reset spawn sampling the null grid (proves `_elevationGrid` reset). (`SimResetTests.cs`.)
  - `[medium]` `[patch]` **F6 — stale-grid robustness + a misleading comment.** `BuildAndInjectElevationGrid`'s early-return / no-terrain / catch paths never cleared the applier's grid, and the fallback comment described a call that never happens. Not reachable today (per-`MainScene` applier; reset re-applies the SAME scenario) but a latent trap. Now every no-build path explicitly `SetElevationGrid(null)`; comment corrected. (`ScenarioLoadPhase.cs`.)
  - `[low]` `[patch]` **VG3 — no serialization round-trip for the two new `ScenarioData` fields.** A wrong `JsonPropertyName` / dropped `WhenWritingDefault` would silently drop the toggle on load (unfolded ⇒ no hash catches it). Added `ScenarioDataHeightVisionTests` (round-trip + default-omits-both-keys). (`ScenarioDataHeightVisionTests.cs`.)
  - `[low]` `[patch]` **F4 — `HEIGHT_STEP_WORLD_UNITS` was documented as tunable but ignored by the bare `>> FRACTIONAL_BITS` shift.** Setting it to 2 would silently not reconfigure the math. `EffectiveVisionRange` now divides by the named constant (`ToInt() / HEIGHT_STEP_WORLD_UNITS`), making it load-bearing. (`EntityWorld.cs`.)
  - `[low]` `[patch]` **EC1 — a GIGO negative `HeightVisionBonusPerStep` produced a below-base / negative stamped radius.** Height vision is an ADVANTAGE; clamped the bonus term ≥ 0 so it never reduces a unit's vision (nor drives a negative `StampCircle` radius). (`EntityWorld.cs`.)
- deferred (logged to `deferred-work.md`):
  - `[medium]` **F3 — the SimChecksum-folded elevation grid is built via Godot `get_height` float interpolation (cross-platform determinism risk).** Not reachable today (all scenarios flat; ticking clients are x64; the server does not tick). A proper fix reads raw per-region height-map cells (the epic's "never Godot Image interpolation" rule). Tracked, not fixed here (Godot-side, no Tier-1 surface; non-trivial).
- notes: **Rejected (9).** **F1 (CRITICAL as raised — "headless server folds Elevation==0 → guaranteed desync"): REJECTED.** Verified `ServerBootstrap`'s host is a validated START-STATE reference only — `MainScene.cs:244` "The server does NOT tick this"; the server is a relay + quorum COLLECTOR over client peers (`ServerHost.OnChecksum(slot,…)`, MainScene.cs:1706 "relay + quorum only"; the F9 desync note quorums over N=2 CLIENTS). All clients build the grid identically from the same scenario → they agree; the server never computes/compares a per-tick `SimChecksum`, and start-state attestation uses `StartStateHash` (Elevation-free). No desync. **F2 (delete→undo re-samples Elevation): REJECTED** — `Elevation` is a pure function of position; `RestoreUnit`→`Create(snap.Position)` re-samples it identically (self-healing), unlike arbitrary caller-owned residue, and editor undo is Edit-mode-only (units don't move). **F5/EC3 (FromFloat overflow): REJECTED** — needs |h|>32768 on a ±128 map (non-realistic; consistent with the codebase's other unclamped load-time FromFloat). **EC2 (steps·bonus overflow): REJECTED** — bounded by the 128×128 `StampCircle` grid clamp; deterministic. **EC4 (Width·Height int overflow in the degenerate guard): REJECTED** — only callers are the 256-cell builder + small tests; no caller passes billions of cells. **EC5 (fallback no longer calls `RestoreTerrainFromScenario`): REJECTED** — it reads `_ctx.Scenario?.TerrainRef`, unset/empty on the fallback path, so the old unconditional call was already a no-op there. **Half-cell center-vs-edge note: REJECTED** — build + Sample are internally consistent (both edge-anchored); benign ≤half-cell offset. **Live-editor gaps (BuildAndInjectElevationGrid heightmap read, phase reorder, GPU fog / minimap / RevealAll render): REJECTED as defects** — no Godot-free xUnit surface by the repo's Tier-1 architecture; intent-sanctioned live godot-mcp verification (the 6.1/6.2 precedent), captured under residual risks. No bad_spec loopback: the core change is determinism-correct for every reachable path (server relay+quorum, x64 clients, replay); all six patches are localized and checksum-neutral (fog/config/tests only — no golden moved).

## Design Notes

**Elevation is a separate SoA array, NOT `Position.Y`.** `SimChecksum:176` already folds `Position.Y`; storing elevation there would move goldens as *position* and risk `MovementSystem` integration carrying it. A dedicated `Fixed[] Elevation` folded explicitly (SimChecksum discipline is manual, not reflection) keeps the change intentional and isolated.

**Effective-vision math (all Fixed, merges before the existing float boundary).**
```
Fixed EffectiveVisionRange(int id) {
  Fixed baseR = VisionRange[id];
  if (!HeightAdvantageVision) return baseR;                 // toggle OFF ⇒ byte-identical fog
  int steps = Elevation[id].Raw > 0 ? (Elevation[id].Raw >> 16) : 0;  // floor to whole world-units, clamp ≥0
  return baseR + Fixed.FromInt(steps) * HeightVisionBonusPerStep;
}
```
Step size is fixed at 1 world height-unit (documented named constant; can become data later). Only the creator's `HeightVisionBonusPerStep` is authorable. The result feeds the *existing* `world.VisionRange[id].ToFloat()` call site unchanged — no new float on any path.

**Godot→sim seam.** `ScenarioApplier`/`EntityWorld` are Godot-free, so a Godot-side load-time step reads the finalized Terrain3D heightmap `Image` (RF; ±128 world XZ, 256×256 ⇒ 1 world-unit/cell for the default map, but `ElevationGrid` stores its own extent) into a `Fixed[]` via `Fixed.FromFloat` (the sanctioned load-time conversion boundary), then injects it. `EntityWorld.Create` samples it (has `position`), so every spawn path — scenario load, `SpawnUnitAt` (trigger/hero respawn), production, editor placement — gets uniform, correct elevation with no per-callsite edits. Null grid ⇒ `Fixed.Zero` (flat/legacy).

**CanonicalModelHash decision (the landmine).** `CanonicalModelHash.Compute` is a manual field-by-field fold; new `ScenarioData` fields do NOT auto-fold. The `HeightAdvantageVision` toggle affects only the fog Grid, which is (a) NOT folded into `SimChecksum` and (b) consumed only by presentation (`MinimapPhase`, `RenderingPhase` — verified by grep; no combat/AI/nav consumer). A toggle mismatch therefore cannot cause a lockstep desync, so it must NOT be folded into the MP-handshake hash — doing so would be an out-of-scope second golden re-baseline (`hero-start-state`) + an `AlgoVersion 5→6` bump the ACs never call for. This mirrors 6.2's TerrainRef-neutralization discipline. (If a future story makes vision feed a deterministic sim decision, THAT story folds it.)

**Golden re-baseline is expected and total.** Consistent with the v7–v14 history, `Compute` does one `Mix` per alive entity, and `Mix(0)` still shifts FNV — so all 23 per-tick SimChecksum goldens move the instant the fold lands, even though every current golden scenario is flat (`Elevation==0`). This is the AC-authorized intentional expansion. No golden scenario sculpts terrain, so non-zero elevation determinism is covered by `ElevationGridTests` + live godot-mcp, not a golden.

## Verification

**Commands:**
- `dotnet build godot.sln` -- expected: clean compile, 0 errors, no new analyzer warnings in touched sim files (no new float on a sim path).
- `dotnet test ProjectChimera.Sim.Tests` -- expected: green AFTER the version bump + golden re-record. Before re-recording, the 23 SimChecksum goldens + `KnownWorldState_ProducesPinnedV15Hash` + `VersionStampConsistencyTests` are RED (expected, this is the re-baseline). `hero-start-state.golden.txt`, `CanonicalModelHash`, and `StartStateHash` tests must STAY green — any movement there means the toggle/bonus were wrongly folded or elevation leaked into a start-state hash; STOP and fix. Note the 2 pre-existing `PersistenceManifestTests` failures on baseline (confirm unrelated via `git stash` if seen).
- `CHIMERA_GOLDEN_RECORD=1 dotnet test ProjectChimera.Sim.Tests` then `dotnet build` -- re-records + re-embeds the 23 goldens; commit the moved `.txt`.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for Terrain3D-node reads or GPU fog):**
- Load a sculpted map (6.2 restore path); via `godot_exec`/`godot_runtime_state` confirm spawned units on hills have non-zero `EntityWorld.Elevation[id]` and flat-area units have `Fixed.Zero`.
- Toggle `HeightAdvantageVision` ON: confirm an elevated unit reveals a visibly larger fog circle than an equal-`VisionRange` ground unit (screenshot the fog overlay / minimap); toggle OFF: confirm identical circles.
- Confirm spectator `RevealAll` still fills every cell Visible, and the minimap renders the same 3 states.
- Sanity: sculpted vs flat load produce the same NavMesh source-geometry face count (elevation did NOT leak into the bake).

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 6 patches applied, 1 deferred, committed)

### Implemented change
Sim-side deterministic terrain elevation + height-advantage vision. Verified the as-built `FogOfWarSystem` (unchanged when the feature is OFF). Added a Godot-free `Fixed[] Elevation` SoA array on `EntityWorld`, sampled once at spawn (in `Create`) from an injected Godot-free `ElevationGrid` (clamped integer cell lookup, never Godot interpolation in the sim). Added a per-scenario `HeightAdvantageVision` toggle + `HeightVisionBonusPerStep`; `EffectiveVisionRange` widens the stamped vision radius by an all-`Fixed` elevation-derived bonus only when enabled, merged before the existing `.ToFloat()` boundary. Folded `Elevation` into `SimChecksum` (`AlgoVersion` 14→15) — the AC-authorized re-baseline of all 23 per-tick goldens. The toggle/bonus are deliberately NOT folded into `CanonicalModelHash`/`StartStateHash` (fog is presentation-only, not lockstep-critical), so those hashes and `hero-start-state.golden.txt` are unchanged.

### Files changed (one line each)
- `godot/src/Core/ElevationGrid.cs` (NEW) — Godot-free `Fixed`-only grid + clamped-integer `Sample` (edge/OOB→nearest cell, degenerate→Zero, never throws).
- `godot/src/Core/EntityWorld.cs` — `Fixed[] Elevation` SoA + sim-globals (`HeightAdvantageVision`/`HeightVisionBonusPerStep`/`HEIGHT_STEP_WORLD_UNITS`/`_elevationGrid`), `SetElevationGrid`, `EffectiveVisionRange` (Fixed, floors via the named step, clamps bonus ≥0), grid-sample + recycle-safe default in `Create`, reset in `Clear`.
- `godot/src/Core/FogOfWarSystem.cs` — `Tick` stamps `EffectiveVisionRange(id)` (verified StampCircle float path unchanged).
- `godot/src/Core/SimChecksum.cs` — folded `Elevation[i].Raw`; `AlgoVersion` 14→15 + v15 doc.
- `godot/src/Core/Definitions/ScenarioData.cs` — `HeightAdvantageVision` (bool) + `HeightVisionBonusPerStep` (float), omit-when-default; not folded into `CanonicalModelHash`.
- `godot/src/Core/Sim/ScenarioApplier.cs` — threads toggle/bonus (single float→Fixed) + grid into the world before spawn; `Position.Y` stays `Fixed.Zero`.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — Godot-side `BuildAndInjectElevationGrid` (reads finalized heightmap → `Fixed[]` grid, NaN/fail→flat) after terrain restore, before apply; every no-build path clears the grid (F6).
- Tests: `SimChecksumCoverageGuardTest` (Elevation tooth + `KnownWorldState_ProducesPinnedV15Hash` = `0xB1E4E662`), `VersionStampConsistencyTests`/`HeroProfilePersistenceTests`/`CombatFeedbackProfileTests`/`SimResetTests` (AlgoVersion 14→15; SimReset also gained VG2 teeth), `ApplyUnitDefinitionGuardTest` (`RecycledSlot_CarriesNoPriorElevation`), NEW `ElevationGridTests`, `HeightAdvantageVisionTests`, `ScenarioApplierTests` (VG1), `ScenarioDataHeightVisionTests` (VG3).
- `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` — 23 per-tick SimChecksum goldens re-recorded to v15 (`hero-start-state.golden.txt` unchanged).

### Review findings breakdown (review pass 1)
- Patches applied (6): VG1 caller-path test, VG2 reset-equivalence teeth, VG3 serialization round-trip, F6 stale-grid clear + comment fix, F4 load-bearing `HEIGHT_STEP_WORLD_UNITS`, EC1 bonus-≥0 clamp.
- Deferred (1): F3 — `get_height` float interpolation feeding the checksummed grid (cross-platform determinism risk; not reachable today).
- Rejected (9): F1 headless-server desync (server is relay+quorum, does not tick — verified), F2 delete→undo (Elevation self-heals from position), F5/EC3 FromFloat overflow, EC2 steps·bonus overflow (grid-clamped), EC4 Width·Height overflow (no caller), EC5 fallback restore (already no-op), half-cell note (consistent), live-editor Terrain3D/GPU gaps (intent-sanctioned residual).

### Verification
- `dotnet build godot.sln` → Build succeeded, 0 errors, 0 warnings.
- `dotnet test ProjectChimera.Sim.Tests` → 1516 passed, 1 skipped, 2 failed. The 2 failures are the pre-existing `PersistenceManifestTests` (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`) — independently confirmed failing on the clean baseline via `git stash` (2 failed / 0 passed at 036eba2), unrelated to elevation.
- Invariants confirmed: `CanonicalModelHash.AlgoVersion`==5, `StartStateHash.AlgoVersion`==2, `hero-start-state.golden.txt` unchanged; exactly 23 per-tick goldens moved; patches are checksum-neutral (no additional golden moved).

### Residual risks
- **F3 (deferred):** cross-platform elevation determinism via `get_height` interpolation — latent until sculpted-map cross-platform MP; fix reads raw region height-map cells.
- **Live-verification gap (intent-sanctioned):** the Godot-side heightmap read, the `ScenarioLoadPhase` reorder, and the GPU fog / minimap / spectator `RevealAll` render have no Godot-free xUnit surface. Not exercised headlessly here — should be confirmed in-editor on a real sculpted 6.2 map (per the spec's manual godot-mcp checks and the 6.1/6.2 precedent): sculpted-hill units report non-zero `Elevation` / flat units Zero; toggle-ON larger fog circle vs OFF identical; `RevealAll` fills every cell; sculpted-vs-flat NavMesh face count identical (no elevation leak into the bake).
