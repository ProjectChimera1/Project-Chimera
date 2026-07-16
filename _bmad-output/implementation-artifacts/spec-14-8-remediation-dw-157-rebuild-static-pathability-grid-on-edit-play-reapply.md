---
title: 'Remediation (14.8): rebuild the static PathabilityGrid on Edit→Play re-apply (DW-157)'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: '8d70a7e3214402d7c832f15563598ea7107161e5'
final_revision: 'dbdcd66c9e6f097456c5e7815917c82dfd20b4b6'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The static `PathabilityGrid` (painted-blocked ∪ slope-derived ∪ blocking-prop/water footprint cells) is built exactly once at boot in `ScenarioLoadPhase.BuildAndInjectPathabilityGrid` and fanned out to three sim sinks. The Edit→Play (F5) re-apply path, `MainScene.ResetToAuthoredStart`, calls `ScenarioApplier.Apply` — which re-threads the applier's **cached** boot grid into `EntityWorld` and never refreshes the `FlowFieldSystem` static obstacle mask. So a blocking prop / water volume / painted cell added, moved, or removed in Edit mode is walked straight through in Play until a full reload, even though `CanonicalModelHash` already folds the obstacle that session. High authoring-loop friction for the trigger/obstacle iterate cycle, and it should precede Epic 7.

**Approach:** Introduce ONE shared Godot-free derivation, `ScenarioApplier.BuildPathabilityGrid(ScenarioData?, ElevationGrid?)`, that reproduces the exact boot recipe (painted ∪ slope-at-threshold ∪ prop/water footprint via `PathabilityGrid.Resolve`). Have the boot path call it (proving one derivation, byte-identical boot). In `ResetToAuthoredStart`, before the re-apply, rebuild the grid from the **current** `_ctx.Scenario` and fan it out to the same three sinks — the applier (→ `EntityWorld` on `Apply`), the `FlowFieldSystem` static mask, and `SceneContext.Pathability` (editor overlay) — then force `RebuildObstacles` so the refreshed static mask actually takes effect this Play.

## Boundaries & Constraints

**Always:**
- The re-applied grid MUST be derived from the current `ScenarioData` via the SAME shared derivation the boot path uses — the two paths can never produce a different blocked-cell set (that set is what `CanonicalModelHash` certified).
- The applier's grid must be set (`SetPathabilityGrid`) **before** `_applier.Apply(validated)` runs, because `Apply` threads it into `EntityWorld` before any spawn.
- After re-apply, `FlowFieldSystem.RebuildObstacles` must run so the refreshed static mask is OR'd into the obstacle map (buildings may be unchanged across the reset, so `FlowFieldBridge`'s per-frame building diff would not otherwise trigger a rebuild).
- Deterministic: all peers re-apply identically; the change re-injects, never invents, blocked cells. No new field enters any hash.

**Block If:**
- Rebuilding the grid on re-apply causes the `SimResetTests` determinism keystone (clear+re-apply of the SAME model == fresh boot, byte-for-byte) to diverge, or any golden checksum shifts for an unchanged model — that signals an unintended value change, not a re-baseline. HALT.

