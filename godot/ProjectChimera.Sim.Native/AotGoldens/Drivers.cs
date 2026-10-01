#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;

namespace ProjectChimera.AotGoldens
{
    /// <summary>How a row's driver relates to the committed test that compares its golden (GOLDENS.md, "Kind").</summary>
    internal enum Kind
    {
        /// <summary>The test calls exactly <c>RunAndRecord(Scenario.DefaultTicks, build: Scenario.Build)</c>.</summary>
        Exact,
        /// <summary>The test has its own loop (schedule steps, a different host builder, a RunMerged helper); the exe mirrors it.</summary>
        Driver,
        /// <summary>No committed golden file: compared AOT vs JIT only.</summary>
        NoGolden,
    }

    /// <summary>One sequence driver: a name, the golden file it is compared with (null: none) and how to run it.</summary>
    internal sealed class Row
    {
        public readonly string Name;
        public readonly string? GoldenFile;
        public readonly Kind Kind;
        public readonly Func<IReadOnlyList<GoldenChecksumReplay.Sample>> Run;

        public Row(string name, string? goldenFile, Kind kind, Func<IReadOnlyList<GoldenChecksumReplay.Sample>> run)
        {
            Name = name;
            GoldenFile = goldenFile;
            Kind = kind;
            Run = run;
        }
    }

    /// <summary>
    /// Every sequence driver of GOLDENS.md. The drivers either call <see cref="GoldenChecksumReplay.RunAndRecord"/> exactly as the
    /// committed test does, or mirror the test's own loop (named in the comment of each). Nothing here edits a scenario or a test.
    /// </summary>
    internal static class Drivers
    {
        private static IReadOnlyList<GoldenChecksumReplay.Sample> Rar(int ticks, Func<GoldenHarness> build) =>
            GoldenChecksumReplay.RunAndRecord(ticks, build: build);

