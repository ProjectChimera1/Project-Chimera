#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core; // Fixed — the authored SearchArea radius the DW-534 cap bounds

namespace ProjectChimera.Effects
{
    /// <summary>The outcome of <see cref="EffectBounds.Validate"/>: valid, or invalid with a LOCATED reason.</summary>
    public readonly struct EffectBoundsResult
    {
        /// <summary>True when the graph is within all structural caps.</summary>
        public readonly bool IsValid;

        /// <summary>A located error (which node type, which limit) when <see cref="IsValid"/> is false; else null.</summary>
        public readonly string? Error;

        private EffectBoundsResult(bool isValid, string? error)
        {
            IsValid = isValid;
            Error = error;
        }

        /// <summary>A valid result.</summary>
        public static readonly EffectBoundsResult Valid = new(true, null);

        /// <summary>An invalid result carrying a located <paramref name="error"/>.</summary>
        public static EffectBoundsResult Invalid(string error) => new(false, error);
    }

    /// <summary>
    /// Load-time bound check for an effect graph (AC2). Rejects a graph whose composition nesting exceeds
    /// <c>EffectCaps.MaxEffectDepth</c>, whose any <c>SequenceEffect</c> holds more than
    /// <c>EffectCaps.MaxSequenceChildren</c> children, or (DW-534) whose any <c>SearchAreaEffect</c> declares a
    /// radius above <c>EffectCaps.MaxSearchRadius</c>, with a LOCATED error (offending node type + the limit).
    ///
    /// <para>DW-534 — this is the ONLY place the authored search radius is bounded, and deliberately so: it is the
    /// shared load-time gate every authored effect source already runs (<c>AbilityValidator</c>,
    /// <c>ItemDefinitionValidator</c>, and <c>ScenarioValidator</c>'s <c>run_effect</c> embeds), so one check here
    /// covers all three, and the cost it bounds — <c>SpatialHash.QueryRadiusLowestIds</c>' deliberately
    /// exit-free scan — cannot be bounded inside the scan without breaking its global-lowest-ids contract. See
    /// <c>EffectCaps.MaxSearchRadius</c> for the derivation of the value.</para>
    ///
    /// Depth semantics (PINNED BY TEST — do not infer from the constant): "depth" is the count of composition
    /// nodes on a root→leaf path. The root frame is depth 0; a composition node at frame-depth d is the (d+1)th
    /// composition on its path. A graph of N nested composition nodes is a "depth-N" graph — depth 8 is accepted,
    /// depth 9 is rejected. Leaves carry no depth contribution.
    ///
    /// Iterative (explicit stack), so validating a maliciously deep graph cannot itself stack-overflow — it
    /// rejects at the first over-cap node and never descends past it.
    /// </summary>
    public static class EffectBounds
    {
        /// <summary>DW-534 — <see cref="EffectCaps.MaxSearchRadius"/> in 16.16 form, built once from the named
        /// constant (never a bare literal) so the gate and the folded cap can never drift apart.</summary>
        private static readonly Fixed MaxSearchRadiusFixed = Fixed.FromInt(EffectCaps.MaxSearchRadius);

        /// <summary>
        /// Validate <paramref name="root"/>. A null graph is trivially valid (the executor no-ops on null).
        /// </summary>
        public static EffectBoundsResult Validate(EffectNode? root)
        {
            if (root is null)
                return EffectBoundsResult.Valid;

            // (node, frameDepth) — frameDepth = number of composition ancestors (root = 0).
            var stack = new Stack<(EffectNode Node, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                (EffectNode node, int depth) = stack.Pop();

                switch (node)
                {
                    case SequenceEffect seq:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"SequenceEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        if (seq.Children.Length > EffectCaps.MaxSequenceChildren)
                            return EffectBoundsResult.Invalid(
                                $"SequenceEffect has {seq.Children.Length} children, exceeds MaxSequenceChildren={EffectCaps.MaxSequenceChildren}.");
                        for (int k = 0; k < seq.Children.Length; k++)
                            if (seq.Children[k] is not null)
                                stack.Push((seq.Children[k], depth + 1));
                        break;

                    case SearchAreaEffect search:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"SearchAreaEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        // DW-534: the authored-radius ceiling. Reported by RAW 16.16 value (never a formatted
                        // float) so the message is deterministic and culture-independent, mirroring the
                        // AbilityValidator cooldown bound.
                        if (search.Radius > MaxSearchRadiusFixed)
                            return EffectBoundsResult.Invalid(
                                $"SearchAreaEffect radius raw {search.Radius.Raw} exceeds " +
                                $"MaxSearchRadius={EffectCaps.MaxSearchRadius} (raw {MaxSearchRadiusFixed.Raw}) — a wider " +
                                "search scans a larger share of the spatial grid on every cast, on the lockstep tick path.");
                        if (search.Child is not null)
                            stack.Push((search.Child, depth + 1));
                        break;

                    case PersistentEffect persistent:
                        if (depth >= EffectCaps.MaxEffectDepth)
                            return EffectBoundsResult.Invalid(
                                $"PersistentEffect at composition depth {depth} exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth}.");
                        if (persistent.InitialEffect is not null) stack.Push((persistent.InitialEffect, depth + 1));
                        if (persistent.PeriodEffect is not null) stack.Push((persistent.PeriodEffect, depth + 1));
                        if (persistent.ExpireEffect is not null) stack.Push((persistent.ExpireEffect, depth + 1));
                        break;

                    // Leaves contribute no depth and have no children.
                    default:
                        break;
                }
            }

            return EffectBoundsResult.Valid;
        }

