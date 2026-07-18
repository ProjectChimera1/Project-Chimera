---
title: 'Story 7.10: T3 visual node-graph editor view (additive) over the shared IR'
type: 'feature'
created: '2026-07-17'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: true
baseline_revision: '38c84a81efdc3650a09c55a38b4ee5750dca3576'
final_revision: '6b00d48'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The DSL IR shipped since 7.2 is already graph-canonical (`TriggerGraph` = typed `NodeBase` list + `ExecEdge`/`DataEdge` lists, persistent int node ids, no Godot types), and the `_editor` per-node annotation bag + 4096-byte cap were pre-provisioned for "Story 7.10 node positions" — yet there is no visual node-graph editor. Graph-native constructs (loops, branches, raise-event, custom-event subscriptions, expressions) live only in the opaque `TriggerGraphJson` string and are **invisible** in the T2 sentence editor (`TriggerEditorPanel.RefreshList` enumerates only flat `Triggers[]`); the only way to touch them is a raw-JSON `TextEdit`. FR-28's promise of authoring the same logic at T1/T2/T3/T4 on one representation is unmet at T3.

**Approach:** Add a GraphEdit-based `DslGraphEditorPanel` (a replaceable *view* over the shared IR — no GraphEdit/Godot types enter the IR) modelled on the existing `TechTreePanel`. It renders the graph channel (`TriggerGraph.FromJson(TriggerGraphJson)`) as fully-editable `GraphNode`s with typed exec/data ports (data-wire color = `DataWireType`), a variables side table (reusing `ScenarioData.Variables`), and load-time validator errors routed onto the offending node via `ChimeraValidationBadge`. Node canvas positions persist verbatim in each node's `_editor` bag (hash-excluded by construction). Flat `Triggers[]` render read-only (auto-laid-out) for context and are never migrated into the graph channel — guaranteeing the T2↔T3 round-trip preserves the IR by node-id equality with no content migration. Reciprocally, T2 gains a non-destructive read-only "edit in graph view" fallback row for graph-only constructs.

## Boundaries & Constraints

**Always:**
- **The IR is untouched; T3 is a pure view.** No GraphEdit/Godot type enters `src/Dsl/**` or `src/Core/Definitions/**`. The editor reads/writes only `NodeBase`/`TriggerGraph`/`ScenarioData` values. Swapping GraphEdit for a custom view later must require no IR or other-tier change (AC: swappability — proven by construction: the panel depends on the IR, never the reverse).
- **Zero hash movement.** `CanonicalModelHash.AlgoVersion` stays **11**, `SimChecksum.AlgoVersion` stays **18**, `StartStateHash.AlgoVersion` stays **2**; no golden re-baseline. Node positions live ONLY in the per-node `_editor` bag, which the typed hash fold never reads (`MixGraphNode`). A layout move yields a byte-identical `CanonicalModelHash` — extend the existing `EditorAnnotationEdit_DoesNotChangeCanonicalHash` test with `_editor.x/_editor.y`.
- **`_editor` position schema is minimal and capped.** Positions serialize as small ints under `_editor` (e.g. `{"x":120,"y":-40}`), well under `DslBounds.MaxEditorBagBytes` (4096). Any pre-existing `_editor` keys on a node are preserved verbatim on re-save (read-merge-write, never clobber).
- **Editing targets the graph channel only.** On save the panel canonicalizes the edited graph-channel `TriggerGraph` back to `_ctx.Scenario.TriggerGraphJson` via `ToCanonicalJson()` (in-memory, mirroring how `TriggerEditorPanel` mutates the shared `ScenarioData`; disk persistence stays with the existing scenario-save path — `ScenarioSerializer.SaveToFile` called by the save phases). `_ctx.Scenario.Triggers[]` (the flat channel) is left byte-identical by a T3 session.
- **Typed ports + wire color.** Map each kind's fixed int ports (`TriggerGraph.*Port` + the `NodePorts` legality table) to `GraphNode` slots; data-wire color derives from `DataWireType` via a Godot-free palette (Boolean/Int/Fixed/Point → 4 stable colors); exec edges use one control color. A proposed wire is validated before it is drawn (reuse the `TechTreePanel` connection_request→validate→`ConnectNode` idiom); illegal wires are rejected with a located status message, never drawn.
- **Errors route onto nodes.** Add an additive, Godot-free located-error path (`GraphStructureGate.CheckGraphLocated → IReadOnlyList<GraphNodeError{ NodeId, Message }>`) that reuses the existing checks; the editor badges the offending node. The existing `CheckGraph` string result (used at the load gate) must stay **byte-identical** (load-gate parity — assert it).
- **T2 read-only fallback.** `TriggerEditorPanel.RefreshList` renders a non-destructive read-only row for each graph-only construct (detected via a Godot-free predicate over graph-only kinds — the same classification `ToFlat` fails closed on: `raise_event`, `custom_event`, `expr_event_param`, loops/branches/array actions), with an "Edit in graph view" button that opens the T3 panel. T2 never mutates these constructs.
- **Fail-closed, deterministic.** New IR-adjacent code (`src/Dsl/**`) stays Godot-free and float-free; positions are ints; `Fixed` only via `.Raw`. An unparseable/empty `TriggerGraphJson` opens an empty editable canvas (no throw).

**Block If:**
- The intended set of editable-in-T3 node kinds, the wire-color assignment, or the graph-only/flat-editable tier split contradicts Epic-7 context (lines 40-42, 51, 59) in a way that changes an observable acceptance outcome and the epic gives no basis to choose. (None expected — the split is fixed: graph-native constructs edit in T3, flat ECA edits in T2, each viewable read-only in the other.)

