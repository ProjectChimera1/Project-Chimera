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
        /// <see cref="Write"/> (authoring round-trip / save) is deferred to the Ability Editor (Story 2.5). 2.3 only
        /// LOADS abilities (hand-/AI-authored JSON → validated runtime graph); nothing serializes one yet. Throws a
        /// clear located error rather than emitting a lossy/half-correct node.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, EffectNode value, JsonSerializerOptions options) =>
            throw new NotSupportedException(
                "Serializing an EffectNode is not supported in Story 2.3 (Read-only). Authoring round-trip lands with the Ability Editor (Story 2.5).");

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
                    RejectUnknownProperties(el, path, "kind", "delta");
                    return new DirectHpDeltaEffect(ReadFixed(el, "delta", path, options));
                }
                case KindHeal:
                {
                    RejectUnknownProperties(el, path, "kind", "amount");
                    return new HealEffect(ReadFixed(el, "amount", path, options));
                }
                case KindDamage:
                {
                    RejectUnknownProperties(el, path, "kind", "amount", "damage_type");
                    Fixed amount = ReadFixed(el, "amount", path, options);
                    DamageType type = ReadEnum<DamageType>(el, "damage_type", path, options, required: true);
                    if (type == DamageType.COUNT)
                        throw new JsonException($"{path}.damage_type: 'COUNT' is an internal sentinel, not an authorable damage type.");
                    return new DamageEffect(amount, type);
                }
                case KindApplyModifier:
                {
                    RejectUnknownProperties(el, path, "kind", "modifier");
                    if (!el.TryGetProperty("modifier", out JsonElement modEl))
                        throw new JsonException($"{path}: apply_modifier is missing its required 'modifier' object.");
                    return new ApplyModifierEffect(ReadModifier(modEl, options, depth, $"{path}.modifier"));
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
                    RejectUnknownProperties(el, path, "kind", "radius", "filter", "child");
                    GuardDepth(depth, path);
                    Fixed radius = ReadFixed(el, "radius", path, options);
                    TargetFilter filter = ReadEnum<TargetFilter>(el, "filter", path, options, required: true);
                    EffectNode child = ReadRequiredChild(el, "child", options, depth + 1, $"{path}.child");
                    return new SearchAreaEffect(radius, filter, child);
                }
                case KindPersistent:
                {
                    RejectUnknownProperties(el, path, "kind",
                        "initial_effect", "period_effect", "expire_effect", "period_ticks", "period_count");
                    GuardDepth(depth, path);
                    EffectNode? initial = ReadOptionalChild(el, "initial_effect", options, depth + 1, $"{path}.initial_effect");
                    EffectNode? period  = ReadOptionalChild(el, "period_effect",  options, depth + 1, $"{path}.period_effect");
                    EffectNode? expire  = ReadOptionalChild(el, "expire_effect",  options, depth + 1, $"{path}.expire_effect");
                    int periodTicks = ReadInt(el, "period_ticks", path, 0);
                    int periodCount = ReadInt(el, "period_count", path, 0);
                    return new PersistentEffect(initial, period, expire, periodTicks, periodCount);
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
                "max_health_delta", "attack_damage_delta", "move_speed_delta",
                "status", "period_effect", "period_ticks");

            int id                 = ReadInt(el, "id", path, 0);
            int durationTicks      = ReadInt(el, "duration_ticks", path, 0);   // <0 = permanent, 0 = one-shot
            StackRule stacking     = ReadEnum<StackRule>(el, "stacking", path, options, required: false, fallback: StackRule.Refresh);
            int maxStacks          = ReadInt(el, "max_stacks", path, 1);
            Fixed maxHealthDelta   = ReadFixedOpt(el, "max_health_delta", path, options, Fixed.Zero);
            Fixed attackDamageDelta = ReadFixedOpt(el, "attack_damage_delta", path, options, Fixed.Zero);
            Fixed moveSpeedDelta   = ReadFixedOpt(el, "move_speed_delta", path, options, Fixed.Zero);
            StatusFlags status     = ReadEnum<StatusFlags>(el, "status", path, options, required: false, fallback: StatusFlags.None);
            // The modifier's periodic effect (DoT/HoT) is itself a nested effect node — recurse (depth+1, conservative
            // parse-time bound; ApplyModifier is a structural leaf so this is for stack-safety, not EffectBounds depth).
            EffectNode? periodEffect = ReadOptionalChild(el, "period_effect", options, depth + 1, $"{path}.period_effect");
            int periodTicks        = ReadInt(el, "period_ticks", path, 0);

            return new Modifier(id, durationTicks, stacking, maxStacks,
                                maxHealthDelta, attackDamageDelta, moveSpeedDelta,
                                status, periodEffect, periodTicks);
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
            foreach (JsonProperty p in el.EnumerateObject())
            {
                bool ok = false;
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(p.Name, allowed[i], StringComparison.Ordinal)) { ok = true; break; }
                if (!ok)
                    throw new JsonException(
                        $"{path}.{p.Name}: unknown property (effect objects are closed — no scripting/extension escape hatch, AR-22).");
            }
        }
    }
}
