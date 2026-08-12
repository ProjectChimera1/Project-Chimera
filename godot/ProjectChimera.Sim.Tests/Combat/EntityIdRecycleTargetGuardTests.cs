#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
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
        public void HeldAutoTarget_RecycledIntoAnotherEnemy_IsDroppedThenConsciouslyReacquired()
        {
            // Story 15-23 (DW-775) — the conscious flip of the pre-15-23 "KeepsFiring" pin. The held ref to the
            // recycled slot is DROPPED (generation mismatch — the inheritance is gone), and the same tick's
            // FindNearestEnemy re-acquires the still-hostile occupant as a fresh nearest-enemy acquisition. Combat
            // does not stall (the WC3 feel is preserved), but the held ref now carries the SUCCESSOR's generation —
            // proof it went through re-acquisition, not inheritance.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            Assert.Equal(w.PackRef(foe), w.AttackTarget[atk]); // gen 0: packed == raw
            int successor = RecycleSlot(w, foe, V(2, 0), Faction.Player4); // still an enemy of P1/P2

            combat.Tick(w, Dt);

            Assert.Equal(1, w.Generation[successor]);              // the slot really recycled
            Assert.Equal(w.PackRef(successor), w.AttackTarget[atk]); // held at the NEW generation — re-acquired, not inherited
            Assert.True(w.Health[successor] < FullHp);             // and legitimately engaged
            Assert.Equal(EntityFlags.Attacking, w.Flags[atk] & EntityFlags.Attacking);
        }

        [Fact]
        public void HeldAutoTarget_Reacquisition_PicksNearestEnemy_NeverTheInheritedSlot()
        {
            // Story 15-23 — the observable difference between inheritance and re-acquisition. The recycled
            // successor sits FARTHER than a fresh legal enemy: silent inheritance would keep firing at the
            // successor (it is inside weapon range); the DW-775 drop + re-acquire picks the NEAREST enemy instead.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            int successor = RecycleSlot(w, foe, V(8, 0), Faction.Player4); // hostile, in range 10, but FAR
            int nearer    = Victim(w, V(3, 0), Faction.Player3);          // a fresh, closer legal enemy

            combat.Tick(w, Dt);

            Assert.Equal(w.PackRef(nearer), w.AttackTarget[atk]); // nearest-enemy acquisition, not the stale slot
            Assert.True(w.Health[nearer] < FullHp);
            Assert.Equal(FullHp, w.Health[successor]);            // the recycled occupant was never struck
        }

        [Fact]
        public void HeldAutoTarget_RecycledIntoNeutral_IsReacquiredAsNeutral()
        {
            // Neutral is never allied to a player faction (AreAllied(P1, Neutral) == false), so a Neutral occupant
            // is still a legal auto-target — the re-acquisition must not degrade into "ignore any non-enemy".
            // Story 15-23: reached via drop + FindNearestEnemy (the held ref carries the new generation).
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            combat.Tick(w, Dt);
            int neutral = RecycleSlot(w, foe, V(2, 0), Faction.Neutral);

            combat.Tick(w, Dt);

            Assert.Equal(w.PackRef(neutral), w.AttackTarget[atk]);
            Assert.True(w.Health[neutral] < FullHp);
        }

        // ── Story 15-23 (DW-775): the FORCED paths — CommandTarget as a packed ref ────────────────────────────────

        [Fact]
        public void ForcedAttack_TargetRecycledIntoAnotherEnemy_RevertsTheOrder_InsteadOfForceFiringTheOccupant()
        {
            // A player force-fired a SPECIFIC enemy. That enemy's slot recycled into a DIFFERENT hostile unit.
            // Pre-15-23 the order silently transferred (the occupant passed every faction guard); now the packed
            // CommandTarget fails TryResolveRef and the order reverts to Idle — the unit may then AUTO-acquire the
            // occupant as a fresh Idle acquisition, but the forced order itself is gone.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(2, 0), Faction.Player3);

            OrderApplier.ApplyActiveOrder(w, atk, UnitCommand.AttackTarget, w.PackRef(foe), 0);
            combat.Tick(w, Dt);
            Assert.True(w.Health[foe] < FullHp); // the forced order engaged

            int successor = RecycleSlot(w, foe, V(2, 0), Faction.Player4);

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[atk]); // the forced order reverted…
            Assert.Equal(-1, w.CommandTarget[atk]);              // …and the stale ref was cleared
        }

        [Fact]
        public void Follow_TargetRecycledIntoNewFriendly_DropsToIdle_InsteadOfEscortingAStranger()
        {
            // Follow tracks ONE friendly. Its slot recycling into a brand-new same-faction unit used to pass the
            // guard (same faction ⇒ accepted) — the escort silently switched to a stranger. The packed ref drops it.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int escort = Victim(w, V(0, 0), Faction.Player1);
            int leader = Victim(w, V(5, 0), Faction.Player1);

            OrderApplier.ApplyActiveOrder(w, escort, UnitCommand.Follow, w.PackRef(leader), 0);
            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Follow, w.CommandState[escort]); // tracking

            int stranger = RecycleSlot(w, leader, V(5, 0), Faction.Player1); // NEW friendly in the same slot

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[escort]);
            Assert.Equal(-1, w.CommandTarget[escort]);
            _ = stranger; // never followed
        }

        [Fact]
        public void QueuedAttackOrder_TargetRecycledBeforePop_Fizzles_AndTheQueueContinues()
        {
            // The order ring is the longest-lived raw-id holder pre-15-23: a Shift-queued attack could sit for
            // arbitrarily many ticks. The packed payload fails consumption after a recycle: the popped order
            // reverts to Idle (exactly like "the target died"), and the ring's NEXT order dispatches.
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            var queue  = new OrderQueueSystem();
            int atk = Attacker(w, V(0, 0), Faction.Player1);
            int foe = Victim(w, V(30, 0), Faction.Player3); // far away — not auto-acquirable from (0,0)

            // Plain Move to the CURRENT position (arrives instantly — no MovementSystem needed in this harness),
            // then a queued forced attack on foe, then a queued Hold.
            var move = new UnitOrder(atk, UnitCommand.Move, Fixed.Zero, Fixed.Zero);
            OrderApplier.Apply(w, in move, Faction.Player1);
            var queuedAtk = new UnitOrder(atk, (UnitCommand)((byte)UnitCommand.AttackTarget | UnitOrderFlags.Queued),
                                          Fixed.FromRaw(w.PackRef(foe)), Fixed.Zero);
            OrderApplier.Apply(w, in queuedAtk, Faction.Player1);
            var queuedHold = new UnitOrder(atk, (UnitCommand)((byte)UnitCommand.HoldPosition | UnitOrderFlags.Queued),
                                           Fixed.Zero, Fixed.Zero);
            OrderApplier.Apply(w, in queuedHold, Faction.Player1);
            Assert.Equal(2, w.OrderQueueCount[atk]);

            // The queued target dies and its slot recycles into ANOTHER enemy before the queue ever pops.
            int successor = RecycleSlot(w, foe, V(30, 0), Faction.Player4);

            // Stop completes immediately → pop the queued attack → its packed ref fails → revert to Idle (same
            // tick, via CombatSystem) → next tick the queue pops the Hold. The successor is NEVER engaged (it is
            // far outside acquisition range, so only inheritance could have attacked it).
            for (int t = 0; t < 4; t++) { queue.Tick(w, Dt); combat.Tick(w, Dt); }

            Assert.Equal(UnitCommand.HoldPosition, w.CommandState[atk]); // the queue continued past the fizzled order
            Assert.Equal(0, w.OrderQueueCount[atk]);                     // fully drained
            Assert.Equal(FullHp, w.Health[successor]);                   // the recycled occupant was never force-fired
        }

        // ── DW-945: the order's SUBJECT is a packed ref too (the target-side class, closed on the commanded unit) ──

        [Fact]
        public void OrderSubject_UnitRecycledInsideTheDelayWindow_OrderIsDropped_NotInheritedByTheTrainee()
        {
            // The wire scenario: order unit X to move at tick T; X dies at T+1; a same-faction train completion
            // recycles X's slot at T+2 (LIFO — the just-freed slot is the FIRST reused); the order applies at
            // T+delay. Pre-DW-945 the guard (IsAlive + faction) passed and the brand-new unit inherited the stale
            // order — abandoning its rally walk, its queued-order ring wiped. The packed SUBJECT fails
            // TryResolveRef and the order is dropped on every peer identically.
            var w = new EntityWorld();
            int x = Victim(w, V(0, 0), Faction.Player1);
            var order = new UnitOrder(w.PackRef(x), UnitCommand.Move, Fixed.FromInt(30), Fixed.FromInt(30)); // issued at T

            int trainee = RecycleSlot(w, x, V(0, 0), Faction.Player1); // X dies; a same-faction trainee takes the slot
            w.MoveTarget[trainee]  = V(5, 5);                          // the trainee's own business (its rally walk)
            w.CommandState[trainee] = UnitCommand.Move;

            OrderApplier.Apply(w, in order, Faction.Player1);          // the stale order reaches exec-tick

            Assert.Equal(V(5, 5), w.MoveTarget[trainee]);              // untouched — the order was DROPPED
            Assert.Equal(UnitCommand.Move, w.CommandState[trainee]);
        }

        [Fact]
        public void OrderSubject_LiveRecycledSlotUnit_IsStillCommandable_TheGuardIsNotOverBroad()
        {
            // A CURRENT packed ref to the occupant of a recycled (generation > 0) slot must command normally —
            // the guard is generation-exact, never "refuse recycled slots".
            var w = new EntityWorld();
            int x = Victim(w, V(0, 0), Faction.Player1);
            int y = RecycleSlot(w, x, V(0, 0), Faction.Player1);
            Assert.Equal(1, w.Generation[y]);

            var order = new UnitOrder(w.PackRef(y), UnitCommand.Move, Fixed.FromInt(30), Fixed.FromInt(30));
            OrderApplier.Apply(w, in order, Faction.Player1);

            Assert.Equal(UnitCommand.Move, w.CommandState[y]);
            Assert.Equal(Fixed.FromInt(30), w.MoveTarget[y].X);
        }

        // ── Story 15-23 (DW-775): kill ATTRIBUTION — dead keeps credit, recycled degrades ─────────────────────────

        [Fact]
        public void ProjectileSource_DeadButNotRecycled_KeepsKillCredit()
        {
            // A unit that dies after firing the killing shot still owns the kill (event.killer names it) — the
            // attribution resolve is generation-match-only, NOT liveness-gated.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int shooter = Victim(w, V(5, 0), Faction.Player1);
            int foe     = Victim(w, V(0, 0), Faction.Player3);
            int p = store.Spawn(V(0, 0), w.PackRef(foe), V(0, 0), Fixed.FromInt(200), DamageType.Normal,
                                ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(18),
                                sourceId: w.PackRef(shooter));

            w.Destroy(shooter); // the shooter dies mid-flight — slot freed but NOT recycled

            system.Tick(w, Dt);

            Assert.False(w.IsAlive(foe));                 // lethal hit landed
            Assert.Equal(w.PackRef(shooter), w.KillerOf[foe]); // credit kept (packed, same generation)
            Assert.Equal(shooter, w.DeathLog.KillerAt(0) & ((1 << EntityWorld.REF_SLOT_BITS) - 1));
            _ = p;
        }

        [Fact]
        public void ProjectileSource_RecycledMidFlight_DegradesKillCreditToUnknown()
        {
            // The shooter's slot recycles into a NEW unit before impact: the kill must NOT be credited to the new
            // occupant — attribution degrades to −1 (unknown).
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store);

            int shooter = Victim(w, V(5, 0), Faction.Player1);
            int foe     = Victim(w, V(0, 0), Faction.Player3);
            int p = store.Spawn(V(0, 0), w.PackRef(foe), V(0, 0), Fixed.FromInt(200), DamageType.Normal,
                                ArmorType.Unarmored, Faction.Player1, speed: Fixed.FromInt(18),
                                sourceId: w.PackRef(shooter));

            int bystander = RecycleSlot(w, shooter, V(5, 0), Faction.Player1); // an innocent takes the slot

            system.Tick(w, Dt);

            Assert.False(w.IsAlive(foe));
            Assert.Equal(-1, w.KillerOf[foe]); // PackRefOrNone(-1): the resolve degraded, the innocent is not credited
            _ = (p, bystander);
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
        public void Projectile_PrimaryTargetRecycledIntoAnotherEnemy_DropsHarmlessly()
        {
            // Story 15-23 (DW-775) — the conscious flip of the pre-15-23 "StillDetonates" pin. The shell tracked a
            // SPECIFIC unit; that unit is gone (its slot merely recycled into a different hostile). The packed
            // TargetId fails TryResolveRef, so the shell coasts to the last known position and drops — exactly the
            // died-in-flight arm, and exactly what the building half has done since Story 2.13. Nobody is struck:
            // the occupant was never the shot's target.
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int foe = Victim(w, V(0, 0), Faction.Player3);
            int p   = SpawnImpactingShell(store, V(0, 0), foe, Faction.Player1);

            int successor = RecycleSlot(w, foe, V(0, 0), Faction.Player4); // hostile occupant — but not the target

            system.Tick(w, Dt);

            Assert.Equal(FullHp, w.Health[successor]); // dropped harmlessly, never transferred
            Assert.False(store.Alive[p]);              // and the shell still retires (no orbiting leak)
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
