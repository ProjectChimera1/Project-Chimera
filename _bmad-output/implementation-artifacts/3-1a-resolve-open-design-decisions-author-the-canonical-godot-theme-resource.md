---
baseline_commit: a56d11de5001f941e35ad0c5b4cdcbae5853b6ab
---

# Story 3.1a: Resolve open design decisions + author the canonical Godot Theme resource

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a creator-tools developer,
I want the two open design decisions (UX-DR4 accent-switch, UX-DR9 chamfer StyleBox) resolved and one canonical Godot `Theme` resource encoding every design token,
so that every component (3.1b/3.1c) and every later editor styles itself from a single committed source of truth.

## Context & Scope — read first

**This is a work-type pivot.** Stories 1.1–2.13 were ~70% simulation/determinism. Story 3.1a is the **first pure-presentation story** and the foundation of the entire UI design-system workstream. It touches **NO simulation code**. There is **no `SimChecksum`, no golden checksum, no fold, no `AlgoVersion` bump, no determinism fence** to move — none of the Epic 1/2 machinery applies here. If you find yourself thinking about checksums, you are in the wrong story.

**What 3.1a delivers (the whole story):**
1. Resolve **UX-DR4** (runtime accent-switch mechanism) and **UX-DR9** (chamfer StyleBox mechanism) — documented in-code with rationale.
2. Add the **3 required fonts** (absent from the repo today — a hard prerequisite).
3. Author **one committed `main.theme`** encoding UX-DR1..UX-DR12 tokens + the UX-DR34 mono tabular-number role.
4. Deliver the **two decision mechanisms as working code** (the chamfer recipe + the accent-switch controller) — the AC requires the decisions "documented in-code," and 3.1b consumes these directly.
5. **Verify in-engine** via `/godot-verify` (a small throwaway preview scene). A Godot `Theme` is a Godot resource — it **cannot** be tested in the Godot-free Tier-1 xUnit tier, so `/godot-verify` is the primary gate (Epic 2 retro §5).

**What 3.1a explicitly does NOT do (scope fences — do not cross):**
- **Does NOT build any component** (panel, btn, kbd, chip, readout, tag, progress, slider, input, tabs, list-row, num-input) — that is **Story 3.1b**. 3.1a authors the token vault + the two mechanisms + a proof; the component kit is next.
- **Does NOT build the composite/feedback components** (menu, tooltip, dialog, toast, spinner, mark, switch) or the demo gallery — that is **Story 3.1c**. The Chimera Seal mark and transmute spinner (UX-DR29/30) live only as JS-generated inline SVG in the mock's `theme.js` today; re-authoring them is 3.1c's job, not 3.1a's.
- **Does NOT restyle the ~10 existing hand-styled panels**, and **does NOT set the project-global default theme** (`project.godot` `gui/theme/custom`). Applying the design system to the front-end shell is **Story 3.11**; applying it to editor panels is 3.3–3.7. Wiring it globally now would instantly and unpredictably restyle every existing panel (which also carry per-control overrides) mid-epic. Author the resource; leave application to the stories that own it.
- **Does NOT deliver the light theme** (warm-paper). That is **UX-DR37**, a separate later story flagged blocked-pending-values in the epics. 3.1a is **dark-theme only** — its coverage is UX-DR1..UX-DR12 + UX-DR34. (The light-theme token values do exist in `chimera.css:142–176` for that future story; ignore them here.) Note: UX-DR4's three **accents** (teal/amber/violet) are all in-scope — they are dark-theme accent palettes, distinct from the light *theme*.

## Acceptance Criteria

**AC1 — the two decisions are resolved and documented in-code.** UX-DR4 (accent-switch mechanism) and UX-DR9 (chamfer StyleBox mechanism) are each resolved to a single canonical choice, with rationale recorded in-code (doc comments / a `DESIGN-DECISIONS.md` beside the theme). UX-DR4 names one accent-switching mechanism; UX-DR9 names one chamfer implementation. (See **Decisions** below — recommended defaults are baked; confirm with Alec before/at dev time.)

**AC2 — the chamfer renders faceted, not rounded.** The chosen UX-DR9 mechanism produces a **45° faceted (chamfered) corner**, verified visually in-engine — provably *not* a rounded corner and *not* a square corner. The brand's canonical 2-corner (top-left + bottom-right) cut is reproduced at the `cut`/`cut-sm`/`cut-lg` sizes.

**AC3 — accent switches at runtime.** Switching the accent teal → amber → violet retints every accent-bound surface **in one operation**, verified in-engine across all three states. No accent-tinted surface is left on the old accent after a switch (this is the seam that quietly breaks — see UX-DR4 notes).

**AC4 — all color/spacing/shape tokens map 1:1 into `main.theme`.** Every UX-DR1..UX-DR12 token is present in the single committed `main.theme` with the **exact values in the Canonical Token Table** below (oklch tokens pre-converted to sRGB — Godot has no oklch). Team colors (UX-DR6) are present in the vault **but reserved** — applied to no UI chrome anywhere.

**AC5 — typography + the mono tabular role.** The 3 fonts (Chakra Petch / Space Grotesk / JetBrains Mono) are imported and wired as the display/ui/mono roles; the type scale (UX-DR8) is encoded; **UX-DR34** — a JetBrains Mono role with **tabular figures** (`tnum`) for all live numbers — is defined in the theme.

**AC6 — clean scope + in-engine proof.** No simulation file, no checksum, no golden, and no existing panel/shell is modified. `godot.csproj` builds 0 errors. `/godot-verify` PASSES: the preview scene loads `main.theme`, shows every token, renders a faceted chamfer, displays all 3 fonts + a mono tabular readout, and switches accent across teal/amber/violet.

## Decisions (recommended defaults — confirm with Alec)

Following the Epic 2 recommended-default pattern. Each is the recommendation; alternatives noted. All are the "recommended" option and can be taken as written unless Alec vetoes.

