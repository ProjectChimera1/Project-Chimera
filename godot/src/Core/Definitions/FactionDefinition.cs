#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>The faction's starting ore balance (Story 5.5, FR-17) — descriptor-only data written by the
        /// Faction Definer wizard's Starting Conditions step, mirroring <see cref="AiPreset"/>/
        /// <see cref="SignatureMechanicId"/>'s unwired-descriptor pattern (Story 5.2): no <c>ScenarioApplier</c>/
        /// <c>ScenarioPlayerSlot</c> change reads this yet — wiring it into actual match-start economy is left for a
        /// future story. Defaults to <c>200</c>, matching <c>ScenarioPlayerSlot.StartOre</c>'s existing default so an
        /// unauthored faction already carries the same starting balance a hand-authored scenario slot would.</summary>
        [JsonPropertyName("starting_ore")]
        public float StartingOre { get; set; } = 200f;

        /// <summary>The faction's starting crystal balance (Story 5.5, FR-17) — mirrors <see cref="StartingOre"/>'s
        /// descriptor-only posture exactly. Defaults to <c>0</c>, matching <c>ScenarioPlayerSlot.StartCrystal</c>'s
        /// existing default.</summary>
        [JsonPropertyName("starting_crystal")]
        public float StartingCrystal { get; set; } = 0f;

        // ── Runtime-only drop record (never authored, never serialized) ─────────

        /// <summary>
        /// DW-652 — the ids of units this faction DECLARED in its JSON but that
        /// <see cref="UnitTagValidator.ValidateAndDropUnits"/> REMOVED from <see cref="Units"/> for carrying an
        /// unknown tag. Runtime-only bookkeeping written by that validator at load; <c>[JsonIgnore]</c> so it never
        /// round-trips through the Faction Definer wizard's re-serialize (the <see cref="UnitDefinition"/>
        /// <c>[JsonIgnore]</c> resolved-index precedent).
        ///
        /// <para><b>Why it exists.</b> The tag drop runs AFTER the faction file loads and BEFORE the scenario gate, so
        /// a shipped map naming a dropped unit sees a roster that no longer declares it. Without this record the gate
        /// cannot tell that case (engine removed an author-declared unit — the map is innocent) from a genuine typo
        /// (an id nothing ever declared), and DW-240's fail-closed unit_id rule turns the first one into a
        /// WHOLE-SCENARIO reject that boots the fallback map. The record lets the gate degrade exactly that case to the
        /// per-entity drop the applier already performs, while a real dangling reference still fails closed.</para>
        /// </summary>
        [JsonIgnore]
        public List<string> TagDroppedUnitIds { get; } = new();

        /// <summary>
        /// DW-652 — record <paramref name="unitId"/> as dropped by the closed-set tag validator. Idempotent (a second
        /// <see cref="UnitTagValidator.ValidateAndDropUnits"/> pass over the already-compacted roster finds no
        /// offenders, and a repeated id is never appended twice), and a null/blank id is ignored — a blank reference
        /// is never "known but dropped", it is malformed.
        /// </summary>
        public void NoteTagDroppedUnit(string? unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return;
            if (!TagDroppedUnitIds.Contains(unitId)) TagDroppedUnitIds.Add(unitId!);
        }

        /// <summary>
        /// DW-652 — true when <paramref name="unitId"/> names a unit this faction DECLARED but the closed-set tag
        /// validator dropped from <see cref="Units"/>. False for a blank id and for any id the faction never declared
        /// (those stay genuinely-malformed references the scenario gate must still fail closed on).
        /// </summary>
        public bool WasUnitDroppedForInvalidTag(string? unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return false;
            for (int i = 0; i < TagDroppedUnitIds.Count; i++)
                if (TagDroppedUnitIds[i] == unitId) return true;
            return false;
        }

        // ── Lookup helpers ──────────────────────────────────────────────────────

        /// <summary>Find a building definition by ID, or null if not found.</summary>
        public BuildingDefinition? GetBuilding(string id)
        {
            foreach (var b in Buildings)
                if (b.Id == id) return b;
            return null;
        }

        /// <summary>Find a unit definition by ID, or null if not found. A null <see cref="Units"/> list (malformed
        /// JSON <c>"units": null</c>) OR a null element inside it is skipped, never an NRE (DW-103 — mirrors
        /// <see cref="GetResearch"/>).</summary>
        public UnitDefinition? GetUnit(string id)
        {
            if (Units == null) return null;
            foreach (var u in Units)
                if (u != null && u.Id == id) return u;
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
            if (Units == null) return -1;   // DW-103: malformed JSON "units": null — never an NRE
            for (int i = 0; i < Units.Count; i++)
                if (Units[i] != null && Units[i].Id == id) return i;   // DW-103: null element skipped
            return -1;
        }

        /// <summary>Find the first unit with the given category string (case-insensitive), or null. A null
        /// <see cref="Units"/> list or a null element is skipped, never an NRE (DW-103).</summary>
        public UnitDefinition? GetUnitByCategory(string category)
        {
            if (Units == null) return null;
            foreach (var u in Units)
                if (u != null && string.Equals(u.Category, category, System.StringComparison.OrdinalIgnoreCase))
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
            if (Units == null) return matches;   // DW-103: malformed JSON "units": null — never an NRE
            for (int i = 0; i < Units.Count; i++)
                if (Units[i] != null && string.Equals(Units[i].Category, category, System.StringComparison.OrdinalIgnoreCase))
                    matches.Add((i, Units[i]));   // DW-103: null element skipped
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
        /// instead of a hand-rolled replica that could silently drift from this loader (Story 2.7 review).
        /// <para>
        /// DW-274: this is now an ALIAS for <see cref="ContentJson.LenientOptions"/> — the same instance, declared
        /// beside the strict ability posture and the scenario posture so all three live in one file and the shared
        /// authoring half (comments / trailing commas) cannot drift. The name is kept because ~12 call sites and the
        /// Tier-1 suite reference it, and "the faction loader's options" is the discoverable spelling at those sites.
        /// Why it stays lenient (a contract, not an omission) is documented on
        /// <see cref="ContentJson.LenientOptions"/>; faction strictness belongs to <see cref="FactionValidator"/>.
        /// </para></summary>
        public static readonly JsonSerializerOptions JsonOptions = ContentJson.LenientOptions;

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

        /// <summary>
        /// Story 5.7 (FR-19/UX-DR80): directory-scan discovery of every SELECTABLE (validated-complete) faction
        /// under <paramref name="absDir"/> — the "what can a creator pick for playtest/skirmish" surface. Mirrors
        /// <see cref="AbilityRegistry.LoadFromDirectory"/>'s pattern, but lives here (not on
        /// <see cref="FactionRegistry"/>, which never touches res:// or does file I/O).
        ///
        /// Scans only <c>*_faction.json</c> (NOT bare <c>*.json</c> — this directory also holds
        /// <c>_buildingcard_sample.json</c>/<c>_unitcard_sample.json</c>, unrelated sample content that must never
        /// be mistaken for a faction). The directory enumeration itself (<see cref="Directory.GetFiles"/>) is
        /// guarded — an I/O error there is reported via <paramref name="onExcluded"/> against <paramref
        /// name="absDir"/> and yields an empty list, honoring this method's "never throws" contract even for a
        /// permissions/enumeration failure, not just a per-file parse failure. Matching files are walked in a
        /// deterministic ordinal-by-filename order — this walk order only controls the order <paramref
        /// name="onExcluded"/> fires and which file wins a duplicate-id tie (see below); it is NOT what makes the
        /// returned list itself deterministic (the final <see cref="Id"/> sort, below, does that).
        ///
        /// Each file is parsed directly via <see cref="JsonSerializer"/> (deliberately NOT <see cref="LoadFromFile"/>,
        /// which throws on a <see cref="FactionValidator.Validate"/> failure — that would abort the whole scan on
        /// one bad file); a parse exception or null result is reported via <paramref name="onExcluded"/> and
        /// skipped. A successfully-parsed def is then gated by <see cref="FactionValidator.ValidateComplete"/> —
        /// the roster-completeness check that closes DW-97's discovery half — and only <c>Ok</c> results proceed.
        /// If a def's <see cref="Id"/> was already claimed by an earlier file in the walk order, it is reported via
        /// <paramref name="onExcluded"/> as a duplicate and dropped (first-file-wins) — two factions can never
        /// alias the same selectable id.
        ///
        /// <para><b>Registry threading (DW-327): selectable == launchable.</b> Story 14.3 shipped
        /// <see cref="FactionValidator.ValidateComplete"/> with an OPTIONAL <see cref="AbilityRegistry"/> and 14.4
        /// threaded a real one at the Edit→Play launch gate (<see cref="FactionLaunchGate"/>) only — leaving THIS
        /// discovery scan registry-less, so the <c>signature_mechanic_effect_id</c> resolution check was dormant
        /// here. A faction with a typo'd signature id therefore listed as SELECTABLE at boot and was then
        /// hard-vetoed at Play with an error the boot console never showed. Pass the same loaded registry the
        /// launch gate gets (<paramref name="abilityRegistry"/>) and the two agree by construction: such a faction
        /// is dropped here, WITH its located reason surfaced through <paramref name="onExcluded"/>, so the creator
        /// sees the problem at the moment of discovery instead of at launch. The parameter stays optional and
        /// defaults to <c>null</c> (existing <see cref="FactionValidator.ValidateComplete"/> semantics: a null
        /// registry skips ONLY the signature check; roster/mesh/hero still enforce), so a caller with no registry
        /// — a test, or any pre-registry boot ordering — behaves exactly as before.</para>
        ///
        /// The returned list is ordinal-sorted by <see cref="Id"/> (mirrors <see cref="AbilityRegistry"/>'s stable
        /// index convention) — THIS sort is what makes the showcase factions (alpha/beta) and any wizard-authored
        /// ones enumerate deterministically alongside each other regardless of on-disk filename order.
        ///
        /// A missing/absent <paramref name="absDir"/> returns an empty list. Never throws.
        /// </summary>
        /// <param name="absDir">Absolute OS directory scanned for <c>*_faction.json</c>.</param>
        /// <param name="onExcluded">Called with (file name, reason) for every scanned file that is dropped.</param>
        /// <param name="abilityRegistry">The loaded ability registry used to resolve each candidate's
        /// <c>signature_mechanic_effect_id</c> (DW-327). <c>null</c> skips only that check.</param>
        public static IReadOnlyList<FactionDefinition> LoadSelectableFromDirectory(
            string absDir, System.Action<string, string>? onExcluded = null,
            AbilityRegistry? abilityRegistry = null)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir))
                return System.Array.Empty<FactionDefinition>();

            string[] files;
            try
            {
                files = Directory.GetFiles(absDir, "*_faction.json");
            }
            catch (System.Exception ex)
            {
                onExcluded?.Invoke(absDir, ex.Message);
                return System.Array.Empty<FactionDefinition>();
            }

            var defs = new List<FactionDefinition>();
            var seenIds = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (string file in files.OrderBy(f => f, System.StringComparer.Ordinal))
            {
                string fileName = Path.GetFileName(file);
                FactionDefinition? def;
                try
                {
                    def = JsonSerializer.Deserialize<FactionDefinition>(File.ReadAllText(file), JsonOptions);
                }
                catch (System.Exception ex)
                {
                    onExcluded?.Invoke(fileName, ex.Message);
                    continue;
                }

                if (def is null)
                {
                    onExcluded?.Invoke(fileName, "deserialized to null");
                    continue;
                }

                // DW-327: the SAME ValidateComplete the launch gate runs, with the SAME registry — a dangling
                // signature_mechanic_effect_id is excluded here (with its located reason) instead of listing as
                // selectable and being hard-vetoed later at Edit→Play.
                FactionValidationResult result = FactionValidator.ValidateComplete(def, abilityRegistry);
                if (!result.Ok)
                {
                    string reason = result.Errors.Count > 0 ? result.Errors[0].Message : "failed ValidateComplete";
                    onExcluded?.Invoke(fileName, reason);
                    continue;
                }

                if (!seenIds.Add(def.Id))
                {
                    onExcluded?.Invoke(fileName, $"duplicate faction id '{def.Id}' — an earlier file already claimed it");
                    continue;
                }

                defs.Add(def);
            }

            return defs.OrderBy(d => d.Id, System.StringComparer.Ordinal).ToList();
        }
    }
}
