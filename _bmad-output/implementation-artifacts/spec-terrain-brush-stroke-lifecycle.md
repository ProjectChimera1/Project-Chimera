---
title: 'Terrain brush stroke lifecycle fixes (DW-141..144)'
type: 'bugfix'
created: '2026-07-28'
status: 'done'
baseline_revision: 'a435b257aeb742a80b2cb7bf0e2f04e4224e89a1'
final_revision: 'be9d8f77f41691e7353a866831ca0e8e58fd36bc'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** Four narrow correctness/robustness gaps in the Edit-mode `TerrainBrush` stroke lifecycle: (DW-141) a stroke that makes Terrain3D auto-create a new region leaves that region behind on undo; (DW-142) toggling the brush off with T mid-drag strands `_isPainting`/`_strokeBefore`, leaking the pending snapshot+undo entry and causing buttonless painting on re-activation; (DW-143) a no-op stroke still pushes an undo command, so a later Ctrl+Z is silently absorbed doing nothing; (DW-144) Ctrl+Z while a stroke's live `operate()` is in flight races `RestoreRegions`, corrupting the captured `after` state.

**Approach:** Track regions that were absent pre-stroke and `remove_region` them on undo (create-and-undo becomes reversible). Finalize the in-flight stroke via `EndPaint()` when T deactivates mid-drag. Add a cheap per-region before/after byte-equality check so a stroke that changed nothing pushes no undo command. Expose `TerrainBrush.IsPainting` and have `EntityPlacer` swallow Ctrl+Z/Y while it is true.

## Boundaries & Constraints

**Always:** Presentation layer only — GDExtension dynamic dispatch via `GodotObject.Call`, no sim-layer types touched. Preserve the existing DELIBERATE skip of `stop_operation` in `EndPaint` (the `_store_undo` red-line route-around) and its verbatim comment. Preserve the DW-140 byte-cost weighting on `_history.Push`. Undo/redo must stay symmetric: after undo→redo the terrain returns to the post-stroke state, and a created region round-trips (removed on undo, re-created on redo). Over-approximation on the was-absent probe box must stay harmless (guard every `remove_region` with a non-null region lookup).

**Block If:** No unattended-blocking decisions are expected. Do NOT block on inability to run the Godot editor in-engine — compile-clean + code trace is the sanctioned gate for this layer (live in-engine verification is a deferred, project-level Epic-10 activity).

**Never:** Do not change the shared `EditorHistory` semantics or its public API. Do not alter entity place/delete/move undo behavior. Do not add per-sample cost to `ContinuePaint` (the equality check runs once at stroke end only). Do not edit the deferred-work ledger. Do not introduce a Godot-node dependency into any `src/Core`/sim file.

Note: the five stroke-lifecycle scenarios (DW-141 undo, DW-141 redo, DW-142 T-mid-drag, DW-143 no-op, DW-144 Ctrl+Z-during-stroke) are carried as Acceptance Criteria below rather than an I/O Matrix — they are GDExtension/Godot-input behaviors in a presentation-layer node with no Godot-free unit-test harness, so they are reviewed and confirmed by code trace + compile, with live in-engine confirmation deferred to Epic 10.

</intent-contract>

## Code Map

- `godot/src/CreationSuite/TerrainBrush.cs` -- the brush; owns `RegionSnapshot`, `SnapshotRegions`, `PushStrokeUndo`, `RestoreRegions`, `EndPaint`, `_isPainting`, T-toggle in `_UnhandledInput`. All of DW-141/142/143 land here plus the DW-144 `IsPainting` accessor.
- `godot/src/UI/EntityPlacer.cs` -- owns the shared `EditorHistory` (`History`) and the Ctrl+Z/Y handler in `_Input` (~line 400). Add the DW-144 guard + a settable brush reference. Already `using ProjectChimera.CreationSuite`.
- `godot/src/Core/Bootstrap/Phases/TerrainBrushPhase.cs` -- sole wiring path (`SetupTerrainBrush` is retired). Wire the brush into `EntityPlacer` here after `Initialize`.
- `godot/src/CreationSuite/EditorHistory.cs` -- shared undo stack (read-only reference; DO NOT modify).

## Tasks & Acceptance

