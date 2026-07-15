---
title: 'Story 6.7: Map properties, New-Map flow, 2–4 start positions, and minimap preview'
type: 'feature'
created: '2026-07-15'
status: 'done'
baseline_revision: '9101bc260c0d48d9d0ffc904393e958303e0b7fe'
final_revision: '6b0199c'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** Maps are not self-describing and only ship at one implicit scale. There is no New-Map flow (the editor always loads a fixed scenario path), `ScenarioData` has no author/description/suggested-players fields, the start-position tool is hardcoded to exactly 2 players (P1/P2), the map package never generates the preview image its dead `thumbnail.png` manifest slot already promises, and the `.chimera.zip` manifest is populated from placeholder LineEdits rather than authored data.

**Approach:** Add authoring metadata (author/description/suggested-players) to `ScenarioData` as cosmetic omit-when-default fields; add a New-Map dialog + editable Map-Properties panel built from the existing design-system components that produce a blank map via a Godot-free `ScenarioData.CreateBlank(...)` factory with a chosen size; generalize the start-position tool to 2–4 slots through the `FactionRegistry` PLAYER_COUNT-aware API with a non-fatal "below suggested-players" advisory; and auto-generate a top-down minimap preview into the package `preview/preview.png` (reusing the `MinimapBridge` orthographic-SubViewport pattern) wired into the real manifest. **Map size is the authored playable half-extent (`ScenarioData.MapBounds`) chosen from a fixed supported set — NOT a variable grid dimension** (see Design Notes / Block-If).

## Boundaries & Constraints

**Always:**
- New metadata fields (`Author`, `Description`, `SuggestedPlayers`) are COSMETIC/authoring-only: excluded from `CanonicalModelHash` **and** `StartStateHash` (the `Id`/`DisplayName` exclusion precedent, `CanonicalModelHash.cs:18`); each serialized omit-when-empty/omit-when-default so every existing/flat/legacy scenario serializes byte-for-byte identically and moves no golden.
- `CanonicalModelHash.AlgoVersion` stays **7**; per-tick `SimChecksum.AlgoVersion` stays **15**; the 23 per-tick goldens + all `CanonicalModelHash`/`StartStateHash` fixtures are UNCHANGED (this story adds no hash-folded field and no algorithm change). `MapBounds` is already folded (`CanonicalModelHash.cs:95`); setting it per-scenario via New-Map is authored data, not an algorithm re-baseline — its default (`120f`) is unchanged.
- Start-position slots map to factions ONLY through `FactionRegistry` (`ToFaction(slot)`, `SLOT_DEFINITIONS_SIZE`, `GetSlotDefinition`) — never a hardcoded player-count loop bound or a new `P1/P2` literal list.
- Supported map sizes are a single Godot-free source of truth (a `MapSize` helper), every value ≤ the fixed grid half-extent (`FlowField.WORLD_HALF_INT` = 128) so no authored position can fall outside the fog/flow/pathability/spatial-hash coverage.
- Sim layer stays Godot-free / `Fixed`-only (`ScenarioData` factory, `MapSize`, validator, `ContentPackager` packaging path take no `using Godot;`).

**Block If:**
- The `MapBounds`-as-playable-extent reading cannot produce observably-different, valid, checksum-stable maps across the supported set (i.e. you conclude the AC genuinely requires resizing the fog/flow-field/pathability/spatial-hash grids themselves) → HALT `blocked` with blocking condition `map-size grid generalization required` (that is a determinism-critical 5-system re-baseline to be escalated via correct-course, per the epic RISK NOTE — do NOT attempt it inside this story and do NOT silently collapse to a single size).
- Satisfying the "below suggested-players" clause would require changing the fail-closed `ValidationResult` type (bool Ok + single Error) semantics used by every existing validation site → do NOT; route advisories through a separate non-fatal channel instead (see Design Notes). If that separation proves impossible, HALT `blocked` `validation-warning channel infeasible`.

