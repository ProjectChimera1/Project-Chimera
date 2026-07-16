---
title: 'Story 7.1: Trigger-layer determinism prerequisites (ordering, Fixed, culture)'
type: 'refactor'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: 'b4e0328dd582a3d63dc0f00888b9ebd2d6b321a9'
final_revision: '59cc34a5d9a80872a9c0806cb63c9dbd9bea0273'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The as-built flat ECA trigger tick (`ScenarioDirector`) still carries the determinism hazards Epic 7's IR rebuild must start clean of: timer/variable state lives in `Dictionary<string,int>` (AR-16 forbids dictionary enumeration in sim; the current ordinal-key snapshot is Story 1.4's band-aid, and it fires same-tick timers in *alphabetical* order rather than declaration order), threshold event payloads round-trip through `int.ToString(InvariantCulture)` → `int.TryParse` inside the tick, `TriggerAction.X/Z/Duration` are `float` so a trigger `spawn_unit` runs an in-tick `Fixed.FromFloat` plus a `i * 2.5f` float offset, InvariantCulture is only applied per-call (not pinned process-wide), and the per-tick `Array.Sort` trips the CHM0003 unstable-sort analyzer.

**Approach:** Finish the determinism hardening Story 1.4 started, without changing observable sim outcomes. Replace the director's timer and variable dictionaries with dense index-keyed stores iterated in creation/declaration order; retype the internal `FiredEvent` numeric payloads to typed ints so no string formatting/parsing occurs in the tick; retype `TriggerAction.X/Z/Duration` to `Fixed` at the deserialization boundary and route trigger spawns through the existing `ScenarioApplier.SpawnUnitAt(Fixed,Fixed)` primitive (converting to `float` only at the presentation delegate boundary); pin `CultureInfo` invariant at the process composition roots; and precompute the trigger evaluation order once at load via a stable LINQ `OrderByDescending/ThenBy` (analyzer-clean). This story folds **nothing new** into `SimChecksum` — that (and the top-level `DslVarTable` hoist) is Story 7.3 — so the golden baselines stay byte-identical.

## Boundaries & Constraints

**Always:**
- Sim layer (`src/Core`) stays Godot-free, `float`-free in the tick path, and iterates in ascending/declaration order. Fractional numerics are `Fixed` 16.16; `Fixed.FromFloat` never runs inside a tick.
- Timer/variable stores are dense index-keyed (SoA-style parallel arrays or index-mapped lists), NOT `Dictionary`/`HashSet`. Same-tick timer expiries emit in creation-index (declaration) order, deterministically and independent of insertion history.
- Trigger evaluation order is an explicit total order: Priority desc, then ascending declaration index. Compute it once per `LoadScenario` (order is stable for the match; only Enabled/fired/cooldown change).
- Observable sim outcomes for existing scenarios are unchanged: the full golden-checksum suite stays byte-identical with NO baseline edit. Behavioral trigger tests keep asserting the same fire/emit results (except the timer same-tick order, which intentionally moves ordinal → creation-index).
- Existing determinism tests that reflect on renamed/retyped internals (`TimerDeterminismTests`, `TriggerOrderingTests`) are updated in the same change so the suite stays green.

**Block If:**
- A change forces an existing golden `*.golden.txt` baseline to change (would mean a checksummed-state change slipped in — 7.1 must not touch checksummed state). HALT `blocked`, condition `unexpected golden baseline drift`.
- Retyping `TriggerAction.X/Z` to `Fixed` cannot be parsed by `ScenarioSerializer`'s options (it should — `FixedJsonConverter` is registered). If parsing regresses, HALT `blocked`.

