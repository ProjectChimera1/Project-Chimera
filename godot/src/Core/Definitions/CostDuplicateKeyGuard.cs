#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-537 — the raw-<see cref="JsonDocument"/> duplicate-key pass for every authored <c>cost</c> block, i.e. the
    /// <see cref="UnitDefinition.Cost"/> map (inherited by <see cref="BuildingDefinition"/>) and the
    /// <see cref="ResearchLevel.Cost"/> map. Both are <c>Dictionary&lt;string,int&gt;</c> DTO properties, and
    /// System.Text.Json binds a repeated JSON key LAST-WINS — so <c>"cost": { "ore": 50, "ore": 5 }</c> deserializes
    /// to <c>{ ore: 5 }</c> and the creator's first amount is gone with no diagnostic. The DESERIALIZED model cannot
    /// see the collision (one repeated key still yields one dictionary entry), so the only place the fault is visible
    /// is the RAW text — exactly the shape DW-227 added for <see cref="ProjectChimera.Combat.DamageTable"/>'s nested
    /// multiplier dictionaries, minus its enum-key alias handling (these keys are plain strings, and a
    /// <c>Dictionary&lt;string,int&gt;</c> uses the default ORDINAL comparer, so <c>"ore"</c> and <c>"Ore"</c> are two
    /// distinct entries, not a collision — see <see cref="ScanCostObject"/>).
    ///
    /// <para><b>Shape.</b> Mirrors <see cref="ResourceCostValidator"/>/<see cref="ResearchValidator"/>: pure C#, no
    /// logging, NO THROW — the caller decides. List-all (every duplicate in the document is reported, not just the
    /// first), and the messages use the same <c>{kind} '{id}'.cost</c> located wording those validators emit so a
    /// creator reads one consistent error format. <see cref="FactionDefinition.LoadFromFile"/> feeds the result into
    /// the SAME aggregate <c>errors</c> channel every other content check throws with.</para>
    ///
    /// <para><b>Shapes understood.</b> A FACTION root (<c>units[]</c> / <c>buildings[]</c> / <c>research[].levels[]</c>)
    /// and a BARE single-definition root (the unit/building/research JSON panes and the LLM draft parsers deserialize
    /// one def straight from a root object). Nothing is matched by blind name-recursion — only these declared
    /// structural paths are walked, so a <c>cost</c> key that ever appears somewhere unrelated is not swept up.</para>
    ///
    /// <para><b>Duplicate ARRAY/OBJECT keys.</b> Every named property is walked at EVERY occurrence rather than via a
    /// first-match lookup: if a document repeats <c>"units"</c> (itself a last-wins discard) a duplicate cost key in
    /// either copy is still reported. Fail closed — the document is malformed either way.</para>
    ///
    /// <para>Load-time only; never runs inside a tick.</para>
    /// </summary>
    public static class CostDuplicateKeyGuard
    {
        /// <summary>Raw-document options that accept exactly the dialect <see cref="ContentJson.LenientOptions"/>
        /// accepts (comments skipped, trailing commas allowed), so this pass can always re-walk text the serializer
        /// already parsed. Same rationale as <c>DamageTable</c>'s DW-227 <c>_docOpts</c>.</summary>
        private static readonly JsonDocumentOptions _docOpts = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Scan <paramref name="json"/> for duplicate keys inside any authored <c>cost</c> block (and for a repeated
        /// <c>cost</c> property, whose earlier map is discarded whole — the analogue of DW-227's duplicate
        /// <c>multipliers</c> object). Returns every located error; empty when the document carries none.
        ///
        /// <para>Never throws. Callers run this AFTER a successful deserialize, so the re-parse below cannot fail;
        /// if it somehow did, that is a mirror drift between this pass and the serializer's dialect and is reported
        /// as an error rather than silently skipped (fail closed — DW-227's convention).</para>
        /// </summary>
        /// <param name="json">The raw content text (faction file, or a single unit/building/research document).</param>
        public static IReadOnlyList<string> Scan(string? json)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(json)) return errors;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json!, _docOpts);
            }
            catch (JsonException ex)
            {
                errors.Add($"cost duplicate-key pass: the document could not be re-read as raw JSON ({ex.Message}).");
                return errors;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return errors;   // a non-object root binds to nothing here; the deserializer already ruled on it

                // ── Faction-shaped root ──────────────────────────────────────────────────────
                foreach (JsonProperty prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("units"))          ScanDefinitionArray(errors, prop.Value, "unit");
                    else if (prop.NameEquals("buildings")) ScanDefinitionArray(errors, prop.Value, "building");
                    else if (prop.NameEquals("research"))  ScanResearchArray(errors, prop.Value);
                }

                // ── Bare single-definition root ──────────────────────────────────────────────
                // A faction root carries neither a root-level "cost" nor "levels", so both calls are no-ops there.
                ScanCostBearingObject(errors, root, $"definition {Label(root, 0)}");
                ScanLevels(errors, root, $"research {Label(root, 0)}");
            }

            return errors;
        }

        /// <summary>Walk a <c>units</c>/<c>buildings</c> array, scanning each element's <c>cost</c> block. The
        /// <paramref name="kind"/> label keeps the located message accurate ("unit"/"building"), matching
        /// <see cref="ResourceCostValidator"/>'s reason for walking Buildings as its own list.</summary>
        private static void ScanDefinitionArray(List<string> errors, JsonElement array, string kind)
        {
            if (array.ValueKind != JsonValueKind.Array) return;

            int index = 0;
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                    ScanCostBearingObject(errors, element, $"{kind} {Label(element, index)}");
                index++;   // counted for EVERY element (including a malformed null) so the #index label is the real one
            }
        }

        /// <summary>Walk a <c>research</c> array, scanning each entry's level ladder.</summary>
        private static void ScanResearchArray(List<string> errors, JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array) return;

            int index = 0;
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                    ScanLevels(errors, element, $"research {Label(element, index)}");
                index++;
            }
        }

        /// <summary>Scan every <c>levels[]</c> entry of one research object for a duplicate cost key. Every
        /// <c>levels</c> occurrence is walked (see the type docs on repeated array keys).</summary>
        private static void ScanLevels(List<string> errors, JsonElement researchObject, string owner)
        {
            foreach (JsonProperty prop in researchObject.EnumerateObject())
            {
                if (!prop.NameEquals("levels") || prop.Value.ValueKind != JsonValueKind.Array) continue;

                int index = 0;
                foreach (JsonElement level in prop.Value.EnumerateArray())
                {
                    if (level.ValueKind == JsonValueKind.Object)
                        ScanCostBearingObject(errors, level, $"{owner}.levels[{index}]");
                    index++;
                }
            }
        }

        /// <summary>Scan one cost-bearing object (a unit/building def, or one research level). Reports a REPEATED
        /// <c>cost</c> property — whose earlier map System.Text.Json discards whole — and then the keys of every
        /// occurrence, so a duplicate inside a discarded copy is still surfaced.</summary>
        private static void ScanCostBearingObject(List<string> errors, JsonElement owner, string ownerPath)
        {
            bool seenCost = false;
            foreach (JsonProperty prop in owner.EnumerateObject())
            {
                if (!prop.NameEquals("cost")) continue;   // exact/ordinal: the lenient loader is case-SENSITIVE

                if (seenCost)
                    errors.Add($"{ownerPath}: duplicate 'cost' object — the earlier cost map is silently discarded.");
                seenCost = true;

                ScanCostObject(errors, prop.Value, $"{ownerPath}.cost");
            }
        }

        /// <summary>Report every repeated key inside one <c>cost</c> object. ORDINAL comparison, deliberately: the
        /// bound <c>Dictionary&lt;string,int&gt;</c> uses the default ordinal comparer, so <c>"ore"</c>/<c>"Ore"</c>
        /// really are two surviving entries (the second is then rejected as an unknown resource id by
        /// <see cref="ResourceCostValidator"/>/<see cref="ResearchValidator"/>) — flagging that pair as a duplicate
        /// here would be a FALSE positive, the mirror-image of DW-227's enum-key aliasing.</summary>
        private static void ScanCostObject(List<string> errors, JsonElement cost, string path)
        {
            if (cost.ValueKind != JsonValueKind.Object) return;   // a wrong-typed cost was rejected by the deserializer

            var seen = new HashSet<string>(StringComparer.Ordinal);   // membership only — never enumerated
            foreach (JsonProperty key in cost.EnumerateObject())
            {
                if (!seen.Add(key.Name))
                    errors.Add($"{path}: duplicate resource key '{key.Name}' — the earlier amount is silently " +
                               "discarded (a repeated JSON key binds last-wins).");
            }
        }

        /// <summary>The located label for one definition: its authored <c>id</c> when it has a non-empty string one
        /// (the LAST such property, matching the same last-wins binding this whole pass exists to expose), else its
        /// positional <c>#index</c> so an id-less entry is still findable.</summary>
        private static string Label(JsonElement element, int index)
        {
            string? id = null;
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.NameEquals("id") && prop.Value.ValueKind == JsonValueKind.String)
                    id = prop.Value.GetString();
            }
            return string.IsNullOrEmpty(id) ? $"#{index}" : $"'{id}'";
        }
    }
}
