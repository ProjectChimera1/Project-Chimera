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
        public void SeedDelayGap_Grow_SeedsExactlyOldThroughNewMinus1()
        {
            var ring = new MergedArrivalRing();
            const uint currentTick = 100;
            ring.SeedDelayGap(currentTick, oldDelay: 4, newDelay: 8);

            Assert.False(ring.IsReady(103),
                "currentTick+oldDelay-1 was issued by the PREVIOUS exec tick — in flight, not part of the gap");
            for (uint t = 104; t <= 107; t++)
            {
                Assert.True(ring.IsReady(t), $"gap tick {t} must be seeded");
                Assert.Equal(0, ring.LenFor(t));
                Assert.True(ring.HasSent(t));
            }
            Assert.False(ring.IsReady(108),
                "currentTick+newDelay is the FIRST tick sent under the new delay — seeding it would discard real orders");
        }

        /// <summary>
        /// DW-914 — the property the old bounds violated, stated as the thing that actually matters rather than as
        /// a pair of endpoints: across a growing delay change, EVERY tick the sim must execute is either sent for by
        /// the client or seeded empty — never neither (a permanent deadlock, which froze the 2026-08-08 LAN runs at
        /// tick 215 and 902) and never both (the seed marks the tick locally-sent, silently suppressing a real
        /// bundle of orders).
        ///
        /// <para>The schedule is modelled exactly as <c>LockstepManager.Flush</c> drives it: the commit happens
        /// inside the flush for <c>applyTick</c> and BEFORE that flush computes its issue tick, so exec ticks below
        /// <c>applyTick</c> issue at the old delay and <c>applyTick</c> onward issue at the new one.</para>
        /// </summary>
        [Theory]
        [InlineData(2, 3)]   // the exact change observed on the rig — a one-tick gap
        [InlineData(2, 5)]
        [InlineData(4, 8)]
        [InlineData(2, 12)]  // MIN_DELAY -> MAX_DELAY, the widest gap possible
        [InlineData(11, 12)]
        public void DelayGrow_LeavesEveryTickEitherSentOrSeeded_NeverNeitherNeverBoth(int oldDelay, int newDelay)
        {
            const uint applyTick = 100;
            var ring = new MergedArrivalRing();

            // Every issue tick the client actually transmits a bundle for, either side of the change. The
            // pre-change window must reach back at least MAX_DELAY exec ticks, or a large oldDelay leaves ticks
            // looking unsent purely because the model never simulated the frame that issued them.
            const uint lookback = DelayMath.MAX_DELAY + 2;
            var sentFor = new System.Collections.Generic.HashSet<uint>();
            for (uint exec = applyTick - lookback; exec < applyTick; exec++) sentFor.Add(exec + (uint)oldDelay);
            for (uint exec = applyTick; exec <= applyTick + lookback; exec++) sentFor.Add(exec + (uint)newDelay);

            ring.SeedDelayGap(applyTick, oldDelay, newDelay);

            for (uint t = applyTick; t <= applyTick + (uint)newDelay; t++)
            {
                bool seeded = ring.IsReady(t);
                bool sent   = sentFor.Contains(t);

                Assert.True(seeded || sent,
                    $"tick {t} is neither sent for nor seeded — no merged packet can ever exist for it and every " +
                    $"client deadlocks here (delay {oldDelay}->{newDelay} at {applyTick}).");
                Assert.False(seeded && sent,
                    $"tick {t} is BOTH seeded empty and sent for — the seed marks it locally-sent, so the client's " +
                    $"real orders for it are silently discarded (delay {oldDelay}->{newDelay} at {applyTick}).");
            }
        }

        [Fact]
        public void SeedDelayGap_Shrink_IsANoOp()
        {
            var ring = new MergedArrivalRing();
            ring.SeedDelayGap(100, oldDelay: 8, newDelay: 4);
            for (uint t = 100; t <= 116; t++)
                Assert.False(ring.IsReady(t), $"a shrinking delay change must seed nothing (tick {t})");
        }

        // ── DW-598: the match-lifecycle wipe ──────────────────────────────────

        /// <summary>
        /// DW-598 — <see cref="MergedArrivalRing.Clear"/> wipes every lane the gate reads: arrival, the tick it was
        /// for, the payload length, and the local-send guard. Asserted over the WHOLE ring, so a lane that is only
        /// half-wiped (the failure mode: a cleared <c>_arrived</c> with a stale <c>_len</c>, or a cleared arrival with
        /// a stale send guard) cannot pass.
        /// </summary>
        [Fact]
        public void Clear_WipesEveryLane_LeavingAFreshRing()
        {
            var ring = new MergedArrivalRing();

            // Fill the whole ring: every slot arrived, every slot marked locally-sent.
            for (uint t = 0; t < MergedArrivalRing.BUFFER_SIZE; t++)
            {
                (byte[] buf, int len) = MakeMergedMove(t, unitId: 0, txInt: 3, tzInt: 4);
                Assert.True(ring.TryStore(buf, len));
                ring.MarkSent(t);
                Assert.True(ring.IsReady(t));
                Assert.True(ring.LenFor(t) > 0);
            }

            ring.Clear();

            for (uint t = 0; t < MergedArrivalRing.BUFFER_SIZE; t++)
            {
                Assert.False(ring.IsReady(t), $"slot {t} still gates through after Clear()");
                Assert.Equal(0, ring.LenFor(t));
                Assert.False(ring.HasSent(t), $"slot {t} still reads locally-sent after Clear()");
            }
        }

        /// <summary>
        /// DW-598, the actual hazard: a REUSED manager starting a second match. Both matches count from tick 0, so a
        /// prior match's packet for tick T sits in the slot the new match's tick T needs WITH THE SAME
        /// <c>_tickFor</c> — the wrong-tick demotion that protects against wraparound is blind to it, because the
        /// tick number genuinely matches. Without the Clear in <c>SeedInitialTicks</c> the new match's gate opens on
        /// the OLD match's commands (and a stale send-guard suppresses a real bundle the new match does need to
        /// emit). Modelled exactly as <c>LockstepManager.SeedInitialTicks</c> drives it: Clear, then SeedBootstrap.
        /// </summary>
        [Fact]
        public void ClearThenSeedBootstrap_NewMatchNeverInheritsThePriorMatchsArrivals()
        {
            const int delay = 4;
            var ring = new MergedArrivalRing();

            // ── match 1 ── the client ran well past the bootstrap gap and left arrivals + send guards behind.
            ring.SeedBootstrap(delay);
            for (uint t = delay; t < 12; t++)
            {
                (byte[] buf, int len) = MakeMergedMove(t, unitId: 0, txInt: 9, tzInt: 9);
                Assert.True(ring.TryStore(buf, len));
                ring.MarkSent(t);
            }

            // ── match 2 ── the same manager restarts: SeedInitialTicks = Clear() then SeedBootstrap(delay).
            ring.Clear();
            ring.SeedBootstrap(delay);

            // The bootstrap gap is seeded EMPTY — a prior match's real payload must not be sitting in it.
            for (uint t = 0; t < delay; t++)
            {
                Assert.True(ring.IsReady(t));
                Assert.Equal(0, ring.LenFor(t));
            }
            // Every tick past the gap must STALL until this match's own merged packet arrives — the prior match's
            // occupant of that slot carried the very same tick number, so nothing else would have rejected it.
            for (uint t = delay; t < 12; t++)
            {
                Assert.False(ring.IsReady(t), $"tick {t} gated through on the PRIOR match's arrival (DW-598)");
                Assert.False(ring.HasSent(t), $"tick {t} reads already-sent from the PRIOR match (DW-598)");
            }
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
