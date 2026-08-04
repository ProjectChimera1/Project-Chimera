#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "TriggerEditor" phase (runtime position 21). Creates the LLM service + trigger-editor panel
    /// (seeded with the unit-id list from the loaded factions), binds the ScenarioDirector On* delegates via
    /// <see cref="ScenarioDelegateBinder"/> (Task 4), and builds the HUD toast label. Publishes ctx.LlmService /
    /// ctx.TriggerPanel / ctx.ToastLabel. Behavior-identical to MainScene.SetupTriggerEditor.
    /// </summary>
    public sealed class TriggerEditorPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public TriggerEditorPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "TriggerEditor";

        public void Run()
        {
            // Story 8.3: the LLMService now routes generation through the Story 8.2 ILLMProvider stack via
            // LlmProviderFactory — the selected provider is authoritative (NO Claude→Ollama fallback). It reads the
            // provider/model/base-URL from the live settings accessor and the API key ONLY through the secret store
            // (never a plaintext field); its owned HttpClient is built AllowAutoRedirect=false (a real key flows
            // through it now). Injecting null for the client lets the service own that hardened client.
            _ctx.LlmService = new LLMService(() => _ctx.SettingsMgr.Current, _ctx.SecretStore, http: null);

            _ctx.TriggerPanel = new TriggerEditorPanel();
            _ctx.Scene.AddChild(_ctx.TriggerPanel);

            // Build unit ID list from all loaded faction defs.
            var unitIds = new System.Collections.Generic.HashSet<string>();
            foreach (var def in _ctx.SlotFactionDefs)
                if (def?.Units != null)
                    foreach (var u in def.Units) unitIds.Add(u.Id);

            var context = new ScenarioContext
            {
                UnitIds   = new string[unitIds.Count],
                MapBounds = _ctx.Scenario?.MapBounds ?? 120f
            };
            unitIds.CopyTo(context.UnitIds);

            // Story 8.2: pass the availability evaluator + secret store so the panel drives its four-state AI status
            // line (Generate disabled when unavailable; manual authoring always stays usable).
            _ctx.TriggerPanel.Initialize(_ctx.Scenario, _ctx.GameState, _ctx.LlmService, context,
                _ctx.AiEvaluator, _ctx.SecretStore);

            // Task 4: the single assignment site for the ScenarioDirector On* presentation delegates.
            ScenarioDelegateBinder.Bind(_ctx);

            // Build the toast label used by OnDisplayMessage.
            _ctx.ToastLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode        = TextServer.AutowrapMode.Word,
                Visible             = false,
                ZIndex              = 10
            };
            _ctx.ToastLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _ctx.ToastLabel.OffsetTop  = 140f;
            _ctx.ToastLabel.OffsetLeft = -300f;
            _ctx.ToastLabel.OffsetRight = 300f;
            _ctx.UiCanvas.AddChild(_ctx.ToastLabel);

            // DW-368: keys are stored per provider — report the key state of the provider actually selected in
            // settings (a local provider needs none), never the legacy shared "llm" id.
            string llmProviderId = _ctx.SettingsMgr?.Current?.LlmProvider
                                   ?? Definitions.LlmProviderCatalog.DefaultProviderId;
            string llmKeyState = !AI.Providers.LlmProviderFactory.RequiresKey(llmProviderId)
                ? "not required (local provider)."
                : _ctx.SecretStore.Has(Definitions.SecretIds.ForLlmProvider(llmProviderId)) ? "configured." : "not set.";
            GD.Print("[TriggerEditor] Initialized — press L in Edit mode to open. " +
                     $"AI provider '{llmProviderId}' key " + llmKeyState);
        }
    }
}
