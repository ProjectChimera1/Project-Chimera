---
baseline_commit: 0853b5f30d1be2f8317979d1a5a7a1f5a440eba3
---

# Story 2.10: Showcase faction signature mechanics — Equal Exchange and Sanguine Furnace passive HoT (D1-only)

Status: review

<!-- Validation: optional. Created via gds-create-story: 3 parallel research agents (ability/passive runtime + effect
leaves · faction/unit content + FMA design · determinism fence + goldens + tests) + direct source grounding
(EffectNodeJsonConverter / DirectHpDeltaEffect / SequenceEffect / all 7 ability JSONs / alpha_faction.json +
beta_faction.json read in full / deferred-work furnace-cap item / 2.9b precedent read end-to-end). -->

## Story

As a player,
I want the Crucible Covenant's Equal Exchange self-cost abilities and the Sanguine Court's passive soul-fed
regeneration to actually run in combat,
So that the two showcase factions finally play with their signature feel instead of resolving as pure stat sheets.

## Why this matters (source-verified)

This story **caps Epic 2** — its stated payoff is literally *"the showcase factions' signature mechanics run with
satisfying combat feedback"* (`epics.md:891`). Every engine primitive it needs already shipped in Stories 2.1–2.9b.
**This is a content / data-authoring story with zero new engine code** — the same shape as Story 2.9b (which was
content + UI + two small fixes, not a sim rewrite). Concretely:

- **Equal Exchange = the `direct_hp_delta` leaf, which already exists and is self-documenting.**
  `DirectHpDeltaEffect.cs:6-15` opens with *"The Equal-Exchange-shaped self-cost primitive… a FLAT,
  armor-INDEPENDENT change to a target's Health pool… deliberately does NOT route through
  `DamageResolver`/`DamageTable` — no damage matrix, no armor scaling."* There is a dedicated
  `EffectExecutorEqualExchangeTests.cs` already proving it. AC1's "DirectHpDelta, not the matrix Damage leaf" is not
  a thing to build — it is a leaf to **author into a Sequence**.

- **Sanguine Furnace = a `while_alive` `persistent(heal)` passive, and one is already written.**
  `godot/resources/data/abilities/furnace_trickle.json` is a complete, validated `while_alive` Persistent-Heal
  passive (`period_effect: heal amount 2`, `period_ticks 5`, `period_count 256`) — it was authored as a Story 2.6
  sample **but is attached to no unit** (grep: `beta_faction.json` has zero `abilities` arrays). Story 2.6 built the
  entire self-passive install path: `EntityWorld.OnUnitDefinitionApplied` (`EntityWorld.cs:403`) fires at the end of
  `ApplyUnitDefinition`, `SimulationHost.cs:108` subscribes `abilitySys.InstallSelfPassive(World, id)`, and
  `AbilityCastSystem.InstallSelfPassive` (`AbilityCastSystem.cs:147-156`) runs the passive's graph with the owner as
  caster+target. The passive pulses in `ModifierSystem` (system index [4]) strictly **before** `CombatSystem` ([5]) —
  AC2's ordering requirement is met by the existing `SimulationHost` order with **no wiring**.

- **The cast-feedback contract shipped in 2.7.** A committed cast emits `CombatEventType.AbilityCast` carrying the
  ability's `CombatFeedbackProfile` at the caster's position — `AbilityCastSystem.cs:217`:
  `_events?.Push(CombatEventType.AbilityCast, world.Position[target], ab.CombatFeedback);`. AC1's "the cast plays its
  CombatFeedbackProfile" is a field (`combat_feedback`) on the ability JSON, not new code.

- **The worker-cast + multi-resource (crystal) cost path shipped in 2.9b**, which also attached the *matter-cost*
  Equal-Exchange sibling `matter_infusion` (Self `apply_modifier`, ore + crystal cost) to the Acolyte. Story 2.9b
  explicitly **reserved the HP-cost variant for this story** (`2-9b-*.md:155`: *"'Mend Matter' (HP-self-cost,
  ally-heal) — that is the **Equal Exchange** mechanic (`DirectHpDelta` self-cost) Story 2.10 is scoped to build"*).

So the whole story is: **author 2–3 small JSON ability files, add ids to the right units' `abilities` arrays,
document the deferred Glut, add deserialize/validate teeth-tests, and verify in-engine.** No `.cs` sim file changes,
no new SoA field, no checksum fold.

_Covers: FR-8, FR-9, FR-10, FR-12a, AR-8, AR-9, AR-29, UX-DR51. Depends on: 2.5, 2.6, 2.7, 2.9a, 2.9b — all done._

## Factions on disk (naming reconciled)

The GDD/design carries a world-bible flavor label AND a shipped `display_name`; the epics carry pre-pivot codenames.
The shipped JSON is ground truth:

| epic / codename | world-bible label | shipped `id` | shipped `display_name` | signature mechanic (this story) |
|---|---|---|---|---|
| Crucible Covenant / Alpha | Rebel Alchemists (slate-blue) | `alpha` | The Crucible Covenant | **Equal Exchange** — flat self-HP **or** matter cost, never both |
| Sanguine Court / Iron Pact | Homunculus Legion (oxblood) | `beta` | The Sanguine Court | **Sanguine Furnace** — passive soul-fed HoT (pawns trickle, immortals pour) |

Design intent (`fma-faction-design.md`): Equal Exchange `:106` *"a flat, armor-independent HP debit (a direct
stat-delta leaf, not a matrix Damage leaf… a self-Damage leaf would route through DamageMatrix and silently scale the
'price' by the caster's armor) **or** an ore/crystal debit for machines."* Sanguine Furnace `:134` *"every Court unit
slowly regenerates HP while alive (**pawns trickle, immortals pour**). GLUT: when units die near the Court, nearby
allies gain a brief stacking accelerated-regen buff."* The Glut acceleration is the on-death half — deferred (see AC3).

## Acceptance Criteria

### AC1 — Equal Exchange (Covenant), epic AC verbatim + made testable

**Given** a Covenant unit with an Equal Exchange signature ability authored in the editor **When** the player casts
it **Then** the beneficial effect applies AND a flat armor-independent HP (or matter/crystal) self-cost is deducted
in the same Sequence, never both resources, and the cost does not scale with the caster's armor (`DirectHpDelta`, not
the matrix `Damage` leaf) **And** the cast plays its `CombatFeedbackProfile` and resolves identically across two
golden-checksum runs.

- **AC1.1** The ability is authored as JSON with `targeting: "Self"` and `effect` = a `sequence` whose children are
  `[ apply_modifier (a beneficial self-buff), direct_hp_delta (negative flat delta) ]`, using only the closed
  7-kind effect vocabulary. It loads through `AbilityRegistry.LoadFromDirectory` and passes the
  `Validated<AbilityDefinition>` gate (proven by a Tier-1 teeth-test asserting `.Ok` + the expected node shape).
- **AC1.2** The self-cost uses the `direct_hp_delta` leaf — flat, armor-independent, clamped to
  `[0, EffectiveMaxHealth]` (`DirectHpDeltaEffect.cs:26-34`) — and **never** the `damage` leaf (the only leaf that
  routes through `DamageResolver`/`DamageMatrix` and scales by `ArmorType`). Two casters of different `armor_type`
  pay the **identical** flat HP cost.
- **AC1.3 (never both resources)** The shipped Equal Exchange ability charges **exactly one** price: **either** a
  flat HP cost (a `direct_hp_delta` child, with `cost_ore` = `cost_crystal` = 0) **or** a matter cost
  (`cost_ore`/`cost_crystal`, with **no** `direct_hp_delta` child). This is an **authoring rule the dev enforces by
  construction** — the `AbilityValidator` does NOT reject an ability carrying both, so the story must not author both
  on one ability. (The matter-cost path is already demonstrated by 2.9b's `matter_infusion`; 2.10's new flagship
  demonstrates the vitality-price path.)
- **AC1.4** The ability carries a `combat_feedback` block (or relies on the tuned default); a committed cast emits
  `CombatEventType.AbilityCast` with that profile at the caster's position (`AbilityCastSystem.cs:217`). The cast is
  visible/audible in-engine (AC5).
- **AC1.5** The Equal Exchange effect resolves deterministically: two runs of an identical cast scenario produce
  byte-identical `SimChecksum` sequences. (The `direct_hp_delta` primitive is already covered by
  `EffectExecutorEqualExchangeTests`; the Sequence[buff, cost] shape is optionally golden-pinned — see AC4 / D-5.)

