#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.1 AC2 — bounded, non-recursive, zero-alloc execution. Pins the EXACT depth semantics (depth 8
    /// runs, depth 9 rejected — not inferred from the constant), the Sequence fan-out cap, the work-stack peak
    /// staying within the pre-allocated size under real multi-level fan-out, the fail-closed capacity backstop,
    /// and zero heap allocation across a warm run. Every gate has a positive case AND a demonstrably-red
    /// negative control.
    /// </summary>
    public class EffectExecutorBoundsTests
    {
        private static EntityWorld World() => new EntityWorld();

        private static EffectContext SelfCtx(EntityWorld w, int id, SpatialHash? sh = null) =>
            new EffectContext(w, id, id, w.FactionOf[id], DamageTable.Default, sh);

        /// <summary>Wrap <paramref name="leaf"/> in <paramref name="depth"/> nested Sequences (composition depth == depth).</summary>
        private static EffectNode NestedSequences(int depth, EffectNode leaf)
        {
            EffectNode node = leaf;
            for (int i = 0; i < depth; i++)
                node = new SequenceEffect(node);
            return node;
        }

        // ── Depth cap: 8 runs, 9 rejected (pinned by test) ────────────────────────────────────────────

        [Fact]
        public void Depth8Graph_Validates_AndRunsToTheLeaf()
        {
            var w = World();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            EffectNode graph = NestedSequences(EffectCaps.MaxEffectDepth, new DirectHpDeltaEffect(Fixed.FromInt(-10)));

            Assert.True(EffectBounds.Validate(graph).IsValid);

            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, caster);
            ex.Run(graph, in ctx);

            // The single deep leaf fired exactly once: 100 - 10 = 90.
            Assert.Equal(Fixed.FromInt(90).Raw, w.Health[caster].Raw);
        }

        [Fact]
        public void Depth9Graph_IsRejected_WithLocatedError()
        {
            EffectNode graph = NestedSequences(EffectCaps.MaxEffectDepth + 1, new DirectHpDeltaEffect(Fixed.FromInt(-10)));

            EffectBoundsResult r = EffectBounds.Validate(graph);

            Assert.False(r.IsValid);
            Assert.NotNull(r.Error);
            Assert.Contains("MaxEffectDepth", r.Error);                  // located: names the limit
            Assert.Contains(EffectCaps.MaxEffectDepth.ToString(), r.Error!);
        }

        // ── Sequence fan-out cap: 8 children OK, 9 rejected ───────────────────────────────────────────

        [Fact]
        public void SequenceWithMaxChildren_Validates_ButOneMore_IsRejected()
        {
            var ok = new SequenceEffect(MakeHeals(EffectCaps.MaxSequenceChildren));
            Assert.True(EffectBounds.Validate(ok).IsValid);

            var tooMany = new SequenceEffect(MakeHeals(EffectCaps.MaxSequenceChildren + 1));
            EffectBoundsResult r = EffectBounds.Validate(tooMany);
            Assert.False(r.IsValid);
            Assert.Contains("MaxSequenceChildren", r.Error);
        }

        private static EffectNode[] MakeHeals(int n)
        {
            var a = new EffectNode[n];
            for (int i = 0; i < n; i++) a[i] = new HealEffect(Fixed.One);
            return a;
        }

        // ── Sequence boundary cases: 0 and 1 children ─────────────────────────────────────────────────

        [Fact]
        public void EmptySequence_IsValid_AndNoOp()
        {
            var w = World();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var empty = new SequenceEffect();

            Assert.True(EffectBounds.Validate(empty).IsValid);

            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);
            ex.Run(empty, in ctx);

            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[id].Raw); // untouched
        }

        // ── Work-stack peak under real multi-level fan-out never exceeds the pre-allocated size ───────

        [Fact]
        public void MaximalFanout_NeverGrowsBeyondPreallocatedStack()
        {
            // Static worst case the caps imply (a chain of MaxEffectDepth SearchAreas each fanning to
            // MaxSearchTargets): the pre-allocated stack MUST cover it.
            int worstCase = (EffectCaps.MaxEffectDepth - 1) * (EffectCaps.MaxSearchTargets - 1)
                            + EffectCaps.MaxSearchTargets;
            Assert.True(EffectCaps.MaxEffectFrames >= worstCase,
                $"MaxEffectFrames {EffectCaps.MaxEffectFrames} must cover the static worst case {worstCase}.");

            // Real, observable multi-level fan-out from two nested SearchAreas over co-located enemies. The
            // per-search fan-out is DATA-dependent (it was 63 rather than 64 before Story 15.4 pushed the Enemy
            // filter into the spatial query, because the same-cell caster used to consume a buffer slot before
            // being filtered out). Rather than hard-code that subtlety either way, MEASURE the single-level
            // fan-out and derive the exact 2-level peak from it.
            var w = World();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            for (int i = 0; i < EffectCaps.MaxSearchTargets; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(30000), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var leaf = new DirectHpDeltaEffect(Fixed.FromInt(-1));
            var inner = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy, leaf);
            var outer = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy, inner);
            Assert.True(EffectBounds.Validate(outer).IsValid);

            EffectContext ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            // One SearchArea from the caster's position fans out to `fanout` targets (all co-located, so the inner
            // searches — centered on a matched enemy at the same point — match the same set).
            var probe = new int[EffectCaps.MaxHitsPerSearch];
            int fanout = inner.FindTargets(in ctx, probe);
            Assert.True(fanout > 1 && fanout <= EffectCaps.MaxSearchTargets,
                $"need real multi-level fan-out within the cap; got {fanout}.");

            var ex = new EffectExecutor();
            ex.Run(outer, in ctx);

            // Pin the EXACT peak, DERIVED (not hard-coded): the outer search pushes `fanout` inner-frames;
            // popping one and expanding it pushes `fanout` leaf-frames → (fanout-1) un-popped outer siblings +
            // fanout = 2*fanout-1 simultaneous frames. Pinning the exact value (vs a loose ≤ MaxEffectFrames
            // range) gives the work-stack derivation real teeth: if the executor ever retained parent frames or
            // mis-counted, this turns RED. The `MaxEffectFrames >= worstCase` assert above, by contrast, is a
            // 505≥505 tautology that can never fail.
            Assert.Equal(2 * fanout - 1, ex.LastPeakStackDepth);
            Assert.True(ex.LastPeakStackDepth <= EffectCaps.MaxEffectFrames,
                $"peak {ex.LastPeakStackDepth} grew beyond the pre-allocated {EffectCaps.MaxEffectFrames} (AC2 violation).");
        }

        // ── Fail-closed capacity backstop: a too-small stack drops work but never overflows/throws ────

        [Fact]
        public void UndersizedStack_FailsClosed_NeverThrows()
        {
            var w = World();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            for (int i = 0; i < EffectCaps.MaxSearchTargets; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(30000), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DirectHpDeltaEffect(Fixed.FromInt(-1)));

            // Capacity of 4 cannot hold a 64-wide fan-out: the executor must clamp pushes, not index past its
            // array. No exception, no OOB — the defensive backstop holds (a should-never-fire path for validated
            // graphs, but proven to fail closed here).
            var tiny = new EffectExecutor(4);
            EffectContext ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
            Exception? ex = Record.Exception(() => tiny.Run(graph, in ctx));
            Assert.Null(ex);
            Assert.True(tiny.LastPeakStackDepth <= 4);
        }

        // ── Zero heap allocation across a warm run (AC2 / 3.6) ────────────────────────────────────────

        [Fact]
        public void Run_IsZeroAlloc_AfterWarmup()
        {
            var w = World();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            for (int i = 0; i < 8; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(30000), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            // Exercise every dispatch path: Sequence + leaf + leaf + SearchArea→Damage.
            var graph = new SequenceEffect(
                new DirectHpDeltaEffect(Fixed.FromInt(-1)),
                new HealEffect(Fixed.One),
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                     new DamageEffect(Fixed.One, DamageType.Normal)));
            Assert.True(EffectBounds.Validate(graph).IsValid);

            var ex = new EffectExecutor();
            EffectContext ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            ex.Run(graph, in ctx); // warm up JIT + any first-call statics
            ex.Run(graph, in ctx);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++)
                ex.Run(graph, in ctx);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }

        // ── DW-252: the two paths the zero-alloc delta above does NOT cover ────────────────────────────

        [Fact]
        public void Run_IsZeroAlloc_AcrossAFullWidthFanOut()
        {
            // Run_IsZeroAlloc_AfterWarmup measures an 8-target search. This one measures the FULL structural
            // fan-out (MaxSearchTargets frames pushed, MaxSearchTargets leaf applies) — the width where a stray
            // per-target allocation would actually hurt, and the width the executor's stack derivation is sized
            // for. High HP so no target dies: this test isolates the WIDTH, not the kill path.
            var w = World();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            for (int i = 0; i < EffectCaps.MaxSearchTargets; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(30000), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DamageEffect(Fixed.One, DamageType.Normal));
            Assert.True(EffectBounds.Validate(graph).IsValid);

            var ex = new EffectExecutor();
            EffectContext ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            // Pin the width the measurement actually covers, so this can never silently degrade into a narrow
            // run. It is a full MaxSearchTargets now that the Enemy filter runs INSIDE the spatial query — the
            // same-cell caster no longer consumes one of the buffer's slots (Story 15.4 / DW-250).
            var probe = new int[EffectCaps.MaxHitsPerSearch];
            Assert.Equal(EffectCaps.MaxSearchTargets, graph.FindTargets(in ctx, probe));

            ex.Run(graph, in ctx); // warm up JIT + any first-call statics
            ex.Run(graph, in ctx);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++)
                ex.Run(graph, in ctx);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
            Assert.Equal(EffectCaps.MaxSearchTargets, ex.LastPeakStackDepth);
        }

        [Fact]
        public void Run_IsZeroAlloc_OnTheLethalDeathSequence()
        {
            // The pre-existing zero-alloc test used 30000-HP enemies and NO event/stats/death sinks, so the
            // UnitKilled push → RecordKill → DeathFeed push → Destroy sequence was never inside the measured
            // delta. A dead entity cannot be re-killed, so instead of looping over ONE world this pre-builds one
            // world per measured run and warms up on a spare — every allocating construction happens BEFORE the
            // measurement window opens.
            const int MeasuredRuns = 4;
            const int Victims = 8;

            var events = new CombatEventQueue();
            var stats = new MatchStats();
            var deaths = new DeathFeed();
            var ex = new EffectExecutor();

            // 50 damage vs 5 HP (Normal vs Unarmored = 1.0) ⇒ every hit is lethal.
            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DamageEffect(Fixed.FromInt(50), DamageType.Normal));
            Assert.True(EffectBounds.Validate(graph).IsValid);

            var ctxs = new EffectContext[MeasuredRuns + 1]; // [0] = warm-up world
            for (int r = 0; r < ctxs.Length; r++)
            {
                var w = World();
                int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                for (int i = 0; i < Victims; i++)
                    w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(5), Fixed.FromInt(3));
                var sh = new SpatialHash();
                sh.Rebuild(w);
                ctxs[r] = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh,
                                            events: events, stats: stats, deaths: deaths);
            }

            ex.Run(graph, in ctxs[0]); // warms the JIT for the WHOLE lethal sequence, not just the damage math
            Assert.Equal(Victims, deaths.Count); // the kill path really is being exercised
            events.Clear();
            deaths.Clear();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int r = 1; r <= MeasuredRuns; r++)
                ex.Run(graph, in ctxs[r]);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);

            // And it stayed lethal for every measured run (a silently-non-lethal graph would make the delta
            // meaningless). Both sinks stay well inside their 256-slot caps at 4*8 pushes.
            Assert.Equal(MeasuredRuns * Victims, deaths.Count);
            Assert.Equal((MeasuredRuns + 1) * Victims, stats.Losses(Faction.Player2));
            Assert.Equal((MeasuredRuns + 1) * Victims, stats.Kills(Faction.Player1));
        }
    }
}
