---
title: 'Editor tool UX consolidation (14.6): single-active-dock arbitration + unified, collision-free hotkey map'
type: 'feature'
created: '2026-07-15'
status: 'in-review'
baseline_revision: 'c87cf35018d463e44119ea131250878c8d410e3f'
review_loop_iteration: 2
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** The World-Editor tools accreted across Epics 3–6 (Terrain, Region, Pathability, Camera, Water, entity-placement palette, Unit/Building/Ability/Trigger/Persistence card panels) each self-anchor and self-toggle with no coordination: the five right-side ("CenterRight") panels claim the identical screen rect and render **overlapped**, any number of tools can be "active" at once, editor hotkeys **collide** (G quad-claims, V/N/K clash tool-toggle vs panel-open, R is shadowed dead), Esc-cancel is fragmented across four handlers so a mid-drag Esc never cancels the tool, and the only hotkey surface is a hardcoded one-line hint strip that omits most keys. Filed from Alec's live-use Epic-6 retro (A3-E6): "so many editors it's hard to keep track of the hotkeys… overlap of water editor, region editor and so on."

**Approach:** Introduce two pure-C# cores — a `ToolDockArbiter` that enforces **at most one active right-dock surface at a time** (activating any tool/panel deactivates the currently-active one), and an `EditorHotkeys` canonical binding registry that is the **single source of truth** for every edit-mode key, fails closed on a duplicate, and drives a new in-app `HotkeyOverlay` reference surface. Route every tool toggle, right-dock panel `Toggle`, and Esc-cancel through these cores; resolve the six documented collisions in the registry; keep the already-correct shared `EditorHistory`/Ctrl+Z-Y contract intact.

## Boundaries & Constraints

**Always:**
- Presentation-layer only. No `src/Core` sim types, no `Fixed`/`ScenarioData`/`SimChecksum` touch, no golden re-baseline. New code lives in `src/CreationSuite/` and `src/Core/Bootstrap/Phases/` (presentation composition), plus xUnit tests.
- `ToolDockArbiter` and `EditorHotkeys` are **pure C#** (no `using Godot;`), so both are Tier-1 unit-testable (the `EditorHistory` precedent).
- The arbiter's invariant is absolute: after any `Activate(id)`, exactly the requested surface is active and every other registered surface is deactivated; a second surface can never be active concurrently.
- `EditorHotkeys` is the only place an edit-mode key is assigned; a duplicate `(Key, Ctrl, Mode)` throws at construction (fail-closed) and is caught by a guard test.
- The shared `EditorHistory` / Ctrl+Z / Ctrl+Y routing (`EntityPlacer.History`, `EntityPlacer._Input`) keeps working uniformly under every active tool — do not regress it, and no tool may swallow Ctrl+Z/Y.
- The in-app hotkey reference surface is **generated from** `EditorHotkeys`, so it can never drift from the audited bindings.

