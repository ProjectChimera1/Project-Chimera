# Plan A: the NativeAOT sim drives 1,000 units in Unreal with the same checksums as Godot

Trial check (a), 2026-10-01, revision 2 (after critique; see the Review log at the end). The plan of record for implementer
agents. Inputs: research `r1-stj-aot-inventory.md` (R1), `r2-sim-entry-checksum-scenario.md` (R2), `r3-ue-host-and-instancing.md` (R3),
the Unreal Spec ("Non-negotiables", "How it is built"), `HANDOFF.md`. **RAN** = executed by the plan author today, **UNVERIFIED** =
only a build or run can settle it.
Shorthands: **R** = `D:/Projects/Project_Chimera`, **G** = `R/godot`, **NAT** = `G/ProjectChimera.Sim.Native`, **ST** = `R/tools/sim-trial`,
**P** = `D:/Projects/Chimera-Unreal/ProjectChimera`, **E** = `D:/Epic Games/UE_5.8/Engine/Source/Runtime`, **OUT** = `D:/Projects/Chimera-Unreal/TrialOut/a`
(git-ignored, `D:/Projects/Chimera-Unreal/.gitignore`), **PROOF** = `R/docs/unreal-move/trial-checks/a-native-sim`, **PA** = `R/docs/unreal-move/trial-checks/research/proto-a`
(prototypes preserved from the session scratchpad on 2026-10-01, 41 files + README), **LOCK** = `bash D:/Projects/Chimera-Unreal/ue_lock.sh`
(Git Bash only, see §4 Rules), **PS** = `pwsh -NoProfile -ExecutionPolicy Bypass -File` (PowerShell 7.6.6, RAN),
**GODOT** = `C:/Godot/Godot_v4.6.3-stable_mono_win64/Godot_v4.6.3-stable_mono_win64_console.exe`.

## 0. What "done" means, in one paragraph

Sim content loading moves from reflection System.Text.Json to one source-generated context with no golden, hash or test outcome
moving. One shared Godot-free composition, `SimSession`, builds a match the way Godot's match start does (known, checksum-neutral
deltas listed in §3.2), and a gating probe inside the real `MainScene` proves its start state equals SimSession's. A scripted 1,000-unit
battle (`trial_1000`, 1,440 ticks, 2,077 frozen orders) then runs in four places: the .NET CLI (JIT), a C++ console calling the NativeAOT
`ChimeraSim.dll` through its C ABI, Godot 4.6.3 headless, and an Unreal `-game` run that draws every unit with instanced static meshes at a
fixed 30 Hz with interpolation. All four write one trace format and equal the committed golden at **every one of the 1,440 ticks**, in
one final evidence run from one clean commit and one DLL. Unreal's drawing is checked against the sim independently (pixels and a
fresh read), its frame rate is measured against Alec's bar, and Alec gets parity, screenshot, perf and video artefacts on his phone as
they are produced.

## 1. Definition: "the same checksum as Godot"

Match **M** = (`trial_1000.json` + `godot/resources/data` at one commit, `trial_1000.orders.csv`, seed `0xC0FFEE1234567890`, AI plan `None`,
1,440 ticks); variant **M-ai** = the same with AI plan `OfflineDefault`. **G(t)** = the `uint32` `SimChecksum.Compute` (AlgoVersion 29,
`G/src/Core/SimChecksum.cs:372,378`) returns after the `StepOnce` that produces tick *t* in the Godot leg. **H0** = the five 64-bit
pre-tick hashes (start-state, canonical model, content, ruleset, agreement; R2 §4) plus **tick0** = `SimChecksum.Compute` of the world
before the first step. Unreal has "the same checksum as Godot" when its trace **U** satisfies **U(t) == G(t) for all t in 1..1440** (exact,
no sampling), its H0 and tick0 equal Godot's, and its header fields equal the golden's meta file (§3.5). The same holds for the managed and
C++ legs, for M and for M-ai (each against its own meta file; the two differ only in `ai` and `hash.agreement`, which folds the AI plan,
`MatchAgreementHash.cs:62-67,107`).
"The Godot leg" = the sim compiled by `godot.csproj` (GODOT defined, Debug), executed by Godot 4.6.3's hosted .NET 8.0.25 and built
through `SimSession`. Since the only `#if` in the sim set is `FixedPoint.cs:359` (presentation conversions; grep RAN), that leg alone
proves only that Godot's hosting does not perturb the sim. Two additions tie it to the real game: **A8b** (gating) runs the real
`MainScene` composition on `trial_1000` and must reproduce SimSession's H0 + tick0 (6/6); **A14** (measured, reported, not gating) replays
the 1,440 ticks through full `MainScene` and reports its first divergent tick or 1440/1440. The verdict is worded: "same sim build and
runtime as Godot, same start state as Godot's MainScene; full MainScene parity: <A14 result>".
Why every tick: `SimChecksum.Compute` is a pure read (goldens run at interval 1, the game at 60, `SimulationLoop.cs:36`), so interval 1
finds a divergence at its first tick. The Godot leg also prints its native `[Checksum] tick=N hash=0x........` line every 60 ticks.

## 2. Facts the plan rests on

