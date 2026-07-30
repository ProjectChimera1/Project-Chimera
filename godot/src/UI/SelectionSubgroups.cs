#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.5 (FR-74) — the Godot-free, pure grouping policy behind the WC3 multi-select subgroup view.
    /// Partitions a flat selection of entity ids into ordered <see cref="Subgroup"/>s by a caller-supplied per-id
    /// grouping key (the presentation layer keys by <c>SourceDefinition</c>, falling back to <c>CategoryOf</c>).
    ///
    /// <para><b>Order contract (unit-tested):</b> subgroup order is the FIRST-APPEARANCE order of each distinct key in
    /// the input; member order within a subgroup is the input order of those ids. Both are stable and deterministic.</para>
    ///
    /// <para>Purely presentation state — the sim never sees a subgroup. Godot-free (System.* only) so it compiles into
    /// the Tier-1 test assembly and its policy is unit-testable directly (the <see cref="UnderAttackThrottle"/>
    /// precedent). It holds no sim state and never enters any determinism hash.</para>
    /// </summary>
    public static class SelectionSubgroups
    {
        /// <summary>One WC3 subgroup — a grouping key plus the member ids that share it (in input order).</summary>
        public readonly struct Subgroup
        {
            /// <summary>The grouping key every member shares (a per-definition or per-category value).</summary>
            public readonly long Key;
            /// <summary>The member entity ids, in the order they appeared in the input selection.</summary>
            public readonly IReadOnlyList<int> Members;

            public Subgroup(long key, IReadOnlyList<int> members)
            {
                Key = key;
                Members = members;
            }
        }

        /// <summary>
        /// Group <paramref name="ids"/> by the parallel per-id key in <paramref name="keys"/> → ordered subgroups.
        /// Subgroup order = first appearance of each distinct key; member order = input order. Handles the empty,
        /// single-group, and all-distinct cases. If the two lists differ in length, only the shared prefix is grouped
        /// (defensive — the caller always passes parallel lists). Never returns null.
        /// </summary>
        public static IReadOnlyList<Subgroup> Group(IReadOnlyList<int> ids, IReadOnlyList<long> keys)
        {
            var groups = new List<Subgroup>();
            if (ids == null || keys == null) return groups;

            int n = ids.Count < keys.Count ? ids.Count : keys.Count;
            if (n == 0) return groups;

            // key → its index in the appearance-ordered member lists (a small map; presentation-only, Dictionary is fine here).
            var indexOfKey = new Dictionary<long, int>();
            var memberLists = new List<List<int>>();
            var groupKeys = new List<long>();

            for (int i = 0; i < n; i++)
            {
                long k = keys[i];
                if (!indexOfKey.TryGetValue(k, out int gi))
                {
                    gi = memberLists.Count;
                    indexOfKey[k] = gi;
                    memberLists.Add(new List<int>());
                    groupKeys.Add(k);
                }
                memberLists[gi].Add(ids[i]);
            }

            for (int g = 0; g < memberLists.Count; g++)
                groups.Add(new Subgroup(groupKeys[g], memberLists[g]));
            return groups;
        }

        /// <summary>
        /// After a rebuild that must PRESERVE the active subgroup across a membership change (a death, not a fresh
        /// selection), pick the new active index. The active subgroup follows its grouping KEY: if a subgroup with
        /// <paramref name="previousKey"/> still exists, its new index is returned (so the active subgroup stays
        /// identity-stable even when an EARLIER subgroup was wiped and the indices shifted). Otherwise the active
        /// subgroup itself emptied, so <paramref name="previousIndex"/> is clamped into <c>[0, count-1]</c>. Returns -1
        /// for an empty group set. Godot-free and unit-tested — the index/preserve policy has explicit Tier-1 coverage.
        /// </summary>
        public static int ReconcileActiveIndex(IReadOnlyList<Subgroup> groups, long previousKey, int previousIndex)
        {
            if (groups == null || groups.Count == 0) return -1;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Key == previousKey) return i; // active follows its key (identity-stable)
            // The active subgroup's key is gone (it emptied) → clamp the old index into range.
            if (previousIndex < 0) return 0;
            if (previousIndex >= groups.Count) return groups.Count - 1;
            return previousIndex;
        }
    }
}
