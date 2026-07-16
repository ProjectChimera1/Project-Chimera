---
title: 'Remediation (14.7): map Export / New-Map write path runs a hard Validate() before it persists (DW-164, pre-Epic-7 HARD GATE)'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: 'c87cf35018d463e44119ea131250878c8d410e3f'
final_revision: '0a40ad9da92eabcb2d39593581bd36ae5daf778a'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The editor map Export (`ExportMapPackage`) and New-Map (`CreateNewMap`) write paths never run a hard `Validate()` — they call only the non-fatal `CollectAdvisories`, and only *after* persisting/packaging. A scenario that fails validation (content stranded past `MapBounds` by a map-size shrink, a slot overflow, etc.) is still written to `scenario.json` and shipped inside a `.chimera.zip` whose manifest hash validates but whose `scenario.json` hard-fails `CheckCoord` on reload — a silent, unloadable export. This is the pre-Epic-7 hard gate: `WinConditionPhase.cs` is exactly what Epic 7's win-condition work builds on, and a broken map that ships as an unloadable package is a content class Epic 7 cannot build on.

**Approach:** Add one shared, Godot-free hard gate (`MapWriteGate.Check`, wrapping the existing `ScenarioValidator.Validate`) and call it as the **first** action in both write paths — before any disk mutation (terrain save, scenario save, package pack). On a located validation error, abort with that error surfaced to the status label and leave nothing partial on disk. The happy path (a valid scenario) is byte-for-byte unchanged, including the existing post-write advisories; only a failing scenario is newly blocked. This is a HARD gate — it must not be weakened into an advisory.

## Boundaries & Constraints

**Always:**
- Both write paths call the hard gate **before** their first disk write. In `ExportMapPackage` the gate precedes `SaveTerrainBesideScenario` (which writes terrain region files first today) — so a rejected export leaves **no** terrain files, **no** `scenario.json`, and **no** `.chimera.zip`. In `CreateNewMap` the gate precedes `ScenarioSerializer.SaveToFile`.
- On gate failure the located validator error (field path + offending value, e.g. `scenario.player_slots[0].base_x=...`) is surfaced to the status label and the method returns without writing; nothing partial is left on disk.
- The gate is the same validator the load path consults (`ScenarioValidator.Validate`), so a rejected export is exactly a scenario the validator deems invalid — the class that hard-fails `CheckCoord` on reload. The export gate is intentionally **fail-closed** even though the master load gate runs in shadow mode (`ScenarioGate.ShouldProceed`); that shadow-vs-hard asymmetry is the point of DW-164.
- The valid-scenario happy path is unchanged: the gate returns "OK", the same bytes are written, and the existing non-fatal `CollectAdvisories` surface still fires after a successful write.
- The gate lives in the Godot-free sim assembly (`src/Core/Definitions/`) and is RED-teeth-proven by a Tier-1 xUnit test in `ProjectChimera.Sim.Tests` (a valid scenario passes; a stranded-past-bounds and a slot-overflow scenario each return a located error).

**Block If:**
- Investigation shows the hard `Validate()` would reject a scenario that today exports AND round-trips through a normal reload successfully (i.e. the gate would break a legitimate happy-path export, not just an already-broken map) — that would mean the export gate diverges from the load contract; HALT with status `blocked` and the reproduction as the blocking condition.

