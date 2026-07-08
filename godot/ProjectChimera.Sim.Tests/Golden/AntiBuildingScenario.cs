using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC2 / AC2.6 / AC3.4) — the ANTI-BUILDING golden scenario. Two Player1 Siege units are ordered
    /// (via <c>CommandState = AttackBuilding</c>, the golden-authoring convention the other goldens use for command
    /// state) onto ONE enemy building: a MELEE sieger already in range (instant Fortified matrix damage) and a RANGED
    /// sieger that chases to range and fires REAL projectiles (the D-4 path). Over the run the checksum captures the
    /// building's Health dropping, its Destroy (Alive→false — both folded), the ranged unit's chase, and the
    /// projectiles in flight, then the attackers reverting to Idle — pinning the full raze sequence deterministically.
    ///
    /// CROSS-PLATFORM SAFE: the building is NEUTRAL (a valid explicit target per AC2.5) so Player2 stays EMPTY and the
    /// float-scoring AI no-ops; every hashed field (buildings.Health/Alive + unit Position/Health) is integer/Fixed;
    /// projectile motion is Fixed. NOT Windows-gated — compared on both CI legs.
    /// </summary>
    public static class AntiBuildingScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples (long enough for the shell to land + the raze).</summary>
        public const int DefaultTicks = 300;

        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (P2 EMPTY → AI no-ops → cross-platform safe)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // The enemy building — NEUTRAL so the P2 AI has nothing to act on (cross-platform safe). Complete it
            // immediately so the golden pins the RAZE, not the construction countdown.
            int b = host.Buildings.Create(V(0, 0, 0), Faction.Neutral, BuildingType.Barracks);
            host.Buildings.ConstructionTimer[b] = Fixed.Zero;

            // id 0 — a MELEE Siege unit already in range → instant Fortified matrix damage each cooldown.
            int melee = Combatant(w, V(2, 0, 0), Faction.Player1, DamageType.Siege, dmg: 40, range: 2);
            w.CommandState[melee]  = UnitCommand.AttackBuilding;
            w.CommandTarget[melee] = b;

            // id 1 — a RANGED Siege unit out of range → chases to range, then fires real projectiles (D-4 path).
            int ranged = Combatant(w, V(-8, 0, 0), Faction.Player1, DamageType.Siege, dmg: 40, range: 6);
            w.CommandState[ranged]  = UnitCommand.AttackBuilding;
            w.CommandTarget[ranged] = b;

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, melee);
        }

        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f, DamageType dtype, int dmg, int range)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(range);
            // Story 3.12: mirror the old range→delivery inference (direct-SoA units skip ApplyUnitDefinition) so the
            // ranged siege unit still fires projectiles at the building (Create default is now Hitscan).
            w.Delivery[id] = w.AttackRange[id] > Fixed.FromFloat(2.5f) ? AttackDelivery.Projectile : AttackDelivery.Hitscan;
            w.AttackSpeed[id]  = Fixed.FromInt(1);
            w.DamageTypeOf[id] = dtype;
            return id;
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