| # | Fact | Evidence |
|---|---|---|
| F1 | Sim set = `G/SimSources.props` (globs + single-file includes incl. `src/UI/FactionPalette.cs` etc., 3 removals), Godot-free, imported by Tests and Analysis | `SimSources.props:6-8,22-161`; `ProjectChimera.Sim.Analysis.csproj:43` |
| F2 | Only NativeAOT blocker: reflection STJ, 65 sites / 127 Roslyn diagnostics; no context exists today | R1 §0, §4; grep `JsonSerializerContext` in `G/src` empty (RAN) |
| F3 | Posture builders to bind a resolver to: `Base()` and two strict-enum lines | `ContentJson.cs:59-63,161,175` |
| F4 | Silent AOT hazard: reflection member walk in `MixUnknownEffect`; dead for shipped content, exercised only by DW-449 probe kinds in tests | `CanonicalFold.cs:146-148,173-205`; `EffectFoldCompletenessTests.cs:182-260` |
| F5 | Migration prototype: 25 files edited, 3 new, 13 guard tests, AOT output identical to JIT; `git apply --check` of both diffs rc 0 at 5e6bc03d | R1 §7-8; `PA/r1_trial_migration_{src,tests}.diff` (RAN) |
| F6 | Real baseline 7,124 tests / 7,122 pass / 1 fail (guard on the retired BMAD file) / 1 skipped | R1 §0.6; `Meta/ToolingGateGuardTests.cs:68-74`; `c794681b` |
| F7 | Godot runs .NET 8.0.25: runtimeconfig `net8.0`, `rollForward: LatestMinor` (stays in major 8). Installed: 8.0.25, 9.0.14, 10.0.3, 10.0.12; 8.0.25 is the only 8.0.x, so removing it makes Godot fail to start rather than roll to 9/10 | `G/.godot/mono/temp/bin/Debug/ProjectChimera.runtimeconfig.json`; `dotnet --list-runtimes` (RAN) |
| F8 | ILCompiler 8.0.25 packs cached; SDK pinned 8.0.419 | `~/.nuget/packages/microsoft.dotnet.ilcompiler/8.0.25`; `R/global.json` |
| F9 | `NativeLib=Shared` publish emits plain C exports; a cl.exe console loads one; `vcvars64.bat` needs the VS Installer dir on PATH. `pefile` is not installed; the stdlib reader `PA/pe_exports_review.py` lists `DotNetRuntimeDebugHeader, smoke_add` | `PA/aotsmoke`, `PA/cppload` (RAN) |
| F10 | `StepOnce`: snapshot Prev, 21 systems, invariants, `CurrentTick++`, checksum when `tick % interval == 0`; no public "checksum now" | `SimulationLoop.cs:207-225`; `SimulationHost.cs:201-203` |
| F11 | `SimulationHost.Create(log, factions, def1, def2, damage, aiLevel = Normal, registry, itemRegistry)` | `Sim/SimulationHost.cs:213-222` |
| F12 | `ServerBootstrap.Build` validates and applies Godot-free, pins invariant culture; passes no `ItemRegistry` (host gets Empty) and default AI level | `ServerBootstrap.cs:38-117`, `:77-80` |
| F13 | Godot additionally wires footprint source, static-blocked cells, `RebuildObstacles` + `SyncBuildingBaseline`, path delegates, AI plan, seed, loaded items | `NavigationPhase.cs:95-96,133-157`; `FlowFieldInitPhase.cs:21-26`; `MatchLifecycleController.cs:129-135`; `MainScene.cs:434,527-535,2990-3009` |
| F14 | Path delegates round-trip `Fixed.FromFloat(x)` (lossless below 256 m) | `FlowFieldBridge.cs:76-77`; R2 §3 |
| F15 | `BuildingNavFootprint.cs` is Godot-free but float-based (`float`, `MathF.Ceiling`), documented as deliberately outside the analyzed set | `BuildingNavFootprint.cs:28-30,38-41,98-100,134-140`; Tests csproj `:61` |
| F16 | One way in: `OrderApplier.Apply(world, in UnitOrder, expectedFaction, …)` returns void and silently no-ops on dead/unowned units | `NetworkCommand.cs:244-330` |
| F17 | Render-side arrays `Flags, Position, PrevPosition, Velocity, Health, EffectiveMaxHealth, FactionOf, MeshType`; `HighWaterMark`; `PackRef` = Generation<<12 | `EntityWorld.cs:173-185,295-316,673,1112,1892-1906` |
| F18 | `MovementSystem` skips the `Velocity` write when net force is zero, and clamps to `EffectiveMoveSpeed` (units/s) | `Navigation/MovementSystem.cs:159-168` |
| F19 | Prototype `trial_1000` (Release JIT): tick 1 `0E7082D2`, 60 `71BA9D54`, 300 `3A69E1EB`, 1440 `2CFCF09E`; JIT and AOT lite traces byte-identical (main and ai) | `PA/proto/full_every1.txt`; `cmp` (RAN) |
| F20 | Shipped scenarios dir is enumerated by hygiene tests, so trial content lives elsewhere | `ShippedScenarioHygieneTests.cs:40` |
| F21 | `godot.csproj` compiles every `**/*.cs` under `G/` except removed folders | `G/godot.csproj:14-26` |
| F22 | DLL API `GetDllHandle`, `GetDllExport`; never free (NativeAOT cannot unload) | `E/Core/Public/GenericPlatform/GenericPlatformProcess.h:305-311` |
| F23 | ISM API: `AddInstance(:271)`, `PreAllocateInstancesMemory`, `BatchUpdateInstancesTransforms`, `GetInstanceTransform(:338)`, `UpdateInstanceTransform(:375)` | `E/Engine/Classes/Components/InstancedStaticMeshComponent.h` |
| F24 | `?game=` overrides the map's game mode; a class that fails to load logs `Failed to load game mode` and silently falls back | `E/Engine/Private/GameInstance.cpp:1514-1553` |
| F25 | `FScreenshotRequest::RequestScreenshot` is serviced at the next draw; `OnScreenshotRequestProcessed()` fires after | `E/Engine/Public/UnrealClient.h:219,266` |
| F26 | `FParse::Value(…, FString&, bShouldStopOnSeparator = true)` truncates at `,` or `)`; the `uint64` overload parses decimal digits only (`0x…` → 0, returns true) | `E/Core/Public/Misc/Parse.h:40-41,71`; `E/Core/Private/Misc/Parse.cpp:552-573`; `FCString::Strtoui64` `CString.h:558` |
| F27 | `FPlatformMisc::GetSHA256Signature` has no Windows implementation (`checkf(false)`) | `E/Core/Private/GenericPlatform/GenericPlatformMisc.cpp:2019-2023` |
| F28 | Pawnless PC: `AutoManageActiveCameraTarget` calls `SetViewTarget` unless a `ACameraActor` auto-activates for the player | `E/Engine/Private/PlayerController.cpp:603-633`; `CameraActor.h:24` |
| F29 | Compile state: `GShaderCompilingManager->GetNumRemainingJobs()`, `FAssetCompilingManager::Get().GetNumRemainingAssets()` | `E/Engine/Public/ShaderCompiler.h:1214,1371`; `AssetCompilingManager.h:44,64` |
| F30 | CSV profiler (`CSV_CUSTOM_STAT`) is the look test's proven frame/GPU source; `RHIGetGPUFrameCycles` per frame is UNVERIFIED | `E/Core/Public/ProfilingDebugging/CsvProfiler.h:50,155`; `LookTest/tools/run_fps.ps1` |
| F31 | Look A_noLumen recipe (sun -32/120, 10 lux, fog 0.02/0.2, SSGI+SSR); look cameras yaw 90 (world −X = screen right); `CAM_Gameplay` auto-activates for Player0; map `LT_A_noLumen.umap` exists; baseline 76.2 fps with ~58 units + ~900 scatter | `P/LookTest/tools/lt_common.py:412-439`; R3 §1.4; `ls P/Content/LookTest/Maps` (RAN) |
| F32 | 16 sim units map to Tripo assets; unrigged GLBs of the 12 rigged ones exist in `D:/tripo-out` | `P/LookTest/run/roster.json`; `ls` (RAN) |
| F33 | Golden files carry a fixed header (`checksum_algo_version`, description, samples, re-baseline hint), not `key: value` trial fields | `GoldenChecksumReplay.cs:175-205` |
| F34 | R1's 22 AOT sequences were compared AOT vs JIT (fnv/last, 300 ticks), never against committed goldens; 35 goldens vs 36 builders, several from other drivers (merged N2-N4, energy-regen, hero-start-state, rebaseline guard) | `PA/spikeaot2/aot_out.txt`, `SpikeAot2.csproj`; `ls Golden` (RAN) |
| F35 | Repo sources are LF, no BOM (966 `i/lf`); `CanonicalFold.cs` is `-text` to git (raw NULs at bytes 11393, 11617 inside `MixUnknownEffect`) | `git ls-files --eol` (RAN); `godot/.gitattributes` |
| F36 | From PowerShell, `bash` = WSL (`WindowsApps\bash.exe`); Git Bash is `C:/Program Files/Git/bin/bash.exe`; `powershell` from Git Bash is 5.1 | RAN |
| F37 | `D:/Projects/Chimera-Unreal` is a git repo with **no remote**; it ignores `Binaries/`, `**/Content/` | `git remote -v` empty (RAN); `.gitignore` |
| F38 | `MainScene` has `[Export] ScenarioPath` and `ReplayPath` (startup replay); headless enters the server branch; `CHIMERA_MATCH_SEED` pins the seed; no skirmish setup ⇒ plan `OfflineDefault` | `MainScene.cs:258-267,361-399,2990-2994`; `MatchSeedProducer.cs:36` |
| F39 | Known timing/perf flakes: `LlmServiceLifecycleTests`, `LlmSupersededDraftCallbackTests`, `LlmServiceRepointTests`, `BalanceAnalysisGenerationTests`, `CanonicalModelHashPerfTests`, `ContentHashPerfTests` | R1 §8.3; memory note "CanonicalModelHashPerf flake" |
| F40 | `SimChecksum` folds no `Velocity`, `Flags`, `CommandState`, `AttackCooldown`, `MoveTarget`, `PrevPosition` | grep `SimChecksum.cs` (RAN) |

## 3. Design

### 3.1 STJ to source generation, and the identity proof (R1 §7, adopted)
- One `internal partial class ChimeraJsonContext : JsonSerializerContext` (`G/src/Core/Definitions/ChimeraJsonContext.cs`, Metadata mode, R1 §7.2's
  38 roots) as `TypeInfoResolver` on `ContentJson.Base()` and the four standalone option objects; facade `AotJson` with a reflection fallback only
  for an options object without a resolver; a closed `StrictEnumConverterFactory` (19 enums) replacing the two `JsonStringEnumConverter(null,false)`
  lines; private nested DTOs become `internal`; anonymous LLM bodies become named DTOs; `JsonArray.Add((JsonNode)x)`. Converter order stays.
  **Keep LF, no BOM** (F35). The native library keeps the full sim set (fixed decision); ILC trims unreachable LLM/HttpClient code.
- `MixUnknownEffect` (F4): when `!RuntimeFeature.IsDynamicCodeSupported`, throw `NotSupportedException` before any reflection; JIT unchanged, so the
  DW-449 probe tests and every hash stay. This is a **deliberate, documented JIT/AOT difference**: only reachable when a developer adds an effect kind
  without a fold arm, which `EffectFoldCompletenessTests` fails in Tier-1; under AOT it fails closed (create returns -6). Suppress IL2075 there with a
  justification. In the same edit replace the two raw NUL bytes with the `\0` escape (same compiled string) so git treats the file as text again.
- **Identity proof**: (1) Tier-1 zero failures with only R1 §7.6's 4 assertion edits; (2) R1's 13 guards incl. the reflection oracle; (3) no modified or
  deleted `*.golden.txt`, no `AlgoVersion` line changed; (4) A6's AOT replay; (5) the real Godot game still boots through the migrated loaders (A1).

