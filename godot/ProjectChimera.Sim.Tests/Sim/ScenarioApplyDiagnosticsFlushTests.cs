#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;             // EffectCaps.MaxModifiersPerEntity
using ProjectChimera.Sim.Tests.Golden;    // GoldenApplierScenario (the trigger-less applied fixture)
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-707 — DW-624's diagnostic flush was wired to match TEARDOWN, not to end-of-scenario-LOAD, so a bulk load's
    /// research catch-up refusals surfaced one match late.
    ///
    /// <para><b>The defect.</b> <c>ResearchSystem.ApplyCompletedResearch</c> hangs off
    /// <c>EntityWorld.OnUnitDefinitionApplied</c>, so it fires once PER SPAWN — which is exactly why DW-624 made it
    /// accumulate a per-match tally instead of warning inline (a 200-unit scenario load would emit 200 lines).
    /// <c>SimulationHost.FlushMatchDiagnostics()</c> was made public precisely so a LOAD path could report that tally,
    /// and DW-624's own ledger text names an "end-of-load flush" — but the only wired call site was
    /// <c>ClearForReset</c>, which runs BEFORE a re-apply. The refusals a load produced were therefore reported at the
    /// NEXT teardown, one match late and attributed to the wrong scenario.</para>
    ///
    /// <para><b>The fixture reproduces the real thing</b>, not a stand-in: a faction with
    /// <c>MaxModifiersPerEntity + 1</c> completed researches, so every unit the applier spawns fills its modifier ring
    /// on the catch-up walk and has the LAST research's banked, already-paid-for bonus dropped. The refusals are
    /// produced BY the load, inside <c>ScenarioApplier.Apply</c>, exactly as a bulk load produces them in the game.
    /// Both assertions below are RED without the flush call at the end of <c>Apply</c>.</para>
    ///
    /// <para>Godot-free and Tier-1. The diagnostic reads and zeroes UNFOLDED counters and routes text through the
    /// injected <c>ILogSink</c>; it mutates no sim array and pushes no event, so an apply that calls it is
    /// byte-identical to one that does not and no golden moves.</para>
    /// </summary>
    public class ScenarioApplyDiagnosticsFlushTests
    {
        /// <summary>Capturing <c>ILogSink</c> (the DW-624 / DW-83 idiom — NullLogSink, but recording).</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        /// <summary>One more research than a unit's modifier ring can hold, so the LAST one is always refused on a
        /// spawn that has banked them all — the DW-624 condition, reached through the ordinary catch-up walk.</summary>
        private const int ResearchCount = EffectCaps.MaxModifiersPerEntity + 1;

        private static string ResearchId(int i) => $"armor_up_{i}";

        /// <summary>The golden applier faction (whose <c>worker</c> unit the scenario places) plus a research lab and
        /// <see cref="ResearchCount"/> one-level researches. Adding to the golden faction rather than inventing one
        /// keeps the applied scenario itself the already-validated <c>GoldenApplierScenario.BuildModel()</c>.</summary>
        private static FactionDefinition ResearchHeavyFaction()
        {
            FactionDefinition f = GoldenApplierScenario.BuildFaction();

            var ids = new string[ResearchCount];
            for (int i = 0; i < ResearchCount; i++)
            {
                ids[i] = ResearchId(i);
                f.Research.Add(new ResearchDefinition
                {
                    Id = ids[i],
                    Prerequisites = System.Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel
                        {
                            Cost = new Dictionary<string, int> { { "ore", 1 } },
                            TimeTicks = 1,
                            ModifierDelta = new ResearchModifierDelta { ArmorDelta = 1f },
                        },
                    },
                });
            }

            f.Buildings.Add(new BuildingDefinition { Id = "lab", Category = "Structure", Hp = 100f, AvailableResearch = ids });
            return f;
        }

        /// <summary>Bank every research for Player1 on an EMPTY world — the DW-623 empty-army exemption, so all
        /// <see cref="ResearchCount"/> levels are genuinely banked and paid for and every FUTURE spawn is meant to
        /// receive them through the catch-up path.</summary>
        private static void BankEveryResearch(SimulationHost host)
        {
            int lab = host.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            host.Buildings.ConstructionTimer[lab] = Fixed.Zero;               // operational
            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));

            for (int ri = 0; ri < ResearchCount; ri++)
            {
                Assert.True(host.ResearchSys.StartResearchCommand(lab, Faction.Player1, ri),
                            $"fixture assumption: research #{ri} could be started");
                host.StepOnce();                                             // TimeTicks 1 → completes this tick
                Assert.Equal(1, host.Research.CompletedLevels[(int)Faction.Player1][ri]);
            }
            Assert.Equal(0, host.World.AliveCount);                          // still an empty army — nothing spawned yet
        }

        private static List<string> ResearchLines(RecordingSink sink)
        {
            var lines = new List<string>();
            foreach (string w in sink.Warns)
                if (w.Contains("[ResearchSystem]")) lines.Add(w);
            return lines;
        }

        [Fact]
        public void ScenarioApply_ReportsTheCatchUpRefusalsITSOwnSpawnsProduced_AtLoadEnd()
        {
            var sink = new RecordingSink();
            FactionDefinition faction = ResearchHeavyFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(sink, new FactionRegistry(2), faction, faction);
            BankEveryResearch(host);
            sink.Warns.Clear();

            var applier = new ScenarioApplier(host, sink, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);

            // The load spawned units into a ring that could not hold every banked research, so the catch-up walk
            // dropped the last one on each of Player1's spawns — and the load REPORTS it, aggregated to ONE line.
            // PRE-FIX: zero lines here, and the tally sat until the next ClearForReset.
            string line = Assert.Single(ResearchLines(sink));
            Assert.Contains(ResearchId(ResearchCount - 1), line);            // the research that was dropped, by name
            Assert.Contains("for Player1", line);
            Assert.Contains("DW-624", line);

            // Non-vacuity: the refusal really happened (the applied units carry only what the ring could hold).
            Assert.True(host.World.AliveCount > 0, "the applier spawned no units, so no catch-up could be refused");
        }

        [Fact]
        public void AfterAnApply_TheTallyIsConsumed_SoTheNextTeardownCannotReportItOneMatchLate()
        {
            // The other half of the defect: the refusals used to be carried past the load and attributed to whatever
            // match happened to end next. Once the load flushes them, the teardown has nothing left to mis-attribute.
            var sink = new RecordingSink();
            FactionDefinition faction = ResearchHeavyFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(sink, new FactionRegistry(2), faction, faction);
            BankEveryResearch(host);

            var applier = new ScenarioApplier(host, sink, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // Idempotent and per-match: the apply already consumed the tally, so an explicit flush right after it
            // reports nothing (PRE-FIX this returned the whole load's refusal count).
            Assert.Equal(0, host.FlushMatchDiagnostics());

            int linesAfterLoad = ResearchLines(sink).Count;
            host.ClearForReset();
            Assert.Equal(linesAfterLoad, ResearchLines(sink).Count);         // the teardown adds nothing of the load's
        }
    }
}