- **D-1 (UX-DR9 mechanism) — Native `StyleBoxFlat` with `corner_detail = 1` + `anti_aliasing = false`.** *(Recommended.)* **This overturns the epics.md ⚠ assumption** that a custom StyleBox / texture / shader is required. Verified against the Godot 4.6 class reference: `corner_detail = 1` turns the per-corner `corner_radius_*` into **straight 45° chamfers** ("A corner detail of 1 will result in chamfered corners instead of rounded corners"). Zero custom code, tints from `bg_color`/`border_color` tokens, fully git-diffable in the `.tres`. **Alternative:** a custom `ChamferStyleBox : StyleBox` overriding `_Draw(Rid, Rect2)` — reserve this ONLY if a later component needs non-45° / multi-facet / notched corners (log it if so). **This resolves the UX-DR9 blocker and unblocks 3.1b/3.1c.**
- **D-2 (UX-DR9 shape) — Reproduce the mock's 2-corner top-left + bottom-right chamfer.** *(Recommended.)* The shipped Claude Design UI cuts only TL+BR (`chimera.css:213`), leaving TR+BL square — the distinctive low-poly diagonal. Achieved with `corner_radius_top_left = corner_radius_bottom_right = <cut>`, `corner_radius_top_right = corner_radius_bottom_left = 0`, `corner_detail = 1`. Fidelity to the shipped UI is the design intent (UX D1: distill, don't redesign). **Alternative:** symmetric all-4-corner chamfer (simpler, less distinctive). Per-corner and trivially adjustable either way.
- **D-3 (UX-DR4 mechanism) — One canonical Theme; switch by mutating the ~6 accent `Color` items on the live Theme.** *(Recommended.)* Verified in the 4.6 engine source: `Theme.SetColor(...)` emits `changed` → `NOTIFICATION_THEME_CHANGED` cascades a repaint down every Control using the theme; the connection is `CONNECT_DEFERRED`, so rewriting all 6 accent entries in a loop coalesces into a **single** end-of-frame repaint. DRY (one theme file, not three), engine-native. **Alternatives:** 3 separate `.theme` files (duplicates every non-accent token — sync hazard); root `AddThemeColorOverride` (shadows the theme, opposite of one-source-of-truth); shader uniform (ignores the theme system). **Critical caveat baked into Task 5** — see UX-DR4 notes.
- **D-4 (token storage) — A custom theme-type "vault" that components read by name.** *(Recommended.)* Store every named token as an entry under one custom theme type (e.g. `"Chimera"`): `theme.SetColor("surface-1", "Chimera", ...)`; components later call `GetThemeColor("surface-1", "Chimera")`. Plus set the theme's `default_font` + `default_font_size` so text inherits globally. **3.1a stops there** — configuring each stock control type's full look (Panel/Button/LineEdit styleboxes) via **type variations** is 3.1b's job. **Alternative:** configure all stock control defaults now (over-reaches into 3.1b).
- **D-5 (home) — `godot/assets/ui/main.theme` + `godot/assets/ui/fonts/`.** *(Recommended.)* The repo convention is `assets/` = binary imported source art, `resources/` = data/config. Fonts are binary source art; `godot/assets/ui/` already exists and is **empty and reserved**. Co-locate the theme with the fonts it references. **Alternative:** `godot/resources/ui/`.
- **D-6 (fonts) — Bundle the 3 OFL static TTFs.** *(Recommended, hard prerequisite.)* All three are SIL Open Font License (free to bundle/ship): **Chakra Petch** (weights 400/500/600/700), **Space Grotesk** (400/500/600/700), **JetBrains Mono** (400/500/700) — the exact weights the mock uses. Include each font's `OFL.txt` license. The mock pulled them from the Google Fonts CDN (`chimera.css:6`); there are no local binaries today.
- **D-7 (application scope) — Author + preview-verify only; defer all real-surface application.** *(Recommended.)* Do not set the project-global default theme and do not restyle existing panels (Story 3.11 + editor stories own that). Prevents mid-epic destabilization of the 10 working panels.
- **D-8 (verification gate) — `/godot-verify` on a throwaway preview scene is the gate.** *(Recommended.)* A Godot `Theme` cannot load in Godot-free Tier-1 xUnit. Optionally add a small **Tier-2 GdUnit4** load-test (`godot/tests/`) that `GD.Load<Theme>`s `main.theme` and asserts a few key entries — nice-to-have teeth, not the gate. Primary proof is visual/in-engine per Epic 2 retro §5.

## Tasks / Subtasks

- [x] **Task 1 — Resolve + document the two decisions (AC1)**
  - [x] Write a short `godot/assets/ui/DESIGN-DECISIONS.md` (or XML doc comments on the theme-owning class) recording D-1/D-2 (UX-DR9 = `StyleBoxFlat` `corner_detail=1`, TL+BR cut, rationale) and D-3 (UX-DR4 = live-Theme accent-color mutation, rationale + the StyleBox caveat).
  - [x] Confirm the recommended defaults with Alec if he hasn't already signed off (they can be taken as written otherwise).
- [x] **Task 2 — Add the 3 fonts (AC5, prerequisite)**
  - [x] Fetch the OFL TTFs at the weights in D-6 (Chakra Petch, Space Grotesk, JetBrains Mono) — from Google Fonts or the upstream OFL repos.
  - [x] Place under `godot/assets/ui/fonts/` (one subfolder per family); include each `OFL.txt`. Let Godot import them (creates `FontFile` + `.import` sidecars).
  - [x] Verify each imports as a usable `FontFile` (no import errors in the editor log).
