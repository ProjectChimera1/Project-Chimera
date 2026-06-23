#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.4 (AC4) — same-tick timer expiries must be emitted in a DETERMINISTIC order that is independent
    /// of the order the timers were created (AR-16, nondeterminism #3). The as-built code iterated
    /// <c>new List&lt;string&gt;(_timers.Keys)</c> — Dictionary enumeration order, which depends on insertion
    /// history and so differs between two peers whose timers were created in different orders. The fix iterates an
    /// ordinal-sorted key snapshot.
    ///
    /// WHY THIS IS A DIRECT EMISSION-ORDER ASSERTION (not a SimChecksum comparison): in the current ScenarioDirector
    /// the timer enumeration order's ONLY effect is the order of <c>timer_expires</c> events in the internal events
    /// list, and that list is consumed solely by the boolean <c>AnyEventMatches</c> — triggers then fire in the
    /// independent (Priority desc, declaration index asc) SORT order, so the timer order never reaches SimChecksum.
    /// A "build twice, compare checksums" test would therefore be TAUTOLOGICAL (it would pass even with the bug),
    /// which the story explicitly forbids. We assert the emission order directly via reflection instead — the same
    /// white-box idiom SimChecksumCoverageGuardTest uses. Story 7.2 folds timers into the hash and supersedes this.
    /// </summary>
    public class TimerDeterminismTests
    {
        /// <summary>
        /// Populate ScenarioDirector's private <c>_timers</c> in the given insertion order (each expiring on the
        /// next enumeration), invoke the private <c>CollectEvents</c>, and return the emitted timer_expires names
        /// in emission order. Reflection is required: the emission order is internal and not exposed on the public
        /// API (see the class remarks for why that is the whole point of the AR-16 nondeterminism).
        /// </summary>
        private static List<string> EmittedTimerOrder(string[] insertionOrder)
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero));

            var timersField = typeof(ScenarioDirector).GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var timers = (Dictionary<string, int>)timersField.GetValue(director)!;
            timers.Clear();
            foreach (string name in insertionOrder)
                timers[name] = 1; // value 1 → CollectEvents decrements to 0 → expires this call

            var collect = typeof(ScenarioDirector).GetMethod("CollectEvents", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var events = (IEnumerable)collect.Invoke(director, new object?[] { new EntityWorld() })!;

            System.Type firedEventType = typeof(ScenarioDirector).GetNestedType("FiredEvent", BindingFlags.NonPublic)!;
            FieldInfo typeF = firedEventType.GetField("Type")!;
            FieldInfo dataF = firedEventType.GetField("Data")!;

            var order = new List<string>();
            foreach (object fe in events)
                if ((string)typeF.GetValue(fe)! == "timer_expires")
                    order.Add((string)dataF.GetValue(fe)!);
            return order;
        }

        [Fact]
        public void SameTickExpiries_EmitInOrdinalSortedOrder_IndependentOfInsertionOrder()
        {
            string[] sorted = { "alpha", "beta", "gamma" };

            List<string> forwardInsertion = EmittedTimerOrder(new[] { "alpha", "beta", "gamma" });
            List<string> reverseInsertion = EmittedTimerOrder(new[] { "gamma", "beta", "alpha" });

            // Both insertion histories must yield the SAME ordinal-sorted emission order. The reverse-insertion
            // case is the one that bites: with the Dictionary.Keys enumeration bug it emits ["gamma","beta","alpha"]
            // (insertion order) ≠ sorted, so reverting the OrderBy fix turns this red (negative control, Task 8).
            Assert.Equal(sorted, forwardInsertion);
            Assert.Equal(sorted, reverseInsertion);
            // Determinism: identical emission regardless of insertion history (the property two peers rely on).
            Assert.Equal(forwardInsertion, reverseInsertion);
        }
    }
}
