#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 6.8 — <see cref="TechTreeChecker.BuildingTypeFromId"/>, the single reverse of
    /// <see cref="TechTreeChecker.BuildingTypeId"/> and the ONE id↔enum source the applier/editor route through.
    /// Round-trips against <see cref="TechTreeChecker.BuildingTypeId"/> for the 5 enum-backed built-ins and returns
    /// <c>null</c> for a custom / empty / unknown id (a <see cref="BuildingType.Custom"/> building with no enum member).
    /// </summary>
    public class BuildingTypeFromIdTests
    {
        [Theory]
        [InlineData(BuildingType.CommandCenter)]
        [InlineData(BuildingType.Barracks)]
        [InlineData(BuildingType.ArcheryRange)]
        [InlineData(BuildingType.SiegeWorkshop)]
        [InlineData(BuildingType.Aviary)]
        public void RoundTrips_AgainstBuildingTypeId_ForEveryBuiltIn(BuildingType type)
        {
            string id = TechTreeChecker.BuildingTypeId(type);
            Assert.Equal(type, TechTreeChecker.BuildingTypeFromId(id));
        }

        [Fact]
        public void Custom_HasNoId_AndNoReverseMapping()
        {
            // Custom's canonical id is "" (no dedicated id), and "" reverses to null (not Custom) — a Custom building
            // is identified by its AUTHORED id, never by the empty enum-id.
            Assert.Equal("", TechTreeChecker.BuildingTypeId(BuildingType.Custom));
            Assert.Null(TechTreeChecker.BuildingTypeFromId(""));
        }

        [Theory]
        [InlineData("watchtower")]
        [InlineData("sky_forge")]
        [InlineData("Barracks")]   // PascalCase enum NAME is not a canonical id — ids are snake_case
        [InlineData("command_centre")] // near-miss spelling
        [InlineData(null)]
        public void AuthoredOrUnknownId_ReturnsNull(string? id)
            => Assert.Null(TechTreeChecker.BuildingTypeFromId(id));
    }
}
