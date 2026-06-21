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
        public List<UnitDefinition> Buildings { get; set; } = new();

        // ── Lookup helpers ──────────────────────────────────────────────────────

        /// <summary>Find a building definition by ID, or null if not found.</summary>
        public UnitDefinition? GetBuilding(string id)
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
        /// First unit in the list — used as the default mesh when a MultiMesh
        /// renders "all units of this faction" without per-type differentiation.
        /// </summary>
        public UnitDefinition? PrimaryUnit => Units.Count > 0 ? Units[0] : null;

        // ── Deserialization ─────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Load a FactionDefinition from a JSON file on disk.
        /// Pass the absolute OS path (not a res:// path — call from presentation layer after
        /// resolving with ProjectSettings.GlobalizePath).
        /// </summary>
        public static FactionDefinition LoadFromFile(string absolutePath)
        {
            string json = File.ReadAllText(absolutePath);
            return JsonSerializer.Deserialize<FactionDefinition>(json, _jsonOptions)
                   ?? new FactionDefinition();
        }
    }
}
