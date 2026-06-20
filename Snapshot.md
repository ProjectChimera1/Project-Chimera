---
project: Project Chimera
last_touched: 2026-04-16
phase: Phase 5 — Polish & 1.0
status: Active
---

# Project Chimera — Snapshot

**Last Touched:** `2026-06-20`

## Current Phase
**Phase 5 — Polish & 1.0** (Months 25-31 of GDD roadmap)

Phases 0–4 are code-complete. Phase 5 is underway. Session 20 shipped worker-placed buildings + UI bug sweep. Session 21 (remote, away from computer) shipped Utility AI + Adaptive Input Delay.

## Next Action
**GDS forward-architecture pass — Step 4 (Architectural Decisions) COMPLETE (2026-06-20).** All six
game-specific decisions are recorded in `_bmad-output/game-architecture.md` (`stepsCompleted: [1,2,3,4]`):
D1 effects-primitive vocabulary, D2 trigger-DSL, D3 schema/loader (the deep-dive trio), plus the D4–D6
batch — **D4** hero persistence (two-rail, server-attested; PvP allowed, normalization via D1/D2),
**D5** >2-player lockstep + matchmaking (N-aware dedicated relay; ship N≤4, 8 fast-follow; AOT split
deferred), **D6** LLM provider abstraction (hand-rolled `ILLMProvider`; plaintext-key floor behind
`ISecretStore`). Full D4–D6 analyses in `game-architecture.D{4,5,6}-briefing.md`. **Cross-cutting finding:**
the batch surfaced one shared latent MP-correctness bug — unsound peer-agreement hashing in three places
(`SimChecksum` P1/P2-only; dedicated server is a pure relay with no hash compare; AI scenarios ship a stale
file-hash) — remediated by a canonical-model start-state hash + server-enforced agreement + generalized
`SimChecksum` (a single prerequisite program). **Step 5 (Cross-Cutting Concerns) COMPLETE (2026-06-20)** —
testing/quality is the headline: **two-tier checks** (fast Godot-free rule checks + GdUnit4 in-game) and an
**AI-orchestrated check-runner** with a Windows↔Linux cross-platform comparison (needs a Linux env — WSL2,
setup deferred to M1), plus determinism enforcement, observability/desync-diagnosis, error handling,
performance, accessibility (English-first), and telemetry (dev-only + opt-in crash report). See *Cross-Cutting
Concerns (Step 5)* in `game-architecture.md` + the `game-architecture.Step5-cross-cutting-briefing.md` sidecar.

**Step 6 (Project Structure + `MainScene` Decomposition) COMPLETE (2026-06-20).** Decision: **Shrinking
Composition Root + Sim-Spine Strangler** — the 2,223-LOC `MainScene` god object (which is *also* the composition
root every D1–D6 system threads through) is decomposed WITHOUT moving the file: it becomes a thin ≤250-LOC
ordered phase-list (`ISetupPhase[]` + `PhaseOrderTest`, preserving the fragile `_Ready()` order) that constructs
a Godot-free `SimulationHost` + `ScenarioApplier` (the sim-mutation path, now headless-testable + reused by a new
`ServerBootstrap`) behind a net-new fail-closed `ScenarioValidator` gate, plus focused presentation coordinators.
`FactionRegistry` localizes the 2-faction hardcodes (D5 N≤8 path). 14-step golden-checksum-gated, always-shippable
strangler; every net-new D1–D6 + Step-5 module homed. Produced via a 16-agent design+adversarial-verify workflow
(winning strategy 93/100). **Alec's 4 scope calls (✅):** (1) Validate gate flips fail-closed only on a release
branch after corpus-verify; (2) the sim-core + `ServerBootstrap` are M1-blocking — the spine; (3) AOT `.csproj`
split stays deferred (discipline + analyzers only now); (4) D6 secrets/config migration lands with the editor/MP
coordinator carve (migration Step 12). See *Step 6 — Project Structure + `MainScene` Decomposition* in
`game-architecture.md` + the `game-architecture.Step6-structure-briefing.md` sidecar.

**Resume at Step 7** (Implementation Patterns) → Step 8 (Validation) → Step 9 (Complete). Remaining chain after
architecture: epics/stories → re-run `gds-check-implementation-readiness` → `gds-sprint-planning`.

