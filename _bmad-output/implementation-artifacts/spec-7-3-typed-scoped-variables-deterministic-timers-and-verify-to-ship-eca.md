---
title: 'Story 7.3: Typed scoped variables, deterministic timers, and verify-to-ship ECA'
type: 'feature'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: 'b2f06984d8cc7ecd0fcb54eced11afb8f8a495e4'
final_revision: '45074dc70c10a1c4fe0cda2792d47707f5048271'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['multiple-goals', 'oversized']
---

<intent-contract>

## Intent

**Problem:** Epic 7 needs game-logic *state* — scoreboard/economy/per-player counters and scheduled delayed logic — but today trigger variables are an untyped, single-global-scope `int` list and timers are name-keyed tick counters, both improvised inside `ScenarioDirector`, declared nowhere in `ScenarioData`, and **absent from `SimChecksum`** (so they can silently desync). The graph IR from 7.2 is only a load-time identity waypoint (the tick still walks the lowered flat form), the `run_effect` embed seam never executes, and the Trigger Editor is AI-only — no manual ECA authoring, no variable declaration, no raw-IR escape hatch — so "basic ECA" is not at a shippable bar.

**Approach:** Hoist variables + timers into one typed, scoped top-level sim store (`DslVarTable`, sibling of the other `SimulationHost` stores) with the closed value-type set and Global / Per-player(0..7) / Trigger-local scopes; fold its live Global + Per-player values and timer remaining-ticks into `SimChecksum` in declaration/creation-index order (AlgoVersion 15→16, recorded golden re-baseline). Declare variables/timers in `ScenarioData` (round-tripping name/type/scope/initial as `Fixed.Raw`), excluded from the multiplayer start-state handshake exactly as Triggers/Regions are. Rewire `ScenarioDirector` to **walk the graph IR directly** (superseding 7.2's flat lowering) so `run_effect` fires via the existing `EffectExecutor` and variable/timer leaves hit the table, with byte-identical behavior for legacy flat scenarios. Extend `TriggerEditorPanel` with a manual ECA preset form, a variable-declaration section, and a raw-IR (canonical JSON) escape hatch that persists graph-only triggers into a new optional `trigger_graph` field.

## Boundaries & Constraints

**Always:**
- `DslVarTable` and all new value types are **pure sim-layer C#**: Godot-free (no `using Godot;`), float-free — fractional numerics are `Fixed` 16.16 stored as `Fixed.Raw`, quantized only at the JSON boundary via the registered `FixedJsonConverter`. Same `ProjectChimera` assembly as `src/Dsl/` (Core may reference Dsl; Dsl must not reference Core/Godot).
- Closed value-type set exactly: `Int, Fixed, Bool, EntityRef, FactionRef, Point, TimerRef, Array` (scalar element). Scopes exactly: `Global`, `PerPlayer(0..7)`, `TriggerLocal`. Storage is **dense SoA in declaration/creation-index order** — never a `Dictionary`, never name-sorted, never hash-set-enumerated (AR-16 determinism).
- `SimChecksum.Compute` is widened with a trailing nullable `DslVarTable?` param folded via the existing guarded store pattern (`null` → a single `Mix(0)` so legacy callers agree with an empty store): fold every live **Global then Per-player** variable value (`Fixed.Raw`/int) and every **timer** remaining-tick count, each in ascending declaration/creation index; per-player outer loop follows `factions.ActiveFactions` ascending. Bump `AlgoVersion` 15→16 and re-baseline every moved golden + re-pin `SimChecksumCoverageGuardTest`'s pinned constant + the AlgoVersion consistency stamp **in the same commit**.
- Timers are **integer ticks only** — `SecondsToTicks` (64-bit intermediate) applied once at the create/declare boundary, no `float→int` truncation; `0 = inactive`; decremented in ascending creation index; `timer_expires` emitted on the tick a timer reaches 0 (byte-identical to the legacy path).
- `TriggerLocal` variables are lexically scoped: allocated when a trigger begins executing its actions and **freed at trigger end**; never engine-global; never persisted; **never folded** into `SimChecksum` (only Global + Per-player persist across ticks and fold).
- Legacy parity: a variable name referenced by `set_variable`/`variable_comparison` that is **not declared** resolves to a `Global`/`Int` slot defaulting to 0 (today's `GetVariable` semantics); the per-player ECA leaf selects the player slot via the action/condition `Faction` field (0..7). `create_timer`/`timer_expires` behave byte-identically after the hoist.
- Trigger execution walks the `TriggerGraph` directly in the total order (Priority desc, then ascending persistent node-id), built from `ScenarioData.trigger_graph` via `TriggerGraph.FromJson` when present, else `TriggerGraph.FromFlat(scenario.Triggers)`. `EffectActionNode` (`run_effect`) executes by delegating to the **existing** `EffectExecutor` — no second executor. Keep 7.2's fail-closed cycle guard.
- New `ScenarioData` fields (`variables`, `timers`, `trigger_graph`) use the omit-when-null array/string pattern (Regions/Items precedent) with empty→null normalization in the `ScenarioSerializer.Serialize` chokepoint, so a scenario without them serializes **byte-identically** to pre-7.3 (no scenario-bytes / `CanonicalModelHash` / `StartStateHash` movement).
- Any new node field or node kind is added to `NodeBaseJsonConverter`'s Read+Write allow-lists AND `NodeKinds` in lockstep, and to `ScenarioValidator`'s closed sets if it is a kind (the vocab stays hand-kept until 7.7 unifies it).

**Block If:**
- Adding `variables`/`timers`/`trigger_graph` to `ScenarioData` forces any `CanonicalModelHash` or `StartStateHash` baseline (or a scenario-bytes golden) to move — i.e. the declarations leaked into the multiplayer start-state handshake. HALT with status `blocked`, condition `variable/timer declarations leak into the multiplayer start-state handshake`. (The per-tick `SimChecksum` AlgoVersion 15→16 re-baseline is expected and is NOT a block.)
- Migrating the flat `set_variable`/`variable_comparison`/`create_timer`/`timer_expires` path onto the typed table, or switching execution to the graph walk, changes observable tick behavior for a legacy (all-Global-Int, flat-trigger) scenario beyond the intended checksum re-baseline (a determinism/parity test diverges). HALT with status `blocked`, condition `typed-table migration or graph-walk is not behavior-parity for legacy scenarios`.

**Never:**
- No authoritative load-time **graph validator** and no fail-closed rejection of malformed authored graphs (duplicate ids, dangling/forked exec/data edges, `EffectActionNode` mid-action-chain) — that is **Story 7.7**. Do not close the 7.2-deferred fail-open `ToFlat`/`FromJson` cases here; 7.3's only gates are the converter's fail-closed parse and 7.2's cycle guard.
- No **expression sublanguage** — no operator arithmetic/boolean evaluation over `Fixed`/`Bool`/`Point`, no typed read/write of non-`Int` values through the ECA leaves. That is **Story 7.4**. Non-`Int` types are declarable, storable, round-trippable, and foldable, but the 7.3 ECA leaf reads/writes `Int`-typed variables and `TimerRef` only.
- No **loops / ForEach / arrays population / fuel** — `Array` is a declarable/storable type slot only; loop counters ride the `TriggerLocal` scope later. That is **Story 7.6**.
- Do NOT fold variable/timer **declarations** into `CanonicalModelHash`/`StartStateHash` (the Triggers/Regions handshake gap stays deferred to 7.7/later).
- Do NOT add a second effect executor, a second trigger validator, or a `Validated<TriggerGraph>` minter. Do NOT build the T3 visual node editor (7.10) — the "raw-IR escape hatch" is a validated JSON text field, not a graph canvas.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Legacy variable parity | Scenario with `set_variable(v,5)` then `variable_comparison(v>=5)`, `v` undeclared | Executes identically to the flat path: `v` resolves to a `Global`/`Int` default-0 slot, comparison true after the set | No error |
| Typed declared round-trip | `ScenarioData.Variables=[{name,type:Fixed,scope:PerPlayer,initial:2.5}]`, `Timers=[{name,seconds:30}]` | Serialize→deserialize deep-equals original (`initial` preserved as `Fixed.Raw`); table initializes per-player slots to 2.5 and the timer to `SecondsToTicks(30)` ticks | No error |
| Timer parity | `create_timer(t,30s)` + `timer_expires(t)` | Timer set to `Math.Max(1,SecondsToTicks(30))` ticks, integer only; fires on the same tick as the legacy path | No error |
| SimChecksum fold determinism | Two headless runs of one seeded scenario+command stream with live vars/timers | Byte-identical `SimChecksum` sequence; `AlgoVersion==16` | Divergence ⇒ determinism test fails (must not) |
| Fold order | Vars declared across Global + multiple players | Fold visits Global slots then Per-player slots (ActiveFactions ascending) then timers, each in ascending declaration/creation index | No error |
| `run_effect` fires | Editor-authored trigger with an `EffectActionNode` (e.g. `damage`) that fires in a match | The embedded `EffectNode` runs via the existing `EffectExecutor`; observable sim effect applied | No error |
| Raw-IR reject | Raw-IR JSON with an unknown `kind` or a stray/duplicate property | Fail-closed parse; located converter error surfaced in the editor; no trigger added | Located `JsonException` message shown |
| Trigger-local lifecycle | A trigger writes a `TriggerLocal` var during its actions | Value readable within that trigger's actions; slot freed at trigger end; absent from the checksum fold | No error |
| Absent-declaration byte-identity | Existing scenario with no `variables`/`timers`/`trigger_graph` | Serializes byte-identically to pre-7.3; `CanonicalModelHash`/`StartStateHash` unchanged | Block-If if any moves |

</intent-contract>

## Code Map

- `godot/src/Dsl/NodeBase.cs` — node kinds + variable/timer-bearing fields (`ConditionNode.Variable/Value/Operator`, `ActionNode.Variable/Value`, `EventNode.TimerName`, `ActionNode.TimerName/TimerSeconds`); `EffectActionNode.Effect` (the `run_effect` embed to execute); `NodeKinds` closed registry (`NodeBase.cs:124-141`, hand-kept copy of the validator vocab).
- `godot/src/Dsl/TriggerGraph.cs` — `FromFlat` (`:52`), `ToFlat` (`:146`, has the fail-closed cycle guard), `ToCanonicalJson` (`:269`), `FromJson` (`:281`); port constants (`:26-38`). Source the direct graph walk here.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — closed-registry converter; Read/Write allow-lists + `RejectUnknownProperties` (`:289`). Update in lockstep with any new field/kind.
- `godot/src/Dsl/DslJson.cs` — the graph-IR `JsonSerializerOptions` (`FixedJsonConverter`, `NodeBaseJsonConverter`, `UnmappedMemberHandling.Disallow`). Used to (de)serialize `trigger_graph`.
- `godot/src/Core/ScenarioDirector.cs` — the tick: `Tick` (`:144`), `EvaluateTriggers` (`:243`), `ExecuteActions` (`:428`), `EvalCondition` (`:365`), `CollectEvents` timer decrement (`:197-215`); the ad-hoc stores to REMOVE: `_timerNames/_timerRemaining` (`:45-46`), `_variableNames/_variableValues` (`:48-49`), `SetVariable` (`:462`), `GetVariable` (`:454`), `SetTimer` (`:440`). KEEP `SecondsToTicks` (`:276`) at this Core boundary — it feeds integer ticks INTO `DslVarTable.TimerSet` (the table is Core-free and cannot reference `SimulationLoop.TICKS_PER_SECOND`). The `:38-44` comment reserves the 7.3 hoist; `:110` is where the graph is built.
- `godot/src/Core/SimChecksum.cs` — `Compute` (`:170-172`), `Mix` (`:482`), `AlgoVersion` (`:164`); ResearchStore fold `:445-466` and ResourceNodeStore fold `:417-431` are the templates for the new nullable-store block; RNG folds last (`:472-474`).
- `godot/src/Core/Sim/SimulationHost.cs` — owns the stores (`:59-94`), constructs them (`:132-147`), registers checksums (`:230`); owns `ScenarioDirector` (`:95`). Add `DslVarTable` ownership + wiring here.
- `godot/src/Core/SimulationLoop.cs` — `EnableChecksums` (`:82-84`) + the two `Compute` calls (`:128`, `:165`). Thread the new store through.
- `godot/src/Core/Definitions/ScenarioData.cs` — `Triggers` (`:458-459`); add `variables`/`timers`/`trigger_graph`. Regions/Items are the omit-when-null precedent.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — the serialize chokepoint with the swap-under-try/finally empty→null block (`:61-105`); options at `:23-29` (`FixedJsonConverter`). Normalize the new fields here.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — closed vocab sets (`:778-781`); the timer dangling-check block (`:385-421`). Add variable/timer declaration validation.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — Triggers excluded (`:18`), Regions excluded with the handshake note (`:27-33`). Keep the new declarations excluded on the same basis (add a matching note).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — the current AI-only editor: trigger list + enable/disable/delete + `RefreshList` (`:209`), accept path (`:338`). Extend with the manual form, variables section, and raw-IR hatch. Presentation layer (Godot).
- `godot/ProjectChimera.Sim.Tests/Golden/` — 25 `*.golden.txt` + paired `*GoldenTests.cs`; re-baseline recipe in `GoldenChecksumReplayTests.cs:27-29` (`CHIMERA_GOLDEN_RECORD=1`). `SimChecksumCoverageGuardTest.cs` (pinned-constant + reflection coverage) and `Meta/VersionStampConsistencyTests.cs` move too.
- `godot/src/Effects/EffectExecutor*` / `EffectNode.cs` — the existing effect runtime `run_effect` must reuse (no second executor).

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/DslValue.cs` (new) — Define `enum DslValueType { Int, Fixed, Bool, EntityRef, FactionRef, Point, TimerRef, Array }` and a Godot-free scalar value representation backed by `Fixed.Raw`/int (Point = two raws; Bool = 0/1; refs/timer = index/id int; Array = element type + scalar element storage, no population). -- the closed typed-value model.
- `godot/src/Dsl/DslVarTable.cs` (new) — The top-level sim store: dense SoA typed variable slots for `Global` and `PerPlayer[0..8)` (declaration-index ordered), a `TriggerLocal` scratch region (Enter/Exit allocate+free), and a dense timer sub-store (names + remaining-tick ints, creation-index ordered). API: init from declarations; `GetInt/SetInt(name, scope, faction)` with legacy-undeclared → Global/Int/default-0; `TimerSet`, `TimerTickAndCollectExpired`; trigger-local `Enter()/Exit()`; `FoldInto(ref uint hash)` folding Global then Per-player values then timers in declaration/creation index; `Clear()` for Edit↔Play reset. Godot-free, `Fixed`-based. -- replaces ScenarioDirector's ad-hoc lists.
- `godot/src/Core/SimChecksum.cs` -- Add trailing `DslVarTable? vars = null` to `Compute`; after the ResearchStore fold, before the RNG fold, add a guarded block (`vars is null` → `Mix(hash,0)`; else `vars.FoldInto(ref hash)`); bump `AlgoVersion` 15→16. -- folds live variable/timer state (behavior-neutral hash move).
- `godot/src/Core/SimulationLoop.cs` -- Add a nullable `DslVarTable` field, accept it in `EnableChecksums`, and pass it to both `Compute` calls (`:128`, `:165`). -- threads the store into the checksum.
- `godot/src/Core/Sim/SimulationHost.cs` -- Construct and own a `DslVarTable` (init from `ScenarioData` declarations); inject it into `ScenarioDirector`; register it via `EnableChecksums`; `Clear()` it on Edit↔Play reset alongside the other stores. -- store ownership + wiring.
- `godot/src/Core/ScenarioDirector.cs` -- Remove `_variableNames/_variableValues/_timerNames/_timerRemaining` + `SetVariable/GetVariable/SetTimer`; route all variable/timer access through the injected `DslVarTable`. Build the execution `TriggerGraph` (FromJson(`trigger_graph`) if present else FromFlat(Triggers)) and **walk it directly** in the total order, superseding the `ToFlat()` lowering. Dispatch `set_variable`/`variable_comparison` (Int, per-player via `Faction`, undeclared → Global default-0) and `create_timer`/`timer_expires` onto the table; execute `EffectActionNode` via the existing `EffectExecutor`; open a trigger-local scope per firing trigger and free it at trigger end; keep the cycle guard. -- the verify-to-ship ECA execution core.
- `godot/src/Core/Definitions/ScenarioData.cs` -- Add `[JsonPropertyName("variables")] ScenarioVariable[]? Variables`, `[JsonPropertyName("timers")] ScenarioTimer[]? Timers`, `[JsonPropertyName("trigger_graph")] string? TriggerGraphJson`, all `WhenWritingNull`. New POCOs `ScenarioVariable { name; DslValueType type; VarScope scope; Fixed initial }` and `ScenarioTimer { name; Fixed seconds }` with `[JsonPropertyName]` per field. -- the declaration schema.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` -- In the serialize chokepoint, normalize empty `Variables`/`Timers` arrays and empty/whitespace `TriggerGraphJson` to null under the existing swap-under-try/finally block, so absent-declaration scenarios serialize byte-identically. -- byte-identity guarantee (Block-If protection).
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- Validate declarations: unique variable names; `type`/`scope` within the closed sets; a `set_variable`/`variable_comparison` variable must resolve to a declared var OR fall through to the legacy Global-Int default (documented); keep the existing dangling-timer check and extend it to declared `timers`. Return located errors. -- declaration-time gate (not the 7.7 graph validator).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- Add `variables`/`timers`/`trigger_graph` to the excluded set with a note mirroring the Triggers/Regions deferral ("fold into the handshake with Triggers/Regions in 7.7/later"). -- keeps declarations out of the MP handshake.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` -- Add (a) a manual "New Trigger" preset form: name/enabled/run-once + add event/condition/action rows from closed-vocab dropdowns incl. a variable read/write leaf referencing declared variables and an embed-effect (`run_effect`) action; (b) a Variables section to declare name/type/scope/initial (persisted to `ScenarioData.Variables`); (c) a raw-IR escape hatch: a JSON text field showing the trigger's canonical IR, parsed on Accept via `TriggerGraph.FromJson`/`NodeBaseJsonConverter` (fail-closed; surface the located error; persist graph-only triggers to `ScenarioData.TriggerGraphJson`). Keep the AI path. -- layered-complexity FR-23 editor.
- `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` (+ regen) -- Re-baseline every moved golden via `CHIMERA_GOLDEN_RECORD=1`; re-pin `SimChecksumCoverageGuardTest`'s pinned constant; update the AlgoVersion consistency stamp. -- records the required behavior-neutral hash move.
- `godot/ProjectChimera.Sim.Tests/Dsl/DslVarTableTests.cs` (new) -- Typed/scoped get/set; declaration-index fold determinism; trigger-local allocate/free (and absence from the fold); timer integer-tick decrement/expiry; `Clear`. -- store unit coverage.
- `godot/ProjectChimera.Sim.Tests/Dsl/DslMigrationParityTests.cs` (new) -- A legacy scenario using `set_variable`/`variable_comparison`/`create_timer`/`timer_expires` produces byte-identical observable tick behavior through the typed table + graph walk (drives `ScenarioDirector`); covers the parity I/O rows. -- Block-If regression net.
- `godot/ProjectChimera.Sim.Tests/Definitions/ScenarioVariableRoundTripTests.cs` (new) -- `Variables`/`Timers`/`trigger_graph` round-trip (Fixed.Raw preserved); a scenario without them serializes byte-identically to a pre-7.3 fixture. -- schema + byte-identity coverage.
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphExecutionTests.cs` (new) -- Graph-walk execution parity vs the old flat walk; an `EffectActionNode` fires via `EffectExecutor`; a variable read/write leaf mutates/reads the table; a per-player variable folds distinctly per faction. -- graph-execution + run_effect coverage.
- `godot/ProjectChimera.Sim.Tests/.../SimChecksumCoverageGuardTest.cs` -- Extend so the coverage guard asserts every `DslVarTable` Global/Per-player value and every timer is folded (differential-mutation style). -- prevents silent under-folding.

**Acceptance Criteria:**
- Given a scenario declaring typed scoped variables (Int/Fixed/... across Global and Per-player) and named timers, when it loads and ticks, then `DslVarTable` is a top-level `SimulationHost` store initialized from the declarations and `SimChecksum` (AlgoVersion 16) folds every live Global then Per-player value and every timer's remaining ticks in declaration/creation-index order, and two headless runs of the same seeded scenario+command stream produce a byte-identical `SimChecksum` sequence.
- Given a variable declaration `{name,type,scope,initial}`, when the scenario is serialized then deserialized, then it deep-equals the original with `initial` preserved as `Fixed.Raw`; and a scenario with no `variables`/`timers`/`trigger_graph` serializes byte-identically to pre-7.3, with `CanonicalModelHash` and `StartStateHash` unchanged.
- Given a legacy scenario using `set_variable`/`variable_comparison`/`create_timer`/`timer_expires` (untyped-global, timers in seconds), when migrated onto the typed table and graph walk, then observable tick behavior is identical to the flat path (parity tests green; timers integer-tick with no float→int truncation), and the golden suite is green after the recorded AlgoVersion 15→16 re-baseline.
- Given the Trigger Editor, when a creator adds an ECA trigger via the simple preset form whose actions embed a D1 effect subgraph (`run_effect`) and read/write a declared variable, then the trigger persists into the graph IR (`ScenarioData.trigger_graph`) and, in a running match, the embedded effect fires via the existing `EffectExecutor` and the variable read/write hits the `DslVarTable`.
- Given the raw-IR escape hatch, when a creator edits a trigger's canonical IR JSON and accepts, then well-formed IR persists and executes, while malformed IR (unknown kind / stray or duplicate property) is rejected fail-closed with the located converter error surfaced and no partial trigger added.
- Given trigger-local variables written during a trigger's actions, when the trigger finishes, then those slots are freed — never engine-global, never persisted, never folded into `SimChecksum`.
- Given `dotnet build`/`dotnet test`, then `src/Dsl` compiles Godot-free and float-free with no new warnings, `NodeBaseJsonConverter` allow-lists + `NodeKinds` are updated for any new field/kind, and no authoritative graph validator (7.7), expression layer (7.4), or loops (7.6) were added.

## Spec Change Log

## Review Triage Log

### 2026-07-16 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 7: (high 1, medium 4, low 2)
- defer: 4
- reject: 2
- addressed_findings:
  - `[high]` `[patch]` The `trigger_graph` channel silently superseded/discarded ALL flat `Triggers` (`LoadScenario` used graph XOR flat), and the editor overwrote the graph channel wholesale — so mixing flat + `run_effect` authoring dropped every flat trigger at runtime (silent whole-feature loss). Fixed: added `TriggerGraph.Merge` + `BuildRunEffectTrigger`; `LoadScenario` now merges `FromFlat(Triggers)` with `FromJson(TriggerGraphJson)` (id-offset union, global Priority-desc/id-asc order preserved); `PersistManualRunEffect` accumulates into the existing graph. Tests: both-channels-execute, two-run_effect-merge-preserves-both, extracted-helper-fires. Corroborated by blind + edge-case + verification-gap reviewers.
  - `[medium]` `[patch]` Per-player variable writes to inactive faction slots escaped the `SimChecksum` fold (`FoldInto` folded only `ActiveFactions`, but `SetInt` writes any slot 0..7) — a silent-desync hole in exactly the state class this story hardens; and the `null` vs empty `DslVarTable` folds differed (1 vs 2 `Mix(0)`), making the 9-arg overload inconsistent. Fixed: `FoldInto` folds all 8 `PlayerSlots` per declared per-player var; `null` branch calls `DslVarTable.FoldEmpty` (byte-identical to an empty table). Coverage-guard teeth extended (inactive-slot write folds; null≡empty). Pinned v16 hash unchanged. Blind + edge-case reviewers.
  - `[medium]` `[patch]` `TriggerGraphJson` bypassed the pre-tick `ScenarioValidator` gate: malformed IR passed validation and threw mid-apply (after items/regions applied — partial-apply crash on a "validated" scenario), and `run_effect` subgraphs skipped the load-time `EffectBounds.Validate` every other effect source gets. Fixed: the validator now parses `trigger_graph` (located error on failure) and runs `EffectBounds.Validate` on every `EffectActionNode.Effect`. Tests: malformed rejected, over-cap rejected, well-formed accepted. (Deep structural graph validation stays deferred to 7.7.) Blind + edge-case reviewers.
  - `[medium]` `[patch]` `variable_comparison` on a `TriggerLocal`-scoped variable always read 0 (conditions evaluated before the trigger-local scope is entered). Fixed: validator rejects a condition referencing a TriggerLocal var; editor condition picker excludes TriggerLocal (still available to action leaves). Validator test added. Edge-case reviewer.
  - `[medium]` `[patch]` The new `ScenarioValidator` declaration rules (blank/duplicate/null variable+timer names; declared-timer seeding of the dangling-`timer_expires` check) were entirely unverified. Fixed: added reject tests (blank/duplicate var, blank timer) and a positive test (a `timer_expires` naming a declared `ScenarioTimer` passes). Verification-gap reviewer.
  - `[low]` `[patch]` The editor's Int-only `set_variable`/`variable_comparison` leaves exposed Fixed/Point-typed vars (operated on raw `Fixed.Raw` — a 7.4-scope leak) and faction slots aliased silently out of range. Fixed: editor pickers list only Int-typed vars; validator rejects non-Int referents and out-of-range variable-leaf faction. Tests added. Edge-case reviewer.
  - `[low]` `[patch]` Removed dead `_prevBuildingAlive` state (written/cleared but never read after the graph-walk refactor). Blind-hunter reviewer.

_Deferred (4 → deferred-work.md): SpatialHash rebuild per `run_effect` invocation (perf); `RunEffect` throws on null `ModifierStore` off the `SimulationHost` path (fragility); Point `Raw1`/Z folded but unpopulated until 7.6 (extend fold teeth then); manual ECA form covers only variable/message/run_effect, not the full closed action vocab (FR-23 breadth / 7.10). Rejected (2): `DslMigrationParityTests` naming is aspirational but the test is adequate; store-level cross-scope duplicate-name defensiveness (the validator already rejects duplicate names on the real path)._

### 2026-07-16 — Review pass (follow-up, independent)
- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 0, medium 5, low 6)
- defer: 0
- reject: 12
- addressed_findings:
  - `[medium]` `[patch]` A CYCLIC authored `trigger_graph` passed the pre-tick gate (which only parsed) then threw `JsonException` inside `LoadScenario` — the exact partial-apply crash the gate was added to close. Fixed: the gate now also runs `BuildExecutionOrder` (7.2's fail-closed cycle guard) at validation; catch widened to `NotSupportedException` (STJ can surface it on hostile input) in the validator and both editor accept paths; `TriggerGraph.FromJson` rejects negative node ids (parse-level sanity protecting `Merge`'s id-offset union). Tests: cyclic graph → located validation error; negative id rejected. Blind + edge-case + verification-gap reviewers.
  - `[medium]` `[patch]` The graph channel escaped every semantic rule the flat channel gets: a graph `ConditionNode` could read a TriggerLocal/Fixed-typed variable (silent raw-as-int comparison), any node could carry an engine-ceiling-crashing faction, and the dangling-timer analysis was single-channel (a flat `timer_expires` referencing a graph `create_timer` false-flagged; graph `timer_expires` unchecked). Fixed: the gate (hoisted above Pass 1) applies `CheckFactionSlot`, the P4/P5 variable-leaf rules, and a cross-channel timer union to every graph node. 5 tests. Blind + edge-case reviewers.
  - `[medium]` `[patch]` Both production host wirings were invisible to the suite — deleting `, Vars` from `EnableChecksums` (null≡empty masks it) or the `SetEffectRuntime` call compiled and passed every test while silently reopening the desync hole / arming an in-match crash; the handshake-exclusion contract also lacked the Regions-precedent teeth. Fixed: new `DslVarWiringTests` (loop-emitted checksum moves on variable write and timer countdown; two-run determinism at host altitude; trigger-embedded `apply_modifier` lands in `host.Modifiers`) + new `CanonicalModelHashDeclarationExclusionTests` (with/without equality on both hashes + AlgoVersion pins). Verification-gap + intent-alignment reviewers.
  - `[medium]` `[patch]` The manual form silently DISCARDED the chosen condition when the action was `run_effect` — the effect fired unconditionally against the authored logic (and the spec's manual check "trigger gated on that variable embedding an effect" was unauthorable). Fixed: `BuildRunEffectTrigger` takes an optional condition (ConditionNode id 3, data-wired); the form passes it through; empty variable pickers now refuse with feedback instead of persisting inert `Variable=""` triggers. Gated-both-ways test. Edge-case reviewer.
  - `[medium]` `[patch]` Determinism-audit docs contradicted the shipped v16 fold: `SimChecksum`'s v16 block and `VersionStampConsistencyTests` said "per ACTIVE faction" + "a single Mix(0)" (the pre-P2 letter; actual: all 8 slots, `FoldEmpty` = two mixes), `ScenarioData.TriggerGraphJson` said "supersedes" (actual: merge), `DslVarTable`'s own scope doc said active-faction fold, two test pins carried stale "(14→15) Story 6.3" comments, and `CanonicalModelHash` overpromised "different declared initials caught at tick 1" (values/counts only, not structure). All corrected to describe the implemented layout. Blind + intent-alignment reviewers.
  - `[low]` `[patch]` The two `>= DslVarTable.PlayerSlots` validator checks were unreachable dead code (`CheckFactionSlot`'s engine ceiling [0,3] runs first and rejects everything they would), and their covering test passed via the other check. Fixed: removed; comments + test attribute the real gate; added a `DslVarTable.PlayerSlots == FactionRegistry.PLAYER_COUNT` pin (hand-kept copy across the Dsl→Core boundary). Blind reviewer.
  - `[low]` `[patch]` Declaration well-formedness gaps: declared `ScenarioTimer.Seconds <= 0` silently became a 1-tick timer (more permissive than the `create_timer` action); an Int-typed `initial` of 2.5 silently truncated to 2; a Bool initial of 7 stored as 7. Fixed: validator rejects all three, located. Tests incl. negative-whole-Int stays legal. Blind + edge-case reviewers.
  - `[low]` `[patch]` `Tick`'s zero-triggers early-out froze declared timers forever (trigger-less timers only became representable in 7.3). Fixed: timers decrement before the early return (no-op for the empty legacy table — goldens unmoved). Test. Blind + edge-case + verification-gap reviewers.
  - `[low]` `[patch]` `DslVarTable.SetInt` on a TriggerLocal-declared name OUTSIDE a scope violated its own documented no-op contract — it fell through and minted a phantom folded Global slot; `GetInt` could similarly fall through. Fixed: TL names now always resolve to the TL slot (no-op write / 0 read outside a scope). Test. Edge-case reviewer.
  - `[low]` `[patch]` Editor vars-section silent failures: duplicate declarations accepted (whole scenario later failed validation with an error the panel never showed) and unparseable initial text silently became 0; an open raw-IR section's stale text could overwrite (drop) a trigger persisted after it was opened. Fixed: duplicate/parse guards with a new status label; raw-IR text resyncs on graph-channel persist. Blind + edge-case reviewers.
  - `[low]` `[patch]` Removed `DslVarTableTests.Fold`'s vestigial `params int[] _` (fossilized mid-review churn in a file this story introduced — made `Fold(a, 0)` look meaningful). Blind reviewer.

_Deferred (0 new — per orchestrator instruction, existing ledger entries untouched). Rejected (12): duplicate-node-id + forked-exec-edge structural rejection (explicitly 7.7 by the intent's Never list); SpatialHash rebuild per run_effect, manual-form action breadth/Priority, and Point Raw1 authoring (already in the ledger from pass 1); fold covers values not declaration structure (the documented 7.7 handshake deferral — doc wording tightened instead); trigger_graph parsed twice per load (negligible load-time cost); run_effect null-`Effect` NRE claim (disproven: converter requires `effect`, runtime guards null); lowest-id run_effect anchor semantics (documented residual, 7.13); editor panel not runtime-verified + `float.TryParse` at the UI boundary (documented residual; sanctioned quantization boundary); round-trip test bypassing production deserialize options (reviewer verified converter equivalence); 4-vs-8 player ceiling tension (pre-existing engine ceiling tracked for Story 9.2, now pinned by the PlayerSlots test)._

## Design Notes

- **Why every golden moves (and why that's behavior-neutral):** the fold adds a `Mix(hash, count)` step to `SimChecksum.Compute` even when the table is empty (`count==0`), so all 25 baselines change — this is the epic-mandated "named, recorded golden re-baseline," not a behavior change. Behavior parity is proven by the migration/execution unit tests, because the goldens themselves carry empty trigger/variable state (their `ScenarioDirector.Tick` early-returns). Re-baseline via `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter ~Golden`, then `dotnet build` (refreshes embedded copies), then commit the `.golden.txt` with the AlgoVersion bump.
- **Handshake deferral (the landmine):** variable/timer **declarations** stay out of `CanonicalModelHash`/`StartStateHash` on the same basis Triggers/Regions already are — a peer that loaded different declared initials would be caught at tick 1 by `SimChecksum`, and the authoritative handshake fold is 7.7/later. This is deliberately the safe side of the `CanonicalModelHash` TerrainRef determinism landmine: only *live per-tick* state folds now; nothing new feeds the pre-match handshake, so existing scenario-bytes goldens do not move.
- **Graph walk supersedes the 7.2 waypoint:** 7.2 left the tick executing the flat `ToFlat()` lowering; 7.3 walks the `TriggerGraph` directly because `run_effect` is graph-only (it has no flat representation — `ToFlat` truncates it). `FromFlat` is lossless and the walk visits nodes in the same logical order (events→trigger→action chain; conditions gate), so legacy flat scenarios execute identically. Malformed *authored* graphs (dangling/forked/duplicate-id/mid-chain effect) are the 7.7 validator's job; 7.3 relies on `FromFlat`/editor well-formedness plus the converter's fail-closed parse and 7.2's cycle guard.
- **Scope/type addressing:** the ECA leaf is Int-only for reads/writes (per-player selected by the `Faction` field; undeclared name → Global/Int/default-0 for parity). The full typed read/write through operators is the 7.4 expression layer; `Array`/loops are 7.6. Example flat→graph mapping unchanged from 7.2; the new work is the value store + graph execution + editor, not the IR shape.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds; `src/Dsl` compiles Godot-free/float-free; no new warnings; no `[JsonPolymorphic]`/reflection polymorphism.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green including the new `Dsl/*` + `Definitions/*` tests and the re-baselined `*Golden*`/coverage-guard/version-stamp tests; `AlgoVersion==16` consistent.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then `dotnet build` -- expected: regenerates the moved baselines (recipe in `GoldenChecksumReplayTests.cs:27-29`); `git add` the changed `.golden.txt`.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` after a normal (non-record) test run -- expected: empty (baselines already committed at v16; none drifting).
- `grep -rniE "using Godot|[^.]\\bfloat\\b|double " godot/src/Dsl` -- expected: no Godot import and no float/double in the new sim types.
- Determinism: run one seeded scenario with live variables+timers twice headless and assert an identical `SimChecksum` sequence.

**Manual checks (in-engine, via godot-verify):**
- In the Trigger Editor, declare a variable and author a trigger that embeds an effect and is gated on that variable; run the match and observe the embedded effect fires and the variable-gated action triggers; paste malformed raw-IR and confirm a located rejection with no trigger added.

## Auto Run Result

Status: done

### Summary
Hoisted trigger variables + timers into one typed, scoped, checksummed sim store (`DslVarTable`, sibling of the other `SimulationHost` stores) with the closed value-type set (Int/Fixed/Bool/EntityRef/FactionRef/Point/TimerRef/Array) and Global / Per-player(0..7) / Trigger-local scopes; folded its live Global + all-per-player-slot values and integer-tick timers into `SimChecksum` (AlgoVersion 15→16, recorded golden re-baseline). Declared variables/timers/`trigger_graph` in `ScenarioData` (round-tripping name/type/scope/initial as `Fixed.Raw`), excluded from the multiplayer start-state handshake exactly as Triggers/Regions are. Rewired `ScenarioDirector` to walk the graph IR directly (superseding 7.2's flat lowering) so `run_effect` fires via the existing `EffectExecutor` and variable/timer leaves hit the table, with byte-identical behavior for legacy flat scenarios. Extended `TriggerEditorPanel` with a manual ECA preset form, a variable-declaration section, and a fail-closed raw-IR escape hatch. Review hardened the flat/graph channel coexistence (merge, no silent loss), the per-player checksum coverage, the pre-tick validation of `trigger_graph`, and trigger-local/Int-leaf scoping.

### Files changed (source)
- `godot/src/Dsl/DslValue.cs` (new) — closed `DslValueType`/`VarScope` + declaration structs.
- `godot/src/Dsl/DslVarTable.cs` (new) — typed/scoped dense-SoA variable + integer-tick timer store; `FoldInto` (all 8 per-player slots) + `FoldEmpty` (null≡empty); trigger-local Enter/Exit; `Clear`.
- `godot/src/Dsl/TriggerGraph.cs` — `BuildExecutionOrder` (graph walk), `Merge` (id-offset union), `BuildRunEffectTrigger` (Godot-free testable helper).
- `godot/src/Core/SimChecksum.cs` — AlgoVersion 15→16; folds `DslVarTable` (Global → all per-player slots → timers) between ResearchStore and RNG; null≡empty.
- `godot/src/Core/SimulationLoop.cs` / `Sim/SimulationHost.cs` — own + thread the store through `EnableChecksums`/`Compute`; wire `EffectExecutor`; `Clear` on reset.
- `godot/src/Core/ScenarioDirector.cs` — graph-walk executor; merges flat + `trigger_graph`; variable/timer access via `DslVarTable`; `run_effect` via `EffectExecutor`; trigger-local scope per firing; removed the ad-hoc lists + dead `_prevBuildingAlive`.
- `godot/src/Core/Definitions/ScenarioData.cs` / `ScenarioSerializer.cs` — `ScenarioVariable`/`ScenarioTimer` + omit-when-null `Variables`/`Timers`/`TriggerGraphJson` with empty→null byte-identity.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — declaration validation (unique/non-empty, type/scope/faction bounds, TriggerLocal-not-in-condition, Int-only leaves); parses + `EffectBounds.Validate`s `trigger_graph` at the gate.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — new declarations documented handshake-excluded (Triggers/Regions basis; no fold).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — manual ECA preset form, variables section, accumulating raw-IR/run_effect graph persistence, Int-only + non-TriggerLocal-condition pickers.
- Tests: new `Dsl/DslVarTableTests`, `Dsl/DslMigrationParityTests`, `Dsl/TriggerGraphExecutionTests`, `Definitions/ScenarioVariableRoundTripTests`; extended `Validation/TriggerValidationTests`, `Golden/SimChecksumCoverageGuardTest`, `Meta/VersionStampConsistencyTests`; retargeted `Golden/TimerDeterminismTests` + `Dsl/TriggerGraphLiveLoweringTests`; 24 `Golden/*.golden.txt` re-baselined at v16; constructor/AlgoVersion pin updates across director-driving tests.

### Review findings breakdown
- **Patches applied (7):** [high] flat+graph channel merge (no silent trigger loss, sim + editor); [medium] per-player fold covers all 8 slots + null≡empty; [medium] `trigger_graph` parse + `EffectBounds.Validate` at the pre-tick gate (no partial-apply crash / silent truncation); [medium] TriggerLocal not readable in conditions; [medium] validator declaration-rule tests; [low] Int-only leaves + faction/type bounds; [low] removed dead `_prevBuildingAlive`.
- **Deferred (4 → deferred-work.md):** SpatialHash rebuild per `run_effect` (perf); `RunEffect` null-`ModifierStore` fragility off the host path; Point `Raw1`/Z folded-but-unpopulated (extend fold teeth at 7.6); manual ECA form limited to variable/message/run_effect vocab (FR-23 breadth / 7.10).
- **Rejected (2):** `DslMigrationParityTests` aspirational naming (test adequate); store-level cross-scope dedup (validator covers the real path).

### Verification performed
- `dotnet build godot/godot.sln` → **0 errors, 0 warnings**.
- `dotnet test godot/ProjectChimera.Sim.Tests` → **1849 passed, 1 skipped** (pre-existing SimRng reservation), **0 failed** (+14 tests over the pre-review 1835).
- Golden baselines byte-identical before and after a normal (non-record) test run → **no drift**; the pinned `KnownWorldState_ProducesPinnedV16Hash` unchanged (empty-table fold byte-identical); AlgoVersion 16 consistent.
- `grep` over `godot/src/Dsl` → no `using Godot`, no `float`/`double` in code; no `[JsonPolymorphic]`.
- Both Block-If tripwires clear: `CanonicalModelHash`/`StartStateHash` did not move (declarations handshake-excluded; absent-declaration byte-identity green), and legacy parity held (migration/execution tests green).
- All 9 I/O-matrix rows covered by tests that ran and passed; review patches added coverage (channel merge, all-slot fold, `trigger_graph` gate, declaration rules) without regressing any row.

### Follow-up review
`followup_review_recommended: true` — the review pass made significant, behavior-changing fixes: a HIGH silent-data-loss correction spanning both the sim loader (flat/graph merge) and the editor persistence, a determinism-relevant `SimChecksum` fold-coverage change, and a new pre-tick validation gate for `trigger_graph`. Their consequence, breadth, and determinism sensitivity warrant an independent follow-up pass.

### Residual risks
- The Trigger Editor surfaces (`TriggerEditorPanel.cs`, Godot presentation) are not runtime-verified in-engine (no live editor session); their authoring logic is covered indirectly via the extracted Godot-free `BuildRunEffectTrigger` helper + sim-layer execution tests, but the manual in-engine check (declare var → author embedded-effect trigger → run match → observe firing) remains outstanding.
- `run_effect` targeting uses a deterministic interim anchor (lowest-id alive entity as caster) — target parameterization is deferred to Story 7.13; an area/self effect only produces an observable result when an anchor entity exists.
- Deep structural validation of authored graphs (duplicate ids / dangling / forked edges / mid-chain `EffectActionNode`) remains deferred to Story 7.7's authoritative validator; 7.3's gate adds parse + the 7.2 cycle guard + effect-bounds + per-leaf semantic checks, fail-closed.
- New `.cs` files ship without Godot `.cs.uid` companions (the editor generates them on next open), consistent with prior story commits.

## Follow-up Review Pass (2026-07-16)

Status: done

### Summary
Independent follow-up review (4 parallel layers: adversarial, edge-case, verification-gap, intent-alignment) over the full 7.3 diff. No intent gaps, no spec defects; 11 patches applied (5 medium, 6 low), all hardening/teeth/doc-truth — no checksum movement, no golden re-baseline, both Block-If tripwires still clear. Key fixes: the `trigger_graph` gate now runs the 7.2 cycle guard at validation (closing the remaining accept-then-crash-mid-apply hole) and applies the flat channel's full semantic rulebook (faction ceiling, TriggerLocal/Int variable-leaf rules, cross-channel timer union) to graph nodes; production host wirings (`EnableChecksums(..., Vars)`, `SetEffectRuntime`) and the handshake-exclusion contract gained regression teeth that turn RED if deleted; the manual form's condition now rides into run_effect graphs instead of being silently discarded; trigger-less declared timers decrement; declaration well-formedness (timer seconds > 0, whole Int / binary Bool initials) is validated; determinism-audit doc comments were corrected to describe the shipped v16 fold (all-8-slots, FoldEmpty two-mix, merge-not-supersede).

### Files changed (this pass)
- `godot/src/Core/Definitions/ScenarioValidator.cs` — trigger_graph gate hoisted above Pass 1 (+cycle guard, +NotSupportedException); graph-node semantic checks; cross-channel timer union; timer-seconds/Int-whole/Bool-binary declaration rules; removed the two unreachable PlayerSlots checks.
- `godot/src/Dsl/TriggerGraph.cs` — `FromJson` rejects negative node ids; `BuildRunEffectTrigger` optional gating condition (ConditionNode id 3).
- `godot/src/Dsl/DslVarTable.cs` — TriggerLocal names never fall through to Global (documented no-op honored); scope-fold doc corrected.
- `godot/src/Core/ScenarioDirector.cs` — timers decrement in the zero-triggers early-out path.
- `godot/src/Core/SimChecksum.cs` / `ScenarioData.cs` / `CanonicalModelHash.cs` — v16 fold layout, merge-not-supersede, and values-vs-structure doc corrections (comment-only).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — run_effect condition pass-through; empty-picker/duplicate-name/bad-initial guards with feedback; raw-IR text resync on graph persist; NotSupportedException fail-closed catches.
- Tests: new `Sim/DslVarWiringTests` (4: loop-checksum wiring, host-altitude determinism, timer fold, apply_modifier sink wiring) + `Validation/CanonicalModelHashDeclarationExclusionTests` (5); +10 `TriggerValidationTests`; +1 `TriggerGraphExecutionTests` (gated run_effect); +1 `DslMigrationParityTests` (trigger-less timer); `DslVarTableTests` TL-no-op + PlayerSlots pin, vestigial param removed; stale version-pin comments fixed in `HeroProfilePersistenceTests`/`SimResetTests`/`VersionStampConsistencyTests`.

### Review findings breakdown
- **Patches applied (11):** 5 medium (cycle-guard-at-gate + parse hardening; graph-channel semantic parity; host-wiring + handshake-exclusion regression teeth; run_effect condition wiring + inert-trigger guards; determinism-doc truth), 6 low (dead-check removal + PlayerSlots pin; declaration well-formedness; trigger-less timer decrement; TriggerLocal no-op contract; editor vars UX guards + raw-IR resync; vestigial test param).
- **Deferred (0):** no new ledger entries; existing entries untouched per orchestrator instruction.
- **Rejected (12):** structural dup-id/forked-edge rejection (7.7 by intent); three pass-1-ledgered items; values-vs-structure fold gap (7.7 handshake deferral, doc tightened); double parse (negligible); null-Effect NRE (disproven); anchor semantics + editor runtime verification (documented residuals); round-trip options divergence (verified equivalent); 4-vs-8 ceiling (pre-existing, Story 9.2, now pinned).

### Verification performed
- `dotnet build godot/godot.sln` → **0 errors** (11 pre-existing CS8632/CS8604 warnings in untouched files; none in `src/Dsl`).
- `dotnet test godot/ProjectChimera.Sim.Tests` → **1872 passed, 1 skipped** (pre-existing AR-13 reservation), **0 failed** (+23 tests over pass 1's 1849).
- Golden baselines: `git diff` over `Golden/*.golden.txt` after the normal (non-record) run → **empty** (no drift; no re-baseline needed — this pass moved no hash).
- `grep` over `godot/src/Dsl` → no `using Godot`, no `float`/`double`.
- Block-If tripwires: `CanonicalModelHash`/`StartStateHash` untouched and now pinned by the new exclusion tests; legacy parity suite green throughout.

### Follow-up review
`followup_review_recommended: false` — this pass's changes are fail-closed validation tightening, regression teeth, editor guards, and doc corrections: no high-severity findings, no checksum/golden movement, no determinism-relevant state change; each behavior change is narrow and directly test-covered.

### Residual risks (delta)
- Unchanged from pass 1 (editor in-engine verification outstanding; run_effect anchor interim semantics; 7.7 structural validation deferral — now narrowed by the cycle-guard-at-gate and id-sanity checks).
- The engine faction ceiling ([0,3] via `CheckFactionSlot`) vs the 8-slot DSL table remains an intentional as-built-vs-forward-architecture gap (Story 9.2 raises the ceiling); the new PlayerSlots pin test will flag any one-sided move.
