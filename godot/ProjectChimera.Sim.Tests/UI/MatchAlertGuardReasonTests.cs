#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server; // ServerLobbyPolicy (server re-stamp)
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 11.4 review (P9) — guard-sourced reason+faction coverage across EVERY reject system beyond Train, the
    /// victim-faction correctness the local-only under-attack filter depends on, the MapPing wire round-trip + server
    /// re-stamp, and the shared afford-reason resolver. All Godot-free.
    /// </summary>
    public class MatchAlertGuardReasonTests
    {
        /// <summary>The last OrderDenied event in the queue (asserts exactly one exists at minimum).</summary>
        private static CombatEvent LastDenial(CombatEventQueue q)
        {
            CombatEvent found = default;
            bool any = false;
            for (int i = 0; i < q.Count; i++)
                if (q.Get(i).Type == CombatEventType.OrderDenied) { found = q.Get(i); any = true; }
            Assert.True(any, "no OrderDenied event was pushed");
            return found;
        }

        // ── (b) victim-faction correctness (the load-bearing filter input) ──────────────────────

        [Fact]
        public void UnitKilled_StampsTheVICTIMFaction_NotTheKiller()
        {
            var world = new EntityWorld();
            int attacker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int victim   = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                        Faction.Player2, Fixed.FromInt(1), Fixed.FromInt(3)); // 1 HP → lethal
            var q = new CombatEventQueue();

            var ctx = new DamageContext(world, victim, world.ArmorTypeOf[victim], Faction.Player1,
                                        DamageTable.Default, q, null);
            Assert.True(DamageResolver.Apply(in ctx, Fixed.FromInt(100), DamageType.Normal)); // died

            bool found = false;
            for (int i = 0; i < q.Count; i++)
            {
                CombatEvent e = q.Get(i);
                if (e.Type != CombatEventType.UnitKilled) continue;
                found = true;
                Assert.Equal(Faction.Player2, e.Faction); // the VICTIM (P2), never the killer (P1) — an attacker/victim swap fails here
            }
            Assert.True(found, "no UnitKilled event was pushed");
        }

        // ── (a) Ability-cast denials (newly non-silent) ─────────────────────────────────────────

        private sealed class AbilityHarness
        {
            public EntityWorld World = new EntityWorld();
            public ResourceStore Resources = new ResourceStore(Fixed.FromInt(10000));
            public CombatEventQueue Events = new CombatEventQueue();
            public ModifierStore Modifiers = null!;
            public AbilityCastSystem Sys = null!;
            public int Caster;
        }

        private static AbilityHarness BuildAbility(AbilityDefinition ability, int casterHp = 100)
        {
            var h = new AbilityHarness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);
            var registry = new AbilityRegistry(new[] { ability });
            h.Sys = new AbilityCastSystem(registry, h.Resources, h.Modifiers, DamageTable.Default, h.Events);

            h.Caster = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(casterHp), Fixed.FromInt(3));
            int abBase = h.Caster * EntityWorld.MAX_ABILITIES_PER_UNIT;
            h.World.AbilityCount[h.Caster]   = 1;
            h.World.AbilityId[abBase + 0]    = 0; // registry index 0
            h.World.PendingCastSlot[h.Caster] = 0; // arm a cast on slot 0
            return h;
        }

        [Fact]
        public void AbilityCast_OnCooldown_PushesOnCooldownForCaster()
        {
            var ab = new AbilityDefinition { Id = "zap", Targeting = "self" };
            var h = BuildAbility(ab);
            int abBase = h.Caster * EntityWorld.MAX_ABILITIES_PER_UNIT;
            h.World.AbilityCooldownTicks[abBase + 0] = 5; // still > 0 after the tick's pre-decrement

            h.Sys.Tick(h.World, Fixed.One);

            CombatEvent d = LastDenial(h.Events);
            Assert.Equal(DenialReason.OnCooldown, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        [Fact]
        public void AbilityCast_NoEnergy_PushesNoEnergy()
        {
            var ab = new AbilityDefinition { Id = "zap", Targeting = "self", CostEnergy = Fixed.FromInt(10) };
            var h = BuildAbility(ab); // Energy defaults to 0 < 10
            h.Sys.Tick(h.World, Fixed.One);

            CombatEvent d = LastDenial(h.Events);
            Assert.Equal(DenialReason.NoEnergy, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        [Fact]
        public void AbilityCast_SelfLethal_PushesInvalidTarget()
        {
            var ab = new AbilityDefinition { Id = "sac", Targeting = "self", CostHealth = 200, AllowSelfLethal = false };
            var h = BuildAbility(ab, casterHp: 100); // 100 HP <= 200 cost → refused
            h.Sys.Tick(h.World, Fixed.One);

            CombatEvent d = LastDenial(h.Events);
            Assert.Equal(DenialReason.InvalidTarget, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        // ── (a) order-ring-full → QueueFull ─────────────────────────────────────────────────────

        [Fact]
        public void ShiftQueue_RingFull_PushesQueueFull()
        {
            var world = new EntityWorld();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var q = new CombatEventQueue();

            // Fill the ring to capacity with queued Move orders through the shared applier.
            var queuedMove = (UnitCommand)((byte)UnitCommand.Move | UnitOrderFlags.Queued);
            for (int i = 0; i < EntityWorld.MAX_ORDER_QUEUE; i++)
            {
                var o = new UnitOrder(id, queuedMove, Fixed.FromInt(i + 1), Fixed.Zero);
                OrderApplier.Apply(world, in o, Faction.Player1, events: q);
            }
            Assert.Equal(EntityWorld.MAX_ORDER_QUEUE, world.OrderQueueCount[id]);

            // One more → deterministic ring-full reject + a QueueFull denial cue.
            var overflow = new UnitOrder(id, queuedMove, Fixed.FromInt(99), Fixed.Zero);
            OrderApplier.Apply(world, in overflow, Faction.Player1, events: q);

            CombatEvent d = LastDenial(q);
            Assert.Equal(DenialReason.QueueFull, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        // ── (a) Research afford-reject → the specific short resource ─────────────────────────────

        private static ResearchSystem BuildResearch(out BuildingStore buildings, out ResourceStore resources,
                                                    out CombatEventQueue events, IReadOnlyDictionary<string, int> cost,
                                                    int ore, int crystal)
        {
            var world = new EntityWorld();
            buildings = new BuildingStore();
            resources = new ResourceStore(Fixed.FromInt(100000));
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(ore);
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(crystal);
            events = new CombatEventQueue();
            var research = new ResearchStore();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);

            var faction = new FactionDefinition
            {
                Id = "p1",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "r" } },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        Id = "r", Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int>(cost), TimeTicks = 3 },
                        },
                    },
                },
            };
            var sys = new ResearchSystem(buildings, resources, research, modifiers, events, faction, null);
            int lab = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            buildings.ConstructionTimer[lab] = Fixed.Zero; // operational
            _labId = lab;
            return sys;
        }
        private static int _labId;

        [Fact]
        public void Research_UnaffordableOre_PushesNeedOre()
        {
            var sys = BuildResearch(out _, out _, out CombatEventQueue events,
                                    new Dictionary<string, int> { { "ore", 500 } }, ore: 0, crystal: 0);
            Assert.False(sys.StartResearchCommand(_labId, Faction.Player1, 0));

            CombatEvent d = LastDenial(events);
            Assert.Equal(DenialReason.NeedOre, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        [Fact]
        public void Research_UnaffordableCrystal_PushesNeedCrystal()
        {
            var sys = BuildResearch(out _, out _, out CombatEventQueue events,
                                    new Dictionary<string, int> { { "crystal", 500 } }, ore: 100000, crystal: 0);
            Assert.False(sys.StartResearchCommand(_labId, Faction.Player1, 0));

            CombatEvent d = LastDenial(events);
            Assert.Equal(DenialReason.NeedCrystal, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        // ── (a) Shop denials → OutOfRange / InventoryFull ───────────────────────────────────────

        private static readonly UnitDefinition ShopHeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true, Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1,
        };

        private sealed class ShopHarness
        {
            public EntityWorld World = new EntityWorld();
            public HeroStore Heroes = new HeroStore();
            public ItemStore Items = new ItemStore();
            public BuildingStore Buildings = new BuildingStore();
            public ResourceStore Resources = new ResourceStore(Fixed.FromInt(10000));
            public CombatEventQueue Events = new CombatEventQueue();
            public ItemSystem Sys = null!;
            public BuildingSystem BuildSys = null!;
            public int ShopId;
        }

        private static ShopHarness BuildShop(int shopX, int radius)
        {
            var h = new ShopHarness();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(modifiers);
            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50), CostOre = Fixed.FromInt(10) },
            });
            h.Sys = new ItemSystem(h.World, h.Heroes, h.Items, modifiers, registry, h.Events);
            h.BuildSys = new BuildingSystem(h.Buildings, h.Resources, null, null, null, h.Heroes, null);
            h.ShopId = h.Buildings.Create(new FixedVec3(Fixed.FromInt(shopX), Fixed.Zero, Fixed.Zero),
                                          Faction.Player1, BuildingType.CommandCenter, revivesHeroes: false,
                                          sellsItems: true, shopStock: new[] { "ring" }, shopRadius: Fixed.FromInt(radius));
            h.Buildings.ConstructionTimer[h.ShopId] = Fixed.Zero;
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            return h;
        }

        private static int MintShopHero(ShopHarness h, int x)
        {
            int e = h.World.Create(new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero),
                                   Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.ApplyUnitDefinition(e, ShopHeroDef);
            int slot = h.Heroes.Mint(new HeroId(1), e, level: 1, xp: Fixed.Zero, sourceDef: ShopHeroDef, ownerFaction: Faction.Player1);
            h.World.HeroIndex[e] = h.Heroes.PackRef(slot);
            return e;
        }

        [Fact]
        public void Shop_BuyerOutOfRange_PushesOutOfRange()
        {
            var h = BuildShop(shopX: 0, radius: 6);
            int hero = MintShopHero(h, x: 100); // far outside shop_radius

            Assert.False(h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, 0, hero, h.Sys, h.Events));

            CombatEvent d = LastDenial(h.Events);
            Assert.Equal(DenialReason.OutOfRange, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        [Fact]
        public void Shop_InventoryFull_PushesInventoryFull()
        {
            var h = BuildShop(shopX: 0, radius: 20);
            int hero = MintShopHero(h, x: 2); // in range
            const int slot = 0; // the sole minted hero occupies hero-store slot 0
            // Fill every inventory slot so HeroHasFreeSlot == false.
            for (int k = 0; k < HeroStore.INVENTORY_SLOTS; k++)
                h.Heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + k] = 999; // any non-empty marker

            Assert.False(h.BuildSys.BuyItemCommand(h.ShopId, Faction.Player1, 0, hero, h.Sys, h.Events));

            CombatEvent d = LastDenial(h.Events);
            Assert.Equal(DenialReason.InventoryFull, d.Reason);
            Assert.Equal(Faction.Player1, d.Faction);
        }

        // ── (c) MapPing wire round-trip + server re-stamp ───────────────────────────────────────

        [Fact]
        public void MapPing_WireRoundTrip_PreservesFactionAndCoords()
        {
            byte[] pkt = TickCommandPacket.MakeMapPing(Faction.Player2, -37, 84);
            Assert.True(TickCommandPacket.TryReadMapPing(pkt, pkt.Length, out Faction f, out int x, out int z));
            Assert.Equal(Faction.Player2, f);
            Assert.Equal(-37, x);
            Assert.Equal(84, z);
        }

        [Fact]
        public void MapPing_TruncatedOrWrongType_ReturnsFalse()
        {
            byte[] pkt = TickCommandPacket.MakeMapPing(Faction.Player1, 5, 6);
            Assert.False(TickCommandPacket.TryReadMapPing(pkt, pkt.Length - 1, out _, out _, out _)); // truncated
            byte[] chat = TickCommandPacket.MakeChat(Faction.Player1, "hi");
            Assert.False(TickCommandPacket.TryReadMapPing(chat, chat.Length, out _, out _, out _));    // wrong type
        }

        [Fact]
        public void MapPing_ServerReStamp_OverridesTheWireFaction()
        {
            // A client spoofs Player1 on the wire; the server re-stamps from the sender's authoritative slot.
            byte[] spoofed = TickCommandPacket.MakeMapPing(Faction.Player1, 3, 4);
            Assert.True(TickCommandPacket.TryReadMapPing(spoofed, spoofed.Length, out _, out int x, out int z));

            var slotFaction = new[] { Faction.Player1, Faction.Player2 };
            Faction stamped = ServerLobbyPolicy.StampChatFaction(slot: 1, slotFaction, maxPlayers: 2);
            byte[] relayed = TickCommandPacket.MakeMapPing(stamped, x, z);

            Assert.True(TickCommandPacket.TryReadMapPing(relayed, relayed.Length, out Faction f, out _, out _));
            Assert.Equal(Faction.Player2, f); // authoritative slot 1 → Player2, not the spoofed Player1
        }

        // ── (d) DenialReasons.ForUnaffordableCost ───────────────────────────────────────────────

        [Fact]
        public void AffordReason_OreOnlyShort_IsNeedOre()
        {
            var r = new ResourceStore(Fixed.Zero); // zero starting balances
            Assert.Equal(DenialReason.NeedOre,
                DenialReasons.ForUnaffordableCost(r, Faction.Player1, new Dictionary<string, int> { { "ore", 100 } }));
        }

        [Fact]
        public void AffordReason_CrystalOnlyShort_IsNeedCrystal()
        {
            var r = new ResourceStore(Fixed.Zero);
            r.Ore[(int)Faction.Player1] = Fixed.FromInt(10000); // ore is fine; crystal is short
            Assert.Equal(DenialReason.NeedCrystal,
                DenialReasons.ForUnaffordableCost(r, Faction.Player1, new Dictionary<string, int> { { "crystal", 100 } }));
        }

        [Fact]
        public void AffordReason_CustomResourceKey_IsInsufficientResources()
        {
            var r = new ResourceStore(Fixed.Zero);
            Assert.Equal(DenialReason.InsufficientResources,
                DenialReasons.ForUnaffordableCost(r, Faction.Player1, new Dictionary<string, int> { { "obsidian", 5 } }));
        }
    }
}
