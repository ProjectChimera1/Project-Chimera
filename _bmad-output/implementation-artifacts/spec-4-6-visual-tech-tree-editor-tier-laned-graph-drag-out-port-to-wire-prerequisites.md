---
title: 'Visual tech-tree editor (tier-laned graph, drag out-port to wire prerequisites)'
type: 'feature'
created: '2026-07-09'
baseline_revision: '3d30ba8fcd68e6d656e18d264d2b3a31953a361f'
final_revision: '5dac906a2759cf87468afea2095fbc5428563a4d'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Creators can only wire `BuildingDefinition.Prerequisites` (Story 4.2's data model) by hand-editing JSON arrays — there is no visual way to design a tech tree, and no prior GraphEdit/graph-UI exists anywhere in this codebase to build one from.

**Approach:** Build `TechTreePanel` on Godot's `GraphEdit`: one `GraphNode` per loaded building, laid out into tier columns computed as longest-path depth over `Prerequisites` (never authored/persisted — always recomputed). Dragging an out-port onto another node appends the source building's id to the target's `Prerequisites`, validated inline by a new single-edge reuse of Story 4.2's own cycle DFS (`TechTreeValidator`), then persisted through the existing `FactionWriter.SyncFactionBuildings` path (already round-trips `prerequisites` — no writer change needed). Selecting a node opens the existing `BuildingCardPanel` (Story 4.5) right-dock inspector.

## Boundaries & Constraints

**Always:** Presentation-layer only — never touches `EntityWorld`/sim arrays/`BuildingSystem`/`TechTreeChecker`'s runtime consumption/checksums; this story only edits `prerequisites` data that 4.2's runtime already gates on unchanged. Every edge add/remove writes through `FactionWriter.SyncFactionBuildings` with the same self-check-reload-then-atomic-`File.Move` sequence `BuildingCardPanel.Edit.cs:PersistSync` uses. An edge that would create a self-reference or cycle is rejected inline, before the visual connection is drawn or any data mutated, using a message textually identical to what Story 4.2's import-time lint would produce for the same graph shape (same DFS code path, reused not reimplemented). Node tier/lane positions are always computed fresh from the current `Prerequisites` graph (deterministic, stable sort by id within a tier) — never read from or written to a persisted field, so reload always redraws identically. Selecting a node calls a new public `BuildingCardPanel.SelectAndShow(BuildingDefinition)` to open the shared inspector.

**Block If:** This is the first GraphEdit/GraphNode usage anywhere in the codebase (confirmed zero prior usage). If `connection_request`/`disconnection_request`/`node_selected` do not fire as documented in this project's Godot 4.6.2 build during initial smoke-testing, HALT with blocking condition `GraphEdit connection signals unavailable or behave unexpectedly` rather than building a custom hit-test/drag system out of this story's scope.

**Never:** Never add research nodes or wire the graph to `ResearchDefinition`/`ResearchSystem` (Story 4.9's job, extending this same graph later). Never modify `TechTreeChecker`'s runtime resolution or `TechTreeValidator.Validate`'s existing signature/return type (11 passing tests depend on it) — add a new method, don't retrofit. Never persist a node's dragged position. Never let `TechTreePanel` mutate `_faction.Units` or any field `BuildingCardPanel` doesn't already own.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid edge | Drag building A's out-port, drop on building B (no cycle) | B's `prerequisites` gains A's id, immediately persisted; edge renders | None |
| Self-edge | Drag A's out-port, drop back on A | Rejected inline, nothing written | Status-line error: located cycle message naming A |
| Cycle edge | Drop would close a cycle (e.g. A already (in)directly requires B) | Rejected inline, nothing written | Status-line error, identical wording to the 4.2 import-lint cycle message for that chain |
| Duplicate edge | A already in B's `prerequisites`, dropped again | No-op on data (no duplicate array entry); edge still drawn | None |
| Delete edge | Creator deletes an existing edge | Source id removed from target's `prerequisites`, immediately persisted | None |
| Node select | Creator clicks a building node | `BuildingCardPanel.SelectAndShow` opens, bound to that building | None |
| Reload | Panel closed and reopened (or faction reloaded) | Graph rebuilds identical nodes, tiers, and edges from current `prerequisites` data | None |
| New building added elsewhere | Creator adds a building via `BuildingCardPanel` while tree is open, then reopens/toggles the tree | New node appears at its recomputed tier on next rebuild | None |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/TechTreeValidator.cs` -- extract cycle DFS into a shared private helper; add `ValidateProposedEdge`.
- `godot/src/Core/Definitions/TechTreeLayout.cs` (new) -- pure tier computation, Godot-free.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- add `SelectAndShow`.
- `godot/src/CreationSuite/TechTreePanel.cs` (new) -- GraphEdit shell, layout, connection handling, persistence.
- `godot/src/Core/Bootstrap/Phases/TechTreePhase.cs` (new) -- `ISetupPhase` wiring.
- `godot/src/Core/Bootstrap/SceneContext.cs:110-113` -- publish point.
- `godot/src/Core/MainScene.cs:408-409,587-594` -- phase registration + `R` hotkey.
- `godot/resources/data/factions/_buildingcard_sample.json` -- reused fixture for the `/godot-verify` manual pass.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` -- extend.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeLayoutTests.cs` (new).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/TechTreeValidator.cs` -- Extract the existing cycle-DFS block out of `Validate` into a private `static string? DetectCycle(FactionDefinition def)` with unchanged logic/wording; `Validate` keeps calling it exactly as before (no behavior change, existing 11 tests untouched). Add `public static string? ValidateProposedEdge(FactionDefinition def, string sourceId, string targetId)`: if `sourceId == targetId`, return a located self-cycle message in the same format `DetectCycle` uses; otherwise find `targetId` in `def.Buildings`, temporarily append `sourceId` to its `Prerequisites` (restore in a `finally`), call `DetectCycle`, and return its result (null = no cycle). -- gives the editor a byte-identical rejection message to the 4.2 import lint (closes deferred-work DW-58 without a breaking tuple retrofit) with zero duplicated cycle logic.
- `godot/src/Core/Definitions/TechTreeLayout.cs` (new) -- `public static Dictionary<string,int> ComputeTiers(IReadOnlyList<BuildingDefinition> buildings)`: tier(b) = 0 when `Prerequisites` is null/empty/fully-unresolvable, else `1 + max(tier(p))` over prerequisite ids that resolve to a building in the list (unresolvable ids skipped defensively, never throw). -- the single deterministic layout source the panel and its tests both use; no tier field is ever authored or persisted.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- add `public void SelectAndShow(BuildingDefinition building) { _panel.Visible = true; GoToBuilding(building); }`. -- the hook `TechTreePanel` needs to open the shared inspector on node selection without duplicating `GoToBuilding`'s index lookup.
- `godot/src/CreationSuite/TechTreePanel.cs` (new) -- `CanvasLayer { Layer = 9 }` (below `BuildingCardPanel`'s 13, so the narrow inspector floats on top when both are open) → `PanelContainer` anchored `FullRect` with an inset margin (a graph needs canvas space, unlike the narrow 480px right-dock panels) → title row ("Tech Tree Editor" + Close) → a `GraphEdit` filling the remaining space. `Initialize(FactionDefinition? faction, GameState gameState, string factionJsonPath, BuildingCardPanel inspector)`. `Toggle()`/`Close()`/`OnModeChanged` mirror the other panels' Edit-mode-only visibility, calling `RebuildGraph()` on becoming visible. `RebuildGraph()`: clear existing `GraphNode`s, compute tiers via `TechTreeLayout.ComputeTiers`, create one `GraphNode` per `_faction.Buildings` entry named `Name = building.Id` with one input + one output slot enabled (`SetSlot(0, true, 0, colorIn, true, 0, colorOut)`), `PositionOffset` from `(tier * TIER_SPACING, laneIndex * LANE_SPACING)` (lane index = stable ascending-id order within a shared tier), then `ConnectNode(prereqId, 0, building.Id, 0)` for every id in `building.Prerequisites` that resolves to a loaded building. `connection_request(fromNode, fromPort, toNode, toPort)`: resolve both ids, call `TechTreeValidator.ValidateProposedEdge`; non-null → show the message on the status line and do not connect; null and edge not already present → append `sourceId` to the target's `Prerequisites`, persist (below), `ConnectNode`; null and already present → `ConnectNode` only (no data change). `disconnection_request`: remove `sourceId` from the target's `Prerequisites`, persist, `DisconnectNode`. `node_selected(node)`: resolve the building by `node.Name` and call `inspector.SelectAndShow(building)`. Persistence: mirror `BuildingCardPanel.Edit.cs`'s `PersistSync` sequence (write via `FactionWriter.SyncFactionBuildings` to a `.tmp` file, self-check by reloading through `FactionDefinition.LoadFromFile`, then atomic `File.Move`) — duplicated locally rather than shared, matching this codebase's existing per-editor mirroring precedent (4.5 mirrored, rather than extracted, 3.3/3.4's sequence). -- the full editor surface.
- `godot/src/Core/Bootstrap/Phases/TechTreePhase.cs` (new) -- `ISetupPhase` mirroring `BuildingCardPhase.cs`: constructs `TechTreePanel`, `_ctx.Scene.AddChild`s it, calls `Initialize(_ctx.FactionDef, _ctx.GameState, factionPath, _ctx.BuildingCardPanel)`, publishes `_ctx.TechTreePanel`. -- bootstrap wiring; must register after `BuildingCardPhase` in `MainScene.cs`'s phase list so `_ctx.BuildingCardPanel` already exists.
- `godot/src/Core/Bootstrap/SceneContext.cs:110-111` -- add `public CreationSuite.TechTreePanel TechTreePanel = null!;  // Story 4.6 (TechTree phase)` alongside the other panel fields. -- publish point.
- `godot/src/Core/MainScene.cs:408-409` -- add `new TechTreePhase(_ctx),` after `BuildingCardPhase` in the phase list. `:587-594` -- add `else if (key.Keycode == Key.R) { _ctx.TechTreePanel.Toggle(); GetViewport().SetInputAsHandled(); }` with a comment noting `R` is unused anywhere in `src/` or `project.godot`'s `InputMap` (verified; `T` is already claimed by `TerrainBrush`/`SelectionSystem`). -- Edit-mode-only open hotkey.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` -- extend: `ValidateProposedEdge` self-edge rejected with the located message; a proposed edge closing a 2-/3-node cycle rejected with wording identical to what `Validate` produces for the equivalent authored graph; a valid non-cyclic proposed edge returns null; `Validate`'s own existing tests still pass unchanged (proves the `DetectCycle` extraction is behavior-preserving). -- proves the reused DFS is truly identical, not just similarly worded.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeLayoutTests.cs` (new) -- `ComputeTiers`: no-prerequisite buildings all tier 0; a linear A←B←C chain tiers 0/1/2; a diamond (two tier-1 buildings both prerequisite to one tier-2 building) resolves via max, not sum; an unresolvable prerequisite id is skipped without throwing. -- proves the layout algorithm the panel and manual verification both depend on.

**Acceptance Criteria:**
- Given the Tech Tree Editor open (`R` key, Edit mode) showing every 4.5-authored building as a node in tier-laned columns, when the creator drags a building's out-port and drops it on another building node with no resulting cycle, then the target's `prerequisites` array is updated and immediately persisted to the faction JSON, the edge renders, and selecting either node opens `BuildingCardPanel` bound to that building.
- Given a drop that would create a cycle or is a self-edge, when the creator releases it, then the connection is rejected inline with a message matching the 4.2 import-lint's cycle wording, and no on-disk `prerequisites` array changes.
- Given an existing edge, when the creator deletes it, then the source id is removed from the target's `prerequisites` array on disk and the edge disappears.
- Given a tech tree authored and saved, when the panel is closed and reopened (or the faction reloaded), then the graph redraws with the same nodes, tiers, and edges, and `BuildingCardPanel`'s raw-JSON pane shows the matching `prerequisites` arrays.
- Given a saved tech tree, when a scenario loads that faction and a gated unit/building is attempted, then `TechTreeChecker.AreMet`/`FirstMissing` (unchanged, Story 4.2 runtime) gates exactly what was drawn, and golden checksums stay byte-identical (presentation-only story, zero sim/runtime code touched).

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-09 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 4: (high 0, medium 1, low 3)
- reject: 14: (high 0, medium 0, low 14)
- addressed_findings:
  - `[low]` `[patch]` `TechTreeValidator.ValidateProposedEdge`'s self-edge case returned a hand-formatted string instead of running through the shared `DetectCycle` DFS, risking future wording drift from every other cycle-rejection message. Removed the special-cased branch so a self-edge now resolves through the same target-lookup + temporary-mutation + `DetectCycle` path as every other proposed edge; updated `ValidateProposedEdge_SelfEdge_RejectedWithLocatedCycleMessage` (renamed `..._RejectedWithWordingIdenticalToValidate`) to cross-check against `Validate()` on an authored equivalent, mirroring the existing 2-/3-node cycle tests. Flagged by the Blind Hunter and Verification-Gap review layers (same finding, deduplicated).
  - `[medium]` `[patch]` `TechTreePanel.OnConnectionRequest`/`OnDisconnectionRequest` mutated `target.Prerequisites` in memory before calling `Persist()`, but never rolled the mutation back if `Persist()` failed (e.g. a transient disk I/O error) — leaving in-memory state permanently diverged from disk until a full process restart, with the next `RebuildGraph()` drawing an edge (or omitting one) that was never actually saved. Both handlers now capture the pre-edit `Prerequisites` value and restore it if `Persist()` returns false. Flagged by the Edge Case Hunter review layer.

Verified-false findings (rejected after checking against the actual code, not merely the reviewers' read of the truncated diff): the `R`-hotkey handler IS already gated by the pre-existing `if (_ctx.GameState.Mode != GameMode.Edit) return;` guard in `MainScene.cs:549` (Blind Hunter's claimed missing Play-mode guard, and its dependent "live-mutation risk" finding, do not hold); `FactionDefinition.LoadFromFile` always throws `InvalidOperationException` on any validation failure rather than silently returning null, so `Persist()`'s self-check cannot "pass when it shouldn't" (Blind Hunter); every building/unit `id` is already restricted at import time to `[a-z0-9_]` and duplicate-checked by `UnitDefinitionValidator`/`BuildingDefinitionValidator`, so `GraphNode.Name = b.Id` cannot encounter an illegal-for-Godot or duplicate name in practice (Blind Hunter + Edge Case Hunter); dangling/unresolvable prerequisite ids are likewise structurally unreachable once a faction has passed the same `Validate()` gate to load at all (Blind Hunter); the `.uid` sidecar file additions are explained, harmless Godot-editor-generated metadata for pre-existing scripts (already documented in the implementing subagent's own report), not stray/unrelated artifacts (Blind Hunter); `AddChild`-then-`Initialize` ordering mirrors `BuildingCardPhase`'s identical, already-shipped (Story 4.5) pattern (Edge Case Hunter); and AC2's "invalid reference" clause is satisfied by construction since every graph node already corresponds to an already-validated loaded building (Intent Alignment Auditor, who themselves called this "defensible"). Additional low-value/out-of-scope findings rejected: redundant O(n) lookups at a scale (single-digit-to-dozens buildings per faction) where this is inconsequential; no minimap/zoom-to-fit/search (explicitly out of scope per epics.md's AC text, which only asks for a tier-laned graph); concurrent-`Persist()`-race and mid-drag-`RebuildGraph`-reentrancy scenarios that Godot's single-threaded, serial input dispatch cannot actually produce; and a `Persist()`-target-file-doesn't-exist scenario unreachable given `TechTreePhase`'s path resolution (shared unchanged with `BuildingCardPhase`) always pointing at an existing file.

Deferred (pre-existing/systemic, not caused by this story — see `deferred-work.md` DW-73..76): no automated interaction/round-trip test coverage exists for any CreationSuite editor's Godot-Control layer, including this one (DW-73); `GameState.ModeChanged` is subscribed with no unsubscribe across `BuildingCardPanel`/`UnitCardPanel`/`TriggerEditorPanel` and now `TechTreePanel` too (DW-74); no undo/redo exists for tech-tree edge edits, unlike sibling editors (DW-75, low severity — recoverable by re-dragging); and CanvasLayer numbers across every CreationSuite panel are hardcoded with no shared registry, with duplicates already pre-existing (DW-76).

## Design Notes

**Why a new `ValidateProposedEdge` method instead of retrofitting `Validate`'s return type:** deferred-work DW-58 flagged that `Validate` returns bare strings where a future editor consumer might want located `(FieldPath, Message)` tuples. Retrofitting the signature would touch `FactionDefinition.LoadFromFile`'s call site and all 11 existing `TechTreeValidatorTests`. This story's rejection surface is a status-line message, not a per-field badge, so a purpose-built string-returning method sharing the exact same `DetectCycle` helper closes DW-58 in spirit — identical wording guaranteed by sharing code, not just convention — with zero risk to already-shipped, tested code.

**Why tier/lane positions are always recomputed, never persisted:** no `tier`/`position` field exists in `BuildingDefinition`'s schema, and the acceptance criteria only require tiers/edges to redraw identically on reload — a deterministic pure function (`ComputeTiers`) guarantees that with no new persisted field or migration. A creator dragging a node mid-session is a transient visual convenience that reverts to the algorithm's layout on next open.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean (first `GraphEdit` usage in the project).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including new/updated `TechTreeValidator`/`TechTreeLayout` tests.
- `git diff --stat godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty (presentation-only story, zero sim/checksum touch).

**Manual checks (if no CLI):**
- `/godot-verify` against `_buildingcard_sample.json`: open the Tech Tree Editor with `R`, confirm nodes render tier-laned matching current `prerequisites`; drag a valid edge and confirm the target's `prerequisites` updates on disk and in `BuildingCardPanel`; attempt a self-edge and a cycle edge, confirm inline rejection with no file change; delete an edge and confirm removal; close/reopen the panel and confirm identical redraw; select a node and confirm `BuildingCardPanel` opens bound to it; then `git checkout` the fixture to restore it.

## Auto Run Result

Status: done

**Summary:** Built the Visual Tech Tree Editor (`TechTreePanel`, first `GraphEdit`/`GraphNode` usage in this codebase): one node per building laid out into tier columns by a new pure `TechTreeLayout.ComputeTiers`, drag out-port → in-port to append a prerequisite (validated inline via a new `TechTreeValidator.ValidateProposedEdge`, reusing Story 4.2's cycle DFS byte-for-byte), drag-off-port to remove one, node selection opens the existing Story 4.5 `BuildingCardPanel` inspector via a new `SelectAndShow` hook. Persists through the existing `FactionWriter.SyncFactionBuildings` path (no writer change needed). `R` toggles it in Edit mode.

**Files changed:**
- `godot/src/CreationSuite/TechTreePanel.cs` (new) — the editor: shell, graph (re)build, connection/disconnection/node-selection handlers, persistence.
- `godot/src/Core/Definitions/TechTreeLayout.cs` (new) — pure, Godot-free tier computation.
- `godot/src/Core/Definitions/TechTreeValidator.cs` — extracted `DetectCycle` from `Validate`; added `ValidateProposedEdge` (single-edge reuse); self-edge case routes through the shared DFS (review patch).
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` — added public `SelectAndShow(BuildingDefinition)`.
- `godot/src/Core/Bootstrap/Phases/TechTreePhase.cs` (new) — `ISetupPhase` wiring, registered after `BuildingCardPhase`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — added `TechTreePanel` publish field.
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` + `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — added `"TechTree"` to the canonical phase-order array and its test mirror.
- `godot/src/Core/MainScene.cs` — registered `TechTreePhase`; added `R` hotkey (Edit-mode-only, existing guard).
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` — 5 new tests for `ValidateProposedEdge`, one updated post-patch to cross-check self-edge wording against `Validate()`.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeLayoutTests.cs` (new) — 6 tests for `ComputeTiers`.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-73..76.
- Several pre-existing `.cs` files' `.uid` sidecars were auto-generated by the Godot editor (harmless metadata; explained in-thread, consistent with hundreds of other tracked `.uid` files).

**Review findings breakdown:** 2 patches applied (both low/medium severity — see Review Triage Log above), 4 deferred to `deferred-work.md` (DW-73..76, all pre-existing/systemic across the CreationSuite, not caused by this story), 14 rejected after independent verification against the actual code (several reviewer claims were factually contradicted by codebase invariants the reviewers didn't have full context on — detailed in the Review Triage Log).

**Verification performed:** `dotnet build` clean (0 errors); full Tier-1 suite 1245 passed / 1 skipped / 1 failed (the 1 failure is `ProceduralMapGeneratorTests`' golden-hash test, confirmed pre-existing and unrelated, present on `master` before this story too); golden-checksum diff empty (zero sim/checksum touch, as expected for a presentation-only story); all 33 TechTree-specific tests green after the review patches. Manual `/godot-verify` pass (via `godot-mcp`, driving real `GraphEdit` gestures and UI interactions) covered every I/O-matrix row: valid edge, self-edge rejection, cycle rejection (including a 4-node chain), duplicate-edge no-op, edge deletion, node-select-opens-inspector, close/reopen redraw round-trip, and a new building (authored live via `BuildingCardPanel`) appearing in the tree on next open — all confirmed working; one real bug (`GraphEdit` drag-to-disconnect silently no-oping without `AddValidLeftDisconnectType`/`AddValidRightDisconnectType`) was found and fixed during this pass. All manual test mutations to `alpha_faction.json` were reverted via `git checkout`.

**Residual risks:** none blocking. See `deferred-work.md` DW-73..76 for pre-existing/systemic gaps surfaced incidentally (no automated interaction tests for any CreationSuite editor; no `ModeChanged` unsubscribe across four panels now; no undo/redo for tech-tree edges; no shared CanvasLayer registry). Story 4.9 will need to generalize `TechTreeValidator`'s buildings-only cycle DFS and this editor's buildings-only node/edge model to include research nodes — flagged as a structural note in the original investigation, not a defect in this story's scope.
