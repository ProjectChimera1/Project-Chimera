#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-103 — the four <see cref="FactionDefinition"/> unit getters (<see cref="FactionDefinition.GetUnit"/>,
    /// <see cref="FactionDefinition.IndexOfUnit"/>, <see cref="FactionDefinition.GetUnitByCategory"/>,
    /// <see cref="FactionDefinition.GetUnitsByCategory"/>) must skip a null <see cref="FactionDefinition.Units"/> list
    /// AND a null element inside it — never a <see cref="NullReferenceException"/> — mirroring the research getters
    /// that already tolerate nulls. A malformed-but-parseable faction (<c>"units": [null, {...}]</c>) loads without
    /// error today, so these getters must resolve the real unit past the null.
    /// </summary>
    public class FactionDefinitionGetterNullSafetyTests
    {
        /// <summary>A faction whose Units list contains a null element ahead of one real unit "w".</summary>
        private static FactionDefinition FactionWithNullUnitElement() => new()
        {
            Id = "f",
            Units = new List<UnitDefinition>
            {
                null!,
                new UnitDefinition { Id = "w", Category = "Worker" },
            },
        };

        [Fact]
        public void GetUnit_NullElement_SkipsNull_ResolvesRealUnit()
        {
            FactionDefinition def = FactionWithNullUnitElement();
            UnitDefinition? u = null;
            var ex = Record.Exception(() => u = def.GetUnit("w"));
            Assert.Null(ex);
            Assert.NotNull(u);
            Assert.Equal("w", u!.Id);
        }

        [Fact]
        public void IndexOfUnit_NullElement_SkipsNull_ResolvesRealUnit()
        {
            FactionDefinition def = FactionWithNullUnitElement();
            int idx = -99;
            var ex = Record.Exception(() => idx = def.IndexOfUnit("w"));
            Assert.Null(ex);
            Assert.Equal(1, idx); // the real unit is at index 1 (null is index 0)
        }

        [Fact]
        public void GetUnitByCategory_NullElement_SkipsNull_ResolvesRealUnit()
        {
            FactionDefinition def = FactionWithNullUnitElement();
            UnitDefinition? u = null;
            var ex = Record.Exception(() => u = def.GetUnitByCategory("Worker"));
            Assert.Null(ex);
            Assert.NotNull(u);
            Assert.Equal("w", u!.Id);
        }

        [Fact]
        public void GetUnitsByCategory_NullElement_SkipsNull_ResolvesRealUnit()
        {
            FactionDefinition def = FactionWithNullUnitElement();
            List<(int Index, UnitDefinition Def)> matches = new();
            var ex = Record.Exception(() => matches = def.GetUnitsByCategory("Worker"));
            Assert.Null(ex);
            Assert.Single(matches);
            Assert.Equal("w", matches[0].Def.Id);
            Assert.Equal(1, matches[0].Index);
        }

        // ── Null Units LIST (malformed "units": null) — every getter degrades gracefully ──────────────────

        [Fact]
        public void AllGetters_NullUnitsList_NoThrow_ReturnEmptyOrNotFound()
        {
            var def = new FactionDefinition { Id = "f", Units = null! };

            Assert.Null(Record.Exception(() =>
            {
                Assert.Null(def.GetUnit("w"));
                Assert.Equal(-1, def.IndexOfUnit("w"));
                Assert.Null(def.GetUnitByCategory("Worker"));
                Assert.Empty(def.GetUnitsByCategory("Worker"));
            }));
        }
    }
}
