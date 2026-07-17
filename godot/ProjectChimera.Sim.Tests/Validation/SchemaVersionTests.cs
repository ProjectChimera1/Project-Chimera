#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 (D3 versioning) — the <c>schema_version</c> / <c>checksum_algo_version</c> stamps: absent ⇒ v1
    /// legacy amnesty (loads normally, no migration); a value NEWER than the build understands is a located
    /// validator reject; <see cref="ScenarioSerializer.Serialize"/> stamps the current values on every save
    /// WITHOUT mutating the caller's model; and both stamps stay out of <see cref="CanonicalModelHash"/> (the
    /// re-save-neutrality half is pinned in the canonical-hash suite).
    /// </summary>
    public class SchemaVersionTests
    {
        private static ScenarioData ValidModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
        };

        [Fact]
        public void AbsentStamps_LoadUnderV1Amnesty()
        {
            var legacy = ValidModel(); // SchemaVersion / ChecksumAlgoVersion both null
            ValidationResult r = new ScenarioValidator().Validate(legacy);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void CurrentStamps_Pass()
        {
            var m = ValidModel();
            m.SchemaVersion = ScenarioSerializer.CurrentSchemaVersion;
            m.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion;
            Assert.True(new ScenarioValidator().Validate(m).Ok);
        }

        [Fact]
        public void FutureSchemaVersion_RejectsLocated()
        {
            var m = ValidModel();
            m.SchemaVersion = ScenarioSerializer.CurrentSchemaVersion + 1;
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.schema_version", r.Error);
            Assert.Contains((ScenarioSerializer.CurrentSchemaVersion + 1).ToString(), r.Error);
        }

        [Fact]
        public void FutureChecksumAlgoVersion_RejectsLocated()
        {
            var m = ValidModel();
            m.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion + 1;
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.checksum_algo_version", r.Error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void ZeroOrNegativeSchemaVersion_RejectsLocated(int stamp)
        {
            // Review follow-up: only an ABSENT stamp gets the v1 amnesty — an explicit 0/negative is nonsense
            // (versions start at 1) and fails closed rather than loading as legacy.
            var m = ValidModel();
            m.SchemaVersion = stamp;
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.schema_version", r.Error);
            Assert.Contains(">= 1", r.Error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-7)]
        public void ZeroOrNegativeChecksumAlgoVersion_RejectsLocated(int stamp)
        {
            var m = ValidModel();
            m.ChecksumAlgoVersion = stamp;
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.checksum_algo_version", r.Error);
            Assert.Contains(">= 1", r.Error);
        }

        [Fact]
        public void Serialize_StampsCurrentValues_AndRoundTripsThem()
        {
            var legacy = ValidModel();
            string json = ScenarioSerializer.Serialize(legacy);
            Assert.Contains("\"schema_version\"", json);
            Assert.Contains("\"checksum_algo_version\"", json);

            ScenarioData back = System.Text.Json.JsonSerializer.Deserialize<ScenarioData>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new FixedJsonConverter() },
                })!;
            Assert.Equal(ScenarioSerializer.CurrentSchemaVersion, back.SchemaVersion);
            Assert.Equal(CanonicalModelHash.AlgoVersion, back.ChecksumAlgoVersion);
        }

        [Fact]
        public void Serialize_DoesNotMutateTheCallersModel()
        {
            var legacy = ValidModel();
            _ = ScenarioSerializer.Serialize(legacy);
            Assert.Null(legacy.SchemaVersion);        // swap-under-try/finally restored the original nulls
            Assert.Null(legacy.ChecksumAlgoVersion);

            var stamped = ValidModel();
            stamped.SchemaVersion = 1;
            stamped.ChecksumAlgoVersion = 3; // an old (legal) stamp survives the re-stamped serialization
            _ = ScenarioSerializer.Serialize(stamped);
            Assert.Equal(1, stamped.SchemaVersion);
            Assert.Equal(3, stamped.ChecksumAlgoVersion);
        }

        [Fact]
        public void StampedResave_DoesNotMoveTheCanonicalHash()
        {
            // The AC2 core: a legacy file re-saved (gaining stamps) hashes identically to its unstamped self.
            var legacy = ValidModel();
            ulong before = CanonicalModelHash.Compute(legacy);
            var restamped = ValidModel();
            restamped.SchemaVersion = ScenarioSerializer.CurrentSchemaVersion;
            restamped.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion;
            Assert.Equal(before, CanonicalModelHash.Compute(restamped));
        }
    }
}