- [x] **Task 3 — Author `main.theme` token vault (AC4, AC5)**
  - [x] Create `godot/assets/ui/main.theme` (text `.tres`, `type="Theme"`).
  - [x] Add every color token from the **Canonical Token Table** under the `"Chimera"` theme type via `SetColor(name, "Chimera", Color.Html("#..."))` — surfaces (UX-DR1), lines (UX-DR2), text (UX-DR3), accent-teal (UX-DR4, the default palette), semantic + `*-ink` (UX-DR5), and the 8 team colors (UX-DR6, **reserved — do not style any chrome with them**).
  - [x] Set `default_font = Space Grotesk`, `default_font_size = 15` (t-md); register the 3 font roles (`font-display`/`font-ui`/`font-mono`) as named `Font` items under `"Chimera"`.
  - [x] Encode the UX-DR8 type scale as `font_size` items (`t-2xs 11 … t-5xl 72`) and UX-DR10 spacing (`s1 4 … s8 64`), UX-DR9 cut sizes (`cut 8 / cut-sm 5 / cut-lg 14`), and the motion constant (`speed 130`) as `constant` items under `"Chimera"`.
  - [x] Record the UX-DR11 shadow recipes (`shadow-1/2/pop` → `shadow_size`/`shadow_offset`/`shadow_color`, values in the table) as documented constants; realize `shadow-1` on the preview panel in Task 6 (full per-component shadows are 3.1b).
- [x] **Task 4 — UX-DR34 mono tabular role (AC5)**
  - [x] Define the mono readout role as a `FontVariation` over **JetBrains Mono** with OpenType `tnum = 1` (reuse the exact pattern at `CommandCardSystem.cs:362-371`, but based on JetBrains Mono instead of the default font). Store it as a named `Font` item (e.g. `mono-tnum`) under `"Chimera"`.
- [x] **Task 5 — Accent-switch mechanism (AC1, AC3) — the UX-DR4 deliverable**
  - [x] Add `godot/src/UI/Theme/ThemeTokens.cs` (or similar): `StringName` constants for token names + the **3 accent palettes** as data (teal/amber/violet, 6 colors each — values in the table).
  - [x] Add `AccentController` (a small node/service): `SwitchAccent(name)` rewrites the 6 accent `Color` items on the live `main.theme` (`accent`, `accent-bright`, `accent-dim`, `accent-ink`, `accent-glow`, `accent-wash`).
  - [x] **CRITICAL:** also retint any **accent-tinted `StyleBoxFlat`** — its `BgColor`/`BorderColor` are sub-resource properties, **not** theme Color tokens, so they do NOT follow the accent Color entries. `AccentController` must own/register those styleboxes and set their colors in the same switch (mutating a StyleBox also emits `changed` and rides the same repaint). This is where "everything reads the accent" silently breaks if missed.
- [x] **Task 6 — Preview/verify scene (AC2, AC3, AC6)**
  - [x] Build a throwaway preview scene (e.g. `godot/scenes/theme_preview.tscn` or a code-built scene) that assigns `main.theme`, then shows: a swatch grid of every token; a `PanelContainer` styled with the faceted chamfer StyleBox (proving AC2); Labels in all 3 fonts + a mono tabular readout (e.g. `1234567890` aligned); and three buttons that call `AccentController.SwitchAccent`.
  - [x] This scene is a proof harness, not a shipped surface — keep it minimal; the real gallery is 3.1c.
- [x] **Task 7 — Build + `/godot-verify` (AC6)**
  - [x] `dotnet build godot/godot.csproj` → 0 errors.
  - [x] Run `/godot-verify` on the preview scene. Capture screenshots of the chamfered panel (AC2 — faceted, not rounded) and of all three accent states (AC3). Confirm fonts render and token colors match.
  - [x] **A3 teeth (prove the gate has teeth):** momentarily set `corner_detail = 8` (or a nonzero radius with detail 8) to show the corner goes *rounded* → revert; and capture the accent actually changing across all 3 states (not a static screenshot). Record the inject→observe→revert in the Dev Record.
- [x] **Task 8 — Scope-fence check (AC6)**
  - [x] `git diff --stat` confirms: only `godot/assets/ui/**`, the new `src/UI/Theme/**`, the preview scene, and docs changed. **Zero** files under `src/Core`, `src/Combat`, `src/Economy`, `src/Navigation`, `src/Multiplayer` or any test golden. No `project.godot` `gui/theme` change. No existing panel `.cs` restyled.

## Dev Notes

### Verification posture — the important reset
No `SimChecksum` / golden / fold / `AlgoVersion` / stamp movement exists in this story. Determinism is untouched because nothing sim is touched. The Epic 2 retro (§5) explicitly calls this out: Epic 3 is the UI pivot, and **`/godot-verify` + `/check-site` become the primary verification gates**. Do not add checksum tests; there is nothing to checksum. The "teeth" (Epic 1 A3, carried) here are visual: prove the chamfer is faceted by contrasting against what rounded looks like, and prove the accent switch by capturing all three states.

### Canonical Token Table (THE reference — oklch pre-converted to sRGB)
`chimera.css` and `DESIGN.md` give accent/semantic colors **only as `oklch(...)`**; Godot `Color` is sRGB with **no oklch support** (it has OKHSL, a *different* model — do not use it to consume these). Values below are the exact oklch→sRGB conversions. Enter them with `Color.Html("#rrggbb")` / `Color.Html("#rrggbbaa")`.

