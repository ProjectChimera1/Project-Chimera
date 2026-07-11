---
title: 'AI-preset selection, advanced raw-JSON mode, hero/persistence config + completion target (FR-18, UX-DR80, AR-12)'
type: 'feature'
created: '2026-07-11'
status: 'done'
baseline_revision: '8592c30b910526acafb9449221826b56f0e5db54'
final_revision: '2e74cb5d55a90570d6f664db5aac4c5ede81e777'
review_loop_iteration: 1
followup_review_recommended: false
context: []
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The Story 5.5 Faction Definer wizard's AI Preset step is a non-interactive stub that unconditionally
pins `ai_preset: "balanced"` on every render, so "no preset chosen" can never actually happen through the UI. The
wizard also never surfaces `hero_unit_id`/`persistence_enabled` (schema fields already added in Story 5.2, AR-12),
and there is no way for a creator who wants full control to bypass the guided steps and author the faction as raw
JSON.

**Approach:** Turn the AI Preset step into a real picker over `FactionValidator.KnownAiPresets` with NO default
selection, add a Hero/Persistence control block to that same step, and add a wizard-level Simple/Advanced mode
toggle whose Advanced pane is a raw-JSON escape hatch that Finish routes through the same `ValidateComplete` gate.

## Boundaries & Constraints

