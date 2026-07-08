#nullable enable
using System;
using System.IO;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The fail-closed item loader (Story 3.15, AR-39) — mirrors <see cref="AbilityLoader"/>. Folds every parse error
    /// into a LOCATED <see cref="ItemValidationResult"/>; it NEVER returns null and NEVER lets a
    /// <see cref="JsonException"/> escape its boundary. Deserializes through the single <c>ContentJson.Options</c>,
    /// then runs the <see cref="ItemDefinitionValidator"/> gate; nothing runnable escapes a failed validation.
    /// </summary>
    public static class ItemLoader
    {
        /// <summary>Load + validate one item from a JSON string. <paramref name="sourceLabel"/> labels the located
        /// error when the document is too malformed to recover an <c>id</c>. Returns a located failure rather than
        /// throwing on any malformed/invalid input.</summary>
        public static ItemValidationResult Load(string json, string sourceLabel)
        {
            if (json is null)
                return ItemValidationResult.Fail($"item '{sourceLabel}': JSON source is null.");

            ItemDefinition? def;
            try
            {
                def = JsonSerializer.Deserialize<ItemDefinition>(json, ContentJson.Options);
            }
            catch (JsonException ex)
            {
                string id = TryPeekId(json) ?? sourceLabel;
                string reason = ex.Message;
                int pathIdx = reason.IndexOf(" Path: ", StringComparison.Ordinal);
                if (pathIdx >= 0) reason = reason.Substring(0, pathIdx);
                return ItemValidationResult.Fail($"item '{id}'.{reason}");
            }

            if (def is null)
                return ItemValidationResult.Fail($"item '{sourceLabel}': JSON deserialized to null.");

            return new ItemDefinitionValidator().Validate(def);
        }

        /// <summary>Load + validate one item from a JSON file on disk. Pass an absolute OS path (resolve a
        /// <c>res://</c> path with <c>ProjectSettings.GlobalizePath</c> in the presentation layer first). A missing
        /// file is a located failure (never a throw / null). The file name is the source label.</summary>
        public static ItemValidationResult LoadFromFile(string absolutePath)
        {
            string label = Path.GetFileName(absolutePath);
            if (!File.Exists(absolutePath))
                return ItemValidationResult.Fail($"item '{label}': file not found at '{absolutePath}'.");
            string json;
            try
            {
                json = File.ReadAllText(absolutePath);
            }
            catch (IOException ex)
            {
                return ItemValidationResult.Fail($"item '{label}': could not read file ({ex.Message}).");
            }
            catch (UnauthorizedAccessException ex)
            {
                return ItemValidationResult.Fail($"item '{label}': access denied reading file ({ex.Message}).");
            }
            return Load(json, label);
        }

        /// <summary>Best-effort extraction of the top-level <c>"id"</c> string for error labeling ONLY — never throws.</summary>
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
