#nullable enable
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Settings" phase (runtime position 1 — runs first so persisted settings are loaded before
    /// anything reads them). Creates the <see cref="UI.SettingsManager"/> + <see cref="UI.SettingsPanel"/> and
    /// re-subscribes MainScene's <c>ApplySettingsToSystems</c> bridge (which pushes live values into the camera /
    /// minimap it retains). Behavior-identical to the former MainScene.SetupSettings.
    /// </summary>
    public sealed class SettingsPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public SettingsPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Settings";

        public void Run()
        {
            // SettingsManager: loads user://settings.json on _Ready, fires OnSettingsChanged.
            var settingsMgr = new UI.SettingsManager();
            _ctx.Scene.AddChild(settingsMgr);
            _ctx.SettingsMgr = settingsMgr;

            // SettingsPanel: layer 15, toggled via Escape key.
            var settingsPanel = new UI.SettingsPanel();
            _ctx.Scene.AddChild(settingsPanel);
            settingsPanel.Initialize(settingsMgr);
            _ctx.SettingsPanel = settingsPanel;

            // Apply settings to systems already initialised (camera applied later in Camera; audio buses in
            // SettingsManager._Ready already). The handler stays on MainScene — it touches the camera/minimap it keeps.
            settingsMgr.OnSettingsChanged += _ctx.Scene.ApplySettingsToSystems;
        }
    }
}
