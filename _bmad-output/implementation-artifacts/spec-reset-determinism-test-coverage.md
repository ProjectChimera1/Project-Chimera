---
title: 'Reset determinism test coverage (DW-19, DW-20, DW-193)'
type: 'chore'
created: '2026-07-19'
status: 'done'
baseline_revision: '51f1894b4fad40b08ded623cf11f956c7be4eb79'
final_revision: 'd131a55'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The SimReset determinism harness is hand-enumerated and therefore self-blinding: `ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed` asserts only ~12 of `EntityWorld`'s ~70 SoA arrays (DW-19), no reset-reproduction test runs a scenario that actually fights, so per-match state held by `ProjectileSystem`/`AbilityCastSystem`/`CombatSystem` is unpinned (DW-20), and nothing pins that `ClearForReset` → re-apply of a *trigger-carrying* scenario re-seeds `TriggerEnabledStore` non-additively (DW-193).

**Approach:** Add three Tier-1 xUnit guards to the existing `ProjectChimera.Sim.Tests` suite. These are **test-only additions** — a reflection-driven exhaustive fresh==cleared field sweep over `EntityWorld`, a combat/ability reset-reproduction fixture, and a trigger-enabled re-seed regression. All three must pass against today's production code unchanged.

## Boundaries & Constraints

**Always:**
- Test-only change. Every new test passes against unmodified production code — these pin existing-correct behavior against *future* regressions.
- Godot-free: no `using Godot;`, `Fixed`/int values only, ascending-id iteration. Match the suite's conventions (xUnit `[Fact]`, `Subject_Behavior_Condition` names, `// ── section ──` banners, class doc-comment citing the DW id).
- Every new test must have **teeth**: assert the precondition it depends on actually held (projectiles in flight, a modifier installed, a trigger runtime-disabled) *before* the reset, so the test cannot silently degrade into a vacuous pass.
- The reflection sweep's exclusion set must be an explicit, commented allowlist — never a silent skip.

**Block If:**
- A new test goes RED against unmodified production code. That means a real reset defect exists, which is a production fix outside this bundle's scope — HALT and report the diverging field/checksum tick rather than changing production code or weakening the assertion.
- Closing DW-19 would require bumping `SimChecksum.AlgoVersion` or `CanonicalModelHash.AlgoVersion`, or re-recording any `*.golden.txt`.

**Never:**
- Do not edit `_bmad-output/implementation-artifacts/deferred-work.md` — the orchestrator records resolution.
- Do not modify `EntityWorld`, `SimulationHost`, `TriggerEnabledStore`, or any other production sim file.
- Do not delete or weaken the existing hand-enumerated assertions in `ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed`; the reflection sweep is a **new, separate** `[Fact]` that supplements them.
- Do not re-record goldens or touch `HashAlgoVersions_AreUnchanged`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Exhaustive fresh==cleared sweep | A world run 60 ticks then `Clear()`ed, vs a freshly-constructed `EntityWorld` | Every instance field (arrays elementwise, scalars by value) is equal, except the documented delegate allowlist | Failure names the diverging field via reflection, not "some array differs" |
| New SoA field omitted from `Clear` | A hypothetical future array field left dirty by `Clear` | The sweep goes RED naming that field | n/a — this is the guard's purpose |
| Reset over a fighting run | Scenario with projectiles in flight + an ability cast + an active modifier at reset time | `ClearForReset` + re-populate reproduces the per-tick checksum sequence byte-identically vs a fresh host | Divergence reports the first differing tick |
| Fight preconditions absent | Projectiles never spawned / ability never cast | Test fails loudly on the precondition assertions | Guards against a vacuous pass |
| Projectile store leak across reset | Projectiles alive at `ClearForReset` | `Projectiles.HighWaterMark == 0` and no row `Alive` after the clear — asserted **directly**, since projectile state is not folded into `SimChecksum` | n/a |
| Trigger-enabled re-seed (runtime flip) | Trigger A `disable_trigger`s B; run until B is runtime-disabled, then `ClearForReset` + re-apply | `Count` → 0 at clear; after re-apply B is enabled again (authored state), and the re-run reproduces the checksum sequence | n/a |
| Trigger-enabled re-seed (authored) | A third trigger authored `Enabled = false` | After re-apply it is still disabled — `Reset`'s all-true seed is overwritten by `SetInitial`, not left permissive | n/a |

