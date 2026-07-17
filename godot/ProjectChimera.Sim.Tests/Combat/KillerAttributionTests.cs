#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 7.5 — the killer-attribution SoA (<see cref="EntityWorld.KillerOf"/> /
    /// <see cref="EntityWorld.KillerFactionOf"/>): written ONLY by <see cref="DamageResolver.KillEntity"/> (the
    /// single death choke point), threaded from the hitscan path (DamageContext.AttackerId), the projectile path
    /// (the source id snapshotted at Spawn, honoured at impact AND splash), and the ability self-lethal path; a
    /// recycled slot never inherits attribution (the SoA-recycle trap); non-combat destroys leave −1.
    /// </summary>
    public class KillerAttributionTests
    {
        private static EntityWorld NewWorld() => new EntityWorld();

        [Fact]
        public void HitscanLethalDamage_WritesKillerIdAndSnapshottedFaction()
        {
            var world = NewWorld();
            int victim   = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            var ctx = new DamageContext(world, victim, ArmorType.Unarmored, world.FactionOf[attacker],
                                        DamageTable.Default, null, null, null, attackerId: attacker);
            Assert.True(DamageResolver.Apply(in ctx, Fixed.FromInt(100), DamageType.Normal));

            Assert.Equal(attacker, world.KillerOf[victim]);
            Assert.Equal(1, world.KillerFactionOf[victim]); // Player2 → slot 1 (snapshotted, not derived)
        }

        [Fact]
        public void ProjectileImpact_CreditsTheSpawnSnapshottedSource_OnPrimaryAndSplash()
        {
            var world = NewWorld();
            int shooter = world.Create(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.Zero),
                                       Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int primary = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            int splashed = world.Create(new FixedVec3(Fixed.Half, Fixed.Zero, Fixed.Zero),
                                        Faction.Player1, Fixed.FromInt(1), Fixed.One);

            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);
            // Spawn ON the target (inside the hit radius) so the very next tick resolves the impact.
            store.Spawn(world.Position[primary], primary, world.Position[primary],
                        Fixed.FromInt(100), DamageType.Normal, ArmorType.Unarmored, Faction.Player2,
                        Fixed.FromInt(18), splashRadius: Fixed.FromInt(2), sourceId: shooter);
            system.Tick(world, SimulationLoop.FixedDt);

            Assert.False(world.IsAlive(primary));
            Assert.False(world.IsAlive(splashed));
            Assert.Equal(shooter, world.KillerOf[primary]);   // primary hit credits the spawn snapshot
            Assert.Equal(shooter, world.KillerOf[splashed]);  // splash is part of the same impact
            Assert.Equal(1, world.KillerFactionOf[primary]);
            Assert.Equal(1, world.KillerFactionOf[splashed]);
        }

        [Fact]
        public void SelfLethal_CreditsTheCasterItself()
        {
            var world = NewWorld();
            int caster = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            world.Health[caster] = Fixed.Zero;
            DamageResolver.KillEntity(world, caster, world.FactionOf[caster], null, null, null, attackerId: caster);

            Assert.Equal(caster, world.KillerOf[caster]);
            Assert.Equal(0, world.KillerFactionOf[caster]); // Player1 → slot 0
        }

        [Fact]
        public void NonCombatDestroy_LeavesMinusOne()
        {
            var world = NewWorld();
            int unit = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            world.Destroy(unit); // editor delete / scripted removal — never KillEntity
            Assert.Equal(-1, world.KillerOf[unit]);
            Assert.Equal(-1, world.KillerFactionOf[unit]);
        }

        [Fact]
        public void RecycledSlot_NeverInheritsPriorAttribution()
        {
            var world = NewWorld();
            int victim   = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            world.Health[victim] = Fixed.Zero;
            DamageResolver.KillEntity(world, victim, Faction.Player2, null, null, null, attackerId: attacker);
            Assert.Equal(attacker, world.KillerOf[victim]); // written pre-Destroy

            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            Assert.Equal(victim, recycled); // the free list reuses the slot — the trap this test exists for
            Assert.Equal(-1, world.KillerOf[recycled]);
            Assert.Equal(-1, world.KillerFactionOf[recycled]);
        }

        [Fact]
        public void NeutralKiller_SnapshotsMinusOneFactionSlot()
        {
            var world = NewWorld();
            int victim = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            world.Health[victim] = Fixed.Zero;
            DamageResolver.KillEntity(world, victim, Faction.Neutral, null, null, null);
            Assert.Equal(-1, world.KillerOf[victim]);        // unknown attacker
            Assert.Equal(-1, world.KillerFactionOf[victim]); // Neutral (0) → −1, never a phantom slot
        }
    }
}
