---
title: 'Expand the faction/player model to 8 and audit every (int)Faction site'
type: 'feature'
created: '2026-07-22'
status: 'done'
baseline_revision: '2845222aef217f4d6c16dd5c90dcde6ea2619e20'
final_revision: '2e48489'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/godot/src/Core/FactionRegistry.cs'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The sim is architected for 8 players (`FactionRegistry.PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9`, `ActiveFactions`/`ToFaction` already N-shaped) but the actual `Faction` enum stops at `Player4` and every per-faction store is still sized to the as-built `FACTION_COUNT=5` (Neutral+P1..4). Constructing a match with >4 active factions today makes `ActiveFactions` emit slot ids 5..8 that overflow the length-5 arrays — a hard `IndexOutOfRangeException` in `ResourceStore` (unguarded writes) and silent wrong results in `MatchStats`/`AllianceStore`/`WinConditionSystem` (guarded-to-0/skip). Load-path validation also rejects slots ≥4 via ceilings hardcoded to `(int)Faction.Player4`.

**Approach:** Atomically (one commit) extend `Faction` to `Player8`, size every per-faction array to `FACTION_ARRAY_SIZE` (9), relax the sim-layer validation/registry ceilings from `Player4` to 8-capable, and generalize `ScenarioDirector`'s literal 2-player threshold-poll loop to iterate active factions. Prove no determinism regression with new N=3 and N=8 two-run byte-identical harnesses while every existing golden (N=2, N=4) stays byte-identical — **no `AlgoVersion` bump, no golden re-baseline** (the checksum fold is `ActiveFactions`-driven, so growing backing arrays is a no-op for existing match sizes).

## Boundaries & Constraints

**Always:** The enum extension and ALL nine array-size sites widen in the SAME commit — an intermediate state where `SLOT_DEFINITIONS_SIZE` (which backs `ScenarioApplier.InFactionRange`) is 9 while the resource/win stores are still 5 is a guaranteed crash at 5–8 players. Prefer referencing `FactionRegistry.FACTION_ARRAY_SIZE` over a fresh `9` literal. Keep the fold `ActiveFactions`-driven; the sim stays Godot-free and Tier-1-testable.

**Block If:** Any existing committed golden (`golden-scenario` N=2, `golden-multifaction` N=4, `SimChecksumCoverageGuardTest` pinned hash `0x1A47DE11`, or any other) moves under these changes. The change is designed to be golden-neutral; a moved golden means an unexpected fold interaction — STOP and investigate, do NOT re-baseline or bump `AlgoVersion`.

**Never:** Bump `SimChecksum.AlgoVersion` or re-record any committed golden (violates the checksum-fold timing rule — no new folded field). Do NOT invent N-player FFA victory semantics for the `defeat` trigger action's `1 - a.Faction` (that is Story 9.14 teams/victory — out of scope; not folded into checksum). Do NOT touch the presentation UI slot caps (deliberately ship-4-for-1.0 per the epic: `WinConditionPhase` survSlot spinbox, `EntityPlacer.START_SLOT_CEILING`, `TriggerEditorPanel` ceiling, MainScene P1/P2 HUD) — those are Story 9.5 / post-1.0 UI. Do NOT touch the merged-tick server (`DedicatedServer 1-slot`, `ServerTransport MAX_PLAYERS=2`, `ServerChecksumCollector MaxSlots=4`) — Story 9.3. Do NOT make `AiOpponentSystem` N-aware (float, non-deterministic AI; multi-AI depends on a separate float→Fixed determinism story).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| 8-faction match runs (AC1) | `new FactionRegistry(8)`, units+ore spawned for Player1..Player8 | Ticks run; `ResourceStore.Ore[(int)Player8]`, MatchStats, WinState, Research, Alliance all read/write slot 8 without OOB | Arrays sized 9 → in-bounds |
| Existing N=2 / N=4 match (AC1) | `FactionRegistry(2)` / `(4)` | Byte-identical SimChecksum to pre-change; folds only slots 1..N regardless of array length | Golden regression gate |
| 8-slot scenario validates (AC1) | scenario with `player_slots` slot in [4,8) | `ScenarioValidator` accepts (ceiling now `Player8`/`PLAYER_COUNT`), not "undefined Faction" reject | Slot ≥8 still fails `>= PLAYER_COUNT` |
| Threshold poll at N>2 (AC1) | scenario active-count = 5 | `resource_threshold`/`unit_count_threshold` emitted for slots 0..4, not just 0..1 | N=2 emits slots 0..1 (unchanged) |
| ScenarioDirector Fixed compare (AC2) | victory/threshold path | Already Fixed end-to-end; zero `.ToFloat()`/`ToString("F2")` in sim path | Verify-only (done by 1.3b+7.4) |
| N=3 & N=8 determinism (AC3) | identical inputs, two in-process runs | `RunAndRecord(...)` sequences `SequenceEqual` each other for N=3 and for N=8 | Divergence → fail with located tick |

