---
title: 'Regions — named areas as a first-class map/trigger primitive'
type: 'feature'
created: '2026-07-14'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '1bf12b188e905ae8a5ef52c3a9ca77050f00b8b6'
final_revision: '4815d9898023d8fc353d5e3f24ddf5e4edaa7200'
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The editor has no concept of a *region* — a named rectangular map area. Nothing in the scenario model, sim, trigger system, or Creation Suite can name a zone or ask "is a unit inside it," so Epic 7's win-condition presets (King of the Hill, etc.) have no author-drawn zone to bind to. Every "region" in the code today is Terrain3D's internal heightmap-tile concept, unrelated to gameplay.

**Approach:** Introduce regions as a genuinely new, rect-only primitive across four thin layers: (1) an authored `ScenarioRegion` row + `Regions[]?` collection on `ScenarioData` that persists inside `scenario.json`/`.chimera.zip` via the established omit-when-null pattern; (2) a Godot-free deterministic `FixedRect` + `RegionStore` (float→`Fixed` resolved once at scenario-apply); (3) exactly ONE trigger-referencing surface — a stateless `unit_in_region` condition wired into the existing `ScenarioDirector` ECA system, referencing a region by string `region_id`; (4) a Creation Suite Region draw tool (drag-rect, named, right-dock panel, labeled 3D overlay, shared undo/redo). Regions are excluded from the MP-handshake hashes on the same basis as Triggers, so **no golden re-baseline** occurs.

## Boundaries & Constraints