</intent-contract>

## Code Map

- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` -- the harness being extended; `BuildApplied()`/`ApplyValidated()`/`RunTicks()` helpers to reuse; `ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed` (:229) is the hand-enumerated test DW-19 supplements.
- `godot/src/Core/EntityWorld.cs` -- `Clear()` at :1155-1212. All SoA arrays are `public readonly T[]` fields (:190-637), so `GetFields` enumerates them. Non-array state that a *public-array-only* sweep would miss: `HeightAdvantageVision`/`HeightVisionBonusPerStep` (public non-array fields), private `_freeList`/`_freeCount`/`_nextId`/`_elevationGrid`, `Rng` + `Pathability` (properties). Delegates `OnDestroy`/`OnUnitDefinitionApplied` are **deliberately preserved** by `Clear` (:1150-1152) → allowlist them.
- `godot/src/Core/Sim/SimulationHost.cs` -- `ClearForReset()` at :342-371, a flat fan-out over ~24 stores. Doc at :337-340 records the DW-20 assumption: the cast system "holds no per-match state".
- `godot/src/Core/TriggerEnabledStore.cs` -- `Reset(int)` (:39) re-seeds every entry `true` then `SetInitial` applies authored state; `Clear()` (:69) only sets `Count = 0` and leaves `_enabled` dirty. The re-seed loop is exactly what DW-193 pins.
- `godot/ProjectChimera.Sim.Tests/Golden/DeliveryScenario.cs` -- pattern for wiring `Delivery`/`ProjectileSpeed`/`AttackRange` directly so units fire projectiles (integer/`Fixed` only, cross-platform safe).
- `godot/ProjectChimera.Sim.Tests/Golden/AbilityCastScenario.cs` -- pattern for `AbilityRegistry` + `AbilityId`/`AbilityCount`/`Energy` wiring and issuing a cast via `OrderApplier.Apply`.
- `godot/ProjectChimera.Sim.Tests/Dsl/RandomChoiceEnableRunEventTests.cs` -- `DisableTrigger_SuppressesTargetSameTick_AndFoldsIntoEnabledMask` (:127) is the graph shape to reuse (exec order is priority-desc → A=idx0, B=idx1).
- `godot/ProjectChimera.Sim.Tests/Effects/` -- `AbilityTestAbilities.BattleFury()`, the in-code ability definition.

## Tasks & Acceptance

**Execution:**

- `godot/ProjectChimera.Sim.Tests/Sim/EntityWorldClearCompletenessTests.cs` -- NEW. Add `Clear_LeavesEveryFieldEqualToFreshlyConstructed_ExhaustiveReflectionSweep`: build a world, run it dirty (spawn units, advance `Rng`, set the height-vision globals + an elevation grid), `Clear()`, then reflect over `typeof(EntityWorld).GetFields(Public|NonPublic|Instance)` and compare each field against a fresh `EntityWorld`. Arrays compare elementwise (null-safe, covering the `UnitDefinition[]`/`CombatFeedbackProfile[]` reference arrays); scalars/refs by `Equals`/null. Assertion messages must name the field. Add a second `[Fact]` asserting the allowlist (`OnDestroy`, `OnUnitDefinitionApplied`) still resolves to real fields, so a rename can't silently widen the exclusion. -- DW-19: converts a hand-maintained list into a self-pinning guard that auto-catches a future field omitted from `Clear`.

- `godot/ProjectChimera.Sim.Tests/Golden/CombatResetScenario.cs` -- NEW fixture. A re-appliable `Populate(SimulationHost)` (mirroring `PopulateAiExpansion`'s shape in `SimResetTests`) building a *fighting* start state: P1 projectile-delivery attackers vs P2 targets close enough to engage, plus a P1 caster holding `battle_fury` with an energy pool. `EntityWorld.Create` defaults `Delivery` to `Hitscan`, so **`Delivery = AttackDelivery.Projectile` must be set explicitly** or the fixture spawns zero projectiles and the test is vacuous. Expose `Build()` (host with `AbilityRegistry`, `ChecksumInterval = 1`) and a `CastAt(host)` helper issuing the cast via `OrderApplier`. Keep every value integer/`Fixed`; leave the AI inactive so it stays cross-platform safe. -- DW-20: the composed projectile/ability/combat start state the golden applier fixture lacks.

- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` -- Add `ClearAndReapply_ReproducesByteIdenticalRun_OverAFightingScenario`: run the combat fixture N ticks, assert the fight preconditions held (projectiles were spawned, an ability cooldown is ticking, a modifier is installed) and that projectiles are **in flight at reset time**, then `ClearForReset()` + re-populate + re-run and compare the per-tick checksum sequence against both the first run and an independent fresh host, using `GoldenChecksumReplay.CompareSequences`/`DescribeDivergence` so a failure names the first divergent tick. Because `ProjectileStore` is **not folded into `SimChecksum`**, add a direct post-clear assertion that `Projectiles.HighWaterMark == 0` and no row is `Alive` — the checksum path only observes projectiles indirectly, through the damage they land on folded `Health`. -- DW-20: pins the composed combat path the existing keystone skips.

- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` -- Add `ClearForReset_ThenReapplyTriggerCarryingScenario_ReseedsEnabledMaskNonAdditively`: load a three-trigger graph where A (`Priority = 10`) `disable_trigger`s B, and C is authored `Enabled = false`. Execs are ordered **priority-descending**, so assert against the resulting index order. Step until `host.TriggerEnabled.IsEnabled(bIdx)` is false, assert `Count == 0` after `ClearForReset()`, re-apply via `host.ScenarioDirector.LoadScenario`, then assert B is enabled again (the `Reset` all-true re-seed), C is *still* disabled (the `SetInitial` authored re-seed overrides that all-true seed), and `Count` equals the trigger count. Capture the checksum sequences either side of the reset and assert byte-identical reproduction. -- DW-193: pins the `Reset` re-seed loop that makes the mask non-additive.

**Acceptance Criteria:**

- Given the reflection sweep, when one `Array.Clear` line in `EntityWorld.Clear()` is commented out **as a temporary local mutation check**, then the sweep fails naming exactly that field; the line is then restored and `git diff` confirms `EntityWorld.cs` is unmodified in the final change.
- Given the fighting reset test, when the run is stepped, then projectiles are observed alive and the caster's ability cooldown is non-zero before `ClearForReset` is called, so the reproduction assertion is non-vacuous.
- Given all three new tests, when run against unmodified production sim code, then every one passes — no production file is modified in this change.
- Given the full Tier-1 suite, when run after this change, then the pre-existing 2748 passing tests still pass and `HashAlgoVersions_AreUnchanged` is untouched.

## Spec Change Log

## Review Triage Log

### 2026-07-19 — Review pass

- intent_gap: 0
- bad_spec: 0
- patch: 13: (high 1, medium 4, low 8)
- defer: 3: (high 0, medium 1, low 2)
- reject: 7
- addressed_findings:
  - `[high]` `[patch]` The DW-19 sweep was exhaustive on the *comparison* side but hand-enumerated on the *fixture* side — `BuildDirtyWorld` dirtied ~16 of ~70 arrays, so any field it never moved off its fresh value sat equal in both worlds and a `Clear()` omitting it passed GREEN. Two reviewers demonstrated this independently (deleting `Array.Clear(PatrolWaypoints)` + the three `OrderQueue*` clears, and separately `Pathability = null`, left the whole suite green). Fixed with a reflection-driven synthetic fill of every array field plus a `SetPathabilityGrid` call, and by replacing the global `before.Count > 0` teeth check with a **per-field coverage assertion** that fails naming any swept field the fixture left undirtied. Re-verified by independent mutation: removing the three `OrderQueue*` clears now goes RED naming all three.
  - `[medium]` `[patch]` DW-20's projectiles never impacted inside the sampled window (`ProjectileSpeed 6` over distance 10 ≈ 50 ticks of flight vs `N = 40`), and `ProjectileStore` is unfolded — so the projectile half contributed nothing to the checksum comparison. Raised `ProjectileSpeed` to 20 (~15-tick flight) and added a precondition asserting a projectile actually landed (P2 target health drops only from P1 projectile fire). Both halves now hold: 2 in flight at reset, one 9-damage impact per target.
  - `[medium]` `[patch]` `CombatResetScenario` set `EffectiveAttackDamage` without `BaseAttackDamage`; the fixture survived only because `ModifierSystem.RecomputeEntity` skips non-dirty entities, so a future aura/debuff/global recompute would silently zero both attackers and collapse the fight. Base now set alongside every Effective write, which also makes the caster's `Effective > Base` buff precondition a real check.
  - `[medium]` `[patch]` `Populate` never verified that `Create` returned the ids its public constants hardcode — a violation would silently retarget every precondition at the wrong entity. Added `RequireId` checks.
  - `[medium]` `[patch]` `GoldenChecksumReplay.CompareSequences` reports no divergence for two EMPTY sequences, so a checksum sink that never fired would make all three comparisons vacuously pass. Added sequence-length assertions to both new tests.
  - `[low]` `[patch]` Guarded the `-1` "no ability" sentinel from `Registry.IndexOf("battle_fury")`, which would have made the cast a silent no-op.
  - `[low]` `[patch]` Built the `AbilityRegistry` per-`Build()` instead of sharing a `static readonly` instance across every host and parallel xUnit class (`AbilityDefinition` is mutable and carries a shared `EffectGraph`).
  - `[low]` `[patch]` Reworded the sweep's failure message, which called every divergence "a reset/determinism leak" — false for the unfolded presentation-only arrays (`MeshType`, `FeedbackProfile`, `SourceDefinition`) it also covers.
  - `[low]` `[patch]` Fixed the allowlist's stale `EntityWorld.cs :1150-1152` citation (that range is inside the XML doc block; the method preserves the delegates by omission).
  - `[low]` `[patch]` `DescribeArrayDivergence` threw `ArgumentException` on rank > 1 arrays and compared jagged inner arrays by reference; both now handled, since this class is explicitly a future-proof tripwire.
  - `[low]` `[patch]` Iterate projectile slots by `Alive.Length` rather than the `MAX_PROJECTILES` constant.
  - `[low]` `[patch]` Corrected `BuildEnableMaskScenario`'s doc, which claimed B/C each set a folded DSL variable visible in the checksum — neither ever fires, so the assertion rests entirely on the mask reads.
  - `[low]` `[patch]` Reworded the `Assert.Equal(2, Allowlist.Length)` comment, which claimed to enforce review but is defeated by editing the literal on the same line.

### 2026-07-19 — Review pass (follow-up)

- intent_gap: 0
- bad_spec: 0
- patch: 13: (high 0, medium 5, low 8)
- defer: 3: (high 1, medium 1, low 1)
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` The `EntityWorldClearCompletenessTests` class doc billed itself as a completeness guard for "the Edit↔Play reset (`SimulationHost.ClearForReset`)" while sweeping exactly one of the ~24 stores that call clears. Three of four review layers independently flagged the claim/delivery gap. Added an explicit SCOPE paragraph naming the unswept sibling stores, stating that dropping an `Array.Clear` from `ProjectileStore`/`HeroStore`/`ItemStore` ships green today, and directing the reader to read a pass as "EntityWorld is field-complete", not "the reset is field-complete".
  - `[medium]` `[patch]` `NonDefaultValue`'s carefully-worded "teach me about this type" `InvalidOperationException` was unreachable for exactly the cases it exists for: `Activator.CreateInstance` *throws* `MissingMethodException` for a reference element type with no public parameterless ctor (and for abstract/interface types), so the author would have gotten a bare reflection error instead. Wrapped in a `catch (MissingMemberException)` that falls through to the guidance, plus explicit `string` and abstract/array handling. Flagged by three layers.
  - `[medium]` `[patch]` `SyntheticallyFillArrays` used `Array.SetValue(object, int)`, which throws `ArgumentException` on rank > 1 — directly contradicting `DescribeArrayDivergence`'s doc advertising deliberate multidimensional support. The two halves could drift apart silently; added an explicit rank guard that fails with the instruction.
  - `[medium]` `[patch]` `typeof(EntityWorld).GetFields(Instance)` does not return a base class's private fields, and `EntityWorld` is non-sealed. Inserting a base type would have silently shrunk the sweep with no failure anywhere — in the one test whose entire thesis is that it cannot silently shrink. Pinned `BaseType == typeof(object)` with a message naming the three methods to teach.
  - `[medium]` `[patch]` Replaced the allowlist's `Assert.Equal(2, Allowlist.Length)` — ceremony its own comment disowned as "a speed bump, NOT review enforcement", defeated by editing the literal on the same line — with the real guarantee: the allowlisted names must be exactly the set of delegate-typed instance fields on `EntityWorld`. A future host-lifetime subscription added without an entry now fails loudly, and nothing non-delegate can be exempted.
  - `[low]` `[patch]` Documented that `BuildDirtyWorld`'s ~15 hand-written per-entity assignments are superseded elementwise by `SyntheticallyFillArrays` (which runs last), so they are not load-bearing coverage — the `Create`/`Destroy` calls are, because they drive the allocation/free-list/id-counter state the synthetic fill cannot reach.
  - `[low]` `[patch]` `CombatResetScenario.LiveProjectiles` bounded its scan by `HighWaterMark` while the sibling post-clear assertion deliberately scans `Alive.Length`; a slot alive past the mark would have *under-counted* the in-flight precondition. Bounded by `Alive.Length` for consistency.
  - `[low]` `[patch]` The DW-20 fight preconditions asserted only `Attacker1`/`Target1`, so the second attacker/target pair could sit inert while the guard passed. Added damage assertions for `Attacker2`/`Target2`.
  - `[low]` `[patch]` DW-193 never asserted trigger A's own post-re-apply enabled state — a regression silently disabling A would leave B enabled for the wrong reason and still pass. Added the assertion with that rationale.
  - `[low]` `[patch]` DW-193 fed the *same* `ScenarioData` instance to both `LoadScenario` calls; a `LoadScenario` that mutated its input would have hidden exactly the additive leak under test. The re-apply now builds a fresh instance, as Edit→Play supplies.
  - `[low]` `[patch]` `CombatResetScenario`'s "TRAP (2)" narrative overstated the danger — `Create` defaults `ProjectileSpeed` to `ProjectileSystem.PROJECTILE_SPEED` (18), not 0, so omitting the write would not have produced the described vacuity. Reworded to the real reason (pinning the flight time the sampling window depends on). Also corrected `CommandState = Idle`, documented as "auto-acquire the nearest enemy" but already `Create`'s default.
  - `[low]` `[patch]` Qualified the fixture's "every value is integer/`Fixed`, cross-platform safe" claim: `EntityWorld.Create` itself performs one `Fixed.FromFloat(8f)` for `VisionRange`. Constant-folded and shared by every Tier-1 fixture, so the guarantee is "no float arithmetic on authored values", not "no float literal on the path" — worth stating precisely, since the Tier-1 cross-platform gate rests on it.
  - `[low]` `[patch]` Documented the registry-coupling invariant in `Populate`: it resolves the ability index from its own `NewRegistry()` while the cast executes against the registry `Build()` gave the host. They agree only because both come from the same factory, and `SimulationHost` exposes no registry for a cross-check — so `Populate` is valid only on a `Build()`-produced host, and the `-1` guard catches "no ability", never "wrong ability".

