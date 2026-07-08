---
title: 'Hero death & revival'
type: 'feature'
created: '2026-07-08'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: 'a251f2fec6f2429880ca58a88da0a73fe429c446'
final_revision: 'ebc5f36b710531294d7b1f72bc49bbf977850415'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** When an authored hero dies today it is frozen forever: `EntityWorld.Destroy` recycles its entity, `ModifierStore.ClearEntity` wipes its growth modifiers, but its `HeroStore` row stays `Alive` with a dangling `EntityId` and stops gaining XP/growth (`IsLiveLinkedHero` fails). There is no way to bring it back — hero death is effectively permanent deletion of a unit the player invested a whole match progressing. Story 3.13 declared and folded four reserved revival fields (`Alive3_14`/`AwaitingRevival`/`RevivalTimer`/`RevivalLink`) but no system reads them.

**Approach:** On hero-entity death, transition the persisted `HeroStore` row into an "awaiting revival" state using the already-folded reserved fields (identity + level/XP retained, row not recycled), and announce the fall via `CombatEventQueue`. Let the player order a revival from any building flagged `revives_heroes` through the shared `OrderApplier`/Train (2.8) path — spending an authored, level-scaled cost at the exec-tick behind ownership + affordability guards — which starts a deterministic countdown. When the timer expires, respawn the hero at the building through the existing single spawn path with retained level/XP and an authored HP fraction, resetting `GrowthStacksApplied` so per-level growth re-applies onto the fresh entity. When revival is disabled for the scenario, a fallen hero dies like any unit and its persisted attributes still finalize per FR-7a. No `AlgoVersion` bump (the four fields are already folded at their defaults).

## Boundaries & Constraints

**Always:**
- Sim-layer determinism is sacred: `Fixed` (16.16) only, **no `float`/`double`/`Mathf`/`Math.*`/`System.Random`/wall-clock** in any new sim code. Authored revival numbers (cost/time/HP-fraction curves) are quantized to `Fixed` at the single load boundary (mirror the Story 3.13 `PlacedHero` curve capture / `ApplyUnitDefinition`), never inside a tick.
- Iterate heroes via `HeroStore.FoldOrder()` (ascending `HeroId.Value`); the revival state machine, countdown, and respawn all run inside the existing hero-runtime system at tick index 8 (`HeroXpSystem`) — **do not add a new tick-order slot** (`SystemOrderTest` must stay green).
- The revival order rides the shared `OrderApplier.Apply` path so **all three apply sites** (live `LockstepManager`, `ReplayPlayer`, offline `CommandCardSystem`) get identical behavior; it is dispatched **before** the entity-ownership guard (the Train/SetRally building-command convention) and **spends at the exec-tick** with a building-ownership guard (`FactionOf[buildingId] == expectedFaction`) + check-both-then-debit-both affordability (`CanAffordOre`/`CanAffordCrystal` → `SpendOre`/`SpendCrystal`).
- On respawn, reset `HeroStore.GrowthStacksApplied[slot] = 0`, re-point `HeroStore.EntityId[slot]` to the new entity, and set `EntityWorld.HeroIndex[newEntityId] = heroes.PackRef(slot)` — the **binding obligation** carried forward from the Story 3.13 follow-up review (else the revived hero silently fights with level-1 stats).
- Reuse the single unit-spawn path (`world.Create` + `world.ApplyUnitDefinition` + MeshType, as `ScenarioApplier.SpawnUnit` does) for respawn — never duplicate `ApplyUnitDefinition`. The revived hero keeps its identity, `Level`, and `Xp`; only its entity is new.
- Presentation/sim separation holds: the `revives_heroes` editor toggle and the revive command-card buttons live in the UI layer; the sim stays Godot-free.
- Every authoring surface is validator-gated: `revives_heroes` and the revival curves fail closed with a located error that blocks Save/Playtest; omitted fields default so every shipped scenario/unit keeps current behavior.

**Block If:**
- The revival state machine cannot be expressed with the four reserved fields (`HeroStore.cs:110-116`) and would require a **new folded `HeroStore` field** — that forces an `AlgoVersion` 11→12 bump + full golden re-baseline, which this story's premise ("fields reserved to avoid a second version bump") forbids. HALT `blocked`; do not bump silently.

