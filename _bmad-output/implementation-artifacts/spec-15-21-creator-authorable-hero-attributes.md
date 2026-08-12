# Spec 15-21 — Creator-authorable hero attribute system

**Status:** DONE 2026-08-12 (interactive ultracode session). Sim half commit `4bb9374b` (15-21a), editor
half + in-engine gate in the following commit (15-21b). Tier-1 6504/0/1 at close; SimChecksum stayed 25
(zero per-tick golden movement — the zero-new-folded-state design held); ContentHash 1→2 re-pinned;
SaveGameFile.FormatVersion 6→7. Riders closed: DW-889 (regen_rate validation), DW-768 (FormatVersion pin).

### In-Engine Gate (2026-08-12, godot-mcp bridge, this session)

Drove the real editor end-to-end: CREATE → Unit Card Editor → Promote-to-Hero (ChimeraSwitch toggled) →
ADVANCED → Attributes section renders the no-model hint → WC3 preset applied via the picker →
str/agi/int base + per-level rows and the 6-rule derived-stat mapping editor appear → all six per-point
values read back EXACTLY against wc3.json ([25.0, 0.0017, 0.3, 15.0, 0.0017, 1.0]) → zero editor errors.
Verified against the authoring source with numbers, not appearance. Save was deliberately NOT clicked
(shipped alpha_faction.json must not gain a model from a verification run); the Save path is Tier-1-pinned
(SyncFactionAttributeModel round-trip). One defect found AND fixed during the gate: the mapping SpinBox's
0.05 step snapped per-tick energy_regen coefficients to zero on display and edit — step is 0.0001 now,
re-verified in-engine.

### Deliberate v1 seams (recorded, not hidden)

- Attribute-LIST editing (add/rename/remove declared attributes) is preset-or-raw-JSON for v1; renames
  would need cascading updates into hero dicts + derived rules.
- Attribute badges key to `attribute_model`, but the card panel's live badge pass runs the UNIT validator
  (no faction context); model errors surface at Save/load (fail-closed) + Tier-1 instead.
- `BalanceSuggestionApplier`'s two hand-enumerated `hero.*` switches do not cover `hero.attributes.*`
  (nested dicts don't fit its numeric-leaf path model) — the AI balance flow can't target attributes yet.
- Attributes apply to heroes only (D-1); non-hero units are an additive follow-up.
**Story key:** `15-21-creator-authorable-hero-attribute-system`
**Requirement (Alec, 2026-08-03):** "I want the creator to be able to easily implement their own attribute
hero model. Our attributes should start off with standard latest top ARPG attribute lists as possibilities.
This will require a full implementation." Data-driven, NOT a fixed enum; 7 shipped presets (WC3 with a
primary attribute that feeds attack damage, PoE, D3, D4, Last Epoch, Grim Dawn, Torchlight).

## The three spec-pass decisions (epics.md:4233 — taken with recommended defaults, surfaced to Alec for ratification)

- **D-1 — Heroes only (v1).** WC3 lineage: attributes are a hero mechanic. The MODEL (attribute definitions +
  derived-stat mapping) is faction-level data with nothing hero-structural in it, so extending to regular
  units later is additive (a `base` block on UnitDefinition) — the seam is left open, no rework.
- **D-2 — Authored auto-growth (v1), WC3 style.** Per-hero `per_level` gains apply automatically on level-up.
  No point-spend: an RTS player is commanding an army (APM budget — the WC3 argument), and point-spend needs
  a new wire order + folded unspent-points state + UI. The data model does not preclude adding a
  `points_per_level` spend variant later.