## Design Notes

The reflection sweep is deliberately broader than DW-19's literal ask ("SoA arrays"): sweeping **all** instance fields also covers `_freeList` (private array), the two height-vision scalars, and `_freeCount`/`_nextId`. `Rng` and `Pathability` are properties, not fields, so the sweep misses them — the existing hand-written test already asserts `Rng.State` and probes the elevation grid behaviorally, which is why those assertions must survive.

Sketch of the comparison core:

```csharp
foreach (FieldInfo f in typeof(EntityWorld).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
{
    if (Allowlist.Contains(f.Name)) continue;              // delegates Clear() deliberately keeps
    object? a = f.GetValue(fresh), b = f.GetValue(cleared);
    if (a is Array ea && b is Array eb) AssertElementwise(f.Name, ea, eb);
    else Assert.True(Equals(a, b), $"EntityWorld.{f.Name} differs after Clear()");
}
```

For DW-20, the honest scope: investigation confirms none of `ProjectileSystem`/`AbilityCastSystem`/`CombatSystem` holds mutable per-match state today — their owned `SpatialHash`/`EffectExecutor` self-refresh at the top of each `Tick`, and `EffectExecutor.LastPeakStackDepth` is diagnostic-only and unfolded. So this test cannot fail today; its value is entirely as a **tripwire for a future per-match field** folded into one of those systems. Framing it that way (rather than as a suspected-bug hunt) is what the ledger entry asks for.

