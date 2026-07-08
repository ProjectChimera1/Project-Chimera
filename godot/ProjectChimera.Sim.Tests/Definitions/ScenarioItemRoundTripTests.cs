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
        private static readonly JsonSerializerOptions Opt = new()
        {
            Converters = { new JsonStringEnumConverter(), new FixedJsonConverter() },
        };

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
            Assert.Equal(2, back!.Items.Length);
            Assert.Equal("ring_of_vigor", back.Items[0].ItemId);
            Assert.Equal(5f, back.Items[0].X);
            Assert.Equal(-4f, back.Items[0].Z);
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
