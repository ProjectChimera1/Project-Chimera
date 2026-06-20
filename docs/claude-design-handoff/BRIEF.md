# Project Chimera — UI Design Handoff · "THE TRANSMUTATION LAB"

> **For Claude (design pass).** This is a complete brief. Attached alongside it are the reference images
> in `reference/` — they are the visual north star; match their material, color, and mood. Build in the
> identity below and hold the hard constraints. Identity source of truth: `docs/world-theme-bible.md`.

---

## 0. What to produce

Design the UI **design system + key screens** for **Project Chimera**, a desktop RTS *creation platform*.
Deliver as **dark-themed HTML/CSS artifacts**, framed for **1920×1080** desktop:

1. **A style-tile / design-system page** — palette swatches, type scale, and the full component kit.
2. **Four screens at high fidelity** (Section 10) — HUD, the Transmutation Bench, Shell & Menus, Content Browser.
3. **The three signature moments** rendered with real CSS (motion welcome where noted) — Section 11.

Use semantic, flat, rebuildable structure (these become Godot Control nodes later — see constraints). Provide
**hover / selected / disabled** states. All numerals in a monospace face.

## 1. The product (one paragraph)

Project Chimera is both a polished single-player **RTS** and a **Warcraft III World-Editor-class tool** for
building custom games without code. PC desktop, 16:9, keyboard + mouse, dark theme primary. Two surfaces
matter most: the **in-match HUD** (most-seen) and the **creation editor** (the headline differentiator).

## 2. The identity — "The Transmutation Lab"

This is the **instrument panel of a master transmuter's bio-alchemical workshop**. You do not *build* units —
you **transmute** raw matter and living parts into chimerical war-beasts. The UI should feel like an
alchemist's ledger and laboratory console: **learned, precise, weighty, a little uncanny.** Warm engraved
metal framing cold, living light.

