#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Effects; // EffectNodeJsonConverter (the consumable effect-graph)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Godot-free serializer for one <see cref="ItemDefinition"/> to its own indented JSON (Story 3.16). The item editor
    /// writes each item to <c>resources/data/items/&lt;id&gt;.json</c> via this, then round-trips the file through
    /// <see cref="ItemLoader.LoadFromFile"/> as a fail-closed reload self-check before committing (refuse to report
    /// "Saved" if it will not reload). Mirrors <c>ContentJson.Options</c>'s converter set (so a written item parses back
    /// through the same gate) but adds <c>WriteIndented</c> + omit-defaults for lean, human-editable files.
    /// </summary>
    public static class ItemWriter
    {
        /// <summary>The write options — the same value/enum/effect converters the read path (<c>ContentJson.Options</c>)
        /// uses, plus indentation and default-omission. <c>UnmappedMemberHandling</c> is a READ setting (irrelevant here).</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented          = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, // omit zero deltas/costs/charges + null effect
            Converters =
            {
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
                new FixedJsonConverter(),
                new EffectNodeJsonConverter(),
            },
        };

        /// <summary>Serialize <paramref name="def"/> to indented JSON. Never throws for a well-formed POCO.</summary>
        public static string Serialize(ItemDefinition def) => JsonSerializer.Serialize(def, Options);
    }
}