**Never:**
- No migration of flat `Triggers[]` into the graph channel; no rewrite of `Triggers[]` by T3; no graph→flat re-split (`ToFlat` over graph-only kinds fails closed — do not call it on the edited graph). Full flat-channel editing *inside* T3 is out of scope (flat triggers stay editable in T2; deferred).
- No `AlgoVersion` bump, no golden move, no new folded sim state, no change to the load-time validator/gate behavior or signatures (only additive methods).
- No `[JsonPolymorphic]`/reflection node construction; no new node kind; no scripting escape hatch. The palette is exactly the existing `NodeKinds` union.
- No undo/redo this story (the trigger editor has none today; `EditorHistory` wiring is deferred). No `.tscn` — pure C# like every CreationSuite panel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Open editor | Scenario with graph-channel triggers | GraphEdit shows one `GraphNode` per graph node, typed slots, exec + colored data edges, variables side table populated | No error |
| Move node + save | Drag a graph-channel node, save | New `x,y` written to that node's `_editor`; `ToCanonicalJson` persisted to `TriggerGraphJson`; **`CanonicalModelHash` unchanged** | No error |
| Round-trip T2→T3→T2 | Flat ECA triggers authored in T2, opened + saved in T3 | `Triggers[]` byte-identical; node-id equality preserved; NO content-migration write | No error |
| Wire two ports | Drag port→port, edge is legal per `NodePorts` | Edge added to the graph model + drawn | No error |
| Illegal wire | Drag creates a type-mismatched / illegal-port / cycle edge | Rejected before draw; located status message names the reason | Deterministic reject |
| Validator error | Graph has a located structural error (e.g. duplicate id, unreachable expr node) | Offending node badged with the located message via `CheckGraphLocated`; load-gate `CheckGraph` string unchanged | Located, on-node |
| Delete node | Select a graph-channel node, delete | Node + its incident edges removed from model + canvas | No error |
| Flat-derived node | A flat `Triggers[]` trigger shown in T3 | Rendered read-only (distinct style, auto-laid-out), not movable/deletable/rewireable; positions not persisted | No error |
| T2 graph-only construct | Scenario has a `raise_event`/loop/branch in `TriggerGraphJson` | T2 shows a read-only "edit in graph view" row that opens T3; T2 does not mutate it | No error |
| Empty/absent graph | `TriggerGraphJson` null or unparseable | Empty editable canvas; palette available; no throw | Fail-open canvas |
| `_editor` with prior keys | Node already has `_editor` non-position keys | Position merged in; other keys preserved verbatim on re-save | No error |
| Oversized `_editor` | Position write would exceed 4096 bytes/node | Parse-time located reject (existing cap) — positions are tens of bytes, not reachable in practice | Fail-closed |

</intent-contract>

## Code Map

- `godot/src/Dsl/NodeBase.cs` — `NodeBase.Id` (:22), `NodeBase.Editor` (`_editor` bag, :32, pre-provisioned for this story), closed `NodeKinds` registry + graph-only kind sets (:369-418), `NodePorts` legality table (:485). Read-only reference for the editor's node/port model. Add a Godot-free graph-only classification helper here or in a sibling.
- `godot/src/Dsl/TriggerGraph.cs` — `FromJson`/`ToCanonicalJson` (:943/:927), `FromFlat` (:87, read-only flat render), port constants (:26-73). The panel's editable model = `FromJson(TriggerGraphJson)`; save = `ToCanonicalJson()`. Do NOT call `ToFlat` on the edited graph.
- `godot/src/Dsl/GraphEdge.cs` — `ExecEdge`/`DataEdge` + `DataWireType {Boolean,Int,Fixed,Point}` (:15). Source of wire-color mapping.
- `godot/src/Dsl/GraphStructureGate.cs` — existing `CheckGraph → string?` first-fail (:36), node ids embedded in prose. ADD `CheckGraphLocated → IReadOnlyList<GraphNodeError>` (`GraphNodeError{ int NodeId, string Message }`) reusing the same checks; keep `CheckGraph`'s string output byte-identical (single source of truth).
- `godot/src/Dsl/DslBounds.cs` — `MaxEditorBagBytes = 4096` (:82). Position-write cap.
- `godot/src/Dsl/NodeEditorAnnotation.cs` — **NEW, Godot-free** (in the `src/Dsl/**` Tier-1 glob). Read/write `x,y` on `NodeBase.Editor` (merge-preserving other keys, cap-aware). The Godot↔IR position seam, unit-tested.
- `godot/src/Dsl/DataWireColorPalette.cs` — **NEW, Godot-free**. `DataWireType → stable hex/RGB` (4 colors) + exec control color; panel converts to `Godot.Color`. Unit-tested for a stable, distinct mapping.
- `godot/src/CreationSuite/DslGraphEditorPanel.cs` — **NEW, Godot-coupled**. Clone `TechTreePanel` structure: `CanvasLayer`→`PanelContainer`→`GraphEdit` + variables side table + status line + node palette + close. Render both graphs (editable graph-channel + read-only flat), typed slots per kind, colored data wires, error badges, drag-to-wire (validate first), disconnect (`AddValid{Left,Right}DisconnectType(0)`), delete, move→`_editor`, save→`TriggerGraphJson`. `EnsureKitInitialized()` before any `ChimeraComponents.*`.
- `godot/src/Core/Bootstrap/Phases/DslGraphEditorPhase.cs` — **NEW**. `ISetupPhase` mirroring `TechTreePhase`: construct, `AddChild`, `Initialize(scenario/ctx)`. Register after the phase that owns `_ctx.Scenario`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — add a `DslGraphEditorPanel` handle (sibling of `TechTreePanel`, :117).
- `godot/src/Core/MainScene.cs` — add the phase to the phase list; bind an unused hotkey (e.g. `G`) toggling the panel (pattern at :657-662).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — `RefreshList` (:301): add read-only "edit in graph view" fallback rows for graph-only constructs (Godot-free detection predicate) with a button that opens `DslGraphEditorPanel`. Reuse the existing variables-table shape (`RefreshVarsList`, :1005) as the T3 side-table reference. Non-destructive.
- `godot/src/UI/Components/ChimeraValidationBadge.cs` — reuse (`Create`/`ShowError`/`Clear`) to badge nodes.
- `godot/ProjectChimera.Sim.Tests/` — new Tier-1 tests (see Tasks). Extend `Validation/CanonicalModelHashDeclarationFoldTests.cs` (`EditorAnnotationEdit_DoesNotChangeCanonicalHash`, :107) with x/y positions.

