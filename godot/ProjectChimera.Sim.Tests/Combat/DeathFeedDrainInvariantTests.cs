#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-766 — the <see cref="DeathFeed"/> "drained at the checksum boundary" invariant, made TRUE instead of merely
    /// claimed.
    ///
    /// <para><b>The defect.</b> <see cref="DeathFeed"/>'s type doc and two <c>SimChecksum</c> comments all cite the feed
    /// being provably drained within the tick as the reason it is excluded from the fold. <see cref="HeroXpSystem"/>
    /// drains and clears it at index [9] — but DW-490 threaded the SAME feed into <see cref="ModifierStore"/>, whose
    /// DW-325 ceiling-collapse kill is reachable from <see cref="ItemSystem"/> at index [10], and
    /// <see cref="ScenarioDirector"/> at [15] holds the feed in the <c>EffectContext</c> its <c>run_effect</c> graphs
    /// execute against. A hero picking up a net-negative <c>max_health_delta</c> item therefore left <c>Count == 1</c>
    /// when the checksum was taken, with the residue's XP — feeding the FOLDED <c>HeroStore.Xp</c>/<c>Level</c> —
    /// landing a tick late. That is mutable state outside the desync detector.</para>
    ///
    /// <para><b>The fix under test.</b> <see cref="DeathFeedDrainSystem"/> runs LAST (past every producer) and credits
    /// the residue in the SAME tick through <see cref="HeroXpSystem.DrainResidue"/>, then clears; and
    /// <see cref="SimulationLoop"/> asserts the feed is empty at the tick boundary, so a future producer registered past
    /// the drain fails loudly instead of silently re-opening the hole.</para>
    ///
    /// <para>Godot-free; <see cref="Fixed"/> only, no float/RNG. The scenarios are content-gated in shipped data (no
    /// shipped item authors a negative MaxHealth delta), which is why they need an oracle rather than a golden.</para>
    /// </summary>
    public class DeathFeedDrainInvariantTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        // ─────────────────────────── HeroXpSystem.DrainResidue — the residue pass itself ───────────────────────────

        /// <summary>Build a bare (host-free) XP fixture: one Player1 hero at level 1 with a wide share radius.</summary>
        private static (EntityWorld world, HeroStore heroes, DeathFeed deaths, HeroXpSystem sys, int slot)
            BareXpFixture(int shareRadius = 100)
        {
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var heroes = new HeroStore();

            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int slot = heroes.Mint(new HeroId(766), ent, level: 1, xp: Fixed.Zero,
                maxLevel: 5, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One,
                xpShareRadius: Fixed.FromInt(shareRadius),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1));
            world.HeroIndex[ent] = heroes.PackRef(slot);

            return (world, heroes, deaths, new HeroXpSystem(heroes, modifiers, deaths), slot);
        }

        [Fact]
        public void DrainResidue_CreditsADeathPushedAfterTheXpTick_AndEmptiesTheFeed()
        {
            // Exactly the post-[9] shape: the XP system has already run and cleared, THEN a later producer pushes.
            var (world, heroes, deaths, sys, slot) = BareXpFixture();
            sys.Tick(world, Dt);
            Assert.Equal(Fixed.Zero.Raw, heroes.Xp[slot].Raw); // nothing to credit yet (non-vacuous)

            deaths.Push(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(30));
            Assert.Equal(1, deaths.Count);

            sys.DrainResidue(world);

            Assert.Equal(Fixed.FromInt(30).Raw, heroes.Xp[slot].Raw); // credited in the SAME tick, not the next one
            Assert.Equal(0, deaths.Count);                            // and the feed is empty at the boundary
        }

        [Fact]
        public void DrainResidue_AppliesTheSameCreditRules_AsTheIndexNineDrain()
        {
            // The residue pass must be the SAME rule, not a looser second copy: a friendly death still credits nothing.
            var (world, heroes, deaths, sys, slot) = BareXpFixture();
            sys.Tick(world, Dt);

            deaths.Push(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(30)); // the hero's OWN faction
            sys.DrainResidue(world);

            Assert.Equal(Fixed.Zero.Raw, heroes.Xp[slot].Raw);
            Assert.Equal(0, deaths.Count); // consumed either way — the invariant does not depend on anyone being credited
        }

        [Fact]
        public void DrainResidue_OutOfRangeResidue_GrantsNothing_ButStillEmptiesTheFeed()
        {
            var (world, heroes, deaths, sys, slot) = BareXpFixture(shareRadius: 3);
            sys.Tick(world, Dt);

            deaths.Push(new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(30));
            sys.DrainResidue(world);

            Assert.Equal(Fixed.Zero.Raw, heroes.Xp[slot].Raw);
            Assert.Equal(0, deaths.Count);
        }

        [Fact]
        public void DrainResidue_WithNoResidue_IsAStrictNoOp()
        {
            // The reason every recorded golden is unmoved: a tick whose deaths were all recorded before index [9] hits
            // the early return and touches nothing.
            var (world, heroes, deaths, sys, slot) = BareXpFixture();
            deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(30));
            sys.Tick(world, Dt);                                      // credits + clears, exactly as before the fix
            int creditedRaw = heroes.Xp[slot].Raw;
            Assert.Equal(Fixed.FromInt(30).Raw, creditedRaw);
            Assert.Equal(0, deaths.Count);

            sys.DrainResidue(world);

            Assert.Equal(creditedRaw, heroes.Xp[slot].Raw); // no second credit of the same kill
            Assert.Equal(1, heroes.Level[slot]);
            Assert.Equal(0, deaths.Count);
        }

        // ─────────────────────── The real path: an ItemSystem [10] ceiling-collapse kill in a host ───────────────────

        private const int CursedMaxHealthDelta = -100; // collapses a 100-ceiling hero to exactly 0 → DW-325 kill

        private static readonly UnitDefinition HeroDef = new UnitDefinition
        {
            // AttackDamage 0 so neither hero fights: the ONLY death in the tick is the ceiling collapse.
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 0, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        /// <summary>A host wired with a single "cursed" item whose −100 MaxHealth kills its own claimant on pickup.</summary>
        private static SimulationHost BuildHost() => SimulationHost.Create(
            NullLogSink.Instance,
            new FactionRegistry(2),
            new FactionDefinition(),
            new FactionDefinition(),
            itemRegistry: new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "cursed", Charges = 0, MaxHealthDelta = Fixed.FromInt(CursedMaxHealthDelta) },
            }));

        /// <summary>Mint a hero entity + persisted row + link at <paramref name="pos"/>. Returns (entityId, heroSlot).</summary>
        private static (int entity, int slot) MintHero(SimulationHost host, Faction faction, ulong id, FixedVec3 pos,
                                                       int shareRadius)
        {
            int e = host.World.Create(pos, faction, Fixed.FromInt(100), Fixed.FromInt(3));
            host.World.ApplyUnitDefinition(e, HeroDef);
            int slot = host.Heroes.Mint(new HeroId(id), e, level: 1, xp: Fixed.Zero,
                maxLevel: 5, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One,
                xpShareRadius: Fixed.FromInt(shareRadius),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: HeroDef, ownerFaction: faction);
            host.World.HeroIndex[e] = host.Heroes.PackRef(slot);
            return (e, slot);
        }

        /// <summary>
        /// The full-order scenario: a Player1 hero picks up the cursed item during <c>ItemSystem</c> at index [10] —
        /// AFTER <c>HeroXpSystem</c> at [9] has drained and cleared — so the collapse death is pushed into an already-
        /// drained feed. Returns the host plus the victim entity and the observing Player2 hero's slot.
        /// </summary>
        private static (SimulationHost host, int victim, int watcherSlot) StageCollapsePickup()
        {
            SimulationHost host = BuildHost();

            var (victim, _) = MintHero(host, Faction.Player1, 7660001UL, FixedVec3.Zero, shareRadius: 50);
            host.World.XpBounty[victim] = Fixed.FromInt(30); // set AFTER the def mapper, which owns the derived bounty

            // The hostile observer that must be credited. Two units away → well inside its 50-unit share radius.
            var (_, watcherSlot) = MintHero(host, Faction.Player2, 7660002UL,
                                            new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), shareRadius: 50);

            int itemRef = host.Items.Create(host.ItemRegistry.IndexOf("cursed"), 0, FixedVec3.Zero);
            var order = new UnitOrder(victim, UnitCommand.PickupItem, Fixed.FromRaw(itemRef), Fixed.Zero);
            OrderApplier.Apply(host.World, order, Faction.Player1, items: host.ItemSys);

            return (host, victim, watcherSlot);
        }

        [Fact]
        public void ItemPickupCollapseKill_LeavesTheDeathFeedEmptyAtTheTickBoundary()
        {
            var (host, victim, _) = StageCollapsePickup();

            host.StepOnce();

            Assert.False(host.World.IsAlive(victim)); // the collapse really killed the claimant (non-vacuous)
            // RED pre-fix: Count == 1. The feed's whole exclusion from SimChecksum rests on this being 0.
            Assert.Equal(0, host.Deaths.Count);
        }

        [Fact]
        public void ItemPickupCollapseKill_CreditsTheHostileHero_InTheSameTick()
        {
            var (host, victim, watcherSlot) = StageCollapsePickup();

            host.StepOnce();

            Assert.False(host.World.IsAlive(victim));
            // RED pre-fix: 0 — the record sat in the feed and the XP landed on the NEXT tick, off unhashed residue.
            Assert.Equal(Fixed.FromInt(30).Raw, host.Heroes.Xp[watcherSlot].Raw);
            Assert.Equal(1, host.Heroes.Level[watcherSlot]); // 30 < the 50 threshold → the level advance is untouched
        }

        [Fact]
        public void ItemPickupCollapseKill_IsCreditedExactlyOnce_AcrossTheFollowingTick()
        {
            // Tooth against the obvious wrong fix (drain without clearing, or clear without consuming): the record must
            // be credited by the residue pass and then be GONE, so the next tick's index-[9] drain finds nothing.
            var (host, _, watcherSlot) = StageCollapsePickup();

            host.StepOnce();
            int afterFirstTick = host.Heroes.Xp[watcherSlot].Raw;
            host.StepOnce();

            Assert.Equal(Fixed.FromInt(30).Raw, afterFirstTick);
            Assert.Equal(afterFirstTick, host.Heroes.Xp[watcherSlot].Raw); // no double credit
            Assert.Equal(0, host.Deaths.Count);
        }

        [Fact]
        public void AnOrdinaryTickWithNoDeaths_LeavesTheFeedEmpty_AndDoesNotThrow()
        {
            // Tooth for the boundary assertion: the common path must stay silent (and the drain a no-op).
            SimulationHost host = BuildHost();
            MintHero(host, Faction.Player1, 7660003UL, FixedVec3.Zero, shareRadius: 50);

            host.StepOnce();
            host.StepOnce();

            Assert.Equal(0, host.Deaths.Count);
        }

        // ───────────────────── SimulationLoop's tick-boundary assertion — the tripwire itself ─────────────────────

        /// <summary>A stand-in for a future producer: pushes one death record every tick.</summary>
        private sealed class DeathPushingSystem : ISimSystem
        {
            private readonly DeathFeed _feed;
            public DeathPushingSystem(DeathFeed feed) => _feed = feed;
            public void Tick(EntityWorld world, Fixed dt) =>
                _feed.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(1));
        }

        /// <summary>A stand-in for the real drain: consumes whatever is in the feed.</summary>
        private sealed class DrainingSystem : ISimSystem
        {
            private readonly DeathFeed _feed;
            public DrainingSystem(DeathFeed feed) => _feed = feed;
            public void Tick(EntityWorld world, Fixed dt) => _feed.Clear();
        }

        [Fact]
        public void TickBoundaryInvariant_Throws_WhenAProducerIsRegisteredPastTheDrain()
        {
            // The DW-766 failure mode, reproduced structurally: drain, THEN a producer. Pre-tripwire this was silent
            // and the checksum was taken over a non-empty feed.
            var world = new EntityWorld();
            var feed = new DeathFeed();
            var loop = new SimulationLoop(world, new DrainingSystem(feed), new DeathPushingSystem(feed));
            loop.EnableTickBoundaryInvariants(feed);

            var ex = Assert.Throws<InvalidOperationException>(() => loop.StepOnce());
            Assert.Contains("DW-766", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TickBoundaryInvariant_IsSilent_WhenTheDrainRunsLast()
        {
            // Tooth: the tripwire must fire on ORDER, not on the mere existence of a death.
            var world = new EntityWorld();
            var feed = new DeathFeed();
            var loop = new SimulationLoop(world, new DeathPushingSystem(feed), new DrainingSystem(feed));
            loop.EnableTickBoundaryInvariants(feed);

            loop.StepOnce();
            loop.Update(1f / SimulationLoop.TICKS_PER_SECOND); // the accumulator path checks it too

            Assert.Equal(0, feed.Count);
        }

        [Fact]
        public void TickBoundaryInvariant_IsInert_WhenUnarmed()
        {
            // Legacy/test loops that own no feed must be unaffected (null ⇒ not checked).
            var world = new EntityWorld();
            var feed = new DeathFeed();
            var loop = new SimulationLoop(world, new DeathPushingSystem(feed));

            loop.StepOnce();

            Assert.Equal(1, feed.Count);
        }
    }
}
