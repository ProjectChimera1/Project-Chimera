---
title: 'Story 3.11: Apply the design system to the front-end shell (Title, Mode Select, Settings)'
type: 'feature'
created: '2026-07-07'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: 'b79093034c75d62adeaaed9f054a6dd388fe9d66'
final_revision: '8324dc4'
context: ['{project-root}/godot/assets/ui/DESIGN-DECISIONS.md']
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The front-end shell (`MainMenuOverlay` title screen, `SettingsPanel`) is raw placeholder UI — hardcoded `ColorRect`/`StyleBoxFlat`/font-size/color overrides, wrong tagline, an untabbed settings card — and does not touch the shared design system (`main.tres` Theme + `ChimeraComponents` kit) that stories 3.1a–3.1c delivered. It reads as unbranded and incoherent versus every editor built from the kit.

**Approach:** Restyle the two existing code-built overlays to the design system and reshape them to the documented information architecture (UX-DR67 Title, UX-DR73 Settings tabs). Mirror the established kit-bootstrap + `Control.Theme` pattern (`HeroPickerOverlay.EnsureKitInitialized`). This is verify + restyle of existing menus — no net-new functional screens, no sim-layer change.

## Boundaries & Constraints

**Always:**
- Apply the committed theme via `ThemeBuilder.ThemePath` (fallback `ThemeBuilder.Build()`), initialize the kit if needed, and set the overlay root's `Control.Theme` — exactly as `HeroPickerOverlay.cs:105-138`. Build all chrome from `ChimeraComponents` / `ChimeraStyleBox.Chamfer` / `ThemeTokens`; add no hardcoded color/font-size/size a token already covers.
- Title tagline is exactly `Build the game. Then play it.`; Title primary nav is Play · Create · Browse · Settings · Quit (UX-DR67); a version/build footer is present.
- Preserve existing behavior/wiring: `MainMenuOverlay`'s public events, `SettingsPanel`'s `ApplyAndSave`/`ResetToDefaults` field read/write, Escape-to-close, and persistence to `user://settings.json` all still work identically; keep the "Generate Map (AI)" entry reachable (do not orphan `OnGenerateMap`). Every field/control keeps a hover tooltip that also shows on keyboard focus.
- Honesty invariant: no Title/Mode-Select element advertises an unbuilt system — no ranked/MMR, no live-online-count, no player-count above the offline cap (1–4), no Multiplayer/Campaign destination that leads nowhere.
- Settings presents the five tabs Gameplay / Graphics / Audio / Controls / Accessibility (UX-DR73), reachable from BOTH the Commander branch (Escape in Play) and the Creator branch (Escape in Edit + Title Settings button).

**Block If:**
- A required kit primitive is missing and no reasonable existing component substitutes (log it, then HALT rather than hand-rolling a new hardcoded primitive).

**Never:**
- Never build a net-new second-level Mode Select screen, a Multiplayer card, a Campaign card, an online/account chip carrying MMR/level/online-count, or any "coming soon" placeholder for an unbuilt system. Those entries are owned by their epics (Multiplayer → Epic 9; Campaign & Tutorial + real N/3 binding → Story 13.1) and the final honesty sweep → Story 11.12. Fabricating them now would be a net-new screen AND advertise an unbuilt system — both forbidden.
- Never touch the simulation layer, checksum, goldens, or any `Fixed`/determinism surface. This is pure presentation (`src/UI/`).
- Never add non-functional controls to the Graphics or Controls tabs to fill them — an honest empty-state note is correct; live video settings are Story 11.12's.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Launch | App starts | Title renders to the Theme: Chimera seal/mark + wordmark, tagline "Build the game. Then play it.", nav Play/Create/Browse/Settings/Quit from the kit, version/build footer | Theme load fails → `ThemeBuilder.Build()` fallback, still renders |
| Open Settings (Commander) | Escape while in Play | Themed Settings overlay opens with the five tabs | — |
| Open Settings (Creator) | Escape while in Edit, or Title → Settings | Same themed Settings overlay opens (both branches reach it) | — |
| Empty tab | Select Graphics or Controls | Tab shows an honest empty-state note; no fabricated controls | — |
| Change + save a setting | Adjust a slider/toggle, Apply & Save | Value applies live and persists to `user://settings.json`, identical to pre-restyle behavior | Invalid/no-op → unchanged |
| Honesty audit | Inspect Title + mode entries | No ranked/MMR/live-online-count element; no Multiplayer/Campaign advertisement; Skirmish reads offline 1–4 | — |

