---
title: 'HeroXpSystem — kill-credit XP, leveling, and stat growth at runtime'
type: 'feature'
created: '2026-07-07'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '74d9bca289d37cb754265a64708c4d5c37c88304'
final_revision: 'd1d6aaed272098396f4fc9195812f81fc434966f'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-12-authorable-attack-delivery-flag-hitscan-vs-projectile-per-unit-projectile-speed.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Authored heroes never gain XP. `HeroStore.Level`/`Xp` exist but no system mutates them mid-match; the leveling curve, XP-gain, and stat-growth authored in Story 3.7 (`HeroDefinition`) are dead data no sim system reads. The whole FR-7 persistence rail carries hero level/XP that never changes. This story closes the verified 1.0 gap "hero XP/leveling runtime unowned".

**Approach:** Add a deterministic `HeroXpSystem` to the sim tick that, on each enemy unit death, credits the victim's `xp_bounty` (a new authored field, default derived from cost) to every hostile hero within that hero's authored XP-share radius; advances the hero's level against its authored geometric curve; and applies per-level stat growth through the existing `ModifierStore` as a permanent, non-dispellable modifier source. Turn on the per-tick `SimChecksum` fold of `HeroStore` mutable state (one `AlgoVersion` bump, reserving Story 3.14's revival fields) and re-baseline the goldens. Harvest real end-of-match level/XP into the deployed `PlayerProfile` so the hero picker shows grown values through the manifest gate.

## Boundaries & Constraints

**Always:**
- Sim-layer determinism is sacred: `Fixed` (16.16) only, **no `float`/`double`/`Mathf`/`Math.*`/`System.Random`/wall-clock** in any new sim code; the `float` authoring fields on `HeroDefinition`/`UnitDefinition` are quantized to `Fixed` at the single load boundary (mirror `UnitDefinition.MaxEnergy`), never inside a tick.
- Process kills and heroes in a deterministic order: drain deaths in recorded (ascending-entity-id combat) order; iterate heroes via `HeroStore.FoldOrder()` (ascending `HeroId.Value`).
- Any new per-unit SoA field derived from `UnitDefinition` (the victim `XpBounty`) MUST be written through the single `EntityWorld.ApplyUnitDefinition` mapper (A2 rule) — never hand-copied in a spawn path — and reset to its `Create()` default in the bulk-clear/recycle block.
- Hero per-instance runtime constants that the runtime needs (curve params, growth deltas, share radius) are set in `HeroStore.Mint` (every live field written there — the SoA-recycle contract); a recycled slot must carry none of the prior hero's state.
- Stat growth goes through the **folded** `ModifierStore.Apply` (permanent = `DurationTicks < 0`), NEVER through the unfolded `ModifierSystem.AccumulateBonus` (bypassing the store mutates unhashed sim truth → desync).
- One `AlgoVersion` bump (10→11) covering all new folded state; declare and fold Story 3.14's reserved `HeroStore` revival fields (`HeroStore.cs:82-92`) in this same bump so 3.14 needs no second bump. Re-baseline every golden and re-pin the coverage-guard known-state hash + `AlgoVersion` assertions in the same commit.
- Presentation/sim separation holds: the editor controls, picker, and MainScene harvest live in the UI/presentation layer; the sim layer stays Godot-free.
- Max-level clamp is total and exception-free: at `MaxLevel`, further XP is ignored (saturated, no `Fixed` overflow, no throw).

**Block If:**
- The reserved 3.14 field set at `HeroStore.cs:82-92` cannot be declared/folded coherently in this bump (e.g. its documented shape conflicts with the fold) — HALT `blocked`, do not invent a divergent revival schema.

