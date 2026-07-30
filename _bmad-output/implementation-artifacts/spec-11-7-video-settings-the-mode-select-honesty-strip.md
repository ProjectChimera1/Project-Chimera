---
title: 'Story 11.7 — Video settings + the Mode Select honesty strip'
type: 'feature'
created: '2026-07-30'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '4723857856dd9d2c54bdbd18838196227a963270'
final_revision: '08d981f2bf7abc03414351d888bb0ba7c857d765'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** The Settings Graphics tab is an honest empty-state placeholder (`SettingsPanel.cs:234-240` — "Live video options … arrive in a later update") — no resolution, window-mode, vsync, quality, or UI-scale control exists, and no code sets any display mode at runtime (`DisplayServer`/`GetWindow()` are used only for headless detection and the window title). Meanwhile the front-end shell must be provably honest: it may advertise only shipped systems. Story 3.11 already removed all ranked/MMR/online-count/unbuilt-Campaign elements; 11.7 fills the Graphics tab with real, live-applying video settings AND locks the honesty invariant as the epic's exit criterion in miniature — verified in-engine against the shipped-feature list. It also closes the DPI/resize gap: the in-match command cards are positioned from a viewport size cached at build time (`CommandCardSystem.cs:1018/1036/1192/1196/1264/1270/1387/1393`), so they do not reflow when the window resizes or UI scale changes.

