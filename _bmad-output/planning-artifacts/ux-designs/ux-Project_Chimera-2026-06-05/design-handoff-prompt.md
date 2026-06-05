# Design Handoff Prompt — Project Chimera UI

**Producer:** Google Stitch (https://stitch.withgoogle.com) — emits a DESIGN.md + per-screen HTML.
**How to use:** Paste **Block 0 (Style System)** first to establish the look, then paste each
**Screen Block** in turn (or one per Stitch project). Save everything Stitch returns into this run
folder (`ux-Project_Chimera-2026-06-05/`) — DESIGN.md output, HTML mocks, anything else. We then run
an Update pass to reconcile the producer output into the `DESIGN.md` spine and author `EXPERIENCE.md`.

> Confirmed directions (from `.decision-log.md`, do not let the tool override): **non-diegetic flat
> overlay HUD · stylized low-poly-echo aesthetic · dark theme primary · faction/team colors reserved
> for in-world units only.** Target: PC desktop, 16:9, keyboard + mouse, rendered with Godot 4.6
> Control nodes.

---

## Block 0 — Style System (paste first)

> You are designing the UI for **Project Chimera**, a PC-desktop **real-time-strategy (RTS) creation
> platform** — it is both a polished RTS game and a Warcraft III World Editor–class tool for building
> custom games without writing code. It ships as a premium title on Steam.
>
> **Platform & canvas:** Desktop only (Windows primary). 16:9, designed at 1920×1080, must scale
> cleanly to 1440p/4K and support a UI-scale setting. Input is **keyboard + mouse** — design for
> hover states, right-click context, drag, and hotkey hints. No touch, no controller, no mobile.
>
> **Visual identity (hold these firm):**
> - **Dark theme primary.** Dark, slightly desaturated panel surfaces; high-contrast light text;
>   one vivid **accent color you propose** for interactive/selected/highlight states. Offer 2–3
>   accent options. Optional light variant is secondary.
> - **Stylized low-poly echo.** UI chrome mirrors a low-poly, cel-shaded 3D art style: **faceted /
>   angular panel shapes** (subtle chamfered or beveled corners, not pill-round), **flat color
>   blocks** with crisp edges, **geometric / flat iconography**, a thin bright edge-light on raised
>   elements to suggest cel-shading. Clean and legible first; faceted character second. Reference
>   feel sits between **Mindustry** (utilitarian schematic clarity) and **Northgard** (warm,
>   hand-crafted) — lean toward Mindustry's clarity.
> - **Non-diegetic overlay.** UI is flat panels over the game world, not in-world surfaces. It must
>   stay readable on top of a busy, zoomed-out 3D battlefield.
> - **Faction colors are reserved.** Team identity uses saturated team colors (e.g. blue vs. red) on
>   units in the world — your UI accent and chrome must be clearly distinct from these and never
>   read as a team color. Keep faction colors colorblind-safe where they appear in UI (player names,
>   minimap dots, ownership tags).
>
> **Typography:** propose a clean, slightly geometric sans-serif for UI text; a tighter/monospace
> companion is welcome for numeric readouts (resources, timers, stats) to reinforce the precise,
> data-driven engine feel.
>
> **Tone of microcopy:** confident, concise, builder-friendly. This is a tool that empowers makers.
>
> **Accessibility floor:** colorblind-safe team colors, support UI scaling, ensure text contrast
> meets WCAG AA on the dark theme, never encode meaning in color alone (pair with icon/label).
>
> Produce a reusable component kit: faceted **panel/card**, **button** (primary/secondary/ghost +
> disabled), **icon button**, **slider with numeric input**, **dropdown**, **tab bar**, **tooltip**,
> **resource/stat readout chip**, **progress bar**, **list row**, **modal/dialog**, **toast/alert
> banner**. Dark theme, low-poly-echo styling, one accent.

---

## Screen Block 1 — In-game RTS HUD _(most-seen surface)_

> Design the **in-game match HUD** for the RTS, as a non-diegetic flat overlay on a 3D battlefield.
> Real-time strategy, top-down-ish angled camera, 500–2000 units possible on screen. Lay out:
>
> - **Top bar:** per-resource counters (two resources: **Ore** and **Crystal**, each with icon +
>   amount), a **supply/population** readout (e.g. 12/20), and a game clock. Numeric, monospace-ish,
>   unobtrusive.
> - **Bottom-left — Selection & Command card:** when units are selected, show selected-unit
>   portrait(s) and a **command card** grid of action buttons (Move, Attack-Move, Stop, Hold,
>   Patrol, and unit abilities), each with a **hotkey glyph** in the corner. When a production
>   building is selected, the card shows its **trainable units** with cost, build-time, a training
>   **progress bar**, and a `[need: Barracks]` style **prerequisite tag** on locked items.
> - **Bottom-right — Minimap:** square minimap with fog-of-war (3 states: unexplored/explored/
>   visible), colored unit dots (team colors), building markers, and a camera viewport rectangle.
> - **Alerts:** a transient **toast/banner** area (e.g. "Under attack!", and a centered amber
>   "Waiting for peer…" multiplayer-stall banner).
> - Keep the **center of the screen clear** for the battlefield. Show a selected-units count and
>   control-group tabs (1–9).
>
> Provide hover and selected states. Dark, faceted, low-poly-echo chrome; reserve saturated
> blue/red for team identity only.

**Secondary screens in this surface (same style, do after the hero screen):** victory/defeat +
score-summary card (kills / units built / ore mined, match duration); pause/in-game menu.

---

## Screen Block 2 — Creation Suite shell + Unit Card Editor _(headline 1.0 differentiator)_

> Design the **in-app game-creation editor** — this is a Warcraft III World Editor–class tool that
> lives inside the same app as the game. It must feel powerful but approachable, with an
> **edit ↔ play toggle** that lets the creator jump into playtesting instantly.
>
> **Editor shell:** a top toolbar with the **EDIT / PLAY mode toggle** (prominent), tool groups
> (Terrain, Entities, Resources, Triggers/Rules, Win Conditions, AI-generate), undo/redo, and
> save/publish. A left or right **dockable tool palette**. The 3D world fills the center. Panels are
> dockable, dark, faceted.
>
> **Hero panel — the consolidated Unit Card Editor:** the signature screen. ONE panel showing ALL
> of a single unit's data (the WC3 "one entity, one view" model — explicitly NOT scattered across
> tabs). Left side: a **live rotating 3D model preview** with buttons to change model/icon. Right
> side: grouped sections — **Combat** (HP, Attack, Range, Armor, Speed as sliders with numeric
> input, each showing min/max), **Economy** (costs, build time), **Abilities** (chips/list of
> attached abilities, add from a library), and a **Hero toggle** that reveals leveling/XP/ultimate
> fields. A **template picker** ("Start from Footman / Archer / Worker"), a **compare-to-unit**
> side-by-side stat view, and a green **validation badge** when the unit is complete. Every field
> has a **hover tooltip**.
>
> **Progressive disclosure:** a **Simple ↔ Advanced** toggle — Simple shows presets/dropdowns/
> sliders; Advanced reveals every field and a **raw JSON** escape hatch. Show both states.
>
> Dark theme, low-poly-echo, accent for the active tool and the selected field.

