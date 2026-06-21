---
title: FMA Faction Redesign — World Bible + Faction Design
generated: 2026-06-21
method: 9-agent design+adversarial-critique workflow (wy9vxg6jv)
status: design complete — asset manifest derived; engine epics feed GDS planning
---

> Decided 2026-06-21: 2 factions, FULL redesign (rosters+abilities), low-poly + FMA palette.
> Role skeleton (8 units + 4 buildings + role ids) kept → zero structural code change.
> Themed asset prompts: `_bmad-output/asset-generation-manifest.md`.

# THE LAW OF EQUAL EXCHANGE
### World Bible — Project Chimera RTS Showcase
*Tone: the spirit of Fullmetal Alchemist. Original universe. No trademarked nouns.*

## 1. One-Line Pitch
In a war-scarred industrial age, **alchemists who heal the world at a personal price** fight **immortal sin-born horrors that feed on the dead** for control of the **Vermillion Core** — a crimson stone that grants miracles and is forged from murdered souls.

## 2. The Alchemy System & Its Law
**Alchemy** is the science of *Reading, Breaking, and Rebuilding* matter — craft, not magic; a chemist with a god's reach, not a wizard.

**The Iron Law — Equal Exchange:** *Nothing is created. To gain, an equal price must be paid.* Raise a wall, spend the earth. Mend a wound, spend vitality. The ledger always balances. Crucially, you pay **either matter OR vitality for a given exchange — never both** (this resolves the earlier "double-pay" incoherence).

**Transmutation Marks:** sigils drawn in chalk, etched into gauntlets, or tattooed for instant casting. A faster mark means a sloppier exchange.

**The Forbidden Threshold — Soul-Debt:** the one price the Law will not let you pay honestly is a human life. Soul transmutation never balances; the Law collects the difference from the alchemist's own body. The most loving act is also the most monstrous.

> RTS hook: Equal Exchange is the Rebel resource fantasy — burst power paid up front, race the clock before the bill comes due.

## 3. The Philosopher's-Stone Analog — *The Vermillion Core*
The **Vermillion Core** (a.k.a. *Sin-Glass*) is a crystallized red catalyst that lets an alchemist **break Equal Exchange** — gain without paying. It is the only way to perform impossible alchemy (heal a dying land, raise the dead). It is forbidden because a Core is not made; it is *rendered* — the compressed, still-screaming essence of many murdered souls. A miracle in your palm, paid for by a massacre.

> RTS hook: Cores are the contested map objective. Rebels want to **destroy/neutralize** them; the Court wants to **manufacture** them. Today this is seeded, not yet a full mechanic (see Open Decisions): the Court command center is explicitly the soul-rendering forge in lore, and `cost_crystal` reads as "Core-shard fuel" flavor on elite units — a hook for a future Core economy.

## 4. The Chimera
**Chimera** are living beings alchemically fused. The art was meant for medicine and labor; in practice it produced suffering.
- **Rebel chimera** are *volunteers and rescues* — partnerships and mounts that keep a flicker of personhood. Tragic but loyal.
- **Court chimera** are *manufactured pawns* — mass-fused thralls, stitched and stone-fed, expendable horrors.

> Roster note (theme fix): the Court's disposable-ground-chimera fantasy is carried by re-flavoring the cheap melee pawn (Ash Conscript) as a fused thrall-beast, so the swarm body IS the disposable chimera. The Envy Wraithwing is the Court's elite flying chimera. The 8-slot engine cap means additional chimera variants are reserved for a future expanded roster.

## 5. The Homunculi & the Stone Tie
**Homunculi** are artificial immortals, each embodying one human flaw (the **Seven Sins** as flavor). They barely bleed and **regenerate while their internal Core has souls to spend** — kill the body, the Core resurrects it; to truly end one you must exhaust or shatter the Core. Every homunculus is built around a Vermillion Core: a furnace of stolen souls in the chest. They wage war to harvest more souls and forge more Cores.

