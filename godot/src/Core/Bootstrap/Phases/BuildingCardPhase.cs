#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 4.5 "BuildingCard" phase — mirrors <see cref="UnitCardPhase"/>. Creates the Building Card Editor panel
    /// and wires it to the current scenario's faction (<c>ctx.FactionDef</c>, the default alpha, populated by
    /// <c>ScenarioLoadPhase</c> well before this phase) + game state, then publishes <c>ctx.BuildingCardPanel</c>.
    /// Toggle with C in Edit mode. No ability/behavior registry — buildings don't author <c>abilities[]</c>/
    /// <c>behaviors[]</c>, so unlike <see cref="UnitCardPhase"/> this phase supplies only the faction + game state +
    /// file path.
    ///
    /// <para>Shares the SAME <c>_ctx.FactionDef</c> instance <see cref="UnitCardPhase"/> binds (both phases read the
    /// same <see cref="SceneContext.FactionDef"/> reference), so a unit edit in one panel and a building edit in the
    /// other panel stay consistent in memory without either reloading — neither panel ever reloads the faction file
    /// mid-session; the file is authoritative only across a restart.</para>
    /// </summary>
    public sealed class BuildingCardPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public BuildingCardPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "BuildingCard";

        public void Run()
        {
            _ctx.BuildingCardPanel = new BuildingCardPanel();
            _ctx.Scene.AddChild(_ctx.BuildingCardPanel);

            // Mirrors UnitCardPhase's D-8 path resolution: the scenario's slot-0 faction file when loaded, else the
            // default P1 alpha faction — an independent copy (matching ItemCardPhase's own precedent — minimal blast
            // radius), NOT a shared helper with UnitCardPhase.
            string factionPath =
                _ctx.Scenario?.PlayerSlots is { Length: > 0 } slots && !string.IsNullOrEmpty(slots[0].FactionJson)
                    ? slots[0].FactionJson
                    : MainScene.P1_FACTION_JSON;

            _ctx.BuildingCardPanel.Initialize(_ctx.FactionDef, _ctx.GameState, factionPath);

            GD.Print("[BuildingCard] Initialized — press C in Edit mode to open (edit/create/duplicate/delete).");
        }
    }
}