### AC2 — Sanguine Furnace passive HoT (Court), epic AC verbatim + made testable

**Given** Court units carrying the Sanguine Furnace passive (trickle for pawns, larger for elites) **When** they are
alive and below max HP over several ticks **Then** each regenerates HP per period via the `Persistent(Heal)` modifier
at its configured rate, capped at MaxHealth **And** the regen runs in `ModifierSystem` before `CombatSystem` and is
byte-identical across two runs.

- **AC2.1** The passive is authored with `activation: "while_alive"`, `targeting: "Self"`, and `effect` = a
  `persistent` node whose `period_effect` is a `heal`, with `period_ticks > 0` and `0 < period_count <= 256`. It is
  attached to Court units by adding its `id` to each unit's `abilities` array; it auto-installs at spawn via the
  `OnUnitDefinitionApplied` → `InstallSelfPassive` seam and lands in the unit's `SelfPassiveAbilityIndex` (never on
  the command card).
- **AC2.2** Rate tiers exist: **pawns trickle** (the baseline rate) and **elites/immortals pour** (a higher rate),
  authored as **distinct ability files** (the rate lives in the ability, not per-unit). At minimum the pawn tier and
  one elite/pour tier are attached to the appropriate Court units (attachment map in Task 2).
- **AC2.3** The `heal` clamps at `EffectiveMaxHealth` — no overheal (`HealEffect.cs:22-31`). A Court unit at full HP
  is unchanged by the passive.
- **AC2.4** The regen pulses in `ModifierSystem` (system index [4], `_store.Advance()` first) strictly **before**
  `CombatSystem` (index [5]) — satisfied by the existing `SimulationHost` order, pinned by `SystemOrderTest`; **no
  wiring change**.
- **AC2.5** The furnace regen resolves deterministically: two runs of an identical regen scenario produce
  byte-identical `SimChecksum` sequences. (Already golden-pinned by `passive-scenario` via the in-code
  `PassiveTestAbilities.FurnaceTrickle`; the deserialize/validate teeth-test proves the shipped JSON.)
- **AC2.6 (256-pulse cap disposition)** The `EffectCaps.MaxPersistentPeriods = 256` cap is respected **as-is**. A
  `while_alive` HoT pulses at most 256 times then stops; nothing re-installs it. The authored `period_ticks` is
  chosen so the 256-pulse window spans a meaningful match segment (see D-3). The lifelong-past-the-cap fix (cap
  re-arm / counter widening) is **owned by Story 2.13** and is **NOT attempted here** — do not touch
  `EffectCaps`/`ModifierStore`. Raising `period_count` above 256 does nothing (it is silently clamped at
  `ModifierStore.cs:208`); the only in-scope lever is `period_ticks`.

### AC3 — Glut deferral, no-regression, always-shippable (epic AC verbatim)

**Given** the Court faction definition in this story **When** the on-death 'Glut' accelerated-regen aura is reviewed
**Then** it is documented as deferred/enabled-by-Epic-7 and is NOT wired into the sim here, with no code dependency on
a later epic **And** the faction remains always-shippable: with abilities present it plays with its signature feel,
and nothing regresses the existing stat-sheet behavior.

- **AC3.1** No working Glut/on-death ability is authored. Glut is documented as **deferred, enabled-by-Epic-7 (the D2
  on-death trigger seam)** — in this story file and, optionally, as an inert note field on the Court faction data
  (the lenient faction loader ignores unknown keys — see D-4). There is **no `on_death` activation** in the closed
  set (`active | aura | on_hit | while_alive`), so Glut cannot be accidentally wired even if attempted.
- **AC3.2** No regression: no `.cs` sim file changes; the only units whose behavior changes are those that gain the
  new abilities; every other unit/ability/building is byte-for-byte unchanged. All **14** existing goldens are
  byte-identical.
- **AC3.3** Always-shippable: both faction JSONs (with the new content) load and validate through the
  `Validated<T>` gate; a live skirmish plays with the signature feel (a Covenant unit casts Equal Exchange; Court
  units visibly regenerate). No half-wired state that would make the faction unplayable.

### AC4 — Determinism & zero regression (explicit; prevents "completion lies")

**Given** the change set **When** the Tier-1 determinism gate runs **Then** `SimChecksum.AlgoVersion` stays **8**,
`CanonicalModelHash.AlgoVersion` stays **3**, the known-state pin `0x983D39AE` is unchanged, all **14** existing
goldens are byte-identical, and `VersionStampConsistencyTests` passes **8 / 3 / 1 / 2** (SimChecksum=8,
CanonicalModelHash=3, `TickCommandPacket.PROTOCOL_VERSION`=1, `ReplayRecorder.VERSION`=2). **No fold, no AlgoVersion
bump, no re-record of the existing 14 goldens.**

- **AC4.1** No new `EntityWorld` SoA field. Every array this content writes to is already folded: `Health` (original
  algo), `Energy` / `Effective*` / `ModifierStore` instances (v6), `EffectiveArmor` (v8), `Ore` / `Crystal` (v2).
  The passive-registration index arrays (`SelfPassiveAbilityIndex` etc.) are authored, **not folded** — per the
  checksum-fold timing rule, authoring content that writes to already-mutable folded arrays introduces no new fold.
- **AC4.2** New coverage lands as **deserialize/validate teeth-tests** (required) plus an **optional** new golden —
  never by editing the existing 14. No committed-baseline golden loads the real faction/ability JSON (verified in
  2.9b and re-verified this session), so attaching new abilities cannot move an existing golden.

### AC5 — In-engine verification

**Given** the shipped content **When** a live Play-mode skirmish runs **Then** `/godot-verify` confirms via
node-state reads: (a) a Covenant unit's Equal Exchange ability appears on its command card and, on cast, applies the
beneficial buff and drops the caster's `Health` by the authored flat cost while playing its feedback; (b) a Court
unit below max HP has its `Health` climb toward `EffectiveMaxHealth` over successive ticks (the furnace pulsing).
Node-state-driven per the 1.9b / 1.11 / 2.9a / 2.9b precedent — fragile physical click/pick gestures are parked as
manual-QA, since the mechanics are proven byte-for-byte by the sim tests.

## Decisions

**Baked in (deterministic-rule / minimal-slice / design-fidelity calls — applied, not re-asked):**

1. **Equal Exchange is authored `targeting: "Self"`, not ally-targeted.** The design's literal "Mend Matter =
   heal an *ally* at the caster's own HP" is **not cleanly D1-expressible today**: every effect leaf applies to
   `ctx.PrimaryTargetId`, a `TargetUnit` cast sets the primary target to the ally (`AbilityCastSystem.cs:184-190`),
   there is **no caster-retarget primitive** in the closed vocabulary, and the cast-click resolver is enemy-only
   (2.9b "Decision B", `SelectionSystem` `FindNearestEnemyUnit`). A `direct_hp_delta` inside a `TargetUnit` graph
   would drain the **ally's** HP, not the caster's. `Self` keeps the cost flat, armor-independent, and caster-paid,
   and needs **zero new UI** (Self/None resolves immediately on button press, like `matter_infusion`). The beneficial
   half is therefore a **self-buff** (`apply_modifier`), not an ally-heal.