For DW-193, note `Clear()` only zeroes `Count` — the `_enabled` buffer keeps its stale flags. **Correction found during verification:** non-additivity does *not* come from `Reset`'s all-true re-seed loop, as first assumed. `ScenarioDirector.LoadScenario` (:577-581) calls `SetInitial(i, ...)` for **every** exec index, so the authored state fully overwrites the buffer regardless; `Reset`'s loop is redundant defense-in-depth for a hypothetical caller that doesn't seed every index. Mutation-testing confirmed this: deleting `Reset`'s loop leaves the test GREEN, while deleting the `SetInitial` call turns it RED. The test therefore pins the observable contract (the mask re-seeds to authored state across a reset) rather than one particular mechanism — which is what DW-193 asked for. The disable must still be run to `false` *before* the reset for the assertion to have teeth.

## Verification

**Commands:**
- `cd godot && dotnet test ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, total count = 2749 + the new tests, 0 failed (baseline before this change: 2748 passed / 1 skipped).
- `cd godot && dotnet test ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~SimResetTests|FullyQualifiedName~EntityWorldClearCompleteness"` -- expected: the reset harness plus the new sweep all green.

**Manual checks (if no CLI):**
- Confirm `git diff --stat` touches only files under `godot/ProjectChimera.Sim.Tests/` — no production sim file is modified.

