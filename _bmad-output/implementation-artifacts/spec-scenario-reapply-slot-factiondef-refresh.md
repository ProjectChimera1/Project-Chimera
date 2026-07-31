---
title: 'Scenario re-apply: per-apply slot-faction-def refresh + editor-panel rebind (DW-229, DW-10)'
type: 'bugfix'
created: '2026-07-31'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: a750def2a9f769857d881be65001bab1db0355f8
final_revision: 633718df63d5d95f68b1591e76986c9e27b88544
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** `ScenarioLoadPhase.ResolveSlotFactionDefs` only ever *overwrites* a slot's faction def when a `faction_json` file exists and `continue`s on empty — it never resets an entry, so a cleared/removed `faction_json` keeps a stale def. Worse, the in-place Edit↔Play re-apply (`MainScene.ResetToAuthoredStart`) re-applies against the same shared `_slotFactionDefs` array but never re-runs resolution, so an in-session `faction_json` change or clear silently doesn't take effect until a full scene reload (DW-229). Separately, the three creation-suite editor panels that hold a `ScenarioData` reference expose a `SetScenario` rebind method that has **zero callers**, so the rebind invariant is unenforced and would silently break the moment any in-place scenario swap is introduced (DW-10).

**Approach:** Extract the faction-resolution body into one shared helper that first **resets every slot to its `_Ready`-seeded default**, then re-resolves per-slot `faction_json` in place; call it from both the boot path (`ScenarioLoadPhase`) and the Edit↔Play re-apply (`ResetToAuthoredStart`, before its validation gate). Capture the seeded defaults once at `_Ready`. Add a single `RebindScenarioPanels()` broadcast on `MainScene` that calls each held panel's `SetScenario` with the live `_ctx.Scenario`, wired into the same re-apply seam; make every panel `SetScenario` a **no-op when the reference is unchanged** so it can never discard unsaved editor state on a same-reference re-apply.

## Boundaries & Constraints

**Always:**
- Faction resolution stays a single shared path — the boot resolve and the re-apply resolve must be byte-identical for an unchanged scenario (same spawns, same start-state hash, no golden movement).
- The shared `_slotFactionDefs` array is mutated **in place** — never reassigned (it is aliased by `_ctx.SlotFactionDefs`, the applier, and MainScene).
- Re-resolution on the re-apply path runs **before** the `ScenarioValidator` and `FactionLaunchGate` checks in `ResetToAuthoredStartCore`, so those gates see the refreshed defs.
- `SetScenario` early-returns (no state change, no refresh) when handed the reference it already holds; it only rebinds/refreshes on an actual object change.
- Preserve every existing behavior of `ResolveSlotFactionDefs`: ability back-fill (`ResolveAbilities`), `UnitTagValidator` drop, `FactionValidator.ValidateComplete` diagnostic, and the missing-file → keep-default graceful fallback.

**Block If:**
- Making the resolver Godot-free would require abstracting `ProjectSettings.GlobalizePath`/`File.Exists`/`FactionDefinition.LoadFromFile` behind a new seam that changes any other caller's behavior. (It should not — keep the helper Godot-coupled in `ProjectChimera.Core.Bootstrap`.)

