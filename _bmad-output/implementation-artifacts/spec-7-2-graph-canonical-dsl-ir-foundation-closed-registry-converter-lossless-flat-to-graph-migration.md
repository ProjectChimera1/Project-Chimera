---
title: 'Story 7.2: Graph-canonical DSL IR foundation, closed-registry converter + lossless flat-to-graph migration'
type: 'feature'
created: '2026-07-16'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '734b451ad2228ab55242d1177373c2b64dec8682'
final_revision: '7fecf98a1c17d012eb608cc4de3617f998a373c8'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Epic 7 must rebuild the trigger layer onto ONE deterministic, server-validatable graph representation that all four authoring tiers (T1/T2/T3/T4) share, but today the only representation is the flat, polled `TriggerDefinition[]` (position-indexed, no node ids) consumed directly by `ScenarioDirector`. There is no graph IR, no closed-registry graph converter, and no migration path — so every later DSL feature would otherwise land on the flat format and diverge.

**Approach:** Introduce the graph-canonical IR as a new `src/Dsl/` module (`ProjectChimera.Dsl`): an id-keyed `NodeBase` list (persistent integer ids) plus two sparse typed edge-lists (exec + data), with concrete node kinds mirroring the existing closed trigger vocabulary and a dedicated node kind that EMBEDS a D1 `EffectNode` subgraph unchanged (reusing `EffectNodeJsonConverter`, no second executor). Serve it with a hand-written closed-registry `NodeBaseJsonConverter` (modeled exactly on `EffectNodeJsonConverter` — no `[JsonPolymorphic]`, fail-closed on unknown kind / stray / duplicate property, canonical ordering). Add a lossless bidirectional migrator (`TriggerGraph.FromFlat`/`ToFlat`) and make it LIVE by routing `ScenarioDirector.LoadScenario`'s single trigger hand-off through `FromFlat(...).ToFlat()` — an identity lowering that keeps the tick byte-identical while proving the migration round-trips losslessly. This story folds NOTHING into any hash and changes NO on-disk format, so every golden baseline stays byte-identical (like 7.1).

## Boundaries & Constraints

