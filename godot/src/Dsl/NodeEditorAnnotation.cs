#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.10 — the Godot↔IR POSITION SEAM: read/write the integer canvas position (<c>x</c>,<c>y</c>) on a
    /// node's verbatim <see cref="NodeBase.Editor"/> (<c>_editor</c>) annotation bag, the channel Story 7.7
    /// pre-provisioned for "7.10 node positions". Godot-free and float-free (positions are ints — the T3 view
    /// rounds Godot's <c>Vector2</c> to ints before it ever reaches here), so the position round-trip is Tier-1
    /// unit-testable and never drags a Godot type into <c>src/Dsl/**</c>.
    ///
    /// <para><b>Merge-preserving, cap-aware.</b> <see cref="SetPosition"/> READS the existing bag, REWRITES only
    /// <c>x</c>/<c>y</c>, and PRESERVES every other pre-existing key verbatim (never clobbers an authoring note),
    /// then re-checks the raw-JSON byte size against <see cref="DslBounds.MaxEditorBagBytes"/> — the SAME cap
    /// <c>NodeBaseJsonConverter</c> enforces at parse — throwing a located <see cref="JsonException"/> if a write
    /// would exceed it (positions are tens of bytes, so unreachable in practice; the guard keeps the seam
    /// fail-closed like every other authored surface). The bag is hash-excluded BY CONSTRUCTION (the typed
    /// <c>CanonicalModelHash</c> fold never reads <c>_editor</c>), so a position write can never move the MP
    /// handshake hash.</para>
    /// </summary>
    public static class NodeEditorAnnotation
    {
        private const string XKey = "x";
        private const string YKey = "y";

        /// <summary>The persisted integer canvas position of <paramref name="node"/>, or null when its
        /// <c>_editor</c> bag carries no (integer) <c>x</c>/<c>y</c> pair (an un-positioned / freshly-added node).</summary>
        public static (int X, int Y)? GetPosition(NodeBase node)
        {
            if (node?.Editor is not JsonElement bag || bag.ValueKind != JsonValueKind.Object)
                return null;
            if (!bag.TryGetProperty(XKey, out JsonElement xe) || !bag.TryGetProperty(YKey, out JsonElement ye))
                return null;
            if (xe.ValueKind != JsonValueKind.Number || ye.ValueKind != JsonValueKind.Number
                || !xe.TryGetInt32(out int x) || !ye.TryGetInt32(out int y))
                return null;
            return (x, y);
        }

        /// <summary>
        /// Merge the integer position (<paramref name="x"/>,<paramref name="y"/>) into <paramref name="node"/>'s
        /// <c>_editor</c> bag, preserving every other pre-existing key verbatim. Throws a located
        /// <see cref="JsonException"/> if the resulting bag would exceed <see cref="DslBounds.MaxEditorBagBytes"/>.
        /// </summary>
        public static void SetPosition(NodeBase node, int x, int y)
        {
            if (node is null) throw new System.ArgumentNullException(nameof(node));

            // A non-object bag (string/array/number — legal at parse, captured verbatim) cannot be merged into:
            // overwriting it with {x,y} would clobber authored content, breaking the preserve-verbatim contract.
            // Fail closed with a located reject instead (callers surface it; the bag stays untouched).
            if (node.Editor is JsonElement existing
                && existing.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
                throw new JsonException(
                    $"node {node.Id}._editor is not a JSON object ({existing.ValueKind}); refusing to overwrite it with a position write.");

            // Read-merge-write: start from a mutable copy of any existing bag (an object), else a fresh one.
            JsonObject obj = node.Editor is JsonElement bag && bag.ValueKind == JsonValueKind.Object
                ? (JsonObject)(JsonNode.Parse(bag.GetRawText()) ?? new JsonObject())
                : new JsonObject();

            obj[XKey] = x;
            obj[YKey] = y;

            string json = obj.ToJsonString();
            int bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > DslBounds.MaxEditorBagBytes)
                throw new JsonException(
                    $"node {node.Id}._editor: position write would make the annotation bag {bytes} bytes, over the " +
                    $"DslBounds.MaxEditorBagBytes={DslBounds.MaxEditorBagBytes} cap.");

            using JsonDocument doc = JsonDocument.Parse(json);
            node.Editor = doc.RootElement.Clone();
        }
    }
}
