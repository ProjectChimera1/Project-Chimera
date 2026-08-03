#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-493 — the fail-closed <c>!IsAlive</c> entry guard on <see cref="DamageResolver.KillEntity"/> (the reused
    /// single death primitive). Every current caller checks aliveness at its own site, but the primitive itself must
    /// refuse a dead/recycled slot so a FUTURE lethal path (or a double-collapse in one tick) that reaches it
    /// unguarded cannot double-Destroy, emit a phantom <c>UnitKilled</c>, inflate <c>RecordKill</c>, or push ghost
    /// death records into the XP feed / the DW-367 death log.
    /// </summary>
    public class KillEntityGuardTests
    {
        [Fact]
        public void SecondKillOnTheSameDeadSlot_IsFailClosedNoOp()
        {
            var world  = new EntityWorld();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            var feed   = new DeathFeed();
            int victim   = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            world.Health[victim] = Fixed.Zero;
            DamageResolver.KillEntity(world, victim, Faction.Player2, events, stats, feed, attackerId: attacker);

            Assert.False(world.IsAlive(victim));
            Assert.Equal(1, events.Count);                    // exactly one UnitKilled (victim is not a hero)
            Assert.Equal(1, stats.Losses(Faction.Player1));
            Assert.Equal(1, stats.Kills(Faction.Player2));
            Assert.Equal(1, feed.Count);                      // one XP death record
            Assert.Equal(1, world.DeathLog.Count);            // one unit_dies attribution record

            // The unguarded-future-caller scenario: a SECOND KillEntity on the already-dead slot must be a total
            // no-op — without the entry guard it re-emits UnitKilled, re-records the loss/kill, and pushes ghost
            // death records (Destroy alone is idempotent, so only the guard closes those).
            DamageResolver.KillEntity(world, victim, Faction.Player2, events, stats, feed, attackerId: attacker);

            Assert.Equal(1, events.Count);                    // no phantom UnitKilled
            Assert.Equal(1, stats.Losses(Faction.Player1));   // no inflated loss count
            Assert.Equal(1, stats.Kills(Faction.Player2));    // no inflated kill credit
            Assert.Equal(1, feed.Count);                      // no ghost XP death
            Assert.Equal(1, world.DeathLog.Count);            // no ghost unit_dies record
        }

        [Fact]
        public void NeverAliveOrOutOfRangeId_IsNoOp()
        {
            var world  = new EntityWorld();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            var feed   = new DeathFeed();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One); // one real unit, untouched

            // A never-allocated slot, a negative id, and an out-of-range id — all fail closed (IsAlive bounds-checks).
            DamageResolver.KillEntity(world, world.HighWaterMark, Faction.Player2, events, stats, feed);
            DamageResolver.KillEntity(world, -1, Faction.Player2, events, stats, feed);
            DamageResolver.KillEntity(world, EntityWorld.MAX_ENTITIES + 5, Faction.Player2, events, stats, feed);

            Assert.Equal(0, events.Count);
            Assert.Equal(0, stats.Losses(Faction.Player1));
            Assert.Equal(0, stats.Kills(Faction.Player2));
            Assert.Equal(0, feed.Count);
            Assert.Equal(0, world.DeathLog.Count);
        }

        [Fact]
        public void RecycledAliveSlot_IsKillableAgain_TheGuardReadsCurrentAliveness()
        {
            var world  = new EntityWorld();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            int victim = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);

            world.Health[victim] = Fixed.Zero;
            DamageResolver.KillEntity(world, victim, Faction.Player2, events, stats);

            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            Assert.Equal(victim, recycled); // the free list reuses the slot

            // The guard gates on CURRENT aliveness, never a per-slot latch — the new occupant dies normally.
            world.Health[recycled] = Fixed.Zero;
            DamageResolver.KillEntity(world, recycled, Faction.Player2, events, stats);

            Assert.False(world.IsAlive(recycled));
            Assert.Equal(2, events.Count);
            Assert.Equal(2, stats.Losses(Faction.Player1));
            Assert.Equal(2, world.DeathLog.Count); // both deaths logged with their own attribution (DW-367)
        }
    }
}
