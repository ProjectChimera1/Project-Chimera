# AotGoldens: the committed goldens, one row each (Unreal trial A6, plan A section 4 A6, fact F34)

`SimAotGoldens` compiles the real sim set (`godot/SimSources.props`), every `Golden/*Scenario.cs` that compiles without xUnit,
`Golden/GoldenChecksumReplay.cs` and two xUnit-free test helpers, and replays them as a NativeAOT executable. Nothing under
`godot/ProjectChimera.Sim.Tests/` is modified or copied: the files are compiled in place (see the csproj).

Paths below are relative to `godot/ProjectChimera.Sim.Tests/Golden/` (G). Line numbers are the committed test's golden-comparing
line at the time of writing (commit 3b5275de + A6). Every golden file was listed with `ls G/*.golden.txt`: **37 files**.

Checks per row:
- **(a) jit** = the AOT sequence equals the JIT sequence of the same exe source run under `dotnet run` (tick and hash, every sample).
- **(b) golden** = the AOT sequence equals the committed golden file's samples (`GoldenChecksumReplay.ParseGolden` on the file bytes).
  Kind **exact** = the committed test calls exactly `GoldenChecksumReplay.RunAndRecord(Scenario.DefaultTicks, build: Scenario.Build)`
  (the plan's wording). Kind **driver** = the committed test uses its own loop; the exe mirrors that loop line for line (named in the
  Driver column) and the mirror is proven faithful by JIT == golden in the same run. The final report counts both kinds apart.

## Compared (33 per-tick or value goldens in the exe)

