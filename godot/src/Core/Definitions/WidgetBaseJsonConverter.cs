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