</intent-contract>

## Code Map

- `godot/src/UI/MainMenuOverlay.cs` -- MODIFIED. Full restyle of `Initialize()`/`AddMenuButton`: replace the backdrop `ColorRect` + per-button `StyleBoxFlat` + font/color overrides with kit calls; `ChimeraMark.Create` seal; wordmark in `font_display`; tagline string change; nav from `ChimeraComponents.Button`; restyle `_versionLabel` (mono/`text_lo`). Keep all six public events + their invocations intact.
- `godot/src/UI/SettingsPanel.cs` -- MODIFIED. Restyle the card (kit `Panel` + `ChimeraStyleBox.Chamfer`) and reshape the single scrolling section list into a `ChimeraTabs` header + a content host that swaps per `TabChanged`. Map existing controls into Gameplay (camera pan/zoom, edge-scroll, minimap, FPS) / Audio (master/SFX/music) / Accessibility (colorblind); Graphics + Controls = honest empty-state. `ApplyAndSave`/`ResetToDefaults` must read the same `SettingsData` fields.
- `godot/src/UI/HeroPickerOverlay.cs:105-138` -- REFERENCE. The canonical `EnsureKitInitialized()` + `_panel.Theme = _theme` pattern to mirror.
- `godot/src/UI/Theme/ThemeBuilder.cs` -- CONSUMED. `ThemePath`, `Build()`.
- `godot/src/UI/Components/ChimeraComponents.cs` (+ `.Controls`/`.Surfaces`/`.Feedback`) -- CONSUMED. `Initialize`, `Panel`, `Button`, `Const`, `Col`, `IsInitialized`.
- `godot/src/UI/Components/ChimeraTabs.cs` -- CONSUMED. `Create(TabsVariant, params labels)`, `TabChanged(int)`, `Active`.
- `godot/src/UI/Components/ChimeraMark.cs`, `ChimeraSlider.cs`, `ChimeraSwitch.cs` -- CONSUMED. Seal + optional themed slider/switch.
- `godot/src/UI/Theme/ChimeraStyleBox.cs`, `Theme/ThemeTokens.cs`, `Theme/AccentController.cs` -- CONSUMED. Chamfer recipe, token vocabulary, accent seam.
- `godot/src/Core/Bootstrap/Phases/MainMenuPhase.cs`, `SettingsPhase.cs` -- REFERENCE. Overlay construction + wiring; unchanged unless the nav set requires an event tweak (keep events stable if possible).
- `godot/src/UI/SettingsManager.cs` + `Core/Definitions/SettingsData` -- REFERENCE. Persistence contract; unchanged.
- `godot/src/Core/MainScene.cs` (Escape handling, ~`:503`) -- REFERENCE. Confirms Settings reachability in both Play and Edit.

## Tasks & Acceptance

**Execution:**
- `godot/src/UI/MainMenuOverlay.cs` -- Bootstrap the kit + set `Control.Theme`; rebuild the title screen from tokens/components; set tagline "Build the game. Then play it."; nav Play/Create/Browse/Settings/Quit (Generate Map kept reachable, off the primary five); themed version/build footer. Preserve all events.
- `godot/src/UI/SettingsPanel.cs` -- Bootstrap the kit + set `Control.Theme`; replace hardcoded card styling; add a `ChimeraTabs` bar (Gameplay/Graphics/Audio/Controls/Accessibility) + per-tab content host; distribute existing controls; honest empty-state for Graphics/Controls; keep tooltips (hover + focus) and identical apply/persist behavior.
- `godot/src/UI/Components/*` or `Theme/*` -- Only if a required primitive is genuinely missing: log it per the Block-If rule; do not hand-roll hardcoded chrome.

**Acceptance Criteria:**
- Given the game launches, when the Title screen renders, then it is drawn from the shared Theme/kit (no hardcoded placeholder styleboxes remain) with the Chimera seal, nav Play/Create/Browse/Settings/Quit, a version/build footer, and the tagline "Build the game. Then play it." (UX-DR67).
- Given the Settings overlay is opened from either the Commander branch (Escape in Play) or the Creator branch (Escape in Edit / Title Settings button), when it appears, then it is themed and presents the tabs Gameplay/Graphics/Audio/Controls/Accessibility, and every existing setting still applies live and persists to `user://settings.json` (UX-DR73).
- Given any front-end surface (Title, mode entries, Settings), when it is inspected, then no element advertises an unbuilt system — no ranked/MMR/live-online-count, no Multiplayer/Campaign destination, Skirmish reads offline (≤4), and Graphics/Controls tabs show an honest empty-state rather than fabricated controls (honesty invariant; amended UX-DR68).
- Given this is presentation-only work, when the solution builds, then `godot.sln` compiles 0-error and no simulation/checksum/golden/`Fixed` code is touched.