**Never:**
- No revival/death-persistence behavior (Story 3.14), no items/inventory (3.15), no ability unlock-on-level behavior (signature/ultimate abilities stay authoring-only this story — only the numeric leveling + stat growth are runtime).
- No new stat channels: growth uses only the four stats `ModifierStore` supports (max-health, attack-damage, move-speed, armor); attack-range/attack-speed have no modifier channel and are out of scope.
- No online persistence rail (Epic 9). Offline rail only.
- Do not add a per-entity definition-index array to `EntityWorld`; reach runtime-needed def values via the dedicated SoA copies (victim `XpBounty` on `EntityWorld`, hero curve/growth on `HeroStore`).
- Do not remove or repurpose the shipped `HeroDefinition.XpPerKill` field in this story (see Design Notes D5) — the runtime is victim-`xp_bounty` driven; the redundant hero field's cleanup is deferred, not done here.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Kill in range | Enemy unit dies; one hostile hero alive within its `xp_share_radius` of the death position | Hero `Xp += victim XpBounty`; level advances if thresholds crossed | No error |
| Kill out of range | Enemy dies; nearest hostile hero beyond its `xp_share_radius` | No XP granted to that hero | No error |
| Shared kill | Enemy dies; two hostile heroes both in range | Each gains the full `XpBounty` (proximity credit, not split) | No error |
| Friendly/own death | A unit dies whose faction == a hero's faction | That hero gains no XP (only hostile deaths credit) | No error |
| Dead hero | Hero entity not alive / `EntityId` link stale | Skipped; grants no XP, applies no growth | No error |
| Default bounty | Victim `xp_bounty` omitted | Bounty = derived from cost (`CostOre + CostCrystal`), quantized to `Fixed` | No error |
| Level-up growth | Hero crosses a curve threshold | Level increments; growth reconciled to `Level-1` stacks of the permanent growth modifier via `ModifierStore.Apply` | No error |
| Deploy at level N>1 | Hero minted from a saved profile at level 5 | First `HeroXpSystem` tick reconciles growth to 4 stacks (catch-up) | No error |
| Max level | Hero at `MaxLevel` earns more XP | Xp saturates at the max-level ceiling; no further level-up; no `Fixed` overflow | Clamp, no throw |
| Invalid authoring | `xp_bounty`/`xp_share_radius`/`*_per_level` non-finite or out of `[0, Range)` | AR-39 validator fails closed with a located badge; blocks Save/Playtest | Located field error |
| End-of-match | Match ends with a grown hero; persistence engaged | Picker reads real end-of-match level/XP via the manifest gate | No error |
| Playtest discard | Return to Edit (`PersistenceTestMode` off) after growth | Grown XP discarded — store re-minted from the authored profile | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `int? XpBounty` (JSON `xp_bounty`, default null) + `ResolveXpBounty()` (authored value else `CostOre + CostCrystal`). Cost fields at `:64`/`:68`; `IsHero` `:217`; `Hero` `:251`.
- `godot/src/Core/Definitions/HeroDefinition.cs` -- add `float XpShareRadius` (`xp_share_radius`, default e.g. `12f`), `float HealthPerLevel`/`DamagePerLevel`/`ArmorPerLevel` (`*_per_level`, default `0f`); extend `Clone()`. Leave `XpPerKill` untouched (D5).
- `godot/src/Core/Definitions/HeroLevelingPresets.cs` -- extend the `Curve` record + presets to carry the new growth/share fields (so presets stay complete authored bundles). Confirm `Detect` still round-trips.
- `godot/src/Core/EntityWorld.cs` -- add `Fixed[] XpBounty` SoA (alloc in ctor; default in `Create`; set in `ApplyUnitDefinition` via `def.ResolveXpBounty()` quantized; clear in the bulk `Array.Clear` reset). Establish the entity→hero link `HeroIndex[entityId] = heroes.PackRef(slot)` — see D8 (wired at mint, presentation side).
- `godot/src/Core/HeroStore.cs` -- **declare** the Story 3.13 mutable/constant columns and the reserved 3.14 fields per `:82-92`: growth-tracking `int[] GrowthStacksApplied`; per-hero curve constants `int[] MaxLevelOf`, `Fixed[] BaseXpOf`, `Fixed[] XpGrowthOf`, `Fixed[] XpShareRadiusOf`, `Fixed[] HealthPerLevelOf`, `Fixed[] DamagePerLevelOf`, `Fixed[] ArmorPerLevelOf`; plus 3.14 `bool[] Alive3_14`, `bool[] AwaitingRevival`, `Fixed[] RevivalTimer`, `int[] RevivalLink`. Widen `Mint` to write every one (curve constants + zeroed growth/revival state). Update `Clear`.
- `godot/src/Combat/DeathFeed.cs` -- NEW. A host-owned, per-tick transient ring buffer of `DeathRecord { FixedVec3 Position; Faction Faction; Fixed Bounty; }`. Drained each tick → NOT folded (empty at checksum time, like `CombatEventQueue`). `Push(...)`, `Count`, `Get(i)`, `Clear()`. Cap + silent-drop mirror `CombatEventQueue`.
- `godot/src/Combat/DamageResolver.cs` -- thread an optional `DeathFeed?` through `DamageContext` and `KillEntity` (alongside `Events`/`Stats`); at `KillEntity` (`:83`, BEFORE `world.Destroy`) record `{world.Position[id], world.FactionOf[id], world.XpBounty[id]}` into the feed.
- `godot/src/Combat/CombatSystem.cs` -- hold a `DeathFeed` ref; pass it into every `DamageContext` it builds (`:613-614`) so hitscan + projectile-spawn-time contexts carry it.
- `godot/src/Combat/ProjectileSystem.cs` / `godot/src/Combat/ProjectileStore.cs` -- ensure the projectile impact's `DamageContext` carries the `DeathFeed` (thread the ref through the same channel the projectile already carries `Events`/killer-faction on).
- `godot/src/Combat/HeroXpSystem.cs` -- NEW `ISimSystem`. `Tick`: (1) drain `DeathFeed`; for each death, for each live hero in `FoldOrder()` on a faction != victim faction, whose entity is alive and link-valid, within `XpShareRadiusOf`, add `Bounty` to `Xp`; (2) advance `Level` against the geometric curve with saturation + `MaxLevel` clamp; (3) reconcile growth: desired stacks = `Level-1`, apply the delta via `ModifierStore.Apply` (permanent, `StackRule.Stack`, `MaxStacks >= HeroLevelMax-1`), update `GrowthStacksApplied`. Clear the feed at end. Stateless (deps: `HeroStore`, `ModifierStore`, `DeathFeed`).
- `godot/src/Core/Sim/SimulationHost.cs` -- construct `DeathFeed`; insert `HeroXpSystem` at index 8 (AFTER `ProjectileSystem` `:7`), shifting Supply/Fog/AI/ScenarioDirector; update the tick-order doc-blocks (`:14-20`, `:123-160`) and the system-count string; thread `HeroStore` into `EnableChecksums` (`:155`).
- `godot/src/Core/SimulationLoop.cs` -- if `SystemOrderTest`/loop asserts a fixed count, update it.
- `godot/src/Core/SimChecksum.cs` -- bump `AlgoVersion` 10→11 (`:97`); add a v11 fold block: iterate `heroes.FoldOrder()`, fold live-count then per slot `HeroId.Value` (two mixes, low/high per `:266-268`), `Level` (int), `Xp.Raw`, `GrowthStacksApplied` (int), and the reserved 3.14 fields (`Alive3_14`/`AwaitingRevival` as int, `RevivalTimer.Raw`, `RevivalLink`) at their defaults; also fold `world.XpBounty[i].Raw` in the entity loop (D2). Thread `HeroStore` into `Compute` (optional param, keep legacy callers). Add v11 summary + history doc entries.
- `godot/src/Multiplayer/ServerChecksumCollector.cs` (or wherever `Compute` is invoked server-side) -- pass `HeroStore` through.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- add `xp_bounty` rule (finite, `[0, Range)`, when authored); add hero-block rules for `xp_share_radius` (finite, `[0, Range)`) and `*_per_level` (finite, `[0, Range)`), following the existing hero range-rule pattern (`:273+`).
- `godot/src/Core/Definitions/FactionWriter.cs` -- write-back (omit-on-default) for `xp_bounty` and the new hero fields; ensure `CloneUnit`/`WriteHero` copy them.
- `godot/src/CreationSuite/UnitCardPanel*.cs` -- expose `xp_bounty` (with tooltip) on the unit; expose `xp_share_radius` + the three `*_per_level` fields in the hero-only (Promote-to-Hero) section; route through EditorHistory (Ctrl+Z); `CloneUnit` copies them.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- `LoadInto` (`:50`): resolve the deployed hero's `UnitDefinition.Hero` and pass curve/growth/share params into the widened `Mint`; establish `EntityWorld.HeroIndex` link (D8). `BuildProfile` (`:80`) already accepts `(level, xp)` — no change to its shape.
- `godot/src/UI/HeroPickerOverlay.cs` -- rewire fresh-Save (`:371`, currently hardcoded `level:1, xp:0`) and Overwrite (`:395`, currently static profile values) to source the harvested end-of-match values through `manifest.DeriveProfileShape()` (D6). Card readouts (`:285`/`:292`) already read `PlayerProfile` — no change.
- `godot/src/UI/MainScene.cs` -- at match end (before `HeroStore` is cleared) harvest live `HeroStore.Level`/`Xp` for the deployed hero into a `SceneContext` result the picker Save reads; the existing preserve-snapshot at `:1188-1194` and discard branch at `:1244` are the seam (D6). Reset/discard behavior unchanged.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` -- add `XpBounty` to `CombatDef()` (off `Create` default) + teeth assertions in both guard tests.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- move the known-state pin + `AlgoVersion` assert to v11 (rename method); add a HeroStore Level/Xp/GrowthStacks "teeth" helper (analogous to the modifier/rally-point fold-teeth).
- `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` -- `ExpectedSimChecksumAlgoVersion` 10→11 (`:52`).
- `godot/ProjectChimera.Sim.Tests/Golden/HeroXpScenario.cs` + `HeroXpGoldenTests.cs` + `hero-xp-scenario.golden.txt` + csproj `<EmbeddedResource>` -- NEW golden pair: a fixed-seed scenario with a deployed hero that kills enemies in range, crosses ≥1 level threshold, and applies growth (proves XP+level+growth fold end-to-end, integer/Fixed-only, cross-platform safe). Mirror `DeliveryScenario`/`DeliveryGoldenTests` structure.
- `godot/ProjectChimera.Sim.Tests/Combat/HeroXpTests.cs` -- NEW direct oracles (Godot-free) for the I/O matrix: in-range grants, out-of-range no-grant, shared full-credit, friendly-no-credit, dead-hero-skip, default-bounty-from-cost, level-up growth via ModifierStore (assert a stat delta materialized in `Effective*`), deploy-at-N catch-up, max-level clamp (no overflow/throw), and the discard-drops-a-grown-run test.
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitDefinitionValidatorTests.cs` + `FactionWriteRoundTripTests.cs` -- new cases for `xp_bounty` and the three hero fields.
- All existing golden tests -- re-baseline after the fold (Verification recipe).

