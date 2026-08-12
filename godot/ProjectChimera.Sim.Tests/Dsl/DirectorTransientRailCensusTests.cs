#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Sim.Tests.Sim; // ClearCompletenessSweep — the shared LOUD-on-rename reflection helpers
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-734 — the structural device that forces the NEXT per-tick transient rail on
    /// <see cref="ScenarioDirector"/> to be drained on BOTH tick paths.
    ///
    /// <para><b>The class, not the instance.</b> The director's trigger-less early-out has needed a hand-added
    /// <c>Clear()</c> for every transient rail introduced since Story 7.13 — <c>_simEventFeed</c>, then the
    /// player_chat rail, then DW-349's re-queue rail, then DW-551's <c>DeathLog</c> — and every one of them was
    /// added REACTIVELY, after the rail had shipped leaking. Four instances of one pattern is a class. Nothing
    /// forced a new rail to be handled on the trigger-free path, and the leak stayed invisible until some
    /// downstream capacity ceiling or persistence assert tripped on a trigger-free map.</para>
    ///
    /// <para><b>The device, in two halves.</b> (1) <c>ScenarioDirector.ClearTransients</c> is now the ONE drain
    /// point that path uses, so a new rail has an obvious home. (2) This census reflects over every instance field
    /// of the director, recognizes the ones with RAIL SHAPE (a count-like scalar, or a reference exposing
    /// <c>Count</c> + <c>Clear()</c>), and requires each to appear in exactly one of two declared lists below —
    /// drained-on-a-trigger-less-tick, or exempt-with-a-recorded-reason. A newly added rail lands in NEITHER and
    /// turns this red with instructions. The drained list is not merely declared: it is asserted against a live
    /// director whose every rail was loaded before a trigger-less tick, so the classification has to be TRUE.</para>
    ///
    /// <para><b>Why the trigger-bearing path is covered by classification rather than by a second call.</b> There,
    /// each rail is drained where its contents are CONSUMED, and several are deliberately non-empty at the tick
    /// boundary (the event queue holds next-tick raises; the carry rail holds trigger-phase deaths for the next
    /// collect) — a wholesale wipe would destroy folded state. So the honest guard is the census: it makes the
    /// author of the next rail state which path drains it and why, instead of discovering the answer in a bug.</para>
    ///
    /// <para>Godot-free; no <c>Fixed</c>-to-float; the census is pure reflection over the shipping type.</para>
    /// </summary>
    public class DirectorTransientRailCensusTests
    {
        // ── The classification. Every rail-shaped director field must appear in exactly one of these. ──────

        /// <summary>
        /// The per-tick TRANSIENT rails: state that must be EMPTY once a trigger-less tick has finished. Each is
        /// asserted for real below against a live director, so adding a name here without draining it fails.
        /// </summary>
        private static readonly string[] DrainedOnATriggerlessTick =
        {
            "_simEventFeed",        // Story 7.13 — the sim-driven built-in event feed (unit_damaged/…/hero_level)
            "_pendingChatCount",    // Story 7.13 Arm D — the player_chat rail
            "_pendingRequeueCount", // DW-349 — the edge-event re-queue rail
            "_eventQueue",          // Story 7.5 — the next-tick custom-event queue (FOLDED; provably empty here)
            "_carryCount",          // DW-548 — deaths deferred from the trigger phase to the next collect
            "_collectedDeaths",     // DW-548 — how much of the DeathLog this tick's collect already consumed
        };

        /// <summary>
        /// Rail-SHAPED fields that are NOT per-tick transients. Every entry states why, because "it looked like
        /// load state to me" is exactly how a real rail gets skipped. Anything not here and not above is unclassified
        /// and fails the census.
        /// </summary>
        private static readonly (string Field, string Why)[] NotAPerTickRail =
        {
            ("_execs",                "LOAD state — the compiled trigger list, rebuilt only by LoadScenario."),
            ("_triggerNodeIdToExec",  "LOAD state — the node-id → exec index map built at LoadScenario."),
            ("_objectiveVarNameById", "LOAD state — the authored objective-variable map."),
            ("_timerNameIndex",       "LOAD state — the interned timer-name table's reverse index."),
            ("_buildingIdIndex",      "LOAD state — the interned building-id table's reverse index."),
            ("_loopState",            "FOLDED cross-tick state (batched continuation rows + fuel); the fuel resets " +
                                      "at the top of every tick, the rows deliberately survive across ticks."),
            ("_vars",                 "FOLDED match state — the DSL variable/timer table, not a rail."),
            ("_triggerEnabled",       "FOLDED match state — the per-exec enabled mask."),
            ("_expiredTimers",        "CLEARED-THEN-REFILLED scratch, not drained-to-empty: both paths Clear() it " +
                                      "immediately before TimerTickAndCollectExpired refills it, and the only " +
                                      "reader runs in that same statement pair. Non-empty at the tick boundary by " +
                                      "design whenever a timer expired."),
            ("_baseEventCount",       "REFILLED-BEFORE-READ on the trigger-bearing path (CollectEvents resets it to " +
                                      "0 and re-emits); the trigger-less path never reads it. Wiping it at the " +
                                      "boundary would buy nothing and hide the refill contract."),
            ("_workHead",             "Cursor into the same-tick custom work list, reset at the top of every " +
                                      "trigger-bearing tick before the list is seeded."),
            ("_workCount",            "Length of that same-tick work list — reset with _workHead, above."),
            ("_frameCount",           "Depth of the CURRENT dispatch frame's param window; zeroed at each frame " +
                                      "boundary inside the sweep, never carried between ticks."),
            ("_runDepth",             "The run_trigger nesting counter — reset at the START of every tick " +
                                      "(including the early-out) before anything can nest."),
            ("_publishTick",          "A monotone publish counter for the presentation readback, deliberately " +
                                      "NEVER reset — it is the version stamp consumers compare."),
            ("_regions",              "LOAD state — the authored region store."),
            ("_buildings",            "HOST-LIFETIME dependency — the shared BuildingStore the director reads. Its " +
                                      "reset is SimulationHost.ClearForReset's own line, swept by the store cases."),
            ("_combatEvents",         "HOST-OWNED transient sink injected via SetEffectRuntime — the presentation " +
                                      "cue queue, drained by its own consumer, never by this director."),
            ("_deaths",               "HOST-OWNED transient sink injected via SetEffectRuntime — the DeathFeed, " +
                                      "drained by DeathFeedDrainSystem (registered LAST) and enforced empty at the " +
                                      "tick boundary by SimulationLoop's DW-766 assertion, not here."),
            (ClearCompletenessSweep.BackingField("EffectSpatialRebuildCount"),
                                      "A cumulative OBSERVATION counter for the DW-339 at-most-once-rebuild test " +
                                      "seam — never folded, never read by the tick, and deliberately never reset " +
                                      "(a rail-shaped name that is the opposite of a rail)."),
        };

        // ── The census ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EveryRailShapedDirectorField_IsClassifiedDrainedOrExempt()
        {
            var director = NewDirector(out _, out _, out _);
            string[] declared = DrainedOnATriggerlessTick
                .Concat(NotAPerTickRail.Select(e => e.Field))
                .ToArray();

            string[] unclassified = RailShapedFields(director)
                .Where(f => !declared.Contains(f.Name, StringComparer.Ordinal))
                .Select(f => $"{f.Name} ({Describe(f.FieldType)})")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.True(unclassified.Length == 0,
                "DW-734: these ScenarioDirector fields have PER-TICK RAIL shape but are classified nowhere:\n  " +
                string.Join("\n  ", unclassified) +
                "\nDecide which they are and say so in DirectorTransientRailCensusTests:\n" +
                "  • a per-tick transient (must be empty after a trigger-less tick) → drain it in " +
                "ScenarioDirector.ClearTransients AND add it to DrainedOnATriggerlessTick;\n" +
                "  • anything else (load state, folded match state, a refilled-before-read cursor) → add it to " +
                "NotAPerTickRail WITH the reason.\n" +
                "This is the whole point of the census: the trigger-less early-out has silently leaked one rail per " +
                "feature added since Story 7.13 (_simEventFeed, the player_chat rail, DW-349's re-queue rail, " +
                "DW-551's DeathLog), each found only after it shipped.");
        }

        [Fact]
        public void NoFieldIsClassifiedTwice_AndEveryClassifiedNameStillExists()
        {
            // A name that no longer resolves is a silent widening — the exact hazard the reset sweep's
            // allowlist-rename guard closes for stores, applied to this census.
            var director = NewDirector(out _, out _, out _);
            var all = ClearCompletenessSweep.InstanceFieldsOf(director.GetType()).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

            string[] declared = DrainedOnATriggerlessTick.Concat(NotAPerTickRail.Select(e => e.Field)).ToArray();
            string[] dead = declared.Where(n => !all.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(dead.Length == 0,
                "DW-734: these census entries no longer name a ScenarioDirector field: " + string.Join(", ", dead) +
                ". Reconcile the census with the rename deliberately — a dead entry can mask its replacement.");

            string[] duplicated = declared.GroupBy(n => n, StringComparer.Ordinal)
                                          .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            Assert.True(duplicated.Length == 0,
                "DW-734: classified BOTH drained and exempt: " + string.Join(", ", duplicated));

            Assert.All(NotAPerTickRail, e => Assert.False(string.IsNullOrWhiteSpace(e.Why)));
        }

        // ── The teeth: the classification has to be true of a real trigger-less tick ──────────────────────

        [Fact]
        public void ATriggerlessTick_DrainsEveryRailDeclaredDrained()
        {
            // RED for any rail whose Clear() is missing from ClearTransients — which is precisely how _simEventFeed,
            // the chat rail, the requeue rail and the DeathLog each shipped leaking, one at a time.
            ScenarioDirector director = NewDirector(out DslEventQueue queue, out DslSimEventFeed feed, out EntityWorld world);
            LoadEveryRail(director, queue, feed, world);

            director.Tick(world, Fixed.One);

            var residue = new List<string>();
            foreach (string name in DrainedOnATriggerlessTick)
            {
                int count = CountOf(director, name);
                if (count != 0) residue.Add($"{name} = {count}");
            }

            Assert.True(residue.Count == 0,
                "DW-734: these rails survived a trigger-less tick: " + string.Join(", ", residue) +
                ". Every one of them must be drained by ScenarioDirector.ClearTransients — the early-out is the " +
                "path with no consumer, so a rail left standing there accumulates across ticks until its capacity " +
                "ceiling or a downstream persistence assert trips.");

            // The world-side rail the director also owns on this path (DW-551). Not a director FIELD, so the census
            // cannot see it — asserted explicitly so it cannot regress either.
            Assert.Equal(0, world.DeathLog.Count);
        }

        [Fact]
        public void TheStagingActuallyFillsEveryRail_SoTheAssertionAboveIsNotVacuous()
        {
            // Without this, a staging that quietly stopped loading (say) the chat rail would make the drain
            // assertion pass by never dirtying it — the same vacuity trap the store reset sweep guards with its
            // per-field divergence precondition.
            ScenarioDirector director = NewDirector(out DslEventQueue queue, out DslSimEventFeed feed, out EntityWorld world);
            LoadEveryRail(director, queue, feed, world);

            var empty = DrainedOnATriggerlessTick.Where(n => CountOf(director, n) == 0).ToArray();
            Assert.True(empty.Length == 0,
                "DW-734: the census staging left these rails EMPTY before the trigger-less tick, so the drain " +
                "assertion is blind on them: " + string.Join(", ", empty));
            Assert.True(world.DeathLog.Count > 0);
        }

        [Fact]
        public void TheTriggerlessTickStillTicksDeclaredTimers_AndRefillsTheExpiredScratch()
        {
            // Tooth against the wrong extraction: the timer tick is WORK, not a drain, so folding it into
            // ClearTransients (or losing it while extracting) would stop declared timers counting down on a
            // trigger-free map — the Story-7.3 contract this early-out has to keep. It also pins the exemption
            // reason recorded for _expiredTimers above: that rail is CLEARED-THEN-REFILLED in the same statement
            // pair, so it is legitimately non-empty at the tick boundary on the expiry tick.
            var table = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), table);
            director.LoadScenario(new ScenarioData
            {
                Timers = new[] { new ScenarioTimer { Name = "t", Seconds = Fixed.One } }, // 1 s = 30 ticks
            });
            var world = new EntityWorld();

            var expired = ClearCompletenessSweep.GetPrivate<List<string>>(director, "_expiredTimers");
            for (int i = 0; i < 29; i++)
            {
                director.Tick(world, Fixed.One);
                Assert.Empty(expired); // counting down — and re-cleared every tick, never accumulating
            }

            director.Tick(world, Fixed.One); // tick 30: the timer reaches zero

            Assert.Equal(new[] { "t" }, expired);
        }

        // ── staging + reflection helpers ──────────────────────────────────────────────────────────────────

        /// <summary>A director with NO triggers (the early-out path) over host-shared rails the test can push into.</summary>
        private static ScenarioDirector NewDirector(out DslEventQueue queue, out DslSimEventFeed feed, out EntityWorld world)
        {
            queue = new DslEventQueue();
            feed  = new DslSimEventFeed();
            world = new EntityWorld();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(),
                                                new DslLoopState(), queue, new TriggerEnabledStore(), feed);
            director.LoadScenario(new ScenarioData()); // no trigger graph ⇒ _execs.Count == 0
            return director;
        }

        /// <summary>Push a record onto every rail the census declares drained, plus the world DeathLog.</summary>
        private static void LoadEveryRail(ScenarioDirector director, DslEventQueue queue, DslSimEventFeed feed, EntityWorld world)
        {
            feed.Push(DslSimEventFeed.KindUnitDamaged, 0, 1, 2, 3);
            queue.Enqueue(EventBounds.PlayerChatRailCode, 0, new[] { 0, 7 }, 2);     // the player_chat rail entry
            queue.Enqueue(EventBounds.RequeueRailBase, 0, new[] { 1, 2, 3, 0 }, 4);  // a persisted edge occurrence
            world.DeathLog.Push(3, 0, 4, 1);

            // The two death-carry scalars have no public writer (they are set by the trigger-phase drain), so they
            // are poked directly — the same LOUD-on-rename reflection the store sweep's fixtures use.
            ClearCompletenessSweep.Poke(director, "_carryCount", 1);
            ClearCompletenessSweep.Poke(director, "_collectedDeaths", 1);
            // Likewise the two transient counters the dequeue re-fills; the queue rows above are what a real tick
            // would turn into them, but the early-out returns before that dequeue runs.
            ClearCompletenessSweep.Poke(director, "_pendingChatCount", 1);
            ClearCompletenessSweep.Poke(director, "_pendingRequeueCount", 1);
        }

        /// <summary>The "how full is it" reading for a declared rail: an int field verbatim, a reference field's
        /// <c>Count</c>. LOUD on a name that no longer resolves.</summary>
        private static int CountOf(ScenarioDirector director, string fieldName)
        {
            FieldInfo f = ClearCompletenessSweep.InstanceFieldsOf(director.GetType())
                .FirstOrDefault(x => string.Equals(x.Name, fieldName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"DW-734 census: ScenarioDirector has no field '{fieldName}'.");

            object? value = f.GetValue(director);
            if (value is int i) return i;
            if (value is null) return 0;
            PropertyInfo? count = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (count?.GetValue(value) is int c) return c;
            throw new InvalidOperationException(
                $"DW-734 census: ScenarioDirector.{fieldName} exposes no int Count — teach CountOf about its shape " +
                "rather than dropping it from the census.");
        }

        /// <summary>
        /// The RAIL SHAPE recognizer: an integer scalar (a count/cursor), or a reference exposing both an
        /// <c>int Count</c> and a <c>Clear()</c> — the shape every rail the early-out has ever leaked actually had.
        /// Arrays are excluded on purpose: the director's are count-gated backing lanes whose stale tail is unread
        /// by contract (the documented PatrolWaypoints discipline), so it is their COUNT that is the rail.
        /// </summary>
        private static IEnumerable<FieldInfo> RailShapedFields(ScenarioDirector director)
        {
            foreach (FieldInfo f in ClearCompletenessSweep.InstanceFieldsOf(director.GetType()))
            {
                Type t = f.FieldType;
                if (t == typeof(int) || t == typeof(uint)) { yield return f; continue; }
                if (t.IsArray || t.IsPrimitive || t.IsEnum || t == typeof(string)) continue;

                bool hasCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(int)
                             || typeof(ICollection).IsAssignableFrom(t);
                bool hasClear = t.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null;
                if (hasCount && hasClear) yield return f;
            }
        }

        private static string Describe(Type t) => t.IsGenericType
            ? $"{t.Name.Split('`')[0]}<{string.Join(", ", t.GetGenericArguments().Select(a => a.Name))}>"
            : t.Name;
    }
}