## Design Notes

- **Intent reconciliation (why unbuilt entries are deferred).** The epic AC enumerates a Mode Select with Multiplayer / Campaign & Tutorial / My Content / breadcrumb / account chip (mirroring the pre-amendment `Shell.html` mockup: "1840 MMR", "2,418 online", "3/12"). Three governing signals override a literal build of those: (1) the story's ADDED note — "verify + restyle… not net-new screens"; (2) the honesty invariant in the same AC — "nothing on this screen may advertise an unbuilt system"; (3) explicit downstream ownership — Multiplayer=Epic 9, Campaign real N/3=Story 13.1, final honesty sweep=Story 11.12. Multiplayer and Campaign are unbuilt today, so honest, in-scope work is to restyle the existing reachable modes (Skirmish/Create/Browse/Settings/Quit) and NOT fabricate cards/chips for systems that don't exist. This is a deliberate, sourced divergence from the mockup, not an omission.
- **Kit-bootstrap pattern (copy it).** `_theme = ResourceLoader.Load<Theme>(ThemeBuilder.ThemePath, CacheMode.Ignore) ?? ThemeBuilder.Build();` then, if `!ChimeraComponents.IsInitialized`, create an `AccentController`, `_accent.Initialize(_theme)`, `ChimeraComponents.Initialize(_theme, _accent)`; set the root `Control.Theme = _theme` (a `CanvasLayer` has no `Theme` — apply on its root `Control`, which propagates).
- **Settings tabs.** `ChimeraTabs` is a header bar only (emits `TabChanged(int)`, exposes `Active`); the panel wires a content `Control` per index and shows/hides on `TabChanged`. Underline or boxed variant per DESIGN-DECISIONS.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` (or `godot/godot.csproj`) -- expected: 0 errors, 0 new warnings.

**Manual checks (`/godot-verify` — presentation is outside the Godot-free Tier-1 boundary):**
- Launch: Title shows seal + wordmark, tagline "Build the game. Then play it.", the five nav items from the kit, and a version/build footer — visibly themed (chamfered surfaces, accent, brand fonts), no legacy blue placeholder buttons.
- Settings: open via Escape in Play AND via Escape in Edit AND via the Title Settings button — all reach the same themed 5-tab panel; change a slider + a toggle, Apply & Save, reopen → values persisted; Graphics/Controls show an honest empty-state.
- Honesty: no MMR/rank/online-count/Multiplayer/Campaign element anywhere on the shell; Skirmish reads offline ≤4.

## Spec Change Log

_No bad_spec loopback occurred; no amendments._

## Review Triage Log

### 2026-07-07 — Review pass

- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 1
- reject: 16
- addressed_findings:
  - `[medium]` `[patch]` Settings tab content lived in a fixed-height (280px) bare `Control` host with `FullRect`-anchored pages, so the tallest page (Gameplay: 2 headers + 2 sliders + 3 toggles ≈ 270–300px) sat at/over budget — a larger font or localized string would clip interactive rows into the Apply/Reset footer. Changed the host to a content-driven `VBoxContainer` (min-height 300) and made pages laid-out `ExpandFill` children instead of FullRect-anchored, so a taller page grows the card rather than overflowing. Build-verified 0-error. Flagged by Blind + Edge Case Hunters.
- Deferred (logged to `deferred-work.md`): the kit-bootstrap (`EnsureKitInitialized`) + `Heading`/`Body` label helpers are copy-pasted across `MainMenuOverlay`/`SettingsPanel`/`HeroPickerOverlay` with drift — pre-existing per-overlay pattern, worth a shared-helper consolidation.
- Rejected (benign / design-conformant / guarded / out-of-scope-on-intent-authority):
  - **Intent-alignment AC2 divergence** — the affirmative Mode Select (Multiplayer/Campaign/N-3/account-chip/breadcrumb, second-level screen) is excluded on the intent's OWN authority ("not net-new screens" note + the absolute honesty invariant), My Content is subsumed by the existing Browse entry, and the deferred pieces are owned by Epic 9 / Story 13.1 / Story 11.12. The intent auditor conceded the choice is "sourced and defensible on the intent's own language."
  - Slider readout now shows raw 0.00–1.00 / no `×` prefix (design-system conformance: the kit `ChimeraSlider` pairs a `NumInput`; persistence unchanged; no clean fix without a kit format API).
  - `AttachFieldTip` makes the row label a focus+hover target → an extra tab-stop and the slider control lacks its own tip: deliberate — it guarantees the spec's "tooltip reveals on keyboard focus" contract for sliders (an `HBox` slider isn't reliably focusable); the field tip is keyboard-accessible via the focusable label.
  - Inline always-visible toggle hint removed → now hover/focus tooltip (the spec's tooltip contract is met; tooltips are the sanctioned kit pattern).
  - Cross-overlay shared `AccentController` ownership / second `Theme` instance vs the factory's: established guarded pattern (mirrors `HeroPickerOverlay`/`PersistenceManifestPanel`), cannot trigger for these app-lifetime overlays, and both theme instances load identical values from the same `.tres`.
  - No-subscriber nav hides the menu permanently: pre-existing pattern (identical in the pre-restyle code); all events are wired by `MainMenuPhase`.
  - Verification-Gap "tabization hides Audio/Accessibility controls from the round-trip": the 9 `SettingsData` fields map 1:1 to the 9 controls (confirmed by direct inspection of `ApplyAndSave`/page builders); wiring is provably correct.
  - Version-footer 160px right-aligned clip (short controlled string), un-tokenized intrinsic dims (340 nav width, host min), "off the primary five" doc wording (ghost variant + separators make it visually distinct), Generate-Map/Browse "honesty" (both are built features), section-header uppercasing via `FieldLabel.Up()` (intentional kit behavior), Reset slider "loud" set (no `ValueChanged` subscriber; harmless), modal backdrop no click-dismiss (intended), and empty Graphics/Controls tabs (intended honest empty-states).

## Auto Run Result

Status: done

**Summary.** Restyled the front-end shell (Title + Settings) to the shared 3.1x design system (Theme + `ChimeraComponents` kit), applying the documented Title IA (UX-DR67) and reshaping Settings into the five documented tabs (UX-DR73), with the honesty invariant governing the Mode Select scope — no fabricated Multiplayer/Campaign/online-identity elements (owned by Epic 9 / Story 13.1 / Story 11.12). Pure presentation; no simulation/checksum/golden/`Fixed` code touched.

**Files changed.**
- `godot/src/UI/MainMenuOverlay.cs` — full restyle: kit bootstrap + `Control.Theme`, `ChimeraMark` seal, display wordmark, tagline "Build the game. Then play it.", nav Play/Create/Browse/Settings/Quit from `ChimeraComponents.Button` (Generate Map kept reachable as a ghost aux entry), mono version footer; all six public events preserved.
- `godot/src/UI/SettingsPanel.cs` — restyle to a chamfered kit `Panel` + a `ChimeraTabs` header (Gameplay/Graphics/Audio/Controls/Accessibility) over a content-driven page host (review patch); themed sliders/switches, hover+focus field tooltips, honest empty-states for Graphics/Controls; `ApplyAndSave`/`ResetToDefaults` read/write the same `SettingsData` fields; Escape-to-close preserved.

**Review findings.** 1 patch applied (medium — Settings tab-host overflow/clipping hardened to content-driven sizing); 1 deferred (kit-bootstrap/label-helper duplication across three overlays); 16 rejected (see triage log). No intent_gap, no bad_spec.

**Verification.** `dotnet build godot/godot.sln` → 0 errors (5 pre-existing warnings in unrelated files, none in the changed files), re-run clean after the patch. `/godot-verify` on the pre-patch implementation confirmed all three ACs incl. a settings persistence round-trip against `user://settings.json`; the review patch is a canonical Godot container change, build-verified.

**Residual risks / artifacts.**
- The review patch (host → content-driven `VBoxContainer`) was build-verified but not re-run through the full in-editor `/godot-verify`; it is a standard container-sizing change and low-risk, but a visual re-confirm of the Settings tabs is a reasonable manual check.
- Slider value readouts display raw 0.00–1.00 (design-system conformance); persistence is unchanged.
- Residual (not part of this change, left in place): `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs.uid` (pre-existing untracked stray from Story 3.10).