2. **Flagship Equal Exchange home = the Covenant Transmuter (`infantry`), "Spike Transmutation."** Rationale: it is
   the first Melee (trainable at the Barracks), so Equal Exchange is **visible in a normal skirmish** without editor
   placement; a combat unit paying its own vitality for battle-power is thematically coherent; and it demonstrates
   the **vitality-price** path distinctly from the Acolyte's already-shipped **matter-price** `matter_infusion` — the
   roster then shows *both* Equal Exchange cost modes. (`fma-faction-design.md:111` assigns "Spike Transmutation —
   Equal Exchange self-HP" to the Transmuter.) See D-1 for the Acolyte "Mend Matter" alternative/addition.
3. **HP-cost XOR matter-cost, enforced by construction.** The flagship HP ability sets `cost_energy` = `cost_ore` =
   `cost_crystal` = 0 (HP is the sole price) and includes the `direct_hp_delta` child. A matter variant would drop
   the child and set the resource costs. The dev never authors both on one ability (the validator won't catch it —
   AC1.3).
4. **Sanguine Furnace attaches the existing `furnace_trickle` (pawns) + a new higher-rate `furnace_pour` (elites);
   the war machine is excluded.** `war_machine` (Render Crawler) gets **no** regen — design fidelity: it is a
   de-sinned machine, not a soul-fed homunculus (`fma-faction-design.md:141` *"machines are not homunculi"*).
   Auto-installs via the shipped seam; no code.
5. **The 256-pulse cap is NOT fixed here.** Story 2.13 owns the lifelong renewal (`epics.md:1161-1165`). This story's
   only lever is a data choice of `period_ticks` (D-3). Do not touch `EffectCaps.cs` / `ModifierStore.cs`.
6. **Glut is documented, not built** (AC3). No `on_death` activation exists, so it is structurally unbuildable.
7. **NO fold — `AlgoVersion` stays 8.** Content writes only already-folded arrays; the 14 existing goldens stay
   byte-identical; new coverage is deserialize/validate teeth-tests (+ optional golden).
   ([[chimera-checksum-fold-timing-rule]])

**Needs Alec's confirmation (recommended defaults baked in so the dev can start — all pure data, retune freely):**

- **D-1 (Equal Exchange placement & flavor).** Default = `spike_transmutation` on `infantry`/Covenant Transmuter
  (Self buff + flat HP cost). Optionally **also** author `mend_matter` on the `worker`/Acolyte (a Self buff + HP
  cost, complementing its `matter_infusion` — the Acolyte has room: `MAX_ABILITIES_PER_UNIT = 4`). Recommended
  numbers for the flagship: buff `apply_modifier` `attack_damage_delta: +15`, `duration_ticks: 120` (~4 s at 30 tps),
  `stacking: Refresh`, `max_stacks: 1`; cost `direct_hp_delta: -25`; `cooldown: 10`. Keep the HP cost **well below**
  the unit's HP (Transmuter hp 145) — a `direct_hp_delta` larger than current HP clamps the caster to 0 HP but does
  **not** kill it (`DirectHpDeltaEffect.cs:12-15`), which would look broken.
- **D-2 (Sanguine Furnace rates & per-unit tiers).** Default attachment: **trickle** (`furnace_trickle`) on the four
  pawns + `bulwark`; **pour** (`furnace_pour`, new, `heal ~6`) on `ironclad` (Pride Colossus) and `wyvern` (Envy
  Wraithwing); **none** on `war_machine`. Exact rates and which unit sits in which tier are Alec's balance call; the
  design specifies only tiering (trickle < mid < pour), not numbers.
- **D-3 (`period_ticks` vs the cap).** Default = raise the furnace `period_ticks` to **15** (256 × 15 = 3840 ticks ≈
  **128 s** regen window at 30 tps), keeping `period_count: 256`, and scale `amount` to hold the intended HP/sec.
  Rationale: `furnace_trickle`'s shipped `period_ticks: 5` gives only ~43 s of regen (and `amount 2 / 5 ticks` ≈ 12
  HP/s, which reads fast for a "trickle"). A longer window better matches AC2's "felt lifelong furnace" while the
  true lifelong fix lands in 2.13. Alternative: keep `period_ticks: 5` (~43 s) and rely wholly on 2.13.
- **D-4 (optional Story 5.2 descriptor).** Default = add a lightweight **inert** `signature_mechanic` note to each
  faction JSON (`alpha` → `"equal_exchange"` referencing the flagship ability id; `beta` → `"sanguine_furnace"`
  referencing the passive id). The faction loader is lenient (no `UnmappedMemberHandling.Disallow`,
  `FactionDefinition.cs:99-103`) so an unknown top-level key loads and is ignored — it gives Story 5.2 a head start
  and satisfies its "referencing a modifier/effect id" forward hint without breaking anything. Skippable if Alec
  prefers 5.2 own it.
- **D-5 (optional determinism golden).** Default = rely on the existing `worker-cast-crystal-cost` and
  `passive-scenario` goldens (both mechanics already deterministically pinned by their in-code equivalents) + the
  **required** deserialize/validate teeth-tests. **Recommended addition:** one small golden exercising the
  `Sequence[apply_modifier, direct_hp_delta]` Equal Exchange shape (no existing golden covers a Sequence with a flat
  HP self-cost) — cheap, reuses `AbilityCastScenario` + a new in-code fixture, adds real coverage. Mark optional.

## Tasks / Subtasks