## Tasks & Acceptance

**Execution:**
- `godot/src/Dsl/NodeEditorAnnotation.cs` — NEW Godot-free: read/write `x,y` on `NodeBase.Editor`, preserving other `_editor` keys, respecting `MaxEditorBagBytes`. — the position seam.
- `godot/src/Dsl/DataWireColorPalette.cs` — NEW Godot-free: `DataWireType`→stable distinct hex + exec color. — wire-color = type.
- `godot/src/Dsl/GraphStructureGate.cs` — ADD `CheckGraphLocated` returning `GraphNodeError` list; `CheckGraph` string output byte-identical. — on-node error routing, load-gate parity.
- `godot/src/Dsl/*` (NodeBase/TriggerGraph) — ADD a Godot-free `IsGraphOnly(kind)`/`ContainsGraphOnly(graph)` predicate reusing the graph-only kind classification. — T2 fallback detection.
- `godot/src/CreationSuite/DslGraphEditorPanel.cs` — NEW: full GraphEdit render/edit of the graph channel + read-only flat render, typed slots, colored wires, variables table, error badges, palette add, validated wire/unwire, delete, move→`_editor`, save→`TriggerGraphJson`. — the T3 view.
- `godot/src/Core/Bootstrap/Phases/DslGraphEditorPhase.cs` + `SceneContext.cs` + `godot/src/Core/MainScene.cs` — construct/register the panel; hotkey toggle. — wiring.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — read-only graph-only fallback rows + "edit in graph view" open; reuse variables-table shape. — T2 reciprocal.
- `godot/ProjectChimera.Sim.Tests/` — unit-test every Godot-free seam and I/O row that is Tier-1-expressible: (a) `NodeEditorAnnotation` round-trip + other-key preservation + cap reject; (b) layout-move hash-neutrality (extend `EditorAnnotationEdit_DoesNotChangeCanonicalHash` with x/y; assert `CanonicalModelHash.Compute` equal and `AlgoVersion`s unchanged 11/18/2); (c) `DataWireColorPalette` stable distinct mapping for all 4 `DataWireType`; (d) `CheckGraphLocated` returns the correct `NodeId` for a duplicate-id/unreachable-expr graph AND `CheckGraph` string byte-identical to baseline (parity); (e) graph-only predicate classifies `raise_event`/`custom_event`/`expr_event_param`/loop/branch as graph-only and flat ECA as not; (f) graph-channel round-trip `FromJson`→position-edit→`ToCanonicalJson` preserves node ids and leaves semantic content hash-identical. — full coverage at every Godot-free seam.

**Acceptance Criteria:**
- Given a scenario with graph-channel triggers, when the T3 panel is opened, then it renders each graph node as a `GraphNode` with typed exec/data ports (data-wire color = `DataWireType`), a variables side table reflecting `ScenarioData.Variables`, and any located structural validator error badged on the offending node — with no GraphEdit/Godot type added to `src/Dsl/**` or `src/Core/Definitions/**`.
- Given a flat ECA trigger authored in T2, when it is opened and the T3 graph is saved, then `ScenarioData.Triggers[]` is byte-identical (no content migration) and the round-trip preserves the IR by persistent-node-id equality; and a T3 node move persists `x,y` into that node's `_editor` bag such that `CanonicalModelHash.Compute` is byte-identical before vs after the move and `CanonicalModelHash.AlgoVersion`==11 / `SimChecksum.AlgoVersion`==18 / `StartStateHash.AlgoVersion`==2 are unchanged with no golden moved.
- Given the T3 view, when a legal wire is dragged it is validated then drawn, when an illegal wire is dragged it is rejected pre-draw with a located status message, and when a node is added from the palette or deleted the graph-channel model + canvas update consistently and save canonicalizes to `TriggerGraphJson`.
- Given a scenario containing a graph-only construct (`raise_event`/`custom_event`/loop/branch/expression), when the T2 `TriggerEditorPanel` list is shown, then that construct appears as a non-destructive read-only "edit in graph view" row that opens the T3 panel, and T2 never mutates it.
- Given `dotnet build`/`dotnet test`, then everything is green including the new `NodeEditorAnnotation`/`DataWireColorPalette`/`CheckGraphLocated`/graph-only-predicate/hash-neutrality suites; `src/Dsl/**` stays Godot-free and float-free; and no `AlgoVersion` bump, golden move, IR type change, validator-gate behavior change, or out-of-scope (flat-channel-editing-in-T3 / new node kind / undo) work was added.

## Design Notes

