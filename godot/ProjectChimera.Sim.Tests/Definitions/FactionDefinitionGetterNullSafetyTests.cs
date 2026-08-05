#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
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
    ///
    /// <para>DW-629 — the same contract for <see cref="FactionDefinition.GetBuilding"/>, the one sibling DW-103's
    /// sweep missed. Both <see cref="FactionDefinition.Buildings"/> being null (a JSON <c>"buildings": null</c>
    /// overwrites the settable property's <c>= new()</c> default) and a null element inside it must degrade to
    /// "not found", never throw.</para>
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

        // ── DW-629: GetBuilding, the sibling DW-103's sweep missed ────────────────────────────────────────

        /// <summary>A faction whose Buildings list contains a null element ahead of one real building "hq".</summary>
        private static FactionDefinition FactionWithNullBuildingElement() => new()
        {
            Id = "f",
            Buildings = new List<BuildingDefinition>
            {
                null!,
                new BuildingDefinition { Id = "hq" },
            },
        };

        [Fact]
        public void GetBuilding_NullElement_SkipsNull_ResolvesRealBuilding()
        {
            FactionDefinition def = FactionWithNullBuildingElement();
            BuildingDefinition? b = null;
            var ex = Record.Exception(() => b = def.GetBuilding("hq"));
            Assert.Null(ex);
            Assert.NotNull(b);
            Assert.Equal("hq", b!.Id);
        }

        /// <summary>A miss must still walk PAST the null element to the end of the list and return null — the
        /// guard has to skip the null, not merely short-circuit on the first match.</summary>
        [Fact]
        public void GetBuilding_NullElement_UnknownId_NoThrow_ReturnsNull()
        {
            FactionDefinition def = FactionWithNullBuildingElement();
            // Deliberately non-null seed so a skipped assignment could not masquerade as a null return.
            BuildingDefinition? b = new BuildingDefinition { Id = "sentinel" };
            var ex = Record.Exception(() => b = def.GetBuilding("nope"));
            Assert.Null(ex);
            Assert.Null(b);
        }

        [Fact]
        public void GetBuilding_NullBuildingsList_NoThrow_ReturnsNull()
        {
            var def = new FactionDefinition { Id = "f", Buildings = null! };
            BuildingDefinition? b = null;
            var ex = Record.Exception(() => b = def.GetBuilding("hq"));
            Assert.Null(ex);
            Assert.Null(b);
        }

        /// <summary>The end-to-end shape DW-629 names: a malformed-but-parseable faction document whose
        /// <c>"buildings"</c> key is a JSON <c>null</c>. The settable property means the <c>= new()</c> default is
        /// OVERWRITTEN by the null, so any caller that skips <see cref="FactionValidator"/>'s structural pre-check
        /// (a direct deserialize, exactly as done here) holds a def whose Buildings really is null.</summary>
        [Fact]
        public void GetBuilding_DeserializedNullBuildingsKey_NoThrow_ReturnsNull()
        {
            const string json = """{"id":"f","units":[],"buildings":null}""";
            FactionDefinition? def = JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions);

            Assert.NotNull(def);
            Assert.Null(def!.Buildings);   // the JSON null really does overwrite the `= new()` default
            Assert.Null(Record.Exception(() => Assert.Null(def.GetBuilding("hq"))));
        }
    }
}
