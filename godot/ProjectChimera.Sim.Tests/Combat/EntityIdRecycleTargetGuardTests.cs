#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-444 / DW-446 — ENTITY-ID RECYCLE guards on the two combat paths that HOLD an entity id ACROSS ticks.
    ///
    /// <para><see cref="EntityWorld"/> recycles entity ids through a LIFO free list, so the slot a shell is flying at
    /// (DW-444) or a unit is holding as its auto-acquired <c>AttackTarget</c> (DW-446) can be re-allocated to a
    /// BRAND-NEW unit between the moment it was chosen and the moment it is used — including one of the attacker's OWN
    /// units or an ALLIED faction's (a teammate training into a freed enemy slot in a 2v2). Story 9.14 added
    /// allied-exclusion at ACQUISITION and on the per-tick FORCED paths only; both held-id paths re-checked nothing but
    /// <c>IsAlive</c>, so a recycled slot let the attacker damage a friendly — violating "an ally is never
    /// auto-attacked", and (same-faction) plain friendly fire even with no alliance mask at all.</para>
    ///
    /// <para>Every test drives the recycle EXPLICITLY (destroy, then create — asserting the id really came back) so it
    /// fails without the guards rather than relying on an incidental allocation order. Godot-free,
    /// <see cref="Fixed"/>-only. The null-mask / FFA byte-identity of the allied term is covered by the combat goldens
    /// (no golden recycles a slot into a friendly while a target/shell is in flight, so no checksum moves).</para>
    /// </summary>
    public class EntityIdRecycleTargetGuardTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static readonly Fixed FullHp = Fixed.FromInt(100);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>P1 allied with P2 (shared team id); P3/P4 stay on their own teams (enemies of both).</summary>
        private static AllianceStore P1P2Allied()
        {
            var a = new AllianceStore();
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            return a;
        }

        /// <summary>An Idle hitscan combatant that fires every tick (AttackSpeed 0 ⇒ no cooldown gate).</summary>
        private static int Attacker(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, FullHp, Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(20);
            w.AttackRange[id]  = Fixed.FromInt(10);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.CommandState[id] = UnitCommand.Idle;
            return id;
        }

        /// <summary>A passive 100-HP body (no attack ⇒ never acts on its own).</summary>
        private static int Victim(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, FullHp, Fixed.FromInt(3));
            w.ArmorTypeOf[id] = ArmorType.Unarmored;
            return id;
        }

        /// <summary>Free <paramref name="doomed"/> and immediately re-create into the SAME slot (LIFO free list).</summary>
        private static int RecycleSlot(EntityWorld w, int doomed, FixedVec3 pos, Faction newFaction)
        {
            w.Destroy(doomed);
            int reborn = Victim(w, pos, newFaction);
            Assert.Equal(doomed, reborn); // the whole defect class depends on the id actually coming back
            return reborn;
        }

        // ── DW-446: the HELD auto-acquired AttackTarget ────────────────────────────────────────────────────────────

        [Fact]
        public void HeldAutoTarget_RecycledIntoAlliedUnit_IsDroppedNotAttacked()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            Assert.Equal(foe, w.AttackTarget[atk]);        // target legitimately acquired and HELD
            Assert.True(w.Health[foe] < FullHp);

            // The enemy dies and an ALLY (P2, same team as the attacker's P1) is trained into its freed slot.
            int ally = RecycleSlot(w, foe, V(2, 0), Faction.Player2);

            combat.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[ally]);          // the retained id is re-checked → the ally is never struck
            Assert.Equal(-1, w.AttackTarget[atk]);         // dropped, not held
            Assert.Equal((EntityFlags)0, w.Flags[atk] & EntityFlags.Attacking);
        }

        [Fact]
        public void HeldAutoTarget_RecycledIntoOwnFactionUnit_IsDroppedNotAttacked()
        {
            // NO alliance mask at all (FFA/null): the same-faction term of the guard is unconditional, because this
            // half of the defect class (friendly fire onto a recycled slot) pre-dates alliances entirely.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            Assert.Equal(foe, w.AttackTarget[atk]);

            int friendly = RecycleSlot(w, foe, V(2, 0), Faction.Player1); // MY OWN faction takes the slot

            combat.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[friendly]);
            Assert.Equal(-1, w.AttackTarget[atk]);
            Assert.Equal((EntityFlags)0, w.Flags[atk] & EntityFlags.Attacking);
        }

        [Fact]
        public void HeldAutoTarget_RecycledIntoAnotherEnemy_KeepsFiring_GuardIsNotOverBroad()
        {
            // The guard must clear ONLY on a friendly occupant. A slot recycled into a still-hostile faction stays a
            // legal target (the pre-existing ABA behaviour DW-446 deliberately left alone), so combat does not stall.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            int successor = RecycleSlot(w, foe, V(2, 0), Faction.Player4); // still an enemy of P1/P2

            combat.Tick(w, Dt);

            Assert.Equal(successor, w.AttackTarget[atk]);
            Assert.True(w.Health[successor] < FullHp);
            Assert.Equal(EntityFlags.Attacking, w.Flags[atk] & EntityFlags.Attacking);
        }

        [Fact]
        public void HeldAutoTarget_RecycledIntoNeutral_IsStillAttacked()
        {
            // Neutral is never allied to a player faction (AreAllied(P1, Neutral) == false), so a Neutral occupant is
            // still a legal auto-target — the guard must not degrade into "clear on any non-enemy".
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            int neutral = RecycleSlot(w, foe, V(2, 0), Faction.Neutral);

            combat.Tick(w, Dt);

            Assert.Equal(neutral, w.AttackTarget[atk]);
            Assert.True(w.Health[neutral] < FullHp);
        }

        // ── DW-444: the in-flight projectile's PRIMARY / direct-hit target ─────────────────────────────────────────

        /// <summary>
        /// Spawn a shell already sitting on its target position (distSqr 0 ≤ HIT_SQR) so it resolves on the very next
        /// tick — the impact check under test, with no flight ticks to reason about.
        /// </summary>
        private static int SpawnImpactingShell(ProjectileStore store, FixedVec3 at, int targetId, Faction owner,
                                               Fixed splashRadius = default)
            => store.Spawn(at, targetId, at, Fixed.FromInt(20), DamageType.Normal, ArmorType.Unarmored,
                           owner, speed: Fixed.FromInt(18), splashRadius: splashRadius);

        [Fact]
        public void Projectile_PrimaryTargetRecycledIntoAlly_DropsHarmlessly()
        {
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int foe = Victim(w, V(0, 0), Faction.Player3);
            int p   = SpawnImpactingShell(store, V(0, 0), foe, Faction.Player1);

            // The target dies and an ALLY of the shell's owner is trained into its slot before the shell lands.
            int ally = RecycleSlot(w, foe, V(0, 0), Faction.Player2);

            system.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[ally]); // the primary hit is alliance-rechecked, exactly like splash is
            Assert.False(store.Alive[p]);         // and the shell still retires (no orbiting leak)
        }

        [Fact]
        public void Projectile_PrimaryTargetRecycledIntoOwnFaction_DropsHarmlessly()
        {
            // NO alliance mask (FFA/null): the same-faction term stands on its own, as it does for splash.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int foe = Victim(w, V(0, 0), Faction.Player3);
            int p   = SpawnImpactingShell(store, V(0, 0), foe, Faction.Player1);

            int friendly = RecycleSlot(w, foe, V(0, 0), Faction.Player1); // the OWNER's own faction takes the slot

            system.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[friendly]);
            Assert.False(store.Alive[p]);
        }

        [Fact]
        public void Projectile_PrimaryTargetRecycledIntoAlly_DropsWithoutSplashingEither()
        {
            // A friendly occupant makes the shell's target GONE, so it drops exactly like a target that died in flight
            // without a recycle: no primary damage, no impact event, and no splash around the ally it homed onto.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int foe      = Victim(w, V(0, 0), Faction.Player3);
            int bystander = Victim(w, V(1, 0), Faction.Player4); // a real enemy well inside the splash radius
            int p = SpawnImpactingShell(store, V(0, 0), foe, Faction.Player1, splashRadius: Fixed.FromInt(5));

            int ally = RecycleSlot(w, foe, V(0, 0), Faction.Player2);

            system.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[ally]);
            Assert.Equal(FullHp, w.Health[bystander]); // the whole impact is cancelled, not just the primary half
            Assert.False(store.Alive[p]);
        }

        [Fact]
        public void Projectile_PrimaryTargetRecycledIntoAnotherEnemy_StillDetonates_GuardIsNotOverBroad()
        {
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int foe = Victim(w, V(0, 0), Faction.Player3);
            int p   = SpawnImpactingShell(store, V(0, 0), foe, Faction.Player1);

            int successor = RecycleSlot(w, foe, V(0, 0), Faction.Player4); // still hostile → the shell still lands

            system.Tick(w, Dt);

            Assert.True(w.Health[successor] < FullHp);
            Assert.False(store.Alive[p]);
        }

        [Fact]
        public void Projectile_PrimaryTargetNeutral_IsStillHit_UnderAnActiveMask()
        {
            // AreAllied(P1, Neutral) == false — Neutral must keep taking direct hits with a mask wired in.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int neutral = Victim(w, V(0, 0), Faction.Neutral);
            int p = SpawnImpactingShell(store, V(0, 0), neutral, Faction.Player1);

            system.Tick(w, Dt);

            Assert.True(w.Health[neutral] < FullHp);
            Assert.False(store.Alive[p]);
        }
    }
}
