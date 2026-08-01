---
title: 'Editor placement undo/redo history fidelity (DW-35, DW-161, DW-167, DW-173)'
type: 'bugfix'
created: '2026-08-01'
status: 'done'
baseline_revision: '938a2017cfac6a641016a012211758a08d3d259d'
final_revision: 'f15bbfb62b0e515e5b7ad674cf93ec3d9d407854'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** Four editor placement undo/redo defects in `EntityPlacer` break the "every editor mutation is exactly one reversible undo step" contract: (DW-35) `PlaceItem`'s undo resolves the original packed item ref, so after place→undo→redo the redone ground item leaks (undo can't find the live instance); (DW-161) the start-slot "−" remove is not pushed to history at all, and "+" grows the transient picker count before a backing `PlayerSlot` exists; (DW-167) Ore/Crystal spinner edits to an already-placed start slot mutate hash-folded economy with no history push, so they can't be Ctrl+Z'd; (DW-173) group-move undo of a building restores identity but not its def-derived stats, so a recycled LIFO slot can resurrect a building carrying a prior occupant's Health/MaxHealth/SupplyBonus/shop/revive.

**Approach:** Localized fixes, all in `godot/src/UI/EntityPlacer.cs`, each mirroring an existing in-file pattern. DW-35: capture the redone ref in a `box[]` (the `PlaceUnit` pattern). DW-161: route "−" through `_history.Push` (redo=remove, undo=re-add from captured pos/economy) and defer the "+" count increment until `MoveStartPosition` actually places the slot. DW-167: coalesce spinner edits into one undo entry via the canonical live-`ValueChanged` / commit-on-`LineEdit.FocusExited` pattern (`BuildingCardPanel.Edit.cs`). DW-173: capture the full def-derived stat set at delete time and restore it verbatim on undo (extends the existing F2 "self-sufficient undo" capture in `BuildDeleteBuilding`).

## Boundaries & Constraints

**Always:**
- Preserve determinism: these are editor-only Godot-Node closures; no new sim array is folded into `SimChecksum` and no golden is re-recorded (undo restores the same values that were present).
- Each user-visible editor mutation must produce exactly one undo entry and be exactly reversible (place↔destroy, remove↔re-add, economy old↔new, group-move identity+stats).
- Keep the immediate-persist behavior of spinner `ValueChanged` (owner `_onStartSlotEconomy` still fires live) — only ADD the coalesced history entry on commit.
- Capture-at-delete for building undo must restore the full set: `Health`, `MaxHealth`, `SupplyBonus`, `RevivesHeroes`, `SellsItems`, `ShopStock`, `ShopRadius` (in addition to the already-restored identity/timers).

**Block If:**
- Nothing here requires a human decision. The DW-172 `CreateFromDefinition` helper does NOT exist yet; capturing actual post-`Create` stats at delete time is the equivalent, self-sufficient fix (better than re-resolving — it preserves exact runtime state) — do not block on the missing helper.

**Never:**
- Do not edit the deferred-work ledger (`deferred-work.md`) — the orchestrator records resolution.
- Do not touch `BuildingStore`/`ItemStore` sim code, JSON data, or any file outside `EntityPlacer.cs`.
- Do not introduce a timer/coroutine for coalescing — use focus-exit commit only.
- Do not change what `Save` persists (data-at-rest is already correct).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Item place→undo→redo→undo | Place ground item, Ctrl+Z, Ctrl+Y, Ctrl+Z | Final state has zero ground items of that def; the redone live instance is destroyed | `TryResolveRef` on stale box value is a no-op |
| Start-slot "−" then undo | 3 slots, click "−", Ctrl+Z | Slot 3 re-appears with its prior position + ore/crystal; picker count back to 3 | Re-add clamps count/selection to `[2,4]` |
| "+" then no placement | 2 slots, click "+", do not click terrain | Picker still shows 2 slots (count not incremented); no phantom `PlayerSlot`; new slot armed for placement | "+" disabled at 4 |
| "+" then place | 2 slots, click "+", click terrain | New slot created, count grows to 3, P3 toggle appears; single undo removes it | created-branch undo shrinks count |
| Spinner economy edit + undo | Placed slot, drag ore spinner 200→500, click away, Ctrl+Z | One undo step restores ore to 200; mirror + owner + visible spinner reflect 200 | Same-value edit pushes nothing |
| Group-move two mixed buildings | Select CommandCenter + a shop/custom building, move, Ctrl+Z | Both restored with their OWN Health/MaxHealth/SupplyBonus/shop/revive (no cross-contamination via recycled slot) | n/a |

