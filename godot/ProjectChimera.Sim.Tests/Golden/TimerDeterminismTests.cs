#nullable enable
using ProjectChimera.Dsl;
using System;
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
        ///
        /// <para>DW-218: every lookup goes through <see cref="ReflectionProbe"/> (null-CHECKED), never the old
        /// <c>GetField(…)!</c> idiom — a renamed member now fails with a diagnostic naming the owner type and the
        /// member instead of an opaque <see cref="System.NullReferenceException"/> at some later use site.</para>
        /// </summary>
        private static List<string> EmittedTimerOrder(string[] creationOrder)
        {
            // Story 7.3: the timer store hoisted from ScenarioDirector into the top-level DslVarTable. Reflect into
            // the table's dense _timerNames/_timerRemaining lists (still creation-index SoA), inject it into the
            // director, and invoke the (unchanged-behavior) CollectEvents emission path.
            var vars = new DslVarTable();
            FieldInfo namesField = ReflectionProbe.Field(typeof(DslVarTable), "_timerNames");
            FieldInfo remField   = ReflectionProbe.Field(typeof(DslVarTable), "_timerRemaining");
            var names     = ReflectionProbe.Read<List<string>>(namesField, vars);
            var remaining = ReflectionProbe.Read<List<int>>(remField, vars);
            names.Clear();
            remaining.Clear();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            foreach (string name in creationOrder)
            {
                names.Add(name);
                remaining.Add(1); // value 1 → CollectEvents decrements to 0 → expires this call
            }

            // Story 7.5: CollectEvents now fills the load-preallocated _baseEvents buffer (zero per-tick heap
            // allocation) instead of returning a List — same emission semantics/order, different plumbing. The
            // buffer is sized in LoadScenario, so load an empty scenario first, then re-inject the timers (the
            // load resets the table). Reflection updated to read the buffer + its count; the ASSERTIONS (creation-
            // index emission order — the AR-16 behavior under test) are unchanged.
            director.LoadScenario(new ScenarioData());
            names.Clear();
            remaining.Clear();
            foreach (string name in creationOrder)
            {
                names.Add(name);
                remaining.Add(1);
            }
            // Signature-checked (not name-only): an added/reordered CollectEvents parameter must fail with a named
            // diagnostic here, not as a parameter-count throw out of Invoke.
            MethodInfo collect = ReflectionProbe.Method(typeof(ScenarioDirector), "CollectEvents", typeof(EntityWorld));
            collect.Invoke(director, new object?[] { new EntityWorld() });

            FieldInfo eventsField = ReflectionProbe.Field(typeof(ScenarioDirector), "_baseEvents");
            FieldInfo countField  = ReflectionProbe.Field(typeof(ScenarioDirector), "_baseEventCount");
            var buffer = ReflectionProbe.Read<System.Array>(eventsField, director);
            int count  = ReflectionProbe.Read<int>(countField, director);

            System.Type firedEventType = ReflectionProbe.NestedType(typeof(ScenarioDirector), "FiredEvent");
            FieldInfo typeF = ReflectionProbe.PublicField(firedEventType, "Type");
            FieldInfo dataF = ReflectionProbe.PublicField(firedEventType, "Data");

            var order = new List<string>();
            for (int i = 0; i < count; i++)
            {
                object fe = ReflectionProbe.ReadElement(buffer, i, "ScenarioDirector._baseEvents");
                if (ReflectionProbe.Read<string>(typeF, fe) == "timer_expires")
                    order.Add(ReflectionProbe.Read<string>(dataF, fe)); // timer_expires always carries the timer name
            }
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

        // ── DW-218 — the probe's own durability contract ───────────────────────────────────────────────────────────
        // These are the regression teeth for the fix: EmittedTimerOrder above probes six private members by NAME, so
        // the value of this test file rests entirely on a rename producing an ACTIONABLE failure. The old
        // `GetField(...)!` idiom produced a NullReferenceException at the use site — no owner type, no member name,
        // nothing pointing at "the test went stale". Asserting the typed, named diagnostic is what makes that
        // impossible to reintroduce: restore the `!` idiom and these two go red.

        [Fact]
        public void ProbingARenamedMember_FailsWithAnActionableDiagnostic_NotAnOpaqueNre()
        {
            // A field/method/nested-type name that does NOT exist — i.e. exactly what a rename leaves behind.
            var field = Assert.Throws<InvalidOperationException>(
                () => ReflectionProbe.Field(typeof(DslVarTable), "_timerNames_RENAMED"));
            Assert.Contains("DslVarTable", field.Message);          // names the OWNER...
            Assert.Contains("_timerNames_RENAMED", field.Message);  // ...and the MEMBER

            var method = Assert.Throws<InvalidOperationException>(
                () => ReflectionProbe.Method(typeof(ScenarioDirector), "CollectEvents_RENAMED", typeof(EntityWorld)));
            Assert.Contains("CollectEvents_RENAMED", method.Message);

            // A signature change (not just a rename) must fail HERE, not later as a parameter-count throw from Invoke.
            var signature = Assert.Throws<InvalidOperationException>(
                () => ReflectionProbe.Method(typeof(ScenarioDirector), "CollectEvents", typeof(EntityWorld), typeof(Fixed)));
            Assert.Contains("CollectEvents", signature.Message);

            var nested = Assert.Throws<InvalidOperationException>(
                () => ReflectionProbe.NestedType(typeof(ScenarioDirector), "FiredEvent_RENAMED"));
            Assert.Contains("FiredEvent_RENAMED", nested.Message);

            // A field whose TYPE changed under the probe is the other half of the stale-probe class: the old idiom's
            // hard cast threw an InvalidCastException naming neither the field nor the expectation.
            FieldInfo real = ReflectionProbe.Field(typeof(DslVarTable), "_timerRemaining");
            var mistyped = Assert.Throws<InvalidOperationException>(
                () => ReflectionProbe.Read<List<string>>(real, new DslVarTable()));
            Assert.Contains("_timerRemaining", mistyped.Message);
        }

        [Fact]
        public void ProbingTheRealMembers_Succeeds_SoTheProbeIsNotVacuous()
        {
            // The negative half above passes trivially if the probe rejects everything, so pin the positive half too:
            // every member EmittedTimerOrder depends on resolves today, with the expected type.
            var vars = new DslVarTable();
            Assert.NotNull(ReflectionProbe.Read<List<string>>(ReflectionProbe.Field(typeof(DslVarTable), "_timerNames"), vars));
            Assert.NotNull(ReflectionProbe.Read<List<int>>(ReflectionProbe.Field(typeof(DslVarTable), "_timerRemaining"), vars));
            Assert.NotNull(ReflectionProbe.Method(typeof(ScenarioDirector), "CollectEvents", typeof(EntityWorld)));

            System.Type fired = ReflectionProbe.NestedType(typeof(ScenarioDirector), "FiredEvent");
            Assert.Equal(typeof(string), ReflectionProbe.PublicField(fired, "Type").FieldType);
            Assert.NotNull(ReflectionProbe.PublicField(fired, "Data"));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(new ScenarioData());
            Assert.NotNull(ReflectionProbe.Read<System.Array>(ReflectionProbe.Field(typeof(ScenarioDirector), "_baseEvents"), director));
            Assert.Equal(0, ReflectionProbe.Read<int>(ReflectionProbe.Field(typeof(ScenarioDirector), "_baseEventCount"), director));
        }
    }
}
