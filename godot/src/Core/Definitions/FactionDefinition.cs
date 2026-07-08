#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Top-level faction definition loaded from JSON.
    /// References all unit and building types that belong to this faction.
    /// </summary>
    public class FactionDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>RGBA as [r, g, b, a] floats 0–1. Used for unit tint if no texture.</summary>
        [JsonPropertyName("color")]
        public float[] Color { get; set; } = [0.2f, 0.5f, 1.0f, 1.0f];

        [JsonPropertyName("units")]
        public List<UnitDefinition> Units { get; set; } = new();

        [JsonPropertyName("buildings")]
        public List<BuildingDefinition> Buildings { get; set; } = new();

        // ── Lookup helpers ──────────────────────────────────────────────────────

        /// <summary>Find a building definition by ID, or null if not found.</summary>
        public BuildingDefinition? GetBuilding(string id)
        {
            foreach (var b in Buildings)
                if (b.Id == id) return b;
            return null;
        }

        /// <summary>Find a unit definition by ID, or null if not found.</summary>
        public UnitDefinition? GetUnit(string id)
        {
            foreach (var u in Units)
                if (u.Id == id) return u;
            return null;
        }

        /// <summary>
        /// Index of the unit with the given ID within the Units list, or -1 if not found.
        /// Used to tag each entity's <c>EntityWorld.MeshType</c> so MultiMeshBridge can
        /// render a distinct mesh per unit type (the index maps 1:1 to the bridge's
        /// per-type MultiMeshInstance3D slots).
        /// </summary>
        public int IndexOfUnit(string id)
        {
            for (int i = 0; i < Units.Count; i++)
                if (Units[i].Id == id) return i;
            return -1;
        }

        /// <summary>Find the first unit with the given category string (case-insensitive), or null.</summary>
        public UnitDefinition? GetUnitByCategory(string category)
        {
            foreach (var u in Units)
                if (string.Equals(u.Category, category, System.StringComparison.OrdinalIgnoreCase))
                    return u;
            return null;
        }

        /// <summary>
        /// Every unit whose category matches (case-insensitive), in ascending <see cref="Units"/>-list order,
        /// each paired with its list index. The index is the SAME coordinate as <see cref="IndexOfUnit"/> /
        /// <c>EntityWorld.MeshType</c>, so callers can persist a chosen unit by index and resolve it back with a
        /// plain <c>Units[idx]</c>. Introduced for the per-unit production picker (Story 2.8) so a building can
        /// train ANY unit of its category, not just the first. Deterministic ascending iteration — no
        /// <c>Dictionary</c>/<c>HashSet</c>, no sort. Empty list when no unit has that category.
        /// </summary>
        public List<(int Index, UnitDefinition Def)> GetUnitsByCategory(string category)
        {
            var matches = new List<(int, UnitDefinition)>();
            for (int i = 0; i < Units.Count; i++)
                if (string.Equals(Units[i].Category, category, System.StringComparison.OrdinalIgnoreCase))
                    matches.Add((i, Units[i]));
            return matches;
        }

        /// <summary>
        /// First unit in the list — used as the default mesh when a MultiMesh
        /// renders "all units of this faction" without per-type differentiation.
        /// </summary>
        public UnitDefinition? PrimaryUnit => Units.Count > 0 ? Units[0] : null;

        // ── Deserialization ─────────────────────────────────────────────────────

        /// <summary>The lenient JSON options the faction/unit loader uses (comments + trailing commas tolerated; no
        /// Disallow, no converters). Public so tests and other unit-definition readers share ONE source of truth
        /// instead of a hand-rolled replica that could silently drift from this loader (Story 2.7 review).</summary>
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Load a FactionDefinition from a JSON file on disk.
        /// Pass the absolute OS path (not a res:// path — call from presentation layer after
        /// resolving with ProjectSettings.GlobalizePath).
        ///
        /// Story 4.1 (AC1): after deserializing, every <see cref="Buildings"/> entry runs through
        /// <see cref="BuildingDefinitionValidator"/>. A building missing <c>construction_time</c>/<c>supply_bonus</c>/
        /// <c>produces_category</c> fails the WHOLE load — throws with every located error (across every bad building)
        /// joined by newlines, so a creator sees all offending fields at once instead of fixing one and re-running to
        /// find the next. Units remain unvalidated at load (unchanged — Story 3.4 gates units only at the editor's
        /// Save/Playtest, not at faction load).
        /// </summary>
        public static FactionDefinition LoadFromFile(string absolutePath)
        {
            string json = File.ReadAllText(absolutePath);
            FactionDefinition def = JsonSerializer.Deserialize<FactionDefinition>(json, JsonOptions)
                                     ?? new FactionDefinition();

            var errors = new List<string>();
            foreach (BuildingDefinition b in def.Buildings)
            {
                BuildingValidationResult result = BuildingDefinitionValidator.Validate(b);
                if (!result.Ok)
                    foreach ((string _, string message) in result.Errors)
                        errors.Add(message);
            }
            if (errors.Count > 0)
                throw new System.InvalidOperationException(string.Join("\n", errors));

            return def;
        }
    }
}
