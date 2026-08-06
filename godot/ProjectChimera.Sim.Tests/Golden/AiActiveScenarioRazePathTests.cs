#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-839 — pins WHAT <see cref="AiActiveScenario"/> actually exercises, so its documentation cannot silently go
    /// stale again.
    ///
    /// <para><b>Why this exists.</b> That fixture's header claimed the AI "scores BuildBarracks (0.85) highest [on
    /// tick 1]" and that "the P2 wave marches to the AI's hardcoded P1_BASE (-45,0,0) with nothing to fight". Neither
    /// has ever been true: P1 fields a CommandCenter and no units, which is precisely the input
    /// <c>ScoreRazeBuildings</c> wins on (no live defenders + a structure to raze, 0.90), so the AI runs
    /// <c>DoRazeBuildings</c> and <c>DoLaunchAttack</c> never executes. The golden itself was never wrong — it pins
    /// whatever the AI does — but a wrong note beside a golden is how a future movement gets mis-attributed, which is
    /// exactly what the Phase B batch hit. A comment cannot be regression-tested; the BEHAVIOUR it describes can, so
    /// this asserts the behaviour and the comment now merely reports it.</para>
    ///
    /// <para><b>Cross-platform note.</b> <c>AiOpponentSystem</c> scores in raw <c>float</c>, which is why the
    /// ai-active GOLDEN is Windows-gated. This suite is not: it pins a discrete decision decided by well-separated
    /// constants (0.90 vs 0.85 vs 0.325), never a near-tie, so the winner is the same on any JIT.</para>
    /// </summary>
    public class AiActiveScenarioRazePathTests
    {
        /// <summary>The 5 pre-placed Player2 combat units occupy ids 0..4 (see AiActiveScenario.PopulateScenario).</summary>
        private const int P2Units = 5;

        [Fact]
        public void Tick1_TheAiRazesTheP1CommandCenter_AndNeverLaunchesAWave()
        {
            GoldenHarness h = AiActiveScenario.Build();
            h.Host.StepOnce();

            int p1Cc = OnlyPlayer1Building(h);

            int commanded = 0;
            for (int i = 0; i < h.World.HighWaterMark; i++)
            {
                if (!h.World.IsAlive(i) || h.World.FactionOf[i] != Faction.Player2) continue;
                commanded++;

                Assert.Equal(UnitCommand.AttackBuilding, h.World.CommandState[i]);

                // The order names P1's CommandCenter — resolved through the packed-ref path the AI writes.
                Assert.True(h.Buildings.TryResolveRef(h.World.CommandTarget[i], out int slot),
                    $"unit {i}'s CommandTarget ({h.World.CommandTarget[i]}) does not resolve to a live building.");
                Assert.Equal(p1Cc, slot);

                // A building id must never leak into entity-space AttackTarget.
                Assert.Equal(-1, h.World.AttackTarget[i]);
            }

            Assert.Equal(P2Units, commanded); // non-vacuity: every unit really was commanded
        }

        [Fact]
        public void Tick2_TheAiBuildsItsBarracks_OnceTheRazeWaveIsNoLongerAvailable()
        {
            GoldenHarness h = AiActiveScenario.Build();

            h.Host.StepOnce();
            Assert.False(HasPlayer2Barracks(h));                                   // tick 1 went to the raze…
            Assert.Equal(Fixed.FromInt(300), h.Resources.Ore[(int)Faction.Player2]);

            h.Host.StepOnce();
            Assert.True(HasPlayer2Barracks(h));                                    // …and the Barracks lands on tick 2
            Assert.Equal(Fixed.FromInt(200), h.Resources.Ore[(int)Faction.Player2]);
        }

        /// <summary>
        /// The two claims the corrected header still makes about the whole 300-tick recording horizon: the wave path
        /// is never taken (so this golden does NOT cover <c>DoLaunchAttack</c>), and the run stays combat-free.
        /// </summary>
        [Fact]
        public void AcrossTheWholeGoldenHorizon_NoWaveIsLaunched_AndNoDamageIsDealt()
        {
            GoldenHarness h = AiActiveScenario.Build();
            int p1Cc = OnlyPlayer1Building(h);
            Fixed p1CcHealth = h.Buildings.Health[p1Cc];

            for (int t = 0; t < AiActiveScenario.DefaultTicks; t++)
            {
                h.Host.StepOnce();

                for (int i = 0; i < h.World.HighWaterMark; i++)
                {
                    if (!h.World.IsAlive(i) || h.World.FactionOf[i] != Faction.Player2) continue;

                    Assert.NotEqual(UnitCommand.AttackMove, h.World.CommandState[i]); // DoLaunchAttack's signature
                    Assert.NotEqual(AiOpponentSystem.P1_BASE, h.World.MoveTarget[i]); // and its hardcoded destination
                }
            }

            // Combat-free: the raze wave never closes the ~28 units to (60,60) inside the horizon.
            Assert.Equal(p1CcHealth, h.Buildings.Health[p1Cc]);
            Assert.Equal(P2Units, AlivePlayer2Units(h)); // …and nothing died on either side of the march
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>The scenario's single Player1 building (its CommandCenter), asserted to BE single so a fixture
        /// change that adds another cannot make the target assertions ambiguous.</summary>
        private static int OnlyPlayer1Building(GoldenHarness h)
        {
            int found = -1;
            for (int b = 0; b < h.Buildings.Count; b++)
            {
                if (!h.Buildings.Alive[b] || h.Buildings.FactionOf[b] != Faction.Player1) continue;
                Assert.True(found < 0, "AiActiveScenario is expected to field exactly one Player1 building.");
                found = b;
            }
            Assert.True(found >= 0, "AiActiveScenario fields no live Player1 building — the raze input is gone.");
            Assert.Equal(BuildingType.CommandCenter, h.Buildings.Type[found]);
            return found;
        }

        private static bool HasPlayer2Barracks(GoldenHarness h)
        {
            for (int b = 0; b < h.Buildings.Count; b++)
                if (h.Buildings.Alive[b]
                    && h.Buildings.FactionOf[b] == Faction.Player2
                    && h.Buildings.Type[b] == BuildingType.Barracks)
                    return true;
            return false;
        }

        private static int AlivePlayer2Units(GoldenHarness h)
        {
            int n = 0;
            for (int i = 0; i < h.World.HighWaterMark; i++)
                if (h.World.IsAlive(i) && h.World.FactionOf[i] == Faction.Player2) n++;
            return n;
        }
    }
}
