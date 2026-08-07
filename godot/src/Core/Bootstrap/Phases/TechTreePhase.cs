#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 4.6 "TechTree" phase — mirrors <see cref="BuildingCardPhase"/>. Creates the Visual Tech Tree Editor panel
    /// and wires it to the current scenario's faction (the SAME <c>ctx.FactionDef</c> instance <see cref="BuildingCardPhase"/>
    /// binds) + game state + the faction file path, plus the ALREADY-published <see cref="SceneContext.BuildingCardPanel"/>
    /// so node selection opens the shared inspector. MUST run after <see cref="BuildingCardPhase"/> in
    /// <c>MainScene.cs</c>'s phase list so <c>_ctx.BuildingCardPanel</c> already exists.
    ///
    /// <para>Toggle with R in Edit mode.</para>
    /// </summary>
    public sealed class TechTreePhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public TechTreePhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "TechTree";

        public void Run()
        {
            _ctx.TechTreePanel = new TechTreePanel();
            _ctx.Scene.AddChild(_ctx.TechTreePanel);

            // Mirrors BuildingCardPhase's path resolution: the scenario's slot-0 faction file when loaded, else the
            // default P1 alpha faction.
            string factionPath =
                _ctx.Scenario?.PlayerSlots is { Length: > 0 } slots && !string.IsNullOrEmpty(slots[0].FactionJson)
                    ? slots[0].FactionJson
                    : MainScene.P1_FACTION_JSON;

            _ctx.TechTreePanel.Initialize(_ctx.FactionDef, _ctx.GameState, factionPath, _ctx.BuildingCardPanel);

            GD.Print($"[TechTree] Initialized — press {Definitions.EditorHotkeys.ChordFor(Definitions.EditorPanelId.TechTree)} in Edit mode to open (drag out-port to wire prerequisites).");
        }
    }
}
