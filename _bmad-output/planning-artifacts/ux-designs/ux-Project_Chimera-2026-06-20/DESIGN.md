---
status: final
updated: 2026-06-20
project: Project Chimera
kind: game-ux-design
distilled_from: ../ux-Project_Chimera-2026-06-05/mockups/project-chimera/project/chimera.css
sources:
  - Project_Chimera_GDD.md
  - _bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md
  - Snapshot.md
# ── TOKENS (canonical; mirror chimera.css :root). Godot Theme-mappable. ──
colors:
  # surfaces — cool, slightly desaturated dark
  void: "#0a0c0f"          # deepest; battlefield bleed behind UI
  surface-0: "#0f1216"     # app base
  surface-1: "#14181d"     # primary panel
  surface-2: "#1a1f26"     # raised panel / card
  surface-3: "#222831"     # inset / control track
  surface-4: "#2c333d"     # hover raise
  # lines & edges
  line: "#2a3038"          # hairline divider
  line-strong: "#3a424d"   # panel border
  edge-light: "#4a5562"    # cel-shade top-edge highlight base
  # text — WCAG AA on surface-1
  text-hi: "#eef2f6"       # headings / key values (~14:1)
  text-mid: "#aeb7c2"      # body / labels (~7:1)
  text-lo: "#727c88"       # meta / hints (~4.6:1)
  text-disabled: "#4b545f"
  # accent — TEAL default (overridable: amber, violet)
  accent: "oklch(0.78 0.13 192)"
  accent-bright: "oklch(0.86 0.13 192)"
  accent-dim: "oklch(0.62 0.10 192)"
  accent-ink: "#04201e"            # text on accent fill
  accent-glow: "oklch(0.78 0.13 192 / 0.28)"
  accent-wash: "oklch(0.78 0.13 192 / 0.12)"
  # semantic — NEVER color-alone; always paired with icon/label
  ok: "oklch(0.78 0.16 145)"
  warn: "oklch(0.80 0.15 80)"
  danger: "oklch(0.66 0.19 25)"
  info: "oklch(0.74 0.11 240)"
  # faction / team — Okabe-Ito derived, colorblind-safe, RESERVED for world units
  team-1: "#2a7fd4"   # blue
  team-2: "#e06a1b"   # vermilion
  team-3: "#16a37a"   # bluish green
  team-4: "#cf72ad"   # magenta
  team-5: "#5cb8ec"   # sky blue
  team-6: "#f0c000"   # yellow
  team-7: "#9a6cf0"   # purple
  team-8: "#9aa3ad"   # neutral gray
typography:
  font-display: "Chakra Petch"          # headings, buttons, labels, tabs (uppercase + tracking)
  font-ui: "Space Grotesk"              # body / UI default
  font-mono: "JetBrains Mono"           # numbers, readouts, kbd (tabular-nums)
  scale-ratio: "1.250"
  t-2xs: "11px"
  t-xs: "12px"
  t-sm: "13px"
  t-md: "15px"   # body default
  t-lg: "18px"
  t-xl: "23px"
  t-2xl: "29px"
  t-3xl: "37px"
  t-4xl: "52px"
  t-5xl: "72px"
rounded:
  # NOT border-radius — the low-poly echo uses CHAMFERED corners via clip-path polygons.
  # Effective border-radius is ~0. These are the diagonal cut sizes.
  cut: "8px"       # default panel/card
  cut-sm: "5px"    # buttons, inputs, chips
  cut-lg: "14px"   # dialogs / large surfaces
spacing:
  s1: "4px"
  s2: "8px"
  s3: "12px"
  s4: "16px"
  s5: "24px"
  s6: "32px"
  s7: "48px"
  s8: "64px"
