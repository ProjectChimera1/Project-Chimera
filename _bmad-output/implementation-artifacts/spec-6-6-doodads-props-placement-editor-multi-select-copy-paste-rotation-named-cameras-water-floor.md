---
title: 'Story 6.6: Doodads/props placement + editor multi-select/copy-paste/rotation + named cameras + water floor'
type: 'feature'
created: '2026-07-14'
status: 'done'
baseline_revision: '9c205a3599688499f0e349af9a1ab55652cae493'
final_revision: '98c7627e946c7f64f32807ef5cbdc513415553fd'
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** Maps are barren and one-click-at-a-time. Creators cannot place decorative props, cannot marquee-select / copy-paste / rotate placements, cannot author named camera viewpoints (which Epic 7's `MoveCamera` trigger needs), and cannot place water. The GDD promised all four; none exists today (no `Props`/`Cameras`/`Water`/rotation fields on `ScenarioData`, no selection concept in `EntityPlacer`).

**Approach:** Four cohesive editor additions, all reusing the established Creation-Suite patterns: (1) **doodads/props** placed through `EntityPlacer` as a new placement category, rendered by a single-`MultiMesh`-per-mesh `PropRenderer`, with an optional `blocks_pathing` flag whose footprint unions into 6.5's `PathabilityGrid`; (2) **multi-select / copy-paste / R-key rotation** as shared editor manipulation across every placeable category (units/buildings/nodes/props), with rotation persisted as presentation-only yaw; (3) **named cameras** authored by a toggle tool (position/target/FOV + in-editor "view through" preview); (4) a **cheap water floor** (visual plane + auto-impassable, no fluid sim). All new state persists inline in `scenario.json` (the Regions precedent — no map-package format change) and round-trips save/load + `.chimera.zip`. Because blocking props/water change unit paths, their footprints are lockstep-critical and fold into `CanonicalModelHash` (a one-time, explicitly-stated `AlgoVersion` 6→7 re-baseline — the exact inverse-free extension of 6.5's 5→6); per-tick `SimChecksum` stays 15.

## Boundaries & Constraints

