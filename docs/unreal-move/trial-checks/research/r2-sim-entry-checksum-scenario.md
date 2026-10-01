# R2 - How a match is built and run headlessly, the checksum contract, and the 1,000-unit scenario

Date 2026-10-01. UNDERSTAND phase for trial check (a). Scope: map the sim entry points, the checksum and golden harness,
find every place real Godot differs from a headless run, list JIT-vs-NativeAOT hazards, and propose one 1,000-unit
scenario plus the minimal C API. Nothing in the repo was edited. All paths are relative to `D:/Projects/Project_Chimera`.

Evidence legend: `file:line` = read in this session. **RAN** = executed here, result quoted. **UNVERIFIED** = not run or
only argued from reading. Line numbers are from the tree at commit `5e6bc03d` (clean).

---

## 0. The findings that matter most

1. **There is already a Godot-free composition root that builds a full match from content**: `ServerBootstrap.Build`
   (`godot/src/Core/Sim/ServerBootstrap.cs:38`) on top of `SimulationHost.Create` (`godot/src/Core/Sim/SimulationHost.cs:213`)
   and `ScenarioApplier.Apply` (`godot/src/Core/Sim/ScenarioApplier.cs:129`). Tests already drive real JSON maps through it
   (`godot/ProjectChimera.Sim.Tests/Multiplayer/FourPlayerEndToEndTests.cs:140-163`). But it is **not** the same recipe as
   real Godot: Godot additionally wires flow-field obstacles, path-request delegates, AI plan, item registry and a
   per-match seed (section 9). **RAN**: leaving the flow-field wiring out changes the whole match (alive P1/P2 at tick 900:
   203/146 without vs 290/285 with). Any "same checksum as Godot" claim needs ONE shared composition used by all three legs.
2. **The existing headless Godot entry does not tick the sim.** The dedicated-server branch (`godot/src/Core/MainScene.cs:361-399`)
   builds and holds a validated host but never steps it (`godot/src/Multiplayer/DedicatedServer.cs:259,311`; comment
   `MainScene.cs:376-380`). There is no scripted-order runner in Godot. The nearest things are `--loopback-test`
   (`MainScene.cs:350-358`, 4 scripted units, `godot/src/Multiplayer/LoopbackPeerSim.cs`) and the debug seam that exposes
   `tick` and `checksum` (`godot/src/Core/MainSceneDebugSeam.cs:72,113-120`). Real Godot already prints
   `[Checksum] tick=N hash=0x...` every 60 ticks (`MainScene.cs:576-580`, interval `SimulationLoop.cs:36`).
3. **NativeAOT reproduces the JIT exactly on this machine. RAN**: a scratch program compiling the real sim sources
   (`godot/SimSources.props`) ran the 1,000-unit scripted match under Debug JIT, Release JIT and NativeAOT (win-x64, ILCompiler
   8.0.25): all 1,440 per-tick checksums identical, with the AI off and with the AI on (float scoring), and the five pre-tick
   content/start-state hashes identical (section 10.2).
4. **Content loading is the real AOT blocker, and it fails loudly. RAN**: the same program built with `PublishAot`
   using today's loaders dies at `FactionDefinition.LoadFromFile` (`FactionDefinition.cs:346`) with
   `NotSupportedException ... Type 'ProjectChimera.Core.Definitions.FactionDefinition'`; with an rd.xml rooting the assembly
   it dies one level deeper on `ArrayConverter<float[],float>` "missing native code or metadata". Source generation is
   required, not optional (section 2.3).
5. **1,000 units is cheap**: Release median 1.1 ms/tick, p95 2.4 ms (AOT the same); Debug-config JIT median 13-14 ms,
   p95 31 ms, worst tick 228 ms. The 33.3 ms tick budget is not at risk at this scale.
6. **The scenario must not reach a verdict.** Real Godot stops stepping at game over (`MainScene.cs:2541`, gate `:1599`).
   My layout produces the first verdict (P1 wins) at tick **1,499**, so the proposal runs **1,440 ticks**.
7. **Shipped content never draws RNG** (the RNG state stayed `0xC0FFEE...` for the whole run), so the RNG fold is not
   exercised by the shipped factions; the proposal calls this out and offers a content variant.
8. **Biggest hidden Godot-only behaviours** (section 9): per-frame legacy writer `PathRequestSystem` flips Move to Stop in
   `_Process` (`godot/src/UI/PathRequestSystem.cs:105-109,221-236`), offline human orders bypass `OrderApplier`
   (`godot/src/UI/SelectionSystem.cs:~827-845`), offline Play uses a wall-clock seed unless `CHIMERA_MATCH_SEED` is set
   (`MainScene.cs:3006`), and the AI defaults to ON for Player2 in every host that does not set a plan
   (`godot/src/AI/AiOpponentSystem.cs:151`).
9. `godot/src/UI/BuildingNavFootprint.cs` is Godot-free and needed for identical flow-field obstacles, but it is **not** in
   `SimSources.props`. A NativeAOT library built from `SimSources.props` alone would stamp different building footprints.

Evidence base (all **RAN**):

| What | Result |
|---|---|
| `dotnet build godot/ProjectChimera.Sim.Analysis -c Debug --no-incremental` | 859 unique warnings, 0 errors: CHM0001 (float) 394, CHM0005 (FromFloat/ToFloat) 280, IL3050 65, IL2026 62, CHM0004 (magic cap literal) 35, CHM0002 (Dictionary/HashSet enumeration) 10, CHM0006 (float parse/format) 3, CHM0003 (unstable sort) 1 |
| `FourPlayerLoadPerfTests` (Debug, 4,096 stacked units + 64 buildings) | 189.364 ms/tick median (samples 168.5-196.9); recorded 141.656 ms on 2026-07-24 (`_bmad-output/implementation-artifacts/perf-4player-9-15.md`) |
| Scratch prototype `scratchpad/proto` (compiles `SimSources.props` + `BuildingNavFootprint.cs`) | sections 10-11 |
| Full `dotnet test godot/ProjectChimera.Sim.Tests` (Debug) | **7,124 tests: 7,122 pass, 1 fail, 1 skipped** in 1 m 46 s. The failure is `Meta.ToolingGateGuardTests.BurnDownDispatcher_HasNoHardcodedTier1Baseline` (`Meta/ToolingGateGuardTests.cs:68`), which asserts `.claude/workflows/dw-burndown.workflow.js` exists; that file went with the BMAD tooling retired in commit `c794681b`. Unrelated to the sim. HANDOFF's "6,392" count is stale |

---

## 1. Top-level object and how it is wired

`SimulationHost` (`SimulationHost.cs:40`) is the composition root: it owns every store, the 21-system array and the loop.
Construction `SimulationHost.Create(log, FactionRegistry checksumFactions, factionDef1, factionDef2, damageTable, aiLevel,
abilityRegistry, itemRegistry)` (`:213-222`, ctor `:224`). Only the two faction defs of slots 1 and 2 are given to the host
(`BuildingSystem`/`ResearchSystem`); every other slot's def goes to the applier (`ScenarioApplier.cs:70`, `:187-201`).

