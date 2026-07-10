---
title: 'FactionRegistry — canonical faction-slot constants (AR-3)'
type: 'refactor'
created: '2026-07-10'
status: 'done'
baseline_revision: '353d8e20aa55b0d023e200666b1e6260dfb89027'
final_revision: 'f46d3cd7cca894594cc248954a46ee763a4f4539'
review_loop_iteration: 1
followup_review_recommended: false
context: []
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** `MainScene` hardcodes P1/P2 faction-JSON constants and a size-5 `_slotFactionDefs` array (built by Story 1.3a's `FactionRegistry` and left with a `// TODO(5.1)` marker) instead of having the registry own per-slot `FactionDefinition` lookups. `MainScene.BuildHeadlessServerSimHost` duplicates the same array + an inline `(Faction)(slot+1)` cast for the headless server path, and `ScenarioLoadPhase.ResolveSlotFactionDefs` also inlines that cast instead of using `FactionRegistry.ToFaction` (the "one place" the class already documents as owning it).

**Approach:** Extend `FactionRegistry` with a `SlotDefinitions` array it owns; migrate both MainScene composition roots (client `_Ready` and `BuildHeadlessServerSimHost`) to populate and read that array instead of a locally-allocated one, constructing the registry before it's needed instead of inline; route the two duplicate `(Faction)(slot+1)` casts through `FactionRegistry.ToFaction`. Byte-identical checksum behavior throughout — this is data-ownership relocation, not new logic.

## Boundaries & Constraints

**Always:**
- `FactionRegistry` stays pure C# — no `using Godot`, no Godot Node types (sim layer; AC4).
- `res://` path resolution and file I/O for faction JSON stay in `MainScene` (the Godot presentation edge) — the registry only stores already-loaded `FactionDefinition` objects, it never resolves a path itself.
- `SlotDefinitions` is sized **5** (current `Faction` enum cardinality: Neutral+Player1..4) — matching `ResourceStore`/`MatchStats`/`BuildingSystem`/`ResearchSystem`'s existing `FACTION_COUNT=5` arrays — NOT `FACTION_ARRAY_SIZE` (9). Document the rationale (see Design Notes) at the declaration site so it isn't "fixed" later.
- Every `(Faction)(slot+1)` → `FactionRegistry.ToFaction(slot)` substitution must be behavior-identical for identical input (same enum value out).
- `PLAYER_COUNT=8` / `FACTION_ARRAY_SIZE=9` remain unchanged public named constants (already correct from Story 1.3a) — do not alter their values or visibility.
- The committed goldens (`golden-scenario.golden.txt`, `golden-multifaction.golden.txt`) must reproduce byte-identical, unmodified.

**Block If:** Any of the two `(Faction)(slot+1)` → `FactionRegistry.ToFaction(slot)` substitutions (in `ScenarioLoadPhase.ResolveSlotFactionDefs` or `MainScene.BuildHeadlessServerSimHost`) would change the resulting `Faction` value for any input `slot` already reachable in the two shipped scenarios/goldens — HALT with status `blocked`, blocking condition `slot-cast substitution changes behavior`, and name the divergent site.

**Never:**
- Never resize `SlotDefinitions`/the slot array to `FACTION_ARRAY_SIZE` (9) or touch `ResourceStore`/`MatchStats`/`BuildingSystem`/`ResearchSystem`'s `FACTION_COUNT=5` arrays or the `Faction` enum — that is Story 9.2's territory. `ScenarioApplier.InFactionRange` derives its bounds-safety from the slot array's `.Length` matching `ResourceStore.Ore`/`FactionBase`'s still-5-sized arrays; widening only the slot array reopens an `IndexOutOfRangeException` for slots 4-7 that today are correctly rejected.
- Never derive `ActiveFactions` from `SlotDefinitions` occupancy, and never change the `FactionRegistry(int activePlayerCount)` constructor's contract or validation range. ~40 existing test and production call sites construct `new FactionRegistry(N)` purely for checksum iteration and never populate `SlotDefinitions` — coupling the two would silently empty `ActiveFactions` for all of them.
- Never touch the `(Faction)(slot+1)` / `(Faction)(x+1)` casts in `ScenarioApplier.cs`, `ScenarioDirector.cs`, or `ScenarioDelegateBinder.cs` — those are downstream consumers of already-resolved slot data (or unrelated per-entity faction math), not slot-array producers, and sit outside this story's named brownfield surface.
- Never add `FactionValidator`, `ai_preset`, signature-mechanic, or hero/persistence schema fields — that is Story 5.2.
- Never re-baseline or re-record either golden file, and never bump `checksum_algo_version`.
- Never touch `StartPositionBridge`'s `[2]`-sized position array — it holds presentation start-marker coordinates, not `FactionDefinition` lookups or a `(Faction)(slot+1)` cast, and is outside AR-3's stated scope.

</intent-contract>

## Code Map

- `godot/src/Core/FactionRegistry.cs` -- add the `SlotDefinitions` per-slot `FactionDefinition?[]` (size 5), resolving the file's own `// TODO(5.1)` marker; add a bounds-checked `GetSlotDefinition(Faction)` lookup (AC3); name the array size as a small public const instead of a bare literal.
- `godot/src/Core/MainScene.cs` -- `_Ready`'s client faction-load path (~L303-330, ~L344-364): construct the `FactionRegistry` before `SimulationHost.Create`, alias `_slotFactionDefs` to its `SlotDefinitions`, drop the standalone `new FactionDefinition?[5]` allocation and the inline `new FactionRegistry(2)`. `BuildHeadlessServerSimHost` (~L1409-1456): mirror the same migration for the headless server path, including the `(Faction)(slot.Slot + 1)` → `FactionRegistry.ToFaction(slot.Slot)` substitution, with an accurate comment about the `factions`/`slotDefs` relationship to `ServerBootstrap.Build`.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- `ResolveSlotFactionDefs` (~L92-115): `(Faction)(slot.Slot + 1)` → `FactionRegistry.ToFaction(slot.Slot)`, with an updated inline comment (no longer restating the raw offset).
- `godot/ProjectChimera.Sim.Tests/Golden/FactionRegistryTests.cs` -- add `SlotDefinitions` unit tests (size, default-null, round-trip set/get) and `GetSlotDefinition` unit tests (in-range unassigned, assigned, out-of-range).

## Review Loop 1 — Amendment Rationale

Adversarial review (4 parallel layers) against the first implementation surfaced a `bad_spec` finding: this spec's original Tasks & Acceptance never captured epics.md Story 5.1's third AC — **"Given a registry lookup for an unassigned or out-of-range slot, when code requests that slot's faction, then it returns a safe empty/neutral default rather than throwing or indexing out of bounds."** The first implementation (correctly following the spec as originally written) exposed only a bare `SlotDefinitions` array with no bounds-checked accessor — any out-of-range `Faction` index would throw `IndexOutOfRangeException`, the literal opposite of that AC. This amendment adds the missing accessor. Everything else about the original design (the `SlotDefinitions` array itself, its size-5 rationale, the two `ToFaction` cast substitutions, the client/server aliasing) was independently validated as correct by the same review and is unchanged below — only new tasks are added.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/FactionRegistry.cs` -- add `public const int SLOT_DEFINITIONS_SIZE = 5;` (named, not a bare literal; doc comment carries the size-5 rationale from Boundaries), `public FactionDefinition?[] SlotDefinitions { get; } = new FactionDefinition?[SLOT_DEFINITIONS_SIZE];`, add `using ProjectChimera.Core.Definitions;`, and replace the stale `// TODO(5.1): hold per-slot FactionDefinition[] and derive ActiveFactions from assigned slots.` comment with an accurate doc note that `ActiveFactions` intentionally stays `activePlayerCount`-driven (see Design Notes) -- fulfills AC1's "registry holds per-slot lookups."
- `godot/src/Core/FactionRegistry.cs` -- add `public FactionDefinition? GetSlotDefinition(Faction faction) { int idx = (int)faction; return idx >= 0 && idx < SlotDefinitions.Length ? SlotDefinitions[idx] : null; }` with a doc comment naming this as the AC3 bounds-checked lookup: returns `null` (safe empty default) for both an unassigned in-range slot and an out-of-range `Faction` value, never throws -- fulfills epics.md AC3 verbatim ("a registry lookup for an unassigned or out-of-range slot... returns a safe empty/neutral default rather than throwing or indexing out of bounds"). This is additive, read-only API; no existing caller is required to adopt it in this story.
- `godot/src/Core/MainScene.cs` (`_Ready`) -- move `var factions = new FactionRegistry(2);` up to where `_slotFactionDefs` is currently allocated; replace `_slotFactionDefs = new FactionDefinition?[5];` with `_slotFactionDefs = factions.SlotDefinitions;`; keep the two `_slotFactionDefs[(int)Faction.Player1] = _factionDef;` / `Player2` assignments unchanged (now writing into the registry's array); pass `factions` into `SimulationHost.Create(...)` in place of the inline `new FactionRegistry(2)`; remove the now-resolved `TODO(5.1)` comment -- extracts the size-5 hardcode into the registry, behavior-preserving.
- `godot/src/Core/MainScene.cs` (`BuildHeadlessServerSimHost`) -- replace `var slotDefs = new FactionDefinition?[5];` with `var factions = new FactionRegistry(2); var slotDefs = factions.SlotDefinitions;`; replace `var f = (Faction)(slot.Slot + 1);` with `var f = FactionRegistry.ToFaction(slot.Slot);`. Add a short comment noting that only `slotDefs` (the array) is threaded into `ServerBootstrap.Build` below -- `ServerBootstrap.Build` constructs its own separate internal `FactionRegistry` for checksum purposes (pre-existing, unchanged), so the local `factions` variable here is a slot-storage source only, not wired into the host's checksum registry. This avoids overclaiming full parity with the client path while keeping the array-sourcing migration itself correct.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` (`ResolveSlotFactionDefs`) -- replace `var faction = (Faction)(slot.Slot + 1);` with `var faction = FactionRegistry.ToFaction(slot.Slot);`; update the trailing `// slot 0 → Player1, slot 1 → Player2` comment so it no longer restates the raw offset now that `ToFaction` is the named, documented source of truth for it (e.g. `// resolved via the one canonical cast site`).
- `godot/ProjectChimera.Sim.Tests/Golden/FactionRegistryTests.cs` -- add: a fresh `FactionRegistry(2).SlotDefinitions` has `Length == FactionRegistry.SLOT_DEFINITIONS_SIZE` and all elements `null`; setting `SlotDefinitions[(int)Faction.Player1] = someDef` and reading it back returns the same reference; `GetSlotDefinition` returns the assigned reference for an assigned in-range slot, `null` for an unassigned in-range slot, and `null` (not a thrown exception) for an out-of-range `Faction` value (e.g. `(Faction)99`) -- unit coverage for both the storage and the new AC3 accessor.

**Acceptance Criteria:**
- Given `MainScene._Ready` (client path), when factions are loaded at match start, then `FactionRegistry.SlotDefinitions` holds the resolved P1/P2 `FactionDefinition` objects indexed by `(int)Faction`, and `_slotFactionDefs`/`_ctx.SlotFactionDefs` reference that same array (no independent allocation remains).
- Given the `FactionRegistry` source file, when inspected, then it contains no `using Godot` and no Godot Node type (structurally covered by the existing `GodotFreeBoundaryTest`).
- Given `ScenarioLoadPhase.ResolveSlotFactionDefs` and `MainScene.BuildHeadlessServerSimHost`, when a scenario player slot resolves to a `Faction`, then both call `FactionRegistry.ToFaction(slot)` rather than an inline `(Faction)(slot+1)` cast.
- Given a freshly constructed `FactionRegistry`, when inspected before any slot is populated, then `SlotDefinitions.Length == SLOT_DEFINITIONS_SIZE (5)` and every element is `null`.
- Given a `FactionRegistry.GetSlotDefinition(faction)` call, when `faction` is unassigned-but-in-range or genuinely out-of-range (any value whose `(int)` cast falls outside `[0, SLOT_DEFINITIONS_SIZE)`), then it returns `null` and never throws (AC3, verbatim from epics.md).
- Given the full Tier-1 suite including both golden files, when run after this refactor, then all tests stay green and both golden files reproduce byte-identical / unmodified (`git status` shows no diff on either `.golden.txt`) -- proving the extraction changed no runtime behavior.

## Spec Change Log

### 2026-07-10 — Review Loop 1

- **Triggering finding:** `bad_spec` (high) — the spec's Tasks & Acceptance never included epics.md Story 5.1's AC3 ("a registry lookup for an unassigned or out-of-range slot... returns a safe empty/neutral default rather than throwing or indexing out of bounds"). Independently surfaced by the Intent Alignment Auditor (AC3 has no accessor anywhere on `FactionRegistry`) and corroborated by the Edge Case Hunter (a concrete reachable unguarded-index path exists in a sibling consumer).
- **What was amended:** Added a `GetSlotDefinition(Faction)` bounds-checked accessor to `FactionRegistry.cs` (Tasks & Acceptance, Code Map, Design Notes) and its unit tests. Named the array-size literal as `SLOT_DEFINITIONS_SIZE` instead of a bare `5` (folded in as a low-risk improvement alongside the required fix, since the implementation was being re-derived regardless). Added two small comment-accuracy fixes (the `BuildHeadlessServerSimHost` "mirrors the client path" comment now states the `factions`/`ServerBootstrap.Build` relationship precisely; `ScenarioLoadPhase`'s stale offset comment no longer restates raw offset math `ToFaction` already owns).
- **Known-bad state avoided:** Shipping `FactionRegistry` with no safe lookup path — any future caller passing an out-of-range or unassigned slot to a naive `SlotDefinitions[idx]` read would throw `IndexOutOfRangeException` instead of getting the safe default the epic explicitly requires.
- **KEEP (validated correct by this review, unchanged):** the `SlotDefinitions` array itself and its size-5 rationale (`ScenarioApplier.InFactionRange` bounds-safety dependency); the two `(Faction)(slot+1)` → `FactionRegistry.ToFaction(slot)` substitutions in `ScenarioLoadPhase.ResolveSlotFactionDefs` and `MainScene.BuildHeadlessServerSimHost`; the client-path aliasing (`_slotFactionDefs = factions.SlotDefinitions`, `factions` threaded into `SimulationHost.Create`); the original `SlotDefinitions` default-state and round-trip unit tests; the decision to leave `ActiveFactions` decoupled from slot occupancy.
- **Deferred, not fixed here (see Design Notes' "Known, deliberately out-of-scope items" and `deferred-work.md`):** the `FactionRegistry(5..8)` ctor-vs-`SLOT_DEFINITIONS_SIZE` mismatch (pre-existing, same class as a finding Story 1.3a's own review already deferred to Story 9.2); the missing bounds guard on `ScenarioLoadPhase.ResolveSlotFactionDefs`'s write (pre-existing, write-side, outside AC3's read-only scope).

## Review Triage Log

### 2026-07-10 — Review pass

- intent_gap: 0
- bad_spec: 1 (high 1, medium 0, low 0)
- patch: 4 (high 0, medium 0, low 4)
- defer: 3 (high 0, medium 3, low 0)
- reject: 4 (high 0, medium 0, low 4)
- addressed_findings:
  - none

### 2026-07-10 — Review pass 2

- intent_gap: 0
- bad_spec: 0
- patch: 2 (high 0, medium 0, low 2)
- defer: 0 (all restated findings map to already-existing deferred-work entries DW-94/DW-96 from pass 1; no new entries)
- reject: 15 (high 0, medium 0, low 15)
- addressed_findings:
  - `[low]` `[patch]` No test pinned the exact off-by-one boundary of `GetSlotDefinition` (`(Faction)SLOT_DEFINITIONS_SIZE`, index 5 — the first genuinely invalid index; the existing out-of-range test used `(Faction)99`, which cannot catch a `<=` vs `<` mistake). Added `GetSlotDefinition_ExactlyAtBoundary_ReturnsNullNeverThrows` to `FactionRegistryTests.cs`.
  - `[low]` `[patch]` `MainScene.cs`'s comment claimed "TODO(5.1) resolved," overclaiming — the original TODO had two halves (hold per-slot storage; derive `ActiveFactions` from assigned slots) and only the first is done, by design. Reworded to "TODO(5.1) partially resolved" with an explicit pointer to why the second half is intentionally not done.

## Design Notes

**Why `SlotDefinitions` stays length-5, not `FACTION_ARRAY_SIZE` (9):** `ScenarioApplier.InFactionRange` (the fail-safe gate before every `_slotFactionDefs`/`Resources.Ore`/`Resources.FactionBase` index) currently reads `fIdx < _slotFactionDefs.Length` as its bound. Those three arrays are today all length-5 by convention (an invariant the codebase already leans on in `BuildingSystem.cs`/`ResearchSystem.cs`'s own `new FactionDefinition?[5]` comments). If only the slot array grew to 9, `InFactionRange` would start accepting `fIdx` 5-8 as "in range," and a subsequent `Resources.Ore[fIdx]` (still length-5) would throw. Keeping the registry's array at 5 preserves that invariant with zero behavior change; widening it in lockstep with `ResourceStore`/`MatchStats` is Story 9.2's job, not this one's.

**Why `ActiveFactions` is not derived from `SlotDefinitions`:** the registry's `activePlayerCount`-driven constructor is used by dozens of call sites (goldens, unit tests, `ServerBootstrap`) purely to control checksum iteration span, independent of any `FactionDefinition` ever being loaded. Coupling `ActiveFactions` to slot occupancy would make every one of those construct an empty active-faction list. The two concerns (which factions are active for the tick loop, vs. which `FactionDefinition` backs each slot) stay decoupled, exactly as Story 1.3a's own scope fence anticipated.

**Why `GetSlotDefinition` is additive-only, not a replacement for existing direct-array access:** `ScenarioApplier.cs` already reads `_slotFactionDefs` (the same array, by reference) through its own pre-existing `InFactionRange` guard, which this story's Never section already forbids touching. `GetSlotDefinition` exists so `FactionRegistry` itself — the type this story is building — satisfies AC3 on its own terms; it does not require migrating already-safe existing consumers.

**Known, deliberately out-of-scope items surfaced by review (tracked in `deferred-work.md`, not fixed here):** (1) `FactionRegistry`'s constructor accepts `activePlayerCount` up to `PLAYER_COUNT` (8), but `SlotDefinitions`/`GetSlotDefinition` only cover indices `[0, SLOT_DEFINITIONS_SIZE)` (5) — a `FactionRegistry(5..8)` instance can have active factions with no corresponding slot storage. No live caller constructs `FactionRegistry(5..8)` and then touches `SlotDefinitions`, so this is dormant, and it is the same pre-existing ctor/enum-cardinality tension Story 1.3a's own review already named and deferred to Story 9.2. (2) The per-slot write path in `ScenarioLoadPhase.ResolveSlotFactionDefs` (`_ctx.SlotFactionDefs[(int)faction] = def;`) has no bounds guard against an out-of-range slot, unlike its sibling in `MainScene.BuildHeadlessServerSimHost` — pre-existing, not introduced by this story, and out of scope for AC3 (which concerns reads/lookups, not writes).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors.
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests green, including the new `SlotDefinitions` cases and every existing `Golden/*` test (byte-identical, no re-baseline).
- `git status --short -- godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt godot/ProjectChimera.Sim.Tests/Golden/golden-multifaction.golden.txt` -- expected: empty output (no diff on either committed golden).

## Auto Run Result

Status: done

**Summary:** `FactionRegistry` (AR-3, built by Story 1.3a) now owns per-slot `FactionDefinition` storage instead of `MainScene` allocating and holding an independent size-5 array. Both MainScene composition roots (client `_Ready` and the headless `BuildHeadlessServerSimHost`) construct the registry earlier and source their slot array from `factions.SlotDefinitions`; the two duplicate `(Faction)(slot+1)` casts (in `ScenarioLoadPhase.ResolveSlotFactionDefs` and `BuildHeadlessServerSimHost`) now route through `FactionRegistry.ToFaction`. A first review pass found the spec had missed epics.md's AC3 (a safe, bounds-checked lookup for an unassigned/out-of-range slot); the spec was amended to add `FactionRegistry.GetSlotDefinition(Faction)`, the code was re-derived, and a second full adversarial review pass came back clean (two trivial test/comment patches, no further gaps). Byte-identical throughout — both golden checksum files are unmodified.

**Files changed** (final state, after both implementation rounds and the two round-2 patches):
- `godot/src/Core/FactionRegistry.cs` — added `SLOT_DEFINITIONS_SIZE` (named const, 5), `SlotDefinitions` (`FactionDefinition?[]`), and the bounds-checked `GetSlotDefinition(Faction)` accessor (AC3); resolved the stale `TODO(5.1)` comment.
- `godot/src/Core/MainScene.cs` — `_Ready` and `BuildHeadlessServerSimHost` both construct `FactionRegistry` before use and source their slot array from it instead of a locally-allocated array; two `(Faction)(slot+1)` casts replaced with `FactionRegistry.ToFaction`; comments corrected (server-path parity clarified, `TODO(5.1)` wording fixed).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — `ResolveSlotFactionDefs`'s cast routed through `FactionRegistry.ToFaction`.
- `godot/ProjectChimera.Sim.Tests/Golden/FactionRegistryTests.cs` — 6 new tests: `SlotDefinitions` default-state/round-trip, `GetSlotDefinition` for assigned/unassigned-in-range/out-of-range/exact-boundary.
- `_bmad-output/implementation-artifacts/deferred-work.md` — 3 new entries (DW-94, DW-95, DW-96) for pre-existing, out-of-scope issues surfaced by review.

**Review findings breakdown:**
- Pass 1 (4 parallel layers: Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment): 1 `bad_spec` (high — missing AC3), 4 `patch`, 3 `defer` (→ DW-94/95/96), 4 `reject`. The `bad_spec` triggered a full spec amendment + code revert + re-derivation.
- Pass 2 (same 4 layers, against the amended diff): 0 `bad_spec`/`intent_gap`, 2 `patch` (both applied: a boundary-value test and a comment-accuracy fix), 0 new `defer` (all restated findings already covered by DW-94/DW-96), 15 `reject` (defensible scope-boundary readings, explicitly licensed by epics.md's own "this story only builds the registry + migrates existing two-faction load" dev note, independently confirmed sufficient by the Verification Gap specialist both passes).

**Follow-up review recommendation:** `false` — the significant change (the AC3 accessor) already received a full independent 4-layer adversarial re-review after amendment, which came back clean.

**Verification performed:** `dotnet build godot/godot.csproj` (0 errors) and `dotnet build .../ProjectChimera.Sim.Tests.csproj` (0 errors) after every round; `dotnet test` full suite green each round (1385 passed, 1 pre-existing skip, 0 failed, final run); `git status --short` empty on both `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` after every round (byte-identical, no re-baseline).

**Residual risks:** Low. Pure data-ownership relocation, confirmed behavior-preserving by unchanged goldens and a full green suite. Three known, deliberately out-of-scope risks are now tracked in `deferred-work.md` (DW-94: `FactionRegistry(5..8)` is ctor-legal but has no corresponding slot storage — dormant, Story 9.2's job; DW-95: bounds-checking for slot-derived indices remains duplicated across three call sites; DW-96: `ScenarioLoadPhase.ResolveSlotFactionDefs`'s write path has no bounds guard — pre-existing, same class as an already-tracked story-1.8c deferral).

**Residual artifacts (not part of this change, left in place):** `godot/ProjectChimera.Sim.Tests/Definitions/ResearchWriteRoundTripTests.cs.uid` — a Godot editor-generated `.uid` sidecar unrelated to this diff, present in the working tree before this run started.
