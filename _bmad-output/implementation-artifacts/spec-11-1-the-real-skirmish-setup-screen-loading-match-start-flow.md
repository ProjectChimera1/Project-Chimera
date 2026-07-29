---
title: 'Story 11.1 — The real skirmish setup screen + loading / match-start flow'
type: 'feature'
created: '2026-07-28'
status: 'done'
baseline_revision: 'ca5fa1c537f774a8090727569d1552c87b239b1e'
final_revision: 'a9a65d9abe705e9431b9cdec467a111b548e9955'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** Today a match boots from a hardcoded `MainScene.ScenarioPath` Inspector export and two hardcoded faction JSONs, decided at `_Ready` *before* the menu is even shown. "Play" just hides the menu overlay and flips a mode flag — there is no way for a player to pick a map, assign factions/teams to player slots, choose an AI opponent, or see any loading feedback; and boot failures crash mid-`_Ready` into a half-built scene instead of returning to a screen.

**Approach:** Add a real skirmish setup screen (a new `CanvasLayer` overlay reached from the menu's "Play") that lets the player choose a map from the shipped scenarios and configure each of that map's player slots (Human / AI+difficulty / Open / Closed, faction, team). On Launch, build a `ScenarioData` **in memory** from the map + the slot config, hand it to the existing `ScenarioLoadPhase.PendingGeneratedScenario` static + `ReloadCurrentScene()` handoff (the same path the AI map generator already uses), show a loading screen driven by a real per-phase progress seam on `ScenePhaseRunner`, then auto-enter Play. A boot exception fails safe back to the setup screen with the actual error instead of crashing. Route faction selection through `FactionJson` file paths so the existing `ResolveSlotFactionDefs` resolves abilities/tags (structurally closing DW-121).

## Boundaries & Constraints

**Always:**
- Presentation/data only — **zero new sim writes and zero `SimChecksum` change**. All match state still flows through the existing `ScenarioApplier`/`ResolveSlotFactionDefs`/`AllianceSeeder` pipeline unchanged. The in-memory `ScenarioData` is applied through the identical fail-closed path a disk scenario uses.
- The Godot-free core (`src/Core/Skirmish/**`) must contain **no `using Godot;`** — it is auto-globbed into the Tier-1 test compile by `SimSources.props` and must stay Tier-1-testable.
- Faction selection is committed as a `ScenarioPlayerSlot.FactionJson` **res:// path**, never as an in-memory `FactionDefinition` handed straight to a slot — so the existing `ResolveSlotFactionDefs` performs `ResolveAbilities` + `UnitTagValidator.ValidateAndDropUnits` (DW-121 closed by construction).
- The setup validator returns **all** located errors (not first-fail), mirroring `UnitDefinitionValidator`, and Launch is blocked while any error stands, each with an actionable message.
- The static handoff fields are consumed exactly once (read-then-clear), mirroring `PendingGeneratedScenario`/`PendingReplayPath`.
- Only the AI/player capability the runtime actually has today may be launchable — the setup screen must not advertise or launch a configuration the sim cannot pilot (Epic 11.7 honesty principle).

**Block If:**
- Delivering the story would require folding any new field into `SimChecksum` or `MatchAgreementHash`, or otherwise changing determinism (e.g. if per-slot color/AI *must* become sim-visible). HALT `blocked`, condition `skirmish setup requires a determinism fold`.
- The existing `PendingGeneratedScenario` + `ReloadCurrentScene` handoff cannot carry an in-memory skirmish scenario into `_Ready` without a broader boot refactor. HALT `blocked`, condition `no viable in-memory scenario handoff`.

**Never:**
- Multiple simultaneous AI opponents / AI in any slot / per-slot AI *behavior* wiring — that is Story 10-10 (multi-instance AI) / 10-11 (AI lockstep-legal). This story pilots the single existing `AiOpponentSystem` (one AI opponent).
- A user color **picker** or a per-slot color data channel — color stays a deterministic per-slot-index palette (extend the existing hardcoded 2 to 4). No `ScenarioPlayerSlot` color field.
- Live per-map minimap **thumbnail** rendering (the renderer needs a populated `World3D`, not a `ScenarioData`) — map list shows textual properties only.
- Mod.io / subscribed-map enumeration (live content deferred to Epic 10); map list = shipped `res://resources/data/scenarios/*.json` only.
- Editing the phase order, or making `ScenePhaseRunner` asynchronous / cross-frame. The progress seam is a callback around the existing synchronous `foreach`; smooth per-phase animation is a non-goal.
- Multiplayer setup/lobby (that is `LobbyUi`, already built) — this is the **offline** skirmish path only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid 1v1 launch | Map with ≥2 start slots; slot0=Human(faction A, team 0), slot1=AI Normal(faction B, team 1) | `SkirmishSetupValidator` returns empty; `SkirmishSetupToScenario` emits a `ScenarioData` cloned from the base map with `PlayerSlots[0..1]` carrying chosen `FactionJson`/`Team`/base `BaseX/Z`; Launch enabled | No error |
| No human slot | All slots AI/Open | Validator returns error "Exactly one Human slot is required"; Launch disabled | Blocked with message |
| No opponent | Only a Human slot active | Validator error "At least one AI opponent is required"; Launch disabled | Blocked with message |
| >1 AI slot | Human + 2 AI slots active | Validator error "Only one AI opponent is supported (Story 10-10 adds more)"; Launch disabled | Blocked, honest message |
| Active slots exceed map | 3 active slots on a 2-start map | Validator error "This map supports N start positions"; Launch disabled | Blocked with message |
| Unknown faction id | Slot faction id not in the discovered catalog | Validator error "Unknown faction: <id>"; Launch disabled | Blocked with message |
| Discovered faction assigned | Chosen faction routed as `FactionJson` path | Existing `ResolveSlotFactionDefs` resolves abilities + drops unknown-tag units before spawn (DW-121) | N/A — reuse |
| Boot failure after Launch | Applied in-memory scenario throws during a phase (e.g. malformed faction file) | Boot is wrapped; on exception, clear pending statics, return to menu, re-open the setup screen pre-filled from the retained setup, surface the located error as a toast | Fail-safe, no half-built crash |
| Empty scenarios dir | No `*.json` under scenarios dir | Catalog returns empty; setup screen shows "No maps found"; Launch disabled | Graceful empty state |
| Progress seam | `ScenePhaseRunner.Run()` with a progress callback | Callback invoked once per phase, in canonical order, before each `phase.Run()`, with `(index, ScenePhaseOrder.Canonical.Length, name)` | N/A |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs` -- `ScenarioData` (:634) + `ScenarioPlayerSlot` (:153, fields: `Slot`,`FactionJson`,`StartOre`,`StartCrystal`,`Team`,`BaseX`,`BaseZ`); `MapBounds`,`SuggestedPlayers`,`DisplayName`,`Author`. Base for the in-memory clone.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `LoadSelectableFromDirectory` (:286, scans `*_faction.json`, `ValidateComplete`-gated, ordinal-sorted by `Id`; never throws). Need id→res://path alongside the def (DW-121 context).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- `PendingGeneratedScenario` static (:38, set→ReloadCurrentScene→consumed-and-cleared :53-85, bypasses disk load), `ResolveSlotFactionDefs` (:345, resolves abilities/tags per non-empty `FactionJson`). The reuse target.
- `godot/src/Core/MainScene.cs` -- `_Ready` (:243) boot; `ScenarioPath` export (:158); `AiLevel` export (:152); `LoadGeneratedScenario` (:1893, sets pending + `ReloadCurrentScene`); phase-array assembly (:458-503) + `new ScenePhaseRunner(phases).Run()` (:503). Entry point for the handoff + loading screen + fail-safe.
- `godot/src/Core/Bootstrap/ScenePhaseRunner.cs` -- `Run()` (:24) synchronous `foreach`. Add the progress seam here. Tier-1-testable (globbed in).
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs` -- `Canonical` (:21), the phase count/order (pinned by `PhaseOrderTest`).
- `godot/src/Core/Bootstrap/Phases/MainMenuPhase.cs` -- `OnPlaySkirmish` wiring (:25-31, currently `HeroPicker.RequestSkirmishLaunch`). Repoint "Play" to open the setup overlay.
- `godot/src/UI/MainMenuOverlay.cs` -- the menu; `OnPlaySkirmish` event (:110-113).
- `godot/src/UI/HeroPickerOverlay.cs` / `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- construction pattern to mirror; `Launch()` (:61-86) = the existing enter-Play authority to reuse after boot.
- `godot/src/UI/LobbyUi.cs` -- N-slot faction/team grid construction pattern to mirror for the setup overlay.
- `godot/src/Core/Bootstrap/Phases/FactionVisualsPhase.cs` -- hardcoded `p1Color`/`p2Color` (:30-31). Extend to a 4-entry per-slot-index palette.
- `godot/src/AI/AiOpponentSystem.cs` -- `AiDifficulty { Easy, Normal, Hard }` (:9); single AI opponent (piloted Player2).
- `godot/SimSources.props` -- globs `src/Core/**` into Tier-1 (:22); `Bootstrap/Phases/**` and `MainScene.cs` removed (:90,:96). New `src/Core/Skirmish/**` auto-included.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDiscoveryTests.cs` / `Bootstrap/PhaseOrderTest.cs` -- test-style anchors for the new Tier-1 tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Skirmish/SkirmishSetup.cs` -- NEW, Godot-free. Define `enum SlotKind { Open, Closed, Human, Ai }`, `SetupSlot { int Slot; SlotKind Kind; AiDifficulty Ai; string? FactionId; int Team; }`, and `SkirmishSetup { string MapId; List<SetupSlot> Slots; }`. Pure data. -- the config model shared by UI + transform + validator.
- `godot/src/Core/Skirmish/SkirmishCatalog.cs` -- NEW, Godot-free. `IReadOnlyList<MapEntry> ScanMaps(string absScenariosDir)` (loads each `*.json` via `ScenarioSerializer.LoadFromFile`, lenient; `MapEntry { Id, DisplayName, ResPath, MapBounds, SuggestedPlayers, StartPositionCount, Author }`) and `IReadOnlyList<FactionEntry> ScanFactions(string absFactionsDir)` (`FactionEntry { Id, DisplayName, ResPath }`, `ValidateComplete`-gated, ordinal-sorted). Use `System.IO`; take absolute paths so it is Tier-1-testable with temp dirs. -- enumerate selectable maps + factions with their res:// paths (the FactionJson source, DW-121-safe).
- `godot/src/Core/Skirmish/SkirmishSetupValidator.cs` -- NEW, Godot-free. `IReadOnlyList<string> Validate(SkirmishSetup setup, MapEntry map, IReadOnlyList<FactionEntry> factions)` returning ALL errors: exactly one Human slot; ≥1 Ai slot; ≤1 Ai slot (honest runtime limit); 2 ≤ active(Human+Ai) count ≤ `map.StartPositionCount`; every Human/Ai slot's `FactionId` resolves in `factions`; teams within `[0, activeCount]`; **and every pre-placed starting unit for that slot's paired base position must remap into the chosen faction's roster** (in-engine gate PATCH — see `SkirmishRosterMap` below; without it a faction that cannot field the map's starting army launches and is silently crippled). -- gate Launch with actionable messages.
- `godot/src/Core/Skirmish/SkirmishRosterMap.cs` -- NEW, Godot-free. `MapUnitId(unitId, authored, target)` translating a map's pre-placed unit id into the roster of the faction a slot actually CHOSE, keyed by role = (`Category`, ordinal-within-category) resolved against the authored faction's roster; identity when authored==target (same `ResPath`), clamp to the last unit of a category when the target roster is shallower, `null` when the target has no unit of that category. **Required:** shipped maps author their starting army against ONE faction's ids (`alpha_map_01` places alpha's `"worker"` for both slots) and the factions have DISJOINT rosters, so without this remap a cross-faction launch resolves to no `UnitDefinition` and the applier's `def == null` skip drops that player's whole starting army silently. -- the cross-faction role translation.
- `godot/src/Core/Skirmish/SkirmishSetupToScenario.cs` -- NEW, Godot-free. `ScenarioData Build(SkirmishSetup setup, ScenarioData baseMap, IReadOnlyList<FactionEntry> factions)`: clone `baseMap`; rebuild `PlayerSlots` so each active (Human/Ai) `SetupSlot` maps to a `ScenarioPlayerSlot` carrying `Slot`, `FactionJson` = the chosen faction's res:// path, `Team`, and the base map's `BaseX/BaseZ/StartOre/StartCrystal` for that slot index; drop Open/Closed slots; leave terrain/win-condition untouched. **Also re-key the pre-placed `Buildings`/`Units`: keep only entities whose original slot is a paired base slot (remapped to its new contiguous owner index) and DROP the rest** — else a map with more start positions than active players (the shipped 4-start `quad_map_01` launched 1v1) leaves entities keyed to dropped slots that the applier's unguarded buildings loop spawns as ghost Player3/Player4 bases (follow-up-review PATCH); new arrays + copied elements so `baseMap` (whose arrays `ShallowClone` shares) is never mutated. Deterministic (same input → identical output). -- the pure transform that is the testable heart.
- `godot/src/Core/Bootstrap/ScenePhaseRunner.cs` -- EDIT. Add an optional ctor param or `Run(Action<int,int,string>? onPhaseStarting = null)` invoked immediately before each `phase.Run()` with `(1-based index, ScenePhaseOrder.Canonical.Length, phase.Name)`, after `AssertOrder()`. No behavior change when null. -- the real per-phase progress seam.
- `godot/src/UI/SkirmishSetupOverlay.cs` -- NEW, Godot-coupled `CanvasLayer` (mirror `LobbyUi`/`HeroPickerOverlay` construction from the theme kit). Left: map list from `SkirmishCatalog.ScanMaps` with properties (name, `MapBounds`, `SuggestedPlayers`, start-position count, author). Right: per-slot grid for the selected map (Kind option, AI-difficulty option when Ai, faction `OptionButton` from `ScanFactions`, team spinner) showing the deterministic slot color. Live-run `SkirmishSetupValidator`, disabling Launch and listing errors while any stand. Back returns to menu. Launch → task below. -- the screen itself.
- `godot/src/UI/LoadingScreenOverlay.cs` -- NEW, Godot-coupled `CanvasLayer` (topmost). Shows map name + a "Loading… <phase> (i/N)" line updated from the progress seam; freed when Play begins. -- loading feedback.
- `godot/src/Core/Bootstrap/Phases/MainMenuPhase.cs` -- EDIT. Repoint `OnPlaySkirmish` to construct/show `SkirmishSetupOverlay` (hiding the menu) instead of calling `HeroPicker.RequestSkirmishLaunch` directly. -- wire "Play" → setup screen.
- `godot/src/Core/MainScene.cs` -- EDIT. Add consumed-once statics `PendingSkirmishStart` (bool) and `PendingSkirmishAiLevel` (`AiDifficulty?`). Setup overlay's Launch: `ScenarioLoadPhase.PendingGeneratedScenario = SkirmishSetupToScenario.Build(...)`, set the two statics, `GetTree().ReloadCurrentScene()`. In `_Ready`: if `PendingSkirmishStart`, read-and-clear the statics, override `AiLevel` from `PendingSkirmishAiLevel`, add `LoadingScreenOverlay` before the runner, pass its update callback as the runner's progress seam, and **wrap the phase run in try/catch** — on success auto-enter Play via the existing `HeroPickerPhase.Launch`/`GameState` authority and free the loading overlay; on exception clear pending state, return to menu, re-open `SkirmishSetupOverlay` from the retained setup, and toast the located error. -- the handoff, loading wiring, auto-launch, and fail-safe.
- `godot/src/Core/Bootstrap/Phases/FactionVisualsPhase.cs` -- EDIT. Replace the two hardcoded `p1Color`/`p2Color` with a 4-entry per-slot-index palette (index 0/1 keep today's blue/red so goldens/visual continuity hold), indexed by faction slot. -- honest per-slot color without a data channel.
- `godot/ProjectChimera.Sim.Tests/Skirmish/SkirmishSetupTests.cs` -- NEW. xUnit tests for: `SkirmishSetupValidator` (each rule + all-errors aggregation via the I/O matrix rows), `SkirmishSetupToScenario` (correct `PlayerSlots` for a 1v1 base map, Open/Closed dropped, determinism, no Player1 assumption), `SkirmishCatalog` (temp-dir scan of maps + factions incl. empty dir), `ScenePhaseRunner` progress seam (fires once per phase, canonical order, count == `ScenePhaseOrder.Canonical.Length`), and DW-121 (a discovered faction routed as a `FactionJson` path passes through `ResolveSlotFactionDefs`-style resolution). -- the Tier-1 verification of the whole Godot-free core.

**Acceptance Criteria:**
- Given the menu, when the player clicks "Play", then the skirmish setup screen opens (menu hidden) listing the shipped maps with their properties — not a direct launch into the hardcoded scenario.
- Given a selected map and a valid 1-human-1-AI slot config, when the player clicks Launch, then an in-memory `ScenarioData` built from the map + config is applied through the existing pipeline, a loading screen appears, and the match enters Play with the chosen factions/teams spawned at the map's start positions.
- Given any invalid config (no human, no opponent, >1 AI, too many active slots, unknown faction), when it stands, then Launch is disabled and every located error is shown with an actionable message.
- Given a chosen discovered faction, when the slot is spawned, then its abilities are resolved and unknown-tag units dropped (via the `FactionJson`→`ResolveSlotFactionDefs` route), never spawning with unresolved ability indices (DW-121).
- Given a boot exception after Launch, when a phase throws, then the run does not leave a half-built scene — it returns to the setup screen pre-filled from the retained config with the located error surfaced.
- Given `ScenePhaseRunner.Run` with a progress callback, when it runs, then the callback fires exactly once per phase in canonical order before each phase executes, and with no callback the behavior is byte-identical to before.
- Given the full Tier-1 suite, when it runs, then all new tests pass and no `SimChecksum` golden is re-baselined.

## Spec Change Log

<!-- No bad_spec loopback occurred; all review findings were localized patches. -->

## Review Triage Log

### 2026-07-28 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 8: (high 1, medium 3, low 4)
- defer: 2
- reject: 5
- addressed_findings:
  - `[high]` `[patch]` Non-contiguous active slots (slot0=Open, Human=slot1, AI=slot2 on a 3–4-start map) mis-aligned the built `PlayerSlots` ordinals with the `FactionRegistry` active span + `ResolveSlotFactionDefs` per-ordinal writes, and `activePlayers` was derived from the stale on-disk `ScenarioPath` — fixed: `SkirmishSetupToScenario.Build` now renumbers active slots to contiguous `0..k-1` with position-paired base positions, and `MainScene._Ready` derives `activePlayers` from the in-memory `PendingGeneratedScenario`.
  - `[medium]` `[patch]` Validator allowed the human + sole AI to share a positive team (allied → no real opponent) — added an all-allied-set rule (Team 0 = per-slot FFA side; positive teams collapse per ordinal; `<2` distinct sides blocks launch).
  - `[medium]` `[patch]` Loading overlay never painted (no frame boundary in synchronous `_Ready`) — the skirmish path now yields exactly one `ProcessFrame` before the phase run so the overlay presents; non-skirmish boot stays synchronous/identical.
  - `[medium]` `[patch]` Fail-safe wrapped only the phase runner — extended it (extracted `FailSafeSkirmishBoot`) to also cover the post-run skirmish tail (prop renderer, hero-picker launch) so a late throw still fails safe.
  - `[low]` `[patch]` `SkirmishCatalog.ScanMaps` now dedupes by map Id (first ordinal filename wins), mirroring `ScanFactions`.
  - `[low]` `[patch]` Team `SpinBox` `MaxValue` now clamps to the active-slot count on `Revalidate`, so the UI can't offer a team ordinal the validator rejects.
  - `[low]` `[patch]` De-duplicated the slot-color palette — `FactionVisualsPhase.SlotColors` is the single source; `SkirmishSetupOverlay` references it.
  - `[low]` `[patch]` Added the missing Tier-1 tests: validator team-range (in/out/boundary), empty/null-faction branch, allied-opponent rule + passing configs, contiguous-renumber transform, `ScanMaps` malformed-json-skip, `ScanMaps` + `ScanFactions` dedupe-by-Id.

### 2026-07-28 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 1, medium 0, low 3)
- defer: 1
- reject: 11
- addressed_findings:
  - `[high]` `[patch]` NullReferenceException on **every** skirmish launch: PATCH-3 made `_Ready` `async void` and yields one `ProcessFrame` BEFORE the phase runner, but `_ctx.GameState` (set only by `GameStatePhase`, which runs *inside* the runner) is still null during that frame, and `_Process`/`_Input`/`_UnhandledInput` guard only `_headless || _bootAborted` — so the resumed per-frame callbacks dereference `_ctx.GameState.Mode` and crash. Fixed: added `_bootPending` (true from construction, cleared after the phase run + post-run tail completes), added to all three callback guards. Synchronous/editor boot is byte-identical (no frame elapses inside `_Ready`, so the flag is never observed).
  - `[low]` `[patch]` `SkirmishCatalog.ScanMaps` globbed every `*.json`, listing a non-map scenario fragment (0 start positions) as a phantom, permanently-unlaunchable map — now skips entries with no `PlayerSlots` (mirrors `ScanFactions`'s discovery-contract drop); locked in by `ScanMaps_SkipsMapWithNoStartPositions`.
  - `[low]` `[patch]` Verification gap: `SkirmishCatalog.ScanFactions`'s lenient malformed-JSON skip (`catch { continue; }`) was exercised by no test, unlike its `ScanMaps` twin — added `ScanFactions_SkipsMalformedJson_NoThrow`.
  - `[low]` `[patch]` Verification gap: `ScanMaps → MapEntry.SuggestedPlayers` was asserted by nothing (the fixture never set a non-default value) — extended the `MapData` helper + `ScanMaps_ReadsProperties_OrderedById` to set and assert it.

### 2026-07-28 — Review pass (follow-up 2)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 2, medium 0, low 1)
- defer: 1
- reject: 8
- addressed_findings:
  - `[high]` `[patch]` Team-color inversion regression introduced by the prior pass's PATCH 7 palette de-dup: `FactionVisualsPhase.SlotColor(Faction)` indexed the **1-based** `Faction` ordinal (Neutral=0, Player1=1, Player2=2) into the **0-based** palette, so Player 1 rendered **red** and Player 2 **green** — breaking the story's own "index 0/1 keep today's blue/red verbatim" continuity invariant on *every* match. Fixed to `SlotColorAt((int)faction - 1)` (Player1→blue, Player2→red, Player3→green, Player4→gold).
  - `[high]` `[patch]` `SkirmishSetupToScenario.Build` human/AI control swap: active slots were renumbered by raw `Slot` only, so a Human placed in a higher setup slot than the AI landed on contiguous index 1 (offline `Player2` = AI-piloted) while the AI's config took index 0 (`Player1` = human-controlled) — the player silently controlled the faction/team they'd marked for the AI, and vice-versa (reachable via the freely-settable per-row Kind). Fixed by ordering active slots **Human-first, then by Slot**; added `Build_HumanSortsToContiguousIndex0_EvenWhenAiInLowerSlot` regression test.
  - `[low]` `[patch]` Verification gap: the transform test asserted only `StartOre`/`BaseX` of the four per-slot fields `Build` copies — added `StartCrystal`/`BaseZ` assertions (the fixture already seeds distinct values) so a drop of either can't stay green.

### 2026-07-28 — Review pass (follow-up 3)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 2
- reject: 11
- addressed_findings:
  - `[medium]` `[patch]` Orphaned pre-placed entities on a >2-start map launched 1v1: `SkirmishSetupToScenario.Build` rebuilt only `PlayerSlots` and left the `ShallowClone`'d `Buildings`/`Units` keyed to the base map's ORIGINAL slot ordinals. Launching the shipped 4-start `quad_map_01` ("Quad Standoff", `DestroyAllBuildings`) as the honest 1v1 (validator caps active slots at 2) left slot-2/3 buildings, which the applier's buildings loop (`ScenarioApplier.cs:238`, unguarded `(Faction)(b.Slot+1)`, and — unlike the units loop at `:271` — it still places a building for a faction with no resolved def, so `FACTION_ARRAY_SIZE=9` means `InFactionRange` never trips) spawns as **ghost Player3/Player4 pre-built bases** → un-ownable enemy structures and, under `DestroyAllBuildings`, an unwinnable match (no crash — `BuildingStore.Create` stores the faction as a value and `GetFactionDef` is bounds-safe). Fixed at the source: `Build` now re-keys kept `Buildings`/`Units` to the paired active slot's new contiguous index and DROPS entities for unpaired slots, with copied elements so `baseMap` is never mutated. Added `Build_DropsAndRemaps_PrePlacedEntities_ForDroppedSlots`, `Build_KeepsAllEntities_When2SlotMapLaunched1v1` (identity for the common 2-slot case), and `Build_DoesNotMutate_BaseMapEntities`. Full Tier-1 suite 3565/1skip/0 fail.

## Design Notes

**Why in-memory `ScenarioData` handoff, not a re-apply path:** `ScenarioLoadPhase.PendingGeneratedScenario` + `ReloadCurrentScene()` already exists and is battle-tested by the AI map generator (`MainScene.LoadGeneratedScenario`). Reusing it means the skirmish scenario flows through the *identical* fail-closed apply + faction-resolution + alliance-seed pipeline — zero new sim code, zero determinism risk. Building a second post-selection re-apply path would duplicate that pipeline.

**Runtime-capability boundary (honesty):** the sim today pilots exactly one AI opponent (single global `AiLevel`, `AiOpponentSystem` on Player2), color is per-slot-index (not data), and `ScenarioPlayerSlot` has no color/AI/is-human field. So this story ships the setup *screen*, the *flow*, team assignment (real, via `AllianceSeeder`), faction-per-slot (real, via `FactionJson`), and a single launchable AI opponent. Richer configurations the runtime cannot yet honor are **blocked at validation**, not silently shipped broken. Deferred pieces get DW entries (multi-AI → 10-10; color picker + `ScenarioPlayerSlot` color; per-map minimap thumbnail; mod.io/subscribed maps).

**DW-121 closed by construction:** because faction choices are committed as `FactionJson` *paths* (never in-memory `FactionDefinition` objects assigned to `SlotFactionDefs`), the existing `ResolveSlotFactionDefs` performs `ResolveAbilities` + `ValidateAndDropUnits` on every slot — the exact resolution DW-121 warned would be skipped. The spec forbids the raw-def-to-slot shortcut, and a Tier-1 test asserts the path route resolves.

**Loading screen honesty:** the runner stays synchronous (touching the order-asserted spine is out of scope), so the progress seam is a real per-phase callback but smooth cross-frame animation is a non-goal; the overlay is visible during boot and reflects real phase names/counts.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including the new `Skirmish/SkirmishSetupTests` and the unchanged `PhaseOrderTest`; no golden re-baseline.
- `dotnet build godot/godot.csproj` -- expected: the full Godot-coupled project compiles (overlays, phase edits) with no banned-API/AOT analyzer regressions.

**Manual checks (in-engine, gated — cannot run headless here):**
- Launch the editor, click Play → the skirmish setup screen appears; pick a map, set slot1 to AI, pick factions/teams, Launch → loading screen shows, match starts with both rosters at the map's start positions; Esc/return works. This is the Epic-11 per-story in-engine gate (run via `/godot-verify` or the godot-mcp bridge with no idle session holding it).

### In-Engine Gate Result — /godot-verify, 2026-07-28

**Verdict: FAIL → FIXED + RE-VERIFIED PASS** (see "Gate Fix" below). Godot 4.6.3-stable-mono,
driven over the godot-mcp bridge (Button `pressed` signal emission + tree-walk digests; no
screenshots relied on for state). The FAIL findings below are kept verbatim as the record of what
the gate caught.

| AC | Result | Evidence |
|----|--------|----------|
| 1. Play → setup screen, menu hidden, maps listed | PASS | `MainMenuOverlay` content layer `visible=false`, setup content layer `visible=true` after `pressed`; 11 maps listed; selected map shows "Start positions: 2 / Suggested players: unspecified / Map bounds: 120 / Author: —" |
| 2. Valid 1v1 → in-memory scenario, loading screen, enters Play with chosen factions/teams | **FAIL** | Loading screen + Play reached (watcher captured "Alpha Skirmish" / "Loading… Onboarding (40/40)"; HUD `[PLAY] Tick 0`). But the **chosen faction's starting units do not spawn** — see defect below |
| 3. Invalid config → Launch disabled, all errors listed | PASS | Slot2→Open: `Launch.disabled=true`, "• At least one AI opponent is required."; both-AI: two errors listed simultaneously (aggregation, not first-fail) |
| 4. Chosen faction resolves abilities / drops unknown-tag units (DW-121) | CAN'T VERIFY | Both shipped factions declare **no tags at all**, so the tag path is never exercised by shipped content; and the cross-faction side spawns nothing to inspect |
| 5. Boot exception → fail-safe back to setup screen | NOT TESTED | Requires injecting a throwing phase; not attempted this pass |
| 6. Progress callback fires once per phase, canonical order | PASS | Tier-1 `ScenePhaseRunner` seam tests pass; live loading text showed a real per-phase counter reaching `(40/40)` |
| 7. Tier-1 green, no golden re-baselined | PASS | 3565 passed / 0 failed / 1 pre-existing skip; `git diff ca5fa1c..HEAD` touches **no** golden or checksum file |

**[HIGH] Cross-faction launch silently deletes the non-authored player's starting units.**

Measured at `Tick 0` in `[PLAY]`, post-reload (scene-instance-change–gated watcher, so the boot
scene is not confounded with the launched scene):

| Launch config | P1 units | P2 units | Total |
|---|---|---|---|
| `alpha_map_01` as authored on disk | 3 | 2 | 5 |
| Skirmish, both slots = Crucible Covenant (`alpha`) | 2 | 2 | 4 |
| Skirmish, slot2 = **Sanguine Court** (`beta`) | 2 | **0** | 2 |

Root cause: every shipped scenario hardcodes alpha-faction unit ids in its pre-placed `units`
(`alpha_map_01` places `unit_id: "worker"` for **both** slots), but the two factions have
**disjoint rosters** — `alpha` = `worker, infantry, …, mage`; `beta` = `forgehand, footsoldier,
…, wyvern`. When the setup screen assigns `beta` to a slot, `"worker"` does not resolve in that
faction, and per `ScenarioLoadPhase.cs:362` "a dropped unit → GetUnit null → the applier's
def==null skip → no EntityWorld slot" — the units vanish with no error. The AI opponent starts
with **zero workers**, no income, and never recovers (observed at tick 1629: P2 30 ore / 1 unit
vs P1 540 ore).

This is a Story 11.1 regression by construction: the story introduced faction *choice*, and
before it factions always came from the scenario file and therefore always matched the
pre-placed unit ids. It also violates this spec's own stated boundary — "the setup screen must
not advertise or launch a configuration the sim cannot pilot (Epic 11.7 honesty principle)":
Sanguine Court is offered, launches without a single validator error, and silently cripples
that player. `SkirmishSetupValidator` has no rule covering it.

Suggested fix direction (not applied): either resolve pre-placed `units` per-slot through the
chosen faction's roster (map a generic role → the faction's equivalent id), or have
`SkirmishSetupToScenario.Build` re-key pre-placed unit ids to the assigned faction, or — the
cheap honest floor — add a validator rule blocking a faction whose roster cannot satisfy the
map's pre-placed unit ids.

**[UNATTRIBUTED] P1 spawns 2 units where the scenario authors 3, even same-faction.**
Reproduced on the both-alpha control above (4 total vs the file's 5). NOT confirmed as
11.1-caused: the pre-launch boot HUD reads `[EDIT]` and carries a "Placing: P1 [Covenant
Transmuter]" placement ghost, so its `P1: 3` may count a ghost rather than a real entity, making
the EDIT-vs-PLAY comparison unsafe. Needs an isolated check of `SkirmishSetupToScenario.Build`
output vs the applier before it can be assigned to this story or to pre-existing behavior.
→ Logged to `deferred-work.md` (11.1 source_spec section). Ruled OUT of the transform by
`Build_SameFactionLaunch_LeavesPrePlacedUnitIdsUntouched`: all 3 slot-0 units are emitted into the
built scenario, so the loss is downstream of this story's code.

### Gate Fix — cross-faction role remap (2026-07-28)

**Approach.** The role skeleton is real data, not a hardcoded table: both shipped factions declare
the same `category` sequence position-for-position (`Worker, Melee, Melee, Melee, Ranged, Ranged,
Siege, Air`), which the FMA redesign preserved. So a unit's ROLE is (`Category`,
ordinal-within-category) resolved against the faction the map was authored against — alpha's `mage`
is (Ranged, 1), which is beta's `rune_caster`. New `SkirmishRosterMap` performs that translation;
`SkirmishCatalog` now carries the data it needs (`FactionEntry.Units` roster; `MapEntry`
`SlotFactionResPaths` + `PrePlacedUnits`, both normalized to start POSITION so the validator and
the transform pair slots identically). A community faction that follows the skeleton maps for free.

**Degradation, deliberately not fail-open.** `FactionValidator.ValidateComplete` — the selectability
gate — only guarantees a Worker plus one combat unit, NOT a full skeleton. A target faction with
fewer units in a category clamps to the last one of that category (a shallower roster shouldn't cost
a player their starting army); a target with NO unit of that category is unmappable, and
`SkirmishSetupValidator` rule 8 blocks Launch with an actionable message rather than letting the
applier drop it silently. Same-faction launches take the identity path, so the common case stays
byte-identical to the base map.

**Files changed:**
- `godot/src/Core/Skirmish/SkirmishRosterMap.cs` — NEW, the role translation.
- `godot/src/Core/Skirmish/SkirmishCatalog.cs` — `FactionEntry.Units`, `FactionUnitEntry`;
  `MapEntry.SlotFactionResPaths` / `.PrePlacedUnits` + `MapPrePlacedUnit`; populated in both scans.
- `godot/src/Core/Skirmish/SkirmishSetupToScenario.cs` — pre-placed units re-keyed through the remap
  (buildings need none: `ScenarioBuilding.Type` is a shared `BuildingType` token, not a faction id).
- `godot/src/Core/Skirmish/SkirmishSetupValidator.cs` — rule 8, the unmappable-role block.
- `godot/ProjectChimera.Sim.Tests/Skirmish/SkirmishSetupTests.cs` — 8 new tests; fixtures now carry
  real rosters and author `FactionJson` against alpha, because the production catalog always does.

**Re-verification (same bridge, same launch that failed):**

| Measurement | Before fix | After fix |
|---|---|---|
| Cross-faction 1v1 at `Tick 0/1 [PLAY]` | `P1: 2, P2: 0, Total: 2` | **`P1: 2, P2: 2, Total: 4`** |
| Same-faction control | `P1: 2, P2: 2, Total: 4` | `P1: 2, P2: 2, Total: 4` (unchanged) |
| AI economy under way (tick 344) | P2 30 ore / starved | **P2 110 ore, 2 supply, building** |
| Launch gating | launched silently broken | no false block — `Launch.disabled=false`, 0 errors |

Cross-faction now matches the same-faction control exactly. Tier-1: **3573 passed / 0 failed / 1
pre-existing skip** (3566 + 8 new), two independent clean runs; no golden re-baselined. AC2 PASS.

**AC4 note:** still not positively exercisable by shipped content — both factions declare no tags —
but the DW-121 `FactionJson`→`ResolveSlotFactionDefs` route is now genuinely exercised for BOTH
players on a cross-faction launch, where before the beta side spawned nothing at all.

### In-Engine Gate - 2026-07-28
- surface: main menu Play -> skirmish setup screen -> Launch -> loading screen -> match start, cross-faction (Crucible Covenant vs Sanguine Court) on Alpha Skirmish
- launched: godot_editor_edit run, then Play and Launch driven over the godot-mcp bridge via emitted Button `pressed` / OptionButton `item_selected` signals; counts read from a holder-attached watcher gated on MainScene instance change so the post-reload scene is measured, not the boot scene
- digest: `FPS 428   [PLAY]   Tick 1   Hash -` / `P1: 2 units   P2: 2 units   Total: 4` / `Selected: -` (cross-faction, post-fix); same-faction control identical; pre-fix the same launch read `P1: 2 units   P2: 0 units   Total: 2`
- asserted: alpha_map_01 authors 2 pre-placed units for start slot 1; the Sanguine Court slot spawned 0 of them before the fix and 2 after, matching the same-faction control exactly, and its economy ran (110 ore / 2 supply / building at tick 344 vs 30 ore / starved before)
- caveat: start slot 0 shows 2 units against 3 authored, identically in BOTH the cross-faction and same-faction launches, so it is not a faction-remap defect; proven outside this story's transform by `Build_SameFactionLaunch_LeavesPrePlacedUnitIdsUntouched` and logged to deferred-work.md for an applier-level investigation
- result: PASS


## Auto Run Result — follow-up review 3 (2026-07-28)

**Summary:** Follow-up adversarial review pass on the `done` Story 11.1. Four review lenses (adversarial, edge-case, verification-gap, intent-alignment) ran in parallel against the full diff since baseline `ca5fa1c`. One this-story defect confirmed and patched; two findings deferred; all others rejected as out-of-intent-scope, unreachable, or by-design.

**Files changed this pass:**
- `godot/src/Core/Skirmish/SkirmishSetupToScenario.cs` — `Build` now drops/re-keys the base map's pre-placed `Buildings`/`Units` to the active renumbered slot set (fixes ghost Player3/Player4 bases when a >2-start map is launched 1v1).
- `godot/ProjectChimera.Sim.Tests/Skirmish/SkirmishSetupTests.cs` — added `Build_DropsAndRemaps_PrePlacedEntities_ForDroppedSlots`, `Build_KeepsAllEntities_When2SlotMapLaunched1v1`, `Build_DoesNotMutate_BaseMapEntities` (+ `BaseMapWithEntities` fixture helper).
- Spec: corrected the `SkirmishSetupToScenario.cs` task line to require the entity drop/re-key (prevents a future re-derivation re-introducing the bug).

**Review findings breakdown:**
- **Patched (1, medium):** orphaned pre-placed buildings/units on a >2-start map launched 1v1 → ghost enemy bases / unwinnable `DestroyAllBuildings`.
- **Deferred (2):** (a) dev scratch maps (`123.json`, `my-new-map.json`) surface as selectable maps — content hygiene, pre-existing, per-intent code; (b) the in-match team-color slot→palette mapping (regressed twice) has no automated regression test — needs a Godot-free palette seam. Both appended to `deferred-work.md` as new entries.
- **Rejected (11):** human-first reorder position/ownership (by design — human=Player1 offline); loading progress renders no per-phase repaint (intent makes smooth animation a non-goal); pre-phase fail-safe gap (intent scopes fail-safe to phase throws; the specified malformed-faction failure is inside the try); boot-error label clobbers validation on reopen (unreachable — only a valid config launches); validator slot-number vs renumbered scenario mismatch (user sees UI ordinals); prefill team-clamp on reopen (unreachable — fails validation); phantom `FactionId` on Open/Closed (harmless); TOCTOU stale `StartPositionCount`/origin-spawn default (exotic, guarded by defaults); fail-safe reopen with vanished map drops config (exotic); empty-faction-catalog generic message (minor UX, validator still blocks); MainScene handoff untested / AC2·AC5 manual-only (intent-accepted in-engine gate, Epic-10-deferred live verification).

**Follow-up review recommendation:** `false`. This pass patched 1 finding: 0 high, 1 medium, 0 low. Score = 3×1 + 1×0 = 3 (< 5), no high severity → `false`.

**Verification:** `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → **3565 passed, 1 skipped, 0 failed** (2m38s); the new drop/remap tests included; no golden re-baseline. The `dotnet build godot/godot.csproj` command is unaffected — no Godot-coupled files were touched; the one changed source file is the Godot-free transform already compiled (and now exercised) by the passing Tier-1 project.

**Residual risks:** resource nodes with a positive `owner_slot` on a dropped slot are NOT re-keyed by `Build` (the shipped maps have only neutral `owner_slot=-1` nodes and the applier degrades an out-of-range owner to Neutral inertly, so no current content is affected); the two deferred items above remain open in the ledger.