**Execution:**
- `godot/src/CreationSuite/TerrainBrush.cs` -- Add a `WasAbsent` bool to `RegionSnapshot` (5th ctor arg). In `SnapshotRegions`, when `get_region` returns null for a probed loc, record a `WasAbsent=true` snapshot (null images) instead of `continue` — so an empty-space stroke yields a non-null `before`. In `PushStrokeUndo`, capture each `after` snapshot's `WasAbsent` from whether its region is null now; then run the DW-143 no-op check (below) and return without pushing if nothing changed. In `RestoreRegions`, for a `WasAbsent` snapshot fetch the region by loc and `remove_region(region, true)` when non-null (guarded) instead of `import_images`; keep the trailing `calc_height_range(true)` + `MarkDirty()`. Add `public bool IsPainting => _isPainting;`. In the T-toggle branch of `_UnhandledInput`, when deactivating (`_brushActive` becomes false) while `_isPainting`, call `EndPaint()` before logging.
- `godot/src/UI/EntityPlacer.cs` -- Add `public CreationSuite.TerrainBrush? TerrainBrush { get; set; }`. In the `editMode && key.CtrlPressed` block in `_Input`, before the Z/Y dispatch, if the keycode is Z or Y and `TerrainBrush?.IsPainting == true`, `GetViewport().SetInputAsHandled()` and return (swallow, do not undo/redo).
- `godot/src/Core/Bootstrap/Phases/TerrainBrushPhase.cs` -- After `brush.Initialize(...)`, set `_ctx.Placer.TerrainBrush = brush;` so the DW-144 guard has its reference.

**No-op check (DW-143), inside `PushStrokeUndo` after `after` is built:** iterate the aligned `before[i]`/`after[i]` pairs; a region "changed" iff (`before[i].WasAbsent` && !`after[i].WasAbsent`) — region was created — OR (both present && the `GetData()` byte spans of Height OR Control differ). If no region changed, clear `_strokeBefore` (already done) and return without calling `_history.Push`.

**Acceptance Criteria:**
- Given the brush painting into empty space auto-creates a new region, when the user presses Ctrl+Z, then the new region is removed and no un-undoable residue remains; and when they then press Ctrl+Y, the region is re-created from the post-stroke snapshot.
- Given a drag is in progress (`_isPainting`), when the user presses T to deactivate the brush, then `EndPaint` runs (`_isPainting` false, `_strokeBefore` null, a valid undo entry pushed for the painted portion) and no painting occurs on the next re-activation until LMB is pressed again.
- Given a stroke that leaves every touched region byte-identical and creates no region, when the stroke ends, then no undo command is pushed and the next Ctrl+Z undoes the previous real operation.
- Given `TerrainBrush.IsPainting` is true (live stroke), when Ctrl+Z or Ctrl+Y is pressed, then `EntityPlacer` consumes the event without invoking `History.Undo/Redo`.
- Given the changes compile, when `dotnet build godot/godot.csproj` runs, then it succeeds with no new warnings/errors.

## Design Notes

`RegionSnapshot` gains `WasAbsent` as a 5th field; existing call sites pass `false`. The undo/redo asymmetry that makes DW-141 correct: `before` holds `WasAbsent=true` for a created region (→ `remove_region` on undo), while `after`, captured post-stroke when the region exists, holds real images (→ `import_images` on redo). Over-approximation from the 9-point ±r probe box is safe: an absent loc the brush never actually reaches stays absent in `after` (→ not "changed", DW-143 ignores it) and its `remove_region` is a guarded no-op.

Byte equality example (per region, stroke-end only — not on the hot per-sample path):
```csharp
static bool ImageBytesEqual(Image? a, Image? b)
    => (a == null && b == null) || (a != null && b != null
       && a.GetData().AsSpan().SequenceEqual(b.GetData()));
```

`remove_region(region, true)` mirrors the addon's own `importer.gd` usage (`data.remove_region(region, false)` + a later `update_maps`); the `true` self-updates maps so no `Terrain3DRegion.TYPE_MAX` enum literal is needed from C#.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj -c Debug` -- expected: Build succeeded, 0 errors, no new warnings from the touched files.

**Manual checks (if no CLI):**
- Code trace: confirm `before`/`after` snapshot lists stay index-aligned (both built by iterating `before`), that every `remove_region` is null-guarded, and that the `EndPaint`-on-T path clears `_strokeBefore`.
- In-engine behavioral confirmation of the five matrix rows is a deferred Epic-10 live-verification activity (godot-mcp), not part of this unattended run; note it as residual risk.

## Review Triage Log

