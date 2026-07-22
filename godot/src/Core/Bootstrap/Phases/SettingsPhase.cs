#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
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
            // Story 8.1: construct the secret store FIRST (this phase runs first) so every later phase can source
            // API keys from it. The Godot layer does the single GlobalizePath("user://secrets") call; the store
            // itself is Godot-free. Migrate any legacy plaintext key on first run — both legacy values are "" in this
            // repo (the [Export] AnthropicApiKey/ModIoApiKey fields were removed), so these are forward-compatible
            // seams, no-ops today. Both keys get a migration call so the seam is symmetric with SecretMigration's
            // contract (which names both fields), not just the LLM key.
            var secretStore = new FileSecretStore(ProjectSettings.GlobalizePath("user://secrets"));
            SecretMigration.MigrateLegacyKey(secretStore, SecretIds.Llm, "");
            SecretMigration.MigrateLegacyKey(secretStore, SecretIds.ModIo, "");
            _ctx.SecretStore = secretStore;

            // Story 8.2: one shared HttpClient (short Test-connection timeout + a UA) feeds the Godot-free
            // availability evaluator. Test-connection / the four-state UI run through this; nothing here touches the
            // deterministic sim. Timeout is short so a Test-connection against an unreachable host fails fast.
            // AllowAutoRedirect=false: the host allowlist is enforced against the INITIAL base URL only, so a 302 from
            // an allowlisted host would otherwise silently follow to an arbitrary host — and .NET does NOT strip the
            // custom x-api-key header on a cross-host redirect (it only strips Authorization), leaking the Anthropic
            // key. Refusing redirects keeps the allowlist guarantee intact.
            var aiHttp = new System.Net.Http.HttpClient(
                new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = System.TimeSpan.FromSeconds(15) };
            aiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectChimera/1.0");
            _ctx.AiEvaluator = new AI.Providers.AiAvailabilityEvaluator(aiHttp);

            // SettingsManager: loads user://settings.json on _Ready, fires OnSettingsChanged.
            var settingsMgr = new UI.SettingsManager();
            _ctx.Scene.AddChild(settingsMgr);
            _ctx.SettingsMgr = settingsMgr;

            // SettingsPanel: layer 15, toggled via Escape key.
            var settingsPanel = new UI.SettingsPanel();
            _ctx.Scene.AddChild(settingsPanel);
            // Story 8.2: hand the panel the evaluator + secret store so its AI Provider section can run
            // Test-connection and read/write the API key exclusively through ISecretStore.
            settingsPanel.Initialize(settingsMgr, _ctx.AiEvaluator, secretStore);
            _ctx.SettingsPanel = settingsPanel;

            // Apply settings to systems already initialised (camera applied later in Camera; audio buses in
            // SettingsManager._Ready already). The handler stays on MainScene — it touches the camera/minimap it keeps.
            settingsMgr.OnSettingsChanged += _ctx.Scene.ApplySettingsToSystems;
        }
    }
}
