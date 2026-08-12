# Spec 15-23 — Generation-validated entity references (DW-775)

**Status:** in dev (2026-08-12, interactive ultracode session)
**Story key:** `15-23-generation-validated-entity-references-dw-775`
**Baseline:** master @ post-bmad-@next-update, Tier-1 6462 pass / 0 fail / 1 skip, SimChecksum.AlgoVersion 24.

## Problem

After DW-444/DW-446, a slot recycled into a *different hostile* faction is still silently inherited on every
held-id path: the attacker keeps firing at a unit nobody aimed at, and an in-flight shell detonates on it.
The close-out (per DW-775's own text) is the DW-184 pattern applied to entity space: every cross-tick holder
of a raw entity id becomes a generation-validated packed ref (`EntityWorld.PackRef`/`TryResolveRef`, which
ALREADY EXIST — this story converts holders, it does not build the mechanism).

## Design decisions (D-1 … D-9)

- **D-1 — Packed refs are stored IN the existing fields** (AttackTarget, CommandTarget entity halves,
  ProjectileStore.TargetId/SourceId, OrderQueue target payloads, ModifierStore._casterId,
  DslLoopState._rowIds, ScenarioDirector._carryKiller). Matches the `BuildTarget`/`HeroIndex`/
  `WinConditionSystem._leaderRef` precedents. `-1` stays the universal none-sentinel
  (`TryResolveRef(-1)` is false by construction). Golden-neutral at generation 0: `PackRef(id) == id`.
- **D-2 — Pack at ISSUE time on the wire paths** (SelectionSystem offline/lockstep issue, AI direct writes),
  blind-store at apply, resolve at consumption — the exact convention `AttackBuilding`/`PickupItem` already
  use for building/item refs on the same wire fields. Closes the issue→apply delay window too. Wire FORM
  (11-byte UnitOrder, int payloads) is unchanged; wire SEMANTICS change → `PROTOCOL_VERSION` 3→4 and
  `ReplayRecorder` format 5→6 (fail-closed against mixed builds / stale replays).
- **D-3 — Consumption semantics on a stale ref** (per path, mirroring what each path already does for a
  dead target):
  - `ValidateOrClearTarget` (held AttackTarget): clear + strip `Attacking`; caller re-acquires via
    SpatialHash the same tick (a legal hostile successor may be *consciously re-acquired* — that is
    correct behavior, distinct from silent inheritance).
  - `TickAttackTargetCombat` / `TickFollowCombat` (CommandTarget): revert to Idle (the existing
    invalid-target arm; the `TickAttackBuildingCombat` template).
  - `OrderQueueSystem` pop: a queued order whose packed target no longer resolves is DROPPED and the next
    queued order dispatches the same tick (deterministic skip — the order's target is gone).
  - `ProjectileSystem` (entity half): `targetAlive=false` → coast to `LastKnownPos`, drop harmlessly, no
    hit/splash — byte-identical to the existing died-in-flight arm and the building half.
  - `SourceId` / `_casterId` / `_carryKiller` (attribution payloads): DEGRADE to `-1`/unknown, never
    retarget — kill credit and pulse caster identity must not transfer to a recycled occupant.
  - `DslLoopState` drain: unresolved row is SKIPPED (today's IsAlive skip, tightened).
- **D-4 — No fold-set change; AlgoVersion 24→25.** No array is added to or removed from the hashed set.
  Folded lanes that now carry packed values (CommandTarget v4, OrderQueue rings v9, DslLoopState v17) move
  only where generation > 0 at a checksum boundary. The bump labels the value-semantics change (the v24
  re-record-marker precedent) and, per DW-874 (keep one constant, fail-closed), breaks all existing saves —
  accepted policy. `EntityWorld.Generation` itself stays UNFOLDED
  (`EntityRefPackingTests.Generation_IsNotFoldedIntoSimChecksum` remains true and green).
- **D-5 — AttackTarget / ProjectileStore stay UNFOLDED** (their divergence still surfaces transitively via
  Health/Position; packing does not change that posture). The ModifierStore "authored / peer-identical"
  caster comment is CORRECTED (pack-at-install is deterministic, which is the true reason it needs no fold).
- **D-6 — HeroStore.EntityId is NOT converted** — its `IsLiveLinkedHero` back-link round-trip through the
  packed `HeroIndex` is already ABA-safe; this story adds the pinning regression test + doc instead of a
  speculative rebuild (the DW-763 "no invented work" rule).
- **D-7 — ItemStore.CarrierHeroSlot (raw HERO slot) is OUT of scope** (hero-slot ids, not entity ids; no
  in-match hero-row recycle path exists; the DropAll invariant covers destroy). Filed as a fresh ledger
  entry instead of silently absorbed.
- **D-8 — DslEventQueue params / DslVarTable ints are opaque creator payload by contract** — documented,
  not structurally guarded (consumer-side validation is the DSL's contract).
- **D-9 — Save compat:** lane SHAPES are unchanged → no `SaveGameFile.FormatVersion` bump; the AlgoVersion
  header reject already fail-closes every pre-15-23 save (DW-874 one-constant policy). Existing lane
  validation is audited so packed values pass restore validation.

## Ledger closures

- **DW-775** (primary) — both held-id combat paths + the whole raw-id class.
- **DW-869** (rider) — the DW-664 pause arm's CommandTarget is validated by the same packed-ref consumption.
- **DW-862** (rider) — `GoldenHarness.PerturbTargetId` type confusion: split/rename so entity- and
  building-slot perturbations are distinct, matching the packed-ref typing.

## Pinned tests deliberately flipped (conscious, not regression)

- `HeldAutoTarget_RecycledIntoAnotherEnemy_KeepsFiring_GuardIsNotOverBroad` → rewritten: the packed ref is
  dropped; a same-tick *re-acquisition* of the legal successor is asserted via the sharper 3-unit form
  (successor far, closer legal enemy present → re-acquisition picks the closer enemy; inheritance would not).
- `Projectile_PrimaryTargetRecycledIntoAnotherEnemy_StillDetonates_GuardIsNotOverBroad` → inverted: the
  shell now drops harmlessly (mirrors the ally-recycle arm).

## Adversarial review outcome (2026-08-12, 3-lens workflow + per-finding verification)

Nine findings; disposition:
- **FIXED in-story:** BuyItem's buying-hero entity id now rides the wire PACKED (the one wire entity payload
  the census missed — IssueBuyCommand packs, `ItemSystem.TryResolveHero` resolves, denial on recycle; pinned
  by `Buy_HeroEntityRecycledIntoAnotherHero_DeniesInsteadOfRedirecting`). `unit_damaged`'s attacker payload
  packs at push + resolves at drain (a revival respawn at system index 11 CAN recycle between the push at
  9/10 and the drain at 17 — the reviewer's "unreachable" claim was wrong in the unsafe direction).
  Generation-overflow guard added at the recycle bump (loud deterministic throw at MAX_PACKABLE_GENERATION —
  confirmed finding; previously only the save-load gate enforced the bound). Four stale docs/harness
  raw-id references updated (CastAbility mode table, BuyItem enum doc, EnqueueTargetedCommand contract,
  AbilityTestSupport.PendSlotAndTick now packs).
- **FILED as fresh DW (the re-file-the-residual rule):** DW-945 (UnitOrder.UnitId — the order SUBJECT — is
  dual-id-space and stays raw; its conversion is a per-command-family split, its own story), DW-946
  (selection/control groups prune on IsAlive only — presentation-side ABA, src/UI + in-engine verify).

## Re-record procedure (spec-15-22 verbatim)

Bound movement; Windows-only record (`CHIMERA_GOLDEN_RECORD=1 dotnet test` → `dotnet build` → full suite
`--logger trx`); diff `grep -v '^#'`; movement ledger attributing every moved golden to DW-775; never
re-freeze the differential-guard control; stage by path; AlgoVersion pin sites (14 Assert sites +
SimChecksumCoverageGuardTest rename V24→V25 + VersionStampConsistencyTests SimChecksum 24→25,
PROTOCOL_VERSION 3→4, Replay 5→6) updated in the same commit.
