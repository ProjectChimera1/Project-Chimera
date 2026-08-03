#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// DW-184 — the generation-stamped PACKED entity reference (the Story 2.13 AC3.4 BuildingStore/HeroStore pattern,
    /// applied to <see cref="EntityWorld"/>): a cross-tick ref to a since-recycled slot fails
    /// <see cref="EntityWorld.TryResolveRef"/> (generation mismatch) instead of ABA-retargeting the new occupant.
    /// Entity ids previously had NO generation counter, so <c>WinConditionSystem</c>'s assassination target could be
    /// masked by a same-tick same-faction slot recycle (Destroy frees to the LIFO free-list immediately; a same-tick
    /// Create pops it). GOLDEN-NEUTRAL at generation 0 (<c>PackRef(id) == id</c>) and UNFOLDED (never a SimChecksum
    /// input — proven below). Godot-free, deterministic.
    /// </summary>
    public class EntityRefPackingTests
    {
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Spawn(EntityWorld w, Faction f) =>
            w.Create(V(0, 0), f, Fixed.FromInt(10), Fixed.FromInt(3));

        // ── The packing invariant: every id must fit in the slot bits ──

        [Fact]
        public void RefSlotBits_CoverEveryPossibleEntityId()
        {
            // MAX_ENTITIES is exactly 1 << REF_SLOT_BITS (4096 == 1 << 12), so the max id (4095) fills the slot mask
            // and the generation occupies the upper bits. Widening MAX_ENTITIES without widening REF_SLOT_BITS would
            // alias two different ids into one packed slot — this pin forces that change to be conscious.
            Assert.Equal(EntityWorld.MAX_ENTITIES, 1 << EntityWorld.REF_SLOT_BITS);
        }

        // ── Golden-neutrality + round-trip of the pack/resolve primitives ──

        [Fact]
        public void PackRef_AtGenerationZero_EqualsId_AndResolvesBack()
        {
            var w = new EntityWorld();
            int id = Spawn(w, Faction.Player1);
            Assert.Equal(id, w.PackRef(id));                    // gen 0 ⇒ packed == id (golden-neutral)
            Assert.True(w.TryResolveRef(w.PackRef(id), out int resolved));
            Assert.Equal(id, resolved);
            Assert.False(w.TryResolveRef(-1, out _));           // the -1 "unresolved" sentinel resolves false
        }

        [Fact]
        public void TryResolveRef_DeadUnrecycledSlot_Fails()
        {
            var w = new EntityWorld();
            int id = Spawn(w, Faction.Player1);
            int packed = w.PackRef(id);
            w.Destroy(id);                                       // freed but NOT yet recycled
            Assert.False(w.TryResolveRef(packed, out _));        // dead ⇒ the ref no longer resolves
        }

        [Fact]
        public void PackRef_AfterSameFactionRecycle_StaleRefFailsResolve_CurrentRefResolves()
        {
            var w = new EntityWorld();
            int e0 = Spawn(w, Faction.Player1);                  // gen 0
            int packedGen0 = w.PackRef(e0);
            w.Destroy(e0);
            int e1 = Spawn(w, Faction.Player1);                  // LIFO recycle: same slot, SAME faction, gen 1
            Assert.Equal(e0, e1);                                // precondition: the slot really recycled
            Assert.NotEqual(packedGen0, w.PackRef(e1));          // generation bumped ⇒ the packed ref changed
            Assert.False(w.TryResolveRef(packedGen0, out _));   // the OLD ref no longer resolves (the DW-184 mask)
            Assert.True(w.TryResolveRef(w.PackRef(e1), out int resolved)); // the CURRENT ref does
            Assert.Equal(e1, resolved);
        }

        // ── Clear() restores the fresh-ctor generations (the Edit↔Play reset must re-enter the gen-0 regime) ──

        [Fact]
        public void Clear_ResetsGenerations_PackRefEqualsIdAgain()
        {
            var w = new EntityWorld();
            int e0 = Spawn(w, Faction.Player1);
            w.Destroy(e0);
            int e1 = Spawn(w, Faction.Player1);                  // gen 1 on the recycled slot
            Assert.NotEqual(e1, w.PackRef(e1));                  // precondition: the generation is really non-zero

            w.Clear();
            int fresh = Spawn(w, Faction.Player1);               // append on the cleared world
            Assert.Equal(e1, fresh);                             // same slot 0 as before the reset
            Assert.Equal(fresh, w.PackRef(fresh));               // gen back to 0 ⇒ packed == id (fresh-boot parity)
        }

        // ── Deliberate exclusion: Generation is NOT folded into SimChecksum (the BuildingStore.Generation posture:
        //    the recycle sequence is deterministic and the folded alive state covers divergence transitively). A fold
        //    would be a conscious algorithm change (AlgoVersion bump + golden re-baseline) — this tooth forces that. ──

        [Fact]
        public void Generation_IsNotFoldedIntoSimChecksum()
        {
            var registry  = new FactionRegistry(2);
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            int e = Spawn(world, Faction.Player1);               // a live entity, so the hashed entity span covers slot e

            uint before = SimChecksum.Compute(world, buildings, resources, registry);
            world.Generation[e] = 5;                             // direct mutation — never reachable this way in the sim
            uint after  = SimChecksum.Compute(world, buildings, resources, registry);

            Assert.Equal(before, after);
        }
    }
}
