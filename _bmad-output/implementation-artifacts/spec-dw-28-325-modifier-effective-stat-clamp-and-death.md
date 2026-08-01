---
title: 'Modifier effective-stat saturating clamp (DW-28) + modifier-driven ceiling-collapse death (DW-325)'
type: 'bugfix'
created: '2026-08-01'
status: 'done'
baseline_revision: '181f76be3b0667b68e81019fac5235bdc85f20d5'
final_revision: '737ea7622a582f946468d4d6e35d70429de56aea'
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/godot/CLAUDE.md']
warnings: []
---

<intent-contract>

## Intent

**Problem:** Two pre-existing, content-gated defects in the effective-stat recompute. (DW-28) `ModifierSystem.RecomputeEntity` sums `Base + Σ bonus` with `Fixed operator+`, which is plain unchecked int addition on the 16.16 `Raw` — a pathological-large base plus a large modifier stack overflows/wraps the int, silently corrupting the effective stat (a huge buff can wrap negative and collapse to the Zero-floor). (DW-325) When a net-negative-MaxHealth modifier drives `EffectiveMaxHealth` to 0, `ModifierStore.ApplyStatDeltas` clamps current Health to 0 but no system raises death — leaving a 0-HP-alive "zombie".

**Approach:** (DW-28) Add a new saturating add to `Fixed` that computes in a widened 64-bit accumulator and clamps to `[MinValue, MaxValue]` instead of wrapping; use it for all four stats in `RecomputeEntity`. (DW-325, per the recorded 2026-07-30 decision "Raise death on ceiling==0") in `ModifierStore.ApplyStatDeltas`, after the Health clamp, if a modifier drove `EffectiveMaxHealth` to exactly 0, kill the host through the single existing `DamageResolver.KillEntity` death sequence, and add `IsAlive` re-checks at every `ApplyStatDeltas` caller (mirroring the store's existing post-`RunEffect` guards).

## Boundaries & Constraints

**Always:**
- Keep `Fixed operator+`/`operator-` WRAPPING — the new saturating add is a SEPARATE method; `FixedBoundaryTests` deliberately pins the wrap and must stay green.
- Sim purity: no `using Godot;`, no `float`/`FromFloat`, ascending-id iteration, integer-only saturation math.
- Raise the ceiling-collapse death ONLY through `DamageResolver.KillEntity` (the single UnitKilled-event + `Destroy` path combat uses) — never an invented death path.
- Gate the death on `maxHealthChange.Raw != 0 && EffectiveMaxHealth == Fixed.Zero` so it fires only on a genuine modifier-driven ceiling collapse, and only while the host `IsAlive`.
- After every `ApplyStatDeltas` call, re-check `IsAlive` before touching that entity's slots/status (the kill fires `OnDestroy`→`ClearEntity`, wiping this host's slots + accumulators).

**Block If:**
- The suite reveals a SHIPPED content definition that authors a net-negative-MaxHealth modifier (would move a golden and change live behavior — a design decision, not a bug fix).

**Never:**
- Do NOT change `operator+` semantics, re-baseline any golden, or fold new state into `SimChecksum` (Health/Alive are already folded; the change is content-gated so no golden moves).
- Do NOT credit a kill or XP bounty to any faction for a ceiling-collapse death (killer = `Faction.Neutral`, `deaths` feed omitted): there is no attacker.
- Do NOT add a saturating variant of subtraction or any other operator — only the add the recompute needs.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Normal recompute | Base + bonus within int range | Identical raw to today's `operator+` (no golden moves) | n/a |
| Overflow buff | Base ≈ MaxValue, large +bonus | Effective saturates at `Fixed.MaxValue`, never wraps negative | Saturating clamp |
| Ceiling collapse on apply | −MaxHealth debuff drives `EffectiveMaxHealth` to 0 on a live unit | Unit dies via `KillEntity` → `!IsAlive`, `CountAt==0`; victim loss recorded, no kill credited | Death raised once |
| Non-lethal debuff | −MaxHealth debuff leaves `EffectiveMaxHealth > 0` | Health clamps down, unit stays alive (unchanged) | No death |
| Ceiling collapse mid-apply | fresh install / stack re-add kills host | `Apply` returns without writing status flags to the dead slot | `IsAlive` guard |

</intent-contract>

## Code Map