## Needs Testing — Written This Session

### ✅ Utility AI (`src/AI/AiOpponentSystem.cs`)

**VERIFIED 2026-06-20 (in-engine, alpha_map_01/Normal, ~290s game time, frozen-step):** All 4 deadlock-fix ACs PASS. P1 ore 200→540→660→900→1060 (monotonic rise); P2 gathered + reinvested (nodes 8→6, army 2→5→7→10→14, no solo-trickle); AI teched CC→Barracks→ArcheryRange (buildings 3→4); P2 wave reached P1 base and eliminated both P1 workers (P1 units 2→0) ~tick 5760–8700; **5 distinct sim hashes** (0x104E51CE→0xF2F66B7A→0x56FA1DEA→0x1774681A→0x5D07F97A — no fixed point); 0 errors. Earlier 2026-06-09 /godot-verify FAIL (deadlock) is resolved by `e3e48bc`. Not exercised this run: SiegeWorkshop tier, supply-expansion CC, Easy/Hard difficulty deltas, destroyed-Barracks recovery.

Full replacement of the rigid 3-phase FSM with utility scoring. All public API unchanged — `MainScene` needs no changes.

**Smoke test (single machine, Play mode):**
- [ ] Open any skirmish map in Play mode. Watch the P2 AI.
- [ ] **Early game**: AI should build a Barracks within ~20s of having 100 ore.
- [ ] **Tech progression**: after the Barracks is complete, AI should eventually build an ArcheryRange (requires Barracks complete), then SiegeWorkshop (requires ArcheryRange complete). Watch Godot Output for `[Lockstep]` / AI build logs to confirm order.
- [ ] **Supply expansion**: when AI supply headroom ≤ 4, it should build a CommandCenter before queuing more units (score 0.95 = highest priority).
- [ ] **Double production**: after the expansion CC is complete, AI should build a second Barracks.
- [ ] **Attack waves**: P2 combat units should periodically attack-move toward P1 base. Easy = fewer waves (threshold 8), Hard = more frequent (threshold 3).
- [ ] **Scenario pre-placed buildings**: load `map_06_contested_peaks` (pre-placed Barracks). AI should immediately train from it — verify units appear without AI needing to build its own Barracks first.
- [ ] **Destroyed Barracks recovery**: destroy P2's Barracks in-game. AI should score `BuildBarracks = 0.85` and rebuild without getting stuck in a reset loop.

**Difficulty smoke test:**
- [ ] Set `AiLevel = Easy` in Inspector → AI attacks late, small waves.
- [ ] Set `AiLevel = Hard` → AI teches up fast, attacks early and often.

---

### ✅ Adaptive Input Delay (`src/Multiplayer/LockstepManager.cs` + `NetworkCommand.cs`)

RTT measurement via Ping/Pong + negotiated delay changes via DelayProposal packets. `INPUT_DELAY = 4` is still the starting value; the constant is preserved for documentation.

**Build check:**
- [ ] `dotnet build` — 0 errors, 0 new warnings.

**Offline smoke test (single machine):**
- [ ] Launch game in Play mode (offline). No pings should be sent (only fires when `IsOnline`). No errors in Output.

**LAN smoke test (two machines required — do alongside P2.4 LAN test):**
- [ ] Host + join on LAN. Watch Godot Output on both machines.
- [ ] Within 2s of match start: both machines should log `[Lockstep] RTT sample: Xms` and a smoothed RTT.
- [ ] On LAN (~1-5ms RTT): target delay = `ceil(2.5ms / 33ms) + 1 = 2`. Both machines should log `[Lockstep] Delay: 4 → 2 ticks` within ~5s.
- [ ] Play for 300+ ticks. Checksums must stay in sync (same HUD hash on both machines). The delay reduction must NOT cause desync.
- [ ] Optionally: to test high-latency path, add artificial latency (e.g. `tc netem` on Linux) and verify delay increases toward MAX_DELAY=12.

**HUD wiring (optional, low priority):**
- The `CurrentDelay` property is now public. You can display it in the HUD stall indicator: e.g. `"Delay: {_lockstep.CurrentDelay} ticks"` alongside the "Waiting for peer…" banner. Not required for correctness — just a nice debug display.

---

