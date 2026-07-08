#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// Story 3.16 — cross-match hero-inventory persistence. Covers <see cref="HeroProfileLoader.CaptureInventory"/>
    /// (live refs → def-ids + charges), the shape-gated <see cref="HeroProfileLoader.BuildProfile"/> inventory capture,
    /// <see cref="HeroProfileLoader.LoadInto"/> re-minting a saved loadout into <c>ItemStore</c> + <c>HeroStore.Inventory[]</c>
    /// byte-faithfully (ref→def-id→ref), and the re-minted loadout folding into a STABLE, non-empty
    /// <see cref="StartStateHash"/> (v2, no algo bump). All Godot-free (Tier-1).
    /// </summary>
    public class HeroInventoryPersistenceTests
    {
        private static ItemRegistry Reg() => new(new[]
        {
            new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50) },
            new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = new HealEffect(Fixed.FromInt(75)) },
        });

        private static PlayerProfileShape ShapeOf(params string[] keys)
        {
            var slots = new List<ProfileSlot>();
            foreach (string k in keys) slots.Add(new ProfileSlot(k, AttributeScope.Hero));
            return new PlayerProfileShape(slots);
        }

        [Fact]
        public void CaptureInventory_ResolvesRefsToDefIdsAndCharges_AscendingSlot()
        {
            var reg = Reg();
            var heroes = new HeroStore();
            var items = new ItemStore();
            int slot = heroes.Mint(new HeroId(1), entityId: 0, level: 1, xp: Fixed.Zero);

            int ringIdx = reg.IndexOf("ring"), potionIdx = reg.IndexOf("potion");
            heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0] = items.Create(ringIdx, 0, FixedVec3.Zero);
            heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 1] = items.Create(potionIdx, 2, FixedVec3.Zero);

            var captured = HeroProfileLoader.CaptureInventory(heroes, items, reg, slot);

            Assert.Equal(2, captured.Count);
            Assert.Equal("ring", captured[0].ItemId);
            Assert.Equal(0, captured[0].Charges);
            Assert.Equal("potion", captured[1].ItemId);
            Assert.Equal(2, captured[1].Charges);
        }

        [Fact]
        public void BuildProfile_CapturesInventory_OnlyWhenShapeCarriesInventoryKey()
        {
            var loadout = new List<ProfileInventoryItem> { new("ring", 0), new("potion", 2) };

            PlayerProfile with = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.level", "hero.inventory"), loadout);
            Assert.Equal(2, with.Inventory.Count);

            PlayerProfile without = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.level"), loadout);
            Assert.Empty(without.Inventory);
        }

        private static (HeroStore heroes, ItemStore items, int minted) ReMint(ItemRegistry reg, PlayerProfile profile)
        {
            var heroes = new HeroStore();
            var items = new ItemStore();
            var world = new EntityWorld();
            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var placed = new List<HeroProfileLoader.PlacedHero> { new(e, "hero") };
            int minted = HeroProfileLoader.LoadInto(heroes, placed, profile, log: null, world: world, items: items, registry: reg);
            return (heroes, items, minted);
        }

        [Fact]
        public void LoadInto_ReMintsSavedLoadout_ByteFaithful()
        {
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem> { new("ring", 0), new("potion", 2) };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);

            var (heroes, items, minted) = ReMint(reg, profile);

            Assert.Equal(1, minted);
            int r0 = heroes.Inventory[0], r1 = heroes.Inventory[1];
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, r0);
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, r1);
            Assert.True(items.TryResolveRef(r0, out int s0));
            Assert.True(items.TryResolveRef(r1, out int s1));
            Assert.Equal(reg.IndexOf("ring"),   items.DefId[s0]);
            Assert.Equal(reg.IndexOf("potion"), items.DefId[s1]);
            Assert.Equal(2, items.Charges[s1]);
            Assert.True(items.Held[s0]);
            Assert.True(items.Held[s1]);
        }

        [Fact]
        public void ReMintedLoadout_FoldsIntoStartStateHash_StableAndNonEmpty()
        {
            var model = new ScenarioData();
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem> { new("ring", 0), new("potion", 2) };
            PlayerProfile withInv = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);
            PlayerProfile noInv = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), new List<ProfileInventoryItem>());

            ulong h1 = Hash(model, reg, withInv);
            ulong h2 = Hash(model, reg, withInv);
            ulong hEmpty = Hash(model, reg, noInv);

            Assert.Equal(h1, h2);         // re-mint is deterministic → byte-identical hash
            Assert.NotEqual(h1, hEmpty);  // the loadout actually folds into the start-state hash
            Assert.Equal(2, StartStateHash.AlgoVersion); // no algo bump (already v2 from 3.15)
        }

        private static ulong Hash(ScenarioData model, ItemRegistry reg, PlayerProfile profile)
        {
            var (heroes, _, _) = ReMint(reg, profile);
            return StartStateHash.Compute(model, heroes);
        }

        // ── Story 3.16 review: re-mint variant threading a ModifierStore + world + usable-slot cap (the deploy path). ──

        private static readonly UnitDefinition HeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        private static (EntityWorld world, int entity, HeroStore heroes, ItemStore items, int minted) ReMintDeploy(
            ItemRegistry reg, PlayerProfile profile, int usableSlots = HeroStore.INVENTORY_SLOTS)
        {
            var heroes = new HeroStore();
            var items = new ItemStore();
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);

            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.ApplyUnitDefinition(e, HeroDef); // seed base stats so a stat modifier materializes in Effective*
            var placed = new List<HeroProfileLoader.PlacedHero> { new(e, "hero") };
            int minted = HeroProfileLoader.LoadInto(heroes, placed, profile, log: null, world: world,
                items: items, registry: reg, modifiers: modifiers, usableSlots: usableSlots);
            return (world, e, heroes, items, minted);
        }

        // PATCH 2 — a persisted STAT item is NOT inert on deploy: its modifier applies at re-mint, exactly as the buy path.
        [Fact]
        public void LoadInto_ReMintedStatItem_AppliesCarriedModifier()
        {
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem> { new("ring", 0, 0) }; // +50 max_health ring in slot 0
            PlayerProfile profile = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);

            var (world, e, _, _, minted) = ReMintDeploy(reg, profile);

            Assert.Equal(1, minted);
            // Same assertion the pickup/buy path uses: base 100 + carried ring 50 = 150.
            Assert.Equal(Fixed.FromInt(150), world.EffectiveMaxHealth[e]);
        }

        // PATCH 5 — a non-contiguous loadout (slots 0 and 2) round-trips to the SAME slots (not repacked to 0 and 1).
        [Fact]
        public void LoadInto_NonContiguousLoadout_RestoresExactSlots()
        {
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem> { new("ring", 0, 0), new("potion", 2, 2) };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);

            var (_, _, heroes, items, _) = ReMintDeploy(reg, profile);

            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, heroes.Inventory[0]);              // ring back in slot 0
            Assert.Equal(HeroStore.INVENTORY_EMPTY, heroes.Inventory[1]);                 // slot 1 stays empty (the gap)
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, heroes.Inventory[2]);              // potion back in slot 2
            Assert.True(items.TryResolveRef(heroes.Inventory[0], out int s0));
            Assert.True(items.TryResolveRef(heroes.Inventory[2], out int s2));
            Assert.Equal(reg.IndexOf("ring"),   items.DefId[s0]);
            Assert.Equal(reg.IndexOf("potion"), items.DefId[s2]);
        }

        // PATCH 5 — a loadout that overflows a REDUCED usable-slot cap is clamped/rejected, never landed over-capacity.
        [Fact]
        public void LoadInto_LoadoutBeyondUsableCap_IsRejected_NotOverCapacity()
        {
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem>
            {
                new("ring", 0, 0), new("potion", 2, 1), new("ring", 0, 2), // slot 2 is beyond a cap of 2
            };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);

            var (_, _, heroes, _, _) = ReMintDeploy(reg, profile, usableSlots: 2);

            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, heroes.Inventory[0]); // within cap
            Assert.NotEqual(HeroStore.INVENTORY_EMPTY, heroes.Inventory[1]); // within cap
            Assert.Equal(HeroStore.INVENTORY_EMPTY, heroes.Inventory[2]);    // beyond cap → rejected, not over-capacity
        }

        // PATCH 6 — a corrupt/hand-edited charge count is clamped to [0, def.Charges] on re-mint.
        [Fact]
        public void LoadInto_CorruptCharges_AreClamped()
        {
            var reg = Reg();
            var loadout = new List<ProfileInventoryItem>
            {
                new("potion", -5, 0),  // negative → clamps to 0
                new("potion", 99, 1),  // over the authored 3 → clamps to 3
            };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("h#1", "hero", "f", "H", null,
                1, Fixed.Zero, ShapeOf("hero.inventory"), loadout);

            var (_, _, heroes, items, _) = ReMintDeploy(reg, profile);

            Assert.True(items.TryResolveRef(heroes.Inventory[0], out int s0));
            Assert.True(items.TryResolveRef(heroes.Inventory[1], out int s1));
            Assert.Equal(0, items.Charges[s0]);
            Assert.Equal(3, items.Charges[s1]);
        }
    }
}
