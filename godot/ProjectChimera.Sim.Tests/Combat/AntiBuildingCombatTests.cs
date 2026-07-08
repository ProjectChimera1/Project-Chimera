#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 2.9a (AC2 / AC2.6 / AC4) — explicit-order anti-building combat: the DamageMatrix path against
    /// <see cref="BuildingStore"/>, the in-tick friendly / Structure-domain / bounds guards, MP-parity through the
    /// shared <see cref="OrderApplier"/>, and the ranged-vs-building projectile (D-4). Godot-free, <see cref="Fixed"/>-only,
    /// ascending-id — runs on every OS leg. Combat damage uses <see cref="DamageTable.Default"/> (the same instance
    /// <see cref="CombatSystem"/> resolves when passed no table), so the expected-damage assertions are exact.
    /// </summary>
    public class AntiBuildingCombatTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A combat attacker. Melee if <paramref name="range"/> ≤ 2.5, else ranged. AttackSpeed 0 ⇒ fires every tick.</summary>
        private static int Attacker(EntityWorld w, FixedVec3 pos, Faction f, DamageType dtype, int dmg, int range)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(range);
            // Story 3.12: mirror the old range→delivery inference (direct-SoA units skip ApplyUnitDefinition) so a
            // ranged (> 2.5) attacker still spawns a projectile instead of the new Hitscan Create default.
            w.Delivery[id] = w.AttackRange[id] > Fixed.FromFloat(2.5f) ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
            w.AttackSpeed[id]  = Fixed.Zero;      // deterministic: cooldown resets to 0 → fires each tick
            w.DamageTypeOf[id] = dtype;
            return id;                            // AttackDomainOf defaults to All (includes Structure)
        }

        private static void OrderAttackBuilding(EntityWorld w, int attacker, int buildingIdRaw)
            => OrderApplier.Apply(w, new UnitOrder(attacker, UnitCommand.AttackBuilding,
                                                   Fixed.FromRaw(buildingIdRaw), Fixed.Zero), w.FactionOf[attacker]);

        private static Fixed ExpectedHit(int dmg, DamageType type)
            => DamageTable.Default.FinalDamage(Fixed.FromInt(dmg), type, ArmorType.Fortified, Fixed.Zero);

        // ── AC2 / AC2.2 — the matrix path: Siege razes (1.5×), Pierce dents (0.25×) ──

        [Fact]
        public void OrderedSiegeMelee_DamagesAndRazesFortifiedBuilding_ViaMatrix()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int siege = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, dmg: 50, range: 2); // melee (≤ 2.5)

            OrderAttackBuilding(w, siege, b);
            Fixed hp0 = buildings.Health[b];

            combat.Tick(w, Dt); // one hit lands (cooldown starts at 0)
            Fixed hit = ExpectedHit(50, DamageType.Siege);
            Assert.Equal((hp0 - hit).Raw, buildings.Health[b].Raw);
            Assert.True(hit > Fixed.FromInt(50), "Siege vs Fortified must be > 1× (the 1.5× multiplier)"); // matrix teeth

            for (int t = 0; t < 30 && buildings.Alive[b]; t++) combat.Tick(w, Dt);
            Assert.False(buildings.Alive[b]);                 // razed at Health ≤ 0
            combat.Tick(w, Dt);                               // next tick: the in-tick guard reverts the attacker
            Assert.Equal(UnitCommand.Idle, w.CommandState[siege]);
        }

        [Fact]
        public void OrderedPierce_BarelyDentsFortified_VersusSiege()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
            int pierce = Attacker(w, V(1, 0), Faction.Player1, DamageType.Pierce, dmg: 50, range: 2);

            OrderAttackBuilding(w, pierce, b);
            Fixed hp0 = buildings.Health[b];
            combat.Tick(w, Dt);

            Fixed pierceHit = ExpectedHit(50, DamageType.Pierce); // 50 × 0.25 = 12.5
            Fixed siegeHit  = ExpectedHit(50, DamageType.Siege);  // 50 × 1.50 = 75
            Assert.Equal((hp0 - pierceHit).Raw, buildings.Health[b].Raw);
            Assert.True(pierceHit < siegeHit, "Pierce must dent a Fortified building far less than Siege razes it");
        }

        // ── AC2 — a friendly building is NEVER targeted (in-tick guard, before any damage) ──

        [Fact]
        public void OrderedOnFriendlyBuilding_NoDamage_RevertsToIdleInTick()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player1, BuildingType.Barracks); // OWN faction
            int attacker = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2);

            OrderAttackBuilding(w, attacker, b);
            Fixed hp0 = buildings.Health[b];
            combat.Tick(w, Dt);

            Assert.Equal(hp0.Raw, buildings.Health[b].Raw);        // untouched
            Assert.Equal(UnitCommand.Idle, w.CommandState[attacker]);
            Assert.Equal(-1, w.CommandTarget[attacker]);
        }

        // ── AC2.4 — a unit whose attack_domains exclude Structure cannot damage a building when ordered ──

        [Fact]
        public void OrderedByAntiAirUnit_NoBuildingDamage_RevertsToIdleInTick()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int aa = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2);
            w.AttackDomainOf[aa] = AttackDomain.Air;               // excludes Structure

            OrderAttackBuilding(w, aa, b);
            Fixed hp0 = buildings.Health[b];
            combat.Tick(w, Dt);

            Assert.Equal(hp0.Raw, buildings.Health[b].Raw);        // no attack spent
            Assert.Equal(UnitCommand.Idle, w.CommandState[aa]);
            // Teeth: an All-domain attacker in the identical setup DOES damage it.
            var w2 = new EntityWorld();
            var b2store = new BuildingStore();
            var combat2 = new CombatSystem(new ProjectileStore(), buildings: b2store);
            int b2 = b2store.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int all = Attacker(w2, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2); // default All
            OrderAttackBuilding(w2, all, b2);
            combat2.Tick(w2, Dt);
            Assert.True(b2store.Health[b2].Raw < b2store.MaxHealth[b2].Raw, "an All-domain unit must damage the building");
        }

        // ── AC2.5 — a Neutral building IS a valid explicit target ──

        [Fact]
        public void OrderedOnNeutralBuilding_IsDamaged()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Neutral, BuildingType.Barracks);
            int siege = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2);

            OrderAttackBuilding(w, siege, b);
            Fixed hp0 = buildings.Health[b];
            combat.Tick(w, Dt);
            Assert.True(buildings.Health[b].Raw < hp0.Raw, "a Neutral building is hostile to explicit orders (AC2.5)");
        }

        // ── AC4 / crash-safety — a bad building id (friendly / -1 / OOB) reverts to Idle WITHOUT throwing ──

        [Theory]
        [InlineData(-1)]   // the -1 sentinel
        [InlineData(999)]  // ≥ Count (OOB) — BuildingStore has no IsAlive short-circuit, so the bounds guard is load-bearing
        public void OrderedOnInvalidBuildingId_NoThrow_RevertsToIdle(int badId)
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks); // a real building at id 0
            int attacker = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2);

            OrderAttackBuilding(w, attacker, badId);
            combat.Tick(w, Dt); // must NOT throw

            Assert.Equal(UnitCommand.Idle, w.CommandState[attacker]);
            Assert.Equal(-1, w.CommandTarget[attacker]);
        }

        // ── AC4 — MP parity through the shared OrderApplier (offline path, NO buildings arg) ──

        [Fact]
        public void AttackBuilding_TwoRunsThroughOrderApplier_AreByteIdentical()
        {
            static Fixed Run()
            {
                var w = new EntityWorld();
                var buildings = new BuildingStore();
                var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
                int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
                int siege = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 40, 2);
                OrderAttackBuilding(w, siege, b); // offline OrderApplier.Apply — no buildings arg, identical to AttackTarget
                for (int t = 0; t < 3; t++) combat.Tick(w, Dt);
                return buildings.Health[b];
            }
            Assert.Equal(Run().Raw, Run().Raw);
        }

        [Fact]
        public void AttackBuilding_WrongFactionAttacker_RejectedAtApply_NothingStored()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int attacker = Attacker(w, V(1, 0), Faction.Player1, DamageType.Siege, 50, 2);

            // expectedFaction = Player2, but the attacker is Player1 → the ownership guard rejects before the switch.
            OrderApplier.Apply(w, new UnitOrder(attacker, UnitCommand.AttackBuilding, Fixed.FromRaw(b), Fixed.Zero),
                               Faction.Player2);

            Assert.NotEqual(UnitCommand.AttackBuilding, w.CommandState[attacker]); // nothing stored
            Assert.Equal(-1, w.CommandTarget[attacker]);
        }

        // ── AC2.6 / D-4 — a ranged attacker fires a real projectile that lands and applies Fortified damage ──

        [Fact]
        public void RangedAttacker_SpawnsProjectile_ThatDamagesBuildingOnImpact()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles, buildings: buildings);
            var projSys = new ProjectileSystem(projectiles, buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
            int ranged = Attacker(w, V(5, 0), Faction.Player1, DamageType.Siege, 40, range: 6); // ranged (> 2.5)

            OrderAttackBuilding(w, ranged, b);
            Fixed hp0 = buildings.Health[b];

            combat.Tick(w, Dt);                                    // in range → spawn a shell; no INSTANT damage
            Assert.Equal(hp0.Raw, buildings.Health[b].Raw);
            Assert.True(projectiles.HighWaterMark > 0, "a ranged attacker must spawn a projectile at the building");

            bool damaged = false;
            for (int t = 0; t < 30 && !damaged; t++)
            {
                projSys.Tick(w, Dt);                               // advance the shell; it resolves on arrival
                damaged = buildings.Health[b].Raw < hp0.Raw;
            }
            Assert.True(damaged, "the ranged shell must apply Fortified matrix damage to the building on impact");
        }

        // ── Story 3.12 — the BUILDING projectile branch honours the attacker's per-unit ProjectileSpeed (not the
        //    global fallback). Direct oracle: without this, reverting TryDealBuildingDamage to pass the constant
        //    PROJECTILE_SPEED would leave every building test green (they all fire at the default 18) and the delivery
        //    golden has no buildings — this pins the building branch's speed argument. ──
        [Fact]
        public void RangedAttacker_VsBuilding_SpawnsShellAtPerUnitSpeed_NotTheGlobalDefault()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles, buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
            int ranged = Attacker(w, V(5, 0), Faction.Player1, DamageType.Siege, 40, range: 6); // ranged → Projectile
            w.ProjectileSpeed[ranged] = Fixed.FromInt(6);          // authored per-unit speed (≠ the 18 fallback)

            OrderAttackBuilding(w, ranged, b);
            combat.Tick(w, Dt);                                    // in range → spawn a building-target shell

            Assert.True(projectiles.HighWaterMark > 0, "a ranged attacker must spawn a projectile at the building");
            Assert.Equal(Fixed.FromInt(6).Raw, projectiles.Speed[0].Raw); // the building branch carried the per-unit speed, not 18
            Assert.NotEqual(ProjectileSystem.PROJECTILE_SPEED.Raw, projectiles.Speed[0].Raw);
        }

        [Fact]
        public void BuildingDiesWhileShellInFlight_ProjectileDropsHarmlessly_NoThrow()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var projectiles = new ProjectileStore();
            var combat = new CombatSystem(projectiles, buildings: buildings);
            var projSys = new ProjectileSystem(projectiles, buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.CommandCenter);
            int ranged = Attacker(w, V(5, 0), Faction.Player1, DamageType.Siege, 40, range: 6);

            OrderAttackBuilding(w, ranged, b);
            combat.Tick(w, Dt);          // spawn a shell in flight
            buildings.Destroy(b);        // building razed by something else before the shell lands

            for (int t = 0; t < 30; t++) projSys.Tick(w, Dt); // must NOT throw (bounds+Alive guard drops the shell)
            Assert.False(buildings.Alive[b]);
        }

        // ── Task 6 — a gathering worker cannot linger in AttackBuilding (gatherer-normalize) ──

        [Fact]
        public void GatheringWorker_InAttackBuilding_IsNormalizedToIdle()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int worker = w.Create(V(1, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.GatherState[worker]  = GatherState.Gathering;    // an active gatherer
            w.CommandState[worker] = UnitCommand.AttackBuilding;
            w.CommandTarget[worker] = b;

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[worker]); // normalized, not frozen
        }
    }
}
