# Handoff: Project Chimera · Game UI (Round 1 + Round 2)

> **Repo note (2026-09-30):** This folder is the only UI mockup of record. It replaces the
> Round 1 export (still in git history). The prompts that produced it are in
> `docs/history/ui-redesign-prompts/`. The four `assets/bf-*.jpg` Manor Lords reference
> screenshots are deliberately left out of the repo, so the HUD boards in `Match.dc.html` show
> no battlefield backdrop. The original export zip keeps them. Fonts: Cinzel, Inter and
> JetBrains Mono for now. Alec may commission a custom font if these look lacklustre in engine.

## Overview
The complete UI for **Project Chimera**, an RTS in which players (**Commanders**) play scenarios of any genre and creators (**Architects**) build them. It covers:
- the front door (title, loading, Home, Play hub, skirmish setup, lobby, settings);
- Browse and publish;
- the in-match HUD, pause, score, spectator and replay screens, and the hero picker;
- every Create editor: map, unit, item, building, ability, rules, tech tree, faction wizard, Game UI builder and import manager.

The visual language is an **engraved alchemical laboratory**: dark slate panels with gold line-work, transmutation geometry, and one bioluminescent accent per screen.

**Target:** native **Unreal Engine UI** (UMG / Common UI). Every effect was designed to be cheap in Unreal: opacity, colour, a mask sliding along a border, a rotating image or a material parameter. There is no blur and there are no particles.

## About the design files
The files in this bundle are **design references built in HTML**. They are prototypes that show the intended look and behaviour; they are **not production code to ship**.

Your job is to **recreate these designs in the target environment**, using its established patterns: UMG widgets, Common UI, 9-slice brushes and widget animations. If a web tool or launcher is ever needed instead, use that codebase's framework. Treat the HTML as the spec for layout, sizes, colours, copy, states and behaviour.

To view the designs, open any `*.dc.html` in a browser. They need `support.js`, `icons.js`, `ornaments.js`, `levers.js` and `assets/` beside them. Pages are large canvases: pan and zoom. Each page starts with an **index board** listing its artboards, and each artboard is labelled **INTERACTIVE** or **STATIC**.

## Fidelity
**High fidelity.** Colours, type, spacing, states, copy and interactions are final for Round 2.

Four parts are placeholders:
- **Battlefield and world backdrops.** The HUD boards use four reference screenshots in `assets/bf-*.jpg`, from Manor Lords, as stand-ins for the near-photoreal terrain. Other boards use painted gradient stand-ins. Replace all of them with in-engine renders.
- **Unit, hero and building portraits** in the specimen vat, which are silhouette placeholders. Use 3D render targets.
- **Scenario thumbnails**, which are gradient placeholders.
- **Board 04 on the design-system page** lists the components added in Round 1 and Batch 1. The components from Batches 2–5 are listed on each file's index board ("New components → board 04") but are not yet drawn on board 04.

## Global rules
- **Artboards:** 1920×1080. **UI scale:** 80–150%. Scale every panel, font and hit target, but not the 3D world. Hit targets are never below 44px at 100%.
- **One shell for every hub and editor screen:**
  - top bar, 56px;
  - left rail, 300–360px;
  - centre, never covered;
  - right inspector, 400–460px;
  - bottom context strip, 40px.

  No floating windows. **Modal dialogs are for destructive confirmations only** (example: 2.3b).
- **The match HUD is the exception.** The battlefield is full-bleed, with a 40px top strip and a bottom console. See *Match HUD* below.
- **Every control** shows its hotkey as a keycap glyph and has a tooltip. Every control needs hover, focus, selected and disabled states.
- **Every editor** has a Simple and an Advanced mode, and most also have a Raw data view.
- **Player colours** appear only inside chips, always paired with a glyph (Okabe-Ito palette, below).
- **Contrast:** WCAG AA for all text.
- **Copy:** an arcane touch is fine, but stat names stay plain. The only role names are **Commander** and **Architect**. Never use "the Law of Equal Exchange". Use "the showcase campaign" or "[World]". The working faction names are **Crucible Covenant** (slate blue) and **Sanguine Court** (oxblood).
- **Motion:**
  - Subtle by default. It never moves text or controls, never flashes, and never loops faster than about every 2.4 s.
  - In a match, motion stays on the edges and alerts, never over the battlefield.
  - **Reduced motion** stops all motion and leaves the ornaments still.
  - Player option **UI ornament: Off / Subtle / Full** sits under Settings › Graphics and Settings › Accessibility.

