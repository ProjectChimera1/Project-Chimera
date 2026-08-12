#nullable enable
using System.Collections.Generic;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>Story 15-1 — the server-side retained merged-tick log (the reconnect tail source). Godot-free.</summary>
    public class MergedTickLogTests
    {
        private static byte[] Frame(byte fill, int len = 16)
        {
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = fill;
            return b;
        }

        [Fact]
        public void Disarmed_RetainsNothing()
        {
            var log = new MergedTickLog();
            log.Append(100, Frame(1), 16);
            Assert.Equal(-1, log.FirstRetainedTick);
            Assert.False(log.TryCopyRange(100, new List<byte[]>()));
        }

        [Fact]
        public void Armed_RetainsInOrder_AndServicesTails()
        {
            var log = new MergedTickLog();
            log.Arm();
            for (long t = 500; t < 510; t++) log.Append(t, Frame((byte)t), 16);

            Assert.Equal(500, log.FirstRetainedTick);
            Assert.Equal(510, log.EndTick);

            var tail = new List<byte[]>();
            Assert.True(log.TryCopyRange(505, tail));      // snapshot at 504 → tail 505..509
            Assert.Equal(5, tail.Count);
            Assert.Equal(unchecked((byte)505), tail[0][0]);
            Assert.Equal(unchecked((byte)509), tail[4][0]);

            Assert.True(log.TryCopyRange(510, tail));      // snapshot at the frontier → empty tail is VALID
            Assert.Empty(tail);
        }

        [Fact]
        public void TailOlderThanRetention_IsRefusedWhole_NeverPartial()
        {
            var log = new MergedTickLog();
            log.Arm();
            for (long t = 500; t < 510; t++) log.Append(t, Frame((byte)t), 16);
            var tail = new List<byte[]>();
            Assert.False(log.TryCopyRange(499, tail));     // pre-retention snapshot → refuse (re-snapshot fresher)
            Assert.Empty(tail);
            Assert.False(log.TryCopyRange(511, tail));     // past the frontier → refuse (nonsensical request)
        }

        [Fact]
        public void GappedAppend_ThrowsLoudly()
        {
            var log = new MergedTickLog();
            log.Arm();
            log.Append(500, Frame(1), 16);
            Assert.Throws<System.InvalidOperationException>(() => log.Append(502, Frame(2), 16));
        }

        [Fact]
        public void ByteBudgetExceeded_DisarmsAndClears_FailClosed()
        {
            var log = new MergedTickLog();
            log.Arm();
            var big = new byte[1024 * 1024];
            long t = 0;
            // 65 MB of frames crosses the 64 MB budget → the log clears itself rather than growing unbounded.
            for (int i = 0; i < 65; i++) log.Append(t++, big, big.Length);
            Assert.False(log.Armed);
            Assert.Equal(-1, log.FirstRetainedTick);
            Assert.Equal(0, log.Bytes);
        }

        [Fact]
        public void AppendCopies_TheCallerBufferIsReusable()
        {
            var log = new MergedTickLog();
            log.Arm();
            var scratch = Frame(7);
            log.Append(0, scratch, 16);
            scratch[0] = 99; // the server reuses its scratch broadcast buffer every tick
            var tail = new List<byte[]>();
            Assert.True(log.TryCopyRange(0, tail));
            Assert.Equal(7, tail[0][0]); // the retained frame is an independent copy
        }
    }
}
