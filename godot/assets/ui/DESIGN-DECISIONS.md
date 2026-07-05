# UI Design-System — Resolved Decisions (Story 3.1a)

This file records the two **open** design decisions the epics flagged with ⚠ (UX-DR4 accent-switch
mechanism, UX-DR9 chamfer StyleBox mechanism) plus the supporting choices, resolved to a single
canonical implementation each. Every later UI story (3.1b component kit, 3.1c composites, 3.3–3.7
editors, 3.11 shell) styles itself from `main.tres` and these mechanisms.

**Scope of 3.1a:** dark theme only, token vault + two mechanisms + an in-engine proof. No components,
no gallery, no restyling of existing panels, no global default theme, no light theme (UX-DR37). No
simulation code, no checksum — `/godot-verify` is the gate (Epic 2 retro §5).

---

## D-1 — UX-DR9 chamfer mechanism: **native `StyleBoxFlat` with `corner_detail = 1`**

**Resolved:** the brand's faceted (45°) corners are produced by a stock `StyleBoxFlat`, not a custom
StyleBox subclass, texture, or shader.

```
corner_radius_top_left     = <cut>      # e.g. 8  (cut / cut-sm / cut-lg)
corner_radius_bottom_right = <cut>
corner_radius_top_right    = 0
corner_radius_bottom_left  = 0
corner_detail              = 1          # ← turns the "radius" into a straight 45° chamfer, NOT a curve
anti_aliasing              = false      # crisp facet edges
bg_color                   = <surface token>
border_color               = <edge-light / line token>   # cel-shade hairline
border_width_*             = 1
```

**Why.** This **overturns the epics.md ⚠ assumption** that a custom StyleBox / texture / shader is
required. Verified against the Godot 4.6 class reference: *"A corner detail of 1 will result in
chamfered corners instead of rounded corners."* Result: zero custom draw code, tints straight from the
`bg_color`/`border_color` tokens, fully git-diffable in the `.tres`. This resolves the UX-DR9 blocker
and unblocks 3.1b/3.1c. Implemented once in `src/UI/Theme/ChimeraStyleBox.Chamfer(...)`.

**Alternative (reserved):** a custom `ChamferStyleBox : StyleBox` overriding `_Draw(Rid, Rect2)` — use
ONLY if a future component needs a **non-45°**, **multi-facet**, or **notched** corner (log it there).
Note: there is no `_GetContentMargins` virtual; content margins are the `content_margin_*` properties.

## D-2 — UX-DR9 shape: **2-corner top-left + bottom-right cut**

