#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-838, post-merge review fix — the determinism fence for the two committed CROSS-PLATFORM MultiFaction
    /// goldens, as a TEST rather than as a comment.
    ///
    /// <para><b>What broke.</b> <c>golden-multifaction.golden.txt</c> and <c>golden-multifaction8.golden.txt</c> are
    /// verified byte-for-byte on BOTH legs of the Story 1.10c Windows↔Linux gate, and neither
    /// <see cref="MultiFactionGoldenTests"/> nor <see cref="MultiFactionExpansionTests"/> carries the
    /// <c>OperatingSystem.IsWindows()</c> guard <see cref="AiActiveGoldenTests"/> uses to keep the float-scored AI
    /// out of that comparison. Both scenarios' docs justified that by asserting the AI stays INERT ("Player2 starved
    /// so the float AI scorer stays inert"). DW-838 deleted <c>ScoreRazeBuildings</c>' <c>HasLiveCommandCenter</c>
    /// term, and from tick 281 — once P1's last combat unit dies and <c>EnemyThreatRemains</c> flips false — the
    /// starved P2 remnant now takes the below-threshold stall-breaker and issues AttackBuilding orders. That is the
    /// drift the Phase-C re-record baked in, and it makes the inertness claim FALSE.</para>
    ///
    /// <para><b>What actually keeps the goldens cross-platform reproducible.</b> Not inertness — reachability. Every
    /// <c>Score*</c> branch in <see cref="AiOpponentSystem"/> that performs float ARITHMETIC is gated behind a
    /// precondition Player2 never satisfies in these two scenarios:</para>
    /// <list type="bullet">
    ///   <item><c>ScoreLaunchAttack</c>'s <c>(float)AvailableCombatUnits / (_attackThreshold * 2)</c> and
    ///     <c>_aggressionWeight * ratio</c> run only at/above the attack threshold;</item>
    ///   <item><c>ScoreBuildArcheryRange</c>'s <c>0.70f * _techWeight</c> needs a COMPLETE Barracks;</item>
    ///   <item><c>ScoreBuildSiegeWorkshop</c>'s <c>0.60f * _techWeight</c> needs a complete ArcheryRange.</item>
    /// </list>
    /// <para>P2 owns no building at all and never fields the threshold, so every score on its reachable path is a
    /// compile-time <c>float</c> CONSTANT and the only float operations executed are exact IEEE-754 comparisons in
    /// <c>ExecuteBestAction</c>. The action itself (<c>DoRazeBuildings</c> → <c>FindNearestEnemyBuilding</c>) is
    /// integer/Fixed throughout. So the recorded sequences remain reproducible on both legs — but that now rests on
    /// a precondition, and a precondition asserted only in prose is exactly what went stale here.</para>
    ///
    /// <para>These tests are the fence. Give P2 ore, a production building, or a threshold-sized force and they go
    /// red BEFORE the golden does, naming the reason — instead of a Linux-leg byte mismatch months later.</para>
    ///
    /// State predicates only (no hash compare), so this runs on every OS.
    /// </summary>
    public class MultiFactionAiFenceTests
    {
        /// <summary>The faction <see cref="AiOpponentSystem"/> plays in every golden scenario.</summary>
        private const Faction AiFaction = Faction.Player2;

        /// <summary>DW-125 posture — read symbolically, so a difficulty retune that drops the bar under these
        /// scenarios' 3-unit remnant fails HERE instead of silently moving them onto the float-arithmetic path.</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        public static TheoryData<string> Scenarios => new() { "N=4", "N=8" };

        private static GoldenHarness Build(string which) =>
            which == "N=4" ? MultiFactionScenario.Build() : MultiFaction8Scenario.Build();

        private static int DefaultTicks(string which) =>
            which == "N=4" ? MultiFactionScenario.DefaultTicks : MultiFaction8Scenario.DefaultTicks;

        /// <summary>The AI's own conscription bar (<c>AiOpponentSystem.IsConscriptable</c>, restated here because it
        /// is private): what <c>AvailableCombatUnits</c> counts, and therefore what gates
        /// <c>ScoreLaunchAttack</c>'s float division.</summary>
        private static int ConscriptableCount(EntityWorld world)
        {
            int n = 0;
            for (int i = 0; i < world.HighWaterMark; i++)
            {
                if (!world.IsAlive(i) || world.FactionOf[i] != AiFaction) continue;
                if (world.GatherState[i] != GatherState.Inactive) continue;
                if (!world.CanDealDamage(i)) continue;
                if (world.CommandState[i] != UnitCommand.Idle && world.CommandState[i] != UnitCommand.Stop) continue;
                n++;
            }
            return n;
        }

        private static int AiBuildingCount(GoldenHarness h)
        {
            int n = 0;
            for (int b = 0; b < h.Buildings.Count; b++)
                if (h.Buildings.Alive[b] && h.Buildings.FactionOf[b] == AiFaction) n++;
            return n;
        }

        /// <summary>
        /// THE fence: across the whole recorded horizon of both cross-platform goldens, the AI faction never reaches
        /// a state in which any float-ARITHMETIC scoring branch is reachable — no ore, no building, and a force below
        /// the attack threshold. Every score it can produce is therefore a compile-time constant, which is what makes
        /// the committed bytes safe to compare across Windows and Linux even though the AI now acts.
        /// </summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void AcrossTheRecordedHorizon_NoFloatArithmeticScoringBranchIsReachable(string which)
        {
            GoldenHarness h = Build(which);
            int ticks = DefaultTicks(which);

            for (int t = 1; t <= ticks; t++)
            {
                h.Host.StepOnce();

                Assert.True(h.Resources.Ore[(int)AiFaction] == Fixed.Zero,
                    $"{which} tick {t}: the AI faction holds ore ({h.Resources.Ore[(int)AiFaction].ToFloat()}). " +
                    "It can now afford a production building, and the tech scorers multiply by _techWeight — a float " +
                    "operation inside a golden that both legs of the Win↔Linux gate compare byte-for-byte. Either " +
                    "keep the AI broke here or move this golden behind the AiActiveGoldenTests OS guard.");

                Assert.True(AiBuildingCount(h) == 0,
                    $"{which} tick {t}: the AI faction owns a building. A COMPLETE Barracks/ArcheryRange opens " +
                    "ScoreBuildArcheryRange/ScoreBuildSiegeWorkshop, whose scores are float MULTIPLICATIONS — see " +
                    "this class's summary for why that breaks the cross-platform posture of this golden.");

                Assert.True(ConscriptableCount(h.World) < NormalAttackThreshold,
                    $"{which} tick {t}: the AI fields {ConscriptableCount(h.World)} conscriptable units, at or past " +
                    $"the Normal attack threshold of {NormalAttackThreshold}. ScoreLaunchAttack then runs a float " +
                    "DIVISION and multiply, folding a float-derived decision into a cross-platform golden.");
            }
        }

        /// <summary>
        /// The other half, so the fence above cannot be mistaken for the old inertness claim and quietly rot again:
        /// the AI DOES commit its remnant to a raze inside the recorded horizon. This is the DW-838 behavior the
        /// tick-281 drift encodes; if it ever stops happening, these goldens no longer pin what their headers say
        /// they pin (and the re-record that follows must be attributed, not assumed to be noise).
        /// </summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void TheStarvedRemnant_CommitsToARaze_InsideTheRecordedHorizon(string which)
        {
            GoldenHarness h = Build(which);
            int ticks = DefaultTicks(which);
            int razeTick = -1;

            for (int t = 1; t <= ticks && razeTick < 0; t++)
            {
                h.Host.StepOnce();
                for (int i = 0; i < h.World.HighWaterMark; i++)
                    if (h.World.IsAlive(i) && h.World.FactionOf[i] == AiFaction
                        && h.World.CommandState[i] == UnitCommand.AttackBuilding)
                    { razeTick = t; break; }
            }

            Assert.True(razeTick > 0,
                $"{which}: no AI unit ever took an AttackBuilding order inside {ticks} ticks. DW-838's " +
                "below-threshold stall-breaker is what moved this golden at tick 281; if it no longer fires, the " +
                "committed sequence has changed meaning.");
        }
    }
}