## Design tokens

### Colour
| Role | Hex |
|---|---|
| Page / void | `#0B0C0E` |
| Surface 0 (sunken, inputs) | `#14161A` |
| Surface 1 (HUD console) | `#1C1F25` |
| Surface 2 (panels) | `#20242B` |
| Surface 3 (focused row) | `#262A31` |
| Surface 4 (hover, selected) | `#2A2F38` |
| Surface 5 (button hover) | `#333945` |
| Hairline | `#2A2F38` |
| Border | `#3A3F48` |
| Border, warm | `#4A4234` |
| Keycap border | `#5C5446` |
| Gold, dark (engraving, panel edge) | `#8A6E3C` |
| Gold (icons, kickers, secondary buttons) | `#C9A86A` |
| Gold, bright (selected, focus ring, titles) | `#E3C887` |
| Text | `#ECE6D8` |
| Text, muted | `#9A958A` |
| Bioluminescent accent (one per screen: AI working, Ready/Start) | `#7FE3A1` |
| Success / verdigris | `#4FB39A` |
| Success fill | `#173129` (hover `#1D3B32`) |
| Danger border | `#C8503C` |
| Danger text | `#E2735F` |
| Danger fill | `#2A1C1A` |

**Player colours (Okabe-Ito), each with its glyph:**
- Blue `#0072B2` ◆, white glyph.
- Vermillion `#D55E00` ▲.
- Sky `#56B4E9` ●.
- Purple `#CC79A7` ■.
- Orange `#E69F00` ✚.
- Yellow `#F0E442` ★.
- Green `#009E73` ⬟, white glyph.

Glyphs are `#14161A` unless noted.

**Rule-chip kinds (Batch 5):**
- event / object `#C9A86A`;
- linked map pick `#4FB39A`, with a pin icon;
- value `#56B4E9`;
- rule variable `#CC79A7`;
- action `#E69F00`.

### Type
- **Cinzel** 500/600/700: display and panel titles.
  - Screen titles: 40–48px.
  - Panel titles: 22–30px.
  - Section headers: 14px, uppercase, letter-spacing .14em, in `#E3C887`.
- **Inter** 400/500/600/700: all UI text.
  - Body: 13–16px, line-height 1.4–1.55.
  - Kicker labels: 11–12px, 600 weight, uppercase, .12–.16em, in `#C9A86A`.
- **JetBrains Mono** 500/600: numbers, stats, versions, keycaps.
  - Keycaps: 10–11px, 600 weight, height 18–20px, padding 0 4–5px, 1px `#5C5446` border with a 2px bottom border, radius 3px, background `#14161A`.

### Shape, spacing and elevation
- **Radius:** 0 everywhere. Panels, buttons and inputs are flat; only keycaps use 2–3px.
- **Spacing steps** (px): 3, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 56, 64.
- **Panel padding:** 22–28px. Row gap: 3–6px. Section gap: 14–22px.
- **Borders:** 1px. Selected items get a `#C9A86A` or `#E3C887` border. Focus is a 2px `#E3C887` outline with an offset of 1–3px.
- **Shadows:** almost none. The top bar has `0 1px 0 #0E0F12`; panels an inset `0 1px 0 rgba(227,200,135,.07)`.

### Buttons
All buttons sit 40–60px tall.
- **Primary gold:** background `#C9A86A`, border `#E3C887`, text `#14161A`. 600 weight, uppercase, .08em. Hover `#E3C887`.
- **Go / Start:** background `#173129`, border `#4FB39A`, text `#ECE6D8`. Carries the pulse accent.
- **Secondary:** background `#2A2F38`, border `#8A6E3C`. Hover: border `#C9A86A`, background `#333945`.
- **Danger:** transparent, border `#C8503C`, text `#E2735F`. Hover `#2A1C1A`.
- **Disabled:** background `#1C1F25`, border `#3A3F48`, text `#9A958A`, cursor not-allowed. The tooltip always says why.

