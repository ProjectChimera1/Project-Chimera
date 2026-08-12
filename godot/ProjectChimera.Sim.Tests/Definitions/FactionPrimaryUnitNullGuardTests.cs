#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-748 — <see cref="FactionDefinition.PrimaryUnit"/> was the last unguarded member of the DW-103/DW-629
    /// null-hygiene class in <c>FactionDefinition</c>, and it carried TWO faults:
    ///
    /// <list type="number">
    /// <item><b>Null list.</b> <see cref="FactionDefinition.Units"/> is a SETTABLE auto-property, so a malformed
    /// <c>"units": null</c> overwrites the <c>= new()</c> initializer with null and the old
    /// <c>Units.Count &gt; 0 ? Units[0] : null</c> threw a <see cref="System.NullReferenceException"/> — exactly the
    /// failure DW-629 proved for <c>Buildings</c>.</item>
    /// <item><b>Null element.</b> Even with a non-null list, it handed back <c>Units[0]</c> VERBATIM, so a
    /// <c>"units": [null, {...}]</c> document returned a NULL <see cref="UnitDefinition"/> as the faction's PRIMARY
    /// unit — relocating the NRE onto the caller instead of skipping to the first real entry, which is what every
    /// sibling accessor (<see cref="FactionDefinition.GetUnit"/> / <see cref="FactionDefinition.IndexOfUnit"/> /
    /// <see cref="FactionDefinition.GetUnitByCategory"/>) already does.</item>
    /// </list>
    ///
    /// <para>Both shapes are reachable from any path that bypasses <see cref="FactionValidator"/>'s structural
    /// pre-check — a direct <c>JsonSerializer.Deserialize</c> (which is what the tests below drive, so the malformed
    /// document is produced the same way production would), hand-built defs in tools, or the Story 6.8
    /// scenario-buildings gate. Godot-free / Tier-1: pure DTO deserialization, no I/O, no sim.</para>
    /// </summary>
    public class FactionPrimaryUnitNullGuardTests
    {
        private static FactionDefinition Parse(string json) =>
            JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)!;

        // ── Fault 1: a null Units list ───────────────────────────────────────────────────────

        [Fact]
        public void PrimaryUnit_NullUnitsList_ReturnsNull_NeverThrows()
        {
            // Pre-fix: Units.Count on the null list → NullReferenceException out of a plain property read.
            FactionDefinition def = Parse("""{ "id": "x", "display_name": "X", "units": null }""");
            Assert.Null(def.Units);   // the premise: the JSON null really does overwrite the = new() default

            Assert.Null(def.PrimaryUnit);
        }

        // ── Fault 2: a null element ahead of the first real unit ─────────────────────────────

        [Fact]
        public void PrimaryUnit_LeadingNullElement_SkipsToTheFirstRealUnit()
        {
            // Pre-fix: returned Units[0] verbatim — i.e. NULL — as the faction's "primary" unit, pushing the NRE
            // onto whichever caller dereferenced it.
            FactionDefinition def = Parse("""
            {
              "id": "x", "display_name": "X",
              "units": [ null, { "id": "worker", "display_name": "Worker", "category": "Worker", "hp": 50 } ]
            }
            """);
            Assert.Equal(2, def.Units.Count);
            Assert.Null(def.Units[0]);   // the premise: element 0 really is null

            UnitDefinition? primary = def.PrimaryUnit;
            Assert.NotNull(primary);
            Assert.Equal("worker", primary!.Id);
        }

        [Fact]
        public void PrimaryUnit_AgreesWithGetUnit_OnADocumentWithALeadingNull()
        {
            // The stated closure is "mirror GetUnit". Pin the two accessors on the SAME malformed document so the
            // one cannot be hardened without the other staying consistent.
            FactionDefinition def = Parse("""
            {
              "id": "x", "display_name": "X",
              "units": [ null, { "id": "worker", "display_name": "Worker", "category": "Worker", "hp": 50 },
                               { "id": "melee",  "display_name": "Melee",  "category": "Melee",  "hp": 60 } ]
            }
            """);

            Assert.Same(def.GetUnit("worker"), def.PrimaryUnit);
            Assert.Equal(1, def.IndexOfUnit("worker")); // and the LIST coordinate is untouched — index 1, not 0
        }

        [Fact]
        public void PrimaryUnit_AllElementsNull_ReturnsNull()
        {
            FactionDefinition def = Parse("""{ "id": "x", "display_name": "X", "units": [ null, null ] }""");
            Assert.Null(def.PrimaryUnit);
        }

        // ── Non-regression: the well-formed shapes must be unchanged ─────────────────────────

        [Fact]
        public void PrimaryUnit_WellFormedRoster_StillReturnsTheFirstUnit()
        {
            var def = new FactionDefinition
            {
                Id = "x", DisplayName = "X",
                Units = new List<UnitDefinition>
                {
                    new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f },
                    new UnitDefinition { Id = "melee",  Category = "Melee",  Hp = 60f },
                },
            };

            Assert.Same(def.Units[0], def.PrimaryUnit);
        }

        [Fact]
        public void PrimaryUnit_EmptyRoster_ReturnsNull()
        {
            Assert.Null(new FactionDefinition { Id = "x", DisplayName = "X" }.PrimaryUnit);
        }
    }
}
