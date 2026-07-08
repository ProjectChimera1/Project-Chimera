---
title: 'Authorable attack-delivery flag (hitscan vs projectile) + per-unit projectile speed'
type: 'feature'
created: '2026-07-07'
status: 'done'
baseline_revision: '7406f90'
final_revision: 'bcc7018'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** Attack delivery (instant melee vs travelling projectile) is inferred implicitly from `world.AttackRange[attacker] > MELEE_THRESHOLD (2.5)`, so a creator cannot author a long-range instant-hit sniper or a slow short-range lobber, and every projectile flies at one hardcoded global speed (18). Delivery must become an explicit, authorable per-unit property decoupled from range, with an optional per-unit projectile speed.

**Approach:** Add a nullable `delivery` enum string (`Hitscan | Projectile`) and a `projectile_speed` float to `UnitDefinition`; carry them into two new per-entity `EntityWorld` SoA arrays (`Delivery`, `ProjectileSpeed`) through the single `ApplyUnitDefinition` mapper; branch `CombatSystem` on `world.Delivery[attacker]` instead of the range threshold; make `ProjectileStore`/`ProjectileSystem` honour the per-unit speed; fold both arrays into `SimChecksum` (v10 re-baseline); reject bad values in `UnitDefinitionValidator`; and expose the two fields in the Unit Card Editor. Legacy JSON that omits `delivery` infers the old range-threshold result so every shipped unit keeps its exact current behavior.

## Boundaries & Constraints

**Always:**
- Delivery is a binary `Hitscan | Projectile` enum only. Legacy-safe default: when `delivery` is omitted/null, resolve via the *exact old Fixed comparison* `quantizedAttackRange > Fixed.FromFloat(2.5f)` → Projectile, else Hitscan (byte-identical partition to today).
- All new per-unit sim state flows through `EntityWorld.ApplyUnitDefinition` (the single def→SoA mapper); recycled-slot defaults are set in `EntityWorld.Create`. No hand-copying in any spawn path.
- Determinism is sacred: `ProjectileSpeed` stored/applied as custom 16.16 `Fixed`, never float/double/Mathf in sim; ascending-id iteration preserved; seeded `SimRng` only; no wall-clock. Both new SoA arrays fold into `SimChecksum` with an `AlgoVersion` bump and a re-baseline of every golden in the same commit.
- The accessor/validator split: `ResolveDelivery` fail-opens an unknown string to the range inference (like `ParsedTags`); the fail-closed reject of an invalid string is the validator's job.
- Presentation stays separate: the enum lives in the sim `ProjectChimera.Combat` namespace; the editor control is a Godot Control in the UI/CreationSuite layer with no sim coupling. No `using Godot;` in sim files.

**Block If:**
- The re-baselined `KnownWorldState` pinned hash cannot be reproduced deterministically across two consecutive runs (indicates a real determinism break, not a re-pin) — HALT `blocked`.

**Never:**
- No beam/arc/ballistic-arc delivery variants; no changes to projectile visuals/tracking beyond honouring per-unit speed.
- Do not add or touch any splash field — `splash_radius` is already fully built and authorable (VERIFY-only); this story adds no splash code.
- Do not fold `ProjectileStore.Speed` into the checksum (the store is never a checksum input).
- Do not widen `UnitSnapshot`/`RestoreUnit` for the new fields — editor-undo fidelity of the SoA is Story 3.17's scope; delivery-field undo here is a def-level form rebuild through `EditorHistory`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Legacy JSON, `delivery` omitted, ranged unit (attack_range 6.5) | archer fires | `ResolveDelivery` infers Projectile → travelling projectile at fallback speed 18 | No error |
| Legacy JSON, `delivery` omitted, melee unit (attack_range 1.5) | footsoldier fires | infers Hitscan → instant damage | No error |
| Authored `delivery:"Hitscan"`, attack_range 12 | long-range unit fires | instant damage, NO projectile spawned (range ignored) | No error |
| Authored `delivery:"Projectile"`, attack_range 1 + `projectile_speed:6` | short-range unit fires | tracking projectile at 6 u/s (from `world.ProjectileSpeed[attacker]`) | No error |
| Authored `delivery:"Projectile"`, `projectile_speed` omitted | unit fires | projectile at the fallback default 18 | No error |
| Invalid `delivery:"Beam"` | Save/Playtest | AR-39 validator returns located `delivery` error → badge → Save/Playtest blocked | Fail-closed |
| `delivery:"Projectile"`, `projectile_speed:0` or negative | Save/Playtest | located `projectile_speed` error → badge → blocked | Fail-closed |
| Editor: toggle Projectile→Hitscan→Projectile, then Ctrl+Z | Unit Card Editor | each change persists to faction JSON and is fully reversible via EditorHistory | No error |

