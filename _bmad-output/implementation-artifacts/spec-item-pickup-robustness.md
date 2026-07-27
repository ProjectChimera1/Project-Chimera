---
title: 'Item pickup robustness — deny at modifier cap (DW-34) + traversal coverage (DW-39)'
type: 'bugfix'
created: '2026-07-27'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '452efa5e8880e16053f689c595f7a9db93efe8fa'
final_revision: '03b094e82668a45d5da0ce495df141a0c7f10890'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md'
warnings: ['multiple-goals']
---

<intent-contract>

## Intent

**Problem:** Two deferred hardening items from the Story 3.15 review. **DW-34:** `ItemSystem.ApplyItemStatModifier` calls `ModifierStore.Apply` and discards its (currently non-existent) result. A hero already at the per-entity modifier cap (`EffectCaps.MaxModifiersPerEntity = 8`, reachable via growth + passives + several carried stat items) that picks up another stat item CLAIMS the item (fills an inventory slot, flips ground→held) but the `Apply` silently no-ops at the cap — so the hero gains no stat bonus and gets no denial cue: a silently-inert item consumed into a slot. **DW-39:** the pickup move-to (traversal) branch has zero automated coverage — every `ItemSystemTests` case spawns the item on top of the hero, so only the immediate-proximity claim runs; the `sqrDist > rr → write MoveTarget → return` steering branch (`ItemSystem.cs:159-165`) would ship a regression green.

**Approach:** Give `ModifierStore.Apply` a `bool` return (`true` = installed or an existing same-id instance handled; `false` = refused because the target is dead or the per-entity ring is full) — a source-compatible `void→bool` change no existing caller reads and no hash folds. Thread that result out through `ItemSystem.ApplyItemStatModifier` / `ApplyStatModifierIfAny`, and at the PICKUP claim site, when a stat item's modifier is refused (hero at cap), roll back the tentative claim (ground→held un-flip, clear the inventory slot) and deny with an `OrderDenied` cue — the item stays on the ground. Add a `ItemSystemTests` case for the cap-denial, and a second case that spawns a ground item OUTSIDE `PickupRadius` and asserts the hero writes `MoveTarget` toward it (steering branch) then claims it on arrival.

## Boundaries & Constraints

**Always:**
- Sim-layer determinism is sacred: the change is control-flow + a return value only. No new `float`/`double`/`Mathf`/`System.Random`/wall-clock. No new folded state — `SimChecksum`/`StartStateHash`/`CanonicalModelHash` and every golden stay byte-identical (the `Apply` return value is not folded; the denied item simply stays on the ground exactly as the existing full-inventory deny already does).
- The cap-denial mirrors the EXISTING full-inventory deny in `ResolvePickup` (`ItemSystem.cs:167-174`): push `CombatEventType.OrderDenied` at the hero position, `EndPickupOrder`, leave the item on the ground (`Held == false`, `CarrierHeroSlot == ItemStore.NO_CARRIER`), inventory slot back to `HeroStore.INVENTORY_EMPTY`.
- Only a STAT item at the cap denies. A non-stat item (`!ItemDefinition.HasStatModifier`) never depends on `Apply`, so it always claims (return `true` when nothing was applied). The claim only rolls back on a genuine `Apply == false`.
- `ModifierStore.Apply`'s new return contract: `false` on the dead-target early-out (`:124`) and the ring-full refuse (`:138`); `true` on a fresh install (`:152`) and on every existing-same-id branch (Refresh/Stack/Ignore, after `:178`). Behavior/state on every path is otherwise UNCHANGED.
- DW-39 is TEST-ONLY: assert the steering output (`MoveTarget` set to the item position, `EntityFlags.Moving` set, order still `PickupItem`, item still on the ground) on the far tick, then move the hero to the item and assert the claim on the near tick. `ItemSystem` runs after `MovementSystem`, so a Tier-1 test with no movement system advances the hero's position by hand to model arrival.

**Block If:**
- Making `Apply` return a value cannot be done without changing an existing caller's behavior or folding new state (i.e. a caller actually depends on the discarded value in a way that shifts a checksum). HALT `blocked` — investigation shows all three call sites (`ItemSystem` pickup/buy, `HeroProfileLoader.ReMintInventory`) currently ignore the result, so this is not expected.

