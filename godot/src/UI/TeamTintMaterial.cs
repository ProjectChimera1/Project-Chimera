#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Builds the material that carries a player's identity colour onto a rendered mesh.
    ///
    /// <para><b>Why this exists.</b> Both bridges used to assign a flat team-coloured
    /// <c>StandardMaterial3D</c> to <c>MultiMeshInstance3D.MaterialOverride</c>. In Godot,
    /// <c>material_override</c> REPLACES a mesh's own surface materials — so the moment an asset ships
    /// with baked texture art, that art would never render. This factory keeps the flat path
    /// byte-identical for untextured art while giving textured art a shader that preserves it.</para>
    ///
    /// <para>The mode decision and blend math live in the Godot-free <see cref="TeamTintPolicy"/> so
    /// they are Tier-1 testable; this type only does the Godot-side construction.</para>
    /// </summary>
    public static class TeamTintMaterial
    {
        /// <summary>
        /// The tint mode requested project-wide. Defaults to <see cref="TeamTintMode.Modulate"/>, which is
        /// safe to leave on before any textured art exists: <see cref="TeamTintPolicy.Resolve"/> collapses
        /// it to <see cref="TeamTintMode.Flat"/> for a mesh with no albedo texture. This is the A/B switch
        /// for judging textured art in-engine.
        /// </summary>
        public static TeamTintMode Mode { get; set; } = TeamTintMode.Modulate;

        /// <summary>0 = show the art untinted, 1 = full team tint. The second A/B dial.</summary>
        public static float Strength { get; set; } = 1f;

        /// <summary>
        /// Build the team material for <paramref name="mesh"/>.
        /// </summary>
        /// <param name="mesh">The mesh about to be rendered; its surface-0 material supplies the art.</param>
        /// <param name="teamColor">The player's identity colour.</param>
        /// <param name="roughness">Preserved per call site — units shipped 0.6, buildings 0.7.</param>
        /// <param name="applied">The mode actually used after the no-texture collapse.</param>
        public static Material Build(Mesh? mesh, Color teamColor, float roughness, out TeamTintMode applied)
        {
            var (albedo, normal) = ArtOf(mesh);
            applied = TeamTintPolicy.Resolve(Mode, albedo != null);

            // The `albedo == null` arm is redundant with Resolve — it is spelled out so the compiler can
            // see the shader path always has a texture, rather than being told to trust us with `!`.
            if (applied == TeamTintMode.Flat || albedo == null)
            {
                // Identical to the pre-existing team material — the untextured path must not move.
                return new StandardMaterial3D
                {
                    AlbedoColor = teamColor,
                    Roughness   = roughness,
                    Metallic    = 0.0f,
                };
            }

            var mat = new ShaderMaterial { Shader = BuildTintShader() };
            mat.SetShaderParameter("albedo_tex", albedo);
            mat.SetShaderParameter("team_color", teamColor);
            mat.SetShaderParameter("tint_strength", Strength);
            mat.SetShaderParameter("use_mask", applied == TeamTintMode.Accent);
            mat.SetShaderParameter("surface_roughness", roughness);
            mat.SetShaderParameter("use_normal", normal != null);
            if (normal != null) mat.SetShaderParameter("normal_tex", normal);
            return mat;
        }

        /// <summary>
        /// Pull the base-colour and normal art off a mesh's first surface. Returns nulls for the box
        /// placeholder, for a mesh with no material, and for every GLB shipped so far — all of which then
        /// take the flat path.
        /// </summary>
        private static (Texture2D? Albedo, Texture2D? Normal) ArtOf(Mesh? mesh)
        {
            if (mesh == null || mesh.GetSurfaceCount() == 0) return (null, null);
            if (mesh.SurfaceGetMaterial(0) is BaseMaterial3D bm)
                return (bm.AlbedoTexture, bm.NormalEnabled ? bm.NormalTexture : null);
            return (null, null);
        }

        // ── Shader ────────────────────────────────────────────────────────────
        //
        // Mirrors TeamTintPolicy.Blend exactly. Keep the two in step: the Tier-1 tests assert the C#
        // half, and a divergence here would pass those tests while rendering something else.

        private static Shader BuildTintShader()
        {
            var shader = new Shader();
            shader.Code = @"
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap;
uniform vec4  team_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float tint_strength : hint_range(0.0, 1.0) = 1.0;
uniform float surface_roughness : hint_range(0.0, 1.0) = 0.7;
uniform bool  use_mask   = false;
uniform bool  use_normal = false;

void fragment() {
    vec4 art = texture(albedo_tex, UV);

    // ACCENT: the base-colour ALPHA channel is the team-colour mask (1 = fully team-coloured).
    // MODULATE: the team colour multiplies the art, keeping detail and pushing the hue.
    vec3 tinted = use_mask
        ? mix(art.rgb, team_color.rgb, art.a)
        : art.rgb * team_color.rgb;

    ALBEDO    = mix(art.rgb, tinted, tint_strength);
    ROUGHNESS = surface_roughness;
    METALLIC  = 0.0;

    if (use_normal) {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
    }
}
";
            return shader;
        }
    }
}
