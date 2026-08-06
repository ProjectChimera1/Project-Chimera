#nullable enable
using System;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-62 — <see cref="FactionDefinition.LoadFromFile"/> must reject a MALFORMED JSON VALUE through the same
    /// located, aggregated <see cref="InvalidOperationException"/> channel every content check already uses
    /// (<see cref="ResourceCostValidator"/>/<see cref="TechTreeValidator"/>/<see cref="BuildingDefinitionValidator"/>
    /// via <see cref="FactionValidator"/>), never as a raw <see cref="JsonException"/>.
    ///
    /// <para><b>Why this matters (the defect these tests pin).</b> The loader's whole contract is "one exception type,
    /// message = every located error joined by newlines". A parse failure was the one path that broke it: a bad value
    /// for ANY field escaped as <see cref="JsonException"/>, a type NO caller of this method catches by name. The
    /// nearest reachable trigger is a <c>cost</c> map amount authored as <c>null</c> against the non-nullable
    /// <c>Dictionary&lt;string,int&gt;</c> value type — the first Dictionary-typed authored field, so the first place
    /// the gap surfaces through a nested value rather than a top-level scalar. Without the DW-62 fix every
    /// <c>Assert.Throws&lt;InvalidOperationException&gt;</c> below fails with an unhandled <see cref="JsonException"/>.</para>
    ///
    /// Godot-free (Tier-1): writes real files to the OS temp directory, exactly like
    /// <c>FactionValidatorTests</c>'s LoadFromFile coverage.
    /// </summary>
    public class FactionLoadParseFailureTests
    {
        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_faction_parsefail_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>A minimal faction that passes <see cref="FactionValidator.Validate"/>; <paramref name="unitBody"/>
        /// supplies the ONE unit's fields so each test mutates exactly one value away from well-formed.</summary>
        private static string FactionJson(string unitBody) => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [
            { {{unitBody}} }
          ],
          "buildings": []
        }
        """;

        private const string ValidUnitBody =
            "\"id\": \"worker\", \"display_name\": \"Worker\", \"category\": \"Worker\", \"hp\": 50";

        // ── The named DW-62 case: a nested malformed value inside the cost map ───────────────

        [Fact]
        public void LoadFromFile_NullCostAmount_ThrowsLocatedInvalidOperationException_NotRawJsonException()
        {
            string path = WriteTempFaction(FactionJson(ValidUnitBody + ", \"cost\": { \"ore\": null }"));
            try
            {
                // Exact-type assertion: a JsonException (the pre-DW-62 behavior) is NOT an InvalidOperationException,
                // so this line is what fails without the fix.
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));

                Assert.Contains(path, ex.Message);                    // located: names the offending file
                Assert.Contains("malformed faction JSON", ex.Message); // classified as a content fault
                Assert.Contains("cost", ex.Message);                   // located: System.Text.Json's own JSON path
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ParseFailure_PreservesTheJsonExceptionAsInnerException()
        {
            // Folding into the aggregate channel must not DESTROY the diagnostic — the original parse exception
            // (with its Path/LineNumber/BytePositionInLine properties) is still reachable for anything that wants it.
            string path = WriteTempFaction(FactionJson(ValidUnitBody + ", \"cost\": { \"ore\": null }"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.IsAssignableFrom<JsonException>(ex.InnerException);
            }
            finally { File.Delete(path); }
        }

        // ── The same hole via a top-level scalar and via outright bad syntax ─────────────────

        [Fact]
        public void LoadFromFile_WrongTypedScalar_ThrowsLocatedInvalidOperationException()
        {
            // The pre-existing half of DW-62: a malformed scalar (hp is a float) threw identically long before the
            // cost map existed. Pinned so the fix is proven general, not cost-map-specific.
            string path = WriteTempFaction(FactionJson(
                "\"id\": \"worker\", \"display_name\": \"Worker\", \"category\": \"Worker\", \"hp\": \"abc\""));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains(path, ex.Message);
                Assert.Contains("malformed faction JSON", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_TruncatedDocument_ThrowsLocatedInvalidOperationException()
        {
            string path = WriteTempFaction("{ \"id\": \"test_faction\", \"display_name\": \"Test");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains(path, ex.Message);
                Assert.Contains("malformed faction JSON", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── The parse error must not be buried under validator noise ─────────────────────────

        [Fact]
        public void LoadFromFile_ParseFailure_ReportsOnlyTheParseError_NoValidatorNoise()
        {
            // A failed parse yields no model, so the validators must NOT also run over a default-constructed shell:
            // that would append a pile of unrelated "empty id"-class lines above/below the one line naming the real
            // cause. Exactly one aggregated line, and it is the parse line.
            string path = WriteTempFaction(FactionJson(ValidUnitBody + ", \"cost\": { \"ore\": null }"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                string[] lines = ex.Message.Split('\n');
                Assert.Single(lines);
                Assert.Contains("malformed faction JSON", lines[0]);
            }
            finally { File.Delete(path); }
        }

        // ── The catch must not over-reach: well-formed input still loads unchanged ───────────

        [Fact]
        public void LoadFromFile_WellFormedCostMap_StillLoadsUnchanged()
        {
            string path = WriteTempFaction(FactionJson(ValidUnitBody + ", \"cost\": { \"ore\": 50 }"));
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Equal("test_faction", def.Id);
                Assert.NotNull(def.Units[0].Cost);
                Assert.Equal(50, def.Units[0].Cost!["ore"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromFile_ContentRejection_StillThrowsTheValidatorMessage_NotAParseMessage()
        {
            // The parse guard sits BEFORE the validators; a well-formed file with BAD CONTENT must still surface the
            // validator's located error, never the new parse wording (i.e. the guard did not shadow the old path).
            string path = WriteTempFaction(FactionJson(ValidUnitBody + ", \"cost\": { \"unobtainium\": 5 }"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("unobtainium", ex.Message);
                Assert.DoesNotContain("malformed faction JSON", ex.Message);
                Assert.Null(ex.InnerException);
            }
            finally { File.Delete(path); }
        }
    }
}
