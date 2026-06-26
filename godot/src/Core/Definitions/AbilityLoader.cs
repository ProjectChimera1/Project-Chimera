#nullable enable
using System.IO;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The fail-closed ability loader (Story 2.3, AC2 / AC6). Folds every parse error into a LOCATED
    /// <see cref="AbilityValidationResult"/> — it NEVER returns null and NEVER lets a <see cref="JsonException"/>
    /// escape its boundary (the architecture's loader contract: parse errors become located results, not throws).
    /// Deserializes through the single <see cref="ContentJson.Options"/>, then runs the <see cref="AbilityValidator"/>
    /// gate; nothing runnable escapes a failed validation.
    /// </summary>
    public static class AbilityLoader
    {
        /// <summary>
        /// Load + validate one ability from a JSON string. <paramref name="sourceLabel"/> labels the located error
        /// when the document is too malformed to recover an <c>id</c> (e.g. a file name). Returns a located failure
        /// rather than throwing on any malformed/invalid input.
        /// </summary>
        public static AbilityValidationResult Load(string json, string sourceLabel)
        {
            AbilityDefinition? def;
            try
            {
                def = JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options);
            }
            catch (JsonException ex)
            {
                // Best-effort id for the located prefix; fall back to the source label when the doc is unparseable.
                string id = TryPeekId(json) ?? sourceLabel;
                // The converter/FixedJsonConverter messages already carry the "<path>: <reason>" tail (e.g.
                // "effect.children[2]: unknown kind 'lua'"), so prefixing "ability '<id>'." yields the AC2 shape.
                return AbilityValidationResult.Fail($"ability '{id}'.{ex.Message}");
            }

            if (def is null)
                return AbilityValidationResult.Fail($"ability '{sourceLabel}': JSON deserialized to null.");

            return new AbilityValidator().Validate(def);
        }

        /// <summary>
        /// Load + validate one ability from a JSON file on disk. Pass an absolute OS path (resolve a <c>res://</c>
        /// path with <c>ProjectSettings.GlobalizePath</c> in the presentation layer first). A missing file is a
        /// located failure (never a throw / null). The file name is the source label.
        /// </summary>
        public static AbilityValidationResult LoadFromFile(string absolutePath)
        {
            string label = Path.GetFileName(absolutePath);
            if (!File.Exists(absolutePath))
                return AbilityValidationResult.Fail($"ability '{label}': file not found at '{absolutePath}'.");
            return Load(File.ReadAllText(absolutePath), label);
        }

        /// <summary>
        /// Best-effort extraction of the top-level <c>"id"</c> string for error labeling ONLY — never throws.
        /// Returns null when the document is malformed or has no string id.
        /// </summary>
        private static string? TryPeekId(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json,
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("id", out JsonElement idEl)
                    && idEl.ValueKind == JsonValueKind.String)
                {
                    return idEl.GetString();
                }
            }
            catch (JsonException) { /* malformed — fall back to the source label */ }
            return null;
        }
    }
}
