#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-63 — <see cref="AiOpponentSystem"/>'s supply-headroom scoring must be aware of a scenario that authors
    /// <c>supply.enabled:false</c> (Story 4.4's master gate).
    ///
    /// With gating disabled <c>ResourceStore.HasSupply</c> returns true unconditionally, so training is never
    /// blocked and <c>SupplyUsed</c> climbs unboundedly past <c>SupplyCap</c>. The headroom
    /// (<c>SupplyCap - SupplyUsed</c>) then saturates to a deeply-negative magnitude that carries no information —
    /// yet the pre-fix ladder read it anyway and returned its worst tier (0.95), permanently pinning the AI's single
    /// highest-scoring action on expanding a cap nobody enforces and starving Barracks/tech/attack/raze.
    ///
    /// These drive the REAL <see cref="AiOpponentSystem.Tick"/> (no reflection, no scorer stub) and assert on the
    /// building the AI actually commits to, which is the only externally-visible product of the scoring decision.
    ///
    /// Determinism: nothing here changes when gating is ENABLED (the shipped scenarios and every golden — none
    /// authors a <c>supply</c> block, so <c>SupplyConfig.Resolve(null)</c> yields <c>enabled:true</c>), which
    /// <see cref="GatedSupply_CriticalHeadroom_StillWinsOverBarracks"/> pins directly. No golden moves.
    /// </summary>
    public class AiSupplyGatingScoringTests
    {
        private const int AI_SLOT = (int)Faction.Player2;

        /// <summary>Enough ore for BOTH a CommandCenter (150) and a Barracks (100), so affordability never
        /// silently decides which action the AI picks — only the score does.</summary>
        private static readonly Fixed AmpleOre = Fixed.FromInt(200);

        /// <summary>
        /// The DEFECT. Gating is disabled and supply has run away far past the cap (headroom -490). Every other
        /// scoring input is neutral, and the one genuinely useful action — build the first Barracks (0.85) — is
        /// available and affordable.
        ///
        /// RED before the fix: the saturated headroom drops through <c>SUPPLY_CRITICAL</c> and returns 0.95, so the
        /// AI spends its tick (and 150 ore) on a supply CommandCenter that buys it nothing while it still has no
        /// production at all.
        ///
        /// The positive-headroom row is the control: the same decision must come out either way, because with the
        /// gate off the headroom MAGNITUDE is not a signal in either direction.
        /// </summary>
        [Theory]
        [InlineData(500)] // runaway supply — headroom -490, the saturated "critical" tier pre-fix
        [InlineData(0)]   // comfortable headroom +10 — control: same decision, no magnitude read
        public void UngatedSupply_NeverOutranksTheFirstBarracks(int supplyUsed)
        {
            Fixture f = NewFixture(supplyGatingEnabled: false);
            f.Resources.SupplyUsed[AI_SLOT] = supplyUsed;

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings));
            Assert.Equal(BuildingType.Barracks, FirstAiBuildingType(f.Buildings));
            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.CommandCenter));
        }

        /// <summary>
        /// The invariant guard: with gating ENABLED (the default, and every shipped scenario/golden) a critical
        /// headroom must STILL outrank the first Barracks exactly as before. This is what keeps the DW-63 fix from
        /// silently retuning the normal AI — if this flips, the fix leaked out of the ungated case.
        /// </summary>
        [Fact]
        public void GatedSupply_CriticalHeadroom_StillWinsOverBarracks()
        {
            Fixture f = NewFixture(supplyGatingEnabled: true);
            f.Resources.SupplyUsed[AI_SLOT] = 500; // headroom -490 → the enforced "critical" tier

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(1, CountAiBuildings(f.Buildings));
            Assert.Equal(BuildingType.CommandCenter, FirstAiBuildingType(f.Buildings));
        }

        /// <summary>
        /// DW-636 — the expansion is now SKIPPED outright when gating is disabled, not merely deprioritized.
        ///
        /// DW-63 could only DEPRIORITIZE it (a flat 0.25) because the expansion CommandCenter was also the gate
        /// <c>ScoreBuildSecondBarracks</c> read, so a hard 0 would have permanently closed that gate and cost an
        /// ungated-scenario AI its second production building forever. DW-636 re-gated the second Barracks on
        /// ore/army demand, so that constraint is gone — and with it the last reason to spend 150 ore on +10 to a
        /// cap nothing enforces. This is the case that used to build one: a Barracks already up (no build/tech
        /// action scores) and ample ore, so pre-DW-636 the 0.25 expansion was the only thing above the 0.01
        /// do-nothing floor and the AI committed it.
        ///
        /// <see cref="AiSecondBarracksDemandTests.UngatedSupply_StillReachesASecondBarracks_WithoutAnExpansion"/>
        /// is the other half: what the 0.25 was protecting is still delivered, via demand instead.
        /// </summary>
        [Fact]
        public void UngatedSupply_SkipsTheExpansionEntirely()
        {
            Fixture f = NewFixture(supplyGatingEnabled: false, preplaceAiBarracks: true);
            f.Resources.SupplyUsed[AI_SLOT] = 500;

            f.Ai.Tick(f.World, SimulationLoop.FixedDt);

            Assert.Equal(0, CountAiBuildings(f.Buildings, BuildingType.CommandCenter));
            Assert.Equal(AmpleOre, f.Resources.Ore[AI_SLOT]); // and the 150 ore stays banked for real production
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
        /// <see cref="BuildingStore"/>/<see cref="ResourceStore"/>/<see cref="BuildingSystem"/>, with an EMPTY world
        /// (no units → no attack/raze score) and no enemy buildings (→ no raze target). Driven by calling
        /// <c>Tick</c> directly rather than through a <c>SimulationHost</c>, so <c>SupplyUsed</c>/<c>SupplyCap</c>
        /// stay exactly as the test authored them (a host would recompute both every tick from live units).
        /// </summary>
        private static Fixture NewFixture(bool supplyGatingEnabled, bool preplaceAiBarracks = false)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(AmpleOre); // seeds Player1/Player2 ore + the 10 base supply cap
            resources.ConfigureSupply(new SupplyConfig { Enabled = supplyGatingEnabled });

            if (preplaceAiBarracks)
            {
                // Under construction (Create seeds the full 10s timer): HasLiveBarracks blocks ScoreBuildBarracks,
                // and !HasCompleteBarracks blocks the ArcheryRange → SiegeWorkshop tech chain — so the expansion is
                // the only action left with a positive score.
                buildings.Create(new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.Zero),
                                 Faction.Player2, BuildingType.Barracks);
            }

            var buildSys = new BuildingSystem(buildings, resources);
            var ai       = new AiOpponentSystem(buildings, resources, buildSys, AiDifficulty.Normal);
            return new Fixture(world, buildings, resources, ai);
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

        private static BuildingType FirstAiBuildingType(BuildingStore buildings)
        {
            for (int i = 0; i < buildings.Count; i++)
                if (buildings.Alive[i] && buildings.FactionOf[i] == Faction.Player2) return buildings.Type[i];
            return default;
        }
    }
}