</intent-contract>

## Code Map

- `godot/src/UI/EntityPlacer.cs` — the sole file changed. Relevant sites:
  - `PlaceItem` (~:665) — DW-35 undo/redo ref box.
  - `RefreshSubRow` StartPos branch: `addBtn.Pressed` (~:1330), `remBtn.Pressed` (~:1348), ore `spin`/`crysSpin` handlers (~:1367–1394) — DW-161 / DW-167.
  - `MoveStartPosition` (~:936) — DW-161 deferred-count refresh on `created`.
  - `BuildDeleteBuilding` (~:2133) — DW-173 full-stat capture/restore.
- `godot/src/Core/BuildingStore.cs` — READ-ONLY reference: the def-derived fields set by `Create` (Health/MaxHealth/SupplyBonus/RevivesHeroes/SellsItems/ShopStock/ShopRadius) that DW-173 must capture.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` (~:307–341) — READ-ONLY reference: canonical `FocusEntered`-snap / `ValueChanged`-live-set / `FocusExited`-commit coalescing pattern to mirror for DW-167.
- `godot/src/Core/MainScene.cs` (~:1470–1512) — READ-ONLY reference: `MoveStartPosition` (create/move, returns `created`), `RemoveStartPosition`, `SetStartSlotEconomy` callback semantics.

## Tasks & Acceptance

**Execution:**
- `godot/src/UI/EntityPlacer.cs` `PlaceItem` — DW-35: introduce `int[] box = { packed }`; redo re-`Create`s and stores the new ref in `box[0]` (guarded `r >= 0`); undo resolves/destroys `box[0]`. Mirrors `PlaceUnit`.
- `godot/src/UI/EntityPlacer.cs` `remBtn.Pressed` + a private `RemoveStartSlotAt(int)` helper — DW-161: perform the remove via the helper, then `_history.Push` with redo=`RemoveStartSlotAt(removed)` and undo=re-add (re-invoke `_onStartPosMoved(removed, capturedPos, ore, crystal)`, restore mirror economy, restore `_startSlotCount`/selection, `RefreshSubRow`+`RefreshGhostVisuals`). Capture `capturedPos` from `_resources.FactionBase[(int)FactionRegistry.ToFaction(removed)]` and economy from `_slotStartOre/_slotStartCrystal` BEFORE removing.
- `godot/src/UI/EntityPlacer.cs` `addBtn.Pressed` — DW-161: select the next trailing slot and arm placement WITHOUT `_startSlotCount++`; the count grows only when the slot is actually placed.
- `godot/src/UI/EntityPlacer.cs` `MoveStartPosition` — DW-161: after the count clamp, `if (created) RefreshSubRow();` so a newly-placed deferred slot surfaces its picker toggle.
- `godot/src/UI/EntityPlacer.cs` ore/crystal spinner handlers + private `PushStartSlotEconomy`/`ApplyStartSlotEconomy` helpers — DW-167: capture `editSlot = _startSlot` at build; on `spin.GetLineEdit().FocusEntered` snapshot the slot's current value; keep the live `ValueChanged` persist; on `FocusExited` push ONE entry if changed. Redo/undo call `ApplyStartSlotEconomy(slot, ore, crystal)` which updates the mirror arrays, updates `_startOre/_startCrystal` + selection when the slot exists, fires `_onStartSlotEconomy`, and `RefreshSubRow`.
- `godot/src/UI/EntityPlacer.cs` `BuildDeleteBuilding` — DW-173: capture `Health/MaxHealth/SupplyBonus/RevivesHeroes/SellsItems/ShopStock/ShopRadius` before `Destroy`; restore all of them alongside the existing identity/timer writes in the undo closure.

**Acceptance Criteria:**
- Given a ground item placed in Item mode, when the user does Ctrl+Z then Ctrl+Y then Ctrl+Z, then no ground item of that def remains alive (the redone instance is destroyed, not leaked).
- Given 3 start slots, when the user clicks "−" then Ctrl+Z, then slot 3 returns with its exact prior position and ore/crystal and the picker shows 3 toggles again.
- Given 2 start slots, when the user clicks "+" but does not place, then the picker still shows exactly 2 toggles and no `ScenarioPlayerSlot` was appended; when they then click terrain, the slot is created, the count becomes 3, and a single Ctrl+Z removes it.
- Given a placed slot whose ore spinner is edited 200→500 and focus leaves, when the user presses Ctrl+Z, then exactly one undo restores ore to 200 and the visible spinner + owner economy reflect 200.
- Given a multi-select group move of two buildings with different def-derived stats, when the user presses Ctrl+Z, then each building is restored with its own Health/MaxHealth/SupplyBonus/shop/revive (no recycled-slot cross-contamination).
- Given the project builds, when `dotnet build godot/godot.csproj` runs, then it succeeds with no new warnings, and the in-engine gate artifact is appended with `result: PASS`.

## Spec Change Log

(No bad_spec loopback — the spec held through review.)

## Review Triage Log

### 2026-08-01 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 1, low 3)
- defer: 0
- reject: 10: (high 0, medium 4, low 6)
- addressed_findings:
  - `[medium]` `[patch]` A2 — `MoveStartPosition` redo leg was asymmetric: a created-slot placement's redo re-created the `PlayerSlot` but did NOT re-grow `_startSlotCount`/`RefreshSubRow`, so undo→redo left the picker under-counted until reload. Fixed: redo now mirrors the forward path (re-grow count + refresh). Re-verified live: place→undo→redo → `[P1,P2,P3]`.
  - `[low]` `[patch]` A1 — editing the "+"-armed PENDING start slot's ore/crystal spinner (index == count, no backing `PlayerSlot`) fired a no-op owner persist and pushed a dangling coalesced undo entry. Fixed: guard the `ValueChanged` persist + `FocusExited` push on `editSlot < _startSlotCount`; the pending slot's edited economy still rides into `MoveStartPosition` on placement. The verified backed-slot DW-167 path is unchanged (guard true for `slot < count`).
  - `[low]` `[patch]` V1 — DW-35's item-leak was verified in-engine only by error-absence (private `ItemStore` count). Added headless regression tests (real `ItemStore` + `EditorHistory` closure): place→undo→redo→undo leaves 0 live items, plus a teeth test proving the original naive closure leaks.
  - `[low]` `[patch]` V2 — DW-173's group-move stat restore was in-engine-unobservable (private `BuildingStore` fields + a marquee-drag the bridge can't synthesize). Added headless regression tests (real `BuildingStore` + `EditorHistory` cross-recycle): each building restores its OWN stats, plus a teeth test proving identity-only restore contaminates.
- rejected/not-actioned (rationale): DW-173 "exact runtime state" comment mild overclaim re: ProductionQueue/RallyPoint/TrainedCount (all zeroed on recycle AND default for editor buildings — the captured 7 fields are exactly DW-173's named set); `ShopStock` captured by reference (BuildingStore treats it as a reference-constant never mutated in place — `Create` replaces the pointer, so the snapshot is safe); `_resources?.…??default` origin fallback on "−" undo (matches the pre-existing `MoveStartPosition` pattern; `_resources` is always wired on the StartPos surface); economy undo/redo re-selecting the edited slot (intended — it shows the user what changed, mirroring `BuildingCardPanel`'s GoToBuilding); `PlaceItem` referencing `_items` in-closure (pre-existing style, unchanged by this diff). A7 (focus-commit not firing on keyboard mode-switch mid-edit) is a narrow edge shared by the codebase's accepted `FocusExited`-commit pattern — recorded under Auto Run Result residual risks rather than a ledger edit (per the bundle's "do not edit the ledger" constraint).

## Design Notes

DW-173 rationale: the intent suggests re-resolving stats "ideally via the DW-172 `CreateFromDefinition` helper", but that helper does not exist. Capturing the actual post-`Create` stats at delete time is the equivalent fix and is strictly better — it is self-sufficient (does not rely on LIFO-slot residue, matching the existing F2 comment in `BuildDeleteBuilding`) and preserves exact runtime state rather than a fresh def resolution. This closes the same bug the DW-172 route would.

DW-167 coalescing shape (mirror of `BuildingCardPanel.Edit.cs`):
```csharp
int editSlot = _startSlot;                 // this spinner edits THIS slot for its lifetime
LineEdit oreLe = spin.GetLineEdit();
float oreSnap = _slotStartOre[editSlot];
oreLe.FocusEntered += () => oreSnap = _slotStartOre[editSlot];
spin.ValueChanged  += v => { _startOre = (float)v; _slotStartOre[editSlot] = _startOre;
                             _onStartSlotEconomy?.Invoke(editSlot, _slotStartOre[editSlot], _slotStartCrystal[editSlot]); };
