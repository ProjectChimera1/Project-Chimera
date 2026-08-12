#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 15-1 (FR-79 / DW-2, D-2) — the server-side RETAINED merged-tick log that makes reconnect possible.
    ///
    /// <para><b>Why it exists.</b> <see cref="MergedTickBuilder"/> is a 64-tick ring: resolved ticks are
    /// discarded, so the server has NOTHING to hand a rejoining client (the falsified "server buffers the
    /// command stream" premise the 15.1 HALT recorded). This log retains every emitted merged FRAME — the exact
    /// broadcast bytes, which are byte-identical to a <c>.chmr</c> replay body frame — from the moment retention
    /// is armed, so a rejoiner can fast-forward <c>snapshotTick+1 .. EmittedThrough</c> and arrive at the live
    /// tick bit-exact.</para>
    ///
    /// <para><b>Retention policy (D-2).</b> Armed at FREEZE-COMMIT (the first moment a rejoin becomes possible),
    /// not at tick 0 — bounded memory, and only frozen-slot matches ever need a tail. Disarmed + cleared when the
    /// last frozen slot resumes or the match ends. A tail request older than <see cref="FirstRetainedTick"/> is
    /// refused (<see cref="TryCopyRange"/> false) — the caller re-requests a FRESHER snapshot instead (the
    /// snapshot donor is live; there is always a newer boundary), never a partial tail.</para>
    ///
    /// <para><b>Memory honesty.</b> A frame is the merged broadcast for one tick: ~7-byte header + one
    /// sub-bundle header per faction + 14 bytes per order. An idle tick with 2 players ≈ 11 bytes; a busy one a
    /// few hundred. An hour of frozen-slot play at 30 tps ≈ 108k frames ≈ single-digit MB — retained only while
    /// a slot is actually away. <see cref="ByteBudget"/> is the fail-closed cap: past it the log DISARMS and
    /// clears (rejoin then reports "away too long" rather than the server exhausting memory).</para>
    ///
    /// <para>Pure C#, deterministic-free zone (server-side bookkeeping — never a sim or checksum input).</para>
    /// </summary>
    public sealed class MergedTickLog
    {
        /// <summary>Fail-closed retention cap (bytes across retained frames). 64 MB ≈ many hours of tail —
        /// far past any reasonable "away"; the cap exists so a forgotten frozen slot can never exhaust the
        /// server, not as a gameplay bound.</summary>
        public const long ByteBudget = 64L * 1024 * 1024;

        private readonly List<byte[]> _frames = new();
        private long _firstTick = -1;   // tick of _frames[0] (-1 = not armed / empty)
        private long _bytes;
        private bool _armed;

        /// <summary>True while retention is armed (some slot is frozen and a rejoin may need a tail).</summary>
        public bool Armed => _armed;

        /// <summary>The oldest retained tick, or -1 when empty. A snapshot must be taken at
        /// <c>tick &gt;= FirstRetainedTick - 1</c> for its tail to be serviceable.</summary>
        public long FirstRetainedTick => _frames.Count == 0 ? -1 : _firstTick;

        /// <summary>One past the newest retained tick, or -1 when empty (ticks are contiguous by construction —
        /// <see cref="Append"/> is fed from the builder's in-order emit).</summary>
        public long EndTick => _frames.Count == 0 ? -1 : _firstTick + _frames.Count;

        /// <summary>Total retained bytes (the <see cref="ByteBudget"/> accounting).</summary>
        public long Bytes => _bytes;

        /// <summary>Arm retention (idempotent). Frames appended while armed are retained.</summary>
        public void Arm() => _armed = true;

        /// <summary>Disarm and drop everything (last frozen slot resumed / match over / budget exceeded).</summary>
        public void DisarmAndClear()
        {
            _armed = false;
            _frames.Clear();
            _firstTick = -1;
            _bytes = 0;
        }

        /// <summary>
        /// Retain one emitted merged frame (the EXACT broadcast bytes for <paramref name="tick"/>). No-op when
        /// disarmed. Ticks must arrive in order with no gaps (the builder emits in order; a gap or regression is
        /// a programmer error and throws — a silently mis-indexed tail would fast-forward a rejoiner into a
        /// desync, which is worse than a loud server fault). Exceeding <see cref="ByteBudget"/> disarms + clears
        /// (fail-closed: "away too long", never memory exhaustion).
        /// </summary>
        public void Append(long tick, byte[] frame, int length)
        {
            if (!_armed) return;
            if (_frames.Count == 0) _firstTick = tick;
            else if (tick != _firstTick + _frames.Count)
                throw new InvalidOperationException(
                    $"MergedTickLog.Append tick {tick} is not contiguous (expected {_firstTick + _frames.Count}) — " +
                    "a gapped tail would fast-forward a rejoiner into a desync.");

            var copy = new byte[length];
            Array.Copy(frame, copy, length);
            _frames.Add(copy);
            _bytes += length;
            if (_bytes > ByteBudget) DisarmAndClear();
        }

        /// <summary>
        /// Copy the tail <c>[fromTick, EndTick)</c> into <paramref name="outFrames"/>. False (and empty output)
        /// when the log cannot service the range — not armed, empty, or <paramref name="fromTick"/> older than
        /// <see cref="FirstRetainedTick"/> (the caller re-requests a fresher snapshot; a PARTIAL tail is never
        /// returned). <paramref name="fromTick"/> == <see cref="EndTick"/> yields true with zero frames (a
        /// snapshot taken at the very frontier needs no tail).
        /// </summary>
        public bool TryCopyRange(long fromTick, List<byte[]> outFrames)
        {
            outFrames.Clear();
            if (_frames.Count == 0) return false;
            if (fromTick < _firstTick) return false;
            if (fromTick > EndTick) return false;
            for (long t = fromTick; t < EndTick; t++)
                outFrames.Add(_frames[(int)(t - _firstTick)]);
            return true;
        }
    }
}