**Always:**
- Regions are **rect-only**. Authored as `float MinX/MinZ/MaxX/MaxZ` (mirroring `ScenarioResourceNode`'s float `X/Z` convention), resolved float→`Fixed` **exactly once** at `ScenarioApplier` (the sanctioned single conversion boundary) into a `FixedRect` held by a Godot-free `RegionStore`.
- All containment tests use **`Fixed` point-in-rect over `FixedVec3` positions**, ascending-entity-id iteration, no `float`/`double`/`Mathf`/`System.Random` on any sim path. Edge membership is **inclusive** (a point on `MinX`/`MaxX`/`MinZ`/`MaxZ` is inside) — documented and tested.
- Regions persist **inside `scenario.json`** (hence inside `.chimera.zip`) via `[JsonIgnore(Condition = WhenWritingNull)]`. An absent/empty regions collection serializes **byte-for-byte identically** to pre-feature — **no map-package format change**, no new zip file.
- Exactly **ONE** trigger surface ships: a stateless `unit_in_region` condition (params: `region_id` string + faction slot) added to `TriggerDefinition` and dispatched in `ScenarioDirector.EvalCondition`. A region is referenced by string `region_id`, mirroring the existing `timer_name`/`variable` string-key convention.
- The Region editor tool shares the **single injected `EditorHistory`** (add/delete/resize as redo/undo delegate pairs) and interleaves safely with entity undo/redo with no cross-corruption; an `IsOverPanel` + `MouseFilter.Stop` guard prevents drawing under the panel; the region overlay is **Edit-mode-only** (gated on `GameMode.Edit`, hidden in Play).
- `ScenarioValidator` stays fail-closed: **unique, non-empty** region ids; `MinX < MaxX && MinZ < MaxZ`; all four corners within `MapBounds` (via the existing `CheckCoord` pattern); a `unit_in_region` condition referencing an **undefined** `region_id` is a validation error (dangling-ref, mirroring the `timer_expires` dangling check).
- Regions are **EXCLUDED** from `CanonicalModelHash` and `StartStateHash` on the SAME basis as `Triggers` (consumed only by the trigger system, no other sim consumer): `CanonicalModelHash.AlgoVersion` stays **5**, `StartStateHash.AlgoVersion` stays **2**, and **no golden is re-recorded**. Document the exclusion beside the existing `Triggers` exclusion note.

**Block If:**
- A **non-trigger** sim system (combat target acquisition, `AiOpponentSystem`, `FlowFieldComputer`/`FlowFieldSystem`, `NavObstacleManager` bake, `MovementSystem`) is found to consume region containment for a deterministic decision that feeds `SimChecksum` — HALT rather than silently make regions lockstep-critical without folding them into the MP handshake.
- The intended 6.4 trigger-consumption surface is ambiguous beyond the single stateless `unit_in_region` condition (e.g. the intent is read to require enter/leave edge-**events** with cross-tick occupancy state) — HALT rather than build unbounded trigger machinery that belongs to Epic 7.

**Never:**
- Never circles/polygons (rect-only for 1.0; deferred post-1.0 per epic scope).
- Never build win-condition **presets** / King-of-the-Hill evaluation (Epic 7). 6.4 exposes only the primitive + the one containment condition those presets will consume.
- Never enter/leave edge **events** or per-region occupancy state machines in 6.4 — only the stateless `unit_in_region` condition.
- Never fold regions into `CanonicalModelHash`/`StartStateHash`/`SimChecksum`; never bump any `AlgoVersion`; never re-record any golden. Regions are static authored data in the Triggers-excluded category.
- Never Godot types in `FixedRect`/`RegionStore`/the `ScenarioDirector` region logic; never per-region Godot Nodes for the overlay beyond the editor `MeshInstance3D`/`Label3D` gizmo.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Draw & save region | Drag a named rect in Edit mode, Save | A `ScenarioRegion {id,name,MinX,MinZ,MaxX,MaxZ}` is written to `scenario.json`; reload restores identical id/name/bounds | No error |
| No regions authored | Scenario with no regions | `regions` key absent; serialization byte-identical to pre-feature; all goldens + `CanonicalModelHash`/`StartStateHash` unchanged | No error |
| `unit_in_region` true | A unit of faction F is inside region R's rect | Condition evaluates **true** (Fixed point-in-rect, ascending id); gated action (e.g. `victory`) fires | No error |
| `unit_in_region` false | No unit of F inside R | Condition evaluates **false** | No error |
| Point on rect edge | Position exactly on `MinX`/`MaxX`/`MinZ`/`MaxZ` | Inclusive → **inside** (documented) | No error |
| Determinism replay | Region-driven trigger, same seed twice | Byte-identical per-frame checksums both runs; no golden moved | No error |
| Invalid region | Duplicate id, empty id, `MinX ≥ MaxX`, or a corner outside `MapBounds` | `ScenarioValidator` fails closed with a clear message; scenario rejected pre-tick | Validation error |
| Dangling region ref | `unit_in_region` → an undefined `region_id` | `ScenarioValidator` fails closed | Validation error |
| Export/import package | `.chimera.zip` round-trip of a map with regions | Region survives identically; `ContentPackageManifest.ScenarioHash` stays consistent | No error |
| Undo/redo region ops | Add, delete, resize regions interleaved with entity placement | Each op is one shared-history redo/undo pair; no cross-corruption with entity undo | No error |

</intent-contract>

## Code Map

- `godot/src/Core/FixedRect.cs` -- **NEW** Godot-free `Fixed`-only rect (`Fixed MinX/MinZ/MaxX/MaxZ`) with `bool Contains(Fixed x, Fixed z)` and `bool Contains(FixedVec3 p)` (inclusive edges). The Tier-1-testable geometry core.
- `godot/src/Core/RegionStore.cs` -- **NEW** Godot-free resolved-region store: parallel `string[] Ids` + `FixedRect[] Rects`, `int Count`, `bool TryGetIndex(string id, out int idx)`, `bool Contains(int idx, FixedVec3 pos)`. Built once at apply from `ScenarioData.Regions` (float→`Fixed`). Static (never mutates mid-match), so **not** in `SimChecksum`.
- `godot/src/Core/Definitions/ScenarioData.cs` -- add the `ScenarioRegion` row type (alongside `ScenarioItem` ~:194 / `ScenarioResourceNode` ~:66: `[JsonPropertyName]` `id`, `name`, `minX/minZ/maxX/maxZ` floats) and `[JsonPropertyName("regions")] ScenarioRegion[]? Regions` with `[JsonIgnore(Condition = WhenWritingNull)]` (slot near `Items`/`Resources` ~:292–320). Read as `Regions ?? Array.Empty<ScenarioRegion>()`.
- `godot/src/Core/Definitions/TriggerDefinition.cs` -- add a `unit_in_region` **condition** type in the Conditions block (~:87–125): carries `region_id` (string) + faction slot (reuse the existing faction-slot field convention used by `unit_count`). No new event, no new action.
- `godot/src/Core/ScenarioDirector.cs` -- hold a `RegionStore` reference (provided by the applier, same way scenario context is supplied today); add the `unit_in_region` case in `EvalCondition` (~:306–336): `TryGetIndex(region_id)` then an **ascending-entity-id** scan of `EntityWorld.Position[]` for a live unit of the faction with `store.Contains(idx, pos)`. Pure `Fixed`. An unresolved id at eval time evaluates false (validator already blocks dangling refs pre-tick).
- `godot/src/Core/Sim/ScenarioApplier.cs` -- in `Apply` (~:85; `s.Items` loop ~:219–231 is the template) build the `RegionStore` from `scenario.Regions` (one `Fixed.FromFloat` per corner) and hand it to `ScenarioDirector`. No per-entity spawn change.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- add a `regions` well-formedness loop (unique non-empty id; `MinX<MaxX && MinZ<MaxZ`; corners within `MapBounds` via `CheckCoord` ~:135) and a dangling-`region_id` check for `unit_in_region` conditions (mirror the `timer_expires` dangling check ~:205–216).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- **comment only**: document that `Regions` are excluded (same basis as `Triggers`, ~:17). Confirm neither `Compute` folds `Regions`; `AlgoVersion` stays 5. (`StartStateHash.cs` likewise untouched; stays 2.)
- `godot/src/CreationSuite/RegionTool.cs` -- **NEW** editor Node (sibling tool, modeled on `TerrainBrush` drag state-machine + `EntityPlacer` Y=0 ground-plane raycast ~:400–407): toggle key, drag-rect (mouse-down corner A → motion updates ghost rect → mouse-up commits corner B), right-dock `PanelContainer` with a region list + name field + `ChimeraTabs` Simple/Advanced (Advanced = raw min/max coords), grid-snap (`G`) reuse, `IsOverPanel`+`MouseFilter.Stop` guard, delete/resize selected region. Each op pushes an `EditorHistory.Push(redo, undo)` pair and mutates `ScenarioData.Regions` directly (as `TriggerEditorPanel` mutates `Triggers[]`). Renders each region as a labeled rect: a `MeshInstance3D` `ImmediateMesh` line-loop (4 corners, Y≈0, unshaded emissive per-region color) + a `Label3D` name; overlay gated on `GameMode.Edit`.
- `godot/src/Core/Bootstrap/Phases/RegionToolPhase.cs` -- **NEW** bootstrap phase wiring `RegionTool`, injecting `EntityPlacer.History` (the shared `EditorHistory`, ~`EntityPlacer.cs:121`) + the `ScenarioContext`; register in the `MainScene` phase list near `TerrainBrushPhase` (~`MainScene.cs:418`).
- `godot/ProjectChimera.Sim.Tests/**` -- **NEW** Tier-1 Godot-free tests: `FixedRectTests`, `RegionStoreTests`, `ScenarioDataRegionsTests` (round-trip + omit-when-null → key absent), `ScenarioValidatorRegionTests` (dup/empty id, min≥max, OOB corner, dangling `region_id`), `ScenarioApplierRegionTests` (Apply builds the store with Fixed-resolved bounds), `UnitInRegionConditionTests` (director evaluates true/false deterministically), and `CanonicalModelHashRegionExclusionTests` (adding regions leaves `CanonicalModelHash`/`StartStateHash` unchanged; AlgoVersion 5/2).

## Tasks & Acceptance

**Execution:**
- `FixedRect.cs` (NEW) -- Godot-free `Fixed` rect + inclusive `Contains(x,z)` / `Contains(FixedVec3)`.
- `RegionStore.cs` (NEW) -- Godot-free `Ids`/`Rects` + `TryGetIndex` + `Contains`; degenerate/empty store safe.
- `ScenarioData.cs` -- add `ScenarioRegion` row + `Regions[]?` (omit-when-null); read via null-coalesce to empty.
- `TriggerDefinition.cs` -- add the `unit_in_region` condition type (region_id + faction).
- `ScenarioApplier.cs` -- build `RegionStore` (float→Fixed once) and supply it to `ScenarioDirector`.
- `ScenarioDirector.cs` -- evaluate `unit_in_region` (ascending-id Fixed point-in-rect scan by faction).
- `ScenarioValidator.cs` -- regions well-formedness + dangling-`region_id` fail-closed checks.
- `CanonicalModelHash.cs` -- exclusion comment; verify no fold; AlgoVersion unchanged.
- `RegionTool.cs` (NEW) + `RegionToolPhase.cs` (NEW) -- editor draw tool, panel, labeled overlay, shared undo/redo, ScenarioData sync; phase-registered.
- Tests -- `FixedRectTests`, `RegionStoreTests`, `ScenarioDataRegionsTests`, `ScenarioValidatorRegionTests`, `ScenarioApplierRegionTests`, `UnitInRegionConditionTests`, `CanonicalModelHashRegionExclusionTests`.

**Acceptance Criteria:**
- Given the Region tool in Edit mode, when the author drags a rectangle and names it, then a labeled rect overlay appears in the 3D viewport and a `ScenarioRegion` (id/name/bounds) exists in the map; when the map is saved and reloaded, the region round-trips with identical id, name, and bounds.
- Given a map with regions exported to `.chimera.zip` and re-imported, when the imported map loads, then every region survives with identical bounds/name/id and the package `ScenarioHash` remains consistent.
- Given the shared editor history, when the author adds, deletes, and resizes regions interleaved with entity placement, then each region operation undoes/redoes as a single step without corrupting entity undo/redo state.
- Given a scenario with **no** regions authored, when it is serialized, then `scenario.json` contains no `regions` key and the bytes are identical to pre-feature, and all 23 per-tick SimChecksum goldens, `hero-start-state.golden.txt`, `CanonicalModelHash.AlgoVersion` (5), and `StartStateHash.AlgoVersion` (2) are unchanged.
- Given a scenario with a duplicate/empty region id, an inverted rect (`MinX ≥ MaxX`), a corner outside `MapBounds`, or a `unit_in_region` condition naming an undefined `region_id`, when it passes through `ScenarioValidator`, then validation fails closed with a clear message and the scenario is rejected before any tick.
- Given a scenario with region R and a trigger `unit_in_region(R, faction F) → victory`, when a live unit of F is inside R's rect, then the condition evaluates true via `Fixed` inclusive point-in-rect over ascending-id positions and the victory action fires; when no unit of F is inside R, the condition evaluates false; and both outcomes are byte-identical across two same-seed replays.
- Given a position exactly on a region's `MinX`/`MaxX`/`MinZ`/`MaxZ` boundary or a degenerate/empty region set, when containment is queried, then the edge is treated as inside (inclusive) and no crash/exception/OOB occurs.

## Spec Change Log

## Review Triage Log

### 2026-07-14 — Review pass 1 (post-implementation adversarial review: 4 layers)

- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 0, medium 6, low 5)
- defer: 0
- reject: 4
- addressed_findings:
  - `[medium]` `[patch]` **F1 — misleading hash-exclusion comment (determinism landmine).** The comment justified excluding Regions from the handshake hash by claiming no consumer feeds region-containment into `SimChecksum` — false, since `unit_in_region` gates trigger actions (`spawn_unit`/`add_resources`/`set_variable`) that DO mutate checksummed state. Reframed both comments (`CanonicalModelHash.cs`, `ScenarioData.cs`) to the accurate basis: regions are a *trigger input* and inherit the already-accepted, bounded Triggers handshake gap; they fold when Triggers fold; the Block-If tripwire is a non-trigger sim consumer. Comment-only, no AlgoVersion change.
  - `[medium]` `[patch]` **VG3+EC1+EC2+F4 — `BuildRegionStore` robustness.** A `"regions":[null]` shadow-mode apply would NRE; non-finite corners (NaN/Inf) slipped past `min<max` (NaN compares false); a rect the float validator accepted could collapse to `min==max` after `Fixed.FromFloat` rounding. `BuildRegionStore` now skips null elements, non-finite corners (`float.IsFinite` at the load-time boundary), and post-conversion degenerate/inverted rects, keeping id↔rect index alignment exact. (`ScenarioApplier.cs`.)
  - `[medium]` `[patch]` **VG1 — dead-unit false-victory was untested.** The `IsAlive` guard in the `unit_in_region` scan was correct but uncovered; dropping it would fire victory off a corpse in a region with no failing test. Added `DeadUnitInsideRegion_IsAliveGuardHolds_NoVictory` (despawn a unit inside the region, tick, assert no victory). (`UnitInRegionConditionTests.cs`.)
  - `[medium]` `[patch]` **VG2 — empty-regions serialization could drift the pinned bytes.** `[JsonIgnore(WhenWritingNull)]` omits null but not `[]`; a map whose regions were all deleted (outside the tool's normalization path) would emit `"regions":[]` and move the pinned scenario FNV bytes with no failing test. `ScenarioSerializer.Serialize` now normalizes an empty `Regions` array to null at the chokepoint; added `EmptyRegions_OmitsTheKey_MatchingNull`. (`ScenarioSerializer.cs`, `ScenarioDataRegionsTests.cs`.)
  - `[medium]` `[patch]` **F3+EC5 — editor could author a validator-rejecting (unsaveable) map with no feedback.** `CommitDrag`/`ApplyAdvancedResize` accepted corners outside `MapBounds`, which the validator later fails closed, potentially discarding unrelated authoring work. Both paths now reject out-of-bounds corners with an inline panel status message; min-extent centralized as a shared tool constant. (`RegionTool.cs`.)
  - `[medium]` `[patch]` **F5 — `RegionToolPhase.Run` NRE on a degraded bootstrap path.** Dereferenced `_ctx.Placer.History`/`Cam`/`GameState` with no null check, so a headless/fallback path could crash the whole scene. Now log-skips (no throw) if any required handle is null. (`RegionToolPhase.cs`.)
  - `[low]` `[patch]` **F7+EC6 — `unit_in_region` faction slot unvalidated.** An out-of-range faction made the scan silently never match — the same foot-gun the dangling-`region_id` check guards. Added a co-located fail-closed `CheckFactionSlot` on the condition. (`ScenarioValidator.cs`.)
  - `[low]` `[patch]` **F6 — editor overlay leaked on teardown.** `_overlayRoot` is scene-parented, not freed with the tool; repeated Edit-mode entry/scene reloads leaked geometry. Added `_ExitTree` QueueFree. (`RegionTool.cs`.)
  - `[low]` `[patch]` **EC3+F11 — drag state hygiene.** Leaving Edit mode mid-drag left `_dragging` stuck (stale-rect commit on return); toggling `G` mid-drag mixed snapped/unsnapped corners. Now cancels the drag on mode-leave and ignores `G` while dragging. (`RegionTool.cs`.)
  - `[low]` `[patch]` **F9+EC4 — undo/redo was not order-faithful.** Redo/undo re-appended a region at the end, reshuffling index-derived colors and on-disk order. `AddRegion` now takes an insertion index; commit/delete closures capture and restore the original index. (`RegionTool.cs`.)
  - `[low]` `[patch]` **F12 — fragile test laundered a model through a failing validation.** `Apply_WithNoRegions` validated an intentionally-invalid model then applied `.Value` via shadow-mode. Rewritten to build a valid region-less model and assert the Empty store behaviorally. (`ScenarioApplierRegionTests.cs`.)
- rejected (4): **F2** (fold a region-bounds digest into the map-package hash) — the exclusion posture is intent-sanctioned (Triggers precedent; mirrors 6.3's accepted headless-server rejection); the residual risk is documented and the accurate framing is handled by F1. **F8** (overlay teardown/rebuild churn) — premature optimization; region counts are small. **F10** (duplicate region *names* allowed) — cosmetic; unique ids disambiguate. **F13** (per-tick O(entities) containment scan) — acceptable for 1.0 (typically `match_start`-gated); noted under residual risks.

### 2026-07-14 — Review pass 2 (independent follow-up: 4 layers — blind/edge/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 3, low 0)
- defer: 0
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` **A — validator/applier degeneracy disagreement (silent dead trigger).** 3-reviewer consensus (Edge#1, Blind F1, VG main). `ScenarioValidator` checked rect non-degeneracy in **float** (`MinX < MaxX`) while `ScenarioApplier.BuildRegionStore` re-checks after **Fixed** (16.16) quantization and silently `continue`s past a collapsed rect. A rect narrower than the Fixed step (~1/65536) passes validation but is dropped from the `RegionStore`, leaving any `unit_in_region` trigger that names it dead forever with no diagnostic (reachable via hand/LLM-authored JSON; the editor's 0.01 min-extent avoids it). Made the validator authoritative in the **same domain the sim resolves**: it now resolves the four corners through `Fixed.FromFloat` into a `FixedRect` and fails closed with a clear message if that collapses/inverts — so a region that passes validation is guaranteed to survive `BuildRegionStore`. (`ScenarioValidator.cs`; test `SubResolutionRect_ValidInFloatButCollapsesAtFixed_IsRejected`.)
  - `[medium]` `[patch]` **B — `ScenarioSerializer.Serialize` mutated its caller's model.** All 4 layers flagged (Edge#2, Blind F2, VG, Intent D2). The pass-1 empty→null normalization (`scenario.Regions = null`) was an in-place side effect on a live model the editor still holds, on a method that is a pure deterministic byte-source for golden-hash checks. Reworked to swap-to-null under `try/finally` and restore the original array reference afterwards — identical JSON bytes, caller's object observably unchanged. (`ScenarioSerializer.cs`.)
  - `[medium]` `[patch]` **C — `BuildRegionStore` skip-logic + index-alignment were untested.** (Blind F10, VG main.) The pass-1 defensive guards (null row / non-finite corner / post-quantization degeneracy) had zero coverage, and a parallel-array (`Ids`/`Rects`) misalignment on skip would silently map a region to the wrong rect. Added a shadow-mode apply test driving all three malformed rows ahead of a good `hill` region and asserting no throw + that `hill` still fires its `unit_in_region → victory` (its index survived the drops). (`ScenarioApplierRegionTests.cs`.)
- rejected (9): **F4/VG-redundant** (`unit_in_region`-specific `CheckFactionSlot` is shadowed by the general per-condition faction check — harmless defense-in-depth; keep for locality). **Blind F3** (Triggers↔Regions hash-coupling tripwire — the exclusion is intent-sanctioned and the residual is documented; mirrors pass-1's F2 rejection). **Blind F7** (RegionTool captures `_scenario` once → stale on a multi-map editor reload — out of the intent's single-scenario edit/undo scope; follows the sibling-tool wiring pattern). **Blind F8** (overlay teardown/rebuild churn — premature; mirrors pass-1's F8). **Blind F9** (resize/delete undo closures capture region by ref → interleaved-delete orphan — speculative; reviewer could not construct a reachable corruption under LIFO history). **Blind F5** (float overlay vs Fixed sim fractional-edge mismatch — within Fixed resolution, cosmetic). **Blind F6** (per-tick O(alive) scan — mirrors pass-1's F13; documented residual). **Blind F11** (no two-host `SimChecksum` test — within-scenario determinism is already proxied; cross-scenario desync is the sanctioned Triggers-parity posture, not an invariant to lock green). **Edge#3** (duplicate region ids first-match-shadow in the store — shadow-mode-only; the validator is the real dup guard on the pass path).

## Design Notes

**Why include a trigger condition (not just inert map data).** The story title is "first-class map/**trigger** primitive," and the epic makes regions the thing Epic 7 win-condition presets "bind to." A region no trigger can reference would not be a trigger primitive and would leave Epic 7 to build both the query and the binding. So 6.4 ships the *minimal* trigger surface — one stateless `unit_in_region` condition + the deterministic containment helper it calls — proving the primitive end-to-end. Win-condition **presets** (the KotH wizard/evaluation) remain Epic 7; a creator can already hand-author `unit_in_region → victory` in raw JSON after 6.4. Anything beyond this single condition (enter/leave edge-events, occupancy state) is explicitly out of scope and a Block-If.

**The hash decision (the landmine, mirroring 6.3's discipline).** `CanonicalModelHash.Compute` is a manual field-by-field fold and already **excludes** `Triggers` (deferred to Epic 7). Regions are consumed only by the trigger system — no combat/AI/nav/movement consumer — so they sit in the exact same category and are excluded on the same basis. This keeps 6.4 **golden-neutral**: no `AlgoVersion` bump, no re-record, and the feature-off/no-regions path is byte-identical. The residual posture (a peer loading different region/trigger bytes isn't caught by the handshake) is the pre-existing `Triggers` posture, not a new gap; if a future ranked-MP story makes region-driven victory handshake-critical, THAT story folds both Triggers and Regions. The Block-If tripwire fires if any non-trigger sim system is found consuming regions.

**Determinism of the containment query.** Region bounds are static authored data resolved float→`Fixed` once at apply and never mutated mid-match, so they need no per-tick checksum coverage. The `unit_in_region` scan reads already-deterministic `FixedVec3` positions in ascending entity-id order with a `Fixed` inclusive point-in-rect test — introducing no new nondeterminism. Same rect on every client (same scenario) ⇒ same result.

**Editor seam.** No unified tools controller exists; `RegionTool` is a new sibling Node like `TerrainBrush`, using `TerrainBrush`'s drag state-machine and `EntityPlacer`'s Y=0 ground-plane raycast (`origin + dir * (-origin.Y/dir.Y)`). It shares the one injected `EditorHistory` (delegate-pair `Push(redo, undo)`), guards drawing-under-the-panel via `IsOverPanel` + `MouseFilter.Stop`, and renders labeled rect gizmos as `MeshInstance3D` `ImmediateMesh` line-loops + `Label3D` (net-new; no existing line-gizmo helper), gated on `GameMode.Edit`.

## Verification

**Commands:**
- `dotnet build godot.sln` -- expected: clean compile, 0 errors, no new analyzer warnings in touched sim files (banned-API analyzer stays green — no `float`/`Mathf`/`System.Random` on the `FixedRect`/`RegionStore`/`ScenarioDirector` region path).
- `dotnet test ProjectChimera.Sim.Tests` -- expected: all new region tests green; **no golden moves** — assert exactly zero `.golden.txt` files change, `hero-start-state.golden.txt` unchanged, `CanonicalModelHash.AlgoVersion == 5`, `StartStateHash.AlgoVersion == 2`. Note the 2 pre-existing `PersistenceManifestTests` failures on baseline (confirm unrelated via `git stash` if seen); any *additional* golden/hash movement means regions were wrongly folded — STOP and fix.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for the Region tool, drag input, overlay render, or `.chimera.zip` UI):**
- Draw a named region rect in Edit mode; confirm the labeled outline renders in the viewport and is hidden when switching to Play.
- Save and reload the map (and export→import `.chimera.zip`); confirm the region persists with identical name/bounds.
- Exercise undo/redo across add/delete/resize interleaved with entity placement; confirm no cross-corruption.
- Hand-author a `unit_in_region(R, F) → victory` trigger; move a unit of F into R and confirm victory fires; confirm it does not fire when no F unit is inside.
- Confirm `ScenarioValidator` rejects a map with a duplicate/empty region id, an inverted rect, an out-of-bounds corner, and a dangling `region_id` reference.

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 11 patches applied, 0 deferred, 4 rejected, committed)

### Implemented change
Regions shipped as a rect-only, first-class map/trigger primitive across four thin layers, golden-neutral (no hash fold, no re-baseline): (1) a `ScenarioRegion` row + `Regions[]?` on `ScenarioData` persisting inside `scenario.json`/`.chimera.zip` via the omit-when-null pattern; (2) a Godot-free deterministic `FixedRect` + `RegionStore` (float→`Fixed` resolved once at apply, inclusive point-in-rect); (3) a stateless `unit_in_region` trigger condition wired into `ScenarioDirector.EvalCondition` (ascending-entity-id `Fixed` scan by faction), referenced by string `region_id`; (4) a Creation Suite `RegionTool` drag-rect editor (named zones, right-dock Simple/Advanced panel, labeled 3D overlay, shared undo/redo) wired via a new `RegionToolPhase`. Regions are excluded from `CanonicalModelHash`/`StartStateHash` on the same accepted basis as Triggers.

### Files changed (one line each)
- `godot/src/Core/FixedRect.cs` (NEW) — Godot-free `Fixed`-only axis-aligned rect + inclusive `Contains(x,z)`/`Contains(FixedVec3)`.
- `godot/src/Core/RegionStore.cs` (NEW) — Godot-free `Ids[]`/`Rects[]` store: `Count`, `TryGetIndex`, `Contains`, shared `Empty`; degenerate/empty-safe.
- `godot/src/Core/Definitions/ScenarioData.cs` — `ScenarioRegion` row + `Regions[]?` (omit-when-null); accurate hash-exclusion comment (P1).
- `godot/src/Core/Definitions/TriggerDefinition.cs` — `region_id` on `TriggerCondition` + `unit_in_region` type.
- `godot/src/Core/ScenarioDirector.cs` — holds `RegionStore` (`SetRegionStore`); `unit_in_region` case (ascending-id, `IsAlive`-guarded, `Fixed` point-in-rect by faction).
- `godot/src/Core/Sim/ScenarioApplier.cs` — `BuildRegionStore` (single float→`Fixed` boundary) wired before `LoadScenario`; skips null/non-finite/degenerate regions (P2).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — `unit_in_region` in the condition vocabulary; fail-closed regions loop (unique non-empty id, `MinX<MaxX && MinZ<MaxZ`, corners within `MapBounds`); dangling-`region_id` + faction-slot checks (P3).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — comment-only: Regions excluded on the Triggers basis; `AlgoVersion` stays 5 (P1).
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — empty `Regions` normalized to null at the serialize chokepoint so `[]` never drifts pinned bytes (P4).
- `godot/src/CreationSuite/RegionTool.cs` (NEW) — drag-rect draw tool (toggle `I`), named zones, Simple/Advanced coord panel, grid-snap `G`, `IsOverPanel`+`MouseFilter.Stop` guard, labeled `ImmediateMesh`+`Label3D` overlay gated on `GameMode.Edit`, shared-history add/delete/resize undo, `MapBounds` rejection + status feedback (P8), `_ExitTree` overlay free (P7), mid-drag hygiene (P9), order-faithful undo (P10).
- `godot/src/Core/Bootstrap/Phases/RegionToolPhase.cs` (NEW) — wires the tool (injects Cam/GameState/Scenario + shared `Placer.History`); log-skips on null handles (P6).
- `godot/src/Core/MainScene.cs`, `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`, `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — registered `RegionTool` phase after `ScenarioLoad` (needs the loaded `_ctx.Scenario`); phase-order contract updated in lockstep.
- Tests (NEW): `FixedRectTests`, `RegionStoreTests`, `ScenarioDataRegionsTests` (round-trip + null/empty omit), `ScenarioValidatorRegionTests`, `ScenarioApplierRegionTests`, `UnitInRegionConditionTests` (incl. dead-unit guard), `CanonicalModelHashRegionExclusionTests` (hash/AlgoVersion exclusion teeth).

### Review findings breakdown (review pass 1)
- Patches applied (11): F1 comment reframe; VG3/EC1/EC2/F4 `BuildRegionStore` robustness; VG1 dead-unit guard test; VG2 empty-regions serialization normalization + test; F3/EC5 out-of-bounds authoring feedback; F5 phase null-guard; F7/EC6 faction-slot validation; F6 overlay-leak fix; EC3/F11 drag-state hygiene; F9/EC4 order-faithful undo; F12 test de-fragilize.
- Deferred (0).
- Rejected (4): F2 (map-package region digest — intent-sanctioned exclusion, mirrors 6.3), F8 (overlay rebuild churn — premature), F10 (duplicate names — cosmetic), F13 (per-tick scan perf — fine for 1.0).

### Verification
- `dotnet build godot.sln` → Build succeeded, 0 errors, 0 warnings (no new warnings in touched files).
- `dotnet test ProjectChimera.Sim.Tests` → 1559 passed, 1 skipped, 2 failed. The 2 failures are the pre-existing `PersistenceManifestTests` (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`) — documented baseline failures at `1bf12b1` (also flagged in the 6.3 spec), unrelated to regions and unaffected by the P4 serializer change (their scenarios carry no regions, so empty→null is a no-op).
- Golden-neutrality confirmed: zero `.golden.txt` changed; `CanonicalModelHash.AlgoVersion == 5`, `StartStateHash.AlgoVersion == 2`.
- Region test suite: 51 passed / 0 failed on the `Region|FixedRect|UnitInRegion` filter.

### Residual risks
- **Editor-surface verification gap (intent-sanctioned, Tier-1 architecture):** `RegionTool` drag input, 3D overlay render, panel undo/redo, and the `.chimera.zip` UI round-trip have no Godot-free xUnit surface — routed to manual godot-mcp/godot-verify checks (the 6.1/6.2/6.3 precedent). Not exercised headlessly here; should be confirmed in-editor: draw/name/resize/delete a region with undo/redo, save+reload and export/import a package preserving bounds/name/id, overlay visible in Edit and hidden in Play, and a hand-authored `unit_in_region → victory` trigger firing only when a faction unit is inside.
- **MP handshake posture (documented, unchanged):** regions are excluded from `CanonicalModelHash`/`StartStateHash` on the same basis as Triggers, so two peers loading different region bytes are not rejected at join — the pre-existing Triggers posture, not a new gap. When Epic 7 makes region-driven victory ranked-critical, that story folds Triggers (and Regions) into the handshake.
- **F13 (rejected):** each `unit_in_region` evaluation is an O(alive-entities) scan; fine for 1.0 (typically `match_start`-gated), but a `unit_in_region` bound to a high-frequency event over a large army would multiply cost.
- **Follow-up review recommended:** the review pass applied 11 patches spanning sim-victory correctness, the serializer chokepoint, validator fail-closed behavior, and editor lifetime/undo — significant by volume and breadth; an independent follow-up review is warranted.

---

## Auto Run Result — Review pass 2 (independent follow-up)

Status: **done** (independent 4-layer follow-up review; 3 medium patches applied, 0 deferred, 9 rejected, committed).

### What pass 2 did
Ran a fresh 4-layer adversarial review (blind / edge-case / verification-gap / intent-alignment) over the full `1bf12b1..HEAD` region diff. No intent_gap and no bad_spec surfaced — the intent-alignment auditor confirmed the diff implements the intent's chosen readings and trips no Block-If/Never. Three medium patches (strong cross-reviewer consensus) were applied; the remaining nine findings were rejected as noise / intent-sanctioned / out-of-scope (several re-raising pass-1's already-rejected items).

### Files changed (pass 2)
- `godot/src/Core/Definitions/ScenarioValidator.cs` — **Patch A:** the region loop now resolves the four corners through `Fixed.FromFloat` into a `FixedRect` and fails closed if it degenerates/inverts at 16.16 resolution, so the float-domain validator and the Fixed-domain applier agree (a validated region is guaranteed to survive `BuildRegionStore`; no more silently-dropped region orphaning a `unit_in_region` trigger).
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — **Patch B:** empty→null `Regions` normalization no longer mutates the caller's model; it swaps-to-null under `try/finally` and restores the original reference (identical bytes, no side effect on a live editor-held model).
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorRegionTests.cs` — **Patch A test:** `SubResolutionRect_ValidInFloatButCollapsesAtFixed_IsRejected`.
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierRegionTests.cs` — **Patch C test:** `Apply_ShadowMode_SkipsMalformedRegions_KeepsIndexAlignment_NoThrow` (null row + non-finite corner + Fixed-collapsing rect ahead of a good region; asserts no throw and correct index alignment via a firing `unit_in_region → victory`).

### Verification (pass 2)
- `dotnet build godot.sln` → Build succeeded, 0 errors. 7 warnings, all pre-existing in untouched files (GatheringSystem/ResourceNodeStore/ResourceStore/FlowFieldSystem/UnitCardPanel) — none in the files this pass touched.
- Region suite (`Region|FixedRect|UnitInRegion` filter) → 53 passed / 0 failed (was 51; +2 new tests).
- Full `ProjectChimera.Sim.Tests` → 1561 passed, 1 skipped, 2 failed. The 2 failures are the documented pre-existing `PersistenceManifestTests` (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`); **re-confirmed pre-existing this pass via `git stash`** (they fail identically without the pass-2 changes — unrelated to regions; the shipped scenarios carry no regions so Patch A's new branch never runs on them).
- Golden-neutrality preserved: patches touch only the validation gate (malformed-region rejection), the serialize chokepoint (byte-identical output), and tests — no `.golden.txt`, `CanonicalModelHash.AlgoVersion` (5), or `StartStateHash.AlgoVersion` (2) affected.

### Follow-up review recommendation: **false**
Three localized, convergent fixes — exactly what three independent reviewers agreed on — each covered on both sides by a new Tier-1 test. Far smaller and more contained than pass 1's 11-patch sweep; no further independent review warranted.

### Residual risks (unchanged from pass 1, still open)
- **Editor-surface verification gap (intent-sanctioned):** `RegionTool` drag input, 3D overlay render, panel undo/redo, and the `.chimera.zip` UI round-trip have no Godot-free xUnit surface — routed to manual godot-mcp/godot-verify checks. The intent-alignment auditor (D1) noted the same: the intent's most user-visible layer-4 expectations are verified by code-reading/in-engine inspection, not the test suite.
- **MP handshake posture (documented, unchanged):** regions excluded from `CanonicalModelHash`/`StartStateHash` on the Triggers basis; a future ranked-MP story folds both.
- **Per-tick O(alive) `unit_in_region` scan (rejected F6/F13):** fine for 1.0; a high-frequency-event binding over a large army would multiply cost.
