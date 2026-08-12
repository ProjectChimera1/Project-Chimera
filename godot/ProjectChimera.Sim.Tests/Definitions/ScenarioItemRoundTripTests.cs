#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.15 — <see cref="ScenarioData.Items"/> + <see cref="ScenarioData.InventorySlotCount"/> serialize/deserialize
    /// round-trip. Placed items round-trip (mirror the Units array); an omitted <c>inventory_slot_count</c> (null) is NOT
    /// emitted (omit-when-null → a scenario with no cap serializes byte-identically to before this story).
    /// </summary>
    public class ScenarioItemRoundTripTests
    {
        /// <summary>
        /// DW-523 - the PRODUCTION scenario options (<see cref="ContentJson.ScenarioOptions"/>), not a hand-rolled
        /// replica that was looser than the real loader on the enum axis and missing its widget converter.
        /// </summary>
        private static readonly JsonSerializerOptions Opt = ContentJson.ScenarioOptions;

        [Fact]
        public void PlacedItems_RoundTrip()
        {
            var model = new ScenarioData
            {
                Items = new[]
                {
                    new ScenarioItem { ItemId = "ring_of_vigor", X = 5f, Z = -4f },
                    new ScenarioItem { ItemId = "potion_of_healing", X = -1f, Z = 2f },
                },
                InventorySlotCount = 4,
            };
            string json = ScenarioSerializer.Serialize(model);
            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);

            Assert.NotNull(back);
            // DW-703 — ScenarioData.Items is `ScenarioItem[]?` (omit-when-null), so reading .Length off it was a
            // CS8602 possible-null-dereference. Asserting it non-null (rather than null-forgiving with `!`) also
            // makes a writer regression that DROPS the items array fail HERE, naming the array, instead of as an
            // unexplained NullReferenceException inside the element assertions below.
            ScenarioItem[]? items = back!.Items;
            Assert.NotNull(items);
            Assert.Equal(2, items!.Length);
            Assert.Equal("ring_of_vigor", items[0].ItemId);
            Assert.Equal(5f, items[0].X);
            Assert.Equal(-4f, items[0].Z);
            Assert.Equal(4, back.InventorySlotCount);
        }

        [Fact]
        public void OmittedInventorySlotCount_IsNotSerialized()
        {
            var model = new ScenarioData(); // no InventorySlotCount → null → omitted
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("inventory_slot_count", json);

            ScenarioData? back = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            Assert.NotNull(back);
            Assert.Null(back!.InventorySlotCount);
        }
    }
}