**Secondary screens in this surface:** Trigger/Rules editor (Event→Condition→Action list view +
a node-graph view toggle for advanced); Ability editor (compose effect primitives); Faction Definer
wizard (5 steps: name/color → unit roster → buildings/tech tree → starting conditions → AI preset);
AI generation panel (type a prompt → editable result preview); Terrain brush panel.

---

## Screen Block 3 — Shell & Menus

> Design the **out-of-match shell** for the RTS creation platform, dark low-poly-echo theme.
>
> - **Title screen:** game logo, primary nav (Play, Create, Browse, Settings, Quit), version, a
>   stylized low-poly background. Confident, premium, single-player-game-first.
> - **Main menu / mode select:** entries for Skirmish (vs AI), Multiplayer (matchmaking + LAN/lobby),
>   Campaign/Tutorial, Create (opens editor), My Content.
> - **Settings:** tabbed (Gameplay, Graphics, Audio, **Controls/remappable keybindings**,
>   Accessibility — UI scale, colorblind-safe team colors, subtitles).
> - **Lobby / matchmaking:** host/join, player slots with faction + color pick, ready states,
>   chat, scenario selection with a content-hash "version match" indicator.
>
> Provide the reusable nav, list-row, tab, and form patterns. Accent for primary actions.

---

## Screen Block 4 — Content Browser _(Share / Discover loop)_

> Design the **in-app content browser** where players discover community-made scenarios and creators
> publish them. Steam-Workshop-like but in-app and dark/low-poly-echo themed.
>
> - **Browse / grid:** scrollable card grid of scenarios — each card: 16:9 thumbnail, title, star
>   rating, subscriber count, tags. A search bar + **filter sidebar** (Game Mode, Player Count, Map
>   Size, Theme, Difficulty) + **sort** dropdown (Popular / Top Rated / Newest). A "Featured" rail.
> - **Detail page:** large preview/screenshots, description, tags, rating, player-count range,
>   **Subscribe / Play Now** button, creator link, version info, report button. Surface an
>   **"creator owns their content"** note at publish time.
> - **Publish flow:** package-as-`.chimera.zip` step, metadata (name, summary >100 chars, tags,
>   thumbnail, screenshots), and a **proof-of-play gate** ("Win your own scenario to publish") —
>   show the gate state (locked until passed).
> - **Creator profile:** avatar, published-scenario grid, total subscribers.
>
> Reuse the card, filter, list-row, and modal patterns. Accent for the primary subscribe/publish CTA.

---

## After Stitch returns

1. Save all outputs (DESIGN.md, per-screen HTML) into this folder.
2. Run **`/gds-ux`** again in **Update** mode (or just say "reconcile the Stitch output") — I will:
   - reconcile the producer's DESIGN.md into the `DESIGN.md` spine (tokens + components),
   - flag anything that conflicts with the confirmed directions (spine wins),
   - author the `EXPERIENCE.md` behavioral spine (IA, HUD behavior, input scheme, game feel,
     accessibility, the six named player journeys UJ-1…UJ-6),
   - optionally render key-screen mocks for any surface the producer didn't cover.
