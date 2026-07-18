#nullable enable
using System.Linq;
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.11 (AC2) — each T1 preset instantiated as canonical public-DSL graph-IR uses ONLY public registry
    /// nodes (the <c>victory</c>/<c>defeat</c> actions already exist) and round-trips through the
    /// <see cref="TriggerGraph"/> schema BYTE-IDENTICALLY (the expressibility/serialization property the spec's
    /// Design Notes call out; the actual match outcome is evaluated natively by <c>WinConditionSystem</c>).
    /// </summary>
    public class WinConditionPresetsTests
    {
        public static readonly object[][] Presets =
        {
            new object[] { new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill,       RegionId = "zone", HoldTicks = 300 } },
            new object[] { new WinConditionSpec { Preset = WinPresetKind.TimedSurvival,       FactionSlot = 1, SurviveTicks = 900 } },
            new object[] { new WinConditionSpec { Preset = WinPresetKind.Assassination,       LeaderUnitIndex = 0 } },
            new object[] { new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 } },
        };

        [Theory]
        [MemberData(nameof(Presets))]
        public void Preset_GraphIr_RoundTrips_ByteIdentical(WinConditionSpec spec)
        {
            TriggerGraph? g = WinConditionPresets.Build(spec);
            Assert.NotNull(g);

            string once  = g!.ToCanonicalJson();
            string twice = TriggerGraph.FromJson(once).ToCanonicalJson();
            Assert.Equal(once, twice); // schema round-trip is byte-identical
        }

        [Theory]
        [MemberData(nameof(Presets))]
        public void Preset_UsesOnlyPublicRegistryNodeKinds(WinConditionSpec spec)
        {
            TriggerGraph g = WinConditionPresets.Build(spec)!;

            // Every node's kind must be a member of the CLOSED public registry (no hidden engine-only opcode).
            foreach (NodeBase n in g.Nodes)
            {
                string kind = NodeKinds.KindOf(n);
                bool known = kind == NodeKinds.Trigger
                    || NodeKinds.EventTypes.Contains(kind)
                    || NodeKinds.ConditionTypes.Contains(kind)
                    || NodeKinds.ActionTypes.Contains(kind);
                Assert.True(known, $"Preset graph uses a non-public node kind '{kind}'.");
            }

            // The victory/defeat action leaf must be present — the win-outcome node the spec mandates reusing.
            Assert.Contains(g.Nodes.OfType<ActionNode>(), a => a.Kind == "victory" || a.Kind == "defeat");
        }

        [Fact]
        public void None_HasNoPresetTemplate()
        {
            Assert.Null(WinConditionPresets.Build(new WinConditionSpec { Preset = WinPresetKind.None }));
            Assert.Null(WinConditionPresets.Build(null));
        }

        // ── IA1: the params the public DSL CAN encode survive the schema round-trip byte-identically. ────────────
        // (Leader/structure INSTANCE params are the documented Story 7.13 vocabulary gap and are intentionally NOT
        //  asserted here — WinConditionPresets production code stays as-is.)

        [Fact]
        public void KotH_RegionId_SurvivesRoundTrip_OnUnitInRegionCondition()
        {
            var spec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "hilltop", HoldTicks = 300 };
            TriggerGraph back = TriggerGraph.FromJson(WinConditionPresets.Build(spec)!.ToCanonicalJson());

            ConditionNode cond = back.Nodes.OfType<ConditionNode>().Single(c => c.Kind == "unit_in_region");
            Assert.Equal("hilltop", cond.RegionId); // the region binding the public DSL CAN encode round-trips
        }

        [Fact]
        public void TimedSurvival_Faction_SurvivesRoundTrip_OnVictoryAction()
        {
            var spec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 900 };
            TriggerGraph back = TriggerGraph.FromJson(WinConditionPresets.Build(spec)!.ToCanonicalJson());

            ActionNode action = back.Nodes.OfType<ActionNode>().Single(a => a.Kind == "victory");
            Assert.Equal(1, action.Faction); // the victory faction the public DSL CAN encode round-trips
        }

        // ── Review P8: the TimedSurvival witness is a GENUINE two-trigger graph — the timer binding + duration
        //    the public DSL CAN encode (create_timer/timer_expires with a shared TimerName) survive the round-trip. ──

        [Fact]
        public void TimedSurvival_SurviveTicksAndTimerName_SurviveRoundTrip_AcrossTheTwoTriggerGraph()
        {
            var spec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 900 };
            TriggerGraph back = TriggerGraph.FromJson(WinConditionPresets.Build(spec)!.ToCanonicalJson());

            // Trigger A: match_start → create_timer("survival", 900). The public timer vocabulary is
            // seconds-typed, so the tick count rides in TimerSeconds (the documented 7.13 units gap).
            ActionNode create = back.Nodes.OfType<ActionNode>().Single(a => a.Kind == "create_timer");
            Assert.Equal("survival", create.TimerName);
            Assert.Equal(Fixed.FromInt(900), create.TimerSeconds);
            back.Nodes.OfType<EventNode>().Single(e => e.Kind == "match_start"); // exactly one start event exists

            // Trigger B: timer_expires("survival") → victory(1) — BOUND to the created timer, so it can fire.
            EventNode expires = back.Nodes.OfType<EventNode>().Single(e => e.Kind == "timer_expires");
            Assert.Equal("survival", expires.TimerName);
            Assert.Equal(1, back.Nodes.OfType<ActionNode>().Single(a => a.Kind == "victory").Faction);
        }

        // ── Review P8: each witness embeds cleanly — its canonical JSON passes the full ScenarioValidator gate
        //    (regions/slots declared as its nodes require), proving the graphs are AUTHORABLE content, not just
        //    schema-round-trippable. ──

        [Theory]
        [MemberData(nameof(Presets))]
        public void Preset_Witness_EmbeddedAsTriggerGraphJson_PassesScenarioValidator(WinConditionSpec spec)
        {
            var s = ScenarioData.CreateBlank("wincon-witness");
            if (spec.Preset == WinPresetKind.KingOfTheHill)
                s.Regions = new[] { new ScenarioRegion { Id = spec.RegionId!, Name = "Zone", MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 } };
            s.TriggerGraphJson = WinConditionPresets.Build(spec)!.ToCanonicalJson();

            ValidationResult r = new ScenarioValidator().Validate(s);
            Assert.True(r.Ok, r.Error);
        }
    }
}
