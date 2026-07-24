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
            // Story 9.9 (review P1): populate the render/session AssetRegistry the bridges below read. The download-time
            // ingest (ContentBrowserPanel) populated a SceneContext registry that ReloadCurrentScene then discarded, so
            // the ingest that must actually render belongs HERE, on the load-to-play path, into the post-reload
            // registry. HandleLoadMap already integrity-verified + unpacked the package (assets included) to
            // user://imported_maps/<id>/; the reloaded scene identifies that map by its scenario path stem.
            IngestImportedAssets();

            var p1Color = new Color(0.2f, 0.5f, 1.0f); // Player 1 = blue
            var p2Color = new Color(1.0f, 0.3f, 0.2f); // Player 2 = red

            var p1Def = _ctx.SlotFactionDefs[(int)Faction.Player1] ?? _ctx.FactionDef;
            var p2Def = _ctx.SlotFactionDefs[(int)Faction.Player2] ?? _ctx.FactionDef2;

            // Story 9.9: pass the shared asset registry so a custom unit whose MeshPath is a downloaded logical id
            // (e.g. "assets/heavy_tank.glb") renders its ingested mesh; a res:// path or absent id is unaffected.
            var unitP1 = new MultiMeshBridge();
            _ctx.Scene.AddChild(unitP1);
            unitP1.Initialize(_ctx.Host, p1Def, Faction.Player1, p1Color, _ctx.AssetRegistry);

            var unitP2 = new MultiMeshBridge();
            _ctx.Scene.AddChild(unitP2);
            unitP2.Initialize(_ctx.Host, p2Def, Faction.Player2, p2Color, _ctx.AssetRegistry);

            var buildingBridge = new BuildingBridge();
            _ctx.Scene.AddChild(buildingBridge);
            // Story 9.9 (review P2): thread the registry so a custom BUILDING whose MeshPath is a downloaded logical id
            // resolves to its ingested mesh, mirroring the unit bridge.
            buildingBridge.Initialize(_ctx.Buildings, p1Def, p2Def, p1Color, p2Color, _ctx.AssetRegistry);

            // Keep the editor placement tool in sync with the slot factions so click-to-spawn in Edit mode
            // produces the same mesh + stats the bridges render (Camera wired it with defaults pre-scenario).
            _ctx.Placer.SetFactionDefs(p1Def, p2Def);
        }

        /// <summary>
        /// Story 9.9 — ingest any custom GLB assets an imported map bundled into the shared render registry. Derives the
        /// map's import dir from the scenario path stem (HandleLoadMap unpacks to user://imported_maps/&lt;id&gt;/ with
        /// an assets/ subdir); a normal res:// scenario has no such dir, so this is a no-op. The bytes were already
        /// integrity-verified by the Unpack that produced the dir; here we only decode + register per-asset (each
        /// invalid/unsafe asset falls back to the box placeholder inside <see cref="AssetRegistry.IngestPackage"/>).
        /// </summary>
        private void IngestImportedAssets()
        {
            string scenarioPath = _ctx.Scene.ScenarioPath;
            if (string.IsNullOrEmpty(scenarioPath)) return;

            string mapId = System.IO.Path.GetFileNameWithoutExtension(scenarioPath);
            if (string.IsNullOrEmpty(mapId)) return;

            string importDir = ProjectSettings.GlobalizePath($"user://imported_maps/{mapId}/");
            string assetsDir = System.IO.Path.Combine(importDir, "assets");
            if (!System.IO.Directory.Exists(assetsDir)) return;

            var logicalIds = new System.Collections.Generic.List<string>();
            foreach (var f in System.IO.Directory.EnumerateFiles(assetsDir))
                logicalIds.Add("assets/" + System.IO.Path.GetFileName(f));
            if (logicalIds.Count == 0) return;

            _ctx.AssetRegistry.IngestPackage(importDir, logicalIds);
            GD.Print($"[FactionVisuals] Ingested {_ctx.AssetRegistry.Count} custom asset(s) from {assetsDir}.");
        }
    }
}