</intent-contract>

## Code Map

- `godot/src/Core/EntityWorld.cs` -- **AC1 root.** `Faction : byte` enum (:90-97) stops at `Player4=4`; add `Player5=5..Player8=8`. No exhaustive `switch(faction)` exists in sim (grep-confirmed) so no case-arm fallout.
- `godot/src/Core/FactionRegistry.cs` -- `SLOT_DEFINITIONS_SIZE=5` (:36) → `FACTION_ARRAY_SIZE` (9); it backs `SlotDefinitions[]` and (via `_slotFactionDefs.Length`) `ScenarioApplier.InFactionRange`. Update the now-stale doc comments (:21-36) that call this "Story 9.2's job".
- `godot/src/Core/ResourceStore.cs` -- `FACTION_COUNT=5` (:12) → 9; sizes Ore/Crystal/SupplyUsed/SupplyCap/FactionBase. **Unguarded** `Ore[(int)f]` writes → the hard-crash site.
- `godot/src/Core/MatchStats.cs` -- `FACTION_COUNT=5` (:14) → 9. Guarded (silent-0 at >4 today).
- `godot/src/Core/ResearchStore.cs` -- `FACTION_COUNT=5` (:24) → 9.
- `godot/src/Core/WinStateStore.cs` -- `FACTION_COUNT=5` (:19) → 9.
- `godot/src/Core/WinConditionSystem.cs` -- `FACTION_COUNT=5` (:51) → 9; sizes team scratch AND bounds team-scan guards (`team < FACTION_COUNT`).
- `godot/src/Core/AllianceStore.cs` -- `FACTION_COUNT = FactionRegistry.SLOT_DEFINITIONS_SIZE` (:27) — **auto-follows** the FactionRegistry bump; verify (no edit needed) it now sizes `TeamId[]` to 9.
- `godot/src/Economy/BuildingSystem.cs` -- bare `new FactionDefinition?[5]` (:81) → `[FactionRegistry.FACTION_ARRAY_SIZE]`. Ctor keeps p1/p2 defaults; runtime `SetFactionDef` already handles arbitrary in-range slots.
- `godot/src/Economy/ResearchSystem.cs` -- bare `new FactionDefinition?[5]` (:61) → same as BuildingSystem.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- `Player4` ceilings at :245-248 & :1411-1412 (`slot+1 > (int)Faction.Player4`) → `Faction.Player8`; `ValidateRegistry(..., (int)Faction.Player4)` at :581 & :808 → `FactionRegistry.PLAYER_COUNT`. Fix the false "relaxes automatically" comments (:244, :426).
- `godot/src/Core/ScenarioDirector.cs` -- threshold poll `for slot<2` (:1344); `maxRaiserSlotExclusive:(int)Faction.Player4` (:719). Constructor (:275) gains an optional `FactionRegistry?` param.
- `godot/src/Core/Sim/SimulationHost.cs` -- (:216) passes `checksumFactions` into the new `ScenarioDirector` param (mirrors the :215 `WinConditionSystem(checksumFactions)` pattern).
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionScenario.cs` + `MultiFactionGoldenTests.cs` -- N=4 template (`new FactionRegistry(4)`); the AC3 model to copy for N=3/N=8. `GoldenChecksumReplay.RunAndRecord` + `SequenceEqual` is the two-run pattern.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` (N=2), `SimChecksumCoverageGuardTest.cs` (pinned hash, `FactionRegistry(2)`), `ScenarioDirectorThresholdTests.cs` -- regression gates that must stay green.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/EntityWorld.cs` -- extend `Faction` enum with `Player5=5, Player6=6, Player7=7, Player8=8` -- gives `ToFaction`/`ActiveFactions` real slot ids to emit.
- `godot/src/Core/FactionRegistry.cs` -- set `SLOT_DEFINITIONS_SIZE = FACTION_ARRAY_SIZE`; refresh the :21-36 doc comments to state the widening is now done -- unifies the InFactionRange/SlotDefinitions bound with the stores.
- `godot/src/Core/ResourceStore.cs`, `MatchStats.cs`, `ResearchStore.cs`, `WinStateStore.cs`, `WinConditionSystem.cs` -- change each local `FACTION_COUNT = 5` to `= FactionRegistry.FACTION_ARRAY_SIZE` -- sizes every per-faction array + count-bounded guard to 9 in lockstep (single-source-of-truth).
- `godot/src/Economy/BuildingSystem.cs`, `ResearchSystem.cs` -- replace bare `new FactionDefinition?[5]` with `new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE]` -- the two literal-`[5]` sites with no named const.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- retarget the two `slot+1 > (int)Faction.Player4` ceilings (:245, :1411) to `Faction.Player8` and the two `ValidateRegistry` `maxRaiserSlotExclusive` args (:581, :808) to `FactionRegistry.PLAYER_COUNT`; correct the misleading comments -- lets 8-slot scenarios pass load-time validation.
- `godot/src/Core/ScenarioDirector.cs` -- add optional `FactionRegistry? factions = null` as the last ctor param, store in a `_factionRegistry` field; change the threshold poll (:1344) to `for (int slot = 0; slot < (_factionRegistry?.ActiveCount ?? 2); slot++)`; retarget `maxRaiserSlotExclusive` (:719) to `FactionRegistry.PLAYER_COUNT` -- generalizes the one literal 2-player loop and the trigger raiser ceiling.
- `godot/src/Core/Sim/SimulationHost.cs` -- pass `checksumFactions` into the new `ScenarioDirector` param at :216 -- wires active-count into the director.
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFaction3Scenario.cs` (new) + `MultiFaction8Scenario.cs` (new) -- copy `MultiFactionScenario`, using `new FactionRegistry(3)` / `new FactionRegistry(8)`, spawning ≥1 unit and seeding ore for each active faction (Player1..3 / Player1..8) so every active slot is exercised and the sequence evolves -- gives AC3 real N=3 and true 8-player coverage of the resized arrays/new enum members.
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionExpansionTests.cs` (new) -- for N=3 and for N=8, call `GoldenChecksumReplay.RunAndRecord(ticks, build)` twice and assert `seq1.SequenceEqual(seq2)` (no committed golden file — AC3 is two-run in-process equality, which also sidesteps the golden-CRLF tripwire) -- proves the expansion introduces no desync.

**Acceptance Criteria:**
- Given the extended `Faction` enum and 9-sized arrays, when an 8-faction sim spawns units/ore for Player1..Player8 and ticks, then no `IndexOutOfRangeException` occurs and every per-faction store addresses slot 8; and every existing golden (`golden-scenario` N=2, `golden-multifaction` N=4, `SimChecksumCoverageGuardTest` pinned `0x1A47DE11`) remains byte-identical with `AlgoVersion` unchanged.
- Given a scenario declaring a `player_slots` slot in [4,8), when `ScenarioValidator.Validate` runs, then it is accepted (no "undefined Faction (engine ceiling)" failure), while a slot ≥ 8 still fails the `>= PLAYER_COUNT` guard.
- Given `ScenarioDirector` at active-count N, when the per-tick threshold poll runs, then it emits `resource_threshold`/`unit_count_threshold` for slots 0..N-1 (N=2 unchanged at slots 0..1), and `ScenarioDirectorThresholdTests` stays green.
- Given the ScenarioDirector victory/threshold path, when inspected, then it contains no `.ToFloat()`/`ToString("F2")`/float arithmetic in the sim path (AC2 — confirmed already satisfied by Stories 1.3b + 7.4; verify-only, no code change).
- Given the new N=3 and N=8 scenarios, when each runs identical inputs twice in-process, then the two checksum sequences are byte-identical (`SequenceEqual`) for each N.

## Review Triage Log

### 2026-07-22 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 2, medium 2, low 1)
- defer: 5: (high 0, medium 2, low 3)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[high]` `[patch]` `BuildingSystem.RecalculateSupplyCaps` had two `for (f=1; f<=4)` per-faction loops (base-reset + hard-ceiling clamp) the planning audit missed — Player5-8 SupplyCap never reset/clamped. Widened both to `f < FactionRegistry.FACTION_ARRAY_SIZE`; added `RecalculateSupplyCaps_HighSlotPlayer8_ResetsToBase_AddsBonus_AndClamps`.
  - `[high]` `[patch]` `ResearchSystem.Tick`'s `for (f=1; f<=4)` countdown loop froze Player5-8 in-progress research forever. Widened to `f < FactionRegistry.FACTION_ARRAY_SIZE`; added `Tick_HighSlotPlayer8_InProgressOrder_CountsDownAndResolves`.
  - `[medium]` `[patch]` `ScenarioValidator` `suggested_players` cap `[2,4]` (tied to the old Player4 engine ceiling) rejected genuine 8-player scenarios. Raised to `FactionRegistry.PLAYER_COUNT`; updated pinned `SuggestedPlayersOutOfRange_FailsClosed` (5→9) and added `SuggestedPlayersAtCeiling_Passes` (8 validates).
  - `[medium]` `[patch]` No test proved faction slots 5-8 actually fold into `SimChecksum` (only Player3 was proven; a fold-span regression would slip through). Added `OreLoop_SpansExactlyTheActiveFactions_TopSlotPlayer8_NotATautology` (mutating `Ore[Player8]` moves the hash under `FactionRegistry(8)`, not `(7)`).
  - `[low]` `[patch]` `ScenarioDirector` base-event buffer `+ 5` term no longer covered the widened poll (up to 2×8 threshold events). Changed to `+ (2 * FactionRegistry.PLAYER_COUNT + 1)` + comment, restoring correct-by-construction sizing (overflow was unreachable behind the MAX_ENTITIES headroom).
