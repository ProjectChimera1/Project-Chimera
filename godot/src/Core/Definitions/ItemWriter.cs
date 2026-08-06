#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Godot-free serializer for one <see cref="ItemDefinition"/> to its own indented JSON (Story 3.16). The item editor
    /// writes each item to <c>resources/data/items/&lt;id&gt;.json</c> via this, then round-trips the file through
    /// <see cref="ItemLoader.LoadFromFile"/> as a fail-closed reload self-check before committing (refuse to report
    /// "Saved" if it will not reload). DERIVES from <see cref="ContentJson.NewStrict"/> (so a written item parses back
    /// through the same gate by construction) and adds <c>WriteIndented</c> + omit-defaults for lean, human-editable files.
    /// </summary>
    public static class ItemWriter
    {
        /// <summary>
        /// The write options: the STRICT read posture (<see cref="ContentJson.Options"/>) plus indentation and
        /// default-omission.
        ///
        /// <para>DW-524 — this used to hand-declare a COPY of the strict converter list ("mirrors ContentJson.Options"
        /// is a hand-maintained duplicate by definition), so a converter added at the <see cref="ContentJson"/> choke
        /// point never reached the item writer and a written item could silently stop parsing back through its own
        /// reload self-check. It now derives from <see cref="ContentJson.NewStrict"/>, so neither the converter set nor
        /// its ORDER (first match wins — order is behavior) can drift from the read path again.</para>
        ///
        /// <para>The inherited READ-only settings (<c>UnmappedMemberHandling</c>, <c>ReadCommentHandling</c>,
        /// <c>AllowTrailingCommas</c>) are inert on this write-only object, so the emitted bytes are byte-for-byte what
        /// the hand-declared version produced.</para>
        /// </summary>
        public static readonly JsonSerializerOptions Options = BuildWriteOptions();

        /// <summary>The strict posture verbatim (enum → Fixed → effect node) plus the two write-side deltas.</summary>
        private static JsonSerializerOptions BuildWriteOptions()
        {
            JsonSerializerOptions o = ContentJson.NewStrict();
            o.WriteIndented          = true;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault; // omit zero deltas/costs/charges + null effect
            return o;
        }

        /// <summary>Serialize <paramref name="def"/> to indented JSON. Never throws for a well-formed POCO.</summary>
        public static string Serialize(ItemDefinition def) => JsonSerializer.Serialize(def, Options);
    }
}