        // ────────────────── DW-888: the PERIODIC-PULSE magnitude ceiling (a value bound, not a structural one) ──────────────────

        /// <summary>
        /// DW-888 — the authoring ceiling, in 16.16 RAW units, on a magnitude that a periodic pulse may MULTIPLY.
        ///
        /// <para><c>PeriodicStackMode.Multiply</c> makes a stacked modifier's period fire once at
        /// <c>magnitude × min(stacks, <see cref="EffectCaps.MaxPeriodicStackScale"/>)</c>, and
        /// <see cref="ProjectChimera.Core.Fixed"/> multiply is <c>(int)(((long)a.Raw * b.Raw) &gt;&gt; 16)</c> with NO
        /// saturation — so a period leaf whose raw magnitude exceeds this bound wraps the product NEGATIVE at the top
        /// scale, and a Multiply DoT would HEAL its victim (a HoT would damage). <c>Modifier.MaxStatDeltaTotalRaw</c>
        /// bounds a modifier's STAT DELTAS only; nothing bounded a <c>damage</c>/<c>heal</c>/<c>direct_hp_delta</c>
        /// <c>Amount</c>.</para>
        ///
        /// <para>Derived from named constants, never a bare literal (CHM0004), and by the same shape as DW-488's
        /// <c>Modifier.MaxStatDeltaTotalRaw</c>: the worst-case multiplier divides <c>int.MaxValue</c>, so
        /// <c>magnitude × MaxPeriodicStackScale ≤ int.MaxValue</c> holds by construction. ≈ 4096.0 in stat units —
        /// orders of magnitude above any shipped period leaf (the largest today is <c>furnace_pour</c>'s heal 6), so
        /// this rejects only content that was already pathological. Deliberately NOT an <see cref="EffectCaps"/>
        /// member: it is a LOAD-TIME AUTHORING bound, not a runtime execution cap, so it stays out of the
        /// <c>RulesetHash</c> wire fingerprint (adding an EffectCaps entry moves the handshake agreement hash).</para>
        /// </summary>
        public const int MaxPeriodicPulseMagnitudeRaw = int.MaxValue / EffectCaps.MaxPeriodicStackScale;

        /// <summary>
        /// DW-888 — the load-time authoring check behind <see cref="MaxPeriodicPulseMagnitudeRaw"/>, for a leaf that
        /// sits inside a PERIOD subtree (the only place <c>EffectContext.PulseScale</c> is ever &gt; 1). Returns
        /// <c>null</c> when <paramref name="node"/> is within bounds — including for every node kind that carries no
        /// scalable magnitude — or the offending <c>(field, reason)</c> pair for the caller to LOCATE into its own
        /// error shape, exactly like <c>Modifier.CheckAuthoringBounds</c>, so any validator that admits a period
        /// subtree can adopt the rule verbatim.
        /// <para>The three magnitude-carrying leaves are exactly the three that read <c>ctx.PulseScale</c>:
        /// <see cref="DamageEffect"/>, <see cref="HealEffect"/> and <see cref="DirectHpDeltaEffect"/>. Pure, never
        /// throws; allocates a string only on a REJECT.</para>
        /// </summary>
        public static (string Field, string Reason)? CheckPeriodicPulseMagnitude(EffectNode? node) => node switch
        {
            DamageEffect damage        => CheckMagnitude("amount", damage.Amount),
            HealEffect heal            => CheckMagnitude("amount", heal.Amount),
            DirectHpDeltaEffect direct => CheckMagnitude("delta",  direct.Delta),
            _                          => null,   // no scalable magnitude (composition nodes, teleport, presentation cues)
        };

        /// <summary>DW-888 helper: reject one period-leaf magnitude whose worst-case scaled product
        /// (<c>|magnitude| × <see cref="EffectCaps.MaxPeriodicStackScale"/></c>) would wrap the 16.16 int. The
        /// magnitude is long-widened first so <c>|int.MinValue|</c> is representable and the CHECK itself can never
        /// overflow.</summary>
        private static (string Field, string Reason)? CheckMagnitude(string field, Fixed magnitude)
        {
            long raw = magnitude.Raw < 0 ? -(long)magnitude.Raw : magnitude.Raw;
            if (raw > MaxPeriodicPulseMagnitudeRaw)
                return (field,
                    $"|{field}| = {raw} raw exceeds MaxPeriodicPulseMagnitudeRaw={MaxPeriodicPulseMagnitudeRaw} for a leaf " +
                    $"inside a period_effect — a periodic_stack_mode Multiply pulse scales it by up to " +
                    $"MaxPeriodicStackScale={EffectCaps.MaxPeriodicStackScale}, which wraps the 16.16 product negative " +
                    "(a scaled DoT would heal, a HoT would damage).");
            return null;
        }
    }
}