- deferred (see `deferred-work.md`): `defeat`→`1-faction` FFA victory semantics (9.14); TriggerEditorPanel raiser ceiling + MatchChatOverlay labels/colors (presentation, 9.5); `_factions` P1/P2-only population (9.7 lobby wiring); optional committed N=8 cross-process golden.
- rejected: threshold poll "contraction" at ActiveCount==1 (malformed 1-faction-with-Player2-trigger premise; generalized `slot < ActiveCount` is more correct and directly serves the "no 2-player assumptions" intent); server N-scale MAX_PLAYERS=2 (Story 9.3, backlogged); AiOpponentSystem Player2-hardcode (documented fence, separate float→Fixed story); N=3/N=8 scenario code duplication (quality nit); "float→Fixed not done" (IA confirmed AC2 already-satisfied by 1.3b+7.4 — validation, not a defect).

### 2026-07-22 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 0, low 3)
- defer: 1: (high 0, medium 0, low 1)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - `[low]` `[patch]` The load-bearing constant/enum invariant (`FACTION_ARRAY_SIZE == PLAYER_COUNT+1 == (int)Faction.Player8+1 == SLOT_DEFINITIONS_SIZE`) was asserted nowhere — a future bump of one without the others would produce a runtime OOB in the poll/stores that no unit test catches until an N>8 match runs. Added `FactionRegistryTests.Constants_EnumAndArraySizesAgree_TheLoadBearingInvariant` tying the four numbers, and extended `ToFaction_IsTheOnePlaceTheSlotPlusOneOffsetLives` to cover the new slots 4-7 → Player5-8.
  - `[low]` `[patch]` The generalized threshold poll was proven firing only at slot 2 (N=3); a regression re-capping the span below `ActiveCount` would be caught nowhere at the top new slot. Added `ScenarioDirectorThresholdTests.ResourceThreshold_AtN8_PollReachesTopSlot7_Fires` (Player8 fires at N=8, stays silent at N=2) via the existing `ThresholdFiresForSlot` helper.
  - `[low]` `[patch]` Comments in `ResourceStore.cs`, `Sim/DslVarReadback.cs`, and three sites in `ScenarioValidator.cs` still described the pre-9.2 `Player4`/`[0,3]` engine ceiling, actively misstating the now-`Player8`/`[0,7]` range for the next maintainer. Corrected all five (doc-only; code was already correct).