- `godot/src/Core/FixedPoint.cs` -- `Fixed` 16.16 struct; `operator+` is unchecked int add (the DW-28 wrap). Add `AddSaturating` beside `Max`/`Min`/`Clamp`.
- `godot/src/Effects/ModifierSystem.cs` -- `RecomputeEntity` computes `Effective = Max(Zero, Base + bonus)` for the 4 stats (DW-28 fix site).
- `godot/src/Effects/ModifierStore.cs` -- `ApplyStatDeltas` (the single MaxHealth-clamp funnel; DW-325 death site) and its 3 callers `Apply` (fresh install + stack) / `RemoveSlot` (add `IsAlive` guards). Already imports `ProjectChimera.Combat` (`DamageResolver`) and holds `_events`/`_stats`.
- `godot/src/Combat/DamageResolver.cs` -- `KillEntity(world, id, killer, events, stats, deaths=null, attackerId=-1)`: the reused single death sequence.
- `godot/src/Economy/ResearchSystem.cs` -- `ApplyCumulativeModifier` already `IsAlive`-guards after `Apply` (positive-only, never triggers the collapse) — verify unchanged.
- `godot/ProjectChimera.Sim.Tests/Determinism/FixedBoundaryTests.cs` -- overflow tests pin the wrap; ADD saturating-add tests here.
- `godot/ProjectChimera.Sim.Tests/Effects/ModifierStoreApplyTests.cs` -- `Wire()` harness + MaxHealth semantics; ADD the ceiling-collapse death tests here.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/FixedPoint.cs` -- add `public static Fixed AddSaturating(Fixed a, Fixed b)` computing `(long)a.Raw + b.Raw` clamped to `[int.MinValue, int.MaxValue]` before constructing the `Fixed` -- deterministic, no wrap.
- `godot/src/Effects/ModifierSystem.cs` -- in `RecomputeEntity`, replace each `Base + bonus` with `Fixed.AddSaturating(Base, bonus)` inside the existing `Fixed.Max(Fixed.Zero, …)` for all four stats -- closes DW-28 without touching realistic-value raws.
- `godot/src/Effects/ModifierStore.cs` -- in `ApplyStatDeltas`, after the Health clamp, add: if `_world.IsAlive(id) && _world.EffectiveMaxHealth[id] == Fixed.Zero` then `DamageResolver.KillEntity(_world, id, Faction.Neutral, _events, _stats)`. In `Apply` (both the fresh-install and `StackRule.Stack` branches) and in `RemoveSlot`, add `if (!_world.IsAlive(...)) return …;` immediately after the `ApplyStatDeltas` call, before any further slot/status write -- DW-325 + re-entrancy safety.
- `godot/ProjectChimera.Sim.Tests/Determinism/FixedBoundaryTests.cs` -- add tests: `AddSaturating` at the positive limit saturates to `MaxValue` (not wrap-negative), at the negative limit to `MinValue`, and equals `operator+` for in-range operands (independently-derived raws).
- `godot/ProjectChimera.Sim.Tests/Effects/ModifierStoreApplyTests.cs` -- add tests for the I/O Matrix death rows: a −MaxHealth debuff that zeroes the ceiling kills the host (`!IsAlive`, `CountAt==0`); a debuff leaving ceiling > 0 does not; assert the victim's loss is recorded and no kill credited when wired with `MatchStats`.

**Acceptance Criteria:**
- Given a unit whose `BaseAttackDamage` is near `Fixed.MaxValue`, when a large +damage modifier is applied, then `EffectiveAttackDamage` saturates at `Fixed.MaxValue` and never wraps to a negative/near-zero value.
- Given a live unit at `EffectiveMaxHealth > 0`, when a modifier drives `EffectiveMaxHealth` to 0, then the unit is no longer alive that same apply, dies through `DamageResolver.KillEntity`, and its modifier slots are cleared.
- Given the ceiling-collapse death, when it is raised, then no faction is credited a kill (killer Neutral) though the victim's loss is counted, and the full sim suite shows no moved golden.

## Review Triage Log

### 2026-08-01 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 3, low 3)
- defer: 3: (high 0, medium 1, low 2)
- reject: 4
- addressed_findings:
  - `[medium]` `[patch]` Heal-on-apply `_world.Health[id] += maxHealthChange` still used the wrapping `operator+`; with saturated ceilings a large/stacked +MaxHealth heal could wrap Health negative → clamp to 0 → a live 0-HP zombie with a non-zero ceiling (the exact state DW-325 removes). Switched to `Fixed.AddSaturating`.
  - `[medium]` `[patch]` No test drove the REMOVAL/expiry ceiling-collapse death (`RemoveSlot`→`ApplyStatDeltas(isApply:false)`) or its `!IsAlive` guard, which prevents a `CompactSlot`-on-`_count==0` slot corruption. Added `RemovalDrivenCeilingCollapse_KillsHostDuringRemove_SecondUnitSurvives` (also pins the mid-`Advance` `n=_count[i]` re-read).
  - `[medium]` `[patch]` No test drove `AddSaturating` THROUGH `RecomputeEntity` (only in isolation). Added `RecomputePipeline_SaturatesMaxHealth_InsteadOfWrappingToZero` (base 30000 + 10000 modifier → `EffectiveMaxHealth.Raw == int.MaxValue`), proving the four recompute swaps are load-bearing.
  - `[low]` `[patch]` No test covered the Stack-branch `!IsAlive` guard. Added `StackBranch_CollapsingStackKillsHost_NoThrow`.
  - `[low]` `[patch]` The wired `CombatEventQueue` was never asserted. Extended `CeilingCollapseDeath_CountsVictimLoss_ButCreditsNoKill` to assert exactly one `UnitKilled` event for the victim.
  - `[low]` `[patch]` `AddSaturating` docstring overclaimed "a large modifier stack SATURATE" (it only saturates the single `Base + already-summed bonus` read; the accumulator `+=` is unsaturated) and the DW-325 comment said "EXACTLY 0" (really any ≤0 computed ceiling floored to 0). Both corrected.

Defers (canonical DW-format captured here — NOT written to `deferred-work.md`, per the invocation directive "Do NOT edit the deferred-work ledger; the orchestrator records resolution"; orchestrator/operator to transcribe as DW-488/489/490 or next free ids):

#### DW-488: `ModifierSystem.AccumulateBonus`'s `+=` accumulator stays unsaturated after the DW-28 recompute clamp — a large POSITIVE +MaxHealth stack that wraps the accumulator negative now KILLS the unit via DW-325 instead of leaving a zombie
- origin: deferred by review of `spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`, 2026-08-01
- source_spec: `_bmad-output/implementation-artifacts/spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`
- location: godot/src/Effects/ModifierSystem.cs:150-154 (AccumulateBonus `_flat*Bonus[id] += delta`, wrapping)
- severity: medium
- reason: DW-28 saturated the `Base + bonus` READ in `RecomputeEntity` but not the per-modifier accumulator write, so a stack of large +MaxHealth modifiers can wrap `_flatMaxHealthBonus` negative BEFORE the read; `AddSaturating(Base, wrappedNegative)` then saturates to `MinValue` → `Max(0,…)==0` → the new DW-325 trigger KILLS the over-buffed unit (previously a 0-ceiling zombie). — Evidence: adversarial + intent-alignment lenses; `AddSaturating`'s own doc concedes "a widen-then-clamp cannot recover a value that has ALREADY wrapped in the int add." Not closable by saturating the accumulator itself — per-step saturation would break `AccumulateBonus`'s AC2 order-independence invariant at the saturation boundary, which is why DW-28's intent offered "or a Base+modifier authoring bound" as the alternative. Full closure = a content-authoring cap on `MaxStacks × delta` magnitude (a validator rule). Content-gated + requires an already-extreme base; deterministic (no desync).
- status: open

#### DW-489: `ModifierStore.Apply`/`RemoveByModifierId` gained a "target may be destroyed on return" post-condition (DW-325); external callers were not audited and cannot distinguish "installed & alive" from "installed & host died"
- origin: deferred by review of `spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`, 2026-08-01
- source_spec: `_bmad-output/implementation-artifacts/spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`
- location: godot/src/Combat/ItemSystem.cs:349 (ApplyItemStatModifier returns Apply's bool as claim-success); godot/src/Effects/EffectExecutor.cs:136 + ApplyModifierEffect.cs:31 (mid-graph Apply)
- severity: low
- reason: The DW-325 kill makes `Apply` able to `Destroy`+recycle its target, a post-condition it never had; only the 3 INTERNAL `ApplyStatDeltas` callers were guarded. `ItemSystem.ApplyItemStatModifier` returns `Apply`'s `true` ("installed") straight through as claim-success, so a future creator-authored net-negative-MaxHealth ("cursed") item would equip onto a corpse whose inventory `OnDestroy` already dropped, and the DW-34 pickup site would consume a ground item for a unit the claim itself killed. — Evidence: adversarial lens; `ApplyItemStatModifier` verified to return the raw `Apply` bool with no post-`IsAlive` check. Content-gated (Block-If confirmed only `aura_guard.json`=0 and `ring_of_vigor.json`=+50 author MaxHealth). Mid-graph case partly pre-existing (a lethal DoT `InitialEffect` could already `Destroy` mid-graph; effect leaves already guard `IsAlive`). Closure = document the post-condition + audit/guard external `Apply` callers (or a tri-state result).
- status: open

#### DW-490: A modifier-driven ceiling-collapse death is hardcoded to killer `Faction.Neutral` — a future ability-driven "reduce max HP to 0" finisher would grant its caster no kill credit or hero XP
- origin: deferred by review of `spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`, 2026-08-01
- source_spec: `_bmad-output/implementation-artifacts/spec-dw-28-325-modifier-effective-stat-clamp-and-death.md`
- location: godot/src/Effects/ModifierStore.cs (ApplyStatDeltas DW-325 kill: `DamageResolver.KillEntity(_world, id, Faction.Neutral, _events, _stats)`)
- severity: low
- reason: The ceiling-collapse death is attributed to no faction (killer Neutral, `deaths` feed omitted) — a deliberate spec choice for an attacker-less rules death, but a creator CAN author a lethal −MaxHealth debuff whose `casterFaction` is a real player; such an ability-driven kill would then be invisible to scoring and hero XP, unlike every other lethal path. — Evidence: adversarial lens; `_casterFaction[slot]` is available at `Apply` but not threaded into `ApplyStatDeltas`. Content-gated (no such ability today); a design question deferred until ability-driven max-health-collapse finishers are authored. Closure = thread caster attribution into the death (and decide XP-bounty policy).
- status: open

### 2026-08-01 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 6: (high 0, medium 2, low 4)
- reject: 4
- addressed_findings:
  - `[medium]` `[patch]` The DW-28 heal-path saturation (`ModifierStore.ApplyStatDeltas`: `_world.Health[id] = Fixed.AddSaturating(_world.Health[id], maxHealthChange)`) had NO test — reverting it to `+=` left the full suite green (verification-gap lens). Added `MaxHealthBuff_HealNearMaxValue_SaturatesHealth_InsteadOfWrappingToZombie` (Health near `Fixed.MaxValue` + a large +MaxHealth buff → `Health.Raw == int.MaxValue`, alive), which is RED under `+=`: the heal wraps Health negative → the `[0, ceiling]` clamp drops it to 0 → a live 0-HP zombie with a non-zero saturated ceiling (the exact DW-28 target).
  - `[low]` `[patch]` The fresh-install re-entrancy guard (`ModifierStore.cs:158`, `if (!IsAlive) return true;` before `StatusFlagsOf[id] |= mod.Status`) was unpinned — every ceiling-collapse test used `StatusFlags.None`, so deleting the guard changed nothing observable (`|= None` is a no-op) (verification-gap lens). Strengthened `MaxHealthDebuff_ZeroesCeiling_KillsHost_ClearsSlots` to carry `StatusFlags.Stunned` and assert `StatusFlagsOf[id] == None` after the kill (RED without the guard: `|= Stunned` stamps the recycled slot).
  - `[low]` `[patch]` The Stack-branch re-entrancy guard (`ModifierStore.cs:178`) was likewise unpinned. Strengthened `StackBranch_CollapsingStackKillsHost_NoThrow` with `StatusFlags.Rooted` + the same `== None` assertion (RED without the Stack-branch guard).

Defers (this pass, written to `deferred-work.md` per the invocation directive "append NEW entries only; do not modify existing"; highest prior id was DW-487):
- **DW-488** (medium): `ModifierSystem.AccumulateBonus`'s `+=` accumulator stays unsaturated after the DW-28 recompute clamp — a large POSITIVE +MaxHealth stack that wraps the accumulator negative now KILLS the over-buffed unit via the new DW-325 trigger. (This confirms and supersedes the DW-488 the prior pass reserved in-spec; re-found by adversarial + edge-case + intent-alignment lenses.)
- **DW-489** (medium): `Apply`/`RemoveByModifierId` gained a "target may be destroyed on return" post-condition; external callers (`ItemSystem.ApplyItemStatModifier`/`DropOne`/`UseItem`, `EffectExecutor`) unaudited → duplicate `ItemDropped` / item consumed for a dead carrier.
- **DW-490** (low): ceiling-collapse death hardcoded to killer `Faction.Neutral` with the DeathFeed omitted → a future ability-driven −MaxHealth finisher grants no kill credit / hero XP.
- **DW-491** (low): the DW-325 kill is gated on absolute `EffectiveMaxHealth == 0`, not a collapse transition → a legitimately-ceiling-0 live host, or a positive heal on one, is lethal.
- **DW-492** (low): the "no 0-HP zombie" invariant is enforced only inside `ApplyStatDeltas` — `RestoreSlot` (SP load) and the Tick catch-all recompute can still reconstitute a living 0-ceiling unit.
- **DW-493** (low): `DamageResolver.KillEntity` has no fail-closed `if (!IsAlive) return;` entry guard; the new lethal `ApplyStatDeltas` path's safety rests on a single inline call-site check.

Rejected (4): silent ~32767 saturation cap has no observability signal (saturating IS the intended DW-28 behavior; realistic stats are far below and a log write would break sim purity); a null-`_system`/`_events`/`_stats` fold-only store reading a stale ceiling (a live-apply store is always fully wired; fold-only stores never apply modifiers); the verbose DW-325 rationale comment / missing inline DW-id cite (accurate, low-value prose nit); the `RemovalDrivenCeilingCollapse` `survivor` assertions being tautological (real, but the `RemoveSlot` guard it targets is already independently pinned by the `CompactSlot` index `-1` throw at id 0, so the added value is marginal and the fix is non-trivial).

### 2026-08-01 — Review pass (follow-up 2)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 0
- reject: 12
- addressed_findings:
  - `[low]` `[patch]` Only the MaxHealth `AddSaturating` recompute swap was pinned at the pipeline level (`RecomputePipeline_SaturatesMaxHealth_…`); the other three swaps — `EffectiveAttackDamage` (`ModifierSystem.cs:90`), `EffectiveMoveSpeed` (`:92`), `EffectiveArmor` (`:94`) — could each be reverted to plain `operator+` with the full suite staying green (verification-gap lens; a genuine regression-gap introduced by this change's four-line swap). Added `RecomputePipeline_SaturatesAttackMoveArmor_InsteadOfWrappingToZero` (three distinct entities, each base 30000 + 10000 bonus → `Effective*.Raw == int.MaxValue`), RED if any of the three lines reverts to `+`.

Rejected (12): the recurring adversarial/edge-case findings all duplicate ALREADY-OPEN ledger entries and were NOT re-deferred (invocation directive: append NEW entries only, never re-open/rewrite existing) — the accumulator `+=` wrap now lethal via DW-325 (**DW-488**); `Apply`/`RemoveByModifierId` "may destroy host on return" post-condition unaudited in `ItemSystem`/`EffectExecutor` (**DW-489**); Neutral-killer death grants no kill/hero-XP credit (**DW-490**); the kill gated on absolute `EffectiveMaxHealth == 0` rather than a collapse transition, so a 0-base entity / positive heal on one is lethal (**DW-491**); `RestoreSlot` (SP load) + the Tick catch-all recompute can reconstitute a live 0-ceiling zombie (**DW-492**); `KillEntity` has no fail-closed entry guard (**DW-493**). Plus six genuine drops: the "no golden moves" claim as "unsupported" (demonstrated this pass — full 3733-test suite green, zero goldens moved); an epsilon `raw==1` ceiling leaving a ~0-HP live unit (BY DESIGN — the intent's I/O matrix keeps `EffectiveMaxHealth > 0` alive; only `== 0` collapses); win-condition logic reading a killer-less `Losses` (speculative, content-gated, pre-existing scoring path untouched here); the test comment "near Fixed.MaxValue (~32767)" as a raw/whole-unit "conflation" (the comment is accurate — `FromInt(30000)` IS near the ~32767 whole-unit ceiling a 16.16 `Fixed` holds); the saturate-HIGH direction for attack/move/armor being a behavior flip vs. the old wrap→0 (intent explicitly mandates "all four stats", and it needs an absurd near-`MaxValue` base — content-gated, same class as DW-488); and the `RemovalDrivenCeilingCollapse` decorative `survivor` asserts (already rejected in the prior pass; the guard is independently pinned by `CountAt(id)==0`).

## Design Notes

`AddSaturating` must widen BEFORE adding — `Fixed.Max(Zero, a + b)` cannot recover a value that already wrapped in the int add, so the saturation has to live in the sum itself. Existing DW-28 growth caps (`*_per_level < 256`, ≤99 stacks) make realistic authoring safe; this closes the residual pathological-base class deterministically.

The death re-entrancy already has precedent: a lethal DoT period / `InitialEffect` can already `Destroy` a host mid-`Advance`/mid-install, and the store guards it with `if (!_world.IsAlive(i)) break;` (Advance) and `if (!_world.IsAlive(hostId)) return;` (RemoveSlot after an expire-effect). This change extends the same guard discipline to the `ApplyStatDeltas` callers, since the kill now runs there. `RecordKill(victim, Neutral)` counts the victim's loss and credits no killer (killer index 0 is skipped) — the correct scoring for an attacker-less rules death.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds clean (no new warnings; touched sim code stays Godot-free / float-free).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~FixedBoundary|FullyQualifiedName~ModifierStoreApply"` -- expected: new saturating-add + ceiling-collapse-death tests pass; existing wrap-pinning tests stay green.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full sim suite green, NO golden moved (change is content-gated). Allow the known CanonicalModelHashPerf timing flake on a lone full-suite fail (re-run/isolate to confirm — never a code regression).

