#nullable enable
using ProjectChimera.Core;     // Fixed
using ProjectChimera.Effects;  // Modifier, StatusFlags, EffectNode leaves

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.5 (FR-74) — the Godot-free classifier that derives a buff/debuff <see cref="Polarity"/> and a display
    /// glyph for a stat/DoT <see cref="Modifier"/> icon, WITHOUT adding any authored icon/polarity field to
    /// <c>Modifier</c> (that would touch the content loader/whitelist — forbidden by the story). Polarity is inferred
    /// from the modifier's own stat deltas, status flags, AND — for a pure periodic (DoT/HoT) modifier — the sign of
    /// its period effect (damage → debuff, heal → buff).
    ///
    /// <para>Godot-free (Core.Fixed + Effects only) so it compiles into the Tier-1 test assembly and its policy is
    /// unit-testable directly. Purely presentation — never enters any determinism hash.</para>
    /// </summary>
    public static class ModifierPolarity
    {
        /// <summary>Buff/debuff/neutral classification of a modifier's net effect on its host.</summary>
        public enum Polarity : byte
        {
            Neutral = 0,
            Buff = 1,
            Debuff = 2,
        }

        /// <summary>The harmful status flags (imposed ON an enemy). <see cref="StatusFlags.Invulnerable"/> is a BUFF,
        /// so it is excluded from this set.
        /// <para>DW-618: this used to be a private copy of the partition. It now ALIASES
        /// <see cref="StatusPolarity.Harmful"/>, the canonical set hosted beside the enum — because
        /// <c>AbilityValidator</c>'s Ally-filtered-harmful-grant lint needs the same classification and a sim-layer
        /// content gate must not depend on this presentation namespace. Behaviour is unchanged (identical flags); the
        /// point is that a future flag can only be classified ONCE.</para></summary>
        private const StatusFlags HarmfulStatus = StatusPolarity.Harmful;

        /// <summary>
        /// Classify <paramref name="mod"/> as a <see cref="Polarity.Buff"/>, <see cref="Polarity.Debuff"/>, or
        /// <see cref="Polarity.Neutral"/>. Precedence: a harmful status flag ⇒ Debuff; a beneficial status
        /// (<see cref="StatusFlags.Invulnerable"/>) ⇒ Buff; a net-negative stat delta ⇒ Debuff; a net-positive ⇒ Buff;
        /// otherwise (no deltas, no status) the sign of a periodic (DoT/HoT) effect decides — damage ⇒ Debuff,
        /// heal ⇒ Buff. Story 15-24a: the net stat delta is the sum over the WHOLE canonical sparse vector
        /// (every registry stat is bigger-is-better today, so a raw signed sum keeps the pre-15-24a semantics
        /// exactly for legacy content and classifies new-stat-only modifiers instead of dropping them to
        /// Neutral; the sum was always unit-mixed — it is a SIGN vote, not a magnitude).
        /// </summary>
        public static Polarity Classify(Modifier mod)
        {
            if (mod == null) return Polarity.Neutral;

            // Harmful status dominates — a stunning modifier is a debuff even if it also grants a stat.
            if ((mod.Status & HarmfulStatus) != 0) return Polarity.Debuff;
            // Beneficial status (Invulnerable) dominates the net-negative check, symmetric to HarmfulStatus above.
            if ((mod.Status & StatusPolarity.Beneficial) != 0) return Polarity.Buff;

            Fixed net = Fixed.Zero;
            for (int i = 0; i < mod.StatDeltas.Length; i++) net += mod.StatDeltas[i].Delta;
            if (net < Fixed.Zero) return Polarity.Debuff;
            if (net > Fixed.Zero) return Polarity.Buff;

            // No stat delta / status → a pure periodic modifier's polarity is its DoT/HoT sign.
            if (HasPeriod(mod))
            {
                int sign = PeriodSign(mod.PeriodEffect);
                if (sign < 0) return Polarity.Debuff;
                if (sign > 0) return Polarity.Buff;
            }
            return Polarity.Neutral;
        }

        /// <summary>True when a modifier has a periodic (DoT/HoT) effect — used to pick a distinct glyph.</summary>
        public static bool HasPeriod(Modifier mod) => mod != null && mod.PeriodEffect != null && mod.PeriodTicks > 0;

        /// <summary>
        /// A display glyph for a stat/DoT modifier icon. Deliberately restricted to glyphs the project theme font
        /// (Space Grotesk / JetBrains Mono / Chakra Petch) actually renders — verified in-engine via <c>Font.has_char</c>:
        /// the geometric shapes ▲/▼/◆ (U+25B2/25BC/25C6) are ABSENT and would render as tofu, so the up/down/neutral
        /// markers use the arrows ↑/↓ (U+2191/2193) and bullet • (U+2022), which the font provides. A periodic effect
        /// shows the ASCII "DoT"/"HoT" keyed to its polarity. Purely a display helper.
        /// </summary>
        public static string Glyph(Modifier mod)
        {
            if (mod == null) return "?";
            Polarity p = Classify(mod);
            if (HasPeriod(mod))
            {
                // Derive the DoT/HoT glyph from the actual period sign — never assume beneficial (review #1).
                // An indeterminate / net-neutral periodic (sign == 0) shows the neutral bullet, matching Classify's
                // Neutral verdict and PolarityTint's grey, rather than falsely reading as a green "HoT" heal.
                int sign = PeriodSign(mod.PeriodEffect);
                return sign < 0 ? "DoT" : sign > 0 ? "HoT" : "•";
            }
            return p switch
            {
                Polarity.Buff => "↑",   // U+2191 (font-verified)
                Polarity.Debuff => "↓", // U+2193 (font-verified)
                _ => "•",               // U+2022 (font-verified)
            };
        }

        /// <summary>
        /// The polarity SIGN of a period/effect node: -1 when it damages the host (DoT), +1 when it heals (HoT), 0 when
        /// indeterminate. Walks the Godot-free Effects graph: <see cref="DamageEffect"/> ⇒ -1; <see cref="HealEffect"/>
        /// ⇒ +1; <see cref="DirectHpDeltaEffect"/> ⇒ the sign of its delta; a <see cref="SequenceEffect"/> sums its
        /// children; a <see cref="SearchAreaEffect"/> descends into its child. Depth-bounded against a pathological graph.
        /// </summary>
        public static int PeriodSign(EffectNode? node) => PeriodSign(node, 0);

        private static int PeriodSign(EffectNode? node, int depth)
        {
            if (node == null || depth > 8) return 0;
            switch (node)
            {
                case DamageEffect: return -1;
                case HealEffect: return +1;
                case DirectHpDeltaEffect d:
                    return d.Delta < Fixed.Zero ? -1 : d.Delta > Fixed.Zero ? +1 : 0;
                case SequenceEffect seq:
                {
                    int sum = 0;
                    for (int i = 0; i < seq.Children.Length; i++) sum += PeriodSign(seq.Children[i], depth + 1);
                    return System.Math.Sign(sum);
                }
                case SearchAreaEffect area:
                    return PeriodSign(area.Child, depth + 1);
                default:
                    return 0;
            }
        }
    }
}