- deferred (see `deferred-work.md`): map-authoring UI still caps player count at 4 (New-Map picker `MapPropertiesPanel`, start-position markers `StartPositionBridge`, placement ceiling `EntityPlacer.START_SLOT_CEILING`) while the validator now accepts 8 — the ship-4-UI-for-1.0 authoring surface, Story 9.5.
- rejected (already tracked or deliberate design): TriggerEditorPanel raiser ceiling, MatchChatOverlay Player5-8 labels/colors, `defeat`→FFA semantics, `_factions` P1/P2-only population, and the optional committed N=8 cross-process golden (all already in `deferred-work.md`); two-run-vs-committed-golden "oversells" framing (intent deliberately chose two-run in-process); ActiveCount==1 poll narrowing (prior pass rejected — `slot < ActiveCount` is correct); redundant `> Player8` ceiling in ScenarioValidator (deliberate documented defensive assertion); `unit_count_threshold` high-slot untested (shares the identical poll-loop iteration now pinned by the resource_threshold top-slot test); MatchStats[8] unexercised (not folded into SimChecksum; defensive widening, no present defect); WinState/Alliance/WinCondition slot-8 folds "tautological-only" (OOB-avoidance proven by the N=8 harness, fold-span mechanism proven by the non-tautological Ore[Player8] test); scenario duplication + perturb-target-invariant-unenforced in the new N=3/N=8 harnesses (quality nits).

