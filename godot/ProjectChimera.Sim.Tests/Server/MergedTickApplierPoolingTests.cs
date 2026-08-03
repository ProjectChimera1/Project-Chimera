#nullable enable
using System;
using ProjectChimera.Core;                 // EntityWorld, Faction, UnitOrder, UnitCommand, Fixed, EntityFlags
using ProjectChimera.Multiplayer;           // TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;    // MergedTickBuilder, MergedTickApplier
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-391 — the pooled (caller-owned scratch) overload of <see cref="MergedTickApplier.Apply"/>. The per-tick
    /// production path (LockstepManager.ApplyMerged, 30 tps) must not allocate: these tests pin (1) the pooled
    /// overload is ZERO-alloc after warmup, (2) dirty scratch reuse across ticks is deterministic (prior-tick
    /// residue never leaks into the applied orders), (3) the pooled and allocating overloads produce identical
    /// world state, (4) undersized scratch fails loudly at the call site, (5) the per-sub-bundle recorder hook
    /// receives the caller's own scratch instance (no hidden copy re-appearing), and (6) the allocating
    /// convenience overload keeps allocating FRESH scratch per call (thread-safe by construction — a shared
    /// static cache sneaking in would be a data race under concurrent appliers).
    /// </summary>
    public class MergedTickApplierPoolingTests
    {
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        private static UnitOrder Move(int unitId, int rx, int rz) =>
            new UnitOrder(unitId, UnitCommand.Move, Fixed.FromRaw(rx), Fixed.FromRaw(rz));

        private static (byte[] data, int len) Tick(uint tick, Faction faction, params UnitOrder[] orders)
        {
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, tick, faction, orders, orders.Length);
            return (buf, len);
        }

        /// <summary>Build a real merged packet (through the builder) from a P1 + P2 order set for one tick.</summary>
        private static byte[] BuildMerged(uint tick, UnitOrder[] p1, UnitOrder[] p2, out int len)
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var (d0, l0) = Tick(tick, Faction.Player1, p1);
            var (d1, l1) = Tick(tick, Faction.Player2, p2);
            b.Submit(0, d0, l0, out _);
            b.Submit(1, d1, l1, out _);
            Assert.True(b.TryBuild(tick, out byte[] merged, out len));
            var copy = new byte[len];
            Array.Copy(merged, copy, len);
            return copy;
        }

        /// <summary>A world with one P1 unit (id out.p1) and one P2 unit (id out.p2), identical across calls.</summary>
        private static (EntityWorld world, int p1, int p2) World()
        {
            var world = new EntityWorld();
            int p1 = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2 = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            return (world, p1, p2);
        }

        private static (Faction[] factions, int[] counts, UnitOrder[] flat) Scratch() => (
            new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
            new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS]);

        /// <summary>Fill scratch with hostile residue: bogus factions, huge counts, junk orders everywhere.</summary>
        private static void Poison(Faction[] factions, int[] counts, UnitOrder[] flat)
        {
            for (int i = 0; i < factions.Length; i++) factions[i] = (Faction)0xFF;
            for (int i = 0; i < counts.Length; i++)   counts[i]   = 12345;
            for (int i = 0; i < flat.Length; i++)
                flat[i] = new UnitOrder(0xFFFF, (UnitCommand)0xEE, Fixed.FromRaw(int.MaxValue), Fixed.FromRaw(int.MinValue));
        }

        [Fact]
        public void PooledOverload_ProducesIdenticalWorldState_ToAllocatingOverload()
        {
            var (wa, a1, a2) = World();
            var (wb, b1, b2) = World();
            Assert.Equal(a1, b1); Assert.Equal(a2, b2); // identical create sequence → identical ids

            byte[] merged = BuildMerged(1u,
                new[] { Move(a1, Fixed.FromInt(20).Raw, Fixed.FromInt(5).Raw) },
                new[] { Move(a2, Fixed.FromInt(-7).Raw, Fixed.FromInt(9).Raw) }, out int len);

            MergedTickApplier.Apply(merged, len, wa); // allocating convenience path

            var (f, c, o) = Scratch();
            MergedTickApplier.Apply(merged, len, wb, f, c, o); // pooled path

            Assert.Equal(wa.MoveTarget[a1].X, wb.MoveTarget[b1].X);
            Assert.Equal(wa.MoveTarget[a1].Z, wb.MoveTarget[b1].Z);
            Assert.Equal(wa.MoveTarget[a2].X, wb.MoveTarget[b2].X);
            Assert.Equal(wa.MoveTarget[a2].Z, wb.MoveTarget[b2].Z);
            Assert.Equal(wa.Flags[a1], wb.Flags[b1]);
            Assert.Equal(wa.Flags[a2], wb.Flags[b2]);
        }

        [Fact]
        public void DirtyScratchReuse_AcrossTicks_MatchesFreshScratchApplies()
        {
            // The LockstepManager reuses ONE scratch set for the whole match without clearing it between ticks.
            // Prove hostile residue (bogus factions, huge counts, junk orders) can never leak into an apply: drive
            // two different ticks through the SAME poisoned scratch and compare against a control world driven by
            // the allocating overload. Tick 2 deliberately carries FEWER P1 orders than tick 1 so any stale-count
            // trust would over-apply junk.
            var (live, l1, l2) = World();
            var (ctrl, c1, c2) = World();

            byte[] t1 = BuildMerged(1u,
                new[] { Move(l1, Fixed.FromInt(20).Raw, Fixed.FromInt(5).Raw), Move(l1, Fixed.FromInt(2).Raw, Fixed.FromInt(3).Raw) },
                new[] { Move(l2, Fixed.FromInt(-7).Raw, Fixed.FromInt(9).Raw) }, out int len1);
            byte[] t2 = BuildMerged(2u,
                new[] { Move(l1, Fixed.FromInt(-4).Raw, Fixed.FromInt(11).Raw) },
                new[] { Move(l2, Fixed.FromInt(6).Raw, Fixed.FromInt(-2).Raw) }, out int len2);

            var (f, c, o) = Scratch();
            Poison(f, c, o);
            MergedTickApplier.Apply(t1, len1, live, f, c, o);
            Poison(f, c, o); // worst-case: residue mutates between ticks too
            MergedTickApplier.Apply(t2, len2, live, f, c, o);

            MergedTickApplier.Apply(t1, len1, ctrl);
            MergedTickApplier.Apply(t2, len2, ctrl);

            Assert.Equal(ctrl.MoveTarget[c1].X, live.MoveTarget[l1].X);
            Assert.Equal(ctrl.MoveTarget[c1].Z, live.MoveTarget[l1].Z);
            Assert.Equal(ctrl.MoveTarget[c2].X, live.MoveTarget[l2].X);
            Assert.Equal(ctrl.MoveTarget[c2].Z, live.MoveTarget[l2].Z);
            Assert.Equal(ctrl.Flags[c1], live.Flags[l1]);
            Assert.Equal(ctrl.Flags[c2], live.Flags[l2]);
        }

        [Fact]
        public void PooledOverload_IsZeroAlloc_AfterWarmup()
        {
            // DW-391's headline pin: the pooled apply path allocates NOTHING once warm — the whole point of the
            // caller-owned overload. Reverting the core to internal `new`s (the pre-fix shape) fails this test.
            var (world, p1, p2) = World();
            byte[] merged = BuildMerged(1u,
                new[] { Move(p1, Fixed.FromInt(20).Raw, Fixed.FromInt(5).Raw), Move(p1, Fixed.FromInt(1).Raw, Fixed.FromInt(2).Raw) },
                new[] { Move(p2, Fixed.FromInt(-7).Raw, Fixed.FromInt(9).Raw) }, out int len);
            var (f, c, o) = Scratch();

            MergedTickApplier.Apply(merged, len, world, f, c, o); // warm up JIT + any first-call statics
            MergedTickApplier.Apply(merged, len, world, f, c, o);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++)
                MergedTickApplier.Apply(merged, len, world, f, c, o);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }

        [Fact]
        public void AllocatingOverload_AllocatesFreshScratchPerCall_ThreadSafeByConstruction()
        {
            // The convenience overload must keep allocating FRESH scratch per call. If someone "optimizes" it with
            // a shared static cache, concurrent appliers (xunit parallelism, a future host+spectator in-process
            // pair) would race on the shared arrays — this pin forces that change to announce itself.
            var (world, p1, p2) = World();
            byte[] merged = BuildMerged(1u,
                new[] { Move(p1, Fixed.FromInt(20).Raw, Fixed.FromInt(5).Raw) },
                new[] { Move(p2, Fixed.FromInt(-7).Raw, Fixed.FromInt(9).Raw) }, out int len);

            MergedTickApplier.Apply(merged, len, world); // warm up JIT + any first-call statics

            long before = GC.GetAllocatedBytesForCurrentThread();
            MergedTickApplier.Apply(merged, len, world);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(after - before > 0,
                "the allocating convenience overload should allocate fresh scratch per call (no hidden shared cache)");
        }

        [Fact]
        public void PooledOverload_Throws_OnUndersizedScratch()
        {
            // Undersized scratch would IndexOutOfRange mid-decode on the first full packet (or silently work on
            // small ones) — the guard must fail loudly at the call site instead.
            var (world, p1, p2) = World();
            byte[] merged = BuildMerged(1u,
                new[] { Move(p1, 1, 1) }, new[] { Move(p2, 2, 2) }, out int len);
            var (f, c, o) = Scratch();

            var shortF = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES - 1];
            var shortC = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES - 1];
            var shortO = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS - 1];

            Assert.Throws<ArgumentException>(() => MergedTickApplier.Apply(merged, len, world, shortF, c, o));
            Assert.Throws<ArgumentException>(() => MergedTickApplier.Apply(merged, len, world, f, shortC, o));
            Assert.Throws<ArgumentException>(() => MergedTickApplier.Apply(merged, len, world, f, c, shortO));
        }

        [Fact]
        public void RecorderHook_ReceivesTheCallerOwnedScratchInstance()
        {
            // The per-sub-bundle hook contract: it reads the scratch the caller owns (the replay recorder copies
            // out of it before the next tick overwrites it). If an internal copy re-appeared, pooling would be
            // silently defeated — pin the reference identity.
            var (world, p1, p2) = World();
            byte[] merged = BuildMerged(1u,
                new[] { Move(p1, 1, 1) }, new[] { Move(p2, 2, 2) }, out int len);
            var (f, c, o) = Scratch();

            int hookCalls = 0; bool sawCallerArray = true;
            MergedTickApplier.Apply(merged, len, world, f, c, o,
                onSubBundle: (faction, buf, baseIdx, count) =>
                {
                    hookCalls++;
                    sawCallerArray &= ReferenceEquals(buf, o);
                });

            Assert.Equal(2, hookCalls);
            Assert.True(sawCallerArray, "onSubBundle must receive the caller-owned scratchOrdersFlat instance");
        }

        [Fact]
        public void PooledOverload_EmptyOrMalformed_IsNoOp()
        {
            // The seeded bootstrap-gap path (len 0) and garbage input must stay deterministic no-ops through the
            // pooled overload — even with poisoned scratch (a failed decode must never apply residue).
            var (world, u, _) = World();
            FixedVec3 before = world.MoveTarget[u];
            var (f, c, o) = Scratch();
            Poison(f, c, o);

            MergedTickApplier.Apply(Array.Empty<byte>(), 0, world, f, c, o);                          // empty
            MergedTickApplier.Apply(new byte[MergedTickPacket.MERGED_MAX_BYTES], 0, world, f, c, o);  // len 0
            var garbage = new byte[3]; garbage[0] = 0xFF;
            MergedTickApplier.Apply(garbage, garbage.Length, world, f, c, o);                          // wrong type

            Assert.Equal(before.X, world.MoveTarget[u].X);
            Assert.Equal(before.Z, world.MoveTarget[u].Z);
        }
    }
}
