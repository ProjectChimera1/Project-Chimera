#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// The ONE place the look of the rendered world is defined: environment, key light, fill light.
    ///
    /// <para><b>Why a factory rather than per-scene setup.</b> The match, the editor, the asset preview and
    /// the unit card each built their own <see cref="WorldEnvironment"/> with their own values. That means
    /// the editor was lying to creators — an asset judged in the unit card rendered under different light
    /// than the same asset in a match, so "it looked right when I made it" and "it looks wrong in game"
    /// were both true. Every scene that renders the world calls <see cref="Apply"/>.</para>
    ///
    /// <para><b>The shading is ADDITIVE ONLY.</b> No ramp, no quantisation. The roster is flat-shaded with
    /// no albedo, so the continuous N·L gradient across roughly 6000 facets is the only channel carrying
    /// form; a 2-3 band cel ramp quantises exactly that away and was measured to collapse the model to a
    /// near-uniform silhouette across three separate variants.</para>
    ///
    /// <para><b>Every additive term here was A/B'd in-engine and kept or cut on the number.</b> Only the
    /// Fresnel rim in <see cref="TeamTintMaterial"/> earned its place on the untextured roster
    /// (silhouette separation 14.6 → 20.8). SSAO measured neutral and is kept for what slices 3 and 4
    /// bring; the opposing fill measured actively harmful and is turned down to a tint. See
    /// <see cref="FillEnergy"/> — it is the ramp's failure mode wearing a different hat, and it was not
    /// caught by inspection. Re-measure all three once the roster carries real albedo.</para>
    /// </summary>
    public static class WorldPresentation
    {
        // Key light. Matches the angle and energy the match has always used, so the primary read of every
        // scene is unchanged and only the additive terms are new.
        public const float KeyPitchDeg = -50f;
        public const float KeyYawDeg = 30f;
        public const float KeyEnergy = 1.2f;

        // Fill light: opposes the key, carries NO shadow and NO specular, and is deliberately cool.
        //
        // DELIBERATELY DIM, on measurement. The plan called for this to "lift the shadow side", and on the
        // CURRENT untextured roster that is actively wrong: with albedo a single flat team colour, the
        // N·L gradient is the ONLY channel carrying form, so a fill light fills in precisely the channel
        // that carries it. Measured in-engine at gameplay zoom, fill alone dropped silhouette separation
        // from 14.6 to 13.2 and visibly washed the model out.
        //
        // This is the SAME failure mode the epic already identified for a quantised cel ramp, which was
        // rejected for exactly this reason — the ramp was caught and the fill light was not. It stays in
        // the rig because it becomes correct once assets carry real albedo (form then lives in the texture,
        // not the gradient, and the shade side genuinely needs lifting), but until then it is turned down
        // to a tint rather than a lift. Re-measure and raise it after the textured roster lands.
        //
        // It must also be a LIGHT rather than more ambient: under SDFGI, raising ambient_light_energy does
        // nothing at all, so "lift the shadows with ambient" silently no-ops the moment GI is switched on.
        public const float FillEnergy = 0.12f;

        /// <summary>Cool shade tint. Warm key against cool fill is what separates planes on an untextured mesh.</summary>
        public static readonly Color FillColor = new(0.55f, 0.63f, 0.85f);

        /// <summary>
        /// Build the shared <see cref="Godot.Environment"/>.
        /// </summary>
        /// <param name="background">
        /// Optional flat backdrop colour. Panel previews sit inside the UI and need to read against a theme
        /// surface rather than the match's sky; that is the ONLY difference any caller is allowed, because
        /// everything that affects how the ART reads has to stay identical across scenes.
        /// </param>
        public static Godot.Environment BuildEnvironment(Color? background = null)
        {
            var env = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor  = new Color(0.30f, 0.30f, 0.35f),
                AmbientLightEnergy = 0.5f,

                // FILMIC, deliberately not ACES. ACES desaturates saturated colour hard, and team colour is
                // not decoration here — it is how a player tells their units from an enemy's at a glance.
                // Washing it out costs gameplay information, so the tonemapper is chosen for that and not
                // for cinematic neutrality.
                TonemapMode     = Godot.Environment.ToneMapper.Filmic,
                TonemapExposure = 1.0f,
                TonemapWhite    = 6.0f,

                // Contact shading. Measured NEUTRAL on the current roster (silhouette separation 14.6 ->
                // 14.7, form range 68.8 -> 69.2) — these meshes have few concave folds at gameplay zoom and
                // sit on flat ground, so there is little for it to occlude. It is kept on rather than tuned
                // away because it costs nothing at this scale and it is the term that pays off once slice 3
                // lands real albedo and slice 4 gives the ground actual relief to catch contact shadow.
                SsaoEnabled     = true,
                SsaoRadius      = 0.9f,
                SsaoIntensity   = 1.6f,
                SsaoPower       = 1.4f,
                SsaoDetail      = 0.4f,
                SsaoLightAffect = 0.15f,
            };
            if (background is { } bg)
            {
                env.BackgroundMode  = Godot.Environment.BGMode.Color;
                env.BackgroundColor = bg;
            }
            return env;
        }

        /// <summary>The primary shadow-casting light.</summary>
        public static DirectionalLight3D BuildKeyLight(bool shadows)
        {
            var light = new DirectionalLight3D
            {
                Rotation      = new Vector3(Mathf.DegToRad(KeyPitchDeg), Mathf.DegToRad(KeyYawDeg), 0),
                LightEnergy   = KeyEnergy,
                ShadowEnabled = shadows,
            };
            return light;
        }

        /// <summary>
        /// The opposing fill. Shadowless and specular-free by construction: a fill that cast shadows would
        /// double every silhouette, and one with specular would put a second highlight on a surface that is
        /// meant to read as being in shade.
        /// </summary>
        public static DirectionalLight3D BuildFillLight()
        {
            var fill = new DirectionalLight3D
            {
                Rotation      = new Vector3(Mathf.DegToRad(-25f), Mathf.DegToRad(KeyYawDeg + 180f), 0),
                LightEnergy   = FillEnergy,
                LightColor    = FillColor,
                LightSpecular = 0f,
                ShadowEnabled = false,
            };
            return fill;
        }

        /// <summary>
        /// Add the environment, key light and fill light under <paramref name="parent"/>.
        /// </summary>
        /// <param name="parent">The node the lighting rig is parented to.</param>
        /// <param name="shadows">Whether the key light casts shadows (driven by the quality tier).</param>
        /// <returns>The key light, so callers can retoggle its shadows when the quality tier changes.</returns>
        public static DirectionalLight3D Apply(Node parent, bool shadows = true, Color? background = null)
        {
            var key = BuildKeyLight(shadows);
            parent.AddChild(key);
            parent.AddChild(BuildFillLight());
            parent.AddChild(new WorldEnvironment { Environment = BuildEnvironment(background) });
            return key;
        }
    }
}
