#nullable enable
using System;
using System.Text;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.16 — the SHARED canonical-fold primitives + the typed effect-tree walk, extracted verbatim from
    /// <see cref="CanonicalModelHash"/> so <see cref="CanonicalModelHash"/> (DSL <c>run_effect</c> embeds) and
    /// <see cref="ContentHash"/> (ability/item <c>EffectGraph</c>s) fold an effect subgraph BYTE-IDENTICALLY —
    /// one implementation, no second copy to drift. The FNV-64 <see cref="MixInt"/>/<see cref="MixStr"/>/
    /// <see cref="MixULong"/> primitives are the exact same little-endian folds the whole hash family uses.
    ///
    /// <para><b>Behavior-preserving extraction.</b> The <see cref="MixEffect"/>/<see cref="MixModifier"/> bodies
    /// moved here unchanged from <see cref="CanonicalModelHash"/>; its goldens (<c>hero-start-state.golden.txt</c>
    /// and the effect-fold tests) guard that the output stays identical. <c>Fixed</c> folds via <c>.Raw</c>, enums
    /// by <c>.ToString()</c> NAME (except the <c>[Flags]</c> Status ordinal, documented at the call site), null
    /// children as a <c>0</c> marker, present nodes with a leading <c>1</c> marker.</para>
    ///
    /// Godot-free (src/Core/Definitions) — int/ulong/<c>Fixed.Raw</c> only (analyzer-clean, no <c>float</c> in the
    /// fold, no <c>Dictionary</c>/<c>DateTime</c>/<c>Random</c>).
    /// </summary>
    internal static class CanonicalFold
    {
        internal const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis
        internal const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>FNV-64 fold of a 32-bit int as 4 little-endian bytes (the family primitive).</summary>
        internal static ulong MixInt(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }

        /// <summary>FNV-64 fold of a 64-bit value as low-32 THEN high-32 (two <see cref="MixInt"/> folds).</summary>
        internal static ulong MixULong(ulong h, ulong value)
        {
            h = MixInt(h, (int)(value & 0xFFFFFFFFUL)); // low 32 bits
            h = MixInt(h, (int)(value >> 32));          // high 32 bits
            return h;
        }

        /// <summary>
        /// FNV-64 fold of a string: a length prefix (so "ab"+"c" != "a"+"bc", and null != "") followed by the
        /// UTF-8 bytes. Null length is folded as -1.
        /// </summary>
        internal static ulong MixStr(ulong h, string? s)
        {
            h = MixInt(h, s?.Length ?? -1);
            if (s == null) return h;
            foreach (byte by in Encoding.UTF8.GetBytes(s))
            {
                h ^= by;
                h *= Prime;
            }
            return h;
        }

        /// <summary>The typed effect-tree walk (kind string + semantic fields; <c>Fixed</c> via <c>.Raw</c>; enums as
        /// names) — never serialized bytes. Null child ⇒ a 0 marker; a present node ⇒ a 1 marker first (so absent vs
        /// default-valued children cannot alias). Depth is bounded by the JSON parser's MaxDepth, so the recursion is
        /// safe on any FromJson-parsed graph. Moved verbatim from <see cref="CanonicalModelHash"/> (v8).</summary>
        internal static ulong MixEffect(ulong h, ProjectChimera.Effects.EffectNode? e)
        {
            if (e is null) return MixInt(h, 0);
            h = MixInt(h, 1);
            switch (e)
            {
                case ProjectChimera.Effects.DirectHpDeltaEffect d:
                    h = MixStr(h, "direct_hp_delta");
                    h = MixInt(h, d.Delta.Raw);
                    h = MixStr(h, d.RequireTag.ToString());
                    break;
                case ProjectChimera.Effects.HealEffect he:
                    h = MixStr(h, "heal");
                    h = MixInt(h, he.Amount.Raw);
                    h = MixStr(h, he.RequireTag.ToString());
                    break;
                case ProjectChimera.Effects.DamageEffect dm:
                    h = MixStr(h, "damage");
                    h = MixInt(h, dm.Amount.Raw);
                    h = MixStr(h, dm.Type.ToString());
                    h = MixStr(h, dm.RequireTag.ToString());
                    break;
                case ProjectChimera.Effects.ApplyModifierEffect am:
                    h = MixStr(h, "apply_modifier");
                    h = MixModifier(h, am.Modifier);
                    h = MixStr(h, am.RequireTag.ToString());
                    break;
                case ProjectChimera.Effects.SequenceEffect s:
                    h = MixStr(h, "sequence");
                    h = MixInt(h, s.Children?.Length ?? 0);
                    foreach (ProjectChimera.Effects.EffectNode? child in s.Children ?? Array.Empty<ProjectChimera.Effects.EffectNode>())
                        h = MixEffect(h, child);
                    break;
                case ProjectChimera.Effects.SearchAreaEffect sa:
                    h = MixStr(h, "search_area");
                    h = MixInt(h, sa.Radius.Raw);
                    h = MixStr(h, sa.Filter.ToString());
                    h = MixStr(h, sa.RequireTag.ToString());
                    h = MixEffect(h, sa.Child);
                    break;
                case ProjectChimera.Effects.PersistentEffect p:
                    h = MixStr(h, "persistent");
                    h = MixEffect(h, p.InitialEffect);
                    h = MixEffect(h, p.PeriodEffect);
                    h = MixEffect(h, p.ExpireEffect);
                    h = MixInt(h, p.PeriodTicks);
                    h = MixInt(h, p.PeriodCount);
                    h = MixInt(h, p.Lifelong ? 1 : 0);
                    break;
                default:
                    h = MixStr(h, e.GetType().Name); // total/never-throw for a future kind
                    break;
            }
            return h;
        }

        /// <summary>An <c>apply_modifier</c> payload (v8): every semantic Modifier field in fixed order. Moved verbatim
        /// from <see cref="CanonicalModelHash"/>.</summary>
        internal static ulong MixModifier(ulong h, ProjectChimera.Effects.Modifier? m)
        {
            if (m is null) return MixInt(h, 0);
            h = MixInt(h, 1);
            h = MixInt(h, m.Id);
            h = MixInt(h, m.DurationTicks);
            h = MixStr(h, m.Stacking.ToString());
            h = MixInt(h, m.MaxStacks);
            h = MixInt(h, m.MaxHealthDelta.Raw);
            h = MixInt(h, m.AttackDamageDelta.Raw);
            h = MixInt(h, m.MoveSpeedDelta.Raw);
            h = MixInt(h, m.ArmorDelta.Raw);
            h = MixInt(h, (int)m.Status); // deliberate ordinal fold: Status is a [Flags] enum — a combined value has no single NAME, and the bit layout is append-only/stable ("fixing" this to a name fold would churn the hash)
            h = MixEffect(h, m.PeriodEffect);
            h = MixInt(h, m.PeriodTicks);
            return h;
        }
    }
}
