#nullable enable
using Godot;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Lighting" phase (runtime position 4). Adds the key DirectionalLight3D and the ambient
    /// WorldEnvironment. Produces no shared handle — behavior-identical to the former MainScene.SetupLighting.
    /// </summary>
    public sealed class LightingPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public LightingPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Lighting";

        public void Run()
        {
            var light = new DirectionalLight3D();
            light.Rotation = new Vector3(Mathf.DegToRad(-50), Mathf.DegToRad(30), 0);
            light.LightEnergy = 1.2f;
            // Story 11.7 (FR-66): seed the shadow baseline from the PERSISTED quality tier (low = off). The
            // MainScene OnSettingsChanged bridge does NOT fire against this light on a fresh match launch — the
            // boot-time SettingsManager._Ready Apply runs before the handler is subscribed and before the light
            // exists — so a persisted quality_preset:"low" would otherwise still get shadows every match until the
            // player re-opened Settings. LightingPhase runs AFTER SettingsPhase, so Current is already loaded.
            // Store the light so the runtime bridge can retoggle it when the tier changes mid-match.
            light.ShadowEnabled = _ctx.SettingsMgr?.Current is { } cur ? cur.QualityPreset != "low" : true;
            _ctx.Scene.AddChild(light);
            _ctx.KeyLight = light;

            var ambient = new WorldEnvironment();
            var env = new Godot.Environment();
            env.AmbientLightSource  = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor   = new Color(0.3f, 0.3f, 0.35f);
            env.AmbientLightEnergy  = 0.5f;
            ambient.Environment = env;
            _ctx.Scene.AddChild(ambient);
        }
    }
}