**Never:**
- Do NOT fold timers or variables into `SimChecksum`, do NOT hoist a top-level `DslVarTable`, do NOT add typed value-kinds/per-player scopes, and do NOT introduce graph-IR node ids — all are Story 7.2/7.3 scope. Declaration index remains the ordering surrogate here.
- Do NOT retype the other Scenario placement DTOs (`ScenarioUnit/ScenarioBuilding/ScenarioProp/…` X/Z) — those are init-time placement where load-time `Fixed.FromFloat` is allowed and in-scope only for their own stories. Only `TriggerAction` is retyped.
- Do NOT widen threshold coverage to all factions (`slot < 2` stays — that is Story 9.2).
- Do NOT modify the CHM0003 analyzer itself (its over-flag/miss gaps are a separate deferred item); just stop tripping it.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Same-tick timer expiries | Two timers created in order [B, A], both reaching 0 this tick | `timer_expires` emit in creation order [B, A] — independent of name and of insertion history across peers | No error |
| Equal-priority triggers | N (>16) match_start triggers, identical priority | Fire in ascending declaration index [0..N-1], deterministically | No error |
| Distinct-priority triggers | priorities [mid=5, high=10, low=1] | Fire high→mid→low (priority is primary key) | No error |
| resource_threshold across culture | Ore=150 raw Fixed, threshold 100, op `>=`, tick run under de-DE culture | Fires identically to invariant culture (no string format/parse in path) | No error |
| Trigger `spawn_unit` count=3 at (x,z) | Fixed x,z from JSON | 3 units spawned via `SpawnUnitAt` at Fixed x + {0,2.5,5.0} offset, z — no in-tick `Fixed.FromFloat`, no float offset | Unknown unit_id logs warn, no spawn (unchanged) |

</intent-contract>

## Code Map

