#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 7.13 — THE re-baseline correctness GATE (authored BEFORE the goldens were re-recorded). Per the spec's
    /// AUTHORITATIVE resolution protocol, the proof the SimChecksum 20→21 re-baseline is correct is THIS differential
    /// guard, NOT a green suite: a green suite over freshly-recorded goldens cannot catch a latent fold/reset defect
    /// baked INTO those goldens. If either assertion here fails, the re-baseline is CORRUPT and the pass must HALT
    /// rather than trust the re-recorded goldens.
    ///
    /// Two independent assertions:
    ///   1. NO-PERTURBATION — a representative scenario carrying NONE of the new kinds AND ZERO DSL triggers (the
    ///      Story 1.2 <see cref="GoldenScenario"/>, which loads an empty ScenarioData) has
    ///      <c>TriggerEnabledStore.Count == 0</c>, so the v21 enabled fold contributes ZERO Mix calls. Its per-tick
    ///      SimChecksum sequence under v21 MUST therefore be BYTE-IDENTICAL to its frozen pre-story (v20) sequence
    ///      (an embedded control the re-record never touches). ANY divergence means the new param threading or the
    ///      ClearForReset/reset wiring perturbed an UNRELATED store → HALT.
    ///   2. FOLD-POSITION — a focused unit test hand-replicating the SimChecksum fold TAIL over a minimal state
    ///      (no entities/buildings, a 0-active-faction registry so the resource/win-state/alliance per-faction loops
    ///      are empty) proves the enabled fold sits AFTER the alliance block and BEFORE the RNG fold, contributes
    ///      exactly N Mix(bit) operations, treats null as a true no-op (skip), and treats a Count==0 store identically.
    /// </summary>
    public class ReBaselineDifferentialGuardTests
    {
        /// <summary>The FROZEN pre-story (v20) control — a verbatim copy of golden-scenario.golden.txt captured
        /// BEFORE the re-record; embedded and NEVER re-recorded (see the .csproj comment).</summary>
        private const string FrozenPreStoryV20 = "rebaseline-guard-story12-frozen-v20.golden.txt";

        [Fact]
        public void NoTriggerScenario_V21SequenceIsByteIdenticalToFrozenPreStoryV20()
        {
            // Skip during a re-baseline run (goldens being rewritten); the control file is frozen either way.
            if (GoldenChecksumReplay.IsRecordMode) return;

            var frozen = GoldenChecksumReplay.LoadGolden(FrozenPreStoryV20);
            var actual = GoldenChecksumReplay.RunAndRecord(GoldenScenario.DefaultTicks);

            GoldenChecksumReplay.Divergence? div = GoldenChecksumReplay.CompareSequences(frozen, actual);
            Assert.True(div is null,
                "RE-BASELINE CORRUPT (HALT): the no-trigger GoldenScenario diverged from its frozen pre-story (v20) " +
                "sequence — the SimChecksum 20→21 param threading / ClearForReset perturbed an unrelated store. " +
                (div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value)));
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
    }
}