## Design Notes

**Why no golden re-baseline (the load-bearing determinism claim):** `SimChecksum.Compute` folds per-faction arrays by iterating `factions.ActiveFactions` (SimChecksum.cs:418-428, and identically for Research :532 / WinState :601 / Alliance :630) — an `activePlayerCount`-driven span, e.g. exactly `[Player1,Player2]` for an N=2 match. Growing a backing array from length 5 to 9 does not change which indices are read for a given N, so every existing N≤4 golden is byte-identical and `AlgoVersion` (21) must not move. The DSL per-player fold already spans all 8 slots (`DslVarTable.PlayerSlots=8`) and is unaffected.

**Atomicity (the InFactionRange trap):** `ScenarioApplier.InFactionRange` derives its upper bound from `SlotDefinitions.Length` == `SLOT_DEFINITIONS_SIZE`. Raising only that (or only the enum) while the resource/win stores stay length-5 lets slots 4-7 pass the guard and then throw on the store write. All nine size sites + the enum move together.

**AC2 is done-by-prior-work (stale epics hint):** epics.md cites a ScenarioDirector float leak "at :168/:170" — that was removed by Story 1.3b and Story 7.4 (all threshold compares are now `Fixed`-vs-`Fixed` via `Compare(Fixed,Fixed,string)` with a `Fixed` epsilon; `InvariantCulture` pinned process-wide). Grep confirms zero float/locale formatting in the path. AC2 is satisfied — verify, don't fabricate a conversion.

**Scope fences (audited, deliberately deferred):** `defeat`→`OnVictory(1 - a.Faction)` (ScenarioDirector:2089) is a 1v1 "other wins" construct; correct N-player FFA winner-on-single-defeat is a design decision owned by Story 9.14 (teams) and is not folded into checksum — leave it. Presentation slot caps (`WinConditionPhase` survSlot `NewIntSpin(0,3)`, `EntityPlacer.START_SLOT_CEILING=4`, `TriggerEditorPanel:1366`, MainScene P1/P2 HUD, `FogOfWarSystem(Player1)`) are intentional ship-4-UI-for-1.0 (Story 9.5 / post-1.0). Server N-scale (`DedicatedServer 1-slot`, `ServerTransport MAX_PLAYERS`, `ServerChecksumCollector MaxSlots`) is Story 9.3. `AiOpponentSystem` (Player2-hardcoded, float AI) needs a separate float→Fixed determinism story before multi-AI.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: compiles clean; the determinism banned-API analyzer stays green (no new `float` in sim).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass, including the new N=3/N=8 determinism tests; **every pre-existing golden byte-identical** (a moved golden is a Block-If, not a re-baseline).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~MultiFaction` -- expected: N=4 golden unchanged + N=3/N=8 two-run equality green.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~SimChecksumCoverageGuard` -- expected: pinned hash `0x1A47DE11` unchanged; per-faction-array coverage guard green.


## Auto Run Result

Status: done (follow-up review pass on an already-`done` spec)
Blocking condition: none

