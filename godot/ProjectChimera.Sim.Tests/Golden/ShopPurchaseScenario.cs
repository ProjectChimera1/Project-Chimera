using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.16 (AC) — the SHOP-PURCHASE golden scenario. A deployed Player1 hero next to a <c>sells_items</c> shop
    /// building, with a starting ore balance: the golden runner issues a <c>BuyItem</c> order that (at exec-tick) spends
    /// ore atomically and mints the stat item into the hero's inventory. The per-tick <see cref="SimChecksum"/> (v12,
    /// folding the mutable ItemStore + per-hero inventory + the ResourceStore spend) captures the whole cycle — proving
    /// the buy mint is byte-identical across two runs with NO new fold / algo bump.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/>; Player2 is EMPTY so the float-scoring AI no-ops.
    /// </summary>
    public static class ShopPurchaseScenario
    {
        public const int DefaultTicks = 60;

        public const int HeroEntityId = 0; // the hero is created FIRST → id 0
        public const int RingStock = 0;    // the shop stocks ["ring"] → stock index 0
        public const int BuyTick = 5;

        private static readonly HeroId HeroIdentity = new HeroId(3_160_000_001UL);

        public static (GoldenHarness harness, ItemSystem items, BuildingSystem buildSys, int shopId) Build()
        {
            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     AttackDamageDelta = Fixed.FromInt(5), CostOre = Fixed.FromInt(100) },
            });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                itemRegistry: registry);
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;
            HeroStore heroes = host.Heroes;

            var heroDef = new UnitDefinition
            {
                Id = "hero_unit", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
            };
            int hero = w.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero),
                                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(HeroEntityId, hero);
            w.ApplyUnitDefinition(hero, heroDef);
            int slot = heroes.Mint(HeroIdentity, hero, level: 1, xp: Fixed.Zero,
                                   sourceDef: heroDef, ownerFaction: Faction.Player1);
            w.HeroIndex[hero] = heroes.PackRef(slot);

            // A pre-built shop at the origin, in range of the hero at (2,0).
            int shopId = host.Buildings.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                               Faction.Player1, BuildingType.CommandCenter, revivesHeroes: false,
                                               sellsItems: true, shopStock: new[] { "ring" }, shopRadius: Fixed.FromInt(10));
            host.Buildings.ConstructionTimer[shopId] = Fixed.Zero; // operational
            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(300));

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return (new GoldenHarness(host, hero), host.ItemSys, host.BuildSys, shopId);
        }
    }
}