**Never:**
- Never re-parameterize / resize the fog (`FogOfWarSystem`), flow-field (`FlowField`), pathability (`PathabilityGrid`), or spatial-hash (`SpatialHash`) grids, or change the 2048-byte pathability persist format, in this story — variable grid dimensions are escalated, not implemented here.
- Never author more than 4 start positions (engine ceiling is `Faction.Player4`; 5–8 is post-1.0 / Story 9.2); never let `SuggestedPlayers` exceed 4 for 1.0.
- Never fold `Author`/`Description`/`SuggestedPlayers` into any checksum/hash; never bump an AlgoVersion; never change a golden.
- Never gate the New-Map / preview ACs on a consuming UI — skirmish setup (11.1), MP lobby (9.7), and content-browser (9.10) image display do not exist yet; anchor on the observable package artifact.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Blank-map factory | `CreateBlank(name, author, desc, suggested=3, size=Medium)` | Valid `ScenarioData`: `MapBounds`=Medium's half-extent, metadata set, flat terrain (`TerrainRef=""`), empty units/buildings/nodes, `clamp(suggested,2,4)` default start slots; passes `ScenarioValidator.Validate` | Out-of-set size / suggested∉[2,4] → factory clamps/rejects with a clear message; never emits an invalid scenario |
| Metadata round-trip | Scenario with author/description/suggested_players set | Fields round-trip identically through save/load and `.chimera.zip`; hashes unchanged vs. the same scenario without them (cosmetic) | Empty/default values → keys omitted, byte-identical to pre-feature |
| Legacy scenario load | Existing 2-slot scenario, no new keys | All new keys absent; `CanonicalModelHash`/`StartStateHash`/per-tick goldens identical; loads unchanged | — |
| Start-position slots 2–4 | Author places/edits slots 0..3 with per-slot ore/crystal | Slots author through `FactionRegistry.ToFaction`; `PlayerSlots` round-trips; each valid slot maps to Player1..Player4 | Slot ≥ 4 / duplicate slot / OOB base → validator fails closed (existing checks) |
| Below-suggested advisory | `suggested_players=4`, only 2 start slots placed | `CollectAdvisories` returns a non-fatal "2 start positions for a 4-player map" advisory; `Validate` still passes (not fail-closed) | — |
| Suggested out of range | `suggested_players=5` (or 1) | `Validate` fails closed (1.0 ships 2–4) | Clear message |
| Preview packaging round-trip | `Pack` with preview PNG bytes → `Unpack` | `.chimera.zip` contains `preview/preview.png`; manifest references it; `Unpack` recovers the bytes and the path | No preview supplied → slot omitted, package still valid (pre-feature parity) |
| Grid-consistency guard | Supported-size set + grid constants | Test asserts fog/flow/pathability agree on cell identity AND every supported `MapBounds` ≤ `FlowField.WORLD_HALF_INT` AND spatial-hash coverage ⊇ that extent | A supported size exceeding coverage → test fails (prevents silent OOB) |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs` — **ADD** `Author` (`author`, omit-when-empty), `Description` (`description`, omit-when-empty), `SuggestedPlayers` (`suggested_players`, omit-when-default) near `MapBounds` (~:393), each with the `DisplayName`/`StartCrystal` cosmetic-exclusion doc pattern. **ADD** static `CreateBlank(displayName, author, description, suggestedPlayers, MapSize size)` factory building a valid empty scenario.
- `godot/src/Core/Definitions/MapSize.cs` — **NEW** Godot-free single source of truth for the supported size set: an enum/table mapping each size (e.g. Small/Medium/Large) → a `MapBounds` half-extent (all ≤ 128), with `All`, `ToBounds`, `FromBounds`, display labels. Used by the picker, factory, validator, and the guard test.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — **DOC ONLY**: extend the documented-exclusions note (~:18) to list `Author`/`Description`/`SuggestedPlayers` as cosmetic-excluded; **no fold, AlgoVersion stays 7**.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — fail-closed: `SuggestedPlayers ∈ [2,4]` when present; start-slot count ≤ 4 (engine ceiling already guarded ~:204). **ADD** a separate non-fatal `CollectAdvisories(ScenarioData) → IReadOnlyList<string>` returning the "start-position count below suggested-players" advisory (leaves `Validate`'s pass/fail semantics untouched).
- `godot/src/UI/EntityPlacer.cs` — generalize the hardcoded 2-slot start-position tool: replace the `("P1",0),("P2",1)` picker (~:1166-1181) and length-2 `_slotStartOre` (~:187) with a slot set sized by the registry ceiling (2–4), per-slot ore/crystal, ghost color/label per slot; add/remove slots; slot→faction via `FactionRegistry.ToFaction`. Keep the shared editor history contract.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — the Map-I/O owner: **ADD** a "New Map" affordance (dialog) and a Map-Properties editor (name/author/description/suggested-players/size) that binds live `ScenarioData` and persists on save; on `ExportMapPackage` (~:180) read `PackOptions` (~:202-212) from the real `ScenarioData` fields (not placeholder LineEdits); render + pass the minimap preview PNG before the `Pack` call (~:216).
- `godot/src/CreationSuite/MapPropertiesPanel.cs` (or reuse `PersistenceManifestPanel` pattern) — **NEW** editable properties panel from `ChimeraComponents.Controls` (`Input`/`Select`/`NumInput`) + the `ChimeraDialog` scrim/focus-trap for the New-Map modal (extend `ChimeraDialog` with a body-content slot or a parallel modal — it only supports a single body Label today).
- `godot/src/Core/Definitions/ContentPackager.cs` — **ADD** a `preview/preview.png` write mirroring the existing `thumbnail.png` block (`Pack` ~:135-137), an `Unpack` extract branch (~:218-228), and a `PackOptions.PreviewPngBytes` input (~:48). Point the manifest's existing dead `ThumbnailFile` field at `preview/preview.png` (wire the dead slot; honor the story's `preview/` naming).
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — set `ThumbnailFile` = `"preview/preview.png"` when a preview is packed (~:96).
- `godot/src/UI/MinimapPreviewRenderer.cs` (or a helper on the export path) — **NEW** presentation-only: reuse the `MinimapBridge` orthographic top-down `SubViewport` pattern (`MinimapBridge.cs:102-120`) to snapshot the world → `GetTexture().GetImage()` → resize → `SavePngToBuffer()` → PNG bytes fed to `PackOptions`. Godot-coupled → godot-verify surface.
- `godot/ProjectChimera.Sim.Tests/**` — NEW Godot-free tests (see Tasks): metadata round-trip + hash-exclusion, `MapSize`/factory, start-slot 2–4 + advisory, grid-consistency guard, preview packaging round-trip.

## Tasks & Acceptance

**Execution:**
- `ScenarioData.cs` — add `Author`/`Description`/`SuggestedPlayers` (omit-when-default, cosmetic-exclusion doc) + the `CreateBlank(...)` factory producing a valid blank map (flat terrain, empty entities, `clamp(suggested,2,4)` default start slots at spread base positions, chosen `MapBounds`).
- `MapSize.cs` (NEW) — the supported size set (each ≤ 128 half-extent) as the single Godot-free source of truth (`All`/`ToBounds`/`FromBounds`/labels).
- `CanonicalModelHash.cs` — DOC-ONLY exclusion note for the three new fields; verify no fold, AlgoVersion 7.
- `ScenarioValidator.cs` — fail-closed `SuggestedPlayers ∈ [2,4]`; add non-fatal `CollectAdvisories` with the below-suggested advisory (no change to `ValidationResult`/`Validate` semantics).
- `EntityPlacer.cs` — generalize the start-position tool to 2–4 slots via `FactionRegistry`; per-slot ore/crystal; add/remove; shared undo/redo intact.
- `WinConditionPhase.cs` (+ `MapPropertiesPanel.cs` NEW) — New-Map dialog + editable Map-Properties panel from design-system components; export reads real `ScenarioData` metadata into `PackOptions`; render + pass the minimap preview PNG before `Pack`.
- `ContentPackager.cs` + `ContentPackageManifest.cs` — `preview/preview.png` write/extract + `PackOptions.PreviewPngBytes` + manifest `ThumbnailFile` = `preview/preview.png`.
- `MinimapPreviewRenderer.cs` (NEW) — orthographic-SubViewport snapshot → resized PNG bytes (godot-verify surface).
- Tests — `ScenarioDataMapPropertiesTests` (author/description/suggested_players round-trip; empty/default → key absent; `CreateBlank` yields a valid scenario; hashes IDENTICAL with vs. without the three fields — cosmetic exclusion; per-tick goldens + `CanonicalModelHash.AlgoVersion==7` + `SimChecksum.AlgoVersion==15` unchanged); `MapSizeTests` (every supported size ≤ 128; round-trips `ToBounds`/`FromBounds`); `StartPositionSlotTests` (2–4 slots map via `FactionRegistry.ToFaction`; `PlayerSlots` round-trip; slot ≥4/dup/suggested∉[2,4] fail closed; below-suggested advisory is non-fatal and `Validate` still passes); `GridDimensionConsistencyTests` (fog↔flow↔pathability cell identity agree AND every `MapSize` bound ≤ `FlowField.WORLD_HALF_INT` AND spatial-hash coverage ⊇ it — the AC's per-system guard, currently absent); `ContentPackagerPreviewTests` (extend `ContentPackagerTerrainTests`) (Pack with preview PNG bytes → Unpack recovers `preview/preview.png` + manifest ref; no-preview → slot omitted, package valid).

**Acceptance Criteria:**
- Given the New-Map dialog, when the author sets name/author/description/suggested-players and picks a size from the supported set, then a blank map of that size is created (via `CreateBlank`) with `MapBounds` = the chosen size, fog/nav/flow-field/spatial-hash grids consistent and covering that extent (proven by `GridDimensionConsistencyTests`), the properties are editable later through the Map-Properties panel, and they persist into `scenario.json` and the package manifest.
- Given a scenario with author/description/suggested-players set, when it is serialized and hashed, then the fields round-trip through save/load and `.chimera.zip`, and `CanonicalModelHash`/`StartStateHash`/every per-tick golden are byte-identical to the same scenario without them (the fields are cosmetic-excluded; AlgoVersions unchanged).
- Given the start-position tool, when the author places/edits 2–4 start-position markers with per-slot starting units/resources, then the count is capped at the engine ceiling (4), each slot maps to a faction through `FactionRegistry`, `PlayerSlots` round-trips, and validation emits a non-fatal warning (not a fail-closed error) when the placed count is below `suggested_players`.
- Given a map is saved/exported, when the package writes, then a top-down minimap preview auto-generates into `preview/preview.png`, the manifest references it, and `Unpack` recovers it — anchored on the package artifact (the consuming UIs are later epics).
- Given a flat/legacy scenario with none of the new fields, when it is serialized and simulated, then all new keys are omitted, the bytes are identical to pre-feature for those sections, and all goldens are unchanged.

## Spec Change Log

## Review Triage Log

### 2026-07-15 — Review pass 1 (post-implementation adversarial review: 4 layers — blind/edge-case/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 13: (high 0, medium 5, low 8)
- defer: 3
- reject: 2
- addressed_findings:
  - `[medium]` `[patch]` **Start-slot mutation was ad-hoc, Godot-coupled, and mis-targeted non-contiguous slots (VG + Edge + Blind).** Removal filtered by `Slot` value while the caller passed an index, and the grow/append lived inside a `using Godot` `MainScene` method with no xUnit coverage. Extracted Godot-free `ScenarioData.UpsertStartSlot`/`RemoveStartSlot` (identity-by-Slot-value semantics), delegated `MainScene.MoveStartPosition`/`RemoveStartPosition` to them, and cleared the stale sim `FactionBase` on removal. New `StartSlotMutationTests` pins append/update/remove incl. the non-contiguous `{0,1,3}` cases + post-op `Validate().Ok`.
  - `[medium]` `[patch]` **Undo of a placement that created a new slot left a phantom slot at origin (Blind + Edge).** `MoveStartPosition`'s undo only repositioned, so an undone P3/P4 placement kept the appended `ScenarioPlayerSlot` (and a visible flag) and would save a slot the author undid. `_onStartPosMoved` now returns the `created` flag; the undo closure removes the created slot instead of repositioning it.
  - `[medium]` `[patch]` **Ore/Crystal spinner edits didn't persist to an already-placed slot; hash-folded StartCrystal could diverge from the spinner (Blind).** Spinner changes only wrote the local array; the `ScenarioPlayerSlot` updated solely on place/move. Added `ScenarioData.UpdateStartSlotEconomy` (updates in place, never appends) wired through a new `_onStartSlotEconomy` callback so a spinner edit persists immediately. Tested.
  - `[medium]` `[patch]` **AC2's "warns when below suggested-players" shipped as dead code (VG).** `CollectAdvisories` was computed and unit-tested but no editor code called it. Surfaced it (joined, "⚠"-prefixed, non-blocking) after Export and New-Map in `WinConditionPhase`; also added a new out-of-bounds-start advisory so a map-size shrink warns instead of failing the next hard validate with a cryptic message. Tested.
  - `[medium]` `[patch]` **New-Map silently overwrote an existing `{slug}.json` (Blind + Edge).** Two maps slugifying to the same filename clobbered each other. Added a `File.Exists` guard that aborts with a clear status message.
  - `[low]` `[patch]` **Preview renderer node leaked on a render exception (Blind + Edge).** `RenderMinimapPreview` freed the node only on success; wrapped the body in `try/finally { renderer.QueueFree(); }`.
  - `[low]` `[patch]` **Export handler had no re-entrancy guard (Blind).** Double-click Export could spawn overlapping renders/packaging; the button is now disabled for the duration in a `try/finally`.
  - `[low]` `[patch]` **Determinism doc cited the wrong precedent (Blind).** The `Author`/`Description`/`SuggestedPlayers` summaries called them cosmetic "like `StartCrystal`" — but `StartCrystal` is FOLDED into `CanonicalModelHash` and IS sim-affecting. Comment corrected to cite `DisplayName`/`Id`; code unchanged (was already correct).
  - `[low]` `[patch]` **Zero-length `PreviewPngBytes` wrote a zero-byte image referenced as valid (Edge).** Guarded both the write and the manifest-reference on `Length > 0`. Tested.
  - `[low]` `[patch]` **Minor guards (Edge).** 3-player package tag was mislabeled "1v1" → added `3 => "ffa3"`; `CreateBlank` guards `DisplayName = displayName ?? ""`; New-Map dialog guards `Mathf.Max(0, playersSelect.Selected)`.
  - `[low]` `[patch]` **Panel bind display/model mismatch (Edge).** An unsupported legacy `MapBounds` now normalizes to Medium on bind so the size picker and the model agree.
  - `[low]` `[patch]` **Divergent size rendering (Blind).** New-Map status text now routes the size through the shared `MapSizes.Label`/`FromBounds` helper instead of an inline `(int)MapBounds*2`.
  - `[low]` `[patch]` **Weaker-than-named guard tests (VG + Blind).** `GridDimensionConsistencyTests` now calls `FogOfWarSystem.WorldToCell` directly (not an inline reimplementation) and documents the `PathabilityGrid`→`FlowField` delegation; `AlgoVersions_Unchanged` now also pins `StartStateHash.AlgoVersion == 2`.
- deferred (3, in `deferred-work.md`): true variable-grid map-size generalization (the epic-RISK-NOTE correct-course escalation — this entry is that escalation record); remove-slot "−" undo symmetry + transient `_startSlotCount` (Godot-coupled interaction polish, godot-verify surface; data-at-rest is correct); Large-128 boundary-line cell aliasing at the `FlowField.WorldToCell` clamp (pre-existing WorldToCell convention, deterministic, same class as a 6.6 defer).
- rejected (2): content-browser/lobby preview *consumption* (the intent tags display to Stories 9.10/9.7/11.1 — out of scope on intent authority; this story anchors on the produced package artifact); legacy on-disk `ThumbnailPath` packaging round-trip test (the production caller only ever sets `PreviewPngBytes`; the `Unpack` change is behavior-preserving for `thumbnail.png` — no live regression).

### 2026-07-15 — Review pass 2 (follow-up adversarial review: 4 layers — blind/edge-case/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 2, low 1)
- defer: 5
- reject: 5
- addressed_findings:
  - `[medium]` `[patch]` **Map-size shrink could strand non-start content into a silent, unloadable export (Blind F1/F8).** `CollectAdvisories` warned only about start positions, so a shrink pushing buildings/units/resource-nodes/props/water past the new `MapBounds` produced a package whose manifest hash validates but whose `scenario.json` hard-fails `CheckCoord` on reload — with no warning. Extended `CollectAdvisories` to count every out-of-bounds coordinate-bearing collection (mirroring the hard validator's coverage) via a shared `OutOfBounds`/`OutOfBoundsCount` helper whose strict `> bounds` threshold matches `CheckCoord` exactly. New tests: content-outside/inside, per-object count, and a `±MapBounds` boundary-exactness test. (The remaining "no hard Validate() gate on the write path" is pre-existing → deferred.)
  - `[medium]` `[patch]` **Name/Author length caps were lost when the design-system panel took over authoring (Edge #4).** The pre-6.7 `WinConditionPhase` LineEdits capped Name at 64 / Author at 40; the new `MapPropertiesPanel` `ChimeraComponents.Input` controls set no `MaxLength`, so unbounded strings could flow into `scenario.json`, the manifest, and the export slug/filename. Restored `MaxLength` (Name 64 / Author 40, and a 240 cap on the new Description) in both the New-Map dialog and the live properties editor.
  - `[low]` `[patch]` **`MainScene.RemoveStartPosition` cleared a faction base unconditionally with a raw cast (Blind F7, Edge #3).** It ignored `RemoveStartSlot`'s bool and always ran `SetFactionBase((Faction)(slot+1), zero)` + hid the marker, so a no-match remove (non-contiguous set) could zero an unrelated faction's deposit point. Now no-ops when nothing was removed and routes the slot→faction offset through the canonical `FactionRegistry.ToFaction`.
- deferred (5, in `deferred-work.md`, appended as NEW entries): non-contiguous start-slot values mishandled in the editor (pre-existing; validator permits them; PATCH 3 hardens only the sim-base clear); no hard `Validate()` gate on the Export/New-Map write path (pre-existing); authored `MapBounds` not wired to camera pan-limits / NavMesh extent — only placement+hash change with size (Design-Notes "already wired to camera/NavMesh" is inaccurate); minimap preview shares the live edit-mode `World3D` and can capture editor gizmos into `preview/preview.png` (godot-verify surface); economy-spinner edits to a placed slot are not undoable despite mutating hash-folded `StartCrystal` (godot-verify interaction surface).
- rejected (5): redo-of-added-slot not restoring `_startSlotCount` (duplicate of the pass-1 deferred transient-`_startSlotCount` undo/redo-symmetry item — not re-opened); `SuggestedPlayers` picker showing unspecified-0 as "2" (low/cosmetic; the only trivial fix — write-back-on-bind — would mutate cosmetic data on mere view); New-Map collision message naming the wrong map on a slug collision (overwrite is correctly prevented; message-only, rare); preview bytes shipped without a PNG-signature check (speculative — `SavePngToBuffer` yields a valid PNG or the render returns null); out-of-bounds start advisory "contradicting" a hard `Validate` (intentional — the advisory previews a hard failure early; not a bug).

## Design Notes

**Map size = authored playable extent, NOT variable grid dimensions (the load-bearing decision).** The codebase has two disagreeing notions of "map size": `ScenarioData.MapBounds` (a float half-extent, default 120, already wired to camera/NavMesh/validator/AI and already folded into `CanonicalModelHash`), and a hardcoded ±128 grid identity shared byte-for-byte across fog (`FogOfWarSystem`, its OWN copy of the 128 constants), flow-field (`FlowField.WORLD_HALF_INT`), and pathability (`PathabilityGrid`, derived from `FlowField` with a fixed 2048-byte persist format), plus a separate ±160/32-dim `SpatialHash`. Truly resizing the map means re-parameterizing all five hardcoded, checksum-folding grid systems, changing the pathability persist format (invalidating every stored scenario's `pathability_blocked`), and re-baselining every `CanonicalModelHash`/`StartStateHash`/golden fixture — with no existing cross-consistency guard. That is the epic's flagged "riskiest slice," and the RISK NOTE pre-directs the resolution: *ship the fixed-size option set that works and escalate the rest via correct-course; do not silently hardcode one size.* So this story ships a real supported set of sizes as different `MapBounds` playable extents (all ≤ 128, so they sit safely inside the fixed grids — observably different camera/nav/placement bounds, zero determinism risk), adds the `GridDimensionConsistencyTests` guard the AC asks for (fog/flow/pathability/spatial-hash agree and cover the extent — a guard that does not exist today), and escalates true variable-grid dimensions to a `deferred-work.md` correct-course entry with the determinism-refactor rationale. This is intent-directed, not a resolved ambiguity — the Block-If fires only if this reading proves unable to yield valid, distinct, checksum-stable maps.

**Cosmetic metadata, zero golden movement.** `Author`/`Description`/`SuggestedPlayers` follow the `DisplayName`/`StartCrystal` precedents exactly: omit-when-empty/default so absent → byte-identical, and excluded from both hashes (they never affect the sim). The one existing hash-folded field this story touches, `MapBounds`, is authored per-scenario (already folded) — changing an authored value is expected map identity, not an algorithm re-baseline, so no AlgoVersion bump and no golden re-record.

**Validator warnings without breaking fail-closed.** `ValidationResult` is a binary `Ok`+`Error` struct used at every fail-closed site; adding severity there is invasive and risky. Instead a separate `CollectAdvisories` returns non-fatal strings the editor surfaces (badge/toast), leaving the pre-tick gate strictly pass/fail. The below-suggested-players case is an advisory (the AC says "warns"), while suggested∉[2,4], slot≥4, and duplicate slots stay hard fail-closed.

**Preview reuses the dead thumbnail slot.** The manifest already declares `ThumbnailFile`="thumbnail.png" but nothing ever generates it. Rather than add a parallel field, this wires that dead slot: the preview is written at `preview/preview.png` (honoring the story's `preview/` naming) and `ThumbnailFile` is pointed at it. The render reuses `MinimapBridge`'s orthographic top-down SubViewport pattern; the packaging round-trip is Godot-free and unit-tested, while the SubViewport snapshot itself is a godot-verify surface.

## Verification

**Commands:**
- `dotnet build godot.sln` — expected: clean compile, 0 new errors; no new banned-API/determinism analyzer warnings on the sim path (`ScenarioData`, `MapSize`, `ScenarioValidator`, `ContentPackager` stay `float`→`Fixed`-boundary-only, no `using Godot;`).
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all new tests green; the **23 per-tick goldens + `KnownWorldState_ProducesPinnedV15Hash` + `SimChecksum.AlgoVersion==15` + `CanonicalModelHash.AlgoVersion==7` UNCHANGED** (any movement = a wrongly-folded field → STOP and fix). Note the 2 pre-existing `PersistenceManifestTests` baseline failures (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`); confirm unrelated via `git stash` if seen — any OTHER new failure is real.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for the dialog, panel, slot picker, or SubViewport render):**
- Open the New-Map dialog; set all properties + pick each supported size; confirm a blank map is created at that extent (camera/placement bounds differ per size) and properties are editable afterward in the Map-Properties panel and survive save/reload.
- Place 2, 3, and 4 start positions with per-slot ore/crystal; confirm the below-suggested-players advisory appears (non-blocking) when count < suggested, and that a 5th slot / suggested=5 is rejected.
- Save/export a map; open the `.chimera.zip` and confirm `preview/preview.png` is a valid top-down image and the manifest references it; re-import and confirm round-trip.

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 13 patches applied, 3 deferred, 2 rejected, 0 spec loopbacks, committed)

### Implemented change
Story 6.7 rounds out map authoring on the established Creation-Suite patterns: (1) **map properties + New-Map flow** — new cosmetic `Author`/`Description`/`SuggestedPlayers` fields on `ScenarioData` (omit-when-default, excluded from both hashes), a Godot-free `ScenarioData.CreateBlank(...)` factory, a `MapPropertiesPanel` New-Map modal + live editable properties (via `ChimeraDialog.CreateCustom` + design-system controls), and export now feeds the real metadata into the package manifest instead of placeholder LineEdits; (2) **map size** as the authored playable half-extent (`MapSize` set Small 80 / Medium 120 / Large 128, all ≤ the fixed ±128 grid) — true variable-grid generalization is escalated per the epic RISK NOTE, and a new `GridDimensionConsistencyTests` guards fog/flow/pathability/spatial-hash mutual consistency and coverage; (3) **2–4 start positions** — the start-position tool generalized from hardcoded P1/P2 to 2–4 slots through `FactionRegistry.ToFaction`, per-slot ore+crystal, add/remove, with a non-fatal below-suggested-players (and out-of-bounds) advisory and fail-closed `suggested_players ∈ [2,4]`; (4) **minimap preview** — a top-down `MinimapPreviewRenderer` (orthographic SubViewport) auto-generates `preview/preview.png` into the `.chimera.zip` (wiring the previously-dead thumbnail manifest slot), round-tripped by `ContentPackager`. New metadata is hash-excluded and omit-when-default so legacy/flat scenarios stay byte-identical; `CanonicalModelHash.AlgoVersion` stays 7, `SimChecksum` 15, all goldens unchanged.

### Files changed (one line each)
- `godot/src/Core/Definitions/ScenarioData.cs` — `Author`/`Description`/`SuggestedPlayers` cosmetic fields; `CreateBlank` factory; Godot-free `UpsertStartSlot`/`RemoveStartSlot`/`UpdateStartSlotEconomy` start-slot helpers.
- `godot/src/Core/Definitions/MapSize.cs` (NEW) — supported size set (Small/Medium/Large → MapBounds 80/120/128, all ≤ 128) as the single source of truth.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — doc-only exclusion note for the three new cosmetic fields (no fold, AlgoVersion 7).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — fail-closed `SuggestedPlayers ∈ [2,4]`; non-fatal `CollectAdvisories` (below-suggested + out-of-bounds start advisories).
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — empty Author/Description → null at the serialize chokepoint (byte-identical when blank).
- `godot/src/Core/Definitions/ContentPackager.cs` + `ContentPackageManifest.cs` — `preview/preview.png` write/extract (guarded on non-empty bytes) wiring the dead `ThumbnailFile` slot.
- `godot/src/UI/MinimapPreviewRenderer.cs` (NEW) — orthographic top-down SubViewport snapshot → resized PNG bytes (godot-verify surface).
- `godot/src/CreationSuite/MapPropertiesPanel.cs` (NEW) — New-Map modal + live-bound Map-Properties editor from design-system components.
- `godot/src/UI/Components/ChimeraDialog.cs` — `CreateCustom(title, Control)` body-content slot.
- `godot/src/UI/EntityPlacer.cs` — start-position tool generalized to 2–4 slots via `FactionRegistry`; per-slot ore+crystal spinners persist immediately; undo removes a placement-created slot.
- `godot/src/UI/StartPositionBridge.cs` — 4 markers/colors, `MAX_SLOTS`, `EnsureVisible`.
- `godot/src/Core/MainScene.cs` — `MoveStartPosition` (returns `created`, delegates to helpers), `RemoveStartPosition` (delegates + clears stale sim base), `SetStartSlotEconomy`.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — New-Map affordance (overwrite-guarded), live Map-Properties editor, export reads real metadata + renders/passes the preview (leak-safe, re-entrancy-guarded) + surfaces advisories.
- `godot/src/Core/Bootstrap/Phases/{ScenarioLoadPhase,CameraPhase}.cs` — marker set sized to player-slot count; start-slot callback wiring (move/remove/economy).
- `godot/src/Navigation/SpatialHash.cs` — coverage constants exposed `public` (values unchanged) for the consistency guard.
- Tests (NEW/extended): `MapSizeTests`, `ScenarioDataMapPropertiesTests`, `StartPositionSlotTests`, `StartSlotMutationTests`, `GridDimensionConsistencyTests`, preview cases in `ContentPackagerTerrainTests` — 64 new tests, all green.

### Review findings breakdown (review pass 1, 4 layers)
- Patches applied: 13 (0 high, 5 medium, 8 low) — see the Review Triage Log.
- Deferred: 3 — variable-grid map-size generalization (RISK-NOTE correct-course escalation), remove-slot undo symmetry (godot-verify), Large-128 edge aliasing (pre-existing convention).
- Rejected: 2 — preview *consumption* (Stories 9.10/9.7/11.1 scope), legacy `ThumbnailPath` round-trip test (no live regression).

### Verification
- `dotnet build godot.sln` → Build succeeded, **0 errors, 0 warnings**. Sim path (`ScenarioData`/`MapSize`/`ScenarioValidator`/`ContentPackager`) stays `float`→`Fixed`-boundary-only, no `using Godot;`.
- `dotnet test ProjectChimera.Sim.Tests` → **1707 passed, 2 failed, 1 skipped**. The 2 failures are the named pre-existing baseline pair (`PersistenceManifestTests.ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`), **git-stash-verified** against the clean baseline (fail identically). Determinism pins green: `CanonicalModelHash.AlgoVersion==7`, `SimChecksum.AlgoVersion==15`, `StartStateHash.AlgoVersion==2`, 23 per-tick goldens unchanged.
- Matrix Test Audit: every I/O-matrix row (blank-map factory, metadata round-trip + hash-identity, legacy byte-identity, 2–4 slots via registry, below-suggested advisory, suggested-out-of-range fail-closed, preview packaging round-trip, grid-consistency guard) is covered by a ran-and-passed test.
- Manual (godot-mcp / godot-verify) checks NOT executed in this unattended run — no xUnit surface for the New-Map dialog, Map-Properties panel, slot picker interaction, or the SubViewport preview render. The implementation compiled and the sim/data spine is fully xUnit-pinned.

### Follow-up review recommendation: **true**
This pass made substantial review-driven changes — 13 patches across ~11 files, including a user-visible AC gap (the below-suggested advisory shipped as dead code), start-slot data-mutation correctness, undo semantics, and several editor guards. The highest-risk surfaces — the `EntityPlacer`/`MainScene` start-slot interaction model (add/remove/undo/spinner) and the `MinimapPreviewRenderer` SubViewport render — are Godot-coupled and verified only by build here, with interaction-polish items (remove-slot undo symmetry) deferred. An in-engine godot-verify of the New-Map flow, the 2–4 start-position authoring/undo, and the generated preview image would add value beyond this automated pass.

### Residual risks
- **Editor start-slot interaction surface** (`EntityPlacer`/`MainScene` add/remove/undo/spinner) is Godot-coupled and unpinned by xUnit; the data-at-rest correctness is now covered (`StartSlotMutationTests`), but remove-slot undo symmetry and the transient `_startSlotCount` are deferred godot-verify items.
- **Minimap preview render** is a godot-verify surface: the packaging round-trip that consumes the bytes is unit-tested and the render fails safe (null → omitted, never a broken package), but the visual quality of the generated PNG was not screenshot-inspected in-engine.
- **Preview consumers** (skirmish setup / MP lobby / content browser) do not display the preview yet — that is Stories 9.10/9.7/11.1 by the intent's own tagging; 6.7 produces the artifact.
- **Variable-grid map-size generalization** is escalated (deferred-work.md), not implemented; the shipped sizes are playable extents within the fixed ±128 grid, and a Large-128 map aliases its exact +X/+Z boundary line into the last grid cell (deterministic, pre-existing WorldToCell convention).
- Manual in-engine verification of the New-Map dialog / properties panel / preview UI is outstanding (unattended run).

### Review pass 2 (follow-up, 2026-07-15)
The recommended follow-up ran the same 4 adversarial layers against the committed diff. No intent-gap or bad-spec loopback (the code is coherent and meets every AC). 3 patches applied, 5 findings newly deferred, 5 rejected — see the Review Triage Log (pass 2) and `deferred-work.md`.

- **Patches (all committed, tested):**
  - `ScenarioValidator.cs` — `CollectAdvisories` now warns on out-of-bounds for **all** placeable collections (buildings/units/resource-nodes/props/water), not just start slots, so a map-size shrink that strands content surfaces a visible advisory instead of a silent unloadable export. Shared `OutOfBounds`/`OutOfBoundsCount` helper; strict `> bounds` matches `CheckCoord`.
  - `MapPropertiesPanel.cs` — restored the Name (64) / Author (40) `MaxLength` caps the pre-6.7 LineEdits had and the design-system `Input` dropped, plus a 240 cap on the new Description, in both the New-Map dialog and the live editor.
  - `MainScene.cs` — `RemoveStartPosition` no-ops the sim-base clear / marker-hide when nothing was actually removed, and routes the slot→faction cast through `FactionRegistry.ToFaction`.
- **New tests (5):** content out-of-bounds advisory (present/absent/count), `±MapBounds` boundary-exactness, and `CreateBlank` produces no advisories across the size × player-count matrix.
- **Verification:** `dotnet build godot.sln` → 0 errors (pre-existing warnings only). `dotnet test ProjectChimera.Sim.Tests` → **1712 passed, 2 failed, 1 skipped**; the 2 failures are the documented pre-existing `PersistenceManifestTests` baseline pair (unrelated to the touched files); determinism pins (`CanonicalModelHash.AlgoVersion==7`, `SimChecksum==15`, `StartStateHash==2`, 23 per-tick goldens) all green.
- **Newly surfaced residual (deferred, not fixed this pass):** authored `MapBounds` is not wired to camera pan-limits or NavMesh extent (only placement + hash change with size; the Design-Notes "already wired to camera/NavMesh" claim is inaccurate); the Export/New-Map write path still has no hard `Validate()` gate (advisory-only); non-contiguous start-slot values are still mishandled in the editor UI; the minimap preview can capture editor gizmos; economy-spinner edits to a placed slot remain non-undoable. All tracked in `deferred-work.md`.

### Follow-up review recommendation (pass 2): **false**
This pass's changes are 3 localized, low-risk fixes (a Godot-free advisory extension, a UI cap restoration, and a defensive guard) fully covered by 5 new xUnit tests — not the kind of broad, behavior-shifting change that benefits from another independent code-review round. The meaningful outstanding risk is concentrated in the deferred Godot-coupled surfaces (start-slot interaction, preview render, camera/nav wiring), which are better served by an in-engine **godot-verify** pass than by a third adversarial review of the same tracked items.