components:
  panel: "faceted surface-1, cel-shade hairline border (edge-light→line gradient), shadow-1; --2 raised, --flat no shadow, --accent accent border"
  btn: "chamfered (cut-sm), Chakra Petch uppercase 13px/600, +0.04em; variants: primary (accent fill), secondary (outline), ghost, danger; sizes sm/lg/block; active translateY(1px)"
  icon-btn: "36×36 faceted; is-active = accent fill; 18px glyph"
  kbd: "JetBrains Mono 11px/700, surface-3, 2px bottom-border, radius 3px — the only radiused element (physical-key affordance)"
  chip: "faceted readout, surface-2, inset line; holds a .num value"
  readout: "top-bar resource style — 22px faceted icon + mono 18px tabular value + 11px uppercase label"
  tag: "uppercase pill (cut 3px); --lock warn, --ok ok, --accent, --danger"
  progress: "8px track, accent gradient fill + glow; --ok green, --xp striped"
  slider: "6px track, faceted accent thumb (14×18); paired .num-input (mono, right-aligned)"
  input: "surface-3, inset line, chamfered; focus = accent ring + wash; .select with chevron; .field label = uppercase 11px"
  menu: "popover surface-2, shadow-pop; menu-item hover surface-4, is-active accent"
  tabs: "underline (accent + glow) or --boxed; segment = inline pill group, is-active accent fill"
  list-row: "surface-1 inset, chamfered; is-selected accent ring + wash; is-locked 0.6 opacity"
  tooltip: "tip__pop above, surface-3, shadow-pop; <b> = accent — used for the NFR-2 tooltip-on-every-control mandate"
  dialog: "scrim (blur) + centered faceted dialog (cut-lg), head/body/foot; gradient surface-2→1"
  toast: "faceted, left accent bar; --danger/--warn/--ok; banner-stall = warn pill for the multiplayer stall indicator"
  spinner: "transmute spinner (.tmute) — 3 layered SVGs (ring cw / triangle ccw / pulsing core); sm 22 / default 48 / lg 96"
  mark: "Chimera Seal — alchemical sigil (two rings + fire/water triangles + nucleus + 3 vertex nodes); .triad heavy-stroke variant for ≤24px"
---

# Project Chimera — DESIGN.md

> Visual-identity spine. Tokens above mirror `chimera.css :root` (the shipped Claude Design system);
> this file owns *how it looks*. `EXPERIENCE.md` owns *how it works* and references these tokens by
> `{path.to.token}`. Both spines win on conflict with any mock or import. Distilled 2026-06-20 from
> the shipped UI — see `.decision-log.md` D1/D2.

## Brand & Style

Project Chimera is an **RTS creation platform** — a game *and* a creation tool in one. The UI serves
two users on one visual system: a **player** in a fast match (glanceable, reads at zoomed-out camera
distance) and a **creator** in a long authoring session (dense, comfortable, editor-grade).

- **Low-poly echo.** UI chrome echoes the faceted low-poly 3D art: **chamfered (clipped) corners**, a
  thin **cel-shade top-edge highlight**, flat color blocks, geometric icons — UI and world read as one
  fabricated object. 3D art reference band: Mindustry (utilitarian clarity) ↔ Northgard (painterly
  warmth); the UI sits at the clarity end while keeping the hand-made faceted character.
- **Non-diegetic HUD.** Honest flat UI floating over the 3D world (SC2 / classic-RTS), clarity over
  in-world illusion.
- **Dark primary.** Cool desaturated-dark panels by default (long-session eye comfort, editor
  convention), light text, a single accent for interactive/highlight states. A warm-paper **light
  theme** is a first-class peer.
- **Faction colors are sacred.** Team identity lives on world units in 8 reserved colorblind-safe
  hues; UI accents must never compete with or be mistaken for them.
- **Alchemical mark, lightly.** The **Chimera Seal** (transmutation sigil) and **transmute spinner**
  give the system a distinct signature. This is the *only* alchemy that ships — the full bio-alchemy
  "Transmutation Lab" retheme was **shelved** (see `.decision-log.md` D3).

## Colors

Full palette in frontmatter `colors`. Usage rules:

- **Surfaces stack by elevation:** `{colors.void}` (battlefield bleed) → `surface-0` (app) → `1`
  (panel) → `2` (card/raised) → `3` (control track/inset) → `4` (hover). Never skip more than one
  level between adjacent elements.
- **Text tiers** `{colors.text-hi}` / `text-mid` / `text-lo` are AA-locked on `surface-1`. Headings &
  key numbers = hi; body = mid; meta/hints = lo. Don't put `text-lo` on `surface-3+`.
- **Accent is interaction, not decoration.** `{colors.accent}` marks the active/primary/selected
  state only. Three runtime accents (`teal` default, `amber`, `violet`) via `[data-accent]`.