**Never:**
- No items/inventory drop-on-death or pickup (Story 3.15) — the death event exists but drops nothing.
- No `AlgoVersion` bump and no re-baseline of the 20 existing goldens (they never exercise revival, so their folded values are unchanged).
- No online persistence rail (Epic 9); offline only.
- No new scenario-settings editor panel — the revival rule is authorable data with sensible defaults; only the per-building `revives_heroes` capability gets an editor toggle.
- No win-condition / defeat-detection changes (a dead-but-awaiting hero's entity is already gone from `EntityWorld`).
- No refund complexity: if the revive building is destroyed mid-countdown the revival is cancelled deterministically with no gold refund (defined edge, D8).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Hero dies, revival enabled | A live hero's entity is killed; scenario `revival_rule.enabled` (or default) true | Row transitions: `Alive3_14=false`, `AwaitingRevival=true`, `RevivalTimer=0`, `RevivalLink=NONE`; slot **not** recycled; `HeroFell` pushed to `CombatEventQueue` at the death position | No error |
| Hero dies, revival disabled | Hero entity killed; `revival_rule.enabled=false` | Hero leaves the field (`Alive3_14=false`) but is **not** awaiting revival; `HeroStore.Alive[slot]` stays true so end-of-match harvest still persists its Level/Xp (FR-7a) | No error |
| Revive order, valid | Building alive, owned by hero's faction, `RevivesHeroes[b]`; hero awaiting & owned; affordable | Cost for `Level` spent once; `RevivalTimer = time(Level)`, `RevivalLink = PackRef(building)`; countdown begins | No error |
| Revive order, unaffordable | Same but faction can't afford the level-scaled cost | Order rejected, nothing spent; `OrderDenied` event | No spend |
| Revive order, not owner / not revive-building / hero not awaiting / already counting | Ownership mismatch, building lacks `RevivesHeroes`, hero on field, or `RevivalLink != NONE` | Order rejected, nothing spent (anti-cheat: hero and building must both belong to `expectedFaction`) | No spend |
| Countdown completes | `AwaitingRevival` hero with `RevivalLink` set; timer reaches ≤0; building still alive | New entity spawned at the building via the shared spawn path; `Health = MaxHealth × revive_hp_fraction`; `EntityId`/`HeroIndex` re-linked; `GrowthStacksApplied=0`; `AwaitingRevival=false`, `Alive3_14=true`; `HeroRevived` event; next tick re-applies `Level-1` growth stacks | No error |
| Revive building destroyed mid-countdown | `RevivalLink` building no longer alive before timer expiry | Countdown cancelled: `RevivalTimer=0`, `RevivalLink=NONE`, hero stays `AwaitingRevival` (can be re-ordered elsewhere); no refund | No error |
| Retained progression | Level-N hero with grown stats dies and is revived | Respawned hero has the same `HeroId`/`Level`/`Xp`; its `Effective*` stats return to the grown values within one tick (growth re-reconciled) | No error |
| Match ends while awaiting | Persistence engaged; hero awaiting or counting at match end | Harvest reads the alive row's real `Level`/`Xp` through the manifest shape (row is still `Alive`) | No error |
| Invalid authoring | `revives_heroes` on a non-Structure unit, or non-finite / out-of-range cost/time/`revive_hp_fraction` | AR-39 / scenario validator fails closed with a located badge; blocks Save/Playtest | Located field error |

</intent-contract>

## Code Map

- `godot/src/Core/EntityWorld.cs` -- add `UnitCommand.ReviveHero = 14` after `SetRally = 13` (`:32`) with the frozen-value replay comment; enum must stay ≤ `0x3F` (bits 6-7 are wire flags).
- `godot/src/Core/HeroStore.cs` -- add two **non-folded per-hero constants**: `UnitDefinition?[] SourceDef` (for respawn) and `Faction[] OwnerFaction` (for revive-order ownership). Widen `Mint(...)` with optional `UnitDefinition? sourceDef = null, Faction ownerFaction = default` and write them (recycle contract); extend `Clear()` to reset both. No new folded field — the four reserved revival fields (`:110-116`) already exist and are folded.
- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `[JsonPropertyName("revives_heroes")] public bool RevivesHeroes { get; set; } = false;` (mirror `IsHero` `:229`).
- `godot/src/Core/Definitions/ScenarioData.cs` -- add nullable `[JsonPropertyName("revival_rule")] RevivalRule? RevivalRule` (mirror `persistence_manifest` `:189-191`, omit-when-null → byte-identical goldens).
- `godot/src/Core/Definitions/RevivalRule.cs` -- **NEW** `sealed class RevivalRule` (mirror `PersistenceManifest`): `bool Enabled = true`; int `CostOreBase`/`CostOrePerLevel`/`CostCrystalBase`/`CostCrystalPerLevel`; float `TimeBaseSeconds`/`TimePerLevelSeconds`/`ReviveHpFraction`; a `Clone()`. Linear level curves (`base + perLevel × Level`). A `RevivalRule.Default` for the omitted/null case.
- `godot/src/Core/RevivalRuleRuntime.cs` -- **NEW** small readonly struct of `Fixed` curve params resolved once from `RevivalRule` at the single load boundary (`CostOre(level)`, `CostCrystal(level)`, `TimeSeconds(level)`, `HpFraction`); the sim-facing, `Fixed`-only form injected into `BuildingSystem` + `HeroXpSystem`.
- `godot/src/Core/BuildingStore.cs` -- add `bool[] RevivesHeroes` SoA (mirror `SupplyBonus[]`), default false in `Create`, cleared on recycle, included in `Clear()`. Non-folded placement constant.
- `godot/src/Economy/BuildingSystem.cs` -- thread the `revives_heroes` flag through the placement path (`PlaceBuildingDirect` / `Create`) into `BuildingStore.RevivesHeroes`; add `ReviveHeroCommand(int buildingId, Faction expectedFaction, Fixed heroSlotRaw)` mirroring `TrainUnitCommand` (`:388`): building alive+owned+`RevivesHeroes` guard, resolve `heroSlot`, validate hero `Alive`+`AwaitingRevival`+`OwnerFaction==expectedFaction`+`RevivalLink==NONE`, level-scaled affordability (check-both-then-debit-both), then set `RevivalTimer`/`RevivalLink` on the hero row. Inject `HeroStore` + `RevivalRuleRuntime`.
- `godot/src/Multiplayer/NetworkCommand.cs` -- in `OrderApplier.Apply`, add `if (cmd == UnitCommand.ReviveHero) { buildings?.ReviveHeroCommand(o.UnitId, expectedFaction, o.TargetX); return; }` alongside Train/SetRally (`:139-153`), before the entity guard. No serialization change (`TickCommandPacket` is command-type-agnostic).
- `godot/src/Combat/DamageResolver.cs` -- at `KillEntity` (`:88`, before `world.Destroy`), push a hero-death announcement: `if (world.HeroIndex[id] != EntityWorld.HERO_NONE) events?.Push(CombatEventType.HeroFell, world.Position[id], world.FeedbackProfile[id]);` (position is still valid pre-Destroy; no `HeroStore` dependency).
- `godot/src/Combat/CombatEventQueue.cs` -- append `HeroFell` and `HeroRevived` to `CombatEventType` (`:26`); append-only, golden-safe (not folded).
- `godot/src/Combat/HeroXpSystem.cs` -- extend the existing per-hero `FoldOrder()` loop (the reserved TODO at `:102`): (a) **death detection** — a live hero row with `Alive3_14 && !IsLiveLinkedHero` transitions to awaiting-revival (enabled) or off-field (disabled); (b) **countdown** — an `AwaitingRevival` hero with `RevivalLink != NONE`: cancel if the linked building is dead, else decrement `RevivalTimer` by `dt`, and on ≤0 **respawn** (spawn via injected spawn fn at building pos, set `Health = MaxHealth × HpFraction`, re-link `EntityId`/`HeroIndex`, `GrowthStacksApplied=0`, clear revival state, push `HeroRevived`). Inject `BuildingStore`, `RevivalRuleRuntime`, a `Func<UnitDefinition, Faction, Fixed, Fixed, int>` spawn delegate, and `CombatEventQueue`.
- `godot/src/Core/Sim/SimulationHost.cs` -- construct `RevivalRuleRuntime` from the applied scenario; wire the new deps into the `HeroXpSystem` ctor (`:160`) and into `BuildingSystem`; wire the spawn delegate to the existing `ScenarioApplier.SpawnUnit` path. Ctor-signature changes only — **no tick-order change**.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` / `godot/src/Builder/ScenarioApplier.cs` -- pass the hero's `UnitDefinition` and `Faction` into the widened `HeroStore.Mint` (extend `PlacedHero` to carry them); nothing else changes in the init mint/link sequence.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- add a `revives_heroes` coherence rule (only valid on a Structure-category building; located error otherwise), following the `is_hero`↔`hero` coherence pattern (`:299-310`).
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- validate `revival_rule` fail-closed when present: costs finite & `[0, Range)`, times finite & `[0, Range)`, `revive_hp_fraction` finite & in `(0, 1]` (Fixed-safe), located.
- `godot/src/UI/CommandCardSystem.cs` -- gate a revive affordance on `BuildingStore.RevivesHeroes[bId]`; render one revive button per awaiting hero of the faction; wire `IssueReviveCommand(bId, heroSlot)` mirroring `IssueTrainCommand` (`:341`) — `EnqueueOrder(bId, UnitCommand.ReviveHero, Fixed.FromRaw(heroSlot), Fixed.Zero)` online, `OrderApplier.Apply(..., buildings: _buildSys)` offline.
- `godot/src/CreationSuite/UnitCardPanel*.cs` -- expose a `revives_heroes` toggle (with tooltip, undo-routed via EditorHistory) in the building/Structure section; `CloneUnit` copies it.
- `godot/src/Core/Definitions/FactionWriter.cs` -- write-back (omit-on-default) for `revives_heroes`; `CloneUnit`/round-trip copy it.
- `godot/ProjectChimera.Sim.Tests/Combat/HeroRevivalTests.cs` -- **NEW** Godot-free oracles (mirror `HeroXpTests.MakeHero`): death→awaiting transition; disabled→dies-like-unit (no awaiting, row stays Alive); revive order via `OrderApplier`/`BuildingSystem.ReviveHeroCommand` (ownership + affordability guards, unaffordable rejects, already-counting rejects); countdown→respawn with retained Level/Xp + HP fraction + `GrowthStacksApplied` reset re-materializing `Effective*` growth; building-destroyed-mid-countdown cancel; the four reserved fields fold correctly.
- `godot/ProjectChimera.Sim.Tests/Golden/HeroRevivalScenario.cs` + `HeroRevivalGoldenTests.cs` + `hero-revival-scenario.golden.txt` + csproj `<EmbeddedResource>` pair -- **NEW** golden: fixed-seed scenario where a hero dies, a revive is ordered, the countdown elapses, and the hero respawns — proving the reserved-field fold end-to-end, integer/Fixed-only, byte-identical across two runs.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- extend `AssertHeroStoreFoldedIntoChecksum` (`:397`) with coverage teeth for the four reserved fields (`Alive3_14`/`AwaitingRevival`/`RevivalTimer`/`RevivalLink`) — additive only; `AlgoVersion` stays 11, known-state pin `0x0AF691CA` unchanged.
- `godot/ProjectChimera.Sim.Tests/Definitions/*` -- validator cases (`revives_heroes` coherence, `revival_rule` range/fraction) + `FactionWriteRoundTripTests` case for `revives_heroes`.

## Tasks & Acceptance

**Execution:**
- `EntityWorld.cs` -- add `UnitCommand.ReviveHero = 14` (frozen-value comment).
- `RevivalRule.cs` / `RevivalRuleRuntime.cs` / `ScenarioData.cs` -- author-time rule (nullable + `Default`) and its `Fixed`-resolved runtime form; linear level curves.
- `UnitDefinition.cs` / `BuildingStore.cs` / `BuildingSystem.cs` (placement) -- `revives_heroes` flag → `BuildingStore.RevivesHeroes` SoA at placement.
- `HeroStore.cs` -- add `SourceDef`/`OwnerFaction` non-folded constants; widen `Mint`/`Clear`; keep the reserved-field resets.
- `HeroProfileLoader.cs` / `ScenarioApplier.cs` -- capture and pass hero `UnitDefinition` + `Faction` into `Mint` (extend `PlacedHero`).
- `NetworkCommand.cs` / `BuildingSystem.cs` -- `ReviveHeroCommand` (pre-guard dispatch + exec-tick ownership/affordability/spend + start countdown), mirroring the 2.8 Train pattern.
- `HeroXpSystem.cs` / `SimulationHost.cs` -- death detection + countdown + respawn in the index-8 loop; wire deps + spawn delegate; no tick-order change.
- `DamageResolver.cs` / `CombatEventQueue.cs` -- `HeroFell`/`HeroRevived` announcements.
- `UnitDefinitionValidator.cs` / `ScenarioValidator.cs` / `FactionWriter.cs` -- validation + write-back for the new fields.
- `CommandCardSystem.cs` / `UnitCardPanel*.cs` -- revive command-card buttons + `revives_heroes` editor toggle.
- Tests -- new `HeroRevivalTests` oracles for the whole I/O matrix; new `HeroRevivalScenario` golden pair; reserved-field coverage teeth; validator + round-trip cases. Confirm the 20 existing goldens and the coverage-guard/version pins stay green untouched.

**Acceptance Criteria:**
- Given a scenario with revival enabled (authored or default) and a live hero, when the hero's entity is killed, then its `HeroStore` row is retained (identity + Level + Xp) and transitions to an awaiting-revival state via the reserved fields, and a hero-death event is announced on `CombatEventQueue` — the slot is never recycled.
- Given an eligible building (alive, owned by the hero's faction, `revives_heroes`) and an awaiting hero, when a revive order is issued from its command card, then it rides `OrderApplier.Apply` (identical across live/replay/offline), spends the level-scaled cost exactly once at the exec-tick behind the building-ownership + affordability guards, and starts a deterministic countdown; an unaffordable, non-owner, non-revive-building, or already-counting order spends nothing.
- Given an awaiting hero whose countdown reaches zero with its revive building still alive, when `HeroXpSystem` ticks, then the hero respawns at the building through the shared spawn path with retained Level/Xp and `Health = MaxHealth × revive_hp_fraction`, `EntityId`/`HeroIndex` are re-linked, `GrowthStacksApplied` is reset to 0, and within one further tick its per-level stat growth re-materializes in `Effective*`.
- Given revival is disabled for the scenario, when a hero dies, then it leaves the field like any unit (no awaiting state) yet its `HeroStore` row stays `Alive` so its manifest-persisted attributes still finalize at match end per FR-7a.
- Given the reserved revival fields now mutate mid-match, when `SimChecksum` computes, then they fold in `FoldOrder()` order under the **existing** `AlgoVersion` 11 (no bump), the 20 existing goldens remain byte-identical, and a new revival golden reproduces byte-identically across two consecutive runs.
- Given the authoring surfaces, when `revives_heroes` is set on a non-Structure unit or a `revival_rule` cost/time/`revive_hp_fraction` is non-finite/out-of-range, then the validator fails closed with a located badge that blocks Save/Playtest; omitting all new fields leaves every shipped unit and scenario behaving exactly as before.

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 2, medium 4, low 5)
- defer: 3: (high 0, medium 1, low 2)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - `[high]` `[patch]` Feature was DEAD end-to-end (Verification-Gap #1): `revives_heroes` was added to `PlaceBuildingDirect`/`Create` but no production caller passed it — `ScenarioApplier.Apply` and the player-build path never resolved `UnitDefinition.RevivesHeroes`, so no real building ever gained the capability (tests were green only because they set the flag directly). Added `BuildingSystem.ResolveRevivesHeroes` (via the canonical `TechTreeChecker.BuildingTypeId`→def-id map) wired into `PlaceBuildingDirect` and `QueueWorkerBuild`. +`PlaceBuildingDirect_ResolvesRevivesHeroesFromFactionDef` oracle.
  - `[high]` `[patch]` Cost-curve overflow → free-money (Edge Case #1): each cost field was validated non-negative but the composed `base + perLevel × level` at max level could exceed 16.16, wrapping NEGATIVE so `CanAffordOre` passes and `SpendOre` ADDS resources. `ScenarioValidator` now rejects any cost/time curve that reaches the range ceiling at `MaxRevivableLevel`; `RevivalRuleRuntime` computes curves in long-widened saturating raw units (defense-in-depth). +2 validator oracles.
  - `[medium]` `[patch]` Time-curve overflow → instant revive (Edge Case #2): same class as the cost overflow; same validator + saturation fix. +1 validator oracle.
  - `[medium]` `[patch]` `revive_hp_fraction` quantize-to-zero → dead-on-arrival (Edge Case #3): a positive fraction (e.g. 1e-5) passed the raw `(0,1]` float check but quantized to `Fixed.Zero`. The validator now also rejects the QUANTIZED value ≤ 0. +1 oracle.
  - `[medium]` `[patch]` Missing under-construction guard (Blind Hunter #1): `ReviveHeroCommand` lacked the `IsUnderConstruction` guard `TrainUnitCommand` has, so a still-constructing revive building could be charged. Added the guard (exec-tick is the trust boundary, not the UI). +oracle.
  - `[medium]` `[patch]` Revive HP fraction applied to BASE not GROWN max (Blind Hunter #3): a level-N hero revived at the fraction of its base HP (growth lags one tick), under-delivering the authored fraction. `RespawnHero` now computes the grown max (`base + (Level-1)×HealthPerLevel`, saturated) so the fraction is of the hero's actual max. Test updated + revival golden re-recorded (no other golden moved).
  - `[low]` `[patch]` Def-less awaiting hero pay-for-nothing loop (Edge Case #4 / Blind Hunter #2): a hero with no `SourceDef` could be ordered revived, spending resources every time yet never respawning. `ReviveHeroCommand` now rejects the order (no spend) when `SourceDef == null`. +oracle.
  - `[low]` `[patch]` Crystal-cost branch never exercised (Verification-Gap #2): every fixture used crystal cost 0, so a wrong-resource debit would ship green. +`ReviveOrder_ChargesLevelScaledCrystal…` and `…AffordableOre_UnaffordableCrystal_SpendsNothing` oracles.
  - `[low]` `[patch]` Per-level time term never exercised (Verification-Gap #3): every fixture used `TimePerLevelSeconds=0` though the default rule ships 2s/level. +`ReviveOrder_CountdownScalesWithLevel_PerLevelTimeTerm` oracle.
  - `[low]` `[patch]` World-full respawn dropped a paid revival (Edge Case #5): `RespawnHero` cancelled on spawn-fail, forcing a re-pay. Now it keeps the building link and re-attempts next tick at no extra cost.
  - `[low]` `[patch]` Stale revive buttons on the construction early-return (Blind Hunter #4): `CommandCardSystem` hid train but not revive buttons before returning. Added `HideReviveButtons()`.
  - Deferred (3): (a) items dropped on revival once Story 3.15 adds entity-side inventory — binding obligation for 3.15 (Intent-Alignment); (b) revive buttons absent on a dual producer+reviver building — presentation-only, headless-unverifiable (Intent-Alignment / Blind Hunter); (c) AC3 manifest-finalize e2e untested — pre-existing Godot-coupled harvest seam (Verification-Gap / Intent-Alignment). All → `deferred-work.md`.
  - Rejected (10, all low): HeroId-vs-raw-slot wire (Train precedent, no mid-match hero-row recycle), worker-respawn determinism trap (heroes are never Workers), UI cost-display `(int)` truncation (costs are whole integers → exact), first-tick spurious death (production always links the same tick), `RevivalRule.Default` unvalidated (its constants are provably in-range), already-counting silent reject (UI-prevented; anti-cheat-silent is consistent), disabled-fold-path unverified end-to-end (fold covered by the reserved-fields oracle + the disabled transition test), residual leak-path test omissions (now added), hero-slot ABA (D-7: no mid-match hero-row recycle), and revive-button overflow past `MAX_TRAIN_OPTIONS` (few heroes; UX).

### 2026-07-08 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 1: (high 0, medium 0, low 1)
- reject: 12: (high 0, medium 0, low 12)
- addressed_findings:
  - `[medium]` `[patch]` Revive HP fraction not honored after growth re-materializes (Blind Hunter #3/#4 + Intent-Alignment R-D): `RespawnHero` set `Health = grownMax × fraction` at the respawn instant, but `ReconcileGrowth`'s next-tick per-stack heal (`ModifierStore.ApplyStatDeltas` adds `+HealthPerLevel` for each of the `Level-1` growth stacks) then stacked on top, so the SETTLED HP of a revived leveled hero drifted far ABOVE the authored fraction (a level-3 @ 0.5 settled at 80/120 = 67%; a level-10 @ 0.5 settled near full) — the authored `revive_hp_fraction` knob was effectively dead for the exact case revival exists for (heroes that leveled up). Fixed: `RespawnHero` now reconciles growth IN the respawn tick, then sets `Health = EffectiveMaxHealth × HpFraction`, landing the settled HP at exactly `fraction × grown max` (next-tick `ReconcileGrowth` becomes a no-op). This also removes the duplicated grown-max formula flagged by Blind Hunter #3 (uses the already saturated + stack-capped `EffectiveMaxHealth` as the single source). Deterministic; test asserts the settled value is stable across the growth tick; the new revival golden was re-recorded once (v11) — the 20 existing goldens are byte-identical.
  - `[low]` `[patch]` Player-built revive building had zero coverage (Verification-Gap #1): `QueueWorkerBuild` correctly threads `ResolveRevivesHeroes(type, faction)` into `BuildingStore.Create`, but only the sibling `PlaceBuildingDirect` path was tested — a dropped 4th arg would kill revive on player-CONSTRUCTED buildings while every scenario-placed test stayed green. Added `QueueWorkerBuild_ResolvesRevivesHeroesFromFactionDef` (Godot-free).
  - `[low]` `[patch]` Real combat-death → awaiting transition untested end-to-end (Intent-Alignment (a)): the announce half (`KillEntity` → `HeroFell`) and the transition half (`world.Destroy` + tick → awaiting) were each tested in isolation; no test drove the actual combat surface into the state machine. Added `HeroDies_ThroughKillEntity_ThenTick_TransitionsToAwaiting`.
  - Deferred (1): CommandCardSystem doesn't filter null-`SourceDef` awaiting heroes → a (production-unreachable) null-def live hero would show a priced revive button that silently no-ops with no `OrderDenied` cue. Presentation-only, headless-unverifiable, no resource loss (sim guards reject with no spend). → `deferred-work.md`.
  - Rejected (12, all low by consequence): hero-slot ABA raw-slot wire (re-affirm D-7 — no mid-match hero-row recycle; `OwnerFaction` re-checked); no-refund on building razed mid-countdown (intended, D-8); world-full respawn "stranded forever" (intended retry, degenerate at `MAX_ENTITIES`); non-combat hero death announces no `HeroFell` (uncertain reachability, presentation cue); `OwnerFaction` non-folded (set once from deterministic scenario data → peer-identical); revive-button cap past `MAX_TRAIN_OPTIONS` (prior-rejected; UX); revivals spawn at building center / stacking (gameplay feel, presentation); `ScenarioValidator.MaxRevivableLevel=100` decoupled from `HeroLevelMax` (latent; runtime `LinearSat` saturates safely); disabled-revival `Alive=true` rows fold forever (intended for persistence, D-7; capped at `MAX_HEROES`); mid-match `Enabled` flip not re-checked in countdown (`RevivalRuleRuntime` resolved once at load — unreachable); `HpFraction` runtime clamp asymmetry (validator is the fail-closed gate); `Configure` with an unvalidated rule (only the in-range `Default` reaches it).

## Design Notes

**D1 — Death detection is a scan, not a `DeathFeed` drain.** `DeathFeed` records only `{position, faction, bounty}` (no entity/hero id), so a drained death cannot be mapped back to a hero row. Instead reuse the entity↔hero link already present in `HeroXpSystem`: a live hero row with `Alive3_14 == true && !IsLiveLinkedHero(world, slot, EntityId[slot])` means its entity died (either `!IsAlive`, or the slot was recycled and `HeroIndex` no longer resolves to it). This is deterministic (`FoldOrder`), needs no death position, and slots naturally into the reserved TODO at `HeroXpSystem.cs:102`. The *announcement* (which does want the death position) is pushed separately at `DamageResolver.KillEntity` where `world.Position[id]` is still valid and `world.HeroIndex[id] != HERO_NONE` cheaply identifies a hero — no `HeroStore` threading into the static kill path.

**D2 — No `AlgoVersion` bump.** The four reserved fields are unconditionally folded per live hero at `SimChecksum.cs:304-307`. Activating them only changes the *values* hashed at runtime, not the fold set/order, so `AlgoVersion` stays 11, the known-state pin `0x0AF691CA` is unchanged (empty hero store), and the 20 existing goldens (none exercise revival) stay byte-identical. Adding any *new* folded `HeroStore` field would force v12 — hence `SourceDef`/`OwnerFaction` are deliberately **non-folded** per-hero constants (like the existing curve constants), and `BuildingStore.RevivesHeroes` is a non-folded placement constant.

**D3 — The `GrowthStacksApplied` reset is load-bearing.** `HeroXpSystem.ReconcileGrowth` early-returns when `Level-1 <= GrowthStacksApplied`, and `ModifierStore.ClearEntity` wiped the growth modifier off the dead entity while the count persisted on the row. Without `GrowthStacksApplied[slot] = 0` at respawn, the revived hero reconciles nothing and fights at base stats. Re-linking `EntityId`/`HeroIndex` then lets the next tick re-apply `Level-1` stacks onto the new entity (idempotent, gated on the live link) — this is exactly the deferred obligation captured in the 3.13 follow-up review.

**D4 — Revival order == the 2.8 Train pattern.** `ReviveHero` names a *building* (`UnitId = buildingId`, hero slot packed in `TargetX` via `Fixed.FromRaw`), is dispatched before the entity-ownership guard, and executes in `BuildingSystem.ReviveHeroCommand` with the same 3-line ownership guard + check-both-then-debit-both spend as `TrainUnitCommand`. Because it never becomes a `CommandState`, it does not touch `ApplyActiveOrder` or the order queue. All three apply sites already pass a `BuildingSystem`, so live/replay/offline parity is automatic. Awaiting heroes don't recycle their slot, so the raw slot index in the wire order stays valid between issue and exec; ownership is still re-checked (`OwnerFaction[slot] == expectedFaction`) as anti-cheat.

**D5 — Respawn reuses the single spawn path.** Store the hero's `UnitDefinition` on the row (`SourceDef`) at mint; on revival, spawn a fresh entity through the same `world.Create + ApplyUnitDefinition + MeshType` path `ScenarioApplier.SpawnUnit` uses (injected as a `Func` for Godot-free testability — never duplicate `ApplyUnitDefinition`), then override `Health` to the HP fraction and re-link. Faction for the spawn comes from the revive building (`BuildingStore.FactionOf`), which equals the validated `OwnerFaction`.

**D6 — State machine over the four fields.** `NONE` sentinel for `RevivalLink` is `-1` (default 0 is a valid building `PackRef`, so death-transition sets `-1`). States: on-field = `Alive3_14=true`; dead-no-order = `AwaitingRevival=true, RevivalLink=NONE`; counting = `AwaitingRevival=true, RevivalLink=building, RevivalTimer>0`. `RevivalTimer` decrements by the shared `Fixed dt` each tick (deterministic across peers even though `dt` isn't an exact 1/30s); respawn fires when it reaches ≤0 with `RevivalLink != NONE`.

**D7 — Disabled branch is minimal.** Nothing destroys hero rows today, so a disabled-revival hero's row stays `Alive` after its entity dies and the existing end-of-match harvest (gated on `heroes.Alive[slot]`) still snapshots its grown Level/Xp — FR-7a "finalize" needs no new persistence code. Set `Alive3_14=false` (off field) but leave `AwaitingRevival=false`.

**D8 — Building lost mid-countdown = cancel, no refund.** If `TryResolveRef(RevivalLink)` fails during the countdown, cancel deterministically (`RevivalTimer=0`, `RevivalLink=NONE`, stay `AwaitingRevival`) so the player can re-order elsewhere. No refund — storing the committed cost would need a fifth folded field (v12 bump), which is out of bounds. Defined, deterministic, documented.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean build, no determinism-analyzer (CHM*) or banned-float violations in the new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green, including `HeroRevivalTests`, `HeroRevivalGoldenTests`, `SimChecksumCoverageGuardTest` (v11, reserved-field teeth), `VersionStampConsistencyTests` (still 11), the validator/round-trip cases, and every existing `*GoldenTests` (byte-identical, no re-baseline).
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then `dotnet build` then a clean `dotnet test` -- expected: only the new `hero-revival-scenario.golden.txt` is recorded (v11 header); the 20 existing goldens are unchanged and stable byte-identical across two consecutive normal runs.

**Manual checks:**
- Open the Unit Card Editor (or `/godot-verify`): a Structure building shows a `revives_heroes` toggle (tooltip, Ctrl+Z reverses); setting it on a non-Structure unit badges the field and blocks Save. In a playtest, kill a hero → a fall is announced and the hero stops fighting; select a `revives_heroes` building → its command card offers to revive the fallen hero; issuing it spends the cost and, after the timer, the hero respawns at the building at the authored HP fraction with its level/XP and grown stats intact.

## Auto Run Result

Status: done
Blocking condition: none

### Summary

Implemented hero death & revival. On a hero-entity death a scan in the index-8 hero-runtime system (`HeroXpSystem`) transitions the persisted `HeroStore` row into the already-folded reserved revival fields (`Alive3_14`/`AwaitingRevival`/`RevivalTimer`/`RevivalLink`) — identity + Level/Xp retained, slot never recycled — and `DamageResolver.KillEntity` announces the fall on `CombatEventQueue`. The player revives at any building flagged `revives_heroes` via a new `UnitCommand.ReviveHero` that rides the shared `OrderApplier` (all three apply sites) and spends a level-scaled cost at the exec-tick behind ownership + affordability guards (the 2.8 Train pattern). A deterministic countdown respawns the hero at the building through the shared spawn path with retained Level/Xp, the authored HP fraction (of the hero's grown max), and `GrowthStacksApplied` reset so per-level growth re-materializes onto the fresh entity. Revival disabled → the hero dies like any unit (row stays Alive for FR-7a harvest). No `AlgoVersion` bump (the four fields were reserved and folded by Story 3.13); the 20 existing goldens are byte-identical.

### Files changed

- `godot/src/Core/EntityWorld.cs` — `UnitCommand.ReviveHero = 14` (frozen-value replay comment).
- `godot/src/Core/Definitions/RevivalRule.cs` (NEW) — authored per-scenario rule (nullable, `Default`, linear level curves).
- `godot/src/Core/RevivalRuleRuntime.cs` (NEW) — sim-facing `Fixed`-resolved curves; saturating `LinearSat` (overflow defense).
- `godot/src/Core/Definitions/ScenarioData.cs` — nullable `revival_rule` block (omit-when-null).
- `godot/src/Core/Definitions/UnitDefinition.cs` — `revives_heroes` building flag.
- `godot/src/Core/BuildingStore.cs` — `RevivesHeroes[]` SoA (non-folded placement constant).
- `godot/src/Economy/BuildingSystem.cs` — `ReviveHeroCommand` (ownership + under-construction + capability + affordability + def-present guards, check-both-then-debit-both, countdown start, OrderDenied cue); `ResolveRevivesHeroes` wired into `PlaceBuildingDirect` + `QueueWorkerBuild`.
- `godot/src/Multiplayer/NetworkCommand.cs` — `ReviveHero` dispatch before the entity guard (all three apply sites), threading `events`.
- `godot/src/Combat/DamageResolver.cs` — `HeroFell` announce at `KillEntity`.
- `godot/src/Combat/CombatEventQueue.cs` — `HeroFell`/`HeroRevived` event types (golden-safe).
- `godot/src/Combat/HeroXpSystem.cs` — death detection + countdown + respawn (grown-max HP fraction, re-link, `GrowthStacksApplied=0`, world-full retry).
- `godot/src/Core/Sim/SimulationHost.cs` / `ScenarioApplier.cs` — runtime construction/wiring, spawn hook, `revives_heroes` + def/faction threading into placed-hero + `Mint`.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` — `PlacedHero` carries def+faction into the widened `Mint`.
- `godot/src/Core/HeroStore.cs` — non-folded `SourceDef`/`OwnerFaction`; widened `Mint`/`Clear`.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` / `ScenarioValidator.cs` / `FactionWriter.cs` — `revives_heroes` coherence + fail-closed `revival_rule` range/curve-overflow/quantize checks + write-back.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` / `godot/src/UI/CommandCardSystem.cs` — `revives_heroes` editor toggle; revive command-card buttons (hidden on construction).
- `godot/src/Core/MainScene.cs` / `Core/Bootstrap/Phases/CameraPhase.cs` — bootstrap wiring for the spawn hook + command-card revive deps (beyond the Code Map; required to make the sim wiring and command card function in production).
- Tests: `HeroRevivalTests.cs` (NEW, 21 oracles), `HeroRevivalScenario.cs`/`HeroRevivalGoldenTests.cs`/`hero-revival-scenario.golden.txt` (NEW golden), `SimChecksumCoverageGuardTest.cs` (reserved-field teeth), `NegativeValidationTests.cs`/`UnitDefinitionValidatorTests.cs`/`FactionWriteRoundTripTests.cs` (validator + round-trip cases).

### Review findings

- Patches applied: 11 (2 high: dead-feature threading + cost-curve free-money overflow; 4 medium: time-curve overflow, hp-fraction quantize-zero, under-construction guard, grown-max HP fraction; 5 low: def-less loop guard, crystal + per-level-time test gaps, world-full retry, stale UI buttons). See Review Triage Log.
- Deferred: 3 (items-on-revival obligation for 3.15; dual producer+reviver UI affordance; AC3 harvest e2e seam) → `deferred-work.md`.
- Rejected: 10 (all low).

### Verification

- `dotnet build godot/godot.sln` — clean, 0 errors, no determinism-analyzer/banned-float violations in new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` — 1006 passed, 1 skipped, 1 failed. The single failure is the pre-existing, unrelated `ProceduralMapGeneratorTests` cross-platform hash tripwire (documented failing on baseline in the 3.12/3.13 auto-runs; this diff touches no procedural-map/serializer code). All 21 `HeroRevivalTests` + `HeroRevivalGoldenTests` (byte-identical across two runs) + coverage-guard v11 + validator/round-trip cases green.
- `hero-revival-scenario.golden.txt` re-recorded once (v11) after the grown-max HP fix; the 20 existing goldens are untouched/byte-identical. `AlgoVersion` stays 11; the coverage-guard known-state pin and `VersionStampConsistencyTests` are unchanged.
- Matrix Test Audit: every I/O row is covered by a passing test (row 9's Godot-coupled harvest is asserted at its sim precondition — the row stays `Alive` — with the harvest itself a pre-existing deferred seam).

### Residual risks

- All UI/presentation (the `revives_heroes` editor toggle, revive command-card buttons, cost display, construction hiding) is verified by clean compile + pattern-consistency, not a live in-engine session (headless environment). The determinism-critical path (death→awaiting→order→countdown→respawn→fold) is fully covered by the golden + 21 direct oracles.
- Follow-up review recommended: this pass made substantial cross-cutting changes — two high-severity fixes (a dead-feature end-to-end wiring fix and a free-money overflow class across the validator + runtime), a gameplay-semantics change (grown-max HP) that moved the golden, and several guard/robustness fixes — warranting an independent confirmatory pass.

### Residual artifacts (not part of this change)

An environment AutoSave hook committed the working tree several times mid-run (commits `0d1a9c7`/`e026354`/`b13ba63`), sweeping in unrelated files (`Snapshot.md`, `.bmad-loop/policy.toml`, an earlier `3-4-*.md` status flip). `Snapshot.md` remains modified in the working tree and is left in place (not part of this story).

## Follow-up Review (2026-07-08)

Independent confirmatory review pass (4 layers: Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment; all Opus 4.8, fresh context) over the full diff since baseline `a251f2fe`. The determinism-critical spine was verified clean by all layers (`Fixed`-only, `FoldOrder` ascending, ABA-safe packed refs, saturating cost/time math, two-run + cross-platform goldens appropriate). Triage: **3 patches, 1 defer, 12 rejects, 0 intent_gap, 0 bad_spec** (see Review Triage Log).

### Patches applied

- **[medium] Revive HP fraction not honored after growth re-materializes.** `RespawnHero` applied `fraction × grownMax` at the respawn instant, but the next tick's `ReconcileGrowth` re-added `+HealthPerLevel` per stack (`ModifierStore.ApplyStatDeltas` heals on positive-MaxHealth apply, Decision #3), inflating the SETTLED HP well above the authored fraction (level-3 @ 0.5 → 67%; level-10 @ 0.5 → ~full) — the `revive_hp_fraction` knob was effectively dead for leveled heroes. Fix (`godot/src/Combat/HeroXpSystem.cs` `RespawnHero`): reconcile growth in-tick, then `Health = EffectiveMaxHealth × HpFraction` → settled HP is exactly `fraction × grown max`, next-tick reconcile is a no-op. Also removes the duplicated grown-max formula (uses the saturated + stack-capped `EffectiveMaxHealth`). The revival golden was re-recorded once (v11); the 20 existing goldens are byte-identical.
- **[low] Player-built revive building coverage** — added `QueueWorkerBuild_ResolvesRevivesHeroesFromFactionDef` (the worker-construct path threaded `ResolveRevivesHeroes` correctly but was untested; only `PlaceBuildingDirect` was covered).
- **[low] Real combat-death → awaiting transition** — added `HeroDies_ThroughKillEntity_ThenTick_TransitionsToAwaiting` (the `KillEntity` announce and the `world.Destroy` transition were only tested in isolation).

### Files changed (this pass)

- `godot/src/Combat/HeroXpSystem.cs` — `RespawnHero`: in-tick growth reconcile + `EffectiveMaxHealth × HpFraction` (settled-HP correctness; removes the duplicated grown-max formula).
- `godot/ProjectChimera.Sim.Tests/Combat/HeroRevivalTests.cs` — settled-HP regression assertion on the respawn oracle; +2 new oracles (worker-build capability resolution; KillEntity→awaiting transition).
- `godot/ProjectChimera.Sim.Tests/Golden/hero-revival-scenario.golden.txt` — re-recorded once (v11) for the settled-HP timing change.

### Verification

- `dotnet build godot.sln` — clean, 0 errors.
- `dotnet test ProjectChimera.Sim.Tests` — **1008 passed, 1 skipped, 1 failed**. The single failure is the pre-existing, unrelated `ProceduralMapGeneratorTests` cross-platform hash tripwire (documented failing on baseline in the 3.12/3.13 auto-runs and in this spec's prior run; this diff touches neither `ProceduralMapGenerator.cs` nor `ScenarioSerializer.cs`, confirmed via `git diff --stat`). All 25 `HeroRevival*` tests green, including the settled-HP regression guard.
- Golden discipline held: recording under `CHIMERA_GOLDEN_RECORD=1` changed **only** `hero-revival-scenario.golden.txt` (`git status`); `AlgoVersion` stays 11; the coverage-guard known-state pin and `VersionStampConsistencyTests` are unchanged.

### Residual risks (this pass)

- The revived-hero HP semantics changed (settled HP is now exactly `fraction × grown max`) and the revival golden moved by one recording. The change is localized to `RespawnHero`, deterministic, and locked by a settled-value regression assertion — but it is a gameplay-semantics change worth a glance at the re-recorded golden if reviewing manually.
- Production-only presentation surfaces remain pattern-verified, not exercised headless: the `MainScene → ScenarioApplier.SpawnUnitAt` revive-spawn wiring (MeshType on the revived hero) and the command-card revive UI (button-index→hero-slot mapping) are Godot-coupled and outside the Godot-free harness. Determinism-identical to the tested closures; a presentation-only regression there would not be caught by a headless test.

### Follow-up recommendation

`followup_review_recommended: false` — this pass made one localized medium gameplay-correctness fix (fully covered by an updated oracle + re-recorded golden) plus two low test-coverage additions; no API/security/data impact, no cross-cutting churn. Does not meet the bar for another independent pass.