**Block If:**
- Delivering a collision-free map turns out to require repurposing a key that already carries a **sim/gameplay** (non-editor) binding whose reassignment would change in-match play — HALT (out of this story's presentation scope).

**Never:**
- Do not add a Godot `InputMap`/`[input]` actions section — the editor is raw-keycode by established convention; the registry stays a C# data table.
- Do not reparent all panels into one literal container node or rebuild the panels themselves — mutual-exclusion visibility in the shared right-edge region satisfies "two docks never overlap." Out of scope: redesigning any individual tool's internals, touching Play-mode/command HUD, or the dedicated-server path.
- Do not change gameplay/selection-command semantics beyond **mode-scoping** the selection-command keys (S/H/Q/P/F/T-use-item) to non-Edit mode.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Activate tool while another active | Terrain active; user activates Region | Region becomes the sole active surface; Terrain (panel + brush) deactivates; only Region's dock visible | No error |
| Toggle active surface off | Region active; user presses Region toggle again | Region deactivates; no surface active; right dock empty | No error |
| Open right-dock panel over active tool | Water tool active; user opens Unit Card | Water deactivates; Unit Card is sole visible right-dock surface (no overlap) | No error |
| Duplicate binding registered | Two entries share `(Key.G, Ctrl:false, Edit)` | `EditorHotkeys` ctor throws `InvalidOperationException` naming the clashing action ids | Fail-closed; guard test RED |
| Esc during in-progress tool op | Region drag armed | Active tool's `CancelInProgress()` runs and consumes Esc; no fall-through to deselect/Settings | Handled=true stops chain |
| Esc with no active tool op | No tool active, unit selected | Falls through existing chain (deselect → Settings) unchanged | No error |
| Ctrl+Z/Y under any active tool | Terrain active, prior mixed ops | Shared `EditorHistory` undo/redo runs regardless of active tool | No error |
| Open hotkey overlay | User presses overlay key (F1) in Edit | Overlay lists every registered binding, grouped, matching the registry exactly | No error |

</intent-contract>

## Code Map

- `godot/src/CreationSuite/ToolDockArbiter.cs` -- **NEW** pure-C# single-active registry (id + activate/deactivate delegates + optional cancel delegate).
- `godot/src/CreationSuite/EditorHotkeys.cs` -- **NEW** pure-C# canonical binding table (`EditorBinding{ ActionId, Key, Ctrl, Mode, Label }`), lookup/enumerate, fail-closed dup detection.
- `godot/src/CreationSuite/HotkeyOverlay.cs` -- **NEW** Godot `Control` rendering `EditorHotkeys` as the in-app reference surface, toggled by its registry key.
- `godot/src/Core/Bootstrap/Phases/HotkeyOverlayPhase.cs` -- **NEW** `ISetupPhase` building the overlay; registered in `MainScene` phase list.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- add `ToolDockArbiter Arbiter` + `EditorHotkeys Hotkeys` shared handles.
- `godot/src/Core/MainScene.cs` -- construct arbiter + hotkeys; migrate `_UnhandledInput` panel-open keys to registry lookups; route the 5 right-dock panel toggles through the arbiter; route Esc to `Arbiter.CancelActive()` first; regenerate `ControlsLabel` edit-string from the registry.
- `godot/src/CreationSuite/TerrainBrush.cs`, `RegionTool.cs`, `PathabilityTool.cs`, `CameraTool.cs`, `WaterTool.cs` -- register with arbiter; toggle activation goes through arbiter (mutual exclusion); read toggle key from registry; add `CancelInProgress()` Esc hook; param-canvas visibility follows arbiter active state.
- `godot/src/CreationSuite/UnitCardPanel.cs`, `BuildingCardPanel.cs`, `AbilityEditorPanel.cs`, `TriggerEditorPanel.cs`, `PersistenceManifestPanel.cs`, **`ItemCardPanel.cs`, `TechTreePanel.cs`** -- route `Toggle`/show through the arbiter so opening one closes the others.
- `godot/src/UI/ContentBrowserPanel.cs` -- **(rev 1)** fullscreen editor; register with the arbiter so it can't render over a right-dock panel (or document exclusion accurately). **(rev 2)** register it with the arbiter **at phase init** and route *every* show/hide through the arbiter (see the two non-hotkey entry points below), not lazily on the hotkey path only.
- `godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs`, `godot/src/Core/Bootstrap/Phases/MainMenuPhase.cs` -- **(rev 2)** both call `ContentBrowser.ToggleVisible()` directly (failed-load reopen; MainMenu Browse button). Route through the arbiter so ContentBrowser can never render over an active right-dock panel.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` (`SelectAndShow`), `godot/src/CreationSuite/ResearchCardPanel.cs` (`SelectAndShow`) -- **(rev 2)** inspector show paths that set `_panel.Visible = true` directly, invoked from `TechTreePanel.OnNodeSelected`. Route through `Arbiter.Activate(id)`; **register `ResearchCardPanel` with the arbiter** (currently unregistered).
- `godot/src/CreationSuite/UnitCardPanel.cs`, `BuildingCardPanel.cs`, `AbilityEditorPanel.cs`, `TriggerEditorPanel.cs`, `PersistenceManifestPanel.cs`, `ItemCardPanel.cs`, `TechTreePanel.cs` -- **(rev 2)** each panel's `Close()` / close-button must call `Arbiter.Deactivate(id)` (the reverse edge of `onDeactivate: Close`) so a user close never strands `ActiveId`. `TechTreePanel`/`PersistenceManifestPanel` close-button **captions** ("Close [R]"/"Close [V]") sourced from `EditorHotkeys.DisplayKey` (stale-literal fix).
- `godot/src/UI/RtsCameraController.cs` -- **(rev 1)** reference only: the polled `Key.W/A/S/D`/arrow camera pan is the reserved-key constraint's source; keep `E`=edge-scroll as the sole camera event binding. No behavior change expected unless the reserved-key rule is best served by reading `E` from the registry.
- `godot/src/UI/EntityPlacer.cs` -- read keys from registry; **consume** G (grid-snap) so it no longer double-fires, **gated `if (!editMode) break;`** so G is inert in Play; keep Ctrl+Z/Y + Esc-armed-cancel.
- `godot/src/UI/SelectionSystem.cs` -- mode-scope the selection-command keys (S/H/Q/P/F, T use-item) to non-Edit so they never collide with Edit tools.
- `godot/ProjectChimera.Sim.Tests/CreationSuite/ToolDockArbiterTests.cs` -- **NEW** single-active + cancel-routing invariants.
- `godot/ProjectChimera.Sim.Tests/CreationSuite/EditorHotkeysTests.cs` -- **NEW** collision-free + label-completeness + registry-drives-overlay invariants.
- `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` -- add `HotkeyOverlayPhase` to the pinned canonical order.

## Tasks & Acceptance

**Execution:**
- `godot/src/CreationSuite/ToolDockArbiter.cs` -- Implement `Register(string id, Action onActivate, Action onDeactivate, Func<bool> cancelInProgress = null)`, `Activate(id)` (deactivates the current active first, then activates), `Deactivate(id)`, `Toggle(id)`, `string ActiveId`, `bool CancelActive()` (invokes active surface's cancel delegate, returns whether it handled). **(rev 1)** run callbacks then commit `ActiveId` (or try/finally) so a throwing callback can't strand `ActiveId` on a never-shown surface; drop dead defensive code (`?? (() => {})` on non-null `Action` params) — keep `IsRegistered` only if a caller uses it.
- `godot/src/CreationSuite/EditorHotkeys.cs` -- Define `EditorMode{Edit,Play,Any}` + `EditorBinding` record + the full binding table; ctor throws `InvalidOperationException` on any duplicate `(Key,Ctrl,Mode)` (treating `Any` as colliding with both); `Get(actionId)`, `IReadOnlyList<EditorBinding> All`, `Matches(InputEventKey, mode)` helper. **(rev 1)** declare `static readonly ReservedPolledKeys = {W,A,S,D,Up,Down,Left,Right}`; ctor also throws if any binding's key is reserved (name the action) — closing the polled-camera-pan collision class the discrete audit is blind to. Reassign per the rev-1 table (Item=F2, Ability=F3, Persistence=F4, Lobby=H, TechTree=Y). -- single source of truth + fail-closed audit.
- `godot/src/CreationSuite/HotkeyOverlay.cs` -- `Control` that builds its rows from `EditorHotkeys.All` grouped by owner/mode; toggled visible by the overlay binding; label-mandate compliant. -- the audited reference surface.
- `godot/src/Core/Bootstrap/Phases/HotkeyOverlayPhase.cs` + `MainScene.cs` phase-list edit -- build + wire the overlay; construct `Arbiter`/`Hotkeys` and place on `SceneContext`.
- `SceneContext.cs` -- add the two shared handles.
- `TerrainBrush/RegionTool/PathabilityTool/CameraTool/WaterTool` -- in each phase `Initialize`, `Arbiter.Register(...)`; replace the private self-toggle bool flip with `Arbiter.Toggle(id)`; on activate show param-canvas, on deactivate hide it and end any in-progress op; read the toggle key via `_ctx.Hotkeys`; implement `CancelInProgress()`. **(rev 1)** on mode→Play, `Arbiter.Deactivate(id)` (symmetry with panels) so `ActiveId` never dangles on a hidden tool; keep the `P` pathability-overlay toggle reachable even if only the tool-toggle is arbiter-gated (don't dead-gate the overlay behind `_arbiter != null`).
- `UnitCardPanel/BuildingCardPanel/AbilityEditorPanel/TriggerEditorPanel/PersistenceManifestPanel/ItemCardPanel/TechTreePanel` (+ `ContentBrowserPanel`) -- register with arbiter (via `AttachArbiter` in their phase) and route `Toggle()`/visible through `Arbiter.Activate/Deactivate`. **(rev 1)** every panel whose in-code comment says it routes through the arbiter MUST actually do so — no false contract comments.
- `MainScene.cs` / `SceneContext.cs` -- panel-open `_UnhandledInput` keys read from `Hotkeys`; Esc handler calls `Arbiter.CancelActive()` first and returns if handled; `ControlsLabel` edit-mode text generated from `Hotkeys`. **(rev 1)** the comment listing which panels register with the arbiter MUST match the actual registered set.
- `EntityPlacer.cs` -- read keys from registry; call `SetInputAsHandled()` after G grid-snap **and gate the G case `if (!editMode) break;`** (inert + not swallowed in Play); leave Ctrl+Z/Y + armed-Esc intact.
- `SelectionSystem.cs` -- gate the command keys to `Mode != Edit`.
- **Stale key-hint sweep (rev 1)** `PersistenceManifestPanel.cs` (drop "toggled with V"), `TechTreePanel.cs` (AttachTip "(R)" → "(Y)"), `EditorHotkeys.cs`/`EditorHotkeysTests.cs` ("Lobby N→E" comment → "N→H"), and any MainScene/phase console hints -- fix every stale key string the remap invalidated (single-source-of-truth intent requires accurate hints).
- `ToolDockArbiterTests.cs` -- assert: activating each of N registered surfaces leaves exactly one active and all others deactivated; toggling the active one clears it; `CancelActive()` returns the active surface's cancel result and is a no-op with none active. **RED-teeth:** a test that a naive "activate without deactivating current" leaves two active must be impossible through the public API.
- `EditorHotkeysTests.cs` -- assert: no two bindings share `(Key,Ctrl,Mode)` (Any collides with Edit+Play); every binding has a non-empty `Label`; the set the overlay would render equals `All`; **(rev 1)** `NoBinding_UsesReservedPolledCameraKey` — no binding uses W/A/S/D/arrows, and injecting one (e.g. Item→W) makes the ctor throw. **RED-teeth:** adding a duplicate binding, or a reserved-key binding, throws at ctor and turns the guard RED.
- `PhaseOrderTest.cs` -- extend the canonical order with `HotkeyOverlayPhase`.

**Revision 2 additions (route every visibility site through the arbiter; close verification + drift gaps):**
- **Close-button routing** — every arbiter panel's `Close()` (driven by its close-button) calls `Arbiter.Deactivate(id)` so a user-initiated close clears `ActiveId`; the panel reopens on the FIRST subsequent hotkey press. Keep `onDeactivate: Close` (arbiter→panel edge); add the panel→arbiter edge.
- **Inspector `SelectAndShow` routing** — `BuildingCardPanel.SelectAndShow` and `ResearchCardPanel.SelectAndShow` route through `Arbiter.Activate(id)` before showing; **register `ResearchCardPanel`** with the arbiter. Opening an inspector from `TechTreePanel.OnNodeSelected` deactivates the Tech Tree so no two dock surfaces coexist.
- **`ContentBrowser` routing** — register `ContentBrowser` (and the lobby) with the arbiter at phase init; route `MainMenuPhase` Browse and `ContentBrowserPhase` failed-load reopen (and any other `ToggleVisible`) through it; remove the one-directional `ToggleKeyedSurface` resync in favor of first-class registration.
- **Arbiter robustness** — `Activate` fail-safe on a throwing `OnDeactivate` (try/finally) AND cannot leave a surface visible while `ActiveId` is null on a throwing `OnActivate` (no orphan-visible panel). Extend `ToolDockArbiterTests` with both cases.
- **Close-button captions from registry** — `TechTreePanel` "Close [R]" and `PersistenceManifestPanel` "Close [V]" captions generated from `EditorHotkeys.DisplayKey(Hotkeys.Get(id))` (no stale key literals).
- **Display/behavior single source** — resolve `EntityPlacer`'s registry-displayed keys (`grid_snap`, `placement_*`) from the registry (preferred) or stop sourcing their display from the registry; pick one so HUD text and actual key handling share a source. Register those keys so the collision guard sees them.
- **`ReservedPolledKeys` coupled to the camera** — derive from or assert-equal to `RtsCameraController`'s polled-key set (shared constant or a test that reflects both), so a future camera-polled key is auto-protected.
- **Overlay hygiene** — dismiss the overlay on mode change; harden the group-grid null-`Group` path (`grid ??= …`); wrap rows in a `ScrollContainer`.
- **`EditorKeyMap` coverage** — give the Godot↔`EditorKey` map automated coverage (extract a Godot-free inner lookup for Tier-1 round-trip/symmetry tests, or an equivalent live-path check of each remapped/tool key), since a single wrong/asymmetric entry silently misroutes.
- **`DisplayKey` exact-value test** — a Tier-1 `[Theory]` asserting exact strings for the special cases (`undo`→"Ctrl+Z", `placement_delete`→"Del", `settings`→"Esc", `mode_toggle`→"F5", a function key, a bare letter), so a mutant dropping "Ctrl+"/collapsing "Del" turns a test RED instead of shipping a misleading overlay.
- **`ControlsLabel` id-resolution guard** — either a Tier-1 test that every action-id the `ControlsLabel` looks up exists in the registry, or a non-throwing lookup, so a renamed `DefaultBindings` id can't NPE the HUD on the first Edit frame.

**Acceptance Criteria:**
- Given any editor tool is active, when the creator activates a different tool or opens a right-dock panel, then only the newly-activated surface is visible in the shared right-dock region and the previously-active one is hidden — two docks are never rendered overlapping (verified live via godot-mcp: activate each in turn, observe a single visible right-dock surface).
- **(rev 2)** Given the Tech Tree editor is open, when the creator clicks a building/research node (opening the Building/Research inspector) and then opens any other right-dock panel, then at no point are two dock surfaces visible at once — the inspector show routes through the arbiter and deactivates the Tech Tree (verified live via godot-mcp: TechTree → click node → open Unit Card, observe a single visible surface at each step).
- **(rev 2)** Given any arbiter panel is open, when the creator closes it via its in-panel Close/X button and then presses that panel's hotkey once, then the panel reopens on that single press (no stranded `ActiveId`; verified live).
- Given the full edit-mode key set, when the `EditorHotkeys` collision guard runs, then no two bindings share the same `(Key, Ctrl, Mode)`, and introducing a duplicate makes construction throw and the guard test RED.
- Given the camera pans on polled W/A/S/D/arrow keys in every mode, when the `EditorHotkeys` ctor is constructed, then no editor binding uses any reserved polled key, and binding a panel to W/A/S/D (as a prior revision did for Item/Ability/Persistence) makes construction throw and the guard test RED — so panning the camera in Edit never toggles a panel.
- Given the creator opens the in-app hotkey overlay in Edit mode, when it renders, then it lists every binding present in `EditorHotkeys` (reference surface reflects the audited truth, no key omitted).
- Given any tool is active, when the creator presses Ctrl+Z/Ctrl+Y, then the shared `EditorHistory` undo/redo runs regardless of which tool owns the dock (no tool swallows them).
- Given a tool has an in-progress operation, when the creator presses Esc, then the active tool cancels via the shared cancel contract and Esc does not fall through to deselect/Settings; with no in-progress tool op, Esc behaves exactly as before.
- Given the previously-colliding keys (G, V, N, K, T, R), when each is pressed in Edit mode, then it triggers exactly one action (e.g. G snaps the grid without also opening the Item Card; R rotates placement without the TechTree binding being shadowed dead).

## Spec Change Log

### 2026-07-15 — Review pass 2 (bad_spec loopback)

- **Triggering findings:**
  1. `[high]` The single-active invariant was enforced only at the `Toggle()` surface, not at the rendered-visibility surface. `BuildingCardPanel.SelectAndShow` (`BuildingCardPanel.Edit.cs:759`) and `ResearchCardPanel.SelectAndShow` (`ResearchCardPanel.cs:126`) set `_panel.Visible = true` directly and are called from `TechTreePanel.OnNodeSelected` (449/451) while the arbiter still believes `panel_tech_tree` is active; `ResearchCardPanel` is not even registered. Reachable flow: open Tech Tree → click a node (CenterRight inspector shows, arbiter unaware) → press J → `Activate(panel_unit_card)` deactivates only the tracked Tech Tree, leaving **UnitCard + BuildingCard both visible** — the exact "two docks never overlap" AC violated. `UnitCardPanel.EnsureVisible`/`StartFromTemplate` already route through `Activate`; the inspector paths did not (routing retrofit incomplete).
  2. `[high]` Every `AttachArbiter` panel's `Close()`/close-button sets `_panel.Visible = false` without calling `Arbiter.Deactivate`, stranding `ActiveId` on a hidden surface → the panel needs **two** hotkey presses to reopen (the first `Toggle` just clears the stale id). Systemic across all 7 panels; the rev-1 lobby/content-browser resync solved exactly this but was never extended to the registered panels.
  3. `[medium]` `ContentBrowser` non-hotkey entry points — `MainMenuPhase.cs:45` (Browse) and `ContentBrowserPhase.cs:88` (failed-load reopen) — call `ToggleVisible()` directly, bypassing the arbiter (which only registers ContentBrowser lazily on its hotkey path); ContentBrowser can render over an active right-dock panel. The rev-1 `ToggleKeyedSurface` resync is one-directional (`!isVisible && ActiveId==id` only).
  - Structural: the bookkeeping-only Tier-1 tests **passed while the core AC was violated** — they assert the arbiter's `ActiveId`/delegate state machine against a fake `Surface`, never real `_panel.Visible` mutual-exclusion, so no test could catch findings 1–3. That verification-surface gap is why this is bad_spec, not patch.
- **Amended (all outside `<intent-contract>`):** Design Notes gained the **visibility-surface routing** principle (every show/hide site — Close buttons, inspector `SelectAndShow`, `ContentBrowser` phase entry points, onboarding — routes through the arbiter), **arbiter both-callbacks-fail-safe** robustness, **overlay hygiene** (mode-change dismiss, null-Group grid harden, scroll), **display/behavior single-source** (close-button captions + EntityPlacer keys from the registry), **`ReservedPolledKeys` coupled to the camera**, and **arbiter scope docstring accuracy** (right-dock-only, MapGen/FactionDefiner intentionally excluded). Code Map added the inspector/ContentBrowser/Close sites; Tasks & Acceptance added the rev-2 routing tasks + two new ACs (inspector-no-overlap, close-then-reopen-on-first-press); Verification added the live inspector/close/content-browser/overlay checks and Tier-1 additions (arbiter throw cases, `DisplayKey` exact-value, `EditorKeyMap` round-trip, reserved-key/camera coupling).
- **Known-bad state avoided:** shipping a "single-active" editor whose green unit tests hide a live two-panel overlap reachable in one click (Tech Tree inspector), and panels that silently need a double key-press to reopen after a Close; plus stale close-button captions naming the wrong post-remap tool and an overlay that lingers over the Play HUD.
- **KEEP (must survive re-derivation):**
  - Both pure cores exactly as built (extend, don't rewrite): `ToolDockArbiter` (Register/Activate/Deactivate/Toggle/ActiveId/CancelActive, deactivate-before-activate, callbacks-then-commit) — ADD the both-callbacks try/finally + no-orphan-visible guard; `EditorHotkeys` (Godot-free `EditorKey`/`EditorMode`/`EditorBinding`/`OverlayRow`, fail-closed dup + reserved-key guard, `Get`/`All`/`Match`/`OverlayRows`/`DisplayKey`, rev-1 remaps Item=F2/Ability=F3/Persistence=F4/Lobby=H/TechTree=Y) — ADD the camera-coupled reserved set + `DisplayKey` exact test.
  - `EditorKeyMap` as the single Godot edge; the two cores in `SimSources.props`; `HotkeyOverlay` generated-from-registry; `HotkeyOverlayPhase` before `Onboarding` in `ScenePhaseOrder`/`PhaseOrderTest`; `ControlsLabel` regenerated from the registry.
  - All AttachArbiter wiring already correct on the 5 tools + 7 panels (Toggle routing, mode→Play `Deactivate`, `CancelInProgress` hooks) and the `UnitCardPanel.EnsureVisible`/`StartFromTemplate` arbiter routing — keep; ADD the MISSING routing sites (Close, inspector `SelectAndShow`, ContentBrowser).
  - The existing 25 Tier-1 tests (single-active, toggle, idempotency, cancel-fallthrough, dup-registration, throwing-activate; collision-free, label, overlay-parity, six-remaps, Ctrl-discrimination, reserved-key) — keep and extend.
  - The `EntityPlacer` `G`-consume + `!editMode` gate; the rev-1 reserved-polled-key guard; Lobby→H and E-stays-edge-scroll; Ctrl-discrimination of Y/Ctrl+Y.

### 2026-07-15 — Review pass 1 (bad_spec loopback)

- **Triggering findings:**
  1. `[high]` The rev-0 collision table assigned Item Card→**W**, Ability→**A**, Persistence→**D**. `RtsCameraController.HandlePan` **polls** `Key.W/A/S/D`/arrows every frame in all modes to pan the camera; polled input can't be de-conflicted by event-consumption, and the registry's discrete `(Key,Ctrl,Mode)` guard is structurally blind to it. Result: panning the camera in Edit also toggles those three panels — the exact collision class the story exists to remove.
  2. `[medium]` Rev-0 scoped the single-active group to only the 5 CenterRight panels + 5 tools, while a code comment claimed Item Card routed through the arbiter (it did not), and fullscreen editors (Tech Tree, Content Browser) could render over a right-dock panel — so "two docks never overlap" / "every other panel hidden" was not actually enforced and a comment lied.
- **Amended (all outside `<intent-contract>`):** Design Notes gained a **Reserved polled-input keys** rule (`{W,A,S,D,arrows}` may never be a discrete editor binding; ctor fails closed on violation) and a **single-active-over-every-keyed-surface** rule (register Item/TechTree/ContentBrowser too; no false arbiter comments); the collision table was revised (Item→F2, Ability→F3, Persistence→F4 — function-key namespace that can't hit WASD; Lobby→H, TechTree→Y kept). Added tasks: reserved-key guard test, register the extra panels, EntityPlacer G Play-mode gate, arbiter callbacks-then-commit robustness, tool arbiter-Deactivate on mode→Play, and a stale-key-hint sweep (Persistence "V", TechTree "R", Lobby "N→E").
- **Known-bad state avoided:** shipping an editor where normal WASD camera navigation spuriously opens/closes panels, and a "collision-free" claim that holds only for discrete keys while the real polled collision ships unseen; plus false in-code contract comments and fullscreen-over-panel overlap.
- **KEEP (must survive re-derivation):**
  - Both pure-C# cores exactly as built: `ToolDockArbiter` (Register/Activate/Deactivate/Toggle/ActiveId/CancelActive, deactivate-before-activate) and `EditorHotkeys` (keys stored as `int`/`EditorKey` to stay Godot-free/Tier-1; fail-closed dup guard with `Any` colliding both modes; `Get`/`All`/`Matches`/`OverlayRows`/`DisplayKey`). Add the reserved-key check to the *existing* ctor; do not rewrite the core.
  - The existing test suites (single-active, toggle, idempotency, cancel-fallthrough, dup-registration; collision-free, label, overlay-parity, six-remaps, ctrl/mode discrimination) — **keep and extend**, don't replace.
  - Adding the two pure files to `SimSources.props` for Tier-1 compilation; the `HotkeyOverlay` generated-from-registry design; `HudPhase`/`ControlsLabel` regenerated from the registry; the `HotkeyOverlayPhase` inserted before Onboarding in `ScenePhaseOrder`/`PhaseOrderTest`.
  - The `G`-consume fix in `EntityPlacer` (kills the double-fire) — keep it, only add the `!editMode` gate.
  - Lobby→**H** and the `E`-stays-edge-scroll decision (rev-0's own correct deviation) and Ctrl-discrimination of Y/Ctrl+Y.

## Review Triage Log

### 2026-07-15 — Review pass 2
- intent_gap: 0
- bad_spec: 3: (high 2, medium 1)
- patch: 11: (low 11)
- defer: 1: (low 1)
- reject: 2: (low 2)
- addressed_findings:
  - `[high]` `[bad_spec]` Inspector `SelectAndShow` (BuildingCard + unregistered ResearchCard) bypasses the arbiter → two overlapping CenterRight panels reachable via Tech Tree node-click → open card (core AC violated) — spec now mandates routing every visibility site through the arbiter + registering ResearchCardPanel; code reverted for re-derivation.
  - `[high]` `[bad_spec]` Every panel Close/close-button strands `ActiveId` (needs two hotkey presses to reopen) — spec now requires the panel→arbiter close edge (`Deactivate`) on every panel; code reverted for re-derivation.
  - `[medium]` `[bad_spec]` ContentBrowser non-hotkey entry points (MainMenu Browse, failed-load reopen) bypass the arbiter and can overlap a right-dock panel; rev-1 resync is one-directional — spec now registers ContentBrowser at phase init and routes all its show/hide through the arbiter; code reverted for re-derivation.
  - _Folded into the re-derivation via amended tasks (would otherwise be patch): stale close-button captions "[R]"/"[V]" → sourced from `DisplayKey`; arbiter both-callbacks fail-safe (deactivate-throws + partial-activate-throws) + tests; overlay hygiene (mode-change dismiss, null-Group grid harden, ScrollContainer); EntityPlacer key display/behavior single-source; `ReservedPolledKeys` coupled to the camera; `DisplayKey` exact-value test; `EditorKeyMap` round-trip coverage; `ControlsLabel` id-resolution guard; in-tool `GD.Print` key strings; onboarding routing verification._
  - _Deferred (pre-existing, moot this pass under the bad_spec cascade; re-evaluate next pass): card panels keep their own local edit-undo histories that shadow the shared `EditorHistory` when focused._
  - _Rejected as noise: MapGenerator/FactionDefiner "overlap" (modal wizards, not right-dock surfaces — arbiter-excluded by the intent's own right-dock scoping; only the docstring-accuracy note was folded); F1-overlay mouse-swallow in Play (acceptable modal behavior)._

### 2026-07-15 — Review pass
- intent_gap: 0
- bad_spec: 2: (high 1, medium 1)
- patch: 6: (low 6)
- defer: 1: (low 1)
- reject: 4: (low 4)
- addressed_findings:
  - `[high]` `[bad_spec]` Panel remap keys W/A/D collide with polled WASD camera pan (audit blind to polled input) — spec revised to function-key panel bindings (F2/F3/F4) + a fail-closed `ReservedPolledKeys` guard; code reverted for re-derivation.
  - `[medium]` `[bad_spec]` Single-active group incomplete + false "Item Card routes through arbiter" comment; fullscreen editors can overlap — spec now registers Item/TechTree/ContentBrowser and forbids false arbiter comments; code reverted for re-derivation.
  - _Folded into the re-derivation via amended tasks (would otherwise be patch): stale key-hints (Persistence "V", TechTree "R", Lobby "N→E"); EntityPlacer G Play-mode gate; arbiter callbacks-then-commit; tool arbiter-Deactivate on mode→Play; PathabilityTool P-overlay not dead-gated behind the arbiter._
  - _Deferred (pre-existing, moot this pass under the bad_spec cascade; re-evaluate next pass): card panels keep their own local edit-undo histories that shadow the shared `EditorHistory` when focused._
  - _Rejected as noise: "audit blind to polled inputs" as a separate item (same root as the high finding, now guarded); `toggle_edge_scroll` E being registry-decorative; use-item-T mode-scope fragility; dead `IsRegistered`/null-coalesce (folded as a cleanup note)._

## Design Notes

**Reserved polled-input keys (the audit's blind spot — revision 1).** `RtsCameraController.HandlePan` **polls** `Input.IsKeyPressed(Key.W/A/S/D)` and the arrow keys every `_Process` frame, in **all modes** (no `GameMode` gate), to pan the camera. Polled input is not an `InputEvent`, so `SetInputAsHandled()` cannot de-conflict it and the registry's discrete `(Key,Ctrl,Mode)` collision guard **cannot see it**. Therefore: **no discrete editor binding (tool or panel) may bind `W`, `A`, `S`, `D`, or any arrow key.** `E` (edge-scroll toggle) is the camera's own event-driven binding and is allowed *only* for `toggle_edge_scroll`. `EditorHotkeys` MUST declare a `ReservedPolledKeys` set `{W,A,S,D,Up,Down,Left,Right}` and its ctor MUST fail closed if any binding uses one — this promotes the polled-collision class into the same fail-closed audit as discrete collisions.

**Shared right-dock = single-active over every keyed editor surface.** The overlap the story kills is "more than one editor surface visible at once." Enforce **at most one active** across **every panel/tool that has an editor hotkey and can occupy a dock region**: the 5 tools (Terrain/Region/Pathability/Camera/Water) + every keyed panel — Unit, Building, Ability, Trigger, Persistence, **Item Card, Tech Tree, Content Browser** (the fullscreen editors can render over a CenterRight panel, so they must join the group). Tool param-canvases join too. `MapGenerator` and `FactionDefiner` are guided/modal dialogs: register them too so activation is uniform, OR, if deliberately excluded, their exclusion MUST be stated accurately in-code. **No comment may claim a panel routes through the arbiter unless it actually calls `AttachArbiter` and routes `Toggle()` through it** (revision 1: the prior code comment falsely listed Item Card).

**Single-active is enforced at the VISIBILITY surface, not just the `Toggle()` surface (revision 2 — root-cause fix).** The intent's binding expectation is observable ("two docks are never rendered overlapping on screen"). Routing only the hotkey `Toggle()` through the arbiter is insufficient: **every** code path that shows or hides a registered surface must go through `Arbiter.Activate`/`Deactivate`, or `ActiveId` desyncs from real `_panel.Visible` state and two surfaces overlap. The rev-1 code enforced single-active at the toggle surface only, leaving these bypass sites (all confirmed reachable):
- **Panel Close / close-button.** Every panel's `Close()` (and its `closeBtn.Pressed += Close` / `+= () => _panel.Visible = false`) hides the panel WITHOUT telling the arbiter, stranding `ActiveId` on a hidden surface → the panel then needs **two** hotkey presses to reopen. Rule: a user-initiated close MUST call `Arbiter.Deactivate(id)` (route the close-button through the arbiter). The `onDeactivate: Close` registration stays — the arbiter drives `Close` on deactivate; the fix is the *reverse* edge (close → arbiter).
- **Inspector `SelectAndShow`.** `BuildingCardPanel.SelectAndShow` and `ResearchCardPanel.SelectAndShow` set `_panel.Visible = true` directly and are invoked from `TechTreePanel.OnNodeSelected` while the arbiter believes `panel_tech_tree` is active → a CenterRight card becomes visible unknown to the arbiter; a subsequent `Activate` deactivates only the tracked surface, leaving **two CenterRight panels overlapping** (the exact AC violation). Rule: these MUST route through `Arbiter.Activate(id)` (mirroring `UnitCardPanel.EnsureVisible`/`StartFromTemplate`, which already do). `ResearchCardPanel` must additionally be **registered** with the arbiter (it is TechTree's own inspector, currently unregistered).
- **`ContentBrowser` non-hotkey entry points.** `MainMenuPhase` (Browse button) and `ContentBrowserPhase` (failed-load reopen) call `ContentBrowser.ToggleVisible()` directly. The rev-1 ad-hoc registration only fires on the `content_browser` hotkey path, so these bypass it. Rule: register `ContentBrowser` (and the lobby) with the arbiter **once at phase init** (not lazily on first hotkey), and route every show/hide — including MainMenu Browse and failed-load reopen — through it. This also removes the one-directional resync bug (rev-1's `ToggleKeyedSurface` only reconciles `!isVisible && ActiveId==id`, never `isVisible && ActiveId!=id`).
- **Onboarding.** Onboarding-driven show paths must route through the arbiter too (UnitCard's already do; verify no onboarding step relies on two panels being open at once — if one does, that step is out of scope and must be documented, not silently broken).

**Arbiter robustness — both callbacks fail-safe (revision 2).** `Activate` must not strand state if EITHER callback throws: wrap the prior surface's `OnDeactivate()` and the new surface's `OnActivate()` so (a) a throwing `OnDeactivate` still proceeds to activate the next surface (or leaves a consistent state), and (b) a throwing `OnActivate` (e.g. `Refresh()` throwing AFTER `_panel.Visible = true`) cannot leave a surface visible while `ActiveId` is null — otherwise the next `Activate` won't deactivate the orphaned-visible panel and two panels overlap. Keep the rev-1 callbacks-then-commit ordering; add try/finally coverage for the deactivate edge and ensure the activate edge cannot orphan a visible panel. Extend `ToolDockArbiterTests` with a deactivate-throws case and a partial-activate-throws (Refresh throws after show) case.

**Overlay hygiene (revision 2).** `HotkeyOverlay` must: dismiss itself on an Edit↔Play mode change (else the Layer-16 dim card persists over the Play HUD until F1); harden the group-grid build against a null `Group` on the first row (`grid ??= …` before `grid!.AddChild`, so a future null-group row can't NRE); and wrap its rows in a `ScrollContainer` so low vertical resolutions don't clip the lower groups. (Esc-closes-overlay and Play-mode mouse-swallow are lower priority; at minimum the mode-change dismiss removes the worst footgun.)

**Single-source-of-truth: close the display/behavior drift (revision 2).** Where a key's *display* is sourced from the registry, its *behavior* must be too — otherwise a future remap changes the HUD/overlay text without changing what the key does (a silent lie the story exists to prevent):
- **Close-button captions** (`TechTreePanel` "Close [R]", `PersistenceManifestPanel` "Close [V]") are hardcoded stale literals naming the WRONG post-remap tool. Source them from `EditorHotkeys.DisplayKey(Hotkeys.Get(id))` so they can never drift (the rev-1 stale-key-hint sweep must extend to these button captions, not just tooltips).
- **`EntityPlacer` placement keys** (`grid_snap`, `placement_unit/building/cycle/delete`, `placement_rotate`): the `ControlsLabel` now sources their display from the registry while `EntityPlacer._Input` still switches on hardcoded `Key.*`. Either resolve those keys in `EntityPlacer` from the registry (preferred — true single source), OR, if `EntityPlacer` keys are deliberately out-of-registry-scope, the `ControlsLabel` must display the same literals `EntityPlacer` actually handles (no registry lookup) — pick one so display and behavior share a source. Registering these keys in the registry also lets the collision guard see them (today an `EntityPlacer` key remap can silently collide with a panel key).

**`ReservedPolledKeys` must be coupled to the camera's actual polled set (revision 2).** The guard hardcodes `{W,A,S,D,arrows}` as a hand-copy of `RtsCameraController.HandlePan`'s polled keys with nothing tying them together; if the camera later polls another key (Q/E rotate, PageUp/Down zoom) the guard silently stops protecting it — reintroducing the very "audit is blind to polled input" class it exists to close. Couple them: derive `ReservedPolledKeys` from (or assert it equal to) the camera's polled-key list via a shared constant or a test that reflects both.

**Arbiter scope docstring accuracy (revision 2).** `MapGenerator` (CenterLeft) and `FactionDefiner` (Center) are modal/guided wizards, NOT right-dock surfaces, and are deliberately arbiter-excluded (stated in-code). The `ToolDockArbiter` docstring must therefore claim single-active only across **registered dock-occupying surfaces**, not an absolute "no two editor surfaces ever overlap" — the accurate scope avoids a false guarantee.

**Collision resolution (documented, collision-free; revision 1).** The load-bearing deliverable is the registry + fail-closed guard; specific keys are the chosen collision-free assignment (any equivalently collision-free remap that respects the reserved-key rule is acceptable). Fixes:
| Key | Was (collision) | Resolution (rev 1) |
|-----|-----------------|------------|
| G | grid-snap + ItemCard + Region-snap + Water-cancel | G = grid-snap only, **consumed**; Water cancel-drag → Esc (shared cancel) |
| V | Camera tool vs Persistence panel | Camera keeps **V**; Persistence panel → **F4** |
| N | Water tool vs Lobby | Water keeps **N**; Lobby → **H** (E is the edge-scroll toggle, not free) |
| K | Pathability tool vs Ability editor | Pathability keeps **K**; Ability editor → **F3** |
| R | EntityPlacer rotate shadows TechTree | Rotate keeps **R**; TechTree → **Y** (bare Y; Ctrl+Y redo is Ctrl-discriminated) |
| Item Card | was G, then W (**W collides with WASD pan**) | Item Card → **F2** |
| T / P / S/H/Q/F | Terrain / Pathability-overlay vs selection commands | Selection-command keys **mode-scoped to non-Edit**; Edit-mode owns the letters for tools |

Panel toggles now avoid WASD entirely: editor panels needing a fresh key land on **function keys** (Item=F2, Ability=F3, Persistence=F4) — an unambiguous namespace that can never collide with camera movement or letter tools. Existing non-WASD panel/menu keys (J/C/L/X/O/M, TechTree=Y, Lobby=H) are kept. Overlay toggle = **F1**; F5 mode-toggle / F9 desc unchanged.

**Undo/redo is already correct — preserve it.** `EntityPlacer.History` is the one shared `EditorHistory`; every tool pushes to it and only `EntityPlacer._Input` routes Ctrl+Z/Y. This story does not restructure that; it only ensures the arbiter/registry changes don't let a tool consume Ctrl+Z/Y, and un-shadows R. (Card panels keep their own local edit-undo histories — pre-existing, out of scope.)

**Play-mode hygiene (revision 1).** `EntityPlacer`'s `G` (grid-snap) case must be gated `if (!editMode) break;` so it neither toggles editor grid-snap nor swallows `G` in Play. On mode→Play, tools must `Deactivate` through the arbiter (symmetry with panels) so `ActiveId` never dangles on a hidden tool across a mode switch.

**Arbiter robustness (revision 1).** `Activate` must run the deactivate/activate callbacks and commit `ActiveId` such that a throwing callback cannot leave `ActiveId` pointing at a never-shown surface (callbacks-then-commit, or try/finally). Drop dead defensive code that contradicts the non-null signatures.

**Testability split.** Arbiter + registry are pure C# (Godot-free) → real Tier-1 RED-teeth. The Node-bound wiring (panels routing through the arbiter, overlay rendering, live key handling, Esc mid-drag) is not Tier-1-instantiable and is verified via the godot-mcp live path (the `EditorHistoryTests` precedent for the split). **Because that split hid the WASD collision, the reserved-polled-key guard is the Tier-1 net that makes the polled class catchable.**

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~ToolDockArbiterTests"` -- expected: single-active + cancel-routing invariants pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~EditorHotkeysTests"` -- expected: collision-free + label + overlay-parity pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~PhaseOrderTest"` -- expected: updated canonical order passes.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green; golden/checksum tests **unchanged** (no sim state touched, no re-baseline).

**RED-teeth proof (do, observe, revert):**
- Add a second `EditorBinding` reusing `(Key.G, Ctrl:false, Edit)` → confirm `EditorHotkeys` ctor throws and `EditorHotkeysTests` collision guard turns RED naming both action ids; revert → GREEN.
- **(rev 1)** Add a binding on a reserved polled key (e.g. `panel_item_card`→`W`) → confirm the ctor throws and `NoBinding_UsesReservedPolledCameraKey` turns RED; revert → GREEN. This is the guard that catches the WASD-vs-camera-pan class the discrete audit was blind to.
- In `ToolDockArbiterTests`, register two surfaces, `Activate("a")` then `Activate("b")`, assert `"a"` received its deactivate callback and `ActiveId == "b"`; a mutant that skips deactivating the prior surface turns the test RED.

**Manual checks (godot-mcp live, Edit mode via F5):**
- Activate Terrain, then Region, then Water, then open Unit Card, then open Tech Tree / Content Browser — observe exactly one editor surface visible at each step (no overlap, including the fullscreen editors over a right panel), via `godot_runtime_state`/screenshot.
- **(rev 1)** Hold/tap W, A, D to pan the camera in Edit — confirm NO panel (Item/Ability/Persistence) toggles; the camera just moves.
- Press F1 — overlay lists every registry binding with the rev-1 keys (Item=F2, Ability=F3, Persistence=F4, Lobby=H, TechTree=Y).
- Start a Region drag, press Esc — drag cancels, selection/Settings untouched.
- Under Terrain then Water active, press Ctrl+Z/Y — undo/redo runs both times.
- Press G in Edit (grid snaps, Item Card does NOT open); press G in Play (no grid-snap toggle, event not swallowed); press R while placing (rotates; TechTree not triggered).
- **(rev 2)** Open Tech Tree (Y) → click a building node (inspector opens) → open Unit Card (J): observe exactly one dock surface visible at each step (the inspector show routed through the arbiter closed the Tech Tree; no two CenterRight panels overlap).
- **(rev 2)** Open each arbiter panel, close it with its in-panel Close/X button, then press its hotkey once: it reopens on the first press (no stranded `ActiveId`).
- **(rev 2)** From the main menu Browse (and after a failed map load), the Content Browser opens without overlapping an already-active right-dock panel (it deactivates it).
- **(rev 2)** Open the F1 overlay, then F5 to Play: the overlay dismisses (no Layer-16 card lingering over the Play HUD).

**Revision 2 Tier-1 additions (must run + pass):**
- `ToolDockArbiterTests`: a deactivate-throws case (prior surface's `OnDeactivate` throws during `Activate`) and a partial-activate-throws case (`OnActivate` throws AFTER showing) — assert no orphan-visible surface and a consistent `ActiveId`; RED under a naive un-guarded `Activate`.
- `EditorHotkeysTests` (or a new `EditorKeyMapTests`): `DisplayKey` exact-value `[Theory]` (Ctrl+Z / Del / Esc / F5 / a bare letter); reserved-polled-key set equals the camera's polled set; `EditorKeyMap` round-trip/symmetry for every bound key (via a Godot-free inner lookup or equivalent).
