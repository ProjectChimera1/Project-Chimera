#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.6 — the validator fails closed (clear, pre-tick) when a start/unit/spawn_unit/resource position
    /// resolves onto a BLOCKING-prop or WATER footprint (same cell domain as the painted layer), and on malformed
    /// structural input: a duplicate camera name, a malformed water rect, an out-of-bounds prop, or a non-finite
    /// hash-folded float. A clear map with well-formed props/cameras/water passes.
    /// </summary>
    public class ScenarioValidatorPropsWaterTests
    {
        private const float BlockedX = 1f, BlockedZ = 1f; // world (1,1) → flow cell (64,64)

        private static ScenarioData Valid() => new ScenarioData
        {
            MapBounds = 120f,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
        };

        private static readonly ScenarioValidator Validator = new();

        [Fact]
        public void WellFormedPropsCamerasWater_Passes()
        {
            var m = Valid();
            m.Props   = new[] { new ScenarioProp { PropId = "tree", X = 10f, Z = 10f, BlocksPathing = true } };
            m.Cameras = new[] { new ScenarioCamera { Name = "a", X = 0, Y = 20, Z = 0, Fov = 60f } };
            m.Water   = new[] { new ScenarioWater { X = 30f, Z = 30f, W = 10f, H = 10f } };
            Assert.True(Validator.Validate(m).Ok);
        }

        [Fact]
        public void StartBaseOnBlockingProp_FailsClosed()
        {
            var m = Valid();
            m.PlayerSlots[0].BaseX = BlockedX;
            m.PlayerSlots[0].BaseZ = BlockedZ;
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = BlockedX, Z = BlockedZ, BlocksPathing = true } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void UnitOnWaterFootprint_FailsClosed()
        {
            var m = Valid();
            m.Water = new[] { new ScenarioWater { X = 0f, Z = 0f, W = 4f, H = 4f } }; // covers cell (64,64) → world (1,1)
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = BlockedX, Z = BlockedZ } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void ResourceNodeOnBlockingProp_FailsClosed()
        {
            var m = Valid();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = BlockedX, Z = BlockedZ, BlocksPathing = true } };
            m.ResourceNodes = new[] { new ScenarioResourceNode { X = BlockedX, Z = BlockedZ } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void SpawnUnitTriggerOnBlockingProp_FailsClosed()
        {
            var m = Valid();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = BlockedX, Z = BlockedZ, BlocksPathing = true } };
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
        public void NonBlockingProp_UnderStart_Passes()
        {
            // A NON-blocking prop under the start base does NOT block — it is cosmetic.
            var m = Valid();
            m.PlayerSlots[0].BaseX = BlockedX;
            m.PlayerSlots[0].BaseZ = BlockedZ;
            m.Props = new[] { new ScenarioProp { PropId = "flower", X = BlockedX, Z = BlockedZ, BlocksPathing = false } };
            Assert.True(Validator.Validate(m).Ok);
        }

        [Fact]
        public void DuplicateCameraName_FailsClosed()
        {
            var m = Valid();
            m.Cameras = new[]
            {
                new ScenarioCamera { Name = "dup", Fov = 60f },
                new ScenarioCamera { Name = "dup", Fov = 60f },
            };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("duplicate", r.Error!);
        }

        [Fact]
        public void EmptyCameraName_FailsClosed()
        {
            var m = Valid();
            m.Cameras = new[] { new ScenarioCamera { Name = "", Fov = 60f } };
            Assert.False(Validator.Validate(m).Ok);
        }

        [Theory]
        [InlineData(0f)]     // zero extent
        [InlineData(-5f)]    // negative extent
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void MalformedWaterRect_FailsClosed(float badW)
        {
            var m = Valid();
            m.Water = new[] { new ScenarioWater { X = 0f, Z = 0f, W = badW, H = 10f } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("water", r.Error!);
        }

        [Fact]
        public void OutOfBoundsProp_FailsClosed()
        {
            var m = Valid();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 9999f, Z = 0f } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("props", r.Error!);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void NonFiniteProp_FailsClosed(float bad)
        {
            var m = Valid();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = bad, Z = 0f } };
            Assert.False(Validator.Validate(m).Ok);
        }

        [Fact]
        public void NonFinitePropScale_FailsClosed()
        {
            var m = Valid();
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 1f, Z = 1f, Scale = -1f } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scale", r.Error!);
        }
    }
}
