---
title: 'Teams & alliances — lobby teams wired into the sim alliance model'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '2f72b72f54d8b65e50a9d729ea592af6c671296b'
final_revision: '904106a'
review_loop_iteration: 0
followup_review_recommended: true # follow-up-2 pass patched: 0 high, 2 medium, 0 low → score 3×2 + 1×0 = 6 (≥5); both are additive host-wiring test guards (ProjectileSystem, AbilityCastSystem), no production-code change this pass
context:
  - '{project-root}/godot/CLAUDE.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** The sim alliance model (`AllianceStore`, shipped by Story 7.12 and already folded into `SimChecksum` v20 and consumed by `WinConditionSystem`) exists but is *never populated* — every match runs FFA (teams-of-1). Teams cannot be assigned, combat/vision ignore alliance, so 2v2 with shared victory — the GDD Phase-3 promise — is impossible.

**Approach:** Add a per-slot `Team` to the scenario/setup model, seed `AllianceStore` from it at match start via a pure canonical mapping, fold the team tuple into the match-start agreement hash, display team glyphs in the lobby, and make combat targeting, projectile splash, ability targeting, and (toggle-gated) fog vision consult `AllianceStore.AreAllied`. Allied victory already resolves via 7.12's `WinConditionSystem` once the mask is seeded — no win-system change.

## Boundaries & Constraints

**Always:**
- Alliance mask team ids MUST stay in `[0, FACTION_COUNT)` and be a **faction-slot index** (the lowest `(int)Faction` slot in the team), never an arbitrary team ordinal — `WinConditionSystem`'s team scans silently drop out-of-range team ids and mis-resolve victory.
- FFA (no team assigned) MUST remain byte-identical to today: `TeamId[f] == f`, and no distinct factions ever test as allied — so existing goldens stay byte-identical with no `SimChecksum` `AlgoVersion` bump and no golden re-baseline.
- Preserve Neutral force-fire (Story 1.12): the allied-exclusion checks must not block targeting Neutral (`AreAllied(Player, Neutral)` is already `false`, so keep the existing Neutral special-cases intact).
- Sim/seeding code stays Godot-free and banned-API-clean (no float/Random/DateTime/Dictionary-enumeration in the sim assembly).
- Team must live in the shared `ScenarioData` model so both peers recompute the agreement hash identically — teams are NOT sent as a separate wire byte.

**Block If:**
- Seeding cannot preserve the faction-slot-index invariant for some valid team layout (would require a human design decision on team-id encoding).
- Folding `Team` into `MatchAgreementHash` would require moving/renaming an existing golden checksum baseline (it must not — this hash is separate from `SimChecksum`).

**Never:**
- No interactive runtime team re-selection / team-change wire protocol or re-agreement round — that belongs to the deferred FR-68 skirmish setup screen. Teams are authored in `ScenarioData` (loaded identically on all content-hash-verified peers); the lobby only *displays* them.
- No in-match diplomacy (1.0 has none). The mask is immutable per match.
- No change to `WinConditionSystem` resolution logic (already team-aware via 7.12).
- No `SimChecksum` `AlgoVersion` bump or golden re-baseline.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| FFA default | slots with no `Team` (or `Team==0`) | seeded mask = `TeamId[f]==f`; combat/checksum byte-identical to pre-change | No error |
| 2v2 | slots 1,2 → teamA; 3,4 → teamB | `TeamId` groups by team; canonical id = lowest faction slot in group (e.g. teamA→1, teamB→3) | No error |
| 3v1 | slots 1,2,3 → teamA; 4 → teamB | teamA all share min-slot id 1; teamB = 4 | No error |
| Ally auto-acquire | allied enemy unit/building in range | excluded from nearest-enemy acquisition; not attacked | No error |
| Ally force-fire | force-fire order onto an ally | rejected (no attack); force-fire onto Neutral still allowed | No error |
| Ally splash | AoE projectile lands near an ally | ally takes no splash damage | No error |
| Shared vision ON | allied unit sees area, toggle enabled | local fog unions allied vision | No error |
| Shared vision OFF | same, toggle disabled | local fog shows only own-faction vision | No error |
| Team mismatch pre-start | peers disagree on any slot's team | agreement hash differs → match refuses to start (fail-closed) | Handled by existing handshake gate |

