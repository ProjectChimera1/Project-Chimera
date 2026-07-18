---
title: 'Complete the trigger vocabulary — state-reads / RandomChoice / enable-disable-run + action leaves + event breadth'
type: 'feature'
created: '2026-07-18'
status: done
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '38b54ee26b524ed5a9bc6b552eb87a9d2d4bed48'
final_revision: '55417df'
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-7-11-win-condition-preset-templates-t1-sim-layer-winconditionsystem.md'
warnings: ['multiple-goals', 'oversized']
---

<intent-contract>

## Intent

**Problem:** The trigger DSL vocabulary is incomplete. There are no state-read accessors (entity HP/owner/position, tag/category-filtered unit counts, player resource, region unit count), no randomness (RandomChoice), no trigger self-management (enable/disable/run), a thin action-leaf set (no unit orders, no camera, no VFX), and only six event sources. Stories 7.11/7.12 explicitly deferred exactly this vocabulary (`WinConditionSystem` was forced to native-evaluate because the DSL had no per-entity read and no instance designation — see spec-7-11 Design Notes). Without it creators cannot author WC3-class custom logic.

**Approach:** Extend the single closed `NodeKinds` registry (`godot/src/Dsl/NodeBase.cs`) — the one vocabulary source since Story 7.7 — with the remaining constructs on the shared IR, each flowing through every vocabulary surface so no tier drifts: (a) **state-read expression built-ins** as new `ExprCallFns` + `ExprProgram` opcodes + `IExprWorld` methods; (b) a **`random_choice`** weighted-branch exec container drawing from `world.Rng`; (c) **`enable_trigger` / `disable_trigger` / `run_trigger`** actions with a named run-depth cap and load-time self/mutual-run cycle lint; (d) **action leaves** — sim `order_units` (reusing `OrderApplier`), presentation-only `move_camera` / `cinematic_mode` / `play_vfx`; (e) **five new event sources** — `unit_damaged`, `unit_trained`, `ability_cast`, `hero_level`, `player_chat` — raised deterministically into the already-folded `DslEventQueue` at ascending-id tick boundaries. New enable/disable runtime state folds into `SimChecksum` (bump 20→21); the new kinds fold into `CanonicalModelHash` (bump 12→13); `player_chat` rides the tick-stamped replicated DSL-event rail so all clients evaluate it on the same tick.

## Boundaries & Constraints

**Always:**
- **One vocabulary source, no tier drift.** Every new construct is declared in `NodeKinds` (`NodeBase.cs`) and propagated through all surfaces it touches (see the Design Notes "add-a-kind checklist"): `KindOf`, `NodePorts`, `NodeBaseJsonConverter` (Read + Write + `RejectUnknownProperties`), `ExprCompiler.IsExprNode` (expr leaves only), `GraphStructureGate`, `DslLoopGate` cost switch (new container/action), `TriggerGraph.IsGraphOnlyKind` + `ToFlat` + `BuildExecutionOrder`/`WalkChain` recognition, `ScenarioDirector.ExecuteItem`/`ExecuteLeaf`, `NodePaletteFactory` (T3), and the `NodeKindsLockstepTests` master list + round-trip builder. Flat-representable actions additionally extend `ActionTypes` (the flat validator/`FlatActionTypes` derive automatically) and `TriggerDefinition` + `FromFlat` lowering; graph-only kinds are added to `IsGraphOnlyKind` and made fail-closed in `ToFlat`.
- **Sim purity** for every sim-side leaf/opcode: no `using Godot`, no `float`/`double`/`Mathf`/`Fixed.FromFloat`, no wall-clock, no string formatting or `int→string`/`Fixed→mm:ss` in the tick; all fractional math is `Fixed` (16.16); entities iterate ascending id skipping `!IsAlive`; factions iterate the active set (never a bare `0..FACTION_COUNT` literal); randomness only via `world.Rng`.
- **State-read built-ins are pure reads.** They extend the closed `ExprCallFns` set + `ExprProgram.OpCode` + `IExprWorld` (the `count()`/`CountAlive` pattern at `ScenarioDirector.cs:836,1662`), return a raw `Int`/`Fixed`/`FactionRef`/`Point` typed by `ExprProgram.ResultType`, never mutate sim state, and reuse the existing ascending-id + `IsAlive` scan and `RegionStore.Contains`. Type/arity mismatch and division-by-zero-class errors reject at load with located errors; an out-of-range/dead entity read returns a defined sentinel (Fixed.Zero / Neutral / origin), never throws in-tick.
- **RandomChoice determinism.** `random_choice` evaluates its weighted exec-out branches in ascending port-index order, sums integer weights, draws `world.Rng.NextInt(totalWeight)`, and selects by subtracting down the pre-sorted branch/weight array. It draws from the single shared `SimRng` stream folded LAST (`SimChecksum.cs:629`); do not add a second RNG stream or reorder the SimRng fold. A zero-total-weight or empty-branch `random_choice` rejects at load.
- **Enable/Disable/Run trigger.** `enable_trigger`/`disable_trigger` reference a target trigger by its persistent trigger-node id; the director maintains a nodeId→`_execs`-index map and a NEW sim-side runtime `bool[] _triggerEnabledRuntime` parallel to `_execs` (mirroring `_triggerFired`/`_triggerCooldown` at `ScenarioDirector.cs:50-51`), initialized from `TriggerNode.Enabled` in `LoadScenario`, consulted alongside `t.Enabled` at BOTH sweep sites (`:1101`, `:1230`), and **folded into `SimChecksum`** (cross-tick sim truth) — bump `SimChecksum.AlgoVersion` 20→21 and re-baseline all world goldens in the same commit. `run_trigger` synchronously executes a target trigger's action chain, bounded by a named `EventBounds`-style run-depth cap (constant, part of the ruleset identity — no per-scenario fold); a per-tick run-depth counter is transient (reset at tick start, not folded). Self-run and mutual-run cycles are rejected at load with a located error naming the cycle (reuse the `EventDispatchPlan` tri-color DFS pattern at `EventDispatchPlan.cs:487`).
- **Action leaves — sim vs presentation split is load-bearing.** `order_units` runs sim-side, per unit collected ascending-id, via `OrderApplier.ApplyActiveOrder` (`NetworkCommand.cs:261`), reusing existing `UnitCommand` semantics — it folds through the existing entity/order-ring checksum, no new fold. `move_camera` (resolve a `ScenarioCamera` by name → `RtsCameraController`), `cinematic_mode`, and `play_vfx` are PRESENTATION-ONLY: they ride the director's presentation delegates (`OnSpawnUnit`/`OnDisplayMessage`/`OnPlaySound` pattern at `ScenarioDirector.cs:184-193`) or `CombatEventQueue.Push` (`CombatEventQueue.cs:83`, provably drained/`Clear()`ed every frame, excluded from `SimChecksum`). No presentation state ever folds into the checksum.
- **Event breadth.** Add `unit_damaged`, `unit_trained`, `ability_cast`, `hero_level`, `player_chat` to `EventTypes` (and `GraphEventTypes`) with typed param schemas mirroring `unit_dies` (`EventDispatchPlan.cs:40-49`). Raise each at its identified ascending-id tick-boundary site into the existing `DslEventQueue`/work-list: `unit_damaged` @ `DamageResolver.cs:80`, `unit_trained` @ `BuildingSystem.cs:167`, `ability_cast` @ the atomic-success point in `AbilityCastSystem.TryCast`, `hero_level` @ `HeroXpSystem.cs:279`. These raises add NO new folded state (the queue is already folded, v18) → **no AlgoVersion bump for them**, but per-tick hashes legitimately move → **golden re-record required**.
- **PlayerChat is the one wire-touching event.** It must ride a tick-stamped replicated path so all clients evaluate it on the same tick: route it through `LockstepManager.EnqueueDslEvent` / `UnitCommand.DslEvent` (`LockstepManager.cs:286`, applied at all four `OrderApplier` sites — live, spectator, `ReplayPlayer.ApplyOrders`, recorder) into the checksum-folded queue via `ScenarioDirector.TryEnqueueExternalDslEvent` (`:254`). Its DSL event carries the sender faction slot + a bounded integer chat-code ONLY (no string enters the tick; the chat-string↔code map is presentation-side). Replay stays `VERSION=3` if it reuses the existing 11-byte `UnitCommand.DslEvent` order; a distinct command byte requires a `VERSION` annotation.
- **CanonicalModelHash** covers the new node kinds via its typed graph walk; bump `CanonicalModelHash.AlgoVersion` 12→13 and re-record `hero-start-state.golden.txt` (the 7.5/7.11 precedent — a graph-walk extension bumps even though no existing scenario carries the new kinds). A scenario carrying none of the new kinds folds byte-identically apart from the version bump.
- **All four tiers.** Each new node is authorable in T3 (palette + factory) and validates identically for T4/NL (same gate); flat-representable actions appear in T2; a T1 preset is added only where a turnkey template is natural (not required per node). Graph-only kinds surface the non-destructive read-only "edit in graph view" row in the T2 editor.