**In-Engine Gate:** NOT APPLICABLE — the diff touches only `src/Core`, `src/Effects` (pure simulation); no `src/UI`, `src/CreationSuite`, `src/Core/Bootstrap`, `MainScene.cs`, `scenes/**`, or `*.tscn`/`*.tres`.

## Auto Run Result

Status: done
Blocking condition: none

**Change:** Third (follow-up) review pass on the DW-28/DW-325 change. Ran all five review layers (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate) in parallel. The gate auditor confirmed GATE NOT APPLICABLE (pure `src/Core`+`src/Effects` diff). Triage found no `intent_gap` and no `bad_spec`. One genuinely-new, in-scope finding was patched: the four-line `AddSaturating` recompute swap had a pipeline-level test only for MaxHealth, so reverting any of the attack/move/armor swaps (`ModifierSystem.cs:90/92/94`) back to `operator+` left the full suite green — a regression-gap this change introduced. Added `RecomputePipeline_SaturatesAttackMoveArmor_InsteadOfWrappingToZero` to close it. Every other finding was a re-discovery of the six already-open ledger entries **DW-488…DW-493** (accumulator wrap now lethal, unaudited destroy-on-return callers, no hero-XP credit, absolute-zero vs. transition gating, RestoreSlot/Tick zombie, KillEntity entry guard) and was rejected rather than re-deferred, per the invocation directive to never re-open or duplicate existing entries. This pass converges the story (`followup_review_recommended: false`).

**Files changed:**
- `godot/ProjectChimera.Sim.Tests/Effects/ModifierStoreApplyTests.cs` — added the attack/move/armor pipeline-saturation test (the sole code change this pass); no production code was modified.

**Verification:**
- `dotnet build godot/godot.sln` — clean (0 errors; 14 pre-existing nullable/annotation warnings, none new).
- `dotnet test … --filter "FixedBoundary|ModifierStoreApply"` — 28/28 passed (includes the new test).
- `dotnet test godot/ProjectChimera.Sim.Tests/…` (full sim suite) — 3733 passed, 1 skipped, 0 failed in 1m41s; NO golden moved (confirming the "no golden moves" claim empirically and closing the DW-28 determinism concern raised by the adversarial lens).
- No deferred-work ledger entries were added or modified this pass; the six recurring findings are already tracked as DW-488…DW-493.

**Residual risks:** The six deferred items (DW-488…DW-493) remain open and content-gated — none is triggerable by shipped content today (confirmed prior passes: only `aura_guard.json`=0 and `ring_of_vigor.json`=+50 author MaxHealth). The residual pathological-base accumulator wrap (DW-488) is the highest-value follow-up should creator content ever author large-magnitude MaxHealth stacks.