- **Why two disjoint rendered graphs, not a merge.** The runtime Merges `FromFlat(Triggers)` with `FromJson(TriggerGraphJson)` (id-offset union) only for *execution*; no code re-splits for *save*, and `ToFlat` fails closed on graph-only kinds. Rendering the graph channel (`FromJson`, editable) and the flat channel (`FromFlat`, read-only, auto-laid-out) as two disjoint `GraphNode` sets (distinct name prefixes) sidesteps all offset/split bookkeeping: save re-canonicalizes ONLY the graph-channel model, `Triggers[]` is never rewritten, and node-id equality + "no content migration" fall out for free. Each construct is fully editable in exactly one tier (graph-native → T3, flat ECA → T2) and read-only-viewable in the other — the coherent interpretation of "full bidirectional editing is the IR-native tier" that the round-trip constraint forces.
- **Ports are bare int indices** with a fixed per-kind layout (`TriggerGraph.*Port` + `NodePorts`); there is no port-object model. Map int port → `GraphNode` slot index per kind. Follow `TechTreePanel`'s GraphEdit workarounds: both `AddValidLeftDisconnectType`/`AddValidRightDisconnectType` (else drag-off never fires `disconnection_request`), self-connection guard, validate-before-`ConnectNode`.
- **`_editor` is hash-safe by construction.** `MixGraphNode`/`MixTriggerGraph` fold only typed fields and never read `_editor`; a green test already proves it. Positions are the intended content (the `NodeBase.Editor` and `DslBounds.MaxEditorBagBytes` docstrings literally name "Story 7.10 node positions"). Merge-preserve any existing `_editor` keys on write.
- **Located errors, additively.** `GraphStructureGate` already bakes node ids into prose but returns first-fail `string?`; adding `CheckGraphLocated` (structured `NodeId`+`Message`) as a *sibling* keeps the determinism-critical load gate byte-identical while giving the editor a machine-readable locator. First located error → badge; if a message has no node locus, show it on the panel status line.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` — expected: 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all green incl. new `NodeEditorAnnotation`/`DataWireColorPalette`/`CheckGraphLocated`/graph-only/hash-neutrality suites.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` — expected: EMPTY (no golden moved).
- `grep -n "AlgoVersion" godot/src/Core/Definitions/CanonicalModelHash.cs godot/src/Core/SimChecksum.cs godot/src/Core/Definitions/StartStateHash.cs` — expected: 11 / 18 / 2, unchanged.
- `grep -rniE "using Godot|[^.]\bfloat\b|double |FromFloat" godot/src/Dsl/NodeEditorAnnotation.cs godot/src/Dsl/DataWireColorPalette.cs godot/src/Dsl/GraphStructureGate.cs` — expected: no hits (Godot-free/float-free).

**Manual checks (in-engine, via godot-verify):**
- Open a scenario with a graph-only construct: press the T3 hotkey → nodes render with typed colored wires + variables side table. Drag a node → save → reopen: position preserved; the scenario's `CanonicalModelHash` unchanged (a cosmetic move does not desync). Drag a legal wire (drawn) and an illegal wire (rejected with a status message). Introduce a structural error → the offending node is badged. In T2, confirm the graph-only construct shows a read-only "edit in graph view" row that opens T3, and editing a flat trigger in T2 then reopening T3 shows it unchanged (read-only).

## Spec Change Log

_No bad_spec loopbacks — the spec was not amended. All review findings were code-level patches against already-specified invariants (typed-wire authoring, validate-before-draw, save honesty, edit-preservation) or intent-supported design decisions (graph-channel-native Reading B)._

## Review Triage Log

### 2026-07-17 — Review pass

Independent four-layer pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff (`38c84a8..working tree`, tracked + untracked). Every escalated finding was verified against the actual code before triage. The determinism spine is intact and confirmed: `CanonicalModelHash.AlgoVersion` 11 / `SimChecksum.AlgoVersion` 18 / `StartStateHash.AlgoVersion` 2 unchanged, zero golden movement, `src/Dsl/**` additions Godot-free/float-free, and the `_editor` hash-exclusion holds by construction (the typed fold never reads the bag). Intent Alignment surfaced the story's central fork — **Reading A** (T3 edits *all* trigger logic, including flat T2 constructs) vs **Reading B** (T3 fully edits the graph channel; flat `Triggers[]` render read-only). The diff implements Reading B; this is **intent-supported** (the epic frames the story as an *additive*, *replaceable view* that *renders/edits the already-graph-canonical IR*, and the two-channel architecture makes flat-in-T3 editing require a forbidden content-migration or a fail-closed `ToFlat` split-back), so no intent-gap loopback — every AC clause is met under Reading B. The escalated defects were all concrete gaps in the graph-channel editing, cleanly patchable in place.

- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 0, medium 4, low 7)
- defer: 3
- reject: 6
- addressed_findings:
  - `[medium]` `[patch]` **Typed data wires (Fixed/Point) were unauthorable and typed sinks mis-wired** — `InferWire` only ever returned Boolean/Int, so a Fixed expression into a Fixed operand was gate-rejected and 2 of the 4 advertised wire colors could never be created (and a wrong, hash-participating `DataEdge.Wire` could be persisted → unloadable scenario). Fixed by extracting a Godot-free `DataWireInference.InferWireType` that derives the wire from the source node's produced type (condition/branch-cond sink → Boolean; `ExprLiteral`→its ValueType; `ExprVar`→declared type; other expr sources via `ExprCompiler.ResultType`); the panel delegates and computes the condition-sink signal from the destination node *kind* (not the port number, which collides with `ActionValueInPort`). Tier-1 tests for all four types added.
  - `[medium]` `[patch]` **Edit-time connection gate masked illegal edges and deadlocked fix-up wiring** — `OnConnectionRequest` compared whole-graph first-fail error *strings* (`after != before`), so an illegal edge was admitted whenever a pre-existing error shadowed it, and a legitimate edge that changed which error sorts first was rejected. Replaced with a new Godot-free `GraphStructureGate.TryValidateNewEdge` that validates the proposed edge alone (endpoints exist, per-kind port legality via `NodePorts`, no exec-out/data-in fork, defined wire); also removes the double gate-walk per drag. Tests for both directions on an already-erroring graph added.
  - `[medium]` `[patch]` **`Save()` persisted a structurally-invalid graph and reported clean success** — now runs `CheckGraphLocated` after canonicalizing and, when invalid, emits a located danger status ("will be rejected at load") instead of a false "Saved" message.
  - `[medium]` `[patch]` **Re-opening the panel silently discarded unsaved edits** — `Toggle`/`Open` unconditionally reloaded from the scenario. Now `ReloadModel` re-parses only when `TriggerGraphJson` actually changed (external T2/raw-IR edits still reload), preserving in-memory topology/position edits across a hide/show.
  - `[low]` `[patch]` **`OnModeChanged` subscribed but never unsubscribed** (leak / `ObjectDisposedException` on scene reload) — added `_ExitTree` with the matching `-=`.
  - `[low]` `[patch]` **T2 read-only fallback listed *every* trigger name, not the graph-only construct** — `RefreshGraphOnlyFallbackRows` now renders one row per node where `TriggerGraph.IsGraphOnly(node)` is true, labeled by its actual kind/name/id.
  - `[low]` `[patch]` **`CapturePositions` truncated positions through an unchecked float→int cast** — now clamps to the int range before rounding (no overflow wraparound on extreme scroll).
  - `[low]` `[patch]` **`DrawEdges` silently dropped an edge into an unrendered port** (invisible yet still persisted) — now surfaces a warning status instead of a silent `continue`.
  - `[low]` `[patch]` **Duplicate node ids opened fail-open with corrupted `g<id>` GraphNode names** — `ReloadModel` now shows a clear danger status on duplicate ids (still opens; the graph is load-gate-invalid regardless).
  - `[low]` `[patch]` **`CheckGraphLocated` node-locus was asserted for only 2 of 13 error paths** — added located-`NodeId` + `Check`-string-parity tests for dangling-exec, exec-in mismatch, data-in mismatch, and forked exec-out families.
  - `[low]` `[patch]` **The layout-move hash-neutrality test only computed `CanonicalModelHash`** — strengthened to also assert `StartStateHash.Compute` byte-identical before vs after the `_editor` position write.

