# Design Handoff Prompt — Project Chimera UI (combined, single-prompt)

> One-shot brief for Claude Design / any single-prompt design tool. Covers the full visual identity
> plus all four surfaces. Save the output back into this run folder; we reconcile it into the
> `DESIGN.md` spine and author `EXPERIENCE.md` afterward.

---

Design the complete UI for **Project Chimera**, a PC-desktop **real-time-strategy (RTS) creation
platform** — it is both a polished RTS game and a Warcraft III World Editor–class tool for building
custom games without writing code. It ships as a premium one-time-purchase title on Steam. I need a
cohesive visual identity plus the key screens below.

## Platform & canvas
- Desktop only (Windows primary). Designed at **1920×1080, 16:9**, scaling cleanly to 1440p/4K with a
  UI-scale setting.
- Input is **keyboard + mouse** — design hover states, right-click context, drag, and show hotkey
  hints/glyphs. No touch, no controller, no mobile, no web.
- Rendered with **Godot 4.6 Control nodes**, so favor layouts and components that map to a standard
  retained-mode UI toolkit (panels, containers, buttons, sliders, dropdowns, tab bars, grids).

## Visual identity (hold these firm)
- **Dark theme primary.** Dark, slightly desaturated panel surfaces; high-contrast light text; a
  single vivid **accent color you propose** for interactive / selected / highlight states. Offer
  2–3 accent options. An optional light variant is secondary.
- **Stylized low-poly echo.** The UI chrome mirrors a low-poly, cel-shaded 3D art style: **faceted /
  angular panel shapes** (subtle chamfered or beveled corners, not pill-round), **flat color blocks**
  with crisp edges, **geometric / flat iconography**, and a thin bright edge-light on raised elements
  to suggest cel-shading. Clean and legible first, faceted character second. The reference feel sits
  between **Mindustry** (utilitarian schematic clarity) and **Northgard** (warm, hand-crafted) —
  lean toward Mindustry's clarity.
- **Non-diegetic overlay.** In-game UI is flat panels over a 3D world, not in-world surfaces, and
  must stay readable on top of a busy, zoomed-out 3D battlefield.
- **Faction colors are reserved.** Team identity uses saturated team colors (e.g. blue vs. red) on
  units in the world; the UI accent and chrome must be clearly distinct and never read as a team
  color. Keep faction colors colorblind-safe wherever they appear in UI (player names, minimap dots,
  ownership tags).
- **Typography:** a clean, slightly geometric sans-serif for UI text; a tighter or monospace
  companion for numeric readouts (resources, timers, stats) to reinforce a precise, data-driven feel.
- **Microcopy tone:** confident, concise, builder-friendly — a tool that empowers makers.
- **Accessibility:** colorblind-safe team colors, UI scaling, text contrast meeting WCAG AA on dark,
  and never encode meaning in color alone (pair with icon/label).

## Reusable component kit (establish first, reuse across all screens)
Faceted **panel/card**, **button** (primary / secondary / ghost + disabled), **icon button**,
**slider with numeric input**, **dropdown**, **tab bar**, **tooltip**, **resource/stat readout
chip**, **progress bar**, **list row**, **modal/dialog**, **toast/alert banner**. Dark theme,
low-poly-echo styling, one accent, with hover and selected states.

---

## Screen 1 — In-game RTS HUD (most-seen surface)
A non-diegetic flat overlay on a 3D battlefield (angled top-down camera, up to ~2000 units on screen).
- **Top bar:** two resource counters — **Ore** and **Crystal** (icon + amount each) — a
  **supply/population** readout (e.g. 12/20), and a game clock. Numeric, monospace-ish, unobtrusive.
- **Bottom-left — Selection & Command card:** on unit selection, show portrait(s) and a **command
  card** grid (Move, Attack-Move, Stop, Hold, Patrol, plus unit abilities), each with a **hotkey
  glyph** in the corner. When a production building is selected, the card lists its **trainable
  units** with cost, build-time, a training **progress bar**, and a `[need: Barracks]`-style
  **prerequisite tag** on locked items.
- **Bottom-right — Minimap:** square minimap with fog-of-war (unexplored / explored / visible),
  team-colored unit dots, building markers, and a camera viewport rectangle.
