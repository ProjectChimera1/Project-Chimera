#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.7 — the presentation-side bridge for the Godot-free <see cref="FactionPalette"/>. Converts a palette
    /// <see cref="FactionPalette.Entry"/> (RGBA bytes) to a <see cref="Color"/>. Kept in its own <c>using Godot;</c>
    /// file so the palette table itself stays Godot-free + Tier-1-testable (this file is NOT compiled into the sim
    /// test assembly).
    /// </summary>
    public static class FactionPaletteGodot
    {
        /// <summary>Convert a palette entry to a Godot <see cref="Color"/> (0-255 bytes → 0-1 channels).</summary>
        public static Color ToColor(this FactionPalette.Entry e) => Color.Color8(e.R, e.G, e.B, e.A);

        /// <summary>Convenience: the Godot color for a faction (via <see cref="FactionPalette.ForFaction"/>).</summary>
        public static Color ColorFor(ProjectChimera.Core.Faction faction) => FactionPalette.ForFaction(faction).ToColor();
    }
}
