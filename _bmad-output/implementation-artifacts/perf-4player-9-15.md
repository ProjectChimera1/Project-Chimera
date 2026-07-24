# Perf — 4-player late-game tick throughput (Story 9.15 → input to Story 10.3)

**Non-gated record.** Produced by `FourPlayerLoadPerfTests.FourPlayerLateGame_MedianMsPerTick_IsMeasuredAndRecorded_NoCeiling`
(headless, Godot-free). There is NO timing ceiling here; this figure feeds the Story 10.3 load/perf budget. This note is a
one-time STATIC record — the test does NOT write to it (it emits the figure via `ITestOutputHelper` only, so re-runs never
dirty the tree). Refresh it by hand from the test output if needed.

## Observed

- **Median: 141.656 ms/tick**
- Method: median-of-5, 120 measured `SimulationHost.StepOnce()` ticks per run, 15 warm-up ticks.
- World: ~4096 entities + 64 buildings across 4 active factions (near `EntityWorld.MAX_ENTITIES`) — a near-max-caps
  worst case (dense interleaved-faction combat), not a typical match; it bounds the load, it does not gate.
- Measured on: 2026-07-24 (dev machine, Windows).

> Regenerate: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter FullyQualifiedName~FourPlayerLoadPerfTests`
> then read the emitted `ms/tick` line from the test output.
