#nullable enable
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-134 — the ONE declaration of the <see cref="JsonSerializerOptions"/> <c>user://settings.json</c> is read and
    /// written with.
    ///
    /// <para><b>Why it exists.</b> <c>SettingsManager</c> (a Godot <c>Node</c>, hence unloadable in the Godot-free
    /// Tier-1 assembly) declared these options privately, and the two Tier-1 suites that assert
    /// <see cref="SettingsData"/> round-trips — <c>SettingsDataRoundTripTests</c> and
    /// <c>SettingsProviderConfigTests</c> — each hand-rolled a replica "matching" it. Three copies of one
    /// persistence-critical setting, with nothing enforcing that they stay equal: if the real options ever gained a
    /// naming policy or a converter, a field's persistence could regress while both "round-trip" suites stayed green,
    /// because they would be validating a DTO against a serializer shape the game does not use. Making the options a
    /// shared Godot-free static removes the drift surface outright rather than guarding it — the same move DW-274 made
    /// for the content postures in <see cref="ContentJson"/>.</para>
    ///
    /// <para><b>Why it is not in <see cref="ContentJson"/>.</b> Settings are not CONTENT: no Fixed quantization
    /// boundary, no closed-registry effect graph, no fail-closed enum posture — they are a local user preferences file
    /// whose whole contract is that an OLD file missing new fields must still load (<see cref="SettingsData.MigrateForward"/>
    /// normalizes it). <see cref="ContentJson"/>'s own doc explicitly scopes <c>SettingsData</c> out as a non-content
    /// format. This type keeps that separation while still being ONE declaration.</para>
    ///
    /// <para><b>Posture</b> (byte-identical to what <c>SettingsManager.Load</c>/<c>Save</c> used before this
    /// extraction — a pure de-duplication, no behavioural change):
    ///   • <c>WriteIndented</c> — <c>settings.json</c> is hand-editable by the player/creator when a UI toggle is
    ///     missing, so it is written readable.
    ///   • <c>ReadCommentHandling.Skip</c> + <c>AllowTrailingCommas</c> — a hand-edited file with a note or a stray
    ///     trailing comma must still load rather than silently reverting every setting to defaults.
    ///   • NO <c>Disallow</c> — an unknown key is a forward-compat no-op (a newer build's settings file must not
    ///     brick an older build); version normalization is <see cref="SettingsData.MigrateForward"/>'s job.
    /// </para>
    ///
    /// <para>One shared <c>static readonly</c> instance: a <see cref="JsonSerializerOptions"/> becomes read-only on
    /// first use, so it is safe to share and MUST NOT be mutated by any consumer.</para>
    /// </summary>
    public static class SettingsJson
    {
        /// <summary>The sole options object <c>user://settings.json</c> is read and written with. Referenced by
        /// <c>SettingsManager.Load</c>/<c>Save</c> and by every Tier-1 settings round-trip suite, so the headless
        /// tests exercise the REAL serializer shape instead of a replica that could drift from it.</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented       = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
    }
}
