#nullable enable
using System;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // CanonicalModelHash.AlgoVersion
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-432 / DW-604 — BOTH of <see cref="ReplayRecorder.RecordTick"/>'s frozen-envelope ceilings are fail-LOUD,
    /// never a silent drop. The recorder's stated invariant is "never silently discard"; the pre-fix code silently
    /// <c>return</c>ed when a tick accumulated more than <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/>
    /// per-faction sub-bundles (DW-432) and silently CLAMPED a sub-bundle carrying more than
    /// <see cref="TickCommandPacket.MAX_ORDERS"/> orders (DW-604) — either one writes a divergent replay (a
    /// faction's orders, or an over-long sub-bundle's tail, silently omitted) with no error anywhere, the exact
    /// silent-drop class the v4 format is fail-closed against everywhere else. Both are unreachable on the live
    /// path today (the merged stream feeds one sub-bundle per faction per tick, and
    /// <see cref="MergedTickPacket.TryRead"/> rejects an over-count sub-bundle before the record hook fires), so
    /// these tests drive the recorder directly to pin the tripwires.
    /// </summary>
    public class ReplayRecorderOverflowTests
    {
        private static ReplayRecorder NewRecorder(string path)
            => new(path, "overflow-test", EntityWorld.DEFAULT_RNG_SEED,
                   0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion,
                   new[] { Faction.Player1, Faction.Player2 });

        private static readonly UnitOrder Order =
            new(0, UnitCommand.Move, Fixed.FromInt(5), Fixed.FromInt(7));

        /// <summary>A full tick — exactly MERGED_MAX_SUBBUNDLES (8) per-faction sub-bundles — records fine; the
        /// overflowing 9th call on the SAME tick throws instead of silently dropping the orders.</summary>
        [Fact]
        public void NinthSubBundleOnOneTick_ThrowsInsteadOfSilentlyDropping()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_overflow_{Guid.NewGuid():N}.chmr");
            try
            {
                using var rec = NewRecorder(path);

                // Fill the tick to the frozen envelope ceiling (one sub-bundle per player slot — the real contract).
                for (int slot = 0; slot < MergedTickPacket.MERGED_MAX_SUBBUNDLES; slot++)
                    rec.RecordTick(1, FactionRegistry.ToFaction(slot), new[] { Order }, 0, 1);

                // The overflow is a loud tripwire, not a silent drop (DW-432).
                var ex = Assert.Throws<InvalidOperationException>(
                    () => rec.RecordTick(1, Faction.Player1, new[] { Order }, 0, 1));
                Assert.Contains("sub-bundles", ex.Message);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>The ceiling is PER TICK: after a full 8-sub-bundle tick, the next tick records normally (the
        /// buffered tick flushes on advance), and the file round-trips through <see cref="ReplayPlayer"/> with every
        /// recorded sub-bundle present — nothing was dropped on the way to the ceiling.</summary>
        [Fact]
        public void FullTickThenNextTick_RecordsAndRoundTrips()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_overflow_rt_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var rec = NewRecorder(path))
                {
                    for (int slot = 0; slot < MergedTickPacket.MERGED_MAX_SUBBUNDLES; slot++)
                        rec.RecordTick(2, FactionRegistry.ToFaction(slot), new[] { Order }, 0, 1);
                    rec.RecordTick(3, Faction.Player1, new[] { Order }, 0, 1); // tick advance — no throw
                }

                var player = new ReplayPlayer(path, new EntityWorld());
                // 8 sub-bundles at tick 2 + 1 at tick 3 — TotalTicks counts tick-faction records.
                Assert.Equal(MergedTickPacket.MERGED_MAX_SUBBUNDLES + 1, player.TotalTicks);
                Assert.Equal(3u, player.LastTick);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ── DW-604: the per-sub-bundle ORDER ceiling ─────────────────────────────────

        /// <summary>DW-604 — a sub-bundle carrying more than <see cref="TickCommandPacket.MAX_ORDERS"/> orders
        /// throws instead of being silently clamped to the ceiling. The pre-fix line
        /// (<c>if (count &gt; MAX_ORDERS) count = MAX_ORDERS;</c>) dropped the tail with no error, so the recording
        /// diverged from the live match at the first truncated order — the same class DW-432 closed one line up.</summary>
        [Fact]
        public void SubBundleOverMaxOrders_ThrowsInsteadOfSilentlyClamping()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_orders_{Guid.NewGuid():N}.chmr");
            try
            {
                using var rec = NewRecorder(path);

                var tooMany = new UnitOrder[TickCommandPacket.MAX_ORDERS + 1];
                for (int i = 0; i < tooMany.Length; i++)
                    tooMany[i] = new UnitOrder((ushort)i, UnitCommand.Move, Fixed.FromInt(i), Fixed.FromInt(-i));

                var ex = Assert.Throws<InvalidOperationException>(
                    () => rec.RecordTick(1, Faction.Player1, tooMany, 0, tooMany.Length));
                Assert.Contains("orders", ex.Message);
                Assert.Contains(TickCommandPacket.MAX_ORDERS.ToString(), ex.Message);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>DW-604 boundary — EXACTLY <see cref="TickCommandPacket.MAX_ORDERS"/> orders is legal (the throw
        /// must not fire one short of the ceiling) and every one of them survives the round-trip: each of the 32
        /// recorded Move orders reaches its own unit on playback, so nothing was quietly trimmed on the way in.</summary>
        [Fact]
        public void ExactlyMaxOrders_RecordsAndEveryOrderRoundTrips()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_orders_max_{Guid.NewGuid():N}.chmr");
            try
            {
                const int N = TickCommandPacket.MAX_ORDERS;

                using (var rec = NewRecorder(path))
                {
                    var full = new UnitOrder[N];
                    for (int i = 0; i < N; i++)
                        full[i] = new UnitOrder((ushort)i, UnitCommand.Move, Fixed.FromInt(i + 1), Fixed.FromInt(-(i + 1)));
                    rec.RecordTick(1, Faction.Player1, full, 0, N); // no throw at the ceiling
                }

                var world = new EntityWorld();
                for (int i = 0; i < N; i++)
                    Assert.Equal(i, world.Create(FixedVec3.Zero, Faction.Player1,
                                                 Fixed.FromInt(100), Fixed.FromInt(3)));

                var player = new ReplayPlayer(path, world);
                player.Flush(1);

                for (int i = 0; i < N; i++)
                {
                    Assert.Equal(Fixed.FromInt(i + 1).Raw,    world.MoveTarget[i].X.Raw);
                    Assert.Equal(Fixed.FromInt(-(i + 1)).Raw, world.MoveTarget[i].Z.Raw);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
