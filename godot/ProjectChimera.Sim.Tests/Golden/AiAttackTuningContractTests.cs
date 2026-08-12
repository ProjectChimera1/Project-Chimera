#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-125 — the AI attack-tuning CONTRACT: the two values Tier-1 fixtures must mirror
    /// (<see cref="AiOpponentSystem.DifficultyProfile"/>'s attack threshold and
    /// <see cref="AiOpponentSystem.P1_BASE"/>) are the values the AI actually runs on.
    ///
    /// The defect this closes was silent staleness, not a live bug: fixtures such as
    /// <see cref="AsymmetryPlaytestValidationTests"/> hand-copied the Normal threshold (<c>5</c>) and the attack
    /// destination (<c>(-45,0,0)</c>) as their own local literals, so a future difficulty-curve or map-geometry
    /// retune would leave them compiling and PASSING in a degraded way — a wave that no longer meets the real
    /// attack bar, or defenders parked on ground the wave no longer marches to.
    ///
    /// These tests are the teeth on the exposure. They cannot even COMPILE against the pre-fix
    /// <see cref="AiOpponentSystem"/> (both symbols were <c>private</c> and the difficulty curve was an inlined
    /// constructor tuple with no accessor at all), and behaviorally they prove the exposed values are LOAD-BEARING
    /// rather than a second copy: the launch decision flips exactly at the exposed threshold, and every launched
    /// unit is sent to exactly the exposed destination. Re-tuning a difficulty row or the destination keeps them
    /// green; adding a divergent copy of either value makes them red.
    ///
    /// STATE PREDICATE over a single <see cref="SimulationHost.StepOnce"/>, not a hash compare (the precedent set
    /// by <see cref="AiRazeTerminationTests"/> / <see cref="AiBelowThresholdRazeTests"/>), so the AI's known float
    /// scoring debt (D2) does not make this OS-sensitive.
    /// </summary>
    public class AiAttackTuningContractTests
    {
        /// <summary>The AI's structure placements cluster on the positive-X half (x≈36..54), so the fixture's own
        /// base sits there — deliberately far from <see cref="AiOpponentSystem.P1_BASE"/> so nothing is inside
        /// weapons range on the single tick under test.</summary>
        private static readonly FixedVec3 AiBase = new(Fixed.FromInt(40), Fixed.Zero, Fixed.Zero);

        /// <summary>
        /// The launch decision flips EXACTLY at the exposed threshold: one unit below it the AI issues no wave at
        /// all, and at it every available unit is conscripted. Run for all three difficulties so a retune of any
        /// row of the curve is covered, not just Normal's.
        /// </summary>
        [Theory]
        [InlineData(AiDifficulty.Easy)]
        [InlineData(AiDifficulty.Normal)]
        [InlineData(AiDifficulty.Hard)]
        public void ExposedAttackThreshold_IsTheBarTheAiActuallyLaunchesAt(AiDifficulty difficulty)
        {
            int threshold = AiOpponentSystem.DifficultyProfile(difficulty).AttackThreshold;
            Assert.True(threshold >= 2,
                $"{difficulty}: the exposed attack threshold is {threshold}; the below-threshold arm needs at " +
                "least one unit to be non-vacuous.");

            GoldenHarness below = BuildAttackDecisionFixture(difficulty, threshold - 1);
            below.Host.StepOnce();
            Assert.Equal(0, CountMarchingOnEnemyBase(below.World));

            GoldenHarness atBar = BuildAttackDecisionFixture(difficulty, threshold);
            atBar.Host.StepOnce();
            Assert.Equal(threshold, CountMarchingOnEnemyBase(atBar.World));
        }

        /// <summary>
        /// Every unit the AI conscripts into a wave is sent to exactly <see cref="AiOpponentSystem.P1_BASE"/> —
        /// both the <see cref="EntityWorld.CommandGoal"/> the order records and the
        /// <see cref="EntityWorld.MoveTarget"/> movement actually steers to. This is the assertion that keeps a
        /// fixture's "defenders parked at the AI's destination" claim honest.
        /// </summary>
        [Theory]
        [InlineData(AiDifficulty.Easy)]
        [InlineData(AiDifficulty.Normal)]
        [InlineData(AiDifficulty.Hard)]
        public void ExposedAttackDestination_IsWhereALaunchedWaveIsActuallySent(AiDifficulty difficulty)
        {
            int threshold = AiOpponentSystem.DifficultyProfile(difficulty).AttackThreshold;
            GoldenHarness h = BuildAttackDecisionFixture(difficulty, threshold);
            EntityWorld world = h.World;

            h.Host.StepOnce();

            FixedVec3 expected = AiOpponentSystem.P1_BASE;
            int marching = 0;
            for (int i = 0; i < world.HighWaterMark; i++)
            {
                if (!world.IsAlive(i) || world.FactionOf[i] != Faction.Player2) continue;
                Assert.Equal(UnitCommand.AttackMove, world.CommandState[i]);
                marching++;
                Assert.Equal(expected.X.Raw, world.CommandGoal[i].X.Raw);
                Assert.Equal(expected.Y.Raw, world.CommandGoal[i].Y.Raw);
                Assert.Equal(expected.Z.Raw, world.CommandGoal[i].Z.Raw);
                Assert.Equal(expected.X.Raw, world.MoveTarget[i].X.Raw);
                Assert.Equal(expected.Y.Raw, world.MoveTarget[i].Y.Raw);
                Assert.Equal(expected.Z.Raw, world.MoveTarget[i].Z.Raw);
            }

            Assert.Equal(threshold, marching);
        }

        /// <summary>
        /// A fixture in which <c>LaunchAttack</c> is the ONLY action that can score above
        /// <c>ExecuteBestAction</c>'s 0.01 floor, so the single stepped tick isolates the attack decision:
        ///   • Player2 (the AI) holds <paramref name="availableCombatUnits"/> Idle combat units and ZERO ore, so
        ///     every ExpandSupply/Build* score gates to 0 on the <c>CanAfford*</c> check;
        ///   • Player1 keeps ONE live armed defender, so <c>ScoreRazeBuildings</c> (which outranks LaunchAttack at
        ///     0.90) returns 0 on its <c>EnemyThreatRemains</c> guard — the AI fights the army first;
        ///   • both bases are far apart, so no combat resolves on the tick under test.
        /// Authored entirely in <see cref="Fixed"/> (no <c>Fixed.FromFloat</c>) apart from the destination imported
        /// from <see cref="AiOpponentSystem.P1_BASE"/> itself.
        /// </summary>
        private static GoldenHarness BuildAttackDecisionFixture(AiDifficulty difficulty, int availableCombatUnits)
        {
            SimulationHost host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                damageTable: null,
                aiLevel: difficulty);
            host.ChecksumInterval = 1;

            EntityWorld   world     = host.World;
            BuildingStore buildings = host.Buildings;
            ResourceStore resources = host.Resources;

            // ── Player2 (AI): a completed CommandCenter (a real base) and NO ore, so it can afford nothing. ──
            int p2cc = buildings.Create(AiBase, Faction.Player2, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p2cc] = Fixed.Zero; // complete
            resources.FactionBase[(int)Faction.Player2] = AiBase;

            // ── Player2 wave: Idle / GatherState.Inactive (EntityWorld.Create defaults) so AiSnapshot counts every
            //    one of them as AvailableCombatUnits — the quantity under test. ──
            for (int i = 0; i < availableCombatUnits; i++)
            {
                int u = world.Create(AiBase + new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.FromInt(i * 2 - 4)),
                                     Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.EffectiveAttackDamage[u] = Fixed.FromInt(6);
                world.AttackRange[u]  = Fixed.FromInt(2);
                world.AttackSpeed[u]  = Fixed.FromInt(1);
                world.DamageTypeOf[u] = DamageType.Normal;
                world.ArmorTypeOf[u]  = ArmorType.Medium;
            }

            // ── Player1: a base AT the AI's real destination plus one live armed defender (the EnemyThreatRemains
            //    discriminator that suppresses the higher-scoring raze branch). Stop — it never chases. ──
            FixedVec3 enemyBase = AiOpponentSystem.P1_BASE;
            int p1cc = buildings.Create(enemyBase, Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p1cc] = Fixed.Zero; // complete
            resources.FactionBase[(int)Faction.Player1] = enemyBase;

            int defender = world.Create(enemyBase + new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero),
                                        Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(3));
            world.EffectiveAttackDamage[defender] = Fixed.FromInt(6); // > 0 ⇒ a real defender, not a gatherer
            world.AttackRange[defender]  = Fixed.FromInt(2);
            world.AttackSpeed[defender]  = Fixed.FromInt(1);
            world.DamageTypeOf[defender] = DamageType.Normal;
            world.ArmorTypeOf[defender]  = ArmorType.Medium;
            world.CommandState[defender] = UnitCommand.Stop;

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            // DW-862: `p2cc` is a BuildingStore SLOT, not an entity id — nothing here reads PerturbTargetId, so
            // pass the explicit "no perturbation target" sentinel (see AiBelowThresholdRazeTests for the rationale).
            return new GoldenHarness(host, -1);
        }

        /// <summary>Player2 units the AI has conscripted into a wave aimed at its exposed attack destination.</summary>
        private static int CountMarchingOnEnemyBase(EntityWorld world)
        {
            FixedVec3 expected = AiOpponentSystem.P1_BASE;
            int n = 0;
            for (int i = 0; i < world.HighWaterMark; i++)
            {
                if (!world.IsAlive(i) || world.FactionOf[i] != Faction.Player2) continue;
                if (world.CommandState[i] != UnitCommand.AttackMove) continue;
                if (world.CommandGoal[i].X.Raw == expected.X.Raw
                    && world.CommandGoal[i].Y.Raw == expected.Y.Raw
                    && world.CommandGoal[i].Z.Raw == expected.Z.Raw) n++;
            }
            return n;
        }
    }
}
