#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // CanonicalModelHash.AlgoVersion
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.5 (AC2 + AC3) / Story 9.11 ("replay v2", format v4) — proves the shared <see cref="SimRng"/> is
    /// (a) folded into <see cref="SimChecksum"/>, (b) recorded by <see cref="ReplayRecorder"/> + restored by
    /// <see cref="ReplayPlayer"/> so a replay regenerates the identical stream, and (c) drives reproducible per-tick
    /// checksums across two live runs AND across a record→replay round-trip. Story 9.11 additionally proves the v4
    /// tagged-body / header round-trip, the fail-closed scenario re-gate, the pre-v4 + forward-algo hard-rejects,
    /// the result trailer, and the lightweight header reader.
    ///
    /// ReplayRecorder.cs / ReplayPlayer.cs / ReplayHeader.cs / NetworkCommand.cs are compiled into this Tier-1
    /// (Godot-free) assembly, so the full record→restore-seed→replay round-trip is verified headlessly here.
    /// </summary>
    public class SimRngChecksumReplayTests
    {
        private const int Ticks = 120;

        // A representative header roster + fields for a 2-player match. scenarioHash/rulesetHash are non-zero so a
        // round-trip re-gate would pass; algo version matches this build (never forward-incompatible).
        private static readonly Faction[] Roster2 = { Faction.Player1, Faction.Player2 };
        private const ulong ScenarioHashFixture = 0xABCDEF0123456789UL;
        private const ulong RulesetHashFixture  = 0x0F0F0F0F0F0F0F0FUL;

        private static ReplayRecorder NewRecorder(string path, string scenarioPath = "simrng-replay-test",
            ulong seed = 0UL, int algoVersion = -1)
            => new(path, scenarioPath, seed, ScenarioHashFixture, RulesetHashFixture,
                   algoVersion < 0 ? CanonicalModelHash.AlgoVersion : algoVersion, Roster2);

        private sealed class RngDrawTestSystem : ISimSystem
        {
            private readonly int _targetId;
            public RngDrawTestSystem(int targetId) => _targetId = targetId;

            public void Tick(EntityWorld world, Fixed dt)
            {
                if (!world.IsAlive(_targetId)) return;
                world.Health[_targetId] = world.Health[_targetId] + Fixed.FromInt(world.Rng.NextInt(3));
            }
        }

        private static (EntityWorld World, SimulationLoop Loop) BuildRngLoop(ulong seed)
        {
            var world = new EntityWorld();
            int targetId = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                        Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.Rng.Seed(seed);

            var loop = new SimulationLoop(world, new RngDrawTestSystem(targetId));
            loop.EnableChecksums(new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2));
            loop.ChecksumInterval = 1;
            return (world, loop);
        }

        private static uint[] StepCapturing(SimulationLoop loop, int ticks)
        {
            var seq = new List<uint>(ticks);
            loop.OnChecksum = (_, hash) => seq.Add(hash);
            for (int i = 0; i < ticks; i++) loop.StepOnce();
            return seq.ToArray();
        }

        /// <summary>AC3 — two live runs with the SAME seed produce byte-identical per-tick checksum sequences.</summary>
        [Fact]
        public void TwoRunsSameSeed_ProduceIdenticalChecksumSequences()
        {
            const ulong seed = 0xA5A5A5A5DEADBEEFUL;
            var (_, loopA) = BuildRngLoop(seed);
            var (_, loopB) = BuildRngLoop(seed);

            uint[] a = StepCapturing(loopA, Ticks);
            uint[] b = StepCapturing(loopB, Ticks);

            Assert.Equal(Ticks, a.Length);
            Assert.True(a.Distinct().Count() > 1,
                "Checksum sequence is constant — the RNG draw is not advancing hashed state.");
            Assert.Equal(a, b);
        }

        /// <summary>AC2 (negative control) — a DIFFERENT seed produces a different checksum sequence.</summary>
        [Fact]
        public void DifferentSeed_DivergesChecksumSequence()
        {
            var (_, loopA) = BuildRngLoop(0x1111111111111111UL);
            var (_, loopB) = BuildRngLoop(0x2222222222222222UL);

            uint[] a = StepCapturing(loopA, Ticks);
            uint[] b = StepCapturing(loopB, Ticks);

            Assert.NotEqual(a, b);
        }

        /// <summary>
        /// Story 9.11 (AC1) — the v4 round-trip: the match seed survives a ReplayRecorder→ReplayPlayer round-trip,
        /// and replaying with the restored seed reproduces the live per-tick checksum sequence byte-for-byte. The
        /// header fields (seed/scenarioHash/rulesetHash/roster) round-trip exactly.
        /// </summary>
        [Fact]
        public void V4RoundTrip_ReproducesChecksums()
        {
            const ulong seed = 0x0BADC0DE12345678UL;
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_simrng_{Guid.NewGuid():N}.chmr");

            try
            {
                using (var recorder = NewRecorder(chmrPath, seed: seed))
                    Assert.Equal(seed, recorder.Seed);

                var (_, liveLoop) = BuildRngLoop(seed);
                uint[] live = StepCapturing(liveLoop, Ticks);

                var (replayWorld, replayLoop) = BuildRngLoop(0xFFFFFFFFFFFFFFFFUL); // deliberately wrong
                var player = new ReplayPlayer(chmrPath, replayWorld);

                // Header round-trips exactly.
                Assert.Equal(seed, player.Seed);
                Assert.Equal(seed, replayWorld.Rng.State);            // ReplayPlayer reseeded the world's RNG
                Assert.Equal(ScenarioHashFixture, player.ScenarioHash);
                Assert.Equal(RulesetHashFixture,  player.RulesetHash);
                Assert.Equal(CanonicalModelHash.AlgoVersion, player.ModelAlgoVersion);
                Assert.Equal(Roster2, player.Roster);

                var replay = new List<uint>(Ticks);
                replayLoop.OnChecksum = (_, hash) => replay.Add(hash);
                for (int i = 0; i < Ticks; i++)
                {
                    player.Flush(replayLoop.CurrentTick);
                    replayLoop.StepOnce();
                }

                Assert.Equal(live, replay.ToArray());
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>Story 9.11 — the fail-closed scenario re-gate (pure policy, mirrors HandshakeGate.CheckStart):
        /// equal nonzero allows; a mismatch or either-hash-0 blocks with a surfaced reason.</summary>
        [Fact]
        public void ScenarioReGate_MismatchIsRejected()
        {
            Assert.Null(ReplayPlayer.ScenarioGateBlockReason(0x1234UL, 0x1234UL));       // equal nonzero → allow
            Assert.NotNull(ReplayPlayer.ScenarioGateBlockReason(0x1234UL, 0x5678UL));    // mismatch → block
            Assert.NotNull(ReplayPlayer.ScenarioGateBlockReason(0UL, 0x5678UL));         // embedded 0 → block
            Assert.NotNull(ReplayPlayer.ScenarioGateBlockReason(0x1234UL, 0UL));         // loaded 0 → block
            Assert.NotNull(ReplayPlayer.ScenarioGateBlockReason(0UL, 0UL));              // both 0 → block
        }

        /// <summary>DW-430 — the fail-closed RULESET re-gate companion (the v4 header's rulesetHash was read but
        /// never compared): equal nonzero allows; a mismatch or either-hash-0 blocks with a surfaced reason —
        /// mirroring MatchAgreementHash, which folds BOTH the scenario and ruleset hashes into the live MP gate.</summary>
        [Fact]
        public void RulesetReGate_MismatchIsRejected()
        {
            Assert.Null(ReplayPlayer.RulesetGateBlockReason(0x1234UL, 0x1234UL));        // equal nonzero → allow
            Assert.NotNull(ReplayPlayer.RulesetGateBlockReason(0x1234UL, 0x5678UL));     // mismatch → block
            Assert.NotNull(ReplayPlayer.RulesetGateBlockReason(0UL, 0x5678UL));          // embedded 0 → block
            Assert.NotNull(ReplayPlayer.RulesetGateBlockReason(0x1234UL, 0UL));          // current 0 → block
            Assert.NotNull(ReplayPlayer.RulesetGateBlockReason(0UL, 0UL));               // both 0 → block

            // The block reason names the drift class (a ruleset problem, not a scenario one).
            Assert.Contains("RULESET", ReplayPlayer.RulesetGateBlockReason(0x1234UL, 0x5678UL)!);

            // The REAL build fingerprint self-gates: a replay recorded on THIS build's ruleset plays on this build
            // (RulesetHash.Compute is sentinel-guarded nonzero, pinned by RulesetHashTests), and a drifted one blocks.
            ulong current = RulesetHash.Compute();
            Assert.Null(ReplayPlayer.RulesetGateBlockReason(current, current));
            Assert.NotNull(ReplayPlayer.RulesetGateBlockReason(current ^ 0x1UL, current));
        }

        /// <summary>Story 9.11 — a v1/v2/v3 file (no embedded scenario hash) is HARD-REJECTED with a descriptive
        /// "older replay format" error and never partially played.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void LegacyVersion_IsHardRejected(int version)
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_legacy_v{version}_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(chmrPath, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write((ushort)version);
                    w.Write((ushort)0);                    // empty scenario path
                    w.Write(EntityWorld.DEFAULT_RNG_SEED); // an 8-byte seed (v2/v3 shape)
                    w.Write(ReplayRecorder.EOF_SENTINEL);
                }

                var world = new EntityWorld();
                var ex = Assert.Throws<InvalidDataException>(() => new ReplayPlayer(chmrPath, world));
                Assert.Contains("older replay format", ex.Message);
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>Story 9.11 — a v4 file whose embedded modelAlgoVersion exceeds this build's is forward-
        /// incompatible and hard-rejected ("newer") — never partially played.</summary>
        [Fact]
        public void ForwardAlgoVersion_IsRejected()
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_fwd_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var rec = NewRecorder(chmrPath, algoVersion: CanonicalModelHash.AlgoVersion + 1))
                    rec.RecordTick(1, Faction.Player1,
                        new[] { new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(5), Fixed.FromInt(7)) }, 0, 1);

                var world = new EntityWorld();
                var ex = Assert.Throws<InvalidDataException>(() => new ReplayPlayer(chmrPath, world));
                Assert.Contains("newer", ex.Message);
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>Story 9.11 — a v4 header truncated before the roster is a corrupt file: rejected with
        /// <see cref="InvalidDataException"/>, no partial playback.</summary>
        [Fact]
        public void TruncatedHeader_ThrowsInvalidData()
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_trunc_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(chmrPath, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write(ReplayRecorder.VERSION); // v4 — promises the full extended header
                    w.Write((ushort)0);              // empty scenario path
                    w.Write(EntityWorld.DEFAULT_RNG_SEED); // seed only — file ends before scenarioHash/roster
                }

                var world = new EntityWorld();
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(chmrPath, world));
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>Story 9.11 — the result trailer round-trips: winnerFaction / finalTick / completed are restored
        /// on load; an interrupted recording carries completed=false.</summary>
        [Fact]
        public void ResultTrailer_RoundTrips()
        {
            string wonPath = Path.Combine(Path.GetTempPath(), $"chimera_won_{Guid.NewGuid():N}.chmr");
            string incPath = Path.Combine(Path.GetTempPath(), $"chimera_inc_{Guid.NewGuid():N}.chmr");
            try
            {
                var order = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(5), Fixed.FromInt(7));

                using (var rec = NewRecorder(wonPath))
                {
                    rec.RecordTick(4, Faction.Player1, new[] { order }, 0, 1);
                    rec.RecordTick(9, Faction.Player2, new[] { order }, 0, 1);
                    rec.Close(winnerFaction: 2, completed: true);
                }

                var wonPlayer = new ReplayPlayer(wonPath, new EntityWorld());
                Assert.Equal(9u, wonPlayer.FinalTick);
                Assert.Equal(2,  wonPlayer.WinnerFaction);
                Assert.True(wonPlayer.Completed);

                using (var rec = NewRecorder(incPath))
                {
                    rec.RecordTick(3, Faction.Player1, new[] { order }, 0, 1);
                    rec.Close(); // interrupted — no winner, incomplete
                }

                var incPlayer = new ReplayPlayer(incPath, new EntityWorld());
                Assert.Equal(3u, incPlayer.FinalTick);
                Assert.False(incPlayer.Completed);
            }
            finally
            {
                if (File.Exists(wonPath)) File.Delete(wonPath);
                if (File.Exists(incPath)) File.Delete(incPath);
            }
        }

        /// <summary>Story 9.11 — the lightweight ReplayHeader.Read returns metadata (map/hash/roster/duration/
        /// result) for a v4 file and flags a legacy file as unplayable without throwing.</summary>
        [Fact]
        public void HeaderRead_ReturnsMetadata()
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_hdr_{Guid.NewGuid():N}.chmr");
            string legacyPath = Path.Combine(Path.GetTempPath(), $"chimera_hdrlegacy_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var rec = NewRecorder(chmrPath, scenarioPath: "res://scenarios/dueling_peaks.json"))
                {
                    rec.RecordTick(150, Faction.Player1,
                        new[] { new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(1), Fixed.FromInt(2)) }, 0, 1);
                    rec.Close(winnerFaction: 1, completed: true);
                }

                var hdr = ReplayHeader.Read(chmrPath);
                Assert.True(hdr.IsPlayable);
                Assert.Equal("res://scenarios/dueling_peaks.json", hdr.ScenarioPath);
                Assert.Equal(ScenarioHashFixture, hdr.ScenarioHash);
                Assert.Equal(Roster2, hdr.Roster);
                Assert.Equal(2, hdr.FactionCount);
                Assert.Equal(150u, hdr.FinalTick);
                Assert.Equal(1, hdr.WinnerFaction);
                Assert.True(hdr.Completed);

                // A legacy file lists as unplayable, not a crash.
                using (var w = new BinaryWriter(File.Open(legacyPath, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write((ushort)3);
                    w.Write((ushort)0);
                    w.Write(EntityWorld.DEFAULT_RNG_SEED);
                    w.Write(ReplayRecorder.EOF_SENTINEL);
                }
                var legacyHdr = ReplayHeader.Read(legacyPath);
                Assert.False(legacyHdr.IsPlayable);
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
            }
        }

        /// <summary>Helper: hand-write a v4 header (2-slot roster) to <paramref name="w"/>.</summary>
        private static void WriteV4Header(BinaryWriter w, string scenarioPath = "", int factionCount = 2)
        {
            w.Write(ReplayRecorder.MAGIC);
            w.Write(ReplayRecorder.VERSION);
            var pb = System.Text.Encoding.UTF8.GetBytes(scenarioPath);
            w.Write((ushort)pb.Length);
            w.Write(pb);
            w.Write(EntityWorld.DEFAULT_RNG_SEED); // seed
            w.Write(ScenarioHashFixture);          // scenarioHash
            w.Write(RulesetHashFixture);           // rulesetHash
            w.Write(CanonicalModelHash.AlgoVersion); // modelAlgoVersion
            w.Write((ushort)factionCount);
            for (int i = 0; i < factionCount; i++) w.Write((byte)FactionRegistry.ToFaction(i));
        }

        /// <summary>Story 9.11 (P9) — a crash-mid-record file (merged frames, NO 0x1A trailer) still lists: the
        /// header reader returns IsPlayable=true, Completed=false, and FinalTick == the max recorded tick (the
        /// TryPeekTick fallback).</summary>
        [Fact]
        public void HeaderRead_NoTrailer_FallsBackToMaxMergedTick()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_notrailer_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    WriteV4Header(w, "res://scenarios/x.json");

                    // Two merged frames (ticks 10 and 150), then EOF — but deliberately NO result trailer.
                    WriteMergedFrame(w, 10, Faction.Player1);
                    WriteMergedFrame(w, 150, Faction.Player2);
                    // no trailer, no frame-len-0 EOF (a crash mid-record)
                }

                var hdr = ReplayHeader.Read(path);
                Assert.True(hdr.IsPlayable);
                Assert.False(hdr.Completed);
                Assert.Equal(150u, hdr.FinalTick);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static void WriteMergedFrame(BinaryWriter w, uint tick, Faction faction)
        {
            var buf      = new byte[MergedTickPacket.MERGED_MAX_BYTES];
            var factions = new[] { faction };
            var counts   = new[] { 1 };
            var orders   = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            orders[0]    = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(1), Fixed.FromInt(2));
            int len = MergedTickPacket.Write(buf, tick, factions, counts, orders, 1);
            w.Write((ushort)len);
            w.Write(buf, 0, len);
        }

        /// <summary>Story 9.11 (P8) — a corrupt header declaring more factions than the ceiling is rejected (no
        /// giant roster allocation) on playback, and lists as unplayable in the browser.</summary>
        [Fact]
        public void OverlargeFactionCount_IsRejected()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_bigroster_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write(ReplayRecorder.VERSION);
                    w.Write((ushort)0);
                    w.Write(EntityWorld.DEFAULT_RNG_SEED);
                    w.Write(ScenarioHashFixture);
                    w.Write(RulesetHashFixture);
                    w.Write(CanonicalModelHash.AlgoVersion);
                    w.Write((ushort)(FactionRegistry.PLAYER_COUNT + 1)); // 9 > 8 ceiling
                }

                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(path, new EntityWorld()));
                Assert.False(ReplayHeader.Read(path).IsPlayable);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>Story 9.11 (P3) — a well-framed but internally-corrupt merged frame, and an unrecognized frame
        /// type, are BOTH fail-closed (throw) rather than silently dropped (the silent-desync class this eliminates).</summary>
        [Fact]
        public void CorruptOrUnknownFrame_IsHardRejected()
        {
            string corruptPath = Path.Combine(Path.GetTempPath(), $"chimera_corruptframe_{Guid.NewGuid():N}.chmr");
            string unknownPath = Path.Combine(Path.GetTempPath(), $"chimera_unknownframe_{Guid.NewGuid():N}.chmr");
            try
            {
                // A 0x14 merged frame whose subBundleCount (99) exceeds the ceiling → TryRead returns false.
                using (var w = new BinaryWriter(File.Open(corruptPath, FileMode.Create)))
                {
                    WriteV4Header(w);
                    w.Write((ushort)MergedTickPacket.HEADER_BYTES);       // frameLen = 6
                    w.Write((byte)PacketType.TickCommandsMerged);         // 0x14
                    w.Write((uint)5);                                     // tick
                    w.Write((byte)99);                                    // subBundleCount > MERGED_MAX_SUBBUNDLES
                }
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(corruptPath, new EntityWorld()));

                // An unrecognized frame-type byte → reject (not skip).
                using (var w = new BinaryWriter(File.Open(unknownPath, FileMode.Create)))
                {
                    WriteV4Header(w);
                    w.Write((ushort)1);   // frameLen = 1
                    w.Write((byte)0x99);  // unknown discriminator
                }
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(unknownPath, new EntityWorld()));
            }
            finally
            {
                if (File.Exists(corruptPath)) File.Delete(corruptPath);
                if (File.Exists(unknownPath)) File.Delete(unknownPath);
            }
        }

        /// <summary>Story 9.11 (follow-up) — the CORE new v4 glue: two factions recorded on the SAME tick in
        /// DESCENDING call order are flushed as ONE merged frame with sub-bundles sorted ASCENDING by faction, and on
        /// replay both sub-bundles are decoded AND applied ascending-by-faction (the canonical apply order the live
        /// merged path uses). Guards <c>ReplayRecorder.FlushTick</c>'s selection-sort and the player's per-sub-bundle
        /// fan-out — every other round-trip test is single-faction-per-tick, so the sort never swaps and the fan-out
        /// loop never runs more than once.</summary>
        [Fact]
        public void V4MultiFactionTick_RoundTripsSortedAscending()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_multi_{Guid.NewGuid():N}.chmr");
            try
            {
                var p1Move = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(1), Fixed.FromInt(2)); // unit 0 → Player1
                var p2Move = new UnitOrder(1, UnitCommand.Move, Fixed.FromInt(3), Fixed.FromInt(4)); // unit 1 → Player2

                // Record BOTH on the same tick, Player2 FIRST (descending) — FlushTick must sort ascending on flush.
                using (var rec = NewRecorder(path))
                {
                    rec.RecordTick(7, Faction.Player2, new[] { p2Move }, 0, 1);
                    rec.RecordTick(7, Faction.Player1, new[] { p1Move }, 0, 1);
                    rec.Close(winnerFaction: 0, completed: true);
                }

                var world = new EntityWorld();
                int u0 = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.One);
                int u1 = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.One);
                Assert.Equal(0, u0);
                Assert.Equal(1, u1);

                var applied = new List<int>();
                var player = new ReplayPlayer(path, world) { OnRequestPath = (id, x, z) => applied.Add(id) };
                player.Flush(7);

                // Both sub-bundles survived the round-trip (presence — the fan-out ran for both)...
                Assert.True((world.Flags[u0] & EntityFlags.Moving) != 0);
                Assert.True((world.Flags[u1] & EntityFlags.Moving) != 0);
                // ...and were applied ascending-by-faction (Player1's unit 0 BEFORE Player2's unit 1) despite the
                // descending record order — proving FlushTick's sort + the merged frame's canonical wire order.
                Assert.Equal(new[] { u0, u1 }, applied.ToArray());
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>Story 9.11 (follow-up, P3) — a result-trailer frame (0x1A) SHORTER than its fixed 7-byte payload is
        /// corruption: fail-closed (throw) like the corrupt-merged / unknown-type branches, never silently ignored
        /// (a dropped trailer is a replay with no result and no error).</summary>
        [Fact]
        public void TruncatedTrailer_IsHardRejected()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_shorttrailer_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    WriteV4Header(w);
                    w.Write((ushort)3);                    // frameLen = 3 (< TRAILER_BYTES == 7)
                    w.Write(ReplayRecorder.FRAME_TRAILER); // 0x1A
                    w.Write((byte)0);                      // one partial payload byte...
                    w.Write((byte)0);                      // ...frame is 3 bytes, short of the 7-byte trailer
                }
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(path, new EntityWorld()));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>Story 9.11 (follow-up, P8) — a roster byte outside the valid player range
        /// (Player1..Player{PLAYER_COUNT}) is a corrupt header: rejected on playback rather than becoming an
        /// out-of-range Faction that flows to Fog.SetViewer.</summary>
        [Fact]
        public void OutOfRangeRosterFaction_IsRejected()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_badroster_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var w = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write(ReplayRecorder.VERSION);
                    w.Write((ushort)0);
                    w.Write(EntityWorld.DEFAULT_RNG_SEED);
                    w.Write(ScenarioHashFixture);
                    w.Write(RulesetHashFixture);
                    w.Write(CanonicalModelHash.AlgoVersion);
                    w.Write((ushort)1);   // factionCount = 1 (within the P8 ceiling)
                    w.Write((byte)200);   // roster[0] = 200 — not a valid player faction
                }
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(path, new EntityWorld()));
                // The lightweight header reader must agree with the player: an out-of-range roster byte is an
                // unplayable row, not a Play-enabled one that only errors on click (mirrors OverlargeFactionCount).
                Assert.False(ReplayHeader.Read(path).IsPlayable);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>Story 9.11 (follow-up) — orders recorded on TWO DIFFERENT ticks both survive the v4
        /// buffer-and-flush-on-tick-advance model: recording tick 3 then tick 7 forces <c>FlushTick</c> to emit tick 3's
        /// buffered frame when tick 7 arrives, and <c>Close</c> flushes tick 7. Every other round-trip test flushes a
        /// single tick, so the buffered earlier tick's frame is never proven to survive the advance.</summary>
        [Fact]
        public void V4MultiTickOrders_BothTicksRoundTrip()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_multitick_{Guid.NewGuid():N}.chmr");
            try
            {
                var p1Move = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(1), Fixed.FromInt(2)); // unit 0 → tick 3
                var p2Move = new UnitOrder(1, UnitCommand.Move, Fixed.FromInt(3), Fixed.FromInt(4)); // unit 1 → tick 7

                using (var rec = NewRecorder(path))
                {
                    rec.RecordTick(3, Faction.Player1, new[] { p1Move }, 0, 1); // buffered...
                    rec.RecordTick(7, Faction.Player2, new[] { p2Move }, 0, 1); // ...FlushTick emits tick 3 here
                    rec.Close(winnerFaction: 0, completed: true);              // ...and flushes tick 7
                }

                var world = new EntityWorld();
                int u0 = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.One);
                int u1 = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.One);
                Assert.Equal(0, u0);
                Assert.Equal(1, u1);

                var applied = new List<int>();
                var player = new ReplayPlayer(path, world) { OnRequestPath = (id, x, z) => applied.Add(id) };

                // Tick 3's buffered frame applies unit 0 only; tick 7 has not been flushed yet.
                player.Flush(3);
                Assert.True((world.Flags[u0] & EntityFlags.Moving) != 0);
                Assert.False((world.Flags[u1] & EntityFlags.Moving) != 0);
                Assert.Equal(new[] { u0 }, applied.ToArray());

                // Tick 7's frame applies unit 1 — proving the earlier buffered tick did not clobber or drop the later one.
                player.Flush(7);
                Assert.True((world.Flags[u1] & EntityFlags.Moving) != 0);
                Assert.Equal(new[] { u0, u1 }, applied.ToArray());
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>Story 9.11 (follow-up) — an interrupted recording's trailer carries winner 0 (no victor), and a
        /// negative winnerFaction passed to <c>Close</c> is clamped to 0 rather than written as a garbage byte.</summary>
        [Fact]
        public void ResultTrailer_IncompleteAndNegativeWinner_ClampToZero()
        {
            string incPath = Path.Combine(Path.GetTempPath(), $"chimera_incwin_{Guid.NewGuid():N}.chmr");
            string negPath = Path.Combine(Path.GetTempPath(), $"chimera_negwin_{Guid.NewGuid():N}.chmr");
            try
            {
                var order = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(5), Fixed.FromInt(7));

                using (var rec = NewRecorder(incPath))
                {
                    rec.RecordTick(3, Faction.Player1, new[] { order }, 0, 1);
                    rec.Close(); // interrupted → no victor
                }
                var incPlayer = new ReplayPlayer(incPath, new EntityWorld());
                Assert.Equal(0, incPlayer.WinnerFaction);
                Assert.False(incPlayer.Completed);

                using (var rec = NewRecorder(negPath))
                {
                    rec.RecordTick(5, Faction.Player1, new[] { order }, 0, 1);
                    rec.Close(winnerFaction: -1, completed: true); // negative must clamp to 0
                }
                var negPlayer = new ReplayPlayer(negPath, new EntityWorld());
                Assert.Equal(0, negPlayer.WinnerFaction);
                Assert.True(negPlayer.Completed);
            }
            finally
            {
                if (File.Exists(incPath)) File.Delete(incPath);
                if (File.Exists(negPath)) File.Delete(negPath);
            }
        }

        // ══════════ DW-224 — recorded ORDERS replayed through a system that DRAWS from world.Rng ══════════
        //
        // Every replay round-trip above is one of two shapes: it either records REAL ORDERS but replays them through a
        // bare EntityWorld with no system at all (the order-application tests), or it drives an Rng-drawing system but
        // records ZERO orders (V4RoundTrip_ReproducesChecksums opens the recorder and closes it without a RecordTick).
        // Neither shape can catch the interaction: a replay whose order stream lands on a different tick than the
        // recording, or in a different order, perturbs how many draws the sim takes from the SHARED SimRng — and the
        // RNG state is folded into SimChecksum, so the divergence is real and silent. That is the live-vs-replay desync
        // class this pair closes.
        //
        // The coupling is deliberate and is the whole point: a Move order folds NOTHING into SimChecksum by itself
        // (Flags/MoveTarget are not hashed), so <see cref="OrderGatedRngSystem"/> makes the DRAW COUNT a function of
        // which units the order stream has commanded. Order timing therefore reaches the checksum only through the RNG
        // stream + the Health it drives — exactly the path a "records zero orders" test leaves unguarded.

        private const int OrderTicks = 40;

        /// <summary>Move orders as (tick, unit): three units commanded on three DIFFERENT ticks, so each has a
        /// different number of drawing ticks — the draw count encodes the order stream's timing.</summary>
        private static readonly (uint Tick, int Unit)[] OrderScript = { (1u, 0), (4u, 2), (9u, 1) };

        /// <summary>
        /// Draws from the SHARED <c>world.Rng</c> once per commanded unit per tick — ascending entity id (the
        /// deterministic iteration contract, AR-15). A unit only draws once an order has set <see cref="EntityFlags.Moving"/>
        /// on it, so both the draw COUNT and the per-unit Health it accumulates depend on the applied order stream.
        /// </summary>
        private sealed class OrderGatedRngSystem : ISimSystem
        {
            public void Tick(EntityWorld world, Fixed dt)
            {
                int cap = world.HighWaterMark;
                for (int i = 0; i < cap; i++)
                {
                    if (!world.IsAlive(i)) continue;
                    if ((world.Flags[i] & EntityFlags.Moving) == 0) continue; // uncommanded units never draw
                    world.Health[i] = world.Health[i] + Fixed.FromInt(world.Rng.NextInt(5) + 1); // 1..5, integer-only
                }
            }
        }

        private static (EntityWorld World, SimulationLoop Loop) BuildOrderGatedLoop(ulong seed)
        {
            var world = new EntityWorld();
            for (int k = 0; k < 3; k++)
                world.Create(new FixedVec3(Fixed.FromInt(k * 2), Fixed.Zero, Fixed.Zero),
                             Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.Rng.Seed(seed);

            var loop = new SimulationLoop(world, new OrderGatedRngSystem());
            loop.EnableChecksums(new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2));
            loop.ChecksumInterval = 1;
            return (world, loop);
        }

        private static UnitOrder MoveOrder(int unit)
            => new UnitOrder(unit, UnitCommand.Move, Fixed.FromInt(20 + unit), Fixed.FromInt(30 + unit));

        /// <summary>
        /// Drive the live run: apply each scripted order through the SHARED <see cref="OrderApplier"/> (the same entry
        /// the replay path uses) and record it, then step. <paramref name="tickShift"/> delays every RECORDED order by
        /// n ticks without moving the live application (an order stream that replays late); <paramref name="recordOrders"/>
        /// false writes an ORDERLESS file (the zero-order shape DW-224 flags) while the live run still issues them.
        /// </summary>
        private static (List<uint> Hashes, EntityWorld World) RecordLiveOrderRun(
            string path, ulong seed, uint tickShift = 0u, bool recordOrders = true)
        {
            var (world, loop) = BuildOrderGatedLoop(seed);
            var hashes = new List<uint>(OrderTicks);
            loop.OnChecksum = (_, hash) => hashes.Add(hash);

            using (var rec = NewRecorder(path, seed: seed))
            {
                for (uint t = 0; t < OrderTicks; t++)
                {
                    for (int k = 0; k < OrderScript.Length; k++)
                    {
                        if (OrderScript[k].Tick != t) continue;
                        UnitOrder order = MoveOrder(OrderScript[k].Unit);
                        OrderApplier.Apply(world, order, Faction.Player1);
                        if (recordOrders)
                            rec.RecordTick(t + tickShift, Faction.Player1, new[] { order }, 0, 1);
                    }
                    loop.StepOnce();
                }
                rec.Close(winnerFaction: 0, completed: true);
            }
            return (hashes, world);
        }

        /// <summary>Replay <paramref name="path"/> from a DELIBERATELY WRONG seed (the header restore must fix it).</summary>
        private static (List<uint> Hashes, EntityWorld World, ReplayPlayer Player) ReplayOrderRun(string path)
        {
            var (world, loop) = BuildOrderGatedLoop(0x0123456789ABCDEFUL); // wrong on purpose
            var player = new ReplayPlayer(path, world);                    // ctor reseeds world.Rng to the recorded seed

            var hashes = new List<uint>(OrderTicks);
            loop.OnChecksum = (_, hash) => hashes.Add(hash);
            for (int i = 0; i < OrderTicks; i++)
            {
                player.Flush(loop.CurrentTick);
                loop.StepOnce();
            }
            return (hashes, world, player);
        }

        /// <summary>
        /// DW-224 — the missing round-trip: REAL recorded orders replayed through a system that draws from
        /// <c>world.Rng</c> reproduce the live per-tick checksum sequence byte-for-byte, AND land the same world state.
        /// </summary>
        [Fact]
        public void V4RecordedOrders_ThroughAnRngDrawingSystem_ReplayIdentically()
        {
            const ulong seed = 0x51E3D0A7B1C24E96UL;
            string path = Path.Combine(Path.GetTempPath(), $"chimera_rngorders_{Guid.NewGuid():N}.chmr");
            try
            {
                var (live, liveWorld) = RecordLiveOrderRun(path, seed);
                var (replay, replayWorld, player) = ReplayOrderRun(path);

                // The recording genuinely carries the order stream (the defect was a test that recorded NONE).
                Assert.Equal(OrderScript.Length, player.TotalTicks);
                Assert.Equal(seed, player.Seed);
                Assert.Equal(OrderTicks, live.Count);

                // The RNG was actually consumed, and the sequence is not a constant (a vacuous pass guard).
                Assert.NotEqual(seed, liveWorld.Rng.State);
                Assert.True(live.Distinct().Count() > 1, "Checksum sequence is constant — no RNG-driven state moved.");

                // The headline: byte-identical per-tick checksums across record→replay.
                Assert.Equal(live, replay);

                // ...and identical WORLD state, not merely an identical hash: the shared stream position plus every
                // unit's accumulated Health match, and each unit drew on exactly the ticks its order gated.
                Assert.Equal(liveWorld.Rng.State, replayWorld.Rng.State);
                for (int k = 0; k < OrderScript.Length; k++)
                {
                    int unit = OrderScript[k].Unit;
                    int drawingTicks = OrderTicks - (int)OrderScript[k].Tick; // ordered on Tick → draws Tick..OrderTicks-1
                    Assert.Equal(liveWorld.Health[unit].Raw, replayWorld.Health[unit].Raw);
                    // Each draw adds 1..5, so the accumulated Health pins the per-unit draw COUNT within tight bounds.
                    Assert.InRange(liveWorld.Health[unit].Raw,
                                   Fixed.FromInt(100 + drawingTicks).Raw,
                                   Fixed.FromInt(100 + 5 * drawingTicks).Raw);
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>
        /// DW-224 (negative controls) — the two ways the guard above could have been vacuous. (a) An ORDERLESS
        /// recording of the same match does NOT reproduce the live sequence: that is the exact shape the pre-existing
        /// round-trip test had, and it must be provably insufficient. (b) The SAME orders recorded one tick LATE do not
        /// reproduce it either: the recorded tick number is load-bearing, because it decides on which tick the RNG
        /// draw count changes.
        /// </summary>
        [Fact]
        public void V4RecordedOrders_OmittedOrShifted_DivergeFromTheLiveRun()
        {
            const ulong seed = 0x51E3D0A7B1C24E96UL;
            string orderless = Path.Combine(Path.GetTempPath(), $"chimera_noorders_{Guid.NewGuid():N}.chmr");
            string shifted   = Path.Combine(Path.GetTempPath(), $"chimera_shifted_{Guid.NewGuid():N}.chmr");
            try
            {
                var (live, _) = RecordLiveOrderRun(orderless, seed, recordOrders: false);
                var (replayNoOrders, _, orderlessPlayer) = ReplayOrderRun(orderless);
                Assert.Equal(0, orderlessPlayer.TotalTicks); // nothing was recorded...
                Assert.NotEqual(live, replayNoOrders);       // ...so the replay cannot match the live run

                var (liveShift, _) = RecordLiveOrderRun(shifted, seed, tickShift: 1u);
                var (replayShifted, _, shiftedPlayer) = ReplayOrderRun(shifted);
                Assert.Equal(OrderScript.Length, shiftedPlayer.TotalTicks); // recorded, but one tick late
                Assert.NotEqual(liveShift, replayShifted);
            }
            finally
            {
                if (File.Exists(orderless)) File.Delete(orderless);
                if (File.Exists(shifted)) File.Delete(shifted);
            }
        }

        /// <summary>Story 9.11 (follow-up) — the header reader's FULL-SCAN trailer decode (used when the fixed-tail
        /// fast path's signature does not match, e.g. a stray byte after EOF) reconstructs the same winner/finalTick as
        /// the fast path. Without a trailing byte the fast path handles a completed file, so this branch is otherwise
        /// never exercised WITH a trailer present.</summary>
        [Fact]
        public void HeaderRead_FullScanTrailer_MatchesFastPath()
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_fullscan_{Guid.NewGuid():N}.chmr");
            try
            {
                using (var rec = NewRecorder(path, scenarioPath: "res://scenarios/y.json"))
                {
                    rec.RecordTick(42, Faction.Player1,
                        new[] { new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(1), Fixed.FromInt(2)) }, 0, 1);
                    rec.Close(winnerFaction: 1, completed: true);
                }

                // Append a stray byte after EOF so the fixed-tail fast-path signature check fails and Read falls through
                // to the full body scan.
                using (var s = new FileStream(path, FileMode.Append, FileAccess.Write))
                    s.WriteByte(0xEE);

                var hdr = ReplayHeader.Read(path);
                Assert.True(hdr.IsPlayable);
                Assert.Equal(42u, hdr.FinalTick);
                Assert.Equal(1, hdr.WinnerFaction);
                Assert.True(hdr.Completed);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