## What's In Progress
- Utility AI + Adaptive Input Delay (written, needs smoke test — see checklist)
- LLM Trigger System (written session 22, needs smoke test — see checklist below)
- AI Map Generator (written session 23, needs smoke test — see checklist below)

### /godot-verify results (2026-06-09 — automated, full report: `D:\Brain\Reports\godot-checks\Project_Chimera-2026-06-09\verify-report.md`)
- **LLM Trigger System: PASS (core).** Panel opens, generator section works, no-API-key path fails gracefully ("Ollama unreachable" — message differs from spec'd "Both Claude and Ollama are unavailable"). Inline triggers verified in Play mode: match_start→add_resources (ore 200→700 tick 1) and create_timer→display_message (toast at ~5s) both fired. Not verified: unit_dies→spawn_unit, Validate() rejection, physical L key.
- **AI Map Generator: PASS (core).** Main-menu button enters Edit + toggles panel; panel renders left side; auto-hides on Play mode. Not verified: Load/Save flows + 7-pass validation (need API key or Ollama), physical M key.
- **Utility AI: FAIL — match deadlocks.** Barracks built fast (tick 45 ✓), but on Normal/alpha_map_01: a single early P2 unit killed both P1 workers, P2 income flatlined (25 ore, sim hash identical across ticks 1680→3180), no tech progression, no further attack waves. Needs investigation: worker gathering stops after AI build/train; no AI recovery path with no workers + <50 ore.
- **Adaptive delay (offline only): no errors observed** in ~110s offline play. LAN test still pending.
- Cosmetic: long status text stretches both AI panels across the screen (no autowrap/max width); possible shortcut leak (Grid Snap toggled while typing "G" in a text field — may be synthetic-input artifact, recheck manually).

---

### ✅ LLM Trigger System (session 22)

**New files:**
- `src/Core/Definitions/TriggerDefinition.cs` — JSON data model (events, conditions, actions)
- `src/Core/ScenarioDirector.cs` — ISimSystem; evaluates triggers every tick; runs last in sim loop
- `src/AI/LLMService.cs` — Claude API (+ Ollama fallback) + 5-pass validation pipeline
- `src/CreationSuite/TriggerEditorPanel.cs` — Edit-mode UI panel (L key toggle)

**Changes:** `ScenarioData.cs` +`Triggers[]`, `MainScene.cs` wired.

**Smoke test (single machine, Edit mode):**
- [ ] Open any map in Edit mode, press **L** → TriggerEditorPanel should open on the right side.
- [ ] Click "**+ New Trigger (via AI)**" → generator section appears.
- [ ] **(No API key):** Type any description and click Generate → status shows "Both Claude and Ollama are unavailable." or an Ollama response if Ollama is running locally.
- [ ] **(With API key):** Set `AnthropicApiKey` in Godot Inspector on MainScene → Generate produces a JSON preview → Accept adds the trigger to the list.
- [ ] **Inline trigger test (no API needed):** Add a trigger JSON manually to a scenario file (e.g. `alpha_map_01.json`), reload, enter Play mode:
  - match_start event → add_resources action → P1 ore should jump by the specified amount on tick 1.
  - create_timer (5s) → display_message action → toast label should appear after 150 ticks (~5 seconds).
  - unit_dies event → spawn_unit action → new units should appear at the specified position.
- [ ] **Validation test:** Manually craft invalid JSON (faction=5, count=200, bad operator) → `LLMService.Validate()` should reject with a clear message.

---

---

### ✅ AI Map Generator (session 23)

**New files:**
- `src/CreationSuite/MapGeneratorPanel.cs` — Edit-mode panel (M key toggle, CanvasLayer layer=13, left side)

**Changed files:**
- `src/AI/LLMService.cs` — `MapGeneratorContext` class, `GenerateScenarioAsync()`, `ValidateScenario()` (7-pass), `BuildMapSystemPrompt()`, `CancelScenario()`. `TryClaudeAsync`/`TryOllamaAsync` refactored to accept full `userMessage` string.
- `src/UI/MainMenuOverlay.cs` — `OnGenerateMap` event + "Generate Map (AI)" button (after Browse).
- `src/Core/MainScene.cs` — `_mapGenPanel` field, `_pendingGeneratedScenario` static field, `SetupMapGenerator()` after `SetupTriggerEditor()`, `LoadGeneratedScenario(ScenarioData)`, M key in `_UnhandledInput`, `_mapGenPanel.Update()` in `_Process`, `_mainMenu.OnGenerateMap` wired, `LoadAndApplyScenario()` checks `_pendingGeneratedScenario` before disk load.

**How it works:**
1. Press **M** in Edit mode (or click "Generate Map (AI)" in main menu) → `MapGeneratorPanel` opens (left side).
2. Type a map brief → **Generate ✦** → Claude API (or Ollama fallback) generates `ScenarioData` JSON.
3. 7-pass validation: schema → player slots (faction paths forced) → building types → unit IDs → position bounds → ore spacing ≥15u → ≤6 combat units per faction.
4. Preview shows: name, win condition, bounds, node/building/unit counts.
5. **↗ Load (no save)**: sets `_pendingGeneratedScenario` static field → `GetTree().ReloadCurrentScene()` → `LoadAndApplyScenario` reads the static field (no disk write).
6. **💾 Save & Load**: writes to `res://resources/data/scenarios/ai_generated.json` first, then same reload.

**Smoke test (single machine, Edit mode):**
- [ ] Open any map in Edit mode, press **M** → `MapGeneratorPanel` should open on the left side.
- [ ] **(No API key):** Type a brief → Generate → status shows "Both Claude and Ollama are unavailable." or Ollama response if running.
- [ ] **(With API key):** Set `AnthropicApiKey` in Inspector → Generate → stats preview appears (name, win condition, node/building/unit counts).
- [ ] Click **↗ Load (no save)** → scene reloads with the generated scenario; no JSON written to `res://resources/data/scenarios/` (check file browser).
- [ ] Click **💾 Save & Load** → `ai_generated.json` appears in `res://resources/data/scenarios/`; scene loads correctly.
- [ ] **Validation test:** The system should reject: positions outside ±120u, ore nodes closer than 15u, >6 combat units per faction, unknown unit_id, unknown building type.
- [ ] **Main menu button:** Open main menu → "Generate Map (AI)" button → menu closes, Edit mode entered, panel opens.
- [ ] Panel hides automatically when switching to Play mode (F5).

---

## Phase 5 Remaining Items
| Item | Status | Notes |
|------|--------|-------|
| Drop in audio .ogg files | 📋 | `res://resources/audio/sfx/` — AudioManager already wired |
| mod.io Inspector setup | 📋 | Select MainScene → set `Mod Io Game Id` + `Mod Io Api Key`; walkthrough at `docs/modio-setup-guide.md` |
| P2.4 LAN test (P2P mode) | 📋 | FlowFieldBridge active, verify checksums stay in sync through 300+ ticks |
| P0.3 Iron Pact art | 📋 | Hunyuan3D or Tripo — 8 GLBs to replace box placeholders (external work) |
| Terrain texture painting | 📋 | Set Terrain3D textures via Godot Inspector (Terrain3D → Assets) — procedural via ClassDB doesn't persist |
| Utility AI decision system | ✅ | VERIFIED in-engine 2026-06-20 (alpha_map_01/Normal, ~290s) — all 4 deadlock ACs pass, no deadlock. `e3e48bc` resolves the 2026-06-09 FAIL. |
| AI build order + attack timing logic | ✅ | Covered by utility scoring (tech tree, supply, aggression weights) |
| Adaptive input delay | 🔨 | Written — needs LAN test (see checklist above) |
| LLM trigger scripting | 🔨 | Written — needs smoke test (see checklist below) |
| AI-assisted map generation | 🔨 | Written session 23 — needs smoke test |
| AI balance analysis tools | 📋 | Phase 5 GDD item |
| Performance optimization pass | 📋 | Phase 5 GDD item |
| Advanced editor features | 📋 | Particles, sound triggers |
| Linux export | 📋 | Export template only — no code changes |
| 1.0 release | 📋 | Final milestone |

## Mental RAM
- **Current stack**: Godot 4.6.2 stable, C# / .NET 8, ECS-inspired simulation (custom SoA arrays, not a framework)
- **Rendering**: MultiMeshInstance3D for all unit rendering; two MultiMesh nodes per faction (separate colors)
- **Pathfinding**: `FlowFieldBridge` is the live path bridge (replaced `PathRequestSystem`). `PathRequestSystem` stays unused as fallback. Flow fields are deterministic — required for lockstep.
- **Networking**: Deterministic lockstep complete. `_currentDelay` starts at 4 and adapts via Ping/Pong RTT measurement + `DelayProposal` negotiation. Target delay = `ceil(OWL/33ms) + 1`, clamped [2, 12]. Both peers must agree before a change applies (`CommitDelayChange` pre-seeds gap ticks on delay increase). `INPUT_DELAY=4` is preserved as the start value constant. `CurrentDelay` property is public for HUD display.
- **Worker construction**: workers walk to site (`UnitCommand.Build` + `BuildTarget[]` SoA), building ticks its own construction timer autonomously, worker arrival clears command + resumes gathering.
- **`CommandCardSystem` worker card** fires `OnWorkerBuildRequested` → `MainScene` owns placement mode. `_Input` (not `_UnhandledInput`) for placement intercept — beats SelectionSystem.
- **`SettingsPanel`** uses intermediate `anchorRoot` Control (MouseFilter=Stop) for full-screen input blocking; Escape in `_Input`.
- **Terrain brush**: panel at (10,155) below HUD; `IsOverPanel()` guard stops paint on slider clicks; `ApplyBrushSettings()` in `ContinuePaint()` for live slider updates.
- **Supply cap**: dynamic — base 10 + 10 per alive CommandCenter. `TrainUnit()` supply-gates before deducting ore.
- **`AiDifficulty`**: Easy(8 units/40s), Normal(5/25s), Hard(3/15s). `[Export] AiLevel` on MainScene.
- **Assembly name**: `ProjectChimera` (csproj + project.godot must match or scripts won't load)
- **`PathRequestSystem` owns Move→Stop transition** (NOT Move→Idle) — Move→Idle caused stutter bug (TickIdleCombat re-wrote MoveTarget on very next sim tick)

## Open Design Decisions
- **AI art tool**: Hunyuan3D vs Tripo vs other — P0.3 Iron Pact art still pending

## Performance Baseline
| Configuration | FPS |
|---|---|
| Movement only, 500 units | ~1150 |
| Combat O(n²), 500 units | ~300 |
| Combat O(n²), 1000 units | ~50 |
| Combat + SpatialHash, 1000 units | ~350 |

## Key Architecture Decisions
- ECS-inspired simulation: SoA arrays, free list, no framework. Pure C# sim layer — no Godot types.
- NavigationServer3D direct API (no NavigationAgent3D nodes). FlowFieldBridge for deterministic multiplayer.
- Fog of war: 128×128 byte grid, R8 ImageTexture uploaded each frame by FogOfWarBridge.
- Buildings use `BuildingStore` SoA (not EntityWorld) — buildings don't move or attack.
- `PathRequestSystem` lives in presentation layer; sim layer only reads MoveTarget.
- `AiOpponentSystem` runs LAST in SimulationLoop — sees fully-updated supply caps and construction states.
- Tech tree: `prerequisites` string[] on `UnitDefinition`; checked by `TechTreeChecker.AreMet()`.
- Scenario system: `[Export] string ScenarioPath` on MainScene — map swappable from Inspector.
- Lockstep: `LockstepManager` pure C# (no Godot dep); bridges via `OnRequestPath/OnRequestAttackMove/OnCancelPath` delegates.
- Replay: `.chmr` binary format. Auto-starts on `OnMatchStart()`. `ReplayPlayer` re-applies stored orders.
- Nakama matchmaking: `NakamaService.FindMatchAsync()` — 2-player, `game=chimera_1v1`. Faction assigned by server.

## Reference
- GDD: `GDD_Project_Chimera.md`
- Implementation status (archived): `D:\Obsidian Brain\Brain\30_Archive\Chimera_STATUS_archived_2026-04-16.md`
- Godot/C# patterns (live, auto-injected each session): `D:\Obsidian Brain\Brain\20_Reference\GameDev\godot-csharp\LEARNINGS.md`
- Godot project: `D:\Obsidian Brain\Brain\10_Active_Projects\Project_Chimera\godot\`
- Server deploy: `godot/docs/server-deploy/`
- mod.io setup: `godot/docs/modio-setup-guide.md`
