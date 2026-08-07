#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Combat;   // DamageType
using ProjectChimera.Core;     // Fixed
using ProjectChimera.Effects;  // the closed 2.1 effect vocabulary + EffectCaps

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The closed-registry polymorphic converter for the 2.1 effect graph (Story 2.3, AR-22). Dispatches on a
    /// closed <c>"kind"</c> discriminator against a HARDCODED registry of the 8 runtime sealed
    /// <see cref="EffectNode"/> types, building each via its public constructor — NO reflection, NO
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> (forbidden project-wide: incompatible with
    /// <c>UnmappedMemberHandling.Disallow</c>, throws at runtime on the first node). There is therefore no open
    /// extension point and no scripting escape hatch — an unauthored/script <c>kind</c> simply isn't registered and
    /// is rejected (fail-closed, AC2/AC3).
    ///
    /// FAIL-CLOSED on read (every branch returns a LOCATED <see cref="JsonException"/> whose message is
    /// <c>"&lt;path&gt;: &lt;reason&gt;"</c>; <see cref="AbilityLoader"/> prepends the ability id):
    ///   • unknown <c>kind</c> → located reject naming the kind;
    ///   • missing required field → located reject naming the field;
    ///   • UNKNOWN PROPERTY on any node/modifier object → located reject (Disallow governs only the POCO layer; a
    ///     custom converter must reject strays itself, or a scripting payload could ride along an effect object);
    ///   • nesting past <see cref="EffectCaps.MaxEffectDepth"/> → located parse-time reject (an anti-stack-overflow
    ///     belt-and-suspenders alongside <see cref="JsonSerializerOptions.MaxDepth"/>, mirroring the EffectBounds
    ///     composition-depth threshold so no valid graph is ever false-rejected).
    ///
    /// Implementation: each node subtree is read into a transient <see cref="JsonDocument"/> (load-time only, never
    /// in-tick), so discrimination is order-independent (the <c>kind</c> need not be the first property) and stray
    /// properties are detectable. Field VALUES are deserialized through the shared <see cref="ContentJson.Options"/>
    /// converters (<c>Fixed</c> → <see cref="FixedJsonConverter"/>; enums by NAME), so the single quantization
    /// boundary still holds. The <c>Modifier</c> descriptor is read by a private helper here (rather than a separate
    /// <c>JsonConverter&lt;Modifier&gt;</c>) — one converter owns the whole effect-and-modifier surface.
    /// </summary>
    public sealed class EffectNodeJsonConverter : JsonConverter<EffectNode>
    {
        // ── The closed kind registry (discriminator → runtime sealed type). No reflection; hardcoded. ──
        private const string KindDirectHpDelta = "direct_hp_delta";
        private const string KindHeal          = "heal";
        private const string KindDamage        = "damage";
        private const string KindApplyModifier = "apply_modifier";
        private const string KindSequence      = "sequence";
        private const string KindSearchArea    = "search_area";
        private const string KindPersistent    = "persistent";

        /// <inheritdoc />
        public override EffectNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Buffer this node's subtree. JsonDocument depth is bounded by options.MaxDepth (default 64), so the DOM
            // build itself can't stack-overflow on a malicious doc; our explicit depth guard below is the named cap.
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            return ReadNode(doc.RootElement, options, depth: 0, path: "effect");
        }

        /// <summary>
        /// Authoring-only serialize (Story 2.5a — the keystone 2.3 deferred to the Ability Editor by name). Emits the
        /// closed effect graph back to JSON so the editor can round-trip it (raw-JSON view) and save it. The EXACT
        /// inverse of <see cref="Read"/>: each node writes its <c>kind</c> discriminator + ONLY that kind's
        /// allow-listed fields (the same property names <see cref="RejectUnknownProperties"/> accepts), so a serialized
        /// graph re-loads byte-faithfully (round-trip identity judged on <c>Fixed.Raw</c> + structure). <c>Fixed</c> and
        /// enum fields are emitted by delegating to the registered converters via
        /// <see cref="JsonSerializer"/>.<c>Serialize</c> (Fixed → <see cref="FixedJsonConverter"/> as a number; enums →
        /// name-only), so the single quantization boundary holds and no hand-rolled <c>ToFloat</c> escapes the
        /// converter. Nullable child nodes are OMITTED when null (Read treats missing and explicit-null identically —
        /// the cleaner mirror). The <c>Modifier</c> object carries NO <c>kind</c> (mirrors <see cref="ReadModifier"/>).
        ///
        /// DETERMINISM: authoring-only — never invoked inside a tick or <c>SimChecksum.Compute</c> (the sim consumes
        /// the already-built runtime graph; only the editor/loader serialize). AlgoVersion is unaffected (AC4).
        /// </summary>
        public override void Write(Utf8JsonWriter writer, EffectNode value, JsonSerializerOptions options) =>
            WriteNode(writer, value, options);

        // ── Node writing (the exact inverse of ReadNode; emits only each kind's allow-listed keys) ───────────────

        private static void WriteNode(Utf8JsonWriter writer, EffectNode node, JsonSerializerOptions options)
        {
            // Fail-closed on a null child. Read never yields a null child, but the SequenceEffect/SearchAreaEffect
            // ctors + the validator's walk tolerate one, so a directly-constructed malformed graph could carry it —
            // surface a located JsonException, never an NRE from the default branch's node.GetType() (keeps the
            // "exact inverse of Read" contract honest and authoring-only Write fail-closed).
            if (node is null)
                throw new JsonException("Cannot serialize a null effect node (malformed graph: a Sequence/SearchArea child is null).");
            writer.WriteStartObject();
            switch (node)
            {
                // ── Leaves ──
                case DirectHpDeltaEffect d:
                    writer.WriteString("kind", KindDirectHpDelta);
                    WriteFixed(writer, "delta", d.Delta, options);
                    WriteRequireTag(writer, d.RequireTag, options);   // Story 2.11 (omit-when-None)
                    break;

                case HealEffect h:
                    writer.WriteString("kind", KindHeal);
                    WriteFixed(writer, "amount", h.Amount, options);
                    WriteRequireTag(writer, h.RequireTag, options);   // Story 2.11 (omit-when-None)
                    break;

                case DamageEffect dm:
                    writer.WriteString("kind", KindDamage);
                    WriteFixed(writer, "amount", dm.Amount, options);
                    WriteEnum(writer, "damage_type", dm.Type, options);   // accessor is .Type (NOT .DamageType); mirrors Read
                    WriteRequireTag(writer, dm.RequireTag, options);   // Story 2.11 (omit-when-None)
                    break;

                case ApplyModifierEffect am:
                    writer.WriteString("kind", KindApplyModifier);
                    writer.WritePropertyName("modifier");
                    WriteModifier(writer, am.Modifier, options);
                    WriteRequireTag(writer, am.RequireTag, options);   // Story 2.11 (omit-when-None)
                    break;

                // ── Composition nodes ──
                case SequenceEffect s:
                    writer.WriteString("kind", KindSequence);
                    writer.WritePropertyName("children");
                    writer.WriteStartArray();
                    foreach (EffectNode child in s.Children)
                        WriteNode(writer, child, options);
                    writer.WriteEndArray();
                    break;

                case SearchAreaEffect sa:
                    writer.WriteString("kind", KindSearchArea);
                    WriteFixed(writer, "radius", sa.Radius, options);
                    WriteEnum(writer, "filter", sa.Filter, options);
                    WriteRequireTag(writer, sa.RequireTag, options);   // Story 2.11 (omit-when-None)
                    writer.WritePropertyName("child");
                    WriteNode(writer, sa.Child, options);
                    break;

                case PersistentEffect p:
                    writer.WriteString("kind", KindPersistent);
                    WriteOptionalChild(writer, "initial_effect", p.InitialEffect, options);
                    WriteOptionalChild(writer, "period_effect",  p.PeriodEffect,  options);
                    WriteOptionalChild(writer, "expire_effect",  p.ExpireEffect,  options);
                    writer.WriteNumber("period_ticks", p.PeriodTicks);
                    writer.WriteNumber("period_count", p.PeriodCount);
                    if (p.Lifelong) writer.WriteBoolean("lifelong", true); // Story 2.13 — omit when false (existing content byte-identical)
                    break;

                default:
                    // Fail-closed: a node type outside the closed registry cannot be authored (mirrors Read's default).
                    throw new JsonException(
                        $"Cannot serialize effect node of type '{node.GetType().Name}': not in the closed kind registry (authoring-only Write).");
            }
            writer.WriteEndObject();
        }

        private static void WriteModifier(Utf8JsonWriter writer, Modifier m, JsonSerializerOptions options)
        {
            // No "kind" — ReadModifier reads a bare modifier object (one converter owns the whole effect+modifier surface).
            writer.WriteStartObject();
            writer.WriteNumber("id", m.Id);
            writer.WriteNumber("duration_ticks", m.DurationTicks);
            WriteEnum(writer, "stacking", m.Stacking, options);
            writer.WriteNumber("max_stacks", m.MaxStacks);
            WriteFixed(writer, "max_health_delta", m.MaxHealthDelta, options);
            WriteFixed(writer, "attack_damage_delta", m.AttackDamageDelta, options);
            WriteFixed(writer, "move_speed_delta", m.MoveSpeedDelta, options);
            WriteFixed(writer, "armor_delta", m.ArmorDelta, options);
            WriteEnum(writer, "status", m.Status, options);
            WriteOptionalChild(writer, "period_effect", m.PeriodEffect, options);
            writer.WriteNumber("period_ticks", m.PeriodTicks);
            // DW-272 / Story 15.12: OMIT-WHEN-None (the require_tag/lifelong discipline) so every pre-15.12 modifier
            // round-trips byte-identically (Read treats a missing periodic_stack_mode as None). Emitted as the enum NAME.
            if (m.PeriodicStacking != PeriodicStackMode.None)
                WriteEnum(writer, "periodic_stack_mode", m.PeriodicStacking, options);
            writer.WriteEndObject();
        }

        private static void WriteOptionalChild(Utf8JsonWriter writer, string name, EffectNode? child, JsonSerializerOptions options)
        {
            if (child is null) return;          // Read treats missing == null; omitting is the clean mirror
            writer.WritePropertyName(name);
            WriteNode(writer, child, options);
        }

        private static void WriteFixed(Utf8JsonWriter writer, string name, Fixed value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, options);   // → FixedJsonConverter (number); never a hand-rolled ToFloat (CHM0005)
        }

        private static void WriteEnum<TEnum>(Utf8JsonWriter writer, string name, TEnum value, JsonSerializerOptions options)
            where TEnum : struct, Enum
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, options);   // → JsonStringEnumConverter (name-only, allowIntegerValues:false)
        }

        // Story 2.11 (D-4 / AC3.4): the optional tag gate shared by search_area AND every leaf kind. OMIT-WHEN-None so
        // every pre-2.11 node round-trips byte-identically (Read treats missing require_tag as None) — the "Write is the
        // exact inverse of Read" contract (2.5a). require_tag is emitted as a single UnitTag enum name ("Mechanical").
        private static void WriteRequireTag(Utf8JsonWriter writer, UnitTag requireTag, JsonSerializerOptions options)
        {
            if (requireTag != UnitTag.None) WriteEnum(writer, "require_tag", requireTag, options);
        }

        // ── Node dispatch ────────────────────────────────────────────────────────

        private static EffectNode ReadNode(JsonElement el, JsonSerializerOptions options, int depth, string path)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path}: effect node must be a JSON object, got {el.ValueKind}.");

            string kind = ReadKind(el, path);

            switch (kind)
            {
                // ── Leaves (no composition depth; no child-node recursion except the modifier's period effect) ──
                case KindDirectHpDelta:
                {
                    RejectUnknownProperties(el, path, "kind", "delta", "require_tag");
                    Fixed delta = ReadFixed(el, "delta", path, options);
                    return new DirectHpDeltaEffect(delta, ReadRequireTag(el, path, options));
                }
                case KindHeal:
                {
                    RejectUnknownProperties(el, path, "kind", "amount", "require_tag");
                    Fixed amount = ReadFixed(el, "amount", path, options);
                    return new HealEffect(amount, ReadRequireTag(el, path, options));
                }
                case KindDamage:
                {
                    RejectUnknownProperties(el, path, "kind", "amount", "damage_type", "require_tag");
                    Fixed amount = ReadFixed(el, "amount", path, options);
                    DamageType type = ReadEnum<DamageType>(el, "damage_type", path, options, required: true);
                    if (type == DamageType.COUNT)
                        throw new JsonException($"{path}.damage_type: 'COUNT' is an internal sentinel, not an authorable damage type.");
                    return new DamageEffect(amount, type, ReadRequireTag(el, path, options));
                }
                case KindApplyModifier:
                {
                    RejectUnknownProperties(el, path, "kind", "modifier", "require_tag");
                    if (!el.TryGetProperty("modifier", out JsonElement modEl))
                        throw new JsonException($"{path}: apply_modifier is missing its required 'modifier' object.");
                    return new ApplyModifierEffect(ReadModifier(modEl, options, depth, $"{path}.modifier"), ReadRequireTag(el, path, options));
                }

                // ── Composition nodes (carry the parse-time depth guard, mirroring EffectBounds' threshold) ──
                case KindSequence:
                {
                    RejectUnknownProperties(el, path, "kind", "children");
                    GuardDepth(depth, path);
                    if (!el.TryGetProperty("children", out JsonElement childrenEl) || childrenEl.ValueKind != JsonValueKind.Array)
                        throw new JsonException($"{path}: sequence is missing its required 'children' array.");
                    var children = new List<EffectNode>(childrenEl.GetArrayLength());
                    int i = 0;
                    foreach (JsonElement childEl in childrenEl.EnumerateArray())
                        children.Add(ReadNode(childEl, options, depth + 1, $"{path}.children[{i++}]"));
                    return new SequenceEffect(children.ToArray());
                }
                case KindSearchArea:
                {
                    RejectUnknownProperties(el, path, "kind", "radius", "filter", "child", "require_tag");
                    GuardDepth(depth, path);
                    Fixed radius = ReadFixed(el, "radius", path, options);
                    TargetFilter filter = ReadEnum<TargetFilter>(el, "filter", path, options, required: true);
                    // Story 2.9a: Air/Ground/Structure are now EVALUATED by TargetMatcher (via the shared
                    // DomainClassifier), so the fail-closed reject that stood here in 2.1 is lifted — an authored
                    // SearchArea filter carrying a domain bit deserializes and filters by domain. ("No domain bit set"
                    // still means all domains, so pre-2.9a filters are unchanged.)
                    // Story 2.11 (AC3.4): optional tag AND-constraint (a single UnitTag enum string, "Mechanical");
                    // missing → None (back-compat); an unknown token is rejected fail-closed by the enum converter.
                    UnitTag requireTag = ReadRequireTag(el, path, options);
                    EffectNode child = ReadRequiredChild(el, "child", options, depth + 1, $"{path}.child");
                    return new SearchAreaEffect(radius, filter, child, requireTag);
                }
                case KindPersistent:
                {
                    RejectUnknownProperties(el, path, "kind",
                        "initial_effect", "period_effect", "expire_effect", "period_ticks", "period_count", "lifelong");
                    GuardDepth(depth, path);
                    EffectNode? initial = ReadOptionalChild(el, "initial_effect", options, depth + 1, $"{path}.initial_effect");
                    EffectNode? period  = ReadOptionalChild(el, "period_effect",  options, depth + 1, $"{path}.period_effect");
                    EffectNode? expire  = ReadOptionalChild(el, "expire_effect",  options, depth + 1, $"{path}.expire_effect");
                    int periodTicks = ReadInt(el, "period_ticks", path, 0);
                    int periodCount = ReadInt(el, "period_count", path, 0);
                    // Story 2.13 (AC4.2): optional lifelong flag (missing/false → expires at the cap as before; only
                    // `true` re-arms). Read leniently — present-and-true ⇒ true.
                    bool lifelong = el.TryGetProperty("lifelong", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.True;
                    return new PersistentEffect(initial, period, expire, periodTicks, periodCount, lifelong);
                }

                default:
                    throw new JsonException($"{path}: unknown kind '{kind}'.");
            }
        }

        // ── Modifier descriptor (folded into this converter; one converter owns the whole effect surface) ──

        private static Modifier ReadModifier(JsonElement el, JsonSerializerOptions options, int depth, string path)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path}: modifier must be a JSON object, got {el.ValueKind}.");

            RejectUnknownProperties(el, path,
                "id", "duration_ticks", "stacking", "max_stacks",
                "max_health_delta", "attack_damage_delta", "move_speed_delta", "armor_delta",
                "status", "period_effect", "period_ticks", "periodic_stack_mode");

            int id                 = ReadInt(el, "id", path, 0);
            int durationTicks      = ReadInt(el, "duration_ticks", path, 0);   // <0 = permanent, 0 = ONE TICK (DW-270; AbilityValidator warns)
            StackRule stacking     = ReadEnum<StackRule>(el, "stacking", path, options, required: false, fallback: StackRule.Refresh);
            int maxStacks          = ReadInt(el, "max_stacks", path, 1);
            Fixed maxHealthDelta   = ReadFixedOpt(el, "max_health_delta", path, options, Fixed.Zero);
            Fixed attackDamageDelta = ReadFixedOpt(el, "attack_damage_delta", path, options, Fixed.Zero);
            Fixed moveSpeedDelta   = ReadFixedOpt(el, "move_speed_delta", path, options, Fixed.Zero);
            Fixed armorDelta       = ReadFixedOpt(el, "armor_delta", path, options, Fixed.Zero);   // Story 2.6
            StatusFlags status     = ReadEnum<StatusFlags>(el, "status", path, options, required: false, fallback: StatusFlags.None);
            // The modifier's periodic effect (DoT/HoT) is itself a nested effect node — recurse (depth+1, conservative
            // parse-time bound; ApplyModifier is a structural leaf so this is for stack-safety, not EffectBounds depth).
            EffectNode? periodEffect = ReadOptionalChild(el, "period_effect", options, depth + 1, $"{path}.period_effect");
            int periodTicks        = ReadInt(el, "period_ticks", path, 0);
            // DW-272 / Story 15.12: name-based enum, missing → None (back-compat); an unknown token is rejected
            // fail-closed by the enum converter, exactly like `stacking`/`status`.
            PeriodicStackMode periodicStacking = ReadEnum<PeriodicStackMode>(el, "periodic_stack_mode", path, options, required: false, fallback: PeriodicStackMode.None);

            return new Modifier(id, durationTicks, stacking, maxStacks,
                                maxHealthDelta, attackDamageDelta, moveSpeedDelta,
                                status, periodEffect, periodTicks, armorDelta, periodicStacking);
        }

        // ── Field readers (each wraps a value-converter error with the located field path) ──

        private static string ReadKind(JsonElement el, string path)
        {
            if (!el.TryGetProperty("kind", out JsonElement kindEl))
                throw new JsonException($"{path}: effect node is missing its required string 'kind' discriminator.");
            if (kindEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.kind: must be a string, got {kindEl.ValueKind}.");
            return kindEl.GetString()!;
        }

        private static Fixed ReadFixed(JsonElement parent, string prop, string path, JsonSerializerOptions options)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el))
                throw new JsonException($"{path}.{prop}: missing required numeric field.");
            try { return el.Deserialize<Fixed>(options); }            // routes through FixedJsonConverter (the one quantizer)
            catch (JsonException ex) { throw new JsonException($"{path}.{prop}: {ex.Message}"); }
        }

        private static Fixed ReadFixedOpt(JsonElement parent, string prop, string path, JsonSerializerOptions options, Fixed fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            try { return el.Deserialize<Fixed>(options); }
            catch (JsonException ex) { throw new JsonException($"{path}.{prop}: {ex.Message}"); }
        }

        private static int ReadInt(JsonElement parent, string prop, string path, int fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
                throw new JsonException($"{path}.{prop}: must be a 32-bit integer.");
            return v;
        }

        private static TEnum ReadEnum<TEnum>(JsonElement parent, string prop, string path, JsonSerializerOptions options,
                                             bool required, TEnum fallback = default) where TEnum : struct, Enum
        {
            if (!parent.TryGetProperty(prop, out JsonElement el))
            {
                if (required) throw new JsonException($"{path}.{prop}: missing required '{typeof(TEnum).Name}' value (authored by name).");
                return fallback;
            }
            try { return el.Deserialize<TEnum>(options); }            // routes through JsonStringEnumConverter (name-only)
            catch (JsonException ex) { throw new JsonException($"{path}.{prop}: {ex.Message}"); }
        }

        // Story 2.11 (D-4 / AC3.4): the optional single-target/area tag gate, shared by every leaf kind + search_area.
        // A single UnitTag enum string ("Mechanical"); missing → None (back-compat); an UNKNOWN token (e.g. "Undead")
        // is rejected fail-closed here by ContentJson's name-only enum converter (allowIntegerValues:false), located.
        private static UnitTag ReadRequireTag(JsonElement el, string path, JsonSerializerOptions options)
            => ReadEnum<UnitTag>(el, "require_tag", path, options, required: false, fallback: UnitTag.None);

        private static EffectNode ReadRequiredChild(JsonElement parent, string prop, JsonSerializerOptions options, int depth, string path)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el))
                throw new JsonException($"{path}: missing required child effect node.");
            return ReadNode(el, options, depth, path);
        }

        private static EffectNode? ReadOptionalChild(JsonElement parent, string prop, JsonSerializerOptions options, int depth, string path)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
                return null;
            return ReadNode(el, options, depth, path);
        }

        // ── Guards ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parse-time composition-depth guard (anti-stack-overflow). <paramref name="depth"/> is the composition
        /// frame-depth (root = 0), exactly EffectBounds' counter — so a composition node at depth ≥
        /// <see cref="EffectCaps.MaxEffectDepth"/> is rejected here AND would be by EffectBounds (no valid graph is
        /// ever false-rejected; the deepest valid path is MaxEffectDepth compositions then a leaf).
        /// </summary>
        private static void GuardDepth(int depth, string path)
        {
            if (depth >= EffectCaps.MaxEffectDepth)
                throw new JsonException(
                    $"{path}: composition nesting reaches depth {depth}, exceeds MaxEffectDepth={EffectCaps.MaxEffectDepth} (rejected at parse).");
        }

        /// <summary>
        /// Fail-closed unknown-property scan for one object. Any property whose name is not in
        /// <paramref name="allowed"/> is a located reject — closing the hole that <c>UnmappedMemberHandling.Disallow</c>
        /// leaves (it does not reach inside a custom converter, so a stray/script-carrying property on an effect
        /// object would otherwise be silently skipped, AR-22).
        /// </summary>
        private static void RejectUnknownProperties(JsonElement el, string path, params string[] allowed)
        {
            // Track which allowed slot each property maps to, so a DUPLICATE key is a located reject too. JsonDocument
            // permits duplicate property names and TryGetProperty silently takes the FIRST — without this, a second
            // "amount"/"kind" could smuggle a value (e.g. an over-range number) past validation (fail-closed, AR-22).
            Span<bool> seen = stackalloc bool[allowed.Length];
            foreach (JsonProperty p in el.EnumerateObject())
            {
                int idx = -1;
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(p.Name, allowed[i], StringComparison.Ordinal)) { idx = i; break; }
                if (idx < 0)
                    throw new JsonException(
                        $"{path}.{p.Name}: unknown property (effect objects are closed — no scripting/extension escape hatch, AR-22).");
                if (seen[idx])
                    throw new JsonException(
                        $"{path}.{p.Name}: duplicate property (each field may appear at most once, AR-22).");
                seen[idx] = true;
            }
        }
    }
}
