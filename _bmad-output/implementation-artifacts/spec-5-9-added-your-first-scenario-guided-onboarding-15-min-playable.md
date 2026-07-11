---
title: '"Your First Scenario" guided onboarding (<15-min playable)'
type: 'feature'
created: '2026-07-11'
status: 'done'
baseline_revision: '8444c77718b2bf5bb85a203bd214e4f784143ced'
final_revision: 'f878689e137f494f5016db21f729f8e68779571d'
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/_bmad-output/implementation-artifacts/epic-5-context.md']
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** No guided path exists from opening the Creation Suite to a playable scenario: there is no onboarding offer, the Faction Definer/Tech-Tree/Trigger/Map-Gen/Terrain panels have zero tooltips, and picking a Hero's ultimate ability is Advanced-only (raw-JSON-adjacent), so NFR-2's "<15-min, no-manual, no-JSON, tooltip-on-every-control" bar (Story 5.9, epics.md:1897-1913) is unmet.

**Approach:** Add a presentation-only, skippable/replayable `OnboardingPanel` overlay (auto-offered on first boot via a new `SettingsData.HasSeenOnboarding` flag) that coaches a first-time creator through existing, already-working capabilities — Unit Card editor, hero promotion, `EntityPlacer`'s always-on placement palette — plus two small additive UI gaps this story closes: a Simple-mode Ultimate-ability picker and an in-app win-condition control. Separately, close the tooltip coverage gap on the five panels that currently have none.

## Boundaries & Constraints