Deferred (3, logged as DW-179/180/181 in `deferred-work.md`): (1) node field/property inline editing in T3 — the story's named editable surface is *wires* + error rendering + positions; configuring a new node's payload stays in T2/raw-IR for now; (2) the in-engine godot-verify checklist for the Godot-coupled drag/wire/disconnect/delete/badge interactions and the T2→T3 open — not driven interactively (the input harness can't do absolute-position mouse drags), the determinism-critical seams are Tier-1-tested (matches the 7.9 DW-178 precedent); (3) the `IsGraphOnlyKind`↔`ToFlat` parity is asserted tautologically — a future graph-only kind (Story 7.13) added to `ToFlat` but not the predicate would silently hide a construct from the T2 fallback.

Rejected (6): the flat-triggers-read-only-in-T3 reading (intent-supported Reading B, not a defect); first-located-error routing (the 7.7 validator is first-fail by architecture; the located structural error IS routed to its node, `-1`-locus errors to the status line); the double-gate-walk perf note (fixed as a side effect of the edit-gate patch; the residual T2-refresh reparse is authoring-time, not hot); `InferWire` ignoring `SrcPort` (no kind exposes two differently-typed data-outs today — latent); `RebuildGraph` omitting `ClearConnections` (mirrors the accepted `TechTreePanel` precedent); the `DataWireColorPalette` default fallthrough being untested (the enum is closed — latent).

### 2026-07-17 — Review pass (follow-up)

Independent four-layer follow-up pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff since `38c84a8` (tracked + untracked), run because the first pass set `followup_review_recommended: true`. Every escalated finding was re-verified against the actual code before triage. The determinism spine re-confirmed intact: `CanonicalModelHash.AlgoVersion` 11 / `SimChecksum.AlgoVersion` 18 / `StartStateHash.AlgoVersion` 2 unchanged, zero golden movement, `src/Dsl/**` additions Godot-free/float-free, `CheckGraph` load-gate string parity green across the whole pre-existing suite. Intent Alignment re-confirmed the intent-supported graph-channel-native reading (Reading B) and found no divergence on the hard constraints; the pass's real findings were concrete gaps between the I/O matrix's stated edit-time behavior and the shipped edit gate, plus regressions-in-waiting in the first pass's own patches. All were patchable in place — 0 intent_gap, 0 bad_spec, no loopback.

- intent_gap: 0
- bad_spec: 0
- patch: 21: (high 0, medium 6, low 15)
- defer: 1: (high 0, medium 1, low 0)
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` **Cycle edges were not rejected pre-draw (matrix row "cycle edge → Rejected before draw")** — `TryValidateNewEdge` checked ports/forks only; an exec cycle (A→B→A) or expression data cycle was admitted and drawn. Added iterative reachability checks to `GraphStructureGate.TryValidateNewEdge` for both edge spaces (exec: src reachable from dst; data: dst among src's transitive producers), with Tier-1 tests for both rejections.
  - `[medium]` `[patch]` **An exec-cyclic graph saved with a clean "Saved" status and was then rejected at load** — the structural gate has no exec-cycle rule (cycles reject later in `TriggerGraph.WalkChain`), so `Save()`'s honesty check missed them. Added additive Godot-free `GraphStructureGate.FindExecCycle` (deterministic colored DFS); the panel's `FirstLocatedProblem()` now feeds both `Save()` and the error badges from gate-errors-then-cycle-scan. Tier-1 tested (cycle located, sound graph null).
  - `[medium]` `[patch]` **Known type-mismatched wires into condition sinks were admitted and silently coerced to Boolean** — an Int-producing source wired into a trigger/branch cond port drew fine and failed only at the load gate. Added `DataWireInference.TryInferSourceType` (explicit UNKNOWN state, never "Int by default"); the panel rejects pre-draw when the source's produced type is KNOWN and non-Boolean, while unknown (work-in-progress) sources stay admissible. Also dissolves the one-port-two-wire-colors divergence for known-typed sources. Tier-1 tested (compiled Fixed source, ConditionNode source, unknown states).
  - `[medium]` `[patch]` **The palette contradicted the Never clause "the palette is exactly the existing `NodeKinds` union"** — a curated 14-kind slice left `custom_event`, `for_each_batched`, `run_effect`, the array actions, and five expression kinds unaddable in T3. Extracted a Godot-free `NodePaletteFactory` deriving the palette from the `NodeKinds` registry (full union, no reflection) with per-kind parse-safe defaults, and a Tier-1 round-trip test (serialize→re-parse per kind). En route this CONFIRMED and fixed a live brick: the old palette's `raise_event` default (`Name=""`) serialized a graph that `FromJson` then rejects — an added-then-saved raise_event made the stored graph channel unparseable (fail-open empty canvas on reopen = channel wipe on next save).
  - `[medium]` `[patch]` **Pure position drags were still lost on hide/show** — the first pass's reopen fix preserved topology but `CapturePositions()` ran only on save/topology ops, so a drag followed by Y-toggle or Play-mode auto-close reverted. `Close()` (and Toggle-off through it) now captures canvas positions into the model before hiding.
  - `[medium]` `[patch]` **The T2 "Edit in graph view" button opened the T3 editor UNDERNEATH the T2 panel** — T2's CanvasLayer is 12, T3's is 9 (TechTreePanel precedent), so the story's headline handoff produced an occluded editor. The button now hides the T2 panel before opening T3.
  - `[low]` `[patch]` **`SetScenario` re-bind would render/save the PREVIOUS scenario's graph** — it rebuilt without reloading and never reset the reload guard (dead code today, a clobber if ever wired). Now drops the in-memory model, clears `_lastLoadedJson`, and reloads.
  - `[low]` `[patch]` **Silent no-op interactions** — a duplicate-wire drag and an unresolvable port drag returned with no feedback; both now show a status message (connect + disconnect paths).
  - `[low]` `[patch]` **Over-cap `_editor` position writes were swallowed with a clean save** — `CapturePositions` now surfaces a per-node warning status (save still never blocks).
  - `[low]` `[patch]` **A non-object `_editor` bag (legal at parse) was silently clobbered by a position write** — `NodeEditorAnnotation.SetPosition` now fails closed with a located `JsonException`, preserving the bag verbatim; Tier-1 tested.
  - `[low]` `[patch]` **`SetPosition(null)` NRE'd while `GetPosition` null-guards** — explicit `ArgumentNullException` for symmetry.
  - `[low]` `[patch]` **`ClampRound(NaN)` cast to garbage** — NaN fails both clamp comparisons; now returns 0.
  - `[low]` `[patch]` **An external T2/raw-IR edit silently discarded unsaved T3 edits on reload** — the external edit still wins (authoritative), but the panel now shows a danger status saying the unsaved edits were replaced.
  - `[low]` `[patch]` **T2 claimed "(no triggers)" while the graph channel held flat-representable (non-graph-only) triggers** — `RefreshGraphOnlyFallbackRows` now renders a summary "N graph-channel node(s) (edit in graph view)" row for that case.
  - `[low]` `[patch]` **T2 rendered nothing for an unparseable graph channel** — now one honest row ("present but unparseable — rejected at load") instead of invisibility.
  - `[low]` `[patch]` **The duplicate-edge check compared full `DataEdge` equality including `Wire`** — a re-drag after a variable's declared type changed would stack a near-duplicate edge into a fan-in port; the exists-check now compares endpoints only.
  - `[low]` `[patch]` **Flat (read-only) nodes were canvas-draggable, against the matrix's "not movable"** — `Draggable = false` on read-only `GraphNode`s (positions were already never persisted).
  - `[low]` `[patch]` **Auto-layout left holes (positioned nodes advanced the grid) and palette adds stacked every 6th node on one diagonal** — grid advances only for auto-laid nodes; adds seed a 6×10 spread.
  - `[low]` `[patch]` **`DataWireInference`'s compiled-source branch (the core of pass-1's medium wire fix) had zero executed tests** — replacing it with `return Int` kept the suite green. Added compiled-Fixed-binary and ConditionNode source tests plus explicit unknown-state tests.
  - `[low]` `[patch]` **`TryValidateNewEdge`'s rejection families (forked exec-out, data fan-in, undefined wire) were untested** — deleting the fork loop kept the suite green. Added one test per family plus the sanctioned condition-in fan-in accept.
  - `[low]` `[patch]` **The story's own tests re-spelled `NodeKinds` literals (the DW-181 drift pattern in miniature)** — now reference `NodeKinds.CustomEvent` directly (internals are visible to the Tier-1 compilation).

Deferred (1, logged as DW-182 in `deferred-work.md`, appended as a NEW entry — existing entries untouched per the orchestrator's ledger rules): the panel-integration halves of this pass's fixes (capture-on-hide, invalid/cycle save status, pre-draw rejection messages, T2-hide-then-open, external-edit warning, T2 coverage rows) are build-verified only and pre-date the DW-180 checklist — one in-engine godot-verify session should cover DW-180 + DW-182 together.

Rejected (8): `CheckGraphLocated` returning a ≤1-element list (first-fail is the 7.7 gate architecture; the intent specifies this exact signature); dangling-edge errors located at the missing endpoint's id (no canvas locus by definition — the Design Notes sanction the status-line fallback the panel applies); the one-port-two-colors recolor divergence as a standalone finding (dissolved by the cond-sink type patch for known sources; the unknown-typed residual is latent and load-gate-covered); the `sprint-status.yaml` done-vs-in-review disagreement (transient orchestrator bookkeeping, consistent after this finalize); the Story 7-9 `.uid` stragglers in the diff (Godot-generated companions of 7-9 files, repo convention — committed with the change set); palette-add id overflow at `int.MaxValue` (unreachable authored input); duplicate-node-id GraphNode auto-rename breaking the port maps (the graph is load-gate-invalid and already gets a loud danger status; render-for-repair posture); the first pass's in-engine smoke-test claim being artifact-less (unverifiable retroactively, superseded by this pass's executed verification + DW-182).

## Auto Run Result

Status: done

**Summary:** Implemented Story 7.10 — the **T3 visual node-graph editor view**, an additive GraphEdit-based *replaceable view* over the already-graph-canonical DSL IR (no GraphEdit/Godot type enters `src/Dsl/**`; the panel depends on the IR, never the reverse). `DslGraphEditorPanel` (modelled on `TechTreePanel`) renders the editable graph channel (`FromJson(TriggerGraphJson)`) plus the read-only flat channel (`FromFlat(Triggers)`, dimmed, auto-laid-out) as two disjoint `GraphNode` sets, with typed exec/data ports, **data-wire color = `DataWireType`**, a variables side table, and load-time structural validator errors routed onto the offending node via `ChimeraValidationBadge`. Node canvas positions persist verbatim in each node's hash-excluded `_editor` bag (`NodeEditorAnnotation`), so a cosmetic layout move yields a byte-identical `CanonicalModelHash` — **no `AlgoVersion` bump, no golden re-baseline** (`CanonicalModelHash` 11 / `SimChecksum` 18 / `StartStateHash` 2 unchanged). Save canonicalizes only the graph channel back to `_ctx.Scenario.TriggerGraphJson`; `Triggers[]` is never rewritten, so the T2↔T3 round-trip preserves the IR by persistent-node-id equality with zero content migration. Reciprocally, the T2 `TriggerEditorPanel` gains non-destructive read-only "edit in graph view" fallback rows for graph-only constructs (`raise_event`/`custom_event`/loops/branches/expressions). The design implements the intent-supported graph-channel-native reading; flat T2 constructs remain editable in T2 (read-only in T3).

**Files changed (production, 9):** NEW `godot/src/Dsl/NodeEditorAnnotation.cs` (`_editor` x/y position seam, Godot-free, merge-preserving, cap-aware), `godot/src/Dsl/DataWireColorPalette.cs` (wire-color-by-type), `godot/src/Dsl/DataWireInference.cs` (source-type wire inference — review patch 1), `godot/src/CreationSuite/DslGraphEditorPanel.cs` (the T3 panel), `godot/src/Core/Bootstrap/Phases/DslGraphEditorPhase.cs` (phase + T2↔T3 wiring); MODIFIED `godot/src/Dsl/GraphStructureGate.cs` (`CheckGraphLocated`/`GraphNodeError` located-error sibling sharing one core with byte-identical `Check` output + `TryValidateNewEdge` — review patch 2), `godot/src/Dsl/TriggerGraph.cs` (`IsGraphOnlyKind`/`IsGraphOnly`/`ContainsGraphOnly` predicate), `godot/src/CreationSuite/TriggerEditorPanel.cs` (graph-only read-only fallback rows), `godot/src/Core/MainScene.cs` (phase registration + `Y` hotkey), `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` (handle + phase-order registration).

**Files changed (tests):** NEW `godot/ProjectChimera.Sim.Tests/Dsl/Story710GraphEditorSeamTests.cs` (position round-trip/other-key-preserve/cap, wire-color distinctness, `CheckGraphLocated` node-locus + `Check` string parity across 6 error families, graph-only classification, graph-channel position-edit id+hash preservation, wire inference for all four types, single-edge validation); MODIFIED `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashDeclarationFoldTests.cs` (layout-move hash-neutrality incl. `StartStateHash`), `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` (order pin).

**Review findings breakdown:** 11 patches applied (4 medium: typed-wire authoring, edit-gate masking/deadlock, save honesty, reopen edit-loss; 7 low: event unsubscribe, T2 fallback labeling, position-cast clamp, invisible-edge warning, duplicate-id guard, `CheckGraphLocated` test coverage, layout-move test strength). 3 deferred (DW-179 node field editing, DW-180 in-engine godot-verify checklist, DW-181 graph-only↔`ToFlat` parity drift). 6 rejected. 0 intent_gap, 0 bad_spec — no loopback.

**Verification performed (independently re-run after patches):**
- `dotnet build godot/godot.sln` → Build succeeded, 0 errors, 0 warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` → **2501 passed, 1 skipped (pre-existing reserved test), 0 failed.**
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` → empty (no golden moved).
- `CanonicalModelHash.AlgoVersion`=11, `SimChecksum.AlgoVersion`=18, `StartStateHash.AlgoVersion`=2 — unchanged (grep-confirmed).
- `src/Dsl/**` additions Godot-free/float-free — grep-confirmed (only doc-comment hits).
- Matrix Test Audit: every I/O row's Tier-1-expressible core is covered by an executed passing test (position round-trip/cap, layout-move hash-neutrality, round-trip node-id equality, error→node locus, graph-only detection); the Godot-coupled GraphEdit interaction rows (open/wire-drag/delete/flat-render/badge-render/T2-row) are inherently outside the `SimSources.props` Tier-1 set and are covered by an in-engine smoke test (panel opens, palette adds typed-port nodes, save succeeds, zero editor errors) + the deferred manual godot-verify checklist (DW-180), matching the 7.9 convention.

**Residual risks:** (1) The Godot-coupled interaction surface (mouse-drag wire/disconnect/delete, on-node badge appearance, the live T2→T3 open) was exercised only via an open/add/save smoke test, not each drag gesture — DW-180 carries the manual godot-verify checklist. (2) Node field/property editing is not yet in T3 (DW-179): a node added from the palette carries default field values and is configured in T2/raw-IR — the story's editable surface is wires, but this limits authoring a complete new construct end-to-end in T3 alone. `followup_review_recommended: true` — the final pass made four medium behavioral fixes on the core authoring flow plus new Godot-free API (`DataWireInference`, `TryValidateNewEdge`) whose panel integration is build-verified only, warranting one independent confirmation, ideally an in-engine per-node wire/badge verify.

---

### Follow-up review pass (2026-07-17, second run)

**Summary:** The recommended independent follow-up review ran as a fresh four-layer pass over the full story diff and confirmed the determinism spine end-to-end, then patched 21 findings (6 medium, 15 low) — all code-level, no intent gap, no spec amendment. The headline fixes: the edit gate now rejects **cycle** edges and **known type-mismatched condition wires** pre-draw (the I/O matrix's until-now-unimplemented rejection classes) and `Save`/badges now surface **exec cycles** (which the structural gate never checks and load-time `WalkChain` rejects); the palette now IS the full closed `NodeKinds` union via a new Godot-free, Tier-1-round-trip-tested `NodePaletteFactory` — which also caught and fixed a real brick where the old palette's `raise_event` default serialized an unparseable graph channel; pure position drags now survive hide/show; and the T2→T3 "Edit in graph view" handoff no longer opens the graph editor underneath the T2 panel.

**Files changed this pass (production):** `godot/src/Dsl/GraphStructureGate.cs` (edge-gate cycle checks + additive `FindExecCycle`; load-gate `Check`/`Evaluate` untouched — full pre-existing parity suite green), `godot/src/Dsl/DataWireInference.cs` (`TryInferSourceType` with an explicit unknown state), NEW `godot/src/Dsl/NodePaletteFactory.cs` (full-union palette + parse-safe per-kind defaults), `godot/src/Dsl/NodeEditorAnnotation.cs` (null guard; non-object-bag fail-closed), `godot/src/CreationSuite/DslGraphEditorPanel.cs` (capture-on-hide, cond-sink pre-draw type reject, save/badge cycle honesty, `SetScenario` rebind fix, status messages for silent no-ops, NaN clamp, flat nodes non-draggable, layout/spread fixes, palette delegation), `godot/src/CreationSuite/TriggerEditorPanel.cs` (unparseable/non-graph-only channel coverage rows; the fallback button hides T2 before opening T3).

**Files changed this pass (tests):** `godot/ProjectChimera.Sim.Tests/Dsl/Story710GraphEditorSeamTests.cs` — +13 tests: compiled-source and ConditionNode wire inference, explicit unknown-state inference, all `TryValidateNewEdge` rejection families incl. exec/data cycle + sanctioned condition-in fan-in, `FindExecCycle`, per-palette-kind construct/kind-match/serialize→reparse round-trip, non-object `_editor` bag fail-closed; `NodeKinds` literals now referenced, not re-spelled.

**Review findings breakdown (this pass):** 21 patches applied (6 medium: pre-draw cycle rejection, exec-cycle save honesty, cond-sink type rejection, full-union palette + raise_event brick, position-drag hide/show loss, T2→T3 occlusion; 15 low). 1 deferred (DW-182 — in-engine verify steps for this pass's panel behaviors, appended as a NEW ledger entry; existing entries untouched). 8 rejected. 0 intent_gap, 0 bad_spec — no loopback; `<intent-contract>` and the load-gate contract untouched.

**Verification performed (re-run after all patches):**
- `dotnet build godot/godot.sln` → 0 errors; no new warnings (pre-existing CS8632 set only).
- `dotnet test godot/ProjectChimera.Sim.Tests` → **2514 passed, 1 skipped (pre-existing reserved test), 0 failed** (+13 over the first pass).
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` → empty (no golden moved).
- `CanonicalModelHash.AlgoVersion`=11 / `SimChecksum.AlgoVersion`=18 / `StartStateHash.AlgoVersion`=2 — grep-confirmed unchanged.
- `src/Dsl/**` additions (incl. new `NodePaletteFactory`) Godot-free/float-free — grep-confirmed (doc-comment hits only).
- Load-gate parity: the entire pre-existing `GraphStructureGateTests` string-contract suite plus the 7.10 parity tests pass unmodified (the gate's `Check`/`Evaluate` core was not touched; all new checks are additive siblings).

**Residual risks (this pass):** the panel-integration halves of these fixes (capture-on-hide, invalid/cycle save status, pre-draw rejection messaging, T2-hide-then-open, external-edit warning, coverage rows) are build-verified only — enumerated in DW-182 for one in-engine godot-verify session together with DW-180. `followup_review_recommended: true` — this pass again introduced review-driven behavioral changes (new gate checks, palette expansion, panel lifecycle changes) that no independent reviewer has seen; the first follow-up pass demonstrably paid for itself (it caught regressions-in-waiting inside pass-1's own patches), so one more independent confirmation of this pass's diff is warranted before the story is treated as settled.
