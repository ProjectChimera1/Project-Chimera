#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 15-1 / DW-879 — the Godot-free TWO-PEER REJOIN HARNESS: the determinism core of snapshot+tail
    /// reconnect, proven end-to-end in Tier-1 with zero transport.
    ///
    /// <para>Two INDEPENDENT <see cref="SimulationHost"/>s (nothing shared) run in lockstep with every order
    /// riding the REAL wire path — per-slot <see cref="TickCommandPacket"/> → the shared
    /// <see cref="MergedTickBuilder"/> fan-in → <see cref="MergedTickApplier"/> — so the retained frame list
    /// (<see cref="MergedTickLog"/>, the story's server-side log component) IS the wire artifact, not a
    /// simulation of one. Peer B then drops (its slot submits EMPTY bundles — the Story 9.6 frozen-slot
    /// injector model), a SNAPSHOT is captured from the surviving peer through the full save codec
    /// (Write→Read over memory, so <c>Validate</c> runs — the networked-snapshot requirement), restored into a
    /// FRESH host, fast-forwarded through the retained tail, and resumed into live lockstep. The acceptance
    /// criterion is DW-879's own: byte-equal per-tick SimChecksum sequences after catch-up.</para>
    /// </summary>
    public class RejoinCatchUpHarnessTests
    {
        private const int Slots = 2;
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        // ── One wire-driven peer: a fresh applied host that consumes BUILT merged frames ─────────────────────

        private sealed class WirePeer
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
            public ScenarioData Model = null!;
            public List<(uint Tick, uint Hash)> Hashes = new();

            public static WirePeer Create()
            {
                FactionDefinition faction = Golden.GoldenApplierScenario.BuildFaction();
                var slotDefs = new FactionDefinition?[5];
                slotDefs[(int)Faction.Player1] = faction;
                slotDefs[(int)Faction.Player2] = faction;

                var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
                host.ChecksumInterval = 1;
                var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
                ScenarioData model = Golden.GoldenApplierScenario.BuildModel();
                ValidationResult r = new ScenarioValidator().Validate(model);
                Assert.True(r.Ok, r.Error);
                applier.Apply(r.Value);

                var peer = new WirePeer { Host = host, SlotDefs = slotDefs, Model = model };
                host.SetChecksumSink((t, h) => peer.Hashes.Add((t, h)));
                return peer;
            }

            /// <summary>Apply one built merged frame through the SHARED exec core, then advance one tick.</summary>
            public void ApplyFrameAndStep(byte[] merged, int len)
            {
                MergedTickApplier.Apply(merged, len, Host.World);
                Host.StepOnce();
            }
        }

        // ── The scripted order stream (deterministic; rides the real wire) ───────────────────────────────────

        /// <summary>The faction's units in ascending id (resolved live — recycling-safe for this scenario).</summary>
        private static List<int> UnitsOf(EntityWorld world, Faction f)
        {
            var ids = new List<int>();
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == f) ids.Add(i);
            return ids;
        }

        /// <summary>Deterministic oscillating Move orders for <paramref name="f"/>'s units at step
        /// <paramref name="step"/> (the LoopbackPeerSim script shape, routed through the wire instead of
        /// self-applied). Subjects ride as PACKED refs — the DW-945 wire contract.</summary>
        private static int WriteSlotPacket(byte[] buf, uint tick, int step, Faction f, EntityWorld world, bool empty)
        {
            if (empty)
                return TickCommandPacket.Write(buf, tick, f, System.Array.Empty<UnitOrder>(), 0);

            List<int> ids = UnitsOf(world, f);
            var orders = new UnitOrder[ids.Count];
            for (int k = 0; k < ids.Count; k++)
            {
                int id = ids[k];
                int tx = ((step * 3 + id * 7) % 30) - 15;
                int tz = ((step * 5 + id * 11) % 24) - 12;
                orders[k] = new UnitOrder(world.PackRef(id), UnitCommand.Move, Fixed.FromInt(tx), Fixed.FromInt(tz));
            }
            return TickCommandPacket.Write(buf, tick, f, orders, orders.Length);
        }

        /// <summary>Build one merged frame for <paramref name="step"/> from both slots (slot 1 empty when
        /// dropped) and return an independent copy. ANY live peer's world can script the orders — the streams
        /// are deterministic and identical across peers; <paramref name="scriptWorld"/> is the one used.</summary>
        private static byte[] BuildFrame(MergedTickBuilder builder, int step, EntityWorld scriptWorld, bool bDropped,
                                         out int len)
        {
            uint tick = (uint)(step + 1); // wire ticks are 1-based here (the parity-test convention)
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

            int n0 = WriteSlotPacket(buf, tick, step, SlotFaction[0], scriptWorld, empty: false);
            Assert.True(builder.Submit(0, buf, n0, out _));
            int n1 = WriteSlotPacket(buf, tick, step, SlotFaction[1], scriptWorld, empty: bDropped);
            Assert.True(builder.Submit(1, buf, n1, out _));

            Assert.True(builder.TryBuild(tick, out byte[] merged, out int mergedLen));
            var copy = new byte[mergedLen]; // the builder returns its REUSABLE scratch — copy synchronously
            System.Array.Copy(merged, copy, mergedLen);
            len = mergedLen;
            return copy;
        }

        private static byte[] Snapshot(WirePeer donor)
        {
            var table = CanonicalEffectDescriptorTable.Build(donor.Host.AbilityRegistry, donor.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(donor.Host, table);
            var header = new SaveGameHeaderData
            {
                CanonicalModelHash = CanonicalModelHash.Compute(donor.Model),
                ContentHash = ContentHash.Compute(new[] { donor.SlotDefs[(int)Faction.Player1]! },
                                                  donor.Host.AbilityRegistry, donor.Host.ItemRegistry, null),
                Tick = donor.Host.CurrentTick,
                MapId = donor.Model.Id,
                Slots = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
            };
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            return ms.ToArray();
        }

        /// <summary>Restore a snapshot blob into a FRESH applied host through the FULL read path (header gates +
        /// Validate run — the networked-snapshot requirement; RestoreInto alone never validates).</summary>
        private static WirePeer RestoreRejoiner(byte[] blob)
        {
            WirePeer rejoiner = WirePeer.Create();
            using var read = new MemoryStream(blob);
            (SaveGameHeaderData _, SaveGameState st) = SaveGameFile.Read(read, "rejoin-snapshot");
            var table = CanonicalEffectDescriptorTable.Build(rejoiner.Host.AbilityRegistry, rejoiner.Host.ItemRegistry);
            st.RestoreInto(rejoiner.Host, table, rejoiner.SlotDefs);
            rejoiner.Hashes.Clear(); // pre-restore boot hashes are meaningless for the comparison
            return rejoiner;
        }

        // ── The DW-879 acceptance test ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void TwoPeerRejoin_SnapshotPlusTail_ResumesWithByteEqualChecksums()
        {
            const int DropStep = 90;      // B drops here (log arms — the freeze-commit moment)
            const int SnapshotStep = 140; // the donor snapshot boundary (between ticks)
            const int RejoinStep = 180;   // B′ has caught up and resumes live input here
            const int EndStep = 220;

            WirePeer a = WirePeer.Create();
            WirePeer b = WirePeer.Create();
            var builder = new MergedTickBuilder(Slots, SlotFaction);
            var log = new MergedTickLog();
            byte[]? snapshot = null;
            WirePeer? bPrime = null;

            for (int step = 0; step < EndStep; step++)
            {
                bool bDropped = step >= DropStep && bPrime == null;
                if (step == DropStep) log.Arm(); // retention starts at freeze-commit (D-2)

                byte[] frame = BuildFrame(builder, step, a.Host.World, bDropped, out int len);
                log.Append(step, frame, len);   // no-op until armed

                a.ApplyFrameAndStep(frame, len);
                if (bPrime != null) bPrime.ApplyFrameAndStep(frame, len);      // resumed rejoiner lives the stream
                else if (step < DropStep) b.ApplyFrameAndStep(frame, len);     // B until the drop

                if (step == SnapshotStep - 1)
                    snapshot = Snapshot(a);      // captured BETWEEN ticks (the save path's own legal boundary)

                if (step == RejoinStep - 1)
                {
                    // ── The rejoin: restore the donor snapshot, fast-forward the retained tail, join live. ──
                    bPrime = RestoreRejoiner(snapshot!);
                    Assert.Equal(a.Hashes[SnapshotStep - 1].Tick, bPrime.Host.CurrentTick); // restored AT the boundary

                    var tail = new List<byte[]>();
                    Assert.True(log.TryCopyRange(SnapshotStep, tail), "tail must be serviceable from the snapshot boundary");
                    foreach (byte[] f in tail) bPrime.ApplyFrameAndStep(f, f.Length);

                    // Post-catch-up agreement: B′'s fast-forwarded hashes equal A's live ones tick-for-tick.
                    for (int i = 0; i < bPrime.Hashes.Count; i++)
                    {
                        (uint tick, uint hash) = bPrime.Hashes[i];
                        (uint aTick, uint aHash) = a.Hashes[SnapshotStep + i];
                        Assert.Equal(aTick, tick);
                        Assert.True(aHash == hash,
                            $"catch-up diverged at tick {tick}: donor 0x{aHash:X8} vs rejoiner 0x{hash:X8}");
                    }
                }
            }

            // Pre-drop: A and B agreed every tick (the lockstep baseline).
            for (int i = 0; i < DropStep; i++)
            {
                Assert.Equal(a.Hashes[i].Tick, b.Hashes[i].Tick);
                Assert.Equal(a.Hashes[i].Hash, b.Hashes[i].Hash);
            }

            // Post-rejoin THROUGH the end: the rejoiner lives the same stream byte-for-byte (DW-879's criterion).
            Assert.NotNull(bPrime);
            int total = bPrime!.Hashes.Count;
            Assert.True(total >= EndStep - SnapshotStep, "the rejoiner ran catch-up + live ticks");
            for (int i = 0; i < total; i++)
            {
                Assert.Equal(a.Hashes[SnapshotStep + i].Tick, bPrime.Hashes[i].Tick);
                Assert.Equal(a.Hashes[SnapshotStep + i].Hash, bPrime.Hashes[i].Hash);
            }

            // Non-degeneracy (the IndependentPeerSimQuorumTests guard): a frozen sim must not pass vacuously.
            var distinct = new HashSet<uint>();
            foreach ((uint _, uint h) in a.Hashes) distinct.Add(h);
            Assert.True(distinct.Count > EndStep / 2, $"only {distinct.Count} distinct hashes over {EndStep} ticks");
        }

        [Fact]
        public void TamperedSnapshot_IsRefusedAtRead_NeverDetonatesMidRestore()
        {
            // The networked-snapshot safety rail: a corrupt blob fails the file integrity/validation gates in
            // SaveGameFile.Read — loudly, BEFORE RestoreInto could half-apply it (RestoreInto never validates).
            WirePeer a = WirePeer.Create();
            var builder = new MergedTickBuilder(Slots, SlotFaction);
            for (int step = 0; step < 20; step++)
            {
                byte[] frame = BuildFrame(builder, step, a.Host.World, bDropped: false, out int len);
                a.ApplyFrameAndStep(frame, len);
            }
            byte[] blob = Snapshot(a);
            blob[blob.Length / 2] ^= 0xFF; // flip one body byte

            using var read = new MemoryStream(blob);
            Assert.ThrowsAny<System.Exception>(() => SaveGameFile.Read(read, "tampered-rejoin-snapshot"));
        }

        [Fact]
        public void TailOlderThanRetention_ForcesAFresherSnapshot_NeverAPartialCatchUp()
        {
            // D-2: a snapshot from BEFORE retention began cannot be serviced — the rejoin flow must re-request
            // a fresher snapshot (the donor is live; a newer boundary always exists) rather than fast-forward a
            // gapped tail into a guaranteed desync.
            var log = new MergedTickLog();
            log.Arm();
            var frame = new byte[16];
            for (long t = 100; t < 120; t++) log.Append(t, frame, frame.Length);
            Assert.False(log.TryCopyRange(90, new List<byte[]>()));
            Assert.True(log.TryCopyRange(110, new List<byte[]>()));
        }
    }
}
