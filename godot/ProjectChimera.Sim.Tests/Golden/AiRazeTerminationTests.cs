#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.13 (AC1.6, Decision D-5) — a headless DestroyAllBuildings match CONCLUDES. The Player2 AI, with no
    /// enemy units to fight, must RAZE the passive Player1 base to zero buildings instead of standing inert next to
    /// it forever (deferred-work #7). Win-condition evaluation is presentation-only (MainScene.CheckWinCondition,
    /// Tier-1-unreachable; the WinConditionSystem its docstring names does not exist), so this RE-IMPLEMENTS the
    /// DestroyAllBuildings predicate (a faction's alive-building count == 0) and steps the AiActiveScenario host for
    /// a bounded budget. It is a STATE PREDICATE, not a hash compare (D-5), so the AI's float non-determinism does
    /// not block it and it runs on EVERY OS. RED before Story 2.13 (the assault marches to the hardcoded empty
    /// P1_BASE and never touches the real base); GREEN after (the AI issues AttackBuilding at the nearest base).
    /// </summary>
    public class AiRazeTerminationTests
    {
        /// <summary>Generous bounded budget: the P2 army marches ~63u to the far P1 base (~630 ticks at 3 u/s) then
        /// razes a 500-HP Fortified CommandCenter. 6000 ticks (200s) sits comfortably above the observed conclusion.</summary>
        private const int MaxTicks = 6000;

        [Fact]
        public void AiRazesPassiveEnemyBase_DestroyAllBuildingsConcludes()
        {
            GoldenHarness h = AiActiveScenario.Build();
            Assert.True(CountFactionBuildings(h, Faction.Player1) > 0, "precondition: Player1 starts with a base to raze");

            int ticksToConclude = -1;
            for (int t = 0; t < MaxTicks; t++)
            {
                h.Host.StepOnce();
                if (CountFactionBuildings(h, Faction.Player1) == 0) { ticksToConclude = t; break; }
            }

            Assert.True(ticksToConclude >= 0,
                $"DestroyAllBuildings did NOT conclude within {MaxTicks} ticks — the AI never razed the passive " +
                $"Player1 base (still {CountFactionBuildings(h, Faction.Player1)} building(s) alive). The raze " +
                $"fallback (ScoreRazeBuildings/DoRazeBuildings) is not concluding the match.");
        }

        private static int CountFactionBuildings(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < h.Buildings.Count; i++)
                if (h.Buildings.Alive[i] && h.Buildings.FactionOf[i] == f) n++;
            return n;
        }
    }
}