</intent-contract>

## Code Map

- `godot/src/Core/AllianceStore.cs` -- **exists (7.12), read + seed target**. `TeamId[]` (index `(int)Faction`), `TeamOf`, `AreAllied`, `Clear()`/FFA default. Its own doc says teams are "seeded by Story 9.15 later" — that seeding IS this story (code comments use older 9.15 numbering).
- `godot/src/Core/Definitions/ScenarioData.cs` -- `class ScenarioPlayerSlot` (~line 153) has NO `Team` field; `ScenarioData.PlayerSlots[]` (~line 720). **Add** `Team`.
- `godot/src/Core/AllianceSeeder.cs` -- **new**, pure Godot-free helper mapping per-slot team ordinals → canonical faction-slot team ids and writing into an `AllianceStore`.
- `godot/src/Core/MainScene.cs` / `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- match-start flow (`ScenarioApplier.Apply`, hero mint ~MainScene:538, `MatchAgreementHash` set ~MainScene:552). **Seed the mask here** right after apply.
- `godot/src/Core/Definitions/MatchAgreementHash.cs` -- `Compute(...)` (~line 44) folds roster in a per-slot loop (~line 56). **Add** `PlayerSlots[slot].Team` to the loop and **bump `AlgoVersion` 1→2**.
- `godot/src/Navigation/SpatialHash.cs` -- `FindNearestEnemy` (~109) & `FindNearestEnemyGlobal` (~145): `== myFaction` skip. **Add** allied skip; needs `AllianceStore` threaded in.
- `godot/src/Combat/CombatSystem.cs` -- built at `SimulationHost.cs:284`; `FindNearestEnemyBuildingInRange` (~581) and force-fire guard in `TickAttackTargetCombat` (~273, the 1.12 Neutral note). **Add** allied exclusion; thread `AllianceStore` in and down into `SpatialHash`.
- `godot/src/Combat/ProjectileSystem.cs` -- `ApplySplash` (~169) friendly skip. **Add** allied skip.
- `godot/src/Effects/TargetMatcher.cs` -- ability allegiance bits (Ally ~66 / Enemy ~68 / Neutral ~70), called from `SearchAreaEffect.cs:76`. **Make** Ally/Enemy alliance-aware via `EffectContext` (carries `casterFaction`).
- `godot/src/Core/FogOfWarSystem.cs` -- single-viewer fog (constructed `SimulationHost.cs:203`); `Tick` skip at ~line 71 (`!= _faction continue`). Presentation-only (NOT checksummed). **Add** `AllianceStore` + `bool SharedTeamVision` and union allied vision when enabled.
- `godot/src/Core/Sim/SimulationHost.cs` -- owns `Alliances` (line 124/211); constructs Combat/Projectile/Fog systems. **Thread** `Alliances` into them.
- `godot/src/UI/LobbyUi.cs` -- `RebuildSlotGrid()` (~712) renders per-slot dot+glyph via `FactionPalette.ForSlot`. **Add** per-slot team glyph from `FactionPalette` keyed by canonical team id.
- `godot/src/UI/FactionPalette.cs` -- **exists**, Okabe-Ito colorblind-safe colors + glyphs; reuse `ForSlot(teamSlot)` for team glyphs.
- `godot/ProjectChimera.Sim.Tests/` -- new tests: `AllianceSeederTests`, combat allied-exclusion, `MatchAgreementHash` team-fold, fog shared-vision, and a 2v2 two-run determinism test.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ScenarioData.cs` -- Add `[JsonPropertyName("team")] public int Team { get; set; }` to `ScenarioPlayerSlot` (default `0` = unassigned/FFA). Settable auto-prop, int (no enum), consistent with the dual-path DTO constraints.
- `godot/src/Core/AllianceSeeder.cs` -- **New** pure static helper. `Seed(AllianceStore alliances, ScenarioData model)`: start from FFA (`TeamId[f]=f`); group active slots by their `Team` ordinal; for each group with `Team>0`, canonical team id = `min((int)Faction)` over the group's members, and write `TeamId[(int)faction]=canonicalId` for each member. `Team==0` slots keep their own-faction id (self-team). Never touch Neutral (index 0). Integer-only, Godot-free, no Dictionary-enumeration in a way that affects order (compute the min deterministically over ascending active factions).
- `godot/src/Core/MainScene.cs` (and/or `MatchLifecycleController.cs`) -- After `ScenarioApplier.Apply`, call `AllianceSeeder.Seed(host.Alliances, model)` so the mask reflects the scenario's teams before tick 0. Ensure `AllianceStore.Clear()` on reset still precedes re-seed.
- `godot/src/Core/Definitions/MatchAgreementHash.cs` -- Inside the existing per-slot roster loop, additionally fold `model.PlayerSlots[slot].Team`; bump `AlgoVersion` from `1` to `2`.
- `godot/src/Core/Sim/SimulationHost.cs` -- Pass `Alliances` into `CombatSystem`, `ProjectileSystem`, and `FogOfWarSystem` constructors.
- `godot/src/Navigation/SpatialHash.cs` -- Accept an `AllianceStore` (via ctor or the acquisition-call args used by `CombatSystem`); in `FindNearestEnemy`/`FindNearestEnemyGlobal`, extend the `== myFaction` skip to also skip `alliances.AreAllied(myFaction, other)`. Keep current Neutral handling.
- `godot/src/Combat/CombatSystem.cs` -- Thread `AllianceStore` in; extend `FindNearestEnemyBuildingInRange` (`|| AreAllied(...)`) and the force-fire same-faction guard in `TickAttackTargetCombat` to also reject allied force-fire — while leaving the Neutral force-fire path intact (verify `AreAllied(myFaction, Neutral)==false` keeps Neutral targetable).
- `godot/src/Combat/ProjectileSystem.cs` -- In `ApplySplash`, extend the friendly-skip to also skip `alliances.AreAllied(owner, victimFaction)`.
- `godot/src/Effects/TargetMatcher.cs` (+ `EffectContext`) -- Thread `AllianceStore` onto `EffectContext`; make the Ally bit match allies (`AreAllied(caster, ef)` && not self) and the Enemy bit exclude allies. Keep Neutral bit unchanged.
- `godot/src/Core/FogOfWarSystem.cs` -- Add `AllianceStore` + `bool SharedTeamVision` (default `true`); in `Tick`, when `SharedTeamVision` and the unit's faction `AreAllied` with the viewer, stamp its vision too. No checksum impact (fog is presentation-only).
- `godot/src/UI/LobbyUi.cs` -- In `RebuildSlotGrid()`, render each occupied slot's team using a colorblind-safe glyph from `FactionPalette` keyed by the canonical team id (own-faction glyph when `Team==0`). Color is never the only signal — glyph/label accompanies.
- `godot/ProjectChimera.Sim.Tests/...` -- Unit-test every I/O-matrix row: `AllianceSeederTests` (FFA/2v2/3v1 canonical ids, invariant range, Neutral untouched); combat allied-exclusion (unit acquire, building acquire, force-fire ally rejected, Neutral force-fire still allowed, splash skips ally); `MatchAgreementHash` team-fold (different team → different hash, `AlgoVersion==2`, FFA-absent unchanged from a fresh recompute); fog shared-vision on/off; and a **2v2 two-run determinism test** proving byte-identical `SimChecksum` across two runs of a seeded-alliance match through elimination + team victory.

