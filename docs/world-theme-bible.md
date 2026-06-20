---
status: draft
updated: 2026-06-19
project: Project Chimera
kind: world-theme-bible
sources:
  - Project_Chimera_GDD.md
  - _bmad-output/project-intent.md
  - _bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-05/DESIGN.md
  - _bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-05/design-handoff-prompt-retheme-alchemy.md
feeds:
  - the UI reference wall
  - DESIGN.md spine (tokens) + GDD §1 (Art direction)
complements:
  - docs/art-style-guide.md   # governs 3D assets; this file governs UI + world identity
---

# Project Chimera — World & Theme Bible

> The identity spine the visual system was always missing. `DESIGN.md` owns *how it looks* and
> `EXPERIENCE.md` owns *how it works* — this owns **what it's about**, so both have a story to grow
> out of instead of defaulting to the safe mean. Where a mock disagrees with this file, this file wins.

---

## 1. Premise — "The Transmutation Lab"

Project Chimera is a master transmuter's **bio-alchemical laboratory**. You do not *build* units — you
**transmute** them: raw matter and living parts fused, under inscribed law, into chimerical war-beasts.
The editor is your workshop; a match is your work tested in the field.

This is one theme, not two. In the reference everyone already reaches for — *Fullmetal Alchemist* — a
**chimera is an alchemically-created biological hybrid**. Alchemy applied to living matter. "Living Lab ×
Arcane Workshop" collapses into a single idea: **the transmutation of living matter.**

It pays off the name three times over: a *chimera* is a beast fused from parts → your tool fuses parts
into living units → the UI itself is two material languages visibly fused. The medium is the message.

**It also expresses your core design pillar, not just your mood.** The alchemical maxim *solve et
coagula* — "dissolve and recombine" — **is** composition-over-inheritance. The Unit Card Editor, where a
"healer" is broken into *ranged + heal + support AI* and recombined, is literally the Great Work. The
theme runs to the bone of the design, not just its skin.

## 2. Tone & register — solemn & scholarly, with a thread of the uncanny

Not grim horror; not heroic triumph. **Calm gravity.** The mood of a learned practitioner in a
candlelit study who knows exactly what they are doing and what it costs. Precise, reverent, weighty —
and *slightly* uncanny at the edges (this is, after all, the making of living things). Wonder with weight.

- **What it buys:** it suits a tool you sit inside for hours, it reconciles with the existing
  "confident, concise, builder-friendly" microcopy voice, and it stays distinctive without tipping into
  either body-horror fatigue or familiar fantasy-triumphant.
- **Microcopy voice** — measured, declarative, a touch arcane; never cute, never grimdark:
  - complete → *"Array stable. The specimen holds."*
  - error → *"The formula will not bind. Check the bound components."*
  - destructive confirm → *"Dissolve this specimen? Its matter returns to the prima."*
  - empty state → *"The crucible is empty. Begin the Work."*
- **Restraint is the register.** The uncanny is a thread, not a texture — one well-placed unsettling
  detail reads as mastery; ten reads as a haunted house.

## 3. World logic (just enough fiction)

A light frame — enough to give surfaces meaning, not a campaign plot.

- **The Work** — the discipline of transmutation; the act of creation in this world.
- **Prima materia** — the raw first-matter you draw on (the economy's base resource).
- **Equivalent exchange** — the world's one law: nothing is created without cost. Reads as the
  resource/supply economy *and* as the tool's honesty about tradeoffs.
- **Inscribed law** — rules are *written*, and what is written governs reality. This is the diegetic
  frame for the Scenario Director / triggers: a creator inscribes the laws of their world.
- **The transmuter** — your in-world role: the practitioner who designs, animates, and commands the
  lab's creatures. (Distinct from the platform archetypes Commander / Architect / Tinkerer, which are
  untouched — those describe *players*, this describes the *fiction*.)

## 4. The two schools — factions as the bio↔iron axis

The aesthetic split and the faction split collapse into **one decision** — but keep what's *real* vs.
*proposed* honest:

- **Iron Pact (Iron school) — already real.** Its shipped roster is iron/forge-built (Forgehand,
  Bulwark, Ironclad, Forge Citadel, Bolt Foundry, War Foundry). This direction *names* an identity the
  faction already has in data; it ships today.
