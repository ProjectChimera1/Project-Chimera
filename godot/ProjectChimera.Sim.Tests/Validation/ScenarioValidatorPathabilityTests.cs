#nullable enable
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.5 — the validator fails closed (clear message, pre-tick) when a start base, a pre-placed unit, or a
    /// spawn_unit trigger coordinate resolves to a PAINTED blocked cell — using the SAME 128²/2-unit
    /// <see cref="FlowField.WorldToCell"/> mapping the sim enforces. A clear cell (or no painted layer) passes.
    /// </summary>
    public class ScenarioValidatorPathabilityTests
    {
        // Blocked flow cell (64, 64) — its centre world position is (1, 1).
        private const float BlockedX = 1f, BlockedZ = 1f;

        private static string BlockedCell64_64()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[64 * PathabilityGrid.GRID_SIZE + 64] = true;
            return PathabilityGrid.ToBase64(mask)!;
        }

        private static ScenarioData Valid()
        {
            return new ScenarioData
            {
                MapBounds = 120f,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
            };
        }

        private static readonly ScenarioValidator Validator = new();

        [Fact]
        public void ClearMap_NoPaint_Passes()
        {
            Assert.True(Validator.Validate(Valid()).Ok);
        }

        [Fact]
        public void StartBaseOnBlockedCell_FailsClosed()
        {
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64();
            m.PlayerSlots[0].BaseX = BlockedX;
            m.PlayerSlots[0].BaseZ = BlockedZ;
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void PrePlacedUnitOnBlockedCell_FailsClosed()
        {
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = BlockedX, Z = BlockedZ } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void SpawnUnitTriggerOnBlockedCell_FailsClosed()
        {
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events  = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[] { new TriggerAction { Type = "spawn_unit", UnitId = "worker", Faction = 0, X = BlockedX, Z = BlockedZ, Count = 1 } },
                },
            };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void PaintedLayer_ButPositionsClear_Passes()
        {
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64(); // a wall exists...
            // ...but the start base (-45, 0) and all spawns are on clear cells.
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f } };
            Assert.True(Validator.Validate(m).Ok);
        }

        [Fact]
        public void ResourceNodeOnBlockedCell_FailsClosed()
        {
            // A resource node on a painted blocked cell is unreachable by gatherers (soft-lock) — must fail closed
            // (checked before the collection-model/resource-type gates, so a minimal node suffices).
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64();
            m.ResourceNodes = new[] { new ScenarioResourceNode { X = BlockedX, Z = BlockedZ } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void PrePlacedBuildingOnBlockedCell_FailsClosed()
        {
            var m = Valid();
            m.PathabilityBlocked = BlockedCell64_64();
            m.Buildings = new[] { new ScenarioBuilding { Slot = 0, X = BlockedX, Z = BlockedZ } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(-1f)]
        [InlineData(40000f)] // ≥ the 16.16 Range ceiling (32768) — would overflow Fixed.FromFloat
        public void SlopeBlockThresholdOutOfRange_FailsClosed(float bad)
        {
            var m = Valid();
            m.SlopeAutoBlock = true;
            m.SlopeBlockThreshold = bad;
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("slope_block_threshold", r.Error!);
        }

        [Fact]
        public void SlopeBlockThresholdInRange_Passes()
        {
            var m = Valid();
            m.SlopeAutoBlock = true;
            m.SlopeBlockThreshold = 5f; // a sane slope threshold
            Assert.True(Validator.Validate(m).Ok);
        }
    }
}
