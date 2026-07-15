---
title: 'Remediation DW-97: Wire FactionValidator.ValidateComplete into the launch gate'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: 'f6a78bd379fd48f2cf4b9b96dd37fbc020ce02fb'
final_revision: '72f2943ad01d550f3c84433fe715899a40af992c'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** `FactionValidator.ValidateComplete` (the roster-completeness / mesh_path / descriptor gate) is wired into no *blocking* launch decision. The client match-load path runs it only as a non-blocking shadow diagnostic (`ScenarioLoadPhase.ResolveSlotFactionDefs` → `GD.PrintErr`, Story 5.7), and the interactive Edit→Play launch boundary (`MainScene.ResetToAuthoredStart`) validates only scenario *structure* (`ScenarioValidator`), never faction completeness. So a structurally-incomplete faction — missing a Worker (→ zero-economy match, DW-97's named symptom), missing `mesh_path`, or a dangling `signature_mechanic_effect_id` — can be launched into a playtest with no block.

**Approach:** Add a fail-closed roster-completeness gate at the client playtest/skirmish launch boundary (`ResetToAuthoredStart`), mirroring its existing scenario veto: run `ValidateComplete` (with the ability registry threaded, so the signature check fires) over every resolved slot faction; if any is incomplete, veto Edit→Play with an actionable located HUD message and stay in Edit. Extract the pure decision into a Godot-free helper (following the `ScenarioGate` precedent) so it is unit-testable in the Tier-1 assembly.

## Boundaries & Constraints

**Always:**
- Reuse `FactionValidator.ValidateComplete` as the single completeness truth-source (epic-14 two-method contract). Pass `_abilityRegistry` so the `signature_mechanic_effect_id` check fires at the gate — this closes the "guarantee a registry everywhere" that Story 14.3 explicitly deferred to 14.4.
- The block is fail-closed and **unconditional** at the Play/F5 boundary, mirroring the existing `ScenarioValidator` veto in `ResetToAuthoredStart`: validate BEFORE any `ClearForReset` (world unchanged on veto) and `return false` so the caller stays in Edit.
- Surface a located, actionable message naming the offending faction `id` and the located error, via `ShowTriggerMessage` (HUD toast) + `GD.PrintErr` — the same idiom as the scenario veto directly above the insertion point.
- The pure gate decision lives in a Godot-free type under `src/Core/Definitions/` (inside the Tier-1 test globs), following `ScenarioGate`'s split-decision-from-side-effects precedent. Skip null slot entries; a null/empty registry deliberately skips only the signature check (existing `ValidateComplete` semantics). Never throw, never log from the pure layer.

**Block If:**
- The shipped `alpha`/`beta` factions do NOT pass `ValidateComplete` with the real loaded `AbilityRegistry` (verified true at planning: `spike_transmutation`/`furnace_trickle` both resolve; neither authors `hero_unit_id`). If a test proves otherwise, that is an out-of-scope data defect — HALT rather than weakening the gate to accommodate it.

**Never:**
- Do NOT wire `ValidateComplete` into `FactionValidator.Validate` or any `LoadFromFile` / editor-Save self-check path (re-opens the Review-Loop-2 editor regression the two-method split exists to prevent).
- Do NOT change the boot-time `ScenarioLoadPhase` shadow diagnostic to blocking, and do NOT touch the dedicated-server / headless match-load path (`ServerBootstrap.Build`, `MainScene.BuildHeadlessServerSimHost`) — multiplayer-determinism-critical, out of scope (DW-97).
- Do NOT invent an env toggle for this gate or alter `ScenarioGate`; the Play-boundary block mirrors the existing unconditional scenario veto.
- Do NOT alter any `ValidateComplete` rule or any sim value — no golden checksum re-baseline.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| All slot factions complete | slots = alpha/beta (or any `ValidateComplete`-passing defs), registry loaded | `FirstIncompleteReason` → `null`; `ResetToAuthoredStart` proceeds and enters Play | none |
| Slot faction missing Worker | one slot def has no Worker-category unit | reason `"faction '<id>' is incomplete:\n…missing a required Worker unit."`; Play vetoed, stays in Edit, world unchanged | HUD toast + `GD.PrintErr` |
| Slot faction dangling signature id | slot def `signature_mechanic_effect_id` not in registry | reason naming `signature_mechanic_effect_id`; Play vetoed | HUD toast + `GD.PrintErr` |
| Null slot entries among valid defs | array with nulls + valid defs | nulls skipped; `null` returned when all non-null defs pass | none |
| Null / empty registry | valid defs, registry `null` | signature check skipped; hero/roster/mesh checks still enforced | none |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionLaunchGate.cs` -- **NEW** pure Godot-free helper `FirstIncompleteReason(IReadOnlyList<FactionDefinition?> slotFactionDefs, AbilityRegistry? abilityRegistry)`: runs `ValidateComplete` per non-null slot, returns the first located block reason (`"faction '<id>' is incomplete:\n<message>"`) or `null`. Mirrors `ScenarioGate.cs` (same dir, same split-decision idiom). Tier-1 testable.
- `godot/src/Core/MainScene.cs` -- `ResetToAuthoredStart` (~lines 1600-1614): after the existing `ScenarioValidator` veto and BEFORE `_host.ClearForReset()`, call the helper with the in-scope fields `_slotFactionDefs` (`FactionDefinition?[]`, line 47) and `_abilityRegistry` (`AbilityRegistry`, line 50); on a non-null reason → `GD.PrintErr` + `ShowTriggerMessage(...,5f)` (line 1536) + `return false`.
- `godot/src/Core/Definitions/FactionValidator.cs` -- `ValidateComplete(def, abilityRegistry)` reused unchanged (registry-threaded signature + hero/roster/mesh checks). Read-only reference.
- `godot/src/Core/Definitions/ScenarioGate.cs` -- read-only precedent for the pure-decision split.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionLaunchGateTests.cs` -- **NEW** xunit tests locking each gate branch at the Godot-free level.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/FactionLaunchGate.cs` -- Create the pure helper (sketch in Design Notes). XML doc records: fail-closed Play-boundary decision, registry-threaded signature check, null-slot skip, why it is split out of `MainScene` (Tier-1 testability, `ScenarioGate` precedent), and that the located reason is presentation-surfaced by the caller. -- Provides the testable launch-gate decision.
- `godot/src/Core/MainScene.cs` -- In `ResetToAuthoredStart`, after the scenario-validation veto block (line ~1614) and before `_host.ClearForReset()` (line ~1617), add the faction-completeness veto: `FactionLaunchGate.FirstIncompleteReason(_slotFactionDefs, _abilityRegistry)`; on a non-null reason, `GD.PrintErr` the flattened reason and `ShowTriggerMessage($"Cannot enter Play — {reason}", 5f)`, then `return false`. -- Wires the fail-closed roster-completeness block into the real client playtest launch boundary (DW-97).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionLaunchGateTests.cs` -- Add tests covering every I/O-Matrix row: all-complete → `null`; missing-Worker → located reason naming the faction + Worker error; dangling signature (registry lacking the id) → located `signature_mechanic_effect_id` reason; null-slot entries skipped; null registry skips signature but still enforces the roster; plus a row proving the shipped alpha/beta descriptor ids pass with an in-memory registry containing `spike_transmutation`/`furnace_trickle`. -- Locks each gate branch at the correct (Godot-free) level.

**Acceptance Criteria:**
- Given a resolved slot faction that fails `ValidateComplete` (e.g. no Worker unit), when the user triggers Edit→Play (Play button or F5) and `ResetToAuthoredStart` runs, then entry to Play is vetoed (`return false`, stays in Edit), the world is left unchanged (no `ClearForReset`), and an actionable HUD message names the offending faction `id` and the located error.
- Given all resolved slot factions pass `ValidateComplete` with the loaded `AbilityRegistry`, when Edit→Play runs, then the faction gate does not block and the existing reset/apply proceeds unchanged.
- Given the shipped `alpha`/`beta` factions and the real abilities registry, when the gate runs, then it passes — the change does not block the default playtest (their `signature_mechanic_effect_id`s resolve; neither authors a hero).
- Given the pure `FactionLaunchGate` helper over a slot array containing null entries plus valid defs, when it runs, then null entries are skipped and the result reflects only the non-null defs.
- Given `dotnet build godot/godot.sln` and the full `ProjectChimera.Sim.Tests` suite, when run, then the build succeeds with no new warnings, all tests pass, and no golden checksum re-baseline occurs.

## Spec Change Log

## Review Triage Log

### 2026-07-15 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 2, low 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 9
- addressed_findings:
  - `[low]` `[patch]` Doc-accuracy: "closes 14.3's registry deferral" overstated (registry threaded at ONE of three ValidateComplete sites). Qualified the XML doc + MainScene comment to "closes the deferral at the launch gate only; boot-shadow + discovery stay registry-less by design."
  - `[medium]` `[patch]` Test coverage: the gate blocks on four axes but only Worker + signature were exercised through it. Added two pure-helper tests covering the `mesh_path` and `hero_unit_id` axes (SlotFactionWithBlankMeshPathUnit_/SlotFactionWithDanglingHeroId_ → located reason). Full suite now 1779 pass.
  - `[medium]` `[patch]` Verification strengthening: the disclosed manual check asserted toast + stay-in-Edit but not the "world-unchanged" fail-closed AC (a tester seeing the toast wouldn't notice a wiped board), and did not prove the real loaded registry reaches the gate. Strengthened the spec `## Verification` manual checks to (a) place an entity → vetoed F5 → confirm the placement survives, and (b) exercise a dangling `signature_mechanic_effect_id` faction. No code change — the gate already runs before `ClearForReset` (verified).

## Design Notes

**Why `ResetToAuthoredStart`, not the boot path or `ScenarioGate`:** the epic's "playtest/skirmish launch boundary" is the interactive Edit→Play transition — no skirmish/lobby UI exists yet (Story 11.1 unbuilt; see `MainScene.cs:300`). `ResetToAuthoredStart` is already the *unconditional* fail-closed veto for that transition (validate → toast → `return false` → stay in Edit); adding the faction check there mirrors the existing scenario veto exactly. The boot-time `ScenarioLoadPhase` `ValidateComplete` stays a non-blocking shadow diagnostic (Story 5.7 / DW-97 client half), and the dedicated-server path stays out of scope.

**Why a pure helper:** `ResetToAuthoredStart` is a Godot `Node` method outside the Tier-1 Godot-free test globs, so its logic cannot be unit-tested directly — the same constraint that made Story 1.7 extract `ScenarioGate`. `FactionLaunchGate` carries the decision (which slots, registry threading, first located reason) into the testable layer; `MainScene` only performs the Godot side effects (PrintErr, toast, veto).

**Registry threading closes 14.3's deferral:** 14.3 shipped `ValidateComplete(def, registry = null)` and explicitly left "guarantee a registry everywhere" to 14.4. Passing `_abilityRegistry` here makes the `signature_mechanic_effect_id` check effective at the launch gate; shipped alpha (`spike_transmutation`) and beta (`furnace_trickle`) both resolve against `resources/data/abilities`, so the block does not fire on the defaults.

Sketch — helper:
```csharp
public static string? FirstIncompleteReason(
    IReadOnlyList<FactionDefinition?> slotFactionDefs, AbilityRegistry? abilityRegistry)
{
    if (slotFactionDefs == null) return null;
    foreach (FactionDefinition? def in slotFactionDefs)
    {
        if (def is null) continue;
        FactionValidationResult r = FactionValidator.ValidateComplete(def, abilityRegistry);
        if (!r.Ok)
        {
            string msg = r.Errors.Count > 0 ? r.Errors[0].Message : "faction is incomplete";
            return $"faction '{def.Id}' is incomplete:\n{msg}";
        }
    }
    return null;
}
```
Sketch — call site in `ResetToAuthoredStart` (after the scenario veto, before `ClearForReset`):
```csharp
string? factionBlock = FactionLaunchGate.FirstIncompleteReason(_slotFactionDefs, _abilityRegistry);
if (factionBlock != null)
{
    GD.PrintErr($"[Reset] {factionBlock.Replace("\n", " ")} — staying in Edit");
    ShowTriggerMessage($"Cannot enter Play — {factionBlock}", 5f);
    return false; // veto: nothing cleared, world unchanged
}
```

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~FactionLaunchGateTests"` -- expected: all new gate tests pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full suite green, golden checksum tests unchanged (no sim value altered).

**Manual checks (if no CLI):**
- **World-unchanged on veto (the load-bearing fail-closed AC — the pure tests cannot observe it):** in-editor, place/edit an entity, then assign a scenario slot a faction JSON missing a Worker and press Play/F5 → expect the `"Cannot enter Play — faction '<id>' is incomplete: …missing a required Worker unit."` toast, staying in Edit, AND the placed/edited entity still present (the gate must run before `ClearForReset`, so a vetoed launch must not wipe the board). Do not treat "saw the toast + stayed in Edit" as sufficient — explicitly confirm the placement survived.
- **Real registry reaches the gate (proves the signature check is live, not just the roster check):** assign a slot a faction JSON with a deliberately dangling `signature_mechanic_effect_id` and press Play/F5 → expect a veto naming `signature_mechanic_effect_id`; this only fires if the real loaded `_abilityRegistry` (not `AbilityRegistry.Empty`) reached the gate.
- With the shipped alpha/beta defaults, Play enters normally. (The pure gate tests are the RED-teeth proof for the decision; these manual checks cover the Godot-Node integration the Tier-1 tests cannot reach.)

## Auto Run Result

Status: done

**Summary:** Wired `FactionValidator.ValidateComplete` into the client playtest/skirmish launch boundary (`MainScene.ResetToAuthoredStart`, the Edit→Play transition — the only interactive client match-launch boundary today, since no skirmish/lobby UI exists yet). A new pure Godot-free helper `FactionLaunchGate.FirstIncompleteReason` runs `ValidateComplete` (with the real loaded `AbilityRegistry` threaded, so the `signature_mechanic_effect_id` check fires) over every resolved slot faction; if any is incomplete, `ResetToAuthoredStart` vetoes entry to Play with an actionable located HUD message and stays in Edit — validated BEFORE `ClearForReset` so the world is unchanged on veto. The boot-time shadow diagnostic (`ScenarioLoadPhase`) and the dedicated-server/headless path stay out of scope, unchanged. Closes DW-97's launch-gate half and 14.3's registry-threading deferral at the gate.

**Files changed:**
- `godot/src/Core/Definitions/FactionLaunchGate.cs` (NEW) — pure fail-closed launch-gate decision; mirrors the `ScenarioGate` split-decision idiom for Tier-1 testability.
- `godot/src/Core/Definitions/FactionLaunchGate.cs.uid` (NEW) — Godot-generated script uid (auto-emitted alongside the .cs).
- `godot/src/Core/MainScene.cs` — new fail-closed faction-completeness veto (step 2b) in `ResetToAuthoredStart`, after the scenario veto and before `ClearForReset`.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionLaunchGateTests.cs` (NEW) — 11 xunit tests over the pure gate (all I/O-matrix rows + mesh_path/hero axes + shipped alpha/beta pass with in-memory and real registries).

**Review findings breakdown:** 3 patches applied (1 low doc-accuracy, 1 medium test-coverage for the mesh_path/hero_unit_id axes, 1 medium verification-strengthening of the manual check for the world-unchanged AC + real-registry axis); 1 medium deferred (boot-discovery vs launch-gate registry asymmetry on dangling signature ids — out of scope by intent); remaining reviewer findings rejected as intended-by-intent (shadow/block asymmetry, placeholder-mesh block, first-error presentation) or unreachable (non-deployed-slot veto).

**Verification performed:**
- `dotnet build godot/godot.sln` → succeeded, 0 errors; 11 warnings all pre-existing in untouched files (no new warnings).
- `dotnet test --filter FactionLaunchGateTests` → 11 passed, 0 failed.
- `dotnet test` (full suite) → 1779 passed, 1 pre-existing skip, 0 failed; golden checksum tests green → no re-baseline (no sim value changed).

**Residual risks:** Low. The pure decision is unit-proven; the Godot-`Node` call-site veto (return false → toast → stay-in-Edit, and world-unchanged ordering) is verified by build + inspection + the strengthened manual check, consistent with the `ScenarioGate`/Story-1.7 precedent for logic behind a Godot Node. No sim value altered.