- **Verdant Coil (Vital school) — a proposal.** The current `alpha_faction.json` ("Alpha Faction") is
  generic-medieval (Worker, Scout, Archer, Mage, Griffin, Barracks) with a placeholder name and no theme.
  Giving it a living/organic identity is a real art-direction change to its ~12 currently-placeholder
  meshes — adopt it when those assets are generated; until then it is *direction, not description*.

The stat asymmetry backs the axis on the iron side (heavy / durable / slow = forged) and leaves the Vital
side an open, fitting target (quicker / softer = living).

| | **The Iron Pact** *(Iron school)* | **The Verdant Coil** *(Vital school — renames "Alpha")* |
|---|---|---|
| **Transmutation** | *Hard* — matter forced into permanent, rigid form | *Soft* — matter coaxed into living, growing form |
| **Philosophy** | Control, permanence, the perfected machine | Growth, adaptation, life |
| **Materials/forms** | Forged brass & iron, clockwork, rigid angular sigils, constructs & automata | Flesh, bone, vine, chitin, bioluminescence, curved organic sigils, grown things |
| **Stat identity (on file)** | +HP / +armor / −speed → heavy, durable, slow | baseline → quicker, softer, more fragile |

> "Verdant Coil" is a proposed name (verdant = living; coil = the serpent-tail of the myth and the
> double-helix) — trivial to swap. "Iron Pact" already fits perfectly; keep it.

## 5. Color discipline (this protects your sacred rule)

Three **independent** color layers — keeping them separate is what makes the system read as intentional:

1. **The Lab — UI chrome.** Brass + verdigris on charcoal. Neutral to ownership. The transmuter's
   instrument panel. The workshop practices *both* schools, so chrome carrying both brass and verdigris
   is thematically correct, not a faction statement.
2. **Ownership — team colors.** Blue vs red (and the rest), **sacred and untouched** — colorblind-safe,
   reserved for unit tints, minimap dots, player tags. This bible does not redefine them. Any faction
   can be fielded under any team color.
3. **Faction aesthetic.** Iron *forms* vs Vital *forms* — expressed in unit silhouette and material,
   **not** ownership tint. An Iron Pact army can be blue or red; its "iron-ness" is the mesh, not the team.

> **Implementation reality (2026-06-19):** today each faction file bakes in a single `color` (Alpha blue
> `[0.2,0.5,1.0]`, Iron Pact red `[0.8,0.25,0.1]`), so faction and ownership are currently **coupled**.
> The decoupled model above is the *target* — a creation platform may want four players all fielding one
> faction — reached by a small later change: move `color` off the faction onto a per-player slot. Until
> then, faction = its color.

**Crimson is danger-only** and must always pair with an icon — never as a general accent, never as
ownership (red is a team color). Keep the UI "active/in-progress" semantic on the cooler *verdigris-teal*,
distinct from a Vital unit's brighter in-world bio-green, so the layers never bleed.

## 6. Material & motif language

The arcane half and the bio half **share every panel** — that fusion is the whole look.

- **Arcane half:** dark slate / aged-iron surfaces; **etched brass line-work** as the structural accent —
  engraved borders, fine inlaid rules, thin filigree at corners; alchemical **sigil geometry**
  (concentric rings, fine radial arrays, runic glyph accents) used *structurally*, on frames, dividers,
  loading and progress states.
- **Bio half:** glass vats, **bioluminescent fluid**, faint living tissue / vein / growth motifs, chitin
  and bone edging — read as *something alive sealed behind instrument glass*.
- **The fusion:** warm engraved brass against cold bioluminescent glow, both on a charcoal substrate —
  metal that frames life.
- **Restraint rule (inherited, non-negotiable):** clarity beats ornament. This is an RTS HUD over a busy,
  zoomed-out battlefield. The alchemy is **flavor on a clean structure**, never clutter over the numbers.

## 7. Palette & light (seed tokens — tune in-engine)

Seeds for the empty `DESIGN.md` token tables; treat as starting points, calibrate for WCAG AA on dark.

- **Substrate** — near-black slate `#14161A`; panel surface `#20242B`; raised `#2A2F38`
- **Brass** *(primary accent — interactive, selected, structural line-work)* — `#C9A86A`; bright
  edge-light `#E3C887`; deep `#8A6E3C`
