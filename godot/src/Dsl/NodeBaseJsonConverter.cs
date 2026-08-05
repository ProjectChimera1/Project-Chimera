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
    ///
    /// <para>DW-356 — each node object's properties are captured ONCE into a <see cref="NodeScan"/> and every field
    /// resolves against that capture, instead of re-walking the element (and re-transcoding the name) per field.
    /// Pure cost change; see <see cref="NodeScan"/> for the semantics it must and does preserve, pinned by
    /// <c>NodeBaseJsonConverterScanTests</c>.</para>
    /// </summary>
    public sealed class NodeBaseJsonConverter : JsonConverter<NodeBase>
    {
        /// <inheritdoc />
        public override NodeBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            // DW-356 — capture this node's properties in ONE enumeration pass (see <see cref="NodeScan"/>); every
            // read below resolves against that scan instead of re-scanning the element per field.
            NodeScan scan = NodeScan.Of(doc.RootElement);
            NodeBase node = ReadNode(doc.RootElement, in scan, options, path: "node");
            // Story 7.7 — the optional per-node `_editor` annotation bag: allow-listed on EVERY kind (see
            // RejectUnknownProperties), captured VERBATIM (a Clone survives the transient JsonDocument) and never
            // interpreted. Write() re-emits it, so authoring metadata round-trips; the canonical hash never reads it.
            // Review follow-up: SIZE-CAPPED like every other authored surface (DslBounds.MaxEditorBagBytes) — the
            // bag is round-tripped, never read, so an unbounded one would be the file's one free payload channel.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && scan.TryGet(EditorProperty, out JsonElement editorEl))
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
                    // Story 7.13 — the OPTIONAL state-read selector, omit-when-empty (mirrors expr_var's canonical
                    // omit-at-default encoding; Read defaults it back, so the round-trip stays byte-exact and a
                    // 7.4-era count/distance node serializes byte-identically to before).
                    if (!string.IsNullOrEmpty(ec.Selector))
                        writer.WriteString("selector", ec.Selector);
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

                // ── Story 7.13 — the four action-leaf kinds (exact inverses of the Read branches) ──

                case OrderUnitsNode ou:
                    if (!NodeKinds.InSet(NodeKinds.OrderCommands, ou.Command))
                        throw new JsonException($"Cannot serialize order_units node {ou.Id}: unknown command '{ou.Command}'.");
                    writer.WriteString("kind", NodeKinds.OrderUnits);
                    writer.WriteString("command", ou.Command);
                    writer.WriteNumber("faction", ou.Faction);
                    WriteOptString(writer, "region_id", ou.RegionId);
                    WriteFixed(writer, "x", ou.X, options);
                    WriteFixed(writer, "z", ou.Z, options);
                    break;

                case MoveCameraNode mc:
                    writer.WriteString("kind", NodeKinds.MoveCamera);
                    writer.WriteString("camera_name", mc.CameraName);
                    break;

                case CinematicModeNode cm:
                    writer.WriteString("kind", NodeKinds.CinematicMode);
                    writer.WriteBoolean("enabled", cm.Enabled);
                    break;

                case PlayVfxNode pv:
                    writer.WriteString("kind", NodeKinds.PlayVfx);
                    writer.WriteString("vfx_id", pv.VfxId);
                    WriteFixed(writer, "x", pv.X, options);
                    WriteFixed(writer, "z", pv.Z, options);
                    break;

                // ── Story 7.13 — the weighted container + the three trigger-control leaves (inverses of Read) ──

                case RandomChoiceNode rc:
                    // DW-579 — Write stays the EXACT inverse of Read: an over-cap node (only reachable by
                    // building one in code — both authoring entrances cap it) must not be emitted as JSON the
                    // parser would then reject, the persist-then-cannot-load class. Unreachable from content;
                    // this is the symmetry guard, mirroring the custom_event empty-name serialize reject above.
                    if (rc.Weights.Length > EventBounds.MaxRandomChoiceBranches)
                        throw new JsonException(
                            $"Cannot serialize random_choice node {rc.Id}: {rc.Weights.Length} branches exceed the " +
                            $"{BranchCapName}={EventBounds.MaxRandomChoiceBranches} cap (it would not re-parse).");
                    writer.WriteString("kind", NodeKinds.RandomChoice);
                    writer.WritePropertyName("weights");
                    writer.WriteStartArray();
                    foreach (int w in rc.Weights) writer.WriteNumberValue(w);
                    writer.WriteEndArray();
                    break;

                case EnableTriggerNode en:
                    writer.WriteString("kind", NodeKinds.EnableTrigger);
                    writer.WriteNumber("target_trigger", en.TargetTriggerId);
                    break;

                case DisableTriggerNode di:
                    writer.WriteString("kind", NodeKinds.DisableTrigger);
                    writer.WriteNumber("target_trigger", di.TargetTriggerId);
                    break;

                case RunTriggerNode rt:
                    writer.WriteString("kind", NodeKinds.RunTrigger);
                    writer.WriteNumber("target_trigger", rt.TargetTriggerId);
                    break;

                // ── Story 7.14 — the three objective action-leaf kinds (exact inverses of the Read branches) ──

                case ShowObjectiveNode so:
                    writer.WriteString("kind", NodeKinds.ShowObjective);
                    writer.WriteString("objective_id", so.ObjectiveId);
                    break;

                case CompleteObjectiveNode co:
                    writer.WriteString("kind", NodeKinds.CompleteObjective);
                    writer.WriteString("objective_id", co.ObjectiveId);
                    break;

                case FailObjectiveNode fo:
                    writer.WriteString("kind", NodeKinds.FailObjective);
                    writer.WriteString("objective_id", fo.ObjectiveId);
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

        /// <summary>DW-579 — the qualified NAME of the random_choice branch cap, for the located parse reject
        /// (built from <c>nameof</c> so a rename cannot leave a stale string behind, and a compile-time constant
        /// so the success path allocates nothing).</summary>
        private const string BranchCapName =
            nameof(EventBounds) + "." + nameof(EventBounds.MaxRandomChoiceBranches);

        // ── Node dispatch (read) ─────────────────────────────────────────────────

        private static NodeBase ReadNode(JsonElement el, in NodeScan s, JsonSerializerOptions options, string path)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path}: graph node must be a JSON object, got {el.ValueKind}.");

            string kind = ReadKind(s, path);

            if (kind == NodeKinds.Trigger)
            {
                RejectUnknownProperties(s, path, "id", "kind", "name", "enabled", "run_once", "cooldown_seconds", "priority");
                return new TriggerNode
                {
                    Id              = ReadId(s, path),
                    Name            = ReadString(s, "name", path, "Trigger"),
                    Enabled         = ReadBool(s, "enabled", path, true),
                    RunOnce         = ReadBool(s, "run_once", path, false),
                    CooldownSeconds = ReadFixed(s, "cooldown_seconds", path, options, Fixed.Zero),
                    Priority        = ReadInt(s, "priority", path, 0),
                };
            }

            if (kind == NodeKinds.RunEffect)
            {
                RejectUnknownProperties(s, path, "id", "kind", "effect");
                if (!s.TryGet("effect", out JsonElement effEl))
                    throw new JsonException($"{path}: run_effect is missing its required 'effect' object.");
                EffectNode effect;
                try { effect = effEl.Deserialize<EffectNode>(options)!; }   // → EffectNodeJsonConverter (byte-faithful embed)
                catch (JsonException ex) { throw new JsonException($"{path}.effect: {ex.Message}"); }
                return new EffectActionNode { Id = ReadId(s, path), Effect = effect };
            }

            if (kind == NodeKinds.CustomEvent)
            {
                // Story 7.5 — the graph-only custom-event subscription. 'event_name' is REQUIRED (fail-closed: a
                // subscription naming nothing can never dispatch and is an almost-certain authoring error); the
                // flat EventNode fields are rejected by this allow-list (they are meaningless on a custom
                // subscription, and admitting them would create non-canonical encodings Write cannot reproduce).
                RejectUnknownProperties(s, path, "id", "kind", "event_name");
                string? evName = ReadOptString(s, "event_name", path);
                if (string.IsNullOrEmpty(evName))
                    throw new JsonException($"{path}.event_name: required for custom_event (the declared custom-event name).");
                return new EventNode { Id = ReadId(s, path), Kind = NodeKinds.CustomEvent, EventName = evName };
            }

            if (kind == NodeKinds.RaiseEvent)
            {
                // Story 7.5 — raise_event. 'raiser' is range-checked like expr_var.faction (fail-closed: Write
                // omits every negative raiser as the -1 system default, so a value outside the canonical encoding
                // would silently rewrite on the next round-trip — reject it located instead). Registry membership
                // of 'name' and allowed-raiser membership are load-gate concerns, not parse concerns.
                RejectUnknownProperties(s, path, "id", "kind", "name", "raiser", "next_tick");
                string name = ReadString(s, "name", path, "");
                if (string.IsNullOrEmpty(name))
                    throw new JsonException($"{path}.name: required for raise_event (the declared custom-event name).");
                int raiser = ReadInt(s, "raiser", path, -1); // -1 = system raise
                if (raiser < -1 || raiser >= DslVarTable.PlayerSlots)
                    throw new JsonException(
                        $"{path}.raiser: {raiser} is outside the canonical range (-1 = system, 0..{DslVarTable.PlayerSlots - 1} = faction slot).");
                return new RaiseEventNode
                {
                    Id       = ReadId(s, path),
                    Name     = name,
                    Raiser   = raiser,
                    NextTick = ReadBool(s, "next_tick", path, false),
                };
            }

            if (NodeKinds.InSet(NodeKinds.EventTypes, kind))
            {
                RejectUnknownProperties(s, path, "id", "kind", "faction", "building_type", "timer_name", "amount", "count", "operator");
                return new EventNode
                {
                    Id           = ReadId(s, path),
                    Kind         = kind,
                    Faction      = ReadInt(s, "faction", path, 0),
                    BuildingType = ReadOptString(s, "building_type", path),
                    TimerName    = ReadOptString(s, "timer_name", path),
                    Amount       = ReadFixed(s, "amount", path, options, Fixed.Zero),
                    Count        = ReadInt(s, "count", path, 0),
                    Operator     = ReadOperator(s, path),
                };
            }

            if (NodeKinds.InSet(NodeKinds.ConditionTypes, kind))
            {
                RejectUnknownProperties(s, path, "id", "kind", "faction", "building_type", "amount", "count", "variable", "region_id", "value", "operator");
                return new ConditionNode
                {
                    Id           = ReadId(s, path),
                    Kind         = kind,
                    Faction      = ReadInt(s, "faction", path, 0),
                    BuildingType = ReadOptString(s, "building_type", path),
                    Amount       = ReadFixed(s, "amount", path, options, Fixed.Zero),
                    Count        = ReadInt(s, "count", path, 0),
                    Variable     = ReadOptString(s, "variable", path),
                    RegionId     = ReadOptString(s, "region_id", path),
                    Value        = ReadInt(s, "value", path, 0),
                    Operator     = ReadOperator(s, path),
                };
            }

            if (NodeKinds.InSet(NodeKinds.ActionTypes, kind))
            {
                RejectUnknownProperties(s, path, "id", "kind", "unit_id", "faction", "x", "z", "count",
                    "text", "duration", "timer_name", "timer_seconds", "amount", "value", "variable", "sound_id");
                return new ActionNode
                {
                    Id           = ReadId(s, path),
                    Kind         = kind,
                    UnitId       = ReadOptString(s, "unit_id", path),
                    Faction      = ReadInt(s, "faction", path, 0),
                    X            = ReadFixed(s, "x", path, options, Fixed.Zero),
                    Z            = ReadFixed(s, "z", path, options, Fixed.Zero),
                    Count        = ReadInt(s, "count", path, 1),
                    Text         = ReadOptString(s, "text", path),
                    Duration     = ReadFixed(s, "duration", path, options, Fixed.FromInt(4)),
                    TimerName    = ReadOptString(s, "timer_name", path),
                    TimerSeconds = ReadFixed(s, "timer_seconds", path, options, Fixed.FromInt(30)),
                    Amount       = ReadFixed(s, "amount", path, options, Fixed.Zero),
                    Value        = ReadInt(s, "value", path, 0),
                    Variable     = ReadOptString(s, "variable", path),
                    SoundId      = ReadOptString(s, "sound_id", path),
                };
            }

            // ── Story 7.4 — the five expression kinds ──

            if (kind == NodeKinds.ExprLiteral)
            {
                RejectUnknownProperties(s, path, "id", "kind", "type", "value");
                // Review (7.4 pass 2): 'type' and 'value' are REQUIRED — a missing one used to silently default to
                // Int 0 / false and evaluate, against the converter's otherwise fail-closed posture.
                if (!s.Has("type"))
                    throw new JsonException($"{path}.type: required for expr_literal (Int/Fixed/Bool).");
                if (!s.Has("value"))
                    throw new JsonException($"{path}.value: required for expr_literal.");
                string typeName = ReadString(s, "type", path, "Int");
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
                    DslValueType.Int   => ReadInt(s, "value", path, 0),
                    DslValueType.Fixed => ReadExprFixedExact(s, "value", path),
                    _                  => ReadBool(s, "value", path, false) ? 1 : 0,
                };
                return new ExprLiteralNode { Id = ReadId(s, path), ValueType = vt, Raw = raw };
            }

            if (kind == NodeKinds.ExprVar)
            {
                RejectUnknownProperties(s, path, "id", "kind", "name", "faction");
                int faction = ReadInt(s, "faction", path, -1); // -1 = bare (slot-less) read
                // Fail-closed: Write omits EVERY negative faction as a bare read, so a value outside the canonical
                // encoding would silently rewrite to -1 on the next round-trip (a lossy rewrite, against the
                // module's fail-closed posture) — reject it located instead.
                if (faction < -1 || faction >= DslVarTable.PlayerSlots)
                    throw new JsonException(
                        $"{path}.faction: {faction} is outside the canonical range (-1 = bare read, 0..{DslVarTable.PlayerSlots - 1} = per-player slot).");
                return new ExprVarNode
                {
                    Id      = ReadId(s, path),
                    Name    = ReadString(s, "name", path, ""),
                    Faction = faction,
                };
            }

            if (kind == NodeKinds.ExprUnary)
            {
                RejectUnknownProperties(s, path, "id", "kind", "op");
                string op = ReadString(s, "op", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprUnaryOps, op))
                    throw new JsonException($"{path}.op: '{op}' is not a known expr_unary operator (neg/not).");
                return new ExprUnaryNode { Id = ReadId(s, path), Op = op };
            }

            if (kind == NodeKinds.ExprBinary)
            {
                RejectUnknownProperties(s, path, "id", "kind", "op");
                string op = ReadString(s, "op", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprBinaryOps, op))
                    throw new JsonException($"{path}.op: '{op}' is not a known expr_binary operator.");
                return new ExprBinaryNode { Id = ReadId(s, path), Op = op };
            }

            if (kind == NodeKinds.ExprCall)
            {
                RejectUnknownProperties(s, path, "id", "kind", "fn", "selector");
                string fn = ReadString(s, "fn", path, "");
                if (!NodeKinds.InSet(NodeKinds.ExprCallFns, fn))
                    throw new JsonException($"{path}.fn: '{fn}' is not a known expression built-in.");
                // Story 7.13 — the optional state-read selector (empty when absent; membership/presence rules are
                // compile concerns, per the closed-vocab-resolves-at-compile decision, not parse concerns).
                return new ExprCallNode { Id = ReadId(s, path), Fn = fn, Selector = ReadString(s, "selector", path, "") };
            }

            // ── Story 7.6 — the loop/branch containers + array expression kinds ──

            if (kind == NodeKinds.ForEach)
            {
                RejectUnknownProperties(s, path, "id", "kind", "source", "array_name", "faction", "region_id", "up_to", "loop_var");
                string source = ReadString(s, "source", path, "");
                if (!NodeKinds.InSet(NodeKinds.ForEachSources, source))
                    throw new JsonException($"{path}.source: '{source}' is not a known for_each source (array/faction_units/region_units).");
                return new ForEachNode
                {
                    Id        = ReadId(s, path),
                    Source    = source,
                    ArrayName = ReadOptString(s, "array_name", path),
                    Faction   = ReadInt(s, "faction", path, -1),
                    RegionId  = ReadOptString(s, "region_id", path),
                    UpTo      = ReadInt(s, "up_to", path, 0),
                    LoopVar   = ReadOptString(s, "loop_var", path),
                };
            }

            if (kind == NodeKinds.ForEachBatched)
            {
                RejectUnknownProperties(s, path, "id", "kind", "source", "faction", "region_id", "batch_size");
                string source = ReadString(s, "source", path, "");
                // Entity sources only — an "array" source can NEVER be valid on a batched loop (arrays never need
                // batching by construction), so it fails closed at parse rather than waiting for the gate.
                if (source != "faction_units" && source != "region_units")
                    throw new JsonException($"{path}.source: '{source}' is not a for_each_batched entity source (faction_units/region_units only — arrays never need batching).");
                return new ForEachBatchedNode
                {
                    Id        = ReadId(s, path),
                    Source    = source,
                    Faction   = ReadInt(s, "faction", path, -1),
                    RegionId  = ReadOptString(s, "region_id", path),
                    BatchSize = ReadInt(s, "batch_size", path, 0),
                };
            }

            if (kind == NodeKinds.Branch)
            {
                RejectUnknownProperties(s, path, "id", "kind");
                return new BranchNode { Id = ReadId(s, path) };
            }

            if (kind == NodeKinds.ExprArrayGet)
            {
                RejectUnknownProperties(s, path, "id", "kind", "name");
                return new ExprArrayGetNode { Id = ReadId(s, path), Name = ReadString(s, "name", path, "") };
            }

            if (kind == NodeKinds.ExprArrayLen)
            {
                RejectUnknownProperties(s, path, "id", "kind", "name");
                return new ExprArrayLenNode { Id = ReadId(s, path), Name = ReadString(s, "name", path, "") };
            }

            if (kind == NodeKinds.ExprEventParam)
            {
                // Story 7.5 — event.<name>. 'name' is required (a nameless read can never compile); declaration
                // membership + the single-subscription rule are compile/gate concerns, not parse concerns.
                RejectUnknownProperties(s, path, "id", "kind", "name");
                string epName = ReadString(s, "name", path, "");
                if (string.IsNullOrEmpty(epName))
                    throw new JsonException($"{path}.name: required for expr_event_param (the event parameter name).");
                return new ExprEventParamNode { Id = ReadId(s, path), Name = epName };
            }

            // ── Story 7.13 — the four action-leaf kinds ──

            if (kind == NodeKinds.OrderUnits)
            {
                RejectUnknownProperties(s, path, "id", "kind", "command", "faction", "region_id", "x", "z");
                string command = ReadString(s, "command", path, "");
                if (!NodeKinds.InSet(NodeKinds.OrderCommands, command))
                    throw new JsonException($"{path}.command: '{command}' is not a known order_units command (move/attack_move/stop/hold_position).");
                return new OrderUnitsNode
                {
                    Id       = ReadId(s, path),
                    Command  = command,
                    Faction  = ReadInt(s, "faction", path, -1),
                    RegionId = ReadOptString(s, "region_id", path),
                    X        = ReadFixed(s, "x", path, options, Fixed.Zero),
                    Z        = ReadFixed(s, "z", path, options, Fixed.Zero),
                };
            }

            if (kind == NodeKinds.MoveCamera)
            {
                RejectUnknownProperties(s, path, "id", "kind", "camera_name");
                return new MoveCameraNode { Id = ReadId(s, path), CameraName = ReadString(s, "camera_name", path, "") };
            }

            if (kind == NodeKinds.CinematicMode)
            {
                RejectUnknownProperties(s, path, "id", "kind", "enabled");
                return new CinematicModeNode { Id = ReadId(s, path), Enabled = ReadBool(s, "enabled", path, true) };
            }

            if (kind == NodeKinds.PlayVfx)
            {
                RejectUnknownProperties(s, path, "id", "kind", "vfx_id", "x", "z");
                return new PlayVfxNode
                {
                    Id    = ReadId(s, path),
                    VfxId = ReadString(s, "vfx_id", path, ""),
                    X     = ReadFixed(s, "x", path, options, Fixed.Zero),
                    Z     = ReadFixed(s, "z", path, options, Fixed.Zero),
                };
            }

            // ── Story 7.13 — the weighted container + the three trigger-control leaves ──

            if (kind == NodeKinds.RandomChoice)
            {
                RejectUnknownProperties(s, path, "id", "kind", "weights");
                // 'weights' is REQUIRED — a random_choice with no weights array can never draw a branch (a
                // zero-total/empty node rejects at the load gate, but a missing array is a parse-level malform).
                // DW-579 — and LENGTH-capped here, at the raw-IR entrance: the array maps one-to-one onto rendered
                // branch ports, so an uncapped parse let a hand-authored file drive unbounded editor fan-out.
                return new RandomChoiceNode
                {
                    Id      = ReadId(s, path),
                    Weights = ReadIntArray(s, "weights", path, EventBounds.MaxRandomChoiceBranches, BranchCapName),
                };
            }

            if (kind == NodeKinds.EnableTrigger)
            {
                RejectUnknownProperties(s, path, "id", "kind", "target_trigger");
                return new EnableTriggerNode { Id = ReadId(s, path), TargetTriggerId = ReadTargetTrigger(s, path) };
            }

            if (kind == NodeKinds.DisableTrigger)
            {
                RejectUnknownProperties(s, path, "id", "kind", "target_trigger");
                return new DisableTriggerNode { Id = ReadId(s, path), TargetTriggerId = ReadTargetTrigger(s, path) };
            }

            if (kind == NodeKinds.RunTrigger)
            {
                RejectUnknownProperties(s, path, "id", "kind", "target_trigger");
                return new RunTriggerNode { Id = ReadId(s, path), TargetTriggerId = ReadTargetTrigger(s, path) };
            }

            // ── Story 7.14 — the three objective action-leaf kinds (single required objective_id string) ──

            if (kind == NodeKinds.ShowObjective)
            {
                RejectUnknownProperties(s, path, "id", "kind", "objective_id");
                return new ShowObjectiveNode { Id = ReadId(s, path), ObjectiveId = ReadObjectiveId(s, path) };
            }

            if (kind == NodeKinds.CompleteObjective)
            {
                RejectUnknownProperties(s, path, "id", "kind", "objective_id");
                return new CompleteObjectiveNode { Id = ReadId(s, path), ObjectiveId = ReadObjectiveId(s, path) };
            }

            if (kind == NodeKinds.FailObjective)
            {
                RejectUnknownProperties(s, path, "id", "kind", "objective_id");
                return new FailObjectiveNode { Id = ReadId(s, path), ObjectiveId = ReadObjectiveId(s, path) };
            }

            throw new JsonException($"{path}: unknown node kind '{kind}'.");
        }

        // ── Field readers (each wraps a value-converter error with the located field path) ──

        private static string ReadKind(in NodeScan s, string path)
        {
            if (!s.TryGet("kind", out JsonElement kindEl))
                throw new JsonException($"{path}: graph node is missing its required string 'kind' discriminator.");
            if (kindEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.kind: must be a string, got {kindEl.ValueKind}.");
            return kindEl.GetString()!;
        }

        private static int ReadId(in NodeScan s, string path)
        {
            if (!s.TryGet("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int v))
                throw new JsonException($"{path}: graph node is missing its required 32-bit integer 'id'.");
            return v;
        }

        private static int ReadInt(in NodeScan s, string prop, string path, int fallback)
        {
            if (!s.TryGet(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
                throw new JsonException($"{path}.{prop}: must be a 32-bit integer.");
            return v;
        }

        private static bool ReadBool(in NodeScan s, string prop, string path, bool fallback)
        {
            if (!s.TryGet(prop, out JsonElement el)) return fallback;
            if (el.ValueKind == JsonValueKind.True)  return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            throw new JsonException($"{path}.{prop}: must be a boolean.");
        }

        private static string ReadString(in NodeScan s, string prop, string path, string fallback)
        {
            if (!s.TryGet(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.{prop}: must be a string, got {el.ValueKind}.");
            return el.GetString()!;
        }

        /// <summary>Story 7.7 review — the comparison-operator field of an event/condition node, membership-checked
        /// against the ONE closed vocabulary (<see cref="NodeKinds.Operators"/> — the same set the flat
        /// <c>ScenarioValidator</c> gate aliases), mirroring the expr-op membership checks. An unknown operator
        /// previously constructed and fell into <c>ScenarioDirector.Compare</c>'s silent <c>_ =&gt; false</c> arm
        /// (an inert dead trigger); now it is a located parse reject. Absent keeps the <c>"&gt;="</c> default.</summary>
        private static string ReadOperator(in NodeScan s, string path)
        {
            string op = ReadString(s, "operator", path, ">=");
            if (!NodeKinds.InSet(NodeKinds.Operators, op))
                throw new JsonException($"{path}.operator: '{op}' is not a known comparison operator (>, <, >=, <=, ==, !=).");
            return op;
        }

        /// <summary>Story 7.13 — read a REQUIRED JSON array of 32-bit integers (random_choice weights). A missing
        /// property, a non-array value, or a non-integer element is a located reject (fail-closed).
        ///
        /// <para>DW-579 — LENGTH-CAPPED: <paramref name="maxLength"/> is checked against the array's DECLARED
        /// length before the result buffer is allocated or a single element is read, so a hostile raw-IR file can
        /// never make the parser materialise (nor the T3 canvas render, via the weight-derived branch ports) an
        /// arbitrarily wide node. <paramref name="capName"/> names the constant in the reject, the
        /// <c>MaxEditorBagBytes</c> style — the caller passes the SAME constant the load gate enforces, so parse
        /// and gate agree by construction.</para></summary>
        private static int[] ReadIntArray(in NodeScan s, string prop, string path, int maxLength, string capName)
        {
            if (!s.TryGet(prop, out JsonElement el))
                throw new JsonException($"{path}.{prop}: required (an integer array).");
            if (el.ValueKind != JsonValueKind.Array)
                throw new JsonException($"{path}.{prop}: must be an integer array, got {el.ValueKind}.");
            int n = el.GetArrayLength();
            if (n > maxLength)
                throw new JsonException($"{path}.{prop}: {n} entries exceed the {capName}={maxLength} cap.");
            var result = new int[n];
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int v))
                    throw new JsonException($"{path}.{prop}[{i}]: must be a 32-bit integer.");
                result[i++] = v;
            }
            return result;
        }

        /// <summary>Story 7.13 — read a REQUIRED non-negative target trigger node id (enable/disable/run_trigger).
        /// A missing or negative value is a located reject (the referenced trigger must exist; resolution is a
        /// load-gate concern).</summary>
        private static int ReadTargetTrigger(in NodeScan s, string path)
        {
            if (!s.Has("target_trigger"))
                throw new JsonException($"{path}.target_trigger: required (the persistent node id of the target trigger).");
            int v = ReadInt(s, "target_trigger", path, -1);
            if (v < 0)
                throw new JsonException($"{path}.target_trigger: {v} is not a valid trigger node id (must be non-negative).");
            return v;
        }

        /// <summary>Story 7.14 — read a REQUIRED non-empty objective id (show/complete/fail_objective). A missing or
        /// blank value is a located reject (the referenced objective must exist; id RESOLUTION is a load-gate concern).</summary>
        private static string ReadObjectiveId(in NodeScan s, string path)
        {
            if (!s.Has("objective_id"))
                throw new JsonException($"{path}.objective_id: required (the id of the target objective).");
            string v = ReadString(s, "objective_id", path, "");
            if (string.IsNullOrWhiteSpace(v))
                throw new JsonException($"{path}.objective_id: must be a non-empty objective id.");
            return v;
        }

        private static string? ReadOptString(in NodeScan s, string prop, string path)
        {
            if (!s.TryGet(prop, out JsonElement el) || el.ValueKind == JsonValueKind.Null) return null;
            if (el.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.{prop}: must be a string, got {el.ValueKind}.");
            return el.GetString();
        }

        private static Fixed ReadFixed(in NodeScan s, string prop, string path, JsonSerializerOptions options, Fixed fallback)
        {
            if (!s.TryGet(prop, out JsonElement el)) return fallback;
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
        private static int ReadExprFixedExact(in NodeScan s, string prop, string path)
        {
            if (!s.TryGet(prop, out JsonElement el))
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
        private static void RejectUnknownProperties(in NodeScan s, string path, params string[] allowed)
        {
            Span<bool> seen = stackalloc bool[allowed.Length];
            bool seenEditor = false;
            for (int k = 0; k < s.Count; k++)
            {
                string name = s.NameAt(k);
                if (string.Equals(name, EditorProperty, StringComparison.Ordinal))
                {
                    if (seenEditor)
                        throw new JsonException(
                            $"{path}.{name}: duplicate property (each field may appear at most once, AR-22).");
                    seenEditor = true;
                    continue;
                }
                int idx = -1;
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(name, allowed[i], StringComparison.Ordinal)) { idx = i; break; }
                if (idx < 0)
                    throw new JsonException(
                        $"{path}.{name}: unknown property (graph nodes are closed — no scripting/extension escape hatch, AR-22).");
                if (seen[idx])
                    throw new JsonException(
                        $"{path}.{name}: duplicate property (each field may appear at most once, AR-22).");
                seen[idx] = true;
            }
        }

        // ── DW-356 — the single-pass property scan ───────────────────────────────

        /// <summary>
        /// One graph node's properties, materialized in a SINGLE <see cref="JsonElement.EnumerateObject"/> pass and
        /// then resolved by ordinal name compare.
        ///
        /// <para><b>Why.</b> The v8 <c>CanonicalModelHash</c> cold compute is dominated by
        /// <see cref="TriggerGraph.FromJson"/>, and inside it by REPEATED property lookups: the old shape ran
        /// <see cref="RejectUnknownProperties"/> over the node's properties and then one
        /// <c>JsonElement.TryGetProperty(string)</c> per allow-listed field — and every such call transcodes the
        /// managed name to UTF-8 and re-walks the document's property list. On the pathological max-caps fixture
        /// (~4000 nodes) that scanning alone measured ~45 ms of a ~76 ms parse; resolving the same fields against
        /// this scan measures ~6 ms. Purely a cost change (DW-356): the scan preserves TryGetProperty's
        /// FIRST-occurrence semantics for a duplicated name, is built BEFORE any validation so
        /// <see cref="ReadKind"/> still runs ahead of the allow-list pass, and enumerates in document order so
        /// <see cref="RejectUnknownProperties"/> reports the SAME first offending property. Parse output — and
        /// therefore every hash/golden folded from it — is byte-identical.</para>
        ///
        /// <para>The names/values buffers are per-node locals (no shared/thread-static state, so the converter stays
        /// re-entrant); peak extra memory is one node's property names, which the transient
        /// <see cref="JsonDocument"/> already holds as UTF-8.</para>
        /// </summary>
        private readonly struct NodeScan
        {
            private readonly string[]? _names;
            private readonly JsonElement[]? _values;
            private readonly int _count;

            private NodeScan(string[] names, JsonElement[] values, int count)
            {
                _names = names; _values = values; _count = count;
            }

            /// <summary>Number of properties on the node object (0 for a non-object, which
            /// <see cref="ReadNode"/> rejects separately).</summary>
            public int Count => _count;

            /// <summary>The name of property <paramref name="i"/>, in DOCUMENT order.</summary>
            public string NameAt(int i) => _names![i];

            /// <summary>Capture <paramref name="el"/>'s properties. A non-object yields an empty scan — the
            /// ValueKind reject is <see cref="ReadNode"/>'s, and must stay the first error it reports.</summary>
            public static NodeScan Of(JsonElement el)
            {
                if (el.ValueKind != JsonValueKind.Object) return default;
                var names = new string[8];
                var values = new JsonElement[8];
                int n = 0;
                foreach (JsonProperty p in el.EnumerateObject())
                {
                    if (n == names.Length)
                    {
                        Array.Resize(ref names, n * 2);
                        Array.Resize(ref values, n * 2);
                    }
                    names[n] = p.Name;
                    values[n] = p.Value;
                    n++;
                }
                return new NodeScan(names, values, n);
            }

            /// <summary>The FIRST property named <paramref name="prop"/> (ordinal) — exactly what
            /// <c>JsonElement.TryGetProperty</c> returns when a name is duplicated (duplicates are themselves a
            /// located reject in <see cref="RejectUnknownProperties"/>).</summary>
            public bool TryGet(string prop, out JsonElement value)
            {
                for (int i = 0; i < _count; i++)
                    if (string.Equals(_names![i], prop, StringComparison.Ordinal))
                    {
                        value = _values![i];
                        return true;
                    }
                value = default;
                return false;
            }

            /// <summary>Presence-only form of <see cref="TryGet"/> (the required-field checks).</summary>
            public bool Has(string prop) => TryGet(prop, out _);
        }
    }
}