## Tasks & Acceptance

**Execution:**
- `HeroDefinition.cs` / `UnitDefinition.cs` / `HeroLevelingPresets.cs` -- add the authored fields (`xp_bounty`, `xp_share_radius`, `*_per_level`), `ResolveXpBounty()`, extend `Clone`/presets.
- `EntityWorld.cs` -- add `XpBounty` SoA (alloc/Create-default/`ApplyUnitDefinition`-set/bulk-clear); wire the `HeroIndex` link helper.
- `HeroStore.cs` -- declare the growth/curve/share columns + reserved 3.14 fields; widen `Mint` to write them all; update `Clear`.
- `DeathFeed.cs` -- create the transient per-tick death buffer.
- `DamageResolver.cs` / `CombatSystem.cs` / `ProjectileSystem.cs` / `ProjectileStore.cs` -- thread `DeathFeed` through the damage/kill path; record victim `{position, faction, bounty}` at `KillEntity` before `Destroy`.
- `HeroXpSystem.cs` -- create the system (credit → level → growth-reconcile), inserted at tick index 8.
- `SimulationHost.cs` / `SimulationLoop.cs` -- construct `DeathFeed`, register `HeroXpSystem`, update order docs/count + `SystemOrderTest`, thread `HeroStore` into checksums.
- `SimChecksum.cs` (+ server collector) -- v11 fold (HeroStore mutable + reserved-3.14 + `XpBounty` entity fold) + docs; thread `HeroStore`.
- `UnitDefinitionValidator.cs` / `FactionWriter.cs` -- validation + write-back + clone for the new fields.
- `UnitCardPanel*.cs` -- editor fields (unit `xp_bounty`; hero-section share/growth) with tooltips, undo-routed.
- `HeroProfileLoader.cs` / `HeroPickerOverlay.cs` / `MainScene.cs` -- mint curve params + HeroIndex link; harvest end-of-match values; rewire picker Save/Overwrite off the D-5 placeholders.
- Tests -- guard-test coverage for `XpBounty`; validator + round-trip cases; NEW `HeroXpTests` oracles for the I/O matrix; NEW `HeroXpScenario` golden with its embed pair; re-pin the coverage-guard v11 hash + `AlgoVersion` asserts; re-baseline every golden.