| # | Golden file (samples) | Builder | Ticks | Driver in the committed test | Matching test (G) | Kind | Notes |
|---|---|---|---|---|---|---|---|
| 1 | golden-scenario (300) | GoldenScenario.Build (default build) | 300 | `RunAndRecord(GoldenScenario.DefaultTicks)` | GoldenChecksumReplayTests.cs:78,95 | exact | also the k=100 perturb check (:120): located at tick 101 |
| 2 | golden-applier-scenario (300) | GoldenApplierScenario.Build | 300 | `RunAndRecord(.., build:)` | GoldenApplierScenarioTests.cs:73 | exact | |
| 3 | same-tick-tie-break (300) | SameTickTieBreakScenario.Build | 300 | `RunAndRecord(.., build:)` | SameTickTieBreakGoldenTests.cs:66 | exact | |
| 4 | ai-active-scenario (300) | AiActiveScenario.Build | 300 | `RunAndRecord(.., build:)` | AiActiveGoldenTests.cs:69 | exact | the test is Windows-gated (AI float scoring); this exe runs on Windows only |
| 5 | command-vocabulary-scenario (300) | CommandVocabularyScenario.Build | 300 | `RunAndRecord(.., build:)` | CommandVocabularyGoldenTests.cs:58 | exact | |
| 6 | formation-separation-scenario (300) | FormationSeparationScenario.Build | 300 | `RunAndRecord(.., build:)` | FormationSeparationGoldenTests.cs:59 | exact | |
| 7 | rebaseline-guard-frozen-v22-formation-separation (300) | FormationSeparationScenario.Build | 300 | `RunAndRecord(.., build:)` | ReBaselineDifferentialGuardTests.cs:76 | exact | frozen control (header algo 22), same builder as row 6 |
| 8 | golden-multifaction (300) | MultiFactionScenario.Build | 300 | `RunAndRecord(.., build:)` | MultiFactionGoldenTests.cs:102,119 | exact | perturb k=100 on PerturbTargetId (:140): located at tick 101 |
| 9 | golden-multifaction8 (300) | MultiFaction8Scenario.Build | 300 | `RunAndRecord(.., build:)` | MultiFactionExpansionTests.cs:139 | exact | perturb k=100 on Player8UnitId (:161): located at tick 101 |
| 10 | pathability-block-scenario (120) | PathabilityBlockScenario.Build | 120 | `RunAndRecord(.., build:)` | PathabilityBlockGoldenTests.cs:45 | exact | |
| 11 | projectile-snap-scenario (300) | ProjectileSnapScenario.Build | 300 | `RunAndRecord(.., build:)` | ProjectileSnapGoldenTests.cs:45 | exact | |
| 12 | combat-air-ground-scenario (300) | CombatAirGroundScenario.Build | 300 | `RunAndRecord(.., build:)` | CombatAirGroundGoldenTests.cs:40 | exact | |
| 13 | anti-building-scenario (300) | AntiBuildingScenario.Build | 300 | `RunAndRecord(.., build:)` | AntiBuildingGoldenTests.cs:42 | exact | |
| 14 | delivery-scenario (300) | DeliveryScenario.Build | 300 | `RunAndRecord(.., build:)` | DeliveryGoldenTests.cs:41 | exact | |
| 15 | hero-xp-scenario (300) | HeroXpScenario.Build | 300 | `RunAndRecord(.., build:)` | HeroXpGoldenTests.cs:41 | exact | |
| 16 | ability-domain-filter-scenario (300) | AbilityDomainFilterScenario.Build | 300 | own loop: `ApplyScheduleStep(host, i)` before each `StepOnce` (RecordRun, :28-39) | AbilityDomainFilterGoldenTests.cs:56 | driver | |
| 17 | crit-dodge-scenario (300) | CritDodgeScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :30-42) | CritDodgeGoldenTests.cs:63 | driver | |
| 18 | ground-cast-scenario (300) | GroundCastScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :33-45) | GroundCastGoldenTests.cs:66 | driver | |
| 19 | modifier-scenario (300) | ModifierScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :33-45) | ModifierGoldenTests.cs:66 | driver | |
| 20 | shift-queue-scenario (300) | ShiftQueueScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :38-50) | ShiftQueueGoldenTests.cs:71 | driver | |
| 21 | stat-pipeline-scenario (300) | StatPipelineScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :32-44) | StatPipelineGoldenTests.cs:65 | driver | |
| 22 | tag-filter-scenario (300) | TagFilterScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :38-50) | TagFilterGoldenTests.cs:71 | driver | |
| 23 | ability-cast-scenario (300) | AbilityCastScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :35-47) | AbilityCastGoldenTests.cs:68 | driver | builder needs `Effects/AbilityTestSupport.cs` (xUnit-free helper, compiled in place) |
| 24 | equal-exchange-scenario (300) | EqualExchangeScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :37-49) | EqualExchangeGoldenTests.cs:70 | driver | same helper as row 23 |
| 25 | worker-cast-crystal-cost-scenario (300) | WorkerCastCrystalCostScenario.Build | 300 | own loop with `ApplyScheduleStep` (RecordRun, :35-47) | WorkerCastCrystalCostGoldenTests.cs:68 | driver | same helper as row 23 |
| 26 | passive-scenario (300) | PassiveScenario.Build | 300 | own plain `StepOnce` loop (RecordRun, :36-45) | PassiveGoldenTests.cs:66 | driver | builder needs `Effects/PassiveTestAbilities.cs` (xUnit-free helper) |
| 27 | energy-regen-scenario (40) | `EnergyRegenGoldenTests.BuildHost()` | 40 | own plain `StepOnce` loop (RecordRun, :70-78) | EnergyRegenGoldenTests.cs:94 | driver | **mirror**: the builder is `internal static` inside the xUnit test class (no *Scenario.cs exists), so its 15 lines are mirrored in `Drivers.cs` (`EnergyRegenMirror`); faithful iff JIT == golden |
| 28 | golden-merged-n2 (300) | MergedTickN2Scenario.RunMerged | 300 | `RunMerged()` | MergedTickN2GoldenTests.cs:67 | driver | scenario method returns the samples |
| 29 | golden-merged-n3 (300) | MergedTickN3Scenario.RunMerged(3) | 300 | `RunMerged(3)` | MergedTickN3GoldenTests.cs:58 | driver | |
| 30 | golden-merged-n4 (300) | MergedTickN3Scenario.RunMerged(4) | 300 | `RunMerged(4)` | MergedTickN3GoldenTests.cs:58 | driver | |
| 31 | hero-start-state (value) | HeroStartStateScenario.Compute | n/a | single FNV-64 value (`startstatehash <hex16>`) | HeroStartStateGoldenTests.cs:41 | value | not a per-tick sequence; compared as one 64-bit value (AOT == golden, AOT == JIT) |
| 32 | trial-1000 (1440) | `SimSession.Build(.., ai:false)` + frozen `Trial/trial_1000.orders.csv` | 1440 | `Simulate(ai:false)` (Trial1000GoldenTests.cs:~200-233) | Trial1000GoldenTests.cs:270 | trial | reported as `trial=` (1440/1440) |
| 33 | trial-1000-ai (1440) | same, `ai:true` (OfflineDefault AI plan) | 1440 | `Simulate(ai:true)` | Trial1000GoldenTests.cs:286 | trial | Windows-gated in the test; reported as `trial_ai=` beside the gate line |

Rows 1-30 count towards N2 (`golden_equal=N2/N2`), together with row 31; rows 32-33 are the `trial=` check.

