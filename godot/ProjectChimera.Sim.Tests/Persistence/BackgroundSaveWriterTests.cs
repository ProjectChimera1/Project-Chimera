#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-467 — regression pins for the off-game-thread save writer. The defect class: IssueSave/autosave ran
    /// <c>SaveGameFile.Write</c> (full-body serialization) and the store's blocking <c>File.WriteAllBytes</c> +
    /// <c>File.Replace</c> synchronously on the game thread, hitching a frame every 120 s autosave. These tests pin
    /// the fixed contract, all Godot-free/Tier-1:
    /// (1) <see cref="BackgroundSaveWriter.Enqueue"/> returns while the store write is still in flight — the calling
    ///     (game) thread is never blocked on disk I/O (a revert to a synchronous write inside Enqueue fails here);
    /// (2) the background pipeline's bytes are BYTE-IDENTICAL to a synchronous <c>SaveGameFile.Write</c>, and the
    ///     already-captured buffer is detached from the live sim — ticking the host after Enqueue cannot leak into
    ///     the written save (the "background write of an already-captured buffer" half of the DW);
    /// (3) writes are FIFO — two saves to the same slot land in issue order (last-issued wins on disk);
    /// (4) a store failure surfaces as a failed <see cref="BackgroundSaveWriter.SaveResult"/> (never swallowed) and
    ///     the chain keeps serving later saves.
    /// </summary>
    public class BackgroundSaveWriterTests
    {
        // ── Harness (the SaveLoadTests pattern: a real applied scenario so the serialized body is real) ──────────

        private sealed class Harness
        {
            public SimulationHost Host = null!;
            public ScenarioData Model = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        private static Harness BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ScenarioData model = GoldenApplierScenario.BuildModel();
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Harness { Host = host, Model = model, SlotDefs = slotDefs };
        }

        private static void Step(SimulationHost host, int n) { for (int i = 0; i < n; i++) host.StepOnce(); }

        private static SaveGameHeaderData Header(Harness h) => new()
        {
            CanonicalModelHash = CanonicalModelHash.Compute(h.Model),
            ContentHash        = ContentHash.Compute(new[] { h.SlotDefs[(int)Faction.Player1]! }, h.Host.AbilityRegistry, h.Host.ItemRegistry, null),
            Tick               = h.Host.CurrentTick,
            MapId              = h.Model.Id,
            Slots              = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
        };

        private static (SaveGameState state, SaveGameHeaderData header) CaptureNow(Harness h)
        {
            var table = CanonicalEffectDescriptorTable.Build(h.Host.AbilityRegistry, h.Host.ItemRegistry);
            return (SaveGameState.CaptureFrom(h.Host, table), Header(h));
        }

        private static byte[] SerializeSynchronously(SaveGameState state, SaveGameHeaderData header)
        {
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            return ms.ToArray();
        }

        /// <summary>An <see cref="ISaveStore"/> fake that records every write. <see cref="BlockGate"/> (when set)
        /// blocks Write until released — capped so a regression to a SYNCHRONOUS write inside Enqueue fails the test
        /// loudly (TimeoutException out of Enqueue) instead of hanging the runner. <see cref="FailWith"/> injects a
        /// per-slot store failure.</summary>
        private sealed class RecordingStore : ISaveStore
        {
            private readonly object _sync = new();
            public readonly List<(string Slot, byte[] Bytes)> Writes = new();
            public ManualResetEventSlim? BlockGate;
            public Func<string, Exception?>? FailWith;

            public void Write(string slot, byte[] bytes)
            {
                ManualResetEventSlim? gate = BlockGate;
                if (gate != null && !gate.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test gate never released — Write ran on the enqueueing thread?");
                Exception? ex = FailWith?.Invoke(slot);
                if (ex != null) throw ex;
                lock (_sync) Writes.Add((slot, (byte[])bytes.Clone()));
            }

            public IReadOnlyList<string> List() { lock (_sync) { var s = new List<string>(); foreach ((string slot, _) in Writes) if (!s.Contains(slot)) s.Add(slot); return s; } }
            public bool Exists(string slot) { lock (_sync) { foreach ((string s, _) in Writes) if (s == slot) return true; return false; } }
            public byte[]? Read(string slot) { lock (_sync) { for (int i = Writes.Count - 1; i >= 0; i--) if (Writes[i].Slot == slot) return Writes[i].Bytes; return null; } }
            public void Delete(string slot) { }
            public string PathFor(string slot) => slot;
        }

        // ── (1) the off-thread pin ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Enqueue_ReturnsWhileTheStoreWriteIsStillInFlight_NeverBlockingTheCallingThread()
        {
            Harness h = BuildApplied();
            Step(h.Host, 10);
            (SaveGameState state, SaveGameHeaderData header) = CaptureNow(h);

            var store = new RecordingStore { BlockGate = new ManualResetEventSlim(false) };
            var writer = new BackgroundSaveWriter(store);

            // If serialization/disk-write ran synchronously inside Enqueue, this call would sit blocked on the gate
            // (and the capped gate would throw TimeoutException out of Enqueue). The off-thread contract: Enqueue
            // returns immediately, the write is pending, and no result exists yet.
            writer.Enqueue("0", state, header);
            Assert.Equal(1, writer.PendingCount);
            Assert.False(writer.TryDequeueResult(out _));

            store.BlockGate.Set();
            Assert.True(writer.WaitForIdle(15_000), "background write never completed");
            Assert.Equal(0, writer.PendingCount);

            Assert.True(writer.TryDequeueResult(out BackgroundSaveWriter.SaveResult r));
            Assert.True(r.Success, r.Error);
            Assert.Equal("0", r.Slot);
            Assert.Equal(h.Host.CurrentTick, r.Tick);
            Assert.Single(store.Writes);
        }

        // ── (2) byte-identical + already-captured-buffer isolation ─────────────────────────────────────────────

        [Fact]
        public void BackgroundWrite_IsByteIdenticalToASynchronousWrite_AndDetachedFromTheLiveSim()
        {
            Harness h = BuildApplied();
            Step(h.Host, 10);
            (SaveGameState state, SaveGameHeaderData header) = CaptureNow(h);
            byte[] reference = SerializeSynchronously(state, header); // the pre-DW-467 synchronous bytes

            var store = new RecordingStore { BlockGate = new ManualResetEventSlim(false) };
            var writer = new BackgroundSaveWriter(store);
            writer.Enqueue("0", state, header);

            // The live sim keeps ticking while the background write is pending — the captured buffer must not move.
            Step(h.Host, 30);
            store.BlockGate.Set();
            Assert.True(writer.WaitForIdle(15_000), "background write never completed");

            Assert.Single(store.Writes);
            Assert.Equal(reference, store.Writes[0].Bytes); // no serialization drift vs the synchronous path

            // The written blob parses back to the SAVE tick (10), not the post-enqueue tick (40).
            using var parse = new MemoryStream(store.Writes[0].Bytes);
            (SaveGameHeaderData parsedHeader, SaveGameState _) = SaveGameFile.Read(parse);
            Assert.Equal(10u, parsedHeader.Tick);

            // Deterministic isolation pin: after the host advanced 30 ticks, re-serializing the SAME captured
            // buffers still yields the original bytes — CaptureFrom deep-copied everything off the live stores.
            // (Fails if any captured lane ever aliases a live sim array again.)
            Assert.Equal(reference, SerializeSynchronously(state, header));
        }

        // ── (3) FIFO order / last-issued-wins ──────────────────────────────────────────────────────────────────

        [Fact]
        public void TwoSavesToTheSameSlot_LandInIssueOrder_SoTheLastIssuedWinsOnDisk()
        {
            Harness h = BuildApplied();
            Step(h.Host, 10);
            (SaveGameState stateA, SaveGameHeaderData headerA) = CaptureNow(h); // tick 10
            Step(h.Host, 12);
            (SaveGameState stateB, SaveGameHeaderData headerB) = CaptureNow(h); // tick 22

            var store = new RecordingStore();
            var writer = new BackgroundSaveWriter(store);
            writer.Enqueue("0", stateA, headerA);
            writer.Enqueue("0", stateB, headerB);
            Assert.True(writer.WaitForIdle(15_000), "background writes never completed");

            Assert.Equal(2, store.Writes.Count);
            using (var first = new MemoryStream(store.Writes[0].Bytes))
                Assert.Equal(10u, SaveGameFile.Read(first).header.Tick);
            using (var last = new MemoryStream(store.Writes[1].Bytes))
                Assert.Equal(22u, SaveGameFile.Read(last).header.Tick); // last-issued is the slot's final content

            Assert.True(writer.TryDequeueResult(out BackgroundSaveWriter.SaveResult r1) && r1.Success);
            Assert.True(writer.TryDequeueResult(out BackgroundSaveWriter.SaveResult r2) && r2.Success);
            Assert.Equal(10u, r1.Tick); // results surface in issue order too
            Assert.Equal(22u, r2.Tick);
        }

        // ── (4) failure surfaces; the chain survives ───────────────────────────────────────────────────────────

        [Fact]
        public void AStoreWriteFailure_SurfacesAsAFailedResult_AndLaterSavesStillLand()
        {
            Harness h = BuildApplied();
            Step(h.Host, 10);
            (SaveGameState state, SaveGameHeaderData header) = CaptureNow(h);

            var store = new RecordingStore { FailWith = slot => slot == "bad" ? new IOException("disk full (test)") : null };
            var writer = new BackgroundSaveWriter(store);
            writer.Enqueue("bad", state, header);
            writer.Enqueue("0", state, header);
            Assert.True(writer.WaitForIdle(15_000), "background writes never completed");
            Assert.Equal(0, writer.PendingCount);

            Assert.True(writer.TryDequeueResult(out BackgroundSaveWriter.SaveResult failed));
            Assert.False(failed.Success);
            Assert.Equal("bad", failed.Slot);
            Assert.Contains("disk full (test)", failed.Error);

            Assert.True(writer.TryDequeueResult(out BackgroundSaveWriter.SaveResult ok));
            Assert.True(ok.Success, ok.Error);
            Assert.Equal("0", ok.Slot);

            Assert.Single(store.Writes);            // only the healthy save reached the store
            Assert.Equal("0", store.Writes[0].Slot);
        }

        // ── idle behavior ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void WaitForIdle_WithNothingPending_ReturnsImmediately()
        {
            var writer = new BackgroundSaveWriter(new RecordingStore());
            Assert.True(writer.WaitForIdle(0));
            Assert.Equal(0, writer.PendingCount);
            Assert.False(writer.TryDequeueResult(out _));
        }
    }
}