**Acceptance Criteria:**
- Given a scenario whose slots carry teams (2v2 / 1v1v1v1 / 3v1), when the match starts, then `AllianceStore.TeamId` is seeded so each team's members share one faction-slot team id in `[1,8]`, the lobby shows each slot's team via a colorblind-safe glyph, and the team tuple is folded into `MatchAgreementHash` (`AlgoVersion==2`).
- Given allied factions in a match, when combat runs, then allies are excluded from nearest-enemy acquisition (units and buildings), from force-fire, and from projectile splash, while Neutral force-fire remains allowed.
- Given the shared-team-vision toggle, when it is enabled, then the local fog unions allied units' vision, and when disabled only own-faction vision is shown; fog changes never affect any `SimChecksum`.
- Given a seeded 2v2 match, when it runs to an elimination and team victory, then both allied winners resolve as WON via the existing `WinConditionSystem`, and two identical-input runs produce byte-identical `SimChecksum` (zero desync).
- Given a scenario with no team assignments (FFA), when the match runs, then every `SimChecksum` and all existing golden baselines are byte-identical to before this change (no `AlgoVersion` bump, no re-baseline).

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 6
- reject: 2
- addressed_findings:
  - `[low]` `[patch]` `MatchAgreementHash` folded Team positionally while `AllianceSeeder` keys by `.Slot` — changed the fold to hash the canonical seeded team ids (via `AllianceSeeder.ComputeTeamIds`) in ascending active-faction order, matching what the sim actually seeds; test's hand-rolled fold updated.
  - `[medium]` `[patch]` The sole production seed wire `ScenarioApplier.Apply → AllianceSeeder.Seed` was untested — added `ScenarioApplierTests` covering team-seeding (2v2 → allied) and the FFA golden-safety twin (all Team==0 → mask stays FFA).
  - `[medium]` `[patch]` Force-attack-building allied rejection (`CombatSystem.TickAttackBuildingCombat`) was untested — added a test asserting a forced `AttackBuilding` onto an allied building reverts to Idle with the building's Health unchanged, and a neutral building stays force-attackable.
  - `[low]` `[patch]` `SimulationHost` fog-wiring (passing `Alliances` into the live `FogOfWarSystem`) was untested — added a test exercising `host.Fog` (not a hand-built fog) so a teammate's scouted tile reveals.