**Never:**
- Do not weaken the gate into an advisory or a warning-only surface, and do not remove/relegate the existing `CollectAdvisories` calls — the advisory layer stays; the hard gate is additive.
- Do not change `ScenarioValidator.Validate` semantics, its check order, or its located-error messages (it is already correct and covered by `NegativeValidationTests`); only consume it.
- Do not gate the **Import** write path (`DoImport`) — it is out of DW-164 scope; an imported package's `scenario.json` is validated at apply/load time by `ScenarioLoadPhase`, and its manifest hash already validated at pack time. (Note it as a candidate deferred-work if a reviewer disagrees.)
- Do not touch sim/checksum/golden code — the validator is pure and read-only; no golden re-baseline.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid map, Export | `_ctx.Scenario` passes `Validate` | Identical to today: terrain saved, `scenario.json` saved, minimap rendered, `.chimera.zip` packed, hash + advisories shown | No error expected |
| Valid map, New-Map | Blank scenario passes `Validate` | Identical to today: `{slug}.json` written, created-message + advisories shown | No error expected |
| Stranded-content Export | `_ctx.Scenario` has a placed unit/prop/resource-node/start-base with `abs(coord) > MapBounds` (map shrunk) | Aborts before `SaveTerrainBesideScenario`; status shows the located error naming the stranded field; no terrain files, no `scenario.json` overwrite, no `.chimera.zip` on disk | Located `CheckCoord` error surfaced |
| Slot-overflow New-Map / Export | A `player_slots[i].slot` out of `[0, PLAYER_COUNT)` or above the engine `Faction.Player4` ceiling | Aborts before any write; status shows the located slot error | Located slot error surfaced |
| Gate on a valid blank | `CreateNewMap` blank with no content | Gate returns OK; write proceeds unchanged | No error expected |

</intent-contract>

## Code Map

- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` -- `CreateNewMap` (:185, writes via `ScenarioSerializer.SaveToFile` at :200 then advisories) and `ExportMapPackage` (:218, `SaveTerrainBesideScenario` at :229 → `SaveToFile` :231 → `Pack` :263 then advisories). **The two write sites to gate.** Already `new ScenarioValidator()`s inline, so the validator type is in scope.
- `godot/src/Core/Definitions/MapWriteGate.cs` -- **NEW** Godot-free static gate. `Check(ScenarioData, IReadOnlyList<FactionDefinition?>?)` → `string?` (null = safe; else the located `ScenarioValidator.Validate` error). The single shared pre-write gate both paths call.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- `Validate(m, slotFactionDefs)` (:76) returns `ValidationResult` (`.Ok`/`.Error`), stopping at the first failed check with a located error; `CollectAdvisories` (:602) is the pre-existing non-fatal layer (leave as-is). Read-only reference.
- `godot/src/Core/Definitions/Validated.cs` -- `ValidationResult` (`.Ok` bool, `.Error` string?). Read-only reference.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:294-302` -- the load-path `ValidateBeforeApply` pattern to mirror (passes `_ctx.SlotFactionDefs`; logs the located error). Read-only reference.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs:53` -- `SlotFactionDefs` (the resolved per-slot faction defs; pass to the export gate so its verdict matches reload; may be null → `Validate` null-guards it).
- `godot/ProjectChimera.Sim.Tests/Validation/NegativeValidationTests.cs` -- existing validator teeth (`NodePositionOutsideMapBounds_IsRejected`, `SlotAboveEngineCeiling_IsRejected`, `ValidModel_Passes...`); the fixture/shape to reuse for the new gate tests. New tests go in a sibling file.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/MapWriteGate.cs` -- add a Godot-free `public static class MapWriteGate` with `public static string? Check(ScenarioData scenario, IReadOnlyList<FactionDefinition?>? slotFactionDefs = null)` returning `new ScenarioValidator().Validate(scenario, slotFactionDefs)` reduced to `r.Ok ? null : r.Error`. XML-doc it as the **single hard pre-write gate** for Export/New-Map: callers MUST invoke it before any disk write and abort on a non-null return; it is a HARD gate distinct from the non-fatal `CollectAdvisories`; pure (never throws/writes/logs). -- the one shared, testable gate decision both write paths consult, so "both paths are gated" is structurally guaranteed (the exact DW-164 defect was that both skipped validation).
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` -- in `ExportMapPackage`, immediately after the `_ctx.Scenario == null` guard and **before** `SaveTerrainBesideScenario`, call `MapWriteGate.Check(_ctx.Scenario, _ctx.SlotFactionDefs)`; if non-null set `statusLabel.Text = $"Export blocked — validation failed: {error}"`, `GD.PrintErr(...)`, and `return`. In `CreateNewMap`, before `ScenarioSerializer.SaveToFile`, call `MapWriteGate.Check(blank)` (blank has no pre-placed custom buildings → null faction defs); if non-null set `statusLabel.Text = $"New map blocked — validation failed: {error}"`, `GD.PrintErr(...)`, and `return`. Leave the existing post-write `CollectAdvisories` calls untouched. Add a brief comment at each gate documenting the "hard Validate before any write; nothing partial on disk; do not weaken to advisory" contract. -- closes DW-164 by hard-gating both writes ahead of every disk mutation.
- `godot/ProjectChimera.Sim.Tests/Validation/MapWriteGateTests.cs` -- **NEW** Tier-1 xUnit tests reusing the `NegativeValidationTests` fixture shape: (a) `ValidScenario_PassesGate_ReturnsNull`; (b) `StrandedContentPastMapBounds_IsBlocked_LocatingTheField` — a scenario with a coordinate-bearing entry (e.g. a resource node or start base) at `abs(coord) > MapBounds`; assert `Check(...)` is non-null and `Assert.Contains("map_bounds", err)` plus the field path; (c) `SlotOverflow_IsBlocked_LocatingTheSlot` — a `player_slots[i].slot` above the engine ceiling; assert non-null and `Assert.Contains("slot", err)`. -- pins the gate's decision + located-error shape at the Godot-free surface; RED-provable (see Verification).

**Acceptance Criteria:**
- Given a scenario that passes `ScenarioValidator.Validate`, when `MapWriteGate.Check` runs, then it returns null and both write paths proceed exactly as today (same bytes, same post-write advisories).
- Given a scenario with content stranded past `MapBounds`, when Export is invoked, then the gate returns a located `CheckCoord` error, the method aborts before `SaveTerrainBesideScenario`, and no terrain files / `scenario.json` overwrite / `.chimera.zip` are written.
- Given a scenario with a slot above the engine ceiling, when New-Map or Export is invoked, then the gate returns a located slot error and the method aborts before any write.
- Given the gate is a hard gate, when a scenario fails validation, then the failure is surfaced as a block (not a warning) and the pre-existing `CollectAdvisories` advisory layer is unchanged and still fires on the happy path.
- Given `MapWriteGate.Check` is stubbed to always return null (simulating the pre-fix ungated write path), when `MapWriteGateTests` runs, then the stranded-content and slot-overflow tests turn RED (teeth demonstrated); restoring the real gate returns them GREEN.

## Spec Change Log

_No bad_spec loopback. Review pass 1 applied 2 low-severity patches (see Review Triage Log) with no re-derivation._

## Review Triage Log

### 2026-07-15 — Review pass 1
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 3: (high 0, medium 2, low 1)
- reject: 8: (low 8)
- addressed_findings:
  - `[low]` `[patch]` MapWriteGate doc + spec Design Notes overclaimed "provably loadable" and implied a codebase-wide "single gate". Softened to the model-level validation subset (slope-derived blocked cells are recomputed at load, outside the Godot-free gate's view) and scoped the "never diverge" claim to the two Export/New-Map paths, naming Import/MapGenerator/Persistence as ungated (deferred). (Blind Hunter)
  - `[low]` `[patch]` The two-arg `slotFactionDefs` pass-through (the one behavior unique to the gate signature) was untested — all 3 tests used the single-arg overload. Added `CustomBuilding_WithNullFactionDefs_IsBlocked` + `CustomBuilding_WithResolvingFactionDefs_PassesGate`: the SAME custom-building scenario is rejected with null defs and accepted once the owner faction declares the id, proving `Check` forwards the defs. Tier-1 5/5 green. (Blind Hunter, Verification Gap)
- deferred_findings (logged to deferred-work.md):
  - `[medium]` The write-path wiring/ordering/"nothing partial on disk" for `ExportMapPackage`/`CreateNewMap` is verified by code-read only — no automated test drives the Godot-`Node`-bound phase; a regression deleting the abort or reordering the gate below `SaveTerrainBesideScenario` would pass all Tier-1 tests. (Intent-Alignment, Verification-Gap, Blind Hunter — most-corroborated)
  - `[medium]` The export gate trusts `_ctx.SlotFactionDefs` (declared `= null!`, may be null/stale relative to `_ctx.Scenario`); for a pre-placed custom-building map this can false-block a loadable map (or, if stale, false-pass an unloadable one). Robust fix: resolve faction defs fresh at export or guard on null. (Edge-Case Hunter)
  - `[low]` Import (`DoImport`) + `MapGeneratorPanel`/`PersistenceManifestPanel` write scenarios via `SaveToFile` without the gate — out of DW-164's Export/New-Map scope, pre-existing. (Blind Hunter, Intent-Alignment)
- rejected (noise / intended / cosmetic): shadow-loaded-map export-block is the intended DW-164 behavior; first-fail-only surfacing / running CollectAdvisories on the reject path is an enhancement beyond "abort with the located error"; null→null mapping is jointly pinned by the valid+failing tests; TerrainRef stamped after Check is benign (not validated); double validator instantiation and duplicated reject boilerplate (2 sites) are cosmetic; blank-template-fails-Validate blocking New-Map is desired behavior.

## Design Notes

**Why a shared `MapWriteGate` seam and not two inline calls.** DW-164's defect is precisely that *both* write paths independently skipped validation. A single Godot-free gate both paths call structurally guarantees they can't diverge again (fix Export, forget New-Map), gives one authoritative place to document the "before any write, nothing partial" contract, and — critically — makes the gate **decision** Tier-1-testable in isolation (`WinConditionPhase` itself is a Godot `Node`-bound `ISetupPhase` the Tier-1 assembly cannot instantiate). This mirrors the codebase's "one shared derivation" idiom (e.g. the single `PathabilityGrid.BuildBlockingFootprint` derivation the load/hash/validator all route through).

**The export gate is intentionally stricter than the master load gate.** On master `ScenarioGate.ShouldProceed` runs the load validator in *shadow* mode (it logs the located rejection but applies anyway); fail-closed engages only on release/MP. DW-164 mandates the export/new-map path be *hard* fail-closed regardless of the authoring session's shadow policy — an exported package must not ship model-invalid content, not merely shadow-limp — so the gate rejects any scenario the validator deems invalid (the same set that hard-fails `CheckCoord` on a fail-closed reload / MP handshake). This is the "must not be weakened into an advisory" mandate. Scope of the guarantee: the gate certifies the **model-level** validation subset (coordinates / slots / player-slot economy / painted-cell blocking). Slope-derived blocked cells depend on the terrain heightmap and are recomputed at load, so a start/spawn on a slope-blocked cell is outside this Godot-free gate's view — the gate reduces, but does not eliminate, the unloadable-on-reload class.

**Ordering is load-bearing.** `ExportMapPackage` writes terrain files (`SaveTerrainBesideScenario`) *before* it serializes the scenario. The gate must be the first statement after the null check so a rejected export leaves nothing partial — not even a stray terrain folder. This is why the gate cannot simply replace the post-write `CollectAdvisories` calls.

**Faction defs on the export gate.** Pass `_ctx.SlotFactionDefs` (as `ScenarioLoadPhase.ValidateBeforeApply` does) so a pre-placed custom building's authored id resolves identically to reload and the gate verdict matches what reload would produce. `Validate` null-guards a null list. `CreateNewMap`'s blank scenario has no pre-placed custom buildings, so null is correct there.

**Verification honesty (Godot-bound seam).** The gate *decision* is Tier-1-proven. The *wiring* (WinConditionPhase actually calls the gate first and aborts before any write) is Godot-`Node`-bound and confirmed by code-read + the gate being the first statement in each method; the `MapWriteGateTests` stub-to-null RED proof backstops the decision logic. Optional in-engine `godot-verify` (shrink a map to strand a start position, hit Export, observe the block + absence of a `.chimera.zip`) is the belt-and-suspenders in-engine confirmation but is not the durable net.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~MapWriteGateTests"` -- expected: valid-passes + stranded-blocked + slot-overflow-blocked all green.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green; golden/checksum tests unchanged (validator is pure, read-only; no golden re-baseline).

