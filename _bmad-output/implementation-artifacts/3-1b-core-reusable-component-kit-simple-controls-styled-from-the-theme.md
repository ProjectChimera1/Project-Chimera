---
baseline_commit: fb28cb935ea83a356f598f25d0f00ff05f087d05
---

# Story 3.1b: Core reusable component kit (simple controls) styled from the Theme

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a creator-tools developer,
I want the core set of simple, reusable UI components pre-styled from the canonical `main.tres` Theme,
so that editors compose layouts, readouts, and inputs without re-styling controls.

## Context & Scope — read first

**This is the second pure-presentation story** (3.1a was the first) and the middle of the UI design-system arc: **3.1a** authored the token vault + the two mechanisms (chamfer, accent) → **3.1b (this story)** builds the *simple* component kit → **3.1c** builds composite/feedback components (menu/tooltip/dialog/toast/spinner/mark/switch) + the demo gallery. Every later editor (3.3–3.7) and the shell (3.11) compose from this kit.

**Like 3.1a, this touches NO simulation code.** There is **no `SimChecksum`, no golden, no fold, no `AlgoVersion` bump, no determinism fence**. None of the Epic 1/2 machinery applies. If you reach for a checksum test, you are in the wrong story. Tier-1 (Godot-free xUnit) **cannot** instantiate a Godot `Control`/`Theme`, and **no GdUnit4 harness exists** (`godot/tests/` is empty) — so **`/godot-verify` on an in-engine proof scene is the sole practical gate**, exactly as in 3.1a (Epic 2 retro §5).

**What 3.1b delivers (the whole story):**
1. **13 reusable components** styled only from the 3.1a Theme — the simple controls that carry no popover/scrim/animation machinery: **panel** (UX-DR13), **btn** (DR14), **icon-btn** (DR15), **kbd** (DR16), **chip** (DR17), **readout** (DR18), **tag** (DR19), **progress** (DR20), **slider** (DR21), **input** (DR22), **tabs** (DR24), **list-row** (DR25), **num-input** (DR32).
2. A **`ChimeraComponents` C# factory** (`godot/src/UI/Components/`) — the uniform API every later editor calls to build a component. See **Decision D-1** for the delivery mechanism.
3. The **two 3.1a deferred fixes assigned to this story**: `ChimeraStyleBox.Chamfer` cut-bounds clamp, and `AccentController` lifecycle (`Unregister`/`Clear`) + an `AccentChanged` signal so accent-bound text/icon colors follow a switch.
4. A **throwaway `component_preview` proof scene** that instantiates every component in its variants/states for `/godot-verify` (the polished *gallery* is 3.1c, NOT this story).

**What 3.1b explicitly does NOT do (scope fences — do not cross):**
- **Does NOT build the composite/feedback components** (menu DR23, tooltip DR26, dialog DR27, toast DR28, spinner DR29, mark DR30, switch DR31) or the **demo gallery** or the **UX-DR33 compose-from-kit guarantee** — all of that is **Story 3.1c**.
- **Does NOT restyle any existing panel** (`AbilityEditorPanel`, `SettingsPanel`, `ContentBrowserPanel`, `CommandCardSystem`, `LobbyUi`, `TriggerEditorPanel`, `MapGeneratorPanel`, etc.) and **does NOT set the project-global default theme** (`project.godot` `gui/theme/custom`). Applying the kit to the shell is **3.11**; to editor panels is **3.3–3.7**. Wiring globally now would restyle every existing (per-control-overridden, rounded-corner) panel mid-epic. Build the kit; leave application to the stories that own it.
- **Does NOT deliver the light theme** (UX-DR37) — dark theme only, as in 3.1a.
- **Does NOT modify** `main.tres`, `ThemeBuilder.cs`, or `ThemeTokens.cs` **for styling** (see D-1). The *only* edits to 3.1a code are the two assigned deferred fixes (`ChimeraStyleBox.cs` clamp, `AccentController.cs` lifecycle+signal) plus, if D-1's alternative is chosen, `ThemeBuilder`.
- **Does NOT do Fixed↔form data binding.** These are presentation chrome with plain `double`/`int` values; `num-input`/`slider` are styled controls, not editor data-binders. Fixed binding (`FixedToDouble`/`ToFixed` at `AbilityEditorPanel.cs:665-668`) is a **3.3+ editor** concern — do not pull it in.

## Acceptance Criteria

**AC1 — all 13 components exist as reusable, Theme-styled components.** Each of UX-DR13, DR14, DR15, DR16, DR17, DR18, DR19, DR20, DR21, DR22, DR24, DR25, DR32 (panel, btn, icon-btn, kbd, chip, readout, tag, progress, slider, input, tabs, list-row, num-input) exists as a reusable component built by the `ChimeraComponents` factory (or, per D-1, a thin component class for the stateful ones), instantiable by a caller in one call, and styled **only** from the 3.1a Theme (`main.tres`) read through `ThemeTokens` constants + the `"Chimera"` theme type.