### 2026-07-24 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 3, low 0)
- defer: 1
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` `MatchAgreementHash`'s team fold still keyed `teamIds[]` by the *positional* `ToFaction(loopIndex)` over only active slots `0..n-1`, while `AllianceSeeder` keys by `.Slot+1`. For a non-contiguous roster (a removed middle slot — supported by `RemoveStartSlot`), a team on a gapped high slot was folded into the wrong/no faction id, so peers that disagreed on it hashed identically → the fail-closed team-mismatch guarantee failed **OPEN**, and the code comment overstated it. Fixed the fold to hash the entire faction-indexed canonical mask `teamIds[1..FACTION_COUNT)` (exactly what `AllianceSeeder` seeds and `SimChecksum` folds) — order/gap-independent; corrected the comment; updated the test hand-roll mirror; added `GappedRoster_TeamMismatch_StillMovesTheHash_FailsClosed`.
  - `[medium]` `[patch]` Ability targeting team-awareness (a named intent surface) had **zero** coverage with a non-null mask — every existing `TargetMatcher`/`SearchArea` test runs the null-mask fallback. Added `AllianceTargetFilterTests` driving the production `SearchAreaEffect.FindTargets` → `TargetMatcher` path with a live `AllianceStore`: `Ally` matches the allied faction (excludes enemy/Neutral/self), `Enemy` excludes the ally, and the null-mask twin proves the pre-9.14 faction-equality reduction.
  - `[medium]` `[patch]` `SimulationHost`'s combat wiring was unverified — removing the `Alliances` arg from the host's `CombatSystem` construction failed no test (all goldens are FFA). Added `SimulationHost_WiresAllianceIntoLiveCombat_AllyExcludedFromAcquisition` driving a full `host.StepOnce()` so a teammate nearer than the enemy is skipped only if the live pipeline carries the mask.

### 2026-07-24 — Review pass (follow-up 2)
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 2, low 0)
- defer: 1
- reject: 12
- addressed_findings:
  - `[medium]` `[patch]` The prior follow-up added host-wiring guards for `CombatSystem` and `FogOfWarSystem` but **not** for the host's `ProjectileSystem` — dropping the `Alliances` arg from `SimulationHost`'s `new ProjectileSystem(...)` (line ~287) would let live AoE splash allied units with every test still green (the only splash test hand-builds `new ProjectileSystem(store, alliances: …)`; all goldens are FFA no-ops). Added `SimulationHost_WiresAllianceIntoLiveProjectileSplash_AllyExcludedFromSplash` driving a full `host.StepOnce()` with an owner-fired splash projectile over {ally, enemy, neutral, primary} — RED if the host drops the mask.
  - `[medium]` `[patch]` Same gap for the host's `AbilityCastSystem` (line ~228) — team-aware ability targeting was verified only via a hand-built `EffectContext(..., alliances: mask)` (`AllianceTargetFilterTests`), never through the live host, so dropping the mask arg would silently revert casts to strict faction-equality (allies hit by Enemy-filtered AoE) with no failing test. Added `SimulationHost_WiresAllianceIntoLiveAbilityTargeting_AllyExcludedFromEnemyAoe` driving a real `CastAbility` order through `host.StepOnce()` with an Enemy-filtered `SearchArea` fireball — the ally is excluded only if the host threads the mask into the cast `EffectContext`.

## Design Notes

**Numbering drift (do not be confused):** the epics file names this **Story 9.14**; older code comments call the alliance model "7.14"/"7.12" and this lobby-seeding work "Story 9.15", and the palette "10.8" ships as `FactionPalette`. The capabilities all exist under those files; only the labels drifted. `AllianceStore`'s "seeded by 9.15 later" comment refers to THIS story.

**Canonical team-id encoding (the load-bearing invariant).** Lobby/scenario teams are ordinals (1,2,…), but `AllianceStore` team ids must be valid faction slots. Map each team to the **lowest faction slot among its members**; e.g. slots {1,2}=teamA → id 1, {3,4}=teamB → id 3. This keeps every id in `[1,8]`, keeps `WinConditionSystem`'s scans correct, and makes `Team==0` degenerate to FFA (`TeamId[f]=f`) — byte-identical to today.

```
FFA (no teams):   TeamId = [_,1,2,3,4,5,6,7,8]   (index 0 = Neutral, untouched)
2v2 {1,2}{3,4}:   TeamId = [_,1,1,3,3, ... ]
```

**Why no checksum re-baseline.** `AllianceStore` is already folded (v20) and a default/FFA store folds identically to `Mix((int)f)` per faction. In FFA no two distinct factions are allied, so every new `AreAllied` combat branch is a no-op → existing goldens are byte-identical. Only genuinely-teamed matches change checksums, which is correct and covered by the new 2v2 determinism test. `MatchAgreementHash.AlgoVersion` (the handshake hash) bumps 1→2 independently; that is not a `SimChecksum` golden.

**Teams live in the model, not the wire.** The agreement hash is recomputed by each peer from its applied `ScenarioData`; the roster is derived, not transmitted. Teams therefore must be a `ScenarioData` field so all content-hash-verified peers derive the same hash — a team mismatch then fails closed pre-tick-0 through the existing `HandshakeGate`/`ServerLobbyPolicy` path with no new protocol.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the new `AllianceSeeder`, combat allied-exclusion, `MatchAgreementHash` team-fold, fog shared-vision, and 2v2 two-run determinism tests, AND every pre-existing golden-checksum test unchanged (proves FFA byte-identity — no re-baseline).
- `dotnet build godot/godot.csproj` -- expected: clean; the new Godot-free `AllianceSeeder` and the threaded `AllianceStore` args raise no banned-API analyzer findings.

**Manual checks:**
- In-engine (optional, via `/godot-verify`): load a 2v2 scenario, confirm the lobby shows team glyphs per slot, allies don't auto-attack or force-fire each other, and with shared vision enabled a teammate's scouted area is revealed on your fog.

## Auto Run Result

Status: done (follow-up review pass 2 on an already-`done` spec)

**Summary:** No production code changed this pass. A fresh adversarial + edge-case + verification-gap + intent-alignment review of the full 9-14 diff (since `2f72b72`) surfaced that the prior follow-up added live-host wiring guards for `CombatSystem` and `FogOfWarSystem` but not for the host's other two mask consumers — `ProjectileSystem` (allied splash) and `AbilityCastSystem` (team-aware targeting). Both wires are correct in the current code, but a future dropped `Alliances` constructor arg would silently break live allied-splash / ability-targeting with every test still green (all goldens are FFA no-ops; the only splash/ability tests hand-build the system with an explicit mask). Two additive host-driven regression guards close that gap.

**Files changed (this pass):**
- `godot/ProjectChimera.Sim.Tests/Combat/AlliedCombatExclusionTests.cs` — added `SimulationHost_WiresAllianceIntoLiveProjectileSplash_AllyExcludedFromSplash` (drives `host.StepOnce()` with an owner-fired splash projectile; ally excluded, enemy/neutral/primary hit).
- `godot/ProjectChimera.Sim.Tests/Effects/AllianceTargetFilterTests.cs` — added `SimulationHost_WiresAllianceIntoLiveAbilityTargeting_AllyExcludedFromEnemyAoe` (drives a real `CastAbility` order with an Enemy-filtered `SearchArea` fireball through the live host; ally excluded) + supporting usings and an in-code ability factory.
- `_bmad-output/implementation-artifacts/deferred-work.md` — one new defer (held auto-acquired `AttackTarget` not alliance/faction-rechecked on entity-id recycle).
- spec triage log + frontmatter (this file).

**Review findings breakdown:** 2 patches applied (both medium — the ProjectileSystem and AbilityCastSystem host-wiring test guards); 1 deferred (held-target recycle re-check gap, a distinct site from the already-ledgered projectile-primary-recycle defer); 12 rejected. Rejections include findings already tracked in the ledger from prior passes (SharedTeamVision has no user-facing toggle; scenario `Team` authoring is unvalidated) and out-of-scope-per-intent items (the lobby intentionally only *displays* teams — authoring lives in `ScenarioData`, per the intent's "Never" clause), plus a speculative release-gate nullable concern that did not materialize (main build clean; `#nullable enable` suppresses rather than introduces CS8632 in the two files).

**Verification performed:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — Build succeeded, 0 errors.
- `dotnet test .../ProjectChimera.Sim.Tests.csproj` (full suite) — **3409 passed, 1 skipped, 0 failed** (the 1 skip is pre-existing; the two new tests pass). No golden re-baseline (FFA byte-identity holds).
- `dotnet build godot/godot.csproj` — Build succeeded, 0 errors (13 pre-existing, codebase-wide nullable warnings; none new).

**Residual risks:** The two new guards are test-only and cannot regress runtime behavior. The single deferred item is an obscure same-tick entity-id-recycle edge (one-tick window) that shares a pre-existing same-faction gap; tracked for a holistic fix. `followup_review_recommended` computes `true` (2 medium patched → score 6 ≥ 5), but note this pass changed no production code — the recommendation reflects the test-coverage patches, and the orchestrator owns whether a further pass is warranted.
