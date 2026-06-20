---
status: final
updated: 2026-06-20
project: Project Chimera
kind: game-ux-experience
design_ref: ./DESIGN.md
sources:
  - Project_Chimera_GDD.md
  - _bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md
  - Snapshot.md
  - _bmad-output/project-context.md
distilled_from:                                            # all UI is local to this run (copied in 2026-06-20; source = ux-Project_Chimera-2026-06-05)
  - ./mockups/project-chimera/project/Shell.html           # title / mode / lobby / settings
  - ./mockups/project-chimera/project/Creation Suite.html  # editor shell + panels
  - ./mockups/project-chimera/project/HUD.html
  - godot/src/Core/MainScene.cs                            # the AS-BUILT in-game HUD (authoritative)
  - ./mockups/project-chimera/project/{tech-tree-editor,hero-picker,custom-ui-builder}.html  # this run's gap surfaces
---

# Project Chimera — EXPERIENCE.md

> Behavioral spine. References `DESIGN.md` tokens by `{path.to.token}`. Owns *how it works*;
> `DESIGN.md` owns *how it looks*. Both spines win on conflict with any mock. Distilled 2026-06-20
> from the shipped Claude Design screens + the as-built Godot HUD — see `.decision-log.md`.

## Foundation

- **Form factor:** PC desktop only (Windows primary; Linux for dedicated/headless servers). **No web, mobile, console, or VR.** Target displays 1080p / 1440p / 4K.
- **Input:** keyboard + mouse (RTS-native). No gamepad. Remappable (see Input Schemes / Accessibility).
- **UI system:** Godot 4.6.2 **Control nodes** at runtime; visual identity per `DESIGN.md` (mapped to a Godot `Theme` resource). The faceted look is `clip-path`-style chamfer in mocks → faceted `StyleBox` in Godot (not `corner_radius`).
- **The dual-user model (the spine's organizing principle):** one app serves two people — the **Commander** (plays matches) and the **Creator** (authors content). Creation surfaces are **opt-in and invisible** to a Commander who never enters them (NFR-3). The hinge between the two is the **Edit ↔ Play** toggle.

## Information Architecture

**Top-level (the Shell — `Shell.html`):**

1. **Title Screen** — grand Chimera seal over a low-poly vista; nav: **Play · Create · Browse · Settings · Quit**; footer carries version/build + patch-notes card. Tagline: *"Build the game. Then play it."*
2. **Mode Select** ("Where to, Commander?") — **Skirmish vs AI** (featured, 1–8 players, offline), **Multiplayer** (ranked / LAN / private + live online count), **Campaign & Tutorial** (progress N/12), **Create** (→ Creation Suite), **My Content** (drafts / published / subscriptions). Header carries breadcrumb + account chip (name, level, MMR) + Settings.
3. **Lobby / Matchmaking** — scenario header + **version-match hash check** (determinism gate, FR-39/40), player **slots** (host / peer / AI; faction select; colorblind-safe color dots with glyphs; ready pills; ping), lobby **chat** (All / Team), footer (`X of Y ready`, "All content synced", Toggle Ready, **Start Match** disabled until all ready).
4. **In-game** — the HUD (below) over the live 3D match.

**Creator branch (the Creation Suite — `Creation Suite.html`):** a single editor shell hosting all authoring surfaces:
- **Top toolbar:** project brand, prominent **Edit/Play** toggle, tool tabs (Terrain · Entities · Resources · Triggers · Win Cond. · Factions · AI Gen), undo/redo, **Save**, **Publish**.
- **Left palette:** tool selector (Select · Terrain · Entities · Resources · Triggers · **Ability** · **Faction Definer** · **Tech Tree** · AI Generate) with hotkey tooltips.
- **Center:** the 3D world (place/sculpt/select) — or, for graph tools, a canvas (Trigger node-graph, **Tech-Tree graph**).
- **Right dock:** the active editor panel (Unit Card Editor, Ability Editor, Trigger ECA, Faction wizard, **Tech-node inspector**, **custom-UI widget inspector**) with a **Simple / Advanced** disclosure toggle.

**Cross-cutting:** **Content Browser** (mod.io — browse/search/tag/sort/subscribe/rate, FR-37) and **Settings** overlay (Gameplay / Graphics / Audio / Controls / Accessibility) reachable from both branches.

**HUD information hierarchy (as-built, `MainScene.cs`):**
top-left **status line** (FPS · mode · tick · sim-hash) → **unit counts** → **resource strip** (per-faction ore + supply, node/building counts, all mono tabular) → context **controls strip** (bottom, changes by Edit/Play) → **minimap** (bottom-right, fog + unit dots) → **command card** (selected building/unit) → **selection** (ring + HP). The **stall banner** (top-center) appears only when a multiplayer peer is behind.

## Voice and Tone

- Address the player as **"Commander."** Confident, terse, mechanical — like the readouts.
- Microcopy is short and concrete: button verbs (**Deploy · Publish · Rebind · Generate**), mono status (`Version match · #a3f9c1e`, `All content synced`). Brand line: *"Build the game. Then play it."* Ownership is stated plainly: *"you own what you make."*
- Tooltips teach, never scold (the `f-tip` pattern): bolded term + one plain-language sentence (e.g. *"Attack Range — 0 = melee. Tiles the unit can strike from."*).

## Component Patterns (behavioral)

Visual specs live in `DESIGN.md.Components`; behavior here.
- **Tooltip (`{components.tooltip}` / `.f-tip`):** hover-reveal on **every** field, button, and panel (NFR-2). Appears after a short hover, dismisses on leave; keyboard-focus also reveals it.
- **Simple / Advanced disclosure:** the dock's `segment` toggles `.is-advanced` — Advanced reveals extra fields (`.adv-only`), slider min/max, and the **raw-JSON escape hatch**. Simple is the default; the expert path is one click away (progressive disclosure pillar).
- **Readouts** update live from sim arrays each frame; mono tabular-nums so digits don't jitter.
- **List-row / card selection:** single-select with `is-selected` accent ring; locked/prereq rows dim to 0.6 and are non-interactive.
- **Switch** (hero promote, settings) flips a boolean and reveals dependent fields inline (e.g. Promote-to-Hero → leveling fields).
- **Dialogs** (`{components.dialog}`) trap focus, dim the scrim, and require explicit confirm for destructive acts (overwrite/delete).

## State Patterns

- **Edit ↔ Play (the central duality).** F5 toggles. Edit shows authoring chrome (palettes, docks, win-condition panel); Play hides all of it and runs the sim. The transition is **instant** — no build/export (NFR-1). Returning to Edit resets match state.
- **Construction / loading / "working":** the **transmute spinner** (`{components.spinner}`) — building construction bars, AI-generation ("Transmuting…"), scenario loads.
- **Validation:** `valid` / `bound` badges on editor surfaces; inline errors block save/playtest (FR-7). Tech nodes show `locked` until prereqs are met.
- **Multiplayer:** lobby `ready` / `not-ready` / `AI`; **version-match** (ok / mismatch) and **content-synced** states gate Start; the in-game **stall banner** signals a lagging peer; a **desync** halts with a clear message.

## HUD & Diegetic UI

- **Non-diegetic** flat overlay (per `DESIGN.md`). Nothing is rendered in-world; the HUD floats above the 3D battlefield.
- **Shown during Play:** status line, resource strip, minimap, command card, selection feedback, context controls strip, transient combat feedback (hit flashes, kill markers) and camera shake.
- **Hidden during Play:** all editor chrome (palette, dock, win-condition panel, tool toolbar). **Hidden during Edit:** the live resource/selection HUD where it would mislead.
- **NFR-3 — editor invisible to Commanders:** a player who only ever uses Play / Skirmish / Multiplayer never sees an authoring surface. Creation lives behind **Create** / the Edit toggle, opt-in.

## Input Schemes

Keyboard + mouse, RTS-native. **Hotkey glyphs** (`{components.kbd}`) annotate every shortcut on-screen and in tooltips. All bindings **remappable** (Settings → Controls; FR-51).

| Group | Default bindings (as-built + Shell settings) |
|------|------|
| Camera | `W/A/S/D` pan · scroll zoom · MMB orbit/tilt · `Space` center base · edge-scroll (toggle `E`) |
| Mode | **`F5` Edit ↔ Play** |
| Commands | `M` Move · **`Q`** Attack-Move *(A conflicts with pan-left; honored over the Shell mock's `A`)* · `S` Stop · `H` Hold · `P` Patrol |
| Selection / groups | click · box-select · `Ctrl+1–9` assign · `1–9` recall · `Shift+#` add · `F2` select army |
| Build / editor | `B` build menu · `U` unit · `T` terrain · `G` grid-snap · `N` lobby · `O` maps · `L` triggers · `M` map-gen · `Y` tech tree · `Ctrl+Z/Y` undo/redo |

The **controls strip** is context-sensitive: it shows Edit shortcuts in Edit, command shortcuts in Play, and placement hints during build/placement.

## Interaction Primitives

- **Instant edit→play round-trip (NFR-1):** place a unit, hit Play, it's in the match — Mario-Maker-style, **no restart, no export**. Return with `Esc`/F5.
- **Consolidated single-entity editing (WC3 model):** one entity, one view — the Unit Card Editor holds model, stats, abilities, economy, hero in a single panel; no hunting across windows.
- **Placement:** ghost preview follows cursor, `G` grid-snap; left-click places, right-click/`Esc` cancels.
- **Selection & command:** click/box-select (Player-faction only), right-click move, `Q`+click attack-move; control groups.
- **Graph editing:** drag a node's **out-port → another node** to wire a dependency (Tech-Tree prerequisites; Trigger node graph).
- **Direct-manipulation UI authoring:** drag widgets onto the 16:9 screen canvas, snap to safe-area/anchors, bind to `{variables}` (custom-UI builder).
- **Undo/redo** everywhere in the editor (`Ctrl+Z` / `Ctrl+Y`).

## Game Feel & Juice

- **130ms** mechanical motion, ease `cubic-bezier(0.4,0.1,0.2,1)` ({DESIGN.md} Elevation & Depth). Buttons depress 1px; toggles snap.
- **Combat feedback:** pooled hit-flashes (orange melee / yellow ranged / red splash / white kill) + brief camera shake on kills (as-built `CombatFeedbackBridge`).
- **Construction & generation:** growing progress bar + glow; the transmute spinner for any async "working" state.
- **Honor `prefers-reduced-motion`** — the spinner and shimmer already gate on it; the Godot build must mirror this via the accessibility settings.

## Accessibility Floor (FR-51)

Behavioral; visual contrast lives in `DESIGN.md`.
- **Remappable keys** — full rebinding with reset-to-defaults (Settings → Controls).
- **Colorblind-safe team colors** — Okabe-Ito palette, **meaning never by color alone**: every team also carries a **glyph + label** (`P1 ◆`, `P2 ▲`, …). Optional deuteranopia/protanopia/tritanopia filters.
- **UI scaling 80–150%** — scales all HUD panels & text across 1080p/1440p/4K.
- **Text contrast boost** — guarantees WCAG AA on dark surfaces.
- **Subtitles** (briefings + unit voice) with S/M/L sizing.

## Responsive & Platform

Single form factor (PC desktop). Adaptation is **UI scale + resolution**, not layout reflow. The in-game custom-UI canvas authors against a **16:9 safe-area**. Dedicated/headless server has no UI (detected via `DisplayServer.GetName()=="headless"`).

## Key Flows (named-protagonist journeys)

> Map to PRD §2.5 **UJ-1…UJ-6** — reconcile exact numbering against `prd.md` at finalize. Climax beat in **bold**.

1. **Maya's first scenario (onboarding, NFR-2).** New creator → Create → "Your First Scenario" guided flow → places a base + a few units, sets a win condition → hits Play → **her map runs in under 15 minutes, no manual or JSON touched.**
2. **Kai authors a unit end-to-end (UJ ~Create).** Opens the Unit Card Editor from a template → tunes Combat/Economy sliders (tooltips explain each) → promotes to Hero, picks an ultimate → **hits Play and the retuned unit fights immediately; the raw JSON stayed hidden the whole time.**
3. **Rosa builds a faction + tech tree.** Faction Definer wizard (name/color → roster → buildings & tech → start → AI preset) → opens the **Tech-Tree editor**, drags Barracks→Stables prerequisites → **the faction is instantly selectable in a skirmish.**
4. **The fast loop (UJ-3 — edit→play).** Mid-build, Dev tweaks a trigger, **F5 → Play → sees it live → F5 → Edit**, repeatedly, with no perceptible build step.
5. **Two friends, one LAN match (FR-39/40).** Host picks a scenario; the lobby shows **version-hash match + "all content synced"**; both ready → **300+ ticks with checksums in lockstep, zero desync.**
6. **Lena deploys a hero & plays (FR-7).** From the **hero-picker**, picks a leveled hero → Deploy → plays a skirmish → **her hero gains XP and the profile persists for next time** (server-validated online).

## Gap Surfaces — behavior (this run)

| Surface | Mockup | Behavior summary |
|---------|--------|------------------|
| **Tech-Tree editor** (FR-13/14) | `mockups/project-chimera/project/tech-tree-editor.html` | Tier-laned graph; drag out-port → building to set a prerequisite; node = building (icon, prereqs, unlocks); right-dock inspector edits building stats (with tooltips) + unlock list; **runtime gates production by the graph.** Building defs reuse the Unit-Card pattern ("Open full Building Card"). |
| **Hero Save/Load picker** (FR-7d/e) | `mockups/project-chimera/project/hero-picker.html` | Creator-enabled per scenario. Slot cards (portrait, level, XP, signature ability, faction); **Deploy / Overwrite / Delete** with confirm dialogs; multiple heroes per player; "validated server-side for online." |
| **Custom runtime UI builder** (FR-26) | `mockups/project-chimera/project/custom-ui-builder.html` | Widget palette (Label/Counter/Bar/Button/Timer/Image/Panel) → drag onto a 16:9 screen canvas; per-widget inspector: **`{variable}` data binding**, 9-point anchor + offsets, style (font role/size/color), **trigger-driven visibility**; **buttons fire triggers on click.** |

**Existing surfaces (in `Creation Suite.html`, not re-mocked):** Unit Card Editor (FR-2), Ability Editor (FR-8–12), Trigger list+graph (FR-23–28), Faction Definer wizard (FR-17). **Extensions to log for architecture/epics:** Ability Editor **passive** path (FR-9); deeper Trigger DSL — typed/scoped variables, arithmetic/boolean expressions, collections, loops, custom events (FR-24/25).

## NFR-1/2/3 — Acceptance Criteria (made testable)

**NFR-1 — Fast edit→play loop.**
- AC1: Toggling Edit→Play (or back) reflects the current edit in the running match with **no application restart** and **no export step**.
- AC2: On a representative scenario, the round-trip completes in **≤ 2 s** wall-clock on target hardware.
- AC3: A unit/stat/trigger edited in Edit is observably changed on the **next** Play without reload.

**NFR-2 — Creator experience / discoverability.**
- AC1: **Every** interactive control (field, button, panel, tool) exposes a hover/focus tooltip with a plain-language explanation.
- AC2: A **"Your First Scenario"** guided onboarding exists and is offered to first-time creators.
- AC3: A first-time creator can produce a **basic playable scenario in < 15 minutes** (measured in playtest).
- AC4: Both **Simple** and **Advanced** modes are reachable on every authoring surface (progressive disclosure).

**NFR-3 — Editor invisible to Commanders.**
- AC1: A user who only uses Play / Skirmish / Multiplayer **never** sees an authoring surface (palette, dock, editor toolbar).
- AC2: All creation entry points are **opt-in** (Create, or the Edit toggle); none appear in the player HUD.
- AC3: No authoring control is reachable by accident from the in-match HUD.