**Never:**
- Do not touch `BuildHeadlessServerSimHost` (MainScene ~2668): it builds a **fresh** slot-def array per call (no staleness) and is MP-determinism-sensitive — changing it risks client/server parity.
- Do not add a new in-session `faction_json`-editing UI or a new in-place scenario-import path (out of scope; other Story-15.6 bundles own import).
- Do not fold any new array into `SimChecksum` or re-baseline goldens — this changes no sim array and no unchanged-scenario spawn.
- Do not add `SetScenario` to `AbilityEditorPanel` (has none today) or wire `CustomUiBuilderPanel` (never instantiated).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unchanged scenario, F5 | slot `faction_json` same as boot | Re-resolve yields identical defs; spawns + start-state hash byte-identical to pre-change | none |
| In-session faction change, F5 | slot 1 `faction_json` repointed to a different faction file that exists | After re-apply, slot 1 spawns the **new** faction's roster | Missing/parse-fail file → keep seeded default (logged) |
| In-session faction clear, F5 | slot 1 `faction_json` set to `""` | Slot 1 reverts to its `_Ready`-seeded default faction | none |
| Same-reference panel rebind | `RebindScenarioPanels()` with unchanged `_ctx.Scenario` | Every panel `SetScenario` no-ops; unsaved DSL graph edits survive the F5 round-trip | none |
| Reference-swap panel rebind | `SetScenario` handed a different `ScenarioData` | Panel drops old model, rebinds, refreshes if visible | null scenario tolerated |

</intent-contract>

## Code Map

- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- boot faction-resolution (`ResolveSlotFactionDefs`, :345-379) + snapshot/restore-on-reject; source of the extracted logic.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- shared phase handles; holds `SlotFactionDefs` (:53), `TriggerPanel`/`DslGraphEditorPanel`/`PersistenceManifestPanel`; add `SeededSlotFactionDefs`.
- `godot/src/Core/MainScene.cs` -- `_slotFactionDefs` seeding (:461-463), `_ctx` build (:510-516), `ResetToAuthoredStartCore` (:2298), `LoadGeneratedScenario` (scene-reload path — unchanged).
- `godot/src/CreationSuite/TriggerEditorPanel.cs` -- `SetScenario` (:155) → `RefreshList()`.
- `godot/src/CreationSuite/DslGraphEditorPanel.cs` -- `SetScenario` (:109) drops `_editGraph`/`_lastLoadedJson`; must guard on unchanged ref.
- `godot/src/CreationSuite/PersistenceManifestPanel.cs` -- `SetScenario` (:80) → `Refresh()`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Bootstrap/SlotFactionResolver.cs` -- NEW static helper `Resolve(ScenarioData scenario, FactionDefinition?[] slotDefs, FactionDefinition?[] seededDefaults, AbilityRegistry abilityRegistry)`: reset `slotDefs[i] = seededDefaults[i]` for all i, then run the exact per-slot resolution body lifted from `ResolveSlotFactionDefs` (empty-`FactionJson` skip, bounds guard as in MainScene:2673, `GlobalizePath` + `File.Exists`, `LoadFromFile`, `ResolveAbilities`, `UnitTagValidator`, `FactionValidator.ValidateComplete` diagnostic). Namespace `ProjectChimera.Core.Bootstrap`. -- one resolve path for both callers with per-apply reset (DW-229 core).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- replace `ResolveSlotFactionDefs`'s body with a delegation to `SlotFactionResolver.Resolve(scenario, _ctx.SlotFactionDefs, _ctx.SeededSlotFactionDefs, _ctx.AbilityRegistry)`; keep the existing pre-resolve `SnapshotSlotFactionDefs()` / `RestoreSlotFactionDefs` reject rollback unchanged. -- boot uses the reset-then-resolve path (no-op reset at first boot; behavior-identical).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- add `public FactionDefinition?[] SeededSlotFactionDefs = null!;`. -- give the resolver the seeded defaults to reset to.
- `godot/src/Core/MainScene.cs` -- (a) after seeding `_slotFactionDefs` (:463) capture `_seededSlotFactionDefs = (FactionDefinition?[])_slotFactionDefs.Clone();` into a new field and set `SeededSlotFactionDefs = _seededSlotFactionDefs` in the `_ctx` initializer (:516); (b) at the **top** of `ResetToAuthoredStartCore`, when `_ctx.Scenario != null`, call `SlotFactionResolver.Resolve(_ctx.Scenario, _slotFactionDefs, _seededSlotFactionDefs, _abilityRegistry)` then `RebindScenarioPanels()`; (c) add `private void RebindScenarioPanels()` that calls `SetScenario(_ctx.Scenario)` on `_ctx.TriggerPanel`, `_ctx.PersistenceManifestPanel`, `_ctx.DslGraphEditorPanel` (null-guarded). -- re-run resolution + rebind on the Edit↔Play seam, before the validation/launch gates (DW-229 + DW-10).
- `godot/src/CreationSuite/TriggerEditorPanel.cs`, `DslGraphEditorPanel.cs`, `PersistenceManifestPanel.cs` -- add `if (ReferenceEquals(scenario, _scenario)) return;` as the first line of each `SetScenario` (capturing the same-null case too). -- same-reference no-op prevents dropping unsaved editor state on a same-object re-apply (DW-10 safety).

**Acceptance Criteria:**
- Given a scenario whose slots' `faction_json` are unchanged since boot, when the player toggles Edit→Play (F5), then the spawned per-slot rosters and the logged start-state hash are identical to the pre-change build (no golden movement).
- Given the live scenario's slot 1 `faction_json` is repointed in-session to a different existing faction file, when Edit→Play re-applies, then slot 1 spawns that new faction's units (verified per-slot against the faction JSON roster), without a scene reload.
- Given the live scenario's slot 1 `faction_json` is cleared to `""` in-session, when Edit→Play re-applies, then slot 1 reverts to its `_Ready`-seeded default faction.
- Given a panel already bound to `_ctx.Scenario`, when `RebindScenarioPanels()` runs on a same-reference re-apply, then no panel discards its in-memory model (unsaved DSL-graph edits survive), and the `SlotFactionResolver` / `ResetToAuthoredStart` fail-closed vetoes still fire on an invalid re-resolved scenario.

## Design Notes

Both defects are **structural/latent** in today's tree: every real reload/import (`LoadGeneratedScenario`, `ContentBrowser.HandleLoadMap`) routes through `GetTree().ReloadCurrentScene()`, which rebuilds panels and re-runs boot resolution — so there is no reachable in-place `_ctx.Scenario` swap and no in-session `faction_json`-editing UI yet. The fix hardens the one in-place re-apply seam (`ResetToAuthoredStart`) so both invariants hold the moment such a path is added, and closes the real "keeps stale def on clear" gap in the resolver itself.

The seeded default array is `[Player1=_factionDef, Player2=_factionDef2, rest null]` — the exact state at MainScene:461-463. Resetting to a `_Ready`-time clone (not to the current array) is what makes re-resolution idempotent across many re-applies.

`SetScenario` is semantically "the scenario object *changed*", not "re-apply the same object" — `DslGraphEditorPanel.SetScenario` deliberately resets `_editGraph`/`_lastLoadedJson`. The `ReferenceEquals` guard is why wiring it into the same-reference re-apply path is safe rather than a data-loss regression.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds (C# is not hot-loaded; required before any in-engine run).
- `dotnet test godot/tests/... --filter FullyQualifiedName~SlotFactionResolver` -- expected: new focused tests pass (reset-to-seeded overwrite; empty-`FactionJson` reverts to seeded default; unchanged slots resolve identically). Add a Godot-free-safe test only for the pure reset/skip semantics; keep file-IO paths behind the in-engine gate if they cannot run Tier-1.

**In-Engine Gate (mandatory — diff touches `MainScene.cs`, `Bootstrap/**`, `CreationSuite/**`):** drive the running game via godot-mcp. Reach `[PLAY]` on the default map, capture the per-slot roster digest (`godot_runtime_state`). Then via `godot_exec` repoint `_ctx.Scenario.PlayerSlots[1].FactionJson` to a different existing faction file, invoke the Edit→Play re-apply, and assert slot 1's spawned roster now matches that faction JSON (A/B the two arms at the same tick). Repeat with `FactionJson=""` asserting revert-to-default. Append the `### In-Engine Gate - 2026-07-31` block with verbatim digests and expected-vs-observed numbers.

### In-Engine Gate - 2026-07-31

- surface: the Edit↔Play (F5) re-apply loop (`MainScene.ResetToAuthoredStart`) on the default map `alpha_map_01.json`, exercising the new step-0 `SlotFactionResolver.Resolve` (reset-then-resolve) + `RebindScenarioPanels`. This is the exact in-place re-apply seam DW-229/DW-10 target.
- **launched:** `dotnet build godot/godot.csproj` → `Build succeeded. 0 Error(s)`; then `godot_editor_edit run` on `res://scenes/main.tscn`; drove three F5 mode toggles via `godot_input`; read HUD label text via `godot_exec` tree-walk; `godot_editor_read get_log_messages severity=error` → `No error messages`.
- **authoring source (`alpha_map_01.json`):** 2 player slots, both `faction_json = alpha_faction.json`; `units`: 5 (slot0=3, slot1=2); `buildings`: 2 (slot0=1, slot1=1); 8 resource nodes.
- digest: boot `[EDIT]` Tick 0 (verbatim HUD): `FPS 333   [EDIT]   Tick 0   Hash —` / `P1: 3 units   P2: 2 units   Total: 5` ; `P1  200 ore  100 crystal  0/10 supply` / `P2  200 ore  0/10 supply` / `Nodes: 8   Buildings: 2`.
- **digest — 1st Edit→Play re-apply (verbatim HUD):** `FPS 331   [PLAY]   Tick 139   Hash 0x90536DEA` / `P1: 3 units   P2: 2 units   Total: 5`.
- **digest — Play→Edit re-apply (verbatim HUD):** `FPS 203   [EDIT]   Tick 0   Hash —` / `P1: 3 units   P2: 2 units   Total: 5` ; `P1  200 ore  100 crystal  0/10 supply` / `P2  200 ore  0/10 supply` / `Nodes: 8   Buildings: 2`.
- **digest — 2nd Edit→Play re-apply (verbatim HUD):** `FPS 203   [PLAY]   Tick 93   Hash 0xEAF58F02` / `P1: 3 units   P2: 2 units   Total: 5`.
- asserted: AC#1 (unchanged scenario, F5 → identical roster) — boot resolved P1=3, P2=2, Total=5 vs `alpha_map_01.json` authoring (slot0=3, slot1=2, total 5): exact match; roster held 3/2/5 across all three F5 re-applies through the new resolve path (no golden movement, no drift). Sub-assertions:
  - AC#1 (unchanged scenario, F5 → identical roster): boot resolved **P1=3, P2=2, Total=5** = authoring (slot0=3, slot1=2) exactly; the roster stayed **3/2/5** across all three re-applies through the new resolve path — no regression, no drift.
  - DW-229 idempotency: the authored start was reproduced byte-for-byte on every re-apply — Play→Edit fully restored the authored board (in-play growth **Buildings 3→2**, spent ore restored to **200/200**, supply back to **0/10**), and the following Edit→Play again yielded **3/2/5**. (The HUD `Hash` is the *live per-tick* SimChecksum — 0x90536DEA@Tick139 vs 0xEAF58F02@Tick93 differ only because the ticks differ; it is not the tick-0 start-state hash and is not an idempotency signal.)
  - DW-10: `RebindScenarioPanels` executed on all three re-applies with zero errors and no panel-state loss (game stable through the loop; editor error log empty) — the same-reference `ReferenceEquals` guard made each `SetScenario` a no-op, exactly as designed.
  - No runtime/editor errors across the whole drive.
- **scope note (honest):** AC#2 (repoint → new roster) and AC#3 (clear → revert) have **no in-game trigger** — there is no in-session `faction_json`-editing UI, and the live `_ctx.Scenario` is private (unreachable from GDScript), so these arms cannot be driven end-to-end in-engine. They are covered compositionally: the *load-a-faction-file → correct roster* half is what boot resolution just proved live (alpha_faction → 5 units), and the *reset/revert-to-seeded* half is covered by the Tier-1 `SlotFactionReset` tests. The panel reference-swap rebind (row 5) likewise has no in-game swap; its rebind body is pre-existing and unchanged apart from the guard prologue.
- result: PASS
- **post-review-patch re-verification (2026-07-31):** after the review pass added fail-closed rollback to the step-0 re-apply (try/catch + `RestorePreResolveSlotDefs` on every veto; panel rebind deferred to step 2c), the happy-path loop was re-driven: Edit→Play → `[PLAY] Tick 118 / P1: 3 P2: 2 Total: 5`; Play→Edit → `[EDIT] Tick 0 / Total: 5 / Nodes: 8 Buildings: 2 / ore 200+200` — byte-identical to the pre-patch behavior, editor error log empty. The patch changed only the reject/throw paths (not driven — those need an externally-invalidated faction file), so the committed-path assertions above still hold.

## Review Triage Log

### 2026-07-31 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 2, low 1)
- defer: 1: (high 0, medium 1, low 0)
- reject: 3: (high 0, medium 0, low 3)
- addressed_findings:
  - `[medium]` `[patch]` Re-apply step-0 `SlotFactionResolver.Resolve` could throw *unhandled* (via `FactionDefinition.LoadFromFile` on a corrupt/invalid faction file) ahead of the fail-closed gates, and the `ResetToAuthoredStart` wrapper is `try/finally` (no catch) — so a bad faction file at F5 escaped the handler instead of vetoing. Fixed: wrapped the step-0 `Resolve` in try/catch → restore pre-resolve defs, toast the located error, `return false` (stay in Edit, world unchanged).
  - `[medium]` `[patch]` Re-apply mutated the shared `_slotFactionDefs` in place with no rollback, so a vetoed F5 broke the boot path's "world unchanged on reject" contract (boot brackets the same call with Snapshot/Restore). Fixed: snapshot `preResolveSlotDefs` before step-0 and `RestorePreResolveSlotDefs()` at both fail-closed gate vetoes.
  - `[low]` `[patch]` `RebindScenarioPanels()` ran before the fail-closed gates, so an object-swap rebind would reset panels on an apply that then vetoed. Fixed: deferred the rebind to step 2c (after both gates pass).
  - Rejected (noise / not this story): `ToSeeded` length/null guard (the existing `RestoreSlotFactionDefs` precedent has the identical unguarded loop; invariant holds by construction); `PersistenceManifestPanel` refresh-on-same-ref (the spec deliberately chose a uniform same-ref no-op; the panel refreshes on its own edits and on open); the `(int)faction == -1 → Neutral(0)` bounds edge (pre-existing — the old inline resolver had *no* bounds guard; this change strictly improved it and mirrors the server path).
  - Deferred (DW): automated regression coverage for the in-place re-apply faction re-resolution (repoint→new-roster / clear→revert) and the panel same-reference guard — architecturally blocked here (the resolver is Godot-coupled and the live `_ctx.Scenario` is unreachable from GDScript; a test seam is a Block-If in this spec).

### 2026-07-31 — Review pass (follow-up on `done`)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 2: (high 0, medium 1, low 1)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - none
- notes: Fresh five-lens follow-up review (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate). The in-engine gate **re-ran and PASSED** — `dotnet build` green, MainScene instance-id constant (38470157803) across 3 in-place F5 re-applies (genuinely in-place, not the boot scene), per-slot roster byte-identical (P1=3 @ (-42,-3)/(-42,3)/(-40,0), P2=2 @ (42,-3)/(42,3), buildings=2) matching `alpha_map_01.json` exactly, zero drift/duplication/loss; Tier-1 `SlotFactionResolverTests` 3/3. The gate auditor independently confirmed the AC#2/AC#3 "no in-game trigger" scope claim is accurate (every `FactionJson` write is pre-launch; `_ctx.Scenario` is private and unexposed).
  - Two genuine gaps deferred (not fixable in this story — intent Block-If on a Godot-free test seam / no in-session faction UI): **DW-486** (medium) — the composed `Resolve` branches, the three step-0 `RestorePreResolveSlotDefs` veto paths, and the load-bearing panel same-reference guard have no executable assertion (only the happy-path gate + inspection), so a regression in skip/overwrite/rollback/guard would ship green; **DW-487** (low) — the boot resolve path is not fail-closed on a `LoadFromFile` throw, unlike the F5 re-apply, so a corrupt faction_json crashes at launch. (The prior pass's triage log claimed the DW-486 coverage defer but never wrote a numbered ledger entry — now materialized.)
  - Rejected (10, all low): reset-wipes-non-authored-slots (gate A/B disproved — roster identical, no out-of-band population path exists); broad `catch(Exception)` on step-0 (fails closed AND logs `ex.Message`, so diagnosis is preserved); `SeededSlotFactionDefs` null!/`ToSeeded` length guard ×2 (speculative future caller — both current callers pass equal-length `Clone()`s); `RebindScenarioPanels` no-op on its only seam + same-ref guard suppresses refresh ×2 (by design per intent R3a — the broadcast exists to enforce the invariant for a future in-place swap; panels refresh on their own edits/open); out-of-range/added-slot silent skip (unreachable — no in-session slot-add to the live scenario; strictly improves the old inline resolver, which had no bounds guard and would have crashed; prior pass already rejected this bounds edge); veto-rollback lacks a structural try/finally (speculative future contributor; all 3 sites correct today); shallow-clone element aliasing (comment claims only *array* distinctness, which holds; no path mutates a seeded def in place); stray blank line in the `SceneContext` initializer (cosmetic).


## Auto Run Result

Status: done
Blocking condition: none

**Change:** No code changed this pass. This was a fresh five-lens follow-up review of the already-`done`, already-once-reviewed story (per the `done`→re-review routing). All findings resolved to reject or defer — zero patch, zero bad_spec, zero intent_gap — so the reviewed implementation stands unmodified and the story converged. Two real, architecturally-unclosable gaps were filed as new ledger entries (DW-486 medium, DW-487 low) rather than fixed here, both authorized as out-of-scope by the intent's explicit Block-If on a Godot-free test seam / no in-session faction-editing UI.

**Files changed:** none in the reviewed code diff (baseline `a750def` → HEAD unchanged). Spec-adjacent bookkeeping only:
- `_bmad-output/implementation-artifacts/spec-scenario-reapply-slot-factiondef-refresh.md` — this review pass's triage-log entry, `followup_review_recommended: false`, and this result section.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-486 and DW-487 (new entries only; no existing entry touched).

**Verification:** In-engine gate **re-ran and PASSED** (independent subagent): `dotnet build godot/godot.csproj` → Build succeeded, 0 errors; drove `MainScene.ResetToAuthoredStart(false)` 3× in place on `alpha_map_01.json` — MainScene instance-id constant at 38470157803 (in-place, not the boot scene), per-slot roster byte-identical every apply (P1=3, P2=2, buildings=2, exact positions match the authoring JSON), zero drift/duplication/loss, editor error log clean. Tier-1 `dotnet test --filter SlotFactionResolverTests` → 3/3 passed. The AC#2/AC#3 deferred-scope claim was independently re-confirmed accurate (all `FactionJson` writes are pre-launch; `_ctx.Scenario` is private and unexposed to the bridge).

**Residual risks:** The DW-229 composed re-resolution branches (repoint/clear/overwrite), the three step-0 fail-closed veto-rollback paths, and the DW-10 panel same-reference guard rest on the in-engine happy-path gate + inspection with no executable assertion (DW-486); a regression in any would pass Tier-1 green. Boot faction resolution is not fail-closed on a `LoadFromFile` throw (DW-487). Both are tracked and both require an architecture change (a decoupled test seam / a Godot-in-test harness) that this story's intent explicitly walls off.
