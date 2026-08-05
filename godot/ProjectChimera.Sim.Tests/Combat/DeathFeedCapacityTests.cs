#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-616 — the <see cref="DeathFeed"/> capacity contract.
    ///
    /// <para>The feed used to be a flat 256-slot ring that silently dropped every death past the cap, copied from the
    /// pre-DW-469 <see cref="CombatEventQueue"/>. The copy was unsound: the event queue carries presentation-only cues
    /// (so DW-469 could settle for a two-lane priority reserve), whereas a dropped <see cref="DeathRecord"/> withholds
    /// hero XP — and <c>HeroStore.Xp</c>/<c>Level</c>/the growth stacks it drives are folded into
    /// <c>SimChecksum</c>. A &gt;256-death tick was therefore a determinism-visible loss of folded feedback. A priority
    /// lane cannot help here: every death carries the identical kind of folded consequence, so there is no low-value
    /// class to sacrifice — the fix has to be lossless.</para>
    ///
    /// <para>These tests pin BOTH halves of the recorded decision: the loss is gone (overflow is recorded and credited),
    /// AND the golden cost of removing it is zero (a tick at or under the old cap runs the identical append, so no
    /// checksum, golden or replay moves).</para>
    /// </summary>
    public class DeathFeedCapacityTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        /// <summary>Deaths pushed by the overflow cases — comfortably past <see cref="DeathFeed.INITIAL_CAPACITY"/> so
        /// the pre-fix ring would have dropped 44 of them.</summary>
        private const int Overflow = 300;

        // ── The loss: overflow must be recorded, not dropped ──────────────────────────────────────────────────

        [Fact]
        public void Push_PastInitialCapacity_RecordsEveryDeath_NoSilentDrop()
        {
            var feed = new DeathFeed();
            for (int i = 0; i < Overflow; i++)
                feed.Push(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(i + 1));

            // Pre-fix this was DeathFeed.INITIAL_CAPACITY (256) — the 44 deaths past the cap were dropped outright.
            Assert.Equal(Overflow, feed.Count);
            Assert.True(feed.Capacity >= Overflow,
                $"the buffer must have grown to hold every push (capacity {feed.Capacity} < {Overflow})");
        }

        [Fact]
        public void Grow_PreservesPushOrderExactly_AcrossTheCapacityBoundary()
        {
            // Order is the whole contract: HeroXpSystem drains [0, Count) in recorded order, so a growth copy that
            // reordered, shifted or duplicated records would silently re-attribute bounties.
            var feed = new DeathFeed();
            for (int i = 0; i < Overflow; i++)
                feed.Push(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                          i % 2 == 0 ? Faction.Neutral : Faction.Player2, Fixed.FromInt(i + 1));

            for (int i = 0; i < Overflow; i++)
            {
                DeathRecord r = feed.Get(i);
                Assert.Equal(Fixed.FromInt(i).Raw, r.Position.X.Raw);
                Assert.Equal(i % 2 == 0 ? Faction.Neutral : Faction.Player2, r.Faction);
                Assert.Equal(Fixed.FromInt(i + 1).Raw, r.Bounty.Raw);
            }
        }

        // ── The golden cost: zero. A sub-cap tick is the identical append it always was. ───────────────────────

        [Fact]
        public void Push_AtInitialCapacity_DoesNotGrow_SoSubCapTicksAreBitIdenticalToThePreFixRing()
        {
            // This is the DW-616 golden analysis, pinned. Everything the simulation can observe about the feed is
            // Count and Get over [0, Count); for the first INITIAL_CAPACITY pushes both are produced by the exact same
            // append the fixed-size ring did, with no reallocation in between. So every tick at or below the old cap —
            // which is every tick in every recorded golden and replay scenario, whose kill counts are orders of
            // magnitude under 256 (the whole entity cap is 4096) — yields a bit-identical HeroXpSystem credit pass and
            // therefore a bit-identical SimChecksum. Removing the cap moved no golden.
            var feed = new DeathFeed();
            Assert.Equal(DeathFeed.INITIAL_CAPACITY, feed.Capacity);

            for (int i = 0; i < DeathFeed.INITIAL_CAPACITY; i++)
                feed.Push(FixedVec3.Zero, Faction.Neutral, Fixed.One);

            Assert.Equal(DeathFeed.INITIAL_CAPACITY, feed.Count);
            Assert.Equal(DeathFeed.INITIAL_CAPACITY, feed.Capacity); // not one byte reallocated below the old cap
        }

        [Fact]
        public void Clear_ResetsCountAndRetainsGrownCapacity_SoABusyTickReallocatesOnlyOnce()
        {
            var feed = new DeathFeed();
            for (int i = 0; i < Overflow; i++) feed.Push(FixedVec3.Zero, Faction.Neutral, Fixed.One);
            int grown = feed.Capacity;

            feed.Clear(); // the end-of-tick drain posture — empty at the checksum boundary, so still unfolded

            Assert.Equal(0, feed.Count);
            Assert.Equal(grown, feed.Capacity); // retained: no re-alloc churn on a sustained big-battle tick

            feed.Push(FixedVec3.Zero, Faction.Player3, Fixed.FromInt(7)); // next tick starts at index 0 again
            Assert.Equal(1, feed.Count);
            Assert.Equal(Faction.Player3, feed.Get(0).Faction);
            Assert.Equal(Fixed.FromInt(7).Raw, feed.Get(0).Bounty.Raw);
        }

        // ── The folded consequence: the overflow deaths now actually pay XP ────────────────────────────────────

        [Fact]
        public void OverflowDeaths_CreditFoldedHeroXp_TheWholeReasonTheCapWasADeterminismDefect()
        {
            // Bounty 1 per death, hero in range of all of them, curve high enough that no level-up interferes.
            // Pre-fix the hero banked 256 (the ring's cap); the 44 overflow deaths paid nothing, and HeroStore.Xp is
            // folded — so the cap was a checksum-visible loss of feedback, not a cosmetic drop.
            (EntityWorld world, HeroStore heroes, DeathFeed feed, HeroXpSystem sys, int slot) = MakeHeroFixture();

            for (int i = 0; i < Overflow; i++)
                feed.Push(FixedVec3.Zero, Faction.Neutral, Fixed.One);
            sys.Tick(world, Dt);

            Assert.Equal(Fixed.FromInt(Overflow).Raw, heroes.Xp[slot].Raw);
            Assert.Equal(1, heroes.Level[slot]);   // 300 XP is far under the 30000 threshold — no level-up in play
            Assert.Equal(0, feed.Count);           // still drained + cleared in-tick → still empty at the fold boundary
        }

        [Fact]
        public void FeedsWithDifferentCapacities_DrainToIdenticalXp_CapacityIsNotSimState()
        {
            // Determinism guard for the growth itself. Capacity is a private allocation detail — never folded, never
            // serialized, never replicated — so two peers/sessions whose feeds grew differently (one having survived a
            // huge battle earlier in the match) must still credit byte-identical XP from the same death sequence. A
            // future "reuse the tail of the grown buffer" or "wrap instead of grow" mistake breaks this.
            (EntityWorld worldA, HeroStore heroesA, DeathFeed fresh, HeroXpSystem sysA, int slotA) = MakeHeroFixture();
            (EntityWorld worldB, HeroStore heroesB, DeathFeed preGrown, HeroXpSystem sysB, int slotB) = MakeHeroFixture();

            // Pre-grow B's feed, then reset it: same logical (empty) state, different capacity.
            for (int i = 0; i < Overflow; i++) preGrown.Push(FixedVec3.Zero, Faction.Neutral, Fixed.One);
            preGrown.Clear();
            Assert.NotEqual(fresh.Capacity, preGrown.Capacity);

            for (int i = 0; i < 10; i++)
            {
                var pos = new FixedVec3(Fixed.FromInt(i % 4), Fixed.Zero, Fixed.Zero);
                fresh.Push(pos, Faction.Neutral, Fixed.FromInt(i + 1));
                preGrown.Push(pos, Faction.Neutral, Fixed.FromInt(i + 1));
            }
            sysA.Tick(worldA, Dt);
            sysB.Tick(worldB, Dt);

            Assert.Equal(heroesA.Xp[slotA].Raw, heroesB.Xp[slotB].Raw);
            Assert.Equal(heroesA.Level[slotA], heroesB.Level[slotB]);
        }

        /// <summary>One Player1 hero at the origin with a wide XP-share radius and a curve high enough that banked XP
        /// never levels it (so the tests read the raw credited total), wired to a fresh feed + HeroXpSystem.</summary>
        private static (EntityWorld World, HeroStore Heroes, DeathFeed Feed, HeroXpSystem Sys, int Slot) MakeHeroFixture()
        {
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var feed = new DeathFeed();
            var heroes = new HeroStore();

            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int slot = heroes.Mint(new HeroId(7), ent, level: 1, xp: Fixed.Zero,
                maxLevel: 10, baseXp: Fixed.FromInt(30000), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(100),
                healthPerLevel: Fixed.Zero, damagePerLevel: Fixed.Zero, armorPerLevel: Fixed.Zero);
            world.HeroIndex[ent] = heroes.PackRef(slot);

            return (world, heroes, feed, new HeroXpSystem(heroes, modifiers, feed), slot);
        }
    }
}