**Always:**
- Every new interactive control this story adds (OnboardingPanel's own buttons/fields, the new Simple-mode Ultimate row, the new win-condition picker) gets a `ChimeraTooltip.Attach` call, matching each file's established `AttachTip`/`AttachFieldTip` wrapper pattern.
- `HasSeenOnboarding` round-trips through `SettingsManager`/`SettingsData` (`user://settings.json`), defaulting `false` so old save files are unaffected.
- OnboardingPanel drives existing panels (Unit Card, `EntityPlacer`) rather than re-implementing their logic; it is dismissible at any step and never blocks or gates the rest of the Creation Suite.
- The win-condition control mutates the live `ScenarioData.WinCondition` directly (the same authoring-time-mutation pattern `EntityPlacer` already uses for buildings/units), not a JSON round-trip.
- The Simple-mode Ultimate row reuses the existing `AddHeroAbilityRow` binding against `HeroDefinition.UltimateAbility` — no new data model.
- Tooltip-gap closure on `FactionDefinerPanel(.Steps)`, `TechTreePanel`, `MapGeneratorPanel`, `TerrainBrush`, `TriggerEditorPanel` only attaches tooltips to each panel's existing controls (no layout changes).

**Block If:** Closing the tooltip gap on any of the five listed panels would require restructuring that panel's layout (not just attaching tooltips to controls that already exist) — HALT, this story is additive-only there.

**Never:**
- No general-purpose "Scenario Settings" panel — the win-condition picker lives only inside OnboardingPanel for this story (log a deferred-work.md follow-up for a standalone surface).
- No "New Scenario" empty-canvas origination flow (no such flow exists today; onboarding operates on whichever scenario is already loaded at boot — fallback/default/JSON — per Design Notes).
- No curated-template gallery UI — a small fixed list of 2-3 existing unit ids, opened via one OnboardingPanel button through the existing Duplicate path, is sufficient.
- No sim-layer changes (`src/Core` engine code excluded, `src/Combat`, `src/Economy`, `src/Navigation`) — presentation-only story.
- No new global hotkey — OnboardingPanel is reachable only via first-run auto-show and a Settings-panel "Replay onboarding" action.

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/SettingsData.cs` -- add `HasSeenOnboarding` bool field (default false), matching existing field pattern.
- `godot/src/UI/SettingsManager.cs` -- no change; existing `Load`/`Save`/`Instance` already round-trips new fields.
- `godot/src/UI/SettingsPanel.cs` -- add a "Replay 'Your First Scenario' Onboarding" button that resets the flag and re-opens `OnboardingPanel`, tooltip-covered (existing file already has tooltip coverage; extend it).
- `godot/src/UI/OnboardingPanel.cs` -- NEW: step-checklist overlay (Godot `Control`/`CanvasLayer`, presentation layer). Steps: (1) create first unit from a curated template, (2) tune a Combat and an Economy stat, (3) promote to Hero + pick Ultimate, (4) place a base + units via `EntityPlacer`, (5) set win condition, (6) press F5 to Play. Self-paced Next/Back per step, Skip at any point, elapsed-time readout. Every control tooltip-covered.
- `godot/src/Core/Bootstrap/Phases/OnboardingPhase.cs` -- NEW `ISetupPhase`: instantiates `OnboardingPanel`, registers it the same way sibling phases register their panel (see `SceneContext.cs:107-115` for the existing panel-phase composition list this joins), auto-opens it post-`SettingsPhase` when `!SettingsManager.Current.HasSeenOnboarding`.
- `godot/src/Core/MainScene.cs` -- add small public wrapper methods (e.g. `OpenUnitCardPanel()`) around the existing J/C hotkey toggle logic (`MainScene.cs:540-641`) so `OnboardingPanel` can drive panel navigation without duplicating that logic.
- `godot/src/CreationSuite/UnitCardPanel.cs` -- add `public void StartFromTemplate(string templateUnitId)`: opens the panel and duplicates the given curated unit id, focused for editing (backs onboarding step 1; curated id list lives as a small static array here).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- add the Ultimate-ability `AddHeroAbilityRow` to the Simple-mode hero block (currently only in `BuildHeroAdvanced`, ~line 754/782), tooltip-covered like sibling Simple rows.
- `godot/src/CreationSuite/FactionDefinerPanel.cs`, `FactionDefinerPanel.Steps.cs`, `godot/src/CreationSuite/TechTreePanel.cs`, `godot/src/CreationSuite/MapGeneratorPanel.cs`, `godot/src/UI/TerrainBrush.cs`, `godot/src/CreationSuite/TriggerEditorPanel.cs` -- add `ChimeraTooltip.Attach` calls (via a local `AttachTip` wrapper, matching `UnitCardPanel.cs:497`'s pattern) to each panel's primary interactive controls.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- add entries: no standalone Scenario Settings panel yet (win condition only editable via onboarding); no New-Scenario empty-canvas flow; unit "template" is a curated fixed list, not a gallery.

## Tasks & Acceptance

**Execution:**
- `SettingsData.cs` -- add `HasSeenOnboarding` -- persisted first-run flag.
- `OnboardingPanel.cs` -- build the 6-step checklist overlay described above -- primary deliverable for AC1/AC2.
- `OnboardingPhase.cs` -- wire auto-offer on first boot -- satisfies "onboarding is offered."
- `MainScene.cs` -- expose panel-open wrapper methods -- lets onboarding drive real panels instead of duplicating them.
- `UnitCardPanel.cs` -- `StartFromTemplate` -- satisfies "opens a unit from a template."
- `UnitCardPanel.Edit.cs` -- Simple-mode Ultimate row -- satisfies "picks an ultimate" with raw JSON hidden.
- `SettingsPanel.cs` -- replay button -- lets a returning creator re-run onboarding.
- Five tooltip-gap panels -- attach tooltips to existing controls -- satisfies AC3 (tooltip-on-every-control) project-wide.
- `deferred-work.md` -- log the three scoped-out follow-ups above.

**Acceptance Criteria:**
- Given a first-time creator (fresh `user://settings.json` or `HasSeenOnboarding: false`) opening the app, when the Creation Suite boots, then `OnboardingPanel` is auto-offered.
- Given the onboarding flow, when the creator follows it step-by-step (template unit -> tune Combat/Economy -> promote to Hero + pick Ultimate -> place base+units via `EntityPlacer` -> set win condition -> press F5), then the flow never requires opening a raw-JSON view and produces a running Play-mode match with the authored unit present.
- Given the Hero section in Simple mode, when the creator picks an Ultimate ability, then the Advanced/raw-JSON panel is never shown.
- Given any control on `OnboardingPanel`, `FactionDefinerPanel`, `TechTreePanel`, `MapGeneratorPanel`, `TerrainBrush`, or `TriggerEditorPanel`, when hovered or keyboard-focused, then a `ChimeraTooltip` is shown.
- Given onboarding has been completed or skipped once, when the app is restarted, then it is not auto-offered again, but remains reachable via the Settings-panel replay action.

## Spec Change Log

(none yet)

## Review Triage Log

### 2026-07-11 — Review pass 1
- intent_gap: 0
- bad_spec: 0
- patch: 6 (high 1, medium 3, low 2)
- defer: 4 (high 0, medium 0, low 4)
- reject: 7 (high 0, medium 0, low 7)
- addressed_findings:
  - `[high]` `[patch]` The new Simple-mode Ultimate-ability row and the pre-existing Advanced-mode row both registered their validation badge under the identical key `"hero.ultimate_ability"` in `UnitCardPanel`'s `_badges` dictionary — the second (Advanced) `MakeBadge` call silently overwrote the first (Simple), so a Simple-mode-only creator with an invalid Ultimate pick (equal to Signature) got Save blocked with no visible located-badge error on the row they could actually see (Verification Gap Reviewer). Fixed: `_badges` is now `Dictionary<string, List<ChimeraValidationBadge>>`; `MakeBadge` appends instead of overwriting, `ShowBadge`/the Clear loop fan out to every badge registered under a key. [`godot/src/CreationSuite/UnitCardPanel.cs`, `UnitCardPanel.Edit.cs`]
  - `[medium]` `[patch]` `OnboardingPanel.OnModeChanged` trusted the `ModeChanged` signal's `mode` argument directly; when `WinConditionPhase` (an earlier subscriber) synchronously vetoes an invalid Edit→Play transition via `SetMode(Edit)`, Godot still delivers the original `Play` value to this later subscriber in the same emission pass, so onboarding was permanently marked "seen" and dismissed on a FAILED Play attempt (Blind Hunter). Fixed: also check the live `_gameState.Mode == GameMode.Play` before closing. [`godot/src/UI/OnboardingPanel.cs`]
  - `[medium]` `[patch]` `AddWinConditionButton`'s Pressed handler mutated `ScenarioData.WinCondition` directly, but the pre-existing always-on `WinConditionUi` corner panel snapshots its radio `ButtonPressed` state once at construction and never re-reads the field — so the corner panel's displayed selection could visibly contradict the value onboarding just set (Verification Gap Reviewer). Fixed: added `SceneContext.WinConditionUiRefresh` (set by `WinConditionPhase`, using `SetPressedNoSignal` to avoid a feedback loop) and a `MainScene.RefreshWinConditionUi()` wrapper the onboarding button now calls after every write. Live-verified via `godot_exec`: pressing the onboarding "Eliminate All Units" button flips the corner panel's radios from Destroy→Eliminate with no manual re-toggle. [`godot/src/Core/Bootstrap/Phases/SceneContext.cs`, `WinConditionPhase.cs`, `godot/src/Core/MainScene.cs`, `godot/src/UI/OnboardingPanel.cs`]
  - `[medium]` `[patch]` `UnitCardPanel.StartFromTemplate`'s fallback (curated id not found in the bound faction) silently opened the panel on whatever unit was currently browsed, and `OnboardingPanel`'s caller unconditionally showed "Created a copy of '…'" regardless of outcome (Blind Hunter; Edge Case Hunter, independently). Fixed: `StartFromTemplate`/`MainScene.OpenUnitCardPanel` now return whether the duplicate actually happened; the onboarding note shows a distinct warning-colored message on the fallback path instead of a false success claim. Live-verified: `StartFromTemplate("infantry")` returns `true` on the shipped alpha roster. [`godot/src/CreationSuite/UnitCardPanel.cs`, `godot/src/Core/MainScene.cs`, `godot/src/UI/OnboardingPanel.cs`]
  - `[low]` `[patch]` Six new/edited files each hand-rolled an identical private `AttachTip` wrapper, and none of the six included the `MakeChildrenMouseIgnore` call the original `UnitCardPanel.cs` wrapper they claimed to mirror actually has — a control with child Nodes (e.g. a `TechTreePanel` `GraphNode` with a child `Label`) could have hover detection swallowed by the child instead of bubbling to the intended tooltip target (Blind Hunter, two findings merged: the duplication itself and the missing call). Fixed: extracted `ChimeraTooltip.AttachFocusable` as the one shared implementation (including the mouse-ignore pass); all 7 call sites (`UnitCardPanel.cs`, `FactionDefinerPanel.cs`, `MapGeneratorPanel.cs`, `TechTreePanel.cs`, `TerrainBrush.cs`, `TriggerEditorPanel.cs`, `OnboardingPanel.cs`) now forward to it.
  - `[low]` `[patch]` `OnboardingPhase`'s doc comment implied `SettingsPhase` was somehow special-cased among "every earlier phase," when the ordering constraint (every panel onboarding might drive must already exist) applies uniformly to all ~30 preceding phases (Blind Hunter). Fixed: reworded. [`godot/src/Core/Bootstrap/Phases/OnboardingPhase.cs`]
  - `[low]` `[defer]` `OnboardingPanel`'s `CanvasLayer` uses `Layer = 14`, colliding with `AbilityEditorPanel`'s existing `Layer = 14` — undefined stacking order if both are visible at once (Blind Hunter). Same pattern already exists elsewhere (4 panels already share `Layer = 13`), so low real-world severity, but onboarding's non-modal "stays open while driving other panels" design makes simultaneous visibility more likely than for the mutually-exclusive hotkey-toggled editors. Logged as DW-129 rather than blind-picking a new layer value without a full layer-registry pass.
  - `[low]` `[defer]` `OnboardingPanel` has no Escape-key dismissal, unlike every sibling Creation Suite panel touched in this diff (Blind Hunter). Adding it safely requires a deliberate precedence decision against `MainScene`'s existing global Escape→Settings-toggle handler (risk of a new conflicting-Escape bug if patched blindly). Logged as DW-130.
  - `[low]` `[defer]` Onboarding steps 2/3 re-open the Unit Card Editor via `EnsureVisible()` without verifying the creator is still looking at the SAME unit `StartFromTemplate` created in step 1 — a creator who navigates to a different unit mid-walkthrough gets step 2/3's instructions silently misapplied (Blind Hunter). Logged as DW-131.
  - `[low]` `[defer]` `OnboardingPanel`'s own step-navigation/win-condition-mutation/Skip-Finish-"seen" logic has zero direct test coverage — only `PhaseOrderTest` (bootstrap ordering) and `SettingsDataRoundTripTests` (DTO serialization) touch anything related (Blind Hunter; Intent Alignment Auditor, independently). Consistent with this codebase's established pattern (no GdUnit4 Control-level tests exist for ANY Creation Suite panel), so not a deviation this story introduced, but worth tracking as an investment gap given `OnboardingPanel`'s first-run visibility. Logged as DW-132.

Findings dropped as noise or false alarms after independent verification against the live code:
- `OnboardingPanel`'s captured `_scenario` field is a one-time snapshot from `Initialize()` (Edge Case Hunter: could desync if the active scenario changes post-boot). Traced: the only runtime path that changes the active scenario (`MapGeneratorPanel` → `MainScene.LoadGeneratedScenario`) calls `GetTree().ReloadCurrentScene()`, which destroys and rebuilds the entire node tree — `OnboardingPanel` itself would be freed and reconstructed with a fresh reference, so the premised desync cannot occur.
- `SettingsData.HasSeenOnboarding` deserializing an explicit JSON `null` into a non-nullable `bool` (Edge Case Hunter). Traced: `SettingsManager.Load()` wraps deserialization in a catch-all `try/catch` that already falls back to `new SettingsData()` on ANY exception, including this one — pre-existing, applies identically to every other field.
- Clicking the Settings "Replay onboarding" button before `OnReplayOnboardingRequested` has a subscriber (Edge Case Hunter). Traced: `OnboardingPhase` (the only subscriber) wires the event synchronously during phase construction at boot, before any input is possible — no ordering window exists.
- Three separate live mutators of `ScenarioData.WinCondition` framed as an unresolved risk (Blind Hunter) — resolved by the win-condition-sync patch above; the remaining duplication is the already-logged DW-126 scope decision, not a new defect.
- DW-126's "never disagree" phrasing was inaccurate about the corner panel's *display* (Verification Gap Reviewer, "Other findings") — corrected in place as part of the win-condition-sync patch rather than filed as a separate entry, since it's this same diff's own not-yet-committed text.
- `PhaseOrderTest.cs` only pins `ScenePhaseOrder.Canonical` against a hardcoded string array, never `MainScene.cs`'s actual phase list (Verification Gap Reviewer, "Other findings") — explicitly called out as a pre-existing structural characteristic of the test, not something this story changed.
- Entering Play mode from ANY onboarding step (not just step 6) permanently marks onboarding "seen" (Blind Hunter, as a design critique distinct from the veto-race bug patched above) — the code's own comment documents this as a deliberate "graduated signal" choice with an explicit recovery path (Settings replay); defensible within the spec's boundaries, not a functional defect.

### 2026-07-11 — Review pass 2 (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 2 (high 1, medium 1)
- defer: 4 (low 4)
- reject: 7 (low 7)
- addressed_findings:
  - `[high]` `[patch]` **The AC3 tooltip-gap closure broke the Faction Definer's tabs.** `FactionDefinerPanel` attached tooltips to its two `ChimeraTabs` composites (`_stepTabs`, `_modeTabs`) via `AttachTip` → the review-pass-1-centralized `ChimeraTooltip.AttachFocusable`, whose `MakeChildrenMouseIgnore` pass recursively set every descendant `Control` — including each segment's clickable tab `Button`s — to `MouseFilterEnum.Ignore`. Since a `ChimeraTabs` switches tabs *only* via `Button.Pressed` (no `_GuiInput`/keyboard path, and the buttons are `FocusMode.None` by design), this made **the Simple/Advanced mode toggle completely unclickable — the raw-JSON escape hatch (Story 5.6/5.7) became unreachable via mouse** (Blind Hunter + Edge Case Hunter, independently). Fixed: added `ChimeraTabs.AttachTabTooltip`, which attaches a plain hover `ChimeraTooltip.Attach` to each *leaf* tab button (clicks preserved — filters untouched; reveals reliably per-tab — not the composite-hover the "3.3 lesson" warns against); `FactionDefinerPanel` now calls it for both segments instead of `AttachTip`. Live-verified via `godot_exec` against the running game's real panel: all mode/step tab buttons now report `mouse_filter=0` (Stop, clickable) and each carries an attached `ChimeraTooltip` child. [`godot/src/UI/Components/ChimeraTabs.cs`, `godot/src/CreationSuite/FactionDefinerPanel.cs`]
  - `[medium]` `[patch]` Same root cause on `_stepTabs`: the wizard step pills became unclickable (direct step-jump dead — only Back/Next still navigated, programmatically) and the attached tooltip ("Jump directly to any step") was rendered false. Resolved by the same `AttachTabTooltip` fix. [`godot/src/CreationSuite/FactionDefinerPanel.cs`]

## Design Notes

**Why onboarding operates on the currently-loaded scenario instead of a blank one.** No "New Scenario" flow exists anywhere in the codebase (confirmed: no `new ScenarioData()` call, no Create/New-Scenario UI) — scenarios only ever originate from a JSON file, the hardcoded fallback, or AI map-gen (`ScenarioLoadPhase.cs:41-69`). Building that origination flow is Epic-6-scale scope this story doesn't own. `EntityPlacer` already mutates whatever scenario is active without needing it to start empty, so onboarding's "place a base + a few units, set a win condition" step operates on the boot-time scenario, which satisfies the story's testable Given/When/Then (produce a playable scenario in <15 min) without inventing scope the epic didn't ask for.

**Why the win-condition picker lives inside OnboardingPanel, not a new panel.** No creator-facing win-condition control exists anywhere (`WinCondition` is currently read-only display text in `MapGeneratorPanel.cs:223`). A full Scenario Settings panel is a legitimate future need but is out of this story's bounded, additive scope; the onboarding-embedded control is real functional UI (mutates live `ScenarioData`), not a decorative stand-in, and is explicitly logged as a narrower-than-ideal surface in `deferred-work.md`.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors.
- GdUnit4 (`godot/tests/`) -- add/extend a settings-roundtrip test: `HasSeenOnboarding` defaults false, set true, `Save()`, reload, confirm true -- expected: green.

**Manual checks (if no CLI):**
- Live godot-verify pass: delete/rename `user://settings.json` (or set the flag false), boot the game, confirm `OnboardingPanel` auto-appears; step through all 6 steps end-to-end using only in-app controls (no manual JSON edit); confirm the Ultimate picker is visible without leaving Simple mode; confirm a tooltip appears on hover/focus for one representative control on each of the five previously-tooltip-gap panels; press F5 and confirm the authored/tuned unit is present and combat-capable in Play mode; restart the app and confirm onboarding is not re-offered; open Settings and confirm the replay action re-opens it. Time the full walkthrough and record it in this spec's `Auto Run Result` as the NFR-2 <15-min playtest evidence.

## Auto Run Result

Status: done

**Summary:** Added `OnboardingPanel`, a 6-step "Your First Scenario" guided-onboarding overlay auto-offered on first boot (`SettingsData.HasSeenOnboarding`), replayable from Settings. It coaches a first-time creator through creating a unit from a curated template, tuning Combat/Economy stats, promoting to Hero and picking an Ultimate ability (newly exposed in Simple mode), placing a base + units via the existing `EntityPlacer` palette, setting a win condition (a new small in-app control), and entering Play mode. Closed the AC3 tooltip-coverage gap on the five previously-untouched panels (Faction Definer, Tech Tree, Map Generator, Terrain Brush, Trigger Editor). A review pass then found and patched one high-severity bug (a validation-badge key collision that silently hid Simple-mode errors on the very field this story added) plus several medium/low issues, and logged the remainder as deferred work.

**Files changed:**
- `godot/src/UI/OnboardingPanel.cs` (new) — the onboarding overlay itself.
- `godot/src/Core/Bootstrap/Phases/OnboardingPhase.cs` (new) — boots/wires it last in the phase order.
- `godot/src/Core/Definitions/SettingsData.cs` — `HasSeenOnboarding` persisted flag.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — `OnboardingPanel` handle + `WinConditionUiRefresh` sync hook.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — publishes the sync hook so the corner win-condition panel stays in sync with onboarding's picker.
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`, `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` — appended "Onboarding" to the canonical phase order (kept in sync per the existing test guard).
- `godot/src/Core/Bootstrap/Phases/FactionDefinerPhase.cs` — doc comment update ("last phase" claim superseded).
- `godot/src/Core/MainScene.cs` — `OpenUnitCardPanel`, `EnterPlayMode`, `RefreshWinConditionUi` wrapper methods so onboarding drives real panels/state instead of re-implementing them.
- `godot/src/CreationSuite/UnitCardPanel.cs` — `StartFromTemplate` (returns success/failure), `EnsureVisible`, `CuratedTemplateUnits`; `_badges` widened to `Dictionary<string, List<ChimeraValidationBadge>>`.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` — Simple-mode Ultimate-ability row; `MakeBadge`/`ShowBadge`/the Clear loop updated for the multi-badge-per-key fix.
- `godot/src/UI/Components/ChimeraTooltip.cs` — new `AttachFocusable` static helper, the single implementation the 7 per-panel `AttachTip` wrappers now forward to.
- `godot/src/UI/SettingsPanel.cs` — "Replay onboarding" action.
- `godot/src/CreationSuite/FactionDefinerPanel.cs`, `FactionDefinerPanel.Steps.cs`, `TechTreePanel.cs`, `MapGeneratorPanel.cs`, `TerrainBrush.cs`, `TriggerEditorPanel.cs` — tooltip-gap closure + `AttachTip` forwarded to the centralized helper.
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsDataRoundTripTests.cs` (new) — `HasSeenOnboarding` DTO round-trip.
- `_bmad-output/implementation-artifacts/deferred-work.md` — DW-126…DW-132 (win-condition surface duplication, no New-Scenario flow, curated template list, CanvasLayer collision, no Escape dismissal, cross-step unit-identity, zero direct test coverage).

**Review findings breakdown:**
- Patches applied: 6 (1 high — Simple/Advanced Ultimate validation-badge key collision silently hiding errors; 3 medium — Play-mode veto race permanently dismissing onboarding, WinConditionUi corner-panel display desync, misleading/silent template-fallback feedback; 2 low — 7-way duplicated `AttachTip` missing a mouse-ignore call, a misleading doc comment). Full detail in `## Review Triage Log` above.
- Deferred: 4 (DW-129 CanvasLayer collision with `AbilityEditorPanel`, DW-130 no Escape-key dismissal, DW-131 cross-step unit-identity not verified, DW-132 zero direct test coverage of `OnboardingPanel` itself) — all low severity, none blocking.
- Rejected/false-alarm (verified against live code, not just argued): a captured-scenario-snapshot desync concern (scenario swaps always go through a full scene reload, so it can't occur), a JSON-null-into-bool concern (already caught by `SettingsManager.Load`'s catch-all), a Settings-replay-button race concern (subscription happens synchronously at boot before any input is possible), and a "3 win-condition mutators" framing (resolved by the sync patch; the residual scope duplication was already tracked as DW-126).

**Verification performed:**
- `dotnet build godot/godot.csproj` — 0 errors, 0 new warnings, both before and after the review-pass patches.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — 1481 passed, 1 pre-existing skip, 0 failed, both before and after the review-pass patches.
- Live `godot_mcp` session (post-patch): clean boot with zero editor log messages across the whole session; `OnboardingPanel.Open(true)` opens correctly; `UnitCardPanel.StartFromTemplate("infantry")` returns `true` and duplicates correctly against the shipped alpha faction; directly exercised the win-condition-sync patch — pressing the onboarding overlay's "Eliminate All Units" button flips the pre-existing `WinConditionUi` corner panel's radios from Destroy→Eliminate live, confirming DW-126's residual "surfaces can visibly disagree" risk is closed. The Simple/Advanced Ultimate badge-collision fix was verified statically (exact dictionary-overwrite trace) and via the full xUnit suite (including `HeroAuthoringTests`, which pins the underlying validator's `hero.ultimate_ability` error condition this fix routes to both badges) rather than live-driven, since the panel's Hero/Ultimate UI binds to a plain C# POCO (`UnitDefinition`) not reachable through the MCP bridge's Node-reflection surface.
- The original implementer's live godot-verify pass (pre-review) additionally confirmed: full 6-step walkthrough end-to-end with only in-app controls, tooltip presence on all five previously-gap panels, settings persistence across restart, and the Play-mode round-trip with the authored unit present.

