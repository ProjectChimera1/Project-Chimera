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
    /// with baked texture art, that art would never render. This factory keeps the untextured path
    /// looking the same while giving textured art a shader that preserves it.</para>
    ///
    /// <para><b>Both arms return a <see cref="ShaderMaterial"/>.</b> They did not used to. The untextured
    /// arm returned a plain <c>StandardMaterial3D</c>, and since every shipped GLB is untextured, that arm
    /// ran for 24 of 24 assets — which meant every edit to the tint shader rendered on ZERO assets while
    /// building green and passing any gate that only checks "the match runs". A shading change has to
    /// reach the art that actually exists today, so both arms are now the same shader.</para>
    ///
    /// <para><b>The shading model is orthogonal to <see cref="TeamTintMode"/>.</b> Whether an asset is
    /// textured is a <i>uniform</i> (<c>has_albedo</c>), not a fourth enum member. Adding a mode would
    /// change <see cref="TeamTintPolicy.Resolve"/>'s arity, move its pinned Tier-1 tests, and put the enum
    /// into the enum-indexed-array touch-site class that crashes past a green gate.</para>
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
        /// Multiplies final albedo. Units ship at 1.0 and buildings slightly below it, so a structure reads
        /// as a darker mass than the units standing in front of it at gameplay zoom — value separation is
        /// what makes a crowded base legible, and it costs nothing.
        /// </summary>
        public const float UnitValue = 1.0f;

        /// <summary>See <see cref="UnitValue"/>.</summary>
        public const float BuildingValue = 0.88f;

        /// <summary>
        /// Set true once by the bootstrap when the untextured-fallback log has been emitted for a run, so a
        /// 28-material Initialize does not print 28 identical lines. Reset by <see cref="ResetDiagnostics"/>.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _loggedUntextured = new();

        /// <summary>Clear the once-per-asset fallback log memory. Called when a scene reloads.</summary>
        public static void ResetDiagnostics() => _loggedUntextured.Clear();

        // One compiled Shader for every material in the game. This used to be `new Shader()` per call,
        // which handed Godot 28+ byte-identical shader resources per match to compile and cache separately.
        private static Shader? _tintShader;

        private static Shader TintShader => _tintShader ??= BuildTintShader();

        /// <summary>
        /// Build the team material for <paramref name="mesh"/>.
        /// </summary>
        /// <param name="mesh">The mesh about to be rendered; its surface-0 material supplies the art.</param>
        /// <param name="teamColor">The player's identity colour.</param>
        /// <param name="roughness">Preserved per call site — units shipped 0.6, buildings 0.7.</param>
        /// <param name="applied">The mode actually used after the no-texture collapse.</param>
        /// <param name="assetName">
        /// Name used only for diagnostics. When art is missing, the fallback says WHICH asset fell back —
        /// silently rendering flat is how an unintended collapse to <see cref="TeamTintMode.Flat"/> stayed
        /// invisible in logs and in the debug seam.
        /// </param>
        /// <param name="valueScale">See <see cref="UnitValue"/>/<see cref="BuildingValue"/>.</param>
        public static Material Build(Mesh? mesh, Color teamColor, float roughness, out TeamTintMode applied,
                                     string? assetName = null, float valueScale = UnitValue)
        {
            var (albedo, normal) = ArtOf(mesh);
            applied = TeamTintPolicy.Resolve(Mode, albedo != null);

            var mat = new ShaderMaterial { Shader = TintShader };

            // has_albedo is the ONLY thing that differs structurally between the two arms. With it false
            // the shader writes team_color straight to ALBEDO, which is what the old StandardMaterial3D
            // did — so the untextured roster does not move, while still going through the shader that
            // carries the rim and the value separation.
            mat.SetShaderParameter("has_albedo", albedo != null);
            mat.SetShaderParameter("team_color", teamColor);
            mat.SetShaderParameter("tint_strength", Strength);
            mat.SetShaderParameter("use_mask", applied == TeamTintMode.Accent);
            mat.SetShaderParameter("surface_roughness", roughness);
            mat.SetShaderParameter("use_normal", normal != null);
            mat.SetShaderParameter("value_scale", valueScale);
            if (albedo != null) mat.SetShaderParameter("albedo_tex", albedo);
            if (normal != null) mat.SetShaderParameter("normal_tex", normal);

            if (albedo == null && assetName != null && _loggedUntextured.Add(assetName))
            {
                GD.Print($"[TeamTint] '{assetName}' has no surface-0 albedo texture — rendering FLAT " +
                         $"(requested {Mode}). Textured art on this asset will not show until it carries " +
                         $"a baseColorTexture on surface 0.");
            }

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
        // The tint half mirrors TeamTintPolicy.Blend exactly. Keep the two in step: the Tier-1 tests assert
        // the C# half, and a divergence here would pass those tests while rendering something else.
        //
        // The SHADING half is deliberately ADDITIVE ONLY — a rim term added to EMISSION, and nothing that
        // quantises. A 2-3 band cel ramp was tested on this roster and is a REGRESSION: the meshes are
        // flat-shaded with no albedo, so the continuous N·L gradient across ~6000 facets is the only
        // channel carrying form, and a ramp quantises exactly that away. Three variants were tried and all
        // three collapsed the model to a near-uniform silhouette. Shadow tinting is likewise NOT done here
        // with a custom light() — it comes from the environment's dim opposing fill light, which tints the
        // shade side without replacing Godot's whole lighting path.

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
uniform bool  has_albedo = false;
uniform float value_scale : hint_range(0.5, 1.5) = 1.0;

// Silhouette separation, and the ONE additive term measured to earn its place on the untextured
// roster: in-engine A/B at gameplay zoom, silhouette separation (mean luminance step across the
// subject/background boundary) went 14.6 -> 20.8, +42%, while SSAO alone moved it 14.6 -> 14.7 and
// the opposing fill light made it WORSE at 13.2. Values are the measured ones, not defaults.
uniform float rim_strength : hint_range(0.0, 1.0) = 0.25;
uniform float rim_power : hint_range(1.0, 16.0) = 5.0;
uniform vec4  rim_color : source_color = vec4(0.72, 0.80, 1.0, 1.0);

// World-space normal, carried through a varying. NORMAL and VIEW are VIEW-space inside a Godot
// fragment shader, so anything that needs to reason about world orientation (up-facing wear, sky
// occlusion, slice 5's motion work) cannot read NORMAL directly and must have this.
varying vec3 world_normal;

void vertex() {
    // The seam slice 5 needs for transform motion. Establishing it now means that work is a body to
    // fill in rather than a new function bolted onto a material that has already shipped.
    world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec3 base;

    if (has_albedo) {
        vec4 art = texture(albedo_tex, UV);

        // ACCENT: the base-colour ALPHA channel is the team-colour mask (1 = fully team-coloured).
        // MODULATE: the team colour multiplies the art, keeping detail and pushing the hue.
        vec3 tinted = use_mask
            ? mix(art.rgb, team_color.rgb, art.a)
            : art.rgb * team_color.rgb;

        base = mix(art.rgb, tinted, tint_strength);

        if (use_normal) {
            NORMAL_MAP = texture(normal_tex, UV).rgb;
        }
    } else {
        // The untextured arm. Byte-equivalent to the StandardMaterial3D this replaced.
        base = team_color.rgb;
    }

    ALBEDO    = base * value_scale;
    ROUGHNESS = surface_roughness;
    METALLIC  = 0.0;

    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), rim_power);
    EMISSION = rim_color.rgb * (fres * rim_strength);
}
";
            return shader;
        }
    }
}
