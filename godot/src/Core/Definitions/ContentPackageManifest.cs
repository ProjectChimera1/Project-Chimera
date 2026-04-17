#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Metadata header for a .chimera.zip content package.
    ///
    /// Stored as <c>manifest.json</c> at the root of every .chimera.zip file.
    /// This is what the content browser reads without extracting the full package:
    /// name, author, tags, description, and a thumbnail path.
    ///
    /// Wire format of the package (Phase 4 / mod.io distribution):
    /// <code>
    /// my_map.chimera.zip
    ///   manifest.json        ← this class
    ///   scenario.json        ← ScenarioData (full map layout)
    ///   thumbnail.png        ← 256×256 preview (optional)
    ///   factions/            ← custom faction JSON overrides (optional)
    ///     my_faction.json
    ///   models/              ← custom GLB models for custom units (optional)
    ///     heavy_tank.glb
    /// </code>
    /// </summary>
    public class ContentPackageManifest
    {
        // ── Identity ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Unique package ID — slug format (lowercase, hyphens, no spaces).
        /// e.g. "mirror-lake-competitive" or "my-first-map".
        /// Set once at publish time; changing it breaks existing subscriptions.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable map name shown in the content browser.</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>Short description (1–3 sentences). Shown on the detail card.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>Creator display name.</summary>
        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        // ── Version ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Semantic version string e.g. "1.0.0".
        /// Increment on any content change so mod.io creates a new file version.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Minimum game version this package requires (semver prefix match).
        /// e.g. "0.3" means any 0.3.x build is compatible.
        /// </summary>
        [JsonPropertyName("min_game_version")]
        public string MinGameVersion { get; set; } = "0.1";

        // ── Taxonomy ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Searchable tags. Suggested values:
        ///   Game Mode: "1v1", "2v2", "ffa", "coop", "tower-defense", "survival"
        ///   Theme: "desert", "snow", "forest", "lava", "sci-fi"
        ///   Size: "small", "medium", "large"
        ///   Difficulty: "beginner", "competitive", "custom-rules"
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        /// <summary>Number of player slots this map supports.</summary>
        [JsonPropertyName("player_count")]
        public int PlayerCount { get; set; } = 2;

        // ── Asset references ──────────────────────────────────────────────────────

        /// <summary>
        /// Path inside the zip to the scenario JSON.
        /// Default "scenario.json" — creators should not change this.
        /// </summary>
        [JsonPropertyName("scenario_file")]
        public string ScenarioFile { get; set; } = "scenario.json";

        /// <summary>
        /// Path inside the zip to the preview image (PNG, 256×256 recommended).
        /// Null or empty = no thumbnail.
        /// </summary>
        [JsonPropertyName("thumbnail_file")]
        public string? ThumbnailFile { get; set; } = "thumbnail.png";

        /// <summary>
        /// Custom faction JSON files bundled in this package.
        /// Paths are relative to the zip root, e.g. "factions/my_faction.json".
        /// </summary>
        [JsonPropertyName("faction_files")]
        public List<string> FactionFiles { get; set; } = new();

        // ── Stats (populated by the game or mod.io, not the creator) ─────────────

        /// <summary>UTC ISO-8601 timestamp when this package was first created.</summary>
        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>
        /// FNV-1a hash of the embedded scenario.json bytes.
        /// Written at pack time, verified at load time to detect corrupt packages.
        /// </summary>
        [JsonPropertyName("scenario_hash")]
        public uint ScenarioHash { get; set; }
    }
}