- **Verdigris-teal** *(secondary — active / in-progress / valid)* — `#4FB39A`; deep `#3E8E7E`
- **Bioluminescent** *(rare highlight / the "alive" beat)* — cold green `#7FE3A1` or warm `#E0A24E`
- **Text** — warm parchment `#ECE6D8`; muted `#9A958A`
- **Danger** *(crimson, icon-paired, never ownership)* — `#C8503C`
- **Ownership** — team blue/red, **defined elsewhere, untouched here**
- **Light story:** a low **candle-warm key** + a **cold bioluminescent rim** — warm metal, cold life.

## 8. Typography

- **Titles / section heads** — a refined **engraved / antique** face ("old scientific-instrument label"):
  high-contrast serif or inscriptional (e.g. *Cinzel*, *Spectral*, *IM Fell*). The alchemist's ledger.
- **Body / UI** — a clean, slightly geometric **sans** (e.g. *Inter*, *Barlow*, *Exo*).
- **Readouts / stats** — **mono** (e.g. *JetBrains Mono*, *IBM Plex Mono*) — precise, instrument-like.
- Families are candidates to test, not final.

## 9. Vocabulary reskin (proposed, adopt incrementally)

Flavor on a clean structure — rename surfaces and actions, not whole systems at once. Keep mechanical
names (Ore, Crystal) wherever a thematic name would hurt clarity; let the theme live in icon + label.

| Mechanical | Thematic |
|---|---|
| Generate / Create unit | **Transmute** ("begin the Work") |
| Unit / created entity | **Chimera** / **specimen** |
| Unit definition / card | **formula** / **array** |
| Validation badge (complete) | **the array is sealed** — a completed-transmutation-circle stamp |
| EDIT ↔ PLAY toggle | **Dormant ↔ Animate** (the array sleeps, then lives) |
| Production building | **Athanor** (the alchemist's furnace) |
| Tech tree | the stages of **the Work** (optional deep cut: nigredo→albedo→citrinitas→rubedo) |
| AI-generate panel | **the transmutation circle** / the Great Work bench |
| Trigger / Scenario Director | **inscribed law** |
| Publish scenario | seal & share a **grimoire** |
| Faction | **school** / **order** |

## 10. Signature moments (where the theme earns its keep)

Apply the motif *strongest* here; keep it quiet everywhere else.

1. **The Transmute action** — your brand in one animation: a brass transmutation circle ignites,
   bioluminescent matter floods the array, parts fuse, a chimera coalesces in the vat. Build this one
   thing well and the identity is established.
2. **Validation seal** — completing a unit stamps a finished alchemical array, not a generic green check.
3. **EDIT ↔ PLAY (Dormant ↔ Animate)** — the toggle reads as activating a transmutation array; the live
   side glows.
4. **Loading / progress** — fine concentric-ring / alchemical-array framing instead of a plain spinner.
5. **Title / logo** — the "Chimera" wordmark over a subtle transmutation-circle emblem; an arcane-workshop
   backdrop, not a generic vista.

## 11. Reference anchors (seed list for the reference wall)

Direction for the next step — pull *specific shipped frames*, not adjectives, into these buckets:

- **Bio-alchemy identity:** *Fullmetal Alchemist* (chimeras, transmutation circles, equivalent exchange);
  apothecary / alchemical emblem plates; anatomical & botanical engraving; *Frostpunk* / *Disco Elysium*
  for UI-as-art-direction.
- **Clarity over a battlefield (steal structure):** *Mindustry*, *StarCraft II*, *Against the Storm*,
  *Songs of Conquest*, *Northgard*.
- **Material & texture:** etched brass / engraved metal, inked parchment, specimen-jar bioluminescence,
  patinated verdigris copper.
- **Motion / juice (2026 instant-understanding):** transmutation-circle shader loops; tween-driven
  micro-interactions; direct-manipulation editor feedback.

---

## How this maps to your hard constraints

Nothing here breaks the engine reality: every motif rebuilds as **Godot Control nodes** — engraved
borders as 9-patch StyleBoxes, sigils as flat textures, the Transmute circle as a cheap animated shader
over flat fills. **Dark-theme primary**, **faction colors reserved** (§5), **WCAG AA / colorblind-safe /
never color-alone**, and the **500–2,000-unit, 60 FPS** budget all hold — etched line-work over flat dark
fills is cheap, and no full-screen blur or heavy shadow stacks are implied.
