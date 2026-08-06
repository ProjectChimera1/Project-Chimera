#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;   // SimulationHost — referenced by the fixture's doc comment
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-644 — <c>AiOpponentSystem.ScoreRazeBuildings</c> must measure the raze wave in units that can actually
    /// damage a STRUCTURE, not in raw availability.
    ///
    /// The defect: the scorer asked only how MANY units were free (<c>AvailableCombatUnits</c>), while
    /// <c>DoRazeBuildings</c> then skipped every unit whose <see cref="AttackDomain"/> lacks
    /// <see cref="AttackDomain.Structure"/> (a unit that cannot hit buildings would just self-revert via combat's
    /// AttackBuilding guard). An AI whose free units were all air-only / anti-unit-only therefore scored RazeBuildings
    /// at a flat 0.90 EVERY tick and issued ZERO orders — an indefinite no-op. Worse, 0.90 out-bids
    /// <c>ScoreBuildBarracks</c> (0.85), the ArcheryRange, the SiegeWorkshop and the second Barracks, so the AI also
    /// stopped producing and could never grow a force that WOULD be able to raze. Same count-vs-dispatch mismatch
    /// family as DW-202 (which closed the zero-damage half).
    ///
    /// The fix adds <c>AiSnapshot.AvailableRazeCapableUnits</c>, counted with the SAME shared
    /// <c>CanRazeStructures</c> predicate the dispatcher filters on, and gates BOTH of the scorer's commit bars
    /// (the full-wave bar and the below-threshold stall-breaker) on it.
    ///
    /// Every test drives the REAL <see cref="AiOpponentSystem.Tick"/> and asserts on the orders/buildings the AI
    /// actually commits — the only externally-visible product of a scoring decision.
    ///
    /// DETERMINISM: no golden moves. <c>attack_domains</c> is unauthored in all shipped content, so
    /// <c>UnitDefinition.ParsedAttackDomains</c> and <c>EntityWorld.Create</c> both yield
    /// <see cref="AttackDomain.All"/> and the new count is identical to <c>AvailableCombatUnits</c> everywhere a
    /// checksum is recorded. The one golden that authors a restricted domain (<c>CombatAirGroundScenario</c>) puts it
    /// on a PLAYER1 unit and leaves Player2 empty, so its AI no-ops exactly as before.
    /// </summary>
    public class AiRazeDomainCapabilityTests
    {
        private const int AI_SLOT = (int)Faction.Player2;

        /// <summary>DW-125 — the Normal attack threshold read SYMBOLICALLY from
        /// <see cref="AiOpponentSystem.DifficultyProfile"/>, so a future difficulty retune moves these fixtures with
        /// it instead of silently sliding them onto the other commit bar.</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        /// <summary>
        /// THE DEFECT, inverted — the starved half. A full-strength free force (>= the Normal threshold) that is
        /// ENTIRELY air-only, an enemy base still standing, no live defenders, ZERO ore and no production building.
        ///
        /// RED before the fix: raw availability clears the full-wave bar, RazeBuildings pins 0.90, the dispatcher
        /// skips all six units on the Structure-domain filter and the AI does literally nothing — this tick and every
        /// tick after it. GREEN after: the raze scores 0 because no free unit can hit a structure, so the AI falls
        /// through to the highest remaining action — an attack wave, whose AttackMove units at least march on the
        /// enemy and let combat's own domain rules decide what they can engage.
        /// </summary>
        [Fact]
        public void AllAirOnlyForce_DoesNotPinAnUnexecutableRaze_AndActsInstead()
        {
            Fixture f = NewFixture(ore: 0, freeUnits: NormalAttackThreshold + 1, razeCapableUnits: 0,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(0, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding)); // it could never raze
            Assert.Equal(NormalAttackThreshold + 1,
                         CountAiUnitsWithCommand(f.World, UnitCommand.AttackMove));        // ...so it did something else
        }

        /// <summary>
        /// THE DEFECT, inverted — the production half, and the one the ledger names explicitly. Same all-air-only
        /// force, but this AI can afford a Barracks and owns none.
        ///
        /// RED before the fix: the unexecutable raze's 0.90 out-bids BuildBarracks' 0.85 forever, so the AI neither
        /// razes nor builds — it banks its ore and never produces a unit that could finish the match. GREEN after:
        /// the raze scores 0 and BuildBarracks wins, which is exactly the "falls through to BuildBarracks etc."
        /// behaviour DW-644 asks for.
        /// </summary>
        [Fact]
        public void AllAirOnlyForce_FallsThroughToBuildBarracks()
        {
            Fixture f = NewFixture(ore: 100, freeUnits: NormalAttackThreshold + 1, razeCapableUnits: 0,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.Zero, f.Resources.Ore[AI_SLOT]);                             // 100 - COST_BARRACKS
            Assert.Equal(0, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding));
        }

        /// <summary>
        /// The CONTROL — the fix must not over-correct. The identical fixture with the unauthored default
        /// <see cref="AttackDomain.All"/> still razes: every free unit is raze-capable, so the full-wave bar is met
        /// and the 0.90 raze correctly out-bids the Barracks. This is the row that proves the two tests above turn on
        /// the domain filter and not on some accidental property of the fixture.
        /// </summary>
        [Fact]
        public void DefaultDomainForce_StillRazes_AndDoesNotBuildInstead()
        {
            int units = NormalAttackThreshold + 1;
            Fixture f = NewFixture(ore: 100, freeUnits: units, razeCapableUnits: units,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(units, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding));
            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.FromInt(100), f.Resources.Ore[AI_SLOT]); // nothing spent — it razed instead
        }

        /// <summary>
        /// The count is load-bearing, not merely its non-emptiness. A MIXED force one unit past the threshold, of
        /// which only two can hit a structure, does not field a full-strength raze wave — it fields a two-unit one.
        /// The commit bar is "at ATTACK strength" (the same bar <c>ScoreLaunchAttack</c> uses), so it must be read in
        /// the units that will actually receive the order.
        ///
        /// RED before the fix: raw availability clears the bar, so the AI dribbles two units at the enemy base and
        /// skips the Barracks that would have given it a real wave. GREEN after: it builds the Barracks.
        ///
        /// A "presence" fix (<c>any raze-capable unit</c>) instead of a COUNT would leave this row red.
        /// </summary>
        [Fact]
        public void MixedForceBelowThresholdInRazeCapableUnits_BuildsInsteadOfDribblingAPartialWave()
        {
            Fixture f = NewFixture(ore: 100, freeUnits: NormalAttackThreshold + 1, razeCapableUnits: 2,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(0, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding));
        }

        /// <summary>
        /// The Story 2.13 below-threshold STALL-BREAKER still fires — DW-644 narrows WHICH remnant qualifies, it does
        /// not remove the branch. A raze-capable remnant under the threshold, with a live CommandCenter, no
        /// production building and no ore, still commits to the raze rather than hanging next to the last enemy base.
        /// </summary>
        [Fact]
        public void RazeCapableRemnantBelowThreshold_StillTakesTheStallBreaker()
        {
            int remnant = NormalAttackThreshold - 2;
            Fixture f = NewFixture(ore: 0, freeUnits: remnant, razeCapableUnits: remnant,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(remnant, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding));
        }

        /// <summary>
        /// An OVER-CORRECTION guard, not a RED-before row — stated plainly because the distinction matters when
        /// someone next edits this scorer. An air-only remnant below the threshold, starved of ore and production,
        /// is the one state where DW-644's stall-breaker change is behaviourally INVISIBLE: pre-fix the bare
        /// <c>AvailableCombatUnits &gt; 0</c> pinned 0.90 on a raze that issued nothing, post-fix the raze scores 0 —
        /// and every other action is zero either way, because the stall-breaker's own gate requires the AI to afford
        /// nothing. So this row is green on both sides by construction.
        ///
        /// What it does buy: it pins that such an AI issues no unexecutable order AND is not left wedged — hand it
        /// exactly one Barracks' worth of ore and it builds on the very next tick.
        ///
        /// The stall-breaker line's real teeth live in
        /// <see cref="AllAirOnlyForce_DoesNotPinAnUnexecutableRaze_AndActsInstead"/>: an all-air-only force ABOVE the
        /// threshold falls past the full-wave bar into this same branch, so fixing only the full-wave read leaves
        /// that row red (verified by mutation).
        /// </summary>
        [Fact]
        public void AirOnlyRemnantBelowThreshold_IsNotFrozen_AndBuildsAsSoonAsItCanAfford()
        {
            int remnant = NormalAttackThreshold - 2;
            Fixture f = NewFixture(ore: 0, freeUnits: remnant, razeCapableUnits: 0,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);
            Assert.Equal(0, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding)); // it never could
            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.Barracks));         // and it had no ore

            f.Resources.AddOre(Faction.Player2, Fixed.FromInt(100));
            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings, BuildingType.Barracks));
        }

        /// <summary>
        /// The dispatcher's own guard, kept honest from the other direction: the mixed force's TWO raze-capable units
        /// are the only ones an executed raze ever orders. Drives the full-wave path by making the raze-capable count
        /// itself clear the threshold while the remaining units stay air-only, so the wave commits and the air-only
        /// units are provably left available rather than leaked into an order they cannot carry out.
        /// </summary>
        [Fact]
        public void ExecutedRaze_OrdersOnlyTheRazeCapableUnits_AndLeavesTheRestAvailable()
        {
            int capable = NormalAttackThreshold;
            Fixture f = NewFixture(ore: 0, freeUnits: capable + 3, razeCapableUnits: capable,
                                   aiCommandCenter: true, enemyBuilding: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(capable, CountAiUnitsWithCommand(f.World, UnitCommand.AttackBuilding));
            Assert.Equal(3, CountAiUnitsWithCommand(f.World, UnitCommand.Idle)); // untouched, still conscriptable
        }

        // ── Fixture ───────────────────────────────────────────────────────────

        private readonly struct Fixture
        {
            public Fixture(EntityWorld world, BuildingStore buildings, ResourceStore resources, AiOpponentSystem ai)
            {
                World = world; Buildings = buildings; Resources = resources; Ai = ai;
            }

            public EntityWorld      World     { get; }
            public BuildingStore    Buildings { get; }
            public ResourceStore    Resources { get; }
            public AiOpponentSystem Ai        { get; }
        }

        /// <summary>
        /// A bare AI decision harness — the real <see cref="AiOpponentSystem"/> over a real
        /// <see cref="BuildingStore"/>/<see cref="ResourceStore"/>/<see cref="BuildingSystem"/>, driven by calling
        /// <c>Tick</c> directly (a <see cref="SimulationHost"/> would recompute supply every tick and run combat,
        /// both of which would blur the DECISION under test).
        ///
        /// <paramref name="razeCapableUnits"/> of the <paramref name="freeUnits"/> keep the unauthored default
        /// <see cref="AttackDomain.All"/>; the rest are authored <see cref="AttackDomain.Air"/> — anti-air-only, the
        /// canonical unit that cannot touch a building. All of them are Idle, non-gathering and damage-bearing, i.e.
        /// conscriptable, so the two counts differ ONLY in the domain term.
        ///
        /// The AI deliberately owns no production building (so <c>ScoreBuildBarracks</c> is live at 0.85) and the
        /// default supply cap of 10 against 0 used leaves the headroom ladder silent, so the only scores in play are
        /// the raze, the Barracks and the attack.
        /// </summary>
        private static Fixture NewFixture(int ore, int freeUnits, int razeCapableUnits,
                                          bool aiCommandCenter, bool enemyBuilding,
                                          AiDifficulty difficulty = AiDifficulty.Normal)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(ore)); // seeds P1/P2 ore + the 10 base supply cap

            if (aiCommandCenter)
            {
                // The stall-breaker's fence discriminator: a real AI base. Never adopted into the training loop
                // (AdoptPreplacedBuildings takes Barracks/ArcheryRange/SiegeWorkshop only), so it drains no ore.
                int cc = buildings.Create(new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.Zero),
                                          Faction.Player2, BuildingType.CommandCenter);
                buildings.ConstructionTimer[cc] = Fixed.Zero; // complete
            }

            if (enemyBuilding)
            {
                // A live hostile structure ⇒ EnemyBuildingExists. No enemy UNITS anywhere, so EnemyThreatRemains is
                // false and the raze fallback is genuinely on the table.
                int eb = buildings.Create(new FixedVec3(Fixed.FromInt(-30), Fixed.Zero, Fixed.Zero),
                                          Faction.Player1, BuildingType.CommandCenter);
                buildings.ConstructionTimer[eb] = Fixed.Zero;
            }

            for (int i = 0; i < freeUnits; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(i * 2)),
                                     Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.EffectiveAttackDamage[u] = Fixed.FromInt(6); // damage-bearing ⇒ conscriptable (DW-202/DW-643)
                if (i >= razeCapableUnits)
                    world.AttackDomainOf[u] = AttackDomain.Air;    // anti-air only — can never damage a structure
            }

            var buildSys = new BuildingSystem(buildings, resources);
            var ai       = new AiOpponentSystem(buildings, resources, buildSys, difficulty);
            return new Fixture(world, buildings, resources, ai);
        }

        private static int CountAiUnitsWithCommand(EntityWorld world, UnitCommand command)
        {
            int n = 0;
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == Faction.Player2 && world.CommandState[i] == command) n++;
            return n;
        }

        private static int CountAiBuildings(BuildingStore buildings, BuildingType type)
        {
            int n = 0;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings.Alive[i] && buildings.FactionOf[i] == Faction.Player2 && buildings.Type[i] == type) n++;
            return n;
        }
    }
}
