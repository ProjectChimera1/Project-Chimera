using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.14 (AC) — the HERO-REVIVAL golden scenario. A deployed level-3 hero (with per-level growth) at a Player1
    /// building flagged <c>revives_heroes</c> is KILLED (the golden runner destroys its entity at <see cref="DeathTick"/>),
    /// transitions to awaiting-revival, is revived (the runner issues <see cref="UnitCommand.ReviveHero"/> each tick — it
    /// succeeds once the hero is awaiting), counts down deterministically, and respawns at the building with retained
    /// Level/Xp, the authored HP fraction, and re-materialized growth. The per-tick <see cref="SimChecksum"/> (v11,
    /// folding the four reserved revival fields at their now-mutating values) captures the whole cycle — proving the
    /// reserved-field fold end-to-end, integer/<see cref="Fixed"/>-only, byte-identical across two runs.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/> (cost ints; time 1.0 / HP 0.5 are exact in 16.16);
    /// Player2 is EMPTY so the float-scoring AI no-ops. Compared on both CI legs.
    /// </summary>
    public static class HeroRevivalScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples. The full death→revive→respawn cycle
        /// (death @10, countdown 30 ticks, respawn @~41) fits comfortably.</summary>
        public const int DefaultTicks = 300;

        /// <summary>The hero entity is created FIRST, so its id is deterministically 0.</summary>
        public const int HeroEntityId = 0;
        /// <summary>The revive building is placed FIRST, so its id is deterministically 0.</summary>
        public const int BuildingId = 0;
        /// <summary>The hero is minted FIRST, so its HeroStore slot is deterministically 0.</summary>
        public const int HeroSlot = 0;
        /// <summary>The runner destroys the hero entity at this tick index to simulate a combat death.</summary>
        public const int DeathTick = 10;

        private static readonly HeroId HeroIdentity = new HeroId(3_140_000_014UL);

        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (P2 EMPTY → AI no-ops → cross-platform safe)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            // Revival rule: enabled, level-scaled cost (affordable), 1s countdown (30 ticks), respawn at 50% HP.
            host.RevivalRuntime.Configure(new RevivalRule
            {
                Enabled = true,
                CostOreBase = 100, CostOrePerLevel = 25, CostCrystalBase = 0, CostCrystalPerLevel = 0,
                TimeBaseSeconds = 1f, TimePerLevelSeconds = 0f, ReviveHpFraction = 0.5f,
            });
            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(10000));

            EntityWorld w = host.World;
            HeroStore heroes = host.Heroes;

            // ── The deployed hero (id 0), Player1, at level 3 with per-level growth so respawn's GrowthStacksApplied reset
            //    re-materializes growth into the folded Effective* stats. ──
            var heroDef = new UnitDefinition
            {
                Id = "hero_unit", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1,
                Hero = new HeroDefinition { MaxLevel = 10, BaseXp = 50, XpGrowth = 1f,
                                            HealthPerLevel = 10, DamagePerLevel = 2, ArmorPerLevel = 1 },
            };
            int hero = w.Create(new FixedVec3(Fixed.FromInt(0), Fixed.Zero, Fixed.FromInt(0)),
                                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(HeroEntityId, hero);
            w.ApplyUnitDefinition(hero, heroDef);

            int slot = heroes.Mint(HeroIdentity, hero, level: 3, xp: Fixed.Zero,
                maxLevel: 10, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(30),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: heroDef, ownerFaction: Faction.Player1);
            Assert.Equal(HeroSlot, slot);
            w.HeroIndex[hero] = heroes.PackRef(slot);

            // ── The revive building (id 0), Player1, pre-built, flagged revives_heroes, offset from the hero. ──
            int bId = host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(5)), preBuilt: true, revivesHeroes: true);
            Assert.Equal(BuildingId, bId);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, hero);
        }
    }
}