**Block If:**
- Making `random_choice` deterministic requires reordering the SimRng fold or introducing a second RNG stream (breaks every golden's SimRng-last invariant) — HALT.
- `player_chat` cannot be made same-tick deterministic without a replay wire/stride change that breaks v3 back-compat beyond a `VERSION` annotation — HALT rather than silently break replays.
- Any required new folded state beyond `_triggerEnabledRuntime` would force a second `SimChecksum` AlgoVersion bump, or another in-flight story already claimed 21 — rebase and take the next free integer; if unresolvable, HALT (colliding-bump hazard, per the memory).
- An AC cannot be met without a new SoA store or wire format not anticipated here — HALT rather than expand scope silently.

**Never:**
- No scripting escape hatch (no Lua/JASS/RunScript/customParams); all kinds come from the closed registry; an unknown kind rejects at parse with a located error naming the kind.
- No second executor — reuse `EffectExecutor`, `OrderApplier`, `CombatEventQueue`, `RegionStore`, the `ExprProgram` machinery. No duplicated vocabulary copy (the 7.7 aliasing must survive; `NodeKindsLockstepTests` stays green).
- No float/culture/wall-clock/string-formatting inside the tick; no presentation state folded into `SimChecksum`; no arbitrary chat string in the tick.
- No unbounded/uncapped `run_trigger` recursion — the cap is by construction at load, never a runtime truncation.
- Out of scope: objectives/quest log & briefing (Story 7.14) and the trigger-debugging overlay (Story 7.15). Do not build them.

**Re-baseline & unattended-safety protocol (escalation resolution 2026-07-18 — AUTHORITATIVE).**
Two prior unattended passes HALTed here on a *judgment* that an unattended golden re-baseline (`SimChecksum` 20→21 + re-record of all ~24 world goldens, plus `CanonicalModelHash` 12→13 + `hero-start-state`) is unsafe because a wrong re-record silently bakes a latent fold/reset bug into the goldens and the suite still goes green. This story ships **whole, in one pass, at maximum effort**; the resolution below converts that judgment into checked invariants so the pass proceeds instead of re-escalating:
- **The re-baseline is PRE-AUTHORIZED. Do NOT escalate on the existence or size of the golden diff.** A moved world-golden set (all ~24) and a moved `hero-start-state.golden.txt` are the *expected* output of the two bumps, not a blocking anomaly.
- **The correctness proof of the re-baseline is the differential guard, not the green suite.** Author (before re-recording) the AC-#6 differential-guard test: a scenario carrying NONE of the new kinds must fold **byte-identical to its pre-story bytes except the single version integer** (`SimChecksum` and `CanonicalModelHash` each differ only by their bump). This mathematically catches a bad `_triggerEnabledRuntime` fold-order or `SimulationHost`/`SimulationLoop` reset-threading defect on every pre-existing scenario. This guard passing — not merely "goldens recorded, suite green" — is the gate. If the differential guard fails, HALT (the re-baseline is corrupt); a green suite alone is NOT sufficient evidence.
- **Commit the checksum-neutral slice SEPARATELY from and BEFORE the re-baseline.** Arm A (state-read built-ins, `order_units`, presentation `move_camera`/`cinematic_mode`/`play_vfx`, the `IsGraphOnlyKind`⇔`ToFlat` test) is checksum-neutral and lands in its own commit with no golden churn. The determinism-bumping arms (`random_choice` → `CanonicalModelHash` 13; enable/disable/run → `SimChecksum` 21 + all-golden re-record) land in a subsequent commit that contains the bumps + the re-baseline together. This keeps a bad re-baseline revertible without discarding the done, safe work (the discarded-near-complete-work hazard in project memory).
- **Exactly one bump per checksum, no colliding claim.** `SimChecksum` → 21 and `CanonicalModelHash` → 13, each bumped once. Assert no other in-flight story has claimed 21/13; if one has, take the next free integer (Block If already governs this). Do not double-bump within this story.
- **A human reviews the golden diff at the resume checkpoint** before the re-baseline is trusted downstream. The pass does not need to wait on that review to reach green; it must leave the diff clean and attributable (base re-record only, no unexplained per-tick churn beyond the folded-state additions).

**Arm D (`player_chat`) headless test bar.** Route it whole (replicated tick-stamped rail across `LockstepManager.EnqueueDslEvent`/`UnitCommand.DslEvent`/all four `OrderApplier` sites/`ReplayRecorder`+`ReplayPlayer`). Its **headless covering test is replay-reproduction via the existing `ReplayDslEventTests` harness** (`ProjectChimera.Sim.Tests/Multiplayer/ReplayDslEventTests.cs`): record a run injecting a `player_chat` DSL event, replay it, assert byte-identical `SimChecksum`. That test satisfies the Matrix `player_chat` row for the audit. The *two-client same-tick* claim (both live clients evaluate the chat-code on the identical tick) is **not headlessly exercisable and is a MANUAL godot-verify check, not a headless-audit-blocking row** — its absence from the headless suite must NOT trigger a matrix-audit HALT.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| State-read `entity_hp` | expr_call over a live entity ref | returns `Fixed` = `world.Health[id]`, wired as a Fixed data edge | dead/out-of-range id → `Fixed.Zero`; type/arity mismatch → located reject at load |
| Tag/category unit count | `unit_count_tag(faction, tag)` | `Int` = ascending-id `IsAlive` scan matching `FactionOf` + `TagsOf`/`CategoryOf` | unknown tag/category value → located reject at parse (closed field vocab) |
| `player_resource` | `player_resource(faction, resource)` | `Fixed` from `ResourceStore.Ore/Crystal[(int)faction]` | unknown resource kind → located reject |
| `random_choice` | 3 weighted branches, weights (1,2,1) | draws `world.Rng.NextInt(4)`, selects branch by pre-sorted subtraction; identical across two seeded runs | total weight 0 / no branches → located reject at load |
| `enable_trigger`/`disable_trigger` | action targets trigger node id T | runtime enabled flag for T's exec index flips; T's firing gates on it next sweep; state folds into `SimChecksum` | target id resolves to no trigger → located reject at load |
| `run_trigger` (depth ok) | action runs target trigger's chain, depth < cap | target chain executes synchronously in place | depth ≥ cap at runtime → deterministic halt at whole-trigger boundary (seatbelt), never mid-Sequence |
| `run_trigger` cycle | trigger A run_trigger→A, or A→B→A | — | rejected at LOAD with a located cycle error naming the path |
| `order_units` | select faction/region units, cmd=AttackMove, target point | each unit (ascending id) gets the order via `OrderApplier.ApplyActiveOrder`; folds through existing order state | invalid target/empty selection → no-op, no throw |
| `move_camera`/`play_vfx` | presentation leaf fires | camera pans to named `ScenarioCamera` / VFX pushed on `CombatEventQueue`; NOT in `SimChecksum` | unknown camera name → located reject at load; queue full → silently dropped (presentation) |
| New event source | e.g. a hero levels up | `hero_level(hero, level, faction)` raised into `DslEventQueue` at the tick boundary, ascending id; subscribed triggers fire deterministically | over cascade/fan-out caps → existing `EventBounds` located reject at load |
| `player_chat` (online) | peer sends chat-code C from faction F | injected as tick-stamped `UnitCommand.DslEvent`, applied at all four sites, evaluated same-tick on every client. **Headless covering test = replay-reproduction via `ReplayDslEventTests` (record→replay→byte-identical `SimChecksum`).** Two-client same-tick = MANUAL godot-verify only (see resolution protocol); its absence from the headless suite is NOT a matrix-audit HALT. | over `MaxDslEventsPerTick` → rate-limited (existing) |
| Determinism replay | seeded scenario exercising random_choice + all new events | two headless runs byte-identical `SimChecksum`; goldens re-recorded | first divergence reported by `GoldenChecksumReplay.CompareSequences` |
| No-new-kind scenario (**re-baseline correctness guard**) | an existing scenario with none of the new kinds | `SimChecksum` differs only by the 20→21 bump; `CanonicalModelHash` only by 12→13 — **byte-identical apart from the version integer.** This is the authoritative proof the re-baseline is correct (catches a bad `_triggerEnabledRuntime` fold / reset threading); it gates the re-record, above a green suite. | guard fails → HALT: re-baseline is corrupt, do NOT trust the green suite |

</intent-contract>

## Code Map

**Vocabulary registry / tier-drift surfaces (extend per new kind):**
- `godot/src/Dsl/NodeBase.cs` — `NodeKinds` (:369): kind consts + `EventTypes`(:397)/`GraphEventTypes`(:400)/`ConditionTypes`(:405)/`ActionTypes`(:409)/`FlatActionTypes`(:417 derived), `ExprCallFns`(:441), `KindOf`(:454), `NodePorts`(:485). Node classes `EventNode`/`ConditionNode`/`ActionNode`/`ExprCallNode` etc.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — `Write`(:58) + `ReadNode`(:264) + `RejectUnknownProperties`(:731), all fail-closed.
- `godot/src/Dsl/ExprCompiler.cs` — `IsExprNode`(:35) closed expr-leaf set; `WireOf`(:41). New state-read expr leaves register here.
- `godot/src/Dsl/GraphStructureGate.cs` — structural gate over `NodePorts`/`KindOf` (:116-331).
- `godot/src/Core/Definitions/DslLoopGate.cs` — cost `switch (it.Node)` (:280); new container/action needs a cost arm.
- `godot/src/Dsl/TriggerGraph.cs` — `IsGraphOnlyKind`(:88), `ToFlat`(:555, fail-closed throws :561-567, skips :651), `BuildExecutionOrder`(:782)/`WalkChain`(:875, recognition set :887-889).
- `godot/src/Dsl/NodePaletteFactory.cs` — T3 palette `kinds`(:27-43) + `Create`(:56-94).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — T2 `ActionKinds`(:76); Raw-IR hatch `BuildRawIrSection`(:1392) covers graph-only authoring.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — flat gate aliases `_triggerEventTypes/_conditionTypes/_actionTypes = NodeKinds.*` (:1343-1346), dispatch (:666,684,733).

**Runtime executor / seams:**
- `godot/src/Core/ScenarioDirector.cs` (`ISimSystem, IExprWorld`, runs LAST) — `Tick`(:854), `EvaluateTriggers`(:1080, order Priority desc→asc node-id), sweep gates (:1101,:1230), `_triggerFired`/`_triggerCooldown`(:50-51), `LoadScenario` reset(:405), `ExecuteItem`(:1417), **`ExecuteLeaf`**(:1554, leaf switch :1556-1617), `CountAlive`/`IExprWorld`(:836,:1662), presentation delegates(:184-193), `_combatEvents`(:160), `_regions`+`SetRegionStore`(:150,:231), `TryEnqueueExternalDslEvent`(:254), work-list/drain(:1186,:1212), event registry(:112-116).
- `godot/src/Core/SimRng.cs` — `NextInt(countExclusive)`(:61) via `EntityWorld.Rng`(`EntityWorld.cs:187`); `State`(:35) folded last.
- `godot/src/Multiplayer/NetworkCommand.cs` — `OrderApplier.Apply`(:117) + `ApplyActiveOrder`(:261, Move/AttackMove/AttackTarget/…); DslEvent branch(:209); `MakeChat`(:705)/`PacketType.Chat`(:41).
- `godot/src/Core/RegionStore.cs` — `TryGetIndex`(:49)/`Contains`(:71); scan pattern `ScenarioDirector.cs:1367-1374`.
- `godot/src/Core/EntityWorld.cs` — `Health`(:206)/`Position`(:191)/`FactionOf`(:211)/`CategoryOf`(:410)/`TagsOf`(:432)/`IsAlive`(:1131)/`HighWaterMark`(:665)/`KillerOf`(:374)/`KillerFactionOf`(:382).
- `godot/src/Core/ResourceStore.cs` — `Ore`(:15)/`Crystal`(:16) by `(int)Faction`.
- `godot/src/Core/Definitions/ScenarioData.cs` — `ScenarioCamera`(:389, `Name` key), `Cameras`(:944).
- `godot/src/UI/RtsCameraController.cs` — `PanTo`(:172); `godot/src/CreationSuite/CameraTool.cs`(:11 doc "Epic 7 MoveCamera").
- `godot/src/Combat/CombatEventQueue.cs` — `Push`(:83,:91), `CombatEventType`(:8, appendable), drained/`Clear()` by `CombatFeedbackBridge.cs:132` — NOT folded.

**Events / expression / checksum / golden / replay:**
- `godot/src/Dsl/EventDispatchPlan.cs` — registry(:52-66), `unit_dies` param map(:40-49), `ValidateRegistry`(:109), cycle DFS(:487). `godot/src/Dsl/EventBounds.cs` — caps(:26-89). `godot/src/Dsl/DslEventQueue.cs` — `Enqueue`(:35)/`FoldInto`(:86).
- `godot/src/Dsl/ExprProgram.cs` — `IExprWorld`(:11), `OpCode`(:38, `Count` @:55), `ResultType`(:82), `Eval`(:119). `DslValueType`(`DslValue.cs:16`).
- Raise sites: `DamageResolver.cs:80` (Health write, `Apply` :65) / `:96` `KillEntity`; `BuildingSystem.cs:167` `SpawnTrainedUnit`; `AbilityCastSystem.cs` `TryCast`(:165, success :93); `HeroXpSystem.cs:279` (`AdvanceLevels` :266).
- `godot/src/Core/SimChecksum.cs` — `AlgoVersion=20`(:234), `Compute`(:240), fold order (DslVarTable :551, DslEventQueue :574, WinState :586, SimRng last :629).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — `AlgoVersion=12`(:163), typed graph walk (:430,:630).
- `godot/src/Core/Sim/SimulationHost.cs` — 16-system array (:239-286, `ScenarioDirector` [15] :285); `DslEventSink`(:137).
- `godot/src/Multiplayer/LockstepManager.cs` — `EnqueueDslEvent`(:286), `SendChat`(:456)/`OnChatReceived`(:74,:511), `ApplyOrders`(:352,:407). `ReplayRecorder.cs` — `VERSION=3`(:29), `RecordTick`(:77); `ReplayPlayer.cs` — accepts `[2,VERSION]`(:109), `ApplyOrders`(:191).
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` — record via `CHIMERA_GOLDEN_RECORD=1`(:31,:40); goldens are embedded resources (`*.csproj:27-34`). `AiActiveGoldenTests.cs:63-66` Windows-only.
- `godot/ProjectChimera.Sim.Tests/Dsl/NodeKindsLockstepTests.cs` — `AllKinds`(:21) master list, `ScenarioValidator_ConsumesNodeKinds_ByReference`(:70), round-trip builder `MinimalNode`(:113,:135).

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/NodeBase.cs` -- register every new construct in `NodeKinds` (state-read `ExprCallFns` entries; `random_choice`/`enable_trigger`/`disable_trigger`/`run_trigger`/`order_units`/`move_camera`/`cinematic_mode`/`play_vfx` kinds; five new `EventTypes`+`GraphEventTypes` strings), add node classes, `KindOf` arms, `NodePorts` arms -- the single vocabulary source; everything else derives/aliases from here.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` -- add `Write`/`ReadNode`/`RejectUnknownProperties` branches for each new node class -- closed-registry round-trip, fail-closed on unknown/dup properties.
- `godot/src/Dsl/ExprProgram.cs` + `godot/src/Dsl/ExprCompiler.cs` -- add opcodes + `IExprWorld` method signatures for the state reads (entity hp/owner/position, tag/category unit count, player resource, region unit count); register the expr leaves in `IsExprNode`; type results via `ResultType`/`WireOf` -- reads return raw Int/Fixed/FactionRef/Point, never throw in-tick.
- `godot/src/Core/ScenarioDirector.cs` -- implement the new `IExprWorld` read methods (ascending-id scans, `RegionStore.Contains`, `ResourceStore`); add `ExecuteLeaf`/`ExecuteItem` arms for `random_choice` (SimRng weighted draw), `enable_trigger`/`disable_trigger` (nodeId→exec-index map + new folded `bool[] _triggerEnabledRuntime`, consulted at :1101/:1230, reset in `LoadScenario`), `run_trigger` (depth-capped synchronous run), `order_units` (`OrderApplier.ApplyActiveOrder` per unit ascending-id), presentation `move_camera`/`cinematic_mode`/`play_vfx` (delegates/`CombatEventQueue`); raise the four sim event sources into the event registry; extend the event registry to the five new types -- the runtime landing for the whole vocabulary.
- `godot/src/Combat/DamageResolver.cs`, `godot/src/Economy/BuildingSystem.cs`, `godot/src/Effects/AbilityCastSystem.cs`, `godot/src/Combat/HeroXpSystem.cs` -- raise `unit_damaged`/`unit_trained`/`ability_cast`/`hero_level` at the identified ascending-id tick-boundary sites into the director's event sink -- deterministic, checksum-neutral (queue already folded).
- `godot/src/Dsl/EventDispatchPlan.cs` + `godot/src/Dsl/EventBounds.cs` -- add typed param schemas + counts for the five new events (mirror `unit_dies`); add a named `MaxRunTriggerDepth` cap constant -- typed, bounded, gated at load.
- `godot/src/Dsl/TriggerGraph.cs` -- add graph-only kinds to `IsGraphOnlyKind`, make `ToFlat` fail-closed/skip them, recognize new action/container kinds in `WalkChain`; flat-representable actions get flat lowering -- no lossy flat drop, T2 shows read-only fallback for graph-only.
- `godot/src/Core/Definitions/DslLoopGate.cs` + `godot/src/Dsl/GraphStructureGate.cs` -- cost/structure arms for `random_choice` (container) and the new actions -- bounded-by-construction at load.
- `godot/src/Core/Definitions/ScenarioValidator.cs` (+ the graph load gate) -- located rejects: unknown camera name, unresolved target trigger id, zero-weight/empty `random_choice`, self/mutual `run_trigger` cycle (tri-color DFS), state-read arity/type mismatch -- fail-closed with one located error each.
- `godot/src/Multiplayer/LockstepManager.cs` + `godot/src/Multiplayer/ReplayRecorder.cs`/`ReplayPlayer.cs` -- route `player_chat` through the tick-stamped `EnqueueDslEvent`/`UnitCommand.DslEvent` rail (sender faction + bounded int chat-code), applied at all four `OrderApplier` sites and recorded in replay; confirm/annotate `VERSION` -- same-tick deterministic across all clients + replays.
- `godot/src/Core/SimChecksum.cs` -- fold `_triggerEnabledRuntime` (declaration order, before the SimRng block, iterating exec order); bump `AlgoVersion` 20→21 with a version note -- the one new folded store.
- `godot/src/Core/Sim/SimulationHost.cs` + `godot/src/Core/SimulationLoop.cs` -- thread the enabled-runtime state through both `SimChecksum.Compute` call sites and `ClearForReset` -- reset-safe, checksum-covered.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- extend the typed graph walk to the new kinds; bump `AlgoVersion` 12→13 -- handshake-safe; no-new-kind scenarios byte-identical apart from the bump.
- `godot/src/Dsl/NodePaletteFactory.cs` (+ optionally `TriggerEditorPanel.cs` `ActionKinds`) -- add the new kinds to the T3 palette/factory (T2 flat actions to `ActionKinds`) -- authorable in every tier; Raw-IR hatch covers the rest.
- `godot/ProjectChimera.Sim.Tests/` -- add each new kind to `NodeKindsLockstepTests.AllKinds` + `MinimalNode`; add the missing **`IsGraphOnlyKind`⇔`ToFlat` equivalence test** (a kind is graph-only iff `ToFlat` throws/drops it, derived from behavior not restated literals); headless tests for every I/O-matrix row (state reads, random_choice determinism over two seeded runs, enable/disable fold + latch, run_trigger depth cap + cycle reject, order_units via applier, each event raise fires a subscribed trigger, **player_chat replay-reproduction via the existing `ProjectChimera.Sim.Tests/Multiplayer/ReplayDslEventTests.cs` harness** — record→replay→byte-identical `SimChecksum`); `SimChecksum`/`CanonicalModelHash` version pins (21/13) + **the differential coverage-guard authored BEFORE re-recording (the no-new-kind byte-identical-except-version test — this is the re-baseline correctness gate per the resolution protocol; a green suite alone is not sufficient)**; then re-record all world goldens + `hero-start-state.golden.txt`. **Commit discipline: land the checksum-neutral Arm A slice in its own commit (no golden churn) FIRST, then the bump+re-baseline arms in a following commit, so a failed differential guard is revertible without discarding done work.**

**Acceptance Criteria:**
- Given the extended vocabulary, when the suite runs, then `NodeKindsLockstepTests` stays green (validator still aliases `NodeKinds` by reference), every new kind round-trips through `NodeBaseJsonConverter`, and a new test proves `IsGraphOnlyKind(k)` ⇔ `ToFlat` has no flat form for `k` (so a graph-only addition can never silently read as flat-editable).
- Given a graph using each state-read built-in, when evaluated in a headless tick, then it returns the correct typed value from the live sim stores (HP/owner/position/filtered counts/resource/region count), a dead/out-of-range entity read yields the defined sentinel without throwing, and an arity/type mismatch is rejected at load with one located error.
- Given a `random_choice` with weighted branches, when two headless runs execute the same seeded scenario+command stream, then both select the identical branch sequence and yield a byte-identical final `SimChecksum` (draw rides the SimRng-last fold), and a zero-total-weight `random_choice` is rejected at load.
- Given `enable_trigger`/`disable_trigger`/`run_trigger`, when exercised headlessly, then the runtime enabled flag flips and gates firing (and folds into `SimChecksum` with `AlgoVersion==21` + re-recorded goldens), `run_trigger` runs the target chain up to the named depth cap and halts deterministically at a whole-trigger boundary beyond it, and a self/mutual run cycle is rejected at load with a located cycle error.
- Given `order_units`, when it fires over a faction/region selection, then each unit (ascending id) receives the order via `OrderApplier.ApplyActiveOrder` identically to a hand-issued command, and the presentation-only `move_camera`/`cinematic_mode`/`play_vfx` leaves drive presentation with the checksum byte-identical whether they fire or not.
- Given the five new event sources, when their triggering sim events occur, then each is raised deterministically at its tick boundary (ascending id) into the existing `DslEventQueue`, subscribed triggers fire, and `player_chat` — injected via the replicated tick-stamped DSL-event rail — is evaluated on the same tick by every client and reproduces byte-identically through a recorded replay.
- Given a scenario carrying none of the new kinds, when hashed, then its `SimChecksum` differs from before only by the 20→21 bump and its `CanonicalModelHash` only by the 12→13 bump (byte-identical apart from the version integer, omit-when-default discipline); the golden diff is exactly the base re-record with no unexplained churn. **This differential guard — authored before re-recording — is the authoritative proof the re-baseline is correct and gates it above a green suite; if it fails, the run HALTs (corrupt re-baseline) rather than trusting green goldens.** The re-baseline itself (all ~24 world goldens moving + `hero-start-state` moving) is the expected, pre-authorized output of the two bumps and is NOT, by its size, an escalation trigger.

## Design Notes

**Add-a-kind checklist (apply to every new construct — this is the tier-drift contract).** For a new node kind K: (1) const + set membership in `NodeKinds`; (2) sealed node class; (3) `KindOf` arm; (4) `NodePorts` exec/data arms; (5) `NodeBaseJsonConverter` Write + Read + `RejectUnknownProperties`; (6) if K emits data → `ExprCompiler.IsExprNode`; (7) `DslLoopGate` cost arm (container/action); (8) `GraphStructureGate` only if K has non-standard reachability; (9) `TriggerGraph`: flat-representable → `ActionTypes` + `TriggerDefinition` + `FromFlat`; graph-only → `IsGraphOnlyKind` + `ToFlat` fail-closed + `WalkChain` recognition; (10) `ScenarioDirector.ExecuteLeaf`/`ExecuteItem` runtime arm; (11) `NodePaletteFactory` (T3); (12) `NodeKindsLockstepTests.AllKinds` + `MinimalNode`; (13) located validator error for its invalid states; (14) round-trip + behavior test. Skipping any surface is the exact drift the DW ledger flagged (DW ~1728/1876) — the new `IsGraphOnlyKind`⇔`ToFlat` test closes the one drift the lockstep test does not yet cover.

**State reads are the `count()` pattern, extended.** `count(faction)` already exists as `ExprCallFn` → `ExprProgram.OpCode.Count` → `IExprWorld.CountAlive` (`ScenarioDirector.cs:1662`). Each new read follows it exactly: closed fn name, new opcode, new `IExprWorld` method doing an ascending-id `IsAlive` scan (+ `TagsOf`/`CategoryOf`/`RegionStore.Contains`/`ResourceStore` lookup), returning a raw int tagged by `ResultType`. Reads are checksum-neutral; only what a `set_variable` writes back into the folded `DslVarTable` moves the hash.

**Two AlgoVersion bumps, one story (the 7.11 discipline).** `SimChecksum` 20→21 is forced by the ONE new folded store (`_triggerEnabledRuntime`) → re-baseline all world goldens. `CanonicalModelHash` 12→13 is forced by extending the typed graph walk to new kinds → re-record `hero-start-state.golden.txt` only. Event raises and state reads and `order_units` add NO new folded state (they ride existing folded stores) — they need golden RE-RECORDS (per-tick hashes move) but NOT bumps. If another in-flight story races bump 21/13, rebase to the next free integer.

**`enable_trigger` example (deterministic, folded):**
```
// nodeId→execIdx built once in LoadScenario alongside _execs
_triggerEnabledRuntime[execIdx] = false;   // disable_trigger
// sweep gate (both :1101 and :1230):
if (!t.Enabled || !_triggerEnabledRuntime[idx] || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;
// SimChecksum.Compute, before the SimRng block, in exec order:
for (int i = 0; i < enabledRuntime.Length; i++) hash = Mix(hash, enabledRuntime[i] ? 1u : 0u);
```

**PlayerChat carries no string.** Strings never enter the tick. The DSL `player_chat` event params are `(senderFactionSlot:Int, chatCode:Int)`; the UI/presentation maps a typed chat command (e.g. a lobby quick-command or a parsed `-code`) to a bounded code before enqueuing on the replicated rail. Free-text chat continues on the existing reliable side-channel for display only — it is the *code* that is sim-visible and replicated.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green incl. new vocabulary/state-read/random_choice/enable-disable-run/order_units/event/player_chat suites and `NodeKindsLockstepTests` + the new `IsGraphOnlyKind`⇔`ToFlat` test.
- Re-baseline (same commit as the bumps): `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden"` then `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` then re-run without the env var → green.
- `CHIMERA_PERF_CEILING_SCALE=2 dotnet test godot/ProjectChimera.Sim.Tests -c Release` -- expected: determinism + perf gate green (matches CI).
- `grep -n "AlgoVersion" godot/src/Core/SimChecksum.cs godot/src/Core/Definitions/CanonicalModelHash.cs` -- expected: 21 and 13.
- `grep -rniE "using Godot|[^.]\bfloat\b|double |Mathf|FromFloat|DateTime|Environment.Tick" godot/src/Core/ScenarioDirector.cs godot/src/Dsl/ExprProgram.cs` -- expected: no new sim-side hits from this story (presentation delegates stay presentation-side).
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` -- expected: world goldens moved (SimChecksum 21) + `hero-start-state.golden.txt` moved (CanonicalModelHash 13); no other golden churn.

**Manual checks (in-engine, via godot-verify):**
- In the T3 node-graph editor, confirm each new kind appears in the palette, wires with correct port/wire-color typing, and a validator error on an invalid construct (e.g. a `run_trigger` self-cycle) routes onto the offending node. Author a trigger using a state read + `random_choice` + `order_units`, playtest (F5) to observe the ordered units and the branch selection, and confirm a `move_camera`/`play_vfx` leaf drives presentation with no desync (checksum stable). If a two-client path is available, send a chat-code and confirm both clients fire the `player_chat` trigger on the same tick.


## Review Triage Log

### 2026-07-18 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 1, medium 2, low 2)
- defer: 3: (medium 2, low 1)
- reject: 12
- addressed_findings:
  - `[high]` `[patch]` `enable_trigger` could not enable an authored-disabled trigger — both sweep gates ANDed the immutable `t.Enabled` with the runtime mask, which is itself seeded from `t.Enabled` at load, so `enable_trigger` was a dead no-op for exactly the dormant-until-activated triggers it exists to turn on. Dropped the redundant `!t.Enabled` term at both gates (fire on the runtime mask alone); added a positive test proving an authored-`Enabled=false` trigger is turned on same-tick. Golden-neutral.
  - `[medium]` `[patch]` `CanonicalModelHash` `ExprCallNode.Selector` fold was not omit-when-default — `MixStr(empty)` adds an op, shifting every existing `count()`/`distance` scenario's handshake hash beyond the 12→13 version bump and violating the "no-new-kind scenario folds byte-identical apart from the version" invariant. Made the fold conditional on a non-empty selector; added a discipline regression test. Golden-neutral (hero-start-state carries no ExprCallNode).
  - `[medium]` `[patch]` Three sim-event raise sites (`unit_trained`/`ability_cast`/`hero_level`) were only covered at the drain level (feed pushed directly at `factionSlot:0`), leaving the producers' faction-slot/payload arithmetic unverified. Added three end-to-end tests driving the real `BuildingSystem`/`AbilityCastSystem`/`HeroXpSystem` producers and asserting the subscribed trigger fires with the correct slot + payload.
  - `[low]` `[patch]` `random_choice` runtime weight-sum is a 32-bit int that could wrap for a pathological weight set the long-based load gate accepted; added a load-time reject when the long weight-sum exceeds `int.MaxValue`, with a test.
  - `[low]` `[patch]` Base-event buffer sizing omitted `player_chat` drain headroom; added the pending-chat capacity term so the buffer is correct-by-construction.

### 2026-07-18 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (medium 2, low 1)
- defer: 0
- reject: 17: (medium 1, low 16)
- addressed_findings:
  - `[medium]` `[patch]` `run_trigger` bled the CALLING trigger's event dispatch frame into its target: `ExecuteRunTrigger` called `ExecuteTopLevel` without resetting `_frameCount`, so a run target reading `event.<param>` resolved it against the runner's frame (e.g. a `unit_damaged` runner's `amount`) instead of the sentinel — contradicting the method's own "runs like a normal fire" doc (a non-event fire is frame 0). Fixed: save/reset `_frameCount = 0` around the target run (restored in `finally`). Added `RunTrigger_TargetReadsEventParam_AsSentinelZero_NotCallerFrame`. Golden-neutral (no golden scenario carries triggers).
  - `[medium]` `[patch]` `run_trigger` was the one trigger-entry path that skipped the batched-drip suppression every other entry site enforces (`ScenarioDirector.cs:1342/1357/1482`): running a target whose `for_each_batched` continuation row was still ACTIVE re-entered `SnapshotBatched`, resetting the folded `_loopState` row cursor mid-drain (a diamond `run_trigger` graph passes the load cycle-DFS, and a mid-drain re-run double-processes already-drained units). Fixed: mirror the existing `_batchRowOfTrigger`/`RowActive` guard at the whole-trigger boundary in `ExecuteRunTrigger`. Added `RunTrigger_DoesNotReSnapshotAnActiveBatchedRow_NoDoubleProcessing`. Golden-neutral.
  - `[low]` `[patch]` The eight new `CanonicalModelHash` field-fold arms (`order_units`/`move_camera`/`cinematic_mode`/`play_vfx`/`random_choice`/`enable`/`disable`/`run_trigger`) — the v13 handshake-gap closure — had no fold-discrimination test (only `ExprCallNode.Selector` did), and no golden carries these kinds, so an incomplete future fold would ship silently and desync at the lobby. Added `NewNodeKinds_FoldSemanticFields_DiscriminatingAtTheHandshake` (the folds were verified correct on inspection; this is the regression net). Test-only.

## Auto Run Result

Status: done
Blocking condition: none

**Summary.** Completed the trigger-DSL vocabulary: state-read expression built-ins (`entity_hp`/`entity_owner`/`entity_position`/`unit_count_tag`/`unit_count_category`/`player_resource`/`region_unit_count`), a SimRng-drawn `random_choice` weighted container, `enable_trigger`/`disable_trigger`/`run_trigger` (folded runtime enabled mask, load-time cycle reject, runtime depth-cap seatbelt), sim action leaves (`order_units`) and presentation-only leaves (`move_camera`/`cinematic_mode`/`play_vfx`), and five new event sources (`unit_damaged`/`unit_trained`/`ability_cast`/`hero_level` raised at their tick-boundary sites + a replicated tick-stamped `player_chat` rail). Two determinism bumps landed with a golden re-baseline gated by a differential guard: `SimChecksum` 20→21 (new folded `TriggerEnabledStore`) and `CanonicalModelHash` 12→13 (typed graph-walk extension).

**Delivery followed the spec's authoritative commit discipline** (checksum-neutral slice first, then bumps+re-baseline), across five commits: `f66233b` (Arm A, checksum-neutral), `767a2ae` (bump + re-baseline), `3ff5523` (Arm D player_chat rail), `c70639b` (run_trigger depth-cap test), `ab88809` (review fixes).

**Files changed (by area).**
- Vocabulary registry / tier surfaces: `godot/src/Dsl/NodeBase.cs` (new kinds + 5 events + node classes), `NodeBaseJsonConverter.cs`, `ExprProgram.cs`/`ExprCompiler.cs` (7 state-read opcodes + `IExprWorld` + FactionRef/Point result types), `TriggerGraph.cs` (`IsGraphOnlyKind`/`ToFlat`/`WalkChain`/branch ports), `NodePaletteFactory.cs` (T3), `Core/Definitions/DslLoopGate.cs` (cost arms + run-cycle DFS + weight-sum reject), `Core/Definitions/ScenarioValidator.cs` (located rejects), `Dsl/EventDispatchPlan.cs` (5 param schemas), `Dsl/EventBounds.cs` (`MaxRunTriggerDepth`/`MaxRandomChoiceBranches`/`PlayerChatRailCode`/`MaxChatCode`).
- Runtime: `godot/src/Core/ScenarioDirector.cs` (state-read impls, `ExecuteRandomChoice`/enable-disable/`ExecuteRunTrigger`/`ExecuteOrderUnits`, sim-event feed drain + 5-event dispatch, player_chat raise via `TryEnqueueExternalDslEvent`, both sweep-gate fixes, buffer sizing), `Core/TriggerEnabledStore.cs` (new folded store), `Core/DslSimEventFeed.cs` (new transient feed), `Core/SimChecksum.cs` (fold + 20→21), `Core/SimulationLoop.cs` + `Core/Sim/SimulationHost.cs` (thread store + reset), `Core/Definitions/CanonicalModelHash.cs` (v13 walk + omit-when-default Selector), `Combat/DamageResolver.cs`/`CombatSystem.cs`/`ProjectileSystem.cs`/`HeroXpSystem.cs`, `Economy/BuildingSystem.cs`, `Effects/AbilityCastSystem.cs` (raise sites), `Multiplayer/LockstepManager.cs` + `ReplayRecorder.cs` (player_chat rail, VERSION stays 3).
- Tests: `StateReadAndActionLeafTests.cs`, `RandomChoiceEnableRunEventTests.cs`, `SimEventRaiseSiteEndToEndTests.cs`, `ReplayPlayerChatTests.cs`, `TriggerGraphGraphOnlyEquivalenceTests.cs`, `Golden/ReBaselineDifferentialGuardTests.cs` (+ frozen v20 control), `NodeKindsLockstepTests.cs`, version pins.
- Goldens: 24 per-tick world goldens re-recorded (header `checksum_algo_version` only — **data byte-identical**, no scenario carries DSL triggers); `hero-start-state.golden.txt` moved by the 12→13 start-state hash; `ai-active` correctly left as its Windows-only float baseline.

**Review findings breakdown:** 5 patches applied (1 high, 2 medium, 2 low — see triage log), 3 deferred (recorded in `deferred-work.md`: unbounded load-time cycle DFS, `unit_damaged` feed-cap saturation, `ClearForReset` re-apply coverage), 12 rejected (intended semantics / consistent-with-existing-pattern / spec-sanctioned manual checks / defensible intent readings).

**Verification.**
- `dotnet build godot/godot.sln` → 0 errors (11 pre-existing CS8632/CS8604 warnings in untouched files; no new).
- `dotnet test godot/ProjectChimera.Sim.Tests` → 2664 passed, 0 failed, 1 skipped.
- `CHIMERA_PERF_CEILING_SCALE=2 dotnet test -c Release` (CI-equivalent determinism + perf gate) → green.
- Differential guard (the authoritative re-baseline gate) → passed; frozen control independently confirmed to be genuine pre-story v20 bytes (300/300 lines).
- Golden diff independently verified attributable: no per-tick world-golden DATA line moved (headers only); `hero-start-state` moved by the version integer only.
- `SimChecksum.AlgoVersion`=21, `CanonicalModelHash.AlgoVersion`=13; replay `VERSION`=3.
- Matrix Test Audit: all 13 I/O-matrix rows covered by tests that ran and passed (incl. the added run_trigger depth-cap seatbelt and the three producer-driven raise-site tests).

**Follow-up review recommended: true** — the review pass made a HIGH-severity behavioral fix to a headline feature (`enable_trigger`, previously untested) and a determinism-handshake fold-discipline fix (`CanonicalModelHash` Selector), both worth an independent second look.

**Residual risks.**
- The `player_chat` two-client same-tick guarantee is a MANUAL godot-verify check (not headlessly exercisable, per the spec's resolution protocol); only the replay-reproduction leg is headless-covered.
- Ability-graph damage via `EffectContext` (`DamageEffect`) does not raise `unit_damaged` (only the two combat `DamageContext` sites do) — a documented scope bound of this story's raise-site set.
- T2/T3 editor UI for the new kinds (palette appearance, the read-only "edit in graph view" row for graph-only kinds) is only manually verifiable in-engine; the headless suite exercises the sim/DSL surface one layer below.
- Deferred items (load-time cycle-DFS recursion, `unit_damaged` feed-cap saturation) remain open in `deferred-work.md`.

---

### 2026-07-18 — Follow-up review pass (dev-auto)

An independent 4-layer review (adversarial / edge-case / verification-gap / intent-alignment) ran against the full `38b54ee..HEAD` diff. Outcome: **3 patches applied, 0 loopbacks, 17 rejected, 0 deferred.** No new `deferred-work.md` entries (every finding was about this story's own new code — patch or reject — so none was defer-eligible; the two open pre-existing items above are unchanged).

**Patches applied (see the Review Triage Log entry above for detail):**
1. `run_trigger` event-frame isolation — reset `_frameCount = 0` for the synchronous-GOSUB target run (was bleeding the caller's dispatch frame into `event.<param>` reads).
2. `run_trigger` batched-drip suppression — extend the existing `_batchRowOfTrigger`/`RowActive` guard (already enforced at the sweep / re-dispatch / drain entry points, `ScenarioDirector.cs:1342/1357/1482`) to the `run_trigger` entry path, closing a folded-`_loopState` mid-drain re-entry.
3. `CanonicalModelHash` new-kind fold-discrimination test — regression net for the eight v13 field-fold arms (verified correct on inspection; no golden exercises them).

**Files changed this pass:** `godot/src/Core/ScenarioDirector.cs` (`ExecuteRunTrigger` — the two guards), `godot/ProjectChimera.Sim.Tests/Dsl/RandomChoiceEnableRunEventTests.cs` (+2 tests), `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashDeclarationFoldTests.cs` (+1 test). All three patches are **golden-neutral** (no golden scenario carries DSL triggers), so the re-baseline is untouched.

**Verification this pass:** `dotnet build godot/ProjectChimera.Sim.Tests` → 0 errors (pre-existing CS8632 warnings only); `dotnet test` → **2667 passed, 1 skipped, 0 failed** (was 2664 + the 3 new tests, no regressions). The two `run_trigger` tests were confirmed to exercise the fixed paths (they assert the corrected behavior the pre-patch code would have violated).

**Notable rejects (defensible-by-design / verified-correct / cosmetic — surfaced for the human checkpoint, not fixed):**
- `run_trigger` runs its target unconditionally (bypassing the target's conditions / `RunOnce` / cooldown, and re-running a target that is also independently swept) — this is the WC3 `TriggerExecute` primitive the spec explicitly models; the frame + batched guards above close the two *non-semantic* holes without changing that intended re-execution behavior.
- `unit_damaged.amount` truncates `Fixed→Int` — the event's `amount` param is schema-declared `Int`; internally consistent and deterministic.
- `SimChecksum` byte-identity holds only for **zero-trigger** scenarios, not trigger-bearing legacy ones (the intent-alignment auditor's sharpest observation) — an inherent tension in the intent itself (folding new cross-tick enable/disable state *necessarily* moves trigger-bearing goldens); resolved toward the zero-trigger reading and documented in the `SimChecksum` v21 note. `CanonicalModelHash` fully satisfies the broader invariant (omit-when-default + unreached-by-old-kinds).
- Test-coverage gaps left as-is (paths verified correct on inspection; suite green): the custom-event enabled-mask gate (`:1479`), the `SimulationHost` producer-wiring / shared checksum-instance path, `player_resource` crystal branch, `order_units` region filter, `random_choice` port-0 continuation.
- Documentation/naming rot (test method names encoding the pre-bump version, e.g. `AlgoVersion_IsTwelve` asserting 13; the `…story12…` frozen-control filename) — cosmetic; the frozen-control rename was deemed too risky to attempt unattended against the single most safety-critical golden, and left for the human checkpoint.

**Follow-up review recommendation (this pass): false** — the changes are localized to one method (`ExecuteRunTrigger`, ~9 lines) plus three targeted tests, golden-neutral, directly test-covered, and the full suite (including the determinism/perf gates) is green. Remaining items are design-intent confirmations and defensive coverage notes for the existing human golden-diff checkpoint, not defects warranting another automated pass.
