# Spec 15-24 — Attribute System 2.0 (Alec's 2026-08-12 Q3 rulings)

**Status:** SPEC'D, backlog (groundwork decisions locked; sequenced after 15-1 reconnect + 15-14 identity,
per the same session's Q1/Q2 build-now rulings). Builds directly on 15-21's shipped substrate.

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
