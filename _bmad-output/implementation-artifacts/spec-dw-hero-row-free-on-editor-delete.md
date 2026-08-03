---
title: 'DW-52: Free the HeroStore row on editor delete'
type: 'bugfix'
created: '2026-08-03'
status: 'done'
baseline_revision: '97674fa853dab3d5cae1c3baca9150e3e8534ab0'
final_revision: 'a19d0e0'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** Deleting a hero-linked unit in the editor orphans its `HeroStore` row. `EntityWorld.Destroy` tears down only the entity; it never frees the linked `HeroStore` row nor clears that row's back-reference `EntityId`, so the row leaks (a permanently-live slot the ascending-identity fold keeps visiting) and its `EntityId` dangles at a dead/recycled entity id. Latent today (no editor flow deletes a re-minted hero) but a correctness landmine the moment one does.

**Approach:** Give `HeroStore` a pure-sim `DestroyByRef(packedHeroRef)` that resolves a packed `HeroIndex` handle to its live slot and frees it, and make `HeroStore.Destroy` clear the freed slot's `EntityId` back-reference. In the editor delete path (`EntityPlacer`), before `_world.Destroy(id)`, call `_heroes?.DestroyByRef(_world.HeroIndex[id])` so a hero-linked unit's row is freed. All behavioral logic lives in pure sim; the coupled `EntityPlacer` change is dependency injection + a one-line delegation.

## Boundaries & Constraints

**Always:**
- Free the row via the generation-stamped `PackRef`/`TryResolveRef` handle (ABA-safe) — never a bare slot. A `HERO_NONE` (`-1`) or stale handle must be a clean no-op.
- Clear the freed row's `EntityId` to `-1` ("no linked entity", mirroring `EntityWorld.HERO_NONE`) inside `HeroStore.Destroy`, so every future caller yields a clean dead row.
- Keep `HeroStore.Destroy`'s existing bounds + double-free guard intact.
- `HeroStore.Count` stays a monotonic high-water mark (freeing pushes the slot to the free-list; it does not shrink `Count`).