**RED-teeth proof (do, observe, revert):**
- Temporarily edit `MapWriteGate.Check` to `return null;` unconditionally (simulating the pre-fix ungated write path). Confirm `StrandedContentPastMapBounds_IsBlocked_LocatingTheField` and `SlotOverflow_IsBlocked_LocatingTheSlot` turn RED (they expect a located error the ungated path never produces). Restore the real gate body and confirm both return GREEN. Record the observed RED in the run result.

**Manual / in-engine (optional):**
- In the editor, shrink the map size so a placed start position lands outside the new bounds, then press Export. Expect the status label to show the located block and NO `{slug}.chimera.zip` to appear beside the scenario. (Recommended in-engine confirmation; Tier-1 is the required gate.)

## Auto Run Result

Status: done

**Implemented change (DW-164):** The editor's map Export and New-Map write paths now run a hard `Validate()` before any disk write and abort with the located error on failure, so a scenario that hard-fails on reload can no longer be persisted or shipped as an unloadable `.chimera.zip`. The gate is a single shared, Godot-free seam (`MapWriteGate.Check`) both paths route through; the happy path is byte-for-byte unchanged (gate returns null → identical writes + the pre-existing post-write `CollectAdvisories` still fire). This is the pre-Epic-7 hard gate. No sim/checksum/golden code touched → no golden re-baseline.