**Approach:** (1) Add six video fields to `SettingsData` (resolution, window mode, vsync, quality preset, UI scale) and build real Graphics-tab controls on the 3.1x kit. Global display state (window mode, resolution, vsync, UI content-scale, MSAA, shadow-atlas) applies through a new `SettingsManager.ApplyVideo()` (called from `Apply()`, which already runs at `_Ready` so relaunch restore is covered); the one scene-coupled knob (directional-light shadows per quality tier) applies through the existing `MainScene.ApplySettingsToSystems` bridge against a newly-exposed `SceneContext.KeyLight`. Display-mode/resolution changes arm a 15-second safe-revert confirm dialog. (2) Run the honesty sweep, verify it in-engine against the shipped-feature list, and keep Skirmish reading "1–4" with no Campaign advertisement (Campaign's real N/3 binding stays owned by Story 13.1). (3) Re-anchor the four viewport-cached command-card panels to bottom/right anchor presets so they reflow automatically on resize and UI-scale change. Pure presentation — zero simulation / `SimChecksum` / `Fixed` / golden code is touched.

## Boundaries & Constraints

**Always:**
- **Six new persisted settings, all defaulted, round-tripping through `settings.json`.** Add to `SettingsData` (snake_case `[JsonPropertyName]` + safe default, mirroring the existing fields): `resolution_width` (int, 1920), `resolution_height` (int, 1080), `window_mode` (string enum `windowed`|`borderless`|`fullscreen`, default `windowed`), `vsync` (bool, true), `quality_preset` (string enum `low`|`medium`|`high`, default `medium`), `ui_scale` (float, 1.0). Extend `MigrateForward` to normalize the two enum strings to their default on an unknown value and clamp `ui_scale` to [0.75, 1.5]; bump `CurrentSchemaVersion` 2→3 (same precedent as the 9.7 1→2 bump for normalized new fields). Absent fields already deserialize to the defaults — no data loss on an old file.
- **Apply live, no relaunch.** All five controls take effect when Apply & Save is pressed (the existing `ApplyAndSave` seam), not on next launch. Restore-on-relaunch works because `SettingsManager._Ready` calls `Load()` then `Apply()` → `ApplyVideo()`; the scene-coupled shadow tier re-applies when a match launches and the bridge fires with `KeyLight` present.
- **Global-vs-scene apply split.** `SettingsManager.ApplyVideo()` (global, no scene refs): window mode via `DisplayServer.WindowSetMode` (`windowed`→`Windowed`, `borderless`→`Fullscreen`, `fullscreen`→`ExclusiveFullscreen`), resolution via `DisplayServer.WindowSetSize`, vsync via `DisplayServer.WindowSetVsyncMode`, UI scale via `GetWindow().ContentScaleFactor`, MSAA via `GetViewport().Msaa3D`, directional shadow atlas via `RenderingServer.DirectionalShadowAtlasSetSize`. The MainScene bridge applies ONLY `KeyLight.ShadowEnabled` (quality tier), null-guarded like the existing camera/minimap pushes.
- **Quality tiers are a named, concrete set.** `low`: shadows off, MSAA disabled. `medium`: shadows on, atlas 4096, MSAA 2×. `high`: shadows on, atlas 8192, MSAA 4×. `LightingPhase` enables `ShadowEnabled` as the baseline and stores the light on `SceneContext.KeyLight`.
- **Safe-revert on display-mode/resolution change only.** If Apply & Save changes `window_mode` or resolution from the previously-persisted values, apply the change, then open a `ChimeraDialog` with a live countdown ("Keep display settings? Reverting in Ns") and a Keep-confirm; a 15 s `Timer` auto-reverts window mode + resolution to the prior values, re-syncs the two dropdowns, and re-persists on timeout. Confirm keeps the new values. VSync / quality / UI-scale changes do NOT arm safe-revert (each is trivially reversible via its own control).
- **Honesty invariant (verify + lock).** No Title/Mode-Select element may advertise an unbuilt system: no ranked/MMR, no live-online-count, no player-count above the offline cap, no Multiplayer/Campaign destination that leads nowhere. Skirmish (the "Play" entry) reads "1–4" (`MainMenuOverlay.cs:112`). Multiplayer leads to the shipped Epic-9 lobby and stays. The sweep is verified in-engine by inventorying the Title entries' visible labels + tooltips against the shipped-feature list.
- **Reflow via anchors, not a resize listener.** Re-anchor the four viewport-cached `CommandCardSystem` panels (command card, inventory, worker, ability) to bottom-left / bottom-right anchor presets with fixed offsets from the anchored edge; Godot then reflows them on window resize and on `ContentScaleFactor` change automatically. The ability panel's normal/stacked toggle becomes two offsets from the bottom anchor.
- **Pure presentation.** Do not touch any file under `src/Core` sim arrays, `SimChecksum`, `Fixed` math, or any `*.golden.txt`. The only `src/Core` edits are `SceneContext.KeyLight`, `LightingPhase`, and the `MainScene` settings bridge — all Godot-node presentation.

**Block If:**
- A settings field would need to persist sim-affecting state (it must not — all six are pure presentation). If any proposed knob changes simulation, HALT `blocked`.
- The godot-mcp bridge is unreachable for the In-Engine Gate (another client holds the single-client bridge) — report the blocking environment condition, do NOT fabricate the gate artifact.

**Never:**
- Never add a Campaign or mission-select entry, an N/3 counter, a Multiplayer-lobby rewrite, or any MMR/rank/online-count/account chip. Campaign's real N/3 binding is owned by Story 13.1; adding it now both advertises an unbuilt system and steps on 13.1's ownership (the same sourced divergence Story 3.11 made).
- Never fill the Controls tab (key rebinding is a later story) or add non-functional controls to pad the Graphics tab.
- Never bump `SimChecksum.AlgoVersion`, re-record a golden, or edit a `Fixed`/sim file — this story is presentation-only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Round-trip persist | Set all six video fields, Apply & Save, reload `settings.json` | `SettingsData.FromJson` returns identical values; `schema_version` == 3 | Malformed JSON → defaults (existing fail-soft) |
| Old settings file | `settings.json` lacking the six keys | Deserializes to the defaults (1920×1080 / windowed / vsync on / medium / 1.0); no crash | — |
| Unknown enum value | `window_mode:"cinema"` / `quality_preset:"ultra"` | `MigrateForward` resets each to its default | — |
| UI-scale out of range | `ui_scale:5.0` | `MigrateForward` clamps to 1.5 | — |
| Apply vsync only | Toggle vsync, Apply & Save | `DisplayServer` vsync mode flips; NO safe-revert dialog | — |
| Apply window-mode change | Windowed → Fullscreen, Apply & Save | Mode applies; safe-revert dialog opens with countdown | Timeout → reverts to Windowed, re-syncs dropdown |
| Safe-revert confirm | Change resolution, click Keep within 15 s | New resolution persists; dialog closes; timer disarmed | — |
| Safe-revert timeout | Change to an unusable mode, wait 15 s | Window reverts to prior mode+size; dropdowns re-sync; prior values re-persisted | — |
| Quality tier apply (in match) | Low → High, Apply & Save while a match runs | `KeyLight.ShadowEnabled` on, atlas 8192, MSAA 4× (A/B visibly differs) | Light absent (menu) → global bits still apply, shadow toggle no-ops via null-guard |
| Relaunch restore | Persisted fullscreen + High, restart app | `_Ready`→`ApplyVideo` restores global display state pre-first-frame; shadow tier re-applies at match launch | — |
| Resize reflow | Launch 1080p, resize window to 1440p mid-match | Command/inventory/worker/ability panels stay pinned to their corners, no clipping/overlap | — |
| Honesty inventory | Drive Title screen, read all entries | Entry set = Play(1–4)/Multiplayer/Create/Browse/Replays/Settings/Generate Map/Quit; NO Campaign/MMR/online-count | — |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/SettingsData.cs` -- add the six video fields (`resolution_width`/`_height`, `window_mode`, `vsync`, `quality_preset`, `ui_scale`) beside the existing fields (:53-141), each with a `[JsonPropertyName]` + safe default; extend `MigrateForward` (:161-183) to normalize the two enum strings and clamp `ui_scale`; bump `CurrentSchemaVersion` 2→3 (:22) with a doc line. -- the persisted contract.
- `godot/src/UI/SettingsManager.cs` -- add `ApplyVideo()` and call it from `Apply()` (:110-114, beside `ApplyAudio()`); implement the global display pushes (window mode / size / vsync via `DisplayServer`, UI scale via `GetWindow().ContentScaleFactor`, MSAA via `GetViewport().Msaa3D`, shadow atlas via `RenderingServer.DirectionalShadowAtlasSetSize`) with a windowed-vs-fullscreen mode map. `_Ready` (:47-52) already runs Load→Apply so relaunch restore is free. -- global video apply + relaunch restore.
- `godot/src/UI/SettingsPanel.cs` -- replace `BuildGraphicsPage()` (:234-240) with real rows: two `ChimeraComponents.Select()` dropdowns (window mode, resolution) + one (quality preset) modeled on the AI-provider Select pattern (:284-295), a `ChimeraSwitch` (vsync) via `AddToggleRow` (:589), and a `ChimeraSlider` (UI scale 0.75–1.5 step 0.25) via `AddSliderRow` (:568); store the controls as fields (near :55-63); read them in `ApplyAndSave` (:650-682, before `_settings.Apply()`) and re-sync in `ResetToDefaults` (:684-715); add a `MaybeArmSafeRevert(prevMode, prevW, prevH)` helper (new) using `ChimeraDialog` + a `Timer` countdown. Resolution list = curated 16:9 set filtered to ≤ `DisplayServer.ScreenGetSize`. -- the Graphics UI + safe-revert.
- `godot/src/Core/Bootstrap/Phases/LightingPhase.cs` -- set `light.ShadowEnabled = true` (baseline) and store `_ctx.KeyLight = light` (:19-22). -- exposes the light for the quality tier.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- add `public DirectionalLight3D? KeyLight = null;` beside the other handles (~:66-97). -- the shared light handle.
- `godot/src/Core/MainScene.cs` -- in `ApplySettingsToSystems` (:2183-2201) apply the quality tier's `ShadowEnabled` to `_ctx.KeyLight` (null-guarded, mirroring the `_ctx.Cam`/`_ctx.Minimap` guards). -- scene-coupled shadow toggle.
- `godot/src/UI/CommandCardSystem.cs` -- re-anchor the four panels currently positioned from a cached `vpSize`: command card (:1018/1036), inventory (:1192/1196, bottom-right), worker (:1264/1270), ability (:1387/1393-1394, two offsets) — use `SetAnchorsAndOffsetsPreset(BottomLeft/BottomRight)` + fixed offsets from the anchored edge so they reflow automatically. -- resize/DPI reflow.
- `godot/src/UI/MainMenuOverlay.cs` -- REFERENCE for the honesty sweep. Confirm Skirmish "1–4" (:112) and no Campaign/MMR/online element; no change expected. -- honesty verify surface.
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` -- extend the round-trip + `MigrateForward` tests for the six new fields (round-trip, absent→default, unknown-enum→default, ui_scale clamp, schema stamp 3). -- persistence coverage.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/SettingsData.cs` -- add six video fields + defaults, extend `MigrateForward` normalization/clamp, bump `CurrentSchemaVersion` to 3 -- persisted video contract.
- `godot/src/UI/SettingsManager.cs` -- add `ApplyVideo()` (window mode/resolution/vsync/UI-scale/MSAA/shadow-atlas) and call it from `Apply()` -- global apply + relaunch restore.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `LightingPhase.cs` -- expose `KeyLight`, enable shadows baseline -- shadow handle for the quality tier.
- `godot/src/Core/MainScene.cs` -- apply the quality tier's shadow toggle to `_ctx.KeyLight` in `ApplySettingsToSystems` -- scene-coupled knob.
- `godot/src/UI/SettingsPanel.cs` -- build the five Graphics controls, wire read/apply/reset, add the 15 s safe-revert dialog -- the Graphics tab UI.
- `godot/src/UI/CommandCardSystem.cs` -- re-anchor the four viewport-cached panels to corner presets + offsets -- reflow.
- `godot/src/UI/MainMenuOverlay.cs` -- verify the honesty inventory (no change unless a stray element is found) -- honesty lock.
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` -- unit-test every I/O-matrix persistence row for the six fields -- coverage.

**Acceptance Criteria:**
- Given the Graphics tab, when opened, then resolution, window mode, vsync, quality preset, and UI scale controls render on the 3.1x kit (no empty-state note remains), each seeded from the persisted value.
- Given any of the five video settings is changed and Apply & Save is pressed, when applied, then it takes effect immediately without relaunch, persists to `settings.json`, and is restored on the next launch.
- Given a window-mode or resolution change is applied, when it lands, then a 15 s safe-revert confirm dialog appears; on Keep the new value persists, and on timeout the window and dropdowns revert to the prior value and re-persist.
- Given the Mode Select / Title shell is inspected against the shipped-feature list, when swept, then no element advertises an unbuilt system, Skirmish reads "1–4", and there is no Campaign advertisement (its N/3 binding remains owned by 13.1).
- Given a mid-session window resize or UI-scale change across the spot matrix (1080p / 1440p / 4K + two scale factors), when it occurs, then the HUD command-card panels and editor panels reflow to their corners without overlap or clipped controls.
- Given the feature is built, when the Sim-Tests suite runs, then the extended `SettingsDataRoundTripTests` pass and no `SimChecksum`/golden/`Fixed`/sim file was modified.

## Review Triage Log

### 2026-07-30 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 2, low 1)
- defer: 2: (high 0, medium 1, low 1)
- reject: 18
- addressed_findings:
  - `[medium]` `[patch]` Persisted `quality_preset:"low"` still rendered shadows every match — `LightingPhase` hardcoded `ShadowEnabled=true` and the tier toggle only reached the light via an `OnSettingsChanged` event that does not fire against the light on a fresh match launch. Fixed by seeding `light.ShadowEnabled = _ctx.SettingsMgr?.Current.QualityPreset != "low"` in `LightingPhase` (which runs after `SettingsPhase`, so `Current` is loaded). Re-verified in-engine: persisted low→shadow off at boot, high→shadow on, no Settings interaction.
  - `[medium]` `[patch]` A persisted resolution larger than the current screen forced an off-screen window on boot restore (safe-revert never arms on boot), and `borderless` still received a fighting `WindowSetSize`. Fixed in `SettingsManager.ApplyVideo()`: only issue `WindowSetSize` in `Windowed` mode, clamped to `DisplayServer.ScreenGetSize()`.
  - `[low]` `[patch]` A hand-corrupted `ui_scale:NaN/Inf` passed `Math.Clamp` untouched → `ContentScaleFactor=NaN` → blank UI. Fixed with a `float.IsFinite` guard in `MigrateForward` (fallback 1.0); added the `Video_UiScale_NonFinite_FallsBackToOne` test.
- deferred (see `deferred-work.md`): unconditional schema-version stamp in `MigrateForward` (a future-schema settings file is silently downgraded on save — pre-existing since Story 8.1); editor-panel reflow at high UI-scale / 4K (the new UI-scale lever can shrink the logical viewport below a center-anchored fixed-size editor panel — untested surface).
- rejected: Campaign N/3 binding absent (out of scope on the intent's own honesty invariant + the `(13.1)` attribution — Epic 13 is unbuilt, so a Campaign entry would violate the same-AC honesty invariant; the sourced divergence Story 3.11 already made); safe-revert Keep-path & panel-teardown (`ChimeraDialog.CloseWith` emits exactly one signal and `OnSafeRevertExpire` is re-entry-guarded; Settings *hides* not frees, so armed timers survive — correct by inspection); window-mode / MSAA-tier / vsync arms "unverified" (the independent in-engine gate auditor drove borderless 4→3 and read shadow+MSAA 0/1/2 across all three tiers live); CanvasLayer UI-scale reflow gap (gate observed all four panels re-pin at scale 1.5); one-step revert semantics, ApplyVideo-on-every-Apply snap-back, atlas-16bit, multi-monitor resolution filter, ability-panel stacked-offset & resolution-injection coverage (defensible or low; anchoring is resolution-uniform); full spot-matrix automation (anchoring is resolution-independent; the gate sampled 1080p + scale 1.5).

### 2026-07-30 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 16
- addressed_findings:
  - `[medium]` `[patch]` `MaybeArmSafeRevert` armed the 15 s safe-revert dialog on ANY resolution delta, but the review-1 fix made `ApplyVideo` issue `WindowSetSize` only in windowed mode — so changing the resolution dropdown while in Borderless/Fullscreen popped a spurious auto-revert countdown for a change that did nothing on screen. Fixed by gating the resolution-delta comparison behind `s.WindowMode == "windowed"` (a window-mode change still always arms). `SettingsPanel.cs`.
  - `[low]` `[patch]` `SettingsData.MigrateForward` normalized the two enum strings and clamped `ui_scale` but left `resolution_width/height` unvalidated; a hand-corrupted sub-pixel resolution (e.g. `resolution_width:1`) survives to `ApplyVideo`, which caps to the screen with `Mathf.Min` but has no floor → a 1×1 window on boot restore where safe-revert never arms. Fixed by resetting both to the 1080p default when either falls outside `[640×480, 16384×16384]`; added `Video_CorruptResolution_ResetsToDefault` + `Video_ValidResolution_IsPreservedByMigration` (23/23 SettingsData tests pass).
- deferred (see `deferred-work.md`): the Resolution dropdown stays enabled in Borderless/Fullscreen (where `ApplyVideo` ignores it), silently no-oping a resolution pick with no grey-out/hint — a live-interaction UX enhancement beyond any AC; the patch above removes the harmful symptom (spurious revert) but not the inert control.
- rejected: safe-revert persists-before-confirm (spec's explicit "re-persists on timeout" design; the curated-mode set + screen-clamp make a truly unusable persisted state near-unreachable); `ApplyVideo`-on-every-Apply window snap-back (already adjudicated review-1; audio-pattern parity, applies the user's own persisted resolution); MSAA on the wrong viewport (the independent in-engine gate read `viewport.msaa_3d` flip live 0/1/2 across tiers — empirically applied); `ContentScaleFactor` no re-clamp at apply (defense-in-depth only; the slider is bounded 0.75–1.5 and MigrateForward guards NaN); `ui_scale` not snapped to the 0.25 grid (cosmetic, self-heals on next apply, hand-edit-only); safe-revert timers not disarmed on Settings close (`Close()` sets `Visible=false` — hides not frees — so the child timers survive and revert fires; the modal dialog captures input, so the panel can't close while armed); `KeyLight` not nulled on match teardown (follows the existing `_ctx.Cam`/`_ctx.Minimap` null-guard pattern exactly); shadow-atlas 16-bit hardcoded (already adjudicated review-1); `SelectedResolution` literal fallback & Keep-only dialog UX (defensive / spec-designed); windowed size == screen height pushing the title bar off-screen (minor, common, out of scope); six verification-gap coverage observations (runtime surface is contracted to the In-Engine Gate by the intent's own `Block If`; the gate re-ran independently and PASSED — these are coverage notes, not defects).

## Design Notes

**Global-vs-scene apply split (why two seams).** Audio already proves the pattern: `SettingsManager.Apply()` pushes global state (`AudioServer`) and fires `OnSettingsChanged` → `MainScene.ApplySettingsToSystems` for scene objects (camera/minimap). Video follows it exactly. Everything reachable from a tree Node — `DisplayServer`, `GetWindow()`, `GetViewport()`, `RenderingServer` — lives in a new `ApplyVideo()` so it applies at `_Ready` (relaunch restore) even from the menu with no match running. Only `DirectionalLight3D.ShadowEnabled` needs the scene light, so it rides the MainScene bridge against the new `SceneContext.KeyLight`, null-guarded like every other bridge push (the light is absent in menus).

**Window-mode map (Godot 4).** `windowed`→`WindowMode.Windowed`; `borderless` (windowed-fullscreen)→`WindowMode.Fullscreen` (Godot's "Fullscreen" is borderless windowed); `fullscreen` (exclusive)→`WindowMode.ExclusiveFullscreen`. Resolution (`WindowSetSize`) is primarily meaningful in windowed/borderless; the curated list is filtered to `≤ ScreenGetSize`. The project already stretches `canvas_items`/`expand` (`project.godot:27-28`), so anchored UI rescales for free; `ContentScaleFactor` is the UI-scale (DPI) lever and, combined with the re-anchored panels, is what makes AC-3 hold.

**Honesty strip is verify-and-lock, not remove.** Story 3.11 already removed the ranked/MMR/online-count/unbuilt-Campaign elements and left the shell reading Skirmish 1–4 with a real Multiplayer lobby (Epic 9 is done, so online matchmaking ships — the `MainMenuOverlay.cs:117` tooltip is honest, keep it). 11.7's honesty deliverable is therefore the sweep + the in-engine inventory assertion + preserving that state; adding a Campaign/N-3 element would violate the invariant AND 13.1's ownership. This is the same sourced divergence 3.11 documented, carried forward as the epic's exit criterion in miniature.

**Reflow via anchors (no listener).** The four command-card panels bake `GetViewport().GetVisibleRect().Size` at build time, so they never move on resize. Converting them to `BottomLeft`/`BottomRight` anchor presets with fixed offsets makes Godot recompute their position against the live logical viewport size every frame — covering both window resize and `ContentScaleFactor` changes with no `SizeChanged` subscription. Editor panels already use `Center` anchoring (they track center automatically) — verify only.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors (C# is not hot-loaded; required before any in-engine run).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter SettingsData` -- expected: extended round-trip/migration tests pass.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-in-engine-gate.ps1` -- expected: PASS (In-Engine Gate artifact present — this story touches `src/UI/**`, `src/Core/Bootstrap/**`, `MainScene.cs`).

**Manual checks (In-Engine Gate, required):**
- Drive the Title screen; read every nav entry's label + tooltip via a tree walk; assert the set against the shipped-feature list (no Campaign/MMR/online-count; Skirmish reads 1–4).
- Open Settings → Graphics; change window mode and resolution → confirm the safe-revert dialog counts down and reverts on timeout; change vsync/quality/UI-scale → confirm apply with no revert dialog; A/B quality Low vs High in a running match and compare shadow presence via a `godot_runtime_state` digest / screenshot.
- Launch a match at 1080p, resize the window (or raise UI scale), and confirm the command-card/inventory/worker/ability panels stay pinned to their corners with no clipping.

### In-Engine Gate - 2026-07-30
- surface: Title/Mode-Select honesty sweep; Settings → Graphics (window mode/resolution/vsync/quality/UI-scale + 15s safe-revert); HUD command-card reflow; quality-tier shadow A/B in a live Alpha Skirmish match.
- launched: godot_editor_edit run (assembly reload succeeded), then godot_exec to emit `pressed` on Title PLAY → Skirmish Setup Launch, and to drive the Settings OptionButtons + APPLY & SAVE.
- digest: Title Buttons = [PLAY, MULTIPLAYER, CREATE, BROWSE, REPLAYS, SETTINGS, GENERATE MAP (AI), QUIT] (no Campaign/MMR/online-count). settings.json after safe-revert timeout: {"schema_version":3,"resolution_width":1920,"resolution_height":1080,"window_mode":"windowed","vsync":true,"quality_preset":"medium","ui_scale":1}. Resolution dropdown re-synced to "1920 × 1080"; DisplayServer.window_get_size()=(1920,1080). At content_scale_factor=1.5 logical viewport=(1280,720); inventory panel global end=(1270,710), bottom-left panels global pos=(10,535) end.y=710. KeyLight (energy 1.2): Low→shadow_enabled=false, High→shadow_enabled=true, viewport msaa_3d=Msaa4X on High.
- asserted: Honesty — entry set matches the shipped-feature list, no unbuilt-system advertisement (expected vs observed identical). Persist — schema stamped 3 and all six video fields round-trip (expected defaults 1920×1080/windowed/vsync-on/medium/1.0). Safe-revert — resolution change armed the dialog and timeout reverted window+dropdown to 1920×1080 and re-persisted (expected). Reflow — panels re-pin to the 1280×720 logical corners on UI-scale change (expected bottom-right end 1270,710 / bottom-left y 535–710). Quality — Low disables the key-light shadow, High enables it + MSAA 4× (expected per tier). No runtime errors.
- result: PASS


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Follow-up review pass on the already-converged Story 11.7. Ran all five review layers (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate) against the full baseline→HEAD diff. The independent in-engine gate re-ran and PASSED (honesty inventory, per-tier shadow+MSAA A/B, UI-scale panel re-pin, safe-revert timeout). Triaged the findings and applied two patches: (1) the safe-revert dialog no longer arms on an inert resolution change while in Borderless/Fullscreen (where `ApplyVideo` never resizes), and (2) `MigrateForward` now floors/caps a corrupt resolution so a sub-pixel `settings.json` can't boot a 1×1 window that safe-revert would never rescue. One UX gap (Resolution control not greyed in non-windowed modes) was deferred; sixteen findings were rejected (most already adjudicated in review-1 or empirically disproven by the live gate).

**Files changed:**
- `godot/src/UI/SettingsPanel.cs` — `MaybeArmSafeRevert` gates the resolution-delta arm behind windowed mode (window-mode change still always arms).
- `godot/src/Core/Definitions/SettingsData.cs` — `MigrateForward` resets `resolution_width/height` to 1080p when either is outside `[640×480, 16384×16384]`.
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` — added `Video_CorruptResolution_ResetsToDefault` (theory) + `Video_ValidResolution_IsPreservedByMigration`.
- `_bmad-output/implementation-artifacts/deferred-work.md` — one new deferred entry (resolution control not disabled in non-windowed modes).

**Verification:**
- `dotnet build godot/godot.csproj` — Build succeeded, 0 errors (14 pre-existing warnings).
- `dotnet test … --filter SettingsData` — Passed! 23/23 (2 new resolution-guard cases green).
- `tools/verify-in-engine-gate.ps1` — PASS (in-engine artifact present; 6 Godot-coupled files detected). The prior In-Engine Gate block remains valid: the patches only affect non-windowed resolution arming (the gate drove a windowed resolution change, which still arms) and pure `MigrateForward` data (covered by the new tests).

**Residual risks:** Runtime behaviors (live apply, safe-revert Keep path, reflow beyond 1080p/scale-1.5) remain covered only by the one-shot in-engine gate, per the intent's own verification contract (`Block If` gates on bridge reachability) — no automated regression net there, as designed. Two pre-existing items and one new UX gap are logged in `deferred-work.md`. Residual artifact in the working tree: `_bmad-output/implementation-artifacts/sprint-status.yaml` (orchestrator-owned, modified before this run; left in place).
