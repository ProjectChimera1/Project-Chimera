---
title: 'Research authoring, command-card research buttons, and upgrade display'
type: 'feature'
created: '2026-07-10'
status: done
baseline_revision: 'c515c8de1d476f37639d5ff080e6bfbc35f61ae7'
final_revision: 'dd75be0ae7218776cce04accf71196c9799aa2af'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
  - '{project-root}/godot/src/UI/CommandCardSystem.cs'
  - '{project-root}/godot/src/CreationSuite/TechTreePanel.cs'
  - '{project-root}/godot/src/CreationSuite/BuildingCardPanel.cs'
  - '{project-root}/godot/src/Economy/ResearchSystem.cs'
warnings: ['oversized', 'multiple-goals']
---

<intent-contract>

## Intent

**Problem:** Research is fully built in the sim (4.8 content model, 4.9 order path, 4.10 checksum fold) but has zero player-facing surface: a creator cannot author a research ladder without hand-editing JSON, a player cannot start/cancel/see progress on a research order in-match, and no UI shows what a completed research is actually giving a unit.

**Approach:** Add a research-authoring surface to the visual tech-tree editor (research `GraphNode`s + a new sibling inspector panel + a `FactionWriter` sync function), a Research button category to `CommandCardSystem` (mirrors the existing Train dispatch and the 2.4b affordability-dim pattern), and an aggregate-upgrade line on the existing in-match unit stat display.

## Boundaries & Constraints