**Surfaces (UX-DR1, hex from css):** `void #0a0c0f` · `surface-0 #0f1216` · `surface-1 #14181d` · `surface-2 #1a1f26` · `surface-3 #222831` · `surface-4 #2c333d`
**Lines (UX-DR2, hex):** `line #2a3038` · `line-strong #3a424d` · `edge-light #4a5562`
**Text (UX-DR3, hex, AA-locked on surface-1):** `text-hi #eef2f6` · `text-mid #aeb7c2` · `text-lo #727c88` · `text-disabled #4b545f`
**Accent — TEAL (default, UX-DR4, converted):** `accent #1ed1cd` · `accent-bright #4cece7` · `accent-dim #1f9996` · `accent-ink #04201e` (hex) · `accent-glow #1ed1cd47` · `accent-wash #1ed1cd1f`
**Accent — AMBER (converted):** `accent #f2af48` · `accent-bright #ffcb63` · `accent-dim #b77f39` · `accent-ink #271700` (hex) · `accent-glow #f2af484c` · `accent-wash #f2af481f`
**Accent — VIOLET (converted):** `accent #b296ff` · `accent-bright #cfb2ff` · `accent-dim #8168be` · `accent-ink #170a2b` (hex) · `accent-glow #b296ff4c` · `accent-wash #b296ff21`
**Semantic (UX-DR5, accent converted, ink hex):** `ok #6ed274` (ink `#06210f`) · `warn #f0b135` (ink `#241803`) · `danger #f05653` (ink `#2a0606`) · `info #65b4e9`
**Team (UX-DR6, hex, RESERVED — world units/minimap only, NEVER chrome):** `team-1 #2a7fd4` · `team-2 #e06a1b` · `team-3 #16a37a` · `team-4 #cf72ad` · `team-5 #5cb8ec` · `team-6 #f0c000` · `team-7 #9a6cf0` · `team-8 #9aa3ad`
**Typography (UX-DR7):** display = Chakra Petch · ui = Space Grotesk (body default) · mono = JetBrains Mono
**Type scale px (UX-DR8, ratio 1.250):** `t-2xs 11` · `t-xs 12` · `t-sm 13` · `t-md 15` (body) · `t-lg 18` · `t-xl 23` · `t-2xl 29` · `t-3xl 37` · `t-4xl 52` · `t-5xl 72`; eyebrow = 11px uppercase 0.22em
**Chamfer cuts px (UX-DR9):** `cut 8` (panels) · `cut-sm 5` (btn/input/chip/menu) · `cut-lg 14` (dialogs). Per-component extras used later (3.1b/c): 3px (tag/readout/num-input), 2px (progress), 4px (tooltip/thumb), 5px (list-row/segment), 6px (banner-stall). **`.kbd` is the sole rounded element** (radius 3px, `corner_detail` default) — the documented exception to "no rounded corners"; the component itself is 3.1b.
**Spacing px (UX-DR10):** `s1 4` · `s2 8` · `s3 12` · `s4 16` · `s5 24` · `s6 32` · `s7 48` · `s8 64`. Control padding ≈ s2/s3; panel ≈ s4/s5; section gaps ≈ s5/s6.
**Shadows (UX-DR11) → StyleBox `shadow_*` (dark; css two-layer, keep the drop layer; css blur ≈ 2× Godot `shadow_size`, spread 0):** `shadow-1` size≈7 offset (0,4) `Color(0,0,0,0.45)` · `shadow-2` size≈15 offset (0,10) `Color(0,0,0,0.55)` · `shadow-pop` size≈25 offset (0,18) `Color(0,0,0,0.65)`. The css inset top-highlight layer is faked by the `edge-light` hairline border, not a second shadow.
**Motion (UX-DR50, low-materiality fold-in):** `speed 130ms`, ease `cubic-bezier(0.4,0.1,0.2,1)` — store `speed` as a constant; easing is applied by animated components later.

