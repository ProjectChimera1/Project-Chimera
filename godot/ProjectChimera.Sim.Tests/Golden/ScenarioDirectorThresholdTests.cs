#nullable enable
using System;
using System.Globalization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.3b (AC3) — proves the de-floated, locale-invariant ScenarioDirector threshold/condition path
    /// fires correctly. Covers BOTH compare sites the story changed: the resource_threshold EVENT (emit→match)
    /// and the resource_comparison CONDITION (EvalCondition).
    ///
    /// Why these tests and not the golden: both goldens load EMPTY triggers, so ScenarioDirector.Tick
    /// early-returns and the emit/match/condition code never runs in the golden. A byte-identical golden
    /// therefore proves "no regression," NOT "the fix works." THESE tests are the real proof — they drive a
    /// real ScenarioDirector with real triggers over a real ResourceStore and assert an OBSERVABLE outcome
    /// (a captured display_message) at the exact boundary, under both the invariant and a comma-decimal culture.
    ///
    /// What the de-DE test DOES prove: the emit→match round-trip is culture-symmetric (InvariantCulture on both
    /// sides, so a comma-decimal locale cannot break the format). What it does NOT prove: cross-architecture
    /// float determinism on a single machine — that hazard is removed STRUCTURALLY by comparing Fixed-vs-Fixed
    /// (raw ints) instead of float, not by this test.
    /// </summary>
    public class ScenarioDirectorThresholdTests
    {
        private const string FireText = "THRESHOLD_FIRED";

        /// <summary>
        /// Drive a fresh ScenarioDirector for ONE tick with a single resource_threshold trigger (faction 0 =
        /// Player1) whose action is an observable display_message, and report whether it fired. Ore[Player1] is
        /// set to <paramref name="oreInt"/>; the threshold is (<paramref name="amount"/>, <paramref name="op"/>).
        /// Optionally runs the tick under <paramref name="culture"/> (restored in a finally).
        /// </summary>
        private static bool ThresholdFires(int oreInt, Fixed amount, string op, string? culture = null)
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(oreInt);
            var director = MakeDirector(resources, out Func<bool> didFire,
                trigger: new TriggerDefinition
                {
                    Name     = "threshold",
                    Enabled  = true,
                    Events   = new[] { new TriggerEvent { Type = "resource_threshold", Faction = 0, Amount = amount, Operator = op } },
                    Conditions = Array.Empty<TriggerCondition>(),
                    Actions  = new[] { new TriggerAction { Type = "display_message", Text = FireText, Duration = 1f } },
                });

            RunOneTick(director, culture);
            return didFire();
        }

        /// <summary>
        /// Drive a fresh ScenarioDirector for ONE tick with a match_start trigger gated by a single
        /// resource_comparison CONDITION (faction 0), and report whether it fired. Proves the EvalCondition
        /// Fixed-vs-Fixed change gates correctly.
        /// </summary>
        private static bool ResourceComparisonGates(int oreInt, Fixed amount, string op)
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(oreInt);
            var director = MakeDirector(resources, out Func<bool> didFire,
                trigger: new TriggerDefinition
                {
                    Name       = "condition",
                    Enabled    = true,
                    Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "resource_comparison", Faction = 0, Amount = amount, Operator = op } },
                    Actions    = new[] { new TriggerAction { Type = "display_message", Text = FireText, Duration = 1f } },
                });

            RunOneTick(director, culture: null);
            return didFire();
        }

        // ── Shared setup ───────────────────────────────────────────────────────

        private static ScenarioDirector MakeDirector(ResourceStore resources, out Func<bool> didFire,
            TriggerDefinition trigger)
        {
            var director = new ScenarioDirector(new BuildingStore(), resources);
            bool fired = false;
            director.OnDisplayMessage = (text, _) => { if (text == FireText) fired = true; };
            director.LoadScenario(new ScenarioData { Triggers = new[] { trigger } });
            didFire = () => fired;
            return director;
        }

        private static void RunOneTick(ScenarioDirector director, string? culture)
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                if (culture is not null)
                    CultureInfo.CurrentCulture = new CultureInfo(culture);
                director.Tick(new EntityWorld(), Fixed.FromInt(1));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        // ── AC3(b): resource_threshold EVENT boundary ─────────────────────────────

        /// <summary>">=" boundary: fires at and above the threshold, NOT below (150≥100 ✓, 100≥100 ✓, 50≥100 ✗).</summary>
        [Fact]
        public void ResourceThreshold_GreaterOrEqual_FiresAtAndAbove_NotBelow()
        {
            Assert.True(ThresholdFires(oreInt: 150, amount: Fixed.FromInt(100), op: ">="), "Ore 150 ≥ 100 should fire.");
            Assert.True(ThresholdFires(oreInt: 100, amount: Fixed.FromInt(100), op: ">="), "Ore 100 ≥ 100 should fire (inclusive boundary).");
            Assert.False(ThresholdFires(oreInt: 50, amount: Fixed.FromInt(100), op: ">="), "Ore 50 ≥ 100 should NOT fire.");
        }

        /// <summary>"&lt;" boundary: fires strictly below only (50&lt;100 ✓, 100&lt;100 ✗, 150&lt;100 ✗).</summary>
        [Fact]
        public void ResourceThreshold_LessThan_FiresStrictlyBelow_NotAtOrAbove()
        {
            Assert.True(ThresholdFires(oreInt: 50, amount: Fixed.FromInt(100), op: "<"), "Ore 50 < 100 should fire.");
            Assert.False(ThresholdFires(oreInt: 100, amount: Fixed.FromInt(100), op: "<"), "Ore 100 < 100 should NOT fire.");
            Assert.False(ThresholdFires(oreInt: 150, amount: Fixed.FromInt(100), op: "<"), "Ore 150 < 100 should NOT fire.");
        }

        // ── AC3(b): resource_comparison CONDITION (the other changed compare site) ──

        /// <summary>The resource_comparison condition gates a match_start trigger on the new Fixed compare.</summary>
        [Fact]
        public void ResourceComparisonCondition_GatesOnFixedCompare()
        {
            Assert.True(ResourceComparisonGates(oreInt: 150, amount: Fixed.FromInt(100), op: ">="),
                "Ore 150 ≥ 100: condition met → trigger fires.");
            Assert.False(ResourceComparisonGates(oreInt: 50, amount: Fixed.FromInt(100), op: ">="),
                "Ore 50 ≥ 100: condition not met → trigger does not fire.");
        }

        // ── AC3(c): culture robustness ─────────────────────────────────────────────

        /// <summary>
        /// The SAME 150≥100 fire case under a comma-decimal culture (de-DE) must STILL fire. Guards against
        /// asymmetric culture handling: a future revert of the emit to a current-culture ToString (yielding
        /// "150,00" under de-DE) while the match parses InvariantCulture would FAIL here. It does NOT prove
        /// cross-architecture float determinism (structural — removed by comparing Fixed-vs-Fixed); it DOES
        /// prove emit/match culture-symmetry and correct firing.
        /// </summary>
        [Fact]
        public void ResourceThreshold_FiresIdentically_UnderCommaDecimalCulture_DeDE()
        {
            Assert.True(ThresholdFires(oreInt: 150, amount: Fixed.FromInt(100), op: ">=", culture: "de-DE"),
                "resource_threshold must fire identically under de-DE — the emit/match round-trip is InvariantCulture on both sides.");
        }
    }
}
