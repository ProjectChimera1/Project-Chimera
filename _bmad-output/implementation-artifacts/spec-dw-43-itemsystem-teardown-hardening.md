---
title: 'DW-43 — ItemSystem teardown hardening: never orphan carried items when the hero row dies first'
type: 'bugfix'
created: '2026-08-05'
status: 'done'
baseline_revision: '620488e1377797cc325297d6dc36499bb677c5af'
final_revision: '832a1009e46df88a908d0a197125ca0648b44c21'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** `ItemSystem.OnEntityDestroyed` resolves the dying entity's packed hero handle via `_heroes.TryResolveRef(...)` and **early-returns (dropping nothing) when it fails**. Every path today keeps the `HeroStore` row alive for revival, so the resolve always succeeds — but a future PERMANENT (non-revivable) hero removal that tears down the `HeroStore` row BEFORE the `EntityWorld` entity would make the resolve fail, leaving the carried items orphaned (`Held = true` with a dead `CarrierHeroSlot`, never on the ground, unreachable — a later `PickupItem` sees `Held` and voids). This leaks item instances permanently.

**Approach:** When the hero row no longer resolves, drop the carried items keyed off the packed handle's **carrier slot** (its low 8 bits survive a stale ref) instead of early-returning, by scanning the `ItemStore` for held instances carried by that slot and dropping them to the death position. Keep the existing live-row path (resolve succeeds → `DropAll`) byte-identical so no currently-reachable behavior — and no golden — changes.

## Boundaries & Constraints

**Always:**
- Pure-sim, deterministic: `Fixed` (16.16) only, no `float`, no `System.Random`, ascending-slot iteration only. No `using Godot`.
- The live-row path (`_heroes.TryResolveRef` succeeds) MUST remain exactly `DropAll(heroSlot, deathPos)` — unchanged, so goldens do not move.
- After the fix, no permanent removal ordering (row-then-entity, freed or recycled) can leave a `Held` item whose carrier is gone.
- If the carrier slot was RECYCLED to a different live hero before this entity is destroyed, that live hero's own carried items (the ones its current inventory ring references) MUST be left untouched — only leftover orphans of the removed hero are dropped.

**Block If:**
- Fixing DW-43 would require moving/re-recording any golden or bumping `AlgoVersion` (it must not — the changed branch is unreachable by any current path).

