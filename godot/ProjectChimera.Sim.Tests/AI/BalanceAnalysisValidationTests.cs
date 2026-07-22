#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.5 — proves <see cref="LLMService.ValidateBalanceReport"/> parses a well-formed suggestions payload into an
    /// editable <see cref="BalanceReport"/> and located-rejects every off-contract input: malformed/prose JSON, an unknown
    /// <c>field</c> (outside the closed tunable set), and an unknown <c>unit_id</c> (outside the roster). No data is mutated.
    /// </summary>
    public class BalanceAnalysisValidationTests
    {
        private static BalanceAnalysisContext Ctx() =>
            new() { UnitIds = new List<string> { "grunt", "archer" } };

        [Fact]
        public void ValidateBalanceReport_Valid_ReturnsReport()
        {
            string json =
                "{\"suggestions\":[" +
                "{\"unit_id\":\"grunt\",\"field\":\"attack_damage\",\"current\":10,\"proposed\":14,\"rationale\":\"weak\"}," +
                "{\"unit_id\":\"archer\",\"field\":\"hero.max_level\",\"current\":5,\"proposed\":6,\"rationale\":\"more growth\"}" +
                "]}";
            var (report, err) = LLMService.ValidateBalanceReport(json, Ctx());

            Assert.Null(err);
            Assert.NotNull(report);
            Assert.Equal(2, report!.Suggestions.Count);
            Assert.Equal("grunt", report.Suggestions[0].UnitId);
            Assert.Equal("attack_damage", report.Suggestions[0].Field);
            Assert.Equal(14, report.Suggestions[0].Proposed);
        }

        [Fact]
        public void ValidateBalanceReport_Prose_LocatedReject()
        {
            var (report, err) = LLMService.ValidateBalanceReport(
                "Sure Commander, here are some ideas: buff the grunt.", Ctx());
            Assert.Null(report);
            Assert.NotNull(err);
            Assert.Contains("Invalid JSON", err);
        }

        [Fact]
        public void ValidateBalanceReport_MalformedJson_LocatedReject()
        {
            var (report, err) = LLMService.ValidateBalanceReport(
                "{\"suggestions\":[{\"unit_id\":\"grunt\",", Ctx());
            Assert.Null(report);
            Assert.NotNull(err);
            Assert.Contains("Invalid JSON", err);
        }

        [Fact]
        public void ValidateBalanceReport_UnknownField_LocatedReject()
        {
            string json =
                "{\"suggestions\":[{\"unit_id\":\"grunt\",\"field\":\"wingspan\",\"proposed\":3,\"rationale\":\"x\"}]}";
            var (report, err) = LLMService.ValidateBalanceReport(json, Ctx());
            Assert.Null(report);
            Assert.NotNull(err);
            Assert.Contains("wingspan", err);   // names the offending field
            Assert.Contains("field", err);
        }

        [Fact]
        public void ValidateBalanceReport_UnknownUnitId_LocatedReject()
        {
            string json =
                "{\"suggestions\":[{\"unit_id\":\"dragon\",\"field\":\"attack_damage\",\"proposed\":30,\"rationale\":\"x\"}]}";
            var (report, err) = LLMService.ValidateBalanceReport(json, Ctx());
            Assert.Null(report);
            Assert.NotNull(err);
            Assert.Contains("dragon", err);   // names the offending unit id
            Assert.Contains("unit_id", err);
        }
    }
}
