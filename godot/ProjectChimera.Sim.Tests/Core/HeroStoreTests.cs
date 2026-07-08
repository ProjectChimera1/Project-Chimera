#nullable enable
using ProjectChimera.Core; // HeroStore, HeroId, Fixed
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 3.2 (AC1) — the <see cref="HeroStore"/> sparse SoA + the stable <see cref="HeroId"/> identity contract.
    /// Proves the three load-bearing guarantees the AR-12 hero substrate rests on:
    ///   • minting assigns a STABLE identity distinct from the entity id (and from the slot);
    ///   • a recycled slot carries NONE of the prior hero's state (the SoA-recycle trap);
    ///   • a stale packed handle FAILS to resolve after the slot is recycled (ABA-armor), never retargeting a new hero;
    /// plus the ascending-identity FoldOrder contract (AC2) that Story 3.13's per-tick SimChecksum fold reuses.
    ///
    /// Mirrors the Story-2.13 BuildingStore recycle/ABA tests (HeroStore is a structural clone of that store).
    /// </summary>
    public class HeroStoreTests
    {
        private static readonly Fixed Xp0 = Fixed.FromInt(0);

        [Fact]
        public void Mint_AssignsStableIdentity_DistinctFromEntityId()
        {
            var s = new HeroStore();
            // A HeroId deliberately NOT equal to the entity id it links (and not the slot it lands in).
            int slot = s.Mint(new HeroId(9_000_000_001UL), entityId: 5, level: 3, xp: Fixed.FromInt(120));

            Assert.Equal(0, slot);                          // first mint → slot 0
            Assert.True(s.Alive[slot]);
            Assert.Equal(9_000_000_001UL, s.Id[slot].Value); // the stable identity...
            Assert.Equal(5, s.EntityId[slot]);               // ...distinct from the entity link...
            Assert.NotEqual((ulong)slot, s.Id[slot].Value);  // ...and distinct from the raw slot index.
            Assert.NotEqual((ulong)s.EntityId[slot], s.Id[slot].Value);
            Assert.Equal(3, s.Level[slot]);
            Assert.Equal(Fixed.FromInt(120).Raw, s.Xp[slot].Raw);
            Assert.Equal(1, s.Count);
        }

        [Fact]
        public void Mint_RecyclesDeadSlotLifo_CountIsHighWater()
        {
            var s = new HeroStore();
            int a = s.Mint(new HeroId(10), 1, 1, Xp0);
            int b = s.Mint(new HeroId(20), 2, 1, Xp0);
            int c = s.Mint(new HeroId(30), 3, 1, Xp0);
            Assert.Equal(new[] { 0, 1, 2 }, new[] { a, b, c });
            Assert.Equal(3, s.Count);

            s.Destroy(b); // free slot 1
            int reused = s.Mint(new HeroId(40), 4, 1, Xp0);
            Assert.Equal(b, reused);   // LIFO reuse of the freed slot
            Assert.Equal(3, s.Count);  // Count is a monotonic high-water mark — recycling does not grow it
        }

        [Fact]
        public void RecycledSlot_CarriesNoPriorHeroState()
        {
            var s = new HeroStore();
            int first = s.Mint(new HeroId(777), entityId: 9, level: 5, xp: Fixed.FromInt(999));
            s.Destroy(first);

            int reused = s.Mint(new HeroId(888), entityId: 2, level: 1, xp: Fixed.FromInt(0));
            Assert.Equal(first, reused); // same slot off the free-list

            // The new occupant carries NONE of the prior hero's identity / link / progression.
            Assert.Equal(888UL, s.Id[reused].Value);
            Assert.Equal(2, s.EntityId[reused]);
            Assert.Equal(1, s.Level[reused]);
            Assert.Equal(Fixed.Zero.Raw, s.Xp[reused].Raw);
        }

        [Fact]
        public void RecycledSlot_CarriesNoPriorInventory()
        {
            // Story 3.15 (P7): mirror ItemStoreTests.RecycledSlot_CarriesNoPriorDefOrCharges for the hero INVENTORY.
            // A recycled hero slot must NOT inherit the prior occupant's held item refs — else a revived/re-minted hero
            // would resurrect the dead hero's loadout (the SoA-recycle trap applied to the inventory ring).
            var s = new HeroStore();
            int a = s.Mint(new HeroId(777), entityId: 9, level: 5, xp: Fixed.FromInt(999));
            // Fill hero A's entire inventory stride with (arbitrary) held item refs.
            int aBase = a * HeroStore.INVENTORY_SLOTS;
            for (int slot = 0; slot < HeroStore.INVENTORY_SLOTS; slot++)
                s.Inventory[aBase + slot] = 500 + slot;

            s.Destroy(a);
            int b = s.Mint(new HeroId(888), entityId: 2, level: 1, xp: Fixed.FromInt(0));
            Assert.Equal(a, b); // same physical slot off the free-list

            // Every one of hero B's inventory slots reads the empty sentinel — none of A's refs survived.
            int bBase = b * HeroStore.INVENTORY_SLOTS;
            for (int slot = 0; slot < HeroStore.INVENTORY_SLOTS; slot++)
                Assert.Equal(HeroStore.INVENTORY_EMPTY, s.Inventory[bBase + slot]);
        }

        [Fact]
        public void PackRef_RoundTrips_ForLiveSlot_AndIsGoldenNeutralAtGenZero()
        {
            var s = new HeroStore();
            int slot = s.Mint(new HeroId(55), 1, 1, Xp0);

            int packed = s.PackRef(slot);
            Assert.Equal(slot, packed); // generation 0 (never recycled) ⇒ PackRef(slot) == slot (golden-neutral)
            Assert.True(s.TryResolveRef(packed, out int resolved));
            Assert.Equal(slot, resolved);
        }

        [Fact]
        public void StalePackedHandle_FailsToResolve_AfterRecycle()
        {
            var s = new HeroStore();
            int slot = s.Mint(new HeroId(100), 1, 1, Xp0);
            int staleHandle = s.PackRef(slot); // a cross-tick reference to hero #100

            s.Destroy(slot);
            int reused = s.Mint(new HeroId(200), 2, 1, Xp0); // a DIFFERENT hero reuses the same slot (generation bumped)
            Assert.Equal(slot, reused);

            // The stale handle must NOT resolve — never ABA-retarget the new occupant (hero #200).
            Assert.False(s.TryResolveRef(staleHandle, out _));
            // ...while a fresh handle to the new occupant resolves fine.
            Assert.True(s.TryResolveRef(s.PackRef(reused), out int now));
            Assert.Equal(reused, now);
        }

        [Fact]
        public void TryResolveRef_HeroNoneSentinel_ResolvesFalse()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1), 1, 1, Xp0); // Count = 1

            // EntityWorld.HERO_NONE (-1) is the "not a hero" sentinel; it must resolve false, not alias slot 0.
            Assert.False(s.TryResolveRef(EntityWorld.HERO_NONE, out _));
        }

        [Fact]
        public void Destroy_IsBoundsAndDoubleFreeGuarded()
        {
            var s = new HeroStore();
            int a = s.Mint(new HeroId(1), 1, 1, Xp0);
            s.Destroy(a);
            s.Destroy(a);      // double-free must be a no-op (never push the slot twice)
            s.Destroy(-1);     // out of bounds — no-op
            s.Destroy(999);    // out of bounds — no-op

            // The free-list is not corrupted: exactly one reuse of slot a, then a fresh append.
            int reused = s.Mint(new HeroId(2), 2, 1, Xp0);
            Assert.Equal(a, reused);
            int fresh = s.Mint(new HeroId(3), 3, 1, Xp0);
            Assert.Equal(1, fresh); // a fresh high-water slot, proving the free-list held exactly one entry
        }

        [Fact]
        public void Mint_ReturnsMinusOne_WhenFull()
        {
            var s = new HeroStore();
            for (int i = 0; i < HeroStore.MAX_HEROES; i++)
                Assert.True(s.Mint(new HeroId((ulong)(i + 1)), i, 1, Xp0) >= 0);

            Assert.Equal(-1, s.Mint(new HeroId(999999), 0, 1, Xp0)); // all slots simultaneously live → full
        }

        [Fact]
        public void FoldOrder_IsAscendingByHeroId_AndSkipsDeadSlots()
        {
            var s = new HeroStore();
            // Mint in a deliberately NON-sorted identity order.
            s.Mint(new HeroId(300), 1, 1, Xp0); // slot 0
            s.Mint(new HeroId(100), 2, 1, Xp0); // slot 1
            s.Mint(new HeroId(200), 3, 1, Xp0); // slot 2
            s.Mint(new HeroId(50),  4, 1, Xp0); // slot 3
            s.Destroy(2); // remove HeroId 200

            int[] order = s.FoldOrder();
            // Live ids {300, 100, 50} sorted ascending → 50 (slot 3), 100 (slot 1), 300 (slot 0). Dead 200 skipped.
            Assert.Equal(new[] { 3, 1, 0 }, order);
            Assert.Equal(new[] { 50UL, 100UL, 300UL }, System.Array.ConvertAll(order, slot => s.Id[slot].Value));
        }

        [Fact]
        public void FoldOrder_IsMintOrderIndependent()
        {
            // Two stores with the SAME heroes minted in OPPOSITE orders must yield the same identity sequence — the
            // producer-independence property StartStateHash relies on (M2-local vs M5-server mint-order divergence).
            var forward = new HeroStore();
            forward.Mint(new HeroId(11), 1, 2, Fixed.FromInt(10));
            forward.Mint(new HeroId(22), 2, 3, Fixed.FromInt(20));
            forward.Mint(new HeroId(33), 3, 4, Fixed.FromInt(30));

            var reverse = new HeroStore();
            reverse.Mint(new HeroId(33), 9, 4, Fixed.FromInt(30));
            reverse.Mint(new HeroId(22), 8, 3, Fixed.FromInt(20));
            reverse.Mint(new HeroId(11), 7, 2, Fixed.FromInt(10));

            ulong[] fwdIds = System.Array.ConvertAll(forward.FoldOrder(), slot => forward.Id[slot].Value);
            ulong[] revIds = System.Array.ConvertAll(reverse.FoldOrder(), slot => reverse.Id[slot].Value);
            Assert.Equal(new[] { 11UL, 22UL, 33UL }, fwdIds);
            Assert.Equal(fwdIds, revIds);
        }

        [Fact]
        public void FoldOrder_Empty_IsEmpty()
        {
            Assert.Empty(new HeroStore().FoldOrder());
        }

        [Fact]
        public void HeroId_Equality_ByValue()
        {
            Assert.Equal(new HeroId(42), new HeroId(42));
            Assert.True(new HeroId(42) == new HeroId(42));
            Assert.True(new HeroId(42) != new HeroId(43));
            Assert.NotEqual(new HeroId(42), new HeroId(43));
        }

        [Fact]
        public void Mint_RefusesDuplicateLiveHeroId()
        {
            // FoldOrder's producer-independence (AC2) requires HeroId UNIQUE across live rows — a duplicate would fold
            // in mint-order-dependent slot order. Mint hard-rejects a duplicate LIVE id with -1 and must not perturb the
            // store; once the original is destroyed the id is free again (uniqueness is over live rows only).
            var s = new HeroStore();
            int first = s.Mint(new HeroId(42), entityId: 1, level: 3, xp: Fixed.FromInt(100));
            Assert.True(first >= 0);

            int dup = s.Mint(new HeroId(42), entityId: 2, level: 9, xp: Fixed.FromInt(500));
            Assert.Equal(-1, dup);                       // refused
            Assert.Equal(1, s.Count);                    // no slot consumed
            Assert.Equal(3, s.Level[first]);             // original row untouched...
            Assert.Equal(1, s.EntityId[first]);
            Assert.Equal(Fixed.FromInt(100).Raw, s.Xp[first].Raw);

            s.Destroy(first);
            Assert.True(s.Mint(new HeroId(42), entityId: 7, level: 1, xp: Xp0) >= 0); // dead id is re-mintable
        }

        [Fact]
        public void MaxHeroes_FitsPackRefSlotField()
        {
            // PackRef packs the slot into the low 8 bits (TryResolveRef reads packed & 0xFF); the cap MUST fit in 256 or
            // slot bits alias the generation field (ABA-unsafe). Tripwire: widen PackRef/TryResolveRef before bumping.
            Assert.True(HeroStore.MAX_HEROES <= 256,
                "MAX_HEROES exceeds the 8-bit PackRef slot field; widen the pack encoding first.");
        }
    }
}
