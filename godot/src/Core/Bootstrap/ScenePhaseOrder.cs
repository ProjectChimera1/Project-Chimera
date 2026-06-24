#nullable enable

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// The single source of truth for the composition-root phase order (Story 1.8c / AR-3, constraint C1).
    /// <see cref="ScenePhaseRunner"/> asserts the live <see cref="ISetupPhase"/> literal matches this at startup,
    /// and the Tier-1 <c>PhaseOrderTest</c> pins this array against an independently-hardcoded expected sequence —
    /// so a reorder, addition, or removal fails loudly at startup AND in CI, never silently. Changing the order
    /// is therefore a deliberate, test-guarded edit (this array + the test), exactly as intended by C1
    /// ("never silently reorder <c>_Ready()</c>"). This is the presentation-side analog of the canonical
    /// 9-system tick order that <c>SimulationHost</c> owns and <c>SystemOrderTest</c> pins.
    /// </summary>
    public static class ScenePhaseOrder
    {
        /// <summary>
        /// The exact ordered phase names as they run in <c>MainScene._Ready</c> after the Godot-free sim-spine
        /// construction block. "ScenarioLoad" wraps <c>LoadAndApplyScenario</c>; "FlowFieldInit" wraps the inline
        /// flow-field initialization that must run after all scenario buildings are placed.
        /// </summary>
        public static readonly string[] Canonical =
        {
            "Settings", "Audio", "GameState", "Lighting", "Terrain", "Navigation", "Camera",
            "Rendering", "Hud", "Minimap", "TerrainBrush", "ScenarioLoad", "FactionVisuals",
            "FlowFieldInit", "WinConditionUi", "GameOverOverlay", "Multiplayer", "ReplayStatus",
            "ContentBrowser", "MainMenu", "TriggerEditor", "MapGenerator",
        };
    }
}