| Group | Members (`SimulationHost.cs`) | Folded into SimChecksum |
|---|---|---|
| Entities | `World` = `EntityWorld` (`:78`), SoA arrays, `MAX_ENTITIES = 4096` (`EntityWorld.cs:210`) | yes (entity loop) |
| Economy | `Nodes`, `Resources`, `Buildings`, `Research` (`:79-81,138`) | yes |
| Combat | `Projectiles`, `Modifiers`, `CombatEvents`, `MatchStats` (`:82,110-112`) | Modifiers yes; Projectiles, CombatEvents, MatchStats no |
| Heroes/items | `Heroes`, `Items`, `ItemSys`, `HeroXp` (`:90,98,102,135`) | yes |
| DSL/win | `Vars`, `LoopState`, `DslEvents`, `WinState`, `Alliances`, `TriggerEnabled`, `WinCon`, `ScenarioDirector` (`:143-189`) | yes (stores) |
| Navigation | `FlowFields`, `Steering` (`:120,126`) | no (state reaches hash via Position) |
| Presentation-ish | `Fog` (`:190`), `Readback` (`:151`), `TriggerFireLog` (`:178`), `DslSimEvents` (`:184`) | no |

Entity ids are slot indices; recycled slots bump `Generation[]`; a cross-tick entity reference is a **packed ref**
`(Generation << 12) | id` (`EntityWorld.cs:1095` `REF_SLOT_BITS = 12`, `:1892` `PackRef`, `:1902` `TryResolveRef`). For a
never-recycled entity `PackRef(id) == id`.

### 1.1 Content on disk

All under `godot/resources/data/` (the only paths the sim reads):

| Path | Content | Loader |
|---|---|---|
| `factions/alpha_faction.json` (8 units: worker, infantry, scout, heavy_infantry, archer, mage, siege_engine, griffin; 5 buildings), `beta_faction.json` (forgehand, footsoldier, bulwark, ironclad, crossbowman, rune_caster, war_machine, wyvern; same 5 building ids) | rosters, stats, abilities, costs | `FactionDefinition.LoadFromFile` (`godot/src/Core/Definitions/FactionDefinition.cs:334`) |
| `abilities/*.json` (13 files) | active/passive abilities, effect graphs | `AbilityLoader.LoadFromFile` via `AbilityRegistry.LoadFromDirectory` (`AbilityRegistry.cs:71`, ordinal file order, final index by ability Id) |
| `items/*.json` (2), `behaviors/*.json` (2, authoring-only), `attribute-models/*.json` (7) | | `ItemRegistry.LoadFromDirectory` (`ItemRegistry.cs:61`), `BehaviorRegistry.LoadFromDirectory` (`:69`) |
| `damage_table.json` | damage x armour matrix | `DamageTable.Load` (`godot/src/Combat/DamageTable.cs:134`); in-code fallback `DamageTable.Default` (`:82`) |
| `scenarios/*.json` (10: alpha_map_01, map_02..06, map_10..12, quad_map_01) | maps: slots, nodes, buildings, units, triggers | `ScenarioSerializer.LoadFromFile` (`ScenarioSerializer.cs:96,104`; 8 MiB input cap `:50`) |
| `terrain_ref` folder (Godot Terrain3D `.res` regions) | heights | Godot only (`ScenarioLoadPhase.RestoreTerrainFromScenario`); the sim only sees an `ElevationGrid` |

A scenario slot names its faction as `res://resources/data/factions/...` (`alpha_map_01.json` player_slots). Resolving
`res://` is Godot-side today (`ProjectSettings.GlobalizePath`, `MainScene.cs:3344,3358`; the shared resolver
`godot/src/Core/Bootstrap/Phases/SlotFactionResolver.cs` is Godot-coupled and excluded from the Godot-free assembly,
`SimSources.props` `Compile Remove` of `Bootstrap\Phases`). The native library needs its own `res://` -> content-root map.

### 1.2 Reflection System.Text.Json sites on the match-load path

Option sets are in `godot/src/Core/Definitions/ContentJson.cs:59-186`. Match-load reflection sites (the analyzer's IL2026/IL3050
list also holds editor, LLM and packager sites that do **not** block the trial):
`FactionDefinition.cs:346,463`; `AbilityLoader.cs:32` (with the polymorphic `EffectNodeJsonConverter.cs`, 8 sites) and
`AbilityLoader.cs:88` (`JsonDocument`); `ScenarioSerializer` (2 sites, plus `NodeBaseJsonConverter`/`WidgetBaseJsonConverter`
for triggers and custom UI inside a scenario); `DamageTable` (1-2); `ItemLoader` (1); `BehaviorRegistry.cs:89`.
Hand-written `Utf8JsonReader`/`JsonDocument` code (EffectNodeJsonConverter, FixedJsonConverter `:32`) is already AOT-safe.

### 1.3 `ScenarioApplier.Apply` order (this is the spawn order)

`ScenarioApplier.cs:129-441`: revival rule + supply config + building gap config -> pathability guard (`:184`) -> per slot:
faction def to `BuildSys`/`ResearchSys`, starting ore/crystal, `FactionBase` (`:187-214`) -> `AllianceSeeder.Seed` (`:223`) ->
resource nodes -> buildings in `CanonicalBuildingOrder` (`:279`) -> units in `CanonicalUnitOrder` (`:347`) -> hero record ->
items -> `RegionStore` -> `WinCon.Configure` (`:438`) -> `ScenarioDirector.LoadScenario` (`:441`, triggers last).
`CanonicalUnitOrder`/`CanonicalBuildingOrder` (`:716-750`) sort by (slot, ordinal id, Fixed x, Fixed z, authored index): spawn
order, hence entity ids, is a **strict total order independent of JSON row order** and of `Array.Sort` stability. Units spawn
through `SpawnUnitAt` (`:664`): `world.Create` + `world.ApplyUnitDefinition` (`:675`) + `MeshType` = index in the faction's
unit list (`:681-682`) + worker gather state. `MeshType` is presentation-only but is exactly what Unreal needs to pick a mesh.
Buildings parse via `Enum.TryParse<BuildingType>` (`:299`), which worked under NativeAOT in my run.

---

## 2. Tick loop and system order

`SimulationLoop` (`godot/src/Core/SimulationLoop.cs:22`): `TICKS_PER_SECOND = 30` (`:24`), `FixedDt = Fixed.FromRaw(65536/30)`
= 2184 raw (`:27`), `ChecksumInterval = 60` default (`:36`).

`StepOnce` (`:207-225`): `World.SnapshotPositions()` (Position -> PrevPosition) -> every system in array order with `FixedDt` ->
`AssertTickBoundaryInvariants` (`:153`, **throws** `InvalidOperationException` if a transient feed is non-empty) -> `CurrentTick++` ->
`SimChecksum.Compute` when `interval > 0 && tick % interval == 0` (`:219-223`) -> `OnChecksum(tick, hash)`.
`Update(float realDelta)` (`:232-268`) is the same body behind a float accumulator clamped at 0.25 s (`:235`); it only changes
cadence. Offline Godot calls `_host.Update(delta * gameSpeed)` (`MainScene.cs:1743`); online and replay use `StepOnce` behind
fixed-timestep pacers (`:1682-1717`, `:1634`). Commands for tick T are applied **before** the `StepOnce` that produces tick T+1
(`MainScene.cs:1708-1717`: `Flush(CurrentTick)` then `StepOnce`). The native library must expose `StepOnce` only.

System order (`SimulationHost.cs:343-445`; pinned by `ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs:48-70`, which fails on any
reorder):