**The Seven-Sin pantheon (canon rule):** the sins name **named, elite, immortal individuals** — a true pantheon. In the buildable 8-unit roster only the genuinely elite/immortal units carry a sin name (**Pride** on the anchor immortal; **Envy** on the stitched-face flying chimera). The remaining sins — **Wrath, Greed, Lust, Sloth, Gluttony** — are reserved for the hero/boss roster beyond the production units, so the player feels a larger pantheon exists. Rank-and-file pawns and machines deliberately do **not** take sin names.

**Their Maker — *The Progenitor* (*The First Sin*):** a being from beneath the world that taught humanity alchemy as a long con. Hidden antagonist; the lore reason the Court exists.

## 6. The Two Factions
### REBEL ALCHEMISTS — *The Crucible Covenant* (id `alpha`, slate-blue)
State-trained alchemists, deserters, and grieving survivors who learned what the Cores are and refused the price. Agile, versatile, heroic, fragile. They reshape the battlefield and heal at personal cost (Equal Exchange), and must end fights fast and clean.
- Hero archetypes (spirit of the Elric brothers / Scar / their father): *the Bonded Pair*, *the Markbreaker*, *the Lawgiver*.

### HOMUNCULUS LEGION — *The Sanguine Court* (id `beta`, oxblood crimson)
The court of the Progenitor: sin-born immortals commanding stone-fed human pawns. Tanky, regenerating, grinding. They never need to win fast — only to make you keep dying, because every death feeds the furnace.

**The clash (the showcase asymmetry):** a blitz-and-reshape faction racing the clock against an unkillable tide that profits from the body count. Rebels end fights *fast and clean*; the Court wins by making them *long and bloody*.

## 7. Tone & Aesthetic
Early-industrial: coal smoke, rivets, brass gauges, telegraph wires beside chalk transmutation circles. **Rebels:** worn slate-blue greatcoats, leather, brass automail (used as a *characterful* motif, not blanketed — varied with chalk gauntlets, etched bracers, tattooed marks), bright chalk-white sigils that flare cyan-white when paid. **Court:** oxblood crimson, black iron, brass, the wet red glow of Cores, gothic/uncanny homunculi. Mood: earnest humanity against patient cosmic horror; magic with a price you can see.

## 8. Trademark-Safe Glossary
| Term | Role |
|---|---|
| Vermillion Core (*Sin-Glass*) | philosopher's-stone analog — soul-forged red catalyst |
| Equal Exchange | the Iron Law (Rebel cost mechanic) |
| The Crucible Covenant | Rebel Alchemist faction (`alpha`) |
| The Sanguine Court | Homunculus Legion faction (`beta`) |
| The Progenitor (*The First Sin*) | maker of the homunculi; hidden antagonist |
| Stonewrought | the Court's Core-fed human pawn class |
| Transmutation Marks / Markbreaker | sigil casting / the Rebel hero who unmakes alchemy |

