---
status: blocked
epic: 15
story: 15-10
dw_ids: [DW-126, DW-127]
warnings: [multiple-goals]
---

# Story 15-10: Scenario Settings panel + New-Scenario empty-canvas flow (DW-126 / DW-127)

## Auto Run Result

Status: blocked
Blocking condition: **Stale premise.** Both deferred-work entries this story exists to resolve are contradicted by the current `master` tree. Story **6.7** (Epic 6, already shipped) built the New-Scenario empty-canvas flow (DW-127, *fully* delivered) and a live scenario-authoring surface covering map name/author/description/players/size, all six win conditions, and per-slot starting resources (DW-126, *functionally* delivered). What DW-126 literally still asks for — a single **standalone** "Scenario Settings" panel and removal of the win-condition duplication — is a cosmetic/organizational consolidation of already-working, F5-reset-coupled UI, which DW-126 itself rates "not urgent, no functional conflict today." Building-vs-closing a named-deliverable key on a now-false premise is a tracker judgment for Alec (same class as 15-1 and 15-3); dev-auto will not fabricate a large, regression-risky refactor unattended.

**Change:** No code changed. This session ran clarify-and-route, recompiled the stale `epic-15-context.md` (was older than `epics.md`), then verified DW-126/DW-127 against the actual source tree rather than trusting the ledger, and surfaced the finding.

### Finding — the DW premises are false against current `master` (clean tree)

**DW-127 "No New-Scenario empty-canvas origination flow" — FULLY DELIVERED (Story 6.7).**
The ledger claims (2026-07-25) "no `new ScenarioData()`-from-empty UI path exists anywhere in `src/`." It does now:
- `ScenarioData.CreateBlank(name, author, description, players, size)` — `godot/src/Core/Definitions/ScenarioData.cs:1081` — a Godot-free factory producing a validator-safe blank map (flat terrain, empty entity arrays, 2–4 spread start slots).
- `MapPropertiesPanel.OpenNewMapDialog` — `godot/src/CreationSuite/MapPropertiesPanel.cs:37` — the New-Map modal collecting name/author/description/suggested-players/size, reachable today via the always-on Edit-mode corner panel's **"New Map…"** button (`WinConditionPhase.cs:199, 232`).
- `WinConditionPhase.CreateNewMap` — `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs:299` — persists the blank through `MapWritePipeline.RunNewMap` (HARD validate-before-write, DW-329) to `res://resources/data/scenarios/{slug}.json`, with duplicate-name protection.
DW-127's own closure line ("when a future epic adds a real New-Scenario empty-canvas flow…") is satisfied.

**DW-126 "No standalone Scenario Settings panel" — FUNCTIONALLY DELIVERED (Story 6.7); only a cosmetic consolidation + already-low-priority dedup remain.**
DW-126's decision wanted a surface for *map name/author, win condition, per-slot starting resources*. Every one of those is editable on the current tree:
- **Win condition** — all six options (2 built-in + 4 T1 presets with inline params) in the corner panel: `WinConditionPhase.cs:45-187`, writing `ScenarioData.WinCondition` / `WinConditionSpec`.
- **Map name / author / description / players / size** — `MapPropertiesPanel.BuildPropertiesEditor` (`MapPropertiesPanel.cs:88`), hosted **live** inside that same corner panel (`WinConditionPhase.cs:206-207`).
- **Per-slot starting resources (StartOre / StartCrystal)** — editable via the EntityPlacer StartPos spinners (`EntityPlacer.cs:102-103, 250-251, 312-346`) → `MainScene.SetStartSlotEconomy` (`MainScene.cs:1569`) → `ScenarioData.UpdateStartSlotEconomy`. (Story 6.7 patch 3.)

The genuinely-unbuilt residual of DW-126 is narrow and non-defective:
1. The settings are **not** a single standalone `ScenarioSettingsPanel.cs` reached by a `Ctrl+<letter>` hotkey — they live in an always-on corner panel titled "Win Condition"/"Map Properties" plus the placement-palette spinner.
2. The win-condition control is **still duplicated** in `OnboardingPanel` (`OnboardingPanel.cs:376-385`) — but the two surfaces are already kept in sync via `MainScene.RefreshWinConditionUi` → `SceneContext.WinConditionUiRefresh` (the DW-126 partial fix noted in the ledger). DW-126 explicitly calls this "not urgent, no functional conflict today."

### Why this HALTs rather than builds (two observably-different outcomes, nothing in the intent selects between them)

- **(A) Close** DW-126 (as substantially resolved by 6.7) and DW-127 (as fully resolved by 6.7), and retire/mark-done the `15-10` sprint key — mirroring **15-3** ("verified the charter against the current source tree rather than trusting the ledger… surfaced the tracker decision to Alec").
- **(B) Build** a dedicated consolidation: a standalone `ScenarioSettingsPanel` (`Ctrl+E`/`Ctrl+R` are free per the `EditorHotkeys` table) that absorbs win-condition + map-properties + per-slot economy into one discoverable editor, migrates them out of `WinConditionPhase`, and removes the OnboardingPanel win-condition copy. This re-homes working UI whose corner panel *also owns the F5 Edit⇄Play authored-start reset* (`WinConditionPhase.cs:252-289`) — real regression surface for debatable value, and it touches a stack of double-guarded surfaces (`EditorHotkeys` + its red test, the `ISetupPhase[]` phase array + `ScenePhaseOrder.Canonical` + `PhaseOrderTest`, `SceneContext`, `MainScene.ToggleEditorPanel`).

DW-126's recorded 2026-07-25 "Build a unified panel" decision predates Story 6.7's delivery of the map-properties/New-Map growth, so it was made on the now-false premise. Per the standing Epic-15 discipline (a ledger `goldens: moves`/gap line is a **suspicion, not a fact**; a prose-only invariant had "silently gone false" twice already), dev-auto will not pick (B) unattended.

**Recommendation for Alec (his call — not applied):**
1. **Close DW-127** as resolved by Story 6.7 (empty-canvas New-Map flow shipped).
2. **Close DW-126** as functionally resolved by Story 6.7, *or* re-file its narrow residual (standalone consolidated panel + onboarding win-condition dedup) as a fresh, explicitly-scoped cosmetic story if you still want the single-panel UX — noting the F5-reset coupling and the multi-surface phase/hotkey guards it must not break.
3. Flip `15-10` in `sprint-status.yaml` to `done` (or retire it as a sprint key the way §§15-4..15-9 were) once you've closed the two DW ids. Either is a one-line text edit; I did not make it — done-marking vs retiring a named-deliverable key is a tracker judgment.

**Files changed:** None (source/tests). `epic-15-context.md` was recompiled (stale-cache refresh, not story work). This result record created.

**Verification:** All claims checked directly against `godot/src` on the clean `master` tree, not taken on the ledger's word:
- `ScenarioData.CreateBlank` present (`ScenarioData.cs:1081`); New-Map modal + persist path read end-to-end (`MapPropertiesPanel.cs:37-80`, `WinConditionPhase.cs:199-341`).
- Corner-panel win-condition (all 6) + live `BuildPropertiesEditor` host read in full (`WinConditionPhase.cs:45-207`).
- Per-slot economy write path traced `EntityPlacer` spinner → `MainScene.SetStartSlotEconomy:1569` → `ScenarioData.UpdateStartSlotEconomy`.
- OnboardingPanel win-condition duplicate + its `RefreshWinConditionUi` sync confirmed (`OnboardingPanel.cs:346-386`).
- No build/test run and **no In-Engine Gate run** — no diff was produced (zero code changed), so the gate is not applicable to this HALT.
