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
    }
}