All names avoid trademarked proper nouns. Generic mythic terms (alchemy, transmutation, philosopher's stone, chimera, homunculus, the seven sins) are flavor only.

## 9. Showcase Mapping
| Element | Covenant (Rebels) | Court (Legion) |
|---|---|---|
| Fantasy | Heal & reshape at a price | Endure & feed on death |
| Pace | Fast, mobile, burst | Slow, grinding, relentless |
| Durability | Fragile, evasive | Tanky, regenerating |
| Economy twist | Spend vitality/matter | Harvest souls → forge Cores |
| Win feel | End it before the bill comes | Make it last; let them bleed |
| Heroes | Bonded Pair, Markbreaker, Lawgiver | Seven Sin-homunculi + the Progenitor |
| Objective | Destroy/neutralize Cores | Capture/manufacture Cores |

JSON wiring: `alpha.display_name` → **"The Crucible Covenant"**; `beta.display_name` → **"The Sanguine Court"** (replacing "Alpha Faction"/"Iron Pact" on disk). The `beta` color `[0.8,0.25,0.1,1.0]` (oxblood) already fits; `alpha` `[0.2,0.5,1.0,1.0]` reads as slate-blue.

---

# FACTION DESIGN — Crucible Covenant vs Sanguine Court

> **CRITICAL ENGINE REALITY (applied throughout).** Production routes by *category*, training only the **first** unit of each category in faction order. So per faction exactly **one Melee** and **one Ranged** unit are reachable through the command card; Air has no producer at all. Every roster below is therefore split into **BUILDABLE TODAY** vs **DESIGNED — NOT YET BUILDABLE** (needs the per-unit production-selection UI epic, or, for Air, the Air-building epic). The `units` array order is specified so the *intended baseline* unit sits first in its category. Additionally, **no signature ability or faction mechanic runs in CombatSystem today** — both armies currently resolve as pure stat sheets — so the interim balance below is tuned so the asymmetry reads on stats that DO run (Rebel DPS/range/speed/cost vs Court HP/armor), with regen/burst layered in once D1/D2 ships.

---

## THE CRUCIBLE COVENANT (`alpha`) — Rebel Alchemists

**Identity.** A scattered brotherhood of alchemists who refused the Cores' price. Fast, fragile, versatile, burst-oriented; they win the opening exchange and reposition out of bad trades.

**Asymmetry pillars.**
1. **AGILE (corrected to read everywhere).** The Covenant is strictly faster than the Court *at every paired archetype* — not just at the extremes. Rule held below: each Rebel unit's speed > its Court counterpart's by ~15-25%.
2. **VERSATILE.** Battlefield transmutation solves many problems from one army (roots, slows, repairs, ground hazards) — adapts rather than hard-counters.
3. **HEROIC SACRIFICE (Equal Exchange).** Signature actions cost the caster's own vitality (HP) **or** army matter — never both. High ceiling, thin margin.
4. **BURST OVER ATTRITION.** Frontloaded damage to delete key targets before regeneration matters — because they cannot win a long grind.

**Unique mechanic — Equal Exchange (Vitality Ledger).** Every signature ability deducts a real cost the instant it fires: a *flat, armor-independent* HP debit (a direct **stat-delta** leaf, **not** a matrix `Damage` leaf — this fixes the critique that a self-`Damage` leaf would route through DamageMatrix and silently scale the "price" by the caster's armor) **or** an ore/crystal debit for machines. Engine fit: **D1/D2** (self stat-delta + beneficial effect as one EffectNode `Sequence`; ground-hazard/spawn cases use `FireProjectile`+`Persistent`). One terrain-transmutation case is flagged needs-code.

### Covenant units — BUILDABLE TODAY
| role id | themed name | archetype | stats (hp / armor / spd / dmg-type / range / atk-spd / splash / supply / ore+crys) | signature ability | engineFit |
|---|---|---|---|---|---|
| **infantry** *(listed FIRST in Melee → the trainable Barracks unit)* | **Covenant Transmuter** | Melee (Barracks) | 145 / Medium / **4.5** / Normal / 1.5 / 0.95 / 0 / 1 / 100+0 | **Spike Transmutation** — targeted Pierce burst + short Root; flat self-HP price | D1/D2 |
| **archer** *(listed FIRST in Ranged → the trainable Archery Range unit)* | **Pierce Marksman** | Ranged (Archery Range) | 85 / Light / **4.0** / Pierce / 6.5 / 0.85 / 0 / 1 / 120+0 | **Transmuted Slug** — *immediately-cast* FireProjectile Pierce nuke; **anti-Light / anti-air finisher** (corrected: Pierce vs Fortified=0.25x, so this is explicitly NOT the anti-tank tool) | D1/D2 |
| **siege_engine** | **Crucible Mortar** | Siege (Siege Workshop) | 330 / Heavy / **2.2** / Siege / 10.0 / 3.8 / 3.0 / 3 / 250+75 | **Molten Payload** — splash Siege + lingering burning-ground DoT; **the Covenant's true anti-Fortified answer** (Siege vs Fortified=1.5x) | D1/D2 |
| **worker** | **Acolyte** | Worker (Command Center) | 55 / Unarmored / 4.0 / Normal / 1.5 / 1.5 / 0 / 1 / 50+0 | **Mend Matter** — repairs an ally; pays a **single** price = the Acolyte's own vitality (no double-pay) | D1/D2 + worker-cast trigger surface (needs-code) |

### Covenant units — DESIGNED, NOT YET BUILDABLE
| role id | themed name | archetype | stats | signature ability | why unbuildable / engineFit |
|---|---|---|---|---|---|
| **scout** | **Quicksilver Runner** | Melee (Barracks) | 70 / Light / **6.5** / Normal / 1.5 / 1.3 / 0 / 1 / 75+0 — **dmg dropped to 7; best vision 12** | **Quicksilver Mark** — self speed burst (disengage/recon); tiny self-HP price | Category-collapse: only first Melee trains. **Re-roled to pure recon/harass** so it doesn't shadow the Transmuter. Needs per-unit production UI. D1/D2 |
| **heavy_infantry** | **Bulwark Adept** | Melee (Barracks) | 270 / Heavy / **3.0** / Normal / 1.5 / 1.4 / 0 / 2 / 175+**0**(crystal stripped) | **Iron Aegis** — short Invuln/heavy-DR; steep flat self-HP price | Per-unit production UI. D1/D2 |
| **mage** | **Circle Savant** | Ranged (Archery Range) | 65 / Unarmored / 3.5 / **Magic** / 7.0 / 1.9 / 1.5 / 2 / 140+**0** | **Detonating Circle** — SearchArea Magic AoE + slow; heavy self-HP price; **Magic is a real anti-Fortified answer** (1.0x vs Heavy, 0.5x vs Fortified) | Per-unit production UI. D1/D2 |
| **griffin** | **Greycrest, the Bonded** | Air (no producer) | 190 / Light / **6.5** / Pierce / 2.0 / 1.1 / 0 / 2 / 200+**0** / vision 15 | **Diving Rend** — dash + heavy Pierce strike + self speed buff | **No Air production building exists** (pre-existing gap). Plus true fly/anti-air targeting is needs-code. needs-code |

**Cross-faction counter narrative (corrected per DamageMatrix):** Covenant answers to the Court's Fortified anchor are the **Crucible Mortar (Siege, 1.5x)** and **Circle Savant (Magic, 0.5x but armor-agnostic elsewhere)** — *not* the Pierce Marksman (0.25x vs Fortified). The Marksman is repositioned as anti-Light/anti-air burst.

---

## THE SANGUINE COURT (`beta`) — Homunculus Legion

**Identity.** The army of the deathless: stone-fed pawns and sin-born immortals around furnaces of stolen souls. +1 armor tier, +20-35% HP, ~15-25% slower than the Covenant. Wins by attrition; profits from the body count.

**Asymmetry pillars.** Deathless durability • Soul-fed regeneration (passive HoT + death-Glut) • Attrition over tempo • Expendable pawns vs immortal elites.

**Unique mechanic — The Sanguine Furnace (Soul-Glut Regeneration).** PASSIVE: every Court unit slowly regenerates HP while alive (pawns trickle, immortals pour). GLUT: when units die near the Court, nearby allies gain a brief stacking accelerated-regen buff — attrition literally heals the survivors. Counter is **burst**: damage exceeding regen+glut kills cleanly; chip just feeds the furnace. **Engine fit: D1/D2 — and the single heaviest deferred dependency in the whole design.** It needs the D1 Modifier system + `Persistent`(periodEffect=Heal) + D2 on-death trigger + SearchArea + ApplyModifier(Refresh-stack). The Court **cannot be meaningfully prototyped as a faction until those ship**; until then it is a pure stat sheet (see interim balance).

### Court units — BUILDABLE TODAY
| role id | themed name | archetype | stats (hp / armor / spd / dmg-type / range / atk-spd / splash / supply / ore+crys) | signature ability | engineFit |
|---|---|---|---|---|---|
| **footsoldier** *(FIRST in Melee → trainable Barracks unit)* | **Maul-Fused Wretch** | Melee (Barracks) | 130 / Medium / **3.4** *(lowered below Rebel Transmuter 4.5 to honor "Court has no real speed")* / Normal / 1.5 / 1.2 / 0 / 1 / 70+0 | **Feed the Furnace** — on death, nearby allies gain accelerated-regen (the Glut). **Re-flavored as a disposable fused thrall-beast** (the Court's ground chimera) | D1/D2 (on-death trigger + Modifier — heavy dependency) |
| **crossbowman** *(FIRST in Ranged → trainable Archery Range unit)* | **Bolt Penitent** | Ranged (Archery Range) | 120 / Medium / **2.8** / Pierce / 5.5 / 1.3 / 0 / 1 / 120+0 | **Bolt Penance** — **stat profile is data-only; the self-mending half is the deferred faction passive (D1/D2)** (critique fix: was mistagged pure data-only) | data-only (stats) + D1/D2 (regen) |
| **war_machine** | **Render Crawler** *(de-sinned — machines are not homunculi)* | Siege (Siege Workshop) | 480 / Heavy / **1.5** / Siege / 9.0 / 4.5 / **4.0** / 3 / 250+75 | **Sin-Glass Barrage** — wide Siege splash vs clustered Rebels | data-only (splash); optional D1/D2 ground-fire patch |
| **forgehand** | **Cinderhand Thrall** | Worker (Command Center) | 80 / Light / 3.0 / Normal / 1.5 / 1.5 / 0 / 1 / 50+0 | **Furnace Trickle** — smallest passive regen rate (the faction passive baseline) | D1/D2 (passive regen) |

### Court units — DESIGNED, NOT YET BUILDABLE
| role id | themed name | archetype | stats | signature ability | why unbuildable / engineFit |
|---|---|---|---|---|---|
| **bulwark** | **Slag Bulwark** | Melee (Barracks) | 240 / Heavy / **2.8** / Normal / 1.5 / 1.0 / 0 / **2** *(raised from 1 → tank class parity; fixes 1-supply over-efficiency)* / 100+0 | **Cooling Slag** — **data-only stat tank** (Heavy armor) **+ D1/D2 regen** (split-tagged per critique); one bold glowing crimson seam | Per-unit production UI. data-only (stats)+D1/D2 (regen) |
| **ironclad** | **Pride Colossus** | Melee (Barracks) | **340** / **Heavy** *(dropped from Fortified — Fortified reserved for buildings; fixes the broken eHP/cost)* / **2.0** / Normal / 1.5 / 1.5 / 0 / **3** *(up from 2)* / **250**+25 *(recost)* | **Deathless Pride** — highest rank-and-file passive regen + heavy armor; the immortal that out-heals chip | Per-unit production UI. D1/D2 (regen) |
| **rune_caster** | **Cinder Cantor** *(de-sinned — Wrath reserved for the hero pantheon)* | Ranged (Archery Range) | 110 / Light / **2.5** / Magic / 7.0 / 2.5 / 1.5 / 2 / 140+50 | **Searing Sigil** — design intent is a DoT burn (D1 `Persistent` via FireProjectile-carried ApplyModifier); data-only approximation today = Magic + 1.5 splash | D1/D2 |
| **wyvern** | **Envy Wraithwing** *(KEEP Envy — stitched stolen faces fit perfectly)* | Air (no producer) | 300 / Medium / **4.8** / Pierce / 2.0 (instant melee, under 2.5) / 1.2 / 0 / 2 / 200+100 | **Carrion Dive** — heavy durable dive (instant melee); unit profile is data-only, true anti-air targeting is needs-code | **No Air producer.** `productionBuilding` = "(none — Air unbuildable)" (critique fix: was wrongly "barracks"). data-only (profile) + needs-code (anti-air) |

**Interim balance ledger (what runs TODAY, all abilities off).**
- **Ironclad/Pride Colossus** no longer dominates: Heavy (not Fortified) at 340 HP / 3 supply / 250 ore brings eHP-vs-Normal to 680 at a real cost, in line with the Rebel Bulwark Adept tier — they trade within ~25% TTK instead of 21s-vs-56s.
- **Slag Bulwark** at **2 supply** (was 1) closes the 520-eHP-for-1-supply flood; eHP/supply now sits in the tank-tier band.
- **Speed rule enforced on the core line:** Maul-Fused Wretch 3.4 < Covenant Transmuter 4.5, and every Court unit is now strictly slower than its Rebel counterpart.
- **Quicksilver Runner** dropped to 7 damage / re-roled recon so it no longer sits on the same Normal-melee DPS curve as the Transmuter.
- **cost_crystal** treated as cosmetic everywhere (TrainUnit never spends it); stripped to 0 on Rebel units where it implied real gating (heavy_infantry, mage, griffin), kept as "Core-shard fuel" flavor on Court elites with an explicit note. Both factions now annotate it consistently.
- **Anti-structure claims removed**: current projectile/splash code damages only EntityWorld units, not BuildingStore — so "flatten fortifications" language is dropped from both sieges pending a needs-code anti-building combat epic.

---

## Needs-new-code epics (for GDS epics/stories planning)

| Title | Scope | Why |
|---|---|---|
| **Per-unit production selection UI (command-card tier/sub-menu)** | medium | The single biggest gap: the Barracks/Archery Range each train only the FIRST category-matching unit, so 3 of 4 Melee and 1 of 2 Ranged units per faction are dead data. Without this, Bulwark Adept, Quicksilver Runner, Circle Savant (Rebels) and Slag Bulwark, Pride Colossus, Cinder Cantor (Court) — including both factions' signature casters and immortal anchors — can never be fielded in normal play. Requires extending CommandCardSystem + GetUnitByCategory to expose multiple units per building with a selection UI. |
| **Air production building + Air category mapping** | medium | No Air BuildingType exists; CategoryForBuilding never returns 'Air'. Both air units (Greycrest griffin, Envy wyvern) are unbuildable except via scenario placement. Needs a new BuildingType (aviary), CategoryForBuilding→'Air', TechTreeChecker entries (BuildingTypeId/ParseBuildingType/DisplayName), WORKER_BUILD_TYPES + card switches in CommandCardSystem, and the building entry in each faction JSON. |
| **D1 Modifier system + ModifierSystem (buffs/debuffs/auras/status/DoT/HoT)** | large | The keystone for nearly every ability and BOTH faction mechanics. Court's entire identity (Sanguine Furnace passive regen via Persistent+Heal; Glut accelerated-regen via Refresh-stack ApplyModifier) and Rebel buffs/roots/invuln/slows all depend on the not-yet-built Modifier store + system running before CombatSystem. Until this ships the Court is a pure stat sheet and unbalanceable as designed. |
| **D1 effect-graph executor + leaf set (Damage/Heal/stat-delta/ApplyModifier/SetVariable/FireProjectile/TargetFilter; Sequence/SearchArea/Persistent)** | large | The shared effect vocabulary that every signature ability compiles to. Includes a NON-MATRIX direct-HP stat-delta leaf required so Equal Exchange self-cost is flat and armor-independent (a self-Damage leaf would scale by the caster's armor). No src/Effects/ directory exists yet. |
| **D2 trigger seam — on-death (and other On*) events** | large | Court's 'Feed the Furnace' Glut requires an on-death trigger firing SearchArea+ApplyModifier on nearby allies. This is the most infrastructure-heavy single ability and the Court's core attrition loop rests on it. Part of the D2 event/dataflow graph layer. |
| **Anti-air / ground-only targeting (TargetFilter honored by CombatSystem)** | medium | CombatSystem ignores Air/Ground entirely — anyone in range is a valid target. True anti-air discrimination (and flyer-only / can't-hit-air rules) needs D1 TargetFilter plus a CombatSystem rewire to honor it. Affects both air units' intended roles. |
| **Worker-cast ability trigger surface** | small | Gatherers are skipped by CombatSystem and only have gather/build command paths. The Acolyte's active 'Mend Matter' needs an ability-activation path for workers that doesn't exist even once D1 lands (the effect graph is D1; the activation on a worker is an extra dependency). |
| **Anti-building combat (unit attacks/splash damage BuildingStore)** | medium | Projectile/splash code only iterates EntityWorld units, not BuildingStore. Both sieges' anti-structure role does not function today; 'flatten fortifications' was removed from copy pending this. Needs ApplyHit/ApplySplash to target buildings. |
| **Crystal-spend wiring (multi-resource costs)** | small | cost_crystal is read but never spent (TrainUnit only calls SpendOre). Treated as cosmetic 'Core-shard' flavor today. If crystal is meant to gate elites, TrainUnit/QueueWorkerBuild must spend it. |
| **Data-driven DamageMatrix (load from JSON)** | small | Matrix multipliers and the 4 damage / 5 armor enums are hardcoded; the file's own 'Phase 1' TODO. Needed if balance wants custom cells or a Hero/Chaos type. Designers can currently only pick existing rows/cols. |
| **'Next-shot' / charge / on-hit-rider primitive** | small | Pierce Marksman's original 'load the next shot' framing has no D1 representation (no charge/next-attack-rider). RESOLVED in design by recasting Transmuted Slug as an immediately-cast FireProjectile nuke (pure D1) — this epic is only needed if any future ability wants a true buffered-next-attack interaction. |
| **Vermillion Core map-objective economy (capture/manufacture vs destroy/neutralize)** | large | The setting's headline theme has no gameplay surface. Court command_center is flavored as the soul-renderer and crystal as Core-shard fuel, but no Core node, harvest, or break interaction exists. Optional showcase capstone tying the economy to the objective. |

---

## Open design decisions for Alec

1. AIR THIS MILESTONE? Both air units (Greycrest, Envy Wraithwing) are unbuildable until the Air-building epic. Decide: cut them from balance scope now, ship the Air-building epic, or keep them as scenario-only placement units. The whole air asymmetry slice is invisible in normal play until resolved.
2. MULTI-UNIT BARRACKS/RANGE: with only the first category unit trainable, do we (a) ship the per-unit production-selection UI epic so all 4 Melee / 2 Ranged are fieldable, or (b) accept a slimmer buildable roster (1 Melee + 1 Ranged + Siege + Worker per faction) for the showcase and treat the rest as designed-for-later? This decides whether the casters and immortal anchor are reachable.
3. SEQUENCING: the Court is unplayable as a faction identity until the D1 Modifier + Persistent(HoT) + D2 on-death systems ship — the Covenant's stat/burst identity is closer to data-expressible. Confirm we either (a) build D1/D2 before any faction balance pass, or (b) ship an honest interim where the Court's edge is paid purely in the HP/armor stats that run today (as tuned in this doc). The current interim numbers assume (b).
4. FORTIFIED FOR UNITS? I dropped Pride Colossus from Fortified to Heavy (Fortified reserved for buildings) to fix the broken eHP/cost. Confirm you want NO unit at Fortified, or whether the immortal anchor should keep Fortified with a much higher cost/lower HP instead.
5. CRYSTAL: keep cost_crystal as pure cosmetic 'Core-shard' flavor (current assumption, stripped to 0 on Rebel units that implied gating), or commit to the crystal-spend epic and make it a real second resource that gates elites?
6. STAT LANDING: the reviewed/revised numbers do not match the JSON on disk (alpha=Alpha Faction, beta=Iron Pact, old stats). Confirm I should land these revised stats + renamed display_names + themed mesh filenames into alpha_faction.json / beta_faction.json as a follow-up data task (this was a design-only task; no files were modified).
7. CORE OBJECTIVE: is the Vermillion Core map-objective economy in scope for the showcase, or a deliberate post-1.0 cut? Right now it lives only in lore/flavor with no mechanic.
8. GRIFFIN NAME: I renamed it 'Greycrest, the Bonded' (a named rescued individual, per the found-family theme). Confirm the name or pick from alternates (Pinion the Bonded / Talonsworn).