### 2026-07-28 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 1: (high 0, medium 0, low 1)
- reject: 15
- addressed_findings:
  - `[medium]` `[patch]` The new DW-144 guard (EntityPlacer swallows Ctrl+Z/Y while `IsPainting`) escalated a stranded `_isPainting` from "buttonless paint" to "all undo/redo swallowed until the next completed stroke" — reachable via an Edit→Play switch mid-stroke, brush deactivation by a non-T route, or a missed mouse-up / focus loss during a drag. Fixed by a `_Process` safety net that finalizes a stroke no longer legitimately in progress (`!inEdit || !_brushActive || !Input.IsMouseButtonPressed(Left)` → `EndPaint()`). Rebuilt clean.

## Auto Run Result

Status: done
Blocking condition: none

**Implemented change:** Four terrain-brush stroke-lifecycle fixes (DW-141..144) plus one review patch.
- DW-141 — `RegionSnapshot.WasAbsent` tracking: absent probed regions are snapshotted (null images) so a stroke that auto-creates a Terrain3D region is undone via `remove_region` and redone via `import_images`.
- DW-142 — T-toggle-off mid-drag calls `EndPaint()` to finalize the in-flight stroke (resets `_isPainting`, clears `_strokeBefore`, pushes undo).
- DW-143 — `PushStrokeUndo` runs a per-region before/after byte-equality check (`ImageBytesEqual`) at stroke end and skips pushing an undo command for a no-op stroke.
- DW-144 — `TerrainBrush.IsPainting` accessor; `EntityPlacer` swallows Ctrl+Z/Y while a stroke is live; wired via `TerrainBrushPhase`.
- Review patch — `_Process` safety net so a stranded `_isPainting` can't brick undo/redo.

**Files changed:**
- `godot/src/CreationSuite/TerrainBrush.cs` — WasAbsent tracking, no-op byte-equality gate, `IsPainting`, T-toggle finalize, `_Process` stranded-stroke safety net.
- `godot/src/UI/EntityPlacer.cs` — settable `TerrainBrush` reference; Ctrl+Z/Y swallow while `IsPainting`.
- `godot/src/Core/Bootstrap/Phases/TerrainBrushPhase.cs` — wires `_ctx.Placer.TerrainBrush = brush`.

**Review findings breakdown:** 1 patch applied (medium — stranded-flag undo brick); 1 deferred (see below); 15 rejected as unreachable-via-brush, already-guarded, or disclosed in-engine posture.

**Deferred (NOT written to the ledger — the orchestrator owns it per the invocation constraint; recorded here for the orchestrator/maintainer to file):**
- summary: `EstimateImageBytes`/`SnapshotBytes` (pre-existing DW-140 code, untouched here) have an untested bpp table with a `_ => 4` fallback for the Terrain3D control-map format; if the real format is wider, the DW-140 undo-memory cap under-counts. These are pure C# and extractable into the Tier-1 (`SimSources.props`) test set like `EditorHistory.cs` already is.
  evidence: Only `EditorHistory.cs` from `CreationSuite` is globbed into `ProjectChimera.Sim.Tests`; `EstimateImageBytes` lives in the excluded `TerrainBrush.cs` and `EditorHistoryTests` only exercises the cap with hand-passed byte literals, never a computed snapshot cost.

**Verification:** `dotnet build godot/godot.csproj -c Debug` → Build succeeded, 0 errors, 13 warnings (all pre-existing, none in the three touched files). Code trace confirmed: `before`/`after` index-aligned, every `remove_region` null-guarded, `EndPaint` clears `_strokeBefore` on every exit (including the no-op early return). No I/O Matrix, so no Matrix Test Audit.

**Residual risks:**
- The GDExtension runtime contracts underpinning DW-141/DW-143 are unverified by compile+trace and require in-engine confirmation (deferred to Epic-10 per project decision): (1) `get_region` returns null for a not-yet-created region; (2) `remove_region(region, true)` regenerates maps and `import_images` re-creates an absent region at `loc*span` origin on redo; (3) `get_height_map`/`get_control_map` bytes are CPU-synced immediately after `operate()` so the no-op check doesn't drop a real undo (the 6.2 undo path already reads these same maps, which is corroborating but not proof).
- The 9-point ±r probe box (pre-existing) could miss a created region if a creator configures a region span smaller than the brush radius; unreachable at defaults (brush max 100 world units < default region span 256).
- Residual working-tree artifact left in place (not part of this change, not committed): `Snapshot.md` (a `Last Touched` date bump, 2026-07-27→2026-07-28, from a session hook).
