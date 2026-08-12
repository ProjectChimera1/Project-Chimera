#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-544 — the creator-facing DSL reference (<c>docs/dsl-reference.md</c>) and its trigger/event
    /// persistence-scope section.
    ///
    /// <para><b>The gap.</b> The DW-349 re-queue rail persists exactly two loss classes — a fuel-break skip and a
    /// batched-suppression skip. An edge occurrence dropped because the trigger was DISABLED, RUN-ONCE-SPENT or
    /// COOLING (including a cooldown armed by an earlier same-tick occurrence) is discarded exactly as before, and
    /// so is a pending targeted redelivery whose trigger has become ineligible by arrival. That split is deliberate
    /// — authored-semantics parity with polled events — and it is test-pinned in
    /// <see cref="DslEdgeEventRequeueTests"/>. It was NOT written down anywhere an author reads, so authors would
    /// reasonably expect a cooldown-suppressed death to replay.</para>
    ///
    /// <para><b>Why a test for a document.</b> A reference that drifts from the runtime is worse than none: an
    /// author trusts it. The event-kind lists below are re-derived from <c>ScenarioDirector.RequeueKindOf</c>'s own
    /// source — the single switch that decides what may ride the rail — so adding or removing a persistable event
    /// type turns this red until the document is updated with it. The rest asserts the non-goals are stated at all,
    /// which is the whole point of the entry.</para>
    ///
    /// <para>Godot-free: reads two files as text.</para>
    /// </summary>
    public class DslReferenceDocTests
    {
        /// <summary>The switch arms of <c>RequeueKindOf</c>: <c>"unit_dies" =&gt; 1,</c>. Named event types only —
        /// the <c>_ =&gt; -1</c> default (the polled kinds) has no literal to capture.</summary>
        private static readonly Regex RequeueArm =
            new(@"""(?<type>[a-z_]+)""\s*=>\s*[0-9]+\s*,", RegexOptions.Compiled);

        [Fact]
        public void TheDslReference_Exists_AndCarriesAnEventPersistenceScopeSection()
        {
            string doc = ReferenceText();

            Assert.Contains("persistence scope", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("re-queue rail", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("edge", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("polled", doc, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheReference_NamesEveryEventTypeTheRailCanActuallyPersist()
        {
            // Derived from the live switch, never hand-listed here: if a tenth edge event becomes persistable and
            // the reference is not updated, an author reading it will believe that event is lost when it is not.
            IReadOnlyList<string> persistable = PersistableEventTypes();
            string doc = ReferenceText();

            Assert.True(persistable.Count >= 9,
                $"only {persistable.Count} persistable event types parsed out of ScenarioDirector.RequeueKindOf — "
                + "the scan's shape assumption broke, so this guard is not actually checking anything.");

            var missing = new List<string>();
            foreach (string type in persistable)
                if (!doc.Contains(type, StringComparison.Ordinal))
                    missing.Add(type);

            Assert.True(missing.Count == 0,
                "docs/dsl-reference.md does not list every event type the DW-349 re-queue rail can persist "
                + "(ScenarioDirector.RequeueKindOf is the source of truth). Missing: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheReference_StatesTheNonGoals_NotJustWhatTheRailDoes()
        {
            // The entry's actual closure: an author must be able to READ that a cooldown-suppressed edge event is
            // dropped rather than replayed. Each of these is one of the four dropped classes.
            string doc = ReferenceText();

            Assert.Contains("disabled", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("run-once", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cooldown", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("same tick", doc, StringComparison.OrdinalIgnoreCase);   // the earlier-occurrence cooldown
            Assert.Contains("redelivery", doc, StringComparison.OrdinalIgnoreCase);  // the ineligible-on-arrival drop
        }

        [Fact]
        public void TheReference_StatesTheRailsCapacityAsTheCodeDefinesIt()
        {
            // A number in prose rots unless something checks it. This one is small and load-bearing (an author
            // hitting it silently loses occurrences), so it is stated in the doc and pinned here.
            Assert.Contains(EventBounds.MaxNextTickEventQueue.ToString(), ReferenceText(), StringComparison.Ordinal);
        }

        [Fact]
        public void TheReference_SaysConditionsAreReEvaluatedAtRedelivery_NotFrozen()
        {
            // RequeueEligible deliberately does NOT pre-evaluate conditions; they are state predicates re-checked
            // at dispatch. An author who assumes the verdict was banked writes a scenario that misfires.
            string doc = ReferenceText();

            Assert.Contains("re-check", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("condition", doc, StringComparison.OrdinalIgnoreCase);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

        private static IReadOnlyList<string> PersistableEventTypes()
        {
            string src = File.ReadAllText(RepoFile(Path.Combine("godot", "src", "Core", "ScenarioDirector.cs")));

            int at = src.IndexOf("int RequeueKindOf(", StringComparison.Ordinal);   // the DECLARATION, not a call site
            Assert.True(at >= 0, "ScenarioDirector.RequeueKindOf no longer exists — re-derive this guard's source.");
            int open = src.IndexOf("switch", at, StringComparison.Ordinal);
            Assert.True(open >= 0, "RequeueKindOf no longer dispatches through a switch expression.");
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            Assert.True(close > open, "could not find the end of RequeueKindOf's switch expression.");

            var types = new List<string>();
            foreach (Match m in RequeueArm.Matches(src.Substring(open, close - open)))
                types.Add(m.Groups["type"].Value);
            return types;
        }

        private static string ReferenceText() =>
            File.ReadAllText(RepoFile(Path.Combine("docs", "dsl-reference.md")));

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Dsl/ ⇒ the repo root is ../../.. of it.</summary>
        private static string RepoFile(string relativePath,
            [System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            string path = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", relativePath));
            Assert.True(File.Exists(path), $"expected file not found at '{path}' (via [CallerFilePath]).");
            return path;
        }
    }
}