## Auto Run Result

Status: done

### Implemented change

Three Tier-1 xUnit guards closing DW-19, DW-20 and DW-193, as test-only additions, plus a follow-up review pass that hardened them. No production file was modified at any point (`git status --porcelain godot/src/` empty throughout, re-verified after this pass).

### Files changed

- `godot/ProjectChimera.Sim.Tests/Sim/EntityWorldClearCompletenessTests.cs` — NEW. DW-19: reflection sweep over every `EntityWorld` instance field (public+private, arrays+scalars) comparing a dirtied-then-`Clear()`ed world against a fresh one, with a reflection-driven synthetic fill so new array fields are auto-dirtied, a per-field coverage assertion, an explicit SCOPE caveat, a base-type pin, and a delegate-set allowlist-integrity `[Fact]`.
- `godot/ProjectChimera.Sim.Tests/Golden/CombatResetScenario.cs` — NEW. DW-20: re-appliable fighting fixture (2 projectile attackers vs 2 returning-fire targets + a `battle_fury` caster).
- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` — DW-20 and DW-193 reset tests plus `RunSamples`/`AssertSameSequence`/`BuildEnableMaskScenario` helpers.

### Review findings

**Pass 1:** 13 patches applied (1 high, 4 medium, 8 low); 3 deferred; 7 rejected.
**Pass 2 (follow-up):** 13 patches applied (5 medium, 8 low); 3 deferred; 8 rejected. See the Review Triage Log.

Deferred findings from this pass were appended to `deferred-work.md` as **new entries only** (DW-196, DW-197, DW-198), per the invocation's instruction; no existing entry was modified — verified by `git diff --numstat` (29 additions; the only 3 deletions are the orchestrator's own pre-existing DW-19/20/193 status flips, which predate this run).

- **DW-196 `[high]`** — the sweep technique is applied to `EntityWorld` only; the ~20 sibling stores `ClearForReset` wipes remain hand-enumerated. Demonstrated by four mutants that each ship **2752 passed / 0 failed**: `ProjectileStore.Clear()` minus `SourceId`; minus `Speed`; `HeroStore.Clear()` minus `Level`+`Xp`; `ItemStore.Clear()` minus `DefId`+`Charges`. `HeroStore`/`ItemStore` fields are checksum-folded, so those are live "reset != fresh boot" desync leaks shipping green.
- **DW-197 `[medium]`** — `TriggerEnabledStore` never shrinks `_enabled` and `IsEnabled` returns true out of range, so re-applying a *smaller* scenario leaves a stale enabled tail. The new DW-193 test only re-applies the same 3-trigger scenario.
- **DW-198 `[low]`** — the DW-20 fight preconditions are asserted against the pre-reset host only, not against `run2`/`host0`.

Notable rejections: a reported "red build from a `FutureScalarFlag` field" was **cross-agent contamination** — two review layers were mutating `godot/src` in the shared working tree concurrently, which the Verification-Gap layer independently diagnosed (it named the live session and worked from an isolated `git worktree` instead). `grep -rn FutureScalarFlag` over the repo returns nothing. Also rejected: the claim that DW-193's checksum-sequence comparison being decorative is a defect (already candidly documented in both the code and the Design Notes, and the Verification-Gap layer's own mutant proved dropping `TriggerEnabled.Clear()` is killed *only* by this test), and the claim that DW-20 has no negative control (refuted — a leaked per-match `CombatSystem` accumulator mutant *is* killed by it).

### Verification performed

- Full Tier-1 suite after this pass's patches: **2752 passed, 1 skipped, 0 failed**.
- Independent re-verification at the start of this pass (before any patch): identical result, confirming pass 1's reported numbers.
- Test-only scope re-confirmed: `git diff 51f1894 HEAD -- godot/src/` empty; `git status --porcelain godot/src/` empty; no `using Godot;` in either new file.
- Cross-checked by four independent review layers, two of which ran their own mutation testing. Confirmed killed-only-by-the-new-tests: `EntityWorld.Clear()` minus `Array.Clear(XpBounty)` / `minus Array.Clear(SplashRadius)`; a brand-new ctor-allocated array field absent from `Clear()` (caught with **zero test edits** — the self-pinning property holds); a new uncleared scalar (caught via the coverage precondition); `ClearForReset` minus `TriggerEnabled.Clear()`; minus `Projectiles.Clear()`; minus `Modifiers.Clear()` (checksum drift at tick 1).
- Confirmed all three tests run in CI on both OSes (`.github/workflows/determinism-gate.yml:68,112` runs the whole project).

### Residual risks

- **DW-196 is the material one:** a passing run of the new sweep means "`EntityWorld` is field-complete", NOT "the reset is field-complete". Four demonstrated mutants in the sibling stores ship green today, two of them in checksum-folded fields. The class doc now says this explicitly, but anyone reading only the test *name* will over-trust it.
- The DW-20 guard cannot fail against current production code by design; it is a forward-looking tripwire whose value depends on `CombatResetScenario` continuing to produce both a landed impact and in-flight projectiles.
- The DW-19 coverage assertion *requires* every swept field to be dirtied, so a future non-array field added to `EntityWorld` fails the test until an explicit write is added to `BuildDirtyWorld`. This is the intended forcing function but will read as an unrelated failure to an author who does not read the message.
- The DW-20 fixture is tuned against tick-rate-dependent arithmetic (speed 20 / distance 10 / ~15-tick flight / 30 tps / `N = 40`) that it never asserts. A tick-rate change surfaces as "no projectile has LANDED", not as a determinism signal. Judged a maintainability nit, not patched.
- The two new `.cs` files have no `.cs.uid` sidecars (Godot generates these on editor import; they cannot be minted headlessly). Expect the editor to create them on next open.

### Residual artifacts

`_bmad-output/implementation-artifacts/deferred-work.md` carries three orchestrator-owned status flips (DW-19/20/193 → `done`) that predate this run. Per the invocation, the orchestrator owns those; they are committed alongside this pass's appended DW-196/197/198 entries because they share the file.
