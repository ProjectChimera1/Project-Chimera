#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 3.15 — Godot-free oracles for every I/O matrix row of the item / inventory sim: pickup into a free slot
    /// (+ the stat modifier materializing in Effective*), full-inventory denial, the two-hero ascending-id race, drop
    /// removing the modifier + returning the item to the ground, consumable charge decrement / last-charge delete,
    /// use-on-empty no-op, the configured-slot-cap-below-stride reject, and hero-death dropping all items at the death
    /// position with the row cleared.
    /// </summary>
    public class ItemSystemTests
    {
        private const int RingDefId = 1;   // registry sorts by Id ordinal: "potion"(0) < "ring"(1)
        private const int PotionDefId = 0;

        private sealed class Harness
        {
            public EntityWorld World = new EntityWorld();
            public HeroStore Heroes = new HeroStore();
            public ItemStore Items = new ItemStore();
            public ModifierStore Modifiers = null!;
            public ItemSystem Sys = null!;
            public ItemRegistry Registry = null!;
        }

        private static Harness Build(int usableSlots = HeroStore.INVENTORY_SLOTS)
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     AttackDamageDelta = Fixed.FromInt(5), ArmorDelta = Fixed.FromInt(2) },
                new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = new HealEffect(Fixed.FromInt(75)) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, events: null);
            h.Sys.ConfigureUsableSlots(usableSlots);
            return h;
        }

        private static readonly UnitDefinition HeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        /// <summary>Create a hero entity + persisted row + link, at position (x,z). Returns (entityId, heroSlot).</summary>
        private static (int entity, int slot) MintHero(Harness h, ulong id, int x, int z)
        {
            int e = h.World.Create(new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z)),
                                   Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.ApplyUnitDefinition(e, HeroDef);
            int slot = h.Heroes.Mint(new HeroId(id), e, level: 1, xp: Fixed.Zero, sourceDef: HeroDef, ownerFaction: Faction.Player1);
            h.World.HeroIndex[e] = h.Heroes.PackRef(slot);
            return (e, slot);
        }

        private static UnitOrder Pickup(int entity, int itemRef) =>
            new UnitOrder(entity, UnitCommand.PickupItem, Fixed.FromRaw(itemRef), Fixed.Zero);

        // ── Row 1: pickup with room available (+ stat modifier materializes in Effective*) ──
        [Fact]
        public void Pickup_IntoFreeSlot_ClaimsAndAppliesStatModifier()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));

            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.True(h.Items.Held[isl]);
            Assert.Equal(slot, h.Items.CarrierHeroSlot[isl]);
            Assert.Equal(itemRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            // Stat modifier materialized: EffectiveMaxHealth = base 100 + 50; EffectiveAttackDamage = 20 + 5.
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[e]);
            Assert.Equal(Fixed.FromInt(25), h.World.EffectiveAttackDamage[e]);
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[e]);
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]); // order completed
        }

        // ── Row 2: pickup with the inventory full → OrderDenied, item stays on the ground ──
        [Fact]
        public void Pickup_WhenFull_Denied_ItemStaysOnGround()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 0, 0);
            // Fill every usable slot with sentinel refs (not real items — just occupancy).
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + s] = 999;

            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]); // stays on the ground
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]); // order voided
        }

        // ── Row 3: two heroes race one item → lower-id claimant wins, the other voids ──
        [Fact]
        public void TwoHeroes_RaceOneItem_LowerIdWins()
        {
            var h = Build();
            var (e1, slot1) = MintHero(h, 100, 0, 0); // lower HeroId → processed first in FoldOrder
            var (e2, slot2) = MintHero(h, 200, 0, 0);
            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));

            OrderApplier.Apply(h.World, Pickup(e1, itemRef), Faction.Player1, items: h.Sys);
            OrderApplier.Apply(h.World, Pickup(e2, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.Equal(itemRef, h.Heroes.Inventory[slot1 * HeroStore.INVENTORY_SLOTS + 0]); // lower-id got it
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot2 * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e2]); // loser's order voided (target gone)
        }

        // ── Row 4: stat item carried → dropped removes the modifier + respawns on the ground ──
        [Fact]
        public void DropStatItem_RemovesModifier_AndReturnsToGround()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[e]); // carried

            // Move the hero so the drop lands at a new position.
            h.World.Position[e] = new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.FromInt(9));
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.DropItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);

            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // modifier removed
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);
            Assert.Equal(Fixed.FromInt(9), h.Items.PosX[isl]); // respawned at the carrier's position
        }

        // ── Row 5: consumable used with charges > 1 → decrement, item remains ──
        [Fact]
        public void UseConsumable_ChargesAboveOne_Decrements_ItemRemains()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 0, 0);
            h.World.Health[e] = Fixed.FromInt(20); // damaged so the heal is observable
            int itemRef = h.Items.Create(PotionDefId, 3, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            // Place directly in the inventory (skip the pickup dance).
            h.Items.Held[FirstSlot(h, itemRef)] = true;
            h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0] = itemRef;

            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.Equal(2, h.Items.Charges[isl]);              // 3 → 2
            Assert.Equal(itemRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]); // still held
            Assert.Equal(Fixed.FromInt(95), h.World.Health[e]); // 20 + 75 heal
        }

        // ── Row 6: consumable used on the last charge → item deleted, slot cleared ──
        [Fact]
        public void UseConsumable_LastCharge_DeletesItem()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 0, 0);
            int itemRef = h.Items.Create(PotionDefId, 1, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            h.Items.Held[FirstSlot(h, itemRef)] = true;
            h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0] = itemRef;

            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);

            Assert.False(h.Items.TryResolveRef(itemRef, out _)); // freed
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Row 7: use on an empty/invalid slot → no-op ──
        [Fact]
        public void UseItem_OnEmptySlot_IsNoOp()
        {
            var h = Build();
            var (e, _) = MintHero(h, 100, 0, 0);
            h.World.Health[e] = Fixed.FromInt(20);
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(2), Fixed.Zero),
                               Faction.Player1, items: h.Sys);
            Assert.Equal(Fixed.FromInt(20), h.World.Health[e]); // unchanged — no crash
        }

        // ── Row 9: configured slot cap below the physical stride → 4th pickup denied ──
        [Fact]
        public void ConfiguredSlotCapBelowStride_DeniesBeyondCap()
        {
            var h = Build(usableSlots: 3);
            var (e, slot) = MintHero(h, 100, 0, 0);
            // Occupy the 3 usable slots.
            for (int s = 0; s < 3; s++) h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + s] = 900 + s;

            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]); // denied — the physical slots 3..5 are free but not usable
        }

        // ── Row 8: hero dies carrying items → items drop at the death position, inventory clears, revived hero empty ──
        [Fact]
        public void HeroDeath_DropsAllItems_AtDeathPosition_InventoryClears()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.True(h.Items.Held[FirstSlot(h, itemRef)]);

            h.World.Position[e] = new FixedVec3(Fixed.FromInt(7), Fixed.Zero, Fixed.FromInt(8)); // die here
            h.World.Destroy(e); // fires OnDestroy → ItemSystem death-drop

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]); // dropped to the ground
            Assert.Equal(Fixed.FromInt(7), h.Items.PosX[isl]);
            Assert.Equal(Fixed.FromInt(8), h.Items.PosZ[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── Row 8b (P8): hero killed through the REAL lethal-damage path → carried items drop, inventory clears ──
        [Fact]
        public void HeroKilledByDamage_DropsCarriedItems_AtDeathPosition_AndClearsInventory()
        {
            // Proves the OnDestroy/KillEntity coupling END-TO-END: the drop must fire from a real combat kill
            // (DamageResolver.Apply lethal path → EntityWorld.Destroy → OnDestroy → ItemSystem.OnEntityDestroyed),
            // NOT a direct world.Destroy (which Row 8 already covers). Guards against the death-drop silently
            // decoupling from the actual combat kill sequence.
            var h = Build();
            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.True(h.Items.Held[FirstSlot(h, itemRef)]); // carried

            // Move the hero to its death position, then deal LETHAL damage through the single combat code path.
            h.World.Position[e] = new FixedVec3(Fixed.FromInt(7), Fixed.Zero, Fixed.FromInt(8));
            var ctx = new DamageContext(h.World, e, ArmorType.Unarmored, Faction.Player2,
                                        DamageTable.Default, events: null, stats: null);
            bool died = DamageResolver.Apply(ctx, Fixed.FromInt(500), DamageType.Normal); // 500 vs 100+50 hp → lethal
            Assert.True(died);
            Assert.False(h.World.IsAlive(e)); // the kill sequence ran (Destroy fired OnDestroy)

            // The carried item dropped to the ground at the death position and the inventory row cleared.
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);
            Assert.Equal(Fixed.FromInt(7), h.Items.PosX[isl]);
            Assert.Equal(Fixed.FromInt(8), h.Items.PosZ[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        // ── DW-38: a HYBRID buff-consumable (charges > 0 AND a stat delta AND an effect graph) applies its carried
        //    modifier on pickup and removes it when the last charge is consumed — the behaviour the softened docs sanction. ──
        [Fact]
        public void HybridConsumable_AppliesModifierOnCarry_RemovesOnConsumeToZero()
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "elixir", Charges = 1, MaxHealthDelta = Fixed.FromInt(40),
                                     EffectGraph = new HealEffect(Fixed.FromInt(10)) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, events: null);
            h.Sys.ConfigureUsableSlots(HeroStore.INVENTORY_SLOTS);

            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(0, 1, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));

            // Carry it — the pickup path applies the hybrid's carried stat modifier.
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.True(h.Items.Held[FirstSlot(h, itemRef)]);
            Assert.Equal(Fixed.FromInt(140), h.World.EffectiveMaxHealth[e]); // 100 base + 40 carried modifier

            // Use to zero charges → item freed, slot cleared, carried modifier removed.
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);

            Assert.False(h.Items.TryResolveRef(itemRef, out _));                                       // freed at zero
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]);                           // modifier removed → base
        }

        // ── DW-38 (review): a HYBRID dropped BEFORE its last charge (charges still > 0) must also shed its carried
        //    modifier and return to the ground — the drop path, distinct from the consume-to-zero path above. ──
        [Fact]
        public void HybridConsumable_DroppedBeforeLastCharge_RemovesModifier_AndReturnsToGround()
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "elixir", Charges = 2, MaxHealthDelta = Fixed.FromInt(40),
                                     EffectGraph = new HealEffect(Fixed.FromInt(10)) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, events: null);
            h.Sys.ConfigureUsableSlots(HeroStore.INVENTORY_SLOTS);

            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(0, 2, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));

            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(Fixed.FromInt(140), h.World.EffectiveMaxHealth[e]); // 100 base + 40 carried modifier

            // Drop while charges are still 2 → modifier removed, item back on the ground at the carrier's position.
            h.World.Position[e] = new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.FromInt(9));
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.DropItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);

            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // carried modifier removed → base
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);                                // back on the ground (charges intact)
            Assert.Equal(2, h.Items.Charges[isl]);                          // NOT consumed — a drop, not a use
            Assert.Equal(Fixed.FromInt(9), h.Items.PosX[isl]);
        }

        // ── DW-38 (review): the load-bearing claim is "removed when the LAST charge is consumed". A 1-charge test only
        //    proves 1→0 removal; it cannot catch a regression that sheds the buff on ANY use. Use a 2-charge hybrid down
        //    to a NON-zero count and assert the carried modifier PERSISTS, then vanishes only at the last charge. ──
        [Fact]
        public void HybridConsumable_PartialConsume_RetainsModifier_UntilLastCharge()
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "elixir", Charges = 2, MaxHealthDelta = Fixed.FromInt(40),
                                     EffectGraph = new HealEffect(Fixed.FromInt(10)) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, events: null);
            h.Sys.ConfigureUsableSlots(HeroStore.INVENTORY_SLOTS);

            var (e, slot) = MintHero(h, 100, 5, 5);
            int itemRef = h.Items.Create(0, 2, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));

            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(Fixed.FromInt(140), h.World.EffectiveMaxHealth[e]); // 100 base + 40 carried modifier

            // Use ONCE (charges 2 → 1): item stays held and the carried modifier MUST persist — it is not the last charge.
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.True(h.Items.Held[isl]);                                 // still carried
            Assert.Equal(1, h.Items.Charges[isl]);                          // one charge spent
            Assert.Equal(Fixed.FromInt(140), h.World.EffectiveMaxHealth[e]); // buff STILL applied mid-consume

            // Use again (charges 1 → 0): now the last charge → item freed and the carried modifier removed.
            OrderApplier.Apply(h.World, new UnitOrder(e, UnitCommand.UseItem, Fixed.FromRaw(0), Fixed.Zero),
                               Faction.Player1, items: h.Sys);
            Assert.False(h.Items.TryResolveRef(itemRef, out _));            // freed at zero
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // modifier removed → base
        }

        // ── DW-34: hero already at the per-entity modifier cap picks up a STAT item → the claim is DENIED, the item stays
        //    on the ground, the tentative inventory slot is cleared, no stat bonus materializes, an OrderDenied fires. ──
        [Fact]
        public void Pickup_WhenHeroAtModifierCap_Denied_ItemStaysOnGround()
        {
            // Wire a real event queue so the denial cue is observable.
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                // Same registry as Build(): "potion"(0) < "ring"(1) by Id ordinal, so RingDefId=1 resolves.
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     AttackDamageDelta = Fixed.FromInt(5), ArmorDelta = Fixed.FromInt(2) },
                new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = new HealEffect(Fixed.FromInt(75)) },
            });
            var events = new CombatEventQueue();
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, events);
            h.Sys.ConfigureUsableSlots(HeroStore.INVENTORY_SLOTS);

            var (e, slot) = MintHero(h, 100, 5, 5);
            // Fill the modifier ring to EffectCaps.MaxModifiersPerEntity with unique-id, zero-delta permanent modifiers
            // (occupancy only — EffectiveMaxHealth stays at the base so the "no bonus materialized" assert is exact).
            for (int i = 0; i < EffectCaps.MaxModifiersPerEntity; i++)
            {
                var filler = new Modifier(0x5000_0000 + i, durationTicks: -1, StackRule.Ignore, maxStacks: 1,
                                          maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero,
                                          moveSpeedDelta: Fixed.Zero, status: StatusFlags.None,
                                          periodEffect: null, periodTicks: 0, armorDelta: Fixed.Zero);
                Assert.True(h.Modifiers.Apply(e, filler, e, Faction.Player1)); // ring not yet full → accepted
            }
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, h.Modifiers.CountAt(e)); // ring is full
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]);        // base, no item bonus yet

            // Matrix row 5 (direct): Apply on a full ring with a fresh id REFUSES with false and leaves the ring unchanged.
            var probe = new Modifier(0x5000_00FF, durationTicks: -1, StackRule.Ignore, maxStacks: 1,
                                     maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero,
                                     moveSpeedDelta: Fixed.Zero, status: StatusFlags.None,
                                     periodEffect: null, periodTicks: 0, armorDelta: Fixed.Zero);
            Assert.False(h.Modifiers.Apply(e, probe, e, Faction.Player1));           // full → refused
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, h.Modifiers.CountAt(e));  // ring unchanged by the refusal

            int itemRef = h.Items.Create(RingDefId, 0, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            // Denied: item stays on the ground, no carrier, inventory slot rolled back to empty, no stat bonus, Idle.
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);
            Assert.Equal(ItemStore.NO_CARRIER, h.Items.CarrierHeroSlot[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // no silently-inert bonus
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, h.Modifiers.CountAt(e)); // ring unchanged (item modifier refused)
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]);

            bool denied = false, pickedUp = false;
            for (int i = 0; i < events.Count; i++)
            {
                if (events.Get(i).Type == CombatEventType.OrderDenied) denied = true;
                if (events.Get(i).Type == CombatEventType.ItemPickedUp) pickedUp = true;
            }
            Assert.True(denied);    // the denial cue fired
            Assert.False(pickedUp); // and NOT a pickup cue — the deny/rollback path must never also emit ItemPickedUp
        }

        // ── DW-39: a ground item OUTSIDE PickupRadius → the far tick STEERS (writes MoveTarget + Moving, no claim); once
        //    the hero reaches the item the near tick CLAIMS it with the stat modifier applied. Covers the steering branch. ──
        [Fact]
        public void Pickup_ItemOutsideRadius_SteersThenClaimsOnArrival()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 0, 0);
            // Item well outside PickupRadius (=2 units) so the far branch runs.
            var itemPos = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10));
            int itemRef = h.Items.Create(RingDefId, 0, itemPos);

            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);

            // Far tick: steers toward the item, does NOT claim.
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(Fixed.FromInt(10), h.World.MoveTarget[e].X);
            Assert.Equal(Fixed.FromInt(10), h.World.MoveTarget[e].Z);
            Assert.True((h.World.Flags[e] & EntityFlags.Moving) != 0);            // Moving flag set
            Assert.Equal(UnitCommand.PickupItem, h.World.CommandState[e]);        // order still active
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);                                      // not claimed yet
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);

            // Model arrival by hand (no MovementSystem in this Tier-1 test), then tick again → claims.
            h.World.Position[e] = itemPos;
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.True(h.Items.Held[isl]);                                        // claimed into the first free slot
            Assert.Equal(itemRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[e]);       // stat modifier materialized
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]);              // order completed
        }

        // ── DW-34 (matrix row 3): a NON-stat item (a consumable, no stat delta) picked up while the hero is at the
        //    modifier cap still CLAIMS normally — the cap-deny gates on HasStatModifier and never blanket-denies at the cap. ──
        [Fact]
        public void Pickup_NonStatItemAtModifierCap_ClaimsNormally()
        {
            var h = Build();
            var (e, slot) = MintHero(h, 100, 5, 5);
            // Saturate the modifier ring (same filler recipe as the stat-item cap-deny test).
            for (int i = 0; i < EffectCaps.MaxModifiersPerEntity; i++)
            {
                var filler = new Modifier(0x5000_0000 + i, durationTicks: -1, StackRule.Ignore, maxStacks: 1,
                                          maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero,
                                          moveSpeedDelta: Fixed.Zero, status: StatusFlags.None,
                                          periodEffect: null, periodTicks: 0, armorDelta: Fixed.Zero);
                Assert.True(h.Modifiers.Apply(e, filler, e, Faction.Player1));
            }

            // PotionDefId is a consumable with no stat delta → HasStatModifier == false → it never consults the ring.
            int itemRef = h.Items.Create(PotionDefId, 3, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)));
            OrderApplier.Apply(h.World, Pickup(e, itemRef), Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);

            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.True(h.Items.Held[isl]);                                            // claimed despite the full ring
            Assert.Equal(itemRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]);                   // order completed, not denied
        }

        private static int FirstSlot(Harness h, int itemRef)
        {
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            return isl;
        }
    }
}
