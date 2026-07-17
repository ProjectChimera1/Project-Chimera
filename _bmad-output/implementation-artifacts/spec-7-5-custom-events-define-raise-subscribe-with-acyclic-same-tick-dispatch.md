---
title: 'Story 7.5: Custom events — define, raise, subscribe with acyclic same-tick dispatch'
type: 'feature'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 1
followup_review_recommended: false
baseline_revision: 'e94fb331fbf338106d2afaa71098ea397aa680e7'
final_revision: '1597e4d'
merge_note: >-
  Re-landed onto master 2026-07-17 via manual merge of the recovered commit 8c36cfe (parent 7-4) across the
  diverged 7-6/7-7/7-8 line: SimChecksum AlgoVersion 17->18 (DslEventQueue fold after DslLoopState), the spec's
  original CanonicalModelHash exclusion inverted to the v10 registry+node-kind fold (the exclusion's
  Triggers-basis dissolved when 7.7's v8 folded the trigger/DSL model), all 25 goldens re-recorded once
  (drift-verified: tick columns byte-identical, only hashes/header moved). Review cycle: 5-lens adversarial
  (determinism / acceptance / regression / blind-hunter / edge-cases), 13 confirmed findings fixed (gate/backstop
  event-param compile parity, array elem/Int typing, per-occurrence batched-row suppression, nested-action param
  maps, dangling event-param root reject, BuildCustomEventTrigger arrayDecls). Tier-1: 2401 passed / 0 failed /
  1 skipped (Windows-gated ai-active compare; its golden was re-recorded on WSL and needs one Windows Tier-1
  pass to confirm).
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Triggers react only to six built-in event kinds in a single once-per-tick priority sweep — creators cannot define named events, raise them from trigger logic, or subscribe handler triggers, so decoupled game-logic modules (FR-25) are inexpressible. The built-in `unit_dies` event carries only the victim's faction slot — no killer/last-hit attribution exists anywhere (killer *faction* passes transiently through `DamageContext`; no attacker entity id is stored), so kill-credit logic (the Sanguine Court "Glut" seam) cannot be authored.

**Approach:** Add a closed custom-event registry to `ScenarioData` (`custom_events`: names + typed `Int/Fixed/Bool` params + per-event allowed-raiser sets; CanonicalModelHash-EXCLUDED on the Triggers basis). Graph-channel-only IR extensions: a `raise_event` node (args fed by expression data edges, optional `next_tick`, authored `raiser` slot) and a `custom_event` subscription kind on `EventNode`. Rewrite the eval loop as: the legacy base sweep (semantics preserved) + a same-tick FIFO work-list drain dispatching each raised occurrence per-occurrence to subscribed triggers in the precomputed total order. Prove the same-tick event-dispatch graph acyclic at load; cap fan-out/depth/transitive cost via named `EventBounds` constants. A→B→A feedback rides `next_tick` through a new checksummed `DslEventQueue` — the story's ONE named, recorded re-baseline (`SimChecksum.AlgoVersion` 16→17, all goldens). Thread killer attribution (attacker entity id + snapshotted killer faction) from `DamageResolver.KillEntity` into per-entity SoA consumed by the director's death diff; expressions gain `event.<param>` payload reads via a new `expr_event_param` node so handlers gate on and consume payloads.

## Boundaries & Constraints

**Always:**
- All new sim/DSL code is Godot-free and float-free (payloads are int raws; `Fixed.Raw` for Fixed params); no `Dictionary` enumeration in tick paths or fold order; InvariantCulture-safe parsing only.
- **Graph-channel-only.** Flat `TriggerDefinition`/`TriggerEvent`/`TriggerCondition`/`TriggerAction` POCOs, their JSON schema, and the validator's flat vocab sets stay frozen. `TriggerGraph.ToFlat` fails closed (located throw) on `raise_event` nodes and `custom_event` EventNodes — never lossy lowering.
- **Closed registry, gated twice.** `ScenarioCustomEvent { Name, Params[{Name, Type}], AllowedRaisers[] }`. Validation (both at the `ScenarioValidator` gate as located `Fail` and mirrored in the failure-atomic `LoadScenario` backstop as located throws): unique non-blank names disjoint from the built-in event-kind set, count ≤ `MaxCustomEvents`; param count ≤ `MaxEventParams`, unique non-blank identifier names (ExprParser identifier rules), types ∈ {Int, Fixed, Bool}; `AllowedRaisers` slots pass `CheckFactionSlot`, no duplicates. `raise_event` nodes: target name declared; each declared param port carries exactly one expression data edge whose inferred type and wire equal the declared param type (missing/forked/extra/mistyped args are located rejects); `Raiser` is −1 (system, default) or ∈ that event's `AllowedRaisers`. `custom_event` EventNodes name a declared event. A trigger subscribing to a custom event, or whose expressions read event params, must have exactly one EventNode (located reject otherwise).
- **DAG proof + cost caps AT LOAD.** Same-tick dispatch graph: edge E1→E2 when a trigger subscribed to E1 has a same-tick `raise_event(E2)` action (`next_tick` raises excluded; triggers with no custom subscription are roots). Cycles rejected with a located error naming the cycle path. Enforce `MaxEventFanOut` (subscribed triggers per event), `MaxEventCascadeDepth` (longest same-tick path), `MaxCascadeOps` (memoized worst-case transitive dispatch ops from any root occurrence) — all named constants in a new `EventBounds` (ExprBounds doc style, corpus-validated dials, errors name the constant).
- **Dispatch semantics.** Base sweep keeps legacy once-per-tick-per-trigger behavior for built-in events — EXCEPT a trigger whose compiled programs read event params dispatches once per matching base occurrence (emission order, i.e. ascending entity id for deaths). Custom-event occurrences (same-tick raises + dequeued next-tick events) always dispatch per-occurrence: work-list FIFO occurrence-major, subscribed triggers in the precomputed total order per occurrence; raises append in execution order; the drain runs after the base sweep, seeded with the next-tick dequeue (dequeued events dispatch before base-sweep raises). Enabled/run-once/cooldown gates re-checked per dispatch: `RunOnce` fires at most once per match even when re-raised; a nonzero cooldown armed at fire suppresses same-tick re-entry. Handlers never nest — a raise defers to the drain; `_vars.Enter/Exit` wraps each dispatch exactly as today.
- **Zero per-tick heap allocation** on the eval/event path: replace `CollectEvents`'s per-tick `new List<FiredEvent>(16)` with buffers preallocated at load (base buffer sized to worst-case emission; work list to a named `MaxSameTickWorkList` capacity with deterministic drop-newest overflow — a documented seatbelt for world-driven volume, distinct from 7.6 fuel). `FiredEvent` widens with `MaxEventParams` int payload slots; no per-tick string construction (names are loaded registry references).
- **`event.<param>` reads.** New closed-registry node `expr_event_param { Name }` (text surface `event.<name>` via ExprParser; new `NodeKinds` kind; converter Read/Write + allow-list; `NodeKindsLockstepTests` extended). Compiles only for triggers subscribed to exactly one event kind declaring that param — the custom registry or the built-in `unit_dies` payload map: `victim` (EntityRef), `killer` (EntityRef, −1 if none), `killer_faction` (FactionRef, −1 if none). Ref-typed params read as Int raw handles (documented — the one sanctioned ref→Int surface). Legal in condition, value, and raise-arg expressions; located reject anywhere else. Runtime stays total and zero-alloc (new PushEventParam opcode reading the current dispatch frame).
- **Killer attribution.** `DamageResolver.KillEntity` is the single write point for new per-entity SoA (`KillerOf` attacker entity id, `KillerFactionOf` snapshotted killer slot); both default −1 in `EntityWorld.Create` (the SoA-recycle trap — a recycled slot must never carry the prior occupant's killer). The projectile path snapshots attacker id at spawn beside the existing `Owner` faction; hitscan and ability self-lethal paths pass what they already know; non-combat destroys leave −1. The director's `_prevFlags` diff stays the `unit_dies` source and reads the SoA for the payload. These arrays are derived attribution state — NOT folded into SimChecksum (same basis as `_prevFlags`).
- **`next_tick` queue.** `raise_event` with `next_tick: true` enqueues into a new `DslEventQueue` (dense preallocated ints only: registry event index, raiser slot, param raws), bounded by `MaxNextTickEventQueue` with deterministic documented drop-newest; dequeued at tick start into the drain seed, then cleared. Folds into `SimChecksum` count-prefixed in enqueue order, after the `DslVarTable` fold and before SimRng — **`AlgoVersion` 16→17**, with the version-history entry naming this story, `VersionStampConsistencyTests.ExpectedSimChecksumAlgoVersion` updated, `SimChecksumCoverageGuardTest` extended (pinned hash + differential coverage of the queue), and ALL `.golden.txt` files re-recorded (`CHIMERA_GOLDEN_RECORD=1`) in the same change — this is the story's named, recorded, expected re-baseline. Wire through `SimulationHost` (construction, `SimChecksum.Compute` param, `EnableChecksums`, `ClearForReset`, `LoadScenario` reset).
- `ScenarioData.CustomEvents` follows the `Variables` persistence pattern (nullable array, `custom_events`, omit-when-null, empty→null at the serializer chokepoint) and is EXCLUDED from `CanonicalModelHash` (AlgoVersion stays 7) — exclusion tests cloned from `CanonicalModelHashDeclarationExclusionTests`.
- Caps are corpus-validated before lock: a test proves a WC3-class fixture (Glut-shaped on-death cascade + a deep-but-legal module chain) loads under the shipped cap values.

**Block If:**
- `CanonicalModelHash` or `StartStateHash` moves for ANY scenario — custom-event declarations are excluded; movement means machinery leaked into the handshake fold. HALT status `blocked`, condition `custom events moved a handshake hash`.
- Any legacy scenario (no custom events, no param reads) changes observable tick behavior — a non-checksum behavior/parity test breaks, or the golden re-record reveals anything beyond the uniform v17 fold change. HALT status `blocked`, condition `event dispatch is not behavior-parity for legacy scenarios`.
- The design needs more than the single sanctioned checksum change (AlgoVersion 16→17). HALT status `blocked`, condition `unsanctioned checksum/hash movement`.

**Never:**
- No loops/`ForEach`/arrays/fuel counter (7.6); no lockstep write rail, `DslEventCommand`, or sim-side runtime raiser enforcement (7.9 — this story enforces allowed-raisers at load only); no custom UI rails (7.8); no structural graph validator beyond what dispatch/compile cannot function without (7.7); no T3 canvas (7.10); no new read-accessor leaves or event breadth beyond the `unit_dies` payload (7.13); no Court Glut faction content — generic seam only.
- No raising built-in event kinds via `raise_event`; no implicit params beyond the `unit_dies` payload map; no dynamic per-player indexing (`name[expr]`); no Point/EntityRef/FactionRef/TimerRef/Array custom-event param types.
- No float/double, `System.Random`, wall-clock; no `[JsonPolymorphic]`; no second executor; no nested (re-entrant) handler execution.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Define + raise + subscribe | Trigger A (built-in event) raises `wave_start(count:Int)` with arg expr `gold + 1`; handler H subscribes, condition `event.count > 2`, action `set_variable` from `event.count * 10` | H fires within the same tick with A's evaluated payload; variables reflect; two headless runs byte-identical | No error |
| Same-tick cycle | H1 handles E1 and raises E2 same-tick; H2 handles E2 and raises E1 same-tick | Rejected at load (gate AND backstop) | Located error naming the cycle (E1→E2→E1) |
| Next-tick feedback | Same A→B→A shape but one edge is `next_tick: true` | Accepted; feedback alternates across ticks; pending queue folds into SimChecksum (checksum differs while an event is pending vs not) | No error |
| Caps | Fan-out > `MaxEventFanOut`; depth > `MaxEventCascadeDepth`; transitive ops > `MaxCascadeOps`; > `MaxEventParams` params; > `MaxCustomEvents` events | Each rejected at load | Located error naming the exceeded constant |
| Run-once re-raise | RunOnce handler; event raised twice same tick and again next tick | Handler fires exactly once per match | No error |
| Cooldown same-tick re-entry | Handler with 1s cooldown; event raised twice same tick | Fires on first occurrence; second suppressed; fires again after cooldown expires | No error |
| Kill credit | Faction-1 unit kills faction-0 unit (hitscan AND projectile kill); handler on `unit_dies` gated `event.killer_faction == 1` | Handler fires, credits via variables; killer id/faction correct on both delivery paths; non-combat `Destroy` yields killer params −1 | No error |
| Mass deaths | 3 units die same tick; one param-reading handler + one legacy (no param reads) `unit_dies` trigger | Param-reading handler dispatches 3× (ascending entity id); legacy trigger fires once (parity) | No error |
| Bad authoring | Raise of undeclared event; `event.ghost` read; arg arity/type mismatch; `Raiser` not in `AllowedRaisers`; param read on a multi-event trigger; duplicate event names; name shadowing `unit_dies` | All rejected at load, gate + backstop | Located errors |
| Queue overflow | More than `MaxNextTickEventQueue` next-tick raises in one tick | Deterministic drop-newest; two runs byte-identical | No crash; documented |
| Zero-alloc | Warmed-up tick with live cascade (raise + dispatch + payload reads) | Zero GC delta on the eval/event path (EffectExecutorBoundsTests pattern) | No error |
| Re-baseline discipline | Full suite after implementation | ALL goldens re-recorded once at v17; `CanonicalModelHash` AlgoVersion 7 and `StartStateHash` fixtures unmoved; behavior/parity tests green unmodified | Any extra movement trips a Block-If |
| IR round-trip | Graph with `raise_event` (+args), `custom_event` subscription, `expr_event_param` | `ToCanonicalJson` → `FromJson` → `ToCanonicalJson` byte-identical | No error |
| Editor | Declare a custom event; author raise + handler via manual form; malformed input (dup name, bad params, bad expression) | Persists via the graph channel (accumulating Merge); fail-closed status messages, no partial persist; raw-IR hatch round-trips | Located messages on the status label |

</intent-contract>

## Code Map

- `godot/src/Dsl/NodeBase.cs` — node POCOs + `NodeKinds` closed registry (`:196-227`; `EventTypes` at `:208`); add `RaiseEventNode`, `EventNode.EventName`, `ExprEventParamNode`, new kind constants + `custom_event` in the graph event set.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — closed-registry converter; Read dispatch (`ReadNode :174`), `RejectUnknownProperties` allow-lists, located `"{path}: reason"` errors; model new branches on existing ones.
- `godot/src/Dsl/TriggerGraph.cs` — port constants (`:26-48`), `FromFlat :62`, `Merge :159`, Build helpers (`:189/:231` — the Godot-free authoring pattern), `ToFlat :304` (add fail-closed 7.5-kind guard), `TriggerExec :428`, `BuildExecutionOrder :457` (total order: Priority desc, id asc), `ToCanonicalJson :545`.
- `godot/src/Dsl/ExprBounds.cs` — the named-cap style to mirror for `EventBounds`.
- `godot/src/Dsl/ExprParser.cs` (`Parse :37`), `ExprCompiler.cs` (`TryCompile :55`), `ExprProgram.cs` (`Eval :102`, `IExprWorld :11`) — extend for `event.<name>` / event-param opcode / dispatch frame.
- `godot/src/Dsl/DslVarTable.cs` — `FoldInto :409` (the count-prefixed fold pattern for `DslEventQueue`), `Enter/Exit :157/:168`.
- `godot/src/Core/ScenarioDirector.cs` — `LoadScenario :131` (failure-atomic locals-then-commit), `CompileExpressionPrograms :205`, `Tick :383`, `CollectEvents :416` (per-tick List alloc to remove; `unit_dies` diff at `:428-438`), `EvaluateTriggers :483`, `EventMatches :545`, `ExecuteActions :633`, `FiredEvent :757`, `SecondsToTicks :517`.
- `godot/src/Core/SimChecksum.cs` — `AlgoVersion = 16 :181` + version history doc; DslVarTable fold at `:495-498`; queue fold goes after vars, before RNG (`:504`).
- `godot/src/Core/Sim/SimulationHost.cs` — wiring (`Vars :152`, director `:153`, `SetEffectRuntime :170`, `EnableChecksums :241`, `ClearForReset :263`); `SimulationLoop.StepOnce` computes checksum post-tick.
- `godot/src/Core/EntityWorld.cs` — SoA arrays + `Create` defaults (A2/recycle rules), `Destroy :1083`.
- `godot/src/Combat/DamageResolver.cs` — `KillEntity :88` (single death choke point), `DamageContext` (add attacker id); `CombatSystem.cs:593-633` hitscan/projectile split (`ProjectileStore.Owner` snapshot at `:610`); `ProjectileSystem` impact site.
- `godot/src/Core/Definitions/ScenarioData.cs` — `Variables :516` persistence pattern for `CustomEvents`; `ScenarioVariable :383`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — declarations blocks (`:377-437`), trigger_graph parse gate (`:462-474`), graph-node switch (`:621-706`), 7.4 consumer-edge pass (`:714-782`), `CheckFactionSlot :1104`, private vocab `:1084-1087` (flat sets stay frozen).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — exclusion doc `:18-42`; AlgoVersion 7 stays.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — dropdown vocab `:66-70`, vars section `:731-799` (clone for events), Persist* graph-channel helpers `:644-719`, raw-IR hatch `:879-928`.
- Tests: `godot/ProjectChimera.Sim.Tests/` — `Dsl/`, `Validation/TriggerValidationTests.cs` (fixtures `:36/:408/:563`), `Validation/CanonicalModelHashDeclarationExclusionTests.cs` (clone), `Golden/GoldenChecksumReplay.cs` (re-record via `CHIMERA_GOLDEN_RECORD=1`), `Golden/SimChecksumCoverageGuardTest.cs`, `Meta/VersionStampConsistencyTests.cs`, `Effects/EffectExecutorBoundsTests.cs:168-198` (zero-alloc pattern).

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/EventBounds.cs` (new) — named caps `MaxCustomEvents=64`, `MaxEventParams=4`, `MaxEventFanOut=16`, `MaxEventCascadeDepth=8`, `MaxCascadeOps=256`, `MaxNextTickEventQueue=64`, `MaxSameTickWorkList=1024` with derivation doc comments (corpus-validated dials, never inline literals).
- `godot/src/Dsl/NodeBase.cs` — add `RaiseEventNode { Name, Raiser=-1, NextTick=false }` (kind `raise_event`), `EventNode.EventName` (string?, JSON `event_name`, used only by kind `custom_event`), `ExprEventParamNode { Name }` (kind `expr_event_param`); register kinds in `NodeKinds` (+`custom_event` in the graph event set) keeping pairwise disjointness; document the sanctioned graph⊃flat vocab divergence at the `:190-194` warning.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — Read+Write branches + allow-lists for the three additions (fail-closed: `raise_event` raiser range-checked like `expr_var.faction`; `custom_event` requires `event_name`).
- `godot/src/Dsl/TriggerGraph.cs` — add `RaiseArgInPort0..3` constants; extend `TriggerExec` (subscribed-custom-event index, param-reading flag, per-action raise-arg expr roots) and `BuildExecutionOrder` to resolve raise-arg edges (exactly-one-per-declared-port; located rejects deferred to compile); `ToFlat` throws located on 7.5 kinds; add Godot-free `BuildCustomEventTrigger(...)` + raise-action support in the Build-helper family (parses arg/condition expression text, wires edges) for editor + tests.
- `godot/src/Dsl/ExprParser.cs` + `ExprCompiler.cs` + `ExprProgram.cs` — `event.<name>` primary (parser → `ExprEventParamNode`); `TryCompile` gains the optional event-param map (name → slot index + declared type; ref types surface Int); new PushEventParam opcode; `Eval` gains the dispatch frame (param raws span/array + count) with total semantics (no frame → 0); reject event-param reads when no single-subscription param map applies.
- `godot/src/Dsl/DslEventQueue.cs` (new) — dense preallocated next-tick queue (event registry index, raiser, `MaxEventParams` raws per entry), enqueue/dequeue-all/clear, deterministic drop-newest at `MaxNextTickEventQueue`, count-prefixed `FoldInto(ref hash, mix)` in enqueue order (DslVarTable pattern), `FoldEmpty`.
- `godot/src/Core/Definitions/ScenarioData.cs` — `ScenarioCustomEvent { Name, Params: ScenarioEventParam[{Name, Type}], AllowedRaisers: int[] }` + `CustomEvents` field on the `Variables` pattern (omit-when-null, empty→null at the `ScenarioSerializer` chokepoint, hash-exclusion doc note).
- `godot/src/Core/EntityWorld.cs` — `KillerOf`/`KillerFactionOf` int SoA arrays, defaulted −1 in `Create` (recycle-safe), doc: written only by `DamageResolver.KillEntity`, read by the director's death diff, not checksum-folded.
- `godot/src/Combat/DamageResolver.cs` + `CombatSystem.cs` + `ProjectileStore.cs`/`ProjectileSystem.cs` — thread attacker entity id: `DamageContext` gains attacker id (−1 unknown); hitscan passes the attacker; projectile snapshots source id at spawn beside `Owner` and passes it at impact; ability self-lethal passes the caster; `KillEntity` writes both SoA fields before `Destroy`.
- `godot/src/Core/ScenarioDirector.cs` — the drain rewrite: preallocated base-event buffer (sized at load: worst-case deaths + buildings + timers + thresholds + match_start) replacing the per-tick List; widened `FiredEvent` payload slots; `unit_dies` payload from the killer SoA; work-list FIFO (seeded with `DslEventQueue` dequeue, appended by same-tick raises, drained after the base sweep, per-occurrence dispatch in trigger total order, gates re-checked per dispatch, drop-newest at `MaxSameTickWorkList`); per-occurrence base dispatch for param-reading triggers only; `raise_event` execution (evaluate arg programs against the current frame, enqueue same-tick or next-tick); extend `CompileExpressionPrograms` into the load-time backstop for ALL 7.5 rules (registry validation, DAG proof + `EventBounds` caps, raise-arg compile, single-subscription rules) staying failure-atomic; implement the DAG/cost proof as a Godot-free routine shared with the validator (home it in `src/Dsl/`, e.g. `EventDispatchPlan`).
- `godot/src/Core/SimChecksum.cs` — fold `DslEventQueue` after `DslVarTable`, before SimRng; `AlgoVersion` 16→17 with a version-history entry naming story 7.5 and the queue fold; `Compute` gains the queue param (nullable → `FoldEmpty`).
- `godot/src/Core/Sim/SimulationHost.cs` (+ `SimulationLoop`) — construct/own `DslEventQueue`, pass to director + `EnableChecksums`/`Compute`, clear in `ClearForReset` and on `LoadScenario`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — `custom_events` declarations block (after timers) building the declared-events map; graph gate: `custom_event`/`raise_event` checks (declared names, arg edges arity/type/wire, raiser membership, single-subscription rules, `event.<param>` compile via the widened `TryCompile`); DAG proof + cascade caps via the shared routine; flat vocab untouched.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — "Custom Events" declaration section (clone of the vars section: name, compact `name:Type` params line, allowed-raisers list; dup/parse refusal fail-closed); `EventKinds` + `custom_event` (declared-event picker), `ActionKinds` + `raise_event` (event picker, next-tick toggle, per-param arg expression fields); persist via the graph channel (accumulating Merge + raw-IR resync); exotic combos route to Raw IR fail-closed.
- `godot/ProjectChimera.Sim.Tests/Dsl/CustomEventDispatchTests.cs` (new) — director-driven: happy path, per-occurrence semantics (mass deaths ×3 vs legacy once), run-once re-raise, cooldown same-tick suppression, next-tick feedback alternation, queue-overflow determinism, drain ordering (dequeued-before-base-raises), two-run byte-identical checksum sequences with live cascades.
- `godot/ProjectChimera.Sim.Tests/Dsl/EventDispatchPlanTests.cs` (new) — DAG proof (cycle located; diamond legal), fan-out/depth/ops cap rejects naming constants, corpus-validation fixture (Glut-shaped cascade + deep-legal chain accepted under shipped caps).
- `godot/ProjectChimera.Sim.Tests/Dsl/` (extend Expr*/converter/lockstep/canonical suites) — `event.<param>` parse/compile/eval matrix (undeclared param, multi-event reject, ref-as-Int reads, total no-frame semantics), converter round-trips + located rejects for the three kinds, `NodeKindsLockstepTests` extension, canonical byte-identical round-trip, `ToFlat` fail-closed guard.
- `godot/ProjectChimera.Sim.Tests/Combat/` (extend) — killer SoA: hitscan/projectile/self-lethal attribution, recycle-safety (spawn over a dead slot reads −1), non-combat destroy −1.
- `godot/ProjectChimera.Sim.Tests/Validation/TriggerValidationTests.cs` (extend) + `CanonicalModelHashCustomEventExclusionTests.cs` (new, cloned from the 7.3 declaration-exclusion suite) — gate rejects for every Bad-authoring matrix row; adding/changing/removing custom events moves neither `CanonicalModelHash` (AlgoVersion 7 pinned) nor `StartStateHash`.
- `godot/ProjectChimera.Sim.Tests/Golden/` + `Meta/` — re-record ALL goldens at v17 (`CHIMERA_GOLDEN_RECORD=1`), update `SimChecksumCoverageGuardTest` (pinned known-state hash + differential mutation covering the queue) and `VersionStampConsistencyTests` (16→17) in the same change; zero-alloc test for the warmed-up cascade tick (EffectExecutorBoundsTests pattern).

**Acceptance Criteria:**
- Given a declared custom event with typed params and an allowed-raiser set, when a trigger raises it same-tick with expression args, then subscribed handlers dispatch within the tick per-occurrence in deterministic total order, gate on `event.<param>` reads in condition expressions and consume them in value expressions, with zero per-tick heap allocation on the eval/event path.
- Given a same-tick dispatch cycle, an over-cap cascade (fan-out/depth/transitive ops), or malformed authoring (undeclared event/param, arg arity/type mismatch, raiser not allowed, multi-event param reader), when the scenario reaches either the `ScenarioValidator` gate or the `LoadScenario` backstop, then it is rejected with a located error (caps name their `EventBounds` constant) and nothing reaches the tick.
- Given A→B→A feedback authored via `next_tick`, when the scenario runs, then feedback alternates across ticks through the bounded `DslEventQueue`, the pending queue folds into `SimChecksum` (AlgoVersion 17), and two headless runs of a seeded scenario with live cascades produce byte-identical checksum sequences.
- Given a unit killed by hitscan or projectile, when its `unit_dies` handler runs, then `event.victim`/`event.killer`/`event.killer_faction` carry correct attribution (−1 for non-combat destruction), a recycled entity slot never inherits stale attribution, and mass same-tick deaths dispatch a param-reading handler once per death while legacy triggers keep once-per-tick behavior.
- Given the full pre-7.5 suite, when the story lands, then `CanonicalModelHash` (AlgoVersion 7) and `StartStateHash` move for no scenario, all behavior/parity tests pass unmodified, the golden suite is re-recorded exactly once as the named v17 re-baseline, and `dotnet build` + `dotnet test` are green with `src/Dsl` still Godot-free/float-free.

## Spec Change Log

## Review Triage Log

## Design Notes

- **Per-occurrence is opt-in by construction:** legacy triggers keep once-per-tick (hard Block-If parity); a trigger that reads event params NEEDS per-occurrence semantics (crediting each kill), and the read is statically visible at compile — no schema flag, no behavior change for anything that exists today. Custom events are new machinery, so per-occurrence is their native semantics.
- **Why the drain defers rather than nests:** `DslVarTable.Enter/Exit` is a single-frame trigger-local reset; nested handler execution would stomp the raiser's locals. FIFO deferral keeps handler execution flat, deterministic, and bounded by the load-proven DAG.
- **Why `MaxSameTickWorkList` exists despite the load proof:** the cost estimator bounds ops per root *occurrence*; occurrence count is world-driven (mass deaths). The capacity cap is a deterministic seatbelt (drop-newest, documented), not runtime fuel (7.6) — the AT-LOAD gate remains the authority over authored structure.
- **Why the queue is its own store:** next-tick events are live cross-tick sim state (non-empty at the checksum boundary) — unlike CombatEventQueue/DeathFeed which are provably drained. Folding it demands the v17 bump; keeping it out of `DslVarTable` keeps declarations-vs-runtime-queue separation clean.
- **Ref params read as Int:** expressions deliberately have no ref algebra (7.4); the payload's EntityRef/FactionRef surface as opaque int handles so handlers can compare/route without new type machinery. FactionRef handles are faction slots; `killer_faction == 1` is the sanctioned kill-credit idiom (dynamic slot indexing stays out — 7.13+).
- **Allowed-raisers now, enforcement split:** the registry field + load-time `raise_event.Raiser` membership check land here; runtime raiser enforcement on the lockstep bus is explicitly 7.9's `ApplyDslEvents`.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` — expected: 0 errors, no new warnings; no `[JsonPolymorphic]`.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all green including the new dispatch/plan/exclusion suites; behavior/parity tests unmodified.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter <per-golden filters>` then rebuild + normal `dotnet test` — expected: all goldens re-recorded once at `checksum_algo_version: 17`, then stable (subsequent `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` empty).
- `grep -rniE "using Godot|[^.]\bfloat\b|double |FromFloat" godot/src/Dsl` — expected: no code hits.
- Determinism: one seeded scenario with live same-tick cascades + next-tick feedback run twice headless — byte-identical `SimChecksum` sequences.

**Manual checks (in-engine, via godot-verify):**
- Declare a custom event in the Trigger Editor, author a raiser and a handler through the manual form, run the match, observe same-tick handler firing and kill-credit on `unit_dies`; malformed declarations/args rejected fail-closed on the status label with no partial persist.