</intent-contract>

## Code Map

- `godot/src/Combat/AttackDelivery.cs` -- NEW. `enum AttackDelivery { Hitscan, Projectile }` in `ProjectChimera.Combat` (mirrors `DamageType`/`ArmorType`).
- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `string? Delivery` (`[JsonPropertyName("delivery")]`, default null) + `float ProjectileSpeed` (`[JsonPropertyName("projectile_speed")]`, default 18f); add `LegacyDeliveryThreshold` (`Fixed.FromFloat(2.5f)`), `ResolveDelivery(Fixed quantizedRange)`, and `EffectiveDeliveryString()` (editor convenience, reuses `ResolveDelivery`).
- `godot/src/Core/EntityWorld.cs` -- add `AttackDelivery[] Delivery` + `Fixed[] ProjectileSpeed` SoA (alloc in ctor); default in `Create` (`Hitscan`, `ProjectileSystem.PROJECTILE_SPEED`); set in `ApplyUnitDefinition` (`def.ResolveDelivery(AttackRange[id])`, `Fixed.FromFloat(def.ProjectileSpeed)`); clear in the bulk `Array.Clear` reset block.
- `godot/src/Combat/CombatSystem.cs` -- DELETE `MELEE_THRESHOLD`; branch `TryDealDamage` and `TryDealBuildingDamage` on `world.Delivery[attacker] == AttackDelivery.Projectile`; pass `world.ProjectileSpeed[attacker]` to `Spawn`; fix doc comments.
- `godot/src/Combat/ProjectileStore.cs` -- add `Fixed[] Speed` SoA + a required `Fixed speed` param on `Spawn` (store per-projectile); clear `Speed` in `Clear`. NOT folded.
- `godot/src/Combat/ProjectileSystem.cs` -- use `_store.Speed[i]` instead of the global `PROJECTILE_SPEED` at the advance step; keep `PROJECTILE_SPEED` as the documented fallback default constant.
- `godot/src/Core/SimChecksum.cs` -- bump `AlgoVersion` 9→10; fold `(int)world.Delivery[i]` + `world.ProjectileSpeed[i].Raw` in the entity loop; add v10 doc block.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- add `_deliveries` closed set + invalid-`delivery` rule; add conditional strictly-positive `projectile_speed` rule (only when `Delivery == "Projectile"`).
- `godot/src/Core/Definitions/FactionWriter.cs` -- in `ApplyFields`, `PutString(obj, "delivery", d.Delivery, null)` (omit when null) + `PutFloat(obj, "projectile_speed", d.ProjectileSpeed, 18f)`; ensure `CloneUnit` copies both.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- add `Deliveries` set; a `delivery` dropdown (rebuild via `GoToUnit` on change so the dependent row toggles live) bound to `def.Delivery` (display via `EffectiveDeliveryString()`); a conditional `projectile_speed` `AddNumFloat` shown only when effective delivery is Projectile; `CloneUnit` copies both fields.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` -- add `Delivery`/`ProjectileSpeed` to `CombatDef()` (off Create defaults) + assertions in both guard tests.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- re-pin known-state hash to v10, assert `AlgoVersion == 10`.
- `godot/ProjectChimera.Sim.Tests/**` (combat helpers + direct-SoA scenarios) -- preserve behavior by setting `Delivery` alongside every `AttackRange` write (see Design Notes); then re-baseline all goldens.
- `godot/ProjectChimera.Sim.Tests/Golden/DeliveryScenario.cs` + `DeliveryGoldenTests.cs` + `delivery-scenario.golden.txt` -- NEW golden pair directly satisfying AC6: a fixed-seed scenario with a long-range **Hitscan** unit (attack_range well above 2.5, proving no projectile spawns), a short-range **Projectile** unit with a custom `projectile_speed` (≠18, proving per-unit speed + range decoupling), and a **splash** unit (VERIFY the existing splash path is unchanged). Integer/Fixed-only → cross-platform safe (compared on both CI legs). Mirror the `CombatAirGroundScenario`/`CombatAirGroundGoldenTests` structure and `MaybeRecord`/header convention.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- add the `<None Remove>` + `<EmbeddedResource Include>` pair for `Golden\delivery-scenario.golden.txt` (goldens are embedded explicitly, not globbed).
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitDefinitionValidatorTests.cs` + `FactionWriteRoundTripTests.cs` -- new cases for the two fields.

## Tasks & Acceptance

**Execution:**
- `AttackDelivery.cs` -- create the enum (`Hitscan=0`, `Projectile=1`).
- `UnitDefinition.cs` -- add the two JSON fields, threshold const, `ResolveDelivery`, `EffectiveDeliveryString`.
- `EntityWorld.cs` -- add both SoA arrays; alloc, Create-default, ApplyUnitDefinition-set, and bulk-clear each.
- `ProjectileStore.cs` / `ProjectileSystem.cs` -- add per-projectile `Speed`; honour it in the advance step.
- `CombatSystem.cs` -- remove `MELEE_THRESHOLD`; branch both damage paths on `Delivery`; pass per-unit speed to `Spawn`.
- `SimChecksum.cs` -- v10 fold + doc.
- `UnitDefinitionValidator.cs` -- invalid-delivery + conditional projectile_speed rules.
- `FactionWriter.cs` -- write-back (omit-on-default) + clone.
- `UnitCardPanel.Edit.cs` -- delivery dropdown + conditional projectile_speed field, undo-routed.
- Tests -- guard-test coverage; validator + round-trip cases; behavior-preserving `Delivery` on every ranged test setup; NEW `DeliveryScenario` golden (hitscan + custom-speed projectile + splash) with its csproj embed pair; re-baseline every golden + re-pin the known-state hash.

**Acceptance Criteria:**
- Given a Siege unit with `splash_radius > 0`, when it fires as a Projectile and hits, then every enemy within `splash_radius` takes full damage exactly as today, and a `splash_radius 0` unit hits only the primary target (VERIFY-only: no splash code added).
- Given `CombatSystem.TryDealDamage`/`TryDealBuildingDamage`, when an attack resolves, then it branches on `world.Delivery[attacker]` and the `MELEE_THRESHOLD` comparison no longer exists anywhere in the delivery decision; a Hitscan unit deals instant damage with no projectile regardless of AttackRange, a Projectile unit spawns a tracking projectile regardless of AttackRange.
- Given a `Projectile` unit with authored `projectile_speed`, when it fires, then the projectile travels at `world.ProjectileSpeed[attacker]` (16.16 Fixed); omitting `projectile_speed` uses the fallback 18 so existing data is unchanged.
- Given faction JSON that omits `delivery`, when it loads, then every shipped alpha/beta unit keeps its current behavior (melee-range units instant, ranged units projectile) with zero authoring errors; an invalid `delivery` string or a non-positive `projectile_speed` on a Projectile unit fails closed through AR-39 with a located badge and blocks Save/Playtest.
- Given the Unit Card Editor, when I open a unit, then a `delivery` dropdown and a `projectile_speed` field (shown only when effective delivery is Projectile) are exposed with tooltips, route through EditorHistory (Ctrl+Z reverses a Projectile↔Hitscan flip), and persist to faction JSON.
- Given the golden harness on a fixed-seed scenario with a Hitscan unit, a Projectile unit with custom `projectile_speed`, and a splash unit, when simulated for the recorded ticks, then `SimChecksum` (folding the new `Delivery` + `ProjectileSpeed` arrays) is byte-identical to the re-baselined golden across two consecutive runs, with ascending-id iteration and no float/Mathf in the new sim code.

## Spec Change Log

_No bad_spec loopbacks. All review findings were resolved as patches or deferred without amending the intent-contract._

## Review Triage Log

### 2026-07-07 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 3, low 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 2: (high 0, medium 0, low 2)
- addressed_findings:
  - `[medium]` `[patch]` F1 — the validator gated the `projectile_speed` rule on the literal `def.Delivery == "Projectile"`, so a legacy unit that OMITS `delivery` but has `attack_range > 2.5` (infers Projectile) could ship `projectile_speed ≤ 0`, spawning stuck (speed 0) or pool-leaking (negative) shells at runtime. Fixed to gate on the RESOLVED delivery via `def.EffectiveDeliveryString() == "Projectile"`; added 3 regression tests (inferred-projectile rejects 0/−5; inferred-hitscan ignores).
  - `[low]` `[patch]` F3 — the two `def == null` fallback spawn paths (`BuildingSystem.cs` FALLBACK_ATTACK_RNG=5, `EntityPlacer.cs` ATTACK_RANGE=5) bypass `ApplyUnitDefinition` and set `AttackRange > 2.5` without `Delivery`, so a degenerate-data ranged unit regressed from projectile to the Create-default Hitscan. Added the mirrored `AttackRange > LegacyDeliveryThreshold ? Projectile : Hitscan` inference at both sites (ProjectileSpeed keeps the Create-default 18 == old global).
  - `[medium]` `[patch]` VG1 — the delivery-decoupling had no direct oracle (only the self-recorded golden). Added `DeliveryCombatTests`: a long-range Hitscan unit spawns NO projectile and damages instantly; a short-range Projectile unit spawns one and deals no damage until arrival.
  - `[medium]` `[patch]` VG2 — per-unit projectile speed was never observed in motion (folding it directly means the golden moves even if the flight step ignores it). Added a `ProjectileSystem` oracle asserting a speed-6 shell advances ~6 (not the global 18) and a speed-18 shell advances further.
  - Deferred: F2 (editor delete+undo `RestoreUnit`/`UnitSnapshot` does not carry `Delivery`/`ProjectileSpeed`) — the same pre-existing incomplete-snapshot class already documented at `EntityPlacer.cs:1122` (collision_radius/separation_priority/category also dropped) and chartered to Story 3.17; the intent's editor-reversibility (AC5) is the Unit Card Editor form-undo, which is implemented. Logged to deferred-work.
  - Rejected: F4 (a Hitscan unit round-trips a harmless unused `projectile_speed` key — cosmetic; retaining the value across a Projectile→Hitscan flip is arguably desirable UX); F5 (checksum re-baseline noted sound — not a defect).

### 2026-07-07 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 7: (high 0, medium 0, low 7)
- addressed_findings:
  - `[low]` `[patch]` R1 — `projectile_speed` is quantized (`Fixed.FromFloat`) and folded into `SimChecksum` (v10) for EVERY entity, but the validator constrained it ONLY when effective delivery is Projectile, so a Hitscan unit (or the untested finite/range branch of a Projectile unit) could carry a NaN/Inf/out-of-range value into the deterministic hash — the one folded numeric stat lacking the `[0, Range)` invariant every other one has via `CheckStat`. Restructured `UnitDefinitionValidator` into two rules: finite & `[0, 32768)` UNCONDITIONALLY, plus strict-positivity kept only for effective-Projectile units; added 8 tests (NaN/±Inf/==Range/>Range on both Projectile and Hitscan units). Also clarified the strict-positive error message to name "authored or inferred from range" (subsumes the F8 clarity finding). Converged finding from three review layers (Blind Hunter F3, Edge Case #1, Verification Gap #2).
  - `[medium]` `[patch]` R2 — the building-target projectile branch (`CombatSystem.TryDealBuildingDamage`) correctly passes `world.ProjectileSpeed[attacker]`, but NO test exercised it: every `AntiBuildingCombatTests`/`BuildingAutoAcquireTests` attacker fires at the Create-default 18 and the new `DeliveryScenario` golden has no buildings, so reverting the branch to the global constant would stay green. Added `RangedAttacker_VsBuilding_SpawnsShellAtPerUnitSpeed_NotTheGlobalDefault` asserting the spawned shell's `Speed` equals the attacker's per-unit speed (6) and NOT the global 18 (Verification Gap #1).
- Deferred (1, NEW ledger entry): R3 — a high authored `projectile_speed` (up to the validator's 32768 ceiling) overshoots the 0.5 hit radius every tick and never converges (`ProjectileSystem.Tick` has no snap-to-goal clamp and no TTL) → the shell orbits forever, leaking its `MAX_PROJECTILES` slot. Pre-existing `ProjectileSystem` non-convergence, newly reachable via the authorable speed; the fix (snap clamp + speed cap + TTL) is a projectile-TRACKING change excluded by this story's intent boundary ("no changes to projectile tracking beyond honouring per-unit speed") and would require a full golden re-baseline, so it is chartered to its own focused change (Blind Hunter F2). Logged to `deferred-work.md`.
- Re-surfaced, already deferred (NO new entry per NEW-entries-only rule): Blind Hunter F1 / Edge Case — `EntityPlacer.RestoreUnit`/`UnitSnapshot` delete-undo drops `Delivery`/`ProjectileSpeed`; already logged in the prior 3.12 pass and chartered to Story 3.17.
- Rejected (7): F4 (`Array.Clear`→0 vs `Create` default is the universal SoA-reset pattern every sibling field follows — benign by design); F5 / F7 (the delivery-string mirrors follow the established `DamageType`/`ArmorType` validator+editor precedent; `LegacyDeliveryThreshold` is a frozen behavior-preservation constant, so the test literals are intentional documentation); F6 (the intended `ResolveDelivery` fail-open / validator fail-closed split — all content-load paths are validator-gated); Edge Case #2 (a `projectile_speed ≤ 0` on a Projectile unit is validator-blocked); Edge Case #3 (a third delivery variant is explicitly excluded by the intent's "binary enum only"); Verification-Gap note (the `UnitCardPanel` Duplicate clone is presentation code unreachable from the sim test project).

## Design Notes

**D1 — Legacy default = the exact old inference (behavior preservation).** `delivery` is `string?` default **null** (not `"Hitscan"`): a flat Hitscan default would silently turn every shipped ranged unit (archer 6.5, mage 7, siege 10, …) into hitscan, violating AC4. Instead `ApplyUnitDefinition` computes `Delivery[id] = def.ResolveDelivery(AttackRange[id])` where the null/unknown branch is `quantizedRange > Fixed.FromFloat(2.5f)`, the *identical Fixed comparison the deleted `MELEE_THRESHOLD` used* — so the partition is byte-identical for all data. `projectile_speed` is a plain `float` default `18f` (== the old global), so omitted-speed units are unchanged and FactionWriter omits the key for every existing unit (round-trips byte-identical).

**D2 — Fold both arrays, re-baseline (v10).** The story mandates folding `Delivery` + `ProjectileSpeed` into `SimChecksum` even though, like `AttackRange`/`SplashRadius`/`CategoryOf`, they are authored spawn-constants whose effect also reaches the hash transitively via Position/Health. Fold them directly anyway: `hash = Mix(hash, (int)world.Delivery[i]); hash = Mix(hash, world.ProjectileSpeed[i].Raw);` per alive entity. Bump `AlgoVersion` to 10 with a v10 doc block, then re-baseline every golden and re-pin `SimChecksumCoverageGuardTest`'s known-state constant + its `AlgoVersion` assertion in the same commit.

**D3 — Preserve behavior in tests BEFORE re-baselining.** Because the tick decision now reads `world.Delivery` (Create default `Hitscan`), any test/golden that builds a *ranged* unit via direct SoA writes (not `ApplyUnitDefinition`) must set `Delivery` or it silently becomes hitscan and the re-baseline would bake wrong behavior. Strategy: in each combat test helper that writes `AttackRange` (`Combatant`/`Attacker`/`Unit` in `CommandVocabularyTests`, `AntiBuildingCombatTests`, `BuildingAutoAcquireTests`, `AntiBuildingScenario`), add the mirror line
```csharp
w.Delivery[id] = w.AttackRange[id] > Fixed.FromFloat(2.5f) ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
```
and for direct-SoA scenario writes (`GoldenScenario.cs:144` p1Ranged r6, `AttackDomainTests.cs:83/102/118` r20) add the equivalent explicit `Delivery = Projectile`. `ProjectileSpeed` needs no per-test edit — Create defaults it to 18 (the old global). Melee (r≤2.5) setups are unchanged by the Hitscan default. Then re-baseline: `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter FullyQualifiedName~Golden` → `dotnet build` (refresh embedded copies) → run `KnownWorldState` test, read the actual hash, update the pinned constant + `Assert.Equal(10, …)` → full green run → commit.

**Enum naming:** use `AttackDelivery` (not `Delivery`) to avoid clashing with the `UnitDefinition.Delivery` string property, mirroring the `DamageType` property/enum disambiguation already in the codebase.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds clean, no determinism-analyzer (CHM*) or banned-float violations in the new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green, including `ApplyUnitDefinitionGuardTest`, `SimChecksumCoverageGuardTest` (v10), every `*GoldenTests`, `UnitDefinitionValidatorTests`, `FactionWriteRoundTripTests`.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then `dotnet build` then a clean `dotnet test` -- expected: goldens regenerated once, then stable byte-identical across two consecutive normal runs.

**Manual checks:**
- Grep confirms `MELEE_THRESHOLD` no longer appears in `godot/src/`.
- Open the Unit Card Editor (or `/godot-verify`): a unit shows the `delivery` dropdown; selecting Projectile reveals `projectile_speed`; an invalid value badges the field and blocks Save; Ctrl+Z reverses a delivery flip.

## Auto Run Result

Status: done

### Summary
Attack delivery is now an explicit authored `delivery` enum (Hitscan | Projectile) with an optional per-unit `projectile_speed`, decoupled from attack range. `CombatSystem` branches on the new `EntityWorld.Delivery` SoA (the `MELEE_THRESHOLD` range inference is deleted from the tick decision and relocated as the null/omitted legacy default in `UnitDefinition.ResolveDelivery`, preserving every shipped unit's behavior byte-for-byte). Projectiles fly at the per-unit `ProjectileStore.Speed` instead of the hardcoded global 18. Both new per-entity SoA arrays fold into `SimChecksum` (AlgoVersion 9→10, all goldens re-baselined). The fields are validated (AR-39) and exposed in the Unit Card Editor with a conditional `projectile_speed` row.

### Files changed (production)
- `godot/src/Combat/AttackDelivery.cs` (NEW) — `enum AttackDelivery { Hitscan=0, Projectile=1 }`.
- `godot/src/Core/Definitions/UnitDefinition.cs` — `Delivery` (`string?`) + `ProjectileSpeed` (`float`=18), `LegacyDeliveryThreshold`, `ResolveDelivery`, `EffectiveDeliveryString`.
- `godot/src/Core/EntityWorld.cs` — `Delivery` + `ProjectileSpeed` SoA (alloc, Create default, ApplyUnitDefinition set, bulk clear).
- `godot/src/Combat/CombatSystem.cs` — deleted `MELEE_THRESHOLD`; both damage paths branch on `Delivery`; pass per-unit speed to `Spawn`.
- `godot/src/Combat/ProjectileStore.cs` / `ProjectileSystem.cs` — per-projectile `Speed[]`; advance step honours it.
- `godot/src/Core/SimChecksum.cs` — v10 fold of `Delivery` + `ProjectileSpeed`.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — invalid-`delivery` reject + conditional `projectile_speed` rule (gated on RESOLVED delivery — F1 fix).
- `godot/src/Core/Definitions/FactionWriter.cs` — omit-on-default write-back of both fields.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` — delivery dropdown + conditional projectile_speed row, undo-routed; clone copies both.
- `godot/src/Economy/BuildingSystem.cs` + `godot/src/UI/EntityPlacer.cs` — def-less fallback spawns mirror the range→delivery inference (F3 fix).

### Files changed (tests)
- `ApplyUnitDefinitionGuardTest`, `SimChecksumCoverageGuardTest` (re-pinned v10 hash), `VersionStampConsistencyTests`, `SimResetTests`, `HeroProfilePersistenceTests`, `CombatFeedbackProfileTests` (AlgoVersion 9→10).
- `UnitDefinitionValidatorTests` (+ inferred-projectile/hitscan speed cases), `UnitDefinitionDeliveryTests` (NEW — null-inference table), `FactionWriteRoundTripTests`.
- `DeliveryCombatTests` (NEW — VG1/VG2 direct oracles: hitscan spawns no projectile, projectile defers damage, per-unit speed honoured).
- `DeliveryScenario` + `DeliveryGoldenTests` + `delivery-scenario.golden.txt` (NEW golden, csproj embed) + all 17 per-tick goldens re-baselined (v10).
- Behavior-preserving `Delivery` mirror added to direct-SoA combat helpers/scenarios (`CommandVocabularyTests`, `AntiBuildingCombatTests`, `BuildingAutoAcquireTests`, `AntiBuildingScenario`, `GoldenScenario`, `AttackDomainTests`).

### Review findings
- Patches applied (4): F1 validator resolved-delivery gate (+ 3 tests); F3 fallback-spawn delivery inference (2 sites); VG1 delivery-decoupling oracle; VG2 per-unit-speed oracle.
- Deferred (1): F2 — `EntityPlacer.RestoreUnit`/`UnitSnapshot` delivery/speed fidelity (same pre-existing incomplete-snapshot class chartered to Story 3.17). Logged to deferred-work.md.
- Rejected (2): F4 (cosmetic unused-key round-trip), F5 (checksum re-baseline noted sound, not a defect).

### Verification
- `dotnet build godot.sln` — succeeded, 0 errors, 0 warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` — 917 passed, 1 skipped, 1 failed. The single failure (`ProceduralMapGeneratorTests.SameSeed_TwiceProducesByteIdenticalSerialization_AndMatchesGoldenHash`) is PRE-EXISTING and unrelated: it hashes procedurally-generated map JSON via `ScenarioSerializer.ComputeHash` (not `SimChecksum`) over `ProceduralMapGenerator`/`ScenarioSerializer` — neither touched by this story — with an unchanged pinned hash; it is a known platform-recorded-hash tripwire.
- Golden re-baseline stable byte-identical across two consecutive normal runs.
- `MELEE_THRESHOLD` no longer exists as a constant/comparison in `godot/src/` (grep hits are doc-comments only).

### Residual risks
- Editor UX (dropdown reveal of `projectile_speed`, invalid-value badge, Ctrl+Z reversing a flip) verified by clean `godot.sln` compile + pattern-consistency with the established `AddSelect`/`OnPromoteToggled` EditorHistory pattern, not a live in-engine session (headless environment).
- F2 (deferred): deleting + undoing a *placed* Projectile unit in the scenario editor reverts it to Hitscan until Story 3.17 widens `UnitSnapshot`.
- The pre-existing `ProceduralMapGeneratorTests` failure is environmental (platform-recorded hash), out of scope — flagged so it is not mistaken for a regression.

### Follow-up review pass (2026-07-07)
An independent 4-layer follow-up review (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) ran against the full baseline→HEAD diff. Intent Alignment confirmed the diff faithfully implements the intent (readings A2/B2/C2) with no correctness divergence. Two patches were applied; one new item was deferred; seven findings were rejected.

**Patches applied (2):**
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — `projectile_speed` now validated finite & `[0, 32768)` UNCONDITIONALLY (it is folded into `SimChecksum` for every unit regardless of delivery), with strict-positivity kept only for effective-Projectile units; clearer error messages. Closes the folded-but-unvalidated hole flagged independently by three layers.
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitDefinitionValidatorTests.cs` — 8 new cases (NaN/±Inf/==Range/>Range on Projectile and Hitscan units).
- `godot/ProjectChimera.Sim.Tests/Combat/AntiBuildingCombatTests.cs` — new `RangedAttacker_VsBuilding_SpawnsShellAtPerUnitSpeed_NotTheGlobalDefault` oracle pinning the building branch's per-unit speed argument.

**Deferred (1, new ledger entry):** high authored `projectile_speed` overshoot / non-convergence + slot leak in `ProjectileSystem.Tick` (no snap-clamp/TTL) — a projectile-tracking change excluded by the intent boundary, needs a golden re-baseline. Logged to `deferred-work.md`. The prior pass's `RestoreUnit`/`UnitSnapshot` deferral re-surfaced (Blind Hunter F1) and was NOT re-logged (already tracked, chartered to Story 3.17).

**Verification (follow-up):** `dotnet build godot/godot.sln` — 0 errors (5 pre-existing warnings in untouched files, no CHM determinism-analyzer violations). `dotnet build godot/ProjectChimera.Sim.Tests` then `dotnet test` — **926 passed, 1 skipped, 1 failed**; the +9 vs the prior 917 are the new tests, all green. The single failure is the same pre-existing, unrelated `ProceduralMapGeneratorTests` tripwire (procedural-map JSON hash via `ScenarioSerializer`, untouched by this story or these patches). Note: the test project is NOT a member of `godot.sln`, so it must be built explicitly (`dotnet build godot/ProjectChimera.Sim.Tests`) before a `--no-build` test run — the sln build alone leaves a stale test dll.

**Follow-up recommendation:** `false` — the follow-up produced two localized, low/medium defensive fixes (one validator hardening + test coverage); no broad, high-consequence, or behavior-altering change that warrants a further independent review.
