#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Combat;   // DamageType / ArmorType (in-flight projectile fixture)
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// Story 11.3 (FR-67) — the acceptance proof for the SP full-world save/load serializer, all Tier-1 headless:
    /// (a) BYTE-IDENTICAL RESUME — a save taken at tick K, loaded into a fresh scenario-applied host, produces a
    ///     SimChecksum stream over the next 300 ticks byte-identical to an uninterrupted reference run;
    /// (b) the same with a live timed <see cref="Modifier"/> + <see cref="PersistentEffect"/> injected before the save
    ///     (the descriptor round-trip via the <see cref="CanonicalEffectDescriptorTable"/>);
    /// (c) fail-closed format cases (bad magic, older/newer version, content-hash mismatch, unknown section, truncation);
    /// (d) format stability (save → load → save byte-identical);
    /// (e) the hash <c>AlgoVersion</c> pins are unchanged (save/load folds nothing new, moves no golden).
    /// Proved with in-memory checksum-stream comparison (no committed golden).
    /// </summary>
    public class SaveLoadTests
    {
        private const int SaveAtTick = 90;   // K
        private const int ResumeTicks = 300; // must exceed the 300-tick acceptance floor

        // ── Harness ─────────────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public SimulationHost Host = null!;
            public ScenarioApplier Applier = null!;
            public ScenarioData Model = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        private static Harness BuildApplied(AbilityRegistry? abilities = null, ScenarioData? model = null)
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction,
                                             registry: abilities);
            host.ChecksumInterval = 1; // checksum every tick → an exact located divergence
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            model ??= GoldenApplierScenario.BuildModel();
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Harness { Host = host, Applier = applier, Model = model, SlotDefs = slotDefs };
        }

        private static void Step(SimulationHost host, int n) { for (int i = 0; i < n; i++) host.StepOnce(); }

        private static List<GoldenChecksumReplay.Sample> Capture(SimulationHost host, int n)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(n);
            host.SetChecksumSink((t, h) => seq.Add(new GoldenChecksumReplay.Sample(t, h)));
            for (int i = 0; i < n; i++) host.StepOnce();
            return seq;
        }

        private static void AssertSame(IReadOnlyList<GoldenChecksumReplay.Sample> expected,
                                       IReadOnlyList<GoldenChecksumReplay.Sample> actual, string what)
        {
            GoldenChecksumReplay.Divergence? d = GoldenChecksumReplay.CompareSequences(expected, actual);
            Assert.True(d is null, d is null ? "" : $"{what}: {GoldenChecksumReplay.DescribeDivergence(d.Value)}");
        }

        private static SaveGameHeaderData Header(Harness h) => new()
        {
            CanonicalModelHash = CanonicalModelHash.Compute(h.Model),
            ContentHash        = ContentHash.Compute(new[] { h.SlotDefs[(int)Faction.Player1]! }, h.Host.AbilityRegistry, h.Host.ItemRegistry, null),
            Tick               = h.Host.CurrentTick,
            MapId              = h.Model.Id,
            Slots              = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
        };

        /// <summary>Full round-trip a save blob: capture → Write → Read → RestoreInto a fresh applied host.</summary>
        private static SimulationHost SaveThenLoadIntoFresh(Harness saved, AbilityRegistry? abilities, out byte[] blob,
                                                            ScenarioData? model = null)
        {
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, Header(saved));
            blob = ms.ToArray();

            Harness load = BuildApplied(abilities, model);
            using var read = new MemoryStream(blob);
            (SaveGameHeaderData _, SaveGameState st) = SaveGameFile.Read(read);
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            st.RestoreInto(load.Host, loadTable, load.SlotDefs);
            return load.Host;
        }

        /// <summary>Assert a save taken at tick K (after <paramref name="perturbAtK"/> injects non-default state on BOTH
        /// the reference and the save host) resumes byte-identical over N ticks. The full file round-trip is exercised.</summary>
        private void AssertResumeByteIdentical(System.Action<SimulationHost> perturbAtK, AbilityRegistry? abilities,
                                               int k, int n, ScenarioData? model = null,
                                               System.Action<SimulationHost>? assertRestored = null)
        {
            Harness reference = BuildApplied(abilities, model);
            Step(reference.Host, k);
            perturbAtK(reference.Host);
            List<GoldenChecksumReplay.Sample> refSeq = Capture(reference.Host, n);

            Harness saved = BuildApplied(abilities, model);
            Step(saved.Host, k);
            perturbAtK(saved.Host);
            SimulationHost resumed = SaveThenLoadIntoFresh(saved, abilities, out _, model);

            Assert.Equal((uint)k, resumed.CurrentTick);
            assertRestored?.Invoke(resumed);
            List<GoldenChecksumReplay.Sample> resumeSeq = Capture(resumed, n);
            AssertSame(refSeq, resumeSeq, "resume diverged");
        }

        // ── (a) Byte-identical resume over the economy golden scenario ─────────────────────────

        [Fact]
        public void SaveLoad_ResumeIsByteIdentical_OverGoldenScenario()
        {
            // Reference: one uninterrupted run, capturing ticks K+1..K+ResumeTicks.
            Harness reference = BuildApplied();
            Step(reference.Host, SaveAtTick);
            List<GoldenChecksumReplay.Sample> refSeq = Capture(reference.Host, ResumeTicks);

            // Save host: same start, run to K, then save + load into a fresh host and resume.
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);
            SimulationHost resumed = SaveThenLoadIntoFresh(saved, null, out _);

            Assert.Equal((uint)SaveAtTick, resumed.CurrentTick); // resumes at the saved tick
            List<GoldenChecksumReplay.Sample> resumeSeq = Capture(resumed, ResumeTicks);

            AssertSame(refSeq, resumeSeq, "loaded save did not resume byte-identically");
        }

        // ── (b) Byte-identical resume with a live Modifier + PersistentEffect (descriptor round-trip) ──

        private static Modifier BuildTestModifier() =>
            new Modifier(id: 42, durationTicks: 500, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(5), moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static PersistentEffect BuildTestPersistent() =>
            new PersistentEffect(initialEffect: null,
                                 periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(1)),
                                 expireEffect: null, periodTicks: 15, periodCount: 40);

        /// <summary>An ability whose effect graph GRANTS the test modifier + persistent, so the canonical table
        /// (built from the registry) can round-trip both descriptor slots by index. Shared across ref/save/load hosts
        /// so the descriptor references are identical.</summary>
        private static AbilityRegistry BuildAbilityRegistry(Modifier mod, PersistentEffect pe)
        {
            var graph = new SequenceEffect(new ApplyModifierEffect(mod), pe);
            var def = new AbilityDefinition { Id = "test_grant", EffectGraph = graph };
            return new AbilityRegistry(new[] { def });
        }

        [Fact]
        public void SaveLoad_ResumeIsByteIdentical_WithLiveModifierAndPersistent()
        {
            Modifier mod = BuildTestModifier();
            PersistentEffect pe = BuildTestPersistent();
            AbilityRegistry registry = BuildAbilityRegistry(mod, pe);

            void Inject(SimulationHost host)
            {
                // Entity 0 is the first scenario-placed worker (Player1). Install a timed stat modifier + a HoT.
                Assert.True(host.World.IsAlive(0));
                Assert.True(host.Modifiers.Apply(0, mod, casterId: 0, casterFaction: Faction.Player1));
                host.Modifiers.InstallPersistent(0, pe, casterId: 0, casterFaction: Faction.Player1);
                Assert.True(host.Modifiers.CountAt(0) >= 2);
            }

            // Reference.
            Harness reference = BuildApplied(registry);
            Step(reference.Host, SaveAtTick);
            Inject(reference.Host);
            List<GoldenChecksumReplay.Sample> refSeq = Capture(reference.Host, ResumeTicks);

            // Save + load.
            Harness saved = BuildApplied(registry);
            Step(saved.Host, SaveAtTick);
            Inject(saved.Host);
            SimulationHost resumed = SaveThenLoadIntoFresh(saved, registry, out _);

            // The descriptor slots round-tripped by canonical index.
            Assert.True(resumed.Modifiers.CountAt(0) >= 2);
            List<GoldenChecksumReplay.Sample> resumeSeq = Capture(resumed, ResumeTicks);

            AssertSame(refSeq, resumeSeq, "loaded save with live modifier/persistent did not resume byte-identically");
        }

        // ── (d) Format stability: save → load → save is byte-identical ─────────────────────────

        [Fact]
        public void SaveLoad_FormatIsStable_RoundTrip()
        {
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);

            SimulationHost resumed = SaveThenLoadIntoFresh(saved, null, out byte[] blob1);

            // Re-capture the SAME state off the loaded host and re-serialize — must be byte-identical.
            var table = CanonicalEffectDescriptorTable.Build(resumed.AbilityRegistry, resumed.ItemRegistry);
            SaveGameState state2 = SaveGameState.CaptureFrom(resumed, table);
            using var ms = new MemoryStream();
            var hdr = new SaveGameHeaderData
            {
                CanonicalModelHash = CanonicalModelHash.Compute(saved.Model),
                ContentHash        = ContentHash.Compute(new[] { saved.SlotDefs[(int)Faction.Player1]! }, resumed.AbilityRegistry, resumed.ItemRegistry, null),
                Tick               = resumed.CurrentTick,
                MapId              = saved.Model.Id,
                Slots              = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
            };
            SaveGameFile.Write(ms, state2, hdr);
            byte[] blob2 = ms.ToArray();

            Assert.Equal(blob1, blob2);
        }

        // ── (c) Fail-closed format cases ───────────────────────────────────────────────────────

        private static byte[] ValidBlob(out SaveGameHeaderData header)
        {
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);
            header = Header(saved);
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            return ms.ToArray();
        }

        // ── Review fix: Validate() bounds free-list ELEMENTS, not just the list length ─────────────────────────────
        // A corrupt/hand-edited save carrying an out-of-range or duplicated slot id used to clear the whole
        // fail-closed gate and detonate later inside Create() (IndexOutOfRange, or one slot handed to two spawns).

        [Fact]
        public void Validate_FreeListEntryOutOfRange_ThrowsWithMessage()
        {
            (SaveGameState state, _) = CaptureValid();
            state.EntFreeList = new[] { EntityWorld.MAX_ENTITIES + 5 };
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("free-list", ex.Message);
            Assert.Contains("outside", ex.Message);
        }

        [Fact]
        public void Validate_FreeListEntryNegative_ThrowsWithMessage()
        {
            (SaveGameState state, _) = CaptureValid();
            state.BFreeList = new[] { -1 };
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("free-list", ex.Message);
        }

        [Fact]
        public void Validate_DuplicateFreeListEntry_ThrowsWithMessage()
        {
            (SaveGameState state, _) = CaptureValid();
            state.EntFreeList = new[] { 3, 7, 3 };
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("more than once", ex.Message);
        }

        [Fact]
        public void Validate_CleanFreeList_Passes()
        {
            (SaveGameState state, _) = CaptureValid();
            state.EntFreeList = new[] { 3, 7, 11 };
            state.Validate("t"); // must not throw
        }

        // ── DW-581: the new generation lane feeds a SHIFT, not an index, so a corrupt value never crashes — it
        //    silently breaks reference resolution instead (a live entity's own freshly-packed ref stops resolving,
        //    e.g. an assassination leader reads as dead ⇒ a bogus verdict on the resumed tick). Reject it at the gate.

        [Fact]
        public void Validate_EntityGenerationAboveThePackableMax_ThrowsWithMessage()
        {
            (SaveGameState state, _) = CaptureValid();
            int[] lane = state.Ent[(int)SaveGameState.EA.Generation];
            Assert.NotEmpty(lane);
            lane[0] = EntityWorld.MAX_PACKABLE_GENERATION + 1; // one past what PackRef can encode
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("generation", ex.Message);
        }

        [Fact]
        public void Validate_NegativeEntityGeneration_ThrowsWithMessage()
        {
            (SaveGameState state, _) = CaptureValid();
            state.Ent[(int)SaveGameState.EA.Generation][0] = -1; // unreachable in the sim (Create only increments from 0)
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("generation", ex.Message);
        }

        [Fact]
        public void Validate_EntityGenerationAtThePackableMax_Passes()
        {
            // The bound is inclusive: MAX_PACKABLE_GENERATION still round-trips through PackRef/TryResolveRef, so the
            // gate must not reject it (an off-by-one here would fail-closed on a legitimate save).
            (SaveGameState state, _) = CaptureValid();
            state.Ent[(int)SaveGameState.EA.Generation][0] = EntityWorld.MAX_PACKABLE_GENERATION;
            state.Validate("t"); // must not throw
        }

        [Fact]
        public void PackRef_AtThePackableMaxGeneration_StillResolves()
        {
            // Pins the constant to the packing it claims to bound — if REF_SLOT_BITS or the constant drifts apart,
            // the save-gate bound above would be validating the wrong number.
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            w.Generation[id] = EntityWorld.MAX_PACKABLE_GENERATION;
            int packed = w.PackRef(id);
            Assert.True(packed > 0);                                    // did not overflow into the sign bit
            Assert.True(w.TryResolveRef(packed, out int back) && back == id);
        }

        // ── Review fix: ShopStock must not be ALIASED between the sim store and the save state ────────────────────

        [Fact]
        public void CaptureThenRestore_DoesNotAliasShopStockArrays()
        {
            Harness h = BuildApplied();
            Step(h.Host, 5);
            BuildingStore b = h.Host.Buildings;
            Assert.True(b.Count > 0);
            b.ShopStock[0] = new[] { "item_a", "item_b" };

            var table = CanonicalEffectDescriptorTable.Build(h.Host.AbilityRegistry, h.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(h.Host, table);

            // Capture must have copied, not aliased: mutating the LIVE store cannot reach the captured state.
            b.ShopStock[0][0] = "MUTATED";
            Assert.Equal("item_a", state.BShopStock[0][0]);

            // Restore must copy too: the restored store must not share the state's array either.
            Harness dest = BuildApplied();
            var destTable = CanonicalEffectDescriptorTable.Build(dest.Host.AbilityRegistry, dest.Host.ItemRegistry);
            state.RestoreInto(dest.Host, destTable, dest.SlotDefs);
            Assert.Equal("item_a", dest.Host.Buildings.ShopStock[0][0]);
            dest.Host.Buildings.ShopStock[0][0] = "MUTATED_AGAIN";
            Assert.Equal("item_a", state.BShopStock[0][0]);
        }

        [Fact]
        public void Load_BadMagic_ThrowsWithMessage()
        {
            var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x00 };
            using var ms = new MemoryStream(bytes);
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("magic", ex.Message);
        }

        [Fact]
        public void Load_OlderFormatVersion_ThrowsWithMessage()
        {
            // Version-RELATIVE (DW-581): the previous format version is the realistic older save, and it must be
            // rejected here at the header with the "older game version" message — not parsed and then reported as a
            // corrupt body. A hard-coded 0 would keep testing a version no build ever wrote once FormatVersion moves.
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            { w.Write(SaveGameFile.MAGIC); w.Write((ushort)(SaveGameFile.FormatVersion - 1)); }
            ms.Position = 0;
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("older", ex.Message);
        }

        [Fact]
        public void Load_NewerFormatVersion_ThrowsWithMessage()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            { w.Write(SaveGameFile.MAGIC); w.Write((ushort)(SaveGameFile.FormatVersion + 1)); }
            ms.Position = 0;
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("newer", ex.Message);
        }

        [Fact]
        public void Load_ContentHashMismatch_ThrowsWithMessage()
        {
            byte[] blob = ValidBlob(out SaveGameHeaderData header);
            using var ms = new MemoryStream(blob);
            (SaveGameHeaderData readHeader, SaveGameState _) = SaveGameFile.Read(ms); // structural read passes
            // The content-value gate (run by the loader after rebuilding content) rejects a drifted map/content hash.
            var ex = Assert.Throws<InvalidDataException>(
                () => readHeader.ThrowIfContentMismatch(header.CanonicalModelHash, header.ContentHash + 1));
            Assert.Contains("content", ex.Message);

            var ex2 = Assert.Throws<InvalidDataException>(
                () => readHeader.ThrowIfContentMismatch(header.CanonicalModelHash + 1, header.ContentHash));
            Assert.Contains("map", ex2.Message);
        }

        [Fact]
        public void Load_UnknownSectionTag_ThrowsWithMessage()
        {
            // Craft a body = one frame carrying an unrecognized tag (0x7F), then the zero-length terminator.
            byte[] body;
            using (var bm = new MemoryStream())
            {
                using (var bw = new BinaryWriter(bm, System.Text.Encoding.UTF8, leaveOpen: true))
                { bw.Write(1); bw.Write((byte)0x7F); bw.Write(0); } // frame len=1, unknown tag, terminator
                body = bm.ToArray();
            }
            ulong bodyHash = SaveGameFile.Fnv64(body);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(SaveGameFile.MAGIC);
                w.Write(SaveGameFile.FormatVersion);
                w.Write(SimChecksum.AlgoVersion);
                w.Write(CanonicalModelHash.AlgoVersion);
                w.Write(StartStateHash.AlgoVersion);
                w.Write(0UL); w.Write(0UL); w.Write(bodyHash); // model, content, body hashes
                w.Write(0u);                     // tick
                w.Write("");                     // map id
                w.Write(0);                      // 0 slots
                w.Write(body);
            }
            ms.Position = 0;
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("unknown section", ex.Message);
        }

        [Fact]
        public void Load_Truncated_ThrowsWithMessage()
        {
            byte[] blob = ValidBlob(out _);
            // Cut the blob to just past the header magic so the body is missing → truncated.
            var truncated = new byte[blob.Length / 2];
            System.Array.Copy(blob, truncated, truncated.Length);
            using var ms = new MemoryStream(truncated);
            Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
        }

        // ── (e) The save/load path folds nothing new and moves no golden ───────────────────────

        [Fact]
        public void SaveLoad_LeavesHashAlgoVersionsUnchanged()
        {
            Assert.Equal(23, SimChecksum.AlgoVersion); // v23 = DW-78 bounded worker-gather-state fold (GatherState/GatherTarget/CarryAmount/CarryResourceType/GateClosedTicks)
            Assert.Equal(14, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        // ── Story 11.6 — a partially-filled depth-5 production queue + a running head timer round-trips and resumes
        //    byte-identical. If any of the widened queue slots or the head timer failed to serialize/restore, the
        //    resumed head would complete/advance on a different tick and the SimChecksum stream would diverge. ──

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithPartiallyFilledProductionQueue()
        {
            static void PerturbQueue(SimulationHost h)
            {
                // A live producer carrying a partial depth-5 queue (head + two waiting) and a running head timer — the
                // exact "save mid-queue" state. Written directly onto the folded BuildingStore so the save must carry
                // every widened slot; the encoded values need only be deterministic (both ref and save hosts get them).
                int b = h.Buildings.Create(new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.FromInt(-30)),
                                           Faction.Player1, BuildingType.Barracks);
                Assert.True(b >= 0);
                h.Buildings.ConstructionTimer[b] = Fixed.Zero; // operational
                int head = h.Buildings.HeadIndex(b);
                // DISTINCT non-zero encodings in EVERY one of the five widened slots (not repeated 1s) so a dropped
                // lane (e.g. pq4 never serialized) or a lane↔slot swap among pq/pq1/pq2 cannot round-trip green — an
                // all-1s fill folds identically whether or not each lane maps to its own slot.
                h.Buildings.ProductionQueue[head + 0] = 1; // head
                h.Buildings.ProductionQueue[head + 1] = 2; // waiting
                h.Buildings.ProductionQueue[head + 2] = 3; // waiting
                h.Buildings.ProductionQueue[head + 3] = 4; // waiting
                h.Buildings.ProductionQueue[head + 4] = 5; // waiting (the deepest lane — never non-zero before this)
                h.Buildings.ProductionTimer[b] = Fixed.FromInt(4); // head running (completes within the resume window)
            }

            AssertResumeByteIdentical(PerturbQueue, abilities: null, k: SaveAtTick, n: ResumeTicks,
                assertRestored: h =>
                {
                    // Every widened slot restored to ITS OWN distinct value + the head timer round-tripped.
                    int b = h.Buildings.Count - 1; // the producer created by the perturb (last-created slot)
                    int head = h.Buildings.HeadIndex(b);
                    Assert.Equal((byte)1, h.Buildings.ProductionQueue[head + 0]);
                    Assert.Equal((byte)2, h.Buildings.ProductionQueue[head + 1]);
                    Assert.Equal((byte)3, h.Buildings.ProductionQueue[head + 2]);
                    Assert.Equal((byte)4, h.Buildings.ProductionQueue[head + 3]);
                    Assert.Equal((byte)5, h.Buildings.ProductionQueue[head + 4]);
                    Assert.Equal(Fixed.FromInt(4).Raw, h.Buildings.ProductionTimer[b].Raw);
                });
        }

        // ── #5a — recycle: non-empty free list + a bumped generation round-trip byte-identical ─────────────────

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithRecycledSlots()
        {
            static void PerturbRecycle(SimulationHost h)
            {
                h.World.Destroy(3);                        // free entity slot 3 → the entity free list is non-empty
                h.Buildings.Destroy(0);                    // free building slot 0
                h.Buildings.Create(new FixedVec3(Fixed.FromInt(-40), Fixed.Zero, Fixed.FromInt(10)),
                                   Faction.Player1, BuildingType.Barracks); // reuses slot 0 → Generation[0] bumps to 1
            }

            AssertResumeByteIdentical(PerturbRecycle, abilities: null, k: SaveAtTick, n: ResumeTicks,
                assertRestored: h =>
                {
                    Assert.Contains(3, h.World.CaptureFreeList());   // the recycled entity slot round-tripped in the free list
                    Assert.Equal(1, h.Buildings.Generation[0]);      // the bumped generation round-tripped
                });
        }

        // ── DW-581 — the ENTITY recycle generation is a save lane too (BuildingStore/HeroStore/ItemStore already were) ──
        // EntityWorld.Generation is the ABA half of a packed entity ref: PackRef stamps it in, TryResolveRef demands it
        // back. Dropping it at the save boundary resumed the world with every slot at generation 0, so a packed ref
        // taken BEFORE a recycle resolved against the NEW occupant — precisely the retarget DW-184's PackRef exists to
        // stop, reintroduced by save/load. RecycledEntitySlot is destroyed then immediately re-Created, so the LIFO
        // free list hands the same slot straight back and its generation bumps to 1.

        private const int RecycledEntitySlot = 3;

        /// <summary>Free + immediately re-fill entity <see cref="RecycledEntitySlot"/> so its recycle generation is 1
        /// at save time. Asserts the precondition both ways, so the fixture can never silently stop recycling.</summary>
        private static void RecycleEntitySlot(SimulationHost h)
        {
            Assert.True(h.World.IsAlive(RecycledEntitySlot));
            Assert.Equal(0, h.World.Generation[RecycledEntitySlot]);            // gen 0 ⇒ PackRef == the bare id
            h.World.Destroy(RecycledEntitySlot);
            int reused = h.World.Create(new FixedVec3(Fixed.FromInt(-25), Fixed.Zero, Fixed.FromInt(25)),
                                        Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            Assert.Equal(RecycledEntitySlot, reused);                            // LIFO: the same slot came back
            Assert.Equal(1, h.World.Generation[RecycledEntitySlot]);             // …and its generation bumped
        }

        [Fact]
        public void SaveLoad_RestoresEntityRecycleGeneration_SoStalePackedRefsStayStale()
        {
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);

            // A packed ref MINTED BEFORE the recycle (generation 0 ⇒ the bare id) — the cross-tick handle a holder
            // would have been carrying. It must NOT resolve against the slot's new occupant, before or after a save.
            int stalePackedRef = saved.Host.World.PackRef(RecycledEntitySlot);
            Assert.Equal(RecycledEntitySlot, stalePackedRef);
            RecycleEntitySlot(saved.Host);
            Assert.False(saved.Host.World.TryResolveRef(stalePackedRef, out _)); // the live world already refuses it

            SimulationHost resumed = SaveThenLoadIntoFresh(saved, null, out _);

            // The lane itself round-tripped (0 here without the DW-581 save lane — the fresh re-apply's value).
            Assert.Equal(1, resumed.World.Generation[RecycledEntitySlot]);
            // …and the property that lane exists for: the stale handle stays stale across the save boundary. Without
            // the lane this resolves TRUE and hands the caller the slot's NEW occupant (the ABA retarget).
            Assert.True(resumed.World.IsAlive(RecycledEntitySlot));              // the slot IS live — so a bare-id check would pass
            Assert.False(resumed.World.TryResolveRef(stalePackedRef, out _));
            // The CURRENT handle still resolves, so the fix rejects staleness rather than rejecting everything.
            Assert.True(resumed.World.TryResolveRef(resumed.World.PackRef(RecycledEntitySlot), out int live));
            Assert.Equal(RecycledEntitySlot, live);
        }

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithRecycledEntitySlot()
        {
            // The determinism half: the new lane carries UNFOLDED state, so restoring it must move no checksum. (It
            // cannot: the reference run and the resumed run now hold the SAME generations, where before the resumed
            // run silently held zeros.)
            AssertResumeByteIdentical(RecycleEntitySlot, abilities: null, k: SaveAtTick, n: ResumeTicks,
                assertRestored: h => Assert.Equal(1, h.World.Generation[RecycledEntitySlot]));
        }

        [Fact]
        public void CaptureThenRestore_ZeroesTheGenerationTailPastTheHighWaterMark()
        {
            // The overlay must be TOTAL for this array: Create() re-defaults every other per-entity field when it hands
            // out a slot, but on the fresh-APPEND path it deliberately leaves Generation alone ("fresh slot — stays
            // 0"). A destination world carrying a stale non-zero tail would therefore hand the next appended entity a
            // generation the saved world never had. Poke a tail slot the save cannot cover and require restore to wipe it.
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);

            Harness dest = BuildApplied();
            int tail = state.EntHwm + 5;
            Assert.True(tail < EntityWorld.MAX_ENTITIES);
            dest.Host.World.Generation[tail] = 9;                 // stale residue past the saved high-water mark

            var destTable = CanonicalEffectDescriptorTable.Build(dest.Host.AbilityRegistry, dest.Host.ItemRegistry);
            state.RestoreInto(dest.Host, destTable, dest.SlotDefs);

            Assert.Equal(0, dest.Host.World.Generation[tail]);    // the saved world's value there is 0 by construction
        }

        // ── #5b — rich non-default state: hero + research + in-flight projectile + DSL var/timer, byte-identical ──

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithHeroResearchProjectileAndDsl()
        {
            static void PerturbRich(SimulationHost h)
            {
                // A persistent hero row (folded Level/Xp/growth).
                int slot = h.Heroes.Mint(new HeroId(777), entityId: 0, level: 3, xp: Fixed.FromInt(50),
                                         maxLevel: 10, baseXp: Fixed.FromInt(100), xpGrowth: Fixed.FromInt(1),
                                         xpShareRadius: Fixed.FromInt(8),
                                         xpGainFactor: Fixed.FromFloat(2f)); // DW-26: non-neutral multiplier must round-trip
                if (h.World.HighWaterMark > 0) h.World.HeroIndex[0] = h.Heroes.PackRef(slot);

                // Completed research + cumulative deltas (the jagged per-faction arrays). Idle (InProgressIndex stays
                // -1) so the golden faction — which authors no research — is never indexed by ResearchSystem.
                h.Research.EnsureCapacity(Faction.Player1, 2);
                h.Research.CompletedLevels[(int)Faction.Player1][0] = 2;
                h.Research.CumulativeMaxHealthDelta[(int)Faction.Player1][0]    = Fixed.FromInt(10);
                h.Research.CumulativeAttackDamageDelta[(int)Faction.Player1][0] = Fixed.FromInt(5);

                // An in-flight projectile (Player2 shell toward a Player1 worker) — ProjectileStore round-trip.
                h.Projectiles.Spawn(FixedVec3.Zero, targetId: 1,
                                    new FixedVec3(Fixed.FromInt(42), Fixed.Zero, Fixed.Zero),
                                    Fixed.FromInt(5), DamageType.Normal, ArmorType.Unarmored, Faction.Player2,
                                    speed: Fixed.FromInt(10));

                // DSL: an (undeclared-append) global + a live timer (decremented each director tick → folded evolution).
                h.Vars.SetInt("saved_flag", 0, 42);
                h.Vars.TimerSet("countdown", 100);
            }

            AssertResumeByteIdentical(PerturbRich, abilities: null, k: SaveAtTick, n: ResumeTicks,
                assertRestored: h =>
                {
                    Assert.Equal(1, LiveHeroes(h));
                    // DW-26: the non-folded per-hero XP-gain multiplier round-trips exactly (the hero keeps its ×2.0).
                    Assert.Equal(Fixed.FromFloat(2f).Raw, h.Heroes.XpGainFactorOf[0].Raw);
                    Assert.Equal(2, h.Research.CompletedLevels[(int)Faction.Player1][0]);
                    Assert.Equal(42, h.Vars.GetInt("saved_flag", 0));
                    Assert.True(h.Projectiles.HighWaterMark >= 1);
                });
        }

        private static int LiveHeroes(SimulationHost h)
        {
            int n = 0;
            for (int i = 0; i < h.Heroes.Count; i++) if (h.Heroes.Alive[i]) n++;
            return n;
        }

        // ── #5c — a run_once trigger that has FIRED (director _triggerFired/_firstTick) + DSL, byte-identical ────
        // If the director's fired-guard / first-tick state did NOT round-trip, the resumed match would re-run the
        // match_start pass and re-fire the one-shot add_resources → Player1 ore diverges at K+1. Byte-identical over
        // 300 ticks is the proof the director runtime (and, via the same capture path, _triggerCooldown) round-trips.

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithFiredRunOnceTriggerAndDsl()
        {
            ScenarioData model = BuildTriggerModel();
            AssertResumeByteIdentical(
                perturbAtK: h => { h.Vars.SetInt("flag", 0, 7); h.Vars.TimerSet("t", 50); },
                abilities: null, k: SaveAtTick, n: ResumeTicks, model: model);
        }

        private static ScenarioData BuildTriggerModel()
        {
            ScenarioData m = GoldenApplierScenario.BuildModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name    = "one-shot-bonus",
                    RunOnce = true,
                    Events  = new[] { new TriggerEvent  { Type = "match_start",   Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "add_resources", Faction = 0, Amount = Fixed.FromInt(777) } },
                },
            };
            return m;
        }

        // ── #5d — the director's CHANGE-DETECTION snapshots survive the load (review fix) ─────────────────────────
        // _prevBuildingDone / _prevFlags are mid-match-mutable but NOT serialized: LoadScenario seeds them from the
        // AUTHORED board and the save overlay lands afterward. Without ScenarioDirector.ReseedChangeDetection the
        // director's "previous" snapshot describes the authored map while the world describes the saved match, so the
        // first resumed tick emits a spurious building_completed for every mid-match-built building — re-firing any
        // non-run_once trigger and mutating FOLDED state. The trigger below is deliberately NOT run_once (a fired
        // guard would mask the re-fire) and its add_resources lands in the checksum.

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithMidMatchBuiltBuilding()
        {
            ScenarioData model = BuildBuildingCompletedTriggerModel();

            static void BuildBarracks(SimulationHost h)
            {
                int id = h.Buildings.Create(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(-20)),
                                            Faction.Player1, BuildingType.Barracks);
                Assert.True(id >= 0);
                h.Buildings.ConstructionTimer[id] = Fixed.Zero; // completed
            }

            // Reference: build, let the completion edge be consumed, then run uninterrupted.
            Harness reference = BuildApplied(null, model);
            Step(reference.Host, SaveAtTick);
            BuildBarracks(reference.Host);
            Step(reference.Host, 2); // the director now records the barracks as ALREADY done
            List<GoldenChecksumReplay.Sample> refSeq = Capture(reference.Host, ResumeTicks);

            // Save host: identical up to the save point, then round-trip through a file and resume.
            Harness saved = BuildApplied(null, model);
            Step(saved.Host, SaveAtTick);
            BuildBarracks(saved.Host);
            Step(saved.Host, 2);
            SimulationHost resumed = SaveThenLoadIntoFresh(saved, null, out _, model);

            Assert.Equal((uint)(SaveAtTick + 2), resumed.CurrentTick);
            List<GoldenChecksumReplay.Sample> resumeSeq = Capture(resumed, ResumeTicks);

            AssertSame(refSeq, resumeSeq,
                       "resume re-fired a building_completed edge for a building constructed before the save");
        }

        private static ScenarioData BuildBuildingCompletedTriggerModel()
        {
            ScenarioData m = GoldenApplierScenario.BuildModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name    = "barracks-bonus",
                    RunOnce = false, // repeatable ON PURPOSE — a run_once guard would hide the spurious re-fire
                    Events  = new[] { new TriggerEvent  { Type = "building_completed", Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "add_resources", Faction = 0, Amount = Fixed.FromInt(333) } },
                },
            };
            return m;
        }

        // ── #6 — a LONG-STABLE modifier (clean host at save time) resumes byte-identical (dirty-flag idempotence) ──

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithLongStableModifier()
        {
            Modifier mod = BuildTestModifier();
            PersistentEffect pe = BuildTestPersistent();
            AbilityRegistry registry = BuildAbilityRegistry(mod, pe);

            void Inject(SimulationHost h)
            {
                Assert.True(h.World.IsAlive(0));
                Assert.True(h.Modifiers.Apply(0, mod, casterId: 0, casterFaction: Faction.Player1));
                h.Modifiers.InstallPersistent(0, pe, casterId: 0, casterFaction: Faction.Player1);
            }

            // Inject EARLY (tick 5) and run to K, so the host is long-CLEAN by save time — the reference is NOT dirty at
            // K+1, whereas RestoreSlot marks the resumed host dirty. Byte-identical here proves RecomputeEntity is
            // idempotent w.r.t. the folded Effective* (the review's dirty-flag-asymmetry concern).
            Harness reference = BuildApplied(registry);
            Step(reference.Host, 5); Inject(reference.Host); Step(reference.Host, SaveAtTick - 5);
            List<GoldenChecksumReplay.Sample> refSeq = Capture(reference.Host, ResumeTicks);

            Harness saved = BuildApplied(registry);
            Step(saved.Host, 5); Inject(saved.Host); Step(saved.Host, SaveAtTick - 5);
            SimulationHost resumed = SaveThenLoadIntoFresh(saved, registry, out _);
            List<GoldenChecksumReplay.Sample> resumeSeq = Capture(resumed, ResumeTicks);

            AssertSame(refSeq, resumeSeq, "long-stable modifier resume diverged (dirty-flag asymmetry)");
        }

        // ── #8 — MatchStats scoreboard counters round-trip (observational; unfolded, but the 11.2 score screen reads them) ──

        [Fact]
        public void SaveLoad_PreservesMatchStatsCounters()
        {
            Harness saved = BuildApplied();
            Step(saved.Host, SaveAtTick);
            saved.Host.MatchStats.RecordKill(Faction.Player2, Faction.Player1);
            saved.Host.MatchStats.RecordUnitBuilt(Faction.Player1);
            saved.Host.MatchStats.RecordBuildingRazed(Faction.Player1);
            int kills = saved.Host.MatchStats.Kills(Faction.Player1);
            int built = saved.Host.MatchStats.UnitsBuilt(Faction.Player1);
            int razed = saved.Host.MatchStats.BuildingsRazed(Faction.Player1);
            Assert.True(kills > 0 && built > 0 && razed > 0);

            SimulationHost resumed = SaveThenLoadIntoFresh(saved, null, out _);

            Assert.Equal(kills, resumed.MatchStats.Kills(Faction.Player1));
            Assert.Equal(built, resumed.MatchStats.UnitsBuilt(Faction.Player1));
            Assert.Equal(razed, resumed.MatchStats.BuildingsRazed(Faction.Player1));
        }

        // ── #2 — fail-closed structural validation (count-over-cap, short jagged lane, out-of-range modifier host) ──

        private static (SaveGameState state, SaveGameHeaderData header) CaptureValid(AbilityRegistry? registry = null)
        {
            Harness saved = BuildApplied(registry);
            Step(saved.Host, SaveAtTick);
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            return (SaveGameState.CaptureFrom(saved.Host, table), Header(saved));
        }

        private static byte[] WriteBytes(SaveGameState state, SaveGameHeaderData header)
        {
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            return ms.ToArray();
        }

        [Fact]
        public void Load_CountOverCap_ThrowsWithMessage()
        {
            (SaveGameState state, SaveGameHeaderData header) = CaptureValid();
            state.HeroCount = HeroStore.MAX_HEROES + 1; // scalar count exceeds the store cap
            byte[] blob = WriteBytes(state, header);
            using var ms = new MemoryStream(blob);
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("exceeds cap", ex.Message);
        }

        [Fact]
        public void Load_ShortJaggedLane_ThrowsWithMessage()
        {
            (SaveGameState state, SaveGameHeaderData header) = CaptureValid();
            state.BCount = 1; // ≤ cap but the building lanes were captured at the real (>1) count → cardinality mismatch
            byte[] blob = WriteBytes(state, header);
            using var ms = new MemoryStream(blob);
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("lane", ex.Message);
        }

        [Fact]
        public void Load_OutOfRangeModifierHost_ThrowsWithMessage()
        {
            Modifier mod = BuildTestModifier();
            PersistentEffect pe = BuildTestPersistent();
            AbilityRegistry registry = BuildAbilityRegistry(mod, pe);
            Harness saved = BuildApplied(registry);
            Step(saved.Host, SaveAtTick);
            Assert.True(saved.Host.Modifiers.Apply(0, mod, 0, Faction.Player1));
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);
            Assert.NotEmpty(state.Modifiers);

            SaveGameState.ModifierEntry m = state.Modifiers[0];
            m.HostId = EntityWorld.MAX_ENTITIES + 5;
            state.Modifiers[0] = m;

            byte[] blob = WriteBytes(state, Header(saved));
            using var ms = new MemoryStream(blob);
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("out of range", ex.Message);
        }

        // ── #3 — a flipped body byte is rejected by the integrity checksum ──────────────────────────────────────

        [Fact]
        public void Load_FlippedBodyByte_ThrowsWithMessage()
        {
            byte[] blob = ValidBlob(out _);
            blob[blob.Length - 1] ^= 0xFF; // flip a byte inside the framed body (the zero-length terminator region)
            using var ms = new MemoryStream(blob);
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("integrity", ex.Message);
        }

        // ── #4 — a save missing a required section is rejected (not silently defaulted) ─────────────────────────

        [Fact]
        public void Load_MissingRequiredSection_ThrowsWithMessage()
        {
            // A body with ONLY the Scalars section (tag 1) + the zero-length terminator.
            byte[] payload;
            using (var pm = new MemoryStream())
            {
                using (var pw = new BinaryWriter(pm, System.Text.Encoding.UTF8, leaveOpen: true))
                { pw.Write((byte)1); pw.Write((uint)5); pw.Write((ulong)9); } // tag=Scalars, tick, rng
                payload = pm.ToArray();
            }
            byte[] body;
            using (var bm = new MemoryStream())
            {
                using (var bw = new BinaryWriter(bm, System.Text.Encoding.UTF8, leaveOpen: true))
                { bw.Write(payload.Length); bw.Write(payload); bw.Write(0); }
                body = bm.ToArray();
            }
            ulong bodyHash = SaveGameFile.Fnv64(body);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(SaveGameFile.MAGIC);
                w.Write(SaveGameFile.FormatVersion);
                w.Write(SimChecksum.AlgoVersion);
                w.Write(CanonicalModelHash.AlgoVersion);
                w.Write(StartStateHash.AlgoVersion);
                w.Write(0UL); w.Write(0UL); w.Write(bodyHash); // model, content, body hashes
                w.Write(0u);  // tick
                w.Write("");  // map id
                w.Write(0);   // 0 slots
                w.Write(body);
            }
            ms.Position = 0;
            var ex = Assert.Throws<InvalidDataException>(() => SaveGameFile.Read(ms));
            Assert.Contains("missing required section", ex.Message);
        }
    }
}