`[0]` BuildingSystem `[1]` ResearchSystem `[2]` GatheringSystem `[3]` FlowFieldSteeringSystem `[4]` MovementSystem
`[5]` OrderQueueSystem `[6]` EnergyRegenSystem `[7]` HealthRegenSystem `[8]` AbilityCastSystem `[9]` ModifierSystem
`[10]` CombatSystem `[11]` ProjectileSystem `[12]` HeroXpSystem `[13]` VeterancySystem `[14]` ItemSystem `[15]` SupplySystem
`[16]` FogOfWarSystem `[17]` AiOpponentSystem `[18]` WinConditionSystem `[19]` ScenarioDirector `[20]` DeathFeedDrainSystem.

Several doc comments are stale about the count (`SimulationHost.cs:17` says 19, `GoldenScenario.cs` says 10, `LoopbackPeerSim.cs:66`
says 16); the array is 21 and the test is the truth.

---

## 3. Command stream (how Move / AttackMove enter the sim)

* Wire struct `UnitOrder` (`godot/src/Multiplayer/NetworkCommand.cs:245-277`, 14 bytes, `Pack = 1`): `UnitId` int32 = **packed
  entity ref** for entity-subject commands (raw building slot for building-family commands), `Command` byte (low 7 bits =
  `UnitCommand`, **0x80 = Shift-queued**: `EntityWorld.cs:70-76`), `TargetX`/`TargetZ` = `Fixed.Raw` int32, `Slot` byte
  (ability slot, or `BuildingType` for PlaceBuilding).
* `UnitCommand` enum values are frozen (`EntityWorld.cs:14-40`): Move 1, AttackMove 2, Stop 3, HoldPosition 4, AttackTarget 6
  (TargetX = packed target ref, **raw**, never via float), Patrol 7, PatrolAppend 9, CastAbility 10, Train 11 ... Concede 22.
* The single choke point: `OrderApplier.Apply(world, in UnitOrder, Faction expectedFaction, onRequestPath, onRequestAttackMove,
  onCancelPath, buildings, events, items, research, dslSink, winState, log, heroXp)` (`NetworkCommand.cs:294-300`). Building-family
  commands are dispatched before the entity guard; entity commands pass `TryResolveRef` (stale refs dropped), the faction-ownership
  check and the phased-unit check; a queued order is appended (`AppendOrder`), a plain order clears the ring then calls
  `ApplyActiveOrder` (`:579`).
* Move (`:590-600`) sets `CommandState`, `CommandGoal`, `MoveTarget`, `Moving` flag, `AttackTarget = -1`, clears the patrol
  route, then calls `onRequestPath(id, float x, float z)` with the **float round trip** `Fixed.FromRaw(x).ToFloat()`. AttackMove
  (`:601-611`) is the same with `onRequestAttackMove`. **The delegates are null in goldens and in `ServerBootstrap`**; in Godot
  they are bound to `FlowFieldBridge` (`godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs:132-135` live, `:353-355`
  replay), whose `ToSim` is `Fixed.FromFloat` of the float (`godot/src/UI/FlowFieldBridge.cs`), which calls
  `FlowFieldSteeringSystem.RequestPath/RequestAttackMove` (`godot/src/Navigation/FlowFieldSteeringSystem.cs:90,104`).
  The float round trip is lossless for |coordinate| < 256 (24-bit mantissa, 16 fractional bits); beyond that it truncates.
* Online/spectator/replay/server: orders travel as `TickCommandPacket` (`:844`) merged into `MergedTickPacket` (`:1897`) and
  applied at the execution tick by `MergedTickApplier.Apply` (`godot/src/Multiplayer/Server/MergedTickApplier.cs:32,75`) which
  calls `OrderApplier.Apply` per order with the faction stamped from the transport slot (`LockstepManager.cs:560-565`).