**AC2 — no hardcoded color or size that exists as a token.** No component hardcodes a color or size that is available as a token: every color comes from `GetThemeColor(token, "Chimera")`, every global spacing/cut from `GetThemeConstant`, every font/size from `GetThemeFont`/`GetThemeFontSize`. (Component-intrinsic dimensions that are *not* tokens — icon-btn 36×36, progress track 8px, kbd radius 3px, the 2/3/4-px per-component micro-chamfers — are named constants documented from the CSS spec, not magic numbers; per the 3.1a token table these were pre-declared as "per-component extras used later in 3.1b/c," not global tokens.)

**AC3 — chamfer is correct and per-component.** Every chamfered component uses `ChimeraStyleBox` (faceted 45° TL+BR cut, never rounded), at its correct cut size: panel **8** (`cut`), btn/icon-btn/chip/input **5** (`cut-sm`), list-row **5**, tag/readout-icon/num-input **3**, progress track **2**, slider thumb **4**. **`kbd` is the SOLE radiused element** (3px `border-radius`, `corner_detail` default) and must **NOT** go through `ChimeraStyleBox.Chamfer` (which forces `corner_detail=1`). `cut-lg` (14) is exercised somewhere in the proof (closes 3.1a deferred #4).

**AC4 — accent switches across the whole kit in one op.** Switching the accent teal → amber → violet retints **every** accent-bound surface across the entire instantiated kit in a single `SwitchAccent` call, verified in-engine across all three states — no surface (button fill, list-row selected ring, chip/tag accent bg, slider thumb, progress fill, accent text/icon) left on the old accent. Accent-tinted **styleboxes** are registered with `AccentController` (auto-retint); accent-bound **text/icon** colors follow the new `AccentChanged` signal. The `AccentController` registry does not leak as components churn (`Unregister`/`Clear` added; accent styleboxes shared per (variant, cut) so the registry stays bounded).

**AC5 — typography + live numbers.** Components use the correct font roles: **display** (Chakra Petch, uppercase + tracking) for btn/tabs/tags/field-labels/kbd headings, **ui** (Space Grotesk) for body, **mono** (JetBrains Mono) for numbers. **All live numbers** — readout value, chip `.num`, num-input, slider value — render in the **`mono_tnum`** tabular-figure role (UX-DR34), so digit columns don't jitter.

**AC6 — each component realizes its spec'd variants + states.** Per the Per-Component Spec Table: btn (primary/secondary/ghost/danger × sm/lg/block, `:active` depresses 1px, disabled); icon-btn (36×36, `is-active` accent fill, disabled); tag (--lock/--ok/--accent/--danger); progress (--ok/--xp striped); input (focus = accent ring + wash, `.select` chevron variant, uppercase field label); num-input (focus = accent ring only, no wash); tabs (underline / --boxed / **segment** pill-group disclosure toggle); list-row (hover, `is-selected` = accent ring + wash, `is-locked` = 0.6 opacity + non-interactive, single-select); panel (--2 / --flat / --accent); readout (22px faceted icon + mono value + uppercase label); chip (surface-2 inset holding a `.num`); kbd (surface-3, 2px bottom border, 3px radius).

**AC7 — clean scope + in-engine proof.** No simulation file, no checksum/golden, no existing panel or shell restyled, no `project.godot` `gui/theme` change, no global default theme set. `main.tres`/`ThemeTokens.cs` unchanged; the only 3.1a-code edits are the two assigned deferred fixes (`ChimeraStyleBox.cs`, `AccentController.cs`). `dotnet build godot/godot.csproj` → 0 errors. `/godot-verify` PASSES on the throwaway `component_preview` scene: all 13 components render, chamfers are faceted (kbd rounded), accent switches across the whole kit teal→amber→violet, and live numbers use mono tabular figures. `git diff --stat` stays within `godot/src/UI/Components/**`, the two 3.1a fix files, the preview scene, and workflow tracking.

## Decisions (recommended defaults — confirm with Alec)

Following the Epic 2 / 3.1a recommended-default pattern. Each is the recommendation; take as written unless Alec vetoes. **D-1 is the one decision most worth Alec's attention** — it shapes the whole implementation.

- **D-1 (delivery mechanism) — a `ChimeraComponents` C# factory using per-instance styling; `main.tres` unchanged.** *(Recommended.)* A static factory class (`godot/src/UI/Components/ChimeraComponents.cs`, `partial` split by group if large) is the uniform API. Each component method news a stock `Control` (or builds a composite node-tree) and applies per-instance styling read from the loaded theme via `ThemeTokens` constants (the exact pattern `ThemePreview.cs:132-171` already proves compiles + runs). **Why this over Theme type-variations** (the mechanism 3.1a's D-4 *hinted* at): (1) **simplicity + one uniform mechanism** for all 13 (composites need factories regardless, so a second "type-variation" path just adds surface area); (2) **no `main.tres` regeneration / ID churn** — a concrete 3.1a review pain (the preview was patched to stop re-saving it); (3) all Chimera panels are **code-built (no `.tscn` UI)**, so type-variations' inspector-auto-styling benefit is moot; (4) **accent complexity is equal either way** — Godot theme entries are baked *values*, so a type-variation's accent `font_color` does not auto-follow the `accent` token any more than an override does (only stylebox *instances* registered with `AccentController` auto-retint; 3.1a's `ThemePreview.RefreshAccentVisuals` manually re-applies accent text — see D-3). "Styled only from the Theme" (AC1/AC2) is satisfied because every value is read from the token vault; nothing is invented. **Alternative (documented, Alec may prefer):** author Theme **type-variations** (`ChimeraBtnPrimary`, `ChimeraPanel`, …) — either baked into `main.tres` via an extended `ThemeBuilder` (one-time regen + ID churn) or installed onto the loaded theme at runtime; the factory then sets `Control.ThemeTypeVariation` for stock controls + builds composites. More Godot-native and puts stock-control styling literally in the theme, at the cost of a second mechanism + (if baked) `main.tres` churn. **This decision does not change any AC** — only the internals.
- **D-2 (factory vs component-class split) — factory methods for stateless components; a thin `partial class : Control` for the 3 stateful ones.** *(Recommended.)* Static factory methods return configured Controls for the stateless components (panel, btn, icon-btn, kbd, chip, readout, tag, progress, input, num-input). The three that carry runtime state get a lightweight component class exposing state setters: **tabs** (active-tab tracking + the segment disclosure toggle), **list-row** (SetSelected / SetLocked, single-select within a group), and **slider** (keeps its paired `num-input` in sync bidirectionally). All under `ProjectChimera.UI.Components`. **Alternative:** pure factory methods with caller-managed state (more caller boilerplate).
- **D-3 (accent-bound non-stylebox colors) — generalize 3.1a's manual refresh into an `AccentController.AccentChanged` signal.** *(Recommended.)* Stylebox fills/borders auto-retint via `AccentController` registration (3.1a mechanism). Accent-bound **text/icon** colors (a Label `font_color`, a TextureRect `modulate`) are *baked values* that do not auto-follow — 3.1a handled this ad-hoc in `ThemePreview.RefreshAccentVisuals()`. Add a `[Signal] AccentChanged(string accentName)` fired at the end of `SwitchAccent`; the factory subscribes accent-colored labels/icons to re-read `GetThemeColor(accent…)`. One reusable mechanism instead of per-scene refresh code. **Alternative:** each consumer wires its own post-switch refresh (the 3.1a status quo — rejected as the seam that silently breaks).
- **D-4 (`AccentController` lifecycle — 3.1a deferred #1) — add `Unregister(box)` + `Clear()`, and share accent styleboxes per (variant, cut).** *(Recommended, folds the deferral.)* The registry is add-only with no teardown → a leak once panels churn. Add `bool Unregister(StyleBoxFlat)` (drops all bindings for a box) + `void Clear()`, called from component/panel teardown. The factory **caches and shares** one accent stylebox instance per (variant, cut) so N primary buttons register ONE box, keeping the registry small. **Alternative:** weak references (more complex; unnecessary given explicit teardown).
- **D-5 (`ChimeraStyleBox.Chamfer` bounds guard — 3.1a deferred #3) — clamp `cut` to ≥ 0.** *(Recommended, folds the deferral.)* `Chamfer(int cut, …)` assigns `cut` straight into the corner radii with no guard; author-supplied or oversized cuts (e.g. `cut-lg`=14 on a ~20px chip) degenerate silently. Clamp `cut = Mathf.Max(0, cut)` (Godot already caps oversized radii to half the box). **Alternative:** also cap to half the min dimension in code (Godot does this at draw time — unnecessary).
- **D-6 (component home) — `godot/src/UI/Components/`, namespace `ProjectChimera.UI.Components`.** *(Recommended.)* New dir beside `src/UI/Theme/`. A `ComponentMetrics` static holds the CSS-derived per-component intrinsic dimensions (icon-btn 36×36, readout-ic 22×22, progress 8px, slider track 6 / thumb 14×18, kbd radius 3, num-input width 64, micro-cuts 2/3/4) — documented from spec so AC2 has no magic numbers. **Alternative:** put components in `src/UI/` root (rejected — clutters).
- **D-7 (proof scene) — a NEW throwaway `component_preview` scene, not the 3.1c gallery.** *(Recommended.)* Add `godot/scenes/component_preview.tscn` + `ComponentPreview.cs` that instantiates every component in each variant/state for `/godot-verify`, incl. a `cut-lg` surface (closes 3.1a deferred #4). Keep `theme_preview` (3.1a) as the token proof; keep this minimal — the polished, complete demo gallery with the UX-DR33 guarantee is **3.1c**. **Alternative:** extend `theme_preview` (rejected — muddies the 3.1a token proof).
- **D-8 (icon-btn disabled state) — add one, though the CSS omits it.** *(Recommended.)* The mock defines a disabled state only for `.btn`, not `.icon-btn`; editors will need disabled icon-buttons (greyed toolbar actions). Give icon-btn a disabled state mirroring btn (text-disabled glyph, surface-1 bg). **Alternative:** omit (rejected — an obvious kit gap that 3.3+ would hit immediately).

## Tasks / Subtasks

- [ ] **Task 1 — Scaffold + fold the two 3.1a deferred fixes (AC7, D-4, D-5, D-6)**
  - [ ] Create `godot/src/UI/Components/` + `ChimeraComponents.cs` (static factory skeleton, `ProjectChimera.UI.Components`, `#nullable enable`) + `ComponentMetrics.cs` (CSS-derived intrinsic dims — see Per-Component Spec Table).
  - [ ] **D-5:** in `ChimeraStyleBox.Chamfer` clamp `cut = Mathf.Max(0, cut)` (single line; the recipe is the one place chamfers are built).
  - [ ] **D-4:** add `AccentController.Unregister(StyleBoxFlat box)` (remove all bindings for the box) + `Clear()`. **D-3:** add `[Signal] public delegate void AccentChangedEventHandler(string accentName)` and `EmitSignal(SignalName.AccentChanged, palette.Name)` at the end of `SwitchAccent`. Keep the existing `RegisterAccentBox`/`RegisterAccentFill`/`RegisterAccentBorder` API intact.
  - [ ] Establish the factory's shared accent-stylebox cache keyed by (variant, cut) so repeated calls register ONE box (D-4 bound).
- [ ] **Task 2 — panel (UX-DR13) (AC1, AC3, AC6)**
  - [ ] `Panel(variant)` → `PanelContainer` with a `ChimeraStyleBox.Chamfer(cut=8, surface_1, edge_light)` + `shadow_1` (`.WithShadow(ThemeTokens.GetShadow(Shadow1))`). Variants: `--2` (surface-2), `--flat` (no shadow), `--accent` (accent-bright/dim border — register the border box).
  - [ ] Approximate the two-layer cel-shade border: lighter top (`edge_light`), darker sides/bottom (`line`). See Per-Component Spec Table note.
- [ ] **Task 3 — buttons: btn (UX-DR14) + icon-btn (UX-DR15) (AC1, AC3, AC5, AC6, D-8)**
  - [ ] `Button(text, variant, size)` → `Button`, display font uppercase 13px/600 tracking 0.04em, `cut-sm`=5 stylebox, states normal/hover/pressed/focus/disabled. Variants primary/secondary/ghost/danger; sizes sm/lg/block (see table for exact colors/padding). Primary/danger fill = accent/danger (register fill+border). `:active` 1px depress: on `pressed`, offset content margins / translate by 1px.
  - [ ] `IconButton(icon, isActive)` → 36×36 `Button` (no text, `.Icon`), 18px glyph, `cut-sm`, `is-active` = accent fill (registered) + accent-ink glyph, hover, **disabled (D-8)**.
- [ ] **Task 4 — kbd (UX-DR16) (AC1, AC3, AC5) — the SOLE radiused element**
  - [ ] `Kbd(text)` → a `PanelContainer`/`Label` with a **rounded** `StyleBoxFlat` (`corner_radius`=3, `corner_detail` DEFAULT — **not** `ChimeraStyleBox.Chamfer`), surface-3 bg, `line_strong` border with `border_width_bottom=2`, mono 11px/700 centered, min-width 18px, padding 1×5.
- [ ] **Task 5 — readout trio: chip (DR17), readout (DR18), tag (DR19) (AC1, AC3, AC5, AC6)**
  - [ ] `Chip(...)` → surface-2, `cut-sm`, inset `line` border, holds a mono `.num` label; padding 5×10 gap s2.
  - [ ] `Readout(iconColor, value, label)` → HBox: 22×22 faceted (cut 3) icon plate + mono-tnum value (text-hi, 18px/700) + uppercase label (text-lo, 11px, tracking 0.12em).
  - [ ] `Tag(text, variant)` → uppercase display 11px/600 pill, cut 3, variants neutral/--lock(warn)/--ok(ok)/--accent(accent-bright text on accent-wash — register)/--danger; tinted-bg + colored-text pairs (see table).
- [ ] **Task 6 — progress (UX-DR20) (AC1, AC3, AC4, AC6)**
  - [ ] `Progress(variant)` → `ProgressBar` (NEW — today's bars are 3D meshes, not Controls), 8px track surface-3 (cut 2), fill = accent gradient + glow (register fill box). Variants `--ok` (green, no glow), `--xp` (45° striped accent).
- [ ] **Task 7 — inputs: input (DR22) + num-input (DR32) + slider (DR21) (AC1, AC3, AC5, AC6, D-2)**
  - [ ] `Input(...)` → `LineEdit`, surface-3, `cut-sm`, inset `line` border, 9×12 padding, 13px; **focus = accent ring + accent-wash** (register the focus box, or re-apply on `AccentChanged`); `.select` chevron variant (OptionButton or LineEdit+chevron, `padding-right` 30); uppercase field-label helper.
  - [ ] `NumInput(...)` → mono, right-aligned 64px wide `SpinBox`/styled `LineEdit`, cut 3, surface-3, mono-tnum 700; **focus = accent ring ONLY (no wash)** — the deliberate difference from `input`. Reach the SpinBox's internal LineEdit for full styling.
  - [ ] `Slider(...)` (component class) → `HSlider` 6px track (surface-3, inset line, `accent_color`), 14×18 thumb (accent gradient, cut 4 — register), paired with a `NumInput` synced bidirectionally.
- [ ] **Task 8 — structure: tabs (DR24) + list-row (DR25) (AC1, AC3, AC4, AC6, D-2)**
  - [ ] `Tabs(...)` (component class) → underline variant (accent bar under `is-active`, register glow), `--boxed` variant, and the **segment** pill-group (the Simple/Advanced disclosure toggle 3.4+ needs — `is-active` child = accent-ink on accent fill). Track active index.
  - [ ] `ListRow(...)` (component class) → surface-1 inset, cut 5; `SetSelected` = accent ring + accent-wash (register), `SetLocked` = 0.6 opacity + `MouseFilter=Ignore`; hover = surface-2; single-select within a group.
- [ ] **Task 9 — accent wiring across the whole kit (AC4, D-3, D-4)**
  - [ ] Every accent-tinted stylebox in the kit is registered with `AccentController` via `RegisterAccentBox(box, Fill|Border, token)` binding the right variant (hover→`accent_bright`, pressed→`accent_dim`, glow→`accent_glow`, wash→`accent_wash`).
  - [ ] Accent-bound text/icon colors subscribe to `AccentChanged` and re-read the token.
  - [ ] Verify one `SwitchAccent` retints the ENTIRE instantiated kit (no stale surface).
- [ ] **Task 10 — component_preview proof scene + /godot-verify (AC7, D-7)**
  - [ ] `godot/scenes/component_preview.tscn` + `ComponentPreview.cs`: load `main.tres`, `new AccentController`, instantiate ALL 13 components across their variants/states (incl. a `cut-lg`=14 surface, closing 3.1a deferred #4), + 3 accent buttons calling `SwitchAccent`.
  - [ ] `dotnet build godot/godot.csproj` → 0 errors. Run `/godot-verify`: capture the kit rendering, faceted chamfers (kbd rounded, for contrast), the three accent states (whole-kit retint), and mono-tabular alignment. Record inject→observe in the Dev Record.
- [ ] **Task 11 — scope-fence check (AC7)**
  - [ ] `git diff --stat`: only `godot/src/UI/Components/**`, `godot/src/UI/Theme/ChimeraStyleBox.cs` + `AccentController.cs` (the two fixes), `godot/scenes/component_preview.tscn` + `ComponentPreview.cs`, and workflow tracking. **Zero** `src/Core|Combat|Economy|Navigation|Multiplayer`, zero golden, no `project.godot` gui/theme, no existing panel `.cs` restyled, no `main.tres`/`ThemeTokens.cs`/`ThemeBuilder.cs` change (unless D-1's alternative is chosen).

## Dev Notes

### Verification posture — same reset as 3.1a
No `SimChecksum`/golden/fold/`AlgoVersion`/stamp exists here; nothing sim is touched. **`/godot-verify` on `component_preview` is the gate** (Tier-1 xUnit is Godot-free and cannot load a `Control`/`Theme`; `godot/tests/` has no GdUnit4 harness — verified). "Teeth" here are visual: prove chamfers are faceted (contrast kbd's rounded corner), prove accent switches across the *whole kit* in one op (capture all three states), prove digit columns don't jitter (mono-tnum).

### The 3.1a Theme API you consume (verbatim — read tokens by these, never a literal)
- **`ThemeTokens` (`src/UI/Theme/ThemeTokens.cs`)** — `Type = "Chimera"` (the theme type every token lives under). `StringName` constants: surfaces `SurfaceVoid`/`Surface0..4`; lines `Line`/`LineStrong`/`EdgeLight`; text `TextHi`/`TextMid`/`TextLo`/`TextDisabled`; accent `Accent`/`AccentBright`/`AccentDim`/`AccentInk`/`AccentGlow`/`AccentWash` (+ `AccentTokens[]`); semantic `Ok`/`OkInk`/`Warn`/`WarnInk`/`Danger`/`DangerInk`/`Info`; team `Team1..8` (**reserved — never chrome**); fonts `FontDisplay`/`FontUi`/`FontMono`/`MonoTnum`; sizes `T2xs`..`T5xl`; spacing `S1..S8`; cuts `Cut`(8)/`CutSm`(5)/`CutLg`(14); `Speed`(130); shadows `Shadow1`/`Shadow2`/`ShadowPop`. Helpers: `GetShadow(name)` → `ShadowRecipe`, `AccentPalettes`, `TryGetPalette`, `AccentHexFor`.
- **`ChimeraStyleBox` (`src/UI/Theme/ChimeraStyleBox.cs`)** — `Chamfer(int cut, Color bg, Color border, int borderWidth = 1)` → faceted TL+BR `StyleBoxFlat` (`corner_detail=1`, AA off). Fluent `.WithContentMargins(h, v)` and `.WithShadow(ShadowRecipe)`. **This is the single place chamfers are built** — add the D-5 clamp here.
- **`AccentController` (`src/UI/Theme/AccentController.cs`, `partial : Node`)** — `Initialize(theme)`, `RegisterAccentFill(box)` / `RegisterAccentBorder(box)` / `RegisterAccentBox(box, AccentProperty.{Fill|Border}, accentToken)`, `SwitchAccent(name)`, `CurrentAccent`. Add the D-3/D-4 `AccentChanged` signal + `Unregister`/`Clear`.
- **Consuming pattern (proven at `ThemePreview.cs:49-52,132-171`):** `_theme = ResourceLoader.Load<Godot.Theme>("res://assets/ui/main.tres", CacheMode.Ignore)`; `Control.Theme = _theme`; `new AccentController` → `AddChild` → `Initialize(_theme)`. Read: `_theme.GetColor(ThemeTokens.Surface2, ThemeTokens.Type)`, `_theme.GetConstant(ThemeTokens.CutSm, ThemeTokens.Type)`, `_theme.GetFont(ThemeTokens.MonoTnum, ThemeTokens.Type)`, `_theme.GetFontSize(ThemeTokens.Tsm, ThemeTokens.Type)`. Build: `ChimeraStyleBox.Chamfer(cut, bg, border).WithContentMargins(h,v).WithShadow(...)`. Apply: `ctrl.AddThemeStyleboxOverride("normal", box)` (+ `hover`/`pressed`/`focus`/`disabled`), `ctrl.AddThemeFontOverride("font", f)`, `ctrl.AddThemeFontSizeOverride("font_size", n)`, `ctrl.AddThemeColorOverride("font_color", c)`. Register: `accent.RegisterAccentFill(box)`.
- **Shadows are C# data, not theme entries** (a Godot `constant` is int-only): pull via `ThemeTokens.GetShadow(ThemeTokens.Shadow1)` → `.WithShadow(...)`. (3.1a deferred #2, recommend-accept.)

### Per-Component Spec Table (THE reference — from the shipped Claude Design mock `chimera.css`)
Cut sizes are per-component (mostly hardcoded px in the CSS, NOT a blanket `cut-sm`). Colors are token names (values in `ThemeTokens`). Fonts: display=Chakra Petch, ui=Space Grotesk, mono=JetBrains Mono, tnum=`mono_tnum`.

| Component | Godot control | bg | border | cut px | font | states / variants |
|---|---|---|---|---|---|---|
| **panel** DR13 | PanelContainer | `surface_1` (--2: `surface_2`) | 2-layer: top `edge_light`, sides/bottom `line`, 1px | **8** (`cut`) | — | shadow_1 (--flat: none); --accent: accent-bright→dim border |
| **btn** DR14 | Button | `surface_2` | `inset` highlight | **5** (`cut_sm`) | display 13/600, UPPER, 0.04em | hover `surface_4`; **active translateY 1px**; disabled `text_disabled`/`surface_1`. primary: `accent_ink` text on accent gradient (+glow); secondary: transparent + `line_strong`; ghost: transparent `text_mid`; danger: `danger_ink` on danger. sizes sm(6×11/11px) / default(9×16/13px) / lg(13×24/15px) / block |
| **icon-btn** DR15 | Button (Icon) | `surface_2` | inset | **5** | 18px glyph | 36×36; hover `surface_4`; **is-active** `accent_ink` on `accent`; **disabled (D-8)** |
| **kbd** DR16 | Panel/Label | `surface_3` | `line_strong`, **bottom 2px** | **radius 3 (ROUND — NOT Chamfer)** | mono 11/700 center | min-width 18, pad 1×5; static |
| **chip** DR17 | HBox+Panel | `surface_2` | inset `line` | **5** | mono `.num` 13 | pad 5×10 gap s2; static |
| **readout** DR18 | HBox | none (icon plate per-instance) | — | icon **3** | val mono-tnum 18/700 `text_hi`; lbl 11 UPPER `text_lo` 0.12em | 22×22 icon; live value |
| **tag** DR19 | Panel/Label | variant | — | **3** | display 11/600 UPPER 0.05em | neutral `surface_3`/`text_mid`; --lock warn; --ok ok; --accent `accent_bright` on `accent_wash`; --danger. tinted-bg+colored-text |
| **progress** DR20 | ProgressBar (NEW) | track `surface_3` | — | **2** | — | fill accent gradient+glow; --ok green no-glow; --xp 45° striped |
| **slider** DR21 | HSlider | track `surface_3` inset `line` | — | thumb **4** | value mono-tnum | 6px track, 14×18 thumb accent gradient+glow; paired num-input |
| **input** DR22 | LineEdit | `surface_3` | inset `line` | **5** | ui 13; label display 11 UPPER 0.1em | **focus: accent ring + `accent_wash`**; placeholder `text_lo`; `.select` chevron (pr 30) |
| **tabs** DR24 | TabBar/custom (NEW — faked today) | — | — | boxed tab top **5**; segment outer 5 / inner 4 | display 13/600 UPPER | tab: `text_lo`→hover `text_mid`→**is-active** `accent_bright` + underline bar (`accent`+glow); --boxed; **segment** = Simple/Advanced toggle (`accent_ink` on `accent`) |
| **list-row** DR25 | PanelContainer/HBox | `surface_1` | inset `line` | **5** | — | hover `surface_2`; **is-selected** accent ring + `accent_wash`; **is-locked** opacity 0.6 + non-interactive; single-select |
| **num-input** DR32 | SpinBox/LineEdit | `surface_3` | inset `line` | **3** | mono-tnum 13/700 right | width 64; **focus: accent ring ONLY (no wash)** |

**Notes / gotchas (all verified against the mock):**
- **btn variants use single-dash** in CSS (`.btn-primary`, `.btn-ghost`) — irrelevant to the Godot factory (you pass an enum), but the 4 variants + 3 sizes are exactly as listed.
- **input focus = ring + wash; num-input focus = ring only** — a deliberate distinction; don't unify them.
- **kbd is the ONE rounded element** — a hard exception to the chamfer language (UX-DR35); building it via `ChimeraStyleBox.Chamfer` is a bug (forces `corner_detail=1`).
- **The panel's two-layer masked-gradient border is the hardest port** — approximate with a lighter top border (`edge_light`) + darker side/bottom (`line`). A full gradient backplate hairline is a later refinement, not required for AC.
- **`StyleBoxFlat` has NO gradient fill** — but btn-primary, progress fill, and the slider thumb are spec'd as accent *gradients* (`accent_bright → accent`). Port them as a **solid accent fill** (register the box for accent-switch); this is the acceptable 3.1b approximation. A real gradient would need a `StyleBoxTexture`/shader (not diffable) — out of scope; log it if a component ever demands it (UX-DR33). The glow is `.WithShadow`-style (`shadow_color` = accent_glow) or omitted for AC.
- **`.num` (utility) vs `.num-input` (component)**, **`.row` (utility) vs `.list-row` (component)**, **`.field` (wrapper) vs `.input` (control)** — don't conflate the utility with the component.
- **`.select`** (input chevron) and **segment** (tabs disclosure pill-group) are in-scope 3.1b variants; **menu** (DR23), **tooltip** (DR26) etc. are 3.1c.

### Existing code you REUSE / SUPERSEDE (do not modify these panels now)
The kit is built fresh; migrating these is **3.11 + 3.3–3.7**, NOT this story. Use them as the shape reference (and the duplication the Theme centralizes):
- **`AbilityEditorPanel.cs`** is the only real factory cluster: `Card(bg,border,radius,mx,my)` **:690** (the **rounded** `StyleBoxFlat` factory your chamfered `Panel()` supersedes), `AddSectionHeader` :700, `MakeLabeledRow` :708, `AddLineEditRow` :720, `AddSpinRow` :734 (~14 callers), `AddDropdownRow`/`MakeStyledDropdown` :748/:758, `MakeJsonPane` :780, `SmallBtn` (Advanced) :441. Model your `Button`/`Input`/`NumInput`/`ListRow` on these signatures.
- **House palette named once** at `AbilityEditorPanel.cs:33-42` (`PanelBg`/`CardBg`/`CardBorder`/`FieldBg`/`HeaderBlue`/`BodyText`/`DimText`/`HintText`/`OkGreen`/`ErrRed`) and **hand-retyped inline** in `SettingsPanel`/`ContentBrowserPanel`/`CommandCardSystem`/`LobbyUi` — the exact duplication `ThemeTokens` ends. (Old values are close-but-not-equal to the new tokens; map **roles**, not hexes.)
- **Net-new (no stock usage exists):** **progress** (bars are 3D `MeshInstance3D` at `BuildingBridge.cs:257`, not Controls), **tabs** (faked with `Button`s + `SetTabActive` at `ContentBrowserPanel.cs:208-214`), **icon-btn** (only emoji-in-`.Text` Buttons, e.g. `MapGeneratorPanel.cs:136+`), **kbd** (nonexistent), **readout** (only in `ThemePreview.cs`). Existing ad-hoc composites to supersede: **chip/tag** (`ContentBrowserPanel.AddTagRow:878` + `TAG_COLORS`), **list-row** (ad-hoc HBox rows).
- **Do NOT pull in `FixedToDouble`/`ToFixed`** (`AbilityEditorPanel.cs:665-668`) — Fixed↔form binding is a 3.3+ editor concern; 3.1b components carry plain `double`/`int`.

### Anti-patterns to avoid (LLM-dev traps)
1. **Rounded corners** anywhere except `kbd`. Every surface goes through `ChimeraStyleBox.Chamfer` (`corner_detail=1`); `kbd` is the sole 3px-radius exception.
2. **Literal colors/sizes.** Read every value via `GetThemeColor/Constant/Font(token, "Chimera")`. A baked literal fails AC2 and (if accent) won't retint.
3. **Forgetting the accent seam.** Accent-tinted styleboxes must be `Register`ed; accent text/icon must subscribe to `AccentChanged`. A baked accent value left un-refreshed is the AC4 failure.
4. **Registry leak.** Share accent styleboxes per (variant, cut); call `Unregister`/`Clear` on teardown (D-4).
5. **Scope creep into 3.1c/3.11.** No menu/tooltip/dialog/toast/spinner/mark/switch, no gallery, no restyling existing panels, no global default theme, no light theme.
6. **Touching sim / adding a checksum.** Nothing deterministic here; `git diff --stat` stays out of `src/Core|Combat|Economy|Navigation|Multiplayer` and every golden.
7. **Team colors on chrome.** `Team1..8` are reserved for world units; present in the vault, styled onto no component.
8. **Modifying `main.tres`/`ThemeTokens`/`ThemeBuilder` for styling** (D-1 recommended path keeps them untouched). Only `ChimeraStyleBox.cs` + `AccentController.cs` get the two assigned fixes.

### Project Structure Notes
- **New:** `godot/src/UI/Components/ChimeraComponents.cs` (+ partials by group if large), `ComponentMetrics.cs`, and thin component classes for stateful ones (`ChimeraTabs.cs`, `ChimeraListRow.cs`, `ChimeraSlider.cs` — D-2). Namespace `ProjectChimera.UI.Components`, `#nullable enable`, `partial` on any class inheriting a Godot type.
- **`Godot.Theme` namespace shadow — you WILL hit it:** these files `using ProjectChimera.UI.Theme;` to reach `ThemeTokens`/`ChimeraStyleBox`/`AccentController`, and that `using` brings the *namespace* `…Theme` into scope, which shadows the *type* `Godot.Theme`. So **always write `Godot.Theme` fully-qualified** when referencing the theme type in Components files (the 3.1a files do exactly this).
- **New:** `godot/scenes/component_preview.tscn` + `godot/src/UI/Components/ComponentPreview.cs` (throwaway proof, mirrors `theme_preview.tscn`'s one-ext-resource shape).
- **Edited (the two assigned fixes only):** `godot/src/UI/Theme/ChimeraStyleBox.cs` (clamp), `godot/src/UI/Theme/AccentController.cs` (`Unregister`/`Clear` + `AccentChanged`).
- **Untouched:** `main.tres`, `ThemeTokens.cs`, `ThemeBuilder.cs` (unless D-1 alternative), all existing panels, `project.godot`, all `src/Core/*`.

### Project Context Rules (from project-context.md)
- **Sim/Presentation boundary is sacred** — 3.1b is entirely presentation; `using Godot;` is expected. Import nothing from `src/Core` sim beyond display types.
- **Everything data-driven / one source of truth** — the Theme *is* the UI's data-driven source of truth; components read tokens 1:1, invent no values (the platform rule applied to chrome).
- **Layered complexity** — the kit is the simple/shared substrate every editor composes from; the segment/disclosure toggle is the seed of simple/advanced disclosure (3.4+).
- **Composition over inheritance** — components compose Controls + styleboxes; don't subclass Godot controls except the thin stateful component classes (D-2).
- **Godot C# gotchas** — `partial` on Godot-derived classes; `GD.Print` not `Console.WriteLine`; `#nullable enable` per file; PascalCase files/classes, camelCase locals, SCREAMING_CASE constants; `Color.FromHtml` (C#), not `Color.Html`.
- **Brownfield discipline** — reuse `ChimeraStyleBox`/`AccentController`/`ThemeTokens` + the `ThemePreview` consuming pattern; do not build a parallel styling system or re-mint the house palette.

### References
- [Source: epics.md#Story-3.1b] (lines 1189–1201) — ACs, coverage (UX-DR13..22,24,25,32), split rationale ("simpler controls… no popover/scrim/animation machinery"), "(scene or factory)".
- [Source: epics.md#Component-kit] (lines 273–293) — canonical UX-DR13..DR32 component definitions; (lines 259–271) tokens; (line 1171) epic coverage fold-ins (UX-DR35 chamfer-everywhere, UX-DR50 motion).
- [Source: implementation-artifacts/3-1a-...-godot-theme-resource.md] — the foundation: `main.tres` vault, `ChimeraStyleBox`/`AccentController`/`ThemeTokens`/`ThemeBuilder`/`ThemePreview`, the 6 ACs, and Cross-story continuity ("3.1b … absorb the duplicated per-file helpers … via components").
- [Source: godot/assets/ui/DESIGN-DECISIONS.md] — D-1..D-8 (chamfer recipe, accent mechanism + the StyleBox seam, API notes `SetTypeVariation`/`ThemeTypeVariation`), engine gotchas (hyphens rejected → underscores; `.theme` binary → `.tres`; `Color.FromHtml`; `Theme` namespace shadow), anti-patterns.
- [Source: implementation-artifacts/deferred-work.md] (lines 325–335) — the four 3.1a deferrals; **#1 AccentController leak** (→ D-4) and **#3 Chamfer bounds guard** (→ D-5) are folded here; #4 cut-lg-in-proof closed by Task 10; #2 shadow-in-C# recommend-accept.
- [Source: ux-designs/ux-Project_Chimera-2026-06-20/mockups/project-chimera/project/chimera.css] — the shipped Claude Design system; per-component rules (`.panel`/`.btn-*`/`.icon-btn`/`.kbd`/`.chip`/`.readout`/`.tag`/`.progress`/`.slider`/`.input`/`.select`/`.tabs`/`.segment`/`.list-row`/`.num-input`), the TL+BR chamfer polygon (:213), the per-component cut px, focus rules, `--speed`/`--ease`.
- [Source: ux-designs/ux-Project_Chimera-2026-06-20/DESIGN.md + EXPERIENCE.md] — token frontmatter, "map tokens 1:1 into a Godot Theme," §Component Patterns behavioral spine (list-row single-select/locked-non-interactive, readout live-updating).
- Existing code: `AbilityEditorPanel.cs:33-42,690,700-780` · `SettingsPanel.cs:221-272` · `ContentBrowserPanel.cs:208-214,837-878` · `CommandCardSystem.cs:443-461` · `BuildingBridge.cs:257` (3D progress bars) · `MapGeneratorPanel.cs:136` (emoji icon-buttons) — reuse-shape + duplication the kit ends; do NOT modify.
- Godot 4.6.3 API: `Theme` / `StyleBoxFlat` / `Button` / `LineEdit` / `SpinBox` / `HSlider` / `ProgressBar` / `TabBar`/`TabContainer` / `PanelContainer` / `Control` (`AddThemeStyleboxOverride`, `ThemeTypeVariation`, `NOTIFICATION_THEME_CHANGED`) — docs.godotengine.org/en/stable/classes.

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