### 3.2 One composition (`SimSession`), shared hashes, orders as data
- `G/src/Core/Sim/SimSession.cs` (Godot-free, `#nullable enable`): `Build(contentRoot, scenarioPath, seed, aiOn)` = load slot factions (`res://` →
  `contentRoot`), `DamageTable.Load`, `AbilityRegistry.LoadFromDirectory`, **`ItemRegistry.LoadFromDirectory(contentRoot/items)`**,
  `ScenarioSerializer.LoadFromFile`, `PlayerCountPolicy.SimActivePlayers`, `ServerBootstrap.Build(…, itemRegistry: items)` (new optional last
  parameter passed by name to `Create`; default null keeps every caller byte-identical), `aiLevel` left at `Normal` = Godot's default (`MainScene.cs:252`),
  then F13 in this order: `SetAiControlPlan(aiOn ? OfflineDefault : None)`; `World.Rng.Seed(seed)`; footprint source with a resolver identical to
  `NavigationPhase.cs:133-157`; `SetStaticBlocked(BuildPathabilityGrid(model, null)?.Blocked)`; `RebuildObstacles`; `SyncBuildingBaseline`; path
  delegates as `FlowFieldBridge.ToSim`. Plus `ApplyOrder(Faction, in UnitOrder)` (returns applied/dropped, §3.4), `Step()`, `PreTick()`.
- Test-only `internal sealed class SimSessionOptions { bool FlowFields = true; }` (the tests compile the sources, so `internal` is visible; nothing in
  the ABI reaches it).
- `G/src/Core/Sim/PreTickHashes.cs`: one helper computing the five hashes (initialDelay 4, the loaded item registry) plus tick0 through a new pure-read
  `SimulationHost.ComputeChecksumNow()`; used by SimSession, the CLI and the MainScene probe, so only the inputs can differ.
- `G/src/Core/Sim/WorldDigest.cs`: (a) `UnitsDigest` = FNV-1a over (id, pos) of every id < HighWaterMark, the same fields `read_units` copies;
  (b) `WideDigest` = FNV-1a over every `EntityWorld` SoA array (explicit list incl. the unfolded ones, F40) and the projectile store. A test pins that
  every public array field of `EntityWorld` is listed (reflection in the test only).
- Known deltas from Godot's match start, all checksum-neutral for `trial_1000` (no items placed, no triggers, fog is presentation): no
  `Fog.SetBuildingVisionSource` (`NavigationPhase.cs:104`), no `ScenarioDelegateBinder`, no `PathRequestSystem` frame writer (DW-681, F38/R2 row 5),
  orders through `OrderApplier` not `SelectionSystem`. Recorded in SimSession's class doc and the README.
- Move `UI/BuildingNavFootprint.cs` from the Tests csproj into `SimSources.props` (precedent: `src/UI/FactionPalette.cs`); update its class doc and
  the props header. Its floats and SimSession's delegate bridge add CHM0001/CHM0005 warnings: expected, counted, attributed (A3).
- `G/src/Core/Sim/OrderScript.cs`: CSV `tick,faction,unit_ref,cmd,x_raw,z_raw,slot` (ints; `cmd` incl. 0x80). Every leg applies all rows with
  `tick == CurrentTick` in file order, then steps. Orders are generated once (R2 §11) and frozen; C++ legs parse the CSV themselves.