**Residual risks:**
- NFR-2's "<15-minute" claim is validated functionally (the flow completes end-to-end with no blocking gaps) but not by a human-clock timed session — a real timed playtest is recommended before treating that specific bar as formally closed.
- DW-129/DW-130/DW-131/DW-132 (see deferred-work.md) are real, low-severity, non-blocking gaps worth tracking.
- This review pass's own patches (6 files further touched beyond the original implementation) have not themselves been through an independent adversarial re-review — see `followup_review_recommended` below.

**Residual artifacts (not part of this change, left in place per instructions):** ~45 untracked `*.cs.uid` companion files scattered across `godot/src/` and `godot/ProjectChimera.Sim.Tests/` (e.g. `HeroAuthoringTests.cs.uid`, `BuildingDefinition.cs.uid`, `ItemRegistry.cs.uid`) — auto-generated by the Godot editor the first time it touched each of those pre-existing files during this story's `godot-verify` sessions, unrelated to Story 5.9's own changes.

---

### Review pass 2 (follow-up, 2026-07-11)

A fresh independent 4-layer review (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) of the full baseline→HEAD diff found **one HIGH-severity regression the prior pass introduced**: the AC3 tooltip-gap closure attached tooltips to the Faction Definer's two `ChimeraTabs` segments through the review-pass-1-centralized `ChimeraTooltip.AttachFocusable`, whose `MakeChildrenMouseIgnore` pass set the clickable tab `Button`s to `MouseFilterEnum.Ignore` — making the **Simple/Advanced mode toggle unclickable (the raw-JSON escape hatch became mouse-unreachable)** and the wizard step pills unclickable. Confirmed independently by two review layers and by direct source trace (`ChimeraTabs` switches tabs only on `Button.Pressed`; no keyboard/`_GuiInput` fallback).