## Ornament and motion system
Defined in `ornaments.js` (Round 1 geometry) and `levers.js` (Round 2 levers).

- **Every lever** has an on/off switch and a level from 1 to 5. **Master intensity** scales them all (level × master ÷ 2). Approved defaults: **Master 5**, **UI ornament Full**, **Border style: Woven rope**.
- **Border styles:** engraved line, double line, woven rope, filigree corners, sigil-studded. In Unreal, each is a 9-slice brush swapped by an enum; intensity is the brush tint alpha.
- **Gold rope:** two gold lines twist round each other. Low levels show only the corners; high levels run the whole edge. Build it as a tiled rope texture on four edge images, with a linear opacity mask.
- **Growth:** the rope and filigree grow in when a panel opens. Material scalar "Reveal" 0→1, keyed in a UMG animation.
- **Sigils:** small transmutation symbols at corners, headers and dividers. Symbol sets: Circles, Triangles, Seals. Build from one atlas with fixed slots.
- **Pulse:** a slow breathing glow on the living accent. Looping opacity animation on a halo image, sine curve, period 2.4–6 s.
- **Shimmer ("laser"):** one bright head runs the whole perimeter (top → right → bottom → left), taking **19–40 s per lap**, then rests. On hover it leaves an **etched gold line** that fades before the next lap. Build it as a panner node moving a gradient mask along the border UVs, plus a Reveal mask for the etch.
- **Turn:** transmutation circles rotate slowly, 32–160 s per turn. Render-transform angle, linear loop.
- **Draw-in:** borders and sigils trace themselves in on appear. Same Reveal scalar, triggered on Construct.
- **Hover:** gold brightens and the rope tightens. OnHovered lerps tint alpha and rope tile scale over 0.18–0.6 s.
- **Seal:** the "complete" array stamps closed once. Scale 1.1–1.5 → 1 with opacity, plus a fading ring.
- **Reduced motion:** set every widget animation's play rate to 0 and Reveal to 1.

Board 05 on `Chimera Design System.dc.html` shows every lever at off, 2 and 5.

## Screens
Every screen is in the files listed at the end. The measurements below are the load-bearing ones; read the rest from the HTML, where every value is inline.

### 00 · Screen map (`Screen Map.dc.html`)
Every screen as a box, with navigation arrows labelled by click and hotkey, the Esc target, and a glyph showing which shell zones it uses. The Match HUD uses its own glyph: full-bleed, top strip and bottom console only.

### Shell top bar (`Shell Top Bar.dc.html`), 56px
Contents, left to right:
1. Logo button.
2. Mode tabs **Play F1 / Create F2 / Browse F3**. The active tab has a 2px bottom `#E3C887` inset and a `#262A31` fill.
3. Title in Cinzel 16px `#E3C887`, over a subtitle in JetBrains Mono 11px.
4. **In Create only:** editor tabs **Map, Objects, Abilities, Rules, Tech tree, Game UI, Factions** on Alt 1–7.
5. Ctrl K command palette: 440px wide, or 220px in Create.
6. Network state (icon only in Create).
7. Profile chip showing the role, Commander or Architect (initials only in Create).
8. Settings.

Where the new content types live:
- **Items and Buildings** share the Objects tab, chosen by a type switcher at the top of the left rail.
- **Factions** is its own tab.
- **Imports** is a manager opened with Ctrl I, from any "Import" button, or by dropping files on the window. It has no tab.

### Batch 1 · Front door (`Front Door.dc.html`)
- **1.1a Title:** logo, "Press any key" (pulse accent), build version, online status.
- **1.1b Loading:**
  - a 440px transmutation-ring progress with the percentage;
  - the map name and chips (genre, players, Architect, version and size);
  - an integrity-check panel, "Verifying 214 files", with three checks;
  - four stage steps and each Commander's load bar;
  - a tip strip with Tab for the next tip and Esc to cancel.
