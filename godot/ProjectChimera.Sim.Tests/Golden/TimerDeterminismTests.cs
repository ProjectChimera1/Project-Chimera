#nullable enable
using ProjectChimera.Dsl;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 7.1 (AC1) — same-tick timer expiries must be emitted in a DETERMINISTIC order. The store is now the
    /// dense creation-index SoA (parallel <c>_timerNames</c> / <c>_timerRemaining</c> lists), replacing Story 1.4's
    /// ordinal-sorted <c>Dictionary&lt;string,int&gt;</c> snapshot. Enumeration is by ASCENDING creation index — so
    /// same-tick expiries emit in DECLARATION (creation) order, independent of insertion history and with NO
    /// Dictionary enumeration anywhere in the path (AR-16).
    ///
    /// This is the intentional behavior MOVE the story calls out: the old code emitted expiries in ORDINAL
    /// (alphabetical) order; the dense store emits them in CREATION order. Both are deterministic; the story
    /// deliberately switches the surrogate from ordinal-of-name to creation-index.
    ///
    /// WHY THIS IS A DIRECT EMISSION-ORDER ASSERTION (not a SimChecksum comparison): in the current
    /// ScenarioDirector the timer enumeration order's ONLY effect is the order of <c>timer_expires</c> events in the
    /// internal events list, and that list is consumed solely by the boolean <c>AnyEventMatches</c> — triggers then
    /// fire in the independent (Priority desc, declaration index asc) order, so the timer order never reaches
    /// SimChecksum. A "build twice, compare checksums" test would therefore be TAUTOLOGICAL (it would pass even with
    /// the bug), which the story explicitly forbids. We assert the emission order directly via reflection instead —
    /// the same white-box idiom SimChecksumCoverageGuardTest uses. Story 7.3 folds timers into the hash and
    /// supersedes this.
    /// </summary>
    public class TimerDeterminismTests
    {
        /// <summary>
        /// Populate ScenarioDirector's dense timer store (<c>_timerNames</c> / <c>_timerRemaining</c>) in the given
        /// CREATION order (each with 1 tick remaining, so <c>CollectEvents</c> decrements to 0 → expires this call),
        /// invoke the private <c>CollectEvents</c>, and return the emitted timer_expires names in emission order.
        /// Reflection is required: the store and the emission order are internal and not exposed on the public API
        /// (see the class remarks for why that is the whole point of the AR-16 nondeterminism).
        /// </summary>
        private static List<string> EmittedTimerOrder(string[] creationOrder)
        {
            // Story 7.3: the timer store hoisted from ScenarioDirector into the top-level DslVarTable. Reflect into
            // the table's dense _timerNames/_timerRemaining lists (still creation-index SoA), inject it into the
            // director, and invoke the (unchanged-behavior) CollectEvents emission path.
            var vars = new DslVarTable();
            var namesField = typeof(DslVarTable).GetField("_timerNames", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var remField   = typeof(DslVarTable).GetField("_timerRemaining", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var names = (List<string>)namesField.GetValue(vars)!;
            var remaining = (List<int>)remField.GetValue(vars)!;
            names.Clear();
            remaining.Clear();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            foreach (string name in creationOrder)
            {
                names.Add(name);
                remaining.Add(1); // value 1 → CollectEvents decrements to 0 → expires this call
            }

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
        public void SameTickExpiries_EmitInCreationIndexOrder_DeterministicAndNotAlphabetical()
        {
            // Creation order drives emission order — deterministically. [B,A] emits [B,A] (NOT alphabetical [A,B]);
            // [A,B] emits [A,B]. The old ordinal-sorted snapshot emitted ALPHABETICALLY regardless of creation
            // order, so it would emit [A,B] for BOTH inputs — reverting to that snapshot turns the first assertion
            // red (negative control). Reverting further to raw Dictionary.Keys enumeration makes emission depend on
            // insertion-hash order, which this creation-index store removes entirely.
            Assert.Equal(new[] { "B", "A" }, EmittedTimerOrder(new[] { "B", "A" }));
            Assert.Equal(new[] { "A", "B" }, EmittedTimerOrder(new[] { "A", "B" }));

            // Determinism: the SAME creation order always yields the SAME emission order across fresh directors.
            Assert.Equal(EmittedTimerOrder(new[] { "B", "A" }), EmittedTimerOrder(new[] { "B", "A" }));
        }

        /// <summary>
        /// Story 7.1 review patch — a sub-frame <c>create_timer</c> (0 &lt; seconds &lt; 1/30) must still fire, exactly
        /// as the old Dictionary store did (it stored 0 ticks and fired one tick later). In the dense store,
        /// <c>remaining == 0</c> also means "expired/inactive", so a raw <c>SecondsToTicks == 0</c> would be skipped
        /// forever and the timer would NEVER fire. The create path clamps to at least 1 tick to preserve the old
        /// "fires next tick" latency. This drives a real director end-to-end: without the clamp it goes red.
        /// </summary>
        [Fact]
        public void SubTickCreateTimer_StillFiresNextTick()
        {
            const string fired = "TIMER_FIRED";
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            bool didFire = false;
            director.OnDisplayMessage = (text, _) => { if (text == fired) didFire = true; };
            director.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name    = "arm",
                        Enabled = true,
                        RunOnce = true,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        // 655 raw ≈ 0.01s → SecondsToTicks rounds to 0 ticks (the sub-frame case).
                        Actions = new[] { new TriggerAction { Type = "create_timer", TimerName = "t", TimerSeconds = Fixed.FromRaw(655) } },
                    },
                    new TriggerDefinition
                    {
                        Name    = "onExpire",
                        Enabled = true,
                        Events  = new[] { new TriggerEvent { Type = "timer_expires", TimerName = "t" } },
                        Actions = new[] { new TriggerAction { Type = "display_message", Text = fired, Duration = Fixed.FromInt(1) } },
                    },
                },
            });

            director.Tick(new EntityWorld(), Fixed.One); // tick 1: match_start → create_timer (remaining clamped to 1)
            Assert.False(didFire, "Timer must not fire on the tick it is created.");
            director.Tick(new EntityWorld(), Fixed.One); // tick 2: timer decrements 1→0 → timer_expires → message
            Assert.True(didFire, "Sub-frame timer must fire one tick later (clamped to 1 tick), not never.");
        }
    }
}
