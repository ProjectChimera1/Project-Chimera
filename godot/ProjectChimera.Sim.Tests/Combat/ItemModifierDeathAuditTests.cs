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
    /// DW-489 — the EXTERNAL callers of <see cref="ModifierStore.Apply"/> / <see cref="ModifierStore.RemoveByModifierId"/>
    /// against those methods' DW-325/DW-491 post-condition: <b>they can DESTROY (and recycle) the host before returning</b>
    /// (the ceiling-collapse death raised inside <c>ApplyStatDeltas</c> when a NET-NEGATIVE MaxHealth change takes
    /// <c>EffectiveMaxHealth</c> from above zero to exactly zero). Only the three INTERNAL <c>ApplyStatDeltas</c> callers
    /// were guarded when that kill landed; <see cref="ItemSystem"/> was not, and it both READS the return value as
    /// claim-success and calls the now-lethal removal while a ring slot is half-updated:
    ///
    /// <list type="bullet">
    /// <item><b>Pickup.</b> <c>ApplyItemStatModifier</c> forwards <c>Apply</c>'s raw <c>true</c>, which means "installed",
    ///   NOT "installed AND the carrier lived". A net-negative-MaxHealth ("cursed") item that kills its own claimant
    ///   therefore still announced <c>ItemPickedUp</c> — AFTER the death drop's <c>ItemDropped</c> — and ran
    ///   <c>EndPickupOrder</c>'s command-state writes onto a destroyed, recyclable slot.</item>
    /// <item><b>Drop.</b> <c>DropOne</c> removed the modifier while its ring slot still referenced the item, so the lethal
    ///   removal re-entered <c>OnEntityDestroyed → DropAll → DropOne</c> on that same in-flight slot and the single drop
    ///   was announced TWICE.</item>
    /// <item><b>Consume.</b> <c>UseItemCommand</c>'s last-charge branch did the same, so the re-entrant sweep flipped a
    ///   spent consumable onto the ground (a phantom <c>ItemDropped</c>) microseconds before this call freed it.</item>
    /// </list>
    ///
    /// <para>Every test here is RED without the fix and non-vacuous: each asserts the collapse really killed the carrier,
    /// and the two "teeth" tests keep the fix from degenerating into "stop dropping / stop announcing". The scenarios are
    /// content-gated in shipped data (no shipped item authors a negative MaxHealth delta), which is exactly why they need
    /// a Godot-free oracle rather than a golden. <see cref="Fixed"/> only; no float, no RNG.</para>
    /// </summary>
    public class ItemModifierDeathAuditTests
    {
        // The debuff that parks a carrier just above the collapse threshold. Distinct from ItemSystem.ItemModifierIdBase.
        private const int WeakenModifierId = 7777;

        private sealed class Harness
        {
            public EntityWorld       World     = new EntityWorld();
            public HeroStore         Heroes    = new HeroStore();
            public ItemStore         Items     = new ItemStore();
            public CombatEventQueue  Events    = new CombatEventQueue();
            public ModifierStore     Modifiers = null!;
            public ItemSystem        Sys       = null!;
            public ItemRegistry      Registry  = null!;

            /// <summary>How many events of <paramref name="type"/> the (presentation-only) queue holds.</summary>
            public int CountOf(CombatEventType type)
            {
                int n = 0;
                for (int i = 0; i < Events.Count; i++) if (Events.Get(i).Type == type) n++;
                return n;
            }

            public int DefIndex(string id) => Registry.IndexOf(id);
        }

        /// <summary>Registry: a benign +50 ring, a CURSED −100 ring (the net-negative-MaxHealth item DW-489 names), a
        /// hybrid +50 elixir with one charge, and a plain stat-less trinket used as the "other carried item".</summary>
        private static Harness Build()
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            h.Registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring",     Charges = 0, MaxHealthDelta = Fixed.FromInt(50) },
                new ItemDefinition { Id = "cursed",   Charges = 0, MaxHealthDelta = Fixed.FromInt(-100) },
                new ItemDefinition { Id = "elixir",   Charges = 1, MaxHealthDelta = Fixed.FromInt(50),
                                     EffectGraph = new HealEffect(Fixed.FromInt(1)) },
                new ItemDefinition { Id = "trinket",  Charges = 0 }, // no stat delta ⇒ no modifier, still droppable
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, h.Modifiers, h.Registry, h.Events);
            return h;
        }

        private static readonly UnitDefinition HeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        /// <summary>Create a hero entity + persisted row + link at the origin. Returns (entityId, heroSlot).</summary>
        private static (int entity, int slot) MintHero(Harness h)
        {
            int e = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.ApplyUnitDefinition(e, HeroDef);
            int slot = h.Heroes.Mint(new HeroId(1UL), e, level: 1, xp: Fixed.Zero, sourceDef: HeroDef,
                                     ownerFaction: Faction.Player1);
            h.World.HeroIndex[e] = h.Heroes.PackRef(slot);
            return (e, slot);
        }

        /// <summary>Drive the REAL pickup path (order → tick) for a ground item at the hero's position.</summary>
        private static void PickUp(Harness h, int entity, int itemRef)
        {
            var order = new UnitOrder(entity, UnitCommand.PickupItem, Fixed.FromRaw(itemRef), Fixed.Zero);
            OrderApplier.Apply(h.World, order, Faction.Player1, items: h.Sys);
            h.Sys.Tick(h.World, SimulationLoop.FixedDt);
        }

        /// <summary>A permanent −<paramref name="amount"/> MaxHealth debuff, applied through the store so the carrier
        /// sits just ABOVE the collapse threshold: removing a carried +50 then lands exactly on zero.</summary>
        private static void Weaken(Harness h, int entity, int amount) =>
            h.Modifiers.Apply(entity,
                new Modifier(WeakenModifierId, durationTicks: -1, StackRule.Ignore, maxStacks: 1,
                             maxHealthDelta: Fixed.FromInt(-amount), attackDamageDelta: Fixed.Zero,
                             moveSpeedDelta: Fixed.Zero, status: StatusFlags.None, periodEffect: null, periodTicks: 0),
                entity, Faction.Player1);

        // ─────────────────────────────── Pickup: Apply's `true` is not "the host lived" ───────────────────────────────

        [Fact]
        public void CursedPickup_ThatKillsItsClaimant_AnnouncesNoPickup_AndLeavesTheItemOnTheGround()
        {
            // A −100 MaxHealth item on a 100-ceiling hero: the install collapses the ceiling 100 → 0 and the DW-325 kill
            // fires INSIDE ModifierStore.Apply. Apply still returns true ("installed"), so the pre-fix pickup site sailed
            // past its own guard and pushed ItemPickedUp for a claim whose item the death hook had ALREADY dropped —
            // announcing the pickup AFTER the drop — then wrote command state onto the destroyed slot.
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("cursed"), 0, FixedVec3.Zero);

            PickUp(h, e, itemRef);

            Assert.False(h.World.IsAlive(e));                        // the collapse really killed the claimant (non-vacuous)
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);                         // death drop returned it to the ground
            Assert.Equal(ItemStore.NO_CARRIER, h.Items.CarrierHeroSlot[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(1, h.CountOf(CombatEventType.ItemDropped)); // the death drop, once
            Assert.Equal(0, h.CountOf(CombatEventType.ItemPickedUp)); // RED pre-fix: a corpse "picked up" the item
        }

        [Fact]
        public void OrdinaryPickup_StillAnnouncesThePickup_AndKeepsTheItem()
        {
            // Tooth for the guard above: it must fire ONLY when the claimant died, never suppress a normal claim.
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("ring"), 0, FixedVec3.Zero);

            PickUp(h, e, itemRef);

            Assert.True(h.World.IsAlive(e));
            Assert.True(h.Items.Held[FirstSlot(h, itemRef)]);
            Assert.Equal(itemRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[e]);
            Assert.Equal(1, h.CountOf(CombatEventType.ItemPickedUp));
            Assert.Equal(0, h.CountOf(CombatEventType.ItemDropped));
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[e]); // EndPickupOrder still runs for a live claimant
        }

        // ───────────────────────── Drop: the lethal removal re-enters DropAll on the in-flight slot ─────────────────────

        [Fact]
        public void DropStatItem_WhoseRemovalKillsTheCarrier_AnnouncesTheDropExactlyOnce()
        {
            // Carry the +50 ring, then weaken by 100: ceiling = 100 + 50 − 100 = 50, alive. Dropping the ring reverts −50,
            // collapsing 50 → 0 → the carrier dies INSIDE RemoveByModifierId, which re-enters
            // OnEntityDestroyed → DropAll → DropOne on the slot the outer DropOne is still mid-way through. Pre-fix both
            // levels ran the ground-flip + event, so ONE dropped item produced TWO ItemDropped cues.
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("ring"), 0, FixedVec3.Zero);
            PickUp(h, e, itemRef);
            Weaken(h, e, 100);
            Assert.True(h.World.IsAlive(e));
            Assert.Equal(Fixed.FromInt(50), h.World.EffectiveMaxHealth[e]); // parked one ring above the collapse
            h.Events.Clear();                                              // ignore the setup's pickup cue

            h.Sys.DropItemCommand(e, 0);

            Assert.False(h.World.IsAlive(e));                        // the removal really collapsed the ceiling (non-vacuous)
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(1, h.CountOf(CombatEventType.ItemDropped)); // RED pre-fix: 2 — the re-entrant sweep re-dropped it
        }

        [Fact]
        public void DropThatKillsTheCarrier_StillDropsTheOtherCarriedItems()
        {
            // Tooth for the reorder: clearing the in-flight slot early must skip ONLY that slot. Every OTHER item the
            // dying hero carries still has to reach the ground through the re-entrant death sweep.
            var h = Build();
            var (e, slot) = MintHero(h);
            int ringRef    = h.Items.Create(h.DefIndex("ring"), 0, FixedVec3.Zero);
            int trinketRef = h.Items.Create(h.DefIndex("trinket"), 0, FixedVec3.Zero);
            PickUp(h, e, ringRef);
            PickUp(h, e, trinketRef);
            Assert.Equal(trinketRef, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 1]);
            Weaken(h, e, 100);
            h.Events.Clear();

            h.Sys.DropItemCommand(e, 0); // drop the ring → lethal removal → death sweep must drop the trinket

            Assert.False(h.World.IsAlive(e));
            Assert.False(h.Items.Held[FirstSlot(h, ringRef)]);
            Assert.False(h.Items.Held[FirstSlot(h, trinketRef)]); // the death sweep still did its job
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 1]);
            Assert.Equal(2, h.CountOf(CombatEventType.ItemDropped)); // exactly one cue per dropped item
        }

        [Fact]
        public void OrdinaryDrop_OfALiveCarrier_AnnouncesTheDropOnce()
        {
            // The non-lethal baseline the reorder must leave byte-identical (this is the only path shipped content takes).
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("ring"), 0, FixedVec3.Zero);
            PickUp(h, e, itemRef);
            h.Events.Clear();

            h.Sys.DropItemCommand(e, 0);

            Assert.True(h.World.IsAlive(e));
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // modifier reverted
            Assert.False(h.Items.Held[FirstSlot(h, itemRef)]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(1, h.CountOf(CombatEventType.ItemDropped));
        }

        // ──────────────────── Consume: the last-charge removal re-enters DropAll on the spent slot ────────────────────

        [Fact]
        public void ConsumingTheLastCharge_ThatKillsTheCarrier_FreesTheItem_WithoutAPhantomDrop()
        {
            // A HYBRID consumable (one charge, +50 carried MaxHealth). Weakened by 100 the carrier sits at ceiling 50;
            // consuming the last charge reverts the +50, collapses 50 → 0 and kills — INSIDE RemoveByModifierId, whose
            // death sweep pre-fix flipped the spent instance onto the ground and announced ItemDropped for an item this
            // very call then destroyed. A consumed item is never a drop.
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("elixir"), 1, FixedVec3.Zero);
            PickUp(h, e, itemRef);
            Weaken(h, e, 100);
            Assert.True(h.World.IsAlive(e));
            Assert.Equal(Fixed.FromInt(50), h.World.EffectiveMaxHealth[e]);
            h.Events.Clear();

            h.Sys.UseItemCommand(e, 0);

            Assert.False(h.World.IsAlive(e));                        // the last-charge revert really collapsed the ceiling
            Assert.False(h.Items.TryResolveRef(itemRef, out _));     // the spent instance is freed, not left lying around
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(1, h.CountOf(CombatEventType.ItemUsed));
            Assert.Equal(0, h.CountOf(CombatEventType.ItemDropped)); // RED pre-fix: 1 phantom drop of a destroyed item
        }

        [Fact]
        public void ConsumingTheLastCharge_OfALiveCarrier_StillFreesTheItemAndClearsTheSlot()
        {
            // Tooth: the non-lethal consume — the path every shipped consumable takes — is untouched by the reorder.
            var h = Build();
            var (e, slot) = MintHero(h);
            int itemRef = h.Items.Create(h.DefIndex("elixir"), 1, FixedVec3.Zero);
            PickUp(h, e, itemRef);
            h.Events.Clear();

            h.Sys.UseItemCommand(e, 0);

            Assert.True(h.World.IsAlive(e));
            Assert.False(h.Items.TryResolveRef(itemRef, out _));
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[e]); // carried +50 removed with the last charge
            Assert.Equal(1, h.CountOf(CombatEventType.ItemUsed));
            Assert.Equal(0, h.CountOf(CombatEventType.ItemDropped));
        }

        // ─────────────────────────── Buy: the audited (deliberately unguarded) outcome ───────────────────────────

        [Fact]
        public void PurchaseThatKillsTheBuyer_ReportsSuccess_AndLeavesTheGoodsOnTheGround()
        {
            // DW-489's third external site, audited rather than changed: GrantPurchasedItem's stat apply can kill the
            // buyer, but the death hook leaves CONSISTENT state (instance on the ground, ring cleared), so the grant must
            // keep reporting success. Reporting −1 would make BuildingSystem.BuyItemCommand refund the cost while the
            // purchased item lay on the map — a resource-duplication exploit. This pins that ruling.
            var h = Build();
            var (e, slot) = MintHero(h);

            int itemRef = h.Sys.GrantPurchasedItem(e, h.DefIndex("cursed"));

            Assert.True(itemRef >= 0);                               // success ⇒ no refund
            Assert.False(h.World.IsAlive(e));                        // …even though the purchase killed its buyer
            Assert.True(h.Items.TryResolveRef(itemRef, out int isl));
            Assert.False(h.Items.Held[isl]);                         // lootable on the ground, not held by a corpse
            Assert.Equal(ItemStore.NO_CARRIER, h.Items.CarrierHeroSlot[isl]);
            Assert.Equal(HeroStore.INVENTORY_EMPTY, h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]);
        }

        private static int FirstSlot(Harness h, int itemRef)
        {
            Assert.True(h.Items.TryResolveRef(itemRef, out int s));
            return s;
        }
    }
}
