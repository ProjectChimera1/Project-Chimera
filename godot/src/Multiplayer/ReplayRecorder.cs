using System;
using System.IO;
using ProjectChimera.Core;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Records the command stream of a live match to a binary file for later replay ("replay v2", format v4).
    ///
    /// File format (v4):
    ///   Header:  magic(4) + version(2) + scenarioPathLen(2) + scenarioPath(UTF8) + seed(8)
    ///            + scenarioHash(8) + rulesetHash(8) + modelAlgoVersion(4) + factionCount(2) + roster(factionCount)
    ///   Body:    a stream of length-framed records: frameLen(2 LE) + frame[frameLen], terminated by frameLen == 0.
    ///            frame[0] self-discriminates:
    ///              0x14 (<see cref="PacketType.TickCommandsMerged"/>) — one full <see cref="MergedTickPacket"/> per
    ///                   recorded tick (sub-bundles ascending by faction id — the canonical apply order),
    ///              0x1A (<see cref="FRAME_TRAILER"/>) — the result trailer:
    ///                   type(1) + winnerFaction(1) + finalTick(4 LE) + completed(1).
    ///
    /// The tagged body reuses the frozen Story-9.3 <see cref="MergedTickPacket"/> codec verbatim (no wire change) —
    /// the same envelope co-designed to be shared across the merged packet, the DSL record, and replay v2. The
    /// recorder buffers a tick's per-faction sub-bundles and flushes one merged frame per tick, so record→replay
    /// reproduces byte-identical SimChecksums (D6). Header scenario/ruleset/algo fields let playback re-gate the
    /// replay against the loaded scenario before the first tick (fail-closed, mirroring <c>HandshakeGate.CheckStart</c>).
    /// </summary>
    public sealed class ReplayRecorder : IDisposable
    {
        // ── File header constants ─────────────────────────────────────────────────

        /// <summary>Four-byte magic: "CHMR" (Chimera Replay).</summary>
        public const uint   MAGIC   = 0x524D4843u; // 'C','H','M','R' LE

        // v2 (Story 1.5): header carries the 8-byte match-start SimRng seed.
        // v3 (Story 7.9): the command stream may carry UnitCommand.DslEvent orders — no format/stride change.
        // v4 (Story 9.11 "replay v2"): SELF-DESCRIBING tagged body built from the frozen MergedTickPacket envelope
        //   plus a result trailer, and a header that embeds the canonical scenario hash, ruleset hash, model
        //   algo-version, faction count, and roster. v1/v2/v3 files carry no scenario hash so the playback re-gate
        //   invariant cannot hold for them → they are HARD-REJECTED by ReplayPlayer ("re-record") — see Design Notes.
        // v5 (Story 15.11, DW-280): the UnitOrder wire stride widened 11→12 (the ability slot moved to its own byte so
        //   a CastAbility can carry a ground point). A v4 body decoded at the v5 stride would misalign every order after
        //   the first — so v4 (and earlier) are HARD-REJECTED at the version gate ("please re-record"), never decoded.
        // v6 (Story 15-23, DW-775): SEMANTIC change, identical stride — entity-target order payloads
        //   (AttackTarget/Follow TargetX; TargetUnit CastAbility TargetZ) are PACKED generation-stamped refs, packed
        //   at issue. A v5 body replayed on a v6 build would re-interpret raw ids as gen-0 packed refs and diverge
        //   on any recycled-slot target — HARD-REJECTED at the version gate ("please re-record"), never decoded.
        // v7 (DW-945): the UnitOrder stride widened 12→14 (4-byte PACKED subject ref). A v6 body decoded at the
        //   v7 stride would misalign every order after the first — HARD-REJECTED at the version gate, never decoded.
        public const ushort VERSION = 7;

        /// <summary>Legacy (pre-v4) EOF sentinel — retained only so the hard-reject tests can hand-write old headers.</summary>
        public const uint EOF_SENTINEL = 0xFFFFFFFFu;

        /// <summary>Body frame discriminator for the replay result trailer (distinct from the 0x14 merged-tick frame).</summary>
        public const byte FRAME_TRAILER = 0x1A;

        /// <summary>Trailer frame payload size: type(1) + winnerFaction(1) + finalTick(4) + completed(1).</summary>
        public const int TRAILER_BYTES = 7;

        // ── State ─────────────────────────────────────────────────────────────────

        private readonly BinaryWriter _writer;
        private bool _closed;

        // Per-tick sub-bundle accumulation (flushed as one MergedTickPacket frame on tick advance / Close). Sized to
        // the frozen MergedTickPacket ceilings so a full tick always fits.
        private readonly Faction[]   _bufFactions   = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly int[]       _bufOrderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly UnitOrder[] _bufOrdersFlat  = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
        private int  _bufCount;
        private uint _bufTick;
        private bool _hasBuffered;

        // Sorted scratch (ascending by faction id) + the serialized-frame buffer.
        private readonly Faction[]   _sortFactions   = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly int[]       _sortOrderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly UnitOrder[] _sortOrdersFlat  = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
        private readonly byte[]      _frameBuf        = new byte[MergedTickPacket.MERGED_MAX_BYTES];

        public string FilePath     { get; }
        public string ScenarioPath { get; }

        /// <summary>The match-start SimRng seed embedded in the header (D6 — restored on playback).</summary>
        public ulong Seed { get; }

        /// <summary>The canonical scenario model hash embedded in the header (the playback re-gate value).</summary>
        public ulong ScenarioHash { get; }

        /// <summary>The ruleset (Effect-Graph caps) hash embedded in the header.</summary>
        public ulong RulesetHash { get; }

        /// <summary>This build's <c>CanonicalModelHash.AlgoVersion</c> at record time (forward-incompatibility guard).</summary>
        public int ModelAlgoVersion { get; }

        /// <summary>The per-slot roster embedded in the header (roster[i] = the faction in slot i).</summary>
        public Faction[] Roster { get; }

        /// <summary>Number of player slots (== <see cref="Roster"/> length).</summary>
        public int FactionCount => Roster.Length;

        /// <summary>Highest tick recorded so far (written into the result trailer).</summary>
        public uint FinalTick { get; private set; }

        // ── Construction ──────────────────────────────────────────────────────────

        /// <param name="filePath">Absolute or Godot user:// path to write to.</param>
        /// <param name="scenarioPath">The scenario that was loaded — stored for playback.</param>
        /// <param name="seed">The match-START SimRng seed (the value passed to <c>world.Rng.Seed</c> at
        /// match start). A lockstep replay regenerates the whole stream from this origin (D6).</param>
        /// <param name="scenarioHash">The canonical scenario model hash (<c>CanonicalModelHash.Compute</c>) —
        /// re-gated against the loaded scenario before the first replayed tick.</param>
        /// <param name="rulesetHash">The ruleset (Effect-Graph caps) hash (<c>RulesetHash.Compute</c>).</param>
        /// <param name="modelAlgoVersion">This build's <c>CanonicalModelHash.AlgoVersion</c> — a replay recorded on a
        /// newer algo is forward-incompatible and rejected on load.</param>
        /// <param name="roster">The per-slot faction roster (roster[i] = the faction assigned to slot i).</param>
        public ReplayRecorder(string filePath, string scenarioPath, ulong seed,
            ulong scenarioHash, ulong rulesetHash, int modelAlgoVersion, Faction[] roster)
        {
            FilePath         = filePath;
            ScenarioPath     = scenarioPath;
            Seed             = seed;
            ScenarioHash     = scenarioHash;
            RulesetHash      = rulesetHash;
            ModelAlgoVersion = modelAlgoVersion;
            Roster           = roster ?? Array.Empty<Faction>();

            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);

            WriteHeader(scenarioPath);
        }

        // ── Recording ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Record one faction's orders for a tick. Skips silently if count == 0 (empty ticks cost nothing).
        /// Called once per faction per tick (ascending by faction id, from the single authoritative merged stream).
        /// The recorder buffers the tick's sub-bundles and flushes one <see cref="MergedTickPacket"/> frame when the
        /// tick advances (or on <see cref="Close(int,bool)"/>). Throws <see cref="InvalidOperationException"/>
        /// (fail loud, never silently drop) on EITHER frozen-envelope ceiling: a single tick accumulating more than
        /// <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> sub-bundles (DW-432), or a single sub-bundle
        /// carrying more than <see cref="TickCommandPacket.MAX_ORDERS"/> orders (DW-604).
        /// </summary>
        public void RecordTick(uint tick, Faction faction, UnitOrder[] buf, int baseIdx, int count)
        {
            if (_closed || count <= 0) return;

            // Tick advanced — flush the previous tick's accumulated sub-bundles as one merged frame.
            if (_hasBuffered && tick != _bufTick)
                FlushTick();

            if (!_hasBuffered)
            {
                _bufTick     = tick;
                _hasBuffered = true;
                _bufCount    = 0;
            }

            // DW-432: the recorder's stated invariant is "never silently discard", so a tick accumulating more
            // per-faction sub-bundles than the frozen MergedTickPacket envelope carries must fail LOUD — the
            // pre-fix silent `return` would drop the overflowing faction's orders and write a divergent replay
            // (the exact silent-drop class the v4 format is fail-closed against). Unreachable in ≤8-slot play
            // (MERGED_MAX_SUBBUNDLES == FactionRegistry.PLAYER_COUNT and the merged stream feeds one sub-bundle
            // per faction per tick), so this is a tripwire for a future >8-slot mode, never a live branch.
            if (_bufCount >= MergedTickPacket.MERGED_MAX_SUBBUNDLES)
                throw new InvalidOperationException(
                    $"ReplayRecorder: tick {tick} accumulated more than {MergedTickPacket.MERGED_MAX_SUBBUNDLES} " +
                    "per-faction sub-bundles — refusing to silently drop orders from the recording " +
                    "(RecordTick is called once per faction per tick from the merged stream).");
            // DW-604: the SAME "never silently discard" invariant on the adjacent ceiling. The pre-fix line silently
            // clamped `count` to MAX_ORDERS, truncating the tail of an over-long sub-bundle — the recording would
            // then diverge from the live match at the first dropped order, with no error anywhere. Unreachable on
            // the live path today (MergedTickPacket.TryRead rejects a sub-bundle with count > MAX_ORDERS outright,
            // before MergedTickApplier fires the record hook), so this is a tripwire for a future caller that feeds
            // the recorder from somewhere other than the validated merged stream — never a live branch.
            if (count > TickCommandPacket.MAX_ORDERS)
                throw new InvalidOperationException(
                    $"ReplayRecorder: tick {tick} sub-bundle for faction {faction} carries {count} orders, past the " +
                    $"frozen {TickCommandPacket.MAX_ORDERS}-order TickCommandPacket ceiling — refusing to silently " +
                    "truncate orders out of the recording.");

            int slot = _bufCount;
            _bufFactions[slot]    = faction;
            _bufOrderCounts[slot] = count;
            int dst = slot * TickCommandPacket.MAX_ORDERS;
            for (int i = 0; i < count; i++)
                _bufOrdersFlat[dst + i] = buf[baseIdx + i];
            _bufCount++;

            if (tick > FinalTick) FinalTick = tick;
        }

        // ── Finalisation ──────────────────────────────────────────────────────────

        /// <summary>Finalise as an INCOMPLETE recording (no winner) — used on return-to-Edit / Dispose.</summary>
        public void Close() => Close(0, completed: false);

        /// <summary>
        /// Finalise the replay file: flush any buffered tick, write the result trailer, then the frame-length EOF (0).
        /// Safe to call multiple times.
        /// </summary>
        /// <param name="winnerFaction">The winning faction id (1-based player number; 0 = no victor / incomplete).</param>
        /// <param name="completed">True when the match reached a resolved end; false for an interrupted recording.</param>
        public void Close(int winnerFaction, bool completed)
        {
            if (_closed) return;
            _closed = true;

            if (_hasBuffered) FlushTick();

            // Result-trailer frame: frameLen + [type + winnerFaction + finalTick + completed].
            _writer.Write((ushort)TRAILER_BYTES);
            _writer.Write(FRAME_TRAILER);
            _writer.Write((byte)(winnerFaction < 0 ? 0 : winnerFaction));
            _writer.Write(FinalTick);
            _writer.Write((byte)(completed ? 1 : 0));

            // Frame-length EOF.
            _writer.Write((ushort)0);

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

            _writer.Write(Seed);             // 8 — match-start SimRng seed (D6 — restored by ReplayPlayer)
            _writer.Write(ScenarioHash);     // 8 — canonical scenario model hash (re-gate value)
            _writer.Write(RulesetHash);      // 8 — Effect-Graph caps ruleset hash
            _writer.Write(ModelAlgoVersion); // 4 — CanonicalModelHash.AlgoVersion at record time
            _writer.Write((ushort)Roster.Length);
            foreach (var f in Roster)
                _writer.Write((byte)f);
        }

        /// <summary>Emit the buffered tick's sub-bundles as ONE length-framed <see cref="MergedTickPacket"/> frame,
        /// sub-bundles sorted ascending by faction id (the canonical apply order the live merged path uses).</summary>
        private void FlushTick()
        {
            // Sort sub-bundle slots ascending by faction into the sorted scratch (input is already ascending from the
            // merged stream, but sorting keeps the frame canonical regardless of call order).
            for (int a = 0; a < _bufCount; a++)
            {
                int min = a;
                for (int b = a + 1; b < _bufCount; b++)
                    if ((byte)_bufFactions[b] < (byte)_bufFactions[min]) min = b;
                if (min != a)
                {
                    (_bufFactions[a], _bufFactions[min])       = (_bufFactions[min], _bufFactions[a]);
                    (_bufOrderCounts[a], _bufOrderCounts[min]) = (_bufOrderCounts[min], _bufOrderCounts[a]);
                    int ia = a * TickCommandPacket.MAX_ORDERS, im = min * TickCommandPacket.MAX_ORDERS;
                    for (int k = 0; k < TickCommandPacket.MAX_ORDERS; k++)
                        (_bufOrdersFlat[ia + k], _bufOrdersFlat[im + k]) = (_bufOrdersFlat[im + k], _bufOrdersFlat[ia + k]);
                }
            }

            // Copy into the write scratch (MergedTickPacket.Write reads faction/count/orders by contiguous slot).
            for (int b = 0; b < _bufCount; b++)
            {
                _sortFactions[b]    = _bufFactions[b];
                _sortOrderCounts[b] = _bufOrderCounts[b];
                int src = b * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < _bufOrderCounts[b]; i++)
                    _sortOrdersFlat[src + i] = _bufOrdersFlat[src + i];
            }

            int len = MergedTickPacket.Write(_frameBuf, _bufTick, _sortFactions, _sortOrderCounts, _sortOrdersFlat, _bufCount);
            _writer.Write((ushort)len);
            _writer.Write(_frameBuf, 0, len);

            _hasBuffered = false;
            _bufCount    = 0;
        }
    }
}
