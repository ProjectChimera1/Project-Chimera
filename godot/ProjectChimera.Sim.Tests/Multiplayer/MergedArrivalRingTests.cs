#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-390 — Tier-1 coverage for the client-side merged-arrival ring/gate/seed extracted from
    /// <c>LockstepManager</c> into the Godot-free <see cref="MergedArrivalRing"/>: stall-on-missing,
    /// apply-on-arrival (through the REAL <see cref="MergedTickApplier"/>), ring-wraparound (a wrong-tick
    /// occupant demotes to a stall, never a mis-apply), and the empty-seed no-op (a seeded bootstrap/delay-gap
    /// tick stores length 0, which the applier decodes to nothing). Before this extraction none of these
    /// decisions were exercised by any automated test (LockstepManager is Godot-coupled and never constructed
    /// in the suite).
    /// </summary>
    public class MergedArrivalRingTests
    {
        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>Serialize a real one-sub-bundle merged packet (one Player1 Move order) for <paramref name="tick"/>.</summary>
        private static (byte[] buf, int len) MakeMergedMove(uint tick, int unitId, int txInt, int tzInt)
        {
            var factions    = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var orderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var ordersFlat  = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            factions[0]    = Faction.Player1;
            orderCounts[0] = 1;
            ordersFlat[0]  = new UnitOrder(unitId, UnitCommand.Move, Fixed.FromInt(txInt), Fixed.FromInt(tzInt));

            var buf = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            int len = MergedTickPacket.Write(buf, tick, factions, orderCounts, ordersFlat, 1);
            return (buf, len);
        }

        /// <summary>A minimal world with one movable Player1 unit at id 0.</summary>
        private static EntityWorld WorldWithOneUnit(out int id)
        {
            var world = new EntityWorld();
            id = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                              Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(0, id);
            return world;
        }

        // ── stall-on-missing ──────────────────────────────────────────────────

        [Fact]
        public void FreshRing_NothingArrived_EveryTickStalls()
        {
            var ring = new MergedArrivalRing();
            foreach (uint t in new uint[] { 0, 1, 5, 15, 16, 17, 1000 })
                Assert.False(ring.IsReady(t), $"tick {t} must stall on a fresh ring (nothing arrived)");
        }

        [Fact]
        public void ArrivalForOneTick_DoesNotUnstallAnyOtherTick()
        {
            var ring = new MergedArrivalRing();
            (byte[] buf, int len) = MakeMergedMove(7, unitId: 0, txInt: 5, tzInt: 7);
            Assert.True(ring.TryStore(buf, len));

            Assert.True(ring.IsReady(7));
            Assert.False(ring.IsReady(6));   // neighbouring slot — untouched
            Assert.False(ring.IsReady(8));   // neighbouring slot — untouched
            Assert.False(ring.IsReady(7 + MergedArrivalRing.BUFFER_SIZE));  // SAME slot, wrong tick → stall
        }

        // ── apply-on-arrival ──────────────────────────────────────────────────

        [Fact]
        public void StoredMergedPacket_GatePasses_AndAppliesThroughTheRealApplier()
        {
            var ring = new MergedArrivalRing();
            EntityWorld world = WorldWithOneUnit(out int id);

            (byte[] buf, int len) = MakeMergedMove(3, id, txInt: 5, tzInt: 7);
            Assert.True(ring.TryStore(buf, len));
            Assert.True(ring.IsReady(3), "the gate must open the tick its merged packet arrived for");
            Assert.Equal(len, ring.LenFor(3));

            // The stored payload is byte-identical to what was received (the ring copies, never aliases).
            byte[] stored = ring.BytesFor(3);
            for (int i = 0; i < len; i++)
                Assert.Equal(buf[i], stored[i]);

            // Apply through the REAL applier — the order lands on the world (the path is load-bearing).
            MergedTickApplier.Apply(ring.BytesFor(3), ring.LenFor(3), world);
            Assert.Equal(UnitCommand.Move, world.CommandState[id]);
            Assert.Equal(Fixed.FromInt(5).Raw, world.MoveTarget[id].X.Raw);
            Assert.Equal(Fixed.FromInt(7).Raw, world.MoveTarget[id].Z.Raw);

            // Consuming releases the slot: the same tick stalls again (one apply per arrival).
            ring.ConsumeArrival(3);
            Assert.False(ring.IsReady(3));
        }

        [Fact]
        public void SequentialStream_StoreGateConsume_SurvivesMultipleWraparounds()
        {
            var ring = new MergedArrivalRing();
            // 40 ticks = 2.5 laps of the 16-slot ring — the steady-state store→ready→apply→consume cycle.
            for (uint t = 0; t < 40; t++)
            {
                (byte[] buf, int len) = MakeMergedMove(t, unitId: 0, txInt: (int)(t % 13), tzInt: (int)(t % 11));
                Assert.True(ring.TryStore(buf, len), $"store failed at tick {t}");
                Assert.True(ring.IsReady(t), $"gate closed at tick {t}");
                ring.ConsumeArrival(t);
                Assert.False(ring.IsReady(t), $"slot not released at tick {t}");
            }
        }

        // ── ring-wraparound ───────────────────────────────────────────────────

        [Fact]
        public void WrongTickOccupant_DemotesToStall_NeverMisapplies()
        {
            var ring = new MergedArrivalRing();
            (byte[] oldBuf, int oldLen) = MakeMergedMove(4, unitId: 0, txInt: 1, tzInt: 1);
            Assert.True(ring.TryStore(oldBuf, oldLen));
            Assert.True(ring.IsReady(4));

            // Tick 20 shares slot 4 (20 & 15 == 4). The stale occupant must NOT satisfy tick 20's gate:
            // the wrong-tick occupant is demoted to a STALL — the sim waits, it never applies tick 4's
            // commands at tick 20 (the mis-apply the DW-390 ledger entry names as the guarded regression).
            Assert.False(ring.IsReady(20), "a wrong-tick occupant must stall, not mis-apply");

            // The newer wrapped tick then overwrites the slot — and the OLD tick no longer gates through.
            (byte[] newBuf, int newLen) = MakeMergedMove(20, unitId: 0, txInt: 9, tzInt: 9);
            Assert.True(ring.TryStore(newBuf, newLen));
            Assert.True(ring.IsReady(20));
            Assert.False(ring.IsReady(4), "the overwritten older tick must stall (its payload is gone)");
            Assert.Equal(newLen, ring.LenFor(20));
        }

        // ── empty-seed no-op ──────────────────────────────────────────────────

        [Fact]
        public void SeededBootstrapTick_GatesThrough_AndAppliesAsDeterministicNoOp()
        {
            var ring = new MergedArrivalRing();
            EntityWorld world = WorldWithOneUnit(out int id);
            UnitCommand cmdBefore = world.CommandState[id];
            FixedVec3   posBefore = world.Position[id];
            FixedVec3   tgtBefore = world.MoveTarget[id];

            ring.SeedBootstrap(4); // INPUT_DELAY-shaped bootstrap: ticks 0..3

            for (uint t = 0; t < 4; t++)
            {
                Assert.True(ring.IsReady(t), $"seeded bootstrap tick {t} must gate through without a packet");
                Assert.Equal(0, ring.LenFor(t)); // seeded = EMPTY merged
                Assert.True(ring.HasSent(t), "a seeded tick must also be marked locally-sent (no client bundle for it)");

                // The empty seed is a deterministic NO-OP through the real applier: nothing on the world moves.
                MergedTickApplier.Apply(ring.BytesFor(t), ring.LenFor(t), world);
                ring.ConsumeArrival(t);
            }

            Assert.Equal(cmdBefore, world.CommandState[id]);
            Assert.Equal(posBefore.X.Raw, world.Position[id].X.Raw);
            Assert.Equal(posBefore.Z.Raw, world.Position[id].Z.Raw);
            Assert.Equal(tgtBefore.X.Raw, world.MoveTarget[id].X.Raw);
            Assert.Equal(tgtBefore.Z.Raw, world.MoveTarget[id].Z.Raw);

            Assert.False(ring.IsReady(4), "the first REAL tick (== delay) must still stall until its packet arrives");
        }

        [Fact]
        public void SeedBootstrap_ZeroDelay_SeedsNothing()
        {
            var ring = new MergedArrivalRing();
            ring.SeedBootstrap(0);
            for (uint t = 0; t < MergedArrivalRing.BUFFER_SIZE; t++)
                Assert.False(ring.IsReady(t));
        }

        [Fact]
        public void SeedDelayGap_Grow_SeedsExactlyOldPlus1ThroughNew()
        {
            var ring = new MergedArrivalRing();
            const uint currentTick = 100;
            ring.SeedDelayGap(currentTick, oldDelay: 4, newDelay: 8);

            Assert.False(ring.IsReady(104), "currentTick+oldDelay is NOT part of the gap (already in flight)");
            for (uint t = 105; t <= 108; t++)
            {
                Assert.True(ring.IsReady(t), $"gap tick {t} must be seeded");
                Assert.Equal(0, ring.LenFor(t));
                Assert.True(ring.HasSent(t));
            }
            Assert.False(ring.IsReady(109), "currentTick+newDelay+1 is past the gap and must stall");
        }

        [Fact]
        public void SeedDelayGap_Shrink_IsANoOp()
        {
            var ring = new MergedArrivalRing();
            ring.SeedDelayGap(100, oldDelay: 8, newDelay: 4);
            for (uint t = 100; t <= 116; t++)
                Assert.False(ring.IsReady(t), $"a shrinking delay change must seed nothing (tick {t})");
        }

        // ── keying rejects (the HandleMergedTick drop rules) ──────────────────

        [Fact]
        public void TryStore_RejectsWrongType_Truncated_AndOverCeiling_StoringNothing()
        {
            var ring = new MergedArrivalRing();

            // Wrong packet type — unpeekable.
            var wrongType = new byte[16];
            wrongType[0] = (byte)PacketType.Checksum;
            Assert.False(ring.TryStore(wrongType, wrongType.Length));

            // Truncated below the merged header — unpeekable.
            var truncated = new byte[MergedTickPacket.HEADER_BYTES - 1];
            truncated[0] = (byte)PacketType.TickCommandsMerged;
            Assert.False(ring.TryStore(truncated, truncated.Length));

            // Over the absolute byte ceiling — peekable but dropped (defensive; the codec also rejects).
            var over = new byte[MergedTickPacket.MERGED_MAX_BYTES + 1];
            over[0] = (byte)PacketType.TickCommandsMerged;
            over[1] = 9; // tick 9, LE
            Assert.False(ring.TryStore(over, over.Length));
            Assert.False(ring.IsReady(9), "an over-ceiling packet must store nothing");

            for (uint t = 0; t < MergedArrivalRing.BUFFER_SIZE; t++)
                Assert.False(ring.IsReady(t), "no reject path may leave a slot armed");
        }

        // ── send-guard round-trip ─────────────────────────────────────────────

        [Fact]
        public void SendGuard_MarkHasClear_RoundTrips_PerSlot()
        {
            var ring = new MergedArrivalRing();
            Assert.False(ring.HasSent(5));
            ring.MarkSent(5);
            Assert.True(ring.HasSent(5));
            Assert.True(ring.HasSent(5 + MergedArrivalRing.BUFFER_SIZE)); // the guard is PER SLOT (issue keying)
            ring.ClearSent(5);
            Assert.False(ring.HasSent(5));
        }

        // ── structural invariant (formerly documented on LockstepManager's private const) ──

        [Fact]
        public void BufferSize_IsAPowerOfTwo_GreaterThanMaxDelayPlusOne()
        {
            Assert.True((MergedArrivalRing.BUFFER_SIZE & (MergedArrivalRing.BUFFER_SIZE - 1)) == 0,
                "BUFFER_SIZE must stay a power of two (the tick & mask keying depends on it)");
            Assert.True(MergedArrivalRing.BUFFER_SIZE > DelayMath.MAX_DELAY + 1,
                "BUFFER_SIZE must exceed MAX_DELAY + 1 or a max-delay issue tick collides with its exec tick");
        }
    }
}