**Acceptance Criteria:**
- Given a hero and an enemy unit that dies within the hero's `xp_share_radius`, when the death is processed, then the hero's `Xp` increases by the victim's `XpBounty` (authored, else derived from cost), processed deterministically in ascending-id order, and a hero on the victim's own faction gains nothing.
- Given a hero whose accumulated `Xp` crosses one or more authored curve thresholds (`BaseXp × XpGrowth^(level-1)`), when `HeroXpSystem` ticks, then `Level` advances accordingly and per-level stat growth (`HealthPerLevel`/`DamagePerLevel`/`ArmorPerLevel`) is applied via `ModifierStore.Apply` as a permanent, non-dispellable modifier — total growth = `(Level-1)` stacks — observable in the hero's `Effective*` stats.
- Given a hero at `MaxLevel`, when further XP is earned, then it is clamped/saturated deterministically with no additional level-up, no `Fixed` overflow, and no exception.
- Given the XP runtime first mutates `HeroStore.Level`/`Xp` mid-match, when `SimChecksum` computes, then `Level`/`Xp`/`GrowthStacksApplied` (and the reserved 3.14 fields) fold in `FoldOrder()` order under a single `AlgoVersion` bump (10→11), the goldens are re-baselined, and the coverage-guard known-state hash + `AlgoVersion` assertions are re-pinned to 11 in the same commit; two consecutive normal runs are byte-identical.
- Given faction JSON omitting the new fields, when it loads, then every shipped unit keeps current behavior with zero authoring errors (non-hero units have no share/growth; heroes default to zero growth and the default share radius); an out-of-range/non-finite `xp_bounty`, `xp_share_radius`, or `*_per_level` fails closed through AR-39 with a located badge that blocks Save/Playtest.
- Given the Unit Card Editor, when I open a unit, then `xp_bounty` is exposed with a tooltip; opening a Promoted hero additionally exposes `xp_share_radius` and the three `*_per_level` fields; all route through EditorHistory (Ctrl+Z) and persist to faction JSON.
- Given a match that ends with a grown hero and persistence engaged, when I open the hero picker, then the slot card shows the real end-of-match level/XP (routed through the manifest shape); and given a return-to-Edit playtest reset with `PersistenceTestMode` off, the grown values are discarded (store re-minted from the authored profile).

