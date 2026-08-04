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
    /// DW-432 — <see cref="ReplayRecorder.RecordTick"/>'s sub-bundle ceiling is fail-LOUD, never a silent drop.
    /// The recorder's stated invariant is "never silently discard"; the pre-fix guard silently <c>return</c>ed when
    /// a tick accumulated more than <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> per-faction sub-bundles,
    /// so a future &gt;8-slot mode would have written a divergent replay (a faction's orders silently omitted) —
    /// the exact silent-drop class the v4 format is fail-closed against everywhere else. Unreachable in ≤8-slot
    /// play (the merged stream feeds one sub-bundle per faction per tick), so these tests drive the recorder
    /// directly to pin the tripwire.
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
    }
}
