#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-664 — a TEMPORARY attack-damage debuff must PAUSE a force-attack order, never CANCEL it.
    ///
    /// <para><b>The defect.</b> <c>CombatSystem.Tick</c> routes a unit to <c>TickNonCombatant</c> on
    /// <see cref="EntityWorld.IsNonCombatant"/>, which reads the EFFECTIVE stat. <c>ModifierSystem.RecomputeEntity</c>
    /// zero-FLOORS that stat, so a modifier with <c>attack_damage_delta &lt;= -BaseAttackDamage</c> — authorable
    /// content, since <c>Modifier.CheckAuthoringBounds</c> bounds only <c>|delta| x MaxStacks</c> — makes a LIVE
    /// combatant indistinguishable from an authored worker for the debuff's duration. That router's
    /// AttackTarget/AttackBuilding arm then wrote <c>CommandState=Idle</c>, <c>CommandTarget=-1</c>,
    /// <c>AttackTarget=-1</c> and cleared <c>Moving|Attacking</c>, on the assumption that its entry condition was a
    /// permanent authoring property. Force-attack an enemy, take a temporary debuff, and the order was gone: on
    /// expiry the unit stood Idle and <c>TickIdleCombat</c> re-acquired the NEAREST enemy instead of the one the
    /// player picked. (Before DW-242 the same entry condition hit a bare <c>continue</c>, which mutated nothing, so
    /// the order survived the window intact — the regression came in with the router.)</para>
    ///
    /// <para><b>The contract this pins.</b> The disposal is asked of the AUTHORED stat
    /// (<see cref="EntityWorld.IsPermanentNonCombatant"/>): only a unit that could never execute the order with every
    /// modifier stripped off it has that order thrown away. A debuffed combatant keeps <c>CommandState</c> +
    /// <c>CommandTarget</c>, stops swinging, deals nothing, and resumes on the SAME target when the debuff expires —
    /// the disposition <see cref="StatusFlags.Disarmed"/> already had (DW-266: "still acquires and chases but can
    /// never land a hit") and the one <c>MovementSystem</c> states for a debuffed queued order.</para>
    ///
    /// <para><b>Non-vacuity.</b> Every "keeps its order" assertion is paired with a NEARER enemy that a cancelled
    /// order would have re-acquired instead, so the pre-fix behaviour is what the assertions catch — not merely a
    /// missing mutation. The permanent-non-combatant fences re-pin the DW-242 normalization the fix must preserve.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg.</para>
    /// </summary>
    public class DebuffedCombatantOrderPreservationTests
    {
        /// <summary>One tick at the 30 tps sim rate.</summary>
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        /// <summary>Base attack damage for the subject, and the magnitude of the debuff that zeroes it.</summary>
        private static readonly Fixed BaseDamage = Fixed.FromInt(10);

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static void AssertVec(FixedVec3 expected, FixedVec3 actual)
        {
            Assert.Equal(expected.X.Raw, actual.X.Raw);
            Assert.Equal(expected.Z.Raw, actual.Z.Raw);
        }

        /// <summary>
        /// A real melee combatant: authored <c>BaseAttackDamage</c> AND the matching effective value, hitscan so a
        /// landed blow is observable in one tick, zero attack speed so a cooldown never masks a refusal.
        /// </summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f, int range = 3)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.BaseAttackDamage[id]      = BaseDamage;
            w.EffectiveAttackDamage[id] = BaseDamage;
            w.AttackRange[id]  = Fixed.FromInt(range);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        /// <summary>An inert target: alive, hostile, and completely harmless so it never fights back into an assertion.</summary>
        private static int Dummy(EntityWorld w, FixedVec3 pos, Faction f)
            => w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));

        /// <summary>The unit the player force-attacked, temporarily debuffed to the ModifierSystem zero-floor.</summary>
        private static void DebuffToZero(EntityWorld w, int id)
        {
            w.EffectiveAttackDamage[id] = Fixed.Zero;
            Assert.True(w.IsNonCombatant(id), "the fixture must reproduce the DW-664 entry condition (effective == 0)");
            Assert.False(w.IsPermanentNonCombatant(id), "…while still being an authored COMBATANT (base > 0)");
        }

        // ── AttackTarget — the order survives the debuff window ───────────────────────────────────────────

        [Fact]
        public void DebuffedCombatant_UnderAttackTarget_KeepsItsForceAttackOrder()
        {
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1);
            int forced = Dummy(w, V(2, 0), Faction.Player2);
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = forced;

            DebuffToZero(w, u);
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.AttackTarget, w.CommandState[u]);
            Assert.Equal(forced, w.CommandTarget[u]);
        }

        [Fact]
        public void DebuffedCombatant_UnderAttackTarget_StopsSwingingAndDealsNothing()
        {
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1);
            int forced = Dummy(w, V(2, 0), Faction.Player2);   // well inside the subject's range 3
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = forced;
            w.AttackTarget[u]  = forced;                        // it was mid-swing when the debuff landed
            w.Flags[u]        |= EntityFlags.Attacking;

            Fixed before = w.Health[forced];
            DebuffToZero(w, u);
            combat.Tick(w, Dt);

            // Pausing must never leak into ENGAGEMENT: a unit that cannot deal damage still deals none.
            Assert.Equal(before.Raw, w.Health[forced].Raw);
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0, "presentation must stop the swing while debuffed");
            // The transient acquisition slot is dropped (nothing re-validates it while this arm runs, and a held id
            // goes stale across the window — DW-184's recycle trap). CommandTarget is what carries the order.
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.Equal(forced, w.CommandTarget[u]);
        }

        [Fact]
        public void DebuffedCombatant_UnderAttackTarget_KeepsItsChaseStateInsteadOfBeingReset()
        {
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1, range: 1);
            int forced = Dummy(w, V(20, 0), Faction.Player2);   // far out of range ⇒ the unit was chasing it
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = forced;
            w.MoveTarget[u]    = w.Position[forced];
            w.Flags[u]        |= EntityFlags.Moving;

            DebuffToZero(w, u);
            combat.Tick(w, Dt);

            // PAUSE, not CANCEL: MovementSystem's rule is that a debuff suspends a queued order. The pre-fix arm
            // cleared Moving and blanked MoveTarget's reason for existing by dropping the order entirely.
            Assert.Equal(UnitCommand.AttackTarget, w.CommandState[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0, "the chase is paused, not abandoned");
            AssertVec(V(20, 0), w.MoveTarget[u]);
        }

        [Fact]
        public void DebuffedCombatant_OnExpiry_ResumesTheForcedTarget_NotTheNearestEnemy()
        {
            // The whole player-visible defect in one test: the ORDER is what a cancel destroys, and the tell is
            // WHICH enemy gets hit afterwards.
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1);
            int nearer = Dummy(w, V(1, 0), Faction.Player2);   // what TickIdleCombat would re-acquire after a cancel
            int forced = Dummy(w, V(2, 0), Faction.Player2);   // what the player actually picked
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = forced;

            DebuffToZero(w, u);
            combat.Tick(w, Dt);                                 // the debuff window
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[nearer].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[forced].Raw);

            w.EffectiveAttackDamage[u] = BaseDamage;            // the debuff expires
            combat.Tick(w, Dt);

            Assert.True(w.Health[forced] < Fixed.FromInt(100), "the force-attacked target must take the blow");
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[nearer].Raw); // …and the nearer one must not
            Assert.Equal(UnitCommand.AttackTarget, w.CommandState[u]);
            Assert.Equal(forced, w.AttackTarget[u]);
        }

        [Fact]
        public void ACancelledOrder_WouldHitTheNearestEnemy_TheControlThatMakesTheTestAbove_LoadBearing()
        {
            // Positive control: the SAME board with the order genuinely gone really does produce the wrong victim,
            // so the assertion above is measuring the preserved order and not an inert fixture.
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1);
            int nearer = Dummy(w, V(1, 0), Faction.Player2);
            int forced = Dummy(w, V(2, 0), Faction.Player2);
            w.CommandState[u]  = UnitCommand.Idle;   // the state the pre-fix cancel left behind
            w.CommandTarget[u] = -1;

            combat.Tick(w, Dt);

            Assert.True(w.Health[nearer] < Fixed.FromInt(100));
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[forced].Raw);
        }

        // ── AttackBuilding — the same arm, the same rule ──────────────────────────────────────────────────

        [Fact]
        public void DebuffedCombatant_UnderAttackBuilding_KeepsItsRazeOrder()
        {
            var w         = new EntityWorld();
            var buildings = new BuildingStore();
            var combat    = new CombatSystem(new ProjectileStore(), buildings: buildings);

            int b = buildings.Create(V(2, 0), Faction.Player2, BuildingType.Barracks);
            int u = Combatant(w, V(0, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = buildings.PackRef(b);
            int packed = w.CommandTarget[u];

            Fixed before = buildings.Health[b];
            DebuffToZero(w, u);
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[u]);
            Assert.Equal(packed, w.CommandTarget[u]);
            Assert.Equal(before.Raw, buildings.Health[b].Raw);   // paused ⇒ still no damage
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0);
        }

        [Fact]
        public void DebuffedCombatant_UnderAttackBuilding_OnExpiry_ResumesRazingTheSameBuilding()
        {
            var w         = new EntityWorld();
            var buildings = new BuildingStore();
            var combat    = new CombatSystem(new ProjectileStore(), buildings: buildings);

            int b = buildings.Create(V(2, 0), Faction.Player2, BuildingType.Barracks);
            int u = Combatant(w, V(0, 0), Faction.Player1);
            w.AttackDomainOf[u] = AttackDomain.Ground | AttackDomain.Structure;
            w.DamageTypeOf[u]   = DamageType.Siege;
            w.CommandState[u]   = UnitCommand.AttackBuilding;
            w.CommandTarget[u]  = buildings.PackRef(b);

            Fixed before = buildings.Health[b];
            DebuffToZero(w, u);
            combat.Tick(w, Dt);
            Assert.Equal(before.Raw, buildings.Health[b].Raw);

            w.EffectiveAttackDamage[u] = BaseDamage;             // the debuff expires
            combat.Tick(w, Dt);

            Assert.True(buildings.Health[b] < before, "the raze must resume on the SAME building the player picked");
        }

        // ── Fences — the DW-242 normalization the fix must NOT weaken ─────────────────────────────────────

        [Fact]
        public void PermanentNonCombatant_UnderAttackTarget_StillNormalizesToIdle()
        {
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            // BaseAttackDamage stays at Create()'s zero — an authored worker/support unit, not a debuffed fighter.
            int u = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[u] = Fixed.FromInt(3);
            int enemy = Dummy(w, V(2, 0), Faction.Player2);
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = enemy;
            w.AttackTarget[u]  = enemy;
            w.Flags[u]        |= EntityFlags.Moving;

            Assert.True(w.IsPermanentNonCombatant(u));
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(-1, w.CommandTarget[u]);
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & (EntityFlags.Moving | EntityFlags.Attacking)) == 0);
        }

        [Fact]
        public void PermanentNonCombatant_UnderAttackBuilding_StillNormalizesToIdle()
        {
            var w         = new EntityWorld();
            var buildings = new BuildingStore();
            var combat    = new CombatSystem(new ProjectileStore(), buildings: buildings);

            int b = buildings.Create(V(2, 0), Faction.Player2, BuildingType.Barracks);
            int u = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackRange[u]   = Fixed.FromInt(3);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = buildings.PackRef(b);

            Assert.True(w.IsPermanentNonCombatant(u));
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(-1, w.CommandTarget[u]);
        }

        [Fact]
        public void ADebuffedCombatant_StillNeverAcquiresWhileIdle()
        {
            // The other half of the fence: preserving an EXPLICIT order must not hand a damage-less unit the idle
            // auto-combat path. An Idle debuffed combatant stays exactly as inert as any other non-combatant.
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());

            int u     = Combatant(w, V(0, 0), Faction.Player1);
            int enemy = Dummy(w, V(1, 0), Faction.Player2);
            w.CommandState[u] = UnitCommand.Idle;

            DebuffToZero(w, u);
            FixedVec3 moveBefore = w.MoveTarget[u];
            combat.Tick(w, Dt);

            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[enemy].Raw);
            Assert.Equal(-1, w.AttackTarget[u]);
            AssertVec(moveBefore, w.MoveTarget[u]);
        }

        // ── The predicate itself ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void IsPermanentNonCombatant_ReadsTheAuthoredStat_NotTheDebuffedEffectiveOne()
        {
            var w = new EntityWorld();
            int u = Combatant(w, V(0, 0), Faction.Player1);

            Assert.False(w.IsPermanentNonCombatant(u));
            Assert.False(w.IsNonCombatant(u));

            w.EffectiveAttackDamage[u] = Fixed.Zero;             // the ModifierSystem zero-floor
            Assert.True(w.IsNonCombatant(u));                    // …routes as a non-combatant THIS tick…
            Assert.False(w.IsPermanentNonCombatant(u));          // …but its orders are still a combatant's orders

            w.BaseAttackDamage[u] = Fixed.Zero;                  // an authored worker
            Assert.True(w.IsPermanentNonCombatant(u));
        }

        [Fact]
        public void IsPermanentNonCombatant_IsBoundsGuardedAndTreatsANegativeBaseAsPermanent()
        {
            var w = new EntityWorld();
            int u = Combatant(w, V(0, 0), Faction.Player1);

            // Out of range answers the conservative side, matching IsNonCombatant.
            Assert.True(w.IsPermanentNonCombatant(-1));
            Assert.True(w.IsPermanentNonCombatant(EntityWorld.MAX_ENTITIES));

            // A negative base is unreachable from authored content but producible by the SoA-direct save-restore
            // overlay: it is PERMANENT (no buff can arrive that makes the order executable), never "awaiting a buff".
            w.BaseAttackDamage[u] = -BaseDamage;
            Assert.True(w.IsPermanentNonCombatant(u));
        }

        // ── End-to-end: a real, authorable modifier drives the whole chain ────────────────────────────────

        [Fact]
        public void ARealExpiringModifier_ZeroesTheDamage_ThenTheUnitResumesItsOriginalTarget()
        {
            var w         = new EntityWorld();
            var modSys    = new ModifierSystem();
            var modifiers = new ModifierStore(w, modSys);
            modSys.AttachStore(modifiers);
            var combat = new CombatSystem(new ProjectileStore());

            int u      = Combatant(w, V(0, 0), Faction.Player1);
            int nearer = Dummy(w, V(1, 0), Faction.Player2);
            int forced = Dummy(w, V(2, 0), Faction.Player2);
            w.CommandState[u]  = UnitCommand.AttackTarget;
            w.CommandTarget[u] = forced;

            // Exactly -BaseAttackDamage for 2 ticks. CheckAuthoringBounds is the DW-488 content gate: it bounds
            // |delta| x MaxStacks only, so this debuff is authorable content, not a synthetic impossibility.
            var debuff = new Modifier(id: 4201, durationTicks: 2, stacking: StackRule.Refresh, maxStacks: 1,
                                      maxHealthDelta: Fixed.Zero, attackDamageDelta: -BaseDamage,
                                      moveSpeedDelta: Fixed.Zero, status: StatusFlags.None,
                                      periodEffect: null, periodTicks: 0);
            Assert.Null(debuff.CheckAuthoringBounds());

            Assert.True(modifiers.Apply(u, debuff, casterId: u, casterFaction: Faction.Player1));
            Assert.Equal(Fixed.Zero.Raw, w.EffectiveAttackDamage[u].Raw); // the eager recompute floors it at zero

            // Drive the real tick order (ModifierSystem is SimulationHost index 3, CombatSystem index 4) until the
            // debuff has expired, asserting the order survives every debuffed tick.
            int guard = 0;
            while (w.EffectiveAttackDamage[u] <= Fixed.Zero)
            {
                Assert.True(++guard <= 8, "the debuff must expire on its own — the fixture is wrong if it does not");
                Assert.Equal(UnitCommand.AttackTarget, w.CommandState[u]);
                Assert.Equal(forced, w.CommandTarget[u]);
                Assert.Equal(Fixed.FromInt(100).Raw, w.Health[nearer].Raw);
                Assert.Equal(Fixed.FromInt(100).Raw, w.Health[forced].Raw);

                modSys.Tick(w, Dt);
                combat.Tick(w, Dt);
            }

            Assert.Equal(BaseDamage.Raw, w.EffectiveAttackDamage[u].Raw); // the debuff really did revert
            modSys.Tick(w, Dt);
            combat.Tick(w, Dt);

            Assert.True(w.Health[forced] < Fixed.FromInt(100),
                "on expiry the unit must resume the target the PLAYER picked (DW-664)");
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[nearer].Raw);
        }
    }
}
