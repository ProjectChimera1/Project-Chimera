#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 9.14 — the end-to-end proof: a 2v2 alliance mask SEEDED THROUGH <see cref="AllianceSeeder"/> (this
    /// story's match-start path, from a scenario's per-slot teams — not a hand-poked TeamId) drives a match to
    /// elimination and team victory, and two identical-input runs produce a BYTE-IDENTICAL <see cref="SimChecksum"/>
    /// (zero desync). Team victory itself resolves through the unchanged Story 7.12 <c>WinConditionSystem</c> once the
    /// mask is seeded. Godot-free (NullLogSink).
    /// </summary>
    public class TwoVsTwoSeededDeterminismTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;
        private static FixedVec3 At(int x) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero);

        private static SimulationHost Host() => SimulationHost.Create(
            NullLogSink.Instance, new FactionRegistry(4), new FactionDefinition(), new FactionDefinition());

        // 2v2: slots {0,1} = team 1, slots {2,3} = team 2 → canonical ids {P1,P2}=1, {P3,P4}=3.
        private static ScenarioData TwoVsTwoModel() => new ScenarioData
        {
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, Team = 1 },
                new ScenarioPlayerSlot { Slot = 1, Team = 1 },
                new ScenarioPlayerSlot { Slot = 2, Team = 2 },
                new ScenarioPlayerSlot { Slot = 3, Team = 2 },
            },
        };

        [Fact]
        public void SeededTwoVsTwo_MaskGroupsTeams()
        {
            var h = Host();
            AllianceSeeder.Seed(h.Alliances, TwoVsTwoModel());
            Assert.True(h.Alliances.AreAllied(Faction.Player1, Faction.Player2));
            Assert.True(h.Alliances.AreAllied(Faction.Player3, Faction.Player4));
            Assert.False(h.Alliances.AreAllied(Faction.Player1, Faction.Player3));
            Assert.Equal((int)Faction.Player1, h.Alliances.TeamOf(Faction.Player2));
            Assert.Equal((int)Faction.Player3, h.Alliances.TeamOf(Faction.Player4));
        }

        [Fact]
        public void SeededTwoVsTwo_RunsToTeamVictory_AndIsByteIdenticalAcrossTwoRuns()
        {
            uint Run()
            {
                var h = Host();
                AllianceSeeder.Seed(h.Alliances, TwoVsTwoModel()); // THIS story's seeding path
                int c1 = h.Buildings.Create(At(-14), Faction.Player1, BuildingType.CommandCenter);
                int c2 = h.Buildings.Create(At(-4),  Faction.Player2, BuildingType.CommandCenter);
                int c3 = h.Buildings.Create(At(6),   Faction.Player3, BuildingType.CommandCenter);
                int c4 = h.Buildings.Create(At(16),  Faction.Player4, BuildingType.CommandCenter);
                h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                                   RegionStore.Empty, null, null);

                h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                h.WinCon.Tick(h.World, Dt);                                 // all alive
                h.Buildings.Destroy(c3); h.WinCon.Tick(h.World, Dt);       // P3 out — team {P3,P4} still live via P4
                h.Buildings.Destroy(c4); h.WinCon.Tick(h.World, Dt);       // P4 out — team {P1,P2} wins as a whole
                for (int t = 0; t < 4; t++) h.WinCon.Tick(h.World, Dt);    // keep ticking (must be inert)

                // Team victory resolved via the unchanged WinConditionSystem: BOTH allies WON, both opponents LOST.
                Assert.Equal(WinStateStore.VERDICT_WON,  h.WinState.Verdict[(int)Faction.Player1]);
                Assert.Equal(WinStateStore.VERDICT_WON,  h.WinState.Verdict[(int)Faction.Player2]);
                Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player3]);
                Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player4]);
                Assert.True(h.WinCon.IsFullyResolved());
                _ = c1; _ = c2;

                return SimChecksum.Compute(h.World, h.Buildings, h.Resources, new FactionRegistry(4),
                                           winState: h.WinState, alliances: h.Alliances);
            }

            Assert.Equal(Run(), Run());
        }
    }
}
