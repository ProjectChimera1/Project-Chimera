#nullable enable
using System.Collections.Generic;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 11.5 (FR-74) — the Godot-free WC3 subgroup grouping policy. Proves the order contract (subgroup order =
    /// key first-appearance; member order = input order) and the empty / single-group / all-distinct edge cases.
    /// </summary>
    public class SelectionSubgroupsTests
    {
        [Fact]
        public void Empty_input_produces_no_subgroups()
        {
            var groups = SelectionSubgroups.Group(new List<int>(), new List<long>());
            Assert.Empty(groups);
        }

        [Fact]
        public void Single_type_produces_one_subgroup_in_input_order()
        {
            var ids  = new List<int> { 7, 3, 9 };
            var keys = new List<long> { 5, 5, 5 };

            var groups = SelectionSubgroups.Group(ids, keys);

            Assert.Single(groups);
            Assert.Equal(5L, groups[0].Key);
            Assert.Equal(new[] { 7, 3, 9 }, groups[0].Members);
        }

        [Fact]
        public void All_distinct_keys_produce_one_subgroup_each_in_first_appearance_order()
        {
            var ids  = new List<int> { 10, 20, 30 };
            var keys = new List<long> { 2, 1, 3 };

            var groups = SelectionSubgroups.Group(ids, keys);

            Assert.Equal(3, groups.Count);
            Assert.Equal(2L, groups[0].Key);
            Assert.Equal(1L, groups[1].Key);
            Assert.Equal(3L, groups[2].Key);
            Assert.Equal(new[] { 10 }, groups[0].Members);
            Assert.Equal(new[] { 20 }, groups[1].Members);
            Assert.Equal(new[] { 30 }, groups[2].Members);
        }

        [Fact]
        public void Mixed_selection_preserves_key_first_appearance_and_member_input_order()
        {
            // Interleaved types: A(1) B(2) A(1) C(3) B(2) A(1)
            var ids  = new List<int> {  1,  2,  3,  4,  5,  6 };
            var keys = new List<long> { 1,  2,  1,  3,  2,  1 };

            var groups = SelectionSubgroups.Group(ids, keys);

            Assert.Equal(3, groups.Count);
            // Subgroup order = first appearance: key 1, then 2, then 3.
            Assert.Equal(1L, groups[0].Key);
            Assert.Equal(2L, groups[1].Key);
            Assert.Equal(3L, groups[2].Key);
            // Member order within a subgroup = input order.
            Assert.Equal(new[] { 1, 3, 6 }, groups[0].Members);
            Assert.Equal(new[] { 2, 5 },    groups[1].Members);
            Assert.Equal(new[] { 4 },       groups[2].Members);
        }

        [Fact]
        public void Emitted_id_multiset_equals_the_input_multiset_no_id_lost_or_duplicated()
        {
            var ids  = new List<int> { 1, 2, 3, 4, 5 };
            var keys = new List<long> { 9, 9, 8, 9, 8 };

            var groups = SelectionSubgroups.Group(ids, keys);

            var emitted = new List<int>();
            foreach (var g in groups) emitted.AddRange(g.Members);
            emitted.Sort();
            var expected = new List<int>(ids); expected.Sort();
            Assert.Equal(expected, emitted); // exact multiset — catches a duplicate-one/drop-another regression
        }

        [Fact]
        public void Length_mismatch_groups_only_the_shared_prefix()
        {
            var ids  = new List<int> { 1, 2, 3, 4 }; // longer
            var keys = new List<long> { 7, 7 };      // shorter → only ids[0..1] are grouped

            var groups = SelectionSubgroups.Group(ids, keys);

            Assert.Single(groups);
            Assert.Equal(new[] { 1, 2 }, groups[0].Members);
        }

        [Fact]
        public void Null_input_returns_empty_not_null()
        {
            Assert.Empty(SelectionSubgroups.Group(null!, new List<long>()));
            Assert.Empty(SelectionSubgroups.Group(new List<int>(), null!));
            Assert.Empty(SelectionSubgroups.Group(null!, null!));
        }

        // ── ReconcileActiveIndex (Story 11.5 review #3 — the active-subgroup preserve/clamp policy) ──────────────────

        private static SelectionSubgroups.Subgroup Sub(long key, params int[] members)
            => new SelectionSubgroups.Subgroup(key, new List<int>(members));

        [Fact]
        public void Reconcile_active_follows_its_key_when_an_earlier_subgroup_is_removed()
        {
            // Was [W(0),S(1),M(2)] active=1 (Soldier, key 20). Workers die → [S(0),M(1)].
            var rebuilt = new List<SelectionSubgroups.Subgroup> { Sub(20, 5, 6), Sub(30, 7) };
            int active = SelectionSubgroups.ReconcileActiveIndex(rebuilt, previousKey: 20, previousIndex: 1);
            Assert.Equal(0, active); // active moved down with its key, staying on the Soldier subgroup
        }

        [Fact]
        public void Reconcile_clamps_when_the_active_subgroup_itself_emptied()
        {
            // Was active=1 (key 20). That subgroup emptied → [W(0),M(1)] (keys 10, 30).
            var rebuilt = new List<SelectionSubgroups.Subgroup> { Sub(10, 1), Sub(30, 7) };
            int active = SelectionSubgroups.ReconcileActiveIndex(rebuilt, previousKey: 20, previousIndex: 1);
            Assert.Equal(1, active); // key gone → old index 1 clamped into range
        }

        [Fact]
        public void Reconcile_clamps_a_trailing_emptied_subgroup_into_range()
        {
            // Was active=2 (key 30, trailing). It emptied → [W(0),S(1)].
            var rebuilt = new List<SelectionSubgroups.Subgroup> { Sub(10, 1), Sub(20, 5) };
            int active = SelectionSubgroups.ReconcileActiveIndex(rebuilt, previousKey: 30, previousIndex: 2);
            Assert.Equal(1, active); // out-of-range old index clamped to last
        }

        [Fact]
        public void Reconcile_is_a_no_op_when_nothing_changed()
        {
            var rebuilt = new List<SelectionSubgroups.Subgroup> { Sub(10, 1), Sub(20, 5), Sub(30, 7) };
            int active = SelectionSubgroups.ReconcileActiveIndex(rebuilt, previousKey: 20, previousIndex: 1);
            Assert.Equal(1, active);
        }

        [Fact]
        public void Reconcile_empty_group_set_returns_minus_one()
        {
            var empty = new List<SelectionSubgroups.Subgroup>();
            Assert.Equal(-1, SelectionSubgroups.ReconcileActiveIndex(empty, previousKey: 5, previousIndex: 0));
        }
    }
}
