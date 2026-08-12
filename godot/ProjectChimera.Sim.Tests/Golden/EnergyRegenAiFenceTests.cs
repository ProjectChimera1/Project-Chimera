#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-890 — the determinism fence for the CROSS-PLATFORM energy-regen golden, as a TEST rather than as a comment.
    ///
    /// <para><b>The gap.</b> <c>energy-regen-scenario.golden.txt</c> declares "All hashed fields are integer/Fixed →
    /// byte-identical Win↔Linux; NOT Windows-gated", and <see cref="EnergyRegenGoldenTests"/> carries none of the
    /// <c>OperatingSystem.IsWindows()</c> gating <see cref="AiActiveGoldenTests"/> uses to keep the float-scored AI
    /// out of a byte comparison. But <c>AiOpponentSystem.Tick</c> scores in raw <c>float</c> EVERY tick, and it runs
    /// on this host: <c>SimulationHost</c> defaults to <c>AiControlPlan.OfflineDefault</c>, which hands the AI
    /// Player2. The golden's cross-platform safety therefore rests entirely on the fixture keeping the AI away from
    /// every float-ARITHMETIC branch and away from every folded write. Nothing proved that premise:
    /// <c>RunsTwiceInProcess</c> and <c>MatchesCommittedGolden</c> only prove SAME-platform determinism, and the only
    /// cross-platform validator is an out-of-repo run. A premise that is subtly wrong — or that a later fixture edit
    /// falsifies by handing Player2 a unit or some ore — would diverge ONLY on Linux/WSL and never trip Tier-1.</para>
    ///
    /// <para><b>What actually keeps it reproducible.</b> Reachability, exactly as for the MultiFaction goldens (see
    /// <see cref="MultiFactionAiFenceTests"/>). Player2 owns no unit, owns no building and holds zero ore, so every
    /// scorer returns a compile-time <c>float</c> CONSTANT before reaching its arithmetic — <c>ScoreLaunchAttack</c>'s
    /// division needs a threshold-sized force, <c>ScoreBuildArcheryRange</c>/<c>ScoreBuildSiegeWorkshop</c>'s
    /// <c>* _techWeight</c> needs completed prerequisites, <c>ScoreRazeBuildings</c> needs an enemy BUILDING (there is
    /// none in this fixture at all) — and every action <c>ExecuteBestAction</c> could dispatch is gated behind ore it
    /// does not have or units it does not own. The only float operations executed are exact IEEE comparisons against
    /// the 0.01f floor, so the chosen action is "Nothing" on every tick of the recorded horizon.</para>
    ///
    /// <para>These tests are the fence. Give Player2 ore or units and they go red BEFORE the golden does, naming the
    /// reason. State predicates only (no hash compare), so they run on every OS.</para>
    /// </summary>
    public class EnergyRegenAiFenceTests
    {
        /// <summary>The faction <c>AiOpponentSystem</c> plays.</summary>
        private const Faction AiFaction = Faction.Player2;

        /// <summary>DW-125 posture — read symbolically, so a difficulty retune that drops the bar under this fixture
        /// fails HERE instead of silently moving the golden onto the float-arithmetic path.</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        /// <summary>
        /// Non-vacuity, first: the AI really is switched ON for this fixture. If <c>AiControlPlan</c> ever stops
        /// naming Player2 by default, the fence below would pass for the wrong reason (a whole-system no-op) and the
        /// golden's header would still be making an unproven claim about a system that no longer runs.
        /// </summary>
        [Fact]
        public void TheAiIsActiveOnThisFixture_SoTheFenceIsNotTrivial()
        {
            SimulationHost host = EnergyRegenGoldenTests.BuildHost();
            Assert.True(host.Ai.IsActive,
                "AiOpponentSystem is INACTIVE on the energy-regen fixture, so this fence proves nothing about the " +
                "float scorer. If the default AiControlPlan changed deliberately, re-point this fence (or drop the " +
                "golden header's cross-platform justification, which is written about an ACTIVE AI).");
        }

        /// <summary>
        /// THE fence: across the whole recorded horizon of the cross-platform golden, the AI faction never reaches a
        /// state in which any float-ARITHMETIC scoring branch is reachable — no ore, no building anywhere, and no
        /// conscriptable unit. Every score it can produce is therefore a compile-time constant, which is what makes
        /// the committed bytes safe to compare on both legs of the Win↔Linux gate.
        /// </summary>
        [Fact]
        public void AcrossTheRecordedHorizon_NoFloatArithmeticScoringBranchIsReachable()
        {
            SimulationHost host = EnergyRegenGoldenTests.BuildHost();

            for (int t = 1; t <= EnergyRegenGoldenTests.Ticks; t++)
            {
                host.StepOnce();

                Assert.True(host.Resources.Ore[(int)AiFaction] == Fixed.Zero,
                    $"tick {t}: the AI faction holds ore ({host.Resources.Ore[(int)AiFaction].ToFloat()}). It can now " +
                    "afford a production building, and the tech scorers multiply by _techWeight — a float operation " +
                    "inside a golden that both legs of the Win↔Linux gate compare byte-for-byte. Either keep the AI " +
                    "broke here or move this golden behind the AiActiveGoldenTests OS guard (DW-890).");

                Assert.True(LiveBuildingCount(host) == 0,
                    $"tick {t}: a building exists. The energy-regen fixture is building-free by construction: an AI " +
                    "building opens the float-MULTIPLYING tech scorers, and an ENEMY building opens ScoreRazeBuildings " +
                    "and with it a folded CommandState write on any AI unit. Either is a cross-platform hazard here.");

                Assert.True(ConscriptableCount(host.World) < NormalAttackThreshold,
                    $"tick {t}: the AI fields {ConscriptableCount(host.World)} conscriptable units, at or past the " +
                    $"Normal attack threshold of {NormalAttackThreshold}. ScoreLaunchAttack then runs a float DIVISION " +
                    "and multiply, folding a float-derived decision into a cross-platform golden.");
            }
        }

        /// <summary>
        /// The other half: the AI writes NO folded state on this fixture, which is the stronger claim the header
        /// actually makes ("the AI no-ops"). Every action <c>ExecuteBestAction</c> can dispatch either creates a
        /// building, spends ore, or writes <c>CommandState</c>/<c>MoveTarget</c>/<c>CommandTarget</c> on an AI unit —
        /// so pinning that the world holds exactly the one Player1 caster, unmoved and uncommanded, and that the
        /// building store stays empty, pins "chosen action == Nothing" through its observable consequences.
        /// </summary>
        [Fact]
        public void AcrossTheRecordedHorizon_TheAiTakesNoStateChangingAction()
        {
            SimulationHost host = EnergyRegenGoldenTests.BuildHost();

            int caster = OnlyLiveEntity(host);
            FixedVec3 spawn = host.World.Position[caster];

            for (int t = 1; t <= EnergyRegenGoldenTests.Ticks; t++)
            {
                host.StepOnce();

                Assert.Equal(caster, OnlyLiveEntity(host));   // no AI unit was trained into existence
                Assert.Equal(0, LiveBuildingCount(host));      // no AI build action executed
                Assert.True(host.Resources.Ore[(int)AiFaction] == Fixed.Zero, $"tick {t}: AI ore moved.");

                // The caster is Player1's and stationary; the AI can only ever touch its OWN units, so any movement
                // or command here would mean something other than EnergyRegenSystem is writing folded state.
                Assert.Equal(spawn, host.World.Position[caster]);
                Assert.Equal(UnitCommand.Idle, host.World.CommandState[caster]);
            }

            // …and the thing the golden exists for DID happen, so this is not a fence over a dead fixture.
            Assert.True(host.World.Energy[caster] == host.World.MaxEnergy[caster],
                "the caster never regenerated to full — the energy-regen fixture is no longer exercising regen, so " +
                "neither this fence nor the golden means what its header says.");
        }

        /// <summary>
        /// The ARMED CONTROL ARM. The fence above is only evidence while the AI would MEASURABLY act if the fixture
        /// armed it — otherwise "the AI does nothing" could be true because the AI does nothing anywhere. Hand the
        /// same fixture's Player2 a bank of ore and it builds inside a handful of ticks, i.e. the assertions above
        /// are exactly the ones that would catch a fixture edit which arms it.
        /// </summary>
        [Fact]
        public void ArmedControl_TheSameAiDoesActWhenGivenOre_SoTheFenceHasTeeth()
        {
            SimulationHost host = EnergyRegenGoldenTests.BuildHost();
            host.Resources.AddOre(AiFaction, Fixed.FromInt(500)); // affords the CC expansion and the Barracks

            int builtAtTick = -1;
            for (int t = 1; t <= EnergyRegenGoldenTests.Ticks && builtAtTick < 0; t++)
            {
                host.StepOnce();
                if (LiveBuildingCount(host) > 0) builtAtTick = t;
            }

            Assert.True(builtAtTick > 0,
                $"An AI faction holding 500 ore built NOTHING in {EnergyRegenGoldenTests.Ticks} ticks, so the fence " +
                "above cannot distinguish 'this fixture keeps the AI inert' from 'the AI never acts at all'. Re-arm " +
                "this control against whatever the AI now responds to (DW-890).");
            Assert.True(host.Resources.Ore[(int)AiFaction] < Fixed.FromInt(500),
                "the armed AI created a building without spending ore — the control arm is not observing the real " +
                "build path.");
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>The AI's own conscription bar (<c>AiOpponentSystem.IsConscriptable</c>, restated here because it
        /// is private): what <c>AvailableCombatUnits</c> counts, and therefore what gates <c>ScoreLaunchAttack</c>'s
        /// float division. Mirrors <see cref="MultiFactionAiFenceTests"/>' copy deliberately — both fences must break
        /// together if the bar moves.</summary>
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

        /// <summary>Live buildings of ANY faction — this fixture authors none, and both the tech scorers (own
        /// buildings) and the raze scorer (enemy buildings) are opened by one appearing.</summary>
        private static int LiveBuildingCount(SimulationHost host)
        {
            int n = 0;
            for (int b = 0; b < host.Buildings.Count; b++)
                if (host.Buildings.Alive[b]) n++;
            return n;
        }

        /// <summary>The fixture's single live entity (the Player1 caster), asserted to BE single so an extra spawn
        /// cannot slip past the per-tick assertions above.</summary>
        private static int OnlyLiveEntity(SimulationHost host)
        {
            int found = -1;
            for (int i = 0; i < host.World.HighWaterMark; i++)
            {
                if (!host.World.IsAlive(i)) continue;
                Assert.True(found < 0, "the energy-regen fixture is expected to hold exactly one live entity.");
                found = i;
            }
            Assert.True(found >= 0, "the energy-regen fixture holds no live entity — the caster is gone.");
            return found;
        }
    }
}
