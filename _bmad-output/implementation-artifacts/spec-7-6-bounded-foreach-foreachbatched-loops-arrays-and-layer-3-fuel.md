---
title: 'Story 7.6: Bounded ForEach / ForEachBatched loops, arrays, and Layer-3 fuel'
type: 'feature'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 0
baseline_revision: 'e94fb331fbf338106d2afaa71098ea397aa680e7'
final_revision: '76808c001981765968cfaa0bb0272ee52cc979ac'
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The trigger DSL has no collections and no iteration: `DslValueType.Array` is a declarable-but-inert slot (`DslValue.cs:14`), the only exec flow is a linear Trigger→Action chain, and there is no conditional branch. Creators cannot express TD waves, autochess pools, or AoE-over-units logic (FR-24's collections+loops half). There is also no runtime cost seatbelt — and the as-built `Math.Min(a.Count, 50)` literal spawn clamp (`ScenarioDirector.cs:649`) silently truncates instead of failing loud.

**Approach:** Extend the graph IR with the only sanctioned iteration forms and a fuel seatbelt: exec-container node kinds `for_each` / `for_each_batched` / `branch`, populated `Array<scalar>` variables in `DslVarTable` (with `array_push`/`array_set`/`array_clear` actions and `arr[i]`/`length(arr)` expression reads), and a per-tick `MaxDslOpsPerTick` fuel counter plus `for_each_batched` cross-tick continuation state, both folded into `SimChecksum` (`AlgoVersion` 16→17, one named golden re-baseline). Loops iterate a collection snapshotted at loop entry in ascending order (the `SearchAreaEffect.cs:55-85` sort-snapshot pattern); nesting cost is rejected AT LOAD by a static cap-product check against `MaxDslOpsPerTrigger`. Story 7.5 (custom events) was re-sequenced AFTER this story by the orchestrator: the batched drip uses a self-contained checksummed continuation store, NOT a next-tick event queue.

## Resume Directive (escalation resolution, 2026-07-16 — BINDING)

Two prior dev attempts implemented this story to near-completion and both hit the session timeout during VERIFICATION (the second died while mutation-testing the coverage guard, after the suites were already running). The work is preserved. **Re-implementing from scratch is FORBIDDEN — it is the exact failure mode that prevented convergence.** On session start, after the version-control sanity check:

1. **Restore the preserved attempt before writing any code.** Pick the `refs/attempt-preserve-dirty/20260716-100752-2040-e94fb331-*` ref with the HIGHEST numeric suffix whose tree contains ALL of `godot/src/Dsl/DslBounds.cs`, `godot/src/Dsl/DslLoopState.cs`, and `godot/ProjectChimera.Sim.Tests/Dsl/DslFuelTests.cs` (verify with `git ls-tree -r --name-only <ref>`). Restore ONLY the `godot/` subtree: `git restore --source=<ref> --worktree -- godot/` — NEVER restore `_bmad-output/` from the ref (it would clobber this corrected spec).
2. **A dirty tree confined to `godot/` (plus this story's `_bmad-output` artifacts and the pre-existing 3-line `deferred-work.md` delta) is expected in-progress work for THIS story, not a blocker** — do not halt on it, do not roll it back.
3. If (and only if) no ref satisfies the step-1 predicate, fall back to a clean implementation per this spec.
4. **Resume at VERIFICATION, not design:** run the Verification commands below, fix only what fails, and prioritize reaching a committed green state over re-deriving or "improving" the restored implementation. Budget the session back-to-front: commit-worthy green suite first, optional polish never.

## Boundaries & Constraints

**Always:**
- All new sim/DSL code is Godot-free and float-free; no `Dictionary`/`HashSet` enumeration in any execution or fold path; ascending-id / declaration-index order everywhere; zero per-tick heap allocation in the loop executor (snapshot buffers, iteration frames, and continuation rows are allocated at load — the `EffectExecutor` preallocated-stack pattern).
- Closed grammar: only `for_each`, `for_each_batched`, `branch` exec containers and `expr_array_get`/`expr_array_len` expr kinds are added. No While/recursion/goto form exists in the grammar (cannot be expressed); exec cycles — including body/then/else chains rejoining any ancestor — are located rejects (extend the existing `BuildExecutionOrder` cycle guard).
- `for_each`: closed `Source` set `array | faction_units | region_units`. Snapshot at loop entry: array → copy elements to the node's preallocated buffer; entity sources → ascending-id scan of alive units (faction filter; `region_units` also `RegionStore.Contains`, `Faction=-1` = any faction). Iterates `min(snapshotCount, UpTo)` — `UpTo` is the loud authored cap (`ForEachUpTo`). `LoopVar` (optional for entity sources, required for `array`) names a declared **TriggerLocal** variable: element value (type must equal element type) or entity id (Int). Body actions execute per iteration; `run_effect` in a body anchors at the CURRENT entity for entity sources (extend `RunEffect` with an anchor override; non-loop `run_effect` keeps the lowest-id-alive anchor).
- `for_each_batched`: entity sources only (arrays never need batching — capacity ≤ `MaxArrayCapacity` ≤ `MaxForEachItems`); must be a top-level chain node (not nested inside `for_each`/`branch`), at most one per trigger. On fire: snapshot ascending ids into its preallocated continuation row (cap `MaxBatchSnapshot`), then drain `BatchSize` entities per tick, ascending, at the START of the director's tick (before event collection/sweep), ascending node-id across rows; dead entities are skipped at drain time; the trigger is suppressed in the sweep while its drain is active; the continuation chain (exec-out port 0) runs on the completion tick. Continuation rows (active, cursor, count, snapshot ids) live in a new `DslLoopState` store folded into `SimChecksum`.
- `branch`: Bool expression via a Boolean data edge into its condition-in data port (compiled `inCondition: false` — TriggerLocal/loop-var reads are LEGAL here, unlike trigger condition-in); exec-out port 1 = then chain, port 2 = else chain, port 0 = continuation (always runs after the taken branch).
- Arrays: declared via `ScenarioVariable` gaining optional `element_type` (Int|Fixed|Bool) + `capacity` (1..`MaxArrayCapacity`) fields (omit-when-null — array-free scenarios serialize byte-identically); Global scope only this story. Storage/ops in `DslVarTable` (preallocated at declared capacity): total runtime semantics — `array_push` at capacity = deterministic no-op, `array_set`/`arr[i]` out-of-bounds = no-op/0 (the div-by-zero precedent). `array_push`/`array_set` REQUIRE a value-in expression edge matching element type; `array_set` also an Int index edge on a new `ActionIndexInPort = 2`; kinds are graph-channel-only (`ToFlat` skips them like `EffectActionNode`; flat `_actionTypes` untouched).
- Named caps in one new `DslBounds` (documented corpus-validated dials, never inline literals): `MaxArrayCapacity=64`, `MaxForEachItems=64`, `MaxLoopNesting=4`, `MaxBatchedLoops=8`, `MaxBatchSnapshot=2048`, `MaxDslOpsPerTrigger=4096`, `MaxDslOpsPerTick=16384`.
- Load gate (both `ScenarioValidator` AND the `CompileExpressionPrograms`-style `LoadScenario` backstop, gate/backstop parity per the 7.4 precedent), all located errors: entity-source `for_each` with `UpTo` unset → error DIRECTING the author to `for_each_batched` or an explicit `up_to`; `UpTo`/`BatchSize` ∈ 1..`MaxForEachItems`; static worst-case cost per trigger (action=1, expression=`OpCount`, `run_effect`=embedded node count, `for_each`=1+iterCap×body, `branch`=1+cond+max(then,else), `for_each_batched`=1+`BatchSize`×body) ≤ `MaxDslOpsPerTrigger`; nesting depth ≤ `MaxLoopNesting`; loop-var declared/TriggerLocal/type-matched; array source declared Array; `RegionId` exists; `CheckFactionSlot` on faction fields (−1 allowed as "any"); batched-node count ≤ `MaxBatchedLoops`; `spawn_unit` `Count` > `EffectCaps.MaxSpawnCount` rejected at the gate.
- Fuel: a per-tick ops counter (reset each director tick; charging mirrors the static cost model) in `DslLoopState`. Exhaustion halts the sweep deterministically at a whole-trigger boundary — the in-flight trigger completes, remaining triggers (and, if drains exhausted it, the whole sweep) skip this tick, identically on two clients, no torn state. Consumed-this-tick value folds into `SimChecksum`.
- Checksum: `DslVarTable.FoldInto` gains an arrays section (leading count; per array: count + elements ascending index) between per-player and timers; `SimChecksum.Compute` folds `DslLoopState` (nullable, `FoldEmpty` null≡empty) after `DslVarTable`, before SimRng (SimRng stays last). This is ONE `AlgoVersion` 16→17 bump: docblock entry naming Story 7.6, re-pinned `KnownWorldState_ProducesPinnedV17Hash`, all goldens re-recorded via `CHIMERA_GOLDEN_RECORD=1` in the same commit, version-pin assertions updated (`HeroProfilePersistenceTests`, `ScenarioDataMapPropertiesTests`, `CombatFeedbackProfileTests`, `Meta/VersionStampConsistencyTests`), coverage-guard teeth for arrays/fuel/continuation rows.
- Spawn-cap reconciliation: `ScenarioDirector.cs:649` literal `50` → `EffectCaps.MaxSpawnCount` (64) as the runtime seatbelt; the validator reject above is the loud gate.
- Every new node kind/field extends `NodeKinds`, `NodeBaseJsonConverter` Read+Write allow-lists (fail-closed, located), and both gates in lockstep; canonical serialization round-trips loop/array subgraphs byte-identically.

**Block If:**
- `CanonicalModelHash` (AlgoVersion 7) or `StartStateHash` (AlgoVersion 2) moves — `Variables`/`TriggerGraphJson` are excluded fields, so movement means loop machinery leaked into the content hash. HALT status `blocked`, condition `loop layer moved a content-hash baseline`.
- More than the single named 16→17 re-baseline is needed, or a loop/array-free legacy scenario's SIM STATE (positions/health/resources — not the hash value) diverges from pre-7.6 behavior in the two-run parity test. HALT status `blocked`, condition `loop wiring is not behavior-parity for loop-free scenarios`.
- An AC turns out to be unimplementable without Story 7.5's custom events. HALT status `blocked`, condition `7.5 dependency is real, not designable-around`.

**Never:**
- No custom events, `RaiseEvent`, next-tick event queue, or cascade caps (7.5 — re-sequenced after this story; the continuation store is the sanctioned substitute). No authoritative structural graph validator — dangling/forked exec edges, duplicate ids stay 7.7 (reject only what loops cannot function without). No T3 canvas (7.10). No entity read-accessor leaves, `OrderUnits`, or new event sources (7.13). No `RandomChoice`/SimRng draws in loops (validator forbids until AR-13 wiring is proven).
- No float/double/`FromFloat`, no `System.Random`/wall-clock, no `[JsonPolymorphic]`, no second executor; the flat `TriggerDefinition` POCOs/JSON stay frozen (loops are graph-channel-only).
- No `Point`/ref-typed array elements; no PerPlayer/TriggerLocal arrays; no expression-language loop constructs.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Array ForEach happy path | Int array [3,5,7], `for_each` w/ TriggerLocal loop var, body `set_variable sum = sum + v` | sum=15 on fire; two headless runs byte-identical | No error |
| Entity ForEach + effect | `for_each` `faction_units` (P2, `up_to` 64), body `run_effect` damage leaf | Each alive P2 unit damaged, ascending id, anchor = current entity | No error |
| Missing loud cap | `for_each` over `faction_units` with `UpTo` unset | Rejected AT LOAD | Located error directing to `for_each_batched` or explicit `up_to` |
| Cap-product overflow | Nested loops, worst-case 64×64×2 ops > `MaxDslOpsPerTrigger` | Rejected AT LOAD | Located error naming the constant |
| Batched drip | `for_each_batched` (`BatchSize` 10) over 25 units | 10/10/5 across 3 ticks; trigger suppressed while draining; continuation chain runs tick 3; unit killed mid-drain skipped; folds move checksum; two-run identical | No error |
| Branch | Bool expr (reads loop var) into `branch`; then/else `set_variable`; continuation after | Taken path + continuation execute per truth value | No error |
| Array totality | `array_push` at capacity; `arr[99]` read; `array_set` idx −1 | No-op / 0 / no-op; tick continues; deterministic | No crash |
| Fuel exhaustion | Many individually-legal loop triggers whose same-tick sum > `MaxDslOpsPerTick` | Sweep halts after the in-flight trigger completes; skipped triggers re-evaluate next tick; identical two-run; fuel value folds | No error, no torn state |
| Bad declarations | Array w/o capacity; capacity 0 or >64; PerPlayer array; loop var not TriggerLocal; wrong element type; unknown region; batched nested in for_each | All rejected at BOTH gate and backstop | Located errors |
| Spawn cap | Graph/flat `spawn_unit` `count: 70` | Rejected at gate; runtime clamp is `EffectCaps.MaxSpawnCount` (no literal 50 remains) | Located error |
| IR round-trip | Loop/branch/array subgraph → `ToCanonicalJson` → `FromJson` → `ToCanonicalJson` | Byte-identical; unknown source/kind strings fail closed | Located converter errors |
| Legacy parity | Loop-free scenario, full suite | Sim state byte-identical to pre-7.6; exactly one 16→17 golden re-baseline; content hashes unmoved | Divergence trips a Block-If |

</intent-contract>

## Code Map

- `godot/src/Dsl/NodeBase.cs` — `NodeKinds` closed registry (`:196-227`), POCO pattern; add 3 exec + 2 expr kinds + `ForEachSources` closed set; `ActionTypes` gains the 3 array kinds.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — fail-closed Read/Write + `RejectUnknownProperties` (`:520-536`); model new branches on existing ones.
- `godot/src/Dsl/TriggerGraph.cs` — port constants (`:26-48`), `TriggerExec` (`:428-446`), `BuildExecutionOrder` (`:457-537`) chain walk + cycle guard; extend to nested container structure; Godot-free `Build*Trigger` helper precedent (`:189-295`).
- `godot/src/Dsl/DslVarTable.cs` — SoA store + fold (`FoldInto :409-434`); arrays live here. `DslValue.cs` — `DslVarDecl` gains ElementType/Capacity.
- `godot/src/Dsl/ExprCompiler.cs` / `ExprParser.cs` / `ExprProgram.cs` — extend grammar (`arr[expr]` via declared-scope disambiguation vs PerPlayer `name[k]` literal; `length(name)`), OpCodes `ArrayGet`/`ArrayLen`; `ExprBounds` untouched.
- `godot/src/Core/ScenarioDirector.cs` — `LoadScenario` (`:131-195`, locals-then-commit atomicity), `CompileExpressionPrograms` backstop (`:205-360`), `EvaluateTriggers` (`:483-510`), `ExecuteActions` (`:633-692`, spawn clamp at `:649`), `RunEffect` (`:702-717`); index [14] in `SimulationHost.cs:195-238`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — graph semantic gate (`:621-706`), expression consumer-edge pass (`:714-782`), `declaredVarInfo` (`:387-412`), `CheckFactionSlot` (`:1104`).
- `godot/src/Core/SimChecksum.cs` — `AlgoVersion=16` at `:181` + docblock; `Compute` fold order (`:187-509`, DslVarTable at `:495-498`, SimRng last).
- `godot/src/Effects/EffectCaps.cs` — `MaxSpawnCount=64` (`:59`); `EffectExecutor.cs` — preallocated-stack pattern; `SearchAreaEffect.cs:55-85` — ascending-id snapshot pattern.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — Variables section (`:731-799`), raw-IR hatch (`:901-926`).
- `godot/ProjectChimera.Sim.Tests/` — `Golden/` (25 goldens, `CHIMERA_GOLDEN_RECORD=1` recipe, `SimChecksumCoverageGuardTest`), `Validation/TriggerValidationTests.cs`, `Dsl/*` suites, `Dsl/NodeKindsLockstepTests.cs`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/DslBounds.cs` (new) — the 7 named caps with doc comments (corpus-validated dials). — one home, no literals.
- `godot/src/Dsl/DslValue.cs` — `DslVarDecl` + `ElementType`, `Capacity` (defaulted for scalars). — array declaration substrate.
- `godot/src/Dsl/NodeBase.cs` — `ForEachNode { Source, ArrayName?, Faction=-1, RegionId?, UpTo=0, LoopVar? }`, `ForEachBatchedNode { Source, Faction=-1, RegionId?, BatchSize }`, `BranchNode {}`, `ExprArrayGetNode { Name }`, `ExprArrayLenNode { Name }`; register `for_each`/`for_each_batched`/`branch`/`expr_array_get`/`expr_array_len`, `ForEachSources`, `ActionTypes` += `array_push`/`array_set`/`array_clear` (pairwise-disjoint). — IR vocabulary.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — Read/Write branches + allow-lists for the 5 kinds (source/kind membership checked at parse; located). — closed-registry round-trip.
- `godot/src/Dsl/GraphEdge.cs` + `TriggerGraph.cs` — `ActionIndexInPort=2`, `ForEachBodyOutPort=1`, `BranchThenOutPort=1`, `BranchElseOutPort=2`, `BranchCondInPort=0` (data); `BuildExecutionOrder` walks containers into a nested exec structure (body/then/else sub-chains; shared visited-set cycle guard; per-node branch-cond and action value/index expr roots surfaced); `ToFlat` skips graph-only kinds; add a Godot-free `BuildForEachTrigger(...)` test/authoring helper mirroring `BuildExpressionTrigger`. — execution view.
- `godot/src/Dsl/DslVarTable.cs` — array SoA (per decl: element type, capacity, count, preallocated raws): `ArrayPush/ArraySet/ArrayClear/ArrayGet/ArrayLen` with total semantics (Bool writes normalized); fold section per Always; `InitFromDeclarations` seeds empty arrays. — collections substrate.
- `godot/src/Dsl/DslLoopState.cs` (new) — fuel counter (reset/charge/consumed) + batched continuation rows allocated at load; `FoldInto`/`FoldEmpty`. — the checksummed runtime state.
- `godot/src/Dsl/ExprParser.cs` + `ExprCompiler.cs` + `ExprProgram.cs` — `arr[expr]` (Array-declared names take the index-expression form; PerPlayer keeps literal-only `name[k]`), `length(name)`; compile: index must be Int, result = element type, Array reads legal ONLY via these forms (bare `arr` still rejects); eval: OOB → 0; branch-condition compile context is `inCondition:false`. — expression access.
- `godot/src/Core/ScenarioDirector.cs` — nested-structure executor (preallocated frames/snapshot buffers per node at load; ascending-id entity snapshots via the SearchArea sort pattern); loop-var writes into TriggerLocal before each iteration; `RunEffect` anchor override; array action cases; batched drain phase at tick start + trigger suppression + completion-tick continuation; fuel charge/reset/boundary-halt; `LoadScenario` backstop parity for every new gate rule (locals-then-commit preserved); `Math.Min(a.Count, 50)` → `EffectCaps.MaxSpawnCount`. — the runtime.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — array-declaration rules; loop/branch/array-action semantic gate + static cap-product cost check per Always (located, constants named); `spawn_unit` count gate; consumer-edge pass extended (branch cond-in must infer Bool + Boolean wire; array-action value/index edges typed vs element type). — authoritative pre-tick gate.
- `godot/src/Core/SimChecksum.cs` (+ `SimulationLoop.cs`/`SimulationHost.cs` plumbing) — fold `DslLoopState` (nullable) after vars; `AlgoVersion` 17 + docblock entry naming Story 7.6. — the named re-baseline.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — Variables section: element-type + capacity inputs shown when Array is selected (validated fail-closed on Add); loops/branches/array actions author via the raw-IR hatch this story (status-label hint; T2/T3 sugar is 7.10/7.13). — layered-complexity minimum.
- `godot/ProjectChimera.Sim.Tests/Dsl/DslArrayTests.cs`, `Dsl/ForEachExecutionTests.cs`, `Dsl/ForEachBatchedTests.cs`, `Dsl/BranchExecutionTests.cs`, `Dsl/DslFuelTests.cs` (new) + extend `Validation/TriggerValidationTests.cs`, converter/round-trip suites, `NodeKindsLockstepTests`, `SimChecksumCoverageGuardTest` (array/fuel/continuation teeth + negative teeth), golden re-record + version-pin updates. — cover every I/O-matrix row, incl. host-altitude two-run determinism for live loops/batched drains and the loop-free legacy parity net.

**Acceptance Criteria:**
- Given declared arrays and `for_each`/`for_each_batched`/`branch` nodes authored as raw IR, when the scenario loads and ticks, then loops iterate their entry-snapshotted collection in ascending order with zero per-tick allocation, batched drains drip `BatchSize`/tick with checksummed continuation, and no While/recursion/goto form is expressible in the grammar.
- Given an entity-group `for_each` without `up_to`, or nesting whose static cap-product exceeds `MaxDslOpsPerTrigger`, when it reaches EITHER the validator gate or the `LoadScenario` backstop, then it is rejected with a located error (directing to `for_each_batched`/`up_to`, or naming the cap) — never a silent runtime truncation — and the `Math.Min(count,50)` literal is gone with the spawn cap reconciled to `EffectCaps.MaxSpawnCount`.
- Given same-tick DSL work exceeding `MaxDslOpsPerTick` from individually-legal triggers, when fuel exhausts, then the sweep halts at a whole-trigger boundary identically on two headless runs with no torn state, and the fuel counter's fold moves `SimChecksum` (proven by coverage-guard teeth).
- Given the full pre-7.6 suite plus a loop-free legacy scenario, when the story lands, then sim-state behavior is byte-identical, `CanonicalModelHash`/`StartStateHash` are unmoved, and exactly one named `AlgoVersion` 16→17 re-baseline (docblock + re-pinned known-state hash + all goldens + version-pin assertions, one commit) is recorded.
- Given `dotnet build`/`dotnet test`, then `src/Dsl` stays Godot-free/float-free with no new warnings, `NodeKinds`/converter/gate/backstop are extended in lockstep (lockstep test passes), and no custom events (7.5), structural validator (7.7), T3 canvas (7.10), or entity read-accessors (7.13) were added.

## Spec Change Log

- 2026-07-16 — Status corrected `in-review` → `in-progress` by a fresh dev-auto run: the prior run's status did not match ground truth (diff vs `baseline_revision` e94fb33 is empty; no 7-6 commits, branches, or dangling worktree snapshots exist anywhere — implementation never persisted). Re-entering step-03 to implement against the unchanged baseline. Spec content untouched.
- 2026-07-16 (escalation resolution) — Added the BINDING Resume Directive after two consecutive 90-min dev-session timeouts, both dying in verification after near-complete implementation. Human decision: warm-start the re-drive from the preserved attempt snapshot (`refs/attempt-preserve-dirty/20260716-100752-2040-e94fb331-*`, highest suffix — the orchestrator parks the current dirty tree there during re-arm rollback) instead of a from-scratch rebuild, and `session_timeout_min` raised 90→180 in `.bmad-loop/policy.toml`. No intent/AC/matrix semantics changed.

## Review Triage Log

### 2026-07-16 — Review pass

- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 0, medium 6, low 4)
- defer: 5: (high 0, medium 2, low 3)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - `[medium]` `[patch]` Hostile deeply-nested container graphs stack-overflowed `BuildExecutionOrder`/`DslLoopGate`/`CompileItems` recursion BEFORE the `MaxLoopNesting` check ran — an uncatchable process kill at the "fail-closed" gate. Added `DslBounds.MaxExecWalkDepth=256` recursion seatbelt (review P9) with located rejects in all three walkers + hostile (264-deep) and legal-nesting tests.
  - `[medium]` `[patch]` `TriggerGraph.BuildExpressionTrigger` never forwarded `arrayDecls`, so a declared Array was unusable from the editor's manual expression field with a false "no array declaration is available" reject. Threaded the optional param through all 4 Parse/TryCompile calls; `TriggerEditorPanel` builds and passes the map (review P10); 2 tests.
  - `[medium]` `[patch]` No test observed a loadable spawn count above the retired literal 50 reaching the delegate — the story's headline clamp reconciliation could regress to `Math.Min(count,50)` undetected. Added `SpawnCountAtMaxSpawnCount_ReachesTheDelegateUnclamped` (Count=64).
  - `[medium]` `[patch]` Array-loop snapshot-at-entry isolation from body mutation was untested (live-read executor would pass the suite). Added body-`array_clear` test: sum still 15, array empty after.
  - `[medium]` `[patch]` Array-source `up_to` runtime cap had no execution coverage (the cap comes purely from the `iter` expression, unlike entity sources' buffer sizing). Added `up_to=2` over [3,5,7] → sum 8.
  - `[medium]` `[patch]` The verbatim dead-anchor mid-loop-death contract was unpinned — a "helpful" re-anchor or an IsAlive body skip would both pass the suite while changing checksummed behavior. Added kill-mid-loop test: dead member's body iteration still runs (counter=3), its run_effect no-ops at the dead anchor, survivors take exactly one hit each.
  - `[low]` `[patch]` `TriggerAction.Count` docs still said "Capped at 50" (two spots) — corrected to the 1..`EffectCaps.MaxSpawnCount` gate + 64 runtime seatbelt truth (review P12).
  - `[low]` `[patch]` `DslLoopGate` class doc overclaimed "parity by construction" — now states rules-identical-by-construction precisely, documents the differing invocation conditions (validator unconditional vs `HasLoopConstructs`-guarded backstop; reachable-only backstop spawn walk) and defers the residual divergence class to 7.7 (review P13).
  - `[low]` `[patch]` The batched drip's fresh-TriggerLocal-scope-per-drain-tick accumulator trap was undocumented — `ForEachBatchedNode` doc now warns (accumulate in Globals) (review P14).
  - `[low]` `[patch]` `ForEachBatchedNode`'s deliberate lack of a `LoopVar` was undocumented — doc now states the run_effect-anchor-only body contract (review P14).

Deferred (4 new ledger entries; a 5th deferred finding — aggregate condition-eval unboundedness — was already ledgered by the prior interrupted pass and is retained, not duplicated): one-shot edge-event loss under fuel exhaustion / drain suppression (→ 7.5 event queue); stray-data-edge silent drops + gate/backstop invocation asymmetry (→ 7.7 structural validator); nested loops sharing one `loop_var` (→ 7.7); `RunEffect` spatial rebuild × loop iterations uncharged by fuel (extends the 7.3 rebuild entry).

Rejected as noise/moot (10): reset-window batched-continuation drop (the deliberate, tested P8 guard; offline editor path), negative spawn count reaching the delegate (unreachable — unconditional backstop + unreachable nodes never execute), literal `set_variable` on an Array name (verified rejected by the pre-existing 7.3 Int-only rule), `FromFlat` vocabulary bypass (sanctioned flow passes the gate; `FlatActionTypes_StayClosedToArrayKinds` pins the flat channel), `DslLoopState` row-node-id fold (load-constant config, not peer-divergent sim state), 24-vs-25 golden count (25th "golden" is the re-pinned known-state constant inside the coverage guard), `up_to` polysemy across sources (spec-chosen, matrix-supported semantics), legacy spawn-count load break (the loud gate IS the story's stated fix for the silent-truncation bug; explicit 0/>64 counts are pathological authoring), batched dead-member body-runs asymmetry vs drain skip (spec-mandated, now pinned by the new test), descriptive 7.7-encroachment observation on the forked cond-in/index-in rejects (justified under "reject only what loops cannot function without").

## Design Notes

- **7.5 re-sequencing:** the orchestrator parked the failed 7-5 attempt (`attempt-preserve/7-5-snapshot-2`) and dispatched 7.6 against master-without-7.5. The epics' "ForEachBatched rides the next-tick event queue" coupling is therefore replaced by the self-contained `DslLoopState` continuation store — same observable drip semantics, no event dependency. When 7.5 re-lands it MAY refactor the drip onto its queue; nothing here presumes it.
- **Why fuel is reachable despite gate/backstop parity:** the load gate caps ops per TRIGGER; the per-TICK budget guards the dynamic aggregate (many legal triggers firing the same tick) plus genuinely escaped definitions. Halting at whole-trigger boundaries keeps state untorn; skipped triggers simply re-evaluate next tick.
- **Batched restrictions (top-level, one per trigger, entity sources only)** keep continuation state one flat row per node — no nested-resume machinery, no loop-var-across-ticks question (entity drains re-enter a fresh TriggerLocal scope per tick).
- **`arr[expr]` vs `name[k]`:** declared-scope-driven disambiguation, exactly as the 7.4 design note reserved — Array-typed names take an index expression; PerPlayer keeps the integer-literal form.
- **Fold placement:** arrays inside `DslVarTable.FoldInto` (they are variables); fuel + continuations in `DslLoopState` folded after vars, before SimRng (SimRng stays last, per the v16 precedent). Array-free scenarios add only leading-count `Mix` steps — behavior-neutral, covered by the one re-baseline.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` — expected: 0 errors, no new warnings; no `[JsonPolymorphic]`; `src/Dsl` Godot-free.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all green including the five new suites, extended validation/converter/lockstep/coverage-guard tests, and all goldens passing at `AlgoVersion` 17 after the single re-record.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` — expected: every golden moved exactly once in the re-baseline commit, headers stamping `checksum_algo_version: 17`; `CanonicalModelHash.AlgoVersion` 7 and `StartStateHash.AlgoVersion` 2 untouched.
- `grep -rniE "using Godot|[^.]\bfloat\b|double |FromFloat" godot/src/Dsl` — expected: no code hits.
- `grep -n "Math.Min(a.Count, 50)" godot/src/Core/ScenarioDirector.cs` — expected: no hits (named constant only).
- Determinism: seeded scenario with live `for_each` + `for_each_batched` (drain spanning ≥3 ticks) + fuel-exhaustion trigger set, run twice headless → byte-identical `SimChecksum` sequences.

**Manual checks (in-engine, via godot-verify):**
- Declare an Int array in the Trigger Editor Variables section (element type + capacity fields); author a `for_each` wave-spawner via the raw-IR hatch; run the match and observe the drip; author an entity `for_each` without `up_to` and confirm the located validator error directs to `for_each_batched`/`up_to`.

## Auto Run Result

Status: done

**Summary.** Story 7.6 implemented, verified, reviewed, and committed. This run executed the BINDING Resume Directive: no re-implementation — it restored the preserved attempt from `refs/attempt-preserve-dirty/20260716-100752-2040-e94fb331-0` (the NEWEST park satisfying the directive's three-marker predicate; the literal "highest numeric suffix" reading would have selected the older 18:56 attempt because the orchestrator rotates parks newest-first, which would have discarded the later session's verified work — the directive's evident intent, preserving the most complete attempt, was honored over its letter), resumed at VERIFICATION, then ran the full four-layer review and patched the surviving findings.

**Implemented change (the restored + patched diff, 70 godot files vs baseline `e94fb331`):**
- `src/Dsl/DslBounds.cs` (new) — 8 named caps (the 7 spec dials + review-P9 `MaxExecWalkDepth=256` recursion seatbelt).
- `src/Dsl/NodeBase.cs`, `NodeBaseJsonConverter.cs`, `GraphEdge.cs`, `TriggerGraph.cs` — `for_each`/`for_each_batched`/`branch` exec containers + `expr_array_get`/`expr_array_len` kinds, fail-closed converter branches, nested `BuildExecutionOrder` with cycle + depth guards, `BuildForEachTrigger` helper, `arrayDecls` threading in `BuildExpressionTrigger` (review P10).
- `src/Dsl/DslValue.cs`, `DslVarTable.cs` — Array declarations (`ElementType`/`Capacity`), preallocated array SoA with total push/set/clear/get/len semantics, fold section between per-player and timers.
- `src/Dsl/DslLoopState.cs` (new) — per-tick fuel counter + batched continuation rows, `FoldInto`/`FoldEmpty` (null≡empty).
- `src/Dsl/ExprParser.cs`, `ExprCompiler.cs`, `ExprProgram.cs` — `arr[expr]` / `length(arr)` grammar, `ArrayGet`/`ArrayLen` opcodes, OOB→0.
- `src/Core/Definitions/DslLoopGate.cs` (new) — the shared gate rulebook (nesting, cap-product cost, loop-var/array/batched rules, spawn counts) invoked by BOTH `ScenarioValidator` and the `LoadScenario` backstop; parity doc honesty (review P13).
- `src/Core/ScenarioDirector.cs` — zero-allocation nested executor (preallocated frames/snapshots, ascending-id entity snapshots), batched drain phase at tick start with suppression + completion-tick continuation, fuel charge/reset/whole-trigger boundary halt, `RunEffect` anchor override with verbatim dead-anchor semantics, spawn clamp → `EffectCaps.MaxSpawnCount`.
- `src/Core/SimChecksum.cs` + host/loop plumbing — `AlgoVersion` 16→17 (single named re-baseline), `DslLoopState` folded after vars, before SimRng.
- `src/CreationSuite/TriggerEditorPanel.cs` — Array element-type/capacity declaration UI, raw-IR hatch hint, arrayDecls pass-through.
- Tests: 5 new Dsl suites + `DslVarWiringTests`/`SimResetTests`/validation/converter/lockstep/coverage-guard extensions; 24 goldens re-recorded at v17; version pins updated. Suite: **2162 passed / 0 failed / 1 pre-existing intentional skip** (was 2154 pre-patch; +8 review-pass tests).

**Review findings breakdown:** 4 layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) → 10 patched (6 medium, 4 low: recursion seatbelt, arrayDecls plumbing, 4 pinning tests, 4 doc corrections), 5 deferred (4 new ledger entries + 1 already ledgered by the prior interrupted pass), 10 rejected. No intent_gap, no bad_spec; `review_loop_iteration` stayed 0.

**Follow-up review recommendation:** false — the review-driven changes are two narrow, individually-tested seatbelt/plumbing fixes plus test pins and doc corrections; the substantive diff itself is what the four layers reviewed.

**Verification performed:** `dotnet build godot/godot.sln` 0 errors (11 pre-existing warnings in untouched files, 0 new); `dotnet test` 2162/2162 green (run independently after the patch pass); golden diff-stat shows exactly one re-baseline, headers stamp `checksum_algo_version: 17`, `hero-start-state` (StartStateHash v2) untouched; `CanonicalModelHash.AlgoVersion` 7 / `StartStateHash.AlgoVersion` 2 unmoved; Dsl purity grep — comment-only hits; `Math.Min(a.Count, 50)` gone; two-run determinism pinned by 4 dedicated tests (director + host altitude); all 13 I/O-matrix rows audited to named passing tests.

**Residual risks / residual artifacts:**
- The spec's manual in-engine checks (godot-verify: array declaration UI, wave-spawner drip, located `up_to` error) were NOT run this session — command verification and the Godot-free suite carried acceptance; run `/godot-verify` on 7.6 when the editor is next open.
- One-shot edge-event loss under fuel exhaustion / drain suppression is real, deterministic, spec-consistent, and deferred to the 7.5 event-queue re-land (ledger).
- Legacy scenarios with pathological explicit spawn counts (0 or >64) now fail loud at load instead of silently no-op/clamping — deliberate, per the story's loud-gate posture.
- Residual uncommitted artifact: `.bmad-loop/policy.toml` (the escalation resolution's `session_timeout_min` 90→180 change) — orchestrator config, not part of this story's diff; left in place.
