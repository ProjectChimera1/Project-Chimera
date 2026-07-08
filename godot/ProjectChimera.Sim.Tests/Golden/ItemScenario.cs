using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.15 (AC) — the ITEM / INVENTORY golden scenario. A deployed Player1 hero at the origin, a stat item and a
    /// charged consumable placed on the ground at the same spot: the golden runner orders the hero to pick up the stat
    /// item (a +50 max-health / +5 damage / +2 armor modifier materializes), pick up the consumable, use a charge, then
    /// dies and drops the stat item. The per-tick <see cref="SimChecksum"/> (v12, folding the mutable ItemStore + the
    /// per-hero inventory) captures the whole cycle — proving the fold end-to-end, integer/<see cref="Fixed"/>-only,
    /// byte-identical across two runs.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/>; Player2 is EMPTY so the float-scoring AI no-ops.
    /// </summary>
    public static class ItemScenario
    {
        public const int DefaultTicks = 120;

        public const int HeroEntityId = 0; // the hero is created FIRST → id 0
        public const int HeroSlot = 0;     // minted FIRST → slot 0
        public const int RingRef = 0;      // the stat item is placed FIRST → packed ref 0 (gen 0, slot 0)
        public const int PotionRef = 1;    // the consumable is placed SECOND → packed ref 1

        public const int PickRingTick = 2;
        public const int PickPotionTick = 12;
        public const int UsePotionTick = 25;
        public const int DeathTick = 60;

        private static readonly HeroId HeroIdentity = new HeroId(3_150_000_001UL);

        public static (GoldenHarness harness, ItemSystem items) Build()
        {
            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     AttackDamageDelta = Fixed.FromInt(5), ArmorDelta = Fixed.FromInt(2) },
                new ItemDefinition { Id = "potion", Charges = 2, EffectGraph = new HealEffect(Fixed.FromInt(75)) },
            });
            // registry sorts by Id ordinal: "potion"(0) < "ring"(1).
            int ringDef = registry.IndexOf("ring");
            int potionDef = registry.IndexOf("potion");

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),   // P1 + P2 active (P2 EMPTY → AI no-ops → cross-platform safe)
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
            int hero = w.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(HeroEntityId, hero);
            w.ApplyUnitDefinition(hero, heroDef);
            int slot = heroes.Mint(HeroIdentity, hero, level: 1, xp: Fixed.Zero,
                                   sourceDef: heroDef, ownerFaction: Faction.Player1);
            Assert.Equal(HeroSlot, slot);
            w.HeroIndex[hero] = heroes.PackRef(slot);

            // Two ground items at the origin (so the hero claims on proximity immediately).
            int ring = host.Items.Create(ringDef, 0, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            int potion = host.Items.Create(potionDef, 2, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            Assert.Equal(RingRef, ring);
            Assert.Equal(PotionRef, potion);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return (new GoldenHarness(host, hero), host.ItemSys);
        }
    }
}
