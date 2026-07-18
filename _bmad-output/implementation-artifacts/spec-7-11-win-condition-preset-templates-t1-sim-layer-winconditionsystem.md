---
title: 'Win-condition preset templates (T1) + sim-layer WinConditionSystem'
type: 'feature'
created: '2026-07-17'
status: done
review_loop_iteration: 0
followup_review_recommended: true
baseline_revision: '16369772b1bf327c4cd7805cc4d13eb93ce86991'
final_revision: '4b5a66734ca6493e28c8c4a1cf4bb9540b016cb5'
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-7-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Win evaluation lives in presentation — `MainScene.CheckWinCondition` is a per-frame, P1/P2-hardcoded 2-case switch — so multiplayer victory is not server-checkable or guaranteed identical across clients, and authors have only two bare built-in win conditions with no turnkey objectives.

**Approach:** Add a deterministic sim-layer `WinConditionSystem` that ticks in the fixed loop (ascending-id order), evaluates the two existing built-ins (verified to pick the same winner/loser as the old switch) plus four named T1 presets (King of the Hill, Timed Survival, Assassination, Landmark Destruction) from a typed spec on `ScenarioData`, and emits a `Faction`-typed verdict that presentation merely consumes. New per-match win-state folds into `SimChecksum` and preset params into `CanonicalModelHash`, each with a named golden re-baseline.

## Boundaries & Constraints

**Always:**
- `WinConditionSystem` is pure sim: no `using Godot`, no `float`/`double`/`Mathf`, no wall-clock, no `Fixed.FromFloat`. All fractional math is `Fixed` (16.16); entities iterate `0..HighWaterMark` skipping `!IsAlive`; factions iterate `registry.ActiveFactions` (never a bare `0..FACTION_COUNT` literal); any randomness is `world.Rng` only.
- It implements `ISimSystem.Tick(EntityWorld, Fixed)` and is registered in the `SimulationHost` system array **after** `AiOpponentSystem` and **immediately before** `ScenarioDirector` (so it sees post-death alive counts and the director still runs last). Update the `SystemOrderTest` expected list and the "N systems" host log line.
- New per-match win-state (per-faction KotH hold-tick counter; Timed-Survival deadline tick; per-faction verdict latch) is a **per-faction SoA store** built on the `ResearchStore` pattern (sized `FACTION_COUNT`, `Clear()` restores post-ctor state, owned as a `SimulationHost` property, reset from `ClearForReset()`), integer ticks only. It folds into `SimChecksum.Compute` in declaration order **before the SimRng block**, iterating `ActiveFactions`; bump `SimChecksum.AlgoVersion` 18→19 and **re-baseline all per-tick world goldens** in the same commit; add a differential-mutation assertion to `SimChecksumCoverageGuardTest`, update its hand-pinned known-state hash (`ExpectedV18Hash`→v19, no record hook — copy the hex from the failure message and rename the method to v19), and update `VersionStampConsistencyTests`.
- Preset selection + typed params persist on `ScenarioData` and fold into `CanonicalModelHash` (which already folds `WinCondition` at `CanonicalModelHash.cs:162`); bump `CanonicalModelHash.AlgoVersion` 11→12, update `VersionStampConsistencyTests`, extend `CanonicalModelHashPerfTests.BuildMaxCapsScenario` to exercise a preset, and **re-baseline `hero-start-state.golden.txt`**. A scenario using a built-in enum with no preset must fold **byte-identically to today apart from the AlgoVersion bump** (omit-when-default discipline).
- The two built-ins produce the **same** winning/losing faction as the old presentation switch, proven by a headless parity test per case (DestroyAllBuildings, EliminateAllUnits) **before** any preset code is added.
- Verdict type is `None / FactionWon(Faction) / FactionLost(Faction)`. Presentation reads only the verdict to drive `ShowGameOver` (`Faction.Player1==1` aligns with the existing 1-based arg) and retains **no** win math. Convert the old 180-frame grace to a tick count.
- Each preset is authorable as canonical public-DSL graph-IR reusing the existing public `victory`/`defeat` action nodes (`NodeBase.cs:410`) — **no hidden engine-only opcode** — and round-trips through the `TriggerGraph` schema unchanged.
- Invalid/missing preset params reject **at load** via `ScenarioValidator` with a single located error naming the preset and offending param (pattern: `ScenarioValidator.cs:626-638`), never a runtime crash or a silently un-winnable match.
- KotH contested rule: the hold-tick counter does **not** advance for either faction on a tick where two or more factions each have ≥1 alive unit in the zone, and resets to 0 for a faction the tick it no longer **solely** holds the zone.
- The existing `ScenarioDirector.OnVictory` trigger action (`ScenarioDirector.cs:193, 1572-1576`) stays intact as the advanced/T3 escape hatch.

