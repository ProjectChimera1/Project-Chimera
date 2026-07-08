#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 3.16 — Godot-free oracles for every guard of <see cref="BuildingSystem.BuyItemCommand"/>: the happy-path
    /// spend+mint (stat modifier applied), unaffordable, full inventory, enemy building, non-shop building, out-of-range
    /// stock index, out-of-radius buyer, and the <c>items==null</c> deterministic no-op — asserting atomic no-spend on
    /// every reject. Plus a live-vs-OrderApplier parity check (BuyItem rides the shared applier).
    /// </summary>
    public class BuyItemCommandTests
    {
        private const int RingStock = 0;   // registry sorts by Id ordinal: "potion"(0) < "ring"(1); stock order is authored
        private static readonly Fixed RingCost = Fixed.FromInt(100);

        private sealed class Harness
        {
            public EntityWorld World = new EntityWorld();
            public HeroStore Heroes = new HeroStore();
            public ItemStore Items = new ItemStore();
            public BuildingStore Buildings = new BuildingStore();
            public ResourceStore Resources = new ResourceStore(Fixed.Zero);
            public CombatEventQueue Events = new CombatEventQueue();
            public ModifierStore Modifiers = null!;
            public ItemSystem Sys = null!;
            public BuildingSystem BuildSys = null!;
            public ItemRegistry Registry = null!;
            public int ShopId;
        }

        private static readonly UnitDefinition HeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        private static Harness Build(bool sellsItems = true, Faction shopFaction = Faction.Player1,
                                     string[]? stock = null, int shopX = 0, int shopZ = 0, int radius = 10,
                                     int oreBalance = 500)
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     AttackDamageDelta = Fixed.FromInt(5), CostOre = RingCost },
                new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = new HealEffect(Fixed.FromInt(75)),
                                     CostOre = Fixed.FromInt(40) },
                // Priced in BOTH resources so the atomic "check both before debiting either" contract is testable.
                new ItemDefinition { Id = "amulet", Charges = 0, MaxHealthDelta = Fixed.FromInt(30),
                                     CostOre = Fixed.FromInt(50), CostCrystal = Fixed.FromInt(60) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, h.Events);
            h.BuildSys = new BuildingSystem(h.Buildings, h.Resources, null, null, null, h.Heroes, null);
            h.ShopId = h.Buildings.Create(new FixedVec3(Fixed.FromInt(shopX), Fixed.Zero, Fixed.FromInt(shopZ)),
                                          shopFaction, BuildingType.CommandCenter, revivesHeroes: false,
                                          sellsItems: sellsItems, shopStock: stock ?? new[] { "ring" },
                                          shopRadius: Fixed.FromInt(radius));
            h.Buildings.ConstructionTimer[h.ShopId] = Fixed.Zero; // pre-built (operational) shop
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(oreBalance));
            return h;
        }

        private static (int entity, int slot) MintHero(Harness h, ulong id, int x, int z, Faction f = Faction.Player1)
        {
            int e = h.World.Create(new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z)),
                                   f, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.ApplyUnitDefinition(e, HeroDef);
            int slot = h.Heroes.Mint(new HeroId(id), e, level: 1, xp: Fixed.Zero, sourceDef: HeroDef, ownerFaction: f);
            h.World.HeroIndex[e] = h.Heroes.PackRef(slot);
            return (e, slot);
        }

        private static int Ore(Harness h) => h.Resources.Ore[(int)Faction.Player1].ToInt();
        private static int Crystal(Harness h) => h.Resources.Crystal[(int)Faction.Player1].ToInt();

        // ── Happy path: spend + mint into the first free slot, stat modifier applied ──
        [Fact]
        public void Buy_Affordable_WithRoom_SpendsAndMints()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.True(ok);
            Assert.Equal(oreBefore - 100, Ore(h));
            int itemRef = h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0];
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, itemRef);
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.True(h.Items.Held[isl]);
            Assert.Equal(slot, h.Items.CarrierHeroSlot[isl]);
            // Stat modifier applied on purchase: EffectiveMaxHealth = 100 + 50; EffectiveAttackDamage = 20 + 5.
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[e]);
            Assert.Equal(Fixed.FromInt(25), h.World.EffectiveAttackDamage[e]);
        }

        // ── Unaffordable → reject, no spend, no mint ──
        [Fact]
        public void Buy_Unaffordable_RejectsWithNoSpend()
        {
            var h = Build(oreBalance: 50); // < 100 cost
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Full inventory → reject, no spend ──
        [Fact]
        public void Buy_FullInventory_RejectsWithNoSpend()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 2, 0);
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + s] = 999;
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── Enemy building → silent reject (anti-cheat), no spend ──
        [Fact]
        public void Buy_EnemyBuilding_RejectsWithNoSpend()
        {
            var h = Build(shopFaction: Faction.Player2);
            var (e, _) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── Non-shop building → reject, no spend ──
        [Fact]
        public void Buy_NonShopBuilding_RejectsWithNoSpend()
        {
            var h = Build(sellsItems: false);
            var (e, _) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── Out-of-range stock index → reject, no spend ──
        [Fact]
        public void Buy_BadStockIndex_RejectsWithNoSpend()
        {
            var h = Build();
            var (e, _) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, stockIndex: 5, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── Buyer out of shop_radius → reject, no spend (anti-cheat proximity in-sim) ──
        [Fact]
        public void Buy_OutOfRadius_RejectsWithNoSpend()
        {
            var h = Build(radius: 5);
            var (e, slot) = MintHero(h, 100, 50, 0); // far away
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Enemy hero buyer at own shop → reject, no spend (owned-hero gate) ──
        [Fact]
        public void Buy_EnemyHeroBuyer_RejectsWithNoSpend()
        {
            var h = Build();
            var (e, _) = MintHero(h, 100, 2, 0, f: Faction.Player2);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── items == null → deterministic no-op ──
        [Fact]
        public void Buy_NullItems_IsNoOp()
        {
            var h = Build();
            var (e, _) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, items: null, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
        }

        // ── Crystal-priced item, affordable in both resources → spends BOTH atomically and mints ──
        [Fact]
        public void Buy_CrystalPricedItem_Affordable_SpendsBothAndMints()
        {
            var h = Build(stock: new[] { "amulet" });
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(500));
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h), crystalBefore = Crystal(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, stockIndex: 0, e, h.Sys, h.Events);

            Assert.True(ok);
            Assert.Equal(oreBefore - 50, Ore(h));
            Assert.Equal(crystalBefore - 60, Crystal(h));
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Crystal-short but ore-rich → reject WITHOUT touching ore (the both-before-either atomicity contract) ──
        [Fact]
        public void Buy_CrystalShortButOreRich_RejectsWithoutSpendingOre()
        {
            var h = Build(stock: new[] { "amulet" });              // ore balance 500 ≥ the 50 ore cost
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(10)); // < the 60 crystal cost
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h), crystalBefore = Crystal(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, stockIndex: 0, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));         // ore left untouched though affordable — no partial spend
            Assert.Equal(crystalBefore, Crystal(h)); // crystal untouched
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Shop still under construction → reject, no spend (guard-parity with Train/Revive) ──
        [Fact]
        public void Buy_UnderConstructionShop_RejectsWithNoSpend()
        {
            var h = Build();
            h.Buildings.ConstructionTimer[h.ShopId] = Fixed.FromInt(5); // not yet operational
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            bool ok = h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, RingStock, e, h.Sys, h.Events);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Parity: BuyItem through the shared OrderApplier executes identically to the direct command ──
        [Fact]
        public void Buy_ThroughOrderApplier_ExecutesLikeDirectCommand()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 2, 0);
            int oreBefore = Ore(h);

            var order = new UnitOrder(h.ShopId, UnitCommand.BuyItem, Fixed.FromRaw(RingStock), Fixed.FromRaw(e));
            OrderApplier.Apply(h.World, in order, Faction.Player1, buildings: h.BuildSys, items: h.Sys, events: h.Events);

            Assert.Equal(oreBefore - 100, Ore(h));
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }
    }
}
