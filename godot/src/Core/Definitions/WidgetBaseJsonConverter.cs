#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Dsl; // DslBounds.MaxEditorBagBytes

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 7.8 — the closed-registry polymorphic converter for the custom-UI widget tree, modeled EXACTLY on
    /// <c>NodeBaseJsonConverter</c>. Dispatches on a closed <c>"kind"</c> discriminator against a HARDCODED registry
    /// (the eight <see cref="WidgetKind"/> names), building each widget via its public constructor — NO reflection,
    /// NO <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> (forbidden project-wide). There is no open extension
    /// point and no scripting escape hatch — an unauthored <c>kind</c> simply isn't registered and is rejected
    /// fail-closed with a LOCATED error naming it.
    ///
    /// FAIL-CLOSED on read (every branch returns a LOCATED <see cref="JsonException"/> whose message is
    /// <c>"&lt;path&gt;: &lt;reason&gt;"</c>): unknown <c>kind</c> → located reject naming the kind; UNKNOWN or
    /// DUPLICATE property on any widget object → located reject naming the property (a custom converter must reject
    /// strays itself); a malformed scalar/anchor → located reject. The per-widget <c>_editor</c> bag is allow-listed
    /// on EVERY kind, captured VERBATIM, size-capped (<see cref="DslBounds.MaxEditorBagBytes"/>), and never
    /// interpreted. Children are read RECURSIVELY through this same converter (closed all the way down). The JSON
    /// parser's MaxDepth bounds the widget-subtree recursion; <c>CustomUiGate</c> enforces the tighter
    /// <see cref="DslBounds.MaxWidgetDepth"/> at load.
    ///
    /// <see cref="Write"/> is the exact inverse, emitting each kind's allow-listed fields in a FIXED order so
    /// serialization is deterministic (byte-identical for equal models).
    /// </summary>
    public sealed class WidgetBaseJsonConverter : JsonConverter<WidgetBase>
    {
        private const string EditorProperty = "_editor";

        // ── Common fields allow-listed on every kind (kind-specific extras are added per-branch). ──
        private static readonly string[] CommonFields =
            { "id", "kind", "anchor", "x", "y", "w", "h", "visible_bind", "children" };

        /// <inheritdoc />
        public override WidgetBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            return ReadWidget(doc.RootElement, options, path: "widget");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, WidgetBase value, JsonSerializerOptions options)
        {
            if (value is null)
                throw new JsonException("Cannot serialize a null widget (malformed tree).");

            writer.WriteStartObject();
            writer.WriteNumber("id", value.Id);
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteString("anchor", value.Anchor.ToString()); // enum by NAME
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("w", value.W);
            writer.WriteNumber("h", value.H);

            switch (value)
            {
                case PanelWidget:
                    break;
                case LabelWidget l:
                    WriteOptString(writer, "text", l.Text);
                    WriteOptString(writer, "bind", l.Bind);
                    break;
                case CounterWidget c:
                    WriteOptString(writer, "bind", c.Bind);
                    break;
                case ProgressBarWidget p:
                    WriteOptString(writer, "bind", p.Bind);
                    writer.WriteNumber("max", p.Max);
                    break;
                case TimerWidget t:
                    WriteOptString(writer, "bind", t.Bind);
                    break;
                case LeaderboardWidget lb:
                    WriteOptString(writer, "bind", lb.Bind);
                    writer.WriteNumber("rows", lb.Rows);
                    break;
                case FloatingTextWidget ft:
                    WriteOptString(writer, "text", ft.Text);
                    WriteOptString(writer, "bind", ft.Bind);
                    break;
                case ItemListWidget il:
                    WriteOptString(writer, "bind", il.Bind);
                    writer.WriteNumber("rows", il.Rows);
                    break;
                case ButtonWidget b:
                    // Story 7.9 — the write rail. Fixed field order (deterministic serialization): text, event, args,
                    // arg_types, then the local-action fields (only when present, keeping absent/keyless round-trips clean).
                    WriteOptString(writer, "text", b.Text);
                    WriteOptString(writer, "event", b.EventName);
                    WriteButtonArgs(writer, b);
                    if (b.LocalAction != LocalUiAction.None)
                    {
                        writer.WriteString("local_action", b.LocalAction.ToString()); // enum by NAME
                        if (b.LocalTargetWidgetId != -1) writer.WriteNumber("local_target", b.LocalTargetWidgetId);
                        WriteOptString(writer, "local_var", b.LocalVarName);
                        if (b.LocalVarName != null) writer.WriteNumber("local_value", b.LocalVarValue);
                    }
                    break;
                default:
                    throw new JsonException(
                        $"Cannot serialize widget of type '{value.GetType().Name}': not in the closed kind registry.");
            }

            WriteOptString(writer, "visible_bind", value.VisibleBind);

            WidgetBase[] children = value.Children ?? Array.Empty<WidgetBase>();
            if (children.Length > 0)
            {
                writer.WritePropertyName("children");
                writer.WriteStartArray();
                foreach (WidgetBase child in children)
                    JsonSerializer.Serialize(writer, child, options); // → this converter (recursive, closed)
                writer.WriteEndArray();
            }

            // The verbatim `_editor` bag LAST (a fixed position keeps serialization deterministic).
            if (value.Editor is JsonElement editor)
            {
                writer.WritePropertyName(EditorProperty);
                editor.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        // ── Widget dispatch (read) ────────────────────────────────────────────────

        private static WidgetBase ReadWidget(JsonElement el, JsonSerializerOptions options, string path)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"{path}: widget must be a JSON object, got {el.ValueKind}.");

            WidgetKind kind = ReadKind(el, path);

            WidgetBase widget = kind switch
            {
                WidgetKind.Panel        => ReadCommon(el, path, new PanelWidget(), CommonFields),
                WidgetKind.Label        => ReadLabel(el, path),
                WidgetKind.Counter      => ReadCounter(el, path),
                WidgetKind.ProgressBar  => ReadProgressBar(el, path),
                WidgetKind.Timer        => ReadTimer(el, path),
                WidgetKind.Leaderboard  => ReadLeaderboard(el, path),
                WidgetKind.FloatingText => ReadFloatingText(el, path),
                WidgetKind.ItemList     => ReadItemList(el, path),
                WidgetKind.Button       => ReadButton(el, path),
                _ => throw new JsonException($"{path}: unknown widget kind '{kind}'."), // unreachable (ReadKind gates)
            };

            widget.Children = ReadChildren(el, options, path);
            CaptureEditor(el, widget, path);
            return widget;
        }

        private static LabelWidget ReadLabel(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new LabelWidget(), Extend(CommonFields, "text", "bind"));
            w.Text = ReadOptString(el, "text", path);
            w.Bind = ReadOptString(el, "bind", path);
            return w;
        }

        private static CounterWidget ReadCounter(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new CounterWidget(), Extend(CommonFields, "bind"));
            w.Bind = ReadOptString(el, "bind", path);
            return w;
        }

        private static ProgressBarWidget ReadProgressBar(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new ProgressBarWidget(), Extend(CommonFields, "bind", "max"));
            w.Bind = ReadOptString(el, "bind", path);
            w.Max = ReadInt(el, "max", path, 100);
            return w;
        }

        private static TimerWidget ReadTimer(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new TimerWidget(), Extend(CommonFields, "bind"));
            w.Bind = ReadOptString(el, "bind", path);
            return w;
        }

        private static LeaderboardWidget ReadLeaderboard(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new LeaderboardWidget(), Extend(CommonFields, "bind", "rows"));
            w.Bind = ReadOptString(el, "bind", path);
            w.Rows = ReadInt(el, "rows", path, 8);
            return w;
        }

        private static FloatingTextWidget ReadFloatingText(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new FloatingTextWidget(), Extend(CommonFields, "text", "bind"));
            w.Text = ReadOptString(el, "text", path);
            w.Bind = ReadOptString(el, "bind", path);
            return w;
        }

        private static ItemListWidget ReadItemList(JsonElement el, string path)
        {
            var w = ReadCommon(el, path, new ItemListWidget(), Extend(CommonFields, "bind", "rows"));
            w.Bind = ReadOptString(el, "bind", path);
            w.Rows = ReadInt(el, "rows", path, 8);
            return w;
        }

        private static ButtonWidget ReadButton(JsonElement el, string path)
        {
            // Story 7.9 — the write-rail button: caption + optional custom-event target (name + typed args) +
            // optional presentation-only local action. Closed allow-list; unknown/dup props reject fail-closed.
            var w = ReadCommon(el, path, new ButtonWidget(),
                Extend(CommonFields, "text", "event", "args", "arg_types", "local_action", "local_target", "local_var", "local_value"));
            w.Text        = ReadOptString(el, "text", path);
            w.EventName   = ReadOptString(el, "event", path);
            // Canonicalize: every behavior gate treats "" as no-event (IsNullOrEmpty), but the fold distinguishes
            // ""(len 0) from null (len −1) — normalize so two behaviorally identical authorings hash identically
            // (the writer itself never emits "", so this only affects hand-authored files).
            if (w.EventName is { Length: 0 }) w.EventName = null;
            ReadButtonArgs(el, path, w);
            w.LocalAction = ReadLocalAction(el, path);
            w.LocalTargetWidgetId = ReadInt(el, "local_target", path, -1);
            w.LocalVarName  = ReadOptString(el, "local_var", path);
            w.LocalVarValue = ReadInt(el, "local_value", path, 0);

            // PATCH 2 — round-trip symmetry with the writer, which OMITS the local fields when LocalAction==None (and
            // local_value when LocalVarName==null). The canonical fold reads these fields UNCONDITIONALLY, so a
            // gate-valid button carrying stray local fields would fold them yet re-save the omitted defaults →
            // hash divergence. Normalize the in-memory state to exactly what the writer emits so the fold agrees.
            if (w.LocalAction == LocalUiAction.None)
            {
                w.LocalTargetWidgetId = -1;
                w.LocalVarName = null;
                w.LocalVarValue = 0;
            }
            if (w.LocalVarName == null)
                w.LocalVarValue = 0;
            return w;
        }

        /// <summary>Read the button's args LOSSLESSLY: <c>args</c> is an array of raw INTS (each an Int/Bool value or a
        /// <c>Fixed.Raw</c>) read straight into <see cref="ButtonWidget.ArgRaws"/>, and <c>arg_types</c> is the parallel
        /// array of <see cref="ProjectChimera.Dsl.DslValueType"/> enum NAMES read into <see cref="ButtonWidget.ArgTypes"/>.
        /// There is NO float path: a non-integer/overflow arg element is a located reject (via <c>TryGetInt32</c>), so
        /// the raws round-trip byte-exact and the canonical Button fold is re-save-neutral. An unknown type name, or an
        /// <c>arg_types</c> length that disagrees with <c>args</c>, is a located reject. When <c>arg_types</c> is absent,
        /// every type defaults to <c>Int</c> (all raws are ints ⇒ lossless; the gate validates the args against the
        /// event's declared param types).</summary>
        private static void ReadButtonArgs(JsonElement el, string path, ButtonWidget w)
        {
            if (!el.TryGetProperty("args", out JsonElement argsEl) || argsEl.ValueKind == JsonValueKind.Null)
            {
                // arg_types WITHOUT args is authored data the writer would silently drop on the next save — this
                // converter rejects malformed shapes, it never swallows them.
                if (el.TryGetProperty("arg_types", out JsonElement strayTypes) && strayTypes.ValueKind != JsonValueKind.Null)
                    throw new JsonException($"{path}.arg_types: present without 'args' (arg_types annotates args element-wise; author 'args' or remove 'arg_types').");
                return; // no args (local-action-only or param-less event)
            }
            if (argsEl.ValueKind != JsonValueKind.Array)
                throw new JsonException($"{path}.args: must be a JSON array, got {argsEl.ValueKind}.");
            int n = argsEl.GetArrayLength();
            // Parse-level cap: the wire can never carry more than MaxButtonEventParams raws, and the gate only
            // re-checks counts for EVENT buttons — without this cap a local-action-only button could smuggle an
            // arbitrarily large args array through both gates into the canonical fold (unbounded alloc + hash work).
            if (n > ProjectChimera.Dsl.EventBounds.MaxButtonEventParams)
                throw new JsonException(
                    $"{path}.args: {n} args exceed EventBounds.MaxButtonEventParams={ProjectChimera.Dsl.EventBounds.MaxButtonEventParams} (the button wire budget).");
            var raws = new int[n];
            int i = 0;
            foreach (JsonElement e in argsEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int iv))
                    throw new JsonException(
                        $"{path}.args[{i}]: a button arg must be a 32-bit integer raw (Int/Bool value or Fixed.Raw), got {e.ValueKind}.");
                raws[i] = iv;
                i++;
            }

            var types = new ProjectChimera.Dsl.DslValueType[n];
            if (el.TryGetProperty("arg_types", out JsonElement typesEl) && typesEl.ValueKind != JsonValueKind.Null)
            {
                if (typesEl.ValueKind != JsonValueKind.Array)
                    throw new JsonException($"{path}.arg_types: must be a JSON array, got {typesEl.ValueKind}.");
                int tn = typesEl.GetArrayLength();
                if (tn != n)
                    throw new JsonException($"{path}.arg_types: length {tn} does not match args length {n}.");
                int j = 0;
                foreach (JsonElement te in typesEl.EnumerateArray())
                {
                    string tp = $"{path}.arg_types[{j}]";
                    if (te.ValueKind != JsonValueKind.String)
                        throw new JsonException($"{tp}: must be a string, got {te.ValueKind}.");
                    string name = te.GetString()!;
                    if (!Enum.TryParse(name, ignoreCase: false, out ProjectChimera.Dsl.DslValueType parsed) || !Enum.IsDefined(parsed))
                        throw new JsonException($"{tp}: '{name}' is not a known DslValueType.");
                    types[j] = parsed;
                    j++;
                }
            }
            else
            {
                for (int k = 0; k < n; k++) types[k] = ProjectChimera.Dsl.DslValueType.Int; // absent ⇒ all Int (lossless)
            }

            w.ArgRaws  = raws;
            w.ArgTypes = types;
        }

        /// <summary>Write the button's args LOSSLESSLY: <c>args</c> as an array of the raw INTS (<see cref="ButtonWidget.ArgRaws"/>,
        /// never a float) plus a parallel <c>arg_types</c> array of the <see cref="ProjectChimera.Dsl.DslValueType"/> enum
        /// NAMES. Both keys are omitted entirely when there are no args (keeps the keyless round-trip clean). Because the
        /// raws are emitted verbatim, <c>ArgRaws</c> round-trips byte-exact and the canonical Button fold (which folds the
        /// raws, not the types) is re-save-neutral.</summary>
        private static void WriteButtonArgs(Utf8JsonWriter writer, ButtonWidget b)
        {
            int[] raws = b.ArgRaws ?? Array.Empty<int>();
            if (raws.Length == 0) return;
            ProjectChimera.Dsl.DslValueType[] types = b.ArgTypes ?? Array.Empty<ProjectChimera.Dsl.DslValueType>();
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            for (int i = 0; i < raws.Length; i++)
                writer.WriteNumberValue(raws[i]); // raw int only — no float, ever
            writer.WriteEndArray();
            writer.WritePropertyName("arg_types");
            writer.WriteStartArray();
            for (int i = 0; i < raws.Length; i++)
            {
                ProjectChimera.Dsl.DslValueType t = i < types.Length ? types[i] : ProjectChimera.Dsl.DslValueType.Int;
                writer.WriteStringValue(t.ToString()); // stable enum NAME
            }
            writer.WriteEndArray();
        }

        private static LocalUiAction ReadLocalAction(JsonElement el, string path)
        {
            if (!el.TryGetProperty("local_action", out JsonElement laEl) || laEl.ValueKind == JsonValueKind.Null)
                return LocalUiAction.None;
            if (laEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.local_action: must be a string, got {laEl.ValueKind}.");
            string la = laEl.GetString()!;
            if (!Enum.TryParse(la, ignoreCase: false, out LocalUiAction parsed) || !Enum.IsDefined(parsed))
                throw new JsonException(
                    $"{path}.local_action: '{la}' is not a known local UI action (closed set — no scripting/extension escape hatch).");
            return parsed;
        }

        /// <summary>Read the fields common to every widget (id/anchor/offset/size/visible_bind) after rejecting
        /// unknown/duplicate properties against <paramref name="allowed"/> (the kind's full allow-list).</summary>
        private static T ReadCommon<T>(JsonElement el, string path, T w, string[] allowed) where T : WidgetBase
        {
            RejectUnknownProperties(el, path, allowed);
            w.Id = ReadId(el, path);
            w.Anchor = ReadAnchor(el, path);
            w.X = ReadInt(el, "x", path, 0);
            w.Y = ReadInt(el, "y", path, 0);
            w.W = ReadInt(el, "w", path, 0);
            w.H = ReadInt(el, "h", path, 0);
            w.VisibleBind = ReadOptString(el, "visible_bind", path);
            return w;
        }

        private static WidgetBase[] ReadChildren(JsonElement el, JsonSerializerOptions options, string path)
        {
            if (!el.TryGetProperty("children", out JsonElement childrenEl) || childrenEl.ValueKind == JsonValueKind.Null)
                return Array.Empty<WidgetBase>();
            if (childrenEl.ValueKind != JsonValueKind.Array)
                throw new JsonException($"{path}.children: must be a JSON array, got {childrenEl.ValueKind}.");
            int n = childrenEl.GetArrayLength();
            if (n == 0) return Array.Empty<WidgetBase>();
            var result = new WidgetBase[n];
            int i = 0;
            foreach (JsonElement childEl in childrenEl.EnumerateArray())
                result[i] = ReadWidget(childEl, options, $"{path}.children[{i++}]");
            return result;
        }

        private static void CaptureEditor(JsonElement el, WidgetBase widget, string path)
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty(EditorProperty, out JsonElement editorEl))
                return;
            int bagBytes = System.Text.Encoding.UTF8.GetByteCount(editorEl.GetRawText());
            if (bagBytes > DslBounds.MaxEditorBagBytes)
                throw new JsonException(
                    $"{path}.{EditorProperty}: annotation bag is {bagBytes} bytes, over the " +
                    $"DslBounds.MaxEditorBagBytes={DslBounds.MaxEditorBagBytes} cap.");
            widget.Editor = editorEl.Clone(); // survives the transient JsonDocument
        }

        // ── Field readers (each wraps a located field path) ────────────────────────

        private static WidgetKind ReadKind(JsonElement el, string path)
        {
            if (!el.TryGetProperty("kind", out JsonElement kindEl))
                throw new JsonException($"{path}: widget is missing its required string 'kind' discriminator.");
            if (kindEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.kind: must be a string, got {kindEl.ValueKind}.");
            string kind = kindEl.GetString()!;
            if (!Enum.TryParse(kind, ignoreCase: false, out WidgetKind parsed) || !Enum.IsDefined(parsed))
                throw new JsonException(
                    $"{path}.kind: '{kind}' is not a known widget kind (widgets are closed — no extension/escape hatch).");
            return parsed;
        }

        private static AnchorPoint ReadAnchor(JsonElement el, string path)
        {
            if (!el.TryGetProperty("anchor", out JsonElement anchorEl)) return AnchorPoint.TopLeft;
            if (anchorEl.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.anchor: must be a string, got {anchorEl.ValueKind}.");
            string anchor = anchorEl.GetString()!;
            if (!Enum.TryParse(anchor, ignoreCase: false, out AnchorPoint parsed) || !Enum.IsDefined(parsed))
                throw new JsonException(
                    $"{path}.anchor: '{anchor}' is not a known 9-point anchor (TopLeft..BottomRight).");
            return parsed;
        }

        private static int ReadId(JsonElement el, string path)
        {
            if (!el.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int v))
                throw new JsonException($"{path}: widget is missing its required 32-bit integer 'id'.");
            return v;
        }

        private static int ReadInt(JsonElement parent, string prop, string path, int fallback)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el)) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
                throw new JsonException($"{path}.{prop}: must be a 32-bit integer.");
            return v;
        }

        private static string? ReadOptString(JsonElement parent, string prop, string path)
        {
            if (!parent.TryGetProperty(prop, out JsonElement el) || el.ValueKind == JsonValueKind.Null) return null;
            if (el.ValueKind != JsonValueKind.String)
                throw new JsonException($"{path}.{prop}: must be a string, got {el.ValueKind}.");
            return el.GetString();
        }

        // ── Field writers ──────────────────────────────────────────────────────────

        private static void WriteOptString(Utf8JsonWriter writer, string name, string? value)
        {
            if (value is null) return; // Read treats missing == null; omitting is the clean mirror
            writer.WriteString(name, value);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string[] Extend(string[] baseFields, params string[] extra)
        {
            var result = new string[baseFields.Length + extra.Length];
            Array.Copy(baseFields, result, baseFields.Length);
            Array.Copy(extra, 0, result, baseFields.Length, extra.Length);
            return result;
        }

        /// <summary>
        /// Any property whose name is not in <paramref name="allowed"/> is a located reject; a DUPLICATE key is a
        /// located reject too (JsonDocument permits duplicate names and TryGetProperty silently takes the FIRST).
        /// The verbatim <c>_editor</c> bag is implicitly allow-listed on EVERY kind (still at most once).
        /// </summary>
        private static void RejectUnknownProperties(JsonElement el, string path, string[] allowed)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, EditorProperty, StringComparison.Ordinal))
                {
                    if (!seen.Add(EditorProperty))
                        throw new JsonException($"{path}.{p.Name}: duplicate property (each field may appear at most once).");
                    continue;
                }
                bool isAllowed = false;
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(p.Name, allowed[i], StringComparison.Ordinal)) { isAllowed = true; break; }
                if (!isAllowed)
                    throw new JsonException(
                        $"{path}.{p.Name}: unknown property (widgets are closed — no scripting/extension escape hatch).");
                if (!seen.Add(p.Name))
                    throw new JsonException($"{path}.{p.Name}: duplicate property (each field may appear at most once).");
            }
        }
    }
}
