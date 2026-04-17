using System;
using System.IO;
using ProjectChimera.Core;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Records the command stream of a live match to a binary file for later replay.
    ///
    /// File format:
    ///   Header:  magic(4) + version(2) + scenarioPathLen(2) + scenarioPath(UTF8)
    ///   Records: For each tick with ≥1 order per faction, zero or more entries of:
    ///              tick(4) + faction(1) + orderCount(1) + [unitId(2)+cmd(1)+tx(4)+tz(4)] * count
    ///   Sentinel: tick = 0xFFFFFFFF (4 bytes) marks EOF.
    ///
    /// Only ticks/factions with at least one order are written — empty ticks cost nothing.
    /// A full 10-minute match at 30 tps with moderate command density is under 200 KB.
    /// </summary>
    public sealed class ReplayRecorder : IDisposable
    {
        // ── File header constants ─────────────────────────────────────────────────

        /// <summary>Four-byte magic: "CHMR" (Chimera Replay).</summary>
        public const uint   MAGIC   = 0x524D4843u; // 'C','H','M','R' LE
        public const ushort VERSION = 1;

        /// <summary>Sentinel written at end-of-file to mark replay completion.</summary>
        public const uint EOF_SENTINEL = 0xFFFFFFFFu;

        // ── State ─────────────────────────────────────────────────────────────────

        private readonly BinaryWriter _writer;
        private uint _ticksWritten;
        private bool _closed;

        public string FilePath     { get; }
        public string ScenarioPath { get; }

        // ── Construction ──────────────────────────────────────────────────────────

        /// <param name="filePath">Absolute or Godot user:// path to write to.</param>
        /// <param name="scenarioPath">The scenario that was loaded — stored for playback.</param>
        public ReplayRecorder(string filePath, string scenarioPath)
        {
            FilePath     = filePath;
            ScenarioPath = scenarioPath;

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);

            WriteHeader(scenarioPath);
        }

        // ── Recording ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Record one faction's orders for a tick.
        /// Skips silently if count == 0 (empty ticks cost nothing in the file).
        /// Call once per faction per tick from <see cref="LockstepManager.Flush"/>.
        /// </summary>
        /// <param name="tick">The simulation tick being executed.</param>
        /// <param name="faction">Which faction issued these orders.</param>
        /// <param name="buf">Flat order buffer (same as LockstepManager's _localBuf/_remoteBuf).</param>
        /// <param name="baseIdx">Start index in buf for this tick's slot.</param>
        /// <param name="count">Number of orders in this slot.</param>
        public void RecordTick(uint tick, Faction faction, UnitOrder[] buf, int baseIdx, int count)
        {
            if (_closed || count <= 0) return;

            _writer.Write(tick);
            _writer.Write((byte)faction);
            _writer.Write((byte)count);

            for (int i = 0; i < count; i++)
            {
                var o = buf[baseIdx + i];
                _writer.Write(o.UnitId);
                _writer.Write((byte)o.Command);
                _writer.Write(o.TargetX);
                _writer.Write(o.TargetZ);
            }

            _ticksWritten++;
        }

        // ── Finalisation ──────────────────────────────────────────────────────────

        /// <summary>
        /// Finalise the replay file — writes EOF sentinel and flushes.
        /// Safe to call multiple times.
        /// </summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;

            _writer.Write(EOF_SENTINEL);
            _writer.Flush();
            _writer.Dispose();
        }

        public void Dispose() => Close();

        // ── Private helpers ────────────────────────────────────────────────────────

        private void WriteHeader(string scenarioPath)
        {
            _writer.Write(MAGIC);
            _writer.Write(VERSION);

            var pathBytes = System.Text.Encoding.UTF8.GetBytes(scenarioPath);
            _writer.Write((ushort)pathBytes.Length);
            _writer.Write(pathBytes);
        }
    }
}
