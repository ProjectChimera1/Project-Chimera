#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 2.13 (AC3.4, Decision D-3) — the generation-stamped PACKED building reference armors the recycle
    /// stale-index hazard: a cross-tick ref to a since-recycled slot fails <see cref="BuildingStore.TryResolveRef"/>
    /// (generation mismatch) and reverts CLEANLY, never ABA-retargeting the new occupant (restoring Story 2.9a's
    /// "stale → clean revert"). GOLDEN-NEUTRAL at generation 0 (<c>PackRef(slot) == slot</c>). Godot-free, deterministic.
    /// </summary>
    public class BuildingRefPackingTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Attacker(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(50);
            w.AttackRange[id]  = Fixed.FromInt(2);   // melee, in range of a building at distance 1
            w.AttackSpeed[id]  = Fixed.Zero;         // fires every tick
            w.DamageTypeOf[id] = DamageType.Siege;
            return id;                               // AttackDomainOf defaults to All (Structure included)
        }

        // ── Golden-neutrality + round-trip of the pack/resolve primitives ──

        [Fact]
        public void PackRef_AtGenerationZero_EqualsSlot_AndResolvesBack()
        {
            var b = new BuildingStore();
            int id = b.Create(V(0, 0), Faction.Player1, BuildingType.Barracks);
            Assert.Equal(id, b.PackRef(id));                       // gen 0 ⇒ packed == slot (byte-identical folded values)
            Assert.True(b.TryResolveRef(b.PackRef(id), out int slot));
            Assert.Equal(id, slot);
            Assert.False(b.TryResolveRef(-1, out _));              // the -1 sentinel resolves false
        }

        [Fact]
        public void PackRef_AfterRecycle_DiffersFromPriorGeneration()
        {
            var b = new BuildingStore();
            int s0 = b.Create(V(0, 0), Faction.Player1, BuildingType.Barracks); // gen 0
            int packedGen0 = b.PackRef(s0);
            b.Destroy(s0);
            int s1 = b.Create(V(0, 0), Faction.Player1, BuildingType.Barracks); // same slot, gen 1
            Assert.Equal(s0, s1);
            Assert.NotEqual(packedGen0, b.PackRef(s1));            // generation bumped ⇒ the packed ref changed
            Assert.False(b.TryResolveRef(packedGen0, out _));     // the OLD ref no longer resolves
            Assert.True(b.TryResolveRef(b.PackRef(s1), out _));   // the CURRENT ref does
        }

        // ── Strong teeth: a stale ref after the slot recycles into a NEW ENEMY building must NOT retarget it ──

        [Fact]
        public void StalePackedRef_RecycledIntoEnemyBuilding_RevertsCleanly_NeverRetargets()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b0 = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks); // gen 0
            int stalePacked = buildings.PackRef(b0);

            int u = Attacker(w, V(1, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = stalePacked;

            // Slot b0 recycles into a NEW enemy building (generation bumps).
            buildings.Destroy(b0);
            int b1 = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            Assert.Equal(b0, b1); // same slot reused
            Fixed hp0 = buildings.Health[b1];

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);    // stale generation ⇒ clean revert
            Assert.Equal(hp0.Raw, buildings.Health[b1].Raw);      // the NEW enemy building is UNTOUCHED (no ABA retarget)
        }

        // ── A stale ref after recycle into a FRIENDLY building also reverts (deterministic, non-crash) ──

        [Fact]
        public void StalePackedRef_RecycledIntoFriendlyBuilding_RevertsCleanly()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b0 = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            int stalePacked = buildings.PackRef(b0);

            int u = Attacker(w, V(1, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = stalePacked;

            buildings.Destroy(b0);
            int b1 = buildings.Create(V(0, 0), Faction.Player1, BuildingType.Barracks); // recycled into the ATTACKER's own faction
            Fixed hp0 = buildings.Health[b1];

            combat.Tick(w, Dt);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
            Assert.Equal(hp0.Raw, buildings.Health[b1].Raw);      // friendly + stale ⇒ untouched
        }

        // ── The player CAN still target a legitimately-recycled (generation ≥ 1) enemy building ──

        [Fact]
        public void CurrentPackedRef_OnRecycledBuilding_ResolvesAndDamages()
        {
            var w = new EntityWorld();
            var buildings = new BuildingStore();
            var combat = new CombatSystem(new ProjectileStore(), buildings: buildings);
            int b0 = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks);
            buildings.Destroy(b0);
            int b1 = buildings.Create(V(0, 0), Faction.Player2, BuildingType.Barracks); // gen 1

            int u = Attacker(w, V(1, 0), Faction.Player1);
            w.CommandState[u]  = UnitCommand.AttackBuilding;
            w.CommandTarget[u] = buildings.PackRef(b1);           // the CURRENT (generation-1) packed ref
            Fixed hp0 = buildings.Health[b1];

            combat.Tick(w, Dt);
            Assert.True(buildings.Health[b1].Raw < hp0.Raw,
                "a current-generation packed ref must resolve and damage the recycled building");
        }
    }
}
