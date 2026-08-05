#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.6 — <see cref="TechTreeLayout.ComputeTiers"/>'s pure tier-computation algorithm: the single layout
    /// source the Visual Tech Tree Editor and this test both depend on. One test per the spec's Task list: no
    /// prerequisites ⇒ tier 0; a linear chain tiers 0/1/2; a diamond resolves via max, not sum; an unresolvable
    /// prerequisite id is skipped without throwing.
    /// </summary>
    public class TechTreeLayoutTests
    {
        private static BuildingDefinition Building(string id, params string[] prereqs) =>
            new() { Id = id, Prerequisites = prereqs };

        [Fact]
        public void NoPrerequisiteBuildings_AllTierZero()
        {
            var buildings = new List<BuildingDefinition>
            {
                Building("a"),
                Building("b"),
                Building("c"),
            };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["a"]);
            Assert.Equal(0, tiers["b"]);
            Assert.Equal(0, tiers["c"]);
        }

        [Fact]
        public void LinearChain_TiersZeroOneTwo()
        {
            // A ← B ← C: A has no prereqs (tier 0), B requires A (tier 1), C requires B (tier 2).
            var buildings = new List<BuildingDefinition>
            {
                Building("a"),
                Building("b", "a"),
                Building("c", "b"),
            };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["a"]);
            Assert.Equal(1, tiers["b"]);
            Assert.Equal(2, tiers["c"]);
        }

        [Fact]
        public void Diamond_ResolvesViaMaxNotSum()
        {
            // A and B are both tier-1 (each requires the tier-0 root). C requires BOTH A and B — tier must resolve
            // via max(tier(a), tier(b)) + 1 = 2, NOT sum(tier(a) + tier(b)) which would wrongly yield 3.
            var buildings = new List<BuildingDefinition>
            {
                Building("root"),
                Building("a", "root"),
                Building("b", "root"),
                Building("c", "a", "b"),
            };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["root"]);
            Assert.Equal(1, tiers["a"]);
            Assert.Equal(1, tiers["b"]);
            Assert.Equal(2, tiers["c"]);
        }

        [Fact]
        public void UnresolvablePrerequisiteId_SkippedWithoutThrowing()
        {
            var buildings = new List<BuildingDefinition>
            {
                Building("a", "does_not_exist"),
            };

            Exception? ex = Record.Exception(() => TechTreeLayout.ComputeTiers(buildings));
            Assert.Null(ex);

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);
            Assert.Equal(0, tiers["a"]);   // the unresolvable prereq contributes nothing — treated as if absent
        }

        [Fact]
        public void MixedResolvableAndUnresolvablePrerequisites_OnlyResolvableContributes()
        {
            var buildings = new List<BuildingDefinition>
            {
                Building("a"),
                Building("b", "a", "phantom_id"),
            };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["a"]);
            Assert.Equal(1, tiers["b"]);
        }

        [Fact]
        public void NullPrerequisites_TreatedAsTierZero_NoThrow()
        {
            var buildings = new List<BuildingDefinition> { new() { Id = "a", Prerequisites = null! } };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["a"]);
        }

        // ── DW-574: deep-chain stack safety (explicit-stack iterative tier walk) ──

        [Fact]
        public void DeepPrerequisiteChain_100kDeep_ComputesEveryTier_NoStackOverflow()
        {
            // DW-574 regression: TierOf was a plain recursion — one C# call-stack frame per depth level. The
            // "visiting" guard bounded the walk against CYCLES but never against DEPTH, so a chain this deep
            // (which now survives import validation precisely because DW-59/DW-573 hardened the two validators)
            // overflowed the call stack while the editor laid out its tech-tree panel, and a StackOverflowException
            // kills the test host process uncatchably (this test crashes the whole run against the recursive
            // version; 100k frames is far past any default stack). The explicit-stack rewrite bounds depth by heap
            // memory instead. b0 requires b1 requires b2 … so the FIRST list entry drives the full descent.
            const int depth = 100_000;
            var buildings = new List<BuildingDefinition>(depth);
            for (int i = 0; i < depth; i++)
            {
                buildings.Add(new BuildingDefinition
                {
                    Id = $"b{i}",
                    Prerequisites = i + 1 < depth ? new[] { $"b{i + 1}" } : Array.Empty<string>(),
                });
            }

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            // Longest-path depth counts UP from the chain's far end: the deepest building has no prerequisites
            // (tier 0) and every step back toward b0 adds one.
            Assert.Equal(depth, tiers.Count);
            Assert.Equal(0, tiers[$"b{depth - 1}"]);
            Assert.Equal(1, tiers[$"b{depth - 2}"]);
            Assert.Equal(depth - 1, tiers["b0"]);
        }

        [Fact]
        public void DeepPrerequisiteChain_100kDeep_WithCycleAtTheDeepEnd_GuardStillFiresWithoutOverflow()
        {
            // The cycle sits at the BOTTOM, so the walk must survive the full 100k-deep descent before the
            // "visiting" re-entry guard can even fire — the two hazards (depth and re-entry) in one graph. The
            // deepest building points back at its parent, which is still being computed, so it short-circuits to
            // tier 0 and every tier above it is still assigned.
            const int depth = 100_000;
            var buildings = new List<BuildingDefinition>(depth);
            for (int i = 0; i < depth; i++)
            {
                buildings.Add(new BuildingDefinition
                {
                    Id = $"b{i}",
                    Prerequisites = i + 1 < depth ? new[] { $"b{i + 1}" } : new[] { $"b{depth - 2}" },
                });
            }

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(depth, tiers.Count);
            // b{depth-1} re-enters the Gray b{depth-2} → that edge contributes 0, so b{depth-1} lands on tier 1.
            Assert.Equal(1, tiers[$"b{depth - 1}"]);
            Assert.Equal(2, tiers[$"b{depth - 2}"]);
            Assert.Equal(depth, tiers["b0"]);
        }

        // ── Cycle-guard value pins (behaviour the DW-574 rewrite must not drift) ──

        [Fact]
        public void SelfPrerequisite_GuardShortCircuitsToTierZero_YieldingTierOne()
        {
            // A hand-edited file the loader never linted: "a requires a". The re-entry guard makes the inner
            // resolution contribute 0, so a lands on tier 1 — pinned so the iterative rewrite cannot silently
            // change the guard's short-circuit VALUE (memoizing the 0 instead, say, would yield 0 here).
            var buildings = new List<BuildingDefinition> { Building("a", "a") };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(1, tiers["a"]);
        }

        [Fact]
        public void TwoNodeCycle_GuardShortCircuits_TiersAssignedInListOrderWithoutHanging()
        {
            // a requires b requires a. Walking from list order: a opens, b opens, b's edge back to the Gray a
            // short-circuits to 0 → b = 1 → a = 2. Asymmetric by construction (the entry point wins a tier),
            // which is exactly the deterministic quirk being pinned: a node short-circuited by the guard is NOT
            // memoized at 0, so its own frame still computes the real value.
            var buildings = new List<BuildingDefinition> { Building("a", "b"), Building("b", "a") };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(2, tiers["a"]);
            Assert.Equal(1, tiers["b"]);
        }

        [Fact]
        public void SharedSubtree_MemoizedOnce_DiamondAndDeeperReuseAgree()
        {
            // Order/memo guard for the rewrite: "wide" is reached twice (via left and via right). The second
            // encounter must come back off the memo with the SAME tier, and the max-fold must still pick the
            // deeper of two branches rather than whichever was examined last.
            var buildings = new List<BuildingDefinition>
            {
                Building("top", "left", "right"),
                Building("left", "wide"),
                Building("right", "mid"),
                Building("mid", "wide"),
                Building("wide", "base"),
                Building("base"),
            };

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(buildings);

            Assert.Equal(0, tiers["base"]);
            Assert.Equal(1, tiers["wide"]);
            Assert.Equal(2, tiers["left"]);   // shallow branch
            Assert.Equal(2, tiers["mid"]);
            Assert.Equal(3, tiers["right"]);  // deep branch
            Assert.Equal(4, tiers["top"]);    // max(2, 3) + 1 — never the shallow branch, never a sum
        }
    }
}