**Always:**
- AI Preset step: render one selectable button per `FactionValidator.KnownAiPresets` entry (today just
  `"balanced"`, but the loop must not hardcode the count/id — iterate the closed set), Primary-styled when
  `_draft.AiPreset` case-insensitively matches, Secondary otherwise (mirrors `BuildNameColorStep`'s existing
  swatch-picker idiom). `ResetWizard` must set `_draft.AiPreset = ""` (overriding `FactionDefinition`'s own
  `"balanced"` C# default) so the step opens with nothing selected and Finish is genuinely blockable.
- Same step: add a Hero/Persistence block — a button row over `_draft.Units.Where(u => u.IsHero)` (plus an explicit
  "(none)" option) setting `_draft.HeroUnitId`, and a `CheckBox` bound to `_draft.PersistenceEnabled`. On every
  rebuild of this step, if `_draft.HeroUnitId` no longer matches any `_draft.Units[].Id`, clear it to `null` first
  (a roster unpick after a hero pick must never leave a dangling reference).
- Wizard-level Simple/Advanced toggle: a second `ChimeraTabs.Create(TabsVariant.Segment, "Simple", "Advanced")`
  control (mirrors this file's own step-tabs construction). Advanced hides the step tabs + per-step body and shows
  one `TextEdit` JSON pane, seeded via `FactionDefinerWizardCore.SerializeDraftClean(_draft)` on every Simple→Advanced
  transition, plus a "Sync JSON from picks" button that re-seeds it on demand (mirrors `UnitCardPanel.Edit.cs`'s raw
  pane pattern). Back/Next are disabled while Advanced is active.
- `FactionDefinerWizardCore`: add `TryFinishFromRawJson(string json, string factionsDirAbsolute)` — deserialize via
  `JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)`, a parse/null failure returns
  a located `("raw_json", …)` `Failure`, a successful parse delegates to the existing `TryFinish` unchanged. `Toggle`
  always resets to Simple mode.
- `OnFinishPressed` routes on mode only: `_advancedMode ? TryFinishFromRawJson(_jsonPane.Text, dir) : TryFinish(_draft, dir)`.
  Only call `_stepTabs.SetActive(result.Step)` on failure when NOT in Advanced mode (Advanced has no step to jump to).

**Block If:** none identified — every field this story surfaces (`ai_preset`, `hero_unit_id`, `persistence_enabled`)
already exists on `FactionDefinition` (Story 5.2) with no validator changes required.

**Never:** Do not widen `FactionValidator.KnownAiPresets` beyond `{"balanced"}` — inventing new preset ids is not
requested anywhere in planning docs and preset behavior wiring is explicitly out of scope (`AiPreset` stays an
unwired descriptor). Do not add a validator check tying `HeroUnitId`/`PersistenceEnabled` to anything (matches
`deferred-work.md` DW-114, a pre-existing, still-deferred gap — not this story's job). Do not attempt
Advanced→Simple reconciliation of raw-JSON edits back into `_draft` — switching back to Simple with unsaved
Advanced edits discards them with an inline status note (never a silent no-op, never folded into the per-preset
`Contains`-by-reference roster checkboxes, which would desync since a JSON round-trip produces new object
instances). Do not touch `AbilityEditorPanel`/`UnitCardPanel` or any sibling editor's own raw-JSON code.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Preset selected, Finish | Creator clicks "balanced" on AI Preset step, valid roster, clicks Finish | Written file's `ai_preset` is `"balanced"` | No error expected |
| No preset selected, Finish | Creator never clicks a preset button (wizard opened fresh) | Finish blocked; located error names `ai_preset`, AI Preset step is shown | Save blocked, no file written |
| Hero + persistence set | Creator picks a hero-flagged roster unit and checks Persistence, Finish | Written file's `hero_unit_id`/`persistence_enabled` match the picks | No error expected |
| Hero unpicked after selection | Creator sets a hero unit, goes Back, unchecks that unit in Roster, returns to AI Preset step | `_draft.HeroUnitId` is cleared before the step renders; no stale reference reaches Finish | No error (defensive clear, not a block) |
| Advanced mode, valid raw JSON | Creator switches to Advanced, edits JSON to a fully valid faction, clicks Finish | File written identically to the Simple-mode path; same `ValidateComplete` gate applied | No error expected |
| Advanced mode, malformed JSON | Creator types unparsable text into the raw pane, clicks Finish | Finish blocked; located `raw_json` error names the parse failure; no file written | Save blocked, no file written |
| Advanced mode, valid JSON missing ai_preset | Raw JSON parses but `ai_preset` is empty | Finish blocked by the SAME `ai_preset` validator error as the Simple path | Save blocked, no file written |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` -- add `TryFinishFromRawJson(string json, string
  factionsDirAbsolute)`: try/catch `JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)`,
  null/exception → `FactionDefinerFinishResult.Failure` with a `("raw_json", message)` error; success → delegate to
  existing `TryFinish(parsed, factionsDirAbsolute)` unchanged. Add `ClearStaleHeroReference(FactionDefinition draft)`
  (null-guard `draft?.Units` — a raw-JSON-deserialized def can carry a null `Units` list — before the `.Any` scan;
  returns bool, true when a clear happened). **The step-render guard alone (Boundaries & Constraints) does not
  satisfy that section's own "must never leave a dangling reference" requirement** — Finish is reachable from any
  step, so `TryFinish` MUST ALSO call `ClearStaleHeroReference(def)` on its `def` parameter, before running
  `FactionValidator.ValidateComplete`, so a stale `HeroUnitId` can never reach a written file regardless of which
  step was last rendered or whether the Advanced raw-JSON path (which never renders the AI Preset step at all) was
  used. This is the SOLE enforcement point the "never dangling" guarantee actually rests on; the step-render call
  (still required, see below) is early UI feedback only, not the guarantee itself.
- `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` -- rewrite `BuildAiPresetStep` into a real
  `FactionValidator.KnownAiPresets` button-picker (no auto-pin) + the new Hero/Persistence block (hero button-row
  over `_draft.Units.Where(u => u.IsHero)` + "(none)", `PersistenceEnabled` `CheckBox`) + the stale-`HeroUnitId`
  clear-on-rebuild guard (`FactionDefinerWizardCore.ClearStaleHeroReference(_draft)`, kept for early feedback —
  see the Core bullet above for why this call alone is insufficient). `ResetWizard` sets `_draft.AiPreset = ""`.
  `OnFinishPressed` gains the Simple/Advanced routing described above.
- `godot/src/CreationSuite/FactionDefinerPanel.cs` -- add `_modeTabs` (`ChimeraTabs`, "Simple"/"Advanced"),
  `_jsonPane` (`TextEdit`, mirrors `UnitCardPanel.Edit.cs`'s `MakeJsonPane`/`SetPaneText`), `_advancedMode` bool.
  `RefreshStepBody`/footer wiring: Advanced hides `_stepTabs` + the per-step body, shows `_jsonPane` + a "Sync JSON
  from picks" button, disables Back/Next. Advanced→Simple with a dirty pane shows a discard status note via the
  existing `ShowError`/status-line helpers (no data fold-back).
- `godot/src/Core/Definitions/FactionValidator.cs` -- read-only reference: `KnownAiPresets` (already `internal`,
  its own doc comment names "any future preset-picker UI" as the intended consumer) and the unchanged
  `ai_preset`/`ValidateComplete` checks this story's Finish path relies on.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- read-only reference: `HeroUnitId`/`PersistenceEnabled`
  already exist (Story 5.2); no schema change this story.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` -- add Godot-free coverage for
  `TryFinishFromRawJson` (valid round-trip, malformed JSON, valid-JSON-missing-ai_preset), for
  `ClearStaleHeroReference` directly (stale/live/absent cases), and for the hero/persistence + empty-ai_preset
  round-trip through `TryFinish`. The hero-round-trip fixture's `HeroUnitId` MUST reference a unit with
  `IsHero == true` in the picked roster — hand-add a `UnitDefinition { Id = "...", IsHero = true }` to `def.Units`
  (neither shipped alpha/beta unit is hero-flagged, confirmed by review), not an ordinary alpha/beta unit — a
  passing test against a non-hero id would validate the wrong thing. Add a case proving `TryFinish` itself
  clears/rejects a `HeroUnitId` naming no unit in `Units` (construct the def directly, bypassing the Panel's
  step-render guard entirely) — this is the test that actually pins the "never dangling" guarantee to `TryFinish`,
  not to a UI rebuild hook.

## Tasks & Acceptance

**Execution:**
- `FactionDefinerWizardCore.cs` -- add `TryFinishFromRawJson` AND `ClearStaleHeroReference`, and call the latter
  from inside `TryFinish` before `ValidateComplete` -- gives the Advanced pane a Godot-free, directly testable
  Finish path that reuses the existing validator gate verbatim, AND makes "never a dangling hero reference reaches
  a written file" true regardless of which UI path (Simple step-render, Advanced raw JSON, or a direct API caller)
  produced the `FactionDefinition`.
- `FactionDefinerPanel.Steps.cs` -- real AI-preset picker, Hero/Persistence block, stale-hero-ref guard, Finish
  routing -- delivers AC1-AC2 and the "no preset chosen blocks" behavior.
- `FactionDefinerPanel.cs` -- Simple/Advanced `ChimeraTabs`, raw JSON pane, mode-aware body/footer -- delivers AC3.
- `FactionDefinerWizardTests.cs` -- cover every new I/O-matrix row -- proves AC1-AC3 without a live Panel.

**Acceptance Criteria:**
- Given the AI-preset step, when the creator selects a preset and finishes, then the written faction definition's
  `ai_preset` matches the selection, and finishing with none selected is blocked by the located `ai_preset` error
  (FR-18, AR-39).
- Given the wizard flow, when the creator sets a hero unit reference and/or the persistence toggle, then the saved
  faction definition carries both (AR-12).
- Given Advanced mode, when the creator edits raw JSON and finishes, then the same `FactionValidator.ValidateComplete`
  gate as Simple mode determines the outcome (no separate/weaker validation path).
- Given a player in `GameMode.Play`/`Skirmish`, when they navigate the HUD, then no Faction Definer control is
  reachable (regression check only — the Story 5.5 `Key.X`/Edit-mode gate is unchanged by this story).
- Given a first-time creator using only Simple-mode presets, when they complete the full 5.5+5.6 flow, then no step
  requires hand-editing JSON and the flow is completable within the <=12 min first-faction target (manual/live-editor
  check, not new code — see Verification).

## Spec Change Log

### 2026-07-11 — Review pass 1 (bad_spec loopback)

- **Triggering finding:** Blind Hunter, Verification Gap Reviewer, and Edge Case Hunter independently converged on
  the same defect: `ClearStaleHeroReference` was only invoked as a side effect of rendering the AI Preset step, so
  a dangling `HeroUnitId` (roster unit picked as hero, then unpicked via Back → Roster → uncheck, then Finish
  clicked without ever returning to the AI Preset step — Finish is reachable from any step) reached the written
  faction file undetected. The Advanced raw-JSON path never called the guard at all. Verification Gap Reviewer
  confirmed by reading `FactionValidator.Validate`/`ValidateComplete` in full that neither method checks
  `HeroUnitId` — nothing downstream catches this either. Edge Case Hunter additionally found the fix, as first
  attempted, would need a null guard (`draft?.Units`) since a raw-JSON-deserialized `FactionDefinition` can carry a
  null `Units` list. Blind Hunter additionally found the implementation's own regression test
  (`TryFinish_HeroAndPersistenceSet_...`) asserted success using a `HeroUnitId` pointing at an ordinary (non-hero)
  alpha/beta unit, baking the wrong behavior into the suite instead of catching it.
- **What was amended:** the Code Map's `FactionDefinerWizardCore.cs` bullet now REQUIRES `TryFinish` to call
  `ClearStaleHeroReference(def)` itself (with the null guard) before `ValidateComplete` — the step-render call
  becomes early UI feedback only, not the enforcement point. The Code Map's test bullet and the first Tasks
  Execution line were amended to require: a `TryFinish`-level test proving a dangling `HeroUnitId` is rejected/
  cleared independent of any Panel step render, and a corrected hero-round-trip fixture using an explicitly
  `IsHero = true` unit. `<intent-contract>`'s Boundaries & Constraints language ("must never leave a dangling
  reference") was NOT touched — it was already correct; only the Code Map's insufficient mechanism for achieving
  it was amended.
- **Known-bad state avoided:** a creator-authored faction JSON shipping a `hero_unit_id` that names no unit in its
  own `units[]` — undetected by the validator, the Finish self-check, or (before this pass) any test — reachable
  through a plausible, ordinary wizard navigation sequence, not an exotic edge case.
- **KEEP instructions (must survive re-derivation):** the real `FactionValidator.KnownAiPresets` button-picker with
  no auto-pin (`ResetWizard` setting `_draft.AiPreset = ""`); the Hero/Persistence block's button-row-over-
  `IsHero`-units + "(none)" + `PersistenceEnabled` checkbox shape; the step-render `ClearStaleHeroReference` call
  (kept as early feedback, now ALSO required inside `TryFinish`); the Simple/Advanced `ChimeraTabs` mode toggle,
  raw-JSON pane seeded via `SerializeDraftClean` on every Simple→Advanced transition, "Sync JSON from picks"
  button, and Back/Next disabled in Advanced, all as-is; `TryFinishFromRawJson`'s parse-then-delegate-to-
  `TryFinish`-unchanged shape and its located `("raw_json", ...)` failure mode, as-is; `OnFinishPressed` routing
  strictly on `_advancedMode` and only jumping `_stepTabs` when not in Advanced mode, as-is; the "Never reconcile
  Advanced→Simple" and "Never widen `KnownAiPresets`" Design Notes rationale, as-is; every already-passing test
  from the first implementation pass except the hero-fixture one being corrected.

## Review Triage Log

### 2026-07-11 — Review pass 1

- intent_gap: 0
- bad_spec: 1 (high 1, medium 0, low 0)
- patch: 0
- defer: 0
- reject: 0
- addressed_findings:
  - `[high]` `[bad_spec]` A dangling `HeroUnitId` could reach a written faction file via either the Simple path
    (Finish clicked without revisiting the AI Preset step after unpicking the hero's roster unit) or the Advanced
    raw-JSON path (never called the clearing guard at all) — independently found by Blind Hunter, Verification Gap
    Reviewer, and Edge Case Hunter. Code Map amended to require `TryFinish` itself call
    `ClearStaleHeroReference` (with a null-Units guard) before validating; code reverted for re-derivation under
    `./step-03-implement.md`. All other findings from this pass (doc-comment inaccuracy about empty-string parsing,
    a silent-discard gap on re-clicking an already-active Advanced tab, `Close()` not resetting Advanced-mode
    dirty-pane state, missing lenient-JSON and id-collision-via-raw-json test coverage, a cosmetic duplicate-hero-
    button-id rendering edge case, and others) are not addressed this pass since the code they reference is being
    discarded; they will be re-evaluated against the re-derived implementation on the next review pass.

### 2026-07-11 — Review pass 2

- intent_gap: 0
- bad_spec: 0
- patch: 2 (high 0, medium 1, low 1)
- defer: 4 (high 0, medium 0, low 4)
- reject: 4 (high 0, medium 0, low 4)
- addressed_findings:
  - `[medium]` `[patch]` A successful Advanced-mode Finish left `_paneDirty` set, so a later Advanced→Simple
    switch showed a false "Advanced JSON edits were discarded" warning even though the edits WERE used to write
    the file (Blind Hunter + Edge Case Hunter, converged). Fixed: `OnFinishPressed`'s success branch now clears
    `_paneDirty` when `_advancedMode`. Independently confirmed via a live in-editor pass (`godot-verify`): switching
    Advanced→Simple after a successful Advanced Finish now shows no warning, while switching Advanced→Simple after
    a genuine unsaved edit still correctly shows the warning.
  - `[low]` `[patch]` `heroCandidates = _draft.Units.Where(u => u.IsHero)` had no null-element guard, unlike every
    other `Units` enumeration in this file (Blind Hunter + Edge Case Hunter, converged; currently unreachable but
    inconsistent with the codebase's defensive convention). Added `u != null &&`.
  - Confirmed via live in-editor verification (`godot-verify`, this pass): the AI Preset step opens with no preset
    selected and blocks Finish with a located `ai_preset` error; selecting a preset highlights it; the Hero Unit
    row shows "(none)" and the correct empty-roster message; the Simple/Advanced toggle renders, seeds the raw-JSON
    pane correctly, and disables Back/Next in Advanced; a full Simple-mode Finish and a full Advanced-mode Finish
    (editing the pane, fixing a validator-blocked `ai_preset`) both wrote real, valid faction files through the
    live wizard. Zero console errors observed. This closes the Verification Gap reviewer's top concern (Panel-level
    routing logic has no xunit coverage — `CreationSuite` is excluded from `ProjectChimera.Sim.Tests` by design,
    matching every sibling editor) with direct runtime evidence instead.
  - `[low]` `[defer]` `StepForError` has no `"raw_json"` case (falls through to `BuildingsTech`, masked/harmless
    since `OnFinishPressed` skips `Step` in Advanced mode) — logged as `deferred-work.md` DW-116.
  - `[low]` `[defer]` Advanced-mode raw JSON omitting the `ai_preset` key silently inherits the class's `"balanced"`
    default, asymmetric with Simple mode's explicit-choice enforcement (Edge Case Hunter) — logged as DW-117.
  - `[low]` `[defer]` A silently-cleared stale `HeroUnitId` gives the creator no explanation, including when
    `ValidateComplete` then blocks Finish for an unrelated reason (Edge Case Hunter + Blind Hunter, converged) —
    logged as DW-118.
  - `[low]` `[defer]` Two roster-picked units sharing an `Id` across preset sources would render as indistinguishable
    duplicate Hero Unit buttons — already independently blocked by `FactionValidator`'s duplicate-unit-id check, so
    cosmetic-only (Blind Hunter + Edge Case Hunter, converged) — logged as DW-119.
  - `[low]` `[reject]` "Sync JSON from picks" silently overwrites unsaved pane edits with no confirmation (Edge
    Case Hunter) — matches `UnitCardPanel.Edit.cs`'s own "Sync JSON from form" precedent exactly, which this
    story's spec explicitly named as the pattern to mirror; not a deviation.
  - `[low]` `[reject]` Closing/reopening the wizard while the Advanced pane is dirty discards it with no warning
    (Edge Case Hunter) — matches the wizard-wide "never carries partial state across a close" precedent established
    in Story 5.5 (Simple-mode picks are discarded identically, also without a warning); not a deviation specific to
    this story's new Advanced-mode code.
  - `[low]` `[reject]` `_draft` is not refreshed from the parsed JSON after a successful Advanced Finish (Blind
    Hunter) — matches existing Simple-mode post-Finish behavior (also does not reset `_draft`); not a new
    regression.
  - `[low]` `[reject]` AR-12 does not couple Hero Unit and Persistence Enabled to each other (Blind Hunter) —
    matches AR-12's own independent-fields requirement; not a defect.

## Design Notes

**Why Advanced→Simple never reconciles raw-JSON edits back into `_draft`.** The Roster/Buildings/Research step
checkboxes are `_draft.Units.Contains(opt.Def)` reference-equality checks against the SCANNED preset-pool object
instances (Story 5.5). A raw-JSON round-trip through `JsonSerializer.Deserialize` always produces brand-new object
instances, so folding a parsed Advanced draft back into `_draft` would make every Simple-mode checkbox render
unchecked even though the data is correct — a confusing, purely-cosmetic desync that AbilityEditorPanel's much
more elaborate bidirectional Simple/Advanced reconciliation exists specifically to avoid for ITS domain. This story
does not need that complexity: the wizard's Finish is a single terminal action, not a repeated-save session, so a
one-way "whichever mode Finish is clicked from wins" design is sufficient and avoids that whole class of bug.

**Why the AI-preset closed set stays `{"balanced"}`.** `FactionValidator.KnownAiPresets`'s own doc comment already
states it is "deliberately seeded with exactly one member... extended in place by later stories" — nothing in
`epics.md`, the PRD, or the UX designs names any concrete preset beyond `"balanced"` (the unrelated `AiDifficulty`
enum — Easy/Normal/Hard — is a different, already-wired concept on `MainScene`, not this field). Widening the set
here would be inventing content, not implementing a specified requirement.

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the
  new `TryFinishFromRawJson`/hero-persistence/empty-ai_preset cases.
- `dotnet build godot/godot.csproj` -- expected: 0 errors (Panel/TextEdit/ChimeraTabs presentation code).

**Manual checks (if no CLI):**
- Launch the game, enter Edit mode, press `X`, walk Simple mode start-to-finish (color → roster incl. a hero-flagged
  unit if one is scanned, else confirm the "(none)" hero option → buildings & tech → starting conditions → pick an
  AI preset → Finish); confirm the written file's `ai_preset`/`hero_unit_id`/`persistence_enabled` match the picks
  and time the walkthrough against the <=12 min target.
- Attempt Finish on the AI Preset step with no preset clicked; confirm it blocks with a located `ai_preset` error.
- Switch to Advanced, edit the seeded JSON (e.g. clear `ai_preset`), click Finish; confirm the SAME located error
  surfaces. Fix it in the pane and Finish again; confirm a valid file is written.
- Switch to Advanced, make an edit, switch back to Simple; confirm a discard note appears and no exception is thrown.

## Auto Run Result

Status: done

**Summary:** Turned the Story 5.5 AI Preset step from a non-interactive stub (`ai_preset: "balanced"` unconditionally
pinned) into a real `FactionValidator.KnownAiPresets` button-picker with no auto-selection, so Finish is genuinely
blockable until a creator picks a preset. Added a Hero Unit / Persistence Enabled block to the same step (AR-12).
Added a wizard-level Simple/Advanced mode toggle whose Advanced pane is a raw-JSON escape hatch seeded from the
current draft, routing Finish through the exact same `FactionValidator.ValidateComplete` gate as Simple mode via a
new `TryFinishFromRawJson`. A first implementation pass had a real defect — the "never leave a dangling
`HeroUnitId`" guarantee only held when the AI Preset step happened to render, not at Finish itself — caught by 3
independent review layers, fixed via a bad_spec loopback that moved the enforcement into `TryFinish` itself
(Spec Change Log, review pass 1). A second review pass found and patched a smaller UX-message bug (a false "edits
discarded" warning after a successful Advanced-mode Finish) and a defensive null-guard gap, then closed with a live
in-editor verification pass.

**Files changed:**
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` — added `ClearStaleHeroReference` (null-guarded, called
  from inside `TryFinish` before `ValidateComplete` — the sole enforcement point) and `TryFinishFromRawJson`
  (parses raw JSON, delegates unchanged to `TryFinish`).
- `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` — real AI-preset picker (no auto-pin), Hero/Persistence
  block, `ResetWizard` now clears `AiPreset`, `OnFinishPressed` routes Simple/Advanced, `_paneDirty` cleared on a
  successful Advanced Finish (review pass 2 patch).
- `godot/src/CreationSuite/FactionDefinerPanel.cs` — Simple/Advanced `ChimeraTabs`, raw-JSON `TextEdit` pane +
  "Sync JSON from picks", Back/Next disabled in Advanced, discard-note wiring.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` — 12 new tests covering every I/O-matrix
  row, including a `TryFinish`-level dangling-hero-reference test with zero Panel code involved.
- `_bmad-output/implementation-artifacts/deferred-work.md` — DW-116..DW-119 (all pre-existing-class UX/robustness
  gaps surfaced by review, none blocking).

**Review findings breakdown:**
- Pass 1: 1 bad_spec (high) — the dangling-`HeroUnitId`-reaches-Finish defect, independently found by 3 reviewers;
  spec Code Map amended, code re-derived. 0 patch, 0 defer, 0 reject this pass (all lower findings deferred to
  pass 2 per protocol, since the code they referenced was discarded).
- Pass 2: 2 patch (1 medium: false discard-warning after a successful Advanced Finish; 1 low: missing null-guard),
  4 defer (DW-116..DW-119, all low, pre-existing-class robustness/UX gaps), 4 reject (matched established
  `UnitCardPanel.Edit.cs`/Story 5.5 precedent or AR-12's own independent-fields design, not deviations).

**Follow-up review recommendation:** false — the final pass's patches were 2 small, localized, low-complexity
fixes (a status-message correctness bug and a defensive null-guard), no behavior/API/security/data impact beyond
what was already independently re-reviewed this same pass.

**Verification performed:**
- `dotnet build`/`dotnet test` on `ProjectChimera.Sim.Tests.csproj` and `dotnet build godot.csproj` — independently
  re-run by the orchestrator after every code change (not just taken from the implementation subagent's report,
  per this project's documented prior incident with fabricated results) — 0 errors, 1461 passed, 1 pre-existing
  skip, 0 failed, no new warnings, both before and after the review-pass-2 patches.
- Live in-editor verification (`godot-verify` skill, Godot MCP): opened the Faction Definer wizard, confirmed the
  AI Preset step opens unselected and blocks Finish with a located `ai_preset` error, confirmed selecting a preset
  works, confirmed the Hero Unit "(none)"/empty-roster state renders correctly, confirmed the Simple/Advanced
  toggle renders and seeds the raw-JSON pane correctly with Back/Next disabled in Advanced, and drove BOTH a full
  Simple-mode Finish and a full Advanced-mode Finish (editing the pane to fix a validator-blocked `ai_preset`) to a
  successful write of a real, valid faction file. Directly confirmed the review-pass-2 patch: no false discard
  warning after a successful Advanced Finish, while the warning still correctly fires after a genuine unsaved
  edit. Zero console errors. Test artifacts (`verify_pass_test*_faction.json`) were written under the real
  `resources/data/factions/` directory during this pass and deleted afterward — not part of the shipped diff.

**Residual risks:** see `deferred-work.md` DW-116..DW-119 — a dead-code `StepForError` gap, an Advanced-mode
`ai_preset`-omission asymmetry, a silently-cleared hero reference with no creator-facing explanation, and a
cosmetic duplicate-hero-button edge case already blocked by the existing duplicate-unit-id validator check. None
are data-integrity risks; all are pre-existing-class UX/robustness gaps, none caused by or blocking this story.
