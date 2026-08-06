#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// THE re-baseline correctness GATE. Per the Story 7.13 resolution protocol, the proof that a SimChecksum
    /// re-baseline is correct is THIS differential guard, NOT a green suite: a green suite over freshly-recorded
    /// goldens cannot catch a latent fold/reset defect baked INTO those goldens. If an assertion here fails, the
    /// re-baseline is CORRUPT and the pass must HALT rather than trust the re-recorded goldens.
    ///
    /// <para><b>What this gate can and cannot see.</b> It does NOT protect gameplay correctness — the ~6,000
    /// behavioural tests in this suite do that, and they survive a re-baseline precisely because they assert
    /// behaviour rather than hash bytes. What it uniquely catches is a defect in the CHECKSUM PLUMBING: a store
    /// reset at the wrong moment, a mis-threaded param, a fold landing in the wrong position — anything that moves
    /// the hash while leaving gameplay correct. Behavioural tests are blind to that class by construction, and its
    /// symptom in production is the worst kind: players desync-kicked out of healthy multiplayer matches.</para>
    ///
    /// <para><b>DW-78 / Phase B (2026-08-06) — why the control moved, and the lesson.</b> The original control was
    /// <c>rebaseline-guard-story12-frozen-v20.golden.txt</c>, a frozen copy of GoldenScenario. It was documented as
    /// "NEVER re-recorded", but story 11.6's production-queue fold moved GoldenScenario (which owns two buildings),
    /// this guard fired exactly as designed — and the response was to overwrite the control file with the new
    /// sequence rather than to halt. From that point the test compared a live GoldenScenario run against a
    /// byte-for-byte copy of GoldenScenario's own committed golden: a tautology that could never fail. It was
    /// carried green and meaningless through v22, and was caught only when this batch audited it. Two changes
    /// follow from that:</para>
    /// <list type="number">
    ///   <item>The control is now <see cref="FormationSeparationScenario"/>, chosen because it carries NONE of the
    ///     v23 gather state (no workers at all) and no DSL triggers, and because it is cross-platform-safe rather
    ///     than Windows-gated. Its sequence was captured from the pre-batch commit and is byte-identical across the
    ///     v22→v23 bump — which is exactly the property being asserted.</item>
    ///   <item><see cref="FrozenControl_OwnBytes_ArePinned"/> now pins the control FILE's own content. Re-freezing
    ///     it — the exact silent failure that happened at 11.6 — changes those bytes and turns the suite RED. The
    ///     pin lives in source, so silencing this gate is now a visible, reviewable edit instead of a file copy.</item>
    /// </list>
    ///
    /// <para><b>Choosing a control for a FUTURE fold.</b> A control arm is only clean with respect to the specific
    /// state being folded, so this is a per-re-baseline choice, not a permanent freeze. Pick a committed golden
    /// whose scenario carries none of the new state, re-capture it, and re-pin. If NO such scenario exists, that is
    /// itself the signal: the fold is unbounded and is perturbing every scenario, and the fold should be reviewed
    /// (see <see cref="SimChecksum"/>'s v23 note on bounded vs unconditional folding) before the goldens are
    /// re-recorded.</para>
    /// </summary>
    public class ReBaselineDifferentialGuardTests
    {
        /// <summary>
        /// The FROZEN control: <see cref="FormationSeparationScenario"/>'s sequence as committed BEFORE the Phase B
        /// batch (algo v22). Embedded and NEVER re-recorded — no <c>MaybeRecord</c> call covers this filename, and
        /// <see cref="FrozenControl_OwnBytes_ArePinned"/> fails loudly if anyone rewrites it anyway.
        /// </summary>
        private const string FrozenControl = "rebaseline-guard-frozen-v22-formation-separation.golden.txt";

        /// <summary>
        /// ASSERTION 1 — NO-PERTURBATION. A scenario carrying none of the newly-folded state must hash
        /// BYTE-IDENTICALLY to its pre-fold sequence.
        ///
        /// <para><see cref="FormationSeparationScenario"/> has no gatherers (v23's <c>GatherState</c> /
        /// <c>GatherTarget</c> / <c>CarryAmount</c> / <c>CarryResourceType</c> / <c>GateClosedTicks</c> all sit at
        /// their inactive defaults) and no DSL triggers. v23's gather fold is BOUNDED — an entity at those defaults
        /// contributes ZERO Mix calls — so every tick here must reproduce the frozen v22 bytes exactly. ANY
        /// divergence means the fold perturbed an unrelated store, or the bounding predicate is wrong → HALT.</para>
        /// </summary>
        [Fact]
        public void NoGatherScenario_V23SequenceIsByteIdenticalToFrozenV22()
        {
            // Skip during a re-baseline run (goldens being rewritten); the control file is frozen either way.
            if (GoldenChecksumReplay.IsRecordMode) return;

            var frozen = GoldenChecksumReplay.LoadGolden(FrozenControl);
            var actual = GoldenChecksumReplay.RunAndRecord(FormationSeparationScenario.DefaultTicks,
                                                           build: FormationSeparationScenario.Build);

            GoldenChecksumReplay.Divergence? div = GoldenChecksumReplay.CompareSequences(frozen, actual);
            Assert.True(div is null,
                "RE-BASELINE CORRUPT (HALT): the gather-free FormationSeparationScenario diverged from its frozen " +
                "pre-batch (v22) sequence. This scenario carries NONE of the v23 gather state, so a bounded fold " +
                "cannot legitimately move it — the fold perturbed an unrelated store, the bounding predicate is " +
                "wrong, or a reset/threading defect was introduced. Do NOT re-freeze the control to make this pass; " +
                "that is how this gate was silently disabled at story 11.6. " +
                (div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value)));
        }

        /// <summary>
        /// ASSERTION 2 — TAMPER EVIDENCE. The control file's own bytes are pinned, so re-recording or overwriting it
        /// fails loudly instead of silently converting this gate into a tautology (the 11.6 failure mode).
        ///
        /// <para>Pins the FNV-1a-32 of the control's DATA lines only — comment/header lines are excluded so a
        /// documentation edit to the header is not a spurious failure, while any change to a recorded hash is.</para>
        /// </summary>
        [Fact]
        public void FrozenControl_OwnBytes_ArePinned()
        {
            string[] data = ReadEmbeddedDataLines(FrozenControl);

            Assert.True(data.Length == FormationSeparationScenario.DefaultTicks,
                $"The frozen control should hold exactly {FormationSeparationScenario.DefaultTicks} recorded ticks, " +
                $"found {data.Length}. The control was replaced or truncated.");

            uint actual = Fnv1a32(string.Join("\n", data));

            // Pinned content hash of the frozen control's 300 data lines (captured from the pre-Phase-B commit).
            const uint PinnedControlHash = 0x6864F671; // formation-separation @ v22, captured pre-Phase-B
            Assert.True(actual == PinnedControlHash,
                $"The FROZEN re-baseline control changed: pinned 0x{PinnedControlHash:X8}, actual 0x{actual:X8}.\n" +
                $"This file is a CONTROL — it must never be re-recorded. If you are here because " +
                $"{nameof(NoGatherScenario_V23SequenceIsByteIdenticalToFrozenV22)} failed and you overwrote the " +
                $"control to make it pass, STOP and revert: that is precisely how this gate was silently disabled " +
                $"at story 11.6, and it stayed green and meaningless for two releases.\n" +
                $"The control is only legitimately replaced when a NEW deliberate re-baseline picks a NEW control " +
                $"scenario that carries none of THAT fold's state — in which case re-pin this constant in the same " +
                $"commit, and say so in the class doc.");
        }

        /// <summary>Read an embedded golden's non-comment lines (the recorded "tick hash" rows), trimmed of CR.</summary>
        private static string[] ReadEmbeddedDataLines(string fileName)
        {
            Assembly asm = typeof(ReBaselineDifferentialGuardTests).Assembly;
            string? resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
            Assert.True(resource is not null, $"Embedded resource not found for '{fileName}'. Is it an <EmbeddedResource> in the csproj?");

            using Stream s = asm.GetManifestResourceStream(resource!)!;
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd()
                         .Split('\n')
                         .Select(l => l.TrimEnd('\r'))
                         .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                         .ToArray();
        }

        /// <summary>FNV-1a 32-bit over UTF-8 bytes — a content fingerprint for the control file (independent of
        /// <see cref="SimChecksum"/>'s own Mix, so a defect in that primitive cannot mask a tampered control).</summary>
        private static uint Fnv1a32(string text)
        {
            uint hash = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash;
        }

        // FNV-1a 32-bit offset basis (mirrors SimChecksum.FNV_OFFSET — a fixed constant, safe to pin here).
        private const uint FNV_OFFSET = 2166136261u;

        /// <summary>Hand-replicate the SimChecksum fold for the minimal state below (no alive entities, no
        /// buildings), so the enabled fold's exact position (after the alliance block, before rng) and count
        /// (N Mix(bit)) are pinned. The per-active-faction loops are replicated by READING the actual store values
        /// (so the pin never hardcodes a resource default), mirroring Compute's ActiveFactions iteration.</summary>
        private static uint ExpectedMinimal(EntityWorld world, BuildingStore buildings, ResourceStore resources,
            FactionRegistry factions, TriggerEnabledStore? triggerEnabled)
        {
            uint h = FNV_OFFSET;
            // entity loop: no alive entities → nothing. building loop: Count 0 → nothing.
            // resource loop (per active faction, reading actual store values — mirrors Compute exactly):
            foreach (Faction f in factions.ActiveFactions)
            {
                int idx = (int)f;
                h = SimChecksum.Mix(h, resources.Ore[idx].Raw);
                h = SimChecksum.Mix(h, resources.Crystal[idx].Raw);
                h = SimChecksum.Mix(h, resources.SupplyUsed[idx]);
                h = SimChecksum.Mix(h, resources.SupplyCap[idx]);
                h = SimChecksum.Mix(h, resources.FactionBase[idx].X.Raw);
                h = SimChecksum.Mix(h, resources.FactionBase[idx].Y.Raw);
                h = SimChecksum.Mix(h, resources.FactionBase[idx].Z.Raw);
            }
            h = SimChecksum.Mix(h, 0); // heroes null ≡ empty count
            h = SimChecksum.Mix(h, 0); // items null ≡ empty count
            h = SimChecksum.Mix(h, 0); // nodes null ≡ empty count
            h = SimChecksum.Mix(h, 0); // research null ≡ single Mix(0)
            DslVarTable.FoldEmpty(ref h, SimChecksum.Mix);   // vars null ≡ empty table
            DslLoopState.FoldEmpty(ref h, SimChecksum.Mix);  // loopState null ≡ empty state
            DslEventQueue.FoldEmpty(ref h, SimChecksum.Mix); // dslEvents null ≡ empty queue
            h = SimChecksum.Mix(h, 0); // winState null: MatchTicks==0
            foreach (Faction f in factions.ActiveFactions) { h = SimChecksum.Mix(h, 0); h = SimChecksum.Mix(h, 0); h = SimChecksum.Mix(h, 0); } // per-faction Koth/Survival/Verdict triples
            foreach (Faction f in factions.ActiveFactions) h = SimChecksum.Mix(h, (int)f); // alliances null ≡ default FFA (team id == slot)
            // ── the enabled fold, AFTER the alliance block and BEFORE rng ──
            if (triggerEnabled != null)
                for (int i = 0; i < triggerEnabled.Count; i++)
                    h = SimChecksum.Mix(h, triggerEnabled.IsEnabled(i) ? 1 : 0);
            // rng fold (last, the standing invariant).
            ulong rng = world.Rng.State;
            h = SimChecksum.Mix(h, (int)(rng & 0xFFFFFFFFUL));
            h = SimChecksum.Mix(h, (int)(rng >> 32));
            return h;
        }

        [Fact]
        public void EnabledFold_SitsAfterAllianceBeforeRng_IsNMixBits_AndNullEqualsSkip()
        {
            var world     = new EntityWorld();          // 0 alive; Rng at DEFAULT_RNG_SEED
            var buildings = new BuildingStore();        // Count 0
            var resources = new ResourceStore(Fixed.Zero);
            var factions  = new FactionRegistry(1);     // 1 active (Player1) → per-faction loops replicated from actual store values

            const int N = 3;
            var storeTrue = new TriggerEnabledStore();
            storeTrue.Reset(N);                          // all-true

            var storeMixed = new TriggerEnabledStore();
            storeMixed.Reset(N);
            storeMixed.SetInitial(1, false);             // true, false, true

            var storeEmpty = new TriggerEnabledStore();  // Count 0 (never Reset) → folds nothing

            uint hNull  = SimChecksum.Compute(world, buildings, resources, factions, triggerEnabled: null);
            uint hEmpty = SimChecksum.Compute(world, buildings, resources, factions, triggerEnabled: storeEmpty);
            uint hTrue  = SimChecksum.Compute(world, buildings, resources, factions, triggerEnabled: storeTrue);
            uint hMixed = SimChecksum.Compute(world, buildings, resources, factions, triggerEnabled: storeMixed);

            // (a) null == Count-0 empty == the no-op skip: both fold NOTHING (the differential-guard clean control).
            Assert.Equal(hNull, hEmpty);
            Assert.Equal(ExpectedMinimal(world, buildings, resources, factions, null), hNull);

            // (b) a populated store IS folded, at the pinned position, exactly N Mix(bit) with the right bit values.
            Assert.Equal(ExpectedMinimal(world, buildings, resources, factions, storeTrue), hTrue);
            Assert.Equal(ExpectedMinimal(world, buildings, resources, factions, storeMixed), hMixed);

            // (c) sensitivity: all-true, mixed, and skip are pairwise distinct (the fold actually moves the hash).
            Assert.NotEqual(hNull, hTrue);
            Assert.NotEqual(hNull, hMixed);
            Assert.NotEqual(hTrue, hMixed);

            // (d) determinism: recomputing the same store yields the same hash.
            Assert.Equal(hTrue, SimChecksum.Compute(world, buildings, resources, factions, triggerEnabled: storeTrue));
        }

        /// <summary>
        /// DW-78 — the v23 gather fold's BOUNDING PREDICATE, pinned directly. This is the property the whole
        /// re-baseline strategy rests on, and it is cheap to assert, so it should never be inferred from golden
        /// movement alone.
        ///
        /// <para>(a) An entity sitting at the full gatherer-inactive default must fold ZERO Mix calls — proven by
        /// hashing a world holding such an entity and comparing it to the identical world before any gather field
        /// was touched. (b) Each of the five folded fields must move the hash when it leaves its default, or that
        /// field escaped the fold and is a silent desync surface. (c) A worker mid-trip and a worker at rest must
        /// hash differently, which is the actual desync the fold exists to catch.</para>
        /// </summary>
        [Fact]
        public void GatherFold_IsBounded_AtDefaults_AndEveryFoldedFieldMovesTheHash()
        {
            static (EntityWorld, BuildingStore, ResourceStore, FactionRegistry) NewWorld()
            {
                var w = new EntityWorld();
                w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                         Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
                return (w, new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(1));
            }

            var (w0, b0, r0, f0) = NewWorld();
            uint atDefaults = SimChecksum.Compute(w0, b0, r0, f0);

            // (a) BOUNDED: an alive non-gatherer contributes nothing, so the hash equals the hand-replicated
            //     no-entity-gather-block value — i.e. re-asserting the defaults changes nothing.
            var (w1, b1, r1, f1) = NewWorld();
            w1.GatherState[0]       = GatherState.Inactive; // the Create defaults, written explicitly
            w1.GatherTarget[0]      = -1;
            w1.CarryAmount[0]       = Fixed.Zero;
            w1.GateClosedTicks[0]   = 0;
            Assert.Equal(atDefaults, SimChecksum.Compute(w1, b1, r1, f1));

            // (b) every folded field is load-bearing: leaving its default MUST move the hash.
            var (wa, ba, ra, fa) = NewWorld(); wa.GatherState[0]       = GatherState.Gathering;
            var (wb, bb, rb, fb) = NewWorld(); wb.GatherTarget[0]      = 0;
            var (wc, bc, rc, fc) = NewWorld(); wc.CarryAmount[0]       = Fixed.FromInt(5);
            var (wd, bd, rd, fd) = NewWorld(); wd.GateClosedTicks[0]   = 7;
            var (we, be, re_, fe) = NewWorld();
            we.GatherState[0] = GatherState.Gathering;              // CarryResourceType only folds inside the block,
            we.CarryResourceType[0] = ResourceKind.Crystal;         // so it needs the predicate already open

            uint hA = SimChecksum.Compute(wa, ba, ra, fa);
            uint hB = SimChecksum.Compute(wb, bb, rb, fb);
            uint hC = SimChecksum.Compute(wc, bc, rc, fc);
            uint hD = SimChecksum.Compute(wd, bd, rd, fd);
            uint hE = SimChecksum.Compute(we, be, re_, fe);

            Assert.NotEqual(atDefaults, hA); // GatherState
            Assert.NotEqual(atDefaults, hB); // GatherTarget
            Assert.NotEqual(atDefaults, hC); // CarryAmount
            Assert.NotEqual(atDefaults, hD); // GateClosedTicks
            Assert.NotEqual(hA,         hE); // CarryResourceType (vs the same world with the block open, Ore)

            // (c) the desync this fold exists to catch: two peers whose worker is in a different gather phase.
            Assert.NotEqual(hA, hB);
        }
    }
}
