#nullable enable
using System;
using System.Reflection;
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
        /// safe on any FromJson-parsed graph. Moved verbatim from <see cref="CanonicalModelHash"/> (v8).
        ///
        /// <para><b>DW-449.</b> A kind WITHOUT an explicit arm below no longer folds value-blind (the old default arm
        /// mixed only <c>GetType().Name</c>): it takes the FULL-FIELD reflection fold in
        /// <see cref="MixUnknownEffect"/>, and a member type that fold cannot make value-visible FAILS CLOSED
        /// (<see cref="NotSupportedException"/>) instead of hashing blind. The seven shipped kinds all have explicit
        /// arms, so this path is dead for existing content — no golden/hash moves. EffectFoldCompletenessTests guards
        /// that every shipped kind keeps an explicit arm and every field of each kind stays consciously folded.</para></summary>
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
                    // DW-449: full-field fold — value-visible for a future kind, never the old name-only fold.
                    h = MixUnknownEffect(h, e);
                    break;
            }
            return h;
        }

        /// <summary>
        /// DW-449 — the FULL-FIELD fold for an effect kind that has no explicit arm in <see cref="MixEffect"/>.
        /// The old default arm folded only <c>GetType().Name</c> (value-blind), so a brand-new effect kind's payload
        /// could change without moving the handshake hash — two peers with divergent modded effect payloads would
        /// pass the lobby gate and desync mid-match. This walk folds the kind name PLUS every public readable member
        /// (fields and non-indexer properties, inherited included), each by VALUE using the family's conventions.
        ///
        /// <para><b>Determinism:</b> reflection member-enumeration order is UNSPECIFIED, so members are sorted on the
        /// composite ordinal key (member name, then declaring type) — total and duplicate-free even under member
        /// hiding — via an in-place insertion sort (no comparerless <c>Array.Sort</c> in sim code, CHM0003; the list
        /// is tiny and this path is cold — it can only run for a not-yet-shipped kind).</para>
        ///
        /// <para><b>Fail-closed:</b> a member whose type cannot be folded by value throws
        /// <see cref="NotSupportedException"/> — a loud error at hash time beats a silent value-blind hash that
        /// desyncs mid-match (the DW-449 hole). Every member type expressible under the vocabulary's closedness
        /// contract (no float/double/object/delegate/Godot) folds fine: <c>Fixed</c>/<c>FixedVec3</c>, integrals,
        /// bool, string, enums, <see cref="ProjectChimera.Effects.EffectNode"/>/<see cref="ProjectChimera.Effects.Modifier"/>
        /// children, and arrays thereof.</para>
        /// </summary>
        private static ulong MixUnknownEffect(ulong h, ProjectChimera.Effects.EffectNode e)
        {
            Type t = e.GetType();
            h = MixStr(h, t.Name); // the kind discriminator — the old default arm's (only) component, kept first

            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (PropertyInfo p in props)
            {
                if (p.GetIndexParameters().Length != 0 || !p.CanRead)
                    throw new NotSupportedException(
                        $"CanonicalFold.MixEffect: effect kind '{t.Name}' has a non-readable or indexer property " +
                        $"('{p.Name}') whose state this fold cannot make value-visible. Add an explicit arm to " +
                        "CanonicalFold.MixEffect for this kind (DW-449 fail-closed; see EffectFoldCompletenessTests).");
            }

            int n = fields.Length + props.Length;
            string[] keys = new string[n];
            object?[] values = new object?[n];
            int w = 0;
            foreach (FieldInfo f in fields)
            {
                keys[w] = f.Name + " " + (f.DeclaringType?.FullName ?? string.Empty);
                values[w] = f.GetValue(e);
                w++;
            }
            foreach (PropertyInfo p in props)
            {
                keys[w] = p.Name + " " + (p.DeclaringType?.FullName ?? string.Empty);
                values[w] = p.GetValue(e);
                w++;
            }

            // In-place insertion sort on the composite ordinal key (unique ⇒ the order is fully deterministic
            // regardless of the runtime's reflection enumeration order).
            for (int i = 1; i < n; i++)
            {
                string k = keys[i];
                object? v = values[i];
                int j = i - 1;
                while (j >= 0 && string.CompareOrdinal(keys[j], k) > 0)
                {
                    keys[j + 1] = keys[j];
                    values[j + 1] = values[j];
                    j--;
                }
                keys[j + 1] = k;
                values[j + 1] = v;
            }

            h = MixInt(h, n); // member count — an added/removed member always moves the hash
            for (int i = 0; i < n; i++)
            {
                h = MixStr(h, keys[i]); // name + declaring type, so a hidden (`new`) member cannot alias its base
                h = MixMemberValue(h, values[i], t.Name);
            }
            return h;
        }

        /// <summary>One unknown-kind member VALUE, folded by runtime type (DW-449). Effect/Modifier children delegate
        /// to the shared walks (which own their 0/1 null markers); any other null ⇒ a 0 marker and any other present
        /// value ⇒ a 1 marker + value fold, so null can never alias a zero/default value. An unfoldable member type
        /// fails closed (<see cref="NotSupportedException"/>) instead of hashing value-blind.</summary>
        private static ulong MixMemberValue(ulong h, object? v, string owningKind)
        {
            // Children first: the shared walks fold their own null/present markers, so a child folds byte-identically
            // here and in an explicit arm.
            if (v is ProjectChimera.Effects.EffectNode child) return MixEffect(h, child);
            if (v is ProjectChimera.Effects.Modifier mod) return MixModifier(h, mod);
            if (v is null) return MixInt(h, 0);

            h = MixInt(h, 1); // present marker
            switch (v)
            {
                case ProjectChimera.Core.Fixed fx: return MixInt(h, fx.Raw);
                case ProjectChimera.Core.FixedVec3 v3:
                    h = MixInt(h, v3.X.Raw);
                    h = MixInt(h, v3.Y.Raw);
                    return MixInt(h, v3.Z.Raw);
                case bool b: return MixInt(h, b ? 1 : 0);
                case Enum en: return MixStr(h, en.ToString()); // family convention: enums fold by NAME
                case string s: return MixStr(h, s);
                case int i: return MixInt(h, i);
                case uint ui: return MixInt(h, unchecked((int)ui));
                case long l: return MixULong(h, unchecked((ulong)l));
                case ulong ul: return MixULong(h, ul);
                case short sh: return MixInt(h, sh);
                case ushort us: return MixInt(h, us);
                case byte by: return MixInt(h, by);
                case sbyte sb: return MixInt(h, sb);
                case char c: return MixInt(h, c);
                case Array arr:
                    h = MixInt(h, arr.Length); // length prefix (the SequenceEffect convention)
                    foreach (object? el in arr) h = MixMemberValue(h, el, owningKind);
                    return h;
                default:
                    throw new NotSupportedException(
                        $"CanonicalFold.MixEffect: effect kind '{owningKind}' carries a member of type " +
                        $"'{v.GetType().FullName}' this fold cannot make value-visible. Add an explicit arm to " +
                        "CanonicalFold.MixEffect for the kind (see EffectFoldCompletenessTests) — a value-blind fold " +
                        "would reopen the DW-449 desync hole, so this fails closed.");
            }
        }

        /// <summary>An <c>apply_modifier</c> payload (v8): every semantic Modifier field in fixed order. Moved verbatim
        /// from <see cref="CanonicalModelHash"/>. v15 (DW-272 / Story 15.12) appends <see cref="ProjectChimera.Effects.Modifier.PeriodicStacking"/>
        /// by NAME (the StackRule convention) — unconditionally, so it also moves <c>ContentHash</c> for any ability that
        /// authors an apply_modifier.</summary>
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
            h = MixStr(h, m.PeriodicStacking.ToString()); // DW-272 / Story 15.12 — folded by NAME (the StackRule convention); AlgoVersion 14→15
            return h;
        }
    }
}