**Files changed:**
- `godot/src/Core/Definitions/MapWriteGate.cs` (NEW) — Godot-free `MapWriteGate.Check(ScenarioData, IReadOnlyList<FactionDefinition?>? = null) → string?` (null = safe; else the located `ScenarioValidator.Validate` error). XML-documented as the single HARD pre-write gate (distinct from non-fatal `CollectAdvisories`; certifies the model-level validation subset only — slope-derived blocked cells are recomputed at load).
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — `ExportMapPackage`: gate as the first statement after the null guard, before `SaveTerrainBesideScenario` (passes `_ctx.SlotFactionDefs`); on failure "Export blocked — validation failed: {error}", `GD.PrintErr`, return. `CreateNewMap`: gate before `SaveToFile` (null defs); "New map blocked — …". Existing post-write `CollectAdvisories` untouched.
- `godot/ProjectChimera.Sim.Tests/Validation/MapWriteGateTests.cs` (NEW) — 5 Tier-1 xUnit tests: valid → null; stranded-past-bounds → located `map_bounds`/`resource_nodes[0].x`; slot-overflow → located `player_slots[1].slot`; plus the review-added `slotFactionDefs` pass-through pair (custom building rejected with null defs, accepted with resolving defs).

**Review findings breakdown (pass 1):** 0 intent_gap, 0 bad_spec. 2 low patches applied (softened the "provably loadable"/"single gate" doc overclaims; added the `slotFactionDefs` pass-through tests — the one behavior unique to the gate signature). 3 deferred (2 medium: write-path wiring/ordering verified by code-read only — the most-corroborated finding across 3 reviewers; and the export gate trusting a possibly-null/stale `_ctx.SlotFactionDefs` for custom-building maps. 1 low: Import/MapGenerator/Persistence writes remain ungated — out of DW-164 scope). 8 rejected (intended shadow-loaded export-block behavior, CollectAdvisories-on-reject enhancement, cosmetic duplication, benign/speculative concerns).

**Verification:**
- `dotnet build godot/godot.sln` — 0 errors, 11 warnings (all pre-existing, unrelated files).
- `dotnet test … --filter MapWriteGateTests` — 5/5 passed.
- `dotnet test` (full Tier-1) — 1786 passed, 1 skipped (pre-existing), 0 failed. No golden/checksum regression.
- RED-teeth proof: the implementation subagent stubbed `MapWriteGate.Check` to `return null;` and the stranded-content + slot-overflow tests turned RED; restoring the real gate returned GREEN.

**Residual risks / artifacts:** The write-path wiring ("nothing partial on disk"), the `_ctx.SlotFactionDefs` null/stale edge for custom-building maps, and the ungated Import/MapGenerator/Persistence writes are covered by code-read + documentation, not by an automated test at the write-path surface — all three logged to the deferred-work ledger. `WinConditionPhase` is Godot-`Node`-bound and not instantiable in the Godot-free Tier-1 assembly, so the durable net is the gate-decision Tier-1 tests plus the first-statement positioning of the gate in each write method.