- `godot/src/Core/ScenarioDirector.cs` -- the tick surface: `_timers`/`_variables` dictionaries, `CollectEvents` (timer loop :164-183, threshold emit :185-196), `FiredEvent` struct (:456), `EvaluateTriggers` `Array.Sort` (:219-225), `EventMatches` TryParse (:301/:305), `ExecuteActions` spawn/message/timer/set_variable (:367-410).
- `godot/src/Core/Definitions/TriggerDefinition.cs` -- `TriggerAction.X/Z` (float :163/:166), `Duration` (float :176) to retype to `Fixed`.
- `godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs` -- binds `OnSpawnUnit`/`OnDisplayMessage`; call `SpawnUnitAt` with Fixed offset; convert Fixed→float for `ShowTriggerMessage`.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- `SpawnUnitAt(def, faction, Fixed x, Fixed z)` (Fixed-native primitive, already exists) — the target for trigger spawns.
- `godot/src/Core/MainScene.cs` -- game composition root + `ShowTriggerMessage(string,float)` (presentation, :1536) — culture pin site (game).
- `godot/src/Core/Sim/ServerBootstrap.cs` -- headless server entry — culture pin site (server).
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- validates `spawn_unit` X/Z coordinates (deferred-work #154) — update for the Fixed retype.
- `godot/ProjectChimera.Sim.Tests/Golden/TimerDeterminismTests.cs` -- reflects on `_timers` (Dictionary) + `FiredEvent.Data`; asserts ordinal order — rewrite for dense store + creation-index order.
- `godot/ProjectChimera.Sim.Tests/Golden/TriggerOrderingTests.cs` -- behavioral fire-order proof; keep assertions, refresh introsort-threshold rationale.
- `godot/ProjectChimera.Sim.Tests/Golden/ScenarioDirectorThresholdTests.cs` -- constructs `TriggerAction{Duration=1f}` — update literal; de-DE culture proof stays.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs`, `SameTickTieBreakScenario.cs`, and `*.golden.txt` -- regression baselines that must stay byte-identical.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/ScenarioDirector.cs` -- (1) Replace `Dictionary<string,int> _timers` and `_variables` with dense index-keyed stores (name→index map + parallel value arrays/lists), created/updated in first-seen order, iterated by ascending index; timer expiry loop emits in creation-index order (delete the `OrderBy(Ordinal)` snapshot). (2) Retype `FiredEvent` to carry numeric payloads as typed ints (e.g. `int Numeric` for ore-raw and unit-count) alongside the existing `string? Data` for building-type/timer-name; drop the `ToString(InvariantCulture)` emits and the `int.TryParse` compares — compare `Fixed.FromRaw(numeric)`/`int` directly. (3) Retype `OnSpawnUnit` to `Action<string,int,Fixed,Fixed,int>` and `OnDisplayMessage` to `Action<string,Fixed>`; `ExecuteActions` passes `a.X/a.Z/a.Duration` (now Fixed) through unchanged. (4) Precompute trigger order once in `LoadScenario` into `int[] _triggerOrder` via `Enumerable.Range(0,n).OrderByDescending(i => _triggers[i].Priority).ThenBy(i => i).ToArray()` (stable, analyzer-clean); `EvaluateTriggers` iterates `_triggerOrder`, deleting the per-tick `Array.Sort`. -- removes dictionary enumeration, string round-trip, in-tick float, and the CHM0003 hit from the tick path.
- `godot/src/Core/Definitions/TriggerDefinition.cs` -- Retype `TriggerAction.X`, `Z`, `Duration` from `float` to `Fixed` (defaults `Fixed.Zero`, `Fixed.Zero`, `Fixed.FromInt(4)`). -- `FixedJsonConverter` (registered in `ScenarioSerializer`) parses JSON numbers to Fixed at the deserialization boundary.
- `godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs` -- Update the `OnSpawnUnit` lambda to Fixed `(x,z)`, call `ctx.Applier.SpawnUnitAt(def, faction, x + Fixed.FromInt(i) * Fixed.FromRaw(163840) /* 2.5 */, z)` (or a named Fixed 2.5 constant), removing the `SpawnUnit(float)`/`i*2.5f` path; wrap `OnDisplayMessage` to convert Fixed→float at the presentation boundary: `(text, dur) => ctx.Scene.ShowTriggerMessage(text, (float)dur.ToDouble())` (or Fixed's float accessor). -- keeps the in-tick path Fixed-only; float appears only presentation-side.
- `godot/src/Core/MainScene.cs` + `godot/src/Core/Sim/ServerBootstrap.cs` -- At the earliest-running entry of each process root (e.g. `MainScene._EnterTree`; `ServerBootstrap`'s startup method), set `CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture`. -- pins invariant culture process-wide as a hardening net.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- Update the `spawn_unit` coordinate validation to read `TriggerAction.X/Z` as `Fixed` (finite check is now guaranteed by `FixedJsonConverter`; keep any range/NaN intent, adapting the comparison to Fixed). -- keep the validator compiling and semantically equivalent.
- `godot/ProjectChimera.Sim.Tests/Golden/TimerDeterminismTests.cs` -- Rewrite reflection for the dense store; assert same-tick expiries emit in CREATION-index order (create [B,A] → emit [B,A]) independent of insertion history; keep it a direct emission-order assertion (checksum comparison would be tautological — timers are not in `SimChecksum` until 7.3). -- update the negative-control rationale accordingly.
- `godot/ProjectChimera.Sim.Tests/Golden/TriggerOrderingTests.cs` + `ScenarioDirectorThresholdTests.cs` + all other `new TriggerAction` sites -- Update `X/Z/Duration` float literals to `Fixed` (e.g. `Duration = Fixed.FromInt(1)`); keep behavioral assertions. Sweep sites via `grep -rn "new TriggerAction" godot --include=*.cs`. -- suite compiles and stays green.
- `godot/ProjectChimera.Sim.Tests/Golden/` (new test) -- Add a behavioral determinism test: two equal-priority `set_variable` triggers writing the SAME variable to different values, then a `variable_comparison`-gated observable action, proving last-writer follows declaration-index order deterministically across two fresh `ScenarioDirector` runs. -- covers AC1's shared-variable ordering surface without depending on the (not-yet-implemented) checksum fold.

**Acceptance Criteria:**
- Given two timers created in order [B, A] both expiring this tick, when `CollectEvents` runs, then `timer_expires` emit in creation order [B, A] regardless of insertion history and with no `Dictionary` enumeration in the path.
- Given two equal-priority triggers writing a shared variable to different values, when the tick evaluates, then the final value is the declaration-index last-writer, identically across two fresh headless runs; and given three distinct-priority triggers, they fire priority-desc.
- Given a `resource_threshold`/`resource_comparison` trigger evaluated under a comma-decimal culture (de-DE), when the tick runs, then it fires identically to invariant culture and no `int.ToString`/`TryParse`/float exists anywhere in the tick path.
- Given a trigger `spawn_unit` action, when it executes, then units spawn via `SpawnUnitAt(Fixed,Fixed)` with a Fixed lateral offset and zero in-tick `Fixed.FromFloat`.
- Given the full golden-checksum suite (`GoldenChecksumReplay`, `SameTickTieBreak`, ability/AI goldens), when run after the change, then every baseline is byte-identical with no `*.golden.txt` edit (recorded as the named expected event: "no baseline change — 7.1 folds nothing into SimChecksum").
- Given a `dotnet build`, when it completes, then `ScenarioDirector.cs` raises no CHM0003 (unstable-sort) diagnostic.

## Spec Change Log

## Review Triage Log

### 2026-07-16 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 2
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` Sub-tick `create_timer` (0 < timer_seconds < 1/30s) rounded to 0 ticks and, stored as `remaining=0`, was treated as expired/inactive by the collect loop → the timer never fired (the old Dictionary path stored 0 and fired one tick later). Confirmed independently by the blind + edge-case reviewers. Fixed by clamping the create path to `Math.Max(1, SecondsToTicks(...))`, exactly reproducing the old fires-next-tick latency; added `TimerDeterminismTests.SubTickCreateTimer_StillFiresNextTick` (drives a real director end-to-end; red without the clamp).
  - `[low]` `[patch]` The process-wide culture pin set only `DefaultThreadCurrentCulture/UICulture`, which does not govern a thread that already materialized a culture (main thread / earlier autoload). Also set `CurrentCulture/CurrentUICulture` at both `MainScene._EnterTree` and `ServerBootstrap` so the hardening net actually covers the running thread.

## Design Notes

- **Why the golden baselines don't move:** every golden builds in code with empty trigger state (`LoadScenario(new ScenarioData())`), so `ScenarioDirector.Tick` early-returns — the changed code never executes in a golden. Timers/variables are not in `SimChecksum` (it folds buildings/heroes/entities/resources only), and the trigger order was already deterministic (Story 1.4). Trigger-spawned unit positions are the only Fixed-vs-float-quantization difference, and no golden spawns via triggers. Hence byte-identical, no re-pin. If a baseline *does* move, that's a real regression → HALT (see Block If).
- **Fixed 2.5 constant:** `2.5 = 163840` raw in 16.16. Prefer a named `Fixed` constant over the raw literal in the binder for readability.
- **Dense store shape:** a `List<int>` value store plus a `Dictionary<string,int>` name→index built at create time is acceptable *only if* the Dictionary is never enumerated in the tick (lookup-by-key is deterministic; enumeration is the banned operation). Iterating the value `List` by ascending index is the deterministic contract. A pure parallel-array + linear-scan name lookup also works and fully avoids `Dictionary`; pick the simpler one that keeps enumeration out of the tick.
- **This is a bridge, not the destination:** 7.3 replaces these director-local stores with a top-level `DslVarTable` folded into `SimChecksum`. Keep the stores minimal and self-contained so 7.3's hoist is clean; do not over-build typed value kinds or scopes here.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds; no CHM0003 diagnostic on `ScenarioDirector.cs`; no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green, including `TimerDeterminismTests`, `TriggerOrderingTests`, `ScenarioDirectorThresholdTests`, the new equal-priority-variable test, and every `*Golden*` test (baselines byte-identical).
- `git status --porcelain godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty (no baseline file changed).

## Auto Run Result

Status: done

### Summary
Finished the trigger-layer determinism hardening Story 1.4 began, with no change to observable sim outcomes. `ScenarioDirector`'s `Dictionary<string,int>` timer/variable stores became dense creation-index parallel-array SoA (no `Dictionary`/`HashSet` in the tick); same-tick timer expiries now emit in creation/declaration order; `FiredEvent` carries a typed `int Numeric` payload so the `ToString→TryParse` round-trip is gone; `TriggerAction.X/Z/Duration` and the `OnSpawnUnit`/`OnDisplayMessage` delegates are `Fixed` (trigger spawns route through `ScenarioApplier.SpawnUnitAt`, float appears only at the presentation boundary); InvariantCulture is pinned process-wide at both composition roots; and the per-tick `Array.Sort` became a stable order precomputed once per `LoadScenario` (clears CHM0003). Nothing was folded into `SimChecksum` (Story 7.3's scope), so every golden baseline stayed byte-identical.

### Files changed
- `godot/src/Core/ScenarioDirector.cs` — dense timer/variable stores; typed `FiredEvent.Numeric`; `Fixed` spawn/message delegates; precomputed `_triggerOrder`; sub-tick `create_timer` clamp (review patch).
- `godot/src/Core/Definitions/TriggerDefinition.cs` — `TriggerAction.X/Z/Duration` float→`Fixed`.
- `godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs` — `SpawnUnitAt` Fixed offset; Fixed→float display conversion at the presentation boundary.
- `godot/src/Core/MainScene.cs`, `godot/src/Core/Sim/ServerBootstrap.cs` — process-wide InvariantCulture pin, now also pinning `CurrentCulture/CurrentUICulture` (review patch).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — `Fixed`-aware spawn-coordinate validation.
- `godot/src/AI/LLMService.cs` — Fixed adaptation of the AI trigger guard (hard compile dependency of the retype; beyond the spec's file list).
- Tests: `TimerDeterminismTests.cs` (creation-index order + new sub-tick regression test), `TriggerOrderingTests.cs`, `ScenarioDirectorThresholdTests.cs`, `TriggerValidationTests.cs`, `ScenarioValidatorPathabilityTests.cs`, `ScenarioValidatorPropsWaterTests.cs`; new `EqualPriorityVariableOrderingTests.cs`, `ScenarioDirectorSpawnActionTests.cs`.

### Review findings breakdown
- **Patches applied (2):** [medium] sub-tick `create_timer` never fired (`remaining==0` overload) → clamped to `Math.Max(1, SecondsToTicks)` + behavioral test; [low] culture pin didn't cover the running thread → added `CurrentCulture/CurrentUICulture`.
- **Deferred (2):** `ScenarioDelegateBinder` fan-out offset + Fixed→float display conversion unverified (needs a Godot `SceneContext`/helper refactor); `LLMService` AI trigger-guard untested (pre-existing untested surface). Both recorded in `deferred-work.md`.
- **Rejected (9):** pathological Fixed-ceiling spawn overflow (deterministic, pre-existing class); fan-out outside map_bounds (pre-existing); never-reclaimed expired slots (deterministic, bounded, by-design, superseded by 7.3); culture-pin test-gap (tick is structurally culture-free); thinned timer negative controls (adequate coverage remains); `_triggerOrder` priority-immutability assumption (correct today); vestigial de-DE threshold test (harmless); intent-alignment proof-surface divergence (correctly scoped — checksum fold is 7.3 per epic sequencing); minor validator recompute / epsilon notes.

### Follow-up review
`followup_review_recommended: false` — the final pass applied two small, localized fixes (one medium one-line clamp + test, one low two-line hardening) with no API/data/security impact.

### Verification performed
- `dotnet build godot/godot.sln` → succeeded, 0 errors (11 pre-existing nullable-context warnings only); CHM0003 no longer reported on `ScenarioDirector.cs`.
- `dotnet test godot/ProjectChimera.Sim.Tests` → 1796 passed, 1 skipped (pre-existing AR-13 reserved), 0 failed; all `*Golden*` tests green.
- `git status --porcelain …/Golden/*.golden.txt` → empty (no baseline moved — the named expected event: "no baseline change").

### Residual risks
- The binder fan-out offset and the AI trigger guard remain unverified by automated tests (deferred above) — both are determinism-adjacent but structurally guarded (Fixed types, JSON-boundary range checks).
- Two new test `.cs` files were committed without Godot `.cs.uid` companions (the editor generates those on next open), consistent with how other test files were added.
