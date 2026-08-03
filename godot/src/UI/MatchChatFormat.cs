#nullable enable
using ProjectChimera.Core; // Faction

namespace ProjectChimera.UI
{
    /// <summary>
    /// The Godot-free line-formatting seam behind <c>MatchChatOverlay</c>: BBCode sanitization plus the
    /// speaker name / color lookup, so the mapping is Tier-1 testable while the overlay keeps only the
    /// RichTextLabel plumbing.
    ///
    /// <para><b>DW-385.</b> The overlay used to carry its OWN 5-entry color list and a Player1–Player4-only name
    /// switch, so in a 5–8 faction match every speaker above Player4 rendered as the placeholder "??" in the
    /// default gray — indistinguishable from each other. Names and colors now come from
    /// <see cref="FactionPalette"/>, the canonical 8-player table (whose own doc always named this overlay as a
    /// source it was meant to consolidate).</para>
    ///
    /// <para>Color is never the only signal: the speaker's short name always precedes the message, so a
    /// colorblind reader (or a monochrome capture) still attributes the line.</para>
    /// </summary>
    public static class MatchChatFormat
    {
        /// <summary>Gray for system/announcement lines — not a faction color, so it is NOT palette-derived.</summary>
        public const string SystemColorHex = "#888888";

        /// <summary>
        /// Neutralize BBCode in author-supplied text: the bracket characters are replaced with their full-width
        /// look-alikes so a message can never open a tag (color/url/img injection) in the RichTextLabel. Length is
        /// preserved and no character is dropped, so the reader still sees what was typed.
        /// </summary>
        public static string Sanitize(string? message) =>
            (message ?? string.Empty).Replace("[", "（").Replace("]", "）");

        /// <summary>The speaker's short display name ("P1".."P8", "Neutral"). Every playable faction has a
        /// DISTINCT name — an out-of-range value falls back to the Neutral entry.</summary>
        public static string DisplayName(Faction faction) => FactionPalette.ForFaction(faction).Name;

        /// <summary>The speaker's BBCode color (<c>#rrggbb</c>) from the canonical faction palette. Every playable
        /// faction has a DISTINCT, colorblind-safe color — an out-of-range value falls back to Neutral gray.</summary>
        public static string ColorHex(Faction faction) => FactionPalette.ForFaction(faction).HexRgb;

        /// <summary>One received/echoed chat line: the colored speaker name followed by the sanitized message.</summary>
        public static string ChatLine(Faction faction, string? message) =>
            $"[color={ColorHex(faction)}]{DisplayName(faction)}:[/color] {Sanitize(message)}";

        /// <summary>One system line (e.g. "Player 2 connected") in neutral gray. System text is engine-authored,
        /// but it is sanitized too — it can interpolate a player-supplied name.</summary>
        public static string SystemLine(string? text) =>
            $"[color={SystemColorHex}]* {Sanitize(text)}[/color]";
    }
}