- **1.2a–c Home:**
  - A living 3D world behind the menu.
  - Left menu, 460px:
    - Continue card with thumbnail (the pulse accent), then nine hotkey rows, each 54px: Campaign K, Skirmish S, Multiplayer M, Custom Games G, Create C, Browse B, Heroes H, Settings O, Quit Q.
    - Unavailable rows sit at 50% opacity and say why.
  - Right column, 440px: Recent, Subscribed (with "Update all"), Featured.
  - States:
    - **First launch:** "Build your first map in 15 minutes", no saves, not signed in.
    - **Returning Commander.**
    - **Offline:** Multiplayer and Browse are disabled, and the reason is given.
- **1.3a–e Play hub:** the left rail switches between Campaign, Skirmish, Multiplayer, Custom games and Load & replays.
  - Campaign: three mission cards (done with seal / open / locked), plus a briefing with objectives and difficulty in the inspector.
  - Multiplayer: three queues, and an open-lobby table with ping bars and a content-match column. Join by code.
  - Custom games: filter by genre and player count, with a Play now button.
  - Load & replays: save slots with an AUTOSAVE tag, and a replay list with duration, player chips and result.
- **1.4a–b Skirmish setup:**
  - Map list and a minimap preview with start markers.
  - Two to four slots: colour + glyph, Who (Commander / AI / Open / Closed), AI personality (Warden, Raider, Alchemist, Opportunist), difficulty, faction, team.
  - Rules: resources, fog, speed.
  - A **conflict** (a duplicate colour, or everyone on one team) blocks Start. It shows the reason and a one-click fix.
- **1.5a–d Lobby:**
  - Slots show ready state, ping and content match; there are spectator slots.
  - Chat, host settings (locked for guests) and kick.
  - States: countdown seal, a player missing content ("Update required", with a one-click download), downloading with progress, and host left (hosting migrates).
- **1.6a–d Settings:**
  - Categories in the rail, the form in the centre, and the description plus live preview on the right.
  - **Graphics:** presets with the fps cost per option.
  - **Controls:** separate Play and Create keymaps, key capture, and a conflict row with "Unbind the other".
  - **Accessibility:** UI scale 80–150% with a live preview; colourblind check (Normal / Protan / Deutan / Tritan); subtitles; Reduced motion; UI ornament.
  - **AI providers:** Claude, OpenAI Codex, OpenRouter and a local Ollama model. Each uses your own key and has a connection test. The page states plainly that everything works with AI off.
  - **Gameplay** also has "Show latency and frame rate in a match", off by default.

### Batch 2 · Browse and publish (`Browse.dc.html`)
- **2.1 Browse home:**
  - Content-type tabs: Scenarios, Factions, Units & abilities, Models.
  - Search, genre tags, sort (Trending, Top rated, Newest, Most played).
  - Filters: players, map size S–XL, Has AI opponents.
  - Four-column card grid. Each card shows thumbnail, genre tag, name, Architect, players, rating and a subscribed/update badge.
- **2.2a–e Detail page:**
  - Gallery with five thumbnails, About, reviews, version history.
  - Architect card with their other work.
  - Verified seal, status block and required content.
  - Actions: Subscribe, Play, Open in Create, Report.
  - States: not subscribed, downloading, installed, update available, verification failed ("Repair · 3 MB").
- **2.3a–b My library:** tabs, disk-use bar, per-row update and unsubscribe. The unsubscribe confirmation is a modal whose focus starts on "Keep it".
- **2.4a–d Publish:** a nine-item checklist in the rail; clicking one jumps to its field. States: blocked (not beaten), validation errors, uploading (ring progress plus steps), published (seal plus share code).

### Batch 3 · Match (`Match.dc.html`)
**Match HUD (3.1, 3.2a–g, 3.3a, 3.6a):** a full-bleed battlefield with no frame, of which about 79% stays open.
- **Top strip, 40px, surface `#1C1F25`:**
  - resources and supply chips (28px tall) on the left;
  - clock and game speed centred;
  - alerts count and Menu (F10) on the right;
  - nothing else. Latency and fps are off by default.
