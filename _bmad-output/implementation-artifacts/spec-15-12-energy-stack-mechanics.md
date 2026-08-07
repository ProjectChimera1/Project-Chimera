---
title: 'Story 15.12 — Energy & stack mechanics (DW-264, DW-265, DW-272 behavior, DW-503 editor)'
type: 'feature'
created: '2026-08-07'
status: 'done'
baseline_revision: '412bb80c374425b513cff83ac49c104509ed0bda'
final_revision: 'b1c7b3cc345a46443d1cf446c4a1810ad052209f'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-15-context.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** Three authored capabilities are missing/silent. (1) DW-265: no energy-regen model exists — a unit starts full (`Energy = MaxEnergy`) and never recovers, so every caster is one-cast-per-life. (2) DW-264: `StackRule` has no per-stack-expiry mode — `Stack` shares one duration across all stacks. (3) DW-272: a stacked periodic modifier's pulse does not scale with stacks (only its flat stat deltas do), and there is no way for a creator to choose how it should. A fourth, adjacent gap (DW-503): the `AbilityValidationResult.Warnings` channel is built and tested on the sim side but nothing in the ability editor surfaces it, so authoring footguns ship silently.

**Approach:** Add a flat authored `regen_rate` on `UnitDefinition` and a folded-state-free per-tick regen system that reads it through a *single seam* Story 15.21's attribute system can later drive. Split modifier stacking into an explicit grouped variant (today's `Stack`, byte-for-byte) plus a new `StackIndependent` per-stack-expiry member, and add an authored `PeriodicStackMode` (None / Multiply / Repeat) with a system-level cap so creators choose how a stacked pulse scales. Surface the new choices — and the existing Warnings channel — in the ability editor's advanced modifier card. **Every new default must preserve today's behavior byte-for-byte** so no shipped content changes meaning.

## Boundaries & Constraints

**Always:**
- Sim layer stays pure C# (no `using Godot;`, no `float`/`Mathf`/`double`, no `System.Random`, `Fixed` only, ascending-id iteration). Authored numbers are `float`, quantized to `Fixed` at exactly ONE load boundary (`EntityWorld.ApplyUnitDefinition`), mirroring the `MaxEnergy`/`*_per_level` convention.
- New per-unit SoA field (`RegenRate`) is written ONLY through the single `EntityWorld.ApplyUnitDefinition` mapper (the A2 rule; guarded by `ApplyUnitDefinitionGuardTest`), defaulted in `Create()` so a recycled slot never inherits it, and cleared in the bulk reset. `RegenRate` is authored + in-tick-immutable → it is NOT folded into `SimChecksum` (the `MaxEnergy`/`BaseArmor` posture; the checksum-fold-timing rule folds only values that become mutable mid-match — its effect reaches the hash transitively through the already-folded `Energy`).
- Per-tick regen clamps `Energy = min(Energy + regenPerTick, MaxEnergy)`; a unit at full energy or `regen_rate == 0` is a no-op write (byte-identical).
- Regen rate is read at the tick site through exactly ONE method (`RegenPerTick(world, id) => world.RegenRate[id]`), documented as the seam Story 15.21 extends. Do not read `RegenRate[id]` directly anywhere else in the tick.
- `StackRule.Refresh=0 / Stack=1 / Ignore=2` keep their names AND numeric values unchanged; `StackIndependent` is appended as `=3`. `Stack` remains today's grouped behavior verbatim. (`StackRule` and `PeriodicStackMode` both fold into `CanonicalModelHash` by member NAME, so appending a member is free for content that does not author it.)
- Closed-enum touch rule: patching `StackRule` requires updating the one runtime switch (`ModifierStore.Apply`), the authoring-bounds check (`Modifier.CheckAuthoringBounds`), the validator warning (`AbilityValidator`), and the vocabulary array (`AbilityDraft.DraftVocabulary.StackRules`). There are NO `(int)StackRule`-indexed arrays.
- New folded `Modifier` field `PeriodicStacking` must be a `public readonly` FIELD (not a property), added to `EffectFoldCompletenessTests.ModifierFoldedFields` and folded in `CanonicalFold.MixModifier` → bump `CanonicalModelHash.AlgoVersion` 14→15 and re-record its pins IN THE SAME COMMIT.
- The periodic-stack cap is a runtime execution cap → a named `EffectCaps` constant → it moves `RulesetHash`; re-pin the ruleset-family hash pins and the hygiene/coverage-guard tests in the same commit.
- In-engine gate (binding): this diff touches `godot/src/CreationSuite/AbilityEditorPanel*`, so `status: done` requires a real in-engine verification artifact captured this session (`/godot-verify`), asserting against the authoring source with numbers.

