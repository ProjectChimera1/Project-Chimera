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
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            { w.Write(SaveGameFile.MAGIC); w.Write((ushort)0); }
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
            Assert.Equal(21, SimChecksum.AlgoVersion);
            Assert.Equal(14, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
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

        // ── #5b — rich non-default state: hero + research + in-flight projectile + DSL var/timer, byte-identical ──

        [Fact]
        public void SaveLoad_ResumeByteIdentical_WithHeroResearchProjectileAndDsl()
        {
            static void PerturbRich(SimulationHost h)
            {
                // A persistent hero row (folded Level/Xp/growth).
                int slot = h.Heroes.Mint(new HeroId(777), entityId: 0, level: 3, xp: Fixed.FromInt(50),
                                         maxLevel: 10, baseXp: Fixed.FromInt(100), xpGrowth: Fixed.FromInt(1),
                                         xpShareRadius: Fixed.FromInt(8));
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
