#nullable enable
using ProjectChimera.UI;   // TeamTintPolicy, TeamTintMode
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Asset-texture pipeline — the Godot-free half of team colouring.
    ///
    /// <para>The rule these pin: unit and building rendering used to apply team colour with
    /// <c>material_override</c>, which REPLACES a mesh's own surface materials. That is invisible on
    /// today's flat-grey art and catastrophic on textured art — the baked texture would never render.
    /// The fix must therefore satisfy two things at once, and only one of them is observable in-engine
    /// today: textured art keeps its texture, AND untextured art does not move at all.</para>
    /// </summary>
    public class TeamTintPolicyTests
    {
        private static readonly (float R, float G, float B) Team = (0.20f, 0.50f, 1.00f); // slate-blue
        private static readonly (float R, float G, float B) Art  = (0.80f, 0.40f, 0.20f); // a warm texel

        // ── Resolve: the no-regression guarantee ────────────────────────────────

        [Theory]
        [InlineData(TeamTintMode.Flat)]
        [InlineData(TeamTintMode.Modulate)]
        [InlineData(TeamTintMode.Accent)]
        public void WithoutAlbedoTexture_EveryModeCollapsesToFlat(TeamTintMode requested)
        {
            // Every GLB shipped so far is untextured. Turning a textured mode on ahead of textured art
            // must be a no-op, or enabling it early silently changes how the whole game looks.
            Assert.Equal(TeamTintMode.Flat, TeamTintPolicy.Resolve(requested, hasAlbedoTexture: false));
        }

        [Theory]
        [InlineData(TeamTintMode.Flat)]
        [InlineData(TeamTintMode.Modulate)]
        [InlineData(TeamTintMode.Accent)]
        public void WithAlbedoTexture_RequestedModeIsHonored(TeamTintMode requested)
        {
            Assert.Equal(requested, TeamTintPolicy.Resolve(requested, hasAlbedoTexture: true));
        }

        [Fact]
        public void FlatMode_ReturnsTheTeamColorExactly_MatchingTheOldMaterialOverride()
        {
            // The old path set StandardMaterial3D.AlbedoColor = teamColor and nothing else, so the
            // resolved albedo was the team colour regardless of what the mesh carried.
            var outColor = TeamTintPolicy.Blend(TeamTintMode.Flat, Art, mask: 0.5f, team: Team);
            Assert.Equal(Team, outColor);
        }

        [Fact]
        public void FlatMode_IgnoresArtMaskAndStrength()
        {
            // Nothing about the art may leak into the flat path — that is what makes it a safe default.
            var a = TeamTintPolicy.Blend(TeamTintMode.Flat, Art,          mask: 0f, team: Team, strength: 0f);
            var b = TeamTintPolicy.Blend(TeamTintMode.Flat, (0f, 0f, 0f), mask: 1f, team: Team, strength: 1f);
            Assert.Equal(Team, a);
            Assert.Equal(Team, b);
        }

        // ── Modulate ───────────────────────────────────────────────────────────

        [Fact]
        public void ModulateMode_MultipliesArtByTeamColor()
        {
            var outColor = TeamTintPolicy.Blend(TeamTintMode.Modulate, Art, mask: 0f, team: Team);
            Assert.Equal(Art.R * Team.R, outColor.R, 5);
            Assert.Equal(Art.G * Team.G, outColor.G, 5);
            Assert.Equal(Art.B * Team.B, outColor.B, 5);
        }

        [Fact]
        public void ModulateMode_PreservesTextureDetail()
        {
            // The whole point: two different texels must stay different after tinting. Under the old
            // material_override they both became the flat team colour and all detail was lost.
            var dark  = TeamTintPolicy.Blend(TeamTintMode.Modulate, (0.10f, 0.10f, 0.10f), 0f, Team);
            var light = TeamTintPolicy.Blend(TeamTintMode.Modulate, (0.90f, 0.90f, 0.90f), 0f, Team);
            Assert.NotEqual(dark, light);
            Assert.True(light.R > dark.R && light.G > dark.G && light.B > dark.B);
        }

        // ── Accent ─────────────────────────────────────────────────────────────

        [Fact]
        public void AccentMode_UsesTheMaskToChooseBetweenArtAndTeamColor()
        {
            var unmasked = TeamTintPolicy.Blend(TeamTintMode.Accent, Art, mask: 0f, team: Team);
            var masked   = TeamTintPolicy.Blend(TeamTintMode.Accent, Art, mask: 1f, team: Team);

            // mask 0 → pure art (exact: lerp(a, b, 0) == a identically)
            Assert.Equal(Art, unmasked);

            // mask 1 → pure team colour. Compared per-channel to a tolerance, NOT as an exact tuple:
            // lerp is `a + (b - a) * t`, so at t == 1 the round-trip through the delta loses a bit
            // (0.8f + (0.2f - 0.8f) == 0.19999998f). That is float arithmetic, not a policy defect.
            Assert.Equal(Team.R, masked.R, 5);
            Assert.Equal(Team.G, masked.G, 5);
            Assert.Equal(Team.B, masked.B, 5);
        }

        [Fact]
        public void AccentMode_HalfMaskIsTheMidpoint()
        {
            var mid = TeamTintPolicy.Blend(TeamTintMode.Accent, Art, mask: 0.5f, team: Team);
            Assert.Equal((Art.R + Team.R) / 2f, mid.R, 5);
            Assert.Equal((Art.G + Team.G) / 2f, mid.G, 5);
            Assert.Equal((Art.B + Team.B) / 2f, mid.B, 5);
        }

        // ── Strength: the A/B dial ─────────────────────────────────────────────

        [Theory]
        [InlineData(TeamTintMode.Modulate)]
        [InlineData(TeamTintMode.Accent)]
        public void ZeroStrength_ReturnsTheUntintedArt(TeamTintMode mode)
        {
            var outColor = TeamTintPolicy.Blend(mode, Art, mask: 1f, team: Team, strength: 0f);
            Assert.Equal(Art.R, outColor.R, 5);
            Assert.Equal(Art.G, outColor.G, 5);
            Assert.Equal(Art.B, outColor.B, 5);
        }

        [Fact]
        public void HalfStrength_SitsBetweenArtAndFullTint()
        {
            var full = TeamTintPolicy.Blend(TeamTintMode.Modulate, Art, 0f, Team, strength: 1f);
            var half = TeamTintPolicy.Blend(TeamTintMode.Modulate, Art, 0f, Team, strength: 0.5f);
            Assert.Equal((Art.R + full.R) / 2f, half.R, 5);
            Assert.Equal((Art.G + full.G) / 2f, half.G, 5);
            Assert.Equal((Art.B + full.B) / 2f, half.B, 5);
        }

        // ── Team identity must survive, or the RTS is unreadable ───────────────

        [Fact]
        public void ModulateMode_KeepsTwoTeamsDistinguishableOnIdenticalArt()
        {
            // Both factions share a unit silhouette; colour is what tells them apart at RTS zoom. If the
            // tint stopped separating them the mode would be unusable regardless of how good it looks.
            var blue = TeamTintPolicy.Blend(TeamTintMode.Modulate, Art, 0f, (0.20f, 0.50f, 1.00f));
            var red  = TeamTintPolicy.Blend(TeamTintMode.Modulate, Art, 0f, (0.80f, 0.25f, 0.10f));
            Assert.NotEqual(blue, red);
            Assert.True(red.R > blue.R,  "oxblood team should read warmer than slate-blue");
            Assert.True(blue.B > red.B,  "slate-blue team should read cooler than oxblood");
        }
    }
}
