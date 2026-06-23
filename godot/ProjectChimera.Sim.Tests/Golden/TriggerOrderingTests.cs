#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.4 (AC3) — the trigger sort is a STABLE TOTAL ORDER: (Priority desc, then ascending declaration
    /// index). Without the declaration-index tiebreak, ScenarioDirector's <c>Array.Sort</c> is an unstable
    /// introsort, so equal-priority triggers can fire in a runtime-dependent order — and since ExecuteActions
    /// runs in that order, equal-priority triggers writing shared state would desync across peers (AR-16).
    ///
    /// These are BEHAVIORAL proofs: they drive a real ScenarioDirector one tick and capture the actual fire
    /// order via OnDisplayMessage. The equal-priority test uses MORE triggers than .NET's introsort insertion-sort
    /// threshold (16) on purpose — a small (≤16) array is sorted by a STABLE insertion sort, which preserves the
    /// already-ascending input order and so does NOT distinguish the priority-only comparator from the tiebreak
    /// (the "looks fine for a handful" trap). Above the threshold the unstable quicksort path engages, so reverting
    /// the tiebreak actually reorders the array — verified as a negative control in the story's Task 8.
    /// </summary>
    public class TriggerOrderingTests
    {
        /// <summary>
        /// Above .NET's IntrospectiveSort insertion-sort threshold (16) so the unstable quicksort path engages:
        /// reverting to a priority-only comparator reorders these equal-priority triggers, while the
        /// (Priority desc, index asc) total order keeps them in declaration order.
        /// </summary>
        private const int EqualPriorityTriggerCount = 24;

        /// <summary>
        /// Build <paramref name="n"/> match_start triggers, all the SAME priority, each firing a display_message
        /// carrying its declaration index, drive one tick, and return the captured fire order (declaration indices).
        /// </summary>
        private static List<int> CaptureEqualPriorityFireOrder(int n)
        {
            var triggers = new TriggerDefinition[n];
            for (int i = 0; i < n; i++)
            {
                triggers[i] = new TriggerDefinition
                {
                    Name     = $"t{i}",
                    Enabled  = true,
                    Priority = 0, // ALL equal — fire order is decided purely by the index tiebreak
                    Events   = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Actions  = new[] { new TriggerAction { Type = "display_message", Text = i.ToString() } },
                };
            }

            var fireOrder = new List<int>(n);
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero));
            director.OnDisplayMessage = (text, _) => fireOrder.Add(int.Parse(text));
            director.LoadScenario(new ScenarioData { Triggers = triggers });
            director.Tick(new EntityWorld(), Fixed.One);
            return fireOrder;
        }

        [Fact]
        public void EqualPriorityTriggers_FireInAscendingDeclarationIndex()
        {
            List<int> order = CaptureEqualPriorityFireOrder(EqualPriorityTriggerCount);

            // match_start matches every trigger on tick 1, so all N must have fired.
            Assert.Equal(EqualPriorityTriggerCount, order.Count);

            // Exact ascending declaration order [0,1,2,...,N-1]. Removing the `a - b` tiebreak makes the introsort
            // shuffle this (negative control, Task 8) → this exact-sequence assert fails.
            Assert.Equal(Enumerable.Range(0, EqualPriorityTriggerCount).ToList(), order);
        }

        [Fact]
        public void HigherPriorityFiresFirst_PrimarySortKey()
        {
            // Three DISTINCT priorities authored in a deliberately non-priority declaration order, proving
            // priority-desc is the primary key (independent of the tiebreak): [mid=5, high=10, low=1] → high,mid,low.
            var triggers = new[]
            {
                MakeMessageTrigger(name: "mid",  priority: 5,  text: "mid"),
                MakeMessageTrigger(name: "high", priority: 10, text: "high"),
                MakeMessageTrigger(name: "low",  priority: 1,  text: "low"),
            };

            var fireOrder = new List<string>();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero));
            director.OnDisplayMessage = (text, _) => fireOrder.Add(text);
            director.LoadScenario(new ScenarioData { Triggers = triggers });
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(new[] { "high", "mid", "low" }, fireOrder);
        }

        private static TriggerDefinition MakeMessageTrigger(string name, int priority, string text) =>
            new TriggerDefinition
            {
                Name     = name,
                Enabled  = true,
                Priority = priority,
                Events   = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                Actions  = new[] { new TriggerAction { Type = "display_message", Text = text } },
            };
    }
}
