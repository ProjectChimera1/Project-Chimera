#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.5 (AC2 + AC3) — proves the shared <see cref="SimRng"/> is (a) folded into <see cref="SimChecksum"/>
    /// so a divergent draw stream is detectable, (b) recorded by <see cref="ReplayRecorder"/> + restored by
    /// <see cref="ReplayPlayer"/> so a replay regenerates the identical stream, and (c) drives reproducible
    /// per-tick checksums across two live runs AND across a record→replay round-trip.
    ///
    /// The RNG-driven behavior lives in a test-only <see cref="ISimSystem"/> (<see cref="RngDrawTestSystem"/>)
    /// added to the loop — NOT a RunAndRecord perturb callback. A .chmr records ORDERS, not perturbs; only a
    /// system re-runs on playback, so the draw MUST live in a system for the replayed checksum to reproduce.
    ///
    /// Replay-test scope (Task 7 note, resolved): took the RECOMMENDED path — ReplayRecorder.cs / ReplayPlayer.cs
    /// / NetworkCommand.cs are explicitly compiled into this Tier-1 (Godot-free) assembly, so the full
    /// record→restore-seed→replay round-trip is verified headlessly here (no Tier-2/GdUnit4 deferral needed).
    /// </summary>
    public class SimRngChecksumReplayTests
    {
        private const int Ticks = 120;

        /// <summary>
        /// Test-only system mirroring how an Epic 2 random effect will draw: each tick it advances the shared
        /// <see cref="SimRng"/> and writes the result into HASHED state (the target entity's Health). Because
        /// both the draw (<see cref="SimRng.State"/>) and its effect (Health) are folded into the checksum, the
        /// per-tick checksum sequence is a faithful fingerprint of the RNG stream.
        /// </summary>
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

        /// <summary>
        /// Build a minimal, checksum-enabled loop whose ONLY system draws from the shared SimRng. Returns the
        /// world (so a caller can let ReplayPlayer reseed it) and the loop (to step + capture).
        /// </summary>
        private static (EntityWorld World, SimulationLoop Loop) BuildRngLoop(ulong seed)
        {
            var world = new EntityWorld();
            int targetId = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                        Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.Rng.Seed(seed);

            var loop = new SimulationLoop(world, new RngDrawTestSystem(targetId));
            loop.EnableChecksums(new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2));
            loop.ChecksumInterval = 1; // one checksum per tick → the sequence is a full per-tick fingerprint
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

        /// <summary>
        /// AC2 (negative control) — a DIFFERENT seed produces a different checksum sequence. Proves the RNG
        /// stream actually drives the hash: if <see cref="SimRng.State"/> were not folded in (or the draw didn't
        /// touch hashed state), seed A and seed B would be indistinguishable. This is the robust,
        /// stream/checksum-level control the 1.4 review asked for — no reflection or BCL-internal probing.
        /// </summary>
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
        /// AC2/AC3 — the match seed survives a ReplayRecorder→ReplayPlayer round-trip, and replaying the SAME
        /// loop (test system included) with the restored seed reproduces the live per-tick checksum sequence
        /// byte-for-byte. The .chmr carries the seed in its v2 header; it records no orders (this scenario
        /// issues none) — the stream regenerates from the seed alone because the draw lives in a system.
        /// </summary>
        [Fact]
        public void RecordThenReplay_ReproducesChecksumSequence()
        {
            const ulong seed = 0x0BADC0DE12345678UL;
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_simrng_{Guid.NewGuid():N}.chmr");

            try
            {
                // ── Persist the match seed to a .chmr (this scenario issues no unit orders). ──
                using (var recorder = new ReplayRecorder(chmrPath, "simrng-replay-test", seed))
                    Assert.Equal(seed, recorder.Seed);

                // ── Live run with the same seed. ──
                var (_, liveLoop) = BuildRngLoop(seed);
                uint[] live = StepCapturing(liveLoop, Ticks);

                // ── Replay: build a fresh loop seeded to a WRONG value, then let ReplayPlayer restore the
                //    recorded seed from the header BEFORE any tick. ──
                var (replayWorld, replayLoop) = BuildRngLoop(0xFFFFFFFFFFFFFFFFUL); // deliberately wrong
                var player = new ReplayPlayer(chmrPath, replayWorld);
                Assert.Equal(seed, player.Seed);            // seed survived the header round-trip
                Assert.Equal(seed, replayWorld.Rng.State);  // ReplayPlayer reseeded the world's RNG (overrode the wrong seed)

                var replay = new List<uint>(Ticks);
                replayLoop.OnChecksum = (_, hash) => replay.Add(hash);
                for (int i = 0; i < Ticks; i++)
                {
                    player.Flush(replayLoop.CurrentTick); // applies recorded orders (none here)
                    replayLoop.StepOnce();
                }

                Assert.Equal(live, replay.ToArray());
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>
        /// AC2 (back-compat, D6) — a v1 .chmr (no seed header) still loads, falling back to
        /// <see cref="EntityWorld.DEFAULT_RNG_SEED"/>. Guards that the VERSION 1→2 bump never bricks older
        /// replays. A non-default pre-seed proves the player actually reseeded (not coincidence).
        /// </summary>
        [Fact]
        public void V1Replay_WithoutSeed_FallsBackToDefaultSeed()
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_simrng_v1_{Guid.NewGuid():N}.chmr");
            try
            {
                // Hand-write a v1 header: magic + version(1) + pathLen(0) + EOF sentinel. No seed field.
                using (var w = new BinaryWriter(File.Open(chmrPath, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write((ushort)1);            // v1 — predates the seed header
                    w.Write((ushort)0);            // empty scenario path
                    w.Write(ReplayRecorder.EOF_SENTINEL);
                }

                var world = new EntityWorld();
                world.Rng.Seed(0xDEADUL);          // make it non-default first, so the reseed is observable
                var player = new ReplayPlayer(chmrPath, world);

                Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, player.Seed);
                Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, world.Rng.State); // proves the v1 path reseeded to default
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }

        /// <summary>
        /// Robustness (review patch, Story 1.5) — a v2-stamped .chmr whose header ends before the full 8-byte
        /// seed is truncated/corrupt. The loader must reject it with <see cref="InvalidDataException"/> (the
        /// documented ctor contract), NOT leak the raw <see cref="EndOfStreamException"/> that a short
        /// ReadUInt64 would throw — matching how bad-magic / unsupported-version headers are rejected.
        /// </summary>
        [Fact]
        public void V2Replay_TruncatedSeed_ThrowsInvalidData()
        {
            string chmrPath = Path.Combine(Path.GetTempPath(), $"chimera_simrng_trunc_{Guid.NewGuid():N}.chmr");
            try
            {
                // Hand-write a v2 header that stops mid-seed: magic + version(2) + pathLen(0) + only 3 of 8 seed bytes.
                using (var w = new BinaryWriter(File.Open(chmrPath, FileMode.Create)))
                {
                    w.Write(ReplayRecorder.MAGIC);
                    w.Write((ushort)2);                       // v2 — promises an 8-byte seed
                    w.Write((ushort)0);                       // empty scenario path
                    w.Write(new byte[] { 0x01, 0x02, 0x03 }); // truncated seed (3 of 8 bytes)
                }

                var world = new EntityWorld();
                Assert.Throws<InvalidDataException>(() => new ReplayPlayer(chmrPath, world));
            }
            finally
            {
                if (File.Exists(chmrPath)) File.Delete(chmrPath);
            }
        }
    }
}
