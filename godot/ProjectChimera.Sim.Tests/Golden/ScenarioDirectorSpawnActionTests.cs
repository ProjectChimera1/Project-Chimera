#nullable enable
using ProjectChimera.Dsl;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 7.1 (I/O matrix row: trigger <c>spawn_unit</c>) — proves the retyped spawn path carries <see
    /// cref="Fixed"/> coordinates and the spawn count through the <c>OnSpawnUnit</c> delegate with NO float in the
    /// director tick path. Before 7.1, <c>TriggerAction.X/Z</c> were <c>float</c> and <c>OnSpawnUnit</c> was
    /// <c>Action&lt;string,int,float,float,int&gt;</c>, so a trigger spawn ran an in-tick <c>Fixed.FromFloat</c> in
    /// the applier. Now the delegate is <c>Action&lt;string,int,Fixed,Fixed,int&gt;</c> and the coordinates reach it
    /// as the byte-exact Fixed values parsed at the JSON boundary — the determinism-relevant surface at the director.
    ///
    /// <para>The per-unit lateral offset (x + i·2.5) and the <c>SpawnUnitAt</c> routing live in
    /// <c>ScenarioDelegateBinder</c> (presentation-adjacent, requires a Godot <c>SceneContext</c>), so they are not
    /// exercisable from this Godot-free suite; this test pins the outermost sim-layer surface (the delegate contract).
    /// The spawn count clamp (<c>Math.Min(count, 50)</c>) is asserted here as unchanged — the reconciliation to the
    /// named cap of 64 is Story 7.6, not 7.1.</para>
    /// </summary>
    public class ScenarioDirectorSpawnActionTests
    {
        private readonly record struct SpawnCall(string UnitId, int Faction, Fixed X, Fixed Z, int Count);

        private static List<SpawnCall> CaptureSpawns(TriggerAction spawnAction)
        {
            var calls = new List<SpawnCall>();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.OnSpawnUnit = (unitId, faction, x, z, count) =>
                calls.Add(new SpawnCall(unitId, faction, x, z, count));
            director.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name    = "spawn",
                        Enabled = true,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { spawnAction },
                    },
                },
            });
            director.Tick(new EntityWorld(), Fixed.One);
            return calls;
        }

        [Fact]
        public void SpawnUnitAction_PassesFixedCoordinatesAndCount_Unchanged()
        {
            List<SpawnCall> calls = CaptureSpawns(new TriggerAction
            {
                Type   = "spawn_unit",
                UnitId = "soldier",
                Faction = 1,
                X      = Fixed.FromInt(40),
                Z      = Fixed.FromInt(5),
                Count  = 3,
            });

            SpawnCall c = Assert.Single(calls);
            Assert.Equal("soldier", c.UnitId);
            Assert.Equal(1, c.Faction);
            // Byte-exact Fixed coordinates reach the delegate — no float round-trip in the director tick path.
            Assert.Equal(Fixed.FromInt(40).Raw, c.X.Raw);
            Assert.Equal(Fixed.FromInt(5).Raw,  c.Z.Raw);
            Assert.Equal(3, c.Count);
        }

        [Fact]
        public void SpawnUnitAction_ClampsCountAt50_Unchanged()
        {
            List<SpawnCall> calls = CaptureSpawns(new TriggerAction
            {
                Type   = "spawn_unit",
                UnitId = "soldier",
                Faction = 0,
                X      = Fixed.Zero,
                Z      = Fixed.Zero,
                Count  = 100,
            });

            SpawnCall c = Assert.Single(calls);
            Assert.Equal(50, c.Count); // Story 7.1 keeps the as-built clamp; the 64 cap is Story 7.6.
        }
    }
}
