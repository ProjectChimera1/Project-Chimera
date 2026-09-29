# Design Handoff Prompt — Project Chimera UI (ALCHEMY RE-THEME)

> Follow-up brief for Claude Design. The previous pass nailed the **layout, component structure,
> and information density** — keep all of that. This pass changes only the **mood, material, motif,
> and palette**. Do NOT re-architect the screens; re-skin them.

---

## What to change and what to keep

**KEEP (do not touch):** every screen's layout, panel placement, component kit structure, the
non-diegetic flat-overlay HUD, the consolidated Unit Card Editor, the EDIT↔PLAY toggle, all the
information hierarchy and density. The previous version is structurally correct.

**CHANGE:** the *surface treatment* — the theme currently reads **futuristic / sci-fi schematic**
(StarCraft-II-meets-Northgard). I want to pull it toward an **arcane-alchemical** identity that
matches the project's name, **Chimera**. Think **Fullmetal Alchemist**: transmutation circles,
alchemical sigils, the philosopher's stone, homunculi, equivalent exchange — a feel that is
**wise, scholarly, occult, hand-engraved, and a little uncanny**, rather than clean-future-tech.

The conceptual hook: in this tool you **transmute** raw parts into living units and worlds — a
*chimera* is a fusion of parts. The UI should feel like the instrument panel of a master
alchemist's workshop, not a spaceship cockpit.

## New aesthetic direction — "Arcane Workshop"

Still **dark-theme primary** and still **flat/legible-first** (this is an RTS HUD over a busy
battlefield — clarity always wins). But reskin the chrome with this material + motif language:

- **Material story:** dark slate / charcoal / aged-iron panel surfaces, with **etched brass &
  antique-gold line-work** as the structural accent — engraved borders, fine inlaid rules, thin
  precise filigree at panel corners. Read it as *engraved metal and inked parchment*, not glossy
  glass or neon. A subtle aged/patina texture is welcome where it won't hurt legibility.
- **Motif — transmutation geometry:** replace generic sci-fi corner brackets and tick-marks with
  **alchemical sigil language**: concentric rings, fine radial geometry, runic/glyph accents,
  thin circular arrays. Use these *sparingly and structurally* (frames, dividers, loading and
  progress states) — suggestion, not decoration overload.
- **Iconography:** redraw icons in an **engraved alchemical-emblem** style — geometric line
  emblems that look etched into metal, like apothecary/alchemy symbols, while staying instantly
  readable as their function.
- **Typography:** keep data legible, but shift the *headers and titles* to a refined engraved
  feel — a high-contrast antique/serif or an "old scientific instrument label" face for headings
  and section titles; keep a clean sans + monospace for stats/readouts. The result should feel
  **learned and precise**, like an alchemist's ledger, not a gamer-neon display.
- **Mood:** candlelit study and arcane laboratory — warm, wise, slightly mysterious. Premium and
  confident, but with depth and age rather than chrome and gloss.

## Palette guidance (propose tokens, but steer here)

- **Primary accent → antique brass / alchemical gold** for interactive, selected, and highlight
  states. Warm gold reads "arcane instrument," and critically it stays **clearly distinct from the
  reserved blue/red faction/team colors** in the world.
- **Secondary accent → verdigris / patinated teal-green** ("aged copper," "transmutation glow")
  for active/in-progress/success states (e.g. training progress, valid badge).
- **Reserve crimson/philosopher's-stone red** for *rare emphasis only* — and be careful: **red is a
  faction/team color**, so do NOT use crimson as a general UI accent. Use it only for danger/alert
  semantics, paired with an icon, never as team identity.
- Backgrounds stay dark and desaturated (slate/charcoal/near-black) so brass line-work and gold
  accents glow against them like etched metal in low light.

## Thematic motif touch-points (where alchemy earns its keep)

Apply the motif most strongly on these signature moments — elsewhere keep it quiet:

- **AI-generate / unit-create panels** → frame as **transmutation**: a transmutation-circle motif
  around the generate action; "the act of creation" is the thematic core of the whole tool.
- **Unit Card Editor validation badge** → an **alchemical seal / completed-array** stamp instead of
  a generic green check.
- **EDIT ↔ PLAY toggle** → the metaphor of activating a transmutation array; let the active side
  glow brass/verdigris.
- **Minimap frame & loading/progress spinners** → fine concentric-ring / alchemical-array framing.
- **Logo / title screen** → the "Chimera" wordmark with a subtle transmutation-circle backing
  emblem; arcane-workshop background instead of generic sci-fi vista.

## Hard constraints (unchanged from before)

- **Legible over a busy zoomed-out 3D battlefield** — clarity beats ornament; no heavy filigree or
  textures that fight the numbers. The alchemy is *flavor on a clean structure*, not clutter.
- **Faction/team colors (blue vs. red) stay reserved for in-world units** — the brass/verdigris
  chrome must never read as a team color, and crimson is restricted to danger semantics.
- **Maps to Godot Control nodes** — engraved borders, flat fills, line-emblem icons, sigil accents
  all rebuild as standard panels/borders/textures; no exotic effects.
- **Performance:** 500–2,000 units at 60 FPS — avoid full-screen blur, live-blur, or heavy shadow
  stacks. Etched brass line-work over flat dark fills is cheap; keep it that way.
- **Accessibility:** WCAG AA contrast on the dark theme, colorblind-safe team colors, never encode
  meaning by color alone (pair with icon/label).

---

Deliver an updated design system — revised color tokens (dark + brass-gold accent + verdigris
secondary), the engraved/alchemical typography pairing, the re-styled component kit, and re-skinned
high-fidelity mockups of the same screens — all sharing one cohesive **arcane-alchemical "Arcane
Workshop"** identity that says *Project Chimera: transmutation, fusion, wisdom* rather than
generic sci-fi.
