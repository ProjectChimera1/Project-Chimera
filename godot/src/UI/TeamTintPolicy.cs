#nullable enable

namespace ProjectChimera.UI
{
    /// <summary>
    /// How a player's identity colour is combined with a unit's own surface art.
    /// </summary>
    public enum TeamTintMode
    {
        /// <summary>The whole surface becomes the team colour. Correct — and the only option — for
        /// art with no albedo texture, which is what every shipped GLB is today.</summary>
        Flat = 0,

        /// <summary>Team colour MULTIPLIES the art's base colour. Keeps texture detail while pushing
        /// the whole model toward the team hue. Maximum battlefield readability of the textured modes;
        /// needs no extra authoring.</summary>
        Modulate = 1,

        /// <summary>Team colour REPLACES the art only where a mask says so (trim, banners, sigils).
        /// Prettiest, and the WC3/SC2 approach — but it needs mask art that does not exist yet, so the
        /// mask currently rides in the base-colour alpha channel and an unmasked (opaque) texture
        /// degrades to showing no team colour at all.</summary>
        Accent = 2,
    }

    /// <summary>
    /// The Godot-free decision + blend math behind team colouring.
    ///
    /// <para>Extracted so the load-bearing safety rule — <b>art with no texture must render exactly as
    /// it does today</b> — is asserted in Tier-1 rather than only observable in-engine. Single-file
    /// include in <c>SimSources.props</c>, following the <see cref="FactionPalette"/> precedent; the
    /// material construction itself stays Godot-coupled in <c>TeamTintMaterial</c>.</para>
    ///
    /// <para>Background: both unit and building rendering used to apply team colour with
    /// <c>material_override</c>, which REPLACES a mesh's own surface materials. That is invisible on
    /// flat-grey art and catastrophic on textured art — the texture simply never renders. This policy
    /// is what lets the two coexist.</para>
    /// </summary>
    public static class TeamTintPolicy
    {
        /// <summary>
        /// The mode actually used. A requested textured mode collapses to <see cref="TeamTintMode.Flat"/>
        /// when the mesh carries no albedo art, so enabling a textured mode ahead of textured assets is a
        /// no-op rather than a regression.
        /// </summary>
        public static TeamTintMode Resolve(TeamTintMode requested, bool hasAlbedoTexture)
            => hasAlbedoTexture ? requested : TeamTintMode.Flat;

        /// <summary>
        /// Blend one texel, matching the fragment shader exactly.
        /// </summary>
        /// <param name="mode">The RESOLVED mode (run <see cref="Resolve"/> first).</param>
        /// <param name="art">The art's base colour at this texel, 0..1.</param>
        /// <param name="mask">Team-colour mask at this texel, 0..1 — only read in <see cref="TeamTintMode.Accent"/>.</param>
        /// <param name="team">The player's identity colour, 0..1.</param>
        /// <param name="strength">0 = untinted art, 1 = full tint. Lets the A/B dial the effect without a rebuild.</param>
        public static (float R, float G, float B) Blend(
            TeamTintMode mode,
            (float R, float G, float B) art,
            float mask,
            (float R, float G, float B) team,
            float strength = 1f)
        {
            if (mode == TeamTintMode.Flat)
                return team;

            (float R, float G, float B) tinted = mode == TeamTintMode.Accent
                ? (Lerp(art.R, team.R, mask), Lerp(art.G, team.G, mask), Lerp(art.B, team.B, mask))
                : (art.R * team.R, art.G * team.G, art.B * team.B);

            return (Lerp(art.R, tinted.R, strength),
                    Lerp(art.G, tinted.G, strength),
                    Lerp(art.B, tinted.B, strength));
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