- **D-3 — CLOSED derived-stat vocabulary.** Targets: `max_health`, `attack_damage`, `armor`, `move_speed`
  (the four ModifierStore delta channels), plus `max_energy`, `energy_regen` (the 15.12 seam pair). Closed =
  validator gates fail-closed (the platform's closed-vocab posture; the enum-indexed-array lesson); open
  reflection over Effective* fields would bypass every overflow bound.

## Architecture — determinism first

**Attribute values are pure functions of authored data × folded hero Level** — `value(attr) = base +
per_level × (Level−1)` — so the system introduces **ZERO new folded sim state**:

1. **Resolve at apply (float→Fixed once).** ScenarioApplier flattens the faction's attribute model × the
   hero's authored attributes into per-hero per-stat contributions at the single load boundary: two
   stride-6 authored-constant lanes on HeroStore (`AttrStatBase`, `AttrStatPerLevel` — the
   BaseXpOf/HealthPerLevelOf posture, NOT folded, divergence surfaces transitively).
2. **Modifier-channel stats (hp/dmg/armor/speed) ride the EXISTING growth channel.** HeroXpSystem's
   ReconcileGrowth already applies `(Level−1)` permanent stacks of the hero-growth modifier; the modifier's
   per-stack deltas become `flat *_per_level + attribute-derived per-level`, and a base contribution applies
   as a second one-stack permanent modifier at first reconcile (skipped when all-zero — the DW-678 lesson;
   idempotent via StackRule cap, no new folded flag). Folds through the already-folded ModifierStore.
3. **Energy pair rides the 15.12 seam.** `EnergyRegenSystem.RegenPerTick` (the documented one-place seam)
   gains the attribute term; the clamp ceiling and any max-energy read go through a
   `HeroAttributeRuntime.MaxEnergyBonus` helper. Deterministic (reads folded Level + authored constants);
   effects reach the hash through the already-folded Energy.
4. **SimChecksum.AlgoVersion stays 25. Zero per-tick goldens move** (shipped factions author no heroes; no
   golden mints one).
5. **ContentHash moves (AlgoVersion 1→2)** — the `hero` block is currently ALLOWLISTED as authoring-only in
   ContentFoldCompletenessTests, but its curve fields are ALREADY sim-read (a pre-existing handshake gap:
   two peers with different hero curves agree at the lobby), and 15-21 adds more sim-read authored fields.
   Fold the sim-read hero fields + the faction attribute model; re-pin per the regen_rate DW-265 precedent.

## Data model

Faction JSON (lenient loader; strictness in FactionValidator):

```json
"attribute_model": {
  "attributes": [ { "id": "strength", "name": "Strength", "description": "..." }, ... ],
  "derived": [ { "attribute": "strength", "stat": "max_health", "per_point": 19.0 },
               { "attribute": "primary",  "stat": "attack_damage", "per_point": 1.0 } ]
}
```

`"primary"` is the WC3 selector: the row applies to whichever attribute the hero flags as primary.

HeroDefinition (dual-path DTO rules: plain floats/strings/dicts, settable auto-props, no enums, no Fixed):

```json
"attributes": { "primary": "strength",
                "base":      { "strength": 22, "agility": 14, "intelligence": 16 },
                "per_level": { "strength": 2.5, "agility": 1.4, "intelligence": 1.8 } }
```

Presets: 7 JSON files under `godot/resources/data/attribute-models/` (the behaviors-directory precedent),
loaded by the editor's preset picker; the WC3 preset authors the classic mapping (str→hp, agi→armor,
int→max_energy+energy_regen, primary→attack_damage).

## Validator rules (FactionValidator + UnitDefinitionValidator.ValidateHero)

Attribute ids unique/non-empty; `derived[].attribute` ∈ declared ids ∪ {"primary"}; `derived[].stat` ∈ the
closed 6-stat vocabulary (fail-closed); `per_point`/`base`/`per_level` finite with overflow-safe bounds
(contributions × 99 stacks must fit Fixed — the DW-488 runtime-consistency posture); a hero authoring
`attributes` requires the faction to declare a model; `primary` ∈ declared ids; `base`/`per_level` keys ∈
declared ids. DW-889 rider: `regen_rate` gains its missing authoring validation in the same pass.

## Editor (Unit Card hero section — Godot-coupled, in-engine gate)

Preset picker (loads an attribute-model JSON into the faction), attribute list editor (id/name/description
rows), per-hero attribute editing (primary dropdown + base/per_level per declared attribute), derived-stat
mapping editor (closed stat dropdown). Persists via the existing FactionWriter path; ChimeraValidationBadge
per field; EditorHistory undo; BalanceSuggestionApplier's two hero.* switches gain the new fields.

## Out of scope (recorded)

Attribute points UI/spend orders; attributes on non-hero units; per-attribute icons; tooltips DSL exposure
(`unit.strength` reads) — follow-ups if Alec wants them.
