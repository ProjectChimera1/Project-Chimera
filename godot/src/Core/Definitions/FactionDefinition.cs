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

        /// <summary>Faction-wide, timed, repeatable research/upgrade entries (Story 4.8) — mirrors
        /// <see cref="Units"/>/<see cref="Buildings"/>'s place on this type. Content-only this story: no runtime
        /// order path consumes it yet (Story 4.9).</summary>
        [JsonPropertyName("research")]
        public List<ResearchDefinition> Research { get; set; } = new();

        /// <summary>The AI opponent's build/behavior preset id for this faction (Story 5.2, FR-18 data). A CLOSED
        /// string set owned by <see cref="FactionValidator"/> (currently seeded with exactly one member,
        /// <c>"balanced"</c> — concrete preset ids for alpha/beta are Story 5.3's job). Defaults to <c>"balanced"</c>
        /// (NOT empty) so an unauthored faction is already a valid closed-set member; an authored empty/unknown value
        /// is still a located <see cref="FactionValidator"/> FAIL. Distinct from <c>AiDifficulty</c>
        /// (<c>src/AI/AiOpponentSystem.cs</c>) — that is an unrelated per-match difficulty knob, never reused here.</summary>
        [JsonPropertyName("ai_preset")]
        public string AiPreset { get; set; } = "balanced";

        /// <summary>The faction's signature-mechanic id (Story 5.2, AR-12) — descriptor-only storage, never wired to
        /// any D1 modifier/effect execution here (Story 5.4's job). Already present as a bare, silently-ignored
        /// string in <c>alpha_faction.json</c>/<c>beta_faction.json</c> today (Story 2.10); this field is the first
        /// consumer. Defaults to <c>""</c> (no signature mechanic authored) — optional, never validated as required.</summary>
        [JsonPropertyName("signature_mechanic")]
        public string SignatureMechanicId { get; set; } = "";

        /// <summary>Human-readable display text for <see cref="SignatureMechanicId"/> (Story 5.2, AR-12).
        /// Descriptor-only — optional, defaults to <c>null</c> (not authored).</summary>
        [JsonPropertyName("signature_mechanic_display")]
        public string? SignatureMechanicDisplay { get; set; } = null;

        /// <summary>The D1 modifier/effect-graph id <see cref="SignatureMechanicId"/> will eventually reference
        /// (Story 5.2, AR-12) — a storage slot only; no runtime execution path reads it yet (Story 5.4). Optional,
        /// defaults to <c>null</c> (not authored).</summary>
        [JsonPropertyName("signature_mechanic_effect_id")]
        public string? SignatureMechanicEffectId { get; set; } = null;

        /// <summary>The faction's hero unit reference (Story 5.2, AR-12) — expected to resolve against
        /// <see cref="Units"/>' <c>id</c>s once a hero authoring flow exists; not cross-checked by
        /// <see cref="FactionValidator"/> this story. Optional, defaults to <c>null</c> (no hero authored).</summary>
        [JsonPropertyName("hero_unit_id")]
        public string? HeroUnitId { get; set; } = null;

        /// <summary>Whether this faction opts into cross-match persistence (Story 5.2, AR-12) — a flag only; no
        /// runtime persistence path reads it yet. Optional, defaults to <c>false</c>.</summary>
        [JsonPropertyName("persistence_enabled")]
        public bool PersistenceEnabled { get; set; } = false;

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

        /// <summary>Find a research definition by ID, or null if not found. Mirrors <see cref="GetBuilding"/>. A null
        /// <see cref="Research"/> list (malformed JSON <c>"research": null</c>, which <see cref="ResearchValidator"/>
        /// tolerates so the file loads) OR a null element inside it is skipped, never an NRE (second-review-pass fix:
        /// the element case was already guarded, but a null list itself would still have NRE'd this getter for a file
        /// that loaded without error).</summary>
        public ResearchDefinition? GetResearch(string id)
        {
            if (Research == null) return null;
            foreach (var r in Research)
                if (r != null && r.Id == id) return r;
            return null;
        }

        /// <summary>Index of the research entry with the given ID within the <see cref="Research"/> list, or -1 if
        /// not found. Mirrors <see cref="IndexOfUnit"/>. A null <see cref="Research"/> list (malformed JSON
        /// <c>"research": null</c>) OR a null element inside it is skipped, never an NRE (second-review-pass fix —
        /// same latent-NRE gap as <see cref="GetResearch"/>).</summary>
        public int IndexOfResearch(string id)
        {
            if (Research == null) return -1;
            for (int i = 0; i < Research.Count; i++)
                if (Research[i] != null && Research[i].Id == id) return i;
            return -1;
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
        /// find the next. Beyond that check and Story 4.2's prerequisite lint below, units remain unvalidated at
        /// load (unchanged — Story 3.4 gates full unit-field validation only at the editor's Save/Playtest, not
        /// at faction load).
        ///
        /// Story 4.2 (AC1/AC2): additively, <see cref="TechTreeValidator"/> runs over the SAME aggregate
        /// <c>errors</c> list — a duplicate building id, a <c>Buildings[]</c>/<c>Units[]</c> <c>prerequisites</c>
        /// entry referencing an unknown building id, or a prerequisite cycle among buildings (direct or self),
        /// each fails the whole load exactly like a missing required building field, list-all, joined by newlines.
        ///
        /// Story 4.3 (AC2): additively, <see cref="ResourceCostValidator"/> runs over the same aggregate list — an
        /// authored <c>cost</c> map entry naming a resource id with no runtime backing (anything outside
        /// <c>{"ore","crystal"}</c>) or an out-of-range amount fails the whole load, list-all, joined by newlines.
        ///
        /// Story 4.8: additively, <see cref="ResearchValidator"/> runs over the same aggregate list — a duplicate
        /// research id, an empty/malformed <see cref="ResearchDefinition.Levels"/> ladder, an out-of-range
        /// <see cref="ResearchDefinition.CancelRefundFraction"/>, a <see cref="ResearchDefinition.Prerequisites"/>/
        /// <see cref="BuildingDefinition.AvailableResearch"/> entry referencing an unknown id, a research→research
        /// prerequisite cycle, or an over-cap research count each fails the whole load exactly like every check
        /// above, list-all (the cycle check excepted — first-fail, same convention as
        /// <see cref="TechTreeValidator"/>'s), joined by newlines. Content-only: this does NOT mint a
        /// <see cref="Validated{T}"/> (matches the real precedent set by the checks above, not the epic's general
        /// framing) and wires no runtime order path (Story 4.9 owns that).
        ///
        /// Story 5.2: the four checks above (Building-per-item, TechTree, ResourceCost, Research) are now relocated,
        /// unchanged, into <see cref="FactionValidator.Validate"/> — this method calls that ONE method instead of
        /// each inline, plus three new structural checks (ai_preset closed-set, color, duplicate-unit-id). Errors are
        /// still aggregated into the same <c>errors</c> list and thrown identically (joined by <c>\n</c>).
        /// Deliberately calls <see cref="FactionValidator.Validate"/>, NOT <see cref="FactionValidator.ValidateComplete"/>
        /// — the roster-completeness checks (missing <c>mesh_path</c>, missing required roles) are a legitimate
        /// mid-edit state that <see cref="BuildingCardPanel"/>/<see cref="UnitCardPanel"/>'s Save self-check (which
        /// also calls this method) must never reject; see <c>FactionValidator</c>'s own docs.
        /// </summary>
        public static FactionDefinition LoadFromFile(string absolutePath)
        {
            string json = File.ReadAllText(absolutePath);
            FactionDefinition def = JsonSerializer.Deserialize<FactionDefinition>(json, JsonOptions)
                                     ?? new FactionDefinition();

            var errors = new List<string>();
            FactionValidationResult result = FactionValidator.Validate(def);
            if (!result.Ok)
                foreach ((string _, string message) in result.Errors)
                    errors.Add(message);
            if (errors.Count > 0)
                throw new System.InvalidOperationException(string.Join("\n", errors));

            return def;
        }
    }
}