        /// <summary>The tests' RecordRun with a schedule: <c>ApplyScheduleStep(host, i)</c> BEFORE each StepOnce.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> Sched(
            int ticks, Func<GoldenHarness> build, Action<SimulationHost, int> step)
        {
            GoldenHarness harness = build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++)
            {
                step(harness.Host, i);
                harness.Host.StepOnce();
            }
            return seq;
        }

        /// <summary>The tests' RecordRun without a schedule: a plain StepOnce loop.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> Plain(int ticks, SimulationHost host)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        /// <summary>
        /// MIRROR of <c>EnergyRegenGoldenTests.BuildHost()</c> (G/EnergyRegenGoldenTests.cs:44-58, <c>internal static</c> in an xUnit
        /// test class, so it cannot be compiled without xUnit and no *Scenario.cs exists for it). Faithful iff JIT == golden.
        /// </summary>
        private static SimulationHost EnergyRegenMirrorHost()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;
            int caster = w.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                  Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.MaxEnergy[caster] = Fixed.FromInt(100);
            w.RegenRate[caster] = Fixed.FromInt(2);
            w.Energy[caster] = Fixed.FromInt(40);

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty -> no-op)
            return host;
        }

        /// <summary>The DataDrivenBuildingGoldenTests.Run loop (G/DataDrivenBuildingGoldenTests.cs:20-28), tick and hash kept.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> DataDrivenBuilding()
        {
            (GoldenHarness harness, int[] _) = DataDrivenBuildingScenario.Build();
            return Plain(DataDrivenBuildingScenario.DefaultTicks, harness.Host);
        }

        /// <summary>All sequence rows, in GOLDENS.md order (rows 1-30, then the builders without a golden).</summary>
        public static List<Row> All() => new()
        {
            // ── exact: the committed test calls RunAndRecord(DefaultTicks, build: X.Build) ──
            new Row("golden-scenario", "golden-scenario.golden.txt", Kind.Exact,
                () => GoldenChecksumReplay.RunAndRecord(GoldenScenario.DefaultTicks)), // default build, as GoldenChecksumReplayTests
            new Row("golden-applier", "golden-applier-scenario.golden.txt", Kind.Exact,
                () => Rar(GoldenApplierScenario.DefaultTicks, GoldenApplierScenario.Build)),
            new Row("same-tick-tie-break", "same-tick-tie-break.golden.txt", Kind.Exact,
                () => Rar(SameTickTieBreakScenario.DefaultTicks, SameTickTieBreakScenario.Build)),
            new Row("ai-active", "ai-active-scenario.golden.txt", Kind.Exact,
                () => Rar(AiActiveScenario.DefaultTicks, AiActiveScenario.Build)),
            new Row("command-vocabulary", "command-vocabulary-scenario.golden.txt", Kind.Exact,
                () => Rar(CommandVocabularyScenario.DefaultTicks, CommandVocabularyScenario.Build)),
            new Row("formation-separation", "formation-separation-scenario.golden.txt", Kind.Exact,
                () => Rar(FormationSeparationScenario.DefaultTicks, FormationSeparationScenario.Build)),
            new Row("formation-separation-frozen-v22", "rebaseline-guard-frozen-v22-formation-separation.golden.txt", Kind.Exact,
                () => Rar(FormationSeparationScenario.DefaultTicks, FormationSeparationScenario.Build)),
            new Row("multifaction", "golden-multifaction.golden.txt", Kind.Exact,
                () => Rar(MultiFactionScenario.DefaultTicks, MultiFactionScenario.Build)),
            new Row("multifaction8", "golden-multifaction8.golden.txt", Kind.Exact,
                () => Rar(MultiFaction8Scenario.DefaultTicks, MultiFaction8Scenario.Build)),
            new Row("pathability-block", "pathability-block-scenario.golden.txt", Kind.Exact,
                () => Rar(PathabilityBlockScenario.DefaultTicks, PathabilityBlockScenario.Build)),
            new Row("projectile-snap", "projectile-snap-scenario.golden.txt", Kind.Exact,
                () => Rar(ProjectileSnapScenario.DefaultTicks, ProjectileSnapScenario.Build)),
            new Row("combat-air-ground", "combat-air-ground-scenario.golden.txt", Kind.Exact,
                () => Rar(CombatAirGroundScenario.DefaultTicks, CombatAirGroundScenario.Build)),
            new Row("anti-building", "anti-building-scenario.golden.txt", Kind.Exact,
                () => Rar(AntiBuildingScenario.DefaultTicks, AntiBuildingScenario.Build)),
            new Row("delivery", "delivery-scenario.golden.txt", Kind.Exact,
                () => Rar(DeliveryScenario.DefaultTicks, DeliveryScenario.Build)),
            new Row("hero-xp", "hero-xp-scenario.golden.txt", Kind.Exact,
                () => Rar(HeroXpScenario.DefaultTicks, HeroXpScenario.Build)),

            // ── driver: the committed test has its own loop; mirrored (GOLDENS.md names the lines) ──
            new Row("ability-domain-filter", "ability-domain-filter-scenario.golden.txt", Kind.Driver,
                () => Sched(AbilityDomainFilterScenario.DefaultTicks, AbilityDomainFilterScenario.Build, AbilityDomainFilterScenario.ApplyScheduleStep)),
            new Row("crit-dodge", "crit-dodge-scenario.golden.txt", Kind.Driver,
                () => Sched(CritDodgeScenario.DefaultTicks, CritDodgeScenario.Build, CritDodgeScenario.ApplyScheduleStep)),
            new Row("ground-cast", "ground-cast-scenario.golden.txt", Kind.Driver,
                () => Sched(GroundCastScenario.DefaultTicks, GroundCastScenario.Build, GroundCastScenario.ApplyScheduleStep)),
            new Row("modifier", "modifier-scenario.golden.txt", Kind.Driver,
                () => Sched(ModifierScenario.DefaultTicks, ModifierScenario.Build, ModifierScenario.ApplyScheduleStep)),
            new Row("shift-queue", "shift-queue-scenario.golden.txt", Kind.Driver,
                () => Sched(ShiftQueueScenario.DefaultTicks, ShiftQueueScenario.Build, ShiftQueueScenario.ApplyScheduleStep)),
            new Row("stat-pipeline", "stat-pipeline-scenario.golden.txt", Kind.Driver,
                () => Sched(StatPipelineScenario.DefaultTicks, StatPipelineScenario.Build, StatPipelineScenario.ApplyScheduleStep)),
            new Row("tag-filter", "tag-filter-scenario.golden.txt", Kind.Driver,
                () => Sched(TagFilterScenario.DefaultTicks, TagFilterScenario.Build, TagFilterScenario.ApplyScheduleStep)),
            new Row("ability-cast", "ability-cast-scenario.golden.txt", Kind.Driver,
                () => Sched(AbilityCastScenario.DefaultTicks, AbilityCastScenario.Build, AbilityCastScenario.ApplyScheduleStep)),
            new Row("equal-exchange", "equal-exchange-scenario.golden.txt", Kind.Driver,
                () => Sched(EqualExchangeScenario.DefaultTicks, EqualExchangeScenario.Build, EqualExchangeScenario.ApplyScheduleStep)),
            new Row("worker-cast-crystal-cost", "worker-cast-crystal-cost-scenario.golden.txt", Kind.Driver,
                () => Sched(WorkerCastCrystalCostScenario.DefaultTicks, WorkerCastCrystalCostScenario.Build, WorkerCastCrystalCostScenario.ApplyScheduleStep)),
            new Row("passive", "passive-scenario.golden.txt", Kind.Driver,
                () => Plain(PassiveScenario.DefaultTicks, PassiveScenario.Build().Host)),
            new Row("energy-regen", "energy-regen-scenario.golden.txt", Kind.Driver,
                () => Plain(40, EnergyRegenMirrorHost())), // EnergyRegenGoldenTests.Ticks = 40
            new Row("merged-n2", "golden-merged-n2.golden.txt", Kind.Driver,
                () => MergedTickN2Scenario.RunMerged()),
            new Row("merged-n3", "golden-merged-n3.golden.txt", Kind.Driver,
                () => MergedTickN3Scenario.RunMerged(3)),
            new Row("merged-n4", "golden-merged-n4.golden.txt", Kind.Driver,
                () => MergedTickN3Scenario.RunMerged(4)),

            // ── builders with no committed golden: AOT vs JIT only ──
            new Row("multifaction3", null, Kind.NoGolden,
                () => Rar(MultiFaction3Scenario.DefaultTicks, MultiFaction3Scenario.Build)),
            new Row("data-driven-building", null, Kind.NoGolden, DataDrivenBuilding),
            new Row("mid-match-drop", null, Kind.NoGolden, () => MidMatchDropScenario.RunDrop()),
            new Row("mid-match-drop-no-inject", null, Kind.NoGolden, () => MidMatchDropScenario.RunDropNoInject()),
            new Row("mid-match-drop-idle-control", null, Kind.NoGolden, () => MidMatchDropScenario.RunDropIdleControl()),
            new Row("mid-match-no-drop", null, Kind.NoGolden, () => MidMatchDropScenario.RunNoDrop()),
        };
    }
}
