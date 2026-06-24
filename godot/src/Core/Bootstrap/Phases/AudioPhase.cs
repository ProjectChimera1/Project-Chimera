#nullable enable
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Audio" phase (runtime position 2). Creates the <see cref="UI.AudioManager"/> and initializes
    /// it against the sim-spine combat-event queue (already on the context). Runs after Settings so the SFX bus
    /// volume Settings applied is in effect. Behavior-identical to the former MainScene.SetupAudio.
    /// </summary>
    public sealed class AudioPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public AudioPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Audio";

        public void Run()
        {
            var audioMgr = new UI.AudioManager();
            _ctx.Scene.AddChild(audioMgr);
            // Initialize is deferred to after sim objects are constructed — CombatEvents already exists on the context.
            audioMgr.Initialize(_ctx.CombatEvents);
            _ctx.AudioMgr = audioMgr;
        }
    }
}
