#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 1.13 (DG-2 / FR-54) — the Godot-free <see cref="FormationPlanner"/>: role-based front/back layout
    /// (AC4b), deterministic slot assignment (AC4c), and the degenerate cases (AC5: single unit, one archetype,
    /// no overlapping destinations). Pure <see cref="Fixed"/> — runs on every OS including the WSL leg.
    /// </summary>
    public class FormationPlannerTests
    {
        private static readonly Fixed Spacing = Fixed.FromInt(2);

        // ── AC4b — front-line archetypes lead; back-line trail ──────────────────────────────────────────────────

        [Fact]
        public void MeleeAndSiege_AreForwardOfRanged_RelativeToFacing()
        {
            int[] ids = { 0, 1, 2, 3 };
            var cats = new[] { UnitCategory.Melee, UnitCategory.Ranged, UnitCategory.Siege, UnitCategory.Ranged };
            FixedVec3 target = V(0, 0, 0), facing = V(1, 0, 0);

            FixedVec3[] d = FormationPlanner.Plan(ids, cats, target, facing, Spacing);

            // Projection onto the facing direction: every front-line (Melee=0, Siege=2) destination must exceed
            // every back-line (Ranged=1,3) destination.
            int[] front = { 0, 2 }, back = { 1, 3 };
            foreach (int fk in front)
                foreach (int bk in back)
                    Assert.True(FixedVec3.Dot(d[fk], facing).Raw > FixedVec3.Dot(d[bk], facing).Raw,
                        $"front unit {fk} (proj {FixedVec3.Dot(d[fk], facing)}) must lead back unit {bk} (proj {FixedVec3.Dot(d[bk], facing)}).");

            AssertAllDistinct(d); // AC5 — no destination on top of another
        }

        // ── AC4c — identical inputs → identical output ──────────────────────────────────────────────────────────

        [Fact]
        public void IdenticalInputs_ProduceIdenticalDestinations()
        {
            int[] ids = { 0, 1, 2, 3, 4 };
            var cats = new[] { UnitCategory.Melee, UnitCategory.Ranged, UnitCategory.Melee, UnitCategory.Worker, UnitCategory.Siege };
            FixedVec3 target = V(7, 0, -3), facing = V(2, 0, 5); // non-axis facing → exercises the perpendicular math

            FixedVec3[] a = FormationPlanner.Plan(ids, cats, target, facing, Spacing);
            FixedVec3[] b = FormationPlanner.Plan(ids, cats, target, facing, Spacing);

            Assert.Equal(a.Length, b.Length);
            for (int k = 0; k < a.Length; k++)
                AssertVec(a[k], b[k]);
        }

        // ── AC5 — degenerate cases ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SingleUnit_DegradesToCenteredTarget()
        {
            FixedVec3 target = V(4, 0, 9);
            FixedVec3[] d = FormationPlanner.Plan(new[] { 0 }, new[] { UnitCategory.Melee }, target, V(1, 0, 0), Spacing);
            Assert.Single(d);
            AssertVec(target, d[0]); // the target point itself — no offset
        }

        [Fact]
        public void OneArchetypeGroup_IsASingleCenteredRow_NoFrontGap()
        {
            int[] ids = { 0, 1, 2 };
            var cats = new[] { UnitCategory.Ranged, UnitCategory.Ranged, UnitCategory.Ranged }; // all back-line
            FixedVec3 target = V(0, 0, 0), facing = V(1, 0, 0);

            FixedVec3[] d = FormationPlanner.Plan(ids, cats, target, facing, Spacing);

            // A one-archetype group sits ON the centerline (no reserved empty opposite rank): every destination's
            // projection along the facing equals the target's (a single row perpendicular to the facing).
            Fixed targetProj = FixedVec3.Dot(target, facing);
            foreach (FixedVec3 dest in d)
                Assert.Equal(targetProj.Raw, FixedVec3.Dot(dest, facing).Raw);

            AssertAllDistinct(d);
        }

        [Fact]
        public void EmptySelection_ReturnsEmpty()
        {
            FixedVec3[] d = FormationPlanner.Plan(System.Array.Empty<int>(),
                System.Array.Empty<UnitCategory>(), V(0, 0, 0), V(1, 0, 0), Spacing);
            Assert.Empty(d);
        }

        // ── Review patch (1.13) — degenerate facing guard ───────────────────────────────────────────────────────

        [Fact]
        public void VerticalFacing_DoesNotCollapseRank_DestinationsStayDistinct()
        {
            // A purely-vertical facing (X == Z == 0) would make the row axis `right = (f.Z, 0, -f.X)` zero and stack
            // an entire rank on one point. The degenerate guard must fall back to the canonical axis so the slots stay
            // distinct (AC5). Unreachable from the shipped Y==0 call sites, but the planner is a general helper — without
            // the guard these four destinations all collapse onto the target.
            int[] ids = { 0, 1, 2, 3 };
            var cats = new[] { UnitCategory.Ranged, UnitCategory.Ranged, UnitCategory.Ranged, UnitCategory.Ranged };
            FixedVec3[] d = FormationPlanner.Plan(ids, cats, V(0, 0, 0), V(0, 1, 0), Spacing);
            AssertAllDistinct(d);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        private static void AssertVec(FixedVec3 expected, FixedVec3 actual)
        {
            Assert.Equal(expected.X.Raw, actual.X.Raw);
            Assert.Equal(expected.Y.Raw, actual.Y.Raw);
            Assert.Equal(expected.Z.Raw, actual.Z.Raw);
        }

        private static void AssertAllDistinct(FixedVec3[] d)
        {
            for (int i = 0; i < d.Length; i++)
                for (int j = i + 1; j < d.Length; j++)
                    Assert.True(d[i] != d[j], $"destinations {i} and {j} coincide ({d[i]}) — slots must be distinct.");
        }
    }
}