- [x] **Task 1 — Content: author the Equal Exchange flagship ability + attach it to a Covenant combat unit** (AC: 1, D-1, D-3-independent)
  - [x] Create `godot/resources/data/abilities/spike_transmutation.json` (Self; a beneficial `apply_modifier`
    self-buff sequenced with a flat `direct_hp_delta` HP cost; HP is the sole price — `cost_ore`/`cost_crystal`/
    `cost_energy` all 0):
    ```json
    {
      "id": "spike_transmutation",
      "display_name": "Spike Transmutation",
      "targeting": "Self",
      "cost_energy": 0,
      "cost_ore": 0,
      "cost_crystal": 0,
      "cooldown": 10,
      "effect": {
        "kind": "sequence",
        "children": [
          {
            "kind": "apply_modifier",
            "modifier": {
              "id": 1100,
              "duration_ticks": 120,
              "stacking": "Refresh",
              "max_stacks": 1,
              "attack_damage_delta": 15,
              "move_speed_delta": 0,
              "status": "None"
            }
          },
          { "kind": "direct_hp_delta", "delta": -25 }
        ]
      },
      "combat_feedback": {
        "hit_flash": { "color_rgb": [0.10, 1.0, 0.35], "emission_mult": 6.0, "scale": 2.2, "duration_sec": 0.6 }
      }
    }
    ```
    - Modifier field set mirrors the shipped `battle_fury.json` (`id`, `duration_ticks`, `stacking`, `max_stacks`,
      `attack_damage_delta`, `move_speed_delta`, `status`). Use any unused modifier `id` — `1001`/`1002`/`2001` are
      taken by `battle_fury`/`matter_infusion`/`aura_guard`; `1100` is free. Add other `*_delta` fields only if the
      `Modifier` model supports them (reference `battle_fury.json` / the `ApplyModifierEffect` reader).
    - The sequence has 2 children (a 0-child sequence is a validator reject, `AbilityValidator.cs:201-202`).
  - [x] In `godot/resources/data/factions/alpha_faction.json`, add to the `"infantry"`/Covenant Transmuter unit block
    (after `vision_range`, matching the Acolyte's `abilities`/`max_energy` placement at `alpha_faction.json:29-30`):
    ```json
    "abilities": ["spike_transmutation"],
    "max_energy": 0
    ```
    (`max_energy: 0` is fine — the ability's `cost_energy` is 0, so energy is never gated. Use a nonzero
    `max_energy` only if D-1 adds an energy price.)
  - [x] (D-1 optional) If also authoring `mend_matter` on the Acolyte, create a second Self ability file
    (buff + `direct_hp_delta`) and append its id to `worker`'s existing `abilities: ["matter_infusion"]` →
    `["matter_infusion", "mend_matter"]`. Do **not** give one ability both HP and matter cost (AC1.3).

- [x] **Task 2 — Content: author + attach the Sanguine Furnace passive at tiers across the Court roster** (AC: 2, 3.3, D-2, D-3)
  - [x] Create `godot/resources/data/abilities/furnace_pour.json` (the elite/immortal rate), mirroring
    `furnace_trickle.json`'s shape with a higher `amount`:
    ```json
    {
      "id": "furnace_pour",
      "display_name": "Sanguine Furnace (Pour)",
      "targeting": "Self",
      "activation": "while_alive",
      "effect": {
        "kind": "persistent",
        "period_effect": { "kind": "heal", "amount": 6 },
        "period_ticks": 15,
        "period_count": 256
      }
    }
    ```
  - [x] (D-3) Optionally slow the pawn baseline: edit `furnace_trickle.json` `period_ticks` `5 → 15` (and, to hold
    the intended HP/s, retune `amount` — e.g. `amount 2 @ period 5` ≈ 12 HP/s → `amount 6 @ period 15` = same rate,
    coarser). This is a data-only edit that stays valid (round-trip test survives — see Regression risks). Keeping
    it as-is (`period 5`, ~43 s window) is also acceptable per AC2.6.
  - [x] In `godot/resources/data/factions/beta_faction.json`, add an `abilities` array (after `vision_range`) to each
    Court unit per the attachment map — **trickle** on pawns, **pour** on elites, **none** on the machine:

    | unit `id` | display name | category | hp | attach | ability id |
    |---|---|---|---|---|---|
    | `forgehand` | Cinderhand Thrall | Worker | 80 | trickle | `["furnace_trickle"]` |
    | `footsoldier` | Maul-Fused Wretch | Melee | 130 | trickle (also the Glut home — Task 3) | `["furnace_trickle"]` |
    | `bulwark` | Slag Bulwark | Melee | 240 | trickle (or a mid "flow" tier if authored) | `["furnace_trickle"]` |
    | `ironclad` | Pride Colossus | Melee | 340 | **pour** | `["furnace_pour"]` |
    | `crossbowman` | Bolt Penitent | Ranged | 120 | trickle | `["furnace_trickle"]` |
    | `rune_caster` | Cinder Cantor | Ranged | 110 | trickle | `["furnace_trickle"]` |
    | `war_machine` | Render Crawler | Siege | 480 | **none** (machine — design) | *(omit)* |
    | `wyvern` | Envy Wraithwing | Air | 300 | **pour** (Air unbuildable — data only) | `["furnace_pour"]` |

    Example block added to `forgehand` (mirror for each):
    ```json
    "vision_range": 7.0,
    "abilities": ["furnace_trickle"]
    ```
    - A `while_alive` passive needs **no** `max_energy` (it is never cast; the validator requires zero cost/cooldown
      on passives, `AbilityValidator.cs:82-92`).
    - `war_machine` intentionally gets no `abilities` field — leave it exactly as-is (Decision 4).

- [x] **Task 3 — Content: document the deferred Glut (+ optional 5.2 descriptor)** (AC: 3.1, D-4)
  - [x] Do **not** author any Glut / on-death ability. Record the deferral in this story (already noted) and, if
    desired, as an inert note on the Court faction data — e.g. a top-level `"deferred_mechanics": ["glut_on_death"]`
    key on `beta_faction.json` (the lenient loader ignores it). Reference: "Feed the Furnace / Glut on-death
    accelerated-regen — enabled-by-Epic-7's D2 on-death trigger seam (`epics.md:2187,2195,2201`); NOT wired here, no
    Epic-7 code dependency."
  - [x] (D-4 optional) Add an inert `signature_mechanic` descriptor to each faction JSON:
    `alpha` → `"signature_mechanic": "equal_exchange"` (and optionally reference the flagship ability id);
    `beta` → `"signature_mechanic": "sanguine_furnace"` (reference the passive id). Loads and is ignored today;
    Story 5.2 formalizes it. Skip if Alec prefers 5.2 own it.

- [x] **Task 4 — Tier-1 deserialize/validate teeth-tests (xUnit, Godot-free)** (AC: 1.1, 2.1, 2.3, 3.2, 4.2)
  - [x] The existing `AbilityDeserializeTests.ShippedSampleAbilityFiles_AllLoadAndValidate`
    (`godot/ProjectChimera.Sim.Tests/Definitions/AbilityDeserializeTests.cs:93`) already globs the abilities dir and
    asserts each file passes the `Validated<AbilityDefinition>` gate — so the new `spike_transmutation.json` /
    `furnace_pour.json` are **auto-covered** the moment they land. Run it and confirm green.
  - [x] Add **shape-asserting** teeth-tests (mirror `ValidAbility_Deserializes_WithExpectedScalarFields`
    `:45`): for `spike_transmutation`, assert `AbilityLoader.LoadFromFile(...)` is `.Ok`, the root effect is a
    `SequenceEffect` with 2 children, child[0] is `ApplyModifierEffect`, child[1] is `DirectHpDeltaEffect` with a
    **negative** `Delta`, and `CostOre == CostCrystal == Fixed.Zero` (proves the "HP-cost, not matter-cost" shape).
    For `furnace_pour`, assert `.Ok`, `ParsedActivation == while_alive`, the root is a `PersistentEffect` whose
    `period_effect` is a `HealEffect`, and `period_ticks > 0 && period_count > 0`.
  - [x] Add a `[InlineData("spike_transmutation.json")]` (+ `furnace_pour.json`) line to
    `Definitions/AbilityRoundTripTests.cs:25` (per-file validated round-trip), matching the existing
    `[InlineData("furnace_trickle.json")]` pattern.
  - [x] (Optional) A cross-armor teeth-test for AC1.2: run the `direct_hp_delta` leaf against two entities with
    different `ArmorType` and assert the HP delta is identical (armor-independent). `EffectExecutorEqualExchangeTests`
    already proves the primitive — extend only if you want the AC1.2 assertion explicit.

- [x] **Task 5 — Determinism gate (no fold; 14 goldens byte-identical)** (AC: 4, 1.5, 2.5)
  - [x] Run the full Tier-1 suite: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c
    Release`. Expected: current **574 pass / 1 skip / 0 fail** + the new teeth-tests, 0 fail.
  - [x] Confirm the fence unmoved: `KnownWorldState_ProducesPinnedV8Hash` green (pin `0x983D39AE`, `AlgoVersion == 8`);
    `VersionStampConsistencyTests` green (**8 / 3 / 1 / 2**); **all 14** existing goldens byte-identical (a moved
    golden means a leaked behavior/fold change — **fix it, don't re-baseline**).
  - [x] Release analyzer gate (should be a no-op — this story touches no `.cs` sim source, but run it per precedent):
    `dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj -c Release --no-restore
    --no-incremental -p:ChimeraRelease=true` → **0 errors**.
  - [x] Full Godot build: `dotnet build godot/godot.csproj` → **0 errors**.
  - [x] (D-5 optional) If adding the Equal Exchange Sequence golden: template it from
    `Golden/WorkerCastCrystalCostScenario.cs` + `WorkerCastCrystalCostGoldenTests.cs` (custom record loop:
    `ApplyScheduleStep` then `StepOnce` each iteration), using an in-code `apply_modifier`+`direct_hp_delta` Sequence
    ability; record via `CHIMERA_GOLDEN_RECORD=1`, register the new `.golden.txt` as `EmbeddedResource` in
    `ProjectChimera.Sim.Tests.csproj`, and update the `SimChecksumCoverageGuardTest.cs` doc-comment golden count
    (14 → 15). This is an **ADD**; the existing 14 are never re-recorded.

- [x] **Task 6 — In-engine verification (`/godot-verify`)** (AC: 5, 3.3)
  - [x] Boot a Play-mode skirmish. Select the Covenant unit carrying Equal Exchange (the match-start seed / a
    quick train) → confirm its ability button appears on the command card. Cast it → read node-state: the caster's
    `Health` drops by the authored flat cost, its buff modifier is applied (e.g. `EffectiveAttackDamage` up), and the
    `AbilityCast` feedback fires. Confirm the drop is **flat** (does not vary with the unit's armor).
  - [x] Place/seed a Court unit below max HP (e.g. after taking damage) → read node-state over several ticks:
    `Health` climbs toward `EffectiveMaxHealth` (the furnace pulsing in `ModifierSystem`), and stops at max (no
    overheal). Confirm a pawn (trickle) heals slower than an elite (pour).
  - [x] Confirm the machine (`war_machine`/Render Crawler) does **not** regenerate.
  - [x] Node-state-driven per 2.9a/2.9b precedent; a physical select→cast pick may be parked as manual-QA. Revert any
    temporary damage/seed scaffolding after capturing evidence — this story is content only; nothing extra should be
    left on disk.

## Dev Notes

### Current state — precise, source-verified

**Equal Exchange primitive already exists and is the intended tool.** `DirectHpDeltaEffect` (`src/Effects/
DirectHpDeltaEffect.cs`) — `kind: "direct_hp_delta"`, single field `"delta"` (a `Fixed`; **negative = cost, positive
= restore**). `Apply` (`:26-34`): `world.Health[t] = Fixed.Clamp(world.Health[t] + Delta, Fixed.Zero,
world.EffectiveMaxHealth[t]);` — flat, armor-independent, **never** through `DamageResolver`/`DamageTable`, clamped to
`[0, MaxHealth]`, and it does **not** fire the death sequence (a self-cost cannot kill; it floors at 0). Contrast
`DamageEffect` (`:33-43`), the only leaf that builds a `DamageContext` + calls `DamageResolver.Apply` (matrix + armor
+ death). **Equal Exchange must use `direct_hp_delta`, never `damage`** (AC1.2).

**The closed effect vocabulary (7 kinds)** — `EffectNodeJsonConverter.cs:41-47`, hardcoded discriminator registry,
fail-closed on any unknown `kind` or unknown property: `direct_hp_delta`, `heal`, `damage`, `apply_modifier`
(leaves); `sequence`, `search_area`, `persistent` (composition). JSON shapes verified against the converter:
- `{ "kind": "direct_hp_delta", "delta": -25 }` (reader `:197-201`, field is `delta`).
- `{ "kind": "heal", "amount": N }` (`:202-205`).
- `{ "kind": "sequence", "children": [ …, … ] }` (`:225-236`; children apply in authored order; ≤ 8 children;
  0-child = reject).
- `{ "kind": "persistent", "period_effect": {…}, "period_ticks": N, "period_count": M }` (`:250-261`; `initial_effect`
  / `expire_effect` also optional).
- `apply_modifier` carries a `modifier` sub-object (no `kind`) — see `battle_fury.json` for the field set.

**`targeting: "Self"` makes every leaf hit the caster.** `AbilityDefinition.cs:88-96` maps `"Self"` →
`AbilityTargeting.Self`; at cast, `AbilityCastSystem.cs:190-191` sets the primary target to the caster for Self/None,
so both the buff and the `direct_hp_delta` cost in the Sequence resolve against the caster. The `AbilityCast` feedback
event is emitted at the caster's `Position` (`:217`).

**The Sanguine Furnace runtime is entirely shipped (Story 2.6).** A `while_alive` passive's id in a unit's `abilities`
array is partitioned by `UnitDefinition.ResolveAbilities` (`:195-223`) into the single `SelfPassiveAbilityIndex`
(first one wins). `EntityWorld.ApplyUnitDefinition` copies it to `EntityWorld.SelfPassiveAbilityIndex[id]` (`:645`)
and fires `OnUnitDefinitionApplied?.Invoke(id)` (`:650`). `SimulationHost.cs:108` subscribes
`abilitySys.InstallSelfPassive(World, id)`, which (`AbilityCastSystem.cs:147-156`) runs the passive graph with the
owner as caster + primary target. `ModifierStore.InstallPersistent` (`:189-214`) arms the period timer;
`ModifierStore.Advance` (`:226-276`) pulses the `period_effect` (`HealEffect`, clamped at `EffectiveMaxHealth`,
`HealEffect.cs:22-31`) each period, decrementing `_periodsRemaining`. Reverted on death by `ModifierStore.ClearEntity`
via `OnDestroy`. **All of this exists — 2.10 only adds the ability id to the units' `abilities` arrays.**

**System order (`SimulationHost.cs:112-134`, pinned by `SystemOrderTest`):** `… [3] AbilityCastSystem [4]
ModifierSystem [5] CombatSystem [6] ProjectileSystem …`. `ModifierSystem.Tick` calls `_store.Advance()` first, so the
HoT pulses at [4] strictly before combat at [5] — AC2.4 is met by construction.

**Content auto-registration.** `AbilityRegistry.LoadFromDirectory` (`AbilityRegistry.cs:71-84`, called from
`MainScene.cs:262-264` and the server path `:1207-1209`) globs every `*.json` in
`godot/resources/data/abilities/`, validates each through `AbilityLoader.LoadFromFile`, keeps the `Ok` ones. **A new
ability JSON is picked up with no code change**; an invalid file is skipped (logged), never crashes. Faction JSONs
load via `FactionDefinition.LoadFromFile` (`MainScene.cs:246,251`).

**The rosters today (both read in full this session).** `alpha` (Crucible Covenant): `worker`/Acolyte
(`abilities:["matter_infusion"]`, `max_energy:20`), `infantry`/Covenant Transmuter, `scout`, `heavy_infantry`,
`archer`, `mage`/Circle Savant (`abilities:["fireball"]`, `max_energy:100`), `siege_engine`, `griffin`. `beta`
(Sanguine Court): `forgehand`, `footsoldier`, `bulwark`, `ironclad`, `crossbowman`, `rune_caster`, `war_machine`,
`wyvern` — **all eight with zero `abilities` arrays today** (pure stat sheets). The `beta` command center is itself
named "The Sanguine Furnace."

### Determinism notes (no fold, `AlgoVersion` stays 8, 14 goldens byte-identical)

- **No new SoA field.** The furnace HoT writes `Health` (folded, original algo) via `ModifierStore` instances (folded
  v6); Equal Exchange writes `Health` (`direct_hp_delta`), `Effective*` (the buff modifier, folded v6/v8), and — if a
  matter variant — `Ore`/`Crystal` (folded v2). The passive-registration index arrays are **authored, not folded**
  (`SimChecksum.cs:74` verbatim: the passive drivers "add NO new folded state"). Per the fold-timing rule, writing to
  already-mutable folded arrays introduces no new fold. **`AlgoVersion` stays 8.** ([[chimera-checksum-fold-timing-rule]])
- **Version stamps unchanged: 8 / 3 / 1 / 2** — `SimChecksum.AlgoVersion` 8, `CanonicalModelHash.AlgoVersion` 3
  (folds `StartCrystal` since the 2.9b follow-up), `TickCommandPacket.PROTOCOL_VERSION` 1, `ReplayRecorder.VERSION` 2.
  (Note the ordering: **Protocol = 1, Replay = 2** — do not transpose them.)
- **`CanonicalModelHash` (v3) is untouched** — this story adds no `ScenarioUnit`/start-state field; abilities resolve
  through `UnitDefinition`/`ApplyUnitDefinition`, not the canonical start-state hash.
- **The 14 existing goldens stay byte-identical (verify empirically).** No committed-baseline golden loads the real
  `alpha_faction.json` / `beta_faction.json` or the real ability JSON — every golden builds units in-code
  (`GoldenScenario.cs:52-61` says so explicitly; `AbilityCastScenario`/`PassiveScenario`/`WorkerCastCrystalCostScenario`
  use in-code `AbilityTestAbilities`/`PassiveTestAbilities` fixtures). The one test that loads the real faction JSON,
  `CanonicalScenarioTests.P2_4_…IsDeterministic`, is a two-run A==B agreement check with **no committed baseline** —
  it stays green as long as the sim stays deterministic. So attaching new abilities to faction JSON cannot move any
  golden.
- **Fixed / ascending-id everywhere:** this story writes no new code inside the tick loop. All content resolves
  through existing, already-hashed sim machinery.

### The 256-pulse cap — known, accepted, NOT this story's fix

`EffectCaps.MaxPersistentPeriods = 256` (`EffectCaps.cs:62`) clamps a `Persistent` HoT's lifetime
(`ModifierStore.cs:208`, also `:463`). A `while_alive` furnace installs once at spawn and is never re-installed, so it
pulses ≤ 256 times then stops (`furnace_trickle`'s `period_ticks 5` → ~43 s). This is a **pre-existing 2.2b cap**,
documented "don't fix," surfaced by 2.6 as the first lifelong-passive feature (`deferred-work.md:289`), and its true
fix (cap re-arm / counter widening + a 3×-cap-window soak) is **owned by Story 2.13** (`epics.md:1161-1165`, depends
on 2.10). **2.10 mitigation is data-only** (D-3: choose `period_ticks` so 256 pulses span a meaningful match; raising
`period_count` past 256 is silently clamped and does nothing). Do **not** modify `EffectCaps.cs` or `ModifierStore.cs`
here.

### The determinism fence (git status must NOT touch — a change here means a fold/behavior leak slipped in)

`godot/src/Core/SimChecksum.cs` (AlgoVersion 8) · `godot/src/Core/Definitions/CanonicalModelHash.cs` (AlgoVersion 3) ·
`godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` (`ExpectedV8Hash = 0x983D39AE`; the
golden-count doc-comment is the only allowed touch, and only if D-5 adds a golden) ·
`godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` (8/3/1/2 + "0.1") · and all **14** existing
`godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` files (`ability-cast`, `ability-domain-filter`, `ai-active`,
`anti-building`, `combat-air-ground`, `command-vocabulary`, `formation-separation`, `golden-applier`,
`golden-multifaction`, `golden-scenario`, `modifier`, `passive`, `same-tick-tie-break`, `worker-cast-crystal-cost`).
Touching any of the 14 is a red flag; adding one new golden (D-5) is the only expected golden change.

### Reuse — do NOT reinvent

- **`furnace_trickle.json`** already IS a valid Sanguine Furnace HoT — reuse it as the pawn baseline; do not rebuild
  it. The elite `furnace_pour.json` is a copy with a higher `amount`.
- **`battle_fury.json` / `matter_infusion.json`** are the Self-buff `apply_modifier` templates the Equal Exchange
  beneficial half mirrors.
- **`InstallSelfPassive` + the `OnUnitDefinitionApplied` seam + `ModifierStore.InstallPersistent`/`Advance`** — the
  whole passive runtime. Do not add any install/tick code; attach the ability id and the shipped seam does the rest.
- **`AbilityDeserializeTests.ShippedSampleAbilityFiles_AllLoadAndValidate`** auto-validates any new ability file — do
  not write a bespoke loader test; extend the shape-assert pattern (`ValidAbility_Deserializes_WithExpectedScalarFields`)
  and add `[InlineData]` to `AbilityRoundTripTests`.
- **`AbilityCastScenario.cs` / `PassiveScenario.cs` + their golden tests** — the templates for the optional D-5
  golden. Invent no new harness.

### Regression risks (must not break)

- **Never author both an HP cost and a matter cost on one ability** (AC1.3) — the validator will not catch it; the
  faction would silently double-charge for one action. HP variant → `direct_hp_delta` child + zero resource costs;
  matter variant → resource costs + no `direct_hp_delta`.
- **Keep the Equal Exchange HP cost below the unit's HP.** A `direct_hp_delta` larger than current HP clamps the
  caster to 0 HP but does not kill it (`DirectHpDeltaEffect.cs:12-15`) — a caster stuck alive at 0 HP looks broken.
- **A `while_alive` passive must carry zero `cost_*` and zero `cooldown`** (`AbilityValidator.cs:82-92`) and be
  `targeting: Self`/`None` — otherwise the `Validated<T>` gate rejects it and the unit silently loses the passive.
- **Do not exceed `period_count: 256`** — anything higher is silently clamped, so a "500-pulse furnace" is a lie in
  the data. Extend duration via `period_ticks`, not `period_count`.
- **`war_machine` (Render Crawler) must stay regen-free** (Decision 4) — do not add a furnace to it as a
  "completeness" reflex; it is a deliberate design exclusion (machines aren't soul-fed).
- **Editor undo/restore fidelity gap (known, do NOT fix here).** `EntityPlacer.RestoreUnit` (`EntityPlacer.cs:~1097-1124`)
  restores a fixed field list that excludes passive-registration indices and armor (`UnitSnapshot` doesn't carry
  them), so an editor-restored Court unit returns without its furnace passive. This is the standing
  `UnitSnapshot`-widening carve-off (documented for `Category`/`armor`/passives/`AttackDomainOf` in 1.13/2.6/2.9a) —
  editor-only fidelity loss, **not a lockstep/desync path**, out of scope. The furnace installs correctly on every
  normal `Create`→`ApplyUnitDefinition` spawn.
- **Self-passive install is once-per-spawn, not idempotent against a live re-`ApplyUnitDefinition`** — inert today
  (all spawns run `Create`→`ApplyUnitDefinition` once on a fresh slot). Do not introduce a re-apply path; if a future
  morph/upgrade re-applies a def in place, a dedup guard is needed (deferred, `deferred-work.md:291`). Not this
  story's concern.
- **Do not touch `EffectCaps.cs` / `ModifierStore.cs` / any `.cs` sim file** — a green git diff outside
  `resources/data/` (and the test project) means scope crept.

### Testing standards

- **Tier-1 (xUnit, Godot-free, `ProjectChimera.Sim.Tests`):** the deserialize/validate teeth-tests (Task 4) — the
  required proof that the new JSON loads through the `Validated<AbilityDefinition>` gate with the right node shape.
  Both mechanics' runtime determinism is already golden-pinned (`passive-scenario`, `worker-cast-crystal-cost`,
  `EffectExecutorEqualExchangeTests`); the optional D-5 golden adds the one uncovered shape (a Sequence with a flat
  HP self-cost).
- **Prove each teeth-test has teeth:** the `spike_transmutation` shape assert must fail if the cost were authored as
  a `damage` leaf or via `cost_ore`/`cost_crystal` (assert child[1] is `DirectHpDeltaEffect` **and** `CostOre ==
  CostCrystal == Zero`) — a test that only checks "loads Ok" would miss the exact "Equal Exchange must be
  armor-independent + single-price" contract.
- **In-engine (`/godot-verify`, Godot 4.6.3):** AC5 — node-state reads for the cast HP-drop + buff and the furnace
  regen climb. Content is not Godot-coupled `.cs`, but the *player-facing* behavior (command-card button, live
  regen) is only observable in-engine.
- **Determinism gate (Task 5):** the non-negotiable "no fold, 14 goldens byte-identical, 8/3/1/2" proof.

### Project Structure Notes

- **Data (the whole story):** `godot/resources/data/abilities/spike_transmutation.json` (new),
  `godot/resources/data/abilities/furnace_pour.json` (new), optionally `furnace_trickle.json` (retune) +
  `mend_matter.json` (D-1 optional); `godot/resources/data/factions/alpha_faction.json` (Transmuter gains
  `abilities`/`max_energy`), `godot/resources/data/factions/beta_faction.json` (Court units gain `abilities`;
  optional Glut/descriptor notes).
- **Tests:** `godot/ProjectChimera.Sim.Tests/Definitions/AbilityDeserializeTests.cs` (extend),
  `Definitions/AbilityRoundTripTests.cs` (add `[InlineData]`), optional `Golden/*` new scenario + golden + csproj
  `EmbeddedResource` (D-5).
- **No `.cs` sim or presentation source changes** — the runtime (2.1/2.2b/2.4a/2.6/2.7/2.9b) already supports both
  mechanics as content. All touched test dirs are covered by `SimSources.props` — no props edit.

### Project Context Rules (from `_bmad-output/project-context.md`)

- **One Effect-Graph is the only effect surface** — both mechanics compile to the closed, statically-validated
  `EffectNode` graph (no scripting escape hatch). Author within the 7 kinds; the `Validated<T>` gate must pass before
  any tick.
- **`Fixed` end-to-end / determinism** — the content's numeric fields deserialize via `FixedJsonConverter` at load
  (the one quantization boundary); no float/`Mathf`/`System.Random`/wall-clock in the sim; ascending-id iteration.
  This story adds no sim code, so it inherits this for free.
- **Everything is data-driven / composition over inheritance** — a "self-cost ability" = Sequence + `apply_modifier`
  + `direct_hp_delta`; a "regenerating unit" = a unit + a `while_alive` `persistent(heal)` ability. No unit subclass,
  no hardcoded balance number — all JSON a creator can edit.
- **Sim/Presentation boundary is sacred** — the ability/passive JSON and the sim runtime are sim; the command-card
  button + feedback flash are presentation reading sim arrays. This story touches neither `.cs` layer.
- **Godot C# gotchas / conventions** — the `.sln` is `godot.sln`; `#nullable enable`; but this story writes JSON +
  xUnit tests only.

### References

- Story spec + AC: `_bmad-output/planning-artifacts/epics.md#Story-2.10` (L1083-1101); Epic-2 objective L889-891
  ("the showcase factions' signature mechanics run with satisfying combat feedback"); downstream L1147-1165 (Story
  2.13 owns the 256-pulse lifelong-HoT fix, depends on 2.10) and L1759-1781 (Story 5.4 re-scoped to VERIFY the
  Epic-2 mechanics, does NOT re-implement).
- Requirements: **FR-8** active abilities (L75), **FR-9** passive abilities/auras/on-hit/modifiers (L76), **FR-10**
  compose from effect primitives (L77), **FR-12a** combat-feedback profile (L80), **AR-8** D1 effect-graph surface
  (L187), **AR-9** D1 Modifier subsystem (L188), **AR-29** CombatFeedbackProfile excluded from checksum (L216),
  **UX-DR51** combat feedback flashes/shake (L320).
- Design intent: `_bmad-output/fma-faction-design.md` (Equal Exchange `:103,:106,:111,:114`; Sanguine Furnace
  `:132,:134,:139-148`; roster tiers / machines-not-homunculi `:45,:141`); `Project_Chimera_GDD.md:47-54` (faction
  pillars + the alpha/beta display-name reconciliation).
- Determinism precedent: `[[chimera-checksum-fold-timing-rule]]`, `[[chimera-content-validator-bound-behavioral-params]]`,
  `[[chimera-dual-path-content-dto-constraint]]`.
- deferred-work: `_bmad-output/implementation-artifacts/deferred-work.md:289` (256-pulse furnace cap → 2.13, "fold
  into 2.10 planning"), `:291` (self-passive install-once, latent), `:293` (RestoreUnit drops passives — editor
  carve-off).
- Reuse templates: `2-9b-*.md` (the content-authoring precedent — matter_infusion, story-file format,
  golden-recording procedure, `/godot-verify` node-state precedent), `2-6-*.md` (the passive runtime this story
  authors content for), `2-7-*.md` (the cast→CombatFeedbackProfile contract).
- Source (verified this session): `EffectNodeJsonConverter.cs:41-47,197-261` · `DirectHpDeltaEffect.cs:1-36` ·
  `SequenceEffect.cs:1-17` · `HealEffect.cs:22-31` · `AbilityCastSystem.cs:147-156,184-217` ·
  `EntityWorld.cs:403,645,650` · `SimulationHost.cs:108,112-134` · `ModifierStore.cs:189-276,208,463` ·
  `EffectCaps.cs:62` · `AbilityValidator.cs:82-92,201-202,266-291` · `AbilityDefinition.cs:74-75,88-96,105-112` ·
  `UnitDefinition.cs:126-127,195-223` · `FactionDefinition.cs:99-103` · `AbilityRegistry.cs:71-84` ·
  `SimChecksum.cs:74,77,101,149,152-164,199-200` · `CanonicalModelHash.cs:34,56,62` ·
  `VersionStampConsistencyTests.cs:51-64` · `SimChecksumCoverageGuardTest.cs:109-120` ·
  `AbilityDeserializeTests.cs:45,93` · `AbilityRoundTripTests.cs:25` · `alpha_faction.json` (full) ·
  `beta_faction.json` (full) · `furnace_trickle.json` / `matter_infusion.json` / `battle_fury.json` /
  `minor_heal.json` / `onhit_searing.json` (full) · git `0853b5f` HEAD (tree clean).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Opus 4.8), via `gds-dev-story`. Zero new engine code — a content/data-authoring + test story, exactly the shape the spec predicted.

### Debug Log References

- **Determinism gate (Tier-1, Release):** `dotnet test godot/ProjectChimera.Sim.Tests -c Release` → **584 passed / 1 skipped / 0 failed** (was 574 + 10 new: 3 shape teeth-tests, 3 round-trip `InlineData`, 4 equal-exchange golden facts).
- **Fence unmoved:** `KnownWorldState_ProducesPinnedV8Hash` green (pin **0x983D39AE**, `AlgoVersion == 8`); `VersionStampConsistencyTests` green (**8 / 3 / 1 / 2**); `SystemOrderTest` green. All **14** pre-existing goldens byte-identical (none in the git diff); **1** new `equal-exchange-scenario.golden.txt` added (the 15th).
- **Release analyzer gate:** `dotnet build …ProjectChimera.Sim.Analysis -c Release --no-restore --no-incremental -p:ChimeraRelease=true` → **0 errors** (342 pre-existing advisories, none in touched files — a true no-op since no sim `.cs` changed).
- **Full Godot build:** `dotnet build godot/godot.csproj` → **0 errors**.
- **Golden record:** `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter FullyQualifiedName~EqualExchangeGolden` wrote `equal-exchange-scenario.golden.txt` (300 samples, algo v8, non-vacuous), then embedded via the csproj `EmbeddedResource` + rebuild.
- **Scope check:** `git diff` confined to `resources/data/**` + the test project (+ the workflow-tracked story/sprint files). **Zero `.cs` sim-source changes** — `EffectCaps.cs`/`ModifierStore.cs`/`SimChecksum.cs`/`CanonicalModelHash.cs` and the 14 golden `.txt` untouched (only the guard-test doc-comment 14→15).
- **/godot-verify (Godot 4.6.3, node-state-driven):** live `[PLAY]` skirmish reached **Tick 385, live Hash 0x32678F3F**, P1 3 / P2 2 units, **zero error/warning log messages** across boot + 385 ticks. The live Ability Editor "loaded snapshot" lists all three new abilities — `spike_transmutation`, `mend_matter`, `furnace_pour` — proving they passed the `Validated<AbilityDefinition>` gate **in the live engine** (the registry keeps only `.Ok` files). Covenant Transmuter recognized as placeable. Physical select→cast (Equal Exchange HP-drop) and damage→furnace-regen gestures parked as manual-QA per the 1.9b/1.11/2.9a/2.9b precedent — the mechanics are proven byte-for-byte by the sim tests.

### Completion Notes List

**All 5 ACs met; all 7 baked Decisions honored; the 4 Alec-confirmed decisions (D-1/D-3/D-4/D-5) all taken the thorough way.**

- **AC1 — Equal Exchange (Covenant):** Authored `spike_transmutation.json` on the Transmuter (`infantry`) = Self `Sequence[apply_modifier(+15 atk, 120-tick Refresh), direct_hp_delta(-25)]`, `cost_energy/ore/crystal` all 0 (HP is the sole price — AC1.3 by construction), `combat_feedback.hit_flash` present (AC1.4). **D-1 taken:** also authored `mend_matter.json` on the Acolyte (`worker`) — a Self buff (`armor_delta +5`) + `direct_hp_delta(-10)`, the HP-priced sibling of the ore/crystal `matter_infusion` (never both prices). Shape teeth-tests assert `SequenceEffect` → `[ApplyModifierEffect, DirectHpDeltaEffect(Δ<0)]` + `CostOre==CostCrystal==0` (proves armor-independent single-price, not a matrix `damage` leaf — AC1.2).
- **AC2 — Sanguine Furnace (Court):** New `furnace_pour.json` (`while_alive` `persistent(heal 6)`, `period_ticks 15`, `period_count 256`). **D-3 taken:** retuned the shipped `furnace_trickle.json` `period_ticks 5→15` (amount `2→3`) → both tiers share a ~128 s / 256-pulse window; pawns trickle (~6 HP/s), elites pour (~12 HP/s, clean 2×). Attached across the Court roster: `furnace_trickle` on the 4 pawns/casters + `bulwark`, `furnace_pour` on `ironclad` + `wyvern`; **`war_machine` deliberately excluded** (Decision 4 — machines aren't soul-fed homunculi). Auto-installs via the shipped `OnUnitDefinitionApplied` → `InstallSelfPassive` seam (no wiring); regen pulses in `ModifierSystem` [4] before `CombatSystem` [5] (AC2.4, unchanged order). Furnace-pour teeth-test asserts `WhileAlive` + `PersistentEffect(PeriodEffect=HealEffect, 0<PeriodCount<=256)`.
- **AC3 — Glut deferral / always-shippable:** No Glut/on-death ability authored (structurally unbuildable — no `on_death` activation exists). **D-4 taken:** inert `signature_mechanic` (`equal_exchange`/`sanguine_furnace`) + `deferred_mechanics: ["glut_on_death"]` descriptors added to the faction JSON (lenient loader ignores them — verified `FactionDefinition.JsonOptions` has no `Disallow`). No regression: no `.cs` sim change; all 14 goldens byte-identical.
- **AC4 — Determinism & zero regression:** NO fold — `AlgoVersion` stays 8, stamps 8/3/1/2, pin 0x983D39AE unchanged, 14 goldens byte-identical (+1 new). No new SoA field; content writes only already-folded arrays (`Health`, `Effective*`, `Ore`/`Crystal`, `ModifierStore` instances).
- **AC5 — In-engine:** live `[PLAY]` skirmish ticks cleanly with all new content loaded + validated (see Debug Log). Node-state-driven per precedent.
- **D-5 taken (optional golden):** added `EqualExchangeScenario` + `EqualExchangeGoldenTests` + `equal-exchange-scenario.golden.txt` (the 15th golden) — the first golden pinning a `Sequence[apply_modifier, direct_hp_delta]` (flat armor-independent HP self-cost). Reused the `AbilityCastScenario` template + a new in-code `AbilityTestAbilities.EqualExchange()` fixture; the existing 14 goldens were NOT re-recorded.
- **Task 4 optional cross-armor test:** the explicit AC1.2 cross-armor assertion was covered by the existing `EffectExecutorEqualExchangeTests` (proves the `direct_hp_delta` primitive is armor-independent) plus the new shape teeth-tests (assert child[1] is `DirectHpDeltaEffect`, not the matrix `damage` leaf) rather than a redundant new test — the story explicitly allows this ("extend only if you want the AC1.2 assertion explicit").

### File List

**New:**
- `godot/resources/data/abilities/spike_transmutation.json`
- `godot/resources/data/abilities/mend_matter.json`
- `godot/resources/data/abilities/furnace_pour.json`
- `godot/ProjectChimera.Sim.Tests/Golden/EqualExchangeScenario.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/EqualExchangeGoldenTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/equal-exchange-scenario.golden.txt`

**Modified:**
- `godot/resources/data/abilities/furnace_trickle.json` (D-3 retune: `period_ticks 5→15`, `amount 2→3`)
- `godot/resources/data/factions/alpha_faction.json` (Transmuter gains `abilities`/`max_energy`; Acolyte gains `mend_matter`; `signature_mechanic`)
- `godot/resources/data/factions/beta_faction.json` (6 Court units gain furnace `abilities`; `war_machine` excluded; `signature_mechanic` + `deferred_mechanics`)
- `godot/ProjectChimera.Sim.Tests/Definitions/AbilityDeserializeTests.cs` (3 shape teeth-tests)
- `godot/ProjectChimera.Sim.Tests/Definitions/AbilityRoundTripTests.cs` (3 `InlineData` round-trips)
- `godot/ProjectChimera.Sim.Tests/Effects/AbilityTestSupport.cs` (in-code `EqualExchange()` golden fixture)
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` (golden-count doc-comment 14→15)
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (embed the equal-exchange golden)

## Change Log

| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-07-02 | 0.1 | Story 2.10 created (`gds-create-story`, Opus 4.8): epic-capping content/authoring story (no new engine code) — author the Covenant's Equal Exchange self-cost ability (`Sequence[apply_modifier self-buff, direct_hp_delta flat HP cost]`, armor-independent, HP-XOR-matter) + attach the Court's Sanguine Furnace `while_alive` `persistent(heal)` passive (existing `furnace_trickle` for pawns + new `furnace_pour` for elites; machine excluded), document the deferred Glut (Epic-7 D2 seam). 3 parallel research agents (ability/passive runtime + effect leaves · faction/unit content + FMA design · determinism fence + goldens/tests) + full source grounding. NO fold — AlgoVersion stays 8, stamps 8/3/1/2, pin `0x983D39AE` + all 14 goldens byte-identical; required deserialize/validate teeth-tests + optional Equal-Exchange-Sequence golden. 256-pulse cap NOT fixed here (Story 2.13). Status → ready-for-dev. | Claude (gds-create-story) |
| 2026-07-02 | 0.2 | Story 2.10 DEV-DONE via `gds-dev-story` (Opus 4.8) → review. Content/authoring only, ZERO engine code. Authored `spike_transmutation` (Transmuter) + `mend_matter` (Acolyte) Equal Exchange self-cost abilities [`Sequence[apply_modifier, direct_hp_delta]`, HP-XOR-matter, armor-independent]; new `furnace_pour` + retuned `furnace_trickle` (`period_ticks 5→15`, ~128s/256-pulse window) attached across the Court roster (trickle×4 pawns+bulwark, pour on ironclad+wyvern; **war_machine excluded**); Glut documented-deferred (no `on_death` activation) + inert `signature_mechanic`/`deferred_mechanics` descriptors. All 4 Alec-confirmed decisions (D-1/D-3/D-4/D-5) taken. NO fold — AlgoVersion 8, stamps 8/3/1/2, pin `0x983D39AE` + all 14 goldens byte-identical + 1 new equal-exchange golden (15th). Tier-1 **584 pass / 1 skip / 0 fail** (+10), release analyzer 0 err, godot.csproj 0 err, `/godot-verify` live `[PLAY]` Tick 385 zero errors (all 3 new abilities loaded+validated in-engine). Zero `.cs` sim change (diff confined to `resources/data/` + test project). | Claude (gds-dev-story) |

## Review Findings

_gds-code-review **ultracode** — 2026-07-02 (Opus 4.8, fresh context; 19 agents / 6 blind parallel layers — Blind Hunter · Edge Case Hunter · Acceptance Auditor + 3 story-risk hardening finders: Determinism Fence · Content Contract · Test Teeth → dedup → per-finding **adversarial verification against live source**). **VERDICT: PASS.** All 5 ACs met; determinism fence intact (AlgoVersion 8, stamps 8/3/1/2, pin `0x983D39AE`, 14 goldens byte-identical + 1 new equal-exchange golden [LF-only, non-vacuous, spec-mandated in-code fixture]); zero sim `.cs` change (git diff confined to `resources/data/` + test project); shipped content correct on every axis (HP-XOR-matter both abilities, armor-independence via the `direct_hp_delta` leaf, `war_machine` excluded, `signature_mechanic`/`deferred_mechanics` load-and-ignore, modifier ids 1100/1101 unique, HP costs ≪ unit HP). 18 raw findings → 12 candidates → **3 CONFIRMED (all `low`, test-teeth/doc — none indicate a current defect; they harden guards against FUTURE regressions), 1 deferred, 8 dismissed** as verified false-positive / by-design._

**Patches (low — test-teeth / doc accuracy; the shipped content is already correct):**

- [ ] [Review][Patch] Deserialize teeth-tests assert node-shape + cost-sign but not the value invariants — add `Assert.Equal(Fixed.Zero, def.CostEnergy)` on both abilities (a `cost_energy` leak on `mend_matter`, whose Acolyte carries `max_energy: 20`, would violate the "HP is the sole price" contract and ship green) and a buff-beneficialness assert (`spike` `AttackDamageDelta > 0` / `mend` `ArmorDelta > 0`; a 0/negative delta currently passes the comment-claimed "beneficial self-buff"). Exact magnitude-pinning (−25/+15) intentionally skipped as brittle balance values. [godot/ProjectChimera.Sim.Tests/Definitions/AbilityDeserializeTests.cs:124-155]
- [ ] [Review][Patch] `FurnacePour_IsWhileAlivePersistentHeal` pins the persistent-heal shape but not the pour>trickle rate that is AC2.2's whole justification — assert `((HealEffect)persistent.PeriodEffect).Amount == Fixed.FromInt(6)`, load both furnace files and assert `pour.Amount > trickle.Amount` (6 vs 3), and `InitialEffect is null && ExpireEffect is null`; drop or substantiate the AC2.1 spawn-seam claim in the comment (this LoadFromFile test never spawns a unit). [godot/ProjectChimera.Sim.Tests/Definitions/AbilityDeserializeTests.cs:159-174]
- [ ] [Review][Patch] `PassiveTestAbilities` docstring "The values match the JSONs" is now FALSE for `furnace_trickle` (retuned to 3/15; the in-code fixture is correctly FROZEN at 2/5 to keep the Story 2.6 passive golden byte-identical) — update the class/method comment to record the intentional decoupling and warn NOT to re-sync (re-syncing silently moves the passive golden → surprise re-baseline). Do NOT change the fixture values. Optionally add a one-line assert on `furnace_trickle.json`'s amount/period to restore the lost rate cross-check. [godot/ProjectChimera.Sim.Tests/Effects/PassiveTestAbilities.cs:12-13,45-50]

**Deferred (real property, out of this content story's zero-`.cs` scope → Story 2.13 / balance; full note in `deferred-work.md`):**

- [x] [Review][Defer] Repeated `Spike Transmutation` self-cast can strand the Transmuter alive-but-stuck at 0 HP — cast pipeline gates only cooldown/energy/ore/crystal, no HP floor; ~6 benefit-less casts (hp 145 / −25, zero incoming damage/heal) reach 0-alive. Catastrophic paths guarded (clamp, no self-kill, deterministic, non-desync); needs pathological self-harm to hit; fix is a `.cs` min-HP gate. [godot/src/Effects/AbilityCastSystem.cs:171-180] — deferred, pre-existing (2.1 primitive property surfaced by 2.10)

**Dismissed (8, verified false-positive / by-design / handled-elsewhere):** golden pins the spec-mandated in-code fixture not the JSON (D-5; JSON shape is guarded by the deserialize teeth-test); `Sequence_Evolves_NotVacuous` is a smoke test — `MatchesCommittedGolden` is the real teeth (folds Health/EffectiveAttack/ModifierStore); armor-independence proven behaviorally by `EffectExecutorEqualExchangeTests`; `signature_mechanic`/`deferred_mechanics` load-and-ignore via the lenient loader (no `Disallow`, exercised by `CanonicalScenarioTests`) with no consumer/collision; `Fixed.FromRaw(-1)` Self sentinel is read back correctly as a packed raw int (mirrors production `SelectionSystem`); asymmetric modifier field authoring matches the pre-2.10 minimal-style norm (all deltas optional, default Zero); "15 goldens" vs csproj "14 above" count two correct sets (total vs pre-existing delta); the two `combat_feedback` hit_flash blocks drive distinct events (AbilityCast 2.2/0.6 = the ability-cast-flash convention vs on-hit 2.5/0.7) and never contend.