**Block If:**
- Making the new `SimChecksum` fold or the new `CanonicalModelHash` preset-param fold **re-save-neutral for existing default scenarios** proves impossible (a non-AlgoVersion hash drift on unchanged content) — do not ship a divergent handshake; HALT.
- Any AC cannot be met without pulling **Story 7.13** trigger vocabulary forward — i.e. adding a *generic* DSL read-accessor / entity-or-structure **instance-designation node kind** (beyond a typed `ScenarioData` param + a preset's canonical graph-IR template) — HALT rather than expand scope.

**Never:**
- No second win-evaluation path: after this lands, `MainScene` holds no win math; do not leave `CheckWinCondition` live alongside the system.
- Not a generic win-condition engine; **not** N-faction (>2) resolution and **no** alliance mask — those are Story 7.12. Keep the existing P1/P2 two-faction assumption.
- Do not rework the `OnVictory` escape hatch.
- No new *generic* DSL node kinds or read-accessor leaves (7.13 scope).
- No `float`→`int` truncation and no string formatting (mm:ss, int→string) inside the tick — formatting is presentation-only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| DestroyAllBuildings (built-in) | P1 has ≥1 alive building, all P2 buildings dead | verdict `FactionWon(Player1)`; presentation `ShowGameOver(1)`; matches old switch | No error |
| EliminateAllUnits (built-in) | all P2 units dead, P1 has ≥1 alive unit | verdict `FactionWon(Player1)`; matches old switch | No error |
| Both sides alive / pre-grace | both retain assets, or tick < grace | verdict `None` (no game over) | No error |
| Timed Survival | designated faction alive at tick N | at tick N → `FactionWon(designated)`; if eliminated before N → `FactionLost(designated)` | N ≤ 0 → located reject at load |
| King of the Hill (sole hold) | faction F solely holds region R for N contiguous ticks | counter reaches N → `FactionWon(F)` | undefined `region_id` → located reject |
| King of the Hill (contested) | ≥2 factions each have a unit in R on the same tick | counter does not advance for any; resets to 0 for a non-sole holder | No error |
| Assassination | designated leader unit dies | `FactionLost(leader's faction)` → other faction wins | leader placement ref out of range / unassigned → located reject |
| Landmark Destruction | designated structure destroyed | `FactionLost(owner)` | structure placement ref invalid / nonexistent → located reject |
| Preset round-trip | instantiated preset → canonical graph-IR → reload | byte-identical canonical JSON; validator-clean | unknown node kind → located reject |

</intent-contract>

## Code Map

- `godot/src/Core/SimulationLoop.cs` — `ISimSystem.Tick(EntityWorld, Fixed)` (:11-15); `FixedDt` 1/30s (:27); `StepOnce()` ticks systems ascending (:124-140); folds `SimChecksum` (:69, 86-103, 137, 174).
- `godot/src/Core/Sim/SimulationHost.cs` — system array (:218-261, tail `[13] Ai`, `[14] ScenarioDirector`); `internal Systems` + `SystemOrderTest` (:34, 217, 346-349); host log (:269); `ClearForReset()` (:296); store ownership + `EnableChecksums` (:95, 170, 264).
- `godot/src/Core/ResearchStore.cs` — **copy-me** per-faction SoA store: declare (:24, 55-75), `Clear()` (:105-118); its fold at `SimChecksum.cs:493-514`.
- `godot/src/Core/SimChecksum.cs` — FNV-1a; `Compute()` (:216), `Mix()` (:567), SimRng block last (:553-559); `AlgoVersion=18` (:210).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — folds `WinCondition` (:162); `AlgoVersion=11` (:150).
- `godot/src/Core/EntityWorld.cs` — `Faction` enum (:90-97); `IsAlive` (:1131), `FactionOf` (:211), `HighWaterMark` (:665). Alive-count pattern at `ScenarioDirector.cs:1662-1669`.
- `godot/src/Core/BuildingStore.cs` — `Alive`/`FactionOf` (:40-42), scan `0..Count`; per-faction scan pattern `ScenarioDirector.cs:1350-1354`.
- `godot/src/Core/FactionRegistry.cs` — `ActiveFactions`; `FACTION_COUNT=5`.
- `godot/src/Core/RegionStore.cs` — `TryGetIndex` (:49), `Contains(idx, pos)` (:71); owned by `ScenarioDirector` (`_regions` :150), injected via `SetRegionStore` (:231) from `ScenarioApplier.BuildRegionStore` (:324-349). "faction-F units in region" query at `ScenarioDirector.cs:1367-1374`.
- `godot/src/Core/Definitions/ScenarioData.cs` — `WinCondition` enum (:7-17), `win_condition` prop (:571), factory default (:925); `StartUnit`/building placement lists (no per-instance id); `TriggerGraphJson` (:642); `Regions` (:786).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — `Validate` (:81); located dangling-`region_id` rule (:626-638) — copy for preset params; `ValidationResult.Fail(located)` (`Validated.cs:72`).
- `godot/src/Dsl/TriggerGraph.cs` — `ToCanonicalJson()` (:959), `FromJson` (:975); round-trip test `TriggerGraphCanonicalTests.cs:60`. Public `victory`/`defeat` action nodes `NodeBase.cs:410`.
- `godot/src/Core/ScenarioDirector.cs` — `OnVictory` (:193, 1572-1576) — leave intact; end-of-tick order (runs last).
- `godot/src/Bootstrap/Phases/WinConditionPhase.cs` — editor picker = two toggle buttons in a `ButtonGroup` (:44-76), writes `_ctx.Scenario.WinCondition`, `WinConditionUiRefresh` resync (:71-76).
- `godot/MainScene.cs` — `CheckWinCondition` (:1817-1854, called from `_Process` :748, frame-grace `_playFrames>180` :744); `ShowGameOver(int winnerPlayer)` 1-based (:1286-1427); `ScenarioDelegateBinder.cs:48` maps `OnVictory` 0-based → `ShowGameOver +1`.
- `godot/ProjectChimera.Sim.Tests/` — xUnit; `Golden/*.golden.txt` (24 world goldens + `hero-start-state.golden.txt`); record via `CHIMERA_GOLDEN_RECORD=1`; `SimChecksumCoverageGuardTest.cs`, `VersionStampConsistencyTests.cs`, `CanonicalModelHashPerfTests.cs` (`BuildMaxCapsScenario` :59).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ScenarioData.cs` -- add a typed `WinConditionSpec` (discriminated: built-in enum, or one of 4 preset kinds with params — KotH `{region_id, hold_ticks}`, Survival `{faction_slot, survive_ticks}`, Assassination `{leader_unit_index}`, Landmark `{structure_index}`). Persist alongside `win_condition`; keep the bare enum as the default/built-in path. Round-trip through the scenario serializer.
- `godot/src/Core/WinConditionSystem.cs` (new) -- `ISimSystem` evaluating all six conditions from the applied spec against `EntityWorld` + `BuildingStore` + `RegionStore` + the win-state store; emits a `Faction`-typed verdict; enforce the tick grace and the KotH contested/sole-hold rule; resolve preset placement refs (leader/structure) to runtime ids at scenario-apply (mirror `SetRegionStore` injection).
- `godot/src/Core/WinStateStore.cs` (new) -- per-faction SoA (KotH hold counter, survival deadline, verdict latch) on the `ResearchStore` pattern; `Clear()`.
- `godot/src/Core/Sim/SimulationHost.cs` -- own + construct + `ClearForReset` the store; register `WinConditionSystem` before `ScenarioDirector`; thread the store into `EnableChecksums`; update the systems log and `SystemOrderTest` expectations.
- `godot/src/Core/SimulationLoop.cs` -- thread the win-state store param into both `SimChecksum.Compute` call sites.
- `godot/src/Core/SimChecksum.cs` -- fold the win-state store before the SimRng block; bump `AlgoVersion` 18→19 with a version note.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- fold the preset params after `WinCondition`; bump `AlgoVersion` 11→12; keep default/no-preset scenarios byte-identical apart from the version bump.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- located rejects for each preset param (undefined region_id; survive_ticks/hold_ticks ≤ 0; leader/structure index out of range or unassigned).
- `godot/src/Dsl/WinConditionPresets.cs` (new) -- pure function instantiating each preset as canonical public-DSL graph-IR (via `victory`/`defeat` + existing condition nodes); the authored/serialized form used to prove round-trip stability.
- `godot/MainScene.cs` + `godot/src/Bootstrap/Phases/ScenarioDelegateBinder.cs` -- delete `CheckWinCondition` win math; read the system verdict → `ShowGameOver((int)winnerFaction)`; keep the `OnVictory` binding.
- `godot/src/Bootstrap/Phases/WinConditionPhase.cs` -- expand the picker from 2 toggles to all 6 options with each preset's required param fields inline; write the `WinConditionSpec`; round-trip on save/reload.
- `godot/ProjectChimera.Sim.Tests/` -- headless tests: built-in parity (2 cases), each preset resolves the correct faction headless, KotH contested + sole-hold, all preset load-reject cases, preset graph-IR round-trip unchanged, the six-condition determinism replay (two runs byte-identical), `SimChecksumCoverageGuardTest` differential mutation, `VersionStampConsistencyTests` (19/12), and re-record both golden sets.

**Acceptance Criteria:**
- Given the two built-in win conditions, when a fixed 2-faction scenario reaches each end-state headlessly, then the `WinConditionSystem` verdict names the same winning/losing faction the old `MainScene` switch produced, and `MainScene` holds no win math (only verdict consumption).
- Given the four presets applied with valid params, when run in a headless match, then each resolves victory/defeat for the correct faction, and each preset instantiated as graph-IR round-trips through the `TriggerGraph` schema **byte-identical** and validator-clean.
- Given the new win-state SoA store, when two headless runs execute the same seeded scenario+command stream triggering each of the six conditions, then `SimChecksum` folds every live win-state field in declaration/ascending order and both runs yield a byte-identical final checksum, with the golden baseline re-recorded and `SimChecksum.AlgoVersion==19`.
- Given a KotH zone contested by two factions on the same tick, when the hold counter is evaluated, then it advances for neither and resets to 0 for a faction that no longer solely holds the zone.
- Given a preset with an invalid/missing required param, when the scenario loads, then it is rejected at load with one located error naming the preset and the offending param (no crash, no un-winnable match).
- Given the picker UI, when opened, then it lists all six options with each preset's param fields inline, writes the corresponding spec into `ScenarioData` such that it reloads to the same selection, and the `ScenarioDirector` `OnVictory` escape hatch remains intact.

## Spec Change Log

## Review Triage Log

### 2026-07-17 — Review pass

Independent four-layer pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff (`16369772..working tree`, tracked + untracked, golden `.txt` data payloads excluded as mechanical re-records). All four layers confirmed the determinism engineering is sound (fold placement before the SimRng block, AlgoVersion bumps 18→19 / 11→12, ~two dozen version pins, the `SimChecksumCoverageGuardTest` per-faction teeth under `FactionRegistry(2)`, validator fail-closed). Intent Alignment confirmed the diff is a clean, disclosed implementation of the native-evaluator + round-trippable-public-DSL-witness reading (the architecture the Design Notes selected on epic-context + 7.13-vocabulary-absence authority) — no intent gap. Every escalated finding was verified against the actual code before triage.

- intent_gap: 0
- bad_spec: 0
- patch: 12: (high 0, medium 6, low 6)
- defer: 2: (medium 2)
- reject: 3
- addressed_findings:
  - `[medium]` `[patch]` **Neutral-winner deadlock + AR-3 raw loop.** In a single-active-faction match a loss-driven preset made `OtherFaction` return `Neutral`, `Resolve` wrote `Verdict[0]=WON`, and `IsResolved()`/`WinnerFaction()` scanned from index 0 → match latched "resolved" but reported winner 0 → frozen, no game-over. Fixed: the two scans skip Neutral (start `f=1`); `Resolve` writes WON only for `w>0`.
  - `[medium]` `[patch]` **Win-eval grace (90 ticks) applied to presets too**, so any `hold_ticks`/`survive_ticks < 90` could not resolve until tick 90 (authored param meaningless). Fixed: grace now gates only the built-in path; presets latch as soon as their condition is met (their per-faction counters already advanced every tick).
  - `[medium]` `[patch]` **Preset target that failed to spawn/place (-1) → silently un-winnable** — the exact outcome the validator promises to prevent (validator checks only authored-array range, not spawn success). Fixed: an unresolved Assassination/Landmark target now resolves the owner's defeat deterministically.
  - `[medium]` `[patch]` **`CanonicalModelHash` v12 preset fold had no differential test** — replacing `MixWinConditionSpec`'s body with `return h` passed the whole suite, so the multiplayer-safety purpose (divergent presets → different hash → `HandshakeGate` blocks) was unverified. Fixed: new `CanonicalModelHashWinConditionFoldTests` (kind + every param moves the hash; None≡null; enum and preset both fold).
  - `[medium]` `[patch]` **Applier placement-map → `Configure` wiring untested end-to-end** — all Assassination/Landmark tests injected hand-built maps, bypassing the applier's own `unitEntityIds`/`buildingSlots` construction. Fixed: new `WinConditionApplierResolutionTests` applies a real scenario, destroys the designated target, and asserts the correct winner (incl. the -1 fail-to-spawn path).
  - `[medium]` `[patch]` **`SixConditionWinState_FoldsDeterministically` exercised only one condition (KotH)** despite its name / AC3's "each of the six conditions." Fixed: broadened to a Theory driving all six conditions through two identical tick sequences asserting byte-identical `SimChecksum`.
  - `[low]` `[patch]` **`Configure` not self-contained** — relied on an external `WinState.Clear()`; a re-`Configure` without it left stale `SurvivalRemaining`/`KothHoldTicks` in the folded store. Fixed: `Configure` calls `_store.Clear()` first.
  - `[low]` `[patch]` **Stale cross-preset params serialized** — the picker reuses one `WinConditionSpec`, so switching presets left dead params that `[JsonIgnore(WhenWritingDefault)]` then wrote, while the hash folds only active params (serialize/hash-parity drift). Fixed: the serializer keeps only the active preset's fields (on a copy; live editor spec untouched) + round-trip test.
  - `[low]` `[patch]` **Serializer None→null normalization untested.** Fixed: `WinConditionSpecSerializationTests` (None omits the key / round-trips null; presets round-trip params).
  - `[low]` `[patch]` **Verdict-latch finality untested** (double-guarded, mutually masking). Fixed: a test that resolves then keeps ticking with changed state and asserts the winner never flips/duplicates.
  - `[low]` `[patch]` **Built-in double-elimination tie-break (Player2 wins) asserted only by comment.** Fixed: a both-sides-zero-buildings test pinning the old-switch Player2 bias.
  - `[low]` `[patch]` **Preset round-trip witness lacked param teeth.** Fixed: assert KotH `region_id` and TimedSurvival faction survive the graph round-trip byte-identically (the params the public DSL can encode; leader/structure instance designation is the documented 7.13 gap — DW noted).

Deferred (2, logged as DW-184 / DW-185): (1) Assassination/Landmark can miss a target death via same-tick same-faction `EntityWorld` slot recycle (no entity generation counter) — real but currently hard to reach (hero revival is tick-delayed; production completes after the win evaluator); (2) the built-ins/presets are hard-2-player and mis-resolve a >2-faction scenario — explicitly out of scope per the intent (owned by Story 7.12).

Rejected (3): KotH counting only units (AC4 literally says "units inside the zone"); the alleged dead `_playFrames`/`_matchStartMs` in `MainScene` (still read — `_playFrames==0` stamps `_matchStartMs`, which feeds the game-over elapsed-time stat); "no shared built-in counting helper" (maintainability opinion; parity is pinned by the built-in-parity tests).

### 2026-07-18 — Review pass (follow-up)

Independent four-layer follow-up pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) over the full story diff (`16369772..working tree`, golden payloads excluded as mechanical re-records), triggered by the prior pass's `followup_review_recommended: true`. Intent Alignment enumerated three defensible readings of the preset/DSL clause and confirmed the diff cleanly implements the native-evaluator + expressibility-witness reading (the one Block-If #2's own parenthetical licenses) with every divergence disclosed — no intent gap. Every escalated finding was verified against the actual code before triage. After patching, the previously-skipped in-engine check was partially executed live via godot-mcp: the six-option picker verified in the running editor (toggles drive the real handlers; inline params respond; the patched slot clamp 0–3 confirmed live), and a Timed Survival playtest ran to resolution — VICTORY overlay for Player 1 at exactly the authored tick 150 (verdict→`ShowGameOver` wiring proven end-to-end).

- intent_gap: 0
- bad_spec: 0
- patch: 13: (high 0, medium 8, low 5)
- defer: 1: (medium 1)
- reject: 7
- addressed_findings:
  - `[medium]` `[patch]` **LOST-only outcome invisible** — in a single-active-faction match a preset loss latched `VERDICT_LOST` with no winner; `IsResolved()`/`WinnerFaction()` scanned only WON → match never ended (one hop from the pass-1 Neutral fix). Fixed: `IsResolved()` latches on any non-NONE verdict; new `SoleLoserFaction()`; `MainScene` shows the no-victor defeat overlay (`ShowGameOver(0)`); single-faction latch-freeze test.
  - `[medium]` `[patch]` **Tick-1 false-loss window** — presets bypass grace entirely, and `WinConditionSystem` (idx 14) runs before `ScenarioDirector` (15), so a designated faction/target spawned by `match_start` triggers read as absent on tick 1 → instant loss. Fixed: only the three loss-by-absence branches (survival elimination; unresolved -1 assassination/landmark targets) are grace-gated; win paths and real-target-death latches stay ungated so short authored params still resolve.
  - `[medium]` `[patch]` **Unknown `WinPresetKind` passed validation** (no `default:` in the preset switch) → hand-edited `"preset": 99` loaded silently un-winnable. Fixed: default located reject + test.
  - `[medium]` `[patch]` **Preset slots above the engine faction ceiling** — declared slots reach 7 but `Faction` tops out at slot 3; a TimedSurvival `faction_slot ≥ 4` validated clean, `Configure` skip-seeded, and the wrong faction won on tick 1. Fixed: validator enforces the canonical `CheckFactionSlot` [0,3] ceiling on `faction_slot` and on the designated Assassination/Landmark placement's slot; picker slot spinner clamped 0–3; three validator tests.
  - `[medium]` `[patch]` **KotH unresolved region silently un-winnable** — `_regionIndex == -1` (defensive `BuildRegionStore` skips / direct hosts) made `UpdateKothCounters` a permanent no-op, the exact P3-class outcome fixed elsewhere. Fixed: `Configure` falls back deterministically to the built-in path when the region is unresolved or `hold_ticks ≤ 0` (post-gate defense-in-depth) + test.
  - `[medium]` `[patch]` **Landmark evaluator violated `BuildingStore`'s own ABA contract** — raw cross-tick slot+faction check despite the store's documented `PackRef`/`TryResolveRef` generation mechanism (Story 2.13 D-3); a same-tick same-faction slot recycle masked the destruction. Fixed: `Configure` captures the packed ref, `EvaluateLandmark` resolves through it; same-tick-recycle test now latches the loss. (Closes the buildings half of the DW-184 class with existing infrastructure; the EntityWorld half remains DW-184.)
  - `[medium]` `[patch]` **KotH/TimedSurvival never exercised through the real `ScenarioApplier.Apply`** — swapping the applier's region store for `RegionStore.Empty` kept the whole suite green. Fixed: applier-path tests for both presets driven through the real `host.StepOnce()` loop, closing both the wiring gap and the no-real-loop gap.
  - `[medium]` `[patch]` **AC2 "validator-clean" asserted nowhere and the TimedSurvival witness was semantically inert** (nameless `timer_expires` bound to no timer, encoding no `survive_ticks`, despite public `create_timer`/`TimerName` vocabulary). Fixed: witness rebuilt as a genuine two-trigger graph (`match_start`→`create_timer("survival", SurviveTicks)`; `timer_expires("survival")`→`victory`); validator-clean theory embeds each witness's canonical JSON in a minimal scenario and asserts `Validate` passes; remaining 7.13 expressibility gaps documented per-witness.
  - `[low]` `[patch]` **SimResetTests keystone lacked WinState teeth** — every prior folded store got dirty-then-fresh-equality assertions; added them for `WinState` in the file's convention.
  - `[low]` `[patch]` **Clear-without-Configure ghost config** — `ClearForReset` cleared the store but left `_preset`/`_survivalFaction`, so a cleared-but-unconfigured tick produced an instant false survival win. Fixed: `WinConditionSystem.ResetConfig()` called from `ClearForReset` + test.
  - `[low]` `[patch]` **Picker refresh kept stale params when the spec went null** — they silently re-entered on the next preset toggle. Fixed: `WinConditionUiRefresh` else-branch restores defaults.
  - `[low]` `[patch]` **Two undocumented semantics pinned** — Neutral units neither hold nor contest the KotH zone (deliberate, `ActiveFactions` excludes Neutral) and survival elimination-vs-deadline same-tick tie resolves to loss (elimination checked first); doc comments + tests for both.
  - `[low]` `[patch]` **Cosmetic** — dead inner preset guards removed, `MatchTicks` doc corrected (stops advancing on resolution), `GRACE_TICKS` doc rewritten for the new gating.

Deferred (1, logged as DW-186): editor surfaces can save a scenario the fail-closed loader then rejects on next boot (no save-time validation surface; author must hand-edit to recover) — a platform-level class predating 7.11 (trigger editor, regions, variables have the same exposure) that the picker's free-text/index params widen; closure is one save-time `ScenarioValidator` surface covering all authoring UIs.

Rejected (7): `WinConditionPresets` "dead code with no production consumers" (it IS the sanctioned AC2 expressibility witness under the reading the intent's Block-If #2 parenthetical licenses); preset-without-elimination-fallback stalemate (the intent's I/O matrix defines each preset's semantics exhaustively; combined conditions are the documented T3 `OnVictory` escape hatch); spec/sprint-status "disagreement" (expected orchestration state mid-follow-up-review); the "24 vs 23 goldens" wording nit (the Auto Run Result already states 23 correctly; `ai-active` is the disclosed Windows-only exception); built-in parity tests "asserting a transcription" (unavoidable once the old switch is deleted; parity was proven against live code before deletion per the pass-1 constraint); private `FACTION_COUNT = 5` duplication (verbatim `ResearchStore`/`ResourceStore` idiom — consolidation is explicitly Story 9.2's job per `FactionRegistry`'s own note); the Windows `ai-active-scenario.golden.txt` re-record (already-disclosed residual risk #1, unchanged).

## Design Notes

**Architecture decision — native evaluation, public-DSL template (evidence-backed, not a free choice):** The four presets are evaluated **natively** by `WinConditionSystem` from a typed `WinConditionSpec`, not executed by the trigger executor. This is forced by ground truth: the DSL at 7.11 has **no** primitive to designate a specific leader entity or structure instance and **no** per-entity-alive read, and `unit_in_region` is presence-of-any (cannot express AC4's contested-*exclusive* rule) — that vocabulary is explicitly earmarked for Story 7.13 (`NodeBase.cs:361`), and pulling it forward is an unsanctioned scope explosion (Block-If #2). The authoritative epic-context decision (epic-7-context.md:52) independently selects this reading: win-state (KotH counters, survival deadlines) are "parallel SoA/declaration-index stores … folded into SimChecksum," which is native system state, not generic `DslVarTable` variables (those already fold, so AC3's re-baseline mandate would be vacuous otherwise). AC2's "expressed entirely through the public DSL … proven by round-tripping through the schema unchanged" is satisfied as an **expressibility/serialization** property: each preset's canonical graph-IR form uses only public registry nodes (the `victory`/`defeat` actions already exist), round-trips unchanged, and compiles through the validator; the observable "correct faction wins in a headless match" is met by the system.

**Verdict convention:** emit the `Faction` enum directly (`Player1==1`); presentation calls `ShowGameOver((int)faction)`, matching the existing 1-based arg with no adapter math. This unifies the current split (presentation `CheckWinCondition` was 1-based; `OnVictory` was 0-based+1). Convert the 180-*frame* grace to ticks (the old grace was framerate-dependent).

**Leader/structure designation:** placed units and buildings carry no stable per-instance id, so Assassination/Landmark reference the target by its **index in the authored placement list**; the scenario-apply path resolves that index to the spawned runtime entity/building id (captured in the win-state store), and the validator rejects an out-of-range/unassigned index at load. No new DSL vocabulary required.

**RegionStore sharing:** `RegionStore` is currently private to `ScenarioDirector`; share the same instance into `WinConditionSystem` at apply-time (mirror `SetRegionStore`) so KotH region queries are consistent with the trigger layer.

**Fold discipline (highest risk):** two AlgoVersion bumps in one commit — `SimChecksum` 18→19 (win-state SoA → re-baseline the 24 per-tick world goldens; the hand-pinned `ExpectedV18Hash` has no record hook, copy the new hex from the failure message) and `CanonicalModelHash` 11→12 (preset params → re-baseline `hero-start-state.golden.txt` only). Author the preset-param fold so a **default/built-in scenario folds byte-identically to today except for the version bump** (omit-when-default). If two stories race the same bump integer, rebase and take the next free integer (colliding-bump hazard).

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green incl. new WinConditionSystem/preset/validator/round-trip/determinism suites.
- Re-baseline (same commit as the AlgoVersion bumps): `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~Golden"` then `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` then re-run **without** the env var → green.
- `CHIMERA_PERF_CEILING_SCALE=2 dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: determinism + perf gate green (matches CI).
- `grep -n "AlgoVersion" godot/src/Core/SimChecksum.cs godot/src/Core/Definitions/CanonicalModelHash.cs` -- expected: 19 and 12.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` -- expected: the 24 world goldens moved (SimChecksum bump) and `hero-start-state.golden.txt` moved (CanonicalModelHash bump); no other golden churn.
- `grep -rniE "using Godot|[^.]\bfloat\b|double |Mathf|FromFloat|DateTime|Environment.Tick" godot/src/Core/WinConditionSystem.cs godot/src/Core/WinStateStore.cs godot/src/Dsl/WinConditionPresets.cs` -- expected: no hits (sim is Godot/float/wall-clock free).

**Manual checks (in-engine, via godot-verify):**
- In the editor, open the win-condition picker: confirm all six options appear with each preset's param fields inline; select King of the Hill (bind a drawn region + N), save, reload → the same selection and params persist. Playtest (F5) each preset to a resolution and confirm the correct VICTORY/DEFEAT overlay; confirm a trigger-authored `victory` action (escape hatch) still ends the match.

## Auto Run Result

Status: done

**Summary:** Implemented Story 7.11 — moved win evaluation out of presentation into a deterministic sim-layer `WinConditionSystem` (`ISimSystem`, registered at index 14 after `AiOpponentSystem`, immediately before `ScenarioDirector`) that emits a `Faction`-typed verdict via a checksum-folded `WinStateStore`; `MainScene` now only reads `WinState.WinnerFaction()` (the per-frame P1/P2 `CheckWinCondition` switch is deleted). It evaluates the two built-ins (verified to pick the same winner/loser as the old switch) plus four T1 presets — King of the Hill (sole-hold a region N ticks, with the contested/exclusive rule), Timed Survival, Assassination, Landmark Destruction — from a typed `WinConditionSpec` on `ScenarioData`, resolved at scenario-apply (regions shared in; leader/structure designated by authored placement index → runtime id). New per-match win-state folds into `SimChecksum` (`AlgoVersion 18→19`, all per-tick world goldens re-recorded) and preset params into `CanonicalModelHash` (`AlgoVersion 11→12`, `hero-start-state` re-recorded). Presets are natively evaluated but each carries a canonical public-DSL graph-IR witness proven to round-trip unchanged (AC2 expressibility), reusing the existing public `victory`/`defeat` nodes — no hidden opcode. Validator rejects every invalid/missing preset param at load with a single located error. The `ScenarioDirector.OnVictory` trigger action is preserved as the T3 escape hatch.

**Files changed (production, 11):** `WinConditionSystem.cs` (new — evaluator), `WinStateStore.cs` (new — folded per-faction SoA), `Dsl/WinConditionPresets.cs` (new — round-trip witness factory); `ScenarioData.cs` (`WinPresetKind`, `WinConditionSpec`, `win_condition_spec`); `ScenarioSerializer.cs` (None→null + active-preset-only normalization); `CanonicalModelHash.cs` (`MixWinConditionSpec`, `AlgoVersion 11→12`); `ScenarioValidator.cs` (located preset-param rejects); `SimChecksum.cs` (win-state fold, `AlgoVersion 18→19`); `SimulationLoop.cs` (thread the store into both `Compute` sites); `Sim/SimulationHost.cs` (own/construct/reset the store, register the system, 16-system order); `Sim/ScenarioApplier.cs` (placement→runtime-id maps + `Configure`); `MainScene.cs` (delete win math, read verdict); `Bootstrap/Phases/WinConditionPhase.cs` (picker 2→6 options with inline params).

**Files changed (tests):** new `WinConditions/{WinConditionSystemTests, WinConditionPresetsTests, WinConditionSpecValidatorTests, CanonicalModelHashWinConditionFoldTests, WinConditionApplierResolutionTests}`, `Definitions/WinConditionSpecSerializationTests`; version pins `18→19`/`11→12` and the `SystemOrderTest` 15→16 + `SimChecksumCoverageGuardTest` v19 rename/re-pin/differential-teeth across ~20 files; `ScenarioApplierTests`/`ProceduralMapGeneratorTests` content-hash pins; 23 per-tick world goldens + `hero-start-state.golden.txt` re-recorded.

**Review:** one four-layer pass — 12 patches applied (6 medium: Neutral-winner freeze, preset-grace scoping, unresolved-target un-winnable, the unverified CanonicalModelHash preset-fold, untested applier→Configure wiring, one-condition determinism test broadened to six; 6 low: Configure self-containment, serializer canonicalization + round-trip test, latch-finality test, double-elimination test, preset round-trip param teeth). 2 deferred (DW-184 same-tick slot-recycle ABA; DW-185 >2-faction resolution → Story 7.12). 3 rejected. 0 intent_gap, 0 bad_spec — no loopback.

**Verification performed (independently re-run after patches):**
- `dotnet build godot/godot.sln` → 0 errors, 0 new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` → 2572 passed, 1 skipped (pre-existing), 0 failed (patch subagent); independently re-ran the WinConditions + VersionStamp + SimChecksumCoverage + SystemOrder + Golden filter → 226 passed, 0 failed.
- `SimChecksum.AlgoVersion`=19, `CanonicalModelHash.AlgoVersion`=12; new sim files Godot/float/wall-clock-free (grep clean); golden movement is exactly the base re-record (24 files) — the review patches moved no golden.

**Residual risks:**
1. **`ai-active-scenario.golden.txt` requires a one-time Windows re-record.** Its golden-match is Windows-only (`AiActiveGoldenTests.cs:66` early-returns on non-Windows) because `AiOpponentSystem` scores with cross-platform-divergent float (pre-existing D2 debt, excluded from the WSL gate). The `SimChecksum 18→19` bump changed its sequence but the golden was deliberately not re-recorded here (a Linux recording would be wrong for Windows), so the **Windows determinism-gate CI leg will fail until re-recorded on the ship-primary Windows machine**: `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~AiActive`, then `dotnet build`, then commit. This is the standard operational cost of any SimChecksum bump in this repo, not a code defect.
2. In-engine `godot-verify` (picker walkthrough + F5 playtest of each preset + the `OnVictory` escape hatch) was not run — the picker UI and verdict consumption are Godot-coupled (Tier-1-inexpressible), verified by construction + build only.
3. DW-184 (same-tick slot-recycle ABA on Assassination/Landmark) and DW-185 (>2-faction resolution) as logged.

`followup_review_recommended: true` — the review pass made 12 fixes across verdict-semantics correctness (match-freeze, un-winnable-match, grace scoping) and closed a previously-unverified multiplayer-safety fold; an independent confirmation (ideally including the in-engine picker/playtest pass) is warranted.

---

## Auto Run Result — Follow-up review pass (2026-07-18)

Status: done

**Summary:** Independent four-layer follow-up review of the full story diff (the pass the 2026-07-17 run recommended). No intent gap and no spec defect — the Intent Alignment audit confirmed the diff implements the one reading of the preset/DSL clause that the intent's own Block-If #2 parenthetical licenses, with every divergence disclosed. 13 patches applied (8 medium, 5 low), all hardening/closing holes adjacent to the first pass's fixes; 1 new deferral (DW-186); 7 rejects. The previously-skipped in-engine check was then partially executed live via godot-mcp.

**Files changed this pass (production):** `WinStateStore.cs` (LOST-latching `IsResolved`, `SoleLoserFaction()`, doc), `WinConditionSystem.cs` (grace-gated loss-by-absence branches, KotH unresolved-region fallback, landmark `PackRef`/`TryResolveRef` ABA defense, `ResetConfig()`, semantics docs), `Sim/SimulationHost.cs` (`ClearForReset` → `ResetConfig`), `ScenarioValidator.cs` (unknown-preset default reject; engine faction-ceiling checks), `WinConditionPhase.cs` (slot spinner 0–3; refresh resets stale params), `MainScene.cs` (no-victor defeat display), `Dsl/WinConditionPresets.cs` (genuine two-trigger TimedSurvival witness; 7.13-gap docs). **Tests:** 18 new across `WinConditionSystemTests` (7), `WinConditionApplierResolutionTests` (real-`StepOnce` KotH/Survival applier-path), `WinConditionSpecValidatorTests` (4), `WinConditionPresetsTests` (two-trigger round-trip + validator-clean theory), `SimResetTests` (WinState teeth).

**Review breakdown:** 0 intent_gap, 0 bad_spec, 13 patch (8 medium, 5 low), 1 defer (DW-186 — editor save-time validation surface, pre-existing platform class), 7 reject. No loopback; `review_loop_iteration` stayed 0 (patches only).

**Verification performed (independently re-run after patches):**
- `dotnet build godot/godot.sln` → 0 errors, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` → 2590 passed, 1 skipped (pre-existing), 0 failed (baseline 2572 + 18 new).
- `git status --porcelain` on `Golden/` → empty: **no golden moved** — the patches are determinism-neutral on all recorded scenarios by construction (no AlgoVersion change, no fold-shape change).
- Purity grep over the three sim files → clean (no Godot/float/wall-clock).
- **In-engine (godot-mcp, live editor 4.6.3):** six-option picker verified running — each toggle drives the real handler, inline params respond, the patched slot clamp (0–3) confirmed live in the built game; Timed Survival playtest to resolution — VICTORY overlay for Player 1 at exactly the authored tick 150, match-duration stat correct, tick counter latched. This closes the picker + verdict-consumption halves of prior residual risk #2.

**Residual risks (delta from the 2026-07-17 list):**
1. **Unchanged:** `ai-active-scenario.golden.txt` still needs its one-time Windows re-record (Windows determinism-gate leg red until then; recording command in the prior list).
2. **Narrowed:** of the in-engine checks, KotH/Assassination/Landmark playtests and the trigger-authored `OnVictory` escape-hatch walkthrough remain unrun in-engine (their sim logic is now covered by real-loop applier-path headless tests; the escape hatch is pinned by existing director tests). The picker walkthrough and one full preset playtest ARE now done.
3. DW-184 (EntityWorld same-tick slot-recycle ABA — the buildings half is now fixed via packed refs), DW-185 (>2-faction — Story 7.12), DW-186 (save-time validation surface) as logged.

`followup_review_recommended: true` — this pass again changed verdict-latch semantics (`IsResolved` now latches on LOST; grace re-scoped; KotH fallback; a presentation-contract addition in `ShowGameOver(0)`), 8 medium findings deep. Everything is latched by targeted tests and the suite + goldens are byte-stable, but by the same symmetry that made this pass worthwhile (it found the LOST-only hole one hop from pass 1's Neutral fix), an independent confirmation of the pass-2 semantics changes has nonzero expected value. The orchestrator's review budget owns termination.