* Offline Godot human input does **not** go through this path: `LockstepManager.EnqueueOrder` returns `true` when offline
  (`LockstepManager.cs:366-368`) and `SelectionSystem` then writes the sim fields itself (`SelectionSystem.cs:~827-845`).
  `MainScene` Play-mode online and all replays use `OrderApplier`. For parity (and for Unreal's "one way in") use `OrderApplier`.

---

## 4. SimChecksum

* API: `public static uint Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry, ModifierStore?, HeroStore?, ItemStore?,
  ResourceNodeStore?, ResearchStore?, DslVarTable?, DslLoopState?, DslEventQueue?, WinStateStore?, AllianceStore?,
  TriggerEnabledStore?)` (`godot/src/Core/SimChecksum.cs:378`). 32-bit FNV-1a over little-endian bytes of each `int`
  (`:76-77`, `Mix` `:927`). All inputs are `int`/`Fixed.Raw`; no float, no string, no hash-container iteration
  (`ActiveFactions` is an ascending list). `AlgoVersion = 29` (`:372`); golden files stamp it (`GoldenChecksumReplay.cs:183`);
  most per-scenario golden headers still read 25 (one 22, 26, 27) because later folds are bounded/zero for those scenarios
  (the v23-v29 notes in `SimChecksum.cs` describe the "frozen control" posture).
* Fold order (`SimChecksum.cs`): per alive entity in ascending id (`:391-597`): Position XYZ, Health, Elevation, CommandTarget,
  patrol ring, CollisionRadius, SeparationPriority, Delivery, ProjectileSpeed, XpBounty, Effective attack/max-health/move-speed/
  armour, modifier instances, ability cooldowns, shift-queue ring, gather state, stat-pipeline terms, dice terms, veterancy
  kills; buildings (`:598-631`, incl. rally and production queue); per active faction resources (`:632-649`); hero store
  (`:650-705`); items (`:706`); resource nodes (`:733`); research (`:756`); DSL vars (`:807`), loop state (`:821`), event
  queue (`:832`); win state (`:846`); alliances (`:876`); trigger-enabled (`:895`); **SimRng state last** (`:911`).
* **Not folded** (divergence shows up late and only transitively through Position/Health): `Flags`, `CommandState`, `MoveTarget`,
  `AttackTarget`, `AttackCooldown`, `ActiveOrderCmd`, `Velocity`, `PrevPosition`, `ProjectileStore`, `MeshType`, fog, combat events.
  So a port bug in steering or projectiles may first appear a few ticks after its cause; debugging needs a wider diagnostic
  hash (test-side, not in the sim).
* When the game computes it: every `ChecksumInterval` = **60 ticks = 2 s** (`SimulationLoop.cs:36`), the only setter is tests
  (`LoopbackPeerSim.cs:50`; goldens set 1). Offline Godot logs `[Checksum] tick=... hash=0x...` (`MainScene.cs:576-580`); online it
  also sends the hash to the server (`LockstepManager.SendChecksum`, `:571-582`), where `ServerChecksumCollector`
  (`godot/src/Multiplayer/Server/ServerChecksumCollector.cs:20`) takes a strict-majority vote and names the minority slot.
* Pre-tick agreement hashes (all Godot-free, versioned): `StartStateHash.Compute(model, heroes)` (AlgoVersion 2),
  `CanonicalModelHash.Compute(model)` (17), `ContentHash.Compute(factions, abilities, items, damage)` (6, `ContentHash.cs:125`),
  `RulesetHash.Compute()` (3), `MatchAgreementHash.Compute(initialDelay, model, heroes, factions, abilities, items, damage,
  aiPlan)` (4, `MatchAgreementHash.cs:62`). These are the cheapest "same content, same start state" proof before tick 1.

---

## 5. Golden-checksum harness (`godot/ProjectChimera.Sim.Tests`)

* Engine: `Golden/GoldenChecksumReplay.cs`. `RunAndRecord(ticks, perturb, build)` (`:51`) builds a fresh scenario, sets the checksum
  sink on the host, calls `host.StepOnce()` N times (`:57-63`). Scenarios return a `GoldenHarness` (`GoldenScenario.cs:17`).
  `CompareSequences` (`:73`) finds the first divergent tick. `DescribeDivergence` (`:97`).
* Golden file: text, `# header` lines then `<tick> <hash8hex>` per line (`FormatGolden`, `:175`; parse `:128`, CRLF-tolerant),
  e.g. `Golden/golden-scenario.golden.txt` (300 lines, one per tick). Embedded as a resource (`ProjectChimera.Sim.Tests.csproj`
  `EmbeddedResource` entries, lines 108-282) and loaded by `LoadGolden` (`:106`) so Linux/Windows need no paths.
* Record: environment variable `CHIMERA_GOLDEN_RECORD=1` (`:31`); `MaybeRecord` (`:198`) writes the source file located by
  `[CallerFilePath]` (`:212`); the record test first requires two in-process runs to match and the file to round-trip
  (`GoldenChecksumReplayTests.cs:32-60`); then `dotnet build` (refresh the embedded copy) and commit. Compare: every other test
  runs the scenario and compares to the embedded golden (`:67-99`); a perturbation test proves a drift is detected and located
  (`:107-133`).
* 35 goldens (`Golden/*.golden.txt`), most 300 ticks at interval 1; scenario builders are in-code (`GoldenScenario.cs`,
  `GoldenApplierScenario.cs`, `AiActiveScenario.cs`, `CommandVocabularyScenario.cs`, `FormationSeparationScenario.cs`,
  `MultiFaction8Scenario.cs`, merged-tick N2/N3/N4 ...). Real-content scenarios exist (`DataDrivenBuildingScenario.cs`,
  `Multiplayer/FourPlayerEndToEndTests.cs`, `Server/ServerBootstrapDeterminismTests.cs`) but none steps a large real-content
  battle. **None of the goldens wires the flow-field delegates** (only `Navigation/FlowFieldSteeringTickPacingTests.cs:50` calls
  `host.Steering.RequestPath`), so real-game pathing is covered only by that test.
* Platform gating: the AI-active golden is Windows-only because it pins the AI's float scoring (`Golden/AiActiveGoldenTests.cs:57-66`).
  A Windows-to-Linux gate exists (`godot/tools/cross-platform-determinism-runbook.md`, `...check.ps1`, `...wsl.sh`).
* Test project compiles the sim sources directly (not a reference to `godot.csproj`) via `godot/SimSources.props`, plus single-file
  includes. Content tests locate `resources/data` by walking up from the test binary (`RealContentFixture.cs:41-52`).

---

## 6. RNG seeding

`SimRng` is SplitMix64, integer-only (`godot/src/Core/SimRng.cs:23-73`); one shared instance `EntityWorld.Rng` constructed with
`DEFAULT_RNG_SEED = 0x9E3779B97F4A7C15` (`EntityWorld.cs:283,1211`) and re-seeded to that on `Clear` (`:2024`). Where seeds change:

| Path | Seed | Site |
|---|---|---|
| `ServerBootstrap`, goldens, tests | default | nothing re-seeds |
| Online match | `DEFAULT_RNG_SEED` re-asserted | `MatchLifecycleController.cs:181-182` |
| Offline Edit-to-Play (`ResetToAuthoredStartCore`) | `MatchSeedProducer.Produce(Time.GetTicksUsec())` = wall-clock mixed through SplitMix64, **or** the value of env var `CHIMERA_MATCH_SEED` (decimal or `0x` hex) | `MainScene.cs:3006-3009`, `MatchSeedProducer.cs:36,99` |
| Replay | seed from header | `ReplayPlayer.cs:237` |
| Save load | saved state | `SaveGameState.cs:833` |

Draws happen in tick time for crit/dodge (`DamageResolver`), DSL `random`, and any effect with a roll. **RAN**: with shipped
alpha/beta content the RNG state never changed over 4,500 ticks, so the seed is invisible; a trial that wants to exercise
the RNG fold needs content with crit/dodge authored (there is a crit/dodge golden scenario in code,
`Golden/CritDodgeScenario.cs`, but no shipped faction uses it). The harness must pin the seed explicitly.

---

## 7. Existing large-count scenarios and headless entry points

| Item | What it is | Reusable? |
|---|---|---|
| `godot/tools/StressTest.cs` + `godot/scenes/stress_test.tscn` | Godot scene, 500 units (250 per side, `:15`), **only Movement + Combat + Projectile systems** (`:40-43`), spawn positions and speeds from Godot's `RandomNumberGenerator` (`:35-36,79-83`), wall-clock `Update` | No: non-deterministic, 3 of 21 systems, renders 500 boxes |
| `Perf/FourPlayerLoadPerfTests.cs` | Tier-1, ~4,096 stationary stacked units + 64 buildings, 4 factions, median ms/tick, no assertion | Cost reference only (189 ms/tick Debug here); not a battle |
| `Server/ServerBootstrapDeterminismTests.cs`, `Multiplayer/FourPlayerEndToEndTests.cs` | real `quad_map_01.json` through `ServerBootstrap`, 8 units | Pattern to copy |
| `MainScene` dedicated-server branch | builds validated host, **does not step** | Entry for loading only |
| `--loopback-test` (DEBUG) | `LoopbackPeerSim` x4 over ENet, 4 scripted units, per-tick real checksum | Pattern for scripted orders + real checksums |
| `MainSceneDebugSeam` (DEBUG, offline) | `_mcp_state()` tick + `checksum` hex + alive counts, spawn, cast, set health | Observe only; no generic order or step API |
| `CHIMERA_MATCH_SEED` | pins offline match seed | Directly usable |
| godot-mcp `godot_game_time` / `godot_exec` | frame-level stepping and GDScript | Cannot inject orders at an exact tick (the offline pump is `Update(float)`) |

There is **no** existing way to run a scripted order list at exact ticks inside real Godot.

---

## 8. What real Godot does at match start that a headless harness may do differently

Godot boots in Edit mode and Play runs `ResetToAuthoredStartCore` (`MainScene.cs:2878`): `ClearForReset` (`:2973`), AI plan
(`:2990-2994`), wall-clock seed (`:3006`), `PathabilityGrid` rebuild, scenario re-apply, `FlowFieldSys.RebuildObstacles` (`:3097`),
hero re-mint, start-state hash log. A fresh `ServerBootstrap.Build` is meant to be equivalent (pinned by `Sim/SimResetTests.cs`,
`ClearCompletenessSweep.cs`) but they are two code paths. Differences that can change a checksum:

| # | Aspect | Real Godot | Headless harness / ServerBootstrap | Effect on checksum | Action for the trial |
|---|---|---|---|---|---|
| 1 | Flow-field wiring | `SetBuildingFootprintSource` (`NavigationPhase.cs:83-101`), `SetStaticBlocked` (`ScenarioLoadPhase.cs` BuildAndInjectPathabilityGrid), `RebuildObstacles` + `SyncBuildingBaseline` (`FlowFieldInitPhase.cs:21-26`), delegates (`MatchLifecycleController.cs:132-135`) | none of it | **Large. RAN**: without it units walk straight lines and arrived `Move` units stay `Move` forever (nothing flips them; `MovementSystem.cs:110-112` leaves it to presentation) | Shared `SimSession` must do all four |
| 2 | RNG seed | wall-clock unless `CHIMERA_MATCH_SEED` | default | None for shipped content, large with crit/dodge | Pin one seed in every leg |
| 3 | AI plan | per-match plan: offline derived from skirmish slots else `OfflineDefault` {P2} (`MainScene.cs:2990`), online none (`MatchLifecycleController.cs:129`) | host default is `OfflineDefault` (`AiOpponentSystem.cs:151`) so **P2 AI is ON** unless set | Large. RAN: with AI on P2 builds 5 buildings (2 to 7) and spends ore | Set `AiControlPlan.None` explicitly (main scenario) |
| 4 | AI level, item registry | passes `AiLevel` (Normal default, `MainScene.cs:252`) and `_itemRegistry` (`:527-535`) | `ServerBootstrap.cs:77-80` passes neither (Normal, Empty) | None unless items/heroes in scenario | Pass both explicitly |
| 5 | `PathRequestSystem` | a Node added at `NavigationPhase.cs:72-75`; `_Process` (`UI/PathRequestSystem.cs:105-109`) runs `CheckMoveGoalArrival` (`:221-236`) every rendered frame: any alive `Move` unit within 1.5 u (float math) of `CommandGoal` is set to `Stop`. `RequestPath` is never called any more, so `_paths` is always null. `OrderQueueSystem.cs:71-75` documents the flip | absent | Frame-paced write into sim state; moves the Move-to-Stop transition to frame time and breaks "keep Move while orders are queued" (`FlowFieldSteeringSystem.cs:~170-185`). Impact **UNVERIFIED** | Run the Godot leg in a scene without it; recommend deleting it before parity claims |
| 6 | Human order path (offline) | `SelectionSystem` direct writes (`:~827-845`) | `OrderApplier` | Possible small differences (e.g. ring clearing); **UNVERIFIED** | Drive Godot parity through `OrderApplier` |
| 7 | Game over | `_gameOver` stops stepping (`MainScene.cs:1599,2541,2672`) | keeps stepping | Checksums after a verdict only exist headless | Stop before first verdict |
| 8 | Terrain / elevation | Terrain3D sampled into `ElevationGrid` (`ScenarioLoadPhase.BuildAndInjectElevationGrid`, 256x256) | `null` unless passed (`ServerBootstrap.cs:33-41`) | None for flat maps; sim samples height once at spawn (`EntityWorld.cs:1422,1430`) | Flat map for the trial |
| 9 | NavMesh bake | Godot `NavigationMesh` (`NavigationPhase.cs`) | none | None: sim uses flow fields only | Ignore |
| 10 | Fog | `FogOfWarSystem` ticks at `[16]`, viewer retarget, building vision source (`NavigationPhase.cs` ~104) | ticks, no vision source | None: nothing in Combat/Economy/Navigation/AI reads it (grep); costs CPU only | Keep it in the order |
| 11 | Trigger delegates | `ScenarioDelegateBinder.cs:26-55` binds `OnSpawnUnit`, `OnVictory`, ... | unbound (`ScenarioDirector.cs:479-493` are `Action?`) | A `spawn_unit` trigger spawns in Godot, no-ops headless | No triggers in the trial; the native session must bind them later |
| 12 | Culture | `CultureInfo` invariant pinned in `_EnterTree` (`MainScene.cs:333-341`) | pinned inside `ServerBootstrap.Build` (`:46-51`) | Formatting only | Pin in native init |
| 13 | Log sink | `GodotLogSink` | `NullLogSink` | Diagnostics only (a sink must not mutate) | n/a |
| 14 | Pacing | float accumulator `Update` | `StepOnce` | None (tick count, not content) | Use `StepOnce` |

---

## 9. JIT vs NativeAOT determinism hazards in the sim (sites and verdicts)

Method: analyzer build (CHM/IL counts above), targeted greps over `godot/src/{Core,Combat,Economy,Navigation,AI,Effects,Dsl,Multiplayer/Server}`
excluding editor/UI code, plus the experiment in section 10.

| Hazard | Sites | Verdict |
|---|---|---|
| **float in tick-time code (AI scoring)** | `AiOpponentSystem.cs:91-92,111,179,544-654,710` (`float` scores, `Math.Min(1f, (float)n/(threshold*2))` `:621`, floor `0.01f`) | IEEE-754 single, scalar; RyuJIT/ILC emit SSE scalar, no contraction. **RAN**: AI-on run identical JIT vs AOT. The AI golden is Windows-only because Windows vs Linux is the open question (`AiActiveGoldenTests.cs:57-66`), not JIT vs AOT. Keep the main trial AI-off; add a second AI-on scenario |
| **float to Fixed in tick time** | `BuildingSystem.cs:40-45` (fallback stats), `:461-462,838,1041,1269,1496` (`Fixed.FromFloat(def.TrainTime/Hp/...)` at train/placement), `VeterancySystem.cs:258-265`, `EntityWorld.cs:1652`, `FlowFieldComputer.cs:34` (static ctor `Fixed.FromFloat(0.7071068f)`), `FlowField.cs:~38` (`ARRIVE_SQR`), `SpatialHash.cs:21-25`; load-time: 44 sites in `ScenarioApplier.cs` | `FromFloat` is `(int)(value * 65536)` (`FixedPoint.cs:27`): truncating cast. Same on both for in-range values. **Out-of-range / NaN conversion differs between .NET versions** (.NET 9 made it saturating; UNVERIFIED here) so pin the NativeAOT compiler to 8.0.x to match Godot's runtime (`godot.csproj` net8.0) |
| float math functions | only `MathF.Sqrt` at `FogOfWarSystem.cs:201` (unfolded); no `Sin/Cos/Pow/Exp/Log` in the sim | Sqrt is correctly rounded. No libm divergence surface |
| `string.GetHashCode` / randomized hashing feeding order | none in the sim path. Custom stable hashes: `Fixed.GetHashCode` = Raw (`FixedPoint.cs:182`), `FixedVec3.GetHashCode` (`:421`), `GraphEdge` | None. Note a .NET `Dictionary` enumerates in insertion order regardless of string hash randomization |
| Dictionary/HashSet enumeration | analyzer CHM0002: 10 sites, all validators/writers/LLM (`FactionValidator.cs:207`, `FactionWriter.cs:701,831,842`, `ResearchValidator.cs:348`, `TechTreeValidator.cs:173`, `SkirmishSetupToScenario.cs:165`, `GraphStructureGate.cs:365`, `LLMService.cs:1033`) except `FlowFieldSystem.cs:273` (LRU scan; stamps are unique so order-independent). Tick-time `Dictionary<string,int>` cost maps in `ResourceStore.CanAfford/Spend/Add` (`:191-240`) are enumerated, but the operations are commutative | Low. No removal-order dependence found in tick systems |
| unstable sort | CHM0003: `GraphStructureGate.cs:196` (editor). `Array.Sort` in `ScenarioApplier.cs:716-750` uses a strict total order with index tie-break by design | None |
| Parallel / threads / async | none in tick code. `Persistence/BackgroundSaveWriter.cs:51-117` (`Task`, `lock`) is save-only | None for the trial. Native exports must be single-threaded per handle |
| culture-sensitive parsing/formatting | CHM0006: `FixedPoint.cs:185` (debug `ToString`), `UnitCardText.cs:25`, `FactionValidator.cs:473` (messages); `MatchSeedProducer.cs:88` uses `Ordinal`. No `ToLower/StartsWith(string)` in tick code | Pin invariant culture in native init (`ServerBootstrap.cs:46-51` pattern) and set `InvariantGlobalization` (the prototype did) |
| DateTime / Stopwatch / Guid / Environment | none in the sim set except off-tick `ContentPackager.cs:289,637,801`, `ContentPackageManifest.cs:116`, and `MatchSeedProducer.cs:99` (env var, offline reset only). `BannedSymbols.txt` bans them with a zero baseline | None |
| static initialisation order | one static ctor `FlowFieldComputer.cs:32`; static readonly `Fixed` tables elsewhere; mutable static only `CanonicalModelHash._graphMemo` (`:549`, load-time cache) | None; NativeAOT runs static ctors lazily like JIT |
| Span / vectorised paths | none in tick code. `stackalloc` in `EffectNodeJsonConverter.cs:586` (load), `ReadOnlySpan` in `TriggerFireLog.cs:100` (debug). ILC targets a baseline ISA while the JIT uses AVX2 for BCL internals (memmove, SequenceEqual) but those are value-exact | None found |
| reflection / trimming | STJ reflection (IL2026 62 + IL3050 65 unique), enum `ToString/TryParse` (`ScenarioApplier.cs:299`, `DamageTable.cs:213,225`) | STJ: **blocker, RAN** (section 0.4). Enum parse worked under AOT |
| exceptions across the boundary | `SimulationLoop.cs:155-170` throws on tick-boundary invariants; loaders throw `JsonException` | Native exports must catch everything and return codes |
| unfolded-state blind spots | `Flags`, `CommandState`, `MoveTarget`, projectiles ... (section 4) | Not a JIT/AOT hazard but affects how fast a port bug is seen |

---

## 10. Prototype results (scratch, not in the repo)

Program: `C:/Users/MD_Ki/AppData/Local/Temp/claude/D--Projects-Project-Chimera/6de76155-94ab-440a-84fd-59aa5580f954/scratchpad/proto/`
(`Proto.csproj` imports `godot/SimSources.props` + `UI/BuildingNavFootprint.cs`; `Program.cs`; `gen_scenario.py` writes
`trial_1000.json`). It composes exactly like `MainScene.BuildHeadlessServerSimHost` / `ServerBootstrap.Build`, then adds what the
headless path skips (seed pin, AI plan, flow-field init, path delegates), applies scripted orders through `OrderApplier.Apply`
before `StepOnce`, and folds `SimChecksum.Compute` each tick. Global.json (SDK 8.0.419) copied in; net8.0.

### 10.1 Results (RAN)

| Case | Result |
|---|---|
| Full content, Release JIT, AI off, flow fields, 1,440 ticks, per tick | p50 1.11 ms, p95 2.37 ms; two in-process runs identical; fold `0xEAA8C56C` |
| Same, **Debug** JIT | per-tick checksums byte-identical to Release (`cmp`); p50 13.1 ms, p95 30.7 ms, max 37 ms (the lite-content Debug run hit one 228 ms tick: JIT/GC) |
| Same without flow-field wiring | different match (alive P1/P2 at 600/900: 354/310, 203/146 vs 455/433, 290/285) |
| AI on (`OfflineDefault`) | no verdict by tick 1,500; buildings 2 to 7; P2 ore drains 1000 to 130 by tick 1,200 |
| First verdict, AI off, scripted layout | tick **1,499** (P1 wins): the run length must stay below it |
| Timeline (AI off) | tick 300: alive 499/500, 58 attacking; 600: 455/433, 361; 900: 290/285, 342; 1200: 124/155, 110 |
| Orders issued | 2,077 over batches at ticks 0 (960), 150 (110), 300 (60), 450 (60), 600 (848), 900 (32), 1200 (7) |

### 10.2 JIT vs NativeAOT (RAN, "lite" content)

NativeAOT cannot load today's content (below), so a "lite" mode builds the same factions/scenario with `JsonDocument` only
(scalar stats, no abilities, `DamageTable.Default`) and runs through the same `ServerBootstrap.Build`:

| Compare | Result |
|---|---|
| Release JIT vs **NativeAOT** (`PublishAot`, win-x64, ILCompiler 8.0.25, VS 2026 linker), AI off, 1,440 per-tick checksums | `cmp` identical |
| Same with **AI on** (float scoring path) | `cmp` identical, fold `0xB06765D0` both |
| Debug JIT vs Release JIT vs AOT (lite) | identical |
| Pre-tick hashes JIT vs AOT: `StartStateHash`, `CanonicalModelHash`, `ContentHash`, `RulesetHash`, `MatchAgreementHash` | identical (`0x9E8CE368C5BFA7AC`, `0xDE2922D1DCFAA487`, `0xB3F975D34C33F06C`, `0x1EB102FD18C50DDD`, `0xB0B53D6A8B7D916E`) |
| AOT per-tick cost | p50 1.09 ms vs JIT 1.14 ms |
| AOT with today's loaders | `NotSupportedException: Deserialization of types without a parameterless constructor ... Type 'ProjectChimera.Core.Definitions.FactionDefinition'` at `FactionDefinition.LoadFromFile` |
| AOT + rd.xml rooting the assembly | `NotSupportedException: 'System.Text.Json.Serialization.Converters.ArrayConverter`2[System.Single[],System.Single]' is missing native code or metadata` |

Caveats: same machine and OS only (Windows 11, Ryzen 5 5600); the lite run excludes abilities and modifiers; Linux AOT not
tested; real Godot not run. The AOT toolchain needed `vswhere` on PATH for the link step (`C:\Program Files (x86)\Microsoft Visual
Studio\Installer`); ILCompiler 8.0.25 was already in the NuGet cache.

---

## 11. Proposal: the 1,000-unit scenario `trial_1000`

**Goal**: one scripted match, identical per-tick checksums in (1) real Godot, (2) the .NET test harness, (3) NativeAOT called from C++.

| Item | Value |
|---|---|
| Content | shipped factions only: slot 0 `alpha`, slot 1 `beta` (`res://resources/data/factions/...`), shipped abilities, shipped `damage_table.json` |
| Map | new `godot/resources/data/scenarios/trial_1000.json`: flat (`terrain_ref` empty, no props/water/slope), `map_bounds` 120, `win_condition` DestroyAllBuildings, start 1000 ore / 500 crystal, one pre-built `CommandCenter` per side at (-100,0) and (100,0), 4 ore nodes per side at x = +/-104, z = +/-14 |
| Units | **1,000** = 480 combat + 20 workers per side. P1: heavy_infantry 50, infantry 150, scout 30, archer 120, mage 60, siege_engine 30, griffin 40. P2: bulwark 50, footsoldier 150, ironclad 30, crossbowman 120, rune_caster 60, war_machine 30, wyvern 40. Workers: `worker` / `forgehand`, 20 each near their base |
| Spawn | declarative `units` rows (the shipped path). Each side 20 columns x 25 rows at spacing 2.0 (z = -24..24), front column at |x| = 20, filled front-to-back in roster order above, so melee fronts the line and siege/air sit behind. Entity ids follow `CanonicalUnitOrder` (`ScenarioApplier.cs:716`), independent of row order |
| Factions/AI | AI off (`AiControlPlan.None`) in the main scenario; AI-on variant as a second run |
| Seed | pinned `0xC0FFEE1234567890` (every leg; Godot via `CHIMERA_MATCH_SEED`) |
| Length | **1,440 ticks** (48 s) at 30 tps; stop below the first verdict (tick 1,499 in the prototype; assert `WinState.Verdict` stays 0) |
| Checksum sampling | record **every tick** (1,440 lines, ~14 KB, like the existing 300-line goldens at interval 1); the cross-leg gate uses ticks 1-10 then every 30th (matching Godot's native cadence of every 60); first-contact window 290-310 per tick; and the last tick. Plus the five pre-tick hashes at tick 0 |
| Orders (applied before the `StepOnce` that makes tick T+1; flagged ones use 0x80 queued) | t=0: all combat units AttackMove toward the enemy line, goals fanned over 13 z-cells (`((k%13)-6)*2`) so the flow-field cache is exercised; t=150: P1 scouts Move (0,-90), P1 griffins and P2 wyverns AttackMove flanks; t=300: scouts Move then **queued** AttackMove; t=450: siege forward; t=600: all survivors AttackMove to (+/-85, 0); t=900: P1 heavies HoldPosition, P2 bulwarks Stop; t=1200: P1 archers AttackTarget (packed refs of live P2 units); (t=1800 final push exists in the script but lies beyond the run) |
| Coverage | movement + flow fields + separation, melee/ranged/siege/air combat, projectiles and splash, self-passive modifiers (beta units carry `furnace_trickle`), shift queue, hold/stop, forced target, economy tick for workers |
| Not covered (state it) | RNG draws, active ability casts, training/production, building placement, heroes/items, triggers, terrain, fog consumers. Phase-2 additions: `CastAbility` fireball from mages, a `Train` order, a crit/dodge content variant, an AI-on run |

Reference values from the prototype (full content, Release JIT; **not** a golden, produced by scratch code; use only to sanity-check
the first implementation): tick 1 `0E7082D2`, 2 `3CD75213`, 3 `CD1BECB8`, 60 `71BA9D54`, 120 `C7F82CA7`, 300 `3A69E1EB`,
600 `9F44372D`, 900 `551EDCDB`, 1200 `DA906CD4`, 1440 `2CFCF09E`; start-state hash `0xA68FAB39663A44E2`, content hash
`0x9DC8ADE0E53877DD`, agreement hash (AI off) `0x7404669C0D1C922B`.

**Orders as data.** Do not re-implement the order script in three languages. Run the C# script once to emit
`trial_1000.orders.csv` (`tick,faction,unit_ref,command_byte,x_raw,z_raw,slot`, ~2,100 rows) and have every leg replay the file through its own
`submit_order`. This also makes "one way in" literal. The `AttackTarget` rows depend on the live set at tick 1,200; the file
freezes them.

---

## 12. Proposal: one shared composition, then the C API

### 12.1 Build one `SimSession` (new, Godot-free, in `godot/src/Core/Sim/`)

`SimSessionBuild(projectRoot, scenarioRes, seed, flags)` = the exact recipe the prototype uses, so all three legs share it:
load defaults + per-slot faction defs (`FactionDefinition.LoadFromFile`), `ResolveAbilities` + `UnitTagValidator.ValidateAndDropUnits`
(as `ServerBootstrap.cs:60-73`), `DamageTable.Load`, `AbilityRegistry.LoadFromDirectory`, `ItemRegistry.LoadFromDirectory`,
`ScenarioSerializer.LoadFromFile`, `PlayerCountPolicy.SimActivePlayers`, then `SimulationHost.Create(... aiLevel, abilityRegistry,
itemRegistry)` + validator + applier like `ServerBootstrap.Build`, then `World.Rng.Seed(seed)`, `SetAiControlPlan`,
`FlowFields.SetBuildingFootprintSource(BuildingNavFootprint.ObstacleExtentSource(...))`, `FlowFields.SetStaticBlocked(grid?.Blocked)`,
`FlowFields.RebuildObstacles(Buildings)`, `Steering.SyncBuildingBaseline()`, and the `OnRequestPath/OnRequestAttackMove/OnCancelPath` closures
(`Steering.RequestPath(world, id, new FixedVec3(Fixed.FromFloat(x), 0, Fixed.FromFloat(z)))`, matching `FlowFieldBridge.ToSim`).
It adds `ApplyOrder(Faction, in UnitOrder)` (= `OrderApplier.Apply` with all handles, as `LockstepManager.cs:402,438`) and `Step`.
Two edits to existing files will be needed in the build phase: add `src/UI/BuildingNavFootprint.cs` to `SimSources.props` (a
single-file include, the existing precedent), and register the new golden in the test csproj. The Godot runner (12.3) and the
tests then call this class instead of copying recipes. Optionally make `ServerBootstrap` delegate to it so the server and clients
cannot drift.

### 12.2 Minimal C API (NativeAOT, `[UnmanagedCallersOnly(EntryPoint = ...)]`, Windows x64 first)

Handle = opaque pointer from a `GCHandle`. Every export catches all exceptions and returns a negative code; the last message is
fetched with `chimera_last_error`. Positions and values cross as raw `Fixed` ints (Unreal divides by 65536.0), which keeps floats
out of the boundary.

| Export | Purpose | Calls |
|---|---|---|
| `int32 chimera_abi_version()` | `(SimChecksum.AlgoVersion << 16) | abi` | `SimChecksum.AlgoVersion` (`:372`) |
| `h chimera_create(content_root, scenario_res, uint64 seed, uint32 flags, err_buf, err_cap)` | build + apply; flags: AI on, flow fields on (default) | `SimSession.Build` (= `ServerBootstrap.Build` `:38` + the extras in 12.1) |
| `void chimera_destroy(h)` | free pins and handle | n/a |
| `uint32 chimera_step(h, n)` | advance n ticks, return `CurrentTick` | `SimulationHost.StepOnce` (`:598`), `CurrentTick` (`:200`) |
| `void chimera_set_checksum_interval(h, n)` | 0 off, 1 every tick, 60 game default | `SimulationHost.ChecksumInterval` (`:203`) |
| `uint32 chimera_checksum_now(h)` / `uint32 chimera_last_checksum(h, uint32* tick)` | checksum at the current tick / last scheduled | `SimChecksum.Compute` (`:378`), `LastChecksum` (`:201`) |
| `void chimera_pre_tick_hashes(h, uint64 out[5])` | start-state, canonical model, content, ruleset, agreement | `StartStateHash.Compute`, `CanonicalModelHash.Compute`, `ContentHash.Compute`, `RulesetHash.Compute`, `MatchAgreementHash.Compute` |
| `int32 chimera_submit_order(h, faction, unit_ref, uint8 cmd_flags, x_raw, z_raw, slot)` | the one way in, offline apply-now | `SimSession.ApplyOrder` -> `OrderApplier.Apply` (`NetworkCommand.cs:300`) |
| `int32 chimera_submit_merged_tick(h, bytes, len)` | lockstep/server merged packet | `MergedTickApplier.Apply` (`:32/:75`) |
| `int32 chimera_world_view(h, ChimeraWorldView* out)` | zero-copy render read | pins `EntityWorld` arrays once (they are `readonly` fields, never reallocated: `EntityWorld.cs:296-316`, "capture-once alias" note at `ClearForReset`) |
| `int32 chimera_read_units(h, ChimeraUnit* out, cap)` | optional packed copy of alive units | loop over `HighWaterMark` (`EntityWorld.cs:1112`) with `IsAlive` |
| `int32 chimera_read_buildings(h, ChimeraBuilding* out, cap)` | buildings | `BuildingStore` arrays (`BuildingStore.cs:46-88`) |
| `int32 chimera_drain_events(h, ChimeraEvent* out, cap)` | hit/kill/cast cues, then clear | `CombatEventQueue.Count/Get/Clear` (`CombatEventQueue.cs:194,197,287`); the queue caps ambient events at 256 (`MAX_AMBIENT_EVENTS`), so drain at least once per tick batch |
| `int32 chimera_verdict(h, faction)` | 0 none, 1 won, 2 lost | `WinState.Verdict` (`WinStateStore.cs:48`) |

`ChimeraWorldView` (all pointers valid until `chimera_destroy`): `count` (= `HighWaterMark`), `alive_count`, and base pointers to
`Position`, `PrevPosition`, `Velocity` (`FixedVec3[]`, 3 x int32 each), `Health`, `EffectiveMaxHealth` (`Fixed[]`),
`FactionOf`, `Flags`, `MeshType`, `CommandState` (byte arrays), `Generation` (int32), plus `interp_alpha` computed on the Unreal
side from its own accumulator (Godot interpolates `PrevPosition` to `Position`, `UI/MultiMeshBridge.cs:203-205`). `ChimeraUnit` is the
packed alternative: `{int32 id, ref; int32 pos[3], prev[3], vel[2]; int32 hp, max_hp; uint8 faction, mesh_type, flags, command}`.
Entity `Flags` bits: Alive 1, Moving 2, Attacking 4, Phased 8 (hidden inside a building; skip when drawing, `MultiMeshBridge.cs:175`).

**Facing and animation state do not exist in the sim.** Godot draws position only (`MultiMeshBridge.cs:196-209`, no rotation).
Unreal derives facing from `Velocity` (or `Position - PrevPosition`) and the animation state from `Flags` (Moving/Attacking),
`Health`, and `UnitKilled`/hit events. `MeshType` indexes the owning faction's unit list (`ScenarioApplier.cs:681-682`), which is
how Unreal picks a mesh and a clip set.

Build notes: a separate project (`net8.0`, `PublishAot`, `NativeLib=Shared`, `InvariantGlobalization`, `AllowUnsafeBlocks`) that imports
`SimSources.props`; the Godot-free guarantee already holds (the analysis project compiles without Godot). Pin the ILCompiler version to
8.0.x (8.0.25 is cached). Content loaders must be on source-generated STJ contexts first; keep reflection options for editor code.

### 12.3 The three legs

1. **.NET harness (reference)**: new golden test `Golden/Trial1000Scenario.cs` + `Trial1000GoldenTests.cs` using `SimSession`, recorded with
   `CHIMERA_GOLDEN_RECORD=1`, file `Golden/trial-1000.golden.txt`; plus the pre-tick hash file and the orders CSV.
2. **Real Godot**: a small Godot scene `res://scenes/sim_trial.tscn` with a `SimTrialRunner : Node` (new file) whose `_Ready` parses
   `-- --trial <scenario> --orders <csv> --ticks N --seed S`, runs `SimSession`, prints one `[Trial] tick hash` line per tick, and quits.
   Because it is not `MainScene`, the dedicated-server branch (`MainScene.cs:361`) is not hit and `PathRequestSystem` is not in the tree.
   It proves Godot's runtime and the `godot.csproj` build of the same sources. Full-`MainScene` fidelity (items 5, 6, 3 above) needs a
   follow-up: a pre-tick hook in `SimulationLoop` (a debug-only `Action<uint>`) plus the skirmish launch with both slots human so the AI
   plan is empty. Treat that as optional.
3. **Unreal**: a C++ module loads `chimera.dll`, calls create/submit/step, compares `chimera_last_checksum` per tick against the golden
   (automation test), and renders from `chimera_world_view`. Pass = zero mismatching ticks plus matching pre-tick hashes.

---

## 13. Baseline check and ordered next steps

Baseline (RAN, end of this session): 7,122 pass / 1 fail / 1 skipped of 7,124 (see the evidence table: the one failure is a guard on a
retired tooling file, not the sim). HANDOFF's 6,392 is out of date; the new trial golden should be added against 7,124, and the
failing guard needs its own fix or deletion (outside this phase). `git status` stayed clean apart from `docs/unreal-move/trial-checks/`.

Suggested order for the build phase (smallest proof first):
1. Source-generate STJ for the load path (faction, ability + effect converter, scenario, damage table, items) behind the existing option sets; the goldens and `ContentHash` must not move.
2. Add `SimSession` + `BuildingNavFootprint` to `SimSources.props`; record the `trial_1000` golden and orders CSV (decide the final order script with Alec only if the layout changes).
3. NativeAOT project with the exports; a C# test that calls the exports through function pointers and compares to the golden (no Unreal needed).
4. Godot `sim_trial` runner; compare its log to the golden.
5. Unreal module consuming the DLL; same comparison; then render.

---

## 14. Risks

1. **Two construction recipes exist** (ServerBootstrap vs MainScene). Until `SimSession` is the single path, "matches Godot" is an argument, not a proof (section 8 rows 1-5).
2. **`PathRequestSystem` frame writer and the offline `SelectionSystem` direct path** are Godot-only deviations from "one way in"; their effect on a real match is UNVERIFIED.
3. **Unfolded state** (`CommandState`, projectiles, `MoveTarget`) delays localisation of a divergence; plan a test-side wide hash.
4. **Runtime-version skew**: Godot runs net8.0 JIT; building the AOT library with ILC 9/10 changes float-to-int conversion semantics for out-of-range values (UNVERIFIED) and library behaviour; pin 8.0.x.
5. **Library cannot be unloaded** (spec): restart the Unreal editor after each rebuild; budget for it in the trial loop.
6. **Debug-config sim cost** (13-14 ms p50, 228 ms worst) if anyone profiles in Godot's editor build; irrelevant to correctness.
7. **Scenario can resolve** if the layout changes: re-measure the first-verdict tick whenever units, positions or order times change; assert `Verdict == 0` in the test.
8. **RNG, abilities, training, triggers not exercised** by the base scenario; the trial would pass with those paths broken under AOT. Add the phase-2 variants before declaring the library production-ready.
9. **Terrain editing (check c)**: the sim samples height once at spawn into a folded array and builds a static pathability grid at match start (`EntityWorld.cs:1422,1430`, `ScenarioApplier.cs:120`); runtime terrain edits are not a sim concept today.

## 15. Open questions

1. Should `ServerBootstrap` be rewritten to delegate to `SimSession` (one recipe for server, tests, Godot and native), or stay as is with `SimSession` beside it? Recommendation: delegate, it removes the drift class.
2. Is the Godot leg required to go through the full `MainScene` (needs a pre-tick hook), or is the `sim_trial` scene acceptable for the trial?
3. Do we want the trial scenario to include ability casts and training (more coverage, but each needs `AttackTarget`-like state frozen in the CSV and more of the AOT-sensitive code), or keep the base trial narrow and add phase-2 later?
4. Which source set for the native library: `SimSources.props` + `BuildingNavFootprint.cs` + `SimSession` only (Godot-free, recommended), or also the server pieces (`ServerHost`, already globbed)?
5. Windows-only first, or also a Linux AOT build for the match server (the WSL gate exists; AI float path is Windows-only golden today)?
6. Delete `PathRequestSystem` (dead path apart from its frame-writer) before parity claims? It is outside this phase.
7. P2 workers never earned ore in the prototype (P2 ore stayed 1000 while P1 rose): not investigated; confirm whether beta workers gather in the shipped faction before using economy as coverage.