**Block If:**
- The godot-mcp bridge is unreachable (single-client contention) so the in-engine gate for the ability-editor changes cannot be run — HALT reporting a blocking environment condition; do NOT mark done or fabricate the artifact.
- Adding `MaxPeriodicStackScale` to `EffectCaps` implies a wire PROTOCOL_VERSION bump (not just a `RulesetHash` re-pin) — e.g. a peer-agreement test demands it — HALT; a protocol bump is an owner decision.

**Never:**
- Never rename or renumber `Refresh`/`Stack`/`Ignore`, and never change `Stack`'s grouped-expiry behavior — that would move `CanonicalModelHash` for shipped content and break byte-for-byte.
- Never bump `SimChecksum.AlgoVersion` for this story: `Energy` is already folded and default `regen_rate=0` leaves every existing per-tick golden byte-identical (a bump would invalidate every existing `.chsav` save via the `SaveGameFile` gate). If an existing per-tick golden DOES move, that is a bug (regen running when it should not) — fix it, do not re-baseline to hide it.
- Never author a non-zero `regen_rate` on any shipped `resources/data` unit in this story (that would change a shipped golden). Exercise regen only in NEW test fixtures / a NEW golden scenario.
- Never turn a DW-278 authoring warning into a hard reject (DW-504 already made the intended rejects fatal); the new diagnostics ride the existing non-fatal `Warnings` channel.
- No new `Dictionary`/`HashSet` enumeration in sim; no per-entity classes; reuse `ModifierStore`'s existing per-slot machinery for per-stack expiry (one ring slot per independent stack) rather than a new per-stack-timer sub-array.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Flat regen recovers a spent caster | unit `max_energy=100`, `regen_rate=2`, `Energy=40` after a cast | each tick `Energy = min(Energy+2, 100)`; reaches 100 in 30 ticks and stays clamped | none |
| Default regen is inert | any shipped unit (`regen_rate=0`) | regen system writes `min(E+0,Max)=E` every tick — no folded state moves; existing goldens byte-identical | none |
| Recycled slot | entity destroyed then id recycled for a `regen_rate=0` def | `RegenRate[id]` reset to `Fixed.Zero` in `Create()`; no inherited regen | n/a |
| StackIndependent per-stack expiry | modifier `StackIndependent`, `MaxStacks=3`, applied at ticks 0/5/10, each `DurationTicks=20` | 3 same-id ring slots, each with its own countdown; they expire at ticks 20/25/30 independently; stat deltas revert one stack at a time | at ring/`MaxStacks` cap a further application is ignored (no refresh) |
| Grouped Stack unchanged | modifier `Stack`, `MaxStacks=3`, re-applied | one slot, `_stackCount` up to 3, single shared duration refreshed on each apply (today's behavior verbatim) | at cap, refresh duration only |
| Periodic None (default) | stacked periodic modifier, `PeriodicStacking=None` | pulse runs ONCE per period regardless of stacks (today's behavior, byte-for-byte) | none |
| Periodic Repeat | grouped `Stack`, 3 stacks, `PeriodicStacking=Repeat`, period DoT −10 | pulse graph runs `min(3, MaxPeriodicStackScale)` times per period (three −10 hits; armor subtracted per hit for a `damage` leaf) | scale bounded by the cap |
| Periodic Multiply | grouped `Stack`, 3 stacks, `PeriodicStacking=Multiply`, period DoT −10 | pulse runs ONCE with magnitude ×`min(3, cap)` (one −30 hit; armor subtracted once) via `EffectContext.PulseScale` | scale bounded by the cap |
| Periodic mode on non-stacking rule | `PeriodicStacking!=None` but `Stacking` is `Refresh`/`Ignore`, or no `period_effect` | behaves as None (stacks never exceed 1 / nothing to pulse); AbilityValidator emits a located non-fatal Warning | warning only |
| Editor surfaces a warning | author a modifier that trips a `Warnings` case in the ability editor | the advanced panel shows the located warning text (amber/appended state) | non-blocking |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `regen_rate` float (default 0f), beside `max_energy` (:315).
- `godot/src/Core/EntityWorld.cs` -- new `RegenRate` `Fixed[]` SoA (declare near `MaxEnergy` :332; alloc :924; `Create()` default `Fixed.Zero` :1043; `ApplyUnitDefinition` write `Fixed.FromFloat(def.RegenRate)` near :1278; bulk clear :1619). Auto-covered by `SnapshotUnit`/`RestoreUnit` (def-derived).
- `godot/src/Effects/EnergyRegenSystem.cs` -- NEW per-tick system; holds the `RegenPerTick` seam; clamps to `MaxEnergy`.
- `godot/src/Core/Sim/SimulationHost.cs` -- register `EnergyRegenSystem` immediately before `AbilityCastSystem` (index [5], :320); shift later indices; update the hardcoded index casts (:371-373) and diagnostic string (:385).
- `godot/ProjectChimera.Sim.Tests/.../SystemOrderTest.cs` -- update the pinned system order.
- `godot/src/Effects/Modifier.cs` -- `StackRule` +`StackIndependent=3` (:7); new `PeriodicStackMode` enum + `public readonly PeriodicStackMode PeriodicStacking` field (fold-shaped) + ctor trailing param; extend `CheckAuthoringBounds` (:198) to cover `StackIndependent`.
- `godot/src/Effects/ModifierStore.cs` -- `Apply` switch (:228) new `StackIndependent` arm (install-new-slot-per-application, bounded by `MaxStacks`+ring); `Advance` pulse step (:382-416) apply `PeriodicStacking`/cap via a scaled `RunEffect` overload.
- `godot/src/Effects/EffectContext.cs` -- new `Fixed PulseScale` (default `Fixed.One`), threaded through both ctors + `WithTarget` (preserved).
- `godot/src/Effects/DirectHpDeltaEffect.cs`, `HealEffect.cs`, `DamageEffect.cs` -- multiply authored magnitude (base damage pre-matrix for `DamageEffect`) by `ctx.PulseScale`.
- `godot/src/Effects/EffectCaps.cs` -- new named `MaxPeriodicStackScale` runtime cap (moves `RulesetHash`).
- `godot/src/Core/Definitions/CanonicalFold.cs` -- `MixModifier` (:258-274) fold `PeriodicStacking` by name → bump `CanonicalModelHash.AlgoVersion` (:180) 14→15.
- `godot/ProjectChimera.Sim.Tests/Validation/EffectFoldCompletenessTests.cs` -- add `PeriodicStacking` to `ModifierFoldedFields` (:49-53).
- `godot/src/Core/Definitions/AbilityDraft.cs` -- append `StackIndependent` to `DraftVocabulary.StackRules` (:61); add `PeriodicStacking` to `DraftModifier` + `ToModifier`/`FromModifier` (:78-109).
- `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` -- read/write `periodic_stack_mode` (name-based enum) in `WriteModifier` (:159) + the modifier read keys (:298-314).
- `godot/src/Core/Definitions/AbilityValidator.cs` -- update the `:398` stacked-pulse warning to fire only for `Stack && MaxStacks>1 && PeriodicStacking==None && hasPeriod`; add the "periodic mode has no effect here" warning.
- `godot/src/CreationSuite/AbilityEditorPanel.Advanced.cs` -- `RenderModifierEditor` (:343): the Stacking dropdown auto-includes `StackIndependent`; add a "Periodic stacking" dropdown for `PeriodicStackMode`.
- `godot/src/CreationSuite/AbilityEditorPanel.cs` -- consume `AbilityValidationResult.Warnings` at the Validate/Load call sites (:633/:677/:714) and render them in the status line (:791-800). Closes DW-503.
- Golden pins to re-record: `hero-start-state.golden.txt` (CanonicalModelHash) + `CanonicalModelHash*Tests` + `SimChecksumCoverageGuardTest` ruleset-family pins + `AlgoVersion*Hygiene` tests. NEW `energy-regen-scenario.golden.txt` recorded fresh at SimChecksum v24.

## Tasks & Acceptance

**Execution:**
- `UnitDefinition.cs` -- add `regen_rate` float authored field -- the flat authored regen source (DW-265).
- `EntityWorld.cs` -- add `RegenRate` SoA through the single mapper + `Create`/`Clear` reset -- A2 rule; not folded.
- `EnergyRegenSystem.cs` + `SimulationHost.cs` + `SystemOrderTest.cs` -- new per-tick regen system with the `RegenPerTick` seam, registered before `AbilityCastSystem`, order test re-pinned -- the folded-`Energy` regen path + the 15.21 seam.
- `Modifier.cs` -- `StackIndependent` member, `PeriodicStackMode` enum + folded field + ctor param, `CheckAuthoringBounds` extension -- the stacking vocabulary (DW-264/DW-272).
- `ModifierStore.cs` -- `StackIndependent` install-per-slot arm + periodic scaling (None/Multiply/Repeat) with the cap -- the runtime behavior.
- `EffectContext.cs` + `DirectHpDeltaEffect.cs`/`HealEffect.cs`/`DamageEffect.cs` -- `PulseScale` plumbing + magnitude scaling -- multiply-the-pulse mechanism.
- `EffectCaps.cs` -- `MaxPeriodicStackScale` -- the runaway-protection system cap (RulesetHash re-pin).
- `CanonicalFold.cs` + `CanonicalModelHash.cs` + `EffectFoldCompletenessTests.cs` -- fold `PeriodicStacking`, bump AlgoVersion 14→15 -- determinism completeness.
- `AbilityDraft.cs` + `EffectNodeJsonConverter.cs` -- authoring/JSON round-trip for the new members/field -- data-driven authoring.
- `AbilityValidator.cs` -- corrected + new located warnings -- authoring guidance on the `Warnings` channel.
- `AbilityEditorPanel.Advanced.cs` + `AbilityEditorPanel.cs` -- new dropdown(s) + surface `Warnings` -- authoring UI (DW-503; Godot-coupled).
- Tier-1 test suites -- new xUnit tests for every I/O matrix row (regen, per-stack expiry, periodic None/Multiply/Repeat + cap, recycle, seam) proven RED without the fix; a NEW `energy-regen-scenario` golden recorded fresh -- coverage.
- Re-record `hero-start-state.golden.txt` + re-pin CanonicalModelHash / RulesetHash / hygiene / coverage-guard tests -- hash-family bumps.

**Acceptance Criteria:**
- Given a unit with `max_energy=100`, `regen_rate=2`, and `Energy=40`, when the sim ticks, then `Energy` rises by 2/tick and clamps at 100, observed identically on a golden replay of the new `energy-regen-scenario`.
- Given every shipped scenario (all `regen_rate=0`), when the full golden suite runs, then all EXISTING per-tick `SimChecksum` goldens are byte-identical and `SimChecksum.AlgoVersion` is unchanged (24).
- Given a `StackIndependent` modifier with `MaxStacks=3` applied at three different ticks, when each stack's duration elapses, then each expires on its own timer (independently), reverting one stack's contribution at a time.
- Given a modifier with `PeriodicStacking=None` (the default all shipped content deserializes to), when a stacked periodic pulse fires, then it runs exactly once per period — byte-for-byte with today, and `CanonicalModelHash` changes ONLY because AlgoVersion bumped 14→15 (re-pinned), not because unchanged content re-serialized differently.
- Given `PeriodicStacking=Repeat`/`Multiply` with N stacks, when a pulse fires, then it runs `min(N, MaxPeriodicStackScale)` times / once at ×`min(N, MaxPeriodicStackScale)` magnitude respectively.
- Given the ability editor's advanced modifier card, when it is driven in-engine, then the Stacking dropdown offers 4 members, a Periodic-stacking dropdown offers 3, and an authored footgun surfaces its located `Warnings` text — captured as a quantitative in-engine gate artifact.
- Given `dotnet build godot/godot.csproj`, then it succeeds with 0 errors; Tier-1 xUnit is green (baseline + new tests, no flake beyond the documented `CanonicalModelHashPerf` timing flake).

## Spec Change Log

_No `bad_spec` loopback occurred — the spec held through implementation and one patch pass._

## Review Triage Log

### 2026-08-07 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 2, low 4)
- defer: 0
- reject: 10
- addressed_findings:
  - `[medium]` `[patch]` P1 — `EnergyRegenSystem.Tick` clamped every entity every tick, making the byte-for-byte no-op depend on the unenforced `Energy≤MaxEnergy` invariant; now reads the regen seam once and early-`continue`s when it is 0 → true no-op regardless of state + no per-tick work for shipped content. Golden-neutral (confirmed).
  - `[medium]` `[patch]` P2 — the `EA` save-lane enum inserted `RegenRate` mid-enum (positional layout) without bumping `SaveGameFile.FormatVersion`, so a pre-15.12 v4 save would be silently misread; bumped `FormatVersion` 4→5 so old saves fail-close at the header.
  - `[low]` `[patch]` P3 — `Amount * ctx.PulseScale` ran a Fixed multiply on every non-period effect (identity path); guarded to keep the plain value when `PulseScale` is `One`, removing the new overflow/perf surface off the period path.
  - `[low]` `[patch]` P4 — the inert-periodic-mode validator message hardcoded `max_stacks=1`; interpolated the real value + added the previously-uncovered `Stack`/`max_stacks=1` warning test.
  - `[low]` `[patch]` P5 — Multiply scaling was behavior-tested only for `DamageEffect`; added a `HealEffect` Multiply-scaling test (base×N in one pulse).
  - `[low]` `[patch]` P6 — `CheckAuthoringBounds` now enters the multiply-contribution branch for `StackIndependent` but no bounds test exercised it; added `max_stacks=0` reject + over-ceiling reject rows.
- rejected (not defects): Multiply under-scales a SearchArea/non-magnitude period pulse (SearchArea + ApplyModifier are validator-rejected inside a `period_effect`, so a period is always direct-magnitude leaves — unreachable); `PulseScale==0` sentinel collision (internal-only transient, sole caller uses scale≥2, unreachable); energy-regen golden tail keeps changing (deterministic folded state, proven byte-identical by the two-run test + integer/Fixed-only header, cross-platform-safe); `_pendingWarnings` staleness (every `DoSave` revalidates and sets it before `WriteFile` reads it — unreachable); `_systems[N]` index-cast fragility (works, `SystemOrderTest`-pinned, pre-existing pattern); StackIndependent cap vs same-id persistent slot (persistent instances carry id 0, cannot collide); StackIndependent under ring pressure delivers fewer stacks (by design, documented in the I/O matrix); `RegenRate` save round-trip untested (already covered by the reflection-driven `EntityWorldSaveCompletenessTests`, DW-519); intent-alignment Axis-A/A2 SimChecksum reading + unconditional name-fold (the spec's deliberate, determinism-correct choice, not a divergence).

### 2026-08-07 — Follow-up review pass (status was `done`)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 5: (high 0, medium 0, low 5)
- reject: 10
- addressed_findings:
  - none
- deferred (new ledger entries, all low): DW-886 (grouped `Stack` + `Multiply`/`Repeat` silently truncates the pulse scale below `max_stacks` — reachable to 32767 — while the `EffectCaps` comment falsely claims the cap "never truncates a legitimately-reachable stack count"); DW-887 (`Multiply` is a silent no-op with no warning on a `period_effect` whose leaf ignores `PulseScale`, e.g. `ApplyModifier`, which the validator explicitly permits inside a period — falsifying the prior pass's "period is always direct-magnitude leaves" rejection); DW-888 (period-leaf magnitude × `PulseScale` up to ×8 is unbounded → Fixed int32 wrap at extreme authored amounts); DW-889 (`regen_rate` unvalidated — a negative rate silently drains energy; `regen_rate>0` with `max_energy=0` is silently inert); DW-890 (the new energy-regen golden's cross-platform / NOT-Windows-gated claim rests on the untested premise that the empty-Player2 float AI takes no folded-state action — the ai-active Windows-only golden hazard class).
- rejected (not defects): StackIndependent consuming the shared 8-slot ring (by-design, deterministic refuse, already documented in the I/O matrix); the `EnergyRegenSystem` placement comment (verified defensible — "deterministic either way" is true and nothing between the two systems reads `Energy`; the order is `SystemOrderTest`-pinned); Repeat re-evaluates an RNG/SearchArea period graph per iteration (documentation nuance, deterministic); `RegenRate` save round-trip untested (covered by the reflection-driven `EntityWorldSaveCompletenessTests` exhaustive lane sweep); `_pendingWarnings` staleness (every save path revalidates before `WriteFile` reads it — prior rejection holds); warnings status-line clipping (unverified; the in-engine gate read the warning text amber and verbatim); DW-503 close overclaim (the in-engine gate was RE-VERIFIED live this pass — 4 Stacking items, 3 Periodic-stacking items, located amber warning that clears on a scaling mode — PASS); per-tick full-entity regen scan (negligible, acknowledged pattern, guarded by the seam early-out); intent-alignment DW-503 rendering locus — status line vs advanced modifier card (the status-line rendering is a defensible reading that matches the recorded DW-278/DW-503 closure decision, and the Problem framing "nothing in the ability editor surfaces it" is satisfied); ability-editor warning surfacing has no standing automated test (accepted model for the Godot-coupled layer — behind the mandatory in-engine gate).

## Design Notes

**The regen seam (15.21 dependency).** `EnergyRegenSystem.Tick` iterates ascending id and, for each alive entity, does `world.Energy[id] = Fixed.Clamp(world.Energy[id] + RegenPerTick(world, id), Fixed.Zero, world.MaxEnergy[id])`. `RegenPerTick` is the ONE method that returns the per-tick amount (`=> world.RegenRate[id]` today). Story 15.21 drives regen by editing THIS method to add an attribute-derived contribution — no other tick site reads `RegenRate`. Placement before `AbilityCastSystem` means a unit can cast with newly-regenerated energy the same tick; the placement is fixed and deterministic either way.

**Why per-stack expiry = one slot per stack.** `ModifierStore` already expires each ring slot on its own `_remainingTicks` in `Advance`. Modelling `StackIndependent` as "install a fresh slot per application (same `Modifier.Id`, `_stackCount=1`), bounded by `MaxStacks` same-id slots AND ring capacity" reuses that machinery exactly — no new folded per-stack-timer array, and the fold/checksum contract is unchanged. Grouped `Stack` keeps its single-slot/`_stackCount` model verbatim. The `Apply` same-id scan must NOT merge for `StackIndependent`; count matching slots and install anew, ignoring at cap.

**Multiply vs Repeat are genuinely different and both wanted (Alec, DW-272).** For a `damage` period leaf, Repeat subtracts armor once per hit (N smaller hits); Multiply scales base damage then subtracts armor once (one big hit). `EffectContext.PulseScale` (default `Fixed.One`, so all non-period paths are byte-identical) is multiplied into the additive-magnitude leaves; period pulses are validated to be direct-target leaves, so only `DirectHpDelta`/`Heal`/`Damage` need honor it. `effectiveScale = min(_stackCount, EffectCaps.MaxPeriodicStackScale)` is the runaway cap. For `StackIndependent` each stack is its own slot pulsing once → the pulse count scales naturally and `PeriodicStacking` on such a slot is a no-op (documented).

**Hash bookkeeping (do all re-records in the feature commit, per the 2026-08-06 batch rule — this is a build, not a bounded correction, so it does NOT ride Story 15.22's window):**
- `SimChecksum`: NO bump. `Energy` already folded; default `regen_rate=0` ⇒ existing per-tick goldens byte-identical. New `energy-regen-scenario` recorded fresh at v24.
- `CanonicalModelHash`: 14→15 (new folded `Modifier.PeriodicStacking`, folded unconditionally by name). Re-record `hero-start-state.golden.txt`; re-pin `CanonicalModelHash*Tests`.
- `RulesetHash`: moves (new `EffectCaps` entry). Re-pin the ruleset-family pins in `SimChecksumCoverageGuardTest` and the `AlgoVersion*Hygiene` tests.
- New `StackRule`/`PeriodicStackMode` members are name-folded ⇒ free for content that does not author them.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Modifier` -- expected: green (grouped Stack unchanged; StackIndependent + periodic modes covered).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Energy` -- expected: green (regen clamp, zero-regen no-op, recycle, seam).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` -- expected: all EXISTING per-tick goldens pass unchanged; new `energy-regen-scenario` passes.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~CanonicalModelHash` and `~CoverageGuard` and `~Hygiene` and `~SystemOrder` and `~EffectFoldCompleteness` -- expected: green after the AlgoVersion bumps + re-pins.
- Re-record: `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~HeroStartState` then `dotnet build` and commit; new golden: `CHIMERA_GOLDEN_RECORD=1 dotnet test ... --filter FullyQualifiedName~EnergyRegen`.
- In-engine gate (`/godot-verify`): drive the Ability Editor advanced pane → add an ApplyModifier node → assert the Stacking dropdown item count/labels, the Periodic-stacking dropdown, and a surfaced Warning against the authored draft; append the `### In-Engine Gate` block. Re-record the Windows-only `ai-active` golden ONLY if 15.12 perturbs it.

**Manual checks:**
- Confirm no `resources/data` unit gained a non-zero `regen_rate` (grep) — shipped content must be unchanged.

### In-Engine Gate - 2026-08-07
- surface: Ability Editor → Advanced pane → an `Apply Modifier` effect card's modifier editor (`AbilityEditorPanel.Advanced.cs` `RenderModifierEditor`) — the "Stacking" and new "Periodic stacking" OptionButtons + the status-line warnings surfacing (`AbilityEditorPanel.cs`, DW-503).
- launched: rebuilt `dotnet build godot/godot.csproj` (0 errors, fresh `ProjectChimera.dll`), ran the game over the godot-mcp bridge, located the live `AbilityEditorPanel` at `/root/MainScene/@Node@1566`, opened it, emitted `pressed` on the Advanced pill, set the root card kind to `Apply Modifier` via `item_selected`, then read both OptionButtons via a tree walk and exercised validation through the Show JSON / Apply JSON raw-authoring path (no absolute-mouse click; signals only).
- digest: "Stacking" OptionButton — item_count **4**, items (idx:id:text) `0:0 Refresh`, `1:1 Stack`, `2:2 Ignore`, `3:3 StackIndependent`. "Periodic stacking" OptionButton — item_count **3**, items `0:0 None`, `1:1 Multiply`, `2:2 Repeat`. Multiply selection → Show JSON emitted `"periodic_stack_mode": "Multiply"` (omit-when-None confirmed). Status label on an inert Multiply-on-Refresh-no-period draft: amber `font_color [0.95, 0.80, 0.45]` (== `WarnAmber`), `(2 warnings)`, verbatim: _"ability 'new_targeted_damage'.effect.modifier.periodic_stack_mode: periodic_stack_mode=Multiply has no effect here — it scales a stacked pulse only when stacking is Stack with max_stacks>1 and a live period_effect. This modifier has no live period_effect to pulse, so the pulse behaves as None."_ Stack+max_stacks=3+period+None → `(1 warning)` "...fires ONCE per period regardless of stack count...". Same but Multiply → "Valid — applied to the form." zero warnings, `font_color [0.4, 0.8, 0.45]` (== `OkGreen`).
- asserted: Stacking items expected 4 (`DraftVocabulary.StackRules` = {Refresh, Stack, Ignore, StackIndependent}) vs observed 4 — labels/order identical → PASS. Periodic-stacking items expected 3 (`DraftVocabulary.PeriodicStackModes` = {None, Multiply, Repeat}) vs observed 3 — labels/order identical → PASS. `periodic_stack_mode` drives the authored model (None warns, Multiply clears the footgun warning) — semantic, not cosmetic → PASS. Warnings channel (`AbilityValidationResult.Warnings`) surfaced in the editor status line (DW-503) with verbatim located text → PASS. No editor errors and no `godot_exec` runtime errors on any call.
- result: PASS


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Follow-up review pass over an already-`done`, already-reviewed story (energy regen + `Stack`/`StackIndependent` split + `PeriodicStackMode` None/Multiply/Repeat with a cap + ability-editor `Warnings` surfacing). No code changed this pass — the review ran five parallel lenses (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate), each finding was verified against source, and every surviving finding was low-severity and routed to `defer`. Notably, the prior pass's rejection of the "Multiply on a non-magnitude period leaf" concern was found to rest on a false premise: `AbilityValidator` explicitly permits `ApplyModifierEffect` inside a `period_effect`, so that finding is real and now deferred (DW-887). The in-engine gate was re-driven live and re-confirmed PASS (4 Stacking items, 3 Periodic-stacking items, located amber warning that clears on a scaling mode).

**Files changed:**
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended 5 new canonical entries DW-886…DW-890 (all `status: open`, all low severity); no existing entry touched.
- `_bmad-output/implementation-artifacts/spec-15-12-energy-stack-mechanics.md` — new Review Triage Log entry (defer 5 / reject 10 / patch 0); `status` returned to `done`; `followup_review_recommended: false`.

**Verification:** No code diff produced, so no build/test re-run was warranted; the story's committed state (final_revision `b1c7b3cc`) already carried a green Tier-1 suite and re-recorded hash pins. The binding in-engine gate for the Godot-coupled `AbilityEditorPanel*` surface was independently re-run this pass over the godot-mcp bridge (`dotnet build godot/godot.csproj` succeeded, 0 errors; live editor driven; digests captured) → PASS with no divergence. Three load-bearing review claims were verified directly against source before triage: `MaxAuthorableStacks=32767` vs `MaxPeriodicStackScale=8` (DW-886), `ApplyModifierEffect` reachable inside a period walk (DW-887), and the empty-Player2 energy-regen golden fixture (DW-890).

**Residual risks:** Five deferred low-severity items remain open (DW-886…DW-890) — three periodic-scaling authoring footguns/overflow surfaces, one `regen_rate` validation gap, one cross-platform golden proof gap; none affect shipped content (no shipped unit authors a non-zero `regen_rate` or a non-`None` periodic mode) and none block the story. Residual working-tree artifacts unrelated to this pass: `Snapshot.md` (pre-existing modification, left in place).