**Always:**
- `CommandCardSystem`: new Research button grid (own `Button[]`/index-array pair, mirrors `_trainBtns`'s per-slot captured-index lambda), populated from the selected building's `BuildingDefinition.AvailableResearch` resolved against `FactionDefinition.Research`/`IndexOfResearch`. `IssueResearchCommand(bId, researchIndex)` mirrors `IssueTrainCommand` exactly (`_lockstep?.EnqueueOrder(bId, UnitCommand.StartResearch, Fixed.FromRaw(researchIndex), Fixed.Zero) ?? true`, else `OrderApplier.Apply(..., research: _research)`); `IssueCancelResearchCommand` mirrors with `UnitCommand.CancelResearch`. Add `SetResearchDeps(ResearchSystem, ResearchStore)`, wired from `CameraPhase.cs` beside `SetReviveDeps`/`SetShopDeps`. Both `OrderApplier.Apply` call sites in this file must pass `research: _research`.
- Button dim predicate re-derives, read-only, the SAME gates `ResearchSystem.StartResearchCommand` checks (already in progress, at max level via `CompletedLevels[f][idx] >= def.Levels.Count`, unmet prerequisite, unaffordable) — never diverge from what the sim would refuse (the 2.4b pattern this AC names explicitly). Locked-but-visible, not hidden, matching the existing `Modulate`/`Disabled` dim convention.
- In-progress state renders as text (`"{DisplayName}  Lv{level}  {remainingTicks/30f:F1}s"`), following the existing Training/Construction text-status convention — no `ProgressBar`/`ChimeraComponents` in this file (it has none today; stay consistent with its own hand-built-Button/Label convention).
- Completion needs no consumer here: `ResearchSystem` already pushes `CombatEventType.ResearchComplete`; the chime/toast is explicitly Story 11.8's job per the AC text. The button grid simply reflects state on its next `RefreshCard`.
- Unit upgrade display extends the ONLY existing live in-Play per-unit stat surface — `SelectionSystem.cs`'s floating selection display (`UpdateHealthBar` region) — with a summed line per non-zero stat (e.g. `"+2 Atk"`), computed by summing `ResearchStore`'s per-faction cumulative deltas across every research index where `CompletedLevels[faction][i] > 0`.
- Tech-tree editor: research nodes are `GraphNode`s on the same `GraphEdit` (own port color, distinct from `PortColorIn`/`PortColorOut`), added alongside building nodes. A dragged edge onto a research node appends the source id to `ResearchDefinition.Prerequisites` (union of building-or-research ids, per `ResearchSystem.PrerequisitesMet`'s resolution order — research id first, then building). Add `ResearchValidator.ValidateProposedEdge(FactionDefinition, sourceId, targetId)` mirroring `TechTreeValidator.ValidateProposedEdge`'s single-edge reuse of its batch cycle DFS (`ResearchValidator`'s existing `DetectCycle`, Research→Research edges only) — reject inline at drop time on cycle or unknown-id, identical wording to the import-time lint.
- Persist research edits via a new `FactionWriter.SyncFactionResearch(string factionJson, IReadOnlyList<ResearchDefinition>)`, following `SyncFactionUnits`/`SyncFactionBuildings`'s exact self-check-reload-then-atomic-`File.Move` sequence, writing `root["research"]`.
- New `ResearchCardPanel` (sibling to `BuildingCardPanel`, same shell: browse cursor, Simple/Advanced `ChimeraTabs`, per-field `ChimeraValidationBadge`, raw-JSON escape hatch, `EditorHistory` undo, Save/New/Duplicate/Delete) edits `Id`/`DisplayName`/`CancelRefundFraction`/`Prerequisites` (`AddCommaList`, mirrors `BuildingCardPanel.Edit.cs:148`) and the repeatable `Levels` list (`Cost` map, `TimeTicks`, the four `ModifierDelta` fields) with its own add/remove-row UI (no existing repeatable-list-field precedent in this codebase — build the minimal version, not a generalized list-editor abstraction). Opens on research-node selection, mirroring `TechTreePanel.OnNodeSelected` → `BuildingCardPanel.SelectAndShow`.
- `BuildingDefinition.AvailableResearch` linkage is authored from the BUILDING side: add one `AddCommaList` field to `BuildingCardPanel.Edit.cs` (mirrors the existing `Prerequisites` line, :148) — `available_research` already round-trips via `FactionWriter.SyncFactionBuildings:503`, so no writer change is needed for this field.
- Invalid authoring input (negative/malformed cost, blank/duplicate research id, an unmet-prerequisite drag) is rejected in-panel with an inline located message and never written to disk, matching every other CreationSuite panel's convention.

**Block If:** None — no decision here requires human input; the codebase already carries every field and dispatch seam this story wires up.

**Never:**
- No `SimChecksum`/sim-array mutation from any panel touched here (all three surfaces are presentation/authoring-only over the already-fold-complete 4.8-4.10 sim).
- No chime/toast consumer for `ResearchComplete` (Story 11.8).
- No second graph edge/port type for the building→research "offers" link — that's authored via the `AvailableResearch` comma-list field, not a graph edge (avoids inventing a new visual convention `TechTreePanel` doesn't already have).
- No generalized reusable list-editor component — the `Levels` row UI is scoped to this panel's own needs.

**Matrix Test Audit waiver (resolved 2026-07-10):** rows 1–6 and 8 of the I/O & Edge-Case Matrix below (command-card research button states; unit upgrade display) are satisfied for the Matrix Test Audit gate by code review + the existing sim-level coverage of the underlying gates (Story 4.9's `ResearchSystemTests`, which already exercises spend-once/in-progress/max-level/prereq/afford-rejection/cancel-refund at the `ResearchSystem` level) whenever live UI verification of the new `CommandCardSystem`/`SelectionSystem` code is blocked by a documented, pre-existing environment/tooling limitation unrelated to this story's own code — e.g. this environment's synthetic click-to-select pipeline, which `godot-mcp`'s own input tool documents as lacking absolute-cursor-position support (only relative mouse-look is supported). This waiver does **not** apply if the missing coverage instead stems from a defect, omission, or scope gap in this story's own implementation — in that case the audit must still fail on those rows. Invoking this waiver requires a residual-risk note in the story's Auto Run Result recording exactly what was not live-verified.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy start | Owned building offers an idle-eligible research, affordable | Button enabled; click starts it, resources deduct, countdown begins | No error |
| Already in progress | Faction has an active order | Button disabled, `"[in progress]"`-style note | Deterministic dim, no click effect |
| At max level | `CompletedLevels[f][idx] >= Levels.Count` | Button disabled, `"[maxed]"` note | Deterministic dim |
| Prerequisite unmet | A named building/research prereq not satisfied | Button disabled, `"[need: X]"` note | Deterministic dim |
| Unaffordable | `ResourceStore.CanAfford` fails for next level | Button disabled, `"[need <resource>]"` note | Deterministic dim |
| Cancel | Order in progress, Cancel pressed | Order clears, refund applied, grid returns to idle state | No error |
| Tech-tree cyclic edge | Dragging research A → research B where B already (transitively) requires A | Edge rejected inline, graph unchanged | Located rejection message |
| Upgrade display, no completed research | Unit's faction has zero completed research | No upgrade line shown | No error (omit line, not a zero) |

</intent-contract>

## Code Map

- `godot/src/UI/CommandCardSystem.cs` -- new Research button grid, `SetResearchDeps`, `IssueResearchCommand`/`IssueCancelResearchCommand`, dim predicate, in-progress text status, hide-on-construction/other-category clearing.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs:54-55` -- wire `commandCard.SetResearchDeps(_ctx.Host.ResearchSys, _ctx.Host.Research)` beside `SetReviveDeps`/`SetShopDeps`.
- `godot/src/UI/SelectionSystem.cs` (`UpdateHealthBar` region, ~1146-1176) -- append summed per-stat research-upgrade line(s) for the focused unit's faction.
- `godot/src/CreationSuite/TechTreePanel.cs` -- research `GraphNode`s, prerequisite/offers edge handling, `ResearchCardPanel` selection wiring.
- `godot/src/Core/Definitions/ResearchValidator.cs` -- add `ValidateProposedEdge(FactionDefinition, sourceId, targetId)`.
- `godot/src/Core/Definitions/FactionWriter.cs` -- add `SyncFactionResearch(string, IReadOnlyList<ResearchDefinition>)`.
- `godot/src/CreationSuite/ResearchCardPanel.cs` (new) -- research inspector, mirrors `BuildingCardPanel`'s shell.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs:148` -- add `AvailableResearch` `AddCommaList` field.

## Tasks & Acceptance

**Execution:**
- `CommandCardSystem.cs` / `CameraPhase.cs` -- Research button grid + dispatch + deps wiring -- makes research runnable in-match.
- `SelectionSystem.cs` -- aggregate upgrade line -- makes completed research legible on the unit.
- `TechTreePanel.cs` / `ResearchValidator.cs` / `FactionWriter.cs` / `ResearchCardPanel.cs` (new) / `BuildingCardPanel.Edit.cs` -- authoring surface + persistence -- makes research authorable without hand-editing JSON.

**Acceptance Criteria:**
- Given the visual tech-tree editor (4.6), when a creator adds research nodes, then they drag into the dependency chain like buildings/units, prereq-lint applies inline, and saved research definitions round-trip through reload unchanged.
- Given an eligible building is selected, when the command card renders, then research buttons show cost/time/level state (dimmed exactly when the sim would refuse: unaffordable/capped/prerequisite-missing/already-in-progress), an in-progress research shows its remaining time, and completion is reflected on the next refresh (no new chime/toast consumer — 11.8's job).
- Given a unit benefiting from completed research, when its panel renders, then the aggregate upgrade contribution is visible beside the relevant base stat (e.g. "+2 Atk"), omitted entirely when zero.

## Design Notes

**Why `AvailableResearch` is edited from the building side, not a graph edge:** the field already round-trips through `FactionWriter.SyncFactionBuildings` (landed silently in 4.8/4.5) and `BuildingCardPanel.Edit.cs` already has the exact `AddCommaList` pattern for a sibling string-array field (`Prerequisites`, :148) — reusing it is a one-line addition, versus inventing a second edge/port semantic in `TechTreePanel`'s graph that Design Notes elsewhere describes as reserved for dependency ("requires"), not offering ("provides") relationships.

**Why the command-card dim predicate re-derives gates instead of calling into `ResearchSystem`:** every existing button category (Train, Ability) reads sim arrays directly and reconstructs the same boolean the sim's order-gate uses, rather than exposing a `CanStart`-style query method on the system. `RefreshAbilityCard`'s own comment is explicit that this copy must stay identical to the sim's refusal logic so the greyed-out button never diverges from what the sim would actually refuse. This story follows that established convention rather than adding a new query surface to `ResearchSystem`.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~ResearchValidator` -- expected: new `ValidateProposedEdge` tests pass, no regressions to existing `ResearchValidator`/`TechTreeValidator` tests.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: full Tier-1 suite green (this story adds no sim/checksum state, so no golden drift is expected).

**Manual checks (no automated UI test project exists in this repo):**
- `/godot-verify`: open the tech-tree editor, add a research node, wire a prerequisite edge (including a rejected cyclic attempt), save, reload, confirm identical round-trip.
- `/godot-verify`: start a match, select a building offering research, confirm button dim states match affordability/prereq/level-cap/in-progress; start, watch the countdown, let it complete; confirm the unit's floating panel shows the new upgrade line. **If this check cannot be completed** because building selection cannot be driven in this environment (synthetic click-to-select has no absolute-cursor-position support — see the Matrix Test Audit waiver above), it is not required to close this story: rely on code review + Story 4.9's `ResearchSystemTests` sim-level coverage instead, and record the untested surfaces as a residual risk.

## Review Triage Log

### 2026-07-10 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 7 (high 3, medium 2, low 2)
- defer: 2 (medium 1, low 1)
- reject: 8 (medium 1, low 7)
- addressed_findings:
  - `[high]` `[patch]` `ResearchCardPanel.BuildLevelRow` called `MakeBadge("levels")` with the SAME cache key across all 6 per-level field rows (Time/Cost/4 modifier deltas), and across every level — since `_badges` caches by key, every `AddFieldRow` past the first for a given research entry tried to re-parent an already-parented `ChimeraValidationBadge` Control, which Godot rejects. Fixed by giving each field its own per-level key (e.g. `levels[2].modifier_delta.armor_delta`) and rewriting `RouteErrorToKey` to parse `ResearchValidator`'s `.levels[N].xxx` error format into the matching key.
  - `[high]` `[patch]` `FactionWriter.SyncFactionResearch`/`PutLevels` had no null-element guard for a malformed hand-edited `"research": [null, {...}]` or `"levels": [null, {...}]` array — an NRE would crash the whole Save path, unlike `ResearchValidator.Validate`'s already-established null-skip convention for the identical input shape. Added matching `if (r == null) continue;` / `if (level == null) continue;` guards; covered by two new `ResearchWriteRoundTripTests` cases.
  - `[high]` `[patch]` `CommandCardSystem.RefreshResearchButtons` dereferenced `rdef.Levels.Count`/`rdef.Levels[...]` directly — `Levels` is a non-nullable-typed property malformed JSON (`"levels": null`) can still leave null at runtime, which would NRE the command card whenever a player selected a building offering that research. Fixed via a local `levels = rdef.Levels ?? EmptyResearchLevels` used throughout the block.
  - `[medium]` `[patch]` `TechTreeValidator.ValidateProposedEdge` had no unknown-source-id check — before this story every node in the graph was a building, so `sourceId` was always valid by construction; Story 4.11 puts research nodes on the SAME port type, making a research-sourced edge onto a building target newly reachable, and `DetectCycle`'s building-only walk silently ignores it and returns "valid". Added an inline rejection (wording byte-identical to the import-time referential lint), mirroring `ResearchValidator.ValidateProposedEdge`'s own unknown-source-id check; covered by a new `TechTreeValidatorTests` case. (Originally patched as a UI-layer pre-check in `TechTreePanel.OnConnectionRequest`, then moved into the Godot-free validator itself for testability and wording consistency.)
  - `[medium]` `[patch]` `CommandCardSystem.RefreshResearchButtons` hid `_researchStatus`/`_researchCancelBtn` whenever `anyInProgress` was true but `inProgressIdx` was out of range for the current `fdef.Research` (a stale index, e.g. from a shrunk research list) — the player would be left with an active order and no way to cancel it from this card. Added a fallback branch that still shows Cancel with a generic status line in that state.
  - `[low]` `[patch]` `SelectionSystem.AppendUpgradePart` omitted a stat line only on exact `Fixed.Zero`, so a nonzero-but-sub-0.05 cumulative delta would format to `"+0 Atk"`, violating the intent contract's "omit the line, not a zero" rule at the per-stat level. Fixed by checking the value rounded to display precision, not the exact `Fixed`.
  - `[low]` `[patch]` `TechTreePanel.RebuildGraph`'s research-edge-drawing loop didn't skip a self-referencing `prereqId == r.Id` (only reachable via hand-edited JSON — the panel's own drop-time validation already rejects authoring this), which would call `GraphEdit.ConnectNode` on the same node/slot twice, undefined behavior. Added a one-line skip.
  - defer (DW-89, medium): no uniqueness check prevents a research id from colliding with an existing building id before both become same-`GraphEdit` `GraphNode`s (Godot auto-renames the loser, silently breaking by-name edge/selection resolution); also, `OnNodeSelected`'s first-wins `FirstOrDefault` resolution is inconsistent with `RebuildGraph`'s last-wins `researchById` dict for the same duplicate-id case. Needs a design call on where the cross-namespace check belongs.
  - defer (DW-90, low): no mutual exclusivity between the Research/Shop/Revive/Train command-card button grids for the same building — pre-existing architectural pattern (Shop/Revive already share this shape unguarded), not introduced by this story; Research just follows the established convention.
  - reject: undo/redo "double-apply" claim on `CommitLevelsSnapshot`/`DoCreate`/`DoDuplicate` — verified against `EditorHistory.Push`'s documented contract ("register a command that has already been executed"); every site either reassigns an idempotent already-live value or is guarded by an explicit `Contains` check, so no functional double-apply occurs.
  - reject: `FactionWriter.SyncFactionResearch`'s "first wins on duplicate id, not caught inline" claim — `ResearchValidator.Validate` already emits a located "duplicate research id" error live (per-keystroke, via `RevalidateAndReflect`) that gates the Save button (`_lastValid`), matching the spec's explicit "matching every other CreationSuite panel's convention" requirement.
  - reject: `ResearchCardPanel.UniqueId` exhausting 99999 numeric-suffix candidates before falling back to a non-unique id — requires 99999 same-base-id entries, not a realistic authoring scenario.
  - reject: re-entrancy concern on `ValidateProposedEdge`'s null-forgiving `Prerequisites`/`ModifierDelta` restore in its `finally` block — single-threaded Godot UI/test execution, no realistic concurrent-call path.
  - reject: missing multi-hop (3+ node) cycle test for the new `ValidateProposedEdge` wrappers — the underlying `DetectCycle` DFS is shared, unmodified code already covered by multi-hop cases in the building-side test suite; the new wrappers only add a thin front door to it.
  - reject: `ResearchCardPanel.LEFT_OFFSET`'s hardcoded 984px viewport offset with no narrow-window fallback — cosmetic, consistent with every other CreationSuite panel's existing hardcoded-offset convention.
  - reject: `TechTreePanel.Persist()`'s comment describing `PutLevels` as a "no-op pass" for a pure building-edit — a real but purely cosmetic doc-accuracy gap (`PutLevels` always rewrites); no functional consequence.
  - reject: "Matrix Test Audit waiver under-applied" meta-commentary — addressed by fixing the underlying implementation defects it was pointing at (badge collision, cross-node-type edge hole, stale-index Cancel gap) rather than by re-litigating the waiver itself.

### 2026-07-10 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 3: (high 0, medium 1, low 2)
- reject: 14: (high 0, medium 1, low 13)
- addressed_findings:
  - `[low]` `[patch]` `CommandCardSystem.RefreshResearchButtons` tooltip showed `"Lv{completedLevels+1}/{levels.Count}"`, so a maxed research read `"Lv3/2"`; clamped the displayed next-level with `System.Math.Min(completedLevels + 1, levels.Count)` → now `"Lv2/2"`.
- notes: independent 4-layer follow-up pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment; all fresh-context subagents at session model) on the already-committed diff (`c515c8d..f714c58`). No crash, no intent gap, no spec deviation found — Intent Alignment confirmed the diff implements every intent surface with defensible readings, and Blind Hunter confirmed the sim dispatch (`Fixed.FromRaw(researchIndex)` round-trip) is correct with no 16.16 scaling bug. Three new items deferred to the ledger (DW-91 faction-wide-vs-per-building research/Cancel gating; DW-92 inspector↔graph sibling staleness; DW-93 pure dim-predicate/upgrade-summary logic has no unit coverage — code-reviewed correct this pass). The building-id⇄research-id collision re-flagged by two layers is already tracked as DW-89 (not re-deferred). All other findings rejected as cosmetic, latent, or consistent with pre-existing accepted conventions (see the reviewer notes folded into the deferred entries).

## Auto Run Result

Status: done

**Summary:** Implemented the full research player/creator surface: a Research `GraphNode` + `ResearchCardPanel` inspector in the visual tech-tree editor (with `FactionWriter.SyncFactionResearch` persistence and `ResearchValidator.ValidateProposedEdge` drop-time linting), a Research button category on `CommandCardSystem` (start/cancel/dim states mirroring Train), and an aggregate completed-research upgrade line on the unit selection panel. A review pass then found and fixed 7 defects (3 crash-class) before commit.

**Files changed:**
- `godot/src/UI/CommandCardSystem.cs` — Research button grid, `SetResearchDeps`, `IssueResearchCommand`/`IssueCancelResearchCommand`, dim predicate, in-progress status + Cancel; review-pass: null-`Levels` guard, stale-in-progress-index Cancel fix.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` — wires research deps into `CommandCardSystem`/`SelectionSystem`.
- `godot/src/UI/SelectionSystem.cs` — aggregate upgrade line under the HP bar; review-pass: rounds-to-zero display fix.
- `godot/src/CreationSuite/TechTreePanel.cs` — research `GraphNode`s, edge validate/persist/select routing; review-pass: self-referencing-prerequisite guard.
- `godot/src/Core/Definitions/ResearchValidator.cs` — `ValidateProposedEdge` for research-target edges.
- `godot/src/Core/Definitions/TechTreeValidator.cs` — review-pass: rejects a non-building `sourceId` inline (new gap exposed by research nodes sharing this graph's port type).
- `godot/src/Core/Definitions/FactionWriter.cs` — `SyncFactionResearch`/`SerializeResearchClean`; review-pass: null-element guards for malformed JSON.
- `godot/src/CreationSuite/ResearchCardPanel.cs` (new) — research inspector; review-pass: fixed shared-badge-instance collision (per-level-per-field keys), rewrote `RouteErrorToKey` to match.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` — `available_research` `AddCommaList` field.
- `godot/src/Economy/ResearchSystem.cs` — `GetFactionDefinition` read accessor for the command card.
- Tests: `ResearchValidatorTests.cs` (+6), `TechTreeValidatorTests.cs` (+1), `ResearchWriteRoundTripTests.cs` (new, 7 cases).

**Review findings breakdown:** 7 patched (3 high, 2 medium, 2 low) — all applied and verified; 2 deferred to `deferred-work.md` (DW-89 cross-namespace id collision, DW-90 command-card grid mutual-exclusivity, both medium/low, pre-existing-pattern or graceful-degradation classes); 8 rejected (verified non-issues or impractical edge cases — see Review Triage Log above for each).

**Verification performed:**
- `dotnet build godot/godot.sln -c Debug` — clean, 0 errors (post-patch, re-run after every fix).
- `dotnet test .../ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~ResearchValidator` — 43/43 pass.
- `dotnet test .../ProjectChimera.Sim.Tests.csproj -c Release` (full Tier-1) — 1379/1380 passed, 1 pre-existing unrelated skip, 0 failures, no golden drift (final run, post all patches).
- Manual `/godot-verify` UI checks (tech-tree drag/drop, command-card button states in a live match) could not be completed in this environment — synthetic click-to-select has no absolute-cursor-position support (`godot-mcp` limitation, documented in the spec's own Matrix Test Audit waiver). Per the waiver, this does not block closing the story; covered instead by this review pass's direct source verification plus Story 4.9's `ResearchSystemTests` sim-level coverage.

**Residual risks:**
- Not live-verified (waiver-covered): command-card research button dim/enable states, in-progress countdown + Cancel affordance, tech-tree editor's research-node drag/drop UI, `ResearchCardPanel` UI — all verified by direct source reading during this review pass instead of interactive testing.
- DW-89 (deferred): a research id colliding with a building id degrades gracefully (Godot auto-rename) rather than crashing, but silently breaks by-name edge resolution for the renamed node.
- DW-90 (deferred): no mutual exclusivity between Research and Shop/Revive/Train command-card grids if a single building is ever authored with more than one — pre-existing pattern, not new to this story.
- `followup_review_recommended: true` — this pass fixed 3 high-severity, crash-class defects (2 malformed-JSON NREs, 1 Godot Control-reparenting failure) across 6 files; an independent follow-up pass is warranted given that volume and severity.

---

### Follow-up review pass (2026-07-10)

The recommended independent follow-up ran a fresh 4-layer adversarial review (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the committed diff `c515c8d..f714c58`.

**Outcome:** No crash, no intent gap, no spec deviation. Blind Hunter found no guaranteed crash on normal input and confirmed the sim dispatch path is correct (no 16.16 scaling bug); Intent Alignment confirmed the diff implements every intent surface with defensible readings of the ambiguous axes. The three prior-pass high-severity fixes hold.

**Change made this pass:**
- `godot/src/UI/CommandCardSystem.cs` — `[low patch]` clamped the research button tooltip's displayed next-level so a maxed research reads `"Lv2/2"` instead of `"Lv3/2"` (`System.Math.Min(completedLevels + 1, levels.Count)`).

**Deferred (new ledger entries, appended — no existing entries modified):**
- **DW-91** — the command card gates all research UI (including the faction-wide in-progress Cancel) on the selected building's producer/offer status, so a producer building never shows its authored `AvailableResearch` and Cancel is unreachable from a building that offers no research while an order runs. Low-consequence (no crash/data loss; research completes on its own); needs a design call. Distinct from DW-90 (grid overlap).
- **DW-92** — `ResearchCardPanel` structural edits and `TechTreePanel` graph edge edits mutate the shared `FactionDefinition` without rebuilding the sibling view; graph/inspector can drift until the next `R` toggle. Self-healing, no disk corruption.
- **DW-93** — the research dim predicate and the upgrade-summary math are pure, unit-testable logic with zero automated coverage (the dim predicate is a hand-copied parallel of `StartResearchCommand`'s gate chain with nothing pinning equivalence). Code-reviewed correct this pass; coverage hardening.
- The building-id⇄research-id collision re-flagged by two layers is already **DW-89** — not re-deferred.

**Rejected (14):** save self-check parse-only (mirrors `BuildingCardPanel`'s accepted posture); `PutLevels` rewrites nested `levels` fresh, dropping hand-authored unknown keys (matches `SyncFactionBuildings`; documented); writer first-wins dedup of duplicate on-disk research ids (Save gated by `ResearchValidator`'s located duplicate-id error); `FirstUnaffordableResource` latent for future resources (currently correct — `KnownResourceIds`={ore,crystal}); enemy-building buttons render no-op (mirrors Train exactly); level-render `??=` model mutation (harmless to serialization); `UniqueId` cap-collision (unreachable); double `ModeChanged` subscription (latent, same as existing pattern); per-frame-stale slot index (sim re-gates); over-cap research options silently dropped (established UI-cap convention, per Shop/Revive); non-object faction JSON raw exception (minor, consistent with other writers); duplicate `available_research` → two buttons (cosmetic); weak `Assert.Contains` round-trip assertion (adequate for intent).

**Verification performed (follow-up pass):**
- `dotnet build godot/godot.sln -c Debug` — clean, 0 errors (7 pre-existing warnings).
- `dotnet test .../ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~ResearchValidator` — 43/43 pass (tooltip patch is game-assembly-only; sim suite is structurally unaffected by the change).

**Residual risks (follow-up pass):** unchanged from above — the live-UI surfaces remain waiver-covered (verified by source reading, not interactive testing). DW-91/92/93 are the newly-recorded, deferred items. `followup_review_recommended` set to `false`: this pass made only a single low-severity cosmetic patch.

