#nullable enable
using ProjectChimera.Combat;            // CombatSystem, DamageResolver, DamageContext, DamageTable, ProjectileStore
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, ResourceStore, SimulationLoop
using ProjectChimera.Core.Definitions;  // AbilityRegistry
using ProjectChimera.Effects;           // StatusFlags, Modifier, StackRule, ModifierStore/System, AbilityCastSystem
using ProjectChimera.Multiplayer;       // OrderApplier, UnitOrder
using ProjectChimera.Navigation;        // MovementSystem
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-266 — the HEADLINE gap: <see cref="EntityWorld.StatusFlagsOf"/> was written by <see cref="ModifierStore"/>,
    /// folded into <c>SimChecksum</c>, authorable through the 2.5 ability editor and rendered as a HUD icon — but read
    /// by NOTHING, so an authored stun cost energy, showed an icon, and did nothing. These tests are the behavioural
    /// oracle for each of the five flags now having a real runtime consumer:
    ///   • <see cref="StatusFlags.Rooted"/>     → <see cref="MovementSystem"/> anchor (no seek, no separation shove).
    ///   • <see cref="StatusFlags.Stunned"/>    → the same anchor PLUS the whole-unit gate in <see cref="CombatSystem"/>
    ///                                            PLUS the <see cref="AbilityCastSystem"/> refusal.
    ///   • <see cref="StatusFlags.Disarmed"/>   → CombatSystem's two damage choke points (unit + building).
    ///   • <see cref="StatusFlags.Silenced"/>   → the atomic AbilityCastSystem refusal.
    ///   • <see cref="StatusFlags.Invulnerable"/> → <see cref="DamageResolver.Apply"/>, the single entity-damage path.
    /// Every test also proves the flag is a PAUSE, not a cancel: clearing it restores the pre-status behaviour.
    /// Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg including the WSL cross-platform gate.
    /// </summary>
    public class StatusFlagRuntimeTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Mover(EntityWorld w, FixedVec3 from, FixedVec3 to)
        {
            int id = w.Create(from, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.MoveTarget[id] = to;
            w.Flags[id]     |= EntityFlags.Moving;
            return id;
        }

        /// <summary>An Idle hitscan attacker that fires every tick (AttackSpeed 0 ⇒ the cooldown resets to 0).</summary>
        private static int Attacker(EntityWorld w, FixedVec3 pos, int dmg = 20, int range = 12,
                                    AttackDelivery delivery = AttackDelivery.Hitscan)
        {
            int id = w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(range);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = delivery;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.CommandState[id] = UnitCommand.Idle;
            return id;
        }

        private static int Victim(EntityWorld w, FixedVec3 pos, int health = 1000)
        {
            int id = w.Create(pos, Faction.Player2, Fixed.FromInt(health), Fixed.FromInt(3));
            w.ArmorTypeOf[id] = ArmorType.Unarmored;
            return id;
        }

        // ── Rooted / Stunned → the MovementSystem anchor ───────────────────────────────────────────────

        [Theory]
        [InlineData((byte)StatusFlags.Rooted)]
        [InlineData((byte)StatusFlags.Stunned)]
        public void MoveBlockingStatus_AnchorsTheUnit_WhileAnIdenticalTwinWalksOn(byte statusRaw)
        {
            var status = (StatusFlags)statusRaw;
            var w = new EntityWorld();
            var move = new MovementSystem();
            int held = Mover(w, V(0, 0), V(20, 0));
            int free = Mover(w, V(0, 40), V(20, 40)); // far away ⇒ no separation coupling between the two
            w.StatusFlagsOf[held] = status;

            FixedVec3 start = w.Position[held];
            for (int t = 0; t < 60; t++) move.Tick(w, Dt);

            // The anchored unit has not moved AT ALL (exact Raw equality — not "moved less").
            Assert.Equal(start.X.Raw, w.Position[held].X.Raw);
            Assert.Equal(start.Z.Raw, w.Position[held].Z.Raw);
            Assert.Equal(Fixed.Zero.Raw, w.Velocity[held].X.Raw);
            Assert.Equal(Fixed.Zero.Raw, w.Velocity[held].Z.Raw);
            // The identical twin proves the scenario really does move a healthy unit.
            Assert.True(w.Position[free].X > Fixed.FromInt(5), "the un-statused twin must have walked toward its target");

            // PAUSE, NOT CANCEL: the order survived the status, so clearing it resumes the same move.
            Assert.True((w.Flags[held] & EntityFlags.Moving) != 0);
            Assert.Equal(V(20, 0).X.Raw, w.MoveTarget[held].X.Raw);
            w.StatusFlagsOf[held] = StatusFlags.None;
            for (int t = 0; t < 60; t++) move.Tick(w, Dt);
            Assert.True(w.Position[held].X > Fixed.FromInt(5), "clearing the status must let the queued move resume");
        }

        [Fact]
        public void RootedUnit_IsNotDisplacedByASeparationShove()
        {
            // Six movers march straight through a rooted unit's tile. Pre-fix it would be shoved off; anchored, its
            // position must be bit-identical after the whole crowd has passed.
            var w = new EntityWorld();
            var move = new MovementSystem();
            int rooted = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.StatusFlagsOf[rooted] = StatusFlags.Rooted;
            for (int k = 0; k < 6; k++)
            {
                int u = Mover(w, V(-4, k - 3), V(10, k - 3));
                w.CollisionRadius[u] = Fixed.One;
            }
            w.CollisionRadius[rooted] = Fixed.One;

            FixedVec3 start = w.Position[rooted];
            for (int t = 0; t < 200; t++) move.Tick(w, Dt);

            Assert.Equal(start.X.Raw, w.Position[rooted].X.Raw);
            Assert.Equal(start.Z.Raw, w.Position[rooted].Z.Raw);
        }

        [Fact]
        public void NoStatus_MovementIsBitIdenticalToAWorldWithoutTheFeature()
        {
            // The gate must be an exact no-op at StatusFlags.None — this is what keeps every recorded golden still.
            static EntityWorld Run(StatusFlags s)
            {
                var w = new EntityWorld();
                var move = new MovementSystem();
                int u = Mover(w, V(-10, 0), V(10, 0));
                w.StatusFlagsOf[u] = s;
                for (int t = 0; t < 120; t++) move.Tick(w, Dt);
                return w;
            }
            EntityWorld a = Run(StatusFlags.None), b = Run(StatusFlags.Invulnerable | StatusFlags.Silenced);
            Assert.Equal(a.Position[0].X.Raw, b.Position[0].X.Raw); // neither flag is move-blocking
            Assert.Equal(a.Position[0].Z.Raw, b.Position[0].Z.Raw);
            Assert.True(a.Position[0].X > Fixed.Zero);
        }

        // ── Disarmed → CombatSystem's damage choke points ─────────────────────────────────────────────

        [Fact]
        public void DisarmedAttacker_LandsNoHit_ThenStrikesTheTickTheDebuffClears()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int attacker = Attacker(w, V(0, 0));
            int target   = Victim(w, V(10, 0));
            w.StatusFlagsOf[attacker] = StatusFlags.Disarmed;

            Fixed hp0 = w.Health[target];
            for (int t = 0; t < 30; t++) combat.Tick(w, Dt);

            Assert.Equal(hp0.Raw, w.Health[target].Raw);                      // 30 ticks of swings, zero damage
            Assert.True((w.Flags[attacker] & EntityFlags.Attacking) == 0);    // presentation stops the swing
            Assert.Equal(Fixed.Zero.Raw, w.AttackCooldown[attacker].Raw);     // refused BEFORE the timer is burned

            w.StatusFlagsOf[attacker] = StatusFlags.None;
            combat.Tick(w, Dt);
            Assert.True(w.Health[target] < hp0, "clearing the disarm must let the very next tick land a hit");
        }

        [Fact]
        public void DisarmedProjectileAttacker_SpawnsNoProjectile()
        {
            var w = new EntityWorld();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles);
            int attacker = Attacker(w, V(0, 0), delivery: AttackDelivery.Projectile);
            w.ProjectileSpeed[attacker] = Fixed.FromInt(18);
            Victim(w, V(10, 0));
            w.StatusFlagsOf[attacker] = StatusFlags.Disarmed;

            for (int t = 0; t < 30; t++) combat.Tick(w, Dt);
            Assert.Equal(0, projectiles.HighWaterMark);   // the disarm gate sits ABOVE the spawn

            w.StatusFlagsOf[attacker] = StatusFlags.None;
            combat.Tick(w, Dt);
            Assert.True(projectiles.HighWaterMark > 0, "clearing the disarm must let the shell fly");
        }

        [Fact]
        public void DisarmedAttacker_CannotDamageABuilding()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int siege = Attacker(w, V(1, 0), dmg: 50, range: 2);
            w.DamageTypeOf[siege] = DamageType.Siege;
            OrderApplier.Apply(w, new UnitOrder(siege, UnitCommand.AttackBuilding, Fixed.FromRaw(b), Fixed.Zero),
                               Faction.Player1);
            w.StatusFlagsOf[siege] = StatusFlags.Disarmed;

            Fixed hp0 = buildings.Health[b];
            for (int t = 0; t < 30; t++) combat.Tick(w, Dt);
            Assert.Equal(hp0.Raw, buildings.Health[b].Raw);
            Assert.True(buildings.Alive[b]);

            w.StatusFlagsOf[siege] = StatusFlags.None;
            combat.Tick(w, Dt);
            Assert.True(buildings.Health[b] < hp0, "clearing the disarm must let the raze resume");
        }

        // ── Stunned → the whole-unit CombatSystem gate ────────────────────────────────────────────────

        [Fact]
        public void StunnedAttacker_TakesNoCombatActionAtAll()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int attacker = Attacker(w, V(0, 0));
            int target   = Victim(w, V(10, 0));
            w.AttackSpeed[attacker]    = Fixed.FromInt(1);
            w.AttackCooldown[attacker] = Fixed.FromInt(1);   // a live cooldown, so "did it tick?" is observable
            w.AttackTarget[attacker]   = target;             // and a live target, so "was it dropped?" is observable
            w.Flags[attacker]         |= EntityFlags.Attacking;
            w.StatusFlagsOf[attacker]  = StatusFlags.Stunned;

            Fixed hp0 = w.Health[target];
            for (int t = 0; t < 30; t++) combat.Tick(w, Dt);

            Assert.Equal(hp0.Raw, w.Health[target].Raw);                                 // no damage
            Assert.Equal(Fixed.FromInt(1).Raw, w.AttackCooldown[attacker].Raw);          // the cooldown never ticked
            Assert.Equal(-1, w.AttackTarget[attacker]);                                  // target dropped
            Assert.True((w.Flags[attacker] & EntityFlags.Attacking) == 0);               // and the swing stopped
        }

        // ── Silenced / Stunned → the atomic AbilityCastSystem refusal ─────────────────────────────────

        private sealed class SilenceHarness
        {
            public readonly EntityWorld World = new EntityWorld();
            public readonly CombatEventQueue Events = new CombatEventQueue();
            public readonly AbilityRegistry Registry;
            public readonly AbilityCastSystem Cast;

            public SilenceHarness()
            {
                var resources = new ResourceStore(Fixed.Zero);
                var modSys    = new ModifierSystem();
                var modifiers = new ModifierStore(World, modSys);
                modSys.AttachStore(modifiers);
                Registry = new AbilityRegistry(new[] { AbilityTestAbilities.MinorHeal() }); // Self, 20 energy, 3s cd, Heal 40
                Cast     = new AbilityCastSystem(Registry, resources, modifiers, null, Events);
            }

            public int Caster()
            {
                int id = World.Create(default, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                World.AbilityId[id * EntityWorld.MAX_ABILITIES_PER_UNIT] = Registry.IndexOf("minor_heal");
                World.AbilityCount[id] = 1;
                World.MaxEnergy[id]    = Fixed.FromInt(100);
                World.Energy[id]       = Fixed.FromInt(100);
                World.Health[id]       = Fixed.FromInt(10);   // wounded ⇒ a landed heal is observable
                return id;
            }

            public void IssueAndTick(int caster)
            {
                OrderApplier.Apply(World, new UnitOrder(caster, UnitCommand.CastAbility,
                    Fixed.FromRaw(0), Fixed.FromRaw(caster)), Faction.Player1);
                Cast.Tick(World, Dt);
            }
        }

        [Theory]
        [InlineData((byte)StatusFlags.Silenced, (int)DenialReason.Silenced)]
        [InlineData((byte)StatusFlags.Stunned,  (int)DenialReason.Stunned)]
        public void CastBlockingStatus_RefusesAtomically_WithTheMatchingDenialReason(byte statusRaw, int expectedReason)
        {
            var h = new SilenceHarness();
            int caster = h.Caster();
            h.World.StatusFlagsOf[caster] = (StatusFlags)statusRaw;

            h.IssueAndTick(caster);

            // Atomic refusal: nothing debited, no cooldown started, no effect graph run.
            Assert.Equal(Fixed.FromInt(10).Raw, h.World.Health[caster].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Energy[caster].Raw);
            Assert.Equal(0, h.World.AbilityCooldownTicks[caster * EntityWorld.MAX_ABILITIES_PER_UNIT]);
            // …and the player is told WHY (single-truth reason authored at the guard).
            Assert.Equal(1, h.Events.Count);
            Assert.Equal(CombatEventType.OrderDenied, h.Events.Get(0).Type);
            Assert.Equal((DenialReason)expectedReason, h.Events.Get(0).Reason);

            // PAUSE, NOT CANCEL: with the status cleared the identical order fires normally.
            h.World.StatusFlagsOf[caster] = StatusFlags.None;
            h.IssueAndTick(caster);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[caster].Raw);   // 10 + Heal 40
            Assert.Equal(Fixed.FromInt(80).Raw, h.World.Energy[caster].Raw);   // 100 − 20
            Assert.True(h.World.AbilityCooldownTicks[caster * EntityWorld.MAX_ABILITIES_PER_UNIT] > 0);
        }

        // ── Invulnerable → DamageResolver.Apply (the SINGLE entity-damage path) ───────────────────────

        [Fact]
        public void InvulnerableTarget_TakesNoDamageFromAnySource_AndCannotBeKilled()
        {
            var w = new EntityWorld();
            int t = w.Create(V(0, 0), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.ArmorTypeOf[t] = ArmorType.Unarmored;
            w.StatusFlagsOf[t] = StatusFlags.Invulnerable;

            var ctx = new DamageContext(w, t, ArmorType.Unarmored, Faction.Player1, DamageTable.Default, null, null);
            Assert.False(DamageResolver.Apply(in ctx, Fixed.FromInt(9999), DamageType.Normal)); // "did not die"
            Assert.Equal(Fixed.FromInt(50).Raw, w.Health[t].Raw);
            Assert.True(w.IsAlive(t));

            w.StatusFlagsOf[t] = StatusFlags.None;
            Assert.True(DamageResolver.Apply(in ctx, Fixed.FromInt(9999), DamageType.Normal));  // the same blow is lethal
            Assert.False(w.IsAlive(t));
        }

        [Fact]
        public void InvulnerableTarget_SurvivesAFullMeleeBeating_ThenDiesOnceItDrops()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int attacker = Attacker(w, V(0, 0), dmg: 50, range: 2);
            int target   = Victim(w, V(1, 0), health: 100);
            w.StatusFlagsOf[target] = StatusFlags.Invulnerable;

            for (int t = 0; t < 60; t++) combat.Tick(w, Dt);
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[target].Raw);
            Assert.True(w.IsAlive(target));

            w.StatusFlagsOf[target] = StatusFlags.None;
            for (int t = 0; t < 60 && w.IsAlive(target); t++) combat.Tick(w, Dt);
            Assert.False(w.IsAlive(target));
        }

        // ── End-to-end: an AUTHORED status modifier, installed and expired by the real store ──────────

        [Fact]
        public void AuthoredStunModifier_AnchorsTheUnitWhileLive_AndReleasesItOnExpiry()
        {
            // The real path an authored ability takes: ApplyModifier(status: Stunned) → ModifierStore writes
            // StatusFlagsOf → MovementSystem honours it → the modifier expires → the unit walks again. No test
            // shortcut writes StatusFlagsOf here.
            var w = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(w, modSys);
            modSys.AttachStore(modifiers);
            var move = new MovementSystem();

            int u = Mover(w, V(0, 0), V(20, 0));
            Assert.True(modifiers.Apply(u, new Modifier(7001, 3, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero,
                                                        Fixed.Zero, StatusFlags.Stunned, null, 0),
                                        casterId: u, casterFaction: Faction.Player1));
            Assert.Equal(StatusFlags.Stunned, w.StatusFlagsOf[u]);

            FixedVec3 start = w.Position[u];
            for (int t = 0; t < 3; t++) { move.Tick(w, Dt); modSys.Tick(w, Dt); }
            Assert.Equal(start.X.Raw, w.Position[u].X.Raw);   // held for the whole authored duration

            // Drain the remaining duration, then confirm the store cleared the flag and the unit resumes.
            for (int t = 0; t < 5; t++) modSys.Tick(w, Dt);
            Assert.Equal(StatusFlags.None, w.StatusFlagsOf[u]);
            for (int t = 0; t < 60; t++) move.Tick(w, Dt);
            Assert.True(w.Position[u].X > Fixed.FromInt(5), "an expired stun must release the unit");
        }
    }
}
