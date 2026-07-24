#nullable enable
using System;
using System.IO;
using ProjectChimera.Core;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// A lightweight, never-throwing reader of a v4 <c>.chmr</c> replay's HEADER metadata (plus a scan for the
    /// result trailer) — for the replay browser list, which must render a row per file WITHOUT a full parse and
    /// WITHOUT crashing on a legacy/corrupt file. A pre-v4, bad-magic, or truncated file yields
    /// <see cref="IsPlayable"/> == false (the browser lists it as "unplayable (old format)"); a v4 file yields the
    /// scenario path/hash, roster, duration (<see cref="FinalTick"/>), and result (<see cref="WinnerFaction"/> /
    /// <see cref="Completed"/>). A missing trailer (a crash mid-record) leaves <see cref="Completed"/> false.
    ///
    /// Godot-free (System.* + Core only) so it is Tier-1 unit-testable alongside the recorder/player.
    /// </summary>
    public readonly struct ReplayHeader
    {
        public string    ScenarioPath  { get; }
        public ulong     ScenarioHash  { get; }
        public Faction[] Roster        { get; }
        public int       FactionCount  => Roster?.Length ?? 0;
        public uint      FinalTick     { get; }
        public int       WinnerFaction { get; }
        public bool      Completed     { get; }
        public bool      IsPlayable    { get; }

        private ReplayHeader(string scenarioPath, ulong scenarioHash, Faction[] roster,
            uint finalTick, int winnerFaction, bool completed, bool isPlayable)
        {
            ScenarioPath  = scenarioPath;
            ScenarioHash  = scenarioHash;
            Roster        = roster;
            FinalTick     = finalTick;
            WinnerFaction = winnerFaction;
            Completed     = completed;
            IsPlayable    = isPlayable;
        }

        /// <summary>An unplayable row (legacy / corrupt / unreadable) — <see cref="IsPlayable"/> == false.</summary>
        public static ReplayHeader Unplayable(string scenarioPath = "")
            => new(scenarioPath, 0UL, Array.Empty<Faction>(), 0, 0, false, isPlayable: false);

        /// <summary>Read the header (and scan the body for the result trailer) of <paramref name="path"/>. Never
        /// throws: any error/legacy format returns <see cref="Unplayable"/>.</summary>
        public static ReplayHeader Read(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

                if (stream.Length < 6) return Unplayable();

                uint magic = reader.ReadUInt32();
                if (magic != ReplayRecorder.MAGIC) return Unplayable();

                ushort version = reader.ReadUInt16();
                if (version != ReplayRecorder.VERSION) return Unplayable(); // pre-v4 / newer → unplayable row

                ushort pathLen = reader.ReadUInt16();
                if (stream.Length - stream.Position < pathLen) return Unplayable();
                string scenarioPath = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(pathLen));

                // seed(8) + scenarioHash(8) + rulesetHash(8) + modelAlgoVersion(4) + factionCount(2)
                if (stream.Length - stream.Position < 8 * 3 + 4 + 2) return Unplayable(scenarioPath);
                reader.ReadUInt64();                       // seed (unused here)
                ulong scenarioHash = reader.ReadUInt64();
                reader.ReadUInt64();                       // rulesetHash (unused here)
                reader.ReadInt32();                        // modelAlgoVersion (unused here)
                ushort factionCount = reader.ReadUInt16();

                // P8: a corrupt header must not drive a huge roster allocation.
                if (factionCount > FactionRegistry.PLAYER_COUNT) return Unplayable(scenarioPath);

                if (stream.Length - stream.Position < factionCount) return Unplayable(scenarioPath);
                var roster = new Faction[factionCount];
                for (int i = 0; i < factionCount; i++)
                {
                    // P8 (follow-up): mirror ReplayPlayer's roster-VALUE check. The ceiling above bounds the roster
                    // SIZE; each byte must also be a real player slot (1..PLAYER_COUNT). Without this, a corrupt-roster
                    // file lists as PLAYABLE here while ReplayPlayer's ctor hard-rejects it — the browser would enable
                    // Play on a file that only errors on click. Fail it as an unplayable row to keep the two gates aligned.
                    byte rb = reader.ReadByte();
                    if (rb < 1 || rb > FactionRegistry.PLAYER_COUNT) return Unplayable(scenarioPath);
                    roster[i] = (Faction)rb;
                }

                long bodyStart = stream.Position;

                // ── P10: fast path — a completed recording's result trailer sits at a fixed 11-byte tail before EOF
                //    (frameLen=7 + 7 trailer bytes + frameLen=0). Read it directly instead of scanning every frame;
                //    on any signature mismatch, fall back to the full body scan below. ──
                const int TAIL = sizeof(ushort) + ReplayRecorder.TRAILER_BYTES + sizeof(ushort); // 2 + 7 + 2 = 11
                if (stream.Length - bodyStart >= TAIL)
                {
                    stream.Seek(stream.Length - TAIL, SeekOrigin.Begin);
                    ushort tLen = reader.ReadUInt16();
                    byte[] tf   = reader.ReadBytes(ReplayRecorder.TRAILER_BYTES);
                    ushort eof  = reader.ReadUInt16();
                    if (tLen == ReplayRecorder.TRAILER_BYTES && tf.Length == ReplayRecorder.TRAILER_BYTES
                        && tf[0] == ReplayRecorder.FRAME_TRAILER && eof == 0)
                    {
                        int  fWinner    = tf[1];
                        uint fFinalTick = (uint)(tf[2] | (tf[3] << 8) | (tf[4] << 16) | (tf[5] << 24));
                        bool fCompleted = tf[6] != 0;
                        return new ReplayHeader(scenarioPath, scenarioHash, roster, fFinalTick, fWinner, fCompleted, isPlayable: true);
                    }
                    stream.Seek(bodyStart, SeekOrigin.Begin); // tail wasn't a trailer — full scan
                }

                // ── Full scan for the result trailer; fall back to the max merged tick for duration (a crash
                //    mid-record leaves no trailer). ──
                uint finalTick = 0;
                int  winner    = 0;
                bool completed = false;
                bool haveTrailer = false;

                while (stream.Length - stream.Position >= sizeof(ushort))
                {
                    ushort frameLen = reader.ReadUInt16();
                    if (frameLen == 0) break;
                    if (stream.Length - stream.Position < frameLen) break;
                    byte[] frame = reader.ReadBytes(frameLen);
                    if (frame.Length < frameLen) break;

                    if (frame[0] == ReplayRecorder.FRAME_TRAILER && frameLen >= ReplayRecorder.TRAILER_BYTES)
                    {
                        winner    = frame[1];
                        finalTick = (uint)(frame[2] | (frame[3] << 8) | (frame[4] << 16) | (frame[5] << 24));
                        completed = frame[6] != 0;
                        haveTrailer = true;
                    }
                    else if (frame[0] == (byte)PacketType.TickCommandsMerged
                             && !haveTrailer
                             && MergedTickPacket.TryPeekTick(frame, frameLen, out uint tick)
                             && tick > finalTick)
                    {
                        finalTick = tick; // fallback duration when no trailer was written (crash mid-record)
                    }
                }

                return new ReplayHeader(scenarioPath, scenarioHash, roster, finalTick, winner, completed, isPlayable: true);
            }
            catch
            {
                return Unplayable();
            }
        }
    }
}