It runs to the bone of the design, not just the skin: the alchemical maxim *solve et coagula* ("dissolve and
recombine") **is** the game's composition-over-inheritance pillar — a unit is broken into parts and recombined.

## 3. Tone & microcopy

Solemn & scholarly, a thread of the uncanny. Confident, builder-friendly. Never cute, never grimdark, never
gamer-neon. Microcopy is measured, declarative, a touch arcane:
- complete → *"Array stable. The specimen holds."*  · empty → *"The crucible is empty. Begin the Work."*
- error → *"The formula will not bind."*  · destructive → *"Dissolve this specimen? Its matter returns to the prima."*

## 4. Reference images — what to draw from each *(the north star)*

| File (in `reference/`) | What it is | What to take from it |
|---|---|---|
| `01-sigil-transmutation-circle.png` | A clean, single transmutation circle (thin line) | The **exact construction** of the signature transmutation-circle motif — concentric rings, radial geometry. Render in **brass**. Use for the Transmute frame, the minimap ring, and loaders. |
| `02-sigil-circles-sheet.png` | Sheet of 6 alchemical circles | Motif vocabulary — framing devices, dividers, loaders. |
| `03-sigil-seals-grid.png` | Grid of ~20 small alchemical **seals** | The **icon + "sealed-array" validation-stamp** language — geometric line emblems that stay instantly readable. |
| `04-material-gold-on-dark.png` | Embossed **antique-gold ornament on near-black** | **THE material & color story.** Warm engraved gold on charcoal, rich but dark. Lean the brass accent toward *this* gold. Note the *restraint* — ornament that stays legible. |
| `05-bioluminescent-specimen-jar.png` | A glowing specimen in a glass jar, dark lab | The **"alive" beat** — cold blue-green bioluminescence behind glass. Use for the unit-preview **specimen vat** and the bioluminescent active/charge glow. |
| `06-ui-hades-menu.jpg`, `07-ui-hades-weapon.jpg` | **Hades** menus | **The execution target.** Ornate gold linework on dark that stays *totally legible* and full of character; strong type hierarchy; how ornament coexists with clarity. Match this bar. |
| `08-ui-northgard.jpg` | **Northgard** build menu | Clean, warm, painterly strategy UI at RTS scale — clarity with warmth. |
| `09-ui-ats-hud.png`, `10-ui-ats-alerts.png`, `11-ui-ats-confirmation.png` | **Against the Storm** | Steal **structure**: a dense-but-readable strategy HUD, and the alert/toast + confirmation/modal patterns. Dark-primary legibility. |

> **Synthesis:** take the **legible-ornament discipline of Hades**, dress it in the **gold-on-charcoal
> material of `04`**, structure it with the **sigil geometry of `01`–`03`**, and let the **bioluminescence of
> `05`** be the one living accent. Northgard/ATS govern density and clarity at RTS scale.

## 5. Palette (seed hex — tune toward the references)

- **Substrate** `#14161A` · **panel** `#20242B` · **raised** `#2A2F38`
- **Brass / antique gold** (primary accent — structure, interactive, selected) `#C9A86A`, bright `#E3C887`,
  deep `#8A6E3C` — *push toward the richer gold of `04-material-gold-on-dark.png`.*
- **Verdigris-teal** (active / in-progress / valid) `#4FB39A`
- **Bioluminescent** (rare "alive" highlight) `#7FE3A1`
- **Text** parchment `#ECE6D8`, muted `#9A958A`
- **Danger** crimson `#C8503C` — icon-paired, **danger only**, never a general accent
- **Team/ownership colors (blue vs red) are reserved** for in-world units — UI chrome must never read as a team color
- **Light story:** a low candle-warm key + a cold bioluminescent rim. Warm metal, cold life.

## 6. Typography

- **Titles / section heads:** engraved/antique serif or "scientific-instrument label" (e.g. *Cinzel*, *Spectral*)
- **Body / UI:** clean geometric sans (e.g. *Inter*)
- **All numerals — resources, timers, stats:** monospace (e.g. *JetBrains Mono*). A cheap, strong "instrument" signal.

## 7. Material & motif language + the three signature motifs

Every panel **fuses two halves**: warm **engraved brass/gold line-work** (etched borders, fine inlaid rules,
filigree at corners) over charcoal, with **cold bioluminescent glow** as the living accent. Sigil geometry is
used **structurally and sparingly** — frames, dividers, loaders, progress — never decoration overload.
**Clarity always beats ornament.** The three motifs that carry the identity (get these right):
1. **The transmutation-circle Transmute frame** (from `01`).  2. **The sealed-array validation stamp** (from `03`).
3. **The minimap ring** (from `01`).

## 8. Vocabulary (use where it doesn't hurt clarity; keep raw stat names plain)

Generate/Create → **Transmute** · a unit → a **specimen / chimera** · production building → **Athanor** ·
the valid badge → a **sealed array** · Edit↔Play → **Dormant ↔ Animate** · trigger/rules → **inscribed law**.

## 9. Component kit

Engraved **panel/card**; **button** (primary / secondary / ghost + disabled); **icon button**; **slider with
numeric input**; **dropdown**; **tab bar**; **tooltip**; **resource/stat readout chip** (mono numerals);
**progress bar** (filling alchemical bar or ring); **list row**; **modal/dialog**; **toast/alert banner**;
the **sealed-array stamp**; and a **transmutation-circle frame** element.

## 10. Screens to produce

**① In-game HUD** *(most-seen)* — non-diegetic flat overlay on a 3D battlefield (angled top-down, 500–2000 units).
- Top bar: **Ore** and **Crystal** (engraved-emblem icon + mono amount), a **supply** readout (12/20), a clock.
- Bottom-left **Selection & Command card:** portrait(s) in an engraved frame + a command grid (Move, Attack-Move,
  Stop, Hold, Patrol, abilities) each with a **hotkey glyph**. When an **Athanor** is selected, show trainable
  specimens with cost, build-time, a **verdigris progress bar**, and a `[need: …]` tag on locked items.
- Bottom-right **Minimap:** square, fog-of-war (unexplored/explored/visible), team-color dots, building markers,
  camera rectangle — **framed by a transmutation-circle ring**.
- Transient **alerts** ("Under attack!"; centered amber "Waiting for peer…"). Center clear. Control-group tabs 1–9.

**② The Transmutation Bench (Unit Card Editor)** *(headline differentiator + signature moment)* — the WC3
"one entity, one view" model, NOT scattered tabs.
- Editor shell: top toolbar with the prominent **Dormant ↔ Animate** toggle, tool groups (Terrain, Entities,
  Resources, Laws, Win Conditions, **Transmute**), undo/redo, save/publish; a dockable palette; 3D world center.
- Hero panel: **left** = a live rotating 3D model in a **glass specimen-vat frame** (cold inner glow), buttons to
  change model/icon. **Right** = grouped **Combat** (HP/Attack/Range/Armor/Speed as sliders + numeric input, min/max),
  **Economy** (costs, build time), **Abilities** (chips + add-from-library), a **Hero** toggle (XP/ultimate). A
  **base-form picker** ("Start from Footman/Archer/Worker"), a **compare-to-specimen** view, and the **sealed-array
  validation stamp** when valid. **Simple ↔ Advanced** toggle (presets/sliders ↔ every field + raw JSON). Tooltips everywhere.
- The signature **Transmute** control: the generate action **ringed by a transmutation circle**; show resting +
  ready (bioluminescent-charged) states.

**③ Shell & Menus** — title (the "Chimera" wordmark over a subtle transmutation-circle emblem, arcane-workshop
backdrop), main menu (Play / Create / Browse / Settings / Quit), tabbed Settings (incl. remappable keybinds,
accessibility), and a lobby (player slots with faction + color, ready states, chat).

**④ Content Browser** — a Steam-Workshop-like in-app grid: scenario cards (thumbnail, title, rating, subscriber
count, tags), search + filter sidebar + sort, a Featured rail, a detail page (Subscribe / Play Now), and a publish flow.

## 11. Signature moments

- **Transmute** — the brand in one moment: a brass transmutation circle, a bioluminescent flood, parts fusing into
  a specimen in the vat. **Motion welcome** (CSS) for the resting → charging → complete sequence.
- **Sealed-array stamp** — completing a specimen stamps a finished alchemical array (brass + verdigris), not a generic check.
- **Dormant ↔ Animate** — the edit/play toggle reads as activating an array; the live side glows.
- **Loaders / progress** — fine concentric-ring / alchemical-array framing, not a plain spinner.

## 12. Hard constraints + accessibility

- **Clarity over a busy zoomed-out battlefield — legibility ALWAYS wins.** Alchemy is flavor on a clean structure.
- Rebuilds as **flat panels** (engraved borders, flat fills, line-emblem icons, sigil accents). **No full-screen
  blur, no heavy live effects** (it must run with 500–2000 units at 60 FPS in Godot).
- **WCAG AA** contrast on dark; support **UI scaling**; **never encode meaning by color alone** (pair icon/label);
  keep team colors colorblind-safe.

## 13. Anti-references — do NOT produce

- Generic dark-fantasy / Dark-Souls-clone ornate chrome (ornament for its own sake).
- Neon sci-fi / hologram-blue "FUI" HUDs.
- Glassmorphism, soft gradients, default rounded-pill cards, indigo/violet accents — the safe AI default.

## 14. Output format

One cohesive dark-themed deliverable: the **style-tile page first**, then each screen as its own full-bleed
1920×1080 frame, real CSS, hover/selected/disabled states, monospace numerals, the three signature motifs present.
Note anything that is a static mock vs. interactive.
