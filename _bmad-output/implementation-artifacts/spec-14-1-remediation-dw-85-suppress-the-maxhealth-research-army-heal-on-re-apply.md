---
title: 'Remediation DW-85: suppress the +MaxHealth research army-heal on re-apply'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '7162400'
final_revision: '4dce63b'
context:
  - '{project-root}/_bmad-output/project-context.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** Completing a research level whose cumulative `max_health_delta` is positive burst-heals **every currently-alive faction unit** — by the FULL cumulative max-health bonus, on **every** completion, not just the level's increment. `ResearchSystem.ApplyCumulativeModifier` must remove-then-reapply the single `StackRule.Refresh` cumulative slot to carry the grown magnitude, and `ModifierStore.ApplyStatDeltas` heals current Health on any positive-MaxHealth apply (Decision #3). So a damaged unit at 10/150 that completes a second +50-max level jumps to 110/200 (healed the full +100 cumulative), and even a non-max-health level re-heals by the prior cumulative. This turns a repeatable +HP research into a repeatable army-heal. (DW-85.)

**Approach:** In the research (re)application path, raise the MaxHealth **ceiling** but keep current Health invariant — snapshot `world.Health[id]` before the remove-then-reapply and restore it after (re-clamped to the freshly-raised `EffectiveMaxHealth`), suppressing the heal. This is scoped to the living-army completion path only; the future-spawn catch-up path keeps healing so a newly trained unit still spawns at full upgraded HP.

## Boundaries & Constraints

**Always:**
- The suppression lives in `ResearchSystem` only; `ModifierStore.ApplyStatDeltas`' shared Decision-#3 heal-on-apply (used by items and hero growth) is **unchanged**.
- Determinism preserved: `Fixed` math only, no `float`/`Fixed.FromFloat`/`System.Random`/`DateTime`; ascending-id iteration order untouched.
- Current Health is monotonic-safe: research max-health delta grows the ceiling, so the restored pre-apply Health is always ≤ the new `EffectiveMaxHealth` (clamp is defensive, never a phantom-HP grant).
- Armor/AttackDamage/MoveSpeed-only research completions remain byte-identical in `SimChecksum` (their apply carries `maxHealthChange == 0`, so restore is a no-op) — no golden re-baseline.

**Block If:**
- A single defensible reading requires the future-spawn catch-up path to ALSO stop healing (would make freshly trained units spawn visibly damaged) — the intent says "army heal", not spawn heal; do not silently flip it.

**Never:**
- Do not change `ModifierStore.Apply`/`ApplyStatDeltas`/`RemoveByModifierId` signatures or heal semantics.
- Do not implement "heal-by-increment" (re-add only the level's delta) — the intent selects **suppress**, not partial heal.
- Do not touch `SupplySystem`, combat, or the checksum fold.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Damaged unit, +MaxHealth completes | Alive unit at 10/100 base; research `hp_up` (+50 max/level) level-0 completes | `EffectiveMaxHealth`→150, `Health` stays 10 (NOT healed) | n/a |
| Repeatable +MaxHealth re-apply | Same unit at 10/150 after level 0; level-1 (+50) completes | `EffectiveMaxHealth`→200, `Health` stays 10 (NOT +100 healed) | n/a |
| Full-health unit, +MaxHealth completes | Alive unit at 100/100; `hp_up` level-0 completes | `EffectiveMaxHealth`→150, `Health` stays 100 (now 100/150, not topped to 150) | n/a |
| Future-spawn catch-up unchanged | `hp_up` level-0 completed; new unit spawned at 100/100, `ApplyCompletedResearch` called | `EffectiveMaxHealth`→150 AND `Health`→150 (full upgraded HP preserved) | n/a |
| Armor-only completion (regression) | Alive unit, `armor_up` (+2 armor) completes | `EffectiveArmor`→2, `Health` unchanged; checksum unchanged | n/a |

</intent-contract>

## Code Map

- `godot/src/Economy/ResearchSystem.cs` -- `ApplyCumulativeModifier` (remove-then-reapply, ~:335); callers `CompleteResearch` (living-army loop ~:314) and `ApplyCompletedResearch` (future-spawn catch-up ~:384). The fix site.
- `godot/src/Effects/ModifierStore.cs` -- `ApplyStatDeltas` (~:456, Decision-#3 heal-on-apply) and `RemoveByModifierId`/`Apply`. **Read-only reference** — do not modify.
- `godot/src/Core/EntityWorld.cs` -- `Health[]` / `EffectiveMaxHealth[]` (public `Fixed[]`, ascending-id SoA). Read/write `Health`, read fresh `EffectiveMaxHealth` after Apply.
- `godot/ProjectChimera.Sim.Tests/Economy/ResearchSystemTests.cs` -- Godot-free oracle harness; add a `hp_up` research entry (new index 4) + the new heal-suppression tests here.

## Tasks & Acceptance

**Execution:**
- `godot/src/Economy/ResearchSystem.cs` -- Thread `EntityWorld world` and a `bool preserveCurrentHealth` param into `ApplyCumulativeModifier`. Snapshot `Fixed healthBefore = world.Health[id]` before `RemoveByModifierId`+`Apply`; when `preserveCurrentHealth`, after the apply set `world.Health[id] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[id])`. Call site in `CompleteResearch` passes `preserveCurrentHealth: true`; the `ApplyCompletedResearch` call site passes `false`. Update the method's doc comment to record the DW-85 suppression rationale.
- `godot/ProjectChimera.Sim.Tests/Economy/ResearchSystemTests.cs` -- Add an `HpUpIdx = 4` `hp_up` research (2 levels, each `MaxHealthDelta = 50f`) to the `Build()` harness faction and its lab `AvailableResearch`. Add tests for the Matrix rows: (1) damaged-unit single completion suppresses heal, (2) repeatable re-apply suppresses the full-cumulative heal, (3) full-health unit is not topped up, (4) future-spawn catch-up STILL heals to full upgraded HP, (5) armor-only completion leaves Health untouched (regression guard).

**Acceptance Criteria:**
- Given an alive, damaged faction unit and a completed positive-`max_health_delta` research level, when a further level of that (or any) research completes, then the unit's `EffectiveMaxHealth` rises by the cumulative delta and its current `Health` is unchanged (no burst-heal).
- Given a completed +MaxHealth research and a newly spawned faction unit, when `ApplyCompletedResearch` runs, then the new unit has both the raised `EffectiveMaxHealth` and full current `Health` (catch-up heal preserved).
- Given research that carries no max-health delta (armor/attack/speed), when it completes, then affected units' `Health` and the golden `SimChecksum` are byte-identical to before this change.

## Design Notes

Why snapshot/restore rather than passing a "don't heal" flag into `ModifierStore.Apply`: the remove step (`RemoveByModifierId`→`RemoveSlot`→`ApplyStatDeltas isApply:false`) clamps Health DOWN by the OLD cumulative before the new one is applied. Suppressing only the apply-heal would make a full-health unit LOSE the old delta's HP on every completion. Snapshotting current Health across the whole remove+reapply and restoring it preserves current HP exactly while the ceiling grows — the correct "suppress the heal" semantics, and it keeps `ModifierStore` shared behavior untouched.

Sketch:

```csharp
private void ApplyCumulativeModifier(EntityWorld world, int id, Faction faction, int f, int researchIndex, bool preserveCurrentHealth)
{
    int modId = ResearchModifierId(researchIndex);
    Fixed healthBefore = world.Health[id];        // DW-85: snapshot to suppress the remove-then-reapply burst-heal
    _modifiers.RemoveByModifierId(id, modId);     // revert the stale (smaller) delta, if any
    _modifiers.Apply(id, BuildCumulativeModifier(f, researchIndex), casterId: id, casterFaction: faction);
    if (preserveCurrentHealth)                    // living-army completion only; future-spawn catch-up keeps its heal
        world.Health[id] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[id]);
}
```

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~ResearchSystemTests"` -- expected: all pass, including the 5 new heal-suppression cases.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~Golden"` -- expected: all golden/checksum tests still green with no re-baseline (armor/attack research checksums unchanged).
- `dotnet build godot/godot.sln` -- expected: builds clean (no banned-API analyzer violation in sim code).

## Review Triage Log

### 2026-07-15 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 1
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` The negative-`MaxHealthDelta` down-clamp branch of the new preserve-health restore was unexercised (all positive-delta tests leave it a pass-through). Added `hp_down` (-50 max) research + `Complete_HpDown_FullHealthUnit_ClampsHealthDownToReducedCeiling` asserting Health clamps 100→50 to the reduced ceiling (pins that the restore respects `EffectiveMaxHealth`, not a bare `= healthBefore`).
  - `[low]` `[patch]` The new `world.Health[id]` write was unconditional; added a `world.IsAlive(id)` guard mirroring `ModifierStore`'s post-effect IsAlive convention (defensive against a future lethal research period/expire effect recycling the host mid-apply).

Rejected (noise / intended / pre-existing-accepted): `preserveCurrentHealth`-boolean fragility (both call sites correct + independently pinned); slot-ring-full first-apply drop (pre-existing accepted deterministic-refuse); absolute-vs-percentage HP preservation and revive-laundering (intent-sanctioned design, confirmed by the intent-alignment auditor); future-spawn "spawn Health == base MaxHealth" invariant (unchanged by this diff); EC "negative Health via clamp inversion" (impossible — `EffectiveMaxHealth` floored at 0 by `ModifierSystem.RecomputeEntity`, and `Fixed.Clamp` min=0 floors the result); EC "future-spawn Health exceeds max" (false — `ModifierStore.ApplyStatDeltas` already down-clamps on any `maxHealthChange != 0`); doc/style nits. Deferred: the shared net-negative-MaxHealth 0-HP-alive invariant (pre-existing across all modifier producers).

## Auto Run Result

Status: done
Intent: DW-85 remediation — suppress the +MaxHealth research army-heal on re-apply.

### Implemented change
Research completions raise the MaxHealth **ceiling** on already-alive faction units without burst-healing their current Health. `ResearchSystem.ApplyCumulativeModifier` now snapshots `world.Health[id]` before the mandatory remove-then-reapply of the single cumulative modifier slot and, on the living-army path, restores it (re-clamped into the freshly raised `EffectiveMaxHealth`). The future-spawn catch-up path keeps healing, so a newly trained unit still spawns at full upgraded HP. `ModifierStore`'s shared Decision-#3 heal-on-apply (used by items/hero growth) is untouched.

### Files changed
- `godot/src/Economy/ResearchSystem.cs` — Threaded `EntityWorld world` + `bool preserveCurrentHealth` into `ApplyCumulativeModifier`; snapshot/restore Health guarded by `preserveCurrentHealth && world.IsAlive(id)`; `CompleteResearch` passes `true`, `ApplyCompletedResearch` passes `false`. DW-85 rationale in the doc comment.
- `godot/ProjectChimera.Sim.Tests/Economy/ResearchSystemTests.cs` — Added `hp_up` (+50) and `hp_down` (-50) research to the harness; 6 tests: damaged single completion, repeatable no-full-re-heal, full-health not-topped-up, future-spawn still heals, armor-only no-op regression, and the negative-delta down-clamp.

### Review findings breakdown
- Patches applied: 2 (1 medium — negative-delta down-clamp test coverage; 1 low — `IsAlive` guard on the new Health write).
- Deferred: 1 (pre-existing shared net-negative-MaxHealth 0-HP-alive invariant).
- Rejected: 9 (intent-sanctioned design consequences, pre-existing accepted behavior, and two demonstrably-impossible/false edge-case claims).

### Verification
- `dotnet test ... --filter "FullyQualifiedName~ResearchSystemTests"` — Passed 32/32.
- `dotnet test ... --filter "FullyQualifiedName~Golden"` — Passed 155/155 (no re-baseline; armor/attack/speed research checksums byte-identical).
- `dotnet build godot/godot.sln` — Build succeeded, 0 errors (11 pre-existing nullable warnings unrelated to this change).

### Residual risks
Low. The living-army-vs-future-spawn split is enforced solely by the `preserveCurrentHealth` argument at the two call sites; a future third caller must consciously choose it. Net-negative-MaxHealth research is now down-clamp-tested but the broader "modifier drives EffectiveMaxHealth to 0 without raising death" question is deferred (pre-existing, shared, content-gated).
