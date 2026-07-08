#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 2.13 (AC1.1–AC1.4, Decision D-6) — Idle and AttackMove units AUTO-ACQUIRE an in-range enemy building
    /// when no enemy UNIT is in range, via the new deterministic <see cref="Fixed"/>-math building search. Godot-free,
    /// Fixed-only, ascending-id. TEETH: without the search an Idle unit falls through to the unit-only global chase
    /// (no enemy unit ⇒ stands inert) and an AttackMove unit keeps marching — the building is never touched.
    /// </summary>
    public class BuildingAutoAcquireTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A melee combat unit that fires every tick (AttackSpeed 0). AttackDomainOf defaults to All (Structure included).</summary>
        private static int Unit(EntityWorld w, FixedVec3 pos, Faction f, int range = 2)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(30);
            w.AttackRange[id]  = Fixed.FromInt(range);
            // Story 3.12: mirror the old range→delivery inference (direct-SoA units skip ApplyUnitDefinition).
            w.Delivery[id] = w.AttackRange[id] > Fixed.FromFloat(2.5f) ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
            w.AttackSpeed[id]  = Fixed.Zero;
            w.DamageTypeOf[id] = DamageType.Siege;
            return id;
        }

        // ── AC1.2 — Idle auto-acquire: no enemy unit in range, an enemy building in range ──

        [Fact]
        public void IdleUnit_NearEnemyBuilding_NoEnemyUnits_AutoAcquiresAndDamages()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int u = Unit(w, V(1, 0), Faction.Player1);   // building in range (dist 1 ≤ range 2); no enemy units; default Idle
            Fixed hp0 = buildings.Health[b];

            combat.Tick(w, Dt);                          // tick 1: Idle → auto-acquire sets AttackBuilding
            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[u]);
            Assert.Equal(b, w.CommandTarget[u]);
            Assert.Equal(-1, w.AttackTarget[u]);         // building id never leaks into entity-space AttackTarget

            combat.Tick(w, Dt);                          // tick 2: AttackBuilding body deals matrix damage
            Assert.True(buildings.Health[b].Raw < hp0.Raw, "the auto-acquired building must take damage");
        }

        // ── AC1.3 — AttackMove auto-acquire: engages a building instead of marching past it ──

        [Fact]
        public void AttackMoveUnit_NearEnemyBuilding_NoEnemyUnits_AutoAcquires()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int u = Unit(w, V(1, 0), Faction.Player1);
            w.CommandState[u] = UnitCommand.AttackMove;
            w.CommandGoal[u]  = V(-50, 0);               // goal far past the building — would otherwise march on
            w.MoveTarget[u]   = V(-50, 0);
            w.Flags[u] |= EntityFlags.Moving;

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[u]); // acquired instead of marching to the goal
            Assert.Equal(b, w.CommandTarget[u]);
        }

        // ── Negative: an out-of-range building is NOT auto-acquired (range gate has teeth) ──

        [Fact]
        public void IdleUnit_BuildingOutOfRange_DoesNotAcquire()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            buildings.Create(V(10, 0), Faction.Player2, BuildingType.Barracks); // dist 10 > range 2
            int u = Unit(w, V(0, 0), Faction.Player1, range: 2);

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]); // stayed Idle — no in-range building to acquire
        }

        // ── Negative: a friendly building is never auto-acquired ──

        [Fact]
        public void IdleUnit_FriendlyBuildingInRange_DoesNotAcquire()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            buildings.Create(V(0, 0), Faction.Player1, BuildingType.Barracks); // OWN faction
            int u = Unit(w, V(1, 0), Faction.Player1);

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
        }

        // ── AC1.1 — a unit whose attack_domains exclude Structure never auto-acquires a building ──

        [Fact]
        public void AntiAirUnit_InRangeOfEnemyBuilding_DoesNotAutoAcquire()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int u = Unit(w, V(1, 0), Faction.Player1);
            w.AttackDomainOf[u] = AttackDomain.Air;      // excludes Structure

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
        }

        // ── Ascending-id tie-break: two equidistant enemy buildings ⇒ the lower id wins ──

        [Fact]
        public void TwoEquidistantEnemyBuildings_AcquiresLowerId()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b0 = buildings.Create(V(1, 0), Faction.Player2, BuildingType.Barracks);  // id 0, dist 1
            buildings.Create(V(-1, 0), Faction.Player2, BuildingType.Barracks);          // id 1, dist 1 (tie)
            int u = Unit(w, V(0, 0), Faction.Player1, range: 3);

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[u]);
            Assert.Equal(b0, w.CommandTarget[u]);        // ascending-id tie-break picks the lower id
        }
    }
}
