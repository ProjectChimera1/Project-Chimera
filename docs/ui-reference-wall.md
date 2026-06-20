---
status: v1 (living doc)
updated: 2026-06-19
project: Project Chimera
kind: ui-reference-wall
governed_by: docs/world-theme-bible.md   # the "Transmutation Lab" identity this wall serves
buckets: [bio-alchemy identity, clarity-with-warmth, material & texture, motion]
pending:
  - visual HTML contact-sheet render (thumbnails grouped by bucket)
  - motion / shader bucket (next research pass)
---

# Project Chimera — UI Reference Wall

> A *tight* wall, not a mood board: each entry says what to **STEAL** and what to **LEAVE**. The job is
> to brief design from specific shipped frames instead of adjectives. Serves the identity in
> `world-theme-bible.md` (bio-alchemy "Transmutation Lab", solemn-scholarly, clarity over a battlefield).

---

## A. Bio-alchemy identity & sigil motif

- **Splendor Solis (1582), Donum Dei & other alchemical diagrams** — *The Public Domain Review*,
  "The Surreal Art of Alchemical Diagrams."
  <https://publicdomainreview.org/collection/the-surreal-art-of-alchemical-diagrams/>
  **STEAL:** the circular emblem grammar — meaning encoded in concentric rings; the engraved,
  hand-inked line quality. This is your sigil vocabulary, and it's public domain (directly usable as
  texture/motif). **LEAVE:** the busy medieval figures and grotesques — too noisy for a HUD.
- **PICRYL — Alchemy (11,087 PD images) + "Alchemical tools" collection.**
  <https://picryl.com/topics/alchemy>
  **STEAL:** real engraved vessels (alembic, retort, crucible, athanor) as icon refs for tools/panels;
  authentic engraving texture for borders. **LEAVE:** anything that reads as clip-art.
- **FMA transmutation-circle anatomy** — CBR breakdown (central point · circumferential lines · inner
  patterns · elemental symbols).
  <https://www.cbr.com/fullmetal-alchemist-transmutation-circle-facts/> ·
  board: <https://www.pinterest.com/ideas/fullmetal-alchemist-transmutation-circle/910343659511/>
  **STEAL:** the *construction rule* of a circle — use it literally for the Transmute-button shader and
  loading rings. **LEAVE:** anime-specific flame/elemental glyphs (too on-the-nose).

## B. Clarity-with-warmth game UI (steal the structure)

- **Against the Storm** — browsable HUD screens on Interface In Game, plus the dev rationale.
  <https://interfaceingame.com/games/against-the-storm/> ·
  <https://eremitegames.com/interface-update/>
  **STEAL:** panel hierarchy, and the *lesson* in their Interface Update — they rebuilt FROM
  ornate-but-unreadable TO clean. That devblog is the argument behind our "flavor on a clean structure"
  rule. **LEAVE:** their lighter palette (we're dark-primary).
- **Songs of Conquest** — high-contrast, clutter-free UI; battlefield readable at a glance, yet a strong
  painterly identity. **STEAL:** proof that a heavy art identity and ruthless legibility coexist — our
  exact target. **LEAVE:** pixel-art treatment (wrong medium for us).
- **Game UI Database** — index; search strategy / RTS / dark.
  <https://www.gameuidatabase.com/>
  **STEAL:** comparative layouts for command cards, minimaps, resource bars.

## C. Material & texture (brass · verdigris · specimen · engraving)

- The Section-A engravings double as **etched-metal + inked-parchment** texture reference.
- **Specimen-jar bioluminescence** — the cold inner glow behind glass = our "alive" beat (the bio half).
  *(research pass pending — collect 3-4 concept refs.)*
- **Marketplace dark-fantasy / "Dark Alchemy" UI kits** (ArtStation / itch / GraphicRiver; Dark Souls /
  Blasphemous lineage).
  **STEAL:** *build technique only* — how etched borders, bevels, and inlay are constructed as 9-patch
  panels. **LEAVE — important:** the overall look. This is precisely the generic dark-fantasy zone we
  must not copy; mine it for construction, never for identity.

## D. Motion / "2026 instant-understanding"  *(next research pass)*

- Transmutation-circle / magic-array **shaders** (Godot) for the Transmute action and loaders.
- Tween-driven micro-interactions; direct-manipulation editor feedback (tools where attention is).
- To be sourced and annotated next.

---

## Anti-references — the "typical AI design" zone to avoid

A tight wall also says what *not* to do. Steer clear of:
- Generic marketplace dark-fantasy UI kits as a *look* (ornate-for-ornate's-sake, Souls-clone chrome).
- Neon sci-fi "FUI" / hologram-blue HUDs (wrong theme, and clashes with reserved team colors).
- Glassmorphism, soft gradients, default-rounded cards, indigo/violet accents — the safe-default look
  that prompted this whole effort.

## Hand-off

Once entries are locked, this wall briefs: the `DESIGN.md` token pass, the icon/sigil set, and the
Transmute signature-moment prototype. Promote standouts to a visual contact sheet for at-a-glance use.
