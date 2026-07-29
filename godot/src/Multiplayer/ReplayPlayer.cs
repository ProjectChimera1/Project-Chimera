#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // CanonicalModelHash.AlgoVersion (forward-incompatibility guard)

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Plays back a recorded match (format v4, "replay v2") by feeding the stored command stream through the
    /// deterministic simulation in place of live network input.
    ///
    /// Drop-in replacement for the LockstepManager's online Flush() path:
    ///   <c>if (_replayPlayer?.Flush(tick) == true) _simLoop.StepOnce();</c>
    ///
    /// Applies stored orders directly to EntityWorld through the SHARED <see cref="OrderApplier.Apply"/> — the same
    /// step the live path uses — so replay stays byte-identical to the recording. The v4 body is a stream of
    /// length-framed records decoded via the frozen <see cref="MergedTickPacket.TryRead"/> (0x14) plus a result
    /// trailer (0x1A). Pre-v4 files carry no scenario hash so the playback re-gate cannot verify them: they are
    /// HARD-REJECTED ("re-record"), as is a replay whose <c>modelAlgoVersion</c> is newer than this build.
    /// </summary>
    public sealed class ReplayPlayer
    {
        // ── Public info ───────────────────────────────────────────────────────────

        /// <summary>The scenario path embedded in the replay file header.</summary>
        public string ScenarioPath { get; }

        /// <summary>The match-start SimRng seed parsed from the header; the ctor reseeds the world's RNG to it before any tick.</summary>
        public ulong Seed { get; }

        /// <summary>The canonical scenario model hash embedded in the header (the playback re-gate value).</summary>
        public ulong ScenarioHash { get; }

        /// <summary>The ruleset (Effect-Graph caps) hash embedded in the header.</summary>
        public ulong RulesetHash { get; }

        /// <summary>The <c>CanonicalModelHash.AlgoVersion</c> the replay was recorded with.</summary>
        public int ModelAlgoVersion { get; }

        /// <summary>The per-slot roster embedded in the header (roster[i] = the faction in slot i).</summary>
        public Faction[] Roster { get; }

        /// <summary>Number of player slots (== <see cref="Roster"/> length).</summary>
        public int FactionCount => Roster.Length;

        /// <summary>The final tick recorded, read from the result trailer (0 if no trailer present).</summary>
        public uint FinalTick { get; private set; }

        /// <summary>The winning faction id from the result trailer (1-based player number; 0 = no victor / incomplete).</summary>
        public int WinnerFaction { get; private set; }

        /// <summary>True when the recording reached a resolved end (from the result trailer).</summary>
        public bool Completed { get; private set; }

        /// <summary>True once all recorded ticks have been applied.</summary>
        public bool IsFinished { get; private set; }

        /// <summary>Highest tick number recorded in this file. The replay ends when <see cref="Flush"/> reaches it.</summary>
        public uint LastTick => _lastTick;

        /// <summary>Total number of tick-faction records in the file (informational; shown in logs).</summary>
        public int TotalTicks { get; private set; }

        // ── Path-request delegates (mirror LockstepManager) ───────────────────────

        public Action<int, float, float>? OnRequestPath;
        public Action<int, float, float>? OnRequestAttackMove;
        public Action<int>? OnCancelPath;

        public ProjectChimera.Economy.BuildingSystem? Buildings;
        public ProjectChimera.Combat.ItemSystem? Items;
        public ProjectChimera.Economy.ResearchSystem? Research;
        public Func<int, int, int, int, bool>? DslEventSink;
        /// <summary>Story 11.2 (FR-66) — the host's folded WinStateStore, so a recorded Concede order resolves in replay
        /// byte-identically to the live run (the one-switch parity rule). Null ⇒ a Concede is a deterministic no-op.</summary>
        public WinStateStore? WinState;

        // ── Replay data ───────────────────────────────────────────────────────────

        private readonly Dictionary<uint, List<(Faction Faction, UnitOrder[] Orders, int Count)>> _ticks;
        private uint _lastTick;

        private readonly EntityWorld _world;

        // ── Playback re-gate (mirrors HandshakeGate.CheckStart) ──────────────────

        /// <summary>
        /// The fail-closed scenario re-gate: block if the embedded hash is 0, the loaded hash is 0, or they differ —
        /// never play a replay recorded against a different version of the scenario (silent desync). Returns
        /// <c>null</c> to ALLOW, or the human-readable BLOCK reason to surface. Pure — no side effects — so the
        /// policy is Tier-1 unit-testable and shared by <c>MatchLifecycleController.TryLoadReplay</c>.
        /// </summary>
        public static string? ScenarioGateBlockReason(ulong embeddedHash, ulong loadedHash)
        {
            if (embeddedHash == 0UL || loadedHash == 0UL)
                return "CANNOT PLAY — scenario hash not computed!\n" +
                       $"Replay:  0x{embeddedHash:X16}\n" +
                       $"Loaded:  0x{loadedHash:X16}\n" +
                       "A hash of 0 means no validated scenario was applied.";

            if (embeddedHash != loadedHash)
                return "SCENARIO MISMATCH — cannot play this replay!\n" +
                       $"Replay:  0x{embeddedHash:X16}\n" +
                       $"Loaded:  0x{loadedHash:X16}\n" +
                       "This replay was recorded on a different version of the scenario.";

            return null; // equal nonzero — playback allowed
        }

        // ── Construction ──────────────────────────────────────────────────────────

        /// <summary>
        /// Load a v4 replay file. Throws <see cref="InvalidDataException"/> if the file is corrupt, is a pre-v4
        /// format (hard-rejected — "please re-record"), or was recorded on a newer model algo-version.
        /// </summary>
        public ReplayPlayer(string filePath, EntityWorld world)
        {
            _world = world;
            _ticks = new Dictionary<uint, List<(Faction, UnitOrder[], int)>>(capacity: 512);
            _lastTick = 0;
            Roster = Array.Empty<Faction>();

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

            // ── Parse header ─────────────────────────────────────────────────────
            Require(stream, sizeof(uint) + sizeof(ushort), filePath);
            uint magic = reader.ReadUInt32();
            if (magic != ReplayRecorder.MAGIC)
                throw new InvalidDataException($"Replay: bad magic 0x{magic:X8} in '{filePath}'");

            ushort version = reader.ReadUInt16();
            // v4 ("replay v2") is the sole supported format: v1/v2/v3 carry no embedded scenario hash, so the
            // fail-closed playback re-gate cannot verify them — hard-reject with an explanatory message.
            if (version < ReplayRecorder.VERSION)
                throw new InvalidDataException(
                    $"Replay: '{filePath}' was recorded on an older replay format (v{version}) — please re-record.");
            if (version > ReplayRecorder.VERSION)
                throw new InvalidDataException(
                    $"Replay: '{filePath}' was recorded on a newer replay format (v{version}) than this build supports.");

            Require(stream, sizeof(ushort), filePath);
            ushort pathLen = reader.ReadUInt16();
            Require(stream, pathLen, filePath);
            ScenarioPath = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(pathLen));

            // seed(8) + scenarioHash(8) + rulesetHash(8) + modelAlgoVersion(4) + factionCount(2)
            Require(stream, sizeof(ulong) * 3 + sizeof(int) + sizeof(ushort), filePath);
            Seed             = reader.ReadUInt64();
            ScenarioHash     = reader.ReadUInt64();
            RulesetHash      = reader.ReadUInt64();
            ModelAlgoVersion = reader.ReadInt32();
            ushort factionCount = reader.ReadUInt16();

            // P8: a corrupt header must not drive a huge roster allocation — reject a factionCount past the ceiling.
            if (factionCount > FactionRegistry.PLAYER_COUNT)
                throw new InvalidDataException(
                    $"Replay: '{filePath}' declares {factionCount} factions (max {FactionRegistry.PLAYER_COUNT}) — corrupt header.");

            Require(stream, factionCount, filePath);
            Roster = new Faction[factionCount];
            for (int i = 0; i < factionCount; i++)
            {
                byte b = reader.ReadByte();
                // P8 (follow-up): the factionCount ceiling above bounds the roster SIZE; each roster VALUE must also
                // be a real player slot (Player1..Player{PLAYER_COUNT} == 1..PLAYER_COUNT). A corrupt byte must not
                // become an out-of-range Faction — it flows to Fog.SetViewer on the perspective cycle.
                if (b < 1 || b > FactionRegistry.PLAYER_COUNT)
                    throw new InvalidDataException(
                        $"Replay: '{filePath}' has an out-of-range roster faction {b} " +
                        $"(expected 1..{FactionRegistry.PLAYER_COUNT}) — corrupt header.");
                Roster[i] = (Faction)b;
            }

            // Forward-incompatibility: a replay recorded on a NEWER canonical-model algo cannot be trusted to
            // reproduce this build's sim (the fold changed) — reject rather than desync.
            if (ModelAlgoVersion > CanonicalModelHash.AlgoVersion)
                throw new InvalidDataException(
                    $"Replay: '{filePath}' was recorded on a newer model format (algo v{ModelAlgoVersion} > " +
                    $"v{CanonicalModelHash.AlgoVersion}) — please update to play it.");

            // Restore the stream origin BEFORE the first tick (D6 — seed only, not per-tick state).
            _world.Rng.Seed(Seed);

            // ── Parse tagged body ─────────────────────────────────────────────────
            var outFactions   = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outOrderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var outOrdersFlat  = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];

            while (stream.Length - stream.Position >= sizeof(ushort))
            {
                ushort frameLen = reader.ReadUInt16();
                if (frameLen == 0) break; // frame-length EOF

                if (stream.Length - stream.Position < frameLen)
                    throw new InvalidDataException($"Replay: truncated frame in '{filePath}'");

                byte[] frame = reader.ReadBytes(frameLen);
                if (frame.Length < frameLen)
                    throw new InvalidDataException($"Replay: truncated frame in '{filePath}'");

                byte type = frame[0];
                if (type == (byte)PacketType.TickCommandsMerged)
                {
                    if (!MergedTickPacket.TryRead(frame, frameLen, out uint tick,
                            outFactions, outOrderCounts, outOrdersFlat, out int subBundleCount))
                        // P3: a well-framed but internally-corrupt merged frame is fail-closed — never silently
                        // dropped (that is the exact silent-desync class this format eliminates).
                        throw new InvalidDataException($"Replay: corrupt merged frame in '{filePath}'");

                    for (int b = 0; b < subBundleCount; b++)
                    {
                        int count = outOrderCounts[b];
                        var orders = new UnitOrder[count];
                        int src = b * TickCommandPacket.MAX_ORDERS;
                        for (int i = 0; i < count; i++)
                            orders[i] = outOrdersFlat[src + i];

                        if (!_ticks.TryGetValue(tick, out var list))
                        {
                            list = new List<(Faction, UnitOrder[], int)>(capacity: 2);
                            _ticks[tick] = list;
                        }
                        list.Add((outFactions[b], orders, count));
                        TotalTicks++;
                    }
                    if (tick > _lastTick) _lastTick = tick;
                }
                else if (type == ReplayRecorder.FRAME_TRAILER)
                {
                    // P3 (follow-up): a trailer frame shorter than its fixed payload is corruption — fail closed like
                    // the corrupt-merged / unknown-type branches, never silently ignore it (a dropped trailer is a
                    // replay with no result and no error, the same silent-desync class this format eliminates).
                    if (frameLen < ReplayRecorder.TRAILER_BYTES)
                        throw new InvalidDataException($"Replay: truncated result trailer in '{filePath}'");
                    WinnerFaction = frame[1];
                    FinalTick     = (uint)(frame[2] | (frame[3] << 8) | (frame[4] << 16) | (frame[5] << 24));
                    Completed     = frame[6] != 0;
                }
                else
                {
                    // P3: within this pinned VERSION an unrecognized frame type is corruption — fail closed rather
                    // than silently skip (a skipped frame is a divergent replay with no error).
                    throw new InvalidDataException($"Replay: unrecognized frame type 0x{type:X2} in '{filePath}'");
                }
            }
        }

        /// <summary>Throw <see cref="InvalidDataException"/> (not a raw EndOfStream) if fewer than
        /// <paramref name="n"/> bytes remain — a truncated header/frame is a corrupt file.</summary>
        private static void Require(FileStream stream, long n, string filePath)
        {
            if (stream.Length - stream.Position < n)
                throw new InvalidDataException($"Replay: truncated header in '{filePath}'");
        }

        // ── Playback ──────────────────────────────────────────────────────────────

        /// <summary>Apply all stored orders for <paramref name="tick"/> to EntityWorld. Always returns true —
        /// replay never stalls waiting for a peer.</summary>
        public bool Flush(uint tick)
        {
            if (_ticks.TryGetValue(tick, out var entries))
            {
                foreach (var (faction, orders, count) in entries)
                    ApplyOrders(orders, count, faction);
            }

            if (tick >= _lastTick)
                IsFinished = true;

            return true;
        }

        // ── Order application (the SHARED OrderApplier — replay-vs-live parity by construction) ──

        private void ApplyOrders(UnitOrder[] orders, int count, Faction expectedFaction)
        {
            for (int i = 0; i < count; i++)
                OrderApplier.Apply(_world, in orders[i], expectedFaction,
                    OnRequestPath, OnRequestAttackMove, OnCancelPath, Buildings, null, Items, Research, DslEventSink, WinState);
        }
    }
}
