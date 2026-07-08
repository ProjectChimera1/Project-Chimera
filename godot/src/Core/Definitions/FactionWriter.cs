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

            PutString(obj, "separation_priority", d.SeparationPriority, "Normal");

            PutStringArray(obj, "prerequisites", d.Prerequisites, defaultsNull: false);
            PutStringArray(obj, "abilities", d.Abilities, defaultsNull: false);
            PutStringArray(obj, "behaviors", d.Behaviors, defaultsNull: false);
            PutStringArray(obj, "attack_domains", d.AttackDomains, defaultsNull: true);
            PutStringArray(obj, "tags", d.Tags, defaultsNull: true);

            PutBool(obj, "is_hero", d.IsHero, false);
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