**Summary of implemented change (reviewed):** The Story 9.2 faction/player-model expansion (Player4→Player8): the `Faction` enum gains Player5..Player8, every per-faction array widens to `FactionRegistry.FACTION_ARRAY_SIZE` (9), sim-layer validation/registry ceilings relax from `Player4` to 8-capable, and `ScenarioDirector`'s threshold poll generalizes from a literal 2-player loop to iterate active factions. This pass reviewed that change (diff since baseline `2845222`) with 4 parallel reviewers and hardened it.

**Files changed this pass (one line each):**
- `godot/ProjectChimera.Sim.Tests/Golden/FactionRegistryTests.cs` — added the load-bearing constant/enum invariant guard (`FACTION_ARRAY_SIZE == PLAYER_COUNT+1 == (int)Player8+1 == SLOT_DEFINITIONS_SIZE`); extended `ToFaction` coverage to slots 4-7 → Player5-8.
- `godot/ProjectChimera.Sim.Tests/Golden/ScenarioDirectorThresholdTests.cs` — added `ResourceThreshold_AtN8_PollReachesTopSlot7_Fires` proving the poll reaches the top slot at N=8.
- `godot/src/Core/ResourceStore.cs` — corrected stale `Player4` doc comment to `Player8`.
- `godot/src/Core/Sim/DslVarReadback.cs` — corrected stale `Player4` doc comment to `Player8`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — corrected three stale `[0,3]`/`Player4` engine-ceiling comments to `[0,7]`/`Player8` (doc-only; logic already correct).
- `_bmad-output/implementation-artifacts/deferred-work.md` — one new deferred entry (map-authoring UI 4-cap → Story 9.5).

**Review findings breakdown (this pass):** patch 3 (all low, applied) · defer 1 (low) · reject 10 (low) · intent_gap 0 · bad_spec 0.
- Patches applied: constant/enum invariant guard + ToFaction slots 4-7; top-slot threshold-poll firing test at N=8; stale `Player4`/`[0,3]` comment corrections (5 sites).
- Deferred (1, in `deferred-work.md`): map-authoring UI still caps player count at 4 (`MapPropertiesPanel`, `StartPositionBridge`, `EntityPlacer.START_SLOT_CEILING`) while the validator now accepts 8 → Story 9.5 UI-to-8.
- Rejected (10): TriggerEditorPanel raiser ceiling, MatchChatOverlay Player5-8 labels/colors, `defeat`→FFA semantics, `_factions` P1/P2-only population, optional N=8 cross-process golden (all already tracked in `deferred-work.md`); two-run-vs-golden "oversells" framing (intent chose two-run in-process); ActiveCount==1 poll narrowing (prior pass rejected); redundant `>Player8` ceiling (deliberate defensive); `unit_count_threshold` high-slot (shares the poll-loop iteration now pinned); MatchStats[8] unexercised (not folded; defensive widening); WinState/Alliance/WinCondition "tautological-only" slot-8 (OOB-avoidance proven by N=8 harness, fold-span by the Ore[Player8] test); N=3/N=8 scenario duplication + perturb-target invariant (quality nits).

**Follow-up review recommendation:** `false`. Patched findings this pass: high 0, medium 0, low 3 → score = 3×0 (medium) + 1×3 (low) = 3 < 5, and no high; therefore false.

**Verification performed:**
- `dotnet build godot/godot.csproj` → 0 errors (11 pre-existing warnings; determinism analyzer green).
- `dotnet test godot/ProjectChimera.Sim.Tests` → 2989 passed, 0 failed, 1 pre-existing skip.
- `dotnet test ... --filter MultiFaction|SimChecksumCoverageGuard|FactionRegistryTests|ScenarioDirectorThreshold` → 35 passed; pinned SimChecksum hash `0x1A47DE11` unchanged, every committed golden byte-identical, `AlgoVersion` unchanged.

**Residual risks / artifacts:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` was already modified before this run started and is not part of the reviewed diff — left in place (subsequently swept into an AutoSave commit).
- Cross-process determinism for the newly-active slots 5-8 remains pinned only by the two-run in-process harness plus the shared fold paths of the N=2/N=4 goldens (deliberate per AC3; optional committed N=8 golden already deferred).

Review commit: `2e48489` (recorded as `final_revision`).
