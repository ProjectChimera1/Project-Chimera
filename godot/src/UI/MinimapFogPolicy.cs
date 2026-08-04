#nullable enable
using ProjectChimera.Core; // FogOfWarSystem

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-406 / DW-408 — the Godot-free minimap fog policy: the single decision seam <c>MinimapBridge</c> reads for
    /// both of its fog-dependent choices.
    ///
    /// <para><b>(a) Fog-texture alpha (DW-406).</b> Previously the minimap read <c>FogOfWarSystem.Grid</c> raw and
    /// ignored the spectator <c>FogOfWarBridge.RevealAll</c> flag, so a spectator's 3D view was fully open while its
    /// minimap stayed fogged to the default viewer. <see cref="FogAlpha"/> honors <c>revealAll</c> — every cell
    /// renders fully clear — so the two views agree.</para>
    ///
    /// <para><b>(b) Dot visibility (DW-408).</b> Previously EVERY alive entity's dot was painted, leaking all enemy
    /// positions through fog. <see cref="ShouldDrawDot"/> draws own dots always, and enemy dots only where the local
    /// fog grid is currently <see cref="FogOfWarSystem.VISIBLE"/> — the same
    /// <see cref="FogOfWarSystem.IsVisible(float, float)"/> vision the main-view fog overlay presents. An allied
    /// unit under shared team vision lights the grid itself (Story 9.14 unions allied sight), so its dot stays
    /// visible through this gate without any alliance plumbing here.</para>
    ///
    /// <para>Godot-free (Core only) so it compiles into the Tier-1 test assembly and both decisions are unit-testable
    /// directly against a real <see cref="FogOfWarSystem"/>. Purely presentation — the fog grid and everything in
    /// this class stay outside every determinism hash.</para>
    /// </summary>
    public static class MinimapFogPolicy
    {
        // Fog overlay alpha per cell state (R=G=B=0 in the texture, only alpha varies). Moved from MinimapBridge so
        // the texture mapping and its Tier-1 tests share one truth.
        public const byte FOG_UNEXPLORED = 210;
        public const byte FOG_EXPLORED   = 110;
        public const byte FOG_VISIBLE    = 0;

        /// <summary>
        /// The minimap fog-texture alpha for one grid cell. <paramref name="revealAll"/> (the spectator flag mirrored
        /// from <c>FogOfWarBridge.RevealAll</c>) forces every cell fully clear so the minimap agrees with the
        /// fully-revealed 3D overlay; otherwise VISIBLE → transparent, EXPLORED → dim, anything else (UNEXPLORED or
        /// an unknown byte) → opaque.
        /// </summary>
        public static byte FogAlpha(byte cellState, bool revealAll)
        {
            if (revealAll) return FOG_VISIBLE;
            return cellState switch
            {
                FogOfWarSystem.VISIBLE  => FOG_VISIBLE,
                FogOfWarSystem.EXPLORED => FOG_EXPLORED,
                _                       => FOG_UNEXPLORED, // UNEXPLORED or unknown
            };
        }

        /// <summary>
        /// Whether a minimap dot at world (<paramref name="worldX"/>, <paramref name="worldZ"/>) may draw.
        /// Own-faction dots always draw. Enemy dots draw when the view is fully revealed (spectator
        /// <paramref name="revealAll"/>), when no fog system is wired (a fog-free minimap keeps its ungated
        /// behavior), or when the dot's cell is currently VISIBLE on the local fog — never merely EXPLORED, so a
        /// once-scouted, now-dark cell hides enemy movement exactly like the main view.
        /// </summary>
        public static bool ShouldDrawDot(bool isOwn, bool revealAll, FogOfWarSystem? fog, float worldX, float worldZ)
        {
            if (isOwn || revealAll || fog == null) return true;
            return fog.IsVisible(worldX, worldZ);
        }
    }
}