- **Bottom console, stepped:**
  - **Minimap panel:** 256×216 at bottom-left. The 184px square map sits on a subtly raised frame plate, 200px, with small gold corner nodes. There are three buttons down its left side.
  - **Selection panel:** 176px tall, spanning between the two side panels (x 256 → 1620).
    - One unit: a 124px vat portrait, name, HP, mana and XP bars, one line of stats.
    - Hero: adds a 3×2 inventory.
    - Building: a five-deep production queue, rally point and research.
    - Big selection: grouped portrait tiles (132×78) plus mini portraits; Tab cycles the active type.
  - **Command card:** 300×216 at bottom-right. A 4×3 grid of 64×60 buttons, each with a keycap, icon, cost, cooldown and progress overlay, and a lock icon for "needs X".
  - Ornament sits only on the console's top edges and corners: a gold top border plus the rope, laser and sigil levers.
- **Control groups:** tabs 30px tall, just above the selection panel, showing only assigned groups. F1 is the hero tab.
- **Overlays fade out; nothing permanent covers the battlefield.** Alerts and chat stack above the minimap (left 12, bottom 228). Under attack and Waiting for player appear top-centre. Pings sit in the world.
- **World overlays:** selection rings are 3px ellipses (height = width × 0.42) in verdigris, or gold for heroes. Health bars are 7px tall with a dark border.
- **Scenario custom-UI zone:** draw nothing unless the map uses it. In 3.6a the kill scoreboard (top-centre) and the respawn timer (top-left) are overlays, tagged with their bound rule variables.
- **3.3a Pause:** dims the HUD and shows a 420px left panel (Resume, Settings, Save, Load, Surrender, Quit) and a Paused badge.

**Other match screens:**
- **3.3b–c Score:** victory seal or defeat mark, with tabs Overview, Economy, Army, Timeline. Timeline is an army-value graph with event markers.
- **3.4a–b Spectator and replay:** a framed view with side panels (nobody gives orders), a large gold "Spectating" or "Replay" badge, a vision switch and every player's resources. Replay adds a draggable scrubber with event marks and speeds ×0.5–×8. The minimap's transmutation ring sits **behind** the map.
- **3.5a Hero picker:** saved heroes; the selected one stands in a large specimen vat with level/XP, signature ability, six item slots and currency.

### Batch 4 · Create, part 1 (`Editors 1.dc.html`)
- **4.1a Create home:**
  - Project filters in the rail.
  - "Build your first map in 15 minutes" banner showing the three steps.
  - New project: map template, faction, unit pack, remix from Browse.
  - Project grid with a state badge: Published, Draft or Unpublished changes.
- **4.2a–c Unit card:**
  - Specimen vat, name, tags and cost chips, plus a seal (complete or not).
  - Stats as a slider plus typed value. A grey tick on each slider marks the template's value.
  - Attack type, abilities, and **Promote to hero**.
  - Modes: Simple (six stats), Advanced (all), Raw data (text).
  - States: new unit from a template; validation errors shown **at the field**, with a one-click fix; compare with any unit, as a table with a difference column.
- **4.3a Item card:** charges, cooldown, duration, stat bundle, use, stacking, drops on death, and where the item is sold.
- **4.3b Building card:** costs in any resource, what it trains and researches, a **Works as a shop** toggle, and requirements.
- **4.4a–c Ability editor:**
  - **Presets:** twelve starting points.
  - **Block graph:** effect blocks snap left to right (Pay 30 health → Find 8 wounded allies → Heal 40 + 1 per 1% missing → Show), with a passives lane and a block shelf.
  - **AI draft:** the sentence is shown with each phrase underlined in the colour of the setting it became; guessed values are flagged. Confirm, Reroll or pick one of three variants. Drafted blocks stay dashed until confirmed.

