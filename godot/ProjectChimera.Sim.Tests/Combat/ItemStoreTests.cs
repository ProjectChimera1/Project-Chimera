#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 3.15 — the <see cref="ItemStore"/> recycle-guard + ABA-armor unit tests (mirrors the BuildingStore/HeroStore
    /// contract): a recycled slot carries NO prior <c>DefId</c>/<c>Charges</c>; the free-list is LIFO; a stale packed ref
    /// fails <see cref="ItemStore.TryResolveRef"/> after recycle; and the live enumeration is count-driven ascending.
    /// </summary>
    public class ItemStoreTests
    {
        private static FixedVec3 P(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        [Fact]
        public void Create_SetsAllLiveFields_OnGround()
        {
            var s = new ItemStore();
            int r = s.Create(defId: 2, charges: 3, P(5, -4));
            Assert.True(s.TryResolveRef(r, out int slot));
            Assert.True(s.Alive[slot]);
            Assert.Equal(2, s.DefId[slot]);
            Assert.Equal(3, s.Charges[slot]);
            Assert.False(s.Held[slot]);
            Assert.Equal(ItemStore.NO_CARRIER, s.CarrierHeroSlot[slot]);
            Assert.Equal(Fixed.FromInt(5), s.PosX[slot]);
            Assert.Equal(Fixed.FromInt(-4), s.PosZ[slot]);
        }

        [Fact]
        public void RecycledSlot_CarriesNoPriorDefOrCharges()
        {
            var s = new ItemStore();
            int r0 = s.Create(defId: 7, charges: 9, P(1, 1));
            Assert.True(s.TryResolveRef(r0, out int slot0));
            s.Destroy(slot0);
            int r1 = s.Create(defId: 1, charges: 0, P(2, 2)); // reuses slot0 (LIFO)
            Assert.True(s.TryResolveRef(r1, out int slot1));
            Assert.Equal(slot0, slot1);              // same physical slot reused
            Assert.Equal(1, s.DefId[slot1]);         // NOT the prior 7
            Assert.Equal(0, s.Charges[slot1]);       // NOT the prior 9
        }

        [Fact]
        public void StaleRef_AfterRecycle_FailsResolve()
        {
            var s = new ItemStore();
            int r0 = s.Create(defId: 1, charges: 1, P(0, 0));
            Assert.True(s.TryResolveRef(r0, out int slot0));
            s.Destroy(slot0);
            int r1 = s.Create(defId: 2, charges: 2, P(0, 0)); // reuses slot0, bumps generation
            Assert.False(s.TryResolveRef(r0, out _));         // stale ref → clean revert, never ABA-retargets r1
            Assert.True(s.TryResolveRef(r1, out _));
        }

        [Fact]
        public void FreeList_IsLifo()
        {
            var s = new ItemStore();
            int ra = s.Create(1, 1, P(0, 0)); s.TryResolveRef(ra, out int a);
            int rb = s.Create(1, 1, P(0, 0)); s.TryResolveRef(rb, out int b);
            s.Destroy(a);
            s.Destroy(b);
            int rc = s.Create(1, 1, P(0, 0)); s.TryResolveRef(rc, out int c); // last freed (b) comes back first
            Assert.Equal(b, c);
        }

        [Fact]
        public void DoubleFree_IsGuarded()
        {
            var s = new ItemStore();
            int r = s.Create(1, 1, P(0, 0)); s.TryResolveRef(r, out int slot);
            s.Destroy(slot);
            s.Destroy(slot); // no-op (never pushes the same slot twice)
            int r2 = s.Create(1, 1, P(0, 0)); s.TryResolveRef(r2, out int slot2);
            int r3 = s.Create(1, 1, P(0, 0)); s.TryResolveRef(r3, out int slot3);
            Assert.NotEqual(slot2, slot3); // distinct slots — the store is not corrupted by the double-free
        }

        [Fact]
        public void Clear_EqualsFreshStore()
        {
            var s = new ItemStore();
            s.Create(1, 1, P(1, 1));
            s.Create(2, 2, P(2, 2));
            s.Clear();
            Assert.Equal(0, s.Count);
            // A cleared store re-mints from slot 0.
            int r = s.Create(3, 3, P(3, 3));
            Assert.True(s.TryResolveRef(r, out int slot));
            Assert.Equal(0, slot);
        }
    }
}
