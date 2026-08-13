# Spec 15-24 — Attribute System 2.0 (Alec's 2026-08-12 Q3 rulings)

**Status:** IN PROGRESS — **legs a + b BUILT + GREEN + review-swept** (15-24a THE STAT PIPELINE
2026-08-12, 15-24b deterministic crit/dodge dice 2026-08-13; as-built record + deviations in
`spec-15-24a-stat-pipeline.md`, which supersedes this file's a/b sections where they differ). Registry at
17 stats; version stamps at SimChecksum 27 / CanonicalModelHash 17 / ContentHash 3 / Save 11; zero
existing goldens moved across all three checksum bumps. Residual legs c–g below remain SPEC'D, backlog;
open seams DW-991..996. Original spec basis: groundwork decisions locked 2026-08-12, sequenced after
15-1 reconnect + 15-14 identity per the same session's Q1/Q2 build-now rulings. Builds directly on
15-21's shipped substrate.

## The rulings (verbatim intent)

- **Q3a — stat targets: ALL proposed + crit/dodge, "and even more than that."** Attack speed, health regen,
  vision range, cooldown reduction all in; crit/dodge explicitly wanted (the WC3-RPG lineage — variable
  numbers, items feeding the same stats). The FULL expanded vocabulary is an open conversation item — the
  proposal list below goes back to Alec for selection.
- **Q3b — shapes: ship thresholds + percentage scaling first; the EFFECT-GRAPH hook is the ultimate goal**
  and every groundwork decision must keep it reachable (derivation rows stay a graph-friendly data shape —
  a row is a degenerate one-node graph; the composer upgrade slots in without a format break).
- **Q3c — a DEDICATED Attribute Editor** (its own Ctrl+letter editor beside Ability/Trigger/Faction, per the
  keymap policy), deep enough to build whole attribute models — **and the AI must be able to drive it**
  (the ability editor's LLM-draft pattern: plain language → a real attribute model rendered in front of the
  creator, confirm/reroll — the standing AI-transparency vision).
- **Q3d — scope 3: heroes + creator-opt-in units + VETERANCY** — regular units EARN attribute growth from
  kills (WC3-veterancy-but-modern), riding the same growth-modifier channel heroes use.
- **Q3e — growth mode is a TOGGLE: creator chooses auto / player-spent / player's-choice-in-game.** The
  campaign will most likely run auto.

## The expanded-vocabulary proposal (the "even more" conversation — Alec picks)

Already in (15-21): max_health, attack_damage, armor, move_speed, max_energy, energy_regen.
Ruled in (Q3a): attack_speed, health_regen, vision_range, cooldown_reduction, crit_chance, dodge_chance.
**Proposed additions to choose from:** crit_multiplier (how hard crits hit) · lifesteal (% of damage dealt
returned as HP) · spell_power (% amplifier on ability effect magnitudes — the "int scales your fireball"
stat) · cast_cost_reduction (% off energy costs) · block (flat damage shaved per hit — the armor-sibling) ·
max_health_percent / attack_damage_percent (multiplicative siblings of the flat stats) · xp_gain (% —
already exists per-hero as xp_per_kill; exposing it as an attribute target is nearly free) ·
gather_rate (% worker yield — economy heroes/veterans) · build_speed (%).

## Ruling update (Alec, 2026-08-12, second pass)

**The entire proposed pick-list is IN** (crit_multiplier, lifesteal, spell_power, cast_cost_reduction,
block, %-siblings, xp_gain, gather_rate, build_speed) — plus a 50-candidate expansion list requested for
multiple-choice selection ("and even more than that").

**STRUCTURAL DECISION — the SHARED STAT VOCABULARY:** Alec: "A lot of these will overlap with what items
can give you through suffixes and prefixes and other modifiers." The stat vocabulary is therefore designed
ONCE as a shared substrate: attributes, item affixes (prefix/suffix rolls), tomes/consumables, auras, and
research all target the same closed, validator-gated stat set through the same modifier/recompute channels.
An item affix system (rolled prefixes/suffixes with tiers — the Diablo lineage) becomes sub-story 15-24g,
consuming the vocabulary 15-24a builds. One stat, one channel, many sources — never per-source stat forks.

## Ruling (Alec, 2026-08-12, third pass): ALL 50 ARE IN — "add them all, i love them"

49 active + #35 detection [PAIRS: invisibility] parked until stealth exists. And the load-bearing
requirement: **"ensure that the pipeline to add more is easily done because we will be adding more in the
future."** That requirement REDEFINES 15-24a — see THE STAT PIPELINE below.

## THE STAT PIPELINE (the add-a-stat recipe — 15-24a's real deliverable)

At ~65 stats the per-stat hand-wired channel approach (today's closed 4 modifier deltas) is dead. 15-24a
builds a **StatVocabulary REGISTRY** — one declaration per stat: id, display name, aggregation kind
(flat / percent / chance / per-hit magnitude / aura-radius), value bounds, and its CONSUMER SITE tag.
Everything else derives from the registry automatically: validator gating, editor dropdowns (Attribute
Editor + affix editor), item-affix eligibility, the LLM-draft vocabulary, Modifier's sparse stat-delta
list (replacing the 4 named fields), the generalized recompute (flat sums then ×(1+Σ%) then chance clamps),
and the save lane (one sparse vector, stride-free).

**Adding stat #66 later = (1) one registry entry, (2) one consumer read at its tagged site (a
recompute-tier stat's consumer is a one-line Effective read; a mechanic-tier stat — an on-hit proc, an
aura — implements its proc site once). Nothing else to touch.** Two guard tests make the recipe safe:
a DECLARED-BUT-NEVER-CONSUMED tripwire (a registry entry no consumer reads fails loudly — the
computed-but-never-consumed class) and a CONSUMED-BUT-UNDECLARED tripwire (no stat read outside the
registry). Implementation tiers, recorded per stat in the registry: RECOMPUTE (pure stat math — most of
the 50), PROC (on-hit/on-kill sites, needs the 15-24b dice where flagged [RNG]), AURA (radiating — the
four [AURA] entries pull the effect-graph groundwork forward, accepted), THRESHOLD (the 15-24c shapes).

## The 50-candidate expansion catalog (ALL RULED IN 2026-08-12; #35 parked on invisibility)

Numbered 1-50 (all in). Flags: [RNG] = rides the 15-24b deterministic dice; [AURA] = radiates to
nearby allies (the effect-graph groundwork in stat form); [PAIRS:x] = requires mechanic x to land first.
Full list mirrored in the session hand-off; every pick enters the SHARED vocabulary (attributes + affixes).

Offense: 1 cleave 2 splash_radius_bonus 3 pierce_chance[RNG] 4 extra_projectiles 5 armor_penetration
6 damage_vs_armor_type 7 damage_vs_tag 8 onhit_bleed 9 onhit_poison 10 onhit_slow 11 onhit_stun[RNG]
12 onhit_manaburn 13 overkill_carryover 14 execute_threshold 15 first_strike 16 spell_crit[RNG]
17 thorns 18 retaliate[RNG] 19 kill_frenzy 20 siege_bonus
Defense: 21 physical_resist 22 magic_resist 23 block_chance[RNG] 24 barrier 25 last_stand 26 tenacity
27 healing_received 28 out_of_combat_regen 29 revive_speed 30 aura_armor[AURA]
Utility: 31 move_speed_percent 32 slow_resistance 33 collision_shrink 34 vision_percent
35 detection[PAIRS:invisibility] 36 inventory_slots 37 shop_discount 38 cast_range 39 aoe_radius
40 ability_duration 41 summon_power 42 cdr_on_kill
Economy/leadership: 43 train_speed_aura[AURA] 44 supply_bonus 45 kill_bounty 46 unit_cost_reduction
47 xp_share_radius_bonus 48 revive_cost_reduction 49 aura_move_speed[AURA] 50 aura_regen[AURA]

## Sub-story partition (each with its own determinism posture)

- **15-24a — stat channels:** the non-RNG targets (attack_speed, health_regen, vision_range,
  cooldown_reduction + chosen % siblings). Requires generalizing the modifier delta set (today a closed 4)
  or a stat-vector recompute — the design decision that shapes everything downstream; percentage shapes
  need a base×(1+Σ%) recompute step ordered after flat sums. Fold: new Effective channels that mutate
  mid-match fold on first-mutability (the standing rule); one scheduled bump + re-record.
- **15-24b — deterministic combat rolls (crit/dodge):** combat gains SimRng draws at the hit site —
  deterministic, folded via the RNG stream, replay-safe, but DRAW-ORDER-SENSITIVE (every peer must draw in
  identical sequence; the draw sits inside DamageResolver at a single point). Moves combat goldens; its own
  bump. This is the gate item RNG-averse reviewers must see consciously: rolls enter the SIM, never
  presentation.
- **15-24c — derivation shapes:** thresholds ("every 25 STR → +1 hp regen"; "at 50 INT → −10% costs") and
  percentage rows in the resolver + validator + editor. Pure resolve-time math — zero new folded state
  (the 15-21 trick holds: values remain functions of folded Level/points).
- **15-24d — veterancy:** creator-opt-in per unit (`veterancy` block on UnitDefinition); units earn
  attribute growth from kills. New folded per-unit progression counter (the HeroStore.Level analogue for
  plain units) — one bump; the growth application reuses the existing modifier-stack channel verbatim.
- **15-24e — spend mode:** creator-authored mode toggle (auto / spent / player-choice); player-spent needs
  UnitCommand.SpendAttributePoint (wire), folded unspent-points, and the level-up UI affordance.
- **15-24f — the Attribute Editor:** dedicated Ctrl+letter panel (attribute list authoring incl.
  add/rename/remove with cascading renames — the 15-21 v1 seam closes here), model-level derivation
  composer (rows now; graph-ready), preset import, per-hero/per-unit assignment view, and the LLM draft
  hook (attribute-model generation from plain language, rendered live).
- **Groundwork-for-the-graph (the Q3b ultimate goal):** derivation rows serialize in a shape the effect
  graph can adopt (attribute source → condition/threshold → contribution leaf); when 24-series matures,
  thresholds become trigger-like nodes and the ability editor's vocabulary plugs in.

## Standing constraints

WC3-but-modern is the taste arbiter; every system data-driven and creator-extensible; determinism fence
rules apply per sub-story (fold-on-first-mutability, one bump per story, bounded folds preferred, goldens
re-record in-story); the closed-vocabulary rule keeps every stat target validator-gated (the vocabulary
GROWS by conscious addition, never opens to reflection).
