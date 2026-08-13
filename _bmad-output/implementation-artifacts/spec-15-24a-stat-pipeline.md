# Spec 15-24a — THE STAT PIPELINE (StatVocabulary registry) — AS-BUILT record

**Status:** BUILT + GREEN (2026-08-12 ultracode session, single conversation). Parent:
`spec-15-24-attribute-system-2.md`. Commits `3b418039`, `bd97d1d3`, `0c32bcc3`, `2be2dcf5`, `1df7570e`
(+ the review-sweep commit that follows). Suite **6977 / 0 / 1** at close (day started 6949); release
RS0030 analyzer gate CLEAN (and un-broken: two pre-existing master RS0030s from session 3 suppressed with
recorded non-sim justifications — RejoinTokens crypto RNG, ContentPackager quarantine GUID).

## Version stamps moved by this story (both LAN machines MUST pull + rebuild together)

SimChecksum **25→26** (BOUNDED fold — zero golden movement; frozen v22 control + known-state pin
0x32911831 both untouched, now four bumps unchanged) · CanonicalModelHash **16→17** · ContentHash **2→3**
· SaveGameFile FormatVersion **9→10** (all pre-10 saves fail-closed — DW-874 posture) · Replay 7 and
PROTOCOL 6 untouched. Goldens moved: hero-start-state (re-recorded, CanonicalModelHash), mapgen hash pin
(stamp digit), + ONE NEW golden `stat-pipeline-scenario` (300 ticks, cross-platform, both CI legs).

## The add-a-stat recipe, as shipped

(1) append a `StatId` member → (2) add its `StatVocabulary.All` row → (3) write its ONE consumer read at
the declared site. The rails that hold it honest: `StatVocabularyGuardTests` (registry completeness),
`StatConsumerTripwireTests` (DECLARED-BUT-NEVER-CONSUMED source scan over each stat's ConsumerEvidence
token), and `StatRecomputeTests.EveryModifierAuthorableStat_ObservablyMovesItsConsumerChannel` (the
behavioral sweep whose hand-map is its own completeness gate). Validators, editors' vocabulary source,
draft carriage, affix eligibility, authoring bounds, save stride, and the checksum fold all follow from
the registry row with no further edits.

## What this story builds

The spec's add-a-stat recipe: a **StatVocabulary registry** (one declaration per stat), a **sparse
stat-delta vector on Modifier** replacing the hand-wired 4-channel, a **generalized recompute**
(flat sums → ×(1+Σ%) → clamps), registry-driven validator gating, four new consumer wires
(attack_speed, health_regen, vision_range, cooldown_reduction) plus four percent siblings, and the
two tripwire guard tests. Adding stat #N later = one registry entry + one consumer read.

## The vocabulary shipped in this story (StatId, append-only, explicit values)

