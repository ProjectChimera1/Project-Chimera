#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // FactionDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 9.14 — combat consults the <see cref="AllianceStore"/> mask: an ALLIED faction is excluded from
    /// nearest-enemy acquisition (units + buildings), from force-fire, and from projectile splash — while NEUTRAL
    /// stays targetable (force-fire onto Neutral is still allowed, Story 1.12). Godot-free, <see cref="Fixed"/>-only.
    /// The null-mask / FFA byte-identity is covered by the existing combat goldens (this feature is a no-op there).
    /// </summary>
    public class AlliedCombatExclusionTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>P1 allied with P2 (shared team id 1); P3/P4 are their own teams (enemies).</summary>
        private static AllianceStore P1P2Allied()
        {
            var a = new AllianceStore();
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            return a;
        }

        private static int Attacker(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(20);
            w.AttackRange[id]  = Fixed.FromInt(10);
            w.AttackSpeed[id]  = Fixed.Zero;               // fires every tick
            w.Delivery[id]     = AttackDelivery.Hitscan;   // instant damage this tick
            w.DamageTypeOf[id] = DamageType.Normal;
            w.CommandState[id] = UnitCommand.Idle;
            return id;
        }

        private static int Victim(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ArmorTypeOf[id] = ArmorType.Unarmored;
            return id;
        }

        // ── Unit auto-acquire: an allied unit is skipped; the farther enemy is chosen instead ──

        [Fact]
        public void AlliedUnit_ExcludedFromAcquisition_FartherEnemyAttackedInstead()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk  = Attacker(w, V(0, 0), Faction.Player1);
            int ally = Victim(w, V(2, 0), Faction.Player2);  // NEARER, but allied → must be skipped
            int foe  = Victim(w, V(4, 0), Faction.Player3);  // farther enemy → the real target

            Fixed allyHp0 = w.Health[ally];
            combat.Tick(w, Dt);

            Assert.Equal(allyHp0, w.Health[ally]);           // ally never hit
            Assert.True(w.Health[foe] < Fixed.FromInt(100)); // enemy took damage
            Assert.Equal(foe, w.AttackTarget[atk]);
        }

        [Fact]
        public void AlliedUnit_OnlyCandidate_NoTargetAcquired()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk  = Attacker(w, V(0, 0), Faction.Player1);
            int ally = Victim(w, V(2, 0), Faction.Player2);

            Fixed allyHp0 = w.Health[ally];
            combat.Tick(w, Dt);

            Assert.Equal(allyHp0, w.Health[ally]);
            Assert.Equal(-1, w.AttackTarget[atk]);
            Assert.Equal((EntityFlags)0, w.Flags[atk] & EntityFlags.Attacking);
        }

        // ── Building auto-acquire: an allied building is skipped; an enemy building is acquired ──

        [Fact]
        public void AlliedBuilding_NotAutoAcquired_EnemyBuildingIs()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings, alliances: P1P2Allied());
            int atk = Attacker(w, V(0, 0), Faction.Player1); // AttackDomain defaults to All (incl. Structure)

            int allyB = buildings.Create(V(3, 0), Faction.Player2, BuildingType.Barracks); // allied — skip
            Fixed allyHp0 = buildings.Health[allyB];

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[atk]);   // did NOT enter AttackBuilding on the ally
            Assert.Equal(allyHp0, buildings.Health[allyB]);

            // Add an enemy building in range → it IS auto-acquired.
            buildings.Create(V(4, 0), Faction.Player3, BuildingType.Barracks);
            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[atk]);
        }

        // ── Explicit AttackBuilding force-order: rejected onto an ally, still allowed onto Neutral ──

        [Fact]
        public void ForceAttackBuilding_OntoAlliedBuilding_Rejected_RevertsToIdle_NoDamage()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings, alliances: P1P2Allied());
            int atk   = Attacker(w, V(0, 0), Faction.Player1);
            int allyB = buildings.Create(V(2, 0), Faction.Player2, BuildingType.Barracks); // allied — in range

            w.CommandState[atk]  = UnitCommand.AttackBuilding;
            w.CommandTarget[atk] = buildings.PackRef(allyB); // force-attack the allied building (packed ref)

            Fixed hp0 = buildings.Health[allyB];
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[atk]); // the in-tick guard rejects the allied force-order
            Assert.Equal(hp0, buildings.Health[allyB]);          // allied building undamaged
        }

        [Fact]
        public void ForceAttackBuilding_OntoNeutralBuilding_StillAllowed()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings, alliances: P1P2Allied());
            int atk      = Attacker(w, V(0, 0), Faction.Player1);
            int neutralB = buildings.Create(V(2, 0), Faction.Neutral, BuildingType.Barracks); // never allied → targetable

            w.CommandState[atk]  = UnitCommand.AttackBuilding;
            w.CommandTarget[atk] = buildings.PackRef(neutralB);

            Fixed hp0 = buildings.Health[neutralB];
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.AttackBuilding, w.CommandState[atk]); // NOT rejected — stays on the forced order
            Assert.True(buildings.Health[neutralB] < hp0);                 // Neutral building took the hit
        }

        // ── Force-fire: rejected onto an ally, still allowed onto Neutral (Story 1.12 force-fire preserved) ──

        [Fact]
        public void ForceFire_OntoAlly_Rejected_RevertsToIdle_NoDamage()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk  = Attacker(w, V(0, 0), Faction.Player1);
            int ally = Victim(w, V(2, 0), Faction.Player2);

            w.CommandState[atk]  = UnitCommand.AttackTarget;
            w.CommandTarget[atk] = ally;                     // force-fire the ally

            Fixed allyHp0 = w.Health[ally];
            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.Idle, w.CommandState[atk]); // allied force-fire refused
            Assert.Equal(allyHp0, w.Health[ally]);               // ally undamaged
        }

        [Fact]
        public void ForceFire_OntoNeutral_StillAllowed()
        {
            var w = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore(), alliances: P1P2Allied());
            int atk     = Attacker(w, V(0, 0), Faction.Player1);
            int neutral = Victim(w, V(2, 0), Faction.Neutral); // AreAllied(P1,Neutral)==false → force-fireable

            w.CommandState[atk]  = UnitCommand.AttackTarget;
            w.CommandTarget[atk] = neutral;

            combat.Tick(w, Dt);

            Assert.Equal(UnitCommand.AttackTarget, w.CommandState[atk]); // stayed on the forced target
            Assert.True(w.Health[neutral] < Fixed.FromInt(100));         // Neutral took the hit
        }

        // ── Projectile splash: allied factions excluded; Neutral + enemies still splashed ──

        [Fact]
        public void Splash_SkipsAlly_ButDamagesEnemyAndNeutral()
        {
            var w = new EntityWorld();
            var store = new ProjectileStore();
            var system = new ProjectileSystem(store, alliances: P1P2Allied());

            int primary = Victim(w, V(0, 0), Faction.Player3); // enemy — the primary target
            int ally    = Victim(w, V(1, 0), Faction.Player2); // allied to owner P1 → no splash
            int enemy   = Victim(w, V(1, 1), Faction.Player4); // enemy → splashed
            int neutral = Victim(w, V(0, 1), Faction.Neutral); // never allied → splashed

            Fixed allyHp0 = w.Health[ally];
            // Spawn already at the target position so it hits THIS tick; splash radius covers all four.
            store.Spawn(V(0, 0), primary, V(0, 0), Fixed.FromInt(20), DamageType.Normal, ArmorType.Unarmored,
                        Faction.Player1, speed: Fixed.FromInt(18), splashRadius: Fixed.FromInt(5));

            system.Tick(w, Dt);

            Assert.Equal(allyHp0, w.Health[ally]);                  // ally excluded from splash
            Assert.True(w.Health[enemy]   < Fixed.FromInt(100));    // enemy splashed
            Assert.True(w.Health[neutral] < Fixed.FromInt(100));    // Neutral splashed
            Assert.True(w.Health[primary] < Fixed.FromInt(100));    // primary hit
        }

        // ── SimulationHost wiring: the LIVE host pipeline must carry host.Alliances into its CombatSystem (not just a
        //    hand-built CombatSystem). Drives a full host.StepOnce() so a dropped Alliances arg on the host's
        //    CombatSystem construction would let the attacker acquire+hit its nearer ALLIED unit. ──

        [Fact]
        public void SimulationHost_WiresAllianceIntoLiveCombat_AllyExcludedFromAcquisition()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(4),
                                             new FactionDefinition(), new FactionDefinition());
            // Seed a 2v2 mask through the seeder: {P1,P2} allied, {P3,P4} the other team.
            AllianceSeeder.Seed(host.Alliances, new ScenarioData
            {
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 1, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 2, Team = 2 },
                    new ScenarioPlayerSlot { Slot = 3, Team = 2 },
                },
            });
            Assert.True(host.Alliances.AreAllied(Faction.Player1, Faction.Player2));

            int atk  = Attacker(host.World, V(0, 0), Faction.Player1);
            int ally = Victim(host.World, V(2, 0), Faction.Player2);  // NEARER, allied → must be skipped by the live pipeline
            int foe  = Victim(host.World, V(4, 0), Faction.Player3);  // farther enemy → the real target

            Fixed allyHp0 = host.World.Health[ally];
            host.StepOnce(); // the full host system pipeline (the wired CombatSystem, not a hand-built one)

            Assert.Equal(allyHp0, host.World.Health[ally]);            // ally never hit → host DID thread Alliances into combat
            Assert.True(host.World.Health[foe] < Fixed.FromInt(100));  // the real enemy took the hit
            Assert.Equal(foe, host.World.AttackTarget[atk]);
        }

        // ── SimulationHost wiring: the LIVE host.Projectiles pipeline must carry host.Alliances into its ProjectileSystem
        //    (not just a hand-built ProjectileSystem). Drives a full host.StepOnce() so a dropped Alliances arg on the
        //    host's ProjectileSystem construction would let live AoE splash the ALLIED unit. Mirrors the combat-wiring
        //    guard above, but for the splash path the follow-up review found unguarded. ──

        [Fact]
        public void SimulationHost_WiresAllianceIntoLiveProjectileSplash_AllyExcludedFromSplash()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(4),
                                             new FactionDefinition(), new FactionDefinition());
            // Seed a 2v2 mask through the seeder: {P1,P2} allied, {P3,P4} the other team.
            AllianceSeeder.Seed(host.Alliances, new ScenarioData
            {
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 1, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 2, Team = 2 },
                    new ScenarioPlayerSlot { Slot = 3, Team = 2 },
                },
            });
            Assert.True(host.Alliances.AreAllied(Faction.Player1, Faction.Player2));

            var w = host.World;
            int primary = Victim(w, V(0, 0), Faction.Player3); // enemy — the primary target
            int ally    = Victim(w, V(1, 0), Faction.Player2); // allied to owner P1 → must NOT be splashed by the live pipeline
            int enemy   = Victim(w, V(1, 1), Faction.Player4); // enemy → splashed
            int neutral = Victim(w, V(0, 1), Faction.Neutral); // never allied → splashed

            Fixed allyHp0 = w.Health[ally];
            // Spawn already at the target position (owner P1) so it impacts THIS tick; splash radius covers all four.
            host.Projectiles.Spawn(V(0, 0), primary, V(0, 0), Fixed.FromInt(20), DamageType.Normal, ArmorType.Unarmored,
                                   Faction.Player1, speed: Fixed.FromInt(18), splashRadius: Fixed.FromInt(5));

            host.StepOnce(); // the full host system pipeline (the wired ProjectileSystem, not a hand-built one)

            Assert.Equal(allyHp0, w.Health[ally]);               // ally excluded → host DID thread Alliances into the projectile splash
            Assert.True(w.Health[enemy]   < Fixed.FromInt(100)); // enemy splashed
            Assert.True(w.Health[neutral] < Fixed.FromInt(100)); // Neutral splashed
            Assert.True(w.Health[primary] < Fixed.FromInt(100)); // primary hit
        }
    }
}
