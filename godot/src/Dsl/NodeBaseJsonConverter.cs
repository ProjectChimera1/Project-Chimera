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
            NodeBase node = ReadNode(doc.RootElement, options, path: "node");
            // Story 7.7 — the optional per-node `_editor` annotation bag: allow-listed on EVERY kind (see
            // RejectUnknownProperties), captured VERBATIM (a Clone survives the transient JsonDocument) and never
            // interpreted. Write() re-emits it, so authoring metadata round-trips; the canonical hash never reads it.
            // Review follow-up: SIZE-CAPPED like every other authored surface (DslBounds.MaxEditorBagBytes) — the
            // bag is round-tripped, never read, so an unbounded one would be the file's one free payload channel.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(EditorProperty, out JsonElement editorEl))
            {
                int bagBytes = System.Text.Encoding.UTF8.GetByteCount(editorEl.GetRawText());
                if (bagBytes > DslBounds.MaxEditorBagBytes)
                    throw new JsonException(
                        $"node {node.Id}.{EditorProperty}: annotation bag is {bagBytes} bytes, over the " +
                        $"DslBounds.MaxEditorBagBytes={DslBounds.MaxEditorBagBytes} cap.");
                node.Editor = editorEl.Clone();
            }
            return node;
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

                case EventNode e when e.Kind == NodeKinds.CustomEvent:
                    // Story 7.5 — the graph-only custom-event subscription: kind + its required event_name only
                    // (the flat EventNode fields are meaningless on it and never serialize — Read's allow-list
                    // rejects them, so the encoding stays canonical).
                    if (string.IsNullOrEmpty(e.EventName))
                        throw new JsonException($"Cannot serialize custom_event node {e.Id}: 'event_name' is required.");
                    writer.WriteString("kind", NodeKinds.CustomEvent);
                    writer.WriteString("event_name", e.EventName);
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

                case RaiseEventNode r:
                    // Story 7.5 — raise_event: name always; raiser omitted at its -1 (system) default and
                    // next_tick omitted when false, mirroring expr_var's canonical omit-at-default encoding
                    // (Read defaults them back, so the round-trip is byte-exact).
                    writer.WriteString("kind", NodeKinds.RaiseEvent);
                    writer.WriteString("name", r.Name);
                    if (r.Raiser >= 0) writer.WriteNumber("raiser", r.Raiser);
                    if (r.NextTick) writer.WriteBoolean("next_tick", true);
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

                // ── Story 7.4 — the five expression kinds (exact inverses of the Read branches) ──

                case ExprLiteralNode lit:
                    writer.WriteString("kind", NodeKinds.ExprLiteral);
                    switch (lit.ValueType)
                    {
                        case DslValueType.Int:
                            writer.WriteString("type", "Int");
                            writer.WriteNumber("value", lit.Raw);
                            break;
                        case DslValueType.Fixed:
                            writer.WriteString("type", "Fixed");
                            // Review (7.4 pass 2): NOT the float-based FixedJsonConverter. A 16.16 raw beyond float's
                            // 24-bit mantissa silently quantized on save, and raw int.MaxValue rounded UP to 32768f —
                            // a value the converter then REJECTS on reload (persist-then-cannot-load data loss). The
                            // expression path is float-free end-to-end: emit the EXACT decimal (raw/65536 always
                            // terminates in ≤16 decimal digits) via pure integer math; Read parses it back the same way.
                            WriteExprFixedExact(writer, "value", lit.Raw);
                            break;
                        case DslValueType.Bool:
                            writer.WriteString("type", "Bool");
                            writer.WriteBoolean("value", lit.Raw != 0);
                            break;
                        default:
                            // Fail-closed: only Int/Fixed/Bool are literal-able (mirrors Read's reject).
                            throw new JsonException(
                                $"Cannot serialize expr_literal node {lit.Id}: value type '{lit.ValueType}' is not literal-able (Int/Fixed/Bool only).");
                    }
                    break;

                case ExprVarNode ev:
                    writer.WriteString("kind", NodeKinds.ExprVar);
                    writer.WriteString("name", ev.Name);
                    if (ev.Faction >= 0) writer.WriteNumber("faction", ev.Faction); // -1 (bare read) omits, mirroring Read's default
                    break;

                case ExprUnaryNode eu:
                    if (!NodeKinds.InSet(NodeKinds.ExprUnaryOps, eu.Op))
                        throw new JsonException($"Cannot serialize expr_unary node {eu.Id}: unknown op '{eu.Op}'.");
                    writer.WriteString("kind", NodeKinds.ExprUnary);
                    writer.WriteString("op", eu.Op);
                    break;

                case ExprBinaryNode eb:
                    if (!NodeKinds.InSet(NodeKinds.ExprBinaryOps, eb.Op))
                        throw new JsonException($"Cannot serialize expr_binary node {eb.Id}: unknown op '{eb.Op}'.");
                    writer.WriteString("kind", NodeKinds.ExprBinary);
                    writer.WriteString("op", eb.Op);
                    break;

                case ExprCallNode ec:
                    if (!NodeKinds.InSet(NodeKinds.ExprCallFns, ec.Fn))
                        throw new JsonException($"Cannot serialize expr_call node {ec.Id}: unknown fn '{ec.Fn}'.");
                    writer.WriteString("kind", NodeKinds.ExprCall);
                    writer.WriteString("fn", ec.Fn);
                    break;

                // ── Story 7.6 — the loop/branch containers + array expression kinds (exact inverses of Read) ──

                case ForEachNode fe:
                    if (!NodeKinds.InSet(NodeKinds.ForEachSources, fe.Source))
                        throw new JsonException($"Cannot serialize for_each node {fe.Id}: unknown source '{fe.Source}'.");
                    writer.WriteString("kind", NodeKinds.ForEach);
                    writer.WriteString("source", fe.Source);
                    WriteOptString(writer, "array_name", fe.ArrayName);
                    writer.WriteNumber("faction", fe.Faction);
                    WriteOptString(writer, "region_id", fe.RegionId);
                    writer.WriteNumber("up_to", fe.UpTo);
                    WriteOptString(writer, "loop_var", fe.LoopVar);
                    break;

                case ForEachBatchedNode fb:
                    if (fb.Source != "faction_units" && fb.Source != "region_units")
                        throw new JsonException($"Cannot serialize for_each_batched node {fb.Id}: source '{fb.Source}' is not an entity source (faction_units/region_units only).");
                    writer.WriteString("kind", NodeKinds.ForEachBatched);
                    writer.WriteString("source", fb.Source);
                    writer.WriteNumber("faction", fb.Faction);
                    WriteOptString(writer, "region_id", fb.RegionId);
                    writer.WriteNumber("batch_size", fb.BatchSize);
                    break;

                case BranchNode br:
                    writer.WriteString("kind", NodeKinds.Branch);
                    break;

                case ExprArrayGetNode ag:
                    writer.WriteString("kind", NodeKinds.ExprArrayGet);
                    writer.WriteString("name", ag.Name);
                    break;

                case ExprArrayLenNode al:
                    writer.WriteString("kind", NodeKinds.ExprArrayLen);
                    writer.WriteString("name", al.Name);
                    break;

                case ExprEventParamNode ep:
                    // Story 7.5 — the event.<name> read leaf (exact inverse of the Read branch).
                    writer.WriteString("kind", NodeKinds.ExprEventParam);
                    writer.WriteString("name", ep.Name);
                    break;

                default:
                    // Fail-closed: a node type outside the closed registry cannot be authored (mirrors Read's default).
                    throw new JsonException(
                        $"Cannot serialize graph node of type '{value.GetType().Name}': not in the closed kind registry.");
            }
            // Story 7.7 — re-emit the verbatim `_editor` bag LAST (a fixed position keeps ToCanonicalJson
            // deterministic). JsonElement.WriteTo replays the captured tokens through this writer.
            if (value.Editor is JsonElement editor)
            {
                writer.WritePropertyName(EditorProperty);
                editor.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        /// <summary>Story 7.7 — the per-node verbatim annotation property (allow-listed on every kind).</summary>
        private const string EditorProperty = "_editor";

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

            if (kind == NodeKinds.CustomEvent)
            {
                // Story 7.5 — the graph-only custom-event subscription. 'event_name' is REQUIRED (fail-closed: a
                // subscription naming nothing can never dispatch and is an almost-certain authoring error); the
                // flat EventNode fields are rejected by this allow-list (they are meaningless on a custom
                // subscription, and admitting them would create non-canonical encodings Write cannot reproduce).
                RejectUnknownProperties(el, path, "id", "kind", "event_name");
                string? evName = ReadOptString(el, "event_name", path);
                if (string.IsNullOrEmpty(evName))
                    throw new JsonException($"{path}.event_name: required for custom_event (the declared custom-event name).");
                return new EventNode { Id = ReadId(el, path), Kind = NodeKinds.CustomEvent, EventName = evName };
            }

            if (kind == NodeKinds.RaiseEvent)
            {
                // Story 7.5 — raise_event. 'raiser' is range-checked like expr_var.faction (fail-closed: Write
                // omits every negative raiser as the -1 system default, so a value outside the canonical encoding
                // would silently rewrite on the next round-trip — reject it located instead). Registry membership
                // of 'name' and allowed-raiser membership are load-gate concerns, not parse concerns.
                RejectUnknownProperties(el, path, "id", "kind", "name", "raiser", "next_tick");
                string name = ReadString(el, "name", path, "");
                if (string.IsNullOrEmpty(name))
                    throw new JsonException($"{path}.name: required for raise_event (the declared custom-event name).");
                int raiser = ReadInt(el, "raiser", path, -1); // -1 = system raise
                if (raiser < -1 || raiser >= DslVarTable.PlayerSlots)
                    throw new JsonException(
                        $"{path}.raiser: {raiser} is outside the canonical range (-1 = system, 0..{DslVarTable.PlayerSlots - 1} = faction slot).");
                return new RaiseEventNode
                {
                    Id       = ReadId(el, path),
                    Name     = name,
                    Raiser   = raiser,
                    NextTick = ReadBool(el, "next_tick", path, false),
                };
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
                    Operator     = ReadOperator(el, path),
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
                    Operator     = ReadOperator(el, path),
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

            // ── Story 7.4 — the five expression kinds ──

            if (kind == NodeKinds.ExprLiteral)
            {
                RejectUnknownProperties(el, path, "id", "kind", "type", "value");
                // Review (7.4 pass 2): 'type' and 'value' are REQUIRED — a missing one used to silently default to
                // Int 0 / false and evaluate, against the converter's otherwise fail-closed posture.
                if (!el.TryGetProperty("type", out _))
                    throw new JsonException($"{path}.type: required for expr_literal (Int/Fixed/Bool).");
                if (!el.TryGetProperty("value", out _))
                    throw new JsonException($"{path}.value: required for expr_literal.");
                string typeName = ReadString(el, "type", path, "Int");
                DslValueType vt = typeName switch
                {
                    "Int"   => DslValueType.Int,
                    "Fixed" => DslValueType.Fixed,
                    "Bool"  => DslValueType.Bool,
                    _ => throw new JsonException(
                        $"{path}.type: '{typeName}' is not a literal-able value type (Int/Fixed/Bool only)."),
                };
                // Fixed values parse via EXACT integer math (never the float-based FixedJsonConverter) — the inverse
                // of WriteExprFixedExact, and the same round-half-up rule ExprParser applies to text literals, so the
                // same decimal authored as text and as raw-IR yields the same raw (one IR).
                int raw = vt switch
                {
                    DslValueType.Int   => ReadInt(el, "value", path, 0),
                    DslValueType.Fixed => ReadExprFixedExact(el, "value", path),
                    _                  => ReadBool(el, "value", path, false) ? 1 : 0,
                };
                return new ExprLiteralNode { Id = ReadId(el, path), ValueType = vt, Raw = raw };
            }

            if (kind == NodeKinds.ExprVar)
            {
                RejectUnknownProperties(el, path, "id", "kind", "name", "faction");
                int faction = ReadInt(el, "faction", path, -1); // -1 = bare (slot-less) read
                // Fail-closed: Write omits EVERY negative faction as a bare read, so a value outside the canonical
                // encoding would silently rewrite to -1 on the next round-trip (a lossy rewrite, against the
                // module's fail-closed posture) — reject it located instead.
                if (faction < -1 || faction >= DslVarTable.PlayerSlots)
                    throw new JsonException(
                        $"{path}.faction: {faction} is outside the canonical range (-1 = bare read, 0..{DslVarTable.PlayerSlots - 1} = per-player slot).");
                return new ExprVarNode
                {
                    Id      = ReadId(el, path),
                    Name    = ReadString(el, "name", path, ""),
                    Faction = faction,
                };
            }

            if (kind == NodeKinds.ExprUnary)
            {
                RejectUnknownProperties(el, path, "id", "kind", "op");
                string op = ReadString(el, "op", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprUnaryOps, op))
                    throw new JsonException($"{path}.op: '{op}' is not a known expr_unary operator (neg/not).");
                return new ExprUnaryNode { Id = ReadId(el, path), Op = op };
            }

            if (kind == NodeKinds.ExprBinary)
            {
                RejectUnknownProperties(el, path, "id", "kind", "op");
                string op = ReadString(el, "op", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprBinaryOps, op))
                    throw new JsonException($"{path}.op: '{op}' is not a known expr_binary operator.");
                return new ExprBinaryNode { Id = ReadId(el, path), Op = op };
            }

            if (kind == NodeKinds.ExprCall)
            {
                RejectUnknownProperties(el, path, "id", "kind", "fn");
                string fn = ReadString(el, "fn", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprCallFns, fn))
                    throw new JsonException($"{path}.fn: '{fn}' is not a known expression built-in (count/distance/min/max/abs).");
                return new ExprCallNode { Id = ReadId(el, path), Fn = fn };
            }

            // ── Story 7.6 — the loop/branch containers + array expression kinds ──

            if (kind == NodeKinds.ForEach)
            {
                RejectUnknownProperties(el, path, "id", "kind", "source", "array_name", "faction", "region_id", "up_to", "loop_var");
                string source = ReadString(el, "source", path, "");
                if (!NodeKinds.InSet(NodeKinds.ForEachSources, source))
                    throw new JsonException($"{path}.source: '{source}' is not a known for_each source (array/faction_units/region_units).");
                return new ForEachNode
                {
                    Id        = ReadId(el, path),
                    Source    = source,
                    ArrayName = ReadOptString(el, "array_name", path),
                    Faction   = ReadInt(el, "faction", path, -1),
                    RegionId  = ReadOptString(el, "region_id", path),
                    UpTo      = ReadInt(el, "up_to", path, 0),
                    LoopVar   = ReadOptString(el, "loop_var", path),
                };
            }

            if (kind == NodeKinds.ForEachBatched)
            {
                RejectUnknownProperties(el, path, "id", "kind", "source", "faction", "region_id", "batch_size");
                string source = ReadString(el, "source", path, "");
                // Entity sources only — an "array" source can NEVER be valid on a batched loop (arrays never need
                // batching by construction), so it fails closed at parse rather than waiting for the gate.
                if (source != "faction_units" && source != "region_units")
                    throw new JsonException($"{path}.source: '{source}' is not a for_each_batched entity source (faction_units/region_units only — arrays never need batching).");
                return new ForEachBatchedNode
                {
                    Id        = ReadId(el, path),
                    Source    = source,
                    Faction   = ReadInt(el, "faction", path, -1),
                    RegionId  = ReadOptString(el, "region_id", path),
                    BatchSize = ReadInt(el, "batch_size", path, 0),
                };
            }

            if (kind == NodeKinds.Branch)
            {
                RejectUnknownProperties(el, path, "id", "kind");
                return new BranchNode { Id = ReadId(el, path) };
            }

            if (kind == NodeKinds.ExprArrayGet)
            {
                RejectUnknownProperties(el, path, "id", "kind", "name");
                return new ExprArrayGetNode { Id = ReadId(el, path), Name = ReadString(el, "name", path, "") };
            }

            if (kind == NodeKinds.ExprArrayLen)
            {
                RejectUnknownProperties(el, path, "id", "kind", "name");
                return new ExprArrayLenNode { Id = ReadId(el, path), Name = ReadString(el, "name", path, "") };
            }

            if (kind == NodeKinds.ExprEventParam)
            {
                // Story 7.5 — event.<name>. 'name' is required (a nameless read can never compile); declaration
                // membership + the single-subscription rule are compile/gate concerns, not parse concerns.
                RejectUnknownProperties(el, path, "id", "kind", "name");
                string epName = ReadString(el, "name", path, "");
                if (string.IsNullOrEmpty(epName))
                    throw new JsonException($"{path}.name: required for expr_event_param (the event parameter name).");
                return new ExprEventParamNode { Id = ReadId(el, path), Name = epName };
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

        /// <summary>Story 7.7 review — the comparison-operator field of an event/condition node, membership-checked
        /// against the ONE closed vocabulary (<see cref="NodeKinds.Operators"/> — the same set the flat
        /// <c>ScenarioValidator</c> gate aliases), mirroring the expr-op membership checks. An unknown operator
        /// previously constructed and fell into <c>ScenarioDirector.Compare</c>'s silent <c>_ =&gt; false</c> arm
        /// (an inert dead trigger); now it is a located parse reject. Absent keeps the <c>"&gt;="</c> default.</summary>
        private static string ReadOperator(JsonElement parent, string path)
        {
            string op = ReadString(parent, "operator", path, ">=");
            if (!NodeKinds.InSet(NodeKinds.Operators, op))
                throw new JsonException($"{path}.operator: '{op}' is not a known comparison operator (>, <, >=, <=, ==, !=).");
            return op;
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
            // The one quantizer (FixedJsonConverter) via its element entry point — same rulebook as
            // el.Deserialize<Fixed>(options), without per-field JsonSerializer machinery (Story 7.7 perf review:
            // that overhead dominated the max-caps graph parse inside the cold handshake-hash budget).
            try { return ProjectChimera.Core.Definitions.FixedJsonConverter.ReadElement(el); }
            catch (JsonException ex) { throw new JsonException($"{path}.{prop}: {ex.Message}"); }
        }

        // ── Story 7.4 (pass-2 review) — exact integer-math Fixed codec for expr_literal values ──────
        //
        // The legacy-node Fixed fields keep the float-based FixedJsonConverter (the sanctioned AR-14 quantization
        // boundary for CONTENT values, where float precision suffices). Expression literals are different: the
        // story guarantees a float-free expression path and a byte-identical canonical round-trip, and the text
        // parser produces raws (e.g. "32767.99998" → int.MaxValue) that float cannot represent — Fixed.ToFloat on
        // raw int.MaxValue rounds to exactly 32768f, which Read then REJECTS, bricking the persisted scenario.
        // So expr_literal Fixed values (de)serialize through this exact codec instead: 16.16 raws always terminate
        // in ≤16 decimal digits (1/65536 = 5^16/10^16), so Write emits the exact decimal and Read recovers the raw
        // with pure integer math (round-half-up — the SAME rule ExprParser applies, keeping text and raw-IR one IR).

        /// <summary>5^16 — the exact per-unit decimal weight of one 16.16 raw step (1/65536 = 152587890625e-16).</summary>
        private const ulong FRAC_DECIMAL_WEIGHT = 152_587_890_625UL;

        /// <summary>Emit <paramref name="raw"/> (a 16.16 Fixed raw) as its EXACT decimal JSON number.</summary>
        private static void WriteExprFixedExact(Utf8JsonWriter writer, string name, int raw)
        {
            ulong mag  = raw < 0 ? (ulong)(-(long)raw) : (ulong)raw;
            ulong ip   = mag >> 16;
            ulong fr   = mag & 0xFFFF;
            string text;
            if (fr == 0)
            {
                text = (raw < 0 ? "-" : "") + ip.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                // fr/65536 exactly = (fr * 5^16) / 10^16 — 16 zero-padded decimal digits, trailing zeros trimmed.
                string frac = (fr * FRAC_DECIMAL_WEIGHT)
                    .ToString("D16", System.Globalization.CultureInfo.InvariantCulture)
                    .TrimEnd('0');
                text = (raw < 0 ? "-" : "") + ip.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + frac;
            }
            writer.WritePropertyName(name);
            writer.WriteRawValue(text, skipInputValidation: true); // plain decimal digits — always a valid JSON number
        }

        /// <summary>Parse a required expr_literal Fixed <paramref name="prop"/> back to its 16.16 raw with pure
        /// integer math (round-half-up, matching <c>ExprParser</c>). Located rejects: missing/non-number property,
        /// exponent notation, more than 20 fraction digits, out of 16.16 range.</summary>
        private static int ReadExprFixedExact(JsonElement parent, string prop, string path)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el))
                throw new JsonException($"{path}.{prop}: required for expr_literal.");
            if (el.ValueKind != JsonValueKind.Number)
                throw new JsonException($"{path}.{prop}: must be a JSON number, got {el.ValueKind}.");

            string text = el.GetRawText();
            int pos = 0;
            bool neg = pos < text.Length && text[pos] == '-';
            if (neg) pos++;

            long intPart = 0;
            int intDigits = 0;
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
            {
                intDigits++;
                // Accumulate with a saturating cap just past the 16.16 ceiling — the range reject below names it.
                if (intPart <= 32768) intPart = intPart * 10 + (text[pos] - '0');
                pos++;
            }
            if (intDigits == 0)
                throw new JsonException($"{path}.{prop}: malformed Fixed decimal '{text}'.");

            long frac = 0;
            int fracDigits = 0;
            if (pos < text.Length && text[pos] == '.')
            {
                pos++;
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
                {
                    fracDigits++;
                    if (fracDigits > 20)
                        throw new JsonException(
                            $"{path}.{prop}: a Fixed value supports at most 20 fraction digits (exact 16.16 values need 16).");
                    frac = frac * 10 + (text[pos] - '0');
                    pos++;
                }
                if (fracDigits == 0)
                    throw new JsonException($"{path}.{prop}: malformed Fixed decimal '{text}'.");
            }
            if (pos != text.Length) // 'e'/'E' exponent or other residue
                throw new JsonException(
                    $"{path}.{prop}: '{text}' is not a plain decimal (exponent notation is not supported for expr_literal Fixed values).");
            if (intPart > 32768)
                throw new JsonException($"{path}.{prop}: {text} is out of the 16.16 range [-32768, 32768).");

            // raw = round_half_up((intPart + frac/10^k) * 65536), exact in Int128 (≤ ~2e33 ≪ Int128.Max).
            System.Int128 pow10 = 1;
            for (int i = 0; i < fracDigits; i++) pow10 *= 10;
            System.Int128 mag = ((System.Int128)intPart * 65536 * pow10 + (System.Int128)frac * 65536 + pow10 / 2) / pow10;
            System.Int128 signed = neg ? -mag : mag;
            if (signed > int.MaxValue || signed < int.MinValue)
                throw new JsonException($"{path}.{prop}: {text} is out of the 16.16 range [-32768, 32768).");
            return (int)signed;
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
        /// <c>UnmappedMemberHandling.Disallow</c> leaves inside a custom converter (AR-22). Story 7.7: the verbatim
        /// <c>_editor</c> annotation bag is implicitly allow-listed on EVERY kind (still at most once) — it is
        /// captured, never interpreted, and excluded from the canonical hash by construction.
        /// </summary>
        private static void RejectUnknownProperties(JsonElement el, string path, params string[] allowed)
        {
            Span<bool> seen = stackalloc bool[allowed.Length];
            bool seenEditor = false;
            foreach (JsonProperty p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, EditorProperty, StringComparison.Ordinal))
                {
                    if (seenEditor)
                        throw new JsonException(
                            $"{path}.{p.Name}: duplicate property (each field may appear at most once, AR-22).");
                    seenEditor = true;
                    continue;
                }
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