**Never:**
- No re-scoping DW-34's denial to the buy path (`GrantPurchasedItem`) or the persisted re-mint (`HeroProfileLoader.ReMintInventory`): those keep their current behavior (they may read the new bool but MUST NOT change claim/refund semantics here — that is Story 3.16 / profile-load territory). Only the ground-pickup claim denies.
- No routing pickup steering through `FlowFieldBridge` and no unreachable-item order timeout — DW-39's ledger flags those as a "consider", a separate gameplay change, not part of closing the coverage gap. Out of scope.
- No new SimChecksum/StartStateHash `AlgoVersion` bump, no golden re-baseline, no new item content, no UI change.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Pickup, hero at modifier cap | Hero with `MaxModifiersPerEntity` modifiers already installed is ordered onto a ground STAT item; a free inventory slot exists | Claim is denied: the item stays on the ground (`Held == false`), the tentatively-written inventory slot is cleared, no stat bonus materializes, `OrderDenied` event, order ends Idle | Deterministic reject, net-zero state change |
| Pickup, hero below cap | Same, but the hero has a free modifier slot | Normal claim: ground→held, modifier applies, `ItemPickedUp`, order ends Idle (unchanged) | No error |
| Pickup, non-stat item at cap | Hero at the modifier cap picks up a consumable/no-delta item | Claims normally — no modifier needed, `Apply` never consulted | No error |
| Pickup, item outside `PickupRadius` | Hero ordered onto a ground item farther than `PickupRadius` | Far tick: `MoveTarget` written to the item position, `Moving` flag set, order stays `PickupItem`, item still on the ground; on arrival (next in-range tick) it claims into the first free slot | No error |
| `ModifierStore.Apply` at cap (unit) | `Apply` on an entity with a full ring, new id | Returns `false`, ring unchanged (as today) | Deterministic refuse |

</intent-contract>

## Code Map

- `godot/src/Effects/ModifierStore.cs` -- `Apply` (`:122`) `void`→`bool`: `return false` at `:124` (dead) and `:138` (ring full); `return true` at `:152` (fresh install) and after the stacking switch (`:178`). No other change. Confirm no delegate/interface types `Apply` as `void` (grep — none expected).
- `godot/src/Combat/ItemSystem.cs` -- `ApplyItemStatModifier` (static, `:309`) `void`→`bool`: `return true` when `def` is null or `!HasStatModifier` (nothing to apply); otherwise `return modifiers.Apply(...)`. `ApplyStatModifierIfAny` (`:298`) `void`→`bool`, returns the inner result. `ResolvePickup` (`:167-185`): after the free-slot check, tentatively claim, then `if (!ApplyStatModifierIfAny(...))` roll back (un-flip `Held`, `CarrierHeroSlot = ItemStore.NO_CARRIER`, inventory slot = `HeroStore.INVENTORY_EMPTY`), push `OrderDenied`, `EndPickupOrder`, return. `GrantPurchasedItem` (`:112`) and the death/drop paths are untouched (they ignore the new bool).
- `godot/src/Core/Definitions/HeroProfileLoader.cs:233` -- calls the static `ApplyItemStatModifier`; keeps ignoring the return (behavior unchanged).
- `godot/src/Combat/CombatEventQueue.cs` -- `OrderDenied` event type; `Count`/`Get(i)` read surface for the denial-cue assertion.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` -- `Harness`/`Build`/`MintHero`/`Pickup` helpers to reuse; add the two new `[Fact]`s.

## Tasks & Acceptance

**Execution:**
- `godot/src/Effects/ModifierStore.cs` -- change `Apply` to return `bool` per the contract above; XML-doc the return value. -- gives the pickup site the accept/refuse signal DW-34 needs.
- `godot/src/Combat/ItemSystem.cs` -- propagate the `bool` through `ApplyItemStatModifier`/`ApplyStatModifierIfAny`; in `ResolvePickup`, roll back the tentative claim and deny (`OrderDenied` + `EndPickupOrder`, item left on ground) when a stat item's modifier is refused. -- closes DW-34: no silently-inert item at the cap.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` -- add `Pickup_WhenHeroAtModifierCap_Denied_ItemStaysOnGround`: fill the hero's ring with `MaxModifiersPerEntity` unique-id modifiers, order a ground stat item onto it, tick, assert item stays on ground + inventory slot empty + `EffectiveMaxHealth` unchanged + order Idle + an `OrderDenied` event (wire a real `CombatEventQueue` into this case). Add `Pickup_ItemOutsideRadius_SteersThenClaimsOnArrival`: hero at origin, ground item well outside `PickupRadius`; tick → assert `MoveTarget` == item pos, `Moving` set, order still `PickupItem`, item not held; move the hero onto the item, tick → assert claimed + stat modifier materialized + order Idle. -- covers DW-34's denial and DW-39's steering branch.

