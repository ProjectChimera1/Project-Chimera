---
title: 'Trigger debugging — variable watch, fired-trigger log, fire counters'
type: 'feature'
created: '2026-07-18'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '00a53c44cd8c897c12add2129605d2216573adcd'
final_revision: '326efc4'
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-7-13-complete-the-trigger-vocabulary-expression-state-reads-randomchoice-enable-disable-run-action-leaves-event-breadth.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-7-14-objectives-quest-log-and-the-match-briefing-surface.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Trigger authors have no runtime visibility. When a scenario's triggers misbehave there is no way to see which triggers fired and on what tick, how many times each fired, whether a trigger is currently enabled, or the live values of DSL variables. The director exposes per-*action* callbacks (`OnSpawnUnit`/`OnDisplayMessage`/…) but no per-*trigger* fire signal and no fire count; `DslVarReadback` has no public "enumerate all declared vars" API; and no debugging overlay exists.

**Approach:** Add a presentation-only trigger-debugging overlay (toggle key, Play-scoped) that reads four data streams, none of which perturb `SimChecksum`: (1) a **live variable watch** off the existing non-folded `DslVarReadback` rail (adding a pure read-side enumerate accessor); (2) a **tick-stamped fired-trigger log** (last N) and (3) **per-trigger fire counters**, both fed by a NEW Godot-free, **non-folded** observation buffer (`TriggerFireLog`) the director writes UNCONDITIONALLY at the single `FireTrigger` choke point; (4) each trigger's **enabled state** read directly from the already-folded `TriggerEnabledStore` (reads are free). Plus **filter/search** over the rows and **click-to-navigate** from a fired-log entry into the flat trigger editor. No new node kinds; **no `SimChecksum` bump, no `CanonicalModelHash` bump, no golden re-record.**

## Boundaries & Constraints

**Always:**
- **Checksum-neutral by construction.** The fired-trigger log + fire counters live in a NEW `SimulationHost`-owned, Godot-free `TriggerFireLog` (per-exec `int[]` counts + a fixed-capacity ring of `(execIdx, tick)` entries) that is **NEVER passed to `SimChecksum.Compute` and never registered with any `FoldInto`** — the exact non-folded posture documented for `DslVarReadback`/`MatchStats` (`SimChecksum.cs:54-69`). It is written **UNCONDITIONALLY** at `FireTrigger` (`ScenarioDirector.cs:1428`, called from `:1407`/`:1420`/`:1538`) on every fire, regardless of whether the overlay exists or is visible, so two runs (overlay open vs closed, buffer attached vs not) produce byte-identical `SimChecksum`. `SimChecksum.AlgoVersion` stays **21** (`:246`); `CanonicalModelHash.AlgoVersion` stays **14**; no golden re-recorded.
- **Sim purity of the instrumentation.** The observation write is a pure integer increment + ring append **after** the folded work in `FireTrigger`: no `using Godot`, no `float`/`double`/`Mathf`, no wall-clock, no string formatting / `int→string` in the tick. The tick stamp is the deterministic sim tick (an `int`, the same monotonic tick source that drives `_readback.Publish(_vars, ++_publishTick)` at `ScenarioDirector.cs:1178`). Trigger identity carried is the exec `idx` (int) and `ex.Trigger.Id` (int node id); the human-readable `Name` string is resolved **presentation-side only**, never in the tick.
- **Reset with the sim.** `TriggerFireLog` counts + ring clear/resize alongside `_triggerFired`/`_triggerCooldown` at `LoadScenario` (`ScenarioDirector.cs:528-529`) so an F5 Edit→Play re-apply starts fresh; the overlay rebuilds its rows when the `ScenarioData` reference changes (the `ObjectiveLogOverlay` `!ReferenceEquals(scenario,_boundScenario)` idiom, `ObjectiveLogOverlay.cs:127`).
- **Enabled state via the folded store's READ API.** Read `TriggerEnabledStore.IsEnabled(i)`/`Count` through `SimulationHost.TriggerEnabled` (`:129`) — a pure read; presentation NEVER writes it.
- **Variable watch via the existing read rail.** Add a NEW **pure read-side** enumerate accessor on `DslVarReadback` returning declared entries (name, scope, current scalar value / array marker, version); the overlay re-formats a row only on version change (the `CustomUiBridge` idiom). Scope covered = declared **Global + Per-player scalars + declared Global arrays** (the addressable variable set, `DslVarReadback.InitFromDeclarations` `:79-137`). Reuse `_ctx.Host.Readback` (`SimulationHost.cs:110`); add NO new folded state.
- **Presentation built to the design system, mirroring Story 7.14.** `TriggerDebugOverlay : CanvasLayer` mirrors `ObjectiveLogOverlay.cs`; `TriggerDebugOverlayPhase : ISetupPhase` mirrors `ObjectiveLogOverlayPhase.cs` (`Run()` → `AddChild` → `Initialize(late-bound getters)` → register `_ctx.TriggerDebugOverlay`); handle on `SceneContext` (`:94` sibling); pumped in `MainScene._Process` (`:816` neighbor). Toggle key = **`F2`** (verified unclaimed, no `Key.F2` in `src/`, no InputMap), wired Play-scoped **above** the Edit-mode guard (`MainScene.cs:625`), mirroring the F1 quest-log block (`:616-622`). Filter/search box = `ChimeraComponents.Input` (`Controls.cs:180`); rows = `FieldLabel`/`Tag`/`Readout`; the last-N log = a raw `ScrollContainer`+`VBoxContainer` (no `ItemList` factory exists — the `_rowHost` VBox pattern, `ObjectiveLogOverlay.cs:111`), capped by freeing the oldest child. No new `.tscn`, no InputMap file.
- **Click-to-navigate switches to the trigger editor focused on the fired trigger.** Clicking a fired-log entry switches `GameState` to Edit, opens the flat `TriggerEditorPanel` (`Toggle()`), and calls a NEW `TriggerEditorPanel.FocusTrigger(int triggerIndex)` that scrolls to + tints that authored `Triggers[]` row (rows built at `:335-370`, label precedent `:424`). The fired exec `idx` maps back to the authored `Triggers[]` index via `ex.Trigger.Id` (flat triggers). Row labels show `Name` (fallback `$"trigger {id}"`) plus the index to disambiguate non-unique names.

