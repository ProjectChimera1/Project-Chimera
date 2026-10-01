# Match HUD board 3.1a: full specification for a Slate rebuild

Research task R4 for trial check (b). Date 2026-10-01. Everything below was measured from a live render of
`docs/ui-redesign/Match.dc.html` (Chromium through playwright-cli 0.1.21, device scale 1), not read off the mockup by eye.
Machine-readable twin: `hud-ref/board-3.1-elements.json` (485 DOM nodes, every box, style, text run, icon and paint order).
Reference images are in `hud-ref/` (table in section 3). Items I could not verify are marked **UNVERIFIED**.

## 0. Findings that change the plan

1. **"Board 3.1" is one board: `3.1a "HUD · early game"`.** Nothing else is labelled 3.1x (section 1). It is 1920x1080, native, no scaling.
2. **The mockup has no world in the repo.** The four `assets/bf-*.jpg` Manor Lords screenshots were left out of the repo on purpose
   (`README.md:6`, `:32`). Opened straight from the repo the board shows a flat `#14161A` field behind the HUD, and Chromium logs four 404s.
   I recovered `bf-village.jpg` from the original export zip (`C:/Users/MD_Ki/Downloads/Project Chimera UI redesign.zip`; its `Match.dc.html`,
   `support.js`, `icons.js`, `levers.js` and `ornaments.js` are byte-identical to the repo copies, checked with `cmp`) and saved it as
   `hud-ref/world-backdrop-bf-village.jpg`. It is a third-party game screenshot: reference only, never ship (`README.md:32`, `:301`).
3. **The HUD occupies 22.1% of the screen** (pixels with alpha > 0.01 in the HUD-only matte), which agrees with `README.md:227` ("about 79% stays open").
4. **Almost nothing in the board is hard for Slate**, with four exceptions that need a decision (section 12): radial gradients and soft
   shadows (no Slate primitive), the polygon-clipped portrait silhouette, the rope/sigil/laser ornament frame, and the minimap's live content.
5. **Text is the real fidelity risk.** The font files have no `kern` table (only GPOS), and Slate's default shaping (`Auto` -> `KerningOnly`
   for left-to-right text) reads only `kern`. Use `ETextShapingMethod::FullShaping` or every string comes out ~1% wide (section 10).
6. **Chromium rounds half pixels up.** The 28px chips sit at y = 5.5 in layout and are drawn on rows 6..33 (section 5.1). Author on whole pixels.

## 1. Which board is 3.1

| code | label (from `Match.dc.html`) | data line | what it is |
|---|---|---|---|
| **3.1a** | HUD · early game | `Match.dc.html:610-611` (state), `:698` (world overlays) | one Acolyte selected, build command card, group 3, idle-worker toast |
| 3.2a-g | big battle / hero / shop / building / under attack / waiting / chat+pings | `:612-626` | same components, other states. Not 3.1 |
| 3.3a | Pause menu | `:628` | overlay on the HUD |
| 3.4a-b | Spectator, Replay | `:630-632` | different layout (400px right column) |
| 3.6a | Arena scenario | `:634` | custom widgets from the Game UI builder |
| 3.3b-c, 3.5a | Score victory/defeat, Hero picker | `:658` | full-screen screens |

- `README.md:227` titles the group "Match HUD (3.1, 3.2a-g, 3.3a, 3.6a)": **3.1 is the group number; the only board in that group coded 3.1 is 3.1a.**
- Section header text in the file is "3.1, 3.2, 3.3a, 3.6 · Match HUD" (`Match.dc.html:55`). The DOM carries `data-screen-label="3.1a"` on a 1920x1080 element.
- **Canonical = 3.1a.** It is the baseline state of the HUD (nothing selected-hero, no alert crossing the world, no pause), it is the board the file
  names 3.1, and it exercises every console component once: top strip, resource chips, clock, alert log, menu, minimap with buttons,
  control-group tab, single-unit selection panel, command card with normal / locked / active / empty buttons, a world selection ring and an edge toast.