**Always:**
- `src/Dsl/` is pure sim-layer C#: Godot-free, `float`-free (fractional numerics are `Fixed` 16.16, quantized only at the JSON boundary via the registered `FixedJsonConverter`), no `using Godot;`.
- The graph converter is a hand-written `JsonConverter<NodeBase>` over a CLOSED, hardcoded `kind`→type registry — NO reflection, NO `[JsonPolymorphic]`/`[JsonDerivedType]` (forbidden project-wide, AR-22). It fails closed on every branch with a LOCATED error (`"<path>: <reason>"`) naming the offending kind/field, and rejects unknown AND duplicate properties on every node object (mirroring `EffectNodeJsonConverter.RejectUnknownProperties`).
- The graph is a SUPERSET of the flat vocabulary that embeds D1 effect subgraphs unchanged: a `run_effect` node carries an `EffectNode` root (the same runtime object tree abilities use), (de)serialized by delegating to the existing `EffectNodeJsonConverter` — never a reimplementation, never a second effect executor.
- `TriggerGraph.FromFlat(TriggerDefinition[])` → `ToFlat()` is an EXACT round-trip identity for all field values AND array order (triggers, and each trigger's events/conditions/actions). Node ids are assigned deterministically from flat array order; graph→flat reconstruction is driven by ascending node id + edge topology, reproducing the original order.
- Canonical graph serialization is deterministic: nodes emitted sorted by ascending `Id`; exec edges and data edges each emitted sorted by `(Src, SrcPort, Dst, DstPort)`. Graph→JSON→graph is a structural identity round-trip.
- Observable sim outcomes are unchanged: `ScenarioDirector` still executes the (lowered) flat triggers; the full golden-checksum replay suite stays byte-identical with NO `*.golden.txt` edit.

**Block If:**
- Routing `LoadScenario` through `FromFlat(...).ToFlat()` forces any existing golden `*.golden.txt` baseline to change, OR a real on-disk scenario's determinism replay diverges. That means the round-trip is not truly lossless — a real regression. HALT with status `blocked`, condition `flat-to-graph round-trip is not identity`.

**Never:**
- Do NOT add a graph section to `ScenarioData` / change the on-disk scenario JSON format (`triggers` stays the flat `TriggerDefinition[]`; graph JSON is authored/persisted in later stories). Do NOT modify `ScenarioSerializer.Serialize` (no scenario-byte drift, no procedural-map golden-hash move).
- Do NOT fold the IR into `CanonicalModelHash` / `StartStateHash` / `SimChecksum` (the Triggers/Regions handshake gap stays deferred — that is 7.7/later). Do NOT add typed scoped variables, timers as first-class nodes, expression data-flow, custom events, or loops (7.3–7.6).
- Do NOT add a second effect executor, a second trigger validator, or a `Validated<TriggerGraph>` minter (graph validation is Story 7.7; `ScenarioValidator` keeps gating the flat triggers unchanged). Do NOT rewire `ScenarioDirector`'s tick to walk the graph directly (that is 7.3 "verify-to-ship ECA") — for 7.2 the graph is an identity waypoint that lowers back to flat.
- Do NOT execute `run_effect` in the tick this story (no flat action migrates to it; it is the embed-capability seam proven only by serialization round-trip).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Lossless migration | A `TriggerDefinition[]` (multiple triggers, each with events/conditions/actions, incl. `Fixed` fields) | `FromFlat(x).ToFlat()` deep-equals `x` (all fields + order) | No error |
| Node round-trip by id | A `TriggerGraph` with N nodes + exec/data edges | graph→JSON (via `NodeBaseJsonConverter`)→graph reproduces every node by id + all edges | No error |
| Unknown kind | Node JSON with `"kind":"run_script"` | Rejected at parse | Located `JsonException` naming the kind (`...: unknown node kind 'run_script'`) |
| Stray / duplicate property | Node JSON with an unlisted or repeated property | Rejected at parse | Located `JsonException` naming the property |
| Embedded effect subgraph | A `run_effect` node carrying a `sequence`/`damage` `EffectNode` tree | Round-trips through `NodeBaseJsonConverter` (delegating to `EffectNodeJsonConverter`) unchanged | No error |
| Canonical ordering | A graph whose nodes/edges are constructed out of id/tuple order | Serialized JSON lists nodes by ascending id and edges by `(Src,SrcPort,Dst,DstPort)` | No error |
| Empty trigger set | `Array.Empty<TriggerDefinition>()` (every golden) | `FromFlat([]).ToFlat()` == `[]`; director tick unchanged | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` -- THE template for the closed-registry converter: `"kind"` dispatch, `RejectUnknownProperties` (unknown + duplicate), `GuardDepth`, located `JsonException`, `Fixed`/enum delegation. Reused verbatim to (de)serialize the embedded `EffectNode` in `run_effect`.
- `godot/src/Core/Definitions/ContentJson.cs` -- the closed-content `JsonSerializerOptions` seed (name-only enums `allowIntegerValues:false`, `FixedJsonConverter`, `EffectNodeJsonConverter`, `UnmappedMemberHandling.Disallow`) to mirror for `DslJson.Options`.
- `godot/src/Effects/EffectNode.cs` -- the `EffectNode` root (opaque position-addressed tree, NO ids) the `run_effect` node embeds whole; `ProjectChimera.Effects`.
- `godot/src/Core/Definitions/TriggerDefinition.cs` -- the flat POCOs to migrate: `TriggerDefinition` (Name/Enabled/RunOnce/CooldownSeconds:Fixed/Priority/Events/Conditions/Actions), `TriggerEvent`, `TriggerCondition`, `TriggerAction`. The exact field set each node kind must carry 1:1.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- `:778-781` the closed vocab sets `_triggerEventTypes`/`_conditionTypes`/`_actionTypes`/`_operators` — the source of truth for the converter's closed `kind` registry (do NOT modify; read to mirror).
- `godot/src/Core/ScenarioDirector.cs` -- `:104` `_triggers = scenario.Triggers;` is the SINGLE live interception point (`:111` comment already anticipates the persistent-id supersession); route through the IR here.
- `godot/src/Core/Definitions/FixedJsonConverter.cs` -- the one quantization boundary; `Fixed.Raw` is a 32-bit int (16.16). Registered in `DslJson.Options`.
- `godot/ProjectChimera.Sim.Tests/Definitions/ScenarioItemRoundTripTests.cs` -- the in-code `Serialize→Deserialize→Assert.Equal` round-trip test pattern to follow.
- `godot/ProjectChimera.Sim.Tests/Server/CanonicalScenarioTests.cs` -- loads real on-disk scenarios + determinism replay; the behavioral net proving live lowering is tick-identical.

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/NodeBase.cs` -- Define `abstract class NodeBase { public int Id; }` plus the closed concrete node kinds (each sealed, carrying the flat field set 1:1): `TriggerNode` (Name, Enabled, RunOnce, CooldownSeconds:Fixed, Priority), `EventNode` (Kind ∈ event types; Faction, BuildingType?, TimerName?, Amount:Fixed, Count, Operator), `ConditionNode` (Kind ∈ condition types; Faction, BuildingType?, Amount:Fixed, Count, Variable?, RegionId?, Value, Operator), `ActionNode` (Kind ∈ action types; UnitId?, Faction, X:Fixed, Z:Fixed, Count, Text?, Duration:Fixed, TimerName?, TimerSeconds:Fixed, Amount:Fixed, Value, Variable?, SoundId?), `EffectActionNode` (Kind="run_effect"; `EffectNode Effect`). Each node exposes its `kind` discriminator string. -- the id-keyed node vocabulary.
- `godot/src/Dsl/GraphEdge.cs` -- Define `ExecEdge { int Src, SrcPort, Dst, DstPort }` and `DataEdge { int Src, SrcPort, Dst, DstPort; DataWireType Wire }` with `enum DataWireType { Boolean }` (name-only serialized; extended in 7.4). Provide a total `(Src,SrcPort,Dst,DstPort)` comparison for canonical sorting. -- the two sparse typed edge-lists.
- `godot/src/Dsl/TriggerGraph.cs` -- Container: `List<NodeBase> Nodes`, `List<ExecEdge> ExecEdges`, `List<DataEdge> DataEdges`. Implement `static TriggerGraph FromFlat(TriggerDefinition[])`: assign ids by a single ascending counter walking triggers in array order, per trigger emitting TriggerNode, then EventNodes, then ConditionNodes, then ActionNodes; wire exec edges EventNode→Trigger (event-in port) and Trigger→Action0→…→Action_n (linear action chain), and data edges ConditionNode→Trigger (condition-in port, Boolean wire). Implement `TriggerDefinition[] ToFlat()`: order TriggerNodes by ascending Id → array order; per trigger, events = EventNodes with an exec edge into it (ascending id), conditions = ConditionNodes with a data edge into it (ascending id), actions = follow the exec chain out of it; copy each node's fields back to the flat POCOs. Use named port constants (e.g. Trigger EventIn=0, ConditionIn=1, ExecOut=0; Action ExecIn=0, ExecOut=0). Provide `string ToCanonicalJson()` / `static TriggerGraph FromJson(string)` that sort nodes by id and edges by tuple before serializing and use `DslJson.Options`. -- the lossless migrator + canonical serialization surface.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` -- Hand-written `sealed JsonConverter<NodeBase>` mirroring `EffectNodeJsonConverter`: read each node subtree into a transient `JsonDocument`; dispatch on `"kind"` against the CLOSED registry (union of event/condition/action type strings + `"trigger"` + `"run_effect"`); build the matching node class; `RejectUnknownProperties` (unknown + duplicate) per kind; unknown kind → located `JsonException` naming it. `run_effect` reads/writes its `effect` child by delegating to the registered `EffectNodeJsonConverter` (via `JsonSerializer` with `options`). `Write` is the exact inverse, emitting `id` + `kind` + that kind's allow-listed fields; `Fixed`/enum via the registered converters. -- the closed-registry graph converter (AR-22).
- `godot/src/Dsl/DslJson.cs` -- The single `JsonSerializerOptions` seed for the IR (mirror `ContentJson`): name-only `JsonStringEnumConverter(allowIntegerValues:false)`, `FixedJsonConverter`, `EffectNodeJsonConverter`, `NodeBaseJsonConverter`, `UnmappedMemberHandling.Disallow`, `WriteIndented`. -- the determinism + fail-closed chokepoint for graph JSON.
- `godot/src/Core/ScenarioDirector.cs` -- At `:104`, replace `_triggers = scenario.Triggers;` with `_triggers = TriggerGraph.FromFlat(scenario.Triggers).ToFlat();`, routing the sole trigger consumption through the IR as an identity lowering (add a brief comment; keep the rest of `LoadScenario` unchanged). -- makes the migration LIVE with no behavior change.
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphMigrationTests.cs` (new) -- Assert `FromFlat(x).ToFlat()` deep-equals `x` for: empty; a single trigger with multiple events/conditions/actions incl. `Fixed` X/Z/Duration/Amount; multiple triggers preserving array + intra-trigger order; a trigger with zero events/conditions/actions. -- covers the lossless-migration AC + I/O rows.
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphConverterTests.cs` (new) -- Assert graph→JSON→graph reproduces every node by id + all edges; unknown `kind` → located `JsonException` naming it; stray property and duplicate property → located reject; a `run_effect` node carrying an `EffectNode` (`sequence` of `damage`) round-trips unchanged. -- covers the converter/reject/embed ACs + I/O rows.
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphCanonicalTests.cs` (new) -- Build a graph with nodes/edges added out of order; assert `ToCanonicalJson()` emits nodes by ascending id and each edge list by `(Src,SrcPort,Dst,DstPort)`, and that two graphs equal-up-to-construction-order serialize byte-identically. -- covers the canonical-serialization AC.
- `godot/ProjectChimera.Sim.Tests/Server/CanonicalScenarioTests.cs` -- Extend (or add a sibling `Dsl` test) to load each real on-disk scenario and assert `TriggerGraph.FromFlat(model.Triggers).ToFlat()` deep-equals `model.Triggers`. -- proves live lowering is lossless on shipped content (Block-If tripwire).

**Acceptance Criteria:**
- Given the flat `TriggerDefinition[]` from any scenario (including one with events, conditions, `Fixed`-bearing actions across multiple triggers), when it is migrated via `TriggerGraph.FromFlat` and lowered back via `ToFlat`, then the result deep-equals the original in every field and in trigger/event/condition/action order.
- Given a `TriggerGraph`, when serialized through `NodeBaseJsonConverter` and deserialized, then every node is round-tripped by its persistent id and every exec/data edge is preserved, with no use of `[JsonPolymorphic]`.
- Given node JSON whose `kind` is not in the closed registry (or a node object with a stray or duplicate property), when parsed, then a located `JsonException` is thrown naming the offending kind/property and no node is produced.
- Given a `run_effect` node carrying a D1 `EffectNode` subgraph, when it round-trips through the converter, then the embedded subgraph is byte-faithful (delegated to `EffectNodeJsonConverter`) — the IR is a superset embedding effect subgraphs with no second executor.
- Given any graph, when serialized canonically, then nodes appear sorted by ascending id and each edge list sorted by `(Src,SrcPort,Dst,DstPort)`, so two structurally-equal graphs serialize byte-identically.
- Given `ScenarioDirector.LoadScenario` now routes triggers through `FromFlat(...).ToFlat()`, when the full golden-checksum replay suite and the on-disk canonical-scenario determinism replay run, then every `*.golden.txt` baseline is byte-identical (no edit) and every replay matches — the migration is behavior-neutral.
- Given `dotnet build`, when it completes, then `src/Dsl/` compiles with no new warnings and no `[JsonPolymorphic]`/reflection-based polymorphism in the converter.

## Spec Change Log

## Review Triage Log

### 2026-07-16 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 4
- reject: 6
- addressed_findings:
  - `[medium]` `[patch]` `ToFlat`'s `while (true)` action-chain walk had no cycle guard — a hand-built / `FromJson` graph with a cyclic exec chain (A→B→A or self-loop) would spin unbounded (hang/OOM). Unreachable in 7.2's live path (`FromFlat` never emits a cycle) but `ToFlat`/`FromJson` are public and 7.3 walks authored graphs. Added a per-chain `visited` id set (seeded with the trigger head) that throws a located `JsonException` on revisit — fail-closed, never a silent hang. Corroborated by blind + edge-case reviewers.
  - `[medium]` `[patch]` The on-disk lossless tripwire (`OnDiskScenario_FlatToGraphLowering_IsLossless`) was both vacuous (every shipped scenario carries zero triggers → loop ran 0 times) and shallow (compared only header fields + child-array *lengths*, not inner field values), so its "guards the round-trip" promise was unmet; and it re-implemented `FromFlat().ToFlat()` inline rather than exercising the wired `LoadScenario` path. Strengthened it to a full field-level deep-compare, and added `TriggerGraphLiveLoweringTests.LiveLoadScenario_RichTriggers_LowersLosslessly` that drives a rich trigger set through the real `ScenarioDirector.LoadScenario` and deep-compares the director's lowered `_triggers` field-for-field (shared `TriggerFieldAssert` helper). Flagged by blind + verification-gap + intent-alignment reviewers.
  - `[low]` `[patch]` `ToFlat` re-sorted the entire exec-edge list (`ExecEdges.OrderBy`) *inside* the per-action `while` loop — a loop-invariant O(A·E log E) re-sort. Hoisted the sort once per `ToFlat` call into a local; behavior-neutral (deterministic first-match preserved).
  - `[low]` `[patch]` The `NodeKinds` doc-comment claimed it "Mirrors `ScenarioValidator`'s … sets (read, not modified)", but the arrays are DUPLICATED string-for-string (the validator's sets are `private` and cannot be shared) — misleading a future dev into assuming auto-sync. Corrected the comment to state it is a hand-kept copy that must be updated in both places, with 7.7 named as the unification point.

## Design Notes

- **Why goldens can't move:** every golden builds in code with empty trigger state, so `ScenarioDirector.Tick` early-returns and `FromFlat([]).ToFlat()` == `[]`. Triggers are in no hash. So the live lowering is a no-op for goldens regardless of round-trip fidelity; the losslessness risk surfaces only for real on-disk trigger content, caught by the migration unit tests + the on-disk replay. If a golden *does* move, that is a real regression → HALT (see Block If).
- **Flat↔graph mapping (the lossless core):** a trigger is `WHEN events IF conditions THEN actions`. Migrated: EventNode(s) `--exec-->` TriggerNode `--exec-->` Action0 `--exec-->` … (the linear action chain = the "T2 sentence list is a linear projection of an exec-edge chain"); ConditionNode(s) `--data(Boolean)-->` TriggerNode (the gate). Deterministic id assignment (trigger, then its events, conditions, actions, ascending) + id-ordered reconstruction guarantees exact order round-trip. Example ids for two 1-event/1-cond/2-action triggers: T0=0,E=1,C=2,A0=3,A1=4 ; T1=5,E=6,C=7,A0=8,A1=9.
- **kind registry:** the converter's closed set = the existing `_triggerEventTypes` ∪ `_conditionTypes` ∪ `_actionTypes` (from `ScenarioValidator`) ∪ `{"trigger","run_effect"}`. Parse-time rejection of any kind outside this set gives the AC's "unknown kind rejected naming the kind" for both structural and sub-type strings. The existing sub-type semantics stay validated by the unchanged `ScenarioValidator` on the flat form.
- **run_effect is capability, not migration:** no flat action lowers to it (flat actions are leaf `ActionNode`s). It exists so the IR is provably a superset that embeds `EffectNode` subgraphs unchanged; 7.2 proves only serialization round-trip. Its tick execution (reusing `EffectExecutor`, no second executor) is later scope.
- **No `Validated<TriggerGraph>`:** avoid touching `ValidatedMintingTests`' sole-minter allow-list. The converter's fail-closed parse is the only gate 7.2 adds; the authoritative load-time graph validator is Story 7.7.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds; `src/Dsl/` compiles; no new warnings; no `[JsonPolymorphic]`/`[JsonDerivedType]` anywhere in `src/Dsl/`.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green, including the new `Dsl/*` tests (migration/converter/canonical) and every `*Golden*` replay test (baselines byte-identical) and `Server/CanonicalScenarioTests`.
- `git status --porcelain godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty (no baseline changed).
- `grep -rn "JsonPolymorphic\|JsonDerivedType" godot/src/Dsl` -- expected: no matches.

## Auto Run Result

Status: done

### Summary
Delivered the graph-canonical trigger DSL IR foundation as a new `godot/src/Dsl/` (`ProjectChimera.Dsl`) module: an id-keyed `NodeBase` list (persistent integer ids) with sealed node kinds mirroring the flat trigger vocabulary 1:1 plus an `EffectActionNode` that embeds a D1 `EffectNode` subgraph unchanged; two sparse typed edge-lists (`ExecEdge`/`DataEdge`); a hand-written closed-registry `NodeBaseJsonConverter` (no `[JsonPolymorphic]`, fail-closed on unknown kind / stray / duplicate property with located errors, delegating the embedded effect to the existing `EffectNodeJsonConverter`); a `DslJson.Options` seed mirroring `ContentJson`'s fail-closed posture; and a lossless bidirectional migrator (`TriggerGraph.FromFlat`/`ToFlat`) with canonical serialization (`ToCanonicalJson` sorts nodes by id, edges by `(Src,SrcPort,Dst,DstPort)`). The migration is LIVE: `ScenarioDirector.LoadScenario` now routes its sole trigger hand-off through `FromFlat(...).ToFlat()` as an identity lowering — the tick stays byte-identical, nothing folds into any hash, and no on-disk format changed, so every golden baseline is byte-identical.

### Files changed
- `godot/src/Dsl/NodeBase.cs` — `NodeBase` + sealed node kinds (`TriggerNode`/`EventNode`/`ConditionNode`/`ActionNode`/`EffectActionNode`) + the closed `NodeKinds` registry.
- `godot/src/Dsl/GraphEdge.cs` — `ExecEdge`/`DataEdge` readonly structs (total `(Src,SrcPort,Dst,DstPort)` order) + `DataWireType` enum.
- `godot/src/Dsl/NodeBaseJsonConverter.cs` — the closed-registry `JsonConverter<NodeBase>` (fail-closed, located errors, effect-subgraph delegation).
- `godot/src/Dsl/DslJson.cs` — the single graph-IR `JsonSerializerOptions` seed.
- `godot/src/Dsl/TriggerGraph.cs` — container + `FromFlat`/`ToFlat` (lossless) + `ToCanonicalJson`/`FromJson`; includes the review patches (acyclic-chain guard, hoisted per-hop sort).
- `godot/src/Core/ScenarioDirector.cs` — the single live interception: `_triggers = TriggerGraph.FromFlat(scenario.Triggers).ToFlat();`.
- `godot/SimSources.props` — added the `src\Dsl\**` compile glob (the props comment reserved this seam) so the Tier-1 test harness + analyzer gate compile the module.
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphMigrationTests.cs`, `TriggerGraphConverterTests.cs`, `TriggerGraphCanonicalTests.cs` — migration/converter/canonical coverage (all I/O-matrix rows).
- `godot/ProjectChimera.Sim.Tests/Dsl/TriggerGraphLiveLoweringTests.cs` (review patch) — drives the wired `LoadScenario` lowering with a rich trigger set + shared `TriggerFieldAssert` field-level deep-compare.
- `godot/ProjectChimera.Sim.Tests/Server/CanonicalScenarioTests.cs` — the on-disk lossless tripwire, strengthened to a field-level deep-compare.

### Review findings breakdown
- **Patches applied (4):** [medium] `ToFlat` acyclic-chain guard (fail-closed, prevents an unbounded-hang DoS on authored/`FromJson` graphs); [medium] verification strengthening (vacuous+shallow on-disk tripwire → field-level deep-compare + a live-`LoadScenario` rich-trigger losslessness test); [low] hoisted `ToFlat`'s loop-invariant per-hop edge sort; [low] corrected the misleading `NodeKinds` "mirror" comment.
- **Deferred (4) → Story 7.7 (authoritative graph validator):** `ToFlat`/`FromJson` fail-OPEN on malformed arbitrary graphs (duplicate ids / dangling / forked edges / `EffectActionNode` mid-chain — all unreachable in 7.2's `FromFlat`-only live path); weaker edge fail-closed reading (missing `wire` → default; no edge duplicate-key scan; relevant at 7.4); `HashCode.Combine` determinism trap on edges + `ToCanonicalJson` must not be a cross-runtime hash source (CanonicalModelHash landmine class); `NodeKinds` vocabulary duplication vs `ScenarioValidator` with no drift guard.
- **Rejected (6):** live wiring deep-copies triggers / reference-identity change (the intended identity-waypoint mechanism, behavior-neutral); `FromFlat(null)` and null sub-array NREs (no regression — `ScenarioData.Triggers` is non-nullable-defaulted and the validator rejects null `Triggers` before apply); `DslJson` `WriteIndented` vs `ContentJson` "mirror" (deliberate authoring-surface choice; the mirror is about fail-closed posture); hand-enumerated field-list fragility (accepted project pattern, all current fields asserted); `NodeKinds` mutable static arrays (mirrors the existing `ScenarioValidator` pattern, module-internal).

### Follow-up review
`followup_review_recommended: false` — the final pass applied four small, localized patches (a 3-line fail-closed guard, a behavior-neutral sort hoist, a comment fix, and test-only verification strengthening) with no production behavior change, no API/data/security impact, and goldens still byte-identical.

### Verification performed
- `dotnet build godot/godot.sln` → **Build succeeded, 0 errors** (11 pre-existing nullable-context warnings only; zero from `src/Dsl`).
- `dotnet test godot/ProjectChimera.Sim.Tests` → **1814 passed, 1 skipped (pre-existing AR-13 reserved), 0 failed**, including all `Dsl/*` tests (migration/converter/canonical/live-lowering), the strengthened on-disk tripwire, and every `*Golden*` replay.
- `git status --porcelain …/Golden/*.golden.txt` → **empty** (no baseline moved — the live lowering is behavior-neutral).
- `grep -rn "JsonPolymorphic\|JsonDerivedType" godot/src/Dsl` → only doc-comment mentions, no attribute usage.

### Residual risks
- The graph IR is a live identity waypoint in 7.2 (built at load, lowered straight back to flat; the tick still walks flat). Direct graph execution and the authoritative load-time graph validator are Stories 7.3 and 7.7 respectively — the fail-open arbitrary-graph robustness gaps deferred above become live-relevant only when 7.3 begins walking authored graphs, and 7.7 owns their fail-closed rejection.
- `run_effect` embedding is proven by serialization round-trip only (no flat action lowers to it; no tick execution) — exactly as scoped.
- New `.cs` files ship without Godot `.cs.uid` companions (the editor generates them on next open), consistent with prior story commits.