- **Semantic colors are never alone** — `{colors.ok}` / `warn` / `danger` always ship with an icon or
  label (colorblind floor). `danger` is reserved for destructive/irreversible.
- **Team colors are out of bounds for chrome.** `{colors.team-1..8}` belong to units/minimap/team
  identity only.

## Typography

- **`{typography.font-display}` (Chakra Petch)** — headings, buttons, tabs, field labels, tags.
  Uppercase with letter-spacing (0.04–0.22em by role); the system's "voice."
- **`{typography.font-ui}` (Space Grotesk)** — body, descriptions, menu items. Sentence case, 1.45
  line-height.
- **`{typography.font-mono}` (JetBrains Mono)** — every number that updates live (resources, supply,
  tick, hash, stats, slider values, hotkeys). **Tabular-nums always** so readouts don't jitter.
- Scale is 1.250, body = `{typography.t-md}` (15px). `.eyebrow` = 11px uppercase 0.22em tracking.

## Layout & Spacing

- 8-pt-ish system: `{spacing.s1}`–`{spacing.s8}` (4→64). Control padding ≈ `s2`/`s3`; panel padding ≈
  `s4`/`s5`; section gaps ≈ `s5`/`s6`.
- HUD anchors to screen edges (corners/strips), leaving the center clear for the battlefield. Editor
  panels dock left/right rails; the 3D viewport stays the protagonist surface.

## Elevation & Depth

Depth = surface step + cel-shade edge + shadow, **not** blur-heavy material.

- `{components.panel}` carries a faceted hairline border (a gradient backplate masked to 1px) — the
  signature edge. Shadows: `shadow-1` (resting panel), `shadow-2` (raised), `shadow-pop` (menus,
  tooltips, dialogs).
- Motion is quick and mechanical: **130ms**, ease `cubic-bezier(0.4,0.1,0.2,1)`. Honor
  `prefers-reduced-motion` (the transmute spinner already gates its animation on it).

## Shapes

- **The chamfer is the brand.** Corners are clipped on a 45° via `clip-path` polygon at
  `{rounded.cut}` (panels), `{rounded.cut-sm}` (controls), `{rounded.cut-lg}` (dialogs). Effective
  border-radius is ~0 everywhere **except `.kbd`** (radius 3px — a deliberate physical-key cue).
- In Godot this maps to a faceted `StyleBoxFlat`/`StyleBoxTexture` (or a small NinePatch/shader) — not
  rounded `corner_radius`. Record as an implementation note for the architecture pass.

## Components

Full kit in frontmatter `components` (1:1 with `chimera.css`). The set is deliberately broad and
editor-grade — buttons, icon-buttons, **hotkey glyphs** (`{components.kbd}`), chips, **resource
readouts** (`{components.readout}`), tags, progress (incl. XP), sliders + numeric inputs, inputs /
selects / dropdown menus, tabs / segmented controls, list rows, **tooltips**, dialogs, toasts, the
**stall banner**, the **transmute spinner**, and the **Chimera Seal** mark. The 7 gap surfaces
(Unit Card Editor, Ability Editor, Tech-Tree editor, Faction Definer wizard, Trigger T2/T3 editors,
hero-picker, custom runtime UI) compose from **this existing kit** — no new primitives unless a gap
surface proves one is missing (log it if so).

## Do's and Don'ts

- **Do** map tokens 1:1 into a Godot `Theme` resource so the implemented HUD and all editor surfaces
  inherit one source of truth.
- **Do** use `{components.kbd}` for every shortcut hint (this is an RTS — hotkeys are everywhere) and
  `{components.tooltip}` on every control (NFR-2 mandate).
- **Do** render all live numbers in `{typography.font-mono}` tabular-nums.
- **Don't** use `{colors.team-1..8}` for any UI chrome, ever.
- **Don't** convey state by color alone — pair `{colors.ok}`/`warn`/`danger` with icon/label.
- **Don't** introduce rounded corners (radius) — the language is chamfered. `.kbd` is the sole, intentional exception.
- **Don't** reintroduce the shelved bio-alchemy retheme; the Chimera Seal mark is the agreed extent of the motif.