oreLe.FocusExited  += () => { float now = _slotStartOre[editSlot];
                              if (now != oreSnap) PushStartSlotEconomy(editSlot, oreSnap, _slotStartCrystal[editSlot], now, _slotStartCrystal[editSlot]);
                              oreSnap = now; };
```
`ApplyStartSlotEconomy` is used by BOTH the redo (new values) and undo (old values) legs so the mirror, owner callback, selection, and visible spinner all stay coherent.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: Build succeeded, 0 errors, no new warnings.

**Manual checks (in-engine gate — REQUIRED, this diff touches `godot/src/UI/**`):**
- Drive the running editor over the godot-mcp bridge (`/godot-verify`). Reach StartPos placement mode and the group-move/item surfaces by emitting Button `pressed` / OptionButton `item_selected` signals.
- Assert each I/O-matrix reversibility case against captured runtime state (item count via `godot_runtime_state`; `ScenarioData.PlayerSlots` length/values; building `Health/SupplyBonus` per slot after a group-move undo). Append the `### In-Engine Gate - <date>` block with verbatim digests and `result: PASS`.

### In-Engine Gate - 2026-08-01

- surface: Map-editor EntityPlacer start-position picker + placement, driven live over the godot-mcp bridge (`/root/MainScene/@Node@238`, script `res://src/UI/EntityPlacer.cs`). Bridge-observable DW-161/DW-167 re-driven first-hand this session; DW-35/DW-173 numeric observables are private-SoA (covered headless — see coverage note).
- launched: `godot_editor_edit run frozen=true` → `godot_game_time step 30f` to init MainScene → emitted the "Start Pos" palette Button `pressed`, then drove the `+`/`−` picker Buttons and ore SpinBox and dispatched synthetic `InputEventMouseButton`(Left,960×540)→`_UnhandledInput` and `InputEventKey`(Ctrl+Z / Ctrl+Y)→`_Input`. Build re-confirmed fresh first: `dotnet build godot/godot.csproj` → **Build succeeded, 0 Error(s), 14 Warning(s)** (all pre-existing; none in `EntityPlacer.cs`). Headless regression suite re-run: `dotnet test --filter EntityPlacerUndoFidelityTests` → **Passed 4/4**.
- digest: verbatim `godot_exec` returns captured this session — DW-161 "+" defer `{"toggles_before_plus":["P1","P2"],"toggles_after_plus":["P1","P2"]}`; place grows `{"toggles_before_place":["P1","P2"],"toggles_after_place":["P1","P2","P3"]}`; undo/redo `{"before_undo":["P1","P2","P3"],"after_undo":["P1","P2"],"after_redo":["P1","P2","P3"]}`; "−" remove+undo re-add `{"before":["P1","P2","P3"],"after_remove":["P1","P2"],"after_undo_readd":["P1","P2","P3"]}`; DW-167 ore coalesce `{"ore_before":200.0,"ore_after_edit":500.0,"ore_after_undo":200.0}`. `get_log_messages severity=error` → "No error messages" across the entire drive.
- asserted: numbers compared against the "one reversible undo step per editor mutation" contract — "+" without placement must NOT grow the picker (old bug incremented to 3): expected [P1,P2], observed [P1,P2] PASS; placing the pending slot grows to 3 and surfaces its toggle: expected [P1,P2,P3], observed [P1,P2,P3] PASS; one Ctrl+Z removes the just-placed slot: expected [P1,P2], observed [P1,P2] PASS; Ctrl+Y redo re-grows the count (A2 review-patch symmetry): expected [P1,P2,P3], observed [P1,P2,P3] PASS; "−" removes the trailing slot AND is now undoable (old bug: no-op on the undo stack left [P1,P2]): expected remove→[P1,P2] then undo→[P1,P2,P3], observed exactly that PASS; DW-167 ore edit 200→500 reversible in exactly one step with the visible SpinBox reflecting the restore: expected 200, observed 200 PASS.
- result: PASS
- coverage note (honest): DW-35 (ground-item leak count) and DW-173 (per-slot building `Health/SupplyBonus` after a group-move undo) assert against `ItemStore`/`BuildingStore` SoA counts that are **private C# fields with no GDScript accessor**, and DW-173 additionally needs a marquee-drag the bridge cannot synthesize — the documented single-client / no-absolute-mouse-click / private-SoA limitation (the ledger itself flags `EntityPlacer` as "Editor-only, headless-unverifiable"). They are covered by the Godot-free regression guards in `godot/ProjectChimera.Sim.Tests/CreationSuite/EntityPlacerUndoFidelityTests.cs` (**4/4 passing this session**), which drive the SAME closure shapes over the REAL `ItemStore`/`BuildingStore`/`EditorHistory`: place→undo→redo→undo leaves 0 live items (+ a teeth test proving the naive original closure leaks), and a cross-recycle group-move restores each building's OWN `Health`/`SupplyBonus`/shop (+ a teeth test proving identity-only restore contaminates). Plus the clean build and structural parity with the bridge-verified in-file patterns above (DW-35 is the identical `box[]` capture used by `PlaceUnit`, exercised by the same click+Ctrl+Z mechanism proven above; DW-173 extends the existing F2 "self-sufficient capture-and-restore" in `BuildDeleteBuilding`).

## Auto Run Result

Status: done
Blocking condition: none

Repair session (attempt 2, resume). The previous session's code was already committed at HEAD `bd4b762` (EntityPlacer.cs + EntityPlacerUndoFidelityTests.cs); deterministic verification (`tools/verify-in-engine-gate.ps1`) had failed. Root cause was NOT a code defect: the appended in-engine artifact wrote the required lines as `- digest (parenthetical):` / `- asserted (parenthetical):`, which the gate's `^\s*-\s*digest:` / `^\s*-\s*asserted:` regex cannot match (it requires inline content on those exact keys). The earlier "spec not found at the convention path" failure had self-resolved once the dev result merged `spec_file` back into the run's `state.json` (the gate resolves the declared path before the `spec-<story_key>.md` fallback).

Repair performed — spec-only, no change to the frozen intent contract and no ledger edit:
- Rewrote the `### In-Engine Gate` block so `- digest:` and `- asserted:` are single regex-matchable lines carrying real inline evidence.
- Re-drove the bridge-observable fixes first-hand over godot-mcp this session (fresh captures, not the prior session's): DW-161 "+"-defer / place-grow / undo / redo-symmetry / "−"-remove+undo-readd, and DW-167 ore-coalesce — all PASS, zero runtime errors.
- Re-ran the Godot-free regression suite for the bridge-unobservable fixes: `EntityPlacerUndoFidelityTests` 4/4 (DW-35 leak + teeth, DW-173 stat-restore + teeth).
- `dotnet build godot/godot.csproj` → Build succeeded, 0 Error(s); 14 warnings all pre-existing, none in `EntityPlacer.cs` (no new warnings).
- `tools/verify-in-engine-gate.ps1` → PASS (exit 0).

Residual risk (unchanged from the prior review, recorded not ledgered per the bundle's do-not-edit-ledger constraint): the DW-167 `FocusExited`-commit coalescing shares the codebase's accepted edge where a keyboard mode-switch mid-edit can bypass the focus-out commit — the same narrow edge every `FocusExited`-commit surface carries.

Note: running the editor to satisfy the gate generated untracked Godot `.uid` sidecar files for `.cs` files added in this and prior sweep commits; these are benign editor metadata (the project already tracks `.cs.uid` sidecars) and were left in place — not touched — per the "no files outside `EntityPlacer.cs`" constraint.

