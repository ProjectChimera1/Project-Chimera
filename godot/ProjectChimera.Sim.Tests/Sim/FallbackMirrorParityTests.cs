#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.7 — the FALLBACK BEHAVIOR-PARITY PIN. The legacy un-tokened <c>ScenarioApplier.ApplyFallback()</c>
    /// writer is retired in favor of <c>Apply(Validate(BuildFallbackMirror()).Value)</c> — one writer path, one
    /// token type. This test pins that the mirror-applied world EQUALS the legacy writer's world: the legacy write
    /// sequence (faction bases, ore/crystal, 8 nodes, 2 command centres, 4 workers — copied VERBATIM from the
    /// deleted method body) is replayed inline against one host, the validated mirror is applied to another, and
    /// the two must agree on key world facts AND produce a byte-identical per-tick SimChecksum run. If this ever
    /// diverges, fix the MIRROR (the spec's Block-If posture), never the legacy sequence recorded here.
    /// </summary>
    public class FallbackMirrorParityTests
    {
        private const int Ticks = 90;

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction(); // carries the "worker" unit both paths spawn
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1;
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        /// <summary>The DELETED legacy ApplyFallback body, replayed verbatim (the parity reference).</summary>
        private static void LegacyApplyFallback(SimulationHost host, ScenarioApplier applier,
            FactionDefinition?[] slotDefs)
        {
            applier.SetFactionBase(Faction.Player1, new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero));
            applier.SetFactionBase(Faction.Player2, new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero));

            host.Resources.AddOre(Faction.Player1, Fixed.FromFloat(200f));
            host.Resources.AddOre(Faction.Player2, Fixed.FromFloat(200f));
            host.Resources.AddCrystal(Faction.Player1, Fixed.FromFloat(100f));
            host.Resources.AddCrystal(Faction.Player2, Fixed.FromFloat(100f));

            var rate = Fixed.FromFloat(5f);
            foreach (var (x, z, supply) in new (float, float, float)[]
            {
                ( -20f, -15f, 600f ), ( -20f,  15f, 600f ),
                (  20f, -15f, 600f ), (  20f,  15f, 600f ),
                (   0f, -25f, 400f ), (   0f,  25f, 400f ),
                ( -35f,   0f, 300f ), (  35f,   0f, 300f ),
            })
            {
                host.Nodes.Create(
                    new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)),
                    Fixed.FromFloat(supply), rate, maxGatherers: 4);
            }

            host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), preBuilt: true);
            host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2,
                new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero), preBuilt: true);

            var workerDef  = slotDefs[(int)Faction.Player1]?.GetUnitByCategory("Worker");
            var workerDef2 = slotDefs[(int)Faction.Player2]?.GetUnitByCategory("Worker") ?? workerDef;
            if (workerDef != null)
            {
                applier.SpawnUnit(workerDef,  Faction.Player1, -42f, -3f);
                applier.SpawnUnit(workerDef,  Faction.Player1, -42f, +3f);
            }
            if (workerDef2 != null)
            {
                applier.SpawnUnit(workerDef2, Faction.Player2, +42f, -3f);
                applier.SpawnUnit(workerDef2, Faction.Player2, +42f, +3f);
            }
        }

        private static List<(uint tick, uint hash)> RunTicks(SimulationHost host, int ticks)
        {
            var seq = new List<(uint, uint)>(ticks);
            host.SetChecksumSink((t, h) => seq.Add((t, h)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        [Fact]
        public void MirrorApplied_EqualsLegacyApplyFallback_WorldAndChecksumRun()
        {
            // Legacy reference world.
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var legacyDefs = new FactionDefinition?[5];
            legacyDefs[(int)Faction.Player1] = faction;
            legacyDefs[(int)Faction.Player2] = faction;
            var legacyHost = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            legacyHost.ChecksumInterval = 1;
            var legacyApplier = new ScenarioApplier(legacyHost, NullLogSink.Instance, legacyDefs);
            LegacyApplyFallback(legacyHost, legacyApplier, legacyDefs);

            // Mirror world (the one production path).
            var (mirrorHost, mirrorApplier) = NewHostAndApplier();
            ValidationResult r = new ScenarioValidator().Validate(ScenarioApplier.BuildFallbackMirror());
            Assert.True(r.Ok, r.Error);
            mirrorApplier.Apply(r.Value);

            // Key world facts agree at t0.
            Assert.Equal(legacyHost.Resources.Ore[(int)Faction.Player1].Raw,     mirrorHost.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(legacyHost.Resources.Crystal[(int)Faction.Player2].Raw, mirrorHost.Resources.Crystal[(int)Faction.Player2].Raw);
            Assert.Equal(CountAlive(legacyHost), CountAlive(mirrorHost));

            // And the two worlds evolve BYTE-IDENTICALLY (the SimChecksum parity core).
            List<(uint, uint)> legacyRun = RunTicks(legacyHost, Ticks);
            List<(uint, uint)> mirrorRun = RunTicks(mirrorHost, Ticks);
            Assert.Equal(legacyRun, mirrorRun);
        }

        [Fact]
        public void FallbackMirror_AlwaysValidates() // the "build defect, not a runtime path" guard
        {
            ValidationResult r = new ScenarioValidator().Validate(ScenarioApplier.BuildFallbackMirror());
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void FallbackMirror_HashesNonzero() // the fallback boot publishes a REAL wire hash (never 0)
        {
            ulong h = CanonicalModelHash.Compute(ScenarioApplier.BuildFallbackMirror());
            Assert.NotEqual(0UL, h);
            Assert.NotEqual(0u, CanonicalModelHash.ToWire(h));
        }

        [Fact]
        public void FallbackMirror_DeclaresNoTriggersAndNoGraph()
        {
            // The legacy writer's "no match_start fire on a fallback boot" contract holds today ONLY because the
            // mirror is trigger-free — pin it, so a trigger/graph quietly added to the mirror turns this red
            // instead of silently changing fallback-boot behavior.
            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror();
            Assert.Empty(mirror.Triggers);
            Assert.Null(mirror.TriggerGraphJson);
            Assert.Null(mirror.Variables);
            Assert.Null(mirror.Timers);
        }

        [Fact]
        public void FallbackMirror_ResolvesWorkerIdByCategory_FromTheSlotFactionDefs()
        {
            // Review follow-up: the legacy writer resolved workers by CATEGORY (GetUnitByCategory("Worker")); the
            // mirror resolves each slot's worker unit_id the same way when defs are threaded, so a custom faction
            // whose worker is not literally id'd "worker" still spawns workers on the fallback boot.
            var custom = new FactionDefinition
            {
                Id = "custom", DisplayName = "Custom",
                Units = { WorkerDefWithId("drone_mk1") },
            };
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = custom;                              // slot 0 → custom worker id
            // slot 1 has no def threaded → conventional "worker" fallback.

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            Assert.Equal("drone_mk1", mirror.Units[0].UnitId);
            Assert.Equal("drone_mk1", mirror.Units[1].UnitId);
            Assert.Equal("worker",    mirror.Units[2].UnitId);
            Assert.Equal("worker",    mirror.Units[3].UnitId);

            // And the no-args mirror keeps the conventional id everywhere (the parity/golden baseline).
            foreach (ScenarioUnit u in ScenarioApplier.BuildFallbackMirror().Units)
                Assert.Equal("worker", u.UnitId);
        }

        /// <summary>A minimal Worker-category unit def with a custom id (helper for the category-resolution pin).</summary>
        private static UnitDefinition WorkerDefWithId(string id) => new UnitDefinition
        {
            Id = id, DisplayName = "Drone", Category = "Worker", Hp = 50f, Speed = 3.5f, Supply = 1,
        };

        private static int CountAlive(SimulationHost host)
        {
            int n = 0;
            for (int i = 0; i < host.World.HighWaterMark; i++)
                if (host.World.IsAlive(i)) n++;
            return n;
        }
    }
}