**Acceptance Criteria:**
- Given a hero already holding `MaxModifiersPerEntity` modifiers and a free inventory slot, when it is ordered onto a ground stat item and the system ticks, then the pickup is denied — the item stays on the ground, the inventory slot is left empty, no stat bonus is applied, an `OrderDenied` event fires, and the order ends Idle (no silently-inert claim).
- Given a hero below the modifier cap, when it picks up a stat item, then it claims and the modifier materializes exactly as before (no regression to the happy path), and a non-stat item at the cap still claims normally.
- Given a ground item farther than `PickupRadius`, when the hero is ordered onto it, then on the first tick it writes `MoveTarget` to the item position and sets the `Moving` flag without claiming, and once it reaches the item it claims into the first free slot with the stat modifier applied.
- Given the whole change, when `dotnet build` and `dotnet test godot/ProjectChimera.Sim.Tests` run, then the build is clean (no CHM/banned-float violations) and all tests pass — including every re-run golden being byte-identical (no hash/golden movement), apart from the pre-existing unrelated `ProceduralMapGeneratorTests` cross-platform tripwire.

## Design Notes

**Why apply-then-rollback, not pre-check.** The intent says "check `Apply`'s return value", so the deny decision uses `Apply`'s ACTUAL accept/refuse result rather than a duplicated cap predicate that could drift from `Apply`'s stacking logic. The rollback is three writes mirroring the three claim writes and touches no folded state, so a denied pickup is state-identical to never having attempted it. In `ResolvePickup` the entity is a live linked hero (guarded in `Tick`), so a `false` return there can only mean "ring full" — exactly the DW-34 case.

**DW-39 models movement by hand.** `ItemSystem` only WRITES `MoveTarget`; `MovementSystem` (which runs earlier in the tick order) does the moving. A Godot-free Tier-1 test has no movement system, so the test asserts the steering output on the far tick, then sets `world.Position[e]` to the item to model arrival and ticks again to assert the claim — the same pattern the existing drop/death tests use to reposition entities directly.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean, no CHM* determinism-analyzer or banned-float violations.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green (the two new `ItemSystemTests` facts pass), only the pre-existing unrelated `ProceduralMapGeneratorTests` WSL/Windows tripwire allowed to fail.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then a clean `dotnet test` -- expected: NO golden file changes (this diff folds no new state); goldens byte-identical across two runs.

## Review Triage Log

### 2026-07-27 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 2: (high 0, medium 2, low 0)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[low]` `[patch]` **Deny test did not assert `ItemPickedUp` is absent (Verification-Gap).** `Pickup_WhenHeroAtModifierCap_Denied_ItemStaysOnGround` confirmed `OrderDenied` fired but never checked the pickup cue, so a regression that dropped the early `return` on the deny/rollback path (emitting a double cue) would pass green. Strengthened the event scan to also assert no `CombatEventType.ItemPickedUp` was pushed.
- deferred (2 — NOT written to `deferred-work.md`: the orchestrator invocation reserved ledger writes ("Do NOT edit the deferred-work ledger; the orchestrator records resolution"), so these are recorded here for orchestrator ingestion):
  - `[medium]` **Buy path (`ItemSystem.GrantPurchasedItem` → `BuildingSystem.BuyItemCommand`) still seats a silently-inert stat item at the modifier cap and spends the currency with no refund/deny.** Pre-existing and *byte-identical before and after this change* (it discarded the previously-`void` `Apply` and still ignores the new `bool`), on the Story 3.16 shop surface the intent does not cover (intent scopes DW-34 to *pickup*: "make item pickup robust", "leave the item on the ground"). Same defect class as DW-34, different surface. Fix would mirror the pickup rollback: on `false`, `GrantPurchasedItem` returns -1 so `BuyItemCommand` refunds. All four review layers surfaced it.
  - `[medium]` **Persisted re-mint (`HeroProfileLoader.ReMintInventory:233`) discards the same `Apply` refusal**, so a saved hero whose carried stat items plus growth/passive modifiers exceed `MaxModifiersPerEntity` reloads weaker than saved (feeding `StartStateHash`) with no diagnostic. Pre-existing, persistence surface, unnamed by the intent. Same class as above.
- rejected (5): (1) the `Apply` `bool` conflates dead-target with ring-full and the pickup site labels all `false` as "cap" — unreachable today (`Tick` guards a live linked hero via `IsLiveLinkedHero` + `TryResolveRef`), speculative-future only; (2) the shared-apply cap contract is enforced by an XML doc-comment rather than a type — design speculation, no current defect, an enum/policy type is scope the intent excludes; (3) capped-pickup outcome changed without golden verification — disproven: `CHIMERA_GOLDEN_RECORD` re-record produced zero golden changes and the full suite is green, so no existing golden exercised that scenario; (4) `OrderDenied` dropped on a null event queue — the `_events?.Push` null-sink is the intentional presentation pattern (identical to the full-inventory deny at `:171`; events are presentation-only, the deterministic reject is the folded state); (5) DW-39's steering test models arrival by hand rather than driving `MovementSystem` — DW-39 scopes the coverage to the *steering branch* (Tier-1 testable, and the test asserts `MoveTarget`==item pos + claim on arrival); full movement-pipeline integration is the separate "consider" the intent excludes, and hand-repositioning is the established Tier-1 idiom (drop/death tests do the same).

