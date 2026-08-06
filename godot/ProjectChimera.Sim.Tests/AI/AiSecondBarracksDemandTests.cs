#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-636 — <c>AiOpponentSystem.ScoreBuildSecondBarracks</c> must gate on real production DEMAND, not on the
    /// supply-expansion CommandCenter.
    ///
    /// The defect: the second Barracks was gated <c>if (!s.HasCCExpansion) return 0f;</c>, so the AI could double
    /// unit production ONLY after committing a CommandCenter whose real payoff is +10 supply — an economic
    /// decision wired to a supply device. That coupling also forced DW-63 to merely DEPRIORITIZE the ungated
    /// supply expansion (a flat 0.25) instead of skipping it, because a hard skip would have permanently closed
    /// this gate for any <c>supply.enabled:false</c> scenario.
    ///
    /// The fix replaces the expansion gate with four demand terms — a live-Barracks count below 2, a COMPLETE
    /// first Barracks, an ore SURPLUS past <c>SECOND_BARRACKS_ORE_SURPLUS</c> (2 x COST_BARRACKS = 200), and an
    /// available army below the decisive size <c>ScoreLaunchAttack</c> tops out at (<c>_attackThreshold * 2</c>) —
    /// and lets <c>ScoreExpandSupply</c> return a hard 0 when gating is disabled.
    ///
    /// Every test drives the REAL <see cref="AiOpponentSystem.Tick"/> (no reflection, no scorer stub) and asserts
    /// on the buildings/orders the AI actually commits, which is the only externally-visible product of a scoring
    /// decision.
    ///
    /// DETERMINISM: no golden moves. The AI-bearing goldens either starve the AI of ore (GoldenScenario /
    /// MultiFactionScenario give Player2 0 ore, so <c>CanAffordBarracks</c> is false) or never complete a first
    /// Barracks inside their horizon (AiActiveScenario builds one on tick 1 with a 10s construction timer and runs
    /// exactly 300 ticks = 10s), and the ungated <c>ScoreExpandSupply</c> branch is unreachable for every shipped
    /// scenario (none authors a <c>supply</c> block, so <c>SupplyConfig.Resolve(null)</c> yields
    /// <c>enabled:true</c>).
    /// </summary>
    public class AiSecondBarracksDemandTests
    {
        private const int AI_SLOT = (int)Faction.Player2;

        /// <summary>Each <c>Tick</c> the AI first drains every COMPLETE production building it owns by queueing one
        /// order. With no FactionDefinition wired, <c>BuildingSystem.TrainUnit</c> falls back to its 100-ore unit
        /// cost, so a fixture holding one complete Barracks spends exactly this much BEFORE the scorer reads the
        /// ore balance. Every expected-ore figure below is authored-minus-this.</summary>
        private static readonly Fixed FallbackTrainCost = Fixed.FromInt(100);

        /// <summary>
        /// THE DEFECT, inverted. One COMPLETE Barracks and 300 ore — 200 of it still banked after the training
        /// drain, i.e. a genuine ore surplus past the 200 second-Barracks bar — and NO supply expansion anywhere.
        ///
        /// RED before the fix: <c>HasCCExpansion</c> is false, so the second Barracks scores 0 and the AI stands
        /// there banking ore it cannot convert. GREEN after: production demand alone commits the second Barracks.
        ///
        /// The under-construction ArcheryRange silences the tech chain (<c>HasLiveArcheryRange</c> blocks the
        /// range, <c>!HasCompleteArcheryRange</c> blocks the workshop) so the assertion turns on the gate under
        /// test rather than on the 0.50-vs-0.49 tuning margin between a second Barracks and an ArcheryRange.
        /// </summary>
        [Fact]
        public void CompleteBarracksWithOreSurplus_BuildsTheSecondBarracks_WithNoSupplyExpansion()
        {
            Fixture f = NewFixture(ore: 300, completeBarracks: 1, blockTechChain: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(2, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.CommandCenter)); // never needed an expansion
            Assert.Equal(Fixed.FromInt(100), f.Resources.Ore[AI_SLOT]);                 // 300 - 100 train - 100 Barracks
        }

        /// <summary>
        /// The ORE-SURPLUS term is load-bearing, not decoration. 250 ore leaves 150 after the training drain: the
        /// AI can plainly AFFORD a second Barracks (150 >= COST_BARRACKS 100) but is income-limited, not
        /// production-limited — a second queue would just idle beside the first.
        ///
        /// If the surplus test were dropped and the gate left at bare affordability, this row builds a Barracks.
        /// </summary>
        [Fact]
        public void OreLimited_DoesNotBuildASecondBarracks_EvenThoughItCanAffordOne()
        {
            Fixture f = NewFixture(ore: 250, completeBarracks: 1, blockTechChain: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.FromInt(150), f.Resources.Ore[AI_SLOT]); // 250 - 100 train, nothing else spent
        }

        /// <summary>
        /// The ARMY-DEMAND term is load-bearing. On Easy (attack threshold 8, aggression 0.40) an available force
        /// of 16 idle combat units is exactly the decisive size <c>ScoreLaunchAttack</c> tops its ratio out at, so
        /// more production capacity is not what this AI needs — a wave is.
        ///
        /// Easy is deliberate: it is the one difficulty whose full-strength attack score (0.40) sits BELOW the
        /// second Barracks' flat 0.50, so the two outcomes are distinguishable. Without the army term the AI
        /// builds a Barracks it does not need while a decisive army stands idle; with it, the army marches.
        /// </summary>
        [Fact]
        public void DecisiveIdleArmy_LaunchesTheWaveInsteadOfDoublingProduction()
        {
            Fixture f = NewFixture(ore: 500, completeBarracks: 1,
                                   difficulty: AiDifficulty.Easy, idleCombatUnits: 16,
                                   hostileTarget: true); // DW-783: the wave needs a real destination

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings));                    // nothing new was built at all
            Assert.Equal(Fixed.FromInt(400), f.Resources.Ore[AI_SLOT]);        // 500 - 100 train only
            for (int i = 0; i < f.World.HighWaterMark; i++)
                if (f.World.IsAlive(i) && f.World.FactionOf[i] == Faction.Player2)
                    Assert.Equal(UnitCommand.AttackMove, f.World.CommandState[i]);
        }

        /// <summary>
        /// A queue that is not yet RUNNING cannot be saturated, so there is no production bottleneck to relieve.
        /// 1000 ore and a first Barracks still under construction must not buy a second one — the AI would be
        /// paying for parallel capacity before learning whether one queue can absorb its income.
        /// </summary>
        [Fact]
        public void FirstBarracksStillUnderConstruction_DoesNotBuildASecond()
        {
            Fixture f = NewFixture(ore: 1000, underConstructionBarracks: 1);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.FromInt(1000), f.Resources.Ore[AI_SLOT]); // an incomplete Barracks trains nothing
        }

        /// <summary>
        /// The runaway guard. The gate counts LIVE Barracks, not COMPLETE ones: with a complete-only test the
        /// second Barracks is invisible to its own gate for its whole construction, so the AI re-enters the action
        /// every tick and stacks duplicate Barracks on the same <c>POS_BARRACKS_2</c> tile for as long as it can
        /// afford them. One complete + one under construction + 900 spendable ore is exactly that state.
        /// </summary>
        [Fact]
        public void SecondBarracksAlreadyUnderConstruction_NeverBuildsAThird()
        {
            Fixture f = NewFixture(ore: 1000, completeBarracks: 1, underConstructionBarracks: 1,
                                   blockTechChain: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(2, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.FromInt(900), f.Resources.Ore[AI_SLOT]); // 1000 - 100 train, no third Barracks
        }

        /// <summary>
        /// DW-636's other half. DW-63 kept the ungated supply expansion alive at a flat 0.25 purely to preserve the
        /// ungated AI's route to a second production building; DW-636 lets <c>ScoreExpandSupply</c> return a hard 0
        /// there instead. This proves the thing that 0.25 was protecting is still delivered — the ungated AI
        /// reaches its second Barracks on DEMAND, and spends nothing on a CommandCenter to get there.
        ///
        /// Runaway supply (used 500 vs cap 10) is the DW-63 saturation state: with gating disabled nothing ever
        /// blocks training, so the headroom is an uninformative -490 that must not be read.
        /// </summary>
        [Fact]
        public void UngatedSupply_StillReachesASecondBarracks_WithoutAnExpansion()
        {
            Fixture f = NewFixture(ore: 300, completeBarracks: 1, blockTechChain: true,
                                   supplyGatingEnabled: false);
            f.Resources.SupplyUsed[AI_SLOT] = 500;

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(2, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.CommandCenter));
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
        /// A bare AI decision harness: the real <see cref="AiOpponentSystem"/> over a real
        /// <see cref="BuildingStore"/>/<see cref="ResourceStore"/>/<see cref="BuildingSystem"/>, with NO enemy
        /// buildings (so the raze fallback never scores) and no CommandCenter of its own (so
        /// <c>RecalculateSupplyCaps</c> — which this harness never runs anyway — cannot matter). Driven by calling
        /// <c>Tick</c> directly rather than through a <c>SimulationHost</c>, so <c>SupplyUsed</c>/<c>SupplyCap</c>
        /// stay exactly as authored (a host would recompute both every tick from live units).
        ///
        /// All pre-placed buildings are created BEFORE the AI, because the constructor's
        /// <c>AdoptPreplacedBuildings</c> is what puts them in the training loop.
        /// </summary>
        private static Fixture NewFixture(int ore,
                                          int completeBarracks = 0,
                                          int underConstructionBarracks = 0,
                                          bool blockTechChain = false,
                                          bool supplyGatingEnabled = true,
                                          AiDifficulty difficulty = AiDifficulty.Normal,
                                          int idleCombatUnits = 0,
                                          bool hostileTarget = false)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(ore)); // seeds P1/P2 ore + the 10 base supply cap
            resources.ConfigureSupply(new SupplyConfig { Enabled = supplyGatingEnabled });

            for (int i = 0; i < completeBarracks; i++)
            {
                int b = buildings.Create(new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.FromInt(i * 4)),
                                         Faction.Player2, BuildingType.Barracks);
                buildings.ConstructionTimer[b] = Fixed.Zero; // mark complete → a RUNNING production queue
            }

            for (int i = 0; i < underConstructionBarracks; i++)
                buildings.Create(new FixedVec3(Fixed.FromInt(34), Fixed.Zero, Fixed.FromInt(i * 4)),
                                 Faction.Player2, BuildingType.Barracks); // Create seeds the full 10s timer

            if (blockTechChain)
            {
                // An ArcheryRange under construction: HasLiveArcheryRange zeroes ScoreBuildArcheryRange and
                // !HasCompleteArcheryRange zeroes ScoreBuildSiegeWorkshop, so the second-Barracks decision is the
                // only positive score left and the assertion cannot be decided by a tech-score tie-break.
                buildings.Create(new FixedVec3(Fixed.FromInt(38), Fixed.Zero, Fixed.Zero),
                                 Faction.Player2, BuildingType.ArcheryRange);
            }

            // Idle, damage-bearing Player2 combat units — Create's defaults leave them Idle/Inactive, which is
            // exactly what IsConscriptable counts as available.
            for (int i = 0; i < idleCombatUnits; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(i * 2 - 4)),
                                     Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.EffectiveAttackDamage[u] = Fixed.FromInt(6);
            }

            // DW-783 — something hostile for a launched wave to march AT. Opt-in, because only the wave-launching
            // test needs it: the build-decision tests are about scoring, not destinations. Before this batch the
            // wave destination was the hardcoded P1_BASE in every FFA match, so a wave could launch into a world
            // containing no enemy at all; it is now computed from the nearest hostile structure (else unit), and
            // without a target the wave correctly declines. Placed opposite the P2 army so it is a destination
            // only, never an engagement.
            if (hostileTarget)
                world.Create(new FixedVec3(Fixed.FromInt(-40), Fixed.Zero, Fixed.Zero),
                             Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            var buildSys = new BuildingSystem(buildings, resources);
            var ai       = new AiOpponentSystem(buildings, resources, buildSys, difficulty);
            return new Fixture(world, buildings, resources, ai);
        }

        /// <summary>Pins the documented per-tick training drain the ore expectations above are computed from — if
        /// <c>BuildingSystem</c>'s def-less fallback unit cost ever moves, this fails first and names the reason
        /// instead of leaving six opaque off-by-N ore assertions.</summary>
        [Fact]
        public void FixtureAssumption_OneCompleteBarracksDrainsTheFallbackTrainCostPerTick()
        {
            Fixture f = NewFixture(ore: 1000, completeBarracks: 1, blockTechChain: true);

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            // 1000 ore is far past the surplus bar, so the AI also builds its second Barracks this tick; the drain
            // is therefore the residual once the Barracks price is accounted for.
            Assert.Equal(2, CountAiBuildings(f.Buildings, BuildingType.Barracks));
            Assert.Equal(Fixed.FromInt(1000) - Fixed.FromInt(100) - FallbackTrainCost,
                         f.Resources.Ore[AI_SLOT]);
        }

        private static int CountAiBuildings(BuildingStore buildings)
        {
            int n = 0;
            for (int i = 0; i < buildings.Count; i++)
                if (buildings.Alive[i] && buildings.FactionOf[i] == Faction.Player2) n++;
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
