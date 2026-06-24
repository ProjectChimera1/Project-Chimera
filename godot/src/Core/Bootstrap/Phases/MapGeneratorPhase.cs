#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "MapGenerator" phase (runtime position 22, last). Creates the AI map-generator panel, seeded
    /// with the unit-id list + slot faction JSONs from the loaded scenario, and wires its load callback back to
    /// MainScene.LoadGeneratedScenario (which stashes the result and reloads the scene). Publishes ctx.MapGenPanel.
    /// Behavior-identical to MainScene.SetupMapGenerator.
    /// </summary>
    public sealed class MapGeneratorPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public MapGeneratorPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "MapGenerator";

        public void Run()
        {
            _ctx.MapGenPanel = new MapGeneratorPanel();
            _ctx.Scene.AddChild(_ctx.MapGenPanel);

            // Build unit ID list — same pass used by TriggerEditor.
            var unitIds = new System.Collections.Generic.HashSet<string>();
            foreach (var def in _ctx.SlotFactionDefs)
                if (def?.Units != null)
                    foreach (var u in def.Units) unitIds.Add(u.Id);

            var context = new MapGeneratorContext
            {
                UnitIds          = new string[unitIds.Count],
                MapBounds        = _ctx.Scenario?.MapBounds ?? 120f,
                Slot0FactionJson = _ctx.Scenario?.PlayerSlots?.Length > 0
                    ? _ctx.Scenario.PlayerSlots[0].FactionJson
                    : "res://resources/data/factions/alpha_faction.json",
                Slot1FactionJson = _ctx.Scenario?.PlayerSlots?.Length > 1
                    ? _ctx.Scenario.PlayerSlots[1].FactionJson
                    : "res://resources/data/factions/beta_faction.json",
            };
            unitIds.CopyTo(context.UnitIds);

            _ctx.MapGenPanel.Initialize(_ctx.GameState, _ctx.LlmService, context);
            _ctx.MapGenPanel.OnLoadRequested += _ctx.Scene.LoadGeneratedScenario;

            GD.Print("[MapGenerator] Initialized — press M in Edit mode to open.");
        }
    }
}
