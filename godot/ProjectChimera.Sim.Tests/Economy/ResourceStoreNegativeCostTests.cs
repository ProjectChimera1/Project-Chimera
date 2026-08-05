#nullable enable
using System.Collections.Generic;
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
    /// Epic-15 bundle <c>resource-store-negative-cost</c> — DW-642.
    ///
    /// <para><b>The defect.</b> <see cref="ResourceStore"/>'s affordability predicates are <c>have &gt;= cost</c>
    /// comparisons, so <c>CanAffordOre(faction, -5)</c> was TRIVIALLY true; <c>SpendOre</c> then subtracted a
    /// negative and CREDITED 5 ore. DW-283 hardened only the ability-cast caller it named, but the money printer
    /// lived in the shared primitive — <c>BuildingSystem</c> (train / worker-build / shop buy), <c>ReviveHeroCommand</c>,
    /// <c>ResearchSystem</c> and <c>AiOpponentSystem</c> all ride it, and Ore/Crystal are SimChecksum-folded, so an
    /// authored negative cost was a determinism-visible resource mint rather than a mere validation gap.</para>
    ///
    /// <para><b>The fix, and why it lands on the predicate too.</b> <c>SpendOre</c>/<c>SpendCrystal</c> now refuse a
    /// negative cost (the <c>ModifierStore.TryDebitEnergy</c> "never refund a negative cost" rule), AND so do
    /// <c>CanAffordOre</c>/<c>CanAffordCrystal</c>. Guarding only the debit would have been strictly worse than a
    /// half-fix: every caller above is a check-then-debit pair that DISCARDS the spend's return, so the printer would
    /// have become a silent FREE transaction — and inside the sparse-map <c>Spend</c> it would have broken
    /// check-all-then-spend-all atomicity outright (a <c>{ore:-5, crystal:10}</c> map would debit the crystal while
    /// the ore leg declined). Failing the predicate makes every site deny with its own reason and mutate nothing.</para>
    ///
    /// <para><b>Determinism.</b> For every non-negative cost the behavior is byte-identical, so no folded value moves
    /// for validated content and no golden is re-recorded. Only the previously-exploitable negative branch changes.</para>
    ///
    /// Every test here is RED without the fix except the two explicitly-labelled over-reach guards. Godot-free,
    /// Fixed-only, hermetic.
    /// </summary>
    public class ResourceStoreNegativeCostTests
    {
        private const int P1 = (int)Faction.Player1;

        // ── The primitive: CanAffordOre / SpendOre ───────────────────────────────────────────────────────────────

        /// <summary>Pre-fix: <c>100 &gt;= -50</c> passes, <c>100 - (-50)</c> lands 150 ore and the call reports success.</summary>
        [Fact]
        public void SpendOre_NegativeCost_Refuses_AndNeverCreditsOre()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1] = Fixed.FromInt(100);

            Assert.False(resources.SpendOre(Faction.Player1, Fixed.FromInt(-50)));
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Ore[P1].Raw); // NOT credited
        }

        /// <summary>The crystal mirror — proving the guard covers both resources, not just the one the first test picked.</summary>
        [Fact]
        public void SpendCrystal_NegativeCost_Refuses_AndNeverCreditsCrystal()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Crystal[P1] = Fixed.FromInt(10);

            Assert.False(resources.SpendCrystal(Faction.Player1, Fixed.FromInt(-25)));
            Assert.Equal(Fixed.FromInt(10).Raw, resources.Crystal[P1].Raw);
        }

        /// <summary>The predicate half: a negative cost must fail CLOSED so a check-then-debit caller denies at its own
        /// gate instead of passing the check and silently no-op'ing the debit (a free transaction).</summary>
        [Fact]
        public void CanAffordOre_NegativeCost_FailsClosed()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1] = Fixed.FromInt(100);

            Assert.False(resources.CanAffordOre(Faction.Player1, Fixed.FromInt(-5)));
        }

        [Fact]
        public void CanAffordCrystal_NegativeCost_FailsClosed()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Crystal[P1] = Fixed.FromInt(100);

            Assert.False(resources.CanAffordCrystal(Faction.Player1, Fixed.FromInt(-5)));
        }

        /// <summary>A negative balance is not a licence to mint either: a bankrupt faction must not be able to "spend"
        /// its way back up. Pins that the guard keys off the COST's sign, not the balance comparison.</summary>
        [Fact]
        public void SpendOre_NegativeCost_AtZeroBalance_StillRefuses()
        {
            var resources = new ResourceStore(Fixed.Zero);

            Assert.False(resources.SpendOre(Faction.Player1, Fixed.FromInt(-1000)));
            Assert.Equal(Fixed.Zero.Raw, resources.Ore[P1].Raw);
        }

        // ── Over-reach guards: the boundary must stay exactly at "strictly negative" ─────────────────────────────

        /// <summary>GREEN both before and after — a ZERO cost is still affordable and still spendable at a ZERO
        /// balance (free units/abilities and every absent cost entry rely on this; see
        /// <c>ProductionSelectionTests</c>'s "CanAffordCrystal(0) is always true" assumption).</summary>
        [Fact]
        public void ZeroCost_StaysAffordableAndSpendable_AtZeroBalance()
        {
            var resources = new ResourceStore(Fixed.Zero);

            Assert.True(resources.CanAffordOre(Faction.Player1, Fixed.Zero));
            Assert.True(resources.CanAffordCrystal(Faction.Player1, Fixed.Zero));
            Assert.True(resources.SpendOre(Faction.Player1, Fixed.Zero));
            Assert.True(resources.SpendCrystal(Faction.Player1, Fixed.Zero));
            Assert.Equal(Fixed.Zero.Raw, resources.Ore[P1].Raw);
            Assert.Equal(Fixed.Zero.Raw, resources.Crystal[P1].Raw);
        }

        /// <summary>GREEN both before and after — the smallest representable POSITIVE cost (1 raw unit of 16.16) is
        /// still spent normally, so the guard cannot have swallowed the sub-unit end of the range.</summary>
        [Fact]
        public void SmallestPositiveCost_StillSpendsNormally()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1] = Fixed.FromInt(1);
            var oneRaw = Fixed.FromRaw(1);

            Assert.True(resources.CanAffordOre(Faction.Player1, oneRaw));
            Assert.True(resources.SpendOre(Faction.Player1, oneRaw));
            Assert.Equal(Fixed.FromInt(1).Raw - 1, resources.Ore[P1].Raw);
        }

        // ── The sparse cost-map API: the atomicity class a Spend-only guard would have opened ────────────────────

        [Fact]
        public void CanAfford_NegativeAmountOnAKnownKey_FailsClosed()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1] = Fixed.FromInt(100);

            Assert.False(resources.CanAfford(Faction.Player1, new Dictionary<string, int> { { "ore", -5 } }));
        }

        /// <summary>The mixed map is the sharp case. Pre-fix BOTH legs land: crystal is debited and ore is CREDITED.
        /// A debit-only guard would have flipped that into a partial spend (crystal gone, ore untouched, call reporting
        /// success). Post-fix the whole map is refused and NOTHING moves.</summary>
        [Fact]
        public void Spend_MixedMapWithANegativeLeg_FailsClosed_SpendsNothingAndCreditsNothing()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1]     = Fixed.FromInt(100);
            resources.Crystal[P1] = Fixed.FromInt(100);

            var cost = new Dictionary<string, int> { { "ore", -5 }, { "crystal", 10 } };

            Assert.False(resources.Spend(Faction.Player1, cost));
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Ore[P1].Raw);     // not credited
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Crystal[P1].Raw); // and not partially debited either
        }

        // ── Caller-level proof: the printer really is reachable from the callers DW-642 names ────────────────────

        private static FactionDefinition NegativeCostFaction()
        {
            var f = new FactionDefinition { Id = "neg", DisplayName = "Neg" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            // Hand-built (validator-bypassing) NEGATIVE authored cost — the shape ResourceCostValidator rejects at
            // import, reproduced here as the runtime's fail-closed backstop for a def that never went through it.
            f.Units.Add(new UnitDefinition
            {
                Id = "printer", Category = "Melee", Hp = 100f,
                Cost = new Dictionary<string, int> { { "ore", -100 } },
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = -250, CostCrystal = 0,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            return f;
        }

        /// <summary>
        /// <c>BuildingSystem.TrainUnit</c> — the sparse-cost-map training path. Pre-fix the order was ACCEPTED, 100 ore
        /// was minted per enqueue and the unit trained anyway: an unbounded resource printer clocked at the queue rate.
        /// </summary>
        [Fact]
        public void TrainUnit_NegativeAuthoredCost_IsDenied_AndNeverCreditsOre()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1]       = Fixed.FromInt(500);
            resources.SupplyCap[P1] = 500; // supply is gated BEFORE cost — keep it out of the way
            var sys = new BuildingSystem(buildings, resources, NegativeCostFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // "printer"
            Assert.Equal(Fixed.FromInt(500).Raw, resources.Ore[P1].Raw);   // NOT minted
            Assert.Equal(0, buildings.ProductionQueue[b]);                 // and nothing enqueued
        }

        /// <summary>
        /// <c>BuildingSystem.QueueWorkerBuild</c> — the construction path, whose cost comes from the BUILDING def's
        /// resolved map. Pre-fix ordering a build PAID the player 250 ore per placement.
        /// </summary>
        [Fact]
        public void QueueWorkerBuild_NegativeAuthoredBuildingCost_IsDenied_AndNeverCreditsOre()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[P1] = Fixed.FromInt(200);
            var sys = new BuildingSystem(buildings, resources, NegativeCostFaction());

            int worker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker] = GatherState.Idle;

            int bId = sys.QueueWorkerBuild(worker, BuildingType.Barracks,
                new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10)), Faction.Player1, resources, world);

            Assert.Equal(-1, bId);
            Assert.Equal(Fixed.FromInt(200).Raw, resources.Ore[P1].Raw);
        }

        /// <summary>
        /// <c>BuildingSystem.BuyItemCommand</c> — the shop path, and the one caller that still uses the RAW
        /// <c>CanAffordOre</c>/<c>SpendOre</c> pair rather than the sparse map, with the spend's return discarded.
        /// Pre-fix a negative-priced item paid the buyer 100 ore AND handed over the item. It is also the caller the
        /// predicate guard matters most for: with a debit-only fix the buy would still have succeeded, just for free.
        /// (<c>ItemDefinitionValidator</c> is the authoring gate; this is the runtime backstop behind it.)
        /// </summary>
        [Fact]
        public void BuyItemCommand_NegativeAuthoredItemCost_IsDenied_AndNeverCreditsOre()
        {
            var world     = new EntityWorld();
            var heroes    = new HeroStore();
            var items     = new ItemStore();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var events    = new CombatEventQueue();

            var modSys    = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);

            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "cursed_ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50),
                                     CostOre = Fixed.FromInt(-100) }, // hand-built: the validator rejects this shape
            });
            var itemSys  = new ItemSystem(world, heroes, items, modifiers, registry, events);
            var buildSys = new BuildingSystem(buildings, resources, null, null, null, heroes, null);

            int shop = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.CommandCenter,
                                        revivesHeroes: false, sellsItems: true,
                                        shopStock: new[] { "cursed_ring" }, shopRadius: Fixed.FromInt(10));
            buildings.ConstructionTimer[shop] = Fixed.Zero; // operational
            resources.AddOre(Faction.Player1, Fixed.FromInt(500));

            var heroDef = new UnitDefinition
            {
                Id = "hero", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
            };
            int e = world.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.ApplyUnitDefinition(e, heroDef);
            int slot = heroes.Mint(new HeroId(1), e, level: 1, xp: Fixed.Zero,
                                   sourceDef: heroDef, ownerFaction: Faction.Player1);
            world.HeroIndex[e] = heroes.PackRef(slot);

            bool ok = buildSys.BuyItemCommand(shop, Faction.Player1, stockIndex: 0, heroEntityId: e,
                                              items: itemSys, events: events);

            Assert.False(ok);
            Assert.Equal(Fixed.FromInt(500).Raw, resources.Ore[P1].Raw); // NOT paid to the buyer
            Assert.Equal(HeroStore.INVENTORY_EMPTY,
                         heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0]); // and no item handed over
        }
    }
}