### Batch 5 · Create, part 2 (`Editors 2.dc.html`)
- **5.1a–d Rules:** one logic model shown four ways: Presets, WHEN / IF / THEN sentences built from chips, node graph, and plain English through the AI. Map objects are **linked** chips picked on the map, never typed.
- **5.2a Rules debugger during a playtest:** live variables, a log of what fired (fired / condition false / waiting / error), and a click that jumps to the rule.
- **5.3a Tech tree:** drag from a node's port to another node to add a "needs" wire. A wire that would create a **loop is rejected on drop**: the loop's path turns red and a banner explains why.
- **5.4a–b Faction wizard:** five steps (Identity, Roster, Economy and buildings, Heroes, AI and test) with a running timer toward ≤12 minutes. It ends in **Play against the AI**.
- **5.5a Game UI builder:** a 16:9 canvas with the HUD's reserved zones hatched. Widgets are draggable, bound to rule variables, and buttons fire game events. A widget dropped in a HUD zone turns red and blocks publishing.
- **5.6a Import manager:** each file measured against its cap (unit models 20k tris, heavy 30k, textures 2048px, sounds 60s), with a per-file rights attestation. Missing rights block publishing.

## Interactions and behaviour
- **Hotkeys:** shown on every control. Global keys: F1/F2/F3 switch modes, Ctrl K opens the palette, Esc goes back, F5 playtests (in Create). Editors use Q / W / E for Simple / Advanced / Raw.
- **Command palette (Ctrl K):** fuzzy-searches every command, screen and setting. When nothing matches, it offers an AI request, which only drafts; you review every change.
- **AI:** optional everywhere. AI output is always a **draft awaiting review** (dashed, with guessed values flagged, Confirm / Reroll / Discard), and undo works after you confirm. AI never plays or advises in a match.
- **Validation:** inline at the field in `#2A1C1A` with a `#C8503C` border, plus a fix button. Blocked primary actions are disabled and the tooltip says why.
- **Downloads and uploads:** 6px bars, or a ring for long jobs, with MB and time remaining.
- **Map editor** (`Map Editor.dc.html`, Round 1): tool hotkeys, brush previews, walkable overlay, draggable region handles, context actions, undo/redo, F5 playtest.

## State
Each prototype keeps its state in local component state; the logic class in each `.dc.html` shows the state shape, for example skirmish slots, lobby states, keymaps, stats and the tech-tree edges. Real data sources to wire up:
- account and profile;
- saves and replays;
- the Browse catalogue, subscriptions and download queue;
- lobby and matchmaking;
- the project, and every card, rule and variable in it;
- the rule variables the HUD widgets bind to.

## Assets
- `icons.js`: line icons on a 24px grid with a 1.5 stroke, primitives only. Rebuild them as an icon font or SVG textures.
- `ornaments.js`: Round 1 ornament geometry (transmutation circles, hexagrams, vertex nodes, corners, dividers, minimap ring).
- `levers.js`: Round 2 ornament and motion levers, rope texture and sigil sets. Each lever carries an Unreal note in `CHI_LV_NOTES`.
- `assets/bf-*.jpg`: **reference screenshots only** (Manor Lords), used to test HUD readability. **Do not ship them.** Replace them with renders of our own world.
- Fonts: Cinzel, Inter and JetBrains Mono, all from Google Fonts under the OFL licence.

## Files
- `Chimera Design System.dc.html`:
  - boards 01 Foundations, 02 Components and 03 Shell;
  - 04 Components added in Round 2 (only partly drawn; see Fidelity);
  - 05 Ornament and motion.
- `Screen Map.dc.html`: 00 · Screen map.
- `Map Editor.dc.html`: the Round 1 lead editor, with seven states.
- `Front Door.dc.html`: Batch 1.
- `Browse.dc.html`: Batch 2.
- `Match.dc.html`: Batch 3.
- `Editors 1.dc.html`: Batch 4.
- `Editors 2.dc.html`: Batch 5.
- `Shell Top Bar.dc.html`: the shared top bar component.
- `support.js`: the runtime that renders the `.dc.html` prototypes. It is not part of the design.