## Excluded goldens (4), each with its reason

| Golden file (samples) | Scenario file | Test (G) | Reason |
|---|---|---|---|
| hero-revival-scenario (300) | HeroRevivalScenario.cs | HeroRevivalGoldenTests.cs:67 | the scenario file has `using Xunit;` and calls `Assert.Equal` inside its `Build`/helpers (lines 4, 69, 76, 82): it does not compile without xUnit; the test's own driver (`Run`, issues ReviveHero orders) is also test code |
| item-scenario (120) | ItemScenario.cs | ItemGoldenTests.cs:68 | `using Xunit;` and `Assert.Equal` in `Build` (lines 6, 66, 70, 76, 77) |
| research-scenario (30) | ResearchScenario.cs | ResearchGoldenTests.cs:61 | `using Xunit;` and `Assert.Equal` in `Build` (lines 6, 79) |
| shop-purchase-scenario (60) | ShopPurchaseScenario.cs | ShopPurchaseGoldenTests.cs:59 | `using Xunit;` and `Assert.Equal` in `Build` (lines 7, 56) |

Scenario files are never modified (task rule), so these four stay out of the AOT replay. Their features (heroes, items, research,
shop) are still exercised by the compared goldens only where the trial and the other scenarios reach them; the gap is named here.

## Builders without a committed golden (compared AOT vs JIT only, check (a))

| Builder | Driver in the exe | Why no golden |
|---|---|---|
| MultiFaction3Scenario.Build | `RunAndRecord(DefaultTicks, build:)` | no golden file exists for the 3-faction builder (the spike also ran it) |
| DataDrivenBuildingScenario.Build | plain `StepOnce` loop, 120 ticks (the test's `Run`) | DataDrivenBuildingGoldenTests compares two same-run sequences, no committed file |
| MidMatchDropScenario.RunDrop / RunDropNoInject / RunDropIdleControl / RunNoDrop | the scenario's own methods | MidMatchDropDesyncTests compares runs against each other, no committed file |
| CombatResetScenario | not driven (compiled in place, so it stays buildable under ILC) | used only by `Sim/SimResetTests.cs` (reset semantics, not a sequence); builds a host, no per-tick golden or sequence driver exists |

Count of `Golden/*Scenario.cs` (36): 4 excluded for xUnit, 32 compiled in place; of those 31 are driven (30 files feed the 36 sequence rows, since formation-separation and merged-n3/n4 share a
file, energy-regen has none, and the last 6 rows have no golden; HeroStartStateScenario feeds the value row) and CombatResetScenario is compiled but not driven.

## Result (A6 run, 2026-10-01; the numbers the report quotes)

Gate line of the NativeAOT exe (`run_gate.ps1 -Step run`, `bin/publish/SimAotGoldens.exe`):

```
jit_equal=37/37 golden_equal=31/31 trial=1440/1440 unknown_effect=fail_closed runtime=.NET 8.0.25 aot=1
```

- N1 = 37 = 36 sequence rows (30 golden rows + 6 builders without a golden) plus the hero-start-state value; each equals the JIT leg.
- N2 = 31 = 15 exact + 15 driver sequences + the hero-start-state value; each equals its committed golden file byte-for-byte in samples.
- Beside the gate line (breakdown line): `trial_ai=1440/1440` (the Windows-gated ai golden), `perturb_located=3/3` (golden-scenario,
  multifaction, multifaction8: +1 raw health at loop index 100 is located at tick 101), trial main and ai equal the JIT leg, and the shipped
  `DamageEffect` fold equals the JIT fold.
- The JIT leg is a plain JIT build (`JitMode=true`: `PublishAot` off, dynamic code and reflection STJ on) so it behaves as Godot's
  runtime does; with `PublishAot=true` even the `dotnet run` build sets the AOT runtime feature switches (found while building this).
  In the JIT leg the unknown effect kind folds by reflection (unchanged JIT behaviour); under NativeAOT it throws `NotSupportedException`
  (A2, create returns -6): `unknown_effect=fail_closed`.
- The gate can fail: a JIT reference with one hash flipped gives `jit_equal=36/37`, `RESULT FAIL`, exit 1 (run recorded in the A6 evidence).

Re-run: `pwsh -File run_gate.ps1 -Step jit` (light), then under the lock
`UE_LOCK_TAG=a/A6 bash D:/Projects/Chimera-Unreal/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File run_gate.ps1 -Step publish`,
then `pwsh -File run_gate.ps1 -Step run` (light). A golden or a scenario that moves makes the gate fail: do not re-record to pass it
(the committed tests carry the re-record rules).