**Resolved:** reproduce the shipped Claude Design UI's distinctive low-poly diagonal — cut **TL + BR**,
leave **TR + BL** square (`chimera.css:213` `polygon(0 var(--c), var(--c) 0, 100% 0, 100% calc(100% -
var(--c)), calc(100% - var(--c)) 100%, 0 100%)`). Fidelity to the shipped UI is the design intent
(UX D1: distill, don't redesign). Per-corner, trivially adjustable to a symmetric all-4-corner chamfer
if ever wanted.

---

## D-3 — UX-DR4 accent-switch mechanism: **one live Theme, mutate the accent `Color` items**

**Resolved:** there is **one** `main.tres`. Switching accent (teal → amber → violet) rewrites the
**6 accent `Color` entries** (`accent`, `accent-bright`, `accent-dim`, `accent-ink`, `accent-glow`,
`accent-wash`) on that one live Theme in a loop. Implemented in `src/UI/Theme/AccentController.cs`.

**Why.** Verified in the 4.6 engine source: `Theme.SetColor(...)` emits `changed` →
`NOTIFICATION_THEME_CHANGED` cascades a repaint down every `Control` using the theme; the theme→control
connection is `CONNECT_DEFERRED`, so rewriting all 6 entries in a loop **coalesces into a single
end-of-frame repaint**. DRY (one theme file, not three), engine-native. Components must **read the
shared entry** (`GetThemeColor("accent", "Chimera")`), never a literal — a literal won't retint.

**Alternatives (rejected):** 3 separate `.theme` files (duplicates every non-accent token — sync
hazard); root `AddThemeColorOverride` (shadows the theme — the opposite of one-source-of-truth); shader
uniform (ignores the theme system).

### ⚠ The seam that silently breaks — accent-tinted StyleBoxes

An accent-tinted **surface** (a chamfered button filled with `accent`) gets its fill/border from a
`StyleBoxFlat`'s `BgColor`/`BorderColor` — those are **sub-resource properties, NOT theme `Color`
tokens** — so they do **not** auto-follow the `accent` Color entry. If you only rewrite the 6 Color
items, accent-filled styleboxes are left on the OLD accent after a switch (this fails AC3).

**Mitigation (owned by `AccentController`):** the controller keeps a registry of accent-tinted
styleboxes and rewrites their `BgColor`/`BorderColor` in the **same** switch call. Mutating a StyleBox
also emits `changed`, so it rides the same coalesced repaint. Prefer routing accent onto surfaces via
theme `Color` items controls read directly (font colors, icon `modulate`) where possible; register a
stylebox only when a surface genuinely needs an accent fill. The preview proves this: an accent-filled
chamfered button retints across all three accents.

**API note:** the setter is `Theme.SetColor(name, themeType, color)` — themeType is the **middle** arg.
Type variations (3.1b) use `Theme.SetTypeVariation(variation, base)` + `Control.ThemeTypeVariation`.

---

## D-6 — Fonts: bundled OFL families (minor deviation flagged)

Three OFL families are bundled under `assets/ui/fonts/` (all SIL Open Font License — free to ship;
each family's `OFL.txt` is included). Sourced from the canonical `google/fonts` repo:

| Role        | Family        | Format in repo | Bundled files |
|-------------|---------------|----------------|---------------|
| display     | Chakra Petch  | static         | Regular / Medium / SemiBold / Bold (400/500/600/700) |
| ui (body)   | Space Grotesk | **variable**   | `SpaceGrotesk-VariableFont_wght.ttf` (300–700) |
| mono        | JetBrains Mono| **variable**   | `JetBrainsMono-VariableFont_wght.ttf` (100–800) |

**Deviation from D-6 (flag for Alec):** D-6 recommended bundling *static* TTFs for all three. Chakra
Petch is static as recommended. **Space Grotesk and JetBrains Mono are shipped as variable fonts** in
the canonical `google/fonts` repo, so we bundle the single variable file per family instead of 4 static
weights. The variable file **covers every weight the mock uses** (400/500/600/700 and 400/500/700
respectively) from one authoritative, license-clean source with a smaller footprint, and Godot pins any
weight via `FontVariation`. Same families, same weight coverage, fewer files. Revert to statics if a
specific static instance is ever required — the weights are all inside the VF.

---

## D-4/D-5/D-7/D-8 — supporting choices (taken as recommended)

- **D-4 token storage:** every named token lives under one custom theme type `"Chimera"`
  (`theme.SetColor("surface_1", "Chimera", …)`); components read `GetThemeColor("surface_1",
  "Chimera")`. Plus `default_font` + `default_font_size` so text inherits globally. Configuring stock
  control types (Panel/Button/LineEdit) via **type variations** is 3.1b — not here.
- **D-5 home:** `assets/ui/main.tres` + `assets/ui/fonts/` (repo convention: `assets/` = binary
  source art, co-located with the theme that references it). The file is `.tres`, not `.theme` — see
  the format gotcha below.

---

## Engine gotchas discovered at dev time (load-bearing for 3.1b/3.1c)

- **Theme item names must be valid identifiers — hyphens are silently rejected.** `Theme::set_color`
  (and every `set_*` sibling) calls `is_valid_item_name()`, which rejects `-`. `set_color("surface-1",
  …)` prints `Invalid item name: 'surface-1'` and **no-ops** (the token never lands; it also vanishes
  on save). The CSS token names (`surface-1`, `accent-bright`, `t-md`) therefore map to **underscore**
  names in the theme (`surface_1`, `accent_bright`, `t_md`) — the Godot-idiomatic convention (cf.
  `font_color`, `h_separation`). `ThemeTokens` is the single source for the exact names; read tokens by
  its `StringName` constants, never by a re-typed literal.
- **`.theme` is Godot's BINARY resource extension; `.tres` is text.** `ResourceSaver.Save(theme,
  "…/main.theme")` writes a binary `RSRC` blob (not git-diffable). To get the diffable text resource the
  design system needs (`[gd_resource type="Theme" format=3]`), the committed artifact is **`main.tres`**.
- **`Color.FromHtml` in C#** (GDScript's `Color.html`); it accepts `#rrggbb` and `#rrggbbaa` (8-digit
  for the accent glow/wash alphas).
- **`ProjectChimera.UI.Theme` shadows the bare type `Theme`** — inside this namespace, refer to the
  Godot class as `Godot.Theme` (fully qualified), or the compiler reads `Theme` as the namespace.
- **D-7 application scope:** author + preview-verify only. The project-global default theme is NOT set
  and no existing panel is restyled (3.11 + editor stories own that) — avoids mid-epic destabilization.
- **D-8 verification gate:** `/godot-verify` on the throwaway `theme_preview` scene is the gate (a
  Godot `Theme` cannot load in Godot-free Tier-1 xUnit). An optional Tier-2 GdUnit4 load-test is
  nice-to-have teeth, not the gate.

## Anti-patterns (do not reintroduce)

1. **Rounded corners** — never `corner_detail = 8` (default) or a nonzero radius with detail ≠ 1 on
   brand surfaces. Sole exception: `.kbd` (3px radius, default detail) — a 3.1b component.
2. **oklch / OKHSL in the theme** — Godot has no oklch; OKHSL ≠ oklch. Use the pre-converted sRGB hex.
3. **Literal accent colors** — read `GetThemeColor("accent", "Chimera")`; a literal won't retint.
4. **Forgetting the StyleBox accent seam** — register accent-tinted styleboxes with `AccentController`.
5. **Scope creep** — no components, gallery, marks, panel restyling, global default theme, or light theme.
6. **Team colors on chrome** — UX-DR6 colors are reserved for world units/minimap; present in the vault,
   applied to no UI element.