- Not in 3.1a, defined by the same template and needed later for the full HUD (not part of this trial's screenshot match): hero tab F1 and
  inventory (3.2b), production queue (3.2d), grouped portrait tiles and mini portraits (3.2a), cooldown / progress overlays (3.2a, 3.2d),
  under-attack banner (3.2e), waiting banner (3.2f), chat (3.2g), pings (3.2g), rally line (3.2d), pause overlay (3.3a). Template lines
  `Match.dc.html:61-219` hold all of them if the trial grows.

## 2. How the board is built (so the numbers can be trusted)

- Runtime: `support.js` renders the `.dc.html` template with React; `icons.js`, `ornaments.js`, `levers.js` register `window.CHI_ICON`, `CHI_ORN`, `CHI_LV`.
- Props at their defaults (`Match.dc.html:516` data-props): `uiOrnament=Full`, `lvMaster=5`, `lvBorderStyle="Woven rope"`, `reducedMotion=false`.
  Every other lever is unset, so each resolves to level 5 through `lvl()` (`levers.js:37`), except draw speed (level 3) and hover speed (level 3).
- Fonts load from Google Fonts (`Match.dc.html:15`). Body: `margin:0; background:#0B0C0E; color:#ECE6D8; font-family:Inter`, `-webkit-font-smoothing:antialiased`, `* {box-sizing:border-box}`.
- Board container: `position:relative; overflow:hidden; background:#14161A; box-shadow:0 0 0 1px #2A2F38` (the 1px outer line is outside the 1920x1080 box).
- **Everything on the board is absolutely positioned or flex-centred.** There is no scroll, no responsive rule. UI scale 80-150% is a global
  option (`README.md:38`): scale every panel, font and hit target, not the world.

### Capture state (important for pixel comparison)

The page animates (toast fade, laser heads, entrance reveals). For reproducible images and numbers every CSS animation was paused at
`currentTime = 3000 ms`: the toast is at full opacity (its visible window is 5%-70% of an 11 s cycle), the 1 s reveals and 1.3 s sigil-ins are finished,
and the four laser heads sit where they are at t = 3.0 s (section 8.3). The match clock in 3.1a is a literal `2:14` (`:610`), so the 2 s React tick does not change it.

## 3. Reference images (all 1920x1080, device scale 1, board element only)

| file (`hud-ref/`) | what it is | use |
|---|---|---|
| `board-3.1a-with-backdrop-1920x1080.png` | the board as the designer saw it, with the Manor Lords placeholder world | visual target |
| `board-3.1a-repo-as-is-no-backdrop-1920x1080.png` | the board exactly as the repo renders it (image 404s, background `#14161A`) | what a fresh checkout looks like |
| `board-3.1a-hud-only-on-14161A-1920x1080.png` | HUD with the world layer removed, flat `#14161A` behind | **primary match target**: compare against an Unreal capture with the world painted `#14161A` |
| `board-3.1a-hud-only-alpha-1920x1080.png` | RGBA HUD layer, alpha recovered by differencing a render on black and a render on white (max channel disagreement 2/255) | overlay on any Unreal frame, or build masks |
| `world-backdrop-bf-village.jpg` (1920x1080) | the placeholder world photo | reference only |
| `board-3.1-elements.json` | full element dump | source for the tables below |
| `fonts/*.ttf` | 10 static TTFs, see section 10 | import into Unreal |

World layer parameters (nodes 1-3): photo `cover`, position `50% 50%`, `filter: saturate(.72) brightness(.8) contrast(1.05)`; vignette
`radial-gradient(120% 95% at 50% 42%, rgba(0,0,0,0) 45%, rgba(8,9,11,.62) 100%)`. Source: `Match.dc.html:686-688` (`photo()`), `:698` (`img:'village'`). The minimap
surface (node 203) reuses the same JPG at `background-size:300% auto` centred, so the minimap picture is a crop of the world photo, not a map render.

## 4. Coordinate system and layout skeleton

- Origin top-left of the board, +x right, +y down, units are CSS px = Slate units at DPI scale 1.0 (set the project's DPI curve so 1920x1080 = 1.0).
- Sizes in the tables are border-box (`getBoundingClientRect`), including the border. Colours are the computed sRGB values.
- `z` is the explicit z-index; `paint` is the computed painting order (0 first) inside the whole board.

```
 x:0                                                                                1920
 y:0   +----------------------------------------------------------------------------------+
       | TOP STRIP 1920x40  #1C1F25  bottom border 1px #3A3F48                  z6       |
 y:40  +----------------------------------------------------------------------------------+
       |                                                                                  |
       |                         WORLD (full bleed, nothing drawn)                        |
       |                      selection ring + hp bar at (1010,560)  z1                   |
       |                                                                                  |
 y:814 | toast 400x38 (12,814) z4                                                         |
 y:864 +--------+                                                       +--------------+  |
 y:874 | MINIMAP| tab(268,874) 60x30   <- control-group tab row z5      |  COMMAND     |  |
 y:904 | 256x216|-------------------------------------------------------|  CARD        |  |
       | z5     | SELECTION PANEL 1364x176 (256,904) z5                 |  300x216 z5  |  |
 y:1080+--------+-------------------------------------------------------+--------------+--+
       0       256                                                   1620           1920
```

Top-level children of the board (DOM order, all `position:absolute` except the board itself):

| node | name | box | z | notes |
|---|---|---|---|---|
| 1 | world backdrop | 0,0 1920x1080 | 0 | photo + vignette (placeholder) |
| 4 | selection ring anchor | 1010,560 0x0 | 1 | child 5 ring, child 6/7 health bar |
| 8 | top strip | 0,0 1920x40 | 6 | |
| 69 | toast stack | 12,814 400x38 | 4 | `left:12px; bottom:228px; width:400px`, column, gap 6 |
| 81 | minimap panel | 0,864 256x216 | 5 | |
| 219 | control-group tab row | 268,874 60x30 | 5 | `left:268px; bottom:176px; display:flex; gap:3px` |
| 231 | selection panel | 256,904 1364x176 | 5 | `left:256px; right:300px; bottom:0; height:176px` |
| 283 | command card | 1620,864 300x216 | 5 | `right:0; bottom:0; width:300px; height:216px` |

Same-z panels paint in DOM order: tab row (219) is painted before the selection panel (231), so the selection panel's top rope (y 901-909) paints over the bottom 3px of the tab (y 901-904).

## 5. Region tables

All values in the tables are generated from the JSON (`board-3.1-elements.json`). "tooltip" is the `title` attribute: the board has **no custom tooltip widget**,
only native browser tooltips. The designed tooltip is the design-system specimen in section 5.9.

### 5.1 Top strip (node 8, z6)

Frame: `0,0 1920x40`, fill `#1C1F25`, 1px bottom border `#3A3F48` (the strip content area is therefore 39px tall). No ornament, no shadow
(`README.md:121` says the top bar has `0 1px 0 #0E0F12`; the board does not draw it. **Follow the board.**) Flex row, `align-items:center; gap:4px; padding:0 8px`.
Left group = 4 resource chips; clock group centred on x=960 (`left:50%; translateX(-50%)`); a flex spacer; alert-log button; menu button.

Chip anatomy (all four): height 28, 1px border `#3A3F48`, fill `#14161A`, padding 0 10px, gap 7: icon 16x16 gold, value (mono 600 15px, `#ECE6D8`), rate (mono 500 11px, `#9A958A`).
The fourth chip has an empty rate node that still contributes its 7px gap (106px wide = 1+10+16+7+54+7+0+10+1).
**Half pixel:** chips are centred in a 39px content box so layout y = 5.5; Chromium draws the border on rows 6 and 33 (measured: row 6 `#3A3F48`, rows 7-32 `#14161A`, row 33 `#3A3F48`). Place chips at y = 6.


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 8 | top-strip | 0,0 1920x40 | 6 | 424 | #1C1F25 | bottom 1px solid #3A3F48 |  |
| 9 | chip:gold-18-every-10-s | 8,5.5 98.81x28 |  | 425 | #14161A | 1px solid #3A3F48 | tooltip: "Gold · +18 every 10 s" |
| 18 | chip:iron-6-every-10-s | 110.81,5.5 83.2x28 |  | 434 | #14161A | 1px solid #3A3F48 | tooltip: "Iron · +6 every 10 s" |
| 27 | chip:aether-regenerates | 198.02,5.5 83.2x28 |  | 443 | #14161A | 1px solid #3A3F48 | tooltip: "Aether · regenerates" |
| 38 | chip:supply-used-max | 285.22,5.5 106x28 |  | 454 | #14161A | 1px solid #3A3F48 | tooltip: "Supply used / max" |
| 49 | clock-group | 886.54,0 146.92x40 |  | 480 |  |  | transform matrix(1, 0, 0, 1, -73.4609, 0) |
| 50 | match-clock | 886.54,11 43.2x18 |  | 481 |  |  | tooltip: "Match clock" |
| 52 | game-speed-pill | 939.74,8 93.72x24 |  | 483 |  | 1px solid #3A3F48 | tooltip: "Game speed · host only (Numpad + / −)" |
| 55 | alert-log-button | 1742.75,5.5 52.81x28 |  | 466 | #14161A | 1px solid #3A3F48 | tooltip: "Alert log · Space jumps to the latest" |
| 61 | menu-button | 1799.56,5.5 112.44x28 |  | 472 | #14161A | 1px solid #3A3F48 | tooltip: "Game menu (F10 or Esc)" |
| 68 | menu-keycap-F10 | 1873,10.5 28x18 |  | 479 | #20242B | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 3px |

Text runs (JetBrains Mono / Inter; `glyph rect` is the content area from `Range.getClientRects`, `line box` the element that carries line-height):

| id | string (rendered) | font | size/line-height | tracking | colour | align | glyph rect x,y w x h | line box x,y w x h |
|---|---|---|---|---|---|---|---|---|
| 15 | 340 | JetBrains Mono 600 | 15px/15px | normal | #ECE6D8 | start | 42,9 27x20 | 42,12 27x15 |
| 17 | +18 | JetBrains Mono 500 | 11px/11px | normal | #9A958A | start | 76,12 19.81x14 | 76,14 19.81x11 |
| 24 | 60 | JetBrains Mono 600 | 15px/15px | normal | #ECE6D8 | start | 144.81,9 18x20 | 144.81,12 18x15 |
| 26 | +6 | JetBrains Mono 500 | 11px/11px | normal | #9A958A | start | 169.81,12 13.2x14 | 169.81,14 13.2x11 |
| 35 | 20 | JetBrains Mono 600 | 15px/15px | normal | #ECE6D8 | start | 232.02,9 18x20 | 232.02,12 18x15 |
| 37 | +2 | JetBrains Mono 500 | 11px/11px | normal | #9A958A | start | 257.02,12 13.2x14 | 257.02,14 13.2x11 |
| 46 | 9 / 20 | JetBrains Mono 600 | 15px/15px | normal | #ECE6D8 | start | 319.22,9 54x20 | 319.22,12 54x15 |
| 51 | 2:14 | JetBrains Mono 600 | 18px/18px | normal | #ECE6D8 | start | 886.54,8 43.2x23 | 886.54,11 43.2x18 |
| 53 | Normal · ×1.0 | Inter 500 | 12px/12px | normal | #9A958A | start | 948.74,12 75.72x15 | 939.74,8 93.72x24 |
| 60 | 3 | JetBrains Mono 600 | 13px/13px | normal | #9A958A | start | 1776.75,11 7.81x17 | 1742.75,5.5 52.81x28 |
| 61 | Menu | Inter 500 | 12px/12px | normal | #ECE6D8 | start | 1833.56,11.5 32.44x15 | 1799.56,5.5 112.44x28 |
| 68 | F10 | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | start | 1878,11.5 18x13 | 1873,10.5 28x18 |

Icons:

| id | icon (icons.js) | size | x,y | stroke | stroke-width |
|---|---|---|---|---|---|
| 11 | resource | 16 | 19,11.5 | #C9A86A | 1.5px |
| 20 | terrain | 16 | 121.81,11.5 | #C9A86A | 1.5px |
| 29 | array | 16 | 209.02,11.5 | #C9A86A | 1.5px |
| 40 | users | 16 | 296.22,11.5 | #C9A86A | 1.5px |
| 56 | alert | 16 | 1753.75,11.5 | #9A958A | 1.5px |
| 62 | grid | 16 | 1810.56,11.5 | #C9A86A | 1.5px |

Data binding in 3.1a: gold 340 (+18, tooltip "every 10 s"), iron 60 (+6), aether 20 (+2), supply 9 / 20 (icon `users`; a lane map shows icon `map`; supply full turns text `#E2735F` and chip border `#C8503C`, `Match.dc.html:600`),
clock `2:14`, speed pill "Normal · ×1.0" (1px `#3A3F48` border, padding 5/8, Inter 500 12px `#9A958A`), alert log count 3 in muted `#9A958A` with muted icon
(urgent state: text `#E2735F`, border `#C8503C`, `Match.dc.html:725`), Menu button with `grid` icon, label "Menu" and keycap "F10".
Keycap spec (used everywhere): mono 600, height 18 (this one), padding 0 4, border 1px `#5C5446` with a **2px bottom border**, radius 3px, fill `#20242B` here (`#14161A` in the toast, `#0B0C0E` and radius 2px on command-card keycaps).

### 5.2 World overlays (nodes 4-7, z1) and the world

Only one world overlay exists in 3.1a: the selected Acolyte.


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 5 | selection-ring (ellipse 38x16, 3px verdigris) | 991,552 38x16 |  | 5 |  | 3px solid #4FB39A | #0B0C0E @0.75 0px 0px 0px 2px, #0B0C0E @0.6 0px 0px 0px 1px inset; radius 50%; transform matrix(1, 0, 0, 1, -19, -8) |
| 6 | selection-health-bar-frame | 995,522 30x7 |  | 6 | #0B0C0E | 1px solid #0B0C0E | #ECE6D8 @0.25 0px 0px 0px 1px; transform matrix(1, 0, 0, 1, -15, 0) |
| 7 | selection-health-bar-fill | 996,523 28x5 |  | 7 | #4FB39A |  |  |

- Ring: ellipse 38x16 (width x .42, `Match.dc.html:692`), centred on (1010,560), border 3px `#4FB39A` (verdigris; gold `#E3C887` for heroes), plus an outer ring `0 0 0 2px rgba(11,12,14,.75)` and an inner hairline `inset 0 0 0 1px rgba(11,12,14,.6)`. Radius 50% (true ellipse).
- Health bar: 30x7 box centred 30px above the ring (`bt = -(ring.h/2 + 30)`), fill `#0B0C0E`, 1px border `#0B0C0E`, outer `0 0 0 1px rgba(236,230,216,.25)`, inner fill `#4FB39A` at 100% (28x5). Bars are 7px tall for every unit (`README.md:244`).
- In Unreal these are world-space (decal or screen-projected). The numbers above are the screenshot positions, not a spec for world scale.
- Nothing else is drawn over the world: no rally line, no ping, no range circle, no fog (`fog` in the `world:` data at `:611` is only used by the old `world()` painter, not by `nhud()`).

### 5.3 Alert toast (node 69, z4; edge alert stack above the minimap)

Anchored `left:12; bottom:228` (README: "alerts and chat stack above the minimap, left 12, bottom 228", `README.md:243`). One toast in 3.1a, 400x38:
fill `rgba(20,22,26,.94)`, 1px border `#3A3F48`, padding 9/12, gap 10, `overflow:hidden`. Children: icon `info` 16 `#C9A86A`, text (Inter 500 13px/1.3 `#ECE6D8`, flex 1), keycap "Space" (fill `#14161A`),
and a **2px gold timer bar** along the bottom edge (`#C9A86A`, starts 100% wide, shrinks to 0 over 70% of an 11 s cycle). Border colour is `#3A3F48` for info toasts, `#C8503C` for danger toasts (`T()`, `:696`).


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 70 | toast-idle-acolyte | 12,814 400x38 |  | 9 | #14161A @0.94 | 1px solid #3A3F48 | overflow hidden; tooltip: "Space jumps to the idle Acolyte" |
| 78 | toast-keycap-Space | 359,824 40x18 |  | 17 | #14161A | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 3px |
| 80 | toast-timer-bar | 13,849 242.92x2 |  | 19 | #C9A86A |  |  |

| id | string (rendered) | font | size/line-height | tracking | colour | align | glyph rect x,y w x h | line box x,y w x h |
|---|---|---|---|---|---|---|---|---|
| 77 | Acolyte idle at the west wells | Inter 500 | 13px/16.9px | normal | #ECE6D8 | start | 51,824.55 180.08x16 | 51,824.55 298x16.89 |
| 79 | Space | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | start | 364,825 30x13 | 359,824 40x18 |

| id | icon (icons.js) | size | x,y | stroke | stroke-width |
|---|---|---|---|---|---|
| 72 | info | 16 | 25,825 | #C9A86A | 1.5px |

Motion: `hudFade 11s ease-in-out infinite` (`0%{opacity:0} 5%,70%{opacity:1} 86%,100%{opacity:0}`) and `hudTimer 11s linear infinite` (`0%{width:100%} 70%,100%{width:0}`). In game this is a one-shot: fade in, hold, fade out, timer drains while it is held.
The captured 242.92px timer width is the t = 3.0 s value (100% = 398px less border: the bar is `width:100%` of the padding box).

### 5.4 Minimap panel (node 81, z5)

Panel `0,864 256x216`, fill `#1C1F25`, 1px top border `#8A6E3C` (gold dark), 1px right border `#3A3F48`. Content, all absolutely placed inside the panel (panel-local coordinates in the JSON `style.left/top`):
three 28x28 buttons in a column at (8,16) with gap 4 (`eye` "Show allied vision (Alt V)", `flag` "Ping (Alt click)", `grid` "Terrain / units only (Alt T)"), a 200x200 raised plate at (44,8), and the 184x184 map at (52,16).


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 81 | minimap-panel | 0,864 256x216 | 5 | 20 | #1C1F25 | top 1px solid #8A6E3C, right 1px solid #3A3F48 |  |
| 182 | minimap-button:show-allied-vision-alt-v | 8,881 28x28 |  | 44 | #14161A | 1px solid #3A3F48 | tooltip: "Show allied vision (Alt V)" |
| 186 | minimap-button:ping-alt-click | 8,913 28x28 |  | 48 | #14161A | 1px solid #3A3F48 | tooltip: "Ping (Alt click)" |
| 190 | minimap-button:terrain-units-only-alt-t | 8,945 28x28 |  | 52 | #14161A | 1px solid #3A3F48 | tooltip: "Terrain / units only (Alt T)" |
| 197 | minimap-frame-plate | 44,873 200x200 |  | 21 | #262A31 | 1px solid #8A6E3C | #000000 @0.4 0px 3px 6px 0px, #E3C887 @0.25 1px 1px 0px 0px inset, #0B0C0E @0.5 -1px -1px 0px 0px inset; tooltip: "Minimap frame" |
| 198 | minimap-frame-inner-line | 48,877 192x192 |  | 22 |  | 1px solid #4A4234 |  |
| 199 | minimap-node-tl | 42,871 6x6 |  | 23 | #14161A | 1px solid #C9A86A | radius 50% |
| 200 | minimap-node-tr | 240,871 6x6 |  | 24 | #14161A | 1px solid #C9A86A | radius 50% |
| 201 | minimap-node-bl | 42,1069 6x6 |  | 25 | #14161A | 1px solid #C9A86A | radius 50% |
| 202 | minimap-node-br | 240,1069 6x6 |  | 26 | #14161A | 1px solid #C9A86A | radius 50% |
| 203 | minimap-map-surface (bf-village.jpg 300% crop) | 52,881 184x184 |  | 27 | url(bf-village.jpg) 300% auto @50% 50% | 1px solid #0B0C0E | #000000 @0.6 0px 2px 6px 0px inset; overflow hidden; tooltip: "Minimap · click or drag to move the camera · Alt-click to ping" |
| 204 | minimap-map-dim (rgba 20,22,26,.35) | 53,882 182x182 |  | 28 | #14161A @0.35 |  |  |
| 205 | minimap-map-fog (radial-gradient) | 53,882 182x182 |  | 29 | radial-gradient(circle at 30% 68%, #000000 @0 30%, #08090B @0.66 52%) |  |  |
| 206 | minimap-unit-dot | 85.89,1009.53 7x7 |  | 30 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -3.5, -3.5) |
| 207 | minimap-unit-dot | 101.45,1021.45 5x5 |  | 31 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2.5, -2.5) |
| 208 | minimap-unit-dot | 79.61,995.97 5x5 |  | 32 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2.5, -2.5) |
| 209 | minimap-unit-dot | 191.45,914.89 7x7 |  | 33 | #D55E00 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -3.5, -3.5) |
| 210 | minimap-unit-dot | 181.53,901.33 5x5 |  | 34 | #D55E00 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2.5, -2.5) |
| 211 | minimap-unit-dot | 134.72,971 4x4 |  | 35 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 212 | minimap-unit-dot | 142,963.72 4x4 |  | 36 | #56B4E9 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 213 | minimap-unit-dot | 149.27,960.08 4x4 |  | 37 | #D55E00 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 214 | minimap-unit-dot | 160.19,949.16 4x4 |  | 38 | #CC79A7 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 215 | minimap-unit-dot | 207.52,985.55 4x4 |  | 39 | #D55E00 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 216 | minimap-unit-dot (duplicate of 206, base list + mmD both contain [20,72]) | 85.89,1009.53 7x7 |  | 40 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -3.5, -3.5) |
| 217 | minimap-unit-dot | 94.67,1000.11 4x4 |  | 41 | #0072B2 | 1px solid #0B0C0E | transform matrix(1, 0, 0, 1, -2, -2) |
| 218 | minimap-camera-rect | 89,972 54x34 |  | 42 |  | 1px solid #ECE6D8 |  |