- **Alerts:** a transient **toast/banner** area (e.g. "Under attack!") and a centered amber
  "Waiting for peer…" multiplayer-stall banner.
- Keep the **center clear** for the battlefield; show a selected-units count and control-group tabs (1–9).
- **Also:** a victory/defeat + score-summary card (kills / units built / ore mined, match duration)
  and a pause/in-game menu, in the same style.

## Screen 2 — Creation Suite shell + Unit Card Editor (headline 1.0 differentiator)
The in-app game-creation editor — a WC3 World Editor–class tool inside the same app as the game,
powerful but approachable, with an **EDIT ↔ PLAY toggle** for instant playtesting.
- **Editor shell:** top toolbar with the prominent **EDIT / PLAY toggle**, tool groups (Terrain,
  Entities, Resources, Triggers/Rules, Win Conditions, AI-generate), undo/redo, save/publish; a
  dockable tool palette; the 3D world fills the center. Dark, faceted, dockable panels.
- **Hero panel — the consolidated Unit Card Editor:** the signature screen. ONE panel showing ALL of
  a single unit's data (the WC3 "one entity, one view" model — explicitly NOT scattered across tabs).
  Left: a **live rotating 3D model preview** with change-model/icon buttons. Right: grouped sections —
  **Combat** (HP, Attack, Range, Armor, Speed as sliders with numeric input + min/max), **Economy**
  (costs, build time), **Abilities** (chips/list, add from a library), and a **Hero toggle** that
  reveals leveling / XP / ultimate fields. Include a **template picker** ("Start from Footman /
  Archer / Worker"), a **compare-to-unit** side-by-side stat view, and a green **validation badge**
  when complete. Every field has a **hover tooltip**.
- **Progressive disclosure:** a **Simple ↔ Advanced** toggle — Simple shows presets/dropdowns/
  sliders; Advanced reveals every field plus a **raw JSON** escape hatch. Show both states.
- **Also (same style):** Trigger/Rules editor (Event→Condition→Action list view + a node-graph view
  toggle); Ability editor (compose effect primitives); Faction Definer wizard (5 steps:
  name/color → unit roster → buildings/tech tree → starting conditions → AI preset); AI generation
  panel (type a prompt → editable result preview); Terrain brush panel.

## Screen 3 — Shell & menus
The out-of-match shell, dark low-poly-echo theme.
- **Title screen:** logo, primary nav (Play, Create, Browse, Settings, Quit), version, a stylized
  low-poly background — confident and premium.
- **Main menu / mode select:** Skirmish (vs AI), Multiplayer (matchmaking + LAN/lobby),
  Campaign/Tutorial, Create (opens editor), My Content.
- **Settings:** tabbed (Gameplay, Graphics, Audio, **Controls / remappable keybindings**,
  Accessibility — UI scale, colorblind-safe team colors, subtitles).
- **Lobby / matchmaking:** host/join, player slots with faction + color pick, ready states, chat,
  scenario selection with a content-hash "version match" indicator.

## Screen 4 — Content browser (Share / Discover loop)
The in-app place to discover community scenarios and publish your own — Steam-Workshop-like but
in-app and dark/low-poly-echo themed.
- **Browse / grid:** scrollable scenario cards (16:9 thumbnail, title, star rating, subscriber
  count, tags); search bar + **filter sidebar** (Game Mode, Player Count, Map Size, Theme,
  Difficulty) + **sort** dropdown (Popular / Top Rated / Newest); a "Featured" rail.
- **Detail page:** large preview/screenshots, description, tags, rating, player-count range,
  **Subscribe / Play Now** button, creator link, version info, report button; a "creator owns their
  content" note surfaced at publish time.
- **Publish flow:** package-as-`.chimera.zip` step, metadata (name, summary >100 chars, tags,
  thumbnail, screenshots), and a **proof-of-play gate** ("Win your own scenario to publish") with a
  visible locked-until-passed state.
- **Creator profile:** avatar, published-scenario grid, total subscribers.

---

Deliver a cohesive design system (color tokens for the dark theme + accent options, typography
scale, the faceted component kit) and high-fidelity mockups of the screens above, all sharing one
consistent low-poly-echo identity.
