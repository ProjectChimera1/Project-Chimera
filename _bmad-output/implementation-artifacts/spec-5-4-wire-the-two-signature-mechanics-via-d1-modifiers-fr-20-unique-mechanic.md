---
title: 'Wire the two signature mechanics via D1 Modifiers (FR-20 unique mechanic)'
type: 'feature'
created: '2026-07-10'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false # judgment: 4 patches, all localized to one new test file, no production/sim code touched, no API/data/security impact
context: []
warnings: [oversized]
baseline_revision: '262b53bc2ca57c9ccad68f7276bc5677ee75ee80'
final_revision: '9e4876ad6adf3f15370e6740488c3bf831dc7593'
---

<intent-contract>

## Intent

**Problem:** Story 2.10/2.13 already authored and attached both signature mechanics to the real content — Equal
Exchange (`spike_transmutation` on alpha's `infantry`) and Sanguine Furnace (`furnace_trickle`/`furnace_pour` across
beta's roster) — but no test drives the REAL faction JSON + REAL `AbilityRegistry` through a running sim to prove
either mechanic fires, or that a Covenant unit does NOT regenerate from the furnace. Alpha's
`signature_mechanic_effect_id` also reads `"equal_exchange_self_cost"`, an id that resolves to no real ability — the
actually-attached one is `spike_transmutation` — and nothing (validator or test) has ever caught the mismatch.

**Approach:** Per epics.md's quality-review re-scope, this is VERIFY/INTEGRATION, not re-implementation: fix the one
mismatched descriptor string, then add real-content integration tests (real `FactionDefinition.LoadFromFile` + real
`AbilityRegistry.LoadFromDirectory` through a real `SimulationHost`) proving cross-faction regen isolation, the
armor-independent flat self-cost, two-run determinism, and that on-death ("Glut") wiring is structurally impossible.

## Boundaries & Constraints

**Always:** No new sim `.cs` production code — the mechanics (`AbilityCastSystem`'s `cost_health` gate,
`Persistent`+`Heal`, `ModifierStore`) already exist and are correct per Story 2.10/2.13; this story only fixes one
JSON descriptor string and adds tests. New/changed tests load the REAL `alpha_faction.json`/`beta_faction.json` and
REAL ability JSON from disk — never a hand-built fixture standing in for shipped content. Test code stays Godot-free,
FixedPoint-only, ascending-id order, no wall-clock/unseeded RNG. All 14 existing goldens stay byte-identical; no
`AlgoVersion` bump; no new `EntityWorld` SoA field.

**Block If:** none identified — scope, the descriptor mismatch, and the missing coverage are each fully determined by
source inspection.

**Never:** Do not touch `AbilityCastSystem.cs`/`HealEffect.cs`/`ModifierStore.cs`/`EffectCaps.cs`/`DirectHpDeltaEffect.cs`
(2.10/2.13-owned, already correct). Do not build the on-death Glut aura — its descriptor stays deferred. Do not retune
`spike_transmutation`/`furnace_trickle`/`furnace_pour` balance numbers — those are 2.10/2.13's authored values,
out of scope. Do not add a new committed-baseline `.golden.txt` — a same-run two-pass checksum-equality check
(mirroring `CanonicalScenarioTests`) satisfies the determinism AC without growing the 14-golden fence.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Beta unit below max HP, ticked forward | real `forgehand` (has `furnace_trickle`) via real content | `Health` climbs toward `EffectiveMaxHealth` | n/a |
| Alpha unit below max HP, same ticks | real unit with no furnace-type ability (e.g. `worker`) | `Health` unchanged — no regen from this mechanic | n/a |
| Equal Exchange cast, two casters differing only in `ArmorType` | real `spike_transmutation` via real `AbilityCastSystem` | Identical flat `Health` delta on both; `CostOre==CostCrystal==0` | n/a |
| `"on_death"` activation string | parsed via `AbilityDefinition.ParsedActivation` | resolves to `null` — cannot be authored into the closed set | fail-closed |
| Identical scenario run twice | same seed/inputs/tick count | `SimChecksum` sequence byte-identical across both runs | n/a |

</intent-contract>

## Code Map

- `godot/resources/data/factions/alpha_faction.json` -- fix the dangling `signature_mechanic_effect_id` to the real attached ability id.
- `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (NEW) -- real-content integration tests for both mechanics.
- `godot/ProjectChimera.Sim.Tests/Effects/PassiveRuntimeTests.cs` -- reference-only precedent (`SimulationHost.Create(...)` "NewHost" wiring pattern); do not modify.
- `godot/ProjectChimera.Sim.Tests/Effects/AbilityTestSupport.cs` -- reference-only precedent (`CastHarness`); do not modify.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- reference-only precedent (`ResolveDataPath` helper, the `AlphaFaction_RealJson_AviaryProducesGriffin_ThroughBuildingSystem` real-content-wiring test shape); do not modify.
- `godot/src/Core/Definitions/AbilityDefinition.cs` -- read-only reference (`ParsedActivation` closed set at line ~123; `CostHealth`/`AllowSelfLethal`).

## Tasks & Acceptance

**Execution:**
- `godot/resources/data/factions/alpha_faction.json` -- change `"signature_mechanic_effect_id": "equal_exchange_self_cost"` to `"signature_mechanic_effect_id": "spike_transmutation"` -- makes the descriptor resolve to the actually-attached ability id, closing a silent mismatch nothing currently guards.
- `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (NEW) -- `SanguineFurnace_RealBetaUnit_Regenerates_RealAlphaUnit_DoesNot`: build a `SimulationHost` via `SimulationHost.Create(..., registry: AbilityRegistry.LoadFromDirectory(<real abilities dir>))` with the real `alpha`/`beta` `FactionDefinition`s; spawn a real beta unit carrying a furnace ability (e.g. `forgehand`) and a real alpha unit with no such ability (e.g. `worker`), both pre-damaged; tick forward; assert beta's `Health` rises toward `EffectiveMaxHealth` and alpha's does not move.
- same file -- `SpikeTransmutation_RealAbility_FlatArmorIndependentSelfCost`: cast the real `spike_transmutation` id (loaded via the real registry) against two casters differing only in `ArmorType`; assert the `Health` delta is identical on both and equals the loaded `AbilityDefinition.CostHealth`, and assert `CostOre==CostCrystal==0` on that same loaded definition.
- same file -- `SignatureMechanicScenario_TwoRuns_ByteIdenticalChecksums`: run the above scenario twice from identical fresh state/tick count; assert the per-tick `SimChecksum` sequences are `SequenceEqual`.
- same file -- `OnDeathActivation_NotInClosedSet_CannotBeAuthored`: assert `new AbilityDefinition{ Activation = "on_death" }.ParsedActivation` is `null` (and that `AbilityValidator.Validate` rejects an ability authored with it, located).

**Acceptance Criteria:**
- Given the real `beta_faction.json` roster and real `AbilityRegistry`, when a Court unit is spawned below max HP and ticked forward, then its `Health` climbs toward `EffectiveMaxHealth` while a same-scenario Covenant unit's `Health` is unaffected by the furnace mechanic (epics AC1).
- Given the real `spike_transmutation` ability cast by two casters of different `ArmorType`, when both casts resolve, then both pay the identical flat `CostHealth` and neither also pays an ore/crystal cost (epics AC2).
- Given the signature-mechanic scenario run twice with identical seed/inputs, when both runs complete the same tick count, then their `SimChecksum` sequences are byte-identical (epics AC3).
- Given `alpha_faction.json` after the descriptor fix, when loaded, then `SignatureMechanicEffectId` names a real, loadable id in the shipped `AbilityRegistry`.
- Given the on-death Glut descriptor, when the `"on_death"` activation string is parsed, then it resolves to no member of the closed `PassiveActivation` set, proving the aura structurally cannot be wired in this epic's build (epics AC4).
- Given `AbilityCastSystem.cs`, `HealEffect.cs`, and `ModifierStore.cs` (the files powering these two mechanics), when inspected, then none contain `using Godot;`, float gameplay state, `System.Random`, or wall-clock/`DateTime` usage (epics AC5; already true by inspection — this story does not modify them).

## Spec Change Log

## Review Triage Log

### 2026-07-10 — Review pass 1

4 layers (Blind Hunter, Edge Case Hunter, Verification Gap Reviewer, Intent Alignment Auditor), all run against the full diff (one-line JSON descriptor fix + new `SignatureMechanicRealContentTests.cs`, 5 tests).

- intent_gap: 0
- bad_spec: 0
- patch: 4 (high 0, medium 2, low 2)
- defer: 5 (high 0, medium 1, low 4)
- reject: 12 (high 0, medium 0, low 12)
- addressed_findings:
  - `[medium]` `[patch]` Only alpha's `signature_mechanic_effect_id` was asserted to resolve against the real `AbilityRegistry`; beta's `furnace_trickle` descriptor had no equivalent guard against the exact same dangling-string defect class this story exists to fix (Blind Hunter + Edge Case Hunter, converged). Renamed/extended the test to `SignatureMechanicEffectIds_ResolveInRealRegistry_BothFactions`, asserting both factions' descriptors resolve.
  - `[medium]` `[patch]` `SpikeTransmutation_RealAbility_FlatArmorIndependentSelfCost` (and the determinism harness) cast through two fully-synthetic entities, never alpha's real `infantry` unit — the one roster member that actually carries `spike_transmutation` — leaving AC2's "a Covenant signature action" untested against real content on that side, unlike the furnace test's real `forgehand`/`worker` (Blind Hunter + Intent Alignment Auditor, converged). Both the cast test and the determinism harness now spawn the real `infantry` unit via `ApplyUnitDefinition` (real `ArmorType.Medium`, real `AbilityId` from the shipped JSON) as one caster, keeping only the second (Heavy-armor) caster synthetic.
  - `[low]` `[patch]` `beta.GetUnit("forgehand")!`/`alpha.GetUnit("worker")!` would NRE with no diagnosable message if a future roster rename drops either unit (Edge Case Hunter). Added explicit `Assert.NotNull` guards before dereferencing.
  - `[low]` `[patch]` No comment explained why the Player1 `spike_transmutation` casters and the Neutral furnace/control units can't interact in `BuildCombinedScenarioHarness`'s shared scenario — two independent reviewers had to re-derive this by inspection (Blind Hunter + Edge Case Hunter, converged). Added an explanatory comment (distance far outside any vision/attack range; synthetic casters receive no attack stats).
  - Deferred (5, logged as DW-106 through DW-110): `FactionValidator` never resolves `signature_mechanic_effect_id` against the registry for any faction — a systemic pre-existing gap this story's test-level fix doesn't close (medium; DW-106); `AbilityRegistry.LoadFromDirectory`'s silent-skip-on-invalid-file blind spot is unguarded in the new tests (low; DW-107); the fixed `-30` pre-damage magic number is fragile against future low-HP roster content (low; DW-108); the determinism test re-reads real content from disk twice per run, a hermeticity/perf cost versus the in-memory pattern used elsewhere (low; DW-109); cast-intent issuance inside the `build` callback rather than `RunAndRecord`'s `perturb` hook is a latent coupling to the harness's current calling convention (low; DW-110).
  - Rejected (12, all low/cosmetic, no test or behavior change needed): the armor-independence test "proves a structural given" since the cost_health debit path never reads `ArmorType` at all — accurate but the test remains legitimate regression insurance; `OnDeathActivation_NotInClosedSet_CannotBeAuthored` "pins a pre-existing invariant" rather than exercising new regression surface — true, but it is the correct, stronger proof of AC4 (no on-death code path exists at all) and was explicitly reasoned about in Design Notes; no isolated "alpha JSON validates cleanly" test (any validator regression would already surface via an exception in every test in the file); no changelog signal inside the JSON itself (not fixable, the format has no comment support); the near-lethal `cost_health` gate edge case doesn't reflect real data (`spike_transmutation`'s cost 25 is well under any caster's starting HP); the single-`StepOnce` cast-resolution assumption matches the established codebase-wide no-wind-up cast semantics (`CastHarness.IssueAndTick`/`SelfLethalCastTests` precedent); `ResolveDataDir`'s repo-root walk-up is copied verbatim from established project-wide precedent (`FactionValidatorTests.ResolveDataPath`, `AbilityDeserializeTests.AbilitiesResourceDir`); Intent Alignment's story-title-vs-diff-scope "mismatch" is already correctly resolved by the epics.md quality-review annotation (the authoritative re-scope, not a defect); Intent Alignment's AC4 structural-vs-runtime framing — the structural proof is the stronger one given no on-death code path exists; Intent Alignment's AC2 double-charge static-vs-dynamic distinction — the static `CostOre==CostCrystal==0` check is dispositive since the resource-debit code reads exactly those fields; Intent Alignment's AC5 "no dedicated test" — already covered by the project's existing banned-API analyzer, run in this spec's own Verification commands, over the real mechanic files; Intent Alignment's AC3 golden-checksum wording ambiguity — deliberately and defensibly resolved in Design Notes, matching the `CanonicalScenarioTests` precedent.

## Design Notes

**Why armor-independence uses two synthetic casters, not two roster units.** Alpha's roster has only one unit
carrying `spike_transmutation` (`infantry`). `ArmorType` is never read by the `cost_health` debit path
(`AbilityCastSystem.cs`'s flat `world.Health[id] -= Fixed.FromInt(ab.CostHealth)`), so the minimal real-content proof
is: load the real `spike_transmutation` `AbilityDefinition` via `AbilityRegistry.LoadFromDirectory`, spawn two bare
entities differing only in `world.ArmorType[...]`, and cast the same real ability id against both — proving armor
independence without needing a second Melee unit in the shipped roster.

**Why the determinism check needs no new `.golden.txt`.** `CanonicalScenarioTests.P2_4_...IsDeterministic` already
proves generic two-run agreement for the real faction JSON; this story's new test reuses that same "two fresh runs,
`SequenceEqual` the checksums" shape, but scoped to the furnace+cast scenario so failure localizes to these two
mechanics specifically, rather than adding a 15th committed baseline.

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the new `SignatureMechanicRealContentTests`, 14 existing goldens byte-identical.
- `git status --short -- godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty (no re-baseline).
- `dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj -c Release --no-restore --no-incremental -p:ChimeraRelease=true` -- expected: 0 errors (banned-API analyzer; no sim `.cs` touched, should be a no-op).

## Auto Run Result

Status: done

**Summary:** Per epics.md's own quality-review re-scope ("VERIFY/INTEGRATION — confirm the Epic-2 signature mechanics are correctly wired... do NOT re-implement them here"), fixed alpha's dangling `signature_mechanic_effect_id` descriptor (`"equal_exchange_self_cost"` → the real, attached `"spike_transmutation"` ability id) and added real-content integration tests proving both signature mechanics (Sanguine Furnace's HoT, Equal Exchange's flat self-cost) actually execute through the real `FactionDefinition`/`AbilityRegistry`/`SimulationHost` — coverage that never existed despite both mechanics having been fully authored and attached to the shipped rosters back in Story 2.10/2.13.

**Files changed:**
- `godot/resources/data/factions/alpha_faction.json` -- one-line fix: `signature_mechanic_effect_id` now names the actually-attached ability (`spike_transmutation`) instead of a dangling string matching no real ability.
- `godot/ProjectChimera.Sim.Tests/Effects/SignatureMechanicRealContentTests.cs` (new) -- 5 tests: real-content cross-faction furnace regen isolation, armor-independent flat Equal Exchange self-cost (now anchored on the real `infantry` caster per review), two-run determinism, structural proof the on-death Glut aura cannot be authored, and both factions' signature-mechanic descriptors resolving in the real registry.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- appended DW-106 through DW-110 (pre-existing gaps surfaced during review: no general validator-level descriptor-resolution gate, a few test-hardening opportunities).

**Review findings breakdown:** 0 intent_gap, 0 bad_spec, 4 patch (2 medium, 2 low — all applied), 5 defer (1 medium, 4 low — logged as DW-106 through DW-110), 12 reject (all low/cosmetic).

**Follow-up review recommendation:** false -- all patched findings were localized to the one new test file (descriptor symmetry, real-unit substitution for one caster, null guards, a comment), no production/sim `.cs` file was touched, and no API/data/security-relevant behavior changed.

**Verification performed:** `dotnet build`/`dotnet test` re-run after the review patches -- 1425 passed, 1 pre-existing unrelated skip, 0 failed (including all 5 patched `SignatureMechanicRealContentTests`); `git status --short` on `Golden/*.golden.txt` empty both before and after patches (no re-baseline); `dotnet build` of `ProjectChimera.Sim.Analysis` (Release, banned-API analyzer) -- 0 errors, only pre-existing warnings in files this story never touched.

**Residual risks:** DW-106 (the systemic gap this story's fix doesn't close: `FactionValidator` still never resolves `signature_mechanic_effect_id` against the registry for any faction, so a future creator-authored faction could ship the same class of dangling descriptor undetected) is the most consequential deferred item; DW-107 through DW-110 are minor test-hardening opportunities, none blocking this story's own acceptance criteria.

**Residual artifacts:** this spec file itself carries an uncommitted trailing edit (`status: done` + `final_revision`) written after the `final_revision` commit hash was captured — necessarily sequenced after that commit, per the workflow's own residual-artifact rule this is left in place rather than committed or amended into the prior commit.