**Never:**
- Do not add a new folded sim array, do not touch `SimChecksum`/`AlgoVersion`, do not edit the deferred-work ledger.
- Do not implement the actual permanent hero-removal feature — this is defensive hardening of the death hook only.
- Do not alter the pickup / use / manual-drop / buy surfaces.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Live-row death (today's path) | Hero carries item; entity destroyed while `HeroStore` row still Alive | `TryResolveRef` succeeds → `DropAll` drops every carried item at death pos, ring cleared (unchanged) | No error expected |
| Row-gone, slot NOT recycled | `HeroStore.Destroy(slot)` then `world.Destroy(entity)`; item still `Held`, ring intact | `TryResolveRef` fails → item dropped to death pos, `Held=false`, `CarrierHeroSlot=NO_CARRIER`, stale ring ref cleared | No crash; no early-return |
| Row-gone, slot recycled to a NEW live hero holding its own item | Old hero row destroyed, slot re-minted to hero H2 which picks up item J; old orphan item I still `Held` (not in H2's ring); old entity then destroyed | Orphan I dropped to death pos; H2's item J untouched (`Held` stays true, remains in H2's ring) | No crash; live occupant's items preserved |
| Non-hero entity destroyed | `HeroIndex[entity] == HERO_NONE` | No-op (unchanged) | No error expected |

</intent-contract>

## Code Map

- `godot/src/Combat/ItemSystem.cs` -- `OnEntityDestroyed` (the death hook, lines ~308-313) is the sole change site; `DropAll`/`DropOne` are the existing ring-driven drop helpers to mirror.
- `godot/src/Core/HeroStore.cs` -- `TryResolveRef`/`PackRef` (packed handle = `(Generation<<8)|slot`, low 8 bits = physical slot), `Destroy` (frees slot, does NOT clear the `Inventory` ring), `Mint` (recycles a slot and clears its ring), `Alive[]`, `Inventory[]`. Read-only reference.
- `godot/src/Core/ItemStore.cs` -- `Alive[]`, `Held[]`, `CarrierHeroSlot[]`, `PosX/PosZ`, `Count`, `PackRef`, `NO_CARRIER`. The scan/drop target. Read-only reference.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` -- existing death-drop oracles (Row 8 / Row 8b) + `Harness`/`MintHero` helpers; new DW-43 tests go here.

## Tasks & Acceptance

**Execution:**
- `godot/src/Combat/ItemSystem.cs` -- Rewrite `OnEntityDestroyed`: read the packed `HeroIndex` once; keep the `HERO_NONE` no-op and the `TryResolveRef`-success → `DropAll(heroSlot, deathPos)` path exactly as today; when `TryResolveRef` FAILS, add a fallback that drops the carried items keyed off `packed & 0xFF`. -- resolves the DW-43 orphan.
- `godot/src/Combat/ItemSystem.cs` -- Add a private fallback helper that scans `_items` (`0..Count`) for `Alive && Held && CarrierHeroSlot == carrierSlot`, and for each drops the instance to the death position (flip `Held→false`, set `CarrierHeroSlot=NO_CARRIER`, write `PosX/PosZ`, remove its stat modifier keyed off the dying `entityId`, push `ItemDropped`, and clear any stale reference to it in that carrier slot's inventory ring). When the carrier slot is currently a LIVE hero (`_heroes.Alive[carrierSlot]`), skip items the live hero actually holds (those referenced in its ring) so only true orphans are dropped. -- the recycle-safe drop-by-carrier logic.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` -- Add unit tests for the two new I/O-matrix rows: (a) row-gone-not-recycled → item drops to death pos and is de-orphaned; (b) row-gone-recycled → the removed hero's orphan drops while the new live hero's carried item is preserved. -- covers the previously-unreachable branch.

**Acceptance Criteria:**
- Given a hero carrying an item whose `HeroStore` row is destroyed BEFORE its entity, when the entity is destroyed, then the item is dropped to the death position (`Held == false`, `CarrierHeroSlot == NO_CARRIER`) rather than left orphaned, and no exception is thrown.
- Given the carrier slot was recycled to a different live hero that has since picked up its own item, when the original (stale) entity is destroyed, then only the removed hero's orphan item is dropped and the live hero's carried item remains `Held` and referenced by its inventory ring.
- Given the existing live-row death path, when a hero dies carrying items, then behavior is byte-for-byte unchanged (Row 8 / Row 8b still pass) and no golden re-records / `AlgoVersion` bump are required.
- The full Godot-free Tier-1 suite builds and passes.

## Spec Change Log

### 2026-08-05 — Review patch (no code re-derivation)
- **Trigger:** Review found the fallback branch is reachable today via the DW-52 editor-delete path, contradicting the draft's "unreachable by any current code path" claim.
- **Amended:** Design Notes "Golden neutrality" paragraph rewritten to state the real reason goldens don't move (editor delete is not a sim-tick golden path; edit-mode heroes carry no items). No `<intent-contract>` change; no code behavior change.
- **Known-bad avoided:** a future maintainer trusting "this branch never runs" and, e.g., adding side effects to the fallback assuming dormancy.
- **KEEP:** the live-row `DropAll` path must stay byte-identical; the recycle discriminator (in-ring ⇒ live occupant's item, skip) must survive re-derivation.

## Review Triage Log

### 2026-08-05 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 1: (high 0, medium 0, low 1)
- reject: 8
- addressed_findings:
  - `[low]` `[patch]` The DW-43 fallback is reachable TODAY (not "a future path"): the DW-52 editor delete (`EntityPlacer.DeleteUnit`/`BuildDeleteUnit`) frees the hero row via `HeroStore.DestroyByRef` before `world.Destroy`, so `OnEntityDestroyed` fires with a stale handle on every hero deletion. Corrected the `OnEntityDestroyed` doc comment and the Design Notes: the fallback is a no-op in practice (edit-mode heroes carry no items; editor delete is not a sim-tick golden path) — the real reason goldens don't move. Conclusion held, original reasoning did not.
  - `[low]` `[patch]` The plural drop (`for` loop over multiple orphans) and the fallback's `ItemDropped` event push were unverified — the single-item DW-43 tests used a harness built with `events: null`. Added `HeroRowDestroyedBeforeEntity_SlotRecycled_DropsAllOrphans_PreservesLiveItem_EmitsDropCue`: two orphans + a recycled-slot live occupant (its pickup routed through the real `OrderApplier`/`Tick` flow, not hand-written state), asserting both orphans drop, the live item is preserved, and exactly two `ItemDropped` cues fire on a real `CombatEventQueue`.
- deferred (recorded here, NOT appended to the ledger — this run's directive is "do NOT edit the deferred-work ledger; the orchestrator records resolution"):
  - `[low]` Editor delete of a hero that carries items has no `ScenarioData` sync and no undo restore for those items — a pre-existing editor-integration gap independent of DW-43 (pre-fix it orphan-leaked the items; post-fix it drops them to the ground unsynced). Unreachable today (an edit-mode hero never carries items), but it will matter if a scenario can author a hero with a starting item or the placer runs during a play-test. Location: `godot/src/UI/EntityPlacer.cs:1916` / `:2481`. Suggest the orchestrator file a follow-up DW.
- rejected (noise / out-of-intent): speculative-code objection (fallback IS reachable and the intent explicitly sanctions the hardening); fallback-vs-`DropAll` drop-order difference (event queue is presentation-only/not folded; item-state changes are order-independent → sim-invisible); multi-recycle drop-position approximation (intent "cannot orphan" is met — all items de-orphaned; wrong death-position only under a pathological future triple-recycle); `DropOne` copy-paste (ring-index vs item-index addressing genuinely differ); modifier-removal subscription-order dependence (keyed by `entityId` → correct regardless of order); `packed & 0xFF` mask duplication (already open-coded in `HeroStore.TryResolveRef`; a width bump would touch `PackRef` too); full-store scan perf (`MAX_ITEMS`=64, off the hot path); non-hero recycle-reset coverage (tests `EntityWorld.Create`, not this change).

## Design Notes

The packed hero handle stored in `EntityWorld.HeroIndex` is `(Generation[slot] << 8) | slot`; `TryResolveRef` fails on a stale ref (freed or generation-bumped slot) but `packed & 0xFF` still yields the physical carrier slot. Held items are always co-referenced by their carrier's inventory ring (set/cleared together on claim/drop), so:
- **Freed, not recycled:** `Alive[carrierSlot] == false`; the dead hero's ring still references the orphans → drop every `Held` item with `CarrierHeroSlot == carrierSlot`.
- **Recycled:** `Alive[carrierSlot] == true`; the ring now belongs to the new hero. Discriminate with "is this item currently referenced by ring[carrierSlot]?" — in-ring ⇒ the live hero's own item (skip); not-in-ring ⇒ leftover orphan (drop).

Scan the `ItemStore` (not the ring) on the fallback path because in the recycled case the orphans are no longer in any ring. Modifier removal passes the dying `entityId` (correct key) and is a no-op once the entity's modifiers were cleared on destroy. Guard `carrierSlot < HeroStore.MAX_HEROES` before indexing `_heroes.Alive`/`Inventory` (a valid hero ref is always slot 0-63; `HERO_NONE` is filtered first).

Golden neutrality: the fallback branch IS reachable today — the DW-52 editor delete (`EntityPlacer.DeleteUnit`/`BuildDeleteUnit`) frees the hero row via `HeroStore.DestroyByRef` BEFORE `world.Destroy`, so `OnEntityDestroyed` fires with a stale handle on every hero deletion. It is nonetheless golden-neutral because (a) editor delete is not a sim-tick golden path, and (b) an edit-mode hero carries no items (items are acquired only during play, and the Edit↔Play reset `Clear`s the `ItemStore`), so the scan drops nothing. Item state is folded into `SimChecksum` v12, but no existing scenario diverges — empirically confirmed by the full Tier-1 suite passing unchanged; no re-baseline, no `AlgoVersion` bump. (The initial spec draft mis-stated this branch as "unreachable"; corrected during review after the reachable editor-delete path was found — the conclusion held, the reasoning did not.)

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: build succeeds (pure-sim, no Godot).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~ItemSystemTests"` -- expected: all ItemSystem tests pass, including the two new DW-43 tests and the unchanged Row 8 / Row 8b.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Godot-free Tier-1 suite green (no golden movement, no `AlgoVersion` change).

**In-engine gate:** NOT APPLICABLE — the diff touches only `godot/src/Combat/ItemSystem.cs` and a sim test; no `godot/src/UI/**`, `CreationSuite/**`, `Core/Bootstrap/**`, `MainScene.cs`, `scenes/**`, `*.tscn`, or `*.tres`.

## Auto Run Result

Status: done
Blocking condition: none

**Change:** Hardened `ItemSystem.OnEntityDestroyed` (DW-43) so a hero's carried items can never be orphaned when the `HeroStore` row is torn down before the `EntityWorld` entity. The live-row death path (`TryResolveRef` succeeds → `DropAll`) is byte-identical; when the packed handle no longer resolves, a new fallback (`DropOrphansByCarrierSlot`) drops the carried items keyed off the physical carrier slot (the handle's low 8 bits), scanning the `ItemStore` rather than the possibly-cleared inventory ring. A recycle discriminator (`FindInventoryRefSlot`) skips a new live occupant's own items so only true orphans of the removed hero are dropped. Review found this fallback is reachable today via the DW-52 editor-delete path (not purely "future"); it is nonetheless golden-neutral because edit-mode heroes carry no items and editor delete is not a sim-tick golden path — the comment and Design Notes were corrected to say so.

**Files changed:**
- `godot/src/Combat/ItemSystem.cs` — rewrote `OnEntityDestroyed`; added `DropOrphansByCarrierSlot` + `FindInventoryRefSlot`; corrected the reachability/golden-neutrality doc comment.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` — added four DW-43 tests: row-gone-not-recycled, row-gone-recycled (single orphan), multi-orphan + real `CombatEventQueue` (exact drop-count + `ItemDropped` cue), and the non-hero no-op; plus a `CountDrops` helper.
- `_bmad-output/implementation-artifacts/spec-dw-43-itemsystem-teardown-hardening.md` — this spec.

**Review findings:** 2 patches applied (reachability-claim correction; multi-item + event-cue test), 1 deferred (recorded in the Review Triage Log, NOT written to the ledger per this run's directive — editor-delete of a hero with items has no `ScenarioData`/undo handling, a pre-existing editor-integration gap that is unreachable today), 8 rejected. `followup_review_recommended: false` (patched severities all low; score 3×0 + 1×2 = 2 < 5, no high).

**Verification (independently re-run):**
- `dotnet test …ItemSystemTests` → 22/22 passed (Row 8 / Row 8b unchanged + 4 new DW-43 tests).
- `dotnet test …` (full Godot-free Tier-1) → 5169 passed, 1 skipped (pre-existing reserved trigger test), 1 failed = `CanonicalModelHashPerfTests…StaysUnderTheRegressionCeiling` (a CPU-contention timing flake — passes 2/2 in isolation; unrelated to this change, which is nowhere near the model-hash path). No golden movement, no `AlgoVersion` bump.
- In-engine gate: NOT APPLICABLE (pure-sim diff; the gate auditor confirmed `GATE NOT APPLICABLE`).

**Residual risks:** Low. The fallback's only production caller today is editor delete, where it is a proven no-op (no items in edit mode). The deferred editor `ScenarioData`/undo gap becomes relevant only if edit-mode heroes can ever carry items. Under a pathological future multi-recycle (two removed heroes both carrying items over one reused slot), all items are still de-orphaned but a later hero's items may drop at the earlier hero's death position — the intent ("cannot orphan Held items") holds; only drop position is approximate.
