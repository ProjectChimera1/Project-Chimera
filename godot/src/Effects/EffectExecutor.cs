#nullable enable
using System;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Executes a closed effect graph against an <see cref="EffectContext"/> using a single PRE-ALLOCATED,
    /// explicit LIFO work-stack — no recursion, zero heap allocation per run (AR-8 / AC2 / AC3). One executor
    /// is constructed once and reused for every effect invocation in a match; the per-run state is the transient
    /// work-stack over the world's existing arrays (so 2.1 adds no persistent per-entity SoA array, and the
    /// single-mapper SoA rule does not apply here — see the story Dev Notes).
    ///
    /// Determinism contract:
    ///   • Children pop in authored order (Sequence) and ascending entity-id order (SearchArea) — both achieved
    ///     by REVERSE-pushing onto the LIFO stack. (AC3's "ascending entity-id order" is the application order,
    ///     so SearchArea reverse-pushes exactly like Sequence — the executable resolution of the AC over the
    ///     forward-push sketch in the story.)
    ///   • A single shared hit buffer is reused across all SearchArea nodes: matched ids are captured into child
    ///     frames (by value, via <c>WithTarget</c>) at push time, so the buffer is fully consumed before any
    ///     child — including a nested SearchArea — runs. No per-depth ring is needed (a deliberate simplification
    ///     of the story sketch, proven safe by the nested-search determinism test).
    ///
    /// Bounds (AC2): the load-time <see cref="EffectBounds.Validate"/> is the real gate. The executor adds two
    /// defensive runtime backstops that fail CLOSED (never resize, never throw OOM): it refuses to expand a
    /// composition node at or beyond <c>MaxEffectDepth</c>, and it refuses to push past the stack capacity.
    /// </summary>
    public sealed class EffectExecutor
    {
        private readonly struct Frame
        {
            public readonly EffectNode Node;
            public readonly EffectContext Ctx; // readonly struct of references — cheap copy
            public readonly int Depth;          // composition ancestors (root = 0)

            public Frame(EffectNode node, in EffectContext ctx, int depth)
            {
                Node = node;
                Ctx = ctx;
                Depth = depth;
            }
        }

        private readonly Frame[] _stack;
        private readonly int[] _hitBuffer; // reused across all SearchArea nodes (see class remarks)

        /// <summary>
        /// The peak number of frames simultaneously on the work-stack during the last <see cref="Run"/>. Exposed
        /// so tests can prove the stack never grew beyond its pre-allocated size (AC2). Reset each run.
        /// </summary>
        public int LastPeakStackDepth { get; private set; }

        /// <summary>Construct an executor with the full statically-derived work-stack (<c>EffectCaps.MaxEffectFrames</c>).</summary>
        public EffectExecutor() : this(EffectCaps.MaxEffectFrames) { }

        /// <summary>
        /// Test seam: construct with a custom frame capacity to exercise the fail-closed capacity backstop. The
        /// hit buffer is always full-sized. Not for production use (production graphs pass through
        /// <see cref="EffectBounds.Validate"/>, which keeps within <c>MaxEffectFrames</c>).
        /// </summary>
        internal EffectExecutor(int frameCapacity)
        {
            _stack = new Frame[frameCapacity];
            _hitBuffer = new int[EffectCaps.MaxHitsPerSearch];
        }

        /// <summary>
        /// Execute <paramref name="root"/> against <paramref name="ctx"/>. No-op on a null graph. Caller is
        /// responsible for having validated the graph (<see cref="EffectBounds.Validate"/>) and rebuilt
        /// <c>ctx.Spatial</c> for the current snapshot before any SearchArea fan-out.
        /// </summary>
        public void Run(EffectNode? root, in EffectContext ctx)
        {
            LastPeakStackDepth = 0;
            if (root is null)
                return;

            _stack[0] = new Frame(root, in ctx, 0);
            int sp = 1;
            int peak = 1;

            while (sp > 0)
            {
                Frame f = _stack[--sp];

                switch (f.Node)
                {
                    case SequenceEffect seq:
                        // Defensive: a composition node at/over the depth cap is the (MaxEffectDepth+1)th on its
                        // path — invalid; Validate rejects it at load. Fail closed (skip expansion) at runtime.
                        if (f.Depth >= EffectCaps.MaxEffectDepth) continue;
                        // Reverse-push ⇒ children pop in authored order.
                        for (int k = seq.Children.Length - 1; k >= 0; k--)
                        {
                            if (seq.Children[k] is null) continue;
                            if (sp >= _stack.Length) break; // fail-closed capacity backstop
                            _stack[sp++] = new Frame(seq.Children[k], in f.Ctx, f.Depth + 1);
                        }
                        break;

                    case SearchAreaEffect search:
                        if (f.Depth >= EffectCaps.MaxEffectDepth) continue;
                        if (search.Child is null) break;
                        int count = search.FindTargets(in f.Ctx, _hitBuffer); // QueryRadius + ascending sort + filter
                        // Reverse-push ⇒ lowest-id target pops (applies) first (AC3 ascending order).
                        for (int i = count - 1; i >= 0; i--)
                        {
                            if (sp >= _stack.Length) break; // fail-closed capacity backstop
                            EffectContext childCtx = f.Ctx.WithTarget(_hitBuffer[i]);
                            _stack[sp++] = new Frame(search.Child, in childCtx, f.Depth + 1);
                        }
                        break;

                    case PersistentEffect:
                        // Defined type; periodic execution lands in Story 2.2b. Loud fail-closed guard so a
                        // premature wire-up is caught, not silently mis-run. Validate keeps these off the
                        // executor until 2.2b (the 2.3 validator).
                        throw new NotSupportedException(
                            "PersistentEffect execution lands in Story 2.2b (ModifierStore not yet built).");

                    case ApplyModifierEffect:
                        // ApplyModifierEffect is a LeafEffect, so it MUST be matched before the generic LeafEffect
                        // case below. Its own Apply also throws; this explicit guard makes the deferral visible at
                        // the dispatch site.
                        throw new NotSupportedException(
                            "ApplyModifier execution lands in Story 2.2b (ModifierStore not yet built).");

                    case LeafEffect leaf:
                        leaf.Apply(in f.Ctx); // guards IsAlive / faction internally
                        break;

                    // Unreachable: every EffectNode is a Leaf or a Composition. Kept for total dispatch.
                    default:
                        break;
                }

                if (sp > peak) peak = sp;
            }

            LastPeakStackDepth = peak;
        }
    }
}
