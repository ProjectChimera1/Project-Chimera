#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;      // Fixed
using ProjectChimera.Effects;   // EffectNode

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 (AR-22) — the closed-registry polymorphic converter for the graph-canonical trigger IR, modeled
    /// EXACTLY on <c>EffectNodeJsonConverter</c>. Dispatches on a closed <c>"kind"</c> discriminator against a
    /// HARDCODED registry (<see cref="NodeKinds"/>: the closed event/condition/action type sets ∪
    /// {"trigger","run_effect"}), building each node via its public constructor — NO reflection, NO
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> (forbidden project-wide: incompatible with
    /// <c>UnmappedMemberHandling.Disallow</c>). There is no open extension point and no scripting escape hatch — an
    /// unauthored/script <c>kind</c> simply isn't registered and is rejected fail-closed.
    ///
    /// FAIL-CLOSED on read (every branch returns a LOCATED <see cref="JsonException"/> whose message is
    /// <c>"&lt;path&gt;: &lt;reason&gt;"</c>): unknown <c>kind</c> → located reject naming the kind; UNKNOWN or
    /// DUPLICATE property on any node object → located reject naming the property (Disallow governs only the POCO
    /// layer; a custom converter must reject strays itself, mirroring
    /// <c>EffectNodeJsonConverter.RejectUnknownProperties</c>); a malformed scalar → located reject.
    ///
    /// The <see cref="EffectActionNode"/> (<c>run_effect</c>) reads/writes its <c>effect</c> child by DELEGATING to
    /// the registered <c>EffectNodeJsonConverter</c> (via <see cref="JsonSerializer"/> with <c>options</c>) — never
    /// a reimplementation of the effect graph, never a second executor. Each node subtree is read into a transient
    /// <see cref="JsonDocument"/> (load-time only), so discrimination is order-independent and stray/duplicate
    /// properties are detectable. <see cref="Write"/> is the exact inverse, emitting <c>id</c> + <c>kind</c> + that
    /// kind's allow-listed fields; <c>Fixed</c> via the registered <c>FixedJsonConverter</c>.
    /// </summary>
    public sealed class NodeBaseJsonConverter : JsonConverter<NodeBase>
    {
        /// <inheritdoc />
        public override NodeBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            return ReadNode(doc.RootElement, options, path: "node");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, NodeBase value, JsonSerializerOptions options)
        {
            if (value is null)
                throw new JsonException("Cannot serialize a null graph node (malformed graph).");

            writer.WriteStartObject();
            writer.WriteNumber("id", value.Id);
            switch (value)
            {
                case TriggerNode t:
                    writer.WriteString("kind", NodeKinds.Trigger);
                    writer.WriteString("name", t.Name);
                    writer.WriteBoolean("enabled", t.Enabled);
                    writer.WriteBoolean("run_once", t.RunOnce);
                    WriteFixed(writer, "cooldown_seconds", t.CooldownSeconds, options);
                    writer.WriteNumber("priority", t.Priority);
                    break;

                case EventNode e:
                    writer.WriteString("kind", e.Kind);
                    writer.WriteNumber("faction", e.Faction);
                    WriteOptString(writer, "building_type", e.BuildingType);
                    WriteOptString(writer, "timer_name", e.TimerName);
                    WriteFixed(writer, "amount", e.Amount, options);
                    writer.WriteNumber("count", e.Count);
                    writer.WriteString("operator", e.Operator);
                    break;

                case ConditionNode c:
                    writer.WriteString("kind", c.Kind);
                    writer.WriteNumber("faction", c.Faction);
                    WriteOptString(writer, "building_type", c.BuildingType);
                    WriteFixed(writer, "amount", c.Amount, options);
                    writer.WriteNumber("count", c.Count);
                    WriteOptString(writer, "variable", c.Variable);
                    WriteOptString(writer, "region_id", c.RegionId);
                    writer.WriteNumber("value", c.Value);
                    writer.WriteString("operator", c.Operator);
                    break;

                case ActionNode a:
                    writer.WriteString("kind", a.Kind);
                    WriteOptString(writer, "unit_id", a.UnitId);
                    writer.WriteNumber("faction", a.Faction);
                    WriteFixed(writer, "x", a.X, options);
                    WriteFixed(writer, "z", a.Z, options);
                    writer.WriteNumber("count", a.Count);
                    WriteOptString(writer, "text", a.Text);
                    WriteFixed(writer, "duration", a.Duration, options);
                    WriteOptString(writer, "timer_name", a.TimerName);
                    WriteFixed(writer, "timer_seconds", a.TimerSeconds, options);
                    WriteFixed(writer, "amount", a.Amount, options);
                    writer.WriteNumber("value", a.Value);
                    WriteOptString(writer, "variable", a.Variable);
                    WriteOptString(writer, "sound_id", a.SoundId);
                    break;

                case EffectActionNode ea:
                    writer.WriteString("kind", NodeKinds.RunEffect);
                    if (ea.Effect is null)
                        throw new JsonException("Cannot serialize a run_effect node with a null embedded effect (malformed graph).");
                    writer.WritePropertyName("effect");
                    JsonSerializer.Serialize(writer, ea.Effect, options);   // → EffectNodeJsonConverter (no second executor)
                    break;

                default:
                    // Fail-closed: a node type outside the closed registry cannot be authored (mirrors Read's default).
                    throw new JsonException(
                        $"Cannot serialize graph node of type '{value.GetType().Name}': not in the closed kind registry.");
            }
            writer.WriteEndObject();
        }

        // ── Node dispatch (read) ─────────────────────────────────────────────────

        private static NodeBase ReadNode(JsonElement el, JsonSerializerOptions options, string path)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path}: graph node must be a JSON object, got {el.ValueKind}.");

            string kind = ReadKind(el, path);

            if (kind == NodeKinds.Trigger)
            {
                RejectUnknownProperties(el, path, "id", "kind", "name", "enabled", "run_once", "cooldown_seconds", "priority");
                return new TriggerNode
                {
                    Id              = ReadId(el, path),
                    Name            = ReadString(el, "name", path, "Trigger"),
                    Enabled         = ReadBool(el, "enabled", path, true),
                    RunOnce         = ReadBool(el, "run_once", path, false),
                    CooldownSeconds = ReadFixed(el, "cooldown_seconds", path, options, Fixed.Zero),
                    Priority        = ReadInt(el, "priority", path, 0),
                };
            }

            if (kind == NodeKinds.RunEffect)
            {
                RejectUnknownProperties(el, path, "id", "kind", "effect");
                if (!el.TryGetProperty("effect", out JsonElement effEl))
                    throw new JsonException($"{path}: run_effect is missing its required 'effect' object.");
                EffectNode effect;
                try { effect = effEl.Deserialize<EffectNode>(options)!; }   // → EffectNodeJsonConverter (byte-faithful embed)
                catch (JsonException ex) { throw new JsonException($"{path}.effect: {ex.Message}"); }
                return new EffectActionNode { Id = ReadId(el, path), Effect = effect };
            }

            if (NodeKinds.InSet(NodeKinds.EventTypes, kind))
            {
                RejectUnknownProperties(el, path, "id", "kind", "faction", "building_type", "timer_name", "amount", "count", "operator");
                return new EventNode
                {
                    Id           = ReadId(el, path),
                    Kind         = kind,
                    Faction      = ReadInt(el, "faction", path, 0),
                    BuildingType = ReadOptString(el, "building_type", path),
                    TimerName    = ReadOptString(el, "timer_name", path),
                    Amount       = ReadFixed(el, "amount", path, options, Fixed.Zero),
                    Count        = ReadInt(el, "count", path, 0),
                    Operator     = ReadString(el, "operator", path, ">="),
                };
            }

            if (NodeKinds.InSet(NodeKinds.ConditionTypes, kind))
            {
                RejectUnknownProperties(el, path, "id", "kind", "faction", "building_type", "amount", "count", "variable", "region_id", "value", "operator");
                return new ConditionNode
                {
                    Id           = ReadId(el, path),
                    Kind         = kind,
                    Faction      = ReadInt(el, "faction", path, 0),
                    BuildingType = ReadOptString(el, "building_type", path),
                    Amount       = ReadFixed(el, "amount", path, options, Fixed.Zero),
                    Count        = ReadInt(el, "count", path, 0),
                    Variable     = ReadOptString(el, "variable", path),
                    RegionId     = ReadOptString(el, "region_id", path),
                    Value        = ReadInt(el, "value", path, 0),
                    Operator     = ReadString(el, "operator", path, ">="),
                };
            }

            if (NodeKinds.InSet(NodeKinds.ActionTypes, kind))
            {
                RejectUnknownProperties(el, path, "id", "kind", "unit_id", "faction", "x", "z", "count",
                    "text", "duration", "timer_name", "timer_seconds", "amount", "value", "variable", "sound_id");
                return new ActionNode
                {
                    Id           = ReadId(el, path),
                    Kind         = kind,
                    UnitId       = ReadOptString(el, "unit_id", path),
                    Faction      = ReadInt(el, "faction", path, 0),
                    X            = ReadFixed(el, "x", path, options, Fixed.Zero),
                    Z            = ReadFixed(el, "z", path, options, Fixed.Zero),
                    Count        = ReadInt(el, "count", path, 1),
                    Text         = ReadOptString(el, "text", path),
                    Duration     = ReadFixed(el, "duration", path, options, Fixed.FromInt(4)),
                    TimerName    = ReadOptString(el, "timer_name", path),
                    TimerSeconds = ReadFixed(el, "timer_seconds", path, options, Fixed.FromInt(30)),
                    Amount       = ReadFixed(el, "amount", path, options, Fixed.Zero),
                    Value        = ReadInt(el, "value", path, 0),
                    Variable     = ReadOptString(el, "variable", path),
                    SoundId      = ReadOptString(el, "sound_id", path),
                };
            }

            throw new JsonException($"{path}: unknown node kind '{kind}'.");
        }

        // ── Field readers (each wraps a value-converter error with the located field path) ──

        private static string ReadKind(JsonElement el, string path)
        {
            if (!el.TryGetProperty("kind", out JsonElement kindEl))
                throw new JsonException($"{path}: graph node is missing its required string 'kind' discriminator.");
            if (kindEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.kind: must be a string, got {kindEl.ValueKind}.");
            return kindEl.GetString()!;
        }

        private static int ReadId(JsonElement el, string path)
        {
            if (!el.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int v))
                throw new JsonException($"{path}: graph node is missing its required 32-bit integer 'id'.");
            return v;
        }

        private static int ReadInt(JsonElement parent, string prop, string path, int fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
                throw new JsonException($"{path}.{prop}: must be a 32-bit integer.");
            return v;
        }

        private static bool ReadBool(JsonElement parent, string prop, string path, bool fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            if (el.ValueKind == JsonValueKind.True)  return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            throw new JsonException($"{path}.{prop}: must be a boolean.");
        }

        private static string ReadString(JsonElement parent, string prop, string path, string fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.{prop}: must be a string, got {el.ValueKind}.");
            return el.GetString()!;
        }

        private static string? ReadOptString(JsonElement parent, string prop, string path)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el) || el.ValueKind == JsonValueKind.Null) return null;
            if (el.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.{prop}: must be a string, got {el.ValueKind}.");
            return el.GetString();
        }

        private static Fixed ReadFixed(JsonElement parent, string prop, string path, JsonSerializerOptions options, Fixed fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            try { return el.Deserialize<Fixed>(options); }            // routes through FixedJsonConverter (the one quantizer)
            catch (JsonException ex) { throw new JsonException($"{path}.{prop}: {ex.Message}"); }
        }

        // ── Field writers ────────────────────────────────────────────────────────

        private static void WriteFixed(Utf8JsonWriter writer, string name, Fixed value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, value, options);   // → FixedJsonConverter (number); never a hand-rolled ToFloat
        }

        private static void WriteOptString(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is null) return;   // Read treats missing == null; omitting is the clean mirror
            writer.WriteString(name, value);
        }

        // ── Fail-closed unknown/duplicate-property scan (mirrors EffectNodeJsonConverter.RejectUnknownProperties) ──

        /// <summary>
        /// Any property whose name is not in <paramref name="allowed"/> is a located reject; a DUPLICATE allowed key
        /// is a located reject too (JsonDocument permits duplicate names and TryGetProperty silently takes the
        /// FIRST — without this, a second value could smuggle past validation). Closes the hole
        /// <c>UnmappedMemberHandling.Disallow</c> leaves inside a custom converter (AR-22).
        /// </summary>
        private static void RejectUnknownProperties(JsonElement el, string path, params string[] allowed)
        {
            Span<bool> seen = stackalloc bool[allowed.Length];
            foreach (JsonProperty p in el.EnumerateObject())
            {
                int idx = -1;
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(p.Name, allowed[i], StringComparison.Ordinal)) { idx = i; break; }
                if (idx < 0)
                    throw new JsonException(
                        $"{path}.{p.Name}: unknown property (graph nodes are closed — no scripting/extension escape hatch, AR-22).");
                if (seen[idx])
                    throw new JsonException(
                        $"{path}.{p.Name}: duplicate property (each field may appear at most once, AR-22).");
                seen[idx] = true;
            }
        }
    }
}
