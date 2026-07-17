#nullable enable
using ProjectChimera.Dsl;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects; // EffectCaps (Story 7.6 spawn-cap reconciliation)
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
    /// Story 7.6 reconciled the spawn count clamp to the NAMED structural cap (<c>EffectCaps.MaxSpawnCount</c> = 64;
    /// the literal 50 is retired) — the runtime clamp is the seatbelt; the validator gate is the loud reject.</para>
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
        public void SpawnCountAtMaxSpawnCount_ReachesTheDelegateUnclamped()
        {
            // Review P11: pins the retirement of the old literal-50 clamp at RUNTIME. Count = MaxSpawnCount (64)
            // is the largest loadable count (the gates admit 1..64); it must reach OnSpawnUnit UNCLAMPED. Before
            // this test no runtime observation exceeded 3, so a regressed Math.Min(count, 50) would have passed
            // the whole suite silently.
            List<SpawnCall> calls = CaptureSpawns(new TriggerAction
            {
                Type    = "spawn_unit",
                UnitId  = "soldier",
                Faction = 0,
                X       = Fixed.Zero,
                Z       = Fixed.Zero,
                Count   = EffectCaps.MaxSpawnCount,
            });

            SpawnCall c = Assert.Single(calls);
            Assert.Equal(EffectCaps.MaxSpawnCount, c.Count); // 64 — not 50, not any other stale literal
        }

        [Fact]
        public void SpawnCountBeyondMaxSpawnCount_IsRejectedAtTheLoadBackstop()
        {
            // Story 7.6 (review P5): a count beyond the named cap can no longer LOAD — the unconditional
            // LoadScenario backstop rejects it located (naming the constant), so the former "runtime clamps to
            // MaxSpawnCount" observation is unreachable through any load path. The ExecuteLeaf Math.Min clamp
            // remains a defense-in-depth seatbelt only.
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => CaptureSpawns(new TriggerAction
            {
                Type   = "spawn_unit",
                UnitId = "soldier",
                Faction = 0,
                X      = Fixed.Zero,
                Z      = Fixed.Zero,
                Count  = 100,
            }));
            Assert.Contains($"EffectCaps.MaxSpawnCount={EffectCaps.MaxSpawnCount}", ex.Message);
        }
    }
}