**Never:**
- Do not change `ScenarioApplier.Apply`'s existing behavior or the boot grid VALUES (boot must stay byte-identical); this is a re-inject-on-reset fix plus a no-op refactor of the boot build.
- Do not touch terrain re-bake / `ElevationGrid` rebuild on Edit→Play (out of scope — DW-157 is painted/prop/water; reuse the applier's already-injected elevation grid for slope re-derivation).
- Do not re-baseline goldens unless an observed value actually shifts for an unchanged model (it must not).
- Do not introduce a `Navigation → Core.Definitions` dependency (keep `PathabilityGrid` taking primitive arrays; the ScenarioData recipe lives in the applier).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Obstacle added in Edit | Boot map has cell C passable; Edit adds a blocking prop at C; F5 | After re-apply, `EntityWorld.Pathability.IsBlocked(C)` is true; the sim keeps units out of C | n/a |
| Obstacle removed in Edit | Boot map blocks cell C; Edit deletes the prop/water; F5 | After re-apply, C is passable (grid rebuilt from source un-stamps it) | n/a |
| Unchanged model re-apply | Same `ScenarioData` at boot and F5 | Rebuilt grid == boot grid; SimChecksum + StartStateHash byte-identical (no golden shift) | n/a |
| Flat / legacy map | Scenario with nothing blocked | Rebuild returns null; all sinks cleared to null; byte-identical no-op | Build never throws (Resolve degrades to null) |
| Fallback (no scenario) | `ResetToAuthoredStart` with `_ctx.Scenario == null` | Pathability path skipped (fallback maps are flat, as at boot) | n/a |

</intent-contract>

## Code Map

- `godot/src/Core/Sim/ScenarioApplier.cs` -- Godot-free sole writer. Holds `_elevationGrid` (`SetElevationGrid`) and `_pathability` (`SetPathabilityGrid`); `Apply` threads `_pathability` → `EntityWorld` at :127. **Add** `public ElevationGrid? ElevationGrid => _elevationGrid;` and **add** the shared static `BuildPathabilityGrid(ScenarioData?, ElevationGrid?)`.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:236-287` -- `BuildAndInjectPathabilityGrid` (the boot build + 3-sink fan-out) and its inline `threshold`/`footprint`/`Resolve` recipe (:246-256). **Refactor** the build half to call `ScenarioApplier.BuildPathabilityGrid(s, _lastElevationGrid)` (byte-identical; the applier's elevation grid == `_lastElevationGrid` at boot).
- `godot/src/Core/MainScene.cs:1565` -- `ResetToAuthoredStart`. **Add** the rebuild + 3-sink inject before `_applier.Apply(validated)` (:1636), and `RebuildObstacles(_buildings)` after. Has `_applier`, `_ctx.FlowFieldSys`, `_ctx.Pathability`, `_ctx.Scenario`, `_buildings`.
- `godot/src/Navigation/PathabilityGrid.cs` -- `Resolve(painted, slopeOn, threshold, elev, extra)` (:193), `BuildBlockingFootprint(props, water)`, `IsBlocked(Fixed x, Fixed z)` (:70). Read-only.
- `godot/src/Navigation/FlowFieldSystem.cs` -- `SetStaticBlocked(bool[]?)` (:84), `RebuildObstacles(BuildingStore)` (:59) which OR's `_staticBlocked` into `_obstacles`. Read-only.
- `godot/src/Core/EntityWorld.cs:314` -- `Pathability { get; private set; }`; `SetPathabilityGrid` (:873). The assertion surface.
- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` -- the clear+re-apply Tier-1 fixture (`BuildApplied`/`ApplyValidated`) to reuse. New tests go in a sibling file.
- `godot/ProjectChimera.Sim.Tests/Navigation/PathabilityUnionPropsWaterTests.cs` -- prop/water footprint fixture idiom (`PathabilityGrid.StampPropInto`) to reuse for building an edited model.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Sim/ScenarioApplier.cs` -- add `public ElevationGrid? ElevationGrid => _elevationGrid;` and `public static ProjectChimera.Navigation.PathabilityGrid? BuildPathabilityGrid(ScenarioData? s, ElevationGrid? elev)` computing `slopeOn = s != null && s.SlopeAutoBlock && s.SlopeBlockThreshold > 0f`, `threshold = slopeOn ? Fixed.FromFloat(s!.SlopeBlockThreshold) : Fixed.Zero`, `footprint = PathabilityGrid.BuildBlockingFootprint(s?.Props, s?.Water)`, returning `PathabilityGrid.Resolve(s?.PathabilityBlocked, s?.SlopeAutoBlock ?? false, threshold, elev, footprint)`. XML-doc it as the ONE shared derivation both boot and Edit→Play re-apply route through; the single float→Fixed slope-threshold boundary. -- centralizes the recipe so the two paths can't diverge.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- in `BuildAndInjectPathabilityGrid`, replace the inline `slopeOn`/`threshold`/`footprint`/`Resolve` block with `var grid = ProjectChimera.Core.Sim.ScenarioApplier.BuildPathabilityGrid(s, _lastElevationGrid);` keeping the existing 3-sink injection + logging + try/catch verbatim. -- proves one derivation; boot stays byte-identical.
- `godot/src/Core/MainScene.cs` -- in `ResetToAuthoredStart`, immediately before `if (hasScenario) _applier.Apply(validated);`, add (guarded by `hasScenario`): `var grid = ScenarioApplier.BuildPathabilityGrid(_ctx.Scenario, _applier.ElevationGrid); _applier.SetPathabilityGrid(grid); _ctx.FlowFieldSys?.SetStaticBlocked(grid?.Blocked); _ctx.Pathability = grid;`. Immediately after the Apply/ApplyFallback branch, add `if (hasScenario) _ctx.FlowFieldSys?.RebuildObstacles(_buildings);`. Comment the DW-157 rationale (boot built once; re-apply reused stale; same 3-sink fan-out as boot; RebuildObstacles because buildings may be unchanged). -- honors Edit-added obstacles this Play.
- `godot/ProjectChimera.Sim.Tests/Navigation/PathabilityReapplyRebuildTests.cs` -- **NEW** Tier-1 tests reusing the `SimResetTests` clear+re-apply composition and the prop-footprint idiom: (a) `AddedBlockingProp_HonoredAfterReapply` — apply model A (cell C passable), assert `host.World.Pathability` null-or-not-blocked at C; add a blocking prop at C → model A'; `host.ClearForReset()`; `applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(A', applier.ElevationGrid))`; re-apply A'; assert `host.World.Pathability!.IsBlocked(Cx, Cz)` is true. (b) `RemovedProp_UnblockedAfterReapply` — inverse: A blocks C, A' removes it, re-apply → not blocked. (c) `UnchangedModel_ReapplyGridIdentical` — `BuildPathabilityGrid` on the same model twice yields equal `Blocked` masks (or both null). -- pins the Godot-free composition `ResetToAuthoredStart` performs; RED-provable (see Verification).

**Acceptance Criteria:**
- Given a boot map where cell C is passable, when a blocking prop is added at C in Edit and F5 re-applies, then `EntityWorld.Pathability.IsBlocked(C)` is true and the sim keeps units out of C — without a full reload.
- Given a boot map that blocks cell C, when the obstacle is deleted in Edit and F5 re-applies, then C is passable (the grid is rebuilt from source, un-stamping it).
- Given the same `ScenarioData` at boot and at F5, when the grid is rebuilt on re-apply, then it equals the boot grid and the `SimResetTests` byte-identical clear+re-apply keystone and all golden checksums are unchanged (no re-baseline).
- Given the boot build now routes through `ScenarioApplier.BuildPathabilityGrid`, when the full Tier-1 suite runs, then all pathability/golden/reset tests remain green (boot byte-identical).
- Given `PathabilityReapplyRebuildTests` and the re-apply rebuild is stubbed out (simulating the pre-fix cached-grid reuse), when the tests run, then `AddedBlockingProp_HonoredAfterReapply` turns RED; restoring the rebuild returns it GREEN.

## Spec Change Log

_No bad_spec loopback. Review pass 1 applied 3 patches (see Review Triage Log) with no re-derivation._

## Review Triage Log

### 2026-07-15 — Review pass 1
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 1: (high 0, medium 1, low 0)
- reject: 5: (low 5)
- addressed_findings:
  - `[medium]` `[patch]` The flow-field ROUTING half of the fix (rebuilt grid → `SetStaticBlocked` → `RebuildObstacles`) had zero coverage — the most-corroborated gap (Intent-Alignment, Verification-Gap, Blind Hunter). Added `RebuiltGridMask_ExcludesCell_FromFlowField`: a grid built by the shared recipe, fed through `SetStaticBlocked`+`RebuildObstacles`, excludes the Edit-added cell from the computed field (mirrors `FlowFieldBlockingTests`). Pins the routing mechanism the Godot-bound `ResetToAuthoredStart` lines rely on.
  - `[low]` `[patch]` The slope arm of `BuildPathabilityGrid` (the `Fixed.FromFloat` threshold boundary + `elev` threading — the very reason the `ElevationGrid` getter was added) was never exercised: all four original tests passed `null` elevation with slope off. Added `SlopeArm_DerivesCells_ThroughBuildPathabilityGrid` (cliff grid → straddling column blocked; null elev / slope-off → none). (Verification-Gap)
  - `[low]` `[patch]` The reset fallback branch (`hasScenario == false`) did not clear the pathability sinks the boot fallback path deliberately clears ("a REUSED applier must not carry a prior sculpted load's blocking"), and `ResetToAuthoredStart` IS the reused-applier case. Added a symmetric `else` clear (`SetPathabilityGrid(null)`/`SetStaticBlocked(null)`/`Pathability=null`) and made `RebuildObstacles` unconditional so the cleared mask drops. Currently unreachable (`_ctx.Scenario` never goes non-null→null in a session) but closes the asymmetry. (Blind Hunter, Edge-Case)
- deferred_findings (logged to deferred-work.md):
  - `[medium]` With `slope_auto_block` on, a TERRAIN edit (sculpting a cliff) is not honored on Edit→Play — the slope layer re-derives from the stale boot `ElevationGrid` while painted/prop/water layers are fresh, so the grid is mixed-freshness. Same authoring-loop class as DW-157 but explicitly outside its intent scope (painted + prop/water); requires an `ElevationGrid` re-bake on Edit→Play. NOT a cross-peer determinism bug: `ResetToAuthoredStart` is the offline-editor loop only; MP peers all fresh-boot with a re-baked grid. (Edge-Case, Blind Hunter, most-corroborated)
- rejected (noise / intended / inherent-and-disclosed): the reset-path rebuild lacking a try/catch (Edge-Case) — `BuildPathabilityGrid` and the sink setters are provably throw-proof today, so a handler for an impossible exception is speculative; the `slope-auto-block={config}` log reporting the config gate not whether cells derived (Blind Hunter) — pre-existing and byte-identical to the prior inline expression; set-before-Apply ordering and the boot "byte-identical" delegation being code-read-only (Verification-Gap, Blind Hunter) — inherent Godot-`Node`-bound gaps already disclosed in the spec's Verification-honesty note; the stale flow-field obstacle map between the sink-clear and `RebuildObstacles` during `Apply` (Blind Hunter) — benign (the sim does not tick during `Apply`; `LoadScenario` evaluates no field) and not newly introduced.

## Design Notes

**Why one shared derivation, not a second inline copy.** The DW-157 defect class is precisely two lifecycle paths (boot vs. re-apply) disagreeing about the blocked-cell set. A single `ScenarioApplier.BuildPathabilityGrid` both paths call structurally prevents drift and mirrors the codebase's prized "one shared derivation" idiom (the same reason `BuildBlockingFootprint` is the sole footprint recipe for load/hash/validator). The recipe lives in the applier (Core.Sim), which already references `ScenarioData`, `ElevationGrid`, `Fixed`, and `PathabilityGrid` — so `PathabilityGrid` stays Godot- and Definitions-free (keeps taking primitive arrays).

**Three sinks, set before Apply.** Boot fans the grid to: the applier (`SetPathabilityGrid` → threaded into `EntityWorld` inside `Apply`), the `FlowFieldSystem` static mask (`SetStaticBlocked`, OR'd in on `RebuildObstacles`), and `SceneContext.Pathability` (the editor overlay). The re-apply must do all three, and set the applier grid **before** `_applier.Apply` (Apply reads `_pathability` before any spawn). `EntityWorld.Pathability` is the primary, Tier-1-testable block (MovementSystem); the flow-field mask is the routing half. `RebuildObstacles` is forced post-apply because `FlowFieldBridge.CheckBuildingChanges` only rebuilds when the building set changed — an obstacle-only edit leaves buildings identical across the reset.

**Determinism & goldens.** For a given `ScenarioData` the rebuilt grid is bit-identical to what a fresh boot produces, and `CanonicalModelHash` already folds these blocked cells — so nothing hashed changes for an unchanged model and no golden re-baseline is warranted. The observable behavior change only manifests when the model actually changed between boot and re-apply (an Edit), which the golden/reset suites do not exercise. If any golden shifts, treat it as a regression and HALT (per Block If), not a re-baseline.

**Verification honesty (Godot-bound seam).** The Godot-free composition (`BuildPathabilityGrid` + `SetPathabilityGrid` + re-apply → `EntityWorld` blocks the new cell) is Tier-1-proven by `PathabilityReapplyRebuildTests`. The `MainScene`-side wiring (3-sink fan-out order + `RebuildObstacles`) is Godot-`Node`-bound, confirmed by code-read plus the applier-grid-before-Apply ordering; optional in-engine `godot-verify` is the belt-and-suspenders.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~PathabilityReapplyRebuildTests"` -- expected: added-honored + removed-unblocked + unchanged-identical all green.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green; `SimResetTests`, all `Pathability*`/`FlowField*`/golden/`CanonicalModelHash*` tests unchanged (no re-baseline; boot byte-identical).

**RED-teeth proof (do, observe, revert):**
- In `PathabilityReapplyRebuildTests.AddedBlockingProp_HonoredAfterReapply`, temporarily skip the `applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(...))` line (re-apply with the stale boot grid, as before the fix). Confirm the `IsBlocked(C)` assertion turns RED. Restore the rebuild and confirm GREEN. Record the observed RED in the run result.

**Manual / in-engine (optional):**
- In the editor, place a blocking prop on an open lane, F5 to Play, order a unit across the prop's cell. Expect it to route around / be blocked (not walk through) without a full map reload. Delete the prop, F5, confirm the lane is passable again.

## Auto Run Result

Status: done

**Summary.** DW-157 (Story 14.8): the static `PathabilityGrid` (painted ∪ slope ∪ blocking-prop/water footprint) is now rebuilt from the current `ScenarioData` on Edit→Play (F5) re-apply, so obstacles added/moved/removed in the editor are honored the next Play without a full reload. Boot and re-apply route through one shared Godot-free derivation.

**Files changed.**
- `godot/src/Core/Sim/ScenarioApplier.cs` — new `public ElevationGrid? ElevationGrid` getter + the shared static `BuildPathabilityGrid(ScenarioData?, ElevationGrid?)` (the ONE recipe; single float→Fixed slope-threshold boundary).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — `BuildAndInjectPathabilityGrid` now delegates its build to the shared method (byte-identical boot); dead `BuildBlockingFootprintMask` removed.
- `godot/src/Core/MainScene.cs` — `ResetToAuthoredStart` rebuilds the grid from `_ctx.Scenario` and fans it to all 3 sinks before `Apply` (applier→EntityWorld, FlowFieldSystem static mask, editor overlay), forces `RebuildObstacles` after; review patch added the symmetric fallback-branch sink clear + made `RebuildObstacles` unconditional.
- `godot/ProjectChimera.Sim.Tests/Navigation/PathabilityReapplyRebuildTests.cs` — NEW; 6 Tier-1 tests (added-prop honored, removed-prop unblocked, unchanged-grid-identical, no-scenario null, slope-arm derivation, flow-field routing exclusion).

**Review (pass 1).** 3 patches applied (1 medium test-coverage: flow-field routing; 2 low: slope-arm test, fallback-sink-clear symmetry), 1 deferred (terrain/slope re-bake on Edit→Play — outside DW-157's painted/prop/water scope), 5 rejected (speculative try/catch, cosmetic log, inherent-and-disclosed Godot-bound ordering/boot gaps, benign stale-window). No intent_gap, no bad_spec, no loopback.

**Verification.**
- `dotnet build godot/godot.sln` — 0 errors; no warnings in touched files.
- Full Tier-1 suite: **1792 passed, 1 skipped (pre-existing), 0 failed.** No golden re-baseline (`CanonicalModelHash`/`SimReset`/`Pathability`/`FlowField`/`SlopeAutoBlock` all green).
- RED-teeth proof observed: skipping the re-apply `SetPathabilityGrid` line fails `AddedBlockingProp_HonoredAfterReapply` (stale cached grid); restored → GREEN.

**Residual risks.**
- The `MainScene.ResetToAuthoredStart` wiring itself (3-sink fan-out order, set-before-Apply, `RebuildObstacles` after) is Godot-`Node`-bound and confirmed by code-read; the review added Tier-1 coverage of the underlying mechanisms (recipe, slope arm, routing) but the method call is not driven by a test. Optional in-engine `godot-verify` (place a blocking prop, F5, drive a unit across it) remains the belt-and-suspenders check and was not run in this unattended pass.
- Deferred: terrain-edit → slope re-derivation on Edit→Play (logged to deferred-work.md).