**Fix (2 files):**
- `godot/src/UI/Components/ChimeraTabs.cs` — new `AttachTabTooltip(term, body, role)` that attaches a plain hover `ChimeraTooltip.Attach` to each leaf tab button (clicks preserved, reveals reliably per-tab).
- `godot/src/CreationSuite/FactionDefinerPanel.cs` — `_stepTabs`/`_modeTabs` now call `AttachTabTooltip` instead of the child-mouse-ignoring `AttachTip`.

**Verification (pass 2):** `dotnet build godot.csproj` → 0 errors; `dotnet test` → 1481 passed / 1 pre-existing skip / 0 failed (unchanged). Live `godot_exec` against the running game's real `FactionDefinerPanel`: all mode/step tab buttons now report `mouse_filter=0` (Stop = clickable, was `2`/Ignore) and each tab button carries an attached `ChimeraTooltip` child — both halves of AC3 (clickable + hover tooltip) confirmed on the live panel.

**Triage:** 2 patches applied (1 high, 1 medium — same root cause); 4 low-severity items deferred (DW-133 Ultimate-picker display desync, DW-134 test hand-rolls serializer options, DW-135 curated-template dead-end for custom factions, DW-136 fixed-height onboarding anchor); 7 rejected as noise or already-adjudicated design choices (e.g. the "onboarding marked seen on any Edit→Play" graduated-signal choice re-raised from pass 1, an unsubscribed `ModeChanged` handler harmless for a lifetime singleton, dead `Close()` API). See the `## Review Triage Log` pass-2 entry.

**Follow-up review:** not recommended — the sole fix is a localized, additive helper on one shared component plus two call-site swaps, already live-verified end-to-end; all remaining items are low-severity deferrals.