## Auto Run Result

Status: done
Blocking condition: none

### Summary

Closed two Story 3.15 deferred-work items as a hardening bundle. **DW-34:** `ModifierStore.Apply` gained a `bool` return (`true` = installed or an existing same-id instance handled; `false` = refused because the target is dead/stale or the per-entity ring is full) — a source-compatible `void→bool` change that folds no state and leaves every existing caller byte-identical. `ItemSystem.ApplyItemStatModifier`/`ApplyStatModifierIfAny` now relay that result, and the ground-pickup claim site (`ResolvePickup`) rolls back its three tentative claim writes (`Held`→false, `CarrierHeroSlot`→`NO_CARRIER`, inventory slot→`INVENTORY_EMPTY`) and denies with an `OrderDenied` cue when a capped hero's stat-item modifier is refused — so a hero at `MaxModifiersPerEntity` no longer consumes an item into a silently-inert slot; the item stays on the ground. **DW-39:** added `ItemSystemTests` coverage for the previously-untested `sqrDist > rr → write MoveTarget → return` steering branch (a ground item outside `PickupRadius` steers then claims on arrival), plus the cap-denial oracle and a non-stat-at-cap regression guard.

### Files changed

- `godot/src/Effects/ModifierStore.cs` — `Apply` `void`→`bool` per the contract above; XML-doc'd the return; every path's behavior/state unchanged.
- `godot/src/Combat/ItemSystem.cs` — threaded the `bool` through `ApplyItemStatModifier` (static) and `ApplyStatModifierIfAny`; tentative-claim-then-rollback + `OrderDenied` deny in `ResolvePickup` when a stat item's modifier is refused at the cap.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` — three new facts: `Pickup_WhenHeroAtModifierCap_Denied_ItemStaysOnGround` (+ a direct `Apply`-at-cap `false` assert and a no-`ItemPickedUp`-cue assert), `Pickup_ItemOutsideRadius_SteersThenClaimsOnArrival` (DW-39 steering branch), `Pickup_NonStatItemAtModifierCap_ClaimsNormally` (regression guard: the cap-deny gates on `HasStatModifier`).

### Review findings

Four parallel Opus-4.8 layers (Blind Hunter / adversarial, Edge Case Hunter, Verification-Gap, Intent-Alignment). Triage: **1 patch (low), 2 defer (medium), 5 reject, 0 intent_gap, 0 bad_spec.** The patch strengthened the deny test to assert no `ItemPickedUp` cue. The two defers (buy-path + re-mint inert-item-at-cap) are the same defect *class* as DW-34 on surfaces the intent does not cover, pre-existing and byte-identical before/after this change — recorded above for orchestrator ledger ingestion (not written to `deferred-work.md` per the invocation's reservation). See the Review Triage Log for the full rejection rationale.

### Follow-up review recommendation

`false`. This pass patched 1 finding, severity low (0 high, 0 medium, 1 low): no high patch, and `3×0 + 1×1 = 1 < 5`.

### Verification

- `dotnet build godot/godot.sln` — clean, 0 errors, 0 warnings; no CHM/banned-float violations in the touched sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter ItemSystemTests` — 16/16 pass (13 original + 3 new).
- `dotnet test godot/ProjectChimera.Sim.Tests` (full) — green across two confirming runs (3520 passed, 1 skipped, 0 failed). A single unrelated failure in one earlier run did not reproduce across the two subsequent full runs — a documented CPU-contention/GC benchmark flake, not a regression (this change is control-flow + a return value + test-only).
- `CHIMERA_GOLDEN_RECORD=1` re-record then clean `git status` — zero golden file changes, confirming the diff folds no new state (no `SimChecksum`/`StartStateHash` `AlgoVersion` bump, no golden re-baseline).
- Matrix Test Audit: all 5 I/O-matrix rows covered by executed, passing tests.

### Residual risks

- The two deferred buy/re-mint inert-item-at-cap paths (medium; degenerate — require a hero at the 8-modifier cap) remain live by design, out of this bundle's pickup scope.
- Residual working-tree artifact unrelated to this change: `_bmad-output/brainstorming/brainstorm-scenario-maps-2026-07-27/.memlog.md` (a brainstorming log, untouched by this run — left in place, not committed).
- The pickup UI/presentation surface (the `OrderDenied` cue actually rendering in-engine) is verified by clean compile + oracle, not a live session — consistent with the Story 3.15 residual-risk posture.
