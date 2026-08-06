#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;           // DamageEffect
using ProjectChimera.Sim.Tests.Golden;  // GoldenApplierScenario / GoldenChecksumReplay
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-548, post-merge review fix — the deferred trigger-phase death rail must SURVIVE a save.
    ///
    /// <para>DW-548 moved the director's own trigger-phase kills (a <c>run_effect</c> that damages a unit to death
    /// during the trigger phase, after that tick's <c>CollectEvents</c> already ran) off <c>world.DeathLog</c> and
    /// onto a director-owned rail, so the NEXT tick's collect emits them. That kept the log's "empty at the tick
    /// boundary" invariant true — but it created a buffer that is deliberately NON-EMPTY across the boundary while
    /// being neither folded into <c>SimChecksum</c> nor serialized, and <c>ReseedChangeDetection</c> zeroed it on
    /// restore. So an in-match save taken at exactly that boundary — the boundary
    /// <c>SaveGameState.AssertDeathLogDrained</c> (DW-551) explicitly PERMITS, because the log is empty there while
    /// the rail is not — silently dropped a whole <c>unit_dies</c> occurrence on resume.</para>
    ///
    /// <para>That is not a cosmetic loss: <c>unit_dies</c> subscribers mutate FOLDED sim state (DSL variables are
    /// folded at v16, and a subscriber may also deal damage or spawn), so the restored run diverged from the
    /// uninterrupted one and the "a restored save reproduces a from-boot run byte-for-byte" keystone broke — with
    /// nothing detecting it, since the rail is never hashed and the one guard aimed at this class covers the log
    /// only. Both halves are pinned here: the occurrence still fires after a resume, and the resumed checksum
    /// stream matches the uninterrupted one tick for tick.</para>
    ///
    /// Godot-free, Tier-1. Nothing new is folded, so no golden moves.
    /// </summary>
    public class DeferredDeathRailPersistenceTests
    {
        private const int ResumeTicks = 60;

        // ── Fixture ─────────────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public SimulationHost Host = null!;
            public ScenarioData Model = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        /// <summary>The applier golden's model plus two triggers: a <c>match_start</c> <c>run_effect</c> that kills
        /// the anchor entity DURING the trigger phase (the only way to arm the deferred rail), and a
        /// <c>unit_dies</c> subscriber that counts occurrences into a FOLDED DSL variable.</summary>
        private static ScenarioData ModelWithTriggerPhaseKiller()
        {
            var deaths = new ScenarioVariable
            {
                Name = "deaths", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero,
            };
            var declMap = new Dictionary<string, (DslValueType Type, VarScope Scope)>(System.StringComparer.Ordinal)
            {
                ["deaths"] = (DslValueType.Int, VarScope.Global),
            };

            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger(
                "trigger_phase_killer", "match_start", new DamageEffect(Fixed.FromInt(1000), DamageType.Normal));
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "death_counter", "unit_dies", null, "event.victim >= 0",
                null, null, -1, false, "deaths", 0, "deaths + 1", declMap, null));

            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Variables = new[] { deaths };
            model.TriggerGraphJson = g.ToCanonicalJson();
            return model;
        }

        private static Harness BuildApplied(ScenarioData model)
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1; // every tick → an exactly located divergence
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Harness { Host = host, Model = model, SlotDefs = slotDefs };
        }

        private static SaveGameHeaderData Header(Harness h) => new()
        {
            CanonicalModelHash = CanonicalModelHash.Compute(h.Model),
            ContentHash        = ContentHash.Compute(new[] { h.SlotDefs[(int)Faction.Player1]! },
                                                     h.Host.AbilityRegistry, h.Host.ItemRegistry, null),
            Tick               = h.Host.CurrentTick,
            MapId              = h.Model.Id,
            Slots              = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
        };

        /// <summary>Full round-trip: capture → Write → Read → RestoreInto a fresh applied host.</summary>
        private static SimulationHost SaveThenLoadIntoFresh(Harness saved)
        {
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, Header(saved));

            Harness load = BuildApplied(saved.Model);
            using var read = new MemoryStream(ms.ToArray());
            (SaveGameHeaderData _, SaveGameState st) = SaveGameFile.Read(read);
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            st.RestoreInto(load.Host, loadTable, load.SlotDefs);
            return load.Host;
        }

        private static List<GoldenChecksumReplay.Sample> Stream(SimulationHost host, int n)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(n);
            host.SetChecksumSink((t, h) => seq.Add(new GoldenChecksumReplay.Sample(t, h)));
            for (int i = 0; i < n; i++) host.StepOnce();
            return seq;
        }

        // ── The premise: the rail really is non-empty at a boundary the save guard passes ───────────────────────

        /// <summary>
        /// The state the defect needs, stated on its own so a future change that makes it unreachable fails HERE with
        /// a clear message instead of quietly turning the two tests below vacuous: after the tick whose trigger phase
        /// killed something, the DeathLog is drained (so <c>CaptureFrom</c>'s DW-551 assert reports "nothing pending"
        /// and the save proceeds) while the deferred rail holds the record.
        /// </summary>
        [Fact]
        public void AfterATriggerPhaseKill_TheLogIsDrained_ButTheDeferredRailIsNot()
        {
            Harness h = BuildApplied(ModelWithTriggerPhaseKiller());
            h.Host.StepOnce();   // tick 1: match_start → run_effect kills the anchor during the trigger phase

            Assert.Equal(0, h.Host.World.DeathLog.Count);                 // the guard's premise holds…
            Assert.Equal(1, h.Host.ScenarioDirector.CarriedDeathCount);   // …and says nothing about the rail
            Assert.Equal(0, h.Host.Vars.GetInt("deaths", 0));             // not emitted yet — that is next tick
        }

        // ── The defect ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A save taken at exactly that boundary must not lose the pending occurrence. RED before the fix: the rail
        /// was not captured and <c>ReseedChangeDetection</c> zeroed it, so the resumed run emitted ZERO
        /// <c>unit_dies</c> for a kill the uninterrupted run emits.
        /// </summary>
        [Fact]
        public void SaveAtTheBoundary_CarriesThePendingUnitDies_AcrossTheRestore()
        {
            ScenarioData model = ModelWithTriggerPhaseKiller();

            // Uninterrupted reference: the occurrence lands on the tick after the kill.
            Harness reference = BuildApplied(model);
            reference.Host.StepOnce();
            Assert.Equal(0, reference.Host.Vars.GetInt("deaths", 0));
            reference.Host.StepOnce();
            Assert.Equal(1, reference.Host.Vars.GetInt("deaths", 0));

            // The same run, saved at the boundary between those two ticks.
            Harness saved = BuildApplied(model);
            saved.Host.StepOnce();
            SimulationHost resumed = SaveThenLoadIntoFresh(saved);

            Assert.Equal(1, resumed.ScenarioDirector.CarriedDeathCount); // the rail rode the save file
            resumed.StepOnce();
            Assert.Equal(1, resumed.Vars.GetInt("deaths", 0));           // RED pre-fix: 0
        }

        /// <summary>
        /// The consequence that makes it a determinism defect rather than a missing notification: the dropped
        /// occurrence mutates a FOLDED variable, so the resumed checksum stream diverged from the uninterrupted one
        /// from the very next tick. Byte-identical resume across the boundary is the property under test.
        /// </summary>
        [Fact]
        public void ResumeAcrossThatBoundary_IsChecksumIdenticalToTheUninterruptedRun()
        {
            ScenarioData model = ModelWithTriggerPhaseKiller();

            Harness reference = BuildApplied(model);
            reference.Host.StepOnce();
            List<GoldenChecksumReplay.Sample> refSeq = Stream(reference.Host, ResumeTicks);

            Harness saved = BuildApplied(model);
            saved.Host.StepOnce();
            SimulationHost resumed = SaveThenLoadIntoFresh(saved);
            List<GoldenChecksumReplay.Sample> resumeSeq = Stream(resumed, ResumeTicks);

            GoldenChecksumReplay.Divergence? d = GoldenChecksumReplay.CompareSequences(refSeq, resumeSeq);
            Assert.True(d is null, d is null
                ? ""
                : $"resume after a trigger-phase kill diverged: {GoldenChecksumReplay.DescribeDivergence(d.Value)}");
        }

        // ── The ordinary case stays ordinary ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The overwhelmingly common save — no trigger-phase kill pending — must round-trip an EMPTY rail and must
        /// not manufacture a spurious post-restore <c>unit_dies</c> (the reseed posture the original DW-548 comment
        /// was protecting). Also covers the lane-cardinality validation path with the empty lanes.
        /// </summary>
        [Fact]
        public void SaveWithNoPendingTriggerPhaseKill_RestoresAnEmptyRail_AndEmitsNothingExtra()
        {
            ScenarioData model = ModelWithTriggerPhaseKiller();

            Harness saved = BuildApplied(model);
            for (int i = 0; i < 5; i++) saved.Host.StepOnce();  // the kill's occurrence is long since emitted
            Assert.Equal(0, saved.Host.ScenarioDirector.CarriedDeathCount);
            Assert.Equal(1, saved.Host.Vars.GetInt("deaths", 0));

            SimulationHost resumed = SaveThenLoadIntoFresh(saved);
            Assert.Equal(0, resumed.ScenarioDirector.CarriedDeathCount);

            resumed.StepOnce();
            Assert.Equal(1, resumed.Vars.GetInt("deaths", 0)); // no ghost re-emission, no second count
        }
    }
}