### UX-DR9 — chamfer resolution details
The brand chamfer is `chimera.css:213`: `polygon(0 var(--c), var(--c) 0, 100% 0, 100% calc(100% - var(--c)), calc(100% - var(--c)) 100%, 0 100%)` — a **TL + BR 2-corner 45° cut**, TR + BL left square. Reproduce with a `StyleBoxFlat`:
```
corner_radius_top_left     = cut     # e.g. 8
corner_radius_bottom_right = cut
corner_radius_top_right    = 0
corner_radius_bottom_left  = 0
corner_detail              = 1       # <-- makes the "radius" a straight 45° chamfer (NOT rounded)
anti_aliasing              = false   # recommended with corner_detail=1 for crisp facets
bg_color                   = <surface token>
border_color               = <edge-light / line token>   # cel-shade hairline
border_width_*             = 1
```
`corner_detail` is global per stylebox (can't mix rounded + chamfered in one box), and the bevel is always symmetric 45° — both fine for this brand. The cel-shade top-edge highlight (UX-DR2 `edge-light`) is the border; a full gradient-backplate hairline is a 3.1b refinement. **Do not** reach for `StyleBoxTexture` (bakes color into a binary PNG — not diffable, single-modulate tint) or a custom `StyleBox` subclass (unnecessary here; `_Draw(Rid, Rect2)` is the override if a future non-45° need arises — note there is no `_GetContentMargins` virtual; content margins are the `content_margin_*` properties).

### UX-DR4 — accent-switch resolution details
Runtime switch = mutate the ~6 accent `Color` items on the one live `main.theme`. Confirmed in engine source (`scene/gui/control.cpp`, 4.6-stable): `Control.set_theme()` connects to the theme's `changed` signal with `CONNECT_DEFERRED`; every `Theme.SetColor(...)` emits `changed` → `NOTIFICATION_THEME_CHANGED` (value 45) propagates + queues redraw down the subtree; deferred connection means a 6-call loop coalesces into ONE repaint. Components must **read the shared entry** (`GetThemeColor("accent", "Chimera")`), **never a literal**.
- **The seam that breaks (Task 5 critical):** a chamfered *surface* gets its fill/border from a `StyleBoxFlat`'s `BgColor`/`BorderColor` — **sub-resource properties, not theme Color tokens** — so they do not auto-follow the `accent` Color entry. `AccentController` must retint those styleboxes too (StyleBox is a `Resource`; mutating it emits `changed` and rides the same cascade). Keep a registry of accent-tinted styleboxes, or prefer routing accent onto surfaces via theme Color items controls read directly (font colors, icon `modulate`) where possible. Decide the binding explicitly and prove it in the preview (an accent-filled chamfered button must retint on switch).
- API note: the setter is `Theme.SetColor(name, themeType, color)` — **themeType is the middle arg**. Type variations (3.1b) use `Theme.SetTypeVariation(variation, base)` (NOT `SetTypeVariationBase`) + `Control.ThemeTypeVariation`.

### Godot `Theme` authoring facts (4.6.3)
- Save as **text `.tres`** (`type="Theme"`, `format=3`) — git-diffable. Items key as `<ThemeType>/<datatype>/<name> = <value>`; datatypes: `colors`, `constants`, `fonts`, `font_sizes`, `icons`, `styles`. StyleBoxes/Fonts serialize as inline `[sub_resource]` or `[ext_resource]`.
- C# authoring: `SetColor/SetConstant/SetFont/SetFontSize/SetStylebox/SetIcon(name, themeType, value)`; generic `SetThemeItem(DataType, name, themeType, value)`.
- Color: sRGB float RGBA; use `Color.Html("#rrggbb"|"#rrggbbaa")` or `Color.Color8(r,g,b,a)`. No linear/sRGB conversion needed for Control colors (Forward+ 3D renderer does not change 2D UI color handling).
- Apply via **code** (`Control.Theme = GD.Load<Theme>("res://assets/ui/main.theme")`), not the scene inspector — all Chimera panels are code-built, no `.tscn` UI.
- **Authoring approach:** don't hand-type ~60 entries in the `.tres` (error-prone). Author either in the Godot editor's built-in **Theme panel** (visual) or via a one-time C# builder that populates a `Theme` with `SetColor/SetConstant/SetFont/SetFontSize` from the Canonical Token Table and `ResourceSaver.Save("res://assets/ui/main.theme")`s it. The committed artifact is the text `.tres` either way; a builder is more reproducible and reviewable.

### Existing-code context (what's there today)
- **No Theme/`.theme`/`.tres` design resource exists** — confirmed. `AbilityEditorPanel.cs:21` literally: *"no Theme resource exists yet — Epic 3 builds it."* Two empty placeholder `new Theme()` instances mark the future injection point: `TriggerEditorPanel.cs:114`, `MapGeneratorPanel.cs:105` (do not need to touch them in 3.1a).
- **The de-facto tokens already exist as inline data** you are formalizing: `AbilityEditorPanel.cs:33-42` (10 named house colors — `PanelBg`, `CardBg`, `CardBorder`, `FieldBg`, `HeaderBlue`, `BodyText`, `DimText`, `HintText`, `OkGreen`, `ErrRed`), re-typed by hand across ~10 files (`SettingsPanel`, `ContentBrowserPanel`, `MainMenuOverlay`, `HudPhase`, `CommandCardSystem`, `SelectionSystem`, `EntityPlacer`, `MainScene`, etc.). This duplication is what the Theme centralizes — but **migrating those panels is NOT this story** (3.11 + editor stories).
- **Every current panel uses rounded `CornerRadius`** (the `Card()` factory at `AbilityEditorPanel.cs:690`, and inline `StyleBoxFlat` in every other panel) — the *opposite* of the chamfer brand. Your new faceted StyleBox is the corrected pattern the component kit will adopt.
- **Fonts are 100% the Godot default** — no `.ttf` anywhere. The only existing mono-figure handling is a `FontVariation` with `tnum` over the *default* font at `CommandCardSystem.cs:362-371` — reuse that exact pattern for UX-DR34, but base it on JetBrains Mono.
- The `assets/ui/` dir exists and is **empty** (reserved for UI art) — the recommended home (D-5).

### Cross-story continuity
- **3.1b** (next) builds the simple component kit (panel/btn/kbd/chip/readout/tag/progress/slider/input/tabs/list-row/num-input) — it reads this theme's vault, uses the faceted-StyleBox recipe (D-1/D-2), binds accent via the shared entries (D-3), and should absorb the duplicated per-file helpers (`AddSectionHeader` ×2, `MakeLabel` ×2, `Card()`, `MakeLabeledRow`, `AddSpinRow`, `AddDropdownRow`, etc.) into Theme type-variations + reusable components.
- **3.1c** builds composite/feedback components (menu/tooltip/dialog/toast/spinner/mark/switch) + the demo gallery + re-authors the Chimera Seal mark and transmute spinner (currently only in the mock's `theme.js`).
- **3.11** applies the design system to the front-end shell (title/mode-select/settings) — the first *real-surface* application; **3.3–3.7** apply it to editor panels. This is where the existing hand-styled panels get migrated and the global default theme may be set.
- Deliver the token-name vocabulary (`ThemeTokens` StringName constants) cleanly — every later editor story depends on it.

### Anti-patterns to avoid (LLM-dev traps)
1. **Rounded corners.** Never `corner_detail = 8` (default) or a nonzero radius without detail=1 on brand surfaces. The one exception is `.kbd` (3.1b).
2. **oklch or OKHSL in the theme.** Godot has no oklch; OKHSL ≠ oklch. Use the pre-converted sRGB hex in the table via `Color.Html`.
3. **Literal accent colors in the preview/components.** Read `GetThemeColor("accent", "Chimera")`; a literal won't retint on switch (breaks AC3).
4. **Forgetting the StyleBox accent seam.** Accent-tinted `StyleBoxFlat` colors don't follow theme Color tokens — retint them explicitly (Task 5).
5. **Scope creep into 3.1b/3.1c/3.11.** No components, no gallery, no marks, no restyling existing panels, no global default theme, no light theme. Author the resource + the two mechanisms + a proof. Stop.
6. **Touching sim / adding a checksum test.** There is nothing deterministic here. `git diff --stat` must stay out of `src/Core|Combat|Economy|Navigation|Multiplayer` and out of every golden.
7. **Applying team colors to chrome.** UX-DR6 colors are reserved for world units — present in the vault, used on no UI element.

### Project Structure Notes
- New: `godot/assets/ui/main.theme`, `godot/assets/ui/fonts/{chakra-petch,space-grotesk,jetbrains-mono}/*.ttf` (+ `OFL.txt` + `.import`), `godot/assets/ui/DESIGN-DECISIONS.md`.
- New code: `godot/src/UI/Theme/ThemeTokens.cs` + `godot/src/UI/Theme/AccentController.cs` (PascalCase files matching class names, `ProjectChimera.UI.Theme` namespace, `#nullable enable`, `partial` if it inherits a Godot type). Presentation layer — `using Godot;` is allowed here (this is NOT sim code).
- Preview scene: `godot/scenes/theme_preview.tscn` (or code-built) — throwaway proof, not a shipped surface.
- No changes to `src/Core/*`, other panels, or `project.godot` gui settings.

### Project Context Rules (from project-context.md)
- **Sim/Presentation boundary is sacred** — this story is entirely presentation; that is correct and expected. Do not import anything from `src/Core` sim into the theme code beyond types you read for display.
- **Everything data-driven / one source of truth** — the Theme *is* the UI's data-driven source of truth (the platform rule applied to chrome). Map tokens 1:1 (project-context "Do map tokens 1:1 into a Godot `Theme`").
- **Godot C# gotchas:** classes inheriting a Godot type must be `partial`; use `GD.Print` not `Console.WriteLine` (presentation side); `#nullable enable` per file; PascalCase files/classes, camelCase locals, SCREAMING_CASE constants.
- **Layered complexity:** the theme is the simple/shared substrate; advanced per-control styling comes via type variations (3.1b) — don't hardcode what a token can express.
- **Brownfield discipline:** reuse — the `tnum` FontVariation pattern (`CommandCardSystem.cs:365`), the code-built-panel + `Control.Theme` injection pattern, the `assets/ui/` reserved dir. Don't build a parallel styling system.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story-3.1a] — story ACs, coverage (UX-DR1..12, UX-DR34), split rationale, the UX-DR9-blocks-3.1b/c note.
- [Source: _bmad-output/planning-artifacts/epics.md#Design-system-tokens] (lines 259–298) — canonical UX-DR1..UX-DR34 definitions incl. the ⚠ open decisions UX-DR4 (accent-switch unspecified) and UX-DR9 (StyleBox mechanism open).
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/DESIGN.md] — visual spine, token frontmatter, chamfer/StyleBox implementation note (line 178), "map tokens 1:1 into a Godot Theme."
- [Source: .../ux-Project_Chimera-2026-06-20/.decision-log.md] — D1 (distill, don't redesign the shipped Claude Design UI), D2 (teal default + amber/violet accents), D3 (Chimera Seal is the only alchemy motif that ships).
- [Source: .../mockups/project-chimera/project/chimera.css] — `:root` tokens (lines 20–99), amber/violet `[data-accent]` palettes (101–116), shadows (93–95), chamfer polygon (213). Accent/semantic are oklch-only; surfaces/lines/text/team/`*-ink` are hex.
- [Source: _bmad-output/game-architecture.md] (lines 198, 284) — "Claude Design System → Godot `Theme` (faceted `StyleBox`)", "UI = Control nodes + Theme", status "designed, impl pending".
- [Source: _bmad-output/project-context.md] — Sim/Presentation boundary, data-driven rule, Godot C# gotchas, conventions.
- [Source: _bmad-output/implementation-artifacts/epic-2-retro-2026-07-05.md §5] — Epic 3 work-type pivot; `/godot-verify` + `/check-site` as the primary UI verification gates; 3.10 recommended early.
- Godot 4.6 API: `Theme` / `StyleBoxFlat` (`corner_detail=1` → chamfer) / `Color` (sRGB, `Color.Html`, no oklch) / `Control` (`NOTIFICATION_THEME_CHANGED`, `ThemeTypeVariation`) — docs.godotengine.org/en/stable/classes.
- Existing code: `AbilityEditorPanel.cs:21,33-42,690` · `CommandCardSystem.cs:362-371` · `TriggerEditorPanel.cs:114` · `MapGeneratorPanel.cs:105` — no theme exists; house palette + rounded `Card()` + `tnum` variation to formalize/supersede.

## Dev Agent Record

### Agent Model Used
claude-opus-4-8 (Claude Opus 4.8), gds-dev-story workflow.

### Debug Log References
Two engine defects surfaced during in-engine verification and were fixed (both are documented in
`godot/assets/ui/DESIGN-DECISIONS.md` for 3.1b/3.1c):

1. **Hyphenated Theme item names are silently dropped.** The first `main.theme` save contained only the
   non-hyphen tokens (7 colors / 0 font_sizes / 10 constants). Root cause confirmed via `godot_exec`:
   `Theme::set_color("with-hyphen", …)` → `[theme.cpp:752] Invalid item name: 'with-hyphen'` and
   `has_color` = false; the underscore variant is accepted. Godot's `is_valid_item_name()` rejects `-`.
   **Fix:** all CSS token names map to underscore names (`surface-1` → `surface_1`, `t-md` → `t_md`,
   `accent-bright` → `accent_bright`) in `ThemeTokens`. Post-fix the theme has 34 colors / 4 fonts /
   10 font_sizes / 12 constants, 0 missing.
2. **`.theme` is Godot's binary extension.** `ResourceSaver.Save(theme, "…/main.theme")` wrote a binary
   `RSRC` blob (not git-diffable). **Fix:** the committed artifact is the text `.tres`
   (`[gd_resource type="Theme" format=3]`) at `godot/assets/ui/main.tres`.
3. Minor: `Color.Html` is GDScript; the C# name is `Color.FromHtml` (compile fix). The mandated
   namespace `ProjectChimera.UI.Theme` shadows the bare type `Theme`, so `Godot.Theme` is fully
   qualified in these files.

### Completion Notes List
- **What shipped:** the canonical `main.tres` token vault (UX-DR1..12 + UX-DR34, dark theme, teal
  default) built by a reproducible C# `ThemeBuilder` from a single `ThemeTokens` source of truth; the
  two open decisions resolved **as working code** — `ChimeraStyleBox.Chamfer` (UX-DR9 = `StyleBoxFlat`
  `corner_detail=1`, TL+BR cut) and `AccentController.SwitchAccent` (UX-DR4 = mutate the 6 accent Color
  items on the one live theme, **plus** retint registered accent StyleBoxes — the seam); the 3 OFL fonts
  bundled + imported; and a throwaway `theme_preview` proof scene.
- **All 6 ACs verified in-engine** (build 0 errors; ran `theme_preview` via the Godot MCP — the
  `/godot-verify` procedure):
  - AC2 — the surface panel renders a faceted 45° TL+BR chamfer; **A3 teeth**: toggled `corner_detail`
    1→8 to show the corner go *rounded* (screenshot contrast), then reverted. Inject→observe→revert done.
  - AC3 — teal→amber→violet each retint every accent surface in one op; the accent-filled chamfered
    button (the StyleBox seam) retints across all three (a Color-token-only switch would leave it stale);
    live token values match the table (amber `#f2af48`, violet `#b296ff`/`#cfb2ff`/wash `#b296ff21`).
  - AC4 — 34 colors + 12 constants + 10 font_sizes present with exact table values (verified via
    `godot_exec` + the swatch grid); team colors present in the vault, applied to **no** chrome.
  - AC5 — Chakra Petch / Space Grotesk / JetBrains Mono render as display/ui/mono; `mono_tnum`
    tabular-figure role aligns digit columns.
  - AC6 — `godot.csproj` builds 0 errors; the preview loads the committed `main.tres`.
- **Decisions (recommended defaults, taken as written per the Epic-2 pattern — flagged for Alec's veto):**
  D-1..D-5, D-7, D-8 as specified. **D-6 deviation:** Chakra Petch bundled as 4 static weights
  (400/500/600/700) as recommended, but Space Grotesk and JetBrains Mono ship as **variable fonts** in
  the canonical `google/fonts` repo, so one VF per family is bundled (covers all mock weights; smaller
  footprint; Godot pins weight via `FontVariation`). Documented in DESIGN-DECISIONS.md §D-6.
- **Shadow recipes (UX-DR11):** stored as reusable data in `ThemeTokens.ShadowRecipes` +
  `ChimeraStyleBox.WithShadow` (a shadow = size+offset+color, which does not fit a single int Theme
  `constant`); `shadow_1` is realized on the preview panel.
- **Scope fence honored:** only `godot/assets/ui/**`, `godot/src/UI/Theme/**`, the preview scene, and the
  workflow tracking files changed. Zero `src/Core|Combat|Economy|Navigation|Multiplayer`, zero golden
  logic, no `project.godot` `gui/theme`, no existing panel restyled, no global default theme set.
  *Incidental:* the editor's rescan generated `ProjectChimera.Sim.Tests/Golden/AiBelowThresholdRazeTests.cs.uid`
  (a UID sidecar for a pre-existing 2.13 test — the `.cs` is byte-unchanged); not a golden-logic change.

### File List
**New — theme + assets:**
- `godot/assets/ui/main.tres` — the committed canonical Theme (text, format=3)
- `godot/assets/ui/DESIGN-DECISIONS.md` — D-1/D-2/D-3 rationale + engine gotchas
- `godot/assets/ui/fonts/chakra-petch/ChakraPetch-{Regular,Medium,SemiBold,Bold}.ttf` (+ `.import`) + `OFL.txt`
- `godot/assets/ui/fonts/space-grotesk/SpaceGrotesk-VariableFont_wght.ttf` (+ `.import`) + `OFL.txt`
- `godot/assets/ui/fonts/jetbrains-mono/JetBrainsMono-VariableFont_wght.ttf` (+ `.import`) + `OFL.txt`

**New — code (`ProjectChimera.UI.Theme`):**
- `godot/src/UI/Theme/ThemeTokens.cs` — token vocabulary (StringName constants) + canonical values + 3 accent palettes
- `godot/src/UI/Theme/ChimeraStyleBox.cs` — the chamfer recipe (D-1/D-2) as working code
- `godot/src/UI/Theme/ThemeBuilder.cs` — reproducible builder → `main.tres`
- `godot/src/UI/Theme/AccentController.cs` — the accent-switch mechanism (D-3) + StyleBox-seam registry
- `godot/src/UI/Theme/ThemePreview.cs` — throwaway in-engine proof harness
- (each `.cs` has an editor-generated `.cs.uid` sidecar)

**New — scene:**
- `godot/scenes/theme_preview.tscn` — proof scene (loads `ThemePreview.cs`)

**Modified — workflow tracking:**
- `_bmad-output/implementation-artifacts/3-1a-...-godot-theme-resource.md` (this file; + `baseline_commit`)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (status → in-progress → review)

### Review Findings

_gds-code-review 2026-07-05 (3 parallel adversarial layers — Blind Hunter · Edge Case Hunter · Acceptance Auditor, all Opus 4.8, fresh context — + independent lead verification of every finding against live source + build)._

**Outcome — all 6 ACs met and independently re-verified.** Build 0 errors / **0 new warnings** (the only 3 warnings are pre-existing `CS8632` in `GatheringSystem`/`FlowFieldSystem`, none in theme files); `main.tres` = **34 colors / 4 fonts / 10 font_sizes / 12 constants**, every value exact vs the Canonical Token Table (all 34 hex→`Color` floats reverse-checked incl. 8-digit glow/wash alpha); `tnum` tag `1953396077` = "tnum"; fonts wired display/ui/mono + `mono_tnum`; scope fence clean (no sim/Combat/Economy/Navigation/Multiplayer/golden-logic, no `project.godot` gui/theme, no panel restyled). **14 raw findings → 10 unique → 1 decision · 3 patch · 4 defer · 2 dismissed. No Critical, no correctness bug in 3.1a's own deliverables** — every survivor is forward-looking robustness of the two mechanisms (accent switch, chamfer) that 3.1b/3.1c build on, plus one live artifact-churn issue.

**Resolution (2026-07-05):** decision D1 → **(A) harden now**; **all 4 patches APPLIED + re-verified** — `godot.csproj` build **0 err / 0 new warnings** (the 3 warnings are pre-existing `CS8632` in GatheringSystem/FlowFieldSystem), and an in-engine `/godot-verify` (theme_preview, Godot 4.6.3) **PASS**: switching teal→amber retints BOTH the `accent` Color token (`1ed1cd`→`f2af48`) AND the registered stylebox seam (`1ed1cd`→`f2af48`), `CurrentAccent`→`amber`, zero runtime/editor errors. AccentController generalized to per-variant accent tracking (`RegisterAccentBox(box, property, token)`); the proof scene now loads the committed `main.tres` instead of overwriting it. 4 defers carried to `deferred-work.md`. **Status → done.**

**Decision needed**

- [x] [Review][Decision→Patch] AccentController seam tracks only the BASE `accent`, not the other 5 variants — `SwitchAccent` retints every registered stylebox to `Color.FromHtml(palette.Accent)` (base), and `RegisterAccentFill`/`RegisterAccentBorder` are the only entry points [AccentController.cs:302-314,340-344]. The stated D-3 contract is "retint every accent surface in one op," but a 3.1b button whose hover/border uses `accent_bright`/`accent_dim`/`accent_glow` can't be registered to track its variant → left stale on switch (AC3-class failure) or forced to the base shade (wrong). 3.1a's own AC3 passes — its proof only exercises a base-`accent` fill. Sources: blind+edge (Blind=Med, Edge=High). **RESOLVED (Alec 2026-07-05): (A) harden now** → tracked as Patch (P4) below.

**Patches**

- [x] [Review][Patch] Proof scene overwrites & churns the committed `main.tres` [ThemePreview.cs:607-616 · ThemeBuilder.cs:478,548] — `_Ready` unconditionally `Build()`→`Save()`s over `res://assets/ui/main.tres`; ResourceSaver re-mints ext/sub-resource IDs each run → a spurious git diff on every `/godot-verify` (undercuts the "git-diffable" goal), silently reverts any hand-edit, and is the only regen path with no `main.tres == Build()` assertion (drift/first-switch color-pop if `ThemeTokens` is edited without re-running). Fix: load the committed `main.tres` for the proof; if the round-trip check is kept, target a throwaway `user://` path, not the committed artifact. Sources: blind+edge.
- [x] [Review][Patch] Silent wrong-value fallbacks [ThemeTokens.cs:1032-1037 `GetShadow` · ThemeBuilder.cs:531 `TryGetPalette`] — `GetShadow(unknown)` returns `shadow_1` with no signal; `Build` discards `TryGetPalette`'s `false`, so a bad `DefaultAccent` would silently bake teal. Benign today (all names valid). Fix: `GD.PrintErr` on the `GetShadow` fallback (or return a bool), and check+log `TryGetPalette`'s return in `Build`. Sources: blind+edge.
- [x] [Review][Patch] Doc comments reference the stale filename `main.theme` [AccentController.cs class doc · ThemeBuilder.cs class doc] — committed artifact is `main.tres` (the `.theme`-is-binary fix); `ThemePath` is correct, only two XML doc comments say "main.theme". Fix: s/`main.theme`/`main.tres`/ in the two class docs. Source: auditor.
- [x] [Review][Patch] (P4 — from the resolved Decision) AccentController: generalize the accent-stylebox registry to per-variant tracking [AccentController.cs] — register each box against a named accent token (accent/bright/dim/ink/glow/wash) + which property (BgColor/BorderColor); `SwitchAccent` re-reads each box's token value from the new palette. Closes the D-3 "retint every accent surface in one op" contract for all 6 variants, not just base. Presentation-only, no sim/test/golden impact.

**Deferred** (real, not actionable in 3.1a — see `deferred-work.md`)

- [x] [Review][Defer] AccentController registry has no unregister/clear → leak in the long-lived controller [AccentController.cs:292-314] — deferred, fix coupled to 3.1b component lifecycle. Source: edge.
- [x] [Review][Defer] UX-DR11 shadow tokens absent from `main.tres` — AC4-literal gap [main.tres] — stored as `ThemeTokens.ShadowRecipes` C# data + realized on the preview panel; a Theme `constant` is int-only (can't hold size+offset+float-alpha), Task 3 softened to "documented constants". **Recommend accept.** Source: auditor.
- [x] [Review][Defer] `ChimeraStyleBox.Chamfer` has no `cut` bounds guard [ChimeraStyleBox.cs:390-413] — safe for internal 5/8/14 callers; matters when 3.1b passes author-supplied cuts. Source: edge.
- [x] [Review][Defer] `cut-lg` (14) not rendered in the in-engine proof [ThemePreview.cs] — AC2 names cut/cut-sm/cut-lg; proof shows cut(8)+cut-sm(5); `Chamfer` is size-parametric so cut-lg is identical. Source: auditor.

**Dismissed** (verified false-positive / by-design) — 2

- glow/wash alpha "inconsistency" across accents — VERIFIED faithful to the source (`chimera.css` dark theme: teal glow `/0.28`, amber/violet `/0.30`; teal/amber wash `/0.12`, violet `/0.13`) → per-accent alpha is by design, correctly transcribed. (Blind F4)
- `AiBelowThresholdRazeTests.cs.uid` added under `Golden/` — benign editor UID sidecar for a pre-existing 2.13 test (`.cs` byte-unchanged), disclosed in the Dev Record; not a golden-logic/determinism change. (Auditor A3)

## Change Log
- 2026-07-05 — Story 3.1a implemented: resolved UX-DR4 (accent-switch) + UX-DR9 (chamfer) as working
  code; authored `main.tres` token vault (UX-DR1..12 + UX-DR34); bundled the 3 OFL fonts; built the
  `theme_preview` proof scene. Fixed 2 engine defects (hyphenated Theme names rejected → underscores;
  `.theme` binary → `.tres` text). All 6 ACs verified in-engine. Status → review.
- 2026-07-05 — gds-code-review PASS (3-layer adversarial: Blind Hunter · Edge Case Hunter · Acceptance
  Auditor, Opus 4.8, fresh context + independent lead verification against source + build). All 6 ACs met;
  14 raw findings → 10 unique → 1 decision + 3 patch + 4 defer + 2 dismissed. Decision (accent seam →
  harden now) + all 4 patches APPLIED + re-verified (build 0-err/0-new-warn; in-engine accent-switch PASS —
  token AND stylebox seam retint teal→amber). AccentController generalized to per-variant accent tracking;
  proof scene no longer overwrites the committed main.tres; silent GetShadow/TryGetPalette fallbacks now log;
  stale `main.theme` doc comments fixed. 4 deferred → deferred-work.md, 2 dismissed. Status → done.
