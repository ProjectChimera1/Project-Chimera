#nullable enable
using Godot;
using ProjectChimera.Core.Bootstrap;

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-462 — the presentation-side bridge for the Godot-free <see cref="TeamColorPalette"/> (the
    /// <c>FactionPaletteGodot</c> pattern). Converts a palette <see cref="TeamColorPalette.Rgb"/> (raw 0–1 float
    /// channels) to a <see cref="Color"/>. Kept in its own <c>using Godot;</c> file so the palette table itself stays
    /// Godot-free + Tier-1-testable (this file is NOT compiled into the sim test assembly).
    /// </summary>
    public static class TeamColorPaletteGodot
    {
        /// <summary>Convert a palette color to a Godot <see cref="Color"/>. Channels pass through verbatim (alpha =
        /// 1), so the rendered color is bit-identical to the pre-DW-462 literals that lived in
        /// <c>FactionVisualsPhase</c>.</summary>
        public static Color ToColor(this TeamColorPalette.Rgb c) => new(c.R, c.G, c.B);
    }
}