## Design Notes

**D1 — Proximity credit keyed on death position, not attacker id.** The projectile path snapshots only the killer *faction* at spawn (the attacker entity id is gone by impact), and `EntityWorld.Destroy` is immediate (the victim slot is recycled inside the same combat tick), so a system running after combat cannot observe the attacker or the corpse. Both problems are solved by recording `{position, faction, bounty}` into a `DeathFeed` at the single death choke point (`DamageResolver.KillEntity`, before `Destroy`), then crediting every hostile hero within its own `xp_share_radius` of that position. This is uniform for hitscan/projectile/self-lethal deaths and fully deterministic (recorded order + `FoldOrder`). We iterate the ≤64 heroes directly (not `SpatialHash`) — the hero set is tiny and `SpatialHash` returns cell-grouped, non-ascending order; direct hero iteration is both simpler and more order-deterministic than the gap note's `SpatialHash` suggestion.

**D2 — Fold surface + one bump.** Bump `AlgoVersion` 10→11. Fold the mutable `HeroStore` state (`Level`, `Xp`, `GrowthStacksApplied`) plus Story 3.14's reserved fields (declared now, folded at defaults) in one bump per `HeroStore.cs:82-92`. Also fold the def-derived per-entity `XpBounty` directly, following the Story 3.12 D2 spawn-constant-folding convention (`AttackRange`/`SplashRadius`/`Delivery` are all folded though their effect is transitive) — this makes the re-baseline uniform and gives the field coverage-guard teeth. `GrowthStacksApplied` converges to `Level-1` each tick but is folded anyway (it is genuine mutable sim state).

**D3 — Growth via stacked permanent modifier; reconcile by counter.** Each level of growth is one stack of a permanent (`DurationTicks < 0`) `Modifier` with `StackRule.Stack`, deltas `= (HealthPerLevel, DamagePerLevel, MoveSpeed=0, ArmorPerLevel)`, a reserved `Modifier.Id`, and `MaxStacks >= HeroLevelMax-1` (so no saturation for valid levels). `ModifierStore.Apply` cannot resize an existing instance, so growth is applied incrementally: each tick, `apply(Level-1 - GrowthStacksApplied)` more stacks, then set `GrowthStacksApplied = Level-1`. This is idempotent and covers BOTH the deploy-at-level-N catch-up (first tick: 0→N-1) and mid-match level-ups (one stack per level). Never call `ModifierSystem.AccumulateBonus` directly — it is unfolded. Recompute lands next tick via the dirty flag (the standard install-now/recompute-next-tick lag).

**D4 — Fixed-safe curve + max-level clamp.** Thresholds `BaseXp × XpGrowth^(level-1)` and accumulated `Xp` are `Fixed`; the validator allows extreme authored values (`MaxLevel` up to 100, `XpGrowth` up to 100) that would overflow 16.16. Compute thresholds and accumulate `Xp` with saturation at a defined ceiling (e.g. `Fixed.FromInt(30000)`), and once `Level == MaxLevel` stop accumulating (cap `Xp` at the max-level threshold). This guarantees "clamped, no overflow, no exception".

**D5 — `XpPerkill` (3.7) is superseded, not removed.** The epics AC is victim-centric: the amount is the *victim's* `xp_bounty`. `HeroDefinition.XpPerKill` (hero-centric, shipped in 3.7) is the wrong home for the same concept and is left untouched here (removing it would perturb 3.7's validator/editor/writer/tests). The runtime does not consume it. Log a deferred-work item to reconcile/remove it; do not silently wire it.

**D6 — End-of-match harvest.** `HeroStore` is cleared on return-to-Edit, so live grown values must be captured *before* the reset. Harvest the deployed hero's live `Level`/`Xp` (found by `HeroId = MintId(PendingHeroProfile)`, the same lookup as `MainScene.cs:1186-1190`) into a `SceneContext` result at match end; the picker's Save/Overwrite reads that through `manifest.DeriveProfileShape()` → `BuildProfile`, replacing the D-5 placeholders (`HeroPickerOverlay.cs:371`/`:395`). The discard branch (`MainScene.cs:1244`) already re-mints authored values, so "discard now drops real values" needs no behavior change — only a test proving a *grown* run is dropped (today discard/preserve coincide because nothing grows).

**D7 — Default bounty formula.** `ResolveXpBounty()` = authored `xp_bounty` if set, else `CostOre + CostCrystal` (both int gold-equivalents), quantized to `Fixed` at the load boundary. Simple, data-driven, overridable.