**Block If:**
- Freeing the row on delete would require re-minting it on undo to preserve hero identity (i.e. if the accepted "restore as plain unit" behavior of `RestoreUnit` were to change). It does not here — `RestoreUnit` already restores a plain unit (DW-51's resolution), so this stays a self-contained free. If investigation shows `RestoreUnit`/`SnapshotUnit` re-establishes hero linkage, HALT with `blocked` / `undo re-mint contract undefined`.

**Never:**
- Do NOT free the hero row inside `EntityWorld.Destroy` or the `OnDestroy` hook — those fire on gameplay death, where a hero row must PERSIST (death→revival keeps the row `Alive`). This fix is the editor delete path only.
- Do NOT fold `EntityId` into `SimChecksum` or move any golden — `EntityId` is non-folded runtime state (per its `HeroStore` field doc); clearing it is checksum-neutral.
- Do NOT add debug/test hooks to production code for the in-engine gate.
- Do NOT edit the deferred-work ledger.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Hero-linked delete | Live hero at slot `s`, `EntityId[s]=e`, `world.HeroIndex[e]=PackRef(s)` | `DestroyByRef(HeroIndex[e])` → `Alive[s]=false`, `EntityId[s]=-1`, freed slot on free-list, returns `true`, `Count` unchanged | No error expected |
| Non-hero delete | `world.HeroIndex[e]=HERO_NONE` (`-1`) | `DestroyByRef(-1)` → no-op, returns `false`, store untouched | No error expected |
| Stale/recycled handle | Handle to a slot whose generation was bumped (re-minted) | `DestroyByRef(stalePacked)` → `TryResolveRef` fails → no-op, returns `false`; the NEW occupant is never freed | No error expected |
| Double delete | Same slot already freed | `Destroy(s)` again → guarded no-op (slot not `Alive`); never double-pushed to free-list | No error expected |

</intent-contract>

## Code Map

- `godot/src/Core/HeroStore.cs` -- pure-sim SoA store; `Destroy(int slot)` (line ~317), `PackRef`/`TryResolveRef` (the generation-stamped handle). Add `DestroyByRef`; clear `EntityId` in `Destroy`.
- `godot/src/UI/EntityPlacer.cs` -- editor delete path; `DeleteUnit(int id)` (~1887) and `BuildDeleteUnit(int id)` (~2444) both do `SnapshotUnit(id)` then `_world.Destroy(id)`. Needs a `HeroStore? _heroes` field + delegation.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` -- constructs + `Initialize`s the `EntityPlacer` (~line 38); wire `_ctx.Host.Heroes` through.
- `godot/src/Core/EntityWorld.cs` -- `HeroIndex[]` (packed hero handle per entity), `HERO_NONE = -1`. Read-only reference here.
- `godot/ProjectChimera.Sim.Tests/Core/HeroStoreTests.cs` -- existing recycle/ABA tests; add `DestroyByRef` + `EntityId`-clear coverage.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/HeroStore.cs` -- In `Destroy(int slot)`, after marking the slot dead, set `EntityId[slot] = -1` (clear the dangling back-reference). Add `public bool DestroyByRef(int packedHeroRef)`: `if (!TryResolveRef(packedHeroRef, out int slot)) return false; Destroy(slot); return true;`. Document both (DW-52).
- `godot/src/UI/EntityPlacer.cs` -- Add `private HeroStore? _heroes;` field; add a `HeroStore? heroes = null` parameter to `Initialize` and assign `_heroes = heroes;`. In `DeleteUnit(int id)` and `BuildDeleteUnit(int id)`, immediately before `_world.Destroy(id)`, call `_heroes?.DestroyByRef(_world.HeroIndex[id]);` (DW-52). Do NOT touch the redo legs (they delete plain restored units).
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` -- Pass `_ctx.Host.Heroes` into the `placer.Initialize(...)` call for the new `heroes` parameter.
- `godot/ProjectChimera.Sim.Tests/Core/HeroStoreTests.cs` -- Add tests covering the four I/O matrix scenarios (hero-linked free clears `EntityId` + frees slot + returns true + `Count` unchanged; `HERO_NONE`/`-1` no-op returns false; stale handle no-op returns false and does not free the re-minted occupant; `Destroy` clears `EntityId`).

**Acceptance Criteria:**
- Given a live hero at slot `s` with `world.HeroIndex[e] = heroes.PackRef(s)`, when `heroes.DestroyByRef(world.HeroIndex[e])` is called, then `heroes.Alive[s]` is `false`, `heroes.EntityId[s]` is `-1`, the call returns `true`, and `heroes.Count` is unchanged.
- Given `world.HeroIndex[e] == EntityWorld.HERO_NONE`, when `heroes.DestroyByRef(world.HeroIndex[e])` is called, then it returns `false` and no row's `Alive`/`EntityId`/free-list state changes.
- Given a packed handle whose slot has since been recycled (generation bumped), when `DestroyByRef(stalePacked)` is called, then it returns `false` and the new occupant of that slot stays `Alive`.
- Given the editor deletes a unit in-engine, when the delete path runs, then it completes without a runtime error and (for a non-hero unit) leaves `HeroStore` untouched — verified via the In-Engine Gate.

## Spec Change Log

## Review Triage Log

### 2026-08-03 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 0
- reject: 8
- addressed_findings:
  - `[low]` `[patch]` Verification gap (raised by blind-hunter, verification-gap, and intent-alignment): the `EntityPlacer` delegation + `CameraPhase` wiring have no executable test — only the store primitive was covered. Added `DestroyByRef_ThroughRealHeroIndexArray_MirrorsTheEditorDeleteExpression`, a Tier-1 integration test that drives the exact editor expression `DestroyByRef(world.HeroIndex[e])` against a real `EntityWorld`, pinning the read-through-`HeroIndex` + packed-handle-not-bare-slot composition and the `HERO_NONE` no-op.
- reject rationale (not this story's problem / intended / safe):
  - delete→undo and group-move/copy-paste restore a hero as a PLAIN unit — governed by the intent's own cited decision ("pairs with DW-51 'restore as plain unit'"); `RestoreUnit`/`BuildCreate` never re-minted heroes, so this is pre-existing and accepted, not a regression. This change only converts a row *leak* into a clean *free* on those shared paths (strictly better, identical user-visible outcome).
  - `EntityWorld.Destroy` leaves `HeroIndex[id]` dangling — pre-existing; edge-case-hunter confirmed every `HeroIndex` deref is ABA-guarded (`TryResolveRef` gates on `Alive`+generation) and recycle resets it, so it is self-healing and outside DW-52's scope.
  - discarded `DestroyByRef` bool return — `false` is the NORMAL case for every non-hero unit delete; logging it would be pure noise.
  - `MAX_HEROES`→256 pack-width fragility — pre-existing `HeroStore` design not introduced here; the `HERO_NONE` no-op is robust regardless of `Count` (generation can never be `-1`), as edge-case-hunter proved.
  - remaining adversarial nits (EntityId resurrect-pattern footgun, no user-facing hero-delete log, paste duplicate-identity, ordering-not-load-bearing) — hypothetical/pre-existing, no defect caused by this diff.

## Design Notes

**Why not `EntityWorld.Destroy`/`OnDestroy`:** those fire on gameplay death, where a hero must survive as an `AwaitingRevival` row (`HeroXpSystem` re-links a fresh entity on revive). Freeing there would delete persistent heroes on every death. The DW decision scopes this to the editor delete path.

**Why the packed handle, not a bare slot:** `world.HeroIndex[e]` is already the generation-stamped `PackRef`. Routing through `TryResolveRef` means a `HERO_NONE` sentinel (`-1 & 0xFF == 255 ≥ Count`) and any stale handle (recycled slot) both resolve `false` and no-op for free — the ABA-armor the store was built around. This also removes the need for a `HERO_NONE` check in `EntityPlacer`.

**Checksum posture:** `EntityId` is documented runtime (non-folded) state; there are zero gameplay callers of `HeroStore.Destroy` today (only `Clear` and tests), so clearing `EntityId` there is invisible to `SimChecksum`, `StartStateHash`, and every golden.

**Undo:** a deleted hero unit restores via `RestoreUnit` as a plain unit (`Create` defaults `HeroIndex` to `HERO_NONE`) — the accepted "restore as plain unit" behavior. The freed hero row is not re-minted; that is intended and in scope of the paired (out-of-bundle) DW-51 decision, not a regression introduced here.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds (C# is not hot-loaded; required before any in-engine run).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~HeroStoreTests"` -- expected: all HeroStore tests pass, including the new `DestroyByRef`/`EntityId`-clear cases.

**In-Engine Gate (coupled surfaces touched: `src/UI/EntityPlacer.cs`, `src/Core/Bootstrap/Phases/CameraPhase.cs`):**
- Build, then `godot_editor_edit run`. Enter Edit + Select mode (`GameState` autoload), place a unit, delete it via the real Delete-key flow, and capture the editor log line `[EntityPlacer] Deleted unit id=…` proving the modified `DeleteUnit` path executed in the running game with `_heroes` wired and no runtime error.
- Note the observability boundary: `HeroStore`/`EntityWorld` are pure-C# with non-Variant structs (`Fixed`, `HeroId`) and no `GodotObject` base, so their state is unreachable through the GDScript-only godot-mcp bridge; a bridge-reachable hero-linked deletable editor unit is not producible today. The exact `Alive`/`EntityId` row-free assertions (the hero-linked branch) are therefore covered by the pure-sim `HeroStoreTests`; the gate verifies the coupled wiring/delete path runs correctly in-engine (the non-hero branch, the only bridge-reachable one). Append the `### In-Engine Gate` block with the captured digest.

### In-Engine Gate - 2026-08-03
- surface: editor unit-delete path in Edit mode — placed a real unit at the cursor, then deleted it via the real `Delete`-key input flow (`InputEventKey KEY_DELETE` → `EntityPlacer._Input` → `TryDeleteAt` → `DeleteUnit`, the modified path carrying `_heroes?.DestroyByRef(_world.HeroIndex[id])`).
- launched: `dotnet build godot/godot.csproj` (0 warn / 0 err, `ProjectChimera.dll` relinked) → `godot_editor_edit run res://scenes/main.tscn`; boot digest `{"mode":"0"}` = `GameMode.Edit`.
- digest: pre-place unit count (Σ MultiMesh `instance_count`) = 4; post-place `{"units_sum":5,"nearest_to_cursor":{"x":-77.1,"z":-20.58},"nearest_dist":0.0}`; post-delete `{"units_sum":4,"nearest_to_deleted_pos":39.26}`; delete-dispatch exec returned with no `runtime_errors` field; `get_log_messages severity=error` → "No error messages"; `get_stack_trace` → "No stack trace available".
- asserted: place +1 (4→5, unit at the cursor) and delete −1 (5→4, the target unit removed, pre-existing units untouched) — matches the expected single-unit place/delete deltas; the modified `DeleteUnit` path executed with `_heroes` wired (`CameraPhase` passes `_ctx.Host.Heroes`) and threw no runtime error. The placed/deleted unit is non-hero, so `_world.HeroIndex[id] == HERO_NONE (-1)` and `DestroyByRef(-1)` resolved false → clean no-op, exactly the wired behavior. The hero-linked branch (row freed + `EntityId` cleared) is unobservable via the bridge by design (pure-C# sim, non-Variant structs) and is covered by the 5 passing `DestroyByRef`/`Destroy`-clears-`EntityId` `HeroStoreTests` — an accepted boundary, not a gap: the in-engine observable is precisely the path a real editor delete exercises today.
- result: PASS

## Auto Run Result

Status: done
Blocking condition: none

**Change:** DW-52 — a hero-linked unit deleted in the editor no longer orphans its `HeroStore` row. Added pure-sim `HeroStore.DestroyByRef(packedHeroRef)` (resolves the generation-stamped `HeroIndex` handle via the ABA-safe `TryResolveRef`, then frees the live slot; a `HERO_NONE`/stale handle is a clean no-op returning `false`) and made `HeroStore.Destroy` clear the freed slot's `EntityId` back-reference to `-1`. The editor delete path (`EntityPlacer.DeleteUnit` and `BuildDeleteUnit`) now calls `_heroes?.DestroyByRef(_world.HeroIndex[id])` before tearing down the entity, with the `HeroStore` injected via a new trailing `Initialize` param wired from `CameraPhase`. Freeing lives on the editor path only — gameplay hero death never calls `HeroStore.Destroy` (heroes persist via `AwaitingRevival`), so the fix cannot delete a persistent hero on death. `EntityId` is non-folded runtime state, so the change is checksum/golden-neutral.

**Files changed:**
- `godot/src/Core/HeroStore.cs` — `Destroy` clears `EntityId` to `-1`; added `DestroyByRef` (the editor delete path's ABA-safe one-line hook).
- `godot/src/UI/EntityPlacer.cs` — new `HeroStore? _heroes` field + trailing `Initialize` param; delegation added in `DeleteUnit` and `BuildDeleteUnit` immediately before `_world.Destroy(id)`.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` — passes `_ctx.Host.Heroes` into `placer.Initialize(...)`.
- `godot/ProjectChimera.Sim.Tests/Core/HeroStoreTests.cs` — 5 new tests: the four I/O-matrix rows (hero-linked free clears `EntityId`/returns true/`Count` unchanged; `HERO_NONE` no-op; stale-handle ABA no-op; `Destroy` clears `EntityId` + double-free guard) plus a review-added integration test driving `DestroyByRef(world.HeroIndex[e])` through a real `EntityWorld`.

**Verification:** `dotnet build godot/godot.csproj` → Build succeeded (0 warnings, 0 errors), independently re-run. `dotnet test … --filter HeroStoreTests` → 20/20 passed. In-Engine Gate → PASS (place/delete deltas 4→5→4, zero runtime errors; see the block above). Review pass: 1 low patch applied (integration test for the untested delegation/composition), 8 findings rejected as pre-existing/intended (the undo/move "restore as plain unit" behavior is the intent's own cited DW-51 decision) or safe (ABA-guarded), 0 intent gaps, 0 spec repairs. `followup_review_recommended: false` (patched score = 3×0 + 1×1 = 1 < 5, no high).

**Residual risk:** low. The hero-linked delete is latent today (no editor flow mints a deletable hero), so the fix is defensive; its behavioral core is fully sim-tested, its wiring verified by build + in-engine path execution. The pre-existing "editor move/paste/undo restores a hero as a plain unit" limitation is unchanged and out of scope (DW-51's accepted decision).