### 3.3 Scenario `trial_1000` (R2 §11; content in `G/ProjectChimera.Sim.Tests/Trial/` because of F20)
alpha (slot 0) vs beta (slot 1), flat, `map_bounds` 120, CommandCenters at x = ∓100, 4 ore nodes a side; 480 combat + 20 workers a side (layout of
`PA/proto/gen_scenario.py`); seed `0xC0FFEE1234567890`; 1,440 ticks; order batches at 0/150/300/450/600/900/1200 (2,077 orders). Variant `ai` =
`OfflineDefault` (float AI path; P2 builds 2→7 buildings and spends ore, R2 §10.1). It uses shipped faction/ability/damage content, so **a balance edit
moves the trial goldens**: re-record only when `hash.content` changed in the same commit, otherwise a mismatch is a determinism bug (stated in the
golden test's failure message and the README). Not covered: RNG draws, casts, training orders, placement orders, heroes/items, triggers, terrain.

### 3.4 NativeAOT library and C ABI (`NAT/`, assembly `ChimeraSim.dll`)
- csproj: net8.0, `PublishAot`, `NativeLib=Shared`, `win-x64`, `InvariantGlobalization`, `AllowUnsafeBlocks`, `EnableDefaultCompileItems=false`,
  imports `..\SimSources.props`, `PackageReference Microsoft.DotNet.ILCompiler 8.0.25` (F7/F8). `publish.ps1` stamps `SourceRevisionId` = HEAD and
  `dirty` = non-empty `git status --porcelain -- godot tools/sim-trial`. `G/godot.csproj` gets `<Compile Remove="ProjectChimera.Sim.Native\**\*.cs" />`;
  `NAT/.gdignore`.
- ABI of record `NAT/include/chimera_sim.h` (C, `static_assert` sizes). All `[UnmanagedCallersOnly]`, return `int32` status:

| Export | Contract |
|---|---|
| `chimera_abi_version()` / `chimera_abi_check(unit_size, building_size)` | `0x00010000`; sizes 56 / 28 |
| `chimera_build_info(buf, cap, int32* len)` | `abi=1.0;algo=29;commit=<sha>;dirty=<0/1>;runtime=<FrameworkDescription>;aot=<0/1>` |
| `chimera_session_create(content_root, scenario, uint64 seed, uint32 flags, int32* out_id)` / `chimera_session_destroy(id)` | via `SimSession`; bit0 = AI on |
| `chimera_set_checksum_interval(id, n)` / `chimera_last_checksum(id, uint32* tick, uint32* hash)` | host loop config (not folded) / read |
| `chimera_pre_tick_hashes(id, uint64 out[6])` | five H0 hashes + tick0 (`PreTickHashes`) |
| `chimera_submit_order(id, faction, unit_ref, cmd, x_raw, z_raw, slot)` | the one way in; pre-checks `TryResolveRef` + ownership only to **report**: returns `0` applied, `1` dropped (Apply is still called, so behaviour is identical) |
| `chimera_step(id)` | exactly one `StepOnce` |
| `chimera_read_units(id, ChimeraUnit* out, cap, int32* count)` / `chimera_read_buildings(…)` | **all** ids `0..HighWaterMark-1` (count = HighWaterMark); `flags` carry Alive/Phased; `-5` + required count if short |
| `chimera_units_digest(id, uint64*)` / `chimera_wide_digest(id, uint64*)` | `WorldDigest` (read-only) |
| `chimera_unit_def_id(…)` / `chimera_building_def_id(…)` / `chimera_verdict(…)` / `chimera_stats(…)` | mesh lookup ids; win state; GC heap and gen counts |
| `chimera_file_sha256(const char* path, buf, cap, int32* len)` | hex SHA-256 of any file (scenario, orders, the DLL itself), `System.Security.Cryptography` |
| `chimera_selftest(int32 kind)` | 1 = managed throw/catch, 2 = null dereference caught as `NullReferenceException`, 3 = full blocking GC; returns 0 if handled |
| `chimera_last_error(id_or_0, buf, cap, int32* len)` | last message per session; 0 = process-wide |

  `ChimeraUnit` (56 B): `int32 id, ref; int32 pos[3], prev[3], vel[3]` (Fixed raw, Godot axes); `int32 hp_raw, max_hp_raw; uint8 faction, mesh_type,
  flags, command`. `ChimeraBuilding` (28 B): `int32 slot, pos[3], hp_raw, max_hp_raw; uint8 faction, type, alive, pad`.
- Ownership: the library allocates nothing the host frees; UTF-8 inputs; caller buffers with capacity and length; sessions are small ids (stale id →
  `-2`). Errors: `0` OK, `-1` bad argument, `-2` bad session, `-3` content load failed, `-4` scenario rejected, `-5` buffer too small, `-6` exception,
  `-7` ABI mismatch. Every body is `try/catch(Exception)`. No threads or callbacks; a process-wide lock; Unreal calls from the game thread only.
- FP environment: hosts call through a shim that saves MXCSR, sets `0x1F80`, calls, restores, and **counts** non-default values found (reported, not
  gated; correctness is gated by trace equality).
- **No export writes folded sim state** except through the command stream: the only mutators are `submit_order`, `step`, and host config
  (`set_checksum_interval`, session create/destroy). `ST/check_exports.py` (stdlib PE reader from `PA/pe_exports_review.py`) asserts DLL exports ==
  header list == the allow-list in this table.

### 3.5 Trace format, meta files and comparator
Every leg writes `# key: value` headers: `leg, host, runtime, config, commit, dirty, algo, scenario_sha256, orders_sha256, seed (hex, as parsed), ai,
hash.start_state, hash.canonical_model, hash.content, hash.ruleset, hash.agreement, hash.tick0, units_at_start`; native legs add `dll_sha256`, Unreal
adds `p_commit, ue_module_sha256, shots (as parsed), max_steps_per_frame`. Body: `<tick> <HASH8>` for 1..1440 (golden body format). Trailer:
`# digest.<t>` and `# wide.<t>` at t = 0/300/900/1440, `# orders_digest` (FNV over parsed rows in submit order), `# orders_applied`, `# orders_dropped`,
`# alive_end`, `# verdict`. All hashes come from the DLL or the shared managed code, never from UE (F27).
The golden body stays in `Golden/trial-1000{,-ai}.golden.txt` (F33); its header fields live in committed sidecars `Golden/trial-1000{,-ai}.meta.txt`
(same keys + `initial_delay`, `item_registry`). `ST/compare_traces.py --set main|ai name=path…`: picks golden+meta by set, maps
`checksum_algo_version`→`algo`, checks tick set, per-tick equality, H0 + tick0, header equality, digests, `orders_digest`; and in `--final` mode exactly
one `commit` with `dirty=0`, one `dll_sha256` (== the published DLL), one `ue_module_sha256`. Writes `compare.json` + `parity.png`; exit 0 iff all pass.

### 3.6 The legs
| Leg | Build | Runtime | Run |
|---|---|---|---|
| Managed (reference) | `NAT/Cli/SimTrialCli.csproj` + the Tier-1 golden test | .NET 8.0.25 JIT | `dotnet run -c Release --project NAT/Cli -- run …` |
| C++ | `NAT/harness/chimera_harness.cpp`, `build.ps1` (vcvars via `cmd /c` inside pwsh), output `NAT/harness/bin/` | NativeAOT, plain process | `NAT/harness/bin/chimera_harness.exe --dll … --cycles 3` |
| Godot | `G/src/Trial/SimTrialRunner.cs` + `G/scenes/sim_trial.tscn` | Godot 4.6.3 hosted .NET 8.0.25, Debug | `GODOT --headless --path G res://scenes/sim_trial.tscn -- …` |
| MainScene probe (A8b) | DEBUG-only `--sim-trial-probe` in `MainScene` | the real game, windowed (headless = server branch, F38) | `GODOT --path G -- --sim-trial-probe <abs json> --probe-out <file>` with `CHIMERA_MATCH_SEED` |
| Unreal | module `ChimeraSimHost` in P | NativeAOT inside `UnrealEditor.exe -game` | `P/SimTrial/tools/run_sim.ps1` |

### 3.7 Unreal module `ChimeraSimHost` (in P; R3 §3, §6.2, §7, with changes)
- Files: `P/Source/ChimeraSimHost/{ChimeraSimHost.Build.cs, Private/ThirdParty/chimera_sim.h (sha256-checked copy), ChimeraSimLibrary, ChimeraSimHostModule,
  ChimeraSimGameMode, ChimeraSimDirector, ChimeraUnitRenderer, ChimeraArena}`; `.uproject` Modules entry; `ExtraModuleNames` in both Target.cs. Deps:
  Core, CoreUObject, Engine, RenderCore, RHI, Json. Tooling/config in `P/SimTrial/`, mirrored to `R/tools/unreal-sim/` at A13.
- DLL: `publish.ps1 -StageOnly` (creates target folders, no publish) copies `ChimeraSim.dll` + pdb to `P/Binaries/ThirdParty/ChimeraSim/Win64/` and the header into the module;
  `RuntimeDependencies.Add(…)`. `StartupModule` loads `FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() / "Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll")`
  once, logs `LogChimeraSim: loaded path=<full> abi=… build=…`, never frees. In `-game`, a failed load or ABI mismatch exits with
  `FPlatformMisc::RequestExitWithStatus(false, 3)`.
- Start: `/Engine/Maps/Entry?game=/Script/ChimeraSimHost.ChimeraSimGameMode` (F24). Game mode: no pawn, no HUD; spawns `AChimeraArena` (300 m plane,
  `M_LT_Ground`, sun, sky atmosphere, sky light, fog, post volume with F31's constants), an `ACameraActor` with `AutoActivateForPlayer = Player0`
  (F28), and `AChimeraSimDirector`. `-ChimeraSimNoArena` (used on `LT_A_noLumen`) spawns neither arena nor camera and keeps the map's `CAM_Gameplay`.
- Options: `-ChimeraSimContent= -ChimeraSimScenario= -ChimeraSimOrders= -ChimeraSimSeed=<decimal or 0x hex> -ChimeraSimTicks=1440 -ChimeraSimAi
  -ChimeraSimOut= -ChimeraSimShots=<path to shots.json> -ChimeraSimHideUnits -ChimeraSimFilmEvery=N -ChimeraSimHitchMs= -ChimeraSimHitchEvery=
  -ChimeraSimNoArena -ChimeraSimExitWhenDone`. String options use `FParse::Value(…, FString&, /*bShouldStopOnSeparator*/ false)`; the seed is read as a
  string and converted with `FCString::Strtoui64(*S, nullptr, 0)` (F26). Every parsed option is echoed to the log and the trace header; `run_sim.ps1`
  asserts the echoed seed and shot list. No comma lists on the command line.
- Warm-up: hold tick 0 until `GetNumRemainingJobs()==0` and `GetNumRemainingAssets()==0` for 60 consecutive frames, then 8 s TSR settle (F29); log
  `warmup_wait_s`; fail with exit 4 after 15 min (first run) / 3 min (later runs, `-ChimeraSimWarmupMaxSec`).
- Loop (game thread, `TG_PrePhysics`): `acc += min(FApp::GetDeltaTime(), 0.25)` in double; while `acc >= 1/30` and `tick < N` (max 8 steps per frame):
  submit this tick's rows, `chimera_step`, append the trace line, `acc -= 1/30`; `alpha = acc*30`. `-ChimeraSimHitchMs/Every` sleeps in the director to
  force clamp and catch-up; the log records `max_steps_per_frame`. Pacing never reaches the sim, so 24 fps and hitch runs must give the same trace.
- Rendering: id-keyed. One ISM per (faction, mesh_type) via `chimera_unit_def_id` and `P/SimTrial/unit_meshes.json` (mesh, team MI, scale, front yaw);
  Movable, `NoCollision`, no nav, `PreAllocateInstancesMemory` with 25% headroom. Per id the director keeps (generation, mesh_type, component, index); an id
  first seen (≥ the initial HighWaterMark) gets `AddInstance` on its group; a changed generation or mesh type hides the old instance and adds a new one.
  One `BatchUpdateInstancesTransforms` per component per frame. Buildings: one ISM per building def from `chimera_read_buildings` (mapped mesh, else a grey
  proxy cube with the team MI), updated when the building set changes. Position `UE(X,Y,Z) = 100·(x, z, y)` from `raw/65536` (axis swap, no mirror),
  `lerp(prev, pos, alpha)`. Yaw from the per-tick displacement `pos − prev` when its length > 0.01 m (F18), else the last yaw (initially facing the enemy).
  Dead or Phased: scale 0 (fallback if A11 sees artefacts: move under the ground plane).
- Shots (`P/SimTrial/shots.json`, list of `{tick, camera}`): cameras yaw 90 (world −X = screen right, so alpha at x<0 is on the right) — `overview` pivot
  (0,0,0) 150 m pitch 55 hFOV 90 (both CommandCentres in frame), `wide` 90 m pitch 55 hFOV 90, `close` 30 m pitch 40 hFOV 60. Default list: 0 overview +
  wide, 60 close, 300 wide + close, 900 wide, 1440 wide (7 PNGs). At a shot tick the director freezes stepping (alpha 1), waits 30 frames, captures, waits
  for `OnScreenshotRequestProcessed` **and** the file to exist with stable size, then hides the unit ISMs, waits 30 frames, captures `<name>_hidden.png`,
  unhides, resumes. Film frames (`FilmEvery`) freeze 3 frames, are named by sequential index, and `film_ticks.txt` records each frame's tick.
- Verify at each shot tick (independent of the render path): a fresh `chimera_read_units` into a separate buffer, a separately built id→(component,
  index) map, compare `GetInstanceTransform` with `100·map(pos)` → `max_err_cm`, `visible == alive`; project 200 alive units per army (id stride) with
  `APlayerController::ProjectWorldLocationToScreen` into `verify.json`. `P/SimTrial/tools/check_shots.py` then diffs shot vs hidden: per army ≥ 90% of
  projected points have changed pixels in a 5x5 window (delta > 24/255), ≤ 5% of changed pixels fall outside the dilated army boxes, and alpha's mean
  screen x > beta's (mirroring check). Thresholds are first guesses; any change is recorded with the evidence.
- Timing: the CSV profiler (`-csvCaptureFrames`, F30) with `CSV_CUSTOM_STAT` for sim ms, ticks stepped and alive; `frames.csv` keeps the director's own
  per-frame sim ms. Logs in `OUT/unreal/<tag>/`: `run.log`, `trace.txt`, `frames.csv`, the CSV, `verify.json`, `shots/`; `LogChimeraSim: tick=N hash=…`
  every 60 ticks; final `LogChimeraSim: RESULT ticks=1440 final=0x… fps_median=… sim_p95_ms=…`. On any `LogChimeraSim: Error` with
  `-ChimeraSimExitWhenDone`, exit with a non-zero status.
- `run_sim.ps1`: hard timeout = warm-up cap + ticks/30 + 180 s, then kill; tails the log and aborts within seconds on `Failed to load game mode`,
  `LogChimeraSim: Error`, or no `loaded path=` within 60 s; `-Perf` mode refuses to start (or marks `quiet=0`) if `dotnet, ilc, cl, link, MSBuild,
  godot*` use > 5% of a core in the 10 s before launch or during the run (Get-Process CPU deltas into `perf.json`).

## 4. Tasks (ordered; tier; lock)

**Rules for every task.** Cite headers/files you rely on; never guess an API. Acceptance commands run in **Git Bash** (the Bash tool) unless tagged
**[pwsh]**; LOCK is only ever invoked from Git Bash (from PowerShell `bash` is WSL, F36; `ue_lock.sh`'s own "From PowerShell" usage line has this trap).
`.ps1` files target pwsh 7. Count with Python (`grep -c` exits 1 on 0). Publishing runs **outside** LOCK; only `-StageOnly`, UE builds, editor sessions and
`-game` runs take it, one command per hold. New `G/src` files start with `#nullable enable`; Godot Node classes are `partial`; generate `.uid` sidecars
with `GODOT --headless --path G --import` and commit them (UNVERIFIED that `--import` writes them; if not, note it). **T1** = `dotnet test
G/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --logger "trx;LogFileName=<task>.trx" --results-directory OUT/tests` then
`python ST/trx_summary.py`; T1 never runs while LOCK is held, an ILC publish or a Godot run is active. **Flake rule:** rerun each failing test alone up to
2 times; the task passes only if all pass alone and all are on F39's list; record them in `OUT/tests/<task>_flakes.txt`; never edit those tests.
**Commits:** at each checkpoint (after A1, A2, A5, A8+A8b, A13) commit R with explicit pathspecs for that task's files only (parallel tasks share the
tree), rewrite `R/HANDOFF.md` in the same commit, push `origin`. Chimera-Unreal: local commits after A10, A11, A12 (no remote, F37). **Delivery:** the
main session sends `parity.png` after A8, the first shot sheet + `verify.json` numbers after A11, `perf.png` after A12, the final pack after A13.
**Decisions:** decision 1 (§8) is asked before A3, decision 2 before A12.

**A0 Baseline and tools** · sonnet · no lock · deps: none
Confirm `PA/` holds the 41 preserved files. Create `ST/trx_summary.py` (`total= passed= failed= skipped=`), `ST/count_warnings.py` (unique warnings by
rule id), `ST/check_exports.py` (from `PA/pe_exports_review.py`). Delete the obsolete guard `ToolingGateGuardTests.BurnDownDispatcher_HasNoHardcodedTier1Baseline`.
Godot smoke baseline (after `dotnet build G/godot.csproj`): `GODOT --headless --path G -- --loopback-test` (exit code; 3 min cap) and a 60 s headless server run on `alpha_map_01` (killed
at 60 s; count `Exception` lines) → `OUT/a0_godot_smoke.txt`.
Accept: T1 → `total=7123 passed=7122 failed=0 skipped=1` (else record measured, failed must be 0); Analysis build `--no-incremental` | `count_warnings.py`
→ record (reference `IL2026=62 IL3050=65 CHM0001=394 CHM0005=280 CHM0004=35 CHM0002=10 CHM0006=3 CHM0003=1`); `PA/aotsmoke` published to
`OUT/aotsmoke` (self-contained csproj) and `check_exports.py` on it → `DotNetRuntimeDebugHeader, smoke_add`; smoke file written.

**A1 Source-generated STJ migration** · sonnet (high) · no lock · deps: A0
From R: `git apply --check PA/r1_trial_migration_src.diff` then `git apply` (same for tests); hand-port any reject (the diffs have no index lines, so
`--3way` cannot help). Files per R1 §7.2-7.7. Keep LF, no BOM.
Accept: (1) T1 failed=0, total = A0 + listed new guards; (2) Analysis → `IL2026=0 IL3050=0`, CHM unchanged; (3) `git diff --diff-filter=MD --stat <A0> --
'godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt'` empty and `git diff <A0> -- godot/src | python -c` count of `^[-+].*AlgoVersion\s*=` = 0;
(4) `dotnet build G/godot.csproj` 0 errors; (5) Godot smoke equals A0 (same loopback exit code; 0 `Exception` lines in the server run).

**A2 AOT hazards outside STJ** · sonnet · no lock · deps: A1
`MixUnknownEffect` fail-closed under AOT (§3.1), NUL bytes → `\0` with a byte-safe Python edit; usings or fully qualified names allowed; release-gate
IL2026/IL3050 in the Analysis `ChimeraRelease` block.
Accept: T1 failed=0; `dotnet build G/ProjectChimera.Sim.Analysis -p:ChimeraRelease=true --no-incremental` 0 errors; `git diff --text` of
`CanonicalFold.cs` touches only `MixUnknownEffect`, the using lines and the two escapes; `git ls-files --eol` shows `w/lf` after the commit.

**A3 SimSession, hashes, digests, orders, footprint** · opus (medium) · no lock · deps: A2 and decision 1
Files per §3.2: `SimSession.cs`, `PreTickHashes.cs`, `WorldDigest.cs`, `OrderScript.cs`, `ComputeChecksumNow()`, the `ServerBootstrap.Build` item
parameter, `SimSources.props` (+footprint, header), footprint class doc, Tests csproj, `Sim/SimSessionTests.cs`. Mirror every F13 site with file:line comments.
Tests on `alpha_map_01` (budget < 10 s total in Debug): two builds step 300 ticks identically; `World.Rng.State == EntityWorld.DEFAULT_RNG_SEED` right
after `ServerBootstrap.Build` and before `Seed` (Apply draws nothing, so seeding after Apply equals Godot's order, `MainScene.cs:3000-3009`); AI plan None
leaves P2 buildings unchanged over 300 ticks; `FlowFields=false` differs from true by tick 300; the new item parameter is neutral (`null` and
`ItemRegistry.Empty` give the same H0; every existing golden/hash test unchanged); `WorldDigest` completeness; CSV round trip incl. 0x80 and negative raws.
Accept: T1 failed=0; `godot.csproj` 0 errors; Analysis: RS0030 and IL* unchanged (IL = 0), `OUT/a3_chm_delta.txt` lists before/after CHM counts with
every new CHM0001/CHM0005 attributed to `BuildingNavFootprint.cs` or SimSession's delegate bridge; this becomes the baseline for later tasks.

**A4 Scenario, CLI, goldens, comparator** · sonnet · no lock · deps: A3
Files: `ST/gen_trial_1000.py` (from `PA/proto`, `--units` param), `Trial/trial_1000.json`, `NAT/Cli/` (`gen` and `run`; flags `--content --scenario
--orders --seed --ticks --ai --out --digest-ticks --commit --dirty`), `Trial/trial_1000.orders.csv`, `Golden/trial-1000{,-ai}.golden.txt` (ai
Windows-gated like `AiActiveGoldenTests.cs:57-66`), `Golden/trial-1000{,-ai}.meta.txt`, `Golden/Trial1000GoldenTests.cs` (CSV replay vs golden, meta
hashes, RNG-before-seed check on trial_1000, `Verdict == 0`, alive ≥ 990 at 300, re-record rule in the failure message), `EmbeddedResource` lines,
`ST/compare_traces.py` (§3.5). `NAT/` gets its `.gdignore` and `G/godot.csproj` its `<Compile Remove="ProjectChimera.Sim.Native\**\*.cs" />` here, since
`NAT/Cli` is the first code under `G/` that must not compile into the game (F21). Record with `CHIMERA_GOLDEN_RECORD=1`.
Accept: `gen` prints `units=1000 orders=2077 verdict=0` and ticks 1/60/300/1440 equal F19 (any difference: STOP, diff against `PA/proto/Program.cs`, never
re-record past it); `run` trace `cmp`-equal to `gen`'s; the two meta files differ only in `ai` and `hash.agreement`; `compare_traces.py --set main
cli=…` and `--set ai cli_ai=…` exit 0, 1440/1440; T1 failed=0; the new golden tests < 60 s in Debug (report).

**A5 NativeAOT library and ABI** · sonnet (high) · no lock · deps: A4
Files: `NAT/ProjectChimera.Sim.Native.csproj`, `NAT/Exports/*.cs`, `NAT/include/chimera_sim.h`, `NAT/publish.ps1` (VS Installer on PATH, publish to
`NAT/bin/publish`, tee `publish.log`, `-StageOnly` implemented but first used in A10), `AnalyzerGateGuardTests` (native csproj imports SimSources.props).
Accept [pwsh]: `PS NAT/publish.ps1` exit 0; Python count of `warning IL` in `publish.log` = 0; `check_exports.py` → `missing=0 extra=0 allowlist=ok`
(ignoring `DotNetRuntimeDebugHeader`); `Microsoft.DotNet.ILCompiler/8.0.25` in `NAT/obj/project.assets.json`; `godot.csproj` 0 errors; T1 failed=0.

**A6 AOT golden replay gate** · sonnet · no lock · deps: A5
`NAT/AotGoldens/` (PublishAot exe) compiles the sim set + every `Golden/*Scenario.cs` that compiles without xUnit + `GoldenChecksumReplay.cs`. First
write `NAT/AotGoldens/GOLDENS.md`: one row per committed golden (builder, ticks, golden file, driver/perturb) extracted from the `*GoldenTests.cs`
files, every excluded golden named with its reason. Two checks: (a) AOT sequence == JIT sequence (same exe source run under `dotnet run`) for every
compiled driver; (b) AOT == the committed golden's samples (`ParseGolden`) wherever the test is exactly `RunAndRecord(Scenario.DefaultTicks, build: Scenario.Build)`; plus
the trial golden via SimSession + CSV; plus an unknown effect kind → `NotSupportedException`.
Accept: `SimAotGoldens.exe` → `jit_equal=N1/N1 golden_equal=N2/N2 trial=1440/1440 unknown_effect=fail_closed runtime=.NET 8.0.25 aot=1` (N1 ≥ 22, report
N2 and the table); publish log 0 IL warnings.

**A7 C++ smoke harness** · sonnet · no lock · deps: A5
`NAT/harness/chimera_harness.cpp` + `build.ps1` (output `NAT/harness/bin/`): LoadLibrary by full path, resolve all exports, ABI/struct checks, MXCSR shim,
CSV parse, run, trace with `orders_digest`/`orders_dropped`, `dll_sha256`/`scenario_sha256`/`orders_sha256` via `chimera_file_sha256` (cross-checked
against Python `hashlib`); `--cycles 3`; `--digest-ticks 0,300,900,1440` (own option parser; units + wide digests); `--selftest-errors` (bad scenario →
`-3`/`-4` with message, bad id → `-2`, short buffer → `-5`, selftest kinds 1-3 → 0); `--mxcsr 0x9FC0 --no-shim` experiment on the ai variant.
Accept [pwsh]: `PS NAT/harness/build.ps1` → `cl_exit=0`; harness main → `cycles=3 identical=yes selftest=pass sha_match=3/3`; `compare_traces.py --set
main cpp=…` and `--set ai cpp_ai=…` exit 0, 1440/1440, digests equal to the CLI's; FTZ/DAZ experiment result recorded (diverged at tick N, or 1440/1440).

**A8 Godot headless leg** · sonnet · no lock · deps: A4
`G/src/Trial/SimTrialRunner.cs` (`partial`, `#nullable enable`; parses args after `--`; SimSession + OrderScript; trace with `runtime`, `godot`, `config`,
`godot_define`; `[Checksum]` every 60 ticks; whole `_Ready` in try/catch → `GetTree().Quit(2)`; `Quit(rc)`), `G/scenes/sim_trial.tscn`,
`ST/run_godot_leg.ps1` (builds `godot.csproj`, runs GODOT, kills after 5 min, checks exit code and trace). Which assembly Godot loads is UNVERIFIED; the
`config` header settles it.
Accept [pwsh]: `-Variant main` and `-Variant ai` exit 0; headers `runtime: .NET 8.0.25`, `host: godot 4.6.3`, `godot_define: 1`; compare `godot=`,
`godot_ai=` exit 0, 1440/1440; 24 `[Checksum]` lines equal the golden. Main session sends `parity.png` (cli, cpp, godot) to Alec.

**A8b MainScene composition probe (gating)** · opus (medium) · no lock · deps: A4
DEBUG-only `--sim-trial-probe <abs path> --probe-out <file>` in `MainScene`: sets `ScenarioPath`, runs the normal offline match start (plan
`OfflineDefault` = variant ai, F38) with `CHIMERA_MATCH_SEED` pinned to the trial seed (check its accepted format in `MatchSeedProducer`), and right after
the seed/AI-plan/re-apply block (`MainScene.cs:2985-3075`) and before the first `StepOnce` writes `PreTickHashes` from MainScene's own loaded
factions/abilities/items/damage, then quits. Windowed (headless takes the server branch).
Accept: `ST/run_mainscene_probe.ps1` exits within 3 min; `probe_match=6/6` against `trial-1000-ai.meta.txt`. A mismatch is a finding: STOP and report
which hash differs (the composition delta is real until explained).

**A9 Static unit meshes for ISMs** · sonnet · LOCK (one editor session) · deps: A0; **must finish before A10 edits anything in P**
`P/SimTrial/tools/{prep_units.py, sim_import.py, import_units.sh, check_unit_meshes.py}`: prep the 12 unrigged GLBs with `prep_roster.py`'s rules, import
as StaticMesh to `/Game/SimUnits/<key>/` via the look-test job bridge, create `MI_SimTeam_<key>_<faction>` with `lt_common.py`'s tint maths, reuse the 4
static unit and 2 CommandCentre meshes; write `P/SimTrial/unit_meshes.json` (18 entries). `import_units.sh` refuses to start if any project module
listed in the `.uproject` has no built DLL.
Accept: `LOCK bash P/SimTrial/tools/import_units.sh` exit 0; `check_unit_meshes.py` → `entries=18 resolved=18 static=18 nanite=18 height_err_max<=3%`.

**A10 UE module: load, step, trace, error paths** · opus (medium) · LOCK per step · deps: A5, A4, A9
Module per §3.7 without the renderer (game mode, arena, camera, director loop, options, warm-up, trace, CSV stats, exits); `P/SimTrial/tools/{build.ps1,
run_sim.ps1}` (launch pattern of `run_fps.ps1`, `MSYS_NO_PATHCONV=1` per PLAN_DELTA D7). Edit the `.uproject`/Target files and build in the same session
without an editor in between.
Accept: `PS NAT/publish.ps1` (no lock); `LOCK pwsh -NoProfile -File NAT/publish.ps1 -StageOnly`; `LOCK pwsh … build.ps1` → `Result: Succeeded`; runs
`main`, `main -MaxFps 24`, `hitch` (300 ms every 97 frames; `max_steps_per_frame` ≥ 7), `ai`: each exit 0, one `loaded path=` line with a full path, a
`RESULT` line, echoed seed `0xC0FFEE1234567890` and shot list; `compare_traces.py` main set (`unreal`, `unreal_24fps`, `unreal_hitch`) and ai set
(`unreal_ai`) exit 0, 1440/1440, digests equal; error run with a bad scenario → logged `-3`/`-4` + `chimera_last_error` text, process exits non-zero
within 2 min; selftest kinds 1-3 inside UE → 0; MXCSR non-default count reported. If UE crashes or hangs on load: see §6 (check (a) FAILED/PARTIAL).

**A11 Renderer, cameras, shots, verify** · opus (medium) · LOCK per run · deps: A10
§3.7 rendering, facing, buildings, shots, hidden frames, verify. One discarded warm-up run first.
Accept: `verify.json` at 0/60/300/900/1440: `visible == alive`, `max_err_cm <= 0.5`; `check_shots.py` passes per army at every shot (incl. mirroring); 7 PNGs
at 1920x1080 > 300 KB; log has no new warnings after the tick-900 mass deaths (zero-scale check); a rendered `ai` run (no shots) with verify at the 4 ticks
→ `visible == alive` including AI-created ids; traces still 1440/1440. `PROOF/visual_check.json` (two colour-distinct armies at 0; lines meet near x≈0 by
300; thinner at 900/1440; feet on the ground; close-ups face their motion; both CommandCentres in the overview; no stretched/rotated meshes) is filled
by a **separate reviewer agent** dispatched by the main session, not by the implementer; any "no" is fixed and re-shot. Main session sends the contact
sheet and verify numbers to Alec.

**A12 Performance and film** · sonnet · LOCK per run · deps: A11, decision 2; **only after A6-A8 have finished**
Runs (all `run_sim.ps1 -Perf`, quiet check enforced): warm-up (discarded); arena `perf` ×3; arena `perf -HideUnits` ×1; `LT_A_noLumen?game=…
-ChimeraSimNoArena` ×3 (ground extent vs ±100 m is UNVERIFIED: report, units may stand off the ground); `trial_2000` (gen `--units 2000`, no golden)
×1, reported only; `film -FilmEvery 6`. `parse_frames.py` reuses `parse_csv.py`'s rules (≥ 1,500 frames per rep, spread < 5%, spikes) over the full
run and W1 (ticks 1-300) → `perf.json` + `perf.png`. Film: `ffmpeg -framerate 24 -i film_%04d.png -vf scale=960:-2 -c:v libx264 -pix_fmt yuv420p -crf 23 battle.mp4`.
Accept: perf traces 1440/1440; every perf run `quiet=1`; the decision-2 bar; sim step p95 ≤ 5 ms, max ≤ 33 ms; GPU delta (units − hidden) and the
LT_A and 2,000-unit numbers reported; GPU column cross-checked; `battle.mp4` 8-12 s, < 10 MB, `ffprobe` 960x540.

**A13 Final evidence run, pack, handoff** · sonnet · LOCK for UE steps · deps: A6, A7, A8, A8b, A12, A14
On a clean tree at the final R commit, after the final P build: `ST/run_all_legs.sh` republishes (outside LOCK), restages (LOCK), runs cli, cpp,
godot (main + ai), the A8b probe, and unreal `main` (rendered, shots), `24fps`, `hitch`, `ai` (rendered), then `compare_traces.py --final` for both sets.
Copy traces, compare outputs, shots + `contact_sheet.png`, `verify.json`, `visual_check.json`, `perf.*`, `battle.mp4`, build infos and `README.md` (verdict
wording of §1, numbers, reproduce commands, known deltas §3.2, MixUnknownEffect fork, not-covered list incl. packaged/cooked builds, A14 result) into
PROOF; mirror `P/Source/ChimeraSimHost` + `P/SimTrial` to `R/tools/unreal-sim/`; commit (pathspecs), HANDOFF, push.
Accept: `--final` main set `legs=7 ticks=1440 all_equal=yes commits=1 dirty=0 dll=1 ue_build=1` (meta, cli, cpp, godot, unreal, unreal_24fps,
unreal_hitch) and ai set `legs=5 …` (meta, cli, cpp, godot, unreal); probe 6/6; every §5 artefact exists.

**A14 Full MainScene replay (measured, reported, not gating)** · opus (medium) · no lock · deps: A8b
Convert the CSV to a `.chmr` (`ReplayRecorder`, in the sim set), play it windowed through `MainScene.ReplayPath` (F38), and compare Godot's own
`[Checksum]` lines (every 60 ticks) with the golden. Pre-registered expectation: divergence at or after the first Move arrival (orders at 150/300) if
DW-681's frame writer is the cause. Report the first divergent 60-tick window or 1440/1440; a divergence is filed as a DW with its first tick.

Locked time ≈ 3 builds × 2 min + A9 editor session (~15 min) + ~22 runs × ~3 min (first warm-up up to 15 min) ≈ 85-100 min, in short holds.

## 5. Proof of done (from A13's single evidence run)

1. **Parity:** both sets pass `--final`: 1,440/1,440 per leg, H0 + tick0, headers, digests and wide digests at 0/300/900/1440, `orders_digest` equal;
   one clean R commit, one DLL hash, one UE module build.
2. **Composition:** MainScene probe 6/6 (A8b); full MainScene result (A14) reported next to it.
3. **The run is the one claimed:** `units_at_start 1000`, `orders_applied 2077`, `orders_dropped` equal across legs, alive ≥ 990 at 300, < 600 at 1440, verdict 0.
4. **Neutral migration:** T1 failed=0 (flakes listed per the rule); `git diff --diff-filter=MD <A0> -- '…/Golden/*.golden.txt'` empty, added goldens
   listed (`trial-1000*`); no `AlgoVersion` diff; IL2026/IL3050 = 0; ILC 0 IL warnings; AOT replay counts; Godot smoke unchanged.
5. **ABI health:** exports == header == allow-list; ABI/struct checks in C++ and UE; 3 identical cycles; error paths and selftests pass in C++ **and in UE**;
   one DLL load per UE process; MXCSR count and the FTZ/DAZ experiment reported.
6. **Unreal draws the sim:** fresh-read `verify.json` (visible == alive, ≤ 0.5 cm), pixel check per army incl. mirroring, reviewer's `visual_check.json` all yes.
7. **Performance:** per decision 2, `quiet=1`, numbers in `perf.json`; LT_A and 2,000-unit numbers reported.
8. **Artefacts in PROOF and on Alec's phone**, sent as produced.

## 6. Risks and fallbacks

| Risk | Fallback |
|---|---|
| The migration moves a golden or hash | STOP; find the type with the oracle; never re-record or bump `AlgoVersion` to pass |
| Fixed baseline (6,392/0/1) is stale (F6) | Gate on A0's measured numbers with failed = 0; HANDOFF corrected at A1 |
| A4 trace differs from F19 | Diff `SimSession` against `PA/proto/Program.cs` first |
| A8b probe ≠ SimSession | A real composition delta: find it before any Unreal parity claim |
| NativeAOT misbehaves inside UE (loader lock, GC suspension, SEH; R3 open question 2, UNVERIFIED) | First call is `chimera_abi_version`; then selftests. If in-process hosting fails, **check (a) is reported FAILED/PARTIAL with the crash evidence**. A helper-process host may be built only as a labelled diagnostic, offered to Alec as a decision |
| Runtime skew (Godot moves to 8.0.26, DLL stays 8.0.25) | Headers record both; re-pin ILC and republish |
| Float paths differ under another FP environment | Shim; `ai` variant in every leg; A7's FTZ/DAZ experiment shows whether it matters |
| Unfolded state hides a divergence (F40) | Wide digest gated at 4 ticks; comparator names the first tick |
| `MainScene` differs (DW-681, `SelectionSystem`) | A14 measures it; recommended fix is closing DW-681 (arrival into the sim) |
| Shader/asset compile on first runs | Warm-up waits on F29 counters with a cap; discarded warm-up runs before shots and perf |
| fps below the bar (GPU-bound already, R3 §1.4) | Measure `-HideUnits` delta; then ISM shadows off; then per-instance custom data; report, never lower the bar silently |
| Other heavy work skews perf or flakes T1 | `-Perf` quiet check; A12 after A6-A8; T1 never concurrent with LOCK/ILC/Godot |
| A run hangs while holding LOCK | `run_sim.ps1` hard timeout and log tail; director exits non-zero on error |
| Unrigged GLBs differ in pose/scale/facing | `unit_meshes.json` is tunable text; worst case proxy cubes with team MIs |
| `godot.csproj` or Godot picks up `NAT/` | `Compile Remove` + `.gdignore`; `godot.csproj` build in every .NET task |
| Disagreement with a fixed decision | None material. Notes: the baseline number is stale; the library keeps the LLM cluster (ILC trims it); `ServerBootstrap.Build` gains one optional parameter (behaviour-neutral, tested in A3) |

## 7. Out of scope (stated as not covered in the README)

Lockstep/merged-tick exports and the .NET match server; animation (R3 §4.4); resource-node and projectile rendering; selection, input, HUD (check b);
terrain (check c); abilities, training/placement orders, RNG-drawing content, triggers and heroes in the scenario; Linux AOT; **packaged/cooked builds**
(RuntimeDependencies staging and NativeAOT under a packaged game remain UNVERIFIED, R3 §3.2); deleting `PathRequestSystem`; making `ServerBootstrap`
delegate to `SimSession`.

## 8. Decisions that are Alec's

1. **What "Godot" means for check (a)** (asked before A3). Recommended: SimSession in Godot 4.6.3's own build and runtime, plus the gating MainScene
   start-state probe (A8b), plus the full-MainScene replay (A14) measured and reported but not gating. Consequence: today's real game has a frame-paced
   Move→Stop writer (DW-681) that will likely make full MainScene diverge; that is a Godot-side bug, fixed by moving arrival into the sim. Alternative:
   gate on full MainScene 1440/1440, which needs DW-681 closed first.
2. **Performance in the pass bar** (asked before A12). Recommended: in the arena, median ≥ 60 fps **and** 1%-low ≥ 50 over the full run (≥ 1,500 frames
   per rep, 3 reps, spread < 5%), with `LT_A_noLumen` (trees + scatter) and 2,000 units reported, not gated. Alternatives: also gate on LT_A_noLumen; or
   report perf separately from the checksum verdict.
3. **Only if in-process hosting fails:** accept check (a) as FAILED/PARTIAL, or fund a helper-process host as an alternative architecture.
4. **Chimera-Unreal has no remote:** add a private remote so its checkpoints are pushed, or keep local commits.

## 9. Review log (critique of 2026-10-01; A = accepted, R = rejected)

1. api A3 CHM warnings — A: confirmed float/MathF in footprint (`:38-41,134-140`) and CHM0001/0005 rules; A3 accepts a recorded, attributed CHM delta.
2. api AI-variant pretick — A: `MatchAgreementHash.cs:107` folds the plan; separate `trial-1000-ai.meta.txt`, comparator picks by set.
3. api one commit/one build — A: A13 now re-runs every leg in one evidence run; headers carry commit/dirty/dll/UE build; `--final` enforces.
4. api renderer and mid-run entities — A: id-keyed renderer with AddInstance, generation tracking, buildings from `read_buildings`; rendered ai run verified.
5. api A6 underspecified — A: confirmed `aot_out.txt` compares fnv/last only; GOLDENS.md table and two-part acceptance.
6. api Godot leg adds little — A: only `#if` is `FixedPoint.cs:359`; A8b probe gating, A14 reported, verdict wording in §1.
7. api FParse traps — A: `Parse.h:71`, `Parse.cpp:552-573` verified; no-separator parse, `Strtoui64(…, 0)`, echo and assert.
8. api UE SHA-256 — A: `GenericPlatformMisc.cpp:2019-2023` is `checkf(false)`; `chimera_file_sha256` export.
9. api CanonicalFold binary — A: `git ls-files --eol` = `-text`, NULs at 11393/11617; byte-safe edit, `\0` escapes, `git diff --text`.
10. api CRLF/BOM and --3way — A: 966 files `i/lf`, no BOM; `git apply --check` rc 0 (RAN); plain apply.
11. api A3 options/scenario — A: test-only `SimSessionOptions`, `alpha_map_01`, < 10 s budget.
12. api vacuous RNG test — A: assert `DEFAULT_RNG_SEED` (`EntityWorld.cs:283`) before `Seed`, as `MainScene.cs:3000-3009` relies on.
13. api "exactly" overclaims — A: deltas listed in §3.2/README; items now loaded and passed (see 45).
14. api golden header keys — A: `FormatGolden` (`:175-190`) fixed header; meta sidecars + `checksum_algo_version`→`algo`.
15. api §5.3 golden diff — A: `--diff-filter=MD`, added goldens listed.
16. api readback self-check — A: fresh read, separate map, shown-vs-hidden pixel check, §0 wording softened.
17. api warm-up compile — A: wait on `GetNumRemainingJobs`/`GetNumRemainingAssets` (F29) with cap; discarded warm-up before A11.
18. api view target and yaw — A: `PlayerController.cpp:603-633`; camera `AutoActivateForPlayer = Player0`, yaw 90 specified.
19. api facing from Velocity — A: `MovementSystem.cs:159-168` skips the write; facing from `pos − prev`.
20. api A9 vs A10 .uproject — A: A9 must finish before A10 edits P; `import_units.sh` refuses on missing module DLLs.
21. api perf overlaps .NET work — A: quiet check in `-Perf`; A12 after A6-A8.
22. api shared working tree commits — A: pathspec commits per task.
23. api no PE parser — A: `pefile` missing (RAN); stdlib reader from `PA/pe_exports_review.py` (RAN on aotsmoke).
24. api A0 aotsmoke/scratchpad paths — A: copied aotsmoke and more into PA now; README marks csprojs as reference copies.
25. api harness outputs/staging folders — A: `NAT/harness/bin/` (`**/bin/` ignored); `-StageOnly` creates folders, first used in A10 under LOCK.
26. api ProjectDir relative — A: `ConvertRelativePathToFull` (`Paths.h:637`), full path logged, exit on failure.
27. api read_units contract — A: all ids `0..HighWaterMark-1`, flags carry Alive/Phased.
28. api film numbering/lock budget — A: sequential frames + `film_ticks.txt`, 3-frame freeze, budget re-estimated.
29. api house rules — A: `#nullable enable` (`NullableContextHygieneTests`), partial Node classes, `.uid` sidecars in Rules.
30. api flake rule — A: rule in §4 with F39's named list (all six files exist).
31. api A10 tier — A: A10 → opus (medium); first in-UE load is diagnosis-heavy.
32. api zero-scale instances — A: A11 log check after tick-900 deaths; under-ground fallback.
33. proof Godot leg / decision late — A: decision 1 asked before A3 with the DW-681 consequence; A8b gating; A14 reported (merged with 6).
34. proof mixed traces / DLL provenance — A: `dirty` + `dll_sha256` in build info and headers; evidence run (merged with 3).
35. proof perf scene and bar — A (bar stays Alec's): LT_A_noLumen ×3 and 2,000 units reported; full-run window ≥ 1,500 frames, 3 reps; stricter bar proposed.
36. proof quiet machine — A: merged with 21; T1 never concurrent with LOCK/ILC/Godot.
37. proof Unreal-draws self-check — A: digests in the Unreal trace, pixel check incl. mirroring, reviewer agent fills `visual_check.json` (merged with 16).
38. proof flaky tests — A: merged with 30.
39. proof no timeouts — A: `GameInstance.cpp:1546-1550` fallback verified; run_sim timeout + log tail, director non-zero exits, Godot try/catch + 5 min kill.
40. proof helper-process fallback — A: in-process failure = check (a) FAILED/PARTIAL; helper only as a labelled diagnostic, Alec decides.
41. proof shell syntax — A: Bash default with [pwsh] tags, LOCK from Git Bash only (F36), PS = pwsh 7, Python counts; `ue_lock.sh` comment trap noted for the main session.
42. proof comma lists — A: duplicate of 7; shots moved to `shots.json`.
43. proof warm-up — A: duplicate of 17.
44. proof AI pretick — A: duplicate of 2.
45. proof items and AI level — A: `ServerBootstrap.cs:77-80` passes no items; SimSession loads `ItemRegistry`, new optional `Build` parameter; `aiLevel` Normal = Godot default.
46. proof checksum blind spots — A: F40 confirmed; `WorldDigest.WideDigest` export gated at 4 ticks, completeness test.
47. proof MXCSR gate vs shim — A: MXCSR count reported not gated; FTZ/DAZ no-shim experiment in A7.
48. proof orders not comparable — A: `orders_digest`, submit returns applied/dropped (Apply still called, behaviour unchanged).
49. proof weak pacing test — A: `hitch` run in the parity set, `max_steps_per_frame` ≥ 7.
50. proof screenshot timing — A: resume after `OnScreenshotRequestProcessed` (`UnrealClient.h:266`) and the file exists.
51. proof camera/shot spec — A: yaw 90, overview shot with both CommandCentres, tick 60 in `shots.json`, 7 PNGs.
52. proof GPU time source — A: CSV profiler as in `run_fps.ps1`; `RHIGetGPUFrameCycles` not relied on.
53. proof renderer mid-match — A: duplicate of 4.
54. proof A9/A10 collision — A: duplicate of 20.
55. proof lock estimate / ILC in lock — A: publish outside LOCK, `-StageOnly` under it; ≈ 85-100 min.
56. proof delivery and handoff rules — A: progressive sends, HANDOFF + push at each R checkpoint, decisions before A3/A12, no-remote noted.
57. proof migration never run in Godot — A: A0/A1 Godot smoke (loopback test + headless server); A8b runs the real loaders.
58. proof DLL error path in UE — A: bad-scenario run and selftest kinds 1-3 inside UE in A10.
59. proof MixUnknownEffect fork — R: the fork is unreachable unless a developer adds an effect kind with no fold arm, which Tier-1 fails; a uniform throw would rewrite ~10 DW-449 probe tests and drop JIT's value-visible fold. Fork documented in §3.1 and the README.
60. proof F7 wrong — A: `dotnet --list-runtimes` shows 8.0.25, 9.0.14, 10.0.3, 10.0.12; F7 corrected.
61. proof trial golden tied to content — A: re-record only with a `hash.content` change in the same commit; in the failure message and README.
62. proof scratchpad-only inputs — A: done during this revision: 41 files copied to `PA/` (traces, trial_1000.json, spike, aotsmoke, cppload, PE reader).
63. proof no packaged smoke — A: listed as a not-covered gap (§7, README); no task added.
64. proof "no setters" — A: reworded in §3.4; `check_exports.py` asserts the allow-list.