**D8 — Establish the entity→hero link at mint.** `EntityWorld.HeroIndex` is currently never populated (only reset to `HERO_NONE`). Set `HeroIndex[entityId] = heroes.PackRef(slot)` when a hero is minted (in the `LoadInto` mint path, presentation side) so the XP system can ABA-safely validate that a `HeroStore` row's `EntityId` still points at the same live hero (via `TryResolveRef`) before crediting — guarded by the existing `RecycledSlot_CarriesNoPriorHeroLink` reset test.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean build, no determinism-analyzer (CHM*) or banned-float violations in the new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green, including `HeroXpTests`, `HeroXpGoldenTests`, `ApplyUnitDefinitionGuardTest`, `SimChecksumCoverageGuardTest` (v11), `VersionStampConsistencyTests` (11), `UnitDefinitionValidatorTests`, `FactionWriteRoundTripTests`, and every existing `*GoldenTests`.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then `dotnet build` then a clean `dotnet test` -- expected: goldens regenerated once (headers self-stamp v11), then stable byte-identical across two consecutive normal runs.

**Manual checks:**
- Open the Unit Card Editor (or `/godot-verify`): a unit shows `xp_bounty`; a Promoted hero shows `xp_share_radius` + the three `*_per_level` fields; an invalid value badges the field and blocks Save; Ctrl+Z reverses an edit. In a playtest, a hero kills an enemy in range, its level/XP advance, and its stats grow.

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 1, medium 6, low 3)
- defer: 3: (high 0, medium 2, low 1)
- reject: 9: (high 0, medium 0, low 9)
- addressed_findings:
  - `[high]` `[patch]` Derived `xp_bounty` overflow (Blind Hunter #1 / Edge Case #2): the authored value is validated `[0,32768)` but the derived default `CostOre+CostCrystal` (sum ≤65534) overflowed `Fixed.FromInt` to a NEGATIVE bounty, bypassing the fail-closed guard. `ResolveXpBounty()` now clamps to `[0, XpBountyMax=32767]`. +2 tests.
  - `[medium]` `[patch]` XP-credit one-sided clamp (Blind Hunter #2 / Edge Case #1): `Xp + Bounty` was a raw `Fixed` add that could wrap negative (a near-ceiling Xp, or ≥2 deaths/tick) before the `>XpCeiling` check. Now a widened `long` add saturates `[0, XpCeiling]`. +1 test.
  - `[medium]` `[patch]` Share-radius `r*r` overflow (Edge Case #3): `xp_share_radius` validated to the generic Range overflowed the runtime's squared-distance test. Tightened the validator to `[0, HeroShareRadiusMax=128)`. +cap tests.
  - `[medium]` `[patch]` Growth-delta accumulation overflow (Edge Case #4): `*_per_level × up-to-99 stacks` overflowed `Effective*` (→ negative health → instant death). Tightened the validator to `[0, HeroStatGrowthMax=256)`. +cap tests.
  - `[medium]` `[patch]` Ability + self-lethal kills fed no XP and the `KillEntity` "uniform" doc was false (Blind Hunter #3 / Verification-Gap #4): threaded `DeathFeed` through `EffectContext`→`AbilityCastSystem`→`DamageEffect` and the self-lethal `KillEntity`, so ability-delivered kills grant XP exactly like auto-attacks (honors AC1's generality). +1 test.
  - `[medium]` `[patch]` Projectile-kill XP was unverified (Verification-Gap #1): a revert of the `ProjectileSystem`→`DeathFeed` threading kept all tests green. Added a projectile-kill oracle asserting the death is recorded.
  - `[medium]` `[patch]` `LoadInto` HeroIndex link was unverified (Verification-Gap #2): deleting the link line left deployed heroes never leveling with tests green. Added a `LoadInto(world)` oracle asserting the link resolves and the hero earns XP.
  - `[low]` `[patch]` `AdvanceLevels` ran for dead-entity heroes (Blind Hunter #4): a hero whose entity died kept leveling from banked XP. Gated the whole level+grow step on `IsLiveLinkedHero`.
  - `[low]` `[patch]` Golden fixture set `EffectiveAttackDamage` without `BaseAttackDamage` (Blind Hunter #6): growth recompute (`Effective=Base+Σ`) discarded the authored 100. Set `BaseAttackDamage` so growth is modeled faithfully; re-recorded `hero-xp-scenario.golden.txt`.
  - `[low]` `[patch]` Stale doc claiming signature/ultimate abilities unlock "by 3.13" (Blind Hunter #8): unlock is explicitly out of 3.13 scope. Corrected the `HeroDefinition` comments.
  - Deferred (3): (a) the shipped hero-centric `HeroDefinition.XpPerKill` is superseded by victim `xp_bounty` yet still live in validator/editor/writer — a misleading authoring knob (Intent-Alignment Div2 / D5); (b) the end-of-match harvest → picker-persist (`MainScene`/`HeroPickerOverlay`, Tier-2 Godot-coupled) has no automated coverage — extract a Godot-free harvest seam (Verification-Gap #3); (c) `Effective*` overflow from a pathological large Base + capped growth is the pre-existing unsaturated `ModifierSystem` accumulation class (Edge Case #4 residual).
  - Rejected (9, all low): kill-attribution-vs-proximity credit (Intent-Alignment Div1 — the project's WC3 design reference distributes XP by proximity to the dying enemy, not to the killer; the model is intent-aligned and the radius is authorable); flat-scalar vs level-indexed deltas (Div3 — flat matches WC3 and the `ModifierStore` flat-only channel); death-drain order not id-sorted (Div4 — XP add is commutative, drain order deterministic, hero iteration is ascending-`HeroId`); `FoldOrder` allocates twice/tick (Blind Hunter #5 — spec-sanctioned, negligible at ≤64 heroes); `DeathFeed` 256/tick cap (Blind Hunter #7 — deterministic, beyond realistic single-tick kill volume); overshoot XP retained at max level (Blind Hunter #9 — harmless deterministic residual); `PlacedHero` defaulted-ctor footgun (Blind Hunter #10 — deliberate compat; only `ScenarioApplier` constructs in production); 1-tick growth lag (Blind Hunter #11 — informational, deterministic); loaded `Level` not clamped to `MaxLevel` (Edge Case — requires hand-corrupted local persisted data, outside the authoring gate).

### 2026-07-08 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 1, medium 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 11: (high 0, medium 0, low 11)
- addressed_findings:
  - `[high]` `[patch]` XP-range check overflowed on realistic maps (Blind Hunter #1 / Edge Case #1): the hero↔death squared distance in `HeroXpSystem.cs:71` is uncapped by map size (coords up to ±`map_bounds`; default 120 → ~240u span), so a single-axis gap past ~181u overflowed the int32 truncation inside `Fixed`'s `*`/`+` (`SqrDistance = X²+Y²+Z²`) and wrapped NEGATIVE — reading as "in range" and crediting kills across the whole map, fully defeating `xp_share_radius`. (The prior pass hardened only the RHS `r*r` via the radius cap; the LHS actual distance was still exposed.) Rewrote the range test in long-widened raw units (shift-then-sum, never truncates) — pure integer/deterministic, byte-identical to the Fixed path for in-range distances (all 18 goldens stable). +1 regression oracle (`FarOffMapDeath_…`, death at 240u/radius 10 must grant nothing — RED before the fix), closing the companion test gap (Blind Hunter #2).
  - `[medium]` `[patch]` ScenarioApplier hero curve/growth capture was value-unverified (Verification-Gap #1): `ScenarioApplier` is the single production float→Fixed boundary for a deployed hero's 7 curve/growth params (`MaxLevel`/`BaseXp`/`XpGrowth`/`XpShareRadius`/`*PerLevel`); the only test asserted `UnitId`/`EntityId`, so a dropped or transposed field would ship heroes that level/grow wrong with CI green. Added a Tier-1 oracle asserting all 7 captured `PlacedHero` fields equal `Fixed.FromFloat(authored)` for a distinctly-valued hero def.
  - Deferred (1, NEW ledger entry): hero growth is tracked by a per-row count (`GrowthStacksApplied`), not modifier-presence, so Story 3.14 revival onto a fresh entity would silently get zero growth unless 3.14 resets the counter — a binding obligation for 3.14 (Blind Hunter #3). Three further findings re-surfaced but are ALREADY in the ledger from the initial 3.13 review and were NOT re-appended (per orchestrator instruction): the base+growth `Effective*` overflow class (Edge Case #2 = existing entry), and the Godot-coupled end-of-match harvest → picker Save/Overwrite coverage gap (Verification-Gap #2/#3 + Intent-Alignment Div2 = existing entry).
  - Rejected (11, all low): optional-defaulted `Mint` curve params (BH#4 — all production sites updated; consistent with the prior PlacedHero-footgun reject); proximity-vs-kill-credit + alliance-unaware exclusion (BH#5 / Intent Div1 — WC3-aligned, alliances out of scope, prior-rejected); `DeathFeed` empty-at-checksum doc imprecision (BH#6 — harmless, the feed is unfolded so safety holds regardless); max-health growth does not heal current Health (BH#7 — intent mandates no current-HP behavior; deterministic); building kills award no XP (BH#8 — intent scopes to "enemy unit death"); `DeathFeed` 256/tick cap (BH#9 — prior-rejected, deterministic); `HeroGrowthModifierId` collision asserted-not-enforced (BH#10 — distinctive high constant, no authored id reaches it); per-death ceiling truncation (BH#11 — prior-rejected class, harmless deterministic residual); drain-order-not-id-sorted (Intent Div3 — commutative add, prior-rejected); Unit Card `xp_bounty` getter(resolved)/setter(raw) asymmetry (Verification-Gap other — deliberate "show effective value" display, setter fires only on user edit, no data corruption).

## Auto Run Result

Status: done
Blocking condition: none

### Summary

Implemented the deterministic `HeroXpSystem` runtime (tick index 8): drains a per-tick `DeathFeed` of recorded kills, credits each hostile hero within its authored `xp_share_radius` the victim's `xp_bounty` (proximity model — WC3-aligned), advances level against the authored geometric curve (saturating, max-level clamped), and reconciles per-level stat growth as `Level-1` stacks of a permanent, non-dispellable `ModifierStore` modifier. Folded the mutable `HeroStore` state + `XpBounty` into `SimChecksum` (AlgoVersion 10→11, reserving Story 3.14's revival fields), re-baselined the goldens, and wired the end-of-match harvest so the hero picker shows real grown level/XP through the manifest gate.

### Review findings

- Patches applied: 10 (1 high overflow fix + 4 further overflow-class fixes + ability-path XP threading + 2 verification-gap tests + dead-hero level gate + golden fidelity + stale doc). See Review Triage Log.
- Deferred: 3 (XpPerKill cleanup, harvest testability seam, pre-existing modifier effective-stat overflow) → `deferred-work.md`.
- Rejected: 9 (all low; see triage log).

### Verification

- `dotnet build godot/godot.sln` — clean, 0 errors, no determinism-analyzer violations.
- `dotnet test godot/ProjectChimera.Sim.Tests` — 966 passed, 1 skipped, 1 failed. The single failure is the pre-existing, unrelated `ProceduralMapGeneratorTests` platform-hash tripwire (a `ScenarioSerializer` hash untouched by this story; documented failing on baseline 74d9bca in the 3.12 auto-run). All 15 `HeroXpTests` + `HeroXpGoldenTests` (byte-identical across two runs) + coverage-guard v11 + validator/round-trip cases green.
- `hero-xp-scenario.golden.txt` re-recorded (v11) after the base-stat fidelity fix.

### Residual risks

- The end-of-match harvest + picker Save/Overwrite rewire and all editor UI (new fields, tooltips, Ctrl+Z, invalid-value badges) are verified by a clean compile + pattern-consistency, not a live in-engine session (headless environment). The determinism-critical XP/level/growth/fold path is fully covered by the golden + direct oracles.
- Follow-up review recommended: the review pass made substantial cross-cutting changes (a high-severity overflow fix, four overflow-class validator/runtime fixes, and ability-path threading through the shared effect executor), warranting an independent pass.

### Follow-up review pass (2026-07-08)

The recommended independent follow-up ran (4 parallel layers: Blind Hunter, Edge Case, Verification-Gap, Intent-Alignment). It found one HIGH correctness defect the prior pass had left half-fixed, plus one headless-testable verification gap; both patched.

- **Files changed (follow-up):**
  - `godot/src/Combat/HeroXpSystem.cs` — rewrote the XP-share range test (`:71`) in long-widened raw units so the hero↔death squared distance can no longer overflow int32 and wrap negative on realistic-sized maps (was silently crediting kills across the whole map, defeating `xp_share_radius`).
  - `godot/ProjectChimera.Sim.Tests/Combat/HeroXpTests.cs` — +1 regression oracle (`FarOffMapDeath_DoesNotOverflowRangeCheck_GrantsNothing`).
  - `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` — +1 oracle asserting the applier's 7 captured hero curve/growth `Fixed` fields (the single production float→Fixed boundary).
- **Findings:** 2 patched (1 high range-check overflow, 1 medium verification-gap test); 1 newly deferred (revival zero-growth obligation for Story 3.14 → `deferred-work.md`); 11 rejected (all low). Three re-surfaced findings were already in the ledger from the initial review and were NOT re-appended.
- **Verification:** `dotnet build godot/godot.sln` clean (0 errors, no determinism-analyzer violations). `dotnet test godot/ProjectChimera.Sim.Tests` — **968 passed** (2 new tests added), 1 skipped, 1 failed (the same pre-existing, unrelated `ProceduralMapGeneratorTests` platform-hash tripwire; this story's diff touches no procedural-map/serializer code). All 18 goldens byte-identical (the long-widened range test is identical to the Fixed path for every in-range distance), coverage-guard v11 green, both new oracles green.
- **Residual risk:** the range-check fix is small, self-contained, and pinned by a deterministic RED-before/GREEN-after regression test, with the full golden suite confirming zero collateral. A brief confirmatory pass is reasonable given the finding was high-severity, but the risk surface is minimal.