**Block If:**
- Observing fires genuinely cannot be isolated from the folded region (the only viable counter/log write sits inside folded state and can't be a non-folded parallel buffer) → HALT rather than bump `SimChecksum.AlgoVersion` / re-baseline goldens (the unattended-safety hazard project memory flags).
- The variable-watch enumeration would require surfacing `TriggerLocal`/loop scratch that is not in the read rail and cannot be exposed without a folded-state change → HALT rather than fold scratch.
- Click-to-navigate cannot be wired without changing sim/mode semantics beyond a presentation Edit-mode switch + a new editor focus API (e.g. it demands the match keep running under Edit, which the engine's mode model does not support) → HALT rather than redesign the Play/Edit model.

**Never:**
- No new folded sim state, no `SimChecksum` `AlgoVersion` bump, no `CanonicalModelHash` bump, no golden re-record — the observation buffer is presentation-only and non-folded.
- No sim writes from the overlay: opening/closing/filtering/navigating never mutates sim state; `SimChecksum` byte-identical open vs closed.
- No new node kinds, no `ActionTypes`/`FlatActionTypes` vocab, no new wire/replay format — this is observation + UI only.
- No strings in the tick — trigger names/labels resolve presentation-side; the tick carries only int exec idx / node id / tick stamp.
- No watching `TriggerLocal` loop scratch (lexically scoped, freed at trigger end — not a stable watchable value); the watch shows declared vars only.
- No `ILogSink` per-tick logging path (string-only, explicitly discouraged, `ILogSink.cs:14`); no new `.tscn` scenes; no InputMap file.
- Out of scope: any change to trigger evaluation semantics / fire ordering / cooldown / run-once behavior; the T3 graph-editor focus API (navigate targets the flat `TriggerEditorPanel` only).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Fire log + counters | seeded scenario, triggers fire over several ticks, overlay open in Play | ring log shows last-N fires (name + index + sim tick); per-trigger counters increment on each fire; two headless runs byte-identical `SimChecksum` (buffer non-folded) | first divergence via `GoldenChecksumReplay.CompareSequences` |
| Checksum neutrality | same run with overlay open vs closed AND `TriggerFireLog` attached vs not | byte-identical `SimChecksum` streams; `AlgoVersion` 21 unchanged; `CanonicalModelHash` 14 unchanged; no golden moved | guard fails → HALT: buffer leaked into the fold |
| Variable watch | declared Global/Per-player scalars + Global arrays, values change at runtime | watch rows show name/scope/value; re-format only on version bump; `TriggerLocal` scratch absent by design | undeclared name → not shown (declared-only) |
| Enabled state | `enable_trigger`/`disable_trigger` flips a trigger | that row reflects enabled/disabled from `TriggerEnabledStore.IsEnabled` next frame | reading the store never mutates it |
| Filter/search | user types a substring in the filter box | variable-watch + trigger rows filter to matching names; clearing restores all | empty query → all rows shown |
| Click-to-navigate | click a fired-log entry in Play | `GameState`→Edit, `TriggerEditorPanel` opens, the corresponding authored `Triggers[]` row scrolled-to + highlighted | exec→`Triggers[]` map miss → open editor unfocused, no crash |
| F5 Edit→Play round trip | fire triggers, F5 to Play, F5 back, F5 again | counters + log reset each Play; overlay rebuilds rows on scenario-ref change; no stale fire counts survive | transient UI state lost by design |
| Toggle scope | press F2 in Edit vs Play | Play: overlay visibility toggles, handled above `:625`, `SetInputAsHandled`; Edit: ignored (Play-scoped) | overlay handle absent in a reduced scene → null-guarded (F1 precedent) |

</intent-contract>

## Code Map

**Observation buffer (NEW, Godot-free, non-folded) + sim wiring:**
- `godot/src/Core/Sim/TriggerFireLog.cs` — **NEW** pure/Godot-free: per-exec `int[] _counts` + a fixed-capacity ring of `readonly struct FireEntry { int ExecIdx; int Tick; }`; `Record(int execIdx, int tick)`, `Reset(int execCount)`, read accessors (`Count(int execIdx)`, `ExecCount`, enumerate recent entries newest-first). **Never** folded / passed to `SimChecksum.Compute`.
- `godot/src/Core/Sim/SimulationHost.cs` — own a stable `TriggerFireLog` (mirror `TriggerEnabledStore` `:204`), share it into the `ScenarioDirector` ctor (`:207`), expose `public TriggerFireLog TriggerFireLog { get; }` (mirror `Readback` `:110` / `TriggerEnabled` `:129`). NOT wired into `EnableChecksums` (`:311`).
- `godot/src/Core/ScenarioDirector.cs` — accept the `TriggerFireLog` in ctor; in `FireTrigger` (`:1428`), **after** `ExecuteTopLevel` + the folded run-once/cooldown arming, `_fireLog.Record(idx, currentSimTick)` unconditionally; `_fireLog.Reset(execs.Count)` alongside `_triggerFired`/`_triggerCooldown` alloc at `LoadScenario` (`:528-529`). Tick stamp = the deterministic sim tick already available (the `_publishTick` source, `:1178`).

**Variable-watch read accessor (NEW pure read):**
- `godot/src/Core/Sim/DslVarReadback.cs` — add a public enumerate accessor over the already-published snapshot (declared Global/Per-player scalar names + Global array names via `_gNames/_pNames/_aNames`, `InitFromDeclarations` `:79-137`), returning name + scope + current value/version. Pure read; NO fold impact (readback already excluded, `:12`).

**Presentation (NEW overlay + phase, all code-built):**
- `godot/src/UI/TriggerDebugOverlay.cs` — **NEW** `CanvasLayer` mirroring `ObjectiveLogOverlay.cs`: `Initialize(Func<DslVarReadback?>, Func<TriggerFireLog?>, Func<TriggerEnabledStore?>, Func<ScenarioData?>, Action<int> navigate)`; `Toggle()` (`:117`); `Update()` (`:124`) pump reading the three read sources + rebuilding rows on scenario-ref change; sections for variable watch, fired-trigger log (ScrollContainer+VBox last-N), fire counters, enabled column; a `ChimeraComponents.Input` filter box; click handler on a log row → `navigate(triggersIndex)`.
- `godot/src/Core/Bootstrap/Phases/TriggerDebugOverlayPhase.cs` — **NEW** `ISetupPhase` mirroring `ObjectiveLogOverlayPhase.cs`: `Run()` constructs the overlay, `_ctx.Scene.AddChild`, `Initialize` with late-bound getters (`() => _ctx.Host?.Readback`, `() => _ctx.Host?.TriggerFireLog`, `() => _ctx.Host?.TriggerEnabled`, `() => _ctx.Scenario`, the navigate action), registers `_ctx.TriggerDebugOverlay = overlay`. `Name => "TriggerDebugOverlay"`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — add `public UI.TriggerDebugOverlay TriggerDebugOverlay = null!;` (`:94` sibling of `ObjectiveLog`).
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` — add `"TriggerDebugOverlay"` to the `Canonical` array (`:21-24`).
- `godot/src/Core/MainScene.cs` — construct `new TriggerDebugOverlayPhase(_ctx)` (`:437-438` area); F2 toggle branch, Play-scoped, above the `:625` Edit guard (mirror F1 `:616-622`, null-guarded); `_Process` pump `_ctx.TriggerDebugOverlay?.Update();` (`:816` neighbor); the navigate action = switch `GameState` to Edit + `_ctx.TriggerPanel.Toggle()` (open) + `_ctx.TriggerPanel.FocusTrigger(index)` (onboarding-wrapper precedent `:704-717`).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` — **NEW** `public void FocusTrigger(int triggerIndex)` scrolling the trigger list to + tinting row `triggerIndex` (rows `:335-370`; ensure visible/open first).

**Tests:**
- `godot/ProjectChimera.Sim.Tests/…/TriggerFireLogTests.cs` — **NEW**: `Record`/`Reset`/ring-cap/`Count` behavior; the **differential guard** — run a golden-replay scenario with a `TriggerFireLog` attached and assert `SimChecksum` byte-identical to a run without it (buffer non-folded); pin `SimChecksum.AlgoVersion == 21`.
- `godot/ProjectChimera.Sim.Tests/…/DslVarReadbackEnumerateTests.cs` — **NEW**: the enumerate accessor lists declared Global/Per-player/Global-array vars with values; `TriggerLocal` absent.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` — assert **no** golden changes (proves the story adds no fold churn).
- `godot/ProjectChimera.Sim.Tests/…/PhaseOrderTest.cs` — add the new phase to the pinned parity list.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Sim/TriggerFireLog.cs` -- NEW Godot-free per-exec counts + fixed-cap tick-stamped ring -- the non-folded observation buffer; the checksum-neutrality keystone.
- `godot/src/Core/Sim/SimulationHost.cs` -- own the `TriggerFireLog`, share into the director ctor, expose a getter; NOT wired into `EnableChecksums` -- host-owned stable ref, never folded.
- `godot/src/Core/ScenarioDirector.cs` -- record fires unconditionally at `FireTrigger` (after folded work) with the sim tick; reset the log at `LoadScenario` -- observe every fire with zero checksum impact; fresh per Play.
- `godot/src/Core/Sim/DslVarReadback.cs` -- add a pure read-side enumerate accessor over declared vars -- the variable watch source; no fold impact.
- `godot/src/UI/TriggerDebugOverlay.cs` + `godot/src/Core/Bootstrap/Phases/TriggerDebugOverlayPhase.cs` -- code-built `CanvasLayer` (watch + fired-log + counters + enabled + filter + click-to-navigate) and its setup phase, late-bound getters -- presentation-only, survives F5, never folded.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` -- overlay handle + phase-order registration -- wiring parity with the 7.14 overlays.
- `godot/src/Core/MainScene.cs` -- construct the phase, F2 Play-scoped toggle above the Edit guard, `_Process` pump, the Edit+open+`FocusTrigger` navigate action -- in-match toggle + navigation with no InputMap file.
- `godot/src/CreationSuite/TriggerEditorPanel.cs` -- NEW `FocusTrigger(int)` scroll-to + highlight -- the editor-side landing for click-to-navigate.
- `godot/ProjectChimera.Sim.Tests/…` -- `TriggerFireLog` unit + differential guard (byte-identical `SimChecksum` with/without the buffer, `AlgoVersion==21`), `DslVarReadback` enumerate unit, golden no-churn assertion, `PhaseOrderTest` update -- prove observation is checksum-neutral and the wiring holds.

**Acceptance Criteria:**
- Given a scenario whose triggers fire over several ticks, when the debug overlay is open in Play, then the fired-trigger log shows the last-N fires (trigger name + index + sim tick), the per-trigger fire counters increment on each fire, each trigger's enabled state reflects `TriggerEnabledStore`, and the variable watch shows live declared-variable values that update on version change.
- Given the same run executed with the overlay open vs closed (and with `TriggerFireLog` attached vs not), then two headless `SimChecksum` streams are byte-identical, `SimChecksum.AlgoVersion` is unchanged (21), `CanonicalModelHash.AlgoVersion` is unchanged (14), and no golden is re-recorded — the fired-log and counters are non-folded, presentation-only.
- Given the filter/search box, when the user types a substring, then the variable-watch and trigger rows filter to matching names and clearing restores all; and given a fired-log entry, when clicked, then the app switches to Edit mode, opens the flat trigger editor, and scrolls to + highlights the corresponding authored trigger row.
- Given an F5 Edit→Play round trip, when the sim re-applies, then the fire counters and fired-trigger log reset to empty and the overlay rebuilds its rows from the re-applied scenario; no stale fire counts survive.
- Given F2 pressed in Edit mode, then it is ignored (Play-scoped, above the Edit guard); and the overlay performs zero sim writes in any interaction (open/close/filter/navigate).

## Spec Change Log

_No `bad_spec` loopback occurred. The spec's intent-contract (including the explicit "map the fired exec back to its authored `Triggers[]` index via `ex.Trigger.Id`" requirement) was correct; the one high-severity review finding was a code deviation from that spec (the implementation used exec-as-authored index equality), fixed as a patch — not a spec defect._

## Review Triage Log

### 2026-07-18 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 0
- reject: 12: (high 0, medium 0, low 12)
- addressed_findings:
  - `[medium]` `[patch]` The production `SimulationHost`→director fire-log wiring had zero headless coverage: every fire-log test handed a buffer straight to a bare `ScenarioDirector`, so dropping the `SimulationHost.cs:216` wiring line would leave the whole suite green while the overlay silently rendered zero fires forever (the story's core value). Added `SimulationHost_WiresItsFireLogThroughTheDirector_AndClearsItOnReset` — loads a firing scenario into a real host, ticks, asserts `host.TriggerFireLog` observed the fire through production wiring, then `ClearForReset` empties it (also closing the untested `ClearForReset → TriggerFireLog.Clear()` line). Flagged by the Verification-Gap layer.
  - `[medium]` `[patch]` The variable-watch positional value↔name zip could misattribute a live value to the wrong name: `RebuildRows` gated only on scenario-ref / row-COUNT / faction drift, so a same-count in-place rename or reorder of a declared variable (which `ResetToAuthoredStart` re-applies on the same `ScenarioData` object) left each slot showing another variable's value. The overlay now also rebuilds on a declared-var / authored-trigger name-sequence signature change (`_watchSig`/`_trigSig`, cheap FNV-1a while Visible). Flagged by Blind Hunter and Edge Case Hunter (the count-drift backstop the prior pass added did not cover identity drift at equal count).
  - `[low]` `[patch]` The fired-trigger log could show stale pre-reset entries after an F5 re-apply whose post-reset fire total climbed straight back to the pre-reset high-water within one frame (a `match_start`-heavy scenario): `RefreshLog` gated a refresh on `TotalRecorded` equality, which cannot see that case. Added a `Generation` counter on `TriggerFireLog` (bumped on `Reset`/`Clear`, non-folded) and gated the fired-log re-sync on a generation change instead; the same edit switches the log teardown to `RemoveChild`-before-`QueueFree` so freed rows leave the container immediately. Pinned by a new `Generation_BumpsOnResetAndClear_EvenWhenTotalReturnsToSameValue` headless unit test. Flagged by Edge Case Hunter.
  - `[low]` `[patch]` The differential-guard test's comment over-claimed ("a regression that folded the buffer would diverge these two arms and fail here") — `SimChecksum.Compute` takes no fire-log parameter, so a future fold added via a new `Compute` argument would not be exercised by this two-arm comparison; and `RunChecksum`'s doc said "per-tick" while it folds once after the run. Corrected both comments to state precisely what the guard proves (the fire-log WRITE has no side effect on the folded stores) and that the un-versioned-fold regression is guarded by the `AlgoVersion` pin + golden suite. Flagged by the Verification-Gap and Blind Hunter layers.
  - _reject (unchanged behavior / by-design / out-of-scope / cosmetic — not fixed):_ fired-log full teardown+rebuild on each fire-tick with scroll-position loss (dev-only overlay capped at 40 rows; the prior pass already accepted this class, and a correct newest-first scroll-preserving rewrite is non-trivial and would risk the working overlay for marginal gain); graph-only triggers rendering a rank label past `Triggers[]` + a no-op click (navigation targets the flat panel only — the T3 graph editor is explicitly out of scope); click-to-navigate resetting the running match on the Play→Edit switch (documented by-design in Design Notes — the user clicked to go *edit* that trigger); the filter box not filtering the fired-log (the AC scopes the filter to the variable-watch + trigger rows); `FocusTrigger` indexing the panel's own `_scenario` (latent only — the panel and `_ctx.Scenario` reference the same live object in the real flow); the O(n²) exec→authored rank build at `LoadScenario` (sub-millisecond once-per-load even at hundreds of triggers); trigger rows shown in execution (priority) order but labeled by authored index (execution order is a legitimate view); the fire-tick `uint→int` cast wrapping negative past 2³¹ (~2.3 years of a single continuous session, non-folded/display-only); F2 consumed unconditionally in Edit mode (identical to the shipped 7.14 F1 precedent, F2 globally unclaimed — the prior pass's standing decision); the redundant `"N: trigger N"` label for unnamed triggers (cosmetic); the Godot-`CanvasLayer`/`Control`-coupled overlay/F2/navigate surface having no headless coverage and `PhaseOrderTest` not building `MainScene`'s real phase list (the established repo test boundary — already a documented residual).

### 2026-07-18 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 1, medium 1, low 3)
- defer: 0
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[high]` `[patch]` Exec index was treated as the authored `Triggers[]` index for trigger-row names, fire-counter labels, fired-log labels, and click-to-navigate — silently wrong under any non-default trigger `Priority` (exec order is Priority-desc/node-id-asc, diverging from authored order), so the tool mislabeled fired triggers and navigated to the wrong editor row. Flagged by all four layers; the spec already required mapping via `ex.Trigger.Id`. Added a director-computed exec→authored map (authored index = rank of each exec's trigger node-id) installed on `TriggerFireLog.SetAuthoredMapping`; the overlay now resolves names + navigation through `TriggerFireLog.AuthoredIndex`. Pinned by a new headless `ExecToAuthoredMapping_...UnderNonDefaultPriority` test.
  - `[medium]` `[patch]` The `DifferentialGuard_ChecksumByteIdentical_WithVsWithoutBuffer` test was trivially true: the director constructor coalesced `null → new TriggerFireLog()`, so both arms executed `Record()` — proving observation-neutrality, not that the fire-log WRITE is fold-neutral (its named invariant). Made `_fireLog` nullable with `_fireLog?.Record`/`?.Reset`, so the `null` arm genuinely skips the write; the guard now exercises write-present vs write-absent and would catch a future fold-leak. Flagged by the Intent-Alignment and Verification-Gap layers.
  - `[low]` `[patch]` Per-player variable-watch rows went stale when the local faction resolved/changed after rows were built (the version short-circuit gates on per-variable version, not per-slot). The overlay now rebuilds rows on a local-faction change. Flagged by Blind Hunter and Edge Case Hunter.
  - `[low]` `[patch]` Watch/trigger rows could positionally misalign (label vs value) if a declaration was edited in place and F5-re-applied without a `ScenarioData` reference swap (`ResetToAuthoredStart` re-applies the same object), since `RebuildRows` keyed only on reference identity. The rebuild condition now also fires on a declared-variable or trigger COUNT drift. Flagged by Blind Hunter and Verification-Gap.
  - `[low]` `[patch]` After click-to-navigate the debug overlay lingered visible over the trigger editor (and kept pumping in Edit). `NavigateToTrigger` now closes the overlay before switching to Edit. Flagged by Blind Hunter.
  - _reject (by-design / by-precedent, not fixed):_ F2 consumed unconditionally in Edit mode (identical to the shipped 7.14 F1 precedent; F2 is globally unclaimed so zero impact — a lone F2 divergence would itself be an inconsistency); per-frame `Enumerate` `List` allocation while the overlay is open (dev-only overlay, bounded, reduced from two enumerations to one by this pass's refactor); fired-log full-rebuild on a fire-tick (dev overlay, capped at 40 rows); the panel capturing mouse input over the top-left play area (inherent to a toggled panel overlay); the enabled-state momentarily rendering "on" for out-of-range execs during the Clear→Reset window (cosmetic, transient, self-correcting).

## Design Notes

**Why a non-folded observation buffer written unconditionally.** The story's hard invariant is "presentation-only, zero sim writes, checksum byte-identical open vs closed." Fire *counts* and a fired-trigger *log* cannot be derived by frame-polling (many sim ticks elapse per frame; a trigger can fire and re-arm between frames), so they must be captured at the sim fire site — the single `FireTrigger` choke point. The safety comes from two properties, together: the buffer is **never folded** (it never enters `SimChecksum.Compute`, mirroring the documented `DslVarReadback`/`MatchStats` posture at `SimChecksum.cs:54-69`), and the write is **unconditional** (it happens on every fire regardless of whether the overlay exists or is visible), so the sim performs byte-identical work in every configuration. Visibility gates only the presentation-side pull. The `TriggerFireLog` is Godot-free and `SimulationHost`-owned so it is exercised by headless determinism tests, not just manual verification.

**Declared-vars-only watch.** `DslVarReadback` publishes declared Global/Per-player scalars and declared Global arrays; `TriggerLocal`/loop scratch is lexically scoped and freed at trigger end (`Enter()`/`Exit()` around `FireTrigger`), so it has no stable cross-tick value to watch. The watch is therefore declared-vars-only by design — the addressable variable set — not a coverage gap.

**Click-to-navigate crosses the Play→Edit boundary.** The trigger editors are Edit-mode tools; the debug overlay runs in Play. Navigating to a trigger switches `GameState` to Edit, which resets the running match — this is inherent to the engine's mode model (the same teardown any Edit switch causes), not a defect. It is the accepted behavior: the user clicked to go *edit* that trigger. `FocusTrigger` is a net-new public method on the flat `TriggerEditorPanel`; the T3 graph editor is out of scope. The fired exec `idx` maps back to the authored `Triggers[]` index via `ex.Trigger.Id`; a map miss opens the editor unfocused rather than crashing.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean build, no analyzer/AOT violations in the sim layer.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~TriggerFireLog` -- expected: `Record`/`Reset`/ring-cap green; differential guard proves `SimChecksum` byte-identical with vs without the buffer; `AlgoVersion==21`.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~GoldenChecksumReplay` -- expected: green with **NO** golden change (no fold churn from this story).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~DslVarReadback` -- expected: enumerate accessor lists declared vars; `TriggerLocal` absent.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~VersionStamp` -- expected: `SimChecksum.AlgoVersion`=21 and `CanonicalModelHash.AlgoVersion`=14 unchanged.

**Manual checks (godot-verify):** in-editor, author a scenario with a few triggers + declared variables, F5 to Play, press F2: the overlay shows the variable watch (values update live), the fired-trigger log (last-N with tick stamps), per-trigger fire counters incrementing, and each trigger's enabled state; typing in the filter box narrows rows; clicking a fired-log entry switches to Edit and highlights that trigger in the trigger editor; F5 round-trip resets counters/log; the overlay open vs closed leaves the run behaving identically.

## Auto Run Result

Status: done

**Summary.** Implemented Story 7.15 — the presentation-only trigger-debugging overlay — in one pass, then hardened it through a four-layer review. The design keystone: a new Godot-free, non-folded `TriggerFireLog` observation buffer (per-exec fire counters + a fixed 256-entry tick-stamped ring) written UNCONDITIONALLY at the single `ScenarioDirector.FireTrigger` choke point, so a run with the overlay open/closed (or the buffer attached/absent) is byte-identical in `SimChecksum`. The overlay (`F2`, Play-scoped) reads four presentation-only streams — a live variable watch off the non-folded `DslVarReadback` rail (new `Enumerate` accessor), the fired-trigger log + fire counters off `TriggerFireLog`, and enabled state off the already-folded `TriggerEnabledStore` (a pure read) — plus filter/search and click-to-navigate into the flat trigger editor. **No `SimChecksum` bump (stays 21), no `CanonicalModelHash` bump (stays 14), no golden re-record.**

**Files changed (production):** `TriggerFireLog.cs` (NEW — observation buffer + exec→authored map), `SimulationHost.cs` (owns/shares/exposes the buffer, cleared on reset; NOT folded), `ScenarioDirector.cs` (nullable fire log; records fires unconditionally after the folded arming; resets + installs the exec→authored map at `LoadScenario`), `DslVarReadback.cs` (NEW pure `Enumerate`/`WatchVar` read accessor), `TriggerDebugOverlay.cs` (NEW `CanvasLayer` — watch/counters/enabled/fired-log/filter/navigate), `TriggerDebugOverlayPhase.cs` (NEW `ISetupPhase`), `SceneContext.cs` + `ScenePhaseOrder.cs` (handle + phase registration), `MainScene.cs` (F2 Play-scoped toggle, `_Process` pump, `NavigateToTrigger`), `TriggerEditorPanel.cs` (`FocusTrigger` scroll-to + highlight).

**Files changed (tests):** `TriggerFireLogTests.cs` (NEW — unit + real differential guard + exec→authored mapping under non-default priority), `DslVarReadbackEnumerateTests.cs` (NEW — declared-var enumeration, TriggerLocal excluded), `PhaseOrderTest.cs` (new phase pinned).

**Review findings breakdown:** 5 patches (1 high: exec→authored index mapping — was silently wrong under non-default trigger priority; 1 medium: the differential-guard test was trivially true — made the fire log nullable so the guard genuinely proves the write is fold-neutral; 3 low: per-player-watch faction-change staleness, row misalignment on in-place declaration edits, overlay lingering over the editor after navigate). 5 rejected (by-design/by-precedent/cosmetic). 0 intent_gap, 0 bad_spec, 0 defer.

**Verification.** `dotnet build godot.sln` → 0 errors (pre-existing CS8632/CS8604 warnings only, in untouched files). `dotnet test ProjectChimera.Sim.Tests` → **2715 passed, 1 skipped, 0 failed** (`CHIMERA_PERF_CEILING_SCALE=2`). Golden replay green with **no golden re-recorded**; `SimChecksum.AlgoVersion`=21 and `CanonicalModelHash.AlgoVersion`=14 unchanged (differential guard + version-stamp tests green). In-engine (godot-mcp): CREATE → F5 Play → F2 opens the overlay (real input path) rendering all four sections + filter box, filter accepts input, F2 closes it, zero runtime errors — re-confirmed after the review refactor.

**Residual risks.** The overlay / `MainScene` F2 + pump / `FocusTrigger` are Godot-`CanvasLayer`/`Control`-coupled and have no headless coverage (same manual-`godot-verify` disposition as Stories 7.13/7.14); the sim-side observation buffer, exec→authored mapping, and determinism-neutrality beneath them are exhaustively headless-tested. Click-to-navigate switches Play→Edit, which resets the running match — inherent to the engine's mode model, documented as intended, not a defect. For graph-only (T3) scenarios the exec→authored mapping is best-effort (a map miss opens the editor unfocused, no crash); the flat-trigger target is the story's scope.

### Follow-up review pass — 2026-07-18

**Summary.** A second, independent four-layer review (Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment) of the same baseline→HEAD diff. The Intent-Alignment auditor confirmed the change faithfully implements the intent with every load-bearing invariant intact (checksum neutrality, non-folded buffer, no evaluation-semantics change, `AlgoVersion` 21 / `CanonicalModelHash` 14 unchanged, exec→authored mapping correct) and no HALT condition tripped. The other three layers surfaced 4 fixable findings (all patches — 0 intent_gap, 0 bad_spec, 0 defer) and a dozen rejects (by-design / out-of-scope / cosmetic / established-repo-boundary).

**Patches applied this pass:**
- **(medium) Headless coverage for the production wiring.** Added `SimulationHost_WiresItsFireLogThroughTheDirector_AndClearsItOnReset` — the prior tests all bypassed the `SimulationHost`→director wiring, so a dropped wiring line would have left the suite green while the overlay showed zero fires.
- **(medium) Variable-watch value↔name misattribution.** The overlay now rebuilds rows on a declared-var / trigger name-sequence signature change, not just count drift — a same-count in-place rename/reorder no longer points a live value at the wrong name.
- **(low) Fired-log staleness after F5.** New non-folded `TriggerFireLog.Generation` counter (bumped on `Reset`/`Clear`) gates the fired-log re-sync, so an F5 whose post-reset total lands on the pre-reset high-water still clears stale rows; log teardown now `RemoveChild`s before `QueueFree`. Pinned by `Generation_BumpsOnResetAndClear_...`.
- **(low) Test-comment precision.** Corrected the differential-guard comment (it proves the fire-log write has no side effect on the folded stores; it does not prove a future `Compute`-signature fold stays neutral — that is the `AlgoVersion` pin + golden suite) and the `RunChecksum` "per-tick" doc.

**Files changed this pass:** `TriggerFireLog.cs` (generation counter), `TriggerDebugOverlay.cs` (identity-signature rebuild trigger + generation-gated fired-log reset + `RemoveChild`), `TriggerFireLogTests.cs` (host-wiring test, generation unit test, corrected guard/doc comments).

**Verification.** `dotnet build godot.sln` → 0 errors (pre-existing warnings only). `dotnet test ProjectChimera.Sim.Tests` → **2717 passed, 1 skipped, 0 failed** (`CHIMERA_PERF_CEILING_SCALE=2`); the two new tests account for the +2 over the prior pass. Golden replay green with **no golden re-recorded**; `SimChecksum.AlgoVersion` 21 / `CanonicalModelHash.AlgoVersion` 14 unchanged. The `TriggerDebugOverlay` presentation edits (signature rebuild, generation-gated `RefreshLog`) compile clean and their sim-side dependency (the generation counter) is headless-tested; the `CanvasLayer` render surface itself remains manual-`godot-verify` per the established repo boundary.

**Residual risks (unchanged).** The Godot-coupled overlay/F2/navigate surface still has no headless coverage — the documented repo boundary; this pass added the generation counter's headless coverage and the host-wiring coverage but the `CanvasLayer` rendering of the new rebuild/reset paths is verified in-engine, not by xUnit.