| StatId | JsonName | Aggregation | Consumer (evidence token) | Notes |
|---|---|---|---|---|
| 0 MaxHealth | max_health | Flat | `EffectiveMaxHealth` | legacy; ceiling-collapse + heal-on-apply semantics preserved |
| 1 AttackDamage | attack_damage | Flat | `EffectiveAttackDamage` | legacy; projectile snapshot asymmetry preserved |
| 2 Armor | armor | Flat | `EffectiveArmor` | legacy |
| 3 MoveSpeed | move_speed | Flat | `EffectiveMoveSpeed` | legacy |
| 4 MaxEnergy | max_energy | Flat | `MaxEnergyOf` (15.12 seam) | attribute-targetable; **NOT modifier-authorable yet** (recorded seam) |
| 5 EnergyRegen | energy_regen | Flat | `RegenPerTick` (15.12 seam) | same posture as MaxEnergy |
| 6 AttackSpeed | attack_speed | Percent | `EffectiveAttackInterval` | +0.15 = 15% faster; interval = AttackSpeed/(1+Σ), floor FixedDt for attackers |
| 7 HealthRegen | health_regen | Flat | `EffectiveHealthRegen` | per-TICK HP (regen_rate convention); new HealthRegenSystem; effective floors at 0 |
| 8 VisionRange | vision_range | Flat | `EffectiveVisionRange` | new Effective array; elevation method renamed `VisionWithElevation` |
| 9 CooldownReduction | cooldown_reduction | Percent | `EffectiveCooldownReduction` | armed = SecondsToTicks(cd × (1−CDR)); CDR clamped [−4, +0.8] |
| 10 MaxHealthPercent | max_health_percent | Percent | recompute pairing → MaxHealth | no arrays, no lanes, no fold (accumulator-only) |
| 11 AttackDamagePercent | attack_damage_percent | Percent | recompute pairing → AttackDamage | ditto |
| 12 MoveSpeedPercent | move_speed_percent | Percent | recompute pairing → MoveSpeed | ditto |
| 13 VisionPercent | vision_percent | Percent | recompute pairing → VisionRange | ditto (catalog #34 name) |

Modifier-authorable set = all except MaxEnergy/EnergyRegen (per-stat `ModifierAuthorable` flag;
the energy pair's modifier channel is a recorded follow-up because the 15.12 seams are static and
hold no ModifierSystem ref). All 14 are attribute-targetable (AttributeStats identity-maps to StatId).

## Design decisions

- **D-1 — StatId is a closed enum, explicit append-only values**; first six pin the 15-21
  AttributeStats order exactly (load-bearing indices). Registry completeness guard closes the
  enum-indexed-array class: every member has exactly one StatDefinition, id == array index,
  JsonNames unique, percent targets valid.
- **D-2 — StatDefinition** carries: Id, JsonName, DisplayName, Aggregation (Flat/Percent/Chance/
  PerHitMagnitude/AuraRadius), Tier (Recompute/ReadSite/Proc/Aura/Threshold), PercentTarget,
  Fixed effective-clamp bounds, per-delta authoring bound, AttributeTargetable, ModifierAuthorable,
  AffixEligible, ConsumerEvidence token + consumer file hint. Bounds are named-constant-derived
  (CHM0004) and deliberately NOT EffectCaps entries (RulesetHash stays still).
- **D-3 — Modifier holds `StatDelta[] StatDeltas`** (readonly struct {StatId Stat; Fixed Delta},
  canonical: ascending StatId, no zeros, no duplicates — canonicalized at construction). The four
  legacy names REMAIN public readonly Fixed FIELDS as derived projections of the vector (the
  fold-shape pin forbids properties; ~348 read sites keep compiling; classified excluded-as-derived
  in EffectFoldCompletenessTests, StatDeltas joins the folded list). The existing 12-arg ctor stays
  as a compatibility overload building the vector; a new primary ctor takes the vector. HasNoEffect
  = empty vector ∧ no status ∧ no period.
- **D-4 — Generalized recompute, bit-identical for legacy content.** Per dirty entity:
  percent-stat sums first (clamped), then per value stat
  `Effective = clampBounds( MulSaturating( AddSaturating(Base, Σflat), One + pct ) )`.
  ×One is exact in 16.16; legacy bounds are [0, MaxValue] → byte-identical when no percent/new
  stats are authored (pinned by parity tests before any golden run). `Fixed.MulSaturating` is new
  (widen-shift-clamp, matching operator* rounding).
- **D-5 — EntityWorld arrays**: named SoA arrays stay; new arrays `EffectiveAttackInterval`
  (mirror of AttackSpeed at rest), `BaseHealthRegen`+`EffectiveHealthRegen` (def field
  `health_regen`, per-tick, default 0), `EffectiveVisionRange` (mirror of VisionRange at rest;
  elevation method renamed `VisionWithElevation`), `EffectiveCooldownReduction` (0 at rest).
  All wired through Create/ApplyUnitDefinition/Clear/RestoreUnit + snapshot rules. Percent stats
  get NO arrays (accumulator-only; rebuilt from the ring on load via RestoreSlot).
- **D-6 — Consumers**: CombatSystem re-arms from `EffectiveAttackInterval` (:878/:940); new
  `HealthRegenSystem` cloned from EnergyRegenSystem (per-tick, zero-early-out, clamp to
  EffectiveMaxHealth, can never kill) inserted after EnergyRegen — SystemOrderTest + index comments
  updated; FogOfWar's single seam reads the effective array; AbilityCastSystem:484 scales
  `ab.Cooldown × (One − CDR)` before SecondsToTicks. Machine-gun guard: interval floor = FixedDt
  when base > 0; base-0 non-attackers keep 0 (mirror-exact, bounded-fold-friendly).
- **D-7 — Authoring**: legacy 4 keys permanent aliases (written unconditionally, as today, for
  byte-stable round-trips); new stats via `"stat_deltas": { "<json_name>": <value> }` in ability
  modifier JSON (allow-list += stat_deltas; registry-gated keys; duplicate legacy+vector = reject;
  omit-when-empty on write, ascending StatId), `Dictionary<string,Fixed>` on ItemDefinition,
  `Dictionary<string,float>` on ResearchModifierDelta (dual-path DTO safe), FactionWriter.PutLevels
  extended (omit-when-zero per key, whole object omitted when empty). DraftModifier carries extras
  losslessly (ToModifier/FromModifier symmetric); AbilityPresetMatcher.IsSimpleSelfBuff requires
  every new stat default (never widen the PeriodicStacking hole).
- **D-8 — Tripwires.** CONSUMED-BUT-UNDECLARED: closed by construction (StatId-keyed everything +
  completeness guard). DECLARED-BUT-NEVER-CONSUMED: every Active stat declares ConsumerEvidence
  (literal source token, e.g. `EffectiveAttackInterval` or `EffectiveMaxHealth`); a guard test
  walks godot/src/** (excluding the registry file) and fails if the token is absent. Percent stats
  record the recompute as consumer and are pinned by recompute tests.
- **D-9 — No parked entries.** The 50-catalog stays in the parent spec; the registry declares only
  stats with live consumers. The add-a-stat recipe is documented in the registry file header.
- **D-10 — Hero/attribute lane.** AttributeStats becomes a facade over the registry (Count/Ids/
  TryIndexOf identity-map StatId); HeroStore stride grows 6→14 (save v10 covers it, no migration —
  DW-874 posture); HeroXpSystem's two mints build sparse vectors over attribute-targetable
  ModifierAuthorable stats (energy pair excluded — stays read-site via AttributeStatAt);
  CheckHeroGrowth probe mirrors in the same change (DW-650); FactionValidator per-stat per_point
  caps from the registry; the 15-21 editor dropdown follows Ids with zero UI-file changes.
- **D-11 — Research lane.** ResearchStore internally stat-indexed `Fixed[][][]`; the four legacy
  `Fixed[][]` fields alias the outer arrays (SimChecksum/SaveGameState/SelectionSystem reads
  unchanged); accumulate/rollback/payload/BoundedDelta/NoteBoundTruncation walk the vector;
  research JSON gains stat_deltas (D-7). SimChecksum keeps the four hand-named folds verbatim and
  appends a BOUNDED per-stat fold for new-stat cumulative values (nonzero-gated, stat id mixed).
- **D-12 — Determinism/version bumps.**
  - SimChecksum 25→**26**: BOUNDED per-entity fold of the four new Effective channels (gate:
    any of effInterval≠AttackSpeed ∨ effRegen≠BaseHealthRegen ∨ effVision≠VisionRange ∨ effCDR≠0)
    + the bounded research fold. Zero Mix calls for untouched entities ⇒ **zero golden movement
    expected**, frozen v22 control untouched, known-state pin 0x32911831 unchanged (v23 posture).
    Coverage teeth added per new lane; SaveLoadTests' literal 25 → 26.
  - CanonicalModelHash 16→**17** (MixModifier folds the vector: count + (int)Stat + Delta.Raw,
    ascending, replacing the four .Raw folds); ContentHash 2→**3** (FoldItems/FoldResearch gain
    sorted-key dict folds); both absolute pins re-pinned; hero-start-state golden re-recorded.
  - SaveGameFile 9→**10**: five new EA lanes (EffAttackInterval, BaseHealthRegen, EffHealthRegen,
    EffVisionRange, EffCooldownReduction — appended before PatrolWpX), HA attr lanes at stride
    StatCount, research section per-stat lanes. Fail-closed, no migrate (all pre-10 saves die —
    accepted DW-874 posture, and the checksum bump kills them anyway).
  - Replay/PROTOCOL untouched (no wire-format change).
- **D-13 — Heal/collapse generalization.** Heal-on-apply becomes REALIZED ceiling change
  (max(0, ceilingAfter−ceilingBefore) on apply; equals the flat delta in every non-pathological
  case — divergence only under Fixed saturation, unreachable by DW-488-bounded content). The
  collapse gate becomes the pure transition `ceilingBefore > 0 ∧ ceilingAfter == 0` (the form
  RaiseExternalCeilingCollapse already uses); the "stat actually changed" gate scans the vector for
  MaxHealth/MaxHealthPercent membership. DW-85's research heal-suppression seam is preserved.

## As-built deviations from the design draft (both are hardenings)

- **D-5/D-6 superseded by the IDENTITY-TERM architecture:** instead of Effective MIRROR arrays every
  direct base-writer must maintain (EffectiveAttackInterval/EffectiveVisionRange — the first build of
  which machine-gunned every hand-built test scenario that writes `AttackSpeed` directly), the new
  channels store the MODIFIER TERMS with identity defaults: `EffectiveAttackSpeedFactor` (One),
  `VisionBonusFlat`/`VisionBonusPct` (0), `EffectiveCooldownReduction` (0), plus the
  `BaseHealthRegen`/`EffectiveHealthRegen` pair (def-derived, no direct writers exist). Consumers:
  `EntityWorld.AttackIntervalOf` (divide + one-tick machine-gun floor) and `VisionWithElevation`
  (merge). Direct `AttackSpeed`/`VisionRange` writers need NO mirror — the defect class is eliminated
  structurally, and the whole mirror-churn (fallback spawns, snapshot branches, ~55 test builders)
  reverted to untouched.
- **D-13's collapse gate keeps a third conjunct:** the pure before/after transition is NOT sufficient —
  a DW-488-pathological all-positive grant that WRAPS the accumulator must stay a benign zombie
  (pinned by `AccumulatorWrapFromAPositiveGrant_IsNotLethal`), so the gate requires a ceiling-LOWERING
  term: some MaxHealth/MaxHealthPercent entry with `delta × multiplier < 0`.

## Out of scope (recorded seams, each gets a DW)

- Editor spinbox rows for new stats in Ability/Item/Research panels (raw-JSON panes author them
  today; rows land with 15-24f, in-engine-gated). The 15-21 attribute-mapping DROPDOWN follows the
  registry automatically (AttributeStats facade — zero UI-file changes) but its widened list is
  UNOBSERVED in-engine this session (bridge not attached; observe at next editor session).
- MaxEnergy/EnergyRegen modifier-authorability (static 15.12 seams need a ModifierSystem ref;
  declared `ModifierAuthorable: false`, validator + converter fail-closed with a naming message).
- Building vision float-delegate path (vision stats reach units only).
- SelectionSystem research-upgrade summary line shows only the legacy four stats (gated UI file).
- New-stat contributions in the buff-bar polarity net-sum are sign-correct but unit-mixed (as the
  legacy sum already was); per-stat tooltips land with 15-24f.

## Verification plan

Parity tests (legacy recompute bit-identity) → registry guard + tripwires → per-consumer behavior
tests → new cross-platform golden `stat-pipeline-scenario` exercising attack_speed/CDR/regen/
vision/percent → save v10 round-trip with a live new-stat modifier → zero-movement check over all
33 goldens (`git status -- '*.golden.txt'`) → hero-start-state re-record (Windows) → full suite +
release analyzer gate → multi-lens adversarial review workflow.
