#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-196 — the SHARED, type-agnostic reset-completeness machinery, lifted out of
    /// <see cref="EntityWorldClearCompletenessTests"/> (where DW-19 first built it for <c>EntityWorld</c> alone) so
    /// EVERY store <c>SimulationHost.ClearForReset</c> fans out over can be swept the same way. Before this lift,
    /// dropping an <c>Array.Clear</c> from <c>ProjectileStore</c>/<c>HeroStore</c>/<c>ItemStore</c> shipped GREEN
    /// through the whole suite (demonstrated by four independent mutants in the DW-196 ledger entry); now each store
    /// is driven through <see cref="AssertClearRestoresFreshState"/> by a <c>[Theory]</c> case in
    /// <c>StoreClearCompletenessTests</c>.
    ///
    /// <para><b>The sweep contract</b> (identical to DW-19's, per store): build a FRESH and a DIRTY instance that
    /// share their dependency references, dirty the dirty one across every swept field (hand-dirty the non-array
    /// state, then a reflection-driven synthetic fill over every array field), REQUIRE per-field divergence (an
    /// undirtied field would leave the sweep blind on it), run the exact reset call <c>ClearForReset</c> performs,
    /// then require the cleared instance field-equal to the fresh one. Every exemption is a named, justified
    /// allowlist entry — never a silent skip — and every allowlist name must still resolve to a real field, so a
    /// rename cannot quietly widen an exemption.</para>
    ///
    /// <para><b>Field enumeration walks the full base-type chain</b> (public AND private, per level via
    /// <see cref="BindingFlags.DeclaredOnly"/>), closing the "a base class silently shrinks the sweep" hazard the
    /// original EntityWorld test could only pin against (<c>BaseType == object</c>) rather than survive.</para>
    ///
    /// <para>Godot-free and allocation-relaxed (test-side only). Comparison handles the shapes the real stores use:
    /// arrays (any rank, jagged), <see cref="SimRng"/> (compared by <c>.State</c>), <c>List&lt;T&gt;</c>/queues
    /// (ordered structural), dictionaries (key-lookup structural), and plain scalars/references (<see cref="object.Equals(object?,object?)"/> —
    /// dependency references shared between fresh and dirty compare reference-equal; singletons like
    /// <c>Array.Empty&lt;T&gt;()</c>/<c>Snapshot.Empty</c>/<c>RegionStore.Empty</c> compare equal by identity).</para>
    /// </summary>
    public static class ClearCompletenessSweep
    {
        public const BindingFlags DeclaredInstanceFields =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // ── Field enumeration (base-chain walk) ────────────────────────────────────────────────

        /// <summary>Every instance field of <paramref name="type"/> INCLUDING private fields declared on base
        /// classes (a plain <c>GetFields(Instance)</c> misses those — the exact silent-shrink hazard DW-19's
        /// EntityWorld pin guarded against; walking the chain removes it instead).</summary>
        public static IReadOnlyList<FieldInfo> InstanceFieldsOf(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
                fields.AddRange(t.GetFields(DeclaredInstanceFields));
            return fields;
        }

        /// <summary>The swept field names of <paramref name="type"/> (all instance fields bar the allowlist).</summary>
        public static IReadOnlyList<string> SweptFieldNames(Type type, IReadOnlyCollection<string> allowlist)
        {
            var names = new List<string>();
            foreach (FieldInfo f in InstanceFieldsOf(type))
                if (!ContainsName(allowlist, f.Name)) names.Add(f.Name);
            return names;
        }

        private static bool ContainsName(IReadOnlyCollection<string> names, string name)
        {
            foreach (string n in names)
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── The comparison core (lifted verbatim from the EntityWorld sweep, plus collection handling) ──────────

        /// <summary>Reflect over every instance field of the two same-typed objects and return the names (with a
        /// short reason) of the fields on which <paramref name="actual"/> differs from <paramref name="expected"/>.</summary>
        public static IReadOnlyList<(string Field, string Detail)> DivergingFields(
            object expected, object actual, IReadOnlyCollection<string> allowlist)
        {
            Type type = expected.GetType();
            Assert.True(type == actual.GetType(),
                $"DivergingFields compared mismatched types: {type.FullName} vs {actual.GetType().FullName}.");

            var diverged = new List<(string, string)>();
            foreach (FieldInfo f in InstanceFieldsOf(type))
            {
                if (ContainsName(allowlist, f.Name)) continue; // named, justified exemptions only

                object? a = f.GetValue(expected);
                object? b = f.GetValue(actual);

                string? why = DescribeValueDivergence(a, b);
                if (why is not null) diverged.Add((f.Name, why));
            }
            return diverged;
        }

        /// <summary>Compare one value pair, dispatching on shape: <see cref="SimRng"/> by folded <c>.State</c>;
        /// arrays elementwise (any rank, recursing into jagged); strings by value; dictionaries by key-lookup;
        /// other enumerables (List/Queue/…) ordered-structurally; everything else by <see cref="object.Equals(object?,object?)"/>.</summary>
        public static string? DescribeValueDivergence(object? a, object? b)
        {
            if (a is SimRng ra && b is SimRng rb)
            {
                // Rng is a fixed reference with mutable internal state (never reassigned), so reference equality is
                // meaningless — resets RE-SEED it. Compare the folded state, which is what the checksum reads.
                return ra.State != rb.State
                    ? $".State: expected 0x{ra.State:X16}, actual 0x{rb.State:X16}"
                    : null;
            }

            if (a is Array ea && b is Array eb) return DescribeArrayDivergence(ea, eb);
            if (a is Array || b is Array) return $": expected '{a ?? "null"}', actual '{b ?? "null"}'";

            if (a is string || b is string)
                return Equals(a, b) ? null : $": expected '{a ?? "null"}', actual '{b ?? "null"}'";

            if (a is IDictionary da && b is IDictionary db) return DescribeDictionaryDivergence(da, db);
            if (a is IDictionary || b is IDictionary) return $": dictionary vs '{(a is IDictionary ? b : a) ?? "null"}'";

            // Ordered structural comparison for the remaining collections (List<T>, Queue<T>, …). Strings, arrays
            // and dictionaries are already handled above, so reaching here with IEnumerable is a real collection.
            if (a is IEnumerable ia && b is IEnumerable ib) return DescribeSequenceDivergence(ia, ib);
            if (a is IEnumerable || b is IEnumerable) return $": expected '{a ?? "null"}', actual '{b ?? "null"}'";

            return Equals(a, b) ? null : $": expected '{a ?? "null"}', actual '{b ?? "null"}'";
        }

        /// <summary>Null-safe elementwise array comparison; returns a located description of the FIRST differing
        /// element (or a rank/length mismatch), or null when equal. Handles multidimensional arrays (flat
        /// enumeration — <c>Array.GetValue(int)</c> throws on rank &gt; 1) and jagged arrays (recursion — inner
        /// arrays would otherwise reference-compare).</summary>
        public static string? DescribeArrayDivergence(Array expected, Array actual)
        {
            if (expected.Rank != actual.Rank)
                return $": rank {expected.Rank} vs {actual.Rank}";

            if (expected.Length != actual.Length)
                return $": length {expected.Length} vs {actual.Length}";

            if (expected.Rank > 1)
            {
                for (int d = 0; d < expected.Rank; d++)
                    if (expected.GetLength(d) != actual.GetLength(d))
                        return $": dimension {d} length {expected.GetLength(d)} vs {actual.GetLength(d)}";

                IEnumerator ae = expected.GetEnumerator(), be = actual.GetEnumerator();
                int flat = 0;
                while (ae.MoveNext() && be.MoveNext())
                {
                    string? why = DescribeElementDivergence(ae.Current, be.Current);
                    if (why is not null) return $"[flat {flat}]{why}";
                    flat++;
                }
                return null;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                string? why = DescribeElementDivergence(expected.GetValue(i), actual.GetValue(i));
                if (why is not null) return $"[{i}]{why}";
            }

            return null;
        }

        /// <summary>Compare one array element, recursing when the element is itself an <see cref="Array"/>.</summary>
        private static string? DescribeElementDivergence(object? a, object? b)
        {
            if (a is Array ia && b is Array ib) return DescribeArrayDivergence(ia, ib);
            if (a is Array || b is Array) return $": expected '{a ?? "null"}', actual '{b ?? "null"}'";
            return Equals(a, b) ? null : $": expected '{a ?? "null"}', actual '{b ?? "null"}'";
        }

        /// <summary>Structural dictionary comparison: counts, then every expected key present in actual with an
        /// equal (recursively compared) value. Key-lookup based, so enumeration order never matters.</summary>
        private static string? DescribeDictionaryDivergence(IDictionary expected, IDictionary actual)
        {
            if (expected.Count != actual.Count)
                return $": dictionary count {expected.Count} vs {actual.Count}";
            foreach (DictionaryEntry e in expected)
            {
                if (!actual.Contains(e.Key))
                    return $": actual is missing key '{e.Key}'";
                string? why = DescribeValueDivergence(e.Value, actual[e.Key]);
                if (why is not null) return $"[{e.Key}]{why}";
            }
            return null;
        }

        /// <summary>Ordered structural sequence comparison (List, Queue, …): lengths then per-index elements.</summary>
        private static string? DescribeSequenceDivergence(IEnumerable expected, IEnumerable actual)
        {
            IEnumerator ae = expected.GetEnumerator(), be = actual.GetEnumerator();
            int i = 0;
            while (true)
            {
                bool an = ae.MoveNext(), bn = be.MoveNext();
                if (an != bn) return $": element count diverges at index {i} (one sequence ended)";
                if (!an) return null;
                string? why = DescribeValueDivergence(ae.Current, be.Current);
                if (why is not null) return $"[{i}]{why}";
                i++;
            }
        }

        // ── The synthetic array fill (lifted from the EntityWorld sweep; jagged arrays fill IN PLACE) ──────────

        /// <summary>
        /// Write a non-default value into every element of every swept array field of <paramref name="target"/>.
        /// Dispatches on the array's ELEMENT type via <see cref="NonDefaultValue"/> (plus the fixture's optional
        /// <paramref name="elementFiller"/>); an element type nobody has taught it about THROWS rather than being
        /// silently skipped. A JAGGED array (element type itself an array) is filled IN PLACE — each non-null inner
        /// array's elements are overwritten, null slots are left null — because several stores (ResearchStore,
        /// DslVarTable) grow inner arrays that their Clear() zeroes WITHOUT replacing; substituting fresh inner
        /// arrays here would fabricate a length divergence no production path can produce. An all-null jagged field
        /// therefore stays undirtied and trips the per-field precondition, forcing the fixture to hand-seed it.
        /// </summary>
        public static void SyntheticallyFillArrays(object target, IReadOnlyCollection<string> allowlist,
                                                   Func<Type, string, object?>? elementFiller = null)
        {
            Type type = target.GetType();
            foreach (FieldInfo f in InstanceFieldsOf(type))
            {
                if (ContainsName(allowlist, f.Name)) continue;
                if (f.GetValue(target) is not Array arr) continue;
                FillArrayInPlace(arr, type.Name, f.Name, elementFiller);
            }
        }

        private static void FillArrayInPlace(Array arr, string typeName, string fieldName,
                                             Func<Type, string, object?>? elementFiller)
        {
            // DescribeArrayDivergence deliberately handles rank > 1; this fill does not (Array.SetValue(object,int)
            // throws on a multidimensional array). Fail with the instruction rather than a bare reflection error.
            if (arr.Rank > 1)
                throw new InvalidOperationException(
                    $"{typeName}.{fieldName} is a rank-{arr.Rank} array. The comparison half of this sweep already " +
                    "supports multidimensional arrays, but the synthetic fill does not — teach FillArrayInPlace() " +
                    "to walk it, or the field enters the sweep undirtied and a Clear() that omits it passes GREEN.");

            Type et = arr.GetType().GetElementType()!;
            if (et.IsArray)
            {
                // Jagged: fill inner arrays IN PLACE (see the method doc). Null slots stay null.
                for (int i = 0; i < arr.Length; i++)
                    if (arr.GetValue(i) is Array inner)
                        FillArrayInPlace(inner, typeName, $"{fieldName}[{i}]", elementFiller);
                return;
            }

            object filler = NonDefaultValue(et, typeName, fieldName, elementFiller);
            for (int i = 0; i < arr.Length; i++) arr.SetValue(filler, i);
        }

        /// <summary>A value guaranteed != <c>default(<paramref name="elementType"/>)</c>, for the synthetic fill.
        /// The fixture's <paramref name="elementFiller"/> is consulted FIRST so a store can supply instances of
        /// types with no parameterless ctor (e.g. <c>Modifier</c>/<c>PersistentEffect</c>).</summary>
        public static object NonDefaultValue(Type elementType, string typeName, string fieldName,
                                             Func<Type, string, object?>? elementFiller = null)
        {
            object? custom = elementFiller?.Invoke(elementType, fieldName);
            if (custom is not null) return custom;

            if (elementType == typeof(bool)) return true;

            if (elementType.IsEnum) return Enum.ToObject(elementType, 1);

            if (elementType == typeof(byte) || elementType == typeof(sbyte) ||
                elementType == typeof(short) || elementType == typeof(ushort) ||
                elementType == typeof(int) || elementType == typeof(uint) ||
                elementType == typeof(long) || elementType == typeof(ulong))
                return Convert.ChangeType(7, elementType);

            if (elementType == typeof(Fixed)) return Fixed.FromInt(7);

            if (elementType == typeof(FixedVec3))
                return new FixedVec3(Fixed.FromInt(7), Fixed.FromInt(7), Fixed.FromInt(7));

            if (elementType == typeof(HeroId)) return new HeroId(7); // the cross-match hero identity (HeroStore.Id)

            if (elementType == typeof(string)) return "dirty";

            if (!elementType.IsValueType && !elementType.IsAbstract && !elementType.IsArray)
            {
                // Activator.CreateInstance THROWS (MissingMethodException) for a reference type with no public
                // parameterless ctor — catch it and fall through to the explicit instruction instead.
                try
                {
                    object? instance = Activator.CreateInstance(elementType);
                    if (instance is not null) return instance;
                }
                catch (MissingMemberException) { /* fall through to the guiding throw */ }
            }

            throw new InvalidOperationException(
                $"{typeName}.{fieldName} is an array of '{elementType.FullName}', a type the Clear-completeness " +
                "synthetic fill does not know how to dirty. Teach NonDefaultValue() (or the fixture's elementFiller) " +
                "about it — leaving it unhandled would silently exempt the field from the sweep, which is the exact " +
                "blind spot DW-19/DW-196 closed.");
        }

        // ── Fixture-side reflection poke (for private per-match state with no public dirty path) ───────────────

        /// <summary>
        /// Set a private instance field (or an auto-property's backing field, by property name) on
        /// <paramref name="target"/>. LOUD on a miss — a renamed field turns the poke into a test failure naming
        /// the store, never a silently-undirtied sweep. Used by fixtures whose per-match state has no public write
        /// path (e.g. <c>WinConditionSystem</c>'s apply-time config, <c>SimulationLoop.LastChecksum</c>).
        /// </summary>
        public static void Poke(object target, string fieldOrPropertyName, object? value)
        {
            Type type = target.GetType();
            FieldInfo? f = FindField(type, fieldOrPropertyName)
                        ?? FindField(type, $"<{fieldOrPropertyName}>k__BackingField");
            Assert.True(f is not null,
                $"{type.Name} no longer has a field (or auto-property backing field) named '{fieldOrPropertyName}' — " +
                "update the store's reset-completeness fixture to dirty whatever replaced it.");
            f!.SetValue(target, value);
        }

        /// <summary>Read a private instance field (fixture-side, e.g. to mutate a private <c>List&lt;int&gt;</c>
        /// in place). LOUD on a miss, like <see cref="Poke"/>.</summary>
        public static T GetPrivate<T>(object target, string fieldName)
        {
            Type type = target.GetType();
            FieldInfo? f = FindField(type, fieldName) ?? FindField(type, $"<{fieldName}>k__BackingField");
            Assert.True(f is not null,
                $"{type.Name} no longer has a field (or auto-property backing field) named '{fieldName}' — " +
                "update the store's reset-completeness fixture.");
            return (T)f!.GetValue(target)!;
        }

        /// <summary>The compiler-generated backing-field name of an auto-property — for allowlist entries that
        /// exempt an auto-property (e.g. <c>SimulationLoop.ChecksumInterval</c>, host config a reset preserves).</summary>
        public static string BackingField(string propertyName) => $"<{propertyName}>k__BackingField";

        private static FieldInfo? FindField(Type type, string name)
        {
            for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo? f = t.GetField(name, DeclaredInstanceFields);
                if (f is not null) return f;
            }
            return null;
        }

        // ── The full sweep drive (the DW-19 three-phase assertion, shared by every Theory case) ────────────────

        /// <summary>
        /// Drive the complete reset-completeness sweep for one store fixture:
        /// <list type="number">
        /// <item>every allowlist entry must resolve to a real field (a rename cannot quietly widen an exemption);</item>
        /// <item>hand-dirty + synthetic-fill the dirty instance, then REQUIRE every swept field to diverge from
        ///   fresh (an undirtied field is a blind spot: a Clear() that omits it would pass GREEN);</item>
        /// <item>run the store's exact reset call and REQUIRE the cleared instance field-equal to fresh.</item>
        /// </list>
        /// </summary>
        public static void AssertClearRestoresFreshState(StoreResetFixture fx)
        {
            Type type = fx.Fresh.GetType();

            // 1. Allowlist entries must keep naming REAL fields (the EntityWorld allowlist-rename guard, generalized).
            foreach (string name in fx.Allowlist)
                Assert.True(FindField(type, name) is not null,
                    $"{type.Name} reset-sweep allowlist entry '{name}' no longer resolves to an instance field. " +
                    "Reconcile the allowlist with the store deliberately — a dead entry hides nothing but suggests " +
                    "the exemption's justification no longer applies.");

            // 2. Dirty, then demand per-field divergence — PER FIELD, not globally (a global "something diverged"
            //    check is nearly free to satisfy and leaves the sweep blind on every field the fixture never moved).
            fx.DirtyNonArrayState?.Invoke();
            SyntheticallyFillArrays(fx.Dirty, fx.Allowlist, fx.ElementFiller);
            fx.NormalizeFresh?.Invoke();

            var dirtiedFields = new HashSet<string>();
            foreach ((string field, string _) in DivergingFields(fx.Fresh, fx.Dirty, fx.Allowlist))
                dirtiedFields.Add(field);

            var undirtied = new List<string>();
            foreach (string name in SweptFieldNames(type, fx.Allowlist))
                if (!dirtiedFields.Contains(name)) undirtied.Add(name);
            undirtied.Sort(StringComparer.Ordinal);

            Assert.True(undirtied.Count == 0,
                $"Precondition failed: the '{fx.Name}' fixture left these swept {type.Name} fields at their FRESH " +
                "value, so the sweep is blind on them — a reset that omits any of them would still pass GREEN:\n  " +
                string.Join("\n  ", undirtied) +
                "\nExtend the fixture to move each of them off its freshly-constructed value (array fields are " +
                "filled automatically by the reflection-driven synthetic fill; a non-array field needs an explicit " +
                "write, e.g. via ClearCompletenessSweep.Poke) — or add a JUSTIFIED allowlist entry if the field is " +
                "genuinely construction-lifetime state the reset deliberately preserves.");

            // 3. The reset, then fresh == cleared over every swept field.
            fx.Clear();

            IReadOnlyList<(string Field, string Detail)> after = DivergingFields(fx.Fresh, fx.Dirty, fx.Allowlist);
            var afterText = new List<string>();
            foreach ((string field, string detail) in after) afterText.Add($"{type.Name}.{field}{detail}");
            Assert.True(after.Count == 0,
                $"{type.Name}'s reset ('{fx.Name}') left these fields diverging from a freshly-constructed " +
                "instance, i.e. a re-Play does NOT start this store from the same state as a fresh boot. For any " +
                "field folded into SimChecksum that is a determinism/desync leak; for unfolded fields it is a " +
                "state-cleanliness leak carried across the Edit↔Play reset. Either way the reset must restore it:\n  " +
                string.Join("\n  ", afterText));
        }
    }

    /// <summary>
    /// One store's reset-completeness sweep case: a FRESH and a DIRTY instance built over SHARED dependency
    /// references (so dependency fields compare reference-equal), the hand-dirty step for non-array state, the
    /// exact reset call <c>SimulationHost.ClearForReset</c> performs for this store, and the store's justified
    /// allowlist. Built lazily per test run via <c>StoreClearCompletenessTests</c>' case table.
    /// </summary>
    public sealed class StoreResetFixture
    {
        /// <summary>Display name (the ClearForReset line this case guards, e.g. "Projectiles.Clear()").</summary>
        public string Name { get; }
        /// <summary>The freshly-constructed reference instance (never dirtied, never cleared).</summary>
        public object Fresh { get; }
        /// <summary>The instance that is dirtied, then reset, then compared against <see cref="Fresh"/>.</summary>
        public object Dirty { get; }
        /// <summary>Hand-dirty step for non-array state (counts, scalars, lists, config), run BEFORE the synthetic
        /// array fill so the fill can overwrite/extend what it seeded. Null when arrays are the whole store.</summary>
        public Action? DirtyNonArrayState { get; init; }
        /// <summary>The exact reset call ClearForReset performs for this store (e.g. <c>Clear()</c>,
        /// <c>Reset()</c>, <c>ResetForMatch()</c>, <c>ResetConfig()</c>, <c>ResetTick()</c>) against <see cref="Dirty"/>.</summary>
        public Action Clear { get; }
        /// <summary>Justified field exemptions (dependency refs, construction-lifetime config, documented
        /// count-gated stale tails). Every entry must resolve to a real field.</summary>
        public string[] Allowlist { get; init; } = Array.Empty<string>();
        /// <summary>Optional per-store element filler for array element types the shared
        /// <see cref="ClearCompletenessSweep.NonDefaultValue"/> cannot construct.</summary>
        public Func<Type, string, object?>? ElementFiller { get; init; }
        /// <summary>Optional post-dirty normalization of <see cref="Fresh"/> — e.g. growing a fresh
        /// <c>ResearchStore</c> to the dirty instance's never-shrinking inner-array capacity, mirroring
        /// <c>SimResetTests.ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed</c>'s EnsureCapacity call.</summary>
        public Action? NormalizeFresh { get; init; }

        public StoreResetFixture(string name, object fresh, object dirty, Action clear)
        {
            Name = name;
            Fresh = fresh;
            Dirty = dirty;
            Clear = clear;
        }

        public override string ToString() => Name;
    }
}