| id | icon (icons.js) | size | x,y | stroke | stroke-width |
|---|---|---|---|---|---|
| 183 | eye | 16 | 14,887 | #C9A86A | 1.5px |
| 187 | flag | 16 | 14,919 | #C9A86A | 1.5px |
| 191 | grid | 16 | 14,951 | #C9A86A | 1.5px |

Notes:
- The plate is `#262A31` with a 1px `#8A6E3C` border and **three shadows**: `0 3px 6px rgba(0,0,0,.4)` (soft drop), `inset 1px 1px 0 rgba(227,200,135,.25)` (top-left bevel highlight), `inset -1px -1px 0 rgba(11,12,14,.5)` (bottom-right bevel). Inside it a 192x192 inner line (inset 3px, 1px `#4A4234`) and four 6px gold-ringed nodes (`#14161A` fill, 1px `#C9A86A`, radius 50%) centred on the plate's corners.
- Map surface: 1px `#0B0C0E` border, `inset 0 2px 6px rgba(0,0,0,.6)`, content = photo crop + `rgba(20,22,26,.35)` dim + fog `radial-gradient(circle at 30% 68%, transparent 30%, rgba(8,9,11,.66) 52%)` + unit dots + camera rectangle.
- Unit dots (nodes 206-217) are 4/5/7px squares with 1px `#0B0C0E` border, centred on percent coordinates, player colours Okabe-Ito (`#0072B2` blue = you, `#D55E00` vermillion, `#56B4E9` sky, `#CC79A7` purple). **Node 216 duplicates node 206** (the base dot list and the board's own list both contain [20,72]); one is enough.
- Camera rectangle: 54x34, 1px `#ECE6D8`, at map-local (36,90) (state `cam {x:.34,y:.58}`; `camL = x*184-27`, `camT = y*184-17`).
- Minimap interactions (not visual): pointer-down/drag moves the camera clamped to x .15-.85, y .1-.9; Alt-click pings.
- The panel carries the ornament frame described in section 8 (top edge only).
- README says "256x216 ... 184px square map ... 200px plate ... three buttons down its left side" (`README.md:234`): all confirmed.

### 5.5 Control-group tab (node 219, z5)

Row at (268,874), gap 3, tabs 30px tall, **no bottom border** (they sit on the selection panel's top edge, y = 904). Only assigned groups render; 3.1a has one: group 3. Selected state: fill `#2A2F38`, border `#E3C887`; unselected: fill `#1C1F25`, border `#3A3F48`; empty slots are dashed `#2A2F38` at 50% opacity but are not drawn on this board (`X.tabs = [3]`, `:698`). No hero tab in 3.1a (hero level 1 and code 3.1a are excluded, `:714`).
Content: number (mono 600 11px, gold bright when selected, muted otherwise), `owner` icon 14px, count (mono 600 12px `#ECE6D8`). Padding 0 9, gap 6. Rendered width 59.81 -> pixels 268..327.


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 220 | control-group-tab-3 | 268,874 59.81x30 |  | 159 | #2A2F38 | top 1px solid #E3C887, right 1px solid #E3C887, left 1px solid #E3C887 | tooltip: "Control group 3 · 8 units · press 3, double-tap to centre" |

| id | string (rendered) | font | size/line-height | tracking | colour | align | glyph rect x,y w x h | line box x,y w x h |
|---|---|---|---|---|---|---|---|---|
| 222 | 3 | JetBrains Mono 600 | 11px/11px | normal | #E3C887 | start | 278,882 6.61x14 | 278,884 6.61x11 |
| 230 | 8 | JetBrains Mono 600 | 12px/12px | normal | #ECE6D8 | start | 310.61,881.5 7.2x16 | 310.61,883.5 7.2x12 |

| id | icon (icons.js) | size | x,y | stroke | stroke-width |
|---|---|---|---|---|---|
| 224 | owner | 14 | 290.61,882.5 | #E3C887 | 1.5px |

### 5.6 Selection panel (node 231, z5): one unit

Panel `256,904 1364x176` (spans minimap right edge 256 to command card left edge 1620), fill `#1C1F25`, 1px top border `#8A6E3C`, padding 14/18, flex row, gap 20, `align-items:stretch`.
Inner content box therefore starts at x = 274, y = 919 and is 147px tall. Children for a single unit:

1. **Portrait vat** 124x147 at (274,919): fill `radial-gradient(circle at 50% 70%, #2E4A44 0, #1A1D22 62%)` over `#14161A`, 1px border `#8A6E3C`. Inside, a 48x78 silhouette placeholder (`linear-gradient(180deg,#9FB0BE,#4A5A70 70%,#2E3A4A)`, opacity .85, `clip-path: polygon(50% 0,64% 8%,64% 18%,78% 28%,86% 58%,76% 60%,70% 44%,68% 72%,72% 100%,28% 100%,32% 72%,30% 44%,24% 60%,14% 58%,22% 28%,36% 18%,36% 8%)`), bottom-aligned with 12px margin, at (312,975). README: replace the silhouette with 3D render targets (`README.md:33`). A building shows a 60px gold icon instead of the figure (not 3.1a). A level badge (32x32, `#14161A`, 1px `#E3C887`, Cinzel 700 14px) pokes out at top-right for heroes (not 3.1a).
2. **Info column** 340x147 at (418,919), `justify-content:center`, gap 7: name row (Cinzel 600 20px/22px `#ECE6D8`, one line), role (Inter 400 13px/1.2 `#9A958A`), HP block (text row with 220 / 220 left and an empty mana text right; 7px bar with 1px `#3A3F48` border, `#0B0C0E` track, `#4FB39A` fill 100% = 338x5; the 5px mana bar `#56B4E9` and XP row `#C9A86A` are omitted when the unit has none), stat line (mono 500 12px/1.3, one line, `overflow:hidden; text-overflow:ellipsis`).
3. Not used by a worker: inventory 3x2 grid (52px slots, 5px gap), production queue, grouped tiles.


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 231 | selection-panel | 256,904 1364x176 | 5 | 170 | #1C1F25 | top 1px solid #8A6E3C |  |
| 265 | portrait-vat | 274,919 124x147 |  | 187 | #14161A + radial-gradient(circle at 50% 70%, #2E4A44 0px, #1A1D22 62%), none | 1px solid #8A6E3C |  |
| 266 | portrait-figure-silhouette | 312,975 48x78 |  | 188 | linear-gradient(#9FB0BE, #4A5A70 70%, #2E3A4A) |  | opacity 0.85; clip-path polygon(50% 0px, 64% 8%, 64% 18%, 78% 28%, 86% 58%, 76% 60%, 70% 44%, 68% 72%, 72% 100%, 28% 100%, 32% 72%, 30% 44%, 24% 60%, 14% 58%, 22% 28%, 36% 18%, 36% 8%) |
| 279 | hp-bar-frame | 418,1011.5 340x7 |  | 183 | #0B0C0E | 1px solid #3A3F48 |  |
| 280 | hp-bar-fill | 419,1012.5 338x5 |  | 184 | #4FB39A |  |  |
| 281 | stat-line-row | 418,1025.5 340x15.59 |  | 185 |  |  | overflow hidden |

| id | string (rendered) | font | size/line-height | tracking | colour | align | glyph rect x,y w x h | line box x,y w x h |
|---|---|---|---|---|---|---|---|---|
| 270 | Covenant Acolyte | Cinzel 600 | 20px/22px | normal | #ECE6D8 | start | 418,940.91 204.42x27 | 418,943.91 204.42x22 |
| 272 | Worker · carrying 10 gold | Inter 400 | 13px/15.6px | normal | #9A958A | start | 418,971.91 154.25x16 | 418,972.91 340x15.59 |
| 276 | 220 / 220 | JetBrains Mono 500 | 12px/12px | normal | #ECE6D8 | start | 418,993.5 64.81x16 | 418,995.5 64.81x12 |
| 282 | Armour 1 · Damage 6–8 · Speed 2.8 · Carry 10 g | JetBrains Mono 500 | 12px/15.6px | normal | #ECE6D8 | start | 418,1024.5 331.2x16 | 418,1025.5 340x15.59 |

Rendered strings: "Covenant Acolyte", "Worker · carrying 10 gold", "220 / 220", "Armour 1 · Damage 6–8 · Speed 2.8 · Carry 10 g".
**The stat line in the source has two spaces around each middle dot (`'  ·  '`, `:732`) but HTML collapses them: the rendered width 331.2px = 46 characters x 7.2.** Slate does not collapse whitespace, so author single spaces (or a deliberate 2-space design if Alec prefers; the mockup shows one).
Cinzel's lowercase letters are small-capitals by design, so "Covenant Acolyte" looks like "COVENANT ACOLYTE" with larger initials; no `text-transform` is applied.

### 5.7 Command card (node 283, z5)

Panel `1620,864 300x216`, fill `#1C1F25`, 1px top border `#8A6E3C`, 1px left border `#3A3F48`, padding 12. Grid: 4 columns x 64px, rows 60px, gap 6 -> 275 x 192 at (1633,877). Row-major slots Q W E R / A S D F / Z X C V.
Button (64x60): fill `#14161A`, border 1px `#4A4234` (normal), keycap at (3,3) 16x16, icon 24px centred in the flex column (`gap:4`), optional cost label (mono 600 9px, `#9A958A`, centred, padded 0 2, clipped), lock icon 12px at (top 4, right 4), optional cooldown overlay (`rgba(11,12,14,.72)` rising from the bottom, 18px mono 700 number) and progress bar (4px, track `#2A2F38`, fill `#C9A86A`): the last two do not occur in 3.1a.

| state | fill | border | icon | opacity | cursor | slots in 3.1a |
|---|---|---|---|---|---|---|
| normal | `#14161A` | 1px solid `#4A4234` | `#C9A86A` | 1 | pointer | Q Foundry (place), W Refinery (resource), A Attack (swords), S Stop (stop), D Hold (pathing), F Patrol (flag), X Return cargo (undo), C Repair (rotate) |
| locked ("needs X") | `#14161A` | 1px solid `#3A3F48` | `#C9A86A` | **.55 on the whole button** | not-allowed | E Watchtower (prop, "Needs a Foundry"), R Alembic Works (array, "Needs a Foundry and a Smithy"); lock icon `#9A958A` |
| active/toggled | `#173129` | 1px solid `#4FB39A` | `#4FB39A` | 1 | pointer | Z Gather (resource) |
| unaffordable ("poor") | as normal but disabled | | | 1, cost text `#E2735F` | not-allowed | none |
| empty | transparent | 1px **dashed** `#2A2F38` | none | 1 | default | V |
| hover | | border `#E3C887` (empty slot `#2A2F38`) | | | | |
| focus | | outline 2px `#E3C887`, offset 1px | | | | |

Source: `CARDS.worker` `Match.dc.html:573`, `mkCard` `:588-595`, template `:200-216`. Composited colours at the locked button (over `#1C1F25`): fill `#181A1F`, border `#2D3138`. Tooltips (title) read `Name (K) · cost · locked: reason`.


| id | element | x,y  w x h | z | paint | fill | border | shadow / other |
|---|---|---|---|---|---|---|---|
| 283 | command-card | 1620,864 300x216 | 5 | 222 | #1C1F25 | top 1px solid #8A6E3C, left 1px solid #3A3F48 |  |
| 384 | cmd-button:foundry | 1633,877 64x60 |  | 224 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Foundry (Q) · 150 g" |
| 385 | div | 1637,881 16x16 |  | 225 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 392 | div | 1634,916.5 62x9 |  | 232 |  |  | overflow hidden |
| 394 | cmd-button:refinery | 1703,877 64x60 |  | 234 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Refinery (W) · 100 g" |
| 395 | div | 1707,881 16x16 |  | 235 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 401 | div | 1704,916.5 62x9 |  | 241 |  |  | overflow hidden |
| 403 | cmd-button:watchtower | 1773,877 64x60 |  | 243 | #14161A | 1px solid #3A3F48 | opacity 0.55; overflow hidden; tooltip: "Watchtower (E) · 75 g · locked: Needs a Foundry" |
| 404 | div | 1777,881 16x16 |  | 250 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 410 | div | 1774,916.5 62x9 |  | 248 |  |  | overflow hidden |
| 416 | cmd-button:alembic-works | 1843,877 64x60 |  | 256 | #14161A | 1px solid #3A3F48 | opacity 0.55; overflow hidden; tooltip: "Alembic Works (R) · 220 g · locked: Needs a Foundry and a Smithy" |
| 417 | div | 1847,881 16x16 |  | 265 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 425 | div | 1844,916.5 62x9 |  | 263 |  |  | overflow hidden |
| 431 | cmd-button:attack | 1633,943 64x60 |  | 271 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Attack (A)" |
| 432 | div | 1637,947 16x16 |  | 272 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 440 | cmd-button:stop | 1703,943 64x60 |  | 280 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Stop (S)" |
| 441 | div | 1707,947 16x16 |  | 281 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 446 | cmd-button:hold | 1773,943 64x60 |  | 286 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Hold (D)" |
| 447 | div | 1777,947 16x16 |  | 287 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 453 | cmd-button:patrol | 1843,943 64x60 |  | 293 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Patrol (F)" |
| 454 | div | 1847,947 16x16 |  | 294 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 460 | cmd-button:gather | 1633,1009 64x60 |  | 300 | #173129 | 1px solid #4FB39A | overflow hidden; tooltip: "Gather (Z)" |
| 461 | div | 1637,1013 16x16 |  | 301 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 467 | cmd-button:return-cargo | 1703,1009 64x60 |  | 307 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Return cargo (X)" |
| 468 | div | 1707,1013 16x16 |  | 308 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 474 | cmd-button:repair | 1773,1009 64x60 |  | 314 | #14161A | 1px solid #4A4234 | overflow hidden; tooltip: "Repair (C)" |
| 475 | div | 1777,1013 16x16 |  | 315 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |
| 481 | cmd-button:empty-slot | 1843,1009 64x60 |  | 321 |  | 1px dashed #2A2F38 | overflow hidden; tooltip: "Empty slot" |
| 482 | div | 1847,1013 16x16 |  | 322 | #0B0C0E | top 1px solid #5C5446, right 1px solid #5C5446, bottom 2px solid #5C5446, left 1px solid #5C5446 | radius 2px |

| id | string (rendered) | font | size/line-height | tracking | colour | align | glyph rect x,y w x h | line box x,y w x h |
|---|---|---|---|---|---|---|---|---|
| 386 | Q | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1642,882 6x13 | 1637,881 16x16 |
| 393 | 150 g | JetBrains Mono 600 | 9px/9px | normal | #9A958A | center | 1651.5,914.5 27x12 | 1634,916.5 62x9 |
| 396 | W | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1712,882 6x13 | 1707,881 16x16 |
| 402 | 100 g | JetBrains Mono 600 | 9px/9px | normal | #9A958A | center | 1721.5,914.5 27x12 | 1704,916.5 62x9 |
| 405 | E | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1782,882 6x13 | 1777,881 16x16 |
| 411 | 75 g | JetBrains Mono 600 | 9px/9px | normal | #9A958A | center | 1794.19,914.5 21.61x12 | 1774,916.5 62x9 |
| 418 | R | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1852,882 6x13 | 1847,881 16x16 |
| 426 | 220 g | JetBrains Mono 600 | 9px/9px | normal | #9A958A | center | 1861.5,914.5 27x12 | 1844,916.5 62x9 |
| 433 | A | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1642,948 6x13 | 1637,947 16x16 |
| 442 | S | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1712,948 6x13 | 1707,947 16x16 |
| 448 | D | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1782,948 6x13 | 1777,947 16x16 |
| 455 | F | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1852,948 6x13 | 1847,947 16x16 |
| 462 | Z | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1642,1014 6x13 | 1637,1013 16x16 |
| 469 | X | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1712,1014 6x13 | 1707,1013 16x16 |
| 476 | C | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1782,1014 6x13 | 1777,1013 16x16 |
| 483 | V | JetBrains Mono 600 | 10px/14px | normal | #ECE6D8 | center | 1852,1014 6x13 | 1847,1013 16x16 |

| id | icon (icons.js) | size | x,y | stroke | stroke-width |
|---|---|---|---|---|---|
| 388 | place | 24 | 1653,888.5 | #C9A86A | 1.5px |
| 398 | resource | 24 | 1723,888.5 | #C9A86A | 1.5px |
| 407 | prop | 24 | 1793,888.5 | #C9A86A | 1.5px |
| 413 | lock | 12 | 1820,882 | #9A958A | 1.5px |
| 420 | array | 24 | 1863,888.5 | #C9A86A | 1.5px |
| 428 | lock | 12 | 1890,882 | #9A958A | 1.5px |
| 435 | swords | 24 | 1653,961 | #C9A86A | 1.5px |
| 444 | stop | 24 | 1723,961 | #C9A86A | 1.5px |
| 450 | pathing | 24 | 1793,961 | #C9A86A | 1.5px |
| 457 | flag | 24 | 1863,961 | #C9A86A | 1.5px |
| 464 | resource | 24 | 1653,1027 | #4FB39A | 1.5px |
| 471 | undo | 24 | 1723,1027 | #C9A86A | 1.5px |
| 478 | rotate | 24 | 1793,1027 | #C9A86A | 1.5px |

### 5.8 Z-order summary (bottom to top)

world photo (0) -> world vignette -> selection ring, hp bar (z1) -> toast (z4) -> [tab row, minimap panel, selection panel, command card at z5 in DOM order: minimap panel, tab row, selection panel, command card] -> top strip (z6). Inside each panel: panel background, content in DOM order, then its ornament frame container (z3) which holds ropes (z2) and sigils/lasers (z3).
Full order list: `paintOrderList` in the JSON.

### 5.9 Tooltip (design-system specimen, `Chimera Design System.dc.html:186-189`; not drawn on board 3.1a)

Box fill `#14161A`, border 1px `#8A6E3C`, padding 10/12, column gap 6, max-width 300, shadow `0 4px 0 rgba(0,0,0,.35)` (a hard 4px drop, no blur). Header row: title Inter 600 14px `#ECE6D8` + keycap (mono 600 11px/18px, min-width 20, height 20, padding 0 5, fill `#20242B`, border `#5C5446` 1px / 2px bottom, radius 3). Body: Inter 400 13px/1.4 `#9A958A`. "The tooltip always says why" a control is disabled (`README.md:129`). The HUD's tooltip strings in 3.1a are the `tooltip:` entries in the tables above.

## 6. Design tokens used by this board

Colours (`README.md:63-86`; measured use in 3.1a):

| token | hex | used for in 3.1a |
|---|---|---|
| Void | `#0B0C0E` | hp bar track, keycap fill (cmd card), minimap map border, ring outline |
| Surface 0 sunken | `#14161A` | chips, buttons, toast keycap, cmd buttons, portrait base |
| Surface 1 HUD console | `#1C1F25` | top strip, three console panels |
| Surface 2 panel | `#20242B` | menu keycap |
| Surface 3 | `#262A31` | minimap plate |
| Surface 4 hover/selected | `#2A2F38` | selected tab, progress track, empty-slot dashed border |
| Border | `#3A3F48` | hairlines, chip borders, panel right/left borders, locked button border |
| Border warm | `#4A4234` | normal command button border, minimap inner line |
| Keycap border | `#5C5446` | all keycaps |
| Gold dark | `#8A6E3C` | console top borders, portrait border, minimap plate border, rope/sigil dark strokes |
| Gold | `#C9A86A` | icons, toast timer, minimap nodes, rope/sigil strokes |
| Gold bright | `#E3C887` | selected tab border and number, bevel highlight (alpha .25), laser head |
| Text | `#ECE6D8` | values, names, keycap text |
| Text muted | `#9A958A` | rates, roles, costs, muted alert icon |
| Verdigris | `#4FB39A` | hp fill, ring, active button |
| Success fill | `#173129` | active button fill |
| Mana blue | `#56B4E9` | mana bar (not in 3.1a), sky player |
| Danger border / text / fill | `#C8503C` / `#E2735F` / `#2A1C1A` | not in 3.1a (urgent alert, poor cost, under-attack banner) |

Type styles that occur (CSS shorthand `weight size/line-height family`):

| style | CSS | where |
|---|---|---|
| resource value | `600 15px/1 JetBrains Mono` | chips |
| resource rate | `500 11px/1 JetBrains Mono` | chips |
| clock | `600 18px/1 JetBrains Mono` | top centre |
| speed pill, Menu | `500 12px/1 Inter` | top strip |
| alert count | `600 13px/1 JetBrains Mono` | alert log |
| toast text | `500 13px/1.3 Inter` | toast |
| keycap | `600 10px/14px JetBrains Mono` (cmd card, toast, F10; 11px/16px in the pause menu, not 3.1a) | all keycaps |
| tab number / count | `600 11px/1` and `600 12px/1 JetBrains Mono` | group tab |
| unit name | `600 20px/1.1 Cinzel` | selection |
| unit role | `400 13px/1.2 Inter` | selection |
| hp text | `500 12px/1 JetBrains Mono` | selection |
| stat line | `500 12px/1.3 JetBrains Mono` | selection |
| cost label | `600 9px/1 JetBrains Mono` | cmd card |

Letter-spacing is `normal` everywhere in 3.1a and there is no text-shadow and no text-transform. (Elsewhere in the set: section headers Cinzel 14px +.14em uppercase, kickers Inter 11-12px +.12-.16em uppercase.)
Shape: radius 0 everywhere except keycaps (2-3px), minimap nodes (50%), selection ring (50%). Borders always 1px (ring 3px, keycap bottom 2px). Spacing steps 3 4 6 7 8 9 10 12 14 18 20 (px).
Shadows: only the minimap plate (3 shadows) and the minimap map inset, plus the ring's two. Focus: 2px `#E3C887` outline, offset 1px.

## 7. Icons

18 distinct icons from `icons.js` appear. All are 24x24 line icons, `fill:none`, `stroke:currentColor`, `stroke-width:1.5`, round caps and joins; the board scales the whole 24 grid to 12 / 14 / 16 / 24 px (so the stroke is 0.75 / 0.875 / 1 / 1.5 px on screen).
Colour comes from the container: gold `#C9A86A` by default, `#4FB39A` on the active button, `#9A958A` for muted, `#E3C887` in the selected tab.


| icon | primitives on the 24x24 grid (stroke 1.5, round caps/joins, fill none) | used for |
|---|---|---|
| resource | `polygon points=12,2.5 19,9 12,21.5 5,9; line x1=5 y1=9 x2=19 y2=9` | command-card (24px, #4FB39A), command-card (24px, #C9A86A), top-strip (16px, #C9A86A) |
| terrain | `polyline points=2,19 8,9 12,14 15,10 22,19; line x1=2 y1=19 x2=22 y2=19` | top-strip (16px, #C9A86A) |
| array | `circle cx=12 cy=12 r=9.5; polygon points=12,3.5 19.4,16.3 4.6,16.3; polygon points=12,20.5 4.6,7.7 19.4,7.7; circle cx=12 cy=12 r=3` | command-card (24px, #C9A86A), top-strip (16px, #C9A86A) |
| users | `circle cx=9 cy=8 r=3; path d=M3.5 19a5.5 5.5 0 0 1 11 0; circle cx=16.5 cy=9 r=2.4; path d=M15.5 14.2A4.5 4.5 0 0 1 21 18.5` | top-strip (16px, #C9A86A) |
| alert | `polygon points=12,3 22,20 2,20; line x1=12 y1=9 x2=12 y2=14; line x1=12 y1=16.5 x2=12 y2=17.5` | top-strip (16px, #9A958A) |
| grid | `rect x=3 y=3 width=18 height=18; line x1=9 y1=3 x2=9 y2=21; line x1=15 y1=3 x2=15 y2=21; line x1=3 y1=9 x2=21 y2=9; line x1=3 y1=15 x2=21 y2=15` | minimap-panel (16px, #C9A86A), top-strip (16px, #C9A86A) |
| info | `circle cx=12 cy=12 r=9; line x1=12 y1=11 x2=12 y2=17; line x1=12 y1=7.5 x2=12 y2=8.5` | alert-toast (16px, #C9A86A) |
| eye | `path d=M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z; circle cx=12 cy=12 r=3` | minimap-panel (16px, #C9A86A) |
| flag | `line x1=5 y1=21 x2=5 y2=3; path d=M5 4h12l-2.5 4L17 12H5` | command-card (24px, #C9A86A), minimap-panel (16px, #C9A86A) |
| owner | `circle cx=8 cy=9 r=4; path d=M3 20a5 5 0 0 1 10 0; polyline points=16,9 20,13 16,17; line x1=12 y1=13 x2=20 y2=13` | control-group-tabs (14px, #E3C887) |
| place | `polyline points=3,10 12,3 21,10; rect x=5 y=9 width=14 height=11; rect x=10 y=14 width=4 height=6` | command-card (24px, #C9A86A) |
| prop | `polygon points=12,3 18.5,15 5.5,15; line x1=12 y1=15 x2=12 y2=21` | command-card (24px, #C9A86A) |
| lock | `rect x=5 y=11 width=14 height=10; path d=M8 11V7a4 4 0 0 1 8 0v4` | command-card (12px, #9A958A) |
| swords | `line x1=4 y1=4 x2=16 y2=16; line x1=20 y1=4 x2=8 y2=16; line x1=14 y1=18 x2=18 y2=14; line x1=6 y1=14 x2=10 y2=18` | command-card (24px, #C9A86A) |
| stop | `rect x=6 y=6 width=12 height=12` | command-card (24px, #C9A86A) |
| pathing | `circle cx=12 cy=12 r=8.5; line x1=6 y1=18 x2=18 y2=6` | command-card (24px, #C9A86A) |
| undo | `polyline points=8,4 4,8 8,12; path d=M4 8h10a6 6 0 0 1 0 12h-4` | command-card (24px, #C9A86A) |
| rotate | `path d=M20 12a8 8 0 1 1-2.4-5.7; polyline points=20,3 20,8 15,8` | command-card (24px, #C9A86A) |

Full icon table (62 icons) is `window.CHI_ICONS` in `icons.js`; the 18 above are also embedded verbatim as `iconsUsed[].markup` in the JSON.

## 8. Ornament frame (the hard part of this board)

Three panels carry an ornament frame: minimap panel, selection panel, command card. Each frame is a 0-height absolute container (`left:0; right:0; top:0; height:0; z-index:3`) placed on the panel's **top inner edge**, so every piece is centred on the top border line. The vertical rope segments and "bottom" pieces have height 0 from this and are invisible or coincide with the top ones. With `lvBorderStyle = "Woven rope"` at level 5, master 5 (`levers.js:163-181`):


| frame | container (id) | rope segments (id: x,y w x h) | laser delays |
|---|---|---|---|
| minimap-panel | 82 at 0,865 255x0 | 83: 0,861 127.5x8; 84: 127.5,861 127.5x8; 85: 0,861 127.5x8; 86: 127.5,861 127.5x8 (ids 87-90 are the vertical ropes: height 0, invisible) | 0s, 3.8s, 7.6s, 11.4s |
| selection-panel | 232 at 256,905 1364x0 | 233: 256,901 682x8; 234: 938,901 682x8; 235: 256,901 682x8; 236: 938,901 682x8 (ids 237-240 are the vertical ropes: height 0, invisible) | 1.5s, 5.3s, 9.1s, 12.9s |
| command-card | 284 at 1621,865 299x0 | 285: 1621,861 149.5x8; 286: 1770.5,861 149.5x8; 287: 1621,861 149.5x8; 288: 1770.5,861 149.5x8 (ids 289-292 are the vertical ropes: height 0, invisible) | 3s, 6.8s, 10.6s, 14.4s |

### 8.1 Rope

- Tile: SVG 16x8, two 1.1px strokes forming a woven S-curve (gold dark `#8A6E3C` over bright `#E3C887`) with a small cross-over bar (`#14161A` 2.6px under a `#8A6E3C` 1.1px line). Full SVG in the JSON at `ornaments.ropeTileSvg_16x8_repeat-x`.
- Placement: `repeat-x` strips 8px tall at `top:-4` (centred on the border), two per panel: left half from x = 0 (tile phase 0) and right half `background-position: right 0` (tile phase anchored to the right edge) each 50% wide. Panel widths 255 / 1364 / 299 give halves of 127.5 / 682 / 149.5, which are not multiples of 16, so the tile phase is mirrored about the panel centre.
- Opacity 1 (`bo = [0,.55,.7,.82,.92,1][5]`, `levers.js:166`). Entrance: `chiRevL`/`chiRevR` clip-path reveal, 1 s.
- Hover (`--chiH = 1` while the panel is hovered): tile shrinks to `16 - 5 = 11 px` over `.35 s` (derived from `levers.js:59-61,96`, not browser-verified) and the laser etch lines appear (8.3).

### 8.2 Sigils (minimap panel and command card only; the selection panel is built with `noSigils`)

Eight 14x14 wrappers per panel at `top:-7`, which is only 5 distinct positions because the "bottom" copies sit on top of the "top" copies (container height 0). Later DOM nodes paint over earlier, so the visible sigil at each position is the later one: left corner = leaf (m2), right corner = dot (m3), top centre = dot (m3), left-mid = leaf (m2), right-mid = ring (m0). The two bottom rope strips likewise lie exactly on the two top strips (same pixels drawn twice at opacity 1). Seal set "Seals" (default), SVG `viewBox 0 0 20 20`: outer ring r9 (`#14161A` fill, `#C9A86A` 1px), dotted ring r7.7 (`#8A6E3C`, `.7 1.3` dash), interlocked triangles r6.3 (hexagram, 0.8px `#C9A86A`), and a centre mark chosen by index (`m = i % 4`: 0 ring, 1 lens, 2 leaf, 3 dot, in `#E3C887`). Entrance `chiSigIn` 1.3 s (`rotate(-120deg) scale(.5)` fade-in).


| wrapper id | frame | x,y (14x14) | seal variant index | visibility |
|---|---|---|---|---|
| 91 | minimap-panel | 64,858 | 0 (m=0) top-left corner | hidden under 108 |
| 99 | minimap-panel | 177,858 | 1 (m=1) top-right corner | hidden under 116 |
| 108 | minimap-panel | 64,858 | 2 (m=2) bottom-left corner | visible |
| 116 | minimap-panel | 177,858 | 3 (m=3) bottom-right corner | visible |
| 124 | minimap-panel | 120.5,858 | 1 (m=1) top centre | hidden under 133 |
| 133 | minimap-panel | 120.5,858 | 3 (m=3) bottom centre | visible |
| 141 | minimap-panel | -7,858 | 2 (m=2) left mid | visible |
| 149 | minimap-panel | 248,858 | 0 (m=0) right mid | visible |
| 293 | command-card | 1685,858 | 0 (m=0) top-left corner | hidden under 310 |
| 301 | command-card | 1842,858 | 1 (m=1) top-right corner | hidden under 318 |
| 310 | command-card | 1685,858 | 2 (m=2) bottom-left corner | visible |
| 318 | command-card | 1842,858 | 3 (m=3) bottom-right corner | visible |
| 326 | command-card | 1763.5,858 | 1 (m=1) top centre | hidden under 335 |
| 335 | command-card | 1763.5,858 | 3 (m=3) bottom centre | visible |
| 343 | command-card | 1614,858 | 2 (m=2) left mid | visible |
| 351 | command-card | 1913,858 | 0 (m=0) right mid | visible |

Corner x offsets are `corners level (5 -> 58) + 6 = 64` from each side (`levers.js:130`). The middle-left and middle-right sigils sit centred on the panel edges, so the command card's right one is half off screen (x 1913-1927) and the minimap's left one is half off screen (x -7..7).

### 8.3 Laser shimmer

Per panel, four 1px lines on the four sides of the 0-height container (so the horizontal ones lie on the top border and the vertical ones have height 0 and do not show). A bright head (`linear-gradient(90deg, transparent, #E3C887 70%, #FFF3D6 88%, transparent)`, 28% of the side length, opacity .95) slides across during the first 20% of a 19 s cycle (`chiRunX`, cubic-bezier(.45,.05,.55,.95)), then rests; sides run top, right, bottom, left, offset by 3.8 s each, base delay 0 s (minimap), 1.5 s (selection), 3 s (command card). On hover a second layer ("etch": 3 stacked lines `rgba(227,200,135,.22)` 11px, `#E3C887` 4px, `#FFF3D6` 1px at .85) is revealed behind the head and fades. In a still frame only the **top** laser can be visible, as a short bright segment on the gold line.
Reduced motion (`reducedMotion` prop) stops all of this and leaves the ropes (`levers.js:35`).

## 9. Animation inventory (all start on mount; none move text or controls)

| what | duration / timing | where | note |
|---|---|---|---|
| toast fade | 11 s ease-in-out, infinite in the mock | toast | one-shot in game |
| toast timer | 11 s linear | toast bottom | width 100% -> 0 |
| rope reveal | 1 s cubic-bezier(.25,.6,.3,1) | 3 frames | clip-path |
| sigil in | 1.3 s cubic-bezier(.3,.6,.3,1) | minimap + command card | rotate/scale/fade |
| laser | 19 s loop, 20% active | 3 frames | gradient pan |
| hover etch / rope tighten | .35 s ease | 3 frames | opacity, background-size |

Keyframe text is in the JSON under `keyframes`. `README.md:54-55`: motion is subtle, never loops faster than ~2.4 s, never over the battlefield.

## 10. Fonts

| family | weights used in 3.1a | all weights loaded | source | licence |
|---|---|---|---|---|
| Cinzel | 600 (unit name 20px) | 500 / 600 / 700 | Google Fonts, `https://fonts.googleapis.com/css2?family=Cinzel:wght@500;600;700&family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap` (`Match.dc.html:15`, identical in `Chimera Design System.dc.html:15`) | SIL Open Font License 1.1 (name table: `https://scripts.sil.org/OFL`) |
| Inter | 400 (role), 500 (speed pill, Menu, toast) | 400 / 500 / 600 / 700 | same URL | SIL OFL 1.1 (`https://openfontlicense.org`) |
| JetBrains Mono | 500 (rates, hp, stats), 600 (values, clock, keycaps, costs, counts) | 400 / 500 / 600 | same URL | SIL OFL 1.1 |

- No system-font fallback was used. The CSS stack is the bare family name (`font-family: Inter`, `"JetBrains Mono"`, `Cinzel`); for every text node Chromium's DevTools protocol (`CSS.getPlatformFontsForNode`) reported the web font (`isCustomFont: true`) with all glyphs: Inter / Inter-Medium, JetBrains Mono / JetBrains-Mono-Medium (reported for weights 500 and 600 alike: the browser got one variable file and interpolates), Cinzel / Cinzel-Bold (same reason). Body fallback `system-ui` was never reached. The console shows 5 errors on the repo as-is (four 404s for `assets/bf-*.jpg` plus `favicon.ico`) and 1 (favicon) with the assets restored; none are font errors.
- Files: `hud-ref/fonts/` holds 10 static TTFs downloaded from `fonts.gstatic.com` (URLs in `fonts/LICENSES.txt`) so Unreal does not need variable-font support: Cinzel Medium/SemiBold/Bold, Inter Regular/Medium/SemiBold/Bold, JetBrains Mono Regular/Medium/SemiBold. No `fvar` table; `OS/2.usWeightClass` matches the name. UE 5.8 has no variable-font support that I could find in `SlateCore` (grep for FontVariation/fvar found nothing): **UNVERIFIED that none exists elsewhere**, static files avoid the question.
- Static versus browser glyphs: advance widths agree to within kerning (Inter 500 "Acolyte idle at the west wells" 13px: 180.43 from hmtx vs 180.08 in browser; Cinzel 600 "Covenant Acolyte" 20px: 206.32 vs 204.42, the 1.9px difference is GPOS kerning).
- Vertical metrics (needed to place baselines): Cinzel upm 1000, ascent 976, descent 372; Inter upm 2048, ascent 1984, descent 494; JetBrains Mono upm 1000, ascent 1020, descent 300 (all `hhea`, line gap 0, USE_TYPO_METRICS set). A CSS line box of height L centres the content area (ascent+descent) in L; Slate stacks lines at font height.
- **Slate mapping, verified in engine source** (`D:/Epic Games/UE_5.8/Engine/Source/Runtime/SlateCore`):
  - `FSlateFontInfo::Size` is in points at 96 dpi (`Public/Fonts/SlateFontInfo.h:170`, `RenderDPI = 96` at `:15`); glyph pixel size = `round(Size * 96/72 * FontScale)` (`Private/Fonts/FontCacheFreeType.cpp:129-139`). **Size = CSS px x 0.75**: 20px -> 15, 15 -> 11.25, 13 -> 9.75, 12 -> 9, 11 -> 8.25, 10 -> 7.5, 9 -> 6.75, 18 -> 13.5. All sizes in the board are integer px, so the rounding is exact at DPI scale 1.
  - Shaping: `ETextShapingMethod::Auto` uses `KerningOnly` for LTR text (`Public/Fonts/FontCache.h:50-70`) and kerning comes from `FT_Get_Kerning` (`Private/Fonts/FontCacheFreeType.cpp:863`), which reads the legacy `kern` table. These fonts have `kern: False, GPOS: True`. **Set `TextShapingMethod = FullShaping` (HarfBuzz) on every text block.**
  - Line height: `STextBlock::LineHeightPercentage` (`Slate/Public/Widgets/Text/STextBlock.h:68,130`) exists but scales the font height; to reproduce CSS `line-height: 15px` on a 15px font use a fixed-height `SBox` around the text and centre it (the tables give the line box for every string).
  - Letter-spacing exists (`FSlateFontInfo::LetterSpacing`, 1/1000 em) but is not needed in 3.1a.
- Rendering differences to expect: Chromium uses unhinted grayscale AA with fractional x positions (e.g. 144.81); Slate's FreeType path hints by default and snaps glyph origins. Text will not match pixel for pixel; compare text regions by box and position, not by pixel diff.

## 11. Interaction states defined by the template (visual spec only)

| control | hover | focus | other |
|---|---|---|---|
| alert log, Menu, minimap buttons, tabs | border `#C9A86A` | 2px `#E3C887` outline, offset 1px | cursor pointer |
| command button | border `#E3C887` (empty: `#2A2F38`) | 2px `#E3C887`, offset 1px | locked/poor: cursor not-allowed, whole button .55 (locked) |
| console panels (3) | sets `--chiH:1`: rope tightens, laser etch appears | | transition .35 s |
| minimap map | cursor pointer | | pointer-down drags camera, Alt-click pings |
| toast | | | `Space` jumps to the idle Acolyte |

Hotkeys displayed: Q W E R / A S D F / Z X C V on the command card, Space (jump to alert), F10 (menu), Alt V / Alt click / Alt T (minimap buttons), 3 (control group), Numpad +/- (game speed, host only). Esc also opens the menu (tooltip text).

## 12. Slate: what must be reproduced, ranked by difficulty

Evidence for primitives is from `Engine/Source/Runtime/SlateCore/Public` in the 5.8.3 install. "A" = trivial with stock widgets, "D" = needs custom work or a decision.

| rank | feature | count in board | how | evidence / caveat |
|---|---|---|---|---|
| A1 | solid rectangles, 1px borders | ~60 | `SBorder`/`SImage` with `FSlateColorBrush`, or `FSlateRoundedBoxBrush` with outline | outline width is a single float, `FSlateBrushOutlineSettings` (`Styling/SlateBrush.h:135-147`). Whole-pixel snap the half-pixel chips |
| A2 | text, 3 families, 8 sizes | ~45 runs | `STextBlock`, FullShaping, Size = px x .75 | section 10 |
| A3 | dashed 1px border (empty slot) | 1 | `FSlateDrawElement::MakeDashedLines` x4, or a 9-slice texture | `Rendering/DrawElementTypes.h` lists `MakeDashedLines`; no dashed box brush |
| A4 | per-element alpha (rgba fills) | ~8 | colour alpha | |
| B1 | keycaps: 1px sides, 2px bottom, radius 2-3 | 14 | two stacked boxes (outline brush is uniform width) | |
| B2 | line icons (18) | 24 | `FSlateVectorImageBrush` from `.svg` (`Brushes/SlateImageBrush.h:85`, macro `IMAGE_BRUSH_SVG`) or pre-rasterised SDF/PNG atlas; tint via brush colour | **UNVERIFIED** that SVG brush rendering works in a packaged game and that 1.5/24 strokes stay crisp at 12-16px; PNG at 2x is the safe fallback |
| B3 | whole-widget opacity .55 (locked buttons) | 2 | colour-and-opacity propagation | Slate multiplies per element, not as a flattened group: overlaps (keycap over icon) differ slightly |
| B4 | linear gradient, vertical, 3 stops (portrait silhouette) | 1 | `FSlateDrawElement::MakeGradient` supports N stops, horizontal/vertical only (`Rendering/DrawElementTypes.h:180,396`) | fine for 180deg |
| B5 | selection ring ellipse, 3px + 2px outer + inner hairline | 1 | not Slate in practice: decal or screen-space material | `RoundedBox` with half-height radius gives a stadium, not an ellipse |
| B6 | 2px timer bar, hp bars | 4 | box + box | |
| B7 | rope tile strips (repeat-x, mirrored phase, masked width) | 6 | tiled image brush (`ESlateBrushTileType::Horizontal`) | phase for the right half needs an offset; alternatively one image per half |
| C1 | box shadows: soft drop `0 3px 6px`, inset `0 2px 6px`, 1px bevels | 3 on 2 elements | pre-baked 9-slice textures for the blurred ones; the 1px bevels are plain 1px lines | **no blurred drop/inset shadow primitive in Slate**: `Rendering/DrawElementTypes.h` offers Box, RotatedBox, Gradient, Spline, CubicBezierSpline, Lines, DashedLines, Text, ShapedText, Custom, CustomVerts, Viewport, GeometryOutline and PostProcessBlur (that blurs what is behind a region, it is not a shadow) |
| C2 | radial gradients: vat glow, minimap fog, world vignette | 3 | pre-baked texture or a tiny UMG/Slate material | `MakeGradient` is linear only. Vignette/world is not UI in Unreal |
| C3 | polygon `clip-path` (portrait silhouette) | 1 | not needed: portrait becomes a render target; for the trial bake the silhouette to a PNG | Slate clipping is axis-aligned rectangles only (`Layout/Clipping.h`) |
| C4 | sigils: SVG with dotted strokes, hexagram, 5 distinct per panel, entrance spin | 10 visible | texture atlas (README says one atlas, `levers.js:19`) | dotted ring needs the dash pattern baked |
| C5 | laser head pan on a 1px line + etch layers | 3 | material with a panner on a masked 1px quad | animated; static frame shows only a short bright segment |
| C6 | `clip-path` reveal animations | 11 | material "Reveal" scalar or `SBox` width animation | `README.md:137`: designed as a material scalar |
| D1 | minimap content (live map, fog, unit dots, camera rect) | 1 panel | scene-capture render target or a dedicated draw | mockup shows a crop of the world photo; in Unreal it must be generated. Out of scope for pixel match except the frame |
| D2 | selection portrait (3D vat) | 1 | render target | mockup uses a flat silhouette placeholder |
| D3 | pixel-faithful text | all | accept ~1px differences | section 10 |

Everything marked A and B1-B4, B6 can be done with stock Slate in a few hundred lines. C-items are where a custom `SLeafWidget` (OnPaint with `FSlateDrawElement`) or a small material set is justified.

## 13. Verification probes (for the Unreal screenshot comparison)

Exact RGB at board pixels in `board-3.1a-hud-only-on-14161A-1920x1080.png` (compare after painting the Unreal world `#14161A`):

| probe | (x,y) | RGB |
|---|---|---|
| top strip fill | (4,20) | `#1C1F25` |
| top strip bottom border | (500,39) | `#3A3F48` |
| strip below border (world) | (500,40) | `#14161A` |
| chip fill / chip left border | (12,20) / (8,20) | `#14161A` / `#3A3F48` |
| chip top / bottom border rows at x=60 | y=6 / y=33 | `#3A3F48` |
| minimap panel fill / top border / right border | (4,1000) / (20,864) / (255,1000) | `#1C1F25` / `#8A6E3C` / `#3A3F48` |
| minimap plate / plate border / inner line / map border | (46,1000) / (44,1000) / (48,1000) / (52,1000) | `#262A31` / `#8A6E3C` / `#4A4234` / `#0B0C0E` |
| minimap button fill / border | (12,885) / (8,895) | `#14161A` / `#3A3F48` |
| tab fill / top border / left border column | (272,890) / (290,874) / (268,890) | `#2A2F38` / `#E3C887` / `#E3C887`; tab spans x 268..327 |
| selection panel fill / top border | (262,1000) / (900,904) | `#1C1F25` / `#8A6E3C` |
| portrait border / vat top / vat bottom | (274,1000) / (336,925) / (336,1040) | `#8A6E3C` / `#191C21` / `#3A4959` |
| hp frame / fill | (418,1015) / (600,1015) | `#3A3F48` / `#4FB39A` |
| command card fill | (1625,1000) | `#1C1F25` |
| normal button fill / border | (1640,930) / (1633,900) | `#14161A` / `#4A4234` |
| active button fill / border | (1640,1060) / (1633,1040) | `#173129` / `#4FB39A` |
| locked button fill / border (composited) | (1780,930) / (1773,900) | `#181A1F` / `#2D3138` |
| keycap fill / bottom border | (1639,890) / (1645,896) | `#0B0C0E` / `#5C5446` |
| toast timer bar | (100,850) | `#C9A86A` |
| ring left edge | (992,560) | `#4FB39A` |
| rope rows at x=40 | y 863..866 | `#58482D` `#83683A` `#3D3526` `#454035` (anti-aliased) |

Suggested pass criteria: geometry (box edges) exact to 1px, flat colours exact, text boxes within 1px and 3% width, ornament frame by eye plus mean error on the 9px-high strip.

## 14. Gaps, inconsistencies and things I could not verify

- **UNVERIFIED:** whether `FSlateVectorImageBrush` renders `.svg` in a packaged runtime build; whether UE 5.8 supports variable fonts anywhere in the engine; Slate's pixel snapping behaviour for fractional `FGeometry` positions; the hover-state numbers in 8.1 (derived from `levers.js`, I did not hover in the browser).
- The README's top-bar shadow (`0 1px 0 #0E0F12`, `README.md:121`) is not in the 3.1a DOM. The board wins.
- The repo's reference world is missing; the repo render looks empty. Decide whether the Unreal comparison uses the HUD-only matte (recommended) or a world render.
- The static TTFs are Google's per-weight instances; Chromium rendered the variable woff2. Glyph outlines at the same weight should match; I only checked advance widths, not rasterised glyph shapes.
- One element, node 216, duplicates node 206; the empty `rate` node in the supply chip and the empty mana-text node in the hp row take no space (the former adds a 7px gap).
- 3.1a never shows: mana bar, XP row, inventory, queue, tiles, cooldowns, progress bars, banners, chat, pings, hero tab. Their styles are in the template (`Match.dc.html:61-219`) and in section 5 where noted; they are needed for the full HUD, not this check.
- `title` tooltips are native browser tooltips, so their on-screen look is not in the board; section 5.9 is the closest approved spec.
- `reducedMotion`, `uiOrnament = Off/Subtle`, and `lvMaster < 5` change ornament levels; the numbers here are for the approved defaults only.

## 15. Reproduce

1. Serve a copy of `docs/ui-redesign` that includes `assets/bf-village.jpg` (`python -m http.server <port>`), confirm HTTP 200 on `/Match.dc.html`.
2. `playwright-cli open http://localhost:<port>/Match.dc.html`, `resize 2200 1300`, `find "Show only 3.1a"` and click its ref, `snapshot --boxes` to find the 1920x1080 board ref.
3. `eval` `document.getAnimations().forEach(a=>{a.pause();a.currentTime=3000})`, then `screenshot <board ref> --filename=...`.
4. Extraction: `run-code` with an in-page walker over the board subtree (`getBoundingClientRect` minus the board rect, `getComputedStyle`, Range rects for text nodes, `CSS.getPlatformFontsForNode` via CDP). The matte: hide the world layer, screenshot on `#000` and on `#fff`, alpha = 1 - mean(white - black)/255.
5. Servers and browser were stopped afterwards.