**Always:**
- Sim/Presentation boundary is sacred. New sim-consumable data (`ScenarioProp`/`ScenarioCamera`/`ScenarioWater` DTOs, the blocking-footprint union, the hash fold) is pure Godot-free C# (`Fixed`/`int`/`bool`, ascending order, clamped integer-cell lookup via `FlowField.WorldToCell` — never Godot `Image`/float interpolation) in `src/Core`/`src/Navigation`. Every editor tool, palette, renderer, and preview is presentation (`src/UI`, `src/CreationSuite`).
- Props render via **one `MultiMeshInstance3D` per distinct prop mesh** (per-instance transform = position + yaw + uniform scale). Never one node per prop. Frame-rate impact must stay presentation-only (the 10.2 budget) at hundreds of props.
- A `blocks_pathing` prop and every water volume **union their footprint cells into the same 6.5 `PathabilityGrid`** at load (a new blocked source threaded through `ScenarioLoadPhase.BuildAndInjectPathabilityGrid` / `PathabilityGrid.Resolve`, OR'd into the same `bool[]` mask as painted + slope cells). Footprint cells resolve through `FlowField.WorldToCell` (128²/2-unit/±128), byte-identical to the painted layer. The grid is rebuilt from scratch each load, so **moving/deleting a blocking prop or removing a water volume un-stamps automatically**. No separate collision system.
- Blocking props + water are **lockstep-critical** (they change unit paths → `Position`), so their footprint-determining data folds into `CanonicalModelHash` (bump `AlgoVersion` 6→7). This is a one-time, explicitly-stated golden re-baseline of the handshake fixtures — the StartCrystal/pathability posture, precedent-identical to 6.5's 5→6. `StartStateHash` inherits via the seed (its own `AlgoVersion` stays 2).
- **Rotation is presentation-only for 1.0.** `Rot` (yaw) persists on `ScenarioBuilding`/`ScenarioUnit`/`ScenarioResourceNode`/`ScenarioProp` (omit-when-default) and applies as visual yaw at spawn; sim footprints stay axis-aligned and rotation is **excluded** from `CanonicalModelHash` and per-tick `SimChecksum` (cosmetic, like `DisplayName`). The editor tooltip must state rotation is visual-only.
- **Non-blocking props, cameras, and water-visual-only attributes never touch sim state or either checksum.** Cameras are pure presentation (excluded from both hashes). Only a `blocks_pathing` prop / a water volume's impassable footprint reaches the handshake hash.
- All new persisted collections are omit-when-null (the `Regions` precedent) and normalize empty→null at the `ScenarioSerializer` chokepoint. Persistence is inline in `scenario.json` — no `ContentPackager`/package-format change, so `.chimera.zip` export/import round-trips for free.
- Every editor mutation (place/rotate/delete a prop, a group move/delete/duplicate/paste, a camera add, a water volume add) pushes exactly one `(redo, undo)` pair onto the shared `EntityPlacer.History` and interleaves safely with entity/region/terrain/pathability undo (the 6.2 interleave guarantee extended to groups).
- New camera tool and water tool follow the RegionTool/PathabilityTool skeleton: Edit-mode-gated toggle hotkey, `_Input` interception, `IsOverPanel`+`MouseFilter.Stop` guard, right-dock panel, `_Process`-polled Edit-only `Visible` (chrome/overlays hidden in Play), `_ExitTree` free; registered via a new `*ToolPhase` in the 3-file lockstep (`MainScene`, `ScenePhaseOrder`, `PhaseOrderTest`) after `ScenarioLoad`.
- A flat/legacy map with no props/cameras/water and no blocking footprints must be **byte-identical** to pre-feature save output for those sections (all four keys omitted), and its `PathabilityGrid` build path unchanged. The 23 per-tick `SimChecksum` goldens and `SimChecksum.AlgoVersion` (15) stay unchanged.
- `ScenarioValidator` fails closed (clear, pre-tick message) if a start/unit/`spawn_unit`/resource position resolves onto a blocking-prop or water footprint (same cell domain as the 6.5 painted check), and validates structural well-formedness (unique camera names, well-formed water rects, in-bounds prop coords, hash-folded floats finite/in-range).

**Block If:**
- Folding blocking-prop/water footprints into `CanonicalModelHash` reveals blocking that is **not** transitively captured by `Position` (i.e. it would force folding into per-tick `SimChecksum` and re-baselining all 23 tick goldens) — surface it rather than expanding the re-baseline surface unattended.
- A required prop/camera/water persistence shape cannot be reconciled with the inline-`scenario.json` + `FlowField.WorldToCell` conventions (e.g. a footprint resolution other than 128²/2-unit/±128 is demanded) — do not silently pick one.

**Never:**
- Never render props or the water plane with per-prop / per-cell `MeshInstance3D` nodes; never leave editor tool chrome, ghosts, or the "view through camera" preview active in Play.
- Never make rotation alter sim behavior for 1.0 (no rotated/oriented footprints — documented post-1.0); never fold rotation or camera data into either checksum.
- Never introduce a second serialization path or a new `.chimera.zip` package entry for props/cameras/water (inline in `scenario.json`); never bake prop/water blocking into the Terrain3D control map.
- Never touch per-tick `SimChecksum` or the 23 per-tick goldens; never bump `SimChecksum.AlgoVersion`. No fluid/water simulation. No mid-match mutation of any authored layer.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Place / rotate / scale a prop | Prop mode, pick library entry, R-rotate, place | Rendered via `MultiMesh` at yaw/scale; one undo entry; persisted to `ScenarioData.Props` | Click over dock panel ignored (`IsOverPanel`); raycast miss = no-op |
| Non-blocking prop | Prop with `blocks_pathing=false` placed | Renders only; grid, both checksums, validator all unchanged | — |
| Blocking prop | Prop with `blocks_pathing=true` placed | Footprint cells union into `PathabilityGrid`; overlay shows them; `CanonicalModelHash` moves | Footprint clamped in-grid; OOB coord clamped, never throws |
| Move/delete a blocking prop | Blocking prop repositioned or removed, map reloaded | Old footprint un-stamps (grid rebuilt from source); new footprint stamps | Delete last prop ⇒ `Props` normalizes to null |
| Marquee multi-select + Shift-add | Drag box over placed units/buildings/nodes/props in Edit | Group selected; Shift-drag adds to set; group move/delete/duplicate as one undo step each | Empty marquee = no selection change |
| Copy / paste a group | Selection copied, paste at cursor | Group pastes at cursor with grid-snapped relative offsets preserved, across all categories | Paste with empty clipboard = no-op |
| Rotate placement | Unit/building/prop, press R | Yaw steps; persists in `ScenarioData` as `Rot`; visual only; sim path/footprint unchanged | Rotation never blocks placement |
| Save → reload / export→import | Map with props/cameras/water/rotations | All four sections round-trip identically; identical `CanonicalModelHash`; `.chimera.zip` survives | Empty section ⇒ key omitted |
| Name / preview a camera | Camera tool, set position/target/FOV, name it | Persists to `ScenarioData.Cameras`; "view through camera" works in-editor; listed for 7.13 | Duplicate/empty name rejected pre-save (validator) |
| Place a water volume | Water tool, drag a rect | Visual plane renders; footprint auto-stamps impassable into `PathabilityGrid`; persists | Removing the volume un-stamps on reload |
| Flat/legacy map | No props/cameras/water/rotation | All four keys omitted; bytes + 23 tick goldens + `SimChecksum.AlgoVersion` (15) byte-identical | — |
| Peer footprint mismatch | Two clients, different blocking props/water | `CanonicalModelHash`/`StartStateHash` differ ⇒ handshake rejection, not in-sim desync | Not a silent desync |
| Spawn on blocking prop/water | Start/unit/`spawn_unit`/resource on a blocking-prop or water cell | `ScenarioValidator` fails closed with a clear message before any tick | Clear cell passes |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs` — **NEW** DTOs `ScenarioProp` (`prop_id`, `x`, `z`, `rot` omit-when-default, `scale` default 1, `blocks_pathing` omit-when-default), `ScenarioCamera` (`name`, `x`/`y`/`z`, `target_x`/`y`/`z`, `fov`), `ScenarioWater` (rect `x`/`z`/`w`/`h`, `y` level) near `ScenarioRegion` (~:400-458). **NEW** `Props`/`Cameras`/`Water` arrays (`[JsonIgnore(WhenWritingNull)]`, the `Regions` precedent ~:412) + add `Rot` (omit-when-default yaw) to `ScenarioBuilding`/`ScenarioUnit`/`ScenarioResourceNode` (~:142/:172/:66).
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — normalize empty `Props`/`Cameras`/`Water` → null at the swap/restore chokepoint (mirror the empty-`Regions`→null block ~:57-58/:73-77).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — fold blocking-prop footprints + water-volume footprints (quantized cell sets via `FlowField.WorldToCell`, canonical ascending order) near the pathability fold (~:109-111); bump `AlgoVersion` 6→7; document that non-blocking props / cameras / rotation are excluded (cosmetic). `StartStateHash` inherits via seed (`AlgoVersion` stays 2).
- `godot/src/Navigation/PathabilityGrid.cs` — extend the load-time union so a third source (blocking-prop + water footprint cells) OR's into the mask; add a Godot-free helper that computes a prop/water footprint cell set (clamped integer cells) so `Resolve` (and the hash) share one derivation. Keep `Empty`/degenerate-safe.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — in `BuildAndInjectPathabilityGrid` (~:236-270), pass blocking props + water to the union so the injected grid (fanned to Applier/FlowFieldSystem/`SceneContext.Pathability`) includes their cells.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — decode blocking-prop + water footprints alongside the painted layer (~:113-118); `CheckNotBlocked` start/unit/`spawn_unit`/resource against the union (~:153/:166/:238/:382); structural checks: unique non-empty camera names, well-formed water rects, in-bounds prop coords, finite/in-range hash-folded floats (mirror the `Regions` loop ~:248-285 and `SlopeBlockThreshold` gate ~:92-94).
- `godot/src/UI/EntityPlacer.cs` — **props as `PlacementMode.Prop`** (enum ~:28 + `MODE_ORDER`/`MODE_LABELS` ~:62): library-picker + rotation/scale/`blocks_pathing` sub-row (`RefreshSubRow` ~:968), ghost, place/delete → `_onPropSync` + history push, delete scan (`TryDeleteAt` ~:1202). **Multi-select/copy-paste/rotation:** marquee selection set (Shift-add) across all categories, group move/delete/duplicate + copy/paste (cursor paste, grid-snapped relative offsets), R-key step-rotate for active placement / selection — each op one `History.Push` pair. **NEW** `_onPropSync` `Func<ScenarioSyncOp,...>` field (wire in `CameraPhase.cs:38-41`).
- `godot/src/Core/MainScene.cs` — **NEW** `SyncProp` mutating `ScenarioData.Props` (mirror `SyncBuilding` ~:789-845); apply persisted `Rot` yaw to placed building/unit/prop meshes at spawn (presentation).
- `godot/src/UI/PropRenderer.cs` — **NEW** presentation node: one `MultiMeshInstance3D` per distinct prop mesh, per-instance transform (position + yaw + scale) rebuilt from `ScenarioData.Props` on change (the `PathabilityTool` overlay MultiMesh pattern).
- `godot/src/CreationSuite/CameraTool.cs` + `godot/src/Core/Bootstrap/Phases/CameraToolPhase.cs` — **NEW** toggle tool (copy `RegionTool`): create/name/preview cameras, "view through camera" preview, right-dock list; persist to `ScenarioData.Cameras`; shared stroke-undo.
- `godot/src/CreationSuite/WaterTool.cs` + `godot/src/Core/Bootstrap/Phases/WaterToolPhase.cs` — **NEW** toggle tool (copy `RegionTool`): drag a water rect, render a visual plane at `y`, auto-stamp impassable footprint (persisted; unions at load), right-dock panel; shared stroke-undo.
- `godot/src/Core/MainScene.cs`, `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`, `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — register `CameraTool` + `WaterTool` phases after `PathabilityTool` in all three in lockstep (~MainScene:418-421, order/test arrays).
- `godot/ProjectChimera.Sim.Tests/**` — **NEW** Tier-1 tests (see Tasks). Re-baseline (one-time, 6→7): `CanonicalModelHashTests`, `CanonicalModelHashRegionExclusionTests`, `StartStateHashTests`, `VersionStampConsistencyTests`, `HeroProfilePersistenceTests`, `SimResetTests`, `ScenarioApplierTests` (pinned hash), `hero-start-state.golden.txt`.

## Tasks & Acceptance

**Execution:**
- `ScenarioData.cs` — add `ScenarioProp`/`ScenarioCamera`/`ScenarioWater` DTOs, `Props`/`Cameras`/`Water` arrays (omit-when-null), and `Rot` (omit-when-default) on building/unit/resource-node entries.
- `ScenarioSerializer.cs` — empty `Props`/`Cameras`/`Water` → null at the serialize chokepoint (empty-`Regions` precedent).
- `PathabilityGrid.cs` + `ScenarioLoadPhase.cs` — Godot-free blocking-prop/water footprint derivation; union as a third source into the load-time grid; un-stamp inherent via load rebuild.
- `CanonicalModelHash.cs` — fold blocking-prop + water footprints; `AlgoVersion` 6→7; documented exclusion of non-blocking props/cameras/rotation.
- `ScenarioValidator.cs` — fail-closed start/unit/`spawn_unit`/resource on blocking-prop/water footprint; structural validation of props/cameras/water + finite hash-folded floats.
- `EntityPlacer.cs` (+ `CameraPhase.cs` wiring, `MainScene.SyncProp`) — prop placement mode; marquee multi-select (Shift-add) + group move/delete/duplicate + copy/paste + R-key rotation across all categories; one history pair per op.
- `PropRenderer.cs` (NEW) — single-`MultiMesh`-per-mesh prop rendering (position/yaw/scale) from `ScenarioData.Props`.
- `MainScene.cs` — presentation yaw applied at spawn from persisted `Rot`.
- `CameraTool.cs` + `CameraToolPhase.cs` (NEW) — camera authoring + in-editor preview; persist `Cameras`.
- `WaterTool.cs` + `WaterToolPhase.cs` (NEW) — water volume + visual plane + auto-impassable; persist `Water`.
- `MainScene.cs` / `ScenePhaseOrder.cs` / `PhaseOrderTest.cs` — register CameraTool + WaterTool phases in lockstep.
- Tests — `ScenarioDataPropsCamerasWaterTests` (each array round-trips; empty→key absent; `Rot`/scale/`blocks_pathing` omit-when-default; rotation round-trips on all entry types); `CanonicalModelHashPropsWaterTests` (blocking prop + water footprint move CMH; non-blocking prop / camera / rotation do NOT; empty == post-rebaseline baseline; `AlgoVersion` 7; propagates to `StartStateHash`); `PathabilityUnionPropsWaterTests` (blocking footprint unions into the resolved grid; non-blocking contributes nothing; removal → grid clear; footprint cell domain == `FlowField.WorldToCell`); `ScenarioValidatorPropsWaterTests` (start/unit/`spawn_unit`/resource on blocking-prop/water fail closed; clear passes; duplicate camera name / malformed water rect / OOB prop / non-finite float fail closed); an editor group-op interleave test extending the 6.2 pattern to a copy/paste/multi-move group (Godot-free where the mutation helper allows).

**Acceptance Criteria:**
- Given the prop palette in Edit mode, when the author places/rotates/scales props (with `MultiMesh` rendering) and interleaves it with entity/region/terrain/pathability edits, then each op undoes/redoes as a single step without corruption, and the props round-trip identically through save/reload and `.chimera.zip` export→import.
- Given a `blocks_pathing` prop and a water volume, when the map loads, then their footprints union into 6.5's `PathabilityGrid` (deterministic `FlowField.WorldToCell` cells), units route around / cannot enter them, moving/deleting them un-stamps on reload, and `CanonicalModelHash` reflects them — a one-time, explicitly-stated re-baseline of the handshake fixtures with `AlgoVersion` 6→7; a non-blocking prop leaves the grid and both checksums untouched.
- Given multi-select in Edit mode, when the author marquee-selects (Shift-add) placed units/buildings/nodes/props and group-moves/deletes/duplicates/copy-pastes them, then all categories participate, paste lands at the cursor with grid-snapped relative offsets preserved, and every group op is a single interleave-safe undo step.
- Given a placement, when the author presses R, then it rotates by a step, the yaw persists in `ScenarioData` and applies as presentation yaw at spawn, and the sim path/footprint and both checksums are unchanged (rotation is visual-only, stated in the tooltip).
- Given the camera tool, when the author creates/names/previews cameras (position/target/FOV), then they persist in the scenario, "view through camera" works in-editor, they are listed for the 7.13 `MoveCamera` action, and cameras never touch sim state or either checksum.
- Given a flat/legacy map with no props/cameras/water/rotation, when it is serialized and simulated, then all four keys are omitted, the bytes are byte-identical to pre-feature for those sections, `SimChecksum.AlgoVersion` stays 15, and all 23 per-tick goldens are unchanged.
- Given a start/unit/`spawn_unit`/resource position on a blocking-prop or water footprint, when it passes through `ScenarioValidator`, then validation fails closed with a clear message before any tick; a clear cell passes.

## Spec Change Log

## Review Triage Log

### 2026-07-14 — Review pass 1 (post-implementation adversarial review: 4 layers — blind/edge/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 4, low 2)
- defer: 6
- reject: 7
- addressed_findings:
  - `[medium]` `[patch]` **Triple-copied blocking-footprint derivation could silently drift (VG1).** The "which props/water block + stamp their cells" loop was hand-written three times (load `ScenarioLoadPhase.BuildBlockingFootprintMask`, hash `CanonicalModelHash.BlockingFootprintDigest`, `ScenarioValidator`); only the per-cell `StampPropInto`/`StampWaterInto` was shared, and the load copy had no test — a drift would ship a wrong-but-deterministic map (the `CanonicalModelHash`/pathability lockstep class). Extracted the one Godot-free `PathabilityGrid.BuildBlockingFootprint(props, water)` that all three now call; behaviour-identical (same props-then-water stamp order) so the handshake baseline is **unmoved** (all `CanonicalModelHash`/`StartStateHash`/golden tests pass without re-recording). Added 3 tests pinning the shared derivation (`PathabilityUnionPropsWaterTests`).
  - `[medium]` `[patch]` **Camera-capture undo strands the editor camera (Blind #2).** Undoing a "Capture" while a "view through" preview was active removed the camera but left the RTS controller suppressed (`SetProcess(false)`), freezing the editor view. The capture's undo closure now calls `StopPreview()` first (mirroring `DeleteSelected`). (`CameraTool.cs`.)
  - `[medium]` `[patch]` **Prop delete matched by position, not identity (Edge E2).** `BuildDeleteProp` removed the first `ScenarioData.Props` entry matching the prop's (x,z); two props stacked on one cell (paste/duplicate at zero offset) unlinked the wrong one and undo restored the wrong one. Now removes the exact selected `ScenarioProp` by identity via `RemoveHandle`. (`EntityPlacer.cs`.)
  - `[medium]` `[patch]` **Stale selection slots after external deletion (Blind #3 / Edge E1).** A group op could act on a selected live slot freed by an intervening hover-Delete / single-op undo / F5 re-apply, mutating a dead/reused slot. Added `PruneSelection()` (drops dead-slot / removed-prop entries) called before every group op (delete/move/copy/rotate) and the move-vs-marquee hit-test. (Freed-then-reused same-index slots remain a narrower residual.) (`EntityPlacer.cs`.)
  - `[low]` `[patch]` **Lossy `double` prop change-detector (Blind #6 / VG / Edge E5).** `PropRenderer.Signature` folded floats into a `double` accumulator that could collide two distinct prop sets or round away a small R-key nudge, leaving the MultiMesh stale. Replaced with an exact 64-bit FNV-1a over the raw IEEE-754 bits. (`PropRenderer.cs`.)
  - `[low]` `[patch]` **Camera preview `LookAt` colinear-up crash (Edge E3).** Previewing a straight-down camera (direction parallel to `Vector3.Up`) yields an undefined/NaN Godot basis. `PreviewSelected` now picks a non-colinear up (and handles a degenerate eye==target). Also corrected the class doc's non-existent "rename via re-capture" claim (Blind #15). (`CameraTool.cs`.)
- deferred (6, in `deferred-work.md`): group move/duplicate/paste re-derives placements lossily (worker→combat, building `pre_built`, node collection fields — single-delete paths are identity-preserving); non-prop (unit/building/node) rotation persists but has no spawn-side visual yaw (cosmetic, architecturally invasive, footprints axis-aligned); marquee/markers assume flat y=0 terrain (pre-existing editor-wide convention); no single-active-dock arbitration (pre-existing RegionTool/PathabilityTool pattern); group-op composition xUnit-uncovered (Godot-coupled, spec-designated manual godot-verify); no ContentPackager zip round-trip test (inline data rides scenario.json; JSON round-trip is tested).
- rejected (7): per-frame prop signature O(n) (negligible with the exact int fold); water footprint over-stamp vs visual plane (by-design cell quantization, identical to painted pathability, fail-closed); `prop_id` unvalidated (presentation-only; box fallback is acceptable recovery); `PropDescriptor` misnomer (cosmetic naming, churn in untested code); re-baseline "not demonstrated" (the green suite empirically proves it — a missed dependent fixture would be red); move-snaps-delta vs paste-snaps-absolute (defensible, sub-grid); frame-budget not measured (MultiMesh satisfies by inspection; no perf harness, matches the spec's manual note).

### 2026-07-15 — Review pass 2 (follow-up adversarial review: 4 layers — blind/edge/verification-gap/intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 3
- reject: 14
- addressed_findings:
  - `[medium]` `[patch]` **Building group-move undo/redo stranded live position (Blind F2).** A group-move deletes-then-recreates through `BuildingStore`'s LIFO free slot, so `Create` reuses the just-freed slot and overwrites `Position` with the moved value; `BuildDeleteBuilding.undo` resurrected via `Alive[id]=true` alone (never re-writing `Position`), so after undo the building sat at the MOVED position while `ScenarioData` held the original (live store ≠ persisted until the next reload). The symmetric `BuildCreate` redo had the mirror gap. Verified against `BuildingStore` (public `Position`/`Type`/`FactionOf` arrays, LIFO `_freeList` reuse) and traced the undo/redo ordering in `MoveSelection`. Fix: both closures now re-write full identifying state (`Position`/`Type`/`FactionOf` + timers) rather than relying on slot residue — units already do this via `RestoreUnit`; nodes are immune (`_count++`, no slot reuse). Build clean, Tier-1 suite unchanged (1655 pass / 2 pre-existing fails); the live-store correctness is a godot-verify surface. (`EntityPlacer.cs`.)
  - `[low]` `[patch]` **`MoveSelection` phantom no-op undo on empty-pruned selection (Edge E5).** If every selected slot was freed mid-drag, `PruneSelection` emptied the set but `MoveSelection` still pushed an empty `(redo, undo)` pair onto the shared stack (a dead undo step), unlike `DeleteSelection`/`PasteClipboard` which guard the empty case. Added the matching `if (deletes.Count == 0 && creates.Count == 0) return;` guard (kept the delete-happened-but-create-failed case pushable so a partial move stays recoverable). (`EntityPlacer.cs`.)
- deferred (3, in `deferred-work.md`, NEW entries): blocking prop/water (and 6.5 painted layer) not rebuilt on the F5 Edit→Play re-apply — sim honors it only on reload, matching the AC's "on reload" scoping (pre-existing `ScenarioLoadPhase`/`ResetToAuthoredStart` mechanism, no desync); `MapBounds > 128` footprint-cell aliasing at the ±128 flow-grid clamp (pre-existing whole-editor `FlowField.WorldToCell` convention, deterministic); no map-bounds guard on prop place/paste/group-move (WaterTool guards; fail-closed at validator, no corruption).
- rejected (14): F5 grid staleness confirmed a NON-issue (defer, not fix — pre-existing + AC-consistent); re-baseline "verify all goldens" (F10) empirically GREEN (1655 pass, AlgoVersion 7 pinned across `HeroProfilePersistenceTests`/`VersionStampConsistencyTests`/`ScenarioApplierTests`, per-tick `SimChecksum` stays 15); freed-then-reused same-index selection slot (F3, self-acknowledged narrow residual, already in residual risks); copy/paste building-type silent CommandCenter fallback + fresh-state re-derive (F5, within the already-deferred lossy-group-re-derive); `PropRenderer.Signature` folds `prop_id` length not content (F6, latent/unreachable — no in-place id mutation); move-snaps-delta vs paste-snaps-absolute (F8, already rejected pass 1); shared `G` snap keybind across WaterTool+EntityPlacer (F9, within the deferred dock-arbitration class); Navigation→Core.Definitions DTO coupling (F11, layering nit, Godot-free rule satisfied); CameraTool capture-while-previewing (F12, by-design "capture current view"); load-phase sink-injection xUnit-uncovered (VG1, core derivation IS pinned; sink seam is spec-designated godot-verify); GroupOp test is a stand-in closure (VG2, already deferred pass 1); building/unit/node `Rot` hash-exclusion untested (VG3, structurally safe — hash never reads those fields); intent-audit live-selection R-rotation prop-only (subsumed by the deferred non-prop-visual-yaw item); intent-audit verification-surface observation (descriptive, not a defect — interaction surface is spec-designated manual).

## Design Notes

**The hash decision (load-bearing).** This extends 6.5's decision rather than inverting it. 6.5 proved authored blocking is lockstep-critical (mismatched blocked layers → divergent paths → desync from the first move order) and therefore belongs in `CanonicalModelHash` (handshake rejection), not per-tick `SimChecksum`. Blocking props and water are *more* authored blocking of the same kind: their footprints become blocked cells in the very same `PathabilityGrid`. So they fold into `CanonicalModelHash` (6→7) exactly as painted cells do (5→6), forcing one explicit re-baseline of the handshake fixtures; per-tick `SimChecksum` stays 15 because the effect reaches it transitively via `Position`, and folding a static per-map footprint there would churn all 23 tick goldens for zero behavioral signal. Non-blocking props, cameras, and rotation are cosmetic (like `DisplayName`) → excluded from both hashes. This is why the two hash-fold tests are the ones that must assert "blocking prop/water move CMH; non-blocking / camera / rotation do NOT."

**Why props go through `EntityPlacer`, not a separate tool.** The multi-select AC requires one marquee to span units/buildings/nodes *and* props with unified group move/delete/duplicate/undo. A separate prop tool with its own selection could not satisfy that. So props are a `PlacementMode` in `EntityPlacer` (sharing its history + the new selection set), while a distinct `PropRenderer` satisfies the MultiMesh-only rendering mandate. Cameras and water, by contrast, are not entity-multi-select members (a named viewpoint / a volume), so they are standalone toggle tools on the RegionTool skeleton.

**Un-stamp for free.** The `PathabilityGrid` is rebuilt from source every load, so a moved/deleted blocking prop or removed water volume un-stamps deterministically without an incremental mutation path — the same property 6.5 relies on. No reference-counting against hand-painted cells is needed because the painted layer and the prop/water footprint sources are kept separate (the painted bitset is unchanged; footprints union at load and fold into the hash independently).

**Persistence: inline, not `props.json`.** The GDD names a `props.json` package slot, but the shipped `ContentPackager` keeps regions/triggers/objectives inline in `scenario.json` and has no props slot. Following that precedent (and 6.4's Regions "no map-package format change"), props/cameras/water persist inline in `scenario.json`; `.chimera.zip` round-trips them for free. The observable AC (round-trip through save/load/package/import) is satisfied identically; only internal storage differs from the GDD's aspirational separate-file layout.

## Verification

**Commands:**
- `dotnet build godot.sln` — expected: clean compile, 0 errors, no new banned-API/determinism analyzer warnings on the sim path (`PathabilityGrid`/`CanonicalModelHash`/`ScenarioValidator`/`ScenarioLoadPhase` stay `float`/`Mathf`/`System.Random`-free; the only float→`Fixed` boundary is load-time footprint/threshold decode).
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all new props/cameras/water/hash/validator tests green; the **23 per-tick goldens + `KnownWorldState_ProducesPinnedV15Hash` + `SimChecksum.AlgoVersion==15` UNCHANGED** (any movement there = wrongly folded into `SimChecksum` → STOP and fix, per Block-If); `CanonicalModelHashTests`/`StartStateHashTests`/`hero-start-state.golden.txt`/`VersionStampConsistencyTests` (and the sibling fixtures listed in the Code Map) re-baselined **once** with `AlgoVersion` 6→7 (record via `CHIMERA_GOLDEN_RECORD=1`, then rebuild). Note the 2 pre-existing `PersistenceManifestTests` failures on baseline (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`); confirm unrelated via `git stash` if seen — any *other* new failure is real.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for the palette, drag input, `MultiMesh` prop rendering, camera preview, water plane, or `.chimera.zip` UI):**
- Place/rotate/scale props; confirm `MultiMesh` rendering and that hundreds of props stay within frame budget; toggle blocking on a prop and confirm the pathability overlay shows its footprint (hidden in Play).
- Marquee-select across units/buildings/nodes/props with Shift-add; group move/delete/duplicate and copy/paste at cursor; confirm grid-snapped relative offsets and single-step undo/redo interleaved with entity + terrain + pathability edits.
- Author/name/preview cameras ("view through camera"); confirm persistence and that they appear as `MoveCamera` targets.
- Place/remove a water volume; confirm the visual plane, auto-impassable footprint (units route around), and un-stamp on removal after reload.
- Save/reload and export→import `.chimera.zip`; confirm props/cameras/water/rotations round-trip identically.
- Confirm `ScenarioValidator` rejects a start/spawn position on a blocking-prop or water cell, and rejects duplicate camera names / malformed water rects.

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 6 patches applied, 6 deferred, 7 rejected, 0 spec loopbacks, committed)

### Implemented change
Story 6.6 shipped four cohesive editor additions on the established Creation-Suite patterns: (1) **doodads/props** placed through `EntityPlacer` (new `Prop`/`Select` modes, starter library, ghost, R-rotate, scale, `blocks_pathing`), rendered by a single-`MultiMesh`-per-mesh `PropRenderer`; (2) **multi-select / copy-paste / R-key rotation** across all placeable categories (marquee + Shift-add, group move/delete/duplicate + copy/paste, each one shared-history undo step), with rotation persisted as presentation-only yaw excluded from every checksum; (3) **named cameras** (`CameraTool` — capture/name/"view-through" preview); (4) a **cheap water floor** (`WaterTool` — drag-rect, visual plane, auto-impassable). New state persists inline in `scenario.json` (Regions precedent — no `ContentPackager` change) and round-trips save/load + `.chimera.zip`. Blocking props + water union their footprints into 6.5's `PathabilityGrid` at load and fold into `CanonicalModelHash` (`AlgoVersion` 6→7, one-time explicit handshake re-baseline); per-tick `SimChecksum` stays 15 (blocking reaches it transitively via `Position`); flat/legacy maps stay byte-identical (all four keys omitted).

### Files changed (one line each)
- `godot/src/Core/Definitions/ScenarioData.cs` — `ScenarioProp`/`ScenarioCamera`/`ScenarioWater` DTOs + `Props`/`Cameras`/`Water` omit-when-null arrays + omit-when-default `Rot` on building/unit/resource-node.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — normalize empty `Props`/`Cameras`/`Water` → null at the serialize chokepoint.
- `godot/src/Navigation/PathabilityGrid.cs` — `StampPropInto`/`StampWaterInto` footprint helpers + `Resolve` third blocked source; **review V1:** the single shared `BuildBlockingFootprint(props, water)` derivation.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — `AlgoVersion` 6→7; folds the blocking-prop + water footprint digest (via the shared derivation); non-blocking props/cameras/rotation/scale excluded.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — builds the blocking footprint (now delegating to the shared derivation) and threads it into `Resolve`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — structural checks (unique camera names, well-formed water rects, in-bounds/finite props, scale) + fail-closed start/unit/spawn_unit/resource on the painted ∪ prop ∪ water union (now via the shared derivation).
- `godot/src/UI/EntityPlacer.cs` — prop placement + R-rotate; marquee multi-select (Shift-add) + group delete/move/copy-paste/duplicate (one history pair each); **review E2/B3/E1:** identity-based prop delete + `PruneSelection` liveness guard.
- `godot/src/UI/PropRenderer.cs` (NEW) — one MultiMesh per distinct prop mesh; **review B6:** exact 64-bit FNV change detector (was a lossy `double`).
- `godot/src/CreationSuite/CameraTool.cs` + `Bootstrap/Phases/CameraToolPhase.cs` (NEW) — named-camera capture/preview; **review B2/E3/B15:** undo stops preview, `LookAt` colinear-up guard, corrected doc.
- `godot/src/CreationSuite/WaterTool.cs` + `Bootstrap/Phases/WaterToolPhase.cs` (NEW) — water rect + visual plane + auto-impassable footprint.
- `godot/src/Core/MainScene.cs` + `Bootstrap/Phases/CameraPhase.cs` — `SyncProp` + PropRenderer creation + prop-sync wiring.
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` + `ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — registered `CameraTool`/`WaterTool` phases (3-file lockstep).
- Tests (NEW): `ScenarioDataPropsCamerasWaterTests`, `CanonicalModelHashPropsWaterTests`, `PathabilityUnionPropsWaterTests` (+3 review-V1 shared-derivation tests), `ScenarioValidatorPropsWaterTests`, a group-op interleave test in `EditorHistoryTests`.
- Re-baselined (one-time, `CanonicalModelHash` 6→7): `CanonicalModelHashTests`, `CanonicalModelHashRegionExclusionTests`, `CanonicalModelHashPathabilityTests`, `VersionStampConsistencyTests`, `HeroProfilePersistenceTests`, `SimResetTests`, `ScenarioApplierTests` (pinned hash), `hero-start-state.golden.txt`.

### Review findings breakdown (review pass 1, 4 layers)
- Patches applied: 6 — VG1 (shared footprint derivation + 3 tests), Blind #2 (camera undo stops preview), Edge E2 (prop delete by identity), Blind #3/Edge E1 (selection liveness prune), Blind #6/Edge E5 (exact prop signature), Edge E3 + Blind #15 (camera LookAt guard + doc).
- Deferred: 6 — lossy group re-derive; non-prop visual yaw; flat-terrain marquee/markers; dock-panel arbitration; group-op composition xUnit coverage; ContentPackager zip round-trip test (all in `deferred-work.md`).
- Rejected: 7 — see Review Triage Log.

### Verification
- `dotnet build godot.sln` → Build succeeded, **0 errors, 0 warnings** on the 6.6 files (11 warnings exist but are all pre-existing `CS8632`/`CS8604` in untouched files — `GatheringSystem`/`FlowFieldSystem`/`EntityWorld`/`ResourceNodeStore`/`ResourceStore`/`UnitCardPanel.Edit`; surfaced only by a forced full rebuild). Sim path stays `float`/`Mathf`/`System.Random`-free.
- `dotnet test ProjectChimera.Sim.Tests` → **1655 passed, 2 failed, 1 skipped**. The 2 failures are the named pre-existing baseline pair (`PersistenceManifestTests.ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`), **git-stash-verified** on the clean baseline (fail identically). +3 new tests over the pre-patch run.
- Golden discipline ground-truthed: the V1 extraction is behaviour-preserving — all `CanonicalModelHash`/`StartStateHash`/golden tests pass **without re-recording**, so the handshake baseline did not move from the implementation pass. The one-time 6→7 re-baseline (from the implementation pass) is the sanctioned handshake step; the 23 per-tick goldens + `SimChecksum.AlgoVersion==15` are unchanged.
- Matrix Test Audit: every I/O-matrix row with an automatable surface (persistence round-trip, blocking/non-blocking footprint union, hash fold + exclusions, handshake propagation, validator fail-closed, flat-map omission) is covered by a ran-and-passed test; UI-interaction halves (marquee drag, copy-paste offsets, camera preview render, water-plane render) are the spec's designated manual godot-verify checks.
- Manual (godot-mcp / godot-verify) checks NOT executed in this unattended run — no xUnit surface for palette/drag/`MultiMesh`/camera-preview/water-plane/`.chimera.zip` UI. The implementation booted to the main menu without runtime errors during the dev pass.

### Follow-up review recommendation: **true**
The change spans four features, and its highest-risk surface — the `EntityPlacer` multi-select/copy-paste/group-move composition — is entirely xUnit-uncovered (Godot-coupled) with several real edge-case findings deferred (lossy re-derive, non-prop visual yaw, flat-terrain selection). A follow-up pass — ideally an in-engine godot-verify of the editor manipulation UX and a look at the deferred fidelity items — would add value beyond this automated pass.

### Residual risks
- **Editor manipulation surface (`EntityPlacer` group ops)** has no automated coverage and known deferred edge cases: group move/duplicate/paste re-derives placements lossily (worker→combat, building `pre_built`, node collection fields — single-delete paths are correct); freed-then-reused same-index selection slots aren't caught by `PruneSelection`. Presentation/authoring-fidelity only — no determinism impact.
- **Non-prop rotation is persisted but not visually applied** (units/buildings/nodes) — cosmetic, footprints axis-aligned; props (the emphasized MultiMesh case) rotate fully.
- **`props.json` deviation:** props/cameras/water persist inline in `scenario.json` (Regions precedent), not the GDD's named `props.json` package slot; the observable round-trip AC holds, but the `.chimera.zip` package/import leg is tested only at the JSON-serializer layer, not the zip surface.
- **Flat-terrain assumptions** in marquee/markers on Story-6.3 elevated maps (a pre-existing editor-wide y=0 convention).
- Manual in-engine verification of every editor tool/overlay/preview/package UI is outstanding (unattended run).

### Follow-up review pass 2 (2026-07-15)

A second independent 4-layer adversarial review (blind / edge-case / verification-gap / intent-alignment) of the committed diff since baseline `9c205a3`. No intent gap and no bad-spec root cause surfaced; the auditor confirmed the code implements the intent, with the divergence being a verification-surface split (the determinism/data spine is xUnit-pinned; the four editor interaction surfaces are the spec's designated manual godot-verify checks).

**Patches applied (2):**
- `[medium]` **Building group-move undo/redo stranded live position (F2).** `BuildingStore`'s LIFO slot reuse meant a group-move's delete→recreate overwrote `Position` in the reused slot, and the building delete-undo / create-redo closures resurrected via `Alive[id]=true` without re-writing `Position`/`Type`/`FactionOf` — so after undo the building sat at the moved position while `ScenarioData` was correctly restored (live store ≠ persisted until reload). Both closures now write full identifying state (mirroring the unit `RestoreUnit` pattern; nodes were already immune via `_count++`). (`EntityPlacer.cs`.)
- `[low]` **`MoveSelection` phantom no-op undo (E5).** Added the empty-selection guard the sibling group ops already had, so a fully-pruned move no longer strands a dead undo step. (`EntityPlacer.cs`.)

**Deferred (3, NEW `deferred-work.md` entries):** F5 Edit→Play footprint staleness (pre-existing `ScenarioLoadPhase`/`ResetToAuthoredStart` mechanism shared with 6.5, AC-consistent, no desync); `MapBounds > 128` footprint-cell aliasing at the ±128 flow-grid clamp (pre-existing whole-editor convention); no map-bounds guard on prop place/paste/group-move (fail-closed at validator).

**Rejected (14):** most notably F1 (F5 staleness — routed to defer not fix, as pre-existing + AC-consistent) and F10 (re-baseline completeness — empirically confirmed: full Tier-1 suite green at 1655 pass / 2 named pre-existing fails, `CanonicalModelHash.AlgoVersion==7` pinned across all fixtures, per-tick `SimChecksum.AlgoVersion==15` unchanged). Remainder were latent/unreachable, by-design, structurally-safe, or already-tracked items — see the Review Triage Log.

**Verification (pass 2):** `dotnet build godot.sln` → 0 errors (11 pre-existing CS8632/CS8604 warnings in untouched files, unchanged). `dotnet test ProjectChimera.Sim.Tests` → 1655 passed / 2 failed (the named pre-existing `PersistenceManifestTests` pair) / 1 skipped — identical to the pre-patch run, as the patches are presentation-side (`EntityPlacer` is Godot-coupled, excluded from the Tier-1 set). The building group-move undo correctness is an in-engine godot-verify surface, not exercised by this unattended run.

**Residual risk (pass 2):** the F2 fix hardens the building undo/redo path against slot-reuse residue, but the broader `EntityPlacer` group-op composition (marquee, copy/paste offsets, interleaved undo across categories) remains xUnit-uncovered and is the standing reason a manual godot-verify of the editor manipulation UX still adds value.
