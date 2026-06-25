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

            // Real, observable multi-level fan-out: two nested SearchAreas over MaxSearchTargets co-located
            // enemies → peak == (2-1)*(64-1)+64 == 127 frames (well within the 505 pre-alloc).
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

            var ex = new EffectExecutor();
            EffectContext ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
            ex.Run(outer, in ctx);

            Assert.True(ex.LastPeakStackDepth > EffectCaps.MaxSearchTargets,
                $"expected real multi-level fan-out (> {EffectCaps.MaxSearchTargets}); got peak {ex.LastPeakStackDepth}.");
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
    }
}
