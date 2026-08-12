#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>The four unit-list operations the Unit Card Editor persists (Story 3.4, D-7).</summary>
    public enum UnitEditKind
    {
        /// <summary>Rewrite the changed fields of an existing unit (matched by <see cref="UnitEdit.TargetId"/>).</summary>
        Update,
        /// <summary>Append a brand-new unit object (from <see cref="UnitEdit.Def"/>, only its non-default fields).</summary>
        Create,
        /// <summary>Deep-clone the target unit object verbatim and give the clone <see cref="UnitEdit.NewId"/>.</summary>
        Duplicate,
        /// <summary>Remove the target unit object.</summary>
        Delete,
    }

    /// <summary>The four building-list operations the Building Card Editor persists (Story 4.5, mirrors <see cref="UnitEditKind"/>).</summary>
    public enum BuildingEditKind
    {
        /// <summary>Rewrite the changed fields of an existing building (matched by <see cref="BuildingEdit.TargetId"/>).</summary>
        Update,
        /// <summary>Append a brand-new building object (from <see cref="BuildingEdit.Def"/>, only its non-default fields).</summary>
        Create,
        /// <summary>Deep-clone the target building object verbatim and give the clone <see cref="BuildingEdit.NewId"/>.</summary>
        Duplicate,
        /// <summary>Remove the target building object.</summary>
        Delete,
    }

    /// <summary>One authoring edit to a faction's <c>units[]</c> array — the argument to <see cref="FactionWriter.PatchFactionJson"/>.</summary>
    public sealed class UnitEdit
    {
        /// <summary>Which list operation to apply.</summary>
        public UnitEditKind Kind { get; init; }

        /// <summary>The id of the unit to update / duplicate / delete (matched against the on-disk <c>id</c>). Ignored for Create.</summary>
        public string TargetId { get; init; } = "";

        /// <summary>The edited/new unit for <see cref="UnitEditKind.Update"/> (reconcile source) and <see cref="UnitEditKind.Create"/> (new unit).</summary>
        public UnitDefinition? Def { get; init; }

        /// <summary>
        /// The raw-JSON escape-hatch path (D-5): when set on an <see cref="UnitEditKind.Update"/>, the target unit object
        /// is REPLACED wholesale by this parsed JSON (the creator hand-edited the unit's JSON, so their exact object
        /// wins — preserving any raw-only keys like a structured <c>combat_feedback</c>). When null, Update reconciles
        /// field-by-field from <see cref="Def"/>.
        /// </summary>
        public string? RawUnitJson { get; init; }

        /// <summary>The id for the clone on <see cref="UnitEditKind.Duplicate"/>.</summary>
        public string? NewId { get; init; }
    }

    /// <summary>One authoring edit to a faction's <c>buildings[]</c> array — mirrors <see cref="UnitEdit"/> (Story 4.5).
    /// The argument to <see cref="FactionWriter.PatchFactionBuildingJson"/>.</summary>
    public sealed class BuildingEdit
    {
        /// <summary>Which list operation to apply.</summary>
        public BuildingEditKind Kind { get; init; }

        /// <summary>The id of the building to update / duplicate / delete (matched against the on-disk <c>id</c>). Ignored for Create.</summary>
        public string TargetId { get; init; } = "";

        /// <summary>The edited/new building for <see cref="BuildingEditKind.Update"/> (reconcile source) and <see cref="BuildingEditKind.Create"/> (new building).</summary>
        public BuildingDefinition? Def { get; init; }

        /// <summary>
        /// The raw-JSON escape-hatch path (mirrors <see cref="UnitEdit.RawUnitJson"/>): when set on an
        /// <see cref="BuildingEditKind.Update"/>, the target building object is REPLACED wholesale by this parsed JSON.
        /// When null, Update reconciles field-by-field from <see cref="Def"/>.
        /// </summary>
        public string? RawBuildingJson { get; init; }

        /// <summary>The id for the clone on <see cref="BuildingEditKind.Duplicate"/>.</summary>
        public string? NewId { get; init; }
    }

    /// <summary>
    /// The Story 3.4 faction persistence core (D-1) — the SINGLE highest-risk part of the story, so it is a pure,
    /// Godot-free string transform that the Tier-1 harness round-trip-tests directly. It patches only the ONE edited
    /// unit inside a faction file's <c>units[]</c> array via a <see cref="System.Text.Json.Nodes.JsonNode"/> DOM,
    /// leaving every other unit, every building, and every faction-level key (<c>signature_mechanic</c>,
    /// <c>deferred_mechanics</c>, <c>color</c>, …) exactly as the creator wrote them (AC4).
    ///
    /// <para><b>Why not re-serialize the whole <see cref="FactionDefinition"/>?</b> A reflection re-serialize corrupts
    /// the file eight ways — it dumps the six computed <c>Parsed*</c> getters as PascalCase int fields (the source at
    /// <c>UnitDefinition.cs:342-344</c> relies on the loader NEVER re-serializing), drops faction-level keys STJ never
    /// mapped, balloons every unit with defaults, reorders fields, collapses formatting, and rewrites every sibling
    /// unit/building for a one-field edit. The DOM patch avoids all eight: untouched value nodes are JsonElement-backed
    /// so they re-serialize with their original token text, and only the fields that actually changed are set.</para>
    ///
    /// <para><b>Determinism.</b> Pure authoring-time. No sim array, store, checksum, or golden is touched; no committed
    /// golden loads the real faction files. Godot-free (<c>src/Core/Definitions</c>) so Tier-1 compiles + tests it; the
    /// presentation wrapper only supplies the globalized path + the atomic write.</para>
    /// </summary>
    public static class FactionWriter
    {
        /// <summary>Human-readable re-serialize options (2-space indent, matching the hand-authored faction files).</summary>
        private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

        /// <summary>Hero sub-object serialize options: omit null-valued optional slots (unset signature/ultimate) so a
        /// default-promoted hero writes no <c>"signature_ability": null</c> noise — matching <see cref="ApplyFields"/>'s
        /// omit-on-default discipline. Values round-trip identically (an omitted key deserializes back to null).</summary>
        private static readonly JsonSerializerOptions HeroSerializeOptions =
            new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        /// <summary>
        /// Apply one <paramref name="edit"/> to a faction JSON string and return the patched JSON. Pure — parses the
        /// input into a JsonNode DOM, mutates only the one affected <c>units[]</c> element, and re-serializes. Throws
        /// <see cref="InvalidOperationException"/> on malformed input (no <c>units</c> array) or a missing target id
        /// (Update/Duplicate/Delete) — the presentation wrapper surfaces that fail-closed.
        /// </summary>
        public static string PatchFactionJson(string factionJson, UnitEdit edit)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            if (edit is null) throw new InvalidOperationException("edit is null.");

            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");

            JsonArray units = GetOrCreateUnits(root, edit.Kind);

            switch (edit.Kind)
            {
                case UnitEditKind.Create:
                {
                    UnitDefinition def = edit.Def
                        ?? throw new InvalidOperationException("Create requires a unit definition.");
                    var obj = new JsonObject();
                    ApplyFields(obj, def);          // fresh object → writes only non-default fields (+ id)
                    units.Add(obj);
                    break;
                }

                case UnitEditKind.Update:
                {
                    JsonObject target = FindUnit(units, edit.TargetId)
                        ?? throw new InvalidOperationException($"unit '{edit.TargetId}' not found.");
                    int idx = units.IndexOf(target);

                    if (edit.RawUnitJson != null)
                    {
                        // Raw-pane path (D-5): the creator's hand-edited JSON wins verbatim (already validated upstream).
                        JsonNode replacement = JsonNode.Parse(edit.RawUnitJson)
                            ?? throw new InvalidOperationException("raw unit JSON did not parse to an object.");
                        units[idx] = replacement;
                    }
                    else
                    {
                        UnitDefinition def = edit.Def
                            ?? throw new InvalidOperationException("Update requires a unit definition or raw JSON.");
                        ApplyFields(target, def);   // reconcile: write only changed fields, preserve untouched tokens
                    }
                    break;
                }

                case UnitEditKind.Duplicate:
                {
                    JsonObject target = FindUnit(units, edit.TargetId)
                        ?? throw new InvalidOperationException($"unit '{edit.TargetId}' not found.");
                    string newId = edit.NewId
                        ?? throw new InvalidOperationException("Duplicate requires a NewId.");
                    // Deep-clone by re-parse (preserves EVERY field of the source verbatim, incl. combat_feedback + unknown keys).
                    JsonObject clone = (JsonNode.Parse(target.ToJsonString())!).AsObject();
                    clone["id"] = newId;
                    units.Add(clone);
                    break;
                }

                case UnitEditKind.Delete:
                {
                    JsonObject target = FindUnit(units, edit.TargetId)
                        ?? throw new InvalidOperationException($"unit '{edit.TargetId}' not found.");
                    units.RemoveAt(units.IndexOf(target));
                    break;
                }
            }

            return root.ToJsonString(IndentedOptions);
        }

        /// <summary>
        /// Serialize a single unit to clean, indented JSON for the raw-JSON escape hatch (D-5) — the authorable
        /// fields via <see cref="ApplyFields"/> (so NO computed <c>Parsed*</c> getter and no ballooned default leaks)
        /// plus the <c>combat_feedback</c> sub-object when present (the dual-path DTO the raw hatch exists to author).
        /// Reflects the current form model, not the on-disk bytes; a raw-pane Save replaces the unit object with the
        /// creator's edited text verbatim.
        /// </summary>
        public static string SerializeUnitClean(UnitDefinition def)
        {
            var obj = new JsonObject();
            ApplyFields(obj, def);   // authorable fields (no Parsed getters, no ballooning) + combat_feedback when authored
            return obj.ToJsonString(IndentedOptions);
        }

        // ── DOM helpers ─────────────────────────────────────────────────────────────

        private static JsonArray GetOrCreateUnits(JsonNode root, UnitEditKind kind)
        {
            if (root["units"] is JsonArray existing) return existing;
            if (kind == UnitEditKind.Create)
            {
                var arr = new JsonArray();
                root["units"] = arr;
                return arr;
            }
            throw new InvalidOperationException("faction JSON has no 'units' array to edit.");
        }

        private static JsonObject? FindUnit(JsonArray units, string id)
        {
            foreach (JsonNode? n in units)
                if (n is JsonObject o && (string?)o["id"] == id) return o;
            return null;
        }

        /// <summary>
        /// Reconcile every authorable field of <paramref name="def"/> onto <paramref name="obj"/>: write a field ONLY
        /// when the edited value differs from the object's current effective value (its token if present, else the POCO
        /// default). Untouched fields keep their exact on-disk token; an absent field whose edited value equals its
        /// default stays absent (no ballooning). NEVER touches <c>combat_feedback</c> (dual-path DTO — preserved
        /// verbatim; edited only via the raw-JSON hatch), the six <c>Parsed*</c> getters, or the <c>[JsonIgnore]</c>
        /// ability-index fields.
        /// </summary>
        private static void ApplyFields(JsonObject obj, UnitDefinition d)
        {
            PutString(obj, "id", d.Id, "");
            PutString(obj, "display_name", d.DisplayName, "");
            PutString(obj, "category", d.Category, "Melee");
            PutNullableString(obj, "mesh_path", d.MeshPath);

            PutFloat(obj, "hp", d.Hp, 100f);
            PutFloat(obj, "speed", d.Speed, 4f);
            PutFloat(obj, "attack_damage", d.AttackDamage, 10f);
            PutFloat(obj, "attack_range", d.AttackRange, 5f);
            PutFloat(obj, "attack_speed", d.AttackSpeed, 1f);

            PutString(obj, "damage_type", d.DamageType, "Normal");
            PutString(obj, "armor_type", d.ArmorType, "Unarmored");
            PutFloat(obj, "armor", d.Armor, 0f);

            PutInt(obj, "cost_ore", d.CostOre, 50);
            PutInt(obj, "cost_crystal", d.CostCrystal, 0);
            // Story 4.5: the sparse authored cost map (Story 4.3) — was silently DROPPED by every prior ApplyFields
            // pass (no PutCostMap call existed), so a creator-authored "cost" map never persisted for a unit OR a
            // building. Omitted entirely when d.Cost is null (unauthored — the legacy cost_ore/cost_crystal fields
            // above are the effective cost), matching every other nullable-field Put* helper's omit-on-null discipline.
            PutCostMap(obj, "cost", d.Cost);
            PutInt(obj, "supply", d.Supply, 1);

            PutFloat(obj, "mesh_scale", d.MeshScale, 1f);
            PutFloat(obj, "train_time", d.TrainTime, 8f);
            PutFloat(obj, "vision_range", d.VisionRange, 8f);
            PutFloat(obj, "splash_radius", d.SplashRadius, 0f);
            PutFloat(obj, "collision_radius", d.CollisionRadius, 1f);
            PutFloat(obj, "max_energy", d.MaxEnergy, 0f);

            // Story 3.12: delivery is nullable (omit when null — the legacy range-inference default); projectile_speed
            // omits at its 18f default so every existing ranged unit round-trips byte-identically.
            PutNullableString(obj, "delivery", d.Delivery);
            PutFloat(obj, "projectile_speed", d.ProjectileSpeed, 18f);
            // Story 3.13: xp_bounty is nullable (omit when null — the derived cost default); an authored value writes.
            PutNullableInt(obj, "xp_bounty", d.XpBounty);

            PutString(obj, "separation_priority", d.SeparationPriority, "Normal");

            PutStringArray(obj, "prerequisites", d.Prerequisites, defaultsNull: false);
            PutStringArray(obj, "abilities", d.Abilities, defaultsNull: false);
            PutStringArray(obj, "behaviors", d.Behaviors, defaultsNull: false);
            PutStringArray(obj, "attack_domains", d.AttackDomains, defaultsNull: true);
            PutStringArray(obj, "tags", d.Tags, defaultsNull: true);

            PutBool(obj, "is_hero", d.IsHero, false);
            // Story 3.14: revives_heroes omits at its false default so every existing building round-trips byte-identically.
            PutBool(obj, "revives_heroes", d.RevivesHeroes, false);
            // Story 3.16: the three shop fields omit at their false/null/0 defaults so every existing building round-trips
            // byte-identically (only an authored shop building writes them).
            PutBool(obj, "sells_items", d.SellsItems, false);
            PutStringArray(obj, "shop_stock", d.ShopStock, defaultsNull: true);
            PutFloat(obj, "shop_radius", d.ShopRadius, 0f);
            WriteHero(obj, d);
            WriteCombatFeedback(obj, d);
        }

        /// <summary>
        /// Reconcile the <c>hero</c> block (Story 3.7 authoring data). Unlike <see cref="WriteCombatFeedback"/> the
        /// <c>hero</c> block is fully FORM-owned (no raw-only sub-keys to preserve), so a deterministic POCO re-serialize
        /// is correct: serialize <see cref="UnitDefinition.Hero"/> to <c>obj["hero"]</c> when non-null, else drop the key.
        /// A non-hero unit therefore carries no <c>hero</c> block (no faction-JSON churn for existing units).
        /// </summary>
        private static void WriteHero(JsonObject obj, UnitDefinition d)
        {
            if (d.Hero == null) { obj.Remove("hero"); return; }
            obj["hero"] = JsonNode.Parse(JsonSerializer.Serialize(d.Hero, HeroSerializeOptions));
        }

        /// <summary>
        /// Reconcile <c>combat_feedback</c> (the dual-path presentation DTO, authored ONLY via the raw-JSON hatch —
        /// the form never touches it). Preserve the on-disk object VERBATIM when the POCO round-trips to the same
        /// normalized JSON (an unrelated field edit must not reformat/balloon a unit's combat_feedback); write the
        /// POCO's JSON only when it genuinely differs (a raw-hatch edit); drop the key when the POCO cleared it.
        /// </summary>
        private static void WriteCombatFeedback(JsonObject obj, UnitDefinition d)
        {
            string? pocoJson = d.CombatFeedback != null ? JsonSerializer.Serialize(d.CombatFeedback) : null;

            string? diskNormalized = null;
            if (obj["combat_feedback"] is JsonNode disk)
            {
                // Normalize the on-disk object THROUGH the DTO so formatting/order/defaults line up with pocoJson,
                // making the "unchanged" comparison semantic (not textual). A clean float/int/string DTO round-trips.
                try
                {
                    var dto = JsonSerializer.Deserialize<CombatFeedbackProfile>(disk.ToJsonString());
                    diskNormalized = dto != null ? JsonSerializer.Serialize(dto) : null;
                }
                catch { diskNormalized = null; }
            }

            if (pocoJson == diskNormalized) return;             // unchanged (incl. both null) → preserve on-disk verbatim
            if (pocoJson == null) { obj.Remove("combat_feedback"); return; }   // creator cleared it
            obj["combat_feedback"] = JsonNode.Parse(pocoJson);  // raw-hatch edit → write the POCO's JSON
        }

        /// <summary>
        /// Persist an entire in-memory <c>units</c> list to a faction JSON string in one atomic transform (Story 3.4
        /// Save path). Reconciles each in-memory unit onto its matching on-disk object (untouched tokens preserved, only
        /// changed fields written), builds a fresh object for a new/duplicated unit, and DROPS any on-disk unit no longer
        /// in the list — while leaving every building and faction-level key (<c>signature_mechanic</c>, …) exactly as the
        /// creator wrote them. The <c>units</c> array is rebuilt in the in-memory list order (3.4 never reorders, so an
        /// unchanged list re-emits byte-identically). This is the whole-list generalization of <see cref="PatchFactionJson"/>;
        /// the editor keeps edits in memory + an undo stack and calls this once on Save (D-6/D-10).
        /// </summary>
        public static string SyncFactionUnits(string factionJson, IReadOnlyList<UnitDefinition> units)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            if (units is null) throw new InvalidOperationException("units is null.");

            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");

            var oldArr = root["units"] as JsonArray ?? new JsonArray();
            // Index the on-disk objects by id (first wins) so each in-memory unit can reconcile onto its match.
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (JsonNode? n in oldArr)
                if (n is JsonObject o && (string?)o["id"] is string id && !byId.ContainsKey(id))
                    byId[id] = o;

            var newArr = new JsonArray();
            foreach (UnitDefinition u in units)
            {
                if (u.Id != null && byId.TryGetValue(u.Id, out JsonObject? existing))
                {
                    byId.Remove(u.Id);
                    oldArr.Remove(existing);        // detach so it can re-parent into newArr
                    ApplyFields(existing, u);       // reconcile in place (untouched tokens preserved)
                    newArr.Add(existing);
                }
                else
                {
                    var fresh = new JsonObject();
                    ApplyFields(fresh, u);          // only non-default fields (+ id, + combat_feedback if authored)
                    newArr.Add(fresh);
                }
            }
            // Anything still in byId is an on-disk unit no longer in the list → dropped (a delete).
            root["units"] = newArr;
            return root.ToJsonString(IndentedOptions);
        }

        /// <summary>
        /// Story 15-21: persist the faction-level <c>attribute_model</c> as a TARGETED root-key patch (the 3.4
        /// posture — never a whole-faction re-serialize). Like the <c>hero</c> block the model is fully FORM-owned
        /// (preset apply + the mapping editor), so a deterministic POCO re-serialize of just this key is correct:
        /// write <c>root["attribute_model"]</c> when non-null, drop the key when null. Every other faction key —
        /// buildings, signature_mechanic, … — stays exactly as the creator wrote it.
        /// </summary>
        public static string SyncFactionAttributeModel(string factionJson, AttributeModelDefinition? model)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");
            if (model == null) ((JsonObject)root).Remove("attribute_model");
            else root["attribute_model"] = JsonNode.Parse(JsonSerializer.Serialize(model, HeroSerializeOptions));
            return root.ToJsonString(IndentedOptions);
        }

        // ── Story 4.5: the buildings[] counterpart to the units machinery above ────────────────────────────────

        /// <summary>
        /// Apply one <paramref name="edit"/> to a faction JSON string's <c>buildings[]</c> array and return the patched
        /// JSON — mirrors <see cref="PatchFactionJson"/> exactly, operating on <c>root["buildings"]</c> instead of
        /// <c>root["units"]</c>. Never touches <c>root["units"]</c>. Throws <see cref="InvalidOperationException"/> on
        /// malformed input (no <c>buildings</c> array, Create excepted) or a missing target id (Update/Duplicate/Delete).
        /// </summary>
        public static string PatchFactionBuildingJson(string factionJson, BuildingEdit edit)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            if (edit is null) throw new InvalidOperationException("edit is null.");

            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");

            JsonArray buildings = GetOrCreateBuildings(root, edit.Kind);

            switch (edit.Kind)
            {
                case BuildingEditKind.Create:
                {
                    BuildingDefinition def = edit.Def
                        ?? throw new InvalidOperationException("Create requires a building definition.");
                    var obj = new JsonObject();
                    ApplyBuildingFields(obj, def);          // fresh object → writes only non-default fields (+ id)
                    buildings.Add(obj);
                    break;
                }

                case BuildingEditKind.Update:
                {
                    JsonObject target = FindBuilding(buildings, edit.TargetId)
                        ?? throw new InvalidOperationException($"building '{edit.TargetId}' not found.");
                    int idx = buildings.IndexOf(target);

                    if (edit.RawBuildingJson != null)
                    {
                        // Raw-pane path: the creator's hand-edited JSON wins verbatim (already validated upstream).
                        JsonNode replacement = JsonNode.Parse(edit.RawBuildingJson)
                            ?? throw new InvalidOperationException("raw building JSON did not parse to an object.");
                        buildings[idx] = replacement;
                    }
                    else
                    {
                        BuildingDefinition def = edit.Def
                            ?? throw new InvalidOperationException("Update requires a building definition or raw JSON.");
                        ApplyBuildingFields(target, def);   // reconcile: write only changed fields, preserve untouched tokens
                    }
                    break;
                }

                case BuildingEditKind.Duplicate:
                {
                    JsonObject target = FindBuilding(buildings, edit.TargetId)
                        ?? throw new InvalidOperationException($"building '{edit.TargetId}' not found.");
                    string newId = edit.NewId
                        ?? throw new InvalidOperationException("Duplicate requires a NewId.");
                    // Deep-clone by re-parse (preserves EVERY field of the source verbatim, incl. unknown keys).
                    JsonObject clone = (JsonNode.Parse(target.ToJsonString())!).AsObject();
                    clone["id"] = newId;
                    buildings.Add(clone);
                    break;
                }

                case BuildingEditKind.Delete:
                {
                    JsonObject target = FindBuilding(buildings, edit.TargetId)
                        ?? throw new InvalidOperationException($"building '{edit.TargetId}' not found.");
                    buildings.RemoveAt(buildings.IndexOf(target));
                    break;
                }
            }

            return root.ToJsonString(IndentedOptions);
        }

        /// <summary>
        /// Serialize a single building to clean, indented JSON for the raw-JSON escape hatch — mirrors
        /// <see cref="SerializeUnitClean"/>. The authorable fields via <see cref="ApplyBuildingFields"/> (no computed
        /// <c>Parsed*</c> getter, no ballooned default).
        /// </summary>
        public static string SerializeBuildingClean(BuildingDefinition def)
        {
            var obj = new JsonObject();
            ApplyBuildingFields(obj, def);
            return obj.ToJsonString(IndentedOptions);
        }

        private static JsonArray GetOrCreateBuildings(JsonNode root, BuildingEditKind kind)
        {
            if (root["buildings"] is JsonArray existing) return existing;
            if (kind == BuildingEditKind.Create)
            {
                var arr = new JsonArray();
                root["buildings"] = arr;
                return arr;
            }
            throw new InvalidOperationException("faction JSON has no 'buildings' array to edit.");
        }

        private static JsonObject? FindBuilding(JsonArray buildings, string id)
        {
            foreach (JsonNode? n in buildings)
                if (n is JsonObject o && (string?)o["id"] == id) return o;
            return null;
        }

        /// <summary>
        /// Reconcile every authorable field of <paramref name="d"/> onto <paramref name="obj"/>: every
        /// <see cref="UnitDefinition"/> field via <see cref="ApplyFields"/> (id, stats, cost map, …) PLUS the three
        /// building-only fields, written UNCONDITIONALLY (no omit-at-default — <see cref="BuildingDefinition.ConstructionTime"/>/
        /// <see cref="BuildingDefinition.SupplyBonus"/>/<see cref="BuildingDefinition.ProducesCategory"/> are
        /// required-nullable per <see cref="BuildingDefinitionValidator"/>, so there is no meaningful default to omit
        /// at). A still-null field (only possible on an invalid, un-Saveable def) is defensively omitted rather than
        /// round-tripped as a literal JSON <c>null</c> token.
        /// </summary>
        private static void ApplyBuildingFields(JsonObject obj, BuildingDefinition d)
        {
            ApplyFields(obj, d);
            // DW-55: hp is REQUIRED for a building (BuildingDefinitionValidator rejects an omitted hp), so an authored
            // hp is always serialized — overriding ApplyFields' shared PutFloat, which omits hp at its 100 default (a
            // building written with an authored default 100 would otherwise round-trip to a file that fails to reload).
            // Gated on HpAuthored so a never-authored hp is NOT silently materialized into 100 on the write side,
            // preserving the omitted-vs-authored distinction. Every building that reaches the writer via load/DoCreate/
            // clone is HpAuthored=true, so hp is still always emitted for real content.
            if (d.HpAuthored) obj["hp"] = d.Hp; else obj.Remove("hp");
            if (d.ConstructionTime.HasValue) obj["construction_time"] = d.ConstructionTime.Value; else obj.Remove("construction_time");
            if (d.SupplyBonus.HasValue) obj["supply_bonus"] = d.SupplyBonus.Value; else obj.Remove("supply_bonus");
            if (!string.IsNullOrEmpty(d.ProducesCategory)) obj["produces_category"] = d.ProducesCategory; else obj.Remove("produces_category");
            // Story 4.8: available_research is a BuildingDefinition-only field (unlike prerequisites, which lives
            // on the shared UnitDefinition base and already round-trips via ApplyFields above) — mirrors
            // PutStringArray's "prerequisites" handling exactly (empty array default, never omitted-as-null).
            PutStringArray(obj, "available_research", d.AvailableResearch, defaultsNull: false);
        }

        /// <summary>
        /// Persist an entire in-memory <c>buildings</c> list to a faction JSON string in one atomic transform (Story 4.5
        /// Save path) — the buildings-array generalization of <see cref="PatchFactionBuildingJson"/>, mirroring
        /// <see cref="SyncFactionUnits"/> exactly. Reconciles onto <c>root["buildings"]</c> ONLY — never touches
        /// <c>root["units"]</c> or any faction-level key.
        /// </summary>
        public static string SyncFactionBuildings(string factionJson, IReadOnlyList<BuildingDefinition> buildings)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            if (buildings is null) throw new InvalidOperationException("buildings is null.");

            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");

            var oldArr = root["buildings"] as JsonArray ?? new JsonArray();
            // Index the on-disk objects by id (first wins) so each in-memory building can reconcile onto its match.
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (JsonNode? n in oldArr)
                if (n is JsonObject o && (string?)o["id"] is string id && !byId.ContainsKey(id))
                    byId[id] = o;

            var newArr = new JsonArray();
            foreach (BuildingDefinition b in buildings)
            {
                if (b.Id != null && byId.TryGetValue(b.Id, out JsonObject? existing))
                {
                    byId.Remove(b.Id);
                    oldArr.Remove(existing);            // detach so it can re-parent into newArr
                    ApplyBuildingFields(existing, b);   // reconcile in place (untouched tokens preserved)
                    newArr.Add(existing);
                }
                else
                {
                    var fresh = new JsonObject();
                    ApplyBuildingFields(fresh, b);       // only non-default fields (+ id) + the required building trio
                    newArr.Add(fresh);
                }
            }
            // Anything still in byId is an on-disk building no longer in the list → dropped (a delete).
            root["buildings"] = newArr;
            return root.ToJsonString(IndentedOptions);
        }

        // ── Story 4.11: the research[] counterpart to the units/buildings machinery above ──────────────────────

        /// <summary>
        /// Persist an entire in-memory <c>research</c> list to a faction JSON string in one atomic transform (Story
        /// 4.11 Save path) — the research-array generalization of <see cref="SyncFactionUnits"/>/
        /// <see cref="SyncFactionBuildings"/>. Reconciles onto <c>root["research"]</c> ONLY — never touches
        /// <c>root["units"]</c>/<c>root["buildings"]</c> or any faction-level key. Unlike the unit/building
        /// reconcilers, a research entry's nested <c>levels</c> array has no existing token-preserving precedent in
        /// this codebase (a repeatable list of composite objects, not a single scalar) — mirrors
        /// <see cref="WriteHero"/>'s posture for a form-owned sub-object: the whole <c>levels</c> array is rewritten
        /// fresh whenever a research entry's fields are reconciled (whether newly matched to an on-disk object or
        /// freshly created), never diffed field-by-field.
        /// </summary>
        public static string SyncFactionResearch(string factionJson, IReadOnlyList<ResearchDefinition> research)
        {
            if (factionJson is null) throw new InvalidOperationException("faction JSON is null.");
            if (research is null) throw new InvalidOperationException("research is null.");

            JsonNode root = JsonNode.Parse(factionJson)
                            ?? throw new InvalidOperationException("faction JSON did not parse to an object.");

            var oldArr = root["research"] as JsonArray ?? new JsonArray();
            // Index the on-disk objects by id (first wins) so each in-memory research entry can reconcile onto its match.
            var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (JsonNode? n in oldArr)
                if (n is JsonObject o && (string?)o["id"] is string id && !byId.ContainsKey(id))
                    byId[id] = o;

            var newArr = new JsonArray();
            foreach (ResearchDefinition? r in research)
            {
                // Review-pass fix: a null list element (malformed hand-edited JSON "research": [null, {...}])
                // must never NRE this write path — mirrors ResearchValidator.Validate's identical null-skip
                // convention for the same input shape.
                if (r == null) continue;
                if (r.Id != null && byId.TryGetValue(r.Id, out JsonObject? existing))
                {
                    byId.Remove(r.Id);
                    oldArr.Remove(existing);           // detach so it can re-parent into newArr
                    ApplyResearchFields(existing, r);  // reconcile in place (id/display_name/cancel_refund_fraction/
                                                        // prerequisites preserve untouched tokens; levels rewrites fresh)
                    newArr.Add(existing);
                }
                else
                {
                    var fresh = new JsonObject();
                    ApplyResearchFields(fresh, r);
                    newArr.Add(fresh);
                }
            }
            // Anything still in byId is an on-disk research entry no longer in the list → dropped (a delete).
            root["research"] = newArr;
            return root.ToJsonString(IndentedOptions);
        }

        /// <summary>
        /// Serialize a single research entry to clean, indented JSON for the raw-JSON escape hatch — mirrors
        /// <see cref="SerializeBuildingClean"/>.
        /// </summary>
        public static string SerializeResearchClean(ResearchDefinition def)
        {
            var obj = new JsonObject();
            ApplyResearchFields(obj, def);
            return obj.ToJsonString(IndentedOptions);
        }

        /// <summary>Reconcile every authorable field of <paramref name="d"/> onto <paramref name="obj"/> — the
        /// four scalar/array fields via the same token-preserving Put* helpers <see cref="ApplyFields"/> uses, plus
        /// the nested <c>levels</c> array (always rewritten fresh; see <see cref="SyncFactionResearch"/>'s doc).</summary>
        private static void ApplyResearchFields(JsonObject obj, ResearchDefinition d)
        {
            PutString(obj, "id", d.Id, "");
            PutString(obj, "display_name", d.DisplayName, "");
            PutFloat(obj, "cancel_refund_fraction", d.CancelRefundFraction, 0f);
            PutStringArray(obj, "prerequisites", d.Prerequisites, defaultsNull: false);
            PutLevels(obj, "levels", d.Levels);
        }

        /// <summary>Rewrite the whole <c>levels</c> array fresh from <paramref name="edited"/> — each level's
        /// <c>cost</c> map (ordinal-sorted keys, omitted when empty/unauthored), <c>time_ticks</c>, and
        /// <c>modifier_delta</c> (omitted entirely when every one of its four fields is 0 — an unauthored level has
        /// no stat effect, mirroring <see cref="ResearchLevel.ModifierDelta"/>'s null-is-no-effect semantics).</summary>
        private static void PutLevels(JsonObject o, string key, List<ResearchLevel>? edited)
        {
            var arr = new JsonArray();
            foreach (ResearchLevel? level in edited ?? new List<ResearchLevel>())
            {
                // Review-pass fix: same null-element defense as SyncFactionResearch above, for a malformed
                // "levels": [null, {...}] entry.
                if (level == null) continue;
                var lo = new JsonObject();
                if (level.Cost != null && level.Cost.Count > 0)
                {
                    var costObj = new JsonObject();
                    foreach (string k in level.Cost.Keys.OrderBy(k => k, StringComparer.Ordinal))
                        costObj[k] = level.Cost[k];
                    lo["cost"] = costObj;
                }
                lo["time_ticks"] = level.TimeTicks;

                ResearchModifierDelta? md = level.ModifierDelta;
                if (md != null && (md.MaxHealthDelta != 0f || md.AttackDamageDelta != 0f || md.MoveSpeedDelta != 0f || md.ArmorDelta != 0f))
                {
                    var mdObj = new JsonObject();
                    if (md.MaxHealthDelta != 0f)    mdObj["max_health_delta"]    = md.MaxHealthDelta;
                    if (md.AttackDamageDelta != 0f) mdObj["attack_damage_delta"] = md.AttackDamageDelta;
                    if (md.MoveSpeedDelta != 0f)    mdObj["move_speed_delta"]    = md.MoveSpeedDelta;
                    if (md.ArmorDelta != 0f)        mdObj["armor_delta"]         = md.ArmorDelta;
                    lo["modifier_delta"] = mdObj;
                }
                arr.Add(lo);
            }
            o[key] = arr;
        }

        // Each Put* writes ONLY when the edited value differs from the current effective value (token-or-default).

        private static void PutFloat(JsonObject o, string key, float edited, float def)
        {
            float current = TryReadDouble(o, key, out double v) ? (float)v : def;
            if (edited != current) o[key] = edited;
        }

        private static void PutInt(JsonObject o, string key, int edited, int def)
        {
            int current = TryReadDouble(o, key, out double v) ? (int)v : def;
            if (edited != current) o[key] = edited;
        }

        private static void PutBool(JsonObject o, string key, bool edited, bool def)
        {
            bool current = o[key] is JsonNode n && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? n.GetValue<bool>() : def;
            if (edited != current) o[key] = edited;
        }

        private static void PutString(JsonObject o, string key, string edited, string def)
        {
            string current = o[key] is JsonNode n && n.GetValueKind() == JsonValueKind.String
                ? n.GetValue<string>() : def;
            if (edited != current) o[key] = edited;
        }

        /// <summary>Story 3.13: write-back for a nullable int (xp_bounty) — omit the key when null (the derived default),
        /// write it when authored, drop it when cleared. Mirrors <see cref="PutNullableString"/>.</summary>
        private static void PutNullableInt(JsonObject o, string key, int? edited)
        {
            int? current = TryReadDouble(o, key, out double v) ? (int)v : (int?)null;
            if (edited == current) return;                   // unchanged (incl. both absent) → preserve/absent
            if (edited == null) { o.Remove(key); return; }   // cleared → drop the key (→ derived-from-cost default)
            o[key] = edited.Value;
        }

        private static void PutNullableString(JsonObject o, string key, string? edited)
        {
            string? norm = string.IsNullOrEmpty(edited) ? null : edited;
            string? current = o[key] is JsonNode n && n.GetValueKind() == JsonValueKind.String
                ? n.GetValue<string>() : null;
            if (norm == current) return;                     // unchanged (incl. both "no mesh") → preserve/absent
            if (norm == null) { o.Remove(key); return; }     // cleared → drop the key (→ box placeholder)
            o[key] = norm;
        }

        private static void PutStringArray(JsonObject o, string key, string[]? edited, bool defaultsNull)
        {
            string[] e = edited ?? Array.Empty<string>();
            string[]? present = ReadStringArray(o, key);            // null = key absent
            string[] current = present ?? Array.Empty<string>();    // absent → effective empty (default is [] or null → both empty-effective)
            if (e.SequenceEqual(current, StringComparer.Ordinal)) return;   // unchanged → preserve/absent

            if (e.Length == 0)
            {
                // Cleared to empty. For a null-default field, drop the key; for an []-default field, an explicit [] is fine.
                if (defaultsNull) { o.Remove(key); return; }
                o[key] = new JsonArray();
                return;
            }
            var arr = new JsonArray();
            // Add via the string→JsonNode implicit operator (a JsonValue PRIMITIVE), NOT the generic Add<T>(string)
            // overload (which mints a JsonValueCustomized<string> that ToJsonString CANNOT serialize without a
            // TypeInfoResolver on the options — the resolver-less IndentedOptions here). Same emitted JSON, but the
            // primitive round-trips; the customized node throws. (The mesh-path string write already uses this path.)
            foreach (string s in e) arr.Add((JsonNode)s);
            o[key] = arr;
        }

        private static string[]? ReadStringArray(JsonObject o, string key)
        {
            if (o[key] is not JsonArray a) return null;
            var list = new List<string>(a.Count);
            foreach (JsonNode? n in a)
                if (n != null && n.GetValueKind() == JsonValueKind.String) list.Add(n.GetValue<string>());
            return list.ToArray();
        }

        /// <summary>
        /// Story 4.5: write-back for the sparse authored resource <c>cost</c> map (Story 4.3) — closes the gap where
        /// NO prior <c>ApplyFields</c> pass ever wrote this key for a unit or a building (silently dropped on every
        /// Save/round-trip). Compares the CURRENT on-disk map's key/value pairs against <paramref name="edited"/>'s
        /// (order-independent via <see cref="MapsEqual"/>); writes the whole map only when they differ; omits the key
        /// entirely when <paramref name="edited"/> is null (unauthored — the legacy <c>cost_ore</c>/<c>cost_crystal</c>
        /// fields are the effective cost, so every existing unit/building with no <c>cost</c> key round-trips
        /// byte-identically, keeping golden checksums untouched); drops the key when a previously-authored map is
        /// cleared to null. An authored EMPTY map (<c>{}</c>) round-trips as an explicit empty object — distinct from
        /// omitted (Story 4.3's "free" semantics). Keys are written in ordinal-sorted order for a deterministic byte
        /// stream regardless of <see cref="Dictionary{TKey,TValue}"/> enumeration order.
        /// </summary>
        private static void PutCostMap(JsonObject o, string key, Dictionary<string, int>? edited)
        {
            Dictionary<string, int>? current = ReadCostMap(o, key);
            if (MapsEqual(edited, current)) return;                 // unchanged (incl. both absent) → preserve/absent
            if (edited == null) { o.Remove(key); return; }          // cleared → drop the key (→ legacy cost_ore/cost_crystal)

            var costObj = new JsonObject();
            foreach (string k in edited.Keys.OrderBy(k => k, StringComparer.Ordinal))
                costObj[k] = edited[k];
            o[key] = costObj;
        }

        private static Dictionary<string, int>? ReadCostMap(JsonObject o, string key)
        {
            if (o[key] is not JsonObject costObj) return null;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonNode?> kv in costObj)
                if (kv.Value != null && kv.Value.GetValueKind() == JsonValueKind.Number)
                    map[kv.Key] = (int)kv.Value.GetValue<double>();
            return map;
        }

        private static bool MapsEqual(Dictionary<string, int>? a, Dictionary<string, int>? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, int> kv in a)
                if (!b.TryGetValue(kv.Key, out int v) || v != kv.Value) return false;
            return true;
        }

        private static bool TryReadDouble(JsonObject o, string key, out double value)
        {
            if (o[key] is JsonNode n && n.GetValueKind() == JsonValueKind.Number)
            {
                value = n.GetValue<double>();
                return true;
            }
            value = 0;
            return false;
        }
    }
}
