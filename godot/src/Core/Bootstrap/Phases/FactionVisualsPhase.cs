#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "FactionVisuals" phase (runtime position 13). Creates the faction-dependent visuals — per-faction
    /// unit MultiMesh bridges (P1 blue / P2 red) and the building bridge — using the slot faction definitions the
    /// scenario assigned, then re-syncs the EntityPlacer so Edit-mode click-to-spawn matches. Runs after
    /// ScenarioLoad (slot factions final). Produces no shared handle. Behavior-identical to MainScene.SetupFactionVisuals.
    /// </summary>
    public sealed class FactionVisualsPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public FactionVisualsPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "FactionVisuals";

        public void Run()
        {
            var p1Color = new Color(0.2f, 0.5f, 1.0f); // Player 1 = blue
            var p2Color = new Color(1.0f, 0.3f, 0.2f); // Player 2 = red

            var p1Def = _ctx.SlotFactionDefs[(int)Faction.Player1] ?? _ctx.FactionDef;
            var p2Def = _ctx.SlotFactionDefs[(int)Faction.Player2] ?? _ctx.FactionDef2;

            var unitP1 = new MultiMeshBridge();
            _ctx.Scene.AddChild(unitP1);
            unitP1.Initialize(_ctx.Host, p1Def, Faction.Player1, p1Color);

            var unitP2 = new MultiMeshBridge();
            _ctx.Scene.AddChild(unitP2);
            unitP2.Initialize(_ctx.Host, p2Def, Faction.Player2, p2Color);

            var buildingBridge = new BuildingBridge();
            _ctx.Scene.AddChild(buildingBridge);
            buildingBridge.Initialize(_ctx.Buildings, p1Def, p2Def, p1Color, p2Color);

            // Keep the editor placement tool in sync with the slot factions so click-to-spawn in Edit mode
            // produces the same mesh + stats the bridges render (Camera wired it with defaults pre-scenario).
            _ctx.Placer.SetFactionDefs(p1Def, p2Def);
        }
    }
}
