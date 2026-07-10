using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 4.10 (AC) — the RESEARCH golden scenario. A Player1 "lab" building offers a 2-level "armor_up"
    /// research; the golden runner issues a <c>StartResearch</c> order (at <see cref="StartTick1"/>) that (at
    /// exec-tick, via <c>ResearchSystem.StartResearchCommand</c>) spends ore and starts the level-1 countdown, ticks
    /// to completion (applying the cumulative <c>ArmorDelta</c> modifier to Player1's one alive unit), then issues a
    /// SECOND <c>StartResearch</c> (at <see cref="StartTick2"/>) for level 2 of the SAME research — proving the
    /// re-baselined ladder state (bumped <c>CompletedLevels</c>/grown cumulative delta) moves the checksum again.
    /// The per-tick <see cref="SimChecksum"/> (v14, folding the mutable <see cref="ResearchStore"/> for the first
    /// time — Story 4.10) captures the whole cycle.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/>; Player2 is EMPTY so the float-scoring AI no-ops.
    /// </summary>
    public static class ResearchScenario
    {
        public const int DefaultTicks = 30;

        public const int UnitEntityId = 0; // the Player1 unit is created FIRST → id 0
        public const int ArmorUpIndex = 0; // the faction's only research entry → list index 0

        public const int StartTick1 = 2;  // issue StartResearch (level 1) BEFORE this loop iteration's StepOnce
        public const int StartTick2 = 10; // issue StartResearch (level 2) — well after level 1's 3-tick countdown

        public static (GoldenHarness harness, int labId) Build()
        {
            var faction = new FactionDefinition
            {
                Id = "research-golden-faction",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up" } },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        Id = "armor_up",
                        CancelRefundFraction = 0.5f,
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel
                            {
                                Cost = new Dictionary<string, int> { { "ore", 50 } },
                                TimeTicks = 3,
                                ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f },
                            },
                            new ResearchLevel
                            {
                                Cost = new Dictionary<string, int> { { "ore", 75 } },
                                TimeTicks = 2,
                                ModifierDelta = new ResearchModifierDelta { ArmorDelta = 3f },
                            },
                        },
                    },
                },
            };

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                faction,
                new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // A Player1 unit (id 0) — alive to receive the cumulative ArmorDelta modifier on each completion.
            int unit = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(UnitEntityId, unit);

            // The pre-built lab, operational (construction complete), offering "armor_up".
            int labId = host.Buildings.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                              Faction.Player1, BuildingType.Custom, buildingId: "lab");
            host.Buildings.ConstructionTimer[labId] = Fixed.Zero; // operational

            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(500));

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return (new GoldenHarness(host, unit), labId);
        }
    }
}
